// DeviceAwareModelSelectorTests.cs
//
// Locks in the behaviour of the SDK's headline promise: "the consumer states
// intent, the SDK picks the model that fits THIS device."
//
// These tests run against the REAL embedded registry deliberately — the whole
// class of bug they exist to catch is metadata drift, and a fake registry would
// hide exactly that. Two real defects motivated them:
//
//   1. No registry entry declared Capabilities, so ParseCapabilities fell back
//      to Default-only and every Tools/Reasoning/LongContext request THREW.
//   2. No entry declared MinRamGb, so the device-fit gate defaulted to 0 and
//      passed everything — a 1.1 GB phone was offered a 12 GB model.

using System;
using CircleAI.Core;
using CircleAI.Core.Models;
using System.Linq;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

// Named for the registry, not the class under test: P0AtomicShiftTests.cs already
// holds a DeviceAwareModelSelectorTests that drives the selector through an
// InMemoryRegistry. That suite proves the ORDERING LOGIC against synthetic
// entries; this one proves the SHIPPED METADATA is correct. Both are needed —
// synthetic entries cannot catch a registry that declares no capabilities.
public sealed class DeviceAwareModelSelectorRegistryTests
{
    /// <summary>A synthetic device. RAM is what the fit gate actually keys on.</summary>
    private static DeviceProbe Device(double ramGb, double storageGb = 32) =>
        new(
            RamAvailableBytes: (long)(ramGb * 1024 * 1024 * 1024),
            StorageFreeBytes:  (long)(storageGb * 1024 * 1024 * 1024),
            Gpu:               GpuKind.None,
            CpuCores:          8,
            Thermal:           ThermalClass.Passive,
            Connectivity:      Connectivity.Online);

    // ── capability resolution ────────────────────────────────────────────────

    [Fact]
    public void Tools_Resolves()
    {
        // Regression: this threw "No model in the registry satisfies required
        // capabilities 'Tools'" because no entry declared any capability.
        var pick = new DeviceAwareModelSelector().BestFit(Device(1.1), ChatCapability.Tools);
        Assert.False(string.IsNullOrWhiteSpace(pick.ModelId));
    }

