/**
 * renderer.js — the frame pipeline.
 *
 *  1. dynamic light accumulation (ground-space light pools)
 *  2. shadow assignment  (the Update() loop from the article)
 *  3. shadow mask raster (vertex stage + fragment stage)
 *  4. ground             (albedo * (ambient + light) * shadow)
 *  5. objects            (normal-mapped, 3D-light-distributed, cached)
 *  6. additive glows     (lamps/oven/fireflies — intensity follows time of day)
 *  7. fog bands          (altitude-tested)
 *  8. LUT colour pass
 *  9. debug overlays
 *
 * Every pass is timed; the HUD shows where the frame budget goes.
 */

import { FrameBuffer, bayer } from './framebuffer.js?v=r6';
import { ShadowSystem, MAX_LIGHT_SHADOWS } from './shadows.js?v=r6';
import { LutController, ambientColorGradient, ambientIntensity, verticalFacingFactor } from './lighting.js?v=r6';
import { buildTerrain } from './terrain.js?v=r6';
import { FogSystem } from './fog.js?v=r6';
import { makeShadowSet } from './pixelart.js?v=r6';
import { drawText } from './debugfont.js?v=r6';
import { BUILD, clamp, lerp } from './util.js?v=r6';

const TAU = Math.PI * 2;

export class Renderer {
  constructor(scene, opts = {}) {
    this.scene = scene;
    this.assets = scene.assets;
    this.W = opts.width || 320;
    this.H = opts.height || 180;
    this.squashY = opts.squashY ?? 0.42;

    this.shadowSprites = makeShadowSet();
    this.shadows = new ShadowSystem(scene.w, scene.h, { squashY: this.squashY });
    this.terrain = buildTerrain(scene.w, scene.h, 1337, { path: scene.pathPoints });
    this.fog = new FogSystem(this.W, this.H, { count: 7 });
    this.lut = new LutController();

    this.out = new FrameBuffer(this.W, this.H, 'out');
    this.lightBuf = new Float32Array(this.W * this.H * 3);
    this.coverage = new Float32Array(this.W * this.H);
    this.ownCov = new Float32Array(this.W * this.H);   // caster's own slots only
    this.altBuf = new Uint8Array(this.W * this.H);

    this.caches = new Map();          // canvas -> Map(key -> {data, directShare})
    this.cacheEntries = 0;
    this._spriteLights = [];
    this._lightPool = [];
    this._scratch = { data: null, directShare: 1 };

    this.settings = {
      shadows: true,
      shadowModel: 'sprite',          // 'sprite' (article) | 'physical'
      shadowStyle: 'auto',            // 'auto' | 'blob' | 'project'
      selfShadow: false,              // let a caster darken itself (off = fixed)
      shadowDarkness: 0.86,
      normals: true,
      light3D: true,
      backFadeRate: 0.16,
      litCache: true,
      cacheBudget: 10,
      wind: true,
      windStrength: 0.6,
      fog: 0.42,
      dither: true,
      glows: true,
      lut: true,
      lutStyle: null,
      view: 'final',                  // final | shadows | lights | normals | altitude
      debug: {
        lightRadii: false, shadowQuads: false, pivots: false, links: false,
        grid: false, channels: false, sunVector: false,
      },
    };

    this.ambient = [0.2, 0.22, 0.3];
    this.sunTangent = { x: 0, y: 1, z: 0 };
    this.emissiveGlow = 0;
    this.camX = 0; this.camY = 0;
    this.stats = {
      frameMs: 0, lightMs: 0, assignMs: 0, rasterMs: 0, groundMs: 0,
      objectMs: 0, glowMs: 0, fogMs: 0, lutMs: 0, debugMs: 0,
      cacheHits: 0, cacheMisses: 0, cacheEntries: 0, lightTexels: 0,
      shadowTexels: 0, quads: 0, tests: 0, maxAngleError: 0,
    };
  }

  get cfg() { return this.shadows.cfg; }

  /* ------------------------------------------------------------------- frame */

