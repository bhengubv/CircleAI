#if IT_VOICE_ANDROID
#nullable enable

// VoiceWiring.cs
//
// The process-wide voice setup, in one place, so it cannot be half-done.
//
// IT WAS WIRED IN ONE ACTIVITY AND NEEDED IN TWO. ItSpeaker.MobilePhonemizerFactory
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

namespace CircleAI.Samples.It.Mobile;

/// <summary>One-time, order-independent wiring for on-device speech.</summary>
public static class VoiceWiring
{
    const string Tag = "CircleAI.VoiceWiring";

    static readonly object Gate = new();
    static bool _installed;

    /// <summary>
    /// Makes sure the process can turn text into phonemes. Safe to call repeatedly.
    /// </summary>
    /// <remarks>
    /// Phonemes come from the SEPARATE espeak G2P app (com.bhengubv.espeakng) across
    /// a process boundary — espeak-ng is GPL and is never linked into CircleAI. If
    /// that app is absent the phonemizer throws a clear reason when it is used,
    /// which SpokenReply now surfaces on screen rather than swallowing.
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

            // Application context, not the activity: this outlives whichever screen
            // happened to install it, and holding an activity in a static is how a
            // process-wide hook leaks a window.
            var app = context.ApplicationContext ?? context;

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
                        CircleAI.Samples.It.Voice.ItSpeaker.MobilePhonemizerFactory =
                            voice => new CircleAI.Voice.NativeEspeakPhonemizer(voice);
                        _installed = true;
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
            CircleAI.Samples.It.Voice.ItSpeaker.MobilePhonemizerFactory =
                voice => new OutOfProcessEspeakPhonemizer(app, voice);

            _installed = true;
            Log.Warn(Tag, "phonemizer: in-process espeak unavailable — falling back to the separate app");
        }
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
