"""In-memory endpoint handlers.

Ports the routing logic of the ``CircleAI.Inference.Server.Endpoints``:
ChatCompletionsEndpoint, EmbeddingsEndpoint, CompanionEndpoint, AdminEndpoints.
Per the task, the HTTP contracts + routing are ported as in-memory handlers
behind an interface — no socket server. Each handler returns an
:class:`EndpointResult` (status code + JSON-serialisable body, or SSE frames)
matching what the C# endpoint would write.

The status codes, error envelopes, admission gating, per-request timeout
overlay (modelled as a caller-supplied ``ct``), stream/non-stream branching,
finish-reason mapping, and token-usage accounting all mirror the C# handlers.
"""
from __future__ import annotations

import uuid
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Any, List, Optional

from ..hosting.inference_bridge import (
    IInferenceBridge,
    InferenceFragmentKind,
    InferenceRequest,
    InferenceStatus,
)
from .auth import ApiKeyAuthHandler, AuthenticateResult
from .bridge_factory import IBridgeFactory
from .companion_resolver import ICompanionSessionResolver
from .counters import AdmissionControl, ServerCounters
from .dtos import (
    AdminLifecycleResponse,
    AdminLoadRequest,
    ChatCompletionChoice,
    ChatCompletionDelta,
    ChatCompletionMessage,
    ChatCompletionRequest,
    ChatCompletionResponse,
    ChatCompletionStreamChoice,
    ChatCompletionStreamChunk,
    CompanionTurnRequest,
    CompanionTurnResponse,
    EmbeddingDatum,
    EmbeddingsRequest,
    EmbeddingsResponse,
    ErrorResponse,
    UsageInfo,
)
from .lifecycle import (
    BackendKind,
    CapabilityTier,
    IModelLifecycleManager,
    LoadOutcome,
    ModelLoadDescriptor,
    UnloadOutcome,
)
from .registry import IInferenceServerModelRegistry
from .sse import ServerSentEventsWriter

__all__ = [
    "EndpointResult",
    "ChatCompletionsHandler",
    "EmbeddingsHandler",
    "CompanionTurnHandler",
    "AdminHandler",
    "require_auth",
]


# HTTP status constants used by the handlers (mirrors StatusCodes.*).
HTTP_OK = 200
HTTP_BAD_REQUEST = 400
HTTP_UNAUTHORIZED = 401
HTTP_NOT_FOUND = 404
HTTP_INTERNAL_ERROR = 500
HTTP_SERVICE_UNAVAILABLE = 503
HTTP_GATEWAY_TIMEOUT = 504
HTTP_INSUFFICIENT_STORAGE = 507


@dataclass(slots=True)
class EndpointResult:
    """What an endpoint handler produced. Mirrors the ``IResult`` a C# endpoint
    returns: a status code + a JSON body, or an SSE frame list (streaming).

    * ``status_code`` — HTTP status the endpoint would set.
    * ``body`` — JSON-serialisable payload (a ``to_dict``-able DTO or plain dict).
    * ``sse_frames`` — populated for streaming responses (body is ``None``).
    * ``headers`` — extra response headers (e.g. ``Retry-After``).
    """

    status_code: int
    body: Any = None
    sse_frames: Optional[List[str]] = None
    headers: dict = field(default_factory=dict)

    @property
    def body_dict(self) -> Any:
        if self.body is None:
            return None
        if hasattr(self.body, "to_dict"):
            return self.body.to_dict()
        return self.body


def _err(message: str, type: str, code: Optional[str], status: int) -> EndpointResult:
    return EndpointResult(status, ErrorResponse.of(message, type, code))


def require_auth(handler: ApiKeyAuthHandler, headers: dict) -> Optional[EndpointResult]:
    """Enforce the authenticated policy. Returns ``None`` when the caller is
    authenticated, or a 401 :class:`EndpointResult` otherwise. Mirrors the
    ``RequireAuthorization(AuthenticatedPolicy)`` gate on every endpoint.
    """
    result = handler.authenticate(headers or {})
    if result.succeeded:
        return None
    return _err("Unauthorized.", "authentication_error", "invalid_api_key", HTTP_UNAUTHORIZED)


