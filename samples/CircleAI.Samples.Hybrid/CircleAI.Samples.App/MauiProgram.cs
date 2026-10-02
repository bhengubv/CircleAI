// The MAUI head's composition root.
//
// Registers this device's answers to the shared UI's questions and hands it a
// BlazorWebView to render in.

using CircleAI.Assistant;
using CircleAI.Assistant.Device;
using CircleAI.Assistant.Voice;   // FileTranscriptStore
using Microsoft.Extensions.Logging;

namespace CircleAI.Samples.App;

/// <summary>Builds the app.</summary>
public static class MauiProgram
{
    /// <summary>Compose and return the app.</summary>
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        // THE MMAP SWITCH IS THE SERVICE'S NOW, and it is the last line of this file
        // that named an engine. QwenTextGenerator.AllowMemoryMapping tells the text
        // generator how to load model weights; there is no generator in this process
        // to tell. Nothing was lost by deleting it: the property already defaults to
        // true (QwenTextGenerator.cs:241), so this line only restated the default -
        // for a type this app no longer loads.

        // Device-specific services the shared UI depends on. This is the seam that
        // lets one set of pages render on a phone and in a browser: the pages ask
        // these interfaces what is possible, and each head answers for itself.
        // ONE FILE FOR ALL OF THE APP'S STATE, beside the career database rather
        // than in SharedPreferences: state spread across four mechanisms is state
        // that cannot be backed up, moved to another phone, or restored after a
        // reinstall - which is why setting up again means setting up everything.
        builder.Services.AddSingleton(_ => new SqliteAppStore(
            System.IO.Path.Combine(FileSystem.AppDataDirectory, "CircleAI", "app.db")));

        // THE MEMORY IS THE SERVICE'S, and this app no longer holds a copy. It used
        // to build its own MemoryService on its own folder — which is how it came to
        // ship the inference engine: CircleAI.Memory reaches CircleAI.Embeddings
        // reaches CircleAI.Inference, and nothing in this file ever named any of
        // them. It is also the wrong shape on its own terms: what somebody told
        // their phone is ONE person's memory on ONE device, not a copy per app.
        // IRemembers is registered below, over the link.

        // NO MEMORY MANAGER AND NO DEVICE READER. Both were here so the setup path
        // could ask CanDownload before fetching a model, and so a storage screen
        // could show what Circle AI uses. This app downloads no model and holds no
        // footprint to show: the figures that mattered - what the models take, what
        // the voices take, what can be given back - are the service's, and they went
        // with MemoryManager to CircleAI.Assistant.Device.
        //
        // What is left of that job here is AppCache, which tidies THIS app's own
        // cache directory to the person's keep/cap. It needs no registration: it is
        // a static over one directory, called from launch and from memory pressure.

        builder.Services.AddSingleton<IFormFactor, DeviceFormFactor>();
        // SPEAKING MOVED TO THE SERVICE. DeviceVoiceHost catalogued TTS models,
        // chose one for the device and loaded it through BundleModelLoader; between
        // that and VoiceWiring's espeak setup, every app that wanted to talk carried
        // libespeak-ng.so, a 12 MB data zip, ONNX Runtime and a downloaded voice.
        // The service owns all of it, so this app asks for bytes and plays them.
        //
        // Playback stays HERE deliberately: an audio track is cheap and belongs to
        // the app somebody is looking at, which knows when to duck and when to stop.
        builder.Services.AddSingleton<IVoiceHost>(sp =>
            new CircleAI.Client.LinkedVoiceHost(
                (CircleAI.Client.LinkedBrain)sp.GetRequiredService<IBrain>()));
        builder.Services.AddSingleton<IDeviceFacts>(sp => new Services.ServiceDeviceFacts((CircleAI.Client.LinkedBrain)sp.GetRequiredService<IBrain>()));
        builder.Services.AddSingleton<IAppPairing, ServiceAppPairing>();
        builder.Services.AddSingleton<ISpokenLanguage, StoredSpokenLanguage>();
        // THE BRAIN IS NOT IN THIS APP ANY MORE. It was DeviceBrain, which loads a
        // model into this process — seconds and hundreds of megabytes, per app, on a
        // phone where fifteen apps cannot each hold a copy. LinkedBrain asks the
        // standalone CircleAI service over the authorized cross-app link instead.
        //
        // IBrain was already the seam, which is why this is one line: the web head has
        // run a non-local brain (BrowserBrain) for a while, so this is the third
        // implementation of an existing contract rather than a new abstraction.
        //
        // THE APP NOW REQUIRES THE SERVICE, and that is the intended shape — two store
        // listings, one of which owns the models. LinkedBrain.StateAsync distinguishes
        // "not installed" from "installed but not linked", because those need different
        // things from a person, and a screen that says only "not ready" leaves them
        // with no idea which action to take.
        builder.Services.AddSingleton<IBrain>(_ =>
            new CircleAI.Client.LinkedBrain(Android.App.Application.Context));

