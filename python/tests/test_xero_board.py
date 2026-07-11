"""test_xero_board.py — CircleAI.Commerce.Integration.Xero port.

Covers the domain records, InMemoryXeroBoard (token store/get, expiry check with
the no-tokens rule, tenant tracking with dedup, newest-first webhook events) and
the static CommerceIntegrationXeroDomainContext. C# is the exact spec.
"""
from __future__ import annotations

from datetime import datetime, timedelta, timezone

import pytest

from circle_ai import (
    CommerceIntegrationXeroDomainContext,
    InMemoryXeroBoard,
    IXeroBoard,
    XeroTenant,
    XeroTokens,
    XeroWebhookEvent,
)

_T0 = datetime(2026, 1, 1, tzinfo=timezone.utc)


def _at(mins: int) -> datetime:
    return _T0 + timedelta(minutes=mins)


def _tokens(expires_min: int) -> XeroTokens:
    return XeroTokens("access", "refresh", _at(expires_min), "id")


def test_board_is_ixeroboard():
    assert isinstance(InMemoryXeroBoard(), IXeroBoard)


def test_store_and_get_tokens_upserts():
    board = InMemoryXeroBoard()
    assert board.get_tokens("u1") is None
    board.store_tokens("u1", _tokens(60))
    board.store_tokens("u1", XeroTokens("a2", "r2", _at(120), "i2"))
    got = board.get_tokens("u1")
    assert got is not None and got.access_token == "a2"


def test_store_tokens_none_raises():
    with pytest.raises(ValueError):
        InMemoryXeroBoard().store_tokens("u1", None)  # type: ignore[arg-type]


def test_tokens_expired_true_when_absent():
    assert InMemoryXeroBoard().tokens_expired("nobody", _at(0)) is True


def test_tokens_expired_boundary():
    board = InMemoryXeroBoard()
    board.store_tokens("u1", _tokens(60))  # expires at T0+60m
    assert board.tokens_expired("u1", _at(59)) is False
    assert board.tokens_expired("u1", _at(60)) is True  # now >= expiry
    assert board.tokens_expired("u1", _at(61)) is True


def test_add_tenant_dedups_by_tenant_id():
    board = InMemoryXeroBoard()
    board.add_tenant("u1", XeroTenant("t1", "Org One", "ORGANISATION"))
    board.add_tenant("u1", XeroTenant("t1", "Org One (dup)", "ORGANISATION"))
    board.add_tenant("u1", XeroTenant("t2", "Org Two", "ORGANISATION"))
    board.add_tenant("u2", XeroTenant("t1", "Other user's org", "ORGANISATION"))
    ids = [t.tenant_id for t in board.tenants_for("u1")]
    assert ids == ["t1", "t2"]  # dup ignored, insertion order preserved
    # First-write wins for the dedup: original name kept.
    assert board.tenants_for("u1")[0].tenant_name == "Org One"
    assert [t.tenant_id for t in board.tenants_for("u2")] == ["t1"]


def test_tenants_for_unknown_user_is_empty():
    assert InMemoryXeroBoard().tenants_for("nobody") == []


def test_add_tenant_none_raises():
    with pytest.raises(ValueError):
        InMemoryXeroBoard().add_tenant("u1", None)  # type: ignore[arg-type]


def test_recent_events_newest_first_with_limit():
    board = InMemoryXeroBoard()
    board.record_webhook(XeroWebhookEvent("t1", "INVOICE", "r0", _at(0)))
    board.record_webhook(XeroWebhookEvent("t1", "INVOICE", "r2", _at(20)))
    board.record_webhook(XeroWebhookEvent("t1", "CONTACT", "r1", _at(10)))
    recent = board.recent_events()
    assert [e.resource_id for e in recent] == ["r2", "r1", "r0"]
    assert [e.resource_id for e in board.recent_events(limit=2)] == ["r2", "r1"]


def test_record_webhook_none_raises():
    with pytest.raises(ValueError):
        InMemoryXeroBoard().record_webhook(None)  # type: ignore[arg-type]


def test_xero_domain_context():
    ctx = CommerceIntegrationXeroDomainContext
    assert ctx.SystemPromptSnippet.startswith("[DOMAIN: Commerce.Integration.Xero]")
    assert "chart of accounts" in ctx.SystemPromptSnippet
    assert list(ctx.ComplianceFlags) == ["SARS", "IFRS", "Xero_Data_Standards", "POPIA"]
    assert list(ctx.SuggestedTools) == ["xero_api", "spreadsheet", "document_editor"]
