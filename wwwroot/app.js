'use strict';
const $ = id => document.getElementById(id);
const state = { config: {}, scenes: [], artworks: [], selected: new Set(), cur: null, frames: [[], []], img: null };
const SPLIT_LABEL = 'Dividir a arte do quadro 1 entre os dois';

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
    let msg = r.status === 413 ? 'A imagem ultrapassa o limite de envio.' : 'Falha na solicitação (HTTP ' + r.status + ').';
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
const okArtworks = () => state.artworks.filter(a => !a.error);
/** A file the app made from an artwork: 0001_part1 (scene split), 0001_print or 0001_print_part1 (print files). */
const isDerived = file => /._(print_part[12]|print|part[12])\.[^.]+$/i.test(file);
/** The original artworks: the only ones that are listed and picked. */
const rawArtworks = () => okArtworks().filter(a => !isDerived(a.file));
const stemOf = file => file.replace(/\.[^.]+$/, '').toLowerCase();
/** 'side' or 'stacked' if the artwork has its two scene parts (by their shape, as the server does), else null. */
function splitOf(file) {
  const part = n => okArtworks().filter(a => stemOf(a.file) === `${stemOf(file)}_part${n}`)
    .sort((a, b) => /\.png$/i.test(b.file) - /\.png$/i.test(a.file))[0];
  const first = part(1);
  return first && part(2) ? (first.width > first.height ? 'stacked' : 'side') : null;
}
/** 'single', or how the frames of a two-frame scene are placed: 'side' or 'stacked' (same rule as Scene.Arrangement). */
function sceneKind(s) {
  if (s.frameCount !== 2) return 'single';
  const mean = (frame, k) => frame.reduce((t, p) => t + p[k], 0) / frame.length;
  return Math.abs(mean(s.corners2, 'x') - mean(s.corners, 'x')) >= Math.abs(mean(s.corners2, 'y') - mean(s.corners, 'y')) ? 'side' : 'stacked';
}
/** Refills a <select> with artwork files, keeping the current choice. `blank` adds a first option with an empty value. */
function fillArtSelect(sel, blank, value = sel.value, arts = rawArtworks()) {
  sel.innerHTML = '';
  if (blank) sel.add(new Option(blank, ''));
  arts.forEach(a => sel.add(new Option(a.file, a.file)));
  if (arts.some(a => a.file === value)) sel.value = value;
}

// ---------- tabs ----------
document.querySelectorAll('.tab').forEach(b => b.onclick = () => {
  document.querySelectorAll('.tab').forEach(x => x.classList.toggle('active', x === b));
  ['scenes', 'artworks', 'split'].forEach(n => $('tab-' + n).hidden = n !== b.dataset.tab);
  if (b.dataset.tab === 'scenes') requestAnimationFrame(() => { resizeCanvas(); fitView(); });
  if (b.dataset.tab === 'split') refreshSplit();
});

// ---------- init ----------
async function init() {
  try { state.selected = new Set(JSON.parse(localStorage.getItem('selectedArtworks') || '[]')); } catch { /* ignore */ }
  state.config = await api('/config');
  $('folderInput').value = state.config.artworkFolder;
  await loadScenes();
  await loadArtworks();
  if (state.scenes.length) selectScene(state.scenes[0].name);
  resumeRunning().catch(e => toast(e.message, true));
}

// ---------- scenes ----------
async function loadScenes() {
  state.scenes = await api('/scenes');
  renderSceneList();
}

const editorSceneFilter = $('editorSceneFilter');
const editorScenes = () => editorSceneFilter.value ? matchingScenes(editorSceneFilter.value) : state.scenes;
editorSceneFilter.onchange = renderSceneList;

function renderSceneList() {
  const scenes = editorScenes();
  const ul = $('sceneList');
  ul.replaceChildren();
  $('editorSceneNotice').hidden = scenes.length > 0;
  for (const s of scenes) {
    const li = document.createElement('li');
    li.className = state.cur && state.cur.name === s.name ? 'sel' : '';
    li.innerHTML = `<span></span><span class="tag">${s.frameCount === 2 ? '2 quadros · ' : ''}${sceneReady(s) ? '✓ pronto' : 'sem cantos'}</span>`;
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
  img.onerror = () => toast('Não foi possível carregar a imagem do cenário.', true);
  img.src = `/api/scenes/${encodeURIComponent(s.name)}/image?v=${Date.now()}`;
});

$('uploadForm').onsubmit = guard(async e => {
  e.preventDefault();
  const fd = new FormData();
  fd.append('file', $('sceneFile').files[0]);
  fd.append('name', $('sceneNameNew').value);
  const s = await api('/scenes', { method: 'POST', body: fd });
  $('uploadForm').reset();
  editorSceneFilter.value = '';
  await loadScenes();
  selectScene(s.name);
  toast('Cenário adicionado. Marque os quatro cantos.');
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
  $('pvArtLabel').textContent = frameCount() === 2 ? 'Arte do quadro 1' : 'Arte';
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
  if (!silent) toast('Cenário salvo.');
}

$('btnDelete').onclick = guard(async () => {
  if (!state.cur || !confirm(`Excluir o cenário "${state.cur.name}"?`)) return;
  await api(`/scenes/${encodeURIComponent(state.cur.name)}`, { method: 'DELETE' });
  state.cur = null; state.img = null; state.frames = [[], []];
  await loadScenes(); draw(); $('emptyScene').hidden = false;
  const [first] = editorScenes();
  if (first) selectScene(first.name);
});

$('btnResetCorners').onclick = () => { state.frames = [[], []]; updateHint(); draw(); };
$('btnFit').onclick = () => { fitView(); };

function updateHint() {
  const names = ['superior esquerdo', 'superior direito', 'inferior direito', 'inferior esquerdo'];
  const f = usedFrames().findIndex(x => x.length < 4);
  let text = 'Cantos marcados. Arraste para ajustar e salve o cenário.';
  if (f >= 0) {
    const n = state.frames[f].length;
    text = `${frameCount() === 2 ? `Quadro ${f + 1} — clique no` : 'Clique no'} canto ${n + 1} de 4 — ${names[n]}`;
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
    msg.textContent = frameCount() === 2 ? 'Marque os quatro cantos dos dois quadros para ver a prévia.' : 'Marque os quatro cantos para ver a prévia.';
    msg.hidden = false; img.removeAttribute('src'); return;
  }
  const art = $('pvArt').value;
  if (!art) { msg.textContent = 'Adicione artes para ver a prévia.'; msg.hidden = false; return; }
  const seq = ++pvSeq;
  msg.textContent = 'Gerando prévia…'; msg.hidden = false;
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
  const sel = rawArtworks().filter(a => state.selected.has(a.file)).map(a => a.file);
  return sel.length ? sel : rawArtworks().map(a => a.file);
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
      w.textContent = `A proporção da arte (${(a.width / a.height).toFixed(2)}) difere da esperada pelo cenário (${exp}).`;
      w.hidden = false; return;
    }
  }
  w.hidden = true;
}

