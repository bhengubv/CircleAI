// MemoryForgetting.swift
//
// Why a memory has to let go of things, and how — plus where it lives.
//
// A STORE THAT KEEPS EVERYTHING AT FULL VOLUME FOREVER IS A FILING CABINET.
// Ask it about deploying after a year and it hands back the same fifty things
// with the same confidence, and the one that matters is somewhere among them.
// Forgetting is the mechanism that makes recall useful, not a defect being
// worked around — and a phone needs it more than a server does.
//
// TWO STRENGTHS, NOT ONE, after Bjork. A single score that goes up when used
// and down when not gets the important case backwards.
//
//   STABILITY      — how deeply the thing is learned. It ONLY EVER GROWS.
//   RETRIEVABILITY — how reachable it is now. Decays with time, restored by
//                    being retrieved.
//
// Retrieving something NEARLY FORGOTTEN strengthens it far MORE than retrieving
// something fresh. That is the spacing effect, and here it falls out of the
// arithmetic — the gain is scaled by (1 - retrievability) — rather than being
// bolted on.
//
// NOTHING IS EVER DELETED. Fading means dropping out of what recall OFFERS; the
// log still has every line and the atom is still there by id.
//
// Ported from Forgetting.cs, MemoryWear.cs and MemoryFolder.cs.

import Foundation

/// How worn the path to one atom is, on this machine.
public struct MemoryTrace: Sendable, Equatable {
    public let retrievals: Int
    public let lastRetrievedUtc: Date
    public let stabilityDays: Double

    public init(retrievals: Int, lastRetrievedUtc: Date, stabilityDays: Double) {
        self.retrievals = retrievals
        self.lastRetrievedUtc = lastRetrievedUtc
        self.stabilityDays = stabilityDays
    }
}

public enum Forgetting {

    /// Three months. Long enough that nothing said this quarter fades, short
    /// enough that a year-old aside is not still being volunteered.
    public static let initialStabilityDays = 90.0

    /// Below this an atom stops being offered.
    public static let threshold = 0.05

    /// How much a retrieval at the edge of fading is worth over a fresh one.
    public static let spacingGain = 2.0

    /// How much each correction deepens the initial learning.
    public static let correctionGain = 0.9

    public static func retrievability(stabilityDays: Double, elapsed: TimeInterval) -> Double {
        guard stabilityDays > 0 else { return 0 }
        let days = max(elapsed / 86_400.0, 0)
        return exp(-days / stabilityDays)
    }

    /// The spacing effect, as arithmetic.
    ///
    /// Stability never falls: the max against the floor means a strengthening
    /// pass cannot make something LESS learned than a brand new atom.
    public static func strengthened(stabilityDays: Double, retrievability: Double) -> Double {
        let current = max(stabilityDays, initialStabilityDays)
        let wasNearlyGone = 1.0 - min(max(retrievability, 0), 1)
        return current * (1.0 + spacingGain * wasNearlyGone)
    }

    /// A repeatedly-corrected atom starts out more deeply learned, capped at
    /// six: past that the point is made, and an uncapped multiplier would make
    /// one much-corrected atom immortal.
    public static func initialStability(_ atom: MemoryAtom) -> Double {
        initialStabilityDays * (1.0 + correctionGain * Double(min(atom.corrections, 6)))
    }

    /// What refuses to fade.
    ///
    /// A RULE does not stop applying because nobody has mentioned it lately —
    /// that is exactly when it gets broken. Neither does how somebody wants to
    /// be spoken to. A decision or a fact can fade; a standing instruction
    /// cannot.
    public static func floorFor(_ kind: AtomKind) -> Double {
        switch kind {
        case .ruling, .relationship: return 0.40
        case .preference: return 0.20
        default: return 0.00
        }
    }

    public static func reach(_ atom: MemoryAtom, trace: MemoryTrace?, now: Date) -> Double {
        let stability = trace?.stabilityDays ?? initialStability(atom)
        // Never retrieved here: the clock starts at the last CORRECTION if
        // there was one, because being corrected is a stronger event than
        // being filed.
        let since = trace?.lastRetrievedUtc ?? atom.lastCorrectedUtc ?? atom.recordedAtUtc
        let decayed = retrievability(stabilityDays: stability, elapsed: now.timeIntervalSince(since))
        return max(decayed, floorFor(atom.kind))
    }

