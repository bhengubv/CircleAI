// MemoryAtomLog.swift
//
// The durable half, what gets kept out of what was spotted, and turning three
// machines' logs into one index.
//
// THE LOG FILE FORMAT OUTLIVES THIS CODE. It is what crosses between a Linux
// box, a Windows box and a Mac, what a person can open and read, and what any
// other tool would have to understand. The database is a CACHE of it.
//
// APPEND-ONLY CHANGES THE MODEL. A row in a table can be UPDATEd to say it was
// superseded; a line already written cannot. So a correction is a NEW line
// naming what it SUPERSEDES, and the forward pointer is derived on replay —
// which is also what makes two machines' logs mergeable by concatenation.
//
// Ported from AtomLog.cs, AtomLearner.cs, MemorySync.cs and Recall.cs.

import Foundation

public struct AtomRecord: Sendable, Equatable, Codable {
    public var id: String
    public var kind: String
    public var text: String
    public var subject: String?
    public var challenge: String?
    public var outcome: String?
    public var recorded: String
    public var machine: String
    public var sourceEpisode: String?
    public var supersedes: String?
    public var verify: String?

    enum CodingKeys: String, CodingKey {
        case id, kind, text, subject, challenge, outcome, recorded, machine, verify
        case sourceEpisode = "source"
        case supersedes
    }

    public init(
        id: String = "", kind: String = "Decision", text: String = "",
        subject: String? = nil, challenge: String? = nil, outcome: String? = nil,
        recorded: String = "", machine: String = "", sourceEpisode: String? = nil,
        supersedes: String? = nil, verify: String? = nil
    ) {
        self.id = id
        self.kind = kind
        self.text = text
        self.subject = subject
        self.challenge = challenge
        self.outcome = outcome
        self.recorded = recorded
        self.machine = machine
        self.sourceEpisode = sourceEpisode
        self.supersedes = supersedes
        self.verify = verify
    }
}

public struct AtomLog: Sendable {

    private let folder: MemoryFolder

    public init(folder: MemoryFolder) { self.folder = folder }

    /// ISO-8601 with fractional seconds, the same shape the C# round-trip
    /// format writes, so one log is readable by both.
    static let stamp: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        f.timeZone = TimeZone(secondsFromGMT: 0)
        return f
    }()

    static let stampNoFraction: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime]
        f.timeZone = TimeZone(secondsFromGMT: 0)
        return f
    }()

    @discardableResult
    public func append(_ atom: MemoryAtom, supersedes: UUID? = nil) throws -> AtomRecord {
        let record = AtomRecord(
            id: Self.compact(atom.id),
            kind: atom.kind.wireName,
            text: atom.text,
            subject: atom.subject,
            challenge: atom.challenge,
            outcome: atom.outcome?.wireName,
            recorded: Self.stamp.string(from: atom.recordedAtUtc),
            machine: folder.machine,
            sourceEpisode: atom.sourceEpisode.map(Self.compact),
            supersedes: supersedes.map(Self.compact),
            verify: atom.verify)

        let encoder = JSONEncoder()
        encoder.outputFormatting = [.withoutEscapingSlashes]
        let data = try encoder.encode(record)
        guard var line = String(data: data, encoding: .utf8) else {
            throw AtomLogError.unencodable
        }

        // ONE LINE, ONE WRITE, and a newline first only if the file does not
        // already end in one — a half-written line from an interrupted session
        // would otherwise swallow the next record into itself.
        let fm = FileManager.default
        let path = folder.ownLog
        if fm.fileExists(atPath: path),
           let existing = try? Data(contentsOf: URL(fileURLWithPath: path)),
           let last = existing.last, last != 0x0A {
            line = "\n" + line
        }
        line += "\n"

        if let handle = FileHandle(forWritingAtPath: path) {
            defer { try? handle.close() }
            try handle.seekToEnd()
            try handle.write(contentsOf: Data(line.utf8))
        } else {
            try line.write(toFile: path, atomically: true, encoding: .utf8)
        }
        return record
    }

    /// Every record from every machine, in ONE order.
    ///
    /// The machine name and then the id break ties, so replay is identical on
    /// all three boxes: two records with the same timestamp must not order
    /// differently depending on which machine read them.
    public func readAll() -> [AtomRecord] {
        var records: [AtomRecord] = []
        let decoder = JSONDecoder()

        for path in folder.allLogs {
            guard let text = try? String(contentsOfFile: path, encoding: .utf8) else { continue }
            for line in text.split(separator: "\n", omittingEmptySubsequences: true) {
                let trimmed = line.trimmingCharacters(in: .whitespacesAndNewlines)
                if trimmed.isEmpty { continue }
                // An unreadable line costs only ITSELF: one truncated write must
                // not cost every memory in the file behind it.
                guard let data = trimmed.data(using: .utf8),
                      let record = try? decoder.decode(AtomRecord.self, from: data),
                      !record.id.trimmingCharacters(in: .whitespaces).isEmpty else { continue }
                records.append(record)
            }
        }

        return records.sorted { a, b in
            let ta = Self.time(a.recorded), tb = Self.time(b.recorded)
            if ta != tb { return ta < tb }
            if a.machine != b.machine { return a.machine < b.machine }
            return a.id < b.id
        }
    }

    public static func rehydrate(_ record: AtomRecord) -> MemoryAtom {
        MemoryAtom(
            id: parseCompact(record.id) ?? UUID(),
            kind: AtomKind.fromWire(record.kind) ?? .decision,
            text: record.text,
            subject: record.subject,
            sourceEpisode: record.sourceEpisode.flatMap(parseCompact),
            recordedAtUtc: time(record.recorded),
            machine: record.machine,
            challenge: record.challenge,
            outcome: DecisionOutcome.fromWire(record.outcome),
            verify: record.verify)
    }

    /// An unparseable timestamp sorts FIRST rather than throwing: a line with a
    /// broken date is still a memory, and the start of the replay is the least
    /// surprising place for it.
    public static func time(_ raw: String) -> Date {
        if let d = stamp.date(from: raw) { return d }
        if let d = stampNoFraction.date(from: raw) { return d }
        return Date.distantPast
    }

    /// 32 hex characters, no hyphens — the form the C# writes.
    public static func compact(_ id: UUID) -> String {
        id.uuidString.replacingOccurrences(of: "-", with: "").lowercased()
    }

    public static func parseCompact(_ s: String) -> UUID? {
        let hex = s.replacingOccurrences(of: "-", with: "")
        guard hex.count == 32 else { return nil }
        let c = Array(hex)
        let dashed = String(c[0..<8]) + "-" + String(c[8..<12]) + "-" + String(c[12..<16])
            + "-" + String(c[16..<20]) + "-" + String(c[20..<32])
        return UUID(uuidString: dashed)
    }
}

