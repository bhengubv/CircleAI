// LinkedSetup.cs
//
// Setting CircleAI up, from an app a person can actually open.
//
// CIRCLEAI HAS NO SCREENS, and that is the whole point of it - it is a service, it
// is not in the launcher, and nobody opens it. But somebody still has to decide what
// it should be able to do: whether it can see as well as hear, which voice it
// speaks in, and whether the gigabytes that make those true are worth the space on
// this phone. That decision belongs to a person, and a person is always in some
// other app.
//
// SO THE SETUP SCREEN LIVES IN THE CLIENT AND THE WORK HAPPENS IN THE SERVICE. This
// is the road between them. The client's ISetup stops being a set of polite refusals
// and becomes a view onto the real one.
//
// STARTING IS NOT WAITING, AND THE WIRE FORCES THAT HONESTY. A binder transaction is
// request and response on a single thread; a model download is minutes over a phone
// network. So SetupStart returns the moment the work begins and SetupProgress is
// polled - the same shape as LinkedBrain, which does not pretend to stream either.
// Holding the binder thread for a download would wedge every other app on the device
// that talks to CircleAI, because the budget is per process.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Assistant;
using CircleAI.Linking;

namespace CircleAI.Client;

/// <summary>The service's own setup, reached over the cross-app link.</summary>
/// <param name="brain">The linked brain, whose connection this rides.</param>
public sealed class LinkedSetup(LinkedBrain brain)
{
    /// <summary>How often the run is asked where it has got to.</summary>
    /// <remarks>
    /// A second is slow enough that polling costs nothing against a download running
    /// for minutes, and fast enough that a progress bar does not look stuck. Each
    /// poll is one small transaction.
    /// </remarks>
    private static readonly TimeSpan Beat = TimeSpan.FromSeconds(1);

    /// <summary>Whether CircleAI can hold a conversation yet, and what it waits on.</summary>
    /// <remarks>
    /// NeedsSetup IS THE HONEST ANSWER WHEN THE LINK CANNOT ANSWER. A client that
    /// cannot reach the service is a client whose person has something to do about
    /// it, which is exactly what that stage means to the screen.
    /// </remarks>
    public async Task<Readiness> ReadinessAsync(CancellationToken ct = default)
    {
        var client = await brain.LinkAsync(ct).ConfigureAwait(false);
        if (client is null) return NotLinked;

        var reply = await client.SetupReadinessAsync(ct).ConfigureAwait(false);
        if (!reply.Ok || reply.Rows.Count == 0)
            return new Readiness(ReadyStage.NeedsSetup, "CircleAI is not ready",
                                 reply.Error ?? "It could not say what it is waiting for.", false);

        var row = reply.Rows[0];
        var stage = Enum.TryParse<ReadyStage>(LinkSetupRows.Text(row, 0), out var parsed)
            ? parsed
            : ReadyStage.NeedsSetup;

        return new Readiness(
            stage,
            LinkSetupRows.Text(row, 1, "CircleAI"),
            LinkSetupRows.Text(row, 2),
            LinkSetupRows.Flag(row, 3));
    }

    /// <summary>What still has to be fetched for full use, and what each piece costs.</summary>
    /// <remarks>
    /// EMPTY ON FAILURE, NOT AN INVENTED PLAN. A screen showing downloads that the
    /// service never listed would have somebody agree to fetch something nothing
    /// asked for.
    /// </remarks>
    public async Task<IReadOnlyList<SetupItem>> PlanAsync(CancellationToken ct = default)
    {
        var client = await brain.LinkAsync(ct).ConfigureAwait(false);
        if (client is null) return [];

        var reply = await client.SetupPlanAsync(ct).ConfigureAwait(false);
        if (!reply.Ok) return [];

        return [.. reply.Rows.Select(r => new SetupItem(
            LinkSetupRows.Text(r, 0),
            LinkSetupRows.Number(r, 1)))];
    }

    /// <summary>What CircleAI already holds.</summary>
    public async Task<Census> CensusAsync(CancellationToken ct = default)
    {
        var client = await brain.LinkAsync(ct).ConfigureAwait(false);
        if (client is null)
            return new Census([], 0, 0, "CircleAI is not linked to this app yet.");

        var reply = await client.SetupCensusAsync(ct).ConfigureAwait(false);
        if (!reply.Ok)
            return new Census([], 0, 0, reply.Error ?? "CircleAI could not say what it holds.");

        var rows = reply.Rows.Select(r => new CensusRow(
            LinkSetupRows.Text(r, 0),
            LinkSetupRows.Flag(r, 1),
            LinkSetupRows.Number(r, 2),
            LinkSetupRows.Text(r, 3),
            // THE FIFTH FIELD, AND READING IT IS HALF THE FIX. The service has always
            // been able to measure a half-finished download; until 2026-10-04 the
            // wire had four columns and the measurement died at this seam.
            LinkSetupRows.Number(r, 4))).ToList();

        var present = rows.Count(r => r.Present);
        return new Census(rows, present, rows.Count,
            rows.Count == 0 ? "CircleAI holds nothing yet."
                            : $"{present} of {rows.Count} ready.");
    }

    /// <summary>Fetch what the plan lists, reporting progress until it finishes.</summary>
    /// <remarks>
    /// START, THEN POLL. The run belongs to the service and outlives this call by
    /// design: somebody who closes the app mid-download and reopens it rejoins the
    /// same run rather than starting a second copy of the same gigabytes.
    /// <para>
    /// CANCELLING STOPS WATCHING, NOT DOWNLOADING, and the difference is worth being
    /// plain about. The token here belongs to a screen; the download belongs to
    /// another app's process and carries on. Reporting otherwise would be a lie a
    /// person acts on.
    /// </para>
    /// </remarks>
    public async Task RunAsync(IProgress<SetupProgressReport>? progress,
                               CancellationToken ct = default)
    {
        var client = await brain.LinkAsync(ct).ConfigureAwait(false);
        if (client is null)
            throw new InvalidOperationException(
                "CircleAI is not linked to this app yet. Approve the link and try again.");

        var started = await client.SetupStartAsync(ct).ConfigureAwait(false);
        if (!started.Ok)
            throw new InvalidOperationException(started.Error ?? "CircleAI could not start.");

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(Beat, ct).ConfigureAwait(false);

            var at = await client.SetupProgressAsync(ct).ConfigureAwait(false);
            if (!at.Ok)
                throw new InvalidOperationException(at.Error ?? "CircleAI stopped setting up.");
            if (at.Rows.Count == 0) continue;

            var row = at.Rows[0];
            var phase = Enum.TryParse<SetupPhase>(LinkSetupRows.Text(row, 5), out var p)
                ? p
                : SetupPhase.Fetching;

            progress?.Report(new SetupProgressReport(
                (int)LinkSetupRows.Number(row, 0),
                (int)LinkSetupRows.Number(row, 1),
                LinkSetupRows.Text(row, 2),
                LinkSetupRows.Fraction(row, 3),
                TimeSpan.FromSeconds(LinkSetupRows.Fraction(row, 4)),
                phase));

            if (phase == SetupPhase.Done) return;
        }
    }

    private static Readiness NotLinked => new(
        ReadyStage.NeedsSetup,
        "CircleAI is not linked yet",
        "Approve the link in Settings, then it can be set up from here.",
        false);
}
