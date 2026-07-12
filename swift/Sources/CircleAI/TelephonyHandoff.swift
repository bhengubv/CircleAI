// TelephonyHandoff.swift
//
// Port of the CircleAI.Telephony multi-agent + escalation + dashboard +
// lifecycle + telemetry + voice-loop-as-tool layer:
//   • AgentHandoff.cs          — CallAgent, HandoffResult, IAgentHandoffOrchestrator,
//                                DefaultAgentHandoffOrchestrator.
//   • ConsultEscalation.cs     — ConsultRequest, ConsultAnswer, IConsultChannel,
//                                ConsultEscalator, HttpWebhookConsultChannel.
//   • DashboardData.cs         — LiveCallRow, RecentCallRow, AgentHealthRow,
//                                DashboardSummary, DashboardSnapshot,
//                                IDashboardDataSource, DefaultDashboardDataSource.
//   • SpeechLifecycleEvents.cs — SpeechLifecycleEvent (+ concrete events),
//                                SpeechEventKind, ISpeechSubscription,
//                                ISpeechLifecycleBus, InMemorySpeechLifecycleBus.
//   • Telemetry.cs             — VoiceLoopTelemetry (+ IVoiceLoopSpan /
//                                IVoiceLoopTelemetrySink abstraction).
//   • VoiceLoopAsTool.cs       — VoiceLoopToolRequest, VoiceLoopToolResult,
//                                IVoiceLoopTool, VoiceLoopAsTool.
//
// DESIGN DEVIATIONS (called out for the Mac build):
//   • SpeechLifecycleEvents: C# models a `record` hierarchy and dispatches via
//     reflection over `BaseType`. Swift has no equivalent runtime base-walk, so
//     the event union is a single `SpeechLifecycleEvent` struct carrying a
//     `SpeechEventKind` payload enum. Subscribers filter by an optional kind
//     (nil == all), reproducing "subscribe to a specific type OR the base for
//     all" without reflection. Same observable behaviour, idiomatic Swift.
//   • Telemetry: C# uses System.Diagnostics.ActivitySource (OpenTelemetry).
//     There is no portable Swift OTel primitive in this module, so spans are
//     abstracted behind `IVoiceLoopTelemetrySink` / `IVoiceLoopSpan`. The default
//     sink is a no-op (like an ActivitySource with no listener). Names /
//     tag-keys / call shape are preserved so a host can bridge to swift-otel.
//   • The C# tool `ToolDefinition` returned by VoiceLoopAsTool.Descriptor maps to
//     the `Telephony…`-prefixed Swift struct `TelephonyToolDefinition`.
//   • `ILogger` → an optional `@Sendable (String) -> Void` log hook (no-op default).
//   • `HttpClient` in HttpWebhookConsultChannel → the injected
//     `ITelephonyHttpTransport` (no sockets).

import Foundation

// =====================================================================
// AgentHandoff.cs
// =====================================================================

/// One AI agent persona that can be handed control of a call. Port of the C#
/// record `CircleAI.Telephony.CallAgent`.
public struct CallAgent: Sendable, Equatable {
    /// Stable id ("reception" / "billing" / "tier2-support").
    public let agentId: String
    /// Friendly name surfaced to logging + analytics.
    public let displayName: String
    /// Persona instructions.
    public let systemPrompt: String
    /// Optional first sentence the agent says when it takes over.
    public let greetingText: String?

    public init(agentId: String, displayName: String, systemPrompt: String, greetingText: String? = nil) {
        self.agentId = agentId
        self.displayName = displayName
        self.systemPrompt = systemPrompt
        self.greetingText = greetingText
    }
}

/// Outcome of a handoff attempt. Port of the C# record
/// `CircleAI.Telephony.HandoffResult`.
public struct HandoffResult: Sendable, Equatable {
    public let succeeded: Bool
    public let failureReason: String?
    public let activeAgent: CallAgent?

    public init(succeeded: Bool, failureReason: String?, activeAgent: CallAgent?) {
        self.succeeded = succeeded
        self.failureReason = failureReason
        self.activeAgent = activeAgent
    }
}

