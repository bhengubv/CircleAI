// Video.swift
//
// Port of the CircleAI.Video contract surface (C# is the exact spec):
//   Primitives.cs          → StyleId, VideoResolution, StyleReferenceFrame,
//                            StyleAttribution, StyleReference, AudioTrack,
//                            VideoGenerationRequest, VideoGenerationResult,
//                            StyleScriptRequest, StyleScriptResult
//   Contracts.cs           → IVideoGenerator, IStyleScript, IStyleReference
//   NullImplementations.cs → NullVideoGenerator, NullStyleScript,
//                            InMemoryStyleReference
//
// Driving use case: txtMe Video Mail. Sender calls, no answer, types a message;
// the recipient's B! renders it as a short styled video. The one concrete video
// backend CircleAI ships first is CogVideoX-2B (native, on-device, under MNN);
// like every native leaf it is INJECTED behind `IVideoGenerator`. The SDK ships
// the three interfaces, the fail-closed null defaults, and the thread-safe
// in-memory style catalogue — all deterministic, no stubs.
//
// NAMING: Swift flattens `CircleAI.Video` into the single `CircleAI` module.
// None of these type names collide with existing symbols (checked: `AudioTrack`,
// `VideoResolution`, `StyleReference`, `VideoFrame`(Vision) etc. are all free).
//
// C# `ReadOnlyMemory<byte>` maps to `Data`; `TimeSpan` maps to `TimeInterval`
// (seconds); `IReadOnlyList<T>` maps to `[T]`.

import Foundation

// =====================================================================
// Primitives (Primitives.cs)
// =====================================================================

/// Identifier for one registered style (e.g. "pooh-1926", "noir-detective",
/// "space-opera"). Port of the `CircleAI.Video.StyleId` `readonly record struct`
/// (with its `ToString` / implicit `string` conversion preserved via
/// `description` and the `value` field).
public struct StyleId: Sendable, Hashable, Codable, CustomStringConvertible {
    public let value: String

    public init(_ value: String) {
        self.value = value
    }

    public var description: String { value }
}

/// Output resolution for a generated video. Port of
/// `CircleAI.Video.VideoResolution`.
public struct VideoResolution: Sendable, Equatable, Codable {
    public let width: Int
    public let height: Int

    public init(width: Int, height: Int) {
        self.width = width
        self.height = height
    }

    public static var p480: VideoResolution { VideoResolution(width: 720, height: 480) }
    public static var p720: VideoResolution { VideoResolution(width: 1280, height: 720) }
    public static var p1080: VideoResolution { VideoResolution(width: 1920, height: 1080) }
}

/// One reference frame the generator can ground style on — public-domain
/// illustration, original-character render, etc. Port of
/// `CircleAI.Video.StyleReferenceFrame`.
public struct StyleReferenceFrame: Sendable, Equatable {
    public let imageBytes: Data
    public let mimeType: String
    public let caption: String?

    public init(imageBytes: Data, mimeType: String, caption: String? = nil) {
        self.imageBytes = imageBytes
        self.mimeType = mimeType
        self.caption = caption
    }
}

/// Attribution + license metadata for one style. Port of
/// `CircleAI.Video.StyleAttribution`.
public struct StyleAttribution: Sendable, Equatable, Codable {
    public let source: String
    public let license: String
    public let url: String?

    public init(source: String, license: String, url: String? = nil) {
        self.source = source
        self.license = license
        self.url = url
    }
}

/// One style the host has registered with the catalogue. Port of
/// `CircleAI.Video.StyleReference`.
public struct StyleReference: Sendable, Equatable {
    public let id: StyleId
    public let displayName: String
    public let shortDescription: String
    public let attribution: StyleAttribution
    public let voicePersonaId: String?
    public let frames: [StyleReferenceFrame]

    public init(
        id: StyleId,
        displayName: String,
        shortDescription: String,
        attribution: StyleAttribution,
        voicePersonaId: String?,
        frames: [StyleReferenceFrame]
    ) {
        self.id = id
        self.displayName = displayName
        self.shortDescription = shortDescription
        self.attribution = attribution
        self.voicePersonaId = voicePersonaId
        self.frames = frames
    }
}

