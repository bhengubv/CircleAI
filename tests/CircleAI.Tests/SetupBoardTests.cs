// SetupBoardTests.cs
//
// STEP 4, "APPS SEE DOWNLOAD PROGRESS": every model on its way in moves through queued, waiting for
// network, downloading, verifying, priming, ready, failed and healing - with bytes, speed and time
// left - whichever path fetches it. The board is static per process, as the downloads are, so these
// run alone. How an app reads it over SetupProgress is in StepFourSettingsTests (LinkSettingsRowsTests).

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircleAI.Assistant.Lifecycle;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

[CollectionDefinition(nameof(StepFourStaticCollection), DisableParallelization = true)]
public sealed class StepFourStaticCollection;

[Collection(nameof(StepFourStaticCollection))]
public sealed class SetupBoardTests : IDisposable
{
    public SetupBoardTests()
    {
        SetupBoard.Reset();
        SetupBoard.TitleFor = null;
        BundleModelLoader.DefaultGate = null;
    }

    public void Dispose()
    {
        SetupBoard.Reset();
        SetupBoard.TitleFor = null;
        BundleModelLoader.DefaultGate = null;
    }

    private static DownloadProgress Moving(long received, long total, double rate, int seconds,
                                           DownloadPhase phase = DownloadPhase.Downloading, int attempt = 1) => new()
    {
        FileName = "weights", BytesReceived = received, TotalBytes = total, BytesPerSecond = rate,
        EstimatedTimeRemaining = TimeSpan.FromSeconds(seconds), Phase = phase, Attempt = attempt,
    };

    [Fact]
    public void A_queued_model_downloads_with_bytes_speed_and_time_left_then_is_checked_and_ready()
    {
        SetupBoard.Queue("voice-x", "the English voice", 63_000_000);
        Assert.Equal(SetupItemState.Queued, SetupBoard.Of("voice-x")!.State);

        SetupBoard.Progress("voice-x", Moving(21_000_000, 63_000_000, 1_500_000, 28));
        var moving = SetupBoard.Of("voice-x")!;
        Assert.Equal(SetupItemState.Downloading, moving.State);
        Assert.Equal("the English voice", moving.Title);          // the planner's words stay
        Assert.Equal(21_000_000, moving.BytesDone);
        Assert.Equal(63_000_000, moving.BytesTotal);
        Assert.Equal(1_500_000, moving.BytesPerSecond);
        Assert.Equal(TimeSpan.FromSeconds(28), moving.Remaining);
        Assert.Equal(1.0 / 3, moving.Fraction, 3);

        SetupBoard.Progress("voice-x", Moving(63_000_000, 63_000_000, 0, 0, DownloadPhase.Verifying));
        Assert.Equal(SetupItemState.Verifying, SetupBoard.Of("voice-x")!.State);
        Assert.Equal(0, SetupBoard.Of("voice-x")!.BytesPerSecond); // no bytes move while checking

        SetupBoard.Arrived("voice-x", primes: false);
        Assert.Equal(SetupItemState.Ready, SetupBoard.Of("voice-x")!.State);
    }

    [Fact]
    public void A_brain_that_arrives_is_priming_until_it_has_been_loaded_and_warmed()
    {
        SetupBoard.Queue("brain-x", "the brain", 429_000_000);
        SetupBoard.Arrived("brain-x", primes: true);

        var arrived = SetupBoard.Of("brain-x")!;
        Assert.Equal(SetupItemState.Priming, arrived.State);
        Assert.False(arrived.Settled);
        Assert.Contains("warmed", arrived.Detail);
    }

