window.lumibelleShots = {
    async focusAfterDeletion(target) {
        target = target?.querySelector('button') ?? target;
        const frame = () => new Promise(resolve => requestAnimationFrame(resolve));
        const until = performance.now() + 2000;
        while (performance.now() < until && document.querySelector('.shot-delete-dialog')?.getClientRects().length) await frame();
        await frame(); await frame();
        if (document.querySelector('.shot-delete-dialog')?.getClientRects().length) return;
        const visible = target?.isConnected && target.getClientRects().length && !target.closest('[inert]');
        (visible ? target : document.querySelector('.studio-workspace[data-studio="Shots"] [data-toggle-pane=left]'))?.focus({ preventScroll: true });
    },
    async focusReference(id) {
        // Mud and the mobile workspace restore focus when their dialog closes.
        // Reveal the new row after that restoration, not during the closing render.
        const frame = () => new Promise(resolve => requestAnimationFrame(resolve));
        const until = performance.now() + 2000;
        while (performance.now() < until && [...document.querySelectorAll('.mud-dialog')].some(d => d.getClientRects().length)) await frame();
        await frame(); await frame();
        const row = document.querySelector(`[data-reference-id="${CSS.escape(id)}"]`);
        if (!row || row.closest('[inert]')) return;
        row.scrollIntoView({ block: 'nearest' });
        row.querySelector('select,button')?.focus({ preventScroll: true });
    },
    async restoreReviewFocus(closed) {
        await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
        const active = document.activeElement;
        if (!closed) {
            const player = document.querySelector('.shot-review-dialog .take-player');
            const trigger = [...(player?.querySelectorAll('[data-take-panel]') ?? [])].find(button => button.dataset.takePanel === player.dataset.lastPanel);
            (trigger ?? player?.querySelector('.take-save-frame'))?.focus();
        }
        // Mud restores a valid original trigger itself. An automatically opened review
        // can have no surviving trigger (generation disabled it), so provide a fallback.
        else if (!active || active === document.body || !active.isConnected || active.closest('.shot-review-dialog'))
            document.getElementById('review-latest-takes')?.focus();
    },
    async focusReferenceGuidance() {
        await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
        const section = document.querySelector('.shot-reference-dialog .reference-guidance');
        (section?.querySelector('textarea') || section?.querySelector('[data-guidance-customize]'))?.focus();
    },
    focusTake(id) {
        const dialog = document.querySelector('.shot-review-dialog');
        const target = id && dialog?.querySelector(`[data-shot-take="${CSS.escape(id)}"]`);
        (target || dialog?.querySelector('.mud-dialog-actions button:not(:disabled)'))?.focus();
    },
    async download(name, text) {
        const host = await import('./host-bridge.js');
        if (host.isDesktop()) return host.saveText(name, text);
        const url = URL.createObjectURL(new Blob([text], {type: 'application/json'}));
        const anchor = document.createElement('a'); anchor.href = url; anchor.download = name;
        anchor.click(); setTimeout(() => URL.revokeObjectURL(url), 1000);
    },
    async downloadResource(name, resourceUrl) {
        const host = await import('./host-bridge.js');
        if (host.isDesktop()) return host.saveResource(name, resourceUrl);
        // HEAD is cheap: exports are already rendered and immutable. Surface an
        // expired/missing artifact before asking the browser to stream its download.
        const response = await fetch(resourceUrl, { method: 'HEAD', cache: 'no-store' });
        if (!response.ok) throw new Error('This export is unavailable or expired. Export the saved cut again.');
        host.download(name, resourceUrl);
    }
};

// Excerpt playback never changes or trims the uploaded original.
for (const name of ['play', 'timeupdate']) document.addEventListener(name, event => {
    const audio = event.target;
    if (!(audio instanceof HTMLAudioElement) || !audio.hasAttribute('data-excerpt-end')) return;
    const start = Number(audio.dataset.excerptStart), end = Number(audio.dataset.excerptEnd);
    if (name === 'play' && (audio.currentTime < start || audio.currentTime >= end)) audio.currentTime = start;
    else if (name === 'timeupdate' && audio.currentTime >= end) audio.pause();
}, true);
