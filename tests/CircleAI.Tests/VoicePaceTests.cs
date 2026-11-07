// VoicePaceTests.cs
//
// The best voice is the best one that can speak here: a voice that takes longer to render a sentence
// than the sentence lasts is passed over for one that does not, however high it is ranked.

using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;
using CircleAI.Voice;
using Xunit;

namespace CircleAI.Tests;

[Collection(nameof(VoicePaceTests))]
public sealed class VoicePaceTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "voice-pace-" + Guid.NewGuid().ToString("N") + ".txt");

    public VoicePaceTests() => VoicePace.Reset();

    public void Dispose()
    {
        VoicePace.Reset();
        File.Delete(_file);
    }

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void A_voice_keeps_up_until_it_has_shown_it_cannot()
    {
        Assert.True(VoicePace.KeepsUp("big"));

        VoicePace.Note("big", S(2.1), S(1));

        Assert.False(VoicePace.KeepsUp("big"));
        Assert.Equal(1, VoicePace.Verdicts);
    }

    [Fact]
    public void The_best_pace_stands_because_a_voice_beside_a_busy_brain_is_slower_than_it_is()
    {
        VoicePace.Note("small", S(0.3), S(1));
        VoicePace.Note("small", S(1.4), S(1));

        Assert.True(VoicePace.KeepsUp("small"));
        Assert.Equal(0.3, VoicePace.Of("small")!.Value, 3);
    }

    [Fact]
    public void One_good_render_clears_a_voice_a_busy_moment_gave_up_on()
    {
        VoicePace.Note("small", S(1.5), S(1));
        Assert.False(VoicePace.KeepsUp("small"));

        VoicePace.Note("small", S(0.4), S(1));

        Assert.True(VoicePace.KeepsUp("small"));
    }

    [Fact]
    public void What_was_learned_is_there_after_a_restart()
    {
        VoicePace.KeepIn(_file);
        VoicePace.Note("big", S(2.1), S(1));
        VoicePace.Note("small", S(0.33), S(1));

        VoicePace.Reset();
        VoicePace.KeepIn(_file);

        Assert.False(VoicePace.KeepsUp("big"));
        Assert.True(VoicePace.KeepsUp("small"));
        Assert.Equal(0.33, VoicePace.Of("small")!.Value, 3);
    }

    [Fact]
    public void A_voice_that_said_nothing_is_passed_over()
    {
        Assert.True(VoicePace.CanSpeak("broken"));

        VoicePace.CouldNotSpeak("broken");

        Assert.False(VoicePace.CanSpeak("broken"));
    }

    // ── in the selector ─────────────────────────────────────────────────

    private static string Picked()
    {
        using var registry = new ModelRegistryService();
        ISpeechModelSelector selector = new SpeechModelSelector(registry);
        var device = new DeviceProbe(
            RamAvailableBytes: 4L * 1024 * 1024 * 1024,
            StorageFreeBytes:  32L * 1024 * 1024 * 1024,
            Gpu:               GpuKind.None,
            CpuCores:          8,
            Thermal:           ThermalClass.Passive,
            Connectivity:      Connectivity.Online);
        return selector.PlanFor(device, ModelModality.Tts, "en").Model!.ModelId;
    }

    [Fact]
    public void The_highest_rank_that_keeps_up_speaks_and_nothing_measured_changes_nothing()
    {
        var top = Picked();

        VoicePace.Note(top, S(2.1), S(1));
        var next = Picked();

        Assert.NotEqual(top, next);
        VoicePace.Reset();
        Assert.Equal(top, Picked());
    }

    [Fact]
    public void When_nothing_keeps_up_the_nearest_to_it_speaks()
    {
        var first = Picked();
        VoicePace.Note(first, S(1.2), S(1));
        var second = Picked();
        VoicePace.Note(second, S(3.0), S(1));
        var third = Picked();
        VoicePace.Note(third, S(4.0), S(1));

        // Every English voice measured and none keeping up.
        while (VoicePace.Of(Picked()) is null) VoicePace.Note(Picked(), S(5.0), S(1));

        Assert.Equal(first, Picked());
    }

    [Fact]
    public void A_voice_that_said_nothing_is_not_chosen_again()
    {
        var top = Picked();

        VoicePace.CouldNotSpeak(top);

        Assert.NotEqual(top, Picked());
    }
}

[CollectionDefinition(nameof(VoicePaceTests), DisableParallelization = true)]
public sealed class VoicePaceCollection;
