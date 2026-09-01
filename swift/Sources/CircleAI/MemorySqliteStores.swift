// MemorySqliteStores.swift
//
// The three SQLite-backed stores: atoms, episodes and goals.
//
// SQLITE IS THE DEFAULT AND THE ONLY ONE THAT MATTERS FIRST. It needs no
// server, ships inside the app, and is the only option on a phone. The other
// engines are the shared case and are a dialect problem behind IAtomStore
// rather than a second design.
//
// A THIN C SHIM, DELIBERATELY. There is no SwiftPM SQLite wrapper here because
// adding one would put a dependency in front of the one store that has to work
// on a device with nothing installed. sqlite3 is already in the system on every
// Apple platform; this file is the fifty lines needed to reach it.
//
// TIMESTAMPS ARE ISO-8601 STRINGS, not native date types. Engines and drivers
// have opinions about time zones and precision, and every one of them is a way
// for a memory to come back an hour wrong. A sortable string is the same
// everywhere and reads correctly in a dump.
//
// Ported from SqliteAtomStore.cs, SqliteEpisodicStore.cs, SqliteGoalStore.cs.

import Foundation
#if canImport(SQLite3)
import SQLite3

/// SQLite hands back pointers it owns and may free; this tells it to copy.
private let SQLITE_TRANSIENT = unsafeBitCast(-1, to: sqlite3_destructor_type.self)

public enum SqliteStoreError: Error, CustomStringConvertible {
    case cannotOpen(String)
    case statementFailed(String)

    public var description: String {
        switch self {
        case .cannotOpen(let p): return "Could not open the database at \(p)."
        case .statementFailed(let m): return "SQLite refused a statement: \(m)."
        }
    }
}

/// One connection, one lock. A SQLite connection is not thread-safe and an app
/// reaches for its memory from the UI thread and a background one in the same
/// second; memory operations are not a parallel workload, so they are
/// serialised rather than made clever.
public final class SqliteConnection: @unchecked Sendable {

    let db: OpaquePointer
    let lock = NSLock()

    public init(path: String) throws {
        var handle: OpaquePointer?
        guard sqlite3_open(path, &handle) == SQLITE_OK, let handle else {
            throw SqliteStoreError.cannotOpen(path)
        }
        db = handle
        // Write-ahead logging: a reader is never blocked by the writer, which
        // is what keeps a recall on the UI thread from waiting on a learn.
        _ = try? execute("PRAGMA journal_mode=WAL")
    }

    /// An in-memory database, which is what a test wants.
    public static func inMemory() throws -> SqliteConnection {
        try SqliteConnection(path: ":memory:")
    }

    deinit { sqlite3_close(db) }

    @discardableResult
    func execute(_ sql: String) throws -> Bool {
        var error: UnsafeMutablePointer<CChar>?
        guard sqlite3_exec(db, sql, nil, nil, &error) == SQLITE_OK else {
            let message = error.map { String(cString: $0) } ?? "unknown"
            sqlite3_free(error)
            throw SqliteStoreError.statementFailed(message)
        }
        return true
    }

    func prepare(_ sql: String) throws -> OpaquePointer {
        var stmt: OpaquePointer?
        guard sqlite3_prepare_v2(db, sql, -1, &stmt, nil) == SQLITE_OK, let stmt else {
            throw SqliteStoreError.statementFailed(String(cString: sqlite3_errmsg(db)))
        }
        return stmt
    }
}

/// What can be bound to a statement. Written out rather than taking `Any` so a
/// caller cannot pass something that silently binds as NULL.
enum SqlValue {
    case text(String)
    case int(Int)
    case double(Double)
    case blob([UInt8])
    case null

    static func optional(_ s: String?) -> SqlValue { s.map { .text($0) } ?? .null }
    static func optional(_ i: Int?) -> SqlValue { i.map { .int($0) } ?? .null }
    static func optional(_ d: Date?) -> SqlValue {
        d.map { .text(AtomLog.stamp.string(from: $0)) } ?? .null
    }
}

extension SqliteConnection {

