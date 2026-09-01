// TelephonyCallEconomics.swift
//
// Four things a live call needs that are not the conversation: whether a
// machine picked up, what the call is costing, how a local dev box becomes
// internet-reachable, and where its tools come from.
//
// Ported from src/CircleAI.Telephony/{AnsweringMachineDetector, CallCostCalculator,
// LocalDevTunnel, McpToolImporter}.cs.

import Foundation

// MARK: - Answering-machine detection

public enum AmdVerdict: Int, Sendable, Equatable, Codable, CaseIterable {
    case unknown = 0
    case human
    case answeringMachine
}

/// The thresholds the whole judgement rests on.
///
/// Every value is optional with a documented default rather than a bare
/// constant, because these want tuning per market — a South African voicemail
/// greeting is not the same length as an American one, and a caller that has
/// measured its own numbers must be able to say so without a rebuild.
public struct AmdOptions: Sendable, Equatable {
    public var humanMaxFirstUtteranceMs: Int?
    public var humanMinFirstUtteranceMs: Int?
    public var maxObservationWindowMs: Int?
    public var silenceFrameThresholdMs: Int?

    public init(humanMaxFirstUtteranceMs: Int? = nil,
                humanMinFirstUtteranceMs: Int? = nil,
                maxObservationWindowMs: Int? = nil,
                silenceFrameThresholdMs: Int? = nil) {
        self.humanMaxFirstUtteranceMs = humanMaxFirstUtteranceMs
        self.humanMinFirstUtteranceMs = humanMinFirstUtteranceMs
        self.maxObservationWindowMs = maxObservationWindowMs
        self.silenceFrameThresholdMs = silenceFrameThresholdMs
    }

    /// A greeting longer than this is a recording. "Hello?" is under two
    /// seconds; "Hi, you've reached Thabo, I can't take your call…" is not.
    public var humanMaxFirstUtterance: Int { humanMaxFirstUtteranceMs ?? 1800 }

    /// Shorter than this is a cough, a click or line noise, not a greeting.
    public var humanMinFirstUtterance: Int { humanMinFirstUtteranceMs ?? 300 }

    /// Stop guessing after this. An answer that never arrives is worse than an
    /// uncertain one, because the caller is holding a live call open.
    public var maxObservationWindow: Int { maxObservationWindowMs ?? 3500 }

    /// Quiet for this long ends the utterance. Too short and a breath between
    /// words looks like the end of "hello".
    public var silenceFrameThreshold: Int { silenceFrameThresholdMs ?? 250 }
}

/// Human or machine, from the length of the first contiguous speech burst.
///
/// Cheaper than carrier-side AMD and it runs on the audio frames the call
/// already has, so it costs nothing extra per call.
///
/// ONCE IT DECIDES IT STOPS. A verdict that could flip mid-call is worse than
/// no verdict: the orchestrator has already started leaving a message or
/// started talking, and reversing that halfway is the one behaviour a person on
/// the other end cannot make sense of.
public final class AnsweringMachineDetector: @unchecked Sendable {

    private let options: AmdOptions
    private let lock = NSLock()

    private var firstUtteranceMs: Double = 0
    private var accumulatedMs: Double = 0
    private var utteranceInProgress = false
    private var trailingSilenceMs: Double = 0
    private var verdict: AmdVerdict = .unknown

    /// RMS below this is not speech. Normalised against full scale so it does
    /// not change with sample width.
    static let energyThreshold = 0.012

    public init(options: AmdOptions = AmdOptions()) {
        self.options = options
    }

    public var currentVerdict: AmdVerdict {
        lock.lock(); defer { lock.unlock() }
        return verdict
    }

    /// Feed one PCM16 frame. Returns the verdict so far.
    @discardableResult
    public func observe(pcmFrame: [UInt8], sampleRateHz: Int) -> AmdVerdict {
        guard sampleRateHz > 0 else { return currentVerdict }
        guard pcmFrame.count >= 2 else { return currentVerdict }

        let frameMs = 1000.0 * Double(pcmFrame.count / 2) / Double(sampleRateHz)
        let isSpeech = Self.frameHasSpeech(pcmFrame)

        lock.lock(); defer { lock.unlock() }
        if verdict != .unknown { return verdict }

        accumulatedMs += frameMs

        if isSpeech {
            utteranceInProgress = true
            firstUtteranceMs += frameMs
            trailingSilenceMs = 0
        } else if utteranceInProgress {
            trailingSilenceMs += frameMs
            if trailingSilenceMs >= Double(options.silenceFrameThreshold) {
                utteranceInProgress = false
            }
        }

        if firstUtteranceMs >= Double(options.humanMaxFirstUtterance) {
            // Still talking past the ceiling — it is a recording, and we can say
            // so without waiting for it to stop.
            verdict = .answeringMachine
        } else if !utteranceInProgress
                    && firstUtteranceMs >= Double(options.humanMinFirstUtterance)
                    && firstUtteranceMs < Double(options.humanMaxFirstUtterance) {
            verdict = .human
        } else if accumulatedMs >= Double(options.maxObservationWindow) {
            // Out of time. Near-silence stays UNKNOWN rather than being called a
            // machine: nobody spoke, and guessing here would leave a message on
            // a line a person is holding.
            verdict = firstUtteranceMs < Double(options.humanMinFirstUtterance)
                ? .unknown
                : .answeringMachine
        }

        return verdict
    }

