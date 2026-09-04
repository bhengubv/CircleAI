namespace CircleAI.Samples.It;

/// <summary>Where a screen appears, if anywhere.</summary>
/// <remarks>
/// A SCREEN THAT IS IN NO MENU IS A SCREEN NOBODY CAN REACH, and this app had
/// six of them. Declaring the surfaces here means adding a page and forgetting
/// to offer it is a thing you can SEE rather than a thing somebody discovers by
/// looking for a feature that is advertised on the home screen.
/// </remarks>
[Flags]
public enum Surface
{
    /// <summary>Reachable only from another screen. Deliberate, not an oversight.</summary>
    None = 0,

    /// <summary>In the bar at the bottom, on every screen.</summary>
    /// <remarks>
    /// WHERE YOU GO. The bar is places; the strip is what the app is doing. The
    /// two were confused once and the result was a bar carrying Settings while
    /// the screens that do the work had no entry anywhere.
    /// </remarks>
    TabBar = 1,

    /// <summary>
    /// One of the app's modes, on the strip at the top of every screen.
    /// </summary>
    /// <remarks>
    /// WHAT THE CIRCLE DOES. Three peers you switch between - talk, translate,
    /// transcribe - not a hierarchy you climb, so the strip stays put and stays
    /// lit rather than changing as you go deeper.
    /// <para>
    /// It replaces a mode setting that lived four taps into Settings and silently
    /// repointed a link on Home labelled "Or type instead". A mode that changes
    /// what the biggest control on the screen does has no business being
    /// invisible.
    /// </para>
    /// </remarks>
    Mode = 8,

    /// <summary>Somewhere in the Services grid.</summary>
    Services = 2,

    /// <summary>Can be asked for out loud.</summary>
    Voice = 4,
}

/// <summary>One screen this app has, and every way of getting to it.</summary>
/// <param name="Route">The path. The same string the page declares with @page.</param>
/// <param name="Title">What it is called, wherever it is offered.</param>
/// <param name="Where">Which surfaces offer it.</param>
/// <param name="Words">
/// What somebody might say to mean it. Distinctive ones only - a phrase common
/// enough to turn up mid-question will hijack it, and a hijacked question is
/// worse than a missed command. Empty means it has no voice route, which is a
/// choice rather than a gap: "you" and "type" cannot be named distinctively.
/// </param>
/// <param name="ModeTitle">
/// What the STRIP calls it, when that differs from what the place is called.
/// </param>
public sealed record AppRoute(
    string Route,
    string Title,
    Surface Where = Surface.None,
    IReadOnlyList<string>? Words = null,
    string? ModeTitle = null)
{
    /// <summary>The words that mean this place, never null.</summary>
    public IReadOnlyList<string> Spoken => Words ?? [];

    /// <summary>
    /// The strip's label. A MODE IS NOT A PLACE, and the first one proves it:
    /// the screen is called Home and the mode is "Tap n Talk", because the strip
    /// answers "what will the circle do" and not "where am I".
    /// </summary>
    public string OnStrip => ModeTitle ?? Title;

    public bool On(Surface surface) => (Where & surface) == surface;
}

