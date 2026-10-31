// Pairs a refined final with its muted source clip: the final's controls drive both players.
export function pair(final, source) {
    if (!final || !source) return null;
    const sync = () => {
        const target = Math.min(final.currentTime, Number.isFinite(source.duration) ? source.duration : final.currentTime);
        if (Math.abs(source.currentTime - target) > 0.04) source.currentTime = target;
    };
    const handlers = {
        play: () => { sync(); source.play().catch(() => {}); },
        pause: () => { source.pause(); sync(); },
        seeked: sync,
        seeking: sync,
        ratechange: () => { source.playbackRate = final.playbackRate; },
        timeupdate: () => { if (Math.abs(source.currentTime - final.currentTime) > 0.25) sync(); }
    };
    source.muted = true;
    for (const [name, handler] of Object.entries(handlers)) final.addEventListener(name, handler);
    return { dispose: () => { for (const [name, handler] of Object.entries(handlers)) final.removeEventListener(name, handler); } };
}
