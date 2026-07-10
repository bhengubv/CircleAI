// VisionCloud.swift
//
// Port of the CircleAI.Vision.Cloud image-generation surface (C# is the exact
// spec):
//   Contracts.cs                   → ImageGenerationRequest, ImageArtifact,
//                                     IImageGenerator, NullImageGenerator
//   Options.cs                     → OpenAiImageOptions, StabilityImageOptions
//   ImageGeneratorFallbackChain.cs → ImageGeneratorFallbackChain
//   OpenAiImageGenerator.cs        → OpenAiImageGenerator
//   StabilityImageGenerator.cs     → StabilityImageGenerator
//   ServiceCollectionExtensions.cs → GeneratorIds constants (the DI wiring itself
//                                     is .NET-container-specific and has no
//                                     SwiftPM analogue, so only the id constants
//                                     are carried over)
//
// The concrete cloud generators are HTTP+JSON/multipart and — exactly as the
// codebase already does for the cloud CHAT providers in HostingCloudFallback.swift
// ("the SDK never bakes in provider keys") — the HTTP call is the one injected
// leaf, behind `IImageHttpTransport`. Everything else (request shaping, response
// parsing, Count clamp, fail-soft-empty behaviour, the fallback chain) is ported
// faithfully. `LocalDeterministicImageGenerator` is the deterministic test fake.
//
// NAMING: Swift flattens `CircleAI.Vision.Cloud` into the single `CircleAI`
// module. None of these names collide with existing symbols.
//
// C# `byte[]?` maps to `Data?`; `IReadOnlyList<T>` maps to `[T]`.

import Foundation

// =====================================================================
// DTOs (Contracts.cs)
// =====================================================================

/// One image-generation request. Port of
/// `CircleAI.Vision.Cloud.ImageGenerationRequest`.
public struct ImageGenerationRequest: Sendable, Equatable, Codable {
    public let prompt: String
    public let negativePrompt: String?
    public let size: Int
    public let count: Int
    public let style: String?

    public init(
        prompt: String,
        negativePrompt: String? = nil,
        size: Int = 1024,
        count: Int = 1,
        style: String? = nil
    ) {
        self.prompt = prompt
        self.negativePrompt = negativePrompt
        self.size = size
        self.count = count
        self.style = style
    }
}

/// One generated image. Either `url` OR `bytes`, never both. Port of
/// `CircleAI.Vision.Cloud.ImageArtifact`.
public struct ImageArtifact: Sendable, Equatable {
    public let generatorId: String
    public let prompt: String
    public let mimeType: String
    public let url: String?
    public let bytes: Data?
    public let generatedAtUtc: Date

    public init(
        generatorId: String,
        prompt: String,
        mimeType: String,
        url: String?,
        bytes: Data?,
        generatedAtUtc: Date
    ) {
        self.generatorId = generatorId
        self.prompt = prompt
        self.mimeType = mimeType
        self.url = url
        self.bytes = bytes
        self.generatedAtUtc = generatedAtUtc
    }
}

// =====================================================================
// IImageGenerator (Contracts.cs)
// =====================================================================

/// Generate images from a text prompt. Port of
/// `CircleAI.Vision.Cloud.IImageGenerator`.
public protocol IImageGenerator: AnyObject, Sendable {
    /// Backend self-identification — "openai-images" / "stability" / "null".
    var generatorId: String { get }

    /// Display label for the UI selector.
    var displayLabel: String { get }

    /// True when the generator has the credentials it needs.
    var isConfigured: Bool { get }

    /// Status message for the UI.
    var statusMessage: String { get }

    /// Generate images. Fail-soft: empty list when not configured.
    func generate(request: ImageGenerationRequest) async throws -> [ImageArtifact]
}

/// Empty generator — always returns no images. Port of
/// `CircleAI.Vision.Cloud.NullImageGenerator`.
public final class NullImageGenerator: IImageGenerator, @unchecked Sendable {
    public static let instance = NullImageGenerator()
    public init() {}

