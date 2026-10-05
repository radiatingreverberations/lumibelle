import { duration, totalTime, atTime, trimEdge } from './cut-time.js';

export function attach(root, project, callbacks) {
    const scroll = root.querySelector('.timeline-scroll'), canvas = root.querySelector('.timeline-canvas');
    const row = root.querySelector('.timeline-clips'), ruler = root.querySelector('.timeline-ruler');
    const head = root.querySelector('.timeline-playhead'), marker = root.querySelector('.timeline-insertion');
    const source = root.querySelector('.timeline-source'), strip = root.querySelector('.source-strip');
    const announcement = root.querySelector('.timeline-announcement'), events = new AbortController();
    const editError = root.querySelector('.timeline-error');
    const failedEdit = () => { editError.textContent = 'The edit could not be applied. Check the connection and try again.'; editError.hidden = false; };
    let clips = [], selected = null, version = 0, unavailable = new Set(), scale = 1, fit = true;
    let currentTime = 0, playingId = null, gesture = null, pending = false, disposed = false, paintId = 0, edgeScrollId = 0;
    const nodes = new Map();
    const on = (el, name, fn) => el.addEventListener(name, fn, { signal: events.signal });
    const frameUrl = (c, frame) => `/media/projects/${project}/takes/${c.takeId}/frames/${frame}`;
    const minScale = () => Math.max(.0001, (scroll.clientWidth - 24) / Math.max(.001, totalTime(clips)));
    const secondsX = x => Math.max(0, (x - canvas.getBoundingClientRect().left - 12) / scale);
    const selectedClip = () => clips.find(c => c.id === selected);
    const announce = text => { announcement.textContent = text; };
    const attrs = (button, c, edge) => {
        const start = edge === 'start';
        button.setAttribute('aria-valuemin', start ? 1 : c.startFrame + 1);
        button.setAttribute('aria-valuemax', start ? c.endFrameExclusive : c.frameCount);
        button.setAttribute('aria-valuenow', start ? c.startFrame + 1 : c.endFrameExclusive);
        button.setAttribute('aria-valuetext', `Frame ${start ? c.startFrame + 1 : c.endFrameExclusive}`);
        button.disabled = unavailable.has(c.takeId) || pending;
    };
    const button = (text, className, label) => {
        const b = document.createElement('button'); b.type = 'button'; b.className = className;
        b.textContent = text; if (label) b.setAttribute('aria-label', label); return b;
    };
    function render() {
        if (disposed) return;
        if (fit && !gesture) scale = minScale();
        const total = totalTime(clips);
        root.querySelectorAll('[data-zoom]').forEach(control => { control.disabled = clips.length === 0; });
        head.setAttribute('aria-disabled', String(clips.length === 0));
        head.tabIndex = clips.length === 0 ? -1 : 0;
        canvas.style.width = `${Math.max(scroll.clientWidth, total * scale + 24)}px`;
        root.dataset.zoom = fit ? 'fit' : 'custom';
        root.querySelector('[data-zoom=fit]').setAttribute('aria-pressed', String(fit));
        root.querySelector('[data-total]').textContent = `· ${clips.length} clips · ${total.toFixed(2)} s`;
        let before = 0, budget = 24;
        for (const [i, c] of clips.entries()) {
            let n = nodes.get(c.id);
            if (!n) {
                n = document.createElement('div'); n.className = 'timeline-clip'; n.dataset.clipId = c.id;
                const thumb = document.createElement('div'); thumb.className = 'timeline-thumbnails'; thumb.setAttribute('aria-hidden', 'true');
                const select = button('', 'timeline-select');
                const grip = button('⠿', 'timeline-grip', 'Drag to reorder'); grip.dataset.grip = c.id;
                n.append(thumb, select, grip);
                for (const edge of ['start', 'end']) {
                    const h = button('', `trim-handle ${edge}`, `Clip ${edge} frame`); h.dataset.edge = edge; h.setAttribute('role', 'slider'); n.append(h);
                }
                row.append(n); nodes.set(c.id, n);
            }
            // Anchor the out point during an in-point drag; close the temporary
            // gap on commit so the dragged handle follows the pointer.
            const startDrag = gesture?.kind === 'trim' && !gesture.source && gesture.edge === 'start' && gesture.c.id === c.id;
            const startDelta = startDrag ? (c.startFrame - gesture.c.startFrame) / c.fps : 0;
            n.style.left = `${12 + (before + startDelta) * scale}px`; n.style.width = `${duration(c) * scale}px`;
            n.classList.toggle('selected', c.id === selected); n.classList.toggle('playing', c.id === playingId);
            n.classList.toggle('unavailable', unavailable.has(c.takeId));
            const select = n.querySelector('.timeline-select');
            select.textContent = `${i + 1}. ${c.shotTitle}`;
            select.title = `${c.shotTitle} · ${c.takeLabel} · ${duration(c).toFixed(3)} s${unavailable.has(c.takeId) ? ' · Unavailable' : ''}`;
            select.setAttribute('aria-label', `Select clip ${i + 1}: ${c.shotTitle}`); select.setAttribute('aria-pressed', String(c.id === selected));
            n.querySelector('.timeline-grip').title = `Reorder clip ${i + 1}`;
            n.querySelector('.timeline-grip').disabled = pending;
            for (const h of n.querySelectorAll('[data-edge]')) { h.hidden = c.id !== selected; attrs(h, c, h.dataset.edge); }
            const visible = before * scale < scroll.scrollLeft + scroll.clientWidth && (before + duration(c)) * scale > scroll.scrollLeft;
            // Every visible clip shows a frame; the budget only limits the extra frames of wide clips,
            // so a long cut at Fit no longer runs out partway along.
            const extra = visible && !unavailable.has(c.takeId) ? Math.min(budget, Math.max(0, Math.min(3, Math.floor(duration(c) * scale / 120)) - 1)) : 0;
            const count = visible && !unavailable.has(c.takeId) ? 1 + extra : 0;
            budget -= extra;
            const thumbnailClip = gesture?.original.find(original => original.id === c.id) ?? c;
            thumbnails(n.querySelector('.timeline-thumbnails'), thumbnailClip, count, thumbnailClip.startFrame, thumbnailClip.endFrameExclusive);
            if (row.children[i] !== n) row.insertBefore(n, row.children[i] ?? null);
            before += startDrag ? duration(gesture.c) : duration(c);
        }
        for (const [id, n] of nodes) if (!clips.some(c => c.id === id)) { n.remove(); nodes.delete(id); }
        // Only create ruler labels inside the visible window, even at frame-level zoom.
        const desired = 85 / scale, power = 10 ** Math.floor(Math.log10(desired));
        const interval = [1, 2, 5, 10].map(x => x * power).find(x => x >= desired);
        ruler.replaceChildren();
        for (let s = Math.max(0, Math.floor((scroll.scrollLeft - 12) / scale / interval) * interval); s <= Math.min(total, (scroll.scrollLeft + scroll.clientWidth) / scale); s += interval) {
            const tick = document.createElement('span'); tick.style.left = `${12 + s * scale}px`;
            tick.textContent = `${s.toFixed(interval < 1 ? 2 : 0)}s`; ruler.append(tick);
        }
        const c = selectedClip(); source.hidden = !c;
        if (c) {
            source.querySelector('[data-source-title]').textContent = `Trim · ${c.shotTitle}`;
            source.querySelector('[data-source-range]').textContent = `${c.startFrame + 1}–${c.endFrameExclusive} / ${c.frameCount} frames`;
            thumbnails(source.querySelector('.source-thumbnails'), c, unavailable.has(c.takeId) ? 0 : Math.min(5, Math.max(1, Math.floor(strip.clientWidth / 160))), 0, c.frameCount);
            const left = c.startFrame / c.frameCount * 100, right = c.endFrameExclusive / c.frameCount * 100;
            source.querySelector('.source-excluded.start').style.width = `${left}%`;
            source.querySelector('.source-excluded.end').style.width = `${100 - right}%`;
            const kept = source.querySelector('.source-retained'); kept.style.left = `${left}%`; kept.style.width = `${right - left}%`;
            for (const h of source.querySelectorAll('[data-source-edge]')) { h.style.left = `${h.dataset.sourceEdge === 'start' ? left : right}%`; attrs(h, c, h.dataset.sourceEdge); }
        }
        position(currentTime, playingId, false);
    }
    function thumbnails(container, c, count, start, end) {
        const key = `${c.takeId}/${start}/${end}/${count}`;
        if (container.dataset.key === key) return;
        container.dataset.key = key; container.replaceChildren();
        for (let i = 0; i < count; i++) {
            const img = document.createElement('img'); img.alt = ''; img.loading = 'lazy'; img.draggable = false;
            img.src = frameUrl(c, Math.min(end - 1, start + Math.floor((end - start) * (i + .5) / count)));
            container.append(img);
        }
    }
    function position(time, id, follow) {
        currentTime = time; playingId = id;
        const x = 12 + time * scale;
        head.style.left = `${x}px`; head.setAttribute('aria-valuenow', time.toFixed(3));
        head.setAttribute('aria-valuemax', totalTime(clips)); head.setAttribute('aria-valuetext', `${time.toFixed(3)} seconds`);
        for (const [clipId, n] of nodes) n.classList.toggle('playing', id === clipId);
        if (follow && !gesture && (x < scroll.scrollLeft + 12 || x > scroll.scrollLeft + scroll.clientWidth - 25)) scroll.scrollLeft = Math.max(0, x - scroll.clientWidth * .3);
    }
    function begin(e) {
        if (e.button !== 0 || pending || gesture) return;
        const edge = e.target.closest('[data-edge], [data-source-edge]'), grip = e.target.closest('[data-grip]');
        const n = e.target.closest('[data-clip-id]');
        if (!edge && !grip && !e.target.closest('.timeline-ruler, .timeline-playhead, .timeline-select, .timeline-clips')) return;
        const c = edge?.hasAttribute('data-source-edge') ? selectedClip() : clips.find(c => c.id === n?.dataset.clipId);
        if (edge && (!c || unavailable.has(c.takeId))) return;
        e.preventDefault(); e.target.focus?.({ preventScroll: true }); callbacks.pause();
        editError.hidden = true;
        gesture = { pointer: e.pointerId, target: e.target, x: e.clientX, lastX: e.clientX, original: clips.map(c => ({ ...c })), version,
            kind: edge ? 'trim' : grip ? 'order' : 'seek', c: c && { ...c }, edge: edge?.dataset.edge ?? edge?.dataset.sourceEdge,
            source: !!edge?.hasAttribute('data-source-edge'), initialScroll: scroll.scrollLeft, time: currentTime, beforeId: null, changed: false };
        e.target.setPointerCapture(e.pointerId);
        if (gesture.kind === 'seek') seekAt(e.clientX);
    }
    function seekAt(x) {
        const time = Math.min(totalTime(clips), secondsX(x)), pos = atTime(clips, time);
        if (pos) { selected = pos.clipId; callbacks.select(selected); callbacks.seek(time); render(); }
    }
    function move(e) {
        if (!gesture || e.pointerId !== gesture.pointer) return;
        gesture.lastX = e.clientX; moveGesture();
        if (!gesture.source && !edgeScrollId) edgeScrollId = requestAnimationFrame(autoScroll);
    }
    function autoScroll() {
        edgeScrollId = 0;
        if (!gesture || gesture.source) return;
        const r = scroll.getBoundingClientRect(), x = gesture.lastX;
        const delta = x < r.left + 28 ? -12 : x > r.right - 28 ? 12 : 0;
        if (delta) { const old = scroll.scrollLeft; scroll.scrollLeft += delta; if (old !== scroll.scrollLeft) moveGesture(); edgeScrollId = requestAnimationFrame(autoScroll); }
    }
    function moveGesture() {
        const g = gesture;
        if (g.kind === 'seek') { seekAt(g.lastX); return; }
        g.changed = Math.abs(g.lastX - g.x) > 3 || g.changed;
        if (!g.changed) return;
        if (g.kind === 'order') {
            const time = secondsX(g.lastX); let before = 0;
            g.beforeId = null;
            for (const c of clips) { if (time < before + duration(c) / 2) { g.beforeId = c.id; break; } before += duration(c); }
            marker.hidden = false; marker.style.left = `${12 + before * scale}px`; return;
        }
        const delta = g.source ? (g.lastX - g.x) / strip.clientWidth * g.c.frameCount : (g.lastX - g.x + scroll.scrollLeft - g.initialScroll) / scale * g.c.fps;
        const edited = trimEdge(g.c, g.edge, (g.edge === 'start' ? g.c.startFrame : g.c.endFrameExclusive) + delta);
        clips = g.original.map(c => c.id === edited.id ? edited : c);
        callbacks.preview(edited, g.edge === 'start' ? edited.startFrame : edited.endFrameExclusive - 1);
        render();
    }
    async function end(cancelled = false) {
        const g = gesture; if (!g) return;
        gesture = null; cancelAnimationFrame(edgeScrollId); edgeScrollId = 0; marker.hidden = true;
        if (g.target.hasPointerCapture?.(g.pointer)) g.target.releasePointerCapture(g.pointer);
        const edited = clips.find(c => c.id === g.c?.id);
        clips = g.original; render();
        if (cancelled) { callbacks.restore(g.time); announce('Drag cancelled'); return; }
        if (g.kind === 'seek') return;
        pending = true; render();
        try {
            if (g.changed && g.kind === 'trim' && (edited.startFrame !== g.c.startFrame || edited.endFrameExclusive !== g.c.endFrameExclusive)) {
                await callbacks.trim(edited, g.version); announce(`Keeping frames ${edited.startFrame + 1}–${edited.endFrameExclusive}`);
            } else if (g.changed && g.kind === 'order' && g.beforeId !== g.c.id) {
                await callbacks.order(g.c.id, g.beforeId, g.version); announce('Clip order updated');
            }
        } catch { failedEdit(); }
        finally { pending = false; render(); callbacks.restore(); }
    }
    on(root, 'pointerdown', begin); on(root, 'pointermove', move);
    on(root, 'pointerup', () => void end()); on(root, 'pointercancel', () => void end(true));
    on(root, 'lostpointercapture', () => { if (gesture) void end(true); });
    on(document, 'keydown', e => { if (e.key === 'Escape' && gesture) { e.preventDefault(); void end(true); } });
    on(root, 'keydown', async e => {
        if (pending || gesture) return;
        const edge = e.target.dataset.edge ?? e.target.dataset.sourceEdge;
        if (e.target === head && ['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(e.key)) {
            e.preventDefault(); callbacks.pause();
            if (e.key === 'Home' || e.key === 'End') callbacks.seek(e.key === 'Home' ? 0 : totalTime(clips));
            else callbacks.step(e.key === 'ArrowLeft' ? -1 : 1);
        } else if (edge && ['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(e.key)) {
            const c = selectedClip(); if (!c || unavailable.has(c.takeId)) return;
            e.preventDefault(); callbacks.pause();
            const delta = (e.key === 'ArrowLeft' ? -1 : 1) * (e.shiftKey ? 10 : 1);
            const next = trimEdge(c, edge, e.key === 'Home' ? 0 : e.key === 'End' ? c.frameCount : (edge === 'start' ? c.startFrame : c.endFrameExclusive) + delta);
            pending = true; editError.hidden = true;
            try { await callbacks.trim(next, version); announce(`Keeping frames ${next.startFrame + 1}–${next.endFrameExclusive}`); }
            catch { failedEdit(); }
            finally {
                pending = false; render();
                // Rendering a pending trim disables its handle and can drop native focus.
                // Keep keyboard editing on that handle unless the author focused elsewhere.
                if (document.activeElement === document.body && e.target.isConnected && !e.target.disabled)
                    e.target.focus({ preventScroll: true });
                callbacks.restore();
            }
        } else if (e.target.classList.contains('timeline-select') && (e.key === 'Enter' || e.key === ' ')) {
            e.preventDefault(); const c = clips.find(c => c.id === e.target.parentElement.dataset.clipId);
            selected = c.id; callbacks.select(c.id); callbacks.seek(clips.slice(0, clips.indexOf(c)).reduce((n, c) => n + duration(c), 0)); render();
        }
    });
    on(scroll, 'scroll', () => { cancelAnimationFrame(paintId); paintId = requestAnimationFrame(render); });
    root.querySelectorAll('[data-zoom]').forEach(b => on(b, 'click', () => {
        if (gesture || pending) return;
        const anchor = 12 + currentTime * scale - scroll.scrollLeft;
        fit = b.dataset.zoom === 'fit';
        scale = fit ? minScale() : Math.max(minScale(), Math.min(480, scale * (b.dataset.zoom === 'in' ? 2 : .5)));
        render(); scroll.scrollLeft = Math.max(0, 12 + currentTime * scale - anchor);
    }));
    let lastWidth = scroll.clientWidth;
    const resize = new ResizeObserver(() => {
        if (Math.abs(scroll.clientWidth - lastWidth) < .5) return;
        lastWidth = scroll.clientWidth;
        if (gesture) void end(true);
        render();
    }); resize.observe(scroll);
    return {
        update(next, id, nextVersion, missing) {
            if (gesture && version !== nextVersion) void end(true);
            clips = next.map(c => ({ ...c })); selected = id; version = nextVersion; unavailable = new Set(missing); render();
        }, position,
        view() { return { scale, fit, left: scroll.scrollLeft }; },
        restoreView(view) {
            if (!view || typeof view !== 'object') return;
            fit = view.fit !== false;
            scale = Number.isFinite(view.scale) ? Math.max(minScale(), Math.min(480, view.scale)) : minScale();
            render(); scroll.scrollLeft = Number.isFinite(view.left) ? Math.max(0, view.left) : 0;
        },
        dispose() { disposed = true; events.abort(); resize.disconnect(); cancelAnimationFrame(paintId); cancelAnimationFrame(edgeScrollId); }
    };
}
