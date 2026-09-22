using Android.App;
using Android.Runtime;

namespace CircleAI.Samples.App;

/// <summary>The Android application object.</summary>
/// <remarks>
/// [Application] IS LOad-BEARING, and leaving it off is silent. Without it the
/// manifest names no Application class, Android instantiates the default one,
/// MauiApplication.CreateMauiApp is never called, and MAUI never initialises.
/// The activity still starts and still draws - a blank window whose
/// android:id/content has no child at all. No exception, no logcat entry, no
/// WebView: indistinguishable from a Blazor component that failed to render,
/// which is where two hours went looking.
/// </remarks>
[Application]
public class MainApplication : MauiApplication
{
    /// <summary>Runtime constructor.</summary>
    public MainApplication(IntPtr handle, JniHandleOwnership ownership)
        : base(handle, ownership)
    {
    }

    /// <inheritdoc />
    public override void OnCreate()
    {
        // REAL RAM BEFORE MAUI INITIALISES, AND THAT ORDER IS THE WHOLE FIX.
        // base.OnCreate() runs CreateMauiApp, which runs the catalogue bootstrap and
        // its DeviceProbe.Snapshot(). Installed AFTER base.OnCreate (as it was), the
        // platform memory probe arrives too late: Snapshot has already fallen back to
        // the GC heap (~100 MB), so every model fails its fit check and the device
        // INTERMITTENTLY decides it can run nothing — measured on the Redmi, a launch
        // that assessed 16 models compatible and the next that assessed 1. Install it
        // first; it only needs the Application context, which exists by now.
        CircleAI.Device.AndroidDeviceMemory.Install(this);

        base.OnCreate();

        // DID THE LAST RUN DIE? SAY SO.
        //
        // A stack overflow, an OOM kill or a SIGSEGV inside a native runtime runs
        // NO handler - there is no exception to catch and nothing in logcat by
        // the time somebody looks. DeviceDiagnostics writes down what is about to
        // be attempted and checks for it on the next start, which is the only
        // thing that works against a death like that.
        //
        // It has existed in CircleAI.Assistant.Runtime the whole time with no
        // caller in this head: the app simply reopened as though nothing had
        // happened, and the person who watched it vanish mid-answer had no way to
        // find out why. Logged rather than shown, because a toast on launch about
        // a crash somebody may not have noticed is its own problem.
        try
        {
            var stateDir = FilesDir?.AbsolutePath;
            if (stateDir is not null)
            {
                // ONE well-known place for the crash breadcrumb, so the risky spans
                // (DeviceBrain's model load) write it where this reads it.
                CircleAI.Assistant.DeviceDiagnostics.DiagnosticsDirectory = stateDir;

                if (CircleAI.Assistant.DeviceDiagnostics.PreviousCrash(stateDir) is { Length: > 0 } died)
                {
                    Android.Util.Log.Error("CircleAI.Crash",
                        $"the previous run died with no handler while: {died}");

                    // FEED IT TO THE SELF-HEAL LOOP. A native death (OOM / SIGSEGV)
                    // runs no handler, so this launch is the only chance to record it —
                    // otherwise the worst failures never reach Wolverine. Fire-and-
                    // forget; the loop never throws.
                    var healer = Microsoft.Maui.IPlatformApplication.Current?.Services?
                        .GetService(typeof(CircleAI.Hosting.SelfHealing.ISelfHealer))
                        as CircleAI.Hosting.SelfHealing.ISelfHealer;
                    _ = healer?.HealAsync(new CircleAI.Hosting.SelfHealing.FailureContext(
                        Message: $"The app closed unexpectedly while: {died}",
                        Source: "native-crash"));
                }
            }
        }
        catch { /* diagnostics must never be the thing that fails */ }

        // (DeviceProbe's real-RAM probe is installed at the TOP of OnCreate now,
        // before base.OnCreate() runs the catalogue bootstrap — see the note there.)

        // THE MEMORY MANAGER'S CENSUS, ON LAUNCH. Step zero of the footprint
        // budget: print the phone's REAL disk and RAM - StatFs and
        // ActivityManager, not the GC heap the probe above exists to correct -
        // and what Circle AI is using, split into what is precious (downloaded
        // models, the person's memory) and what is free to give back (skills,
        // espeak, cache). A claim about storage is then checkable in logcat
        // rather than believed. The budget itself (MemoryBudget) is pure and
        // tested; this is where the real numbers enter it.
        try
        {
            var manager = new CircleAI.Assistant.Device.MemoryManager(
                new CircleAI.Assistant.Device.DeviceResourcesReader());
            manager.LogCensus();

            // AND TIDY THE CACHE, ON LAUNCH, TO THE PERSON'S OWN KEEP/CAP. The
            // self-managing half of the footprint budget: age out scratch past its
            // window and cap what remains, so the regenerable cache cannot creep up
            // across sessions on a phone that is already full. The choices live in
            // the one app store; resolve JUST that singleton (not the whole settings
            // graph) so this stays cheap at OnCreate, and fall back to the defaults
            // if it is not up yet. The CACHE ALONE - models and the person's memory
            // are never enumerated (see MemoryManager.TrimCache). Logged either way,
            // so the launch tidy-up is visible in logcat rather than silent.
            var store = Microsoft.Maui.IPlatformApplication.Current?.Services?
                .GetService(typeof(CircleAI.Assistant.Device.SqliteAppStore))
                as CircleAI.Assistant.Device.SqliteAppStore;
            long trimmed;
            if (store is not null)
            {
                var (keep, cap) = CircleAI.Assistant.Device.CachePolicySettings.Resolve(store);
                trimmed = manager.TrimCache(cap, keep);
            }
            else
            {
                trimmed = manager.TrimCache();
            }
            Android.Util.Log.Info("CircleAI.Memory",
                trimmed > 0
                    ? $"launch cache tidy: freed {CircleAI.Assistant.MemoryBudget.Human(trimmed)}"
                    : "launch cache tidy: nothing to remove");
        }
        catch (System.Exception ex)
        {
            Android.Util.Log.Warn("CircleAI.Memory", "census on launch failed: " + ex.Message);
        }

        // AND THE PHONEMIZER, or this app can hear and translate but never
        // speak. The native head has always called this; the hybrid never did,
        // so CircleAISpeaker.MobilePhonemizerFactory stayed null and every voice that
        // needs espeak G2P - all of them but Japanese - refused with "on-device
        // phonemizer not wired". Same call, same file, same order as
        // CircleAIApplication.OnCreate.
        CircleAI.Assistant.Device.VoiceWiring.Install(this);

        // WHERE A SIDELOADED VOICE IS FOUND - AND THIS HEAD NEVER LOOKED.
        //
        // CircleAISpeaker.SideloadFolder is read at CircleAISpeaker:841 and by
        // CircleAITtsProbe twice, and only the native sample ever set it. So a
        // voice bundle the owner copied onto the phone - the delivery route for
        // somebody with no data to spend - was invisible here, and the app would
        // offer to download something already sitting on the device.
        //
        // The external files dir, because that is where adb and a file manager
        // can put something without root. The importer still checks every byte
        // against the catalogue's hash, so this is a delivery route and not a
        // trust hole.
        //
        // Audited 2026-09-13: the third of three seams this file set and the
        // native head did not, after the phonemizer and the memory probe.
        CircleAI.Assistant.Voice.CircleAISpeaker.SideloadFolder =
            GetExternalFilesDir(null)?.AbsolutePath;

        // WHERE A SIDELOADED OPEN JTALK DICTIONARY LIVES - the other half this
        // head never set, and the exact sibling of the SideloadFolder bug above.
        //
        // 104 MB of compiled morphology (sys.dic alone is 100 MB), shared by
        // every Japanese voice, so it is registered once rather than bundled into
        // any one of them. OpenJTalkPhonemizer searches BOTH this and the model
        // store; setting only the store meant a dictionary copied onto the phone
        // was invisible, and the Japanese voice refuses to speak rather than
        // falling back to characters - which would be confident noise.
        CircleAI.Voice.OpenJTalkPhonemizer.DictionaryFolder =
            GetExternalFilesDir(null)?.AbsolutePath;

        // Where the phonemiser looks for Open JTalk's dictionary once it has been
        // downloaded. The model store, not the sideload folder: the catalogued
        // entry unpacks into the store, and a registry entry nothing can find is
        // decorative.
        CircleAI.Voice.OpenJTalkPhonemizer.ModelStoreFolder =
            CircleAI.Assistant.Device.ModelStore.Path;

        // Managed voice logging reaches nothing through ILogger on Android, so it
        // goes to logcat directly - the one place it is actually readable.
        CircleAI.Voice.VoiceTrace.Sink = line => Android.Util.Log.Info("ITHYB", line);

        // And what the voice router heard, and where it sent it. Its own tag so
        // a session's routing decisions are one grep, and it logs the MISSES too:
        // the matcher is tuned against typed guesses until real Whisper output
        // is written down somewhere.
        CircleAI.Assistant.VoiceTurnRouter.Trace =
            line => Android.Util.Log.Info("CircleAI.Route", line);

        // AND NOW SAY WHAT IS ACTUALLY WIRED. Last, so the trace sink above is
        // already attached and every hook reports through the same channel.
        //
        // THE MUTE BUILD PRODUCED NO LINE ANYWHERE saying the phonemizer was
        // missing - it had to be inferred from a translation that never spoke,
        // days later. Five lines at startup make the next missing wire a grep
        // instead of a day.
        CircleAI.Assistant.Device.DeviceWiringProbe.LogHooks();

        // THE SKILL LIBRARY - AND IT WAS WIRED IN THE NATIVE HEAD ONLY, WHICH IS
        // THE SAME MISTAKE THIS FILE ALREADY RECORDS TWICE ABOVE. The phonemizer
        // and the memory probe were both "called by the native head, never by
        // this one", each found days later by a feature that quietly did nothing.
        // The 5.1 MB skills.db.zip asset was linked into this project and nothing
        // here ever opened it.
        //
        // Off the UI thread: the first call unpacks 20 MB out of the APK, and
        // OnCreate runs before any page exists. Nothing needs it until a turn.
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                CircleAI.Assistant.CircleAISession.Library =
                    CircleAI.Assistant.Device.SkillLibrary.Open(this);
            }
            catch (Exception ex)
            {
                Android.Util.Log.Warn("CircleAI.Skills",
                    $"skill library: {ex.GetType().Name}: {ex.Message}");
            }
        });

        // And a full voice sweep, only when somebody has asked for one. Not
        // awaited: it takes minutes, and startup is not allowed to wait on a
        // diagnostic.
        _ = CircleAI.Assistant.Device.DeviceWiringProbe.SweepVoicesIfRequestedAsync();
    }

    /// <inheritdoc />
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
