# enums.py
#
# Port of CircleAI.Runtime.Backends BackendKind.cs + CapabilityTier.cs (C# —
# the EXACT spec).
#
# The MNN execution backend enum and the device capability-tier enum. C# enums
# map to IntEnum with the exact ordinals declared in the source.

from __future__ import annotations

from enum import IntEnum


class BackendKind(IntEnum):
    """Mirrors ``CircleAI.Runtime.Backends.BackendKind``. Picked by
    :class:`IBackendSelector` based on the host's HostProfile."""

    Cpu = 0
    Cuda = 1
    Vulkan = 2
    OpenCL = 3
    Metal = 4
    Ascend = 5
    Cambricon = 6
    CoreML = 7


class CapabilityTier(IntEnum):
    """Mirrors ``CircleAI.Runtime.Backends.CapabilityTier``. Maps to a Qwen /
    DeepSeek / GLM / Kimi model size band; higher tiers need more RAM/VRAM.
    Tier0 is the always-runnable floor."""

    Tier0_Tiny = 0
    Tier1_Small = 1
    Tier2_Medium = 2
    Tier3_Large = 3
    Tier4_Frontier = 4
