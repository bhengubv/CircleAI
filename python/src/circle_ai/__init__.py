# circle_ai/__init__.py — Circle AI Python SDK
#
# Re-exports every public symbol so callers can do:
#   from circle_ai import AffectState, KnownLanguages, IChatGenerator, ...

# models
from .models.models import ChatMessage, DownloadProgress

# memory
from .memory.affect_state import AffectState, AffectVad
from .memory.episodic_memory import EpisodicMemoryEntry
from .memory.feedback_signal import FeedbackPolarity, FeedbackSignal
from .memory.goal import Goal, GoalPriority, GoalStatus
from .memory.persona_state import PersonaState
from .memory.stores import (
    IAffectStore,
    IEpisodicMemoryStore,
    IFeedbackStore,
    IGoalStore,
    IPersonaStore,
)

# security
from .security import AnomalySignal, ThreatVector

# identity
from .identity.identity_types import CircleIdentity, IdentityTier, RegisteredDevice
from .identity.biometric_profile import BiometricProfile
from .identity.biometric_store import IBiometricStore
from .identity.biometric_matcher import cosine_similarity, is_match

# languages
from .languages.language_types import DetectionResult, LanguageTag, WritingSystem
from .languages.known_languages import DefaultLanguageRegistry, KnownLanguages

# companion
from .companion.companion_types import (
    CompanionContext,
    CompanionProactiveEvent,
    CompanionTurn,
    InterfaceKind,
)
from .companion.face_affect_mapper import apply as apply_face_affect
from .companion.face_companion_bridge import CONFUSION_THRESHOLD, observe

# inference
from .inference.inference import GenerationOptions, IChatGenerator

# tools
from .tools.tool_types import ToolDefinition, ToolInvocation, ToolParameter, ToolResult
from .tools.facial_metric_matrix import (
    FaceBoundingBox,
    FaceExpressionClassification,
    FacialMetricMatrix,
)

# sync
from .sync.sync_types import SchedulingHint, SyncDeliveryMode, SyncDelta, SyncDomainKeys

__all__ = [
    # models
    "ChatMessage",
    "DownloadProgress",
    # memory
    "AffectState",
    "AffectVad",
    "EpisodicMemoryEntry",
    "FeedbackPolarity",
    "FeedbackSignal",
    "Goal",
    "GoalPriority",
    "GoalStatus",
    "PersonaState",
    "IAffectStore",
    "IEpisodicMemoryStore",
    "IFeedbackStore",
    "IGoalStore",
    "IPersonaStore",
    # identity
    "CircleIdentity",
    "IdentityTier",
    "RegisteredDevice",
    "BiometricProfile",
    "IBiometricStore",
    "cosine_similarity",
    "is_match",
    # languages
    "DetectionResult",
    "DefaultLanguageRegistry",
    "KnownLanguages",
    "LanguageTag",
    "WritingSystem",
    # companion
    "CompanionContext",
    "CompanionProactiveEvent",
    "CompanionTurn",
    "InterfaceKind",
    "CONFUSION_THRESHOLD",
    "apply_face_affect",
    "observe",
    # inference
    "GenerationOptions",
    "IChatGenerator",
    # tools
    "FaceBoundingBox",
    "FaceExpressionClassification",
    "FacialMetricMatrix",
    "ToolDefinition",
    "ToolInvocation",
    "ToolParameter",
    "ToolResult",
    # sync
    "SchedulingHint",
    "SyncDeliveryMode",
    "SyncDelta",
    "SyncDomainKeys",
    # security
    "AnomalySignal",
    "ThreatVector",
]
