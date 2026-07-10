"""test_model_manager.py

Verifies CircleAI.Core.LocalModelManager: model-id sanitisation, missing-model
download via an injected IModelDownloader, checksum verification (raw bytes),
and disposal.
"""
from __future__ import annotations

import hashlib
import os

import pytest

from circle_ai.core.model_downloader import IModelDownloader
from circle_ai.core.model_manager import LocalModelManager


class _Downloader(IModelDownloader):
    def __init__(self, payload: bytes) -> None:
        self._payload = payload
        self.calls: list[str] = []

    async def download_model_async(self, model_id, local_path, ct=None) -> None:
        self.calls.append(model_id)
        os.makedirs(local_path, exist_ok=True)
        with open(os.path.join(local_path, "pytorch_model.bin"), "wb") as fh:
            fh.write(self._payload)

    async def download_from_candidates_async(self, candidate_urls, local_file_path, progress=None, ct=None):
        raise NotImplementedError


async def test_get_model_path_downloads_when_missing(tmp_path) -> None:
    dl = _Downloader(b"weights")
    mgr = LocalModelManager(models_directory=str(tmp_path / "models"), model_downloader=dl)
    path = await mgr.get_model_path_async("org/model-x")
    # '/' sanitised to '_'.
    assert path.endswith("org_model-x")
    assert os.path.isfile(os.path.join(path, "pytorch_model.bin"))
    assert dl.calls == ["org/model-x"]


async def test_get_model_path_skips_download_when_present(tmp_path) -> None:
    dl = _Downloader(b"weights")
    mgr = LocalModelManager(models_directory=str(tmp_path / "models"), model_downloader=dl)
    await mgr.get_model_path_async("m")
    await mgr.get_model_path_async("m")
    # Second call finds pytorch_model.bin already present — no re-download.
    assert dl.calls == ["m"]


async def test_get_model_path_no_downloader_raises(tmp_path) -> None:
    mgr = LocalModelManager(models_directory=str(tmp_path / "models"))
    with pytest.raises(RuntimeError):
        await mgr.get_model_path_async("m")


async def test_get_model_path_checksum_mismatch_raises(tmp_path) -> None:
    dl = _Downloader(b"weights")
    mgr = LocalModelManager(models_directory=str(tmp_path / "models"), model_downloader=dl)
    with pytest.raises(ValueError):
        await mgr.get_model_path_async("m", expected_checksum=b"\x00" * 32)


async def test_verify_model_async_true_on_match(tmp_path) -> None:
    payload = b"weights"
    dl = _Downloader(payload)
    mgr = LocalModelManager(models_directory=str(tmp_path / "models"), model_downloader=dl)
    path = await mgr.get_model_path_async("m")
    good = hashlib.sha256(payload).digest()
    assert await mgr.verify_model_async(path, good) is True
    assert await mgr.verify_model_async(path, b"\x00" * 32) is False


async def test_verify_model_async_empty_checksum_passes(tmp_path) -> None:
    dl = _Downloader(b"w")
    mgr = LocalModelManager(models_directory=str(tmp_path / "models"), model_downloader=dl)
    path = await mgr.get_model_path_async("m")
    assert await mgr.verify_model_async(path, b"") is True


async def test_repository_url_builds_modelscope_downloader(tmp_path) -> None:
    # With a repository URL and no explicit downloader, a ModelScope-backed
    # ModelDownloader is constructed. No download is triggered here — we only
    # assert construction + disposal work.
    mgr = LocalModelManager(
        model_repository_url="https://modelscope.cn", models_directory=str(tmp_path / "m")
    )
    mgr.dispose()


async def test_dispose_is_idempotent(tmp_path) -> None:
    mgr = LocalModelManager(models_directory=str(tmp_path / "m"))
    mgr.dispose()
    mgr.dispose()  # no raise
