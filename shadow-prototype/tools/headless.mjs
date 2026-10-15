/**
 * headless.mjs — runs the real renderer without a DOM and dumps PNG frames.
 *
 *   node tools/headless.mjs            # a set of verification shots
 *   node tools/headless.mjs --bench    # frame-time benchmark
 *
 * This is the prototype's smoke test: if a pass throws, or the shadows stop
 * covering anything, it shows up here before it shows up in the browser.
 */

import fs from 'node:fs';
import path from 'node:path';
import { buildAssets } from '../src/sprites.js';
import { Scene } from '../src/scene.js';
import { Renderer } from '../src/renderer.js';
import { encodePNG } from './png.mjs';

const OUT = path.resolve('tools/shots');
fs.mkdirSync(OUT, { recursive: true });

const W = 320, H = 180;

function makeScene(timeOfDay = 0.71) {
  const assets = buildAssets();
  const scene = new Scene(assets, { timeOfDay, timeSpeed: 0 });
  const renderer = new Renderer(scene, { width: W, height: H });
  scene.renderer = renderer;
  // snap the camera onto the keeper immediately
  scene.camX = Math.max(0, Math.min(scene.w - W, scene.keeper.x - W * 0.5));
  scene.camY = Math.max(0, Math.min(scene.h - H, scene.keeper.y - H * 0.62));
  return { scene, renderer, assets };
}

function shoot(name, opts = {}) {
  const { scene, renderer } = makeScene(opts.timeOfDay ?? 0.71);
  Object.assign(renderer.settings, opts.settings || {});
  if (opts.shadowCfg) Object.assign(renderer.shadows.cfg, opts.shadowCfg);
  if (opts.debug) Object.assign(renderer.settings.debug, opts.debug);
  if (opts.select !== false) {
    scene.selectedCaster = opts.selectCaster
      ? scene.casters.find((c) => c.name.startsWith(opts.selectCaster))
      : scene.casters.find((c) => c.name.startsWith('tomb_round'));
  }
  if (opts.moveKeeper) { scene.keeper.x += opts.moveKeeper[0]; scene.keeper.y += opts.moveKeeper[1]; }
  if (opts.keeperAt) {
    scene.keeper.x = opts.keeperAt[0]; scene.keeper.y = opts.keeperAt[1];
    scene.keeper.patrol.manual = 99;
    scene.camX = Math.max(0, Math.min(scene.w - W, scene.keeper.x - W * 0.5));
    scene.camY = Math.max(0, Math.min(scene.h - H, scene.keeper.y - H * 0.55));
  }
  if (opts.torch) scene.addLight(opts.torch[0], opts.torch[1], { z: 13, radius: 74, intensity: 1.3, color: [255, 168, 84], kind: 'torch', flicker: 0 });

  const frames = opts.frames ?? 3;
  let stats = null;
  for (let i = 0; i < frames; i++) {
    scene.update(1 / 60, { viewW: W, viewH: H, snapCamera: i === 0 });
    stats = renderer.frame(1 / 60);
  }
  const file = path.join(OUT, `${name}.png`);
  fs.writeFileSync(file, encodePNG(W, H, renderer.out.data, { scale: opts.scale ?? 3 }));
  console.log(`${name.padEnd(24)} -> ${file}  frame=${stats.frameMs.toFixed(2)}ms quads=${stats.quads} cov=${coverageStats(renderer).toFixed(3)}`);
  return { scene, renderer, stats };
}

function coverageStats(renderer) {
  let sum = 0, n = 0;
  for (const c of renderer.coverage) { sum += c; if (c > 0.02) n++; }
  return n / renderer.coverage.length;
}

/* ------------------------------------------------------------------ self test */

function assert(cond, msg) {
  if (!cond) { console.error(`FAIL: ${msg}`); process.exitCode = 1; }
  else console.log(`ok   ${msg}`);
}

