// Goal.cs
//
// A user goal that B! tracks and proactively helps with.
// Inspired by the way Samantha in *Her* remembered what Theodore cared about.

using System;

namespace Circle.AI.Memory;

/// <summary>Lifecycle state of a <see cref="Goal"/>.</summary>
public enum GoalStatus
{
    /// <summary>Goal is currently being pursued.</summary>
    Active,
    /// <summary>Goal has been achieved.</summary>
    Completed,
    /// <summary>Goal has been abandoned without completion.</summary>
    Abandoned,
}

/// <summary>Relative importance of a <see cref="Goal"/>.</summary>
public enum GoalPriority
{
    /// <summary>Nice-to-have; may be deferred.</summary>
    Low,
    /// <summary>Standard importance.</summary>
    Normal,
    /// <summary>Urgent or critical to the user.</summary>
    High,
}

/// <summary>
/// A user goal that B! tracks and proactively helps with.
/// Inspired by the way Samantha in <em>Her</em> remembered what Theodore cared about.
/// </summary>
/// <param name="Id">Unique stable identifier for this goal.</param>
/// <param name="UserId">Owner of this goal.</param>
/// <param name="Title">Short, human-readable title.</param>
/// <param name="Description">Full description of what the user wants to achieve.</param>
/// <param name="Status">Current lifecycle state.</param>
/// <param name="Priority">Relative importance.</param>
/// <param name="CreatedUtc">When this goal was first recorded (UTC).</param>
/// <param name="DueUtc">Optional deadline (UTC).</param>
/// <param name="CompletedUtc">When the goal was completed or abandoned (UTC).</param>
/// <param name="Notes">Freeform notes B! or the user has attached to this goal.</param>
public sealed record Goal(
    string Id,
    string UserId,
    string Title,
    string Description,
    GoalStatus Status,
    GoalPriority Priority,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? DueUtc = null,
    DateTimeOffset? CompletedUtc = null,
    string? Notes = null)
{
    // ------------------------------------------------------------------
    // Progress tracking
    // ------------------------------------------------------------------

    /// <summary>
    /// Fraction of the goal completed, in the range [0.0, 1.0].
    /// <c>0.0</c> = not started; <c>1.0</c> = fully achieved.
    /// Stored separately from <see cref="Status"/> so that B! can show
    /// fine-grained progress bars before a goal is officially completed.
    /// </summary>
    public float Progress { get; set; } = 0f;

    /// <summary>
    /// Returns a copy of this goal with <see cref="Progress"/> advanced by
    /// <paramref name="delta"/>, clamped to [0.0, 1.0].
    /// </summary>
    /// <param name="delta">
    /// Amount to add to the current progress. May be negative to regress
    /// progress (e.g. when a sub-task is un-completed). The result is always
    /// clamped: <c>clamp(Progress + delta, 0.0, 1.0)</c>.
    /// </param>
    public Goal AdvanceProgress(float delta) =>
        this with { Progress = Math.Clamp(Progress + delta, 0f, 1f) };
}
