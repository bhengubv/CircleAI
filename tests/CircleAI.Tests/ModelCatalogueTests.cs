// ModelCatalogueTests.cs
//
// That a refresh reaches every reader, and that a bad one reaches none.
//
// THE REGISTRY IS BUILT PER-CALL AND THROWN AWAY. About twenty places do
// `new ModelRegistryService()`, most of them `using var` - DeviceBrain,
// DeviceSetup six times over, CircleAISession, CircleAIListener,
// CircleAISpeaker, the TTS probe, the sweep, BundleModelLoader,
// DeviceAwareModelSelector, the abilities screen. A catalogue held in an
// instance field therefore reached exactly one of them, for as long as that one
// lived, which is no time at all.
//
// So "keep the model options updated" had to mean something stronger than
// "update the object that asked": it has to reach the instance built by the NEXT
// screen, which does not exist yet when the refresh happens. These pin that.
//
// NO NETWORK IS TOUCHED HERE. RefreshAsync is the only thing that would reach
// ModelScope and nothing below calls it; Offer is the same publishing path with
// the fetch taken out, which is also why Offer is public - a host on a mirror
// uses exactly this door.

using System;
using System.Collections.Generic;
using System.Linq;
using CircleAI.Core;
using CircleAI.Core.Models;
using Xunit;

namespace CircleAI.Tests;

/// <summary>
/// Runs alone, because the live catalogue is process-wide state.
/// </summary>
/// <remarks>
/// THIS WOULD OTHERWISE BE A FLAKE FACTORY. Eight other test files read
/// ModelRegistryService.AllModels - DeviceAwareModelSelectorTests,
/// SpeechModelSelectorTests, ModelUpgradeTests, SelectionQualityTests and the
/// rest - and xUnit runs collections in parallel. A test here publishing a live
/// catalogue would put two extra models under their feet mid-assertion, and the
/// failure would land in THEIR file, intermittently, with nothing pointing back
/// here.
/// <para>
/// DisableParallelization is the only honest answer: the state being tested is
/// genuinely global, so the test of it cannot be concurrent with anything that
/// reads it. Twelve tests in this repo were failing under load for a related
/// reason (see the deadline comments in Circle34LoopbackRealtimeTests); adding
/// a thirteenth while fixing those would be careless.
/// </para>
/// </remarks>
[CollectionDefinition("catalogue", DisableParallelization = true)]
public sealed class CatalogueCollection { }

[Collection("catalogue")]
public class ModelCatalogueTests : IDisposable
{
    // The live catalogue is process-wide by design, so a test that publishes one
    // must put it back or it leaks into every other test in the run.
    public ModelCatalogueTests() => ModelCatalogue.Forget();
    public void Dispose() => ModelCatalogue.Forget();

    [Fact]
    public void A_refresh_reaches_a_registry_that_did_not_exist_when_it_happened()
    {
        // THE WHOLE POINT. The screen that will ask has not been opened yet.
        ModelCatalogue.Offer(Catalogue(Entry("Qwen4-2B-MNN", 1_800_000_000)));

        using var builtAfterwards = new ModelRegistryService();

        Assert.Contains(builtAfterwards.AllModels, m => m.Name == "Qwen4-2B-MNN");
    }

    [Fact]
    public void Two_registries_built_separately_see_the_same_catalogue()
    {
        ModelCatalogue.Offer(Catalogue(Entry("Qwen4-2B-MNN", 1_800_000_000)));

        using var one = new ModelRegistryService();
        using var two = new ModelRegistryService();

        Assert.Equal(one.AllModels.Count, two.AllModels.Count);
        Assert.Contains(one.AllModels, m => m.Name == "Qwen4-2B-MNN");
        Assert.Contains(two.AllModels, m => m.Name == "Qwen4-2B-MNN");
    }

