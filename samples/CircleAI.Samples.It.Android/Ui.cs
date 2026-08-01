// Theme.cs
//
// One place for colour, spacing and type, so the sample reads like a designed app
// rather than whatever each screen felt like at the time. A sample is the first
// thing a stranger sees of CircleAI; if it looks like a debug console they will
// assume the engine underneath is one too.
//
// The palette is deliberately three colours. Everything else is a tint of the
// slate, derived here rather than typed as a fresh hex string at each call site —
// that is how a codebase ends up with nine nearly-identical greys.

using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Util;
using Android.Views;
using Android.Widget;

namespace CircleAI.Samples.It.Mobile;

internal static class Ui
{
    public static readonly Color Blue  = Color.ParseColor("#2196F3");
    public static readonly Color Slate = Color.ParseColor("#2c3e50");
    public static readonly Color White = Color.ParseColor("#ffffff");

    // Surfaces: the slate darkened, so the whole app stays in one colour family.
    public static readonly Color Bg      = Shade(Slate, 0.42f);   // page
    public static readonly Color Surface = Shade(Slate, 0.62f);   // cards, bars
    public static readonly Color Raised  = Shade(Slate, 0.78f);   // pressed / inputs
    public static readonly Color Hairline = Shade(Slate, 0.95f);

    public static readonly Color Ink     = White;
    public static readonly Color InkSoft = Blend(White, Slate, 0.45f);

    /// <summary>Density-independent pixels → real pixels for this screen.</summary>
    public static int Dp(Context c, float dp) =>
        (int)TypedValue.ApplyDimension(ComplexUnitType.Dip, dp, c.Resources!.DisplayMetrics);

    static Color Shade(Color c, float factor) => Color.Rgb(
        (int)(c.R * factor), (int)(c.G * factor), (int)(c.B * factor));

    static Color Blend(Color a, Color b, float t) => Color.Rgb(
        (int)(a.R + (b.R - a.R) * t),
        (int)(a.G + (b.G - a.G) * t),
        (int)(a.B + (b.B - a.B) * t));

    /// <summary>A filled, rounded rectangle — used for buttons, cards and chips.</summary>
    public static GradientDrawable Rounded(Context c, Color fill, float radiusDp = 12f)
    {
        var d = new GradientDrawable();
        d.SetShape(ShapeType.Rectangle);
        d.SetColor(fill.ToArgb());
        d.SetCornerRadius(Dp(c, radiusDp));
        return d;
    }

    /// <summary>Outlined variant, for secondary actions that must not shout.</summary>
    public static GradientDrawable Outlined(Context c, Color stroke, float radiusDp = 12f)
    {
        var d = Rounded(c, Color.Transparent, radiusDp);
        d.SetStroke(Dp(c, 1.5f), stroke);
        return d;
    }

    /// <summary>
    /// A button that looks pressable: real height, real padding, rounded, and a
    /// visible pressed state. The stock Android button in a dark theme renders as
    /// grey-on-grey and reads as disabled.
    /// </summary>
    public static Button Action(Context c, string text, bool primary)
    {
        var b = new Button(c) { Text = text, TextSize = 15f };
        b.SetAllCaps(false);            // stock Android SHOUTS every button label
        // One line, always. A label that wraps mid-word ("Languag / es") is the
        // detail that makes a person doubt everything else on the screen; shrinking
        // the text to fit is the lesser evil.
        b.SetSingleLine(true);
        b.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        b.SetAutoSizeTextTypeUniformWithConfiguration(11, 15, 1, (int)ComplexUnitType.Sp);
        b.SetTextColor(primary ? White : Ink);
        b.Background = primary ? Rounded(c, Blue) : Outlined(c, Hairline);
        b.SetPadding(Dp(c, 18), Dp(c, 12), Dp(c, 18), Dp(c, 12));
        b.SetMinimumHeight(Dp(c, 48));            // the 48dp touch target, not 32
        b.StateListAnimator = null;                // kill the elevation wobble
        return b;
    }

    public static TextView Label(Context c, string text, float size, Color colour, bool bold = false)
    {
        var t = new TextView(c) { Text = text, TextSize = size };
        t.SetTextColor(colour);
        if (bold) t.SetTypeface(null, TypefaceStyle.Bold);
        return t;
    }

    public static LinearLayout.LayoutParams Fill(float weight = 0f) =>
        new(ViewGroup.LayoutParams.MatchParent,
            weight > 0 ? 0 : ViewGroup.LayoutParams.WrapContent, weight);
}