  frame(dt) {
    const t0 = performance.now();
    const s = this.scene;
    const st = this.stats;
    st.cacheHits = 0; st.cacheMisses = 0;

    this.camX = Math.round(s.camX);
    this.camY = Math.round(s.camY);

    // ---- dynamic ambient light ----
    const ac = ambientColorGradient(s.timeOfDay);
    const ai = ambientIntensity(s.timeOfDay);
    this.ambient[0] = (ac[0] / 255) * ai;
    this.ambient[1] = (ac[1] / 255) * ai;
    this.ambient[2] = (ac[2] / 255) * ai;
    this.emissiveGlow = lerp(1.45, 0.15, clamp(s.dayAmount));
    // "The picture also shows how the intensity of the light sprites changes
    //  depending on the time of day." — pools fade while the sun is up.
    this.lightDayScale = lerp(1, 0.30, clamp(s.dayAmount * 1.15));

    // ---- sun direction in sprite tangent space (x = east, y = up, z = toward camera)
    const el = s.sun.elevation, az = s.sun.azimuth;
    this.sunTangent.x = Math.cos(el) * Math.sin(az);
    this.sunTangent.y = Math.sin(el);
    this.sunTangent.z = -Math.cos(el) * Math.cos(az);

    let t = performance.now();
    this.computeLightBuffer();
    st.lightMs = performance.now() - t;

    t = performance.now();
    this.renderShadows();
    st.assignMs = this.shadows.stats.assignMs;
    st.rasterMs = this.shadows.stats.rasterMs;
    st.quads = this.shadows.stats.renderedQuads || 0;
    st.shadowTexels = this.shadows.stats.texels || 0;
    st.tests = this.shadows.stats.tests || 0;
    st.maxAngleError = this.shadows.stats.maxAngleError || 0;
    if (this.settings.shadowModel === 'physical') this.maskLightBuffer();

    t = performance.now();
    this.drawGround();
    st.groundMs = performance.now() - t;

    t = performance.now();
    this.drawObjects();
    st.objectMs = performance.now() - t;

    t = performance.now();
    if (this.settings.glows) this.drawGlows();
    st.glowMs = performance.now() - t;

    t = performance.now();
    this.drawFog(dt);
    st.fogMs = performance.now() - t;

    const view = this.settings.view;
    if (view !== 'final') this.applyViewOverlay();

    t = performance.now();
    this.lut.enabled = this.settings.lut;
    this.lut.forceStyle = this.settings.lutStyle || null;
    this.lut.update(s.timeOfDay, ac, clamp(ai) * 0.85);
    if (view === 'final' && this.lut.enabled) this.lut.apply(this.out);
    st.lutMs = performance.now() - t;

    t = performance.now();
    if (view === 'final') this.drawDebug();
    st.debugMs = performance.now() - t;

    st.cacheEntries = this.cacheEntries;
    st.frameMs = performance.now() - t0;
    this.out.present();
    return st;
  }

  /* ------------------------------------------------- 1. dynamic light sources */

  computeLightBuffer() {
    const lb = this.lightBuf, { W, H } = this, sq = this.squashY;
    lb.fill(0);
    let touched = 0;
    for (const L of this.scene.lights) {
      if (!L.enabled) continue;
      const lx = L.x - this.camX, ly = L.y - this.camY;
      const r2 = L.radius * L.radius;
      const rr2 = r2 - L.z * L.z;
      if (rr2 <= 0) continue;
      const rr = Math.sqrt(rr2);
      if (lx < -rr || lx > W + rr || ly < -rr * sq - 4 || ly > H + rr * sq + 4) continue;
      const x0 = Math.max(0, Math.floor(lx - rr)), x1 = Math.min(W - 1, Math.ceil(lx + rr));
      const y0 = Math.max(0, Math.floor(ly - rr * sq)), y1 = Math.min(H - 1, Math.ceil(ly + rr * sq));
      if (x1 < x0 || y1 < y0) continue;
      const cr = L.color[0] / 255, cg = L.color[1] / 255, cb = L.color[2] / 255;
      const dz2 = L.z * L.z, cur = L._cur, dayScale = this.lightDayScale;
      for (let y = y0; y <= y1; y++) {
        const dyG = (y + 0.5 - ly) / sq;
        const base = dyG * dyG + dz2;
        let i3 = (y * W + x0) * 3;
        for (let x = x0; x <= x1; x++, i3 += 3) {
          const dx = x + 0.5 - lx;
          const d2 = dx * dx + base;
          if (d2 >= r2) continue;
          const k = 1 - d2 / r2;
          const att = k * k * cur * dayScale;
          lb[i3] += att * cr; lb[i3 + 1] += att * cg; lb[i3 + 2] += att * cb;
          touched++;
        }
      }
    }
    this.stats.lightTexels = touched;
  }

  /** 'physical' shadow model: shadows remove *direct* light only. */
  maskLightBuffer() {
    const lb = this.lightBuf, cov = this.coverage;
    for (let p = 0, i3 = 0; p < cov.length; p++, i3 += 3) {
      const c = cov[p];
      if (c <= 0) continue;
      const k = 1 - c;
      lb[i3] *= k; lb[i3 + 1] *= k; lb[i3 + 2] *= k;
    }
  }

  /* ---------------------------------------------------- 2+3. shadow generation */

