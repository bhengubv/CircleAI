"""Inference-server configuration tree.

Port of ``CircleAI.Inference.Server.Options.InferenceServerOptions`` and its
subtree (``AuthOptions``, ``ApiKeyOptions``, ``JwtOptions``). In C# these bind
appsettings.json; here they are plain dataclasses the host constructs directly.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from typing import List

__all__ = [
    "InferenceServerOptions",
    "AuthOptions",
    "ApiKeyOptions",
    "JwtOptions",
    "SECTION_NAME",
]

SECTION_NAME = "CircleAIServer"


@dataclass(slots=True)
class ApiKeyOptions:
    """API-key auth configuration. Mirrors ``ApiKeyOptions``."""

    enabled: bool = True
    header_name: str = "X-CircleAI-Api-Key"
    keys: List[str] = field(default_factory=list)


@dataclass(slots=True)
class JwtOptions:
    """JWT-bearer auth configuration. Mirrors ``JwtOptions``."""

    enabled: bool = False
    issuer: str = ""
    audience: str = ""
    signing_key: str = ""


@dataclass(slots=True)
class AuthOptions:
    """Auth subtree. Mirrors ``AuthOptions``."""

    api_key: ApiKeyOptions = field(default_factory=ApiKeyOptions)
    jwt: JwtOptions = field(default_factory=JwtOptions)


@dataclass(slots=True)
class InferenceServerOptions:
    """Root configuration for the inference server. Mirrors
    ``InferenceServerOptions``.
    """

    runtime_cache_root: str = "%LOCALAPPDATA%/CircleAI/runtime"
    model_storage_root: str = "%LOCALAPPDATA%/CircleAI/models"
    max_concurrent_requests: int = 16
    request_timeout_seconds: int = 120
    auth: AuthOptions = field(default_factory=AuthOptions)
