// DeviceWiringProbe.cs
//
// What this phone can actually do, asked of the phone.
//
// WRITTEN THE DAY THE HYBRID TURNED OUT TO BE MUTE. It had shipped able to hear
// and translate and never speak, because ItSpeaker.MobilePhonemizerFactory was
// never set: the head linked five files from the native app and not VoiceWiring,
// and shipped no espeak at all. Nothing could have reported that. The setup
// census counts DOWNLOADS, and a phonemizer is not a download - it is a csproj
// link and a line in OnCreate. So the app went on advertising "78 languages,
// spoken out loud" while the true number was one, and the only symptom was a
// sentence under a translation somebody had already stopped trusting.
//
// EVERY ANSWER HERE COMES FROM THE REAL PATH. Voices are judged by asking
// ItSpeaker.TryCreateAsync to build the speaker the app would build, and
// reporting the status string it hands back - the same string that reached the
// screen. Re-deriving "can this speak" would be a second implementation of that
// rule, free to drift from the one that runs, which is the exact shape of the
// bug this file exists to catch.

using CircleAI.Samples.It.App.Services;
using CircleAI.Samples.It.Voice;

namespace CircleAI.Samples.It.App.Services;

/// <inheritdoc />
public sealed class DeviceWiringProbe : IWiringProbe
{
    private const string Tag = "CircleAI.Wiring";

    private readonly IVoiceHost _voice;

    public DeviceWiringProbe(IVoiceHost voice) => _voice = voice;

    /// <inheritdoc />
    public Task<WiringReport> HooksAsync(CancellationToken ct = default)
        => Task.Run(Hooks, ct);

    /// <summary>The hook report, with no instance and no DI.</summary>
    /// <remarks>
    /// STATIC BECAUSE STARTUP HAS NO SCOPE. Every check here reads a static hook
    /// or a directory, so the most useful moment to run it - Application.OnCreate,
    /// before a single screen exists - is also the one where no service provider
    /// has been built yet.
    /// </remarks>
    public static WiringReport Hooks()
    {
        var rows = new List<WiringRow>
        {
            Timed(Phonemizer),
            Timed(EspeakData),
            Timed(OpenJTalk),
            Timed(RealRam),
            Timed(WakeBundle),
        };

        return new WiringReport(rows, rows.Count(r => r.Working), rows.Count);
    }