  renderShadows() {
    const s = this.scene;
    if (!this.settings.shadows) {
      this.coverage.fill(0);
      const st = this.shadows.stats;
      st.renderedQuads = 0; st.texels = 0; st.assignMs = 0; st.rasterMs = 0; st.tests = 0;
      for (const c of s.casters) { c.usedSlots = 0; for (const sl of c.slots) { sl.active = false; sl.verts = null; } }
      return;
    }
    this.shadows.cfg.squashY = this.squashY;
    this.shadows.cfg.styleMode = this.settings.shadowStyle;
    const dir = s.sun.shadowDirection();
    s.shadowDir = dir;
    // projection parameters for the sun: ground distance per altitude unit
    this.shadows.sunDir = dir;
    this.shadows.sunK = clamp(s.sun.shadowLength * 0.5, 0.3, 2.6) * this.shadows.cfg.sunShadowScale;
    this.shadows.assign(s.casters, s.lights, s.sun, dir);
    this.shadows.renderMask(s.casters, this.shadowSprites, this.coverage, this.W, this.H, this.camX, this.camY);
  }

  /* ------------------------------------------------------------- 4. the ground */

  drawGround() {
    const { W, H } = this;
    const d = this.out.data;
    const alb = this.terrain.albedo, tw = this.terrain.w, th = this.terrain.h;
    const lb = this.lightBuf, cov = this.coverage, alt = this.altBuf;
    const [ar0, ag0, ab0] = this.ambient;
    // the ground is lit by the sun as well: a flat plane's N.L is sin(elevation)
    const sunGround = Math.max(0, Math.sin(this.scene.sun.elevation)) * this.scene.sun.intensity;
    const sc = this.scene.sun.color;
    const ar = ar0 + (sc[0] / 255) * sunGround;
    const ag = ag0 + (sc[1] / 255) * sunGround;
    const ab = ab0 + (sc[2] / 255) * sunGround;
    const camX = clamp(this.camX, 0, Math.max(0, tw - W));
    const camY = clamp(this.camY, 0, Math.max(0, th - H));
    const darkness = this.settings.shadowDarkness;
    const spriteModel = this.settings.shadowModel === 'sprite';
    const dither = this.settings.dither;
    const plain = this.settings.view !== 'final';

    for (let y = 0; y < H; y++) {
      const trow = (y + camY) * tw + camX;
      let i4 = y * W * 4, i3 = y * W * 3, p = y * W;
      for (let x = 0; x < W; x++, i4 += 4, i3 += 3, p++) {
        alt[p] = 0;
        if (plain) { d[i4] = 18; d[i4 + 1] = 18; d[i4 + 2] = 24; d[i4 + 3] = 255; continue; }
        const ai = (trow + x) * 3;
        let r = alb[ai] * (ar + lb[i3]);
        let g = alb[ai + 1] * (ag + lb[i3 + 1]);
        let b = alb[ai + 2] * (ab + lb[i3 + 2]);
        if (dither) {
          const n = (bayer(x, y) - 0.5) * 3.4;
          r += n; g += n; b += n;
        }
        const c = cov[p];
        if (c > 0 && spriteModel) {
          const k = 1 - c * darkness;
          r *= k; g *= k; b *= k;
        }
        d[i4] = r; d[i4 + 1] = g; d[i4 + 2] = b; d[i4 + 3] = 255;
      }
    }
    this.out.dirty = true;
  }

  /** Alternative inspector views, applied after everything else is drawn. */
  applyViewOverlay() {
    const view = this.settings.view;
    const { W, H } = this, d = this.out.data, cov = this.coverage, lb = this.lightBuf, alt = this.altBuf;
    for (let p = 0, i = 0; p < W * H; p++, i += 4) {
      if (view === 'shadows') {
        const c = cov[p];
        d[i] = 30 + c * 210; d[i + 1] = 24 + c * 46; d[i + 2] = 40 + c * 96; d[i + 3] = 255;
      } else if (view === 'lights') {
        d[i] = clamp(lb[p * 3]) * 255; d[i + 1] = clamp(lb[p * 3 + 1]) * 255;
        d[i + 2] = clamp(lb[p * 3 + 2]) * 255; d[i + 3] = 255;
      } else if (view === 'altitude') {
        const a = clamp(alt[p] / 56);
        d[i] = 18 + a * 70; d[i + 1] = 20 + a * 210; d[i + 2] = 28 + a * 120; d[i + 3] = 255;
      }
      // 'normals' is drawn per sprite, so the picture is already correct
    }
    this.out.dirty = true;
  }

  /* ------------------------------------------------------------ 5. the objects */

