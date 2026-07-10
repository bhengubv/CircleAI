// HostingObservers.swift
//
// Port of the CircleAI.Hosting observer bridges + the InferenceBridge test
// double:
//   - PushAIObserver.cs    → IPushNotificationSender, PushAIObserver
//   - AetherAIObserver.cs  → ICircleAetherTransport, AetherAIObserver
//   - InferenceBridge/MockInferenceBridge.cs → MockInferenceBridge
//
// The bridges implement `IAIServiceObserver` (the event-record observer surface
// declared in HostingService.swift, which carries the exact C# `IAIObserver`
// event semantics — AIChatEvent etc.).

import Foundation

// =====================================================================
// PushAIObserver
// =====================================================================

/// Platform-agnostic push notification sender. Implement with an APN or FCM SDK
/// for real delivery. Ported from `IPushNotificationSender`.
public protocol IPushNotificationSender: Sendable {
    /// Send a push notification to `deviceToken`.
    func send(deviceToken: String, title: String, body: String) async throws
}

/// `IAIServiceObserver` that delivers butler responses as push notifications via
/// `IPushNotificationSender`. Ported from `PushAIObserver`.
public final class PushAIObserver: IAIServiceObserver, @unchecked Sendable {
    private static let maxBodyLength = 100

    private let sender: any IPushNotificationSender
    private let deviceToken: String

    public init(sender: any IPushNotificationSender, deviceToken: String) {
        precondition(!deviceToken.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
                     "Device token is required.")
        self.sender = sender
        self.deviceToken = deviceToken
    }

    public func onChatCompleted(_ event: AIChatEvent) async {
        sendResponse(event.response)
    }

    /// Sends an error push notification. Call from error-handling code that
    /// cannot surface through the standard observer lifecycle.
    public func onError(_ error: Error) {
        let msg = "\(error)"
        let body = Self.truncateWithEllipsis(msg)
        Task { try? await sender.send(deviceToken: deviceToken, title: "B! Error", body: body) }
    }

    private func sendResponse(_ fullResponse: String) {
        let body = Self.truncateWithEllipsis(fullResponse)
        Task { try? await sender.send(deviceToken: deviceToken, title: "B!", body: body) }
    }

    private static func truncateWithEllipsis(_ s: String) -> String {
        if s.count > maxBodyLength {
            return String(s.prefix(maxBodyLength)) + "…"
        }
        return s
    }
}

// =====================================================================
// AetherAIObserver
// =====================================================================

/// Publish/subscribe transport contract for the CircleAether mesh. Host packages
/// (AetherNet, Bluetooth, NearLink, gRPC) implement it. Ported from
/// `ICircleAetherTransport`.
public protocol ICircleAetherTransport: Sendable {
    /// Publish a payload to the given topic.
    func publish(topic: String, payload: Data) async throws
}

/// `IAIServiceObserver` that forwards butler events to a CircleAether mesh
/// transport. Ported from `AetherAIObserver`.
public final class AetherAIObserver: IAIServiceObserver, @unchecked Sendable {
    private let transport: any ICircleAetherTransport

    public init(transport: any ICircleAetherTransport) {
        self.transport = transport
    }

    public func onChatCompleted(_ event: AIChatEvent) async {
        let payload = Self.jsonBytes(["response": event.response])
        // Fire-and-forget — keep the callback non-blocking.
        Task { try? await transport.publish(topic: "butler/response", payload: payload) }
    }

    /// Publishes an error payload to the `butler/error` topic.
    public func onError(_ error: Error) {
        let payload = Self.jsonBytes([
            "error": String(describing: type(of: error)),
            "message": "\(error)",
        ])
        Task { try? await transport.publish(topic: "butler/error", payload: payload) }
    }

    private static func jsonBytes(_ dict: [String: Any]) -> Data {
        (try? JSONSerialization.data(withJSONObject: dict, options: [.sortedKeys])) ?? Data()
    }
}

// =====================================================================
// MockInferenceBridge (InferenceBridge test double)
// =====================================================================

/// Deterministic `IInferenceBridge` for tests. Returns the same canned output
/// for every call and reports a single fixed model as loaded. Reuses the bridge
/// contracts already ported in InferenceServer.swift. Ported from
/// `MockInferenceBridge`.
public final class MockInferenceBridge: IInferenceBridge, @unchecked Sendable {
    private let cannedOutput: String
    private let latencyMillis: Int
    private let descriptor: ModelDescriptor

    public init(cannedOutput: String, latencyMillis: Int = 0, modelId: String = "mock-model") {
        precondition(latencyMillis >= 0, "latencyMillis must be non-negative.")
        self.cannedOutput = cannedOutput
        self.latencyMillis = latencyMillis
        self.descriptor = ModelDescriptor(
            modelId: modelId,
            version: "mock-1.0.0",
            format: .unknown,
            contextWindowTokens: 4096,
            vocabSize: 32000,
            parameterCount: 0,
            quantisationLabel: nil,
            approximateMemoryBytes: 0)
    }

    /// The model descriptor this mock reports as loaded.
    public var mockDescriptor: ModelDescriptor { descriptor }

    public func listLoadedModels() async throws -> [ModelDescriptor] { [descriptor] }

    public func isModelLoaded(_ modelId: String) async throws -> Bool {
        precondition(!modelId.isEmpty)
        return descriptor.modelId == modelId
    }

    public func complete(_ request: InferenceRequest) async throws -> InferenceResponse {
        let started = Date()
        if latencyMillis > 0 {
            try await Task.sleep(nanoseconds: UInt64(latencyMillis) * 1_000_000)
        } else {
            try Task.checkCancellation()
        }
        let elapsed = Date().timeIntervalSince(started) * 1000.0
        return InferenceResponse(
            requestId: request.id,
            modelId: descriptor.modelId,
            outputText: cannedOutput,
            outputTokenCount: max(0, cannedOutput.count / 4),
            promptTokenCount: max(0, request.prompt.count / 4),
            status: .completed,
            inferenceMillis: elapsed,
            failureMessage: nil,
            completedAt: Date())
    }

    public func streamCompletion(_ request: InferenceRequest) -> AsyncStream<String> {
        let output = cannedOutput
        let latency = latencyMillis
        return AsyncStream { continuation in
            let task = Task {
                if latency > 0 {
                    try? await Task.sleep(nanoseconds: UInt64(latency) * 1_000_000)
                }
                if !Task.isCancelled { continuation.yield(output) }
                continuation.finish()
            }
            continuation.onTermination = { _ in task.cancel() }
        }
    }

    public func deviceCapabilities() async throws -> DeviceCapabilities {
        DeviceCapabilities(
            osName: "Mock",
            osVersion: "1.0",
            physicalMemoryBytes: 4 * 1024 * 1024 * 1024,
            cpuCoreCount: 1,
            hasGpu: false,
            gpuName: nil,
            gpuMemoryBytes: nil,
            hasNpu: false,
            npuName: nil,
            hasTransportLayerEncryption: true)
    }
}
