/**
 * shadows.js — the dynamic shadow generator.
 *
 * Faithful to the Graveyard Keeper write-up:
 *
 *  1. "Shadows are made with sprites rotating around a point."
 *     -> every caster has a pivot (its base); a shadow *sprite* is rotated
 *        around that pivot. No skew ("I tried to add a skew there, but it
 *        turned out to be unnecessary") — an optional skew parameter is kept in
 *        the config, defaulting to 0, so you can reproduce the experiment.
 *
 *  2. "Every object may have a maximum of 4 shadows. The shade from the sun and
 *     the additional three from dynamic light sources."
 *     -> slot 0 is always the sun/moon, slots 1..3 the three nearest lights.
 *
 *  3. "The problem 'how to find the closest 3 light sources and to calculate the
 *     distance and an angle' was solved with a script running in the Update()
 *     loop ... it's not the quickest way considering how much math is involved."
 *     -> assign() is that Update() loop, timed and shown in the HUD. A uniform
 *     light grid ("what I would do today") can be toggled on to compare.
 *
 *  4. "I did the sprite rotation not modifying the transform but inside a vertex
 *     shader ... You just put a parameter to a sprite (I used the color channel
 *     for that, all the shadows are black anyway)."
 *     -> every shadow carries a packed RGBA8 "vertex colour":
 *          R = rotation angle (8 bit! ~1.4 deg steps)
 *          G = length, B = intensity, A = width
 *        shadowVertexStage() consumes it exactly like a vertex shader would: it
 *        transforms the 4 corners of the quad, the transform is never touched.
 *
 *  5. "The sprite is highly distorted vertically (the shadow sprite original
 *     looks like a circle). That's why its rotation looks like not just a simple
 *     rotation but also like a distortion."
 *     -> squashY: the ground plane is foreshortened, so rotation + vertical
 *        squash = the characteristic wobble of these shadows.
 */

import { clamp } from './util.js';

export const TAU = Math.PI * 2;
export const MAX_LIGHT_SHADOWS = 3;   // + 1 sun slot = the article's 4 shadows

/* ------------------------------------------------------------- vertex colour */

/**
 * Pack the shadow parameters into the sprite's vertex colour.
 * 8 bits per channel is all Unity's Color32 gives you — and all we need,
 * "all the shadows are black anyway".
 */
export function packShadowColor(slot, cfg) {
  const a = ((slot.angle / TAU) % 1 + 1) % 1;
  slot.color[0] = Math.round(a * 255);
  slot.color[1] = Math.round(clamp(slot.length / cfg.lengthRange) * 255);
  slot.color[2] = Math.round(clamp(slot.intensity) * 255);
  slot.color[3] = Math.round(clamp(slot.width / cfg.widthRange) * 255);
}

/** Decode the vertex colour back (this is what the "shader" sees). */
export function unpackShadowColor(slot, cfg) {
  slot.uAngle = (slot.color[0] / 255) * TAU;
  slot.uLength = (slot.color[1] / 255) * cfg.lengthRange;
  slot.uIntensity = slot.color[2] / 255;
  slot.uWidth = (slot.color[3] / 255) * cfg.widthRange;
  // how much precision the 8-bit channel cost us
  let err = Math.abs(slot.uAngle - (((slot.angle / TAU) % 1 + 1) % 1) * TAU);
  if (err > Math.PI) err = TAU - err;
  slot.angleErrorDeg = (err * 180) / Math.PI;
}

export class ShadowSlot {
  constructor(index) {
    this.index = index;
    this.active = false;
    this.isSun = index === 0;
    this.light = null;
    this.color = new Uint8Array(4);      // R=angle G=length B=intensity A=width
    this.angle = 0; this.length = 0; this.intensity = 0; this.width = 1;
    this.uAngle = 0; this.uLength = 0; this.uIntensity = 0; this.uWidth = 1;
    this.angleErrorDeg = 0;
    this.distance = 0;
    this.attenuation = 0;
    this.kind = 'oval';
    this.sprite = null;
    this.verts = null;
  }
}