    public func reset() {
        lock.lock()
        firstUtteranceMs = 0
        accumulatedMs = 0
        utteranceInProgress = false
        trailingSilenceMs = 0
        verdict = .unknown
        lock.unlock()
    }

    static func frameHasSpeech(_ pcm: [UInt8]) -> Bool {
        let sampleCount = pcm.count / 2
        guard sampleCount > 0 else { return false }

        var sumSquares = 0.0
        for i in 0..<sampleCount {
            // Little-endian PCM16, read as SIGNED. Read unsigned, every negative
            // sample becomes a large positive one and silence reads as speech.
            let raw = UInt16(pcm[i * 2]) | UInt16(pcm[i * 2 + 1]) << 8
            let s = Double(Int16(bitPattern: raw))
            sumSquares += s * s
        }
        let rms = (sumSquares / Double(sampleCount)).squareRoot() / Double(Int16.max)
        return rms >= energyThreshold
    }
}

// MARK: - What a call costs

/// The five prices a call is charged at.
///
/// Decimal in C#; Swift has no decimal, so these are `Double`. Money is
/// therefore rounded at the point it is PRESENTED, never accumulated as a
/// rounded value — a fraction of a cent per second across a ten-minute call is
/// a real amount.
public struct CallPricing: Sendable, Equatable {
    public let carrierPerMinute: Double
    public let sttPerSecond: Double
    public let ttsPerThousandChars: Double
    public let llmInputPerKToken: Double
    public let llmOutputPerKToken: Double

    public init(carrierPerMinute: Double, sttPerSecond: Double,
                ttsPerThousandChars: Double, llmInputPerKToken: Double,
                llmOutputPerKToken: Double) {
        self.carrierPerMinute = carrierPerMinute
        self.sttPerSecond = sttPerSecond
        self.ttsPerThousandChars = ttsPerThousandChars
        self.llmInputPerKToken = llmInputPerKToken
        self.llmOutputPerKToken = llmOutputPerKToken
    }

    public static let free = CallPricing(carrierPerMinute: 0, sttPerSecond: 0,
                                         ttsPerThousandChars: 0, llmInputPerKToken: 0,
                                         llmOutputPerKToken: 0)
}

/// Where the money went, per axis.
///
/// Broken out rather than reported as one number because the four axes have
/// completely different fixes: carrier minutes mean shorter calls, TTS
/// characters mean shorter replies, and LLM tokens mean a smaller model.
public struct CallCostBreakdown: Sendable, Equatable {
    public let carrier: Double
    public let stt: Double
    public let tts: Double
    public let llmInput: Double
    public let llmOutput: Double
    public let total: Double

    public init(carrier: Double, stt: Double, tts: Double,
                llmInput: Double, llmOutput: Double, total: Double) {
        self.carrier = carrier
        self.stt = stt
        self.tts = tts
        self.llmInput = llmInput
        self.llmOutput = llmOutput
        self.total = total
    }
}

/// A running cost figure the orchestrator can compare against a ceiling.
public final class CallCostCalculator: @unchecked Sendable {

    private let pricing: CallPricing
    private let lock = NSLock()

    private var carrierMs: Int64 = 0
    private var sttMs: Int64 = 0
    private var ttsChars: Int64 = 0
    private var llmInputTokens: Int64 = 0
    private var llmOutputTokens: Int64 = 0

    public init(pricing: CallPricing) {
        self.pricing = pricing
    }

    /// Negative durations are IGNORED rather than subtracted. A clock that goes
    /// backwards (and on a phone one does) must not hand somebody a refund.
    public func addCarrierTime(_ duration: TimeInterval) {
        guard duration > 0 else { return }
        lock.lock(); carrierMs += Int64(duration * 1000); lock.unlock()
    }

    public func addSttTime(_ duration: TimeInterval) {
        guard duration > 0 else { return }
        lock.lock(); sttMs += Int64(duration * 1000); lock.unlock()
    }

