/**
 * lighting.js — dynamic ambient light, the sun/moon model, the LUT colour
 * correction and the "3D light distribution" trick.
 *
 * From the article:
 *  - "Dynamic ambient light is just change of illumination depending on the
 *     time of the day. By nightfall a light source not only becomes darker but
 *     gets blue tint."
 *  - "There is a shader that calculates the distance between a light source and
 *     a sprite on the vertical axis ... if it's negative (the sprite is blocking
 *     the light source), then the intensity of lighting is fading depending on
 *     the distance at a very quick rate."
 */

import { clamp, lerp, smoothstep, makeGradient, LUT_SIZE, LUT_W, LUT_H } from './util.js?v=r6';

/** 5-bit -> 8-bit expansion so a 32-step LUT still reaches pure white. */
const Q5 = new Uint8Array(32);
for (let i = 0; i < 32; i++) Q5[i] = Math.round((i * 255) / 31);

/* ---------------------------------------------------------- dynamic ambient */

const AMBIENT_STOPS = [
  [0.00, '#0a0f24'],
  [0.18, '#151c3c'],
  [0.24, '#3b3358'],
  [0.28, '#8a6478'],
  [0.34, '#9fb0cc'],
  [0.50, '#c9d6ea'],
  [0.66, '#c3c0d4'],
  [0.74, '#9a6a72'],
  [0.79, '#4a3a63'],
  [0.86, '#1d2247'],
  [1.00, '#0a0f24'],
];

const AMBIENT_INTENSITY_STOPS = [
  [0.00, 0.16], [0.20, 0.20], [0.26, 0.34], [0.32, 0.62],
  [0.50, 0.95], [0.68, 0.86], [0.76, 0.52], [0.82, 0.28], [0.90, 0.18], [1.00, 0.16],
];

export const ambientColorGradient = makeGradient(AMBIENT_STOPS);

export function ambientIntensity(t) {
  const x = ((t % 1) + 1) % 1;
  const s = AMBIENT_INTENSITY_STOPS;
  for (let i = 0; i < s.length - 1; i++) {
    if (x >= s[i][0] && x <= s[i + 1][0]) {
      const k = (x - s[i][0]) / (s[i + 1][0] - s[i][0]);
      return lerp(s[i][1], s[i + 1][1], smoothstep(k));
    }
  }
  return s[s.length - 1][1];
}

/* ------------------------------------------------------------------ sun/moon */

export const MAX_SUN_ELEVATION = 62 * Math.PI / 180;

export class Sun {
  constructor() {
    this.azimuth = 0;        // radians, from north (up/away), clockwise
    this.elevation = 0;      // radians, > 0 = above the horizon
    this.intensity = 0;
    this.color = [255, 244, 214];
    this.isMoon = false;
    this.shadowStrength = 0; // how strongly it stamps a shadow on the ground
    this.shadowLength = 1;   // ground length multiplier (1/tan(elevation), clamped)
  }

  /** t: 0..1 time of day (0 = midnight, 0.25 = sunrise, 0.5 = noon, 0.75 = sunset) */
  update(t) {
    const dayProgress = (t - 0.25) * 2;                       // 0 at sunrise, 1 at sunset
    const sunElev = Math.sin(dayProgress * Math.PI) * MAX_SUN_ELEVATION;
    const sunAz = Math.PI / 2 + Math.PI * clamp(dayProgress, -0.4, 1.4);
    const isDay = sunElev > -0.02;

    if (isDay) {
      this.elevation = sunElev;
      this.azimuth = sunAz;
      this.isMoon = false;
      const k = clamp(sunElev / MAX_SUN_ELEVATION);
      // low sun = warm and orange, high sun = near white
      this.color = [255, lerp(150, 244, k), lerp(96, 216, k * k)];
      this.intensity = smoothstep(clamp(sunElev / 0.30)) * 1.25;
      // how strongly the sun still stamps a shadow (stays high while it is up)
      this.shadowStrength = smoothstep(clamp(sunElev / 0.12)) * 0.95;
    } else {
      // the moon walks the mirrored path: dim, cold, still casts a shadow
      this.elevation = -sunElev;
      this.azimuth = sunAz + Math.PI;
      this.isMoon = true;
      const k = clamp(this.elevation / MAX_SUN_ELEVATION);
      this.color = [lerp(120, 170, k), lerp(150, 200, k), 255];
      this.intensity = smoothstep(clamp(this.elevation / 0.45)) * 0.30;
      this.shadowStrength = smoothstep(clamp(this.elevation / 0.45)) * 0.42;
    }
    const tanE = Math.tan(Math.max(0.06, this.elevation));
    this.shadowLength = clamp(2.6 / tanE, 0.55, 4.2);
    return this;
  }

