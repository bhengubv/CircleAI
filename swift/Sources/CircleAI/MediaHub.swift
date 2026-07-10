// MediaHub.swift
//
// Port of the CircleAI.MediaHub module:
//   • Contracts.cs           — MediaItem, PlaybackPosition, IMediaLibrary,
//                              ISyncedPlayback (media-server contracts).
//   • InMemoryMediaHub.cs     — InMemoryMediaLibrary (title-substring search) +
//                              InMemorySyncedPlayback (subscribe/broadcast
//                              pub-sub for synced playback positions).
//   • NullImplementations.cs  — NullMediaLibrary, NullSyncedPlayback.
//
// NAMING: the flat `CircleAI` module already has a `CircleAI.Media.IMediaLibrary`
// / `InMemoryMediaLibrary` (over `MediaAsset`, in Media.swift). The MediaHub
// library contracts are a *different* interface over a *different* DTO
// (`MediaItem`), so — following the SpeechContracts precedent for cross-namespace
// clashes — the MediaHub library types are disambiguated with a `Hub` prefix:
//   MediaHub.IMediaLibrary        -> IHubMediaLibrary
//   MediaHub.InMemoryMediaLibrary -> InMemoryHubMediaLibrary
//   MediaHub.NullMediaLibrary     -> NullHubMediaLibrary
// The playback types (`ISyncedPlayback`, `InMemorySyncedPlayback`,
// `NullSyncedPlayback`, `MediaItem`, `PlaybackPosition`) are unique and keep
// their names.

import Foundation

// =====================================================================
// Contracts.cs — DTOs
// =====================================================================

/// One item in a media library. Port of the C# record
/// `CircleAI.MediaHub.MediaItem`.
///
/// `TimeSpan Duration` maps to `TimeInterval` (seconds). `Kind` is a free-form
/// string here (unlike `CircleAI.Media.MediaKind`), matching the C# record's
/// `string Kind`.
public struct MediaItem: Sendable, Equatable, Codable {
    public let itemId: String
    public let title: String
    public let kind: String
    public let duration: TimeInterval
    public let mimeType: String

    public init(
        itemId: String,
        title: String,
        kind: String,
        duration: TimeInterval,
        mimeType: String
    ) {
        self.itemId = itemId
        self.title = title
        self.kind = kind
        self.duration = duration
        self.mimeType = mimeType
    }
}

/// A broadcast playback position for a synced-watch session. Port of the C#
/// record `CircleAI.MediaHub.PlaybackPosition`. `DateTimeOffset AtUtc` maps to
/// `Date`.
public struct PlaybackPosition: Sendable, Equatable, Codable {
    public let itemId: String
    public let position: TimeInterval
    public let atUtc: Date

    public init(itemId: String, position: TimeInterval, atUtc: Date) {
        self.itemId = itemId
        self.position = position
        self.atUtc = atUtc
    }
}

// =====================================================================
// Contracts.cs — errors
// =====================================================================

/// Errors raised by the MediaHub contracts. Mirrors the C# argument guards.
public enum MediaHubError: Error, Equatable {
    /// An `id` was null / empty / whitespace.
    case idRequired
    /// `topK` was <= 0.
    case topKOutOfRange
    /// A `sessionId` was null / empty / whitespace.
    case sessionIdRequired
    /// A `userId` was null / empty / whitespace.
    case userIdRequired
}

// =====================================================================
// Contracts.cs — IMediaLibrary (MediaHub)
// =====================================================================

/// Media library backend. Port of `CircleAI.MediaHub.IMediaLibrary` (renamed
/// `IHubMediaLibrary` to avoid the flat-module clash with
/// `CircleAI.Media.IMediaLibrary`).
public protocol IHubMediaLibrary: AnyObject, Sendable {
    /// Backend self-identification — "in-memory" / "null".
    var backendId: String { get }

    /// Fetch an item by id, or nil if absent. Throws on empty `id`.
    func get(_ id: String) async throws -> MediaItem?

    /// Title-substring search (case-insensitive), title-ascending, capped `topK`.
    func search(_ query: String, topK: Int) async throws -> [MediaItem]
}

public extension IHubMediaLibrary {
    /// Overload matching the C# default `topK = 20`.
    func search(_ query: String) async throws -> [MediaItem] {
        try await search(query, topK: 20)
    }
}

// =====================================================================
// Contracts.cs — ISyncedPlayback
// =====================================================================

