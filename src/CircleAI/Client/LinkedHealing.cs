// LinkedHealing.cs
//
// WOLVERINE, READ FROM AN APP, OVER THE LINK (S6).
//
// The healing loop runs in the service, which has no screens; an app that wants to show what it
// fixed has had nothing to read - ServiceHealing in the sample says "self-healing runs in
// CircleAI" for exactly that reason, and names the missing piece as a wire change. This is the
// app's end of that wire: IHealingView over the Healing and HealingSet verbs, so the Wolverine
// screen can be pointed at the service instead of at a placeholder.
//
// EMPTY ON EVERY FAILURE, NEVER INVENTED. No link, a refusal, a service too old to know the
// verb: the lists are empty and the summary says nothing was seen, rather than a status this
// side made up. A setter that the service refused throws, with the service's reason - a person
// who switched the reports off must not be told it worked when it did not.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Assistant;
using CircleAI.Linking;

namespace CircleAI.Client;

/// <summary>The service's self-healing, over the cross-app link.</summary>
/// <param name="brain">The linked brain, whose connection this rides.</param>
public sealed class LinkedHealing(LinkedBrain brain) : IHealingView
{
    /// <summary>What the service said about its healing, row by row, in one read.</summary>
    /// <param name="Summary">The summary row; null when the link is down or the service does not know the verb.</param>
    /// <param name="Switches">Every switch: [name, light, trips, why, backOnUtc].</param>
    /// <param name="Knobs">Every knob in force: [name, value, untilUtc].</param>
    /// <param name="Items">The newest records.</param>
    public sealed record Snapshot(
        IReadOnlyList<string>? Summary,
        IReadOnlyList<IReadOnlyList<string>> Switches,
        IReadOnlyList<IReadOnlyList<string>> Knobs,
        IReadOnlyList<HealingItem> Items);

    /// <inheritdoc />
    /// <remarks>Never raised: a binder cannot push. Poll <see cref="HealthAsync"/>.</remarks>
    public event Action? Changed { add { } remove { } }

    /// <summary>Everything the service says about its healing, with up to <paramref name="limit"/> records.</summary>
    public async Task<Snapshot> ReadAsync(int limit = 20, CancellationToken ct = default)
    {
        var client = await brain.LinkAsync(ct).ConfigureAwait(false);
        if (client is null) return new Snapshot(null, [], [], []);

        var reply = await client.HealingAsync(limit, ct).ConfigureAwait(false);
        if (!reply.Ok) return new Snapshot(null, [], [], []);

        IReadOnlyList<string>? summary = null;
        var switches = new List<IReadOnlyList<string>>();
        var knobs = new List<IReadOnlyList<string>>();
        var items = new List<HealingItem>();
        foreach (var row in reply.Rows)
        {
            switch (LinkHealingRows.KindOf(row))
            {
                case LinkHealingRows.Summary: summary ??= row; break;
                case LinkHealingRows.Switch: switches.Add(row.Skip(1).ToList()); break;
                case LinkHealingRows.Knob: knobs.Add(row.Skip(1).ToList()); break;
                case LinkHealingRows.Item: items.Add(Item(row)); break;
            }
        }
        return new Snapshot(summary, switches, knobs, items);
    }

    /// <inheritdoc />
    public async Task<HealthSummary> HealthAsync(CancellationToken ct = default)
    {
        var s = (await ReadAsync(1, ct).ConfigureAwait(false)).Summary;
        if (s is null) return new HealthSummary(0, 0, 0, null, AutonomyLevel.Off);
        return new HealthSummary(
            (int)LinkSetupRows.Number(s, 6),
            (int)LinkSetupRows.Number(s, 7),
            (int)LinkSetupRows.Number(s, 8),
            LinkHealingRows.TimeAt(s, 9),
            AutonomyLevels.Parse(LinkSetupRows.Text(s, 1)));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<HealingItem>> HealedAsync(CancellationToken ct = default)
        => (await ReadAsync(50, ct).ConfigureAwait(false)).Items.Where(i => i.Outcome == "Healed").ToList();

    /// <inheritdoc />
    public async Task<IReadOnlyList<HealingItem>> NeedsYouAsync(CancellationToken ct = default)
        => (await ReadAsync(50, ct).ConfigureAwait(false)).Items.Where(i => i.NeedsHuman).ToList();

    /// <inheritdoc />
    public async Task<AutonomyLevel> AutonomyAsync(CancellationToken ct = default)
    {
        var s = (await ReadAsync(1, ct).ConfigureAwait(false)).Summary;
        return s is null ? AutonomyLevel.Off : AutonomyLevels.Parse(LinkSetupRows.Text(s, 1));
    }

    /// <inheritdoc />
    public Task SetAutonomyAsync(AutonomyLevel level, CancellationToken ct = default)
        => SetAsync(LinkHealingRows.ArgAutonomy, AutonomyLevels.ToWire(level), ct);

    /// <inheritdoc />
    public Task MarkHandledAsync(string id, CancellationToken ct = default)
        => SetAsync(LinkHealingRows.ArgHandled, id ?? throw new ArgumentNullException(nameof(id)), ct);

    /// <inheritdoc />
    public Task RunSelfCheckAsync(CancellationToken ct = default)
        => SetAsync(LinkHealingRows.ArgSelfCheck, "1", ct);

    /// <summary>Whether the service sends WolverineAPI reports; false when it cannot be read.</summary>
    public async Task<bool> ReportsAsync(CancellationToken ct = default)
    {
        var s = (await ReadAsync(1, ct).ConfigureAwait(false)).Summary;
        return s is not null && LinkSetupRows.Flag(s, 2);
    }

    /// <summary>Switches the service's WolverineAPI reports on or off; off deletes what was waiting.</summary>
    public Task SetReportsAsync(bool on, CancellationToken ct = default)
        => SetAsync(LinkHealingRows.ArgReports, on ? "on" : "off", ct);

    private async Task SetAsync(string key, string value, CancellationToken ct)
    {
        var client = await brain.LinkAsync(ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Circle AI is not reachable.");
        var reply = await client.HealingSetAsync(new Dictionary<string, string> { [key] = value }, ct).ConfigureAwait(false);
        if (!reply.Ok) throw new InvalidOperationException(reply.Error ?? "Circle AI refused the change.");
    }

    // [item, id, atUtc, area, fingerprint, remedy, actionTaken, outcome, needsHuman, handled, recommended]
    private static HealingItem Item(IReadOnlyList<string> row)
    {
        var area = LinkSetupRows.Text(row, 3);
        var fingerprint = LinkSetupRows.Text(row, 4);
        var recommended = LinkSetupRows.Text(row, 10);
        return new HealingItem(
            LinkSetupRows.Text(row, 1),
            LinkHealingRows.TimeAt(row, 2) ?? DateTimeOffset.MinValue,
            string.IsNullOrEmpty(area) ? fingerprint : $"{area}: {fingerprint}",
            LinkSetupRows.Text(row, 6),
            string.IsNullOrEmpty(recommended) ? null : recommended,
            Pretty(LinkSetupRows.Text(row, 7)),
            LinkSetupRows.Flag(row, 8) && !LinkSetupRows.Flag(row, 9));
    }

    // The same words DeviceHealing shows, so the screen reads one way whichever end it reads.
    private static string Pretty(string outcome) => outcome switch
    {
        "Healed" => "Healed",
        "Escalated" => "Needs you",
        "Failed" => "Fix failed",
        "AwaitingBrain" => "Awaiting brain",
        _ => "Recorded",
    };
}
