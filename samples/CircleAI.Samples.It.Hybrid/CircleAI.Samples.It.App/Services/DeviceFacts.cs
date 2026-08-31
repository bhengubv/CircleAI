// DeviceFacts.cs
//
// The phone's own answers, read from the same registry and probe
// AbilitiesActivity reads.

using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;

namespace CircleAI.Samples.It.App.Services;

/// <inheritdoc />
public sealed class DeviceFacts : IDeviceFacts
{
    /// <summary>The abilities, in the order the screen shows them.</summary>
    /// <remarks>
    /// COMPILED IN, NOT DISCOVERED. The list is fixed because an ability is code
    /// that runs, not a model that happens to be on disk - a build without the
    /// speech stack must not advertise waking however many wake models it finds.
    /// </remarks>
    private static readonly (string Title, string Blurb, ModelModality Modality)[] Catalogue =
    [
        // "{n}" is filled in at read time by Blurb below, NOT here: a static
        // field initialiser that reaches into another assembly's static makes
        // this whole type fail to initialise if that one is not ready, and a
        // type-initialiser failure takes every member with it. It did exactly
        // that - the abilities list came back empty and the settings screen went
        // blank behind it, with nothing in logcat, because managed logging does
        // not reach it.
        ("Talking",   "Reads things out loud, in {n} languages",      ModelModality.Tts),
        ("Listening", "Understands you when you speak",                ModelModality.Asr),
        ("Answering", "Answers questions and helps you write",         ModelModality.Chat),
        ("Seeing",    "Looks at a photo and tells you what is in it",  ModelModality.Vision),
        // Listed now that this head can actually wake. The rule stands: an
        // ability is code that runs, not a model left on disk - the chat-only
        // build advertised "Waking ✓ On" on a phone that could not wake at all.
        //
        // NO PHRASE NAMED HERE. This said 'Hears you say "Hey B"' whatever the
        // phone was set to, so a phone working in isiZulu told its owner to say
        // an English phrase - the same lie as the old wake-language setting, in a
        // different place. The phrase has exactly one home now, Settings ›
        // Language › Waking, and it is the only screen that may name it.
        ("Waking",    "Hears you without being touched",               ModelModality.WakeWord),
    ];

    /// <summary>The screen that demonstrates an ability, where one exists.</summary>
    /// <remarks>
    /// An ability that is ON should be somewhere you can GO rather than just a
    /// tick - but a row that looks tappable and does nothing is worse than a plain
    /// one, so this is null for everything without a screen.
    /// </remarks>
    private static string? RouteFor(ModelModality modality)
        => modality == ModelModality.WakeWord ? "wake" : null;

    private static string StorageDir => ModelStore.Path;

