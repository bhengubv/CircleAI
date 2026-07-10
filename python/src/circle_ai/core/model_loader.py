# core/model_loader.py
#
# Port of:
#   • CircleAI.Core.IModelLoader
#   • CircleAI.Core.LocalModelLoader
#
# LocalModelLoader downloads single-file model entries, verifies SHA-256,
# resolves model paths and reports whether a model exists (checksum-verified).
# Bundle-shaped entries are steered to the (out-of-scope) bundle downloader
# with the exact C# error message.
#
# Two injection points replace C# ambient dependencies:
#   • The registry (C# embedded registry.json) is passed in as a dict / path.
#   • The download transport (C# HttpClient) is an injectable ``downloader``
#     callable ``(url, out_path) -> None``; the default resolves file:// URLs
#     and bare paths from disk. ``check_for_critical_update_async`` takes an
#     injectable ``versions_fetcher`` (default returns "" — no critical flag).

from __future__ import annotations

import hashlib
import os
from abc import ABC, abstractmethod
from dataclasses import dataclass
from typing import Callable, Dict, List, Mapping, Optional

from .model_source import Fetcher, local_file_fetcher

__all__ = ["IModelLoader", "LocalModelLoader", "ModelInfo", "BundleFileInfo"]

_BUNDLE_ANCHOR_FILE_NAME = "llm.mnn.weight"


# ─────────────────────────────────────────────────────────────────────────────
# Registry row shapes — mirror LocalModelLoader.ModelInfo / BundleFileInfo.
# ─────────────────────────────────────────────────────────────────────────────


@dataclass(frozen=True, slots=True)
class BundleFileInfo:
    name: str
    sha256: str
    size_bytes: int = 0


@dataclass(frozen=True, slots=True)
class ModelInfo:
    file_name: Optional[str] = None
    primary_url: Optional[str] = None
    fallback_url: Optional[str] = None
    checksum: Optional[str] = None
    size_bytes: int = 0
    version: str = ""
    architecture: str = ""
    quantization_type: str = ""
    repo: Optional[str] = None
    total_bytes: int = 0
    bundle_files: Optional[List[BundleFileInfo]] = None

    @property
    def is_bundle(self) -> bool:
        return self.bundle_files is not None and len(self.bundle_files) > 0

    @staticmethod
    def from_json(obj: Mapping[str, object]) -> "ModelInfo":
        low = {str(k).lower(): v for k, v in obj.items()}

        def g(key: str):
            return low.get(key.lower())

        bundle_raw = g("BundleFiles")
        bundle_files: Optional[List[BundleFileInfo]] = None
        if isinstance(bundle_raw, list):
            bundle_files = []
            for bf in bundle_raw:
                if isinstance(bf, Mapping):
                    bl = {str(k).lower(): v for k, v in bf.items()}
                    bundle_files.append(
                        BundleFileInfo(
                            name=str(bl.get("name", "")),
                            sha256=str(bl.get("sha256", "")),
                            size_bytes=int(bl.get("sizebytes", 0) or 0),
                        )
                    )

        return ModelInfo(
            file_name=_opt(g("FileName")),
            primary_url=_opt(g("PrimaryUrl")),
            fallback_url=_opt(g("FallbackUrl")),
            checksum=_opt(g("Checksum")),
            size_bytes=int(g("SizeBytes") or 0),
            version=str(g("Version") or ""),
            architecture=str(g("Architecture") or ""),
            quantization_type=str(g("QuantizationType") or ""),
            repo=_opt(g("Repo")),
            total_bytes=int(g("TotalBytes") or 0),
            bundle_files=bundle_files,
        )


def _opt(v: object) -> Optional[str]:
    return None if v is None else str(v)


# ─────────────────────────────────────────────────────────────────────────────
# IModelLoader — the loader contract.
# ─────────────────────────────────────────────────────────────────────────────


class IModelLoader(ABC):
    """Acquires, caches and verifies model files; disposable."""

    @abstractmethod
    async def download_model_async(
        self, model_name: str, progress: Optional[Callable[[float], None]] = None
    ) -> str:
        """Download *model_name*, returning the local path."""
        raise NotImplementedError

    @abstractmethod
    def get_model_path(self, model_name: str) -> str:
        """Resolve the local path for *model_name* (may not exist yet)."""
        raise NotImplementedError

    @abstractmethod
    def model_exists(self, model_name: str) -> bool:
        """True if the model is present locally AND checksum-verified."""
        raise NotImplementedError

    @abstractmethod
    async def check_for_critical_update_async(self) -> bool:
        """True if a ``[CRITICAL]`` marker is present in the remote versions
        feed."""
        raise NotImplementedError

    def dispose(self) -> None:
        return None


# ─────────────────────────────────────────────────────────────────────────────
# LocalModelLoader — single-file loader with SHA-256 verification.
# ─────────────────────────────────────────────────────────────────────────────


