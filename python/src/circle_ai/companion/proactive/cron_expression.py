# companion/proactive/cron_expression.py
#
# Minimal 5-field cron parser: ``minute hour day-of-month month day-of-week``.
# Ported from CircleAI.Companion.Proactive (CronExpression.cs) — the C# reference.
#
# Supports ``*``, integers, ranges (``1-5``), lists (``1,15,30``), and step
# values (``*/15``). Day-of-week uses 0=Sunday through 6=Saturday (matching
# .NET's ``DayOfWeek`` enum). Day-of-month AND day-of-week must both match
# (AND semantics, as in the reference — two tasks give OR).

from __future__ import annotations

from datetime import datetime, timedelta
from typing import Set


def _dotnet_day_of_week(moment: datetime) -> int:
    """.NET ``DateTimeOffset.DayOfWeek`` — Sunday=0 .. Saturday=6."""
    # Python isoweekday(): Mon=1..Sun=7 -> % 7 gives Sun=0, Mon=1..Sat=6.
    return moment.isoweekday() % 7


class CronExpression:
    """Five-field cron expression parser.

    Mirrors ``CircleAI.Companion.Proactive.CronExpression``. Public surface:
    :meth:`parse`, :meth:`get_next_occurrence`, :meth:`matches`.
    """

    __slots__ = ("_minutes", "_hours", "_days_of_month", "_months", "_days_of_week")

    def __init__(
        self,
        minutes: Set[int],
        hours: Set[int],
        days_of_month: Set[int],
        months: Set[int],
        days_of_week: Set[int],
    ) -> None:
        self._minutes = minutes
        self._hours = hours
        self._days_of_month = days_of_month
        self._months = months
        self._days_of_week = days_of_week

    @staticmethod
    def parse(expression: str) -> "CronExpression":
        if expression is None:
            raise ValueError("expression required")
        # Split on whitespace, dropping empties and trimming (Split with
        # RemoveEmptyEntries | TrimEntries).
        fields = [f for f in expression.split() if f]
        if len(fields) != 5:
            raise ValueError(
                f"Cron expression must have 5 fields, got {len(fields)}: '{expression}'"
            )
        return CronExpression(
            CronExpression._parse_field(fields[0], 0, 59),
            CronExpression._parse_field(fields[1], 0, 23),
            CronExpression._parse_field(fields[2], 1, 31),
            CronExpression._parse_field(fields[3], 1, 12),
            CronExpression._parse_field(fields[4], 0, 6),
        )

    def get_next_occurrence(self, after: datetime) -> datetime:
        """Next UTC time at or after ``after`` when the expression matches.

        Hard upper bound of one year forward — if nothing matches in 365 days the
        expression is effectively dead and we raise rather than spin.
        """
        t = after + timedelta(minutes=1)
        # Truncate to the minute (seconds/microseconds zeroed).
        t = t.replace(second=0, microsecond=0)
        limit = _add_years(t, 1)
        while t <= limit:
            if self.matches(t):
                return t
            t = t + timedelta(minutes=1)
        raise RuntimeError("Cron expression does not match any time in the next year.")

    def matches(self, moment: datetime) -> bool:
        if moment.minute not in self._minutes:
            return False
        if moment.hour not in self._hours:
            return False
        if moment.day not in self._days_of_month:
            return False
        if moment.month not in self._months:
            return False
        # Day-of-month AND day-of-week must both match (AND for predictability).
        if _dotnet_day_of_week(moment) not in self._days_of_week:
            return False
        return True

    @staticmethod
    def _parse_field(field: str, min_v: int, max_v: int) -> Set[int]:
        values: Set[int] = set()
        for part in field.split(","):
            CronExpression._expand_part(part.strip(), min_v, max_v, values)
        if len(values) == 0:
            raise ValueError(f"Cron field '{field}' resolved to no values.")
        return values

    @staticmethod
    def _expand_part(part: str, min_v: int, max_v: int, sink: Set[int]) -> None:
        step = 1
        slash = part.find("/")
        if slash >= 0:
            step_str = part[slash + 1 :]
            try:
                step = int(step_str)
            except ValueError:
                raise ValueError(f"Cron step '{part}' is not a positive integer.")
            if step <= 0:
                raise ValueError(f"Cron step '{part}' is not a positive integer.")
            part = part[:slash]

        if part == "*":
            range_start = min_v
            range_end = max_v
        elif "-" in part:
            dash = part.find("-")
            range_start = int(part[:dash])
            range_end = int(part[dash + 1 :])
        else:
            range_start = int(part)
            range_end = range_start

        if range_start < min_v or range_end > max_v or range_start > range_end:
            raise ValueError(f"Cron part '{part}' out of range [{min_v},{max_v}].")

        v = range_start
        while v <= range_end:
            sink.add(v)
            v += step


def _add_years(dt: datetime, years: int) -> datetime:
    """``DateTimeOffset.AddYears`` — add calendar years, clamping Feb 29 -> Feb 28."""
    try:
        return dt.replace(year=dt.year + years)
    except ValueError:
        # Feb 29 -> non-leap year: .NET clamps to the 28th.
        return dt.replace(year=dt.year + years, day=28)


__all__ = ["CronExpression"]
