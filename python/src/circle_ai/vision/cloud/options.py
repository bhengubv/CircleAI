# vision/cloud/options.py
#
# Port of CircleAI.Vision.Cloud/Options.cs (C# — the EXACT spec).
#
# (3.2.0) Provider-specific options. Concierge's defaults preserved verbatim —
# dall-e-3 / sd3.5-large / response_format=url.

from __future__ import annotations

from dataclasses import dataclass
from typing import Optional


@dataclass(frozen=True, slots=True)
class OpenAiImageOptions:
    """(3.2.0) OpenAI image-generation options. Mirrors ``OpenAiImageOptions``.

    The C# ``Uri BaseAddress`` maps to the base-URL string ``base_address``.
    """

    base_address: str = "https://api.openai.com"
    api_key: Optional[str] = None
    #: Model id. Default ``dall-e-3``.
    model: str = "dall-e-3"


@dataclass(frozen=True, slots=True)
class StabilityImageOptions:
    """(3.2.0) Stability AI image-generation options. Mirrors ``StabilityImageOptions``."""

    base_address: str = "https://api.stability.ai"
    api_key: Optional[str] = None
    #: Model id. Default ``sd3.5-large``.
    model: str = "sd3.5-large"
    #: Output format. Default ``png``.
    output_format: str = "png"


__all__ = [
    "OpenAiImageOptions",
    "StabilityImageOptions",
]
