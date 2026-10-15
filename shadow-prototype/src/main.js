/**
 * main.js — bootstrap, input, HUD and the inspector.
 *
 * The prototype renders at 320x180 internal pixels and is scaled up by the
 * browser with `image-rendering: pixelated`, so every effect you see is real
 * per-pixel work in the JavaScript passes — there is no GPU in this prototype.
 */


import { buildAssets } from './sprites.js?v=r6';
import { Scene } from './scene.js?v=r6';
import { Renderer } from './renderer.js?v=r6';
import { Light, LUT_STYLES } from './lighting.js?v=r6';
import { MAX_LIGHT_SHADOWS } from './shadows.js?v=r6';
import { BUILD, clamp } from './util.js?v=r6';

const VIEW_W = 320;
const VIEW_H = 180;

const $ = (sel) => document.querySelector(sel);
const $$ = (sel) => Array.from(document.querySelectorAll(sel));

function getPath(obj, path) {
  return path.split('.').reduce((o, k) => (o == null ? o : o[k]), obj);
}
function setPath(obj, path, value) {
  const keys = path.split('.');
  let o = obj;
  for (let i = 0; i < keys.length - 1; i++) o = o[keys[i]];
  o[keys[keys.length - 1]] = value;
}

export function startApp() {
  const canvas = $('#view');
  canvas.width = VIEW_W;
  canvas.height = VIEW_H;

  const assets = buildAssets();
  const scene = new Scene(assets, { timeOfDay: 0.71, timeSpeed: 0.006 });
  const renderer = new Renderer(scene, { width: VIEW_W, height: VIEW_H, squashY: 0.42 });
  renderer.out.attachCanvas(canvas);
  scene.renderer = renderer;

  const state = {
    get timeOfDay() { return scene.timeOfDay; },
    set timeOfDay(v) { scene.timeOfDay = v; },
    get timeSpeed() { return scene.timeSpeed; },
    set timeSpeed(v) { scene.timeSpeed = v; },
    get paused() { return scene.paused; },
    set paused(v) { scene.paused = v; },
    get windStrength() { return scene.windStrength; },
    set windStrength(v) { scene.windStrength = v; },
    get squashY() { return renderer.squashY; },
    set squashY(v) { renderer.squashY = v; renderer.shadows.cfg.squashY = v; },
    get maxDynamic() { return renderer.shadows.cfg.maxDynamic; },
    set maxDynamic(v) { renderer.shadows.cfg.maxDynamic = v; },
    settings: renderer.settings,
    shadowCfg: renderer.shadows.cfg,
    get useGrid() { return renderer.shadows.useGrid; },
    set useGrid(v) { renderer.shadows.useGrid = v; },
  };

  /* ------------------------------------------------------------- UI binding */

  function readValue(el) {
    if (el.type === 'checkbox') return el.checked;
    if (el.tagName === 'SELECT') return el.value === 'true' ? true : (el.value === 'false' ? false : el.value);
    const n = parseFloat(el.value);
    return Number.isNaN(n) ? el.value : n;
  }

  $$('[data-bind]').forEach((el) => {
    const path = el.dataset.bind;
    const ev = (el.type === 'range' || el.type === 'text' || el.type === 'number') ? 'input' : 'change';
    el.addEventListener(ev, () => {
      const v = readValue(el);
      setPath(state, path, v);
      if (path === 'settings.shadowDarkness' || path === 'settings.shadowModel') invalidateCaches();
      if (path.startsWith('settings.litCache') || path.startsWith('settings.cacheBudget')) invalidateCaches();
      if (path === 'shadowCfg.maxDynamic' && v > MAX_LIGHT_SHADOWS) setPath(state, path, MAX_LIGHT_SHADOWS);
      syncLabels();
    });
    // initial value from state
    const cur = getPath(state, path);
    if (el.type === 'checkbox') el.checked = !!cur;
    else if (cur != null) el.value = cur;
  });

  function invalidateCaches() {
    for (const [, map] of renderer.caches) map.clear();
    renderer.cacheEntries = 0;
  }

  /* ---------------------------------------------------------------- buttons */

  $('#btn-add-light').addEventListener('click', () => addTorch(scene.keeper.x + 14, scene.keeper.y - 4));
  $('#btn-clear-lights').addEventListener('click', () => {
    scene.lights = scene.lights.filter((L) => L.owner || L === scene.keeper.light);
    syncLabels();
  });
  $('#btn-reset').addEventListener('click', () => {
    scene.timeOfDay = 0.71; scene.paused = false;
    renderer.settings.view = 'final';
    renderer.settings.shadowStyle = 'auto';
    renderer.settings.selfShadow = false;
    renderer.settings.fog = 0.42;
    renderer.settings.shadowDarkness = 0.86;
    renderer.shadows.cfg.maxDynamic = MAX_LIGHT_SHADOWS;
    renderer.shadows.cfg.squashY = 0.42;
    renderer.squashY = 0.42;
    renderer.shadows.cfg.skew = 0;
    renderer.shadows.cfg.angleQuantize = 0;
    renderer.shadows.cfg.pixelSnap = true;
    renderer.shadows.useGrid = true;
    for (const d of Object.keys(renderer.settings.debug)) renderer.settings.debug[d] = false;
    invalidateCaches();
    $$('[data-bind]').forEach((el) => {
      const cur = getPath(state, el.dataset.bind);
      if (el.type === 'checkbox') el.checked = !!cur;
      else if (cur != null) el.value = cur;
    });
    syncLabels();
  });
  $('#btn-pause').addEventListener('click', () => { scene.paused = !scene.paused; syncLabels(); });

  const presets = {
    dusk: 0.735, night: 0.02, dawn: 0.265, noon: 0.5, storm: 0.62,
  };
  $$('#time-presets button').forEach((b) => {
    b.addEventListener('click', () => {
      scene.timeOfDay = presets[b.dataset.preset];
      scene.paused = true;
      syncLabels();
    });
  });

  $$('#view-buttons button').forEach((b) => {
    b.addEventListener('click', () => {
      renderer.settings.view = b.dataset.view;
      $$('#view-buttons button').forEach((x) => x.classList.toggle('active', x === b));
    });
  });

  const lutSelect = $('#lut-style');
  for (const name of Object.keys(LUT_STYLES)) {
    const o = document.createElement('option');
    o.value = name; o.textContent = name;
    lutSelect.appendChild(o);
  }
  lutSelect.value = '';
  lutSelect.addEventListener('change', () => {
    renderer.settings.lutStyle = lutSelect.value || null;
  });

  /* ------------------------------------------------------------------ torch */

  let torchCount = 0;
  function addTorch(x, y) {
    if (scene.lights.length >= 14) return null;
    torchCount++;
    const L = new Light({
      x, y, z: 13, radius: 70, intensity: 1.25,
      color: [255, 168, 84], kind: 'torch', flicker: 0.6,
    });
    L.label = `torch ${torchCount}`;
    L.loose = true;
    scene.lights.push(L);
    scene.selectedLight = L;
    syncLabels();
    return L;
  }

  /* ------------------------------------------------------------------ input */

  const keys = new Set();
  window.addEventListener('keydown', (e) => {
    if (e.target.tagName === 'INPUT' || e.target.tagName === 'SELECT') return;
    keys.add(e.key.toLowerCase());
    const k = e.key.toLowerCase();
    if (k === ' ') { scene.paused = !scene.paused; syncLabels(); e.preventDefault(); }
    if (k === 'l') addTorch(scene.keeper.x + 12, scene.keeper.y - 2);
    if (k === 'x') {
      const loose = scene.lights.filter((L) => L.loose);
      if (loose.length) scene.lights.splice(scene.lights.indexOf(loose[loose.length - 1]), 1);
      syncLabels();
    }
    if (k === 'q') toggleDebug('shadowQuads', e);
    if (k === 'e') toggleDebug('lightRadii', e);
    if (k === 'r') toggleDebug('links', e);
    if (k === 't') toggleDebug('channels', e);
    if (k === 'y') toggleDebug('grid', e);
    if (k === 'p') toggleDebug('pivots', e);
    if (k === 'u') toggleDebug('sunVector', e);
    if (k === 'f') { renderer.settings.fog = renderer.settings.fog > 0.01 ? 0 : 0.42; syncBind('settings.fog'); }
    if (k === 'n') { renderer.settings.wind = !renderer.settings.wind; syncBind('settings.wind'); }
    if (k === 'g') { renderer.shadows.useGrid = !renderer.shadows.useGrid; syncBind('useGrid'); }
    if (k === 'h') { renderer.settings.shadows = !renderer.settings.shadows; syncBind('settings.shadows'); }
    if (k === ',' || k === '.') {
      scene.paused = true;
      scene.timeOfDay = (scene.timeOfDay + (k === ',' ? -0.004 : 0.004) + 1) % 1;
      syncLabels();
    }
    if (k >= '1' && k <= '5') {
      const views = ['final', 'shadows', 'lights', 'normals', 'altitude'];
      renderer.settings.view = views[parseInt(k, 10) - 1];
      $$('#view-buttons button').forEach((x) => x.classList.toggle('active', x.dataset.view === renderer.settings.view));
    }
    if (k === 'tab') { cycleSelection(); e.preventDefault(); }
  });
  window.addEventListener('keyup', (e) => keys.delete(e.key.toLowerCase()));

  function toggleDebug(name, e) {
    renderer.settings.debug[name] = !renderer.settings.debug[name];
    syncBind(`settings.debug.${name}`);
    if (e) e.preventDefault();
  }

  function syncBind(path) {
    const el = document.querySelector(`[data-bind="${path}"]`);
    if (!el) return;
    const cur = getPath(state, path);
    if (el.type === 'checkbox') el.checked = !!cur;
    else el.value = cur;
    syncLabels();
  }

  function cycleSelection() {
    const list = scene.casters;
    if (!list.length) return;
    const i = list.indexOf(scene.selectedCaster);
    scene.selectedCaster = list[(i + 1) % list.length];
    scene.renderer.settings.debug.channels = true;
    syncBind('settings.debug.channels');
  }

  function toWorld(ev) {
    const r = canvas.getBoundingClientRect();
    return {
      x: ((ev.clientX - r.left) / r.width) * VIEW_W + scene.camX,
      y: ((ev.clientY - r.top) / r.height) * VIEW_H + scene.camY,
    };
  }

  let dragging = null;
  canvas.addEventListener('pointerdown', (ev) => {
    const p = toWorld(ev);
    canvas.setPointerCapture(ev.pointerId);
    const L = scene.lightAt(p.x, p.y, 12);
    if (L && !ev.shiftKey) {
      dragging = { light: L, dx: L.x - p.x, dy: L.y - p.y };
      scene.selectedLight = L;
      scene.selectedCaster = null;
      syncLabels();
      return;
    }
    if (ev.shiftKey) { addTorch(p.x, p.y); return; }
    const c = scene.casterAt(p.x, p.y);
    scene.selectedCaster = c;
    if (c) { renderer.settings.debug.channels = true; renderer.settings.debug.shadowQuads = true; syncBind('settings.debug.shadowQuads'); }
    syncLabels();
  });
  canvas.addEventListener('pointermove', (ev) => {
    const p = toWorld(ev);
    if (dragging) {
      dragging.light.x = clamp(p.x + dragging.dx, 4, scene.w - 4);
      dragging.light.y = clamp(p.y + dragging.dy, 40, scene.h - 4);
      if (dragging.light.owner) dragging.light.owner = null;   // freed from its prop
      return;
    }
    const hover = scene.lightAt(p.x, p.y, 12);
    canvas.style.cursor = hover ? 'grab' : (scene.casterAt(p.x, p.y) ? 'crosshair' : 'default');
  });
  window.addEventListener('pointerup', () => { dragging = null; });
  canvas.addEventListener('wheel', (ev) => {
    const L = scene.selectedLight || scene.lightAt(toWorld(ev).x, toWorld(ev).y, 16);
    if (!L) return;
    ev.preventDefault();
    if (ev.shiftKey) L.z = clamp(L.z + (ev.deltaY > 0 ? -1 : 1), 2, 40);
    else L.radius = clamp(L.radius + (ev.deltaY > 0 ? -3 : 3), 20, 160);
    syncLabels();
  }, { passive: false });

  /* -------------------------------------------------------------- HUD wiring */

  const hud = {
    build: $('#hud-build'),
    fps: $('#hud-fps'),
    frame: $('#hud-frame'),
    passes: $('#hud-passes'),
    shadows: $('#hud-shadows'),
    cache: $('#hud-cache'),
    time: $('#hud-time'),
  };
  const inspectorEl = $('#inspector');
  const lightsEl = $('#light-list');
  const lutCanvas = $('#lut-strip');
  const lutCtx = lutCanvas.getContext('2d');
  lutCtx.imageSmoothingEnabled = false;

  const PASS_NAMES = [
    ['lightMs', 'light pools'], ['assignMs', 'shadow assign'], ['rasterMs', 'shadow raster'],
    ['groundMs', 'ground'], ['objectMs', 'objects'], ['glowMs', 'glows'],
    ['fogMs', 'fog'], ['lutMs', 'lut'], ['debugMs', 'debug'],
  ];

  function timeLabel(t) {
    const hours = Math.floor(t * 24);
    const mins = Math.floor((t * 24 - hours) * 60);
    return `${String(hours).padStart(2, '0')}:${String(mins).padStart(2, '0')}`;
  }

  let lastLutRebuild = -1;
  function drawLutStrip() {
    const strip = renderer.lut.strip;
    const w = lutCanvas.width, h = lutCanvas.height;
    const img = lutCtx.createImageData(w, h);
    for (let y = 0; y < h; y++) {
      const sy = Math.min(31, Math.floor((y / h) * 32));
      for (let x = 0; x < w; x++) {
        const sx = Math.min(1023, Math.floor((x / w) * 1024));
        const si = (sy * 1024 + sx) * 4;
        const di = (y * w + x) * 4;
        img.data[di] = strip[si]; img.data[di + 1] = strip[si + 1];
        img.data[di + 2] = strip[si + 2]; img.data[di + 3] = 255;
      }
    }
    lutCtx.putImageData(img, 0, 0);
  }

  function syncLabels() {
    const bindEls = $$('[data-out]');
    for (const el of bindEls) {
      const v = getPath(state, el.dataset.out);
      if (typeof v !== 'number') el.textContent = v;
      else el.textContent = Number.isInteger(v) ? String(v) : v.toFixed(2);
    }
    $('#btn-pause').textContent = scene.paused ? 'resume' : 'pause';
    $('#light-count').textContent = scene.lights.length;
  }

  let hudTick = 0;
  function updateHud(stats, dt) {
    hudTick++;
    if (hudTick % 6 === 0) {
      hud.build.textContent = BUILD;
    hud.fps.textContent = `${(1 / Math.max(dt, 0.0001)).toFixed(0)} fps`;
      hud.frame.textContent = `${stats.frameMs.toFixed(2)} ms`;
      hud.time.textContent = `${timeLabel(scene.timeOfDay)} · ${scene.sun.isMoon ? 'moon' : 'sun'} ${(scene.sun.elevation * 180 / Math.PI).toFixed(0)}°`;
      hud.passes.innerHTML = PASS_NAMES
        .map(([k, name]) => `<div class="bar"><span>${name}</span><i style="width:${Math.min(100, (stats[k] / 8) * 100).toFixed(1)}%"></i><b>${stats[k].toFixed(2)}</b></div>`)
        .join('');
      const s = renderer.shadows.stats;
      hud.shadows.innerHTML = `
        <div><span>shadow quads</span><b>${s.renderedQuads || 0}</b></div>
        <div><span>casters / lights</span><b>${s.casters} / ${s.lights}</b></div>
        <div><span>light tests</span><b>${s.tests}</b></div>
        <div><span>shadow texels</span><b>${s.texels}</b></div>
        <div><span>angle quantisation</span><b>${(s.maxAngleError || 0).toFixed(2)}°</b></div>
        <div><span>broad phase</span><b>${renderer.shadows.useGrid ? 'grid' : 'brute force'}</b></div>`;
      hud.cache.innerHTML = `
        <div><span>lit-sprite cache</span><b>${stats.cacheHits} hit / ${stats.cacheMisses} miss</b></div>
        <div><span>entries</span><b>${stats.cacheEntries}</b></div>
        <div><span>light texels</span><b>${stats.lightTexels}</b></div>`;
      updateLightList();
      updateInspector();
      syncLabels();
    }
    const ts = $('#time-slider');
    if (ts && document.activeElement !== ts) ts.value = scene.timeOfDay;
    if (renderer.lut.rebuilds !== lastLutRebuild) {
      lastLutRebuild = renderer.lut.rebuilds;
      drawLutStrip();
      $('#lut-label').textContent = renderer.lut.enabled
        ? `${renderer.lut.styleA}${renderer.lut.styleB !== renderer.lut.styleA ? ` → ${renderer.lut.styleB} (${Math.round(renderer.lut.fade * 100)}%)` : ''}`
        : 'identity (LUT off)';
    }
  }

  function updateLightList() {
    const rows = scene.lights.map((L, i) => {
      const on = L.enabled ? 'on' : 'off';
      return `<div class="lrow ${scene.selectedLight === L ? 'sel' : ''}" data-i="${i}">
        <i class="dot" style="background:rgb(${L.color.join(',')})"></i>
        <span class="nm">${L.label || L.kind}</span>
        <span class="meta">r${L.radius | 0} z${L.z | 0} ${(L._cur).toFixed(2)}</span>
        <button class="mini" data-act="toggle">${on}</button>
      </div>`;
    }).join('');
    lightsEl.innerHTML = rows || '<div class="empty">no lights</div>';
  }

  lightsEl.addEventListener('click', (ev) => {
    const row = ev.target.closest('.lrow');
    if (!row) return;
    const L = scene.lights[parseInt(row.dataset.i, 10)];
    if (!L) return;
    if (ev.target.dataset.act === 'toggle') { L.enabled = !L.enabled; syncLabels(); return; }
    scene.selectedLight = L;
    syncLabels();
  });

  function styleOf(c) {
    const mode = renderer.settings.shadowStyle;
    const st = (mode === 'auto') ? (c.shadowStyle || 'blob') : mode;
    return st === 'project' ? 'projected silhouette' : `${c.shadowKind} sprite`;
  }

  function updateInspector() {
    const c = scene.selectedCaster;
    if (!c) {
      inspectorEl.innerHTML = `<div class="empty">click an object in the scene (or press <kbd>tab</kbd>) to inspect its 4 shadow slots</div>`;
      return;
    }
    const rows = c.slots.map((slot, i) => {
      if (!slot.active) {
        return `<tr class="off"><td>${i === 0 ? 'sun' : `light ${i}`}</td><td colspan="4">unused</td></tr>`;
      }
      const deg = ((slot.uAngle * 180 / Math.PI) % 360).toFixed(1);
      const src = slot.isSun ? (scene.sun.isMoon ? 'moon' : 'sun') : (slot.light.label || slot.light.kind);
      return `<tr>
        <td>${i === 0 ? (scene.sun.isMoon ? 'moon' : 'sun') : `light ${i}`}<span class="src">${src}</span></td>
        <td><span class="sw" style="background:rgb(${slot.color[0]},${slot.color[1]},${slot.color[2]})"></span>
            ${slot.color[0]},${slot.color[1]},${slot.color[2]},${slot.color[3]}</td>
        <td>${deg}°</td>
        <td>${slot.uLength.toFixed(1)} px</td>
        <td>${(slot.uIntensity * 100).toFixed(0)}%</td>
      </tr>`;
    }).join('');
    inspectorEl.innerHTML = `
      <div class="ihead"><b>${c.name}</b><span>${c.usedSlots}/4 shadows · ${styleOf(c)} · h=${c.worldHeight | 0}</span></div>
      <table class="slots"><thead><tr><th>slot</th><th>vertex colour RGBA</th><th>angle</th><th>length</th><th>strength</th></tr></thead>
      <tbody>${rows}</tbody></table>`;
  }

  /* ------------------------------------------------------------------- loop */

  let last = performance.now();
  let acc = 0, frames = 0, fpsTimer = 0;

  function loop(now) {
    const dt = Math.min(0.05, (now - last) / 1000);
    last = now;

    const moveX = (keys.has('a') || keys.has('arrowleft') ? -1 : 0) + (keys.has('d') || keys.has('arrowright') ? 1 : 0);
    const moveY = (keys.has('w') || keys.has('arrowup') ? -1 : 0) + (keys.has('s') || keys.has('arrowdown') ? 1 : 0);
    scene.update(dt, { moveX, moveY, viewW: VIEW_W, viewH: VIEW_H, snapCamera: frames === 0 });

    const stats = renderer.frame(dt);
    updateHud(stats, dt);

    frames++;
    fpsTimer += dt;
    requestAnimationFrame(loop);
    void acc;
  }

  syncLabels();
  drawLutStrip();
  scene.selectedCaster = scene.casters.find((c) => c.name.startsWith('tomb_round')) || scene.casters[0];
  requestAnimationFrame(loop);

  return { scene, renderer, state };
}

if (typeof window !== 'undefined' && document.querySelector('#view')) {
  window.addEventListener('DOMContentLoaded', () => { window.GK = startApp(); });
}
