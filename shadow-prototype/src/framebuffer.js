/**
 * framebuffer.js — a tiny CPU "render target" standing in for the Unity
 * framebuffers / sprite batches of the original implementation.
 *
 * Everything the prototype draws goes through this class so that we can time
 * the individual passes (ground, shadows, sprites, fog, LUT) and show them in
 * the HUD, exactly like a real render profiler would.
 */

import { clamp, hexToRgb } from './util.js?v=r5';

export class FrameBuffer {
  constructor(w, h, name = 'fb') {
    this.w = w; this.h = h; this.name = name;
    this.image = null;   // created lazily so the class also works head-less (node)
    this.data = new Uint8ClampedArray(w * h * 4);
    this.canvas = null;
    this.ctx = null;
    this.dirty = true;
  }

  /** Render straight into an existing canvas element (used for the visible view). */
  attachCanvas(canvas) {
    canvas.width = this.w;
    canvas.height = this.h;
    this.canvas = canvas;
    this.ctx = canvas.getContext('2d', { alpha: false });
    try {
      this.image = new ImageData(this.data, this.w, this.h);
    } catch (e) {
      this.image = this.ctx.createImageData(this.w, this.h);
      this.image.data.set(this.data);
      this.data = this.image.data;
    }
    return canvas;
  }

  /** Lazily attach a real canvas (browser only). */
  ensureCanvas() {
    if (this.canvas) return this.canvas;
    if (typeof document === 'undefined') return null;
    const canvas = document.createElement('canvas');
    canvas.width = this.w; canvas.height = this.h;
    this.canvas = canvas;
    this.ctx = canvas.getContext('2d', { alpha: true });
    // wrap the very same pixels — putImageData then needs no copy at all
    try {
      this.image = new ImageData(this.data, this.w, this.h);
    } catch (e) {
      this.image = this.ctx.createImageData(this.w, this.h);
      this.image.data.set(this.data);
      this.data = this.image.data;
    }
    return canvas;
  }

  /** Upload the CPU buffer into the canvas texture. Call once per frame. */
  present() {
    const c = this.ensureCanvas();
    if (!c) return null;
    if (this.image.data !== this.data) this.image.data.set(this.data);
    this.ctx.putImageData(this.image, 0, 0);
    this.dirty = false;
    return c;
  }

  clear(r = 0, g = 0, b = 0, a = 0) {
    const d = this.data;
    for (let i = 0; i < d.length; i += 4) { d[i] = r; d[i + 1] = g; d[i + 2] = b; d[i + 3] = a; }
    this.dirty = true;
  }

  clearCss(css, a = 255) { const [r, g, b] = hexToRgb(css); this.clear(r, g, b, a); }

  get(x, y) {
    if (x < 0 || y < 0 || x >= this.w || y >= this.h) return [0, 0, 0, 0];
    const i = (y * this.w + x) * 4, d = this.data;
    return [d[i], d[i + 1], d[i + 2], d[i + 3]];
  }

  setPixel(x, y, r, g, b, a = 255) {
    x |= 0; y |= 0;
    if (x < 0 || y < 0 || x >= this.w || y >= this.h) return;
    const i = (y * this.w + x) * 4, d = this.data;
    d[i] = r; d[i + 1] = g; d[i + 2] = b; d[i + 3] = a;
    this.dirty = true;
  }

  /** Source-over blend. */
  blend(x, y, r, g, b, a) {
    x |= 0; y |= 0;
    if (a <= 0 || x < 0 || y < 0 || x >= this.w || y >= this.h) return;
    const i = (y * this.w + x) * 4, d = this.data;
    if (a >= 255) { d[i] = r; d[i + 1] = g; d[i + 2] = b; d[i + 3] = 255; return; }
    const na = a / 255, ia = 1 - na;
    d[i] = d[i] * ia + r * na;
    d[i + 1] = d[i + 1] * ia + g * na;
    d[i + 2] = d[i + 2] * ia + b * na;
    d[i + 3] = Math.min(255, d[i + 3] + a * (1 - d[i + 3] / 255));
    this.dirty = true;
  }

  /** Additive blend — used for lamp glows, fireflies and light gizmos. */
  add(x, y, r, g, b, a) {
    x |= 0; y |= 0;
    if (a <= 0 || x < 0 || y < 0 || x >= this.w || y >= this.h) return;
    const i = (y * this.w + x) * 4, d = this.data, k = a / 255;
    d[i] = Math.min(255, d[i] + r * k);
    d[i + 1] = Math.min(255, d[i + 1] + g * k);
    d[i + 2] = Math.min(255, d[i + 2] + b * k);
    d[i + 3] = Math.max(d[i + 3], Math.min(255, a));
    this.dirty = true;
  }

  /**
   * Blit another framebuffer with source-over. Both buffers must be RGBA.
   * dx/dy may be fractional (integer pixel art => truncated).
   */
  blit(src, dx, dy, alpha = 1, srcX = 0, srcY = 0, sw = src.w, sh = src.h) {
    dx |= 0; dy |= 0;
    const d = this.data, s = src.data, W = this.w, H = this.h, SW = src.w;
    const x0 = Math.max(0, dx), x1 = Math.min(W, dx + sw);
    const y0 = Math.max(0, dy), y1 = Math.min(H, dy + sh);
    for (let y = y0; y < y1; y++) {
      const sy = (y - dy + srcY) * SW;
      let di = (y * W + x0) * 4;
      for (let x = x0; x < x1; x++, di += 4) {
        const si = (sy + (x - dx + srcX)) * 4;
        const sa = s[si + 3] * alpha;
        if (sa <= 0) continue;
        if (sa >= 255) { d[di] = s[si]; d[di + 1] = s[si + 1]; d[di + 2] = s[si + 2]; d[di + 3] = 255; continue; }
        const na = sa / 255, ia = 1 - na;
        d[di] = d[di] * ia + s[si] * na;
        d[di + 1] = d[di + 1] * ia + s[si + 1] * na;
        d[di + 2] = d[di + 2] * ia + s[si + 2] * na;
        d[di + 3] = Math.min(255, d[di + 3] + sa);
      }
    }
    this.dirty = true;
  }

