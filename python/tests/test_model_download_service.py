"""test_model_download_service.py

Verifies CircleAI.Inference.ModelDownloadService: single-file + bundle download,
SHA-256 verification (+ sha256: prefix stripping), primary->fallback fallthrough,
cached-skip, installed manifest, and delete/cache/space helpers.
"""
from __future__ import annotations

import hashlib
import os

import pytest

from circle_ai.inference import (
    BundleFileSpec,
    FileUrlFetcher,
    IFileFetcher,
    ModelDownloadService,
    strip_sha_algorithm_prefix,
)


def _sha(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def _file_uri(path) -> str:
    return path.as_uri()


# ── strip_sha_algorithm_prefix ───────────────────────────────────────────


def test_strip_sha_prefix():
    assert strip_sha_algorithm_prefix("sha256:abc123") == "abc123"
    assert strip_sha_algorithm_prefix("SHA-256: DEAD ") == "DEAD"
    assert strip_sha_algorithm_prefix("abc123") == "abc123"
    assert strip_sha_algorithm_prefix("") == ""
    # A colon after a >16-char token is NOT an algorithm prefix.
    long_prefix = "a" * 20 + ":rest"
    assert strip_sha_algorithm_prefix(long_prefix) == long_prefix


# ── single-file ──────────────────────────────────────────────────────────


async def test_ensure_model_downloads_and_verifies(tmp_path):
    payload = b"model-weights"
    src = tmp_path / "src.gguf"
    src.write_bytes(payload)
    root = tmp_path / "store"
    svc = ModelDownloadService(str(root))

    out = await svc.ensure_model_async("m1", _file_uri(src), _sha(payload), None, None)
    assert os.path.isfile(out)
    assert out.endswith(os.path.join("store", "m1.gguf"))
    with open(out, "rb") as fh:
        assert fh.read() == payload


async def test_ensure_model_sha_mismatch_deletes(tmp_path):
    src = tmp_path / "src.gguf"
    src.write_bytes(b"actual")
    svc = ModelDownloadService(str(tmp_path / "store"))
    with pytest.raises(RuntimeError) as ei:
        await svc.ensure_model_async("m1", _file_uri(src), "sha256:" + _sha(b"wrong"), None, None)
    assert "mismatch" in str(ei.value).lower()
    assert not os.path.isfile(os.path.join(str(tmp_path / "store"), "m1.gguf"))


async def test_ensure_model_cached_skip(tmp_path):
    payload = b"cached"
    src = tmp_path / "src.gguf"
    src.write_bytes(payload)
    root = tmp_path / "store"
    svc = ModelDownloadService(str(root))
    p1 = await svc.ensure_model_async("m1", _file_uri(src), _sha(payload), None, None)
    # Overwrite source; second call should keep the cached (valid) file.
    src.write_bytes(b"different")
    reports = []
    p2 = await svc.ensure_model_async("m1", _file_uri(src), _sha(payload), lambda p: reports.append(p), None)
    assert p1 == p2
    with open(p2, "rb") as fh:
        assert fh.read() == payload
    assert reports and reports[-1] == 1.0


async def test_ensure_model_no_sha_returns_existing(tmp_path):
    root = tmp_path / "store"
    svc = ModelDownloadService(str(root))
    # Pre-create the target.
    os.makedirs(root, exist_ok=True)
    target = root / "m1.gguf"
    target.write_bytes(b"exists")
    p = await svc.ensure_model_async("m1", "file:///ignored", None, None, None)
    assert p == str(target)


# ── bundle ───────────────────────────────────────────────────────────────


class _MapFetcher(IFileFetcher):
    """Fetcher that serves bytes by file name, optionally failing the primary
    URL so the fallback path is exercised.
    """

    def __init__(self, by_name: dict, fail_primary: bool = False):
        self._by_name = by_name
        self._fail_primary = fail_primary
        self.calls: list[str] = []

    async def fetch_async(self, url, dest_path, progress=None, ct=None):
        self.calls.append(url)
        if self._fail_primary and "/api/v1/models/" in url:
            raise RuntimeError("primary down")
        # Recover the file name from the URL tail.
        name = url.rsplit("/", 1)[-1].split("?")[0]
        # Bundle primary URL puts the name in FilePath=...
        if "FilePath=" in url:
            name = url.split("FilePath=")[-1]
        data = None
        for k, v in self._by_name.items():
            if k == name or url.endswith(k):
                data = v
                break
        if data is None:
            raise RuntimeError(f"no bytes for {url}")
        d = os.path.dirname(dest_path)
        if d:
            os.makedirs(d, exist_ok=True)
        with open(dest_path, "wb") as fh:
            fh.write(data)
        if progress:
            progress(1.0)


async def test_ensure_bundle_downloads_all_files(tmp_path):
    cfg = b'{"a":1}'
    weight = b"WEIGHTS"
    fetcher = _MapFetcher({"config.json": cfg, "model.mnn": weight})
    svc = ModelDownloadService(str(tmp_path / "store"), fetcher)
    spec = [
        BundleFileSpec("config.json", _sha(cfg), len(cfg)),
        BundleFileSpec("model.mnn", "sha256:" + _sha(weight), len(weight)),
    ]
    reports = []
    model_dir = await svc.ensure_bundle_async("q", "MNN/Q-MNN", spec, lambda p: reports.append(p), None)
    assert os.path.isfile(os.path.join(model_dir, "config.json"))
    assert os.path.isfile(os.path.join(model_dir, "model.mnn"))
    assert reports[-1] == 1.0


async def test_ensure_bundle_falls_back_to_cdn(tmp_path):
    weight = b"W"
    fetcher = _MapFetcher({"model.mnn": weight}, fail_primary=True)
    svc = ModelDownloadService(str(tmp_path / "store"), fetcher)
    spec = [BundleFileSpec("model.mnn", _sha(weight), len(weight))]
    model_dir = await svc.ensure_bundle_async("q", "org/repo", spec, None, None)
    assert os.path.isfile(os.path.join(model_dir, "model.mnn"))
    # Primary attempted (raised), then fallback attempted.
    assert any("/api/v1/models/" in u for u in fetcher.calls)
    assert any("/resolve/master/" in u for u in fetcher.calls)


async def test_ensure_bundle_skips_valid_cached(tmp_path):
    weight = b"W"
    fetcher = _MapFetcher({"model.mnn": weight})
    svc = ModelDownloadService(str(tmp_path / "store"), fetcher)
    spec = [BundleFileSpec("model.mnn", _sha(weight), len(weight))]
    await svc.ensure_bundle_async("q", "org/repo", spec, None, None)
    calls_after_first = len(fetcher.calls)
    await svc.ensure_bundle_async("q", "org/repo", spec, None, None)
    # No new fetches — cached file with matching SHA is kept.
    assert len(fetcher.calls) == calls_after_first


async def test_ensure_bundle_sha_mismatch_raises(tmp_path):
    fetcher = _MapFetcher({"model.mnn": b"actual"})
    svc = ModelDownloadService(str(tmp_path / "store"), fetcher)
    spec = [BundleFileSpec("model.mnn", _sha(b"expected-different"), 6)]
    with pytest.raises(RuntimeError):
        await svc.ensure_bundle_async("q", "org/repo", spec, None, None)


async def test_write_installed_manifest(tmp_path):
    weight = b"W"
    fetcher = _MapFetcher({"model.mnn": weight})
    svc = ModelDownloadService(str(tmp_path / "store"), fetcher)
    spec = [BundleFileSpec("model.mnn", _sha(weight), len(weight))]
    model_dir = await svc.ensure_bundle_async("q", "org/repo", spec, None, None)
    await svc.write_installed_manifest_async(model_dir, "q", "2.0", "org/repo", spec)
    import json
    with open(os.path.join(model_dir, "installed.json")) as fh:
        m = json.load(fh)
    assert m["ModelId"] == "q"
    assert m["Version"] == "2.0"
    assert m["Repo"] == "org/repo"
    assert m["Files"][0]["Name"] == "model.mnn"


# ── validation + helpers ─────────────────────────────────────────────────


async def test_empty_model_id_raises(tmp_path):
    svc = ModelDownloadService(str(tmp_path))
    with pytest.raises(ValueError):
        await svc.ensure_model_async("", "file:///x", None, None, None)


async def test_bundle_requires_repo_and_nonempty(tmp_path):
    svc = ModelDownloadService(str(tmp_path))
    with pytest.raises(ValueError):
        await svc.ensure_bundle_async("q", "", [BundleFileSpec("a", "x")], None, None)
    with pytest.raises(ValueError):
        await svc.ensure_bundle_async("q", "org/r", [], None, None)


async def test_is_cached_and_delete(tmp_path):
    payload = b"m"
    src = tmp_path / "s.gguf"
    src.write_bytes(payload)
    svc = ModelDownloadService(str(tmp_path / "store"))
    assert await svc.is_model_cached_async("m1") is False
    await svc.ensure_model_async("m1", _file_uri(src), _sha(payload), None, None)
    assert await svc.is_model_cached_async("m1") is True
    await svc.delete_model_async("m1", None)
    assert await svc.is_model_cached_async("m1") is False


async def test_available_disk_space_positive(tmp_path):
    svc = ModelDownloadService(str(tmp_path))
    space = await svc.get_available_disk_space_bytes_async()
    assert space > 0


def test_ctor_requires_storage_dir():
    with pytest.raises(ValueError):
        ModelDownloadService("")
