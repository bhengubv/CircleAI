// BuildFarm.swift
//
// Port of CircleAI.BuildFarm/ — an in-memory build-farm: agent pool + job
// runner (state machine) + artifact store. Hosts that integrate real CI swap
// in a real implementation behind the same contracts.
//   • Contracts.cs           — BuildAgentKind, BuildJobPhase, BuildAgent,
//                              BuildJob, BuildArtifact, IBuildAgentPool,
//                              IBuildJobRunner, IBuildArtifactStore
//   • InMemoryBuildFarm.cs   — InMemoryBuildAgentPool (acquire/release/list),
//                              InMemoryBuildJobRunner (Pending→Running→
//                              Succeeded/Failed), InMemoryBuildArtifactStore
//   • NullImplementations.cs — Null* fail-closed backends
//
// Porting notes:
//   • `ReadOnlyMemory<byte>` → `Data`.
//   • `IReadOnlyList<BuildAgent>` return → `[BuildAgent]`.
//   • Job id / run-seq is a monotonic counter under the lock (C# uses
//     `Interlocked.Increment`).
//   • `Complete(jobId, success)` flips a running job to Succeeded/Failed and
//     throws on an unknown job (C# `InvalidOperationException`).

import Foundation

// MARK: - Enums + records

/// Kind of build agent. (C# `BuildAgentKind`.)
public enum BuildAgentKind: Int, Sendable, Codable, CaseIterable {
    case linux = 0
    case mac = 1
    case windows = 2
    case android = 3
    case ios = 4
}

/// Lifecycle phase of a build job. (C# `BuildJobPhase`.)
public enum BuildJobPhase: Int, Sendable, Codable, CaseIterable {
    case pending = 0
    case running = 1
    case succeeded = 2
    case failed = 3
}

/// A build agent. (C# `BuildAgent`.)
public struct BuildAgent: Sendable, Equatable, Codable {
    /// Agent identifier.
    public let agentId: String
    /// Agent kind.
    public let kind: BuildAgentKind
    /// OS descriptor.
    public let os: String
    /// Optional hardware descriptor.
    public let hardware: String?

    public init(agentId: String, kind: BuildAgentKind, os: String, hardware: String?) {
        self.agentId = agentId
        self.kind = kind
        self.os = os
        self.hardware = hardware
    }
}

/// A build job. (C# `BuildJob`.)
public struct BuildJob: Sendable, Equatable, Codable {
    /// Job identifier.
    public let jobId: String
    /// Agent running the job.
    public let agentId: String
    /// Repo being built.
    public let repo: String
    /// Branch being built.
    public let branch: String
    /// Current phase.
    public let phase: BuildJobPhase
    /// UTC start time.
    public let startUtc: Date

    public init(jobId: String, agentId: String, repo: String, branch: String,
                phase: BuildJobPhase, startUtc: Date) {
        self.jobId = jobId
        self.agentId = agentId
        self.repo = repo
        self.branch = branch
        self.phase = phase
        self.startUtc = startUtc
    }

    func with(phase: BuildJobPhase) -> BuildJob {
        BuildJob(jobId: jobId, agentId: agentId, repo: repo, branch: branch, phase: phase, startUtc: startUtc)
    }
}

/// A build artifact. (C# `BuildArtifact`.) `ReadOnlyMemory<byte>` → `Data`.
public struct BuildArtifact: Sendable, Equatable, Codable {
    /// Artifact identifier.
    public let artifactId: String
    /// Job that produced it.
    public let jobId: String
    /// Artifact name.
    public let name: String
    /// Artifact payload.
    public let payload: Data

    public init(artifactId: String, jobId: String, name: String, payload: Data) {
        self.artifactId = artifactId
        self.jobId = jobId
        self.name = name
        self.payload = payload
    }
}

// MARK: - Errors

/// Errors raised by the build farm.
public enum BuildFarmError: Error, Equatable, CustomStringConvertible {
    case unknownJob(String)

    public var description: String {
        switch self {
        case .unknownJob(let id): return "Unknown job \(id)"
        }
    }
}

// MARK: - Contracts

/// A pool of build agents. (C# `IBuildAgentPool`.)
public protocol IBuildAgentPool: Sendable {
    /// Backend identifier.
    var backendId: String { get }
    /// Acquires a free agent of `kind`, or `nil` when none are available.
    func acquire(_ kind: BuildAgentKind) async -> BuildAgent?
    /// Releases the agent `agentId` back to the pool.
    func release(_ agentId: String) async
    /// Lists all agents (busy or free).
    func list() async -> [BuildAgent]
}

/// Starts + tracks build jobs. (C# `IBuildJobRunner`.)
public protocol IBuildJobRunner: Sendable {
    /// Backend identifier.
    var backendId: String { get }
    /// Starts a job on `agentId` for `repo`/`branch`.
    func start(agentId: String, repo: String, branch: String) async -> BuildJob
    /// Returns a job by id, or `nil`.
    func get(_ jobId: String) async -> BuildJob?
}

/// Stores build artifacts. (C# `IBuildArtifactStore`.)
public protocol IBuildArtifactStore: Sendable {
    /// Backend identifier.
    var backendId: String { get }
    /// Saves an artifact.
    func save(_ artifact: BuildArtifact) async
    /// Returns an artifact by id, or `nil`.
    func get(_ artifactId: String) async -> BuildArtifact?
}

