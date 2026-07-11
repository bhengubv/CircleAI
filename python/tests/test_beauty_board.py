"""test_beauty_board.py — CircleAI.Beauty port.

Covers InMemoryBeautyBoard (treatments, appointment range, skin profiles,
concern-driven recommendations) and BeautyDomainContext. C# is the exact spec.
"""
from __future__ import annotations

from datetime import datetime, timezone
from decimal import Decimal

import pytest

from circle_ai import (
    BeautyAppointment,
    BeautyDomainContext,
    BeautyTreatment,
    IBeautyBoard,
    InMemoryBeautyBoard,
    SkinProfile,
)


def _at(day: int) -> datetime:
    return datetime(2026, 2, day, 10, 0, tzinfo=timezone.utc)


def test_board_is_ibeautyboard():
    assert isinstance(InMemoryBeautyBoard(), IBeautyBoard)


def test_appointments_between_inclusive_ordered():
    b = InMemoryBeautyBoard()
    b.book(BeautyAppointment("a2", "Sam", "t1", _at(5), None))
    b.book(BeautyAppointment("a1", "Kim", "t1", _at(2), "note"))
    b.book(BeautyAppointment("out", "Lee", "t1", _at(20), None))
    got = b.appointments_between(_at(1), _at(10))
    assert [a.appt_id for a in got] == ["a1", "a2"]


def test_recommend_for_matches_concern_in_name():
    b = InMemoryBeautyBoard()
    b.add_treatment(BeautyTreatment("t1", "Acne Peel", 30, Decimal("450"), "ZAR"))
    b.add_treatment(BeautyTreatment("t2", "Relaxing Massage", 60, Decimal("600"), "ZAR"))
    b.save_profile(SkinProfile("Kim", "oily", ["acne"]))
    recs = b.recommend_for("Kim")
    assert {t.treatment_id for t in recs} == {"t1"}


def test_recommend_for_unknown_client_empty():
    assert InMemoryBeautyBoard().recommend_for("Nobody") == []


def test_price_is_decimal():
    b = InMemoryBeautyBoard()
    b.add_treatment(BeautyTreatment("t1", "Facial", 45, Decimal("399.99"), "ZAR"))
    assert b.get_treatment("t1").price == Decimal("399.99")


def test_beauty_domain_context():
    assert BeautyDomainContext.SystemPromptSnippet.startswith("[DOMAIN: Beauty]")
    assert list(BeautyDomainContext.ComplianceFlags) == [
        "POPIA",
        "Medicines_Act_cosmetic_claims",
    ]