/**
 * A shadow caster: anything in the scene that owns up to 4 shadow slots.
 * `x`/`y` is the ground/base position in screen pixels.
 */
export class ShadowCaster {
  constructor(opts) {
    this.name = opts.name || 'object';
    this.x = opts.x; this.y = opts.y;
    this.worldHeight = opts.worldHeight ?? 12;
    this.shadowKind = opts.shadowKind ?? 'oval';
    this.lengthScale = opts.lengthScale ?? 1;
    this.widthScale = opts.widthScale ?? 1;
    this.strength = opts.strength ?? 1;      // per-object shadow density
    this.castSunShadow = opts.castSunShadow !== false;
    this.castLightShadows = opts.castLightShadows !== false;
    this.slots = [];
    for (let i = 0; i < 1 + MAX_LIGHT_SHADOWS; i++) this.slots.push(new ShadowSlot(i));
    this.usedSlots = 0;
    this.lastCost = 0;
  }

  /** The pivot the shadow sprites rotate around (the object's feet). */
  pivotX(cfg) { return cfg.pixelSnap ? Math.round(this.x) : this.x; }
  pivotY(cfg) { return cfg.pixelSnap ? Math.round(this.y) : this.y; }
}

/* --------------------------------------------------------------- light grid */

/**
 * Uniform spatial hash of the light sources. The article's Update() loop tested
 * every object against every light; this is the "if I programmed it today"
 * version (in Unity: a jobified broad phase). Toggle it in the UI to compare.
 */
export class LightGrid {
  constructor(w, h, cell = 48) {
    this.w = w; this.h = h; this.cell = cell;
    this.cols = Math.ceil(w / cell); this.rows = Math.ceil(h / cell);
    this.buckets = new Array(this.cols * this.rows);
    for (let i = 0; i < this.buckets.length; i++) this.buckets[i] = [];
    this._used = [];
  }

  rebuild(lights) {
    // clearing 60+ arrays every frame costs more than the broad phase saves;
    // only recycle the buckets the previous rebuild actually filled
    for (let i = 0; i < this._used.length; i++) this.buckets[this._used[i]].length = 0;
    this._used.length = 0;
    for (const L of lights) {
      if (!L.enabled) continue;
      const r = L.radius;
      const c0 = Math.max(0, Math.floor((L.x - r) / this.cell));
      const c1 = Math.min(this.cols - 1, Math.floor((L.x + r) / this.cell));
      const r0 = Math.max(0, Math.floor((L.y - r) / this.cell));
      const r1 = Math.min(this.rows - 1, Math.floor((L.y + r) / this.cell));
      for (let ry = r0; ry <= r1; ry++) {
        for (let cx = c0; cx <= c1; cx++) {
          const bi = ry * this.cols + cx;
          if (this.buckets[bi].length === 0) this._used.push(bi);
          this.buckets[bi].push(L);
        }
      }
    }
  }

  /** Lights whose radius reaches (x, y). Result is written into `out`. */
  query(x, y, out) {
    out.length = 0;
    const cx = Math.floor(x / this.cell), cy = Math.floor(y / this.cell);
    if (cx < 0 || cy < 0 || cx >= this.cols || cy >= this.rows) return out;
    const b = this.buckets[cy * this.cols + cx];
    for (let i = 0; i < b.length; i++) out.push(b[i]);
    return out;
  }
}

/* ------------------------------------------------------------- shadow system */

export const DEFAULT_SHADOW_CONFIG = {
  squashY: 0.42,          // ground-plane foreshortening ("highly distorted vertically")
  pixelSnap: true,        // keep pivots on the pixel grid (pixel art must not shimmer)
  angleQuantize: 0,       // extra angle snapping in degrees (0 = only the 8-bit channel)
  lengthRange: 64,        // px encoded by the G channel
  widthRange: 2.5,        // width factor encoded by the A channel
  widthScale: 1.55,       // global shadow width
  skew: 0,                // the experiment the article abandoned
  maxDynamic: MAX_LIGHT_SHADOWS,
  minIntensity: 0.015,
  minAngleStep: 0,
  sunShadowScale: 1,
  dynamicSunSuppression: 0.55, // daylight washes out torch shadows
  ambientLift: 0.18,           // shadows never go 100% black (sky/ambient bounce)
  lengthResponse: 1,           // how strongly distance stretches point-light shadows
};

