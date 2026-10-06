// ModelOfferTests.cs
//
// PART 4: NEVER OFFER SOMEBODY A DOWNLOAD THIS PHONE CANNOT USE.
//
// A Circle OS device was offered Qwen3.6-35B-A3B-MNN at 22.8 GB. The size was
// honest and the person accepted it. Three days later it finished, and loading it
// aborted the process and set lowmemorykiller on a dozen system apps. The offer was
// the defect, not the number beside it.
//
// PART 5: AND SAY WHAT IS SITTING THERE UNUSABLE, THEN WAIT TO BE TOLD.
//
// After the abort the model is refused - correctly, permanently and silently - and
// 22.8 GB stays on the phone. Housekeeping will not reclaim it, and its comment is
// right about why: "free space comes back, a fit rule gets corrected, and the bytes
// are already paid for." That reasoning holds for a model refused by a PREDICTION.
// It does not hold for one refused by an ATTEMPT.

using System;
using System.IO;
using System.Linq;
using CircleAI.Assistant;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public sealed class ModelOfferTests : IDisposable
{
    private readonly SqliteModelCatalog _cat = new("Data Source=:memory:");
    private readonly string _store = Path.Combine(
        Path.GetTempPath(), "circleai-offer-" + Guid.NewGuid().ToString("N"));

    public ModelOfferTests() => Directory.CreateDirectory(_store);

    public void Dispose()
    {
        _cat.Dispose();
        try { Directory.Delete(_store, recursive: true); } catch { }
    }

    private static ModelEntry Model(string name, int rank = 9) =>
        new(name, "1.0", "MNN-Q4")
        {
            Modality     = ModelModality.Chat,
            Engine       = ModelEngine.Mnn,
            TotalBytes   = 1_000_000_000,
            MinRamGb     = 1,
            MinStorageGb = 1,
            QualityRank  = rank,
            Capabilities = ["Default"],
            BundleFiles  = [new BundleFile("llm.mnn.weight", "", 1_000_000_000)],
        };

    private static DeviceProbe Phone() =>
        new(RamAvailableBytes: 8_000_000_000,
            StorageFreeBytes:  64_000_000_000,
            Gpu:               GpuKind.None,
            CpuCores:          8,
            Thermal:           ThermalClass.Passive,
            Connectivity:      Connectivity.Online);

    // ── Part 4 ───────────────────────────────────────────────────────────────

    [Fact]
    public void A_first_launch_still_offers_everything()
    {
        // THE TRAP IN THIS WHOLE IDEA. On a first launch the catalogue is seeded and
        // UNASSESSED, so every row reads compatible = 0. A predicate that treated
        // that as "cannot run" would offer NOTHING on the one run whose entire job is
        // to offer everything.
        _cat.Upsert(Model("fresh"));

        Assert.Null(_cat.Assessment("fresh")!.AssessedAt);
        Assert.False(_cat.Assessment("fresh")!.Compatible);
        Assert.True(ModelOffer.Worth(_cat, "fresh"), "an unassessed row must not be hidden");
    }

    [Fact]
    public void No_catalogue_offers_everything()
    {
        // A device whose catalogue could not be opened must behave as it did before
        // any of this existed.
        Assert.True(ModelOffer.Worth(null, "anything"));
    }

    [Fact]
    public void A_model_not_in_this_catalogue_is_not_our_call()
        => Assert.True(ModelOffer.Worth(_cat, "never-heard-of-it"));

    [Fact]
    public void An_assessed_and_usable_model_is_offered()
    {
        _cat.Upsert(Model("small"));
        DeviceModelAssessor.MnnOnly(_cat).Assess(Phone());

        Assert.True(ModelOffer.Worth(_cat, "small"));
    }

    [Fact]
    public void An_assessed_and_incompatible_model_is_not_offered()
    {
        // The whole point of Part 4: a row the assessor judged against THIS device
        // and rejected never reaches a download offer.
        _cat.Upsert(Model("huge") with { MinRamGb = 500, TotalBytes = 500_000_000_000 });
        DeviceModelAssessor.MnnOnly(_cat).Assess(Phone());

        Assert.NotNull(_cat.Assessment("huge")!.AssessedAt);
        Assert.False(ModelOffer.Worth(_cat, "huge"));
    }

    [Fact]
    public void A_refused_model_is_not_offered_even_before_any_assessment()
    {
        // A refusal is evidence from an attempt and does not wait on a prediction.
        _cat.Upsert(Model("killer"));
        _cat.Refuse("killer", CrashVerdict.Reason);

        Assert.Null(_cat.Assessment("killer")!.AssessedAt);
        Assert.False(ModelOffer.Worth(_cat, "killer"));
    }

    [Fact]
    public void The_plan_predicate_honours_the_owner_and_the_phone_together()
    {
        // ONE SEAM, THREE REASONS. Only the native sample ever passed `declined`, so
        // the shipping head re-downloaded whatever somebody had switched off;
        // composing them here means a caller cannot honour one and forget the rest.
        _cat.Upsert(Model("killer"));
        _cat.Upsert(Model("fine"));
        _cat.Refuse("killer", CrashVerdict.Reason);
        DeviceModelAssessor.MnnOnly(_cat).Assess(Phone());

        var notOffered = ModelOffer.NotWorthOffering(_cat, name => name == "turned-off");

        Assert.True(notOffered("turned-off"), "the owner's refusal must still bind");
        Assert.True(notOffered("killer"), "the phone's refusal must bind too");
        Assert.False(notOffered("fine"));
    }

    [Fact]
    public void A_screen_names_the_same_model_the_runtime_would_load()
    {
        // ModelChoice.For is installed-first and used to return an installed model
        // UNCONDITIONALLY - so on a device holding a complete 22.8 GB MoE it would
        // have named it as the chosen Chat model while the service refused to load
        // it. "One rule, one answer" is written in its own comments.
        var registry = new ModelRegistryService();
        var chat = registry.AllModels
            .Where(m => m.Modality == ModelModality.Chat && m.BundleFiles is { Count: > 0 })
            .OrderByDescending(m => m.QualityRank)
            .Take(2)
            .ToList();
        Assert.Equal(2, chat.Count);

        foreach (var m in chat)
        {
            var dir = Path.Combine(_store, m.Name);
            Directory.CreateDirectory(dir);
            foreach (var f in m.BundleFiles!)
            {
                SparseStore.Write(Path.Combine(dir, f.Name), f.SizeBytes);
            }
            _cat.Upsert(m);
            _cat.SetInstalled(m.Name, true);
        }
        _cat.Refuse(chat[0].Name, CrashVerdict.Reason);

        using var loader = new BundleModelLoader(_store, registry);
        var named = ModelChoice.For(ModelModality.Chat, registry, loader, Phone(), _cat);

        Assert.NotNull(named);
        Assert.NotEqual(chat[0].Name, named!.Name);
    }

    // ── Part 5 ───────────────────────────────────────────────────────────────

    private string Install(string name, long bytes)
    {
        var dir = Path.Combine(_store, name);
        Directory.CreateDirectory(dir);
        SparseStore.Write(Path.Combine(dir, "llm.mnn.weight"), bytes);
        return dir;
    }

    [Fact]
    public void Dead_weight_is_what_is_here_and_refused()
    {
        _cat.Upsert(Model("killer"));
        _cat.Refuse("killer", CrashVerdict.Reason);
        Install("killer", 4096);

        var found = DeadWeight.On(_cat, _store);

        Assert.Single(found);
        Assert.Equal("killer", found[0].Name);
        Assert.Equal(CrashVerdict.Reason, found[0].Why);
    }

    [Fact]
    public void A_model_merely_predicted_not_to_fit_is_not_dead_weight()
    {
        // REFUSED FROM EVIDENCE ONLY. A prediction can be wrong and has been - the
        // 2.5 GB claim on a 22.8 GB model was one - and offering to delete somebody's
        // download on an estimate is how they lose it to a guess.
        _cat.Upsert(Model("huge") with { MinRamGb = 500, TotalBytes = 500_000_000_000 });
        DeviceModelAssessor.MnnOnly(_cat).Assess(Phone());
        Install("huge", 4096);

        Assert.False(_cat.Assessment("huge")!.Compatible);
        Assert.Empty(DeadWeight.On(_cat, _store));
    }

    [Fact]
    public void A_refused_model_with_nothing_on_disk_is_not_dead_weight()
    {
        _cat.Upsert(Model("killer"));
        _cat.Refuse("killer", CrashVerdict.Reason);

        Assert.Empty(DeadWeight.On(_cat, _store));
    }

    [Fact]
    public void Clearing_frees_the_bytes_and_keeps_the_verdict()
    {
        // THE VERDICT SURVIVES THE DELETION. It stopped this phone and that is still
        // true, so the same version must not be offered again. Pardoning is a
        // separate word somebody says on purpose.
        _cat.Upsert(Model("killer"));
        _cat.SetInstalled("killer", true);
        _cat.Refuse("killer", CrashVerdict.Reason);
        var dir = Install("killer", 8192);

        var freed = DeadWeight.Clear(_cat, _store);

        Assert.Equal(8192, freed);
        Assert.False(Directory.Exists(dir));
        Assert.True(_cat.Assessment("killer")!.Refused, "clearing must not pardon");
        Assert.False(_cat.IsInstalled("killer"));
    }

    [Fact]
    public void Clearing_touches_nothing_it_was_not_asked_about()
    {
        _cat.Upsert(Model("killer"));
        _cat.Upsert(Model("keeper"));
        _cat.Refuse("killer", CrashVerdict.Reason);
        Install("killer", 4096);
        var keep = Install("keeper", 4096);

        DeadWeight.Clear(_cat, _store);

        Assert.True(Directory.Exists(keep));
    }

    [Fact]
    public void Nothing_is_ever_deleted_without_the_word()
    {
        // feedback_never_delete_without_asking. There is no automatic caller of
        // Clear and there must not be one; On() is the only thing anything runs by
        // itself, and it reads.
        _cat.Upsert(Model("killer"));
        _cat.Refuse("killer", CrashVerdict.Reason);
        var dir = Install("killer", 4096);

        DeadWeight.On(_cat, _store);

        Assert.True(Directory.Exists(dir), "finding dead weight must not delete it");
    }

    [Theory]
    [InlineData("Clear it.")]
    [InlineData("clear the big one")]
    [InlineData("Get rid of it")]
    [InlineData("Free up the space")]
    [InlineData("Hey B, clear it")]
    public void Being_told_to_clear_it_is_recognised(string said)
        => Assert.Equal(SelfAsk.ClearIt, SelfRequest.Of(said));

    [Theory]
    [InlineData("yes")]
    [InlineData("yeah")]
    [InlineData("ok")]
    [InlineData("sure")]
    [InlineData("clear my calendar")]
    [InlineData("delete the message I just sent")]
    public void A_bare_agreement_never_deletes_anything(string said)
    {
        // DELIBERATELY NOT "yes". It is the commonest word in a conversation, and
        // binding twenty-two gigabytes to it would mean somebody agreeing with the
        // previous sentence loses a download.
        Assert.NotEqual(SelfAsk.ClearIt, SelfRequest.Of(said));
    }

    [Fact]
    public void Asking_what_it_runs_is_told_about_the_dead_weight_too()
    {
        // THE OFFER RIDES ON THE STATUS ANSWER, because the binder is
        // request/response and there is no unprompted channel to the speaker. Before
        // this, 22.8 GB of unusable model existed only in a dumpsys reading.
        var said = AssistantPersona.WhichBrain(
            ready: true, running: true, gaveUpOnSomething: true, deadWeightSize: "22.8 GB");

        Assert.Contains("smaller brain", said);
        Assert.Contains("22.8 GB", said);
        Assert.Contains("Say the word", said);
    }

    [Fact]
    public void With_nothing_wasted_the_answer_stays_short()
    {
        // A person whose phone is tidy should not hear about storage.
        var said = AssistantPersona.WhichBrain(ready: true, running: true, gaveUpOnSomething: false);

        Assert.Equal(AssistantPersona.RunningWell, said);
        Assert.DoesNotContain("Say the word", said);
    }

    [Fact]
    public void The_offer_states_the_cost_and_the_answer_states_the_gain()
    {
        Assert.Contains("22.8 GB", AssistantPersona.DeadWeight("22.8 GB"));
        Assert.Contains("22.8 GB", AssistantPersona.ClearIt("22.8 GB"));
        Assert.Equal(AssistantPersona.NothingToClear, AssistantPersona.ClearIt(null));
    }
}
