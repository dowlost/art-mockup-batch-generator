'use strict';
const $ = id => document.getElementById(id);
const state = { config: {}, scenes: [], artworks: [], selected: new Set(), cur: null, frames: [[], []], img: null };
const SPLIT_LABEL = 'Split Frame 1 artwork across both';

// ---------- helpers ----------
let toastTimer;
function toast(msg, err = false) {
  const t = $('toast'); t.textContent = msg; t.className = err ? 'err' : ''; t.hidden = false;
  clearTimeout(toastTimer); toastTimer = setTimeout(() => t.hidden = true, err ? 8000 : 3000);
}
async function api(path, opts = {}) {
  if (opts.json !== undefined) {
    opts = { method: opts.method || 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(opts.json) };
  }
  const r = await fetch('/api' + path, opts);
  if (!r.ok) {
    let msg = r.statusText;
    try { msg = (await r.json()).error || msg; } catch { /* ignore */ }
    throw new Error(msg);
  }
  const ct = r.headers.get('content-type') || '';
  if (ct.includes('application/json')) return r.json();
  if (ct.startsWith('image/')) return r.blob();
  return null;
}
const guard = fn => async (...a) => { try { await fn(...a); } catch (e) { toast(e.message, true); } };
const debounce = (fn, ms) => { let t; return (...a) => { clearTimeout(t); t = setTimeout(() => fn(...a), ms); }; };
const sceneReady = s => s.corners.length === 4 && (s.frameCount !== 2 || s.corners2.length === 4);
const isDuo = name => state.scenes.find(s => s.name === name)?.frameCount === 2;
const okArtworks = () => state.artworks.filter(a => !a.error);
/** A file written by the "Print files" tab (0001_print.png, 0001_part1.png). */
const isPrintFile = file => /._(part[12]|print)\.[^.]+$/i.test(file);
/** Refills a <select> with artwork files, keeping the current choice. `blank` adds a first option with an empty value. */
function fillArtSelect(sel, blank, value = sel.value, arts = okArtworks()) {
  sel.innerHTML = '';
  if (blank) sel.add(new Option(blank, ''));
  arts.forEach(a => sel.add(new Option(a.file, a.file)));
  if (arts.some(a => a.file === value)) sel.value = value;
}

// ---------- tabs ----------
document.querySelectorAll('.tab').forEach(b => b.onclick = () => {
  document.querySelectorAll('.tab').forEach(x => x.classList.toggle('active', x === b));
  ['scenes', 'artworks', 'generate', 'split'].forEach(n => $('tab-' + n).hidden = n !== b.dataset.tab);
  if (b.dataset.tab === 'scenes') requestAnimationFrame(() => { resizeCanvas(); fitView(); });
  if (b.dataset.tab === 'split') refreshSplit();
  if (b.dataset.tab === 'generate') (async () => {
    // don't lose unsaved corners (a half-clicked second frame cannot be saved yet)
    const [a, b2] = state.frames;
    try { if (state.cur && a.length === 4 && (frameCount() === 1 || b2.length === 4 || b2.length === 0)) await saveScene(true); }
    catch (e) { toast('Could not auto-save the scene: ' + e.message, true); }
    renderGenerate();
  })();
});

// ---------- init ----------
async function init() {
  try { state.selected = new Set(JSON.parse(localStorage.getItem('selectedArtworks') || '[]')); } catch { /* ignore */ }
  state.config = await api('/config');
  $('folderInput').value = state.config.artworkFolder;
  await loadScenes();
  await loadArtworks();
  if (state.scenes.length) selectScene(state.scenes[0].name);
  resumeRunning();
}

// ---------- scenes ----------
async function loadScenes() {
  state.scenes = await api('/scenes');
  const ul = $('sceneList'); ul.innerHTML = '';
  for (const s of state.scenes) {
    const li = document.createElement('li');
    li.className = state.cur && state.cur.name === s.name ? 'sel' : '';
    li.innerHTML = `<span></span><span class="tag">${s.frameCount === 2 ? '2 frames · ' : ''}${sceneReady(s) ? '✓ set' : 'no corners'}</span>`;
    li.firstChild.textContent = s.name;
    li.onclick = () => selectScene(s.name);
    ul.appendChild(li);
  }
}

const selectScene = guard(async name => {
  const s = state.scenes.find(x => x.name === name);
  if (!s) return;
  state.cur = structuredClone(s);
  state.frames = [s.corners, s.corners2].map(f => f.map(p => ({ ...p })));
  await loadScenes();
  fillForm();
  $('emptyScene').hidden = true;
  const img = new Image();
  img.onload = () => { state.img = img; resizeCanvas(); fitView(); updateHint(); schedulePreview(); };
  img.onerror = () => toast('Could not load the scene image.', true);
  img.src = `/api/scenes/${encodeURIComponent(s.name)}/image?v=${Date.now()}`;
});

