// InferenceServer.swift
//
// CircleAI.Inference.Server port. Because Swift has no ASP.NET Core, the HTTP
// server is expressed as in-memory handlers behind interfaces — the routing,
// validation, admission, auth, streaming, and OpenAI wire contracts are all
// ported faithfully; only the socket/DI plumbing is replaced by direct calls.
//
// Contents:
//   • Bridge contracts        — ModelFormat, ModelDescriptor, DeviceCapabilities,
//                               InferenceRequest/Response/Status, InferenceFragment,
//                               IInferenceBridge, LocalProcessInferenceBridge.
//   • Bridge factory          — IBridgeFactory, UnconfiguredBridgeFactory.
//   • OpenAI DTOs             — ChatCompletion*, Embeddings*, ErrorResponse.
//   • Server registry         — IInferenceServerModelRegistry + impl.
//   • Lifecycle               — descriptors, IModelLifecycleManager + impl,
//                               IHostResourceProbe.
//   • Native runtime status   — INativeRuntimeStatus + impl.
//   • Companion resolver       — ICompanionSessionResolver + in-memory impl.
//   • Admission / counters    — ServerCounters, AdmissionControl.
//   • Auth                    — ApiKeyOptions, ApiKeyAuthHandler (constant-time).
//   • Handlers                — ChatCompletionsHandler, EmbeddingsHandler,
//                               CompanionHandler, AdminHandler (the routing logic
//                               of the C# endpoints, returning typed results).

import Foundation

// ─────────────────────────────────────────────────────────────────────────────
// MARK: - Backend / tier (mirrors CircleAI.Runtime.Backends)
// ─────────────────────────────────────────────────────────────────────────────

/// MNN execution backend. Values match the Alibaba MNN runtime layout.
public enum BackendKind: Int, Sendable, CaseIterable {
    case cpu = 0, cuda = 1, vulkan = 2, openCL = 3, metal = 4, ascend = 5, cambricon = 6, coreML = 7

    /// Case-insensitive parse mirroring `Enum.TryParse<BackendKind>`.
    public static func parse(_ s: String) -> BackendKind? {
        switch s.lowercased() {
        case "cpu": return .cpu
        case "cuda": return .cuda
        case "vulkan": return .vulkan
        case "opencl": return .openCL
        case "metal": return .metal
        case "ascend": return .ascend
        case "cambricon": return .cambricon
        case "coreml": return .coreML
        default: return nil
        }
    }

    public var name: String {
        switch self {
        case .cpu: return "Cpu"
        case .cuda: return "Cuda"
        case .vulkan: return "Vulkan"
        case .openCL: return "OpenCL"
        case .metal: return "Metal"
        case .ascend: return "Ascend"
        case .cambricon: return "Cambricon"
        case .coreML: return "CoreML"
        }
    }

    /// GPU-class backends enforce VRAM admission.
    var isGpuClass: Bool {
        self == .cuda || self == .vulkan || self == .metal || self == .openCL
    }
}

/// Capability tier that maps to a model size band.
public enum CapabilityTier: Int, Sendable, CaseIterable {
    case tier0Tiny = 0, tier1Small = 1, tier2Medium = 2, tier3Large = 3, tier4Frontier = 4

    /// Case-insensitive parse mirroring `Enum.TryParse<CapabilityTier>`.
    public static func parse(_ s: String) -> CapabilityTier? {
        switch s.lowercased().replacingOccurrences(of: " ", with: "") {
        case "tier0_tiny", "tier0tiny": return .tier0Tiny
        case "tier1_small", "tier1small": return .tier1Small
        case "tier2_medium", "tier2medium": return .tier2Medium
        case "tier3_large", "tier3large": return .tier3Large
        case "tier4_frontier", "tier4frontier": return .tier4Frontier
        default: return nil
        }
    }

