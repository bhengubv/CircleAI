# persona_provider.py
#
# Port of CircleAI.Personality IPersonaProvider.cs + JsonPersonaProvider.cs
# (C# — the EXACT spec).
#
# Storage contract for user-owned Persona documents (distinct from
# CircleAI.Memory.IPersonaStore, which stores the AI's learned PersonaState).
#
#   • IPersonaProvider — get / save (refreshes UpdatedAt) / exists / export-all.
#   • JsonPersonaProvider — file-system provider: each persona -> a JSON document
#     at "{root}/{userId}.persona.json"; atomic write-then-rename; per-userId lock.
#   • InMemoryPersonaProvider — deterministic in-memory provider (no disk).
#
# The C# uses System.Text.Json with default (PascalCase) property names,
# JsonStringEnumConverter, and WhenWritingNull. The JSON layout is reproduced
# faithfully by _to_json / _from_json below so files round-trip PascalCase keys
# with PrivacyLevel serialised as its enum name.

from __future__ import annotations

import json
import os
import threading
import uuid
from dataclasses import replace
from datetime import datetime, timezone
from typing import AsyncIterator, Dict, Optional

from .persona import FormalityRange, Persona, PrivacyLevel


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


def _iso(dt: datetime) -> str:
    # C# DateTimeOffset serialises round-trippable ISO-8601.
    return dt.isoformat()


def _parse_dt(raw: str) -> datetime:
    return datetime.fromisoformat(raw)


def persona_to_json(p: Persona) -> str:
    """Serialise a :class:`Persona` to the JsonPersonaProvider file layout
    (PascalCase keys, enum-name privacy, nulls omitted)."""
    d: Dict[str, object] = {
        "Id": str(p.id),
        "DisplayName": p.display_name,
    }
    if p.pronouns is not None:
        d["Pronouns"] = p.pronouns
    d["IdentityTags"] = list(p.identity_tags)
    d["Values"] = list(p.values)
    d["Taboos"] = list(p.taboos)
    d["PreferredLocale"] = p.preferred_locale
    if p.voice_preference is not None:
        d["VoicePreference"] = p.voice_preference
    d["Formality"] = {"Floor": p.formality.floor, "Ceiling": p.formality.ceiling}
    d["Privacy"] = PrivacyLevel(p.privacy).name.capitalize()
    d["CreatedAt"] = _iso(p.created_at)
    d["UpdatedAt"] = _iso(p.updated_at)
    return json.dumps(d, indent=2, ensure_ascii=False)


def persona_from_json(text: str) -> Persona:
    """Inverse of :func:`persona_to_json`."""
    d = json.loads(text)
    formality = d.get("Formality") or {}
    privacy_name = (d.get("Privacy") or "Balanced").upper()
    return Persona(
        id=uuid.UUID(d["Id"]),
        display_name=d.get("DisplayName", ""),
        pronouns=d.get("Pronouns"),
        identity_tags=list(d.get("IdentityTags") or []),
        values=list(d.get("Values") or []),
        taboos=list(d.get("Taboos") or []),
        preferred_locale=d.get("PreferredLocale", ""),
        voice_preference=d.get("VoicePreference"),
        formality=FormalityRange(formality.get("Floor", "casual"), formality.get("Ceiling", "formal")),
        privacy=PrivacyLevel[privacy_name],
        created_at=_parse_dt(d["CreatedAt"]) if "CreatedAt" in d else _utc_now(),
        updated_at=_parse_dt(d["UpdatedAt"]) if "UpdatedAt" in d else _utc_now(),
    )


class IPersonaProvider:
    """Persists and retrieves user-declared :class:`Persona` documents. Mirrors
    ``CircleAI.Personality.IPersonaProvider``."""

    async def get_async(
        self, user_id: str, ct: Optional[object] = None
    ) -> Optional[Persona]:
        raise NotImplementedError  # pragma: no cover - interface marker

    async def save_async(
        self, user_id: str, persona: Persona, ct: Optional[object] = None
    ) -> Persona:
        raise NotImplementedError  # pragma: no cover - interface marker

    async def exists_async(
        self, user_id: str, ct: Optional[object] = None
    ) -> bool:
        raise NotImplementedError  # pragma: no cover - interface marker

    def export_all_async(
        self, ct: Optional[object] = None
    ) -> AsyncIterator[Persona]:
        raise NotImplementedError  # pragma: no cover - interface marker


