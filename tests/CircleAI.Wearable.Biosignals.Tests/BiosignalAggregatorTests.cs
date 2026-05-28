// BiosignalAggregatorTests.cs

using System.Runtime.CompilerServices;
using CircleAI.Wearable.Biosignals;
using Xunit;

namespace CircleAI.Wearable.Biosignals.Tests;

public sealed class BiosignalAggregatorTests
{
    [Fact]
    public async Task SnapshotAsync_OverSyntheticSource_ComputesMinMaxMean()
    {
        var now = DateTimeOffset.UtcNow;
        var src = new SyntheticBiosignalSource(new[]
        {
            new BiosignalSample(Guid.NewGuid(), BiosignalKind.HeartRate, 60f, "bpm", 1f, false, now),
            new BiosignalSample(Guid.NewGuid(), BiosignalKind.HeartRate, 80f, "bpm", 1f, false, now),
            new BiosignalSample(Guid.NewGuid(), BiosignalKind.HeartRate, 100f, "bpm", 1f, false, now),
            new BiosignalSample(Guid.NewGuid(), BiosignalKind.OxygenSaturation, 95f, "%", 1f, false, now),
            new BiosignalSample(Guid.NewGuid(), BiosignalKind.OxygenSaturation, 97f, "%", 1f, false, now),
        });
        var agg = new BiosignalAggregator(src);

        var snap = await agg.SnapshotAsync(TimeSpan.FromMinutes(5), CancellationToken.None);

        Assert.Equal(2, snap.Stats.Count);

        var hr = snap.Stats[BiosignalKind.HeartRate];
        Assert.Equal(3, hr.SampleCount);
        Assert.Equal(60f, hr.Min);
        Assert.Equal(100f, hr.Max);
        Assert.Equal(80f, hr.Mean, 1e-5f);

        var sp = snap.Stats[BiosignalKind.OxygenSaturation];
        Assert.Equal(2, sp.SampleCount);
        Assert.Equal(95f, sp.Min);
        Assert.Equal(97f, sp.Max);
        Assert.Equal(96f, sp.Mean, 1e-5f);
    }

    [Fact]
    public async Task SnapshotAsync_DropsSamplesOlderThanWindow()
    {
        var now = DateTimeOffset.UtcNow;
        var src = new SyntheticBiosignalSource(new[]
        {
            new BiosignalSample(Guid.NewGuid(), BiosignalKind.HeartRate, 200f, "bpm", 1f, false, now.AddHours(-2)),
            new BiosignalSample(Guid.NewGuid(), BiosignalKind.HeartRate, 60f,  "bpm", 1f, false, now),
        });
        var agg = new BiosignalAggregator(src);

        var snap = await agg.SnapshotAsync(TimeSpan.FromMinutes(5), CancellationToken.None);

        var hr = snap.Stats[BiosignalKind.HeartRate];
        Assert.Equal(1, hr.SampleCount);
        Assert.Equal(60f, hr.Min);
        Assert.Equal(60f, hr.Max);
    }

    [Fact]
    public async Task SnapshotAsync_EmptySource_ReturnsEmptyStats()
    {
        var agg = new BiosignalAggregator(new NullBiosignalSource());

        var snap = await agg.SnapshotAsync(TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Empty(snap.Stats);
    }

    private sealed class SyntheticBiosignalSource : IBiosignalSource
    {
        private readonly BiosignalSample[] _samples;

        public SyntheticBiosignalSource(BiosignalSample[] samples) => _samples = samples;

        public BiosignalKind[] SupportedKinds => new[] { BiosignalKind.HeartRate, BiosignalKind.OxygenSaturation };

        public Task<bool> IsSupportedAsync(BiosignalKind kind, CancellationToken cancellationToken) =>
            Task.FromResult(Array.IndexOf(SupportedKinds, kind) >= 0);

        public async IAsyncEnumerable<BiosignalSample> StreamAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (var s in _samples)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return s;
                await Task.Yield();
            }
        }
    }
}
