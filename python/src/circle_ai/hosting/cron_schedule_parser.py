"""CronScheduleParser — port of CircleAI.Hosting.CronScheduleParser.

Minimal 5-field cron expression parser. Computes the next occurrence of a
5-field cron expression strictly after a given UTC datetime. Supports:

    *          — every unit
    N          — fixed value
    N,M,...    — list of values
    */N        — step (every N units)
    N-M        — range
    N-M/S      — range with step

Field order:  minute  hour  dom  month  dow
              0-59    0-23  1-31  1-12  0-6 (0 = Sunday, .NET DayOfWeek)

No external dependencies — pure stdlib. The search starts at the next whole
minute after ``after`` and advances field-by-field, capped at 5 years to
avoid infinite loops on impossible expressions (e.g. ``0 9 31 2 *``).

Timezone note: the C# operates on ``UtcDateTime`` and builds candidates with
``TimeSpan.Zero`` offset. This port operates on timezone-aware UTC datetimes;
naive datetimes are treated as UTC.
"""
from __future__ import annotations

import datetime as _dt
from typing import Set

__all__ = ["CronScheduleParser"]

_UTC = _dt.timezone.utc


def _as_utc(dt: _dt.datetime) -> _dt.datetime:
    """Project any datetime into aware-UTC (naive treated as UTC)."""
    if dt.tzinfo is None:
        return dt.replace(tzinfo=_UTC)
    return dt.astimezone(_UTC)


def _dotnet_day_of_week(dt: _dt.datetime) -> int:
    """Return .NET ``DayOfWeek`` ordinal (Sunday=0 .. Saturday=6).

    Python's ``weekday()`` is Monday=0 .. Sunday=6; ``isoweekday()`` is
    Monday=1 .. Sunday=7. .NET DayOfWeek is Sunday=0, so ``isoweekday() % 7``
    maps Sunday(7)->0, Monday(1)->1, ... Saturday(6)->6.
    """
    return dt.isoweekday() % 7


