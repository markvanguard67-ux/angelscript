/**
 * pixelart.js — sprite authoring + the "merge painted light into a normal map"
 * script described in the article:
 *
 *   "These normals are pretty easy to draw. An artist roughly paints light on
 *    4 sides with a brush ... And then we merge them with a script in a normal
 *    map."
 *
 * Here the "paint" is the luminance the artist put into the sprite plus a
 * silhouette-curvature term; makeNormals() merges both into a tangent-space
 * normal map (RGB, z up).
 */

import { clamp, hexToRgb, shade } from './util.js';
import { bayer } from './framebuffer.js';

export class PixelCanvas {
  constructor(w, h) {
    this.w = w; this.h = h;
    this.data = new Uint8ClampedArray(w * h * 4);
    this.heights = new Float32Array(w * h);   // 0..1 authoring height
    this.normals = null;                      // Uint8Array w*h*3, built lazily
    this.emissive = null;                     // Uint8Array w*h mask, lazily created
    this.emissiveSeed = null;
  }

  /** Mark a pixel as self-lit (oven mouth, chapel window): shadows skip it. */
  markEmissive(x, y) {
    if (x < 0 || y < 0 || x >= this.w || y >= this.h) return;
    if (!this.emissive) this.emissive = new Uint8Array(this.w * this.h);
    this.emissive[y * this.w + x] = 1;
  }

  idx(x, y) { return (y * this.w + x) * 4; }

  set(x, y, r, g, b, a = 255, h = 0.5) {
    x |= 0; y |= 0;
    if (x < 0 || y < 0 || x >= this.w || y >= this.h) return;
    const i = (y * this.w + x) * 4;
    this.data[i] = r; this.data[i + 1] = g; this.data[i + 2] = b; this.data[i + 3] = a;
    if (a > 0) this.heights[y * this.w + x] = h;
  }

  alpha(x, y) {
    if (x < 0 || y < 0 || x >= this.w || y >= this.h) return 0;
    return this.data[(y * this.w + x) * 4 + 3];
  }

  /* --- drawing primitives (same shapes as FrameBuffer, but with heights) --- */

  setPixel(x, y, r, g, b, a = 255, h = 0.5) { this.set(x, y, r, g, b, a, h); }

  hline(x0, x1, y, r, g, b, a = 255, h = 0.5) {
    for (let x = Math.min(x0, x1); x <= Math.max(x0, x1); x++) this.set(x, y, r, g, b, a, h);
  }

  vline(x, y0, y1, r, g, b, a = 255, h = 0.5) {
    for (let y = Math.min(y0, y1); y <= Math.max(y0, y1); y++) this.set(x, y, r, g, b, a, h);
  }

  rect(x, y, w, h2, r, g, b, a = 255, h = 0.5) {
    for (let j = 0; j < h2; j++) for (let i = 0; i < w; i++) this.set(x + i, y + j, r, g, b, a, h);
  }

  /** Bresenham line with a square brush. */
  line(x0, y0, x1, y1, r, g, b, a = 255, thick = 1, h = 0.5) {
    x0 = Math.round(x0); y0 = Math.round(y0); x1 = Math.round(x1); y1 = Math.round(y1);
    const dx = Math.abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
    const dy = -Math.abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
    let err = dx + dy;
    const off = thick >> 1;
    for (;;) {
      if (thick <= 1) this.set(x0, y0, r, g, b, a, h);
      else {
        for (let j = -off; j <= thick - 1 - off; j++) {
          for (let i = -off; i <= thick - 1 - off; i++) this.set(x0 + i, y0 + j, r, g, b, a, h);
        }
      }
      if (x0 === x1 && y0 === y1) break;
      const e2 = 2 * err;
      if (e2 >= dy) { err += dy; x0 += sx; }
      if (e2 <= dx) { err += dx; y0 += sy; }
    }
  }

  /** Dithered filled disc. */
  disc(cx, cy, radius, r, g, b, a = 255, h = 0.5, dither = 0) {
    const R = Math.ceil(radius) + 1;
    for (let y = -R; y <= R; y++) {
      for (let x = -R; x <= R; x++) {
        const d = Math.hypot(x, y);
        const t = radius - d;
        if (t <= -dither) continue;
        if (t < dither && bayer(cx + x, cy + y) > (t + dither) / (2 * dither)) continue;
        this.set(cx + x, cy + y, r, g, b, a, h * (1 - d / (radius + 1) * 0.4));
      }
    }
  }

