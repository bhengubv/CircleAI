// DeviceFacts.cs
//
// What this phone can do, answered once for every head.
//
// IT LIVED IN THE HYBRID APP'S Services FOLDER AND ONLY THE HYBRID COULD USE IT.
// The native head asked the same question and answered it itself, in
// AbilitiesActivity.Row and in three more places in HomeActivity, and the two
// answers differed in ways a person could see on one phone:
//
//   THE STATE WAS WRONG. Android printed "✓ On" whenever a model was on disk.
//   AbilityState.Ready exists precisely because that is a lie - Settings said
//   "Waking ✓ On" the moment the bundle finished downloading, while nothing was
//   listening. That was fixed here and never reached the native head.
//
//   IT COST NINE SECONDS A ROW. Android called loader.ModelExists, which proves
//   every byte by hashing the anchor file - 470 MB for chat, 9.4 s on the P30,
//   MEASURED - once per candidate, on a screen that only wanted to know whether
//   the model was there. ModelChoice.For was written to stop paying that and
//   says so in its own header.
//
//   IT LOOKED IN A DIFFERENT DIRECTORY. The native head built its model path
//   from SpecialFolder.ApplicationData; ModelStore and ModelPaths both use the
//   files directory itself. On Android the first is ".config" UNDERNEATH the
//   second - see ModelPaths, which found that by looking at a phone's disk after
//   a 523 MB model was downloaded twice - so the screen was reporting on a
//   directory the session does not read.
//
// One implementation, two heads. What a head still supplies is which screens it
// actually has, because a row that looks tappable and does nothing is worse than
// a plain one, and the two heads genuinely differ there.

using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;

namespace CircleAI.Assistant.Device;

/// <inheritdoc />
public sealed class DeviceFacts : IDeviceFacts
{
    /// <summary>Whether the resident listener is actually holding the microphone.</summary>
    /// <remarks>
    /// Read from the service rather than taken as a constructor dependency: this
    /// is the same static IResidentAssistant.IsListening reports, and routing it
    /// through DI would give this screen a second path to one fact.
    /// </remarks>
    private static bool Listening
    {
        get
        {
            try { return CircleAI.Device.CircleNeuronService.IsListening; }
            catch { return false; }
        }
    }

    /// <summary>One thing the phone can do, in the words a person would use.</summary>
    /// <param name="Title">What it is. A verb, not a noun - "Talking", not "TTS".</param>
    /// <param name="Blurb">What it means for you, in one sentence.</param>
    /// <param name="Modality">Which models serve it.</param>
    /// <param name="Route">The screen that demonstrates it, by name.</param>
    /// <param name="NeedsNoModel">
    /// True for an ability that is arithmetic rather than a download.
    /// <para>
    /// WITHOUT THIS THE ROW IS UNREACHABLE. A row's state is driven by whether a
    /// MODEL is present, which is exactly right for Seeing and Listening and
    /// exactly wrong for Music, where the generator is managed code and there is
    /// nothing to install. The row rendered as unavailable, with no size, no
    /// button and no way in.
    /// </para>
    /// </param>
    private sealed record Ability(
        string Title,
        string Blurb,
        ModelModality Modality,
        string? Route = null,
        bool NeedsNoModel = false);

