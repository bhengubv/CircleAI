// NullImplementations.cs
//
// (3.2.0) Safe null defaults. The default scheduler wired in DI uses
// these unless the host swaps in real implementations.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Companion.Proactive;

/// <summary>(3.2.0) Empty source — no tasks, no errors.</summary>
public sealed class NullProactiveTaskSource : IProactiveTaskSource
{
    public static readonly NullProactiveTaskSource Instance = new();

    public string BackendId => "null";

    public ValueTask<IReadOnlyList<ProactiveTask>> GetTasksAsync(CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<ProactiveTask>>(Array.Empty<ProactiveTask>());

    public ValueTask<IReadOnlyList<ProactiveTaskLoadError>> GetErrorsAsync(CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<ProactiveTaskLoadError>>(Array.Empty<ProactiveTaskLoadError>());
}

/// <summary>
/// (3.2.0) Reports every run as a failure with a "no runner registered"
/// message. Fail-closed default so a host that forgot to wire a real
/// runner notices on first scheduled fire rather than silently doing
/// nothing.
/// </summary>
public sealed class NullProactiveTaskRunner : IProactiveTaskRunner
{
    public static readonly NullProactiveTaskRunner Instance = new();

    public string BackendId => "null";

    public ValueTask<ProactiveTaskRunResult> RunAsync(
        ProactiveTask                  task,
        IDictionary<string, string>?   variables = null,
        CancellationToken              ct        = default)
        => ValueTask.FromResult(new ProactiveTaskRunResult(
            TaskId: task.Id,
            Success: false,
            FailureMessage: "No IProactiveTaskRunner registered; using NullProactiveTaskRunner."));
}

/// <summary>
/// (3.2.0) In-memory source for testing + simple consumers. Add /
/// remove tasks; the scheduler picks up changes on next
/// <see cref="IProactiveScheduler.RefreshAsync"/>.
/// </summary>
public sealed class InMemoryProactiveTaskSource : IProactiveTaskSource
{
    private readonly object _gate = new();
    // Keyed by (sourceContext, id) so multi-tenant hosts can hold the
    // same task id in two contexts without collision. SourceContext
    // defaults to "" when null.
    private readonly Dictionary<(string Ctx, string Id), ProactiveTask> _byKey =
        new(KeyComparer.Instance);
    private readonly List<ProactiveTaskLoadError> _errors = new();

    public string BackendId => "in-memory";

    public void Upsert(ProactiveTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        lock (_gate) _byKey[Key(task)] = task;
    }

    public bool Remove(string id, string? sourceContext = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (_gate) return _byKey.Remove((sourceContext ?? "", id));
    }

    public void Clear()
    {
        lock (_gate) { _byKey.Clear(); _errors.Clear(); }
    }

    public void RecordError(ProactiveTaskLoadError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        lock (_gate) _errors.Add(error);
    }

    public ValueTask<IReadOnlyList<ProactiveTask>> GetTasksAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            var snapshot = (IReadOnlyList<ProactiveTask>)new List<ProactiveTask>(_byKey.Values);
            return ValueTask.FromResult(snapshot);
        }
    }

    public ValueTask<IReadOnlyList<ProactiveTaskLoadError>> GetErrorsAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            var snapshot = (IReadOnlyList<ProactiveTaskLoadError>)new List<ProactiveTaskLoadError>(_errors);
            return ValueTask.FromResult(snapshot);
        }
    }

    private static (string Ctx, string Id) Key(ProactiveTask task) =>
        (task.SourceContext ?? "", task.Id);

    private sealed class KeyComparer : IEqualityComparer<(string Ctx, string Id)>
    {
        public static readonly KeyComparer Instance = new();

        public bool Equals((string Ctx, string Id) a, (string Ctx, string Id) b) =>
            string.Equals(a.Ctx, b.Ctx, StringComparison.OrdinalIgnoreCase)
         && string.Equals(a.Id,  b.Id,  StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Ctx, string Id) k) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(k.Ctx),
                StringComparer.OrdinalIgnoreCase.GetHashCode(k.Id));
    }
}

/// <summary>
/// (3.2.0) Runner that hands every task off to a host-supplied
/// delegate. Useful for hosts whose tasks don't need a structured
/// runner — just "given a task, run something."
/// </summary>
public sealed class DelegateProactiveTaskRunner : IProactiveTaskRunner
{
    private readonly Func<ProactiveTask, IDictionary<string, string>?, CancellationToken, ValueTask<ProactiveTaskRunResult>> _handler;

    public DelegateProactiveTaskRunner(
        Func<ProactiveTask, IDictionary<string, string>?, CancellationToken, ValueTask<ProactiveTaskRunResult>> handler)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    public string BackendId => "delegate";

    public ValueTask<ProactiveTaskRunResult> RunAsync(
        ProactiveTask                  task,
        IDictionary<string, string>?   variables = null,
        CancellationToken              ct        = default)
        => _handler(task, variables, ct);
}
