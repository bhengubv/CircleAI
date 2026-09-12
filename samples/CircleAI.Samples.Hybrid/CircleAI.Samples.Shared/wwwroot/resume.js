/*
    "The app came back to the front."

    WHY A SCREEN NEEDS THIS. Some things a screen shows are not the app's to
    change - a permission, a battery exemption, an OS setting. The app opens the
    system's own screen, the person answers it there, and control comes back with
    the answer already made. Nothing in Blazor's lifecycle fires on that return:
    no navigation happened, no state changed inside the app, so nothing re-renders
    and the screen goes on showing what was true before it handed over.

    That is exactly how "Fix it" earned a second bug. The row asking you to allow
    background running re-read the exemption in the same breath as opening the
    dialog - before anyone could possibly have answered - so it always read the
    old value, the row stayed put, and the button invited being tapped again by
    somebody who reasonably concluded nothing had happened.

    Imported as a module by the component that needs it, so no host document has
    to know it exists.
*/

// BOTH EVENTS, because neither is reliable alone across the two hosts. An
// Android WebView covered by another activity fires visibilitychange; a browser
// tab switched back to fires focus; some firmwares fire only one of them. Firing
// twice is harmless - the handler only re-reads a value - and firing never is
// the bug this exists to prevent.
const EVENTS = ['visibilitychange', 'focus', 'pageshow'];

export function watch(dotnet, method) {
    let last = 0;

    const fire = function () {
        if (document.visibilityState === 'hidden') return;

        // Coalesce, because the events above overlap: coming back to the front
        // can fire all three within a few milliseconds and there is no reason to
        // ask the same question three times.
        const now = Date.now();
        if (now - last < 250) return;
        last = now;

        try { dotnet.invokeMethodAsync(method); }
        catch (e) { /* the component went away between the event and the call */ }
    };

    EVENTS.forEach(function (e) { window.addEventListener(e, fire); });
    document.addEventListener('visibilitychange', fire);

    return {
        stop: function () {
            EVENTS.forEach(function (e) { window.removeEventListener(e, fire); });
            document.removeEventListener('visibilitychange', fire);
        }
    };
}
