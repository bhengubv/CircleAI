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
}
