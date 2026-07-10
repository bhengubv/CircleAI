// Hosting.swift
//
// IAIObserver + AIOptions.

import Foundation

public protocol IAIObserver: Sendable {
    func onStarted() async
    func onStopped() async
    func onChatCompleted(_ response: ChatResponse) async
    func onStreamStarted(_ modelId: String) async
    func onStreamCompleted(_ modelId: String, tokenCount: Int) async
    func onToolInvoked(_ toolName: String, success: Bool) async
    func onModelFetching(_ modelId: String, autoSelected: Bool) async
    func onUpgradeAvailable(_ upgrade: UpgradeInfo) async
}

/// No-op base. Conform and override only what you care about.
open class AIObserverBase: IAIObserver, @unchecked Sendable {
    public init() {}
    open func onStarted() async {}
    open func onStopped() async {}
    open func onChatCompleted(_ response: ChatResponse) async {}
    open func onStreamStarted(_ modelId: String) async {}
    open func onStreamCompleted(_ modelId: String, tokenCount: Int) async {}
    open func onToolInvoked(_ toolName: String, success: Bool) async {}
    open func onModelFetching(_ modelId: String, autoSelected: Bool) async {}
    open func onUpgradeAvailable(_ upgrade: UpgradeInfo) async {}
}

/// Configuration bag for the long-lived butler service. All fields have safe
/// defaults so callers can `AIOptions()` and get a working instance. Ported
/// from `AIOptions` (v2/v3 surface: sensorium, RAG, persona, feedback, agentic,
/// loopback, cloud fallback, thermal, scheduled tasks, affect, goals).
///
/// `@unchecked Sendable`: it holds reference-typed collaborators (tool bridge,
/// stores) that aren't themselves `Sendable`; the options bag is treated as an
/// immutable configuration snapshot, mirroring the C# init-only record.
public struct AIOptions: @unchecked Sendable {
    // Model
    public let modelId: String?
    public let modelPath: String?

    // Inference
    public let systemPrompt: String
    public let defaultGenerationOptions: GenerationOptions?
    public let contextSize: Int?
    public let threadCount: Int?
    public let warmOnStart: Bool

    // Tools
    public let toolBridge: (any IToolBridge)?

    // Observers
    public let observer: (any IAIObserver)?

    // v2.0 — Sensorium
    public let deviceContext: (any IDeviceContext)?
    public let catalogClient: ModelScopeCatalogClient?
    public let requiredCapabilities: ChatCapability
    public let checkForUpgradesOnStart: Bool
    public let modelStorageDirectory: String?

    // v2.0 — Memory / RAG
    public let episodicMemory: (any IEpisodicMemoryStore)?
    public let ragBuilder: RagContextBuilder?
    public let ragTopK: Int

    // v2.0 — Persona evolution
    public let personaStore: (any IPersonaStore)?
    public let personaUserId: String

    // v2.0 — Feedback
    public let feedbackStore: (any IFeedbackStore)?

    // v2.0 — Agentic loop
    public let agenticMaxIterations: Int?

    // Loopback endpoint
    public let loopbackPort: Int
    public let loopbackToken: String?

    // v2.1 — Model management
    public let modelStorageDir: String
    public let wifiOnlyModelDownload: Bool

    // v2.1 — Cloud fallback
    public let cloudFallbackEnabled: Bool
    public let cloudFallbackToken: String?
    public let cloudFallbackRamThresholdBytes: Int64

    // v3.0 — Scheduled tasks
    public let scheduledTaskStore: (any IScheduledTaskStore)?

    // v3.0 — Affect
    public let affectStore: (any IAffectStore)?

    // v3.0 — Goals
    public let goalStore: (any IGoalStore)?

    public init(
        modelId: String? = nil,
        modelPath: String? = nil,
        systemPrompt: String = "You are B!, a helpful on-device assistant.",
        defaultGenerationOptions: GenerationOptions? = nil,
        contextSize: Int? = nil,
        threadCount: Int? = nil,
        warmOnStart: Bool = true,
        toolBridge: (any IToolBridge)? = nil,
        observer: (any IAIObserver)? = nil,
        deviceContext: (any IDeviceContext)? = nil,
        catalogClient: ModelScopeCatalogClient? = nil,
        requiredCapabilities: ChatCapability = .defaultCap,
        checkForUpgradesOnStart: Bool = false,
        modelStorageDirectory: String? = nil,
        episodicMemory: (any IEpisodicMemoryStore)? = nil,
        ragBuilder: RagContextBuilder? = nil,
        ragTopK: Int = 5,
        personaStore: (any IPersonaStore)? = nil,
        personaUserId: String = "default",
        feedbackStore: (any IFeedbackStore)? = nil,
        agenticMaxIterations: Int? = nil,
        loopbackPort: Int = 0,
        loopbackToken: String? = nil,
        modelStorageDir: String? = nil,
        wifiOnlyModelDownload: Bool = true,
        cloudFallbackEnabled: Bool = false,
        cloudFallbackToken: String? = nil,
        cloudFallbackRamThresholdBytes: Int64 = 2 * 1024 * 1024 * 1024,
        scheduledTaskStore: (any IScheduledTaskStore)? = nil,
        affectStore: (any IAffectStore)? = nil,
        goalStore: (any IGoalStore)? = nil
    ) {
        self.modelId = modelId
        self.modelPath = modelPath
        self.systemPrompt = systemPrompt
        self.defaultGenerationOptions = defaultGenerationOptions
        self.contextSize = contextSize
        self.threadCount = threadCount
        self.warmOnStart = warmOnStart
        self.toolBridge = toolBridge
        self.observer = observer
        self.deviceContext = deviceContext
        self.catalogClient = catalogClient
        self.requiredCapabilities = requiredCapabilities
        self.checkForUpgradesOnStart = checkForUpgradesOnStart
        self.modelStorageDirectory = modelStorageDirectory
        self.episodicMemory = episodicMemory
        self.ragBuilder = ragBuilder
        self.ragTopK = ragTopK
        self.personaStore = personaStore
        self.personaUserId = personaUserId
        self.feedbackStore = feedbackStore
        self.agenticMaxIterations = agenticMaxIterations
        self.loopbackPort = loopbackPort
        self.loopbackToken = loopbackToken
        self.modelStorageDir = modelStorageDir
            ?? (URL(fileURLWithPath: FileManager.default.currentDirectoryPath)
                .appendingPathComponent("models").path)
        self.wifiOnlyModelDownload = wifiOnlyModelDownload
        self.cloudFallbackEnabled = cloudFallbackEnabled
        self.cloudFallbackToken = cloudFallbackToken
        self.cloudFallbackRamThresholdBytes = cloudFallbackRamThresholdBytes
        self.scheduledTaskStore = scheduledTaskStore
        self.affectStore = affectStore
        self.goalStore = goalStore
    }

    /// Generates a cryptographically random 32-byte token, base64-encoded. Used
    /// by `HttpLoopbackEndpoint` when `loopbackToken` is nil. Mirrors
    /// `AIOptions.GenerateRandomToken`.
    public static func generateRandomToken() -> String {
        var bytes = [UInt8](repeating: 0, count: 32)
        for i in 0..<bytes.count { bytes[i] = UInt8.random(in: 0...255) }
        return Data(bytes).base64EncodedString()
    }
}
