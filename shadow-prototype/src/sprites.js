/**
 * sprites.js — the pixel-art asset library.
 *
 * Small props are hand-authored as ASCII art (the honest way to make pixel art
 * readable in a source tree). Bigger/organic props are generated procedurally
 * from a seed and then "painted" with directional shading, which is the input
 * the normal-map merge script (pixelart.js) consumes — the same workflow the
 * article describes:
 *   "An artist roughly paints light on 4 sides with a brush ... and then we
 *    merge them with a script in a normal map."
 */

import { PixelCanvas, spriteFromRows, makeGlowSprite } from './pixelart.js';
import { clamp, mulberry32, valueNoise, shade as shadeRgb, hexToRgb } from './util.js';
import { bayer } from './framebuffer.js';

/* ------------------------------------------------------------------ palettes */

const P_STONE = {
  o: ['#221a2b', 0.95],
  S: ['#c6c2d8', 0.62],
  s: ['#9d99b3', 0.55],
  d: ['#6f6b88', 0.48],
  D: ['#4b4763', 0.42],
  x: ['#565272', 0.40],
  M: ['#6d8a4a', 0.50],
  g: ['#3d5530', 0.10],
  m: ['#4f6b39', 0.10],
};

const P_WOOD = {
  o: ['#241a1c', 0.95],
  k: ['#3a2519', 0.45],
  w: ['#a97a4c', 0.60],
  W: ['#8a6039', 0.55],
  d: ['#5f4028', 0.45],
  g: ['#3d5530', 0.10],
};

const P_KEEPER = {
  o: ['#191223', 0.95],
  h: ['#332844', 0.70],
  c: ['#4e3d66', 0.60],
  C: ['#6b5588', 0.62],
  d: ['#382c4c', 0.55],
  D: ['#2c2140', 0.52],
  s: ['#e0ac86', 0.60],
  E: ['#201426', 0.60],
  l: ['#8d97a6', 0.60],
  f: ['#ffb347', 0.60],
  F: ['#ffe9a8', 0.60],
  b: ['#2a2030', 0.40],
};

/* ------------------------------------------------------- hand-authored props */

const TOMB_ROUND_ROWS = [
  '....oooo....',
  '..ooSSSSoo..',
  '.oSSssssSSo.',
  '.oSsssxsssdo',
  'oSssxxxxsddo',
  'oSssssxssddo',
  'oSssssssddDo',
  'oSssssssddDo',
  'oSssMssdddDo',
  'oSssssssddDo',
  'oSsssssdddDo',
  'oDddddddddDo',
  '.oooooooooo.',
  '.ggggmggggg.',
  'gggggggggggg',
];

const TOMB_CROSS_ROWS = [
  '....ooo....',
  '....oSo....',
  '....oso....',
  '....oso....',
  '.ooooooooo.',
  '.oSSsssddo.',
  '.oSssssddo.',
  '....osd....',
  '....osd....',
  '....osd....',
  '....odd....',
  '....odd....',
  '...ooddo...',
  '..ooooooo..',
  '.ggggggggg.',
  'gggmggggggg',
];

const TOMB_SLAB_ROWS = [
  '......oooo....',
  '.....oSSSSo...',
  '....oSSssssdo.',
  '...oSssssssddo',
  '..oSssssssdddo',
  '.oSssssssddddo',
  '.oSssMssdddddo',
  'oDdddddddddddo',
  'oooooooooooooo',
  '.gggggggggggg.',
  'ggggmggggggmgg',
  'gggggggggggggg',
];


const FENCE_ROWS = [
  '...............',
  '..o....o....o..',
  '.ooo..ooo..ooo.',
  '.oww..oww..oww.',
  '.owd..owd..owd.',
  'kwwwwwwwwwwwwwk',
  'kdddddddddddddk',
  '.owd..owd..owd.',
  '.owd..owd..owd.',
  '.owd..owd..owd.',
  'kwwwwwwwwwwwwwk',
  'kdddddddddddddk',
  '.owd..owd..owd.',
  '.owd..owd..owd.',
  '.okk..okk..okk.',
];

const BUSH_ROWS = [
  '....ooooo.....',
  '..ooLLLLLoo...',
  '.oLLllllLLLo..',
  'oLllllllllLLo.',
  'oLlllllllldLo.',
  'ollllllllddddo',
  'ollllllddddddo',
  '.olllddddddo..',
  '..ooddddooo...',
  '....owwo......',
  '...gggggg.....',
];

