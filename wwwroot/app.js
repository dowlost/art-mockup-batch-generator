'use strict';
const $ = id => document.getElementById(id);
const state = { config: {}, scenes: [], artworks: [], selected: new Set(), cur: null, corners: [], img: null };

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

// ---------- tabs ----------
document.querySelectorAll('.tab').forEach(b => b.onclick = () => {
  document.querySelectorAll('.tab').forEach(x => x.classList.toggle('active', x === b));
  ['scenes', 'artworks', 'generate'].forEach(n => $('tab-' + n).hidden = n !== b.dataset.tab);
  if (b.dataset.tab === 'scenes') requestAnimationFrame(() => { resizeCanvas(); fitView(); });
  if (b.dataset.tab === 'generate') (async () => {
    try { if (state.cur && state.corners.length === 4) await saveScene(true); } // don't lose unsaved corners
    catch (e) { toast('Could not auto-save the scene: ' + e.message, true); }
    renderGenerate();
  })();
});

// ---------- init ----------
async function init() {
  try { state.selected = new Set(JSON.parse(localStorage.getItem('selectedArtworks') || '[]')); } catch { /* ignore */ }
  state.config = await api('/config');
  $('captionPrompt').value = state.config.captionPrompt;
  $('folderInput').value = state.config.artworkFolder;
  const ai = state.config.aiEnabled;
  document.querySelectorAll('.aiBtn').forEach(b => {
    b.disabled = !ai; b.classList.toggle('off', !ai);
    if (!ai) b.title = 'Set ANTHROPIC_API_KEY in .env and restart to enable AI features.';
  });
  $('aiNote').hidden = ai;
  await loadScenes();
  await loadArtworks();
  if (state.scenes.length) selectScene(state.scenes[0].name);
  const running = await api('/batch').catch(() => null);
  if (running && running.status === 'running') pollBatch(running.id);
}

// ---------- scenes ----------
async function loadScenes() {
  state.scenes = await api('/scenes');
  const ul = $('sceneList'); ul.innerHTML = '';
  for (const s of state.scenes) {
    const li = document.createElement('li');
    li.className = state.cur && state.cur.name === s.name ? 'sel' : '';
    li.innerHTML = `<span></span><span class="tag">${s.corners.length === 4 ? '✓ set' : 'no corners'}</span>`;
    li.firstChild.textContent = s.name;
    li.onclick = () => selectScene(s.name);
    ul.appendChild(li);
  }
}