    /// <summary>The abilities, in the order the screen shows them.</summary>
    /// <remarks>
    /// COMPILED IN, NOT DISCOVERED. The list is fixed because an ability is code
    /// that runs, not a model that happens to be on disk - a build without the
    /// speech stack must not advertise waking however many wake models it finds.
    /// <para>
    /// MERGED FROM TWO LISTS. This head had five entries and the native head had
    /// eight, so one phone's Settings screen and its own abilities screen listed
    /// different products. Music, Translating and Finding were only ever on the
    /// native side; they are abilities of the assistant, not of a head.
    /// </para>
    /// </remarks>
    private static readonly Ability[] Catalogue =
    [
        // "{n}" is filled in at read time by Blurb below, NOT here: a static
        // field initialiser that reaches into another assembly's static makes
        // this whole type fail to initialise if that one is not ready, and a
        // type-initialiser failure takes every member with it. It did exactly
        // that - the abilities list came back empty and the settings screen went
        // blank behind it, with nothing in logcat, because managed logging does
        // not reach it.
        new("Talking",   "Reads things out loud, in {n}",               ModelModality.Tts),

        // LISTENING WAS A DOWNLOAD TO NOWHERE. Whisper has been catalogued and
        // fetchable for as long as this row has existed, and there was nothing
        // behind it.
        new("Listening", "Understands you when you speak",              ModelModality.Asr,
            Route: "transcribe"),

        new("Answering", "Answers questions and helps you write",       ModelModality.Chat),

        // SEEING WAS THE OTHER ONE. Two models catalogued with their hashes, the
        // bridge able to encode an image, RunImageTurnAsync complete - and the
        // list would cheerfully fetch 311 MB for an ability with no screen
        // behind it. The tick then said On for something a person could not do.
        new("Seeing",    "Looks at a photo and tells you what is in it", ModelModality.Vision,
            Route: "seeing"),

        // THE ONE THAT NEEDS NOTHING. Every other row waits on a download; this
        // is arithmetic, so it works on a phone with no network and nothing
        // installed - which is also why the row has no size beside it.
        new("Music",     "Makes a piece of music, with nothing installed", ModelModality.Music,
            Route: "music", NeedsNoModel: true),

        // TRANSLATION RIDES THE CHAT MODEL, so it is available exactly when
        // Answering is - there is no separate translation model to download.
        //
        // AND THIS IS WHY A ROW IS KEYED ON ITS TITLE, NOT ITS MODALITY. Two
        // abilities share ModelModality.Chat, so a modality-keyed route would
        // send "Answering" to the translate screen as well.
        new("Translating", "Carries what you say into another language", ModelModality.Chat,
            Route: "translate"),

        // SEARCH NEEDS NOTHING EITHER. Lexical ranking is arithmetic, and memory
        // searches itself - so this works on a phone with no model downloaded at
        // all, like Music. The semantic half waits on an embedding model nobody
        // has catalogued.
        new("Finding",   "Looks through what you said and what it wrote down", ModelModality.Chat,
            Route: "find", NeedsNoModel: true),

        // Listed now that both heads can actually wake. The rule stands: an
        // ability is code that runs, not a model left on disk - the chat-only
        // build advertised "Waking ✓ On" on a phone that could not wake at all.
        //
        // NO PHRASE NAMED HERE. This said 'Hears you say "Hey B"' whatever the
        // phone was set to, so a phone working in isiZulu told its owner to say
        // an English phrase - the same lie as the old wake-language setting, in a
        // different place. The phrase has exactly one home now, Settings >
        // Language > Waking, and it is the only screen that may name it.
        new("Waking",    "Hears you without being touched",             ModelModality.WakeWord,
            Route: "wake"),
    ];

    /// <summary>The abilities a head has no screen for.</summary>
    /// <remarks>
    /// A ROUTE IS ONLY OFFERED BY A HEAD THAT HAS THE SCREEN. AbilityRow.TryRoute
    /// says it: "a row that looks tappable and does nothing is worse than a plain
    /// one, so this is null unless a screen really exists." The two heads
    /// genuinely differ - the native head has Seeing, Music and Finding as
    /// activities and the Blazor head has no page for any of them - so the head
    /// declares what it has rather than this file guessing.
    /// <para>
    /// The default is the Blazor head's set, unchanged, so wiring
    /// <c>AddSingleton&lt;IDeviceFacts, DeviceFacts&gt;()</c> keeps behaving
    /// exactly as it did.
    /// </para>
    /// </remarks>
    private readonly HashSet<string> _screens;

    /// <summary>The Blazor head's screens: the ones with a @page behind them.</summary>
    private static readonly string[] BlazorScreens = ["Waking"];

    /// <summary>The Blazor head's set. This is the constructor DI resolves.</summary>
    /// <remarks>
    /// EXPLICIT, NOT AN OPTIONAL PARAMETER, AND THE DIFFERENCE IS SILENT.
    /// Microsoft's container special-cases <c>IEnumerable&lt;T&gt;</c> and hands
    /// back an EMPTY one when nothing is registered - so a single constructor
    /// taking <c>IEnumerable&lt;string&gt;? screens = null</c> would have been
    /// resolved with an empty set rather than the default, and every "Try it"
    /// link in Settings would have quietly disappeared. Nothing would have
    /// thrown and no test would have gone red.
    /// </remarks>
    public DeviceFacts() : this(BlazorScreens) { }