/// Drives mid-call agent handoff. Port of
/// `CircleAI.Telephony.IAgentHandoffOrchestrator`.
public protocol IAgentHandoffOrchestrator: Sendable {
    /// The agent currently in control of the call.
    var currentAgent: CallAgent? { get }
    /// Available agents indexed by id.
    var agentCatalog: [String: CallAgent] { get }
    /// Hand the call over to `targetAgentId`; speaks the greeting via `tts`.
    func handoff(session: ICallSession, targetAgentId: String, tts: @escaping BriefingSynthesiser) async -> HandoffResult
    /// Register / replace an agent in the catalog at runtime.
    func registerAgent(_ agent: CallAgent)
    /// Set the initial agent on a fresh call without TTS (no greeting).
    func setInitialAgent(_ agentId: String) throws
}

/// Default in-memory orchestrator. Thread-safe via a simple lock. Port of
/// `CircleAI.Telephony.DefaultAgentHandoffOrchestrator`.
///
/// The catalog is keyed lowercased (OrdinalIgnoreCase) but exposes the
/// original-cased `CallAgent` values.
public final class DefaultAgentHandoffOrchestrator: IAgentHandoffOrchestrator, @unchecked Sendable {
    private let gate = NSLock()
    private var agents: [String: CallAgent] = [:]
    private var current: CallAgent?
    private let log: @Sendable (String) -> Void

    public init(seed: [CallAgent]? = nil, log: (@Sendable (String) -> Void)? = nil) {
        self.log = log ?? { _ in }
        if let seed {
            for agent in seed { agents[agent.agentId.lowercased()] = agent }
        }
    }

    public var currentAgent: CallAgent? {
        gate.lock(); defer { gate.unlock() }
        return current
    }

    public var agentCatalog: [String: CallAgent] {
        gate.lock(); defer { gate.unlock() }
        // Re-key by the original agent id (C# exposes AgentId-keyed catalog).
        var out: [String: CallAgent] = [:]
        for a in agents.values { out[a.agentId] = a }
        return out
    }

    public func registerAgent(_ agent: CallAgent) {
        precondition(!agent.agentId.isBlank, "AgentId is required.")
        gate.lock(); agents[agent.agentId.lowercased()] = agent; gate.unlock()
    }

    public func setInitialAgent(_ agentId: String) throws {
        gate.lock(); defer { gate.unlock() }
        guard let agent = agents[agentId.lowercased()] else {
            throw TelephonyError.invalidOperation("Agent '\(agentId)' is not registered.")
        }
        current = agent
    }

    public func handoff(
        session: ICallSession,
        targetAgentId: String,
        tts: @escaping BriefingSynthesiser
    ) async -> HandoffResult {
        if targetAgentId.isBlank {
            return HandoffResult(succeeded: false, failureReason: "targetAgentId is required", activeAgent: currentAgent)
        }

        let target: CallAgent
        let previous: CallAgent?
        gate.lock()
        guard let found = agents[targetAgentId.lowercased()] else {
            let cur = current
            gate.unlock()
            return HandoffResult(succeeded: false, failureReason: "Agent '\(targetAgentId)' is not registered.", activeAgent: cur)
        }
        target = found
        previous = current
        if let prev = previous, prev.agentId.caseInsensitiveCompare(target.agentId) == .orderedSame {
            gate.unlock()
            return HandoffResult(succeeded: true, failureReason: nil, activeAgent: prev)
        }
        current = target
        gate.unlock()

        log("Call \(session.info.callId) handed off from \(previous?.displayName ?? "(none)") to \(target.displayName)")

        if let greeting = target.greetingText, !greeting.isBlank {
            do {
                let greetingPcm = try await tts(greeting)
                if !greetingPcm.isEmpty {
                    try await session.sendAudio(AudioFrame(pcm: greetingPcm, format: .pcm24000, offset: 0))
                }
            } catch {
                log("Greeting playback failed during handoff to \(target.agentId): \(error)")
            }
        }

        return HandoffResult(succeeded: true, failureReason: nil, activeAgent: target)
    }
}

// =====================================================================
// ConsultEscalation.cs
// =====================================================================

/// Question the AI asks a human expert. Port of the C# record
/// `CircleAI.Telephony.ConsultRequest`.
public struct ConsultRequest: Sendable, Equatable {
    /// Source call id for the audit trail.
    public let callId: String
    /// Plain-English question text.
    public let question: String
    /// Structured context (caller intent, last few utterances, customer record).
    public let contextJson: String
    /// "normal" / "high".
    public let urgency: String

    public init(callId: String, question: String, contextJson: String, urgency: String = "normal") {
        self.callId = callId
        self.question = question
        self.contextJson = contextJson
        self.urgency = urgency
    }
}