    public var generatorId: String { "null" }
    public var displayLabel: String { "No image generator" }
    public var isConfigured: Bool { false }
    public var statusMessage: String {
        "No image generator wired. Configure OpenAI:ApiKey or Stability:ApiKey to enable."
    }

    public func generate(request: ImageGenerationRequest) async throws -> [ImageArtifact] { [] }
}

// =====================================================================
// Options (Options.cs)
// =====================================================================

/// OpenAI image-generation options. Port of
/// `CircleAI.Vision.Cloud.OpenAiImageOptions`.
public struct OpenAiImageOptions: Sendable, Equatable {
    public let baseAddress: String
    public let apiKey: String?
    /// Model id. Default `dall-e-3`.
    public let model: String

    public init(
        baseAddress: String = "https://api.openai.com",
        apiKey: String? = nil,
        model: String = "dall-e-3"
    ) {
        self.baseAddress = baseAddress
        self.apiKey = apiKey
        self.model = model
    }
}

/// Stability AI image-generation options. Port of
/// `CircleAI.Vision.Cloud.StabilityImageOptions`.
public struct StabilityImageOptions: Sendable, Equatable {
    public let baseAddress: String
    public let apiKey: String?
    /// Model id. Default `sd3.5-large`.
    public let model: String
    /// Output format. Default `png`.
    public let outputFormat: String

    public init(
        baseAddress: String = "https://api.stability.ai",
        apiKey: String? = nil,
        model: String = "sd3.5-large",
        outputFormat: String = "png"
    ) {
        self.baseAddress = baseAddress
        self.apiKey = apiKey
        self.model = model
        self.outputFormat = outputFormat
    }
}

/// Generator id constants (from `ServiceCollectionExtensions.GeneratorIds`).
public enum ImageGeneratorIds {
    public static let openAi = "openai-images"
    public static let stability = "stability"
}

// =====================================================================
// Injected HTTP leaf
// =====================================================================

/// One HTTP response the transport hands back: status code + body bytes.
public struct ImageHttpResponse: Sendable, Equatable {
    public let statusCode: Int
    public let body: Data

    public init(statusCode: Int, body: Data) {
        self.statusCode = statusCode
        self.body = body
    }

    /// Mirrors `HttpResponseMessage.IsSuccessStatusCode` (2xx).
    public var isSuccess: Bool { (200..<300).contains(statusCode) }
}

/// One multipart form part — either a plain text field or a file field.
public struct ImageHttpFormField: Sendable, Equatable {
    public let name: String
    public let value: String

    public init(name: String, value: String) {
        self.name = name
        self.value = value
    }
}

/// The single injected HTTP leaf the cloud generators use. Keeps the SDK free of
/// any baked-in HTTP client / provider key, exactly as the chat providers do.
public protocol IImageHttpTransport: Sendable {
    /// POST a JSON body. `headers` includes Authorization. Returns status + body.
    func postJson(
        baseAddress: String,
        path: String,
        headers: [String: String],
        jsonBody: Data
    ) async throws -> ImageHttpResponse

    /// POST a multipart/form-data body. `accept` sets the Accept header.
    func postMultipart(
        baseAddress: String,
        path: String,
        headers: [String: String],
        accept: String,
        fields: [ImageHttpFormField]
    ) async throws -> ImageHttpResponse
}

// =====================================================================
// OpenAiImageGenerator (OpenAiImageGenerator.cs)
// =====================================================================

/// `IImageGenerator` backed by OpenAI's `/v1/images/generations` endpoint.
/// Fail-soft when the API key is missing or the call fails — returns an empty
/// artifact list so a fallback chain can move on. Uses `response_format=url` and
/// clamps `count` to 1..4, exactly as the C#. Port of
/// `CircleAI.Vision.Cloud.OpenAiImageGenerator`.
public final class OpenAiImageGenerator: IImageGenerator, @unchecked Sendable {
    private let options: OpenAiImageOptions
    private let transport: any IImageHttpTransport
    private let clock: @Sendable () -> Date

