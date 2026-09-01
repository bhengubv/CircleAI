/**
 * telephony/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// telephony/index.ts
//
// Barrel for the CircleAI.Telephony port — the carrier-agnostic voice-agent
// stack: sentence chunking, call-cost tracking, DTMF tone generation, barge-in,
// answering-machine + IVR-loop detection, warm transfer, tool-calling (+ circuit
// breaker + streaming progress + MCP import), guardrails, LLM-as-judge, eval
// sessions, latency + false-interruption tracking, reassurance fillers,
// speculative generation, prompt-variable resolution, stereo call recording,
// hold-music mixing, phone-number provisioning, voice-loop-as-a-tool, agent
// handoff, consult escalation, dashboard data, speech-lifecycle events, first-
// message preamble, an in-memory test call session, and OTel-style telemetry.
// Faithful port of the CircleAI.Telephony C# project (C# is the exact spec).
//
// PLATFORM / HTTP SEAMS. Carrier integrations (Twilio/Telnyx/Plivo) and the MCP
// / consult webhooks are HTTP boundaries: the C# injects `HttpClient`; the port
// injects `IHttpClient` (contracts.ts). Native tracing (`ActivitySource`) is
// injected behind `IActivitySource` with a `NullActivitySource` default. The
// carrier itself is injected behind `ITelephonyCarrier`, with `NullTelephony-
// Carrier` / `NullInboundCallDispatcher` as fail-soft defaults.
//
// NAME NOTE. Telephony's tool-calling records (`ToolDefinition`, `ToolInvocation`,
// `ToolResult`) share names with `CircleAI.Tools`. They keep their natural names
// here; the package-root barrel re-exports them as `TelephonyTool*` to avoid the
// clash (see typescript/src/index.ts).

// ── Value primitives (Primitives.cs) ─────────────────────────────────────────
export {
  CallDirection,
  CallStatus,
  CallMediaFormat,
  TransferMode,
  callInfo,
  callSnapshot,
  audioFrame,
  dtmfEvent,
  provisionedNumber,
} from "./primitives";
export type {
  CallInfo,
  CallSnapshot,
  AudioFrame,
  DtmfEvent,
  ProvisionedNumber,
} from "./primitives";

// ── Contracts + HTTP seam (Contracts.cs, IMediaStream.cs, IDtmfSendable.cs) ───
export { isSuccessStatusCode, isDtmfSendable, DEFAULT_RING_TIMEOUT_SECONDS } from "./contracts";
export type {
  HttpMethod,
  HttpRequest,
  HttpResponse,
  IHttpClient,
  OutboundDialOptions,
  CallStatusChangedHandler,
  ICallSession,
  ITelephonyCarrier,
  ISubscription,
  IInboundCallDispatcher,
  IMediaStream,
  IDtmfSendable,
  BriefingSynthesiser,
} from "./contracts";

// ── Cancellation / timing helpers (idiom shims) ──────────────────────────────
export {
  OperationCancelledError,
  throwIfAborted,
  delay,
  utcNow,
  LinkedTimeout,
  isCancellation,
  AsyncChannel,
} from "./internal";

// ── Null* fail-soft defaults (NullImplementations.cs) ─────────────────────────
export { NullTelephonyCarrier, NullInboundCallDispatcher } from "./null_impls";

// ── SentenceChunker.cs ────────────────────────────────────────────────────────
export { SentenceChunker } from "./sentence_chunker";

// ── CallCostCalculator.cs ─────────────────────────────────────────────────────
export { CallCostCalculator, callPricing } from "./call_cost_calculator";
export type { CallPricing, CallCostBreakdown } from "./call_cost_calculator";

// ── DtmfToneGenerator.cs ──────────────────────────────────────────────────────
export { DtmfToneGenerator } from "./dtmf_tone_generator";

// ── BargeInController.cs ──────────────────────────────────────────────────────
export { BargeInState, BargeInController } from "./barge_in_controller";
export type { BargeInTransition, BargeInOptions } from "./barge_in_controller";

// ── AnsweringMachineDetector.cs ───────────────────────────────────────────────
export { AmdVerdict, AnsweringMachineDetector } from "./answering_machine_detector";
export type { AmdOptions } from "./answering_machine_detector";

// ── IvrLoopDetector.cs ────────────────────────────────────────────────────────
export { IvrLoopDetector, ivrRound } from "./ivr_loop_detector";
export type { IvrRound, IvrLoopVerdict } from "./ivr_loop_detector";

// ── ToolCalling.cs (tool-calling records + registry) ─────────────────────────
export {
  DefaultToolCallRegistry,
  toolDefinition,
  toolInvocation,
  toolResult,
} from "./tool_calling";
export type {
  ToolDefinition,
  ToolInvocation,
  ToolResult,
  LocalToolHandler,
  IToolCallRegistry,
  ILogger,
} from "./tool_calling";

// ── ToolCircuitBreaker.cs ─────────────────────────────────────────────────────
export { ToolBreakerState, CircuitBreakerToolRegistry } from "./tool_circuit_breaker";
export type { ToolCallPolicy } from "./tool_circuit_breaker";

// ── StreamingToolProgress.cs ──────────────────────────────────────────────────
export {
  toolProgressUpdate,
  SpokenToolProgressSink,
  RecordingToolProgressSink,
  StreamingToolRunner,
} from "./streaming_tool_progress";
export type {
  ToolProgressUpdate,
  IToolProgressSink,
  StreamingToolHandler,
} from "./streaming_tool_progress";

// ── McpToolImporter.cs ────────────────────────────────────────────────────────
export { HttpMcpToolImporter } from "./mcp_tool_importer";
export type { McpToolDescriptor, McpServerConfig, IMcpToolImporter } from "./mcp_tool_importer";

// ── WarmTransferOrchestrator.cs ───────────────────────────────────────────────
export { DefaultWarmTransferOrchestrator } from "./warm_transfer_orchestrator";
export type {
  WarmTransferRequest,
  WarmTransferResult,
  IWarmTransferOrchestrator,
} from "./warm_transfer_orchestrator";

// ── Guardrails.cs ─────────────────────────────────────────────────────────────
export { GuardrailAction, Guardrails, CommonGuardrails, guardrailRule } from "./guardrails";
export type { GuardrailRule, GuardrailResult } from "./guardrails";

// ── LlmJudge.cs ───────────────────────────────────────────────────────────────
export { LlmJudge, judgeDimension } from "./llm_judge";
export type { JudgeDimension, JudgeVerdict, JudgeCompletion } from "./llm_judge";

// ── EvalSession.cs ────────────────────────────────────────────────────────────
export { EvalSession, evalTurn } from "./eval_session";
export type { EvalTurn, EvalTurnResult, EvalRunResult, EvalTurnHandler } from "./eval_session";

// ── LatencyTracker.cs ─────────────────────────────────────────────────────────
export { LatencyStage, LatencyTracker } from "./latency_tracker";
export type { LatencySnapshot } from "./latency_tracker";

// ── FalseInterruptionTracker.cs ───────────────────────────────────────────────
export { InMemoryFalseInterruptionTracker } from "./false_interruption_tracker";
export type { InterruptionStats, IFalseInterruptionTracker } from "./false_interruption_tracker";

// ── ReassuranceFiller.cs ──────────────────────────────────────────────────────
export { DefaultReassuranceFiller, DEFAULT_REASSURANCE_VOCABULARY } from "./reassurance_filler";
export type {
  ReassuranceVocabulary,
  ReassuranceFillerOptions,
  IReassuranceFiller,
} from "./reassurance_filler";

// ── SpeculativeGenerator.cs ───────────────────────────────────────────────────
export { DefaultSpeculativeGenerator } from "./speculative_generator";
export type {
  SpeculativeBranch,
  ResponseGenerator,
  ISpeculativeGenerator,
} from "./speculative_generator";

// ── PromptVariableResolver.cs ─────────────────────────────────────────────────
export { PromptVariableResolver } from "./prompt_variable_resolver";
export type { PromptVariableProvider } from "./prompt_variable_resolver";

// ── StereoCallRecorder.cs ─────────────────────────────────────────────────────
export { StereoCallRecorder } from "./stereo_call_recorder";

// ── HoldMusicMixer.cs ─────────────────────────────────────────────────────────
export { HoldMusicMixer } from "./hold_music_mixer";

// ── PhoneNumberProvisioner.cs ─────────────────────────────────────────────────
export { PhoneNumberProvisioner, InMemoryProvisionedNumberStore } from "./phone_number_provisioner";
export type { IProvisionedNumberStore } from "./phone_number_provisioner";

// ── VoiceLoopAsTool.cs ────────────────────────────────────────────────────────
export { VoiceLoopAsTool } from "./voice_loop_as_tool";
export type {
  VoiceLoopToolRequest,
  VoiceLoopToolResult,
  IVoiceLoopTool,
  VoiceLoopRunner,
} from "./voice_loop_as_tool";

// ── AgentHandoff.cs ───────────────────────────────────────────────────────────
export { DefaultAgentHandoffOrchestrator, callAgent } from "./agent_handoff";
export type { CallAgent, HandoffResult, IAgentHandoffOrchestrator } from "./agent_handoff";

// ── ConsultEscalation.cs ──────────────────────────────────────────────────────
export { ConsultEscalator, HttpWebhookConsultChannel, consultRequest } from "./consult_escalation";
export type { ConsultRequest, ConsultAnswer, IConsultChannel } from "./consult_escalation";

// ── DashboardData.cs ──────────────────────────────────────────────────────────
export { DefaultDashboardDataSource, dashboardSummary } from "./dashboard_data";
export type {
  LiveCallRow,
  RecentCallRow,
  AgentHealthRow,
  DashboardSummary,
  DashboardSnapshot,
  IDashboardDataSource,
} from "./dashboard_data";

// ── SpeechLifecycleEvents.cs ──────────────────────────────────────────────────
export { ALL_EVENTS, InMemorySpeechLifecycleBus } from "./speech_lifecycle_events";
export type {
  SpeechLifecycleEventBase,
  CallerSpeechStartedEvent,
  CallerSpeechEndedEvent,
  TranscriptInterimEvent,
  TranscriptFinalEvent,
  AgentThinkingEvent,
  AgentSpeakingStartedEvent,
  AgentSpeakingFinishedEvent,
  SpeechErrorEvent,
  SpeechLifecycleEvent,
  SpeechEventKind,
  ISpeechSubscription,
  ISpeechLifecycleBus,
} from "./speech_lifecycle_events";

// ── FirstMessagePreamble.cs ───────────────────────────────────────────────────
export { DefaultFirstMessagePreamble } from "./first_message_preamble";
export type {
  FirstMessagePreambleOptions,
  IFirstMessagePreamble,
} from "./first_message_preamble";

// ── TestCallSession.cs ────────────────────────────────────────────────────────
export { TestCallSession } from "./test_call_session";

// ── Telemetry.cs ──────────────────────────────────────────────────────────────
export {
  ActivityKind,
  ActivityStatusCode,
  NullActivitySource,
  VoiceLoopTelemetry,
} from "./telemetry";
export type { IActivity, IActivitySource } from "./telemetry";
