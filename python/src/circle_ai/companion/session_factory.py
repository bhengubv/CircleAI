# companion/session_factory.py
#
# Creates CompanionSession instances with all optional backing services resolved
# from a service provider. Ported from CircleAI.Companion
# (CompanionSessionFactory.cs) — the C# reference. Callers only need the factory
# — they never construct a CompanionSession directly.
#
# The C# factory pulls ~13 optional services out of the DI ``IServiceProvider``
# and hands them to ``CompanionSession``. Python has no ambient DI container, so
# the provider is modelled as an ``IServiceProvider`` seam: an object exposing
# ``get_service(key) -> value | None`` keyed by a stable string name (or a plain
# mapping). The factory resolves a rich display-name / preferred-language from an
# optional identity provider, then builds the session.
#
# Service keys (mirroring the C# ``GetService<T>()`` calls):
#   "ai"        -> IChatGenerator          (required to converse)
#   "episodic"  -> IEpisodicMemoryStore    (required to persist/recall)
#   "recall"    -> IRecall                 (optional; falls back to FusedRecall)
#   "graph"     -> IHippoRagStore          (optional; enriches the fallback recall)
#   "encoder"   -> CompanionMemoryEncoder  (optional; background graph fill)
#   "beliefs"   -> SelfBeliefStore         (optional; user-fact injection)
#   "embedder"  -> Embedder                (optional; associative recall)
#   "persona"   -> IPersonaStore           (optional; persona hints)
#   "affect"    -> IAffectStore            (optional; affect summary)
#   "goals"     -> IGoalStore              (optional; active goals)

from __future__ import annotations

import uuid
from abc import ABC, abstractmethod
from typing import Mapping, Optional, Protocol, runtime_checkable

from ..memory.recall import FusedRecall
from .companion_types import InterfaceKind
from .session import CompanionSession, CompanionSessionOptions


@runtime_checkable
class IServiceProvider(Protocol):
    """A minimal service-locator seam: ``get_service(key) -> value | None``.

    Stands in for the C# ``IServiceProvider``; a plain ``dict`` also works (the
    factory falls back to mapping lookup).
    """

    def get_service(self, key: str) -> Optional[object]: ...


def _resolve(provider: object, key: str) -> Optional[object]:
    """Resolve a service from an :class:`IServiceProvider` or a plain mapping."""
    if provider is None:
        return None
    get = getattr(provider, "get_service", None)
    if callable(get):
        return get(key)
    if isinstance(provider, Mapping):
        return provider.get(key)
    return None


class IIdentityProvider(ABC):
    """The factory's identity seam — mirrors ``CircleAI.Identity.IIdentityProvider``.

    Only the ``get_current_identity_async`` member the factory uses is modelled.
    The returned object is expected to expose ``display_name`` and
    ``preferred_language`` (e.g. a ``CircleIdentity``), or ``None`` if unresolved.
    """

    @abstractmethod
    async def get_current_identity_async(
        self, *, ct: Optional[object] = None
    ) -> Optional[object]: ...


class ICompanionSessionFactory(ABC):
    """Contract for creating per-identity, per-surface Companion sessions.

    Mirrors ``CircleAI.Companion.ICompanionSessionFactory``.
    """

    @abstractmethod
    async def create_async(
        self, identity_id: str, interface: InterfaceKind, *, ct: Optional[object] = None
    ) -> CompanionSession:
        """Create a new session for the given identity and interface surface."""
        ...


class CompanionSessionFactory(ICompanionSessionFactory):
    """Builds :class:`CompanionSession` instances from resolved services.

    Mirrors ``CircleAI.Companion.CompanionSessionFactory``.
    """

    __slots__ = ("_services", "_identity")

    def __init__(
        self, services: object, identity: Optional[IIdentityProvider] = None
    ) -> None:
        self._services = services
        self._identity = identity

    async def create_async(
        self, identity_id: str, interface: InterfaceKind, *, ct: Optional[object] = None
    ) -> CompanionSession:
        if identity_id is None or len(identity_id.strip()) == 0:
            raise ValueError("identity_id required")

        # Try to resolve a rich display name from the identity store.
        display_name = identity_id
        preferred_lang: Optional[str] = None
        if self._identity is not None:
            resolved = await self._identity.get_current_identity_async(ct=ct)
            if resolved is not None:
                display_name = getattr(resolved, "display_name", identity_id)
                preferred_lang = getattr(resolved, "preferred_language", None)

        generator = _resolve(self._services, "ai")
        episodic = _resolve(self._services, "episodic")
        recall = _resolve(self._services, "recall")
        graph = _resolve(self._services, "graph")
        encoder = _resolve(self._services, "encoder")
        beliefs = _resolve(self._services, "beliefs")
        embedder = _resolve(self._services, "embedder")

        if generator is None:
            raise ValueError(
                "an 'ai' (IChatGenerator) service must be registered to create a session"
            )
        if episodic is None:
            raise ValueError(
                "an 'episodic' (IEpisodicMemoryStore) service must be registered to create a session"
            )
        # The C# session builds recall from stores internally; here we accept a
        # registered IRecall or synthesise the default FusedRecall over the
        # episodic store (optionally enriched by a registered graph store).
        if recall is None:
            recall = FusedRecall(episodic, graph)  # type: ignore[arg-type]

        opts = CompanionSessionOptions(
            session_id=uuid.uuid4().hex,
            identity_id=identity_id,
            interface=interface,
            display_name=display_name or "",
            preferred_language=preferred_lang,
            encoder=encoder,  # type: ignore[arg-type]
            beliefs=beliefs,  # type: ignore[arg-type]
            embedder=embedder,  # type: ignore[arg-type]
        )
        return CompanionSession(generator, episodic, recall, opts)  # type: ignore[arg-type]


__all__ = [
    "IServiceProvider",
    "IIdentityProvider",
    "ICompanionSessionFactory",
    "CompanionSessionFactory",
]