export class ShadowSystem {
  constructor(worldW, worldH, config = {}) {
    this.cfg = Object.assign({}, DEFAULT_SHADOW_CONFIG, config);
    this.grid = new LightGrid(worldW, worldH, 48);
    this.useGrid = true;
    this._candidates = [];
    this._near = [];
    this._pool = [];
    // stats for the HUD
    this.stats = {
      assignMs: 0, rasterMs: 0, quads: 0, texels: 0, tests: 0,
      casters: 0, lights: 0, maxAngleError: 0,
    };
  }

  setConfig(patch) { Object.assign(this.cfg, patch); }

  /**
   * The Update() loop: for every caster find the sun + the 3 nearest lights and
   * write the packed vertex colour of each shadow slot.
   */
  assign(casters, lights, sun, sunShadowDir) {
    const t0 = performance.now();
    const cfg = this.cfg;
    let tests = 0, quads = 0, maxErr = 0;
    let activeLights = 0;
    for (const L of lights) if (L.enabled) activeLights++;
    if (this.useGrid) this.grid.rebuild(lights);

    const sunVisible = sun && sun.intensity > 0.02;

    for (let ci = 0; ci < casters.length; ci++) {
      const c = casters[ci];
      c.usedSlots = 0;

      /* ---- slot 0: the sun (or the moon) ---- */
      const s0 = c.slots[0];
      s0.active = false; s0.light = null;
      if (sunVisible && c.castSunShadow) {
        const dir = sunShadowDir;                       // ground-space direction
        // angle of the quad: local north (0,1) must land on `dir`
        // local north (0,1) must land on the ground-space shadow direction:
        // R(theta)(0,1) = (-sin, cos) = dir  =>  theta = atan2(-dir.x, dir.y)
        s0.angle = Math.atan2(-dir.x, dir.y);
        const raw = c.worldHeight * c.lengthScale * sun.shadowLength * cfg.sunShadowScale;
        s0.length = clamp(raw, 3, cfg.lengthRange);
        s0.intensity = clamp((sun.shadowStrength ?? sun.intensity) * c.strength, 0, 1);
        s0.width = c.widthScale;
        s0.distance = Infinity;
        s0.kind = c.shadowKind;
        s0.active = true;
        c.usedSlots = 1;
        quads++;
      }

      /* ---- slots 1..3: the nearest dynamic lights ---- */
      const cand = this.useGrid ? this.grid.query(c.x, c.y, this._candidates) : lights;
      const near = this._near;
      near.length = 0;
      for (let i = 0; i < cand.length; i++) {
        const L = cand[i];
        if (!L.enabled) continue;
        tests++;
        const dx = L.x - c.x;
        const dyScreen = L.y - c.y;
        // ground-space distance (the ground plane is squashed on screen)
        const dyGround = dyScreen / cfg.squashY;
        const d = Math.sqrt(dx * dx + dyGround * dyGround);
        if (d > L.radius) continue;
        const att = L.attenuateAt(c.x, c.y, c.worldHeight * 0.4, cfg.squashY);
        if (att <= cfg.minIntensity) continue;
        // reuse pooled candidate records: this loop runs casters*lights times
        // per frame and must not allocate (that is the "not the quickest way"
        // the article complains about).
        const rec = this._pool[near.length] || (this._pool[near.length] = {});
        rec.L = L; rec.dx = dx; rec.dyScreen = dyScreen; rec.dyGround = dyGround;
        rec.d = d; rec.att = att;
        near.push(rec);
      }
      if (near.length > 1) near.sort((a, b) => a.d - b.d);

      const maxDyn = Math.min(cfg.maxDynamic, MAX_LIGHT_SHADOWS);
      const n = Math.min(maxDyn, near.length);
      for (let i = 0; i < maxDyn; i++) {
        const slot = c.slots[1 + i];
        if (i >= n || !c.castLightShadows) { slot.active = false; slot.light = null; continue; }
        const hit = near[i];
        // angle: theta = atan2(dxScreen, dyScreen / squashY)  (see file header)
        slot.angle = Math.atan2(hit.dx, hit.dyScreen / cfg.squashY);
        const stretch = 0.5 + 0.85 * clamp(hit.d / Math.max(1, hit.L.radius)) * cfg.lengthResponse;
        const heightTerm = c.worldHeight / Math.max(4, hit.L.z);
        slot.length = clamp(c.worldHeight * c.lengthScale * heightTerm * stretch * 0.85, 3, cfg.lengthRange);
        const suppression = 1 - (sun ? sun.intensity : 0) * cfg.dynamicSunSuppression;
        slot.intensity = clamp(hit.att * hit.L.intensity * c.strength * suppression, 0, 1);
        slot.width = c.widthScale * (0.85 + 0.35 * clamp(1 - hit.d / hit.L.radius));
        slot.distance = hit.d;
        slot.attenuation = hit.att;
        slot.kind = c.shadowKind;
        slot.light = hit.L;
        slot.active = slot.intensity > cfg.minIntensity;
        if (slot.active) { c.usedSlots++; quads++; }
      }

      /* ---- pack + unpack through the 8-bit vertex colour ---- */
      for (let i = 0; i < c.slots.length; i++) {
        const slot = c.slots[i];
        if (!slot.active) continue;
        if (cfg.angleQuantize > 0) {
          const step = (cfg.angleQuantize * Math.PI) / 180;
          slot.angle = Math.round(slot.angle / step) * step;
        }
        packShadowColor(slot, cfg);
        unpackShadowColor(slot, cfg);
        if (slot.angleErrorDeg > maxErr) maxErr = slot.angleErrorDeg;
      }
    }

    const t1 = performance.now();
    this.stats.assignMs = t1 - t0;
    this.stats.tests = tests;
    this.stats.quads = quads;
    this.stats.casters = casters.length;
    this.stats.lights = activeLights;
    this.stats.maxAngleError = maxErr;
    return this.stats;
  }