    public static func faded(_ atom: MemoryAtom, trace: MemoryTrace?, now: Date) -> Bool {
        reach(atom, trace: trace, now: now) < threshold
    }
}

/// How worn the path to each memory is, on THIS machine.
///
/// WEAR IS LOCAL AND IT IS NOT MEMORY. What was decided is shared — it goes in
/// the log and travels by git, and all three machines see it. How often
/// somebody reached for it HERE is a different thing entirely: my use of a
/// memory strengthens my access to it, not yours. Syncing wear would mean one
/// machine's habits deciding what another finds easy to bring to mind.
///
/// Losing it costs FAMILIARITY, not knowledge. Everything still recalls; it
/// just recalls the way it did the first week.
public final class MemoryWear: @unchecked Sendable {

    private let lock = NSLock()
    private var traces: [UUID: MemoryTrace] = [:]
    private var dirty = false

    public init() {}

    public var count: Int {
        lock.lock(); defer { lock.unlock() }
        return traces.count
    }

    public var isDirty: Bool {
        lock.lock(); defer { lock.unlock() }
        return dirty
    }

    public func forAtom(_ id: UUID) -> MemoryTrace? {
        lock.lock(); defer { lock.unlock() }
        return traces[id]
    }

    public func reach(_ atom: MemoryAtom, now: Date) -> Double {
        Forgetting.reach(atom, trace: forAtom(atom.id), now: now)
    }

    public func faded(_ atom: MemoryAtom, now: Date) -> Bool {
        Forgetting.faded(atom, trace: forAtom(atom.id), now: now)
    }

    public func retrieved(_ atom: MemoryAtom, now: Date) {
        // The reach is measured BEFORE the trace is updated — that is what makes
        // the spacing effect work. Measure it after and every retrieval looks
        // fresh, so nothing ever gains anything.
        let existing = forAtom(atom.id)
        let reach = Forgetting.reach(atom, trace: existing, now: now)
        let stability = Forgetting.strengthened(
            stabilityDays: existing?.stabilityDays ?? Forgetting.initialStability(atom),
            retrievability: reach)

        lock.lock()
        traces[atom.id] = MemoryTrace(
            retrievals: (existing?.retrievals ?? 0) + 1,
            lastRetrievedUtc: now,
            stabilityDays: stability)
        dirty = true
        lock.unlock()
    }

    public func retrieved(_ atoms: [MemoryAtom], now: Date) {
        for atom in atoms { retrieved(atom, now: now) }
    }

    public func clear() {
        lock.lock(); defer { lock.unlock() }
        guard !traces.isEmpty else { return }
        traces.removeAll()
        dirty = true
    }

    /// The rows as they go to disk, keyed by atom id.
    public func snapshot() -> [UUID: MemoryTrace] {
        lock.lock(); defer { lock.unlock() }
        return traces
    }

    public func restore(_ rows: [UUID: MemoryTrace]) {
        lock.lock(); defer { lock.unlock() }
        traces = rows
        dirty = false
    }

    public func markClean() {
        lock.lock(); dirty = false; lock.unlock()
    }
}

/// Where memory lives, and which machine is writing to it.
///
/// THREE MACHINES, ONE MEMORY. The memory directory is a symlink into a git
/// repository, so it travels by pull and push like everything else.
///
/// THAT DECIDES THE FILE LAYOUT, not taste. A SQLite database is a binary blob
/// and git cannot merge one — two machines writing the same day produce a
/// conflict whose only resolutions are "keep mine" and "keep theirs", and both
/// destroy memory. So the durable thing is an append-only text log, and there
/// is ONE PER MACHINE: a file with a single writer can never conflict, which is
/// a stronger guarantee than any merge strategy.
public struct MemoryFolder: Sendable {

    public let path: String
    public let machine: String

    public init(path: String, machine: String? = nil) throws {
        guard !path.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw MemoryFolderError.pathRequired
        }
        let resolved = (path as NSString).expandingTildeInPath
        self.path = URL(fileURLWithPath: resolved).standardizedFileURL.path
        try FileManager.default.createDirectory(
            atPath: self.path, withIntermediateDirectories: true)

        var name = Self.sanitise(machine ?? Self.defaultMachineName())

