"""test_prefix_cache.py — PrefixCacheService key derivation + LRU eviction."""
from __future__ import annotations

import hashlib
import os

import pytest

from circle_ai.inference import PrefixCacheService


def test_key_for_derivation(tmp_path):
    key = PrefixCacheService.key_for("model-x", "you are helpful")
    mh = hashlib.sha256(b"model-x").hexdigest()[:16]
    sh = hashlib.sha256(b"you are helpful").hexdigest()[:16]
    assert key == f"{mh}_{sh}"


def test_key_for_none_when_no_system_prompt():
    assert PrefixCacheService.key_for("model", None) is None
    assert PrefixCacheService.key_for("model", "") is None
    assert PrefixCacheService.key_for("", "sys") is None
    assert PrefixCacheService.key_for("  ", "sys") is None


def test_path_for_and_has_entry(tmp_path):
    svc = PrefixCacheService(str(tmp_path))
    key = PrefixCacheService.key_for("m", "s")
    path = svc.path_for(key)
    assert path.endswith(f"{key}.session")


async def test_has_entry_async(tmp_path):
    svc = PrefixCacheService(str(tmp_path))
    key = PrefixCacheService.key_for("m", "s")
    assert await svc.has_entry_async(key) is False
    with open(svc.path_for(key), "w") as fh:
        fh.write("snapshot")
    assert await svc.has_entry_async(key) is True


def test_touch_updates_mtime(tmp_path):
    svc = PrefixCacheService(str(tmp_path))
    key = PrefixCacheService.key_for("m", "s")
    p = svc.path_for(key)
    with open(p, "w") as fh:
        fh.write("x")
    os.utime(p, (1000, 1000))
    svc.touch(key)
    assert os.stat(p).st_mtime > 1000


def test_ctor_requires_root():
    with pytest.raises(ValueError):
        PrefixCacheService("")


async def test_evict_if_needed_is_bounded(tmp_path, monkeypatch):
    import circle_ai.inference.prefix_cache as pc

    # Shrink the cap so a couple of small files trigger eviction.
    monkeypatch.setattr(pc, "_CAP_BYTES", 100)
    svc = PrefixCacheService(str(tmp_path))

    import time
    # Write 3 files, each 60 bytes, oldest-first mtimes.
    for i in range(3):
        p = svc.path_for(f"k{i}")
        with open(p, "wb") as fh:
            fh.write(b"x" * 60)
        os.utime(p, (1000 + i, 1000 + i))

    await svc.evict_if_needed_async()
    remaining = sorted(n for n in os.listdir(str(tmp_path)) if n.endswith(".session"))
    # 3 * 60 = 180 > 100; evict oldest (k0), then 120 > 100; evict k1; 60 <= 100 stop.
    assert remaining == ["k2.session"]


async def test_evict_noop_when_under_cap(tmp_path):
    svc = PrefixCacheService(str(tmp_path))
    p = svc.path_for("k0")
    with open(p, "wb") as fh:
        fh.write(b"tiny")
    await svc.evict_if_needed_async()
    assert os.path.isfile(p)
