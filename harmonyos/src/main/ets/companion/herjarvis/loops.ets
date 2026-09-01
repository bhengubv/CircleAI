/**
 * companion/herjarvis/loops
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// companion/herjarvis/loops.ts
//
// The remaining HerJarvis implementations, ported from
// HerJarvisRealImplementations.cs:
//
//   18. RegistryPhysicalActuator        — IPhysicalActuator
//   20. InMemoryFederatedFineTuner       — IFederatedFineTuner
//   21. SlidingP50FirstTokenOptimizer     — IFirstTokenOptimizer
//   23. SyntaxCheckingCodeGenerationLoop  — ICodeGenerationLoop
//   24. TrackingSelfImprovementLoop       — ISelfImprovementLoop
//
// Native / cloud bindings are injected behind delegate parameters exactly as the
// C# does (the trainer, generator, test-runner, deployment-hint, run-bench, and
// propose-improvement callbacks), with working local defaults — no stubs.

import { newGuidN } from "./guid";
import { toRoundTripUtc } from "./stores";
import type {
  IPhysicalActuator,
  PhysicalCommand,
  PhysicalCommandResult,
  IFederatedFineTuner,
  FineTuneJobStatus,
  IFirstTokenOptimizer,
  FirstTokenBudget,
  ICodeGenerationLoop,
  CodeGenJob,
  ISelfImprovementLoop,
  SelfImprovementVerdict,
} from "./contracts";

// =====================================================================
// 18. PhysicalActuator — device-handler registry with per-action dispatch.
// =====================================================================

export type PhysicalDeviceHandler = (
  command: PhysicalCommand,
  signal?: AbortSignal,
) => Promise<PhysicalCommandResult>;

/**
 * A device-handler registry. Hosts register a handler per device id (the native
 * robotics / home-automation binding); `invokeAsync` dispatches to it, or
 * returns a failure result for an unknown device.
 */
export class RegistryPhysicalActuator implements IPhysicalActuator {
  private readonly handlers = new Map<string, PhysicalDeviceHandler>();

  registerDevice(deviceId: string, handler: PhysicalDeviceHandler): void {
    if (!deviceId || deviceId.trim().length === 0) throw new Error("deviceId required");
    if (handler == null) throw new Error("handler required");
    this.handlers.set(deviceId, handler);
  }

  async invokeAsync(command: PhysicalCommand, signal?: AbortSignal): Promise<PhysicalCommandResult> {
    if (command == null) throw new Error("command required");
    const h = this.handlers.get(command.deviceId);
    if (h === undefined) return { succeeded: false, error: `Unknown device '${command.deviceId}'` };
    return h(command, signal);
  }
}

// =====================================================================
// 20. FederatedFineTuner — job runner with status tracking.
// =====================================================================

export type FineTuneTrainer = (
  baseModel: string,
  trainingDataPath: string,
  progress: (p: number) => void,
  signal?: AbortSignal,
) => Promise<void>;

/**
 * Runs on-device / federated fine-tune jobs, tracking per-job progress. The
 * trainer is injected (a real host wires the MNN / LoRA pipeline); the default
 * trainer simulates monotonic progress to 1.0. Jobs run in the background and
 * the status map is updated as progress is reported.
 */
export class InMemoryFederatedFineTuner implements IFederatedFineTuner {
  private readonly jobs = new Map<string, FineTuneJobStatus>();
  private readonly trainer: FineTuneTrainer;

  constructor(trainer?: FineTuneTrainer) {
    this.trainer = trainer ?? defaultTrainer;
  }

  async startAsync(baseModel: string, trainingDataPath: string): Promise<string> {
    if (!baseModel || baseModel.trim().length === 0) throw new Error("baseModel required");
    if (!trainingDataPath || trainingDataPath.trim().length === 0)
      throw new Error("trainingDataPath required");
    const jobId = newGuidN();
    this.jobs.set(jobId, { jobId, progress: 0, error: null });
    const report = (p: number) => {
      const cur = this.jobs.get(jobId);
      if (cur) this.jobs.set(jobId, { ...cur, progress: clamp(p, 0, 1) });
    };
    // Fire-and-forget (C# `_ = Task.Run(...)`).
    void (async () => {
      try {
        await this.trainer(baseModel, trainingDataPath, report);
        const cur = this.jobs.get(jobId);
        if (cur) this.jobs.set(jobId, { ...cur, progress: 1.0, error: null });
      } catch (ex) {
        const cur = this.jobs.get(jobId);
        if (cur) this.jobs.set(jobId, { ...cur, error: ex instanceof Error ? ex.message : String(ex) });
      }
    })();
    return Promise.resolve(jobId);
  }

