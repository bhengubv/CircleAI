// CompanionGraphStores.swift
//
// The personal knowledge graph on disk, multi-hop recall over it, and the seam
// that puts a Neuron brain behind the voice loop.
//
// Ported from src/CircleAI.Companion/{SqliteKnowledgeGraph, HippoRagStore,
// NeuronVoice}.cs.

import Foundation
#if canImport(SQLite3)
import SQLite3
#endif

#if canImport(SQLite3)

// MARK: - The graph on disk

/// Triples in SQLite, so what the assistant learned about somebody survives a
/// restart.
///
/// Keyed on (subject, predicate, object) so re-stating a fact UPDATES its
/// confidence rather than adding a duplicate edge. Without that the PageRank
/// walk below is skewed by nothing more than how often a fact happened to be
/// repeated in conversation.
public final class SqliteKnowledgeGraph: @unchecked Sendable {

    private let connection: SqliteConnection

    public init(connection: SqliteConnection) throws {
        self.connection = connection
        try migrate()
    }

    public convenience init(path: String) throws {
        try self.init(connection: try SqliteConnection(path: path))
    }

    private func migrate() throws {
        try connection.execute("""
            CREATE TABLE IF NOT EXISTS kg_nodes (
                id TEXT PRIMARY KEY,
                kind TEXT NOT NULL,
                name TEXT NOT NULL,
                properties TEXT
            );
            CREATE TABLE IF NOT EXISTS kg_triples (
                subject TEXT NOT NULL,
                predicate TEXT NOT NULL,
                object TEXT NOT NULL,
                source TEXT,
                confidence REAL NOT NULL,
                recorded_at TEXT NOT NULL,
                PRIMARY KEY (subject, predicate, object)
            );
            CREATE INDEX IF NOT EXISTS ix_kg_triples_subject ON kg_triples(subject);
            """)
    }

    // MARK: Nodes

    public func upsertNode(_ node: KnowledgeNode) {
        connection.lock.lock(); defer { connection.lock.unlock() }

        var properties: String?
        if let p = node.properties,
           let data = try? JSONSerialization.data(withJSONObject: p, options: [.sortedKeys]) {
            properties = String(decoding: data, as: UTF8.self)
        }

        guard let stmt = try? connection.prepare("""
            INSERT INTO kg_nodes (id, kind, name, properties) VALUES (?, ?, ?, ?)
            ON CONFLICT(id) DO UPDATE SET kind = excluded.kind, name = excluded.name,
                                          properties = excluded.properties
            """) else { return }
        defer { sqlite3_finalize(stmt) }

        connection.bind(stmt, [.text(node.id), .text(node.kind), .text(node.name),
                               .optional(properties)])
        _ = sqlite3_step(stmt)
    }

    public func getNode(_ id: String) -> KnowledgeNode? {
        connection.lock.lock(); defer { connection.lock.unlock() }
        guard let stmt = try? connection.prepare(
            "SELECT id, kind, name, properties FROM kg_nodes WHERE id = ?") else { return nil }
        defer { sqlite3_finalize(stmt) }

        connection.bind(stmt, [.text(id)])
        guard sqlite3_step(stmt) == SQLITE_ROW else { return nil }

        var properties: [String: String]?
        if let raw = sqlite3_column_text(stmt, 3) {
            let json = String(cString: raw)
            properties = (try? JSONSerialization.jsonObject(with: Data(json.utf8)))
                as? [String: String]
        }
        return KnowledgeNode(id: String(cString: sqlite3_column_text(stmt, 0)),
                             kind: String(cString: sqlite3_column_text(stmt, 1)),
                             name: String(cString: sqlite3_column_text(stmt, 2)),
                             properties: properties)
    }

    // MARK: Triples

    @discardableResult
    public func addTriple(subject: String, predicate: String, object: String,
                          source: String? = nil, confidence: Float = 1.0,
                          recordedAt: Date = Date()) -> Bool {
        connection.lock.lock(); defer { connection.lock.unlock() }
        guard let stmt = try? connection.prepare("""
            INSERT INTO kg_triples (subject, predicate, object, source, confidence, recorded_at)
            VALUES (?, ?, ?, ?, ?, ?)
            ON CONFLICT(subject, predicate, object) DO UPDATE SET
                source = excluded.source,
                confidence = excluded.confidence,
                recorded_at = excluded.recorded_at
            """) else { return false }
        defer { sqlite3_finalize(stmt) }

        connection.bind(stmt, [.text(subject), .text(predicate), .text(object),
                               .optional(source), .double(Double(confidence)),
                               .text(AtomLog.stamp.string(from: recordedAt))])
        return sqlite3_step(stmt) == SQLITE_DONE
    }

