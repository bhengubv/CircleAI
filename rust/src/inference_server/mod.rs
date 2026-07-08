//! inference_server — CircleAI.Inference.Server (+ .Enterprise) Rust port.
//!
//! Ports the OpenAI-compatible inference server's contracts + routing logic as
//! in-memory handlers (no real socket server, per the porting brief):
//!   - [`bridge`]: the inference-bridge contract ([`bridge::IInferenceBridge`],
//!     [`bridge::InferenceRequest`]/[`bridge::InferenceResponse`],
//!     [`bridge::ModelDescriptor`], [`bridge::LocalProcessInferenceBridge`]) plus
//!     [`bridge::IBridgeFactory`] and the [`bridge::BackendKind`] /
//!     [`bridge::CapabilityTier`] enums.
//!   - [`openai`]: [`openai::ChatCompletionRequest`]/[`openai::ChatCompletionResponse`],
//!     [`openai::EmbeddingsRequest`]/[`openai::EmbeddingsResponse`],
//!     [`openai::ErrorResponse`].
//!   - [`auth`]: [`auth::ApiKeyAuthHandler`] + schemes.
//!   - [`registry`]: [`registry::IInferenceServerModelRegistry`].
//!   - [`lifecycle`]: [`lifecycle::IModelLifecycleManager`] +
//!     [`lifecycle::INativeRuntimeStatus`].
//!   - [`companion_resolver`]: [`companion_resolver::ICompanionSessionResolver`].
//!   - [`handlers`]: in-memory HTTP handlers for chat/embeddings/companion/admin.
//!   - [`enterprise`]: [`enterprise::ServerTier`], [`enterprise::ITenantRouter`],
//!     [`enterprise::IBatchScheduler`], [`enterprise::IModelShardPlanner`],
//!     [`enterprise::ICrossTierOffload`] + real + null impls.

pub mod auth;
pub mod bridge;
pub mod companion_resolver;
pub mod enterprise;
pub mod handlers;
pub mod lifecycle;
pub mod openai;
pub mod registry;

// -- Flat convenience re-exports ------------------------------------------------

pub use auth::{ApiKeyAuthHandler, ApiKeyOptions, AuthResult, AuthSchemes, Claim};
pub use bridge::{
    default_device_capabilities, estimate_token_count, BackendKind, BridgeFactoryError,
    CapabilityTier, DeterministicBridge, DeterministicBridgeFactory, DeviceCapabilities,
    IBridgeFactory, IInferenceBridge, InferenceFragment, InferenceFragmentKind, InferenceRequest,
    InferenceResponse, InferenceStatus, LocalProcessInferenceBridge, ModelDescriptor, ModelFormat,
    UnconfiguredBridgeFactory,
};
pub use companion_resolver::{
    CompanionTurn as CompanionSessionTurn, ICompanionSessionFactory as IServerCompanionSessionFactory,
    ICompanionSessionResolver, ICompanionTurnSession, InMemoryCompanionSession,
    InMemoryCompanionSessionFactory, InMemoryCompanionSessionResolver,
};
pub use enterprise::{
    BatchSlot, EvenSplitModelShardPlanner, IBatchScheduler, ICrossTierOffload, IModelShardPlanner,
    ITenantRouter, InMemoryBatchScheduler, NullBatchScheduler, NullCrossTierOffload,
    NullModelShardPlanner, NullTenantRouter, OffloadDecision, PolicyCrossTierOffload,
    RoundRobinTenantRouter, ServerTier, ShardDescriptor, TenantContext, TenantQuota,
};
pub use handlers::{
    build_inference_request, map_finish, AdmissionControl, AdmissionSlot, AdminHandler,
    AdminLifecycleResponse, AdminLoadRequest, ChatCompletionsHandler, ChatStreamResult,
    CompanionHandler, CompanionTurnRequest, CompanionTurnResponse, EmbeddingsHandler,
    HandlerResult, ServerCounters,
};
pub use lifecycle::{
    CapabilityProbe, HostProfile, IModelLifecycleManager, INativeRuntimeStatus, LoadOutcome,
    LoadResult, ModelLifecycleManager, ModelLoadDescriptor, ModelLoadState, NativeRuntimePaths,
    NativeRuntimeStatus, UnloadOutcome,
};
pub use openai::{
    ChatCompletionChoice, ChatCompletionDelta, ChatCompletionMessage, ChatCompletionRequest,
    ChatCompletionResponse, ChatCompletionStreamChoice, ChatCompletionStreamChunk, EmbeddingDatum,
    EmbeddingsRequest, EmbeddingsResponse, ErrorBody, ErrorResponse, UsageInfo,
};
pub use registry::{
    IInferenceServerModelRegistry, InferenceServerModelRegistry, SharedBridge, SharedEmbedder,
};