// ---------- artworks ----------
async function loadArtworks() {
  try { state.artworks = await api('/artworks'); }
  catch (e) { state.artworks = []; toast(e.message, true); }
  const valid = new Set(rawArtworks().map(a => a.file));
  state.selected = new Set([...state.selected].filter(f => valid.has(f)));
  if (!localStorage.getItem('selectedArtworks')) valid.forEach(f => state.selected.add(f));
  persistSelection(); renderArtworks();

  duoSelection = new Set([...duoSelection].filter(f => valid.has(f)));
  if ($('generationDialog').open) renderBatchArtworks();
  fillArtSelect($('pvArt'));
  fillArtSelect($('pvArt2'), SPLIT_LABEL);
  const spBefore = $('spArt').value;
  // only the numbered originals (0001.png) can be turned into print files
  fillArtSelect($('spArt'), '', spBefore, rawArtworks().filter(a => /^\d+\.[^.]+$/.test(a.file)));
  if ($('spArt').value !== spBefore) resetSplitForArtwork();
}
function persistSelection() { localStorage.setItem('selectedArtworks', JSON.stringify([...state.selected])); }
function renderArtworks() {
  const g = $('artGrid'); g.innerHTML = '';
  for (const a of listedArtworks()) {
    const t = document.createElement('div');
    t.className = 'tile' + (state.selected.has(a.file) ? ' on' : '') + (a.error ? ' bad' : '');
    const img = a.error ? '' : `<img loading="lazy" src="/api/artworks/${encodeURIComponent(a.file)}/thumb" alt="">`;
    t.innerHTML = `${a.error ? '' : '<input type="checkbox">'}${img}<div class="nm"></div>` +
      (a.error ? '<div class="er"></div>'
        : `<div class="dm">${a.width}×${a.height}${splitOf(a.file) ? ` · corte ${splitOf(a.file) === 'side' ? 'lado a lado' : 'superior e inferior'}` : ''}</div>`);
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
/** What the Artworks tab shows: originals, including unreadable ones. Parts and print files are left out. */
const listedArtworks = () => state.artworks.filter(a => !isDerived(a.file));
function updateArtCount() { $('artCount').textContent = `${state.selected.size} de ${rawArtworks().length} selecionadas`; }
$('btnAll').onclick = () => { rawArtworks().forEach(a => state.selected.add(a.file)); persistSelection(); renderArtworks(); };
$('btnNone').onclick = () => { state.selected.clear(); persistSelection(); renderArtworks(); };
$('btnReload').onclick = guard(loadArtworks);
$('btnFolder').onclick = guard(async () => {
  const r = await api('/config', { method: 'PUT', json: { artworkFolder: $('folderInput').value.trim() } });
  $('folderInput').value = r.artworkFolder; localStorage.removeItem('selectedArtworks'); state.selected.clear();
  duoSelection.clear();
  await loadArtworks(); toast('Pasta de artes atualizada.');
});

// ---------- geração de mockups ----------
const generationDialog = $('generationDialog');

let duoSelection = new Set();
const tickedArts = () => rawArtworks().filter(a => state.selected.has(a.file)).map(a => a.file);
const duoArts = () => rawArtworks().filter(a => duoSelection.has(a.file));
const cutKind = art => art.width > art.height ? 'side' : 'stacked';
const sceneType = scene => {
  const [tl, tr, br, bl] = scene.corners;
  const length = (a, b) => Math.hypot(a.x - b.x, a.y - b.y);
  const orientation = (length(tl, tr) + length(bl, br)) >= (length(tl, bl) + length(tr, br)) ? 'horizontal' : 'vertical';
  return scene.frameCount === 2 ? 'duplo-' + orientation : orientation;
};

const matchingScenes = type => state.scenes.filter(s => sceneReady(s) && sceneType(s) === type);
const isScene3 = scene => /^cen[aá]rio[ _-]*3(?:[ _-]|$)/i.test(scene.name);
const types = {
  vertical: 'Vertical', horizontal: 'Horizontal',
  'duplo-vertical': 'Duplo-vertical', 'duplo-horizontal': 'Duplo-horizontal',
};

const namedPart = file => file.match(/^(.+?)[ _-](esquerda|direita|cima|baixo)(?:-d+)?.[^.]+$/i);
function generationGroups() {
  const files = Object.fromEntries(Object.keys(types).map(type => [type, []]));
  const explicit = new Map();
  const square = new Set();
  for (const art of rawArtworks()) {
    const duo = duoSelection.has(art.file);
    if (!duo && !state.selected.has(art.file)) continue;
    const part = namedPart(art.file);
    if (part) {
      const side = part[2].toLowerCase();
      const type = side === 'esquerda' || side === 'direita' ? 'duplo-vertical' : 'duplo-horizontal';
      const key = type + '/' + part[1].normalize('NFC').toLowerCase();
      if (!explicit.has(key)) explicit.set(key, { type, parts: [] });
      explicit.get(key).parts.push({ side, file: art.file });
      continue;
    }
    if (art.width === art.height) { square.add(art.file); continue; }
    const landscape = art.width > art.height;
    const type = duo ? (landscape ? 'duplo-vertical' : 'duplo-horizontal') : (landscape ? 'horizontal' : 'vertical');
    files[type].push({ first: art.file });
  }
  const incomplete = [];
  for (const { type, parts } of explicit.values()) {
    const sides = type === 'duplo-vertical' ? ['esquerda', 'direita'] : ['cima', 'baixo'];
    const [first, second] = sides.map(side => parts.find(p => p.side === side)?.file);
    if (parts.length !== 2 || !first || !second) incomplete.push(...parts.map(p => p.file));
    else files[type].push({ first, second });
  }
  const groups = Object.entries(files).filter(([, items]) => items.length).map(([type, items]) => {
    const scenes = matchingScenes(type);
    return { type, arts: items.map(item => item.first), items, scenes, video: scenes.find(isScene3) || scenes[0] };
  });
  return { groups, square, incomplete };
}

function renderBatchArtworks() {
  $('importStatus').textContent = state.selected.size + ' arte(s) selecionada(s)';
  const list = $('duoArtList');
  list.replaceChildren();
  $('duoStatus').textContent = duoSelection.size + ' arte(s) selecionada(s)';
  for (const art of rawArtworks()) {
    const label = document.createElement('label');
    label.className = 'inline';
    const input = document.createElement('input');
    input.type = 'checkbox';
    input.checked = duoSelection.has(art.file);
    input.disabled = generationBusy;
    input.onchange = () => {
      input.checked ? duoSelection.add(art.file) : duoSelection.delete(art.file);
      $('duoStatus').textContent = duoSelection.size + ' arte(s) selecionada(s)';
      renderGenerate();
    };
    label.append(input, art.file);
    list.append(label);
  }
}

function renderSceneGallery(gallery, scenes) {
  gallery.replaceChildren();
  for (const scene of scenes) {
    const card = document.createElement('figure');
    const image = document.createElement('img');
    image.loading = 'lazy';
    image.alt = '';
    image.src = '/api/scenes/' + encodeURIComponent(scene.name) + '/image?v=' + encodeURIComponent(scene.image);
    const caption = document.createElement('figcaption');
    caption.textContent = scene.name;
    card.append(image, caption);
    gallery.append(card);
  }
}


function renderGenerate() {
  const { groups, square, incomplete } = generationGroups();
  const box = $('detectedGroups');
  box.replaceChildren();
  for (const group of groups) {
    const section = document.createElement('details');
    const count = group.arts.length;
    const header = document.createElement('summary');
    header.textContent = types[group.type] + ' · ' + count + (group.type.startsWith('duplo-') ? ' conjunto(s) identificado(s) · ' : ' arte(s) identificada(s) · ') +
      count * group.scenes.length + ' imagem(ns) · ' + (group.video ? count : 0) + ' vídeo(s)';
    const images = document.createElement('div');
    images.className = 'sceneGallery';
    renderSceneGallery(images, group.scenes);
    section.append(header, images);
    if (group.video) {
      const label = document.createElement('p');
      label.textContent = 'Vídeo: ' + group.video.name;
      section.append(label);
    }
    box.append(section);
  }
  const missing = groups.filter(g => !g.scenes.length).map(g => types[g.type]);
  const messages = [];
  if (square.size) messages.push(square.size + ' arte(s) quadrada(s) sem categoria. Podem ser exportadas como originais.');
  if (incomplete.length) messages.push('Pares incompletos ou duplicados: ' + incomplete.join(', ') + '.');
  if (missing.length) messages.push('Categorias sem cenários configurados: ' + missing.join(', ') + '.');
  $('detectionNotice').hidden = !messages.length;
  $('detectionNotice').textContent = messages.join(' ');
  renderSplits();
  synFillLists();
  updateSummary();
}

function openGeneration() {
  if (generationDialog.open) return;
  generationDialog.showModal();
  loadUpscayl().catch(e => { $('upscaylNote').textContent = e.message; });
  $('btnCloseGeneration').after($('toast'));
  renderBatchArtworks();
  renderGenerate();
}
$('btnAddImages').onclick = openGeneration;
$('btnCloseGeneration').onclick = () => generationDialog.close();
generationDialog.addEventListener('close', () => document.querySelector('header').append($('toast')));

$('btnPickImages').onclick = () => $('artFiles').click();
$('btnPickDuo').onclick = () => $('duoFiles').click();
$('artFiles').onchange = guard(() => importBatchArtworks('artFiles', 'importStatus', false));
$('duoFiles').onchange = guard(() => importBatchArtworks('duoFiles', 'duoStatus', true));
async function importBatchArtworks(inputId, statusId, duo) {
  const files = [...$(inputId).files];
  if (!files.length) return;
  $(inputId).value = '';
  setBusy(true, false);
  const imported = [];
  const errors = [];
  try {
    for (const [index, file] of files.entries()) {
      $(statusId).textContent = `Importando ${index + 1} de ${files.length}…`;
      const form = new FormData();
      form.append('file', file);
      try {
        const result = await api('/artworks/import', { method: 'POST', body: form });
        imported.push(result.file);
      } catch (e) { errors.push(`${file.name}: ${e.message}`); }
    }
    if (imported.length) {
      if (duo) duoSelection = new Set(imported);
      else { state.selected = new Set(imported); persistSelection(); }
      await loadArtworks();
      renderGenerate();
    }
  } finally {
    setBusy(false);
    renderBatchArtworks();
  }
  if (errors.length) throw new Error(errors.join(' · '));
}

// --- splitting artworks for two-frame scenes (a plain cut into part1 / part2: no upscaling, no border) ---
const cutOffsets = new Map(); // artwork -> crop position chosen in the panel
const redo = new Set(); // already split artworks the user wants to cut again

const cuttableArts = () => duoArts().filter(a => !namedPart(a.file) && a.width !== a.height);
const toCut = () => cuttableArts().filter(a => splitOf(a.file) !== cutKind(a) || redo.has(a.file));
/** How much of the artwork a split cuts away, in percent. */
function cutAway(file, kind) {
  const a = state.artworks.find(x => x.file === file), shape = kind === 'side' ? 4 / 3 : 3 / 4, ratio = a.width / a.height;
  return Math.round(100 * (1 - Math.min(ratio / shape, shape / ratio)));
}


function renderSplits() {
  const arts = cuttableArts();
  $('splitBox').hidden = !$('chkAdjustCut').checked || !arts.length;
  const list = $('splitList'); list.replaceChildren();
  if ($('splitBox').hidden) return;
  $('splitHelp').textContent = 'Ajuste a posição do recorte de cada arte e salve os cortes.';
  $('resplitRow').hidden = !arts.some(a => splitOf(a.file) === cutKind(a));
  for (const art of arts) {
    const kind = cutKind(art), needed = splitOf(art.file) !== kind;
    if (needed || $('chkResplit').checked) list.append(splitCard(art.file, kind, needed));
  }
  updateCutButton();
}
$('chkAdjustCut').onchange = renderSplits;
function splitCard(file, kind, needed) {
  const card = document.createElement('div'); card.className = 'splitCard';
  const img = document.createElement('img'); img.loading = 'lazy'; img.alt = `Prévia do corte de ${file}`;
  const show = () => { img.src = `/api/split/cut-preview?artwork=${encodeURIComponent(file)}&layout=${kind}&offset=${cutOffsets.get(file) ?? 0.5}`; };
  show();
  const name = document.createElement('strong'); name.textContent = file;
  const now = splitOf(file), away = cutAway(file, kind);
  const status = document.createElement('span'); status.className = 'small ' + (away > 30 ? 'warn' : 'muted');
  status.textContent = (!now ? 'Corte automático' : needed ? `Corte atual ${now === 'side' ? 'lado a lado' : 'superior e inferior'}; salvar substituirá as partes` : 'Corte salvo') +
    (away ? ` · descarta ${away}%` : ' · aproveita a arte inteira');
  const slider = document.createElement('input'); slider.type = 'range'; slider.min = 0; slider.max = 1; slider.step = 0.01;
  slider.value = cutOffsets.get(file) ?? 0.5; slider.disabled = !away;
  const label = document.createElement('label'); label.append('Posição do recorte', slider);
  card.append(img, name, status, label);
  let again = null;
  if (!needed) {
    const row = document.createElement('label'); row.className = 'inline';
    again = document.createElement('input'); again.type = 'checkbox'; again.checked = redo.has(file);
    again.onchange = () => { again.checked ? redo.add(file) : redo.delete(file); updateCutButton(); };
    row.append(again, 'Refazer corte (substitui as partes)');
    card.appendChild(row);
  }
  const refresh = debounce(show, 250);
  slider.oninput = () => {
    cutOffsets.set(file, +slider.value);
    if (again) { again.checked = true; redo.add(file); updateCutButton(); }
    refresh();
  };
  return card;
}
function updateCutButton() {
  const n = toCut().length;
  $('btnCut').hidden = !n;
  $('btnCut').textContent = `Salvar ${n} corte(s)`;
}
$('chkResplit').onchange = () => { if (!$('chkResplit').checked) redo.clear(); renderSplits(); };
$('btnCut').onclick = guard(async () => {
  const files = toCut(), button = $('btnCut');
  button.disabled = true;
  const replaced = files.some(a => splitOf(a.file));
  try {
    for (const [i, art] of files.entries()) {
      const file = art.file;
      $('splitProgress').textContent = `Cortando ${i + 1} de ${files.length} — ${file}`;
      await api('/split/cut', { json: { artwork: file, layout: cutKind(art), offset: cutOffsets.get(file) ?? 0.5, overwrite: true } });
      redo.delete(file);
    }
    toast('Corte salvo. Você já pode gerar os arquivos.');
  } finally {
    button.disabled = false;
    $('splitProgress').textContent = replaced ? 'Partes substituídas. Marque a opção de substituir os arquivos para gerar novamente.' : '';
    await loadArtworks();
    renderSplits(); updateSummary();
  }
});


const groupInputs = group => group.type.startsWith('duplo-')
  ? { pairs: group.items }
  : { artworks: group.arts };

function originalsFor() {
  return $('chkOriginals').checked ? [...new Set([...tickedArts(), ...duoArts().map(a => a.file)])] : [];
}
$('chkOriginals').onchange = updateSummary;

function updateSummary() {
  const { groups } = generationGroups();
  const imageCount = groups.reduce((total, g) => total + g.scenes.length * g.arts.length, 0);
  const videoCount = groups.reduce((total, g) => total + (g.video ? g.arts.length : 0), 0);
  const originalCount = originalsFor().length;
  const parts = [];
  if (imageCount) parts.push(imageCount + ' mockup(s)');
  if (videoCount) parts.push(videoCount + ' vídeo(s)');
  if (originalCount) parts.push(originalCount + ' original(is) sem cenário');
  $('genSummary').textContent = parts.length ? 'Saída: ' + parts.join(' e ') + '.' : 'Selecione artes para gerar.';
  $('btnGenerate').disabled = generationBusy || !parts.length;
}

$('chkFull').onchange = () => {
  const full = $('chkFull').checked, o = $('selFormat').options;
  o[0].textContent = full ? 'JPEG (qualidade 100)' : 'JPEG (até 2 MB por arquivo)';
  o[1].textContent = full ? 'PNG (sem perdas)' : 'PNG (até 2 MB, com redimensionamento se necessário)';
  o[2].textContent = full ? 'WebP (sem limite de tamanho)' : 'WebP (até 2 MB, com redimensionamento se necessário)';
};


let upscaylAvailable = false;
async function loadUpscayl() {
  if (!state.config.supportsUpscayl) {
    $('upscaylState').textContent = '· atualização pendente';
    $('upscaylNote').textContent = 'Reinicie o gerador com a versão atual para usar o Upscayl.';
    return;
  }
  const { models } = await api('/upscayl');
  upscaylAvailable = models.length > 0;
  const select = $('upscaylModel'), previous = select.value;
  select.replaceChildren(...models.map(m => new Option(m.name, m.id)));
  if (models.some(m => m.id === previous)) select.value = previous;
  if (!upscaylAvailable) {
    $('upscaylEnabled').checked = false;
  }
  updateUpscayl();
}
function updateUpscayl() {
  const enabled = $('upscaylEnabled').checked && upscaylAvailable;
  $('upscaylControls').disabled = !upscaylAvailable || generationBusy;

  $('upscaylOptions').disabled = !enabled;
  const scale = Number($('upscaylScale').value);
  const doublePass = $('upscaylDouble').checked;
  const usesScale = !$('upscaylWidth').value || doublePass;
  const messages = [];
  const scaleWarning = $('upscaylScaleWarning');
  const showScaleWarning = enabled && usesScale && scale >= 5;
  scaleWarning.hidden = !showScaleWarning;
  scaleWarning.textContent = showScaleWarning ? 'A partir de 5×, o processamento pode ficar mais lento e consumir mais memória.' : '';
  if (doublePass) messages.push('A ampliação dupla faz duas passagens e pode aumentar bastante o tempo de processamento, especialmente acima de 4×.');
  if ($('upscaylTta').checked) messages.push('O modo TTA pode aumentar o tempo de processamento em até 8 vezes.');
  const performance = $('upscaylPerformance');
  performance.textContent = messages.join(' ');
  performance.hidden = !enabled || !messages.length;
  $('upscaylQuality').disabled = $('selFormat').value === 'png' || $('chkFull').checked;
  $('upscaylState').textContent = upscaylAvailable ? (enabled ? '· ativado' : '· desativado') : '· indisponível';
  $('upscaylNote').textContent = !upscaylAvailable
    ? 'Prepare o motor portátil na pasta tools/upscayl. Consulte o README.'
    : $('upscaylTarget').value === 'mockup'
      ? 'Amplia a cena inteira antes de salvar a imagem e criar o zoom. Marque qualidade máxima para conservar as dimensões sem o limite de 2 MB.'
      : 'Amplia a arte antes de encaixá-la no quadro. A resolução do mockup segue a foto do cenário.';
}
const upscaylInputs = ['upscaylEnabled', 'upscaylTarget', 'upscaylModel', 'upscaylScale', 'upscaylWidth',
  'upscaylGpu', 'upscaylTile', 'upscaylDouble', 'upscaylTta', 'upscaylQuality'];
for (const id of [...upscaylInputs, 'selFormat', 'chkFull']) {
  $(id).addEventListener('input', () => {
    updateUpscayl();
    if (upscaylInputs.includes(id)) {
      videoPreviewVersion++;
      synInfo();
    }
  });
}
function upscaylOptions() {
  if (!upscaylAvailable || !$('upscaylEnabled').checked) return null;
  const optionalNumber = id => $(id).value === '' ? null : $(id).valueAsNumber;
  return {
    model: $('upscaylModel').value, scale: +$('upscaylScale').value, width: optionalNumber('upscaylWidth'),
    gpuId: optionalNumber('upscaylGpu'), tileSize: +$('upscaylTile').value, doublePass: $('upscaylDouble').checked,
    tta: $('upscaylTta').checked, entireMockup: $('upscaylTarget').value === 'mockup',
  };
}

// --- configurações e prévia do vídeo ---

function synFillLists() {
  const sc = $('synScene'), previous = sc.value;
  const { groups } = generationGroups();
  sc.replaceChildren(...groups.filter(g => g.video).map(g => new Option(g.video.name, g.video.name)));
  if ([...sc.options].some(o => o.value === previous)) sc.value = previous;
  refreshVideoPreview();
}
function refreshVideoPreview() {
  videoPreviewVersion++;
  const { groups } = generationGroups();
  const files = new Set(groups.find(g => g.video?.name === $('synScene').value)?.arts ?? []);
  fillArtSelect($('synArt'), undefined, $('synArt').value, rawArtworks().filter(a => files.has(a.file)));
  for (const i of [0, 1, 2]) {
    const img = $('synPv' + i);
    if (img.src.startsWith('blob:')) URL.revokeObjectURL(img.src);
    img.removeAttribute('src');
  }
  synInfo();
}
$('synScene').onchange = refreshVideoPreview;

const videoOrientation = type => $('synOri').value === 'auto'
  ? (type?.endsWith('vertical') ? 'portrait' : 'landscape')
  : $('synOri').value;
const synOptions = () => ({
  scene: $('synScene').value, orientation: videoOrientation(generationGroups().groups.find(g => g.video?.name === $('synScene').value)?.type), seconds: +$('synSec').value, fps: +$('synFps').value,
  upscayl: upscaylOptions(), endFill: +$('synFill').value, ease: $('synEase').checked, loop: $('synLoop').checked, overwrite: $('chkOverwrite').checked,
});
const synInfo = debounce(guard(async () => {
  $('synFillOut').textContent = Math.round($('synFill').value * 100) + '%';
  if (!$('synScene').value) { $('synInfo').textContent = ''; return; }
  const i = await api('/videos/synthetic/info', { json: synOptions() });
  $('synInfo').textContent = `${i.outWidth}×${i.outHeight} · ampliação ao fim do zoom ${i.endUpscale.toFixed(1)}×` + (i.endUpscale > 1.5 ? ' (reduza o preenchimento final ou use uma foto maior para preservar nitidez)' : ' (boa nitidez)');
}), 250);
['synScene', 'synOri', 'synFill', 'synEase'].forEach(id => $(id).addEventListener('input', synInfo));
$('btnSynPreview').onclick = guard(async () => {
  if (!$('synScene').value) throw new Error('Configure os cantos de um cenário para ver a prévia.');
  const art = $('synArt').value;
  if (!art) throw new Error('Escolha uma arte para ver a prévia.');
  const version = ++videoPreviewVersion;
  const o = synOptions();
  const artwork2 = generationGroups().groups.find(g => g.video?.name === o.scene)?.items.find(item => item.first === art)?.second;
  for (const [i, t] of [0, 0.5, 1].entries()) {
    const blob = await api('/videos/synthetic/preview', { json: { options: o, artwork: art, artwork2, t } });
    if (version !== videoPreviewVersion) return;
    const img = $('synPv' + i);
    if (img.src.startsWith('blob:')) URL.revokeObjectURL(img.src);
    img.src = URL.createObjectURL(blob);
  }
});

// --- running jobs ---
let activeCancel = null, cancelRequested = false, generationBusy = false, videoPreviewVersion = 0;
const sleep = ms => new Promise(r => setTimeout(r, ms));
const imgUi = { bar: 'bar', text: 'progText', errBox: 'errBox', errList: 'errList' };
const vidUi = { bar: 'synBar', text: 'synProg', errBox: 'synErrBox', errList: 'synErrList' };

/** Polls a job until it finishes and keeps its progress widgets up to date. */
async function watchJob(pollPath, cancelPath, ui, noun, previousErrors = []) {
  activeCancel = cancelPath;
  try {
    if (cancelRequested) await api(cancelPath, { method: 'POST' });
    for (;;) {
      const j = await api(pollPath);
      $(ui.bar).style.width = (j.total ? 100 * j.done / j.total : 0) + '%';
      const info = `${j.rendered} ${noun}(s) gerado(s), ${j.skipped} ignorado(s), ${j.failed} com erro`;
      $(ui.text).textContent = j.status === 'running' ? `${info}  ·  ${j.current}` : `${j.status === 'cancelled' ? 'Cancelado' : 'Concluído'}: ${info} (${j.seconds.toFixed(1)}s)`;
      const list = $(ui.errList); list.innerHTML = '';
      [...previousErrors, ...j.errors].forEach(er => { const li = document.createElement('li'); li.textContent = `${er.artwork || ''}${er.scene ? ' → ' + er.scene : ''}: ${er.message}`; list.appendChild(li); });
      $(ui.errBox).hidden = list.children.length === 0;
      if (j.status !== 'running') return j;
      await sleep(600);
    }
  } finally { activeCancel = null; }
}
function setBusy(busy, cancellable = true) {
  generationBusy = busy;
  updateUpscayl();
  $('btnCancel').disabled = !busy || !cancellable;
  $('btnPickImages').disabled = busy;
  $('btnPickDuo').disabled = $('chkAdjustCut').disabled = busy;
  for (const input of document.querySelectorAll('#duoArtList input')) input.disabled = busy;
  updateSummary();
}

$('btnGenerate').onclick = guard(async () => {

  const { groups } = generationGroups();
  if (upscaylOptions() && upscaylInputs.some(id => !$(id).checkValidity())) {
    $('upscaylPanel').open = true;
    for (const id of upscaylInputs) if (!$(id).reportValidity()) break;
    return;
  }

  const originals = originalsFor();
  const videoOptions = synOptions();
  const orientations = Object.fromEntries(groups.map(g => [g.type, videoOrientation(g.type)]));
  cancelRequested = false; setBusy(true);
  $('errBox').hidden = true; $('synErrBox').hidden = true;
  $('bar').style.width = '0%'; $('synBar').style.width = '0%'; $('progText').textContent = ''; $('synProg').textContent = '';
  try {

    const imageErrors = [], videoErrors = [];
    const imageGroups = groups.filter(g => g.scenes.length);
    const batches = imageGroups.length || !originals.length ? imageGroups : [{ type: 'originais', arts: [], scenes: [] }];
    for (const [i, group] of batches.entries()) {
      if (cancelRequested) break;
      const job = await api('/batch', { json: {
        scenes: group.scenes.map(s => s.name), ...groupInputs(group), originals: i === 0 ? originals : [],
        allowAutomaticSplit: true, upscayl: videoOptions.upscayl,
        quality: videoOptions.upscayl && !$('upscaylQuality').disabled ? +$('upscaylQuality').value : null,
        overwrite: videoOptions.overwrite, format: $('selFormat').value, fullQuality: $('chkFull').checked,
      } });
      const result = await watchJob('/batch/' + job.id, '/batch/' + job.id + '/cancel', imgUi, 'imagem · ' + (types[group.type] || 'Originais'), imageErrors);
      imageErrors.push(...result.errors);
      if (result.status === 'cancelled') cancelRequested = true;
    }
    for (const group of groups) {
      if (cancelRequested) break;
      if (!group.video) continue;
      const job = await api('/videos/synthetic', { json: {
        ...videoOptions, orientation: orientations[group.type], scene: group.video.name, scenes: [group.video.name], ...groupInputs(group), allowAutomaticSplit: true,
      } });
      const result = await watchJob('/videos/jobs/' + job.id, '/videos/jobs/' + job.id + '/cancel', vidUi, 'vídeo · ' + types[group.type], videoErrors);
      videoErrors.push(...result.errors);
      if (result.status === 'cancelled') cancelRequested = true;
    }
  } finally { setBusy(false); updateSummary(); }
});
$('btnCancel').onclick = guard(async () => { cancelRequested = true; if (activeCancel) await api(activeCancel, { method: 'POST' }); });

// Reconecta à geração em andamento depois de recarregar a página.
async function resumeRunning() {
  const [images, videos] = await Promise.all([
    api('/batch'),
    state.config.supportsVideoResume ? api('/videos/jobs') : null,
  ]);
  const job = images || videos;
  if (!job || generationBusy) return;
  openGeneration();
  setBusy(true);
  const path = images ? '/batch/' + job.id : '/videos/jobs/' + job.id;
  try {
    await watchJob(path, path + '/cancel', images ? imgUi : vidUi, images ? 'imagem' : 'vídeo');
  } finally { setBusy(false); }
}
$('btnOpenOut').onclick = guard(() => api('/output/open', { method: 'POST' }));

// ---------- rename artworks to the 0000 format ----------
$('btnRenameArt').onclick = guard(async () => {
  const plan = await api('/artworks/normalize?dryRun=true', { method: 'POST' });
  if (!plan.count) { toast('Todas as artes já usam o formato 0000.'); return; }
  const sample = plan.items.slice(0, 6).map(i => i.from + '  ->  ' + i.to).join('\n');
  if (!confirm('Numerar ' + plan.count + ' arte(s) com os próximos números disponíveis?\n\n' + sample + (plan.count > 6 ? '\n...' : ''))) return;
  const r = await api('/artworks/normalize', { method: 'POST' });
  r.items.forEach(i => { if (state.selected.delete(i.from)) state.selected.add(i.to); });
  const renamed = new Map(r.items.map(i => [i.from, i.to]));
  duoSelection = new Set([...duoSelection].map(file => renamed.get(file) ?? file));
  persistSelection();
  await loadArtworks();
  toast('' + r.count + ' arte(s) numerada(s). Os nomes anteriores constam em data/artworks-rename-map.csv.');
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
  $('spOffsetOut').textContent = o.offset === 0.5 ? 'centralizado' : Math.round(o.offset * 100) + '%';
  const clear = text => { msg.textContent = text; msg.hidden = false; img.removeAttribute('src'); $('spInfo').textContent = ''; $('spWarn').hidden = true; };
  if (!o.artwork) return clear('Adicione artes primeiro.');
  try {
    const [info, blob] = await Promise.all([api('/split/info', { json: o }), api('/split/preview', { json: o })]);
    if (seq !== spSeq) return;
    if (spUrl) URL.revokeObjectURL(spUrl);
    spUrl = URL.createObjectURL(blob); img.src = spUrl; msg.hidden = true;
    const cropped = info.croppedPercent < 0.5 ? 'recorte mínimo' : `${info.croppedPercent.toFixed(0)}% da arte será descartada (ajuste a posição para escolher a área)`;
    const one = info.pieces === 1;
    $('btnSplit').textContent = one ? 'Exportar 1 arquivo' : 'Exportar 2 arquivos';
    $('spInfo').textContent = `${one ? '1 impressão' : '2 impressões'} de ${info.fileCmWidth}×${info.fileCmHeight} cm (${info.artCmWidth}×${info.artCmHeight} cm de arte + ${info.borderCm} cm de borda branca), ${info.fileWidth}×${info.fileHeight} px${one ? '' : ' por arquivo'} · arte ${info.sourceWidth}×${info.sourceHeight} px · ${cropped}.` +
      (info.usesAi ? ' Os detalhes da ampliação por IA aparecem nos arquivos exportados.' : '');
    if (!info.aiAvailable) $('spAi').checked = false;
    $('spAi').disabled = !info.aiAvailable;
    $('spAiNote').textContent = !info.aiAvailable ? '— Upscayl precisa ser instalado'
      : !o.dpi ? '— disponível ao escolher uma resolução em DPI' : '';
    const low = info.sourceDpi < 150;
    $('spWarn').hidden = !low;
    if (low) {
      const advice = info.usesAi
        ? `A ampliação por IA chegará a cerca de ${Math.round(info.aiDpi)} DPI. Confira os detalhes gerados antes de imprimir.`
        : (o.dpi ? 'O redimensionamento preserva o nível de detalhe da arte.' : 'A impressão pode perder nitidez.') +
          (info.aiAvailable ? ' Para melhorar a nitidez, escolha uma resolução em DPI e marque a ampliação por IA.' : ' Para melhorar a nitidez, use uma arte com mais pixels.');
      $('spWarn').textContent = `Nesta dimensão, a arte tem cerca de ${Math.round(info.sourceDpi)} DPI. Para impressão, recomenda-se pelo menos 150 DPI. ${advice}`;
    }
  } catch (e) { if (seq === spSeq) clear(e.message); }
}
const scheduleSplit = debounce(() => refreshSplit(), 250);
$('spArt').onchange = () => { resetSplitForArtwork(); refreshSplit(); };
['spLayout', 'spDpi', 'spOffset', 'spAi'].forEach(id => $(id).addEventListener('input', () => { $('spResult').textContent = ''; scheduleSplit(); }));
$('btnSplit').onclick = guard(async () => {
  if (!$('spArt').value) throw new Error('Escolha uma arte.');
  const button = $('btnSplit');
  const o = spOptions();
  button.disabled = true;
  try {
    const { existing } = await api('/split/info', { json: o });
    if (existing.length && !confirm(`Substituir estes arquivos na pasta de artes?\n\n${existing.join('\n')}`)) return;
    $('spResult').textContent = o.ai && o.dpi ? 'Exportando… A ampliação por IA pode levar cerca de 30 segundos.' : 'Exportando… Arquivos grandes podem levar algum tempo.';
    const r = await api('/split/export', { json: { ...o, overwrite: existing.length > 0 } });
    $('spResult').textContent = `Arquivos salvos em ${r.folder} — ` + r.files.map(f => `${f.name} (${f.width}×${f.height}, ${(f.bytes / 1e6).toFixed(1)} MB)`).join(', ');
    await loadArtworks(); // the new files are artworks too
  } catch (e) { $('spResult').textContent = ''; throw e; }
  finally { button.disabled = false; }
});
$('btnOpenParts').onclick = guard(() => api('/split/open', { method: 'POST' }));


const cutDialog = $('cutDialog');
let cutPreviewUrls = [];
function refreshCutForm() {
  cutPreviewUrls.forEach(url => URL.revokeObjectURL(url));
  cutPreviewUrls = [];
  $('cutPreviews').replaceChildren();
  const files = [...$('cutFirst').files];
  $('cutName').disabled = files.length > 1;
  $('cutCount').textContent = files.length ? files.length + ' arte(s) selecionada(s)' : '';
  for (const file of files) {
    const url = URL.createObjectURL(file), img = document.createElement('img');
    const card = document.createElement('figure'), caption = document.createElement('figcaption');
    cutPreviewUrls.push(url);
    img.src = url; img.alt = ''; img.loading = 'lazy';
    caption.textContent = file.name;
    card.append(img, caption);
    $('cutPreviews').append(card);
  }
}
$('btnOpenCut').onclick = () => { refreshCutForm(); cutDialog.showModal(); };
$('btnCloseCut').onclick = () => cutDialog.close();
cutDialog.addEventListener('close', () => {
  cutPreviewUrls.forEach(url => URL.revokeObjectURL(url));
  cutPreviewUrls = [];
  $('cutPreviews').replaceChildren();
});
for (const id of ['cutLayout', 'cutFirst']) $(id).onchange = refreshCutForm;

function cutNames(files, name) {
  if (files.length === 1 && name) return [name];
  const used = new Set();
  return files.map(file => {
    const stem = file.name.replace(/.[^.]+$/, '').replace(/[^p{L}p{N}p{M}_ -]/gu, '-').trim() || 'arte';
    let result = stem.slice(0, 60).trimEnd(), sequence = 2;
    while (used.has(result.toLowerCase())) {
      const suffix = '-' + sequence++;
      result = stem.slice(0, 60 - suffix.length).trimEnd() + suffix;
    }
    used.add(result.toLowerCase());
    return result;
  });
}
$('cutForm').onsubmit = async e => {
  e.preventDefault();
  const status = $('cutFormStatus');
  const buttons = ['btnDownloadImages', 'btnDownloadCut'].map($);
  const images = e.submitter?.value === 'images';
  const files = [...$('cutFirst').files];
  if (!files.length) return;
  const names = cutNames(files, $('cutName').value.trim());
  const layout = $('cutLayout').value, offset = $('cutOffset').value;
  const suffixes = layout === 'side' ? ['esquerda', 'direita'] : ['cima', 'baixo'];
  async function request(indices, part = '') {
    const form = new FormData();
    form.append('layout', layout);
    form.append('offset', offset);
    form.append('part', part);
    for (const index of indices) {
      form.append('name', names[index]);
      form.append('first', files[index]);
    }
    const response = await fetch('/api/split/package', { method: 'POST', body: form });
    if (!response.ok) {
      const error = await response.json().catch(() => null);
      throw new Error(error?.error || 'Falha ao cortar as artes.');
    }
    return response.blob();
  }
  function download(blob, file) {
    const url = URL.createObjectURL(blob), link = document.createElement('a');
    link.href = url; link.download = file;
    link.click();
    setTimeout(() => URL.revokeObjectURL(url), 30000);
  }

  buttons.forEach(button => button.disabled = true);
  let requested = 0;
  try {
    if (images) {
      for (const [i, file] of files.entries()) {
        status.textContent = 'Cortando ' + (i + 1) + ' de ' + files.length + ': ' + file.name;
        const first = await request([i], 'first'), second = await request([i], 'second');
        download(first, names[i] + '-' + suffixes[0] + '.png');
        download(second, names[i] + '-' + suffixes[1] + '.png');
        requested += 2;
      }
      status.textContent = requested + ' downloads solicitados. Se o navegador pedir, permita baixar vários arquivos.';
    } else {
      status.textContent = 'Preparando o ZIP com ' + files.length * 2 + ' imagens…';
      download(await request(files.map((_, i) => i)), files.length === 1 ? names[0] + '.zip' : 'artes-cortadas.zip');
      status.textContent = 'ZIP pronto com ' + files.length * 2 + ' imagens.';
    }
  } catch (e) { status.textContent = (requested ? requested + ' downloads já solicitados. ' : '') + e.message; }
  finally { buttons.forEach(button => button.disabled = false); }
};

init().catch(e => toast(e.message, true));
