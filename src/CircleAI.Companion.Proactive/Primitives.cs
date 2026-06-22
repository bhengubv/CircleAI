// Primitives.cs
//
// (3.2.0) Shared shapes for the proactive scheduling surface. A
// `ProactiveTask` is opaque to the substrate — its `Payload` is whatever
// the consumer's `IProactiveTaskRunner` knows how to execute. CircleUp
// puts a `Workflow` object in there; a Concierge "remind me at 6pm"
// scheduler could put a `ProactiveReminder`; a third-party plugin
// could put anything.

using System;
using System.Collections.Generic;

namespace CircleAI.Companion.Proactive;

/// <summary>
/// One scheduled task. Opaque from the substrate's perspective — the
/// host's `IProactiveTaskRunner` reads the `Payload` and executes it.
/// </summary>
/// <param name="Id">Unique task id within its source. Used for last-run tracking.</param>
/// <param name="Trigger">Cron / event / manual trigger.</param>
/// <param name="Payload">Consumer-owned object. Substrate never inspects it.</param>
/// <param name="SourceContext">Optional context tag (vault path, tenant id, …) so multi-tenant sources keep per-context last-run state separate.</param>
public sealed record ProactiveTask(
    string  Id,
    ProactiveTrigger Trigger,
    object  Payload,
    string? SourceContext = null);

/// <summary>
/// How a task fires. Exactly one of <see cref="Cron"/>, <see cref="OnEvent"/>,
/// or <see cref="Manual"/> is non-null.
/// </summary>
/// <param name="Cron">5-field cron expression — see <see cref="CronExpression"/>.</param>
/// <param name="OnEvent">Event name (e.g. "note-saved", "task-created").</param>
/// <param name="Manual">True if the task only fires when explicitly invoked.</param>
public sealed record ProactiveTrigger(
    string? Cron   = null,
    string? OnEvent = null,
    bool    Manual = false);

/// <summary>One run outcome — success or failure with a message.</summary>
public sealed record ProactiveTaskRunResult(
    string  TaskId,
    bool    Success,
    string? FailureMessage = null);

/// <summary>One parse failure surfaced through the source.</summary>
public sealed record ProactiveTaskLoadError(
    string  TaskId,
    string  Message,
    string? SourceContext = null);
