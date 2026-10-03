export function frameAt(time, fps, count) {
    return Math.max(0, Math.min(count - 1, Math.floor(Math.max(0, time) * fps + 0.0001)));
}

export function attach(root, dotnet, baseUrl, count, fps, lossless = true) {
    const video = root.querySelector('video'), frame = root.querySelector('.take-paused-frame');
    const seek = root.querySelector('[data-action=seek]'), cache = new Map(), requests = new Map(), listeners = [];
    let index = 0, ready = false, disposed = false, version = 0, presented = null, callback = null, selecting = null;
    const on = (el, event, action) => { el.addEventListener(event, action); listeners.push(() => el.removeEventListener(event, action)); };
    const notify = error => { if (!disposed) dotnet.invokeMethodAsync('PlayerState', index, !video.paused, ready, error ?? null).catch(() => {}); };
    const prune = () => {
        for (const [i, url] of cache) if (Math.abs(i - index) > (lossless ? 2 : 0)) { URL.revokeObjectURL(url); cache.delete(i); }
        for (const [i, req] of requests) if (Math.abs(i - index) > (lossless ? 2 : 0)) { req.controller.abort(); requests.delete(i); }
    };
    const load = i => {
        if (cache.has(i)) return Promise.resolve(cache.get(i));
        if (requests.has(i)) return requests.get(i).promise;
        const controller = new AbortController();
        const promise = fetch(`${baseUrl}/frames/${i}`, { signal: controller.signal }).then(r => {
            if (!r.ok) throw new Error('Frame unavailable'); return r.blob();
        }).then(blob => {
            if (disposed || controller.signal.aborted || Math.abs(i - index) > (lossless ? 2 : 0)) throw new Error('Stale frame');
            const url = URL.createObjectURL(blob); cache.set(i, url); return url;
        }).finally(() => { if (requests.get(i)?.controller === controller) requests.delete(i); });
        requests.set(i, { controller, promise }); return promise;
    };
    const select = async i => {
        const token = ++version;
        index = Math.max(0, Math.min(count - 1, i)); seek.value = index; ready = false; frame.hidden = true; notify(); prune();
        try {
            const url = await load(index);
            if (disposed || token !== version || !video.paused) return -1;
            frame.src = url; await frame.decode();
            if (disposed || token !== version || !video.paused) return -1;
            frame.hidden = false; ready = true; root.dataset.frameIndex = index; notify();
            for (const near of (lossless ? [index - 1, index + 1] : [])) if (near >= 0 && near < count) load(near).catch(() => {});
            return index;
        } catch {
            if (!disposed && token === version) notify(lossless ? 'The lossless frame is unavailable. Retry to load it; video pixels cannot be saved as a substitute.' : 'The MP4 frame could not be extracted. Check FFmpeg and retry.');
            return -1;
        }
    };
    const settle = i => (selecting = select(i));
    const settlePause = () => {
        const token = ++version;
        ready = false; frame.hidden = true; notify();
        // The compositor can deliver its final presented timestamp after the pause event.
        selecting = new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))
            .then(() => disposed || token !== version || !video.paused ? -1 : select(frameAt(presented ?? video.currentTime, fps, count)));
    };
    const seekTo = i => {
        video.pause(); presented = null;
        const target = Math.max(0, Math.min(count - 1, i));
        // Seek inside the chosen frame: container timestamps can round its leading edge down.
        video.currentTime = (target + .5) / fps;
        return settle(target);
    };
    const track = (_, metadata) => {
        if (disposed) return;
        presented = metadata.mediaTime;
        if (!video.paused) { index = frameAt(presented, fps, count); seek.value = index; }
        callback = video.requestVideoFrameCallback(track);
    };
    if (video.requestVideoFrameCallback) callback = video.requestVideoFrameCallback(track);
    on(video, 'pause', settlePause);
    on(video, 'seeking', () => { ++version; ready = false; presented = null; frame.hidden = true; notify(); });
    on(video, 'seeked', () => { if (video.paused) settle(frameAt(video.currentTime, fps, count)); });
    on(video, 'play', () => { ++version; frame.hidden = true; ready = false; notify(); });
    on(video, 'timeupdate', () => { if (!video.paused && !video.requestVideoFrameCallback) { index = frameAt(video.currentTime, fps, count); seek.value = index; } });
    on(video, 'error', () => notify(lossless ? 'Video playback is unavailable. You can still browse and save archived frames.' : 'Video playback is unavailable. Frame extraction remains available if the saved MP4 can be read by FFmpeg.'));
    on(root.querySelector('[data-action=play]'), 'click', () => video.paused ? video.play().catch(() => notify('Playback could not start.')) : video.pause());
    for (const button of root.querySelectorAll('.take-save-frame, .take-continue-frame')) on(button, 'click', () => video.pause());
    on(root.querySelector('[data-action=previous]'), 'click', () => seekTo(index - 1));
    on(root.querySelector('[data-action=next]'), 'click', () => seekTo(index + 1));
    on(seek, 'input', () => seekTo(Number(seek.value)));
    on(root.querySelector('[data-action=volume]'), 'input', e => video.volume = Number(e.target.value));
    on(root.querySelector('[data-action=mute]'), 'click', e => { video.muted = !video.muted; e.currentTarget.setAttribute('aria-pressed', String(video.muted)); e.currentTarget.textContent = video.muted ? 'Muted' : 'Sound'; e.currentTarget.setAttribute('aria-label', video.muted ? 'Unmute audio' : 'Mute audio'); });
    on(root.querySelector('[data-action=fullscreen]'), 'click', () => (document.fullscreenElement ? document.exitFullscreen() : root.requestFullscreen()).catch(() => {}));
    settle(0);
    return {
        pause: () => video.pause(),
        async pauseForSave() {
            if (!video.paused) await new Promise(resolve => { video.addEventListener('pause', resolve, { once: true }); video.pause(); });
            if (ready) return index;
            let pending;
            do { pending = selecting; if (pending) await pending; } while (!disposed && pending !== selecting);
            return ready && !disposed ? index : -1;
        },
        retry: () => { if (cache.has(index)) { URL.revokeObjectURL(cache.get(index)); cache.delete(index); } return settle(index); },
        step: delta => seekTo(index + delta),
        dispose() {
            disposed = true; ++version; video.pause(); listeners.forEach(fn => fn());
            if (callback !== null) video.cancelVideoFrameCallback(callback);
            for (const req of requests.values()) req.controller.abort();
            for (const url of cache.values()) URL.revokeObjectURL(url);
            requests.clear(); cache.clear(); frame.removeAttribute('src');
        }
    };
}
