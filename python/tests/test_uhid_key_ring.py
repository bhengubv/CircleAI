"""test_uhid_key_ring.py — ephemeral ECDSA P-256 UHID key ring.

Covers sign/verify round-trip, tamper rejection, cross-ring isolation,
revocation semantics (sign fails, verify still works), rotation, and DER
public-key export — mirroring the C# UhidKeyRing contract.
"""
from __future__ import annotations

import pytest

from circle_ai.security import UhidKeyRing


def test_generate_fresh_produces_active_ring():
    r = UhidKeyRing.generate_fresh("uhid-1")
    assert r.uhid_identity_id == "uhid-1"
    assert r.is_revoked is False
    assert r.revoked_at is None
    assert len(r.public_key_der) > 0
    assert r.generated_at is not None


def test_blank_identity_rejected():
    with pytest.raises(ValueError):
        UhidKeyRing.generate_fresh("")
    with pytest.raises(ValueError):
        UhidKeyRing.generate_fresh("   ")


def test_sign_verify_round_trip():
    r = UhidKeyRing.generate_fresh("uhid-1")
    data = b"transaction-payload"
    sig = r.sign(data)
    assert r.verify(data, sig) is True


def test_verify_rejects_tampered_data():
    r = UhidKeyRing.generate_fresh("uhid-1")
    sig = r.sign(b"original")
    assert r.verify(b"tampered", sig) is False


def test_verify_rejects_garbage_signature():
    r = UhidKeyRing.generate_fresh("uhid-1")
    assert r.verify(b"data", b"not-a-signature") is False


def test_foreign_ring_cannot_verify():
    a = UhidKeyRing.generate_fresh("uhid-1")
    b = UhidKeyRing.generate_fresh("uhid-1")
    sig = a.sign(b"data")
    assert b.verify(b"data", sig) is False


def test_each_ring_has_distinct_id_and_key():
    a = UhidKeyRing.generate_fresh("uhid-1")
    b = UhidKeyRing.generate_fresh("uhid-1")
    assert a.ring_id != b.ring_id
    assert a.public_key_der != b.public_key_der


def test_revoke_blocks_signing_but_not_verifying():
    r = UhidKeyRing.generate_fresh("uhid-1")
    sig = r.sign(b"data")
    r.revoke()
    assert r.is_revoked is True
    assert r.revoked_at is not None
    with pytest.raises(RuntimeError):
        r.sign(b"data")
    # Historical verification still works after revocation.
    assert r.verify(b"data", sig) is True


def test_revoke_is_idempotent():
    r = UhidKeyRing.generate_fresh("uhid-1")
    r.revoke()
    first = r.revoked_at
    r.revoke()
    assert r.revoked_at == first


def test_rotate_returns_fresh_ring_and_revokes_old():
    old = UhidKeyRing.generate_fresh("uhid-1")
    old_sig = old.sign(b"data")
    new = old.rotate()
    assert old.is_revoked is True
    assert new.is_revoked is False
    assert new.ring_id != old.ring_id
    assert new.uhid_identity_id == old.uhid_identity_id
    # New ring can sign; old ring cannot.
    new_sig = new.sign(b"data")
    assert new.verify(b"data", new_sig) is True
    with pytest.raises(RuntimeError):
        old.sign(b"data")
    # Old ring's prior signature still validates against the old ring.
    assert old.verify(b"data", old_sig) is True
    # Cross-ring signatures do not validate.
    assert new.verify(b"data", old_sig) is False


def test_dispose_disables_sign_and_verify():
    r = UhidKeyRing.generate_fresh("uhid-1")
    sig = r.sign(b"data")
    r.dispose()
    assert r.verify(b"data", sig) is False
    with pytest.raises(RuntimeError):
        r.sign(b"data")


def test_context_manager_disposes():
    with UhidKeyRing.generate_fresh("uhid-1") as r:
        sig = r.sign(b"data")
        assert r.verify(b"data", sig)
    assert r.verify(b"data", sig) is False
