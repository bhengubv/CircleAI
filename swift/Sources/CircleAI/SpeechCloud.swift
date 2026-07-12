// SpeechCloud.swift
//
// Port of CircleAI.Speech.Cloud/ — 12 cloud ASR/TTS provider backends plus
// their Options records and the shared WAV/multipart/base64 plumbing.
//   Recognisers (ISpeechRecognizer):
//     • OpenAiSpeechRecognizer.cs     — Whisper /v1/audio/transcriptions
//     • DeepgramSpeechRecognizer.cs   — /v1/listen (raw linear16)
//     • AssemblyAiSpeechRecognizer.cs — upload → submit → poll
//     • GoogleSpeechRecognizer.cs     — /v1/speech:recognize (base64)
//     • AzureSpeechRecognizer.cs      — cognitiveservices/v1
//     • CartesiaSpeechRecognizer.cs   — /v1/transcribe (multipart)
//   Synthesisers (ISpeechSynthesizer):
//     • OpenAiSpeechSynthesizer.cs     — /v1/audio/speech (pcm)
//     • ElevenLabsSpeechSynthesizer.cs — /v1/text-to-speech
//     • CartesiaSpeechSynthesizer.cs   — /v1/tts/bytes
//     • DeepgramSpeechSynthesizer.cs   — /v1/speak (Aura)
//     • AzureSpeechSynthesizer.cs      — cognitiveservices/v1 (SSML)
//     • GoogleSpeechSynthesizer.cs     — /v1/text:synthesize (base64 WAV)
//     • PlayHtSpeechSynthesizer.cs     — /api/v2/tts/stream
//
// Porting notes:
//   • These conform to the ALREADY-PORTED contracts in SpeechContracts.swift:
//     `ISpeechRecognizer.transcribe(audioPcm16Mono:sampleRateHz:languageHint:)`
//     -> `SpeechTranscriptionResult`, and
//     `ISpeechSynthesizer.synthesize(text:voiceId:languageHint:)`
//     -> `SpeechSynthesisResult`. The C# `TranscribedSegment` /
//     `TranscriptionResult` / `SynthesisResult` map to the `Speech*`-prefixed
//     Swift DTOs there.
//   • The C# `HttpClient` becomes the injected `ISpeechCloudHttpTransport`
//     seam (same idiom as VisionCloud's `IImageHttpTransport`). It handles the
//     four request shapes these providers need: JSON body, raw-bytes body,
//     multipart/form-data, and a bare GET (AssemblyAI poll). Responses carry
//     status + body + content-type.
//   • Fail-soft everywhere: an unconfigured provider or a non-2xx response
//     returns an empty result (never throws), matching the C# `Empty()` path so
//     a fallback router can move on. The optional logger seam is the tree's
//     `ICircleAILogger`.
//   • `TimeSpan` → `TimeInterval` (seconds). Azure's 100-ns "ticks" convert as
//     ticks / 10_000_000. Durations are computed exactly as the C#.
//   • WAV wrapping / header-stripping is byte-identical to the C# little-endian
//     44-byte header writer.

import Foundation

// MARK: - HTTP seam

/// One HTTP response a Speech.Cloud transport hands back. (Same shape as the
/// tree's other cloud leaves.)
public struct SpeechCloudHttpResponse: Sendable, Equatable {
    public let statusCode: Int
    public let body: Data

    public init(statusCode: Int, body: Data) {
        self.statusCode = statusCode
        self.body = body
    }

    /// Mirrors `HttpResponseMessage.IsSuccessStatusCode` (2xx).
    public var isSuccess: Bool { (200..<300).contains(statusCode) }
}

/// One multipart/form-data part — either a text field or a file field.
public struct SpeechCloudFormPart: Sendable, Equatable {
    public let name: String
    /// For a file part: the filename; nil for a plain text field.
    public let fileName: String?
    /// For a file part: the content-type; nil for a plain text field.
    public let contentType: String?
    /// The field value bytes (UTF-8 for text fields).
    public let data: Data

    /// Text field.
    public static func text(_ name: String, _ value: String) -> SpeechCloudFormPart {
        SpeechCloudFormPart(name: name, fileName: nil, contentType: nil, data: Data(value.utf8))
    }

    /// File field.
    public static func file(_ name: String, fileName: String, contentType: String, data: Data) -> SpeechCloudFormPart {
        SpeechCloudFormPart(name: name, fileName: fileName, contentType: contentType, data: data)
    }

    public init(name: String, fileName: String?, contentType: String?, data: Data) {
        self.name = name
        self.fileName = fileName
        self.contentType = contentType
        self.data = data
    }
}

/// The single injected HTTP leaf the Speech.Cloud providers use. Keeps the SDK
/// free of a baked-in HTTP client / provider key, exactly as the other cloud
/// integrations do. Covers the four request shapes these backends need.
public protocol ISpeechCloudHttpTransport: Sendable {
    /// GET `baseAddress + path` with `headers`.
    func get(
        baseAddress: String,
        path: String,
        headers: [String: String]
    ) async throws -> SpeechCloudHttpResponse

    /// POST a JSON body (`contentType` typically "application/json").
    func postJson(
        baseAddress: String,
        path: String,
        headers: [String: String],
        contentType: String,
        jsonBody: Data
    ) async throws -> SpeechCloudHttpResponse

    /// POST a raw byte body (`contentType` e.g. "audio/raw" / octet-stream / SSML).
    func postBytes(
        baseAddress: String,
        path: String,
        headers: [String: String],
        contentType: String,
        body: Data
    ) async throws -> SpeechCloudHttpResponse

    /// POST a multipart/form-data body assembled from `parts`.
    func postMultipart(
        baseAddress: String,
        path: String,
        headers: [String: String],
        parts: [SpeechCloudFormPart]
    ) async throws -> SpeechCloudHttpResponse
}

// MARK: - Shared audio plumbing

