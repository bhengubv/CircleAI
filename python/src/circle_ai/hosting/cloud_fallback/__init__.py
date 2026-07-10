"""circle_ai.hosting.cloud_fallback — port of CircleAI.Hosting.CloudFallback.

Composite fallback chain + between-turn backup-brain orchestrator over
injected chat generators, with a deterministic local fake for tests.
"""
from __future__ import annotations

from .chains import (
    BackupBrainOrchestrator,
    BackupBrainPolicy,
    BrainHealth,
    BrainStatus,
    CloudFallbackChain,
    FakeConfigurableChatGenerator,
    IConfigurableChatGenerator,
)

__all__ = [
    "IConfigurableChatGenerator",
    "CloudFallbackChain",
    "BrainHealth",
    "BrainStatus",
    "BackupBrainPolicy",
    "BackupBrainOrchestrator",
    "FakeConfigurableChatGenerator",
]
