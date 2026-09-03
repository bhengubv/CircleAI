// BrowserWiringProbe.cs
//
// A browser has none of the wires a phone has, and says so rather than reporting
// an empty list.
//
// AN EMPTY REPORT WOULD READ AS "NOTHING IS WRONG". The whole point of this
// interface is that a capability nobody checked looks identical to one that
// works, so the web head answers the same questions the phone does and gives the
// platform reason for each - which is a fact about the browser, not a fault.

namespace CircleAI.Samples.It.Web.Client.Services;

/// <inheritdoc />
public sealed class BrowserWiringProbe : IWiringProbe
{
    private readonly IVoiceHost _voice;

    public BrowserWiringProbe(IVoiceHost voice) => _voice = voice;

    private static WiringRow No(string title, string kind, string why)
        => new(title, kind, WiringStage.Absent, why);

    /// <inheritdoc />
    public Task<WiringReport> HooksAsync(CancellationToken ct = default)
    {
        var rows = new List<WiringRow>
        {
            No("Phonemizer (text to sounds)", "hook",
               "the browser speaks through the platform's own voices, so no G2P is wired here"),
            No("espeak dictionaries", "engine",
               "no native library in a tab"),
            No("Japanese phonemizer (Open JTalk)", "engine",
               "no native library in a tab"),
            No("Real RAM reading", "hook",
               "a tab is not told how much memory the machine has"),
            No("Wake word bundle", "engine",
               "a tab cannot hold the microphone once it is closed"),
        };

        return Task.FromResult(new WiringReport(rows, 0, rows.Count));
    }

    /// <inheritdoc />
    /// <remarks>
    /// The catalogue is the host's own - a browser reports the voices it actually
    /// has - so every row is Wired: what the platform offers, it can speak.
    /// </remarks>
    public async Task<WiringReport> VoicesAsync(
        IEnumerable<string>? languages = null,
        IProgress<WiringRow>? progress = null,
        CancellationToken ct = default)
    {
        var catalogue = await _voice.CatalogueAsync(ct).ConfigureAwait(false);
        var wanted = languages?.ToList();

        var rows = catalogue
            .Where(r => wanted is not { Count: > 0 } || wanted.Contains(r.Tag))
            .Select(r => new WiringRow(
                $"{SampleLanguages.Find(r.Tag)?.Name ?? r.Tag} voice",
                "voice",
                WiringStage.Wired,
                "offered by the browser's own speech engine"))
            .ToList();

        foreach (var r in rows) progress?.Report(r);

        return new WiringReport(rows, rows.Count, catalogue.Count);
    }
}
