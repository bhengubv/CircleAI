"""AIOptions — port of CircleAI.Hosting.AIOptions.

Configuration bag for the host AIService. Mirrors the C# init-only-property
record by using a frozen dataclass with field defaults.
"""
from __future__ import annotations

from dataclasses import dataclass
from typing import Optional

from ..catalog.modelscope_catalog_client import ModelScopeCatalogClient
from ..device.device_probe import IDeviceContext
from ..inference.inference import ChatCapability
from .ai_observer import IAIObserver


@dataclass
class AIOptions:
    """Host configuration for AIService."""

    # Model selection
    model_id: Optional[str] = None
    """When None, the SDK auto-resolves via IModelSelector + DeviceProbe."""

    model_path: Optional[str] = None
    """Explicit model file path — bypasses the registry."""

    # Inference
    system_prompt: str = "You are B!, a helpful on-device assistant."
    context_size: Optional[int] = None
    """When None, derived from DeviceTierDefaults.context_window(tier)."""

    thread_count: Optional[int] = None
    warm_on_start: bool = True

    # Sensorium
    device_context: Optional[IDeviceContext] = None

    # Catalog
    catalog_client: Optional[ModelScopeCatalogClient] = None
    """When supplied, the registry primes from disk + refreshes per cadence."""

    required_capabilities: ChatCapability = ChatCapability.DEFAULT
    """Capabilities the model must declare. Selector filters by these."""

    # Agentic
    agentic_max_iterations: Optional[int] = None
    """When None, derived from DeviceTierDefaults.agentic_max_iterations(tier)."""

    # Observer
    observer: Optional[IAIObserver] = None

    # Upgrade detection
    check_for_upgrades_on_start: bool = False
    """When True, AIService.start_async triggers check_for_upgrades_async
    after model load and fires observer events per upgrade.
    """

    model_storage_directory: Optional[str] = None
    """Where downloaded bundles live. Required for upgrade detection."""