/// Synced-watch playback coordinator: users join a session and broadcast their
/// current position; subscribers receive every broadcast. Port of
/// `CircleAI.MediaHub.ISyncedPlayback`.
public protocol ISyncedPlayback: AnyObject, Sendable {
    /// Backend self-identification — "in-memory" / "null".
    var backendId: String { get }

    /// Add `userId` to `sessionId`'s member set (creating the session).
    func joinSession(sessionId: String, userId: String) async throws

    /// Broadcast `pos` to every subscriber of `sessionId`. Subscriber failures
    /// are swallowed (logged) so one bad handler cannot break the fan-out.
    func broadcastPosition(sessionId: String, pos: PlaybackPosition) async throws

    /// Subscribe to `sessionId`'s position broadcasts. Returns a handle; call
    /// `dispose()` on it to unsubscribe (mirrors the C# `IDisposable`).
    func subscribe(sessionId: String, handler: @escaping @Sendable (PlaybackPosition) async -> Void) -> any Disposable
}

// =====================================================================
// InMemoryMediaHub.cs — InMemoryMediaLibrary
// =====================================================================

/// Title-substring searchable media library backed by a dictionary. Port of
/// `CircleAI.MediaHub.InMemoryMediaLibrary` (renamed `InMemoryHubMediaLibrary`).
/// The C# `ConcurrentDictionary` (ordinal comparer) maps to a plain dictionary
/// guarded by an `NSLock` confined to the private sync helpers.
public final class InMemoryHubMediaLibrary: IHubMediaLibrary, @unchecked Sendable {
    private let lock = NSLock()
    private var items: [String: MediaItem] = [:]

    public init() {}

    public var backendId: String { "in-memory" }

    /// Seed the library with an item (keyed by `itemId`). Port of the C# `Add`.
    public func add(_ item: MediaItem) {
        // ArgumentNullException.ThrowIfNull(item) is implicit — value type.
        setItem(item)
    }

    public func get(_ id: String) async throws -> MediaItem? {
        if id.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            throw MediaHubError.idRequired
        }
        return getItem(id)
    }

    public func search(_ query: String, topK: Int = 20) async throws -> [MediaItem] {
        // C#: `if (query is null) throw` — Swift `String` is non-optional, so the
        // null guard is unrepresentable; the topK guard is preserved.
        if topK <= 0 { throw MediaHubError.topKOutOfRange }

        let hits = snapshotValues()
            .filter { $0.title.range(of: query, options: .caseInsensitive) != nil }
            // OrderBy(Title, OrdinalIgnoreCase): case-insensitive ascending.
            .sorted { lhs, rhs in
                lhs.title.lowercased() < rhs.title.lowercased()
            }
        return Array(hits.prefix(topK))
    }

    // ── sync helpers ──────────────────────────────────────────────────────────

    private func setItem(_ item: MediaItem) {
        lock.lock(); items[item.itemId] = item; lock.unlock()
    }

    private func getItem(_ id: String) -> MediaItem? {
        lock.lock(); defer { lock.unlock() }
        return items[id]
    }

    private func snapshotValues() -> [MediaItem] {
        lock.lock(); defer { lock.unlock() }
        return Array(items.values)
    }
}

// =====================================================================
// InMemoryMediaHub.cs — InMemorySyncedPlayback
// =====================================================================

/// In-memory broadcast/subscribe playback sync. Port of
/// `CircleAI.MediaHub.InMemorySyncedPlayback`.
///
/// Concurrency (mirrors the C# `lock (state)` discipline, adapted to Swift's
/// async model): the subscriber list is snapshotted under the lock and the lock
/// is released *before* any handler is awaited, so a handler that re-enters the
/// service (e.g. subscribes/unsubscribes, or broadcasts again) can never
/// self-deadlock the non-reentrant `NSLock`.
public final class InMemorySyncedPlayback: ISyncedPlayback, @unchecked Sendable {
    /// A subscriber handler boxed so identity-based removal works (closures are
    /// not `Equatable`); mirrors the C# `List<Func<...>>` element + the
    /// `SubscriptionToken` that removes the exact instance.
    private final class Subscriber {
        let fn: @Sendable (PlaybackPosition) async -> Void
        init(_ fn: @escaping @Sendable (PlaybackPosition) async -> Void) { self.fn = fn }
    }

    /// Per-session state: joined members + active subscribers. Port of the C#
    /// private `SessionState` record.
    private final class SessionState {
        var members: Set<String> = []
        var subscribers: [Subscriber] = []
    }

    private let lock = NSLock()
    private var sessions: [String: SessionState] = [:]

    public init() {}

    public var backendId: String { "in-memory" }

