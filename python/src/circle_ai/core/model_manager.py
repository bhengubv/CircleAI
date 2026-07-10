# core/model_manager.py
#
# Port of:
#   • CircleAI.Core.IModelManager
#   • CircleAI.Core.LocalModelManager
#
# IModelManager is the contract TextEmbedder consumes:
#   get_model_path_async(model_id, ct)         -> str
#   verify_model_async(model_path, checksum, ct) -> bool
#
# LocalModelManager resolves a model directory, downloads through an
# IModelDownloader when the model is missing (checked by presence of
# ``pytorch_model.bin``), and verifies the file's SHA-256 against an expected
# checksum (raw bytes, compared for equality as C# SequenceEqual does).

from __future__ import annotations

import hashlib
import os
from abc import ABC, abstractmethod
from typing import Optional

from .model_downloader import IModelDownloader, ModelDownloader
from .model_source import ModelScopeSource

__all__ = ["IModelManager", "LocalModelManager"]


# ─────────────────────────────────────────────────────────────────────────────
# IModelManager — the manager contract (disposable).
# ─────────────────────────────────────────────────────────────────────────────


class IModelManager(ABC):
    """Resolves and verifies model files on behalf of consumers such as
    TextEmbedder. Disposable."""

    @abstractmethod
    async def get_model_path_async(self, model_id: str, ct: object = None) -> str:
        """Resolve (downloading if necessary) the local path for *model_id*."""
        raise NotImplementedError

    @abstractmethod
    async def verify_model_async(
        self, model_path: str, expected_checksum: bytes, ct: object = None
    ) -> bool:
        """True if the file at *model_path* hashes to *expected_checksum*."""
        raise NotImplementedError

    def dispose(self) -> None:
        return None


# ─────────────────────────────────────────────────────────────────────────────
# LocalModelManager — filesystem-backed manager.
# ─────────────────────────────────────────────────────────────────────────────


class LocalModelManager(IModelManager):
    """Filesystem-backed :class:`IModelManager`.

    Two constructions mirror the C# overloads:
      * ``LocalModelManager(model_repository_url=..., models_directory=...)``
        — builds a :class:`ModelDownloader` over a :class:`ModelScopeSource`
        when a repository URL is supplied.
      * ``LocalModelManager(model_downloader=..., models_directory=...)``
        — injects a downloader directly.
    """

    _MODEL_FILE = "pytorch_model.bin"

    def __init__(
        self,
        model_repository_url: Optional[str] = None,
        models_directory: str = "Models",
        model_downloader: Optional[IModelDownloader] = None,
    ) -> None:
        self._models_directory = models_directory
        self._disposed = False

        if model_downloader is not None:
            self._model_downloader: Optional[IModelDownloader] = model_downloader
            self._owns_downloader = False
        elif model_repository_url is not None:
            # ModelScope (Alibaba) is the sole download source — no Western fallback.
            self._model_downloader = ModelDownloader(
                [ModelScopeSource()], owns_sources=True
            )
            self._owns_downloader = True
        else:
            self._model_downloader = None
            self._owns_downloader = False

        os.makedirs(self._models_directory, exist_ok=True)

    async def get_model_path_async(
        self, model_id: str, ct: object = None, expected_checksum: Optional[bytes] = None
    ) -> str:
        if self._disposed:
            raise RuntimeError("LocalModelManager is disposed")

        model_path = os.path.join(self._models_directory, self._sanitize_model_id(model_id))

        if not os.path.isdir(model_path) or not os.path.isfile(
            os.path.join(model_path, self._MODEL_FILE)
        ):
            if self._model_downloader is None:
                raise RuntimeError("Model not found and no downloader configured")
            await self._model_downloader.download_model_async(model_id, model_path, ct)

        if expected_checksum is not None and len(expected_checksum) > 0:
            actual = await self._compute_file_checksum_async(
                os.path.join(model_path, self._MODEL_FILE)
            )
            if actual != expected_checksum:
                raise ValueError(
                    f"Model checksum verification failed for '{model_id}'. "
                    "The file may be corrupt or tampered with."
                )

        return model_path

    async def verify_model_async(
        self, model_path: str, expected_checksum: bytes, ct: object = None
    ) -> bool:
        """Verify the file at *model_path* against *expected_checksum*.

        If *model_path* is a directory (as returned by
        :meth:`get_model_path_async`), the ``pytorch_model.bin`` inside it is
        hashed. When *expected_checksum* is empty, verification passes (there is
        nothing to check against)."""
        if expected_checksum is None or len(expected_checksum) == 0:
            return True
        target = model_path
        if os.path.isdir(model_path):
            target = os.path.join(model_path, self._MODEL_FILE)
        if not os.path.isfile(target):
            return False
        actual = await self._compute_file_checksum_async(target)
        return actual == expected_checksum

    @staticmethod
    def _sanitize_model_id(model_id: str) -> str:
        return model_id.replace("/", "_").replace("\\", "_")

    @staticmethod
    async def _compute_file_checksum_async(file_path: str) -> bytes:
        sha = hashlib.sha256()
        with open(file_path, "rb") as fh:
            for chunk in iter(lambda: fh.read(65536), b""):
                sha.update(chunk)
        return sha.digest()

    def dispose(self) -> None:
        if self._disposed:
            return
        if self._owns_downloader and self._model_downloader is not None:
            disp = getattr(self._model_downloader, "dispose", None)
            if callable(disp):
                disp()
        self._disposed = True