const shots = [
  ['01-dusk', { timeOfDay: 0.70 }],
  ['02-noon', { timeOfDay: 0.5 }],
  ['03-night', { timeOfDay: 0.02 }],
  ['04-dawn', { timeOfDay: 0.27 }],
  ['05-shadow-mask', { timeOfDay: 0.70, settings: { view: 'shadows' } }],
  ['06-quads', { timeOfDay: 0.70, debug: { shadowQuads: true, pivots: true, channels: true }, selectCaster: 'tomb_cross' }],
  ['07-normals', { timeOfDay: 0.70, settings: { view: 'normals' } }],
  ['08-lights', { timeOfDay: 0.02, settings: { view: 'lights' } }],
  ['09-fog-100', { timeOfDay: 0.3, settings: { fog: 1 } }],
  ['10-no-shadows', { timeOfDay: 0.70, settings: { shadows: false } }],
  ['11-squash-1', { timeOfDay: 0.70, shadowCfg: { squashY: 1 }, settings: {} }],
  ['12-skew', { timeOfDay: 0.70, shadowCfg: { skew: 0.45 } }],
  ['13-torch-night', { timeOfDay: 0.02, torch: [210, 200] }],
  ['14-physical', { timeOfDay: 0.70, settings: { shadowModel: 'physical' } }],
  ['15-light-radii', { timeOfDay: 0.70, debug: { lightRadii: true, grid: true, sunVector: true } }],
  ['16-one-shadow', { timeOfDay: 0.70, shadowCfg: { maxDynamic: 1 }, torch: [200, 205] }],
  ['17-chapel-auto', { timeOfDay: 0.70, keeperAt: [330, 200] }],
  ['18-chapel-blob', { timeOfDay: 0.70, keeperAt: [330, 200], settings: { shadowStyle: 'blob' } }],
  ['19-chapel-project-all', { timeOfDay: 0.70, keeperAt: [330, 200], settings: { shadowStyle: 'project' } }],
  ['20-chapel-selfshadow', { timeOfDay: 0.70, keeperAt: [330, 200], settings: { selfShadow: true } }],
  ['21-trees-morning', { timeOfDay: 0.33, keeperAt: [150, 170] }],
];

for (const [name, opts] of shots) shoot(name, opts);

/* sanity checks on the main shot */
const { renderer, scene } = shoot('00-check', { timeOfDay: 0.70, frames: 4 });
assert(coverageStats(renderer) > 0.002, 'shadow coverage mask is not empty');
assert(renderer.shadows.stats.renderedQuads > 20, `enough shadow quads rendered (${renderer.shadows.stats.renderedQuads})`);
assert(renderer.shadows.stats.maxAngleError < 1.5, `8-bit angle channel error < 1.5deg (${renderer.shadows.stats.maxAngleError.toFixed(3)})`);
const d = renderer.out.data;
let nonBlack = 0;
for (let i = 0; i < d.length; i += 4) if (d[i] + d[i + 1] + d[i + 2] > 24) nonBlack++;
assert(nonBlack / (W * H) > 0.9, 'the frame is mostly non-black');

// every sprite must have a normal map and a pivot
let bad = 0;
for (const key of Object.keys(scene.assets)) {
  const a = scene.assets[key];
  if (!a || !a.canvas) continue;
  if (!a.canvas.normals) { console.error(`  missing normals: ${key}`); bad++; }
  if (!a.pivot) { console.error(`  missing pivot: ${key}`); bad++; }
}
assert(bad === 0, 'all assets have normals + pivots');

// shadow sprite set
assert(Object.keys(renderer.shadowSprites).length >= 8, 'shadow sprite library has >= 8 universal blobs');

if (process.argv.includes('--bench')) {
  const { scene: s2, renderer: r2 } = makeScene(0.70);
  r2.settings.litCache = false;
  for (let i = 0; i < 20; i++) { s2.update(1 / 60, { viewW: W, viewH: H }); r2.frame(1 / 60); }
  const runs = { cacheOn: null, cacheOff: null };
  for (const mode of ['cacheOff', 'cacheOn']) {
    r2.settings.litCache = mode === 'cacheOn';
    for (const m of r2.caches.values()) m.clear();
    let t = 0, f = 0;
    const N = 120;
    for (let i = 0; i < N; i++) {
      s2.update(1 / 60, { viewW: W, viewH: H });
      const st = r2.frame(1 / 60);
      if (i > 20) { t += st.objectMs; f += st.frameMs; }   // skip the warm-up
    }
    runs[mode] = t / (N - 21);
    console.log(`bench ${mode}: objects ${(t / (N - 21)).toFixed(3)} ms/frame, whole frame ${(f / (N - 21)).toFixed(2)} ms`);
  }
  console.log(`bench lit-sprite cache: objects pass is ${(runs.cacheOff / runs.cacheOn).toFixed(2)}x faster with the cache`);
  // grid vs brute force in the assign loop
  for (const grid of [true, false]) {
    r2.shadows.useGrid = grid;
    let t = 0; const N = 120;
    for (let i = 0; i < N; i++) { s2.update(1 / 60, { viewW: W, viewH: H }); r2.frame(1 / 60); t += r2.shadows.stats.assignMs; }
    console.log(`bench assign (${grid ? 'spatial grid' : 'brute force'}): ${(t / N).toFixed(3)} ms/frame`);
  }
}
