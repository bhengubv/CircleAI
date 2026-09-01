/**
 * hosting/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// hosting/index.ts
//
// Barrel for the CircleAI.Hosting runtime + sub-hosts ported to TypeScript.
// The runtime core (service, options, observers, endpoints, scheduling, cron,
// triggers, thermal, memory-pressure, warmup, tool catalog, generative UI,
// cloud fallback, background worker, skills, voice) plus the four sub-host
// projects (CloudFallback, InferenceBridge, Mcp, Multiplayer).

// ── Observers + event records + push/aether bridges ────────────────────────────
export {
  BrownoutReason,
  AIObserverBase,
  PushAIObserver,
  AetherAIObserver,
} from "./observers";
export type {
  IAIObserver,
  AIChatEvent,
  AIStreamEvent,
  AIToolEvent,
  IPushNotificationSender,
  ICircleAetherTransport,
} from "./observers";

// ── Options ────────────────────────────────────────────────────────────────────
export { DEFAULT_AI_OPTIONS, generateRandomToken } from "./options";
export type { AIOptions } from "./options";

// ── Tool bridge ─────────────────────────────────────────────────────────────────
export { NullToolBridge } from "./tool_bridge";
export type { IToolBridge } from "./tool_bridge";

// ── Voice ────────────────────────────────────────────────────────────────────────
export { DEFAULT_VOICE_OPTIONS } from "./voice";
export type { VoiceOptions } from "./voice";

// ── Skills (enrichment seam) ──────────────────────────────────────────────────────
export {
  SkillSource,
  InMemorySkillStore,
  SkillContextBuilder,
} from "./skills";
export type {
  ISkillStore,
  SkillSummary,
  SkillDetail,
  SkillDraft,
} from "./skills";

// ── Service ───────────────────────────────────────────────────────────────────────
export { AIService } from "./service";
export type { IAIService, IFallbackChainModelSelector } from "./service";

// ── Chat runtime seam (host-neutral) ────────────────────────────────────────────
export { NullChatRuntime, isPersistable, chatTurn } from "./chat_runtime";
export type {
  IChatRuntime,
  IPersistableChatRuntime,
  ChatTurn,
} from "./chat_runtime";

// ── Neuron (concierge two-slot node) ────────────────────────────────────────────
export {
  Organ,
  NeuronGate,
  HeuristicNeuronRouter,
  ResidentSlotManager,
  SlotOutcome,
  NeuronNode,
  generalistDecision,
  specialistDecision,
} from "./neuron/index";
export type {
  INeuronRouter,
  RouteContext,
  RouteDecision,
  SlotAdmission,
  INeuronBrain,
} from "./neuron/index";

// ── Endpoints + HTTP transport seam ─────────────────────────────────────────────
export {
  InProcessEndpoint,
  HttpLoopbackEndpoint,
  LoopbackHttpTransport,
  AIHttpClient,
} from "./endpoints";
export type {
  IAIEndpoint,
  IHttpTransport,
  HttpResponse,
} from "./endpoints";

// ── Cloud proxy + fallback wrapper ───────────────────────────────────────────────
export { AIApiClient } from "./ai_api_client";
export { FallbackAIService } from "./fallback_service";
export type { RamProbe } from "./fallback_service";

// ── Background worker ─────────────────────────────────────────────────────────────
export { BackgroundInferenceWorker } from "./background_worker";

// ── Cron parser ───────────────────────────────────────────────────────────────────
export { getNextOccurrence, CronScheduleError } from "./cron_schedule_parser";

// ── Cron job models ─────────────────────────────────────────────────────────────
export {
  DeliveryTargetValues,
  CronJobStateValues,
  cronJob,
} from "./cron_job_models";
export type { DeliveryTarget, CronJobState, CronJob } from "./cron_job_models";

// ── Scheduled task store + service ──────────────────────────────────────────────
export { InMemoryScheduledTaskStore } from "./scheduled_store";
export type { IScheduledTaskStore } from "./scheduled_store";
export { ScheduledAIService } from "./scheduled";
export type {
  JobCompletedEventArgs,
  JobCompletedHandler,
} from "./scheduled";

// ── Triggers ───────────────────────────────────────────────────────────────────
export { ScheduleTrigger, IdleTrigger } from "./triggers";
export type { ITriggerCondition, ProactiveContext } from "./triggers";

// ── Proactive reasoning ─────────────────────────────────────────────────────────
export { ProactiveReasoningService } from "./proactive_reasoning";
export type {
  IProactiveReasoningService,
  ProactiveMessageEventArgs,
  ProactiveMessageHandler,
} from "./proactive_reasoning";

// ── Thermal ─────────────────────────────────────────────────────────────────────
export { ThermalState, ThermalThrottleService } from "./thermal";
export type {
  IThermalThrottleService,
  ThermalSampler,
  ThermalStateHandler,
} from "./thermal";

// ── Memory pressure ──────────────────────────────────────────────────────────────
export {
  MemoryPressureLevel,
  NullMemoryPressureSource,
  ManualMemoryPressureSource,
} from "./memory_pressure";
export type {
  IMemoryPressureSource,
  MemoryPressureHandler,
} from "./memory_pressure";

// ── Predictive warmup ─────────────────────────────────────────────────────────────
export {
  HistogramRequestPredictor,
  PredictiveWarmupController,
  DEFAULT_PREDICTIVE_WARMUP_OPTIONS,
} from "./warmup";
export type {
  IRequestPredictor,
  ArrivalForecast,
  PredictiveWarmupOptions,
} from "./warmup";

// ── Tool catalog ─────────────────────────────────────────────────────────────────
export { InMemoryToolCatalog, importFromAsync } from "./tool_catalog";
export type {
  ToolDescriptor,
  ToolExecutionResult,
  IToolCatalog,
  IToolProvider,
  IToolExecutor,
} from "./tool_catalog";

// ── Generative UI ─────────────────────────────────────────────────────────────────
export {
  UiCatalogs,
  RecordingGenerativeUIRenderer,
  JsonRenderError,
  parseRender,
  describeCatalogForPrompt,
} from "./generative_ui";
export type {
  UiComponent,
  UiCatalogEntry,
  IGenerativeUIRenderer,
} from "./generative_ui";

// ── Sub-host: CloudFallback ─────────────────────────────────────────────────────
export * from "./cloud_fallback/index";

// ── Sub-host: InferenceBridge ────────────────────────────────────────────────────
// NOTE: CircleAI.Hosting.InferenceBridge (IInferenceBridge, ModelDescriptor,
// ModelFormat, InferenceStatus, InferenceRequest/Response,
// LocalProcessInferenceBridge, InferenceFragment[Kind], DeviceCapabilities) is
// already ported at parity in ../inference/server/bridge.ts and barrel-exported
// via ./inference/server/index.js — not re-ported here to avoid a duplicate.

// ── Sub-host: Mcp ─────────────────────────────────────────────────────────────────
export * from "./mcp/index";

// ── Sub-host: Multiplayer ────────────────────────────────────────────────────────
export * from "./multiplayer/index";
