// HostingChatRuntime.swift
//
// Host-neutral chat runtime seam — port of CircleAI.Hosting.Chat.IChatRuntime.
// Lets a UI / harness drive the on-device engine without touching inference
// types. NeuronNode implements these over an IAIService brain.

import Foundation

/// Host-neutral chat turn. Mirrors `ChatTurn` (role / content).
public struct ChatTurn: Sendable {
    public let role: String
    public let content: String
    public init(role: String, content: String) {
        self.role = role
        self.content = content
    }
}

/// Host-neutral chat surface. Mirrors `IChatRuntime`.
public protocol IChatRuntime: AnyObject, Sendable {
    var id: String { get }
    var engineLabel: String { get }
    var isReady: Bool { get }
    var statusMessage: String { get }
    /// Streams the assistant reply chunk-by-chunk.
    func stream(_ messages: [ChatTurn]) -> AsyncThrowingStream<String, Error>
}

/// Optional KV-snapshot capability. Mirrors `IPersistableChatRuntime`.
public protocol IPersistableChatRuntime: AnyObject, Sendable {
    var sessionSnapshotPath: String? { get }
    func saveSession(path: String) async -> Bool
    func loadSession(path: String) async -> Bool
}

/// Honest "engine offline" runtime. Mirrors `NullChatRuntime`.
public final class NullChatRuntime: IChatRuntime, @unchecked Sendable {
    public init() {}

    static let offlineStatus =
        "No chat engine is wired. Add a NeuronNode (or another IChatRuntime adapter) to enable conversations."

    public var id: String { "null" }
    public var engineLabel: String { "No engine wired" }
    public var isReady: Bool { false }
    public var statusMessage: String { Self.offlineStatus }

    public func stream(_ messages: [ChatTurn]) -> AsyncThrowingStream<String, Error> {
        AsyncThrowingStream { continuation in
            continuation.yield(Self.offlineStatus)
            continuation.finish()
        }
    }
}