const BARREL_ROWS = [
  '..oooooo..',
  '.owwwwwwo.',
  'owwwwwwwdo',
  'owwwwwwwdo',
  'okkkkkkkdo',
  'owwwwwwwdo',
  'owwwwwwwdo',
  'owwwwwwddo',
  'okkkkkkkdo',
  'owwwwwdddo',
  '.owwwwddo.',
  '..oooooo..',
];

const CRATE_ROWS = [
  'oooooooooooo',
  'owwwwwwwwwdo',
  'owwwwwwwwwdo',
  'owkwwwwwwkdo',
  'owwkwwwwkddo',
  'owwwkwwkdddo',
  'owwwkkwkdddo',
  'owwwkwwkdddo',
  'owwkwwwwkddo',
  'owkwwwwwwkdo',
  'oddddddddddo',
  'oooooooooooo',
];

const GRASS_TUFT_ROWS = [
  '..g...g..',
  '.gGg.gGg.',
  '.gGG.gGG.',
  'gGGGgGGGg',
  'gGGGGGGGg',
  '.ggggggg.',
  '..ddddd..',
];

/* ------------------------------------------------------------- procedural art */

function newCanvas(w, h) { return new PixelCanvas(w, h); }

/**
 * Bake directional shading into an already-drawn sprite. This is the "artist
 * paints light on 4 sides" step, done with a script instead of a brush: the
 * local surface gradient (from the distance transform) is dotted with the paint
 * light direction and used to lighten/darken the base colour.
 */
function paintDirectionalShading(pc, dir = [-0.6, -0.8], strength = 0.42) {
  const { w, h, data } = pc;
  const edge = pc.distanceToEdge(5);
  const hgt = new Float32Array(w * h);
  for (let p = 0; p < w * h; p++) hgt[p] = data[p * 4 + 3] > 0 ? Math.min(1, edge[p] / 3.0) : 0;
  const L = Math.hypot(dir[0], dir[1]) || 1;
  const lx = dir[0] / L, ly = dir[1] / L;
  for (let y = 0; y < h; y++) {
    for (let x = 0; x < w; x++) {
      const p = y * w + x;
      if (data[p * 4 + 3] === 0) continue;
      const l = hgt[y * w + Math.max(0, x - 1)], r = hgt[y * w + Math.min(w - 1, x + 1)];
      const u = hgt[Math.max(0, y - 1) * w + x], d = hgt[Math.min(h - 1, y + 1) * w + x];
      let nx = (l - r), ny = (d - u);
      const n = Math.hypot(nx, ny) || 1;
      nx /= n; ny /= n;
      const lam = clamp((nx * lx + ny * ly) * 0.5 + 0.5);
      const amt = (lam - 0.5) * 2 * strength;
      const i = p * 4;
      const c = shadeRgb([data[i], data[i + 1], data[i + 2]], amt);
      data[i] = c[0]; data[i + 1] = c[1]; data[i + 2] = c[2];
    }
  }
  return pc;
}

function noisyColor(base, x, y, amount, seed = 7) {
  const n = valueNoise(x * 0.9, y * 0.9, seed) * amount;
  return [
    clamp(base[0] + n * 255, 0, 255),
    clamp(base[1] + n * 255, 0, 255),
    clamp(base[2] + n * 255, 0, 255),
  ];
}