    public func allTriples() -> [KnowledgeTriple] {
        connection.lock.lock(); defer { connection.lock.unlock() }
        return readTriples(sql: """
            SELECT subject, predicate, object, source, confidence, recorded_at
            FROM kg_triples ORDER BY subject, predicate, object
            """, bind: [])
    }

    public func readTriples(subject: String) -> [KnowledgeTriple] {
        connection.lock.lock(); defer { connection.lock.unlock() }
        return readTriples(sql: """
            SELECT subject, predicate, object, source, confidence, recorded_at
            FROM kg_triples WHERE subject = ? ORDER BY predicate, object
            """, bind: [.text(subject)])
    }

    private func readTriples(sql: String, bind values: [SqlValue]) -> [KnowledgeTriple] {
        guard let stmt = try? connection.prepare(sql) else { return [] }
        defer { sqlite3_finalize(stmt) }
        connection.bind(stmt, values)

        var out: [KnowledgeTriple] = []
        while sqlite3_step(stmt) == SQLITE_ROW {
            let source = sqlite3_column_text(stmt, 3).map { String(cString: $0) }
            let stampText = String(cString: sqlite3_column_text(stmt, 5))
            out.append(KnowledgeTriple(
                subject: String(cString: sqlite3_column_text(stmt, 0)),
                predicate: String(cString: sqlite3_column_text(stmt, 1)),
                object: String(cString: sqlite3_column_text(stmt, 2)),
                source: source,
                confidence: Float(sqlite3_column_double(stmt, 4)),
                recordedAt: AtomLog.stamp.date(from: stampText) ?? Date(timeIntervalSince1970: 0)))
        }
        return out
    }

    public var tripleCount: Int {
        allTriples().count
    }
}

// MARK: - Multi-hop recall

/// Personalised PageRank over the knowledge graph.
///
/// The HippoRAG idea (Wang et al. 2024): a query's own terms SEED a random walk
/// over the graph, and whatever the walk pools mass on is what the query is
/// associated with — including things no single edge connects it to. That is
/// the "multi-hop" part, and it is what a plain similarity search cannot do.
public final class SqliteHippoRagStore: IHippoRagStore, @unchecked Sendable {

    private let graph: SqliteKnowledgeGraph
    private let walkIterations: Int
    private let damping: Double

    public init(graph: SqliteKnowledgeGraph, walkIterations: Int = 32,
                damping: Double = 0.85) {
        self.graph = graph
        self.walkIterations = walkIterations
        self.damping = damping
    }

    public var backendId: String { "sqlite-hippo-ppr" }

    public func index(_ item: MemoryItem) async throws {
        // The graph is populated by the extractor; this only ensures the item
        // exists as a NODE so the walker has somewhere to land on it.
        graph.addTriple(subject: item.id, predicate: "memory_text", object: item.text,
                        source: item.id, confidence: 1.0)
        for (k, v) in (item.metadata ?? [:]).sorted(by: { $0.key < $1.key }) {
            graph.addTriple(subject: item.id, predicate: k, object: v,
                            source: item.id, confidence: 0.9)
        }
    }

    public func multiHopRecall(query: String, topK: Int) async throws -> [MemoryHit] {
        guard !query.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw HerJarvisError.invalidArgument("A query is required.")
        }
        guard topK > 0 else {
            throw HerJarvisError.invalidArgument("topK must be greater than zero.")
        }

        let triples = graph.allTriples()
        guard !triples.isEmpty else { return [] }

        var outgoing: [String: [(neighbour: String, confidence: Float)]] = [:]
        var allNodes = Set<String>()
        for t in triples {
            allNodes.insert(t.subject)
            allNodes.insert(t.object)
            outgoing[t.subject, default: []].append((t.object, t.confidence))
        }

        let queryTerms = Set(Self.terms(of: query))
        let seeds = allNodes.filter { queryTerms.contains($0.lowercased()) }.sorted()

        // NO QUERY TERM TOUCHES THE GRAPH, so there is no genuine association.
        // Return nothing rather than fabricating one from arbitrary nodes — the
        // episodic path already covers recency and similarity, and noise here
        // is worse than silence.
        guard !seeds.isEmpty else { return [] }

        let seedMass = 1.0 / Double(seeds.count)
        var rank: [String: Double] = [:]
        for n in allNodes { rank[n] = 0 }
        for s in seeds { rank[s] = seedMass }

