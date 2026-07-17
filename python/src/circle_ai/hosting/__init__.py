"""circle_ai.hosting — port of the CircleAI.Hosting runtime + sub-hosts.

Re-exports every public host type: the AIService runtime + endpoints, the
observer + event records, the cron scheduler + triggers + proactive reasoning,
thermal throttling, predictive warmup, the tool catalog, generative UI, the
push/aether observer bridges, the inference bridge, and (from sub-packages) the
cloud-fallback chain, MCP dispatcher, and multiplayer hub.
"""
from __future__ import annotations

# ── Options + observer ─────────────────────────────────────────────────────
from .ai_observer import (
    AIChatEvent,
    AIStreamEvent,
    AIToolEvent,
    BrownoutReason,
    IAIObserver,
)
from .ai_options import AIOptions

# ── Core service + endpoints ───────────────────────────────────────────────
from .ai_service import AIService, IAIService, parse_tool_call

# ── Neuron — host-neutral seam + concierge + two-slot residency ─────────────
from .chat_runtime import (
    ChatTurn,
    IChatRuntime,
    IPersistableChatRuntime,
    NullChatRuntime,
)
from .neuron import (
    HeuristicNeuronRouter,
    INeuronRouter,
    NeuronGate,
    NeuronNode,
    Organ,
    ResidentSlotManager,
    RouteContext,
    RouteDecision,
    SlotAdmission,
    SlotOutcome,
)
from .endpoints import (
    AIHttpClient,
    HttpLoopbackEndpoint,
    IAIEndpoint,
    InProcessEndpoint,
    generate_random_token,
)
from .fallback_service import (
    AIApiClient,
    ButlerApiResponse,
    DefaultAvailableRamProbe,
    FallbackAIService,
    IAvailableRamProbe,
    IButlerApiTransport,
    InProcessButlerApiTransport,
)

# ── Memory pressure ────────────────────────────────────────────────────────
from .memory_pressure import (
    IMemoryPressureSource,
    ManualMemoryPressureSource,
    MemoryPressureLevel,
    NullMemoryPressureSource,
)

# ── Cron scheduler + triggers + proactive reasoning ────────────────────────
from .cron_job_models import CronJob, CronJobState, DeliveryTarget
from .cron_schedule_parser import CronScheduleParser
from .scheduled_task_store import InMemoryScheduledTaskStore, IScheduledTaskStore
from .scheduled_ai_service import JobCompletedEventArgs, ScheduledAIService
from .triggers import IdleTrigger, ITriggerCondition, ProactiveContext, ScheduleTrigger
from .proactive_reasoning_service import (
    IProactiveReasoningService,
    ProactiveMessageEventArgs,
    ProactiveReasoningService,
)

# ── Thermal + warmup ───────────────────────────────────────────────────────
from .thermal_throttle_service import (
    IThermalThrottleService,
    ThermalState,
    ThermalThrottleService,
)
from .background_inference_worker import BackgroundInferenceWorker
from .warmup import (
    ArrivalForecast,
    HistogramRequestPredictor,
    IRequestPredictor,
    PredictiveWarmupController,
    PredictiveWarmupOptions,
)

# ── Tools + generative UI ──────────────────────────────────────────────────
from .tool_catalog import (
    InMemoryToolCatalog,
    IToolCatalog,
    IToolExecutor,
    IToolProvider,
    ToolDescriptor,
    ToolExecutionResult,
    import_from_async,
)
from .generative_ui import (
    IGenerativeUIRenderer,
    JsonRenderParser,
    RecordingGenerativeUIRenderer,
    UiCatalogEntry,
    UiCatalogs,
    UiComponent,
)

# ── Observer bridges ───────────────────────────────────────────────────────
from .observers import (
    AetherAIObserver,
    ICircleAetherTransport,
    IPushNotificationSender,
    PushAIObserver,
)

# ── Inference bridge ───────────────────────────────────────────────────────
from .inference_bridge import (
    DeviceCapabilities,
    IInferenceBridge,
    InferenceFragment,
    InferenceFragmentKind,
    InferenceRequest,
    InferenceResponse,
    InferenceStatus,
    LocalProcessInferenceBridge,
    MockInferenceBridge,
    ModelDescriptor,
    ModelFormat,
)

