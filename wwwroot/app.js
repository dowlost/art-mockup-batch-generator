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

// ---------- generate (still images + zoom videos in one flow) ----------
const genOff = new Set(); // scenes unticked for images (default: every ready scene is ticked)
let vidOn = new Set(); // scenes ticked for videos (default: none, videos are slower)
try { vidOn = new Set(JSON.parse(localStorage.getItem('videoScenes') || '[]')); } catch { /* ignore */ }
const saveVidOn = () => { try { localStorage.setItem('videoScenes', JSON.stringify([...vidOn])); } catch { /* ignore */ } };
const sceneReady = s => s.corners.length === 4;
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
    const name = document.createElement('span'); name.textContent = s.name + (ready ? '' : ' (set corners first)');
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
  updateSummary();
}
$('btnScAll').onclick = () => { genOff.clear(); renderGenerate(); };
$('btnScNone').onclick = () => { state.scenes.forEach(s => genOff.add(s.name)); renderGenerate(); };
$('btnVidScAll').onclick = () => { state.scenes.filter(sceneReady).forEach(s => vidOn.add(s.name)); saveVidOn(); renderGenerate(); };
$('btnVidScNone').onclick = () => { vidOn.clear(); saveVidOn(); renderGenerate(); };

function updateSummary() {
  const si = pickedScenes().length, sv = pickedVideoScenes().length, a = state.selected.size;
  const parts = [];
  if (si) parts.push(`${si * a} image(s) (${si} scene(s) × ${a} artwork(s))`);
  if (sv) parts.push(`${sv * a} video(s) (${sv} scene(s) × ${a} artwork(s))`);
  $('genSummary').textContent = parts.length ? 'Will generate ' + parts.join(' and ') + '.' : 'Tick at least one scene under Images or Videos.';
  if (parts.length && !a) $('genSummary').textContent += ' Select artworks in the Artworks tab.';
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
  const art = $('synArt'), ka = art.value; art.innerHTML = '';
  const okArts = state.artworks.filter(a => !a.error);
  okArts.forEach(a => art.add(new Option(a.file, a.file)));
  if (ka && okArts.some(a => a.file === ka)) art.value = ka;
  synInfo();
}
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
  for (const [i, t] of [0, 0.5, 1].entries()) {
    const blob = await api('/videos/synthetic/preview', { json: { options: o, artwork: art, t } });
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
  if (!arts.length) throw new Error('No artwork selected. Tick artworks in the Artworks tab.');
  cancelRequested = false; setBusy(true);
  $('errBox').hidden = true; $('synErrBox').hidden = true;
  $('bar').style.width = '0%'; $('synBar').style.width = '0%'; $('progText').textContent = ''; $('synProg').textContent = '';
  try {
    if (imgs.length) {
      const job = await api('/batch', { json: { scenes: imgs, artworks: arts, overwrite: $('chkOverwrite').checked, format: $('selFormat').value, fullQuality: $('chkFull').checked } });
      await watchJob('/batch/' + job.id, `/batch/${job.id}/cancel`, imgUi, 'image');
    }
    if (vids.length && !cancelRequested) {
      const job = await api('/videos/synthetic', { json: { ...synOptions(), scenes: vids, artworks: arts } });
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
  persistSelection();
  await loadArtworks();
  toast('Renamed ' + r.count + ' artwork(s). The old names are listed in data/artworks-rename-map.csv.');
});

init().catch(e => toast(e.message, true));