$('uploadForm').onsubmit = guard(async e => {
  e.preventDefault();
  const fd = new FormData();
  fd.append('file', $('sceneFile').files[0]);
  fd.append('name', $('sceneNameNew').value);
  const s = await api('/scenes', { method: 'POST', body: fd });
  $('uploadForm').reset();
  await loadScenes();
  selectScene(s.name);
  toast('Scene added. Now click the 4 corners.');
});

function fillForm() {
  const s = state.cur.settings;
  $('sName').value = state.cur.name;
  $('sFrames').value = state.cur.frameCount;
  $('sFit').value = s.fitMode; $('sMatte').value = s.matteColor;
  $('sLight').value = s.lightingStrength; $('sGlare').value = s.glareStrength; $('sFeather').value = s.edgeFeatherPx;
  $('sAspect').value = state.cur.expectedAspectRatio ?? '';
  syncOutputs();
}
function syncOutputs() {
  $('oLight').textContent = (+$('sLight').value).toFixed(2);
  $('oGlare').textContent = (+$('sGlare').value).toFixed(2);
  $('oFeather').textContent = $('sFeather').value;
  $('matteRow').style.display = $('sFit').value === 'contain' ? '' : 'none';
  $('pvArt2Row').hidden = frameCount() !== 2;
  $('pvArtLabel').textContent = frameCount() === 2 ? 'Frame 1 artwork' : 'Artwork';
}
const frameCount = () => +$('sFrames').value;
const usedFrames = () => state.frames.slice(0, frameCount());
const framesReady = () => usedFrames().every(f => f.length === 4);
function readSettings() {
  return {
    fitMode: $('sFit').value, matteColor: $('sMatte').value,
    lightingStrength: +$('sLight').value, glareStrength: +$('sGlare').value, edgeFeatherPx: +$('sFeather').value,
  };
}
['sFit', 'sMatte', 'sLight', 'sGlare', 'sFeather'].forEach(id => $(id).addEventListener('input', () => {
  syncOutputs(); if (state.cur) { state.cur.settings = readSettings(); schedulePreview(); }
}));
$('sAspect').addEventListener('input', () => { if (state.cur) { state.cur.expectedAspectRatio = +$('sAspect').value || null; checkAspect(); } });
$('sFrames').addEventListener('change', () => { syncOutputs(); updateHint(); draw(); schedulePreview(); });

$('btnSave').onclick = guard(() => saveScene(false));
async function saveScene(silent) {
  if (!state.cur) return;
  const body = {
    name: $('sName').value.trim(), corners: state.frames[0], settings: readSettings(),
    frameCount: frameCount(), corners2: frameCount() === 2 ? state.frames[1] : null, // null keeps what is saved
    expectedAspectRatio: +$('sAspect').value || null, clearExpectedAspectRatio: !+$('sAspect').value,
  };
  const saved = await api(`/scenes/${encodeURIComponent(state.cur.name)}`, { method: 'PUT', json: body });
  state.cur.name = saved.name;
  await loadScenes();
  if (!silent) toast('Scene saved.');
}

$('btnDelete').onclick = guard(async () => {
  if (!state.cur || !confirm(`Delete scene "${state.cur.name}" and its image? Generated outputs are kept.`)) return;
  await api(`/scenes/${encodeURIComponent(state.cur.name)}`, { method: 'DELETE' });
  state.cur = null; state.img = null; state.frames = [[], []];
  await loadScenes(); draw(); $('emptyScene').hidden = false;
  if (state.scenes.length) selectScene(state.scenes[0].name);
});

$('btnResetCorners').onclick = () => { state.frames = [[], []]; updateHint(); draw(); };
$('btnFit').onclick = () => { fitView(); };

function updateHint() {
  const names = ['top-left', 'top-right', 'bottom-right', 'bottom-left'];
  const f = usedFrames().findIndex(x => x.length < 4);
  let text = 'All corners set — drag to fine-tune, then Save.';
  if (f >= 0) {
    const n = state.frames[f].length;
    text = `${frameCount() === 2 ? `Frame ${f + 1}: click` : 'Click'} corner ${n + 1} of 4: ${names[n]}`;
  }
  $('cornerHint').textContent = state.img ? text : '';
}

