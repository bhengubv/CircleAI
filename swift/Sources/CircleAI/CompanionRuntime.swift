// CompanionRuntime.swift
//
// Ported from CircleAI.Memory.Runtime (the C# reference). The host
// orchestrator that ticks the consolidator on a schedule, keeps the sync
// engine running, and exposes a single ingestion entry point for multimodal
// artefacts.
//
// C# implements IHostedService (Generic Host / ASP.NET). Swift has no such
// host abstraction, so this exposes `start()` / `stop()` async lifecycle
// methods and runs the periodic passes as detached `Task`s. Behaviour matches
// the C# loops exactly: an InitialDelay before the first tick, a CatchUp
// OnDemand pass on start, and per-tier interval loops that are suppressed when
// their interval is `.zero`.

import Foundation

// MARK: - CompanionRuntimeOptions

/// Configuration for `CompanionRuntime`. All values have sensible defaults so a
/// host can construct `CompanionRuntime(consolidator:)` and get a working
/// pipeline out of the box.
public struct CompanionRuntimeOptions: Sendable {
    /// Cadence for the daily-tier consolidation pass. Default: every 6 hours.
    /// Setting this to `.zero` disables automatic daily ticks.
    public var dailyTickInterval: TimeInterval

    /// Cadence for the weekly-tier consolidation pass. Default: every 24 hours.
    public var weeklyTickInterval: TimeInterval

    /// Cadence for the monthly-tier (persona-delta) consolidation pass.
    /// Default: every 48 hours.
    public var monthlyTickInterval: TimeInterval

    /// Cadence at which the runtime broadcasts its sync state vector to peers.
    /// Default: every 5 minutes. Setting to `.zero` disables periodic sync (the
    /// engine still responds to inbound envelopes; only the initiating Announce
    /// broadcasts are suppressed).
    public var syncBroadcastInterval: TimeInterval

    /// Initial delay before the first consolidator tick after `start()`.
    /// Default: 30 seconds. Keeps startup quiet.
    public var initialDelay: TimeInterval

    /// When true, the runtime runs an OnDemand consolidation pass during
    /// `start()` to catch up anything pending before the timer cadence kicks in.
    /// Default: true.
    public var catchUpOnStart: Bool

    public init(
        dailyTickInterval: TimeInterval = 6 * 3600,
        weeklyTickInterval: TimeInterval = 24 * 3600,
        monthlyTickInterval: TimeInterval = 48 * 3600,
        syncBroadcastInterval: TimeInterval = 5 * 60,
        initialDelay: TimeInterval = 30,
        catchUpOnStart: Bool = true
    ) {
        self.dailyTickInterval = dailyTickInterval
        self.weeklyTickInterval = weeklyTickInterval
        self.monthlyTickInterval = monthlyTickInterval
        self.syncBroadcastInterval = syncBroadcastInterval
        self.initialDelay = initialDelay
        self.catchUpOnStart = catchUpOnStart
    }
}

// MARK: - CompanionRuntime

/// Owns the lifecycle of the memory pipeline (consolidator, sync engine,
/// multimodal ingester) and ticks the consolidation passes on a configurable
/// schedule.
public final class CompanionRuntime: @unchecked Sendable {
    private let consolidator: IMemoryConsolidator
    private let syncEngine: ICompanionStateSyncEngine?
    private let ingester: MultimodalMemoryIngester?
    private let options: CompanionRuntimeOptions

    private let lock = NSLock()
    private var running = false
    private var loops: [Task<Void, Never>] = []