    public func joinSession(sessionId: String, userId: String) async throws {
        if sessionId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            throw MediaHubError.sessionIdRequired
        }
        if userId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            throw MediaHubError.userIdRequired
        }
        lock.lock()
        let state = getOrAddLocked(sessionId)
        state.members.insert(userId)
        lock.unlock()
    }

    public func broadcastPosition(sessionId: String, pos: PlaybackPosition) async throws {
        // ArgumentNullException.ThrowIfNull(pos) is implicit — value type.
        if sessionId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            throw MediaHubError.sessionIdRequired
        }

        // Snapshot subscribers under the lock, then RELEASE before awaiting any
        // handler (the C# `lock (state) { snapshot = ...ToArray(); }` then the
        // foreach-await outside the lock).
        lock.lock()
        guard let state = sessions[sessionId] else { lock.unlock(); return }
        let snapshot = state.subscribers
        lock.unlock()

        for sub in snapshot {
            // C# swallows per-subscriber exceptions (Debug.WriteLine). Swift
            // closures here are non-throwing, so there is nothing to catch; the
            // fan-out simply proceeds to the next subscriber.
            await sub.fn(pos)
        }
    }

    public func subscribe(sessionId: String, handler: @escaping @Sendable (PlaybackPosition) async -> Void) -> any Disposable {
        // C# throws ArgumentException on empty sessionId here too. `subscribe`
        // is non-throwing in the protocol (matching the C# `IDisposable`
        // return), so an empty sessionId yields a no-op handle rather than
        // registering an unreachable subscriber — the same net effect as the
        // C# guard (no broadcast can ever target an empty-id session).
        if sessionId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            return EmptyDisposable()
        }
        let sub = Subscriber(handler)
        lock.lock()
        let state = getOrAddLocked(sessionId)
        state.subscribers.append(sub)
        lock.unlock()
        return SubscriptionToken(owner: self, sessionId: sessionId, sub: sub)
    }

    // ── internals ─────────────────────────────────────────────────────────────

    /// Get-or-create the session state. MUST be called with `lock` held.
    private func getOrAddLocked(_ sessionId: String) -> SessionState {
        if let existing = sessions[sessionId] { return existing }
        let created = SessionState()
        sessions[sessionId] = created
        return created
    }

    /// Remove one subscriber by identity. Port of the C# `SubscriptionToken.Dispose`.
    fileprivate func removeSubscriber(sessionId: String, sub: Subscriber) {
        lock.lock()
        if let state = sessions[sessionId] {
            state.subscribers.removeAll { $0 === sub }
        }
        lock.unlock()
    }

    /// Handle returned by `subscribe`; disposing removes the exact subscriber
    /// instance. Port of the C# private `SubscriptionToken`. Idempotent.
    private final class SubscriptionToken: Disposable, @unchecked Sendable {
        private let owner: InMemorySyncedPlayback
        private let sessionId: String
        private let sub: Subscriber
        private let lock = NSLock()
        private var disposed = false

        init(owner: InMemorySyncedPlayback, sessionId: String, sub: Subscriber) {
            self.owner = owner
            self.sessionId = sessionId
            self.sub = sub
        }

        func dispose() {
            lock.lock()
            if disposed { lock.unlock(); return }
            disposed = true
            lock.unlock()
            owner.removeSubscriber(sessionId: sessionId, sub: sub)
        }
    }
}

// =====================================================================
// NullImplementations.cs
// =====================================================================

/// A media library that holds nothing and returns empty. Port of
/// `CircleAI.MediaHub.NullMediaLibrary` (renamed `NullHubMediaLibrary`).
public final class NullHubMediaLibrary: IHubMediaLibrary, @unchecked Sendable {
    public static let instance = NullHubMediaLibrary()
    public init() {}
    public var backendId: String { "null" }

    public func get(_ id: String) async throws -> MediaItem? { nil }

    public func search(_ query: String, topK: Int = 20) async throws -> [MediaItem] { [] }
}

/// A synced-playback backend that accepts joins/broadcasts but does nothing and
/// never delivers. Port of `CircleAI.MediaHub.NullSyncedPlayback`.
public final class NullSyncedPlayback: ISyncedPlayback, @unchecked Sendable {
    public static let instance = NullSyncedPlayback()
    public init() {}
    public var backendId: String { "null" }

    public func joinSession(sessionId: String, userId: String) async throws {}

    public func broadcastPosition(sessionId: String, pos: PlaybackPosition) async throws {}

    public func subscribe(sessionId: String, handler: @escaping @Sendable (PlaybackPosition) async -> Void) -> any Disposable {
        EmptyDisposable()
    }
}