/// PCM-16 mono WAV helpers, byte-identical to the C# little-endian writer.
enum SpeechCloudWav {
    /// Wrap raw PCM-16 mono bytes in a 44-byte WAV envelope.
    static func wrapPcmAsWav(_ pcm: Data, sampleRate: Int) -> Data {
        let channels = 1
        let bitsPerSample = 16
        let byteRate = sampleRate * channels * (bitsPerSample / 8)
        let blockAlign = channels * (bitsPerSample / 8)
        let dataSize = pcm.count
        let chunkSize = 36 + dataSize

        var buffer = Data(capacity: 44 + dataSize)
        func u32(_ v: Int) -> [UInt8] {
            let x = UInt32(truncatingIfNeeded: v)
            return [UInt8(x & 0xFF), UInt8((x >> 8) & 0xFF), UInt8((x >> 16) & 0xFF), UInt8((x >> 24) & 0xFF)]
        }
        func u16(_ v: Int) -> [UInt8] {
            let x = UInt16(truncatingIfNeeded: v)
            return [UInt8(x & 0xFF), UInt8((x >> 8) & 0xFF)]
        }

        buffer.append(contentsOf: Array("RIFF".utf8))
        buffer.append(contentsOf: u32(chunkSize))
        buffer.append(contentsOf: Array("WAVE".utf8))
        buffer.append(contentsOf: Array("fmt ".utf8))
        buffer.append(contentsOf: u32(16))              // Subchunk1Size
        buffer.append(contentsOf: u16(1))               // PCM = 1
        buffer.append(contentsOf: u16(channels))
        buffer.append(contentsOf: u32(sampleRate))
        buffer.append(contentsOf: u32(byteRate))
        buffer.append(contentsOf: u16(blockAlign))
        buffer.append(contentsOf: u16(bitsPerSample))
        buffer.append(contentsOf: Array("data".utf8))
        buffer.append(contentsOf: u32(dataSize))
        buffer.append(pcm)
        return buffer
    }

    /// Strip a leading 44-byte WAV header if present (Google returns WAV).
    static func stripWavHeader(_ data: Data) -> Data {
        let bytes = [UInt8](data)
        if bytes.count > 44,
           bytes[0] == UInt8(ascii: "R"), bytes[1] == UInt8(ascii: "I"),
           bytes[2] == UInt8(ascii: "F"), bytes[3] == UInt8(ascii: "F") {
            return data.subdata(in: 44..<data.count)
        }
        return data
    }
}

