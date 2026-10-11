// Forwards single-key shortcuts to .NET, skipping keys typed into form fields or pressed with modifiers.
export function register(dotnet) {
    const handler = e => {
        if (e.repeat || e.ctrlKey || e.altKey || e.metaKey) return;
        const t = e.target;
        if (t instanceof HTMLElement && (t.isContentEditable || t.closest('input, textarea, select'))) return;
        dotnet.invokeMethodAsync('OnHotkey', e.key.toLowerCase());
    };
    document.addEventListener('keydown', handler);
    return { dispose: () => document.removeEventListener('keydown', handler) };
}