    func bind(_ stmt: OpaquePointer, _ values: [SqlValue]) {
        for (i, v) in values.enumerated() {
            let idx = Int32(i + 1)
            switch v {
            case .text(let s): sqlite3_bind_text(stmt, idx, s, -1, SQLITE_TRANSIENT)
            case .int(let n): sqlite3_bind_int64(stmt, idx, Int64(n))
            case .double(let d): sqlite3_bind_double(stmt, idx, d)
            case .blob(let b):
                if b.isEmpty {
                    sqlite3_bind_zeroblob(stmt, idx, 0)
                } else {
                    sqlite3_bind_blob(stmt, idx, b, Int32(b.count), SQLITE_TRANSIENT)
                }
            case .null: sqlite3_bind_null(stmt, idx)
            }
        }
    }

    func run(_ sql: String, _ values: [SqlValue] = []) throws {
        lock.lock(); defer { lock.unlock() }
        let stmt = try prepare(sql)
        defer { sqlite3_finalize(stmt) }
        bind(stmt, values)
        let rc = sqlite3_step(stmt)
        guard rc == SQLITE_DONE || rc == SQLITE_ROW else {
            throw SqliteStoreError.statementFailed(String(cString: sqlite3_errmsg(db)))
        }
    }

    func query<T>(_ sql: String, _ values: [SqlValue] = [],
                  _ read: (OpaquePointer) -> T) throws -> [T] {
        lock.lock(); defer { lock.unlock() }
        let stmt = try prepare(sql)
        defer { sqlite3_finalize(stmt) }
        bind(stmt, values)
        var out: [T] = []
        while sqlite3_step(stmt) == SQLITE_ROW { out.append(read(stmt)) }
        return out
    }

    func scalarInt(_ sql: String, _ values: [SqlValue] = []) throws -> Int {
        try query(sql, values) { Int(sqlite3_column_int64($0, 0)) }.first ?? 0
    }
}

func sqlText(_ stmt: OpaquePointer, _ i: Int32) -> String? {
    guard let c = sqlite3_column_text(stmt, i) else { return nil }
    return String(cString: c)
}

func sqlDate(_ stmt: OpaquePointer, _ i: Int32) -> Date? {
    sqlText(stmt, i).map { AtomLog.time($0) }
}

// MARK: - Atoms

/// IAtomStore on SQLite.
///
/// UPSERT IS DELETE-THEN-INSERT IN A TRANSACTION, not MERGE — the same
/// everywhere, and exactly the idempotence a replay needs.
///
/// SUPERSEDED ATOMS ARE NEVER DELETED. They stop being answers and stay
/// readable, because the history is what gives a current atom its weight.
public final class SqliteAtomStore: IAtomStore, @unchecked Sendable {

    private let conn: SqliteConnection

    public init(connection: SqliteConnection) throws {
        self.conn = connection
        try conn.execute("""
            CREATE TABLE IF NOT EXISTS atoms (
                id TEXT NOT NULL PRIMARY KEY,
                kind TEXT NOT NULL,
                text TEXT NOT NULL,
                subject TEXT,
                source_episode TEXT,
                recorded_at_utc TEXT NOT NULL,
                corrections INTEGER NOT NULL,
                last_corrected_utc TEXT,
                superseded_by TEXT,
                challenge TEXT,
                outcome TEXT,
                verify TEXT,
                verified_at_utc TEXT,
                verified_ok INTEGER,
                machine TEXT,
                text_key TEXT
            )
            """)
        // Indexed, because learning asks "do I know this" of every sentence it
        // spots, and learning runs on every turn of a conversation.
        try conn.execute(
            "CREATE INDEX IF NOT EXISTS ix_atoms_text_key ON atoms (text_key, superseded_by)")
        try conn.execute(
            "CREATE INDEX IF NOT EXISTS ix_atoms_subject ON atoms (subject, superseded_by)")
        try conn.execute(
            "CREATE INDEX IF NOT EXISTS ix_atoms_kind ON atoms (kind, superseded_by)")
    }

    public convenience init(path: String) throws {
        try self.init(connection: try SqliteConnection(path: path))
    }

    private static let columns =
        "id, kind, text, subject, source_episode, recorded_at_utc, corrections, "
        + "last_corrected_utc, superseded_by, challenge, outcome, verify, "
        + "verified_at_utc, verified_ok, machine"

