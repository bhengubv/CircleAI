// companion/herjarvis/index.ts
//
// Barrel for the HER/Jarvis companion contracts + their deterministic in-memory
// implementations, ported from CircleAI.Companion.HerJarvis
// (HerJarvisContracts.cs + HerJarvisRealImplementations.cs) and the companion
// bridges (IVoiceListener.cs / VoiceCompanionListener.cs) and the SelfBench
// self-improvement orchestrator (SelfBenchSelfImprovementLoop.cs).
//
// The four reasoning-core contracts (IWorldModel / ITheoryOfMind /
// IInnerMonologue / IPredictiveEngine) and their impls live in ../reasoning and
// are re-exported from ../reasoning/index.js by the companion barrel; the
// contracts are also re-exported through ./contracts.js so this surface is whole.

// Contracts + supporting records (the 20 declared here). The four reasoning-core
// contracts (IWorldModel / ITheoryOfMind / IInnerMonologue / IPredictiveEngine)
// are intentionally NOT re-exported here — they reach the package surface via
// ../reasoning/index.js, so re-exporting them again would duplicate the name in
// the top-level barrel. They remain available inside this subtree through
// ./contracts.js for local use.
export type {
  // 1
  IAlwaysOnPresence,
  // 2
  FusedPercept,
  IFusedPerception,
  // 3
  IIdentitySync,
  // 4
  IContinuousLearner,
  // 6
  LongHorizonGoal,
  IGoalPursuer,
  // 7
  EpisodeRecord,
  IEpisodicMemory,
  // 8
  IVoiceIdentity,
  // 9
  ConfidenceBand,
  ICalibratedConfidence,
  // 11
  EmotionFrame,
  IEmotionSensor,
  // 12
  AcquiredSkill,
  ISkillAcquisition,
  // 15
  KnowledgeNode,
  KnowledgeRelation,
  IPersonalKnowledgeGraph,
  // 16
  WorldFact,
  ILiveWorldKnowledge,
  // 17
  BioSignal,
  IBioSignalStream,
  // 18
  PhysicalCommand,
  PhysicalCommandResult,
  IPhysicalActuator,
  // 19
  AgentToAgentMessage,
  IAgentPeerNetwork,
  // 20
  FineTuneJobStatus,
  IFederatedFineTuner,
  // 21
  FirstTokenBudget,
  IFirstTokenOptimizer,
  // 22
  DelegationCredential,
  ICryptoDelegation,
  // 23
  CodeGenJob,
  ICodeGenerationLoop,
  // 24
  SelfImprovementVerdict,
  ISelfImprovementLoop,
} from "./contracts.js";

// Stream/channel implementations.
export {
  HeartbeatAlwaysOnPresence,
  ChannelFusedPerception,
  TopicLiveWorldKnowledge,
  ChannelBioSignalStream,
  MailboxAgentPeerNetwork,
} from "./streams.js";
export { AsyncQueue } from "./async_queue.js";

// Store / math implementations.
export {
  JsonIdentitySync,
  EwaContinuousLearner,
  InMemoryGoalPursuer,
  TfEpisodicMemory,
  HistoricalCalibratedConfidence,
  KeywordEmotionSensor,
  DemoStoreSkillAcquisition,
  AdjacencyPersonalKnowledgeGraph,
  toRoundTripUtc,
} from "./stores.js";

// Voice identity (MFCC).
export { EnergyBandVoiceIdentity } from "./voice_identity.js";

// Actuation / fine-tune / latency / codegen / self-improvement loops.
export {
  RegistryPhysicalActuator,
  InMemoryFederatedFineTuner,
  SlidingP50FirstTokenOptimizer,
  SyntaxCheckingCodeGenerationLoop,
  TrackingSelfImprovementLoop,
  isSyntacticallyBalanced,
} from "./loops.js";
export type {
  PhysicalDeviceHandler,
  FineTuneTrainer,
  CodeGenerator,
  CodeTestRunner,
  CodeDeploymentHint,
  BenchRunner,
  ImprovementProposer,
} from "./loops.js";

// Cryptographic delegation (ECDSA P-256).
export { EcdsaCryptoDelegation, generateP256KeyPair } from "./crypto_delegation.js";
export type { EcdsaKeyPair } from "./crypto_delegation.js";

// SelfBench-backed self-improvement loop.
export { SelfBenchSelfImprovementLoop, defaultRegressionGateConfig } from "./selfbench_loop.js";
export type {
  BenchTask,
  BenchSummary,
  RegressionGateConfig,
  AbVerdict,
  IBenchSuiteRegistry,
  IAIService,
  IAbBenchRunner,
  AiServiceFactory,
  PromoteCallback,
} from "./selfbench_loop.js";

// Voice listener bridge.
export { VoiceCompanionListener } from "./voice_listener.js";
export type {
  IVoiceListener,
  IVoicePipeline,
  UtteranceDetectedEventArgs,
  ResponseReadyEventArgs,
  UtteranceDetectedHandler,
  ResponseReadyHandler,
  TranscriptionResult,
  TranscribedEventArgs,
  TranscribedHandler,
} from "./voice_listener.js";

// Guid helper (exported for tests / callers that want the "n" format).
export { newGuidN } from "./guid.js";