  /**
   * VERTEX STAGE — runs once per corner (4x per shadow), never touches a
   * transform. Input: the packed vertex colour. Output: 4 screen-space corners.
   */
  vertexStage(slot, sprite, pivotX, pivotY) {
    const cfg = this.cfg;
    const angle = slot.uAngle;
    const quadLen = Math.max(1, slot.uLength);
    const aspect = sprite.w / sprite.h;
    const quadW = quadLen * aspect * slot.uWidth * cfg.widthScale;
    const skew = cfg.skew;

    const c = Math.cos(angle), s = Math.sin(angle);
    const sq = cfg.squashY;
    // unit quad in ground space: u across [-0.5, 0.5], v along [0, 1] from the pivot
    const lu = [-0.5 * quadW, 0.5 * quadW, 0.5 * quadW, -0.5 * quadW];
    const lv = [0, 0, quadLen, quadLen];
    const out = slot._verts || (slot._verts = [{ x: 0, y: 0 }, { x: 0, y: 0 }, { x: 0, y: 0 }, { x: 0, y: 0 }]);
    for (let i = 0; i < 4; i++) {
      const gx = lu[i] + skew * lv[i];       // the abandoned skew experiment
      const gy = lv[i];
      const rx = gx * c - gy * s;
      const ry = gx * s + gy * c;
      out[i].x = pivotX + rx;
      out[i].y = pivotY - ry * sq;           // ground -> screen foreshortening
    }
    return out;
  }