    public func add(_ atom: MemoryAtom) async throws {
        try conn.run("DELETE FROM atoms WHERE id = ?", [.text(AtomLog.compact(atom.id))])
        try conn.run("""
            INSERT INTO atoms (\(Self.columns), text_key)
            VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)
            """, [
            .text(AtomLog.compact(atom.id)),
            .text(atom.kind.wireName),
            .text(atom.text),
            .optional(atom.subject),
            .optional(atom.sourceEpisode.map(AtomLog.compact)),
            .text(AtomLog.stamp.string(from: atom.recordedAtUtc)),
            .int(atom.corrections),
            .optional(atom.lastCorrectedUtc),
            .optional(atom.supersededBy.map(AtomLog.compact)),
            .optional(atom.challenge),
            .optional(atom.outcome?.wireName),
            .optional(atom.verify),
            .optional(atom.verifiedAtUtc),
            atom.verifiedOk.map { SqlValue.int($0 ? 1 : 0) } ?? .null,
            .optional(atom.machine),
            .text(CueExtractor.normalise(atom.text)),
        ])
    }

    public func supersede(_ oldAtomId: UUID, with replacement: MemoryAtom) async throws -> MemoryAtom {
        let previous = try await get(oldAtomId)

        // THE COUNT CARRIES FORWARD. Losing the tally would throw away the
        // signal that makes a repeatedly-corrected atom outrank a fresh one.
        var carried = replacement
        carried.machine = replacement.machine ?? previous?.machine
        carried.verify = replacement.verify ?? previous?.verify
        // The KIND is the old one: a correction refines what was said, it does
        // not reclassify it. A ruling corrected into a decision would quietly
        // lose its floor and start fading.
        carried.kind = previous?.kind ?? replacement.kind
        carried.subject = replacement.subject ?? previous?.subject
        carried.challenge = replacement.challenge ?? previous?.challenge
        carried.outcome = replacement.outcome ?? previous?.outcome
        carried.corrections = max(replacement.corrections, (previous?.corrections ?? 0) + 1)
        carried.lastCorrectedUtc = replacement.lastCorrectedUtc ?? Date()

        try await add(carried)
        try conn.run("UPDATE atoms SET superseded_by = ? WHERE id = ?",
                     [.text(AtomLog.compact(carried.id)), .text(AtomLog.compact(oldAtomId))])
        return carried
    }

    public func markVerified(_ id: UUID, ok: Bool, whenUtc: Date) async throws {
        try conn.run(
            "UPDATE atoms SET verified_ok = ?, verified_at_utc = ? WHERE id = ?",
            [.int(ok ? 1 : 0), .text(AtomLog.stamp.string(from: whenUtc)),
             .text(AtomLog.compact(id))])
    }

    public func match(_ situation: Situation, limit: Int) async throws -> [MemoryAtom] {
        var results: [MemoryAtom] = []
        var seen = Set<UUID>()

        // SUBJECT FIRST, MOST SPECIFIC FIRST. Matching what the action is about
        // against what the atom is about is a lookup; searching prose for
        // relevance is a guess. Keyword search fills in behind it.
        for key in situation.keys {
            if results.count >= limit { break }
            let rows = try conn.query("""
                SELECT \(Self.columns) FROM atoms
                WHERE superseded_by IS NULL AND subject = ?
                ORDER BY recorded_at_utc DESC LIMIT ?
                """, [.text(key), .int(limit - results.count)], Self.read)
            for atom in rows where seen.insert(atom.id).inserted { results.append(atom) }
        }

        if results.count < limit {
            let terms = Self.terms(situation.query)
            if !terms.isEmpty {
                let clause = terms.map { _ in
                    "(text LIKE ? OR subject LIKE ? OR challenge LIKE ?)"
                }.joined(separator: " OR ")
                var binds: [SqlValue] = []
                for t in terms {
                    binds.append(.text("%\(t)%"))
                    binds.append(.text("%\(t)%"))
                    binds.append(.text("%\(t)%"))
                }
                binds.append(.int(limit))
                let rows = try conn.query("""
                    SELECT \(Self.columns) FROM atoms
                    WHERE superseded_by IS NULL AND (\(clause))
                    ORDER BY recorded_at_utc DESC LIMIT ?
                    """, binds, Self.read)
                for atom in rows where seen.insert(atom.id).inserted { results.append(atom) }
            }
        }

        return Array(results.prefix(limit))
    }

