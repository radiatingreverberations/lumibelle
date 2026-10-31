// Keeps a scrolling preview on its newest text while it streams, unless the reader has scrolled up.
export function follow(element) {
    if (!element) return null;
    let pinned = true;
    const onScroll = () => { pinned = element.scrollHeight - element.scrollTop - element.clientHeight < 24; };
    const observer = new MutationObserver(() => { if (pinned) element.scrollTop = element.scrollHeight; });
    element.addEventListener('scroll', onScroll, { passive: true });
    observer.observe(element, { childList: true, subtree: true, characterData: true });
    element.scrollTop = element.scrollHeight;
    return { dispose: () => { observer.disconnect(); element.removeEventListener('scroll', onScroll); } };
}
