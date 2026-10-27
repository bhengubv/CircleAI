// FirstRunFetchStartsByItselfTests.cs
//
// EVERY ABILITY IS MEANT TO BE ON FROM FIRST RUN, and on the P30 none of them was.
//
// Measured on the P30 on 2026-10-09, on a freshly cleared app:
//
//   setup: no engine components on this device
//   Talking   - "Nothing for this yet"
//   Listening - "Nothing for this yet"
//   Answering - "Nothing for this yet"
//   Its name  - "Nothing for this yet"
//
// Nothing was broken. ModelFetchService asked ModelSetup.Unfinished what was owed,
// Unfinished reads loader.InstalledIds(), InstalledIds lists directories that
// EXIST - and a phone that has downloaded nothing has no directories. So it owed
// nothing, correctly, and started nothing. The one surface that could have fixed
// it was a Start button nobody had pressed.
//
// "talking and listening answer to its name, answering - all should be on by
// default - how else will it be sampled?"  - the owner, same day. So the fetch now
// begins by itself.
//
// WHAT THIS COSTS, PINNED HERE BECAUSE IT IS SOMEBODY ELSE'S MONEY. The P30's
// first-run plan is 3.4 GB on a metered South African connection, begun before
// anyone agrees to it. The setup screen used to be the gate; it no longer is. What
// replaced it is a foreground notification naming each model and a cancel that
// works - so the tests below pin BOTH halves, the fetching and the stopping,
// because a quieter version of this would be a surprise on a stranger's bill.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public sealed class FirstRunFetchStartsByItselfTests : IDisposable
{
    private readonly string _store =
        Path.Combine(Path.GetTempPath(), "circleai-onset-" + Guid.NewGuid().ToString("N"));

    public FirstRunFetchStartsByItselfTests() => Directory.CreateDirectory(_store);

    public void Dispose()
    {
        try { Directory.Delete(_store, recursive: true); } catch { /* a temp dir */ }
    }

    /// <summary>Real chat bundles, biggest first — the ones a plan is made of.</summary>
    private static List<ModelEntry> Bundles(ModelRegistryService registry, int take) =>
        registry.AllModels
            .Where(m => m.Modality == ModelModality.Chat && m.BundleFiles is { Count: > 1 })
            .OrderByDescending(m => m.TotalBytes)
            .Take(take)
            .ToList();

    /// <summary>Lays down every bundle file at full size, so ModelPresent says yes.</summary>
    private void LayComplete(ModelEntry model)
    {
        var dir = Path.Combine(_store, model.Name);
        foreach (var f in model.BundleFiles!)
            SparseStore.Write(Path.Combine(dir, f.Name), f.SizeBytes);

        // ModelPresent wants config.json for a chat bundle whether or not the
        // catalogue lists it among the files.
        var config = Path.Combine(dir, "config.json");
        if (!File.Exists(config)) File.WriteAllText(config, "{}");
    }

    /// <summary>Lays down everything but the weight — the half-finished state.</summary>
    private void LayPartial(ModelEntry model)
    {
        var dir = Path.Combine(_store, model.Name);
        var weight = model.BundleFiles!.OrderByDescending(f => f.SizeBytes).First();
        foreach (var f in model.BundleFiles!.Where(f => f.Name != weight.Name))
            SparseStore.Write(Path.Combine(dir, f.Name), f.SizeBytes);
    }

    /// <summary>Every file at half its size — the half-finished state for a one-file bundle.</summary>
    /// <remarks>
    /// LayPartial cannot do this one. It leaves out the largest file, and a GGUF bundle
    /// is a single file, so leaving out the largest leaves out everything and the model
    /// reads as not started. Progress() counts min(length on disk, catalogued size), so
    /// a short file is partial.
    /// </remarks>
    private void LayHalf(ModelEntry model)
    {
        var dir = Path.Combine(_store, model.Name);
        foreach (var f in model.BundleFiles!)
            SparseStore.Write(Path.Combine(dir, f.Name), Math.Max(1, f.SizeBytes / 2));
    }

    /// <summary>The smallest catalogued chat model on an engine, for the engine-fit tests.</summary>
    private static ModelEntry SmallestChatOn(ModelRegistryService registry, ModelEngine engine) =>
        registry.AllModels
            .Where(m => m.Modality == ModelModality.Chat && m.Engine == engine && !m.Retired)
            .OrderBy(m => m.TotalBytes)
            .First();

    private static (string Name, long Bytes) Planned(ModelEntry m) => (m.Name, m.TotalBytes);

    // ── What gets started ─────────────────────────────────────────────────────

    [Fact]
    public void A_model_with_not_one_byte_down_is_now_started()
    {
        // THE WHOLE CHANGE, in one assertion. Unfinished() returns empty for this
        // store - that is the P30 - and NotStarted is what sees it anyway.
        var registry = new ModelRegistryService();
        var model = Bundles(registry, 1).Single();
        using var loader = new BundleModelLoader(_store, registry);

        Assert.Empty(ModelSetup.Unfinished(loader));

        var todo = ModelSetup.NotStarted(loader, [Planned(model)]);

        Assert.Single(todo);
        Assert.Equal(model.Name, todo[0].Name);
        Assert.Equal(model.TotalBytes, todo[0].Need);
    }

    [Fact]
    public void A_model_already_on_the_phone_is_not_fetched_again()
    {
        var registry = new ModelRegistryService();
        var model = Bundles(registry, 1).Single();
        LayComplete(model);
        using var loader = new BundleModelLoader(_store, registry);

        Assert.True(loader.ModelPresent(model.Name), "precondition: the store holds it");
        Assert.Empty(ModelSetup.NotStarted(loader, [Planned(model)]));
    }

    [Fact]
    public void A_half_finished_download_belongs_to_the_resume_not_the_onset()
    {
        // THE TWO METHODS MUST NOT BOTH CLAIM IT. ResumeUnfinishedAsync carries on
        // from the bytes on disk; FetchNotStartedAsync starts from zero. A model
        // claimed by both would have its partial bytes thrown away and begin again
        // - which is the three-days-at-1.1-GB failure, recreated by the fix for it.
        var registry = new ModelRegistryService();
        var model = Bundles(registry, 1).Single();
        LayPartial(model);
        using var loader = new BundleModelLoader(_store, registry);

        Assert.True(loader.ModelPartial(model.Name), "precondition: bytes are down");
        Assert.Contains(ModelSetup.Unfinished(loader), u => u.Name == model.Name);
        Assert.Empty(ModelSetup.NotStarted(loader, [Planned(model)]));
    }

    [Fact]
    public void The_small_ones_go_first_so_the_phone_works_within_minutes()
    {
        // Unfinished() is biggest-first, deliberately: the big one is what blocks a
        // better answer. NotStarted is the REVERSE, and for an equally concrete
        // reason - the small entries are the wake word, a voice and the ears, so a
        // few minutes in the phone can hear its name and reply, instead of a person
        // waiting out a 2.8 GB brain before anything at all responds.
        var registry = new ModelRegistryService();
        var models = Bundles(registry, 3);
        Assert.Equal(3, models.Count);
        using var loader = new BundleModelLoader(_store, registry);

        var todo = ModelSetup.NotStarted(loader, models.Select(Planned));

        Assert.Equal(3, todo.Count);
        Assert.Equal(todo.OrderBy(t => t.Need).Select(t => t.Name), todo.Select(t => t.Name));
        Assert.True(todo[0].Need <= todo[^1].Need);
    }

    [Fact]
    public void An_empty_plan_starts_nothing()
    {
        // The branch that keeps an unasked-for 3.4 GB from beginning on a guess:
        // ModelFetchService refuses to launch when the plan will not compute, and
        // an empty plan must mean nothing rather than everything.
        using var loader = new BundleModelLoader(_store, new ModelRegistryService());

        Assert.Empty(ModelSetup.NotStarted(loader, []));
    }

    [Fact]
    public void A_blank_name_on_the_plan_is_skipped_rather_than_fetched()
    {
        using var loader = new BundleModelLoader(_store, new ModelRegistryService());

        var todo = ModelSetup.NotStarted(loader, [("", 1L), ("   ", 2L)]);

        Assert.Empty(todo);
    }

    // ── What the owner can still stop ─────────────────────────────────────────

    [Fact]
    public void A_cancelled_token_stops_the_sweep_before_a_single_byte()
    {
        // THE CONSENT, SUCH AS IT IS. With the setup screen no longer the gate, a
        // cancel that works is most of what is left. Pinned on the sweep here; the
        // in-flight abort is pinned by the next test.
        var registry = new ModelRegistryService();
        var model = Bundles(registry, 1).Single();
        using var loader = new BundleModelLoader(_store, registry);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var said = new List<string>();
        ModelSetup.FetchNotStartedAsync(loader, [Planned(model)], said.Add, cancelled.Token)
                  .GetAwaiter().GetResult();

        Assert.DoesNotContain(said, s => s.StartsWith("fetching ", StringComparison.Ordinal));
        Assert.False(loader.ModelPartial(model.Name), "nothing should have been written");
    }

    [Fact]
    public void The_download_call_is_handed_the_token_and_not_only_the_loop()
    {
        // THE BUG THIS PINS WAS MINE, found reading the code back before testing
        // it. The first version called the two-argument overload -
        // DownloadModelAsync(name) - which takes no CancellationToken at all. So
        // the token was checked only BETWEEN models: "Stop" during the 2.8 GB brain
        // did nothing until the brain finished. For a download that STARTS WITHOUT
        // ANYONE TAPPING ANYTHING, that is not a cancel, and the notification it
        // sits under would have been advertising a stop that did not stop.
        //
        // Asserted against the signature because the alternative is a real
        // multi-gigabyte HTTP fetch: a unit test can prove the token has somewhere
        // to go, and the device run proves it arrives. If someone later swaps this
        // back to the convenient overload, this fails and says why.
        var rich = typeof(BundleModelLoader).GetMethod(
            nameof(BundleModelLoader.DownloadModelAsync),
            [typeof(string), typeof(IProgress<CircleAI.Core.DownloadProgress>), typeof(CancellationToken)]);

        Assert.NotNull(rich);

        var thin = typeof(BundleModelLoader).GetMethod(
            nameof(BundleModelLoader.DownloadModelAsync),
            [typeof(string), typeof(IProgress<float>)]);

        Assert.NotNull(thin);
        Assert.DoesNotContain(
            thin!.GetParameters(), p => p.ParameterType == typeof(CancellationToken));
    }

    [Fact]
    public void The_sweep_says_what_it_is_about_to_spend_before_it_spends_it()
    {
        // The notification is built from these lines, and it is now the whole
        // disclosure. A sweep that fetched silently would be the defect.
        var registry = new ModelRegistryService();
        var model = Bundles(registry, 1).Single();
        LayComplete(model);
        using var loader = new BundleModelLoader(_store, registry);

        var said = new List<string>();
        ModelSetup.FetchNotStartedAsync(loader, [Planned(model)], said.Add)
                  .GetAwaiter().GetResult();

        // Nothing owed, so it says so rather than going quiet - the same rule as
        // Describe(): silence and health must not look alike.
        Assert.Contains("setup: nothing left to start", said);
    }

    [Fact]
    public void A_name_the_catalogue_does_not_know_fails_by_itself_and_not_for_the_others()
    {
        // One unreachable model must not deny a person the other five. An unknown
        // id cannot be downloaded, and the sweep must name it and carry on.
        var registry = new ModelRegistryService();
        var real = Bundles(registry, 1).Single();
        LayComplete(real);
        using var loader = new BundleModelLoader(_store, registry);

        var said = new List<string>();
        ModelSetup.FetchNotStartedAsync(
                loader, [("Something-Nobody-Ships", 1L), Planned(real)], said.Add)
            .GetAwaiter().GetResult();

        Assert.Contains(said, s => s.StartsWith("Something-Nobody-Ships failed:", StringComparison.Ordinal));
        Assert.DoesNotContain(said, s => s.StartsWith("first-run fetch failed", StringComparison.Ordinal));
    }

    // ── The brain is not one of seventy-five ──────────────────────────────────
    //
    // SMALLEST FIRST IS A TIEBREAK, NOT A PRIORITY, AND IT PUT THE BRAIN LAST.
    // Measured on a clean Windows 11 install on 2026-10-27: 75 downloads queued
    // smallest first, so the 2.85 GB brain came second-to-last - behind 45 MMS voices
    // at 114 MB each and 13 Piper voices at 63 MB - while the service printed
    // "questions will wait for one". Roughly 8 GB landed before the one model that
    // makes it answer. Nothing was broken and no test could have caught it, because
    // the only test of the order asserted the order that produced it.

    [Fact]
    public void The_brain_goes_first_even_though_it_is_the_biggest()
    {
        var registry = new ModelRegistryService();
        var models = Bundles(registry, 3);              // biggest first
        var brain = models[0];
        using var loader = new BundleModelLoader(_store, registry);

        Assert.True(brain.TotalBytes >= models[^1].TotalBytes, "precondition: the brain is the biggest");

        var todo = ModelSetup.NotStarted(loader, models.Select(Planned), brainFirst: brain.Name);

        Assert.Equal(3, todo.Count);
        Assert.Equal(brain.Name, todo[0].Name);
    }

    [Fact]
    public void Everything_after_the_brain_keeps_its_smallest_first_order()
    {
        // The small-first reasoning is still right for everything else: the small
        // entries are the wake word, a voice and the ears. Naming the brain lifts ONE
        // model, and must not shuffle the rest.
        var registry = new ModelRegistryService();
        var models = Bundles(registry, 3);
        var brain = models[0];
        using var loader = new BundleModelLoader(_store, registry);

        var rest = ModelSetup.NotStarted(loader, models.Select(Planned), brainFirst: brain.Name)
                             .Skip(1).ToList();

        Assert.Equal(2, rest.Count);
        Assert.Equal(rest.OrderBy(t => t.Need).Select(t => t.Name), rest.Select(t => t.Name));
    }

    [Fact]
    public void A_brain_that_is_not_on_the_plan_leaves_the_order_alone()
    {
        // The desktop head passes whatever the selector chose, and on a machine where
        // nothing fits that is null - or a name the plan does not carry. Either way the
        // queue must come out exactly as it would have without the argument.
        var registry = new ModelRegistryService();
        var models = Bundles(registry, 3);
        using var loader = new BundleModelLoader(_store, registry);

        var named = ModelSetup.NotStarted(loader, models.Select(Planned), brainFirst: "Nothing-Like-This");
        var plain = ModelSetup.NotStarted(loader, models.Select(Planned));

        Assert.Equal(plain.Select(t => t.Name), named.Select(t => t.Name));
        Assert.Equal(named.OrderBy(t => t.Need).Select(t => t.Name), named.Select(t => t.Name));
    }

    [Fact]
    public void The_queue_says_the_order_it_actually_uses()
    {
        // THE LOG DESCRIBED AN INTENTION, NOT THE QUEUE. "starting 75 download(s),
        // smallest first" was printed while the brain sat second-to-last in that very
        // list, which is how the ordering went unnoticed for a whole install. The line
        // is said before the token is checked, so a cancelled token proves the wording
        // without fetching a byte.
        var registry = new ModelRegistryService();
        var models = Bundles(registry, 3);
        var brain = models[0];
        using var loader = new BundleModelLoader(_store, registry);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var withBrain = new List<string>();
        ModelSetup.FetchNotStartedAsync(loader, models.Select(Planned), withBrain.Add,
                                        cancelled.Token, landed: null, brainFirst: brain.Name)
                  .GetAwaiter().GetResult();

        var withoutBrain = new List<string>();
        ModelSetup.FetchNotStartedAsync(loader, models.Select(Planned), withoutBrain.Add, cancelled.Token)
                  .GetAwaiter().GetResult();

        Assert.Contains($"setup: starting 3 download(s), {brain.Name} first, then smallest first", withBrain);
        Assert.Contains("setup: starting 3 download(s), smallest first", withoutBrain);
        Assert.DoesNotContain(withBrain, s => s.StartsWith("fetching ", StringComparison.Ordinal));
    }

    // ── A model this build cannot open ────────────────────────────────────────

    [Fact]
    public void A_model_this_build_cannot_open_is_not_fetched_at_all()
    {
        // THE 7.59 GB. On that same Windows install Qwen-Image-2.1-UC was fetched in
        // full and then rejected with "no loadable model file was found for that
        // modality": it is an Engine=LlamaCpp model, the build shipped no llama.cpp,
        // and NotStarted had no engine filter. Unfinished has had one since the service
        // was caught resuming half a GGUF - so the same model was refused a resume and
        // granted a fresh download.
        //
        // ENGINES PASSED EXPLICITLY, not probed. Whether llamabridge.dll loads in the
        // test host decides what ShippedEngines() answers, and a test whose verdict
        // depends on that proves nothing about the filter.
        var registry = new ModelRegistryService();
        var gguf = SmallestChatOn(registry, ModelEngine.LlamaCpp);
        var mnn  = SmallestChatOn(registry, ModelEngine.Mnn);
        using var loader = new BundleModelLoader(_store, registry);

        var todo = ModelSetup.NotStarted(loader, [Planned(gguf), Planned(mnn)],
                                         brainFirst: null, engines: [ModelEngine.Mnn]);

        Assert.Single(todo);
        Assert.Equal(mnn.Name, todo[0].Name);
    }

    [Fact]
    public void A_downloaded_GGUF_chat_model_reads_as_present_without_a_config_json()
    {
        // IT NEVER DID, AND THAT MADE EVERY GGUF BRAIN UNREACHABLE. ModelPresent required
        // config.json of ANY chat bundle, which is an MNN invariant rather than a chat
        // one: all nine or ten files of an MNN chat bundle include it, and every MNN
        // entry in this registry carries it. A GGUF chat bundle is one .gguf and
        // upstream ships no config.json at all - so a fully downloaded Bonsai read as
        // absent for ever. The brain's own present-check threw ModelNotInstalledException
        // on a model sitting whole on disk, and the first-run fetch queued it again on
        // every start. Seven GGUF chat entries landed in this registry in that state.
        var registry = new ModelRegistryService();
        var gguf = SmallestChatOn(registry, ModelEngine.LlamaCpp);

        Assert.DoesNotContain(gguf.BundleFiles!,
            f => f.Name.Equals("config.json", StringComparison.OrdinalIgnoreCase));

        var dir = Path.Combine(_store, gguf.Name);
        foreach (var f in gguf.BundleFiles!)
            SparseStore.Write(Path.Combine(dir, f.Name), f.SizeBytes);

        using var loader = new BundleModelLoader(_store, registry);

        Assert.True(loader.ModelPresent(gguf.Name),
            gguf.Name + " is whole on disk and still reads as absent");
        Assert.False(loader.ModelPartial(gguf.Name), "whole is not partial");

        // And so the fetch stops owing it, which is the half that cost the re-download.
        Assert.Empty(ModelSetup.NotStarted(loader, [Planned(gguf)], brainFirst: null,
                                           engines: [ModelEngine.Mnn, ModelEngine.LlamaCpp]));
    }

    [Fact]
    public void The_path_handed_to_the_generator_for_a_GGUF_is_the_gguf()
    {
        // THE THIRD PLACE THE MNN ASSUMPTION LIVED, and the one that actually threw.
        // ResolveLoadPath's Chat arm resolved to config.json, so every llama.cpp entry
        // fell into it and failed with "no loadable model file was found for that
        // modality" - the exact sentence the 3.9.0 Windows service answered with after
        // fetching 7.59 GB of Qwen-Image-2.1-UC, which was read as a download problem
        // rather than a resolver that could not name the file it had just downloaded.
        var registry = new ModelRegistryService();
        var gguf = SmallestChatOn(registry, ModelEngine.LlamaCpp);

        var dir = Path.Combine(_store, gguf.Name);
        foreach (var f in gguf.BundleFiles!)
            SparseStore.Write(Path.Combine(dir, f.Name), f.SizeBytes);

        // A projector beside the weight, as prism's repos actually ship: the resolver
        // must hand back the brain and not a third of a vision tower.
        SparseStore.Write(Path.Combine(dir, gguf.Name + "-mmproj-Q8_0.gguf"), 1024);

        using var loader = new BundleModelLoader(_store, registry);
        var path = loader.GetModelPath(gguf.Name);

        Assert.EndsWith(".gguf", path, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mmproj", Path.GetFileName(path), StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(path), "the resolved path must be a file that is actually here: " + path);
        Assert.Equal(gguf.BundleFiles![0].Name, Path.GetFileName(path));
    }

    [Fact]
    public void The_brain_is_built_with_the_engine_the_model_needs()
    {
        // A GREP FOR CALLERS IS WORTH MORE THAN A GREEN TEST RUN. "new LlamaGenerator"
        // matched nothing in the whole repository: both of AIService's construction
        // sites built QwenTextGenerator, which is MNN, so every GGUF brain was
        // unreachable at the very last step after the selector, the assessor, the
        // presence check and the resolver had all been taught about it.
        //
        // ASSERTED ON THE SOURCE because constructing the real thing needs the native
        // bridge and a multi-gigabyte model, and what went wrong here is not arithmetic -
        // it is that one branch did not exist. A device run proves it generates; this
        // proves the branch is there to be run, and fails loudly if someone collapses it
        // back to a single generator.
        var service = File.ReadAllText(Path.Combine(RepoRoot(), "src", "CircleAI", "Hosting", "AIService.cs"));

        Assert.Contains("new LlamaGenerator(", service);
        Assert.Contains("EngineChoice.Run(entry)", service);

        // AND BOTH SITES GO THROUGH IT. The start path builds the brain; BuildGenerator
        // builds a specialist. Wiring only the second would leave the brain - the one
        // generator the service loads on start - on MNN.
        Assert.Contains("BuildByEngine(_resolvedModelId, modelPath, contextSize, ChatCapability.Default)", service);
        Assert.Contains("BuildByEngine(modelId, modelPath, contextSize, capability)", service);
    }

    /// <summary>The repository root, found by walking up to the folder holding src/.</summary>
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new DirectoryNotFoundException("could not find the repository root");
    }

    [Fact]
    public void A_GGUF_that_is_not_here_yet_is_expected_at_its_catalogued_name()
    {
        // GetModelPath's contract is that a caller File.Exists-tests it and downloads
        // when missing. Returning the MNN config.json anchor for a GGUF named a file
        // that would never appear however completely the model arrived, so the test
        // could only ever answer "still missing".
        var registry = new ModelRegistryService();
        var gguf = SmallestChatOn(registry, ModelEngine.LlamaCpp);

        using var loader = new BundleModelLoader(_store, registry);
        var path = loader.GetModelPath(gguf.Name);

        Assert.False(File.Exists(path), "precondition: nothing is downloaded");
        Assert.Equal(gguf.BundleFiles![0].Name, Path.GetFileName(path));
    }

    [Fact]
    public void The_resume_and_the_onset_agree_about_what_this_build_can_open()
    {
        // ONE RULE, TWO METHODS, AND ONLY ONE OF THEM HAD IT. That is the whole defect:
        // a model excluded from the resume and included in the fresh fetch is a model
        // whose bytes get spent twice over for an engine that will never open it. Pinned
        // on both methods together so neither can drift from the other again.
        var registry = new ModelRegistryService();
        var gguf = SmallestChatOn(registry, ModelEngine.LlamaCpp);
        LayHalf(gguf);
        using var loader = new BundleModelLoader(_store, registry);

        Assert.True(loader.ModelPartial(gguf.Name), "precondition: half of it is on disk");
        Assert.False(loader.CanRunHere(gguf.Name, [ModelEngine.Mnn]), "precondition: an MNN-only build cannot open it");

        Assert.DoesNotContain(ModelSetup.Unfinished(loader, [ModelEngine.Mnn]), u => u.Name == gguf.Name);
        Assert.DoesNotContain(ModelSetup.NotStarted(loader, [Planned(gguf)], brainFirst: null, engines: [ModelEngine.Mnn]),
                              t => t.Name == gguf.Name);

        // And it is the ENGINE that excludes it, not the bytes: the same store with
        // llama.cpp shipped owes the resume exactly this model.
        Assert.Contains(ModelSetup.Unfinished(loader, [ModelEngine.Mnn, ModelEngine.LlamaCpp]),
                        u => u.Name == gguf.Name);
    }
}
