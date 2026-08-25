// DeviceSetup.cs
//
// First run on this phone, over the same FirstRun planner the native head uses.
//
// THE PLAN IS NOT REIMPLEMENTED. FirstRun.Plan decides what to fetch and in what
// order, and every one of those decisions was argued: two voices by NAME rather
// than by fit, because asking for "a TTS model that fits" once put a Nepali voice
// in an English assistant's mouth; anything already on disk skipped, so an
// interrupted setup resumes rather than restarting; a phone too small for a chat
// model still gets a voice and ears rather than being refused outright.

using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;

namespace CircleAI.Samples.It.App.Services;

/// <inheritdoc />
public sealed class DeviceSetup : ISetup
{
    private static string StorageDir
        => Path.Combine(FileSystem.AppDataDirectory, "CircleAI", "Models");

    /// <inheritdoc />
    public Task<Readiness> ReadinessAsync(CancellationToken ct = default)
        => Task.Run(() =>
        {
            using var registry = new ModelRegistryService();
            using var loader = new BundleModelLoader(StorageDir, registry);

            bool Has(ModelModality m) => registry.AllModels
                .Any(e => e.Modality == m && loader.ModelExists(e.Name));

            var voice = Has(ModelModality.Tts);
            var ears = Has(ModelModality.Asr);
            var brain = Has(ModelModality.Chat);
            var anything = voice || ears || brain;

            // The same wording the native screen uses, kept here rather than in
            // the page: it is a description of a device, not of a layout.
            if (!anything)
                return new Readiness(ReadyStage.NeedsSetup, "Let's set it up",
                    "It needs a few things first. Tap to start.", CanTalk: false);

            const string lead = "Tap and talk";

            // CAN TALK BEFORE IT CAN THINK. As soon as it can hear and speak,
            // pressing the circle does something useful even while the brain is
            // still arriving.
            if (voice && ears && !brain)
                return new Readiness(ReadyStage.CanListen, lead,
                    "Still waking up — the first answer may take a moment.", CanTalk: true);

            if (voice && ears && brain)
                return new Readiness(ReadyStage.Ready, lead, "", CanTalk: true);

            if (voice && !ears)
                return new Readiness(ReadyStage.Waking, "Getting ready",
                    "You can talk to it in a moment.", CanTalk: false);

            return new Readiness(ReadyStage.Waking, "Getting ready", "", CanTalk: false);
        }, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<SetupItem>> PlanAsync(CancellationToken ct = default)
        => Task.Run<IReadOnlyList<SetupItem>>(() =>
        {
            using var registry = new ModelRegistryService();
            using var loader = new BundleModelLoader(StorageDir, registry);

            // speech: true, because this head compiles the voice stack in. The
            // chat-only APK passes false so setup does not spend somebody's data
            // on 140 MB no line of that binary can open.
            return FirstRun.Plan(registry, loader, DeviceProbe.Snapshot(), speech: true)
                .Select(s => new SetupItem(s.Title, s.Model.TotalBytes))
                .ToList();
        }, ct);

    /// <inheritdoc />
    public Task RunAsync(IProgress<SetupProgressReport> progress, CancellationToken ct = default)
        => Task.Run(async () =>
        {
            using var registry = new ModelRegistryService();
            using var loader = new BundleModelLoader(StorageDir, registry);
            var steps = FirstRun.Plan(registry, loader, DeviceProbe.Snapshot(), speech: true);

            var inner = new Progress<SetupProgress>(p => progress.Report(
                new SetupProgressReport(p.Index, p.Count, p.Title, p.Fraction, p.Remaining)));

            await FirstRun.RunAsync(loader, steps, inner, ct).ConfigureAwait(false);
        }, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<TourStep>> TourAsync(
        TimeSpan remaining, CancellationToken ct = default)
        => Task.Run<IReadOnlyList<TourStep>>(() =>
        {
            // UNDER TWO MINUTES IS NOT WORTH INTERRUPTING. On a fast link setup
            // barely appears, and a tour that flashes up and vanishes is noise.
            if (remaining < TimeSpan.FromMinutes(2)) return [];

            using var registry = new ModelRegistryService();
            using var loader = new BundleModelLoader(StorageDir, registry);

            bool Has(ModelModality m) => registry.AllModels
                .Any(e => e.Modality == m && loader.ModelExists(e.Name));

            var steps = new List<TourStep>();
            var budget = remaining.TotalSeconds;

            void Offer(string title, string body, string? action, string? route, int seconds)
            {
                if (seconds > budget) return;
                steps.Add(new TourStep(title, body, action, route));
                budget -= seconds;
            }

            if (Has(ModelModality.Tts))
                Offer("Your language",
                      "Hear it speak, and pick the one you want to be answered in.",
                      "Choose a language", "languages", 90);

            Offer("Let it hear you",
                  "The microphone is only used when you talk to it. Nothing is recorded.",
                  "Allow the microphone", "wake", 20);

            if (Has(ModelModality.WakeWord))
                Offer("Say “Hey B”",
                      "Wake it without touching the phone. Try it now and see it light up.",
                      "Try the wake word", "wake", 60);

            // THE STEP THAT DECIDES WHETHER ANY OF THE REST SURVIVES. Huawei,
            // Xiaomi, Oppo and Vivo all kill foreground services on their own
            // schedule whatever Android says, and only the owner can exempt an
            // app. Skip it and the assistant goes deaf an hour later for reasons
            // nobody can see - the phone will not say it did it.
            Offer("Keep it awake",
                  "This phone stops apps in the background. Allowing it to run means "
                + "it can still hear you later.",
                  "Allow it to run", "settings", 40);

            Offer("Build your CV while you wait",
                  "Answer a few questions and it writes one for you.",
                  "Start my CV", "career", 120);

            return steps;
        }, ct);
}