    [Fact]
    public void A_failure_then_a_new_attempt_reads_as_healing_and_counts_the_attempt()
    {
        SetupBoard.Queue("ears-x", "the ears", 78_000_000);
        SetupBoard.Progress("ears-x", Moving(10_000_000, 78_000_000, 900_000, 60));
        SetupBoard.Failed("ears-x", "SHA-256 mismatch");
        Assert.Equal(SetupItemState.Failed, SetupBoard.Of("ears-x")!.State);

        SetupBoard.Healing("ears-x", "fetching it again after a bad hash");
        var healing = SetupBoard.Of("ears-x")!;
        Assert.Equal(SetupItemState.Healing, healing.State);
        Assert.Equal(2, healing.Attempt);

        // Bytes moving again after a failure are still healing, not a fresh download.
        SetupBoard.Progress("ears-x", Moving(5_000_000, 78_000_000, 900_000, 70));
        Assert.Equal(SetupItemState.Healing, SetupBoard.Of("ears-x")!.State);

        // And a retry inside the download service is healing too.
        SetupBoard.Queue("wake-x", "the wake word", 6_000_000);
        SetupBoard.Progress("wake-x", Moving(1_000_000, 6_000_000, 0, 0, DownloadPhase.Retrying, attempt: 3));
        Assert.Equal(SetupItemState.Healing, SetupBoard.Of("wake-x")!.State);
        Assert.Equal(3, SetupBoard.Of("wake-x")!.Attempt);
    }

    [Fact]
    public void Queueing_never_drags_a_moving_model_back_into_line()
    {
        SetupBoard.Queue("brain-y", "the brain", 1_000);
        SetupBoard.Progress("brain-y", Moving(500, 1_000, 100, 5));
        SetupBoard.Queue("brain-y", null, 1_000);
        Assert.Equal(SetupItemState.Downloading, SetupBoard.Of("brain-y")!.State);
    }

    [Fact]
    public void The_summary_weighs_by_bytes_counts_the_settled_and_estimates_what_is_owed()
    {
        SetupBoard.Queue("small", "the wake word", 1_000_000);
        SetupBoard.Queue("big", "the brain", 9_000_000);
        SetupBoard.Arrived("small", primes: false);
        SetupBoard.Progress("big", Moving(3_000_000, 9_000_000, 1_000_000, 6));

        var summary = SetupBoard.Summary()!;
        Assert.Equal(1, summary.Settled);
        Assert.Equal(2, summary.Count);
        Assert.Equal("the brain", summary.Title);
        Assert.Equal(0.4, summary.Fraction, 3);                    // (1 + 3) of 10 MB
        Assert.Equal(TimeSpan.FromSeconds(6), summary.Remaining);  // 6 MB owed at 1 MB/s
        Assert.False(summary.Done);

        SetupBoard.Arrived("big", primes: false);
        Assert.True(SetupBoard.Summary()!.Done);
    }

    [Fact]
    public void An_unplanned_model_is_titled_by_the_hook_and_an_empty_board_has_no_summary()
    {
        Assert.Null(SetupBoard.Summary());
        SetupBoard.TitleFor = id => id == "whisper-x" ? "the ears" : null;
        SetupBoard.Progress("whisper-x", Moving(1, 10, 1, 9));
        Assert.Equal("the ears", SetupBoard.Of("whisper-x")!.Title);

        SetupBoard.TitleFor = _ => throw new InvalidOperationException("a broken hook");
        SetupBoard.Progress("nameless", Moving(1, 10, 1, 9));
        Assert.Equal("nameless", SetupBoard.Of("nameless")!.Title); // a hook that throws never stops the board
    }

    [Fact]
    public async Task A_download_the_loader_cannot_start_reads_as_failed_on_the_board()
    {
        var store = Directory.CreateTempSubdirectory("circleai-board-").FullName;
        try
        {
            using var loader = new BundleModelLoader(store, new ModelRegistryService());
            await Assert.ThrowsAsync<ArgumentException>(() =>
                loader.DownloadModelAsync("no-such-model", (IProgress<DownloadProgress>?)null));

            var failed = SetupBoard.Of("no-such-model")!;
            Assert.Equal(SetupItemState.Failed, failed.State);
            Assert.Contains("not in the registry", failed.Detail);
        }
        finally { Directory.Delete(store, recursive: true); }
    }

    // A gate that refuses everything, with the person's reason - what a host's gate says on mobile data.
    private sealed class Refusing(string why) : IModelDownloadGate
    {
        public string? BlockReason(long estimatedBytes) => why;
        public bool IsEnforceable => true;
    }

