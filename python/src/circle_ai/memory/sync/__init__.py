# circle_ai.memory.sync — companion-state sync layer.
#
# Ported from CircleAI.Memory.Sync (C#). HLC-versioned convergent sync of
# companion state across a user's devices over a pluggable channel.

from __future__ import annotations

from .hybrid_logical_clock import HybridLogicalClock
from .syncable_entry import SyncableEntry
from .sync_envelope import (
    RequestItem,
    StateVectorEntry,
    SyncEnvelope,
    SyncEnvelopeKind,
)
from .syncable_entry_store import (
    ISyncableEntryStore,
    InMemorySyncableEntryStore,
)
from .companion_state_channel import (
    EnvelopeHandler,
    ICompanionStateChannel,
    IDisposable,
    InProcessCompanionStateChannel,
    InProcessSyncHub,
)
from .companion_state_sync_engine import (
    CompanionStateSyncEngine,
    ICompanionStateSyncEngine,
)
from .persona_state_sync_bridge import PersonaStateSyncBridge
from .lora_adapter_sync_bridge import (
    LoraAdapterSnapshot,
    LoraAdapterSyncBridge,
)
from .companion_conversation_sync_bridge import (
    CompanionConversationSyncBridge,
    ConversationStateDelta,
)

__all__ = [
    "HybridLogicalClock",
    "SyncableEntry",
    "RequestItem",
    "StateVectorEntry",
    "SyncEnvelope",
    "SyncEnvelopeKind",
    "ISyncableEntryStore",
    "InMemorySyncableEntryStore",
    "EnvelopeHandler",
    "ICompanionStateChannel",
    "IDisposable",
    "InProcessCompanionStateChannel",
    "InProcessSyncHub",
    "CompanionStateSyncEngine",
    "ICompanionStateSyncEngine",
    "PersonaStateSyncBridge",
    "LoraAdapterSnapshot",
    "LoraAdapterSyncBridge",
    "CompanionConversationSyncBridge",
    "ConversationStateDelta",
]
