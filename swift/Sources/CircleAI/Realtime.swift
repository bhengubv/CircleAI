// Realtime.swift
//
// Port of the CircleAI.Realtime module:
//   • Contracts.cs             — RealtimeAudioFormat, RealtimeDirection,
//                                RealtimeSessionConfig, RealtimeTool,
//                                RealtimeAudioFrame, RealtimeEvent (+ subtypes),
//                                IRealtimeSession, IRealtimeService.
//   • LoopbackRealtimeService.cs — the built-in in-process IRealtimeService that
//                                loops audio in→out, emits speech-started/ended
//                                from silence detection, and answers SendText
//                                with a TTS-shaped PCM stream.
//   • NullImplementations.cs    — NullRealtimeService (throws on start),
//                                NullRealtimeSession (yields nothing).
//
// Carrier-agnostic contracts for streaming realtime AI services. Five vendors
// (OpenAI Realtime, Gemini Live, AWS Nova Sonic, ElevenLabs Conversational,
// Ultravox) implement these in Realtime.Cloud.swift.
//
// C# `IAsyncEnumerable<T>` streams map to Swift `AsyncStream<T>`; the two
// producer channels (audio + events) are modelled on the unbounded, pre-
// subscription-buffering channel idiom already used by NetworkingWebSocket.swift
// (a delta written before `receive*()` is iterated is retained, not lost).
// `IAsyncDisposable` maps to `func dispose() async`.

import Foundation

// =====================================================================
// Contracts.cs — enums
// =====================================================================

/// Audio format used in realtime sessions. Port of
/// `CircleAI.Realtime.RealtimeAudioFormat`. Ordinals match the C# declaration
/// order: Pcm16k = 0, Pcm24k = 1, Mulaw8k = 2.
public enum RealtimeAudioFormat: Int, Sendable, Codable, CaseIterable {
    /// 16-bit linear PCM, mono, 16 kHz.
    case pcm16k = 0
    /// 16-bit linear PCM, mono, 24 kHz.
    case pcm24k = 1
    /// G.711 μ-law, mono, 8 kHz (carrier-native).
    case mulaw8k = 2
}

/// Direction of audio in a realtime session. Port of
/// `CircleAI.Realtime.RealtimeDirection`. Ordinals: Inbound = 0, Outbound = 1.
public enum RealtimeDirection: Int, Sendable, Codable, CaseIterable {
    case inbound = 0
    case outbound = 1
}

// =====================================================================
// Contracts.cs — DTOs
// =====================================================================

/// One tool the model can call. Port of the C# record `CircleAI.Realtime.RealtimeTool`.
public struct RealtimeTool: Sendable, Equatable, Codable {
    /// Tool name as the model sees it.
    public let name: String
    /// Human description of when to call this.
    public let description: String
    /// JSON schema for the tool's input arguments.
    public let jsonSchema: String

    public init(name: String, description: String, jsonSchema: String) {
        self.name = name
        self.description = description
        self.jsonSchema = jsonSchema
    }
}

/// Configuration for opening a realtime session. Port of the C# record
/// `CircleAI.Realtime.RealtimeSessionConfig`.
public struct RealtimeSessionConfig: Sendable, Equatable, Codable {
    /// Vendor-specific model id (e.g. `gpt-4o-realtime-preview-2024-12-17`).
    public let model: String
    /// Vendor voice id (e.g. `alloy` for OpenAI, `Aoede` for Gemini).
    public let voiceId: String?
    /// Persona / instructions that shape the assistant's responses.
    public let systemPrompt: String?
    /// Wire audio format. The host must transcode to/from this if the carrier differs.
    public let audioFormat: RealtimeAudioFormat
    /// ISO language hint (e.g. `en-US`); nil = auto-detect.
    public let languageHint: String?
    /// Optional list of tool definitions exposed to the model.
    public let tools: [RealtimeTool]?

    public init(
        model: String,
        voiceId: String? = nil,
        systemPrompt: String? = nil,
        audioFormat: RealtimeAudioFormat = .pcm24k,
        languageHint: String? = nil,
        tools: [RealtimeTool]? = nil
    ) {
        self.model = model
        self.voiceId = voiceId
        self.systemPrompt = systemPrompt
        self.audioFormat = audioFormat
        self.languageHint = languageHint
        self.tools = tools
    }
}

