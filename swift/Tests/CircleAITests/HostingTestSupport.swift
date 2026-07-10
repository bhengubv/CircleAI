// HostingTestSupport.swift
//
// Shared deterministic test doubles for the Hosting port tests.

import Foundation
import XCTest
@testable import CircleAI

// =====================================================================
// EchoGenerator — deterministic IChatGenerator
// =====================================================================

/// Deterministic chat generator. `generate` returns `scriptedReply` when set,
/// otherwise "echo:<last user content>". Records the messages of each call and
/// can be told to throw. `stream` yields the same text as one chunk.
final class EchoGenerator: IChatGenerator, @unchecked Sendable {
    private let lock = NSLock()
    private let scriptedReply: String?
    private let throwsOnGenerate: Bool
    private(set) var generateCalls: [[ChatMessage]] = []

    init(scriptedReply: String? = nil, throwsOnGenerate: Bool = false) {
        self.scriptedReply = scriptedReply
        self.throwsOnGenerate = throwsOnGenerate
    }

    private func compose(_ messages: [ChatMessage]) -> String {
        if let scripted = scriptedReply { return scripted }
        let lastUser = messages.last { $0.role.caseInsensitiveCompare("user") == .orderedSame }?.content ?? ""
        return "echo:\(lastUser)"
    }

    func generate(messages: [ChatMessage], options: GenerationOptions?) async throws -> String {
        lock.lock(); generateCalls.append(messages); lock.unlock()
        if throwsOnGenerate { throw NSError(domain: "EchoGenerator", code: 1) }
        return compose(messages)
    }

    func stream(messages: [ChatMessage], options: GenerationOptions?) -> AsyncStream<String> {
        let text = compose(messages)
        lock.lock(); generateCalls.append(messages); lock.unlock()
        return AsyncStream { continuation in
            continuation.yield(text)
            continuation.finish()
        }
    }

    var lastGenerateMessages: [ChatMessage]? {
        lock.lock(); defer { lock.unlock() }; return generateCalls.last
    }
    var callCount: Int { lock.lock(); defer { lock.unlock() }; return generateCalls.count }
}

/// A generator that emits a fixed `<tool_call>...` block on its first call, then
/// a plain reply — used to drive the agentic loop.
final class ToolCallGenerator: IChatGenerator, @unchecked Sendable {
    private let lock = NSLock()
    private let toolName: String
    private let finalReply: String
    private var calls = 0

    init(toolName: String, finalReply: String = "done") {
        self.toolName = toolName
        self.finalReply = finalReply
    }

    func generate(messages: [ChatMessage], options: GenerationOptions?) async throws -> String {
        lock.lock(); calls += 1; let n = calls; lock.unlock()
        if n == 1 {
            return "<tool_call>{\"name\": \"\(toolName)\", \"arguments\": {\"q\": \"x\"}}</tool_call>"
        }
        return finalReply
    }

    func stream(messages: [ChatMessage], options: GenerationOptions?) -> AsyncStream<String> {
        AsyncStream { c in c.finish() }
    }

    var callCount: Int { lock.lock(); defer { lock.unlock() }; return calls }
}

// =====================================================================
// FakeButler — deterministic IAIService
// =====================================================================

/// Deterministic `IAIService` used to exercise services that consume a butler
/// (ScheduledAIService, ProactiveReasoningService, FallbackAIService, workers).
final class FakeButler: IAIService, @unchecked Sendable {
    private let lock = NSLock()
    private let reply: String?
    private let throwsError: Bool
    private(set) var asked: [String] = []
    private(set) var started = false
    private(set) var startCount = 0
    private(set) var prewarmCount = 0

    init(reply: String? = "ok", throwsError: Bool = false) {
        self.reply = reply
        self.throwsError = throwsError
    }

    var isReady: Bool { lock.lock(); defer { lock.unlock() }; return started }

    func start() async throws {
        lock.lock(); started = true; startCount += 1; lock.unlock()
    }
    func stop() async throws { lock.lock(); started = false; lock.unlock() }

    func ask(_ question: String) async throws -> String {
        lock.lock(); asked.append(question); lock.unlock()
        if throwsError { throw NSError(domain: "FakeButler", code: 1) }
        return reply ?? "echo:\(question)"
    }

    func chat(_ messages: [ChatMessage], options: GenerationOptions?) async throws -> String {
        let last = messages.last { $0.role == "user" }?.content ?? ""
        return try await ask(last)
    }

    func stream(_ messages: [ChatMessage], options: GenerationOptions?) -> AsyncThrowingStream<String, Error> {
        let last = messages.last { $0.role == "user" }?.content ?? ""
        let text = reply ?? "echo:\(last)"
        let fail = throwsError
        return AsyncThrowingStream { c in
            if fail { c.finish(throwing: NSError(domain: "FakeButler", code: 1)); return }
            c.yield(text); c.finish()
        }
    }