class JsonPersonaProvider(IPersonaProvider):
    """File-system :class:`IPersonaProvider`. Mirrors
    ``CircleAI.Personality.JsonPersonaProvider``."""

    def __init__(self, root_directory: str) -> None:
        if root_directory is None or root_directory.strip() == "":
            raise ValueError("rootDirectory required")
        self._root = root_directory
        self._locks: Dict[str, threading.Lock] = {}
        self._locks_guard = threading.Lock()
        os.makedirs(self._root, exist_ok=True)

    def _lock_for(self, user_id: str) -> threading.Lock:
        with self._locks_guard:
            lk = self._locks.get(user_id)
            if lk is None:
                lk = threading.Lock()
                self._locks[user_id] = lk
            return lk

    def _persona_path(self, user_id: str) -> str:
        safe = "_".join(_split_invalid(user_id))
        if safe.strip() == "":
            safe = "default"
        return os.path.join(self._root, safe + ".persona.json")

    async def get_async(
        self, user_id: str, ct: Optional[object] = None
    ) -> Optional[Persona]:
        if user_id is None or user_id.strip() == "":
            raise ValueError("userId required")
        path = self._persona_path(user_id)
        if not os.path.isfile(path):
            return None
        with self._lock_for(user_id):
            with open(path, "r", encoding="utf-8") as fh:
                return persona_from_json(fh.read())

    async def save_async(
        self, user_id: str, persona: Persona, ct: Optional[object] = None
    ) -> Persona:
        if user_id is None or user_id.strip() == "":
            raise ValueError("userId required")
        if persona is None:
            raise ValueError("persona")
        refreshed = replace(persona, updated_at=_utc_now())
        target = self._persona_path(user_id)
        tmp = target + "." + uuid.uuid4().hex + ".tmp"
        with self._lock_for(user_id):
            try:
                with open(tmp, "w", encoding="utf-8") as fh:
                    fh.write(persona_to_json(refreshed))
                os.replace(tmp, target)  # atomic move-with-overwrite
                return refreshed
            except Exception:
                try:
                    if os.path.exists(tmp):
                        os.remove(tmp)
                except OSError:
                    pass
                raise

    async def exists_async(
        self, user_id: str, ct: Optional[object] = None
    ) -> bool:
        if user_id is None or user_id.strip() == "":
            raise ValueError("userId required")
        return os.path.isfile(self._persona_path(user_id))

    async def export_all_async(
        self, ct: Optional[object] = None
    ) -> AsyncIterator[Persona]:
        if not os.path.isdir(self._root):
            return
        for fn in sorted(os.listdir(self._root)):
            if not fn.endswith(".persona.json"):
                continue
            path = os.path.join(self._root, fn)
            try:
                with open(path, "r", encoding="utf-8") as fh:
                    persona = persona_from_json(fh.read())
            except Exception:
                # Skip corrupted records during export rather than failing.
                continue
            yield persona


class InMemoryPersonaProvider(IPersonaProvider):
    """Deterministic in-memory :class:`IPersonaProvider` (no disk). Follows the
    same contract as :class:`JsonPersonaProvider` — ``save`` refreshes
    ``updated_at`` and returns the stored record."""

    def __init__(self) -> None:
        self._by_user: Dict[str, Persona] = {}
        self._lock = threading.Lock()

    async def get_async(
        self, user_id: str, ct: Optional[object] = None
    ) -> Optional[Persona]:
        if user_id is None or user_id.strip() == "":
            raise ValueError("userId required")
        with self._lock:
            return self._by_user.get(user_id)

    async def save_async(
        self, user_id: str, persona: Persona, ct: Optional[object] = None
    ) -> Persona:
        if user_id is None or user_id.strip() == "":
            raise ValueError("userId required")
        if persona is None:
            raise ValueError("persona")
        refreshed = replace(persona, updated_at=_utc_now())
        with self._lock:
            self._by_user[user_id] = refreshed
        return refreshed

    async def exists_async(
        self, user_id: str, ct: Optional[object] = None
    ) -> bool:
        if user_id is None or user_id.strip() == "":
            raise ValueError("userId required")
        with self._lock:
            return user_id in self._by_user

    async def export_all_async(
        self, ct: Optional[object] = None
    ) -> AsyncIterator[Persona]:
        with self._lock:
            snapshot = list(self._by_user.values())
        for p in snapshot:
            yield p


# Characters Windows/POSIX forbid in a filename (Path.GetInvalidFileNameChars
# superset); used to sanitise the userId into a safe file stem.
_INVALID_FILENAME_CHARS = set('<>:"/\\|?*') | {chr(c) for c in range(0, 32)}


def _split_invalid(user_id: str):
    # Split on any invalid filename char (C# userId.Split(GetInvalidFileNameChars())).
    parts = []
    cur = []
    for ch in user_id:
        if ch in _INVALID_FILENAME_CHARS:
            parts.append("".join(cur))
            cur = []
        else:
            cur.append(ch)
    parts.append("".join(cur))
    return parts
