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
        ("Talking",   "Reads things out loud, in 10 plus languages",   ModelModality.Tts),
        ("Listening", "Understands you when you speak",                ModelModality.Asr),
        ("Answering", "Answers questions and helps you write",         ModelModality.Chat),
        ("Seeing",    "Looks at a photo and tells you what is in it",  ModelModality.Vision),
        // Listed now that this head can actually wake. The rule stands: an
        // ability is code that runs, not a model left on disk - the chat-only
        // build advertised "Waking ✓ On" on a phone that could not wake at all.
        ("Waking",    "Hears you say \"Hey B\" without being touched",  ModelModality.WakeWord),
    ];

    /// <summary>The screen that demonstrates an ability, where one exists.</summary>
    /// <remarks>
    /// An ability that is ON should be somewhere you can GO rather than just a
    /// tick - but a row that looks tappable and does nothing is worse than a plain
    /// one, so this is null for everything without a screen.
    /// </remarks>
    private static string? RouteFor(ModelModality modality)
        => modality == ModelModality.WakeWord ? "wake" : null;

    private static string StorageDir
        => Path.Combine(FileSystem.AppDataDirectory, "CircleAI", "Models");

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
                var candidates = registry.AllModels.Where(m => m.Modality == modality).ToList();
                var installed = candidates.FirstOrDefault(m => loader.ModelExists(m.Name));

                if (installed is not null)
                {
                    rows.Add(new AbilityRow(title, blurb, AbilityState.On,
                        TryRoute: RouteFor(modality)));
                    continue;
                }

                var best = candidates.Where(m => Fits(m, probe))
                                     .OrderByDescending(m => m.QualityRank)
                                     .ThenBy(m => m.MinRamGb)
                                     .FirstOrDefault();

                rows.Add(best is not null
                    ? new AbilityRow(title, blurb, AbilityState.Available, best.TotalBytes)
                    : new AbilityRow(title, blurb,
                        candidates.Count == 0 ? AbilityState.NotCatalogued : AbilityState.TooBig));
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
                var m = registry.AllModels
                    .Where(x => x.Modality == modality)
                    .OrderByDescending(x => loader.ModelExists(x.Name))
                    .ThenByDescending(x => x.QualityRank)
                    .FirstOrDefault();
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

        var best = registry.AllModels
            .Where(m => m.Modality == entry.Modality && Fits(m, probe))
            .OrderByDescending(m => m.QualityRank)
            .ThenBy(m => m.MinRamGb)
            .FirstOrDefault();
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

    private static bool Fits(ModelEntry m, DeviceProbe probe)
        => m.MinRamGb <= probe.UsableRamGb + 0.0001
        && (probe.StorageFreeGb <= 0 || m.MinStorageGb <= probe.StorageFreeGb + 0.0001);

    private static string Size(long bytes)
        => bytes >= 1_000_000_000
            ? $"{bytes / 1_000_000_000.0:0.#} GB"
            : $"{bytes / 1_000_000.0:0} MB";
}
