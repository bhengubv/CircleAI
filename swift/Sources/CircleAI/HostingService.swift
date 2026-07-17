// HostingService.swift
//
// Port of the CircleAI.Hosting butler-service surface:
//   - IAIService.cs          → IAIService (long-lived butler contract)
//   - AIService.cs           → AIService  (default impl: enrich → generate → episodic)
//   - FallbackAIService.cs   → FallbackAIService (local-first, cloud fallback)
//   - AIApiClient.cs         → AIApiClient (cloud proxy DTO surface; injected transport)
//   - IAIObserver.cs event records → AIChatEvent / AIStreamEvent / AIToolEvent,
//                                     BrownoutReason, IAIServiceObserver
//
// Threading model mirrors the C#:
//   - start() is idempotent, serialised by an NSLock-guarded gate flag.
//   - chat()/stream() are safe to call concurrently.
//   - Native model loading (QwenTextGenerator / NativeLibraryResolver) has no
//     Swift analogue, so the generator is always injected via a factory — the
//     same seam the C# `generatorFactory` constructor argument exposes.

import Foundation

// =====================================================================
// Observer event records (mirror IAIObserver.cs)
// =====================================================================

/// Payload delivered to `IAIServiceObserver.onChatCompleted`. Carries the full
/// conversation and the model's reply. Mirrors C# `AIChatEvent`.
public struct AIChatEvent: @unchecked Sendable {
    public let correlationId: UUID
    public let messages: [ChatMessage]
    public let response: String
    public let elapsed: TimeInterval
    public let timestamp: Date

    public init(correlationId: UUID, messages: [ChatMessage], response: String,
                elapsed: TimeInterval, timestamp: Date) {
        self.correlationId = correlationId
        self.messages = messages
        self.response = response
        self.elapsed = elapsed
        self.timestamp = timestamp
    }
}

/// Payload delivered to stream start / completed observer hooks. Mirrors C#
/// `AIStreamEvent`. `tokenCount` is 0 at stream start; total at completion.
public struct AIStreamEvent: @unchecked Sendable {
    public let correlationId: UUID
    public let messages: [ChatMessage]
    public let elapsed: TimeInterval
    public let tokenCount: Int
    public let timestamp: Date

    public init(correlationId: UUID, messages: [ChatMessage], elapsed: TimeInterval,
                tokenCount: Int, timestamp: Date) {
        self.correlationId = correlationId
        self.messages = messages
        self.elapsed = elapsed
        self.tokenCount = tokenCount
        self.timestamp = timestamp
    }
}

/// Payload delivered to `IAIServiceObserver.onToolInvoked`. Mirrors C#
/// `AIToolEvent`.
public struct AIToolEvent: @unchecked Sendable {
    public let correlationId: UUID
    public let invocation: ToolInvocation
    public let result: ToolResult
    public let elapsed: TimeInterval
    public let timestamp: Date

    public init(correlationId: UUID, invocation: ToolInvocation, result: ToolResult,
                elapsed: TimeInterval, timestamp: Date) {
        self.correlationId = correlationId
        self.invocation = invocation
        self.result = result
        self.elapsed = elapsed
        self.timestamp = timestamp
    }
}

/// (RT-04) Why a brownout swap fired. Mirrors C# `BrownoutReason`.
public enum BrownoutReason: Int, Sendable {
    /// OS-reported memory pressure.
    case memoryPressure = 0
    /// Battery dropped below the brownout floor.
    case batteryFloor = 1
    /// Thermal throttle declared the runtime must downshift.
    case thermalCritical = 2
    /// Application requested the swap explicitly.
    case manual = 3
}

/// Observability hook for `AIService` with the exact C# `IAIObserver` event
/// semantics. Named `IAIServiceObserver` to sit alongside the pre-existing
/// param-based `IAIObserver` in `Hosting.swift` without colliding. All methods
/// default to no-ops; observer errors are caught by `AIService` and never
/// propagate to the caller.
public protocol IAIServiceObserver: AnyObject, Sendable {
    func onStarted() async
    func onStopped() async
    func onChatCompleted(_ event: AIChatEvent) async
    func onStreamStarted(_ event: AIStreamEvent) async
    func onStreamCompleted(_ event: AIStreamEvent) async
    func onToolInvoked(_ event: AIToolEvent) async
    func onModelFetching(_ modelId: String, autoSelected: Bool) async
    func onUpgradeAvailable(_ upgrade: UpgradeInfo) async
    func onBrownout(from: String, to: String, reason: BrownoutReason) async
}

public extension IAIServiceObserver {
    func onStarted() async {}
    func onStopped() async {}
    func onChatCompleted(_ event: AIChatEvent) async {}
    func onStreamStarted(_ event: AIStreamEvent) async {}
    func onStreamCompleted(_ event: AIStreamEvent) async {}
    func onToolInvoked(_ event: AIToolEvent) async {}
    func onModelFetching(_ modelId: String, autoSelected: Bool) async {}
    func onUpgradeAvailable(_ upgrade: UpgradeInfo) async {}
    func onBrownout(from: String, to: String, reason: BrownoutReason) async {}
}

// =====================================================================
// IAIService
// =====================================================================

/// Long-lived butler service. Owns the loaded chat generator and exposes
/// ask / chat / stream / tool / agentic entry points. Implementations are
/// thread-safe — concurrent callers can share one instance. Ported from
/// `IAIService`.
public protocol IAIService: AnyObject, Sendable {
    /// `true` once `start()` has completed and the model is loaded.
    var isReady: Bool { get }

    /// Resolves the model file, loads it, and optionally runs a warm-up
    /// generation. Idempotent after first success.
    func start() async throws

    /// Releases the model handle and shuts the service down.
    func stop() async throws

    /// Single user question — the configured (enriched) system prompt is
    /// prepended automatically.
    func ask(_ question: String) async throws -> String

    /// Complete assistant reply for the supplied conversation, with context
    /// enrichment applied.
    func chat(_ messages: [ChatMessage], options: GenerationOptions?) async throws -> String