// ---------- canvas ----------
const cv = $('cv'), ctx = cv.getContext('2d');
const view = { s: 1, ox: 0, oy: 0 };
function resizeCanvas() {
  const r = cv.getBoundingClientRect(), dpr = devicePixelRatio || 1;
  cv.width = Math.max(1, r.width * dpr); cv.height = Math.max(1, r.height * dpr); draw();
}
function fitView() {
  if (!state.img) return draw();
  const r = cv.getBoundingClientRect();
  view.s = Math.min(r.width / state.img.naturalWidth, r.height / state.img.naturalHeight);
  view.ox = (r.width - state.img.naturalWidth * view.s) / 2;
  view.oy = (r.height - state.img.naturalHeight * view.s) / 2;
  draw();
}
const toImg = (x, y) => ({ x: (x - view.ox) / view.s, y: (y - view.oy) / view.s });
const toScr = p => ({ x: p.x * view.s + view.ox, y: p.y * view.s + view.oy });
function draw() {
  const dpr = devicePixelRatio || 1;
  ctx.setTransform(1, 0, 0, 1, 0, 0); ctx.clearRect(0, 0, cv.width, cv.height);
  if (!state.img) return;
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.imageSmoothingQuality = 'high';
  ctx.drawImage(state.img, view.ox, view.oy, state.img.naturalWidth * view.s, state.img.naturalHeight * view.s);
  usedFrames().forEach((corners, f) => drawFrame(corners.map(toScr), f));
}
const frameColors = [{ line: '#ff4d4d', fill: 'rgba(255,80,80,.18)' }, { line: '#2f7dff', fill: 'rgba(47,125,255,.2)' }];
function drawFrame(pts, f) {
  const color = frameColors[f];
  ctx.textAlign = 'center'; ctx.textBaseline = 'middle';
  if (pts.length > 1) {
    ctx.beginPath(); pts.forEach((p, i) => i ? ctx.lineTo(p.x, p.y) : ctx.moveTo(p.x, p.y));
    if (pts.length === 4) { ctx.closePath(); ctx.fillStyle = color.fill; ctx.fill(); }
    ctx.strokeStyle = color.line; ctx.lineWidth = 2; ctx.stroke();
  }
  if (pts.length === 4 && frameCount() === 2) {
    const cx = pts.reduce((s, p) => s + p.x, 0) / 4, cy = pts.reduce((s, p) => s + p.y, 0) / 4;
    ctx.fillStyle = color.line; ctx.fillRect(cx - 34, cy - 11, 68, 22);
    ctx.fillStyle = '#fff'; ctx.font = 'bold 12px system-ui'; ctx.fillText(`Frame ${f + 1}`, cx, cy + 0.5);
  }
  pts.forEach((p, i) => {
    ctx.beginPath(); ctx.arc(p.x, p.y, 7, 0, Math.PI * 2); ctx.fillStyle = color.line; ctx.fill();
    ctx.strokeStyle = '#fff'; ctx.lineWidth = 2; ctx.stroke();
    ctx.fillStyle = '#fff'; ctx.font = 'bold 11px system-ui';
    ctx.fillText(i + 1, p.x, p.y + 0.5);
  });
}
window.addEventListener('resize', () => { resizeCanvas(); });

let drag = null;
/** The corner under the pointer as {f: frame, i: corner}, or null. */
function hit(x, y) {
  for (const [f, corners] of usedFrames().entries()) {
    const i = corners.findIndex(c => { const p = toScr(c); return Math.hypot(p.x - x, p.y - y) <= 11; });
    if (i >= 0) return { f, i };
  }
  return null;
}
function localXY(e) { const r = cv.getBoundingClientRect(); return [e.clientX - r.left, e.clientY - r.top]; }
cv.addEventListener('pointerdown', e => {
  if (!state.img) return;
  cv.setPointerCapture(e.pointerId);
  const [x, y] = localXY(e), h = hit(x, y);
  drag = h ? { type: 'pt', ...h } : { type: 'pan', x, y, ox: view.ox, oy: view.oy, moved: false };
});
cv.addEventListener('pointermove', e => {
  if (!state.img) return;
  const [x, y] = localXY(e);
  if (!drag) { cv.style.cursor = hit(x, y) ? 'grab' : 'crosshair'; return; }
  if (drag.type === 'pt') {
    const p = toImg(x, y), W = state.img.naturalWidth, H = state.img.naturalHeight;
    state.frames[drag.f][drag.i] = { x: Math.min(W, Math.max(0, p.x)), y: Math.min(H, Math.max(0, p.y)) };
  } else {
    if (Math.hypot(x - drag.x, y - drag.y) > 4) drag.moved = true;
    if (drag.moved) { view.ox = drag.ox + x - drag.x; view.oy = drag.oy + y - drag.y; }
  }
  draw();
});
cv.addEventListener('pointerup', e => {
  if (!drag) return;
  const [x, y] = localXY(e), d = drag; drag = null;
  const open = usedFrames().find(f => f.length < 4);
  if (d.type === 'pan' && !d.moved && open) {
    const p = toImg(x, y), W = state.img.naturalWidth, H = state.img.naturalHeight;
    if (p.x >= 0 && p.y >= 0 && p.x <= W && p.y <= H) open.push(p);
    updateHint(); draw();
  }
  if (framesReady()) schedulePreview();
});
cv.addEventListener('wheel', e => {
  if (!state.img) return;
  e.preventDefault();
  const [x, y] = localXY(e), before = toImg(x, y);
  view.s = Math.min(40, Math.max(0.02, view.s * (e.deltaY < 0 ? 1.15 : 1 / 1.15)));
  view.ox = x - before.x * view.s; view.oy = y - before.y * view.s; draw();
}, { passive: false });

