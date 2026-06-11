// ModelsV15.swift
//
// 1.5.0 parity additions to the models layer. Kept in its own file so
// the existing Models.swift API surface stays byte-stable.

import Foundation

/// Why a generation call stopped emitting tokens.
public enum FinishReason: Int, Sendable {
    case stop = 0
    case length = 1
    case cancelled = 2
    case error = 3
    case unknown = 4
}

/// Structured response from `IChatGenerator.generateResponse`.
/// Carries text + token counts + latency + finish reason.
///
/// `reasoningContent` is the chain-of-thought emitted by reasoning models
/// (Qwen3 / DeepSeek-R1 / o1) inside `<think>…</think>`. `nil` when the
/// model emitted no reasoning or `GenerationOptions.includeReasoning` was
/// `false`. Tags themselves are stripped — only the text content.
public struct ChatResponse: Sendable, Equatable {
    public let text: String
    public let tokensIn: Int
    public let tokensOut: Int
    public let latencyMs: Double
    public let finishReason: FinishReason
    public let reasoningContent: String?

    public init(
        text: String,
        tokensIn: Int,
        tokensOut: Int,
        latencyMs: Double,
        finishReason: FinishReason = .stop,
        reasoningContent: String? = nil
    ) {
        self.text = text
        self.tokensIn = tokensIn
        self.tokensOut = tokensOut
        self.latencyMs = latencyMs
        self.finishReason = finishReason
        self.reasoningContent = reasoningContent
    }
}

/// Kind of fragment a streaming generator emits.
public enum ChatFragmentKind: Int, Sendable {
    /// Part of the user-facing answer (goes into `content`).
    case content = 0
    /// Part of the model's reasoning trace (goes into `reasoning_content`).
    case reasoning = 1
}

/// A single fragment yielded by `IChatGenerator.streamFragments`.
/// Tagged so the caller can route the model's `<think>` block into a
/// separate `reasoning_content` field (o1 / DeepSeek style).
public struct ChatFragment: Sendable, Equatable {
    public let kind: ChatFragmentKind
    public let text: String

    public init(kind: ChatFragmentKind, text: String) {
        self.kind = kind
        self.text = text
    }
}

/// One file inside a model bundle.
public struct BundleFile: Codable, Equatable, Sendable {
    public let name: String
    public let sha256: String
    public let sizeBytes: Int64

    public init(name: String, sha256: String, sizeBytes: Int64) {
        self.name = name
        self.sha256 = sha256
        self.sizeBytes = sizeBytes
    }
}

/// On-disk record of what was installed for a given model.
public struct InstalledManifest: Codable, Equatable, Sendable {
    public let modelId: String
    public let version: String
    public let repo: String?
    public let totalBytes: Int64
    public let files: [BundleFile]
    public let installedAtUtc: Date

    public init(
        modelId: String,
        version: String,
        repo: String?,
        totalBytes: Int64,
        files: [BundleFile],
        installedAtUtc: Date
    ) {
        self.modelId = modelId
        self.version = version
        self.repo = repo
        self.totalBytes = totalBytes
        self.files = files
        self.installedAtUtc = installedAtUtc
    }

    enum CodingKeys: String, CodingKey {
        case modelId = "model_id"
        case version
        case repo
        case totalBytes = "total_bytes"
        case files
        case installedAtUtc = "installed_at_utc"
    }
}

/// Why `checkForUpgrades` flagged a model.
public enum UpgradeReason: Int, Sendable {
    case versionChanged = 0
    case shaChanged = 1
    case both = 2
    case unknown = 3
}

/// One detected upgrade for a locally-installed model.
public struct UpgradeInfo: Sendable, Equatable {
    public let modelId: String
    public let installedVersion: String?
    public let availableVersion: String
    public let reason: UpgradeReason
    public let estimatedDownloadBytes: Int64
    public let detectedAt: Date

    public init(
        modelId: String,
        installedVersion: String?,
        availableVersion: String,
        reason: UpgradeReason,
        estimatedDownloadBytes: Int64,
        detectedAt: Date
    ) {
        self.modelId = modelId
        self.installedVersion = installedVersion
        self.availableVersion = availableVersion
        self.reason = reason
        self.estimatedDownloadBytes = estimatedDownloadBytes
        self.detectedAt = detectedAt
    }
}

/// Vision-capable chat message. Existing ChatMessage stays byte-stable;
/// consumers needing image attachments use this variant.
public struct VisionChatMessage: Sendable, Equatable {
    public let role: String
    public let content: String
    public let imageBytes: Data?

    public init(role: String, content: String, imageBytes: Data? = nil) {
        self.role = role
        self.content = content
        self.imageBytes = imageBytes
    }
}
