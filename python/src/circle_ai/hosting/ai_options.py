"""AIOptions — port of CircleAI.Hosting.AIOptions.

Configuration bag for the long-lived butler service. All fields have safe
defaults so callers can ``AIOptions()`` and get a working instance. Mirrors the
C# init-only-property record via a mutable dataclass with field defaults.

Fields cover: model resolution, inference knobs, tools, observer, sensorium,
memory/RAG, persona evolution, feedback, the agentic loop, loopback transport
config, thermal management, scheduled-task + goal stores, and the cloud-fallback
knobs. (Skill-context enrichment is out of scope for this port — the C#
``SkillStore``/``SkillTopK`` fields are omitted; enrichment simply skips them.)
"""
from __future__ import annotations

import base64
import secrets
from dataclasses import dataclass
from typing import TYPE_CHECKING, Callable, Optional

from ..catalog.modelscope_catalog_client import ModelScopeCatalogClient
from ..device.device_probe import IDeviceContext
from ..inference.inference import (
    ChatCapability,
    GenerationOptions,
    IChatGenerator,
    IModelSelector,
)
from ..memory.rag import RagContextBuilder
from ..memory.stores import (
    IAffectStore,
    IEpisodicMemoryStore,
    IFeedbackStore,
    IGoalStore,
    IPersonaStore,
)
from ..tools.tool_types import IToolBridge
from .ai_observer import IAIObserver
from .scheduled_task_store import IScheduledTaskStore
from .thermal_throttle_service import IThermalThrottleService

if TYPE_CHECKING:  # pragma: no cover - type-only, avoids an import cycle
    from .neuron.router import INeuronRouter

__all__ = ["AIOptions"]


@dataclass
class AIOptions:
    """Host configuration for AIService. Mirrors ``CircleAI.Hosting.AIOptions``."""

    # ── Model ──────────────────────────────────────────────────────────────
    model_id: Optional[str] = None
    """When None, the SDK auto-resolves via IModelSelector + DeviceProbe."""

    model_path: Optional[str] = None
    """Explicit model file path — bypasses the registry."""

    # ── Inference ──────────────────────────────────────────────────────────
    system_prompt: str = "You are B!, a helpful on-device assistant."
    default_generation_options: Optional[GenerationOptions] = None
    """Default sampling knobs applied when a caller passes none."""

    context_size: Optional[int] = None
    """When None, derived from DeviceTierDefaults.context_window(tier)."""

    thread_count: Optional[int] = None
    warm_on_start: bool = True

    # ── Tools ──────────────────────────────────────────────────────────────
    tool_bridge: Optional[IToolBridge] = None
    """When None, invoke_tool_async returns a failure result."""

    # ── Neuron — concierge routing + two-slot residency ────────────────────
    router: Optional["INeuronRouter"] = None
    """When set, AIService becomes a two-slot Neuron: the generalist stays warm
    and one capability-matched specialist may answer per turn. None (default) =
    single-slot, byte-identical behaviour."""

    model_selector: Optional[IModelSelector] = None
    """Selector used to best-fit a specialist for the router's capability."""

    specialist_factory: Optional[Callable[[str], IChatGenerator]] = None
    """Builds a specialist generator by model id — the Python analog of the C#
    IModelLoader path. Required alongside router + model_selector for two-slot."""

    # ── Observer ───────────────────────────────────────────────────────────
    observer: Optional[IAIObserver] = None

    # ── Sensorium ──────────────────────────────────────────────────────────
    device_context: Optional[IDeviceContext] = None

    # ── Catalog ────────────────────────────────────────────────────────────
    catalog_client: Optional[ModelScopeCatalogClient] = None

    required_capabilities: ChatCapability = ChatCapability.DEFAULT

    # ── Upgrade detection ──────────────────────────────────────────────────
    check_for_upgrades_on_start: bool = False
    model_storage_directory: Optional[str] = None

    # ── Memory / RAG ───────────────────────────────────────────────────────
    episodic_memory: Optional[IEpisodicMemoryStore] = None
    rag_builder: Optional[RagContextBuilder] = None
    rag_top_k: int = 5
    """Number of relevant past episodes to inject per call. 0 disables RAG."""

    # ── Persona evolution ──────────────────────────────────────────────────
    persona_store: Optional[IPersonaStore] = None
    persona_user_id: str = "default"

    # ── Feedback signals ───────────────────────────────────────────────────
    feedback_store: Optional[IFeedbackStore] = None

    # ── Agentic loop ───────────────────────────────────────────────────────
    agentic_max_iterations: Optional[int] = None
    """When None, derived from DeviceTierDefaults.agentic_max_iterations(tier)."""

    # ── Loopback endpoint config ───────────────────────────────────────────
    loopback_port: int = 0
    loopback_token: Optional[str] = None

    # ── Thermal management ─────────────────────────────────────────────────
    thermal_pause_enabled: bool = True
    thermal_service: Optional[IThermalThrottleService] = None

    # ── Scheduled tasks ────────────────────────────────────────────────────
    scheduled_task_store: Optional[IScheduledTaskStore] = None

    # ── Affect / goals ─────────────────────────────────────────────────────
    affect_store: Optional[IAffectStore] = None
    goal_store: Optional[IGoalStore] = None

    # ── Cloud fallback ─────────────────────────────────────────────────────
    cloud_fallback_enabled: bool = False
    cloud_fallback_endpoint: Optional[str] = None
    cloud_fallback_token: Optional[str] = None
    cloud_fallback_ram_threshold_bytes: int = 2 * 1024 * 1024 * 1024

    @staticmethod
    def generate_random_token() -> str:
        """Generate a cryptographically-random 32-byte token, base64-encoded.
        Mirrors ``AIOptions.GenerateRandomToken``.
        """
        return base64.b64encode(secrets.token_bytes(32)).decode("ascii")
