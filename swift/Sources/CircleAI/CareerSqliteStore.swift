// CareerSqliteStore.swift
//
// The profile, the job specs, and every version of every document ever
// approved.
//
// WHY A SCHEMA AND NOT A JSON BLOB. The whole point of the profile is that it is
// queryable and reusable: the same facts answer "draft me a CV for this security
// job" today and "which of my jobs match this one" next month. A blob can be
// rendered and cannot be reasoned about — and a blob is exactly what people
// already have, a CV.doc they edit and re-save until nobody knows which one they
// sent.
//
// APPROVED DOCUMENTS ARE KEPT AS BOTH. The rendered file is what the person owns
// and can send; the facts and the SELECTION that produced it are kept beside it,
// because a blob alone cannot be re-tailored. Applying for a second job should
// start from the last approval, not from nothing. It also makes the record
// honest: for any application there is a row saying which facts were claimed, to
// whom, and when.
//
// ON-DEVICE, AND THERE IS NO SYNC. Employment history and contact details are
// the personal information most able to do harm if it travelled. Nothing in this
// file opens a socket.
//
// Ported from src/CircleAI.Career/SqliteCareerStore.cs.

import Foundation
#if canImport(SQLite3)
import SQLite3

public final class SqliteCareerStore: @unchecked Sendable {

    private let connection: SqliteConnection

    public init(connection: SqliteConnection) throws {
        self.connection = connection
        try createSchema()
    }

    public convenience init(databasePath: String) throws {
        let dir = (databasePath as NSString).deletingLastPathComponent
        if !dir.isEmpty {
            try? FileManager.default.createDirectory(atPath: dir, withIntermediateDirectories: true)
        }
        try self.init(connection: try SqliteConnection(path: databasePath))
    }

    private func createSchema() throws {
        try connection.execute("""
            -- ONE ROW, ENFORCED. A person has one career profile on their own
            -- phone; a table that permits two invites the bug where half the app
            -- reads the other one.
            CREATE TABLE IF NOT EXISTS profile (
                id        INTEGER PRIMARY KEY CHECK (id = 1),
                full_name TEXT NOT NULL DEFAULT '',
                headline  TEXT NOT NULL DEFAULT '',
                phone     TEXT,
                email     TEXT,
                location  TEXT,
                summary   TEXT
            );
            INSERT OR IGNORE INTO profile (id) VALUES (1);

            -- Organisation is nullable and formal is a FLAG: piece work, a
            -- family business and a season on a farm are all work history, and
            -- a schema that only accepts salaried employment quietly tells most
            -- of the country it has never worked.
            CREATE TABLE IF NOT EXISTS history (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                role         TEXT NOT NULL,
                organisation TEXT,
                formal       INTEGER NOT NULL DEFAULT 1,
                start_text   TEXT,
                end_text     TEXT,
                achievements TEXT NOT NULL DEFAULT '',
                ordinal      INTEGER NOT NULL DEFAULT 0
            );

            -- evidence_history_id ties a skill to WHERE IT WAS USED, so a CV can
            -- cite it instead of asserting a level nobody can check.
            CREATE TABLE IF NOT EXISTS skill (
                id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                name                TEXT NOT NULL,
                years               REAL,
                evidence_history_id INTEGER REFERENCES history(id) ON DELETE SET NULL
            );

            CREATE TABLE IF NOT EXISTS education (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                qualification TEXT NOT NULL,
                institution   TEXT,
                year          TEXT,
                completed     INTEGER NOT NULL DEFAULT 1
            );

            CREATE TABLE IF NOT EXISTS certification (
                id      INTEGER PRIMARY KEY AUTOINCREMENT,
                name    TEXT NOT NULL,
                issuer  TEXT,
                year    TEXT,
                expires TEXT
            );

            CREATE TABLE IF NOT EXISTS language (
                id    INTEGER PRIMARY KEY AUTOINCREMENT,
                name  TEXT NOT NULL,
                level TEXT
            );

            -- Specs are KEPT, not consumed. Applying to a similar job later
            -- should start from one that already worked.
            CREATE TABLE IF NOT EXISTS job_spec (
                id        INTEGER PRIMARY KEY AUTOINCREMENT,
                title     TEXT NOT NULL,
                employer  TEXT,
                body      TEXT NOT NULL,
                source    TEXT NOT NULL DEFAULT 'typed',
                added_utc TEXT NOT NULL
            );

            -- The document AND what went into it. selected_facts is why a second
            -- application can start from the first instead of from scratch.
            CREATE TABLE IF NOT EXISTS approved_document (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                spec_id        INTEGER REFERENCES job_spec(id) ON DELETE SET NULL,
                pdf            BLOB NOT NULL,
                selected_facts TEXT NOT NULL DEFAULT '',
                approved_utc   TEXT NOT NULL
            );
            """)
    }

