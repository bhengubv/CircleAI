// TheCatalogueIsTheSelectorTests.cs
//
// Part 1 of the plan: one assessed catalogue, and no second rule to bypass.
//
// SqliteModelCatalog, DeviceModelAssessor, CatalogModelSelector and
// AddModelCatalogue were all built, tested and committed - and AddModelCatalogue
// had ZERO production callers. Five live sites went on constructing
// `new DeviceAwareModelSelector(registry)` and ranking the shipped JSON:
//
//     CircleAISession.cs:255
//     CircleNeuronService.cs:503
//     Hosting/ServiceCollectionExtensions.cs:169
//     CircleAIService/Host/Program.cs:68
//     CircleAIService/Host/Program.cs:102
//
// docs/OPEN-GAPS.md records that defect four times over (A'4, A7, E12, E13), and
// it is this repo's signature failure: a library written, tested, committed, and
// with no way in. It mattered most where it cost most - CrashVerdict could write
// "this phone aborted on that model" into the catalogue and the thing choosing
// the model never read it.
//
// WHAT THESE PIN. Not the gate - CatalogModelSelectorTests already covers how it
// chooses. These cover the WIRING: that an assessed catalogue is actually
// preferred, that an unassessed one is not trusted, and that the fallback is a
// fallback rather than the normal path.
//
// WHY A FALLBACK EXISTS AT ALL, since "no second rule" was the point.
// CatalogueBootstrap runs on a background task at startup and BestFit THROWS
// when nothing in the catalogue satisfies the capability - so a question asked
// in the first seconds would crash on the one path somebody is waiting on. The
// fallback is one named, tested decision instead of five implicit ones.