/// <summary>
/// Every screen the app has, declared once.
/// </summary>
/// <remarks>
/// THREE TABLES OWNED THIS AND ALL THREE DISAGREED. Measured 2026-09-05: the
/// pages declared fourteen routes, the Services catalogue named four, and the
/// voice table named ten - including six screens the menu had never heard of,
/// while four real pages appeared in neither.
///
/// <para>
/// The cost was not theoretical. TRANSLATE - the thing on the front of the
/// app - was in no menu at all. From Home the only way to it was a link
/// labelled "Or type instead", which silently pointed at Chat or at the
/// interpreter depending on a mode set four taps deep in Settings; from any
/// other screen there was no way to reach either. The tab bar carried Settings
/// and not the two screens where the app does its work. A person could not find
/// the product.
/// </para>
/// <para>
/// The comment that produced it is still in MainLayout and is worth keeping as
/// a warning: removing Type from the bar was justified with "Home carries 'Or
/// type instead', so nothing is lost". Something was lost. A fallback on ONE
/// screen is not a replacement for a way in from every screen.
/// </para>
/// <para>
/// So this is the one owner. The tab bar renders what says TabBar, the voice
/// matcher reads what says Voice, and Services offers what says Services. A
/// route that is not here cannot be offered anywhere; a route here that no page
/// declares is caught by a test rather than by a person tapping into Not Found.
/// </para>
/// </remarks>
public static class AppRoutes
{
    /// <summary>
    /// Ordered as the tab bar shows them, because that order is the product's
    /// opinion about itself and it should be legible in one place.
    /// </summary>
    public static IReadOnlyList<AppRoute> All { get; } =
    [
        new("home", "Home", Surface.TabBar | Surface.Mode | Surface.Voice,
            ["home screen", "go home", "the home page"],
            ModeTitle: "Tap n Talk"),

        // THE HEADLINE FEATURE, AND IT WAS IN NO MENU AT ALL. It is a MODE, not
        // a tab: switching to it changes what the circle does, which is why it
        // belongs on the strip beside the other two rather than in a bar of
        // places to go.
        new("translate", "Translate", Surface.Mode | Surface.Services | Surface.Voice,
            ["translate", "translating", "translation", "translator", "interpret",
             "interpreter", "interpreting"]),

        // THE ONE THE APP COULD ALREADY DO AND NEVER OFFERED. DictateAsync has
        // been on IConversation the whole time - speech to text, built and
        // wired - with no screen, no menu entry and no way to ask for it. An
        // assistant that cannot take down a meeting is missing the thing people
        // most want a recorder for.
        new("transcribe", "Transcribe", Surface.Mode | Surface.Services | Surface.Voice,
            ["transcribe", "transcript", "take notes", "write this down",
             "record this", "minutes"]),

        new("services", "Services", Surface.TabBar | Surface.Voice,
            ["services", "service list"]),

        // In the bar because it is WHERE YOU GO, not what the app does. Its
        // absence from voice is deliberate: "you" cannot be matched without
        // eating half of everything anybody says.
        new("you", "You", Surface.TabBar),

        // LAST, where a settings item belongs: what you reach for when something
        // is wrong, not what you came to do. Ordering this list wrong once
        // already put it ahead of You in the bar.
        new("settings", "Settings", Surface.TabBar | Surface.Voice,
            ["settings", "preferences", "options"]),

        new("chat", "Type", Surface.Services | Surface.Voice,
            ["chat", "conversation", "message it"]),

        new("languages", "Languages", Surface.Services | Surface.Voice,
            ["languages", "language list", "which languages"]),

        new("career", "Your CV", Surface.Services | Surface.Voice,
            ["my cv", "the cv", "curriculum vitae", "resume", "career"]),

        new("job-spec", "Aim at a job", Surface.Voice,
            ["job spec", "job description", "aim at a job", "apply for"]),

        new("wake", "Hey B", Surface.Voice,
            ["wake word", "wake phrase", "answer to its name", "hey bee"]),

        new("abilities", "What it can do", Surface.Voice,
            ["what can you do", "what it can do", "abilities", "features"]),

        // Stages, not destinations: each owns the whole screen and leaves on its
        // own. Offering them in a menu would be offering somewhere to go before
        // there is anywhere worth going.
        new("", "Loading", Surface.None),
        new("setup", "Setup", Surface.None),
        new("not-found", "Not found", Surface.None),
    ];

    /// <summary>What the bar shows, in order.</summary>
    public static IReadOnlyList<AppRoute> TabBar { get; } =
        All.Where(r => r.On(Surface.TabBar)).ToList();

    /// <summary>The three things the app does, in the order the strip shows them.</summary>
    public static IReadOnlyList<AppRoute> Modes { get; } =
        All.Where(r => r.On(Surface.Mode)).ToList();

    /// <summary>Everything that can be asked for out loud.</summary>
    public static IReadOnlyList<AppRoute> Spoken { get; } =
        All.Where(r => r.On(Surface.Voice) && r.Spoken.Count > 0).ToList();

    /// <summary>One route by path, or null.</summary>
    public static AppRoute? For(string? route) =>
        route is null ? null
            : All.FirstOrDefault(r => string.Equals(r.Route, route.Trim('/'), StringComparison.OrdinalIgnoreCase));

    /// <summary>Which mode a route IS, when it is one.</summary>
    /// <remarks>
    /// ONE MAPPING, IN ONE DIRECTION, so the strip and the stored setting cannot
    /// come apart. The alternative - a switch in the strip and another in
    /// Settings and a third wherever the circle decides what to do - is how the
    /// mode came to be invisible in the first place.
    /// </remarks>
    public static AppMode? ModeOf(string? route) => route?.Trim('/').ToLowerInvariant() switch
    {
        "home"       => AppMode.Assistant,
        "translate"  => AppMode.Translator,
        "transcribe" => AppMode.Transcribe,
        _            => null,
    };

    /// <summary>Where a mode lives.</summary>
    public static string RouteOf(AppMode mode) => mode switch
    {
        AppMode.Translator => "translate",
        AppMode.Transcribe => "transcribe",
        _                  => "home",
    };
}