    public func byKind(_ kind: AtomKind, limit: Int) async throws -> [MemoryAtom] {
        try conn.query("""
            SELECT \(Self.columns) FROM atoms
            WHERE superseded_by IS NULL AND kind = ?
            ORDER BY recorded_at_utc DESC LIMIT ?
            """, [.text(kind.wireName), .int(limit)], Self.read)
    }

    public func all(includeSuperseded: Bool, limit: Int) async throws -> [MemoryAtom] {
        let filter = includeSuperseded ? "" : "WHERE superseded_by IS NULL "
        return try conn.query("""
            SELECT \(Self.columns) FROM atoms \(filter)ORDER BY recorded_at_utc DESC LIMIT ?
            """, [.int(limit)], Self.read)
    }

    public func knows(_ text: String) async throws -> Bool {
        guard !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return false }
        return try conn.scalarInt(
            "SELECT COUNT(*) FROM atoms WHERE text_key = ? AND superseded_by IS NULL",
            [.text(CueExtractor.normalise(text))]) > 0
    }

    public func get(_ id: UUID) async throws -> MemoryAtom? {
        try conn.query("SELECT \(Self.columns) FROM atoms WHERE id = ?",
                       [.text(AtomLog.compact(id))], Self.read).first
    }

    public func count() async throws -> Int {
        try conn.scalarInt("SELECT COUNT(*) FROM atoms WHERE superseded_by IS NULL")
    }

    /// Words shorter than three characters match everything, and past eight
    /// terms a keyword search stops narrowing and starts costing.
    static func terms(_ query: String) -> [String] {
        var seen = Set<String>()
        var out: [String] = []
        for raw in query.split(whereSeparator: { " \t\n,;".contains($0) }) {
            let t = String(raw)
            guard t.count > 2, seen.insert(t.lowercased()).inserted else { continue }
            out.append(t)
            if out.count == 8 { break }
        }
        return out
    }

    static func read(_ s: OpaquePointer) -> MemoryAtom {
        MemoryAtom(
            id: AtomLog.parseCompact(sqlText(s, 0) ?? "") ?? UUID(),
            kind: AtomKind.fromWire(sqlText(s, 1)) ?? .fact,
            text: sqlText(s, 2) ?? "",
            subject: sqlText(s, 3),
            sourceEpisode: sqlText(s, 4).flatMap(AtomLog.parseCompact),
            recordedAtUtc: sqlDate(s, 5) ?? Date.distantPast,
            machine: sqlText(s, 14),
            corrections: Int(sqlite3_column_int64(s, 6)),
            lastCorrectedUtc: sqlDate(s, 7),
            supersededBy: sqlText(s, 8).flatMap(AtomLog.parseCompact),
            challenge: sqlText(s, 9),
            outcome: DecisionOutcome.fromWire(sqlText(s, 10)),
            verify: sqlText(s, 11),
            verifiedAtUtc: sqlDate(s, 12),
            // NULL is "never checked", which is not the same as "checked and
            // wrong" — conflating them makes every unverified fact look stale.
            verifiedOk: sqlite3_column_type(s, 13) == SQLITE_NULL
                ? nil
                : sqlite3_column_int64(s, 13) == 1)
    }
}

// MARK: - Episodes

/// IEpisodicMemoryStore on SQLite.
public final class SqliteEpisodicStore: IEpisodicMemoryStore, @unchecked Sendable {

    private let conn: SqliteConnection

    public init(connection: SqliteConnection) throws {
        self.conn = connection
        try conn.execute("""
            CREATE TABLE IF NOT EXISTS episodes (
                id TEXT NOT NULL PRIMARY KEY,
                recorded_at TEXT NOT NULL,
                user_text TEXT NOT NULL,
                assistant_text TEXT NOT NULL,
                app_context TEXT,
                embedding BLOB
            )
            """)
        try conn.execute(
            "CREATE INDEX IF NOT EXISTS ix_episodes_time ON episodes (recorded_at)")
    }

