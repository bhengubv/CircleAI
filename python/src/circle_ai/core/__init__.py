"""circle_ai.core — port of CircleAI.Core model-management runtime.

Public surface mirrors the C# CircleAI.Core assembly:
  * model runtime: IModelLoader/LocalModelLoader, IModelManager/LocalModelManager,
    IModelDownloader/ModelDownloader, IModelSource/ModelScopeSource/HuggingFaceSource,
    SourceDownloadHelper, SafeModelHandle + PlatformInterop (load_model),
  * engine: CircleEngine, ICircleModule, IEmbeddingService,
  * multi-tenant: ICircleAITenantContext, NullTenantContext, SingleTenantContext,
  * auditing: ICircleAIAuditLog, CircleAIAuditEntry, CircleAIAuditQuery,
    NoopAuditLog, LoggerAuditLog, CircleAIAuditing,
  * compression: ShardKvCodec, ShardCompressedFrame.
"""
from __future__ import annotations

from .audit_log import (
    CircleAIAuditEntry,
    CircleAIAuditQuery,
    CircleAIAuditing,
    ICircleAIAuditLog,
    LoggerAuditLog,
    NoopAuditLog,
)
from .circle_engine import CircleEngine, ICircleModule, IEmbeddingService
from .model_downloader import (
    BundleFileEntry,
    DownloadProgressReport,
    IModelDownloader,
    ModelDownloader,
    ModelEntry,
)
from .model_loader import (
    BundleFileInfo,
    IModelLoader,
    LocalModelLoader,
    ModelInfo,
)
from .model_manager import IModelManager, LocalModelManager
from .model_source import (
    DownloadProgress,
    Fetcher,
    HuggingFaceSource,
    IModelSource,
    ModelScopeSource,
    SourceDownloadHelper,
    local_file_fetcher,
    set_default_fetcher,
)
from .safe_model_handle import (
    SafeModelHandle,
    default_shim,
    load_model,
    set_native_loader,
)
from .shard_kv_codec import ShardCompressedFrame, ShardKvCodec
from .tenant_context import (
    ICircleAITenantContext,
    NullTenantContext,
    SingleTenantContext,
)

__all__ = [
    # model runtime
    "IModelLoader",
    "LocalModelLoader",
    "ModelInfo",
    "BundleFileInfo",
    "IModelManager",
    "LocalModelManager",
    "IModelDownloader",
    "ModelDownloader",
    "ModelEntry",
    "BundleFileEntry",
    "DownloadProgressReport",
    "IModelSource",
    "ModelScopeSource",
    "HuggingFaceSource",
    "SourceDownloadHelper",
    "DownloadProgress",
    "Fetcher",
    "local_file_fetcher",
    "set_default_fetcher",
    "SafeModelHandle",
    "load_model",
    "set_native_loader",
    "default_shim",
    # engine
    "CircleEngine",
    "ICircleModule",
    "IEmbeddingService",
    # multi-tenant
    "ICircleAITenantContext",
    "NullTenantContext",
    "SingleTenantContext",
    # auditing
    "ICircleAIAuditLog",
    "CircleAIAuditEntry",
    "CircleAIAuditQuery",
    "NoopAuditLog",
    "LoggerAuditLog",
    "CircleAIAuditing",
    # compression
    "ShardKvCodec",
    "ShardCompressedFrame",
]
