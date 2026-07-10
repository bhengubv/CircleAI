"""test_inference_server.py

Verifies the in-memory inference-server handlers: chat completions (stream +
non-stream), embeddings, auth gate, admission cap, registry, counters, and the
OpenAI-shaped error envelopes.
"""
from __future__ import annotations

import json

import pytest

from circle_ai.hosting import LocalProcessInferenceBridge, ModelDescriptor, ModelFormat
from circle_ai.inference import DeterministicChatGenerator
from circle_ai.inference_server import (
    AdmissionControl,
    ApiKeyAuthHandler,
    ApiKeyOptions,
    AuthSchemes,
    ChatCompletionMessage,
    ChatCompletionRequest,
    ChatCompletionsHandler,
    CompanionTurnRequest,
    CompanionTurnHandler,
    EmbeddingsHandler,
    EmbeddingsRequest,
    InferenceServerModelRegistry,
    InferenceServerOptions,
    ServerCounters,
)


class _FakeEmbedder:
    async def generate_async(self, text, ct=None):
        return [float(len(text)), 0.5]


def _descriptor(model_id):
    return ModelDescriptor(model_id, "1.0", ModelFormat.GGUF, 4096, 151936, 0, None, 1024)


def _registry_with_model(model_id="qwen"):
    reg = InferenceServerModelRegistry()
    bridge = LocalProcessInferenceBridge(DeterministicChatGenerator(model_id), _descriptor(model_id))
    reg.register(model_id, bridge)
    return reg


def _chat_handler(reg=None, opts=None, auth=None):
    reg = reg or _registry_with_model()
    opts = opts or InferenceServerOptions()
    counters = ServerCounters()
    admission = AdmissionControl(opts, counters)
    return ChatCompletionsHandler(reg, admission, counters, opts, auth=auth), counters, admission


# ── chat completions ─────────────────────────────────────────────────────


async def test_chat_completion_non_stream_ok():
    handler, _, _ = _chat_handler()
    body = ChatCompletionRequest(model="qwen", messages=[ChatCompletionMessage("user", "hi")])
    res = await handler.handle(body)
    assert res.status_code == 200
    d = res.body_dict
    assert d["object"] == "chat.completion"
    assert d["choices"][0]["message"]["role"] == "assistant"
    assert d["choices"][0]["message"]["content"] != ""
    # reasoning surfaced by the deterministic generator
    assert d["choices"][0]["message"]["reasoning_content"] is not None
    assert d["usage"]["total_tokens"] == d["usage"]["prompt_tokens"] + d["usage"]["completion_tokens"]


async def test_chat_completion_missing_model_400():
    handler, _, _ = _chat_handler()
    res = await handler.handle(ChatCompletionRequest(model="", messages=[ChatCompletionMessage("user", "x")]))
    assert res.status_code == 400
    assert res.body_dict["error"]["code"] == "missing_model"


async def test_chat_completion_missing_messages_400():
    handler, _, _ = _chat_handler()
    res = await handler.handle(ChatCompletionRequest(model="qwen", messages=[]))
    assert res.status_code == 400
    assert res.body_dict["error"]["code"] == "missing_messages"


async def test_chat_completion_unknown_model_404():
    handler, _, _ = _chat_handler()
    res = await handler.handle(ChatCompletionRequest(model="nope", messages=[ChatCompletionMessage("user", "x")]))
    assert res.status_code == 404
    assert res.body_dict["error"]["code"] == "model_not_found"


async def test_chat_completion_stream_frames():
    handler, _, _ = _chat_handler()
    body = ChatCompletionRequest(model="qwen", messages=[ChatCompletionMessage("user", "hi")], stream=True)
    res = await handler.handle(body)
    assert res.sse_frames is not None
    # First frame is the role announcement, last is [DONE].
    first = json.loads(res.sse_frames[0][len("data: "):].strip())
    assert first["choices"][0]["delta"]["role"] == "assistant"
    assert res.sse_frames[-1] == "data: [DONE]\n\n"
    # Penultimate frame carries the stop finish_reason.
    stop_frame = json.loads(res.sse_frames[-2][len("data: "):].strip())
    assert stop_frame["choices"][0]["finish_reason"] == "stop"


async def test_chat_completion_stream_includes_reasoning_delta():
    handler, _, _ = _chat_handler()
    body = ChatCompletionRequest(model="qwen", messages=[ChatCompletionMessage("user", "hi")], stream=True)
    res = await handler.handle(body)
    saw_reasoning = False
    saw_content = False
    for frame in res.sse_frames:
        if frame.strip() == "data: [DONE]":
            continue
        payload = json.loads(frame[len("data: "):].strip())
        delta = payload["choices"][0]["delta"]
        if "reasoning_content" in delta:
            saw_reasoning = True
        if "content" in delta:
            saw_content = True
    assert saw_reasoning and saw_content


# ── auth ─────────────────────────────────────────────────────────────────


async def test_auth_disabled_allows():
    auth = ApiKeyAuthHandler(ApiKeyOptions(enabled=False))
    handler, _, _ = _chat_handler(auth=auth)
    res = await handler.handle(
        ChatCompletionRequest(model="qwen", messages=[ChatCompletionMessage("user", "hi")]), headers={}
    )
    assert res.status_code == 200


async def test_auth_enabled_rejects_missing_key():
    auth = ApiKeyAuthHandler(ApiKeyOptions(enabled=True, keys=["k1"]))
    handler, _, _ = _chat_handler(auth=auth)
    res = await handler.handle(
        ChatCompletionRequest(model="qwen", messages=[ChatCompletionMessage("user", "hi")]), headers={}
    )
    assert res.status_code == 401