        // WHAT THE CIRCLE CAN BE ASKED TO DO. Services is the catalogue you
        // browse; this is the side that acts. One instance, because every voice
        // button consults it and two would be two answers to one sentence.
        // WHAT THE PHONE ITSELF CAN BE ASKED TO DO. Android's standard media
        // search intent, so whatever music app is installed answers it and no
        // app is named in code.
        builder.Services.AddSingleton<IPlaysMedia, AndroidMediaPlayer>();

        // MUSIC, WHICH NEEDS NOTHING INSTALLED. ProceduralMusicBedGenerator has
        // been in CircleAI.Music the whole time with exactly one consumer - the
        // native sample that is being retired - so without this registration the
        // capability leaves with it.
        builder.Services.AddSingleton<IMakesMusic, DeviceMusic>();

        // TRANSCRIPTS ARE KEPT, WHICH THIS HEAD NEVER DID.
        //
        // Transcribe produced a meeting and threw it away the moment you left
        // the screen: no IKeepsTranscripts was ever registered here, so
        // AsSubtitles had nothing to render and Find had half a corpus to search.
        // The native head kept them; FileTranscriptStore sits in
        // CircleAI.Assistant, beside the interface, and needs nothing but a folder.
        //
        // FilesDir, NOT the external directory the voices use. A transcript is
        // the most private thing this app produces - a meeting, a clinic
        // appointment, somebody's interview - and app-private storage is not
        // readable by other apps, is off the shared card, and goes on uninstall.
        builder.Services.AddSingleton<IKeepsTranscripts>(_ => new FileTranscriptStore(
            System.IO.Path.Combine(FileSystem.AppDataDirectory, "Transcripts")));

        // KEEPING THE PERSON IN THE LOOP WHILE IT ACTS. Anything that hands off
        // to another app, or acts on somebody else's behalf, says so out loud and
        // leaves it on the shade - one sentence per step, before the step. The
        // browser head has neither a voice nor a shade and gets AnnouncesNothing.
        builder.Services.AddSingleton<IAnnounces, Services.ServiceAnnouncer>();

        builder.Services.AddSingleton(sp => CapabilityRegistry.For(
            sp.GetService<IBrain>(), sp.GetService<ISettings>(), sp.GetService<IPlaysMedia>()));
        builder.Services.AddSingleton<ICareerInterview, CareerInterviewHost>();
        builder.Services.AddSingleton<IJobSpecTailor, JobSpecTailor>();
        // WAKE PHRASES ARE THE SERVICE'S, AND NOW THEY ARE SETTABLE FROM HERE. The
        // first version of this refused and told the person to "open CircleAI" - an
        // app with no launcher icon, so the advice could not be followed. The judging
        // still happens over there, because it reads the keyword spotter's own
        // tokeniser; what travels is the verdict.
        builder.Services.AddSingleton<IWakePhrases>(sp =>
            new CircleAI.Client.LinkedWakePhrases((CircleAI.Client.LinkedBrain)sp.GetRequiredService<IBrain>()));
        builder.Services.AddSingleton<IShareTarget, AndroidShareTarget>();
        builder.Services.AddSingleton<IWakeWord>(sp => new CircleAI.Client.LinkedWakeWord((CircleAI.Client.LinkedBrain)sp.GetRequiredService<IBrain>()));
        builder.Services.AddSingleton<ISettings, DeviceSettings>();
        // MAY THIS APP LISTEN. The assistant asks; this head knows how to get
        // the answer, because it is the thing with a screen. See IMicrophoneAccess.
        builder.Services.AddSingleton<IMicrophoneAccess, MauiMicrophoneAccess>();
        builder.Services.AddSingleton<ISetup>(sp => new Services.ServiceSetup((CircleAI.Client.LinkedBrain)sp.GetRequiredService<IBrain>()));
        builder.Services.AddSingleton<IConversation>(sp => new Services.ServiceConversation(
            (CircleAI.Client.LinkedBrain)sp.GetRequiredService<IBrain>(),
            sp.GetRequiredService<IRemembers>()));
        builder.Services.AddSingleton<IProfile, DeviceProfile>();
        // LISTENING RUNS IN THE SERVICE. DeviceResidentAssistant did it here — found
        // the bundle, installed the wake word, started the resident service, held the
        // microphone. That is the always-on half and it belongs to the app with the
        // foreground service and the notification that discloses the microphone.
        builder.Services.AddSingleton<IResidentAssistant>(sp =>
            new CircleAI.Client.LinkedResidentAssistant(
                (CircleAI.Client.LinkedBrain)sp.GetRequiredService<IBrain>()));

        // ONE MICROPHONE, SO ONE ANSWER TO WHAT IT IS DOING. Home's circle and the
        // middle of the tab bar are the same control offered twice, and each used to keep
        // its own copy of the phase - so a turn started from one left the other drawn
        // idle. Scoped rather than singleton: on the server head that is one per circuit,
        // and a singleton would show every visitor whoever spoke last.
        builder.Services.AddScoped<VoiceMark>();