/// Human reply. Port of the C# record `CircleAI.Telephony.ConsultAnswer`.
public struct ConsultAnswer: Sendable, Equatable {
    public let answer: String
    /// true = expert confirmed.
    public let confidence: Bool
    public let notes: String?

    public init(answer: String, confidence: Bool, notes: String? = nil) {
        self.answer = answer
        self.confidence = confidence
        self.notes = notes
    }
}

/// Channel for asking a human expert. Port of
/// `CircleAI.Telephony.IConsultChannel`.
public protocol IConsultChannel: Sendable {
    var name: String { get }
    func ask(_ request: ConsultRequest, timeout: TimeInterval) async throws -> ConsultAnswer?
}

/// Default escalation driver: try channels in order until one returns within the
/// timeout. Port of `CircleAI.Telephony.ConsultEscalator`.
public final class ConsultEscalator: @unchecked Sendable {
    private let channels: [IConsultChannel]
    private let log: @Sendable (String) -> Void

    public init(channels: [IConsultChannel], log: (@Sendable (String) -> Void)? = nil) {
        self.channels = channels
        self.log = log ?? { _ in }
    }

    /// Walk channels in order; first one to return a non-nil answer wins.
    public func escalate(_ request: ConsultRequest, timeoutPerChannel: TimeInterval) async -> ConsultAnswer? {
        for channel in channels {
            do {
                if let answer = try await channel.ask(request, timeout: timeoutPerChannel) {
                    log("Consult \(request.callId) answered by \(channel.name)")
                    return answer
                }
            } catch {
                log("Consult channel \(channel.name) threw: \(error)")
            }
        }
        return nil
    }
}

/// HTTP webhook channel — POSTs the request, expects a JSON reply. Port of
/// `CircleAI.Telephony.HttpWebhookConsultChannel` (HttpClient → injected
/// `ITelephonyHttpTransport`).
///
/// The C# `JsonContent.Create(request)` serialises the ConsultRequest; the Swift
/// port emits an equivalent JSON body by hand (the four fields), preserving the
/// snake/camel key names the C# `System.Text.Json` default would produce for the
/// record's PascalCase properties (CallId → "CallId", etc.). If a specific host
/// needs different casing it can wrap this channel.
public final class HttpWebhookConsultChannel: IConsultChannel, @unchecked Sendable {
    private let http: ITelephonyHttpTransport
    private let endpoint: URL
    private let _name: String

    public init(http: ITelephonyHttpTransport, endpoint: URL, name: String = "webhook") {
        self.http = http
        self.endpoint = endpoint
        self._name = name
    }

    public var name: String { _name }

    public func ask(_ request: ConsultRequest, timeout: TimeInterval) async throws -> ConsultAnswer? {
        // Enforce the per-channel timeout by racing the HTTP send against a sleep
        // (the transport takes no CancellationToken). On timeout, return nil —
        // matching the C# `catch (OperationCanceledException) => null`.
        let body = Self.encodeRequest(request)
        let req = TelephonyHttpRequest(
            method: .post,
            path: endpoint.absoluteString,
            body: body,
            contentType: .json)

        let resp: TelephonyHttpResponse? = await withTaskGroup(
            of: TelephonyHttpResponse?.self, returning: TelephonyHttpResponse?.self
        ) { group in
            group.addTask { try? await self.http.send(req) }
            group.addTask {
                try? await Task.sleep(nanoseconds: UInt64((timeout * 1_000_000_000).rounded()))
                return nil
            }
            let first = await group.next() ?? nil
            group.cancelAll()
            return first
        }

        guard let resp, resp.isSuccessStatusCode else { return nil }

        // Parse { "answer": string, "confidence": bool, "notes": string? }.
        guard let root = try? TelephonyJson.parse(resp.body) else { return nil }
        guard let answer = root["answer"] as? String, !answer.isBlank else { return nil }
        // C#: confidence true only when the JSON value is boolean `true`.
        let confidence = Self.isJsonTrue(root["confidence"])
        let notes = root["notes"] as? String
        return ConsultAnswer(answer: answer, confidence: confidence, notes: notes)
    }

    private static func isJsonTrue(_ raw: Any?) -> Bool {
        guard let n = raw as? NSNumber, CFGetTypeID(n) == CFBooleanGetTypeID() else { return false }
        return n.boolValue
    }

