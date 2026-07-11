"""test_distribution_board.py — CircleAI.Distribution port.

Covers the file-sync / peer-advertiser null defaults, the DefaultAppStoreSubmitter
(known-store validation), the DefaultSignedDeltaUpdater (HMAC-SHA256 verify +
channel version ordering), and the OEM / carrier preload catalogues. C# is the
exact spec.
"""
from __future__ import annotations

import hmac
from hashlib import sha256

import pytest

from circle_ai.distribution import (
    AppStorePackage,
    DefaultAppStoreSubmitter,
    DefaultCarrierPreloadCatalog,
    DefaultOemPreloadCatalog,
    DefaultSignedDeltaUpdater,
    DeltaUpdate,
    FileMetadata,
    IAppStoreSubmitter,
    ICarrierPreloadCatalog,
    IOemPreloadCatalog,
    ISignedDeltaUpdater,
    NullFileSync,
    NullPeerAdvertiser,
    Peer,
)


async def test_null_file_sync_and_peer_advertiser():
    fs = NullFileSync.Instance
    pa = NullPeerAdvertiser.Instance
    assert fs.backend_id == "null" and pa.backend_id == "null"
    assert await fs.has_async("h") is False
    assert await fs.fetch_async("h") is None
    await fs.announce_async(FileMetadata("h", "n", 10), b"data")  # no-op
    assert await pa.discover_async() == []


async def test_app_store_submitter_validates_known_stores():
    sub = DefaultAppStoreSubmitter()
    assert isinstance(sub, IAppStoreSubmitter)
    ok = await sub.submit_async(AppStorePackage("PlayStore", "/pkg.aab", "1.0.0", {"track": "prod"}))
    assert ok is True
    # Unknown store -> False, not recorded.
    assert await sub.submit_async(AppStorePackage("BootlegStore", "/p", "1.0.0", {})) is False
    assert [p.store_name for p in sub.submitted] == ["PlayStore"]


async def test_app_store_submitter_guards():
    sub = DefaultAppStoreSubmitter()
    with pytest.raises(ValueError):
        await sub.submit_async(AppStorePackage("  ", "/p", "1", {}))
    with pytest.raises(ValueError):
        await sub.submit_async(AppStorePackage("PlayStore", " ", "1", {}))
    with pytest.raises(ValueError):
        await sub.submit_async(AppStorePackage("PlayStore", "/p", "", {}))


def _sign(key: bytes, channel: str, frm: str, to: str, payload: bytes) -> bytes:
    msg = f"{channel}|{frm}|{to}|".encode("utf-8") + payload
    return hmac.new(key, msg, sha256).digest()


async def test_signed_delta_updater_verifies_and_orders():
    key = b"0123456789abcdef"  # 16 bytes
    upd = DefaultSignedDeltaUpdater(key)
    assert isinstance(upd, ISignedDeltaUpdater)
    payload = b"deltabytes"
    sig = _sign(key, "stable", "1.0.0", "1.1.0", payload)
    assert await upd.apply_async(DeltaUpdate("stable", "1.0.0", "1.1.0", payload, sig)) is True
    assert upd.current_version("stable") == "1.1.0"

    # Wrong FromVersion (channel now at 1.1.0) -> rejected.
    sig2 = _sign(key, "stable", "1.0.0", "1.2.0", payload)
    assert await upd.apply_async(DeltaUpdate("stable", "1.0.0", "1.2.0", payload, sig2)) is False

    # Bad signature -> rejected.
    good = _sign(key, "stable", "1.1.0", "1.2.0", payload)
    tampered = bytes([good[0] ^ 0xFF]) + good[1:]
    assert await upd.apply_async(DeltaUpdate("stable", "1.1.0", "1.2.0", payload, tampered)) is False


async def test_signed_delta_updater_blank_fields_false():
    upd = DefaultSignedDeltaUpdater(b"0123456789abcdef")
    assert await upd.apply_async(DeltaUpdate("  ", "1", "2", b"", b"")) is False
    assert await upd.apply_async(DeltaUpdate("c", "1", "  ", b"", b"")) is False


def test_signed_delta_updater_short_key_raises():
    with pytest.raises(ValueError):
        DefaultSignedDeltaUpdater(b"short")


def test_preload_catalogues():
    oem = DefaultOemPreloadCatalog()
    car = DefaultCarrierPreloadCatalog()
    assert isinstance(oem, IOemPreloadCatalog) and isinstance(car, ICarrierPreloadCatalog)
    assert oem.partners == ["Tecno", "Itel", "Samsung mid-tier", "Xiaomi", "Huawei"]
    assert car.carriers == ["MTN", "Vodacom", "Cell C", "Telkom", "Safaricom", "Airtel"]