    // MARK: - Profile

    public func load() -> CareerProfile {
        connection.lock.lock(); defer { connection.lock.unlock() }
        return CareerProfile(
            identity: loadIdentity(),
            history: loadHistory(),
            skills: loadSkills(),
            education: loadEducation(),
            certifications: loadCertifications(),
            languages: loadLanguages())
    }

    public func saveIdentity(_ identity: ProfileIdentity) {
        connection.lock.lock(); defer { connection.lock.unlock() }
        guard let stmt = try? connection.prepare("""
            UPDATE profile SET full_name = ?, headline = ?, phone = ?, email = ?,
                               location = ?, summary = ? WHERE id = 1
            """) else { return }
        defer { sqlite3_finalize(stmt) }
        connection.bind(stmt, [.text(identity.fullName), .text(identity.headline),
                               .optional(identity.phone), .optional(identity.email),
                               .optional(identity.location), .optional(identity.summary)])
        _ = sqlite3_step(stmt)
    }

    private func loadIdentity() -> ProfileIdentity {
        guard let stmt = try? connection.prepare("""
            SELECT full_name, headline, phone, email, location, summary
            FROM profile WHERE id = 1
            """) else { return ProfileIdentity() }
        defer { sqlite3_finalize(stmt) }
        guard sqlite3_step(stmt) == SQLITE_ROW else { return ProfileIdentity() }

        return ProfileIdentity(
            fullName: Self.text(stmt, 0) ?? "",
            headline: Self.text(stmt, 1) ?? "",
            phone: Self.text(stmt, 2),
            email: Self.text(stmt, 3),
            location: Self.text(stmt, 4),
            summary: Self.text(stmt, 5))
    }

    // MARK: - History

    @discardableResult
    public func addHistory(_ item: ProfileHistory, ordinal: Int = 0) -> Int64 {
        connection.lock.lock(); defer { connection.lock.unlock() }
        guard let stmt = try? connection.prepare("""
            INSERT INTO history (role, organisation, formal, start_text, end_text,
                                 achievements, ordinal)
            VALUES (?, ?, ?, ?, ?, ?, ?)
            """) else { return 0 }
        defer { sqlite3_finalize(stmt) }

        // Achievements are stored newline-separated rather than as JSON: they
        // are lines a person typed, and keeping them as lines means the column
        // is readable by anyone who ever opens this file with sqlite3.
        connection.bind(stmt, [
            .text(item.role), .optional(item.organisation), .int(item.formal ? 1 : 0),
            .optional(item.start), .optional(item.end),
            .text((item.achievements ?? []).joined(separator: "\n")), .int(ordinal)])
        guard sqlite3_step(stmt) == SQLITE_DONE else { return 0 }
        return sqlite3_last_insert_rowid(connection.db)
    }

    private func loadHistory() -> [ProfileHistory] {
        guard let stmt = try? connection.prepare("""
            SELECT id, role, organisation, formal, start_text, end_text, achievements
            FROM history ORDER BY ordinal, id
            """) else { return [] }
        defer { sqlite3_finalize(stmt) }

        var out: [ProfileHistory] = []
        while sqlite3_step(stmt) == SQLITE_ROW {
            let raw = Self.text(stmt, 6) ?? ""
            let achievements = raw.isEmpty
                ? []
                : raw.split(separator: "\n").map(String.init)
            out.append(ProfileHistory(
                role: Self.text(stmt, 1) ?? "",
                organisation: Self.text(stmt, 2),
                formal: sqlite3_column_int64(stmt, 3) != 0,
                start: Self.text(stmt, 4),
                end: Self.text(stmt, 5),
                achievements: achievements,
                id: sqlite3_column_int64(stmt, 0)))
        }
        return out
    }

