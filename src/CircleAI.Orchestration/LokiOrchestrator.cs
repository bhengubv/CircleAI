using System.Runtime.CompilerServices;

namespace CircleAI.Orchestration;

/// <summary>
/// Host-side orchestrator. Accepts <see cref="AgentTask"/> items, dispatches
/// them through an <see cref="IAgentDispatcher"/>, enforces quality gates, and
/// exposes results as an <see cref="IAsyncEnumerable{T}"/> stream for host
/// applications to consume.
/// </summary>
/// <remarks>
/// Task execution is bounded by <see cref="AgentSwarmConfig.MaxConcurrency"/>.
/// After each task completes, the quality gate is evaluated; gate failures are
/// re-emitted as <see cref="AgentStatus.Blocked"/> results with the gate's
/// blocker messages appended to <see cref="SwarmResult.Issues"/>.
/// </remarks>
public sealed class LokiOrchestrator
{
    private readonly IAgentDispatcher _dispatcher;
    private readonly AgentSwarmConfig _config;

    /// <summary>
    /// Initialises a new <see cref="LokiOrchestrator"/> with the given
    /// dispatcher and optional configuration.
    /// </summary>
    /// <param name="dispatcher">
    /// The <see cref="IAgentDispatcher"/> used to execute tasks. Must not be
    /// <c>null</c>.
    /// </param>
    /// <param name="config">
    /// Swarm configuration. Defaults to <see cref="AgentSwarmConfig.Default"/>
    /// when <c>null</c>.
    /// </param>
    public LokiOrchestrator(IAgentDispatcher dispatcher, AgentSwarmConfig? config = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
        _config = config ?? AgentSwarmConfig.Default;
    }

    /// <summary>
    /// Runs a swarm of tasks concurrently up to
    /// <see cref="AgentSwarmConfig.MaxConcurrency"/>. For each completed task,
    /// the quality gate is evaluated; gate failures are yielded as
    /// <see cref="AgentStatus.Blocked"/> results.
    /// </summary>
    /// <param name="tasks">
    /// The tasks to execute. Evaluated eagerly into a list before any
    /// dispatching begins.
    /// </param>
    /// <param name="ct">Token used to cancel the entire swarm run.</param>
    /// <returns>
    /// An async stream of <see cref="SwarmResult"/> items, one per task, in
    /// completion order.
    /// </returns>
    public async IAsyncEnumerable<SwarmResult> RunSwarmAsync(
        IEnumerable<AgentTask> tasks,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var semaphore = new SemaphoreSlim(_config.MaxConcurrency);
        var pending = tasks.ToList();
        var running = new List<Task<SwarmResult>>(pending.Count);

        foreach (var task in pending)
        {
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            running.Add(RunOneAsync(task, semaphore, ct));
        }

        foreach (var runningTask in running)
        {
            var result = await runningTask.ConfigureAwait(false);
            var gate = await _dispatcher.RunQualityGateAsync(result, ct).ConfigureAwait(false);

            if (!gate.Passed
                && (_config.RequireReviewPassBeforeDeploy
                    || _config.RequireSecurityPassBeforeDeploy))
            {
                yield return result with
                {
                    Status = AgentStatus.Blocked,
                    Issues = result.Issues.Concat(gate.Blockers).ToList(),
                };
            }
            else
            {
                yield return result;
            }
        }
    }

    private async Task<SwarmResult> RunOneAsync(
        AgentTask task,
        SemaphoreSlim semaphore,
        CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_config.TaskTimeout);
            return await _dispatcher.DispatchAsync(task, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new SwarmResult(
                task.Id,
                task.Role,
                AgentStatus.Failed,
                "Task timed out.",
                new[] { "[HIGH] Task exceeded configured timeout." },
                DateTimeOffset.UtcNow);
        }
        finally
        {
            semaphore.Release();
        }
    }
}