  get(x, y) {
    if (x < 0 || y < 0 || x >= this.w || y >= this.h) return null;
    const i = (y * this.w + x) * 4;
    return [this.data[i], this.data[i + 1], this.data[i + 2], this.data[i + 3]];
  }

  /** Chamfer distance transform: distance (in px) to the nearest empty pixel. */
  distanceToEdge(maxDist = 8) {
    const { w, h } = this;
    const d = new Float32Array(w * h).fill(1e9);
    for (let y = 0; y < h; y++) {
      for (let x = 0; x < w; x++) if (this.alpha(x, y) === 0) d[y * w + x] = 0;
    }
    const pass = (x0, y0, x1, y1, sx, sy, offs) => {
      for (let y = y0; y !== y1; y += sy) {
        for (let x = x0; x !== x1; x += sx) {
          const i = y * w + x;
          let v = d[i];
          for (const [ox, oy, c] of offs) {
            const nx = x + ox, ny = y + oy;
            if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
            const cand = d[ny * w + nx] + c;
            if (cand < v) v = cand;
          }
          d[i] = v;
        }
      }
    };
    const D = 1, G = 1.4142;
    pass(0, 0, w, h, 1, 1, [[-1, 0, D], [0, -1, D], [-1, -1, G], [1, -1, G]]);
    pass(w - 1, h - 1, -1, -1, -1, -1, [[1, 0, D], [0, 1, D], [1, 1, G], [-1, 1, G]]);
    for (let i = 0; i < d.length; i++) if (d[i] > maxDist) d[i] = maxDist;
    return d;
  }

  /**
   * Merge the painted shading + silhouette curvature into a normal map.
   * mode: 'merged' (default, what the article describes), 'painted', 'silhouette'
   */
  makeNormals(mode = 'merged', strength = 1.6, silhouetteStrength = 1.0) {
    const { w, h, data, heights } = this;
    const edge = this.distanceToEdge(6);
    const lum = new Float32Array(w * h);
    for (let p = 0; p < w * h; p++) {
      const i = p * 4;
      lum[p] = (data[i] * 0.35 + data[i + 1] * 0.5 + data[i + 2] * 0.15) / 255;
    }
    // height field: painted luminance + curvature from the distance transform
    const hgt = new Float32Array(w * h);
    for (let y = 0; y < h; y++) {
      for (let x = 0; x < w; x++) {
        const p = y * w + x;
        if (data[p * 4 + 3] === 0) { hgt[p] = 0; continue; }
        const painted = lum[p];
        const curve = Math.min(1, edge[p] / 3.2);           // rounder in the middle
        const authored = heights[p];
        let v;
        if (mode === 'painted') v = painted;
        else if (mode === 'silhouette') v = curve * 0.7 + authored * 0.3;
        else v = painted * 0.45 + curve * 0.35 + authored * 0.2;
        hgt[p] = v;
      }
    }
    const nrm = new Uint8Array(w * h * 3);
    for (let y = 0; y < h; y++) {
      for (let x = 0; x < w; x++) {
        const p = y * w + x;
        if (data[p * 4 + 3] === 0) { nrm[p * 3] = 128; nrm[p * 3 + 1] = 128; nrm[p * 3 + 2] = 255; continue; }
        const l = hgt[y * w + Math.max(0, x - 1)], r = hgt[y * w + Math.min(w - 1, x + 1)];
        const u = hgt[Math.max(0, y - 1) * w + x], d = hgt[Math.min(h - 1, y + 1) * w + x];
        // screen +y is down; a normal pointing "up the screen" is -y in tangent space
        let nx = (l - r) * strength;
        let ny = (d - u) * strength * 0.85;
        let nz = 1 / Math.max(0.35, silhouetteStrength);
        const inv = 1 / Math.sqrt(nx * nx + ny * ny + nz * nz);
        nrm[p * 3] = (nx * inv * 0.5 + 0.5) * 255;
        nrm[p * 3 + 1] = (ny * inv * 0.5 + 0.5) * 255;
        nrm[p * 3 + 2] = (nz * inv * 0.5 + 0.5) * 255;
      }
    }
    this.normals = nrm;
    return nrm;
  }

