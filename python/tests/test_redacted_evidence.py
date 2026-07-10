"""test_redacted_evidence.py — RedactedEvidenceJsonConverter + AnomalySignal redaction.

Every evidence value must serialise as "sha256:" + lowercase-hex(SHA256(utf8)),
labels preserved, raw values never emitted. Empty/None -> "sha256:". Read side
returns an empty dict (None -> None).
"""
from __future__ import annotations

import hashlib

from circle_ai.security import (
    AnomalySignal,
    RedactedEvidenceJsonConverter,
    ThreatVector,
)


def _expected(raw: str) -> str:
    if not raw:
        return "sha256:"
    return "sha256:" + hashlib.sha256(raw.encode("utf-8")).hexdigest()


def test_write_redacts_every_value():
    conv = RedactedEvidenceJsonConverter()
    out = conv.write({"token": "abc123", "ip": "10.0.0.1"})
    assert out == {"token": _expected("abc123"), "ip": _expected("10.0.0.1")}
    # Hex is lowercase.
    assert out["token"] == out["token"].lower()


def test_write_preserves_keys_only():
    conv = RedactedEvidenceJsonConverter()
    out = conv.write({"label-one": "secret", "label-two": "other"})
    assert set(out.keys()) == {"label-one", "label-two"}
    assert "secret" not in out.values()
    assert "other" not in out.values()


def test_empty_value_maps_to_bare_tag():
    conv = RedactedEvidenceJsonConverter()
    assert conv.write({"k": ""}) == {"k": "sha256:"}


def test_write_none_returns_none():
    assert RedactedEvidenceJsonConverter().write(None) is None


def test_read_none_returns_none_else_empty_dict():
    conv = RedactedEvidenceJsonConverter()
    assert conv.read(None) is None
    assert conv.read({"anything": "here"}) == {}


def test_anomaly_signal_to_redacted_dict():
    sig = AnomalySignal.create(
        vector=ThreatVector.MEMORY_ANOMALY,
        confidence=0.7,
        affected_module="CircleAI.Companion",
        description="leak detected",
        evidence={"session": "tok-xyz"},
    )
    d = sig.to_redacted_dict()
    assert d["vector"] == int(ThreatVector.MEMORY_ANOMALY)
    assert d["affectedModule"] == "CircleAI.Companion"
    assert d["confidence"] == 0.7
    assert d["evidence"] == {"session": _expected("tok-xyz")}
    # Raw secret must never appear anywhere in the serialised form.
    assert "tok-xyz" not in str(d)
