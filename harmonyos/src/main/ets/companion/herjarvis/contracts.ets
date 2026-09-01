/**
 * companion/herjarvis/contracts
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// companion/herjarvis/contracts.ts
//
// The HER/Jarvis-level companion contracts + supporting records, ported
// verbatim from CircleAI.Companion.HerJarvis (HerJarvisContracts.cs).
//
// Four of the 24 contracts (5 IWorldModel, 10 ITheoryOfMind, 13 IInnerMonologue,
// 14 IPredictiveEngine) already live in ../reasoning/contracts.ts; those are
// re-exported here so the HerJarvis surface is complete without duplicating the
// type declarations. This file declares the remaining 20.
//
// C# uses ValueTask/CancellationToken; TypeScript models both as a Promise (or
// AsyncGenerator for IAsyncEnumerable). Cancellation is cooperative and, in the
// deterministic in-memory implementations, never actually observed, so there is
// no CancellationToken parameter to carry. Interface names keep the C# `I`
// prefix; record property names are camelCased.

// Re-export the four reasoning-core contracts so the HerJarvis barrel is whole.
export type {
  CausalPrediction, // 5.  IWorldModel
  IWorldModel,
  OtherMindEstimate, // 10. ITheoryOfMind
  ITheoryOfMind,
  SelfReflection, // 13. IInnerMonologue
  IInnerMonologue,
  AnticipatedNeed, // 14. IPredictiveEngine
  IPredictiveEngine,
} from "../reasoning/contracts";

// =====================================================================
// 1. Always-on background presence across all devices.
// =====================================================================

/** Mirrors CircleAI.Companion.HerJarvis.IAlwaysOnPresence. */
export interface IAlwaysOnPresence {
  readonly isRunning: boolean;
  startAsync(): Promise<void>;
  stopAsync(): Promise<void>;
}

// =====================================================================
// 2. Fused perceptual stream.
// =====================================================================

/** Mirrors CircleAI.Companion.HerJarvis.FusedPercept. */
export interface FusedPercept {
  readonly at: Date;
  readonly vision: string | null;
  readonly audio: string | null;
  readonly text: string | null;
  readonly sensors: ReadonlyMap<string, number>;
}

/** Mirrors CircleAI.Companion.HerJarvis.IFusedPerception. */
export interface IFusedPerception {
  streamAsync(signal?: AbortSignal): AsyncGenerator<FusedPercept>;
}

// =====================================================================
// 3. Memory + identity sync across devices.
// =====================================================================

/** Mirrors CircleAI.Companion.HerJarvis.IIdentitySync. */
export interface IIdentitySync {
  pushAsync(deltaJson: string): Promise<void>;
  pullAsync(sinceCursor: string): Promise<string>;
}

// =====================================================================
// 4. Continuous online learning.
// =====================================================================

/** Mirrors CircleAI.Companion.HerJarvis.IContinuousLearner. */
export interface IContinuousLearner {
  registerFeedbackAsync(interactionId: string, reward: number, contextJson: string): Promise<void>;
}

// =====================================================================
// 6. Multi-month goal pursuit with replanning.
// =====================================================================

/** Mirrors CircleAI.Companion.HerJarvis.LongHorizonGoal. */
export interface LongHorizonGoal {
  readonly id: string;
  readonly description: string;
  readonly deadlineUtc: Date;
  readonly planJson: string;
  readonly progressFraction: number;
}

/** Mirrors CircleAI.Companion.HerJarvis.IGoalPursuer. */
export interface IGoalPursuer {
  registerAsync(description: string, deadlineUtc: Date): Promise<LongHorizonGoal>;
  currentAsync(id: string): Promise<LongHorizonGoal | null>;
  replanAsync(id: string): Promise<void>;
}

// =====================================================================
// 7. Episodic memory of lived experiences.
// =====================================================================

/** Mirrors CircleAI.Companion.HerJarvis.EpisodeRecord. */
export interface EpisodeRecord {
  readonly id: string;
  readonly at: Date;
  readonly title: string;
  readonly contentJson: string;
}

/** Mirrors CircleAI.Companion.HerJarvis.IEpisodicMemory. */
export interface IEpisodicMemory {
  recordAsync(episode: EpisodeRecord): Promise<void>;
  recallAsync(query: string, take?: number): Promise<readonly EpisodeRecord[]>;
}