    /// Streams the assistant reply token-by-token, with the same enrichment as
    /// `chat`.
    func stream(_ messages: [ChatMessage], options: GenerationOptions?) -> AsyncThrowingStream<String, Error>

    /// Routes a tool invocation to the configured tool bridge. Returns a failure
    /// result when no bridge is wired up.
    func invokeTool(_ invocation: ToolInvocation) async throws -> ToolResult

    /// Agentic run: generate → detect tool calls → execute → re-prompt, until a
    /// plain-text response or the iteration cap is reached.
    func agenticChat(_ prompt: String, options: GenerationOptions?) async throws -> String

    /// Records a user feedback signal against a past response.
    func submitFeedback(_ signal: FeedbackSignal) async throws

    /// Detects installed-model upgrades. Empty when everything is current or no
    /// storage directory is configured.
    func checkForUpgrades() async throws -> [UpgradeInfo]

    /// Pre-warm the loaded generator without a user-facing call.
    func prewarm() async throws

    /// Releases all resources (mirrors C# `IAsyncDisposable`).
    func dispose() async

    /// (RT-02) Snapshot the session (KV cache + history) to `path`. No-op
    /// default; `AIService` overrides to snapshot the generalist floor.
    func saveSession(path: String) async -> Bool

    /// (RT-02) Restore a session from `path`. No-op default.
    func loadSession(path: String) async -> Bool
}

public extension IAIService {
    func ask(_ question: String) async throws -> String {
        try await ask(question)
    }
    func chat(_ messages: [ChatMessage]) async throws -> String {
        try await chat(messages, options: nil)
    }
    func stream(_ messages: [ChatMessage]) -> AsyncThrowingStream<String, Error> {
        stream(messages, options: nil)
    }
    func agenticChat(_ prompt: String) async throws -> String {
        try await agenticChat(prompt, options: nil)
    }
    func checkForUpgrades() async throws -> [UpgradeInfo] { [] }
    func prewarm() async throws { try await start() }
    func saveSession(path: String) async -> Bool { false }
    func loadSession(path: String) async -> Bool { false }
}

// =====================================================================
// AIService
// =====================================================================

/// Errors surfaced by `AIService`. Mirrors the C# `InvalidOperationException` /
/// `ObjectDisposedException` / `FileNotFoundException` cases.
public enum AIServiceError: Error, Equatable {
    case notReady
    case disposed
    case noResolver(String)
    case modelPathMissing(String)
    case loaderFailed(String)
}

/// Default `IAIService`. Loads a chat generator once (via the injected factory)
/// and serves all downstream callers from that single handle. Ported from
/// `AIService`. The generator factory replaces the C# native `QwenTextGenerator`
/// path; everything else — persona/affect/device/RAG enrichment, agentic tool
/// loop, episodic writes, feedback-driven persona adaptation, brownout — is
/// faithful to the reference.
public final class AIService: IAIService, @unchecked Sendable {

    // Tool call detection tags (Qwen3 native format).
    private static let toolCallOpen  = "<tool_call>"
    private static let toolCallClose = "</tool_call>"

    private let options: AIOptions
    private let modelLoader: (any IModelLoader)?
    private let generatorFactory: (@Sendable (String) -> IChatGenerator)?
    private let modelSelector: (any IModelSelector)?
    private let modelRegistry: ModelRegistryService?
    private let pressureSource: (any IMemoryPressureSource)?
    private let observer: (any IAIServiceObserver)?
    private let router: (any INeuronRouter)?

    private let gate = NSLock()
    private var generator: IChatGenerator?
    private var started = false
    private var disposed = false
    private var resolvedModelId: String?
    private var resolvedDeviceTier: DeviceTier = .desktop
    private var autoSelected = false
    private var personaCache: PersonaState?
    private var ragBuilder: RagContextBuilder?
    private var pressureSub: (any Disposable)?
    private var slots: ResidentSlotManager?
    // Memoised in-flight start so concurrent callers await one load (SemaphoreSlim parity).
    private var startTask: Task<Void, Error>?

    /// Construct the service. Either `modelLoader` (+ optional selector) or
    /// `generatorFactory` must be able to resolve a model.
    public init(
        options: AIOptions,
        modelLoader: (any IModelLoader)? = nil,
        generatorFactory: (@Sendable (String) -> IChatGenerator)? = nil,
        modelSelector: (any IModelSelector)? = nil,
        modelRegistry: ModelRegistryService? = nil,
        memoryPressureSource: (any IMemoryPressureSource)? = nil,
        observer: (any IAIServiceObserver)? = nil,
        router: (any INeuronRouter)? = nil
    ) {
        self.options = options
        self.modelLoader = modelLoader
        self.generatorFactory = generatorFactory
        self.modelSelector = modelSelector
        self.modelRegistry = modelRegistry
        self.pressureSource = memoryPressureSource
        self.observer = observer
        self.router = router
    }

    // ------------------------------------------------------------------
    // Sync helpers (lock confined here — never held across await)
    // ------------------------------------------------------------------

    private func isStarted() -> Bool { gate.lock(); defer { gate.unlock() }; return started }
    private func isDisposedFlag() -> Bool { gate.lock(); defer { gate.unlock() }; return disposed }
    private func currentGenerator() -> IChatGenerator? { gate.lock(); defer { gate.unlock() }; return generator }
    private func setGenerator(_ g: IChatGenerator?) { gate.lock(); generator = g; gate.unlock() }
    private func setStarted(_ v: Bool) { gate.lock(); started = v; gate.unlock() }
    private func markDisposed() -> Bool { gate.lock(); defer { gate.unlock() }; if disposed { return false }; disposed = true; return true }

    private func throwIfDisposed() throws {
        if isDisposedFlag() { throw AIServiceError.disposed }
    }

    // ------------------------------------------------------------------
    // IsReady + lifecycle
    // ------------------------------------------------------------------

    public var isReady: Bool {
        gate.lock(); defer { gate.unlock() }
        return started && generator != nil && !disposed
    }

