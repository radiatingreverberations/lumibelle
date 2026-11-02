// Inline dialogs render their focus-trap sentinels outside DialogContent.
// Listen on the actual dialog so Escape also works after wrapping keyboard focus.
// When focus has fallen back to the page (the focused control was disabled or removed),
// the topmost open dialog takes the key, as it would with focus inside it.
export function attach(button) {
    const dialog = button.closest('.mud-dialog');
    const close = event => {
        event.preventDefault();
        event.stopImmediatePropagation();
        button.click();
    };
    const keydown = event => {
        if (event.key !== 'Escape' || event.target.closest('.mud-dialog') !== dialog || document.fullscreenElement) return;
        close(event);
    };
    const unfocused = event => {
        if (event.key !== 'Escape' || document.fullscreenElement) return;
        if (event.target !== document.body && event.target !== document.documentElement) return;
        const dialogs = document.querySelectorAll('.mud-dialog');
        if (dialogs[dialogs.length - 1] !== dialog) return;
        close(event);
    };
    dialog?.addEventListener('keydown', keydown, true);
    document.addEventListener('keydown', unfocused);
    return {
        dispose() {
            dialog?.removeEventListener('keydown', keydown, true);
            document.removeEventListener('keydown', unfocused);
        }
    };
}
