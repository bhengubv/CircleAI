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
        // Only the Qwen3 ladder declares Reasoning; Qwen2.5-Instruct must never win.
        var pick = new DeviceAwareModelSelector().BestFit(Device(3.4), ChatCapability.Reasoning);
        Assert.StartsWith("Qwen3-", pick.ModelId, StringComparison.Ordinal);
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
        var pick = new DeviceAwareModelSelector().BestFit(Device(1.1), ChatCapability.Default);
        Assert.Equal("Qwen3-0.6B-MNN", pick.ModelId);
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
