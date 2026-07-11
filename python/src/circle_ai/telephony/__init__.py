"""circle_ai.telephony — port of the CircleAI.Telephony assembly.

(3.3.0) The carrier-agnostic telephony surface: place / receive phone calls,
provision numbers, and run a low-latency voice loop (barge-in, sentence
chunking, speculative generation, reassurance fillers, guardrails, tool-calling,
warm transfer, agent handoff, consult escalation) with cost + latency
observability. Any consumer (txtMe, Panik, a salon receptionist) talks to these
contracts; the real Twilio / Telnyx / Plivo adapters ship as sibling packages.
C# is the exact spec.

Two ports differ from the C# by design (the Python tree has no equivalents):

  * DI wiring — the ``TelephonyServiceCollectionExtensions`` extension methods
    (``AddCircleAiTelephony`` / ``AddCarrierFallback``) are NOT ported; wire the
    null defaults, ``PhoneNumberProvisioner``, and ``CarrierFallback`` via their
    constructors. The multi-carrier failover *logic* those extensions register
    lives in :mod:`circle_ai.telephony.carrier_fallback` as ``CarrierFallback``.
  * HTTP — the C# ``HttpClient`` consumers (tool webhooks, MCP import, consult
    webhook) take the shared ``circle_ai.integration.http.IHttpFetcher`` instead.
  * Telemetry — the C# ``ActivitySource`` / OpenTelemetry ``VoiceLoopTelemetry``
    is a faithful, dependency-free shim (same ``SOURCE_NAME`` + span surface).
"""
from __future__ import annotations

