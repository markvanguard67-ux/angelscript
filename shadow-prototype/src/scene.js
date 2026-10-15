/**
 * scene.js — the graveyard: props, lights, the keeper and the sun.
 *
 * Every prop that can cast a shadow owns a ShadowCaster (see shadows.js), so
 * the "4 shadows per object" budget from the article is a real, inspectable
 * object in this prototype.
 */

import { Sun, Light, verticalFacingFactor } from './lighting.js?v=r5';
import { ShadowCaster } from './shadows.js?v=r5';
import { clamp, lerp, mulberry32, valueNoise } from './util.js?v=r5';

export const WORLD_W = 480;
export const WORLD_H = 260;

export class Scene {
  constructor(assets, opts = {}) {
    this.assets = assets;
    this.w = opts.worldW || WORLD_W;
    this.h = opts.worldH || WORLD_H;
    this.objects = [];
    this.lights = [];
    this.casters = [];
    this.sun = new Sun();
    this.timeOfDay = opts.timeOfDay ?? 0.70;   // golden hour: long, readable shadows
    this.timeSpeed = opts.timeSpeed ?? 0.006;  // 1.0 => a full day in 1 second
    this.paused = false;
    this.time = 0;
    this.camX = 0; this.camY = 0;
    this.windTime = 0;
    this.windStrength = 0.6;
    this.fireflies = [];
    this._rnd = mulberry32(20251023);
    this.selectedCaster = null;
    this.hoverLight = null;
    this.dragLight = null;

    this.build();
  }

  /* -------------------------------------------------------------- construction */

  add(asset, x, y, opts = {}) {
    const o = {
      asset, x, y,
      flip: !!opts.flip,
      scale: opts.scale || 1,
      phase: this._rnd() * 100,
      frame: 0,
      frameTime: 0,
      shadowless: opts.shadowless ?? (asset.shadow && asset.shadow.kind == null),
      emissiveScale: opts.emissiveScale ?? 1,
      label: opts.label || asset.name,
    };
    if (!o.shadowless) {
      const c = new ShadowCaster({
        name: `${asset.name}#${this.objects.length}`,
        x, y,
        worldHeight: asset.worldHeight,
        shadowKind: asset.shadow.kind,
        lengthScale: asset.shadow.length ?? 1,
        widthScale: asset.shadow.width ?? 1,
        strength: opts.strength ?? 1,
        castSunShadow: opts.castSunShadow !== false,
        castLightShadows: opts.castLightShadows !== false,
        shadowStyle: (asset.shadow && asset.shadow.style) || 'blob',
      });
      o.caster = c;
      c.object = o;                 // the shadow system resolves the live canvas
      this.casters.push(c);
    }
    this.objects.push(o);
    return o;
  }

  addLight(x, y, opts = {}) {
    const L = new Light(Object.assign({ x, y }, opts));
    this.lights.push(L);
    return L;
  }