/** Bare winter tree: recursive branches, then shading + outline. */
export function makeDeadTree(seed = 3, W = 28, H = 36) {
  const rnd = mulberry32(seed * 977 + 13);
  const pc = newCanvas(W, H);
  const baseX = W >> 1, baseY = H - 2;
  const bark = hexToRgb('#6b4a30');
  const barkDark = hexToRgb('#40291a');

  const branch = (x, y, ang, len, thick, depth) => {
    if (depth > 6 || len < 1.2) return;
    const steps = Math.max(2, Math.ceil(len));
    let px = x, py = y;
    for (let i = 0; i < steps; i++) {
      const t = i / steps;
      const nx = x + Math.cos(ang) * len * t;
      const ny = y + Math.sin(ang) * len * t;
      const th = Math.max(1, Math.round(thick * (1 - t * 0.75)));
      const col = (i + depth) % 3 === 0 ? barkDark : bark;
      pc.line(Math.round(px), Math.round(py), Math.round(nx), Math.round(ny), col[0], col[1], col[2], 255, th);
      px = nx; py = ny;
    }
    const ex = x + Math.cos(ang) * len, ey = y + Math.sin(ang) * len;
    const spread = 0.42 + rnd() * 0.4;
    const n = depth < 2 ? 2 + (rnd() < 0.4 ? 1 : 0) : 1 + (rnd() < 0.55 ? 1 : 0);
    for (let i = 0; i < n; i++) {
      const a2 = ang + (n === 1 ? (rnd() - 0.5) * spread * 1.6 : (i / (n - 1) - 0.5) * spread * 2.4) + (rnd() - 0.5) * 0.2;
      branch(ex, ey, a2, len * (0.62 + rnd() * 0.22), Math.max(1, thick * 0.6), depth + 1);
    }
  };

  branch(baseX, baseY, -Math.PI / 2 + (rnd() - 0.5) * 0.18, H * 0.30, 4, 0);
  // root flare
  pc.line(baseX - 3, baseY + 1, baseX + 3, baseY + 1, barkDark[0], barkDark[1], barkDark[2], 255, 2);
  paintDirectionalShading(pc, [-0.7, -0.7], 0.5);
  pc.autoOutline(hexToRgb('#241a1c'));
  pc.name = 'dead_tree';
  return pc;
}

/** Leafy tree: trunk + dithered foliage clusters in 3 greens. */
export function makeLeafyTree(seed = 5, W = 32, H = 38, autumn = false) {
  const rnd = mulberry32(seed * 613 + 7);
  const pc = newCanvas(W, H);
  const baseX = W >> 1, baseY = H - 2;
  const bark = hexToRgb('#6b4a30');
  const barkDark = hexToRgb('#3d2718');

  // trunk
  const trunkH = H * 0.42;
  for (let y = 0; y < trunkH; y++) {
    const t = y / trunkH;
    const th = Math.max(1, Math.round(3.2 - t * 1.6));
    const yy = Math.round(baseY - y);
    const xx = Math.round(baseX + Math.sin(t * 2.1) * 1.2);
    pc.line(xx - th, yy, xx + th, yy, bark[0], bark[1], bark[2], 255, 1);
    pc.setPixel(xx - th, yy, barkDark[0], barkDark[1], barkDark[2]);
  }
  // a couple of limbs poking into the crown
  pc.line(baseX, baseY - trunkH * 0.7, baseX - 6, baseY - trunkH * 0.95, bark[0], bark[1], bark[2], 255, 2);
  pc.line(baseX, baseY - trunkH * 0.6, baseX + 6, baseY - trunkH * 0.9, bark[0], bark[1], bark[2], 255, 2);

  const greens = autumn
    ? ['#7d4a22', '#a86a2a', '#c98f34', '#e0b252'].map(hexToRgb)
    : ['#25401f', '#33572a', '#43702f', '#5d8f3c'].map(hexToRgb);

  const crownY = baseY - trunkH - H * 0.20;
  const clusters = 7 + Math.floor(rnd() * 4);
  for (let i = 0; i < clusters; i++) {
    const a = rnd() * Math.PI * 2;
    const rr = Math.pow(rnd(), 0.6) * W * 0.26;
    const cx = baseX + Math.cos(a) * rr;
    const cy = crownY + Math.sin(a) * rr * 0.62;
    const rad = W * (0.16 + rnd() * 0.13);
    for (let y = Math.floor(cy - rad - 1); y <= Math.ceil(cy + rad + 1); y++) {
      for (let x = Math.floor(cx - rad - 1); x <= Math.ceil(cx + rad + 1); x++) {
        if (x < 0 || y < 0 || x >= W || y >= H) continue;
        const dx = (x - cx) / rad, dy = (y - cy) / (rad * 0.86);
        const dd = Math.hypot(dx, dy);
        const nz = valueNoise(x * 0.55 + seed, y * 0.55, seed * 3 + i) * 0.34;
        const v = dd + nz;
        if (v > 1.06) continue;
        let band;
        if (v > 0.94) band = 0;
        else if (v > 0.7) band = 1;
        else if (v > 0.42) band = 2;
        else band = 3;
        // dithered transitions between the bands
        if (v > 0.86 && bayer(x, y) > 0.5) band = Math.max(0, band - 1);
        const c = noisyColor(greens[band], x, y, 0.035, seed + i);
        pc.set(x, y, c[0], c[1], c[2], 255, 0.35 + (1 - v) * 0.4);
      }
    }
  }
  paintDirectionalShading(pc, [-0.55, -0.8], 0.34);
  pc.autoOutline(hexToRgb('#1c2418'));
  pc.name = autumn ? 'tree_autumn' : 'tree_oak';
  return pc;
}

