// CapabilityProbeTests.cs
//
// Real OS probes are platform-gated and produce host-dependent values,
// so the deterministic test surface is the constructor-injected probe
// path and the public HostProfile invariants.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Runtime.Capabilities;
using Xunit;

namespace CircleAI.Runtime.Tests;

public sealed class CapabilityProbeTests
{
    private const long GiB = 1024L * 1024 * 1024;

    [Fact]
    public async Task Default_Probe_Returns_NonNull_Profile_With_Best_Effort_Values()
    {
        // Even on the build agent we should get *something* — the default
        // probe never throws and always produces a valid HostProfile.
        var probe = new CapabilityProbe();
        var p = await probe.ProbeAsync(CancellationToken.None);

        Assert.NotNull(p);
        Assert.NotEqual(OperatingSystemKind.Unknown, p.Os); // tests run on Win/Mac/Linux
        Assert.True(p.LogicalCoreCount > 0);
        Assert.False(string.IsNullOrWhiteSpace(p.CpuModel));
        Assert.True(p.ProbedAt <= DateTimeOffset.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task Constructor_With_Inner_Probe_Routes_To_It()
    {
        var inner = new StubProbe(new HostProfile(
            OperatingSystemKind.HarmonyOS, "5.0", ArchitectureKind.Arm64,
            "Kirin 9010", 8, 8, 12 * GiB,
            new GpuInfo(GpuVendor.Huawei, "Maleoon", 0, null),
            new NpuInfo(NpuVendor.HuaweiAscend, "Ascend Lite"),
            new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero)));

        var probe = new CapabilityProbe(inner);
        var p = await probe.ProbeAsync();
        Assert.Equal(OperatingSystemKind.HarmonyOS, p.Os);
        Assert.Equal(NpuVendor.HuaweiAscend, p.Npu!.Vendor);
    }

    [Fact]
    public async Task Probe_Honours_Cancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var probe = new CapabilityProbe(new ThrowingProbe());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.ProbeAsync(cts.Token));
    }

    [Fact]
    public void HostProfile_HasUsableGpu_Returns_False_For_Low_VRAM()
    {
        var p = new HostProfile(
            OperatingSystemKind.Linux, "5", ArchitectureKind.X64,
            "cpu", 1, 1, 8 * GiB,
            new GpuInfo(GpuVendor.Intel, "iGPU", 512L * 1024 * 1024, null),
            null, DateTimeOffset.UtcNow);
        Assert.False(p.HasUsableGpu());                  // default 2 GiB threshold
        Assert.True (p.HasUsableGpu(minimumVramBytes: 256L * 1024 * 1024));
    }

    [Fact]
    public void HostProfile_Is64Bit_Detects_X64_And_Arm64_And_Loong64()
    {
        Assert.True (Profile(ArchitectureKind.X64).Is64Bit);
        Assert.True (Profile(ArchitectureKind.Arm64).Is64Bit);
        Assert.True (Profile(ArchitectureKind.Loong64).Is64Bit);
        Assert.False(Profile(ArchitectureKind.X86).Is64Bit);
        Assert.False(Profile(ArchitectureKind.Arm).Is64Bit);
    }

    private static HostProfile Profile(ArchitectureKind a) =>
        new(OperatingSystemKind.Linux, "5", a, "cpu", 1, 1, GiB, null, null, DateTimeOffset.UtcNow);

    private sealed class StubProbe : ICapabilityProbe
    {
        private readonly HostProfile _p;
        public StubProbe(HostProfile p) => _p = p;
        public Task<HostProfile> ProbeAsync(CancellationToken ct = default) => Task.FromResult(_p);
    }

    private sealed class ThrowingProbe : ICapabilityProbe
    {
        public Task<HostProfile> ProbeAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Should not reach here when ct is already cancelled.");
        }
    }
}