/// One audio frame in a realtime session. Port of the C# record
/// `CircleAI.Realtime.RealtimeAudioFrame`. `ReadOnlyMemory<byte>` maps to `Data`;
/// `TimeSpan Offset` maps to `TimeInterval` (seconds).
public struct RealtimeAudioFrame: Sendable, Equatable {
    /// PCM (or μ-law) audio bytes in the frame's `format`.
    public let pcm: Data
    /// The wire format of `pcm`.
    public let format: RealtimeAudioFormat
    /// Offset of this frame relative to the stream start.
    public let offset: TimeInterval

    public init(pcm: Data, format: RealtimeAudioFormat, offset: TimeInterval) {
        self.pcm = pcm
        self.format = format
        self.offset = offset
    }
}

// =====================================================================
// Contracts.cs — RealtimeEvent (discriminated union)
// =====================================================================

/// Discriminated union of events emitted by the vendor session. Port of the C#
/// `abstract record RealtimeEvent(DateTimeOffset At)` and its sealed subtypes.
///
/// In C# each event is a distinct record type; Swift models the closed set as an
/// enum with associated values. Every case carries `at` (the shared
/// `DateTimeOffset At`). The `at` and payloads are exposed via computed helpers
/// so call sites read the same fields the C# records expose.
public enum RealtimeEvent: Sendable, Equatable {
    /// Caller speech started. (`SpeechStartedEvent`)
    case speechStarted(at: Date)
    /// Caller speech ended — model is now processing. (`SpeechEndedEvent`)
    case speechEnded(at: Date)
    /// Partial transcript. (`TranscriptDeltaEvent`)
    case transcriptDelta(at: Date, delta: String, direction: RealtimeDirection)
    /// Final transcript of an utterance. (`TranscriptFinalEvent`)
    case transcriptFinal(at: Date, text: String, direction: RealtimeDirection)
    /// The model wants to call a tool. (`ToolCallEvent`)
    case toolCall(at: Date, callId: String, toolName: String, argumentsJson: String)
    /// The assistant turn is complete. (`TurnCompleteEvent`)
    case turnComplete(at: Date)
    /// Vendor reported an error mid-session. (`SessionErrorEvent`)
    case sessionError(at: Date, message: String)

    /// The timestamp shared by every event (the C# `RealtimeEvent.At`).
    public var at: Date {
        switch self {
        case let .speechStarted(at): return at
        case let .speechEnded(at): return at
        case let .transcriptDelta(at, _, _): return at
        case let .transcriptFinal(at, _, _): return at
        case let .toolCall(at, _, _, _): return at
        case let .turnComplete(at): return at
        case let .sessionError(at, _): return at
        }
    }
}

// =====================================================================
// Contracts.cs — IRealtimeSession / IRealtimeService
// =====================================================================

/// One open conversation with a realtime vendor. Audio flows in both directions
/// concurrently; control + transcripts surface as `RealtimeEvent`s. Port of
/// `CircleAI.Realtime.IRealtimeSession` (`IAsyncDisposable` → `dispose() async`).
public protocol IRealtimeSession: AnyObject, Sendable {
    /// Session identifier from the vendor.
    var sessionId: String { get }

    /// Inbound audio (from caller → us).
    func receiveAudio() -> AsyncStream<RealtimeAudioFrame>

    /// Send one audio frame to the model.
    func sendAudio(_ frame: RealtimeAudioFrame) async throws

    /// Send a text turn to the model (no audio, e.g. for a TTS-only turn).
    func sendText(_ text: String) async throws

    /// Reply to a tool call with its result.
    func sendToolResult(callId: String, resultJson: String) async throws

    /// Cancel the current model response (e.g. on barge-in).
    func cancelResponse() async throws

    /// Control + transcript events from the vendor.
    func receiveEvents() -> AsyncStream<RealtimeEvent>

    /// Tear down the session (mirrors `IAsyncDisposable.DisposeAsync`).
    func dispose() async
}

