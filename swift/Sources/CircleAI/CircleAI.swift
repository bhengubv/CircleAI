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
//   Tools/Tools.swift         — ToolDefinition, ToolParameter, ToolInvocation,
//                               ToolResult, IToolBridge,
//                               FaceExpressionClassification, FaceBoundingBox,
//                               FacialMetricMatrix
//   Sync/Sync.swift           — SyncDeliveryMode, SyncDomainKeys, SyncDelta,
//                               ISyncChannel
//
// Swift package: CircleAI
// Minimum platforms: macOS 13, iOS 16, watchOS 9
// Language standard: Swift 5.9+, Swift Concurrency (async/await, AsyncStream)
// External dependencies: none

import Foundation
