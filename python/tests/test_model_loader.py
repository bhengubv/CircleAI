"""test_model_loader.py

Verifies CircleAI.Core.LocalModelLoader — single-file download via an injected
fetcher, SHA-256 verification (bare-hex and sha256: prefix), checksum-skip on
sha256:TBD, bundle steering, path resolution, and model_exists.
"""
from __future__ import annotations

import hashlib

import pytest

from circle_ai.core.model_loader import LocalModelLoader


def _mk(tmp_path, registry, fetch_map):
    def fetcher(url: str) -> bytes:
        return fetch_map[url]

    return LocalModelLoader(
        model_directory=str(tmp_path / "store"),
        registry=registry,
        downloader=fetcher,
    )


async def test_download_verifies_checksum_and_returns_path(tmp_path) -> None:
    payload = b"GGUF-bytes"
    checksum = hashlib.sha256(payload).hexdigest()
    reg = {
        "demo": {"FileName": "m.gguf", "PrimaryUrl": "u://p", "Checksum": "sha256:" + checksum},
        "Notes": "free text — must be skipped, not parsed as an entry",
    }
    loader = _mk(tmp_path, reg, {"u://p": payload})
    path = await loader.download_model_async("demo")
    assert path.endswith("m.gguf")
    assert loader.model_exists("demo") is True


async def test_bare_hex_checksum_accepted(tmp_path) -> None:
    payload = b"weights"
    checksum = hashlib.sha256(payload).hexdigest()  # no sha256: prefix
    reg = {"demo": {"FileName": "m.bin", "PrimaryUrl": "u://p", "Checksum": checksum}}
    loader = _mk(tmp_path, reg, {"u://p": payload})
    await loader.download_model_async("demo")
    assert loader.model_exists("demo") is True


async def test_bad_checksum_deletes_and_raises(tmp_path) -> None:
    reg = {"demo": {"FileName": "m.bin", "PrimaryUrl": "u://p", "Checksum": "sha256:" + "0" * 64}}
    loader = _mk(tmp_path, reg, {"u://p": b"content"})
    with pytest.raises(Exception):
        await loader.download_model_async("demo")
    assert loader.model_exists("demo") is False


async def test_tbd_checksum_skips_verification(tmp_path) -> None:
    reg = {"demo": {"FileName": "m.bin", "PrimaryUrl": "u://p", "Checksum": "sha256:TBD"}}
    loader = _mk(tmp_path, reg, {"u://p": b"content"})
    path = await loader.download_model_async("demo")
    assert path.endswith("m.bin")


async def test_fallback_url_used_when_primary_fails(tmp_path) -> None:
    payload = b"fallback-bytes"
    checksum = hashlib.sha256(payload).hexdigest()
    reg = {
        "demo": {
            "FileName": "m.bin",
            "PrimaryUrl": "u://primary",
            "FallbackUrl": "u://fallback",
            "Checksum": "sha256:" + checksum,
        }
    }

    def fetcher(url: str) -> bytes:
        if url == "u://primary":
            raise RuntimeError("primary down")
        return payload

    loader = LocalModelLoader(
        model_directory=str(tmp_path / "s"), registry=reg, downloader=fetcher
    )
    path = await loader.download_model_async("demo")
    assert path.endswith("m.bin")


async def test_unsupported_model_raises(tmp_path) -> None:
    loader = _mk(tmp_path, {}, {})
    with pytest.raises(ValueError):
        await loader.download_model_async("nope")


async def test_bundle_entry_steers_to_bundle_downloader(tmp_path) -> None:
    reg = {
        "bundled": {
            "Repo": "org/model",
            "BundleFiles": [{"Name": "llm.mnn.weight", "Sha256": "ab", "SizeBytes": 10}],
        }
    }
    loader = _mk(tmp_path, reg, {})
    with pytest.raises(RuntimeError) as ei:
        await loader.download_model_async("bundled")
    assert "bundle" in str(ei.value).lower()


async def test_get_model_path_bundle_uses_anchor(tmp_path) -> None:
    reg = {
        "bundled": {
            "Repo": "org/model",
            "BundleFiles": [{"Name": "llm.mnn.weight", "Sha256": "ab", "SizeBytes": 10}],
        }
    }
    loader = _mk(tmp_path, reg, {})
    p = loader.get_model_path("bundled")
    assert p.endswith("llm.mnn.weight")
    assert "bundled" in p


async def test_get_model_path_missing_raises(tmp_path) -> None:
    loader = _mk(tmp_path, {}, {})
    with pytest.raises(FileNotFoundError):
        loader.get_model_path("nope")


async def test_check_for_critical_update(tmp_path) -> None:
    loader = LocalModelLoader(
        model_directory=str(tmp_path / "s"),
        registry={},
        versions_fetcher=lambda: "v1.2.3 [CRITICAL] patch",
    )
    assert await loader.check_for_critical_update_async() is True

    loader2 = LocalModelLoader(
        model_directory=str(tmp_path / "s2"),
        registry={},
        versions_fetcher=lambda: "v1.2.3 routine",
    )
    assert await loader2.check_for_critical_update_async() is False


async def test_check_for_critical_update_swallows_errors(tmp_path) -> None:
    def boom() -> str:
        raise RuntimeError("network down")

    loader = LocalModelLoader(
        model_directory=str(tmp_path / "s"), registry={}, versions_fetcher=boom
    )
    assert await loader.check_for_critical_update_async() is False