using System;
using System.Collections.Generic;
using System.Linq;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public sealed class TheCatalogueIsTheSelectorTests
{
    private static ModelEntry Chat(string name, double gb, int rank) =>
        new(name, "1.0", "MNN-Q4")
        {
            Engine = ModelEngine.Mnn,
            Modality = ModelModality.Chat,
            TotalBytes = (long)(gb * 1_000_000_000d),
            MinStorageGb = gb,
            MinRamGb = gb,
            QualityRank = rank,
        };

    private static SqliteModelCatalog Empty() => new("Data Source=:memory:");

    private static SqliteModelCatalog Seeded(params ModelEntry[] entries)
    {
        var cat = new SqliteModelCatalog("Data Source=:memory:");
        foreach (var e in entries) cat.Upsert(e);
        return cat;
    }

    private static SqliteModelCatalog Assessed(params (ModelEntry Entry, bool Ok, double Rank)[] rows)
    {
        var cat = new SqliteModelCatalog("Data Source=:memory:");
        foreach (var (e, ok, rank) in rows)
        {
            cat.Upsert(e);
            cat.SetAssessment(e.Name, ok, rank);
        }
        return cat;
    }

    // ── has this device decided anything yet ──────────────────────────────────

    [Fact]
    public void Nothing_is_assessed_when_there_is_no_catalogue_at_all()
        => Assert.False(DeviceAwareModelSelector.HasBeenAssessed(null));

    [Fact]
    public void An_empty_catalogue_counts_as_unassessed()
    {
        using var cat = Empty();
        Assert.False(DeviceAwareModelSelector.HasBeenAssessed(cat));
    }

    [Fact]
    public void A_SEEDED_catalogue_is_still_unassessed_until_this_device_judges_it()
    {
        // THE DISTINCTION THAT MATTERS, and the reason row count is not the test.
        // CatalogueBootstrap seeds every model first and assesses after. In
        // between, the catalogue is FULL and every row says compatible = 0 -
        // and selecting from that returns the smallest capability match flagged
        // NothingFits. A confident wrong answer, where the fallback would have
        // given a right one.
        using var cat = Seeded(Chat("qwen-2b", 1.4, rank: 30), Chat("qwen-8b", 5.5, rank: 60));

        Assert.Equal(2, cat.Count());
        Assert.False(DeviceAwareModelSelector.HasBeenAssessed(cat),
            "a seeded-but-unjudged catalogue must not be trusted to select");
    }

    [Fact]
    public void One_assessed_row_is_enough_to_say_this_device_has_decided()
    {
        using var cat = Assessed((Chat("qwen-2b", 1.4, 30), true, 10.0));
        Assert.True(DeviceAwareModelSelector.HasBeenAssessed(cat));
    }

    [Fact]
    public void A_device_that_can_run_NOTHING_has_still_assessed_it()
    {
        // compatible = 0 everywhere is a VERDICT, not an absence of one, and it
        // is the answer a very small phone should get. Treating it as "not
        // assessed yet" would send exactly those devices back to the rule that
        // does not know about crashes.
        using var cat = Assessed((Chat("qwen-8b", 5.5, 60), false, 0.0));

        Assert.True(DeviceAwareModelSelector.HasBeenAssessed(cat));
        Assert.Empty(cat.Compatible(ModelModality.Chat));
    }

    // ── which selector a site actually gets ───────────────────────────────────

    // THESE ASSERTED A TYPE AND THERE IS ONLY ONE TYPE NOW.
    //
    // They read Assert.IsType<CatalogModelSelector> when the catalogue was assessed and
    // Assert.IsType<DeviceAwareModelSelector> when it was not, which pinned the SHAPE of
    // the old arrangement: two implementations and a factory choosing between them. That
    // factory's own comment called it "still a second rule". With one selector and the
    // catalogue as a source inside it, a type assertion is true by construction and
    // proves nothing - so what is pinned now is the behaviour those types stood for.

    [Fact]
    public void An_assessed_catalogue_is_what_selects()
    {
        // THE CATALOGUE'S VERDICT BEATS THE SHIPPED RANK, which is the whole reason the
        // assessment exists. Here the better model on paper is marked incompatible and
        // the lesser one compatible, so a selector reading the registry's ranking would
        // answer "big" and one reading this device's verdicts answers "small".
        using var cat = Assessed(
            (Chat("big-on-paper",   2.0, 30), false, 0.0),
            (Chat("small-but-runs", 1.0, 10), true, 10.0));

        var pick = DeviceAwareModelSelector.Preferring(cat)
            .BestFit(Probe(ramGb: 8), ChatCapability.Default);

        Assert.Equal("small-but-runs", pick.ModelId);
    }

    [Fact]
    public void Without_one_it_answers_from_the_registry_rather_than_throwing()
    {
        // The startup race: CatalogueBootstrap is on a background task, and BestFit
        // THROWS when nothing in the catalogue satisfies the capability. An unassessed
        // catalogue must therefore not be selected from at all - every row in it reads
        // compatible = 0 - and the registry answers until the assessment lands.
        using var cat = Empty();

        foreach (var selector in new[] { DeviceAwareModelSelector.Preferring(cat),
                                         DeviceAwareModelSelector.Preferring(null) })
        {
            var pick = selector.BestFit(Probe(ramGb: 8), ChatCapability.Default);
            Assert.False(string.IsNullOrWhiteSpace(pick?.ModelId),
                "an unassessed catalogue must fall through to the registry, not answer nothing");
        }
    }

    /// <summary>A device with room, so fit is not what the assertion turns on.</summary>
    private static DeviceProbe Probe(double ramGb) => new(
        RamAvailableBytes: (long)(ramGb * 1_000_000_000d),
        StorageFreeBytes: (long)(200 * 1_000_000_000d),
        Gpu: GpuKind.None, CpuCores: 8,
        Thermal: ThermalClass.Passive, Connectivity: Connectivity.Online)
    {
        RamTotalBytes = (long)(ramGb * 2 * 1_000_000_000d),
    };

    [Fact]
    public void The_fallback_can_actually_answer()
    {
        // A fallback that resolves and then throws is not a fallback. This is the
        // case that happens on every cold start before the first assessment.
        var selector = DeviceAwareModelSelector.Preferring(null, new ModelRegistryService());

        var pick = selector.BestFit(
            new DeviceProbe(
                RamAvailableBytes: (long)(3.0 * 1_000_000_000d),
                StorageFreeBytes: (long)(30 * 1_000_000_000d),
                Gpu: GpuKind.None, CpuCores: 8,
                Thermal: ThermalClass.Passive, Connectivity: Connectivity.Online),
            ChatCapability.Default);

        Assert.False(string.IsNullOrWhiteSpace(pick?.ModelId));
    }

    [Fact]
    public void A_catalogue_that_throws_is_treated_as_unassessed_not_fatal()
    {
        // A corrupt or locked database must cost the device its catalogue, not
        // its brain. HasBeenAssessed is asked on the path to answering a
        // question, so it swallows and falls back.
        Assert.False(DeviceAwareModelSelector.HasBeenAssessed(new ThrowingCatalogue()));

        // AND IT STILL ANSWERS. A type assertion proved only that a factory returned
        // something; what matters is that a corrupt or locked database costs the device
        // its catalogue and not its brain, so the registry has to carry the answer.
        var pick = DeviceAwareModelSelector.Preferring(new ThrowingCatalogue(), new ModelRegistryService())
            .BestFit(Probe(ramGb: 8), ChatCapability.Default);

        Assert.False(string.IsNullOrWhiteSpace(pick?.ModelId));
    }

    // ── the verdict reaches the decision, which is the whole point ────────────

    [Fact]
    public void A_model_this_phone_crashed_on_is_not_selected()
    {
        // PART 2 MEETS PART 1. CrashVerdict could already write a refusal into
        // the catalogue, and IsRefused could already read it - but the thing
        // CHOOSING the model was a separate rule over the shipped JSON, so the
        // refusal could not reach the choice. With the catalogue selecting, it
        // does.
        using var cat = Assessed(
            (Chat("big-one", 5.0, rank: 90), true, 50.0),
            (Chat("small-one", 1.4, rank: 30), true, 10.0));

        cat.Refuse("big-one", "aborted on this phone");

        var pick = DeviceAwareModelSelector.Preferring(cat)
            .BestFit(new DeviceProbe(
                RamAvailableBytes: (long)(8.0 * 1_000_000_000d),
                StorageFreeBytes: (long)(60 * 1_000_000_000d),
                Gpu: GpuKind.None, CpuCores: 8,
                Thermal: ThermalClass.Passive, Connectivity: Connectivity.Online),
                ChatCapability.Default);

        Assert.Equal("small-one", pick.ModelId);
    }

    private sealed class ThrowingCatalogue : IModelCatalog
    {
        private static InvalidOperationException Boom() => new("database is locked");

        public void Upsert(ModelEntry entry) => throw Boom();
        public bool ReSync(ModelEntry entry) => throw Boom();
        public void SetAssessment(string id, bool compatible, double rank) => throw Boom();
        public void Refuse(string id, string reason) => throw Boom();
        public void Pardon(string id) => throw Boom();
        public int Strike(string id) => throw Boom();
        public void RecordLoad(string id) => throw Boom();
        public int ForgiveFirstStrikes(string reason) => throw Boom();
        public AssessedModel? Assessment(string id) => throw Boom();
        public IReadOnlyList<AssessedModel> AllAssessed() => throw Boom();
        public void SetInstalled(string id, bool installed) => throw Boom();
        public ModelEntry? Get(string id) => throw Boom();
        public bool IsInstalled(string id) => throw Boom();
        public IReadOnlyList<ModelEntry> All() => throw Boom();
        public IReadOnlyList<ModelEntry> Compatible(ModelModality modality) => throw Boom();
        public ModelEntry? Best(ModelModality modality) => throw Boom();
        public int Count() => throw Boom();
        public void Dispose() { }
    }
}
