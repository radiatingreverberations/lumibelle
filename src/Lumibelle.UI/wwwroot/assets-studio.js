let extractionFocus;
export function focusAssetCategoryFilter() {
    document.querySelector('.asset-category-filter button')?.focus({ preventScroll: true });
}
export function focusAssetLibraryOptions() {
    requestAnimationFrame(() => requestAnimationFrame(() => {
        document.querySelector('.asset-library [aria-label="Asset library options"]')?.focus({ preventScroll: true });
    }));
}
export function rememberExtractionFocus() { extractionFocus = document.activeElement; }
export function restoreExtractionFocus() {
    const target = extractionFocus; extractionFocus = null;
    requestAnimationFrame(() => requestAnimationFrame(() => {
        if (target?.isConnected && !target.disabled) target.focus();
        else document.querySelector('.asset-library-extraction .request-action-button')?.focus();
    }));
}
const cropCleanups = new WeakMap();
export function detachCropGestures(stage) { cropCleanups.get(stage)?.(); cropCleanups.delete(stage); }

export function focusLookActions(section) {
    section?.querySelector('.look-actions-menu button')?.focus();
}
export function focusReferenceImageActions(menu) {
    menu?.querySelector('button')?.focus();
}

export function attachCropGestures(stage, reference) {
    cropCleanups.get(stage)?.();
    const selection = stage?.querySelector('.crop-selection');
    if (!selection) return;

    let drag;
    const clamp = (value, minimum, maximum) => Math.min(maximum, Math.max(minimum, value));
    const focusFor = (offset, size) => size >= 0.999999 ? 50 : clamp(offset / (1 - size) * 100, 0, 100);
    const render = value => {
        selection.style.left = `${value.x * 100}%`;
        selection.style.top = `${value.y * 100}%`;
        selection.style.width = `${value.width * 100}%`;
        selection.style.height = `${value.height * 100}%`;
        selection.dataset.x = value.x;
        selection.dataset.y = value.y;
        selection.dataset.width = value.width;
        selection.dataset.height = value.height;
        selection.dataset.zoom = value.zoom;
        drag.current = value;
    };

    const pointerDown = event => {
        if (event.button !== 0) return;
        const handle = event.target.closest('[data-crop-handle]');
        if (!handle && !event.target.closest('.crop-selection')) return;
        event.preventDefault();
        const bounds = stage.getBoundingClientRect();
        const start = {
            x: Number(selection.dataset.x), y: Number(selection.dataset.y),
            width: Number(selection.dataset.width), height: Number(selection.dataset.height),
            zoom: Number(selection.dataset.zoom)
        };
        drag = {
            pointerId: event.pointerId,
            mode: handle?.dataset.cropHandle ?? 'move', bounds, start, current: start,
            pointerX: event.clientX, pointerY: event.clientY,
            maxWidth: start.width * start.zoom, maxHeight: start.height * start.zoom
        };
        selection.setPointerCapture(event.pointerId);
    };

    const pointerMove = event => {
        if (!drag || event.pointerId !== drag.pointerId) return;
        event.preventDefault();
        const { start, bounds, mode } = drag;
        if (mode === 'move') {
            const x = clamp(start.x + (event.clientX - drag.pointerX) / bounds.width, 0, 1 - start.width);
            const y = clamp(start.y + (event.clientY - drag.pointerY) / bounds.height, 0, 1 - start.height);
            render({ ...start, x, y });
            return;
        }

        const pointerX = clamp((event.clientX - bounds.left) / bounds.width, 0, 1);
        const pointerY = clamp((event.clientY - bounds.top) / bounds.height, 0, 1);
        const desiredSize = mode === 'left' ? start.x + start.width - pointerX :
            mode === 'right' ? pointerX - start.x :
            mode === 'top' ? start.y + start.height - pointerY : pointerY - start.y;
        const maximum = mode === 'left' || mode === 'right' ? drag.maxWidth : drag.maxHeight;
        const zoom = clamp(maximum / Math.max(0.001, desiredSize), 1, Number(stage.dataset.maxZoom) || 4);
        const width = drag.maxWidth / zoom;
        const height = drag.maxHeight / zoom;
        const centerX = start.x + start.width / 2;
        const centerY = start.y + start.height / 2;
        const x = clamp(mode === 'left' ? start.x + start.width - width :
            mode === 'right' ? start.x : centerX - width / 2, 0, 1 - width);
        const y = clamp(mode === 'top' ? start.y + start.height - height :
            mode === 'bottom' ? start.y : centerY - height / 2, 0, 1 - height);
        render({ x, y, width, height, zoom });
    };

    const pointerUp = event => {
        if (!drag || event.pointerId !== drag.pointerId) return;
        const value = drag.current;
        const zoom = value.zoom ?? drag.start.zoom;
        selection.releasePointerCapture?.(event.pointerId);
        drag = undefined;
        reference.invokeMethodAsync('ApplyCropGesture', zoom,
            focusFor(value.x, value.width), focusFor(value.y, value.height));
    };

    selection.addEventListener('pointerdown', pointerDown);
    selection.addEventListener('pointermove', pointerMove);
    selection.addEventListener('pointerup', pointerUp);
    selection.addEventListener('pointercancel', pointerUp);
    const cleanup = () => {
        selection.removeEventListener('pointerdown', pointerDown);
        selection.removeEventListener('pointermove', pointerMove);
        selection.removeEventListener('pointerup', pointerUp);
        selection.removeEventListener('pointercancel', pointerUp);
    };
    cropCleanups.set(stage, cleanup);
}