  /** Ground-space direction the shadow of a vertical object points to. */
  shadowDirection() {
    // azimuth is measured toward the light; the shadow points the other way
    return { x: -Math.sin(this.azimuth), y: -Math.cos(this.azimuth) };
  }
}

/* ------------------------------------------------------- dynamic light source */

let LIGHT_ID = 1;

export class Light {
  constructor(opts = {}) {
    this.id = LIGHT_ID++;
    this.x = opts.x || 0;          // screen-space ground position (px)
    this.y = opts.y || 0;
    this.z = opts.z ?? 12;         // height above the ground (px) — matters for shadows
    this.radius = opts.radius ?? 70;
    this.intensity = opts.intensity ?? 1;
    this.color = opts.color || [255, 190, 110];
    this.kind = opts.kind || 'torch';
    this.flicker = opts.flicker ?? 0.18;   // 0 = steady (lamp), 1 = wild (torch)
    this.phase = Math.random() * 100;
    this.enabled = opts.enabled !== false;
    this._cur = this.intensity;
    this.dragging = false;
  }

  /** Cheap flame flicker: two out-of-phase sines + a jitter term. */
  update(time) {
    const f = this.flicker;
    if (f <= 0) { this._cur = this.intensity; return this._cur; }
    const a = Math.sin(time * 7.3 + this.phase) * 0.5 + Math.sin(time * 17.1 + this.phase * 2.3) * 0.3;
    const b = Math.sin(time * 3.1 + this.phase * 0.7);
    this._cur = this.intensity * clamp(1 + (a * 0.6 + b * 0.4) * f, 0.35, 1.5);
    return this._cur;
  }

  /**
   * Spherical influence volume, evaluated in *ground* units: the screen y axis
   * is the foreshortened ground depth, so it is divided by squashY, and `alt`
   * is how high above the ground the shaded point sits.
   */
  attenuate(dxScreen, dyScreen, alt, squashY) {
    const dyG = dyScreen / squashY;
    const dz = this.z - alt;
    const d2 = dxScreen * dxScreen + dyG * dyG + dz * dz;
    const r2 = this.radius * this.radius;
    if (d2 >= r2) return 0;
    const k = 1 - d2 / r2;
    return k * k * this._cur;
  }

  attenuateAt(x, y, alt, squashY) {
    return this.attenuate(x - this.x, y - this.y, alt, squashY);
  }
}

/**
 * The article's "3D light distribution": how much a sprite is lit when the
 * light sits behind it. dyGround > 0 => the light is in front of the sprite
 * (we see its lit side). dyGround < 0 => the sprite blocks the light, so the
 * lighting fades with distance "at a very quick rate".
 */
export function verticalFacingFactor(dyGround, rate = 0.16, floor = 0.06) {
  if (dyGround >= 0) return 1;
  return Math.max(floor, Math.exp(dyGround * rate));
}

/* ------------------------------------------------------------------ LUT stuff */

/**
 * Grade styles. Each one is a tiny colour-transform the "artist" would have
 * painted by hand; here they are generated so we can cross-fade between them
 * continuously and still show the resulting 1024x32 strip in the UI.
 */
