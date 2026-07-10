"""AI endpoints — port of CircleAI.Hosting IAIEndpoint + endpoint impls.

Transport-agnostic surface for exposing an :class:`IAIService`.

  * ``IAIEndpoint`` — the endpoint contract (start/stop/dispose).
  * ``InProcessEndpoint`` — no transport; exposes the service directly.
  * ``HttpLoopbackEndpoint`` + ``AIHttpClient`` — a loopback request/response
    pair. The C# binds ``System.Net.HttpListener`` on 127.0.0.1 with an
    ``X-Butler-Token`` shared secret and four routes (ask/chat/stream/tool),
    SSE-framing the stream. This port keeps the identical routing, token
    auth, and SSE ``data:``/``event: done`` framing but dispatches over an
    in-process channel instead of real sockets — the "inject the transport,
    keep it in-memory" contract. ``AIHttpClient`` is the matching client whose
    methods mirror :class:`IAIService`.

Token generation uses ``secrets.token_bytes(32)`` base64-encoded, matching the
C# ``RandomNumberGenerator.Fill(32) + Convert.ToBase64String``.
"""
from __future__ import annotations

import base64
import hmac
import json as _json
import secrets
from abc import ABC, abstractmethod
from typing import AsyncGenerator, List, Optional

from ..inference.inference import GenerationOptions
from ..models.models import ChatMessage
from ..tools.tool_types import ToolInvocation, ToolResult

__all__ = [
    "IAIEndpoint",
    "InProcessEndpoint",
    "HttpLoopbackEndpoint",
    "AIHttpClient",
    "generate_random_token",
]


def generate_random_token() -> str:
    """Generate a cryptographically-random 32-byte token, base64-encoded.
    Mirrors ``AIOptions.GenerateRandomToken``.
    """
    return base64.b64encode(secrets.token_bytes(32)).decode("ascii")


class IAIEndpoint(ABC):
    """Transport-agnostic endpoint that exposes an :class:`IAIService`. Mirrors
    ``IAIEndpoint``.
    """

    @abstractmethod
    async def start_async(self, service, ct: object = None) -> None:
        """Begin serving requests against ``service``. Idempotent."""
        ...

    @abstractmethod
    async def stop_async(self, ct: object = None) -> None:
        """Stop accepting new requests and drain in-flight ones."""
        ...

    @abstractmethod
    async def dispose_async(self) -> None:
        """Async-dispose the endpoint."""
        ...


class InProcessEndpoint(IAIEndpoint):
    """In-process endpoint. No transport — exposes the underlying
    :class:`IAIService` directly. Mirrors ``InProcessEndpoint``.
    """

    __slots__ = ("_service", "_started", "_disposed")

    def __init__(self) -> None:
        self._service = None
        self._started = False
        self._disposed = False

    @property
    def service_accessor(self):
        """The wrapped service. ``None`` until :meth:`start_async` has run.
        Mirrors ``ServiceAccessor``.
        """
        return self._service

    async def start_async(self, service, ct: object = None) -> None:
        if self._disposed:
            raise RuntimeError("InProcessEndpoint is disposed")
        if self._started:
            return
        if service is None:
            raise ValueError("service is required")
        self._service = service
        self._started = True

    async def stop_async(self, ct: object = None) -> None:
        self._started = False
        self._service = None

    async def dispose_async(self) -> None:
        if self._disposed:
            return
        self._disposed = True
        self._service = None
        self._started = False


# ── Loopback request/response (in-memory) ──────────────────────────────────


class _Response:
    """Minimal HTTP-shaped response object used by the in-memory loopback."""

    __slots__ = ("status", "content_type", "body", "sse_frames")

    def __init__(self) -> None:
        self.status = 200
        self.content_type = "text/plain; charset=utf-8"
        self.body = ""
        self.sse_frames: Optional[List[str]] = None  # set for streaming routes


