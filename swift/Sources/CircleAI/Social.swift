// Social.swift
//
// Port of the Social vertical from src/CircleAI.Social/SocialPrimitives.cs and
// the static domain-context constants from SocialDomainContext.cs:
//   • SocialPost, Reaction, Follow — domain records
//   • ISocialBoard                 — posts, reactions, follows, feed
//   • InMemorySocialBoard          — deterministic in-memory impl
//   • SocialDomainContext          — system-prompt snippet + flags
//
// The Companion-facing wrapper (SocialCompanionAdapter) is an ICompanionSession
// decorator that prefixes the social domain prompt.
//
// Porting notes:
//   • `DateTimeOffset` → `Date`.
//   • `ReactionCount(postId, kind)` counts reactions matching post + kind
//     (case-insensitive).
//   • `Follow` rejects self-follows with `.cannotFollowSelf`. `Unfollow` removes
//     all matching edges.
//   • `FeedFor(userId, limit)` returns posts authored by anyone the user follows,
//     ordered descending by AtUtc, take limit; `limit <= 0` throws `.invalidLimit`.
//   • `Followers` returns follower ids for the user. All state guarded by a
//     single `NSLock`.

import Foundation

// MARK: - Records

/// A social post.
public struct SocialPost: Sendable, Equatable, Codable {
    public let postId: String
    public let authorId: String
    public let body: String
    public let atUtc: Date
    public let tags: [String]

    public init(postId: String, authorId: String, body: String, atUtc: Date, tags: [String]) {
        self.postId = postId
        self.authorId = authorId
        self.body = body
        self.atUtc = atUtc
        self.tags = tags
    }
}

/// A reaction to a post.
public struct Reaction: Sendable, Equatable, Codable {
    public let postId: String
    public let userId: String
    public let kind: String
    public let atUtc: Date

    public init(postId: String, userId: String, kind: String, atUtc: Date) {
        self.postId = postId
        self.userId = userId
        self.kind = kind
        self.atUtc = atUtc
    }
}

/// A follow edge.
public struct Follow: Sendable, Equatable, Codable {
    public let followerId: String
    public let followeeId: String
    public let atUtc: Date

    public init(followerId: String, followeeId: String, atUtc: Date) {
        self.followerId = followerId
        self.followeeId = followeeId
        self.atUtc = atUtc
    }
}

// MARK: - Errors

public enum SocialError: Error, Equatable, CustomStringConvertible {
    case cannotFollowSelf
    case invalidLimit

    public var description: String {
        switch self {
        case .cannotFollowSelf: return "Cannot follow yourself."
        case .invalidLimit: return "limit must be positive"
        }
    }
}

// MARK: - Contract

/// Posts, reactions, follows, and a simple feed for the social vertical.
public protocol ISocialBoard: AnyObject, Sendable {
    func post(_ p: SocialPost)
    func getPost(_ id: String) -> SocialPost?
    func react(_ r: Reaction)
    func reactionCount(postId: String, kind: String) -> Int
    func follow(_ f: Follow) throws
    func unfollow(followerId: String, followeeId: String)
    func feedFor(userId: String, limit: Int) throws -> [SocialPost]
    func followers(userId: String) -> [String]
}

public extension ISocialBoard {
    /// Convenience overload mirroring the C# default `limit = 20`.
    func feedFor(userId: String) throws -> [SocialPost] { try feedFor(userId: userId, limit: 20) }
}

// MARK: - InMemorySocialBoard

/// Deterministic in-memory `ISocialBoard`. All state guarded by a single `NSLock`.
public final class InMemorySocialBoard: ISocialBoard, @unchecked Sendable {
    private let lock = NSLock()
    private var posts: [String: SocialPost] = [:]
    private var reacts: [Reaction] = []
    private var follows: [Follow] = []

    public init() {}

    public func post(_ p: SocialPost) {
        lock.lock(); defer { lock.unlock() }
        posts[p.postId] = p
    }

    public func getPost(_ id: String) -> SocialPost? {
        lock.lock(); defer { lock.unlock() }
        return posts[id]
    }

    public func react(_ r: Reaction) {
        lock.lock(); defer { lock.unlock() }
        reacts.append(r)
    }

    public func reactionCount(postId: String, kind: String) -> Int {
        lock.lock(); defer { lock.unlock() }
        return reacts.filter { $0.postId == postId && $0.kind.caseInsensitiveCompare(kind) == .orderedSame }.count
    }

    public func follow(_ f: Follow) throws {
        if f.followerId == f.followeeId { throw SocialError.cannotFollowSelf }
        lock.lock(); defer { lock.unlock() }
        follows.append(f)
    }

    public func unfollow(followerId: String, followeeId: String) {
        lock.lock(); defer { lock.unlock() }
        follows.removeAll { $0.followerId == followerId && $0.followeeId == followeeId }
    }