/// Vendor connector — opens realtime sessions. Port of
/// `CircleAI.Realtime.IRealtimeService`.
public protocol IRealtimeService: AnyObject, Sendable {
    /// Vendor self-id (e.g. `openai-realtime`).
    var providerId: String { get }

    /// True when credentials are present.
    var isConfigured: Bool { get }

    /// Open one realtime session per the supplied config.
    func startSession(_ config: RealtimeSessionConfig) async throws -> IRealtimeSession
}

/// Errors raised by realtime services / sessions. Mirrors the C#
/// `InvalidOperationException` / `ArgumentException` guards.
public enum RealtimeError: Error, Equatable {
    /// No vendor is registered (C# `NullRealtimeService.StartSessionAsync`).
    case noVendorRegistered(String)
    /// The vendor is not configured (missing API key / credentials).
    case notConfigured(String)
    /// A required argument was empty (e.g. `callId`).
    case argumentRequired(String)
    /// The vendor API returned an unusable response (e.g. Ultravox no joinUrl).
    case badVendorResponse(String)
}

// =====================================================================
// LoopbackRealtimeService.cs
// =====================================================================

/// Synthesise outbound audio for text. Default produces real silence frames
/// matching the text's expected speech duration (~80 ms per word). Hosts with a
/// real TTS engine plug it in via `LoopbackRealtimeService`'s initialiser. Port
/// of the C# `LoopbackTextToAudio` delegate.
public typealias LoopbackTextToAudio =
    @Sendable (_ text: String, _ format: RealtimeAudioFormat) async throws -> Data

/// Built-in, in-process `IRealtimeService`. Makes `CircleAI.Realtime` usable
/// end-to-end out of the box for tests + dev. Port of
/// `CircleAI.Realtime.LoopbackRealtimeService`.
public final class LoopbackRealtimeService: IRealtimeService, @unchecked Sendable {
    private let textToAudio: LoopbackTextToAudio

    /// Default: silence sized to the text's expected speech duration.
    public convenience init() {
        self.init(textToAudio: LoopbackRealtimeService.silenceTextToAudio)
    }

    public init(textToAudio: @escaping LoopbackTextToAudio) {
        self.textToAudio = textToAudio
    }

    public var providerId: String { "loopback" }
    public var isConfigured: Bool { true }

    public func startSession(_ config: RealtimeSessionConfig) async throws -> IRealtimeSession {
        LoopbackRealtimeSession(config: config, textToAudio: textToAudio)
    }

    /// Default `LoopbackTextToAudio`: emit real silence frames sized to ~80 ms
    /// per word (min 50 ms). Real audio bytes (zero amplitude) so downstream
    /// signal-processing / duration accounting works. Port of the C#
    /// `SilenceTextToAudio`.
    public static let silenceTextToAudio: LoopbackTextToAudio = { text, format in
        let sr = LoopbackRealtimeSession.sampleRateOf(format)
        let trimmed = text.trimmingCharacters(in: .whitespacesAndNewlines)
        let wordCount = trimmed.isEmpty
            ? 0
            : text.split(whereSeparator: { $0 == " " || $0 == "\t" || $0 == "\n" }).count
        let durationMs = max(50, wordCount * 80)
        let sampleCount = sr * durationMs / 1000
        // 16-bit silence (already zeros).
        return Data(count: sampleCount * 2)
    }
}

/// The loopback session backing `LoopbackRealtimeService`. Echoes received audio
/// back as outbound, emits speech-started/ended from an RMS silence detector,
/// and answers `sendText` with a TTS-shaped PCM stream. Port of
/// `CircleAI.Realtime.LoopbackRealtimeSession`.
public final class LoopbackRealtimeSession: IRealtimeSession, @unchecked Sendable {
    /// An unbounded, pre-subscription-buffering channel (mirrors C#'s
    /// `Channel.CreateUnbounded<T>()`): items written before `stream()` is first
    /// iterated are retained and replayed, and `complete()` finishes the reader.
    /// The continuation is finished *outside* the lock so a termination handler
    /// re-entering the lock cannot self-deadlock the non-reentrant `NSLock`.
    private final class Chan<T: Sendable>: @unchecked Sendable {
        private let lock = NSLock()
        private var completed = false
        private var pending: [T] = []
        private var continuation: AsyncStream<T>.Continuation?