  build() {
    const A = this.assets;
    const rnd = this._rnd;

    // ---- the path the keeper walks (also baked into the terrain texture) ----
    this.pathPoints = [
      { x: 0.02, y: 0.80 }, { x: 0.20, y: 0.72 }, { x: 0.38, y: 0.75 },
      { x: 0.56, y: 0.64 }, { x: 0.74, y: 0.56 }, { x: 0.97, y: 0.50 },
    ];
    this.pathY = (wx) => {
      const nx = clamp(wx / this.w);
      const pts = this.pathPoints;
      for (let i = 0; i < pts.length - 1; i++) {
        if (nx >= pts[i].x && nx <= pts[i + 1].x) {
          const t = (nx - pts[i].x) / (pts[i + 1].x - pts[i].x || 1);
          return lerp(pts[i].y, pts[i + 1].y, t) * this.h;
        }
      }
      return pts[pts.length - 1].y * this.h;
    };

    // ---- background trees (tall casters => long, dramatic shadows) ----
    this.add(A.pine, 46, 112, { scale: 1 });
    this.add(A.oak, 118, 102);
    this.add(A.deadTree, 196, 100);
    this.add(A.oak2, 258, 108, { flip: true });
    this.add(A.autumn, 430, 110);
    this.add(A.deadTree2, 462, 142);
    this.add(A.pine, 14, 152, { scale: 1 });
    this.add(A.oak2, 392, 98, { scale: 0.9 });

    // ---- the chapel (the biggest shadow caster in the scene) ----
    this.add(A.chapel, 336, 140, { label: 'chapel' });

    // ---- the oven: one of the article's dynamic light sources ----
    const oven = this.add(A.oven, 296, 158, { label: 'oven' });
    oven.light = this.addLight(296 + 11, 158 - 6, {
      z: 8, radius: 62, intensity: 1.25, color: [255, 138, 48], kind: 'oven', flicker: 0.55,
    });
    oven.light.owner = oven;

    // ---- lamp posts along the path ----
    this.lamps = [];
    const lampXs = [36, 128, 226, 322, 424];
    for (const lx of lampXs) {
      const ly = this.pathY(lx) - 16;
      const o = this.add(A.lampPost, lx, ly, { label: 'lamp' });
      o.light = this.addLight(lx + 4, ly - 16, {
        z: 19, radius: 66, intensity: 1.05, color: [255, 196, 116], kind: 'lamp', flicker: 0.10,
      });
      o.light.owner = o;
      this.lamps.push(o);
    }

    // ---- graves: a hand-placed ring plus a seeded scatter ----
    const graves = [
      [A.tombRound, 84, 138], [A.tombCross, 132, 152], [A.tombSlab, 168, 132],
      [A.tombRound, 232, 154], [A.tombCross, 268, 134], [A.mound, 196, 168],
      [A.tombSlab, 300, 200], [A.tombRound, 356, 182], [A.tombCross, 392, 160],
      [A.mound, 246, 202], [A.tombRound, 152, 210], [A.tombCross, 92, 198],
      [A.tombSlab, 44, 224], [A.mound, 420, 188], [A.tombRound, 452, 216],
      [A.tombCross, 344, 232], [A.mound, 286, 244], [A.tombSlab, 190, 248],
      [A.tombRound, 116, 252], [A.tombCross, 404, 248],
    ];
    for (const [asset, x, y] of graves) {
      this.add(asset, x + (rnd() - 0.5) * 6, y + (rnd() - 0.5) * 4, {
        strength: 0.9 + rnd() * 0.25,
      });
    }

    // ---- clutter ----
    this.add(A.barrel, 314, 172);
    this.add(A.crate, 326, 178, { flip: true });
    this.add(A.barrel, 322, 184, { scale: 1 });
    this.add(A.rock, 62, 176);
    this.add(A.rock, 214, 230);
    this.add(A.rock, 378, 212);
    this.add(A.bush, 158, 176);
    this.add(A.bush, 274, 120);
    this.add(A.bush, 438, 170);
    this.add(A.bush, 24, 198);

    // ---- fence run: the article's "stretched along one axis" problem child ----
    this.fenceStart = { x: 8, y: 240 };
    for (let i = 0; i < 9; i++) {
      this.add(A.fence, 8 + i * 14, 240 + Math.sin(i * 0.7) * 1.2, { label: 'fence' });
    }
    for (let i = 0; i < 5; i++) {
      this.add(A.fence, 402 + i * 14, 222 + i * 2.2, { label: 'fence' });
    }

    // ---- grass tufts (wind shader demo, no shadows) ----
    for (let i = 0; i < 46; i++) {
      const x = rnd() * this.w;
      const y = 92 + rnd() * (this.h - 102);
      this.add(A.grass, x, y, { shadowless: true });
    }

    // ---- the keeper ----
    this.keeper = this.add(A.keeper, 60, this.pathY(60) + 4, { label: 'keeper' });
    this.keeper.patrol = { t: 0, speed: 15, dir: 1, manual: 0, vx: 0, vy: 0 };
    this.keeper.light = this.addLight(this.keeper.x + 7, this.keeper.y - 9, {
      z: 11, radius: 58, intensity: 1.15, color: [255, 198, 124], kind: 'lantern', flicker: 0.3,
    });
    this.keeper.light.owner = this.keeper;

    // ---- a loose torch the player can drag around ----
    this.torch = this.addLight(180, 190, {
      z: 14, radius: 74, intensity: 1.3, color: [255, 168, 84], kind: 'torch', flicker: 0.65,
    });

    // ---- fireflies ----
    for (let i = 0; i < 26; i++) {
      this.fireflies.push({
        x: rnd() * this.w, y: 100 + rnd() * (this.h - 118),
        phase: rnd() * 100, speed: 0.4 + rnd() * 0.8, r: 6 + rnd() * 10,
      });
    }

    // depth-sort once (objects never change y except the keeper)
    this.sortObjects();
  }

  sortObjects() {
    this.objects.sort((a, b) => (a.y - b.y) || (a.x - b.x));
  }

  /* ------------------------------------------------------------------- update */

