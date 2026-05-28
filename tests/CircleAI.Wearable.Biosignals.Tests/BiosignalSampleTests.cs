// BiosignalSampleTests.cs
//
// Construction and clamping behaviour for BiosignalSample.

using CircleAI.Wearable.Biosignals;
using Xunit;

namespace CircleAI.Wearable.Biosignals.Tests;

public sealed class BiosignalSampleTests
{
    [Fact]
    public void Create_ClampsConfidenceAboveOne_ToOne()
    {
        var s = BiosignalSample.Create(BiosignalKind.HeartRate, 72f, "bpm", confidence: 2.5f);

        Assert.Equal(1f, s.Confidence);
    }

    [Fact]
    public void Create_ClampsConfidenceBelowZero_ToZero()
    {
        var s = BiosignalSample.Create(BiosignalKind.HeartRate, 72f, "bpm", confidence: -0.4f);

        Assert.Equal(0f, s.Confidence);
    }

    [Fact]
    public void Create_AssignsFreshGuidAndUtcTimestamp()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var s = BiosignalSample.Create(BiosignalKind.OxygenSaturation, 97f, "%");
        var after = DateTimeOffset.UtcNow.AddSeconds(1);

        Assert.NotEqual(Guid.Empty, s.Id);
        Assert.InRange(s.MeasuredAt, before, after);
        Assert.Equal(BiosignalKind.OxygenSaturation, s.Kind);
        Assert.Equal(97f, s.Value);
        Assert.Equal("%", s.Unit);
        Assert.False(s.IsCumulative);
    }
}
