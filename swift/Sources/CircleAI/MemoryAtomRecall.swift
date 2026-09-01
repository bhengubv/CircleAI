// MemoryAtomRecall.swift
//
// What to put in front of the agent, at the moment it is about to act — plus
// the memory an application actually holds, and a module's view of it.
//
// THE STORE FINDS CANDIDATES; RECALL DECIDES WHAT IS WORTH THE SPACE. Keeping
// the ranking out of the store is what lets the same policy run over SQLite on
// a phone and PostgreSQL on a server without either engine's SQL encoding the
// judgement.
//
// THE KINDS ARE WEIGHTS, NOT GATES. Nothing here blocks anything. A ruling
// outranks a preference when both match; a fact that failed its last check is
// still returned, carrying the doubt.
//
// Ported from Recall.cs, MemoryService.cs, ModuleMemory.cs and HookPayload.cs.

import Foundation

/// NAMED IAtomRecall, NOT IRecall. Swift is one module where C# has
/// namespaces, and the vector side already owns the bare name for a different
/// contract entirely — that one answers `recall(query:queryEmbedding:topK:)`
/// with `[MemoryHit]`, this one answers a SITUATION with a ranked, budgeted
/// `RecallResult`. Two different questions; the name goes to whichever asked
/// first, and this one takes the suffix rather than fighting for it.
public protocol IAtomRecall: Sendable {
    func recall(_ situation: Situation, budget: RecallBudget?) async throws -> RecallResult
}

public struct Recall: IAtomRecall {

    private let atoms: any IAtomStore
    public let wear: MemoryWear?

    public init(atoms: any IAtomStore, wear: MemoryWear? = nil) {
        self.atoms = atoms
        self.wear = wear
    }

    public func recall(_ situation: Situation, budget: RecallBudget? = nil) async throws -> RecallResult {
        guard !situation.isEmpty else { return .empty }
        let cap = budget ?? .default

        // Ask for MORE than the budget: ranking only means something if there
        // was a choice, and the store's ordering is by subject match rather
        // than by what matters here.
        let candidates = try await atoms.match(situation, limit: max(cap.maxAtoms * 4, 20))
        let now = Date()

        // TONE IS NOT SITUATIONAL, and fetching it from the situation match was
        // wrong. "Blunt, hates being asked twice" applies to answering about
        // deploying exactly as much as to anything else — it describes the
        // PERSON, not the subject. Filed under its own topic it simply never
        // matched, so the manner vanished the moment the work got specific,
        // which is precisely when it matters most.
        let tone = try await atoms.byKind(.relationship, limit: 8)
            .sorted { a, b in
                a.corrections != b.corrections
                    ? a.corrections > b.corrections
                    : a.recordedAtUtc > b.recordedAtUtc
            }
            .prefix(3)
            .map { $0 }

        if candidates.isEmpty {
            return tone.isEmpty ? .empty : RecallResult(atoms: [], tone: tone, considered: 0)
        }

        // WHAT HAS FADED IS NOT OFFERED. It is not gone — the log still has
        // every line and the atom is still there by id — it simply stops being
        // volunteered.
        let ranked = candidates.enumerated()
            .filter { $0.element.kind != .relationship }
            .filter { wear == nil || !wear!.faded($0.element, now: now) }
            .map { (atom: $0.element,
                    score: score($0.element, situation: situation, now: now)
                        + found(position: $0.offset, total: candidates.count)) }
            .sorted { a, b in
                a.score != b.score ? a.score > b.score : a.atom.recordedAtUtc > b.atom.recordedAtUtc
            }
            .map(\.atom)

        var chosen: [MemoryAtom] = []
        var characters = 0
        for atom in ranked {
            if chosen.count >= cap.maxAtoms { break }
            // A single long atom must not eat the whole budget and starve three
            // short ones that would have been more use together. SKIPPED, not
            // stopped at — the next one may well fit.
            let cost = atom.text.count
            if characters + cost > cap.maxCharacters && !chosen.isEmpty { continue }
            chosen.append(atom)
            characters += cost
        }

        // BRINGING SOMETHING TO MIND IS WHAT MAKES IT STICK. Only what was
        // actually handed back counts: an atom that matched and lost on ranking
        // was not remembered, it was passed over.
        wear?.retrieved(chosen, now: now)

        return RecallResult(atoms: chosen, tone: tone, considered: candidates.count)
    }

    /// A small nudge for what the store put first. A tiebreak, not a ranking:
    /// the store's ordering knows about subject match and nothing about what
    /// kind of thing this is or how badly it went last time.
    func found(position: Int, total: Int) -> Double {
        total <= 1 ? 0 : 0.12 * (1.0 - Double(position) / Double(total - 1))
    }