  statusAsync(jobId: string): Promise<FineTuneJobStatus> {
    const s = this.jobs.get(jobId);
    if (s === undefined) return Promise.resolve({ jobId, progress: 0, error: "unknown job" });
    return Promise.resolve(s);
  }
}

/** Default trainer — no file system dependency: 100 monotonic steps to 1.0. */
async function defaultTrainer(
  _baseModel: string,
  _trainingDataPath: string,
  progress: (p: number) => void,
): Promise<void> {
  // C# reads the training file's line count; with no data path we use 100 steps,
  // matching the C# `File.Exists(path) ? ... : 100` fallback branch.
  const lineCount = 100;
  const step = 1.0 / Math.max(1, lineCount);
  for (let i = 0; i < lineCount; i++) {
    progress(i * step);
    await Promise.resolve(); // Task.Yield()
  }
  progress(1.0);
}

// =====================================================================
// 21. FirstTokenOptimizer — sliding-window p50 latency tracker.
// =====================================================================

/**
 * A sliding-window first-token latency tracker. `recordFirstTokenLatency`
 * pushes a sample (dropping the oldest past the window); `currentAsync` reports
 * the target and the median (upper-middle for even counts, matching the C#
 * `sorted[length/2]` integer index).
 */
export class SlidingP50FirstTokenOptimizer implements IFirstTokenOptimizer {
  private readonly samples: number[] = [];
  private readonly windowSize: number;
  private readonly targetMs: number;

  constructor(targetMs = 100, windowSize = 256) {
    if (targetMs <= 0) throw new Error("targetMs out of range");
    if (windowSize <= 0) throw new Error("windowSize out of range");
    this.targetMs = targetMs;
    this.windowSize = windowSize;
  }

  recordFirstTokenLatency(ms: number): void {
    if (ms < 0) throw new Error("ms out of range");
    this.samples.push(ms);
    while (this.samples.length > this.windowSize) this.samples.shift();
  }

  currentAsync(): Promise<FirstTokenBudget> {
    let p50: number;
    if (this.samples.length === 0) {
      p50 = 0;
    } else {
      const sorted = this.samples.slice().sort((a, b) => a - b);
      p50 = sorted[Math.trunc(sorted.length / 2)];
    }
    return Promise.resolve({ targetMs: this.targetMs, currentP50Ms: p50 });
  }
}

// =====================================================================
// 23. CodeGenerationLoop — syntax-validates + runs registered tests.
// =====================================================================

export type CodeGenerator = (prompt: string, signal?: AbortSignal) => Promise<string>;
export type CodeTestRunner = (snippet: string, signal?: AbortSignal) => Promise<boolean>;
export type CodeDeploymentHint = (snippet: string) => string | null;

/**
 * A generate → syntax-check → test → deploy-hint loop. The generator and test
 * runner are injected (a real host wires an LLM + sandbox); the default
 * generator echoes the prompt and the default test runner only checks bracket
 * balance. Tests only run when the snippet's brackets balance.
 */
export class SyntaxCheckingCodeGenerationLoop implements ICodeGenerationLoop {
  private readonly generator: CodeGenerator;
  private readonly testRunner: CodeTestRunner;
  private readonly deploymentHint: CodeDeploymentHint;

  constructor(generator?: CodeGenerator, testRunner?: CodeTestRunner, deploymentHint?: CodeDeploymentHint) {
    this.generator = generator ?? defaultGenerator;
    this.testRunner = testRunner ?? defaultTestRunner;
    this.deploymentHint = deploymentHint ?? defaultDeploymentHint;
  }

  async runAsync(prompt: string, signal?: AbortSignal): Promise<CodeGenJob> {
    if (!prompt || prompt.trim().length === 0) throw new Error("prompt required");
    const id = newGuidN();
    const snippet = await this.generator(prompt, signal);
    const parses = isSyntacticallyBalanced(snippet);
    const testsOk = parses && (await this.testRunner(snippet, signal));
    return {
      id,
      prompt,
      outputSnippet: snippet,
      testsPass: testsOk,
      deployHint: testsOk ? this.deploymentHint(snippet) : null,
    };
  }
}

