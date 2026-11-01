// DeviceBalanceTests.cs
//
// Three gaps from docs/OPEN-GAPS.md, closed and pinned here:
//   A14 - "Warming" was a load step nothing reported. AIService now says which part of a start
//         is running, the in-process brain carries it into its status, and readiness has a
//         sentence for it ("Nearly ready to answer.").
//   A11 / A13 - the power budget's battery and heat steps could never fire: its callers passed
//         neither, and the thermal service that would know was never built. The budget now
//         reads the device's conditions itself when a caller passes none.

using CircleAI.Assistant.Lifecycle;
using CircleAI.Core;
using CircleAI.Hosting;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

[Collection(nameof(PowerBudgetConditionsCollection))]
public sealed class DeviceBalanceTests : IDisposable
{
    private readonly string _modelPath = Path.GetTempFileName();

    public void Dispose()
    {
        PowerBudgetPolicy.Conditions = null;
        try { File.Delete(_modelPath); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task A_start_says_it_is_loading_and_then_that_it_is_warming()
    {
        var steps = new List<LoadStep>();
        var options = new AIOptions { ModelPath = _modelPath, WarmOnStart = true, SystemPrompt = "You are B!." };
        options.LoadStepChanged = steps.Add;
        await using var service = new AIService(options, generatorFactory: _ => new FakeChatGenerator("Hi!"));

        await service.StartAsync(CancellationToken.None);

        Assert.Equal(new[] { LoadStep.LoadingWeights, LoadStep.Warming }, steps);
    }

    [Fact]
    public async Task Without_a_warm_up_there_is_no_warming_step()
    {
        var steps = new List<LoadStep>();
        var options = new AIOptions { ModelPath = _modelPath, WarmOnStart = false };
        options.LoadStepChanged = steps.Add;
        await using var service = new AIService(options, generatorFactory: _ => new FakeChatGenerator("Hi!"));

        await service.StartAsync(CancellationToken.None);

        Assert.Equal(new[] { LoadStep.LoadingWeights }, steps);
    }

    [Fact]
    public async Task The_in_process_brain_carries_the_step_into_its_status_and_readiness_into_words()
    {
        var release = new TaskCompletionSource();
        var seen = new List<LoadStep>();
        InProcessBrainWorker? worker = null;
        worker = new InProcessBrainWorker(async ct =>
        {
            worker!.ReportStep(LoadStep.Warming);
            await release.Task.WaitAsync(ct);
            return new InProcessBrain(new FakeAIService(), "test-brain");
        });
        worker.StatusChanged += (_, s) => { lock (seen) seen.Add(s.Step); };

        var loading = worker.EnsureReadyAsync(CancellationToken.None);
        Assert.Equal(BrainPhase.Loading, worker.Status.Phase);
        Assert.Equal(LoadStep.Warming, worker.Status.Step);
        Assert.Equal("Nearly ready to answer.", ReadinessSentences.For(
            ComponentState.Of(ReadinessComponents.Brain, worker.Status.Phase, "", DateTimeOffset.UtcNow) with { Step = worker.Status.Step }));

        release.SetResult();
        await loading;
        Assert.Equal(BrainPhase.Ready, worker.Status.Phase);
        worker.ReportStep(LoadStep.Warming); // outside a load: ignored
        Assert.Equal(LoadStep.None, worker.Status.Step);
        Assert.Equal(ReadinessSentences.Loading, ReadinessSentences.For(LoadStep.LoadingWeights));
    }

    [Fact]
    public void A_nearly_flat_battery_steps_the_budget_down_without_the_caller_saying_so()
    {
        PowerBudgetPolicy.Conditions = () => (8, false);
        var low = PowerBudgetPolicy.Resolve(PowerBudget.Normal, 400);
        Assert.Equal(64, low.MaxTokens);
        Assert.True(low.PreferSmallerModelInChain);

        PowerBudgetPolicy.Conditions = () => (80, false);
        Assert.Equal(400, PowerBudgetPolicy.Resolve(PowerBudget.Normal, 400).MaxTokens);
    }

    [Fact]
    public void A_hot_device_steps_a_high_budget_down()
    {
        PowerBudgetPolicy.Conditions = () => (null, true);
        Assert.Equal(KvCompressionMode.TurboQuant4Bit, PowerBudgetPolicy.Resolve(PowerBudget.High, 400).PreferredKvMode);

        PowerBudgetPolicy.Conditions = () => (null, false);
        Assert.Equal(KvCompressionMode.Off, PowerBudgetPolicy.Resolve(PowerBudget.High, 400).PreferredKvMode);
    }

    [Fact]
    public void What_a_caller_says_outranks_the_reading_and_a_failed_reading_bends_nothing()
    {
        PowerBudgetPolicy.Conditions = () => (5, true);
        Assert.Equal(400, PowerBudgetPolicy.Resolve(PowerBudget.Normal, 400, batteryLevelPercent: 90, thermalThrottled: false).MaxTokens);

        PowerBudgetPolicy.Conditions = () => throw new InvalidOperationException("no battery here");
        Assert.Equal(400, PowerBudgetPolicy.Resolve(PowerBudget.Normal, 400).MaxTokens);
    }
}

/// <summary>Tests that set the process-wide power-budget reader run one at a time.</summary>
[CollectionDefinition(nameof(PowerBudgetConditionsCollection), DisableParallelization = true)]
public sealed class PowerBudgetConditionsCollection;