// ---------- preview ----------
let pvSeq = 0, pvUrl = null;
const schedulePreview = debounce(() => renderPreview(), 300);
async function renderPreview(sheet = false) {
  const msg = $('pvMsg'), img = $('pvImg');
  if (!state.cur) return;
  if (!framesReady()) {
    msg.textContent = frameCount() === 2 ? 'Set the 4 corners of both frames to preview.' : 'Set 4 corners to preview.';
    msg.hidden = false; img.removeAttribute('src'); return;
  }
  const art = $('pvArt').value;
  if (!art) { msg.textContent = 'Add artworks to preview.'; msg.hidden = false; return; }
  const seq = ++pvSeq;
  msg.textContent = 'Rendering…'; msg.hidden = false;
  try {
    const duo = frameCount() === 2;
    const common = {
      scene: state.cur.name, settings: readSettings(),
      frameCount: frameCount(), corners: state.frames[0], corners2: duo ? state.frames[1] : null,
    };
    const blob = sheet
      ? await api('/contact-sheet', { json: { ...common, artworks: previewArtworks().slice(0, 6) } })
      : await api('/preview', { json: { ...common, artwork: art, artwork2: duo ? $('pvArt2').value || null : null } });
    if (seq !== pvSeq) return;
    if (pvUrl) URL.revokeObjectURL(pvUrl);
    pvUrl = URL.createObjectURL(blob); img.src = pvUrl; msg.hidden = true;
  } catch (e) { if (seq === pvSeq) { msg.textContent = e.message; img.removeAttribute('src'); } }
}
function previewArtworks() {
  const sel = state.artworks.filter(a => !a.error && state.selected.has(a.file)).map(a => a.file);
  return sel.length ? sel : state.artworks.filter(a => !a.error).map(a => a.file);
}
$('btnPreview').onclick = () => renderPreview(false);
$('btnSheet').onclick = () => renderPreview(true);
$('pvArt').onchange = () => { checkAspect(); renderPreview(); };
$('pvArt2').onchange = () => renderPreview();
function checkAspect() {
  const w = $('aspectWarn'), exp = state.cur?.expectedAspectRatio;
  const a = state.artworks.find(x => x.file === $('pvArt').value);
  if (exp && a && a.width) {
    const ratio = (a.width / a.height) / exp;
    if (ratio < 0.85 || ratio > 1.15) {
      w.textContent = `This artwork's aspect (${(a.width / a.height).toFixed(2)}) differs a lot from the scene's expected ${exp}.`;
      w.hidden = false; return;
    }
  }
  w.hidden = true;
}

// ---------- artworks ----------
async function loadArtworks() {
  let loaded = true;
  try { state.artworks = await api('/artworks'); }
  catch (e) { loaded = false; state.artworks = []; toast(e.message, true); }
  const valid = new Set(state.artworks.map(a => a.file));
  state.selected = new Set([...state.selected].filter(f => valid.has(f)));
  if (!localStorage.getItem('selectedArtworks')) state.artworks.forEach(a => { if (!a.error) state.selected.add(a.file); });
  persistSelection(); renderArtworks();
  if (loaded) { // drop pairs whose files are gone
    const ok = new Set(okArtworks().map(a => a.file));
    pairs = pairs.filter(p => ok.has(p.first) && (!p.second || ok.has(p.second)));
    savePairs();
  }
  fillArtSelect($('pvArt'));
  fillArtSelect($('pvArt2'), SPLIT_LABEL);
  const spBefore = $('spArt').value;
  fillArtSelect($('spArt'), '', spBefore, okArtworks().filter(a => !isPrintFile(a.file))); // print files are not processed again
  if ($('spArt').value !== spBefore) resetSplitForArtwork();
}
function persistSelection() { localStorage.setItem('selectedArtworks', JSON.stringify([...state.selected])); }
function renderArtworks() {
  const g = $('artGrid'); g.innerHTML = '';
  for (const a of state.artworks) {
    const t = document.createElement('div');
    t.className = 'tile' + (state.selected.has(a.file) ? ' on' : '') + (a.error ? ' bad' : '');
    const img = a.error ? '' : `<img loading="lazy" src="/api/artworks/${encodeURIComponent(a.file)}/thumb" alt="">`;
    t.innerHTML = `${a.error ? '' : '<input type="checkbox">'}${img}<div class="nm"></div>` +
      (a.error ? '<div class="er"></div>' : `<div class="dm">${a.width}×${a.height}</div>`);
    t.querySelector('.nm').textContent = a.file; t.querySelector('.nm').title = a.file;
    if (a.error) t.querySelector('.er').textContent = a.error;
    else {
      const cb = t.querySelector('input'); cb.checked = state.selected.has(a.file);
      const toggle = () => { cb.checked ? state.selected.add(a.file) : state.selected.delete(a.file); t.classList.toggle('on', cb.checked); persistSelection(); updateArtCount(); };
      cb.onclick = e => { e.stopPropagation(); toggle(); };
      t.onclick = () => { cb.checked = !cb.checked; toggle(); };
    }
    g.appendChild(t);
  }
  updateArtCount();
}
function updateArtCount() { $('artCount').textContent = `${state.selected.size} of ${state.artworks.filter(a => !a.error).length} selected`; }
$('btnAll').onclick = () => { state.artworks.forEach(a => !a.error && state.selected.add(a.file)); persistSelection(); renderArtworks(); };
$('btnNone').onclick = () => { state.selected.clear(); persistSelection(); renderArtworks(); };
$('btnReload').onclick = guard(loadArtworks);
$('btnFolder').onclick = guard(async () => {
  const r = await api('/config', { method: 'PUT', json: { artworkFolder: $('folderInput').value.trim() } });
  $('folderInput').value = r.artworkFolder; localStorage.removeItem('selectedArtworks'); state.selected.clear();
  pairs = []; savePairs();
  await loadArtworks(); toast('Artwork folder updated.');
});

