"""API-key authentication.

Ports ``CircleAI.Inference.Server.Auth.AuthSchemes`` and ``ApiKeyAuthHandler``.
The C# handler is an ASP.NET ``AuthenticationHandler`` that reads request
headers; here the same logic is exposed as an in-memory
:meth:`ApiKeyAuthHandler.authenticate` that takes a header mapping and returns
an :class:`AuthenticateResult`. Constant-time key comparison guards against
timing-attack key discovery (``hmac.compare_digest`` == C#
``CryptographicOperations.FixedTimeEquals``).
"""
from __future__ import annotations

import hmac
from dataclasses import dataclass, field
from enum import IntEnum
from typing import Dict, List, Mapping, Optional

from .options import ApiKeyOptions

__all__ = [
    "AuthSchemes",
    "AuthOutcome",
    "AuthenticateResult",
    "ApiKeyAuthHandler",
]


class AuthSchemes:
    """Identifiers for the auth schemes the server registers. Mirrors ``AuthSchemes``."""

    API_KEY = "ApiKey"
    JWT = "Bearer"
    AUTHENTICATED_POLICY = "Authenticated"


class AuthOutcome(IntEnum):
    """Which of the three ASP.NET ``AuthenticateResult`` states occurred."""

    SUCCESS = 0
    NO_RESULT = 1
    FAIL = 2


@dataclass(frozen=True, slots=True)
class AuthenticateResult:
    """Outcome of an authentication attempt. Mirrors the ASP.NET
    ``AuthenticateResult`` trio: ``Success`` (with a principal), ``NoResult``
    (no credentials presented), ``Fail`` (bad credentials).
    """

    outcome: AuthOutcome
    principal_name: Optional[str] = None
    claims: Dict[str, str] = field(default_factory=dict)
    failure_message: Optional[str] = None

    @property
    def succeeded(self) -> bool:
        return self.outcome == AuthOutcome.SUCCESS

    @staticmethod
    def success(principal_name: str, claims: Dict[str, str]) -> "AuthenticateResult":
        return AuthenticateResult(AuthOutcome.SUCCESS, principal_name, dict(claims))

    @staticmethod
    def no_result() -> "AuthenticateResult":
        return AuthenticateResult(AuthOutcome.NO_RESULT)

    @staticmethod
    def fail(message: str) -> "AuthenticateResult":
        return AuthenticateResult(AuthOutcome.FAIL, failure_message=message)


class ApiKeyAuthHandler:
    """API-key authentication handler. Port of ``ApiKeyAuthHandler``.

    When ``ApiKeyOptions.enabled`` is ``False`` the handler succeeds with a
    synthetic anonymous principal (so dev environments need no keys). Otherwise
    it reads the configured header and constant-time-matches against the
    allow-list.
    """

    __slots__ = ("_options",)

    def __init__(self, options: ApiKeyOptions) -> None:
        if options is None:
            raise ValueError("options is required")
        self._options = options

    def authenticate(self, headers: Mapping[str, str]) -> AuthenticateResult:
        """Authenticate a request from its header mapping. Header lookup is
        case-insensitive (HTTP header semantics).
        """
        cfg = self._options

        if not cfg.enabled:
            return AuthenticateResult.success(
                "anonymous",
                {"scheme": AuthSchemes.API_KEY, "auth_disabled": "true"},
            )

        raw = _get_header(headers, cfg.header_name)
        if raw is None or not raw.strip():
            return AuthenticateResult.no_result()

        if not _try_match_key(raw, cfg.keys):
            return AuthenticateResult.fail("Invalid API key.")

        return AuthenticateResult.success(
            "api-key-caller", {"scheme": AuthSchemes.API_KEY}
        )


def _get_header(headers: Mapping[str, str], name: str) -> Optional[str]:
    if name in headers:
        return headers[name]
    lowered = name.lower()
    for k, v in headers.items():
        if k.lower() == lowered:
            return v
    return None


def _try_match_key(presented: str, allowed: List[str]) -> bool:
    """Constant-time match against any configured key. Mirrors the C#
    ``TryMatchKey``: skip empty keys, length-guard, then FixedTimeEquals.
    """
    if not allowed:
        return False
    presented_bytes = presented.encode("utf-8")
    for k in allowed:
        if not k:
            continue
        key_bytes = k.encode("utf-8")
        if len(key_bytes) != len(presented_bytes):
            continue
        if hmac.compare_digest(key_bytes, presented_bytes):
            return True
    return False