// =====================================================================
// 8. Per-user voice continuity.
// =====================================================================

/** Mirrors CircleAI.Companion.HerJarvis.IVoiceIdentity. */
export interface IVoiceIdentity {
  /** Returns a stable voice fingerprint id (or null if unknown). */
  identifyAsync(audioPcm16: Uint8Array, sampleRateHz: number): Promise<string | null>;
  enrollAsync(userId: string, audioPcm16: Uint8Array, sampleRateHz: number): Promise<void>;
}

// =====================================================================
// 9. Calibrated uncertainty at orchestration.
// =====================================================================

/** Mirrors CircleAI.Companion.HerJarvis.ConfidenceBand. */
export interface ConfidenceBand {
  readonly lower: number;
  readonly upper: number;
}

/** Mirrors CircleAI.Companion.HerJarvis.ICalibratedConfidence. */
export interface ICalibratedConfidence {
  evaluateAsync(answer: string, contextJson: string): Promise<ConfidenceBand>;
}

// =====================================================================
// 11. Emotion sensing.
// =====================================================================

/** Mirrors CircleAI.Companion.HerJarvis.EmotionFrame. */
export interface EmotionFrame {
  readonly label: string;
  readonly arousal: number;
  readonly valence: number;
}

/** Mirrors CircleAI.Companion.HerJarvis.IEmotionSensor. */
export interface IEmotionSensor {
  senseAsync(fusedJson: string): Promise<EmotionFrame>;
}

// =====================================================================
// 12. Skill acquisition.
// =====================================================================

/** Mirrors CircleAI.Companion.HerJarvis.AcquiredSkill. */
export interface AcquiredSkill {
  readonly id: string;
  readonly name: string;
  readonly descriptionJson: string;
}

/** Mirrors CircleAI.Companion.HerJarvis.ISkillAcquisition. */
export interface ISkillAcquisition {
  acquireAsync(demonstrationJson: string): Promise<AcquiredSkill>;
  listAsync(): Promise<readonly AcquiredSkill[]>;
}

// =====================================================================
// 15. Personal knowledge graph.
// =====================================================================

/** Mirrors CircleAI.Companion.HerJarvis.KnowledgeNode. */
export interface KnowledgeNode {
  readonly id: string;
  readonly kind: string;
  readonly name: string;
  readonly properties: ReadonlyMap<string, string>;
}

/** Mirrors CircleAI.Companion.HerJarvis.KnowledgeRelation. */
export interface KnowledgeRelation {
  readonly fromId: string;
  readonly toId: string;
  readonly relation: string;
}

/** Mirrors CircleAI.Companion.HerJarvis.IPersonalKnowledgeGraph. */
export interface IPersonalKnowledgeGraph {
  upsertNodeAsync(node: KnowledgeNode): Promise<void>;
  upsertRelationAsync(rel: KnowledgeRelation): Promise<void>;
  neighboursAsync(id: string): Promise<readonly KnowledgeNode[]>;
}

// =====================================================================
// 16. Live world-knowledge stream.
// =====================================================================

/** Mirrors CircleAI.Companion.HerJarvis.WorldFact. */
export interface WorldFact {
  readonly topic: string;
  readonly summaryJson: string;
  readonly at: Date;
}

/** Mirrors CircleAI.Companion.HerJarvis.ILiveWorldKnowledge. */
export interface ILiveWorldKnowledge {
  subscribeAsync(topics: readonly string[], signal?: AbortSignal): AsyncGenerator<WorldFact>;
}

// =====================================================================
// 17. Bio-signal integration.
// =====================================================================

/** Mirrors CircleAI.Companion.HerJarvis.BioSignal. */
export interface BioSignal {
  readonly kind: string;
  readonly value: number;
  readonly at: Date;
}

/** Mirrors CircleAI.Companion.HerJarvis.IBioSignalStream. */
export interface IBioSignalStream {
  streamAsync(signal?: AbortSignal): AsyncGenerator<BioSignal>;
}

// =====================================================================
// 18. Robotics / physical actuation.
// =====================================================================

