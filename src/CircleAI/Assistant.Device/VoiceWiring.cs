#if IT_VOICE_ANDROID
#nullable enable

// VoiceWiring.cs
//
// The process-wide voice setup, in one place, so it cannot be half-done.
//
// IT WAS WIRED IN ONE ACTIVITY AND NEEDED IN TWO. CircleAISpeaker.MobilePhonemizerFactory
// is a STATIC — set it once and the whole process has a voice; never set it and
// English synthesis fails with "on device phonemizer not wired". It was being
// assigned in MainActivity.OnCreate, and MainActivity is not the launcher.
//
// So a normal run went: HomeActivity starts -> "Hey B" -> answer generated ->
// nothing spoken, because the factory was still null. The chat screen worked
// perfectly, which made it look like a voice problem rather than a startup-order
// problem: the one path that set the static was the one path anybody tested.
//
// The greeting on the home screen hid it further. Those are MMS voices, which are
// character-driven and never ask for phonemes, so the phone demonstrably spoke
// eleven languages on a screen where the English speaker could not say a word.
//
// A static that must be set before use, from whichever entry point happens to run
// first, is a rule no one can keep by remembering. Both activities now call this,
// it is idempotent, and it is the only place the assignment lives.

using System.IO.Compression;
using Android.Content;
using Android.Util;

namespace CircleAI.Assistant.Device;

/// <summary>One-time, order-independent wiring for on-device speech.</summary>
public static class VoiceWiring
{
    const string Tag = "CircleAI.VoiceWiring";

    static readonly object Gate = new();
    static bool _installed;

    // The real factory, and the gate that says it has been chosen. Both exist so
    // Install can return immediately while the expensive part runs elsewhere.
    static readonly ManualResetEventSlim Chosen = new(initialState: false);
    // Fully qualified like every other type in this file: CircleAI.Assistant.Device
    // has no using for CircleAI.Voice, and adding one here would collide with
    // Android.* names the rest of the file already has to qualify around.
    static Func<string, CircleAI.Voice.IPhonemizer>? _chosen;