// ---------- generate (still images + zoom videos in one flow) ----------
const genOff = new Set(); // scenes unticked for images (default: every ready scene is ticked)
let vidOn = new Set(); // scenes ticked for videos (default: none, videos are slower)
try { vidOn = new Set(JSON.parse(localStorage.getItem('videoScenes') || '[]')); } catch { /* ignore */ }
const saveVidOn = () => { try { localStorage.setItem('videoScenes', JSON.stringify([...vidOn])); } catch { /* ignore */ } };
const pickedScenes = () => state.scenes.filter(s => sceneReady(s) && !genOff.has(s.name)).map(s => s.name);
const pickedVideoScenes = () => state.scenes.filter(s => sceneReady(s) && vidOn.has(s.name)).map(s => s.name);

function renderGenerate() {
  const box = $('genScenes');
  box.innerHTML = '';
  if (!state.scenes.some(sceneReady)) {
    box.innerHTML = '<div class="warn">No scene is ready yet. Go to the Scenes tab, upload a photo and click its 4 corners.</div>';
  }
  for (const s of state.scenes) {
    const ready = sceneReady(s);
    const row = document.createElement('div'); row.className = 'genGrid';
    const name = document.createElement('span'); name.textContent = s.name + (s.frameCount === 2 ? ' · 2 frames' : '') + (ready ? '' : ' (set corners first)');
    const mk = (on, set, onChange) => {
      const cb = document.createElement('input'); cb.type = 'checkbox'; cb.disabled = !ready; cb.checked = ready && on;
      cb.onchange = () => { onChange(cb.checked); updateSummary(); };
      return cb;
    };
    row.append(name,
      mk(!genOff.has(s.name), genOff, on => on ? genOff.delete(s.name) : genOff.add(s.name)),
      mk(vidOn.has(s.name), vidOn, on => { on ? vidOn.add(s.name) : vidOn.delete(s.name); saveVidOn(); }));
    box.appendChild(row);
  }
  renderPairs();
  synFillLists();
  updateSummary();
}

// --- artwork pairs for two-frame scenes ({first, second}; an empty second splits the first across both frames) ---
let pairs = [];
try { pairs = JSON.parse(localStorage.getItem('artPairs') || '[]').filter(p => p && p.first); } catch { /* ignore */ }
function savePairs() { try { localStorage.setItem('artPairs', JSON.stringify(pairs)); } catch { /* ignore */ } }
/** The pairs without duplicates, as sent to the server. */
const pickedPairs = () => [...new Map(pairs.map(p => [`${p.first}|${p.second}`, { first: p.first, second: p.second || null }])).values()];

