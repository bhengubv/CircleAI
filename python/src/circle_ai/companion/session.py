# companion/session.py
#
# The conscious loop: a concrete companion session that recalls from fused
# memory, persists each turn, and encodes it into the graph off the hot path.
# Ported from CircleAI.Companion (CompanionSession) — the C# reference — and
# mirrors the TypeScript pilot (companion/session.ts) and Go port
# (companion_session.go).
#
# On every turn it (1) recalls the most relevant memories + the user's own facts
# and injects them into the system prompt, (2) calls the generator, (3) persists
# the exchange to episodic memory, and (4) hands it to the background encoder so
# the knowledge graph fills for future associative recall.

from __future__ import annotations

import uuid
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import AsyncGenerator, Awaitable, Callable, Optional

from ..inference.inference import IChatGenerator
from ..models.models import ChatMessage
from ..memory.episodic_memory import EpisodicMemoryEntry
from ..memory.graph import MemoryHit
from ..memory.recall import IRecall
from ..memory.stores import IEpisodicMemoryStore
from .belief import SelfBeliefStore
from .companion_types import (
    CompanionContext,
    CompanionProactiveEvent,
    CompanionTurn,
    InterfaceKind,
)
from .memory_encoder import CompanionMemoryEncoder


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


# An embedder computes an embedding for the given text, or returns ``None`` when
# no embedding is available (-> episodic recency recall). Optional.
Embedder = Callable[[str], Awaitable[Optional[list[float]]]]

# A handler invoked when the companion proactively initiates contact.
ProactiveMessageHandler = Callable[[CompanionProactiveEvent], None]


@dataclass
class CompanionSessionOptions:
    """Construction-time configuration for a :class:`CompanionSession`."""

    session_id: str
    identity_id: str
    interface: InterfaceKind
    display_name: str = ""
    preferred_language: Optional[str] = None
    # Static persona hint block prepended to the system prompt.
    persona_hints: str = ""
    # Static affect hint block prepended to the system prompt.
    affect_summary: str = ""
    active_goals: list[str] = field(default_factory=list)
    # How many memories to recall per turn. Default 5.
    recall_top_k: int = 5
    # Optional app context stamped onto persisted episodes.
    app_context: Optional[str] = None
    # Background graph/belief encoder. When None, turns are not encoded.
    encoder: Optional[CompanionMemoryEncoder] = None
    # The user's own facts, surfaced into the system prompt.
    beliefs: Optional[SelfBeliefStore] = None
    # Optional embedder for associative episodic recall; None -> recency recall.
    embedder: Optional[Embedder] = None


@dataclass
class _PreparedTurn:
    messages: list[ChatMessage]
    query_embedding: Optional[list[float]]
    snippets: list[str]