    /// Minimal JSON body matching the C# record serialisation (PascalCase keys).
    private static func encodeRequest(_ r: ConsultRequest) -> Data {
        let json =
            "{\"CallId\":\(jsonString(r.callId))," +
            "\"Question\":\(jsonString(r.question))," +
            "\"ContextJson\":\(jsonString(r.contextJson))," +
            "\"Urgency\":\(jsonString(r.urgency))}"
        return Data(json.utf8)
    }

    private static func jsonString(_ s: String) -> String {
        var out = "\""
        for scalar in s.unicodeScalars {
            switch scalar {
            case "\"": out += "\\\""
            case "\\": out += "\\\\"
            case "\n": out += "\\n"
            case "\r": out += "\\r"
            case "\t": out += "\\t"
            default:
                if scalar.value < 0x20 {
                    out += String(format: "\\u%04x", scalar.value)
                } else {
                    out.unicodeScalars.append(scalar)
                }
            }
        }
        out += "\""
        return out
    }
}

// =====================================================================
// DashboardData.cs
// =====================================================================

/// One row in the live-calls panel. Port of the C# record
/// `CircleAI.Telephony.LiveCallRow`.
public struct LiveCallRow: Sendable, Equatable {
    public let callId: String
    public let carrier: String
    public let from: String
    public let to: String
    public let status: CallStatus
    public let startedAtUtc: Date
    public let duration: TimeInterval
    public let costSoFar: Decimal

    public init(
        callId: String, carrier: String, from: String, to: String,
        status: CallStatus, startedAtUtc: Date, duration: TimeInterval, costSoFar: Decimal
    ) {
        self.callId = callId
        self.carrier = carrier
        self.from = from
        self.to = to
        self.status = status
        self.startedAtUtc = startedAtUtc
        self.duration = duration
        self.costSoFar = costSoFar
    }
}

/// One row in the recent-calls panel. Port of the C# record
/// `CircleAI.Telephony.RecentCallRow`.
public struct RecentCallRow: Sendable, Equatable {
    public let callId: String
    public let carrier: String
    public let from: String
    public let to: String
    public let finalStatus: CallStatus
    public let endedAtUtc: Date
    public let duration: TimeInterval
    public let totalCost: Decimal

    public init(
        callId: String, carrier: String, from: String, to: String,
        finalStatus: CallStatus, endedAtUtc: Date, duration: TimeInterval, totalCost: Decimal
    ) {
        self.callId = callId
        self.carrier = carrier
        self.from = from
        self.to = to
        self.finalStatus = finalStatus
        self.endedAtUtc = endedAtUtc
        self.duration = duration
        self.totalCost = totalCost
    }
}

/// Agent health summary row. Port of the C# record
/// `CircleAI.Telephony.AgentHealthRow`.
public struct AgentHealthRow: Sendable, Equatable {
    public let agentLabel: String
    /// "Healthy" / "Degraded" / "CoolingDown".
    public let health: String
    public let consecutiveFailures: Int

    public init(agentLabel: String, health: String, consecutiveFailures: Int) {
        self.agentLabel = agentLabel
        self.health = health
        self.consecutiveFailures = consecutiveFailures
    }
}

/// Top-of-page summary card. Port of the C# record
/// `CircleAI.Telephony.DashboardSummary`.
public struct DashboardSummary: Sendable, Equatable {
    public let liveCallCount: Int
    public let currentSpendUsd: Decimal
    public let callsLast24h: Int
    public let pauseFalseAlarmRate: Float

    public init(liveCallCount: Int, currentSpendUsd: Decimal, callsLast24h: Int, pauseFalseAlarmRate: Float) {
        self.liveCallCount = liveCallCount
        self.currentSpendUsd = currentSpendUsd
        self.callsLast24h = callsLast24h
        self.pauseFalseAlarmRate = pauseFalseAlarmRate
    }
}

/// Full dashboard snapshot. Port of the C# record
/// `CircleAI.Telephony.DashboardSnapshot`.
public struct DashboardSnapshot: Sendable, Equatable {
    public let summary: DashboardSummary
    public let liveCalls: [LiveCallRow]
    public let recentCalls: [RecentCallRow]
    public let agentHealth: [AgentHealthRow]
    public let latencyByStage: [LatencySnapshot]

