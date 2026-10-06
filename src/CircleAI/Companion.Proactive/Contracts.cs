// Contracts.cs
//
// (3.2.0) Proactive scheduling contract surface. Three interfaces split
// cleanly so consumers can replace one without touching the others:
//
//   IProactiveTaskSource   — where do tasks come from? (vault FS, DB,
//                            in-memory, …)
//   IProactiveTaskRunner   — how do we execute one? (workflow engine,
//                            skill dispatcher, plain delegate, …)
//   IProactiveScheduler    — when do they fire? (provided by the
//                            substrate; reads cron from
//                            ProactiveTask.Trigger, ticks via the
//                            background service)
//
// The substrate ships its own IProactiveScheduler (the cron tick loop +
// last-run tracking). Consumers swap the source + the runner.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Companion.Proactive;

/// <summary>
/// (3.2.0) Where the active set of tasks comes from. Refreshed via
/// <see cref="GetTasksAsync"/> on every scheduler refresh / tick.
/// </summary>
public interface IProactiveTaskSource
{
    /// <summary>Backend self-identification — "vault-fs", "in-memory", "null".</summary>
    string BackendId { get; }

    /// <summary>Snapshot the current set of tasks.</summary>
    ValueTask<IReadOnlyList<ProactiveTask>> GetTasksAsync(CancellationToken ct = default);

    /// <summary>Any parse / load failures surfaced from the last refresh.</summary>
    ValueTask<IReadOnlyList<ProactiveTaskLoadError>> GetErrorsAsync(CancellationToken ct = default);
}

/// <summary>
/// (3.2.0) Executes one task. The substrate hands the task back; the
/// consumer reads <see cref="ProactiveTask.Payload"/> and runs it.
/// </summary>
public interface IProactiveTaskRunner
{
    /// <summary>Backend self-identification — "workflow-engine", "delegate", "null".</summary>
    string BackendId { get; }

    /// <summary>
    /// Execute one task. <paramref name="variables"/> carry trigger-time
    /// context (event payload, manual-invoke args, …) the runner can
    /// substitute into prompts or pass through.
    /// </summary>
    ValueTask<ProactiveTaskRunResult> RunAsync(
        ProactiveTask                  task,
        IDictionary<string, string>?   variables = null,
        CancellationToken              ct        = default);
}

/// <summary>
/// (3.2.0) The scheduling loop. Owns cron parsing + last-run tracking +
/// event dispatch. Ticked once a minute by
/// <see cref="ProactiveSchedulerBackgroundService"/>.
/// </summary>
public interface IProactiveScheduler
{
    /// <summary>Backend self-identification.</summary>
    string BackendId { get; }

    /// <summary>Current snapshot — populated by <see cref="RefreshAsync"/>.</summary>
    IReadOnlyList<ProactiveTask> Tasks { get; }

    /// <summary>Any load errors from the source.</summary>
    IReadOnlyList<ProactiveTaskLoadError> LoadErrors { get; }

    /// <summary>
    /// Next cron firing for a task. Returns null for non-cron triggers
    /// or unparseable expressions.
    /// </summary>
    DateTimeOffset? GetNextRun(ProactiveTask task, DateTimeOffset after);

    /// <summary>
    /// Re-snapshot tasks from the source. Drops state for tasks the
    /// source no longer reports; leaves last-run state for surviving
    /// tasks intact.
    /// </summary>
    Task RefreshAsync(CancellationToken ct = default);

    /// <summary>
    /// Tick. Run every task whose cron next-run is at-or-before
    /// <paramref name="now"/> and that hasn't already fired for the
    /// matching minute. Called once a minute by the background service.
    /// </summary>
    Task TickAsync(DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Fire every event-triggered task matching the event name.</summary>
    Task DispatchEventAsync(
        string                       eventName,
        IDictionary<string, string>? variables = null,
        CancellationToken            ct        = default);

    /// <summary>One-shot manual run by task id.</summary>
    Task<ProactiveTaskRunResult> RunByIdAsync(
        string                       id,
        IDictionary<string, string>? variables = null,
        CancellationToken            ct        = default);
}
