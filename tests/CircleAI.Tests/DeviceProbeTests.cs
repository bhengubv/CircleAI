// DeviceProbeTests.cs
//
// Guards the mobile-memory fix. DeviceProbe.Snapshot's platform-neutral RAM
// reading is GC.GetGCMemoryInfo().TotalAvailableMemoryBytes — the GC HEAP
// LIMIT, which in an Android app sandbox is ~100 MB. On a 3 GB phone that
// classified the device as a Wearable and made EVERY model come back
// NothingFits (found by running the on-device capability sweep on a Huawei).
//
// The fix is a platform hook + explicit overrides so a head that CAN read real
// hardware injects it. These tests pin the precedence and the tier maths.

using System;
using CircleAI.Core;
using Xunit;

namespace CircleAI.Tests;

public sealed class DeviceProbeTests
{
    private const long Gb = 1024L * 1024 * 1024;

    [Fact]
    public void Snapshot_ExplicitOverrides_WinOverHeuristics_AndTierIsPhoneNotWearable()
    {
        var p = DeviceProbe.Snapshot(ramBytesOverride: 4 * Gb, storageBytesOverride: 20 * Gb);

        Assert.Equal(4 * Gb, p.RamAvailableBytes);
        Assert.Equal(20 * Gb, p.StorageFreeBytes);
        // 4 GB → Phone. Before the fix a phone read ~100 MB → Wearable.
        Assert.Equal(DeviceTier.Phone, p.Classify());
    }

    [Fact]
    public void Snapshot_PlatformHook_SuppliesRealHardware_WhenNoExplicitOverride()
    {
        var prev = DeviceProbe.PlatformMemoryProbe;
        try
        {
            DeviceProbe.PlatformMemoryProbe = () => new DeviceProbe.PlatformMemory(3 * Gb, 8 * Gb);

            var p = DeviceProbe.Snapshot();

            Assert.Equal(3 * Gb, p.RamAvailableBytes);
            Assert.Equal(8 * Gb, p.StorageFreeBytes);
            Assert.Equal(DeviceTier.Phone, p.Classify());
        }
        finally { DeviceProbe.PlatformMemoryProbe = prev; }
    }

    [Fact]
    public void Snapshot_ExplicitOverride_BeatsThePlatformHook()
    {
        var prev = DeviceProbe.PlatformMemoryProbe;
        try
        {
            DeviceProbe.PlatformMemoryProbe = () => new DeviceProbe.PlatformMemory(1 * Gb, 1 * Gb);

            var p = DeviceProbe.Snapshot(ramBytesOverride: 8 * Gb);

            Assert.Equal(8 * Gb, p.RamAvailableBytes);   // explicit wins over the hook
        }
        finally { DeviceProbe.PlatformMemoryProbe = prev; }
    }

    [Fact]
    public void Snapshot_NoHookNoOverride_StillProducesAUsableProbe()
    {
        // Desktop / server path: no hook set, heuristics apply, and the probe is
        // still valid (non-negative counts). This is the case the hook exists to
        // fix ONLY on mobile — it must not change desktop behaviour.
        var prev = DeviceProbe.PlatformMemoryProbe;
        try
        {
            DeviceProbe.PlatformMemoryProbe = null;
            var p = DeviceProbe.Snapshot();

            Assert.True(p.RamAvailableBytes >= 0);
            Assert.True(p.StorageFreeBytes >= 0);
            Assert.True(p.CpuCores >= 1);
        }
        finally { DeviceProbe.PlatformMemoryProbe = prev; }
    }

    [Fact]
    public void Tier_UsesTotalRam_ButFitUsesFreeRam_SoABusyPhoneStaysAPhone()
    {
        // The OOM found on the Huawei: a 3.6 GB phone with ~1.5 GB free must still
        // classify as Phone (device class = TOTAL), yet only be offered models that
        // fit the free RAM (~1.5 GB), never the full 3.6 GB. Reporting total as
        // "available" is what made the selector pick a 4 B model and get OOM-killed.
        var p = DeviceProbe.Snapshot(
            ramBytesOverride:      (long)(1.5 * Gb),   // free RAM  → fit
            storageBytesOverride:  20 * Gb,
            ramTotalBytesOverride: (long)(3.6 * Gb));  // total RAM → tier

        Assert.Equal(DeviceTier.Phone, p.Classify());        // class from total, not free
        Assert.Equal((long)(1.5 * Gb), p.RamAvailableBytes); // fit gate = free RAM
        Assert.Equal((long)(3.6 * Gb), p.RamTotalBytes);
    }