    public init(
        consolidator: IMemoryConsolidator,
        options: CompanionRuntimeOptions = CompanionRuntimeOptions(),
        syncEngine: ICompanionStateSyncEngine? = nil,
        ingester: MultimodalMemoryIngester? = nil
    ) {
        self.consolidator = consolidator
        self.options = options
        self.syncEngine = syncEngine
        self.ingester = ingester
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────

    /// Starts the sync engine (if wired), runs a catch-up consolidation pass
    /// (if enabled), and launches the periodic tick loops.
    public func start() async throws {
        lock.lock()
        if running { lock.unlock(); return }
        running = true
        lock.unlock()

        if let engine = syncEngine {
            try await engine.start()
        }

        if options.catchUpOnStart {
            // Catch-up failures are non-fatal (mirrors the C# try/catch).
            _ = try? await consolidator.tick(kind: .onDemand)
        }

        var started: [Task<Void, Never>] = []
        if options.dailyTickInterval > 0 {
            started.append(runPeriodic(kind: .daily, interval: options.dailyTickInterval))
        }
        if options.weeklyTickInterval > 0 {
            started.append(runPeriodic(kind: .weekly, interval: options.weeklyTickInterval))
        }
        if options.monthlyTickInterval > 0 {
            started.append(runPeriodic(kind: .monthly, interval: options.monthlyTickInterval))
        }
        if syncEngine != nil && options.syncBroadcastInterval > 0 {
            started.append(runSyncBroadcasts(interval: options.syncBroadcastInterval))
        }

        lock.lock()
        loops = started
        lock.unlock()
    }

    /// Cancels all periodic loops and disposes the sync engine.
    public func stop() async {
        lock.lock()
        if !running {
            lock.unlock()
            return
        }
        running = false
        let toCancel = loops
        loops = []
        lock.unlock()

        for t in toCancel { t.cancel() }
        for t in toCancel { _ = await t.value }

        if let engine = syncEngine {
            await engine.dispose()
        }
    }

    // ── Public helpers ────────────────────────────────────────────────────

    /// Triggers an OnDemand consolidation pass. Hosts call this after large
    /// chunks of new activity (e.g. end of a long conversation) when they don't
    /// want to wait for the timer.
    @discardableResult
    public func consolidateNow() async throws -> ConsolidationOutcome {
        try await consolidator.tick(kind: .onDemand)
    }

    /// Forwards multimodal ingestion to the registered ingester. Throws when no
    /// ingester was wired (the runtime can be wired without one for text-only
    /// hosts).
    @discardableResult
    public func ingestMedia(
        modality: MediaModality,
        sourceBytes: [UInt8],
        mimeType: String? = nil,
        sourceUri: String? = nil,
        tags: [String: String]? = nil
    ) async throws -> IngestionResult {
        guard let ingester = ingester else {
            throw CompanionRuntimeError.noIngester
        }
        return try await ingester.ingest(
            modality: modality, sourceBytes: sourceBytes,
            mimeType: mimeType, sourceUri: sourceUri, tags: tags)
    }

    /// Forces an immediate sync broadcast. No-op when sync isn't wired.
    public func syncNow() async throws {
        try await syncEngine?.syncNow()
    }

    // ── Internals ─────────────────────────────────────────────────────────

    private func runPeriodic(kind: SleepKind, interval: TimeInterval) -> Task<Void, Never> {
        Task { [weak self] in
            guard let self else { return }
            if await self.sleep(self.options.initialDelay) == false { return }
            while !Task.isCancelled {
                // A tick failure is logged-and-swallowed in C#; here we simply
                // continue the loop so one bad pass doesn't kill the cadence.
                _ = try? await self.consolidator.tick(kind: kind)
                if await self.sleep(interval) == false { return }
            }
        }
    }

    private func runSyncBroadcasts(interval: TimeInterval) -> Task<Void, Never> {
        Task { [weak self] in
            guard let self, let engine = self.syncEngine else { return }
            if await self.sleep(self.options.initialDelay) == false { return }
            while !Task.isCancelled {
                try? await engine.syncNow()
                if await self.sleep(interval) == false { return }
            }
        }
    }

    /// Sleeps for `seconds`. Returns false if the task was cancelled during the
    /// sleep (so callers can break the loop), true otherwise.
    private func sleep(_ seconds: TimeInterval) async -> Bool {
        if seconds <= 0 { return !Task.isCancelled }
        do {
            try await Task.sleep(nanoseconds: UInt64(seconds * 1_000_000_000))
            return !Task.isCancelled
        } catch {
            return false
        }
    }
}

/// Errors raised by `CompanionRuntime`.
public enum CompanionRuntimeError: Error, Sendable, Equatable {
    /// `ingestMedia` was called but no `MultimodalMemoryIngester` was wired.
    case noIngester
}
