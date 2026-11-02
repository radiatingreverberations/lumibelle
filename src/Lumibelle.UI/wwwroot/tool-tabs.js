// A tab row that cannot fit scrolls within itself. Keep the selected tab visible, without moving the
// page, when the row resizes or the selection changes.
export function attach(row) {
    const reveal = () => {
        const tab = row.querySelector('[role=tab][aria-selected=true]');
        if (!tab || row.scrollWidth <= row.clientWidth) return;
        const left = tab.offsetLeft - row.offsetLeft, right = left + tab.offsetWidth;
        if (left < row.scrollLeft) row.scrollLeft = left;
        else if (right > row.scrollLeft + row.clientWidth) row.scrollLeft = right - row.clientWidth;
    };
    const size = new ResizeObserver(reveal);
    const selection = new MutationObserver(reveal);
    size.observe(row);
    selection.observe(row, { subtree: true, attributes: true, attributeFilter: ['aria-selected'] });
    reveal();
    return { dispose() { size.disconnect(); selection.disconnect(); } };
}
