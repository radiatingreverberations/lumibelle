// Drag a shot by its handle to another position in the same scene. A plain click on the handle is left to Blazor (Move to…).
export function attach(list, receiver) {
    // Navigation can remove the element while the module import is in flight.
    if (!list?.isConnected) return { dispose() {} };
    const scroll = list.closest('.workspace-pane-body');
    let drag, frame, suppressClick = false;
    const clearMarker = () => list.querySelectorAll('.shot-drop-before,.shot-drop-after').forEach(el => el.classList.remove('shot-drop-before', 'shot-drop-after'));
    function update() {
        if (!drag) return;
        const bounds = scroll?.getBoundingClientRect();
        if (bounds) { const edge = 40; scroll.scrollTop += drag.y < bounds.top + edge ? -8 : drag.y > bounds.bottom - edge ? 8 : 0; }
        clearMarker();
        const target = document.elementFromPoint(drag.x, drag.y)?.closest('[data-shot-row]');
        // A shot belongs to its script scene, so it only moves among that scene's shots.
        drag.target = target && list.contains(target) && target !== drag.row && target.dataset.scene === drag.row.dataset.scene ? target : null;
        if (drag.target) { drag.after = drag.y > target.getBoundingClientRect().top + target.offsetHeight / 2; target.classList.add(drag.after ? 'shot-drop-after' : 'shot-drop-before'); }
        frame = requestAnimationFrame(update);
    }
    function down(e) {
        const handle = e.target.closest('[data-shot-drag]');
        if (!handle || handle.disabled || e.button !== 0) return;
        drag = { id: e.pointerId, row: handle.closest('[data-shot-row]'), handle, x: e.clientX, y: e.clientY, startX: e.clientX, startY: e.clientY, active: false };
        handle.setPointerCapture(e.pointerId);
    }
    function move(e) {
        if (!drag || drag.id !== e.pointerId) return;
        drag.x = e.clientX; drag.y = e.clientY;
        if (!drag.active && Math.hypot(drag.x - drag.startX, drag.y - drag.startY) > 6) {
            drag.active = true; drag.row.classList.add('shot-dragging'); update();
        }
        if (drag.active) e.preventDefault();
    }
    function finish(e) {
        if (!drag || e.pointerId !== drag.id) return;
        const done = drag; drag = null; cancelAnimationFrame(frame); clearMarker();
        done.row.classList.remove('shot-dragging');
        if (done.handle.hasPointerCapture(done.id)) done.handle.releasePointerCapture(done.id);
        if (done.active) {
            suppressClick = true; setTimeout(() => suppressClick = false, 0);
            if (e.type === 'pointerup' && done.target) receiver.invokeMethodAsync('Move', done.row.dataset.shotRow, done.target.dataset.shotRow, done.after);
        }
    }
    const click = e => { if (suppressClick) { e.preventDefault(); e.stopImmediatePropagation(); } };
    list.addEventListener('pointerdown', down); list.addEventListener('pointermove', move);
    list.addEventListener('pointerup', finish); list.addEventListener('pointercancel', finish); list.addEventListener('click', click, true);
    return { dispose() { drag = null; cancelAnimationFrame(frame); list.removeEventListener('pointerdown', down); list.removeEventListener('pointermove', move); list.removeEventListener('pointerup', finish); list.removeEventListener('pointercancel', finish); list.removeEventListener('click', click, true); } };
}
