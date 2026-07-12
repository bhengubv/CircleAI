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
} from "./primitives.js";
export type {
  CallInfo,
  CallSnapshot,
  AudioFrame,
  DtmfEvent,
  ProvisionedNumber,
} from "./primitives.js";

// ── Contracts + HTTP seam (Contracts.cs, IMediaStream.cs, IDtmfSendable.cs) ───
export { isSuccessStatusCode, isDtmfSendable, DEFAULT_RING_TIMEOUT_SECONDS } from "./contracts.js";
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
} from "./contracts.js";

// ── Cancellation / timing helpers (idiom shims) ──────────────────────────────
export {
  OperationCancelledError,
  throwIfAborted,
  delay,
  utcNow,
  LinkedTimeout,
  isCancellation,
  AsyncChannel,
} from "./internal.js";

// ── Null* fail-soft defaults (NullImplementations.cs) ─────────────────────────
export { NullTelephonyCarrier, NullInboundCallDispatcher } from "./null_impls.js";

// ── SentenceChunker.cs ────────────────────────────────────────────────────────
export { SentenceChunker } from "./sentence_chunker.js";

// ── CallCostCalculator.cs ─────────────────────────────────────────────────────
export { CallCostCalculator, callPricing } from "./call_cost_calculator.js";
export type { CallPricing, CallCostBreakdown } from "./call_cost_calculator.js";

// ── DtmfToneGenerator.cs ──────────────────────────────────────────────────────
export { DtmfToneGenerator } from "./dtmf_tone_generator.js";

// ── BargeInController.cs ──────────────────────────────────────────────────────
export { BargeInState, BargeInController } from "./barge_in_controller.js";
export type { BargeInTransition, BargeInOptions } from "./barge_in_controller.js";

// ── AnsweringMachineDetector.cs ───────────────────────────────────────────────
export { AmdVerdict, AnsweringMachineDetector } from "./answering_machine_detector.js";
export type { AmdOptions } from "./answering_machine_detector.js";

// ── IvrLoopDetector.cs ────────────────────────────────────────────────────────
export { IvrLoopDetector, ivrRound } from "./ivr_loop_detector.js";
export type { IvrRound, IvrLoopVerdict } from "./ivr_loop_detector.js";

// ── ToolCalling.cs (tool-calling records + registry) ─────────────────────────
export {
  DefaultToolCallRegistry,
  toolDefinition,
  toolInvocation,
  toolResult,
} from "./tool_calling.js";
export type {
  ToolDefinition,
  ToolInvocation,
  ToolResult,
  LocalToolHandler,
  IToolCallRegistry,
  ILogger,
} from "./tool_calling.js";

// ── ToolCircuitBreaker.cs ─────────────────────────────────────────────────────
export { ToolBreakerState, CircuitBreakerToolRegistry } from "./tool_circuit_breaker.js";
export type { ToolCallPolicy } from "./tool_circuit_breaker.js";

// ── StreamingToolProgress.cs ──────────────────────────────────────────────────
export {
  toolProgressUpdate,
  SpokenToolProgressSink,
  RecordingToolProgressSink,
  StreamingToolRunner,
} from "./streaming_tool_progress.js";
export type {
  ToolProgressUpdate,
  IToolProgressSink,
  StreamingToolHandler,
} from "./streaming_tool_progress.js";

// ── McpToolImporter.cs ────────────────────────────────────────────────────────
export { HttpMcpToolImporter } from "./mcp_tool_importer.js";
export type { McpToolDescriptor, McpServerConfig, IMcpToolImporter } from "./mcp_tool_importer.js";

// ── WarmTransferOrchestrator.cs ───────────────────────────────────────────────
export { DefaultWarmTransferOrchestrator } from "./warm_transfer_orchestrator.js";
export type {
  WarmTransferRequest,
  WarmTransferResult,
  IWarmTransferOrchestrator,
} from "./warm_transfer_orchestrator.js";

// ── Guardrails.cs ─────────────────────────────────────────────────────────────
export { GuardrailAction, Guardrails, CommonGuardrails, guardrailRule } from "./guardrails.js";
export type { GuardrailRule, GuardrailResult } from "./guardrails.js";

// ── LlmJudge.cs ───────────────────────────────────────────────────────────────
export { LlmJudge, judgeDimension } from "./llm_judge.js";
export type { JudgeDimension, JudgeVerdict, JudgeCompletion } from "./llm_judge.js";

