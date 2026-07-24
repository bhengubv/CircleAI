// Reminders.cs — (0.1.0) Scheduling + follow-up primitives.
//
// These are PRIMITIVES, not a background service: they model due dates and
// recurrence and let a caller ask "what is due as of now?". They start no timers
// and own no threads — when to poll and how to notify is the host's decision.
// Everything is deterministic and offline, which also makes it trivially testable.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.BusinessOps;

/// <summary>How often a reminder repeats.</summary>
public enum Recurrence
{
    /// <summary>One-off; does not repeat.</summary>
    None = 0,
    Daily,
    Weekly,
    Monthly,
    Yearly,
}

/// <summary>What a reminder is for (drives host presentation / filtering).</summary>
public enum ReminderKind
{
    General = 0,
    /// <summary>Chase a client (call, email).</summary>
    FollowUp,
    /// <summary>An invoice payment is coming due / is due.</summary>
    InvoiceDue,
    Custom,
}

/// <summary>
/// A recurrence rule. <see cref="Interval"/> is the step multiplier (every N
/// units); a non-positive interval is treated as 1. <see cref="Next"/> advances
/// exactly one period using calendar-correct arithmetic (month and year lengths
/// vary, so this is not just "add 30 days").
/// </summary>
public readonly record struct RecurrenceRule(Recurrence Kind, int Interval = 1)
{
    /// <summary>The non-repeating rule.</summary>
    public static readonly RecurrenceRule Once = new(Recurrence.None, 0);

    /// <summary>True when the rule actually repeats.</summary>
    public bool IsRecurring => Kind != Recurrence.None;

    /// <summary>The next occurrence after <paramref name="from"/>, or null when the rule does not repeat.</summary>
    public DateTimeOffset? Next(DateTimeOffset from)
    {
        var step = Interval <= 0 ? 1 : Interval;
        return Kind switch
        {
            Recurrence.Daily => from.AddDays(step),
            Recurrence.Weekly => from.AddDays(7 * step),
            Recurrence.Monthly => from.AddMonths(step),
            Recurrence.Yearly => from.AddYears(step),
            _ => null,
        };
    }
}

/// <summary>A scheduled reminder / follow-up.</summary>
public sealed record Reminder
{
    /// <summary>Stable id.</summary>
    public required string ReminderId { get; init; }

    /// <summary>What to be reminded about.</summary>
    public required string Title { get; init; }

    /// <summary>When it is due (UTC).</summary>
    public required DateTimeOffset DueAtUtc { get; init; }

    /// <summary>Repeat rule; defaults to one-off.</summary>
    public RecurrenceRule Repeat { get; init; } = RecurrenceRule.Once;

    /// <summary>Category.</summary>
    public ReminderKind Kind { get; init; } = ReminderKind.General;

    /// <summary>Optional link to the thing this is about (e.g. an <see cref="Invoice.InvoiceId"/> or <see cref="Client.ClientId"/>).</summary>
    public string? RelatedEntityId { get; init; }

    /// <summary>True once acted upon.</summary>
    public bool Completed { get; init; }

    /// <summary>Optional detail.</summary>
    public string? Notes { get; init; }

    /// <summary>When the reminder was created.</summary>
    public DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>True when not yet completed and its due time has arrived by <paramref name="asOf"/>.</summary>
    public bool IsDue(DateTimeOffset asOf) => !Completed && asOf >= DueAtUtc;
}

/// <summary>
/// The scheduling seam. Stores reminders, answers "what is due?", and rolls
/// recurring reminders forward on completion. Backed by <see cref="IBusinessStore"/>.
/// </summary>
public interface IReminderScheduler
{
    /// <summary>Identifies the backing implementation.</summary>
    string BackendId { get; }

    /// <summary>Stores a reminder (stamping <see cref="Reminder.CreatedAtUtc"/> if unset) and returns it.</summary>
    ValueTask<Reminder> ScheduleAsync(Reminder reminder, CancellationToken ct = default);

    /// <summary>Convenience: schedule a follow-up tied to an entity.</summary>
    ValueTask<Reminder> ScheduleFollowUpAsync(
        string relatedEntityId,
        string title,
        DateTimeOffset dueAtUtc,
        RecurrenceRule? repeat = null,
        CancellationToken ct = default);

    /// <summary>Fetches a reminder by id, or null.</summary>
    ValueTask<Reminder?> GetAsync(string reminderId, CancellationToken ct = default);

    /// <summary>
    /// Marks a reminder done. If it recurs, the next occurrence is scheduled and
    /// returned; otherwise returns null.
    /// </summary>
    ValueTask<Reminder?> CompleteAsync(string reminderId, CancellationToken ct = default);

    /// <summary>Deletes a reminder. Returns true if one was removed.</summary>
    ValueTask<bool> CancelAsync(string reminderId, CancellationToken ct = default);

    /// <summary>Reminders due (and not completed) as of <paramref name="asOf"/>, earliest first.</summary>
    ValueTask<IReadOnlyList<Reminder>> ListDueAsync(DateTimeOffset asOf, CancellationToken ct = default);

    /// <summary>All not-yet-completed reminders, earliest first.</summary>
    ValueTask<IReadOnlyList<Reminder>> ListPendingAsync(CancellationToken ct = default);

    /// <summary>Reminders linked to a given entity id, earliest first.</summary>
    ValueTask<IReadOnlyList<Reminder>> ListForEntityAsync(string relatedEntityId, CancellationToken ct = default);
}