/** Pine: stacked triangular skirts. */
export function makePine(seed = 11, W = 24, H = 40) {
  const rnd = mulberry32(seed * 331 + 5);
  const pc = newCanvas(W, H);
  const baseX = W >> 1, baseY = H - 2;
  const bark = hexToRgb('#5b3d26');
  pc.rect(baseX - 1, baseY - 5, 3, 6, bark[0], bark[1], bark[2]);
  const greens = ['#1d3320', '#27452a', '#335c33', '#43743c'].map(hexToRgb);
  const tiers = 5;
  for (let t = 0; t < tiers; t++) {
    const ty = baseY - 4 - t * (H * 0.17);
    const halfW = (W * 0.46) * (1 - t / (tiers + 1.2)) + 1.5;
    const tierH = H * 0.24;
    for (let y = 0; y < tierH; y++) {
      const k = y / tierH;
      const hw = halfW * (1 - k * 0.92);
      const yy = Math.round(ty - y);
      for (let x = Math.round(baseX - hw); x <= Math.round(baseX + hw); x++) {
        if (x < 0 || x >= W || yy < 0 || yy >= H) continue;
        const edge = Math.abs(x - baseX) / Math.max(0.001, hw);
        let band = edge > 0.85 ? 0 : edge > 0.6 ? 1 : k < 0.4 ? 3 : 2;
        if (edge > 0.7 && bayer(x, yy) > 0.55) band = 0;
        const n = valueNoise(x * 0.8, yy * 0.8, seed) * 0.04;
        const c = greens[band];
        pc.set(x, yy, clamp(c[0] + n * 255, 0, 255), clamp(c[1] + n * 255, 0, 255), clamp(c[2] + n * 255, 0, 255), 255, 0.4 + (1 - edge) * 0.35);
      }
    }
  }
  paintDirectionalShading(pc, [-0.6, -0.75], 0.3);
  pc.autoOutline(hexToRgb('#161f18'));
  pc.name = 'pine';
  void rnd;
  return pc;
}