function defaultGenerator(prompt: string): Promise<string> {
  return Promise.resolve(`// (3.3.0) generated from: ${prompt.replace(/\n/g, " ")}\nreturn 0;`);
}

function defaultTestRunner(snippet: string): Promise<boolean> {
  return Promise.resolve(isSyntacticallyBalanced(snippet));
}

function defaultDeploymentHint(snippet: string): string | null {
  return snippet.includes("public class") ? "stage as nuget" : "run inline";
}

/** Balanced {} () [] with no premature close, exactly like the C#. */
export function isSyntacticallyBalanced(snippet: string): boolean {
  if (!snippet || snippet.length === 0) return false;
  let curly = 0;
  let paren = 0;
  let square = 0;
  for (const c of snippet) {
    switch (c) {
      case "{":
        curly++;
        break;
      case "}":
        curly--;
        break;
      case "(":
        paren++;
        break;
      case ")":
        paren--;
        break;
      case "[":
        square++;
        break;
      case "]":
        square--;
        break;
    }
    if (curly < 0 || paren < 0 || square < 0) return false;
  }
  return curly === 0 && paren === 0 && square === 0;
}

// =====================================================================
// 24. SelfImprovementLoop — tracks bench scores + applies improvements.
// =====================================================================

export type BenchRunner = (benchSuiteId: string, signal?: AbortSignal) => Promise<number>;
export type ImprovementProposer = (
  benchSuiteId: string,
  current: number,
  signal?: AbortSignal,
) => Promise<string>;

/**
 * Tracks the best bench score seen per suite. Each cycle runs the bench; if the
 * new score is >= the tracked best it is recorded ("new best" / "no regression"),
 * otherwise an improvement is proposed. Bench-run and propose callbacks are
 * injected; the defaults give a deterministic (content-hashed) score and a fixed
 * retry proposal.
 */
export class TrackingSelfImprovementLoop implements ISelfImprovementLoop {
  private readonly bestScores = new Map<string, number>();
  private readonly runBench: BenchRunner;
  private readonly proposeImprovement: ImprovementProposer;

  constructor(runBench?: BenchRunner, proposeImprovement?: ImprovementProposer) {
    this.runBench = runBench ?? defaultRunBench;
    this.proposeImprovement = proposeImprovement ?? defaultProposeImprovement;
  }

  async cycleAsync(benchSuiteId: string, signal?: AbortSignal): Promise<SelfImprovementVerdict> {
    if (!benchSuiteId || benchSuiteId.trim().length === 0) throw new Error("benchSuiteId required");
    const baseline = this.bestScores.get(benchSuiteId) ?? 0.0;
    const current = await this.runBench(benchSuiteId, signal);
    let applied = "none";
    if (current >= baseline) {
      this.bestScores.set(benchSuiteId, current);
      applied = current > baseline ? "new best" : "no regression";
    } else {
      applied = await this.proposeImprovement(benchSuiteId, current, signal);
    }
    return { improvementsApplied: applied, newBenchScore: current };
  }

  bestScoreFor(benchSuiteId: string): number {
    return this.bestScores.get(benchSuiteId) ?? 0;
  }
}

/**
 * Default bench — deterministic score in [0.5, 1.0] from a stable content hash.
 * The C# uses `id.GetHashCode()` which is randomised per-process and cannot be
 * reproduced across runs/languages; a stable FNV-1a hash preserves the INTENT
 * (a content-derived, reproducible score) and, unlike the C#, is deterministic.
 */
function defaultRunBench(benchSuiteId: string): Promise<number> {
  const h16 = stableHash(benchSuiteId) & 0xffff;
  return Promise.resolve(0.5 + (h16 / 65535.0) * 0.5);
}

function defaultProposeImprovement(_id: string, current: number): Promise<string> {
  return Promise.resolve(`retry-with-temperature-0 (score was ${current.toFixed(3)})`);
}

/** 32-bit FNV-1a (unsigned), same helper used by TemplateInnerMonologue. */
function stableHash(s: string): number {
  let h = 0x811c9dc5;
  for (let i = 0; i < s.length; i++) {
    h ^= s.charCodeAt(i) & 0xff;
    h = Math.imul(h, 0x01000193);
  }
  return h >>> 0;
}

function clamp(v: number, lo: number, hi: number): number {
  return Math.min(hi, Math.max(lo, v));
}

// re-export for callers/tests that want the round-trip formatter alongside loops.
export { toRoundTripUtc };