  update(dt, input = {}) {
    if (!this.paused) this.timeOfDay = (this.timeOfDay + dt * this.timeSpeed) % 1;
    this.time += dt;
    this.windTime += dt * (0.6 + this.windStrength);
    this.sun.update(this.timeOfDay);

    for (const L of this.lights) L.update(this.time);

    // ---- the keeper: patrol or manual ----
    const k = this.keeper;
    const p = k.patrol;
    if (input.moveX || input.moveY) {
      p.manual = 2.2;
      const sp = 42 * dt;
      const len = Math.hypot(input.moveX, input.moveY) || 1;
      k.x = clamp(k.x + (input.moveX / len) * sp, 6, this.w - 6);
      k.y = clamp(k.y + (input.moveY / len) * sp * 0.6, 60, this.h - 6);
      if (input.moveX) k.faceDir = input.moveX > 0 ? 1 : -1;
      p.moving = true;
    } else {
      p.manual = Math.max(0, p.manual - dt);
      if (p.manual <= 0) {
        p.t += dt * p.speed * p.dir;
        const span = this.w - 40;
        if (p.t > span) { p.t = span; p.dir = -1; }
        if (p.t < 0) { p.t = 0; p.dir = 1; }
        const nx = 20 + p.t;
        k.x = nx;
        k.y = this.pathY(nx) + 4 + Math.sin(p.t * 0.05) * 1.5;
        k.faceDir = p.dir;
        p.moving = true;
      } else p.moving = false;
    }
    // walk cycle
    if (p.moving) {
      k.frameTime += dt * 7.5;
      if (k.frameTime > 1) { k.frameTime = 0; k.frame = (k.frame + 1) % 3; }
    } else { k.frame = 0; k.frameTime = 0; }

    // lantern follows the keeper's hand
    if (k.light) {
      k.light.x = k.x + (k.faceDir > 0 ? 8 : -8);
      k.light.y = k.y - 9 + Math.sin(this.time * 7.5) * (p.moving ? 0.8 : 0.25);
    }

    // ---- camera follows the keeper with a dead zone ----
    const viewW = input.viewW || 320, viewH = input.viewH || 180;
    const targetX = clamp(k.x - viewW * 0.5, 0, Math.max(0, this.w - viewW));
    const targetY = clamp(k.y - viewH * 0.55, 0, Math.max(0, this.h - viewH));
    const s = 1 - Math.pow(0.0016, dt);
    this.camX = lerp(this.camX, targetX, s);
    this.camY = lerp(this.camY, targetY, s);
    if (input.snapCamera) { this.camX = targetX; this.camY = targetY; }

    // ---- fireflies drift ----
    for (const f of this.fireflies) {
      f.x += Math.cos(this.time * f.speed + f.phase) * dt * 3.2;
      f.y += Math.sin(this.time * f.speed * 1.3 + f.phase) * dt * 2.1;
      if (f.x < 0) f.x += this.w; if (f.x > this.w) f.x -= this.w;
      f.y = clamp(f.y, 70, this.h - 6);
    }

    // the keeper is the only moving caster: keep its shadow pivot in sync
    if (k.caster) { k.caster.x = k.x; k.caster.y = k.y; }
  }

  /* ------------------------------------------------------------------- helpers */

  /** Lights sorted by distance to a world point (used by the inspector). */
  nearestLights(x, y, n = 3) {
    return this.lights
      .filter((L) => L.enabled)
      .map((L) => ({ L, d: Math.hypot(L.x - x, (L.y - y) / 0.42) }))
      .sort((a, b) => a.d - b.d)
      .slice(0, n);
  }

  lightAt(x, y, radius = 8) {
    let best = null, bd = radius * radius;
    for (const L of this.lights) {
      const d = (L.x - x) ** 2 + (L.y - y) ** 2;
      if (d < bd) { bd = d; best = L; }
    }
    return best;
  }

  casterAt(x, y) {
    let best = null, bd = 14 * 14;
    for (const c of this.casters) {
      const d = (c.x - x) ** 2 + ((c.y - y) * 1.6) ** 2;
      if (d < bd) { bd = d; best = c; }
    }
    return best;
  }

  /** 0..1 how much daylight there is — drives lamp glow intensity. */
  get dayAmount() { return clamp(this.sun.intensity / 1.25); }

  get ambientLevel() { return clamp(this.sun.intensity * 0.6 + (this.sun.isMoon ? 0.05 : 0)); }

  /** The article's 3D light factor for a sprite at (x, y) and a light L. */
  facingFactor(L, x, y, rate) {
    return verticalFacingFactor(L.y - y, rate, 0.06);
  }

  groundNoise(x, y) { return valueNoise(x * 0.1, y * 0.1, 5); }
}