  /**
   * RASTER + FRAGMENT STAGE — the affine basis comes straight out of the vertex
   * stage (v1-v0 is the u axis, v3-v0 the v axis), so the fragment code is just
   * an inverse 2x2 and a nearest-neighbour sample of the shadow sprite.
   *
   * Coverage accumulates into a screen-space mask (screen blending) which the
   * renderer later uses to remove *direct* light — shadows block light, they
   * don't paint black.
   */
  raster(verts, sprite, alphaScale, coverage, W, H) {
    if (alphaScale <= 0.004) return 0;
    const o = verts[0], a = verts[1], b = verts[3];
    const exx = a.x - o.x, exy = a.y - o.y;
    const eyx = b.x - o.x, eyy = b.y - o.y;
    const det = exx * eyy - exy * eyx;
    if (Math.abs(det) < 1e-4) return 0;
    const inv = 1 / det;
    // inverse affine: [u,v] = M^-1 * (p - o)
    const m00 = eyy * inv, m01 = -eyx * inv;
    const m10 = -exy * inv, m11 = exx * inv;

    let minx = Infinity, miny = Infinity, maxx = -Infinity, maxy = -Infinity;
    for (let i = 0; i < 4; i++) {
      const v = verts[i];
      if (v.x < minx) minx = v.x;
      if (v.x > maxx) maxx = v.x;
      if (v.y < miny) miny = v.y;
      if (v.y > maxy) maxy = v.y;
    }
    const x0 = Math.max(0, Math.floor(minx)), x1 = Math.min(W - 1, Math.ceil(maxx));
    const y0 = Math.max(0, Math.floor(miny)), y1 = Math.min(H - 1, Math.ceil(maxy));
    if (x1 < x0 || y1 < y0) return 0;

    const sw = sprite.w, sh = sprite.h, sd = sprite.data;
    let texels = 0;
    for (let y = y0; y <= y1; y++) {
      const dy = y + 0.5 - o.y;
      let p = y * W + x0;
      for (let x = x0; x <= x1; x++, p++) {
        const dx = x + 0.5 - o.x;
        const u = m00 * dx + m01 * dy;
        if (u < 0 || u >= 1) continue;
        const v = m10 * dx + m11 * dy;
        if (v < 0 || v >= 1) continue;
        // nearest sampling keeps the shadow sprite crunchy
        const tx = (u * sw) | 0;
        const ty = ((1 - v) * sh) | 0;
        const sa = sd[(ty * sw + tx) * 4 + 3];
        if (sa === 0) continue;
        texels++;
        const alpha = (sa / 255) * alphaScale;
        const prev = coverage[p];
        coverage[p] = prev + alpha * (1 - prev);
      }
    }
    return texels;
  }

  /**
   * Build the whole shadow mask for the frame.
   * coverage: Float32Array(W*H) in 0..1, cleared here.
   */
  renderMask(casters, shadowSprites, coverage, W, H, camX = 0, camY = 0) {
    const t0 = performance.now();
    coverage.fill(0);
    const cfg = this.cfg;
    let quads = 0, texels = 0;
    for (let i = 0; i < casters.length; i++) {
      const c = casters[i];
      // the pivot is the object's feet, in screen space
      const px = c.pivotX(cfg) - camX, py = c.pivotY(cfg) - camY;
      if (px < -90 || py < -90 || px > W + 90 || py > H + 90) continue;
      for (let s = 0; s < c.slots.length; s++) {
        const slot = c.slots[s];
        if (!slot.active) { slot.verts = null; continue; }
        const sprite = shadowSprites[c.shadowKind] || shadowSprites.oval;
        slot.sprite = sprite;
        const verts = this.vertexStage(slot, sprite, px, py);
        slot.verts = verts;
        const alpha = slot.uIntensity * (1 - cfg.ambientLift * 0.5);
        texels += this.raster(verts, sprite, alpha, coverage, W, H);
        quads++;
      }
    }
    this.stats.rasterMs = performance.now() - t0;
    this.stats.renderedQuads = quads;
    this.stats.texels = texels;
    return this.stats;
  }
}

/** Convert the float coverage mask into an RGBA debug texture. */
export function coverageToBuffer(coverage, fb, tint = [10, 6, 18]) {
  const d = fb.data;
  for (let p = 0, i = 0; p < coverage.length; p++, i += 4) {
    const c = coverage[p];
    d[i] = tint[0]; d[i + 1] = tint[1]; d[i + 2] = tint[2];
    d[i + 3] = c >= 1 ? 255 : (c * 255) | 0;
  }
  fb.dirty = true;
}
