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
//   Security.swift            — ThreatVector, AnomalySignal (local-runtime primitives)
//   PeerSecurity.swift        — Peer* enums/DTOs (PeerSecurityEventKind,
//                               PeerThreatLevel, PeerDirectiveKind,
//                               PeerSecurityEvent, PeerDirective,
//                               PeerTrustScoreUpdate, PeerSecurityPosture,
//                               PeerNetworkHealthReport, PeerThreatAssessment,
//                               PeerRoutingAdvice), IPeerDirectiveConsumer,
//                               IPeerSecurityLayer, IPeerIntelligence,
//                               IPeerSecurityEventFeed, IDirectiveSubscription,
//                               ThreatDetector, SecurityOptions, NodeTrustEntry,
//                               NodeTrustRegistry, DirectivePublisher,
//                               SecurityLayerService, PeerIntelligenceService
//   SecurityWatchdog.swift    — SecurityCheckpoint, SecurityResponseKind,
//                               SecurityResponse, UhidKeyRing (+Error),
//                               RedactedEvidenceJsonConverter,
//                               AnomalySignal:Encodable (redacting),
//                               ISecurityWatchdog, DefaultSecurityWatchdog,
//                               AnomalyDispatchOutcome, AnomalyDispatchResult,
//                               IAnomalyEventDispatcher, DefaultAnomalyEventDispatcher
//   Aether.swift              — CircleAI.Aether contracts: AetherThreatLevel and
//                               all event DTOs (Node/Transport/Route/Security/
//                               Network), IAetherTelemetry(+Observer)/
//                               NullAetherTelemetry/InMemoryAetherTelemetry,
//                               AetherInstallLevel/IAetherContext/
//                               InMemoryAetherContext/SemanticVersion,
//                               IAetherIntelligence (+NetworkHealthReport,
//                               ThreatAssessment, RoutingAdvice, TrustScoreUpdate),
//                               SecurityDirectiveKind/SecurityDirective/
//                               SecurityPosture/ISecurityDirectiveConsumer/
//                               IAISecurityLayer, AuthChallengeReason/AuthMethod/
//                               AuthChallengeResult/IAuthChallenge/
//                               InMemoryAuthChallenge, IAetherSubscription
//   MeshCapabilityRegistry.swift — CircleAI.AetherNet mesh discovery:
//                               MeshCapabilityAdvertisement, IMeshCapabilityRegistry,
//                               InMemoryMeshCapabilityRegistry, MeshCapabilityError,
//                               IMeshCapabilityBroadcaster,
//                               NullMeshCapabilityBroadcaster,
//                               LoopbackMeshCapabilityBroadcaster
//   SecurityAetherNet.swift   — CircleAI.Security.AetherNet bindings:
//                               MeshDirectiveStore, MeshSecurityGate(+GateDecision),
//                               MeshSecurityBlockedError, MeshGatedCompanionSession,
//                               AetherMapper, AetherIntelligenceAdapter,
//                               AetherSecurityBridge
//   Vision.swift              — CircleAI.Vision contracts: BoundingBox,
//                               LandmarkPoint, DetectedFace, FaceEmbedding,
//                               LivenessResult, DocumentField,
//                               DocumentVerificationResult, PlateRecognitionResult,
//                               BluetoothAnomaly, VideoPixelFormat, VideoFrame,
//                               IVideoCapture/NullVideoCapture,
//                               IComputerVisionRuntime/NullComputerVisionRuntime,
//                               IFaceDetector/NullFaceDetector,
//                               IFaceEmbedder/NullFaceEmbedder,
//                               IFaceLivenessDetector/NullFaceLivenessDetector,
//                               IDocumentVerifier/NullDocumentVerifier,
//                               IPlateRecognizer/NullPlateRecognizer,
//                               IBluetoothAnomalyDetector(+Subscription)/
//                               NullBluetoothAnomalyDetector
//   VisionOnnx.swift          — CircleAI.Vision ONNX backends: RgbImage,
//                               IImageDecoder, DenseTensorF, IOnnxTensorRunner,
//                               VisionGeometry (letterbox/tensor/YOLO/NMS/IoU/
//                               clamp/L2), OnnxFaceDetector(+Options),
//                               OnnxFaceEmbedder(+Options),
//                               OnnxPlateRecognizer(+Options)
//   VisionCloud.swift         — CircleAI.Vision.Cloud image generation:
//                               ImageGenerationRequest, ImageArtifact,
//                               IImageGenerator/NullImageGenerator,
//                               OpenAiImageOptions, StabilityImageOptions,
//                               ImageGeneratorIds, ImageHttpResponse/
//                               ImageHttpFormField/IImageHttpTransport,
//                               OpenAiImageGenerator, StabilityImageGenerator,
//                               ImageGeneratorFallbackChain,
//                               LocalDeterministicImageGenerator
//   Video.swift               — CircleAI.Video contracts: StyleId,
//                               VideoResolution, StyleReferenceFrame,
//                               StyleAttribution, StyleReference, AudioTrack,
//                               VideoGenerationRequest, VideoGenerationResult,
//                               StyleScriptRequest, StyleScriptResult,
//                               VideoGenerationError,
//                               IVideoGenerator/NullVideoGenerator,
//                               IStyleScript/NullStyleScript,
//                               IStyleReference/InMemoryStyleReference
//
// Swift package: CircleAI
// Minimum platforms: macOS 13, iOS 16, watchOS 9
// Language standard: Swift 5.9+, Swift Concurrency (async/await, AsyncStream)
// External dependencies: none

import Foundation