    public func start() async throws {
        try throwIfDisposed()
        if isStarted() { return }

        // Serialise: concurrent callers await the same in-flight start task, so
        // the model is loaded exactly once (mirrors the C# SemaphoreSlim gate).
        gate.lock()
        if started { gate.unlock(); return }
        if let existing = startTask { gate.unlock(); try await existing.value; return }
        let task = Task<Void, Error> { [weak self] in
            guard let self = self else { return }
            try await self.performStart()
        }
        startTask = task
        gate.unlock()

        do {
            try await task.value
            gate.lock(); startTask = nil; gate.unlock()
        } catch {
            gate.lock(); startTask = nil; gate.unlock()
            throw error
        }
    }

    private func performStart() async throws {
        let modelPath = try await resolveModelPath()

        let contextSize = options.contextSize ?? DeviceTierDefaults.contextWindow(currentTier())
        let g = try makeGenerator(modelPath: modelPath, contextSize: contextSize)
        setGenerator(g)

        if options.warmOnStart {
            try? await warmUp()
        }

        setStarted(true)

        // RT-04 — subscribe to platform pressure source. Critical → brownout.
        if let src = pressureSource {
            gate.lock(); let alreadySubbed = pressureSub != nil; gate.unlock()
            if !alreadySubbed {
                let sub = src.subscribe { [weak self] _, next in
                    if next == .critical {
                        _ = try? await self?.brownout(reason: .memoryPressure)
                    }
                }
                gate.lock(); pressureSub = sub; gate.unlock()
            }
        }

        await fireObserver { await $0.onStarted() }

        if options.checkForUpgradesOnStart {
            let upgrades = (try? await checkForUpgrades()) ?? []
            for u in upgrades { await fireObserver { await $0.onUpgradeAvailable(u) } }
        }
    }

    public func stop() async throws {
        if isDisposedFlag() { return }
        await trySavePersona()

        gate.lock(); let mgr = slots; gate.unlock()
        mgr?.evictSpecialist()
        if let g = currentGenerator() { (g as? Disposable)?.dispose() }
        setGenerator(nil)
        setStarted(false)
        gate.lock(); personaCache = nil; gate.unlock()

        await fireObserver { await $0.onStopped() }
    }

    public func dispose() async {
        guard markDisposed() else { return }
        gate.lock(); let sub = pressureSub; pressureSub = nil; gate.unlock()
        sub?.dispose()
        await trySavePersona()
        try? await stop()
        if let g = currentGenerator() { (g as? Disposable)?.dispose() }
        setGenerator(nil)
    }

    private func currentTier() -> DeviceTier {
        gate.lock(); defer { gate.unlock() }; return resolvedDeviceTier
    }

    private func makeGenerator(modelPath: String, contextSize: Int) throws -> IChatGenerator {
        if let factory = generatorFactory {
            return factory(modelPath)
        }
        // No Swift-native QwenTextGenerator: a generator factory is required.
        throw AIServiceError.noResolver(
            "AIService needs a generatorFactory; the native QwenTextGenerator path is C#-only.")
    }

    // ------------------------------------------------------------------
    // Neuron — two-slot residency + session persistence
    // ------------------------------------------------------------------

    /// The generalist model id — surfaced by `NeuronNode.engineLabel`.
    public var resolvedModelIdValue: String? {
        gate.lock(); defer { gate.unlock() }; return resolvedModelId
    }

    /// (RT-02) Snapshot the always-warm generalist floor. No-throw.
    public func saveSession(path: String) async -> Bool {
        if path.isEmpty { return false }
        guard let g = currentGenerator() else { return false }
        return (try? await g.saveSession(path: path)) ?? false
    }

    /// (RT-02) Restore the generalist floor. No-throw.
    public func loadSession(path: String) async -> Bool {
        if path.isEmpty { return false }
        try? await ensureStarted()
        guard let g = currentGenerator() else { return false }
        return (try? await g.loadSession(path: path)) ?? false
    }

    private func ensureSlots() -> ResidentSlotManager {
        gate.lock()
        if let s = slots { gate.unlock(); return s }
        gate.unlock()
        let mgr = ResidentSlotManager(generalistReservedBytes: 0, ramAvailable: { [weak self] in
            let probe = (self?.options.deviceContext as? DefaultDeviceContext)?.buildProbe()
                ?? DeviceProbe.snapshot()
            return probe.ramAvailableBytes
        })
        gate.lock()
        if slots == nil { slots = mgr }
        let result = slots!
        gate.unlock()
        return result
    }

    /// Build a specialist generator by model id (resolve path via loader +
    /// makeGenerator) — the specialist analog of the brownout swap.
    private func buildSpecialist(modelId: String) async throws -> IChatGenerator {
        guard let loader = modelLoader else {
            throw AIServiceError.loaderFailed("Specialist build requires an IModelLoader.")
        }
        var modelPath = (try? loader.getModelPath(modelId)) ?? ""
        if modelPath.isEmpty || !FileManager.default.fileExists(atPath: modelPath) {
            modelPath = try await loader.downloadModel(modelId, progress: nil)
        }
        if modelPath.isEmpty || !FileManager.default.fileExists(atPath: modelPath) {
            throw AIServiceError.loaderFailed("Specialist '\(modelId)' resolution failed.")
        }
        let contextSize = options.contextSize ?? DeviceTierDefaults.contextWindow(currentTier())
        return try makeGenerator(modelPath: modelPath, contextSize: contextSize)
    }

