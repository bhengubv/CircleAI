"""test_payfast_board.py — CircleAI.Commerce.Integration.PayFast port.

Covers the domain records, InMemoryPayFastBoard signature builder, ITN merchant
validation, and the newest-first webhook recorder, plus the static
CommerceIntegrationPayFastDomainContext. C# is the exact spec.

The signature vectors below were captured from the real .NET implementation
(WebUtility.UrlEncode + MD5) and MUST match byte-for-byte — they lock cross-
language wire parity for the PayFast gateway signature.
"""
from __future__ import annotations

from decimal import Decimal

import pytest

from circle_ai import (
    CommerceIntegrationPayFastDomainContext,
    InMemoryPayFastBoard,
    IPayFastBoard,
    PayFastConfig,
    PayFastItnPayload,
)
from circle_ai.commerce_integration_payfast.payfast_primitives import _url_encode


def _board(passphrase: str = "") -> InMemoryPayFastBoard:
    return InMemoryPayFastBoard(PayFastConfig("10000100", "46f0cd694581a", passphrase, True))


def test_board_is_ipayfastboard():
    assert isinstance(_board(), IPayFastBoard)


def test_config_none_raises():
    with pytest.raises(ValueError):
        InMemoryPayFastBoard(None)  # type: ignore[arg-type]


def test_url_encode_matches_dotnet_webutility():
    # Ground truth from .NET WebUtility.UrlEncode(...).Replace("%20","+").
    probe = "AZaz09 -_.!*()~ +%&=@:/?#[],'\""
    assert _url_encode(probe) == "AZaz09+-_.!*()%7E+%2B%25%26%3D%40%3A%2F%3F%23%5B%5D%2C%27%22"
    # UTF-8 multibyte characters are percent-encoded per UTF-8 byte, uppercase.
    assert _url_encode("Rcafé ü") == "Rcaf%C3%A9+%C3%BC"


def test_signature_no_passphrase_matches_dotnet():
    fields = {
        "merchant_id": "10000100",
        "merchant_key": "46f0cd694581a",
        "amount": "100.00",
        "item_name": "Test Item",
    }
    assert _board("").signature_for(fields) == "7abbb23afc89fb75f1412d1f9e5bf7bc"


def test_signature_with_passphrase_matches_dotnet():
    fields = {
        "merchant_id": "10000100",
        "merchant_key": "46f0cd694581a",
        "amount": "100.00",
        "item_name": "Test Item",
    }
    assert _board("mySecret 123").signature_for(fields) == "6005c346f28243d22d6bb609e9c917d9"


def test_signature_encodes_special_field_values_matches_dotnet():
    fields = {"name_first": "Jan & Co", "return_url": "https://x.test/ok?a=1"}
    assert _board("pp").signature_for(fields) == "e41013e51d602019c972824763862a0f"


def test_signature_single_field_no_passphrase_trims_trailing_amp():
    assert _board("").signature_for({"a": "b"}) == "7acaac15494e6820b1ed6d8b539af089"


def test_signature_none_raises():
    with pytest.raises(ValueError):
        _board().signature_for(None)  # type: ignore[arg-type]


def test_signature_is_lowercase_hex_32():
    sig = _board("pp").signature_for({"a": "b"})
    assert len(sig) == 32 and sig == sig.lower() and set(sig) <= set("0123456789abcdef")


def test_verify_itn_checks_merchant_id():
    board = _board()
    ok = PayFastItnPayload("10000100", "PID", "COMPLETE", Decimal("100.00"), "MP1", "sig")
    bad = PayFastItnPayload("99999999", "PID", "COMPLETE", Decimal("100.00"), "MP1", "sig")
    assert board.verify_itn(ok) is True
    assert board.verify_itn(bad) is False


def test_verify_itn_none_raises():
    with pytest.raises(ValueError):
        _board().verify_itn(None)  # type: ignore[arg-type]


def test_record_and_recent_webhooks_newest_first():
    board = _board()
    for i in range(3):
        board.record_webhook(
            PayFastItnPayload("10000100", f"PID{i}", "COMPLETE", Decimal("1.00"), f"MP{i}", "s")
        )
    recent = board.recent_webhooks()
    assert [w.payment_id for w in recent] == ["PID2", "PID1", "PID0"]
    assert [w.payment_id for w in board.recent_webhooks(limit=2)] == ["PID2", "PID1"]


def test_record_webhook_none_raises():
    with pytest.raises(ValueError):
        _board().record_webhook(None)  # type: ignore[arg-type]


def test_payfast_domain_context():
    ctx = CommerceIntegrationPayFastDomainContext
    assert ctx.SystemPromptSnippet.startswith("[DOMAIN: Commerce.Integration.PayFast]")
    assert "ITN" in ctx.SystemPromptSnippet
    assert list(ctx.ComplianceFlags) == ["PCI_DSS", "POPIA", "PASA", "Consumer_Protection_Act"]
    assert list(ctx.SuggestedTools) == ["payfast_api", "webhook_debugger", "document_editor"]
