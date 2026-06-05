// ModelLifecycleManagerTests.cs
//
// Verifies admission gate logic and bookkeeping invariants of the
// default IModelLifecycleManager. Uses a stub capability probe so the
// test surface is deterministic.

using System.Threading;
using System.Threading.Tasks;
using CircleAI.Inference.Server.Lifecycle;
using CircleAI.Inference.Server.Models;
using CircleAI.Inference.Server.Tests.TestFixtures;
using CircleAI.Runtime.Backends;
using CircleAI.Runtime.Capabilities;
using Xunit;

namespace CircleAI.Inference.Server.Tests;

public sealed class ModelLifecycleManagerTests
{
    private const long GiB = 1024L * 1024 * 1024;

    private static HostProfile MakeProfile(long ram = 32 * GiB, long vram = 16 * GiB)
    {
        var gpu = vram > 0
            ? new GpuInfo(GpuVendor.Nvidia, "Test RTX", vram, "test-driver")
            : null;
        return new HostProfile(
            OperatingSystemKind.Linux, "test", ArchitectureKind.X64,
            "TestCpu", 16, 8, ram, gpu, null,
            new System.DateTimeOffset(2026, 6, 1, 0, 0, 0, System.TimeSpan.Zero));
    }

    private sealed class StubProbe : ICapabilityProbe
    {
        private readonly HostProfile _p;
        public StubProbe(HostProfile p) => _p = p;
        public Task<HostProfile> ProbeAsync(CancellationToken ct = default) => Task.FromResult(_p);
    }

    private static IInferenceServerModelRegistry NewRegistry() => new InferenceServerModelRegistry();

    [Fact]
    public async Task Load_Admits_When_Vram_And_Ram_Have_Headroom()
    {
        var mgr = new ModelLifecycleManager(NewRegistry(), new StubProbe(MakeProfile()));
        var d   = new ModelLoadDescriptor("qwen-test",
            BackendKind.Cuda, CapabilityTier.Tier2_Medium,
            VramRequiredBytes: 4 * GiB,
            RamRequiredBytes:  4 * GiB,
            BridgeFactory: _ => Task.FromResult<CircleAI.Hosting.InferenceBridge.IInferenceBridge>(
                new StubInferenceBridge("qwen-test")));

        var result = await mgr.LoadAsync(d);
        Assert.Equal(LoadOutcome.Loaded, result.Outcome);
        Assert.NotNull(result.State);
        Assert.Equal(4 * GiB, mgr.TotalAllocatedVramBytes);
        Assert.Equal(4 * GiB, mgr.TotalAllocatedRamBytes);
    }

    [Fact]
    public async Task Load_Rejects_When_Vram_Is_Insufficient()
    {
        var mgr = new ModelLifecycleManager(NewRegistry(),
            new StubProbe(MakeProfile(vram: 4 * GiB)));
        var d = new ModelLoadDescriptor("oversize",
            BackendKind.Cuda, CapabilityTier.Tier4_Frontier,
            VramRequiredBytes: 24 * GiB,
            RamRequiredBytes:  0,
            BridgeFactory: _ => Task.FromResult<CircleAI.Hosting.InferenceBridge.IInferenceBridge>(
                new StubInferenceBridge("oversize")));

        var result = await mgr.LoadAsync(d);
        Assert.Equal(LoadOutcome.InsufficientVram, result.Outcome);
        Assert.Null(result.State);
        Assert.Equal(0, mgr.TotalAllocatedVramBytes);
        Assert.Contains("Need", result.Rationale);
    }

    [Fact]
    public async Task Load_Rejects_When_Ram_Is_Insufficient()
    {
        var mgr = new ModelLifecycleManager(NewRegistry(),
            new StubProbe(MakeProfile(ram: 4 * GiB, vram: 0)));
        var d = new ModelLoadDescriptor("ramhog",
            BackendKind.Cpu, CapabilityTier.Tier4_Frontier,
            VramRequiredBytes: 0,
            RamRequiredBytes:  16 * GiB,
            BridgeFactory: _ => Task.FromResult<CircleAI.Hosting.InferenceBridge.IInferenceBridge>(
                new StubInferenceBridge("ramhog")));

        var result = await mgr.LoadAsync(d);
        Assert.Equal(LoadOutcome.InsufficientRam, result.Outcome);
    }