    /// <summary>
    /// How long a caller asking for a phonemizer will wait for wiring to finish.
    /// </summary>
    /// <remarks>
    /// Generous on purpose: the only thing it is waiting for is an 11.9 MB unpack
    /// that happens once per install, measured at 317 ms on a P30. If this is ever
    /// hit, something is wrong and falling back is better than throwing into a
    /// synthesis call.
    /// </remarks>
    static readonly TimeSpan ChosenTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Makes sure the process can turn text into phonemes. Safe to call repeatedly.
    /// </summary>
    /// <remarks>
    /// RETURNS IMMEDIATELY, AND THAT IS THE POINT. This used to do its work inline,
    /// and the only caller is ServiceApplication.OnCreate — the service's MAIN
    /// thread. Measured on a P30 on 2026-10-08, from the service's own log:
    ///
    ///   23:09:04.978 → 05.295   espeak data unpacked          317 ms
    ///   23:09:05.295 → 05.427   native load + phonemize probe 132 ms
    ///   23:09:05.675            Choreographer: Skipped 92 frames!
    ///
    /// Android's own words for it: "The application may be doing too much work on
    /// its main thread." A service with no UI still owns the main looper, and
    /// blocking it delays every binder call, every lifecycle callback and the
    /// foreground notification — so a linked app asking a question during startup
    /// waits on a zip extraction.
    ///
    /// THE FACTORY IS STILL SET SYNCHRONOUSLY, because null is a load-bearing value
    /// here: CircleAISpeaker reads it and answers "on-device phonemizer not wired",
    /// and PersonalSpeech null-checks it. Deferring the ASSIGNMENT would turn a
    /// slow startup into a silent loss of speech for anything that asked early. So
    /// a waiting factory goes in at once and the work happens on a pool thread;
    /// whoever asks first waits for the choice instead of the main thread paying
    /// for it up front. After the first run there is nothing to unpack and the
    /// wait is the native probe alone.
    ///
    /// Phonemes come from in-process espeak where the native library is present,
    /// and otherwise from the SEPARATE espeak G2P app (com.bhengubv.espeakng)
    /// across a process boundary. If that app is absent too, the phonemizer throws
    /// a clear reason when it is used, which SpokenReply surfaces on screen rather
    /// than swallowing.
    /// <para>
    /// Called from every activity that can reach the speaker, because which one
    /// runs first depends on how the app was opened: the launcher, a notification,
    /// or the wake word.
    /// </para>
    /// </remarks>
    public static void Install(Context context)
    {
        lock (Gate)
        {
            if (_installed) return;
            _installed = true;

            // Application context, not the activity: this outlives whichever screen
            // happened to install it, and holding an activity in a static is how a
            // process-wide hook leaks a window.
            var app = context.ApplicationContext ?? context;

            // In place before this method returns, so nothing ever reads null.
            CircleAI.Assistant.Voice.CircleAISpeaker.MobilePhonemizerFactory = voice =>
            {
                if (!Chosen.Wait(ChosenTimeout))
                {
                    // Never throw into a synthesis call over a slow unpack. The
                    // out-of-process route is the same fallback Choose would pick.
                    Log.Warn(Tag, $"phonemizer: wiring still not finished after {ChosenTimeout.TotalSeconds:0}s — using the separate app");
                    return new OutOfProcessEspeakPhonemizer(app, voice);
                }
                return (_chosen ?? (v => new OutOfProcessEspeakPhonemizer(app, v)))(voice);
            };

            // The 11.9 MB unpack and the native probe. Long-running on purpose:
            // this is I/O plus a dlopen, not a queue of short work items, and
            // borrowing a pool thread for 450 ms would otherwise starve it.
            _ = Task.Factory.StartNew(
                () => Choose(app),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }
    }

    /// <summary>Picks the phonemizer this device can actually use, off the main thread.</summary>
    static void Choose(Context app)
    {
        try
        {
            ChooseCore(app);
        }
        catch (Exception ex)
        {
            // Install used to be wrapped in a try/catch by its caller, which cannot
            // see an exception on a pool thread. Keep the same outcome: a warning
            // and the out-of-process fallback, never an unobserved crash.
            Log.Warn(Tag, $"phonemiser not wired: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            // Released even on failure. A caller blocked on this must get the
            // fallback rather than wait out the full timeout.
            Chosen.Set();
        }
    }

    static void ChooseCore(Context app)
    {
            // ESPEAK IS IN THIS APK NOW. It lived in a second package only because
            // linking GPL code here would have forced a relicense; with that
            // constraint lifted it links in, and the one-APK rule — an app may
            // never require a second install to work — stops being violated.
            // Unpack the dictionaries once, point the phonemiser at them, and
            // confirm the native library actually loads before committing to it.
            var espeakData = UnpackEspeakData(app);
            if (espeakData is not null)
            {
                CircleAI.Voice.NativeEspeakPhonemizer.DataPath = espeakData;

                // Prove the native library loads AND produces phonemes before
                // committing to it. A DllNotFoundException surfacing later, from
                // inside synthesis, reads as "the voice broke" rather than "this
                // build has no espeak".
                try
                {
                    var probe = new CircleAI.Voice.NativeEspeakPhonemizer("en-us").Phonemize("test");
                    if (probe.Count > 0)
                    {
                        // _chosen, not the public static: the public one is already
                        // the waiting factory Install put there, and whoever is
                        // blocked on Chosen is waiting for exactly this line.
                        _chosen = voice => new CircleAI.Voice.NativeEspeakPhonemizer(voice);
                        Log.Info(Tag, $"phonemizer: espeak IN-PROCESS ({probe.Count} symbols, data={espeakData})");
                        return;
                    }
                    Log.Warn(Tag, "phonemizer: in-process espeak returned no symbols");
                }
                catch (Exception ex)
                {
                    Log.Warn(Tag, $"phonemizer: in-process espeak failed — {ex.GetType().Name}: {ex.Message}");
                }
            }

            // Fallback, not the plan: the separate GPL app, if the user happens to
            // have it. Kept because an arm64-only .so means x86_64 has no
            // in-process espeak, and a missing voice beats a crash.
            _chosen = voice => new OutOfProcessEspeakPhonemizer(app, voice);

            Log.Warn(Tag, "phonemizer: in-process espeak unavailable — falling back to the separate app");
    }

    /// <summary>
    /// Unpack <c>espeak-ng-data.zip</c> once into app storage and return the
    /// directory that CONTAINS <c>espeak-ng-data</c>.
    /// </summary>
    /// <remarks>
    /// espeak wants a real filesystem path; Android assets live inside the APK
    /// and have none, so they must be extracted before first use. Done once and
    /// then skipped — the marker is the unpacked folder itself, so a half-finished
    /// extraction (killed mid-copy) re-runs rather than leaving espeak pointed at
    /// a partial dictionary set, which would mispronounce rather than fail.
    /// </remarks>
    private static string? UnpackEspeakData(Context app)
    {
        try
        {
            var root = app.FilesDir?.AbsolutePath;
            if (string.IsNullOrEmpty(root)) return null;

            var target = System.IO.Path.Combine(root, "espeak");
            var dataDir = System.IO.Path.Combine(target, "espeak-ng-data");

            // phontab is the file espeak loads first; its presence means the
            // unpack completed, where a bare directory would not.
            if (System.IO.File.Exists(System.IO.Path.Combine(dataDir, "phontab")))
                return target;

            if (System.IO.Directory.Exists(target)) System.IO.Directory.Delete(target, true);
            System.IO.Directory.CreateDirectory(target);

            using (var zip = app.Assets!.Open("espeak-ng-data.zip"))
            using (var archive = new System.IO.Compression.ZipArchive(zip, System.IO.Compression.ZipArchiveMode.Read))
            {
                foreach (var entry in archive.Entries)
                {
                    var dest = System.IO.Path.GetFullPath(System.IO.Path.Combine(target, entry.FullName));
                    if (!dest.StartsWith(target, StringComparison.Ordinal)) continue;   // zip-slip
                    if (string.IsNullOrEmpty(entry.Name)) { System.IO.Directory.CreateDirectory(dest); continue; }
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dest)!);
                    entry.ExtractToFile(dest, overwrite: true);
                }
            }

            Log.Info(Tag, $"espeak data unpacked to {dataDir}");
            return target;
        }
        catch (Exception ex)
        {
            Log.Warn(Tag, $"espeak data unpack failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
#endif
