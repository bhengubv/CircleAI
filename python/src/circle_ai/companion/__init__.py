from .companion_types import (
    CompanionContext,
    CompanionProactiveEvent,
    CompanionTurn,
    InterfaceKind,
)
from .face_affect_mapper import apply as apply_face_affect
from .face_companion_bridge import CONFUSION_THRESHOLD, observe
from .belief import (
    Attribution,
    HeuristicBeliefExtractor,
    IBeliefExtractor,
    PersonalBelief,
    SelfBeliefStore,
)
from .memory_encoder import CompanionMemoryEncoder
from .session import (
    CompanionSession,
    CompanionSessionOptions,
    ProactiveMessageHandler,
)

__all__ = [
    "CompanionContext",
    "CompanionProactiveEvent",
    "CompanionTurn",
    "InterfaceKind",
    "CONFUSION_THRESHOLD",
    "apply_face_affect",
    "observe",
    # memory-brain companion layer
    "Attribution",
    "HeuristicBeliefExtractor",
    "IBeliefExtractor",
    "PersonalBelief",
    "SelfBeliefStore",
    "CompanionMemoryEncoder",
    "CompanionSession",
    "CompanionSessionOptions",
    "ProactiveMessageHandler",
]
