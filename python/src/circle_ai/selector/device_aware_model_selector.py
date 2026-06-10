"""DeviceAwareModelSelector — port of CircleAI.Inference.DeviceAwareModelSelector.

Walks ModelRegistryService.all_models, filters by capability + device fit,
ranks by quality_rank. No hardcoded model names — registry is the source
of truth, exactly like the C# port.
"""
from __future__ import annotations

from typing import Optional

from ..catalog.modelscope_catalog_client import ModelEntry
from ..device.device_probe import DeviceProbe
from ..inference.inference import ChatCapability, ModelSelection
from ..registry.model_registry_service import ModelRegistryService


class DeviceAwareModelSelector:
    """Default IModelSelector implementation."""

    def __init__(self, registry: ModelRegistryService) -> None:
        if registry is None:
            raise ValueError("registry is required")
        self._registry = registry

    def best_fit(
        self,
        probe: DeviceProbe,
        required: ChatCapability = ChatCapability.DEFAULT,
    ) -> ModelSelection:
        entries = list(self._registry.all_models)
        if not entries:
            raise RuntimeError("Model registry is empty. Cannot select a model.")

        ram_gb = probe.ram_available_bytes / (1024 ** 3)
        storage_gb = probe.storage_free_bytes / (1024 ** 3)

        # 1. Filter by capability — every required flag must be declared.
        capability_ok = [
            e for e in entries if _satisfies_capability(e, required)
        ]
        if not capability_ok:
            raise RuntimeError(
                f"No model in the registry satisfies required capabilities "
                f"'{required}'. Refresh the registry or relax the requirement."
            )

        # 2. Filter by device fit. Advisory — fall back to smallest when
        #    nothing fits rather than throwing.
        device_ok = [
            e
            for e in capability_ok
            if e.min_ram_gb <= ram_gb + 1e-4
            and (storage_gb <= 0 or e.min_storage_gb <= storage_gb + 1e-4)
        ]
        candidates = device_ok if device_ok else capability_ok

        # Higher QualityRank first, then lower MinRamGb (smallest model wins
        # the tiebreaker — closer to "fits everywhere").
        winner = max(candidates, key=lambda e: (e.quality_rank, -e.min_ram_gb))

        return ModelSelection(
            model_id=winner.name,
            requires_download=True,  # selector can't tell — caller checks cache
            estimated_bytes=winner.total_bytes,
            tier=probe.classify(),
        )

    def all_candidates(self, probe: DeviceProbe) -> list[ModelSelection]:
        tier = probe.classify()
        sorted_entries = sorted(
            self._registry.all_models,
            key=lambda e: e.quality_rank,
            reverse=True,
        )
        return [
            ModelSelection(
                model_id=e.name,
                requires_download=True,
                estimated_bytes=e.total_bytes,
                tier=tier,
            )
            for e in sorted_entries
        ]


# ── Helpers ────────────────────────────────────────────────────────────────


def _satisfies_capability(entry: ModelEntry, required: ChatCapability) -> bool:
    if required == ChatCapability.NONE:
        return True
    declared = parse_capabilities(entry.capabilities)
    return (declared & required) == required


def parse_capabilities(labels: Optional[list[str]]) -> ChatCapability:
    """Parse a registry capability list into a ChatCapability flag set.

    Empty list -> Default-only (matches the C# port).
    """
    if not labels:
        return ChatCapability.DEFAULT
    result = ChatCapability.NONE
    for label in labels:
        if not label or not label.strip():
            continue
        key = label.strip().upper().replace(" ", "_")
        # Map common names to enum members.
        match key:
            case "DEFAULT":
                result |= ChatCapability.DEFAULT
            case "TOOLS":
                result |= ChatCapability.TOOLS
            case "VISION":
                result |= ChatCapability.VISION
            case "LONGCONTEXT" | "LONG_CONTEXT":
                result |= ChatCapability.LONG_CONTEXT
            case "REASONING":
                result |= ChatCapability.REASONING
    return result if result != ChatCapability.NONE else ChatCapability.DEFAULT
