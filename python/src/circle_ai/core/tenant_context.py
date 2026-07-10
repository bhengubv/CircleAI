# core/tenant_context.py
#
# Port of CircleAI.Core.MultiTenant:
#   • ICircleAITenantContext — ambient tenant context contract
#   • NullTenantContext      — default: throws on any read (fail-closed)
#   • SingleTenantContext    — explicit single-tenant context

from __future__ import annotations

from abc import ABC, abstractmethod
from typing import Optional


class ICircleAITenantContext(ABC):
    """Ambient tenant context. Implementations resolve the current tenant from
    whatever signal the host uses.

    The default registration is :class:`NullTenantContext` — a stub that raises
    on access. There is no safe default for "which tenant is this request for",
    and silently failing open is the kind of bug that causes cross-tenant data
    leaks. Consumers MUST register their own implementation before any
    multi-tenant code path executes.
    """

    @property
    @abstractmethod
    def current_tenant_id(self) -> str:
        """The tenant identifier for the current unit of work.

        :raises RuntimeError: no tenant is in scope — multi-tenant code paths
            must NEVER silently fall back to a default.
        """
        raise NotImplementedError

    @property
    @abstractmethod
    def has_tenant(self) -> bool:
        """True when a tenant is currently in scope."""
        raise NotImplementedError


class NullTenantContext(ICircleAITenantContext):
    """Default :class:`ICircleAITenantContext` — raises on any read.

    The raise is intentional: it makes "I forgot to wire tenant resolution" a
    load-time error rather than a silent data-leak at runtime.
    """

    _instance: Optional["NullTenantContext"] = None

    @classmethod
    def instance(cls) -> "NullTenantContext":
        """Shared singleton instance."""
        if cls._instance is None:
            cls._instance = cls()
        return cls._instance

    @property
    def current_tenant_id(self) -> str:
        raise RuntimeError(
            "No CircleAI tenant context is in scope. Register a concrete "
            "ICircleAITenantContext (e.g. SingleTenantContext, or your own "
            "ClaimsPrincipal-backed resolver) before using multi-tenant-aware "
            "components."
        )

    @property
    def has_tenant(self) -> bool:
        return False


class SingleTenantContext(ICircleAITenantContext):
    """Explicit single-tenant context. Returns a fixed tenant id for every read.

    Use this when the deployment genuinely has one tenant and the raising
    default would just be ceremony.
    """

    __slots__ = ("_tenant_id",)

    def __init__(self, tenant_id: str) -> None:
        if tenant_id is None or tenant_id.strip() == "":
            raise ValueError("tenant_id")
        self._tenant_id = tenant_id

    @property
    def current_tenant_id(self) -> str:
        return self._tenant_id

    @property
    def has_tenant(self) -> bool:
        return True
