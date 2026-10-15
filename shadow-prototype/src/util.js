/**
 * util.js — math, PRNG, noise, gradients, colour helpers and the LUT format.
 *
 * The LUT layout used here is exactly the one described in the article:
 *   "for convenience that 3D texture is represented as two-dimensional"
 *   -> a 32x32x32 RGB cube flattened into a 1024x32 strip (32 slices of 32x32,
 *      slice = blue, row = green, column = red).
 */

export const clamp = (v, a = 0, b = 1) => (v < a ? a : v > b ? b : v);
export const lerp = (a, b, t) => a + (b - a) * t;
export const smoothstep = (t) => { const x = clamp(t); return x * x * (3 - 2 * x); };
export const mix = (a, b, t) => lerp(a, b, t);

export function invLerp(a, b, v) { return b === a ? 0 : clamp((v - a) / (b - a)); }

export function hash2i(x, y, seed = 1) {
  let h = (Math.imul(x | 0, 374761393) + Math.imul(y | 0, 668265263) + Math.imul(seed | 0, 2147483647)) | 0;
  h = Math.imul(h ^ (h >>> 13), 1274126177);
  h ^= h >>> 16;
  return (h >>> 0) / 4294967296;
}

/** Deterministic value noise in [-1, 1]. Good enough for ground / leaf texture. */
export function valueNoise(x, y, seed = 1) {
  const xi = Math.floor(x), yi = Math.floor(y);
  const xf = x - xi, yf = y - yi;
  const u = xf * xf * (3 - 2 * xf), v = yf * yf * (3 - 2 * yf);
  const a = hash2i(xi, yi, seed), b = hash2i(xi + 1, yi, seed);
  const c = hash2i(xi, yi + 1, seed), d = hash2i(xi + 1, yi + 1, seed);
  return (lerp(lerp(a, b, u), lerp(c, d, u), v) * 2 - 1);
}

export function fbm(x, y, octaves = 3, seed = 1) {
  let amp = 0.5, freq = 1, sum = 0, norm = 0;
  for (let i = 0; i < octaves; i++) {
    sum += amp * valueNoise(x * freq, y * freq, seed + i * 37);
    norm += amp; amp *= 0.5; freq *= 2;
  }
  return sum / norm;
}

/** mulberry32 — small seeded PRNG so every generated sprite is reproducible. */
export function mulberry32(a) {
  return function () {
    a |= 0; a = (a + 0x6D2B79F5) | 0;
    let t = Math.imul(a ^ (a >>> 15), 1 | a);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

/* ------------------------------------------------------------------ colours */

/** css string -> [r,g,b] in 0..255 */
export function hexToRgb(hex) {
  const h = hex.replace('#', '');
  const n = parseInt(h.length === 3 ? h.split('').map((c) => c + c).join('') : h, 16);
  return [(n >> 16) & 255, (n >> 8) & 255, n & 255];
}

export function rgbToCss(r, g, b) {
  return `rgb(${r | 0},${g | 0},${b | 0})`;
}

export function mixRgb(a, b, t) {
  return [lerp(a[0], b[0], t), lerp(a[1], b[1], t), lerp(a[2], b[2], t)];
}

/** Shift a colour lighter (amt > 0) or darker (amt < 0). amt in -1..1. */
export function shade(rgb, amt) {
  if (amt >= 0) return [
    rgb[0] + (255 - rgb[0]) * amt,
    rgb[1] + (255 - rgb[1]) * amt,
    rgb[2] + (255 - rgb[2]) * amt,
  ];
  const k = 1 + amt;
  return [rgb[0] * k, rgb[1] * k, rgb[2] * k];
}

export const rgbKey = (r, g, b) => (((r | 0) << 16) | ((g | 0) << 8) | (b | 0)) >>> 0;

/* ------------------------------------------------------------- time gradient */

/**
 * Gradient sampler used for the dynamic ambient light ("the light's colour set
 * with a gradient"). Stops are [pos, cssColour] with pos in 0..1.
 */
export function makeGradient(stops) {
  const s = stops.map(([p, c]) => [p, hexToRgb(c)]).sort((a, b) => a[0] - b[0]);
  return function sample(t) {
    const x = ((t % 1) + 1) % 1;
    for (let i = 0; i < s.length - 1; i++) {
      if (x >= s[i][0] && x <= s[i + 1][0]) {
        const k = invLerp(s[i][0], s[i + 1][0], x);
        return mixRgb(s[i][1], s[i + 1][1], smoothstep(k));
      }
    }
    return s[s.length - 1][1].slice();
  };
}

/* ------------------------------------------------------------------- LUT i/o */

export const LUT_SIZE = 32;
/** Flat RGBA strip: LUT_SIZE*LUT_SIZE wide (1024), LUT_SIZE tall (32). */
export const LUT_W = LUT_SIZE * LUT_SIZE;
export const LUT_H = LUT_SIZE;

export function makeIdentityLut() {
  const data = new Uint8ClampedArray(LUT_W * LUT_H * 4);
  for (let b = 0; b < LUT_SIZE; b++) {
    for (let g = 0; g < LUT_SIZE; g++) {
      for (let r = 0; r < LUT_SIZE; r++) {
        // slice b is laid out horizontally: x = b*32 + r, y = g
        const i = (g * LUT_W + (b * LUT_SIZE + r)) * 4;
        data[i] = (r / (LUT_SIZE - 1)) * 255;
        data[i + 1] = (g / (LUT_SIZE - 1)) * 255;
        data[i + 2] = (b / (LUT_SIZE - 1)) * 255;
        data[i + 3] = 255;
      }
    }
  }
  return data;
}

/**
 * Build the 16-bit (r<<10 | g<<5 | b) lookup table used by the colour pass.
 * lutData is the RGBA strip produced by makeIdentityLut / applyLutStyle.
 */
export function buildLutTable(lutData) {
  const table = new Uint16Array(32768);
  for (let b = 0; b < 32; b++) {
    for (let g = 0; g < 32; g++) {
      for (let r = 0; r < 32; r++) {
        // read the strip: x = b*LUT_SIZE + r, y = g
        const px = ((g * LUT_W) + (b * LUT_SIZE + r)) * 4;
        const R = lutData[px], G = lutData[px + 1], B = lutData[px + 2];
        table[(r << 10) | (g << 5) | b] = ((R >> 3) << 10) | ((G >> 3) << 5) | (B >> 3);
      }
    }
  }
  // silence unused warning for step (kept for documentation clarity)
  void step;
  return table;
}

// Build stamp: shown in the HUD and drawn into the pixel buffer so any
// screenshot reveals which code generation produced it.
export const BUILD = 'r5 · versioned modules + live reload';
