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
        // THE MEMORY PROBE MOVED WITH THE MODELS. It exists so DeviceProbe reports
        // real RAM instead of the GC heap limit, and that only matters to something
        // deciding whether a model FITS — which this app no longer does. The service
        // installs it in its own ServiceApplication, before anything can probe.

        base.OnCreate();

        // THE CRASH BREADCRUMB WENT WITH THE MODEL, and it is worth saying what
        // that costs. DeviceDiagnostics wrote down what was about to be attempted and
        // checked for it on the next start, which is the only thing that works against
        // a death that runs no handler: a stack overflow, an OOM kill, a SIGSEGV
        // inside a native runtime. Every one of those came from loading or running a
        // model, in this process, and none of them can happen here any more - the
        // model is in CircleAI, and so is the breadcrumb that catches it dying.
        //
        // It also lived in CircleAI.Assistant.Runtime, which reaches Hosting and then
        // the whole inference engine. Keeping it for the failures a thin app CAN have
        // would have meant carrying all of that for a text file.

        // (DeviceProbe's real-RAM probe is installed at the TOP of OnCreate now,
        // before base.OnCreate() runs the catalogue bootstrap — see the note there.)

        // NO CENSUS ON LAUNCH, BECAUSE THERE IS NOTHING LEFT TO COUNT. MemoryManager
        // printed the phone's real disk and RAM and what Circle AI was using, split
        // into what is precious (downloaded models, the person's memory) and what is
        // free to give back. Every one of those figures is now the service's, and it
        // went with MemoryManager to CircleAI.Assistant.Device.
        //
        // WHAT STAYED IS THIS APP'S OWN CACHE - the web view's, the scratch a share
        // leaves, decoded audio - aged out to the person's own keep/cap. It is a
        // directory this app owns, so tidying it is this app's job and nobody else's.
        // Models and the person's memory are not reachable from here at all, which is
        // a stronger guarantee than the old "never enumerates them" comment was.
        try
        {
            var store = Microsoft.Maui.IPlatformApplication.Current?.Services?
                .GetService(typeof(CircleAI.Assistant.Device.SqliteAppStore))
                as CircleAI.Assistant.Device.SqliteAppStore;

            long trimmed;
            if (store is not null)
            {
                var (keep, cap) = CircleAI.Assistant.Device.CachePolicySettings.Resolve(store);
                trimmed = CircleAI.Assistant.Device.AppCache.Trim(cap, keep);
            }
            else
            {
                trimmed = CircleAI.Assistant.Device.AppCache.Trim();
            }

            Android.Util.Log.Info("CircleAI.Cache",
                trimmed > 0
                    ? $"launch cache tidy: freed {CircleAI.Assistant.MemoryBudget.Human(trimmed)}"
                    : "launch cache tidy: nothing to remove");
        }
        catch (System.Exception ex)
        {
            Android.Util.Log.Warn("CircleAI.Cache", "cache tidy on launch failed: " + ex.Message);
        }

        // AND THE PHONEMIZER, or this app can hear and translate but never
        // speak. The native head has always called this; the hybrid never did,
        // so CircleAISpeaker.MobilePhonemizerFactory stayed null and every voice that
        // needs espeak G2P - all of them but Japanese - refused with "on-device
        // phonemizer not wired". Same call, same file, same order as
        // CircleAIApplication.OnCreate.
        // VoiceWiring.Install is GONE from this app. It unpacked espeak's data and
        // installed the phonemiser so this process could synthesise speech; the
        // service does that now. Worth noting what else leaves with it: espeak-ng is
        // GPL-3.0 and was kept out-of-process precisely so linking it would not
        // relicense the app — a client that no longer synthesises needs none of that
        // arrangement.

        // NO SIDELOAD FOLDER, NO DICTIONARY FOLDER, NO SPEAKER. Three statics stood
        // here and all three configured SYNTHESIS: where CircleAISpeaker looks for a
        // voice bundle somebody copied onto the phone, and where Open JTalk finds its
        // 104 MB of Japanese morphology. This process no longer synthesises a word -
        // it asks CircleAI for PCM and plays it - so setting them would be wiring a
        // path into a code path that is not here.
        //
        // The service sets its own, against its own external files directory, which
        // is also where a sideloaded voice has to land for the process that reads it.

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
        // The voice-wiring log went with the voice stack. What this app can fail to
        // wire is the LINK, and ServiceWiringProbe reports that on demand rather than
        // at every startup.

        // THE SKILL LIBRARY IS THE BRAIN'S, so it is opened where the brain is. It
        // unpacks 20 MB out of an APK to give a model something to look things up in;
        // an app that asks questions over a link needs neither the database nor the
        // 5.1 MB asset. CircleAISession, which held it, is in CircleAI.Assistant.Runtime
        // anyway - the engine side of the split.

        // And a full voice sweep, only when somebody has asked for one. Not
        // awaited: it takes minutes, and startup is not allowed to wait on a
        // diagnostic.
        // No voice sweep here: the voices are in the service, and walking them across
        // the link would mean synthesising the whole catalogue to fill a log line.
    }

    /// <inheritdoc />
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