class LocalModelLoader(IModelLoader):
    """:class:`IModelLoader` that caches single-file model entries on disk and
    verifies them with SHA-256."""

    def __init__(
        self,
        model_directory: Optional[str] = None,
        registry: Optional[Mapping[str, object]] = None,
        downloader: Optional[Fetcher] = None,
        versions_fetcher: Optional[Callable[[], str]] = None,
    ) -> None:
        if model_directory is not None:
            self._model_dir = model_directory
        else:
            appdata = (
                os.environ.get("APPDATA")
                or os.environ.get("LOCALAPPDATA")
                or os.path.join(os.path.expanduser("~"), ".config")
            )
            self._model_dir = os.path.join(appdata, "CircleAI", "Models")
        os.makedirs(self._model_dir, exist_ok=True)

        self._model_registry: Dict[str, ModelInfo] = self._load_registry(registry)
        self._downloader: Fetcher = downloader if downloader is not None else local_file_fetcher
        self._versions_fetcher = versions_fetcher
        self._disposed = False

    @staticmethod
    def _load_registry(registry: Optional[Mapping[str, object]]) -> Dict[str, ModelInfo]:
        out: Dict[str, ModelInfo] = {}
        if registry is None:
            return out
        for key, value in registry.items():
            # Skip non-object metadata (mirror the C# ValueKind != Object skip).
            if not isinstance(value, Mapping):
                continue
            out[str(key)] = ModelInfo.from_json(value)
        return out

    # ── IModelLoader ────────────────────────────────────────────────────────

    async def download_model_async(
        self, model_name: str, progress: Optional[Callable[[float], None]] = None
    ) -> str:
        if self._disposed:
            raise RuntimeError("LocalModelLoader is disposed")
        model_info = self._model_registry.get(model_name)
        if model_info is None:
            raise ValueError(f"Model {model_name} not supported")

        if model_info.is_bundle:
            raise RuntimeError(
                f"Model '{model_name}' is a multi-file bundle (registry entry has "
                "BundleFiles[]); use ModelDownloadService.EnsureBundleAsync via "
                "MnnInferenceBridgeFactory instead. LocalModelLoader.DownloadModelAsync "
                "only handles legacy single-file entries."
            )

        local_path = os.path.join(self._model_dir, model_info.file_name)

        if os.path.exists(local_path):
            if model_info.checksum is None or model_info.checksum.startswith("sha256:TBD"):
                return local_path
            if self._verify_checksum(local_path, model_info.checksum):
                return local_path
            os.remove(local_path)

        sources = [model_info.primary_url, model_info.fallback_url]
        last_error: Optional[Exception] = None
        for url in sources:
            if url is None or url.strip() == "":
                continue
            try:
                self._download_file(url, local_path)
                if model_info.checksum is None or model_info.checksum.startswith("sha256:TBD"):
                    return local_path
                if self._verify_checksum(local_path, model_info.checksum):
                    return local_path
                os.remove(local_path)
                last_error = ValueError("Downloaded model failed checksum verification.")
            except Exception as ex:  # noqa: BLE001 — mirror C# fallthrough
                last_error = ex

        raise last_error if last_error is not None else RuntimeError("All sources failed.")

    def _download_file(self, url: str, output_path: str) -> None:
        data = self._downloader(url)
        d = os.path.dirname(output_path)
        if d:
            os.makedirs(d, exist_ok=True)
        with open(output_path, "wb") as fh:
            fh.write(data)

    def get_model_path(self, model_name: str) -> str:
        if self._disposed:
            raise RuntimeError("LocalModelLoader is disposed")
        model_info = self._model_registry.get(model_name)
        if model_info is None:
            raise FileNotFoundError(f"Model {model_name} not found")

        if model_info.is_bundle:
            return os.path.join(self._model_dir, model_name, _BUNDLE_ANCHOR_FILE_NAME)
        return os.path.join(self._model_dir, model_info.file_name)

    def model_exists(self, model_name: str) -> bool:
        try:
            model_info = self._model_registry.get(model_name)
            if model_info is None:
                return False
            path = self.get_model_path(model_name)
            if not os.path.isfile(path):
                return False
            if model_info.is_bundle:
                anchor = None
                if model_info.bundle_files is not None:
                    for f in model_info.bundle_files:
                        if f.name.lower() == _BUNDLE_ANCHOR_FILE_NAME.lower():
                            anchor = f
                            break
                if anchor is None:
                    return False
                return self._verify_checksum(path, anchor.sha256)
            return model_info.checksum is not None and self._verify_checksum(
                path, model_info.checksum
            )
        except Exception:  # noqa: BLE001 — C# swallows and returns false
            return False

    async def check_for_critical_update_async(self) -> bool:
        try:
            text = self._versions_fetcher() if self._versions_fetcher is not None else ""
            return "[CRITICAL]" in text
        except Exception:  # noqa: BLE001
            return False

    # ── helpers ───────────────────────────────────────────────────────────────

    @staticmethod
    def _verify_checksum(file_path: str, expected_checksum: str) -> bool:
        sha = hashlib.sha256()
        with open(file_path, "rb") as fh:
            for chunk in iter(lambda: fh.read(65536), b""):
                sha.update(chunk)
        actual_hex = sha.hexdigest().lower()

        expected = (expected_checksum or "").strip()
        if expected.lower().startswith("sha256:"):
            expected = expected[len("sha256:") :].strip()
        return expected.lower() == actual_hex

    def dispose(self) -> None:
        if self._disposed:
            return
        self._disposed = True