    // MARK: - Skills, education, certifications, languages

    @discardableResult
    public func addSkill(_ skill: ProfileSkill) -> Int64 {
        insert(sql: "INSERT INTO skill (name, years, evidence_history_id) VALUES (?, ?, ?)",
               values: [.text(skill.name),
                        skill.years.map { SqlValue.double($0) } ?? .null,
                        skill.evidenceHistoryId.map { SqlValue.int(Int($0)) } ?? .null])
    }

    @discardableResult
    public func addEducation(_ e: ProfileEducation) -> Int64 {
        insert(sql: """
            INSERT INTO education (qualification, institution, year, completed)
            VALUES (?, ?, ?, ?)
            """,
            values: [.text(e.qualification), .optional(e.institution), .optional(e.year),
                     .int(e.completed ? 1 : 0)])
    }

    @discardableResult
    public func addCertification(_ c: ProfileCertification) -> Int64 {
        insert(sql: """
            INSERT INTO certification (name, issuer, year, expires) VALUES (?, ?, ?, ?)
            """,
            values: [.text(c.name), .optional(c.issuer), .optional(c.year),
                     .optional(c.expires)])
    }

    @discardableResult
    public func addLanguage(_ l: ProfileLanguage) -> Int64 {
        insert(sql: "INSERT INTO language (name, level) VALUES (?, ?)",
               values: [.text(l.name), .optional(l.level)])
    }

    private func loadSkills() -> [ProfileSkill] {
        select(sql: "SELECT id, name, years, evidence_history_id FROM skill ORDER BY id") { stmt in
            ProfileSkill(
                name: Self.text(stmt, 1) ?? "",
                years: sqlite3_column_type(stmt, 2) == SQLITE_NULL
                    ? nil : sqlite3_column_double(stmt, 2),
                evidenceHistoryId: sqlite3_column_type(stmt, 3) == SQLITE_NULL
                    ? nil : sqlite3_column_int64(stmt, 3),
                id: sqlite3_column_int64(stmt, 0))
        }
    }

    private func loadEducation() -> [ProfileEducation] {
        select(sql: """
            SELECT id, qualification, institution, year, completed FROM education ORDER BY id
            """) { stmt in
            ProfileEducation(
                qualification: Self.text(stmt, 1) ?? "",
                institution: Self.text(stmt, 2),
                year: Self.text(stmt, 3),
                completed: sqlite3_column_int64(stmt, 4) != 0,
                id: sqlite3_column_int64(stmt, 0))
        }
    }

    private func loadCertifications() -> [ProfileCertification] {
        select(sql: """
            SELECT id, name, issuer, year, expires FROM certification ORDER BY id
            """) { stmt in
            ProfileCertification(
                name: Self.text(stmt, 1) ?? "",
                issuer: Self.text(stmt, 2),
                year: Self.text(stmt, 3),
                expires: Self.text(stmt, 4),
                id: sqlite3_column_int64(stmt, 0))
        }
    }

    private func loadLanguages() -> [ProfileLanguage] {
        select(sql: "SELECT id, name, level FROM language ORDER BY id") { stmt in
            ProfileLanguage(name: Self.text(stmt, 1) ?? "",
                            level: Self.text(stmt, 2),
                            id: sqlite3_column_int64(stmt, 0))
        }
    }

    // MARK: - Job specs

    @discardableResult
    public func addSpec(_ spec: JobSpec, now: Date = Date()) -> Int64 {
        connection.lock.lock(); defer { connection.lock.unlock() }
        return insertLocked(sql: """
            INSERT INTO job_spec (title, employer, body, source, added_utc)
            VALUES (?, ?, ?, ?, ?)
            """,
            values: [.text(spec.title), .optional(spec.employer), .text(spec.text),
                     .text(spec.source),
                     .text(AtomLog.stamp.string(from: spec.added ?? now))])
    }

