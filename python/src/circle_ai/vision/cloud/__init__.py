"""circle_ai.vision.cloud — port of the CircleAI.Vision.Cloud assembly.

The image-generation counterpart to circle_ai.vision (which is detection-only).
C# is the exact spec. The ``NullImageGenerator`` and the ``ImageGeneratorFallbackChain``
composite ship ready to use; the OpenAI + Stability generators speak HTTP and take
an injected :class:`IImageHttpTransport` seam (the same way every other .Cloud pack
in this tree treats real providers as injected dependencies) — the request
construction and response parsing are ported faithfully and are fully exercisable
in-memory via :class:`InMemoryImageHttpTransport`.

The C# ``VisionCloudServiceCollectionExtensions`` (Microsoft.Extensions.DependencyInjection
plumbing) has no Python analogue and is intentionally omitted, matching the
CircleAI.Speech.Cloud port.

Public surface:

  * Contracts + records:
      ImageGenerationRequest, ImageArtifact, IImageGenerator, NullImageGenerator.
  * Options:
      OpenAiImageOptions, StabilityImageOptions.
  * Generators:
      OpenAiImageGenerator, StabilityImageGenerator, ImageGeneratorFallbackChain.
  * Injected HTTP seam:
      HttpRequest, HttpResponse, IImageHttpTransport, InMemoryImageHttpTransport.
"""
from __future__ import annotations

from .contracts import (
    IImageGenerator,
    ImageArtifact,
    ImageGenerationRequest,
    NullImageGenerator,
)
from .fallback_chain import ImageGeneratorFallbackChain
from .http_transport import (
    HttpRequest,
    HttpResponse,
    IImageHttpTransport,
    InMemoryImageHttpTransport,
)
from .openai_image_generator import OpenAiImageGenerator
from .options import OpenAiImageOptions, StabilityImageOptions
from .stability_image_generator import StabilityImageGenerator

__all__ = [
    # contracts + records
    "ImageGenerationRequest",
    "ImageArtifact",
    "IImageGenerator",
    "NullImageGenerator",
    # options
    "OpenAiImageOptions",
    "StabilityImageOptions",
    # generators
    "OpenAiImageGenerator",
    "StabilityImageGenerator",
    "ImageGeneratorFallbackChain",
    # injected HTTP seam
    "HttpRequest",
    "HttpResponse",
    "IImageHttpTransport",
    "InMemoryImageHttpTransport",
]
