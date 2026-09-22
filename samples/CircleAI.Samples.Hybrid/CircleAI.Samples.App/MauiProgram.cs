// The MAUI head's composition root.
//
// Registers this device's answers to the shared UI's questions and hands it a
// BlazorWebView to render in.

using CircleAI.Assistant;
using CircleAI.Assistant.Device;
using CircleAI.Assistant.Voice;   // FileTranscriptStore
using Microsoft.Extensions.Logging;
using CircleAI.Memory;

namespace CircleAI.Samples.App;

/// <summary>Builds the app.</summary>
public static class MauiProgram
{
    /// <summary>Compose and return the app.</summary>
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        // EVERYTHING ON BY DEFAULT (product-owner directive). Memory-map model
        // weights and the KV/prefix cache from first launch — the documented host
        // hook the runtime leaves for exactly this. It unlocks two things at once:
        //   • MoE bundles (Qwen3-30B-A3B etc.) become loadable, because only the
        //     active experts stay resident while the kernel pages the rest off
        //     disk — the "punch above our weight" the catalogue already selects for;
        //     the DeviceModelAssessor reads this same flag, so its compatible bit
        //     matches what will actually load.
        //   • the cross-session prefix cache (short-term memory), which MNN refuses
        //     to attach without kvcache_mmap — until now every chat re-paid the
        //     ~13 s system-prompt prefill.
        // ON THE RECORD: mmap was disabled after an MNN SIGSEGV killed two builds
        // mid-answer. It is on now by directive; a recurrence surfaces in the
        // self-heal log (Wolverine) instead of vanishing — which is what that loop
        // is for.
        CircleAI.Inference.QwenTextGenerator.AllowMemoryMapping = true;

        // Device-specific services the shared UI depends on. This is the seam that
        // lets one set of pages render on a phone and in a browser: the pages ask
        // these interfaces what is possible, and each head answers for itself.
        // ONE FILE FOR ALL OF THE APP'S STATE, beside the career database rather
        // than in SharedPreferences: state spread across four mechanisms is state
        // that cannot be backed up, moved to another phone, or restored after a
        // reinstall - which is why setting up again means setting up everything.
        builder.Services.AddSingleton(_ => new SqliteAppStore(
            System.IO.Path.Combine(FileSystem.AppDataDirectory, "CircleAI", "app.db")));

        // THE MEMORY THE APP HOLDS. One for the life of the process, on the
        // app's own storage, beside everything else it keeps. It is not a
        // feature of a screen - it is what the app knows, and anything that
        // wants to ask or tell it takes IMemoryService.
        //
        // Nothing here is held back for a lifecycle callback: a force-stop
        // never calls one, and on a phone that is how an app usually ends.
        // ONE memory instance for the whole process, shared by the app AND the
        // cross-app link (wired below), so a linked app recalls and writes the
        // person's REAL memory rather than a second store racing on the same folder.
        var appMemory = new MemoryService(
            System.IO.Path.Combine(FileSystem.AppDataDirectory, "CircleAI", "memory"));
        builder.Services.AddSingleton<IMemoryService>(appMemory);

        // THE MEMORY MANAGER. Step zero (the real device reader) plus the facade
        // that ties it to the footprint and the pure budget. Registered so the
        // setup path can ask CanDownload before a fetch - prevention, not cure -
        // and so a storage screen can show what Circle AI uses. Reads the whole
        // device, acts only on Circle AI's own footprint.
        builder.Services.AddSingleton<CircleAI.Assistant.IDeviceResources,
                                      CircleAI.Assistant.Device.DeviceResourcesReader>();
        builder.Services.AddSingleton<CircleAI.Assistant.Device.MemoryManager>();

        builder.Services.AddSingleton<IFormFactor, DeviceFormFactor>();
        builder.Services.AddSingleton<IVoiceHost, DeviceVoiceHost>();
        builder.Services.AddSingleton<IDeviceFacts, DeviceFacts>();
        builder.Services.AddSingleton<ISpokenLanguage, StoredSpokenLanguage>();
        // One brain for the app, shared by the chat screen and the job-spec
        // tailoring: loading a model is seconds and hundreds of megabytes.
        builder.Services.AddSingleton<IBrain, DeviceBrain>();

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
        // The native head kept them; FileTranscriptStore has been in
        // CircleAI.Assistant.Runtime the whole time with one consumer.
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
        builder.Services.AddSingleton<IAnnounces, AndroidAnnouncer>();

