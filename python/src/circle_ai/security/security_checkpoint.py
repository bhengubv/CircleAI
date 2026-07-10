# security_checkpoint.py
#
# Port of CircleAI.Security.SecurityCheckpoint (C# — the EXACT spec).
#
# A cryptographically-bound snapshot of trusted local state.
#
# When CircleAI detects an anomaly, the watchdog may roll back to the last
# verified checkpoint. A checkpoint is:
#   - IMMUTABLE once created (frozen record)
#   - SELF-VERIFYING (SHA-256 of Payload, verified on restore)
#   - TAGGED with the UHID that created it (identity binding)
#
# The payload is deliberately opaque (bytes) so any module can checkpoint its
# own serialised state without this package taking a dependency on it.

from __future__ import annotations

import hashlib
import hmac
from dataclasses import dataclass
from datetime import datetime, timezone
from uuid import UUID, uuid4


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


@dataclass(frozen=True, slots=True)
class SecurityCheckpoint:
    """An immutable, self-verifying snapshot of trusted local state.

    Created before a risky operation; used for rollback if an
    :class:`AnomalySignal` is confirmed.

    :param id: Unique checkpoint identifier.
    :param uhid_identity_id: The UHID of the local user whose state is captured.
        Binds the checkpoint to a specific identity.
    :param module_label: Label for the module or subsystem that created this
        checkpoint (e.g. ``"CircleAI.Companion"``, ``"CircleAI.Memory"``).
    :param payload: Opaque serialised state payload.
    :param payload_hash: SHA-256 hash of ``payload``, computed at creation time.
        Verified by :meth:`verify` before restoring.
    :param created_at: UTC timestamp of checkpoint creation.
    """

    id: UUID
    uhid_identity_id: str
    module_label: str
    payload: bytes
    payload_hash: bytes
    created_at: datetime

    @classmethod
    def create(
        cls, uhid_identity_id: str, module_label: str, payload: bytes
    ) -> "SecurityCheckpoint":
        """Create a new checkpoint, computing :attr:`payload_hash` automatically.

        Mirrors ``ArgumentException.ThrowIfNullOrWhiteSpace`` /
        ``ArgumentNullException.ThrowIfNull`` on the C# side.
        """
        if uhid_identity_id is None or uhid_identity_id.strip() == "":
            raise ValueError("uhid_identity_id must be non-empty")
        if module_label is None or module_label.strip() == "":
            raise ValueError("module_label must be non-empty")
        if payload is None:
            raise ValueError("payload must not be None")

        digest = hashlib.sha256(payload).digest()
        return cls(
            id=uuid4(),
            uhid_identity_id=uhid_identity_id,
            module_label=module_label,
            payload=payload,
            payload_hash=digest,
            created_at=_utc_now(),
        )

    def verify(self) -> bool:
        """Verify that :attr:`payload` has not been tampered with since the
        checkpoint was created.

        Returns ``True`` if the current SHA-256 of :attr:`payload` matches
        :attr:`payload_hash`; ``False`` if the payload was modified. Uses a
        constant-time comparison (mirrors ``CryptographicOperations``
        ``.FixedTimeEquals``).
        """
        current = hashlib.sha256(self.payload).digest()
        return hmac.compare_digest(current, self.payload_hash)

    def __str__(self) -> str:
        """Non-sensitive textual representation — the payload bytes are NEVER
        included in clear. Only the first 16 hex chars of :attr:`payload_hash`
        are emitted, sufficient for correlation across logs without leaking
        content.
        """
        if self.payload_hash is not None and len(self.payload_hash) >= 8:
            hash_prefix = self.payload_hash[:8].hex().upper()
        else:
            hash_prefix = "(empty)"
        payload_bytes = len(self.payload) if self.payload is not None else 0
        return (
            f"SecurityCheckpoint(Id={self.id}, Module={self.module_label}, "
            f"Uhid={self.uhid_identity_id}, PayloadSha256={hash_prefix}…, "
            f"PayloadBytes={payload_bytes}, CreatedAt={self.created_at.isoformat()})"
        )
