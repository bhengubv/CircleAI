// ProactiveScheduler.cs
//
// (3.2.0) Generic IProactiveScheduler — owns cron parsing, last-run
// tracking, refresh, and event dispatch. Calls into a host-supplied
// IProactiveTaskSource (what tasks exist) and IProactiveTaskRunner
// (how to execute one).
//
// Lifted from CircleUp's VaultWorkflowScheduler, with the vault /
// multi-tenant binding abstracted away. Per-context (SourceContext)
// last-run tracking is preserved so multi-tenant hosts keep tenants'
// schedules separate.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Companion.Proactive;

/// <summary>
/// (3.2.0) Default <see cref="IProactiveScheduler"/>. Singleton-safe.
/// Refresh / tick is the contract; the background service ticks every
/// minute by default.
/// </summary>
public sealed class ProactiveScheduler : IProactiveScheduler
{
    private readonly IProactiveTaskSource _source;
    private readonly IProactiveTaskRunner _runner;

    private readonly object _gate = new();
    private List<ProactiveTask>            _tasks  = new();
    private List<ProactiveTaskLoadError>   _errors = new();

    // Per-(context, taskId) last-run map. Context = ProactiveTask.SourceContext
    // or "" if null. Keeps multi-tenant hosts honest — same task id
    // in different contexts has independent last-run state.
    private readonly Dictionary<string, Dictionary<string, DateTimeOffset>> _lastRuns =
        new(StringComparer.OrdinalIgnoreCase);

    public ProactiveScheduler(IProactiveTaskSource source, IProactiveTaskRunner runner)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
    }

    public string BackendId => "default";

    public IReadOnlyList<ProactiveTask> Tasks
    {
        get { lock (_gate) return _tasks.ToArray(); }
    }

    public IReadOnlyList<ProactiveTaskLoadError> LoadErrors
    {
        get { lock (_gate) return _errors.ToArray(); }
    }

    public DateTimeOffset? GetNextRun(ProactiveTask task, DateTimeOffset after)
    {
        if (task.Trigger.Cron is null) return null;
        try
        {
            var expr = CronExpression.Parse(task.Trigger.Cron);
            return expr.GetNextOccurrence(after);
        }
        catch
        {
            return null;
        }
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var snapshot = await _source.GetTasksAsync(ct).ConfigureAwait(false);
        var errors   = await _source.GetErrorsAsync(ct).ConfigureAwait(false);

        lock (_gate)
        {
            _tasks  = snapshot.ToList();
            _errors = errors.ToList();

            // Drop last-run state for (context, taskId) pairs the source
            // no longer reports — prevents memory growth when tasks
            // come and go.
            var live = _tasks
                .Select(t => (Ctx: ContextKey(t.SourceContext), Id: t.Id))
                .ToHashSet();

            foreach (var ctxKey in _lastRuns.Keys.ToList())
            {
                var ids = _lastRuns[ctxKey];
                foreach (var id in ids.Keys.ToList())
                {
                    if (!live.Contains((ctxKey, id)))
                    {
                        ids.Remove(id);
                    }
                }
                if (ids.Count == 0)
                {
                    _lastRuns.Remove(ctxKey);
                }
            }
        }
    }

    public async Task TickAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        ProactiveTask[] candidates;
        lock (_gate)
        {
            candidates = _tasks
                .Where(t => t.Trigger.Cron is not null)
                .ToArray();
        }

        foreach (var task in candidates)
        {
            ct.ThrowIfCancellationRequested();

            DateTimeOffset lastRun;
            var ctxKey = ContextKey(task.SourceContext);
            lock (_gate)
            {
                if (!_lastRuns.TryGetValue(ctxKey, out var map))
                {
                    map = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
                    _lastRuns[ctxKey] = map;
                }
                lastRun = map.TryGetValue(task.Id, out var t) ? t : DateTimeOffset.MinValue;
            }

            try
            {
                var expr = CronExpression.Parse(task.Trigger.Cron!);
                var anchor = lastRun == DateTimeOffset.MinValue
                    ? now.AddMinutes(-1)
                    : lastRun;
                var next = expr.GetNextOccurrence(anchor);
                if (next <= now)
                {
                    await _runner.RunAsync(task, variables: null, ct).ConfigureAwait(false);
                    MarkRun(task, now);
                }
            }
            catch
            {
                // Parse error — already surfaced via LoadErrors at the
                // source layer. Skip this task; don't crash the tick.
            }
        }
    }

    public async Task DispatchEventAsync(
        string                       eventName,
        IDictionary<string, string>? variables = null,
        CancellationToken            ct        = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);

        ProactiveTask[] matched;
        lock (_gate)
        {
            matched = _tasks
                .Where(t => string.Equals(t.Trigger.OnEvent, eventName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        foreach (var task in matched)
        {
            ct.ThrowIfCancellationRequested();
            await _runner.RunAsync(task, variables, ct).ConfigureAwait(false);
            MarkRun(task, DateTimeOffset.UtcNow);
        }
    }

    public async Task<ProactiveTaskRunResult> RunByIdAsync(
        string                       id,
        IDictionary<string, string>? variables = null,
        CancellationToken            ct        = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        ProactiveTask? task;
        lock (_gate)
        {
            task = _tasks.FirstOrDefault(t =>
                string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        if (task is null)
        {
            return new ProactiveTaskRunResult(id, Success: false,
                FailureMessage: $"No task with id '{id}'.");
        }

        var result = await _runner.RunAsync(task, variables, ct).ConfigureAwait(false);
        MarkRun(task, DateTimeOffset.UtcNow);
        return result;
    }

    private void MarkRun(ProactiveTask task, DateTimeOffset when)
    {
        var ctxKey = ContextKey(task.SourceContext);
        lock (_gate)
        {
            if (!_lastRuns.TryGetValue(ctxKey, out var map))
            {
                map = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
                _lastRuns[ctxKey] = map;
            }
            map[task.Id] = when;
        }
    }

    private static string ContextKey(string? sourceContext) =>
        sourceContext ?? string.Empty;
}
