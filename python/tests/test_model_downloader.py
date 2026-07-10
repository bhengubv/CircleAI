"""test_model_downloader.py

Verifies CircleAI.Core.ModelDownloader + ModelScopeSource + SourceDownloadHelper:
registry parsing (skip non-object metadata), candidate building, source
fallthrough, progress reporting, bundle steering, and disposal.
"""
from __future__ import annotations

import os

import pytest

from circle_ai.core.model_downloader import (
    DownloadProgressReport,
    ModelDownloader,
)
from circle_ai.core.model_source import IModelSource, ModelScopeSource


class _FakeSource(IModelSource):
    def __init__(self, name: str, *, fail: bool = False) -> None:
        self._name = name
        self._fail = fail
        self.calls: list[str] = []

    @property
    def name(self) -> str:
        return self._name

    async def is_available_async(self, ct: object = None) -> bool:
        return not self._fail

    async def download_async(self, url, local_path, progress=None, ct=None) -> None:
        self.calls.append(url)
        if self._fail:
            raise RuntimeError(f"{self._name} down")
        d = os.path.dirname(local_path)
        if d:
            os.makedirs(d, exist_ok=True)
        with open(local_path, "wb") as fh:
            fh.write(b"data-from-" + self._name.encode())
        if progress is not None:
            from circle_ai.core.model_source import DownloadProgress

            progress(DownloadProgress(file_name=os.path.basename(local_path),
                                      bytes_received=9, total_bytes=9))


def test_ctor_requires_at_least_one_source() -> None:
    with pytest.raises(ValueError):
        ModelDownloader([])


async def test_download_from_candidates_falls_through(tmp_path) -> None:
    s1 = _FakeSource("ModelScope", fail=True)
    s2 = _FakeSource("Mirror")
    dd = ModelDownloader([s1, s2])
    out = str(tmp_path / "f.bin")
    # URLs whose host contains the source name are matched to that source.
    winner = await dd.download_from_candidates_async(
        ["https://modelscope.cn/a", "https://mirror.example/b"], out
    )
    assert winner == "Mirror"
    assert os.path.isfile(out)


async def test_download_from_candidates_all_fail_raises(tmp_path) -> None:
    dd = ModelDownloader([_FakeSource("ModelScope", fail=True)])
    with pytest.raises(RuntimeError) as ei:
        await dd.download_from_candidates_async(["https://modelscope.cn/a"], str(tmp_path / "f"))
    assert "All model sources failed" in str(ei.value)


async def test_download_model_uses_registry_and_reports_progress(tmp_path) -> None:
    reg = {
        "demo": {"FileName": "m.bin", "PrimaryUrl": "https://modelscope.cn/m.bin", "Version": "1"},
        "$schema": "http://example/schema",  # non-object-ish metadata → skipped
    }
    src = _FakeSource("ModelScope")
    dd = ModelDownloader([src], registry=reg)
    reports: list[DownloadProgressReport] = []
    dd.add_progress_handler(lambda r: reports.append(r))
    outdir = str(tmp_path / "out")
    await dd.download_model_async("demo", outdir)
    assert os.path.isfile(os.path.join(outdir, "m.bin"))
    assert len(reports) >= 1
    assert reports[0].bytes_received == 9


async def test_download_model_unknown_id_raises(tmp_path) -> None:
    dd = ModelDownloader([_FakeSource("ModelScope")], registry={})
    with pytest.raises(KeyError):
        await dd.download_model_async("nope", str(tmp_path / "o"))


async def test_download_model_bundle_steers(tmp_path) -> None:
    reg = {
        "bundled": {"Repo": "org/m", "BundleFiles": [{"Name": "llm.mnn.weight", "Sha256": "ab"}]}
    }
    dd = ModelDownloader([_FakeSource("ModelScope")], registry=reg)
    with pytest.raises(RuntimeError) as ei:
        await dd.download_model_async("bundled", str(tmp_path / "o"))
    assert "bundle" in str(ei.value).lower()


async def test_download_model_no_urls_raises(tmp_path) -> None:
    reg = {"demo": {"FileName": "m.bin"}}  # no PrimaryUrl / FallbackUrl
    dd = ModelDownloader([_FakeSource("ModelScope")], registry=reg)
    with pytest.raises(RuntimeError) as ei:
        await dd.download_model_async("demo", str(tmp_path / "o"))
    assert "no PrimaryUrl or FallbackUrl" in str(ei.value)


async def test_registry_from_path(tmp_path) -> None:
    import json

    p = tmp_path / "registry.json"
    p.write_text(json.dumps({"demo": {"FileName": "m.bin", "PrimaryUrl": "https://modelscope.cn/x"}}))
    dd = ModelDownloader([_FakeSource("ModelScope")], registry_path=str(p))
    await dd.download_model_async("demo", str(tmp_path / "o"))
    assert os.path.isfile(str(tmp_path / "o" / "m.bin"))


# ── ModelScopeSource host guard + file fetch ─────────────────────────────────


async def test_modelscope_rejects_non_modelscope_http_url(tmp_path) -> None:
    src = ModelScopeSource()
    with pytest.raises(ValueError):
        await src.download_async("https://evil.example/x", str(tmp_path / "f"))


async def test_modelscope_downloads_via_file_fetcher(tmp_path) -> None:
    payload = b"local-model-bytes"
    srcfile = tmp_path / "model.gguf"
    srcfile.write_bytes(payload)
    url = srcfile.as_uri()  # file:// URL
    reports = []
    src = ModelScopeSource()
    out = str(tmp_path / "copied.gguf")
    await src.download_async(url, out, progress=lambda p: reports.append(p))
    assert os.path.isfile(out)
    with open(out, "rb") as fh:
        assert fh.read() == payload
    assert len(reports) >= 1


async def test_modelscope_disposed_download_raises(tmp_path) -> None:
    src = ModelScopeSource()
    src.dispose()
    with pytest.raises(RuntimeError):
        await src.download_async("file:///x", str(tmp_path / "f"))
