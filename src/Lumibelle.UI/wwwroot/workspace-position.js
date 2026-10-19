const memory = new Map();
const written = new Map();
export const key = (project, studio) => `lumibelle.position.${project}.${studio.toLowerCase()}.v1.view`;
export function read(key) {
    if (!memory.has(key)) {
        let value; try { value = JSON.parse(localStorage.getItem(key)); } catch {}
        value = value && typeof value === 'object' && !Array.isArray(value) ? value : {};
        if (!value.scroll || typeof value.scroll !== 'object' || Array.isArray(value.scroll)) value.scroll = {};
        if (!value.tabs || typeof value.tabs !== 'object' || Array.isArray(value.tabs)) value.tabs = {};
        memory.set(key, value);
        written.set(key, JSON.stringify(value));
    }
    return memory.get(key);
}
export function write(key, value) {
    memory.set(key, value);
    const json = JSON.stringify(value);
    if (written.get(key) === json) return;
    written.set(key, json);
    try { localStorage.setItem(key, json); } catch {}
}
const finite = value => Number.isFinite(value) ? Math.max(0, value) : 0;
const attributes = ['data-block-id', 'data-scene-id', 'data-media-id', 'data-asset-id', 'data-shot-id', 'data-take-id'];
function capture(el) {
    const bounds = el.getBoundingClientRect();
    const anchor = [...el.querySelectorAll(attributes.map(a => `[${a}]`).join(','))].find(node => {
        const box = node.getBoundingClientRect(); return box.height > 0 && box.bottom > bounds.top && box.top < bounds.bottom;
    });
    const attribute = anchor && attributes.find(a => anchor.hasAttribute(a));
    return { top: el.scrollTop, left: el.scrollLeft, anchor: attribute ? [attribute, anchor.getAttribute(attribute)] : null,
        offset: anchor ? anchor.getBoundingClientRect().top - bounds.top : 0 };
}
function restore(el, saved) {
    if (!saved) { el.scrollTop = el.scrollLeft = 0; return; }
    const anchor = Array.isArray(saved.anchor) && attributes.includes(saved.anchor[0]) && typeof saved.anchor[1] === 'string'
        ? el.querySelector(`[${saved.anchor[0]}="${CSS.escape(saved.anchor[1])}"]`) : null;
    el.scrollTop = anchor ? el.scrollTop + anchor.getBoundingClientRect().top - el.getBoundingClientRect().top - (Number.isFinite(saved.offset) ? saved.offset : 0) : finite(saved.top);
    el.scrollLeft = finite(saved.left);
}
export function attach(root, storageKey, context) {
    const state = read(storageKey); state.scroll ??= {};
    const events = new AbortController(); let timer, settle, restoring = true, disposed = false;
    let revealRestoredShot = ['Shots', 'Scenes'].includes(root.dataset.studio);
    const loading = () => root.getAttribute('aria-busy') === 'true';
    // Pane bodies, plus nested scrollers such as horizontal gallery strips that name themselves.
    const regions = () => [...root.querySelectorAll('.workspace-pane-body, [data-scroll-region]')].filter(el => el.getClientRects().length);
    const regionKey = el => {
        const side = el.closest('.workspace-left') ? 'left' : el.closest('.workspace-right') ? 'right' : 'center';
        const named = el.dataset.scrollRegion ? `:${el.dataset.scrollRegion}` : '';
        return `${side}:${side === 'left' ? '' : context(side)}${named}`;
    };
    function snapshot() {
        if (restoring || disposed || loading()) return;
        for (const el of regions()) state.scroll[regionKey(el)] = capture(el);
    }
    function save() { snapshot(); if (!restoring && !disposed) write(storageKey, state); }
    function revealShot() {
        if (!revealRestoredShot || loading() || disposed) return;
        const body = root.querySelector('.workspace-left .workspace-pane-body');
        const row = body?.querySelector('.shot-outline-item.selected, .scene-outline-item.selected');
        if (!body?.getClientRects().length || !row?.getClientRects().length) return;
        const pane = body.getBoundingClientRect(), item = row.getBoundingClientRect();
        // Move only the outline pane, without scrolling the page or taking focus.
        if (item.top < pane.top) body.scrollTop += item.top - pane.top;
        else if (item.bottom > pane.bottom) body.scrollTop += Math.min(item.bottom - pane.bottom, item.top - pane.top);
        state.scroll[regionKey(body)] = capture(body);
        revealRestoredShot = false;
    }
    function apply() {
        if (!disposed && restoring && !loading()) {
            for (const el of regions()) { const saved = state.scroll[regionKey(el)]; if (saved) restore(el, saved); }
            revealShot();
        }
    }
    function resume() {
        clearTimeout(timer); clearTimeout(settle);
        if (loading()) { restoring = true; return; }
        for (const el of regions()) restore(el, state.scroll[regionKey(el)]);
        revealShot();
        restoring = regions().some(el => !!state.scroll[regionKey(el)]);
        if (!restoring) return;
        requestAnimationFrame(() => { apply(); requestAnimationFrame(apply); });
        // Keep the anchor through initial editor/media layout, until the user takes control.
        settle = setTimeout(() => { apply(); restoring = false; }, 1800);
    }
    const stopRestoring = () => { if (loading()) return; if (restoring) { clearTimeout(settle); restoring = false; } snapshot(); };
    // Capture the current view before a tab's keyboard handler changes its content.
    for (const name of ['wheel', 'pointerdown', 'keydown', 'touchstart']) root.addEventListener(name, stopRestoring, { signal: events.signal, passive: true, capture: true });
    root.addEventListener('scroll', () => { if (!restoring) { snapshot(); clearTimeout(timer); timer = setTimeout(() => write(storageKey, state), 120); } }, { capture: true, signal: events.signal });
    window.addEventListener('pagehide', save, { signal: events.signal });
    const resize = new ResizeObserver(apply); resize.observe(root);
    const mutation = new MutationObserver(changes => {
        if (changes.some(change => change.target === root && change.attributeName === 'aria-busy') && !loading()) resume();
        else if (restoring) requestAnimationFrame(apply);
    });
    mutation.observe(root, { childList: true, subtree: true, attributes: true, attributeFilter: ['aria-busy'] });
    root.addEventListener('load', apply, { capture: true, signal: events.signal });
    resume();
    return { state, save, restore: resume,
        restoreSide(side) {
            const el = root.querySelector(`.workspace-${side} .workspace-pane-body`);
            const saved = el && state.scroll[regionKey(el)];
            if (!el) return false;
            if (saved) restore(el, saved);
            const revealing = revealRestoredShot;
            revealShot();
            return !!saved || side === 'left' && revealing && !revealRestoredShot;
        },
        dispose() { save(); disposed = true; clearTimeout(timer); clearTimeout(settle); events.abort(); resize.disconnect(); mutation.disconnect(); } };
}
export function focusHeading(path) {
    if (location.pathname !== path) return;
    const heading = document.querySelector('main h1');
    if (heading) { heading.setAttribute('tabindex', '-1'); heading.focus({ preventScroll: true }); }
}
export function page(project, studio, marker) {
    marker ??= document.querySelector('main h1');
    const storageKey = key(project, studio), state = read(storageKey), events = new AbortController(); let timer, restoring = true;
    requestAnimationFrame(() => {
        if (!marker.isConnected) return;
        if (restoring) window.scrollTo(0, finite(state.top));
        restoring = false; marker.dataset.ready = 'true';
    });
    const capture = () => { if (!restoring && marker.isConnected) state.top = window.scrollY; };
    const save = () => { if (!restoring) write(storageKey, state); };
    for (const name of ['wheel', 'touchstart', 'pointerdown', 'keydown']) window.addEventListener(name, () => { restoring = false; capture(); }, { signal: events.signal, passive: true });
    window.addEventListener('scroll', () => { capture(); clearTimeout(timer); timer = setTimeout(save, 100); }, { signal: events.signal });
    window.addEventListener('pagehide', save, { signal: events.signal });
    return { dispose() { save(); clearTimeout(timer); events.abort(); } };
}
