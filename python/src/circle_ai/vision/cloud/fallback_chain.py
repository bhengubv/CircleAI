# vision/cloud/fallback_chain.py
#
# Port of CircleAI.Vision.Cloud/ImageGeneratorFallbackChain.cs (C# — the EXACT spec).
#
# (3.2.0) Composite IImageGenerator that walks a configured chain in order,
# returning the first non-empty artifact set. Skips generators whose
# is_configured is False. Mirrors CloudFallbackChain semantics for chat.

from __future__ import annotations

from typing import Iterable, List, Tuple

from .contracts import IImageGenerator, ImageArtifact, ImageGenerationRequest


class ImageGeneratorFallbackChain(IImageGenerator):
    """(3.2.0) Composite :class:`IImageGenerator` — tries each child in order,
    skipping those that report ``is_configured`` = False. Returns the first
    non-empty artifact tuple, or empty if everyone failed.

    Mirrors ``CircleAI.Vision.Cloud.ImageGeneratorFallbackChain``.
    """

    def __init__(self, chain: "Iterable[IImageGenerator] | None") -> None:
        # C#: chain?.ToList() ?? new List<IImageGenerator>()
        self._chain: List[IImageGenerator] = list(chain) if chain is not None else []

    @property
    def generator_id(self) -> str:
        return "fallback-chain"

    @property
    def display_label(self) -> str:
        return f"Fallback ({len(self._chain)})"

    @property
    def is_configured(self) -> bool:
        return any(g.is_configured for g in self._chain)

    @property
    def status_message(self) -> str:
        if self.is_configured:
            ready = " → ".join(g.generator_id for g in self._chain if g.is_configured)
            return f"Ready · {ready}"
        return "No configured generator in chain."

    async def generate_async(
        self, request: ImageGenerationRequest, ct: object = None
    ) -> Tuple[ImageArtifact, ...]:
        for g in self._chain:
            if not g.is_configured:
                continue
            result = await g.generate_async(request, ct)
            if len(result) > 0:
                return tuple(result)
        return ()


__all__ = ["ImageGeneratorFallbackChain"]