/** Small chapel / mortuary house — the biggest caster in the scene. */
export function makeChapel(seed = 21, W = 48, H = 50) {
  const pc = newCanvas(W, H);
  const stone = ['#8f8ba6', '#7a7690', '#666279', '#524e63'].map(hexToRgb);
  const roof = ['#4c3346', '#3d2839', '#5d3f55'].map(hexToRgb);
  const groundY = H - 4;
  const wallTop = Math.round(H * 0.42);
  const x0 = 7, x1 = W - 8;

  // walls with course lines
  for (let y = wallTop; y <= groundY; y++) {
    for (let x = x0; x <= x1; x++) {
      const course = Math.floor((y - wallTop) / 4);
      const brickOffset = (course % 2) * 3;
      const mortar = ((y - wallTop) % 4 === 3) || ((x + brickOffset) % 7 === 0);
      let c = mortar ? stone[3] : stone[(Math.floor((x + brickOffset) / 7) + course) % 2 === 0 ? 1 : 2];
      const n = valueNoise(x * 0.7, y * 0.7, seed) * 0.05;
      pc.set(x, y, clamp(c[0] + n * 255, 0, 255), clamp(c[1] + n * 255, 0, 255), clamp(c[2] + n * 255, 0, 255), 255, 0.5);
    }
  }

  // roof (triangle) + ridge
  const apexX = (x0 + x1) / 2, apexY = 5;
  const eaveL = x0 - 4, eaveR = x1 + 4, eaveY = wallTop + 1;
  for (let y = apexY; y <= eaveY; y++) {
    const k = (y - apexY) / (eaveY - apexY);
    const hw = (eaveR - eaveL) / 2 * k;
    for (let x = Math.round(apexX - hw); x <= Math.round(apexX + hw); x++) {
      if (x < 0 || x >= W) continue;
      const tileRow = Math.floor((y - apexY) / 3);
      let c = roof[(x + tileRow) % 3 === 0 ? 2 : (tileRow % 2 === 0 ? 0 : 1)];
      if ((y - apexY) % 3 === 2) c = roof[1];
      pc.set(x, y, c[0], c[1], c[2], 255, 0.62);
    }
  }
  // eave board
  pc.rect(eaveL, eaveY, eaveR - eaveL + 1, 2, 40, 30, 44, 255, 0.55);

  // buttresses
  pc.rect(x0 - 2, wallTop + 6, 3, groundY - wallTop - 5, stone[2][0], stone[2][1], stone[2][2], 255, 0.55);
  pc.rect(x1, wallTop + 6, 3, groundY - wallTop - 5, stone[3][0], stone[3][1], stone[3][2], 255, 0.5);

  // arched door
  const doorX = Math.round(apexX) - 4, doorW = 9, doorTop = groundY - 15;
  for (let y = doorTop; y <= groundY; y++) {
    for (let x = doorX; x < doorX + doorW; x++) {
      const dy = (y - doorTop);
      const archR = doorW / 2;
      const cx = doorX + archR;
      if (dy < archR && Math.hypot(x + 0.5 - cx, doorTop + archR - (y + 0.5)) > archR) continue;
      const dark = 0.55 + 0.45 * clamp(dy / 14);
      pc.set(x, y, 44 * dark, 32 * dark, 40 * dark, 255, 0.2);
    }
  }
  // door frame
  for (let y = doorTop - 1; y <= groundY; y++) {
    pc.set(doorX - 1, y, stone[3][0], stone[3][1], stone[3][2], 255, 0.55);
    pc.set(doorX + doorW, y, stone[3][0], stone[3][1], stone[3][2], 255, 0.5);
  }

  // round window (emissive at night)
  const winX = Math.round(apexX), winY = wallTop - 6;
  for (let y = -4; y <= 4; y++) {
    for (let x = -4; x <= 4; x++) {
      const d = Math.hypot(x, y);
      if (d > 4) continue;
      if (d > 3.1) { pc.set(winX + x, winY + y, stone[3][0], stone[3][1], stone[3][2], 255, 0.6); continue; }
      const warm = 1 - d / 4.2;
      pc.set(winX + x, winY + y, 90 + 140 * warm, 70 + 110 * warm, 40 + 60 * warm, 255, 0.3);
      pc.markEmissive(winX + x, winY + y);
      pc.emissiveSeed = { x: winX, y: winY, r: 6, color: [255, 186, 92] };
    }
  }
  // window cross bars
  pc.vline(winX, winY - 3, winY + 3, 60, 46, 40);
  pc.hline(winX - 3, winX + 3, winY, 60, 46, 40);

  // roof cross
  const crossY = apexY - 5;
  pc.vline(Math.round(apexX), crossY, apexY, 52, 44, 58);
  pc.hline(Math.round(apexX) - 2, Math.round(apexX) + 2, crossY + 2, 52, 44, 58);

  // ground line
  pc.rect(x0 - 5, groundY + 1, (x1 + 5) - (x0 - 5) + 1, 2, 61, 85, 48, 255, 0.1);

  paintDirectionalShading(pc, [-0.65, -0.6], 0.4);
  pc.autoOutline(hexToRgb('#1d1726'));
  pc.name = 'chapel';
  return pc;
}

/** Brick oven / furnace — one of the article's dynamic light sources. */
export function makeOven(seed = 33, W = 22, H = 22) {
  const pc = newCanvas(W, H);
  const brick = ['#7a5248', '#69453c', '#8a5f52', '#57382f'].map(hexToRgb);
  const groundY = H - 3;
  const x0 = 3, x1 = W - 4, top = 6;
  for (let y = top; y <= groundY; y++) {
    for (let x = x0; x <= x1; x++) {
      const course = Math.floor((y - top) / 3);
      const off = (course % 2) * 2;
      const mortar = ((y - top) % 3 === 2) || ((x + off) % 5 === 0);
      const c = mortar ? brick[3] : brick[(x + course) % 3 === 0 ? 2 : 0];
      const n = valueNoise(x * 0.9, y * 0.9, seed) * 0.05;
      pc.set(x, y, clamp(c[0] + n * 255, 0, 255), clamp(c[1] + n * 255, 0, 255), clamp(c[2] + n * 255, 0, 255), 255, 0.5);
    }
  }
  // chimney
  pc.rect(Math.round(W / 2) - 2, 1, 5, top, brick[1][0], brick[1][1], brick[1][2], 255, 0.55);
  pc.rect(Math.round(W / 2) - 3, 0, 7, 2, brick[3][0], brick[3][1], brick[3][2], 255, 0.55);
  // fire mouth (emissive)
  const mx = Math.round(W / 2), my = groundY - 3;
  for (let y = -3; y <= 3; y++) {
    for (let x = -4; x <= 4; x++) {
      const d = Math.hypot(x * 0.8, y * 1.25);
      if (d > 3.4) continue;
      const hot = clamp(1 - d / 3.4);
      pc.set(mx + x, my + y, 60 + 195 * hot, 30 + 150 * hot * hot, 16 + 40 * hot, 255, 0.2);
      pc.markEmissive(mx + x, my + y);
    }
  }
  pc.emissiveSeed = { x: mx, y: my, r: 5, color: [255, 148, 54] };
  paintDirectionalShading(pc, [-0.5, -0.7], 0.3);
  pc.autoOutline(hexToRgb('#221a1c'));
  pc.name = 'oven';
  return pc;
}