        func write(_ value: T) {
            lock.lock()
            if completed { lock.unlock(); return }
            if let cont = continuation {
                cont.yield(value)
                lock.unlock()
            } else {
                pending.append(value)
                lock.unlock()
            }
        }

        func stream() -> AsyncStream<T> {
            AsyncStream(bufferingPolicy: .unbounded) { continuation in
                lock.lock()
                if completed {
                    lock.unlock()
                    continuation.finish()
                    return
                }
                for p in pending { continuation.yield(p) }
                pending.removeAll()
                self.continuation = continuation
                lock.unlock()

                continuation.onTermination = { [weak self] _ in
                    guard let self else { return }
                    self.lock.lock(); self.continuation = nil; self.lock.unlock()
                }
            }
        }

        func complete() {
            lock.lock()
            completed = true
            let cont = continuation
            continuation = nil
            pending.removeAll()
            lock.unlock()
            cont?.finish()   // finish outside the lock
        }
    }

    private let config: RealtimeSessionConfig
    private let textToAudio: LoopbackTextToAudio
    private let audio = Chan<RealtimeAudioFrame>()
    private let events = Chan<RealtimeEvent>()

    // Mutable session state (guarded by `stateLock`, confined to sync helpers).
    private let stateLock = NSLock()
    private var offset: TimeInterval = 0
    private var speaking = false

    public let sessionId: String

    public convenience init(config: RealtimeSessionConfig) {
        self.init(config: config, textToAudio: LoopbackRealtimeService.silenceTextToAudio)
    }

    public init(config: RealtimeSessionConfig, textToAudio: @escaping LoopbackTextToAudio) {
        self.config = config
        self.textToAudio = textToAudio
        // C# `$"loop-{Guid.NewGuid():N}"` — "N" = 32 hex digits, no dashes.
        self.sessionId = "loop-" + UUID().uuidString.replacingOccurrences(of: "-", with: "").lowercased()
    }

    public func receiveAudio() -> AsyncStream<RealtimeAudioFrame> {
        audio.stream()
    }

    public func sendAudio(_ frame: RealtimeAudioFrame) async throws {
        // ArgumentNullException.ThrowIfNull(frame) is implicit — value type.
        let nowSpeaking = !LoopbackRealtimeSession.isSilent(frame.pcm)
        if toggleSpeaking(to: nowSpeaking) {
            events.write(nowSpeaking
                ? .speechStarted(at: Date())
                : .speechEnded(at: Date()))
        }
        // Loopback: echo received audio back as outbound.
        audio.write(frame)
    }

    public func sendText(_ text: String) async throws {
        // C#: `if (text is null) throw` — Swift `String` is non-optional.
        events.write(.transcriptDelta(at: Date(), delta: text, direction: .outbound))
        let pcm = try await textToAudio(text, config.audioFormat)
        if !pcm.isEmpty {
            let frameOffset = currentOffset()
            audio.write(RealtimeAudioFrame(pcm: pcm, format: config.audioFormat, offset: frameOffset))
            // Advance by pcm.Length / 2 samples at the format's sample rate.
            let sr = Double(LoopbackRealtimeSession.sampleRateOf(config.audioFormat))
            let advanceSeconds = Double(pcm.count) / 2.0 / sr
            advanceOffset(by: advanceSeconds)
        }
        events.write(.transcriptFinal(at: Date(), text: text, direction: .outbound))
        events.write(.turnComplete(at: Date()))
    }