  drawObjects() {
    const s = this.scene;
    const view = this.settings.view;
    const colorless = (view === 'shadows' || view === 'lights' || view === 'altitude');
    const objs = s.objects;
    const { W, H } = this;
    const camX = this.camX, camY = this.camY;

    for (let i = 0; i < objs.length; i++) {
      const o = objs[i];
      const asset = o.asset;
      const canvas = asset.frameCanvases
        ? asset.frameCanvases[o.frame % asset.frameCanvases.length]
        : asset.canvas;
      const pivot = asset.pivot;
      const sx = Math.round(o.x - camX), sy = Math.round(o.y - camY);
      const dx = sx - Math.round(pivot.x), dy = sy - Math.round(pivot.y);
      if (dx > W || dy > H || dx + canvas.w < 0 || dy + canvas.h < 0) continue;

      let lit = null;
      if (colorless) lit = null;
      else if (view === 'normals') lit = this.normalView(canvas);
      else lit = this.lightSprite(o, canvas);

      // Own-shadow mask: a caster must not darken *itself* with its drop
      // shadow — that belongs to the ground layer underneath it. We raster the
      // caster's own slots into a scratch buffer clipped to its bbox and solve
      //   others = 1 - (1 - total) / (1 - own)
      // at blit time (exact inverse of the screen-blend accumulation).
      let own = null;
      if (!colorless && o.caster && this.settings.shadows && !this.settings.selfShadow) {
        own = this.ownCov;
        const clip = {
          x0: Math.max(0, dx), x1: Math.min(this.W - 1, dx + canvas.w - 1),
          y0: Math.max(0, dy), y1: Math.min(this.H - 1, dy + canvas.h - 1),
        };
        for (let yy = clip.y0; yy <= clip.y1; yy++) own.fill(0, yy * this.W + clip.x0, yy * this.W + clip.x1 + 1);
        this.shadows.rasterCaster(o.caster, this.shadowSprites, own, this.W, this.H, camX, camY, clip);
      }
      this.blitSprite(canvas, lit, dx, dy, o, asset, colorless, own);
    }
  }

  /** Normal-map debug view: tangent space straight to RGB. */
  normalView(canvas) {
    let cache = this.caches.get(canvas);
    if (!cache) { cache = new Map(); this.caches.set(canvas, cache); }
    let buf = cache.get(-1);
    if (buf) return buf.data;
    buf = new Uint8ClampedArray(canvas.w * canvas.h * 4);
    const n = canvas.normals, sd = canvas.data;
    for (let p = 0; p < canvas.w * canvas.h; p++) {
      if (sd[p * 4 + 3] === 0) continue;
      buf[p * 4] = n[p * 3]; buf[p * 4 + 1] = n[p * 3 + 1]; buf[p * 4 + 2] = n[p * 3 + 2];
      buf[p * 4 + 3] = 255;
    }
    cache.set(-1, { data: buf, directShare: 0 });
    return buf;
  }

  /** Lights that reach a sprite, strongest first (pooled — no per-frame allocs). */
  gatherSpriteLights(o, canvas) {
    const out = this._spriteLights;
    out.length = 0;
    const sq = this.squashY;
    const reach = Math.max(canvas.w, canvas.h) * 0.6;
    const alt = (o.asset.worldHeight || canvas.h) * 0.5;
    for (const L of this.scene.lights) {
      if (!L.enabled) continue;
      const dx = L.x - o.x, dyS = L.y - o.y;
      const dyG = dyS / sq;
      if (dx * dx + dyG * dyG > (L.radius + reach) ** 2) continue;
      const att = L.attenuate(dx, dyS, alt, sq);
      if (att <= 0.002) continue;
      const facing = this.settings.light3D ? verticalFacingFactor(dyS, this.settings.backFadeRate, 0.06) : 1;
      const rec = this._lightPool[out.length] || (this._lightPool[out.length] = {});
      rec.L = L; rec.dx = dx; rec.dyS = dyS; rec.dyG = dyG; rec.att = att;
      rec.facing = facing; rec.score = att * facing;
      out.push(rec);
    }
    if (out.length > 1) out.sort((a, b) => b.score - a.score);
    return out;
  }

  /**
   * Quantised light probe (52 bits) used as the cache key:
   *   time(5) | sun azimuth(5) | sun intensity(3) | flip(1) | light count(2)
   *   | 3 x [angle(6), intensity(3), facing(3)]
   */
  litKey(o, lights, flip) {
    const s = this.scene;
    let key = Math.floor(s.timeOfDay * 32) & 31;
    key = key * 32 + (Math.floor((((s.sun.azimuth / TAU) % 1) + 1) % 1 * 32) & 31);
    key = key * 8 + (Math.floor(clamp(s.sun.intensity / 1.3) * 7.99) & 7);
    key = key * 2 + (flip ? 1 : 0);
    key = key * 4 + Math.min(3, lights.length);
    for (let i = 0; i < 3; i++) {
      const rec = lights[i];
      if (!rec) { key = key * 4096; continue; }
      const ang = Math.atan2(rec.dx, rec.dyG);
      key = key * 64 + (Math.floor((((ang / TAU) % 1) + 1) % 1 * 64) & 63);
      key = key * 8 + (Math.floor(clamp(rec.att) * 7.99) & 7);
      key = key * 8 + (Math.floor(clamp(rec.facing) * 7.99) & 7);
    }
    return key;
  }