/** Dirt mound with a small wooden cross. */
export function makeMound(seed = 41, W = 20, H = 12, cross = true) {
  const pc = newCanvas(W, H);
  const dirt = ['#5d4a37', '#4a3a2c', '#6b5741'].map(hexToRgb);
  const grass = hexToRgb('#3f5c31');
  const baseY = H - 2;
  const cx = W / 2;
  for (let y = 0; y < 6; y++) {
    const hw = (W / 2 - 1) * Math.sqrt(Math.max(0, 1 - (y / 7) ** 2)) * 0.95 + 2;
    for (let x = Math.round(cx - hw); x <= Math.round(cx + hw); x++) {
      if (x < 0 || x >= W) continue;
      const yy = baseY - y;
      const c = y > 3 ? dirt[2] : (x + y) % 3 === 0 ? dirt[0] : dirt[1];
      const n = valueNoise(x, yy, seed) * 0.05;
      pc.set(x, yy, clamp(c[0] + n * 255, 0, 255), clamp(c[1] + n * 255, 0, 255), clamp(c[2] + n * 255, 0, 255), 255, 0.2);
    }
  }
  for (let x = 1; x < W - 1; x++) {
    pc.set(x, baseY + 1, grass[0], grass[1], grass[2], 255, 0.1);
    if (bayer(x, baseY) > 0.55) pc.set(x, baseY, grass[0], grass[1], grass[2], 255, 0.1);
  }
  if (cross) {
    const wood = hexToRgb('#8a6039'), woodD = hexToRgb('#4d3320');
    const ccx = Math.round(cx);
    pc.vline(ccx, baseY - 9, baseY - 1, wood[0], wood[1], wood[2]);
    pc.vline(ccx + 1, baseY - 9, baseY - 1, woodD[0], woodD[1], woodD[2]);
    pc.hline(ccx - 3, ccx + 4, baseY - 7, wood[0], wood[1], wood[2]);
    pc.hline(ccx - 3, ccx + 4, baseY - 6, woodD[0], woodD[1], woodD[2]);
  }
  pc.autoOutline(hexToRgb('#241a1c'));
  pc.name = 'mound';
  return pc;
}

/** Boulder. */
export function makeRock(seed = 55, W = 14, H = 10) {
  const pc = newCanvas(W, H);
  const cols = ['#8b87a0', '#736f88', '#5d5a70', '#48455a'].map(hexToRgb);
  const cx = W / 2, cy = H - 2;
  for (let y = 0; y < H; y++) {
    for (let x = 0; x < W; x++) {
      const dx = (x - cx) / (W * 0.46), dy = (y - cy) / (H * 0.72);
      const d = Math.hypot(dx, dy) + valueNoise(x * 0.6, y * 0.6, seed) * 0.28;
      if (d > 1) continue;
      const c = d > 0.85 ? cols[3] : d > 0.6 ? cols[2] : d > 0.3 ? cols[1] : cols[0];
      pc.set(x, y, c[0], c[1], c[2], 255, 0.45);
    }
  }
  paintDirectionalShading(pc, [-0.6, -0.75], 0.35);
  pc.autoOutline(hexToRgb('#221c2b'));
  pc.name = 'rock';
  return pc;
}

/* ------------------------------------------------------------------ assembly */

