/*
    Appearance: dark by default and by identity, light when a person turns it on
    in Settings. Two states, one switch - the second half of the same three
    colours, not a different design.

    ONE OWNER FOR THE BOOTSTRAP BACKGROUND. The stylesheet arrives a frame after
    the document, and each host guards that frame with a solid background so a
    dark app does not open on white. When the choice is light, that guard has to
    be light too, or a light app opens on a dark flash - the same flash in the
    other direction. Both bootstrap colours live here, next to the choice that
    picks between them, rather than being copied into every host document.
    They mirror --page in app.css; if that changes, change it here.

    THIS RUNS BEFORE THE STYLESHEET. It is a plain synchronous script in the
    <head>, so data-theme is on <html> before app.css is applied and the first
    paint is already the right theme. color-scheme is carried by the stylesheet
    off that same attribute, so the platform's own controls - the language
    select's dropdown, the caret, autofill - follow the theme instead of
    fighting it.
*/
(function () {
    var KEY = 'circleai.theme';                 // 'light', or absent for dark
    var DARK = '#080B0E', LIGHT = '#ffffff';     // mirror --page in app.css

    function paint(light) {
        var root = document.documentElement;
        if (light) { root.setAttribute('data-theme', 'light'); }
        else { root.removeAttribute('data-theme'); }

        // Override the host's static flash-guard to match the chosen theme, for
        // the one frame before app.css lands. A later <style> wins over the
        // host's earlier one; the inline html background covers <html> itself.
        var bg = light ? LIGHT : DARK;
        root.style.background = bg;
        var id = 'circleai-boot-bg';
        var s = document.getElementById(id);
        if (!s) { s = document.createElement('style'); s.id = id; document.head.appendChild(s); }
        s.textContent = 'html,body{background:' + bg + '}';
    }

    function saved() {
        try { return localStorage.getItem(KEY) === 'light'; } catch (e) { return false; }
    }

    paint(saved());

    window.circleaiTheme = {
        // Whether light is the current choice - read once by Settings to show
        // the toggle in the right position.
        isLight: saved,

        // Turn light on or off: remember it and apply it to the live page at
        // once, so the whole app reflows to the new theme without a reload.
        set: function (light) {
            try {
                if (light) { localStorage.setItem(KEY, 'light'); }
                else { localStorage.removeItem(KEY); }
            } catch (e) { /* private mode or storage off: this launch still flips */ }
            paint(!!light);
        }
    };
})();
