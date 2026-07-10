"""test_vision_cloud.py — CircleAI.Vision.Cloud image-generation surface.

Covers the null generator, the OpenAI + Stability generators over the injected
in-memory HTTP transport (exact request construction + response parsing), and the
fallback-chain composite (skip-unconfigured, first-non-empty). C#
(CircleAI.Vision.Cloud) is the reference.
"""
from __future__ import annotations

import json

import pytest

from circle_ai.vision.cloud import (
    HttpResponse,
    IImageGenerator,
    ImageGenerationRequest,
    ImageGeneratorFallbackChain,
    InMemoryImageHttpTransport,
    NullImageGenerator,
    OpenAiImageGenerator,
    OpenAiImageOptions,
    StabilityImageGenerator,
    StabilityImageOptions,
)


# ── null generator ───────────────────────────────────────────────────────────────


async def test_null_image_generator_returns_empty():
    g = NullImageGenerator.instance()
    assert g.generator_id == "null"
    assert g.display_label == "No image generator"
    assert g.is_configured is False
    assert "Configure OpenAI:ApiKey" in g.status_message
    assert await g.generate_async(ImageGenerationRequest(prompt="a cat")) == ()


# ── OpenAI generator ─────────────────────────────────────────────────────────────


def _openai_ok(urls):
    body = json.dumps({"data": [{"url": u} for u in urls]})
    return lambda req: HttpResponse(status_code=200, body_text=body)


async def test_openai_not_configured_short_circuits():
    transport = InMemoryImageHttpTransport(_openai_ok(["http://x/1.png"]))
    g = OpenAiImageGenerator(transport, OpenAiImageOptions(api_key=None))
    assert g.is_configured is False
    assert "OpenAI API key not configured" in g.status_message
    assert await g.generate_async(ImageGenerationRequest(prompt="cat")) == ()
    assert transport.requests == []  # never hits the wire when unconfigured


async def test_openai_builds_exact_request_and_parses_urls():
    transport = InMemoryImageHttpTransport(_openai_ok(["http://cdn/a.png", "http://cdn/b.png"]))
    g = OpenAiImageGenerator(transport, OpenAiImageOptions(api_key="sk-test", model="dall-e-3"))
    assert g.generator_id == "openai-images"
    assert g.display_label == "OpenAI · dall-e-3"
    assert g.is_configured is True

    arts = await g.generate_async(ImageGenerationRequest(prompt="a red bus", size=512, count=2))
    # request shape
    req = transport.requests[0]
    assert req.method == "POST"
    assert req.path == "/v1/images/generations"
    assert req.headers["Authorization"] == "Bearer sk-test"
    assert req.json_body == {
        "model": "dall-e-3",
        "prompt": "a red bus",
        "n": 2,
        "size": "512x512",
        "response_format": "url",
    }
    # parsed artifacts
    assert len(arts) == 2
    assert arts[0].url == "http://cdn/a.png"
    assert arts[0].bytes_ is None
    assert arts[0].mime_type == "image/png"
    assert arts[0].generator_id == "openai-images"
    assert arts[0].prompt == "a red bus"


async def test_openai_clamps_count_to_four():
    transport = InMemoryImageHttpTransport(_openai_ok([]))
    g = OpenAiImageGenerator(transport, OpenAiImageOptions(api_key="sk"))
    await g.generate_async(ImageGenerationRequest(prompt="p", count=99))
    assert transport.requests[0].json_body["n"] == 4
    await g.generate_async(ImageGenerationRequest(prompt="p", count=0))
    assert transport.requests[1].json_body["n"] == 1


async def test_openai_non_success_returns_empty():
    transport = InMemoryImageHttpTransport(lambda req: HttpResponse(status_code=429, body_text="rate limited"))
    g = OpenAiImageGenerator(transport, OpenAiImageOptions(api_key="sk"))
    assert await g.generate_async(ImageGenerationRequest(prompt="p")) == ()


async def test_openai_skips_items_without_url():
    body = json.dumps({"data": [{"revised_prompt": "no url here"}, {"url": "http://ok/1.png"}]})
    transport = InMemoryImageHttpTransport(lambda req: HttpResponse(200, body_text=body))
    g = OpenAiImageGenerator(transport, OpenAiImageOptions(api_key="sk"))
    arts = await g.generate_async(ImageGenerationRequest(prompt="p"))
    assert len(arts) == 1
    assert arts[0].url == "http://ok/1.png"


# ── Stability generator ──────────────────────────────────────────────────────────


async def test_stability_not_configured_short_circuits():
    transport = InMemoryImageHttpTransport(lambda req: HttpResponse(200, body_bytes=b"\x89PNG"))
    g = StabilityImageGenerator(transport, StabilityImageOptions(api_key=""))
    assert g.is_configured is False
    assert "Stability AI API key not configured" in g.status_message
    assert await g.generate_async(ImageGenerationRequest(prompt="p")) == ()
    assert transport.requests == []


