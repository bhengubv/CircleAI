"""test_agriculture_board.py — CircleAI.Agriculture port.

Covers InMemoryFarmBoard (fields, crops ordered by planted date, yield join +
case-insensitive variety average incl. 0.0 when empty) and
AgricultureDomainContext. C# is the exact spec.
"""
from __future__ import annotations

from datetime import datetime, timezone

import pytest

from circle_ai import (
    AgricultureDomainContext,
    Crop,
    FarmField,
    IFarmBoard,
    InMemoryFarmBoard,
    YieldRecord,
)


def _d(y: int, m: int, d: int) -> datetime:
    return datetime(y, m, d, tzinfo=timezone.utc)


def test_board_is_ifarmboard():
    assert isinstance(InMemoryFarmBoard(), IFarmBoard)


def test_crops_for_field_ordered_by_planted():
    b = InMemoryFarmBoard()
    b.add_field(FarmField("f1", 10.0, "loam", "drip"))
    b.plant(Crop("c2", "f1", "Maize", _d(2026, 3, 1), None))
    b.plant(Crop("c1", "f1", "Maize", _d(2026, 1, 1), None))
    b.plant(Crop("other", "f2", "Wheat", _d(2026, 1, 1), None))
    got = b.crops_for_field("f1")
    assert [c.crop_id for c in got] == ["c1", "c2"]


def test_avg_yield_of_variety_case_insensitive():
    b = InMemoryFarmBoard()
    b.plant(Crop("c1", "f1", "Maize", _d(2026, 1, 1), None))
    b.plant(Crop("c2", "f1", "maize", _d(2026, 1, 1), None))
    b.record_yield(YieldRecord("c1", 8.0, _d(2026, 6, 1)))
    b.record_yield(YieldRecord("c2", 10.0, _d(2026, 6, 1)))
    assert b.avg_yield_of_variety("MAIZE") == pytest.approx(9.0)


def test_avg_yield_no_rows_is_zero():
    assert InMemoryFarmBoard().avg_yield_of_variety("Nothing") == 0.0


def test_none_guards():
    b = InMemoryFarmBoard()
    for fn in (lambda: b.add_field(None), lambda: b.plant(None), lambda: b.record_yield(None)):
        with pytest.raises(ValueError):
            fn()  # type: ignore[misc]


def test_agriculture_domain_context():
    assert AgricultureDomainContext.SystemPromptSnippet.startswith("[DOMAIN: Agriculture]")
    assert list(AgricultureDomainContext.ComplianceFlags) == [
        "DAFF_regs",
        "CARA",
        "Fertilizer_Act",
        "POPIA",
    ]
