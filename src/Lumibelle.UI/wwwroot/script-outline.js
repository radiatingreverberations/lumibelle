let cleanup;
export function mount(reference) {
  dispose();
  let drag, target, position, hoverTimer, scrollFrame;
  const clear = () => { document.querySelectorAll('[data-outline-drop]').forEach(el => el.removeAttribute('data-outline-drop')); target = null; };
  const finish = () => { clearTimeout(hoverTimer); cancelAnimationFrame(scrollFrame); clear(); document.body.classList.remove('moving-outline'); drag = null; };
  const down = event => {
    const handle = event.target.closest('.outline-drag'), row = handle?.closest('[data-outline-id]');
    if (!row || event.button !== 0) return;
    event.preventDefault(); handle.setPointerCapture(event.pointerId);
    drag = { row, startY: event.clientY, moved: false, y: event.clientY }; document.body.classList.add('moving-outline');
    const scroll = () => {
      if (!drag) return;
      const pane = row.closest('.workspace-pane-body'), bounds = pane?.getBoundingClientRect();
      if (bounds) pane.scrollTop += drag.y < bounds.top + 35 ? -8 : drag.y > bounds.bottom - 35 ? 8 : 0;
      scrollFrame = requestAnimationFrame(scroll);
    }; scroll();
  };
  const move = event => {
    if (!drag) return; drag.y = event.clientY;
    if (Math.abs(event.clientY - drag.startY) < 5 && !drag.moved) return; drag.moved = true;
    const row = document.elementFromPoint(event.clientX, event.clientY)?.closest('[data-outline-id]');
    if (!row || row === drag.row || drag.row.dataset.outlineKind === 'Act' && row.dataset.outlineKind !== 'Act') { clear(); return; }
    if (row !== target) { clearTimeout(hoverTimer); clear(); target = row;
      if (row.dataset.outlineKind === 'Act') hoverTimer = setTimeout(() => reference.invokeMethodAsync('ExpandOutline', row.dataset.outlineId), 650);
    }
    const bounds = row.getBoundingClientRect(), fraction = (event.clientY - bounds.top) / bounds.height;
    position = row.dataset.outlineKind === 'Act' && drag.row.dataset.outlineKind === 'Scene' ? (fraction < .25 ? 'before' : fraction < .65 ? 'start' : 'after') : fraction < .5 ? 'before' : 'after';
    row.dataset.outlineDrop = position;
  };
  const up = () => {
    const id = drag?.row.dataset.outlineId, to = target?.dataset.outlineId, side = position, moved = drag?.moved;
    finish(); if (id && to && moved) void reference.invokeMethodAsync('MoveOutline', id, to, side);
  };
  const key = event => { if (event.key === 'Escape') finish(); };
  document.addEventListener('pointerdown', down); document.addEventListener('pointermove', move);
  document.addEventListener('pointerup', up); document.addEventListener('pointercancel', finish); document.addEventListener('keydown', key);
  cleanup = () => { finish(); document.removeEventListener('pointerdown', down); document.removeEventListener('pointermove', move); document.removeEventListener('pointerup', up); document.removeEventListener('pointercancel', finish); document.removeEventListener('keydown', key); };
}
export function dispose() { cleanup?.(); cleanup = null; }

export function reveal(id) {
  const row = document.querySelector(`[data-outline-id="${id}"]`), pane = row?.closest('.workspace-pane-body');
  if (!row || !pane) return;
  const r = row.getBoundingClientRect(), p = pane.getBoundingClientRect();
  if (r.top < p.top) pane.scrollTop += r.top - p.top - 8;
  else if (r.bottom > p.bottom) pane.scrollTop += r.bottom - p.bottom + 8;
}