    public init(
        options: OpenAiImageOptions,
        transport: any IImageHttpTransport,
        clock: @escaping @Sendable () -> Date = { Date() }
    ) {
        self.options = options
        self.transport = transport
        self.clock = clock
    }

    public var generatorId: String { "openai-images" }
    public var displayLabel: String { "OpenAI · \(options.model)" }
    public var isConfigured: Bool { !(options.apiKey ?? "").trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
    public var statusMessage: String {
        isConfigured
            ? "Ready · \(options.model)"
            : "OpenAI API key not configured — set OpenAI:ApiKey to enable."
    }

    public func generate(request: ImageGenerationRequest) async throws -> [ImageArtifact] {
        if !isConfigured { return [] }

        let n = min(max(request.count, 1), 4)   // Math.Clamp(Count, 1, 4)
        let payload: [String: Any] = [
            "model": options.model,
            "prompt": request.prompt,
            "n": n,
            "size": "\(request.size)x\(request.size)",
            "response_format": "url",
        ]
        let jsonBody = (try? JSONSerialization.data(withJSONObject: payload, options: [.sortedKeys])) ?? Data()
        let headers = ["Authorization": "Bearer \(options.apiKey ?? "")"]

        let response = try await transport.postJson(
            baseAddress: options.baseAddress,
            path: "/v1/images/generations",
            headers: headers,
            jsonBody: jsonBody)

        if !response.isSuccess { return [] }

        // Parse { "data": [ { "url": "..." }, ... ] }.
        guard
            let root = try? JSONSerialization.jsonObject(with: response.body) as? [String: Any],
            let data = root["data"] as? [[String: Any]]
        else {
            return []
        }

        var artifacts: [ImageArtifact] = []
        for item in data {
            if let url = item["url"] as? String {
                artifacts.append(ImageArtifact(
                    generatorId: generatorId,
                    prompt: request.prompt,
                    mimeType: "image/png",
                    url: url,
                    bytes: nil,
                    generatedAtUtc: clock()))
            }
        }
        return artifacts
    }
}

// =====================================================================
// StabilityImageGenerator (StabilityImageGenerator.cs)
// =====================================================================

/// `IImageGenerator` backed by Stability AI's
/// `/v2beta/stable-image/generate/sd3` endpoint. Stability returns one image per
/// call, so this loops `count` (clamped 1..4) times and returns images inline as
/// bytes. A per-image failure is skipped (the loop continues), matching the C#
/// `continue`. Port of `CircleAI.Vision.Cloud.StabilityImageGenerator`.
public final class StabilityImageGenerator: IImageGenerator, @unchecked Sendable {
    private let options: StabilityImageOptions
    private let transport: any IImageHttpTransport
    private let clock: @Sendable () -> Date

    public init(
        options: StabilityImageOptions,
        transport: any IImageHttpTransport,
        clock: @escaping @Sendable () -> Date = { Date() }
    ) {
        self.options = options
        self.transport = transport
        self.clock = clock
    }

