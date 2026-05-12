# test_identity.py
#
# Validates CircleIdentity and RegisteredDevice deserialization,
# tier values, device counts, and platform strings from fixtures/identity.json.

from __future__ import annotations

import json
import pathlib
import sys
from datetime import datetime, timezone

import pytest

sys.path.insert(0, str(pathlib.Path(__file__).parent.parent / "src"))

from circle_ai.identity import CircleIdentity, RegisteredDevice, IdentityTier


FIXTURES_DIR = pathlib.Path(__file__).parent.parent.parent / "fixtures"


def _load_fixture() -> dict:
    with open(FIXTURES_DIR / "identity.json", encoding="utf-8") as f:
        return json.load(f)


FIXTURE = _load_fixture()
EXAMPLES = FIXTURE["examples"]


# ---------------------------------------------------------------------------
# Helper: parse ISO 8601 UTC string
# ---------------------------------------------------------------------------

def _parse_dt(s: str) -> datetime:
    """Parse an ISO 8601 UTC timestamp (Z suffix) to an aware datetime."""
    return datetime.fromisoformat(s.replace("Z", "+00:00"))


# ---------------------------------------------------------------------------
# Helper: build CircleIdentity from fixture dict
# ---------------------------------------------------------------------------

def _build_identity(raw: dict) -> CircleIdentity:
    return CircleIdentity(
        identity_id=raw["identityId"],
        display_name=raw["displayName"],
        preferred_language=raw.get("preferredLanguage"),
        tier=IdentityTier(raw["tier"]),
        device_ids=raw["deviceIds"],
        created_at=_parse_dt(raw["createdAt"]),
        last_seen_at=_parse_dt(raw["lastSeenAt"]),
    )


def _build_device(raw: dict) -> RegisteredDevice:
    return RegisteredDevice(
        device_id=raw["deviceId"],
        identity_id=raw["identityId"],
        platform=raw["platform"],
        device_name=raw.get("deviceName"),
        registered_at=_parse_dt(raw["registeredAt"]),
        last_active_at=_parse_dt(raw["lastActiveAt"]),
    )


# ---------------------------------------------------------------------------
# IdentityTier enum values
# ---------------------------------------------------------------------------

def test_identity_tier_values() -> None:
    assert IdentityTier("Anonymous")    is IdentityTier.Anonymous
    assert IdentityTier("Pseudonymous") is IdentityTier.Pseudonymous
    assert IdentityTier("Verified")     is IdentityTier.Verified


def test_identity_tier_order() -> None:
    """Fixture declares tier order as Anonymous < Pseudonymous < Verified."""
    order = [IdentityTier(t) for t in FIXTURE["assertions"]["tierOrder"]]
    assert order == [IdentityTier.Anonymous, IdentityTier.Pseudonymous, IdentityTier.Verified]


# ---------------------------------------------------------------------------
# Per-example checks (parametrised)
# ---------------------------------------------------------------------------

@pytest.mark.parametrize("example", EXAMPLES, ids=[e["id"] for e in EXAMPLES])
def test_identity_schema(example: dict) -> None:
    identity = _build_identity(example["identity"])
    devices  = [_build_device(d) for d in example["devices"]]

    raw_id = example["identity"]

    # tier
    assert identity.tier == IdentityTier(raw_id["tier"])

    # device count
    assert len(identity.device_ids) == len(example["devices"])

    # device ids match
    fixture_device_ids = set(raw_id["deviceIds"])
    assert set(identity.device_ids) == fixture_device_ids

    # platforms match expected platforms list
    valid_platforms = set(FIXTURE["platforms"])
    for device in devices:
        assert device.platform in valid_platforms, \
            f"Unknown platform {device.platform!r} in example {example['id']!r}"

    # each device points back to the correct identity
    for device in devices:
        assert device.identity_id == identity.identity_id


# ---------------------------------------------------------------------------
# Specific example assertions
# ---------------------------------------------------------------------------

def test_verified_multi_device() -> None:
    example = next(e for e in EXAMPLES if e["id"] == "verified_multi_device")
    identity = _build_identity(example["identity"])

    assert identity.tier == IdentityTier.Verified
    assert len(identity.device_ids) == 3
    assert identity.preferred_language == "zu"
    assert identity.display_name == "Sipho Dlamini"


def test_pseudonymous_single_device() -> None:
    example = next(e for e in EXAMPLES if e["id"] == "pseudonymous_single_device")
    identity = _build_identity(example["identity"])

    assert identity.tier == IdentityTier.Pseudonymous
    assert len(identity.device_ids) == 1
    assert identity.preferred_language == "en"


def test_anonymous_iot() -> None:
    example = next(e for e in EXAMPLES if e["id"] == "anonymous_iot")
    identity = _build_identity(example["identity"])
    devices  = [_build_device(d) for d in example["devices"]]

    assert identity.tier == IdentityTier.Anonymous
    assert identity.preferred_language is None
    assert len(devices) == 1
    assert devices[0].platform == "iot"
    assert devices[0].device_name is None