    public init(
        summary: DashboardSummary,
        liveCalls: [LiveCallRow],
        recentCalls: [RecentCallRow],
        agentHealth: [AgentHealthRow],
        latencyByStage: [LatencySnapshot]
    ) {
        self.summary = summary
        self.liveCalls = liveCalls
        self.recentCalls = recentCalls
        self.agentHealth = agentHealth
        self.latencyByStage = latencyByStage
    }
}

/// Dashboard data source: hosts compose live + recent + health + latency feeds.
/// Port of `CircleAI.Telephony.IDashboardDataSource`.
public protocol IDashboardDataSource: Sendable {
    func snapshot() async -> DashboardSnapshot
}

/// Default composed data source — pulls from supplied feed closures. Port of
/// `CircleAI.Telephony.DefaultDashboardDataSource` (the five `Func<>`s become
/// `@Sendable` closures).
public final class DefaultDashboardDataSource: IDashboardDataSource, @unchecked Sendable {
    private let liveCalls: @Sendable () -> [LiveCallRow]
    private let recentCalls: @Sendable () -> [RecentCallRow]
    private let agentHealth: @Sendable () -> [AgentHealthRow]
    private let latency: @Sendable () -> [LatencySnapshot]
    private let summary: @Sendable () -> DashboardSummary

    public init(
        liveCalls: @escaping @Sendable () -> [LiveCallRow],
        recentCalls: @escaping @Sendable () -> [RecentCallRow],
        agentHealth: @escaping @Sendable () -> [AgentHealthRow],
        latency: @escaping @Sendable () -> [LatencySnapshot],
        summary: @escaping @Sendable () -> DashboardSummary
    ) {
        self.liveCalls = liveCalls
        self.recentCalls = recentCalls
        self.agentHealth = agentHealth
        self.latency = latency
        self.summary = summary
    }

    public func snapshot() async -> DashboardSnapshot {
        DashboardSnapshot(
            summary: summary(),
            liveCalls: liveCalls(),
            recentCalls: recentCalls(),
            agentHealth: agentHealth(),
            latencyByStage: latency())
    }
}

// =====================================================================
// SpeechLifecycleEvents.cs
// =====================================================================

/// The concrete kind + payload of a speech-lifecycle event. Port of the C#
/// record hierarchy (`CallerSpeechStartedEvent`, `TranscriptFinalEvent_v2`,
/// `AgentSpeakingFinishedEvent`, `SpeechErrorEvent`, …) collapsed into a Swift
/// discriminated union — Swift has no runtime base-type walk for pub/sub.
public enum SpeechEventKind: Sendable, Equatable {
    case callerSpeechStarted
    case callerSpeechEnded
    case transcriptInterim(text: String)
    case transcriptFinal(text: String)
    case agentThinking
    case agentSpeakingStarted
    case agentSpeakingFinished(spokenDuration: TimeInterval)
    case speechError(stage: String, message: String)
}

/// One lifecycle event. Port of the C# `SpeechLifecycleEvent` base (CallId + At)
/// plus its concrete subtypes' payload via `kind`.
public struct SpeechLifecycleEvent: Sendable, Equatable {
    public let callId: String
    public let at: Date
    public let kind: SpeechEventKind

    public init(callId: String, at: Date, kind: SpeechEventKind) {
        self.callId = callId
        self.at = at
        self.kind = kind
    }

    /// A stable selector for the kind, ignoring associated payload — used for
    /// per-kind subscription filtering (the analogue of C#'s `typeof(TEvent)`).
    public var kindSelector: SpeechEventSelector {
        switch kind {
        case .callerSpeechStarted: return .callerSpeechStarted
        case .callerSpeechEnded: return .callerSpeechEnded
        case .transcriptInterim: return .transcriptInterim
        case .transcriptFinal: return .transcriptFinal
        case .agentThinking: return .agentThinking
        case .agentSpeakingStarted: return .agentSpeakingStarted
        case .agentSpeakingFinished: return .agentSpeakingFinished
        case .speechError: return .speechError
        }
    }
}

/// Payload-free selector matching one concrete event type. Passing `nil` to
/// `subscribe` means "all events" (the C# `SpeechLifecycleEvent` base subscription).
public enum SpeechEventSelector: Sendable, Equatable, CaseIterable {
    case callerSpeechStarted
    case callerSpeechEnded
    case transcriptInterim
    case transcriptFinal
    case agentThinking
    case agentSpeakingStarted
    case agentSpeakingFinished
    case speechError
}

