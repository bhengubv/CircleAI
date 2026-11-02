// LinkedDeviceFacts.cs
//
// What this phone can do, and what Circle AI is using on it — asked, not guessed.
//
// THE CLIENT'S OWN ANSWER WAS "NOTHING FOR THIS YET". Truthful, since it holds no
// models, and useless, because the question a person is asking on that screen is
// about their PHONE: can it hear me, can it read this photo, how much of my disk is
// this taking. Every one of those facts is on the other side of the link, and until
// there were verbs for them the client had to answer from whether the link was up -
// one answer shared across four rows.
//
// THE SERVICE KNOWS PER-ABILITY, so it says per-ability. A device that has the ears
// but not the eyes now reads that way, instead of everything going green together.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Assistant;
using CircleAI.Linking;

namespace CircleAI.Client;

/// <summary>The service's own view of this device, over the cross-app link.</summary>
/// <param name="brain">The linked brain, whose connection this rides.</param>
public sealed class LinkedDeviceFacts(LinkedBrain brain)
{
    /// <summary>What this device can actually do, as the service sees it.</summary>
    /// <remarks>
    /// EMPTY WHEN THE LINK IS DOWN, so the caller can fall back to saying why rather
    /// than showing a list of abilities invented on this side.
    /// </remarks>
    public async Task<IReadOnlyList<AbilityRow>> AbilitiesAsync(CancellationToken ct = default)
    {
        var client = await brain.LinkAsync(ct).ConfigureAwait(false);
        if (client is null) return [];

        var reply = await client.AbilitiesAsync(ct).ConfigureAwait(false);
        if (!reply.Ok) return [];

        return [.. reply.Rows.Select(r =>
        {
            var bytes = LinkSetupRows.Text(r, 3);

            // THE ROUTE, WHICH THIS USED TO DROP ON THE FLOOR. It is not decoration:
            // the abilities screen reads it to tell "start the listener" from "fetch
            // the model", so a null route sent every Waking tap down the download
            // path and the row stayed off however many times it was pressed.
            var route = LinkSetupRows.Text(r, 4);

            return new AbilityRow(
                LinkSetupRows.Text(r, 0),
                LinkSetupRows.Text(r, 1),
                Enum.TryParse<AbilityState>(LinkSetupRows.Text(r, 2), out var st) ? st : AbilityState.NotCatalogued,
                string.IsNullOrEmpty(bytes) ? null : LinkSetupRows.Number(r, 3),
                string.IsNullOrEmpty(route) ? null : route);
        })];
    }

    /// <summary>What CircleAI is allowed to do on this device.</summary>
    /// <remarks>
    /// EMPTY WHEN THE LINK IS DOWN, like the abilities above: a list of permissions
    /// invented on this side would be a claim about another app's grants, which is
    /// precisely the thing somebody is reading this screen to check.
    /// </remarks>
    public async Task<IReadOnlyList<PermissionRow>> PermissionsAsync(CancellationToken ct = default)
    {
        var client = await brain.LinkAsync(ct).ConfigureAwait(false);
        if (client is null) return [];

        var reply = await client.PermissionsAsync(ct).ConfigureAwait(false);
        if (!reply.Ok) return [];

        return [.. reply.Rows.Select(r => new PermissionRow(
            LinkSetupRows.Text(r, 0),
            LinkSetupRows.Text(r, 1),
            LinkSetupRows.Flag(r, 2),
            LinkSetupRows.Flag(r, 3)))];
    }

    /// <inheritdoc />
    /// <remarks>
    /// SILENCE ON EVERY FAILURE, and that is the right default here. No link, a
    /// refused reply, no rows - all of them mean "nothing to say", and inventing
    /// a sentence out of a broken call would have the phone announcing a verdict
    /// it never reached.
    /// </remarks>
    public async Task<(int Seq, string? Said)> VerdictAsync(CancellationToken ct = default)
    {
        var client = await brain.LinkAsync(ct).ConfigureAwait(false);
        if (client is null) return (0, null);

        var reply = await client.VerdictAsync(ct).ConfigureAwait(false);
        if (!reply.Ok || reply.Rows.Count == 0) return (0, null);

        var row = reply.Rows[0];
        var said = LinkSetupRows.Text(row, 1);
        return string.IsNullOrWhiteSpace(said)
            ? (0, null)
            : ((int)LinkSetupRows.Number(row, 0), said);
    }

