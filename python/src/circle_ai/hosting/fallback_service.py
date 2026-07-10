"""AIApiClient + FallbackAIService — ports of CircleAI.Hosting.AIApiClient
and FallbackAIService.

  * ``IButlerApiTransport`` — injected HTTP-shaped transport to a remote
    ButlerAPI. The C# ``AIApiClient`` uses a real ``HttpClient``; per the
    "inject external/cloud dependencies behind interfaces, keep it in-memory"
    contract this port routes every call through this abstraction, with a
    deterministic in-memory fake (:class:`InProcessButlerApiTransport`) for
    tests.
  * ``AIApiClient`` — :class:`IAIService` that proxies to a remote ButlerAPI.
  * ``IAvailableRamProbe`` + ``DefaultAvailableRamProbe`` — RAM gate.
  * ``FallbackAIService`` — prefers local inference, falls back to cloud when
    RAM is below the threshold or local start throws.

Wire format (endpoints/JSON keys) matches the C# ``AIApiClient`` DTOs so a real
transport can be dropped in unchanged: ``api/butler/health`` (GET),
``api/butler/ask`` / ``chat`` / ``agentic`` / ``tool`` / ``feedback`` (POST),
``api/butler/stream`` (POST → SSE ``data:`` frames, terminated by ``[DONE]``).
"""
from __future__ import annotations

import json as _json
from abc import ABC, abstractmethod
from typing import AsyncGenerator, List, Optional, Sequence

from ..inference.inference import GenerationOptions
from ..memory.feedback_signal import FeedbackSignal
from ..models.models import ChatMessage
from ..tools.tool_types import ToolInvocation, ToolResult
from .ai_service import IAIService

__all__ = [
    "IButlerApiTransport",
    "ButlerApiResponse",
    "InProcessButlerApiTransport",
    "AIApiClient",
    "IAvailableRamProbe",
    "DefaultAvailableRamProbe",
    "FallbackAIService",
]


class ButlerApiResponse:
    """Minimal HTTP-shaped response returned by :class:`IButlerApiTransport`."""

    __slots__ = ("status", "body", "sse_lines")

    def __init__(
        self, status: int = 200, body: str = "", sse_lines: Optional[List[str]] = None
    ) -> None:
        self.status = status
        self.body = body
        self.sse_lines = sse_lines  # for the streaming route

    @property
    def is_success(self) -> bool:
        return 200 <= self.status < 300


class IButlerApiTransport(ABC):
    """Injected HTTP-shaped transport to a remote ButlerAPI. Mirrors the role
    of the C# ``HttpClient`` inside ``AIApiClient``.
    """

    @abstractmethod
    async def get_async(self, path: str, ct: object = None) -> ButlerApiResponse:
        """Issue a GET to ``path`` (e.g. ``api/butler/health``)."""
        ...

    @abstractmethod
    async def post_json_async(
        self, path: str, payload: dict, ct: object = None
    ) -> ButlerApiResponse:
        """Issue a POST with a JSON body to ``path``."""
        ...

    @abstractmethod
    async def post_sse_async(
        self, path: str, payload: dict, ct: object = None
    ) -> ButlerApiResponse:
        """Issue a POST expecting an SSE stream; ``sse_lines`` holds the raw
        ``data: …`` lines.
        """
        ...


class InProcessButlerApiTransport(IButlerApiTransport):
    """Deterministic in-memory :class:`IButlerApiTransport` that forwards to a
    local :class:`IAIService` — a test/dev "cloud" that never leaves the
    process. Produces the same wire-shaped responses the real ButlerAPI would.
    """

    __slots__ = ("_service",)

    def __init__(self, service: IAIService) -> None:
        if service is None:
            raise ValueError("service is required")
        self._service = service

    async def get_async(self, path: str, ct: object = None) -> ButlerApiResponse:
        if path.rstrip("/").endswith("butler/health"):
            return ButlerApiResponse(200, _json.dumps({"status": "ok"}))
        return ButlerApiResponse(404, "not found")

    async def post_json_async(
        self, path: str, payload: dict, ct: object = None
    ) -> ButlerApiResponse:
        p = path.rstrip("/")
        if p.endswith("butler/ask"):
            text = await self._service.ask_async(payload["question"])
            return ButlerApiResponse(200, _json.dumps({"text": text}))
        if p.endswith("butler/chat"):
            text = await self._service.chat_async(
                _messages(payload["messages"]), _options(payload.get("options"))
            )
            return ButlerApiResponse(200, _json.dumps({"text": text}))
        if p.endswith("butler/agentic"):
            text = await self._service.agentic_chat_async(
                payload["prompt"], _options(payload.get("options"))
            )
            return ButlerApiResponse(200, _json.dumps({"text": text}))
        if p.endswith("butler/tool"):
            invocation = ToolInvocation(
                tool_name=payload["name"], arguments=payload.get("arguments") or {}
            )
            result = await self._service.invoke_tool_async(invocation)
            return ButlerApiResponse(
                200,
                _json.dumps(
                    {
                        "toolName": result.tool_name,
                        "success": result.success,
                        "result": result.result,
                        "error": result.error,
                    }
                ),
            )
        if p.endswith("butler/feedback"):
            return ButlerApiResponse(200, "{}")
        return ButlerApiResponse(404, "not found")

    async def post_sse_async(
        self, path: str, payload: dict, ct: object = None
    ) -> ButlerApiResponse:
        lines: List[str] = []
        async for piece in self._service.stream_async(
            _messages(payload["messages"]), _options(payload.get("options"))
        ):
            lines.append("data: " + piece)
        lines.append("data: [DONE]")
        return ButlerApiResponse(200, "", sse_lines=lines)


