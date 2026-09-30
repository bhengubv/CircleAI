// ServiceApplication.cs
//
// The standalone CircleAI service, bootstrapped.
//
// EVERYTHING HERE WAS IN THE HYBRID SAMPLE'S MauiProgram, which is the thing this
// app exists to undo. LinkIpc.HostPackage named the sample, so "the shared brain on
// this device" was something a person got by installing a demo — and any second app
// that wanted it had to hope the demo was still there. The host is now an app of its
// own, and the sample is one of its clients.
//
// WHAT AN APPLICATION CLASS IS FOR HERE: the link service can be bound by another app
// before anybody opens this one, and OnBind runs with whatever statics were set at
// process start. Wiring in an Activity would mean the first bind after a cold start
// found no grant store and no memory — so it happens here, before any component runs.

using Android.App;
using Android.Runtime;
using CircleAI.Core;
using CircleAI.Device;
using CircleAI.Linking;
using CircleAI.Assistant.Device;
using CircleAI.Memory;

namespace CircleAI.Service.Android;

// NO ICON SET, which is a gap and not a choice worth defending: this ships with
// Android's default launcher icon. A store listing needs a real one, and inventing
// artwork here would be worse than leaving the hole visible.
[Application(Label = "CircleAI")]
public sealed class ServiceApplication : Application
{
    public ServiceApplication(IntPtr handle, JniHandleOwnership transfer)
        : base(handle, transfer) { }