    [Fact]
    public void UsableRamGb_ReservesHeadroomBelowFreeRam()
    {
        // Fit must NOT commit 100% of free RAM — the KV cache grows during
        // generation, so a model that fits at load can still OOM mid-output.
        // UsableRamGb scales free RAM by RamFitHeadroom (0.85 → reserve ~15%).
        //
        // Stated in the CATALOGUE's unit, which is the only one that means
        // anything here: this number exists to be compared against MinRamGb.
        // Writing it in GiB — as this test used to — is what let the two drift.
        const long twoGb = (long)(2 * DeviceProbe.BytesPerGb);
        var p = DeviceProbe.Snapshot(ramBytesOverride: twoGb);

        Assert.True(p.UsableRamGb < 2.0);
        Assert.Equal(2.0 * DeviceProbe.RamFitHeadroom, p.UsableRamGb, 3);
    }

    [Fact]
    public void TheFitCheckReservesExactlyTheHeadroomItAdvertises()
    {
        // The bug this pins: MinRamGb is derived from file bytes / 10^9, while
        // every device-side number was bytes / 2^30, and the two were compared to
        // each other. A GiB figure is ~7% numerically smaller than the same
        // quantity in GB, so the check really demanded a model fit in 79.2% of
        // free RAM while the constant beside it said 85%.
        //
        // It failed silently and in the mean direction — models REFUSED that would
        // have run — so the cheapest phones were told a capability was unavailable
        // when it was not. Nothing crashed, so nothing pointed at it.
        const long freeBytes = 4_000_000_000;
        var p = DeviceProbe.Snapshot(ramBytesOverride: freeBytes);

        // A model needing exactly the advertised share of free RAM must fit...
        var exactlyTheBudget = freeBytes * DeviceProbe.RamFitHeadroom / DeviceProbe.BytesPerGb;
        Assert.True(exactlyTheBudget <= p.UsableRamGb + 0.0001,
            $"a model needing {exactlyTheBudget:0.####} GB was refused {p.UsableRamGb:0.####} GB of budget");

        // ...and a hair more must not.
        Assert.False(exactlyTheBudget * 1.01 <= p.UsableRamGb);
    }

    [Fact]
    public void AGuessedMemoryFigureSaysSoInsteadOfPassingAsMeasured()
    {
        // The root cause of the whole mobile-memory class of bug: a probe that
        // GUESSED was indistinguishable from one that MEASURED, so every verdict
        // downstream was stated with full confidence about the GC heap limit
        // (~100 MB in an Android sandbox). Nothing said the input was invented.
        try
        {
            DeviceProbe.PlatformMemoryProbe = null;
            var guessed = DeviceProbe.Snapshot();
            Assert.Equal(DeviceProbe.RamMeasurement.Heuristic, guessed.RamSource);

            // A head that reads the hardware is recorded as such, and says nothing.
            DeviceProbe.PlatformMemoryProbe = () =>
                new DeviceProbe.PlatformMemory(3_000_000_000, 20_000_000_000, 3_600_000_000);
            var measured = DeviceProbe.Snapshot();

            Assert.Equal(DeviceProbe.RamMeasurement.PlatformMeasured, measured.RamSource);
            Assert.Null(measured.MeasurementWarning);
        }
        finally { DeviceProbe.PlatformMemoryProbe = null; }
    }

    [Fact]
    public void TheWarningFiresOnlyWhenTheGuessIsImplausible()
    {
        // Narrow on purpose. The heuristic is fine on desktop and server, where it
        // returns GB-scale numbers — warning there is noise people learn to skip.
        // It must fire on the actual signature of the bug: an INFERRED figure too
        // small for any real device.
        var phoneSandbox = new DeviceProbe(
            RamAvailableBytes: 100L * 1024 * 1024,     // the ~100 MB heap limit
            StorageFreeBytes:  8L * 1024 * 1024 * 1024,
            Gpu: GpuKind.None, CpuCores: 8,
            Thermal: ThermalClass.Passive, Connectivity: Connectivity.Online)
        { RamSource = DeviceProbe.RamMeasurement.Heuristic };

        Assert.NotNull(phoneSandbox.MeasurementWarning);
        Assert.Contains("PlatformMemoryProbe", phoneSandbox.MeasurementWarning);

        // Same heuristic, a desktop-sized answer: nothing to complain about.
        var desktop = phoneSandbox with { RamAvailableBytes = 16L * 1024 * 1024 * 1024 };
        Assert.Null(desktop.MeasurementWarning);

        // And a figure somebody stated outright is not a guess, however small.
        var stated = phoneSandbox with { RamSource = DeviceProbe.RamMeasurement.Explicit };
        Assert.Null(stated.MeasurementWarning);
    }

    [Fact]
    public void StorageIsMeasuredInTheSameUnitAsTheCatalogue()
    {
        // Five call sites each divided StorageFreeBytes by 2^30 for themselves,
        // then compared the result against MinStorageGb, which comes from bytes /
        // 10^9. One shared property is the only version that cannot go out of step.
        const long freeBytes = 32_000_000_000;
        var p = DeviceProbe.Snapshot(storageBytesOverride: freeBytes);

        Assert.Equal(32.0, p.StorageFreeGb, 3);
    }
}