    func score(_ atom: MemoryAtom, situation: Situation, now: Date) -> Double {
        var score: Double
        switch atom.kind {
        case .ruling: score = 1.00
        case .decision: score = 0.90
        case .fact: score = 0.80
        case .preference: score = 0.55
        default: score = 0.00
        }

        // A ROAD ALREADY TRIED AND FOUND CLOSED goes near the top. Knowing what
        // failed is worth as much as knowing what worked, and it arrives too
        // late by default: the whole cost of a repeated mistake is paid before
        // anybody remembers making it the first time.
        if atom.failed { score += 0.25 }

        // CAPPED at four: after that the point is made, and without a cap one
        // much-corrected atom would crowd out everything else forever.
        score += Double(min(atom.corrections, 4)) * 0.18

        if let subject = atom.subject, !subject.isEmpty {
            let keys = situation.keys
            if let depth = keys.firstIndex(where: { $0.lowercased() == subject.lowercased() }) {
                // Exact key first, then the broader ones it rolls up to.
                score += depth == 0 ? 0.50 : 0.30
            }
        }

        // HOW REACHABLE IT IS, which replaced a plain recency term. Recency
        // said "newer is better" and nothing else; this says "what you have
        // been using is easier to bring to mind, and what you have not is
        // fading" — and it is the same arithmetic that decides what has faded
        // out altogether, rather than a second opinion about the same thing.
        score += 0.30 * (wear?.reach(atom, now: now) ?? Forgetting.reach(atom, trace: nil, now: now))

        // A fact that failed its own check is still returned, carrying the doubt.
        if atom.isStale { score -= 0.35 }

        return score
    }
}

// MARK: - The service

public protocol IMemoryService: Sendable {
    func recall(_ situation: Situation, budget: RecallBudget?) async throws -> RecallResult
    func remember(_ atom: MemoryAtom, supersedes: UUID?) async throws
    func learn(_ wasSaid: String, subject: String?) async throws -> LearnReport
    func all(limit: Int) async throws -> [MemoryAtom]
    func count() async throws -> Int
}

/// The memory an application actually holds.
///
/// IT IS BUILT FOR BEING KILLED, because on a phone that is the ordinary case:
/// the system takes the app for memory, the person swipes it away, the battery
/// goes. So nothing is held back. Atoms reach the log the moment they are
/// recorded, and wear is written on the way OUT of every recall — not on a
/// timer, and not on a lifecycle callback, both of which a force-stop walks
/// straight past.
///
/// ONE STORE, GUARDED. Memory operations are not a parallel workload, so they
/// are serialised rather than made clever: the alternative is a torn read on a
/// connection two threads are using, which fails rarely and unreproducibly.
///
/// NO PLATFORM IN HERE. It takes a folder path and a store, so the same service
/// is what a phone holds, what a server holds, and what a test holds.
public actor MemoryService: IMemoryService {

    private let folder: MemoryFolder
    private let sync: MemorySync
    private let wear = MemoryWear()
    private let store: any IAtomStore
    private let recaller: Recall
    private let learner = AtomLearner()
    private let wearStore: (@Sendable ([UUID: MemoryTrace]) -> Void)?

    /// - Parameter store: the index. The caller opens it, because opening a
    ///   database is the one platform-shaped thing here and this actor must not
    ///   know which one it is.
    public init(
        folderPath: String,
        store: any IAtomStore,
        machine: String? = nil,
        wearStore: (@Sendable ([UUID: MemoryTrace]) -> Void)? = nil
    ) throws {
        self.folder = try MemoryFolder(path: folderPath, machine: machine)
        try self.folder.ensureGitIgnore()
        self.sync = MemorySync(folder: folder)
        self.store = store
        self.recaller = Recall(atoms: store, wear: wear)
        self.wearStore = wearStore
    }

    public nonisolated var path: String { folder.path }
    public nonisolated var machineName: String { folder.machine }
    public nonisolated var log: AtomLog { sync.log }

    /// Replays every machine's log into the index.
    @discardableResult
    public func rebuild() async throws -> SyncReport {
        try await sync.rebuild(into: store)
    }

    public func recall(_ situation: Situation, budget: RecallBudget? = nil) async throws -> RecallResult {
        let result = try await recaller.recall(situation, budget: budget)
        // WRITTEN NOW, NOT LATER. Recall is the only thing that changes wear,
        // and holding it back would mean a force-stop taking the session's
        // familiarity with it.
        flushWear()
        return result
    }

    public func remember(_ atom: MemoryAtom, supersedes: UUID? = nil) async throws {
        // Straight through to the log, which is the durable half. Nothing is
        // queued, so nothing is lost when the app goes away.
        try await sync.record(store: store, atom: atom, supersedes: supersedes)
    }

    public func learn(_ wasSaid: String, subject: String? = nil) async throws -> LearnReport {
        guard !wasSaid.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            return .nothing
        }
        let episode = EpisodicMemoryEntry(
            userText: wasSaid, assistantText: "", appContext: subject)
        // Asked with an INDEX rather than handed the whole memory: this runs on
        // every turn of a conversation.
        return try await learner.learn(
            episodes: [episode],
            record: { [sync, store] atom in try await sync.record(store: store, atom: atom) },
            knows: { [store] text in try await store.knows(text) },
            subject: subject)
    }

    public func all(limit: Int = 200) async throws -> [MemoryAtom] {
        try await store.all(includeSuperseded: false, limit: limit)
    }

    public func count() async throws -> Int {
        try await store.count()
    }

    private func flushWear() {
        guard wear.isDirty else { return }
        wearStore?(wear.snapshot())
        wear.markClean()
    }
}

