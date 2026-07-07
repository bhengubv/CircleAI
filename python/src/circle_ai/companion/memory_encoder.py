# companion/memory_encoder.py
#
# Background writer: turn -> knowledge graph + attributed beliefs, off the hot
# path. Ported from CircleAI.Companion (CompanionMemoryEncoder) — the C#
# reference — and mirrors the TypeScript pilot (companion/memory_encoder.ts) and
# Go port (companion_memory_encoder.go).
#
# After each turn the session hands the exchange here and moves on; encoding
# happens on a background asyncio task so the reply is never delayed. A full queue
# drops rather than blocks (C#'s BoundedChannelFullMode.DropWrite).
#
# Async posture (matching the TS pilot's determinism, not Go's eager drain):
# asyncio is single-threaded and cooperative, so the drain task advances only at
# await points. ``enqueue`` is synchronous and uses ``Queue.put_nowait`` with a
# ``QueueFull`` catch to drop — so N synchronous enqueues with no await between
# them cannot have the drain free a slot mid-burst. That makes drop-on-full
# deterministic exactly like the JS microtask model, while still doing every
# encode off the caller's turn. ``close_async`` stops intake and awaits the drain.

from __future__ import annotations

import asyncio
from dataclasses import dataclass
from typing import Optional

from ..memory.extractor import IKnowledgeGraphExtractor
from ..memory.graph import InMemoryKnowledgeGraph, KnowledgeNode
from .belief import IBeliefExtractor, SelfBeliefStore


@dataclass(frozen=True)
class _EncodeJob:
    user_text: str
    assistant_text: str
    episode_id: str


class CompanionMemoryEncoder:
    """Background writer: turn -> knowledge graph, off the hot path."""

    def __init__(
        self,
        extractor: IKnowledgeGraphExtractor,
        graph: InMemoryKnowledgeGraph,
        belief_extractor: Optional[IBeliefExtractor] = None,
        beliefs: Optional[SelfBeliefStore] = None,
        capacity: int = 256,
    ) -> None:
        if extractor is None:
            raise ValueError("extractor required")
        if graph is None:
            raise ValueError("graph required")
        self._extractor = extractor
        self._graph = graph
        self._belief_extractor = belief_extractor
        self._beliefs = beliefs
        self._capacity = max(1, capacity)

        self._queue: asyncio.Queue[_EncodeJob] = asyncio.Queue(maxsize=self._capacity)
        self._closed = False
        # First error hit while draining, if any (diagnostics).
        self.last_error: Optional[BaseException] = None
        # The drain runs as a background task on the running loop.
        self._drain: asyncio.Task[None] = asyncio.ensure_future(self._drain_loop())

    def enqueue(self, user_text: str, assistant_text: str, episode_id: str) -> None:
        """Hand a turn to the encoder. Non-blocking; returns immediately."""
        if not episode_id or len(episode_id.strip()) == 0:
            return
        if self._closed:
            return
        job = _EncodeJob(
            user_text=user_text or "",
            assistant_text=assistant_text or "",
            episode_id=episode_id,
        )
        try:
            self._queue.put_nowait(job)
        except asyncio.QueueFull:
            # DropWrite: queue is full — never block a turn.
            return

    async def _drain_loop(self) -> None:
        while True:
            if self._queue.empty():
                if self._closed:
                    return
                # Yield so producers/consumers interleave; re-check on wake.
                await asyncio.sleep(0)
                continue

            job = self._queue.get_nowait()
            try:
                # Give the memory node a readable name so recall hands back the
                # actual exchange, not an opaque id.
                self._graph.upsert_node(
                    KnowledgeNode(
                        id=job.episode_id,
                        kind="memory",
                        name=job.user_text,
                        properties={},
                    )
                )

                triples = await self._extractor.extract_from_turn_async(
                    job.user_text, job.assistant_text, job.episode_id
                )
                for t in triples:
                    self._graph.add_triple(
                        t.subject, t.predicate, t.object, t.source, t.confidence
                    )

                # Form attributed beliefs from this turn — a third party's fact
                # never becomes the user's. Happens here, off the turn, at the
                # point the false belief would otherwise be created.
                if self._belief_extractor is not None and self._beliefs is not None:
                    for b in await self._belief_extractor.extract_async(
                        job.user_text, job.episode_id
                    ):
                        self._beliefs.record(b)
            except Exception as ex:  # noqa: BLE001 — capture, never crash the drain
                if self.last_error is None:
                    self.last_error = ex

    async def close_async(self) -> None:
        """Stop accepting work and wait for the queue to drain."""
        self._closed = True
        await self._drain

    # Async-context-manager sugar mirroring the C# IAsyncDisposable surface.
    async def aclose(self) -> None:
        await self.close_async()

    async def __aenter__(self) -> "CompanionMemoryEncoder":
        return self

    async def __aexit__(self, *exc: object) -> None:
        await self.close_async()
