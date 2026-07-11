# payfast_primitives.py
#
# Port of CircleAI.Commerce.Integration.PayFast PayFastPrimitives.cs
# (C# — the EXACT spec).
#
# (3.3.0) PayFast integration primitives — real signature builder, real ITN
# validation params, in-memory webhook recorder. The HTTP-side callbacks are
# wired by the host.
#
# The signature must be byte-for-byte identical to the C# implementation, which
# uses `WebUtility.UrlEncode(value).Replace("%20","+")` per field. .NET's
# WebUtility.UrlEncode:
#   * leaves unescaped:  A-Z a-z 0-9  and  - _ . ! * ( )
#   * encodes space as   +
#   * percent-encodes every other byte of the UTF-8 encoding as UPPERCASE hex.
# The trailing `.Replace("%20","+")` is a no-op safety net (space is already
# "+"), preserved here for fidelity. The concatenated field string is MD5-hashed
# and returned as lowercase hex. RecentWebhooks returns newest-first.

from __future__ import annotations

import hashlib
import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from decimal import Decimal
from typing import List, Mapping


@dataclass(frozen=True, slots=True)
class PayFastConfig:
    """Mirrors ``CircleAI.Commerce.Integration.PayFast.PayFastConfig`` —
    ``record(string MerchantId, string MerchantKey, string Passphrase,
    bool Sandbox)``.
    """

    merchant_id: str
    merchant_key: str
    passphrase: str
    sandbox: bool


@dataclass(frozen=True, slots=True)
class PayFastItnPayload:
    """Mirrors ``CircleAI.Commerce.Integration.PayFast.PayFastItnPayload`` —
    ``record(string MerchantId, string PaymentId, string PaymentStatus,
    decimal Amount, string MPaymentId, string Signature)``.
    """

    merchant_id: str
    payment_id: str
    payment_status: str
    amount: Decimal
    m_payment_id: str
    signature: str


# Characters .NET WebUtility.UrlEncode leaves unescaped (besides A-Za-z0-9).
_URL_SAFE_PUNCT = frozenset("-_.!*()")


def _url_encode(value: str) -> str:
    """Replicate ``WebUtility.UrlEncode(value).Replace("%20","+")``."""
    out: List[str] = []
    for byte in value.encode("utf-8"):
        ch = chr(byte)
        if ("a" <= ch <= "z") or ("A" <= ch <= "Z") or ("0" <= ch <= "9") or ch in _URL_SAFE_PUNCT:
            out.append(ch)
        elif ch == " ":
            out.append("+")
        else:
            out.append("%" + format(byte, "02X"))
    return "".join(out)


class IPayFastBoard(ABC):
    """PayFast signature + ITN board."""

    @property
    @abstractmethod
    def config(self) -> PayFastConfig:
        ...

    @abstractmethod
    def signature_for(self, ordered_fields: Mapping[str, str]) -> str:
        ...

    @abstractmethod
    def verify_itn(self, p: PayFastItnPayload) -> bool:
        ...

    @abstractmethod
    def record_webhook(self, p: PayFastItnPayload) -> None:
        ...

    @abstractmethod
    def recent_webhooks(self, limit: int = 20) -> List[PayFastItnPayload]:
        ...


class InMemoryPayFastBoard(IPayFastBoard):
    """Thread-safe in-memory :class:`IPayFastBoard`."""

    def __init__(self, cfg: PayFastConfig) -> None:
        if cfg is None:
            raise ValueError("config must not be None")
        self._config = cfg
        self._webhooks: List[PayFastItnPayload] = []
        self._lock = threading.Lock()

    @property
    def config(self) -> PayFastConfig:
        return self._config

    def signature_for(self, ordered_fields: Mapping[str, str]) -> str:
        if ordered_fields is None:
            raise ValueError("orderedFields must not be None")
        parts: List[str] = []
        for key, value in ordered_fields.items():
            parts.append(f"{key}={_url_encode(value)}&")
        sb = "".join(parts)
        if self._config.passphrase:
            sb += "passphrase=" + _url_encode(self._config.passphrase)
        elif sb and sb[-1] == "&":
            sb = sb[:-1]
        return hashlib.md5(sb.encode("utf-8")).hexdigest()

    def verify_itn(self, p: PayFastItnPayload) -> bool:
        if p is None:
            raise ValueError("payload must not be None")
        return p.merchant_id == self._config.merchant_id

    def record_webhook(self, p: PayFastItnPayload) -> None:
        if p is None:
            raise ValueError("payload must not be None")
        with self._lock:
            self._webhooks.append(p)

    def recent_webhooks(self, limit: int = 20) -> List[PayFastItnPayload]:
        with self._lock:
            return list(reversed(self._webhooks))[:limit]
