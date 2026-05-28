using System.Threading.Channels;

namespace CircleAI.Orchestration;

/// <summary>
/// In-process agent dispatcher. Routes tasks to handler delegates registered
/// per <see cref="AgentRole"/>. No external network calls are made — loki-mode
/// hooks into this dispatcher at the host application level.
/// </summary>
/// <remarks>
/// Register a handler for each <see cref="AgentRole"/> your application
/// supports via <see cref="RegisterHandler"/> before calling
/// <see cref="DispatchAsync"/>. Tasks dispatched to roles without a registered
/// handler return <see cref="AgentStatus.Blocked"/> immediately.
/// </remarks>
public sealed class LocalAgentDispatcher : IAgentDispatcher, IDisposable
{
    private readonly Dictionary<AgentRole, Func<AgentTask, CancellationToken, Task<SwarmResult>>> _handlers = new();
    private readonly Channel<AgentTask> _queue = Channel.CreateUnbounded<AgentTask>();
    private bool _disposed;

    /// <summary>
    /// Registers an async handler delegate for the given <paramref name="role"/>.
    /// Replaces any previously registered handler for that role.
    /// </summary>
    /// <param name="role">The <see cref="AgentRole"/> this handler services.</param>
    /// <param name="handler">
    /// A delegate that receives an <see cref="AgentTask"/> and a
    /// <see cref="CancellationToken"/> and returns a <see cref="SwarmResult"/>.
    /// Must not be <c>null</c>.
    /// </param>
    public void RegisterHandler(
        AgentRole role,
        Func<AgentTask, CancellationToken, Task<SwarmResult>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handlers[role] = handler;
    }

    /// <inheritdoc />
    public async Task<SwarmResult> DispatchAsync(AgentTask task, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_handlers.TryGetValue(task.Role, out var handler))
            return await handler(task, ct).ConfigureAwait(false);

        // No handler registered — surface a blocked result with an actionable message.
        return new SwarmResult(
            task.Id,
            task.Role,
            AgentStatus.Blocked,
            $"No handler registered for role {task.Role}.",
            new[] { $"Register a handler for AgentRole.{task.Role} before dispatching." },
            DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Deterministic gate: any issue prefixed with <c>[CRITICAL]</c> or
    /// <c>[HIGH]</c> (case-insensitive) is classified as a blocker; all other
    /// issues are demoted to warnings.
    /// </remarks>
    public Task<QualityGateResult> RunQualityGateAsync(
        SwarmResult result,
        CancellationToken ct = default)
    {
        var blockers = result.Issues
            .Where(i => i.StartsWith("[CRITICAL]", StringComparison.OrdinalIgnoreCase)
                     || i.StartsWith("[HIGH]", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var warnings = result.Issues
            .Where(i => !blockers.Contains(i))
            .ToList();

        return Task.FromResult(new QualityGateResult(
            Passed: blockers.Count == 0,
            Blockers: blockers,
            Warnings: warnings));
    }

    /// <summary>
    /// Disposes the dispatcher and completes the internal task queue.
    /// After disposal, calls to <see cref="DispatchAsync"/> will throw
    /// <see cref="ObjectDisposedException"/>.
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        _queue.Writer.Complete();
    }
}