    [Fact]
    public void Reasoning_NeverSelectsAModelWithoutThinkingMode()
    {
        // Only the Qwen3 line declares Reasoning; Qwen2.5-Instruct must never win.
        //
        // ASSERTS THE CAPABILITY, NOT THE NAME. This used to check the id started
        // with "Qwen3-", which was a proxy for "declares Reasoning" and held right
        // up until Qwen3.5 arrived — a thinking model whose name does not match
        // that prefix. The proxy failed while the invariant was still perfectly
        // true, which is a test reporting on its own spelling rather than on the
        // behaviour it guards.
        var pick = new DeviceAwareModelSelector().BestFit(Device(3.4), ChatCapability.Reasoning);
        var entry = new ModelRegistryService().AllModels
            .Single(m => string.Equals(m.Name, pick.ModelId, StringComparison.Ordinal));

        Assert.Contains("Reasoning", entry.Capabilities, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Vision_ThrowsUntilAVisionModelIsCatalogued()
    {
        // Documents an HONEST hole: there is no vision model in the registry, so
        // this must fail loudly. If someone "fixes" it by declaring Vision on a
        // text model, this test is the thing that should stop them.
        var ex = Assert.Throws<InvalidOperationException>(
            () => new DeviceAwareModelSelector().BestFit(Device(8), ChatCapability.Vision));
        Assert.Contains("capabilit", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── device fit ───────────────────────────────────────────────────────────

    [Fact]
    public void CheapPhone_GetsTheSmallQwen3()
    {
        // ~1.1 GB free is the measured Huawei P30 Lite / Redmi Note 9 case.
        //
        // ASSERTS THE FIT, NOT THE NAME. This pinned "Qwen3-0.6B-MNN" and broke
        // the day a better model at the same size was catalogued — it now picks
        // Qwen3.5-0.8B, which is the selector doing exactly its job. A test that
        // fails when the answer IMPROVES is guarding the catalogue's contents
        // rather than the selector's behaviour.
        //
        // What must stay true: whatever it picks has to fit in what the phone
        // actually has free, and has to be the best-ranked thing that does.
        var selector = new DeviceAwareModelSelector();
        var pick     = selector.BestFit(Device(1.1), ChatCapability.Default);

        var chosen = new ModelRegistryService().AllModels
            .Single(m => string.Equals(m.Name, pick.ModelId, StringComparison.Ordinal));

        Assert.True(chosen.MinRamGb <= 1.1,
            $"{chosen.Name} wants {chosen.MinRamGb} GB on a 1.1 GB handset");

        // Compared against the SELECTOR'S OWN candidate list, not the whole
        // registry. Re-deriving "what counts as a chat model" in the test means
        // maintaining a second copy of the selector's rules, and mine was
        // already wrong twice — it accused the selector of passing over a
        // text-to-speech voice, and then a wake-word bundle, both of which
        // declare Default and fit in 1.1 GB and are not conversations.
        var betterAndRunnable = selector.AllCandidates(Device(1.1))
            .Where(c => c.Quality == SelectionQuality.Good && c.ModelId != pick.ModelId)
            .Select(c => c.ModelId)
            .Where(id => new ModelRegistryService().AllModels
                .Single(m => string.Equals(m.Name, id, StringComparison.Ordinal))
                .QualityRank > chosen.QualityRank)
            .ToList();

        Assert.True(betterAndRunnable.Count == 0,
            "a better model also runs here and was passed over: "
            + string.Join(", ", betterAndRunnable));
    }

    [Fact]
    public void CheapPhone_IsNeverOfferedAModelItCannotHold()
    {
        // Regression: with MinRamGb absent the fit gate was inert and this
        // returned the 14B on a 1.1 GB handset.
        var pick = new DeviceAwareModelSelector().BestFit(Device(1.1), ChatCapability.Default);
        Assert.NotEqual("Qwen3-14B-MNN", pick.ModelId);
        Assert.True(pick.EstimatedBytes < 1_000_000_000L,
            $"Picked '{pick.ModelId}' ({pick.EstimatedBytes} bytes) for a 1.1 GB device.");
    }

    [Fact]
    public void SelectionScalesWithTheDevice()
    {
        // The gate must be live in BOTH directions — not merely always-smallest.
        var selector = new DeviceAwareModelSelector();
        var small = selector.BestFit(Device(1.1), ChatCapability.Default);
        var big   = selector.BestFit(Device(16, storageGb: 64), ChatCapability.Default);

        Assert.NotEqual(small.ModelId, big.ModelId);
        Assert.True(big.EstimatedBytes > small.EstimatedBytes);
    }

    [Fact]
    public void WhenNothingFits_FallsBackToTheSmallest()
    {
        // BestFit's own comment: "when no entry fits, we fall back to the
        // smallest one rather than throwing. A wearable that can only run the
        // smallest model should still get the smallest model, not an exception."
        // The code ordered by QualityRank DESC and returned the LARGEST instead.
        var pick = new DeviceAwareModelSelector().BestFit(Device(0.25, storageGb: 1),
                                                          ChatCapability.Default);
        Assert.Equal("Qwen3-0.6B-MNN", pick.ModelId);
    }

    // ── fallback chain (RT-08 brownout) ──────────────────────────────────────

    [Fact]
    public void ChainFor_WalksTheFallbackChainDownToTheSmallest()
    {
        var chain = new DeviceAwareModelSelector().ChainFor("Qwen3-4B-MNN");
        Assert.Equal(
            new[] { "Qwen3-4B-MNN", "Qwen3-1.7B-MNN", "Qwen3-0.6B-MNN" },
            chain);
    }
}

