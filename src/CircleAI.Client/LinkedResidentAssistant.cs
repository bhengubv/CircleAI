// LinkedResidentAssistant.cs
//
// The "answer to its name" switch, made to actually switch something.
//
// IT USED TO SAY "OPEN CIRCLEAI TO TURN IT ON OR OFF" - about an app with no
// launcher icon. That was the honest half of the answer (the microphone and the
// foreground service really are over there) attached to an action nobody can take.
// A toggle whose only instruction is unreachable is a toggle that does not exist.
//
// THE STATE IS ASKED, NOT REMEMBERED. Whether the phone is listening is the
// SERVICE's fact, and it can change without this app: the OS kills the process, the
// person revokes the microphone, a battery optimiser steps in. So every call asks,
// and nothing here is cached - a stale "Listening" on a screen is worse than a slow
// one.

using System;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Assistant;
using CircleAI.Linking;

namespace CircleAI.Client;

/// <inheritdoc />
/// <param name="brain">The linked brain, whose connection this rides.</param>
public sealed class LinkedResidentAssistant(LinkedBrain brain) : IResidentAssistant
{
    /// <inheritdoc />
    /// <remarks>
    /// THE LAST ANSWER THE SERVICE GAVE, not a guess and not a constant. This is
    /// synchronous and the truth is across a link, so it cannot ask — it was
    /// hardcoded false for exactly that reason, which meant the settings checkbox
    /// could never tick no matter what the service said. Settings reads this to
    /// decide whether the switch is on.
    /// <para>
    /// It is only ever written by a call that DID ask, so before the first call it
    /// reports false — not listening — which is the safe direction: a dark switch
    /// over a listening phone would be a lie about the microphone, and the screen
    /// refreshes within a second of opening.
    /// </para>
    /// </remarks>
    public bool IsListening => _listening;

    private volatile bool _listening;

    /// <inheritdoc />
    /// <remarks>
    /// Declared to satisfy the interface, never raised: the wake word fires in the
    /// service's process and there is no verb that pushes an event back.
    /// </remarks>
    public event EventHandler<string>? Woke { add { } remove { } }

    /// <inheritdoc />
    public Task<ResidentStatus> StartAsync(CancellationToken ct = default)
        => AskAsync(c => c.ResidentStartAsync(ct), ct);

    /// <inheritdoc />
    public Task<ResidentStatus> StopAsync(CancellationToken ct = default)
        => AskAsync(c => c.ResidentStopAsync(ct), ct);

    /// <inheritdoc />
    /// <remarks>Resuming is starting: the service decides whether that is a no-op.</remarks>
    public Task<ResidentStatus> ResumeAsync(CancellationToken ct = default)
        => AskAsync(c => c.ResidentStartAsync(ct), ct);

    /// <inheritdoc />
    public Task<ResidentStatus> RefreshAsync(CancellationToken ct = default)
        => AskAsync(c => c.ResidentStatusAsync(ct), ct);

    private async Task<ResidentStatus> AskAsync(
        Func<CircleAiLinkClient, Task<LinkRowsReply>> call, CancellationToken ct)
    {
        if (!brain.ServiceInstalled)
            return new ResidentStatus(ResidentState.NotInstalled,
                "CircleAI is not installed",
                "This app asks CircleAI to listen and to think. Install it to use them.");

        var client = await brain.LinkAsync(ct).ConfigureAwait(false);
        if (client is null)
            return new ResidentStatus(ResidentState.NeedsPermission,
                "Not linked yet",
                "Approve CircleAI in Settings, then it can listen for you.");

        var reply = await call(client).ConfigureAwait(false);
        if (!reply.Ok || reply.Rows.Count == 0)
            return new ResidentStatus(ResidentState.Failed,
                "CircleAI could not say",
                reply.Error ?? "It did not answer.");

        var row = reply.Rows[0];
        var state = Enum.TryParse<ResidentState>(LinkSetupRows.Text(row, 0), out var st)
            ? st
            : ResidentState.Failed;

        _listening = state == ResidentState.Listening;

        return new ResidentStatus(state, LinkSetupRows.Text(row, 1), LinkSetupRows.Text(row, 2));
    }
}
