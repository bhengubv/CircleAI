// Media.swift
//
// Port of the CircleAI.Media vertical:
//   • MediaPrimitives.cs      — MediaKind, MediaAsset, IMediaLibrary,
//                               InMemoryMediaLibrary (audio + video + image
//                               asset catalog).
//   • MediaDomainContext.cs   — domain system-prompt snippet + compliance flags.
//   • MediaCompanionAdapter.cs — an ICompanionSession decorator that prepends
//                               the media domain prompt and adds media-authoring
//                               convenience methods.
//
// NAMING: Swift flattens C# namespaces into a single `CircleAI` module. The
// MediaHub vertical (MediaHub.swift) contributes its own `IMediaLibrary` /
// `InMemoryMediaLibrary` over a different DTO (`MediaItem`), so this
// `CircleAI.Media` library keeps the plain names (`IMediaLibrary`,
// `InMemoryMediaLibrary`, over `MediaAsset`) and the MediaHub versions are the
// ones disambiguated (`IHubMediaLibrary` / `InMemoryHubMediaLibrary`). See
// MediaHub.swift for that split.

import Foundation

// =====================================================================
// MediaPrimitives.cs
// =====================================================================

/// The kind of a media asset. Port of `CircleAI.Media.MediaKind`.
///
/// C# ordinals: Audio = 0, Video = 1, Image = 2 (backed by `Int` so the
/// discriminant matches the enum's declaration order exactly).
public enum MediaKind: Int, Sendable, Codable, CaseIterable {
    case audio = 0
    case video = 1
    case image = 2
}

/// One catalogued media asset (audio / video / image). Port of the C# record
/// `CircleAI.Media.MediaAsset`.
///
/// `TimeSpan? Duration` maps to `TimeInterval?` (seconds); `long Bytes` maps to
/// `Int64`; `DateTimeOffset CreatedAtUtc` maps to `Date`.
public struct MediaAsset: Sendable, Equatable, Codable {
    public let assetId: String
    public let title: String
    public let kind: MediaKind
    public let duration: TimeInterval?
    public let bytes: Int64
    public let mime: String
    public let createdAtUtc: Date

    public init(
        assetId: String,
        title: String,
        kind: MediaKind,
        duration: TimeInterval?,
        bytes: Int64,
        mime: String,
        createdAtUtc: Date
    ) {
        self.assetId = assetId
        self.title = title
        self.kind = kind
        self.duration = duration
        self.bytes = bytes
        self.mime = mime
        self.createdAtUtc = createdAtUtc
    }
}

/// Errors raised by the media library. Mirrors the C# argument guards
/// (`ArgumentNullException` / `ArgumentException` / `ArgumentOutOfRangeException`).
public enum MediaLibraryError: Error, Equatable {
    /// An `AssetId` was null / empty / whitespace.
    case assetIdRequired
    /// `topK` was <= 0.
    case topKOutOfRange
}

/// An in-memory catalog of media assets. Port of `CircleAI.Media.IMediaLibrary`.
public protocol IMediaLibrary: AnyObject {
    /// Add (or replace) an asset keyed by its `assetId`.
    func add(_ a: MediaAsset) throws

    /// Fetch an asset by id, or nil if absent.
    func get(_ id: String) -> MediaAsset?

    /// Remove an asset by id. Returns true if it was present.
    @discardableResult
    func remove(_ id: String) -> Bool

    /// Number of assets currently catalogued.
    var count: Int { get }

    /// Total on-disk footprint of every catalogued asset, in bytes.
    var totalBytes: Int64 { get }

    /// All assets of a given kind, newest-first (by `createdAtUtc`).
    func listByKind(_ kind: MediaKind) -> [MediaAsset]

    /// Assets whose MIME type starts with a given prefix (e.g. "image/",
    /// "audio/"), matched case-insensitively and returned newest-first. Empty
    /// prefix yields nothing.
    func byMime(_ mimePrefix: String) -> [MediaAsset]

    /// Title-substring search (case-insensitive), newest-first, capped to `topK`.
    func search(_ q: String, topK: Int) throws -> [MediaAsset]
}

public extension IMediaLibrary {
    /// Overload matching the C# default `topK = 20`.
    func search(_ q: String) throws -> [MediaAsset] {
        try search(q, topK: 20)
    }
}