/** Mirrors CircleAI.Companion.HerJarvis.PhysicalCommand. */
export interface PhysicalCommand {
  readonly deviceId: string;
  readonly action: string;
  readonly args: ReadonlyMap<string, string>;
}

/** Mirrors CircleAI.Companion.HerJarvis.PhysicalCommandResult. */
export interface PhysicalCommandResult {
  readonly succeeded: boolean;
  readonly error: string | null;
}

/** Mirrors CircleAI.Companion.HerJarvis.IPhysicalActuator. */
export interface IPhysicalActuator {
  invokeAsync(command: PhysicalCommand): Promise<PhysicalCommandResult>;
}

// =====================================================================
// 19. Agent-to-agent peer protocol.
// =====================================================================

/** Mirrors CircleAI.Companion.HerJarvis.AgentToAgentMessage. */
export interface AgentToAgentMessage {
  readonly fromAgentId: string;
  readonly toAgentId: string;
  readonly payload: string;
  readonly at: Date;
}

/** Mirrors CircleAI.Companion.HerJarvis.IAgentPeerNetwork. */
export interface IAgentPeerNetwork {
  sendAsync(message: AgentToAgentMessage): Promise<void>;
  receiveAsync(forAgentId: string, signal?: AbortSignal): AsyncGenerator<AgentToAgentMessage>;
}

// =====================================================================
// 20. Federated / on-device fine-tune pipeline.
// =====================================================================

/** Mirrors CircleAI.Companion.HerJarvis.FineTuneJobStatus. */
export interface FineTuneJobStatus {
  readonly jobId: string;
  readonly progress: number;
  readonly error: string | null;
}

/** Mirrors CircleAI.Companion.HerJarvis.IFederatedFineTuner. */
export interface IFederatedFineTuner {
  startAsync(baseModel: string, trainingDataPath: string): Promise<string>;
  statusAsync(jobId: string): Promise<FineTuneJobStatus>;
}

// =====================================================================
// 21. Sub-100ms first-token latency on cheap phones.
// =====================================================================

/** Mirrors CircleAI.Companion.HerJarvis.FirstTokenBudget. */
export interface FirstTokenBudget {
  readonly targetMs: number;
  readonly currentP50Ms: number;
}

/** Mirrors CircleAI.Companion.HerJarvis.IFirstTokenOptimizer. */
export interface IFirstTokenOptimizer {
  currentAsync(): Promise<FirstTokenBudget>;
}

// =====================================================================
// 22. Cryptographic delegation framework.
// =====================================================================

/** Mirrors CircleAI.Companion.HerJarvis.DelegationCredential. */
export interface DelegationCredential {
  readonly issuer: string;
  readonly subjectId: string;
  readonly scope: string;
  readonly expiresAtUtc: Date;
  readonly signature: string;
}

/** Mirrors CircleAI.Companion.HerJarvis.ICryptoDelegation. */
export interface ICryptoDelegation {
  /** `lifetime` is a duration in milliseconds (C# TimeSpan). */
  issue(subjectId: string, scope: string, lifetimeMs: number): DelegationCredential;
  verify(credential: DelegationCredential): boolean;
}

// =====================================================================
// 23. Live code generation + test + deploy loop.
// =====================================================================

/** Mirrors CircleAI.Companion.HerJarvis.CodeGenJob. */
export interface CodeGenJob {
  readonly id: string;
  readonly prompt: string;
  readonly outputSnippet: string;
  readonly testsPass: boolean;
  readonly deployHint: string | null;
}

/** Mirrors CircleAI.Companion.HerJarvis.ICodeGenerationLoop. */
export interface ICodeGenerationLoop {
  runAsync(prompt: string): Promise<CodeGenJob>;
}

// =====================================================================
// 24. Self-debugging / self-improvement loop.
// =====================================================================

/** Mirrors CircleAI.Companion.HerJarvis.SelfImprovementVerdict. */
export interface SelfImprovementVerdict {
  readonly improvementsApplied: string;
  readonly newBenchScore: number;
}

/** Mirrors CircleAI.Companion.HerJarvis.ISelfImprovementLoop. */
export interface ISelfImprovementLoop {
  cycleAsync(benchSuiteId: string): Promise<SelfImprovementVerdict>;
}