    [Fact]
    public async Task Second_Load_Of_Same_Id_Is_AlreadyLoaded_NoOp()
    {
        var mgr = new ModelLifecycleManager(NewRegistry(), new StubProbe(MakeProfile()));
        var d = new ModelLoadDescriptor("idempotent",
            BackendKind.Cpu, CapabilityTier.Tier1_Small,
            0, 1 * GiB,
            BridgeFactory: _ => Task.FromResult<CircleAI.Hosting.InferenceBridge.IInferenceBridge>(
                new StubInferenceBridge("idempotent")));

        var first  = await mgr.LoadAsync(d);
        var second = await mgr.LoadAsync(d);
        Assert.Equal(LoadOutcome.Loaded,        first.Outcome);
        Assert.Equal(LoadOutcome.AlreadyLoaded, second.Outcome);
        Assert.Single(mgr.List());
    }

    [Fact]
    public async Task Unload_Removes_State_And_Frees_Vram_And_Ram()
    {
        var registry = NewRegistry();
        var mgr = new ModelLifecycleManager(registry, new StubProbe(MakeProfile()));
        var d = new ModelLoadDescriptor("free-me",
            BackendKind.Cuda, CapabilityTier.Tier2_Medium,
            VramRequiredBytes: 4 * GiB,
            RamRequiredBytes:  2 * GiB,
            BridgeFactory: _ => Task.FromResult<CircleAI.Hosting.InferenceBridge.IInferenceBridge>(
                new StubInferenceBridge("free-me")));

        await mgr.LoadAsync(d);
        Assert.True(mgr.TotalAllocatedVramBytes > 0);

        var outcome = await mgr.UnloadAsync("free-me");
        Assert.Equal(UnloadOutcome.Unloaded, outcome);
        Assert.Equal(0, mgr.TotalAllocatedVramBytes);
        Assert.Equal(0, mgr.TotalAllocatedRamBytes);
        Assert.Empty(mgr.List());
        Assert.Null(registry.Resolve("free-me"));
    }

    [Fact]
    public async Task Unload_Unknown_Id_Returns_NotLoaded()
    {
        var mgr = new ModelLifecycleManager(NewRegistry(), new StubProbe(MakeProfile()));
        var outcome = await mgr.UnloadAsync("phantom");
        Assert.Equal(UnloadOutcome.NotLoaded, outcome);
    }

    [Fact]
    public async Task Load_Factory_Failure_Rolls_Back_Reservation()
    {
        var mgr = new ModelLifecycleManager(NewRegistry(), new StubProbe(MakeProfile()));
        var d = new ModelLoadDescriptor("broken",
            BackendKind.Cuda, CapabilityTier.Tier2_Medium,
            VramRequiredBytes: 4 * GiB,
            RamRequiredBytes:  2 * GiB,
            BridgeFactory: _ => throw new System.InvalidOperationException("disk full"));

        var result = await mgr.LoadAsync(d);
        Assert.Equal(LoadOutcome.FactoryFailed, result.Outcome);
        Assert.Equal(0, mgr.TotalAllocatedVramBytes);
        Assert.Equal(0, mgr.TotalAllocatedRamBytes);
        Assert.Empty(mgr.List());
    }

    [Fact]
    public async Task Cpu_Backend_Does_Not_Need_Vram()
    {
        // Profile has zero VRAM; CPU load must succeed regardless.
        var mgr = new ModelLifecycleManager(NewRegistry(),
            new StubProbe(MakeProfile(vram: 0)));
        var d = new ModelLoadDescriptor("cpu-only",
            BackendKind.Cpu, CapabilityTier.Tier1_Small,
            VramRequiredBytes: 8 * GiB, // ignored on CPU backend
            RamRequiredBytes:  2 * GiB,
            BridgeFactory: _ => Task.FromResult<CircleAI.Hosting.InferenceBridge.IInferenceBridge>(
                new StubInferenceBridge("cpu-only")));

        var result = await mgr.LoadAsync(d);
        Assert.Equal(LoadOutcome.Loaded, result.Outcome);
    }

    [Fact]
    public async Task Total_Allocations_Sum_Across_Multiple_Loads()
    {
        var mgr = new ModelLifecycleManager(NewRegistry(), new StubProbe(MakeProfile()));
        await mgr.LoadAsync(new ModelLoadDescriptor("a",
            BackendKind.Cuda, CapabilityTier.Tier1_Small, 2 * GiB, 1 * GiB,
            _ => Task.FromResult<CircleAI.Hosting.InferenceBridge.IInferenceBridge>(new StubInferenceBridge("a"))));
        await mgr.LoadAsync(new ModelLoadDescriptor("b",
            BackendKind.Cuda, CapabilityTier.Tier2_Medium, 6 * GiB, 4 * GiB,
            _ => Task.FromResult<CircleAI.Hosting.InferenceBridge.IInferenceBridge>(new StubInferenceBridge("b"))));

        Assert.Equal(8 * GiB, mgr.TotalAllocatedVramBytes);
        Assert.Equal(5 * GiB, mgr.TotalAllocatedRamBytes);
        Assert.Equal(2, mgr.List().Count);
    }
}