    public var name: String {
        switch self {
        case .tier0Tiny: return "Tier0_Tiny"
        case .tier1Small: return "Tier1_Small"
        case .tier2Medium: return "Tier2_Medium"
        case .tier3Large: return "Tier3_Large"
        case .tier4Frontier: return "Tier4_Frontier"
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// MARK: - Bridge contracts (CircleAI.Hosting.InferenceBridge)
// ─────────────────────────────────────────────────────────────────────────────

/// On-disk encoding format of a model weight artefact.
public enum ModelFormat: Int, Sendable, Codable {
    case gguf = 0, onnx = 1, coreMl = 2, tflite = 3, unknown = 4
}

/// Canonical descriptor for a single loaded model.
public struct ModelDescriptor: Sendable, Equatable, Codable {
    public let modelId: String
    public let version: String
    public let format: ModelFormat
    public let contextWindowTokens: Int
    public let vocabSize: Int
    public let parameterCount: Int64
    public let quantisationLabel: String?
    public let approximateMemoryBytes: Int64

    public init(
        modelId: String, version: String, format: ModelFormat,
        contextWindowTokens: Int, vocabSize: Int, parameterCount: Int64,
        quantisationLabel: String?, approximateMemoryBytes: Int64
    ) {
        self.modelId = modelId
        self.version = version
        self.format = format
        self.contextWindowTokens = contextWindowTokens
        self.vocabSize = vocabSize
        self.parameterCount = parameterCount
        self.quantisationLabel = quantisationLabel
        self.approximateMemoryBytes = approximateMemoryBytes
    }
}

/// Static-ish capabilities report from the device hosting the bridge.
public struct DeviceCapabilities: Sendable, Equatable, Codable {
    public let osName: String
    public let osVersion: String
    public let physicalMemoryBytes: Int64
    public let cpuCoreCount: Int
    public let hasGpu: Bool
    public let gpuName: String?
    public let gpuMemoryBytes: Int64?
    public let hasNpu: Bool
    public let npuName: String?
    public let hasTransportLayerEncryption: Bool

    public init(
        osName: String, osVersion: String, physicalMemoryBytes: Int64, cpuCoreCount: Int,
        hasGpu: Bool, gpuName: String?, gpuMemoryBytes: Int64?, hasNpu: Bool,
        npuName: String?, hasTransportLayerEncryption: Bool
    ) {
        self.osName = osName
        self.osVersion = osVersion
        self.physicalMemoryBytes = physicalMemoryBytes
        self.cpuCoreCount = cpuCoreCount
        self.hasGpu = hasGpu
        self.gpuName = gpuName
        self.gpuMemoryBytes = gpuMemoryBytes
        self.hasNpu = hasNpu
        self.npuName = npuName
        self.hasTransportLayerEncryption = hasTransportLayerEncryption
    }
}

/// Injected device-capabilities provider (replaces ICapabilityProbe). Default
/// returns a fixed, self-consistent snapshot so the bridge is deterministic.
public protocol IDeviceCapabilitiesProvider: Sendable {
    func capabilities() async -> DeviceCapabilities
}

public struct StaticDeviceCapabilitiesProvider: IDeviceCapabilitiesProvider {
    private let value: DeviceCapabilities
    public init(_ value: DeviceCapabilities? = nil) {
        self.value = value ?? DeviceCapabilities(
            osName: "InMemory", osVersion: "1.0", physicalMemoryBytes: 8 * 1024 * 1024 * 1024,
            cpuCoreCount: 8, hasGpu: false, gpuName: nil, gpuMemoryBytes: nil,
            hasNpu: false, npuName: nil, hasTransportLayerEncryption: true)
    }
    public func capabilities() async -> DeviceCapabilities { value }
}

/// Terminal state of a single inference call.
public enum InferenceStatus: Int, Sendable {
    case completed = 0, stoppedByToken = 1, stoppedByLength = 2, failed = 3, cancelled = 4
}

/// Kind of fragment a streaming bridge emits.
public enum InferenceFragmentKind: Int, Sendable {
    case content = 0, reasoning = 1
}

/// A single fragment emitted by `IInferenceBridge.streamFragments`.
public struct InferenceFragment: Sendable, Equatable {
    public let kind: InferenceFragmentKind
    public let text: String
    public init(kind: InferenceFragmentKind, text: String) {
        self.kind = kind
        self.text = text
    }
}

/// One completion request submitted to an `IInferenceBridge`.
public struct InferenceRequest: Sendable, Equatable {
    public let id: UUID
    public let modelId: String
    public let prompt: String
    public let maxOutputTokens: Int
    public let temperature: Float
    public let topP: Float
    public let stopSequences: [String]
    public let metadata: [String: String]
    public let requestedAt: Date

    public init(
        id: UUID, modelId: String, prompt: String, maxOutputTokens: Int,
        temperature: Float, topP: Float, stopSequences: [String],
        metadata: [String: String], requestedAt: Date
    ) {
        self.id = id
        self.modelId = modelId
        self.prompt = prompt
        self.maxOutputTokens = maxOutputTokens
        self.temperature = temperature
        self.topP = topP
        self.stopSequences = stopSequences
        self.metadata = metadata
        self.requestedAt = requestedAt
    }

    /// Convenience factory with sensible defaults, mirroring `InferenceRequest.Create`.
    public static func create(
        modelId: String, prompt: String, maxOutputTokens: Int = 256,
        temperature: Float = 0.7, topP: Float = 0.95
    ) -> InferenceRequest {
        precondition(!modelId.isEmpty)
        return InferenceRequest(
            id: UUID(), modelId: modelId, prompt: prompt, maxOutputTokens: maxOutputTokens,
            temperature: temperature, topP: topP, stopSequences: [], metadata: [:],
            requestedAt: Date())
    }
}

/// Result of a single completion call.
public struct InferenceResponse: Sendable, Equatable {
    public let requestId: UUID
    public let modelId: String
    public let outputText: String
    public let outputTokenCount: Int
    public let promptTokenCount: Int
    public let status: InferenceStatus
    public let inferenceMillis: Double
    public let failureMessage: String?
    public let completedAt: Date
    public let reasoningText: String?

    public init(
        requestId: UUID, modelId: String, outputText: String, outputTokenCount: Int,
        promptTokenCount: Int, status: InferenceStatus, inferenceMillis: Double,
        failureMessage: String?, completedAt: Date, reasoningText: String? = nil
    ) {
        self.requestId = requestId
        self.modelId = modelId
        self.outputText = outputText
        self.outputTokenCount = outputTokenCount
        self.promptTokenCount = promptTokenCount
        self.status = status
        self.inferenceMillis = inferenceMillis
        self.failureMessage = failureMessage
        self.completedAt = completedAt
        self.reasoningText = reasoningText
    }
}

/// Cross-OS contract for an inference daemon.
public protocol IInferenceBridge: AnyObject, Sendable {
    func listLoadedModels() async throws -> [ModelDescriptor]
    func isModelLoaded(_ modelId: String) async throws -> Bool
    func complete(_ request: InferenceRequest) async throws -> InferenceResponse
    func streamCompletion(_ request: InferenceRequest) -> AsyncStream<String>
    func streamFragments(_ request: InferenceRequest) -> AsyncStream<InferenceFragment>
    func deviceCapabilities() async throws -> DeviceCapabilities
}

extension IInferenceBridge {
    /// Default fragment stream wraps `streamCompletion` tagging every chunk as
    /// `.content` (parity with the C# default `StreamFragmentsAsync`).
    public func streamFragments(_ request: InferenceRequest) -> AsyncStream<InferenceFragment> {
        let inner = streamCompletion(request)
        return AsyncStream { continuation in
            let task = Task {
                for await chunk in inner {
                    if Task.isCancelled { break }
                    continuation.yield(InferenceFragment(kind: .content, text: chunk))
                }
                continuation.finish()
            }
            continuation.onTermination = { _ in task.cancel() }
        }
    }
}

/// In-process `IInferenceBridge` wrapping any `IChatGenerator`. Reports transport
/// encryption as `true` — calls never leave the host process.
public final class LocalProcessInferenceBridge: IInferenceBridge, @unchecked Sendable {
    private let chatGenerator: IChatGenerator
    private let descriptor: ModelDescriptor
    private let capsProvider: IDeviceCapabilitiesProvider

    public init(
        chatGenerator: IChatGenerator,
        descriptor: ModelDescriptor,
        capabilitiesProvider: IDeviceCapabilitiesProvider = StaticDeviceCapabilitiesProvider()
    ) {
        self.chatGenerator = chatGenerator
        self.descriptor = descriptor
        self.capsProvider = capabilitiesProvider
    }

    public func listLoadedModels() async throws -> [ModelDescriptor] { [descriptor] }

    public func isModelLoaded(_ modelId: String) async throws -> Bool {
        precondition(!modelId.isEmpty)
        return descriptor.modelId == modelId
    }

    public func complete(_ request: InferenceRequest) async throws -> InferenceResponse {
        guard descriptor.modelId == request.modelId else {
            return InferenceResponse(
                requestId: request.id, modelId: request.modelId, outputText: "",
                outputTokenCount: 0, promptTokenCount: 0, status: .failed,
                inferenceMillis: 0.0,
                failureMessage: "Model '\(request.modelId)' is not loaded by this bridge (have '\(descriptor.modelId)').",
                completedAt: Date())
        }

        let messages = [ChatMessage(role: "user", content: request.prompt)]
        let options = Self.optionsFrom(request)
        let started = Date()

        do {
            let response = try await chatGenerator.generateResponse(messages: messages, options: options)
            let elapsed = Date().timeIntervalSince(started) * 1000.0
            let status = Self.determineStatus(output: response.text, request: request)
            return InferenceResponse(
                requestId: request.id, modelId: request.modelId, outputText: response.text,
                outputTokenCount: Self.estimateTokenCount(response.text),
                promptTokenCount: Self.estimateTokenCount(request.prompt),
                status: status, inferenceMillis: elapsed, failureMessage: nil,
                completedAt: Date(), reasoningText: response.reasoningContent)
        } catch is CancellationError {
            let elapsed = Date().timeIntervalSince(started) * 1000.0
            return InferenceResponse(
                requestId: request.id, modelId: request.modelId, outputText: "",
                outputTokenCount: 0, promptTokenCount: Self.estimateTokenCount(request.prompt),
                status: .cancelled, inferenceMillis: elapsed, failureMessage: nil,
                completedAt: Date())
        } catch {
            let elapsed = Date().timeIntervalSince(started) * 1000.0
            return InferenceResponse(
                requestId: request.id, modelId: request.modelId, outputText: "",
                outputTokenCount: 0, promptTokenCount: Self.estimateTokenCount(request.prompt),
                status: .failed, inferenceMillis: elapsed, failureMessage: error.localizedDescription,
                completedAt: Date())
        }
    }

    public func streamCompletion(_ request: InferenceRequest) -> AsyncStream<String> {
        AsyncStream { continuation in
            let task = Task {
                guard descriptor.modelId == request.modelId else {
                    continuation.finish(); return
                }
                let messages = [ChatMessage(role: "user", content: request.prompt)]
                let options = Self.optionsFrom(request)
                var hasYielded = false
                for await chunk in chatGenerator.stream(messages: messages, options: options) {
                    if Task.isCancelled { break }
                    hasYielded = true
                    continuation.yield(chunk)
                }
                if !hasYielded, !Task.isCancelled {
                    let full = (try? await chatGenerator.generate(messages: messages, options: options)) ?? ""
                    continuation.yield(full)
                }
                continuation.finish()
            }
            continuation.onTermination = { _ in task.cancel() }
        }
    }

    public func streamFragments(_ request: InferenceRequest) -> AsyncStream<InferenceFragment> {
        AsyncStream { continuation in
            let task = Task {
                guard descriptor.modelId == request.modelId else {
                    continuation.finish(); return
                }
                let messages = [ChatMessage(role: "user", content: request.prompt)]
                let options = Self.optionsFrom(request)
                for await f in chatGenerator.streamFragments(messages: messages, options: options) {
                    if Task.isCancelled { break }
                    let kind: InferenceFragmentKind = f.kind == .reasoning ? .reasoning : .content
                    continuation.yield(InferenceFragment(kind: kind, text: f.text))
                }
                continuation.finish()
            }
            continuation.onTermination = { _ in task.cancel() }
        }
    }

    public func deviceCapabilities() async throws -> DeviceCapabilities {
        await capsProvider.capabilities()
    }

    // Helpers

    private static func optionsFrom(_ request: InferenceRequest) -> GenerationOptions {
        GenerationOptions(
            maxTokens: request.maxOutputTokens,
            temperature: request.temperature,
            topP: request.topP,
            stopSequences: request.stopSequences.isEmpty ? nil : request.stopSequences)
    }

    static func determineStatus(output: String, request: InferenceRequest) -> InferenceStatus {
        if !request.stopSequences.isEmpty {
            for s in request.stopSequences where !s.isEmpty {
                if output.contains(s) { return .stoppedByToken }
            }
        }
        let produced = estimateTokenCount(output)
        return produced >= request.maxOutputTokens ? .stoppedByLength : .completed
    }

    static func estimateTokenCount(_ text: String) -> Int {
        if text.isEmpty { return 0 }
        return max(1, text.count / 4)
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// MARK: - Bridge factory
// ─────────────────────────────────────────────────────────────────────────────

/// The host registers one of these so the admin handler knows how to
/// materialise an `IInferenceBridge` for a model id + backend + tier.
public protocol IBridgeFactory: Sendable {
    func create(modelId: String, backend: BackendKind, tier: CapabilityTier) async throws -> IInferenceBridge
}

/// Default implementation — refuses every load with a clear error.
public struct UnconfiguredBridgeFactory: IBridgeFactory {
    public init() {}
    public func create(modelId: String, backend: BackendKind, tier: CapabilityTier) async throws -> IInferenceBridge {
        throw InferenceServerError.noBridgeFactory
    }
}

public enum InferenceServerError: Error, Equatable, CustomStringConvertible {
    case noBridgeFactory
    case bridgeFactoryReturnedNil(String)

    public var description: String {
        switch self {
        case .noBridgeFactory:
            return "No IBridgeFactory is configured. Register one before calling /v1/admin/models/load."
        case .bridgeFactoryReturnedNil(let id):
            return "BridgeFactory for '\(id)' returned nil."
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// MARK: - OpenAI-compatible DTOs
// ─────────────────────────────────────────────────────────────────────────────

/// One message in the chat-completion conversation.
public struct ChatCompletionMessage: Codable, Equatable, Sendable {
    public var role: String
    public var content: String
    public var name: String?
    public var reasoningContent: String?

    public init(role: String = "user", content: String = "", name: String? = nil, reasoningContent: String? = nil) {
        self.role = role
        self.content = content
        self.name = name
        self.reasoningContent = reasoningContent
    }

    enum CodingKeys: String, CodingKey {
        case role, content, name
        case reasoningContent = "reasoning_content"
    }

    public func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: CodingKeys.self)
        try c.encode(role, forKey: .role)
        try c.encode(content, forKey: .content)
        try c.encodeIfPresent(name, forKey: .name)
        // Omit reasoning_content from JSON when nil (WhenWritingNull parity).
        try c.encodeIfPresent(reasoningContent, forKey: .reasoningContent)
    }
}

/// OpenAI-shaped chat-completion request body.
public struct ChatCompletionRequest: Codable, Sendable {
    public var model: String
    public var messages: [ChatCompletionMessage]
    public var temperature: Float?
    public var topP: Float?
    public var maxTokens: Int?
    public var stream: Bool
    public var stop: [String]?
    public var user: String?

    public init(
        model: String = "", messages: [ChatCompletionMessage] = [], temperature: Float? = nil,
        topP: Float? = nil, maxTokens: Int? = nil, stream: Bool = false,
        stop: [String]? = nil, user: String? = nil
    ) {
        self.model = model
        self.messages = messages
        self.temperature = temperature
        self.topP = topP
        self.maxTokens = maxTokens
        self.stream = stream
        self.stop = stop
        self.user = user
    }

    enum CodingKeys: String, CodingKey {
        case model, messages, temperature, stream, stop, user
        case topP = "top_p"
        case maxTokens = "max_tokens"
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        model = (try? c.decode(String.self, forKey: .model)) ?? ""
        messages = (try? c.decode([ChatCompletionMessage].self, forKey: .messages)) ?? []
        temperature = try? c.decodeIfPresent(Float.self, forKey: .temperature)
        topP = try? c.decodeIfPresent(Float.self, forKey: .topP)
        maxTokens = try? c.decodeIfPresent(Int.self, forKey: .maxTokens)
        stream = (try? c.decodeIfPresent(Bool.self, forKey: .stream)) ?? false
        stop = try? c.decodeIfPresent([String].self, forKey: .stop)
        user = try? c.decodeIfPresent(String.self, forKey: .user)
    }
}

/// Token-usage block.
public struct UsageInfo: Codable, Equatable, Sendable {
    public var promptTokens: Int
    public var completionTokens: Int
    public var totalTokens: Int

    public init(promptTokens: Int = 0, completionTokens: Int = 0, totalTokens: Int = 0) {
        self.promptTokens = promptTokens
        self.completionTokens = completionTokens
        self.totalTokens = totalTokens
    }

    enum CodingKeys: String, CodingKey {
        case promptTokens = "prompt_tokens"
        case completionTokens = "completion_tokens"
        case totalTokens = "total_tokens"
    }
}

/// One choice in a non-streaming chat completion response.
public struct ChatCompletionChoice: Codable, Equatable, Sendable {
    public var index: Int
    public var message: ChatCompletionMessage
    public var finishReason: String

    public init(index: Int = 0, message: ChatCompletionMessage = .init(), finishReason: String = "stop") {
        self.index = index
        self.message = message
        self.finishReason = finishReason
    }

    enum CodingKeys: String, CodingKey {
        case index, message
        case finishReason = "finish_reason"
    }
}

/// OpenAI-shaped successful chat completion response.
public struct ChatCompletionResponse: Codable, Equatable, Sendable {
    public var id: String
    public var object: String
    public var created: Int64
    public var model: String
    public var choices: [ChatCompletionChoice]
    public var usage: UsageInfo

    public init(
        id: String = "", object: String = "chat.completion", created: Int64 = 0,
        model: String = "", choices: [ChatCompletionChoice] = [], usage: UsageInfo = .init()
    ) {
        self.id = id
        self.object = object
        self.created = created
        self.model = model
        self.choices = choices
        self.usage = usage
    }
}

/// Delta payload — only non-null fields are emitted between SSE frames.
public struct ChatCompletionDelta: Codable, Equatable, Sendable {
    public var role: String?
    public var content: String?
    public var reasoningContent: String?

    public init(role: String? = nil, content: String? = nil, reasoningContent: String? = nil) {
        self.role = role
        self.content = content
        self.reasoningContent = reasoningContent
    }

    enum CodingKeys: String, CodingKey {
        case role, content
        case reasoningContent = "reasoning_content"
    }

    public func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: CodingKeys.self)
        try c.encodeIfPresent(role, forKey: .role)
        try c.encodeIfPresent(content, forKey: .content)
        try c.encodeIfPresent(reasoningContent, forKey: .reasoningContent)
    }
}

/// One delta in a streamed chat completion chunk.
public struct ChatCompletionStreamChoice: Codable, Equatable, Sendable {
    public var index: Int
    public var delta: ChatCompletionDelta
    public var finishReason: String?

    public init(index: Int = 0, delta: ChatCompletionDelta = .init(), finishReason: String? = nil) {
        self.index = index
        self.delta = delta
        self.finishReason = finishReason
    }

    enum CodingKeys: String, CodingKey {
        case index, delta
        case finishReason = "finish_reason"
    }

    public func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: CodingKeys.self)
        try c.encode(index, forKey: .index)
        try c.encode(delta, forKey: .delta)
        try c.encodeIfPresent(finishReason, forKey: .finishReason)
    }
}

/// One SSE delta frame in a streamed chat completion.
public struct ChatCompletionStreamChunk: Codable, Equatable, Sendable {
    public var id: String
    public var object: String
    public var created: Int64
    public var model: String
    public var choices: [ChatCompletionStreamChoice]

    public init(
        id: String = "", object: String = "chat.completion.chunk", created: Int64 = 0,
        model: String = "", choices: [ChatCompletionStreamChoice] = []
    ) {
        self.id = id
        self.object = object
        self.created = created
        self.model = model
        self.choices = choices
    }
}

/// One embedding row in the response.
public struct EmbeddingDatum: Codable, Equatable, Sendable {
    public var object: String
    public var index: Int
    public var embedding: [Float]

    public init(object: String = "embedding", index: Int = 0, embedding: [Float] = []) {
        self.object = object
        self.index = index
        self.embedding = embedding
    }
}

/// OpenAI-shaped embeddings response.
public struct EmbeddingsResponse: Codable, Equatable, Sendable {
    public var object: String
    public var data: [EmbeddingDatum]
    public var model: String
    public var usage: UsageInfo

    public init(object: String = "list", data: [EmbeddingDatum] = [], model: String = "", usage: UsageInfo = .init()) {
        self.object = object
        self.data = data
        self.model = model
        self.usage = usage
    }
}

/// Embeddings input — either a single string or an array of strings.
public enum EmbeddingsInput: Sendable, Equatable {
    case single(String)
    case many([String])

    /// Normalise into a flat list of strings, or an OpenAI-shaped error.
    /// Mirrors the C# `TryNormaliseInput`.
    public func normalised() -> Result<[String], ErrorResponse> {
        switch self {
        case .single(let s):
            return .success([s])
        case .many(let arr):
            if arr.isEmpty {
                return .failure(.of("'input' array must not be empty.", type: "invalid_request_error", code: "invalid_input"))
            }
            return .success(arr)
        }
    }
}

/// OpenAI-shaped embeddings request. `input` accepts either a single string or
/// an array of strings (the same wire shape OpenAI's SDKs send). Decoding
/// normalises both into `EmbeddingsInput`.
public struct EmbeddingsRequest: Codable, Sendable {
    public var model: String
    public var input: EmbeddingsInput
    public var user: String?

    public init(model: String = "", input: EmbeddingsInput = .many([]), user: String? = nil) {
        self.model = model
        self.input = input
        self.user = user
    }

    enum CodingKeys: String, CodingKey { case model, input, user }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        model = (try? c.decode(String.self, forKey: .model)) ?? ""
        user = try? c.decodeIfPresent(String.self, forKey: .user)
        if let single = try? c.decode(String.self, forKey: .input) {
            input = .single(single)
        } else if let arr = try? c.decode([String].self, forKey: .input) {
            input = .many(arr)
        } else {
            // Match the C# validator: a non-string, non-array input is invalid.
            throw DecodingError.dataCorruptedError(
                forKey: .input, in: c,
                debugDescription: "'input' must be a string or array of strings.")
        }
    }

    public func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: CodingKeys.self)
        try c.encode(model, forKey: .model)
        try c.encodeIfPresent(user, forKey: .user)
        switch input {
        case .single(let s): try c.encode(s, forKey: .input)
        case .many(let arr): try c.encode(arr, forKey: .input)
        }
    }
}