const selectScene = guard(async name => {
  const s = state.scenes.find(x => x.name === name);
  if (!s) return;
  state.cur = structuredClone(s);
  state.corners = s.corners.map(p => ({ ...p }));
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
}
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

$('btnSave').onclick = guard(() => saveScene(false));
async function saveScene(silent) {
  if (!state.cur) return;
  const body = {
    name: $('sName').value.trim(), corners: state.corners, settings: readSettings(),
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
  state.cur = null; state.img = null; state.corners = [];
  await loadScenes(); draw(); $('emptyScene').hidden = false;
  if (state.scenes.length) selectScene(state.scenes[0].name);
});

$('btnResetCorners').onclick = () => { state.corners = []; updateHint(); draw(); };
$('btnAiCorners').onclick = guard(async () => {
  if (!state.cur) return;
  toast('Asking Claude to find the frame…');
  const r = await api('/ai/detect-corners', { json: { scene: state.cur.name } });
  state.corners = r.corners; updateHint(); draw(); schedulePreview();
  toast('Suggestion placed. Check it and drag points to adjust before saving.');
});
$('btnFit').onclick = () => { fitView(); };

function updateHint() {
  const n = state.corners.length;
  const names = ['top-left', 'top-right', 'bottom-right', 'bottom-left'];
  $('cornerHint').textContent = !state.img ? '' : n < 4 ? `Click corner ${n + 1} of 4: ${names[n]}` : 'All 4 corners set — drag to fine-tune, then Save.';
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
  const pts = state.corners.map(toScr);
  if (pts.length > 1) {
    ctx.beginPath(); pts.forEach((p, i) => i ? ctx.lineTo(p.x, p.y) : ctx.moveTo(p.x, p.y));
    if (pts.length === 4) { ctx.closePath(); ctx.fillStyle = 'rgba(255,80,80,.18)'; ctx.fill(); }
    ctx.strokeStyle = '#ff4d4d'; ctx.lineWidth = 2; ctx.stroke();
  }
  pts.forEach((p, i) => {
    ctx.beginPath(); ctx.arc(p.x, p.y, 7, 0, Math.PI * 2); ctx.fillStyle = '#ff4d4d'; ctx.fill();
    ctx.strokeStyle = '#fff'; ctx.lineWidth = 2; ctx.stroke();
    ctx.fillStyle = '#fff'; ctx.font = 'bold 11px system-ui'; ctx.textAlign = 'center'; ctx.textBaseline = 'middle';
    ctx.fillText(i + 1, p.x, p.y + 0.5);
  });
}
window.addEventListener('resize', () => { resizeCanvas(); });

let drag = null;
function hit(x, y) {
  return state.corners.findIndex(c => { const p = toScr(c); return Math.hypot(p.x - x, p.y - y) <= 11; });
}
function localXY(e) { const r = cv.getBoundingClientRect(); return [e.clientX - r.left, e.clientY - r.top]; }
cv.addEventListener('pointerdown', e => {
  if (!state.img) return;
  cv.setPointerCapture(e.pointerId);
  const [x, y] = localXY(e), i = hit(x, y);
  drag = i >= 0 ? { type: 'pt', i } : { type: 'pan', x, y, ox: view.ox, oy: view.oy, moved: false };
});
cv.addEventListener('pointermove', e => {
  if (!state.img) return;
  const [x, y] = localXY(e);
  if (!drag) { cv.style.cursor = hit(x, y) >= 0 ? 'grab' : 'crosshair'; return; }
  if (drag.type === 'pt') {
    const p = toImg(x, y), W = state.img.naturalWidth, H = state.img.naturalHeight;
    state.corners[drag.i] = { x: Math.min(W, Math.max(0, p.x)), y: Math.min(H, Math.max(0, p.y)) };
  } else {
    if (Math.hypot(x - drag.x, y - drag.y) > 4) drag.moved = true;
    if (drag.moved) { view.ox = drag.ox + x - drag.x; view.oy = drag.oy + y - drag.y; }
  }
  draw();
});
cv.addEventListener('pointerup', e => {
  if (!drag) return;
  const [x, y] = localXY(e), d = drag; drag = null;
  if (d.type === 'pan' && !d.moved && state.corners.length < 4) {
    const p = toImg(x, y), W = state.img.naturalWidth, H = state.img.naturalHeight;
    if (p.x >= 0 && p.y >= 0 && p.x <= W && p.y <= H) state.corners.push(p);
    updateHint(); draw();
  }
  if (state.corners.length === 4) schedulePreview();
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
  if (state.corners.length !== 4) { msg.textContent = 'Set 4 corners to preview.'; msg.hidden = false; img.removeAttribute('src'); return; }
  const art = $('pvArt').value;
  if (!art) { msg.textContent = 'Add artworks to preview.'; msg.hidden = false; return; }
  const seq = ++pvSeq;
  msg.textContent = 'Rendering…'; msg.hidden = false;
  try {
    const common = { scene: state.cur.name, corners: state.corners, settings: readSettings() };
    const blob = sheet
      ? await api('/contact-sheet', { json: { ...common, artworks: previewArtworks().slice(0, 6) } })
      : await api('/preview', { json: { ...common, artwork: art } });
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
  try { state.artworks = await api('/artworks'); }
  catch (e) { state.artworks = []; toast(e.message, true); }
  const valid = new Set(state.artworks.map(a => a.file));
  state.selected = new Set([...state.selected].filter(f => valid.has(f)));
  if (!localStorage.getItem('selectedArtworks')) state.artworks.forEach(a => { if (!a.error) state.selected.add(a.file); });
  persistSelection(); renderArtworks();
  const sel = $('pvArt'); const keep = sel.value; sel.innerHTML = '';
  state.artworks.filter(a => !a.error).forEach(a => sel.add(new Option(a.file, a.file)));
  if (keep && valid.has(keep)) sel.value = keep;
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
  await loadArtworks(); toast('Artwork folder updated.');
});

// ---------- generate ----------
const genOff = new Set(); // scenes the user explicitly unticked
function renderGenerate() {
  const box = $('genScenes');
  box.innerHTML = '';
  if (!state.scenes.some(s => s.corners.length === 4)) {
    box.innerHTML = '<div class="warn">No scene is ready yet. Go to the Scenes tab, upload a photo and click its 4 corners.</div>';
  }
  for (const s of state.scenes) {
    const ready = s.corners.length === 4;
    const l = document.createElement('label'); l.className = 'inline';
    const cb = document.createElement('input'); cb.type = 'checkbox'; cb.value = s.name; cb.disabled = !ready;
    cb.checked = ready && !genOff.has(s.name);
    cb.onchange = () => { cb.checked ? genOff.delete(s.name) : genOff.add(s.name); updateSummary(); };
    l.append(cb, document.createTextNode(' ' + s.name + (ready ? '' : ' (set corners first)')));
    box.appendChild(l);
  }
  updateSummary();
}
$('btnScAll').onclick = () => { genOff.clear(); renderGenerate(); };
$('btnScNone').onclick = () => { state.scenes.forEach(s => genOff.add(s.name)); renderGenerate(); };
const pickedScenes = () => state.scenes.filter(s => s.corners.length === 4 && !genOff.has(s.name)).map(s => s.name);
function updateSummary() {
  const s = pickedScenes().length, a = state.selected.size;
  $('genSummary').textContent = `${s} scene(s) × ${a} artwork(s) = ${s * a} images`;
  $('genSummary').textContent += !s ? ' — tick at least one scene.' : !a ? ' — select artworks in the Artworks tab.' : '';
}

let polling = null;
$('btnGenerate').onclick = guard(async () => {
  if (!pickedScenes().length) throw new Error('No scene ticked. Set the 4 corners in the Scenes tab, save, then tick the scene here.');
  if (!state.selected.size) throw new Error('No artwork selected. Tick artworks in the Artworks tab.');
  const job = await api('/batch', { json: { scenes: pickedScenes(), artworks: [...state.selected], overwrite: $('chkOverwrite').checked, format: $('selFormat').value } });
  $('errBox').hidden = true; pollBatch(job.id);
});
$('btnCancel').onclick = guard(async () => { if (polling) await api(`/batch/${polling}/cancel`, { method: 'POST' }); });
function pollBatch(id) {
  polling = id; $('btnGenerate').disabled = true; $('btnCancel').disabled = false;
  const tick = async () => {
    let j;
    try { j = await api('/batch/' + id); } catch (e) { toast(e.message, true); polling = null; return; }
    $('bar').style.width = (j.total ? 100 * j.done / j.total : 0) + '%';
    const info = `${j.done} / ${j.total} — ${j.rendered} rendered, ${j.skipped} skipped, ${j.failed} failed`;
    $('progText').textContent = j.status === 'running' ? `${info}  ·  ${j.current}` : `${j.status.toUpperCase()}: ${info} (${j.seconds.toFixed(1)}s)`;
    const list = $('errList'); list.innerHTML = '';
    j.errors.forEach(er => { const li = document.createElement('li'); li.textContent = `${er.artwork} → ${er.scene}: ${er.message}`; list.appendChild(li); });
    $('errBox').hidden = j.errors.length === 0;
    if (j.status === 'running') return setTimeout(tick, 500);
    polling = null; $('btnCancel').disabled = true; $('btnGenerate').disabled = false; updateSummary();
  };
  tick();
}
$('btnOpenOut').onclick = guard(() => api('/output/open', { method: 'POST' }));
$('btnSavePrompt').onclick = guard(async () => { await api('/config', { method: 'PUT', json: { captionPrompt: $('captionPrompt').value } }); toast('Prompt saved.'); });
$('btnCaptions').onclick = guard(async () => {
  if (!state.selected.size) throw new Error('Select at least one artwork.');
  $('capText').textContent = `Generating ${state.selected.size} captions…`; $('btnCaptions').disabled = true;
  try {
    await api('/config', { method: 'PUT', json: { captionPrompt: $('captionPrompt').value } });
    const r = await api('/ai/captions', { json: { artworks: [...state.selected] } });
    $('capText').textContent = `${r.count} captions written to ${r.file ?? '(nothing)'}` + (r.errors.length ? ` — ${r.errors.length} failed: ${r.errors[0].message}` : '');
  } finally { $('btnCaptions').disabled = !state.config.aiEnabled; }
});

init().catch(e => toast(e.message, true));