    // ── Step 4: what it can do now, an ability on or off, and the person's settings ───

    /// <summary>
    /// What it can do right now, ability by ability, and the sentence to say. Empty rows and a null
    /// sentence when the link is down or this app has no Settings grant.
    /// </summary>
    public async Task<(IReadOnlyList<CanDoRow> Rows, string? Sentence)> CanDoNowAsync(CancellationToken ct = default)
    {
        var client = await brain.LinkAsync(ct).ConfigureAwait(false);
        if (client is null) return ([], null);

        var reply = await client.CanDoNowAsync(ct).ConfigureAwait(false);
        if (!reply.Ok) return ([], null);

        var rows = reply.Rows.Select(LinkSettingsRows.ReadCanDo).ToList();
        var summary = rows.LastOrDefault(r => r.Title.Length == 0);
        return (rows.Where(r => r.Title.Length > 0).ToList(), summary?.Detail);
    }

    /// <summary>Switch one ability on or off; its live row back, or why not.</summary>
    public async Task<(CanDoRow? Row, string? Why)> SetAbilityAsync(string title, bool on, CancellationToken ct = default)
    {
        var client = await brain.LinkAsync(ct).ConfigureAwait(false);
        if (client is null) return (null, "CircleAI is not linked to this app yet.");

        var reply = await client.SetAbilityAsync(title, on, ct).ConfigureAwait(false);
        return reply.Ok && reply.Rows.Count > 0
            ? (LinkSettingsRows.ReadCanDo(reply.Rows[0]), null)
            : (null, reply.Error ?? "CircleAI could not switch that.");
    }

    /// <summary>The service's settings and the person's preferences; empty when the link is down.</summary>
    public async Task<IReadOnlyList<CircleAI.Core.SettingRow>> SettingsAsync(CancellationToken ct = default)
    {
        var client = await brain.LinkAsync(ct).ConfigureAwait(false);
        if (client is null) return [];

        var reply = await client.SettingsAsync(ct).ConfigureAwait(false);
        return reply.Ok
            ? reply.Rows.Select(LinkSettingsRows.ReadSetting).Where(r => r is not null).Select(r => r!).ToList()
            : [];
    }

    /// <summary>Change one setting; its new row back, or why the value means nothing.</summary>
    public async Task<(CircleAI.Core.SettingRow? Row, string? Why)> SetSettingAsync(string key, string value, CancellationToken ct = default)
    {
        var client = await brain.LinkAsync(ct).ConfigureAwait(false);
        if (client is null) return (null, "CircleAI is not linked to this app yet.");

        var reply = await client.SetSettingAsync(key, value, ct).ConfigureAwait(false);
        return reply.Ok && reply.Rows.Count > 0
            ? (LinkSettingsRows.ReadSetting(reply.Rows[0]), null)
            : (null, reply.Error ?? "CircleAI could not change that.");
    }

    /// <summary>What Circle AI holds on this phone.</summary>
    /// <remarks>
    /// THE LAST TWO ROWS ARE THE TOTALS, not lines — the service appends them so a
    /// screen gets the breakdown and the figure that goes with it in one round trip.
    /// An empty label marks them, which no real line has.
    /// <para>
    /// StorageReport.None when the link is down, which the contract already defines as
    /// the answer for a head that cannot measure its own footprint: the screen shows
    /// nothing rather than a fabricated number.
    /// </para>
    /// </remarks>
    public async Task<StorageReport> StorageAsync(CancellationToken ct = default)
    {
        var client = await brain.LinkAsync(ct).ConfigureAwait(false);
        if (client is null) return StorageReport.None;

        var reply = await client.FootprintAsync(ct).ConfigureAwait(false);
        if (!reply.Ok || reply.Rows.Count < 2) return StorageReport.None;

        var lines = reply.Rows
            .Take(reply.Rows.Count - 2)
            .Select(r => new StorageLine(
                LinkSetupRows.Text(r, 0),
                LinkSetupRows.Text(r, 1),
                LinkSetupRows.Flag(r, 2)))
            .ToList();

        var total    = LinkSetupRows.Text(reply.Rows[^2], 1);
        var freeable = LinkSetupRows.Text(reply.Rows[^1], 1);
        return new StorageReport(lines, total, freeable);
    }
}