        // A HOST NAME THAT IDENTIFIES NOTHING IS WORSE THAN NO HOST NAME. Every
        // Android device reports "localhost", so two phones would both call
        // themselves android-localhost and append to ONE log — which is the
        // merge problem this whole layout exists to avoid, arriving through the
        // front door. Found by running it on a P30.
        //
        // The condition is the NAME, not where it came from: a caller passing
        // "android-unnamed" is saying the same thing the environment said.
        if name.hasSuffix(Self.anonymous) {
            let stem = String(name.dropLast(Self.anonymous.count))
            name = stem + "-" + Self.installed(in: self.path)
        }
        self.machine = name
    }

    public var ownLog: String {
        (path as NSString).appendingPathComponent("atoms.\(machine).jsonl")
    }

    /// Every machine's log, in a stable order so a rebuild is reproducible.
    public var allLogs: [String] {
        let fm = FileManager.default
        guard let names = try? fm.contentsOfDirectory(atPath: path) else { return [] }
        return names
            .filter { $0.hasPrefix("atoms.") && $0.hasSuffix(".jsonl") }
            .sorted()
            .map { (path as NSString).appendingPathComponent($0) }
    }

    public var indexPath: String {
        (path as NSString).appendingPathComponent("index.\(machine).db")
    }

    public func ensureGitIgnore() throws {
        let file = (path as NSString).appendingPathComponent(".gitignore")
        guard !FileManager.default.fileExists(atPath: file) else { return }
        try Self.gitIgnore.write(toFile: file, atomically: true, encoding: .utf8)
    }

    public static let anonymous = "-unnamed"

    public static let gitIgnore = """
        # Derived, not memory. Rebuilt from the logs on demand.
        index.*.db
        index.*.db-wal
        index.*.db-shm

        # This machine's name for itself. Per-machine by definition - sharing it
        # would put two machines back in one log.
        .machine-id

        # How worn the paths are HERE. What was decided is shared; how often
        # somebody reached for it on this machine is not, and syncing it would
        # put one machine's habits in charge of what another finds easy to
        # bring to mind.
        wear.*.json
        wear.*.json.tmp
        """

    public static func defaultMachineName(host: String? = nil, platform: String? = nil) -> String {
        let plat: String
        if let platform {
            plat = platform
        } else {
            #if os(macOS)
            plat = "mac"
            #elseif os(iOS) || os(watchOS) || os(tvOS)
            plat = "ios"
            #elseif os(Linux)
            plat = "linux"
            #elseif os(Windows)
            plat = "windows"
            #else
            plat = "other"
            #endif
        }

        let name = host ?? ProcessInfo.processInfo.hostName

        // "localhost" is what every Android device answers, and an empty or
        // unknown name is no better. Say so plainly and let the caller settle it.
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed.isEmpty ||
            trimmed.lowercased() == "localhost" ||
            trimmed.lowercased() == "unknown" {
            return plat + anonymous
        }
        return "\(plat)-\(trimmed)"
    }

    /// A machine id that survives restarts, minted once into the folder.
    ///
    /// A read-only folder still has to work: the fallback is not stable across
    /// runs, which is worse than a file and far better than a collision with
    /// every other device.
    static func installed(in folder: String) -> String {
        let file = (folder as NSString).appendingPathComponent(".machine-id")
        if let existing = try? String(contentsOfFile: file, encoding: .utf8) {
            let trimmed = existing.trimmingCharacters(in: .whitespacesAndNewlines)
            if !trimmed.isEmpty { return sanitise(trimmed) }
        }
        let minted = String(UUID().uuidString.replacingOccurrences(of: "-", with: "").prefix(8)).lowercased()
        try? minted.write(toFile: file, atomically: true, encoding: .utf8)
        return minted
    }

    /// A file name, not a host name: anything else becomes a hyphen.
    static func sanitise(_ name: String) -> String {
        let cleaned = String(name.trimmingCharacters(in: .whitespacesAndNewlines).lowercased().map {
            $0.isLetter || $0.isNumber || $0 == "-" || $0 == "_" ? $0 : "-"
        }).trimmingCharacters(in: CharacterSet(charactersIn: "-"))
        return cleaned.isEmpty ? "unknown" : cleaned
    }
}

public enum MemoryFolderError: Error, Equatable, CustomStringConvertible {
    case pathRequired
    public var description: String { "A memory folder path is required." }
}
