/**
 * terrain.js — the pre-baked ground albedo (grass, dirt, a path, pebbles).
 * It is generated once at world size; per frame the renderer only multiplies it
 * by (ambient + dynamic light) * shadow mask.
 */

import { clamp, valueNoise, fbm, mulberry32 } from './util.js';

export function buildTerrain(w, h, seed = 1337, opts = {}) {
  const albedo = new Uint8Array(w * h * 3);
  const detail = new Uint8Array(w * h);      // 0..255, small variation for the light pass
  const rnd = mulberry32(seed);

  const grassA = [76, 106, 56];
  const grassB = [60, 88, 46];
  const grassC = [98, 130, 66];
  const dirtA = [108, 86, 62];
  const dirtB = [86, 68, 50];
  const pathA = [132, 112, 86];
  const pathB = [112, 96, 74];

  // a winding path (used by the keeper's patrol too)
  const pathPoints = opts.path || [
    { x: 0.02, y: 0.78 }, { x: 0.22, y: 0.70 }, { x: 0.42, y: 0.74 },
    { x: 0.60, y: 0.62 }, { x: 0.78, y: 0.55 }, { x: 0.97, y: 0.48 },
  ];
  const pathAt = (nx) => {
    // piecewise-linear interpolation of the path centre, in normalised coords
    for (let i = 0; i < pathPoints.length - 1; i++) {
      const a = pathPoints[i], b = pathPoints[i + 1];
      if (nx >= a.x && nx <= b.x) {
        const t = (nx - a.x) / (b.x - a.x || 1);
        return a.y + (b.y - a.y) * t;
      }
    }
    return pathPoints[pathPoints.length - 1].y;
  };

  for (let y = 0; y < h; y++) {
    for (let x = 0; x < w; x++) {
      const i = (y * w + x) * 3;
      const nx = x / w, ny = y / h;
      // large-scale biome blend: more dirt near the top (far away / trampled)
      const biome = clamp(fbm(x * 0.024, y * 0.024, 3, seed) * 0.34 + 0.5 + (0.5 - ny) * 0.26);
      // path mask
      const py = pathAt(clamp(nx)) * h;
      const pd = Math.abs(y - py);
      const pathW = 13 + valueNoise(x * 0.05, 3.3, seed + 9) * 3;
      const path = clamp(1 - pd / pathW);
      const pathMask = path > 0.4 ? clamp((path - 0.4) / 0.6) * 0.9 : 0;

      const n1 = fbm(x * 0.16, y * 0.16, 2, seed + 3) * 0.5 + 0.5;
      const n2 = valueNoise(x * 0.9, y * 0.9, seed + 11) * 0.5 + 0.5;

      let c;
      if (biome > 0.58) c = n1 > 0.55 ? grassC : grassA;
      else if (biome > 0.42) c = n1 > 0.5 ? grassA : grassB;
      else c = n1 > 0.5 ? dirtA : dirtB;

      let r = c[0], g = c[1], b = c[2];
      // path blend
      if (pathMask > 0) {
        const pc = n2 > 0.62 ? pathA : pathB;
        r = r + (pc[0] - r) * pathMask;
        g = g + (pc[1] - g) * pathMask;
        b = b + (pc[2] - b) * pathMask;
      }
      // per-pixel dither so the ground reads as pixel art once lit
      const dth = (n2 - 0.5) * 26 + (valueNoise(x * 2.7, y * 2.7, seed + 31) * 0.5) * 14;
      r = clamp(r + dth, 0, 255); g = clamp(g + dth * 1.05, 0, 255); b = clamp(b + dth * 0.9, 0, 255);

      albedo[i] = r; albedo[i + 1] = g; albedo[i + 2] = b;
      detail[y * w + x] = clamp(60 + n2 * 120, 0, 255);
    }
  }

  // scattered pebbles + tiny grass blades
  const count = Math.floor(w * h * 0.004);
  for (let k = 0; k < count; k++) {
    const x = Math.floor(rnd() * w), y = Math.floor(rnd() * h);
    const kind = rnd();
    const i = (y * w + x) * 3;
    if (kind < 0.4) {
      albedo[i] = clamp(albedo[i] + 34, 0, 255); albedo[i + 1] = clamp(albedo[i + 1] + 32, 0, 255);
      albedo[i + 2] = clamp(albedo[i + 2] + 38, 0, 255);
    } else if (kind < 0.75) {
      albedo[i] = clamp(albedo[i] * 0.72, 0, 255); albedo[i + 1] = clamp(albedo[i + 1] * 0.8, 0, 255);
      albedo[i + 2] = clamp(albedo[i + 2] * 0.7, 0, 255);
    } else {
      albedo[i] = clamp(albedo[i] * 0.8 + 12, 0, 255); albedo[i + 1] = clamp(albedo[i + 1] * 1.18 + 8, 0, 255);
      albedo[i + 2] = clamp(albedo[i + 2] * 0.8, 0, 255);
    }
  }

  return { w, h, albedo, detail, pathAt };
}
