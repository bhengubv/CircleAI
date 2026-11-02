// BrainTuningTests.cs
//
// The projection of Wolverine's knobs that both service processes read (S6): an entry is in force
// until its time, the strongest of two wins, a damaged or missing file is no tuning at all, and
// the caps hold a request under them. Where the 2B is on this machine, BrainFactory builds the C#
// engine with the knobs in force - the one place every brain is built.

using CircleAI.Core;
using CircleAI.Inference;
using CircleAI.Inference.Managed;
using Xunit;
using Xunit.Abstractions;
using static CircleAI.Tests.WolverineEvents;

namespace CircleAI.Tests;

[Collection(nameof(WolverineCollection))]
public sealed class BrainTuningTests(ITestOutputHelper output) : IDisposable
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Setup();

    private static string Setup()
    {
        Reset();
        var dir = TempDir("tuning");
        BrainTuning.Directory = dir;
        return dir;
    }

    public void Dispose()
    {
        Reset();
        Delete(_dir);
    }

    private static TunedKnob Knob(string name, string value, TimeSpan forHowLong) => new(name, value, Noon + forHowLong);

    [Fact]
    public void Nothing_written_is_nothing_turned_down()
    {
        Assert.True(BrainTuning.Current(Noon).IsNone);
        Assert.Equal("nothing turned down", BrainTuning.Current(Noon).Describe());
    }

    [Fact]
    public void A_knob_is_in_force_until_its_time_and_not_a_moment_after()
    {
        Assert.True(BrainTuning.Write([Knob(BrainTuning.Knobs.Threads, "2", TimeSpan.FromHours(1))]));

        Assert.Equal(2, BrainTuning.Current(Noon).MaxThreads);
        Assert.True(BrainTuning.Current(Noon + TimeSpan.FromHours(1)).IsNone);
    }

    [Fact]
    public void Of_two_entries_for_one_knob_the_stronger_wins()
    {
        BrainTuning.Write(
        [
            Knob(BrainTuning.Knobs.MemoryBits, nameof(KvCompressionMode.TurboQuant3Bit), TimeSpan.FromHours(1)),
            Knob(BrainTuning.Knobs.MemoryBits, nameof(KvCompressionMode.TurboQuant2Bit), TimeSpan.FromHours(1)),
            Knob(BrainTuning.Knobs.Threads, "2", TimeSpan.FromHours(1)),
            Knob(BrainTuning.Knobs.Threads, "1", TimeSpan.FromHours(1)),
            Knob(BrainTuning.Knobs.Context, "2048", TimeSpan.FromHours(1)),
            Knob(BrainTuning.Knobs.Context, "1024", TimeSpan.FromHours(1)),
        ]);

        var tuning = BrainTuning.Current(Noon);
        Assert.Equal(KvCompressionMode.TurboQuant2Bit, tuning.WorkingMemory);
        Assert.Equal(1, tuning.MaxThreads);
        Assert.Equal(1024, tuning.MaxContext);
    }

    [Fact]
    public void A_damaged_file_is_no_tuning_at_all()
    {
        File.WriteAllText(Path.Combine(_dir, BrainTuning.FileName), "{ not json");

        Assert.True(BrainTuning.Current(Noon).IsNone);
        Assert.Empty(BrainTuning.ReadKnobs());
    }

    [Fact]
    public void No_folder_is_no_tuning_and_nothing_written()
    {
        BrainTuning.Directory = null;

        Assert.False(BrainTuning.Write([Knob(BrainTuning.Knobs.Threads, "1", TimeSpan.FromHours(1))]));
        Assert.True(BrainTuning.Current(Noon).IsNone);
    }

    [Fact]
    public void The_caps_hold_a_request_under_them_and_leave_the_engine_its_choice_when_nothing_is_capped()
    {
        var capped = new BrainTuning { MaxThreads = 2, MaxContext = 1024 };
        Assert.Equal(2, capped.Threads(3));
        Assert.Equal(1, capped.Threads(1));
        Assert.Equal(2, capped.Threads(null));       // nothing asked: the cap is the answer
        Assert.Equal(1024, capped.Context(4096));

        Assert.Null(BrainTuning.None.Threads(null)); // nothing asked, nothing capped: the engine picks
        Assert.Equal(3, BrainTuning.None.Threads(3));
        Assert.Equal(4096, BrainTuning.None.Context(4096));
    }

    [Fact]
    public void A_brain_stepped_down_from_is_avoided_whatever_its_case_and_the_rest_are_not()
    {
        BrainTuning.Write([Knob(BrainTuning.Knobs.AvoidPrefix + Model, "1", TimeSpan.FromHours(6))]);

        var tuning = BrainTuning.Current(Noon);
        Assert.True(tuning.Avoids(Model.ToLowerInvariant()));
        Assert.False(tuning.Avoids("Qwen3.5-0.8B-MNN"));
        Assert.Contains(Model, tuning.Describe());
    }

    [Fact]
    public void Brainfactory_builds_the_csharp_engine_with_the_knobs_in_force()
    {
        var path = Bonsai2Model.TwoB();
        if (path is null)
        {
            output.WriteLine(Bonsai2Model.Missing);
            return;
        }

        var until = DateTimeOffset.UtcNow.AddHours(1);
        BrainTuning.Write(
        [
            new TunedKnob(BrainTuning.Knobs.Threads, "1", until),
            new TunedKnob(BrainTuning.Knobs.Context, "1024", until),
        ]);

        using var brain = (ManagedChatGenerator)BrainFactory.Default.Build(
            new BrainRequest(path) { ModelId = "Ternary-Bonsai-2-2B", Engine = ModelEngine.Managed, ContextSize = 4096, Threads = 3 });

        Assert.Equal(1, brain.Threads);
        Assert.Equal(1024, brain.ContextLength);
    }
}