  /**
   * Per-sprite lighting: normal maps + sun + up to N dynamic lights, with the
   * article's "3D light distribution" (a sprite that blocks the light source
   * goes dark quickly).
   */
  lightSprite(o, canvas) {
    const lights = this.gatherSpriteLights(o, canvas);
    const flip = !!o.flip;
    // more than 3 lights => the probe cannot represent the state, skip the cache
    const useCache = this.settings.litCache && lights.length <= MAX_LIGHT_SHADOWS;
    let cache = this.caches.get(canvas);
    if (!cache) { cache = new Map(); this.caches.set(canvas, cache); }
    const key = useCache ? this.litKey(o, lights, flip) : -2;
    if (useCache) {
      const hit = cache.get(key);
      if (hit) { this.stats.cacheHits++; this._scratch.directShare = hit.directShare; return hit.data; }
    }
    this.stats.cacheMisses++;

    const buf = new Uint8ClampedArray(canvas.w * canvas.h * 4);
    const w = canvas.w, h = canvas.h;
    const src = canvas.data, nrm = canvas.normals, em = canvas.emissive;
    const pivot = o.asset.pivot;
    const [ar, ag, ab] = this.ambient;
    const s = this.scene, sq = this.squashY;
    const useNormals = this.settings.normals && nrm;
    const sunI = s.sun.intensity, sunC = s.sun.color;
    const stx = this.sunTangent.x, sty = this.sunTangent.y, stz = this.sunTangent.z;
    const nLights = lights.length;
    const glow = this.emissiveGlow;
    const dayScale = this.lightDayScale;
    const baseWX = o.x - pivot.x;
    let directSum = 0, ambientSum = 0;

    for (let py = 0; py < h; py++) {
      const alt = pivot.y - py;
      const rowBase = py * w;
      for (let px = 0; px < w; px++) {
        const p = rowBase + px;
        if (src[p * 4 + 3] === 0) continue;
        const sxp = flip ? (w - 1 - px) : px;
        const sp = rowBase + sxp;
        const spi = sp * 4;
        const alr = src[spi], alg = src[spi + 1], alb = src[spi + 2];

        let lr = ar, lg = ag, lb = ab;

        if (useNormals) {
          const ni = sp * 3;
          const nx = (nrm[ni] / 127.5) - 1;
          const ny = (nrm[ni + 1] / 127.5) - 1;
          const nz = (nrm[ni + 2] / 127.5) - 1;

          if (sunI > 0.001) {
            const ndl = nx * stx + ny * sty + nz * stz;
            if (ndl > 0) {
              const k = ndl * sunI;
              lr += (sunC[0] / 255) * k; lg += (sunC[1] / 255) * k; lb += (sunC[2] / 255) * k;
              directSum += k;
            }
          }
          for (let li = 0; li < nLights; li++) {
            const rec = lights[li];
            const L = rec.L;
            const ddx = L.x - (baseWX + sxp);
            const fx = flip ? -ddx : ddx;
            const dz = L.z - alt;
            const d2 = fx * fx + rec.dyG * rec.dyG + dz * dz;
            const r2 = L.radius * L.radius;
            if (d2 >= r2) continue;
            const kk = 1 - d2 / r2;
            const att = kk * kk * L._cur * rec.facing * dayScale;
            if (att <= 0.003) continue;
            const inv = 1 / Math.sqrt(d2);
            const ndl = (nx * fx + ny * dz + nz * rec.dyG) * inv;
            if (ndl <= 0) continue;
            const k = ndl * att;
            lr += (L.color[0] / 255) * k; lg += (L.color[1] / 255) * k; lb += (L.color[2] / 255) * k;
            directSum += k;
          }
        } else {
          if (sunI > 0.001) {
            lr += (sunC[0] / 255) * sunI * 0.7; lg += (sunC[1] / 255) * sunI * 0.7; lb += (sunC[2] / 255) * sunI * 0.7;
            directSum += sunI * 0.7;
          }
          for (let li = 0; li < nLights; li++) {
            const rec = lights[li];
            const k = rec.att * rec.facing * 0.85 * dayScale;
            lr += (rec.L.color[0] / 255) * k; lg += (rec.L.color[1] / 255) * k; lb += (rec.L.color[2] / 255) * k;
            directSum += k;
          }
        }

        ambientSum += (ar + ag + ab) * 0.333;
        let r = alr * lr, g = alg * lg, b = alb * lb;
        if (em && em[sp]) { r += alr * glow; g += alg * glow; b += alb * glow * 0.75; }

        const di = p * 4;
        buf[di] = r; buf[di + 1] = g; buf[di + 2] = b; buf[di + 3] = src[p * 4 + 3];
      }
    }

    const directShare = clamp(directSum / Math.max(0.0001, directSum + ambientSum));
    this._scratch.directShare = directShare;
    if (useCache) {
      if (cache.size >= this.settings.cacheBudget) {
        cache.delete(cache.keys().next().value);
        this.cacheEntries--;
      }
      cache.set(key, { data: buf, directShare });
      this.cacheEntries++;
    }
    return buf;
  }