    [Fact]
    public async Task A_download_the_hosts_gate_refuses_waits_for_a_network_with_its_reason()
    {
        var store = Directory.CreateTempSubdirectory("circleai-board-").FullName;
        try
        {
            using var registry = new ModelRegistryService();
            var voice = registry.AllModels.First(m => m.Modality == ModelModality.WakeWord && m.TotalBytes > 0);

            // THE HOST'S GATE REACHES A LOADER THAT WAS GIVEN NONE: every pull path builds its own.
            BundleModelLoader.DefaultGate = () => new Refusing("Waiting for Wi-Fi: downloads over mobile data are switched off.");

            using var loader = new BundleModelLoader(store, registry);
            await Assert.ThrowsAsync<ModelDownloadBlockedException>(() =>
                loader.DownloadModelAsync(voice.Name, (IProgress<DownloadProgress>?)null));

            var waiting = SetupBoard.Of(voice.Name)!;
            Assert.Equal(SetupItemState.WaitingForNetwork, waiting.State);
            Assert.Contains("Wi-Fi", waiting.Detail);
        }
        finally { Directory.Delete(store, recursive: true); }
    }

    [Fact]
    public void The_board_follows_the_brains_readiness_from_loading_to_ready_and_failing_with_a_retry()
    {
        var now = DateTimeOffset.UtcNow;
        SetupBoard.Arrived("brain-z", primes: true);

        SetupBoardFollow.Apply(Snapshot(new ComponentState(ReadinessComponents.Brain, BrainPhase.Loading,
            LoadStep.Warming, null, PauseReason.None, "", "brain-z", now)));
        var priming = SetupBoard.Of("brain-z")!;
        Assert.Equal(SetupItemState.Priming, priming.State);
        Assert.Equal(ReadinessSentences.Warming, priming.Detail);   // "Nearly ready to answer."

        SetupBoardFollow.Apply(Snapshot(ComponentState.Of(ReadinessComponents.Brain, BrainPhase.Ready, "", now, "brain-z")));
        Assert.Equal(SetupItemState.Ready, SetupBoard.Of("brain-z")!.State);

        SetupBoardFollow.Apply(Snapshot(new ComponentState(ReadinessComponents.Brain, BrainPhase.Failed, LoadStep.None,
            FailureKind.OutOfMemory, PauseReason.WaitingForMemory, "", "brain-z", now, null, TimeSpan.FromSeconds(30))));
        Assert.Equal(SetupItemState.Healing, SetupBoard.Of("brain-z")!.State);

        // A brain the board had never heard of - on the phone from a run before - shows its priming too.
        SetupBoardFollow.Apply(Snapshot(new ComponentState(ReadinessComponents.Brain, BrainPhase.Loading,
            LoadStep.LoadingWeights, null, PauseReason.None, "", "older-brain", now)));
        Assert.Equal(SetupItemState.Priming, SetupBoard.Of("older-brain")!.State);
    }

    [Fact]
    public void A_tracker_change_reaches_the_board_until_the_follow_is_disposed()
    {
        var file = Path.Combine(Path.GetTempPath(), "circleai-readiness-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var tracker = new ReadinessTracker(file);
            using (SetupBoardFollow.Follow(tracker))
            {
                tracker.Report(ComponentState.Of(ReadinessComponents.Ears, BrainPhase.Ready, "ready", DateTimeOffset.UtcNow, "ears-z"));
                Assert.Equal(SetupItemState.Ready, SetupBoard.Of("ears-z")!.State);
            }

            tracker.Report(ComponentState.Of(ReadinessComponents.Voice, BrainPhase.Ready, "ready", DateTimeOffset.UtcNow, "voice-z"));
            Assert.Null(SetupBoard.Of("voice-z"));
        }
        finally { try { File.Delete(file); } catch { } }
    }

    private static ReadinessSnapshot Snapshot(ComponentState brain) =>
        new(ReadinessSnapshot.CurrentSchema, 1, "boot", brain, [], [], []);
}