        // THE MEMORY LOOP, CLOSED, AND NOW ACROSS THE LINK. LearnAsync has been
        // called on every utterance for a long time and RecallAsync from nowhere but
        // a diagnostic screen, so the phone accumulated everything anybody said and
        // could not tell them their own name the next morning. The store is the
        // service's; LinkedMemory is this app's end of the recall / remember verbs,
        // which have been on the wire behind LinkScope.Memory the whole time.
        builder.Services.AddSingleton<IRemembers>(sp =>
            new CircleAI.Client.LinkedMemory(
                (CircleAI.Client.LinkedBrain)sp.GetRequiredService<IBrain>()));

        // ONE OWNER FOR THE CONVERSATION. Registered after IRemembers so the
        // effects get the real memory rather than the do-nothing fallback.
        builder.Services.AddConversationStore();

        // WHAT IS ACTUALLY WIRED, as opposed to what is offered. The setup census
        // counts downloads; this asks the runtime hooks and the real speech path
        // whether they work. Scoped, because it holds no state worth sharing.
        builder.Services.AddScoped<IWiringProbe>(sp => new Services.ServiceWiringProbe((CircleAI.Client.LinkedBrain)sp.GetRequiredService<IBrain>()));

        // WHERE THE PHONE IS, WHICH IS NOT WHERE ITS OWNER IS FROM. The
        // interpreter needs both: your language, and the language of the
        // people around you.
        builder.Services.AddSingleton<IWhereAmI, DeviceWhereAmI>();

        // THE CROSS-APP LINK IS HOSTED ELSEWHERE NOW. This block used to wire
        // CircleNeuronLinkService's grant store, first-party set and memory, because
        // this app WAS the host — LinkIpc.HostPackage literally named it, so the
        // shared brain on a device was something a person got by installing a sample.
        // All of it moved to samples/CircleAIService, which is a separate
        // store listing that owns the models.
        //
        // This app is now one of its clients, and asks through LinkedBrain above.


        // -- SELF-HEALING AND THE MODEL CATALOGUE ARE THE SERVICE'S --------------
        // Both blocks that stood here are gone, and they are the clearest example of
        // what thin costs and what it buys. The self-heal loop diagnoses a failure
        // with the brain and fixes it by freeing the MODEL caches; the catalogue
        // decides which model this device can run and downloads it. Neither means
        // anything in a process that holds no model - and wiring them here is what
        // referenced CircleAI.Hosting, and behind it the whole inference engine.
        //
        // THE SCREEN STAYS. ServiceHealing renders Wolverine and says where the
        // watching happens, rather than showing an empty dashboard - "healed: 0,
        // needs you: 0" reads as "nothing has gone wrong" when the truth is
        // "nothing here is watching".
        builder.Services.AddSingleton<IHealingView>(sp =>
            new Services.ServiceHealing((CircleAI.Client.LinkedBrain)sp.GetRequiredService<IBrain>()));

        builder.Services.AddMauiBlazorWebView();

#if DEBUG
        // Lets the web view be inspected from Chrome's remote devtools, which is
        // the only way to see a Blazor error on a phone: a component that throws
        // during render leaves a BLANK PAGE and writes nothing to logcat.
        builder.Services.AddBlazorWebViewDeveloperTools();

        // NO ILogger PROVIDER IS ADDED HERE, and that is deliberate rather than an
        // omission. AddDebug() reaches nothing on Android - every warning goes
        // invisible while the radios stay audible in logcat - and AddConsole()
        // needs a package this head does not reference, which is how a build that
        // only ever ran in Release compiled a Debug block nobody had tried.
        // Platform logging is what actually arrives; use logcat.
#endif

        var app = builder.Build();

        // -- FAILURE HOOKS, WITHOUT A HEALER ------------------------------------
        // The funnels stay wired, because losing a failure silently is worse than not
        // fixing it: every screen's catch block goes through Trouble.Say, and the
        // runtime's two channels are where a failure nobody caught turns up. They go
        // to logcat here rather than into a loop, because the loop is in CircleAI.
        //
        // WHAT IS LOST BY THAT, PLAINLY: this app's own failures no longer reach
        // Wolverine. Closing it means a healing verb on the link - a protocol change
        // with its own scope, not something to paper over here.
        Trouble.Observer = ex => Note(ex, "app");

        System.AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is System.Exception ex) Note(ex, "unhandled");
        };

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Note(e.Exception, "background-task");
            e.SetObserved();   // recorded - don't let it tear the process down.
        };

        // NO CATALOGUE BOOTSTRAP. Seeding a model ladder, assessing it against this
        // device, and refreshing it from ModelScope and HuggingFace all belong to the
        // process that downloads and loads models. CircleAI does that; this app asks
        // it questions.

        return app;
    }

    // Write a failure down where it can be read, and never let the writing itself
    // throw - a reporter that failed must not add a failure of its own.
    private static void Note(System.Exception exception, string source)
    {
        try
        {
            Android.Util.Log.Error("CircleAI.Trouble",
                $"{source}: {exception.GetType().Name}: {exception.Message}");
        }
        catch { /* reporting must never surface its own failure */ }
    }
}