    /// Neuron slot selection. Returns nil for the generalist (unchanged path).
    /// With a router: route the turn and, on a specialist decision, best-fit +
    /// hot-load (admission-gated) a specialist. Any miss degrades to nil.
    private func selectSlot(userQuery: String, hasImage: Bool) async -> IChatGenerator? {
        guard let router = router else { return nil }
        let decision = router.route(RouteContext(query: userQuery, hasImage: hasImage))
        guard decision.organ == .specialist else { return nil }
        guard let selector = modelSelector, modelLoader != nil else { return nil }
        let probe = (options.deviceContext as? DefaultDeviceContext)?.buildProbe() ?? DeviceProbe.snapshot()
        guard let selection = try? selector.bestFit(probe, required: decision.capability) else { return nil }
        gate.lock(); let genId = resolvedModelId; gate.unlock()
        if let genId = genId, selection.modelId.caseInsensitiveCompare(genId) == .orderedSame {
            return nil // best-fit resolved to the generalist itself
        }
        let mgr = ensureSlots()
        let admission = await mgr.ensureSpecialist(selection) { [weak self] modelId in
            guard let self = self else { return nil }
            return try await self.buildSpecialist(modelId: modelId)
        }
        return admission.generator
    }

    // ------------------------------------------------------------------
    // RT-04 — Brownout: hot-swap to next-smaller fallback under pressure
    // ------------------------------------------------------------------

    /// Hot-swap the running generator to the next model in the fallback chain.
    /// No-op when not started, when no fallback exists, or when no selector /
    /// loader is wired. Ported from `AIService.BrownoutAsync`. The Swift
    /// `IModelSelector` has no `chainFor`, so the chain is derived from
    /// `allCandidates` ordered by quality (best→worst): the entry after the
    /// current model is the downshift target.
    @discardableResult
    public func brownout(reason: BrownoutReason) async throws -> Bool {
        try throwIfDisposed()
        guard isStarted(), currentGenerator() != nil else { return false }
        guard let selector = modelSelector else { return false }
        gate.lock(); let from = resolvedModelId; gate.unlock()
        guard let from = from, !from.isEmpty else { return false }

        let probe = (options.deviceContext as? DefaultDeviceContext)?.buildProbe()
            ?? DeviceProbe.snapshot()
        let chain = selector.allCandidates(probe).map { $0.modelId }
        guard let idx = chain.firstIndex(where: { $0.caseInsensitiveCompare(from) == .orderedSame }),
              idx + 1 < chain.count else {
            return false
        }
        let to = chain[idx + 1]
        if to.caseInsensitiveCompare(from) == .orderedSame { return false }

        // Dispose current, resolve fallback path, load it.
        if let g = currentGenerator() { (g as? Disposable)?.dispose() }
        setGenerator(nil)
        gate.lock(); resolvedModelId = to; gate.unlock()

        guard let loader = modelLoader else {
            throw AIServiceError.loaderFailed("Brownout requires an IModelLoader to fetch the fallback bundle.")
        }
        var modelPath = (try? loader.getModelPath(to)) ?? ""
        if modelPath.isEmpty || !FileManager.default.fileExists(atPath: modelPath) {
            modelPath = try await loader.downloadModel(to, progress: nil)
        }
        if modelPath.isEmpty || !FileManager.default.fileExists(atPath: modelPath) {
            throw AIServiceError.loaderFailed("Brownout target '\(to)' resolution failed.")
        }

        let contextSize = options.contextSize ?? DeviceTierDefaults.contextWindow(currentTier())
        let g = try makeGenerator(modelPath: modelPath, contextSize: contextSize)
        setGenerator(g)

        await fireObserver { await $0.onBrownout(from: from, to: to, reason: reason) }
        return true
    }

    // ------------------------------------------------------------------
    // Inference
    // ------------------------------------------------------------------

    public func ask(_ question: String) async throws -> String {
        precondition(!question.isEmpty, "question required")
        return try await chat([ChatMessage(role: "user", content: question)],
                              options: options.defaultGenerationOptions)
    }

    public func chat(_ messages: [ChatMessage], options callOptions: GenerationOptions?) async throws -> String {
        try await ensureStarted()
        let userQuery = lastUserMessage(messages)
        // Neuron: generalist by default; a specialist may answer when a router is
        // configured. Byte-identical to the single-slot path when router is nil.
        guard let generator = await selectSlot(userQuery: userQuery, hasImage: false) ?? currentGenerator() else {
            throw AIServiceError.notReady
        }
        let prepared = await prepareMessages(messages, userQuery: userQuery)
        let effective = callOptions ?? options.defaultGenerationOptions

        let correlationId = UUID()
        let started = Date()
        let response = try await generator.generate(messages: prepared, options: effective)
        let elapsed = Date().timeIntervalSince(started)

        Task { await self.tryStoreEpisode(userText: userQuery, assistantText: response) }

        await fireObserver {
            await $0.onChatCompleted(AIChatEvent(
                correlationId: correlationId, messages: prepared, response: response,
                elapsed: elapsed, timestamp: Date()))
        }
        return response
    }

    public func stream(_ messages: [ChatMessage], options callOptions: GenerationOptions?) -> AsyncThrowingStream<String, Error> {
        AsyncThrowingStream { continuation in
            let task = Task {
                do {
                    try await self.ensureStarted()
                    let userQuery = self.lastUserMessage(messages)
                    guard let generator = await self.selectSlot(userQuery: userQuery, hasImage: false) ?? self.currentGenerator() else {
                        continuation.finish(throwing: AIServiceError.notReady); return
                    }
                    let prepared = await self.prepareMessages(messages, userQuery: userQuery)
                    let effective = callOptions ?? self.options.defaultGenerationOptions

                    let correlationId = UUID()
                    let started = Date()
                    var tokenCount = 0
                    var firstToken = true
                    var full = ""

                    for await piece in generator.stream(messages: prepared, options: effective) {
                        if Task.isCancelled { break }
                        if firstToken {
                            firstToken = false
                            await self.fireObserver {
                                await $0.onStreamStarted(AIStreamEvent(
                                    correlationId: correlationId, messages: prepared,
                                    elapsed: Date().timeIntervalSince(started), tokenCount: 0, timestamp: Date()))
                            }
                        }
                        full += piece
                        tokenCount += 1
                        continuation.yield(piece)
                    }

                    let elapsed = Date().timeIntervalSince(started)
                    let finalFull = full
                    let finalCount = tokenCount
                    Task { await self.tryStoreEpisode(userText: userQuery, assistantText: finalFull) }
                    await self.fireObserver {
                        await $0.onStreamCompleted(AIStreamEvent(
                            correlationId: correlationId, messages: prepared,
                            elapsed: elapsed, tokenCount: finalCount, timestamp: Date()))
                    }
                    continuation.finish()
                } catch {
                    continuation.finish(throwing: error)
                }
            }
            continuation.onTermination = { _ in task.cancel() }
        }
    }