class AIApiClient(IAIService):
    """:class:`IAIService` that proxies requests to a remote ButlerAPI over the
    injected :class:`IButlerApiTransport`. Mirrors ``AIApiClient``.
    """

    __slots__ = ("_transport", "_is_ready", "_disposed")

    def __init__(self, transport: IButlerApiTransport) -> None:
        if transport is None:
            raise ValueError("transport is required")
        self._transport = transport
        self._is_ready = False
        self._disposed = False

    @property
    def is_ready(self) -> bool:
        return self._is_ready

    async def start_async(self, ct: object = None) -> None:
        resp = await self._transport.get_async("api/butler/health")
        _ensure_success(resp)
        self._is_ready = True

    async def stop_async(self, ct: object = None) -> None:
        self._is_ready = False

    async def ask_async(self, question: str, ct: object = None) -> str:
        resp = await self._transport.post_json_async(
            "api/butler/ask", {"question": question}
        )
        _ensure_success(resp)
        return _text_of(resp)

    async def chat_async(
        self,
        messages: Sequence[ChatMessage],
        options: Optional[GenerationOptions] = None,
        ct: object = None,
    ) -> str:
        resp = await self._transport.post_json_async(
            "api/butler/chat",
            {"messages": _messages_payload(messages), "options": _options_payload(options)},
        )
        _ensure_success(resp)
        return _text_of(resp)

    async def stream_async(
        self,
        messages: Sequence[ChatMessage],
        options: Optional[GenerationOptions] = None,
        ct: object = None,
    ) -> AsyncGenerator[str, None]:
        resp = await self._transport.post_sse_async(
            "api/butler/stream",
            {"messages": _messages_payload(messages), "options": _options_payload(options)},
        )
        _ensure_success(resp)
        for line in resp.sse_lines or []:
            if line is None:
                break
            if not line.startswith("data:"):
                continue
            token = line[len("data:") :].strip()
            if token == "[DONE]":
                return
            if token:
                yield token

    async def agentic_chat_async(
        self,
        prompt: str,
        options: Optional[GenerationOptions] = None,
        ct: object = None,
    ) -> str:
        resp = await self._transport.post_json_async(
            "api/butler/agentic",
            {"prompt": prompt, "options": _options_payload(options)},
        )
        _ensure_success(resp)
        return _text_of(resp)

    async def invoke_tool_async(
        self, invocation: ToolInvocation, ct: object = None
    ) -> ToolResult:
        resp = await self._transport.post_json_async(
            "api/butler/tool",
            {"name": invocation.tool_name, "arguments": dict(invocation.arguments)},
        )
        _ensure_success(resp)
        parsed = _json.loads(resp.body) if resp.body else {}
        if not parsed:
            return ToolResult.failure(invocation.tool_name, "Empty response from cloud")
        return ToolResult(
            tool_name=parsed.get("toolName", invocation.tool_name),
            success=bool(parsed.get("success", False)),
            result=parsed.get("result"),
            error=parsed.get("error"),
        )

    async def submit_feedback_async(
        self, signal: FeedbackSignal, ct: object = None
    ) -> None:
        resp = await self._transport.post_json_async(
            "api/butler/feedback",
            {
                "id": str(signal.id),
                "polarity": int(signal.polarity.value),
                "userText": signal.user_text,
                "assistantText": signal.assistant_text,
                "comment": signal.comment,
            },
        )
        _ensure_success(resp)

    async def dispose_async(self) -> None:
        if not self._disposed:
            self._disposed = True
            self._is_ready = False


# ── RAM probe + FallbackAIService ──────────────────────────────────────────


class IAvailableRamProbe(ABC):
    """Reports available RAM in bytes. Injected so the fallback decision is
    deterministic in tests. Mirrors the C# ``GC.GetGCMemoryInfo`` read.
    """

    @abstractmethod
    def available_ram_bytes(self) -> int:
        ...


class DefaultAvailableRamProbe(IAvailableRamProbe):
    """Reads available RAM via ``psutil`` when present; otherwise returns 0
    (the C# fallback path when the probe fails).
    """

    def available_ram_bytes(self) -> int:
        try:
            import psutil  # type: ignore

            return int(psutil.virtual_memory().available)
        except Exception:  # noqa: BLE001 - probe failure → 0, per C#
            return 0