    [Fact]
    public void The_shipped_catalogue_survives_a_refresh_intact()
    {
        using var before = new ModelRegistryService();
        var shipped = before.AllModels.Select(m => m.Name).ToList();

        Assert.NotEmpty(shipped);   // the embedded resource really is loaded

        ModelCatalogue.Offer(Catalogue(Entry("Qwen4-2B-MNN", 1_800_000_000)));

        using var after = new ModelRegistryService();
        var now = after.AllModels.Select(m => m.Name).ToList();

        Assert.All(shipped, name => Assert.Contains(name, now));
        Assert.Equal(shipped.Count + 1, now.Count);
    }

    [Fact]
    public void The_voices_and_the_ears_are_still_there_after_a_chat_only_refresh()
    {
        // A ModelScope MNN query returns chat and vision. If a refresh could
        // replace rather than add, this is the assertion that would fail - and
        // the phone would have lost its voice and its ears.
        using var before = new ModelRegistryService();
        var voices = before.AllModels.Count(m => m.Modality == ModelModality.Tts);
        var ears   = before.AllModels.Count(m => m.Modality == ModelModality.Asr);
        var wake   = before.AllModels.Count(m => m.Modality == ModelModality.WakeWord);

        Assert.True(voices > 0 && ears > 0 && wake > 0, "the shipped catalogue should have all three");

        ModelCatalogue.Offer(Catalogue(
            Entry("Qwen4-2B-MNN", 1_800_000_000),
            Entry("Qwen4-VL-3B-MNN", 2_600_000_000)));

        using var after = new ModelRegistryService();

        Assert.Equal(voices, after.AllModels.Count(m => m.Modality == ModelModality.Tts));
        Assert.Equal(ears,   after.AllModels.Count(m => m.Modality == ModelModality.Asr));
        Assert.Equal(wake,   after.AllModels.Count(m => m.Modality == ModelModality.WakeWord));
    }

    [Fact]
    public void An_empty_catalogue_is_refused_rather_than_published()
    {
        // What a rate-limit page, a partial fetch or a changed API parses to.
        // Harmless to merge and a silent lie about what arrived.
        ModelCatalogue.Offer(Catalogue());

        Assert.False(ModelCatalogue.HasLive);
    }

    [Fact]
    public void A_null_catalogue_is_refused()
    {
        ModelCatalogue.Offer(null);

        Assert.False(ModelCatalogue.HasLive);
    }

    [Fact]
    public void Forgetting_it_goes_back_to_what_shipped()
    {
        using var before = new ModelRegistryService();
        var shipped = before.AllModels.Count;

        ModelCatalogue.Offer(Catalogue(Entry("Qwen4-2B-MNN", 1_800_000_000)));
        Assert.True(ModelCatalogue.HasLive);

        ModelCatalogue.Forget();

        using var after = new ModelRegistryService();
        Assert.False(ModelCatalogue.HasLive);
        Assert.Equal(shipped, after.AllModels.Count);
    }

    [Fact]
    public void A_discovered_model_arrives_ranked_and_therefore_choosable()
    {
        ModelCatalogue.Offer(Catalogue(Entry("Qwen4-2B-MNN", 1_800_000_000)));

        using var registry = new ModelRegistryService();
        var found = registry.AllModels.Single(m => m.Name == "Qwen4-2B-MNN");

        Assert.True(found.QualityRank > 0);
    }

    [Fact]
    public void With_no_refresh_the_catalogue_is_exactly_what_shipped()
    {
        // The behaviour of a phone that never reaches the network, which is the
        // behaviour most of these phones will actually have.
        using var registry = new ModelRegistryService();

        Assert.False(ModelCatalogue.HasLive);
        Assert.NotEmpty(registry.AllModels);
    }

    private static ModelRegistry Catalogue(params ModelEntry[] models)
        => new("test", DateTime.UtcNow, [.. models]);

    private static ModelEntry Entry(string name, long bytes)
        => new(name, "1", "MNN") { TotalBytes = bytes, Modality = ModelModality.Chat };
}