    public func invokeTool(_ invocation: ToolInvocation) async throws -> ToolResult {
        try throwIfDisposed()
        guard let bridge = options.toolBridge else {
            let fail = ToolResult.failure(toolName: invocation.toolName, error: "No tool bridge configured.")
            await fireObserver {
                await $0.onToolInvoked(AIToolEvent(
                    correlationId: UUID(), invocation: invocation, result: fail,
                    elapsed: 0, timestamp: Date()))
            }
            return fail
        }

        let correlationId = UUID()
        let started = Date()
        let result = try await bridge.invoke(invocation)
        let elapsed = Date().timeIntervalSince(started)
        await fireObserver {
            await $0.onToolInvoked(AIToolEvent(
                correlationId: correlationId, invocation: invocation, result: result,
                elapsed: elapsed, timestamp: Date()))
        }
        return result
    }

    public func agenticChat(_ prompt: String, options callOptions: GenerationOptions?) async throws -> String {
        precondition(!prompt.isEmpty, "prompt required")
        try await ensureStarted()
        guard let generator = await selectSlot(userQuery: prompt, hasImage: false) ?? currentGenerator() else {
            throw AIServiceError.notReady
        }

        let maxIter = max(1, options.agenticMaxIterations ?? DeviceTierDefaults.agenticMaxIterations(currentTier()))
        let effective = callOptions ?? options.defaultGenerationOptions

        var history: [ChatMessage] = [ChatMessage(role: "user", content: prompt)]
        var lastResponse = ""

        for _ in 0..<maxIter {
            let prepared = await prepareMessages(history, userQuery: prompt)
            let started = Date()
            let response = try await generator.generate(messages: prepared, options: effective)
            let elapsed = Date().timeIntervalSince(started)
            lastResponse = response
            history.append(ChatMessage(role: "assistant", content: response))

            await fireObserver {
                await $0.onChatCompleted(AIChatEvent(
                    correlationId: UUID(), messages: prepared, response: response,
                    elapsed: elapsed, timestamp: Date()))
            }

            guard let invocation = Self.parseToolCall(response) else { break }

            if options.toolBridge == nil {
                history.append(ChatMessage(role: "tool",
                    content: "{\"tool\": \"\(invocation.toolName)\", \"error\": \"No tool bridge configured.\"}"))
                continue
            }

            let toolResult = try await invokeTool(invocation)
            let toolContent: String
            if toolResult.success {
                toolContent = "{\"tool\": \"\(toolResult.toolName)\", \"result\": \(Self.jsonEncode(toolResult.result))}"
            } else {
                toolContent = "{\"tool\": \"\(toolResult.toolName)\", \"error\": \(Self.jsonEncode(toolResult.error))}"
            }
            history.append(ChatMessage(role: "tool", content: toolContent))
        }

        let finalResponse = lastResponse
        Task { await self.tryStoreEpisode(userText: prompt, assistantText: finalResponse) }
        return finalResponse
    }

    public func submitFeedback(_ signal: FeedbackSignal) async throws {
        try throwIfDisposed()
        guard let store = options.feedbackStore else { return }

        do {
            try await store.add(signal)

            let persona = try await ensurePersona()
            if signal.polarity == .positive { persona.positiveSignals += 1 }
            else if signal.polarity == .negative { persona.negativeSignals += 1 }
            persona.totalInteractions += 1

            let recent = try await store.getRecent(count: 20)
            let adaptation = FeedbackAnalyser().analyse(recent)

            if adaptation.verbosityDelta < 0 {
                persona.verbosity = persona.verbosity == "detailed" ? "balanced" : "brief"
            } else if adaptation.verbosityDelta > 0 {
                persona.verbosity = persona.verbosity == "brief" ? "balanced" : "detailed"
            }

            if adaptation.formalityDelta < 0 {
                persona.formality = persona.formality == "formal" ? "neutral" : "casual"
            } else if adaptation.formalityDelta > 0 {
                persona.formality = persona.formality == "casual" ? "neutral" : "formal"
            }

            for topic in adaptation.preferredTopics {
                persona.topicWeights[topic, default: 0] += 1
            }

            await trySavePersona()
        } catch {
            // Non-fatal — feedback storage failures never break the caller.
        }
    }

    public func checkForUpgrades() async throws -> [UpgradeInfo] {
        try throwIfDisposed()
        guard let registry = modelRegistry,
              let dir = options.modelStorageDirectory, !dir.isEmpty else { return [] }
        return registry.checkForUpgrades(storageDirectory: dir)
    }

    public func prewarm() async throws {
        try throwIfDisposed()
        if !isStarted() { try await start(); return }
        try await warmUp()
    }

    // ------------------------------------------------------------------
    // Private — startup helpers
    // ------------------------------------------------------------------

    private func ensureStarted() async throws {
        try throwIfDisposed()
        if isStarted() { return }
        try await start()
    }

