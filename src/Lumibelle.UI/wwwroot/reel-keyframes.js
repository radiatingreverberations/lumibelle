const players = new WeakMap();
export function track(player) {
    if (!player || players.has(player)) return;
    const state = { time: null, selectedTime: null, target: null, callback: null, disposed: false };
    const frame = (_, metadata) => { state.time = metadata.mediaTime; if (!state.disposed) state.callback = player.requestVideoFrameCallback(frame); };
    state.play = () => { state.selectedTime = null; state.target = null; };
    state.seeking = () => {
        state.time = null;
        if (state.target === null || Math.abs(player.currentTime - state.target) > .00001) state.selectedTime = null;
        // An outstanding callback may still describe the frame from before this seek.
        if (state.callback !== null) player.cancelVideoFrameCallback(state.callback);
        if (player.requestVideoFrameCallback) state.callback = player.requestVideoFrameCallback(frame);
    };
    state.seeked = () => { state.target = null; };
    player.addEventListener('play', state.play);
    player.addEventListener('seeking', state.seeking);
    player.addEventListener('seeked', state.seeked);
    if (player.requestVideoFrameCallback) state.callback = player.requestVideoFrameCallback(frame);
    players.set(player, state);
}
export function seek(player, seconds, endSeconds) {
    player.pause();
    // Seek inside the frame interval, away from rounded presentation-time boundaries.
    const target = Math.max(0, seconds + Math.max(0, endSeconds - seconds) / 2);
    const state = players.get(player);
    if (state) { state.time = null; state.selectedTime = seconds; state.target = target; }
    player.currentTime = target;
}
export function position(player) {
    const state = players.get(player);
    const time = state?.selectedTime ?? (player.seeking ? player.currentTime : state?.time ?? player.currentTime);
    player.pause(); return Number.isFinite(time) ? time : 0;
}
export function dispose(player) {
    const state = players.get(player); if (!state) return; state.disposed = true;
    if (state.callback !== null) player.cancelVideoFrameCallback(state.callback);
    player.removeEventListener('play', state.play);
    player.removeEventListener('seeking', state.seeking);
    player.removeEventListener('seeked', state.seeked);
    player.pause(); players.delete(player);
}

// The image may already be gone when its load event reaches the server, after a list re-rendered.
export function imageSize(image) { return image?.isConnected ? [image.naturalWidth, image.naturalHeight] : null; }