export const LUT_STYLES = {
  identity: { gain: [1, 1, 1], gamma: [1, 1, 1], lift: [0, 0, 0], sat: 1, contrast: 0, tint: [0, 0, 0] },
  dawn:     { gain: [1.02, 0.94, 0.96], gamma: [1.02, 1.0, 0.96], lift: [10, 4, 8], sat: 0.92, contrast: 0.06, tint: [12, -4, 6] },
  day:      { gain: [1.0, 1.02, 1.04], gamma: [1, 1, 1], lift: [2, 3, 6], sat: 1.06, contrast: 0.04, tint: [0, 2, 6] },
  golden:   { gain: [1.14, 1.0, 0.80], gamma: [0.98, 1.0, 1.04], lift: [12, 4, -4], sat: 1.16, contrast: 0.10, tint: [16, 2, -14] },
  dusk:     { gain: [0.94, 0.82, 1.00], gamma: [1.0, 1.02, 0.98], lift: [10, 2, 14], sat: 0.96, contrast: 0.12, tint: [6, -8, 18] },
  night:    { gain: [0.62, 0.72, 1.10], gamma: [1.06, 1.02, 0.94], lift: [4, 8, 22], sat: 0.72, contrast: 0.14, tint: [-10, -4, 26] },
  crypt:    { gain: [0.70, 0.92, 0.78], gamma: [1.04, 1.0, 1.02], lift: [2, 12, 6], sat: 0.80, contrast: 0.18, tint: [-6, 14, 2] },
};

/** Which style dominates at which time of day (cross-faded in between). */
const LUT_TIMELINE = [
  [0.00, 'night'], [0.20, 'night'], [0.26, 'dawn'], [0.33, 'day'],
  [0.52, 'day'], [0.68, 'golden'], [0.76, 'dusk'], [0.84, 'night'], [1.00, 'night'],
];

function gradeColor(r, g, b, s, ambient, ambientAmt) {
  // 1) ambient multiply (dynamic ambient light) + tint
  let R = r * (1 - ambientAmt) + r * (ambient[0] / 255) * ambientAmt;
  let G = g * (1 - ambientAmt) + g * (ambient[1] / 255) * ambientAmt;
  let B = b * (1 - ambientAmt) + b * (ambient[2] / 255) * ambientAmt;
  // 2) style: gain / lift / gamma
  R = R * s.gain[0] + s.lift[0] + s.tint[0];
  G = G * s.gain[1] + s.lift[1] + s.tint[1];
  B = B * s.gain[2] + s.lift[2] + s.tint[2];
  R = 255 * Math.pow(clamp(R / 255), s.gamma[0]);
  G = 255 * Math.pow(clamp(G / 255), s.gamma[1]);
  B = 255 * Math.pow(clamp(B / 255), s.gamma[2]);
  // 3) saturation
  const l = R * 0.299 + G * 0.587 + B * 0.114;
  R = lerp(l, R, s.sat); G = lerp(l, G, s.sat); B = lerp(l, B, s.sat);
  // 4) contrast (S-curve around mid grey)
  if (s.contrast !== 0) {
    const c = s.contrast;
    R = 127.5 + (R - 127.5) * (1 + c) - c * (R - 127.5) * Math.abs(R - 127.5) / 127.5 * 0.5;
    G = 127.5 + (G - 127.5) * (1 + c) - c * (G - 127.5) * Math.abs(G - 127.5) / 127.5 * 0.5;
    B = 127.5 + (B - 127.5) * (1 + c) - c * (B - 127.5) * Math.abs(B - 127.5) / 127.5 * 0.5;
  }
  return [clamp(R, 0, 255), clamp(G, 0, 255), clamp(B, 0, 255)];
}

/** Build a 1024x32 RGBA LUT strip for one style + ambient. */
export function renderLutStrip(style, ambient, ambientAmt, out = null) {
  const s = typeof style === 'string' ? LUT_STYLES[style] : style;
  const data = out || new Uint8ClampedArray(LUT_W * LUT_H * 4);
  for (let b = 0; b < LUT_SIZE; b++) {
    for (let g = 0; g < LUT_SIZE; g++) {
      for (let r = 0; r < LUT_SIZE; r++) {
        const src = [
          (r / (LUT_SIZE - 1)) * 255,
          (g / (LUT_SIZE - 1)) * 255,
          (b / (LUT_SIZE - 1)) * 255,
        ];
        const c = gradeColor(src[0], src[1], src[2], s, ambient, ambientAmt);
        const i = (g * LUT_W + (b * LUT_SIZE + r)) * 4;
        data[i] = c[0]; data[i + 1] = c[1]; data[i + 2] = c[2]; data[i + 3] = 255;
      }
    }
  }
  return data;
}