/// OpenAI-shaped error envelope: `{"error": {...}}`.
public struct ErrorResponse: Codable, Equatable, Sendable, Error {
    public struct ErrorBody: Codable, Equatable, Sendable {
        public var message: String
        public var type: String
        public var param: String?
        public var code: String?

        public init(message: String = "", type: String = "invalid_request_error", param: String? = nil, code: String? = nil) {
            self.message = message
            self.type = type
            self.param = param
            self.code = code
        }
    }

    public var error: ErrorBody
    public init(error: ErrorBody = .init()) { self.error = error }

    public static func of(_ message: String, type: String, code: String? = nil) -> ErrorResponse {
        ErrorResponse(error: ErrorBody(message: message, type: type, param: nil, code: code))
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// MARK: - Server-side model registry
// ─────────────────────────────────────────────────────────────────────────────

/// In-process registry of bridge instances keyed by logical model ID.
public protocol IInferenceServerModelRegistry: AnyObject, Sendable {
    func register(_ modelId: String, bridge: IInferenceBridge)
    func registerEmbedder(_ modelId: String, embedder: ITextEmbedder)
    @discardableResult func deregister(_ modelId: String) -> Bool
    func resolve(_ modelId: String) -> IInferenceBridge?
    func resolveEmbedder(_ modelId: String) -> ITextEmbedder?
    func allModelIds() -> [String]
    func chatModelIds() -> [String]
}

/// Default thread-safe implementation.
public final class InferenceServerModelRegistry: IInferenceServerModelRegistry, @unchecked Sendable {
    private let lock = NSLock()
    private var chat: [String: IInferenceBridge] = [:]
    private var embed: [String: ITextEmbedder] = [:]

    public init() {}

    public func register(_ modelId: String, bridge: IInferenceBridge) {
        precondition(!modelId.trimmingCharacters(in: .whitespaces).isEmpty)
        lock.lock(); chat[modelId] = bridge; lock.unlock()
    }

    public func registerEmbedder(_ modelId: String, embedder: ITextEmbedder) {
        precondition(!modelId.trimmingCharacters(in: .whitespaces).isEmpty)
        lock.lock(); embed[modelId] = embedder; lock.unlock()
    }

    @discardableResult
    public func deregister(_ modelId: String) -> Bool {
        lock.lock(); defer { lock.unlock() }
        return chat.removeValue(forKey: modelId) != nil
    }

    public func resolve(_ modelId: String) -> IInferenceBridge? {
        lock.lock(); defer { lock.unlock() }
        return chat[modelId]
    }

    public func resolveEmbedder(_ modelId: String) -> ITextEmbedder? {
        lock.lock(); defer { lock.unlock() }
        return embed[modelId]
    }

    public func allModelIds() -> [String] {
        lock.lock(); defer { lock.unlock() }
        var set = Set(chat.keys)
        set.formUnion(embed.keys)
        return Array(set)
    }

    public func chatModelIds() -> [String] {
        lock.lock(); defer { lock.unlock() }
        return Array(chat.keys)
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// MARK: - Lifecycle
// ─────────────────────────────────────────────────────────────────────────────

/// What the caller wants to load. The factory produces the bridge; the manager
/// runs the admission gate.
public struct ModelLoadDescriptor: Sendable {
    public let modelId: String
    public let backend: BackendKind
    public let requestedTier: CapabilityTier
    public let vramRequiredBytes: Int64
    public let ramRequiredBytes: Int64
    public let bridgeFactory: @Sendable () async throws -> IInferenceBridge

    public init(
        modelId: String, backend: BackendKind, requestedTier: CapabilityTier,
        vramRequiredBytes: Int64, ramRequiredBytes: Int64,
        bridgeFactory: @escaping @Sendable () async throws -> IInferenceBridge
    ) {
        self.modelId = modelId
        self.backend = backend
        self.requestedTier = requestedTier
        self.vramRequiredBytes = vramRequiredBytes
        self.ramRequiredBytes = ramRequiredBytes
        self.bridgeFactory = bridgeFactory
    }
}

/// Runtime view of one loaded model.
public struct ModelLoadState: Sendable, Equatable {
    public let modelId: String
    public let backend: BackendKind
    public let tier: CapabilityTier
    public let vramBytes: Int64
    public let ramBytes: Int64
    public let loadedAt: Date

    public init(modelId: String, backend: BackendKind, tier: CapabilityTier, vramBytes: Int64, ramBytes: Int64, loadedAt: Date) {
        self.modelId = modelId
        self.backend = backend
        self.tier = tier
        self.vramBytes = vramBytes
        self.ramBytes = ramBytes
        self.loadedAt = loadedAt
    }
}

public enum LoadOutcome: Int, Sendable {
    case loaded = 0, alreadyLoaded = 1, insufficientVram = 2, insufficientRam = 3, factoryFailed = 4
}

public struct LoadResult: Sendable {
    public let outcome: LoadOutcome
    public let state: ModelLoadState?
    public let rationale: String
    public init(outcome: LoadOutcome, state: ModelLoadState?, rationale: String) {
        self.outcome = outcome
        self.state = state
        self.rationale = rationale
    }
}

public enum UnloadOutcome: Int, Sendable {
    case unloaded = 0, notLoaded = 1
}

/// Host resource snapshot for the admission gate (replaces ICapabilityProbe).
public struct HostResources: Sendable, Equatable {
    public let totalPhysicalMemoryBytes: Int64
    public let vramBytes: Int64
    public init(totalPhysicalMemoryBytes: Int64, vramBytes: Int64) {
        self.totalPhysicalMemoryBytes = totalPhysicalMemoryBytes
        self.vramBytes = vramBytes
    }
}

public protocol IHostResourceProbe: Sendable {
    func probe() async -> HostResources
}

public struct StaticHostResourceProbe: IHostResourceProbe {
    private let value: HostResources
    public init(totalPhysicalMemoryBytes: Int64 = 16 * 1024 * 1024 * 1024, vramBytes: Int64 = 0) {
        self.value = HostResources(totalPhysicalMemoryBytes: totalPhysicalMemoryBytes, vramBytes: vramBytes)
    }
    public func probe() async -> HostResources { value }
}

/// Admits or rejects model loads and keeps the authoritative ledger.
public protocol IModelLifecycleManager: AnyObject, Sendable {
    func load(_ descriptor: ModelLoadDescriptor) async throws -> LoadResult
    func unload(_ modelId: String) async throws -> UnloadOutcome
    func list() -> [ModelLoadState]
    var totalAllocatedVramBytes: Int64 { get }
    var totalAllocatedRamBytes: Int64 { get }
}

/// Default `IModelLifecycleManager` — VRAM/RAM headroom + duplicate gate.
public final class ModelLifecycleManager: IModelLifecycleManager, @unchecked Sendable {
    private let registry: IInferenceServerModelRegistry
    private let probe: IHostResourceProbe
    private let lock = NSLock()
    private var loaded: [String: ModelLoadState] = [:]
    private var cachedResources: HostResources?

    public init(registry: IInferenceServerModelRegistry, probe: IHostResourceProbe) {
        self.registry = registry
        self.probe = probe
    }

    public var totalAllocatedVramBytes: Int64 {
        lock.lock(); defer { lock.unlock() }
        return loaded.values.reduce(0) { $0 + $1.vramBytes }
    }

    public var totalAllocatedRamBytes: Int64 {
        lock.lock(); defer { lock.unlock() }
        return loaded.values.reduce(0) { $0 + $1.ramBytes }
    }

    public func load(_ descriptor: ModelLoadDescriptor) async throws -> LoadResult {
        precondition(!descriptor.modelId.trimmingCharacters(in: .whitespaces).isEmpty)

        // Idempotent fast path.
        if let existing = readState(descriptor.modelId) {
            return LoadResult(outcome: .alreadyLoaded, state: existing,
                rationale: "Model '\(descriptor.modelId)' is already loaded (\(existing.backend.name), \(existing.tier.name)).")
        }

        let resources = await getOrProbe()

        // VRAM admission — only on GPU-class backends.
        if descriptor.backend.isGpuClass {
            let vramCeiling = resources.vramBytes
            let vramFree = vramCeiling - totalAllocatedVramBytes
            if vramFree < descriptor.vramRequiredBytes {
                return LoadResult(outcome: .insufficientVram, state: nil,
                    rationale: "Need \(descriptor.vramRequiredBytes / (1024 * 1024)) MiB VRAM, " +
                               "have \(max(0, vramFree) / (1024 * 1024)) MiB free " +
                               "(\(totalAllocatedVramBytes / (1024 * 1024)) MiB of \(vramCeiling / (1024 * 1024)) MiB in use).")
            }
        }

        // RAM admission — always enforced.
        let ramFree = resources.totalPhysicalMemoryBytes - totalAllocatedRamBytes
        if ramFree < descriptor.ramRequiredBytes {
            return LoadResult(outcome: .insufficientRam, state: nil,
                rationale: "Need \(descriptor.ramRequiredBytes / (1024 * 1024)) MiB RAM, " +
                           "have \(max(0, ramFree) / (1024 * 1024)) MiB free " +
                           "(\(totalAllocatedRamBytes / (1024 * 1024)) MiB of \(resources.totalPhysicalMemoryBytes / (1024 * 1024)) MiB in use).")
        }

        // Reserve before invoking the factory so concurrent loads see the accounting.
        let reserveState = ModelLoadState(
            modelId: descriptor.modelId, backend: descriptor.backend, tier: descriptor.requestedTier,
            vramBytes: descriptor.vramRequiredBytes, ramBytes: descriptor.ramRequiredBytes, loadedAt: Date())

        lock.lock()
        if let raceWinner = loaded[descriptor.modelId] {
            lock.unlock()
            return LoadResult(outcome: .alreadyLoaded, state: raceWinner,
                rationale: "Model '\(descriptor.modelId)' was loaded by a concurrent request.")
        }
        loaded[descriptor.modelId] = reserveState
        lock.unlock()

        do {
            let bridge = try await descriptor.bridgeFactory()
            registry.register(descriptor.modelId, bridge: bridge)
            return LoadResult(outcome: .loaded, state: reserveState,
                rationale: "Loaded '\(descriptor.modelId)' on \(descriptor.backend.name) at \(descriptor.requestedTier.name).")
        } catch {
            // Roll the reservation back.
            lock.lock(); loaded.removeValue(forKey: descriptor.modelId); lock.unlock()
            return LoadResult(outcome: .factoryFailed, state: nil,
                rationale: "Bridge factory for '\(descriptor.modelId)' failed: \(error.localizedDescription)")
        }
    }

    public func unload(_ modelId: String) async throws -> UnloadOutcome {
        precondition(!modelId.trimmingCharacters(in: .whitespaces).isEmpty)
        lock.lock()
        let removed = loaded.removeValue(forKey: modelId) != nil
        lock.unlock()
        if !removed { return .notLoaded }
        registry.deregister(modelId)
        return .unloaded
    }

    public func list() -> [ModelLoadState] {
        lock.lock(); defer { lock.unlock() }
        return Array(loaded.values)
    }

    private func readState(_ id: String) -> ModelLoadState? {
        lock.lock(); defer { lock.unlock() }
        return loaded[id]
    }

    private func getOrProbe() async -> HostResources {
        if let cached = readCached() { return cached }
        let r = await probe.probe()
        lock.lock()
        if cachedResources == nil { cachedResources = r }
        let result = cachedResources!
        lock.unlock()
        return result
    }

    private func readCached() -> HostResources? {
        lock.lock(); defer { lock.unlock() }
        return cachedResources
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// MARK: - Native runtime status
// ─────────────────────────────────────────────────────────────────────────────

/// Last-known native runtime paths, surfaced through diagnostics. Ported as a
/// small value type (the C# `NativeRuntimePrep.NativeRuntimePaths`).
public struct NativeRuntimePaths: Sendable, Equatable {
    public let mnnCorePath: String
    public let bridgePath: String
    public let extractedRoot: String
    public init(mnnCorePath: String, bridgePath: String, extractedRoot: String) {
        self.mnnCorePath = mnnCorePath
        self.bridgePath = bridgePath
        self.extractedRoot = extractedRoot
    }
}

public protocol INativeRuntimeStatus: AnyObject, Sendable {
    var latest: NativeRuntimePaths? { get }
    func update(_ paths: NativeRuntimePaths)
}

public final class NativeRuntimeStatus: INativeRuntimeStatus, @unchecked Sendable {
    private let lock = NSLock()
    private var value: NativeRuntimePaths?
    public init() {}
    public var latest: NativeRuntimePaths? {
        lock.lock(); defer { lock.unlock() }
        return value
    }
    public func update(_ paths: NativeRuntimePaths) {
        lock.lock(); value = paths; lock.unlock()
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// MARK: - Companion session resolver
// ─────────────────────────────────────────────────────────────────────────────

/// Resolves an `ICompanionSession` for a given session_id + identity_id.
public protocol ICompanionSessionResolver: Sendable {
    func resolve(sessionId: String, identityId: String) async throws -> ICompanionSession?
}

/// In-process `ICompanionSessionResolver`. Caches one session per
/// (sessionId, identityId) and constructs missing sessions via the injected
/// factory closure. Single-flighted per key. A failed construction drops the
/// cache slot so the next caller retries cleanly.
public final class InMemoryCompanionSessionResolver: ICompanionSessionResolver, @unchecked Sendable {
    public typealias Factory = @Sendable (_ identityId: String, _ interface: InterfaceKind) async throws -> ICompanionSession

    /// Single-flight cache entry: a task + a monotonic token so a losing racer
    /// can tell whether the slot it observed is still the one it created before
    /// evicting it on failure (mirrors the C# `KeyValuePair` remove check).
    private final class Entry {
        let token: Int
        let task: Task<ICompanionSession, Error>
        init(token: Int, task: Task<ICompanionSession, Error>) { self.token = token; self.task = task }
    }

    private let factory: Factory
    private let defaultInterface: InterfaceKind
    private let lock = NSLock()
    private var sessions: [Key: Entry] = [:]
    private var nextToken = 0

    private struct Key: Hashable { let sessionId: String; let identityId: String }

    public init(factory: @escaping Factory, defaultInterface: InterfaceKind = .web) {
        self.factory = factory
        self.defaultInterface = defaultInterface
    }

    public func resolve(sessionId: String, identityId: String) async throws -> ICompanionSession? {
        if sessionId.trimmingCharacters(in: .whitespaces).isEmpty
            || identityId.trimmingCharacters(in: .whitespaces).isEmpty {
            return nil
        }
        let key = Key(sessionId: sessionId, identityId: identityId)

        lock.lock()
        let entry: Entry
        if let existing = sessions[key] {
            entry = existing
        } else {
            let iface = defaultInterface
            let f = factory
            let token = nextToken
            nextToken += 1
            entry = Entry(token: token, task: Task { try await f(identityId, iface) })
            sessions[key] = entry
        }
        lock.unlock()

        do {
            let session = try await entry.task.value
            try Task.checkCancellation()
            return session
        } catch {
            // A failed construction must not poison the cache — drop the slot,
            // but only if it is still the same attempt (don't evict a racer).
            lock.lock()
            if let stored = sessions[key], stored.token == entry.token { sessions.removeValue(forKey: key) }
            lock.unlock()
            throw error
        }
    }

    /// Number of currently cached sessions. Diagnostics only.
    public var cachedSessionCount: Int {
        lock.lock(); defer { lock.unlock() }
        return sessions.count
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// MARK: - Companion DTOs
// ─────────────────────────────────────────────────────────────────────────────

/// POST /v1/companion/turn request body.
public struct CompanionTurnRequest: Codable, Sendable {
    public var sessionId: String
    public var identityId: String
    public var message: String
    public var stream: Bool
    public var agentic: Bool

    public init(sessionId: String = "", identityId: String = "", message: String = "", stream: Bool = false, agentic: Bool = false) {
        self.sessionId = sessionId
        self.identityId = identityId
        self.message = message
        self.stream = stream
        self.agentic = agentic
    }

    enum CodingKeys: String, CodingKey {
        case sessionId = "session_id"
        case identityId = "identity_id"
        case message, stream, agentic
    }
}

/// POST /v1/companion/turn response body.
public struct CompanionTurnResponse: Codable, Equatable, Sendable {
    public var sessionId: String
    public var reply: String
    public var agentic: Bool
    public var turnIndex: Int

    public init(sessionId: String = "", reply: String = "", agentic: Bool = false, turnIndex: Int = 0) {
        self.sessionId = sessionId
        self.reply = reply
        self.agentic = agentic
        self.turnIndex = turnIndex
    }

    enum CodingKeys: String, CodingKey {
        case sessionId = "session_id"
        case reply, agentic
        case turnIndex = "turn_index"
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// MARK: - Counters + admission
// ─────────────────────────────────────────────────────────────────────────────

/// Thread-safe server-wide counters surfaced by diagnostics.
public final class ServerCounters: @unchecked Sendable {
    private let lock = NSLock()
    private var total: Int64 = 0
    private var rejected: Int64 = 0
    private var failed: Int64 = 0
    private var active: Int = 0

    public let startedAt = Date()
    public init() {}

    public var totalRequests: Int64 { lock.lock(); defer { lock.unlock() }; return total }
    public var rejectedRequests: Int64 { lock.lock(); defer { lock.unlock() }; return rejected }
    public var failedRequests: Int64 { lock.lock(); defer { lock.unlock() }; return failed }
    public var activeRequests: Int { lock.lock(); defer { lock.unlock() }; return active }

    public func accountAdmitted() { lock.lock(); total += 1; active += 1; lock.unlock() }
    public func accountCompleted() { lock.lock(); active -= 1; lock.unlock() }
    public func accountRejected() { lock.lock(); rejected += 1; lock.unlock() }
    public func accountFailed() { lock.lock(); failed += 1; lock.unlock() }
}

/// Bounded admission gate — at most `maxConcurrentRequests` in flight. Excess
/// requests are rejected immediately (no queueing).
public final class AdmissionControl: @unchecked Sendable {
    private let lock = NSLock()
    private var available: Int
    private let counters: ServerCounters

    public let maxConcurrentRequests: Int

    public init(maxConcurrentRequests: Int, counters: ServerCounters) {
        self.maxConcurrentRequests = max(1, maxConcurrentRequests)
        self.available = max(1, maxConcurrentRequests)
        self.counters = counters
    }

    /// Attempt to acquire a slot. Returns a token the caller MUST release, or
    /// `nil` when saturated.
    public func tryEnter() -> AdmissionSlot? {
        lock.lock()
        if available > 0 {
            available -= 1
            lock.unlock()
            counters.accountAdmitted()
            return AdmissionSlot(owner: self)
        }
        lock.unlock()
        counters.accountRejected()
        return nil
    }

    fileprivate func release() {
        lock.lock(); available += 1; lock.unlock()
        counters.accountCompleted()
    }
}

/// RAII-style admission token. Call `release()` exactly once (idempotent).
public final class AdmissionSlot: @unchecked Sendable {
    private let lock = NSLock()
    private weak var owner: AdmissionControl?
    private var released = false

    fileprivate init(owner: AdmissionControl) { self.owner = owner }

    public func release() {
        lock.lock()
        let shouldRelease = !released
        released = true
        lock.unlock()
        if shouldRelease { owner?.release() }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// MARK: - Auth (API key, constant-time)
// ─────────────────────────────────────────────────────────────────────────────

public struct ApiKeyOptions: Sendable, Equatable, Codable {
    public var enabled: Bool
    public var headerName: String
    public var keys: [String]

    public init(enabled: Bool = true, headerName: String = "X-CircleAI-Api-Key", keys: [String] = []) {
        self.enabled = enabled
        self.headerName = headerName
        self.keys = keys
    }
}

/// Outcome of an authentication attempt (mirrors AuthenticateResult).
public enum AuthResult: Sendable, Equatable {
    /// Authenticated — carries the principal name.
    case success(name: String)
    /// No credential presented — the pipeline treats this as unauthenticated.
    case noResult
    /// Credential presented but invalid.
    case fail(reason: String)
}

/// API-key authentication handler. Reads the configured header and matches
/// against the allow-list with a constant-time comparison. When disabled,
/// succeeds with a synthetic "anonymous" principal.
public struct ApiKeyAuthHandler: Sendable {
    public static let schemeApiKey = "ApiKey"

    private let options: ApiKeyOptions
    public init(options: ApiKeyOptions) { self.options = options }

    /// Authenticate a request given its headers (case-insensitive lookup).
    public func authenticate(headers: [String: String]) -> AuthResult {
        if !options.enabled {
            return .success(name: "anonymous")
        }
        guard let raw = Self.header(headers, name: options.headerName), !raw.trimmingCharacters(in: .whitespaces).isEmpty else {
            return .noResult
        }
        if !Self.matchKey(presented: raw, allowed: options.keys) {
            return .fail(reason: "Invalid API key.")
        }
        return .success(name: "api-key-caller")
    }

    private static func header(_ headers: [String: String], name: String) -> String? {
        for (k, v) in headers where k.caseInsensitiveCompare(name) == .orderedSame {
            return v
        }
        return nil
    }

    /// Constant-time match against any configured key.
    static func matchKey(presented: String, allowed: [String]) -> Bool {
        if allowed.isEmpty { return false }
        let presentedBytes = Array(presented.utf8)
        for k in allowed where !k.isEmpty {
            let bytes = Array(k.utf8)
            if bytes.count != presentedBytes.count { continue }
            if fixedTimeEquals(bytes, presentedBytes) { return true }
        }
        return false
    }

    /// Constant-time byte comparison (both arrays must be equal length).
    static func fixedTimeEquals(_ a: [UInt8], _ b: [UInt8]) -> Bool {
        if a.count != b.count { return false }
        var diff: UInt8 = 0
        for i in 0..<a.count { diff |= a[i] ^ b[i] }
        return diff == 0
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// MARK: - HTTP result model (replaces IResult)
// ─────────────────────────────────────────────────────────────────────────────

/// Common HTTP status codes the handlers emit (mirrors StatusCodes.Status*).
public enum HttpStatus: Int, Sendable {
    case ok = 200
    case badRequest = 400
    case unauthorized = 401
    case notFound = 404
    case insufficientStorage = 507
    case serviceUnavailable = 503
    case gatewayTimeout = 504
    case internalServerError = 500
}

/// A non-streaming handler result: status + JSON body + optional headers.
public struct HandlerResult: Sendable {
    public let status: HttpStatus
    public let jsonBody: Data
    public let headers: [String: String]

    public init(status: HttpStatus, jsonBody: Data, headers: [String: String] = [:]) {
        self.status = status
        self.jsonBody = jsonBody
        self.headers = headers
    }

    static let encoder: JSONEncoder = {
        let e = JSONEncoder()
        e.dateEncodingStrategy = .iso8601
        return e
    }()

    public static func json<T: Encodable>(_ value: T, status: HttpStatus = .ok, headers: [String: String] = [:]) -> HandlerResult {
        let data = (try? encoder.encode(value)) ?? Data("{}".utf8)
        return HandlerResult(status: status, jsonBody: data, headers: headers)
    }

    public static func error(_ err: ErrorResponse, status: HttpStatus, headers: [String: String] = [:]) -> HandlerResult {
        json(err, status: status, headers: headers)
    }
}

/// A streaming (SSE) handler result: an async stream of already-serialised
/// event payloads plus the terminating `[DONE]` sentinel (last element).
public struct SseResult: Sendable {
    public let frames: AsyncStream<String>
    public init(frames: AsyncStream<String>) { self.frames = frames }
}

/// The union a handler returns for endpoints that can stream.
public enum EndpointResponse: Sendable {
    case immediate(HandlerResult)
    case sse(SseResult)
}

// ─────────────────────────────────────────────────────────────────────────────
// MARK: - Chat completions handler
// ─────────────────────────────────────────────────────────────────────────────

/// POST /v1/chat/completions — routes to the bridge registered for the model.
/// Streaming mode returns SSE frames; non-streaming returns a single response.
public struct ChatCompletionsHandler: Sendable {
    private let registry: IInferenceServerModelRegistry
    private let admission: AdmissionControl
    private let counters: ServerCounters
    private let requestTimeoutSeconds: Int

    public init(
        registry: IInferenceServerModelRegistry, admission: AdmissionControl,
        counters: ServerCounters, requestTimeoutSeconds: Int = 120
    ) {
        self.registry = registry
        self.admission = admission
        self.counters = counters
        self.requestTimeoutSeconds = requestTimeoutSeconds
    }

    public func handle(_ body: ChatCompletionRequest) async -> EndpointResponse {
        if body.model.trimmingCharacters(in: .whitespaces).isEmpty {
            return .immediate(.error(.of("Missing or empty 'model' field.", type: "invalid_request_error", code: "missing_model"), status: .badRequest))
        }
        if body.messages.isEmpty {
            return .immediate(.error(.of("Missing 'messages' array.", type: "invalid_request_error", code: "missing_messages"), status: .badRequest))
        }

        guard let bridge = registry.resolve(body.model) else {
            return .immediate(.error(.of("Model '\(body.model)' is not loaded.", type: "invalid_request_error", code: "model_not_found"), status: .notFound))
        }

        guard let slot = admission.tryEnter() else {
            return .immediate(.error(
                .of("Server is at concurrency cap (\(admission.maxConcurrentRequests)). Retry after a brief delay.",
                    type: "server_busy", code: "concurrency_cap"),
                status: .serviceUnavailable, headers: ["Retry-After": "1"]))
        }

        let request = Self.buildInferenceRequest(body)

        if body.stream {
            let result = streamResponse(bridge: bridge, request: request, body: body, slot: slot)
            return .sse(result)
        } else {
            let result = await nonStreamResponse(bridge: bridge, request: request, body: body)
            slot.release()
            return .immediate(result)
        }
    }

    // Non-streaming branch.
    private func nonStreamResponse(bridge: IInferenceBridge, request: InferenceRequest, body: ChatCompletionRequest) async -> HandlerResult {
        let resp: InferenceResponse
        do {
            resp = try await bridge.complete(request)
        } catch is CancellationError {
            counters.accountFailed()
            return .error(.of("Request cancelled or timed out.", type: "timeout", code: "request_timeout"), status: .gatewayTimeout)
        } catch {
            counters.accountFailed()
            return .error(.of(error.localizedDescription, type: "internal_error", code: "bridge_failure"), status: .internalServerError)
        }

        if resp.status == .failed {
            counters.accountFailed()
            return .error(.of(resp.failureMessage ?? "Inference failed.", type: "internal_error", code: "inference_failed"), status: .internalServerError)
        }

        let response = ChatCompletionResponse(
            id: "chatcmpl-\(UUID().uuidString.replacingOccurrences(of: "-", with: ""))",
            created: Int64(Date().timeIntervalSince1970),
            model: body.model,
            choices: [ChatCompletionChoice(
                index: 0,
                message: ChatCompletionMessage(role: "assistant", content: resp.outputText, reasoningContent: resp.reasoningText),
                finishReason: Self.mapFinish(resp.status))],
            usage: UsageInfo(
                promptTokens: resp.promptTokenCount,
                completionTokens: resp.outputTokenCount,
                totalTokens: resp.promptTokenCount + resp.outputTokenCount))
        return .json(response)
    }

    // Streaming branch — emits OpenAI-shaped SSE frames.
    private func streamResponse(bridge: IInferenceBridge, request: InferenceRequest, body: ChatCompletionRequest, slot: AdmissionSlot) -> SseResult {
        let id = "chatcmpl-\(UUID().uuidString.replacingOccurrences(of: "-", with: ""))"
        let created = Int64(Date().timeIntervalSince1970)
        let model = body.model
        let counters = self.counters

        let stream = AsyncStream<String> { continuation in
            let task = Task {
                defer { slot.release() }

                // First frame: role announcement.
                continuation.yield(Self.encodeFrame(ChatCompletionStreamChunk(
                    id: id, created: created, model: model,
                    choices: [ChatCompletionStreamChoice(index: 0, delta: ChatCompletionDelta(role: "assistant"))])))

                for await f in bridge.streamFragments(request) {
                    if Task.isCancelled { break }
                    if f.text.isEmpty { continue }
                    let delta = f.kind == .reasoning
                        ? ChatCompletionDelta(reasoningContent: f.text)
                        : ChatCompletionDelta(content: f.text)
                    continuation.yield(Self.encodeFrame(ChatCompletionStreamChunk(
                        id: id, created: created, model: model,
                        choices: [ChatCompletionStreamChoice(index: 0, delta: delta)])))
                }
                if Task.isCancelled { counters.accountFailed() }

                // Final frame: stop reason.
                continuation.yield(Self.encodeFrame(ChatCompletionStreamChunk(
                    id: id, created: created, model: model,
                    choices: [ChatCompletionStreamChoice(index: 0, delta: ChatCompletionDelta(), finishReason: "stop")])))
                // Terminator.
                continuation.yield("[DONE]")
                continuation.finish()
            }
            continuation.onTermination = { _ in task.cancel() }
        }
        return SseResult(frames: stream)
    }

    // Helpers.

    static func buildInferenceRequest(_ body: ChatCompletionRequest) -> InferenceRequest {
        // Concatenate messages into a single prompt — the underlying generator
        // does its own chat-templating.
        let prompt = body.messages.map { "<|\($0.role)|>\n\($0.content)\n<|end|>" }.joined(separator: "\n")
        var metadata: [String: String] = [:]
        if let user = body.user, !user.isEmpty { metadata["user"] = user }
        return InferenceRequest(
            id: UUID(), modelId: body.model, prompt: prompt,
            maxOutputTokens: body.maxTokens ?? 512,
            temperature: body.temperature ?? 0.7,
            topP: body.topP ?? 0.9,
            stopSequences: body.stop ?? [],
            metadata: metadata,
            requestedAt: Date())
    }

    static func mapFinish(_ status: InferenceStatus) -> String {
        switch status {
        case .completed, .stoppedByToken: return "stop"
        case .stoppedByLength: return "length"
        case .cancelled: return "cancelled"
        default: return "error"
        }
    }

    static func encodeFrame<T: Encodable>(_ value: T) -> String {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        let data = (try? encoder.encode(value)) ?? Data("{}".utf8)
        return String(data: data, encoding: .utf8) ?? "{}"
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// MARK: - Embeddings handler
// ─────────────────────────────────────────────────────────────────────────────

/// POST /v1/embeddings — routes to the embedder registered for the model.
public struct EmbeddingsHandler: Sendable {
    private let registry: IInferenceServerModelRegistry
    private let admission: AdmissionControl
    private let counters: ServerCounters

    public init(registry: IInferenceServerModelRegistry, admission: AdmissionControl, counters: ServerCounters) {
        self.registry = registry
        self.admission = admission
        self.counters = counters
    }

    /// Handle a decoded `EmbeddingsRequest` (the OpenAI wire DTO).
    public func handle(_ body: EmbeddingsRequest) async -> HandlerResult {
        await handle(model: body.model, input: body.input)
    }

    public func handle(model: String, input: EmbeddingsInput) async -> HandlerResult {
        if model.trimmingCharacters(in: .whitespaces).isEmpty {
            return .error(.of("Missing or empty 'model' field.", type: "invalid_request_error", code: "missing_model"), status: .badRequest)
        }
        guard let embedder = registry.resolveEmbedder(model) else {
            return .error(.of("Embedding model '\(model)' is not loaded.", type: "invalid_request_error", code: "model_not_found"), status: .notFound)
        }

        let inputs: [String]
        switch input.normalised() {
        case .success(let arr): inputs = arr
        case .failure(let err): return .error(err, status: .badRequest)
        }

        guard let slot = admission.tryEnter() else {
            return .error(.of("Server is at concurrency cap. Retry shortly.", type: "server_busy", code: "concurrency_cap"), status: .serviceUnavailable)
        }
        defer { slot.release() }

        var data: [EmbeddingDatum] = []
        var totalChars = 0
        do {
            for (i, text) in inputs.enumerated() {
                let vec = try await embedder.generate(text)
                data.append(EmbeddingDatum(index: i, embedding: vec))
                totalChars += text.count
            }
        } catch is CancellationError {
            counters.accountFailed()
            return .error(.of("Request cancelled or timed out.", type: "timeout", code: "request_timeout"), status: .gatewayTimeout)
        } catch {
            counters.accountFailed()
            return .error(.of(error.localizedDescription, type: "internal_error", code: "embedding_failure"), status: .internalServerError)
        }

        // OpenAI reports input tokens only for embeddings.
        let estimatedPromptTokens = max(1, totalChars / 4)
        let response = EmbeddingsResponse(
            data: data, model: model,
            usage: UsageInfo(promptTokens: estimatedPromptTokens, completionTokens: 0, totalTokens: estimatedPromptTokens))
        return .json(response)
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// MARK: - Companion handler
// ─────────────────────────────────────────────────────────────────────────────

/// POST /v1/companion/turn — send a message to a Companion session.
public struct CompanionHandler: Sendable {
    private let resolver: ICompanionSessionResolver
    private let admission: AdmissionControl
    private let counters: ServerCounters

    public init(resolver: ICompanionSessionResolver, admission: AdmissionControl, counters: ServerCounters) {
        self.resolver = resolver
        self.admission = admission
        self.counters = counters
    }

    public func handle(_ body: CompanionTurnRequest) async -> EndpointResponse {
        if body.sessionId.trimmingCharacters(in: .whitespaces).isEmpty
            || body.identityId.trimmingCharacters(in: .whitespaces).isEmpty
            || body.message.trimmingCharacters(in: .whitespaces).isEmpty {
            return .immediate(.error(.of("session_id, identity_id, and message are all required.", type: "invalid_request_error", code: "missing_field"), status: .badRequest))
        }

        let session: ICompanionSession?
        do {
            session = try await resolver.resolve(sessionId: body.sessionId, identityId: body.identityId)
        } catch {
            counters.accountFailed()
            return .immediate(.error(.of(error.localizedDescription, type: "internal_error", code: "companion_failure"), status: .internalServerError))
        }
        guard let session = session else {
            return .immediate(.error(
                .of("No Companion session for session_id='\(body.sessionId)', identity_id='\(body.identityId)'.",
                    type: "invalid_request_error", code: "session_not_found"),
                status: .notFound))
        }

        guard let slot = admission.tryEnter() else {
            return .immediate(.error(.of("Server is at concurrency cap. Retry shortly.", type: "server_busy", code: "concurrency_cap"), status: .serviceUnavailable))
        }

        if body.stream {
            let result = streamReply(session: session, body: body, slot: slot)
            return .sse(result)
        }

        defer { slot.release() }
        do {
            let reply = body.agentic
                ? try await session.agent(body.message)
                : try await session.send(body.message)
            let response = CompanionTurnResponse(
                sessionId: body.sessionId, reply: reply, agentic: body.agentic, turnIndex: session.history.count)
            return .immediate(.json(response))
        } catch is CancellationError {
            counters.accountFailed()
            return .immediate(.error(.of("Request cancelled or timed out.", type: "timeout", code: "request_timeout"), status: .gatewayTimeout))
        } catch {
            counters.accountFailed()
            return .immediate(.error(.of(error.localizedDescription, type: "internal_error", code: "companion_failure"), status: .internalServerError))
        }
    }

    private func streamReply(session: ICompanionSession, body: CompanionTurnRequest, slot: AdmissionSlot) -> SseResult {
        let sessionId = body.sessionId
        let message = body.message
        let stream = AsyncStream<String> { continuation in
            let task = Task {
                defer { slot.release() }
                for await chunk in session.stream(message) {
                    if Task.isCancelled { break }
                    if chunk.isEmpty { continue }
                    let frame = ["session_id": sessionId, "delta": chunk]
                    continuation.yield(ChatCompletionsHandler.encodeFrame(frame))
                }
                continuation.yield("[DONE]")
                continuation.finish()
            }
            continuation.onTermination = { _ in task.cancel() }
        }
        return SseResult(frames: stream)
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// MARK: - Admin handler
// ─────────────────────────────────────────────────────────────────────────────

/// Request body for POST /v1/admin/models/load.
public struct AdminLoadRequest: Codable, Sendable {
    public var modelId: String
    public var backend: String
    public var tier: String
    public var vramRequiredBytes: Int64
    public var ramRequiredBytes: Int64

    public init(modelId: String = "", backend: String = "Cpu", tier: String = "Tier1_Small", vramRequiredBytes: Int64 = 0, ramRequiredBytes: Int64 = 0) {
        self.modelId = modelId
        self.backend = backend
        self.tier = tier
        self.vramRequiredBytes = vramRequiredBytes
        self.ramRequiredBytes = ramRequiredBytes
    }
}

/// Response body for /v1/admin/lifecycle.
public struct AdminLifecycleResponse: Codable, Sendable {
    public struct LoadedState: Codable, Sendable {
        public let modelId: String
        public let backend: String
        public let tier: String
        public let vramBytes: Int64
        public let ramBytes: Int64
    }
    public var totalAllocatedVramBytes: Int64
    public var totalAllocatedRamBytes: Int64
    public var loaded: [LoadedState]
}

/// POST /v1/admin/models/load, DELETE /v1/admin/models/{id}, GET /v1/admin/lifecycle.
public struct AdminHandler: Sendable {
    private let manager: IModelLifecycleManager
    private let factory: IBridgeFactory

    public init(manager: IModelLifecycleManager, factory: IBridgeFactory) {
        self.manager = manager
        self.factory = factory
    }

    public func lifecycle() -> HandlerResult {
        let resp = AdminLifecycleResponse(
            totalAllocatedVramBytes: manager.totalAllocatedVramBytes,
            totalAllocatedRamBytes: manager.totalAllocatedRamBytes,
            loaded: manager.list().map {
                AdminLifecycleResponse.LoadedState(
                    modelId: $0.modelId, backend: $0.backend.name, tier: $0.tier.name,
                    vramBytes: $0.vramBytes, ramBytes: $0.ramBytes)
            })
        return .json(resp)
    }

    public func load(_ body: AdminLoadRequest) async -> HandlerResult {
        if body.modelId.trimmingCharacters(in: .whitespaces).isEmpty {
            return .error(.of("Missing 'modelId'.", type: "invalid_request_error", code: "missing_model"), status: .badRequest)
        }
        guard let backend = BackendKind.parse(body.backend) else {
            return .error(.of("Unknown backend '\(body.backend)'. Valid: Cpu, Cuda, Vulkan, OpenCL, Metal, Ascend, Cambricon, CoreML.", type: "invalid_request_error", code: "invalid_backend"), status: .badRequest)
        }
        guard let tier = CapabilityTier.parse(body.tier) else {
            return .error(.of("Unknown tier '\(body.tier)'. Valid: Tier0_Tiny..Tier4_Frontier.", type: "invalid_request_error", code: "invalid_tier"), status: .badRequest)
        }

        let factory = self.factory
        let modelId = body.modelId
        let descriptor = ModelLoadDescriptor(
            modelId: modelId, backend: backend, requestedTier: tier,
            vramRequiredBytes: max(0, body.vramRequiredBytes),
            ramRequiredBytes: max(0, body.ramRequiredBytes),
            bridgeFactory: { try await factory.create(modelId: modelId, backend: backend, tier: tier) })

        let result = (try? await manager.load(descriptor))
            ?? LoadResult(outcome: .factoryFailed, state: nil, rationale: "load threw")

        switch result.outcome {
        case .loaded, .alreadyLoaded:
            let payload = AdminLoadOk(
                outcome: result.outcome == .loaded ? "Loaded" : "AlreadyLoaded",
                rationale: result.rationale,
                state: result.state.map {
                    AdminLoadOk.State(modelId: $0.modelId, backend: $0.backend.name, tier: $0.tier.name, vramBytes: $0.vramBytes, ramBytes: $0.ramBytes)
                })
            return .json(payload)
        case .insufficientVram, .insufficientRam:
            let code = result.outcome == .insufficientVram ? "InsufficientVram" : "InsufficientRam"
            return .error(.of(result.rationale, type: "resource_exhausted", code: code), status: .insufficientStorage)
        case .factoryFailed:
            return .error(.of(result.rationale, type: "internal_error", code: "factory_failed"), status: .internalServerError)
        }
    }

    public func unload(_ modelId: String) async -> HandlerResult {
        let outcome = (try? await manager.unload(modelId)) ?? .notLoaded
        switch outcome {
        case .unloaded:
            return .json(["outcome": "Unloaded", "modelId": modelId])
        case .notLoaded:
            return .error(.of("Model '\(modelId)' is not loaded.", type: "invalid_request_error", code: "not_loaded"), status: .notFound)
        }
    }

    struct AdminLoadOk: Codable, Sendable {
        struct State: Codable, Sendable {
            let modelId: String
            let backend: String
            let tier: String
            let vramBytes: Int64
            let ramBytes: Int64
        }
        let outcome: String
        let rationale: String
        let state: State?
    }
}
