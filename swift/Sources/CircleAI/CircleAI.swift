// CircleAI.swift
// Top-level re-export and package documentation for the CircleAI Swift SDK.
//
// All public symbols are declared in the module files below and are
// automatically available to consumers who import CircleAI:
//
//   Models/Models.swift       — ChatMessage, DownloadProgress
//   Memory/Memory.swift       — AffectState, PersonaState, EpisodicMemoryEntry,
//                               FeedbackSignal, FeedbackPolarity,
//                               Goal, GoalStatus, GoalPriority,
//                               IAffectStore, IPersonaStore,
//                               IEpisodicMemoryStore, IFeedbackStore, IGoalStore
//   Identity/Identity.swift   — IdentityTier, CircleIdentity, RegisteredDevice,
//                               BiometricProfile, BiometricMatcher,
//                               IBiometricStore, IIdentityStore, IIdentityProvider
//   Languages/Languages.swift — WritingSystem, LanguageTag, DetectionResult,
//                               ScriptNormalisationResult, KnownLanguages,
//                               ILanguageDetector, ILanguageRegistry
//   Companion/Companion.swift — InterfaceKind, CompanionContext, CompanionTurn,
//                               CompanionProactiveEvent, FaceAffectMapper,
//                               FaceCompanionBridge, ICompanionSession
//   Inference/Inference.swift — GenerationOptions, IChatGenerator
//   WorldModel.swift          — CausalPrediction, IWorldModel,
//                               FrequencyWorldModel, BayesianWorldModel
//   PredictiveEngine.swift    — AnticipatedNeed, IPredictiveEngine,
//                               HistogramPredictiveEngine, SequencePredictiveEngine
//   InnerMonologue.swift      — SelfReflection, IInnerMonologue,
//                               TemplateInnerMonologue, ReasoningLoopInnerMonologue
//   TheoryOfMind.swift        — OtherMindEstimate, ITheoryOfMind,
//                               BeliefTrackerTheoryOfMind
//   Tools/Tools.swift         — ToolDefinition, ToolParameter, ToolInvocation,
//                               ToolResult, IToolBridge,
//                               FaceExpressionClassification, FaceBoundingBox,
//                               FacialMetricMatrix
//   Sync/Sync.swift           — SyncDeliveryMode, SyncDomainKeys, SyncDelta,
//                               ISyncChannel
//   CompanionStateSync.swift  — HybridLogicalClock, SyncEnvelope(Kind),
//                               StateVectorEntry, RequestItem, SyncableEntry,
//                               ISyncableEntryStore, InMemorySyncableEntryStore,
//                               ICompanionStateChannel, InProcessSyncHub,
//                               InProcessCompanionStateChannel,
//                               ICompanionStateSyncEngine, CompanionStateSyncEngine,
//                               PersonaStateSyncBridge, LoraAdapterSyncBridge,
//                               CompanionConversationSyncBridge
//   CompanionRuntime.swift    — CompanionRuntime, CompanionRuntimeOptions
//   MemorySyncService.swift   — IMemorySyncService, MemorySyncService,
//                               MemoryDeltaCodec
//   MemoryStores.swift        — InMemoryEpisodicStore, InMemoryPersonaStore,
//                               InMemoryFeedbackStore, InMemoryGoalStore
//   CircleEngine.swift        — CircleEngine, ICircleModule, IEmbeddingService
//   ModelRuntime.swift        — IModelLoader/LocalModelLoader,
//                               IModelManager/LocalModelManager,
//                               IModelDownloader/ModelDownloader,
//                               IModelSource/ModelScopeSource/HuggingFaceSource,
//                               SourceDownloadHelper, IByteSource,
//                               SafeModelHandle, PlatformInterop,
//                               ModelInfoEntry, SourceDownloadProgress
//   MultiTenant.swift         — ICircleAITenantContext, NullTenantContext,
//                               SingleTenantContext
//   Auditing.swift            — ICircleAIAuditLog, CircleAIAuditEntry,
//                               CircleAIAuditQuery, NoopAuditLog, LoggerAuditLog,
//                               CircleAIAuditing, ICircleAILogger
//   ShardKvCodec.swift        — ShardCompressedFrame, ShardKvCodec, DotNetRandom
//   EmbeddingStore.swift      — IEmbeddingEncoder, ICircleEmbeddingStore,
//                               IEmbeddingIndex, EmbeddingDocument,
//                               EmbeddingSearchHit, EmbeddingIndexHit,
//                               InMemoryEmbeddingStore, InMemoryEmbeddingIndex
//   (CircleAI.Embeddings.ITextEmbedder is already present as `ITextEmbedder`
//    in Rag.swift — same async `[Float]` contract — so it is not redeclared.)
//
// Swift package: CircleAI
// Minimum platforms: macOS 13, iOS 16, watchOS 9
// Language standard: Swift 5.9+, Swift Concurrency (async/await, AsyncStream)
// External dependencies: none

import Foundation
