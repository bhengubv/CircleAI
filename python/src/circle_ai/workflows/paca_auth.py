# paca_auth.py
#
# Port of CircleAI.Workflows PacaAuth.cs (C# — the EXACT spec).
#
# (3.3.0) Auth primitives ported from paca: JWT (access + refresh) + API-key
# validation. Issuance and verification use HMAC-SHA256. API keys live in an
# in-memory store keyed by hashed prefix.
#
# Self-contained crypto: hmac.new(..., hashlib.sha256) for the JWT signature +
# hashlib.sha256 for API-key hashing, secrets.token_bytes for key material, and
# a constant-time compare (hmac.compare_digest) mirroring the C# FixedTimeEquals
# / SlowEquals byte-diff loops. Base64Url is standard-b64 with +/ -> -_ and
# padding stripped. The token payload is serialised with compact separators so
# the signed bytes are stable; the same class signs and verifies.

from __future__ import annotations

import base64
import hashlib
import hmac
import json
import secrets
import threading
from dataclasses import dataclass, replace
from datetime import datetime, timedelta, timezone
from typing import Callable, Dict, Mapping, Optional, Tuple


@dataclass(frozen=True, slots=True)
class JwtPair:
    """(3.3.0) Token-shaped JWT result."""

    access_token: str
    refresh_token: str
    access_expires_at_utc: datetime
    refresh_expires_at_utc: datetime


@dataclass(frozen=True, slots=True)
class JwtPayload:
    """(3.3.0) Verified JWT payload."""

    subject: str
    claims: Dict[str, str]
    expires_at_utc: datetime


def _b64url_encode(data: bytes) -> str:
    return base64.b64encode(data).decode("ascii").rstrip("=").replace("+", "-").replace("/", "_")


def _b64url_decode(text: str) -> bytes:
    s = text.replace("-", "+").replace("_", "/")
    pad = len(s) % 4
    if pad == 2:
        s += "=="
    elif pad == 3:
        s += "="
    return base64.b64decode(s)


class HmacJwtAuthenticator:
    """(3.3.0) HMAC-SHA256 JWT issuer + verifier."""

    def __init__(
        self,
        signing_secret: str,
        access_lifetime: Optional[timedelta] = None,
        refresh_lifetime: Optional[timedelta] = None,
        clock: Optional[Callable[[], datetime]] = None,
    ) -> None:
        if signing_secret is None or signing_secret.strip() == "" or len(signing_secret) < 16:
            raise ValueError("Signing secret must be at least 16 characters.")
        self._secret = signing_secret.encode("utf-8")
        self._access_lifetime = access_lifetime if access_lifetime is not None else timedelta(minutes=15)
        self._refresh_lifetime = refresh_lifetime if refresh_lifetime is not None else timedelta(days=7)
        self._clock = clock if clock is not None else (lambda: datetime.now(timezone.utc))

    def issue(self, subject: str, claims: Optional[Mapping[str, str]] = None) -> JwtPair:
        """(3.3.0) Issue access + refresh tokens for ``subject``."""
        if subject is None or subject.strip() == "":
            raise ValueError("subject required")
        now = self._clock()
        access_exp = now + self._access_lifetime
        refresh_exp = now + self._refresh_lifetime
        access = self._encode_token(subject, "access", access_exp, claims)
        refresh = self._encode_token(subject, "refresh", refresh_exp, None)
        return JwtPair(access, refresh, access_exp, refresh_exp)

    def verify(self, token: str, expected_type: str = "access") -> Optional[JwtPayload]:
        """(3.3.0) Verify a token; returns the payload or None if invalid/expired."""
        if token is None or token.strip() == "":
            return None
        parts = token.split(".")
        if len(parts) != 3:
            return None

        header, payload, sig = parts[0], parts[1], parts[2]
        signing = f"{header}.{payload}"
        expected = self._sign_b64url(signing)
        if not self._fixed_time_equals(expected, sig):
            return None

        try:
            json_bytes = _b64url_decode(payload)
            data = json.loads(json_bytes.decode("utf-8"))
            if not isinstance(data, dict):
                return None
        except (ValueError, UnicodeDecodeError):
            return None

        if data.get("typ") != expected_type:
            return None
        subject = data.get("sub")
        if not isinstance(subject, str):
            return None
        exp_seconds = data.get("exp")
        if not isinstance(exp_seconds, int) or isinstance(exp_seconds, bool):
            return None
        exp = datetime.fromtimestamp(exp_seconds, tz=timezone.utc)
        if exp <= self._clock():
            return None

        extra_claims: Dict[str, str] = {}
        for k, v in data.items():
            if k in ("typ", "sub", "exp"):
                continue
            extra_claims[k] = v if isinstance(v, str) else _json_scalar_to_str(v)
        return JwtPayload(subject, extra_claims, exp)

    def _encode_token(
        self,
        subject: str,
        type_: str,
        expires: datetime,
        claims: Optional[Mapping[str, str]],
    ) -> str:
        header = '{"alg":"HS256","typ":"JWT"}'
        payload: Dict[str, object] = {
            "sub": subject,
            "typ": type_,
            "exp": int(expires.timestamp()),
        }
        if claims is not None:
            for k, v in claims.items():
                payload[k] = v
        header_b = _b64url_encode(header.encode("utf-8"))
        payload_b = _b64url_encode(json.dumps(payload, separators=(",", ":")).encode("utf-8"))
        signing = f"{header_b}.{payload_b}"
        sig = self._sign_b64url(signing)
        return f"{signing}.{sig}"

    def _sign_b64url(self, signing: str) -> str:
        mac = hmac.new(self._secret, signing.encode("utf-8"), hashlib.sha256).digest()
        return _b64url_encode(mac)

    @staticmethod
    def _fixed_time_equals(a: str, b: str) -> bool:
        return hmac.compare_digest(a.encode("utf-8"), b.encode("utf-8"))


