// companion/herjarvis/selfbench_loop.ts
//
// SelfBenchSelfImprovementLoop — SelfBenchSelfImprovementLoop.cs. Orchestrates
// CircleAI.SelfBench to implement ISelfImprovementLoop: run the named suite
// against a baseline AIService, run it against a candidate (e.g. a freshly-
// trained LoRA adapter), A/B compare through a regression gate, and only
// "promote" the candidate when the gate passes.
//
// CircleAI.SelfBench is not part of this port's work unit, so its surface is
// modelled here as small INJECTED collaborator interfaces — exactly the C#
// constructor dependencies (BenchSuiteRegistry, AbBenchRunner, the baseline /
// candidate AIService factories, the on-promote callback, and the regression
// gate config). A host wires the real SelfBench + AIService; this class only
// runs the gate, faithful to the C#.

import type { ISelfImprovementLoop, SelfImprovementVerdict } from "./contracts.js";

/** One benchmark task (opaque here — the runner interprets it). */
export interface BenchTask {
  readonly id: string;
  readonly prompt: string;
  readonly critical?: boolean;
}

/** Mirrors the subset of CircleAI.SelfBench.BenchSummary the loop reads. */
export interface BenchSummary {
  readonly meanScore: number;
}

/** Mirrors CircleAI.SelfBench.RegressionGateConfig defaults. */
export interface RegressionGateConfig {
  readonly minMeanScoreImprovement: number;
  readonly maxP95LatencyRegressionMs: number;
  readonly maxCriticalRegressions: number;
}

export function defaultRegressionGateConfig(): RegressionGateConfig {
  return {
    minMeanScoreImprovement: 0.01,
    maxP95LatencyRegressionMs: 250.0,
    maxCriticalRegressions: 0,
  };
}

/** Mirrors the subset of CircleAI.SelfBench.AbVerdict the loop reads. */
export interface AbVerdict {
  readonly shouldPromote: boolean;
  readonly candidateSummary: BenchSummary;
  readonly reason: string;
}

/** Registry of bench suites — `get` returns the tasks for a suite id. */
export interface IBenchSuiteRegistry {
  get(suiteId: string): readonly BenchTask[];
}

/** An opaque AI service handle (the thing being benched). Host-defined. */
export interface IAIService {
  readonly id?: string;
}

/** A/B bench runner — compares baseline vs candidate through the gate. */
export interface IAbBenchRunner {
  compareAsync(
    suiteId: string,
    tasks: readonly BenchTask[],
    baseline: IAIService,
    candidate: IAIService,
    gate: RegressionGateConfig,
    signal?: AbortSignal,
  ): Promise<AbVerdict>;
}

export type AiServiceFactory = (signal?: AbortSignal) => Promise<IAIService>;
export type PromoteCallback = (verdict: AbVerdict, signal?: AbortSignal) => Promise<void>;

/**
 * ISelfImprovementLoop backed by SelfBench A/B comparison. Each cycle resolves a
 * baseline and a candidate AIService, compares them on the suite's tasks, and
 * promotes the candidate (invoking the host callback + recording the best score)
 * only when the verdict says so. An empty suite is skipped.
 */
export class SelfBenchSelfImprovementLoop implements ISelfImprovementLoop {
  private readonly registry: IBenchSuiteRegistry;
  private readonly runner: IAbBenchRunner;
  private readonly baselineFactory: AiServiceFactory;
  private readonly candidateFactory: AiServiceFactory;
  private readonly onPromote: PromoteCallback;
  private readonly gate: RegressionGateConfig;
  private readonly bestScores = new Map<string, number>();

  constructor(
    registry: IBenchSuiteRegistry,
    runner: IAbBenchRunner,
    baselineFactory: AiServiceFactory,
    candidateFactory: AiServiceFactory,
    onPromote?: PromoteCallback,
    gate?: RegressionGateConfig,
  ) {
    if (registry == null) throw new Error("registry required");
    if (runner == null) throw new Error("runner required");
    if (baselineFactory == null) throw new Error("baselineFactory required");
    if (candidateFactory == null) throw new Error("candidateFactory required");
    this.registry = registry;
    this.runner = runner;
    this.baselineFactory = baselineFactory;
    this.candidateFactory = candidateFactory;
    this.onPromote = onPromote ?? (() => Promise.resolve());
    this.gate = gate ?? defaultRegressionGateConfig();
  }

  async cycleAsync(benchSuiteId: string, signal?: AbortSignal): Promise<SelfImprovementVerdict> {
    // C# `if (string.IsNullOrWhiteSpace(benchSuiteId)) benchSuiteId = "default";`
    let suiteId = benchSuiteId;
    if (suiteId == null || suiteId.trim().length === 0) suiteId = "default";

    const tasks = this.registry.get(suiteId);
    if (tasks.length === 0) {
      return { improvementsApplied: "skipped: no tasks in suite", newBenchScore: 0.0 };
    }

    const baseline = await this.baselineFactory(signal);
    const candidate = await this.candidateFactory(signal);

    const verdict = await this.runner.compareAsync(suiteId, tasks, baseline, candidate, this.gate, signal);

    const newScore = verdict.candidateSummary.meanScore;
    let applied: string;
    if (verdict.shouldPromote) {
      await this.onPromote(verdict, signal);
      const prev = this.bestScores.get(suiteId);
      this.bestScores.set(suiteId, prev === undefined ? newScore : Math.max(prev, newScore));
      applied = `promoted candidate (${verdict.reason})`;
    } else {
      applied = `rejected (${verdict.reason})`;
    }
    return { improvementsApplied: applied, newBenchScore: newScore };
  }

  bestScoreFor(suiteId: string): number {
    return this.bestScores.get(suiteId) ?? 0;
  }
}
