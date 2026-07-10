"""Native runtime status holder.

Ports ``CircleAI.Inference.Server.Lifecycle.INativeRuntimeStatus`` +
``NativeRuntimeStatus``, plus a small ``NativeRuntimePaths`` record standing in
for ``NativeRuntimePrep.NativeRuntimePaths`` (the resolved native library paths
surfaced through /v1/diagnostics). The bridge factory updates this after every
successful runtime prep; the diagnostics endpoint reads it.
"""
from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from typing import Optional

__all__ = ["NativeRuntimePaths", "INativeRuntimeStatus", "NativeRuntimeStatus"]


@dataclass(frozen=True, slots=True)
class NativeRuntimePaths:
    """Resolved native-runtime library paths. Stands in for
    ``NativeRuntimePrep.NativeRuntimePaths`` — enough surface for diagnostics.
    """

    bridge_path: str
    mnn_core_path: str
    extracted_root: str
    self_check_passed: bool = True


class INativeRuntimeStatus(ABC):
    """Singleton holder of the last-known native-runtime paths. Mirrors
    ``INativeRuntimeStatus``.
    """

    @property
    @abstractmethod
    def latest(self) -> Optional[NativeRuntimePaths]: ...

    @abstractmethod
    def update(self, paths: NativeRuntimePaths) -> None: ...


class NativeRuntimeStatus(INativeRuntimeStatus):
    """Default thread-safe implementation. Mirrors ``NativeRuntimeStatus``."""

    __slots__ = ("_lock", "_latest")

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._latest: Optional[NativeRuntimePaths] = None

    @property
    def latest(self) -> Optional[NativeRuntimePaths]:
        with self._lock:
            return self._latest

    def update(self, paths: NativeRuntimePaths) -> None:
        if paths is None:
            raise ValueError("paths is required")
        with self._lock:
            self._latest = paths