    public var generatorId: String { "stability" }
    public var displayLabel: String { "Stability AI · \(options.model)" }
    public var isConfigured: Bool { !(options.apiKey ?? "").trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
    public var statusMessage: String {
        isConfigured
            ? "Ready · \(options.model)"
            : "Stability AI API key not configured — set Stability:ApiKey to enable."
    }

    public func generate(request: ImageGenerationRequest) async throws -> [ImageArtifact] {
        if !isConfigured { return [] }

        var artifacts: [ImageArtifact] = []
        let count = min(max(request.count, 1), 4)   // Math.Clamp(Count, 1, 4)
        for _ in 0..<count {
            try Task.checkCancellation()   // ct.ThrowIfCancellationRequested()

            var fields: [ImageHttpFormField] = [
                ImageHttpFormField(name: "prompt", value: request.prompt),
                ImageHttpFormField(name: "output_format", value: options.outputFormat),
                ImageHttpFormField(name: "model", value: options.model),
            ]
            if let neg = request.negativePrompt, !neg.isEmpty {
                fields.append(ImageHttpFormField(name: "negative_prompt", value: neg))
            }

            let headers = ["Authorization": "Bearer \(options.apiKey ?? "")"]
            let response = try await transport.postMultipart(
                baseAddress: options.baseAddress,
                path: "/v2beta/stable-image/generate/sd3",
                headers: headers,
                accept: "image/\(options.outputFormat)",
                fields: fields)

            if !response.isSuccess { continue }

            artifacts.append(ImageArtifact(
                generatorId: generatorId,
                prompt: request.prompt,
                mimeType: "image/\(options.outputFormat)",
                url: nil,
                bytes: response.body,
                generatedAtUtc: clock()))
        }
        return artifacts
    }
}

// =====================================================================
// ImageGeneratorFallbackChain (ImageGeneratorFallbackChain.cs)
// =====================================================================

/// Composite `IImageGenerator` — tries each child in order, skipping those that
/// report `isConfigured == false`. Returns the first non-empty artifact list, or
/// empty if everyone failed. Port of
/// `CircleAI.Vision.Cloud.ImageGeneratorFallbackChain`.
public final class ImageGeneratorFallbackChain: IImageGenerator, @unchecked Sendable {
    private let chain: [any IImageGenerator]

    public init(_ chain: [any IImageGenerator]) {
        self.chain = chain
    }

    public var generatorId: String { "fallback-chain" }
    public var displayLabel: String { "Fallback (\(chain.count))" }
    public var isConfigured: Bool { chain.contains { $0.isConfigured } }
    public var statusMessage: String {
        isConfigured
            ? "Ready · " + chain.filter { $0.isConfigured }.map { $0.generatorId }.joined(separator: " → ")
            : "No configured generator in chain."
    }

    public func generate(request: ImageGenerationRequest) async throws -> [ImageArtifact] {
        for g in chain {
            if !g.isConfigured { continue }
            let result = try await g.generate(request: request)
            if result.count > 0 { return result }
        }
        return []
    }
}

// =====================================================================
// LocalDeterministicImageGenerator (deterministic test fake)
// =====================================================================

/// Deterministic local fake `IImageGenerator`. Produces `min(max(count,1),4)`
/// synthetic artifacts (byte-payload derived from the prompt) without any
/// network, so the fallback chain and consumers can be tested offline. When
/// `isConfigured` is false it returns an empty list, so the chain skips it —
/// mirroring the real generators' fail-soft contract. This is the image analogue
/// of `LocalDeterministicChatGenerator` in HostingCloudFallback.swift.
public final class LocalDeterministicImageGenerator: IImageGenerator, @unchecked Sendable {
    public let generatorId: String
    public let displayLabel: String
    private let configured: Bool
    private let mimeType: String
    private let clock: @Sendable () -> Date

    public init(
        generatorId: String = "local-fake",
        displayLabel: String = "Local fake",
        isConfigured: Bool = true,
        mimeType: String = "image/png",
        clock: @escaping @Sendable () -> Date = { Date() }
    ) {
        self.generatorId = generatorId
        self.displayLabel = displayLabel
        self.configured = isConfigured
        self.mimeType = mimeType
        self.clock = clock
    }

    public var isConfigured: Bool { configured }
    public var statusMessage: String { configured ? "Ready · \(generatorId)" : "\(generatorId) not configured." }

    public func generate(request: ImageGenerationRequest) async throws -> [ImageArtifact] {
        if !configured { return [] }
        let count = min(max(request.count, 1), 4)
        var out: [ImageArtifact] = []
        for i in 0..<count {
            let payload = Data("\(generatorId):\(request.prompt):\(i)".utf8)
            out.append(ImageArtifact(
                generatorId: generatorId,
                prompt: request.prompt,
                mimeType: mimeType,
                url: nil,
                bytes: payload,
                generatedAtUtc: clock()))
        }
        return out
    }
}