  /** Add a 1px outline around the silhouette (useful for procedural art). */
  autoOutline(rgb, includeDiagonals = true) {
    const { w, h } = this;
    const add = [];
    const nb = includeDiagonals
      ? [[1, 0], [-1, 0], [0, 1], [0, -1], [1, 1], [1, -1], [-1, 1], [-1, -1]]
      : [[1, 0], [-1, 0], [0, 1], [0, -1]];
    for (let y = 0; y < h; y++) {
      for (let x = 0; x < w; x++) {
        if (this.alpha(x, y) > 0) continue;
        for (const [ox, oy] of nb) {
          if (this.alpha(x + ox, y + oy) > 0) { add.push([x, y]); break; }
        }
      }
    }
    for (const [x, y] of add) this.set(x, y, rgb[0], rgb[1], rgb[2], 255, 0.9);
    return this;
  }
}

/* ------------------------------------------------------------ string art DSL */

/**
 * Author a sprite from ASCII rows.
 *   palette: { key: '#rrggbb' | ['#rrggbb', height] | null (transparent) }
 * Rows must all have the same length (validated — silent typos are the worst
 * kind of pixel-art bug).
 */
export function spriteFromRows(name, palette, rows, extra = {}) {
  const h = rows.length;
  const w = rows[0].length;
  for (let i = 0; i < h; i++) {
    if (rows[i].length !== w) {
      throw new Error(`sprite "${name}": row ${i} is ${rows[i].length}px wide, expected ${w}px`);
    }
  }
  const pc = new PixelCanvas(w, h);
  const resolved = {};
  for (const k of Object.keys(palette)) {
    const v = palette[k];
    if (v == null) { resolved[k] = null; continue; }
    resolved[k] = Array.isArray(v) ? [hexToRgb(v[0]), v[1]] : [hexToRgb(v), 0.5];
  }
  for (let y = 0; y < h; y++) {
    for (let x = 0; x < w; x++) {
      const key = rows[y][x];
      if (key === '.' || key === ' ') continue;
      const e = resolved[key];
      if (!e) {
        if (key !== '.') console.warn(`sprite "${name}": unknown palette key "${key}" at ${x},${y}`);
        continue;
      }
      pc.set(x, y, e[0][0], e[0][1], e[0][2], 255, e[1]);
    }
  }
  pc.name = name;
  Object.assign(pc, extra);
  return pc;
}

/** Procedural radial glow sprite (additive lamp halos, fireflies). */
export function makeGlowSprite(size, coreRgb, midRgb, power = 2.2) {
  const pc = new PixelCanvas(size, size);
  const c = (size - 1) / 2;
  for (let y = 0; y < size; y++) {
    for (let x = 0; x < size; x++) {
      const d = Math.hypot(x - c, y - c) / (c + 0.5);
      let a = Math.pow(clamp(1 - d), power);
      if (a <= 0.01) continue;
      // dither the outer falloff so the halo reads as pixel art
      if (a < 0.35 && bayer(x, y) > a / 0.35) continue;
      const t = clamp(1 - d * 1.2);
      const r = coreRgb[0] * t + midRgb[0] * (1 - t);
      const g = coreRgb[1] * t + midRgb[1] * (1 - t);
      const b = coreRgb[2] * t + midRgb[2] * (1 - t);
      pc.set(x, y, r, g, b, Math.round(a * 255), 0.5);
    }
  }
  pc.name = 'glow';
  return pc;
}

/**
 * Procedural shadow sprite. The article: "we used about ten different more or
 * less universal sprites (thin, thick, oval etc.)" and "the shadow sprite
 * original looks like a circle" (which is then heavily squashed vertically).
 *
 * All shadow sprites are authored pointing NORTH (away from the camera) with
 * the pivot at the bottom-centre of the canvas — the vertex stage rotates them
 * around that pivot.
 */
