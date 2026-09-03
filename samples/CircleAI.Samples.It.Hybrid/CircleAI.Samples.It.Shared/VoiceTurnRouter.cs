using Microsoft.AspNetCore.Components;

namespace CircleAI.Samples.It.Shared;

/// <summary>
/// Watches a turn for "take me somewhere", and takes them there.
/// </summary>
/// <remarks>
/// ONE OWNER FOR THE WIRING, not just for the rule. VoiceDestinations already
/// owns WHICH sentences are requests; this owns what happens when one arrives -
/// end the turn, say where it is going, go. Both microphone buttons need that,
/// and this codebase has been bitten twice in a week by the same behaviour
/// living in two components: two MarkState fields that never agreed, and two
/// keyword files holding two different wake phrases. A third copy of anything is
/// not worth the paste.
/// <para>
/// Deliberately not a service: it is per-turn state - one cancellation source,
/// one destination - so it is created where a turn is, and a singleton would
/// have to be reset by every caller instead.
/// </para>
/// </remarks>
public sealed class VoiceTurnRouter : IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    /// <summary>Where the turn is going, once it has been asked.</summary>
    public VoiceDestination? Routed { get; private set; }

    /// <summary>Pass this to the turn, so a routed one can be stopped.</summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>
    /// Show a turn report; returns true when this one asked to go somewhere.
    /// </summary>
    /// <remarks>
    /// CHECKED ONCE. Heard is reported again as the turn goes on, and routing
    /// twice would navigate on top of a navigation.
    /// </remarks>
    public bool Observe(TurnState t)
    {
        if (Routed is not null) return false;
        if (t.Heard is not { Length: > 0 } heard) return false;
        if (VoiceDestinations.Match(heard) is not { } where) return false;

        Routed = where;

        // THE TURN STOPS HERE. Otherwise the answering model runs on and speaks
        // a paragraph about translation over the top of the interpreter opening
        // - work nobody asked for, and slower than the thing they did ask for.
        _cts.Cancel();
        return true;
    }

    /// <summary>Whether an exception is just this router having stopped the turn.</summary>
    /// <remarks>
    /// A routed turn cancels itself, and that must not be reported as a failure.
    /// Any OTHER cancellation still is - a turn cut off by something else is a
    /// thing somebody needs to be told about.
    /// </remarks>
    public bool Ended(Exception ex)
        => ex is OperationCanceledException && Routed is not null;

    /// <summary>Say where it is going, then go.</summary>
    /// <param name="nav">The router. Same routes the bar and the links use.</param>
    /// <param name="say">
    /// How this head speaks a line, or null where it cannot. Out loud because the
    /// point of asking by voice is that the phone is across the room or face
    /// down; a screen that silently changes is no answer to somebody who spoke.
    /// </param>
    /// <param name="show">Puts the same line on screen, for whoever is looking.</param>
    /// <remarks>
    /// IT MOVES WHETHER OR NOT IT MANAGES TO SPEAK. A voice that will not play is
    /// a reason to be quiet, never a reason to ignore what was asked.
    /// </remarks>
    public async Task GoAsync(
        NavigationManager nav, Func<string, Task>? say = null, Action<string>? show = null)
    {
        if (Routed is null) return;

        var line = $"Opening {Routed.Title}";
        show?.Invoke(line);

        if (say is not null)
        {
            try { await say(line).ConfigureAwait(true); }
            catch { /* it moves anyway */ }
        }

        nav.NavigateTo(Routed.Route);
    }

    public void Dispose() => _cts.Dispose();
}