// ── EvalSession.cs ────────────────────────────────────────────────────────────
export { EvalSession, evalTurn } from "./eval_session.js";
export type { EvalTurn, EvalTurnResult, EvalRunResult, EvalTurnHandler } from "./eval_session.js";

// ── LatencyTracker.cs ─────────────────────────────────────────────────────────
export { LatencyStage, LatencyTracker } from "./latency_tracker.js";
export type { LatencySnapshot } from "./latency_tracker.js";

// ── FalseInterruptionTracker.cs ───────────────────────────────────────────────
export { InMemoryFalseInterruptionTracker } from "./false_interruption_tracker.js";
export type { InterruptionStats, IFalseInterruptionTracker } from "./false_interruption_tracker.js";

// ── ReassuranceFiller.cs ──────────────────────────────────────────────────────
export { DefaultReassuranceFiller, DEFAULT_REASSURANCE_VOCABULARY } from "./reassurance_filler.js";
export type {
  ReassuranceVocabulary,
  ReassuranceFillerOptions,
  IReassuranceFiller,
} from "./reassurance_filler.js";

// ── SpeculativeGenerator.cs ───────────────────────────────────────────────────
export { DefaultSpeculativeGenerator } from "./speculative_generator.js";
export type {
  SpeculativeBranch,
  ResponseGenerator,
  ISpeculativeGenerator,
} from "./speculative_generator.js";

// ── PromptVariableResolver.cs ─────────────────────────────────────────────────
export { PromptVariableResolver } from "./prompt_variable_resolver.js";
export type { PromptVariableProvider } from "./prompt_variable_resolver.js";

// ── StereoCallRecorder.cs ─────────────────────────────────────────────────────
export { StereoCallRecorder } from "./stereo_call_recorder.js";

// ── HoldMusicMixer.cs ─────────────────────────────────────────────────────────
export { HoldMusicMixer } from "./hold_music_mixer.js";

// ── PhoneNumberProvisioner.cs ─────────────────────────────────────────────────
export { PhoneNumberProvisioner, InMemoryProvisionedNumberStore } from "./phone_number_provisioner.js";
export type { IProvisionedNumberStore } from "./phone_number_provisioner.js";

// ── VoiceLoopAsTool.cs ────────────────────────────────────────────────────────
export { VoiceLoopAsTool } from "./voice_loop_as_tool.js";
export type {
  VoiceLoopToolRequest,
  VoiceLoopToolResult,
  IVoiceLoopTool,
  VoiceLoopRunner,
} from "./voice_loop_as_tool.js";

// ── AgentHandoff.cs ───────────────────────────────────────────────────────────
export { DefaultAgentHandoffOrchestrator, callAgent } from "./agent_handoff.js";
export type { CallAgent, HandoffResult, IAgentHandoffOrchestrator } from "./agent_handoff.js";

// ── ConsultEscalation.cs ──────────────────────────────────────────────────────
export { ConsultEscalator, HttpWebhookConsultChannel, consultRequest } from "./consult_escalation.js";
export type { ConsultRequest, ConsultAnswer, IConsultChannel } from "./consult_escalation.js";

// ── DashboardData.cs ──────────────────────────────────────────────────────────
export { DefaultDashboardDataSource, dashboardSummary } from "./dashboard_data.js";
export type {
  LiveCallRow,
  RecentCallRow,
  AgentHealthRow,
  DashboardSummary,
  DashboardSnapshot,
  IDashboardDataSource,
} from "./dashboard_data.js";

// ── SpeechLifecycleEvents.cs ──────────────────────────────────────────────────
export { ALL_EVENTS, InMemorySpeechLifecycleBus } from "./speech_lifecycle_events.js";
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
} from "./speech_lifecycle_events.js";

// ── FirstMessagePreamble.cs ───────────────────────────────────────────────────
export { DefaultFirstMessagePreamble } from "./first_message_preamble.js";
export type {
  FirstMessagePreambleOptions,
  IFirstMessagePreamble,
} from "./first_message_preamble.js";

// ── TestCallSession.cs ────────────────────────────────────────────────────────
export { TestCallSession } from "./test_call_session.js";

// ── Telemetry.cs ──────────────────────────────────────────────────────────────
export {
  ActivityKind,
  ActivityStatusCode,
  NullActivitySource,
  VoiceLoopTelemetry,
} from "./telemetry.js";
export type { IActivity, IActivitySource } from "./telemetry.js";