export function makeShadowSprite(kind, opts = {}) {
  const size = opts.size || 32;
  const w = opts.w || size;
  const h = opts.h || size;
  const pc = new PixelCanvas(w, h);
  const px = (w - 1) / 2;     // pivot x (bottom-centre)
  const py = h - 1;           // pivot y
  const dither = opts.dither !== false;

  // normalised local coords: u in [-0.5, 0.5] across, v in [0,1] up from pivot
  // Ordered-dither quantisation that *preserves the mean alpha*: the shadow
  // keeps its silhouette at any size while staying crunchy.
  const put = (x, y, coverage) => {
    let a = clamp(coverage);
    if (a <= 0) return;
    if (dither && a < 0.999) {
      const levels = 5;
      const f = a * (levels - 1);
      const lo = Math.floor(f);
      a = (lo + (bayer(x, y) < f - lo ? 1 : 0)) / (levels - 1);
    }
    if (a <= 0.001) return;
    pc.set(x, y, 0, 0, 0, Math.round(a * 255), 0);
  };

  const rx = opts.rx ?? 0.42;   // half width  (fraction of w)
  const ry = opts.ry ?? 0.9;    // length      (fraction of h)
  const soft = opts.soft ?? 0.26;
  const shape = opts.shape || 'ellipse';

  // `edge` is a normalised distance field: 0 at the spine of the shadow,
  // 1 at its silhouette. Coverage stays fully opaque until (1 - soft), which
  // keeps the blob chunky instead of a soft cone.
  for (let y = 0; y < h; y++) {
    for (let x = 0; x < w; x++) {
      const u = (x - px) / w;           // -0.5..0.5 across
      const v = (py - y) / h;           // 0 at the pivot .. ~1 at the far end
      if (v < -0.08) continue;
      let edge;
      if (shape === 'ellipse') {
        // teardrop: narrow at the pivot, wide in the middle, rounded tip
        const along = clamp((v + 0.02) / ry, 0, 1.6);
        const widthProfile = along <= 1
          ? Math.sin(Math.PI * Math.min(1, along * 0.86 + 0.14)) ** 0.5
          : 0;
        const halfW = Math.max(0.02, rx * widthProfile * (1 + (opts.taper || 0) * (1 - along)));
        edge = Math.max(Math.abs(u) / halfW, along > 1 ? (along - 1) * 4 : 0);
      } else if (shape === 'bar') {
        const along = clamp(v / ry, 0, 1.3);
        edge = Math.max(Math.abs(u) / rx, along > 1 ? (along - 1) * 5 : 0);
      } else if (shape === 'blob') {
        // a circle — "the shadow sprite original looks like a circle"
        const cy = ry * 0.55;
        edge = Math.hypot(u / rx, (v - cy) / (ry * 0.55));
      } else if (shape === 'trunk') {
        // tree: narrow trunk near the pivot + a big crown blob at the far end
        const body = Math.abs(u) / Math.max(0.02, rx * 0.3);
        const cy = ry * 0.78;
        const crown = Math.hypot(u / Math.max(0.02, rx * 1.05), (v - cy) / (ry * 0.42));
        edge = Math.min(body, crown);
      } else if (shape === 'human') {
        const body = Math.abs(u) / Math.max(0.02, rx * 0.55);
        const cy = ry * 0.8;
        const head = Math.hypot(u / Math.max(0.02, rx * 0.45), (v - cy) / (ry * 0.2));
        edge = Math.min(body, head);
      } else {
        edge = 2;
      }
      const a = 1 - clamp((edge - (1 - soft)) / soft);
      put(x, y, a);
    }
  }
  pc.name = `shadow_${kind}`;
  pc.pivot = { x: px, y: py };
  pc.shadowKind = kind;
  return pc;
}

export const SHADOW_PRESETS = {
  blob:   { shape: 'blob',    w: 20, h: 20, rx: 0.46, ry: 0.82, soft: 0.34 },
  oval:   { shape: 'ellipse', w: 18, h: 30, rx: 0.42, ry: 0.92, soft: 0.26 },
  thin:   { shape: 'ellipse', w: 12, h: 34, rx: 0.30, ry: 0.95, soft: 0.24 },
  thick:  { shape: 'ellipse', w: 26, h: 26, rx: 0.48, ry: 0.86, soft: 0.34 },
  tree:   { shape: 'trunk',   w: 26, h: 40, rx: 0.40, ry: 0.95, soft: 0.3 },
  human:  { shape: 'human',   w: 14, h: 26, rx: 0.36, ry: 0.92, soft: 0.26 },
  fence:  { shape: 'bar',     w: 10, h: 44, rx: 0.46, ry: 0.96, soft: 0.24 },
  chapel: { shape: 'ellipse', w: 40, h: 44, rx: 0.46, ry: 0.94, soft: 0.4 },
  mound:  { shape: 'ellipse', w: 20, h: 16, rx: 0.48, ry: 0.82, soft: 0.4 },
};

export function makeShadowSet() {
  const set = {};
  for (const [kind, opts] of Object.entries(SHADOW_PRESETS)) set[kind] = makeShadowSprite(kind, opts);
  return set;
}
