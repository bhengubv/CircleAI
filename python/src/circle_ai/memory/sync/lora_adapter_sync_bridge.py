# lora_adapter_sync_bridge.py
#
# (Phase D4) Bridges trained LoRA adapter bytes across the user's
# devices through the existing CompanionStateSyncEngine. Adapter bytes
# are base64-encoded into the SyncableEntry payload; receiving devices
# decode and persist to disk for the LoRAAdapterManager to apply.
#
# Ported faithfully from CircleAI.Memory.Sync.LoraAdapterSyncBridge (C# — the
# spec).

from __future__ import annotations

import base64
import json
import os
from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Optional

from .companion_state_sync_engine import ICompanionStateSyncEngine
from .syncable_entry import SyncableEntry


def _parse_dt(value: object) -> datetime:
    if isinstance(value, str) and value:
        dt = datetime.fromisoformat(value)
        if dt.tzinfo is None:
            dt = dt.replace(tzinfo=timezone.utc)
        return dt
    return datetime.now(timezone.utc)


@dataclass(frozen=True, slots=True)
class LoraAdapterSnapshot:
    """(Phase D4) Payload of a synced LoRA adapter snapshot.

    :param adapter_id: Stable id (typically "personal-{userId}").
    :param base64_bytes: Adapter file contents, base64-encoded.
    :param trained_at_utc: When training that produced these bytes finished.
    :param step_count: Total training steps so far (monotonic).
    """

    adapter_id: str
    base64_bytes: str
    trained_at_utc: datetime
    step_count: int


class LoraAdapterSyncBridge:
    #: EntityType used on the wire.
    ENTITY_TYPE = "LoraAdapter"

    def __init__(self, engine: ICompanionStateSyncEngine) -> None:
        if engine is None:
            raise ValueError("engine required")
        self._engine = engine

    async def publish_async(
        self,
        adapter_id: str,
        adapter_path: str,
        step_count: int,
        *,
        ct: Optional[object] = None,
    ) -> None:
        """Publish a trained adapter to peer devices."""
        if adapter_id is None or adapter_id.strip() == "":
            raise ValueError("adapter_id required")
        if adapter_path is None or adapter_path.strip() == "":
            raise ValueError("adapter_path required")
        if not os.path.isfile(adapter_path):
            raise FileNotFoundError(f"adapter file not found: {adapter_path}")
        with open(adapter_path, "rb") as f:
            data = f.read()
        snapshot = LoraAdapterSnapshot(
            adapter_id=adapter_id,
            base64_bytes=base64.b64encode(data).decode("ascii"),
            trained_at_utc=datetime.now(timezone.utc),
            step_count=step_count,
        )
        payload = self._serialize(snapshot)
        await self._engine.write_local_async(
            self.ENTITY_TYPE, adapter_id, payload, is_tombstone=False, ct=ct
        )

    @classmethod
    async def try_write_async(
        cls,
        entry: SyncableEntry,
        destination_path: str,
        *,
        ct: Optional[object] = None,
    ) -> Optional[LoraAdapterSnapshot]:
        """Decode an inbound SyncableEntry, write the adapter to
        ``destination_path``. Returns the decoded snapshot for caller-side
        bookkeeping (e.g. trigger Apply).
        """
        if entry is None:
            raise ValueError("entry required")
        if entry.is_tombstone:
            return None
        if entry.entity_type != cls.ENTITY_TYPE:
            return None
        try:
            snapshot = cls._deserialize(entry.payload)
        except (ValueError, json.JSONDecodeError):
            # inbound payload decode failed
            return None
        if snapshot is None:
            return None
        if not snapshot.base64_bytes:
            return snapshot
        try:
            directory = os.path.dirname(destination_path)
            if directory:
                os.makedirs(directory, exist_ok=True)
            data = base64.b64decode(snapshot.base64_bytes)
            with open(destination_path, "wb") as f:
                f.write(data)
        except Exception:
            # write failed — non-fatal, snapshot still returned
            pass
        return snapshot

    # ── serialisation ────────────────────────────────────────────────────

    @staticmethod
    def _serialize(snapshot: LoraAdapterSnapshot) -> str:
        obj = {
            "adapterId": snapshot.adapter_id,
            "base64Bytes": snapshot.base64_bytes,
            "trainedAtUtc": snapshot.trained_at_utc.isoformat(),
            "stepCount": snapshot.step_count,
        }
        return json.dumps(obj, separators=(",", ":"))

    @staticmethod
    def _deserialize(payload: str) -> LoraAdapterSnapshot:
        d = json.loads(payload)
        return LoraAdapterSnapshot(
            adapter_id=d.get("adapterId", ""),
            base64_bytes=d.get("base64Bytes") or "",
            trained_at_utc=_parse_dt(d.get("trainedAtUtc")),
            step_count=d.get("stepCount", 0),
        )
