// WakeWordStartsWhenItsBundleLandsTests.cs
//
// THE MICROPHONE CAME FIRST, AND THE WAKE WORD DIED OF IT. On the P30's fresh install
// on 2026-10-23 the phone asked for Circle AI's microphone thirteen seconds into setup
// and the owner said yes. That started the wake word at once - on a bundle still
// arriving:
//
//   21:36:35.851  CircleAI.Wake: listening, keywords=built-in
//   21:36:38.174  CircleAI.ScreenWake: wake loop failed: FileNotFoundException ...
//                 KWS-Zipformer-HeyB/kws-hey-b/keywords.txt
//   21:36:44.080  CircleAI.Fetch: KWS-Zipformer-HeyB complete
//
// and nothing started it again when the bundle finished.
//
// THE FIRST FIX FOR THAT WAS GUARDED BY SEARCHING THE SOURCE FOR STRINGS, and the
// review of 2026-10-23 found what that cost: the "a model landed" callback was handed
// to the two loops in ModelSetup that the background fetch service drives, and the
// repo has three other pull paths - setup started from the app, the abilities row's
// Turn on, and anything calling the loader directly. A bundle landed by one of those
// still started nothing. Meanwhile "is this bundle downloaded" had THREE owners that
// disagreed, and the state between them was unrecoverable.
//
// Both halves are now one owner each, and both are TESTED BY VALUE rather than by
// text:
//
//   BundleModelLoader.ModelPresent   every catalogued file of a bundle with no
//                                    weight-file anchor (the trap: the encoder is
//                                    4.8 of 6.76 MB and lands FIRST)
//   InFlightDownloads                "it landed" fires once, for the caller that did
//                                    the work, and never for a joiner
//
// The Android wiring - which object subscribes, and what it does - is in files the
// test legs do not compile, so that part stays a source guard.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public sealed class WakeWordStartsWhenItsBundleLandsTests
{
    private const string Kws = "KWS-Zipformer-HeyB";

    // ── "is this bundle downloaded": one owner, every file ───────────────────

    private sealed class Store : IDisposable
    {
        public string Root { get; } = Path.Combine(
            Path.GetTempPath(), "circleai-kws-" + Guid.NewGuid().ToString("N"));

        public Store() => Directory.CreateDirectory(Root);

        /// <summary>Writes the catalogued files, in catalogue order, up to <paramref name="howMany"/>.</summary>
        /// <remarks>
        /// IN ORDER, BECAUSE THE ORDER IS THE DEFECT. EnsureBundleCoreAsync fetches one
        /// file at a time, each written as name.tmp and renamed when it finishes - so
        /// "the first N files are here and the rest are not" is a state every
        /// interrupted download passes through, and it lasted at least thirteen
        /// seconds on the P30.
        /// </remarks>
        public IReadOnlyList<string> Fill(int howMany)
        {
            using var registry = new ModelRegistryService();
            var files = registry.GetLatestModel(Kws)!.BundleFiles!;
            var written = new List<string>();

            foreach (var f in files.Take(howMany))
            {
                var path = Path.Combine(Root, Kws, f.Name.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, new byte[f.SizeBytes]);
                written.Add(f.Name);
            }

            return written;
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }

    private static (bool Present, bool Partial) Ask(string root)
    {
        using var registry = new ModelRegistryService();
        using var loader = new BundleModelLoader(root, registry);
        return (loader.ModelPresent(Kws), loader.ModelPartial(Kws));
    }

    [Fact]
    public void An_encoder_is_not_a_bundle()
    {
        // THE WHOLE DEFECT IN ONE ASSERTION. The encoder is the largest catalogued
        // file, so it was the anchor the old rule asked about - and it is also the
        // first one fetched. From the moment it landed the bundle read as PRESENT with
        // 1.96 MB still owed: decoder, joiner, tokens, bpe.model and both keyword
        // files. ModelPartial returns false whenever ModelPresent is true, so the
        // resume skipped it; NotStarted excludes anything present, so the fetch
        // skipped it; the first-run plan saw its wake-word slot filled, so setup
        // skipped it - and the listener, which opens all five of the files it needs,
        // refused for ever.
        using var store = new Store();
        store.Fill(1);

        var (present, partial) = Ask(store.Root);
        Assert.False(present, "one file of seven was read as the whole bundle");
        Assert.True(partial, "a half-downloaded bundle must be something a resume can pick up");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(6)]
    public void Any_file_short_is_not_downloaded(int howMany)
    {
        // FIVE OF SEVEN USED TO PASS. The first fix hand-listed the names the spotter
        // opens - encoder, decoder, joiner, tokens.txt, keywords.txt - so a start that
        // landed between keywords.txt and bpe.model was told the bundle was complete,
        // and the keyword file could not be written. Asking the catalogue counts all
        // seven without anybody maintaining a list.
        using var store = new Store();
        store.Fill(howMany);

        var (present, partial) = Ask(store.Root);
        Assert.False(present, $"{howMany} files of seven were read as the whole bundle");
        Assert.True(partial);
    }

    [Fact]
    public void Every_file_at_its_full_size_is_downloaded()
    {
        using var store = new Store();
        var all = store.Fill(99);
        Assert.Equal(7, all.Count);

        var (present, partial) = Ask(store.Root);
        Assert.True(present, "a complete bundle is not being recognised - nothing would ever listen");
        Assert.False(partial);
    }

    [Fact]
    public void A_truncated_file_is_not_a_finished_one()
    {
        // A DOWNLOAD THAT STOPPED MID-FILE leaves a short file, not a missing one -
        // and the rule is the same one the anchor always used: the catalogued size or
        // more.
        using var store = new Store();
        store.Fill(99);

        using var registry = new ModelRegistryService();
        var last = registry.GetLatestModel(Kws)!.BundleFiles!.Last();
        var path = Path.Combine(store.Root, Kws, last.Name.Replace('/', Path.DirectorySeparatorChar));
        File.WriteAllBytes(path, new byte[Math.Max(0, last.SizeBytes - 1)]);

        Assert.False(Ask(store.Root).Present);
    }

    [Fact]
    public void Nothing_downloaded_is_neither_present_nor_partial()
    {
        using var store = new Store();
        var (present, partial) = Ask(store.Root);
        Assert.False(present);
        Assert.False(partial, "a bundle nobody has started is not a resume waiting to happen");
    }

    [Fact]
    public void A_chat_bundle_still_answers_on_its_weight_file()
    {
        // THE NARROW SCOPE IS DELIBERATE. A chat bundle's weight file IS the bundle -
        // 470 MB to 21 GB of it - and its siblings are kilobytes of config that
        // config.json already covers. The trap this fixes needs a bundle whose small
        // files matter, which is exactly the set with no weight file. Re-deciding
        // multi-gigabyte models on a new rule is a separate, much more expensive
        // question.
        using var registry = new ModelRegistryService();
        var chat = registry.AllModels.FirstOrDefault(m =>
            m.Modality == ModelModality.Chat && m.BundleFiles is { Count: > 1 } &&
            m.BundleFiles.Any(f => string.Equals(f.Name, "llm.mnn.weight", StringComparison.OrdinalIgnoreCase)));
        Assert.NotNull(chat);

        using var store = new Store();
        var dir = Path.Combine(store.Root, chat!.Name);
        Directory.CreateDirectory(dir);

        var weight = chat.BundleFiles!.First(f =>
            string.Equals(f.Name, "llm.mnn.weight", StringComparison.OrdinalIgnoreCase));
        File.WriteAllBytes(Path.Combine(dir, weight.Name), new byte[Math.Min(weight.SizeBytes, 4096)]);
        File.WriteAllText(Path.Combine(dir, "config.json"), "{}");

        // Short, so this says only that the rule still looks at the anchor rather than
        // at every file - not that a 21 GB model is being judged by a 4 KB stub.
        using var loader = new BundleModelLoader(store.Root, registry);
        Assert.False(loader.ModelPresent(chat.Name));
    }

    // ── "a model landed": once, and for every path ───────────────────────────

    [Fact]
    public async Task A_download_says_it_landed_once()
    {
        var landed = new List<string>();
        var key = "landed-" + Guid.NewGuid().ToString("N");

        await InFlightDownloads.RunAsync(key, null,
            _ => Task.FromResult("/models/thing"),
            completed: landed.Add);

        Assert.Equal(key, Assert.Single(landed));
    }

    [Fact]
    public async Task A_caller_that_joined_a_download_does_not_say_it_landed_as_well()
    {
        // ONCE PER LANDING, OR THE LISTENER RESTARTS THE MICROPHONE PER CALLER. Two
        // paths in the service fetched the same 548 MB at once on the P30 before this
        // type existed; the joiner shares the first caller's answer, and must share
        // its report too.
        var landed = new List<string>();
        var key = "joined-" + Guid.NewGuid().ToString("N");
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        var owner = InFlightDownloads.RunAsync(key, null,
            async _ => { started.SetResult(); await release.Task; return "/models/thing"; },
            completed: name => { lock (landed) landed.Add("owner:" + name); });

        await started.Task;
        var joiner = InFlightDownloads.RunAsync(key, null,
            _ => Task.FromResult("/models/never-run"),
            completed: name => { lock (landed) landed.Add("joiner:" + name); });

        release.SetResult();
        Assert.Equal("/models/thing", await owner);
        Assert.Equal("/models/thing", await joiner);

        lock (landed) Assert.Equal("owner:" + key, Assert.Single(landed));
    }

    [Fact]
    public async Task A_download_that_failed_did_not_land()
    {
        var landed = new List<string>();
        var key = "failed-" + Guid.NewGuid().ToString("N");

        await Assert.ThrowsAsync<InvalidOperationException>(() => InFlightDownloads.RunAsync(
            key, null,
            _ => Task.FromException<string>(new InvalidOperationException("no network")),
            completed: landed.Add));

        Assert.Empty(landed);
    }

    [Fact]
    public async Task A_listener_that_throws_does_not_fail_the_download_that_told_it()
    {
        // THE BYTES ARE DOWN BY THE TIME ANYBODY HEARS. Rethrowing would turn a landed
        // model into a failed one, and on the fetch service's loop it would cost every
        // model queued behind it.
        var key = "throwing-" + Guid.NewGuid().ToString("N");

        var path = await InFlightDownloads.RunAsync(key, null,
            _ => Task.FromResult("/models/thing"),
            completed: _ => throw new InvalidOperationException("the listener is broken"));

        Assert.Equal("/models/thing", path);
    }

    // ── the Android wiring, guarded at the source ────────────────────────────

    private static string Repo()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CircleAI.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }

    private static string Code(params string[] parts)
    {
        var path = Path.Combine(Repo(), Path.Combine(parts));
        Assert.True(File.Exists(path), path + " has moved - this guard is now pointing at nothing");

        var text = File.ReadAllText(path);
        text = Regex.Replace(text, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        text = Regex.Replace(text, @"^[ \t]*//.*$", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"^[ \t]*///.*$", "", RegexOptions.Multiline);
        return text;
    }

    [Fact]
    public void The_loader_raises_a_landing_only_for_a_model_that_was_not_here()
    {
        var loader = Code("src", "CircleAI", "Inference", "BundleModelLoader.cs");

        Assert.Contains("public static event Action<string>? ModelLanded;", loader, StringComparison.Ordinal);

        var core = loader.IndexOf("private Task<string> DownloadCoreAsync(", StringComparison.Ordinal);
        Assert.True(core >= 0, "DownloadCoreAsync has moved");
        var body = loader[core..loader.IndexOf("private static void Arrived(", core, StringComparison.Ordinal)];

        // ASKED BEFORE THE BYTES MOVE. This method is also how a bundle already on
        // disk is re-verified, and a listener that restarted the microphone on every
        // census would be its own bug.
        var was = body.IndexOf("var wasHere = ModelPresent(modelName);", StringComparison.Ordinal);
        var run  = body.IndexOf("InFlightDownloads.RunAsync(", StringComparison.Ordinal);
        Assert.True(was >= 0 && run > was, "the loader cannot tell a landing from a model that was already here");
        Assert.Contains("completed: _ => { if (!wasHere) Arrived(modelName); }", body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_fetch_service_no_longer_owns_the_hook_it_could_not_cover()
    {
        var fetch = Code("src", "CircleAI", "Device", "ModelFetchService.cs");

        // THREE OTHER PULL PATHS EXISTED. Passing the hook to this service's own two
        // loops is what left a wake bundle landed by setup-from-the-app, by the
        // abilities row's Turn on, or by a direct loader call starting nothing.
        Assert.DoesNotContain("landed: OnLanded", fetch, StringComparison.Ordinal);
        Assert.DoesNotContain("private void OnLanded(", fetch, StringComparison.Ordinal);
    }

    [Fact]
    public void The_process_that_owns_the_models_subscribes_once_and_respects_the_owners_choice()
    {
        var app = Code("src", "CircleAIService", "ServiceApplication.cs");

        var subscribe = app.IndexOf("BundleModelLoader.ModelLanded +=", StringComparison.Ordinal);
        Assert.True(subscribe >= 0, "nothing starts the wake word when its bundle lands");

        var body = app[subscribe..app.IndexOf("};", subscribe, StringComparison.Ordinal)];

        // Only for a model the wake word actually listens with, and through
        // ResumeAsync - a model arriving is not somebody asking for the microphone, so
        // the owner's recorded choice still decides.
        var listens = body.IndexOf("ResidentListeningControl.ListensWith(name)", StringComparison.Ordinal);
        var resume  = body.IndexOf("resident.ResumeAsync()", StringComparison.Ordinal);
        Assert.True(listens >= 0 && resume > listens,
            "the wake word is started for a model it does not use, or by a verb that ignores the owner's off");
        Assert.DoesNotContain("resident.StartAsync()", body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_listener_asks_the_catalogue_rather_than_listing_file_names()
    {
        var control = Code("src", "CircleAI", "Assistant.Device", "ResidentListeningControl.cs");

        // A SECOND OWNER OF "IS IT COMPLETE" IS WHAT MADE THE STATE UNRECOVERABLE.
        // Hand-listed names disagreed with the loader, which counted the bundle
        // present as soon as its largest file arrived - so every fetch path believed
        // it was here and the listener refused, for ever.
        var complete = control.IndexOf("private static bool IsComplete()", StringComparison.Ordinal);
        Assert.True(complete >= 0, "IsComplete has moved");
        var body = control[complete..control.IndexOf("private static bool _complete;", complete, StringComparison.Ordinal)];

        Assert.Contains("loader.ModelPresent(KwsBundle)", body, StringComparison.Ordinal);
        foreach (var listed in new[] { "*encoder*.onnx\") &&", "keywords.txt\"))", "tokens.txt\"))" })
            Assert.DoesNotContain(listed, body, StringComparison.Ordinal);

        // Both bundles the listener can run on.
        Assert.Contains("model is KwsBundle or TranscribingWake.ModelId", control, StringComparison.Ordinal);
    }

    [Fact]
    public void The_sideloaded_recogniser_is_checked_for_every_file_it_opens()
    {
        var asr = Code("src", "CircleAI", "Assistant.Device", "TranscribingWake.cs");

        // IT HAS NO CATALOGUE TO ASK - it is copied in with a file manager or pushed
        // to the external files directory for a measurement, so it appears file by
        // file. A listener started halfway took the transcribing path, threw inside
        // the transducer's constructor, and reported Listening.
        var usable = asr.IndexOf("private static bool IsUsable(string dir)", StringComparison.Ordinal);
        Assert.True(usable >= 0, "the recogniser is accepted on an encoder alone");

        var body = asr[usable..];
        foreach (var part in new[] { "*encoder*.onnx", "*decoder*.onnx", "*joiner*.onnx", "tokens.txt" })
            Assert.Contains(part, body, StringComparison.Ordinal);

        Assert.Contains("IsUsable(root) ? root : null", asr, StringComparison.Ordinal);
    }

    [Fact]
    public void What_the_room_says_is_only_written_down_when_somebody_asked_for_it()
    {
        // THE TRANSCRIBING WAKE MODE WROTE A TRANSCRIPT OF SOMEBODY'S HOME. heard.txt
        // in the external files directory, and every utterance verbatim in logcat, on
        // any phone that had the recogniser on it, from the next start of listening,
        // with nothing said and nothing asked - beside a card promising that the
        // listening is not kept.
        var log = Code("src", "CircleAI", "Voice", "UtteranceLog.cs");
        Assert.Contains("CaptureDump.MarkerName", log, StringComparison.Ordinal);

        var wake = Code("src", "CircleAI", "Assistant.Device", "ScreenUpWakeWord.cs");
        Assert.Contains("VoiceTrace.KeepsSpeech", wake, StringComparison.Ordinal);

        // And the keyword spotter's stall trace named the words it heard INSTEAD of
        // the phrase, on every frame it was waiting on.
        var spotter = Code("src", "CircleAI", "Voice", "ZipformerKwsSpotter.cs");
        Assert.Contains("if (VoiceTrace.KeepsSpeech)", spotter, StringComparison.Ordinal);

        // Off unless a host turns it on, and the host turns it on from the same
        // deliberate act that records the audio.
        var trace = Code("src", "CircleAI", "Audio", "VoiceTrace.cs");
        Assert.Contains("public static bool KeepsSpeech { get; set; }", trace, StringComparison.Ordinal);

        var service = Code("src", "CircleAIService", "ServiceApplication.cs");
        Assert.Contains("VoiceTrace.KeepsSpeech = CircleAI.Voice.CaptureDump.Asked(", service, StringComparison.Ordinal);
    }
}