  /**
   * Blit a lit sprite: shadow mask multiply, wind deformation (per-scanline x
   * offset weighted by height) and the altitude buffer write used by the fog.
   */
  blitSprite(canvas, lit, dx, dy, o, asset, altitudeOnly, ownCov) {
    const { W, H } = this;
    const d = this.out.data, cov = this.coverage, alt = this.altBuf;
    const w = canvas.w, h = canvas.h;
    const em = canvas.emissive;
    const settings = this.settings;
    const pivot = asset.pivot;

    let sway = null;
    if (settings.wind && asset.sway && settings.windStrength > 0) {
      sway = {
        amp: asset.sway.amp * (0.3 + this.scene.windStrength * 1.4) * settings.windStrength,
        power: asset.sway.power, t: this.scene.windTime, phase: o.phase,
      };
    }

    const share = settings.shadowModel === 'sprite'
      ? settings.shadowDarkness
      : settings.shadowDarkness * (this._scratch.directShare || 0.8);

    for (let py = 0; py < h; py++) {
      const dyy = dy + py;
      if (dyy < 0 || dyy >= H) continue;
      let off = 0;
      if (sway) {
        const wNorm = Math.pow((h - 1 - py) / h, sway.power);
        off = Math.round(sway.amp * wNorm * Math.sin(sway.t * 2.1 + sway.phase + py * 0.42));
      }
      const altVal = clamp(pivot.y - py, 0, 255) | 0;
      const row = py * w;
      let p = dyy * W;
      const rowBase4 = dyy * W * 4;
      for (let px = 0; px < w; px++) {
        const dxx = dx + px + off;
        if (dxx < 0 || dxx >= W) continue;
        const sp = row + px;
        const alpha = lit ? lit[sp * 4 + 3] : canvas.data[sp * 4 + 3];
        if (alpha === 0 && !altitudeOnly) continue;
        const pp = p + dxx;
        if (alt[pp] < altVal) alt[pp] = altVal;
        if (altitudeOnly || alpha === 0 || !lit) continue;
        const dstI = rowBase4 + dxx * 4;
        const si = sp * 4;
        let r = lit[si], g = lit[si + 1], b = lit[si + 2];
        let c = cov[pp];
        if (ownCov) {
          const oc = ownCov[pp];
          if (oc > 0.0001) {
            const denom = 1 - oc;
            c = denom > 0.02 ? 1 - (1 - c) / denom : Math.max(0, c - oc);
            if (c < 0) c = 0; else if (c > 1) c = 1;
          }
        }
        if (c > 0 && !(em && em[sp])) {
          const k = 1 - c * share;
          r *= k; g *= k; b *= k;
        }
        if (alpha >= 255) { d[dstI] = r; d[dstI + 1] = g; d[dstI + 2] = b; d[dstI + 3] = 255; }
        else {
          const na = alpha / 255, ia = 1 - na;
          d[dstI] = d[dstI] * ia + r * na;
          d[dstI + 1] = d[dstI + 1] * ia + g * na;
          d[dstI + 2] = d[dstI + 2] * ia + b * na;
          d[dstI + 3] = 255;
        }
      }
    }
    this.out.dirty = true;
  }

  /* ------------------------------------------------------- 6. additive glows */

  drawGlows() {
    const s = this.scene;
    const night = clamp(1 - s.dayAmount * 0.9);
    const A = this.assets;
    if (night > 0.02) {
      for (const L of s.lights) {
        if (!L.enabled) continue;
        const sprite = L.kind === 'oven' ? A.glowFire : A.glowWarm;
        const scale = (L.radius * (L.kind === 'oven' ? 1.5 : 1.75)) / sprite.w;
        const alpha = clamp(L._cur * 0.42 * night * (L.intensity / 1.2));
        if (alpha <= 0.01) continue;
        this.blitAddScaled(sprite, L.x - this.camX, L.y - this.camY - L.z * 0.35, scale, alpha, L.color);
      }
    }
    if (night > 0.3) {
      for (const f of s.fireflies) {
        const pulse = Math.sin(s.time * 2.4 + f.phase) * 0.5 + 0.5;
        if (pulse < 0.55) continue;
        const x = Math.round(f.x - this.camX);
        const y = Math.round(f.y - this.camY - Math.sin(s.time + f.phase) * f.r * 0.4);
        if (x < 0 || y < 0 || x >= this.W || y >= this.H) continue;
        const a = (pulse - 0.55) * 2 * night;
        this.out.add(x, y, 214, 255, 150, a * 230);
        this.out.add(x + 1, y, 150, 220, 110, a * 110);
        this.out.add(x, y + 1, 150, 220, 110, a * 90);
      }
    }
  }