    public override void OnCreate()
    {
        // MEASURE THE DEVICE BEFORE ANYTHING ASKS WHAT FITS. DeviceProbe reads the
        // GC heap limit on Android — about 100 MB inside the sandbox — unless a head
        // installs the real hook, so every model-fit decision would be made against a
        // fiction. It has to run before base.OnCreate, because base can start
        // components that probe.
        AndroidDeviceMemory.Install(this);

        base.OnCreate();

        // WHERE STANDING GRANTS LIVE. A killed service process must not forget who
        // the person already approved: an in-memory store is safe but asks again on
        // every restart, which trains people to tap through consent without reading.
        var grants = System.IO.Path.Combine(FilesDir!.AbsolutePath, "link-grants.json");
        CircleNeuronLinkService.Grants = new FileLinkGrantStore(grants);

        // NOBODY IS TRUSTED BY DEFAULT. An empty first-party set means every client,
        // including our own sample, is approved once by the person with device auth
        // rather than waved through on a signature. Populating this is a deliberate
        // decision about which packages ship as "ours", not a convenience.
        CircleNeuronLinkService.FirstPartySignatures = new HashSet<string>(StringComparer.Ordinal);

        // THE PERSON'S MEMORY, AND THE RECALL / REMEMBER VERBS WERE DEAD WITHOUT IT.
        // CircleNeuronLinkService.Memory defaults to null, and null is answered with
        // "memory not available" - so the two verbs that have been on the wire the
        // whole time returned a failure on every call, and the only reason nobody saw
        // it is that the one client kept its own store and never asked.
        //
        // ONE STORE FOR THE DEVICE, which is the whole point of putting it here: the
        // service's folder, every linked app reading and writing the same atoms, and
        // uninstalling a client does not take somebody's name with it.
        //
        // Built eagerly at process start rather than on first bind: a bind can arrive
        // from another app before anything in this one has run, and opening SQLite
        // inside a transaction the caller is blocked on is the wrong place for it.
        CircleNeuronLinkService.Memory = new MemoryService(
            System.IO.Path.Combine(FilesDir!.AbsolutePath, "CircleAI", "memory"));

        // THE PHONEMISER, OR THIS PROCESS CAN HEAR AND THINK BUT NEVER SPEAK. Every
        // voice trained on espeak IPA needs it - which is every language here except
        // Japanese - and without this call CircleAISpeaker.MobilePhonemizerFactory
        // stays null and synthesis refuses with "on-device phonemizer not wired".
        //
        // The client used to make this call and no longer can: it ships neither the
        // library nor the 11.9 MB of data. Both moved here with the synthesis, and
        // espeak-ng stays OUT OF PROCESS - it is GPL-3.0, and invoking it rather
        // than linking it is what keeps it off this app's licence.
        // global:: ON EVERY Android.* CALL IN THIS FILE, AND IT IS NOT DECORATION.
        // This assembly's namespace is CircleAI.Service.Android, which SHADOWS the
        // platform's own Android namespace from inside its own files: a bare
        // Android.Util.Log resolves to CircleAI.Service.Android.Util.Log, which does
        // not exist, and the compiler reports it as "the type or namespace name
        // 'Util' does not exist" - which reads as a missing assembly reference and is
        // nothing of the sort. CircleAI.Assistant.Device is named .Device rather than
        // .Android for exactly this reason; this app cannot be, so it pays with the
        // qualifier.
        try
        {
            VoiceWiring.Install(this);
        }
        catch (Exception ex)
        {
            global::Android.Util.Log.Warn("CircleAI.Voice", "phonemiser not wired: " + ex.Message);
        }

        // WHERE A SIDELOADED VOICE AND THE JAPANESE DICTIONARY ARE FOUND. Three
        // statics that the client used to set against ITS external files directory,
        // which was the wrong process: the code that reads them is here. The
        // external directory, because that is where adb or a file manager can put
        // something without root - and the importer still checks every byte against
        // the catalogue's hash, so this is a delivery route and not a trust hole.
        var sideload = GetExternalFilesDir(null)?.AbsolutePath;
        CircleAI.Assistant.Voice.CircleAISpeaker.SideloadFolder = sideload;
        CircleAI.Voice.OpenJTalkPhonemizer.DictionaryFolder = sideload;
        CircleAI.Voice.OpenJTalkPhonemizer.ModelStoreFolder = ModelStore.Path;

        // Managed voice logging reaches nothing through ILogger on Android, so it
        // goes to logcat directly - the one place it is actually readable.
        CircleAI.Voice.VoiceTrace.Sink = line => global::Android.Util.Log.Info("CircleAI.Voice", line);

        // SPEECH FOR THE LINK, which has been refused on every call until now.
        // CircleNeuronLinkService.Speech defaults to null and null answers "speech
        // not available", so the audio transaction - the codec, the 800 KB budget,
        // the client end - was complete and dead. This is the recogniser and the
        // voice it needed.
        CircleNeuronLinkService.Speech = new ServiceSpeech();

        // SETTING THIS APP UP, FROM AN APP THAT HAS A SCREEN. CircleNeuronLinkService
        // .Setup defaults to null and null answers "setup not available", so the five
        // setup verbs were refused - which left the models unreachable by anybody:
        // this app cannot ask for them (no UI) and the client could not either.
        // DeviceSetup is the real thing; it plans, fetches and reports.
        // The microphone state is read from the platform (this app cannot prompt for
        // it - no screens), and the footprint gate is the same MemoryManager the
        // storage census uses, so "can I fetch this" is answered against the real disk.
        CircleNeuronLinkService.Setup = new DeviceSetup(
            new DeviceMicrophoneAccess(this),
            new MemoryManager(new DeviceResourcesReader()));

        // THE BRAIN ITSELF, AND NOTHING HAS EVER ASKED FOR ONE. CircleNeuronService
        // hosts a NeuronNode only when a host sets OptionsFactory; left null it runs,
        // holds the microphone, and reports "listening only — no brain was asked for".
        // That property had ZERO setters in the repo, and its own comment explains why
        // it was reasonable: the hybrid app used to host its brain in its OWN process
        // and wanted this service purely to keep the mic open with the screen off.
        //
        // That world is gone. The app holds no model now, so if this app does not ask
        // for a brain, nothing on the device has one - which is exactly what happened:
        // every question came back "brain warming up, try again shortly" for ever,
        // because Node was null and always would be.
        //
        // WarmOnStart pays the cold start once, up front, rather than on the first
        // question somebody asks. On a P30 that is thirteen to twenty-three seconds,
        // and it is far better spent while the notification says "loading" than while
        // a person waits on a blank bubble.
        CircleNeuronService.OptionsFactory = () => new CircleAI.Hosting.AIOptions
        {
            NativeLibDir          = ApplicationInfo?.NativeLibraryDir,
            ModelStorageDirectory = ModelStore.Path,
            WarmOnStart           = true,
        };

        // WHAT THE PHONE ANSWERS TO. DeviceWakePhrases keeps the chosen phrase in this
        // app's own store and judges a new one against the KWS bundle's tokeniser -
        // both of which are here, which is exactly why a client could only ever be
        // told to go elsewhere. Wired, it can be set from any approved app.
        CircleNeuronLinkService.WakePhrases = new DeviceWakePhrases(
            new SqliteAppStore(System.IO.Path.Combine(FilesDir!.AbsolutePath, "CircleAI", "app.db")));

        // LISTENING, AND WHAT THIS DEVICE HOLDS. The last two rows in the client's
        // settings that could only report: "Answer to its name" pointed at an app with
        // no launcher icon, and the abilities read "Nothing for this yet" because the
        // client has no models to count. Both facts are here, so both are served here.
        var footprint = new MemoryManager(new DeviceResourcesReader());
        CircleNeuronLinkService.Resident = new ResidentListeningControl(this);
        CircleNeuronLinkService.Facts    = new DeviceFacts(footprint);

        // AND THE SKILL LIBRARY, off the UI thread: the first call unpacks 20 MB out
        // of the APK, and OnCreate runs before any component does. Nothing needs it
        // until a turn, and a bind that arrives first will simply find it a moment
        // later.
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var skills = SkillLibrary.Open(this);
                if (skills is not null) CircleNeuronLinkService.Skills = skills;
                CircleAI.Assistant.CircleAISession.Library = skills;
            }
            catch (Exception ex)
            {
                global::Android.Util.Log.Warn("CircleAI.Skills", "skill library: " + ex.Message);
            }
        });

        // No auth gate on the SERVICE: a background service cannot raise a biometric
        // sheet. Approval happens in LinkConsentActivity, launched FOR RESULT by the
        // foreground client, which is also how the OS tells the consent screen which
        // package is really asking.
    }
}