// NOTE: The C# `SpeechLifecycleEvents.cs` declares its own
// `ISpeechSubscription : IDisposable`. The Swift module already has an
// identically-shaped `ISpeechSubscription` (SpeechContracts.swift:
// `protocol ISpeechSubscription: AnyObject, Sendable { func dispose() }`).
// Since the flattened module can only declare it once, this port REUSES that
// existing protocol rather than redeclaring it — behaviour is identical.

/// Speech lifecycle pub/sub. Port of `CircleAI.Telephony.ISpeechLifecycleBus`.
public protocol ISpeechLifecycleBus: Sendable {
    /// Subscribe to a specific event kind, or all kinds when `kind == nil`.
    /// Handlers are invoked synchronously on publish (as in C#).
    func subscribe(_ kind: SpeechEventSelector?, handler: @escaping @Sendable (SpeechLifecycleEvent) -> Void) -> ISpeechSubscription
    /// Publish one event. All matching subscribers are invoked.
    func publish(_ event: SpeechLifecycleEvent)
}

/// Default in-memory bus. Port of
/// `CircleAI.Telephony.InMemorySpeechLifecycleBus`.
///
/// The C# reflection-based hierarchy walk is replaced by an explicit "all"
/// bucket (`nil` selector) plus per-kind buckets; a published event notifies its
/// own kind bucket and the "all" bucket — reproducing "a base subscriber sees
/// every concrete type".
public final class InMemorySpeechLifecycleBus: ISpeechLifecycleBus, @unchecked Sendable {
    private let gate = NSLock()
    // key: selector or nil("all"); handlers keyed by monotonically increasing id.
    private var allSubscribers: [Int64: @Sendable (SpeechLifecycleEvent) -> Void] = [:]
    private var kindSubscribers: [SpeechEventSelector: [Int64: @Sendable (SpeechLifecycleEvent) -> Void]] = [:]
    private var nextHandle: Int64 = 0

    public init() {}

    public func subscribe(
        _ kind: SpeechEventSelector?,
        handler: @escaping @Sendable (SpeechLifecycleEvent) -> Void
    ) -> ISpeechSubscription {
        gate.lock()
        nextHandle += 1
        let id = nextHandle
        if let kind {
            var bucket = kindSubscribers[kind] ?? [:]
            bucket[id] = handler
            kindSubscribers[kind] = bucket
        } else {
            allSubscribers[id] = handler
        }
        gate.unlock()
        return SubHandle { [weak self] in
            guard let self else { return }
            self.gate.lock()
            if let kind {
                self.kindSubscribers[kind]?.removeValue(forKey: id)
            } else {
                self.allSubscribers.removeValue(forKey: id)
            }
            self.gate.unlock()
        }
    }

    public func publish(_ event: SpeechLifecycleEvent) {
        // Snapshot the matching handlers under the lock, invoke outside it (so a
        // handler that unsubscribes / re-publishes cannot deadlock on the lock).
        gate.lock()
        var handlers: [@Sendable (SpeechLifecycleEvent) -> Void] = []
        handlers.append(contentsOf: allSubscribers.values)
        if let bucket = kindSubscribers[event.kindSelector] {
            handlers.append(contentsOf: bucket.values)
        }
        gate.unlock()
        for h in handlers { h(event) }
    }

    private final class SubHandle: ISpeechSubscription, @unchecked Sendable {
        private let onDispose: @Sendable () -> Void
        private let l = NSLock()
        private var disposed = false
        init(_ onDispose: @escaping @Sendable () -> Void) { self.onDispose = onDispose }
        func dispose() {
            l.lock()
            if disposed { l.unlock(); return }
            disposed = true
            l.unlock()
            onDispose()
        }
    }
}

// =====================================================================
// Telemetry.cs
// =====================================================================

/// One in-flight voice-loop span. Port of the role of `System.Diagnostics.Activity`
/// for the subset VoiceLoopTelemetry uses (set tags, set outcome/status, end).
public protocol IVoiceLoopSpan: AnyObject, Sendable {
    func setTag(_ key: String, _ value: String?)
    func setOutcome(success: Bool, errorReason: String?)
    func end()
}

/// Sink that materialises spans. Port of the role of `ActivitySource`. A host
/// bridges this to swift-otel (or similar); the default is a no-op.
public protocol IVoiceLoopTelemetrySink: Sendable {
    /// Start a span with an operation name + initial tags. Returns nil when no
    /// listener is attached (mirrors `ActivitySource.StartActivity` returning
    /// null with no listeners).
    func startSpan(_ operation: String, tags: [String: String?]) -> IVoiceLoopSpan?
}

