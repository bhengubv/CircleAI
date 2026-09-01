/**
 * buildfarm/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// buildfarm/index.ts
// Full-parity port of CircleAI.BuildFarm (C#). C# is the exact spec.
//
// Build-farm primitives: BuildAgentKind / BuildJobPhase enums, BuildAgent /
// BuildJob / BuildArtifact records, the IBuildAgentPool / IBuildJobRunner /
// IBuildArtifactStore contracts, deterministic in-memory implementations (an
// agent pool with acquire/release, a job runner with a Pending→Running→
// Succeeded/Failed state machine, an artifact store), and fail-closed Null*.
//
// Type mappings (C# → TS):
//   ReadOnlyMemory<byte> Payload        → Uint8Array
//   DateTimeOffset StartUtc             → Date
//   ValueTask / ValueTask<T>            → Promise<void> / Promise<T>
//   Interlocked.Increment(ref _seq)     → ++this.seq

/** Build agent platform. Mirrors C# `BuildAgentKind`. */
export enum BuildAgentKind {
  Linux = 0,
  Mac = 1,
  Windows = 2,
  Android = 3,
  Ios = 4,
}

/** Build job lifecycle phase. Mirrors C# `BuildJobPhase`. */
export enum BuildJobPhase {
  Pending = 0,
  Running = 1,
  Succeeded = 2,
  Failed = 3,
}

/** A registered build agent. Mirrors C# `BuildAgent` record. */
export interface BuildAgent {
  readonly agentId: string;
  readonly kind: BuildAgentKind;
  readonly os: string;
  readonly hardware: string | null;
}

/** Constructs a {@link BuildAgent}. */
export function buildAgent(
  agentId: string,
  kind: BuildAgentKind,
  os: string,
  hardware: string | null,
): BuildAgent {
  return { agentId, kind, os, hardware };
}

/** A build job. Mirrors C# `BuildJob` record. */
export interface BuildJob {
  readonly jobId: string;
  readonly agentId: string;
  readonly repo: string;
  readonly branch: string;
  readonly phase: BuildJobPhase;
  readonly startUtc: Date;
}

/** Constructs a {@link BuildJob}. */
export function buildJob(
  jobId: string,
  agentId: string,
  repo: string,
  branch: string,
  phase: BuildJobPhase,
  startUtc: Date,
): BuildJob {
  return { jobId, agentId, repo, branch, phase, startUtc };
}

/** A build artifact. Mirrors C# `BuildArtifact` record. */
export interface BuildArtifact {
  readonly artifactId: string;
  readonly jobId: string;
  readonly name: string;
  readonly payload: Uint8Array;
}

/** Constructs a {@link BuildArtifact}. */
export function buildArtifact(
  artifactId: string,
  jobId: string,
  name: string,
  payload: Uint8Array,
): BuildArtifact {
  return { artifactId, jobId, name, payload };
}

/** Pool of build agents. Mirrors C# `IBuildAgentPool`. */
export interface IBuildAgentPool {
  readonly backendId: string;
  acquireAsync(kind: BuildAgentKind, signal?: AbortSignal): Promise<BuildAgent | null>;
  releaseAsync(agentId: string, signal?: AbortSignal): Promise<void>;
  listAsync(signal?: AbortSignal): Promise<readonly BuildAgent[]>;
}

/** Starts + tracks build jobs. Mirrors C# `IBuildJobRunner`. */
export interface IBuildJobRunner {
  readonly backendId: string;
  startAsync(agentId: string, repo: string, branch: string, signal?: AbortSignal): Promise<BuildJob>;
  getAsync(jobId: string, signal?: AbortSignal): Promise<BuildJob | null>;
}

/** Stores build artifacts. Mirrors C# `IBuildArtifactStore`. */
export interface IBuildArtifactStore {
  readonly backendId: string;
  saveAsync(artifact: BuildArtifact, signal?: AbortSignal): Promise<void>;
  getAsync(artifactId: string, signal?: AbortSignal): Promise<BuildArtifact | null>;
}

// ─────────────────────────────────────────────────────────────────────────────
// In-memory implementations
// ─────────────────────────────────────────────────────────────────────────────

/** In-memory {@link IBuildAgentPool} with acquire/release. Mirrors C# `InMemoryBuildAgentPool`. */
export class InMemoryBuildAgentPool implements IBuildAgentPool {
  private readonly all = new Map<string, BuildAgent>();
  private readonly busy = new Set<string>();
  readonly backendId = "in-memory";

  /** Register an agent. Mirrors C# `Register`. */
  register(a: BuildAgent): void {
    if (a == null) throw new Error("agent required");
    this.all.set(a.agentId, a);
  }

  acquireAsync(kind: BuildAgentKind, _signal?: AbortSignal): Promise<BuildAgent | null> {
    for (const a of this.all.values()) {
      if (a.kind === kind && !this.busy.has(a.agentId)) {
        this.busy.add(a.agentId);
        return Promise.resolve(a);
      }
    }
    return Promise.resolve(null);
  }

