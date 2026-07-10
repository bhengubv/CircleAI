"""Model download service — single-file + bundle downloader with SHA verify.

Ports ``CircleAI.Inference.IModelDownloadService`` + ``ModelDownloadService`` +
``BundleFileSpec``. The C# implementation drives ``HttpClient``; Python injects
the fetch seam behind :class:`IFileFetcher` so the SHA-verify, primary/fallback
URL construction, temp-file swap, and installed-manifest logic are exercised
deterministically in-process. A ``file://`` fetcher is the default so real
round-trips work without a network.

Byte-format parity points preserved from C#:
  * single-file entries land at ``{root}/{modelId}.gguf``; bundles at ``{root}/{modelId}/``,
  * SHA-256 verification strips an optional ``sha256:`` algorithm prefix
    (:func:`strip_sha_algorithm_prefix`) and compares case-insensitively,
  * primary URL is the ModelScope API form, fallback is the CDN resolve form,
  * ``installed.json`` manifest shape matches ``InstalledManifest``.
"""
from __future__ import annotations

import hashlib
import json
import os
import shutil
import urllib.parse
import urllib.request
from abc import ABC, abstractmethod
from dataclasses import dataclass
from datetime import datetime, timezone
from typing import Callable, List, Optional, Sequence

__all__ = [
    "BundleFileSpec",
    "IFileFetcher",
    "FileUrlFetcher",
    "IModelDownloadService",
    "ModelDownloadService",
    "strip_sha_algorithm_prefix",
]

_PROGRESS_CHUNK_BYTES = 1 * 1024 * 1024  # 1 MB


@dataclass(frozen=True, slots=True)
class BundleFileSpec:
    """One file in a model bundle. Mirrors ``CircleAI.Inference.BundleFileSpec``.

    ``sha256`` may be in ``sha256:<hex>`` or bare-hex form; the verify path
    strips the optional prefix before comparing.
    """

    name: str
    sha256: str
    size_bytes: int = 0


def strip_sha_algorithm_prefix(raw: str) -> str:
    """Return the hex portion of a checksum, stripping an optional leading
    algorithm token of the form ``sha256:`` / ``SHA-256:`` etc.

    Port of ``ModelDownloadService.StripShaAlgorithmPrefix``: the prefix must
    be 1..16 chars of letters/digits/``-``/``_`` before a colon; otherwise the
    whole trimmed string is returned.
    """
    if not raw:
        return ""
    trimmed = raw.strip()
    colon = trimmed.find(":")
    if colon < 0:
        return trimmed
    prefix = trimmed[:colon]
    if 0 < len(prefix) <= 16:
        is_alg = all(c.isalnum() or c in ("-", "_") for c in prefix)
        if is_alg:
            return trimmed[colon + 1:].strip()
    return trimmed


class IFileFetcher(ABC):
    """Injected fetch seam: download ``url`` to ``dest_path``.

    Replaces the C# ``HttpClient.GetAsync`` streaming download. ``progress`` is
    an optional callback taking a float in [0, 1]. Raises on failure (the
    bundle path relies on a raising primary to fall back to the CDN URL).
    """

    @abstractmethod
    async def fetch_async(
        self,
        url: str,
        dest_path: str,
        progress: Optional[Callable[[float], None]] = None,
        ct: object = None,
    ) -> None: ...


class FileUrlFetcher(IFileFetcher):
    """Default fetcher. Supports ``file://`` URLs (real byte copy) — the
    deterministic stand-in for a network fetch. Non-``file`` schemes fall back
    to :mod:`urllib` so a real HTTP endpoint works when one is available.
    """

    async def fetch_async(
        self,
        url: str,
        dest_path: str,
        progress: Optional[Callable[[float], None]] = None,
        ct: object = None,
    ) -> None:
        parsed = urllib.parse.urlparse(url)
        d = os.path.dirname(dest_path)
        if d:
            os.makedirs(d, exist_ok=True)
        if parsed.scheme == "file":
            src = urllib.request.url2pathname(parsed.path)
            total = os.path.getsize(src)
            with open(src, "rb") as rf, open(dest_path, "wb") as wf:
                read = 0
                until_report = _PROGRESS_CHUNK_BYTES
                while True:
                    buf = rf.read(81_920)
                    if not buf:
                        break
                    wf.write(buf)
                    read += len(buf)
                    until_report -= len(buf)
                    if progress is not None and until_report <= 0:
                        ratio = (read / total) if total > 0 else 0.0
                        progress(min(ratio, 0.999))
                        until_report = _PROGRESS_CHUNK_BYTES
            if progress is not None:
                progress(1.0)
            return
        # Generic scheme (http/https): blocking urlopen; adequate for the port.
        with urllib.request.urlopen(url) as resp:  # noqa: S310 - injectable seam
            data = resp.read()
        with open(dest_path, "wb") as wf:
            wf.write(data)
        if progress is not None:
            progress(1.0)


