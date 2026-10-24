// TourActions.cs
//
// What a tour button does when its route is a "#" verb rather than a place.
//
// IT LIVED IN ONE PAGE. Setup turned "#microphone" into an ask and a sentence; Home
// shows the same tour while a download runs and rendered every route as a link - so
// the step "Allow the microphone" on Home was <a href="#microphone">, which moves
// nothing and asks nothing, on the very screen a fresh install opens on. One mapping,
// here, for both.

namespace CircleAI.Assistant;

/// <summary>Runs the tour routes that act on the spot.</summary>
public static class TourActions
{
    /// <summary>Where an unknown "#" route is reported. Null discards it.</summary>
    /// <remarks>
    /// A "#" ROUTE NOBODY HANDLES RENDERS A BUTTON THAT DOES NOTHING, IN SILENCE.
    /// The switch below returns null for an unrecognised route and null is also the
    /// honest answer for a route that worked and had nothing to say - so renaming
    /// "#background" in one place would leave a live button that is a no-op, and
    /// nothing anywhere would know. Set by a head that has a log.
    /// </remarks>
    public static Action<string>? Unknown { get; set; }

    /// <summary>Whether <paramref name="route"/> is something to do here rather than somewhere to go.</summary>
    public static bool ActsHere(string? route) => route is { Length: > 1 } && route[0] == '#';

    /// <summary>
    /// Do what <paramref name="route"/> names and say how it went, in the person's
    /// words; null when there is nothing to say or the route is not one of these.
    /// </summary>
    /// <remarks>
    /// THE MICROPHONE HAS THREE ANSWERS. ISetup.AllowMicrophoneAsync returns null
    /// when nothing was answered - the dialog is still up and the two-minute wait
    /// gave out, or this head cannot ask - and that was being rendered as "Not
    /// allowed. It can still be typed to." under a dialog the person was about to
    /// allow.
    /// </remarks>
    public static async Task<string?> RunAsync(ISetup setup, string route, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(setup);

        switch (route)
        {
            case MicrophoneAsk.Route:
                return (await setup.AllowMicrophoneAsync(ct).ConfigureAwait(false)) switch
                {
                    true  => MicrophoneAsk.Allowed,
                    false => MicrophoneAsk.NotAllowed,
                    null  => MicrophoneAsk.StillAsking,
                };

            // A phone that has no screen for the vendor exemption gets told what to
            // look for rather than a button that failed in silence - these screens
            // move between firmwares and there is no list that stays right.
            case BackgroundRoute:
                return await setup.AllowBackgroundAsync(ct).ConfigureAwait(false)
                    ? null
                    : "This phone has no screen for that. Find Circle AI in your "
                    + "battery settings and allow it to run in the background.";

            default:
                Report(route);
                return null;
        }
    }

    /// <summary>The tour route that asks to be left running by the battery settings.</summary>
    /// <remarks>
    /// A CONSTANT BECAUSE THE STRING HAD THREE OWNERS - this switch, the step that
    /// produces it, and the screens that decide whether to draw a button or a link.
    /// Renaming it in one of them produced a button that silently did nothing.
    /// </remarks>
    public const string BackgroundRoute = "#background";

    private static void Report(string route)
    {
        if (!ActsHere(route)) return;
        try { Unknown?.Invoke($"tour: nothing handles the route \"{route}\" - its button does nothing"); }
        catch { /* a log line is never worth failing a tour step for */ }
    }
}