function defineSprite(name, canvas, opts = {}) {
  canvas.makeNormals(opts.normalMode || 'merged', opts.normalStrength ?? 1.7);
  const w = canvas.w, h = canvas.h;
  return {
    name,
    canvas,
    // pivot: the point the shadow sprite rotates around (bottom centre by default)
    pivot: opts.pivot ? { x: opts.pivot.x, y: opts.pivot.y } : { x: (w - 1) / 2, y: h - 1 },
    // "altitude" of the sprite in world units — used for fog and shadow length
    worldHeight: opts.worldHeight ?? h * 0.9,
    shadow: opts.shadow || { kind: 'oval', length: 1, width: 1 },
    sway: opts.sway || null,
    emissive: canvas.emissive || null,
    emissiveSeed: canvas.emissiveSeed || null,
    frames: 1,
  };
}

/** The keeper: 3 animation frames (idle / step L / step R). */
function makeKeeper() {
  const head = [
    '.....oooo.....',
    '....ohhhho....',
    '...ohhhhhho...',
    '...ohssssho...',
    '...ohEssEho...',
    '...ohssssho...',
    '....occcco....',
    '...oCccccDo...',
    '..oCcccccDo...',
    '..occccccdlll',
    '..occccccdolfl',
    '..occccccdolFl',
    '..occccccdolfl',
    '..occccccd.lll',
  ];
  const legsA = [
    '...occ..ddo...',
    '...occ..ddo...',
    '..obbo..ddo...',
    '..obbo..bbbo..',
  ];
  const legsB = [
    '....cc..dd....',
    '....cc..dd....',
    '...obbo.bbo...',
    '...obbo.bbo...',
  ];
  const legsC = [
    '...odd..cco...',
    '...odd..cco...',
    '...odd..obbo..',
    '..obbb..obbo..',
  ];
  const frames = [head.concat(legsA), head.concat(legsB), head.concat(legsC)];
  return frames.map((rows, i) => {
    const pc = spriteFromRows(`keeper_${i}`, P_KEEPER, rows.map((s) => s.padEnd(14, '.').slice(0, 14)));
    return pc;
  });
}

/** Lantern post — 9px wide ASCII rows padded to a consistent grid. */
function makeLampPost() {
  const rows = [
    '..kkkkk..',
    '.kWWWWWk.',
    '.kGGGGGk.',
    'kGGfffGGk',
    'kGGfffGGk',
    '.kGGGGGk.',
    '.kWWWWWk.',
    '..kkkkk..',
    '...wWw...',
    '...wWw...',
    '...wWw...',
    '...wWw...',
    '...wWw...',
    '...wWw...',
    '...wWw...',
    '...wWw...',
    '..kwWwk..',
    '.kwwWWwk.',
    '.kgggggk.',
    'kkkkkkkkk',
  ];
  const palette = Object.assign({}, P_WOOD, {
    k: ['#3b2f24', 0.7],
    W: ['#6d5a3c', 0.6],
    w: ['#8a7248', 0.6],
    G: ['#6f7f8c', 0.5],
    f: ['#ffc46a', 0.5],
    g: ['#3d5530', 0.1],
    o: ['#241a1c', 0.95],
  });
  const pc = spriteFromRows('lamp_post', palette, rows);
  for (let y = 0; y < rows.length; y++) {
    for (let x = 0; x < rows[y].length; x++) {
      if (rows[y][x] === 'f' || rows[y][x] === 'G') pc.markEmissive(x, y);
    }
  }
  pc.emissiveSeed = { x: 4, y: 3, r: 7, color: [255, 190, 110] };
  return pc;
}

/* ------------------------------------------------------------- the asset set */

