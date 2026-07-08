// Rag.swift
// Retrieval-augmented context assembly. Ported from CircleAI.Memory (C#),
// mirroring the verified TypeScript port (memory/rag.ts):
//   • ITextEmbedder — the semantic-ranking seam
//   • RagContextBuilder — retrieves the most relevant episodes and formats them
//     as a compact context block for injection into the B! system prompt
//   • RagPipelineBuilder — fluent factory with sensible defaults
//
// RAG is strictly best-effort: any retrieval / embedding failure degrades to an
// empty string and must never block inference. In-memory port — the C#
// WithSqliteStore convenience is intentionally omitted (no SQLite backend here);
// use withStore / withInMemoryStore instead.

import Foundation

// MARK: - ITextEmbedder

/// Produces an embedding vector for a text.
public protocol ITextEmbedder: Sendable {
    /// Generates an embedding for `text`.
    func generate(_ text: String) async throws -> [Float]
}

// MARK: - RagContextBuilder

/// Retrieves the most semantically relevant episodes from an
/// `IEpisodicMemoryStore` and formats them as a compact context block for
/// injection into the B! system prompt.
public final class RagContextBuilder: @unchecked Sendable {
    private let store: IEpisodicMemoryStore
    private let embedder: ITextEmbedder?
    private let topK: Int
    private let maxCharsPerEntry: Int

    /// - Parameters:
    ///   - store: The episodic store to query.
    ///   - embedder: Optional embedder. When provided, uses semantic similarity
    ///     to rank results; when nil, falls back to recency ranking.
    ///   - topK: Maximum number of episodes to include. Default 5 (floored at 1).
    ///   - maxCharsPerEntry: Maximum characters taken from each episode's texts.
    ///     Default 300 (floored at 50).
    public init(
        store: IEpisodicMemoryStore,
        embedder: ITextEmbedder? = nil,
        topK: Int = 5,
        maxCharsPerEntry: Int = 300
    ) {
        self.store = store
        self.embedder = embedder
        self.topK = max(1, topK)
        self.maxCharsPerEntry = max(50, maxCharsPerEntry)
    }

    /// Builds a context block for the given `query` text. Returns an empty string
    /// when the store is empty or all retrievals fail (RAG is best-effort and
    /// must never block inference).
    public func buildContext(_ query: String) async -> String {
        if query.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { return "" }

        do {
            var queryEmbedding: [Float]? = nil
            if let embedder = embedder {
                do {
                    queryEmbedding = try await embedder.generate(query)
                } catch {
                    // Embedding failure is non-fatal — fall back to recency.
                }
            }

            let entries = try await store.search(queryEmbedding: queryEmbedding, topK: topK)
            if entries.isEmpty { return "" }

            return formatEntries(entries)
        } catch {
            // RAG is strictly best-effort — never break inference.
            return ""
        }
    }

    private func formatEntries(_ entries: [EpisodicMemoryEntry]) -> String {
        // Half-budget per side, integer-divided to match the C# `_maxCharsPerEntry / 2`.
        let half = maxCharsPerEntry / 2
        var sb = "[Relevant past exchanges — for context only]\n"

        for e in entries {
            let user = RagContextBuilder.truncate(e.userText, maxLen: half)
            let asst = RagContextBuilder.truncate(e.assistantText, maxLen: half)
            let when = RagContextBuilder.formatWhen(e.recordedAt) + " UTC"

            sb += "• [" + when + "] "
            if let ctx = e.appContext, !ctx.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                sb += "(" + ctx + ") "
            }
            sb += "User: " + user + "\n"
            sb += "  B!: " + asst + "\n"
        }

        return sb
    }

    /// Truncate to maxLen, replacing the last kept char with an ellipsis (matches C#).
    static func truncate(_ text: String, maxLen: Int) -> String {
        if text.isEmpty { return "" }
        if text.count <= maxLen { return text }
        return String(text.prefix(maxLen - 1)) + "…"
    }

    /// Formats a Date as "yyyy-MM-dd HH:mm" in UTC (matches C# ToString on a UTC value).
    static func formatWhen(_ d: Date) -> String {
        let fmt = DateFormatter()
        fmt.locale = Locale(identifier: "en_US_POSIX")
        fmt.timeZone = TimeZone(identifier: "UTC")
        fmt.dateFormat = "yyyy-MM-dd HH:mm"
        return fmt.string(from: d)
    }
}

// MARK: - RagPipelineBuilder

/// Fluent builder for constructing a `RagContextBuilder` with an episodic store,
/// optional embedder, and tuning parameters.
///
/// ```swift
/// let rag = RagPipelineBuilder.create()
///     .withInMemoryStore()
///     .withTopK(10)
///     .withMaxCharsPerEntry(500)
///     .build()
/// let context = await rag.buildContext("user query")
/// ```
public final class RagPipelineBuilder {
    private var store: IEpisodicMemoryStore? = nil
    private var embedder: ITextEmbedder? = nil
    private var topK: Int = 5
    private var maxCharsPerEntry: Int = 300

    private init() {}

    /// Creates a new `RagPipelineBuilder` instance.
    public static func create() -> RagPipelineBuilder {
        RagPipelineBuilder()
    }

    /// Sets the episodic memory store to retrieve past exchanges from.
    @discardableResult
    public func withStore(_ store: IEpisodicMemoryStore) -> RagPipelineBuilder {
        self.store = store
        return self
    }

    /// Convenience: creates an `InMemoryEpisodicStore` and uses it. Suitable for
    /// tests and short-lived processes where persistence is not needed.
    @discardableResult
    public func withInMemoryStore() -> RagPipelineBuilder {
        self.store = InMemoryEpisodicStore()
        return self
    }

    /// Sets the text embedder for semantic similarity search. When not set, the
    /// builder falls back to recency-based retrieval.
    @discardableResult
    public func withEmbedder(_ embedder: ITextEmbedder) -> RagPipelineBuilder {
        self.embedder = embedder
        return self
    }

    /// Sets the max number of relevant past episodes to include. Default 5, min 1.
    @discardableResult
    public func withTopK(_ topK: Int) -> RagPipelineBuilder {
        precondition(topK >= 1, "topK must be at least 1.")
        self.topK = topK
        return self
    }

    /// Sets the max characters taken from each episode's texts. Default 300, min 50.
    @discardableResult
    public func withMaxCharsPerEntry(_ maxChars: Int) -> RagPipelineBuilder {
        precondition(maxChars >= 50, "maxChars must be at least 50.")
        self.maxCharsPerEntry = maxChars
        return self
    }

    /// Builds the `RagContextBuilder` from the accumulated configuration.
    public func build() -> RagContextBuilder {
        guard let store = store else {
            preconditionFailure(
                "An episodic memory store is required. Call withStore() or " +
                "withInMemoryStore() before build().")
        }
        return RagContextBuilder(store: store, embedder: embedder, topK: topK, maxCharsPerEntry: maxCharsPerEntry)
    }
}