    /// <summary>The blurb, with the language count filled in.</summary>
    /// <remarks>
    /// COUNTED, NOT HEDGED - "10 plus" disagreed with the abilities pitch and the
    /// language list, which both say 75 - but counted HERE rather than in the
    /// table above, where it would run during type initialisation.
    /// </remarks>
    private static string Blurb(string template)
    {
        if (!template.Contains("{n}", StringComparison.Ordinal)) return template;

        // THE VOICE CATALOGUE, like Home and the language list. Counting
        // SampleLanguages here would put a third number on a third screen for
        // one fact - the table holds 75 names, this phone offers 78 voices.
        var languages = 0;
        try
        {
            using var registry = new ModelRegistryService();
            var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in registry.AllModels.Where(m => m.Modality == ModelModality.Tts))
                foreach (var raw in (m.Language ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                    if (raw.Trim().Length > 0) tags.Add(raw.Trim());
            languages = tags.Count;
        }
        catch { /* a blurb is not worth failing the screen for */ }

        return languages > 0
            ? template.Replace("{n}", languages.ToString())
            : "Reads things out loud, in your language";
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AbilityRow>> AbilitiesAsync(CancellationToken ct = default)
        => Task.Run<IReadOnlyList<AbilityRow>>(() =>
        {
            using var registry = new ModelRegistryService();
            using var loader = new BundleModelLoader(StorageDir, registry);
            var probe = DeviceProbe.Snapshot();

            var rows = new List<AbilityRow>(Catalogue.Length);
            foreach (var (title, blurb, modality) in Catalogue)
            {
                // THE SAME CHOICE THE CHAT SCREEN MAKES. The rule used to live
                // here in full and in a different form in DeviceBrain, so this
                // screen offered Answering at 547 MB while the chat screen said
                // it needed 22797 MB. One rule, one answer.
                var chosen = ModelChoice.For(modality, registry, loader, probe);

                // Size, not SHA-256 - see ModelChoice.For. This runs once per
                // modality to draw the "Turned on" list, so the hash was paid for
                // every installed model each time Settings opened.
                if (chosen is not null && loader.ModelPresent(chosen.Name))
                {
                    rows.Add(new AbilityRow(title, Blurb(blurb), AbilityState.On,
                        TryRoute: RouteFor(modality)));
                    continue;
                }

                rows.Add(chosen is not null
                    ? new AbilityRow(title, Blurb(blurb), AbilityState.Available, chosen.TotalBytes)
                    : new AbilityRow(title, Blurb(blurb),
                        ModelChoice.AnyCatalogued(modality, registry)
                            ? AbilityState.TooBig
                            : AbilityState.NotCatalogued));
            }
            return rows;
        }, ct);

    /// <inheritdoc />
    public Task<PhoneFacts> PhoneAsync(CancellationToken ct = default)
        => Task.Run(() =>
        {
            using var registry = new ModelRegistryService();
            using var loader = new BundleModelLoader(StorageDir, registry);
            var probe = DeviceProbe.Snapshot();

            var facts = new List<PhoneFact>
            {
                new("Space free", $"{probe.StorageFreeGb:0.#} GB"),
                // MeasurementWarning is not decoration. A phone that cannot report
                // its own memory must say so rather than print a confident zero.
                new("Memory", probe.MeasurementWarning is null
                    ? $"{probe.RamAvailableBytes / 1_000_000_000.0:0.#} GB free of "
                      + $"{probe.RamTotalBytes / 1_000_000_000.0:0.#} GB"
                    : "Can't be read on this phone"),
                new("Where it runs", "On this phone. Nothing is sent anywhere."),
            };

            var technical = new List<string>();
            foreach (var (title, _, modality) in Catalogue)
            {
                // THE FOURTH PLACE, and a readout that named a different model
                // from the one the row above it offered is a diagnostics screen
                // actively misleading whoever came to it to diagnose something.
                var m = ModelChoice.For(modality, registry, loader, probe);
                if (m is null) continue;
                technical.Add($"{title}: {m.Name}\n{Size(m.TotalBytes)} · needs "
                            + $"{m.MinRamGb:0.#} GB · {m.Repo}");
            }

            return new PhoneFacts(facts, technical);
        }, ct);

    /// <inheritdoc />
    public async Task<string> TurnOnAsync(
        string title, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var entry = Catalogue.FirstOrDefault(a => a.Title == title);
        if (entry.Title is null) return $"No ability called '{title}'.";

        using var registry = new ModelRegistryService();
        using var loader = new BundleModelLoader(StorageDir, registry);
        var probe = DeviceProbe.Snapshot();

        // THE THIRD PLACE THIS CHOICE WAS MADE, and the one that actually spends
        // somebody's data. A download that picked a different model from the one
        // the row advertised would be the worst of the three to get wrong.
        var best = ModelChoice.For(entry.Modality, registry, loader, probe);
        if (best is null) return "Nothing that fits this phone.";

        try
        {
            progress?.Report($"Getting {best.Name}…");
            await loader.DownloadModelAsync(best.Name,
                new Progress<float>(f => progress?.Report($"{f * 100:0}%")))
                .ConfigureAwait(false);
            return "On";
        }
        catch (Exception ex)
        {
            // The reason, not a generic failure: "no space" and "no signal" call
            // for completely different things from the person reading it.
            return ex switch
            {
                HttpRequestException => "Could not reach the internet.",
                IOException => "Not enough space.",
                _ => $"{ex.GetType().Name}: {ex.Message}",
            };
        }
    }

    // Fits and Size moved to ModelChoice, which is now the one place that
    // decides which model this phone should use for a job. Four copies of that
    // decision lived here and in DeviceBrain, and two of them disagreed by a
    // factor of forty on the same handset.
    private static string Size(long bytes) => ModelChoice.Size(bytes);
}
