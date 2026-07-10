"""test_kv_compression.py — KvCompressionMode apply/read + PowerBudgetPolicy."""
from __future__ import annotations

import pytest

from circle_ai.inference import (
    InMemoryKvCompressionNative,
    KvCompressionApplyResult,
    KvCompressionMode,
    MnnKvCompression,
    PowerBudget,
    PowerBudgetPolicy,
)


def test_mode_ordinals():
    assert int(KvCompressionMode.OFF) == 0
    assert int(KvCompressionMode.TURBO_QUANT_4BIT) == 1
    assert int(KvCompressionMode.TURBO_QUANT_3BIT) == 2
    assert int(KvCompressionMode.TURBO_QUANT_2BIT) == 3


def test_apply_and_read_roundtrip():
    kv = MnnKvCompression()
    h = object()
    assert kv.set(h, KvCompressionMode.TURBO_QUANT_3BIT) == KvCompressionApplyResult.APPLIED
    assert kv.get(h) == KvCompressionMode.TURBO_QUANT_3BIT


def test_default_mode_is_off():
    kv = MnnKvCompression()
    assert kv.get(object()) == KvCompressionMode.OFF


def test_invalid_mode_status():
    native = InMemoryKvCompressionNative()
    # Native reports 1 for an out-of-range mode; wrapper maps to INVALID_MODE.
    assert native.set_mode(object(), 9) == 1


def test_set_requires_handle():
    kv = MnnKvCompression()
    with pytest.raises(ValueError):
        kv.set(None, KvCompressionMode.OFF)
    with pytest.raises(ValueError):
        kv.get(None)


# ── PowerBudgetPolicy ─────────────────────────────────────────────────────


def test_resolve_none_honours_requested():
    r = PowerBudgetPolicy.resolve(PowerBudget.NONE, 5000)
    assert r.max_tokens == 5000
    assert r.preferred_kv_mode == KvCompressionMode.TURBO_QUANT_4BIT
    assert r.prefer_smaller_model_in_chain is False


def test_resolve_low_caps_64_and_prefers_smaller():
    r = PowerBudgetPolicy.resolve(PowerBudget.LOW, 5000)
    assert r.max_tokens == 64
    assert r.prefer_smaller_model_in_chain is True


def test_resolve_normal_caps_512():
    assert PowerBudgetPolicy.resolve(PowerBudget.NORMAL, 5000).max_tokens == 512
    # under cap: requested honoured
    assert PowerBudgetPolicy.resolve(PowerBudget.NORMAL, 100).max_tokens == 100


def test_resolve_high_caps_2048_and_full_kv():
    r = PowerBudgetPolicy.resolve(PowerBudget.HIGH, 5000)
    assert r.max_tokens == 2048
    assert r.preferred_kv_mode == KvCompressionMode.OFF


def test_normal_downgrades_on_low_battery():
    r = PowerBudgetPolicy.resolve(PowerBudget.NORMAL, 5000, battery_level_percent=10)
    assert r.max_tokens == 64  # became LOW
    assert r.prefer_smaller_model_in_chain is True


def test_high_downgrades_on_thermal():
    r = PowerBudgetPolicy.resolve(PowerBudget.HIGH, 5000, thermal_throttled=True)
    assert r.max_tokens == 512  # became NORMAL
    assert r.preferred_kv_mode == KvCompressionMode.TURBO_QUANT_4BIT


def test_battery_at_15_does_not_downgrade():
    # C# uses `< 15`, so exactly 15 stays NORMAL.
    assert PowerBudgetPolicy.resolve(PowerBudget.NORMAL, 5000, battery_level_percent=15).max_tokens == 512
