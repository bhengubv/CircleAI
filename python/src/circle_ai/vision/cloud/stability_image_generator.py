# vision/cloud/stability_image_generator.py
#
# Port of CircleAI.Vision.Cloud/StabilityImageGenerator.cs (C# — the EXACT spec).
#
# (3.2.0) IImageGenerator backed by Stability AI's
# /v2beta/stable-image/generate/sd3 endpoint. Direct lift of Concierge's
# StabilityImageRuntime — Stability returns one image per call, so we loop on the
# caller's behalf to honour Count. Returns images inline as bytes (no remote URL).
#
# The C# drives HttpClient directly; here the HTTP leg is the injected
# :class:`IImageHttpTransport` seam. The per-image loop, multipart-form field set
# (incl. the conditional negative_prompt), and byte-result construction are ported
# faithfully.

from __future__ import annotations

from datetime import datetime, timezone
from typing import List, Tuple

from .contracts import IImageGenerator, ImageArtifact, ImageGenerationRequest
from .http_transport import HttpRequest, IImageHttpTransport
from .options import StabilityImageOptions


def _clamp(value: int, low: int, high: int) -> int:
    if value < low:
        return low
    if value > high:
        return high
    return value


def _is_null_or_whitespace(s: "str | None") -> bool:
    return s is None or s.strip() == ""


class StabilityImageGenerator(IImageGenerator):
    """(3.2.0) :class:`IImageGenerator` backed by Stability AI.

    Mirrors ``CircleAI.Vision.Cloud.StabilityImageGenerator``.
    """

    def __init__(self, http: IImageHttpTransport, options: StabilityImageOptions) -> None:
        if http is None:
            raise ValueError("http")
        if options is None:
            raise ValueError("options")
        self._http = http
        self._options = options

    @property
    def generator_id(self) -> str:
        return "stability"

    @property
    def display_label(self) -> str:
        return f"Stability AI · {self._options.model}"

    @property
    def is_configured(self) -> bool:
        return not _is_null_or_whitespace(self._options.api_key)

    @property
    def status_message(self) -> str:
        if self.is_configured:
            return f"Ready · {self._options.model}"
        return "Stability AI API key not configured — set Stability:ApiKey to enable."

    async def generate_async(
        self, request: ImageGenerationRequest, ct: object = None
    ) -> Tuple[ImageArtifact, ...]:
        if not self.is_configured:
            return ()

        artifacts: List[ImageArtifact] = []
        count = _clamp(request.count, 1, 4)
        for _i in range(count):
            # C#: ct.ThrowIfCancellationRequested() — no-op here (ct is a passthrough).

            # Ordered multipart fields, mirroring the C# MultipartFormDataContent.
            fields: List[Tuple[str, str]] = [
                ("prompt", request.prompt),
                ("output_format", self._options.output_format),
                ("model", self._options.model),
            ]
            # C#: if (!string.IsNullOrEmpty(request.NegativePrompt)) form.Add(...)
            if request.negative_prompt:
                fields.append(("negative_prompt", request.negative_prompt))

            http_request = HttpRequest(
                method="POST",
                base_address=self._options.base_address,
                path="/v2beta/stable-image/generate/sd3",
                headers={
                    "Authorization": f"Bearer {self._options.api_key}",
                    "Accept": f"image/{self._options.output_format}",
                },
                form_fields=tuple(fields),
            )

            response = await self._http.send_async(http_request, ct)
            if not response.is_success_status_code:
                # C# logs a warning + continue; we skip this image and move on.
                continue

            artifacts.append(
                ImageArtifact(
                    generator_id=self.generator_id,
                    prompt=request.prompt,
                    mime_type=f"image/{self._options.output_format}",
                    url=None,
                    bytes_=response.body_bytes,
                    generated_at_utc=datetime.now(timezone.utc),
                )
            )

        return tuple(artifacts)


__all__ = ["StabilityImageGenerator"]
