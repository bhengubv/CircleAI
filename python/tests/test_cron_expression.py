"""test_cron_expression.py

Verifies the 5-field cron parser ported from CircleAI.Companion.Proactive
(CronExpression.cs). Covers field parsing (*, int, range, list, step),
match semantics (AND of day-of-month and day-of-week), the .NET DayOfWeek
mapping (Sunday=0), and next-occurrence search with the one-year bound.
"""
from __future__ import annotations

from datetime import datetime, timezone

import pytest

from circle_ai.companion.proactive.cron_expression import CronExpression


def _dt(y, mo, d, h, mi) -> datetime:
    return datetime(y, mo, d, h, mi, tzinfo=timezone.utc)


# ── parsing ─────────────────────────────────────────────────────────────────


def test_requires_five_fields() -> None:
    with pytest.raises(ValueError):
        CronExpression.parse("* * * *")
    with pytest.raises(ValueError):
        CronExpression.parse("* * * * * *")


def test_none_expression_raises() -> None:
    with pytest.raises(ValueError):
        CronExpression.parse(None)  # type: ignore[arg-type]


def test_star_matches_everything() -> None:
    cron = CronExpression.parse("* * * * *")
    assert cron.matches(_dt(2026, 7, 8, 3, 17)) is True


def test_specific_minute_hour() -> None:
    cron = CronExpression.parse("30 6 * * *")
    assert cron.matches(_dt(2026, 7, 8, 6, 30)) is True
    assert cron.matches(_dt(2026, 7, 8, 6, 31)) is False
    assert cron.matches(_dt(2026, 7, 8, 7, 30)) is False


def test_range_field() -> None:
    cron = CronExpression.parse("0 9-17 * * *")
    assert cron.matches(_dt(2026, 7, 8, 9, 0)) is True
    assert cron.matches(_dt(2026, 7, 8, 17, 0)) is True
    assert cron.matches(_dt(2026, 7, 8, 18, 0)) is False


def test_list_field() -> None:
    cron = CronExpression.parse("0,15,30,45 * * * *")
    assert cron.matches(_dt(2026, 7, 8, 1, 15)) is True
    assert cron.matches(_dt(2026, 7, 8, 1, 20)) is False


def test_step_field() -> None:
    cron = CronExpression.parse("*/15 * * * *")
    for mi in (0, 15, 30, 45):
        assert cron.matches(_dt(2026, 7, 8, 1, mi)) is True
    assert cron.matches(_dt(2026, 7, 8, 1, 7)) is False


def test_step_over_range() -> None:
    cron = CronExpression.parse("0 0-6/2 * * *")  # hours 0,2,4,6
    for h in (0, 2, 4, 6):
        assert cron.matches(_dt(2026, 7, 8, h, 0)) is True
    assert cron.matches(_dt(2026, 7, 8, 1, 0)) is False


def test_out_of_range_raises() -> None:
    with pytest.raises(ValueError):
        CronExpression.parse("60 * * * *")  # minute max 59
    with pytest.raises(ValueError):
        CronExpression.parse("* 24 * * *")  # hour max 23
    with pytest.raises(ValueError):
        CronExpression.parse("* * * * 7")  # dow max 6


def test_bad_step_raises() -> None:
    with pytest.raises(ValueError):
        CronExpression.parse("*/0 * * * *")
    with pytest.raises(ValueError):
        CronExpression.parse("*/x * * * *")


# ── day-of-week mapping (Sunday = 0) ────────────────────────────────────────


def test_sunday_is_zero() -> None:
    # 2026-07-12 is a Sunday.
    sunday = _dt(2026, 7, 12, 0, 0)
    assert sunday.isoweekday() == 7
    cron = CronExpression.parse("0 0 * * 0")
    assert cron.matches(sunday) is True
    # Monday (2026-07-13) should not match dow=0.
    assert cron.matches(_dt(2026, 7, 13, 0, 0)) is False


def test_monday_is_one() -> None:
    monday = _dt(2026, 7, 13, 8, 0)  # Monday
    cron = CronExpression.parse("0 8 * * 1")
    assert cron.matches(monday) is True


def test_weekday_range_mon_fri() -> None:
    cron = CronExpression.parse("0 9 * * 1-5")
    assert cron.matches(_dt(2026, 7, 13, 9, 0)) is True  # Mon
    assert cron.matches(_dt(2026, 7, 17, 9, 0)) is True  # Fri
    assert cron.matches(_dt(2026, 7, 12, 9, 0)) is False  # Sun
    assert cron.matches(_dt(2026, 7, 18, 9, 0)) is False  # Sat


# ── AND semantics for dom + dow ─────────────────────────────────────────────


def test_dom_and_dow_both_required() -> None:
    # Fire only when it's the 13th AND a Monday. 2026-07-13 is a Monday.
    cron = CronExpression.parse("0 0 13 * 1")
    assert cron.matches(_dt(2026, 7, 13, 0, 0)) is True
    # The 13th of a month that isn't Monday should NOT match (AND, not OR).
    # 2026-08-13 is a Thursday.
    assert cron.matches(_dt(2026, 8, 13, 0, 0)) is False


# ── next occurrence ─────────────────────────────────────────────────────────


def test_next_occurrence_daily() -> None:
    cron = CronExpression.parse("30 6 * * *")
    nxt = cron.get_next_occurrence(_dt(2026, 7, 8, 6, 0))
    assert nxt == _dt(2026, 7, 8, 6, 30)


def test_next_occurrence_rolls_to_tomorrow() -> None:
    cron = CronExpression.parse("30 6 * * *")
    nxt = cron.get_next_occurrence(_dt(2026, 7, 8, 7, 0))
    assert nxt == _dt(2026, 7, 9, 6, 30)


def test_next_occurrence_is_strictly_after() -> None:
    cron = CronExpression.parse("* * * * *")
    # Starts search at after + 1 minute.
    nxt = cron.get_next_occurrence(_dt(2026, 7, 8, 6, 30))
    assert nxt == _dt(2026, 7, 8, 6, 31)


def test_next_occurrence_specific_weekday() -> None:
    cron = CronExpression.parse("0 0 * * 0")  # Sundays midnight
    nxt = cron.get_next_occurrence(_dt(2026, 7, 8, 12, 0))  # Wed
    # Next Sunday is 2026-07-12.
    assert nxt == _dt(2026, 7, 12, 0, 0)


def test_impossible_expression_raises() -> None:
    # Feb 30th never exists -> no match within a year.
    cron = CronExpression.parse("0 0 30 2 *")
    with pytest.raises(RuntimeError):
        cron.get_next_occurrence(_dt(2026, 1, 1, 0, 0))