    public func sendToolResult(callId: String, resultJson: String) async throws {
        if callId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            throw RealtimeError.argumentRequired("callId required")
        }
        // C#: `if (resultJson is null) throw` — Swift `String` is non-optional.
        let summary = "[tool \(callId): \(LoopbackRealtimeSession.truncate(resultJson, max: 60))]"
        events.write(.transcriptDelta(at: Date(), delta: summary, direction: .outbound))
    }

    public func cancelResponse() async throws {
        events.write(.turnComplete(at: Date()))
    }

    public func receiveEvents() -> AsyncStream<RealtimeEvent> {
        events.stream()
    }

    public func dispose() async {
        audio.complete()
        events.complete()
    }

    // ── state sync helpers ──────────────────────────────────────────────────────

    /// Update the speaking flag; returns true if it changed (so the caller emits
    /// the corresponding speech-started/ended event). Mirrors the C#
    /// `if (nowSpeaking != _speaking) { ...; _speaking = nowSpeaking; }`.
    private func toggleSpeaking(to nowSpeaking: Bool) -> Bool {
        stateLock.lock(); defer { stateLock.unlock() }
        if nowSpeaking != speaking {
            speaking = nowSpeaking
            return true
        }
        return false
    }

    private func currentOffset() -> TimeInterval {
        stateLock.lock(); defer { stateLock.unlock() }
        return offset
    }

    private func advanceOffset(by seconds: TimeInterval) {
        stateLock.lock(); offset += seconds; stateLock.unlock()
    }

    // ── static helpers (ported verbatim) ────────────────────────────────────────

    /// Sample rate for a format. Port of the C# `SampleRateOf`.
    internal static func sampleRateOf(_ f: RealtimeAudioFormat) -> Int {
        switch f {
        case .pcm16k: return 16_000
        case .pcm24k: return 24_000
        case .mulaw8k: return 8_000
        }
    }

    /// RMS-based silence detector over 16-bit linear PCM. Port of the C#
    /// `IsSilent`. `< 64` bytes → silent; threshold 250.0 (~ -42 dBFS).
    internal static func isSilent(_ pcm: Data) -> Bool {
        if pcm.count < 64 { return true }
        var sumSq: Int64 = 0
        let sampleCount = pcm.count / 2
        // Iterate 16-bit little-endian samples exactly as the C# `(short)(lo | hi<<8)`.
        pcm.withUnsafeBytes { (raw: UnsafeRawBufferPointer) in
            var i = 0
            while i + 1 < raw.count {
                let lo = Int(raw[i])
                let hi = Int(raw[i + 1])
                let sample = Int16(truncatingIfNeeded: lo | (hi << 8))
                let s = Int64(sample)
                sumSq += s * s
                i += 2
            }
        }
        let rms = (Double(sumSq) / Double(sampleCount)).squareRoot()
        return rms < 250.0
    }

    /// Truncate with an ellipsis. Port of the C# `Truncate`.
    internal static func truncate(_ s: String, max: Int) -> String {
        s.count <= max ? s : String(s.prefix(max)) + "…"
    }
}

// =====================================================================
// NullImplementations.cs
// =====================================================================

/// Throws on `startSession`; reports `isConfigured == false`. Port of
/// `CircleAI.Realtime.NullRealtimeService`.
public final class NullRealtimeService: IRealtimeService, @unchecked Sendable {
    public static let instance = NullRealtimeService()
    public init() {}

    public var providerId: String { "null" }
    public var isConfigured: Bool { false }

    public func startSession(_ config: RealtimeSessionConfig) async throws -> IRealtimeSession {
        throw RealtimeError.noVendorRegistered(
            "No realtime vendor is registered. Add CircleAI.Realtime.Cloud connectors (OpenAI, Gemini, Nova, ElevenLabs, Ultravox).")
    }
}

/// A session that yields nothing — fully muted. Port of
/// `CircleAI.Realtime.NullRealtimeSession`.
public final class NullRealtimeSession: IRealtimeSession, @unchecked Sendable {
    public init() {}

    public var sessionId: String { "null" }

    public func receiveAudio() -> AsyncStream<RealtimeAudioFrame> {
        AsyncStream { $0.finish() }
    }

    public func sendAudio(_ frame: RealtimeAudioFrame) async throws {}
    public func sendText(_ text: String) async throws {}
    public func sendToolResult(callId: String, resultJson: String) async throws {}
    public func cancelResponse() async throws {}

    public func receiveEvents() -> AsyncStream<RealtimeEvent> {
        AsyncStream { $0.finish() }
    }

    public func dispose() async {}
}