def _sha256_file_hex(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(81_920), b""):
            h.update(chunk)
    return h.hexdigest()


def _verify_sha256(path: str, expected: str) -> bool:
    actual = _sha256_file_hex(path)
    expected_norm = strip_sha_algorithm_prefix(expected)
    return actual.lower() == expected_norm.lower()


def _build_primary_url(repo: str, file_name: str) -> str:
    esc = urllib.parse.quote(file_name, safe="")
    return f"https://modelscope.cn/api/v1/models/{repo}/repo?Revision=master&FilePath={esc}"


def _build_fallback_url(repo: str, file_name: str) -> str:
    esc = urllib.parse.quote(file_name, safe="")
    return f"https://modelscope.cn/models/{repo}/resolve/master/{esc}"


class IModelDownloadService(ABC):
    """Downloads and manages model files on disk. Mirrors
    ``CircleAI.Inference.IModelDownloadService``.
    """

    @abstractmethod
    async def ensure_model_async(
        self,
        model_id: str,
        download_uri: str,
        expected_sha256: Optional[str],
        progress: Optional[Callable[[float], None]] = None,
        ct: object = None,
    ) -> str: ...

    @abstractmethod
    async def ensure_bundle_async(
        self,
        model_id: str,
        repo: str,
        bundle_files: Sequence[BundleFileSpec],
        progress: Optional[Callable[[float], None]] = None,
        ct: object = None,
    ) -> str: ...

    @abstractmethod
    async def is_model_cached_async(self, model_id: str, ct: object = None) -> bool: ...

    @abstractmethod
    async def delete_model_async(self, model_id: str, ct: object = None) -> None: ...

    @abstractmethod
    async def get_available_disk_space_bytes_async(self, ct: object = None) -> int: ...


