"""test_federation_aggregator.py — CircleAI.Federation port.

Covers the FederatedAveraging float math (encode/decode round-trip, sample-size
weighted average, guards), and the InMemoryFederationAggregator round lifecycle
(open -> submit -> commit gating on min participants, signature-validator
filtering, empty-payload tolerance, idempotent re-commit, unknown-round errors).
C# is the exact spec.
"""
from __future__ import annotations

import struct
from datetime import datetime, timezone
from uuid import uuid4

import pytest

from circle_ai.federation import (
    FederatedAveraging,
    FederationRound,
    IFederationAggregator,
    InMemoryFederationAggregator,
    ModelDelta,
    RoundStatus,
)

_NOW = datetime(2026, 1, 1, tzinfo=timezone.utc)


def _delta(round_id, payload: bytes, samples: int, sig: bytes = b"\x01") -> ModelDelta:
    return ModelDelta(
        id=uuid4(),
        round_id=round_id,
        contributor_uhid="uhid",
        model_id="m",
        from_version="1.0.0",
        delta_payload=payload,
        sample_count=samples,
        signature=sig,
        submitted_at=_NOW,
    )


def test_encode_decode_roundtrip():
    floats = [0.0, 1.5, -2.25, 100.0]
    encoded = FederatedAveraging.encode_floats(floats)
    assert encoded == struct.pack("<4f", *floats)
    assert FederatedAveraging.decode_floats(encoded) == floats


def test_weighted_average_matches_manual():
    # Two deltas: [2.0, 4.0] w=1 sample, [4.0, 8.0] w=3 samples.
    a = FederatedAveraging.encode_floats([2.0, 4.0])
    b = FederatedAveraging.encode_floats([4.0, 8.0])
    out = FederatedAveraging.average([_delta(uuid4(), a, 1), _delta(uuid4(), b, 3)])
    result = FederatedAveraging.decode_floats(out)
    # (2*1 + 4*3)/4 = 3.5 ; (4*1 + 8*3)/4 = 7.0
    assert result == pytest.approx([3.5, 7.0])


def test_average_guards():
    with pytest.raises(ValueError):
        FederatedAveraging.average([])
    with pytest.raises(ValueError):
        FederatedAveraging.average([_delta(uuid4(), b"", 1)])  # empty payload
    with pytest.raises(ValueError):
        FederatedAveraging.average([_delta(uuid4(), b"\x00\x00\x00", 1)])  # not mult of 4
    a = FederatedAveraging.encode_floats([1.0])
    b = FederatedAveraging.encode_floats([1.0, 2.0])
    with pytest.raises(ValueError):
        FederatedAveraging.average([_delta(uuid4(), a, 1), _delta(uuid4(), b, 1)])  # length mismatch
    with pytest.raises(ValueError):
        FederatedAveraging.average([_delta(uuid4(), a, 0)])  # zero total samples


def test_decode_guard():
    with pytest.raises(ValueError):
        FederatedAveraging.decode_floats(b"\x00\x00\x00")


async def test_open_round_validation():
    agg = InMemoryFederationAggregator(lambda _: True)
    assert isinstance(agg, IFederationAggregator)
    with pytest.raises(ValueError):
        await agg.open_round_async("", "1", "2", 1, 1)
    with pytest.raises(ValueError):
        await agg.open_round_async("m", "1", "2", 0, 1)
    with pytest.raises(ValueError):
        await agg.open_round_async("m", "1", "2", 3, 2)  # max < min


async def test_commit_gates_on_min_participants():
    agg = InMemoryFederationAggregator(lambda _: True)
    rnd = await agg.open_round_async("m", "1.0.0", "1.1.0", 2, 5)
    assert isinstance(rnd, FederationRound) and rnd.status == RoundStatus.Open

    payload = FederatedAveraging.encode_floats([1.0, 1.0])
    await agg.submit_delta_async(_delta(rnd.id, payload, 10))
    assert await agg.try_commit_async(rnd.id) is None  # only 1 < min 2

    await agg.submit_delta_async(_delta(rnd.id, payload, 30))
    committed = await agg.try_commit_async(rnd.id)
    assert committed is not None
    snap = await agg.get_round_async(rnd.id)
    assert snap.status == RoundStatus.Committed and snap.current_participant_count == 2

    # Idempotent re-commit returns same bytes.
    assert await agg.try_commit_async(rnd.id) == committed


async def test_signature_validator_filters_deltas():
    # Reject any delta whose signature is b"\x00".
    agg = InMemoryFederationAggregator(lambda d: d.signature != b"\x00")
    rnd = await agg.open_round_async("m", "1", "2", 2, 5)
    payload = FederatedAveraging.encode_floats([1.0])
    await agg.submit_delta_async(_delta(rnd.id, payload, 1, sig=b"\x00"))  # invalid
    await agg.submit_delta_async(_delta(rnd.id, payload, 1, sig=b"\x01"))  # valid
    assert await agg.try_commit_async(rnd.id) is None  # only 1 valid < min 2


async def test_empty_payload_tolerated_not_counted():
    agg = InMemoryFederationAggregator(lambda _: True)
    rnd = await agg.open_round_async("m", "1", "2", 1, 5)
    await agg.submit_delta_async(_delta(rnd.id, b"", 5))  # empty -> ignored
    snap = await agg.get_round_async(rnd.id)
    assert snap.current_participant_count == 0
    assert await agg.try_commit_async(rnd.id) is None  # nothing valid


async def test_unknown_round_errors():
    agg = InMemoryFederationAggregator(lambda _: True)
    ghost = uuid4()
    with pytest.raises(KeyError):
        await agg.submit_delta_async(_delta(ghost, b"\x00\x00\x00\x00", 1))
    with pytest.raises(KeyError):
        await agg.try_commit_async(ghost)
    with pytest.raises(KeyError):
        await agg.get_round_async(ghost)


async def test_max_participants_enforced():
    agg = InMemoryFederationAggregator(lambda _: True)
    rnd = await agg.open_round_async("m", "1", "2", 1, 1)
    payload = FederatedAveraging.encode_floats([1.0])
    await agg.submit_delta_async(_delta(rnd.id, payload, 1))
    with pytest.raises(RuntimeError):
        await agg.submit_delta_async(_delta(rnd.id, payload, 1))  # exceeds max 1


def test_none_validator_raises():
    with pytest.raises(ValueError):
        InMemoryFederationAggregator(None)  # type: ignore[arg-type]