    /// <param name="screens">The ability titles this head can actually open.</param>
    public DeviceFacts(IEnumerable<string> screens)
        => _screens = new HashSet<string>(screens, StringComparer.Ordinal);

    /// <summary>Every ability title, in order. A head naming its screens needs them.</summary>
    public static IReadOnlyList<string> Titles => [.. Catalogue.Select(a => a.Title)];

    /// <summary>Which model an ability means on this phone, or null.</summary>
    /// <remarks>
    /// FOR A HEAD THAT DRAWS ITS OWN DOWNLOAD. TurnOnAsync reports progress as
    /// words, which is all a Blazor row shows; the native head draws a bar and a
    /// time remaining, and both need the model's size to do it. Without this the
    /// head would keep its own ability-to-modality table - which is exactly the
    /// second copy this file exists to delete.
    /// <para>
    /// Null for an ability that needs no model, and null when nothing in the
    /// catalogue will run here.
    /// </para>
    /// </remarks>
    public static ModelEntry? ChoiceFor(
        string title,
        ModelRegistryService registry,
        BundleModelLoader loader,
        DeviceProbe probe)
    {
        var a = Catalogue.FirstOrDefault(x => x.Title == title);
        return a is null || a.NeedsNoModel
            ? null
            : ModelChoice.For(a.Modality, registry, loader, probe);
    }

    private static string StorageDir => ModelStore.Path;

    /// <summary>The blurb, with the language count filled in.</summary>
    /// <remarks>
    /// COUNTED, NOT HEDGED - "10 plus" disagreed with the abilities pitch and the
    /// language list - but counted HERE rather than in the table above, where it
    /// would run during type initialisation.
    /// <para>
    /// INSTALLED, NOT CATALOGUED, and this is the fourth screen to learn it. Home
    /// was fixed on 2026-09-06 to count what is on the disk; this one went on
    /// counting every Tts tag in the registry, so on 2026-09-07 a P30 showed "11
    /// languages, spoken out loud" on Home and "Reads things out loud, in 78
    /// languages" in Settings, on the same phone, two taps apart. The catalogue is
    /// what this device COULD speak once everything is downloaded, which is the
    /// right number for a picker offering downloads and the wrong one under a
    /// sentence describing what it does now.
    /// </para>
    /// <para>
    /// Falls back to the catalogue only when nothing reports as present, which is
    /// the honest answer for a head with no model store to inspect - the same rule
    /// Home applies, so the two cannot drift apart again without both moving.
    /// </para>
    /// </remarks>
    private static string Blurb(string template)
    {
        if (!template.Contains("{n}", StringComparison.Ordinal)) return template;

        // THE WORDS ARE ABILITYRULES', THE COUNT IS THIS FILE'S. The count needs
        // a registry, a loader and a phone; the sentence needs none of those, and
        // keeping them together is what left "in 1 languages" unassertable.
        return AbilityRules.Languages(template, SpokenLanguages());
    }