    /// Newest first: the spec somebody is working on is the one they just added.
    public func specs() -> [JobSpec] {
        connection.lock.lock(); defer { connection.lock.unlock() }
        return select(sql: """
            SELECT id, title, employer, body, source, added_utc
            FROM job_spec ORDER BY added_utc DESC, id DESC
            """) { stmt in
            JobSpec(title: Self.text(stmt, 1) ?? "",
                    employer: Self.text(stmt, 2),
                    text: Self.text(stmt, 3) ?? "",
                    source: Self.text(stmt, 4) ?? "typed",
                    added: AtomLog.stamp.date(from: Self.text(stmt, 5) ?? ""),
                    id: sqlite3_column_int64(stmt, 0))
        }
    }

    // MARK: - Approved documents

    @discardableResult
    public func approve(_ doc: ApprovedDocument, now: Date = Date()) -> Int64 {
        connection.lock.lock(); defer { connection.lock.unlock() }
        return insertLocked(sql: """
            INSERT INTO approved_document (spec_id, pdf, selected_facts, approved_utc)
            VALUES (?, ?, ?, ?)
            """,
            values: [doc.specId.map { SqlValue.int(Int($0)) } ?? .null,
                     .blob([UInt8](doc.pdf)),
                     .text(doc.selectedFacts.map(String.init).joined(separator: ",")),
                     .text(AtomLog.stamp.string(from: doc.approved))])
    }

    /// Every approval, newest first. Nothing is ever deleted here — the record
    /// of what was claimed, to whom, and when is the point.
    public func approvals(specId: Int64? = nil) -> [ApprovedDocument] {
        connection.lock.lock(); defer { connection.lock.unlock() }

        let sql = specId == nil
            ? """
              SELECT id, spec_id, pdf, selected_facts, approved_utc
              FROM approved_document ORDER BY approved_utc DESC, id DESC
              """
            : """
              SELECT id, spec_id, pdf, selected_facts, approved_utc
              FROM approved_document WHERE spec_id = ? ORDER BY approved_utc DESC, id DESC
              """
        let bind: [SqlValue] = specId.map { [.int(Int($0))] } ?? []

        return select(sql: sql, bind: bind) { stmt in
            var pdf = Data()
            if let bytes = sqlite3_column_blob(stmt, 2) {
                pdf = Data(bytes: bytes, count: Int(sqlite3_column_bytes(stmt, 2)))
            }
            let facts = (Self.text(stmt, 3) ?? "")
                .split(separator: ",")
                .compactMap { Int64($0) }

            return ApprovedDocument(
                specId: sqlite3_column_type(stmt, 1) == SQLITE_NULL
                    ? nil : sqlite3_column_int64(stmt, 1),
                pdf: pdf,
                selectedFacts: facts,
                approved: AtomLog.stamp.date(from: Self.text(stmt, 4) ?? "")
                    ?? Date(timeIntervalSince1970: 0),
                id: sqlite3_column_int64(stmt, 0))
        }
    }

    // MARK: - Helpers

    private func insert(sql: String, values: [SqlValue]) -> Int64 {
        connection.lock.lock(); defer { connection.lock.unlock() }
        return insertLocked(sql: sql, values: values)
    }

    private func insertLocked(sql: String, values: [SqlValue]) -> Int64 {
        guard let stmt = try? connection.prepare(sql) else { return 0 }
        defer { sqlite3_finalize(stmt) }
        connection.bind(stmt, values)
        guard sqlite3_step(stmt) == SQLITE_DONE else { return 0 }
        return sqlite3_last_insert_rowid(connection.db)
    }

    private func select<T>(sql: String, bind values: [SqlValue] = [],
                           _ read: (OpaquePointer) -> T) -> [T] {
        guard let stmt = try? connection.prepare(sql) else { return [] }
        defer { sqlite3_finalize(stmt) }
        connection.bind(stmt, values)

        var out: [T] = []
        while sqlite3_step(stmt) == SQLITE_ROW { out.append(read(stmt)) }
        return out
    }

    private static func text(_ stmt: OpaquePointer, _ column: Int32) -> String? {
        sqlite3_column_text(stmt, column).map { String(cString: $0) }
    }
}

#endif
