/**
 * fog.js — "Object altitude simulation — for the fog to be displayed correctly."
 *
 *   "The fog consists of a great deal of horizontal clouds spread across all
 *    the picture. As a result the upper part of all the sprites is covered with
 *    a fewer amount of fog sprites."
 *
 * Implementation: N horizontal cloud bands, each pre-baked into a tileable
 * alpha texture and scrolled horizontally. Every band owns an *altitude
 * threshold*: it only fogs pixels whose altitude (how far above their own base
 * they are) is below that threshold. Because a band's threshold grows towards
 * the bottom of the screen, the tops of trees and houses are covered by fewer
 * bands than their feet — exactly the effect in the article's screenshot.
 */

import { clamp, lerp, smoothstep, hash2i } from './util.js?v=r6';

function tiledNoise(x, y, period, seed) {
  const xi = Math.floor(x), yi = Math.floor(y);
  const xf = x - xi, yf = y - yi;
  const u = xf * xf * (3 - 2 * xf), v = yf * yf * (3 - 2 * yf);
  const wx = ((xi % period) + period) % period;
  const wx1 = (wx + 1) % period;
  const a = hash2i(wx, yi, seed), b = hash2i(wx1, yi, seed);
  const c = hash2i(wx, yi + 1, seed), d = hash2i(wx1, yi + 1, seed);
  return lerp(lerp(a, b, u), lerp(c, d, u), v);
}

export class FogSystem {
  constructor(w, h, opts = {}) {
    this.w = w; this.h = h;
    this.layers = [];
    const count = opts.count ?? 7;
    const cell = 12;                       // pixels per noise texel
    for (let i = 0; i < count; i++) {
      const t = i / (count - 1);
      // bands are spread over the lower 2/3 of the picture, denser near camera
      const y = Math.round(lerp(h * 0.34, h * 1.02, Math.pow(t, 0.85)));
      const height = Math.round(lerp(h * 0.10, h * 0.20, t));
      const period = 16 + i * 4;
      const texW = period * cell;
      const tex = new Float32Array(texW * height);
      const seed = 91 + i * 17;
      for (let ty = 0; ty < height; ty++) {
        // vertical gradient: thicker towards the bottom of the band
        const vy = ty / Math.max(1, height - 1);
        const vert = smoothstep(1 - Math.abs(vy - 0.6) * 1.15) * (0.55 + 0.45 * vy);
        for (let tx = 0; tx < texW; tx++) {
          // second octave uses exactly half the cell size and double the period
          // so the sum still tiles over texW
          const n = tiledNoise(tx / cell, (ty + i * 31) / cell, period, seed) * 0.65
            + tiledNoise(tx / (cell / 2), (ty + i * 13) / (cell / 2), period * 2, seed + 5) * 0.35;
          let a = clamp((n - 0.30) / 0.48);
          a = Math.pow(a, 0.75) * vert;
          tex[ty * texW + tx] = a;
        }
      }
      this.layers.push({
        y, height, tex, texW,
        altitude: Math.round(lerp(14, 62, t)),   // what this band can swallow
        density: lerp(0.55, 1.0, t),
        speed: lerp(2.2, 7.5, t) * (i % 2 === 0 ? 1 : 0.82),
        offset: (i * 37) % texW,
        parallax: lerp(0.15, 0.6, t),
      });
    }
    this.amount = 0.45;
    this.color = [168, 178, 196];
  }

  update(dt, cameraX = 0) {
    for (const L of this.layers) L.offset = (L.offset + L.speed * dt) % L.texW;
    this.cameraX = cameraX;
  }

  /**
   * @param {FrameBuffer} fb        destination (already contains the scene)
   * @param {Uint8Array}  altBuf    per-pixel altitude above the object's base
   * @param {number}      amount    0..1 ("extreme 100% fog" slider)
   * @param {number[]}    color     fog colour, tinted by the ambient light
   */
  render(fb, altBuf, amount, color) {
    if (amount <= 0.001) return 0;
    const { w, h } = this;
    const d = fb.data;
    const [cr, cg, cb] = color;
    let touched = 0;
    for (let li = 0; li < this.layers.length; li++) {
      const L = this.layers[li];
      const y0 = Math.max(0, L.y);
      const y1 = Math.min(h - 1, L.y + L.height - 1);
      if (y1 < y0) continue;
      const camOff = Math.floor((this.cameraX || 0) * L.parallax);
      const base = Math.floor(L.offset) + camOff;
      const dens = L.density * amount;
      for (let y = y0; y <= y1; y++) {
        const ty = y - L.y;
        const rowTex = ty * L.texW;
        let p = (y * w) * 4;
        for (let x = 0; x < w; x++, p += 4) {
          const a = L.tex[rowTex + (((x + base) % L.texW) + L.texW) % L.texW] * dens;
          if (a <= 0.004) continue;
          // altitude test: tall sprites poke out of this band
          if (altBuf[y * w + x] >= L.altitude) continue;
          const na = a > 1 ? 1 : a;
          const ia = 1 - na;
          d[p] = d[p] * ia + cr * na;
          d[p + 1] = d[p + 1] * ia + cg * na;
          d[p + 2] = d[p + 2] * ia + cb * na;
          touched++;
        }
      }
    }
    return touched;
  }
}
