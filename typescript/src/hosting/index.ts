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
} from "./observers.js";
export type {
  IAIObserver,
  AIChatEvent,
  AIStreamEvent,
  AIToolEvent,
  IPushNotificationSender,
  ICircleAetherTransport,
} from "./observers.js";

// ── Options ────────────────────────────────────────────────────────────────────
export { DEFAULT_AI_OPTIONS, generateRandomToken } from "./options.js";
export type { AIOptions } from "./options.js";

// ── Tool bridge ─────────────────────────────────────────────────────────────────
export { NullToolBridge } from "./tool_bridge.js";
export type { IToolBridge } from "./tool_bridge.js";

// ── Voice ────────────────────────────────────────────────────────────────────────
export { DEFAULT_VOICE_OPTIONS } from "./voice.js";
export type { VoiceOptions } from "./voice.js";

// ── Skills (enrichment seam) ──────────────────────────────────────────────────────
export {
  SkillSource,
  InMemorySkillStore,
  SkillContextBuilder,
} from "./skills.js";
export type {
  ISkillStore,
  SkillSummary,
  SkillDetail,
  SkillDraft,
} from "./skills.js";

// ── Service ───────────────────────────────────────────────────────────────────────
export { AIService } from "./service.js";
export type { IAIService, IFallbackChainModelSelector } from "./service.js";

// ── Endpoints + HTTP transport seam ─────────────────────────────────────────────
export {
  InProcessEndpoint,
  HttpLoopbackEndpoint,
  LoopbackHttpTransport,
  AIHttpClient,
} from "./endpoints.js";
export type {
  IAIEndpoint,
  IHttpTransport,
  HttpResponse,
} from "./endpoints.js";

// ── Cloud proxy + fallback wrapper ───────────────────────────────────────────────
export { AIApiClient } from "./ai_api_client.js";
export { FallbackAIService } from "./fallback_service.js";
export type { RamProbe } from "./fallback_service.js";

// ── Background worker ─────────────────────────────────────────────────────────────
export { BackgroundInferenceWorker } from "./background_worker.js";

// ── Cron parser ───────────────────────────────────────────────────────────────────
export { getNextOccurrence, CronScheduleError } from "./cron_schedule_parser.js";

// ── Cron job models ─────────────────────────────────────────────────────────────
export {
  DeliveryTargetValues,
  CronJobStateValues,
  cronJob,
} from "./cron_job_models.js";
export type { DeliveryTarget, CronJobState, CronJob } from "./cron_job_models.js";

// ── Scheduled task store + service ──────────────────────────────────────────────
export { InMemoryScheduledTaskStore } from "./scheduled_store.js";
export type { IScheduledTaskStore } from "./scheduled_store.js";
export { ScheduledAIService } from "./scheduled.js";
export type {
  JobCompletedEventArgs,
  JobCompletedHandler,
} from "./scheduled.js";

// ── Triggers ───────────────────────────────────────────────────────────────────
export { ScheduleTrigger, IdleTrigger } from "./triggers.js";
export type { ITriggerCondition, ProactiveContext } from "./triggers.js";

// ── Proactive reasoning ─────────────────────────────────────────────────────────
export { ProactiveReasoningService } from "./proactive_reasoning.js";
export type {
  IProactiveReasoningService,
  ProactiveMessageEventArgs,
  ProactiveMessageHandler,
} from "./proactive_reasoning.js";

// ── Thermal ─────────────────────────────────────────────────────────────────────
export { ThermalState, ThermalThrottleService } from "./thermal.js";
export type {
  IThermalThrottleService,
  ThermalSampler,
  ThermalStateHandler,
} from "./thermal.js";

// ── Memory pressure ──────────────────────────────────────────────────────────────
export {
  MemoryPressureLevel,
  NullMemoryPressureSource,
  ManualMemoryPressureSource,
} from "./memory_pressure.js";
export type {
  IMemoryPressureSource,
  MemoryPressureHandler,
} from "./memory_pressure.js";

// ── Predictive warmup ─────────────────────────────────────────────────────────────
export {
  HistogramRequestPredictor,
  PredictiveWarmupController,
  DEFAULT_PREDICTIVE_WARMUP_OPTIONS,
} from "./warmup.js";
export type {
  IRequestPredictor,
  ArrivalForecast,
  PredictiveWarmupOptions,
} from "./warmup.js";

// ── Tool catalog ─────────────────────────────────────────────────────────────────
export { InMemoryToolCatalog, importFromAsync } from "./tool_catalog.js";
export type {
  ToolDescriptor,
  ToolExecutionResult,
  IToolCatalog,
  IToolProvider,
  IToolExecutor,
} from "./tool_catalog.js";

// ── Generative UI ─────────────────────────────────────────────────────────────────
export {
  UiCatalogs,
  RecordingGenerativeUIRenderer,
  JsonRenderError,
  parseRender,
  describeCatalogForPrompt,
} from "./generative_ui.js";
export type {
  UiComponent,
  UiCatalogEntry,
  IGenerativeUIRenderer,
} from "./generative_ui.js";

// ── Sub-host: CloudFallback ─────────────────────────────────────────────────────
export * from "./cloud_fallback/index.js";

// ── Sub-host: InferenceBridge ────────────────────────────────────────────────────
// NOTE: CircleAI.Hosting.InferenceBridge (IInferenceBridge, ModelDescriptor,
// ModelFormat, InferenceStatus, InferenceRequest/Response,
// LocalProcessInferenceBridge, InferenceFragment[Kind], DeviceCapabilities) is
// already ported at parity in ../inference/server/bridge.ts and barrel-exported
// via ./inference/server/index.js — not re-ported here to avoid a duplicate.

// ── Sub-host: Mcp ─────────────────────────────────────────────────────────────────
export * from "./mcp/index.js";

// ── Sub-host: Multiplayer ────────────────────────────────────────────────────────
export * from "./multiplayer/index.js";
