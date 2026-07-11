"""circle_ai.collaboration — port of the CircleAI.Collaboration assembly.

(2.8.0 contracts / 3.3.0 in-memory impl) Collaboration domain: channels,
messages, and presence, with concurrent in-memory stores and fail-closed null
defaults. C# is the exact spec.

Public surface:

  * Channel / Message / PresenceState                     — domain records.
  * IChannelStore / IMessageStore / IPresence             — backend contracts.
  * InMemoryChannelStore / InMemoryMessageStore / InMemoryPresence.
  * NullChannelStore / NullMessageStore / NullPresence    — fail-closed defaults.
"""
from __future__ import annotations

from .contracts import (
    Channel,
    IChannelStore,
    IMessageStore,
    IPresence,
    Message,
    PresenceState,
)
from .in_memory_collaboration import (
    InMemoryChannelStore,
    InMemoryMessageStore,
    InMemoryPresence,
)
from .null_implementations import (
    NullChannelStore,
    NullMessageStore,
    NullPresence,
)

__all__ = [
    "Channel",
    "Message",
    "PresenceState",
    "IChannelStore",
    "IMessageStore",
    "IPresence",
    "InMemoryChannelStore",
    "InMemoryMessageStore",
    "InMemoryPresence",
    "NullChannelStore",
    "NullMessageStore",
    "NullPresence",
]
