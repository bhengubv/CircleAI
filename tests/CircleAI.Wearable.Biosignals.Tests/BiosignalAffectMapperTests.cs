// BiosignalAffectMapperTests.cs
//
// Deterministic rule verification for the biosignal → AffectState projection.

using CircleAI.Memory;
using CircleAI.Wearable.Biosignals;
using Xunit;

namespace CircleAI.Wearable.Biosignals.Tests;

public sealed class BiosignalAffectMapperTests
{
    private const float Eps = 1e-5f;

    private static AffectState NeutralState() => new()
    {
        Curiosity   = 0.5f,
        Engagement  = 0.5f,
        Uncertainty = 0.2f,
        Rapport     = 0.5f,
        Energy      = 0.5f,
    };

    [Fact]
    public void HighHeartRate_Above100_RaisesEnergy_By005()
    {
        var a = NeutralState();
        var s = new BiosignalSample(Guid.NewGuid(), BiosignalKind.HeartRate, 110f, "bpm", 0.9f, false, DateTimeOffset.UtcNow);

        BiosignalAffectMapper.Apply(s, a);

        Assert.Equal(0.55f, a.Energy, Eps);
        Assert.Equal(0.2f,  a.Uncertainty, Eps);
    }

    [Fact]
    public void VeryHighHeartRate_Above130_RaisesEnergy010_AndUncertainty005()
    {
        var a = NeutralState();
        var s = new BiosignalSample(Guid.NewGuid(), BiosignalKind.HeartRate, 140f, "bpm", 1.0f, false, DateTimeOffset.UtcNow);

        BiosignalAffectMapper.Apply(s, a);

        Assert.Equal(0.60f, a.Energy, Eps);
        Assert.Equal(0.25f, a.Uncertainty, Eps);
    }

    [Fact]
    public void LowHeartRate_Below50_LowersEnergy_By005()
    {
        var a = NeutralState();
        var s = new BiosignalSample(Guid.NewGuid(), BiosignalKind.HeartRate, 45f, "bpm", 0.9f, false, DateTimeOffset.UtcNow);

        BiosignalAffectMapper.Apply(s, a);

        Assert.Equal(0.45f, a.Energy, Eps);
    }

    [Fact]
    public void LowHrv_Below20ms_RaisesUncertainty_LowersRapport()
    {
        var a = NeutralState();
        var s = new BiosignalSample(Guid.NewGuid(), BiosignalKind.HeartRateVariability, 15f, "ms", 0.9f, false, DateTimeOffset.UtcNow);

        BiosignalAffectMapper.Apply(s, a);

        Assert.Equal(0.25f, a.Uncertainty, Eps);
        Assert.Equal(0.48f, a.Rapport, Eps);
    }

    [Fact]
    public void HighHrv_Above60ms_RaisesEngagement_By002()
    {
        var a = NeutralState();
        var s = new BiosignalSample(Guid.NewGuid(), BiosignalKind.HeartRateVariability, 75f, "ms", 0.9f, false, DateTimeOffset.UtcNow);

        BiosignalAffectMapper.Apply(s, a);

        Assert.Equal(0.52f, a.Engagement, Eps);
    }

    [Fact]
    public void LowSpO2_Below90_RaisesUncertainty_By010()
    {
        var a = NeutralState();
        var s = new BiosignalSample(Guid.NewGuid(), BiosignalKind.OxygenSaturation, 85f, "%", 0.9f, false, DateTimeOffset.UtcNow);

        BiosignalAffectMapper.Apply(s, a);

        Assert.Equal(0.30f, a.Uncertainty, Eps);
    }

    [Fact]
    public void LowConfidence_ProducesNoMutation()
    {
        var a = NeutralState();
        var snapshot = (a.Curiosity, a.Engagement, a.Uncertainty, a.Rapport, a.Energy);
        var s = new BiosignalSample(Guid.NewGuid(), BiosignalKind.HeartRate, 140f, "bpm", 0.3f, false, DateTimeOffset.UtcNow);

        BiosignalAffectMapper.Apply(s, a);

        Assert.Equal(snapshot, (a.Curiosity, a.Engagement, a.Uncertainty, a.Rapport, a.Energy));
    }

    [Fact]
    public void SleepStageDeep_ProducesNoMutation()
    {
        var a = NeutralState();
        var snapshot = (a.Curiosity, a.Engagement, a.Uncertainty, a.Rapport, a.Energy);
        var s = new BiosignalSample(Guid.NewGuid(), BiosignalKind.SleepStage, 2f, "stage", 0.9f, false, DateTimeOffset.UtcNow);

        BiosignalAffectMapper.Apply(s, a);

        Assert.Equal(snapshot, (a.Curiosity, a.Engagement, a.Uncertainty, a.Rapport, a.Energy));
    }

    [Fact]
    public void ChainedHighHeartRate_TenTimes_ClampsToOne_NoOverflow()
    {
        var a = NeutralState();
        var s = new BiosignalSample(Guid.NewGuid(), BiosignalKind.HeartRate, 140f, "bpm", 1.0f, false, DateTimeOffset.UtcNow);

        for (var i = 0; i < 10; i++)
        {
            BiosignalAffectMapper.Apply(s, a);
        }

        Assert.InRange(a.Energy, 0f, 1f);
        Assert.InRange(a.Uncertainty, 0f, 1f);
        Assert.Equal(1f, a.Energy, Eps);
        // Uncertainty starts at 0.2, +0.05 * 10 = 0.7 (still in range, not yet clamped).
        Assert.Equal(0.7f, a.Uncertainty, Eps);
    }

    [Fact]
    public void ChainedLowHeartRate_TenTimes_ClampsToZero_NoUnderflow()
    {
        var a = NeutralState();
        var s = new BiosignalSample(Guid.NewGuid(), BiosignalKind.HeartRate, 45f, "bpm", 1.0f, false, DateTimeOffset.UtcNow);

        for (var i = 0; i < 20; i++)
        {
            BiosignalAffectMapper.Apply(s, a);
        }

        Assert.InRange(a.Energy, 0f, 1f);
        Assert.Equal(0f, a.Energy, Eps);
    }

    [Fact]
    public void Apply_UpdatesLastUpdatedUtc()
    {
        var a = NeutralState();
        a.LastUpdatedUtc = DateTimeOffset.UtcNow.AddDays(-1);
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        var s = new BiosignalSample(Guid.NewGuid(), BiosignalKind.HeartRate, 110f, "bpm", 0.9f, false, DateTimeOffset.UtcNow);
        BiosignalAffectMapper.Apply(s, a);

        Assert.True(a.LastUpdatedUtc > before);
    }
}