  blitAddScaled(src, cx, cy, scale, alpha, tint) {
    const sw = src.w, sh = src.h;
    const dw = Math.max(1, Math.round(sw * scale)), dh = Math.max(1, Math.round(sh * scale));
    const x0 = Math.round(cx - dw / 2), y0 = Math.round(cy - dh / 2);
    const { W, H } = this;
    const tr = tint[0] / 255, tg = tint[1] / 255, tb = tint[2] / 255;
    const sd = src.data;
    for (let y = 0; y < dh; y++) {
      const dyy = y0 + y;
      if (dyy < 0 || dyy >= H) continue;
      const sy = Math.min(sh - 1, (y * sh / dh) | 0) * sw;
      for (let x = 0; x < dw; x++) {
        const dxx = x0 + x;
        if (dxx < 0 || dxx >= W) continue;
        const si = (sy + Math.min(sw - 1, (x * sw / dw) | 0)) * 4;
        const a = (sd[si + 3] / 255) * alpha;
        if (a <= 0.004) continue;
        this.out.add(dxx, dyy, sd[si] * tr, sd[si + 1] * tg, sd[si + 2] * tb, a * 255);
      }
    }
  }

  /* -------------------------------------------------------------- 7. the fog */

  drawFog(dt) {
    const amount = this.settings.fog;
    if (amount <= 0.001) return;
    const s = this.scene;
    const ac = ambientColorGradient(s.timeOfDay);
    const ai = ambientIntensity(s.timeOfDay);
    const col = [
      clamp(ac[0] * (0.35 + ai * 0.85) + 44, 0, 255),
      clamp(ac[1] * (0.35 + ai * 0.85) + 48, 0, 255),
      clamp(ac[2] * (0.35 + ai * 0.85) + 58, 0, 255),
    ];
    this.fog.update(dt, this.camX);
    this.fog.render(this.out, this.altBuf, amount, col);
  }

  /* --------------------------------------------------------- 9. debug overlay */

  line(x0, y0, x1, y1, color, alpha = 255) {
    this.out.line(Math.round(x0), Math.round(y0), Math.round(x1), Math.round(y1), color[0], color[1], color[2], alpha, 1);
  }

  circle(cx, cy, r, color, alpha = 255) {
    const steps = Math.max(14, Math.round(r * 1.8));
    let px = cx + r, py = cy;
    for (let i = 1; i <= steps; i++) {
      const a = (i / steps) * TAU;
      const x = cx + Math.cos(a) * r, y = cy + Math.sin(a) * r * this.squashY;
      this.line(px, py, x, y, color, alpha);
      px = x; py = y;
    }
  }

  box(x, y, w, h, color, alpha = 255) {
    this.line(x, y, x + w, y, color, alpha);
    this.line(x + w, y, x + w, y + h, color, alpha);
    this.line(x + w, y + h, x, y + h, color, alpha);
    this.line(x, y + h, x, y, color, alpha);
  }