def _json_scalar_to_str(v: object) -> str:
    if isinstance(v, bool):
        return "true" if v else "false"
    if v is None:
        return ""
    if isinstance(v, (int, float, str)):
        return str(v)
    return json.dumps(v, separators=(",", ":"))


@dataclass(frozen=True, slots=True)
class PacaApiKeyRecord:
    """(3.3.0) Issued API key — store hashes only."""

    key_id: str
    label: str
    hashed_secret: str
    created_at_utc: datetime
    revoked_at_utc: Optional[datetime]


class PacaApiKeyAuthenticator:
    """(3.3.0) API-key registry separate from JWT user auth."""

    def __init__(self, clock: Optional[Callable[[], datetime]] = None) -> None:
        self._clock = clock if clock is not None else (lambda: datetime.now(timezone.utc))
        self._keys: Dict[str, PacaApiKeyRecord] = {}
        self._lock = threading.Lock()

    def issue(self, label: str) -> Tuple[PacaApiKeyRecord, str]:
        """(3.3.0) Generate a fresh key; the raw ``secret`` is returned ONCE for
        the caller to store."""
        if label is None or label.strip() == "":
            raise ValueError("label required")
        key_id = secrets.token_hex(16)  # Guid.NewGuid().ToString("n") == 32 hex chars
        secret = base64.b64encode(secrets.token_bytes(32)).decode("ascii").rstrip("=")
        hashed = self._hash(secret)
        record = PacaApiKeyRecord(key_id, label, hashed, self._clock(), None)
        with self._lock:
            self._keys[key_id] = record
        return record, secret

    def verify(self, key_id: str, presented_secret: str) -> Optional[PacaApiKeyRecord]:
        """(3.3.0) Verify an incoming key. Returns the record if valid and live."""
        with self._lock:
            record = self._keys.get(key_id)
        if record is None:
            return None
        if record.revoked_at_utc is not None:
            return None
        hashed = self._hash(presented_secret)
        return record if self._slow_equals(hashed, record.hashed_secret) else None

    def revoke(self, key_id: str) -> None:
        """(3.3.0) Revoke a key. Idempotent."""
        with self._lock:
            existing = self._keys.get(key_id)
            if existing is None or existing.revoked_at_utc is not None:
                return
            self._keys[key_id] = replace(existing, revoked_at_utc=self._clock())

    @staticmethod
    def _hash(secret: str) -> str:
        digest = hashlib.sha256(secret.encode("utf-8")).digest()
        return base64.b64encode(digest).decode("ascii").rstrip("=")

    @staticmethod
    def _slow_equals(a: str, b: str) -> bool:
        return hmac.compare_digest(a.encode("utf-8"), b.encode("utf-8"))