        builder.Services.AddSingleton(sp => CapabilityRegistry.For(
            sp.GetService<IBrain>(), sp.GetService<ISettings>(), sp.GetService<IPlaysMedia>()));
        builder.Services.AddSingleton<ICareerInterview, CareerInterviewHost>();
        builder.Services.AddSingleton<IJobSpecTailor, JobSpecTailor>();
        builder.Services.AddSingleton<IWakePhrases, DeviceWakePhrases>();
        builder.Services.AddSingleton<IShareTarget, AndroidShareTarget>();
        builder.Services.AddSingleton<IWakeWord, DeviceWakeWord>();
        builder.Services.AddSingleton<ISettings, DeviceSettings>();
        // MAY THIS APP LISTEN. The assistant asks; this head knows how to get
        // the answer, because it is the thing with a screen. See IMicrophoneAccess.
        builder.Services.AddSingleton<IMicrophoneAccess, MauiMicrophoneAccess>();
        builder.Services.AddSingleton<ISetup, DeviceSetup>();
        builder.Services.AddSingleton<IConversation, DeviceConversation>();
        builder.Services.AddSingleton<IProfile, DeviceProfile>();
        builder.Services.AddSingleton<IResidentAssistant, DeviceResidentAssistant>();

        // ONE MICROPHONE, SO ONE ANSWER TO WHAT IT IS DOING. Home's circle and the
        // middle of the tab bar are the same control offered twice, and each used to keep
        // its own copy of the phase - so a turn started from one left the other drawn
        // idle. Scoped rather than singleton: on the server head that is one per circuit,
        // and a singleton would show every visitor whoever spoke last.
        builder.Services.AddScoped<VoiceMark>();

        // THE MEMORY LOOP, CLOSED. LearnAsync has been called on every utterance
        // for a long time and RecallAsync from nowhere but a diagnostic screen,
        // so the phone accumulated everything anybody said and could not tell
        // them their own name the next morning. DeviceMemory is the real store
        // behind the shared contract; the store's effects are what read it back.
        builder.Services.AddSingleton<IRemembers, DeviceMemory>();

        // ONE OWNER FOR THE CONVERSATION. Registered after IRemembers so the
        // effects get the real memory rather than the do-nothing fallback.
        builder.Services.AddConversationStore();

        // WHAT IS ACTUALLY WIRED, as opposed to what is offered. The setup census
        // counts downloads; this asks the runtime hooks and the real speech path
        // whether they work. Scoped, because it holds no state worth sharing.
        builder.Services.AddScoped<IWiringProbe, DeviceWiringProbe>();

        // WHERE THE PHONE IS, WHICH IS NOT WHERE ITS OWNER IS FROM. The
        // interpreter needs both: your language, and the language of the
        // people around you.
        builder.Services.AddSingleton<IWhereAmI, DeviceWhereAmI>();

        // THE CROSS-APP LINK. Another app can bind CircleNeuronLinkService to use
        // this device's one shared brain; these hooks are how it is authorized.
        // No caller is trusted by default (no first-party signatures wired here),
        // so every linking app is approved once with device auth — biometric / PIN
        // / pattern — and remembered. The grant store is in-memory for now: a
        // killed service process simply asks again, which is safe if less handy.
        var grantsPath = System.IO.Path.Combine(
            Microsoft.Maui.Storage.FileSystem.AppDataDirectory, "link-grants.json");
        CircleAI.Device.CircleNeuronLinkService.Grants =
            new CircleAI.Linking.FileLinkGrantStore(grantsPath);
        CircleAI.Device.CircleNeuronLinkService.FirstPartySignatures =
            new HashSet<string>(StringComparer.Ordinal);
        // No auth gate is wired onto the SERVICE: a background service cannot show a
        // biometric sheet, so approval happens in LinkConsentActivity (launched by
        // the foreground client), which mints the grant this store then persists.

        // THE MEMORY VERBS SERVE THE SAME STORE THE APP USES. A linked app that
        // holds a Memory grant recalls and writes the person's real memory through
        // this one instance. Skills and discovery need no wiring — the service falls
        // back to the built-in consumer pack and the embedded capability manifest.
        CircleAI.Device.CircleNeuronLinkService.Memory = appMemory;

        // ── SELF-HEALING ────────────────────────────────────────────────────────
        // The app heals its own failures: a caught failure → diagnose (reusing the ONE
        // resident brain, never a second model) → run a safe, reversible fix → escalate
        // the rest. The loop's logic lives in the product; here we only wire it and give
        // it the platform fixes. The Wolverine dashboard reads it through IHealingView.
        var healingLog = new CircleAI.Hosting.SelfHealing.SqliteHealingLog(
            "Data Source=" + System.IO.Path.Combine(FileSystem.AppDataDirectory, "CircleAI", "healing.db"));
        builder.Services.AddSingleton<CircleAI.Hosting.SelfHealing.IHealingLog>(healingLog);

        // The analyst rides the app's resident IBrain rather than a second IAIService,
        // folding its authoritative instruction into one prompt.
        builder.Services.AddSingleton<CircleAI.Hosting.SelfHealing.IFailureAnalyst>(sp =>
        {
            var brain = sp.GetRequiredService<IBrain>();
            return new CircleAI.Hosting.SelfHealing.FailureAnalyst(
                (system, user, ct) => brain.AskAsync($"{system}\n\n{user}", token: null, ct));
        });

        builder.Services.AddSingleton<CircleAI.Hosting.SelfHealing.ISelfHealPolicy,
                                      CircleAI.Assistant.Device.DeviceSelfHealPolicy>();