  drawDebug() {
    // State warnings baked into the pixel buffer: toggles that re-create the
    // original bugs must be visible in any screenshot, crop or capture.
    let wy = 3;
    if (this.settings.selfShadow) {
      drawText(this.out, 'SELF-SHADOW ON: CASTERS DARKEN THEMSELVES (DEMO TOGGLE IN PANEL)', 3, wy, [255, 110, 80], 240);
      wy += 7;
    }
    if (this.settings.shadowStyle && this.settings.shadowStyle !== 'auto') {
      drawText(this.out, `SHADOW CONSTRUCTION OVERRIDE: ${this.settings.shadowStyle.toUpperCase()} (AUTO = PER-ASSET)`, 3, wy, [255, 190, 80], 240);
      wy += 7;
    }
    const tag = BUILD;
    drawText(this.out, tag, this.W - tag.length * 4 - 2, this.H - 7, [215, 220, 230], 120);
    const dbg = this.settings.debug;
    const s = this.scene;
    const out = this.out;
    const camX = this.camX, camY = this.camY;

    if (dbg.grid) {
      const g = this.shadows.grid, cell = g.cell;
      for (let cx = 0; cx <= g.cols; cx++) {
        const x = cx * cell - camX;
        if (x < -2 || x > this.W + 2) continue;
        this.line(x, 0, x, this.H, [70, 92, 124], 80);
      }
      for (let cy = 0; cy <= g.rows; cy++) {
        const y = cy * cell - camY;
        if (y < -2 || y > this.H + 2) continue;
        this.line(0, y, this.W, y, [70, 92, 124], 80);
      }
    }

    if (dbg.lightRadii) {
      s.lights.forEach((L, i) => {
        if (!L.enabled) return;
        const x = L.x - camX, y = L.y - camY;
        if (x < -90 || y < -90 || x > this.W + 90 || y > this.H + 90) return;
        const rr = Math.sqrt(Math.max(1, L.radius * L.radius - L.z * L.z));
        this.circle(x, y, rr, L.color, 120);
        this.line(x - 3, y, x + 3, y, [255, 255, 255], 210);
        this.line(x, y - 3, x, y + 3, [255, 255, 255], 210);
        drawText(out, `${i}`, Math.round(x) + 4, Math.round(y) - 8, [255, 240, 200], 235);
        drawText(out, `Z${L.z | 0}`, Math.round(x) + 4, Math.round(y) - 2, [170, 195, 230], 190);
      });
    }

    if (dbg.sunVector) {
      const dir = s.shadowDir || s.sun.shadowDirection();
      const cx = 26, cy = 26;
      this.circle(cx, cy, 15, [255, 230, 160], 130);
      this.line(cx, cy, cx + dir.x * 15, cy - dir.y * 15 * this.squashY, [255, 244, 190], 235);
      drawText(out, 'SUN', cx - 6, cy + 16, [255, 240, 180], 220);
    }

    const wantCasters = dbg.shadowQuads || dbg.pivots || dbg.links || dbg.channels;
    if (wantCasters) {
      const targets = s.selectedCaster ? [s.selectedCaster] : s.casters;
      for (const c of targets) {
        const px = c.x - camX, py = c.y - camY;
        if (px < -80 || py < -80 || px > this.W + 80 || py > this.H + 80) continue;
        const isSel = c === s.selectedCaster;

        if (dbg.pivots) {
          out.setPixel(Math.round(px), Math.round(py), 255, 90, 90, 255);
          out.setPixel(Math.round(px) + 1, Math.round(py), 255, 90, 90, 255);
          out.setPixel(Math.round(px), Math.round(py) - 1, 255, 90, 90, 255);
        }
        if (dbg.shadowQuads) {
          const palette = [[255, 224, 120], [120, 220, 255], [160, 255, 170], [255, 160, 220]];
          for (let i = 0; i < c.slots.length; i++) {
            const slot = c.slots[i];
            if (!slot.active || !slot.verts) continue;
            const col = palette[i];
            const v = slot.verts;
            for (let k = 0; k < 4; k++) {
              const a = v[k], b = v[(k + 1) % 4];
              this.line(a.x, a.y, b.x, b.y, col, isSel ? 240 : 110);
            }
          }
        }
        if (dbg.links) {
          const palette = [[120, 220, 255], [160, 255, 170], [255, 160, 220]];
          for (let i = 1; i < c.slots.length; i++) {
            const slot = c.slots[i];
            if (!slot.active || !slot.light) continue;
            this.line(px, py, slot.light.x - camX, slot.light.y - camY, palette[i - 1], 160);
          }
        }
        if (dbg.channels && isSel) {
          let bx = clamp(Math.round(px) + 12, 2, this.W - 52);
          let by = clamp(Math.round(py) - 30, 2, this.H - 22);
          out.rect(bx - 3, by - 3, 50, 22, 12, 10, 18, 190);
          this.box(bx - 3, by - 3, 50, 22, [120, 130, 160], 200);
          for (let i = 0; i < c.slots.length; i++) {
            const slot = c.slots[i];
            const x = bx + i * 11;
            if (!slot.active) { this.box(x, by, 8, 8, [90, 90, 110], 170); continue; }
            out.rect(x, by, 8, 8, slot.color[0], slot.color[1], slot.color[2], 255);
            this.box(x, by, 8, 8, [255, 255, 255], 210);
          }
          drawText(out, 'R=ANG G=LEN', bx - 1, by + 10, [214, 218, 236], 235);
          drawText(out, 'B=INT A=WID', bx - 1, by + 16, [214, 218, 236], 235);
        }
      }
      if (s.selectedCaster) {
        const c = s.selectedCaster;
        const tx = clamp(Math.round(c.x - camX) - 24, 2, this.W - 62);
        const ty = clamp(Math.round(c.y - camY) + 5, 2, this.H - 8);
        drawText(out, `${c.usedSlots}/4`, tx, ty, [255, 255, 255], 240);
      }
    }
  }
}