    private func resolveModelPath() async throws -> String {
        // 1. Explicit path wins.
        if let path = options.modelPath, !path.isEmpty {
            if !FileManager.default.fileExists(atPath: path) {
                throw AIServiceError.modelPathMissing(path)
            }
            gate.lock(); resolvedModelId = options.modelId; gate.unlock()
            return path
        }

        guard let loader = modelLoader else {
            throw AIServiceError.noResolver("AIService needs either AIOptions.modelPath or an IModelLoader.")
        }

        // 2. Resolve model id — pinned, or auto-selected from the live device.
        var modelId = options.modelId
        var autoSel = false

        if modelId == nil || modelId!.isEmpty {
            guard let selector = modelSelector else {
                throw AIServiceError.noResolver(
                    "AIOptions.modelId is null and no IModelSelector is registered.")
            }
            let deviceCtx = options.deviceContext ?? DefaultDeviceContext()
            let probe = (deviceCtx as? DefaultDeviceContext)?.buildProbe() ?? DeviceProbe.snapshot()
            let selection = try selector.bestFit(probe, required: options.requiredCapabilities)
            modelId = selection.modelId
            gate.lock(); resolvedDeviceTier = selection.tier; gate.unlock()
            autoSel = true
        }

        gate.lock(); resolvedModelId = modelId; autoSelected = autoSel; gate.unlock()
        let idForObserver = modelId!
        await fireObserver { await $0.onModelFetching(idForObserver, autoSelected: autoSel) }

        // 3. Already on disk?
        let existing = (try? loader.getModelPath(idForObserver)) ?? ""
        if !existing.isEmpty && FileManager.default.fileExists(atPath: existing) {
            return existing
        }

        // 4. Fetch via the loader.
        let downloaded = try await loader.downloadModel(idForObserver, progress: nil)
        if downloaded.isEmpty || !FileManager.default.fileExists(atPath: downloaded) {
            throw AIServiceError.loaderFailed("Model loader returned an invalid path for '\(idForObserver)'.")
        }
        return downloaded
    }

    private func warmUp() async throws {
        guard let generator = currentGenerator() else { return }
        let warm = [
            ChatMessage(role: "system", content: options.systemPrompt),
            ChatMessage(role: "user", content: "."),
        ]
        let warmOptions = GenerationOptions(maxTokens: 1, temperature: 0)
        _ = try await generator.generate(messages: warm, options: warmOptions)
    }

    // ------------------------------------------------------------------
    // Private — context enrichment
    // ------------------------------------------------------------------

    private func lastUserMessage(_ messages: [ChatMessage]) -> String {
        for m in messages.reversed() where m.role.caseInsensitiveCompare("user") == .orderedSame {
            return m.content
        }
        return ""
    }

    private func prepareMessages(_ messages: [ChatMessage], userQuery: String) async -> [ChatMessage] {
        let systemContent = await buildEnrichedSystemPrompt(userQuery: userQuery)
        let hasSystem = messages.contains { $0.role.caseInsensitiveCompare("system") == .orderedSame }

        if hasSystem {
            return messages
        }
        var prepared: [ChatMessage] = []
        if !systemContent.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            prepared.append(ChatMessage(role: "system", content: systemContent))
        }
        prepared.append(contentsOf: messages)
        return prepared
    }

    private func buildEnrichedSystemPrompt(userQuery: String) async -> String {
        var sb = options.systemPrompt

        // 1. Persona hints.
        if let persona = try? await ensurePersona() {
            let hint = persona.toSystemPromptHint()
            if !hint.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                sb += "\n" + hint
            }
        }

        // 1b. Affect state.
        if let affectStore = options.affectStore {
            if let affect = try? await affectStore.load(userId: options.personaUserId) {
                let hint = affect.toSystemPromptHint()
                if !hint.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                    sb += "\n" + hint
                }
            }
        }

        // 2. Device context.
        if let ctx = options.deviceContext, !(ctx is NullDeviceContext) {
            var lines: [String] = []
            if let localTime = ctx.localTime {
                let df = DateFormatter()
                df.dateFormat = "yyyy-MM-dd HH:mm"
                df.timeZone = TimeZone(identifier: ctx.timeZoneId ?? "UTC")
                lines.append("Local time: \(df.string(from: localTime)) (\(ctx.timeZoneId ?? "UTC"))")
            }
            if let loc = ctx.locationHint, !loc.trimmingCharacters(in: .whitespaces).isEmpty {
                lines.append("Location: \(loc)")
            }
            if let battery = ctx.batteryLevel {
                let pct = Int(battery * 100)
                let charging = ctx.isCharging == true ? " (charging)" : ""
                lines.append("Battery: \(pct)%\(charging)")
            }
            if let net = ctx.networkType, !net.trimmingCharacters(in: .whitespaces).isEmpty {
                lines.append("Network: \(net)")
            }
            if let app = ctx.activeAppId, !app.trimmingCharacters(in: .whitespaces).isEmpty {
                lines.append("Active app: \(app)")
            }
            if !lines.isEmpty {
                sb += "\n[Device context]\n" + lines.joined(separator: "\n") + "\n"
            }
        }

        // 3. RAG context.
        if let episodic = options.episodicMemory, options.ragTopK > 0,
           !userQuery.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            let builder = ensureRagBuilder(episodic: episodic)
            let block = await builder.buildContext(userQuery)
            if !block.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                sb += "\n" + block
            }
        }

        return sb
    }

    private func ensureRagBuilder(episodic: IEpisodicMemoryStore) -> RagContextBuilder {
        gate.lock()
        if let existing = ragBuilder { gate.unlock(); return existing }
        gate.unlock()
        let builder = options.ragBuilder
            ?? RagContextBuilder(store: episodic, embedder: nil, topK: options.ragTopK)
        gate.lock(); ragBuilder = builder; gate.unlock()
        return builder
    }

    // ------------------------------------------------------------------
    // Private — persona helpers
    // ------------------------------------------------------------------

    private func ensurePersona() async throws -> PersonaState {
        gate.lock(); if let cached = personaCache { gate.unlock(); return cached }; gate.unlock()
        let persona: PersonaState
        if let store = options.personaStore {
            persona = try await store.load(userId: options.personaUserId)
        } else {
            persona = PersonaState(userId: options.personaUserId)
        }
        gate.lock(); personaCache = persona; gate.unlock()
        return persona
    }

    private func trySavePersona() async {
        gate.lock(); let cached = personaCache; gate.unlock()
        guard let cached = cached, let store = options.personaStore else { return }
        try? await store.save(cached)
    }

    // ------------------------------------------------------------------
    // Private — episodic memory
    // ------------------------------------------------------------------

    private func tryStoreEpisode(userText: String, assistantText: String) async {
        guard let episodic = options.episodicMemory else { return }
        if userText.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { return }
        let entry = EpisodicMemoryEntry(
            userText: userText,
            assistantText: assistantText,
            appContext: options.deviceContext?.activeAppId,
            embedding: nil)
        try? await episodic.add(entry)
    }

    // ------------------------------------------------------------------
    // Private — tool call parsing
    // ------------------------------------------------------------------

    /// Parses a Qwen3 native `<tool_call>...</tool_call>` block. Returns nil when
    /// no tool call is present. Supports both `name` and `tool_name` spellings.
    static func parseToolCall(_ response: String) -> ToolInvocation? {
        if response.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { return nil }
        guard let startRange = response.range(of: toolCallOpen) else { return nil }
        let afterOpen = startRange.upperBound
        guard let endRange = response.range(of: toolCallClose, range: afterOpen..<response.endIndex) else { return nil }
        let json = String(response[afterOpen..<endRange.lowerBound]).trimmingCharacters(in: .whitespacesAndNewlines)
        if json.isEmpty { return nil }

        guard let data = json.data(using: .utf8),
              let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            return nil
        }
        let toolName = (obj["name"] as? String) ?? (obj["tool_name"] as? String)
        guard let toolName = toolName, !toolName.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            return nil
        }
        var args: [String: Any?] = [:]
        if let argObj = obj["arguments"] as? [String: Any] {
            for (k, v) in argObj { args[k] = v }
        }
        return ToolInvocation(toolName: toolName, arguments: args)
    }

    private static func jsonEncode(_ value: Any?) -> String {
        guard let value = value else { return "null" }
        if let s = value as? String {
            let data = (try? JSONSerialization.data(withJSONObject: [s], options: [])) ?? Data()
            if let arr = String(data: data, encoding: .utf8), arr.count >= 2 {
                return String(arr.dropFirst().dropLast()) // strip [ ]
            }
            return "\"\(s)\""
        }
        if JSONSerialization.isValidJSONObject([value]),
           let data = try? JSONSerialization.data(withJSONObject: [value], options: []),
           let arr = String(data: data, encoding: .utf8), arr.count >= 2 {
            return String(arr.dropFirst().dropLast())
        }
        return "\"\(String(describing: value))\""
    }

    // ------------------------------------------------------------------
    // Private — observer
    // ------------------------------------------------------------------

    private func fireObserver(_ action: (any IAIServiceObserver) async -> Void) async {
        guard let observer = observer else { return }
        await action(observer)
    }
}

