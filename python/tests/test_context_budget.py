"""test_context_budget.py — ContextWindowBudgetManager parity."""
from __future__ import annotations

import pytest

from circle_ai.inference import ContextWindowBudgetManager


def test_defaults_and_properties():
    b = ContextWindowBudgetManager(1000)
    assert b.context_size == 1000
    assert b.used_tokens == 0
    assert b.remaining_tokens == 1000
    assert b.fill_ratio == 0.0
    assert b.eviction_threshold == 0.85
    assert b.should_evict is False


def test_record_exchange_accumulates():
    b = ContextWindowBudgetManager(1000)
    b.record_exchange(300, 200)
    assert b.used_tokens == 500
    assert b.remaining_tokens == 500
    assert b.fill_ratio == 0.5


def test_should_evict_at_threshold():
    b = ContextWindowBudgetManager(1000, 0.85)
    b.record_exchange(850, 0)
    assert b.should_evict is True
    b2 = ContextWindowBudgetManager(1000, 0.85)
    b2.record_exchange(849, 0)
    assert b2.should_evict is False


def test_calculate_eviction_count():
    b = ContextWindowBudgetManager(1000)
    b.record_exchange(900, 0)
    assert b.calculate_eviction_count(0.5) == 400  # 900 - int(1000*0.5)
    assert b.calculate_eviction_count(0.95) == 0   # already below target


def test_eviction_count_truncates_like_csharp():
    b = ContextWindowBudgetManager(1001)
    b.record_exchange(600, 0)
    # int(1001 * 0.5) == 500 -> 600 - 500 = 100
    assert b.calculate_eviction_count(0.5) == 100


def test_reset_clears_usage():
    b = ContextWindowBudgetManager(1000)
    b.record_exchange(500, 100)
    b.reset()
    assert b.used_tokens == 0


def test_ctor_validation():
    with pytest.raises(ValueError):
        ContextWindowBudgetManager(0)
    with pytest.raises(ValueError):
        ContextWindowBudgetManager(100, 1.5)
    with pytest.raises(ValueError):
        ContextWindowBudgetManager(100, -0.1)


def test_record_exchange_validation():
    b = ContextWindowBudgetManager(100)
    with pytest.raises(ValueError):
        b.record_exchange(-1, 0)
    with pytest.raises(ValueError):
        b.record_exchange(0, -1)


def test_eviction_count_validation():
    b = ContextWindowBudgetManager(100)
    with pytest.raises(ValueError):
        b.calculate_eviction_count(1.1)
