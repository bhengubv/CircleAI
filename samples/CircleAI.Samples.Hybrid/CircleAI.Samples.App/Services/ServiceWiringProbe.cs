// ServiceWiringProbe.cs
//
// What this app depends on is one thing: the link.
//
// DeviceWiringProbe checked the voice stack in this process — was the phonemiser
// installed, had espeak's data unpacked, could a TTS model load. None of that is
// here any more. A thin app has exactly one hook that can be wired or not, and
// reporting four green rows about machinery it no longer owns would be worse than
// reporting none.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Assistant;
using CircleAI.Client;

namespace CircleAI.Samples.App.Services;

/// <inheritdoc />
public sealed class ServiceWiringProbe(LinkedBrain brain) : IWiringProbe
{
    /// <inheritdoc />
    public async Task<WiringReport> HooksAsync(CancellationToken ct = default)
    {
        var rows = new List<WiringRow>();

        var installed = brain.ServiceInstalled;
        rows.Add(new WiringRow(
            "CircleAI installed", "package",
            installed ? WiringStage.Present : WiringStage.Absent,
            installed ? "The service app is on this device."
                      : "Not found. Note that a missing <queries> entry looks exactly like this.",
            Where: "com.bhengubv.circleai.service",
            Who: "CircleAiLinkClient.IsInstalled"));

        var clock = Stopwatch.StartNew();
        var state = await brain.StateAsync(ct).ConfigureAwait(false);
        rows.Add(new WiringRow(
            "Link", "binder",
            state.Ready ? WiringStage.Wired : (installed ? WiringStage.Present : WiringStage.Absent),
            state.Detail,
            Where: "com.bhengubv.circleai.action.LINK",
            Who: "LinkedBrain.StateAsync",
            Took: clock.Elapsed));

        var working = 0;
        foreach (var r in rows) if (r.Working) working++;
        return new WiringReport(rows, working, rows.Count);
    }

    /// <inheritdoc />
    /// <remarks>
    /// EMPTY. The only way to know whether a voice speaks is to make it speak, and
    /// the voices are in the service — walking them from here would mean synthesising
    /// seventy-eight clips across the link to fill a diagnostics screen. The service's
    /// own app is where that sweep belongs.
    /// </remarks>
    public Task<WiringReport> VoicesAsync(IEnumerable<string>? languages = null,
                                          IProgress<WiringRow>? progress = null,
                                          CancellationToken ct = default)
        => Task.FromResult(new WiringReport(Array.Empty<WiringRow>(), 0, 0));
}