// =====================================================================
// FallbackAIService
// =====================================================================

/// A source of "available RAM" so the local-vs-cloud decision is testable
/// without probing the real host. Mirrors the C# `GC.GetGCMemoryInfo` read.
public protocol IAvailableRamSource: Sendable {
    var availableRamBytes: Int64 { get }
}

/// Default RAM source: reports the host's physical memory.
public struct PhysicalMemoryRamSource: IAvailableRamSource {
    public init() {}
    public var availableRamBytes: Int64 { Int64(ProcessInfo.processInfo.physicalMemory) }
}

/// Wraps a local `IAIService` with a cloud fallback. Local inference is
/// preferred; cloud is used transparently when local is unavailable (RAM below
/// threshold, or local `start()` throws). Ported from `FallbackAIService`.
public final class FallbackAIService: IAIService, @unchecked Sendable {
    private let local: IAIService
    private let cloud: IAIService
    private let ramThresholdBytes: Int64
    private let ramSource: any IAvailableRamSource

    private let lock = NSLock()
    private var activeRef: IAIService?
    private var disposed = false

    public init(
        local: IAIService,
        cloud: IAIService,
        ramThresholdBytes: Int64 = 2 * 1024 * 1024 * 1024,
        ramSource: any IAvailableRamSource = PhysicalMemoryRamSource()
    ) {
        self.local = local
        self.cloud = cloud
        self.ramThresholdBytes = ramThresholdBytes
        self.ramSource = ramSource
    }

    public var isReady: Bool {
        lock.lock(); let a = activeRef; lock.unlock()
        return a?.isReady ?? false
    }

    public func start() async throws {
        let availableRam = ramSource.availableRamBytes
        if availableRam >= ramThresholdBytes {
            do {
                try await local.start()
                lock.lock(); activeRef = local; lock.unlock()
                return
            } catch {
                // Local start failed — fall through to cloud.
            }
        }
        try await cloud.start()
        lock.lock(); activeRef = cloud; lock.unlock()
    }

    public func stop() async throws {
        lock.lock(); let a = activeRef; lock.unlock()
        try await a?.stop()
    }

    private func active() throws -> IAIService {
        lock.lock(); let a = activeRef; lock.unlock()
        guard let a = a else {
            throw AIServiceError.notReady
        }
        return a
    }

    public func ask(_ question: String) async throws -> String { try await active().ask(question) }
    public func chat(_ messages: [ChatMessage], options: GenerationOptions?) async throws -> String {
        try await active().chat(messages, options: options)
    }
    public func stream(_ messages: [ChatMessage], options: GenerationOptions?) -> AsyncThrowingStream<String, Error> {
        do { return try active().stream(messages, options: options) }
        catch { return AsyncThrowingStream { $0.finish(throwing: error) } }
    }
    public func invokeTool(_ invocation: ToolInvocation) async throws -> ToolResult {
        try await active().invokeTool(invocation)
    }
    public func agenticChat(_ prompt: String, options: GenerationOptions?) async throws -> String {
        try await active().agenticChat(prompt, options: options)
    }
    public func submitFeedback(_ signal: FeedbackSignal) async throws {
        try await active().submitFeedback(signal)
    }

    public func dispose() async {
        lock.lock(); if disposed { lock.unlock(); return }; disposed = true; lock.unlock()
        await local.dispose()
        await cloud.dispose()
    }
}

// =====================================================================
// AIApiClient (cloud proxy)
// =====================================================================