        // The safe, reversible remedies the loop may run on its own (v1: free the
        // regenerable caches — boundaried, never code / money / security).
        builder.Services.AddSingleton<CircleAI.Hosting.SelfHealing.ISafeFix>(sp =>
            new CircleAI.Assistant.Device.MemoryManagerSafeFix(
                sp.GetRequiredService<CircleAI.Assistant.Device.MemoryManager>(), "cache", reclaimAll: false));
        builder.Services.AddSingleton<CircleAI.Hosting.SelfHealing.ISafeFix>(sp =>
            new CircleAI.Assistant.Device.MemoryManagerSafeFix(
                sp.GetRequiredService<CircleAI.Assistant.Device.MemoryManager>(), "memory", reclaimAll: true));

        builder.Services.AddSingleton<CircleAI.Hosting.SelfHealing.ISelfHealer,
                                      CircleAI.Hosting.SelfHealing.SelfHealer>();
        builder.Services.AddSingleton<IHealingView, CircleAI.Assistant.Device.DeviceHealing>();

        // ── MODEL CATALOGUE ─────────────────────────────────────────────────────
        // The runtime SQLite catalogue is the single source of truth for model
        // selection: a signed feed adds/re-pins rows, the device re-assesses them,
        // the app reads the best it can run — no app release. This build ships the
        // MNN engine only, so a GGUF model stays compatible=0 until one is added.
        // The Wolverine bridge (wired via the ISelfHealer registered above) surfaces
        // a refused feed or a "can run nothing" device into the self-heal log. The
        // live IModelSelector is NOT swapped yet — that flip waits for the on-device
        // demo (useAsPrimarySelector defaults false).
        CircleAI.Hosting.ModelCatalogueServiceCollectionExtensions.AddModelCatalogue(
            builder.Services,
            System.IO.Path.Combine(FileSystem.AppDataDirectory, "CircleAI", "models.db"),
            new[] { CircleAI.Core.ModelEngine.Mnn },
            useAsPrimarySelector: true);   // the catalogue now drives model choice per device

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

        // ── SELF-HEALING HOOKS ──────────────────────────────────────────────────
        // Every screen already funnels caught failures through Trouble.Say — point it
        // at the loop. Unhandled managed failures come through the runtime's channels.
        // The healer is resolved lazily so a failure during startup still finds it.
        Trouble.Observer = ex => Heal(app, ex, "app");

        System.AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is System.Exception ex) Heal(app, ex, "unhandled");
        };

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Heal(app, e.Exception, "background-task");
            e.SetObserved();   // recorded — don't let it tear the process down.
        };

        // ── MODEL CATALOGUE BOOTSTRAP ───────────────────────────────────────────
        // Seed the catalogue on first run and assess it against this device, so the
        // ladder exists offline and the self-heal log sees what can run here. Never
        // fails the app; a heuristic (unmeasured) probe stays quiet rather than cry
        // "can run nothing" on a guessed RAM figure.
        try
        {
            CircleAI.Inference.CatalogueBootstrap.Run(
                app.Services.GetRequiredService<CircleAI.Core.Models.IModelCatalog>(),
                app.Services.GetRequiredService<CircleAI.Inference.IModelAssessor>(),
                CircleAI.Core.DeviceProbe.Snapshot(),
                app.Services.GetService<CircleAI.Core.Models.IModelCatalogObserver>());
        }
        catch { /* a catalogue bootstrap hiccup must not stop the app starting */ }

        // Keep the catalogue current from ANY connection, and make its own failures
        // VISIBLE: route catalogue diagnostics (a failed refresh, a sink that threw)
        // into the self-heal loop so Wolverine shows them rather than a silent
        // "quietly doing nothing". Attach the sink so an internet refresh AND an
        // aethernet offer both flow in, then kick BOTH internet hosts (ModelScope +
        // HuggingFace; each self-throttles, offline is a no-op).
        CircleAI.Core.Models.ModelCatalogue.Diagnostics = (message, error) =>
        {
            if (error is not null) Heal(app, error, "model-catalogue");
        };
        try
        {
            app.Services.GetRequiredService<CircleAI.Inference.CatalogueUpdater>().Attach();
            CircleAI.Core.Models.ModelCatalogue.RefreshInBackground();
            _ = app.Services.GetRequiredService<CircleAI.Core.Models.HuggingFaceCatalogClient>().RefreshAsync();
        }
        catch (System.Exception ex) { Heal(app, ex, "model-catalogue-startup"); }

        return app;
    }

    // Route a failure into the self-heal loop, resolved lazily, and never let the
    // routing itself throw — a healer that failed must not add a failure of its own.
    private static void Heal(MauiApp app, System.Exception exception, string source)
    {
        try { app.Services.GetRequiredService<CircleAI.Hosting.SelfHealing.ISelfHealer>().Heal(exception, source); }
        catch { /* healing must never surface its own failure */ }
    }
}