/// Dictionary-backed `IMediaLibrary`. Port of
/// `CircleAI.Media.InMemoryMediaLibrary`. The C# `ConcurrentDictionary`
/// (ordinal string comparer) maps to a plain dictionary guarded by an `NSLock`
/// confined to the private sync helpers.
public final class InMemoryMediaLibrary: IMediaLibrary, @unchecked Sendable {
    private let lock = NSLock()
    private var items: [String: MediaAsset] = [:]

    public init() {}

    public func add(_ a: MediaAsset) throws {
        // ArgumentNullException.ThrowIfNull(a) is implicit — `a` is a value type.
        if a.assetId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            throw MediaLibraryError.assetIdRequired
        }
        setItem(a)
    }

    public func get(_ id: String) -> MediaAsset? {
        getItem(id)
    }

    @discardableResult
    public func remove(_ id: String) -> Bool {
        // C#: `!string.IsNullOrEmpty(id) && _items.TryRemove(id, out _)`.
        if id.isEmpty { return false }
        lock.lock(); defer { lock.unlock() }
        return items.removeValue(forKey: id) != nil
    }

    public var count: Int {
        lock.lock(); defer { lock.unlock() }
        return items.count
    }

    public var totalBytes: Int64 {
        // C#: `_items.Values.Sum(a => a.Bytes)`.
        snapshotValues().reduce(0) { $0 + $1.bytes }
    }

    public func byMime(_ mimePrefix: String) -> [MediaAsset] {
        // C#: empty prefix yields nothing; else StartsWith(OrdinalIgnoreCase),
        // OrderByDescending(CreatedAtUtc).
        if mimePrefix.isEmpty { return [] }
        return snapshotValues()
            .filter { $0.mime.range(of: mimePrefix, options: [.caseInsensitive, .anchored]) != nil }
            .sorted { $0.createdAtUtc > $1.createdAtUtc }
    }

    public func listByKind(_ kind: MediaKind) -> [MediaAsset] {
        snapshotValues()
            .filter { $0.kind == kind }
            .sorted { $0.createdAtUtc > $1.createdAtUtc } // OrderByDescending(CreatedAtUtc)
    }

    public func search(_ q: String, topK: Int = 20) throws -> [MediaAsset] {
        // C#: `if (q is null) throw` — Swift `String` is non-optional so the null
        // guard is unrepresentable; the topK guard is preserved.
        if topK <= 0 { throw MediaLibraryError.topKOutOfRange }

        let hits = snapshotValues()
            .filter { $0.title.range(of: q, options: .caseInsensitive) != nil }
            .sorted { $0.createdAtUtc > $1.createdAtUtc } // OrderByDescending(CreatedAtUtc)
        return Array(hits.prefix(topK))
    }

    // ── sync helpers (lock confined here; never held across an await) ──────────

    private func setItem(_ a: MediaAsset) {
        lock.lock(); items[a.assetId] = a; lock.unlock()
    }

    private func getItem(_ id: String) -> MediaAsset? {
        lock.lock(); defer { lock.unlock() }
        return items[id]
    }

    private func snapshotValues() -> [MediaAsset] {
        lock.lock(); defer { lock.unlock() }
        return Array(items.values)
    }
}

// =====================================================================
// MediaDomainContext.cs
// =====================================================================

/// Static media-vertical domain context: the system-prompt snippet, compliance
/// flags, and suggested tools. Port of `CircleAI.Media.MediaDomainContext`.
public enum MediaDomainContext {
    /// Domain system-prompt prefix injected ahead of user messages.
    public static let systemPromptSnippet: String =
        "[DOMAIN: Media] Expert media and content production assistant. Help with editorial calendars, content briefs, video production schedules, audience analytics interpretation, social media strategy, and IP rights management. Apply data-driven creative strategy. Compliance: ICASA, BCCSA, Copyright Act 98/1978, POPIA."

    /// Regulatory compliance flags relevant to the media domain.
    public static let complianceFlags: [String] =
        ["ICASA", "BCCSA", "Copyright_Act_98_1978", "POPIA"]

    /// Tool ids suggested for media-vertical sessions.
    public static let suggestedTools: [String] =
        ["content_planner", "analytics", "video_editor", "social_media_api"]
}