export class LutController {
  constructor() {
    this.stripA = new Uint8ClampedArray(LUT_W * LUT_H * 4);
    this.stripB = new Uint8ClampedArray(LUT_W * LUT_H * 4);
    this.strip = new Uint8ClampedArray(LUT_W * LUT_H * 4);
    /** 32768-entry packed 5-6-5 table used by the colour pass. */
    this.table = new Uint16Array(32768);
    this.styleA = 'night'; this.styleB = 'night'; this.fade = 0;
    this.enabled = true;
    this.forceStyle = null;   // UI override so a single LUT can be inspected
    this.rebuilds = 0;
    this._lastKey = '';
  }

  static stylesAt(t) {
    const x = ((t % 1) + 1) % 1;
    for (let i = 0; i < LUT_TIMELINE.length - 1; i++) {
      if (x >= LUT_TIMELINE[i][0] && x <= LUT_TIMELINE[i + 1][0]) {
        const k = (x - LUT_TIMELINE[i][0]) / (LUT_TIMELINE[i + 1][0] - LUT_TIMELINE[i][0]);
        return { a: LUT_TIMELINE[i][1], b: LUT_TIMELINE[i + 1][1], fade: smoothstep(k) };
      }
    }
    return { a: 'night', b: 'night', fade: 0 };
  }

  /** Rebuild only when the quantised time/ambient actually changed. */
  update(timeOfDay, ambientRgb, ambientAmt) {
    let a, b, fade;
    if (this.forceStyle) { a = this.forceStyle; b = this.forceStyle; fade = 0; }
    else ({ a, b, fade } = LutController.stylesAt(timeOfDay));

    const key = `${a}|${b}|${Math.round(fade * 24)}|${ambientRgb.map((v) => v >> 3).join(',')}|${Math.round(ambientAmt * 24)}|${this.enabled}`;
    if (key === this._lastKey) return false;
    this._lastKey = key;
    this.styleA = a; this.styleB = b; this.fade = fade;

    if (!this.enabled) {
      // identity LUT: the picture the article shows as "default LUT"
      for (let bi = 0; bi < LUT_SIZE; bi++) {
        for (let g = 0; g < LUT_SIZE; g++) {
          for (let r = 0; r < LUT_SIZE; r++) {
            const i = (g * LUT_W + (bi * LUT_SIZE + r)) * 4;
            this.strip[i] = (r / 31) * 255; this.strip[i + 1] = (g / 31) * 255;
            this.strip[i + 2] = (bi / 31) * 255; this.strip[i + 3] = 255;
          }
        }
      }
    } else {
      renderLutStrip(LUT_STYLES[a], ambientRgb, ambientAmt, this.stripA);
      if (b === a || fade <= 0.001) this.strip.set(this.stripA);
      else {
        renderLutStrip(LUT_STYLES[b], ambientRgb, ambientAmt, this.stripB);
        const w = fade;
        for (let i = 0; i < this.strip.length; i += 4) {
          this.strip[i] = this.stripA[i] * (1 - w) + this.stripB[i] * w;
          this.strip[i + 1] = this.stripA[i + 1] * (1 - w) + this.stripB[i + 1] * w;
          this.strip[i + 2] = this.stripA[i + 2] * (1 - w) + this.stripB[i + 2] * w;
          this.strip[i + 3] = 255;
        }
      }
    }
    this.bakeTable();
    this.rebuilds++;
    return true;
  }

  /** Flatten the strip into the 15-bit index table used per pixel. */
  bakeTable() {
    const t = this.table, s = this.strip;
    for (let b = 0; b < 32; b++) {
      for (let g = 0; g < 32; g++) {
        for (let r = 0; r < 32; r++) {
          const px = (g * LUT_W + (b * LUT_SIZE + r)) * 4;
          t[(r << 10) | (g << 5) | b] = ((s[px] >> 3) << 10) | ((s[px + 1] >> 3) << 5) | (s[px + 2] >> 3);
        }
      }
    }
  }

  /**
   * Apply the LUT to a framebuffer's pixels ("Easy to make. Easy to use. Works
   * really fast.") — one 15-bit table lookup per pixel, no branching.
   */
  apply(fb) {
    const d = fb.data, t = this.table, q = Q5;
    for (let i = 0; i < d.length; i += 4) {
      const v = t[((d[i] >> 3) << 10) | ((d[i + 1] >> 3) << 5) | (d[i + 2] >> 3)];
      d[i] = q[v >> 10];
      d[i + 1] = q[(v >> 5) & 31];
      d[i + 2] = q[v & 31];
    }
    fb.dirty = true;
  }
}
