"""Catalog signature verifier — port of CircleAI.Core.Models.ICatalogSignatureVerifier."""
from __future__ import annotations

from enum import IntEnum
from typing import Optional, Protocol, runtime_checkable


class CatalogSignatureResult(IntEnum):
    """Outcome of a catalog payload signature check."""

    VALID = 0
    INVALID = 1
    MISSING = 2
    NOT_CONFIGURED = 3  # fail-closed default


@runtime_checkable
class ICatalogSignatureVerifier(Protocol):
    """Verify a catalog payload against an embedded public key."""

    def verify(
        self, payload: bytes, signature_base64: Optional[str]
    ) -> CatalogSignatureResult:
        ...


class NullCatalogSignatureVerifier:
    """Default verifier — always returns NOT_CONFIGURED.

    The catalog client treats this as "do not apply fetched catalog, keep
    cached version" — fail-closed. Ships as the registered default until
    a real Ed25519 verifier with an embedded public key replaces it.
    """

    _instance: Optional["NullCatalogSignatureVerifier"] = None

    @classmethod
    def instance(cls) -> "NullCatalogSignatureVerifier":
        if cls._instance is None:
            cls._instance = cls()
        return cls._instance

    def verify(
        self, payload: bytes, signature_base64: Optional[str]
    ) -> CatalogSignatureResult:
        return CatalogSignatureResult.NOT_CONFIGURED