function renderPairs() {
  $('pairBox').hidden = !state.scenes.some(s => s.frameCount === 2);
  const list = $('pairList'); list.innerHTML = '';
  pairs.forEach((pair, i) => {
    const row = document.createElement('div'); row.className = 'row pairRow';
    const pick = (text, key, blank) => {
      const label = document.createElement('label'); label.className = 'inline'; label.append(text);
      const sel = document.createElement('select');
      fillArtSelect(sel, blank, pair[key]);
      sel.onchange = () => { pair[key] = sel.value; savePairs(); updateSummary(); };
      label.appendChild(sel);
      return label;
    };
    const remove = document.createElement('button'); remove.type = 'button'; remove.textContent = 'Remove';
    remove.onclick = () => { pairs.splice(i, 1); savePairs(); renderPairs(); updateSummary(); };
    row.append(pick('Frame 1', 'first'), pick('Frame 2', 'second', SPLIT_LABEL), remove);
    list.appendChild(row);
  });
}
$('btnAddPair').onclick = guard(() => {
  const all = okArtworks().map(a => a.file);
  if (!all.length) throw new Error('Add artworks first.');
  const used = new Set(pairs.flatMap(p => [p.first, p.second]));
  const free = previewArtworks().filter(f => !used.has(f)); // start from the ticked artworks not paired yet
  pairs.push({ first: free[0] ?? all[0], second: free[1] ?? '' });
  savePairs(); renderPairs(); updateSummary();
});
$('btnScAll').onclick = () => { genOff.clear(); renderGenerate(); };
$('btnScNone').onclick = () => { state.scenes.forEach(s => genOff.add(s.name)); renderGenerate(); };
$('btnVidScAll').onclick = () => { state.scenes.filter(sceneReady).forEach(s => vidOn.add(s.name)); saveVidOn(); renderGenerate(); };
$('btnVidScNone').onclick = () => { vidOn.clear(); saveVidOn(); renderGenerate(); };

function updateSummary() {
  const imgs = pickedScenes(), vids = pickedVideoScenes(), a = state.selected.size, p = pickedPairs().length;
  const describe = (names, noun) => {
    const duo = names.filter(isDuo).length, solo = names.length - duo, how = [];
    if (solo) how.push(`${solo} scene(s) × ${a} artwork(s)`);
    if (duo) how.push(`${duo} two-frame scene(s) × ${p} pair(s)`);
    return `${solo * a + duo * p} ${noun}(s) (${how.join(' + ')})`;
  };
  const parts = [];
  if (imgs.length) parts.push(describe(imgs, 'image'));
  if (vids.length) parts.push(describe(vids, 'video'));
  let text = parts.length ? 'Will generate ' + parts.join(' and ') + '.' : 'Tick at least one scene under Images or Videos.';
  const all = [...imgs, ...vids];
  if (!a && all.some(n => !isDuo(n))) text += ' Select artworks in the Artworks tab.';
  if (!p && all.some(isDuo)) text += ' Add a two-frame pair above.';
  $('genSummary').textContent = text;
}

$('chkFull').onchange = () => {
  const full = $('chkFull').checked, o = $('selFormat').options;
  o[0].textContent = full ? 'JPEG (quality 100, full detail)' : 'JPEG (≤ 2 MB each)';
  o[1].textContent = full ? 'PNG (lossless, largest files)' : 'PNG (≤ 2 MB, resized if needed)';
};

// --- video options + preview ---
function synFillLists() {
  const sc = $('synScene'), ks = sc.value; sc.innerHTML = '';
  const ready = state.scenes.filter(sceneReady);
  ready.forEach(s => sc.add(new Option(s.name, s.name)));
  if (ks && ready.some(s => s.name === ks)) sc.value = ks;
  fillArtSelect($('synArt'));
  fillArtSelect($('synArt2'), SPLIT_LABEL);
  synSecondArt();
  synInfo();
}
const synSecondArt = () => { $('synArt2Row').hidden = !isDuo($('synScene').value); };
$('synScene').addEventListener('change', synSecondArt);
const synOptions = () => ({
  scene: $('synScene').value, orientation: $('synOri').value, seconds: +$('synSec').value, fps: +$('synFps').value,
  endFill: +$('synFill').value, ease: $('synEase').checked, loop: $('synLoop').checked, overwrite: $('chkOverwrite').checked,
});
const synInfo = debounce(guard(async () => {
  $('synFillOut').textContent = Math.round($('synFill').value * 100) + '%';
  if (!$('synScene').value) { $('synInfo').textContent = ''; return; }
  const i = await api('/videos/synthetic/info', { json: synOptions() });
  $('synInfo').textContent = `${i.outWidth}×${i.outHeight} · end-of-zoom upscale ${i.endUpscale.toFixed(1)}×` + (i.endUpscale > 1.5 ? ' (soft: lower End fill or use a bigger scene photo)' : ' (sharp)');
}), 250);
['synScene', 'synOri', 'synFill', 'synEase'].forEach(id => $(id).addEventListener('input', synInfo));
$('btnSynPreview').onclick = guard(async () => {
  if (!$('synScene').value) throw new Error('No scene with 4 corners. Set one up in the Scenes tab.');
  const art = $('synArt').value;
  if (!art) throw new Error('Pick an artwork to preview.');
  const o = synOptions();
  const artwork2 = isDuo(o.scene) ? $('synArt2').value || null : null;
  for (const [i, t] of [0, 0.5, 1].entries()) {
    const blob = await api('/videos/synthetic/preview', { json: { options: o, artwork: art, artwork2, t } });
    $('synPv' + i).src = URL.createObjectURL(blob);
  }
});

