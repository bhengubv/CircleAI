"""IAIService + AIService — ports of CircleAI.Hosting.IAIService / AIService.

The long-lived B! butler service. It owns a single :class:`IChatGenerator` for
the process lifetime so callers don't pay the model-load cost per request, and
exposes ask / chat / stream / tool / agentic entry points.

Port notes vs the C#:
  * The C# resolves a GGUF path via ``IModelLoader`` and constructs a native
    ``QwenTextGenerator``. Python has no native generator, so the generator is
    injected — either a ready :class:`IChatGenerator` (``generator=``) or a
    ``generator_factory`` callable. This matches the C# ``generatorFactory``
    overload and keeps the service fully in-memory / testable.
  * System-prompt enrichment (persona hints + affect + device context + RAG)
    is preserved. The C# skill-context branch is out of scope for this port
    (no ``ISkillStore`` in the Python tree) and is omitted — it was an optional
    no-op branch when ``SkillStore`` was ``None``.
  * Observer events fire with the same :class:`AIChatEvent` /
    :class:`AIStreamEvent` / :class:`AIToolEvent` payloads and are error-isolated.
  * Tool-call parsing mirrors Qwen3's ``<tool_call>…</tool_call>`` format.
"""
from __future__ import annotations

import asyncio
import json as _json
import time
import uuid
from abc import ABC, abstractmethod
from datetime import datetime, timedelta, timezone
from typing import AsyncGenerator, Callable, List, Optional, Sequence

from ..device.device_probe import (
    DefaultDeviceContext,
    DeviceProbe,
    DeviceTier,
    DeviceTierDefaults,
)
from ..inference.inference import GenerationOptions, generate_response_async
from ..memory.episodic_memory import EpisodicMemoryEntry
from ..memory.feedback_analyser import FeedbackAnalyser
from ..memory.feedback_signal import FeedbackPolarity, FeedbackSignal
from ..memory.persona_state import PersonaState
from ..memory.rag import RagContextBuilder
from ..models.models import ChatMessage, UpgradeInfo
from ..tools.tool_types import ToolInvocation, ToolResult
from .ai_observer import AIChatEvent, AIStreamEvent, AIToolEvent, IAIObserver
from .ai_options import AIOptions
from .neuron.resident_slot_manager import ResidentSlotManager
from .neuron.router import Organ, RouteContext

__all__ = ["IAIService", "AIService"]

_UTC = timezone.utc

# Tool call detection tags (Qwen3 native format).
_TOOL_CALL_OPEN = "<tool_call>"
_TOOL_CALL_CLOSE = "</tool_call>"


