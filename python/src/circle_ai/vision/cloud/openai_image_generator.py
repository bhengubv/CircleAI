# vision/cloud/openai_image_generator.py
#
# Port of CircleAI.Vision.Cloud/OpenAiImageGenerator.cs (C# — the EXACT spec).
#
# (3.2.0) IImageGenerator backed by OpenAI's /v1/images/generations endpoint.
# Direct lift of Concierge.Media.Cloud.OpenAiImageRuntime — same response_format=url
# path, same Clamp(Count, 1, 4) safety. Fail-soft when the API key is missing:
# returns an empty artifact tuple so a fallback chain can move on.
#
# The C# drives HttpClient directly; here the HTTP leg is the injected
# :class:`IImageHttpTransport` seam. The request construction and the
# ``data[].url`` response parsing are ported faithfully.

from __future__ import annotations

import json
from datetime import datetime, timezone
from typing import List, Tuple

from .contracts import IImageGenerator, ImageArtifact, ImageGenerationRequest
from .http_transport import HttpRequest, IImageHttpTransport
from .options import OpenAiImageOptions


def _clamp(value: int, low: int, high: int) -> int:
    # Mirrors System.Math.Clamp.
    if value < low:
        return low
    if value > high:
        return high
    return value


def _is_null_or_whitespace(s: "str | None") -> bool:
    return s is None or s.strip() == ""


class OpenAiImageGenerator(IImageGenerator):
    """(3.2.0) :class:`IImageGenerator` backed by OpenAI DALL-E.

    Mirrors ``CircleAI.Vision.Cloud.OpenAiImageGenerator``.
    """

    def __init__(self, http: IImageHttpTransport, options: OpenAiImageOptions) -> None:
        if http is None:
            raise ValueError("http")
        if options is None:
            raise ValueError("options")
        self._http = http
        self._options = options

    @property
    def generator_id(self) -> str:
        return "openai-images"

    @property
    def display_label(self) -> str:
        return f"OpenAI · {self._options.model}"

    @property
    def is_configured(self) -> bool:
        return not _is_null_or_whitespace(self._options.api_key)

    @property
    def status_message(self) -> str:
        if self.is_configured:
            return f"Ready · {self._options.model}"
        return "OpenAI API key not configured — set OpenAI:ApiKey to enable."

    async def generate_async(
        self, request: ImageGenerationRequest, ct: object = None
    ) -> Tuple[ImageArtifact, ...]:
        if not self.is_configured:
            return ()

        http_request = HttpRequest(
            method="POST",
            base_address=self._options.base_address,
            path="/v1/images/generations",
            headers={"Authorization": f"Bearer {self._options.api_key}"},
            json_body={
                "model": self._options.model,
                "prompt": request.prompt,
                "n": _clamp(request.count, 1, 4),
                "size": f"{request.size}x{request.size}",
                "response_format": "url",
            },
        )

        response = await self._http.send_async(http_request, ct)
        if not response.is_success_status_code:
            # C# logs a warning here; we degrade to empty as the C# does.
            return ()

        try:
            doc = json.loads(response.body_text) if response.body_text else {}
        except (ValueError, TypeError):
            return ()

        artifacts: List[ImageArtifact] = []
        data = doc.get("data") if isinstance(doc, dict) else None
        if isinstance(data, list):
            for item in data:
                if not isinstance(item, dict):
                    continue
                url = item.get("url")
                if isinstance(url, str):
                    artifacts.append(
                        ImageArtifact(
                            generator_id=self.generator_id,
                            prompt=request.prompt,
                            mime_type="image/png",
                            url=url,
                            bytes_=None,
                            generated_at_utc=datetime.now(timezone.utc),
                        )
                    )
        return tuple(artifacts)


__all__ = ["OpenAiImageGenerator"]