class ModelDownloadService(IModelDownloadService):
    """Default :class:`IModelDownloadService`. Port of
    ``CircleAI.Inference.ModelDownloadService``.

    Single-file entries land at ``{root}/{modelId}.gguf``; bundle entries at
    ``{root}/{modelId}/`` with each file written beneath it. Construct with an
    optional :class:`IFileFetcher` (defaults to :class:`FileUrlFetcher`).
    """

    __slots__ = ("_root", "_fetcher")

    def __init__(
        self, storage_directory: str, fetcher: Optional[IFileFetcher] = None
    ) -> None:
        if not storage_directory or not storage_directory.strip():
            raise ValueError("Storage directory must not be empty.")
        self._root = storage_directory
        self._fetcher = fetcher if fetcher is not None else FileUrlFetcher()
        os.makedirs(self._root, exist_ok=True)

    # ── Single-file (legacy) ──────────────────────────────────────────────

    async def ensure_model_async(
        self,
        model_id: str,
        download_uri: str,
        expected_sha256: Optional[str],
        progress: Optional[Callable[[float], None]] = None,
        ct: object = None,
    ) -> str:
        _validate_model_id(model_id)
        if download_uri is None:
            raise ValueError("download_uri is required")

        file_path = self._single_file_path(model_id)

        if os.path.isfile(file_path) and expected_sha256 is not None:
            if _verify_sha256(file_path, expected_sha256):
                if progress is not None:
                    progress(1.0)
                return file_path
            os.remove(file_path)
        elif os.path.isfile(file_path) and expected_sha256 is None:
            if progress is not None:
                progress(1.0)
            return file_path

        temp_path = file_path + ".tmp"
        try:
            await self._fetcher.fetch_async(download_uri, temp_path, progress, ct)
            if expected_sha256 is not None:
                if not _verify_sha256(temp_path, expected_sha256):
                    os.remove(temp_path)
                    raise RuntimeError(
                        f"SHA-256 mismatch for model '{model_id}'. "
                        "The downloaded file has been deleted."
                    )
            if os.path.isfile(file_path):
                os.remove(file_path)
            os.replace(temp_path, file_path)
        except BaseException:
            if os.path.isfile(temp_path):
                os.remove(temp_path)
            raise
        return file_path

    # ── Bundle ────────────────────────────────────────────────────────────

    async def ensure_bundle_async(
        self,
        model_id: str,
        repo: str,
        bundle_files: Sequence[BundleFileSpec],
        progress: Optional[Callable[[float], None]] = None,
        ct: object = None,
    ) -> str:
        _validate_model_id(model_id)
        if not repo or not repo.strip():
            raise ValueError("Repo path is required for bundle entries.")
        if bundle_files is None:
            raise ValueError("bundle_files is required")
        bundle_files = list(bundle_files)
        if len(bundle_files) == 0:
            raise ValueError("Bundle file list must not be empty.")

        model_dir = os.path.join(self._root, model_id)
        os.makedirs(model_dir, exist_ok=True)

        total_bytes = sum(max(0, f.size_bytes) for f in bundle_files)
        done_bytes = 0

        for file in bundle_files:
            if not file.name or not file.name.strip():
                raise RuntimeError(f"Bundle for '{model_id}' contains a file with no Name.")

            dest_path = os.path.join(model_dir, file.name)
            os.makedirs(os.path.dirname(dest_path) or model_dir, exist_ok=True)

            # Skip when cached + valid.
            if os.path.isfile(dest_path) and _verify_sha256(dest_path, file.sha256):
                done_bytes += file.size_bytes
                _report_overall(progress, done_bytes, total_bytes)
                continue
            if os.path.isfile(dest_path):
                os.remove(dest_path)

            temp_path = dest_path + ".tmp"

            def _per_file(p: float, _base: int = done_bytes, _size: int = file.size_bytes) -> None:
                _report_overall(progress, _base + int(_size * p), total_bytes)

            per_file = None if progress is None else _per_file

            try:
                primary = _build_primary_url(repo, file.name)
                fallback = _build_fallback_url(repo, file.name)
                try:
                    await self._fetcher.fetch_async(primary, temp_path, per_file, ct)
                except Exception:
                    if os.path.isfile(temp_path):
                        os.remove(temp_path)
                    await self._fetcher.fetch_async(fallback, temp_path, per_file, ct)

                if not _verify_sha256(temp_path, file.sha256):
                    os.remove(temp_path)
                    raise RuntimeError(
                        f"SHA-256 mismatch for bundle file '{file.name}' of model '{model_id}'. "
                        "The downloaded file has been deleted."
                    )
                if os.path.isfile(dest_path):
                    os.remove(dest_path)
                os.replace(temp_path, dest_path)
                done_bytes += file.size_bytes
                _report_overall(progress, done_bytes, total_bytes)
            except BaseException:
                if os.path.isfile(temp_path):
                    try:
                        os.remove(temp_path)
                    except OSError:
                        pass
                raise

        if progress is not None:
            progress(1.0)
        return model_dir

    async def write_installed_manifest_async(
        self,
        model_dir: str,
        model_id: str,
        version: str,
        repo: Optional[str],
        bundle_files: Sequence[BundleFileSpec],
        ct: object = None,
    ) -> None:
        """Stamp an ``installed.json`` describing what's on disk. Best-effort —
        failures are swallowed (a missing manifest just downgrades upgrade
        detection to ``UNKNOWN``). Mirrors ``WriteInstalledManifestAsync``.
        """
        try:
            if not model_dir or not model_dir.strip():
                raise ValueError("model_dir required")
            if not model_id or not model_id.strip():
                raise ValueError("model_id required")
            if bundle_files is None:
                raise ValueError("bundle_files required")

            total_bytes = 0
            files = []
            for f in bundle_files:
                files.append({"Name": f.name, "Sha256": f.sha256, "SizeBytes": f.size_bytes})
                total_bytes += max(0, f.size_bytes)

            manifest = {
                "ModelId": model_id,
                "Version": version or "",
                "Repo": repo,
                "TotalBytes": total_bytes,
                "Files": files,
                "InstalledAtUtc": datetime.now(timezone.utc).isoformat(),
            }
            path = os.path.join(model_dir, "installed.json")
            with open(path, "w", encoding="utf-8") as fh:
                json.dump(manifest, fh, indent=2)
        except Exception:
            # Best-effort. Never a hard failure.
            pass

    # ── Common ────────────────────────────────────────────────────────────

    async def is_model_cached_async(self, model_id: str, ct: object = None) -> bool:
        _validate_model_id(model_id)
        if os.path.isfile(self._single_file_path(model_id)):
            return True
        return os.path.isdir(os.path.join(self._root, model_id))

    async def delete_model_async(self, model_id: str, ct: object = None) -> None:
        _validate_model_id(model_id)
        single = self._single_file_path(model_id)
        if os.path.isfile(single):
            os.remove(single)
        d = os.path.join(self._root, model_id)
        if os.path.isdir(d):
            shutil.rmtree(d)

    async def get_available_disk_space_bytes_async(self, ct: object = None) -> int:
        absolute = os.path.abspath(self._root)
        usage = shutil.disk_usage(absolute)
        return usage.free

    def dispose(self) -> None:
        """No-op — the injectable fetcher owns no unmanaged resources here."""

    # ── Helpers ───────────────────────────────────────────────────────────

    def _single_file_path(self, model_id: str) -> str:
        return os.path.join(self._root, f"{model_id}.gguf")


def _validate_model_id(model_id: str) -> None:
    if not model_id or not model_id.strip():
        raise ValueError("Model ID must not be empty.")


def _report_overall(
    progress: Optional[Callable[[float], None]], done: int, total: int
) -> None:
    if progress is None:
        return
    if total <= 0:
        progress(0.0)
    else:
        progress(min(0.999, done / total))
