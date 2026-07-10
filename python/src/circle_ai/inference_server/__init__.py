"""circle_ai.inference_server — port of CircleAI.Inference.Server.

Ports the OpenAI-compatible inference server as in-memory handlers behind
interfaces (no socket server):
  * options: InferenceServerOptions + auth subtree,
  * counters + admission: ServerCounters, AdmissionControl,
  * DTOs: OpenAI chat/embeddings/error, companion, admin request/response,
  * auth: AuthSchemes, ApiKeyAuthHandler,
  * registry: IInferenceServerModelRegistry, InferenceServerModelRegistry,
  * lifecycle: BackendKind, CapabilityTier, HostProfile, ModelLifecycleManager,
  * native status: INativeRuntimeStatus, NativeRuntimeStatus,
  * companion resolver: ICompanionSessionResolver, InMemoryCompanionSessionResolver,
  * bridge factory: IBridgeFactory, UnconfiguredBridgeFactory, DeterministicBridgeFactory,
  * SSE writer + endpoint handlers.
"""
from .options import (
    ApiKeyOptions,
    AuthOptions,
    InferenceServerOptions,
    JwtOptions,
    SECTION_NAME,
)
from .counters import AdmissionControl, AdmissionSlot, ServerCounters
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
    ErrorBody,
    ErrorResponse,
    UsageInfo,
)
from .auth import ApiKeyAuthHandler, AuthenticateResult, AuthOutcome, AuthSchemes
from .registry import IInferenceServerModelRegistry, InferenceServerModelRegistry
from .native_status import INativeRuntimeStatus, NativeRuntimePaths, NativeRuntimeStatus
from .lifecycle import (
    BackendKind,
    CapabilityTier,
    GpuInfo,
    HostProfile,
    IHostProfileProbe,
    IModelLifecycleManager,
    LoadOutcome,
    LoadResult,
    ModelLifecycleManager,
    ModelLoadDescriptor,
    ModelLoadState,
    StaticHostProfileProbe,
    UnloadOutcome,
)
from .companion_resolver import (
    ICompanionSessionResolver,
    InMemoryCompanionSessionResolver,
)
from .bridge_factory import (
    DeterministicBridgeFactory,
    IBridgeFactory,
    UnconfiguredBridgeFactory,
)
from .sse import ServerSentEventsWriter
from .endpoints import (
    AdminHandler,
    ChatCompletionsHandler,
    CompanionTurnHandler,
    EmbeddingsHandler,
    EndpointResult,
    require_auth,
)

__all__ = [
    # options
    "InferenceServerOptions",
    "AuthOptions",
    "ApiKeyOptions",
    "JwtOptions",
    "SECTION_NAME",
    # counters + admission
    "ServerCounters",
    "AdmissionControl",
    "AdmissionSlot",
    # DTOs
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
    # auth
    "AuthSchemes",
    "AuthOutcome",
    "AuthenticateResult",
    "ApiKeyAuthHandler",
    # registry
    "IInferenceServerModelRegistry",
    "InferenceServerModelRegistry",
    # native status
    "INativeRuntimeStatus",
    "NativeRuntimeStatus",
    "NativeRuntimePaths",
    # lifecycle
    "BackendKind",
    "CapabilityTier",
    "GpuInfo",
    "HostProfile",
    "IHostProfileProbe",
    "StaticHostProfileProbe",
    "ModelLoadDescriptor",
    "ModelLoadState",
    "LoadOutcome",
    "LoadResult",
    "UnloadOutcome",
    "IModelLifecycleManager",
    "ModelLifecycleManager",
    # companion resolver
    "ICompanionSessionResolver",
    "InMemoryCompanionSessionResolver",
    # bridge factory
    "IBridgeFactory",
    "UnconfiguredBridgeFactory",
    "DeterministicBridgeFactory",
    # SSE + endpoints
    "ServerSentEventsWriter",
    "EndpointResult",
    "ChatCompletionsHandler",
    "EmbeddingsHandler",
    "CompanionTurnHandler",
    "AdminHandler",
    "require_auth",
]