/// Audio track produced by CircleAI.Speech for the generator to embed. Port of
/// `CircleAI.Video.AudioTrack`. `TimeSpan Duration` maps to `TimeInterval`.
public struct AudioTrack: Sendable, Equatable {
    public let audioPcm16Mono: Data
    public let sampleRateHz: Int
    public let duration: TimeInterval

    public init(audioPcm16Mono: Data, sampleRateHz: Int, duration: TimeInterval) {
        self.audioPcm16Mono = audioPcm16Mono
        self.sampleRateHz = sampleRateHz
        self.duration = duration
    }
}

/// One generation request — text + optional style + optional grounding image +
/// optional audio. Port of `CircleAI.Video.VideoGenerationRequest`.
public struct VideoGenerationRequest: Sendable, Equatable {
    public let prompt: String
    public let duration: TimeInterval
    public let resolution: VideoResolution
    public let frameRate: Int
    public let styleId: StyleId?
    public let referenceImage: StyleReferenceFrame?
    public let audioTrack: AudioTrack?
    public let seed: Int64?

    public init(
        prompt: String,
        duration: TimeInterval,
        resolution: VideoResolution,
        frameRate: Int = 24,
        styleId: StyleId? = nil,
        referenceImage: StyleReferenceFrame? = nil,
        audioTrack: AudioTrack? = nil,
        seed: Int64? = nil
    ) {
        self.prompt = prompt
        self.duration = duration
        self.resolution = resolution
        self.frameRate = frameRate
        self.styleId = styleId
        self.referenceImage = referenceImage
        self.audioTrack = audioTrack
        self.seed = seed
    }
}

/// One generation outcome. Port of `CircleAI.Video.VideoGenerationResult`.
public struct VideoGenerationResult: Sendable, Equatable {
    public let videoBytes: Data
    public let mimeType: String
    public let duration: TimeInterval
    public let frameCount: Int
    public let resolution: VideoResolution
    public let backendId: String

    public init(
        videoBytes: Data,
        mimeType: String,
        duration: TimeInterval,
        frameCount: Int,
        resolution: VideoResolution,
        backendId: String
    ) {
        self.videoBytes = videoBytes
        self.mimeType = mimeType
        self.duration = duration
        self.frameCount = frameCount
        self.resolution = resolution
        self.backendId = backendId
    }
}

/// One style-script request — raw user message + chosen voice. Port of
/// `CircleAI.Video.StyleScriptRequest`.
public struct StyleScriptRequest: Sendable, Equatable {
    public let sourceMessage: String
    public let style: StyleId
    public let speakerHint: String?
    public let languageHint: String?

    public init(sourceMessage: String, style: StyleId, speakerHint: String? = nil, languageHint: String? = nil) {
        self.sourceMessage = sourceMessage
        self.style = style
        self.speakerHint = speakerHint
        self.languageHint = languageHint
    }
}

/// One style-script outcome — the rewritten line + voice + estimated duration.
/// Port of `CircleAI.Video.StyleScriptResult`.
public struct StyleScriptResult: Sendable, Equatable {
    public let rewrittenText: String
    public let style: StyleId
    public let voicePersonaId: String?
    public let estimatedSpokenDuration: TimeInterval

    public init(
        rewrittenText: String,
        style: StyleId,
        voicePersonaId: String?,
        estimatedSpokenDuration: TimeInterval
    ) {
        self.rewrittenText = rewrittenText
        self.style = style
        self.voicePersonaId = voicePersonaId
        self.estimatedSpokenDuration = estimatedSpokenDuration
    }
}

// =====================================================================
// Contracts (Contracts.cs)
// =====================================================================

/// Raised by an `IVideoGenerator` when the device cannot satisfy a request
/// (mirrors the C# doc "Throws if the device cannot satisfy the request").
public enum VideoGenerationError: Error, Equatable, Sendable {
    /// The device cannot honour the request (e.g. insufficient VRAM).
    case deviceCannotSatisfyRequest(String)
}

