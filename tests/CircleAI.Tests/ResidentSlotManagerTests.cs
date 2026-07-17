// ResidentSlotManagerTests.cs — the two-slot admission gate + eviction.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Core;
using CircleAI.Hosting.Neuron;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public sealed class ResidentSlotManagerTests
{
    private static DeviceProbe Probe(long ramBytes) =>
        new(ramBytes, ramBytes, GpuKind.None, 8, ThermalClass.Active, Connectivity.Offline);

    private static ModelSelection Sel(string id, long bytes) =>
        new(id, RequiresDownload: false, EstimatedBytes: bytes, Tier: DeviceTier.Desktop);

    /// <summary>Generator that records disposal so eviction can be asserted.</summary>
    private sealed class TrackGen : IChatGenerator
    {
        private readonly string _reply;
        public TrackGen(string reply) => _reply = reply;
        public bool Disposed { get; private set; }

        public Task<string> GenerateAsync(IReadOnlyList<ChatMessage> m, GenerationOptions? o = null, CancellationToken ct = default)
            => Task.FromResult(_reply);

        public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> m, GenerationOptions? o = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield return _reply;
        }

        public void Dispose() => Disposed = true;
    }

    [Fact]
    public async Task Admits_WithinBudget()
    {
        await using var mgr = new ResidentSlotManager(generalistReservedBytes: 1_000, () => Probe(1_000_000));
        var g = new TrackGen("S");
        var a = await mgr.EnsureSpecialistAsync(Sel("spec", 5_000), (_, _) => Task.FromResult<IChatGenerator>(g));

        Assert.Equal(SlotOutcome.Admitted, a.Outcome);
        Assert.Same(g, a.Generator);
        Assert.Equal("spec", mgr.ResidentSpecialistModelId);
    }

    [Fact]
    public async Task Denies_OverBudget()
    {
        await using var mgr = new ResidentSlotManager(generalistReservedBytes: 900_000, () => Probe(1_000_000));
        var a = await mgr.EnsureSpecialistAsync(Sel("spec", 500_000),
            (_, _) => Task.FromResult<IChatGenerator>(new TrackGen("S")));

        Assert.Equal(SlotOutcome.InsufficientRam, a.Outcome);
        Assert.Null(a.Generator);
        Assert.Null(mgr.ResidentSpecialistModelId);
    }

    [Fact]
    public async Task SameModel_AlreadyResident_DoesNotRebuild()
    {
        await using var mgr = new ResidentSlotManager(0, () => Probe(1_000_000));
        var builds = 0;
        Task<IChatGenerator> Build(string id, CancellationToken ct)
        {
            builds++;
            return Task.FromResult<IChatGenerator>(new TrackGen("S"));
        }

        await mgr.EnsureSpecialistAsync(Sel("spec", 1), Build);
        var second = await mgr.EnsureSpecialistAsync(Sel("spec", 1), Build);

        Assert.Equal(SlotOutcome.AlreadyResident, second.Outcome);
        Assert.Equal(1, builds);
    }

    [Fact]
    public async Task Swap_EvictsIncumbentFirst()
    {
        await using var mgr = new ResidentSlotManager(0, () => Probe(1_000_000));
        var a = new TrackGen("A");
        var b = new TrackGen("B");

        await mgr.EnsureSpecialistAsync(Sel("A", 1), (_, _) => Task.FromResult<IChatGenerator>(a));
        await mgr.EnsureSpecialistAsync(Sel("B", 1), (_, _) => Task.FromResult<IChatGenerator>(b));

        Assert.True(a.Disposed);
        Assert.False(b.Disposed);
        Assert.Equal("B", mgr.ResidentSpecialistModelId);
    }

    [Fact]
    public async Task BuildFailure_Reported_SlotEmpty()
    {
        await using var mgr = new ResidentSlotManager(0, () => Probe(1_000_000));
        var a = await mgr.EnsureSpecialistAsync(Sel("spec", 1),
            (_, _) => throw new InvalidOperationException("boom"));

        Assert.Equal(SlotOutcome.BuildFailed, a.Outcome);
        Assert.Null(mgr.ResidentSpecialistModelId);
    }

    [Fact]
    public async Task Evict_DisposesAndEmptiesSlot()
    {
        await using var mgr = new ResidentSlotManager(0, () => Probe(1_000_000));
        var g = new TrackGen("S");
        await mgr.EnsureSpecialistAsync(Sel("spec", 1), (_, _) => Task.FromResult<IChatGenerator>(g));

        await mgr.EvictSpecialistAsync();

        Assert.True(g.Disposed);
        Assert.Null(mgr.ResidentSpecialistModelId);
    }
}
