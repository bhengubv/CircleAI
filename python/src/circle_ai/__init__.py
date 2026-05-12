# __init__.py — Circle AI Python SDK
#
# Re-exports every public symbol so callers can do:
#   from circle_ai import AffectState, KnownLanguages, IChatGenerator, ...

# models
from .models import ChatMessage, DownloadProgress

# memory
from .memory import (
    AffectState,
    EpisodicMemoryEntry,
    FeedbackPolarity,
    FeedbackSignal,
    PersonaState,
    GoalStatus,
    GoalPriority,
    Goal,
    IAffectStore,
    IEpisodicMemoryStore,
    IPersonaStore,
    IFeedbackStore,
    IGoalStore,
)

# identity
from .identity import (
    IdentityTier,
    CircleIdentity,
    RegisteredDevice,
    IIdentityStore,
    IIdentityProvider,
)

# languages
from .languages import (
    WritingSystem,
    LanguageTag,
    DetectionResult,
    KnownLanguages,
    DefaultLanguageRegistry,
    ILanguageDetector,
    ILanguageRegistry,
)

# companion
from .companion import (
    InterfaceKind,
    CompanionContext,
    CompanionTurn,
    CompanionProactiveEvent,
    ICompanionSession,
)

# inference
from .inference import (
    GenerationOptions,
    IChatGenerator,
)

# tools
from .tools import (
    ToolParameter,
    ToolDefinition,
    ToolInvocation,
    ToolResult,
    IToolBridge,
)

# sync
from .sync import (
    SyncDeliveryMode,
    SyncDomainKeys,
    SyncDelta,
    ISyncChannel,
)

__all__ = [
    # models
    "ChatMessage",
    "DownloadProgress",
    # memory
    "AffectState",
    "EpisodicMemoryEntry",
    "FeedbackPolarity",
    "FeedbackSignal",
    "PersonaState",
    "GoalStatus",
    "GoalPriority",
    "Goal",
    "IAffectStore",
    "IEpisodicMemoryStore",
    "IPersonaStore",
    "IFeedbackStore",
    "IGoalStore",
    # identity
    "IdentityTier",
    "CircleIdentity",
    "RegisteredDevice",
    "IIdentityStore",
    "IIdentityProvider",
    # languages
    "WritingSystem",
    "LanguageTag",
    "DetectionResult",
    "KnownLanguages",
    "DefaultLanguageRegistry",
    "ILanguageDetector",
    "ILanguageRegistry",
    # companion
    "InterfaceKind",
    "CompanionContext",
    "CompanionTurn",
    "CompanionProactiveEvent",
    "ICompanionSession",
    # inference
    "GenerationOptions",
    "IChatGenerator",
    # tools
    "ToolParameter",
    "ToolDefinition",
    "ToolInvocation",
    "ToolResult",
    "IToolBridge",
    # sync
    "SyncDeliveryMode",
    "SyncDomainKeys",
    "SyncDelta",
    "ISyncChannel",
]