async def test_stability_loops_per_image_and_returns_bytes():
    transport = InMemoryImageHttpTransport(lambda req: HttpResponse(200, body_bytes=b"IMGDATA"))
    g = StabilityImageGenerator(transport, StabilityImageOptions(api_key="sk", model="sd3.5-large"))
    assert g.generator_id == "stability"
    assert g.display_label == "Stability AI · sd3.5-large"

    arts = await g.generate_async(ImageGenerationRequest(prompt="a dog", count=3))
    assert len(transport.requests) == 3  # one call per requested image
    assert len(arts) == 3
    a = arts[0]
    assert a.url is None
    assert a.bytes_ == b"IMGDATA"
    assert a.mime_type == "image/png"
    # request shape
    req = transport.requests[0]
    assert req.method == "POST"
    assert req.path == "/v2beta/stable-image/generate/sd3"
    assert req.headers["Authorization"] == "Bearer sk"
    assert req.headers["Accept"] == "image/png"
    assert req.form_fields == (("prompt", "a dog"), ("output_format", "png"), ("model", "sd3.5-large"))


async def test_stability_adds_negative_prompt_when_present():
    transport = InMemoryImageHttpTransport(lambda req: HttpResponse(200, body_bytes=b"X"))
    g = StabilityImageGenerator(transport, StabilityImageOptions(api_key="sk"))
    await g.generate_async(ImageGenerationRequest(prompt="p", negative_prompt="blurry", count=1))
    fields = dict(transport.requests[0].form_fields)
    assert fields["negative_prompt"] == "blurry"


async def test_stability_skips_failed_images_but_keeps_successes():
    calls = {"n": 0}

    def handler(req):
        calls["n"] += 1
        # first image fails, rest succeed
        if calls["n"] == 1:
            return HttpResponse(500, body_text="server error")
        return HttpResponse(200, body_bytes=b"OK")

    transport = InMemoryImageHttpTransport(handler)
    g = StabilityImageGenerator(transport, StabilityImageOptions(api_key="sk"))
    arts = await g.generate_async(ImageGenerationRequest(prompt="p", count=3))
    assert len(transport.requests) == 3
    assert len(arts) == 2  # only the two successes


async def test_stability_honours_output_format():
    transport = InMemoryImageHttpTransport(lambda req: HttpResponse(200, body_bytes=b"J"))
    g = StabilityImageGenerator(transport, StabilityImageOptions(api_key="sk", output_format="jpeg"))
    arts = await g.generate_async(ImageGenerationRequest(prompt="p", count=1))
    assert arts[0].mime_type == "image/jpeg"
    assert transport.requests[0].headers["Accept"] == "image/jpeg"


# ── fallback chain ───────────────────────────────────────────────────────────────


class _StubGen(IImageGenerator):
    def __init__(self, gid, configured, result):
        self._gid = gid
        self._configured = configured
        self._result = result
        self.called = False

    @property
    def generator_id(self):
        return self._gid

    @property
    def display_label(self):
        return self._gid

    @property
    def is_configured(self):
        return self._configured

    @property
    def status_message(self):
        return "ok"

    async def generate_async(self, request, ct=None):
        self.called = True
        return self._result


def _artifact_tuple():
    from datetime import datetime, timezone

    from circle_ai.vision.cloud import ImageArtifact

    return (ImageArtifact("g", "p", "image/png", "http://x/1.png", None, datetime.now(timezone.utc)),)


async def test_fallback_chain_empty_is_unconfigured():
    chain = ImageGeneratorFallbackChain(None)
    assert chain.generator_id == "fallback-chain"
    assert chain.display_label == "Fallback (0)"
    assert chain.is_configured is False
    assert chain.status_message == "No configured generator in chain."
    assert await chain.generate_async(ImageGenerationRequest(prompt="p")) == ()


async def test_fallback_chain_skips_unconfigured_and_takes_first_nonempty():
    unconfigured = _StubGen("a", configured=False, result=_artifact_tuple())
    configured_empty = _StubGen("b", configured=True, result=())
    configured_hit = _StubGen("c", configured=True, result=_artifact_tuple())
    late = _StubGen("d", configured=True, result=_artifact_tuple())

    chain = ImageGeneratorFallbackChain([unconfigured, configured_empty, configured_hit, late])
    assert chain.is_configured is True
    assert chain.display_label == "Fallback (4)"
    assert chain.status_message == "Ready · b → c → d"

    res = await chain.generate_async(ImageGenerationRequest(prompt="p"))
    assert len(res) == 1
    assert unconfigured.called is False  # skipped (not configured)
    assert configured_empty.called is True  # tried, returned empty
    assert configured_hit.called is True  # tried, returned the hit
    assert late.called is False  # never reached — first non-empty wins


async def test_fallback_chain_all_empty_returns_empty():
    a = _StubGen("a", True, ())
    b = _StubGen("b", True, ())
    chain = ImageGeneratorFallbackChain([a, b])
    assert await chain.generate_async(ImageGenerationRequest(prompt="p")) == ()
    assert a.called and b.called