/// Percent-encode a URL path/query component (≈ `Uri.EscapeDataString`).
private func speechEscape(_ s: String) -> String {
    let allowed = CharacterSet(charactersIn:
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_.~")
    return s.addingPercentEncoding(withAllowedCharacters: allowed) ?? s
}

/// Empty transcription (fail-soft). (C# `Empty()`.)
private func emptyTranscription(_ language: String? = nil) -> SpeechTranscriptionResult {
    SpeechTranscriptionResult(text: "", language: language, segments: [], totalDuration: 0)
}

/// Empty synthesis (fail-soft). (C# `Empty()` — rate 0.)
private func emptySynthesis() -> SpeechSynthesisResult {
    SpeechSynthesisResult(audioPcm16Mono: Data(), sampleRateHz: 0, duration: 0)
}

// MARK: - JSON helpers

/// Parse response bytes to a JSON object. Returns nil when the body is not a
/// JSON object.
private func jsonObject(_ data: Data) -> [String: Any]? {
    (try? JSONSerialization.jsonObject(with: data, options: [.fragmentsAllowed])) as? [String: Any]
}

private extension Dictionary where Key == String, Value == Any {
    func string(_ key: String) -> String? { self[key] as? String }
    func double(_ key: String) -> Double? {
        if let d = self[key] as? Double { return d }
        if let n = self[key] as? NSNumber { return n.doubleValue }
        if let i = self[key] as? Int { return Double(i) }
        return nil
    }
    func object(_ key: String) -> [String: Any]? { self[key] as? [String: Any] }
    func array(_ key: String) -> [Any]? { self[key] as? [Any] }
}

// =====================================================================
// Options (Options.cs)
// =====================================================================

/// OpenAI Whisper + TTS options. (C# `OpenAiVoiceOptions`.)
public struct OpenAiVoiceOptions: Sendable {
    public var baseAddress: String
    public var apiKey: String?
    public var transcriptionModel: String
    public var speechModel: String
    public var defaultVoice: String
    public var pcmSampleRateHz: Int

    public init(baseAddress: String = "https://api.openai.com", apiKey: String? = nil,
                transcriptionModel: String = "whisper-1", speechModel: String = "tts-1",
                defaultVoice: String = "alloy", pcmSampleRateHz: Int = 24_000) {
        self.baseAddress = baseAddress
        self.apiKey = apiKey
        self.transcriptionModel = transcriptionModel
        self.speechModel = speechModel
        self.defaultVoice = defaultVoice
        self.pcmSampleRateHz = pcmSampleRateHz
    }
}

/// Deepgram STT options. (C# `DeepgramOptions`.)
public struct DeepgramOptions: Sendable {
    public var baseAddress: String
    public var apiKey: String?
    public var model: String

    public init(baseAddress: String = "https://api.deepgram.com", apiKey: String? = nil,
                model: String = "nova-2-general") {
        self.baseAddress = baseAddress
        self.apiKey = apiKey
        self.model = model
    }
}

/// AssemblyAI STT options. (C# `AssemblyAiOptions`.)
public struct AssemblyAiOptions: Sendable {
    public var baseAddress: String
    public var apiKey: String?
    public var speechModel: String

    public init(baseAddress: String = "https://api.assemblyai.com", apiKey: String? = nil,
                speechModel: String = "universal") {
        self.baseAddress = baseAddress
        self.apiKey = apiKey
        self.speechModel = speechModel
    }
}

/// Google Cloud STT options. (C# `GoogleSpeechOptions`.)
public struct GoogleSpeechOptions: Sendable {
    public var baseAddress: String
    public var apiKey: String?
    public var languageCode: String

    public init(baseAddress: String = "https://speech.googleapis.com", apiKey: String? = nil,
                languageCode: String = "en-US") {
        self.baseAddress = baseAddress
        self.apiKey = apiKey
        self.languageCode = languageCode
    }
}

/// Azure STT options. (C# `AzureSpeechOptions`.)
public struct AzureSpeechOptions: Sendable {
    public var baseAddress: String?
    public var apiKey: String?
    public var languageCode: String

    public init(baseAddress: String? = nil, apiKey: String? = nil, languageCode: String = "en-US") {
        self.baseAddress = baseAddress
        self.apiKey = apiKey
        self.languageCode = languageCode
    }
}

/// ElevenLabs TTS options. (C# `ElevenLabsOptions`.)
public struct ElevenLabsOptions: Sendable {
    public var baseAddress: String
    public var apiKey: String?
    public var defaultVoiceId: String
    public var model: String
    public var outputFormat: String
    public var pcmSampleRateHz: Int

    public init(baseAddress: String = "https://api.elevenlabs.io", apiKey: String? = nil,
                defaultVoiceId: String = "21m00Tcm4TlvDq8ikWAM", model: String = "eleven_flash_v2_5",
                outputFormat: String = "pcm_24000", pcmSampleRateHz: Int = 24_000) {
        self.baseAddress = baseAddress
        self.apiKey = apiKey
        self.defaultVoiceId = defaultVoiceId
        self.model = model
        self.outputFormat = outputFormat
        self.pcmSampleRateHz = pcmSampleRateHz
    }
}

/// Cartesia Sonic TTS options. (C# `CartesiaTtsOptions`.)
public struct CartesiaTtsOptions: Sendable {
    public var baseAddress: String
    public var apiKey: String?
    public var model: String
    public var defaultVoiceId: String
    public var outputContainer: String
    public var outputEncoding: String
    public var pcmSampleRateHz: Int
    public var cartesiaVersion: String

    public init(baseAddress: String = "https://api.cartesia.ai", apiKey: String? = nil,
                model: String = "sonic-2", defaultVoiceId: String = "a0e99841-438c-4a64-b679-ae501e7d6091",
                outputContainer: String = "raw", outputEncoding: String = "pcm_s16le",
                pcmSampleRateHz: Int = 24_000, cartesiaVersion: String = "2025-04-16") {
        self.baseAddress = baseAddress
        self.apiKey = apiKey
        self.model = model
        self.defaultVoiceId = defaultVoiceId
        self.outputContainer = outputContainer
        self.outputEncoding = outputEncoding
        self.pcmSampleRateHz = pcmSampleRateHz
        self.cartesiaVersion = cartesiaVersion
    }
}

/// Deepgram Aura TTS options. (C# `DeepgramTtsOptions`.)
public struct DeepgramTtsOptions: Sendable {
    public var baseAddress: String
    public var apiKey: String?
    public var voice: String
    public var pcmSampleRateHz: Int

    public init(baseAddress: String = "https://api.deepgram.com", apiKey: String? = nil,
                voice: String = "aura-asteria-en", pcmSampleRateHz: Int = 24_000) {
        self.baseAddress = baseAddress
        self.apiKey = apiKey
        self.voice = voice
        self.pcmSampleRateHz = pcmSampleRateHz
    }
}

/// Azure TTS options. (C# `AzureTtsOptions`.)
public struct AzureTtsOptions: Sendable {
    public var baseAddress: String?
    public var apiKey: String?
    public var languageCode: String
    public var defaultVoiceName: String
    public var pcmSampleRateHz: Int

    public init(baseAddress: String? = nil, apiKey: String? = nil, languageCode: String = "en-US",
                defaultVoiceName: String = "en-US-AvaMultilingualNeural", pcmSampleRateHz: Int = 24_000) {
        self.baseAddress = baseAddress
        self.apiKey = apiKey
        self.languageCode = languageCode
        self.defaultVoiceName = defaultVoiceName
        self.pcmSampleRateHz = pcmSampleRateHz
    }
}

/// Google Cloud TTS options. (C# `GoogleTtsOptions`.)
public struct GoogleTtsOptions: Sendable {
    public var baseAddress: String
    public var apiKey: String?
    public var languageCode: String
    public var defaultVoiceName: String
    public var pcmSampleRateHz: Int

    public init(baseAddress: String = "https://texttospeech.googleapis.com", apiKey: String? = nil,
                languageCode: String = "en-US", defaultVoiceName: String = "en-US-Studio-O",
                pcmSampleRateHz: Int = 24_000) {
        self.baseAddress = baseAddress
        self.apiKey = apiKey
        self.languageCode = languageCode
        self.defaultVoiceName = defaultVoiceName
        self.pcmSampleRateHz = pcmSampleRateHz
    }
}

/// PlayHT TTS options. (C# `PlayHtOptions`.)
public struct PlayHtOptions: Sendable {
    public var baseAddress: String
    public var apiKey: String?
    public var userId: String?
    public var defaultVoice: String
    public var model: String
    public var pcmSampleRateHz: Int

    public init(baseAddress: String = "https://api.play.ht", apiKey: String? = nil, userId: String? = nil,
                defaultVoice: String = "s3://voice-cloning-zero-shot/d9ff78ba-d016-47f6-b0ef-dd630f59414e/female-cs/manifest.json",
                model: String = "PlayDialog", pcmSampleRateHz: Int = 24_000) {
        self.baseAddress = baseAddress
        self.apiKey = apiKey
        self.userId = userId
        self.defaultVoice = defaultVoice
        self.model = model
        self.pcmSampleRateHz = pcmSampleRateHz
    }
}

/// Cartesia STT options. (C# `CartesiaSttOptions`.)
public struct CartesiaSttOptions: Sendable {
    public var baseAddress: String
    public var apiKey: String?
    public var model: String
    public var cartesiaVersion: String

    public init(baseAddress: String = "https://api.cartesia.ai", apiKey: String? = nil,
                model: String = "ink-whisper", cartesiaVersion: String = "2025-04-16") {
        self.baseAddress = baseAddress
        self.apiKey = apiKey
        self.model = model
        self.cartesiaVersion = cartesiaVersion
    }
}

private func isBlank(_ s: String?) -> Bool {
    (s ?? "").trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
}

// =====================================================================
// Recognisers
// =====================================================================

/// OpenAI Whisper-backed `ISpeechRecognizer`. (C# `OpenAiSpeechRecognizer`.)
public final class OpenAiSpeechRecognizer: ISpeechRecognizer, @unchecked Sendable {
    private let transport: any ISpeechCloudHttpTransport
    private let options: OpenAiVoiceOptions
    private let logger: (any ICircleAILogger)?

    public init(transport: any ISpeechCloudHttpTransport, options: OpenAiVoiceOptions, logger: (any ICircleAILogger)? = nil) {
        self.transport = transport
        self.options = options
        self.logger = logger
    }

    public var backendId: String { "openai-whisper" }
    public var isConfigured: Bool { !isBlank(options.apiKey) }

    public func transcribe(audioPcm16Mono: Data, sampleRateHz: Int, languageHint: String?) async throws -> SpeechTranscriptionResult {
        guard isConfigured else { return emptyTranscription() }

        let wav = SpeechCloudWav.wrapPcmAsWav(audioPcm16Mono, sampleRate: sampleRateHz)
        var parts: [SpeechCloudFormPart] = [
            .file("file", fileName: "audio.wav", contentType: "audio/wav", data: wav),
            .text("model", options.transcriptionModel),
            .text("response_format", "verbose_json"),
        ]
        if !isBlank(languageHint) { parts.append(.text("language", languageHint!)) }

        let resp = try await transport.postMultipart(
            baseAddress: options.baseAddress,
            path: "/v1/audio/transcriptions",
            headers: ["Authorization": "Bearer \(options.apiKey ?? "")"],
            parts: parts)

        guard resp.isSuccess else {
            logger?.logInformation("OpenAI transcription returned \(resp.statusCode)")
            return emptyTranscription()
        }
        guard let root = jsonObject(resp.body) else { return emptyTranscription() }

        let text = root.string("text") ?? ""
        let language = root.string("language")
        let duration = TimeInterval(root.double("duration") ?? 0)

        var segments: [SpeechTranscribedSegment] = []
        if let segs = root.array("segments") {
            for case let s as [String: Any] in segs {
                let segText = s.string("text") ?? ""
                let segStart = s.double("start") ?? 0
                let segEnd = s.double("end") ?? segStart
                segments.append(SpeechTranscribedSegment(
                    text: segText,
                    offset: TimeInterval(segStart),
                    duration: TimeInterval(max(0, segEnd - segStart)),
                    language: language,
                    confidence: 0))
            }
        }
        return SpeechTranscriptionResult(text: text, language: language, segments: segments, totalDuration: duration)
    }
}

/// Deepgram-backed `ISpeechRecognizer`. (C# `DeepgramSpeechRecognizer`.)
public final class DeepgramSpeechRecognizer: ISpeechRecognizer, @unchecked Sendable {
    private let transport: any ISpeechCloudHttpTransport
    private let options: DeepgramOptions
    private let logger: (any ICircleAILogger)?

    public init(transport: any ISpeechCloudHttpTransport, options: DeepgramOptions, logger: (any ICircleAILogger)? = nil) {
        self.transport = transport
        self.options = options
        self.logger = logger
    }

    public var backendId: String { "deepgram" }
    public var isConfigured: Bool { !isBlank(options.apiKey) }

    public func transcribe(audioPcm16Mono: Data, sampleRateHz: Int, languageHint: String?) async throws -> SpeechTranscriptionResult {
        guard isConfigured else { return emptyTranscription() }

        var path = "/v1/listen?model=\(speechEscape(options.model))&encoding=linear16&sample_rate=\(sampleRateHz)&channels=1&punctuate=true"
        if !isBlank(languageHint) { path += "&language=\(speechEscape(languageHint!))" }

        let resp = try await transport.postBytes(
            baseAddress: options.baseAddress,
            path: path,
            headers: ["Authorization": "Token \(options.apiKey ?? "")"],
            contentType: "audio/raw",
            body: audioPcm16Mono)

        guard resp.isSuccess else {
            logger?.logInformation("Deepgram returned \(resp.statusCode)")
            return emptyTranscription()
        }
        guard let root = jsonObject(resp.body),
              let results = root.object("results"),
              let channels = results.array("channels"), !channels.isEmpty,
              let firstChannel = channels[0] as? [String: Any],
              let alts = firstChannel.array("alternatives"), !alts.isEmpty,
              let firstAlt = alts[0] as? [String: Any]
        else { return emptyTranscription() }

        let text = firstAlt.string("transcript") ?? ""

        var segments: [SpeechTranscribedSegment] = []
        if let words = firstAlt.array("words") {
            for case let w as [String: Any] in words {
                let start = w.double("start") ?? 0
                let end = w.double("end") ?? start
                segments.append(SpeechTranscribedSegment(
                    text: w.string("word") ?? "",
                    offset: TimeInterval(start),
                    duration: TimeInterval(end - start),
                    language: languageHint,
                    confidence: Float(w.double("confidence") ?? 0)))
            }
        }

        var duration: TimeInterval = 0
        if let meta = root.object("metadata"), let d = meta.double("duration") { duration = TimeInterval(d) }

        return SpeechTranscriptionResult(text: text, language: languageHint, segments: segments, totalDuration: duration)
    }
}

/// AssemblyAI-backed `ISpeechRecognizer`. Two-step upload → submit → poll.
/// (C# `AssemblyAiSpeechRecognizer`.)
public final class AssemblyAiSpeechRecognizer: ISpeechRecognizer, @unchecked Sendable {
    private let transport: any ISpeechCloudHttpTransport
    private let options: AssemblyAiOptions
    private let logger: (any ICircleAILogger)?
    /// Injected delay between poll attempts (defaults to the C# 500 ms).
    private let pollInterval: TimeInterval
    /// Max poll attempts (defaults to the C# 60 = 30 s at 500 ms).
    private let maxPolls: Int

    public init(transport: any ISpeechCloudHttpTransport, options: AssemblyAiOptions,
                logger: (any ICircleAILogger)? = nil, pollInterval: TimeInterval = 0.5, maxPolls: Int = 60) {
        self.transport = transport
        self.options = options
        self.logger = logger
        self.pollInterval = pollInterval
        self.maxPolls = maxPolls
    }

    public var backendId: String { "assemblyai" }
    public var isConfigured: Bool { !isBlank(options.apiKey) }

    public func transcribe(audioPcm16Mono: Data, sampleRateHz: Int, languageHint: String?) async throws -> SpeechTranscriptionResult {
        guard isConfigured else { return emptyTranscription() }
        let apiKey = options.apiKey ?? ""

        // 1) Upload audio.
        let wav = SpeechCloudWav.wrapPcmAsWav(audioPcm16Mono, sampleRate: sampleRateHz)
        let uploadResp = try await transport.postBytes(
            baseAddress: options.baseAddress,
            path: "/v2/upload",
            headers: ["Authorization": apiKey],
            contentType: "application/octet-stream",
            body: wav)
        guard uploadResp.isSuccess else {
            logger?.logInformation("AssemblyAI upload returned \(uploadResp.statusCode)")
            return emptyTranscription()
        }
        guard let uploadDoc = jsonObject(uploadResp.body),
              let uploadUrl = uploadDoc.string("upload_url"), !isBlank(uploadUrl)
        else { return emptyTranscription() }

        // 2) Submit transcript job.
        var submitBody: [String: Any] = ["audio_url": uploadUrl, "speech_model": options.speechModel]
        if !isBlank(languageHint) { submitBody["language_code"] = languageHint! }
        let submitJson = (try? JSONSerialization.data(withJSONObject: submitBody, options: [.sortedKeys])) ?? Data("{}".utf8)

        let submitResp = try await transport.postJson(
            baseAddress: options.baseAddress,
            path: "/v2/transcript",
            headers: ["Authorization": apiKey],
            contentType: "application/json",
            jsonBody: submitJson)
        guard submitResp.isSuccess else {
            logger?.logInformation("AssemblyAI submit returned \(submitResp.statusCode)")
            return emptyTranscription()
        }
        guard let submitDoc = jsonObject(submitResp.body),
              let transcriptId = submitDoc.string("id"), !isBlank(transcriptId)
        else { return emptyTranscription() }

        // 3) Poll until completed.
        for _ in 0..<maxPolls {
            try? await Task.sleep(nanoseconds: UInt64(pollInterval * 1_000_000_000))
            try Task.checkCancellation()

            let pollResp = try await transport.get(
                baseAddress: options.baseAddress,
                path: "/v2/transcript/\(transcriptId)",
                headers: ["Authorization": apiKey])
            guard pollResp.isSuccess, let pollDoc = jsonObject(pollResp.body) else { continue }

            let status = pollDoc.string("status")
            if status == "completed" {
                let text = pollDoc.string("text") ?? ""
                let lang = pollDoc.string("language_code") ?? languageHint
                let duration = TimeInterval(pollDoc.double("audio_duration") ?? 0)

                var segments: [SpeechTranscribedSegment] = []
                if let words = pollDoc.array("words") {
                    for case let w as [String: Any] in words {
                        // AssemblyAI reports ms.
                        let start = (w.double("start") ?? 0) / 1000
                        let end = (w.double("end") ?? start * 1000) / 1000
                        segments.append(SpeechTranscribedSegment(
                            text: w.string("text") ?? "",
                            offset: TimeInterval(start),
                            duration: TimeInterval(max(0, end - start)),
                            language: lang,
                            confidence: Float(w.double("confidence") ?? 0)))
                    }
                }
                return SpeechTranscriptionResult(text: text, language: lang, segments: segments, totalDuration: duration)
            }
            if status == "error" {
                logger?.logInformation("AssemblyAI transcript error: \(pollDoc.string("error") ?? "")")
                return emptyTranscription()
            }
        }

        logger?.logInformation("AssemblyAI transcript \(transcriptId) timed out")
        return emptyTranscription()
    }
}

/// Google Cloud STT-backed `ISpeechRecognizer`. (C# `GoogleSpeechRecognizer`.)
public final class GoogleSpeechRecognizer: ISpeechRecognizer, @unchecked Sendable {
    private let transport: any ISpeechCloudHttpTransport
    private let options: GoogleSpeechOptions
    private let logger: (any ICircleAILogger)?

    public init(transport: any ISpeechCloudHttpTransport, options: GoogleSpeechOptions, logger: (any ICircleAILogger)? = nil) {
        self.transport = transport
        self.options = options
        self.logger = logger
    }

    public var backendId: String { "google-stt" }
    public var isConfigured: Bool { !isBlank(options.apiKey) }

    public func transcribe(audioPcm16Mono: Data, sampleRateHz: Int, languageHint: String?) async throws -> SpeechTranscriptionResult {
        guard isConfigured else { return emptyTranscription() }

        let lang = isBlank(languageHint) ? options.languageCode : languageHint!
        let audioB64 = audioPcm16Mono.base64EncodedString()

        let payload: [String: Any] = [
            "config": [
                "encoding": "LINEAR16",
                "sampleRateHertz": sampleRateHz,
                "languageCode": lang,
                "enableWordTimeOffsets": true,
                "enableWordConfidence": true,
            ] as [String: Any],
            "audio": ["content": audioB64] as [String: Any],
        ]
        let json = (try? JSONSerialization.data(withJSONObject: payload, options: [.sortedKeys])) ?? Data("{}".utf8)

        let path = "/v1/speech:recognize?key=\(speechEscape(options.apiKey ?? ""))"
        let resp = try await transport.postJson(
            baseAddress: options.baseAddress,
            path: path,
            headers: [:],
            contentType: "application/json",
            jsonBody: json)

        guard resp.isSuccess else {
            logger?.logInformation("Google STT returned \(resp.statusCode)")
            return emptyTranscription()
        }
        guard let root = jsonObject(resp.body) else { return emptyTranscription() }

        var allText = ""
        var segments: [SpeechTranscribedSegment] = []
        if let results = root.array("results") {
            for case let r as [String: Any] in results {
                guard let alts = r.array("alternatives"), !alts.isEmpty,
                      let alt = alts[0] as? [String: Any] else { continue }
                if !allText.isEmpty { allText += " " }
                allText += alt.string("transcript") ?? ""

                if let words = alt.array("words") {
                    for case let w as [String: Any] in words {
                        let start = Self.parseSeconds(w, "startTime")
                        let end = Self.parseSeconds(w, "endTime")
                        segments.append(SpeechTranscribedSegment(
                            text: w.string("word") ?? "",
                            offset: TimeInterval(start),
                            duration: TimeInterval(max(0, end - start)),
                            language: lang,
                            confidence: Float(w.double("confidence") ?? 0)))
                    }
                }
            }
        }
        return SpeechTranscriptionResult(text: allText, language: lang, segments: segments, totalDuration: 0)
    }

    /// Google encodes durations as e.g. "1.500s".
    private static func parseSeconds(_ el: [String: Any], _ property: String) -> Double {
        guard var s = el.string(property), !isBlank(s) else { return 0 }
        if s.hasSuffix("s") { s = String(s.dropLast()) }
        return Double(s) ?? 0
    }
}

/// Azure STT-backed `ISpeechRecognizer`. (C# `AzureSpeechRecognizer`.)
public final class AzureSpeechRecognizer: ISpeechRecognizer, @unchecked Sendable {
    private let transport: any ISpeechCloudHttpTransport
    private let options: AzureSpeechOptions
    private let logger: (any ICircleAILogger)?

    public init(transport: any ISpeechCloudHttpTransport, options: AzureSpeechOptions, logger: (any ICircleAILogger)? = nil) {
        self.transport = transport
        self.options = options
        self.logger = logger
    }

    public var backendId: String { "azure-stt" }
    public var isConfigured: Bool { !isBlank(options.apiKey) && options.baseAddress != nil }

    public func transcribe(audioPcm16Mono: Data, sampleRateHz: Int, languageHint: String?) async throws -> SpeechTranscriptionResult {
        guard isConfigured, let baseAddress = options.baseAddress else { return emptyTranscription() }

        let lang = isBlank(languageHint) ? options.languageCode : languageHint!
        let path = "/speech/recognition/conversation/cognitiveservices/v1?language=\(speechEscape(lang))&format=detailed"

        let resp = try await transport.postBytes(
            baseAddress: baseAddress,
            path: path,
            headers: [
                "Ocp-Apim-Subscription-Key": options.apiKey ?? "",
                "Accept": "application/json",
            ],
            contentType: "audio/wav; codecs=audio/pcm; samplerate=\(sampleRateHz)",
            body: audioPcm16Mono)

        guard resp.isSuccess else {
            logger?.logInformation("Azure STT returned \(resp.statusCode)")
            return emptyTranscription()
        }
        guard let root = jsonObject(resp.body), root.string("RecognitionStatus") == "Success" else {
            return emptyTranscription()
        }

        let text = root.string("DisplayText") ?? ""
        // Azure offsets/durations are in 100-ns ticks (HNS) → seconds.
        let offsetTicks = (root["Offset"] as? NSNumber)?.doubleValue ?? 0
        let durationTicks = (root["Duration"] as? NSNumber)?.doubleValue ?? 0
        let duration = TimeInterval(durationTicks / 10_000_000)

        var confidence: Float = 0
        if let nbest = root.array("NBest"), !nbest.isEmpty, let first = nbest[0] as? [String: Any],
           let c = first.double("Confidence") {
            confidence = Float(c)
        }

        let segment = SpeechTranscribedSegment(
            text: text,
            offset: TimeInterval(offsetTicks / 10_000_000),
            duration: duration,
            language: lang,
            confidence: confidence)
        return SpeechTranscriptionResult(text: text, language: lang, segments: [segment], totalDuration: duration)
    }
}

/// Cartesia-backed `ISpeechRecognizer`. Multipart upload of WAV-wrapped audio.
/// (C# `CartesiaSpeechRecognizer`.)
public final class CartesiaSpeechRecognizer: ISpeechRecognizer, @unchecked Sendable {
    private let transport: any ISpeechCloudHttpTransport
    private let options: CartesiaSttOptions
    private let logger: (any ICircleAILogger)?

    public init(transport: any ISpeechCloudHttpTransport, options: CartesiaSttOptions, logger: (any ICircleAILogger)? = nil) {
        self.transport = transport
        self.options = options
        self.logger = logger
    }

    public var backendId: String { "cartesia-stt" }
    public var isConfigured: Bool { !isBlank(options.apiKey) }

    public func transcribe(audioPcm16Mono: Data, sampleRateHz: Int, languageHint: String?) async throws -> SpeechTranscriptionResult {
        guard isConfigured else { return emptyTranscription() }

        let wav = SpeechCloudWav.wrapPcmAsWav(audioPcm16Mono, sampleRate: sampleRateHz)
        var parts: [SpeechCloudFormPart] = [
            .file("file", fileName: "audio.wav", contentType: "audio/wav", data: wav),
            .text("model", options.model),
        ]
        if !isBlank(languageHint) { parts.append(.text("language", languageHint!)) }

        let resp = try await transport.postMultipart(
            baseAddress: options.baseAddress,
            path: "/v1/transcribe",
            headers: [
                "Authorization": "Bearer \(options.apiKey ?? "")",
                "Cartesia-Version": options.cartesiaVersion,
            ],
            parts: parts)

        guard resp.isSuccess else {
            logger?.logInformation("Cartesia STT returned \(resp.statusCode)")
            return emptyTranscription()
        }
        guard let root = jsonObject(resp.body) else { return emptyTranscription() }

        let text = root.string("text") ?? ""
        let lang = root.string("language") ?? languageHint
        let duration = TimeInterval(root.double("duration") ?? 0)
        return SpeechTranscriptionResult(text: text, language: lang, segments: [], totalDuration: duration)
    }
}

// =====================================================================
// Synthesisers
// =====================================================================

/// OpenAI TTS-backed `ISpeechSynthesizer`. Returns PCM-16 mono at the option's
/// sample rate. (C# `OpenAiSpeechSynthesizer`.)
public final class OpenAiSpeechSynthesizer: ISpeechSynthesizer, @unchecked Sendable {
    private let transport: any ISpeechCloudHttpTransport
    private let options: OpenAiVoiceOptions
    private let logger: (any ICircleAILogger)?

    public init(transport: any ISpeechCloudHttpTransport, options: OpenAiVoiceOptions, logger: (any ICircleAILogger)? = nil) {
        self.transport = transport
        self.options = options
        self.logger = logger
    }

    public var backendId: String { "openai-tts" }
    public var isConfigured: Bool { !isBlank(options.apiKey) }

    public func synthesize(text: String, voiceId: String?, languageHint: String?) async throws -> SpeechSynthesisResult {
        guard isConfigured else { return emptySynthesis() }

        let resolvedVoice = isBlank(voiceId) ? options.defaultVoice : voiceId!
        let payload: [String: Any] = [
            "model": options.speechModel,
            "input": text,
            "voice": resolvedVoice,
            "response_format": "pcm",
        ]
        let json = (try? JSONSerialization.data(withJSONObject: payload, options: [.sortedKeys])) ?? Data("{}".utf8)

        let resp = try await transport.postJson(
            baseAddress: options.baseAddress,
            path: "/v1/audio/speech",
            headers: ["Authorization": "Bearer \(options.apiKey ?? "")"],
            contentType: "application/json",
            jsonBody: json)

        guard resp.isSuccess else {
            logger?.logInformation("OpenAI synthesis returned \(resp.statusCode)")
            return emptySynthesis()
        }

        let bytes = resp.body
        let samples = bytes.count / 2
        let duration = TimeInterval(Double(samples) / Double(options.pcmSampleRateHz))
        return SpeechSynthesisResult(audioPcm16Mono: bytes, sampleRateHz: options.pcmSampleRateHz, duration: duration)
    }
}

/// ElevenLabs-backed `ISpeechSynthesizer`. (C# `ElevenLabsSpeechSynthesizer`.)
public final class ElevenLabsSpeechSynthesizer: ISpeechSynthesizer, @unchecked Sendable {
    private let transport: any ISpeechCloudHttpTransport
    private let options: ElevenLabsOptions
    private let logger: (any ICircleAILogger)?

    public init(transport: any ISpeechCloudHttpTransport, options: ElevenLabsOptions, logger: (any ICircleAILogger)? = nil) {
        self.transport = transport
        self.options = options
        self.logger = logger
    }

    public var backendId: String { "elevenlabs" }
    public var isConfigured: Bool { !isBlank(options.apiKey) }

    public func synthesize(text: String, voiceId: String?, languageHint: String?) async throws -> SpeechSynthesisResult {
        guard isConfigured else { return emptySynthesis() }

        let voice = isBlank(voiceId) ? options.defaultVoiceId : voiceId!
        let rate = Self.parsePcmRate(options.outputFormat, fallback: options.pcmSampleRateHz)

        let payload: [String: Any] = ["text": text, "model_id": options.model]
        let json = (try? JSONSerialization.data(withJSONObject: payload, options: [.sortedKeys])) ?? Data("{}".utf8)

        let resp = try await transport.postJson(
            baseAddress: options.baseAddress,
            path: "/v1/text-to-speech/\(speechEscape(voice))?output_format=\(options.outputFormat)",
            headers: ["xi-api-key": options.apiKey ?? ""],
            contentType: "application/json",
            jsonBody: json)

        guard resp.isSuccess else {
            logger?.logInformation("ElevenLabs returned \(resp.statusCode)")
            return emptySynthesis()
        }

        let bytes = resp.body
        let samples = bytes.count / 2
        return SpeechSynthesisResult(
            audioPcm16Mono: bytes,
            sampleRateHz: rate,
            duration: TimeInterval(Double(samples) / Double(rate)))
    }

    /// Format: pcm_22050 / pcm_24000 / pcm_44100 / pcm_16000.
    private static func parsePcmRate(_ outputFormat: String, fallback: Int) -> Int {
        guard let range = outputFormat.range(of: "pcm_([0-9]+)", options: .regularExpression) else { return fallback }
        let digits = outputFormat[range].dropFirst(4)  // drop "pcm_"
        return Int(digits) ?? fallback
    }
}

/// Cartesia Sonic-backed `ISpeechSynthesizer`. (C# `CartesiaSpeechSynthesizer`.)
public final class CartesiaSpeechSynthesizer: ISpeechSynthesizer, @unchecked Sendable {
    private let transport: any ISpeechCloudHttpTransport
    private let options: CartesiaTtsOptions
    private let logger: (any ICircleAILogger)?

    public init(transport: any ISpeechCloudHttpTransport, options: CartesiaTtsOptions, logger: (any ICircleAILogger)? = nil) {
        self.transport = transport
        self.options = options
        self.logger = logger
    }

    public var backendId: String { "cartesia-tts" }
    public var isConfigured: Bool { !isBlank(options.apiKey) }

    public func synthesize(text: String, voiceId: String?, languageHint: String?) async throws -> SpeechSynthesisResult {
        guard isConfigured else { return emptySynthesis() }

        let voice = isBlank(voiceId) ? options.defaultVoiceId : voiceId!
        let payload: [String: Any] = [
            "model_id": options.model,
            "transcript": text,
            "voice": ["mode": "id", "id": voice] as [String: Any],
            "output_format": [
                "container": options.outputContainer,
                "encoding": options.outputEncoding,
                "sample_rate": options.pcmSampleRateHz,
            ] as [String: Any],
            "language": languageHint ?? "en",
        ]
        let json = (try? JSONSerialization.data(withJSONObject: payload, options: [.sortedKeys])) ?? Data("{}".utf8)

        let resp = try await transport.postJson(
            baseAddress: options.baseAddress,
            path: "/v1/tts/bytes",
            headers: [
                "Authorization": "Bearer \(options.apiKey ?? "")",
                "Cartesia-Version": options.cartesiaVersion,
            ],
            contentType: "application/json",
            jsonBody: json)

        guard resp.isSuccess else {
            logger?.logInformation("Cartesia TTS returned \(resp.statusCode)")
            return emptySynthesis()
        }

        let bytes = resp.body
        let samples = bytes.count / 2
        return SpeechSynthesisResult(
            audioPcm16Mono: bytes,
            sampleRateHz: options.pcmSampleRateHz,
            duration: TimeInterval(Double(samples) / Double(options.pcmSampleRateHz)))
    }
}

/// Deepgram Aura-backed `ISpeechSynthesizer`. (C# `DeepgramSpeechSynthesizer`.)
public final class DeepgramSpeechSynthesizer: ISpeechSynthesizer, @unchecked Sendable {
    private let transport: any ISpeechCloudHttpTransport
    private let options: DeepgramTtsOptions
    private let logger: (any ICircleAILogger)?

    public init(transport: any ISpeechCloudHttpTransport, options: DeepgramTtsOptions, logger: (any ICircleAILogger)? = nil) {
        self.transport = transport
        self.options = options
        self.logger = logger
    }

    public var backendId: String { "deepgram-aura" }
    public var isConfigured: Bool { !isBlank(options.apiKey) }

    public func synthesize(text: String, voiceId: String?, languageHint: String?) async throws -> SpeechSynthesisResult {
        guard isConfigured else { return emptySynthesis() }

        let voice = isBlank(voiceId) ? options.voice : voiceId!
        let path = "/v1/speak?model=\(speechEscape(voice))&encoding=linear16&sample_rate=\(options.pcmSampleRateHz)"
        let json = (try? JSONSerialization.data(withJSONObject: ["text": text], options: [.sortedKeys])) ?? Data("{}".utf8)

        let resp = try await transport.postJson(
            baseAddress: options.baseAddress,
            path: path,
            headers: ["Authorization": "Token \(options.apiKey ?? "")"],
            contentType: "application/json",
            jsonBody: json)

        guard resp.isSuccess else {
            logger?.logInformation("Deepgram Aura returned \(resp.statusCode)")
            return emptySynthesis()
        }

        let bytes = resp.body
        let samples = bytes.count / 2
        return SpeechSynthesisResult(
            audioPcm16Mono: bytes,
            sampleRateHz: options.pcmSampleRateHz,
            duration: TimeInterval(Double(samples) / Double(options.pcmSampleRateHz)))
    }
}

/// Azure TTS-backed `ISpeechSynthesizer`. SSML body → raw PCM. (C#
/// `AzureSpeechSynthesizer`.)
public final class AzureSpeechSynthesizer: ISpeechSynthesizer, @unchecked Sendable {
    private let transport: any ISpeechCloudHttpTransport
    private let options: AzureTtsOptions
    private let logger: (any ICircleAILogger)?

    public init(transport: any ISpeechCloudHttpTransport, options: AzureTtsOptions, logger: (any ICircleAILogger)? = nil) {
        self.transport = transport
        self.options = options
        self.logger = logger
    }

    public var backendId: String { "azure-tts" }
    public var isConfigured: Bool { !isBlank(options.apiKey) && options.baseAddress != nil }

    public func synthesize(text: String, voiceId: String?, languageHint: String?) async throws -> SpeechSynthesisResult {
        guard isConfigured, let baseAddress = options.baseAddress else { return emptySynthesis() }

        let voice = isBlank(voiceId) ? options.defaultVoiceName : voiceId!
        let lang = isBlank(languageHint) ? options.languageCode : languageHint!
        let rate = options.pcmSampleRateHz

        let ssml = "<speak version='1.0' xml:lang='\(lang)'>\n"
            + "  <voice name='\(voice)'>\(Self.htmlEncode(text))</voice>\n"
            + "</speak>"

        let resp = try await transport.postBytes(
            baseAddress: baseAddress,
            path: "/cognitiveservices/v1",
            headers: [
                "Ocp-Apim-Subscription-Key": options.apiKey ?? "",
                "X-Microsoft-OutputFormat": "raw-\(rate / 1000)khz-16bit-mono-pcm",
                "User-Agent": "CircleAI",
            ],
            contentType: "application/ssml+xml",
            body: Data(ssml.utf8))

        guard resp.isSuccess else {
            logger?.logInformation("Azure TTS returned \(resp.statusCode)")
            return emptySynthesis()
        }

        let bytes = resp.body
        let samples = bytes.count / 2
        return SpeechSynthesisResult(
            audioPcm16Mono: bytes,
            sampleRateHz: rate,
            duration: TimeInterval(Double(samples) / Double(rate)))
    }

    /// Minimal XML/HTML text escape (≈ `WebUtility.HtmlEncode`) for SSML.
    private static func htmlEncode(_ s: String) -> String {
        s.replacingOccurrences(of: "&", with: "&amp;")
            .replacingOccurrences(of: "<", with: "&lt;")
            .replacingOccurrences(of: ">", with: "&gt;")
            .replacingOccurrences(of: "\"", with: "&quot;")
            .replacingOccurrences(of: "'", with: "&#39;")
    }
}

/// Google TTS-backed `ISpeechSynthesizer`. base64 LINEAR16 (WAV) → stripped PCM.
/// (C# `GoogleSpeechSynthesizer`.)
public final class GoogleSpeechSynthesizer: ISpeechSynthesizer, @unchecked Sendable {
    private let transport: any ISpeechCloudHttpTransport
    private let options: GoogleTtsOptions
    private let logger: (any ICircleAILogger)?

    public init(transport: any ISpeechCloudHttpTransport, options: GoogleTtsOptions, logger: (any ICircleAILogger)? = nil) {
        self.transport = transport
        self.options = options
        self.logger = logger
    }

    public var backendId: String { "google-tts" }
    public var isConfigured: Bool { !isBlank(options.apiKey) }

    public func synthesize(text: String, voiceId: String?, languageHint: String?) async throws -> SpeechSynthesisResult {
        guard isConfigured else { return emptySynthesis() }

        let voice = isBlank(voiceId) ? options.defaultVoiceName : voiceId!
        let lang = isBlank(languageHint) ? options.languageCode : languageHint!

        let payload: [String: Any] = [
            "input": ["text": text] as [String: Any],
            "voice": ["languageCode": lang, "name": voice] as [String: Any],
            "audioConfig": [
                "audioEncoding": "LINEAR16",
                "sampleRateHertz": options.pcmSampleRateHz,
            ] as [String: Any],
        ]
        let json = (try? JSONSerialization.data(withJSONObject: payload, options: [.sortedKeys])) ?? Data("{}".utf8)

        let path = "/v1/text:synthesize?key=\(speechEscape(options.apiKey ?? ""))"
        let resp = try await transport.postJson(
            baseAddress: options.baseAddress,
            path: path,
            headers: [:],
            contentType: "application/json",
            jsonBody: json)

        guard resp.isSuccess else {
            logger?.logInformation("Google TTS returned \(resp.statusCode)")
            return emptySynthesis()
        }
        guard let root = jsonObject(resp.body),
              let b64 = root.string("audioContent"), !b64.isEmpty,
              let raw = Data(base64Encoded: b64)
        else { return emptySynthesis() }

        let pcm = SpeechCloudWav.stripWavHeader(raw)
        let samples = pcm.count / 2
        return SpeechSynthesisResult(
            audioPcm16Mono: pcm,
            sampleRateHz: options.pcmSampleRateHz,
            duration: TimeInterval(Double(samples) / Double(options.pcmSampleRateHz)))
    }
}

/// Play.HT-backed `ISpeechSynthesizer`. (C# `PlayHtSpeechSynthesizer`.)
public final class PlayHtSpeechSynthesizer: ISpeechSynthesizer, @unchecked Sendable {
    private let transport: any ISpeechCloudHttpTransport
    private let options: PlayHtOptions
    private let logger: (any ICircleAILogger)?

    public init(transport: any ISpeechCloudHttpTransport, options: PlayHtOptions, logger: (any ICircleAILogger)? = nil) {
        self.transport = transport
        self.options = options
        self.logger = logger
    }

    public var backendId: String { "playht" }
    public var isConfigured: Bool { !isBlank(options.apiKey) && !isBlank(options.userId) }

    public func synthesize(text: String, voiceId: String?, languageHint: String?) async throws -> SpeechSynthesisResult {
        guard isConfigured else { return emptySynthesis() }

        let voice = isBlank(voiceId) ? options.defaultVoice : voiceId!
        let payload: [String: Any] = [
            "text": text,
            "voice": voice,
            "voice_engine": options.model,
            "output_format": "raw",
            "sample_rate": options.pcmSampleRateHz,
            "language": languageHint ?? "english",
        ]
        let json = (try? JSONSerialization.data(withJSONObject: payload, options: [.sortedKeys])) ?? Data("{}".utf8)

        let resp = try await transport.postJson(
            baseAddress: options.baseAddress,
            path: "/api/v2/tts/stream",
            headers: [
                "Authorization": "Bearer \(options.apiKey ?? "")",
                "X-USER-ID": options.userId ?? "",
                "Accept": "audio/raw",
            ],
            contentType: "application/json",
            jsonBody: json)

        guard resp.isSuccess else {
            logger?.logInformation("Play.HT returned \(resp.statusCode)")
            return emptySynthesis()
        }

        let bytes = resp.body
        let samples = bytes.count / 2
        return SpeechSynthesisResult(
            audioPcm16Mono: bytes,
            sampleRateHz: options.pcmSampleRateHz,
            duration: TimeInterval(Double(samples) / Double(options.pcmSampleRateHz)))
    }
}
