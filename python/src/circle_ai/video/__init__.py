"""circle_ai.video — port of the CircleAI.Video assembly (C# is the exact spec).

The short-video generation contract surface behind txtMe Video Mail: one generator
(IVideoGenerator), one style-aware script rewriter (IStyleScript), and one style
catalogue (IStyleReference). Deterministic null defaults ship out of the box, and
``InMemoryStyleReference`` is a thread-safe catalogue suitable for production until
a persistent store lands. The real generators (CogVideoX-2B ONNX->MNN, LTX-Video
distilled-2B) are injected — no model is required to exercise the ported surface.

Public surface:

  * Primitives (records):
      StyleId, VideoResolution, StyleReferenceFrame, StyleAttribution,
      StyleReference, AudioTrack, VideoGenerationRequest, VideoGenerationResult,
      StyleScriptRequest, StyleScriptResult.
  * Contracts:
      IVideoGenerator, IStyleScript, IStyleReference.
  * Implementations:
      NullVideoGenerator, NullStyleScript, InMemoryStyleReference.
"""
from __future__ import annotations

from .contracts import IStyleReference, IStyleScript, IVideoGenerator
from .null_implementations import (
    InMemoryStyleReference,
    NullStyleScript,
    NullVideoGenerator,
)
from .primitives import (
    AudioTrack,
    StyleAttribution,
    StyleId,
    StyleReference,
    StyleReferenceFrame,
    StyleScriptRequest,
    StyleScriptResult,
    VideoGenerationRequest,
    VideoGenerationResult,
    VideoResolution,
)

__all__ = [
    # primitives
    "StyleId",
    "VideoResolution",
    "StyleReferenceFrame",
    "StyleAttribution",
    "StyleReference",
    "AudioTrack",
    "VideoGenerationRequest",
    "VideoGenerationResult",
    "StyleScriptRequest",
    "StyleScriptResult",
    # contracts
    "IVideoGenerator",
    "IStyleScript",
    "IStyleReference",
    # implementations
    "NullVideoGenerator",
    "NullStyleScript",
    "InMemoryStyleReference",
]