class CronScheduleParser:
    """Computes the next occurrence of a 5-field cron expression after a given
    UTC datetime. Handles wildcards, lists, steps, and ranges. Mirrors the
    static C# ``CronScheduleParser``.
    """

    @staticmethod
    def get_next_occurrence(cron_expression: str, after: _dt.datetime) -> _dt.datetime:
        """Return the earliest aware-UTC timestamp strictly after ``after``
        that satisfies ``cron_expression``. Mirrors ``GetNextOccurrence``.

        Raises ``ValueError`` when the expression cannot be parsed, and
        ``RuntimeError`` when no occurrence is found within 5 years.
        """
        if cron_expression is None or not cron_expression.strip():
            raise ValueError("cronExpression is required")

        parts = [p for p in cron_expression.strip().split(" ") if p]
        if len(parts) != 5:
            raise ValueError(
                f"Cron expression must have exactly 5 fields, got {len(parts)}: "
                f"'{cron_expression}'"
            )

        minute_set = CronScheduleParser._parse_field(parts[0], 0, 59)
        hour_set = CronScheduleParser._parse_field(parts[1], 0, 23)
        dom_set = CronScheduleParser._parse_field(parts[2], 1, 31)
        month_set = CronScheduleParser._parse_field(parts[3], 1, 12)
        dow_set = CronScheduleParser._parse_field(parts[4], 0, 6)

        utc_after = _as_utc(after)

        # Start searching from the next whole minute after `after`.
        candidate = utc_after.replace(second=0, microsecond=0) + _dt.timedelta(minutes=1)

        limit = _add_years(candidate, 5)

        while candidate <= limit:
            # Month check.
            if candidate.month not in month_set:
                candidate = CronScheduleParser._advance_to_next_month(candidate, month_set)
                continue

            # Day-of-month check.
            if candidate.day not in dom_set:
                candidate = _midnight(candidate + _dt.timedelta(days=1))
                continue

            # Day-of-week check (.NET DayOfWeek, Sunday=0).
            if _dotnet_day_of_week(candidate) not in dow_set:
                candidate = _midnight(candidate + _dt.timedelta(days=1))
                continue

            # Hour check.
            if candidate.hour not in hour_set:
                candidate = CronScheduleParser._advance_to_next_hour(candidate, hour_set)
                continue

            # Minute check.
            if candidate.minute not in minute_set:
                candidate = candidate + _dt.timedelta(minutes=1)
                continue

            # All fields match.
            return candidate

        raise RuntimeError(
            f"No occurrence found within 5 years for cron expression '{cron_expression}'."
        )

    # ── Parsing helpers ────────────────────────────────────────────────────

    @staticmethod
    def _parse_field(field: str, minimum: int, maximum: int) -> Set[int]:
        """Parse one cron field into the set of matching integer values."""
        result: Set[int] = set()
        for part in field.split(","):
            CronScheduleParser._parse_part(part.strip(), minimum, maximum, result)
        return result

    @staticmethod
    def _parse_part(part: str, minimum: int, maximum: int, result: Set[int]) -> None:
        step = None
        core = part

        slash_idx = part.find("/")
        if slash_idx >= 0:
            step_str = part[slash_idx + 1 :]
            try:
                s = int(step_str)
            except ValueError:
                raise ValueError(f"Invalid step in cron field part '{part}'.")
            if s < 1:
                raise ValueError(f"Invalid step in cron field part '{part}'.")
            step = s
            core = part[:slash_idx]

        if core == "*":
            range_min = minimum
            range_max = maximum
        else:
            dash_idx = core.find("-")
            if dash_idx >= 0:
                try:
                    range_min = int(core[:dash_idx])
                    range_max = int(core[dash_idx + 1 :])
                except ValueError:
                    raise ValueError(f"Invalid range in cron field part '{part}'.")
            else:
                try:
                    range_min = int(core)
                except ValueError:
                    raise ValueError(f"Invalid value in cron field part '{part}'.")
                range_max = range_min

        if range_min < minimum or range_max > maximum or range_min > range_max:
            raise ValueError(
                f"Cron field value {range_min}-{range_max} out of range "
                f"[{minimum},{maximum}]."
            )

        effective_step = step if step is not None else 1
        v = range_min
        while v <= range_max:
            result.add(v)
            v += effective_step

    # ── Advancement helpers ────────────────────────────────────────────────

    @staticmethod
    def _advance_to_next_month(dt: _dt.datetime, month_set: Set[int]) -> _dt.datetime:
        year = dt.year
        month = dt.month + 1
        if month > 12:
            month = 1
            year += 1

        while year < dt.year + 6:
            if month in month_set:
                return _dt.datetime(year, month, 1, 0, 0, 0, tzinfo=_UTC)
            month += 1
            if month > 12:
                month = 1
                year += 1

        raise RuntimeError("No valid month found in cron expression.")

    @staticmethod
    def _advance_to_next_hour(dt: _dt.datetime, hour_set: Set[int]) -> _dt.datetime:
        # Try subsequent hours today.
        for h in range(dt.hour + 1, 24):
            if h in hour_set:
                return _dt.datetime(dt.year, dt.month, dt.day, h, 0, 0, tzinfo=_UTC)
        # No valid hour today — move to next day, first valid hour.
        next_day = _midnight(dt + _dt.timedelta(days=1))
        min_hour = min(hour_set)
        return _dt.datetime(
            next_day.year, next_day.month, next_day.day, min_hour, 0, 0, tzinfo=_UTC
        )


def _midnight(dt: _dt.datetime) -> _dt.datetime:
    """Midnight-UTC for the given datetime's date. Mirrors the C#
    ``DateTimeOffsetExtensions.Date`` helper.
    """
    return _dt.datetime(dt.year, dt.month, dt.day, 0, 0, 0, tzinfo=_UTC)


def _add_years(dt: _dt.datetime, years: int) -> _dt.datetime:
    """Add whole years, clamping Feb-29 to Feb-28 in non-leap years. Mirrors
    .NET ``DateTimeOffset.AddYears`` semantics.
    """
    try:
        return dt.replace(year=dt.year + years)
    except ValueError:
        # Feb 29 -> Feb 28 in the target non-leap year.
        return dt.replace(year=dt.year + years, day=28)
