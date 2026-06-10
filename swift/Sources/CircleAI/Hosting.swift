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

public struct AIOptions: Sendable {
    public let modelId: String?
    public let modelPath: String?
    public let systemPrompt: String
    public let contextSize: Int?
    public let threadCount: Int?
    public let warmOnStart: Bool
    public let deviceContext: (any IDeviceContext)?
    public let catalogClient: ModelScopeCatalogClient?
    public let requiredCapabilities: ChatCapability
    public let agenticMaxIterations: Int?
    public let observer: (any IAIObserver)?
    public let checkForUpgradesOnStart: Bool
    public let modelStorageDirectory: String?

    public init(
        modelId: String? = nil,
        modelPath: String? = nil,
        systemPrompt: String = "You are B!, a helpful on-device assistant.",
        contextSize: Int? = nil,
        threadCount: Int? = nil,
        warmOnStart: Bool = true,
        deviceContext: (any IDeviceContext)? = nil,
        catalogClient: ModelScopeCatalogClient? = nil,
        requiredCapabilities: ChatCapability = .defaultCap,
        agenticMaxIterations: Int? = nil,
        observer: (any IAIObserver)? = nil,
        checkForUpgradesOnStart: Bool = false,
        modelStorageDirectory: String? = nil
    ) {
        self.modelId = modelId; self.modelPath = modelPath; self.systemPrompt = systemPrompt
        self.contextSize = contextSize; self.threadCount = threadCount; self.warmOnStart = warmOnStart
        self.deviceContext = deviceContext; self.catalogClient = catalogClient
        self.requiredCapabilities = requiredCapabilities; self.agenticMaxIterations = agenticMaxIterations
        self.observer = observer; self.checkForUpgradesOnStart = checkForUpgradesOnStart
        self.modelStorageDirectory = modelStorageDirectory
    }
}