  /** Multiply-blit: dst *= src — used to apply the accumulated shadow mask. */
  multiplyBlit(src, alpha = 1, dx = 0, dy = 0) {
    const d = this.data, s = src.data, W = this.w, H = this.h;
    const x0 = Math.max(0, dx), x1 = Math.min(W, dx + src.w);
    const y0 = Math.max(0, dy), y1 = Math.min(H, dy + src.h);
    for (let y = y0; y < y1; y++) {
      let di = (y * W + x0) * 4;
      const sy = (y - dy) * src.w;
      for (let x = x0; x < x1; x++, di += 4) {
        const si = (sy + (x - dx)) * 4;
        const a = (s[si + 3] / 255) * alpha;
        if (a <= 0) continue;
        const k = 1 - a;
        d[di] = d[di] * (k + (s[si] / 255) * a);
        d[di + 1] = d[di + 1] * (k + (s[si + 1] / 255) * a);
        d[di + 2] = d[di + 2] * (k + (s[si + 2] / 255) * a);
      }
    }
    this.dirty = true;
  }

  hline(x0, x1, y, r, g, b, a = 255) {
    for (let x = x0; x <= x1; x++) this.setPixel(x, y, r, g, b, a);
  }

  vline(x, y0, y1, r, g, b, a = 255) {
    for (let y = y0; y <= y1; y++) this.setPixel(x, y, r, g, b, a);
  }

  rect(x, y, w, h, r, g, b, a = 255) {
    for (let j = 0; j < h; j++) for (let i = 0; i < w; i++) this.setPixel(x + i, y + j, r, g, b, a);
  }

  rectOutline(x, y, w, h, r, g, b, a = 255) {
    this.hline(x, x + w - 1, y, r, g, b, a);
    this.hline(x, x + w - 1, y + h - 1, r, g, b, a);
    this.vline(x, y, y + h - 1, r, g, b, a);
    this.vline(x + w - 1, y, y + h - 1, r, g, b, a);
  }

  /** Bresenham line with a square brush. */
  line(x0, y0, x1, y1, r, g, b, a = 255, thick = 1) {
    x0 = Math.round(x0); y0 = Math.round(y0); x1 = Math.round(x1); y1 = Math.round(y1);
    const dx = Math.abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
    const dy = -Math.abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
    let err = dx + dy;
    const h = thick >> 1;
    for (;;) {
      if (thick <= 1) this.setPixel(x0, y0, r, g, b, a);
      else for (let j = -h; j <= h; j++) for (let i = -h; i <= h; i++) this.setPixel(x0 + i, y0 + j, r, g, b, a);
      if (x0 === x1 && y0 === y1) break;
      const e2 = 2 * err;
      if (e2 >= dy) { err += dy; x0 += sx; }
      if (e2 <= dx) { err += dx; y0 += sy; }
    }
  }

  /**
   * Filled disc. `edge` > 0 enables an ordered-dither (bayer 4x4) rim so the
   * silhouette keeps a hand-drawn pixel-art feel instead of a smooth curve.
   */
  disc(cx, cy, radius, r, g, b, a = 255, edge = 0, noiseFn = null) {
    const R = Math.ceil(radius) + 1;
    for (let y = -R; y <= R; y++) {
      for (let x = -R; x <= R; x++) {
        const d = Math.sqrt(x * x + y * y);
        let t = radius - d;
        if (noiseFn) t += noiseFn(cx + x, cy + y) * edge;
        if (t <= -edge) continue;
        if (t < edge) {
          const th = BAYER4[(y & 3) * 4 + (x & 3)] / 16;
          if ((t + edge) / (2 * edge) < th) continue;
        }
        this.setPixel(cx + x, cy + y, r, g, b, a);
      }
    }
  }

  ellipse(cx, cy, rx, ry, r, g, b, a = 255, edge = 0) {
    const RX = Math.ceil(rx) + 1, RY = Math.ceil(ry) + 1;
    for (let y = -RY; y <= RY; y++) {
      for (let x = -RX; x <= RX; x++) {
        const d = Math.sqrt((x / rx) ** 2 + (y / ry) ** 2) * Math.min(rx, ry);
        let t = Math.min(rx, ry) - d;
        if (t <= -edge) continue;
        if (t < edge) {
          const th = BAYER4[(y & 3) * 4 + (x & 3)] / 16;
          if ((t + edge) / (2 * edge) < th) continue;
        }
        this.setPixel(cx + x, cy + y, r, g, b, a);
      }
    }
  }
}

/** 4x4 bayer matrix (values 0..15) for ordered dithering. */
export const BAYER4 = new Uint8Array([
  0, 8, 2, 10,
  12, 4, 14, 6,
  3, 11, 1, 9,
  15, 7, 13, 5,
]);

export const bayer = (x, y) => BAYER4[(y & 3) * 4 + (x & 3)] / 16;

export const clampByte = (v) => (v < 0 ? 0 : v > 255 ? 255 : v);
export const clamp01 = clamp;