# ── Sub-packages ───────────────────────────────────────────────────────────
from .cloud_fallback import (
    BackupBrainOrchestrator,
    BackupBrainPolicy,
    BrainHealth,
    BrainStatus,
    CloudFallbackChain,
    FakeConfigurableChatGenerator,
    IConfigurableChatGenerator,
)
from .mcp import (
    IMcpResourceProvider,
    IMcpTool,
    McpDispatcher,
    McpResource,
    McpResourceContent,
    McpServerInfo,
    McpToolException,
)
from .multiplayer import (
    GuestPeerIdentity,
    IMultiplayerPeerIdentity,
    MultiplayerHub,
    PeerState,
    colour_for,
)

__all__ = [
    # options + observer
    "AIOptions",
    "IAIObserver",
    "AIChatEvent",
    "AIStreamEvent",
    "AIToolEvent",
    "BrownoutReason",
    # core service + endpoints
    "IAIService",
    "AIService",
    "parse_tool_call",
    # neuron — host-neutral seam + concierge + two-slot residency
    "ChatTurn",
    "IChatRuntime",
    "IPersistableChatRuntime",
    "NullChatRuntime",
    "Organ",
    "RouteContext",
    "RouteDecision",
    "INeuronRouter",
    "NeuronGate",
    "HeuristicNeuronRouter",
    "SlotOutcome",
    "SlotAdmission",
    "ResidentSlotManager",
    "NeuronNode",
    "IAIEndpoint",
    "InProcessEndpoint",
    "HttpLoopbackEndpoint",
    "AIHttpClient",
    "generate_random_token",
    "AIApiClient",
    "FallbackAIService",
    "IButlerApiTransport",
    "InProcessButlerApiTransport",
    "ButlerApiResponse",
    "IAvailableRamProbe",
    "DefaultAvailableRamProbe",
    # memory pressure
    "MemoryPressureLevel",
    "IMemoryPressureSource",
    "NullMemoryPressureSource",
    "ManualMemoryPressureSource",
    # cron + triggers + proactive
    "DeliveryTarget",
    "CronJobState",
    "CronJob",
    "CronScheduleParser",
    "IScheduledTaskStore",
    "InMemoryScheduledTaskStore",
    "ScheduledAIService",
    "JobCompletedEventArgs",
    "ITriggerCondition",
    "ProactiveContext",
    "ScheduleTrigger",
    "IdleTrigger",
    "IProactiveReasoningService",
    "ProactiveReasoningService",
    "ProactiveMessageEventArgs",
    # thermal + warmup
    "ThermalState",
    "IThermalThrottleService",
    "ThermalThrottleService",
    "BackgroundInferenceWorker",
    "ArrivalForecast",
    "IRequestPredictor",
    "HistogramRequestPredictor",
    "PredictiveWarmupOptions",
    "PredictiveWarmupController",
    # tools + generative UI
    "ToolDescriptor",
    "ToolExecutionResult",
    "IToolCatalog",
    "IToolProvider",
    "IToolExecutor",
    "InMemoryToolCatalog",
    "import_from_async",
    "UiComponent",
    "UiCatalogEntry",
    "UiCatalogs",
    "IGenerativeUIRenderer",
    "RecordingGenerativeUIRenderer",
    "JsonRenderParser",
    # observer bridges
    "IPushNotificationSender",
    "PushAIObserver",
    "ICircleAetherTransport",
    "AetherAIObserver",
    # inference bridge
    "DeviceCapabilities",
    "IInferenceBridge",
    "InferenceFragment",
    "InferenceFragmentKind",
    "InferenceRequest",
    "InferenceResponse",
    "InferenceStatus",
    "LocalProcessInferenceBridge",
    "MockInferenceBridge",
    "ModelDescriptor",
    "ModelFormat",
    # cloud fallback
    "IConfigurableChatGenerator",
    "CloudFallbackChain",
    "BrainHealth",
    "BrainStatus",
    "BackupBrainPolicy",
    "BackupBrainOrchestrator",
    "FakeConfigurableChatGenerator",
    # mcp
    "IMcpTool",
    "IMcpResourceProvider",
    "McpResource",
    "McpResourceContent",
    "McpToolException",
    "McpDispatcher",
    "McpServerInfo",
    # multiplayer
    "IMultiplayerPeerIdentity",
    "GuestPeerIdentity",
    "PeerState",
    "MultiplayerHub",
    "colour_for",
]
