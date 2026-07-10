# companion/inner_monologue.py
#
# IInnerMonologue implementations. Ported from CircleAI.Companion — the C#
# reference:
#
#   * ReasoningLoopInnerMonologue (ReasoningLoopInnerMonologue.cs)  — LLM-driven
#   * TemplateInnerMonologue      (HerJarvisRealImplementations.cs) — templated
#
# ReasoningLoopInnerMonologue is an o1 / DeepSeek-R1 style reasoning loop: it
# drives an ``IChatGenerator`` fragment stream and captures the reasoning-kind
# fragments as the inner monologue, falling back to the visible content when the
# generator surfaces no reasoning.
#
# TemplateInnerMonologue is the model-free predecessor: a narrative-template
# reflection over the context JSON.

from __future__ import annotations

import re
from datetime import datetime, timezone
from typing import List, Optional

from ..inference.inference import (
    GenerationOptions,
    IChatGenerator,
    stream_fragments_async,
)
from ..models.models import ChatFragmentKind, ChatMessage
from .herjarvis_contracts import IInnerMonologue, SelfReflection


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


# ======================================================================
# ReasoningLoopInnerMonologue — reasoning-capable LLM inner monologue.
# ======================================================================
class ReasoningLoopInnerMonologue(IInnerMonologue):
    """Inner-monologue powered by a reasoning-capable LLM.

    Mirrors ``CircleAI.Companion.ReasoningLoopInnerMonologue``.
    """

    _REASONING_SYSTEM_PROMPT = (
        "You are this user's inner monologue. Reason carefully before responding. "
        "Use <think>...</think> blocks for chain-of-thought. The visible answer "
        "afterwards should be short and reflective — not a solution, an observation."
    )

    __slots__ = ("_llm",)

    def __init__(self, llm: IChatGenerator) -> None:
        if llm is None:
            raise ValueError("llm required")
        self._llm = llm

    async def reflect_async(
        self, context_json: str, *, ct: Optional[object] = None
    ) -> SelfReflection:
        if context_json is None:
            raise ValueError("context_json required")

        messages = [
            ChatMessage("system", self._REASONING_SYSTEM_PROMPT),
            ChatMessage(
                "user",
                f"Context (raw JSON):\n{context_json}\n\nReflect on this in 2-3 sentences.",
            ),
        ]
        options = GenerationOptions(max_tokens=256, temperature=0.5, include_reasoning=True)

        reasoning: List[str] = []
        content: List[str] = []
        try:
            # StreamFragmentsAsync is a default interface method in C#; here it is
            # the module-level helper unless the generator surfaces its own
            # (reasoning-aware) stream_fragments_async — mirror that override.
            own = getattr(self._llm, "stream_fragments_async", None)
            if callable(own):
                frag_stream = own(messages, options)
            else:
                frag_stream = stream_fragments_async(self._llm, messages, options)
            async for frag in frag_stream:
                if frag.kind == ChatFragmentKind.REASONING:
                    reasoning.append(frag.text)
                else:
                    content.append(frag.text)
        except Exception:  # noqa: BLE001 — matches C#: swallow + fall back
            # C# logs to Debug and continues with whatever was captured.
            pass

        # Prefer the reasoning trace as the "thought"; fall back to visible content.
        thought = "".join(reasoning).strip() if reasoning else "".join(content).strip()
        if len(thought) == 0:
            thought = "(no inner state)"
        return SelfReflection(thought, _utc_now())


# ======================================================================
# TemplateInnerMonologue — narrative-template reflection over context.
# ======================================================================
class TemplateInnerMonologue(IInnerMonologue):
    """Model-free narrative-template inner monologue.

    Mirrors ``CircleAI.Companion.HerJarvis.TemplateInnerMonologue``.

    Frame selection: the C# reference keys the frame off
    ``string.GetHashCode()``, which is process-randomised in modern .NET and so
    is NOT stable across runs. This port uses a stable content hash instead — the
    same input always yields the same frame — which preserves the observable
    contract (a well-formed reflection using one of the three frames) while being
    strictly more deterministic than the reference.
    """

    _FRAMES = (
        "Observation: {summary}. Implication: this likely means {direction}.",
        "Looking at {summary}, the salient pattern is {direction}.",
        "Given {summary}, my next step is to {direction}.",
    )

    _CLEAN_RX = re.compile(r"[\{\}\[\]\"]")

    async def reflect_async(
        self, context_json: str, *, ct: Optional[object] = None
    ) -> SelfReflection:
        if context_json is None:
            raise ValueError("context_json required")
        summary = self._summarise(context_json)
        direction = self._infer_direction(context_json)
        seed = self._stable_hash(context_json) & 0x7FFFFFFF
        frame = self._FRAMES[seed % len(self._FRAMES)]
        thought = frame.replace("{summary}", summary).replace("{direction}", direction)
        return SelfReflection(thought, _utc_now())

    @classmethod
    def _summarise(cls, json_text: str) -> str:
        clean = cls._CLEAN_RX.sub(" ", json_text)
        words = [w for w in clean.split(" ") if len(w) > 0][:12]
        return " ".join(words)

    @staticmethod
    def _infer_direction(json_text: str) -> str:
        lowered = json_text.lower()
        if "error" in lowered:
            return "diagnose the failure first"
        if "goal" in lowered:
            return "advance toward the stated goal"
        if "user" in lowered:
            return "respond to the user"
        return "gather more context"

    @staticmethod
    def _stable_hash(text: str) -> int:
        """Deterministic 32-bit FNV-1a over the UTF-8 bytes.

        Stands in for .NET's process-randomised ``String.GetHashCode()`` so the
        chosen frame is reproducible across processes and test runs.
        """
        h = 0x811C9DC5
        for b in text.encode("utf-8"):
            h ^= b
            h = (h * 0x01000193) & 0xFFFFFFFF
        return h


__all__ = [
    "ReasoningLoopInnerMonologue",
    "TemplateInnerMonologue",
]