// =====================================================================
// MediaCompanionAdapter.cs
// =====================================================================

/// An `ICompanionSession` decorator that prepends the media domain system prompt
/// to every conversational call and adds media-authoring convenience methods
/// (content briefs, audience analysis, press releases, thumbnails, narrative
/// structure, captions). Port of `CircleAI.Media.MediaCompanionAdapter`.
///
/// The inner session's identity/context/feedback surface is forwarded verbatim.
/// C# exposes `ProactiveMessageReady` as an event that add/remove-forwards to
/// the inner session; the Swift `ICompanionSession` surface models proactive
/// events as the `proactiveEvents` async stream, so this adapter forwards that
/// stream straight through. (The C# `DisposeAsync` forwarding has no analogue
/// because the Swift `ICompanionSession` protocol does not declare disposal.)
public final class MediaCompanionAdapter: ICompanionSession, @unchecked Sendable {
    private let inner: ICompanionSession

    public init(_ inner: ICompanionSession) {
        self.inner = inner
    }

    // ── Forwarded identity / surface ──────────────────────────────────────────

    public var sessionId: String { inner.sessionId }
    public var identityId: String { inner.identityId }
    public var interface: InterfaceKind { inner.interface }
    public var history: [CompanionTurn] { inner.history }

    public func getContext() -> CompanionContext { inner.getContext() }

    public func refreshContext() async throws { try await inner.refreshContext() }

    public func signalFeedback(positive: Bool, note: String?) async throws {
        try await inner.signalFeedback(positive: positive, note: note)
    }

    /// Forwards the wrapped session's proactive-event stream (mirrors the C#
    /// `ProactiveMessageReady` add/remove chaining onto the inner session).
    public var proactiveEvents: AsyncStream<CompanionProactiveEvent> { inner.proactiveEvents }

    // ── Domain-prefixed conversation ──────────────────────────────────────────

    public func send(_ message: String) async throws -> String {
        try await inner.send(enrich(message))
    }

    public func stream(_ message: String) -> AsyncStream<String> {
        inner.stream(enrich(message))
    }

    public func agent(_ instruction: String) async throws -> String {
        try await inner.agent(enrich(instruction))
    }

    /// Prepend the media domain system prompt to a message. Port of the private
    /// `E(m)` helper (`$"{SystemPromptSnippet}\n\n{m}"`).
    private func enrich(_ m: String) -> String {
        "\(MediaDomainContext.systemPromptSnippet)\n\n\(m)"
    }

    // ── Media-authoring helpers (route through inner.agent) ───────────────────

    /// Create a detailed content brief for a platform.
    public func createContentBrief(topic: String, audience: String, platform: String) async throws -> String {
        try await inner.agent(
            "Create a detailed content brief for \(platform): Topic: \(topic). Target audience: \(audience). Include angle, key messages, SEO keywords, call to action, and production notes.")
    }

    /// Analyse audience / analytics data and recommend content strategy.
    public func analyseAudienceData(_ analyticsData: String) async throws -> String {
        try await inner.agent(
            "Analyse this audience/analytics data and provide actionable content strategy recommendations:\n\(analyticsData)")
    }

    /// Draft a press release.
    public func draftPressRelease(announcement: String, audience: String) async throws -> String {
        try await inner.agent(
            "Draft a press release on: \(announcement) for \(audience). AP style, inverted pyramid, quote from leadership, boilerplate.")
    }

    /// Suggest 3 thumbnail concepts for a video.
    public func suggestThumbnailConcepts(videoTopic: String, channelStyle: String) async throws -> String {
        try await inner.agent(
            "Suggest 3 thumbnail concepts for a video on '\(videoTopic)' in \(channelStyle) style. Hook, composition, text.")
    }

    /// Structure a timed narrative.
    public func structureNarrative(topic: String, format: String, durationMinutes: Int) async throws -> String {
        try await inner.agent(
            "Structure a \(durationMinutes)-min \(format) on '\(topic)'. Hook, beats, payoff, CTA.")
    }

    /// Write a platform caption.
    public func writeCaption(mediaDescription: String, platform: String, voice: String) async throws -> String {
        try await inner.agent(
            "Write a \(platform) caption for: \(mediaDescription). Voice: \(voice). Optimise for platform's algorithm + accessibility.")
    }
}
