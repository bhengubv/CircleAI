"""The Neuron — concierge router + two-slot residency + host-neutral facade.

Port of CircleAI.Hosting.Neuron.
"""
from __future__ import annotations

from .neuron_node import NeuronNode
from .resident_slot_manager import ResidentSlotManager, SlotAdmission, SlotOutcome
from .router import (
    HeuristicNeuronRouter,
    INeuronRouter,
    NeuronGate,
    Organ,
    RouteContext,
    RouteDecision,
)

__all__ = [
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
]
