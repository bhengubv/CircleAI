"""OpenAI-compatible + CircleAI-native request/response DTOs.

Ports the DTOs under ``CircleAI.Inference.Server.Models``:
  * OpenAI chat: ChatCompletionRequest/Message/Response/Choice/Usage +
    stream chunk/choice/delta,
  * OpenAI embeddings: EmbeddingsRequest/Response/Datum,
  * error envelope: ErrorResponse/ErrorBody (+ ``of`` factory),
  * companion: CompanionTurnRequest/Response,
  * admin: AdminLoadRequest/AdminLifecycleResponse.

Each type exposes ``to_dict()`` producing the exact JSON shape (property names,
``WhenWritingNull`` omission) the C# ``System.Text.Json`` config emits, and the
request types expose ``from_dict()`` for parsing an inbound JSON body.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any, Dict, List, Optional

__all__ = [
    "ChatCompletionMessage",
    "ChatCompletionRequest",
    "UsageInfo",
    "ChatCompletionChoice",
    "ChatCompletionResponse",
    "ChatCompletionDelta",
    "ChatCompletionStreamChoice",
    "ChatCompletionStreamChunk",
    "EmbeddingsRequest",
    "EmbeddingDatum",
    "EmbeddingsResponse",
    "ErrorBody",
    "ErrorResponse",
    "CompanionTurnRequest",
    "CompanionTurnResponse",
    "AdminLoadRequest",
    "AdminLifecycleResponse",
]


# ── OpenAI chat completion ────────────────────────────────────────────────


@dataclass(slots=True)
class ChatCompletionMessage:
    """One message in the chat completion conversation. Mirrors
    ``ChatCompletionMessage``. ``reasoning_content`` is omitted from JSON when
    ``None`` (WhenWritingNull).
    """

    role: str = "user"
    content: str = ""
    name: Optional[str] = None
    reasoning_content: Optional[str] = None

    def to_dict(self) -> Dict[str, Any]:
        d: Dict[str, Any] = {"role": self.role, "content": self.content}
        if self.name is not None:
            d["name"] = self.name
        if self.reasoning_content is not None:
            d["reasoning_content"] = self.reasoning_content
        return d

    @staticmethod
    def from_dict(d: Dict[str, Any]) -> "ChatCompletionMessage":
        return ChatCompletionMessage(
            role=d.get("role", "user"),
            content=d.get("content", "") or "",
            name=d.get("name"),
            reasoning_content=d.get("reasoning_content"),
        )


@dataclass(slots=True)
class ChatCompletionRequest:
    """OpenAI-shaped chat-completion request body. Mirrors ``ChatCompletionRequest``."""

    model: str = ""
    messages: List[ChatCompletionMessage] = field(default_factory=list)
    temperature: Optional[float] = None
    top_p: Optional[float] = None
    max_tokens: Optional[int] = None
    stream: bool = False
    stop: Optional[List[str]] = None
    user: Optional[str] = None

    @staticmethod
    def from_dict(d: Dict[str, Any]) -> "ChatCompletionRequest":
        raw_msgs = d.get("messages") or []
        return ChatCompletionRequest(
            model=d.get("model", "") or "",
            messages=[ChatCompletionMessage.from_dict(m) for m in raw_msgs],
            temperature=d.get("temperature"),
            top_p=d.get("top_p"),
            max_tokens=d.get("max_tokens"),
            stream=bool(d.get("stream", False)),
            stop=list(d["stop"]) if d.get("stop") is not None else None,
            user=d.get("user"),
        )


@dataclass(slots=True)
class UsageInfo:
    """Token-usage block. Mirrors ``UsageInfo``."""

    prompt_tokens: int = 0
    completion_tokens: int = 0
    total_tokens: int = 0

    def to_dict(self) -> Dict[str, Any]:
        return {
            "prompt_tokens": self.prompt_tokens,
            "completion_tokens": self.completion_tokens,
            "total_tokens": self.total_tokens,
        }


@dataclass(slots=True)
class ChatCompletionChoice:
    """One choice in a non-streaming response. Mirrors ``ChatCompletionChoice``."""

    index: int = 0
    message: ChatCompletionMessage = field(default_factory=ChatCompletionMessage)
    finish_reason: str = "stop"

    def to_dict(self) -> Dict[str, Any]:
        return {
            "index": self.index,
            "message": self.message.to_dict(),
            "finish_reason": self.finish_reason,
        }


@dataclass(slots=True)
class ChatCompletionResponse:
    """OpenAI-shaped successful response. Mirrors ``ChatCompletionResponse``."""

    id: str = ""
    object: str = "chat.completion"
    created: int = 0
    model: str = ""
    choices: List[ChatCompletionChoice] = field(default_factory=list)
    usage: UsageInfo = field(default_factory=UsageInfo)

    def to_dict(self) -> Dict[str, Any]:
        return {
            "id": self.id,
            "object": self.object,
            "created": self.created,
            "model": self.model,
            "choices": [c.to_dict() for c in self.choices],
            "usage": self.usage.to_dict(),
        }


@dataclass(slots=True)
class ChatCompletionDelta:
    """Delta payload — only non-null fields are emitted. Mirrors ``ChatCompletionDelta``."""

    role: Optional[str] = None
    content: Optional[str] = None
    reasoning_content: Optional[str] = None

    def to_dict(self) -> Dict[str, Any]:
        d: Dict[str, Any] = {}
        if self.role is not None:
            d["role"] = self.role
        if self.content is not None:
            d["content"] = self.content
        if self.reasoning_content is not None:
            d["reasoning_content"] = self.reasoning_content
        return d


@dataclass(slots=True)
class ChatCompletionStreamChoice:
    """One delta in a streamed chunk. Mirrors ``ChatCompletionStreamChoice``.
    ``finish_reason`` is omitted when ``None`` (WhenWritingNull on Delta fields;
    the choice's finish_reason is nullable and omitted when null).
    """

    index: int = 0
    delta: ChatCompletionDelta = field(default_factory=ChatCompletionDelta)
    finish_reason: Optional[str] = None

    def to_dict(self) -> Dict[str, Any]:
        d: Dict[str, Any] = {"index": self.index, "delta": self.delta.to_dict()}
        if self.finish_reason is not None:
            d["finish_reason"] = self.finish_reason
        return d


@dataclass(slots=True)
class ChatCompletionStreamChunk:
    """One SSE delta frame. Mirrors ``ChatCompletionStreamChunk``."""

    id: str = ""
    object: str = "chat.completion.chunk"
    created: int = 0
    model: str = ""
    choices: List[ChatCompletionStreamChoice] = field(default_factory=list)

    def to_dict(self) -> Dict[str, Any]:
        return {
            "id": self.id,
            "object": self.object,
            "created": self.created,
            "model": self.model,
            "choices": [c.to_dict() for c in self.choices],
        }


# ── OpenAI embeddings ─────────────────────────────────────────────────────


@dataclass(slots=True)
class EmbeddingsRequest:
    """OpenAI-shaped embeddings request. Mirrors ``EmbeddingsRequest``.

    ``input`` is either a string or a list of strings (the C# ``JsonElement``).
    """

    model: str = ""
    input: Any = None
    user: Optional[str] = None

    @staticmethod
    def from_dict(d: Dict[str, Any]) -> "EmbeddingsRequest":
        return EmbeddingsRequest(
            model=d.get("model", "") or "",
            input=d.get("input"),
            user=d.get("user"),
        )


@dataclass(slots=True)
class EmbeddingDatum:
    """One embedding row. Mirrors ``EmbeddingDatum``."""

    index: int = 0
    embedding: List[float] = field(default_factory=list)
    object: str = "embedding"

    def to_dict(self) -> Dict[str, Any]:
        return {"object": self.object, "index": self.index, "embedding": list(self.embedding)}


@dataclass(slots=True)
class EmbeddingsResponse:
    """OpenAI-shaped embeddings response. Mirrors ``EmbeddingsResponse``."""

    data: List[EmbeddingDatum] = field(default_factory=list)
    model: str = ""
    usage: UsageInfo = field(default_factory=UsageInfo)
    object: str = "list"

    def to_dict(self) -> Dict[str, Any]:
        return {
            "object": self.object,
            "data": [d.to_dict() for d in self.data],
            "model": self.model,
            "usage": self.usage.to_dict(),
        }


# ── Error envelope ────────────────────────────────────────────────────────


@dataclass(slots=True)
class ErrorBody:
    """Inner error body. Mirrors ``ErrorBody``."""

    message: str = ""
    type: str = "invalid_request_error"
    param: Optional[str] = None
    code: Optional[str] = None

    def to_dict(self) -> Dict[str, Any]:
        return {
            "message": self.message,
            "type": self.type,
            "param": self.param,
            "code": self.code,
        }


@dataclass(slots=True)
class ErrorResponse:
    """OpenAI-shaped error envelope ``{"error": {...}}``. Mirrors ``ErrorResponse``."""

    error: ErrorBody = field(default_factory=ErrorBody)

    @staticmethod
    def of(message: str, type: str, code: Optional[str] = None) -> "ErrorResponse":
        return ErrorResponse(error=ErrorBody(message=message, type=type, code=code))

    def to_dict(self) -> Dict[str, Any]:
        return {"error": self.error.to_dict()}


# ── Companion ─────────────────────────────────────────────────────────────


@dataclass(slots=True)
class CompanionTurnRequest:
    """POST /v1/companion/turn request. Mirrors ``CompanionTurnRequest``."""

    session_id: str = ""
    identity_id: str = ""
    message: str = ""
    stream: bool = False
    agentic: bool = False

    @staticmethod
    def from_dict(d: Dict[str, Any]) -> "CompanionTurnRequest":
        return CompanionTurnRequest(
            session_id=d.get("session_id", "") or "",
            identity_id=d.get("identity_id", "") or "",
            message=d.get("message", "") or "",
            stream=bool(d.get("stream", False)),
            agentic=bool(d.get("agentic", False)),
        )


@dataclass(slots=True)
class CompanionTurnResponse:
    """POST /v1/companion/turn response. Mirrors ``CompanionTurnResponse``."""

    session_id: str = ""
    reply: str = ""
    agentic: bool = False
    turn_index: int = 0

    def to_dict(self) -> Dict[str, Any]:
        return {
            "session_id": self.session_id,
            "reply": self.reply,
            "agentic": self.agentic,
            "turn_index": self.turn_index,
        }


# ── Admin ─────────────────────────────────────────────────────────────────


@dataclass(slots=True)
class AdminLoadRequest:
    """Request body for POST /v1/admin/models/load. Mirrors ``AdminLoadRequest``."""

    model_id: str = ""
    backend: str = "Cpu"
    tier: str = "Tier1_Small"
    vram_required_bytes: int = 0
    ram_required_bytes: int = 0

    @staticmethod
    def from_dict(d: Dict[str, Any]) -> "AdminLoadRequest":
        return AdminLoadRequest(
            model_id=d.get("model_id", d.get("modelId", "")) or "",
            backend=d.get("backend", d.get("Backend", "Cpu")) or "Cpu",
            tier=d.get("tier", d.get("Tier", "Tier1_Small")) or "Tier1_Small",
            vram_required_bytes=int(d.get("vram_required_bytes", d.get("VramRequiredBytes", 0)) or 0),
            ram_required_bytes=int(d.get("ram_required_bytes", d.get("RamRequiredBytes", 0)) or 0),
        )


@dataclass(slots=True)
class AdminLifecycleResponse:
    """Response body for /v1/admin/lifecycle. Mirrors ``AdminLifecycleResponse``."""

    total_allocated_vram_bytes: int = 0
    total_allocated_ram_bytes: int = 0
    loaded: List[Any] = field(default_factory=list)

    def to_dict(self) -> Dict[str, Any]:
        return {
            "total_allocated_vram_bytes": self.total_allocated_vram_bytes,
            "total_allocated_ram_bytes": self.total_allocated_ram_bytes,
            "loaded": [
                (s.to_dict() if hasattr(s, "to_dict") else s) for s in self.loaded
            ],
        }