public enum AtomLogError: Error, Equatable {
    case unencodable
}

// MARK: - Learning

public struct LearnReport: Sendable, Equatable {
    public let considered: Int
    public let recorded: [AtomCandidate]
    public let alreadyKnown: [AtomCandidate]
    public let offered: [AtomCandidate]

    public init(considered: Int, recorded: [AtomCandidate],
                alreadyKnown: [AtomCandidate], offered: [AtomCandidate]) {
        self.considered = considered
        self.recorded = recorded
        self.alreadyKnown = alreadyKnown
        self.offered = offered
    }

    public static let nothing = LearnReport(
        considered: 0, recorded: [], alreadyKnown: [], offered: [])
}

/// What gets KEPT, out of what was spotted.
///
/// THIS IS THE HALF THAT DECIDES, and it is separate from the half that spots
/// so that "what did you see" and "what did you keep" are two questions with
/// two answers. An extractor that also committed would make a wrong reading
/// unfalsifiable: there would be nothing to look at but the atom it produced.
///
/// TWICE MUST NOT MEAN TWO. Running this over the same conversation again —
/// after a crash, a pull, or simply a second pass — has to be the same as
/// running it once.
public struct AtomLearner: Sendable {

    private let extractor: any IAtomExtractor

    public init(extractor: (any IAtomExtractor)? = nil) {
        self.extractor = extractor ?? CueExtractor()
    }

    public var extractorName: String { extractor.name }

    /// The convenience form: a snapshot of what is already remembered.
    public func learn(
        episodes: [EpisodicMemoryEntry],
        record: (MemoryAtom) async throws -> Void,
        known: [MemoryAtom],
        subject: String? = nil
    ) async rethrows -> LearnReport {
        let already = Set(known.map { CueExtractor.normalise($0.text) })
        return try await learn(
            episodes: episodes,
            record: record,
            knows: { already.contains(CueExtractor.normalise($0)) },
            subject: subject)
    }

    public func learn(
        episodes: [EpisodicMemoryEntry],
        record: (MemoryAtom) async throws -> Void,
        knows: (String) async throws -> Bool,
        subject: String? = nil
    ) async rethrows -> LearnReport {
        // Still a SET as well, because two identical sentences in ONE pass are
        // not yet in any store and would otherwise both be kept.
        var seen = Set<String>()
        var considered = 0
        var recorded: [AtomCandidate] = []
        var alreadyKnown: [AtomCandidate] = []
        var offered: [AtomCandidate] = []

        // OLDEST FIRST, so that when two passes over the same conversation
        // produce the same sentence twice, the one kept is the one said first —
        // and a rebuild lands on the same atom either way.
        for episode in episodes.sorted(by: { $0.recordedAt < $1.recordedAt }) {
            for candidate in extractor.extract(episode, subject: subject) {
                considered += 1

                // ALREADY KNOWN BEATS NOT SURE ENOUGH. A sentence that is
                // already remembered is not a question for anybody, however
                // faintly it was spotted.
                let normalised = CueExtractor.normalise(candidate.atom.text)
                let fresh = seen.insert(normalised).inserted
                // Hoisted out of the condition: Swift's `||` takes an
                // autoclosure, which cannot carry an await. Written this way it
                // also keeps the short-circuit - the store is only asked about
                // a sentence this pass has not already seen.
                var isKnown = false
                if fresh { isKnown = try await knows(candidate.atom.text) }
                if !fresh || isKnown {
                    alreadyKnown.append(candidate)
                    continue
                }

                if !candidate.certain {
                    offered.append(candidate)
                    continue
                }

                try await record(candidate.atom)
                recorded.append(candidate)
            }
        }

        return LearnReport(considered: considered, recorded: recorded,
                           alreadyKnown: alreadyKnown, offered: offered)
    }