async def test_auth_enabled_accepts_valid_key_case_insensitive_header():
    auth = ApiKeyAuthHandler(ApiKeyOptions(enabled=True, keys=["k1"], header_name="X-CircleAI-Api-Key"))
    handler, _, _ = _chat_handler(auth=auth)
    res = await handler.handle(
        ChatCompletionRequest(model="qwen", messages=[ChatCompletionMessage("user", "hi")]),
        headers={"x-circleai-api-key": "k1"},
    )
    assert res.status_code == 200


async def test_auth_rejects_wrong_key():
    auth = ApiKeyAuthHandler(ApiKeyOptions(enabled=True, keys=["right"]))
    handler, _, _ = _chat_handler(auth=auth)
    res = await handler.handle(
        ChatCompletionRequest(model="qwen", messages=[ChatCompletionMessage("user", "hi")]),
        headers={"X-CircleAI-Api-Key": "wrong"},
    )
    assert res.status_code == 401


# ── admission cap + counters ─────────────────────────────────────────────


async def test_admission_cap_rejects_when_saturated():
    reg = _registry_with_model()
    opts = InferenceServerOptions(max_concurrent_requests=1)
    counters = ServerCounters()
    admission = AdmissionControl(opts, counters)
    handler = ChatCompletionsHandler(reg, admission, counters, opts)

    # Manually hold the only slot, then a request must 503.
    held = admission.try_enter()
    assert held is not None
    res = await handler.handle(ChatCompletionRequest(model="qwen", messages=[ChatCompletionMessage("user", "hi")]))
    assert res.status_code == 503
    assert res.headers.get("Retry-After") == "1"
    held.release()

    # After release, the request admits.
    ok = await handler.handle(ChatCompletionRequest(model="qwen", messages=[ChatCompletionMessage("user", "hi")]))
    assert ok.status_code == 200


async def test_counters_track_admissions():
    handler, counters, _ = _chat_handler()
    await handler.handle(ChatCompletionRequest(model="qwen", messages=[ChatCompletionMessage("user", "hi")]))
    assert counters.total_requests == 1
    assert counters.active_requests == 0  # released after completion


# ── embeddings ───────────────────────────────────────────────────────────


async def test_embeddings_single_and_array():
    reg = InferenceServerModelRegistry()
    reg.register_embedder("emb", _FakeEmbedder())
    counters = ServerCounters()
    admission = AdmissionControl(InferenceServerOptions(), counters)
    h = EmbeddingsHandler(reg, admission, counters)

    r1 = await h.handle(EmbeddingsRequest(model="emb", input="hello"))
    assert r1.status_code == 200 and len(r1.body_dict["data"]) == 1

    r2 = await h.handle(EmbeddingsRequest(model="emb", input=["a", "bb"]))
    assert r2.status_code == 200 and len(r2.body_dict["data"]) == 2
    assert r2.body_dict["data"][1]["embedding"][0] == 2.0
    assert r2.body_dict["data"][1]["index"] == 1


async def test_embeddings_unknown_model_404():
    reg = InferenceServerModelRegistry()
    counters = ServerCounters()
    h = EmbeddingsHandler(reg, AdmissionControl(InferenceServerOptions(), counters), counters)
    res = await h.handle(EmbeddingsRequest(model="nope", input="x"))
    assert res.status_code == 404


async def test_embeddings_bad_input_400():
    reg = InferenceServerModelRegistry()
    reg.register_embedder("emb", _FakeEmbedder())
    counters = ServerCounters()
    h = EmbeddingsHandler(reg, AdmissionControl(InferenceServerOptions(), counters), counters)
    # Non-string array element.
    res = await h.handle(EmbeddingsRequest(model="emb", input=["ok", 5]))
    assert res.status_code == 400
    # Empty array.
    res2 = await h.handle(EmbeddingsRequest(model="emb", input=[]))
    assert res2.status_code == 400
    # Wrong type entirely.
    res3 = await h.handle(EmbeddingsRequest(model="emb", input=123))
    assert res3.status_code == 400


# ── registry ─────────────────────────────────────────────────────────────


def test_registry_register_resolve_deregister():
    reg = InferenceServerModelRegistry()
    bridge = LocalProcessInferenceBridge(DeterministicChatGenerator("m"), _descriptor("m"))
    reg.register("m", bridge)
    assert reg.resolve("m") is bridge
    assert reg.chat_model_ids() == ["m"]
    assert reg.deregister("m") is True
    assert reg.resolve("m") is None
    assert reg.deregister("m") is False


def test_registry_all_model_ids_distinct():
    reg = InferenceServerModelRegistry()
    reg.register("chat", LocalProcessInferenceBridge(DeterministicChatGenerator("chat"), _descriptor("chat")))
    reg.register_embedder("emb", _FakeEmbedder())
    reg.register_embedder("chat", _FakeEmbedder())  # same id in both maps
    ids = reg.all_model_ids()
    assert set(ids) == {"chat", "emb"}
    assert len(ids) == 2  # distinct


def test_registry_validation():
    reg = InferenceServerModelRegistry()
    with pytest.raises(ValueError):
        reg.register("", LocalProcessInferenceBridge(DeterministicChatGenerator("m"), _descriptor("m")))
    with pytest.raises(ValueError):
        reg.register("m", None)


def test_auth_schemes_constants():
    assert AuthSchemes.API_KEY == "ApiKey"
    assert AuthSchemes.JWT == "Bearer"
    assert AuthSchemes.AUTHENTICATED_POLICY == "Authenticated"