    /// <summary>Runs one check and records how long it took.</summary>
    /// <remarks>
    /// AROUND THE CALL, NOT INSIDE EACH CHECK. Five methods each starting their
    /// own stopwatch is five chances to forget one, and a row with no timing in a
    /// report that shows timings reads as instant rather than unmeasured.
    /// <para>
    /// It matters because the loading screen holds the door until these finish.
    /// "The app takes a while to start" is not a finding; "espeak G2P took 2.1 of
    /// the 2.4 seconds" is one.
    /// </para>
    /// </remarks>
    private static WiringRow Timed(Func<WiringRow> check)
    {
        var start = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            return check() with { Took = System.Diagnostics.Stopwatch.GetElapsedTime(start) };
        }
        catch (Exception ex)
        {
            // A CHECK THAT THROWS IS A FINDING, NOT A CRASH. This runs at
            // startup, and an exception escaping here would take the app down
            // over a diagnostic - the report exists to survive the thing it is
            // reporting on.
            return new WiringRow(
                check.Method.Name, "hook", WiringStage.Broken,
                $"the check itself threw — {ex.GetType().Name}: {ex.Message}",
                Took: System.Diagnostics.Stopwatch.GetElapsedTime(start));
        }
    }

    /// <summary>
    /// THE WIRE THAT WAS MISSING. Not "is the factory null" alone: a factory that
    /// is set and throws, or returns a phonemizer that yields nothing, is a
    /// working wire by that test and a mute phone in fact. So it is invoked.
    /// </summary>
    private static WiringRow Phonemizer()
    {
        const string title = "Phonemizer (text to sounds)";

        if (ItSpeaker.MobilePhonemizerFactory is null)
            return new WiringRow(title, "hook", WiringStage.Absent,
                "ItSpeaker.MobilePhonemizerFactory is null — VoiceWiring.Install was never called. "
                + "Every voice that needs espeak G2P will refuse.",
                Where: null,
                Who: "ItSpeaker.MobilePhonemizerFactory, set by VoiceWiring.Install in MainApplication");

        try
        {
            var symbols = ItSpeaker.MobilePhonemizerFactory("en-us").Phonemize("test");
            return symbols.Count > 0
                ? new WiringRow(title, "hook", WiringStage.Wired,
                    $"espeak G2P answered with {symbols.Count} symbols for \"test\"",
                    Where: CircleAI.Voice.NativeEspeakPhonemizer.DataPath,
                    Who: "ItSpeaker.MobilePhonemizerFactory, set by VoiceWiring.Install")
                : new WiringRow(title, "hook", WiringStage.Broken,
                    "the phonemizer is wired and returned NO symbols — set, but useless",
                    Where: CircleAI.Voice.NativeEspeakPhonemizer.DataPath,
                    Who: "ItSpeaker.MobilePhonemizerFactory, set by VoiceWiring.Install");
        }
        catch (Exception ex)
        {
            return new WiringRow(title, "hook", WiringStage.Broken,
                $"the phonemizer is wired and threw — {ex.GetType().Name}: {ex.Message}",
                Where: CircleAI.Voice.NativeEspeakPhonemizer.DataPath,
                Who: "ItSpeaker.MobilePhonemizerFactory, set by VoiceWiring.Install");
        }
    }

    /// <summary>espeak's dictionaries, unpacked from the APK on first use.</summary>
    /// <remarks>
    /// Its own row because the failure is silent by design: VoiceWiring wraps the
    /// unpack in a try/catch and falls back to the separate app, so a wrong asset
    /// path produces no voice and no error - which is indistinguishable from the
    /// bug this file was written for.
    /// </remarks>
    private static WiringRow EspeakData()
    {
        const string title = "espeak dictionaries";
        var path = CircleAI.Voice.NativeEspeakPhonemizer.DataPath;

        if (string.IsNullOrWhiteSpace(path))
            return new WiringRow(title, "engine", WiringStage.Absent,
                "NativeEspeakPhonemizer.DataPath is unset — the espeak-ng-data.zip asset never unpacked",
                Who: "NativeEspeakPhonemizer.DataPath, unpacked by VoiceWiring.Install");

        var dir = System.IO.Path.Combine(path, "espeak-ng-data");
        if (!System.IO.Directory.Exists(dir))
            return new WiringRow(title, "engine", WiringStage.Broken,
                $"DataPath points at {path} but {dir} does not exist",
                Where: dir,
                Who: "NativeEspeakPhonemizer.DataPath, unpacked by VoiceWiring.Install");

        var files = System.IO.Directory.EnumerateFiles(dir, "*", System.IO.SearchOption.AllDirectories).Take(2).Count();
        return files > 0
            ? new WiringRow(title, "engine", WiringStage.Wired, $"unpacked at {dir}",
                Where: dir, Who: "NativeEspeakPhonemizer.DataPath, unpacked by VoiceWiring.Install")
            : new WiringRow(title, "engine", WiringStage.Broken, $"{dir} exists and is empty",
                Where: dir, Who: "NativeEspeakPhonemizer.DataPath, unpacked by VoiceWiring.Install");
    }

    /// <summary>Japanese has its own G2P, and it is wired somewhere else entirely.</summary>
    /// <remarks>
    /// This row is why the mute build was so hard to see: Japanese kept speaking
    /// because OpenJTalk is set in MainApplication, so the failure looked like a
    /// language gap rather than a missing wire.
    /// </remarks>
    private static WiringRow OpenJTalk()
    {
        const string title = "Japanese phonemizer (Open JTalk)";
        var folder = CircleAI.Voice.OpenJTalkPhonemizer.ModelStoreFolder;

        if (string.IsNullOrWhiteSpace(folder))
            return new WiringRow(title, "engine", WiringStage.Absent,
                "OpenJTalkPhonemizer.ModelStoreFolder is unset",
                Who: "OpenJTalkPhonemizer.ModelStoreFolder, set in MainApplication.OnCreate");

        return System.IO.Directory.Exists(folder)
            ? new WiringRow(title, "engine", WiringStage.Wired, $"model store at {folder}",
                Where: folder, Who: "OpenJTalkPhonemizer.ModelStoreFolder, set in MainApplication.OnCreate")
            : new WiringRow(title, "engine", WiringStage.Broken, $"model store {folder} does not exist",
                Where: folder, Who: "OpenJTalkPhonemizer.ModelStoreFolder, set in MainApplication.OnCreate");
    }

    /// <summary>
    /// The RAM hook, because without it every model fails its own fit check.
    /// </summary>
    /// <remarks>
    /// DeviceProbe falls back to the GC heap limit - a few hundred MB on a 4 GB
    /// phone - so nothing throws and every ability quietly reads "Needs more
    /// memory". A capability report that could not see this would miss the reason
    /// a fully-downloaded phone refuses to use what it has.
    /// </remarks>
    private static WiringRow RealRam()
    {
        const string title = "Real RAM reading";
        try
        {
            var bytes = CircleAI.Core.DeviceProbe.Snapshot().RamTotalBytes;
            var gb = bytes / 1024d / 1024d / 1024d;

            // A GC heap limit reads as a few hundred MB. No phone this app
            // supports has under a gigabyte, so that is the tell.
            const string who = "DeviceProbe.PlatformMemoryProbe, set by AndroidDeviceMemory.Install";
            return bytes >= 1L << 30
                ? new WiringRow(title, "hook", WiringStage.Wired, $"{gb:0.0} GB", Who: who)
                : new WiringRow(title, "hook", WiringStage.Broken,
                    $"reports {gb:0.00} GB — that is the GC heap limit, not the phone. "
                    + "AndroidDeviceMemory.Install did not run.",
                    Who: who);
        }
        catch (Exception ex)
        {
            return new WiringRow(title, "hook", WiringStage.Broken, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>The wake bundle, located the one way that knows a half-download is not an install.</summary>
    private static WiringRow WakeBundle()
    {
        const string title = "Wake word bundle";
        try
        {
            var bundle = DeviceWakeWord.FindBundle();
            return bundle is null
                ? new WiringRow(title, "engine", WiringStage.Absent, "no wake bundle on this device",
                    Who: "DeviceWakeWord.FindBundle")
                : new WiringRow(title, "engine", WiringStage.Wired, bundle,
                    Where: bundle, Who: "DeviceWakeWord.FindBundle");
        }
        catch (Exception ex)
        {
            return new WiringRow(title, "engine", WiringStage.Broken, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async Task<WiringReport> VoicesAsync(
        IEnumerable<string>? languages = null,
        IProgress<WiringRow>? progress = null,
        CancellationToken ct = default)
    {
        var catalogue = await _voice.CatalogueAsync(ct).ConfigureAwait(false);
        var claimed = catalogue.Count;

        var wanted = languages?.ToList();
        var tags = wanted is { Count: > 0 }
            ? catalogue.Where(r => wanted.Contains(r.Tag)).Select(r => r.Tag).ToList()
            : catalogue.Select(r => r.Tag).ToList();

        var rows = new List<WiringRow>(tags.Count);

        foreach (var tag in tags)
        {
            ct.ThrowIfCancellationRequested();
            var row = await VoiceAsync(tag, ct).ConfigureAwait(false);
            rows.Add(row);
            progress?.Report(row);
        }

        // Claimed stays the WHOLE catalogue even when a subset was asked for, so
        // "3 of 78" cannot be mistaken for "3 of 3" by a screen showing three rows.
        return new WiringReport(rows, rows.Count(r => r.Working), claimed);
    }

    /// <summary>Can this phone actually speak this language?</summary>
    /// <remarks>
    /// BY BUILDING THE SPEAKER THE APP WOULD BUILD, and no further: TryCreateAsync
    /// runs the whole selection - catalogue, model fit, phonemizer, engine - and
    /// hands back either a speaker or the reason there is none. Nothing is
    /// synthesised and nothing is played, so a sweep of seventy-eight languages
    /// does not turn the phone into a radio.
    /// </remarks>
    private async Task<WiringRow> VoiceAsync(string tag, CancellationToken ct)
    {
        var name = SampleLanguages.Find(tag)?.Name ?? tag;
        var title = $"{name} voice";

        try
        {
            var (speaker, status) = await ItSpeaker
                .TryCreateAsync(ModelStore.Path, log: null, ct, languageCode: tag)
                .ConfigureAwait(false);

            if (speaker is null)
                return new WiringRow(title, "voice", WiringStage.Broken, status);

            // Built, so it can speak. Disposed straight away: this is a question,
            // not a session, and holding seventy-eight engines open to answer it
            // would be its own outage.
            (speaker as IDisposable)?.Dispose();
            return new WiringRow(title, "voice", WiringStage.Wired, status);
        }
        catch (Exception ex)
        {
            return new WiringRow(title, "voice", WiringStage.Broken, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>The file that asks for a full voice sweep on the next launch.</summary>
    /// <remarks>
    /// A SWEEP IS MINUTES, so it is not something startup should decide to do.
    /// It builds and discards a speaker per language to find out which ones can
    /// really talk, and on a P30 that is the better part of a coffee. So it runs
    /// when asked and not otherwise:
    ///
    ///     adb shell run-as com.bhengubv.circleai.hybrid touch files/sweep-voices
    ///
    /// One-shot on purpose - the marker is consumed as it starts, so a sweep
    /// left switched on cannot quietly cost every launch after it.
    /// </remarks>
    public static string SweepMarker =>
        System.IO.Path.Combine(FileSystem.AppDataDirectory, "sweep-voices");

    /// <summary>Runs the voice sweep to logcat, if the marker asked for one.</summary>
    public static async Task SweepVoicesIfRequestedAsync()
    {
        try
        {
            if (!System.IO.File.Exists(SweepMarker)) return;
            System.IO.File.Delete(SweepMarker);

            Android.Util.Log.Info(Tag, "voices: sweep requested — building a speaker per language");
            var probe = new DeviceWiringProbe(new DeviceVoiceHost());

            var report = await probe.VoicesAsync(
                progress: new Progress<WiringRow>(r =>
                {
                    var line = $"  [{r.Stage}] {r.Title} — {r.Detail}";
                    if (r.Working) Android.Util.Log.Info(Tag, line);
                    else Android.Util.Log.Warn(Tag, line);
                })).ConfigureAwait(false);

            // THE HEADLINE IS THE CLAIM AND THE TRUTH SIDE BY SIDE, because that
            // gap is the whole reason this exists: the home screen was printing
            // the left-hand number and nothing was measuring the right-hand one.
            Android.Util.Log.Info(Tag, $"voices: {report.Summary}");
            foreach (var r in report.Failing)
                Android.Util.Log.Warn(Tag, $"voices: CANNOT SPEAK {r.Title} — {r.Detail}");
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn(Tag, $"voices: sweep failed — {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Writes the hook report to logcat, once, at startup.</summary>
    /// <remarks>
    /// SO THAT EVERY RUN SAYS WHAT IT CAN DO. The mute build produced no line
    /// anywhere saying the phonemizer was missing - it had to be inferred from a
    /// translation that never spoke. One line per hook, at Info, costs nothing
    /// and makes the next missing wire a grep rather than a day.
    /// </remarks>
    public static void LogHooks()
    {
        try
        {
            var report = Hooks();
            Android.Util.Log.Info(Tag, $"hooks: {report.Summary}");
            foreach (var r in report.Rows)
            {
                var line = $"  [{r.Stage}] {r.Title} — {r.Detail}";
                if (r.Working) Android.Util.Log.Info(Tag, line);
                else Android.Util.Log.Warn(Tag, line);
            }
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn(Tag, $"could not probe wiring: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