class IAIService(ABC):
    """Long-lived butler service contract. Mirrors ``IAIService``. Implements
    async-dispose via :meth:`dispose_async`.
    """

    @property
    @abstractmethod
    def is_ready(self) -> bool:
        """True once :meth:`start_async` has completed and the model is loaded."""
        ...

    @abstractmethod
    async def start_async(self, ct: object = None) -> None:
        """Resolve + load the model and optionally warm up. Idempotent."""
        ...

    @abstractmethod
    async def stop_async(self, ct: object = None) -> None:
        """Release the model handle and shut the service down."""
        ...

    @abstractmethod
    async def ask_async(self, question: str, ct: object = None) -> str:
        """Single-user-question convenience wrapper."""
        ...

    @abstractmethod
    async def chat_async(
        self,
        messages: Sequence[ChatMessage],
        options: Optional[GenerationOptions] = None,
        ct: object = None,
    ) -> str:
        """Generate a complete assistant reply for the conversation."""
        ...

    @abstractmethod
    def stream_async(
        self,
        messages: Sequence[ChatMessage],
        options: Optional[GenerationOptions] = None,
        ct: object = None,
    ) -> AsyncGenerator[str, None]:
        """Stream the assistant reply token-by-token."""
        ...

    @abstractmethod
    async def invoke_tool_async(
        self, invocation: ToolInvocation, ct: object = None
    ) -> ToolResult:
        """Route a tool invocation to the configured tool bridge."""
        ...

    @abstractmethod
    async def agentic_chat_async(
        self,
        prompt: str,
        options: Optional[GenerationOptions] = None,
        ct: object = None,
    ) -> str:
        """Agentic run: generate, detect tool calls, execute, re-prompt."""
        ...

    @abstractmethod
    async def submit_feedback_async(
        self, signal: FeedbackSignal, ct: object = None
    ) -> None:
        """Record a user feedback signal against a past response."""
        ...

    async def check_for_upgrades_async(self, ct: object = None) -> List[UpgradeInfo]:
        """Default: no upgrades. Mirrors the C# default-interface-method."""
        return []

    async def prewarm_async(self, ct: object = None) -> None:
        """Default: pre-warm by starting. Mirrors the C# default-interface-method."""
        await self.start_async(ct)

    async def save_session_async(self, path: str, ct: object = None) -> bool:
        """(RT-02) Default: no snapshot support. ``AIService`` overrides."""
        return False

    async def load_session_async(self, path: str, ct: object = None) -> bool:
        """(RT-02) Default: no snapshot support. ``AIService`` overrides."""
        return False

    async def dispose_async(self) -> None:
        """Default: stop. Mirrors ``IAsyncDisposable``."""
        await self.stop_async()


