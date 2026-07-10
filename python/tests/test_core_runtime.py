"""test_core_runtime.py

Verifies the smaller CircleAI.Core ports:
  * CircleEngine + ICircleModule + IEmbeddingService
  * ICircleAITenantContext / NullTenantContext / SingleTenantContext
  * ICircleAIAuditLog / NoopAuditLog / LoggerAuditLog / CircleAIAuditing
  * SafeModelHandle + PlatformInterop.load_model + HuggingFaceSource tombstone
"""
from __future__ import annotations

import logging
from datetime import datetime, timezone

import pytest

from circle_ai.core import (
    CircleAIAuditEntry,
    CircleAIAuditQuery,
    CircleAIAuditing,
    CircleEngine,
    HuggingFaceSource,
    IEmbeddingService,
    LoggerAuditLog,
    NoopAuditLog,
    NullTenantContext,
    SafeModelHandle,
    SingleTenantContext,
    default_shim,
    load_model,
    set_native_loader,
)
from circle_ai.core.model_loader import LocalModelLoader


# ── CircleEngine ─────────────────────────────────────────────────────────────


class _Emb(IEmbeddingService):
    @property
    def module_name(self) -> str:
        return "emb"

    async def init_async(self, engine) -> None:
        return None

    @property
    def is_model_loaded(self) -> bool:
        return True

    def generate_embedding(self, text: str):
        return [1.0, 2.0]

    @property
    def embedding_size(self) -> int:
        return 2


def _engine(tmp_path) -> CircleEngine:
    return CircleEngine(LocalModelLoader(model_directory=str(tmp_path / "m"), registry={}))


def test_engine_requires_loader() -> None:
    with pytest.raises(ValueError):
        CircleEngine(None)


def test_engine_register_get_has_module(tmp_path) -> None:
    eng = _engine(tmp_path)
    mod = _Emb()
    assert eng.register_module(mod, IEmbeddingService) is eng  # chainable
    assert eng.has_module(IEmbeddingService) is True
    assert eng.get_module(IEmbeddingService) is mod
    assert eng.get_module(_Emb) is None  # keyed by the registered type only


def test_engine_register_defaults_key_to_concrete_type(tmp_path) -> None:
    eng = _engine(tmp_path)
    mod = _Emb()
    eng.register_module(mod)
    assert eng.get_module(_Emb) is mod


def test_engine_model_loader_exposed(tmp_path) -> None:
    loader = LocalModelLoader(model_directory=str(tmp_path / "m"), registry={})
    eng = CircleEngine(loader)
    assert eng.model_loader is loader
    assert eng.embedding_service is None


# ── tenant context ───────────────────────────────────────────────────────────


def test_null_tenant_context_raises_and_has_no_tenant() -> None:
    ctx = NullTenantContext.instance()
    assert ctx.has_tenant is False
    with pytest.raises(RuntimeError):
        _ = ctx.current_tenant_id


def test_single_tenant_context() -> None:
    ctx = SingleTenantContext("acme")
    assert ctx.current_tenant_id == "acme"
    assert ctx.has_tenant is True


def test_single_tenant_rejects_blank() -> None:
    with pytest.raises(ValueError):
        SingleTenantContext("   ")


# ── audit log ────────────────────────────────────────────────────────────────


def _entry() -> CircleAIAuditEntry:
    return CircleAIAuditEntry(
        at=datetime(2026, 1, 1, tzinfo=timezone.utc),
        component="Comp",
        operation="Op",
        outcome="success",
        duration_ms=2.0,
    )


async def test_noop_audit_records_and_queries_empty() -> None:
    log = NoopAuditLog.instance()
    await log.record_async(_entry())
    got = [e async for e in log.query_async(CircleAIAuditQuery())]
    assert got == []


async def test_logger_audit_writes_info(caplog) -> None:
    logger = logging.getLogger("circleai.audit.test")
    log = LoggerAuditLog(logger)
    with caplog.at_level(logging.INFO, logger="circleai.audit.test"):
        await log.record_async(_entry())
    assert any("CircleAI audit Comp.Op success" in r.getMessage() for r in caplog.records)
    # Query is always empty for the logger sink.
    assert [e async for e in log.query_async(CircleAIAuditQuery())] == []


async def test_logger_audit_rejects_none_entry() -> None:
    with pytest.raises(ValueError):
        await LoggerAuditLog(logging.getLogger("x")).record_async(None)


def test_ambient_auditing_set_and_reset() -> None:
    assert isinstance(CircleAIAuditing.default(), NoopAuditLog)
    logger_sink = LoggerAuditLog(logging.getLogger("x"))
    CircleAIAuditing.set_default(logger_sink)
    assert CircleAIAuditing.default() is logger_sink
    CircleAIAuditing.reset_to_noop()
    assert isinstance(CircleAIAuditing.default(), NoopAuditLog)


def test_ambient_auditing_rejects_none() -> None:
    with pytest.raises(ValueError):
        CircleAIAuditing.set_default(None)


# ── SafeModelHandle + PlatformInterop ────────────────────────────────────────


def test_safe_handle_release_called_once() -> None:
    freed = []
    h = SafeModelHandle(0xABCD, lambda ptr: freed.append(ptr))
    assert h.is_invalid is False
    assert h.handle == 0xABCD
    h.dispose()
    h.dispose()  # idempotent
    assert freed == [0xABCD]
    assert h.is_invalid is True


def test_safe_handle_default_is_invalid_until_set() -> None:
    h = SafeModelHandle()
    assert h.is_invalid is True
    h.set_handle(0x10)
    freed = []
    h.with_release_callback(lambda p: freed.append(p))
    h.dispose()
    assert freed == [0x10]


def test_safe_handle_explicit_ctor_requires_callback() -> None:
    with pytest.raises(ValueError):
        SafeModelHandle(0x10, None)


def test_load_model_returns_live_handle(tmp_path) -> None:
    f = tmp_path / "m.gguf"
    f.write_bytes(b"gguf")
    h = load_model(str(f))
    assert h.is_invalid is False
    ptr = h.handle
    assert default_shim().is_live(ptr) is True
    h.dispose()
    # After dispose the shim no longer tracks the pointer as live, and the
    # handle reports invalid.
    assert default_shim().is_live(ptr) is False
    assert h.is_invalid is True


def test_load_model_validation(tmp_path) -> None:
    with pytest.raises(ValueError):
        load_model("")
    with pytest.raises(FileNotFoundError):
        load_model(str(tmp_path / "missing.gguf"))


def test_load_model_null_pointer_raises(tmp_path) -> None:
    f = tmp_path / "m.gguf"
    f.write_bytes(b"gguf")
    set_native_loader(lambda path: (0, lambda p: None))
    try:
        with pytest.raises(RuntimeError):
            load_model(str(f))
    finally:
        set_native_loader(None)  # restore default shim


def test_huggingface_source_is_a_tombstone() -> None:
    with pytest.raises(RuntimeError):
        HuggingFaceSource()
