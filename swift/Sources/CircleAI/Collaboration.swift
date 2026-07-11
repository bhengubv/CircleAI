// Collaboration.swift
//
// Port of CircleAI.Collaboration/ — in-memory channel / message / presence
// stores (a small team-chat board).
//   • Contracts.cs               — Channel, Message, IChannelStore, IMessageStore,
//                                  PresenceState, IPresence
//   • InMemoryCollaboration.cs   — InMemoryChannelStore, InMemoryMessageStore
//                                  (per-channel, newest-first reads), InMemoryPresence
//   • NullImplementations.cs     — Null* fail-closed backends
//
// Porting notes:
//   • `Channel` / `Message` keep their C# names (no collision in the flat module;
//     the instruction is to keep type names).
//   • `ReadAsync(channelId, limit=100)` returns newest-first up to `limit`.
//   • `ListForTeamAsync` returns channels for a team ordered by name.

import Foundation

// MARK: - Records

/// A chat channel. (C# `Channel`.)
public struct Channel: Sendable, Equatable, Codable {
    /// Channel identifier.
    public let channelId: String
    /// Display name.
    public let name: String
    /// Owning team.
    public let teamId: String

    public init(channelId: String, name: String, teamId: String) {
        self.channelId = channelId
        self.name = name
        self.teamId = teamId
    }
}

/// A chat message. (C# `Message`.)
public struct Message: Sendable, Equatable, Codable {
    /// Message identifier.
    public let messageId: String
    /// Channel this message belongs to.
    public let channelId: String
    /// Author identifier.
    public let authorId: String
    /// Message body.
    public let body: String
    /// UTC timestamp.
    public let atUtc: Date

    public init(messageId: String, channelId: String, authorId: String, body: String, atUtc: Date) {
        self.messageId = messageId
        self.channelId = channelId
        self.authorId = authorId
        self.body = body
        self.atUtc = atUtc
    }
}

/// A user's presence state. (C# `PresenceState`.)
public struct PresenceState: Sendable, Equatable, Codable {
    /// User identifier.
    public let userId: String
    /// Whether the user is online.
    public let online: Bool
    /// UTC last-seen timestamp.
    public let lastSeenUtc: Date

    public init(userId: String, online: Bool, lastSeenUtc: Date) {
        self.userId = userId
        self.online = online
        self.lastSeenUtc = lastSeenUtc
    }
}

// MARK: - Contracts

/// Stores channels. (C# `IChannelStore`.)
public protocol IChannelStore: Sendable {
    /// Backend identifier.
    var backendId: String { get }
    /// Returns the channel with `id`, or `nil`.
    func get(_ id: String) async -> Channel?
    /// Returns all channels for `teamId`, ordered by name.
    func listForTeam(_ teamId: String) async -> [Channel]
}

/// Stores messages. (C# `IMessageStore`.)
public protocol IMessageStore: Sendable {
    /// Backend identifier.
    var backendId: String { get }
    /// Posts a message and returns it.
    func post(_ msg: Message) async -> Message
    /// Returns up to `limit` most-recent messages for `channelId`, newest first.
    func read(_ channelId: String, limit: Int) async -> [Message]
}

public extension IMessageStore {
    /// Overload matching the C# default `limit = 100`.
    func read(_ channelId: String) async -> [Message] {
        await read(channelId, limit: 100)
    }
}

/// Reads presence state. (C# `IPresence`.)
public protocol IPresence: Sendable {
    /// Backend identifier.
    var backendId: String { get }
    /// Returns presence for `userId`, or `nil`.
    func get(_ userId: String) async -> PresenceState?
}

// MARK: - In-memory stores

/// In-memory channel store. (C# `InMemoryChannelStore`.)
public final class InMemoryChannelStore: IChannelStore, @unchecked Sendable {
    private let lock = NSLock()
    private var items: [String: Channel] = [:]

    public init() {}

    public var backendId: String { "in-memory" }

    /// Inserts or replaces (by channel id).
    public func upsert(_ c: Channel) {
        lock.lock(); items[c.channelId] = c; lock.unlock()
    }

    public func get(_ id: String) async -> Channel? {
        precondition(!id.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, "id required")
        lock.lock(); defer { lock.unlock() }
        return items[id]
    }

    public func listForTeam(_ teamId: String) async -> [Channel] {
        precondition(!teamId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, "teamId required")
        lock.lock(); defer { lock.unlock() }
        return items.values.filter { $0.teamId == teamId }.sorted { $0.name < $1.name }
    }
}

/// In-memory message store — messages kept per channel. (C# `InMemoryMessageStore`.)
public final class InMemoryMessageStore: IMessageStore, @unchecked Sendable {
    private let lock = NSLock()
    private var byChannel: [String: [Message]] = [:]

    public init() {}

    public var backendId: String { "in-memory" }

    public func post(_ msg: Message) async -> Message {
        precondition(!msg.channelId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, "ChannelId required")
        lock.lock(); byChannel[msg.channelId, default: []].append(msg); lock.unlock()
        return msg
    }

    public func read(_ channelId: String, limit: Int) async -> [Message] {
        precondition(!channelId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, "channelId required")
        lock.lock(); defer { lock.unlock() }
        guard let list = byChannel[channelId] else { return [] }
        return Array(list.sorted { $0.atUtc > $1.atUtc }.prefix(limit))
    }
}

/// In-memory presence store. (C# `InMemoryPresence`.)
public final class InMemoryPresence: IPresence, @unchecked Sendable {
    private let lock = NSLock()
    private var states: [String: PresenceState] = [:]

    public init() {}

    public var backendId: String { "in-memory" }

    /// Sets a user's presence.
    public func set(_ s: PresenceState) {
        lock.lock(); states[s.userId] = s; lock.unlock()
    }

    public func get(_ userId: String) async -> PresenceState? {
        precondition(!userId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, "userId required")
        lock.lock(); defer { lock.unlock() }
        return states[userId]
    }
}

// MARK: - Null implementations

/// Fail-closed channel store. (C# `NullChannelStore`.)
public final class NullChannelStore: IChannelStore, @unchecked Sendable {
    public static let instance = NullChannelStore()
    public init() {}
    public var backendId: String { "null" }
    public func get(_ id: String) async -> Channel? { nil }
    public func listForTeam(_ teamId: String) async -> [Channel] { [] }
}

/// Fail-closed message store — echoes posts, reads nothing. (C# `NullMessageStore`.)
public final class NullMessageStore: IMessageStore, @unchecked Sendable {
    public static let instance = NullMessageStore()
    public init() {}
    public var backendId: String { "null" }
    public func post(_ msg: Message) async -> Message { msg }
    public func read(_ channelId: String, limit: Int) async -> [Message] { [] }
}

/// Fail-closed presence — knows no one. (C# `NullPresence`.)
public final class NullPresence: IPresence, @unchecked Sendable {
    public static let instance = NullPresence()
    public init() {}
    public var backendId: String { "null" }
    public func get(_ userId: String) async -> PresenceState? { nil }
}
