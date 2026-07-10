# uhid_key_ring.py
#
# Port of CircleAI.Security.UhidKeyRing (C# — the EXACT spec).
#
# Ephemeral session key management bound to a UHID identity.
#
# Each UHID session gets a fresh P-256 (NIST) key pair for ECDSA signing.
# When an anomaly is confirmed the watchdog calls generate_fresh() — the old
# key is revoked and a new key ring is issued. All in-flight requests signed
# with the revoked key are rejected.
#
# Uses `cryptography` (installed) for genuine ECDSA P-256 parity with the C#
# `ECDsa.Create(ECCurve.NamedCurves.nistP256)`:
#   - public_key_der  == ExportSubjectPublicKeyInfo() (DER SubjectPublicKeyInfo)
#   - sign            == SignData(data, HashAlgorithmName.SHA256)  (ASN.1/DER sig)
#   - verify          == VerifyData(data, sig, SHA256)
# P-256 is selected over Ed25519 for BCL compatibility, matching the C# note.

from __future__ import annotations

import threading
from datetime import datetime, timezone
from typing import Optional
from uuid import UUID, uuid4

from cryptography.exceptions import InvalidSignature
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import ec


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


class UhidKeyRing:
    """Ephemeral ECDSA (P-256) session key ring bound to a UHID identity.

    Generate a fresh ring at session start or on anomaly confirmation. Once
    revoked, the ring cannot sign; generate a new one.

    Disposable — call :meth:`dispose` (or use as a context manager) to drop the
    private key. Mirrors the C# ``IDisposable`` contract.
    """

    def __init__(self, uhid_identity_id: str) -> None:
        # Private constructor semantics: prefer generate_fresh().
        if uhid_identity_id is None or uhid_identity_id.strip() == "":
            raise ValueError("uhid_identity_id must be non-empty")
        self._uhid_identity_id = uhid_identity_id
        self._key: Optional[ec.EllipticCurvePrivateKey] = None
        self._revoked = False
        self._lock = threading.RLock()
        self._ring_id: UUID = uuid4()
        self._generated_at: datetime = _utc_now()
        self._revoked_at: Optional[datetime] = None
        self._public_key_der: bytes = b""
        self._regenerate_key()

    # ── Factory ──────────────────────────────────────────────────────────────

    @staticmethod
    def generate_fresh(uhid_identity_id: str) -> "UhidKeyRing":
        """Create a new :class:`UhidKeyRing` for ``uhid_identity_id`` with a
        freshly generated P-256 key pair.
        """
        return UhidKeyRing(uhid_identity_id)

    # ── Properties ───────────────────────────────────────────────────────────

    @property
    def ring_id(self) -> UUID:
        """Unique ring identifier. Changes on every :meth:`generate_fresh` /
        regeneration.
        """
        return self._ring_id

    @property
    def uhid_identity_id(self) -> str:
        """The UHID identity this ring is bound to."""
        return self._uhid_identity_id

    @property
    def generated_at(self) -> datetime:
        """UTC timestamp when this ring was generated."""
        return self._generated_at

    @property
    def revoked_at(self) -> Optional[datetime]:
        """UTC timestamp when this ring was revoked, or ``None`` if still active."""
        return self._revoked_at

    @property
    def is_revoked(self) -> bool:
        """``True`` if this ring has been explicitly revoked."""
        return self._revoked

    @property
    def public_key_der(self) -> bytes:
        """The DER-encoded (SubjectPublicKeyInfo) public key for this ring.

        Safe to share; corresponds to the private signing key.
        """
        return self._public_key_der

    # ── Operations ───────────────────────────────────────────────────────────

    def rotate(self) -> "UhidKeyRing":
        """Rotate the ring: revoke the current key and generate a replacement.

        Returns a NEW :class:`UhidKeyRing` — this instance remains revoked.
        Prefer this over mutating in place so call sites holding a reference to
        the old ring cannot accidentally sign with a rotated key.
        """
        self.revoke()
        return UhidKeyRing.generate_fresh(self._uhid_identity_id)

    def sign(self, data: bytes) -> bytes:
        """Sign ``data`` with the current private key using ECDSA-SHA256.

        Raises ``RuntimeError`` if revoked or disposed.
        """
        if data is None:
            raise ValueError("data must not be None")
        with self._lock:
            if self._key is None:
                raise RuntimeError("UhidKeyRing has been disposed")
            if self._revoked:
                raise RuntimeError(
                    f"UhidKeyRing {self._ring_id} has been revoked — "
                    f"call rotate() to get a fresh ring."
                )
            return self._key.sign(data, ec.ECDSA(hashes.SHA256()))

    def verify(self, data: bytes, signature: bytes) -> bool:
        """Verify an ECDSA-SHA256 ``signature`` against ``data`` using this
        ring's public key.

        Works even after revocation (so prior signatures can still be
        validated).
        """
        if data is None:
            raise ValueError("data must not be None")
        if signature is None:
            raise ValueError("signature must not be None")
        with self._lock:
            if self._key is None:
                return False
            try:
                self._key.public_key().verify(
                    signature, data, ec.ECDSA(hashes.SHA256())
                )
                return True
            except InvalidSignature:
                return False

    def revoke(self) -> None:
        """Revoke this ring. After revocation :meth:`sign` raises;
        :meth:`verify` continues to work for historical validation.
        """
        with self._lock:
            if self._revoked:
                return
            self._revoked = True
            self._revoked_at = _utc_now()

    # ── Private helpers ──────────────────────────────────────────────────────

    def _regenerate_key(self) -> None:
        with self._lock:
            self._key = ec.generate_private_key(ec.SECP256R1())
            self._ring_id = uuid4()
            self._generated_at = _utc_now()
            self._revoked_at = None
            self._revoked = False
            self._public_key_der = self._key.public_key().public_bytes(
                encoding=serialization.Encoding.DER,
                format=serialization.PublicFormat.SubjectPublicKeyInfo,
            )

    def dispose(self) -> None:
        """Drop the private key. Mirrors C# ``Dispose``."""
        with self._lock:
            self._key = None

    def __enter__(self) -> "UhidKeyRing":
        return self

    def __exit__(self, *exc_info: object) -> None:
        self.dispose()