    public func addTtsCharacters(_ chars: Int) {
        guard chars > 0 else { return }
        lock.lock(); ttsChars += Int64(chars); lock.unlock()
    }

    public func addLlmTokens(input: Int, output: Int) {
        lock.lock()
        if input > 0 { llmInputTokens += Int64(input) }
        if output > 0 { llmOutputTokens += Int64(output) }
        lock.unlock()
    }

    public func currentBreakdown() -> CallCostBreakdown {
        lock.lock()
        let cMs = carrierMs, sMs = sttMs, tc = ttsChars
        let inTok = llmInputTokens, outTok = llmOutputTokens
        lock.unlock()

        let carrier = Double(cMs) / 60_000 * pricing.carrierPerMinute
        let stt = Double(sMs) / 1000 * pricing.sttPerSecond
        let tts = Double(tc) / 1000 * pricing.ttsPerThousandChars
        let llmIn = Double(inTok) / 1000 * pricing.llmInputPerKToken
        let llmOut = Double(outTok) / 1000 * pricing.llmOutputPerKToken

        return CallCostBreakdown(carrier: carrier, stt: stt, tts: tts,
                                 llmInput: llmIn, llmOutput: llmOut,
                                 total: carrier + stt + tts + llmIn + llmOut)
    }

    public func reset() {
        lock.lock()
        carrierMs = 0; sttMs = 0; ttsChars = 0
        llmInputTokens = 0; llmOutputTokens = 0
        lock.unlock()
    }
}

// MARK: - Reaching a local dev box

/// A voice loop needs an internet-reachable webhook URL even when it is running
/// on somebody's laptop. Same interface, three backings.
public protocol ILocalDevTunnel: Sendable {
    var providerId: String { get }
    var isAvailable: Bool { get }
    func publicUrl(localPort: Int) async throws -> URL
}

public enum LocalDevTunnelError: Error, CustomStringConvertible, Equatable {
    case notConfigured
    case notAbsolute(String)

    public var description: String {
        switch self {
        case .notConfigured:
            return "No local-dev tunnel is configured. Register a Cloudflare, ngrok or static tunnel."
        case .notAbsolute(let s):
            return "A tunnel URL must be absolute, and '\(s)' is not — a carrier posts to it from outside."
        }
    }
}

/// Reports UNAVAILABLE and throws when asked, rather than handing back
/// localhost. A carrier posting a webhook to localhost reaches itself, and the
/// call simply never gets a reply.
public struct NullLocalDevTunnel: ILocalDevTunnel, Sendable {
    public static let instance = NullLocalDevTunnel()
    public init() {}

    public var providerId: String { "null" }
    public var isAvailable: Bool { false }

    public func publicUrl(localPort: Int) async throws -> URL {
        throw LocalDevTunnelError.notConfigured
    }
}

/// A URL somebody pinned by hand.
public struct StaticLocalDevTunnel: ILocalDevTunnel, Sendable {
    private let url: URL

    public init(publicUrl: URL) throws {
        // Checked at CONSTRUCTION, not at first use: a relative URL discovered
        // when the first call arrives is discovered during a live call.
        guard publicUrl.scheme != nil, publicUrl.host != nil else {
            throw LocalDevTunnelError.notAbsolute(publicUrl.absoluteString)
        }
        self.url = publicUrl
    }

    public var providerId: String { "static" }
    public var isAvailable: Bool { true }
    public func publicUrl(localPort: Int) async throws -> URL { url }
}

/// Cloudflare Tunnel, resolved by a closure the host supplies.
///
/// A closure rather than a client: talking to cloudflared is a dev-box concern,
/// and wiring an HTTP client for it into a library that runs on phones would
/// ship dev machinery to every user.
public struct CloudflareTunnel: ILocalDevTunnel, Sendable {
    private let resolver: @Sendable (Int) async throws -> URL

    public init(resolver: @escaping @Sendable (Int) async throws -> URL) {
        self.resolver = resolver
    }

    public var providerId: String { "cloudflare" }
    public var isAvailable: Bool { true }
    public func publicUrl(localPort: Int) async throws -> URL { try await resolver(localPort) }
}

public struct NgrokTunnel: ILocalDevTunnel, Sendable {
    private let resolver: @Sendable (Int) async throws -> URL

    public init(resolver: @escaping @Sendable (Int) async throws -> URL) {
        self.resolver = resolver
    }

    public var providerId: String { "ngrok" }
    public var isAvailable: Bool { true }
    public func publicUrl(localPort: Int) async throws -> URL { try await resolver(localPort) }
}

// MARK: - Importing tools from an MCP server

