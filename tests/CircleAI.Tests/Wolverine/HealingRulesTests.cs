// HealingRulesTests.cs
//
// RULES FIRST (S6): a fault Wolverine knows is decided by a rule - a ladder of remedies - with no
// brain involved, and the brain's own failures are never put to the brain. Pure functions over a
// HealthEvent, so every rung is a fact on both legs.

using CircleAI.Assistant.Lifecycle;
using CircleAI.Core.Logging;
using CircleAI.Hosting.SelfHealing;
using Xunit;
using static CircleAI.Tests.WolverineEvents;

namespace CircleAI.Tests;

public sealed class HealingRulesTests
{
    private static IReadOnlyList<string>? Ladder(HealthEvent e) => HealingRules.For(e)?.Ladder;

    [Theory]
    [InlineData("exit-reason=2 signal=4", "IllegalInstruction")]
    [InlineData("crash-log-signal signal=4", "IllegalInstruction")]
    [InlineData("exit-reason=2 signal=11", "SegmentationFault")]
    [InlineData("exit-reason=2 signal=6", "Abort")]
    [InlineData("exit-reason=2 signal=7", "BusError")]
    [InlineData("crash-log-signal", "NativeWorkerCrashed")]
    [InlineData("exit-reason=5", "NativeWorkerCrashed")]
    public void A_worker_crash_is_named_by_the_signal_its_evidence_carries(string evidence, string expected) =>
        Assert.Equal(expected, EngineFaults.ErrorType(FailureKind.NativeWorkerCrashed, evidence));

    [Fact]
    public void Every_other_kind_keeps_its_own_name_whatever_signal_the_evidence_names() =>
        Assert.Equal(nameof(FailureKind.OutOfMemory), EngineFaults.ErrorType(FailureKind.OutOfMemory, "exit-reason=2 signal=9 memavail=0.04"));

    [Fact]
    public void The_classifier_carries_the_crash_logs_signal_on_a_phone_with_no_exit_record()
    {
        // API 29 - the P30 - keeps no exit record: the crash buffer is the only witness, and an
        // illegal instruction must still read as one.
        var failure = FailureClassifier.Classify(new ExitFacts(29, WorkPhase.Decode, Model, DateTimeOffset.UtcNow,
            WorkerDiedAlone: true, CrashBufferShowsSignal: true, CrashSignal: 4));

        Assert.Equal(FailureKind.NativeWorkerCrashed, failure.Kind);
        Assert.Equal("IllegalInstruction", EngineFaults.ErrorType(failure.Kind, failure.Evidence));
    }

    [Fact]
    public void An_illegal_instruction_is_cured_by_scalar_kernels() =>
        Assert.Equal(new[] { Remedies.ScalarKernels }, Ladder(Engine("IllegalInstruction", WorkPhase.Decode)));

    [Fact]
    public void Out_of_memory_while_loading_steps_the_brain_down() =>
        Assert.Equal(new[] { Remedies.SmallerBrain }, Ladder(Engine(nameof(FailureKind.OutOfMemory), WorkPhase.Load)));

    [Fact]
    public void Out_of_memory_while_answering_shrinks_before_it_drops() =>
        Assert.Equal(new[] { Remedies.FewerMemoryBits, Remedies.ShorterContext, Remedies.SmallerBrain },
            Ladder(Engine(nameof(FailureKind.OutOfMemory), WorkPhase.Decode)));

    [Fact]
    public void An_idle_brain_taken_back_for_memory_is_no_fault_of_its_knobs() =>
        Assert.Null(HealingRules.For(Engine(nameof(FailureKind.OutOfMemory), WorkPhase.Idle)));

    [Theory]
    [InlineData("SegmentationFault")]
    [InlineData("Abort")]
    [InlineData(nameof(FailureKind.NativeWorkerCrashed))]
    public void A_crash_while_working_tries_fewer_threads_then_scalar_kernels_then_a_smaller_brain(string errorType) =>
        Assert.Equal(new[] { Remedies.FewerThreads, Remedies.ScalarKernels, Remedies.SmallerBrain }, Ladder(Engine(errorType, WorkPhase.Prefill)));

    [Fact]
    public void A_brain_that_stopped_making_progress_gets_a_shorter_context_first() =>
        Assert.Equal(new[] { Remedies.ShorterContext, Remedies.FewerThreads, Remedies.SmallerBrain },
            Ladder(Engine(nameof(FailureKind.TimedOut), WorkPhase.Decode)));

    [Fact]
    public void A_reply_that_thought_its_length_away_gets_a_tighter_thinking_budget() =>
        Assert.Equal(new[] { Remedies.TighterThinking },
            Ladder(HealthEvents.Of(HealingRules.EngineArea, "answer", "NoAnswer", "Reply.Length", "the reply ran out")));

    [Fact]
    public void A_file_that_failed_its_hash_is_fetched_again() =>
        Assert.Equal(new[] { Remedies.Redownload }, Ladder(Download(nameof(CircleAI.Inference.ModelIntegrityException))));

    [Fact]
    public void A_download_that_stalled_past_every_retry_is_resumed() =>
        Assert.Equal(new[] { Remedies.ResumeDownload }, Ladder(Download(nameof(CircleAI.Inference.ModelDownloadException))));

    [Fact]
    public void An_io_error_outside_a_download_is_not_a_stalled_download() =>
        Assert.Null(HealingRules.For(HealthEvents.Of("Voice", "speak", nameof(IOException), "Speaker.Say")));

    [Fact]
    public void A_tool_call_nobody_could_read_is_retried_held_to_its_form() =>
        Assert.Equal(new[] { Remedies.ToolCallRetry },
            Ladder(HealthEvents.Of(HealingRules.ToolsArea, "tool call", "MalformedToolCall", "AIService.AgenticChatAsync")));

    [Fact]
    public void A_process_short_of_managed_memory_frees_its_caches() =>
        Assert.Equal(new[] { Remedies.Memory }, Ladder(HealthEvents.Of("Voice", "load", nameof(OutOfMemoryException), "Speaker.Load")));

    [Theory]
    [InlineData(nameof(FailureKind.Unknown))]
    [InlineData(nameof(FailureKind.AndroidStopped))]
    [InlineData(nameof(FailureKind.ManagedError))]
    public void A_death_no_rule_covers_is_still_the_brains_own_and_never_put_to_it(string errorType)
    {
        var e = Engine(errorType, WorkPhase.Load);
        Assert.Null(HealingRules.For(e));
        Assert.True(HealingRules.IsTheBrainsOwn(e));
    }

    [Fact]
    public void A_download_is_not_the_brains_own_failure() =>
        Assert.False(HealingRules.IsTheBrainsOwn(Download(nameof(CircleAI.Inference.ModelDownloadException))));

    [Fact]
    public void A_failure_with_no_event_meets_no_rule()
    {
        Assert.Null(HealingRules.For(null));
        Assert.False(HealingRules.IsTheBrainsOwn(null));
    }

    [Fact]
    public void Every_rung_of_every_ladder_is_a_remedy_that_has_a_description()
    {
        foreach (var rule in HealingRules.Standard)
            foreach (var remedy in rule.Ladder)
                Assert.NotEqual(remedy, Remedies.Describe(remedy));
    }
}
