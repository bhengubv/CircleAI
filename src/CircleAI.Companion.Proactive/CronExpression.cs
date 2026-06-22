// CronExpression.cs
//
// (3.2.0) Minimal 5-field cron parser: `minute hour day-of-month month
// day-of-week`. Supports `*`, integers, ranges (`1-5`), lists
// (`1,15,30`), and step values (`*/15`). Day-of-week uses 0=Sunday
// through 6=Saturday.
//
// Lifted from CircleUp's CronExpression — same parser, same semantics.
// Standalone so consumers don't have to take a Quartz / NCrontab
// dependency just to schedule a workflow every Monday morning.

using System;
using System.Collections.Generic;

namespace CircleAI.Companion.Proactive;

/// <summary>
/// (3.2.0) Five-field cron expression parser. Public surface is small —
/// <see cref="Parse"/>, <see cref="GetNextOccurrence"/>,
/// <see cref="Matches"/> — so a future swap to a third-party scheduler
/// stays trivial.
/// </summary>
public sealed class CronExpression
{
    private readonly HashSet<int> _minutes;
    private readonly HashSet<int> _hours;
    private readonly HashSet<int> _daysOfMonth;
    private readonly HashSet<int> _months;
    private readonly HashSet<int> _daysOfWeek;

    private CronExpression(
        HashSet<int> minutes,
        HashSet<int> hours,
        HashSet<int> daysOfMonth,
        HashSet<int> months,
        HashSet<int> daysOfWeek)
    {
        _minutes     = minutes;
        _hours       = hours;
        _daysOfMonth = daysOfMonth;
        _months      = months;
        _daysOfWeek  = daysOfWeek;
    }

    public static CronExpression Parse(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var fields = expression.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (fields.Length != 5)
        {
            throw new FormatException($"Cron expression must have 5 fields, got {fields.Length}: '{expression}'");
        }

        return new CronExpression(
            ParseField(fields[0], 0, 59),
            ParseField(fields[1], 0, 23),
            ParseField(fields[2], 1, 31),
            ParseField(fields[3], 1, 12),
            ParseField(fields[4], 0, 6));
    }

    /// <summary>
    /// Next UTC time at or after <paramref name="after"/> when the
    /// expression matches. Hard upper bound of one year forward — if
    /// nothing matches in 365 days the expression is effectively dead
    /// and we throw rather than spin.
    /// </summary>
    public DateTimeOffset GetNextOccurrence(DateTimeOffset after)
    {
        var t = after.AddMinutes(1);
        t = new DateTimeOffset(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, TimeSpan.Zero);
        var limit = t.AddYears(1);
        while (t <= limit)
        {
            if (Matches(t)) return t;
            t = t.AddMinutes(1);
        }
        throw new InvalidOperationException("Cron expression does not match any time in the next year.");
    }

    public bool Matches(DateTimeOffset moment)
    {
        if (!_minutes.Contains(moment.Minute)) return false;
        if (!_hours.Contains(moment.Hour))     return false;
        if (!_daysOfMonth.Contains(moment.Day)) return false;
        if (!_months.Contains(moment.Month))   return false;
        // Day-of-month AND day-of-week must both match. Standard cron
        // debates OR vs AND when both are restricted — we settle on AND
        // for predictability. Two workflows give OR.
        if (!_daysOfWeek.Contains((int)moment.DayOfWeek)) return false;
        return true;
    }

    private static HashSet<int> ParseField(string field, int min, int max)
    {
        var values = new HashSet<int>();
        foreach (var part in field.Split(','))
        {
            ExpandPart(part.Trim(), min, max, values);
        }
        if (values.Count == 0)
        {
            throw new FormatException($"Cron field '{field}' resolved to no values.");
        }
        return values;
    }

    private static void ExpandPart(string part, int min, int max, HashSet<int> sink)
    {
        var step = 1;
        var slash = part.IndexOf('/');
        if (slash >= 0)
        {
            if (!int.TryParse(part[(slash + 1)..], out step) || step <= 0)
            {
                throw new FormatException($"Cron step '{part}' is not a positive integer.");
            }
            part = part[..slash];
        }

        int rangeStart, rangeEnd;
        if (part == "*")
        {
            rangeStart = min;
            rangeEnd   = max;
        }
        else if (part.Contains('-'))
        {
            var dash = part.IndexOf('-');
            rangeStart = int.Parse(part[..dash]);
            rangeEnd   = int.Parse(part[(dash + 1)..]);
        }
        else
        {
            rangeStart = int.Parse(part);
            rangeEnd   = rangeStart;
        }

        if (rangeStart < min || rangeEnd > max || rangeStart > rangeEnd)
        {
            throw new FormatException($"Cron part '{part}' out of range [{min},{max}].");
        }

        for (var v = rangeStart; v <= rangeEnd; v += step)
        {
            sink.Add(v);
        }
    }
}