  releaseAsync(agentId: string, _signal?: AbortSignal): Promise<void> {
    if (isBlank(agentId)) throw new Error("agentId required");
    this.busy.delete(agentId);
    return Promise.resolve();
  }

  listAsync(_signal?: AbortSignal): Promise<readonly BuildAgent[]> {
    return Promise.resolve([...this.all.values()]);
  }
}

/** In-memory {@link IBuildJobRunner} state machine. Mirrors C# `InMemoryBuildJobRunner`. */
export class InMemoryBuildJobRunner implements IBuildJobRunner {
  private readonly jobs = new Map<string, BuildJob>();
  private seq = 0;
  readonly backendId = "in-memory";

  startAsync(
    agentId: string,
    repo: string,
    branch: string,
    _signal?: AbortSignal,
  ): Promise<BuildJob> {
    if (isBlank(agentId)) throw new Error("agentId required");
    if (isBlank(repo)) throw new Error("repo required");
    if (isBlank(branch)) throw new Error("branch required");
    const jobId = `job-${++this.seq}`;
    const job = buildJob(jobId, agentId, repo, branch, BuildJobPhase.Running, new Date());
    this.jobs.set(jobId, job);
    return Promise.resolve(job);
  }

  getAsync(jobId: string, _signal?: AbortSignal): Promise<BuildJob | null> {
    if (isBlank(jobId)) throw new Error("jobId required");
    return Promise.resolve(this.jobs.get(jobId) ?? null);
  }

  /** Mark a running job Succeeded/Failed. Mirrors C# `Complete`. */
  complete(jobId: string, success: boolean): void {
    const j = this.jobs.get(jobId);
    if (j === undefined) throw new Error(`Unknown job ${jobId}`);
    this.jobs.set(jobId, {
      ...j,
      phase: success ? BuildJobPhase.Succeeded : BuildJobPhase.Failed,
    });
  }
}

/** In-memory {@link IBuildArtifactStore}. Mirrors C# `InMemoryBuildArtifactStore`. */
export class InMemoryBuildArtifactStore implements IBuildArtifactStore {
  private readonly items = new Map<string, BuildArtifact>();
  readonly backendId = "in-memory";

  saveAsync(artifact: BuildArtifact, _signal?: AbortSignal): Promise<void> {
    if (artifact == null) throw new Error("artifact required");
    if (isBlank(artifact.artifactId)) throw new Error("ArtifactId required");
    this.items.set(artifact.artifactId, artifact);
    return Promise.resolve();
  }

  getAsync(artifactId: string, _signal?: AbortSignal): Promise<BuildArtifact | null> {
    if (isBlank(artifactId)) throw new Error("artifactId required");
    return Promise.resolve(this.items.get(artifactId) ?? null);
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Null implementations
// ─────────────────────────────────────────────────────────────────────────────

const EMPTY_GUID = "00000000-0000-0000-0000-000000000000";
const MIN_DATE = new Date("0001-01-01T00:00:00Z");

/** Fail-closed {@link IBuildAgentPool}. Mirrors C# `NullBuildAgentPool`. */
export class NullBuildAgentPool implements IBuildAgentPool {
  static readonly instance = new NullBuildAgentPool();
  readonly backendId = "null";
  acquireAsync(_kind: BuildAgentKind, _signal?: AbortSignal): Promise<BuildAgent | null> {
    return Promise.resolve(null);
  }
  releaseAsync(_agentId: string, _signal?: AbortSignal): Promise<void> {
    return Promise.resolve();
  }
  listAsync(_signal?: AbortSignal): Promise<readonly BuildAgent[]> {
    return Promise.resolve([]);
  }
}

/** Fail-closed {@link IBuildJobRunner}. Mirrors C# `NullBuildJobRunner`. */
export class NullBuildJobRunner implements IBuildJobRunner {
  static readonly instance = new NullBuildJobRunner();
  readonly backendId = "null";
  startAsync(agentId: string, repo: string, branch: string, _signal?: AbortSignal): Promise<BuildJob> {
    return Promise.resolve(
      buildJob(EMPTY_GUID, agentId, repo, branch, BuildJobPhase.Failed, MIN_DATE),
    );
  }
  getAsync(_jobId: string, _signal?: AbortSignal): Promise<BuildJob | null> {
    return Promise.resolve(null);
  }
}

/** Fail-closed {@link IBuildArtifactStore}. Mirrors C# `NullBuildArtifactStore`. */
export class NullBuildArtifactStore implements IBuildArtifactStore {
  static readonly instance = new NullBuildArtifactStore();
  readonly backendId = "null";
  saveAsync(_artifact: BuildArtifact, _signal?: AbortSignal): Promise<void> {
    return Promise.resolve();
  }
  getAsync(_artifactId: string, _signal?: AbortSignal): Promise<BuildArtifact | null> {
    return Promise.resolve(null);
  }
}

function isBlank(s: string | null | undefined): boolean {
  return s == null || s.trim().length === 0;
}