from .agent_handoff import (
    CallAgent,
    DefaultAgentHandoffOrchestrator,
    HandoffResult,
    IAgentHandoffOrchestrator,
)
from .answering_machine_detector import (
    AmdOptions,
    AmdVerdict,
    AnsweringMachineDetector,
)
from .barge_in_controller import (
    BargeInController,
    BargeInOptions,
    BargeInState,
    BargeInTransition,
)
from .call_cost_calculator import (
    CallCostBreakdown,
    CallCostCalculator,
    CallPricing,
)
from .carrier_fallback import CarrierFallback
from .consult_escalation import (
    ConsultAnswer,
    ConsultEscalator,
    ConsultRequest,
    HttpWebhookConsultChannel,
    IConsultChannel,
)
from .contracts import (
    ICallSession,
    IInboundCallDispatcher,
    ITelephonyCarrier,
    OutboundDialOptions,
    StatusChangedHandler,
)
from .dashboard_data import (
    AgentHealthRow,
    DashboardSnapshot,
    DashboardSummary,
    DefaultDashboardDataSource,
    IDashboardDataSource,
    LiveCallRow,
    RecentCallRow,
)
from .disposable import IDisposable
from .dtmf_sendable import IDtmfSendable
from . import dtmf_tone_generator as DtmfToneGenerator  # module-as-static-class
from .eval_session import (
    EvalRunResult,
    EvalSession,
    EvalTurn,
    EvalTurnHandler,
    EvalTurnResult,
)
from .false_interruption_tracker import (
    IFalseInterruptionTracker,
    InMemoryFalseInterruptionTracker,
    InterruptionStats,
)
from .first_message_preamble import (
    DefaultFirstMessagePreamble,
    FirstMessagePreambleOptions,
    IFirstMessagePreamble,
)
from .guardrails import (
    CommonGuardrails,
    GuardrailAction,
    GuardrailResult,
    GuardrailRule,
    Guardrails,
)
from .hold_music_mixer import HoldMusicMixer
from .ivr_loop_detector import IvrLoopDetector, IvrLoopVerdict, IvrRound
from .latency_tracker import LatencySnapshot, LatencyStage, LatencyTracker
from .llm_judge import JudgeCompletion, JudgeDimension, JudgeVerdict, LlmJudge
from .local_dev_tunnel import (
    CloudflareTunnel,
    ILocalDevTunnel,
    NgrokTunnel,
    NullLocalDevTunnel,
    StaticLocalDevTunnel,
    TunnelResolver,
)
from .mcp_tool_importer import (
    HttpMcpToolImporter,
    IMcpToolImporter,
    McpServerConfig,
    McpToolDescriptor,
)
from .media_stream import IMediaStream
from .null_implementations import (
    NullInboundCallDispatcher,
    NullTelephonyCarrier,
)
from .phone_number_provisioner import (
    IProvisionedNumberStore,
    InMemoryProvisionedNumberStore,
    PhoneNumberProvisioner,
)
from .primitives import (
    AudioFrame,
    CallDirection,
    CallInfo,
    CallMediaFormat,
    CallSnapshot,
    CallStatus,
    DtmfEvent,
    ProvisionedNumber,
    TransferMode,
)
from .prompt_variable_resolver import (
    PromptVariableProvider,
    PromptVariableResolver,
)
from .reassurance_filler import (
    DefaultReassuranceFiller,
    IReassuranceFiller,
    ReassuranceFillerOptions,
    ReassuranceVocabulary,
)
from .sentence_chunker import SentenceChunker
from .speculative_generator import (
    DefaultSpeculativeGenerator,
    ISpeculativeGenerator,
    ResponseGenerator,
    SpeculativeBranch,
)
from .speech_lifecycle_events import (
    AgentSpeakingFinishedEvent,
    AgentSpeakingStartedEvent,
    AgentThinkingEvent,
    CallerSpeechEndedEvent,
    CallerSpeechStartedEvent,
    ISpeechLifecycleBus,
    ISpeechSubscription,
    InMemorySpeechLifecycleBus,
    SpeechErrorEvent,
    SpeechLifecycleEvent,
    TranscriptFinalEventV2,
    TranscriptInterimEvent,
)
from .stereo_call_recorder import StereoCallRecorder
from .streaming_tool_progress import (
    IToolProgressSink,
    RecordingToolProgressSink,
    SpokenToolProgressSink,
    StreamingToolHandler,
    ToolProgressUpdate,
    run_streaming_tool_async,
)
from .telemetry import (
    Activity,
    ActivityKind,
    ActivityStatusCode,
    VoiceLoopTelemetry,
)
from .test_call_session import TestCallSession
from .tool_calling import (
    DefaultToolCallRegistry,
    IToolCallRegistry,
    LocalToolHandler,
    ToolDefinition,
    ToolInvocation,
    ToolResult,
)
from .tool_circuit_breaker import (
    CircuitBreakerToolRegistry,
    ToolBreakerState,
    ToolCallPolicy,
)
from .voice_loop_as_tool import (
    IVoiceLoopTool,
    VoiceLoopAsTool,
    VoiceLoopRunner,
    VoiceLoopToolRequest,
    VoiceLoopToolResult,
)
from .warm_transfer_orchestrator import (
    BriefingSynthesiser,
    DefaultWarmTransferOrchestrator,
    IWarmTransferOrchestrator,
    WarmTransferRequest,
    WarmTransferResult,
)

