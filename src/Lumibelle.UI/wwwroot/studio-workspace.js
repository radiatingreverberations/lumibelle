import { attach as attachPosition, key as positionKey, read as readPosition, write as writePosition } from './workspace-position.js';
const limits = { left: [160, 300], right: [300, 480] };
const wide = studio => studio === 'Shots' || studio === 'Assets' || studio === 'Languages';
const defaults = studio => ({ left: wide(studio) ? 280 : 200, right: wide(studio) ? 320 : 340, leftCollapsed: false, rightCollapsed: false, tabs: {} });
const focusables = root => [...root.querySelectorAll('button:not(:disabled),a[href],input:not(:disabled),textarea:not(:disabled),select:not(:disabled),summary,[tabindex="0"]')]
    .filter(el => el.getClientRects().length && !el.closest('[inert]'));

export function attach(root, toolbarId) {
    const key = `lumibelle.workspace.${root.dataset.studio}.v1`, sides = { left: root.querySelector('.workspace-left'), right: root.querySelector('.workspace-right') };
    const center = root.querySelector('.workspace-center'), toolbar = document.getElementById(toolbarId), scrim = root.querySelector('.workspace-scrim');
    let prefs = defaults(root.dataset.studio), drawer = null, returnFocus = null, drawerFocus = null, suspended = false, disposed = false, selection = root.dataset.selection;
    let lastWorkspaceFocus = null, hadModal = false;
    const paneScroll = { left: 0, right: 0 }, tabScroll = new Map();
    const activeTabs = new Map();
    const placeKey = positionKey(root.dataset.project, root.dataset.studio), storedPosition = readPosition(placeKey);
    let position;
    const rememberScroll = side => { paneScroll[side] = sides[side].querySelector('.workspace-pane-body')?.scrollTop ?? 0; };
    const restoreScroll = side => requestAnimationFrame(() => {
        const body = sides[side].querySelector('.workspace-pane-body');
        if (!disposed && body && !position?.restoreSide(side)) body.scrollTop = paneScroll[side];
    });
    try {
        const stored = JSON.parse(localStorage.getItem(key));
        if (stored) {
            for (const side of ['left', 'right']) {
                if (Number.isFinite(stored[side])) prefs[side] = Math.min(limits[side][1], Math.max(limits[side][0], stored[side]));
                // Ignore legacy collapsed preferences; inline sidebars stay visible.
            }
            if (stored.tabs && typeof stored.tabs === 'object') prefs.tabs = { ...stored.tabs };
            else if (typeof stored.tab === 'string') prefs.tabs.tools = stored.tab;
        }
    } catch { /* Browser privacy settings must not prevent editing. */ }
    if (storedPosition.tabs && typeof storedPosition.tabs === 'object') prefs.tabs = { ...storedPosition.tabs };
    const save = () => { try { localStorage.setItem(key, JSON.stringify(prefs)); } catch {} storedPosition.tabs = { ...prefs.tabs }; writePosition(placeKey, storedPosition); };
    const mode = () => innerWidth < 760 ? 'mobile' : innerWidth < 1100 ? 'tablet' : 'desktop';
    const isDrawer = side => side === 'right' ? mode() !== 'desktop' : mode() === 'mobile';
    const put = (el, name, value) => { if (el.getAttribute(name) !== String(value)) el.setAttribute(name, String(value)); };
    function paint() {
        if (disposed) return;
        let left = prefs.leftCollapsed ? 0 : prefs.left, right = root.dataset.hasRight === "false" || prefs.rightCollapsed ? 0 : prefs.right;
        const room = Math.max(0, root.clientWidth - 360 - 16);
        if (mode() === 'desktop' && left + right > room) {
            if (right) right = Math.max(limits.right[0], Math.min(right, room - left));
            if (left) left = Math.max(limits.left[0], Math.min(left, room - right));
        }
        root.style.setProperty('--workspace-left', `${left}px`); root.style.setProperty('--workspace-right', `${right}px`);
        put(root, 'data-drawer', drawer ?? ''); put(root, 'data-suspended', suspended);
        for (const side of ['left', 'right']) {
            const visible = !(side === 'right' && root.dataset.hasRight === 'false') && (isDrawer(side) ? drawer === side && !suspended : !prefs[`${side}Collapsed`]);
            put(root, `data-${side}-collapsed`, prefs[`${side}Collapsed`]);
            sides[side].inert = !visible || !!drawer && (side !== drawer || suspended);
            const toggle = toolbar.querySelector(`[data-toggle-pane=${side}]`), splitter = root.querySelector(`[data-resize-pane=${side}]`);
            put(toggle, 'aria-expanded', visible);
            put(root, `data-${side}-visible`, visible);
            put(toolbar, `data-${side}-visible`, visible);
            const label = toggle.querySelector('[data-toggle-label]');
            const text = `${visible ? 'Hide' : 'Show'} ${toggle.dataset.paneLabel}`;
            if (label && label.textContent !== text) label.textContent = text;
            const width = side === 'left' ? left : right;
            put(splitter, 'aria-valuenow', width); put(splitter, 'aria-valuetext', `${Math.round(width)} pixels`);
            if (drawer === side && !suspended) { put(sides[side], 'role', 'dialog'); put(sides[side], 'aria-modal', 'true'); }
            else { sides[side].removeAttribute('role'); sides[side].removeAttribute('aria-modal'); }
        }
        center.inert = toolbar.inert = !!drawer && !suspended;
        scrim.hidden = !drawer || suspended;
    }
    function closeDrawer(restore = true) {
        if (drawer && !suspended) { position?.save(); rememberScroll(drawer); }
        drawer = null; suspended = false; paint();
        if (restore && returnFocus?.isConnected) returnFocus.focus({ preventScroll: true });
        returnFocus = drawerFocus = null;
    }
    function toggle(side) {
        if (side === "right" && root.dataset.hasRight === "false") return;
        if (isDrawer(side)) {
            if (drawer === side) { closeDrawer(); return; }
            returnFocus = document.activeElement; drawer = side; paint(); restoreScroll(side);
            requestAnimationFrame(() => focusables(sides[side])[0]?.focus({ preventScroll: true }));
        }
    }
    function tabs(selected, focus = false, group = 'tools') {
        const list = [...root.querySelectorAll('[data-workspace-group]')].find(el => el.dataset.workspaceGroup === group);
        const buttons = list ? [...list.querySelectorAll('[data-workspace-tab]')] : [];
        if (!buttons.length) return;
        selected ??= prefs.tabs[group];
        // Earlier preferences stored display labels. IDs now remain stable when copy changes.
        selected = buttons.find(b => b.dataset.workspaceTab === selected || b.textContent.trim() === selected)?.dataset.workspaceTab ?? buttons[0].dataset.workspaceTab;
        const pane = list.closest('.workspace-pane'), body = pane?.querySelector('.workspace-pane-body');
        const previous = activeTabs.get(group), changing = previous !== selected;
        if (changing) position?.save();
        if (changing && previous && body) tabScroll.set(`${group}:${previous}`, body.scrollTop);
        prefs.tabs[group] = selected;
        for (const button of buttons) {
            const name = button.dataset.workspaceTab, active = name === selected;
            const id = `workspace-${root.dataset.studio}-${group === 'tools' ? '' : group + '-'}tab-${name.replaceAll(' ', '-')}`;
            put(button, 'id', id); put(button, 'aria-selected', active); button.tabIndex = active ? 0 : -1;
            const panel = [...pane.querySelectorAll('[data-workspace-panel]')].find(p => p.dataset.workspacePanel === name);
            if (panel) {
                put(button, 'aria-controls', id + '-panel'); put(panel, 'id', id + '-panel'); put(panel, 'role', 'tabpanel'); put(panel, 'aria-labelledby', id);
                panel.hidden = !active;
            }
            if (active && focus) button.focus({ preventScroll: true });
        }
        activeTabs.set(group, selected);
        if (changing && body) body.scrollTop = tabScroll.get(`${group}:${selected}`) ?? 0;
        if (changing) position?.restore();
    }
    function resetLayout() { prefs = defaults(root.dataset.studio); closeDrawer(false); syncTabs(); paint(); save(); }
    function syncTabs() { for (const list of root.querySelectorAll('[data-workspace-group]')) tabs(undefined, false, list.dataset.workspaceGroup); }
    const listeners = [];
    const on = (node, name, callback, capture = false) => { node.addEventListener(name, callback, capture); listeners.push(() => node.removeEventListener(name, callback, capture)); };
    const click = e => {
        const button = e.target.closest('button'); if (!button) return;
        if (button.hasAttribute('data-toggle-pane')) toggle(button.dataset.togglePane);
        if (button.hasAttribute('data-close-pane')) closeDrawer();
        if (button.hasAttribute('data-reset-layout')) resetLayout();
        if (button.hasAttribute('data-workspace-tab')) { tabs(button.dataset.workspaceTab, false, button.closest('[data-workspace-group]').dataset.workspaceGroup); save(); }
        if (button.hasAttribute('data-workspace-open-tab') || button.hasAttribute('data-workspace-focus')) {
            const name = button.dataset.workspaceOpenTab;
            if (name) { tabs(name, false, button.dataset.workspaceOpenGroup ?? 'tools'); save(); }
            const panel = name ? [...root.querySelectorAll('[data-workspace-panel]')].find(p => p.dataset.workspacePanel === name) : button.closest('.workspace-pane');
            const target = button.dataset.workspaceFocus ? panel?.querySelector(button.dataset.workspaceFocus) : panel && focusables(panel)[0];
            if (target) { target.closest('details')?.setAttribute('open', ''); target.focus({ preventScroll: true }); target.scrollIntoView({ block: 'nearest' }); }
        }
    };
    on(root, 'click', click);
    on(toolbar, 'click', click);
    on(root, 'keydown', e => {
        if (e.target.hasAttribute('data-workspace-tab') && ['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(e.key)) {
            const list = e.target.closest('[data-workspace-group]'), buttons = [...list.querySelectorAll('[data-workspace-tab]')], index = buttons.indexOf(e.target);
            const next = e.key === 'Home' ? 0 : e.key === 'End' ? buttons.length - 1 : (index + (e.key === 'ArrowLeft' ? -1 : 1) + buttons.length) % buttons.length;
            e.preventDefault(); tabs(buttons[next].dataset.workspaceTab, true, list.dataset.workspaceGroup); save();
        }
    });
    on(document, 'focusin', e => {
        if (root.contains(e.target) || toolbar.contains(e.target)) lastWorkspaceFocus = e.target;
        // A global dialog restores its own trigger; an older workspace focus must not override it.
        else if (!e.target.closest('.mud-dialog,[role=dialog]')) lastWorkspaceFocus = null;
        if (drawer && sides[drawer].contains(e.target)) drawerFocus = e.target;
    });
    on(document, 'keydown', e => {
        if (!drawer || suspended) return;
        if (e.key === 'Escape') {
            // A nested menu handles Escape before its containing drawer.
            if (root.querySelector('[data-workspace-menu-open="true"]')) return;
            e.preventDefault(); e.stopPropagation(); closeDrawer();
        }
        if (e.key === 'Tab') {
            const items = focusables(sides[drawer]), first = items[0], last = items.at(-1);
            if (e.shiftKey && (document.activeElement === first || !sides[drawer].contains(document.activeElement))) { e.preventDefault(); last?.focus(); }
            else if (!e.shiftKey && (document.activeElement === last || !sides[drawer].contains(document.activeElement))) { e.preventDefault(); first?.focus(); }
        }
    }, true);
    for (const side of ['left', 'right']) {
        const splitter = root.querySelector(`[data-resize-pane=${side}]`);
        let drag = null;
        const resize = value => {
            const other = side === 'left' ? 'right' : 'left';
            const otherWidth = isDrawer(other) || prefs[`${other}Collapsed`] ? 0 : prefs[other];
            const max = Math.min(limits[side][1], Math.max(limits[side][0], root.clientWidth - 376 - otherWidth));
            prefs[side] = Math.round(Math.max(limits[side][0], Math.min(max, value))); prefs[`${side}Collapsed`] = false; paint();
        };
        on(splitter, 'pointerdown', e => { if (e.button !== 0) return; e.preventDefault(); splitter.focus(); drag = { x: e.clientX, width: prefs[side] }; splitter.setPointerCapture(e.pointerId); });
        on(splitter, 'pointermove', e => { if (drag) resize(drag.width + (e.clientX - drag.x) * (side === 'left' ? 1 : -1)); });
        for (const end of ['pointerup', 'pointercancel', 'lostpointercapture']) on(splitter, end, () => { if (drag) { drag = null; save(); } });
        on(splitter, 'keydown', e => {
            if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(e.key)) return;
            e.preventDefault();
            resize(e.key === 'Home' ? limits[side][0] : e.key === 'End' ? limits[side][1] : prefs[side] + (e.key === 'ArrowRight' ? 1 : -1) * (side === 'left' ? 1 : -1) * (e.shiftKey ? 40 : 10)); save();
        });
    }
    on(window, 'resize', () => { if (drawer && !isDrawer(drawer)) closeDrawer(false); paint(); });
    const size = new ResizeObserver(paint); size.observe(root);
    const selectionObserver = new MutationObserver(() => {
        if (selection !== root.dataset.selection) {
            // The first selection comes from the page loading its items, not from a choice in the drawer.
            const navigated = !!selection;
            selection = root.dataset.selection;
            for (const key of tabScroll.keys()) if (key.startsWith('center:')) tabScroll.delete(key);
            position?.restore();
            if (drawer === 'left' && navigated) closeDrawer();
        }
        syncTabs();
    });
    selectionObserver.observe(root, { childList: true, subtree: true, attributes: true, attributeFilter: ['data-selection'] });
    const modalObserver = new MutationObserver(() => {
        const modal = [...document.querySelectorAll('.mud-dialog,[role=dialog]:not(.workspace-side)')].some(d => d.getClientRects().length && !root.contains(d));
        if (hadModal && !modal && !drawer && lastWorkspaceFocus?.isConnected && lastWorkspaceFocus.closest('[inert]')) {
            const side = sides.right.contains(lastWorkspaceFocus) ? 'right' : 'left';
            requestAnimationFrame(() => toolbar.querySelector(`[data-toggle-pane=${side}]`)?.focus({ preventScroll: true }));
        }
        hadModal = modal;
        if (!drawer) return;
        if (modal === suspended) return;
        if (modal) rememberScroll(drawer);
        suspended = modal; paint();
        if (!suspended) restoreScroll(drawer);
        if (!suspended) requestAnimationFrame(() => { if (drawer && !suspended && !disposed) (drawerFocus?.isConnected ? drawerFocus : focusables(sides[drawer])[0])?.focus({ preventScroll: true }); });
    });
    modalObserver.observe(document.body, { childList: true, subtree: true });
    syncTabs(); paint();
    position = attachPosition(root, placeKey, side => `${root.dataset.selection ?? ''}:${activeTabs.get(side === 'center' ? 'center' : 'tools') ?? ''}`);
    root.dataset.ready = 'true';
    return {
        showCenter(tab) { if (drawer) closeDrawer(false); tabs(tab, false, 'center'); save(); },
        revealCenter() { if (drawer) closeDrawer(false); },
        resetLayout() { resetLayout(); },
        showTools(tab, group = 'tools') { if (isDrawer('right')) { if (drawer !== 'right') toggle('right'); } else { prefs.rightCollapsed = false; paint(); } if (tab) tabs(tab, false, group); save(); },
        dispose() { position?.dispose(); disposed = true; listeners.forEach(off => off()); size.disconnect(); selectionObserver.disconnect(); modalObserver.disconnect(); center.inert = toolbar.inert = false; }
    };
}