class CompanionSession:
    """A companion session that thinks with fused memory and remembers what it learns."""

    def __init__(
        self,
        generator: IChatGenerator,
        episodic: IEpisodicMemoryStore,
        recall: IRecall,
        opts: CompanionSessionOptions,
    ) -> None:
        if generator is None:
            raise ValueError("generator required")
        if episodic is None:
            raise ValueError("episodic required")
        if recall is None:
            raise ValueError("recall required")
        self._generator = generator
        self._episodic = episodic
        self._recall = recall
        self._opts = opts
        self._history: list[CompanionTurn] = []
        self.on_proactive_message_ready: Optional[ProactiveMessageHandler] = None
        self._context = self._build_context([])

    # ── Identity ─────────────────────────────────────────────────────────────

    @property
    def session_id(self) -> str:
        return self._opts.session_id

    @property
    def identity_id(self) -> str:
        return self._opts.identity_id

    @property
    def interface(self) -> InterfaceKind:
        return self._opts.interface

    @property
    def history(self) -> list[CompanionTurn]:
        """In-session conversation history (not persisted)."""
        return list(self._history)

    # ── Core conversation ────────────────────────────────────────────────────

    async def send_async(self, message: str, *, ct: Optional[object] = None) -> str:
        """Send a message and receive a complete reply."""
        prepared = await self._prepare_async(message)
        reply = await self._generator.generate_async(prepared.messages)
        await self._record_turn_async(
            message, reply, prepared.query_embedding, prepared.snippets
        )
        return reply

    async def stream_async(
        self, message: str, *, ct: Optional[object] = None
    ) -> AsyncGenerator[str, None]:
        """Stream the companion's reply chunk-by-chunk; persist the full reply at the end."""
        prepared = await self._prepare_async(message)
        parts: list[str] = []
        async for chunk in self._generator.stream_async(prepared.messages):
            parts.append(chunk)
            yield chunk
        await self._record_turn_async(
            message, "".join(parts), prepared.query_embedding, prepared.snippets
        )

    async def agent_async(
        self, instruction: str, *, ct: Optional[object] = None
    ) -> str:
        """Agentic mode. Pilot: no tool-execution loop yet — falls back to a plain reply."""
        return await self.send_async(instruction)

    # ── Context ──────────────────────────────────────────────────────────────

    def get_context(self) -> CompanionContext:
        """Return the most recent :class:`CompanionContext` snapshot."""
        return self._context

    async def refresh_context_async(self, *, ct: Optional[object] = None) -> None:
        """Refresh the context from backing stores (recency recall)."""
        hits = await self._recall.recall_async("", None, self._recall_top_k())
        self._context = self._build_context([h.item.text for h in hits])

    # ── Feedback ─────────────────────────────────────────────────────────────

    async def signal_feedback_async(
        self, positive: bool, note: Optional[str] = None, *, ct: Optional[object] = None
    ) -> None:
        """Signal satisfaction with the last reply. Pilot: accepted, not yet routed."""
        return None

    # ── internals ────────────────────────────────────────────────────────────

    async def _prepare_async(self, message: str) -> _PreparedTurn:
        # Recall runs BEFORE the current turn is persisted, so it draws on prior
        # memory, never echoes the message back.
        query_embedding: Optional[list[float]] = None
        if self._opts.embedder is not None:
            query_embedding = await self._opts.embedder(message)

        hits = await self._recall.recall_async(
            message, query_embedding, self._recall_top_k()
        )
        snippets = [h.item.text for h in hits]

        messages: list[ChatMessage] = [
            ChatMessage(role="system", content=self._build_system_prompt(snippets))
        ]
        for turn in self._history:
            messages.append(ChatMessage(role=turn.role, content=turn.content))
        messages.append(ChatMessage(role="user", content=message))

        return _PreparedTurn(
            messages=messages, query_embedding=query_embedding, snippets=snippets
        )

    async def _record_turn_async(
        self,
        user_text: str,
        reply: str,
        query_embedding: Optional[list[float]],
        snippets: list[str],
    ) -> None:
        episode_id = uuid.uuid4()
        entry = EpisodicMemoryEntry(
            id=episode_id,
            recorded_at_utc=_utc_now(),
            user_text=user_text,
            assistant_text=reply,
            app_context=self._opts.app_context,
            embedding=query_embedding,
        )
        await self._episodic.add_async(entry)

        # Off the hot path: fill the graph + form attributed beliefs for next time.
        if self._opts.encoder is not None:
            self._opts.encoder.enqueue(user_text, reply, str(episode_id))

        now = _utc_now()
        self._history.append(
            CompanionTurn(role="user", content=user_text, timestamp=now)
        )
        self._history.append(
            CompanionTurn(role="assistant", content=reply, timestamp=now)
        )
        self._context = self._build_context(snippets)

    def _build_system_prompt(self, snippets: list[str]) -> str:
        parts: list[str] = []
        if self._opts.persona_hints and self._opts.persona_hints.strip():
            parts.append(self._opts.persona_hints.strip())
        if self._opts.affect_summary and self._opts.affect_summary.strip():
            parts.append(self._opts.affect_summary.strip())

        facts = self._user_facts()
        if len(facts) > 0:
            parts.append(
                "[What you know about the user]\n"
                + "\n".join("- " + f for f in facts)
            )
        if len(snippets) > 0:
            parts.append(
                "[Relevant memories]\n" + "\n".join("- " + s for s in snippets)
            )
        return "\n\n".join(parts)

    def _user_facts(self) -> list[str]:
        if self._opts.beliefs is None:
            return []
        return [f.object for f in self._opts.beliefs.self_facts()]

    def _build_context(self, snippets: list[str]) -> CompanionContext:
        return CompanionContext(
            identity_id=self._opts.identity_id,
            display_name=self._opts.display_name or "",
            preferred_language=self._opts.preferred_language,
            interface=self._opts.interface,
            persona_hints=self._opts.persona_hints or "",
            affect_summary=self._opts.affect_summary or "",
            recent_memory_snippets=list(snippets),
            active_goals=list(self._opts.active_goals),
            context_built_at=_utc_now(),
        )

    def _recall_top_k(self) -> int:
        return self._opts.recall_top_k if self._opts.recall_top_k > 0 else 5