public struct McpToolDescriptor: Sendable, Equatable, Codable {
    public let name: String
    public let description: String
    public let inputJsonSchema: String

    public init(name: String, description: String, inputJsonSchema: String) {
        self.name = name
        self.description = description
        self.inputJsonSchema = inputJsonSchema
    }
}

public struct McpServerConfig: Sendable, Equatable {
    public let serverEndpoint: URL
    public let authorizationHeader: String?
    /// Prefixed so two MCP servers offering a "search" tool do not collide, and
    /// so a person reading a transcript can see which server answered.
    public let toolNamePrefix: String?

    public init(serverEndpoint: URL, authorizationHeader: String? = nil,
                toolNamePrefix: String? = nil) {
        self.serverEndpoint = serverEndpoint
        self.authorizationHeader = authorizationHeader
        self.toolNamePrefix = toolNamePrefix
    }
}

public protocol IMcpToolImporter: Sendable {
    func `import`(into registry: any IToolCallRegistry,
                  from server: McpServerConfig) async throws -> [TelephonyToolDefinition]
}

/// Pulls tool definitions from an MCP server at call start and registers each
/// one as a webhook that forwards back to that server.
public struct HttpMcpToolImporter: IMcpToolImporter, Sendable {

    /// The transport, as a closure. The library does not own an HTTP client:
    /// a host has one configured with its own timeouts, proxy and pinning, and
    /// a second one hidden in here would quietly bypass all of it.
    private let send: @Sendable (URLRequest) async throws -> (Data, Int)
    private let log: (@Sendable (String) -> Void)?

    public init(send: @escaping @Sendable (URLRequest) async throws -> (Data, Int),
                log: (@Sendable (String) -> Void)? = nil) {
        self.send = send
        self.log = log
    }

    public func `import`(into registry: any IToolCallRegistry,
                         from server: McpServerConfig) async throws -> [TelephonyToolDefinition] {

        var request = URLRequest(url: server.serverEndpoint)
        request.httpMethod = "POST"
        request.setValue("application/json", forHTTPHeaderField: "Content-Type")
        if let auth = server.authorizationHeader,
           !auth.trimmingCharacters(in: .whitespaces).isEmpty {
            request.setValue(auth, forHTTPHeaderField: "Authorization")
        }
        request.httpBody = Data("""
            {"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}
            """.utf8)

        let (data, status) = try await send(request)
        guard (200..<300).contains(status) else {
            // A tool server that is down must not take the CALL down. The call
            // proceeds with whatever tools it already had.
            log?("MCP server \(server.serverEndpoint) returned \(status)")
            return []
        }

        return Self.parse(data, prefix: server.toolNamePrefix)
            .compactMap { descriptor in
                let definition = TelephonyToolDefinition(
                    name: descriptor.name,
                    description: descriptor.description,
                    argumentsJsonSchema: descriptor.inputJsonSchema)
                let url = Self.appendQuery(server.serverEndpoint,
                                           key: "remote_tool",
                                           value: descriptor.originalName)
                do {
                    try registry.registerWebhook(definition, webhook: url)
                    return definition
                } catch {
                    // One bad tool (a duplicate name, usually) does not lose the
                    // rest of the server's catalogue.
                    log?("could not register \(descriptor.name): \(error)")
                    return nil
                }
            }
    }

    struct Imported {
        let name: String
        let originalName: String
        let description: String
        let inputJsonSchema: String
    }

    /// Reads `result.tools[]`. A tool with no name is SKIPPED rather than
    /// registered under an empty string, which would shadow the next one.
    static func parse(_ data: Data, prefix: String?) -> [Imported] {
        guard let root = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let result = root["result"] as? [String: Any],
              let tools = result["tools"] as? [[String: Any]]
        else { return [] }

        return tools.compactMap { entry in
            guard let name = entry["name"] as? String,
                  !name.trimmingCharacters(in: .whitespaces).isEmpty
            else { return nil }

            let description = entry["description"] as? String ?? ""
            var schema = "{}"
            if let raw = entry["inputSchema"],
               let bytes = try? JSONSerialization.data(withJSONObject: raw) {
                schema = String(decoding: bytes, as: UTF8.self)
            }

            let local = (prefix?.isEmpty == false) ? prefix! + name : name
            return Imported(name: local, originalName: name,
                            description: description, inputJsonSchema: schema)
        }
    }

    static func appendQuery(_ base: URL, key: String, value: String) -> URL {
        var components = URLComponents(url: base, resolvingAgainstBaseURL: false)
        var items = components?.queryItems ?? []
        items.append(URLQueryItem(name: key, value: value))
        components?.queryItems = items
        return components?.url ?? base
    }
}