// --- running jobs ---
let activeCancel = null, cancelRequested = false;
const sleep = ms => new Promise(r => setTimeout(r, ms));
const imgUi = { bar: 'bar', text: 'progText', errBox: 'errBox', errList: 'errList' };
const vidUi = { bar: 'synBar', text: 'synProg', errBox: 'synErrBox', errList: 'synErrList' };

/** Polls a job until it finishes and keeps its progress widgets up to date. */
async function watchJob(pollPath, cancelPath, ui, noun) {
  activeCancel = cancelPath;
  try {
    for (;;) {
      const j = await api(pollPath);
      $(ui.bar).style.width = (j.total ? 100 * j.done / j.total : 0) + '%';
      const info = `${j.rendered} ${noun}(s) done, ${j.skipped} skipped, ${j.failed} failed`;
      $(ui.text).textContent = j.status === 'running' ? `${info}  ·  ${j.current}` : `${j.status.toUpperCase()}: ${info} (${j.seconds.toFixed(1)}s)`;
      const list = $(ui.errList); list.innerHTML = '';
      j.errors.forEach(er => { const li = document.createElement('li'); li.textContent = `${er.artwork || ''}${er.scene ? ' → ' + er.scene : ''}: ${er.message}`; list.appendChild(li); });
      $(ui.errBox).hidden = j.errors.length === 0;
      if (j.status !== 'running') return j;
      await sleep(600);
    }
  } finally { activeCancel = null; }
}
const setBusy = busy => { $('btnGenerate').disabled = busy; $('btnCancel').disabled = !busy; };

$('btnGenerate').onclick = guard(async () => {
  const imgs = pickedScenes(), vids = pickedVideoScenes(), arts = [...state.selected];
  if (!imgs.length && !vids.length) throw new Error('No scene ticked. Tick scenes under Images and/or Videos (scenes need their 4 corners set in the Scenes tab).');
  const chosen = pickedPairs(), all = [...imgs, ...vids];
  if (!arts.length && all.some(n => !isDuo(n))) throw new Error('No artwork selected. Tick artworks in the Artworks tab.');
  if (!chosen.length && all.some(isDuo)) throw new Error('A two-frame scene is ticked. Add at least one pair under Two-frame pairs.');
  cancelRequested = false; setBusy(true);
  $('errBox').hidden = true; $('synErrBox').hidden = true;
  $('bar').style.width = '0%'; $('synBar').style.width = '0%'; $('progText').textContent = ''; $('synProg').textContent = '';
  try {
    if (imgs.length) {
      const job = await api('/batch', { json: { scenes: imgs, artworks: arts, pairs: chosen, overwrite: $('chkOverwrite').checked, format: $('selFormat').value, fullQuality: $('chkFull').checked } });
      await watchJob('/batch/' + job.id, `/batch/${job.id}/cancel`, imgUi, 'image');
    }
    if (vids.length && !cancelRequested) {
      const job = await api('/videos/synthetic', { json: { ...synOptions(), scenes: vids, artworks: arts, pairs: chosen } });
      await watchJob('/videos/jobs/' + job.id, `/videos/jobs/${job.id}/cancel`, vidUi, 'video');
    }
  } finally { setBusy(false); updateSummary(); }
});
$('btnCancel').onclick = guard(async () => { cancelRequested = true; if (activeCancel) await api(activeCancel, { method: 'POST' }); });

/** After a page reload: re-attach to an image batch that is still running. */
async function resumeRunning() {
  const running = await api('/batch').catch(() => null);
  if (running && running.status === 'running') {
    setBusy(true);
    try { await watchJob('/batch/' + running.id, `/batch/${running.id}/cancel`, imgUi, 'image'); } finally { setBusy(false); }
  }
}
$('btnOpenOut').onclick = guard(() => api('/output/open', { method: 'POST' }));

// ---------- rename artworks to the 0000 format ----------
$('btnRenameArt').onclick = guard(async () => {
  const plan = await api('/artworks/normalize?dryRun=true', { method: 'POST' });
  if (!plan.count) { toast('All artworks already use the 0000 format.'); return; }
  const sample = plan.items.slice(0, 6).map(i => i.from + '  ->  ' + i.to).join('\n');
  if (!confirm('Rename ' + plan.count + ' artwork(s) to the next free numbers?\n\n' + sample + (plan.count > 6 ? '\n...' : ''))) return;
  const r = await api('/artworks/normalize', { method: 'POST' });
  r.items.forEach(i => { if (state.selected.delete(i.from)) state.selected.add(i.to); });
  const renamed = new Map(r.items.map(i => [i.from, i.to]));
  pairs = pairs.map(p => ({ first: renamed.get(p.first) ?? p.first, second: renamed.get(p.second) ?? p.second }));
  persistSelection(); savePairs();
  await loadArtworks();
  toast('Renamed ' + r.count + ' artwork(s). The old names are listed in data/artworks-rename-map.csv.');
});

