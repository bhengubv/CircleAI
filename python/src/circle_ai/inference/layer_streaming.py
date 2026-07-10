"""Layer-by-layer streaming inference (3.3.0).

Port of ``CircleAI.Inference.LayerStreamingInference`` — the AirLLM pattern:
load one transformer layer's weights at a time, run forward, save activations,
evict the layer, load the next. Lets a very large model fit on a small device
at the cost of disk bandwidth per token.

The native per-layer glue is host-supplied via :class:`ILayerStreamingRunner`.
This module defines the records, the runner contract, a null default that
raises on use, an orchestrator that drives a full forward pass, and a shard
discovery helper.
"""
from __future__ import annotations

import os
import re
from abc import ABC, abstractmethod
from dataclasses import dataclass
from typing import Callable, List, Optional, Sequence

__all__ = [
    "LayerWeightShard",
    "LayerStreamingPlan",
    "LayerActivations",
    "ILayerStreamingRunner",
    "NullLayerStreamingRunner",
    "LayerStreamingOrchestrator",
    "LayerShardDiscovery",
]


@dataclass(frozen=True, slots=True)
class LayerWeightShard:
    """One layer's weights packed for streaming. Mirrors ``LayerWeightShard``.

    * ``layer_index`` — 0-based transformer layer index.
    * ``weight_shard_path`` — path on disk to this layer's tensor shard.
    * ``approx_bytes`` — size of the shard, for memory accounting.
    """

    layer_index: int
    weight_shard_path: str
    approx_bytes: int


@dataclass(frozen=True, slots=True)
class LayerStreamingPlan:
    """Layer-streaming model plan. Mirrors ``LayerStreamingPlan``."""

    model_id: str
    total_layers: int
    shards: Sequence[LayerWeightShard]
    approx_parameter_bytes: int


@dataclass(frozen=True, slots=True)
class LayerActivations:
    """One layer's hidden-state output after forward. Mirrors ``LayerActivations``.

    ``hidden`` is a sequence of floats (the C# ``ReadOnlyMemory<float>``).
    """

    layer_index: int
    hidden: Sequence[float]


class ILayerStreamingRunner(ABC):
    """Host-supplied per-layer runner (load + forward + evict). Mirrors
    ``ILayerStreamingRunner``.
    """

    @property
    @abstractmethod
    def backend_id(self) -> str: ...

    @property
    @abstractmethod
    def is_available(self) -> bool: ...

    @abstractmethod
    async def run_layer_async(
        self,
        shard: LayerWeightShard,
        input_hidden: Sequence[float],
        ct: object = None,
    ) -> LayerActivations:
        """Forward one layer; return hidden states."""
        ...

    @abstractmethod
    async def evict_async(self, layer_index: int, ct: object = None) -> None:
        """Drop the layer from RAM after forward."""
        ...


class NullLayerStreamingRunner(ILayerStreamingRunner):
    """Null runner that raises on use — drop-in default. Mirrors
    ``NullLayerStreamingRunner``.
    """

    _instance: "NullLayerStreamingRunner | None" = None

    @classmethod
    def instance(cls) -> "NullLayerStreamingRunner":
        if cls._instance is None:
            cls._instance = cls()
        return cls._instance

    @property
    def backend_id(self) -> str:
        return "null"

    @property
    def is_available(self) -> bool:
        return False

    async def run_layer_async(
        self,
        shard: LayerWeightShard,
        input_hidden: Sequence[float],
        ct: object = None,
    ) -> LayerActivations:
        raise RuntimeError(
            "No ILayerStreamingRunner is wired. Register one "
            "(CircleAI.Inference.Native.AirLlm) to enable layer-streaming."
        )

    async def evict_async(self, layer_index: int, ct: object = None) -> None:
        return None


class LayerStreamingOrchestrator:
    """Drives a full forward pass layer by layer. Mirrors
    ``LayerStreamingOrchestrator``.
    """

    __slots__ = ("_runner",)

    def __init__(self, runner: ILayerStreamingRunner) -> None:
        if runner is None:
            raise ValueError("runner is required")
        self._runner = runner

    async def forward_async(
        self,
        plan: LayerStreamingPlan,
        initial_hidden: Sequence[float],
        on_layer_complete: Optional[Callable[[LayerActivations], None]] = None,
        ct: object = None,
    ) -> LayerActivations:
        """Stream every layer in ``plan``, evicting after each. Returns the
        final hidden state. ``on_layer_complete`` fires after each layer.
        Raises ``ValueError`` when the plan has no shards.
        """
        if plan is None:
            raise ValueError("plan is required")
        if len(plan.shards) == 0:
            raise ValueError("Plan has no layer shards.")

        hidden: Sequence[float] = initial_hidden
        last: Optional[LayerActivations] = None
        for shard in plan.shards:
            last = await self._runner.run_layer_async(shard, hidden, ct)
            hidden = last.hidden
            if on_layer_complete is not None:
                on_layer_complete(last)
            await self._runner.evict_async(shard.layer_index, ct)
        assert last is not None
        return last


class LayerShardDiscovery:
    """Discover layer shards on disk from a manifest directory. Mirrors
    ``LayerShardDiscovery``.
    """

    _LAYER_RE = re.compile(r"^layer_.*$")

    @staticmethod
    def discover(model_id: str, model_directory: str) -> LayerStreamingPlan:
        """Scan ``model_directory`` for files named ``layer_NNN.*`` and build a
        :class:`LayerStreamingPlan`. Shards are sorted by layer index.
        """
        if not model_id or not model_id.strip():
            raise ValueError("model_id required")
        if not os.path.isdir(model_directory):
            raise NotADirectoryError(f"Model directory not found: {model_directory}")

        shards: List[LayerWeightShard] = []
        total = 0
        for entry in os.listdir(model_directory):
            path = os.path.join(model_directory, entry)
            if not os.path.isfile(path):
                continue
            name_no_ext = os.path.splitext(entry)[0]
            if not name_no_ext.startswith("layer_"):
                continue
            underscore = name_no_ext.find("_")
            if underscore < 0:
                continue
            idx_str = name_no_ext[underscore + 1:]
            try:
                index = int(idx_str)
            except ValueError:
                continue
            size = os.path.getsize(path)
            shards.append(LayerWeightShard(index, path, size))
            total += size

        shards.sort(key=lambda s: s.layer_index)
        return LayerStreamingPlan(model_id, len(shards), shards, total)