    /// <summary>How many languages this phone can speak RIGHT NOW.</summary>
    /// <remarks>
    /// INSTALLED, NOT CATALOGUED, AND THIS IS THE FIFTH SCREEN TO NEED IT. The
    /// catalogue is what the device COULD speak once everything is downloaded -
    /// the right number for a picker offering downloads, the wrong one under a
    /// sentence describing what it does now.
    /// <para>
    /// MEASURED ON A P30, 2026-09-12: home said "78 languages, spoken out loud"
    /// while the abilities screen two taps away said "in 1 language", on one
    /// phone with one voice installed. Both numbers were honestly computed and
    /// they came from different owners of one fact - which is the defect this
    /// whole class of change exists to stop.
    /// </para>
    /// <para>
    /// Public so a head can render the same number rather than reaching for
    /// SampleLanguages.All.Count, which is the catalogue.
    /// </para>
    /// </remarks>
    public static int SpokenLanguages()
    {
        try
        {
            using var registry = new ModelRegistryService();
            using var loader = new BundleModelLoader(StorageDir, registry);

            var voices = registry.AllModels
                .Where(m => m.Modality == ModelModality.Tts)
                .ToList();

            // Falls back to the catalogue only when nothing reports as present,
            // which is the honest answer for a phone that has not set up yet.
            var present = voices.Where(m => loader.ModelPresent(m.Name)).ToList();
            if (present.Count > 0) voices = present;

            var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in voices)
                foreach (var raw in (m.Language ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                    if (raw.Trim().Length > 0) tags.Add(raw.Trim());
            return tags.Count;
        }
        catch { return 0; }   // a blurb is not worth failing the screen for
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AbilityRow>> AbilitiesAsync(CancellationToken ct = default)
        => Task.Run<IReadOnlyList<AbilityRow>>(() =>
        {
            using var registry = new ModelRegistryService();
            using var loader = new BundleModelLoader(StorageDir, registry);
            var probe = DeviceProbe.Snapshot();
            var listening = Listening;

            var rows = new List<AbilityRow>(Catalogue.Length);
            foreach (var a in Catalogue)
            {
                ct.ThrowIfCancellationRequested();

                // THE SAME CHOICE THE CHAT SCREEN MAKES. The rule used to live
                // here in full and in a different form in DeviceBrain, so this
                // screen offered Answering at 547 MB while the chat screen said
                // it needed 22797 MB. One rule, one answer.
                var chosen = a.NeedsNoModel
                    ? null
                    : ModelChoice.For(a.Modality, registry, loader, probe);

                // Size, not SHA-256 - see ModelChoice.For.
                var present = chosen is not null && loader.ModelPresent(chosen.Name);

                // THE DECISION IS NOT MADE HERE ANY MORE. It is AbilityRules',
                // in the assembly that owns AbilityState - because it needs a
                // registry, a loader and a probe to get here, so nothing could
                // assert it, and the native head's copy of the same rule drifted
                // for weeks with every test green.
                rows.Add(AbilityRules.Decide(
                    a.Title,
                    Blurb(a.Blurb),
                    a.NeedsNoModel,
                    needsListening: a.Modality == ModelModality.WakeWord,
                    chosen?.TotalBytes,
                    present,
                    ModelChoice.AnyCatalogued(a.Modality, registry),
                    listening,
                    _screens.Contains(a.Title) ? a.Route : null));
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
                // THE NATIVE HEAD HAD THIS LINE AND THIS ONE DID NOT. A phone
                // that unloads a 500 MB model after a while is doing something
                // deliberate, and a person who is not told reads it as the app
                // getting slow again for no reason.
                new("Frees memory after",
                    $"{CircleAI.Device.AndroidMemoryPressure.IdleWindowFor(probe.Classify()).TotalMinutes:0} "
                    + "minutes unused, or straight away if the phone needs it"),
                new("Where it runs", "On this phone. Nothing is sent anywhere."),
                // NAMEABLE IN THE ROOM. Three phones on a table, and the only way
                // to tell which one had tonight's build was a file hash over adb.
                // Read off the screen instead: the display version is the thing
                // somebody says out loud, the build number is the thing that
                // actually goes up on every cut.
                new("Version", Version()),
            };

            var technical = new List<string>();
            foreach (var a in Catalogue)
            {
                // THE FOURTH PLACE, and a readout that named a different model
                // from the one the row above it offered is a diagnostics screen
                // actively misleading whoever came to it to diagnose something.
                var m = ModelChoice.For(a.Modality, registry, loader, probe);
                if (m is null) continue;
                technical.Add($"{a.Title}: {m.Name}\n{Size(m.TotalBytes)} · needs "
                            + $"{m.MinRamGb:0.#} GB · {m.Repo}");
            }

            return new PhoneFacts(facts, technical);
        }, ct);

    /// <summary>This build, as a person would read it off the screen.</summary>
    /// <remarks>
    /// ANDROID'S OWN PACKAGE MANAGER, NOT MAUI'S AppInfo. MAUI reads exactly this
    /// and wraps it, and that single call was the only thing keeping this file
    /// inside a MAUI application - which is the whole reason the native head had
    /// to answer the abilities question itself.
    /// </remarks>
    private static string Version()
    {
        try
        {
            var ctx = global::Android.App.Application.Context;
            var info = ctx.PackageManager!.GetPackageInfo(ctx.PackageName!, 0)!;
            var code = global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.P
                ? info.LongVersionCode
                : info.VersionCode;
            return $"{info.VersionName} (build {code})";
        }
        catch { return "unknown"; }
    }

    /// <inheritdoc />
    public async Task<string> TurnOnAsync(
        string title, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var entry = Catalogue.FirstOrDefault(a => a.Title == title);
        if (entry is null) return $"No ability called '{title}'.";
        if (entry.NeedsNoModel) return "On";

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