/// Transport seam for `AIApiClient`. The C# implementation posts JSON to a
/// remote ButlerAPI over HTTP; Swift injects the transport so the client is
/// testable in-memory and has no networking dependency. Implementations map the
/// logical endpoint + JSON body to a response body string.
public protocol IButlerHttpTransport: Sendable {
    /// GET the health endpoint. Throws on non-success.
    func health() async throws
    /// POST `bodyJson` to `path`; return the response body as a UTF-8 string.
    func post(path: String, bodyJson: String) async throws -> String
    /// POST `bodyJson` to `path` and stream SSE `data:` lines as they arrive.
    func postStream(path: String, bodyJson: String) -> AsyncThrowingStream<String, Error>
}

/// `IAIService` that proxies requests to a remote ButlerAPI. Wire routes mirror
/// the C# `AIApiClient`: `api/butler/{health,ask,chat,stream,agentic,tool,feedback}`.
/// Ported from `AIApiClient`; the concrete HTTP is injected via
/// `IButlerHttpTransport`.
public final class AIApiClient: IAIService, @unchecked Sendable {
    private let transport: any IButlerHttpTransport
    private let lock = NSLock()
    private var ready = false
    private var disposed = false

    public init(transport: any IButlerHttpTransport) {
        self.transport = transport
    }

    public var isReady: Bool { lock.lock(); defer { lock.unlock() }; return ready }

    public func start() async throws {
        try await transport.health()
        lock.lock(); ready = true; lock.unlock()
    }

    public func stop() async throws {
        lock.lock(); ready = false; lock.unlock()
    }

    public func ask(_ question: String) async throws -> String {
        let body = Self.jsonObject(["question": question])
        let resp = try await transport.post(path: "api/butler/ask", bodyJson: body)
        return Self.readText(resp)
    }

    public func chat(_ messages: [ChatMessage], options: GenerationOptions?) async throws -> String {
        let body = Self.chatRequestJson(messages, options)
        let resp = try await transport.post(path: "api/butler/chat", bodyJson: body)
        return Self.readText(resp)
    }

    public func stream(_ messages: [ChatMessage], options: GenerationOptions?) -> AsyncThrowingStream<String, Error> {
        let body = Self.chatRequestJson(messages, options)
        let raw = transport.postStream(path: "api/butler/stream", bodyJson: body)
        // Each SSE frame is "data: {token}"; stops on "data: [DONE]".
        return AsyncThrowingStream { continuation in
            let task = Task {
                do {
                    for try await line in raw {
                        if Task.isCancelled { break }
                        guard line.hasPrefix("data:") else { continue }
                        let token = String(line.dropFirst("data:".count)).trimmingCharacters(in: .whitespaces)
                        if token == "[DONE]" { break }
                        if !token.isEmpty { continuation.yield(token) }
                    }
                    continuation.finish()
                } catch {
                    continuation.finish(throwing: error)
                }
            }
            continuation.onTermination = { _ in task.cancel() }
        }
    }

    public func agenticChat(_ prompt: String, options: GenerationOptions?) async throws -> String {
        var dict: [String: Any] = ["prompt": prompt]
        if let o = options { dict["options"] = Self.optionsDict(o) }
        let resp = try await transport.post(path: "api/butler/agentic", bodyJson: Self.jsonObject(dict))
        return Self.readText(resp)
    }

    public func invokeTool(_ invocation: ToolInvocation) async throws -> ToolResult {
        var argDict: [String: Any] = [:]
        for (k, v) in invocation.arguments { argDict[k] = v ?? NSNull() }
        let body = Self.jsonObject(["name": invocation.toolName, "arguments": argDict])
        let resp = try await transport.post(path: "api/butler/tool", bodyJson: body)
        guard let data = resp.data(using: .utf8),
              let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            return ToolResult.failure(toolName: invocation.toolName, error: "Empty response from cloud")
        }
        let success = (obj["success"] as? Bool) ?? (obj["Success"] as? Bool) ?? false
        let result = obj["result"] ?? obj["Result"]
        let error = (obj["error"] as? String) ?? (obj["Error"] as? String)
        return ToolResult(toolName: invocation.toolName, success: success,
                          result: result, error: error)
    }

    public func submitFeedback(_ signal: FeedbackSignal) async throws {
        let body = Self.jsonObject([
            "id": signal.id.uuidString,
            "polarity": signal.polarity.rawValue,
            "userText": signal.userText,
            "assistantText": signal.assistantText,
            "comment": signal.comment ?? NSNull(),
        ])
        _ = try await transport.post(path: "api/butler/feedback", bodyJson: body)
    }

    public func dispose() async {
        lock.lock(); disposed = true; ready = false; lock.unlock()
    }

    // ---- JSON helpers ----

    private static func readText(_ resp: String) -> String {
        guard let data = resp.data(using: .utf8),
              let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            return ""
        }
        return (obj["text"] as? String) ?? (obj["Text"] as? String) ?? ""
    }

    private static func optionsDict(_ o: GenerationOptions) -> [String: Any] {
        var d: [String: Any] = [
            "maxTokens": o.maxTokens,
            "temperature": o.temperature,
            "topP": o.topP,
            "topK": o.topK,
        ]
        if let seed = o.seed { d["seed"] = seed }
        if let stops = o.stopSequences { d["stopSequences"] = stops }
        return d
    }

    private static func chatRequestJson(_ messages: [ChatMessage], _ options: GenerationOptions?) -> String {
        var dict: [String: Any] = [
            "messages": messages.map { ["role": $0.role, "content": $0.content] },
        ]
        if let o = options { dict["options"] = optionsDict(o) }
        return jsonObject(dict)
    }

    private static func jsonObject(_ dict: [String: Any]) -> String {
        guard let data = try? JSONSerialization.data(withJSONObject: dict, options: [.sortedKeys]),
              let s = String(data: data, encoding: .utf8) else {
            return "{}"
        }
        return s
    }
}

// =====================================================================
// Disposable (lightweight ARC-friendly analogue of IDisposable)
// =====================================================================

/// Minimal synchronous dispose contract used by generators / subscriptions in
/// this module. Types that need teardown conform; callers dispose explicitly.
public protocol Disposable: AnyObject {
    func dispose()
}