class AIService(IAIService):
    """Default :class:`IAIService`. Wraps a single injected
    :class:`IChatGenerator` and serves all callers from it. Mirrors ``AIService``.
    """

    __slots__ = (
        "_options",
        "_generator_factory",
        "_generator",
        "_resolved_device_tier",
        "_start_gate",
        "_started",
        "_disposed",
        "_persona_cache",
        "_rag_builder",
        "_slots",
        "_generalist_reserved_bytes",
    )

    def __init__(
        self,
        options: AIOptions,
        generator=None,
        generator_factory: Optional[Callable[[], object]] = None,
        resolved_device_tier: DeviceTier = DeviceTier.DESKTOP,
    ) -> None:
        if options is None:
            raise ValueError("options is required")
        if generator is None and generator_factory is None:
            raise ValueError("either generator or generator_factory is required")
        self._options = options
        self._generator_factory = generator_factory
        self._generator = generator  # may be pre-set; else built at start
        self._resolved_device_tier = resolved_device_tier
        self._start_gate = asyncio.Lock()
        self._started = False
        self._disposed = False
        self._persona_cache: Optional[PersonaState] = None
        self._rag_builder: Optional[RagContextBuilder] = None
        self._slots: Optional[ResidentSlotManager] = None
        self._generalist_reserved_bytes = 0

    @property
    def is_ready(self) -> bool:
        return self._started and self._generator is not None and not self._disposed

    @property
    def resolved_model_id(self) -> Optional[str]:
        """The generalist's model id — surfaced by ``NeuronNode.engine_label``."""
        return self._options.model_id

    # ── Lifecycle ──────────────────────────────────────────────────────────

    async def start_async(self, ct: object = None) -> None:
        self._throw_if_disposed()
        if self._started:
            return
        async with self._start_gate:
            if self._started:
                return

            if self._generator is None:
                if self._generator_factory is None:
                    raise RuntimeError(
                        "AIService has no generator and no generator_factory."
                    )
                generator = self._generator_factory()
                if generator is None:
                    raise RuntimeError("Generator factory returned None.")
                self._generator = generator

            # Fire the model-fetching observer event (auto_selected False —
            # Python injects the generator directly).
            model_id = self._options.model_id or ""
            await self._fire_observer(
                lambda o: o.on_model_fetching_async(model_id, False)
            )

            if self._options.warm_on_start:
                try:
                    await self._warm_up_async()
                except Exception:  # noqa: BLE001 - warmup failure is non-fatal
                    pass

            self._started = True
            await self._fire_observer(lambda o: o.on_started_async())

            if self._options.check_for_upgrades_on_start:
                upgrades = await self.check_for_upgrades_async()
                for u in upgrades:
                    await self._fire_observer(lambda o, up=u: o.on_upgrade_available_async(up))

    async def stop_async(self, ct: object = None) -> None:
        if self._disposed:
            return
        await self._try_save_persona()
        async with self._start_gate:
            if self._slots is not None:
                await self._slots.evict_specialist_async()
            gen = self._generator
            dispose = getattr(gen, "dispose_async", None) if gen is not None else None
            if dispose is not None:
                try:
                    await dispose()
                except Exception:  # noqa: BLE001
                    pass
            self._generator = None
            self._started = False
            self._persona_cache = None
            await self._fire_observer(lambda o: o.on_stopped_async())

    async def prewarm_async(self, ct: object = None) -> None:
        self._throw_if_disposed()
        if not self._started:
            await self.start_async(ct)
            return
        await self._warm_up_async()

    async def _warm_up_async(self) -> None:
        generator = self._generator
        if generator is None:
            return
        warm_messages = [
            ChatMessage("system", self._options.system_prompt),
            ChatMessage("user", "."),
        ]
        warm_options = GenerationOptions(max_tokens=1, temperature=0.0)
        await generator.generate_async(warm_messages, warm_options)

    # ── Single-turn inference ──────────────────────────────────────────────

    async def ask_async(self, question: str, ct: object = None) -> str:
        if question is None or not question.strip():
            raise ValueError("question is required")
        messages = [ChatMessage("user", question)]
        return await self.chat_async(messages, self._options.default_generation_options, ct)

    async def chat_async(
        self,
        messages: Sequence[ChatMessage],
        options: Optional[GenerationOptions] = None,
        ct: object = None,
    ) -> str:
        if messages is None:
            raise ValueError("messages is required")
        await self._ensure_started()

        user_query = _last_user_query(messages)
        has_image = _has_image(messages)
        # Neuron: generalist by default; a specialist may answer when a router is
        # configured. Byte-identical to the single-slot path when router is None.
        generator = await self._select_slot_async(user_query, has_image)

        prepared = await self._prepare_messages(messages, user_query)
        effective_options = options or self._options.default_generation_options

        correlation_id = uuid.uuid4()
        started = time.monotonic()
        response = await generator.generate_async(prepared, effective_options)
        elapsed = timedelta(seconds=time.monotonic() - started)

        # Store exchange in episodic memory (fire-and-forget with isolation).
        await self._try_store_episode(user_query, response)

        await self._fire_observer(
            lambda o: o.on_chat_completed_async(
                AIChatEvent(correlation_id, prepared, response, elapsed, datetime.now(_UTC))
            )
        )
        return response

    async def stream_async(
        self,
        messages: Sequence[ChatMessage],
        options: Optional[GenerationOptions] = None,
        ct: object = None,
    ) -> AsyncGenerator[str, None]:
        if messages is None:
            raise ValueError("messages is required")
        await self._ensure_started()

        user_query = _last_user_query(messages)
        has_image = _has_image(messages)
        # Neuron: generalist by default; a specialist may answer when a router is
        # configured. Byte-identical to the single-slot path when router is None.
        generator = await self._select_slot_async(user_query, has_image)

        prepared = await self._prepare_messages(messages, user_query)
        effective_options = options or self._options.default_generation_options

        correlation_id = uuid.uuid4()
        started = time.monotonic()
        token_count = 0
        first_token = True
        parts: List[str] = []

        async for piece in generator.stream_async(prepared, effective_options):
            if first_token:
                first_token = False
                await self._fire_observer(
                    lambda o: o.on_stream_started_async(
                        AIStreamEvent(
                            correlation_id,
                            prepared,
                            timedelta(seconds=time.monotonic() - started),
                            0,
                            datetime.now(_UTC),
                        )
                    )
                )
            parts.append(piece)
            token_count += 1
            yield piece

        elapsed = timedelta(seconds=time.monotonic() - started)
        await self._try_store_episode(user_query, "".join(parts))
        await self._fire_observer(
            lambda o: o.on_stream_completed_async(
                AIStreamEvent(correlation_id, prepared, elapsed, token_count, datetime.now(_UTC))
            )
        )

    async def invoke_tool_async(
        self, invocation: ToolInvocation, ct: object = None
    ) -> ToolResult:
        if invocation is None:
            raise ValueError("invocation is required")
        self._throw_if_disposed()

        if self._options.tool_bridge is None:
            fail_result = ToolResult(
                tool_name=invocation.tool_name,
                success=False,
                error="No tool bridge configured.",
            )
            await self._fire_observer(
                lambda o: o.on_tool_invoked_async(
                    AIToolEvent(
                        uuid.uuid4(), invocation, fail_result, timedelta(0), datetime.now(_UTC)
                    )
                )
            )
            return fail_result

        correlation_id = uuid.uuid4()
        started = time.monotonic()
        result = await self._options.tool_bridge.invoke_async(invocation)
        elapsed = timedelta(seconds=time.monotonic() - started)

        await self._fire_observer(
            lambda o: o.on_tool_invoked_async(
                AIToolEvent(correlation_id, invocation, result, elapsed, datetime.now(_UTC))
            )
        )
        return result

    # ── Agentic loop ───────────────────────────────────────────────────────

    async def agentic_chat_async(
        self,
        prompt: str,
        options: Optional[GenerationOptions] = None,
        ct: object = None,
    ) -> str:
        if prompt is None or not prompt.strip():
            raise ValueError("prompt is required")
        await self._ensure_started()

        # Neuron slot selection for the whole agentic run (prompt has no image).
        generator = await self._select_slot_async(prompt, False)

        max_iter = max(
            1,
            self._options.agentic_max_iterations
            if self._options.agentic_max_iterations is not None
            else DeviceTierDefaults.agentic_max_iterations(self._resolved_device_tier),
        )
        effective_options = options or self._options.default_generation_options

        history: List[ChatMessage] = [ChatMessage("user", prompt)]
        last_response = ""

        for _iteration in range(max_iter):
            prepared = await self._prepare_messages(history, prompt)

            started = time.monotonic()
            response = await generator.generate_async(prepared, effective_options)
            elapsed = timedelta(seconds=time.monotonic() - started)

            last_response = response
            history.append(ChatMessage("assistant", response))

            await self._fire_observer(
                lambda o, p=prepared, r=response, e=elapsed: o.on_chat_completed_async(
                    AIChatEvent(uuid.uuid4(), p, r, e, datetime.now(_UTC))
                )
            )

            invocation = parse_tool_call(response)
            if invocation is None:
                break  # no tool call — done

            if self._options.tool_bridge is None:
                history.append(
                    ChatMessage(
                        "tool",
                        '{"tool": "'
                        + invocation.tool_name
                        + '", "error": "No tool bridge configured."}',
                    )
                )
                continue

            tool_result = await self.invoke_tool_async(invocation)
            if tool_result.success:
                tool_content = (
                    '{"tool": "'
                    + tool_result.tool_name
                    + '", "result": '
                    + _json.dumps(tool_result.result)
                    + "}"
                )
            else:
                tool_content = (
                    '{"tool": "'
                    + tool_result.tool_name
                    + '", "error": '
                    + _json.dumps(tool_result.error)
                    + "}"
                )
            history.append(ChatMessage("tool", tool_content))

        await self._try_store_episode(prompt, last_response)
        return last_response

    # ── Feedback ───────────────────────────────────────────────────────────

    async def submit_feedback_async(
        self, signal: FeedbackSignal, ct: object = None
    ) -> None:
        if signal is None:
            raise ValueError("signal is required")
        self._throw_if_disposed()

        if self._options.feedback_store is None:
            return

        try:
            await self._options.feedback_store.add_async(signal)

            persona = await self._ensure_persona()
            if signal.polarity == FeedbackPolarity.POSITIVE:
                persona.positive_signals += 1
            elif signal.polarity == FeedbackPolarity.NEGATIVE:
                persona.negative_signals += 1
            persona.total_interactions += 1

            recent_signals = await self._options.feedback_store.get_recent_async(20)
            adaptation = FeedbackAnalyser().analyse(recent_signals)

            if adaptation.verbosity_delta < 0.0:
                persona.verbosity = (
                    "balanced" if persona.verbosity == "detailed" else "brief"
                )
            elif adaptation.verbosity_delta > 0.0:
                persona.verbosity = (
                    "balanced" if persona.verbosity == "brief" else "detailed"
                )

            if adaptation.formality_delta < 0.0:
                persona.formality = (
                    "neutral" if persona.formality == "formal" else "casual"
                )
            elif adaptation.formality_delta > 0.0:
                persona.formality = (
                    "neutral" if persona.formality == "casual" else "formal"
                )

            for topic in adaptation.preferred_topics:
                existing = persona.topic_weights.get(topic, 0.0)
                persona.topic_weights[topic] = existing + 1.0

            await self._try_save_persona()
        except Exception:  # noqa: BLE001 - feedback storage failure is non-fatal
            pass

    # ── Dispose ────────────────────────────────────────────────────────────

    async def dispose_async(self) -> None:
        if self._disposed:
            return
        self._disposed = True
        if self._slots is not None:
            try:
                await self._slots.dispose_async()
            except Exception:  # noqa: BLE001
                pass
            self._slots = None
        await self._try_save_persona()
        try:
            await self.stop_async()
        except Exception:  # noqa: BLE001
            pass

    # ── Private helpers ────────────────────────────────────────────────────

    async def _ensure_started(self) -> None:
        self._throw_if_disposed()
        if self._started:
            return
        await self.start_async()

    # ── Neuron — two-slot residency + session persistence ──────────────────

    async def save_session_async(self, path: str, ct: object = None) -> bool:
        """(RT-02) Snapshot the always-warm generalist floor. No-throw."""
        self._throw_if_disposed()
        if not path or not path.strip():
            return False
        gen = self._generator
        if gen is None:
            return False
        try:
            return await gen.save_session_async(path)
        except Exception:  # noqa: BLE001 - no-throw contract
            return False

    async def load_session_async(self, path: str, ct: object = None) -> bool:
        """(RT-02) Restore the generalist floor. No-throw."""
        self._throw_if_disposed()
        if not path or not path.strip():
            return False
        await self._ensure_started()
        gen = self._generator
        if gen is None:
            return False
        try:
            return await gen.load_session_async(path)
        except Exception:  # noqa: BLE001 - no-throw contract
            return False

    async def evict_specialist_async(self) -> None:
        """Evict the hot specialist; the generalist floor keeps serving."""
        if self._slots is not None:
            await self._slots.evict_specialist_async()

    def _probe_device(self) -> DeviceProbe:
        ctx = self._options.device_context
        if isinstance(ctx, DefaultDeviceContext):
            return ctx.build_probe()
        return DeviceProbe.snapshot()

    async def _select_slot_async(self, user_query: str, has_image: bool):
        """Neuron slot selection. No router -> the generalist (unchanged). With a
        router: route the turn and, on a specialist decision, best-fit + hot-load
        (admission-gated) a specialist. Any miss — no selector/factory, a
        best-fit that resolves to the generalist, gate denial, or a build failure
        — degrades to the generalist and never raises.
        """
        generalist = self._generator
        if generalist is None:
            raise RuntimeError("Butler is not ready.")

        router = self._options.router
        if router is None:
            return generalist

        try:
            decision = router.route(RouteContext(user_query or "", has_image))
        except Exception:  # noqa: BLE001 - a router fault must not break generation
            return generalist

        if decision.organ != Organ.SPECIALIST:
            return generalist

        selector = self._options.model_selector
        factory = self._options.specialist_factory
        if selector is None or factory is None:
            return generalist

        try:
            selection = selector.best_fit(self._probe_device(), decision.capability)
            if (
                self._options.model_id is not None
                and selection.model_id.lower() == self._options.model_id.lower()
            ):
                return generalist  # best-fit resolved to the generalist itself
            if self._slots is None:
                self._slots = ResidentSlotManager(
                    self._generalist_reserved_bytes,
                    lambda: self._probe_device().ram_available_bytes,
                )
            admission = await self._slots.ensure_specialist_async(selection, factory)
            return admission.generator or generalist
        except Exception:  # noqa: BLE001 - degrade to generalist, never raise
            return generalist

    async def _prepare_messages(
        self, messages: Sequence[ChatMessage], user_query: str
    ) -> List[ChatMessage]:
        system_content = await self._build_enriched_system_prompt(user_query)
        has_system = any(m.role.lower() == "system" for m in messages)

        prepared: List[ChatMessage] = []
        if has_system:
            prepared.extend(messages)
        else:
            if system_content and system_content.strip():
                prepared.append(ChatMessage("system", system_content))
            prepared.extend(messages)
        return prepared

    async def _build_enriched_system_prompt(self, user_query: str) -> str:
        parts: List[str] = [self._options.system_prompt]

        # 1. Persona hints.
        try:
            persona = await self._ensure_persona()
            hint = persona.to_system_prompt_hint()
            if hint and hint.strip():
                parts.append("\n")
                parts.append(hint)
        except Exception:  # noqa: BLE001 - persona load is non-fatal
            pass

        # 1b. Affect state.
        if self._options.affect_store is not None:
            try:
                affect = await self._options.affect_store.load_async(
                    self._options.persona_user_id
                )
                hint = affect.to_system_prompt_hint()
                if hint and hint.strip():
                    parts.append("\n")
                    parts.append(hint)
            except Exception:  # noqa: BLE001 - affect load is non-fatal
                pass

        # 2. Device context.
        ctx = self._options.device_context
        if ctx is not None and not _is_null_context(ctx):
            ctx_lines: List[str] = []
            if ctx.local_time is not None:
                tz = ctx.time_zone_id or "UTC"
                ctx_lines.append(
                    f"Local time: {ctx.local_time.strftime('%Y-%m-%d %H:%M')} ({tz})"
                )
            if ctx.location_hint and ctx.location_hint.strip():
                ctx_lines.append(f"Location: {ctx.location_hint}")
            if ctx.battery_level is not None:
                pct = int(ctx.battery_level * 100)
                charging = " (charging)" if ctx.is_charging is True else ""
                ctx_lines.append(f"Battery: {pct}%{charging}")
            if ctx.network_type and ctx.network_type.strip():
                ctx_lines.append(f"Network: {ctx.network_type}")
            if ctx.active_app_id and ctx.active_app_id.strip():
                ctx_lines.append(f"Active app: {ctx.active_app_id}")

            if len(ctx_lines) > 0:
                parts.append("\n")
                parts.append("[Device context]\n")
                for line in ctx_lines:
                    parts.append(line + "\n")

        # 3. RAG context (relevant past exchanges).
        if (
            self._options.episodic_memory is not None
            and self._options.rag_top_k > 0
            and user_query
            and user_query.strip()
        ):
            try:
                builder = self._ensure_rag_builder()
                rag_block = await builder.build_context_async(user_query)
                if rag_block and rag_block.strip():
                    parts.append("\n")
                    parts.append(rag_block)
            except Exception:  # noqa: BLE001 - RAG failure is non-fatal
                pass

        return "".join(parts)

    def _ensure_rag_builder(self) -> RagContextBuilder:
        if self._rag_builder is not None:
            return self._rag_builder
        self._rag_builder = self._options.rag_builder or RagContextBuilder(
            self._options.episodic_memory,
            embedder=None,
            top_k=self._options.rag_top_k,
        )
        return self._rag_builder

    async def _ensure_persona(self) -> PersonaState:
        if self._persona_cache is not None:
            return self._persona_cache
        if self._options.persona_store is None:
            self._persona_cache = PersonaState(user_id=self._options.persona_user_id)
            return self._persona_cache
        self._persona_cache = await self._options.persona_store.load_async(
            self._options.persona_user_id
        )
        return self._persona_cache

    async def _try_save_persona(self) -> None:
        if self._persona_cache is None or self._options.persona_store is None:
            return
        try:
            await self._options.persona_store.save_async(self._persona_cache)
        except Exception:  # noqa: BLE001 - persist failure is non-fatal
            pass

    async def _try_store_episode(self, user_text: str, assistant_text: str) -> None:
        if self._options.episodic_memory is None:
            return
        if user_text is None or not user_text.strip():
            return
        try:
            app_ctx = (
                self._options.device_context.active_app_id
                if self._options.device_context is not None
                else None
            )
            entry = EpisodicMemoryEntry(
                user_text=user_text,
                assistant_text=assistant_text,
                app_context=app_ctx,
                embedding=None,
            )
            await self._options.episodic_memory.add_async(entry)
        except Exception:  # noqa: BLE001 - episodic write is non-fatal
            pass

    async def _fire_observer(self, action: Callable[[IAIObserver], object]) -> None:
        observer = self._options.observer
        if observer is None:
            return
        try:
            await action(observer)
        except Exception:  # noqa: BLE001 - observer errors are non-fatal
            pass

    def _throw_if_disposed(self) -> None:
        if self._disposed:
            raise RuntimeError("AIService is disposed")