class HttpLoopbackEndpoint(IAIEndpoint):
    """Loopback transport for :class:`IAIService`. Semantically equivalent to
    the C# ``HttpLoopbackEndpoint`` (token auth, POST-only, four routes, SSE
    framing) but dispatches over an in-process channel. Mirrors the routes:

        POST /butler/ask    -> {"question": str}                 -> text/plain
        POST /butler/chat   -> {"messages": [...], "options": {}} -> {"content": str}
        POST /butler/stream -> {"messages": [...], "options": {}} -> SSE frames
        POST /butler/tool   -> {"toolName": str, "arguments": {}} -> ToolResult JSON

    Auth is a shared secret compared in constant time. When no token is
    configured, a random one is generated at start and exposed via :attr:`token`.
    """

    __slots__ = ("_options", "_service", "_token", "_started", "_disposed")

    def __init__(self, options) -> None:
        if options is None:
            raise ValueError("options is required")
        self._options = options
        self._service = None
        self._token: Optional[str] = None
        self._started = False
        self._disposed = False

    @property
    def token(self) -> Optional[str]:
        """Effective shared-secret token. ``None`` when not started."""
        return self._token

    async def start_async(self, service, ct: object = None) -> None:
        if self._disposed:
            raise RuntimeError("HttpLoopbackEndpoint is disposed")
        if service is None:
            raise ValueError("service is required")
        if self._started:
            return
        self._service = service
        configured = getattr(self._options, "loopback_token", None)
        self._token = configured if configured else generate_random_token()
        self._started = True

    async def stop_async(self, ct: object = None) -> None:
        if not self._started:
            return
        self._started = False
        self._service = None

    async def dispose_async(self) -> None:
        if self._disposed:
            return
        self._disposed = True
        await self.stop_async()

    def _authorise(self, supplied_token: Optional[str]) -> bool:
        token = self._token
        if not token:
            return False
        if not supplied_token:
            return False
        return hmac.compare_digest(supplied_token, token)

    async def handle_request_async(
        self,
        method: str,
        path: str,
        token: Optional[str],
        body: str,
    ) -> _Response:
        """Dispatch one request (the in-memory analogue of the C# accept-loop
        handler). Returns a :class:`_Response`.
        """
        resp = _Response()
        if not self._authorise(token):
            resp.status = 401
            resp.body = "unauthorised"
            return resp
        if method.upper() != "POST":
            resp.status = 405
            resp.body = "method not allowed"
            return resp

        service = self._service
        if service is None:
            resp.status = 500
            resp.body = "internal error"
            return resp

        try:
            if path == "/butler/ask":
                return await self._handle_ask(service, body, resp)
            if path == "/butler/chat":
                return await self._handle_chat(service, body, resp)
            if path == "/butler/stream":
                return await self._handle_stream(service, body, resp)
            if path == "/butler/tool":
                return await self._handle_tool(service, body, resp)
            resp.status = 404
            resp.body = "not found"
            return resp
        except Exception:  # noqa: BLE001 - handler faults become a 500
            resp.status = 500
            resp.content_type = "text/plain; charset=utf-8"
            resp.body = "internal error"
            return resp

    async def _handle_ask(self, service, body: str, resp: _Response) -> _Response:
        payload = _try_deserialise(body)
        if payload is None or not str(payload.get("question", "")).strip():
            resp.status = 400
            resp.body = "missing 'question'"
            return resp
        answer = await service.ask_async(payload["question"])
        resp.status = 200
        resp.body = answer
        return resp

    async def _handle_chat(self, service, body: str, resp: _Response) -> _Response:
        payload = _try_deserialise(body)
        messages = payload.get("messages") if payload else None
        if not messages:
            resp.status = 400
            resp.body = "missing 'messages'"
            return resp
        chat_messages = _messages_from_payload(messages)
        options = _options_from_payload(payload.get("options"))
        content = await service.chat_async(chat_messages, options)
        resp.status = 200
        resp.content_type = "application/json; charset=utf-8"
        resp.body = _json.dumps({"content": content})
        return resp

    async def _handle_stream(self, service, body: str, resp: _Response) -> _Response:
        payload = _try_deserialise(body)
        messages = payload.get("messages") if payload else None
        if not messages:
            resp.status = 400
            resp.body = "missing 'messages'"
            return resp
        chat_messages = _messages_from_payload(messages)
        options = _options_from_payload(payload.get("options"))

        resp.status = 200
        resp.content_type = "text/event-stream"
        frames: List[str] = []
        async for piece in service.stream_async(chat_messages, options):
            # SSE framing: `data: <json>\n\n` — matches the C# writer.
            frames.append("data: " + _json.dumps(piece) + "\n\n")
        # Closing event so clients know we're done cleanly.
        frames.append("event: done\ndata: {}\n\n")
        resp.sse_frames = frames
        resp.body = "".join(frames)
        return resp

    async def _handle_tool(self, service, body: str, resp: _Response) -> _Response:
        payload = _try_deserialise(body)
        if payload is None or not str(payload.get("toolName", "")).strip():
            resp.status = 400
            resp.body = "missing 'toolName'"
            return resp
        args = payload.get("arguments") or {}
        invocation = ToolInvocation(tool_name=payload["toolName"], arguments=args)
        result = await service.invoke_tool_async(invocation)
        resp.status = 200 if result.success else 502
        resp.content_type = "application/json; charset=utf-8"
        resp.body = _json.dumps(_tool_result_to_dict(result))
        return resp