def _estimate_tokens(text: str) -> int:
    if not text:
        return 0
    return max(1, len(text) // 4)


# ── Chat completions ──────────────────────────────────────────────────────


class ChatCompletionsHandler:
    """POST /v1/chat/completions. Port of ``ChatCompletionsEndpoint``."""

    __slots__ = ("_registry", "_admission", "_counters", "_options", "_auth")

    def __init__(
        self,
        registry: IInferenceServerModelRegistry,
        admission: AdmissionControl,
        counters: ServerCounters,
        options,
        auth: Optional[ApiKeyAuthHandler] = None,
    ) -> None:
        self._registry = registry
        self._admission = admission
        self._counters = counters
        self._options = options
        self._auth = auth

    async def handle(
        self, body: ChatCompletionRequest, headers: dict = None, ct: object = None
    ) -> EndpointResult:
        if self._auth is not None:
            denied = require_auth(self._auth, headers or {})
            if denied is not None:
                return denied

        if body is None or not body.model or not body.model.strip():
            return _err("Missing or empty 'model' field.", "invalid_request_error", "missing_model", HTTP_BAD_REQUEST)
        if body.messages is None or len(body.messages) == 0:
            return _err("Missing 'messages' array.", "invalid_request_error", "missing_messages", HTTP_BAD_REQUEST)

        bridge = self._registry.resolve(body.model)
        if bridge is None:
            return _err(f"Model '{body.model}' is not loaded.", "invalid_request_error", "model_not_found", HTTP_NOT_FOUND)

        slot = self._admission.try_enter()
        if slot is None:
            return EndpointResult(
                HTTP_SERVICE_UNAVAILABLE,
                ErrorResponse.of(
                    f"Server is at concurrency cap ({self._admission.max_concurrent_requests}). "
                    "Retry after a brief delay.",
                    "server_busy",
                    "concurrency_cap",
                ),
                headers={"Retry-After": "1"},
            )

        try:
            request = self._build_inference_request(body)
            if body.stream:
                return await self._stream_response(bridge, request, body, ct)
            return await self._non_stream_response(bridge, request, body, ct)
        finally:
            slot.release()

    async def _non_stream_response(
        self,
        bridge: IInferenceBridge,
        request: InferenceRequest,
        body: ChatCompletionRequest,
        ct: object,
    ) -> EndpointResult:
        try:
            resp = await bridge.complete_async(request, ct)
        except Exception as ex:  # noqa: BLE001 - mirror C# bridge_failure catch
            self._counters.account_failed()
            return _err(str(ex), "internal_error", "bridge_failure", HTTP_INTERNAL_ERROR)

        if resp.status == InferenceStatus.FAILED:
            self._counters.account_failed()
            return _err(
                resp.failure_message or "Inference failed.",
                "internal_error",
                "inference_failed",
                HTTP_INTERNAL_ERROR,
            )

        response = ChatCompletionResponse(
            id=f"chatcmpl-{uuid.uuid4().hex}",
            created=int(datetime.now(timezone.utc).timestamp()),
            model=body.model,
            choices=[
                ChatCompletionChoice(
                    index=0,
                    message=ChatCompletionMessage(
                        role="assistant",
                        content=resp.output_text,
                        reasoning_content=resp.reasoning_text,
                    ),
                    finish_reason=_map_finish(resp.status),
                )
            ],
            usage=UsageInfo(
                prompt_tokens=resp.prompt_token_count,
                completion_tokens=resp.output_token_count,
                total_tokens=resp.prompt_token_count + resp.output_token_count,
            ),
        )
        return EndpointResult(HTTP_OK, response)

    async def _stream_response(
        self,
        bridge: IInferenceBridge,
        request: InferenceRequest,
        body: ChatCompletionRequest,
        ct: object,
    ) -> EndpointResult:
        sse = ServerSentEventsWriter()
        sid = f"chatcmpl-{uuid.uuid4().hex}"
        created = int(datetime.now(timezone.utc).timestamp())

        # First frame: role announcement.
        await sse.write_async(
            ChatCompletionStreamChunk(
                id=sid,
                created=created,
                model=body.model,
                choices=[ChatCompletionStreamChoice(index=0, delta=ChatCompletionDelta(role="assistant"))],
            ),
            ct,
        )

        try:
            async for f in bridge.stream_fragments_async(request, ct):
                if not f.text:
                    continue
                delta = (
                    ChatCompletionDelta(reasoning_content=f.text)
                    if f.kind == InferenceFragmentKind.REASONING
                    else ChatCompletionDelta(content=f.text)
                )
                await sse.write_async(
                    ChatCompletionStreamChunk(
                        id=sid,
                        created=created,
                        model=body.model,
                        choices=[ChatCompletionStreamChoice(index=0, delta=delta)],
                    ),
                    ct,
                )
        except Exception as ex:  # noqa: BLE001 - mirror C# error frame path
            self._counters.account_failed()
            await sse.write_async(
                ChatCompletionStreamChunk(
                    id=sid,
                    created=created,
                    model=body.model,
                    choices=[
                        ChatCompletionStreamChoice(
                            index=0,
                            delta=ChatCompletionDelta(content=f"[error: {ex}]"),
                            finish_reason="error",
                        )
                    ],
                ),
            )

        # Final frame: stop + [DONE].
        await sse.write_async(
            ChatCompletionStreamChunk(
                id=sid,
                created=created,
                model=body.model,
                choices=[ChatCompletionStreamChoice(index=0, delta=ChatCompletionDelta(), finish_reason="stop")],
            ),
        )
        await sse.write_terminator_async()
        return EndpointResult(HTTP_OK, None, sse_frames=sse.frames)

    @staticmethod
    def _build_inference_request(body: ChatCompletionRequest) -> InferenceRequest:
        prompt = "\n".join(f"<|{m.role}|>\n{m.content}\n<|end|>" for m in body.messages)
        metadata = {}
        if body.user:
            metadata["user"] = body.user
        return InferenceRequest(
            id=uuid.uuid4(),
            model_id=body.model,
            prompt=prompt,
            max_output_tokens=body.max_tokens if body.max_tokens is not None else 512,
            temperature=body.temperature if body.temperature is not None else 0.7,
            top_p=body.top_p if body.top_p is not None else 0.9,
            stop_sequences=list(body.stop) if body.stop else [],
            metadata=metadata,
            requested_at=datetime.now(timezone.utc),
        )


def _map_finish(status: InferenceStatus) -> str:
    if status in (InferenceStatus.COMPLETED, InferenceStatus.STOPPED_BY_TOKEN):
        return "stop"
    if status == InferenceStatus.STOPPED_BY_LENGTH:
        return "length"
    if status == InferenceStatus.CANCELLED:
        return "cancelled"
    return "error"


# ── Embeddings ────────────────────────────────────────────────────────────


class EmbeddingsHandler:
    """POST /v1/embeddings. Port of ``EmbeddingsEndpoint``.

    ``resolve_embedder`` returns an object with ``generate_async(text) -> list[float]``.
    """

    __slots__ = ("_registry", "_admission", "_counters", "_auth")

    def __init__(
        self,
        registry: IInferenceServerModelRegistry,
        admission: AdmissionControl,
        counters: ServerCounters,
        auth: Optional[ApiKeyAuthHandler] = None,
    ) -> None:
        self._registry = registry
        self._admission = admission
        self._counters = counters
        self._auth = auth

    async def handle(
        self, body: EmbeddingsRequest, headers: dict = None, ct: object = None
    ) -> EndpointResult:
        if self._auth is not None:
            denied = require_auth(self._auth, headers or {})
            if denied is not None:
                return denied

        if body is None or not body.model or not body.model.strip():
            return _err("Missing or empty 'model' field.", "invalid_request_error", "missing_model", HTTP_BAD_REQUEST)

        embedder = self._registry.resolve_embedder(body.model)
        if embedder is None:
            return _err(
                f"Embedding model '{body.model}' is not loaded.",
                "invalid_request_error",
                "model_not_found",
                HTTP_NOT_FOUND,
            )

        ok, inputs, error = _normalise_input(body.input)
        if not ok:
            return EndpointResult(HTTP_BAD_REQUEST, error)

        slot = self._admission.try_enter()
        if slot is None:
            return _err("Server is at concurrency cap. Retry shortly.", "server_busy", "concurrency_cap", HTTP_SERVICE_UNAVAILABLE)

        try:
            data: List[EmbeddingDatum] = []
            total_chars = 0
            try:
                for i, text in enumerate(inputs):
                    vec = await embedder.generate_async(text, ct)
                    data.append(EmbeddingDatum(index=i, embedding=list(vec)))
                    total_chars += len(text)
            except Exception as ex:  # noqa: BLE001 - mirror C# embedding_failure
                self._counters.account_failed()
                return _err(str(ex), "internal_error", "embedding_failure", HTTP_INTERNAL_ERROR)

            estimated = max(1, total_chars // 4)
            return EndpointResult(
                HTTP_OK,
                EmbeddingsResponse(
                    data=data,
                    model=body.model,
                    usage=UsageInfo(prompt_tokens=estimated, completion_tokens=0, total_tokens=estimated),
                ),
            )
        finally:
            slot.release()


def _normalise_input(inp: Any):
    """Normalise ``input`` into a list of strings. Mirrors ``TryNormaliseInput``:
    accepts a single string or an array of strings; every array element must be
    a string; the array must be non-empty.
    """
    if isinstance(inp, str):
        return True, [inp], None
    if isinstance(inp, list):
        out: List[str] = []
        for el in inp:
            if not isinstance(el, str):
                return False, [], ErrorResponse.of(
                    "Every 'input' array element must be a string.",
                    "invalid_request_error",
                    "invalid_input",
                )
            out.append(el)
        if len(out) == 0:
            return False, [], ErrorResponse.of(
                "'input' array must not be empty.", "invalid_request_error", "invalid_input"
            )
        return True, out, None
    return False, [], ErrorResponse.of(
        "'input' must be a string or array of strings.", "invalid_request_error", "invalid_input"
    )


# ── Companion ─────────────────────────────────────────────────────────────


class CompanionTurnHandler:
    """POST /v1/companion/turn. Port of ``CompanionEndpoint``."""

    __slots__ = ("_resolver", "_admission", "_counters", "_auth")

    def __init__(
        self,
        resolver: ICompanionSessionResolver,
        admission: AdmissionControl,
        counters: ServerCounters,
        auth: Optional[ApiKeyAuthHandler] = None,
    ) -> None:
        self._resolver = resolver
        self._admission = admission
        self._counters = counters
        self._auth = auth

    async def handle(
        self, body: CompanionTurnRequest, headers: dict = None, ct: object = None
    ) -> EndpointResult:
        if self._auth is not None:
            denied = require_auth(self._auth, headers or {})
            if denied is not None:
                return denied

        if (
            body is None
            or not body.session_id
            or not body.session_id.strip()
            or not body.identity_id
            or not body.identity_id.strip()
            or not body.message
            or not body.message.strip()
        ):
            return _err(
                "session_id, identity_id, and message are all required.",
                "invalid_request_error",
                "missing_field",
                HTTP_BAD_REQUEST,
            )

        session = await self._resolver.resolve_async(body.session_id, body.identity_id, ct)
        if session is None:
            return _err(
                f"No Companion session for session_id='{body.session_id}', "
                f"identity_id='{body.identity_id}'.",
                "invalid_request_error",
                "session_not_found",
                HTTP_NOT_FOUND,
            )

        slot = self._admission.try_enter()
        if slot is None:
            return _err("Server is at concurrency cap. Retry shortly.", "server_busy", "concurrency_cap", HTTP_SERVICE_UNAVAILABLE)

        try:
            if body.stream:
                return await self._stream_reply(session, body, ct)

            try:
                reply = (
                    await session.agent_async(body.message)
                    if body.agentic
                    else await session.send_async(body.message)
                )
            except Exception as ex:  # noqa: BLE001 - mirror C# companion_failure
                self._counters.account_failed()
                return _err(str(ex), "internal_error", "companion_failure", HTTP_INTERNAL_ERROR)

            return EndpointResult(
                HTTP_OK,
                CompanionTurnResponse(
                    session_id=body.session_id,
                    reply=reply,
                    agentic=body.agentic,
                    turn_index=len(session.history),
                ),
            )
        finally:
            slot.release()

    async def _stream_reply(
        self, session: object, body: CompanionTurnRequest, ct: object
    ) -> EndpointResult:
        sse = ServerSentEventsWriter()
        try:
            async for chunk in session.stream_async(body.message):
                if not chunk:
                    continue
                await sse.write_async({"session_id": body.session_id, "delta": chunk}, ct)
        except Exception as ex:  # noqa: BLE001 - mirror C# error frame path
            self._counters.account_failed()
            await sse.write_async({"session_id": body.session_id, "error": str(ex)})
        await sse.write_terminator_async()
        return EndpointResult(HTTP_OK, None, sse_frames=sse.frames)


# ── Admin ─────────────────────────────────────────────────────────────────


class AdminHandler:
    """Admin lifecycle endpoints. Port of ``AdminEndpoints``.

    * :meth:`lifecycle` — GET /v1/admin/lifecycle
    * :meth:`load` — POST /v1/admin/models/load
    * :meth:`unload` — DELETE /v1/admin/models/{id}
    """

    __slots__ = ("_manager", "_factory", "_auth")

    def __init__(
        self,
        manager: IModelLifecycleManager,
        factory: IBridgeFactory,
        auth: Optional[ApiKeyAuthHandler] = None,
    ) -> None:
        self._manager = manager
        self._factory = factory
        self._auth = auth

    def lifecycle(self, headers: dict = None) -> EndpointResult:
        if self._auth is not None:
            denied = require_auth(self._auth, headers or {})
            if denied is not None:
                return denied
        resp = AdminLifecycleResponse(
            total_allocated_vram_bytes=self._manager.total_allocated_vram_bytes,
            total_allocated_ram_bytes=self._manager.total_allocated_ram_bytes,
            loaded=list(self._manager.list()),
        )
        return EndpointResult(HTTP_OK, resp)

    async def load(
        self, body: AdminLoadRequest, headers: dict = None, ct: object = None
    ) -> EndpointResult:
        if self._auth is not None:
            denied = require_auth(self._auth, headers or {})
            if denied is not None:
                return denied

        if body is None or not body.model_id or not body.model_id.strip():
            return _err("Missing 'modelId'.", "invalid_request_error", "missing_model", HTTP_BAD_REQUEST)

        backend = BackendKind.parse(body.backend)
        if backend is None:
            return _err(
                f"Unknown backend '{body.backend}'. Valid: Cpu, Cuda, Vulkan, "
                "OpenCL, Metal, Ascend, Cambricon, CoreML.",
                "invalid_request_error",
                "invalid_backend",
                HTTP_BAD_REQUEST,
            )

        tier = CapabilityTier.parse(body.tier)
        if tier is None:
            return _err(
                f"Unknown tier '{body.tier}'. Valid: Tier0_Tiny..Tier4_Frontier.",
                "invalid_request_error",
                "invalid_tier",
                HTTP_BAD_REQUEST,
            )

        async def _factory(cancel):
            return await self._factory.create_async(body.model_id, backend, tier, cancel)

        descriptor = ModelLoadDescriptor(
            model_id=body.model_id,
            backend=backend,
            requested_tier=tier,
            vram_required_bytes=max(0, body.vram_required_bytes),
            ram_required_bytes=max(0, body.ram_required_bytes),
            bridge_factory=_factory,
        )

        result = await self._manager.load_async(descriptor, ct)
        if result.outcome in (LoadOutcome.LOADED, LoadOutcome.ALREADY_LOADED):
            return EndpointResult(
                HTTP_OK,
                {
                    "outcome": result.outcome.name,
                    "state": result.state.to_dict() if result.state is not None else None,
                    "rationale": result.rationale,
                },
            )
        if result.outcome in (LoadOutcome.INSUFFICIENT_VRAM, LoadOutcome.INSUFFICIENT_RAM):
            return _err(result.rationale, "resource_exhausted", result.outcome.name, HTTP_INSUFFICIENT_STORAGE)
        if result.outcome == LoadOutcome.FACTORY_FAILED:
            return _err(result.rationale, "internal_error", "factory_failed", HTTP_INTERNAL_ERROR)
        return _err(result.rationale, "internal_error", "unknown", HTTP_INTERNAL_ERROR)

    async def unload(
        self, model_id: str, headers: dict = None, ct: object = None
    ) -> EndpointResult:
        if self._auth is not None:
            denied = require_auth(self._auth, headers or {})
            if denied is not None:
                return denied
        outcome = await self._manager.unload_async(model_id, ct)
        if outcome == UnloadOutcome.UNLOADED:
            return EndpointResult(HTTP_OK, {"outcome": "Unloaded", "modelId": model_id})
        if outcome == UnloadOutcome.NOT_LOADED:
            return _err(f"Model '{model_id}' is not loaded.", "invalid_request_error", "not_loaded", HTTP_NOT_FOUND)
        return _err("Unknown unload outcome.", "internal_error", "unknown", HTTP_INTERNAL_ERROR)