/// No-op telemetry sink — the default when nothing is wired (an ActivitySource
/// with no registered listener). `startSpan` returns nil so all downstream
/// tagging is skipped, exactly like the C# null-Activity path.
public final class NullVoiceLoopTelemetrySink: IVoiceLoopTelemetrySink, @unchecked Sendable {
    public init() {}
    public func startSpan(_ operation: String, tags: [String: String?]) -> IVoiceLoopSpan? { nil }
}

/// Voice-loop trace spans. Port of the C# static class
/// `CircleAI.Telephony.VoiceLoopTelemetry`.
///
/// C# exposes a static `ActivitySource`. Because Swift can't inject a listener
/// into a global the way OTel hooks ActivitySource, the sink is a settable
/// static (`sink`) defaulting to the no-op; a host assigns a real sink once at
/// startup. Operation names + tag keys are byte-identical to the C# spans so
/// dashboards pinned to them keep working.
public enum VoiceLoopTelemetry {
    /// ActivitySource name CircleAI uses for voice-loop spans.
    public static let sourceName = "CircleAI.Telephony.VoiceLoop"

    /// The active sink. Assign once at startup to bridge to a real exporter.
    /// Guarded so assignment/read are safe across threads.
    nonisolated(unsafe) private static var _sink: IVoiceLoopTelemetrySink = NullVoiceLoopTelemetrySink()
    private static let sinkLock = NSLock()

    public static var sink: IVoiceLoopTelemetrySink {
        get { sinkLock.lock(); defer { sinkLock.unlock() }; return _sink }
        set { sinkLock.lock(); _sink = newValue; sinkLock.unlock() }
    }

    /// Start a span for one voice loop turn.
    public static func startTurn(_ callId: String) -> IVoiceLoopSpan? {
        sink.startSpan("voice_loop.turn", tags: ["call.id": callId])
    }

    /// Start a span around the STT stage.
    public static func startAsr(_ backend: String) -> IVoiceLoopSpan? {
        sink.startSpan("voice_loop.asr", tags: ["backend": backend])
    }

    /// Start a span around the LLM stage.
    public static func startLlm(provider: String, model: String) -> IVoiceLoopSpan? {
        sink.startSpan("voice_loop.llm", tags: ["provider": provider, "model": model])
    }

    /// Start a span around the TTS stage.
    public static func startTts(backend: String, voiceId: String? = nil) -> IVoiceLoopSpan? {
        sink.startSpan("voice_loop.tts", tags: ["backend": backend, "voice": voiceId])
    }

    /// Tag a turn span with its outcome (mirrors the C# `RecordOutcome`).
    public static func recordOutcome(_ span: IVoiceLoopSpan?, success: Bool, errorReason: String? = nil) {
        guard let span else { return }
        span.setOutcome(success: success, errorReason: errorReason)
    }
}

// =====================================================================
// VoiceLoopAsTool.cs
// =====================================================================

/// Request to make one outbound voice call as a tool invocation. Port of the C#
/// record `CircleAI.Telephony.VoiceLoopToolRequest`.
public struct VoiceLoopToolRequest: Sendable, Equatable {
    /// E.164 destination number.
    public let toNumber: String
    /// Plain-English goal ("Book a haircut for Sipho on Saturday").
    public let goal: String
    /// Extra structured context the agent needs.
    public let contextJson: String?
    /// Persona / script for the voice agent.
    public let systemPrompt: String?
    /// Hard ceiling on call length.
    public let maxDuration: TimeInterval?

    public init(
        toNumber: String,
        goal: String,
        contextJson: String? = nil,
        systemPrompt: String? = nil,
        maxDuration: TimeInterval? = nil
    ) {
        self.toNumber = toNumber
        self.goal = goal
        self.contextJson = contextJson
        self.systemPrompt = systemPrompt
        self.maxDuration = maxDuration
    }
}

/// Result of the call returned to the calling agent. Port of the C# record
/// `CircleAI.Telephony.VoiceLoopToolResult`.
public struct VoiceLoopToolResult: Sendable, Equatable {
    /// True if the AI reports it completed the goal.
    public let goalAchieved: Bool
    /// Natural-language summary the AI wrote.
    public let summary: String
    /// Carrier call id.
    public let callId: String
    /// Actual call duration.
    public let duration: TimeInterval
    /// Full conversation transcript.
    public let transcript: String
    /// Optional JSON the AI extracted (e.g. appointment time).
    public let structuredOutputJson: String?