// MARK: - InMemoryBuildAgentPool

/// In-memory agent pool. `acquire` hands out the first free agent of the kind
/// and marks it busy; `release` frees it. (C# `InMemoryBuildAgentPool`.)
public final class InMemoryBuildAgentPool: IBuildAgentPool, @unchecked Sendable {
    private let lock = NSLock()
    private var all: [String: BuildAgent] = [:]
    private var busy: Set<String> = []

    public init() {}

    public var backendId: String { "in-memory" }

    /// Registers (or replaces, by id) an agent.
    public func register(_ a: BuildAgent) {
        lock.lock(); all[a.agentId] = a; lock.unlock()
    }

    public func acquire(_ kind: BuildAgentKind) async -> BuildAgent? {
        lock.lock(); defer { lock.unlock() }
        for a in all.values where a.kind == kind {
            if !busy.contains(a.agentId) {
                busy.insert(a.agentId)
                return a
            }
        }
        return nil
    }

    public func release(_ agentId: String) async {
        precondition(!agentId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, "agentId required")
        lock.lock(); busy.remove(agentId); lock.unlock()
    }

    public func list() async -> [BuildAgent] {
        lock.lock(); defer { lock.unlock() }
        return Array(all.values)
    }
}

// MARK: - InMemoryBuildJobRunner

/// In-memory job runner: `start` creates a Running job; `complete` flips it to
/// Succeeded/Failed. (C# `InMemoryBuildJobRunner`.)
public final class InMemoryBuildJobRunner: IBuildJobRunner, @unchecked Sendable {
    private let lock = NSLock()
    private var jobs: [String: BuildJob] = [:]
    private var seq: Int64 = 0

    public init() {}

    public var backendId: String { "in-memory" }

    public func start(agentId: String, repo: String, branch: String) async -> BuildJob {
        precondition(!agentId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, "agentId required")
        precondition(!repo.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, "repo required")
        precondition(!branch.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, "branch required")
        lock.lock()
        seq += 1
        let jobId = "job-\(seq)"
        let job = BuildJob(jobId: jobId, agentId: agentId, repo: repo, branch: branch,
                           phase: .running, startUtc: Date())
        jobs[jobId] = job
        lock.unlock()
        return job
    }

    public func get(_ jobId: String) async -> BuildJob? {
        precondition(!jobId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, "jobId required")
        lock.lock(); defer { lock.unlock() }
        return jobs[jobId]
    }

    /// Flips a job to Succeeded/Failed. Throws `BuildFarmError.unknownJob` when
    /// the id is unknown.
    public func complete(_ jobId: String, success: Bool) throws {
        lock.lock(); defer { lock.unlock() }
        guard let j = jobs[jobId] else { throw BuildFarmError.unknownJob(jobId) }
        jobs[jobId] = j.with(phase: success ? .succeeded : .failed)
    }
}

// MARK: - InMemoryBuildArtifactStore

/// In-memory artifact store. (C# `InMemoryBuildArtifactStore`.)
public final class InMemoryBuildArtifactStore: IBuildArtifactStore, @unchecked Sendable {
    private let lock = NSLock()
    private var items: [String: BuildArtifact] = [:]

    public init() {}

    public var backendId: String { "in-memory" }

    public func save(_ artifact: BuildArtifact) async {
        precondition(!artifact.artifactId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, "ArtifactId required")
        lock.lock(); items[artifact.artifactId] = artifact; lock.unlock()
    }

    public func get(_ artifactId: String) async -> BuildArtifact? {
        precondition(!artifactId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty, "artifactId required")
        lock.lock(); defer { lock.unlock() }
        return items[artifactId]
    }
}

// MARK: - Null implementations

/// Fail-closed agent pool — never acquires. (C# `NullBuildAgentPool`.)
public final class NullBuildAgentPool: IBuildAgentPool, @unchecked Sendable {
    public static let instance = NullBuildAgentPool()
    public init() {}
    public var backendId: String { "null" }
    public func acquire(_ kind: BuildAgentKind) async -> BuildAgent? { nil }
    public func release(_ agentId: String) async {}
    public func list() async -> [BuildAgent] { [] }
}

/// Fail-closed job runner — every job starts Failed with the empty GUID.
/// (C# `NullBuildJobRunner`.)
public final class NullBuildJobRunner: IBuildJobRunner, @unchecked Sendable {
    public static let instance = NullBuildJobRunner()
    public init() {}
    public var backendId: String { "null" }
    public func start(agentId: String, repo: String, branch: String) async -> BuildJob {
        BuildJob(jobId: "00000000-0000-0000-0000-000000000000", agentId: agentId, repo: repo,
                 branch: branch, phase: .failed, startUtc: IntegrationDates.minValue)
    }
    public func get(_ jobId: String) async -> BuildJob? { nil }
}

/// Fail-closed artifact store — discards saves. (C# `NullBuildArtifactStore`.)
public final class NullBuildArtifactStore: IBuildArtifactStore, @unchecked Sendable {
    public static let instance = NullBuildArtifactStore()
    public init() {}
    public var backendId: String { "null" }
    public func save(_ artifact: BuildArtifact) async {}
    public func get(_ artifactId: String) async -> BuildArtifact? { nil }
}
