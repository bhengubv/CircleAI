"""circle_ai.agents.peer — port of the CircleAI.Agents.Peer assembly.

Agent-to-agent protocol over the Aether mesh: the signed AgentMessage envelope,
the PeerAgent / AgentCapability identity records, the IAgentPeerProtocol
contract, the AgentInvocationException, an in-process AgentBus that simulates a
mesh of peers, and the InMemoryAgentPeerProtocol reference implementation
(channel bus, discovery window, invoke-timeout, pending-reply correlation,
inbox pump). C# is the exact spec.
"""
from .agent_bus import AgentBus
from .agent_invocation_exception import AgentInvocationException
from .agent_message import AgentMessage, AgentMessageKind
from .agent_peer_protocol import IAgentPeerProtocol
from .in_memory_agent_peer_protocol import InMemoryAgentPeerProtocol
from .peer_agent import AgentCapability, PeerAgent

__all__ = [
    "AgentMessage",
    "AgentMessageKind",
    "AgentCapability",
    "PeerAgent",
    "IAgentPeerProtocol",
    "AgentInvocationException",
    "AgentBus",
    "InMemoryAgentPeerProtocol",
]
