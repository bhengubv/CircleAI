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

using Android.App;
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

    /// <summary>
    /// The header every screen that is not the circle must wear: a small circle and
    /// the name, and tapping it goes back to the circle.
    /// </summary>
    /// <remarks>
    /// THE CIRCLE IS THE PRODUCT, AND EVERY OTHER SCREEN IS A DETOUR. That was true
    /// in the design and false in the app: "Or type instead" led to the typing
    /// screen and nothing led back. Every secondary screen called
    /// <c>ActionBar?.Hide()</c>, which switched off the Up arrow that
    /// <c>ParentActivity</c> would have drawn, so the only way home was the system
    /// Back gesture. An app that never offers to take you home does not think of
    /// home as home — and a person who types once stays in the text app forever.
    ///
    /// A CIRCLE, NOT A BACK ARROW. "Back" means the previous screen, which is a fact
    /// about history; this is a fact about the product. Tapping the mark takes you
    /// to the thing itself, from three screens deep, in one press.
    /// </remarks>
    /// <param name="title">
    /// What this screen is. Shown small beside the name, because a person who has
    /// navigated somewhere should be told where they are.
    /// </param>
    public static LinearLayout HomeBar(Activity a, string? title = null)
    {
        var bar = new LinearLayout(a) { Orientation = Orientation.Horizontal };
        bar.SetBackgroundColor(Surface);
        bar.SetPadding(Dp(a, 16), Dp(a, 12), Dp(a, 16), Dp(a, 12));
        bar.SetGravity(GravityFlags.CenterVertical);
        bar.SetMinimumHeight(Dp(a, 56));
        bar.Clickable = true;

        // THE MARK ITSELF, not a stand-in for it. This was a filled circle —
        // Rounded(a, Blue, 11f) — under a comment asserting it read as the same
        // object as the hero. It did not: the hero is an open ring with three
        // arcs leaving it, and a solid dot shares nothing with that but the
        // colour. Every screen except home was showing a different logo.
        //
        // Small enough that the arcs need room to be legible, so it is given 30dp
        // rather than the dot's 22 and the same 12dp gap to the wordmark.
        var mark = new MarkView(a);
        var markLp = new LinearLayout.LayoutParams(Dp(a, 30), Dp(a, 30));
        markLp.RightMargin = Dp(a, 12);
        bar.AddView(mark, markLp);

        var name = Label(a, "Circle AI", 17f, Ink, bold: true);
        bar.AddView(name);

        if (!string.IsNullOrWhiteSpace(title))
        {
            var where = Label(a, "  ·  " + title, 15f, InkSoft);
            bar.AddView(where);
        }

        // THE WHOLE BAR IS THE TARGET, not the 22dp dot. A control the size of a
        // full-stop is a control most people miss.
        bar.Click += (_, _) => GoHome(a);
        return bar;
    }

    /// <summary>Returns to the circle without stacking a second copy of it.</summary>
    /// <remarks>
    /// ClearTop + SingleTop reuses the home screen already underneath rather than
    /// building another on top, so pressing home three screens deep leaves ONE
    /// screen in the stack and the next Back press exits — instead of walking a
    /// person back through a pile of identical circles.
    /// </remarks>
    public static void GoHome(Activity a)
    {
        var home = new Intent(a, typeof(HomeActivity));
        home.SetFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop);
        a.StartActivity(home);
    }
}