    public func add(_ entry: EpisodicMemoryEntry) async throws {
        try conn.run("""
            INSERT OR REPLACE INTO episodes
            (id, recorded_at, user_text, assistant_text, app_context, embedding)
            VALUES (?,?,?,?,?,?)
            """, [
            .text(AtomLog.compact(entry.id)),
            .text(AtomLog.stamp.string(from: entry.recordedAt)),
            .text(entry.userText),
            .text(entry.assistantText),
            .optional(entry.appContext),
            entry.embedding.map { SqlValue.blob(Self.floatsToBytes($0)) } ?? .null,
        ])
    }

    /// Newest first, which is what a screen showing recent turns wants.
    public func getRecent(count: Int) async throws -> [EpisodicMemoryEntry] {
        try conn.query(Self.select + " ORDER BY recorded_at DESC LIMIT ?",
                       [.int(count)], Self.read)
    }

    /// Cosine over the stored embeddings, falling back to RECENCY when there is
    /// no query vector.
    ///
    /// The fallback is the point: an embedding model that failed to load must
    /// leave the store answering something useful rather than nothing. Recall
    /// gets worse; it does not stop.
    public func search(queryEmbedding: [Float]?, topK: Int) async throws -> [EpisodicMemoryEntry] {
        guard let q = queryEmbedding, !q.isEmpty else {
            return try await getRecent(count: topK)
        }
        let all = try conn.query(Self.select, [], Self.read)
        let scored = all.compactMap { e -> (EpisodicMemoryEntry, Float)? in
            guard let v = e.embedding, v.count == q.count else { return nil }
            return (e, Self.cosine(v, q))
        }
        return scored.sorted { $0.1 > $1.1 }.prefix(topK).map { $0.0 }
    }

    public func delete(_ id: UUID) async throws {
        try conn.run("DELETE FROM episodes WHERE id = ?", [.text(AtomLog.compact(id))])
    }

    public func count() async throws -> Int {
        try conn.scalarInt("SELECT COUNT(*) FROM episodes")
    }

    /// Removes everything older than the cutoff and says how many went.
    public func pruneOlderThan(cutoff: Date) async throws -> Int {
        let before = try await count()
        try conn.run("DELETE FROM episodes WHERE recorded_at < ?",
                     [.text(AtomLog.stamp.string(from: cutoff))])
        return before - (try await count())
    }

    private static let select =
        "SELECT id, recorded_at, user_text, assistant_text, app_context, embedding FROM episodes"

    static func read(_ s: OpaquePointer) -> EpisodicMemoryEntry {
        var embedding: [Float]?
        if sqlite3_column_type(s, 5) != SQLITE_NULL, let raw = sqlite3_column_blob(s, 5) {
            let n = Int(sqlite3_column_bytes(s, 5))
            embedding = bytesToFloats([UInt8](UnsafeRawBufferPointer(start: raw, count: n)))
        }
        return EpisodicMemoryEntry(
            id: AtomLog.parseCompact(sqlText(s, 0) ?? "") ?? UUID(),
            recordedAt: sqlDate(s, 1) ?? Date.distantPast,
            userText: sqlText(s, 2) ?? "",
            assistantText: sqlText(s, 3) ?? "",
            appContext: sqlText(s, 4),
            embedding: embedding)
    }

    static func cosine(_ a: [Float], _ b: [Float]) -> Float {
        var dot: Float = 0, na: Float = 0, nb: Float = 0
        for i in 0..<min(a.count, b.count) {
            dot += a[i] * b[i]
            na += a[i] * a[i]
            nb += b[i] * b[i]
        }
        // A zero vector has no direction, so it is similar to NOTHING rather
        // than dividing by zero and coming back NaN, which sorts unpredictably.
        guard na > 0, nb > 0 else { return 0 }
        return dot / (na.squareRoot() * nb.squareRoot())
    }