# ── Tool-call parsing (module-level, mirrors the internal C# static) ───────


def parse_tool_call(response: str) -> Optional[ToolInvocation]:
    """Parse a tool call from Qwen3's ``<tool_call>…</tool_call>`` format.
    Returns ``None`` when no tool call is present. Mirrors ``AIService.ParseToolCall``.
    """
    if response is None or not response.strip():
        return None

    start = response.find(_TOOL_CALL_OPEN)
    if start < 0:
        return None

    content_start = start + len(_TOOL_CALL_OPEN)
    end = response.find(_TOOL_CALL_CLOSE, content_start)
    if end < 0:
        return None

    json_text = response[content_start:end].strip()
    if not json_text:
        return None

    try:
        root = _json.loads(json_text)
    except _json.JSONDecodeError:
        return None
    if not isinstance(root, dict):
        return None

    # Support both {"name":...} and {"tool_name":...} spellings.
    tool_name = root.get("name")
    if tool_name is None:
        tool_name = root.get("tool_name")
    if not isinstance(tool_name, str) or not tool_name.strip():
        return None

    args: dict = {}
    args_prop = root.get("arguments")
    if isinstance(args_prop, dict):
        for name, value in args_prop.items():
            # C#: strings pass through; everything else keeps its raw JSON text.
            if isinstance(value, str):
                args[name] = value
            else:
                args[name] = _json.dumps(value)

    return ToolInvocation(tool_name=tool_name, arguments=args)


def _last_user_query(messages: Sequence[ChatMessage]) -> str:
    for m in reversed(list(messages)):
        if m.role.lower() == "user":
            return m.content or ""
    return ""


def _has_image(messages: Sequence[ChatMessage]) -> bool:
    return any(getattr(m, "image_bytes", None) for m in messages)


def _is_null_context(ctx) -> bool:
    """True when the device context carries no signal — mirrors the C#
    ``ctx is NullDeviceContext`` guard. Detected structurally: every field None.
    """
    from ..device.device_probe import NullDeviceContext

    if isinstance(ctx, NullDeviceContext):
        return True
    return False
