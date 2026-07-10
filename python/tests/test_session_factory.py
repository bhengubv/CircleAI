"""test_session_factory.py

Verifies CompanionSessionFactory ported from CircleAI.Companion
(CompanionSessionFactory.cs): resolving backing services from an IServiceProvider
seam, defaulting recall to FusedRecall over the episodic store, folding an
optional identity provider's display name / preferred language into the session,
and failing clearly when the required services are absent.
"""
from __future__ import annotations

from typing import AsyncGenerator, Optional

import pytest

from circle_ai.companion.companion_types import InterfaceKind
from circle_ai.companion.session import CompanionSession
from circle_ai.companion.session_factory import (
    CompanionSessionFactory,
    ICompanionSessionFactory,
    IIdentityProvider,
)
from circle_ai.memory.in_memory_episodic_store import InMemoryEpisodicStore
from circle_ai.memory.recall import FusedRecall


class FakeGenerator:
    async def generate_async(self, messages, options=None) -> str:
        return "hi"

    async def stream_async(self, messages, options=None) -> AsyncGenerator[str, None]:
        yield "hi"


class FakeIdentity:
    def __init__(self, display_name: str, preferred_language: Optional[str]) -> None:
        self.display_name = display_name
        self.preferred_language = preferred_language


class FakeIdentityProvider(IIdentityProvider):
    def __init__(self, identity) -> None:
        self._identity = identity

    async def get_current_identity_async(self, *, ct=None):
        return self._identity


def _provider(**services) -> dict:
    return dict(services)


# ── construction ────────────────────────────────────────────────────────────


async def test_factory_builds_session_with_defaulted_recall() -> None:
    episodic = InMemoryEpisodicStore()
    factory = CompanionSessionFactory(_provider(ai=FakeGenerator(), episodic=episodic))
    assert isinstance(factory, ICompanionSessionFactory)
    session = await factory.create_async("u1", InterfaceKind.MOBILE)
    assert isinstance(session, CompanionSession)
    assert session.identity_id == "u1"
    assert session.interface == InterfaceKind.MOBILE
    # A fresh session id was minted.
    assert len(session.session_id) > 0


async def test_factory_uses_registered_recall() -> None:
    episodic = InMemoryEpisodicStore()
    recall = FusedRecall(episodic, None)
    factory = CompanionSessionFactory(
        _provider(ai=FakeGenerator(), episodic=episodic, recall=recall)
    )
    session = await factory.create_async("u1", InterfaceKind.WEB)
    # The session works end-to-end with the injected recall.
    reply = await session.send_async("hello")
    assert reply == "hi"


async def test_factory_folds_identity_display_name() -> None:
    episodic = InMemoryEpisodicStore()
    identity = FakeIdentityProvider(FakeIdentity("Ada Lovelace", "en"))
    factory = CompanionSessionFactory(
        _provider(ai=FakeGenerator(), episodic=episodic), identity=identity
    )
    session = await factory.create_async("u1", InterfaceKind.DESKTOP)
    ctx = session.get_context()
    assert ctx.display_name == "Ada Lovelace"
    assert ctx.preferred_language == "en"


async def test_factory_defaults_display_name_to_identity_id() -> None:
    episodic = InMemoryEpisodicStore()
    factory = CompanionSessionFactory(_provider(ai=FakeGenerator(), episodic=episodic))
    session = await factory.create_async("user-42", InterfaceKind.MOBILE)
    assert session.get_context().display_name == "user-42"


async def test_factory_accepts_service_provider_object() -> None:
    class Provider:
        def __init__(self, **svcs) -> None:
            self._svcs = svcs

        def get_service(self, key: str):
            return self._svcs.get(key)

    episodic = InMemoryEpisodicStore()
    factory = CompanionSessionFactory(Provider(ai=FakeGenerator(), episodic=episodic))
    session = await factory.create_async("u1", InterfaceKind.AMBIENT)
    assert isinstance(session, CompanionSession)


# ── failure modes ───────────────────────────────────────────────────────────


async def test_factory_requires_generator() -> None:
    episodic = InMemoryEpisodicStore()
    factory = CompanionSessionFactory(_provider(episodic=episodic))
    with pytest.raises(ValueError):
        await factory.create_async("u1", InterfaceKind.MOBILE)


async def test_factory_requires_episodic() -> None:
    factory = CompanionSessionFactory(_provider(ai=FakeGenerator()))
    with pytest.raises(ValueError):
        await factory.create_async("u1", InterfaceKind.MOBILE)


async def test_factory_rejects_blank_identity() -> None:
    episodic = InMemoryEpisodicStore()
    factory = CompanionSessionFactory(_provider(ai=FakeGenerator(), episodic=episodic))
    with pytest.raises(ValueError):
        await factory.create_async("  ", InterfaceKind.MOBILE)