__all__ = [
    # primitives
    "CallDirection",
    "CallStatus",
    "CallMediaFormat",
    "TransferMode",
    "CallInfo",
    "CallSnapshot",
    "AudioFrame",
    "DtmfEvent",
    "ProvisionedNumber",
    # contracts
    "ITelephonyCarrier",
    "ICallSession",
    "IInboundCallDispatcher",
    "OutboundDialOptions",
    "StatusChangedHandler",
    "IMediaStream",
    "IDtmfSendable",
    "IDisposable",
    # null impls + failover
    "NullTelephonyCarrier",
    "NullInboundCallDispatcher",
    "CarrierFallback",
    # provisioning
    "PhoneNumberProvisioner",
    "IProvisionedNumberStore",
    "InMemoryProvisionedNumberStore",
    # cost + latency + telemetry
    "CallPricing",
    "CallCostBreakdown",
    "CallCostCalculator",
    "LatencyStage",
    "LatencySnapshot",
    "LatencyTracker",
    "VoiceLoopTelemetry",
    "Activity",
    "ActivityKind",
    "ActivityStatusCode",
    # audio / dtmf
    "DtmfToneGenerator",
    "HoldMusicMixer",
    "StereoCallRecorder",
    "SentenceChunker",
    # voice-loop control
    "BargeInController",
    "BargeInOptions",
    "BargeInState",
    "BargeInTransition",
    "IFalseInterruptionTracker",
    "InMemoryFalseInterruptionTracker",
    "InterruptionStats",
    "AnsweringMachineDetector",
    "AmdOptions",
    "AmdVerdict",
    "IvrLoopDetector",
    "IvrLoopVerdict",
    "IvrRound",
    "DefaultSpeculativeGenerator",
    "ISpeculativeGenerator",
    "SpeculativeBranch",
    "ResponseGenerator",
    # greeting / fillers / guardrails
    "IFirstMessagePreamble",
    "DefaultFirstMessagePreamble",
    "FirstMessagePreambleOptions",
    "PromptVariableResolver",
    "PromptVariableProvider",
    "IReassuranceFiller",
    "DefaultReassuranceFiller",
    "ReassuranceFillerOptions",
    "ReassuranceVocabulary",
    "Guardrails",
    "GuardrailRule",
    "GuardrailResult",
    "GuardrailAction",
    "CommonGuardrails",
    # speech lifecycle
    "SpeechLifecycleEvent",
    "CallerSpeechStartedEvent",
    "CallerSpeechEndedEvent",
    "TranscriptInterimEvent",
    "TranscriptFinalEventV2",
    "AgentThinkingEvent",
    "AgentSpeakingStartedEvent",
    "AgentSpeakingFinishedEvent",
    "SpeechErrorEvent",
    "ISpeechSubscription",
    "ISpeechLifecycleBus",
    "InMemorySpeechLifecycleBus",
    # tool-calling
    "ToolDefinition",
    "ToolInvocation",
    "ToolResult",
    "LocalToolHandler",
    "IToolCallRegistry",
    "DefaultToolCallRegistry",
    "CircuitBreakerToolRegistry",
    "ToolCallPolicy",
    "ToolBreakerState",
    "IToolProgressSink",
    "SpokenToolProgressSink",
    "RecordingToolProgressSink",
    "ToolProgressUpdate",
    "StreamingToolHandler",
    "run_streaming_tool_async",
    "IMcpToolImporter",
    "HttpMcpToolImporter",
    "McpServerConfig",
    "McpToolDescriptor",
    # transfer / handoff / consult
    "IWarmTransferOrchestrator",
    "DefaultWarmTransferOrchestrator",
    "WarmTransferRequest",
    "WarmTransferResult",
    "BriefingSynthesiser",
    "IAgentHandoffOrchestrator",
    "DefaultAgentHandoffOrchestrator",
    "CallAgent",
    "HandoffResult",
    "IConsultChannel",
    "ConsultEscalator",
    "ConsultRequest",
    "ConsultAnswer",
    "HttpWebhookConsultChannel",
    # eval / judge
    "EvalSession",
    "EvalTurn",
    "EvalTurnResult",
    "EvalRunResult",
    "EvalTurnHandler",
    "LlmJudge",
    "JudgeDimension",
    "JudgeVerdict",
    "JudgeCompletion",
    # dashboard
    "IDashboardDataSource",
    "DefaultDashboardDataSource",
    "DashboardSnapshot",
    "DashboardSummary",
    "LiveCallRow",
    "RecentCallRow",
    "AgentHealthRow",
    # dev tunnel
    "ILocalDevTunnel",
    "NullLocalDevTunnel",
    "StaticLocalDevTunnel",
    "CloudflareTunnel",
    "NgrokTunnel",
    "TunnelResolver",
    # voice-loop-as-tool
    "IVoiceLoopTool",
    "VoiceLoopAsTool",
    "VoiceLoopToolRequest",
    "VoiceLoopToolResult",
    "VoiceLoopRunner",
    # test harness
    "TestCallSession",
]