class AIHttpClient:
    """Client that talks to a :class:`HttpLoopbackEndpoint`. Methods mirror
    :class:`IAIService` so the same call sites work in-process or via this
    client. Mirrors ``AIHttpClient`` (over the in-memory loopback).
    """

    __slots__ = ("_endpoint", "_token")

    def __init__(self, endpoint: HttpLoopbackEndpoint, token: str) -> None:
        if endpoint is None:
            raise ValueError("endpoint is required")
        if token is None or not token.strip():
            raise ValueError("token is required")
        self._endpoint = endpoint
        self._token = token

    async def ask_async(self, question: str) -> str:
        if question is None or not question.strip():
            raise ValueError("question is required")
        resp = await self._endpoint.handle_request_async(
            "POST", "/butler/ask", self._token, _json.dumps({"question": question})
        )
        _ensure_success(resp)
        return resp.body

    async def chat_async(
        self,
        messages: List[ChatMessage],
        options: Optional[GenerationOptions] = None,
    ) -> str:
        if messages is None:
            raise ValueError("messages is required")
        body = _json.dumps(
            {
                "messages": _messages_to_payload(messages),
                "options": _options_to_payload(options),
            }
        )
        resp = await self._endpoint.handle_request_async(
            "POST", "/butler/chat", self._token, body
        )
        _ensure_success(resp)
        parsed = _json.loads(resp.body)
        return parsed.get("content", "")

    async def stream_async(
        self,
        messages: List[ChatMessage],
        options: Optional[GenerationOptions] = None,
    ) -> AsyncGenerator[str, None]:
        if messages is None:
            raise ValueError("messages is required")
        body = _json.dumps(
            {
                "messages": _messages_to_payload(messages),
                "options": _options_to_payload(options),
            }
        )
        resp = await self._endpoint.handle_request_async(
            "POST", "/butler/stream", self._token, body
        )
        _ensure_success(resp)
        # Parse the SSE framing exactly as the C# AIHttpClient does.
        for frame in resp.sse_frames or []:
            for line in frame.split("\n"):
                if len(line) == 0:
                    continue
                if line.startswith("event:"):
                    if line[6:].strip() == "done":
                        return
                    continue
                if not line.startswith("data:"):
                    continue
                data_part = line[5:].lstrip()
                if len(data_part) == 0:
                    continue
                try:
                    piece = _json.loads(data_part)
                except _json.JSONDecodeError:
                    piece = data_part
                if piece:
                    yield piece

    async def invoke_tool_async(self, invocation: ToolInvocation) -> ToolResult:
        if invocation is None:
            raise ValueError("invocation is required")
        body = _json.dumps(
            {"toolName": invocation.tool_name, "arguments": dict(invocation.arguments)}
        )
        resp = await self._endpoint.handle_request_async(
            "POST", "/butler/tool", self._token, body
        )
        # Accept 200 (success) and 502 (tool failure) — the body is a ToolResult.
        if resp.status not in (200, 502):
            _ensure_success(resp)
        parsed = _json.loads(resp.body)
        return ToolResult(
            tool_name=parsed.get("toolName", invocation.tool_name),
            success=bool(parsed.get("success", False)),
            result=parsed.get("result"),
            error=parsed.get("error"),
        )


# ── payload helpers ────────────────────────────────────────────────────────


def _ensure_success(resp: _Response) -> None:
    if resp.status < 200 or resp.status >= 300:
        raise RuntimeError(f"Butler endpoint returned status {resp.status}: {resp.body}")


def _try_deserialise(body: str) -> Optional[dict]:
    if body is None or not body.strip():
        return None
    try:
        parsed = _json.loads(body)
    except _json.JSONDecodeError:
        return None
    return parsed if isinstance(parsed, dict) else None


def _messages_from_payload(items) -> List[ChatMessage]:
    return [ChatMessage(m.get("role") or "user", m.get("content") or "") for m in items]


def _messages_to_payload(messages: List[ChatMessage]) -> List[dict]:
    return [{"role": m.role, "content": m.content} for m in messages]


def _options_from_payload(o: Optional[dict]) -> Optional[GenerationOptions]:
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


def _options_to_payload(o: Optional[GenerationOptions]) -> Optional[dict]:
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


def _tool_result_to_dict(result: ToolResult) -> dict:
    return {
        "toolName": result.tool_name,
        "success": result.success,
        "result": result.result,
        "error": result.error,
    }