export function buildAssets() {
  const A = {};

  A.tombRound = defineSprite('tomb_round', spriteFromRows('tomb_round', P_STONE, TOMB_ROUND_ROWS), {
    worldHeight: 11, shadow: { kind: 'oval', length: 0.85, width: 0.8 },
  });
  A.tombCross = defineSprite('tomb_cross', spriteFromRows('tomb_cross', P_STONE, TOMB_CROSS_ROWS), {
    worldHeight: 13, shadow: { kind: 'thin', length: 0.8, width: 0.6 },
  });
  A.tombSlab = defineSprite('tomb_slab', spriteFromRows('tomb_slab', P_STONE, TOMB_SLAB_ROWS), {
    worldHeight: 7, shadow: { kind: 'thick', length: 0.8, width: 1.15 },
  });
  A.fence = defineSprite('fence', spriteFromRows('fence', P_WOOD, FENCE_ROWS), {
    // The article's known weak spot: a sprite stretched along one axis.
    worldHeight: 12, shadow: { kind: 'fence', length: 1.15, width: 1.5 },
  });
  A.bush = defineSprite('bush', spriteFromRows('bush', Object.assign({}, P_STONE, {
    L: ['#5d8f3c', 0.5], l: ['#43702f', 0.45], o: ['#1c2418', 0.9], w: ['#6b4a30', 0.4],
  }), BUSH_ROWS), { worldHeight: 8, shadow: { kind: 'blob', length: 0.7, width: 1 }, sway: { amp: 0.9, power: 2 } });
  A.barrel = defineSprite('barrel', spriteFromRows('barrel', P_WOOD, BARREL_ROWS), {
    worldHeight: 9, shadow: { kind: 'blob', length: 0.7, width: 0.9 },
  });
  A.crate = defineSprite('crate', spriteFromRows('crate', P_WOOD, CRATE_ROWS), {
    worldHeight: 9, shadow: { kind: 'thick', length: 0.6, width: 0.9 },
  });
  A.grass = defineSprite('grass', spriteFromRows('grass', {
    g: ['#4f7a37', 0.3], G: ['#6b9c46', 0.35], d: ['#3f3222', 0.1], '.': null,
  }, GRASS_TUFT_ROWS), { worldHeight: 5, shadow: { kind: null }, sway: { amp: 1.6, power: 1.4 } });

  A.lampPost = defineSprite('lamp_post', makeLampPost(), {
    worldHeight: 20, shadow: { kind: 'thin', length: 1.05, width: 0.55 },
  });
  A.deadTree = defineSprite('dead_tree', makeDeadTree(3), {
    worldHeight: 32, shadow: { kind: 'tree', length: 1.35, width: 1, style: 'project' }, sway: { amp: 0.5, power: 3 },
  });
  A.deadTree2 = defineSprite('dead_tree2', makeDeadTree(9, 24, 30), {
    worldHeight: 26, shadow: { kind: 'tree', length: 1.1, width: 0.85, style: 'project' }, sway: { amp: 0.6, power: 3 },
  });
  A.oak = defineSprite('oak', makeLeafyTree(5), {
    worldHeight: 34, shadow: { kind: 'tree', length: 1.5, width: 1.35, style: 'project' }, sway: { amp: 1.1, power: 2.5 },
  });
  A.oak2 = defineSprite('oak2', makeLeafyTree(17, 26, 32), {
    worldHeight: 28, shadow: { kind: 'tree', length: 1.25, width: 1.15, style: 'project' }, sway: { amp: 1.2, power: 2.5 },
  });
  A.autumn = defineSprite('autumn', makeLeafyTree(23, 30, 36, true), {
    worldHeight: 32, shadow: { kind: 'tree', length: 1.4, width: 1.3, style: 'project' }, sway: { amp: 1.3, power: 2.5 },
  });
  A.pine = defineSprite('pine', makePine(11), {
    worldHeight: 38, shadow: { kind: 'tree', length: 1.6, width: 1.1, style: 'project' }, sway: { amp: 0.4, power: 3 },
  });
  A.chapel = defineSprite('chapel', makeChapel(21), {
    worldHeight: 46, shadow: { kind: 'chapel', length: 1.7, width: 1.5, style: 'project' },
  });
  A.oven = defineSprite('oven', makeOven(33), {
    worldHeight: 18, shadow: { kind: 'thick', length: 0.9, width: 1 },
  });
  A.mound = defineSprite('mound', makeMound(41), {
    worldHeight: 4, shadow: { kind: 'mound', length: 0.5, width: 1.2 },
  });
  A.rock = defineSprite('rock', makeRock(55), {
    worldHeight: 6, shadow: { kind: 'blob', length: 0.55, width: 1 },
  });

  // keeper frames
  const kframes = makeKeeper();
  A.keeper = defineSprite('keeper', kframes[0], {
    worldHeight: 15, shadow: { kind: 'human', length: 0.9, width: 0.75 }, pivot: { x: 6, y: 17 },
  });
  A.keeper.frames = kframes.length;
  A.keeper.frameCanvases = kframes.map((c) => {
    c.makeNormals('merged', 1.7);
    return c;
  });

  // lamp halos (additive), intensity is scaled by time of day in the renderer
  A.glowWarm = makeGlowSprite(48, [255, 236, 190], [255, 150, 60], 2.0);
  A.glowFire = makeGlowSprite(40, [255, 214, 140], [255, 110, 30], 2.4);
  A.glowCool = makeGlowSprite(40, [200, 226, 255], [90, 140, 220], 2.2);

  return A;
}
