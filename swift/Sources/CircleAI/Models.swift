// Models.swift
// Shared primitive types for CircleAI Swift SDK.
// ChatMessage and DownloadProgress live here because they span multiple modules.
// All other domain types live in their respective module files.

import Foundation

// MARK: - ChatMessage

/// A single message in a chat conversation.
/// Roles: "system", "user", "assistant".
public struct ChatMessage: Sendable {
    /// Stable unique identifier for this message.
    public let id: String

    /// Role of the author. One of "system", "user", or "assistant".
    public let role: String

    /// Plain-text (or markdown) content of the message.
    public let content: String

    /// UTC time when this message was created.
    public let createdAt: Date

    public init(
        id: String = UUID().uuidString,
        role: String,
        content: String,
        createdAt: Date = Date()
    ) {
        self.id = id
        self.role = role
        self.content = content
        self.createdAt = createdAt
    }
}

// MARK: - DownloadProgress

/// Progress snapshot for a model-file download.
public struct DownloadProgress: Sendable {
    /// Total bytes to download. 0 when the server did not report Content-Length.
    public let totalBytes: Int64

    /// Bytes received so far.
    public let downloadedBytes: Int64

    /// The filename (last path component) being downloaded.
    public let filename: String

    /// Fraction downloaded in [0.0, 1.0]. Returns 0.0 when totalBytes == 0.
    public var fractionComplete: Double {
        guard totalBytes > 0 else { return 0.0 }
        return Double(downloadedBytes) / Double(totalBytes)
    }

    public init(totalBytes: Int64, downloadedBytes: Int64, filename: String) {
        self.totalBytes = totalBytes
        self.downloadedBytes = downloadedBytes
        self.filename = filename
    }
}
