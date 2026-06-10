"""Core data records shared across CircleAI Python.

Mirrors the C# CircleAI.Inference / CircleAI.Core records so cross-language
fixtures and the planned WASM/IPC bridges see the same shapes.
"""
from __future__ import annotations

import datetime as _dt
from dataclasses import dataclass, field
from enum import IntEnum
from typing import Optional


@dataclass(frozen=True)
class ChatMessage:
    """A single message in a chat history.

    role is one of "system", "user", "assistant", or "tool".

    image_bytes: optional raw image bytes (JPEG / PNG / WebP) attached to
    this turn. Consumed by vision-capable generators (e.g. KimiVlGenerator
    in the C# port); text-only generators ignore it.
    """

    role: str
    content: str
    image_bytes: Optional[bytes] = None


@dataclass(frozen=True)
class DownloadProgress:
    """Progress report for a model or asset download."""

    file_name: str = ""
    bytes_received: int = 0
    total_bytes: int = 0
    bytes_per_second: float = 0.0
    estimated_time_remaining: float = 0.0  # seconds


# ── ChatResponse / FinishReason ────────────────────────────────────────────


class FinishReason(IntEnum):
    """Why a generation call stopped emitting tokens.

    Mirrors CircleAI.Inference.FinishReason.
    """

    STOP = 0
    """Hit a stop sequence — normal completion."""

    LENGTH = 1
    """Hit GenerationOptions.max_tokens."""

    CANCELLED = 2
    """The cancellation token fired."""

    ERROR = 3
    """Native generation reported an error before a stop sequence."""

    UNKNOWN = 4
    """Generator didn't surface a finish reason; treat as STOP."""


@dataclass(frozen=True)
class ChatResponse:
    """Structured response from IChatGenerator.generate_response_async.

    Carries the generated text alongside token counts, latency, and finish
    reason — the metadata callers need for rate-limiting, billing, telemetry,
    and trace stitching.
    """

    text: str
    tokens_in: int
    tokens_out: int
    latency_ms: float
    finish_reason: FinishReason = FinishReason.STOP


# ── BundleFile / InstalledManifest / UpgradeInfo ───────────────────────────


@dataclass(frozen=True)
class BundleFile:
    """One file inside a model bundle. Mirrors CircleAI.Core.Models.BundleFile."""

    name: str
    sha256: str
    size_bytes: int


@dataclass(frozen=True)
class InstalledManifest:
    """On-disk record of what was installed for a given model.

    Written by the downloader after every successful bundle install. Read
    by ModelRegistryService.check_for_upgrades_async to detect drift.
    """

    model_id: str
    version: str
    repo: Optional[str]
    total_bytes: int
    files: list[BundleFile]
    installed_at_utc: _dt.datetime


class UpgradeReason(IntEnum):
    """Why check_for_upgrades_async flagged a model."""

    VERSION_CHANGED = 0
    """Registry's Version string differs from installed."""

    SHA_CHANGED = 1
    """One or more file SHAs differ; Version string is identical."""

    BOTH = 2
    """Both Version and at least one SHA differ — common case for a release."""

    UNKNOWN = 3
    """No local installed.json found, but directory exists."""


@dataclass(frozen=True)
class UpgradeInfo:
    """One detected upgrade for a locally-installed model."""

    model_id: str
    installed_version: Optional[str]
    available_version: str
    reason: UpgradeReason
    estimated_download_bytes: int
    detected_at: _dt.datetime