// MARK: - Module memory

/// What a module is allowed to KEEP.
///
/// rulesOnly is not "no memory". A live interpreter must never retain what
/// passes through it, because those are two other people's words; a safety gate
/// must never remember that something was allowed, because being talked past
/// once would then buy you past it forever. But "never keep this" is ITSELF a
/// thing that has to be remembered, and a module with no continuity cannot
/// remember its own prohibition.
///
/// So the line is not which modules have memory. It is WHAT THEY HOLD.
public enum MemoryRetention: Int, Sendable, Equatable, CaseIterable {
    case everything = 0
    case rulesOnly = 1
}

public protocol IModuleMemory: Sendable {
    var module: String { get }
    var retention: MemoryRetention { get }
    func recall(_ situation: Situation, budget: RecallBudget?) async throws -> RecallResult
    func remember(_ atom: MemoryAtom, supersedes: UUID?) async throws -> Bool
    func heard(_ said: String, subject: String?) async throws -> LearnReport
}

/// A module's own view of the memory the device holds.
///
/// MEMORY IS A SERVICE EVERY MODULE CONSUMES, not a feature one app has. There
/// is one memory on a device and a hundred and fifty things that might want it.
///
/// THE GUARANTEE IS IN THE REGISTRATION, NOT IN THE MEMORY. The retention a
/// module was built with is declared where it is constructed, so it holds even
/// on a device whose memory was wiped or edited. A rule that could be forgotten
/// is not a rule, and a prohibition that fails open is worse than none at all.
public struct ModuleMemory: IModuleMemory {

    private let memory: any IMemoryService
    public let module: String
    public let retention: MemoryRetention

    public init(
        memory: any IMemoryService,
        module: String,
        retention: MemoryRetention = .everything
    ) throws {
        guard !module.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw ModuleMemoryError.moduleRequired
        }
        self.memory = memory
        self.module = module.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
        self.retention = retention
    }

    public func recall(_ situation: Situation, budget: RecallBudget? = nil) async throws -> RecallResult {
        try await memory.recall(situation, budget: budget)
    }

    @discardableResult
    public func remember(_ atom: MemoryAtom, supersedes: UUID? = nil) async throws -> Bool {
        guard mayKeep(atom.kind) else { return false }
        try await memory.remember(owned(atom), supersedes: supersedes)
        return true
    }

    public func heard(_ said: String, subject: String? = nil) async throws -> LearnReport {
        // A module that must not retain what passes through it does not extract
        // from it either. The words never reach the learner.
        if retention == .rulesOnly { return .nothing }
        return try await memory.learn(said, subject: subject ?? module)
    }

    func mayKeep(_ kind: AtomKind) -> Bool {
        retention == .everything || kind == .ruling || kind == .preference || kind == .relationship
    }

    /// PREFIXED rather than replaced, so "interpret:languages" still rolls up
    /// to "interpret" and a module's whole memory can be read at once.
    func owned(_ atom: MemoryAtom) -> MemoryAtom {
        var copy = atom
        if let subject = atom.subject, !subject.isEmpty {
            copy.subject = subject.hasPrefix(module + ":") ? subject : "\(module):\(subject)"
        } else {
            copy.subject = module
        }
        return copy
    }
}

public enum ModuleMemoryError: Error, Equatable, CustomStringConvertible {
    case moduleRequired
    public var description: String { "A module has to say what it is." }
}

// MARK: - The editor hook

/// Getting the words out of whatever an editor sent.
///
/// THIS LIVED IN THE COMMAND, WHERE NO TEST COULD REACH IT. It runs on every
/// prompt somebody types and it decides what gets remembered, and its behaviour
/// was only ever checked by hand — a claim nothing can test is a claim, not a
/// fact.
///
/// FORGIVING BY DESIGN, because the shape belongs to somebody else. Anything
/// that is not the envelope is the WORDS themselves; an envelope WITHOUT a
/// prompt is NOTHING, because reading the envelope as if it were the message
/// would file field names as things somebody said.
public enum HookPayload {

    public static func promptFrom(_ raw: String?) -> String {
        guard let raw, !raw.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return "" }
        let trimmed = raw.trimmingCharacters(in: .whitespacesAndNewlines)

        // Not an envelope. Take it at face value — a person piping their own
        // notes in is the other half of what this reads.
        guard trimmed.hasPrefix("{") else { return raw }

        guard let data = trimmed.data(using: .utf8),
              let object = try? JSONSerialization.jsonObject(with: data) else {
            // Something that starts with a brace and is not JSON is far more
            // likely to be prose than a broken payload.
            return raw
        }
        guard let dict = object as? [String: Any] else { return raw }
        guard let prompt = dict["prompt"] else { return "" }  // an envelope with no message
        return prompt as? String ?? ""
    }
}
