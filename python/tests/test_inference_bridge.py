"""test_inference_bridge.py — LocalProcessInferenceBridge + records."""
from __future__ import annotations

import uuid

import pytest

from circle_ai.hosting import (
    DeviceCapabilities,
    InferenceFragmentKind,
    InferenceRequest,
    InferenceStatus,
    LocalProcessInferenceBridge,
    ModelDescriptor,
    ModelFormat,
)
from circle_ai.inference import DeterministicChatGenerator


def _descriptor(model_id="m1"):
    return ModelDescriptor(
        model_id=model_id,
        version="1.0",
        format=ModelFormat.GGUF,
        context_window_tokens=4096,
        vocab_size=151936,
        parameter_count=0,
        quantisation_label=None,
        approximate_memory_bytes=1024,
    )


def _bridge(model_id="m1", caps=None):
    return LocalProcessInferenceBridge(DeterministicChatGenerator(model_id), _descriptor(model_id), caps)


def _req(model_id="m1", prompt="hi", max_tokens=256):
    return InferenceRequest(
        id=uuid.uuid4(),
        model_id=model_id,
        prompt=prompt,
        max_output_tokens=max_tokens,
        temperature=0.7,
        top_p=0.9,
        stop_sequences=[],
        metadata={},
        requested_at=__import__("datetime").datetime.now(__import__("datetime").timezone.utc),
    )


async def test_list_and_is_loaded():
    b = _bridge("m1")
    models = await b.list_loaded_models_async()
    assert len(models) == 1 and models[0].model_id == "m1"
    assert await b.is_model_loaded_async("m1") is True
    assert await b.is_model_loaded_async("other") is False


async def test_complete_returns_response():
    b = _bridge("m1")
    resp = await b.complete_async(_req("m1"))
    assert resp.model_id == "m1"
    assert resp.status in (InferenceStatus.COMPLETED, InferenceStatus.STOPPED_BY_LENGTH)
    assert resp.output_text != ""
    assert resp.prompt_token_count >= 1


async def test_complete_wrong_model_fails():
    b = _bridge("m1")
    resp = await b.complete_async(_req("other"))
    assert resp.status == InferenceStatus.FAILED
    assert "not loaded" in resp.failure_message


async def test_complete_surfaces_reasoning():
    b = _bridge("m1")
    resp = await b.complete_async(_req("m1"))
    # The deterministic generator emits a <think> block by default.
    assert resp.reasoning_text is not None
    assert "<think>" not in resp.output_text


async def test_stream_completion_content_only():
    b = _bridge("m1")
    chunks = [c async for c in b.stream_completion_async(_req("m1"))]
    joined = "".join(chunks)
    assert joined != ""
    assert "<think>" not in joined


async def test_stream_completion_wrong_model_yields_nothing():
    b = _bridge("m1")
    chunks = [c async for c in b.stream_completion_async(_req("other"))]
    assert chunks == []


async def test_stream_fragments_tags_kinds():
    b = _bridge("m1")
    kinds = set()
    async for f in b.stream_fragments_async(_req("m1")):
        kinds.add(f.kind)
    assert InferenceFragmentKind.CONTENT in kinds
    assert InferenceFragmentKind.REASONING in kinds


async def test_device_capabilities_injected():
    caps = DeviceCapabilities(
        os_name="Linux", os_version="6.1", physical_memory_bytes=8 * 1024**3,
        cpu_core_count=8, has_gpu=True, gpu_name="RTX", gpu_memory_bytes=12 * 1024**3,
        has_npu=False, npu_name=None, has_transport_layer_encryption=True,
    )
    b = _bridge("m1", caps)
    got = await b.get_device_capabilities_async()
    assert got.os_name == "Linux" and got.has_gpu is True


async def test_stop_sequence_sets_stopped_by_token():
    # StoppedByToken fires when a stop sequence is present in the produced
    # output. A generator that does NOT strip the stop (echoes it) exercises
    # the bridge's DetermineStatus stop-detection path — the same logic the C#
    # reference bridge runs. (The default deterministic generator strips stops,
    # so we use an echoing stub here.)
    class _EchoGenerator:
        async def generate_response_async(self, messages, options=None):
            from circle_ai.models.models import ChatResponse, FinishReason
            return ChatResponse(
                text="here is the END of it",
                tokens_in=1, tokens_out=5, latency_ms=1.0,
                finish_reason=FinishReason.STOP, reasoning_content=None,
            )

    b = LocalProcessInferenceBridge(_EchoGenerator(), _descriptor("m1"))
    req = InferenceRequest(
        id=uuid.uuid4(), model_id="m1", prompt="hi", max_output_tokens=256,
        temperature=0.7, top_p=0.9, stop_sequences=["END"], metadata={},
        requested_at=__import__("datetime").datetime.now(__import__("datetime").timezone.utc),
    )
    resp = await b.complete_async(req)
    assert resp.status == InferenceStatus.STOPPED_BY_TOKEN


async def test_short_output_is_completed():
    # Output shorter than max_output_tokens with no stop -> Completed.
    b = _bridge("m1")
    resp = await b.complete_async(_req("m1", "hi", max_tokens=100000))
    assert resp.status == InferenceStatus.COMPLETED


def test_ctor_validation():
    with pytest.raises(ValueError):
        LocalProcessInferenceBridge(None, _descriptor())
    with pytest.raises(ValueError):
        LocalProcessInferenceBridge(DeterministicChatGenerator("m1"), None)


def test_inference_request_create_factory():
    r = InferenceRequest.create("m1", "prompt")
    assert r.model_id == "m1" and r.prompt == "prompt"
    assert r.max_output_tokens == 256 and isinstance(r.id, uuid.UUID)