    /// What was spotted, without keeping any of it.
    public func read(_ episode: EpisodicMemoryEntry, subject: String? = nil) -> [AtomCandidate] {
        extractor.extract(episode, subject: subject)
    }
}

// MARK: - Sync

public struct SyncReport: Sendable, Equatable {
    public let records: Int
    public let atoms: Int
    public let current: Int
    public let machines: Int

    public init(records: Int, atoms: Int, current: Int, machines: Int) {
        self.records = records
        self.atoms = atoms
        self.current = current
        self.machines = machines
    }
}

/// Turning three machines' logs into ONE local index.
///
/// THE INDEX IS DISPOSABLE AND THE LOGS ARE NOT. Replay rebuilds the database
/// from the text, so a corrupt index, a schema change, or a machine that has
/// never seen the folder all cost the same thing: a rebuild.
///
/// SUPERSEDING IS RESOLVED HERE, not in the log. A log line can only point
/// BACKWARDS at what it replaces; the forward pointer the index wants is worked
/// out by walking the records in time order. That is also what makes a
/// correction on the Mac apply to a decision made on Windows.
public struct MemorySync: Sendable {

    public let log: AtomLog

    public init(folder: MemoryFolder) {
        self.log = AtomLog(folder: folder)
    }

    public func record(
        store: any IAtomStore, atom: MemoryAtom, supersedes: UUID? = nil
    ) async throws {
        // INDEX WHAT THE LOG SAYS, not what the caller passed. The line is
        // stamped with this machine and normalised on the way out, and reading
        // it back is what makes "the index now" and "the index after a rebuild"
        // the same thing without two pieces of code having to agree.
        let stored = AtomLog.rehydrate(try log.append(atom, supersedes: supersedes))
        if let old = supersedes {
            _ = try await store.supersede(old, with: stored)
        } else {
            try await store.add(stored)
        }
    }

    @discardableResult
    public func rebuild(into store: any IAtomStore) async throws -> SyncReport {
        let replayed = replay()
        guard replayed.records > 0 else { return SyncReport(records: 0, atoms: 0, current: 0, machines: 0) }

        var stored = 0
        for atom in replayed.atoms {
            try await store.add(atom)
            stored += 1
        }

        return SyncReport(
            records: replayed.records,
            atoms: stored,
            current: replayed.atoms.filter(\.isCurrent).count,
            machines: replayed.machines)
    }

    public func current() -> [MemoryAtom] {
        replay().atoms.filter(\.isCurrent)
    }

    public struct Replayed: Sendable {
        public let records: Int
        public let machines: Int
        public let atoms: [MemoryAtom]
    }

    public func replay() -> Replayed {
        let records = log.readAll()
        guard !records.isEmpty else { return Replayed(records: 0, machines: 0, atoms: []) }

        var atoms: [String: MemoryAtom] = [:]
        var order: [String] = []
        var supersededBy: [String: String] = [:]
        var corrections: [String: Int] = [:]
        var correctedAt: [String: Date] = [:]

        for record in records {
            if let old = record.supersedes, !old.isEmpty {
                supersededBy[old] = record.id
                // THE COUNT CARRIES DOWN THE CHAIN, so an atom corrected on
                // three different machines reads as corrected three times
                // rather than once each.
                corrections[record.id] = (corrections[old] ?? 0) + 1
                correctedAt[record.id] = AtomLog.time(record.recorded)
            }
            if atoms[record.id] == nil { order.append(record.id) }
            atoms[record.id] = AtomLog.rehydrate(record)
        }

        let finished: [MemoryAtom] = order.compactMap { key in
            guard var atom = atoms[key] else { return nil }
            atom.corrections = corrections[key] ?? 0
            atom.lastCorrectedUtc = correctedAt[key]
            atom.supersededBy = supersededBy[key].flatMap(AtomLog.parseCompact)
            return atom
        }

        let machines = Set(records.map { $0.machine.lowercased() }).count
        return Replayed(records: records.count, machines: machines, atoms: finished)
    }
}