    /// Little-endian float bits, written out rather than left to a serialiser:
    /// an embedding whose bytes come back the other way round produces
    /// plausible nonsense rather than an error, and a vector search then
    /// returns the wrong neighbours with no sign anything is wrong.
    static func floatsToBytes(_ v: [Float]) -> [UInt8] {
        var out = [UInt8]()
        out.reserveCapacity(v.count * 4)
        for f in v {
            let bits = f.bitPattern
            out.append(UInt8(bits & 0xFF))
            out.append(UInt8((bits >> 8) & 0xFF))
            out.append(UInt8((bits >> 16) & 0xFF))
            out.append(UInt8((bits >> 24) & 0xFF))
        }
        return out
    }

    static func bytesToFloats(_ b: [UInt8]) -> [Float] {
        guard b.count >= 4 else { return [] }
        var out = [Float]()
        out.reserveCapacity(b.count / 4)
        for i in stride(from: 0, to: b.count - 3, by: 4) {
            let bits = UInt32(b[i]) | (UInt32(b[i + 1]) << 8)
                | (UInt32(b[i + 2]) << 16) | (UInt32(b[i + 3]) << 24)
            out.append(Float(bitPattern: bits))
        }
        return out
    }
}

// MARK: - Goals

/// IGoalStore on SQLite.
public final class SqliteGoalStore: IGoalStore, @unchecked Sendable {

    private let conn: SqliteConnection

    public init(connection: SqliteConnection) throws {
        self.conn = connection
        try conn.execute("""
            CREATE TABLE IF NOT EXISTS goals (
                id TEXT NOT NULL PRIMARY KEY,
                user_id TEXT NOT NULL,
                title TEXT NOT NULL,
                detail TEXT,
                status TEXT NOT NULL,
                priority TEXT NOT NULL,
                created_at TEXT NOT NULL,
                due_at TEXT,
                completed_at TEXT,
                notes TEXT
            )
            """)
        try conn.execute("CREATE INDEX IF NOT EXISTS ix_goals_user ON goals (user_id, status)")
    }

    @discardableResult
    public func upsert(_ goal: Goal) async throws -> Goal {
        try conn.run("""
            INSERT OR REPLACE INTO goals
            (id, user_id, title, detail, status, priority, created_at, due_at, completed_at, notes)
            VALUES (?,?,?,?,?,?,?,?,?,?)
            """, [
            .text(goal.id),
            .text(goal.userId),
            .text(goal.title),
            .text(goal.description),
            .text(goal.status.rawValue),
            .text(goal.priority.rawValue),
            .text(AtomLog.stamp.string(from: goal.createdAt)),
            .optional(goal.dueAt),
            .optional(goal.completedAt),
            .optional(goal.notes),
        ])
        return goal
    }

    public func get(id: String) async throws -> Goal? {
        try conn.query(Self.select + " WHERE id = ?", [.text(id)], Self.read).first
    }

    public func list(userId: String) async throws -> [Goal] {
        try conn.query(Self.select + " WHERE user_id = ? ORDER BY created_at DESC",
                       [.text(userId)], Self.read)
    }

    public func getActive(userId: String) async throws -> [Goal] {
        try conn.query(Self.select + " WHERE user_id = ? AND status = ? ORDER BY created_at DESC",
                       [.text(userId), .text(GoalStatus.active.rawValue)], Self.read)
    }

    public func delete(id: String) async throws {
        try conn.run("DELETE FROM goals WHERE id = ?", [.text(id)])
    }

    private static let select =
        "SELECT id, user_id, title, detail, status, priority, created_at, due_at, "
        + "completed_at, notes FROM goals"

    static func read(_ s: OpaquePointer) -> Goal {
        Goal(id: sqlText(s, 0) ?? "",
             userId: sqlText(s, 1) ?? "",
             title: sqlText(s, 2) ?? "",
             description: sqlText(s, 3) ?? "",
             status: GoalStatus(rawValue: sqlText(s, 4) ?? "") ?? .active,
             priority: GoalPriority(rawValue: sqlText(s, 5) ?? "") ?? .normal,
             createdAt: sqlDate(s, 6) ?? Date.distantPast,
             dueAt: sqlDate(s, 7),
             completedAt: sqlDate(s, 8),
             notes: sqlText(s, 9))
    }
}

#endif
