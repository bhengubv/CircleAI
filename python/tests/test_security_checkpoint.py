"""test_security_checkpoint.py — self-verifying state checkpoint.

Covers SHA-256 payload binding, tamper detection via verify(), the redacted
__str__ (payload bytes never leak), and create() argument validation.
"""
from __future__ import annotations

import hashlib
from uuid import UUID

import pytest

from circle_ai.security import SecurityCheckpoint


def test_create_computes_sha256_hash():
    payload = b"serialized-state"
    cp = SecurityCheckpoint.create("uhid-1", "CircleAI.Companion", payload)
    assert cp.payload == payload
    assert cp.payload_hash == hashlib.sha256(payload).digest()
    assert isinstance(cp.id, UUID)
    assert cp.id.version == 4
    assert cp.uhid_identity_id == "uhid-1"
    assert cp.module_label == "CircleAI.Companion"


def test_verify_passes_for_untampered_payload():
    cp = SecurityCheckpoint.create("uhid-1", "CircleAI.Memory", b"abc")
    assert cp.verify() is True


def test_verify_fails_when_payload_mutated():
    cp = SecurityCheckpoint.create("uhid-1", "CircleAI.Memory", b"abc")
    tampered = SecurityCheckpoint(
        id=cp.id,
        uhid_identity_id=cp.uhid_identity_id,
        module_label=cp.module_label,
        payload=b"xyz",  # different bytes, same (now-stale) hash
        payload_hash=cp.payload_hash,
        created_at=cp.created_at,
    )
    assert tampered.verify() is False


def test_empty_payload_verifies():
    cp = SecurityCheckpoint.create("uhid-1", "CircleAI.Memory", b"")
    assert cp.verify() is True


@pytest.mark.parametrize("bad_uhid", ["", "   "])
def test_blank_uhid_rejected(bad_uhid):
    with pytest.raises(ValueError):
        SecurityCheckpoint.create(bad_uhid, "CircleAI.Memory", b"x")


@pytest.mark.parametrize("bad_label", ["", "   "])
def test_blank_module_label_rejected(bad_label):
    with pytest.raises(ValueError):
        SecurityCheckpoint.create("uhid-1", bad_label, b"x")


def test_str_never_leaks_payload_and_shows_hash_prefix():
    payload = b"super-secret-token-value"
    cp = SecurityCheckpoint.create("uhid-1", "CircleAI.Memory", payload)
    text = str(cp)
    assert "super-secret-token-value" not in text
    assert f"PayloadBytes={len(payload)}" in text
    # First 8 bytes of the hash, upper-hex.
    assert cp.payload_hash[:8].hex().upper() in text
    assert "CircleAI.Memory" in text
    assert "uhid-1" in text


def test_frozen_is_immutable():
    cp = SecurityCheckpoint.create("uhid-1", "CircleAI.Memory", b"x")
    with pytest.raises(Exception):
        cp.payload = b"y"  # type: ignore[misc]