/// Generate a short video from a text prompt (and optional style + reference
/// frame + audio track). Port of `CircleAI.Video.IVideoGenerator`.
public protocol IVideoGenerator: AnyObject, Sendable {
    /// Backend self-identification — "cogvideox-2b", "ltx-video-2b-distilled",
    /// "null".
    var backendId: String { get }

    /// Synthesise the requested video. Throws if the device cannot satisfy the
    /// request.
    func generate(request: VideoGenerationRequest) async throws -> VideoGenerationResult
}

/// Rewrite a user message in a chosen style's voice. Port of
/// `CircleAI.Video.IStyleScript`.
public protocol IStyleScript: AnyObject, Sendable {
    /// Backend self-identification — "circleai-llm", "null".
    var backendId: String { get }

    /// Rewrite the source message in the requested style.
    func rewrite(request: StyleScriptRequest) async throws -> StyleScriptResult
}

/// Catalogue of registered styles. Lets the txtMe UI present a picker and lets
/// the generator look up grounding frames. Port of
/// `CircleAI.Video.IStyleReference`.
public protocol IStyleReference: AnyObject, Sendable {
    /// Backend self-identification — "in-memory", "embedded-defaults", "null".
    var backendId: String { get }

    /// Register a style (typically at host startup).
    func register(_ style: StyleReference) async throws

    /// Look up one style by id.
    func get(_ id: StyleId) async throws -> StyleReference?

    /// Enumerate every registered style — drives picker UIs.
    func list() async throws -> [StyleReference]
}

// =====================================================================
// Null / in-memory implementations (NullImplementations.cs)
// =====================================================================

/// Returns an empty video — zero bytes, declared mime type "video/mp4". Useful
/// as the DI default. Port of `CircleAI.Video.NullVideoGenerator`.
public final class NullVideoGenerator: IVideoGenerator, @unchecked Sendable {
    public static let instance = NullVideoGenerator()
    public init() {}

    public var backendId: String { "null" }

    public func generate(request: VideoGenerationRequest) async throws -> VideoGenerationResult {
        VideoGenerationResult(
            videoBytes: Data(),
            mimeType: "video/mp4",
            duration: 0,
            frameCount: 0,
            resolution: request.resolution,
            backendId: "null")
    }
}

/// Returns the source message unchanged with a zero estimated duration. Port of
/// `CircleAI.Video.NullStyleScript`.
public final class NullStyleScript: IStyleScript, @unchecked Sendable {
    public static let instance = NullStyleScript()
    public init() {}

    public var backendId: String { "null" }

    public func rewrite(request: StyleScriptRequest) async throws -> StyleScriptResult {
        StyleScriptResult(
            rewrittenText: request.sourceMessage,
            style: request.style,
            voicePersonaId: nil,
            estimatedSpokenDuration: 0)
    }
}

/// Thread-safe in-memory style catalogue. Hosting layers (txtMe, content
/// authoring tools) register their style packs on startup and the picker reads
/// from here. Keyed case-insensitively to reproduce the C#
/// `OrdinalIgnoreCase` dictionary. Port of `CircleAI.Video.InMemoryStyleReference`.
public final class InMemoryStyleReference: IStyleReference, @unchecked Sendable {
    private let lock = NSLock()
    private var byId: [String: StyleReference] = [:]

    public init() {}

    public var backendId: String { "in-memory" }

    // Synchronous, lock-guarded helpers — the lock is never held across an await.

    private func put(_ style: StyleReference) {
        lock.lock(); defer { lock.unlock() }
        byId[InMemoryStyleReference.keyOf(style.id.value)] = style
    }

    private func fetch(_ id: StyleId) -> StyleReference? {
        lock.lock(); defer { lock.unlock() }
        return byId[InMemoryStyleReference.keyOf(id.value)]
    }

    private func snapshot() -> [StyleReference] {
        lock.lock(); defer { lock.unlock() }
        return Array(byId.values)
    }

    public func register(_ style: StyleReference) async throws {
        put(style)
    }

    public func get(_ id: StyleId) async throws -> StyleReference? {
        fetch(id)
    }

    public func list() async throws -> [StyleReference] {
        snapshot()
    }

    static func keyOf(_ id: String) -> String { id.lowercased() }
}
