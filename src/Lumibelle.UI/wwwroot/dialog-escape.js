// Escape for inline dialogs. DialogEscape renders a hidden [data-dialog-escape] button inside a dialog
// whose close path should run on Escape. One listener, installed with the page, serves every dialog,
// so a key pressed as a dialog appears is never lost to a late per-dialog attachment.
// The listener runs in the capture phase, so it also works while focus is on MudBlazor's focus-trap
// sentinels. When focus has fallen back to the page (the focused control was disabled or removed),
// the topmost open dialog takes the key, as it would with focus inside it.
// Popovers (menus, selects) render outside the dialog and keep their own Escape.
document.addEventListener('keydown', event => {
    if (event.key !== 'Escape' || event.defaultPrevented || document.fullscreenElement) return;
    const target = event.target instanceof Element ? event.target : null;
    let dialog = target?.closest('.mud-dialog');
    if (!dialog) {
        if (target && target !== document.body && target !== document.documentElement) return;
        const dialogs = document.querySelectorAll('.mud-dialog');
        dialog = dialogs[dialogs.length - 1];
    }
    // A component inside the dialog (a reference manager, a region editor) may mark its own, more specific
    // close path after the dialog's own marker; the last marker wins.
    const markers = dialog?.querySelectorAll('[data-dialog-escape]') ?? [];
    const button = markers[markers.length - 1];
    if (!button) return;
    event.preventDefault();
    event.stopImmediatePropagation();
    button.click();
}, true);
