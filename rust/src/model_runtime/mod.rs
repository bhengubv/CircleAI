//! model_runtime — CircleAI.Core model-management runtime (Rust port).
//!
//! Faithful ports of the `CircleAI.Core` model-management surface:
//!   - Loaders:    [`IModelLoader`], [`LocalModelLoader`]
//!   - Managers:   [`IModelManager`], [`LocalModelManager`]
//!   - Downloader: [`IModelDownloader`], [`ModelDownloader`]
//!   - Sources:    [`IModelSource`], [`ModelScopeSource`], [`HuggingFaceSource`]
//!                 (removed tombstone), [`SourceDownloadHelper`]
//!   - Handles:    [`SafeModelHandle`], [`PlatformInterop`]
//!   - Facade:     [`CircleEngine`], [`ICircleModule`], [`IEmbeddingService`]
//!   - Tenancy:    [`ICircleAITenantContext`], [`NullTenantContext`],
//!                 [`SingleTenantContext`]
//!   - Auditing:   [`ICircleAIAuditLog`], [`LoggerAuditLog`], [`NoopAuditLog`],
//!                 [`CircleAIAuditing`]
//!   - Compression:[`ShardKvCodec`] (byte-exact) — note the TurboQuant codec is
//!                 already ported under `crate::memory::compression`.
//!
//! Networking/native are injected behind interfaces ([`ContentProvider`],
//! [`NativeModelLoader`]) with deterministic in-memory defaults, per the no-real-IO
//! porting brief.

pub mod auditing;
pub mod dotnet_random;
pub mod downloader;
pub mod engine;
pub mod loader;
pub mod manager;
pub mod platform_interop;
pub mod shard_kv_codec;
pub mod sources;
pub mod tenant;

// -- Flat convenience re-exports ------------------------------------------------

pub use auditing::{
    outcomes, CircleAIAuditEntry, CircleAIAuditQuery, CircleAIAuditing, ICircleAIAuditLog, LogSink,
    LoggerAuditLog, NoopAuditLog,
};
pub use dotnet_random::DotNetRandom;
pub use downloader::{
    BundleFileEntry, DownloadProgressReport, IModelDownloader, ModelDownloader, ModelEntry,
    SharedDownloader,
};
pub use engine::{CircleEngine, ICircleModule, IEmbeddingService};
pub use loader::{
    BundleFileInfo, IModelLoader, LocalModelLoader, ModelInfo, ModelLoaderError,
};
pub use manager::{IModelManager, LocalModelManager};
pub use platform_interop::{
    InMemoryNativeLoader, InteropError, NativeModelLoader, PlatformInterop, ReleaseCallback,
    SafeModelHandle,
};
pub use shard_kv_codec::{ShardCodecError, ShardCompressedFrame, ShardKvCodec};
pub use sources::{
    ContentProvider, DownloadProgress, HuggingFaceSource, IModelSource, InMemoryContentProvider,
    ModelScopeSource, ProgressSink, SourceDownloadHelper, SourceError,
};
pub use tenant::{
    ICircleAITenantContext, NoTenantError, NullTenantContext, SingleTenantContext,
};