class FallbackAIService(IAIService):
    """Wraps a local :class:`IAIService` with a cloud :class:`AIApiClient`
    fallback. Local is preferred; cloud is used transparently when local is
    unavailable (RAM below threshold or local start throws). Mirrors
    ``FallbackAIService``.
    """

    __slots__ = ("_local", "_cloud", "_ram_threshold_bytes", "_ram_probe", "_active", "_disposed")

    def __init__(
        self,
        local: IAIService,
        cloud: AIApiClient,
        ram_threshold_bytes: int = 2 * 1024 * 1024 * 1024,
        ram_probe: Optional[IAvailableRamProbe] = None,
    ) -> None:
        if local is None:
            raise ValueError("local is required")
        if cloud is None:
            raise ValueError("cloud is required")
        self._local = local
        self._cloud = cloud
        self._ram_threshold_bytes = ram_threshold_bytes
        self._ram_probe = ram_probe or DefaultAvailableRamProbe()
        self._active: Optional[IAIService] = None
        self._disposed = False

    @property
    def is_ready(self) -> bool:
        return self._active.is_ready if self._active is not None else False

    async def start_async(self, ct: object = None) -> None:
        available_ram = self._get_available_ram_bytes()

        if available_ram >= self._ram_threshold_bytes:
            try:
                await self._local.start_async(ct)
                self._active = self._local
                return
            except Exception:  # noqa: BLE001 - local start failure → cloud
                pass

        await self._cloud.start_async(ct)
        self._active = self._cloud

    async def stop_async(self, ct: object = None) -> None:
        if self._active is not None:
            await self._active.stop_async(ct)

    async def ask_async(self, question: str, ct: object = None) -> str:
        return await self._require_active().ask_async(question, ct)

    async def chat_async(
        self,
        messages: Sequence[ChatMessage],
        options: Optional[GenerationOptions] = None,
        ct: object = None,
    ) -> str:
        return await self._require_active().chat_async(messages, options, ct)

    def stream_async(
        self,
        messages: Sequence[ChatMessage],
        options: Optional[GenerationOptions] = None,
        ct: object = None,
    ) -> AsyncGenerator[str, None]:
        return self._require_active().stream_async(messages, options, ct)

    async def agentic_chat_async(
        self,
        prompt: str,
        options: Optional[GenerationOptions] = None,
        ct: object = None,
    ) -> str:
        return await self._require_active().agentic_chat_async(prompt, options, ct)

    async def invoke_tool_async(
        self, invocation: ToolInvocation, ct: object = None
    ) -> ToolResult:
        return await self._require_active().invoke_tool_async(invocation, ct)

    async def submit_feedback_async(
        self, signal: FeedbackSignal, ct: object = None
    ) -> None:
        await self._require_active().submit_feedback_async(signal, ct)

    async def dispose_async(self) -> None:
        if self._disposed:
            return
        self._disposed = True
        await self._local.dispose_async()
        await self._cloud.dispose_async()

    def _require_active(self) -> IAIService:
        if self._active is None:
            raise RuntimeError(
                "FallbackAIService has not been started. Call start_async first."
            )
        return self._active

    def _get_available_ram_bytes(self) -> int:
        try:
            return self._ram_probe.available_ram_bytes()
        except Exception:  # noqa: BLE001
            return 0


# ── payload helpers ────────────────────────────────────────────────────────


def _ensure_success(resp: ButlerApiResponse) -> None:
    if not resp.is_success:
        raise RuntimeError(f"ButlerAPI returned status {resp.status}: {resp.body}")


def _text_of(resp: ButlerApiResponse) -> str:
    if not resp.body:
        return ""
    parsed = _json.loads(resp.body)
    return parsed.get("text", "") if isinstance(parsed, dict) else ""


def _messages_payload(messages: Sequence[ChatMessage]) -> List[dict]:
    return [{"role": m.role, "content": m.content} for m in messages]


def _options_payload(o: Optional[GenerationOptions]) -> Optional[dict]:
    if o is None:
        return None
    return {
        "maxTokens": o.max_tokens,
        "temperature": o.temperature,
        "topP": o.top_p,
        "topK": o.top_k,
        "seed": o.seed,
        "stopSequences": list(o.stop_sequences) if o.stop_sequences else None,
    }


def _messages(items) -> List[ChatMessage]:
    return [ChatMessage(m.get("role") or "user", m.get("content") or "") for m in items]


def _options(o: Optional[dict]) -> Optional[GenerationOptions]:
    if not o:
        return None
    defaults = GenerationOptions()
    return GenerationOptions(
        max_tokens=o.get("maxTokens", defaults.max_tokens),
        temperature=o.get("temperature", defaults.temperature),
        top_p=o.get("topP", defaults.top_p),
        top_k=o.get("topK", defaults.top_k),
        seed=o.get("seed"),
        stop_sequences=o.get("stopSequences"),
    )