        for _ in 0..<walkIterations {
            var next: [String: Double] = [:]
            for n in allNodes { next[n] = 0 }

            // The random-jump component: the walk always falls back to the
            // query's own terms, which is what makes this PERSONALISED rather
            // than global PageRank.
            for s in seeds { next[s, default: 0] += (1 - damping) * seedMass }

            for (node, mass) in rank where mass > 0 {
                guard let neighbours = outgoing[node], !neighbours.isEmpty else {
                    // A dangling node would otherwise LOSE its mass, and the
                    // ranking would stop summing to one.
                    for s in seeds {
                        next[s, default: 0] += damping * mass / Double(seeds.count)
                    }
                    continue
                }
                // CONFIDENCE-WEIGHTED spread: a high-confidence edge carries more
                // of the walk than a guessed one, so a shaky belief does not
                // steer recall like a stated fact. Equal confidences reduce this
                // to the plain 1/count split.
                let totalConfidence = neighbours.reduce(Float(0)) { $0 + $1.confidence }
                for (neighbour, confidence) in neighbours {
                    let weight = totalConfidence > 0
                        ? Double(confidence) / Double(totalConfidence)
                        : 1.0 / Double(neighbours.count)
                    next[neighbour, default: 0] += damping * mass * weight
                }
            }
            rank = next
        }

        // THE SEEDS ARE THE QUERY'S OWN TERMS and are not recalled memories.
        // Excluding them is what makes this return the associations the walk
        // reached rather than the question echoed back.
        let seedSet = Set(seeds)
        var scored = rank
            .filter { $0.value > 0 && !seedSet.contains($0.key) }
            .map { (node: $0.key, score: $0.value) }

        // Ties break by node id so repeated recalls of the same graph return the
        // same order — a caller taking the top 1 must be able to reproduce it.
        scored.sort { $0.score != $1.score ? $0.score > $1.score : $0.node < $1.node }

        return scored.prefix(topK).map { hit in
            MemoryHit(item: MemoryItem(id: hit.node, text: textFor(node: hit.node)),
                      score: hit.score)
        }
    }

    /// The stored text for a node, or the node id when it is an entity rather
    /// than a memory. Never empty: a hit with no text is a row a caller cannot
    /// show anybody.
    private func textFor(node: String) -> String {
        for t in graph.readTriples(subject: node) where t.predicate == "memory_text" {
            return t.object
        }
        return node
    }

    static func terms(of text: String) -> [String] {
        var out: [String] = []
        var current = ""
        func flush() {
            if !current.isEmpty { out.append(current) }
            current = ""
        }
        for ch in text.lowercased() {
            if ch.isLetter || ch.isNumber { current.append(ch) } else { flush() }
        }
        flush()
        return out
    }
}

#endif

// MARK: - A Neuron behind the voice loop

/// Composition seam: build a companion session over a brain, then hand it to
/// the voice listener.
///
/// The point is that NO NEW VOICE LOGIC exists here. Routing the voice loop
/// through a `CompanionSession` rather than straight at the brain is what makes
/// the Neuron's concierge routing, two-slot residency, memory and persona all
/// apply to a spoken turn exactly as they do to a typed one — otherwise the
/// assistant knows you when you type and forgets you when you speak.
public enum NeuronVoice {

    /// Builds the session over the brain and hands it to the voice listener.
    ///
    /// The interface is `.ambient` and that is the substantive choice: a voice
    /// turn arriving from across a room is not a phone turn, and the session
    /// carries that through to persona and context. Everything else here is
    /// composition — the C# takes an `IAIService`, Swift's session takes the
    /// generator and stores directly, so the caller passes those.
    public static func createListener(
        pipeline: any IVoicePipeline,
        generator: any IChatGenerator,
        episodic: any IEpisodicMemoryStore,
        recall: any IRecall,
        identityId: String = "default",
        displayName: String = "You",
        sessionId: String = UUID().uuidString
    ) -> VoiceCompanionListener {
        let options = CompanionSessionOptions(
            sessionId: sessionId,
            identityId: identityId,
            interface: .ambient,
            displayName: displayName)

        let session = CompanionSession(generator: generator, episodic: episodic,
                                       recall: recall, options: options)
        return VoiceCompanionListener(pipeline: pipeline, session: session)
    }

    /// The same wiring when the caller already holds a session.
    public static func createListener(
        pipeline: any IVoicePipeline,
        session: any ICompanionSession
    ) -> VoiceCompanionListener {
        VoiceCompanionListener(pipeline: pipeline, session: session)
    }
}