    public func feedFor(userId: String, limit: Int = 20) throws -> [SocialPost] {
        if limit <= 0 { throw SocialError.invalidLimit }
        lock.lock(); defer { lock.unlock() }
        let following = Set(follows.filter { $0.followerId == userId }.map { $0.followeeId })
        return Array(posts.values.filter { following.contains($0.authorId) }.sorted { $0.atUtc > $1.atUtc }.prefix(limit))
    }

    public func followers(userId: String) -> [String] {
        lock.lock(); defer { lock.unlock() }
        return follows.filter { $0.followeeId == userId }.map { $0.followerId }
    }
}

// MARK: - SocialDomainContext

/// Static domain-context constants for the social vertical.
public enum SocialDomainContext {
    public static let systemPromptSnippet = "[DOMAIN: Social] Expert social media and community management assistant. Help with platform-specific content creation (LinkedIn, Instagram, TikTok, X, Facebook), engagement strategy, hashtag research, influencer brief writing, community moderation guidelines, and social analytics. Apply scroll-stopping creative principles. Compliance: POPIA, ASA Advertising Code, platform community standards."
    public static let complianceFlags: [String] = ["POPIA", "ASA_Advertising_Code", "Platform_Community_Standards"]
    public static let suggestedTools: [String] = ["social_media_api", "analytics", "content_planner", "image_tools"]
}

// MARK: - SocialCompanionAdapter

/// An `ICompanionSession` decorator that prepends the social domain system
/// prompt to every conversational call and adds social-media helper methods.
/// Port of `CircleAI.Social.SocialCompanionAdapter`. Identity/context/feedback
/// are forwarded to the inner session; proactive events forward through the
/// inner session's `proactiveEvents` stream (the Swift protocol has no disposal).
public final class SocialCompanionAdapter: ICompanionSession, @unchecked Sendable {
    private let inner: ICompanionSession

    public init(_ inner: ICompanionSession) {
        self.inner = inner
    }

    public var sessionId: String { inner.sessionId }
    public var identityId: String { inner.identityId }
    public var interface: InterfaceKind { inner.interface }
    public var history: [CompanionTurn] { inner.history }

    public func getContext() -> CompanionContext { inner.getContext() }
    public func refreshContext() async throws { try await inner.refreshContext() }
    public func signalFeedback(positive: Bool, note: String?) async throws {
        try await inner.signalFeedback(positive: positive, note: note)
    }
    public var proactiveEvents: AsyncStream<CompanionProactiveEvent> { inner.proactiveEvents }

    public func send(_ message: String) async throws -> String { try await inner.send(enrich(message)) }
    public func stream(_ message: String) -> AsyncStream<String> { inner.stream(enrich(message)) }
    public func agent(_ instruction: String) async throws -> String { try await inner.agent(enrich(instruction)) }

    private func enrich(_ m: String) -> String { "\(SocialDomainContext.systemPromptSnippet)\n\n\(m)" }

    // ── Social helpers ────────────────────────────────────────────────────────

    /// Write an engaging post (C# `WritePostAsync`).
    public func writePost(platform: String, message: String, tone: String) async throws -> String {
        try await inner.agent(
            "Write an engaging \(platform) post. Core message: \(message). Tone: \(tone). Include relevant hashtags, call to action, and emoji where appropriate for the platform.")
    }

    /// Plan a content calendar (C# `PlanContentCalendarAsync`).
    public func planContentCalendar(brand: String, month: String, goals: String) async throws -> String {
        try await inner.agent(
            "Plan a social media content calendar for \(brand) in \(month). Goals: \(goals). Include content mix, posting frequency, themes, and key dates.")
    }

    /// Draft a post in a voice (C# `DraftPostAsync`).
    public func draftPost(topic: String, platform: String, voice: String) async throws -> String {
        try await inner.agent(
            "Draft a \(platform) post on '\(topic)' in \(voice) voice. Hook, payload, CTA, platform-appropriate length.")
    }

    /// Analyse engagement vs baseline (C# `AnalyseEngagementAsync`).
    public func analyseEngagement(postPerformance: String, baseline: String) async throws -> String {
        try await inner.agent(
            "Analyse post performance: \(postPerformance) vs baseline: \(baseline). Why it over/under-performed + what to try next.")
    }

    /// Respond to a public critic (C# `ResponseToCriticAsync`).
    public func responseToCritic(critique: String, ourPosition: String) async throws -> String {
        try await inner.agent(
            "Respond to public critique: \(critique). Our position: \(ourPosition). De-escalate, acknowledge, offer path forward.")
    }

    /// Design a content series (C# `DesignContentSeriesAsync`).
    public func designContentSeries(theme: String, episodeCount: Int, platform: String) async throws -> String {
        try await inner.agent(
            "Design a \(episodeCount)-episode content series on '\(theme)' for \(platform). Per-episode hook + cumulative arc.")
    }
}
