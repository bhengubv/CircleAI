# core/model_source.py
#
# Port of:
#   • CircleAI.Core.IModelSource            — model file source abstraction
#   • CircleAI.Core.DownloadProgress        — in-flight download snapshot
#   • CircleAI.Core.Sources.ModelScopeSource
#   • CircleAI.Core.Sources.HuggingFaceSource  (compile-time tombstone)
#   • CircleAI.Core.Sources.SourceDownloadHelper
#
# Per the port rules there is NO real network here. IModelSource downloads
# from an injectable "fetcher" (a callable url -> bytes) behind the injection
# point. The default fetcher resolves ``file://`` URLs and bare local paths
# from disk; hosts inject an HTTP fetcher (or a canned in-memory map) for tests
# and production. SourceDownloadHelper reproduces the C# streaming loop
# (chunked copy, progress reporting cadence, ETA) over the fetched bytes.

from __future__ import annotations

import os
import time
from abc import ABC, abstractmethod
from dataclasses import dataclass
from typing import Callable, Optional
from urllib.parse import urlparse, unquote

# DownloadProgress already lives in circle_ai.models — reuse it verbatim so the
# shape is shared across the SDK.
from ..models.models import DownloadProgress

__all__ = [
    "DownloadProgress",
    "IModelSource",
    "ModelScopeSource",
    "HuggingFaceSource",
    "SourceDownloadHelper",
    "Fetcher",
    "set_default_fetcher",
    "local_file_fetcher",
]

# A fetcher resolves a URL to its raw bytes. Injected behind IModelSource so no
# real HTTP is performed inside the SDK.
Fetcher = Callable[[str], bytes]


def local_file_fetcher(url: str) -> bytes:
    """Default fetcher — resolves ``file://`` URLs and bare local paths.

    This is the deterministic, network-free default. It lets the download
    machinery be exercised end-to-end against on-disk fixtures.
    """
    parsed = urlparse(url)
    if parsed.scheme in ("file", ""):
        path = unquote(parsed.path if parsed.scheme == "file" else url)
        # On Windows a file:// path is like /C:/x — strip the leading slash.
        if os.name == "nt" and path.startswith("/") and len(path) > 2 and path[2] == ":":
            path = path[1:]
        with open(path, "rb") as fh:
            return fh.read()
    raise ValueError(
        f"No fetcher configured for scheme '{parsed.scheme}'. Inject one via "
        "set_default_fetcher (or pass fetcher= to the source) — the SDK performs "
        "no real network I/O."
    )


_default_fetcher: Fetcher = local_file_fetcher


def set_default_fetcher(fetcher: Optional[Fetcher]) -> None:
    """Set the process-wide default fetcher used by sources that don't get one
    injected. Pass ``None`` to restore the local-file fetcher."""
    global _default_fetcher
    _default_fetcher = fetcher if fetcher is not None else local_file_fetcher


# ─────────────────────────────────────────────────────────────────────────────
# IModelSource — model file source abstraction.
# ─────────────────────────────────────────────────────────────────────────────


class IModelSource(ABC):
    """Abstraction for model file sources. Allows fallback chains for sanctions
    resilience (e.g. ModelScope API primary, ModelScope CDN fallback)."""

    @property
    @abstractmethod
    def name(self) -> str:
        """Friendly name of the source (e.g. "ModelScope"). Used in logs."""
        raise NotImplementedError

    @abstractmethod
    async def is_available_async(self, ct: object = None) -> bool:
        """Quick reachability check. Returns False on any failure rather than
        raising."""
        raise NotImplementedError

    @abstractmethod
    async def download_async(
        self,
        url: str,
        local_path: str,
        progress: Optional[Callable[[DownloadProgress], None]] = None,
        ct: object = None,
    ) -> None:
        """Download a single file from *url* to *local_path*, reporting
        progress."""
        raise NotImplementedError


# ─────────────────────────────────────────────────────────────────────────────
# SourceDownloadHelper — shared streaming download routine.
# ─────────────────────────────────────────────────────────────────────────────