    public init(
        goalAchieved: Bool,
        summary: String,
        callId: String,
        duration: TimeInterval,
        transcript: String,
        structuredOutputJson: String?
    ) {
        self.goalAchieved = goalAchieved
        self.summary = summary
        self.callId = callId
        self.duration = duration
        self.transcript = transcript
        self.structuredOutputJson = structuredOutputJson
    }
}

/// Voice-loop-as-a-tool surface. Port of
/// `CircleAI.Telephony.IVoiceLoopTool`.
public protocol IVoiceLoopTool: Sendable {
    /// Make the call and report back.
    func invoke(_ request: VoiceLoopToolRequest) async throws -> VoiceLoopToolResult
}

/// Driver that delegates the actual call to a host-supplied runner. Port of
/// `CircleAI.Telephony.VoiceLoopAsTool`.
///
/// The C# `CancellationTokenSource(maxDuration)` becomes a race between the
/// runner and a `Task.sleep`; on timeout a synthetic "timed out" result is
/// returned (the runner closure itself observes cancellation cooperatively).
public final class VoiceLoopAsTool: IVoiceLoopTool, @unchecked Sendable {
    public typealias Runner = @Sendable (_ request: VoiceLoopToolRequest) async throws -> VoiceLoopToolResult

    private let runner: Runner
    private let defaultMaxDuration: TimeInterval

    public init(runner: @escaping Runner, defaultMaxDuration: TimeInterval? = nil) {
        self.runner = runner
        self.defaultMaxDuration = defaultMaxDuration ?? 300.0   // 5 minutes
    }

    public func invoke(_ request: VoiceLoopToolRequest) async throws -> VoiceLoopToolResult {
        if request.toNumber.isBlank {
            throw TelephonyError.argument("ToNumber is required.")
        }
        if request.goal.isBlank {
            throw TelephonyError.argument("Goal is required.")
        }

        let maxDuration = request.maxDuration ?? defaultMaxDuration

        // Race runner vs timeout. `.success(result)` from the runner wins; the
        // timeout branch yields `.timedOut`. Errors from the runner propagate.
        enum Outcome: Sendable {
            case success(VoiceLoopToolResult)
            case failure(Error)
            case timedOut
        }

        let outcome = await withTaskGroup(of: Outcome.self, returning: Outcome.self) { group in
            group.addTask {
                do { return .success(try await self.runner(request)) }
                catch { return .failure(error) }
            }
            group.addTask {
                try? await Task.sleep(nanoseconds: UInt64((maxDuration * 1_000_000_000).rounded()))
                return .timedOut
            }
            let first = await group.next() ?? .timedOut
            group.cancelAll()
            return first
        }

        switch outcome {
        case .success(let result):
            return result
        case .failure(let error):
            throw error
        case .timedOut:
            return VoiceLoopToolResult(
                goalAchieved: false,
                summary: "Call timed out after \(Self.minutes(maxDuration)) minutes.",
                callId: "",
                duration: maxDuration,
                transcript: "",
                structuredOutputJson: nil)
        }
    }

    /// Tool descriptor for use with `IToolCallRegistry`. Port of the C# static
    /// `VoiceLoopAsTool.Descriptor` (`ToolDefinition` → `TelephonyToolDefinition`).
    public static let descriptor = TelephonyToolDefinition(
        name: "make_voice_call",
        description: "Place an outbound phone call and follow the supplied goal/script. Returns whether the goal was achieved.",
        argumentsJsonSchema: """
        {
          "type": "object",
          "properties": {
            "to_number":     { "type": "string", "description": "E.164 destination." },
            "goal":          { "type": "string" },
            "context_json":  { "type": "string", "nullable": true },
            "system_prompt": { "type": "string", "nullable": true },
            "max_duration_seconds": { "type": "integer", "nullable": true }
          },
          "required": ["to_number", "goal"]
        }
        """)

    /// Mirror of C#'s `$"{maxDuration.TotalMinutes:F1}"` — one decimal place.
    private static func minutes(_ seconds: TimeInterval) -> String {
        String(format: "%.1f", seconds / 60.0)
    }
}