    func invokeTool(_ invocation: ToolInvocation) async throws -> ToolResult {
        ToolResult.ok(toolName: invocation.toolName, result: "ok")
    }
    func agenticChat(_ prompt: String, options: GenerationOptions?) async throws -> String {
        try await ask(prompt)
    }
    func submitFeedback(_ signal: FeedbackSignal) async throws {}
    func prewarm() async throws { lock.lock(); prewarmCount += 1; lock.unlock(); try await start() }
    func dispose() async {}
}

// =====================================================================
// InMemoryButlerTransport — deterministic IButlerHttpTransport
// =====================================================================

/// In-memory transport backing `AIApiClient` / `AIHttpClient`. Routes each POST
/// path to a canned response so the cloud proxy can be tested without network.
final class InMemoryButlerTransport: IButlerHttpTransport, @unchecked Sendable {
    private let lock = NSLock()
    private var healthy: Bool
    private(set) var posts: [(path: String, body: String)] = []

    init(healthy: Bool = true) { self.healthy = healthy }

    func setHealthy(_ v: Bool) { lock.lock(); healthy = v; lock.unlock() }

    func health() async throws {
        lock.lock(); let ok = healthy; lock.unlock()
        if !ok { throw NSError(domain: "transport", code: 503) }
    }

    func post(path: String, bodyJson: String) async throws -> String {
        lock.lock(); posts.append((path, bodyJson)); lock.unlock()
        if path.hasSuffix("/ask") || path.hasSuffix("/chat") || path.hasSuffix("/agentic") {
            return "{\"text\":\"cloud-reply\"}"
        }
        if path.hasSuffix("/tool") {
            return "{\"toolName\":\"t\",\"success\":true,\"result\":\"r\"}"
        }
        if path.hasSuffix("/feedback") { return "{}" }
        return "{}"
    }

    func postStream(path: String, bodyJson: String) -> AsyncThrowingStream<String, Error> {
        lock.lock(); posts.append((path, bodyJson)); lock.unlock()
        return AsyncThrowingStream { c in
            c.yield("data: tok1")
            c.yield("data: tok2")
            c.yield("data: [DONE]")
            c.finish()
        }
    }

    var postCount: Int { lock.lock(); defer { lock.unlock() }; return posts.count }
}

// =====================================================================
// Recording IAIServiceObserver
// =====================================================================

/// Records observer callbacks for assertions.
final class RecordingServiceObserver: IAIServiceObserver, @unchecked Sendable {
    private let lock = NSLock()
    private(set) var started = 0
    private(set) var stopped = 0
    private(set) var chatEvents: [AIChatEvent] = []
    private(set) var streamStarted = 0
    private(set) var streamCompleted = 0
    private(set) var toolEvents: [AIToolEvent] = []
    private(set) var fetches: [(String, Bool)] = []

    func onStarted() async { lock.lock(); started += 1; lock.unlock() }
    func onStopped() async { lock.lock(); stopped += 1; lock.unlock() }
    func onChatCompleted(_ event: AIChatEvent) async { lock.lock(); chatEvents.append(event); lock.unlock() }
    func onStreamStarted(_ event: AIStreamEvent) async { lock.lock(); streamStarted += 1; lock.unlock() }
    func onStreamCompleted(_ event: AIStreamEvent) async { lock.lock(); streamCompleted += 1; lock.unlock() }
    func onToolInvoked(_ event: AIToolEvent) async { lock.lock(); toolEvents.append(event); lock.unlock() }
    func onModelFetching(_ modelId: String, autoSelected: Bool) async {
        lock.lock(); fetches.append((modelId, autoSelected)); lock.unlock()
    }

    var chatCount: Int { lock.lock(); defer { lock.unlock() }; return chatEvents.count }
    var toolCount: Int { lock.lock(); defer { lock.unlock() }; return toolEvents.count }
}

// =====================================================================
// StaticRam — deterministic RAM source for FallbackAIService
// =====================================================================

struct StaticRam: IAvailableRamSource {
    let availableRamBytes: Int64
}

// =====================================================================
// EchoToolBridge — deterministic IToolBridge
// =====================================================================

final class EchoToolBridge: IToolBridge, @unchecked Sendable {
    let availableTools: [ToolDefinition] = []
    private(set) var invoked: [String] = []
    private let lock = NSLock()

    func invoke(_ invocation: ToolInvocation) async throws -> ToolResult {
        lock.lock(); invoked.append(invocation.toolName); lock.unlock()
        return ToolResult.ok(toolName: invocation.toolName, result: "tool-ok")
    }
    func getAvailableTools() async throws -> [ToolDefinition] { availableTools }
    var invokeCount: Int { lock.lock(); defer { lock.unlock() }; return invoked.count }
}