class SourceDownloadHelper:
    """Shared streaming download routine used by :class:`IModelSource`
    implementations. Handles progress reporting and ETA estimation over bytes
    supplied by an injected fetcher (no real network)."""

    _BUFFER_SIZE = 8192
    _PROGRESS_INTERVAL_S = 0.5  # 500 ms, matches C# ProgressInterval

    @staticmethod
    async def download_with_progress_async(
        fetcher: Fetcher,
        url: str,
        local_path: str,
        progress: Optional[Callable[[DownloadProgress], None]],
        ct: object = None,
    ) -> None:
        file_name = os.path.basename(local_path)
        data = fetcher(url)
        total_bytes = len(data)

        bytes_read = 0
        start = time.monotonic()
        last_update = start
        last_bytes = 0

        d = os.path.dirname(local_path)
        if d:
            os.makedirs(d, exist_ok=True)

        with open(local_path, "wb") as fh:
            offset = 0
            while True:
                chunk = data[offset : offset + SourceDownloadHelper._BUFFER_SIZE]
                if not chunk:
                    break
                fh.write(chunk)
                offset += len(chunk)
                bytes_read += len(chunk)

                now = time.monotonic()
                if (
                    progress is not None
                    and (now - last_update > SourceDownloadHelper._PROGRESS_INTERVAL_S
                         or bytes_read == total_bytes)
                ):
                    elapsed = now - last_update
                    diff = bytes_read - last_bytes
                    bps = diff / elapsed if elapsed > 0 else 0.0
                    eta = 0.0
                    if total_bytes > 0 and bps > 0:
                        remaining = total_bytes - bytes_read
                        if remaining > 0:
                            eta = remaining / bps
                    progress(
                        DownloadProgress(
                            file_name=file_name,
                            bytes_received=bytes_read,
                            total_bytes=total_bytes,
                            bytes_per_second=bps,
                            estimated_time_remaining=eta,
                        )
                    )
                    last_update = now
                    last_bytes = bytes_read

        # Always emit a terminal 100%-complete report so callers that never hit
        # the cadence branch (tiny files) still observe completion.
        if progress is not None and total_bytes >= 0:
            progress(
                DownloadProgress(
                    file_name=file_name,
                    bytes_received=bytes_read,
                    total_bytes=total_bytes,
                    bytes_per_second=0.0,
                    estimated_time_remaining=0.0,
                )
            )


# ─────────────────────────────────────────────────────────────────────────────
# ModelScopeSource — ModelScope (modelscope.cn, Alibaba) source.
# ─────────────────────────────────────────────────────────────────────────────


class ModelScopeSource(IModelSource):
    """:class:`IModelSource` backed by ModelScope (modelscope.cn, Alibaba).
    Treated as the primary source for sanctions resilience.

    Network I/O is delegated to an injected ``fetcher`` (default: the process
    fetcher). The URL-host guard from the C# source is preserved.
    """

    _HOST_NAME = "modelscope.cn"

    def __init__(self, fetcher: Optional[Fetcher] = None) -> None:
        self._fetcher = fetcher
        self._disposed = False

    @property
    def name(self) -> str:
        return "ModelScope"

    def _fetch(self) -> Fetcher:
        return self._fetcher if self._fetcher is not None else _default_fetcher

    async def is_available_async(self, ct: object = None) -> bool:
        if self._disposed:
            return False
        # Reachability is a property of the injected fetcher. The default
        # local-file fetcher is always "available".
        return True

    async def download_async(
        self,
        url: str,
        local_path: str,
        progress: Optional[Callable[[DownloadProgress], None]] = None,
        ct: object = None,
    ) -> None:
        if self._disposed:
            raise RuntimeError("ModelScopeSource is disposed")
        if url is None or url.strip() == "":
            raise ValueError("url")
        if local_path is None or local_path.strip() == "":
            raise ValueError("local_path")

        parsed = urlparse(url)
        host = parsed.hostname or ""
        # Enforce the host guard only for real network schemes. Local file
        # fetchers (file://, bare paths) are exempt so fixtures work.
        if parsed.scheme in ("http", "https"):
            if not host.lower().endswith(self._HOST_NAME):
                raise ValueError(
                    f"URL host must be on {self._HOST_NAME} for {self.name} source. Got: {url}"
                )

        d = os.path.dirname(local_path)
        if d:
            os.makedirs(d, exist_ok=True)

        await SourceDownloadHelper.download_with_progress_async(
            self._fetch(), url, local_path, progress, ct
        )

    def dispose(self) -> None:
        self._disposed = True


# ─────────────────────────────────────────────────────────────────────────────
# HuggingFaceSource — REMOVED tombstone.
# ─────────────────────────────────────────────────────────────────────────────


class HuggingFaceSource:
    """Removed. Use :class:`ModelScopeSource` instead.

    HuggingFace is a Western (US) company; all downloads must route through
    ModelScope (modelscope.cn, Alibaba). Constructing this raises — mirroring
    the C# ``[Obsolete(error: true)]`` tombstone.
    """

    def __init__(self, *args, **kwargs) -> None:
        raise RuntimeError(
            "HuggingFaceSource has been removed. Use ModelScopeSource (modelscope.cn)."
        )