// ---------- print files: the whole artwork, or split in two ----------
const spOptions = () => ({
  artwork: $('spArt').value, layout: $('spLayout').value, offset: +$('spOffset').value,
  dpi: +$('spDpi').value, format: $('spFormat').value, ai: $('spAi').checked,
});
/** A new artwork starts centred. A split layout switches to the one that crops the least; "not split" stays. */
function resetSplitForArtwork() {
  const a = state.artworks.find(x => x.file === $('spArt').value);
  if (a && $('spLayout').value !== 'full') $('spLayout').value = a.width >= a.height ? 'side' : 'stacked';
  $('spOffset').value = 0.5;
  $('spResult').textContent = '';
}
let spSeq = 0, spUrl = null;
async function refreshSplit() {
  const o = spOptions(), seq = ++spSeq, msg = $('spMsg'), img = $('spImg');
  $('spOffsetOut').textContent = o.offset === 0.5 ? 'centred' : Math.round(o.offset * 100) + '%';
  const clear = text => { msg.textContent = text; msg.hidden = false; img.removeAttribute('src'); $('spInfo').textContent = ''; $('spWarn').hidden = true; };
  if (!o.artwork) return clear('Add artworks first.');
  try {
    const [info, blob] = await Promise.all([api('/split/info', { json: o }), api('/split/preview', { json: o })]);
    if (seq !== spSeq) return;
    if (spUrl) URL.revokeObjectURL(spUrl);
    spUrl = URL.createObjectURL(blob); img.src = spUrl; msg.hidden = true;
    const cropped = info.croppedPercent < 0.5 ? 'almost nothing cropped' : `${info.croppedPercent.toFixed(0)}% of the artwork cropped away (move Crop position to choose what stays)`;
    const one = info.pieces === 1;
    $('btnSplit').textContent = one ? 'Export 1 file' : 'Export 2 files';
    $('spInfo').textContent = `${one ? '1 print' : '2 prints'} of ${info.fileCmWidth}×${info.fileCmHeight} cm (${info.artCmWidth}×${info.artCmHeight} cm art + ${info.borderCm} cm white border), ${info.fileWidth}×${info.fileHeight} px${one ? '' : ' each'} · artwork ${info.sourceWidth}×${info.sourceHeight} px · ${cropped}.` +
      (info.usesAi ? ' AI detail shows in the exported files, not in this preview.' : '');
    if (!info.aiAvailable) $('spAi').checked = false;
    $('spAi').disabled = !info.aiAvailable;
    $('spAiNote').textContent = !info.aiAvailable ? '— Upscayl is not installed'
      : !o.dpi ? '— not used with original pixels' : '';
    const low = info.sourceDpi < 150;
    $('spWarn').hidden = !low;
    if (low) {
      const advice = info.usesAi
        ? `AI upscaling will sharpen it to about ${Math.round(info.aiDpi)} DPI. That detail is generated by the model, so check the files before printing.`
        : (o.dpi ? 'Resizing adds pixels but no new detail.' : 'The print may look soft.') +
          (info.aiAvailable ? ' For a sharper print, choose a DPI and tick AI upscale.' : ' For a sharper print, use a larger source artwork.');
      $('spWarn').textContent = `Low resolution: at this size the artwork has about ${Math.round(info.sourceDpi)} DPI of real detail (150 or more is usually recommended). ${advice}`;
    }
  } catch (e) { if (seq === spSeq) clear(e.message); }
}
const scheduleSplit = debounce(() => refreshSplit(), 250);
$('spArt').onchange = () => { resetSplitForArtwork(); refreshSplit(); };
['spLayout', 'spDpi', 'spOffset', 'spAi'].forEach(id => $(id).addEventListener('input', () => { $('spResult').textContent = ''; scheduleSplit(); }));
$('btnSplit').onclick = guard(async () => {
  if (!$('spArt').value) throw new Error('Pick an artwork.');
  const button = $('btnSplit');
  const o = spOptions();
  button.disabled = true;
  try {
    const { existing } = await api('/split/info', { json: o });
    if (existing.length && !confirm(`Replace these files in the artworks folder?\n\n${existing.join('\n')}`)) return;
    $('spResult').textContent = o.ai && o.dpi ? 'Exporting… AI upscaling can take about half a minute.' : 'Exporting… large print files can take a little while.';
    const r = await api('/split/export', { json: { ...o, overwrite: existing.length > 0 } });
    $('spResult').textContent = `Saved in ${r.folder}: ` + r.files.map(f => `${f.name} (${f.width}×${f.height}, ${(f.bytes / 1e6).toFixed(1)} MB)`).join(', ');
    await loadArtworks(); // the new files are artworks too
  } catch (e) { $('spResult').textContent = ''; throw e; }
  finally { button.disabled = false; }
});
$('btnOpenParts').onclick = guard(() => api('/split/open', { method: 'POST' }));

init().catch(e => toast(e.message, true));