// Wait for the provider's dialog fragment to update before restoring focus.
window.lumibelleAssets = {
    focusEmptyLibrary() {
        requestAnimationFrame(() => requestAnimationFrame(() => {
            const create = document.querySelector('.asset-library [aria-label="Create asset"]');
            const toggle = document.querySelector('.studio-workspace[data-studio="Assets"] [data-toggle-pane="left"]');
            (create?.getClientRects().length && !create.closest('[inert]') ? create : toggle)?.focus({ preventScroll: true });
        }));
    },
    focusEditPrompt() {
        requestAnimationFrame(() => requestAnimationFrame(() => {
            const root = document.querySelector('.studio-workspace[data-studio="Assets"]');
            if (!root?.querySelector('.workspace-right')?.getClientRects().length)
                root?.querySelector('[data-toggle-pane="right"]')?.click();
            root?.querySelector('[data-workspace-tab="Prompt"]')?.click();
            document.querySelector('#image-prompt')?.focus();
        }));
    },
    focusEnhance() {
        requestAnimationFrame(() => requestAnimationFrame(() =>
            (document.querySelector('.enhance-button:not(:disabled)') || document.querySelector('#image-prompt'))?.focus()));
    },
    focusCropControl(label) {
        requestAnimationFrame(() => requestAnimationFrame(() => {
            [...document.querySelectorAll('.generation-panel button[aria-label]')]
                .find(button => button.getAttribute('aria-label') === label)?.focus();
        }));
    },
    // Rename opens from a menu, which gives focus back to its button as it closes; on a slow machine that came after the
    // name was focused. So wait for the field and for the menu to let go of focus. (A rename dialog applies no initial
    // focus of its own, so its focus trap cannot take the name's focus either.)
    focusMediaName(id) {
        return new Promise(resolve => {
            const started = performance.now();
            const attempt = () => {
                const input = document.querySelector(`.media-details-dialog [data-media-name="${CSS.escape(id)}"]`);
                const active = document.activeElement;
                // A closing menu keeps its popover open briefly and then gives focus back to its button: wait for that, at most a second.
                const menuClosing = document.querySelector('.mud-popover-open .mud-menu-list, .mud-popover-open .mud-list') && performance.now() - started < 1000;
                const waiting = !input || active?.closest('.mud-menu-item, .mud-list, .mud-popover') || (active === document.body && menuClosing);
                if (waiting) { if (performance.now() - started < 3000) requestAnimationFrame(attempt); else resolve(false); return; }
                // Never move typing that has already started in another field of the dialog.
                if (active !== input && input.closest('.media-details-dialog')?.contains(active) && active.matches('input,textarea,select')) { resolve(false); return; }
                input.focus(); input.select(); resolve(true);
                // If the menu still hands focus back to its button after this, take it back once, unless the user clicked or typed.
                let acted = false;
                const act = () => { acted = true; };
                const back = event => {
                    if (acted || !input.isConnected || !event.target.closest?.('.mud-menu')) return;
                    stop(); input.focus(); input.select();
                };
                const stop = () => { document.removeEventListener('focusin', back, true); document.removeEventListener('pointerdown', act, true); document.removeEventListener('keydown', act, true); };
                document.addEventListener('focusin', back, true); document.addEventListener('pointerdown', act, true); document.addEventListener('keydown', act, true);
                setTimeout(stop, 1500);
            };
            requestAnimationFrame(() => requestAnimationFrame(attempt));
        });
    },
    focusReview(label) {
        requestAnimationFrame(() => requestAnimationFrame(() => {
            const dialog = document.querySelector('.image-review-dialog');
            const target = label.startsWith('Image ') ? [...document.querySelectorAll('.image-edit-reference-manager .reference-action-buttons button')].find(b => b.getAttribute('aria-label') === label) : [...(dialog?.querySelectorAll('button[aria-label],input[aria-label],textarea[aria-label]') || [])].find(b => b.getAttribute('aria-label') === label);
            // A delayed initial focus must not move typing into another field.
            const active = document.activeElement;
            if (active !== target && dialog?.contains(active) && active.matches('input,textarea,select,[contenteditable=true]')) return;
            target?.focus();
            if (label === 'Name') target?.select();
        }));
    },
    focusLibrary() {
        requestAnimationFrame(() => requestAnimationFrame(() =>
            (document.querySelector('.reference-image') || document.querySelector('h1'))?.focus()));
    }
};
export function selectAssetName() { const input = document.getElementById('asset-name'); input?.focus(); input?.select(); }
export function revealAssetMedia(id) {
    const card = document.querySelector(`[data-media-id="${CSS.escape(id)}"]`);
    const group = card?.closest('details.asset-gallery-group');
    if (group) group.open = true;
    card?.scrollIntoView({ block: 'nearest', inline: 'nearest' });
    card?.querySelector('.media-select')?.focus();
}


export function focusAssetCreation() {
    requestAnimationFrame(() => requestAnimationFrame(() =>
        document.querySelector('.asset-tools-header [aria-label="Create media type"]')?.focus({ preventScroll: true })));
}
// After Generate queues a request the composer clears and Generate is disabled until the next
// prompt. Keep keyboard focus in the composer rather than losing it with the disabled button,
// unless the author has already moved elsewhere or a dialog is open.
export function focusPromptAfterSubmit(origin) {
    requestAnimationFrame(() => requestAnimationFrame(() => {
        const active = document.activeElement;
        const dialog = [...document.querySelectorAll('.mud-dialog')].some(d => d.getClientRects().length && getComputedStyle(d).visibility !== 'hidden');
        if (dialog || active && active !== document.body && !origin?.contains(active)) return;
        document.querySelector('#image-prompt')?.focus({ preventScroll: true });
    }));
}
export function focusAssetCreationFields(kind) {
    requestAnimationFrame(() => requestAnimationFrame(() =>
        document.querySelector(kind === 'Reel' ? '.reel-form input' : '#image-prompt')?.focus({ preventScroll: true })));
}
