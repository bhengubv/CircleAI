"""Inference bridge contract + records + in-process reference implementation.

Port of the ``CircleAI.Hosting.InferenceBridge`` assembly:
  * records: ModelFormat, ModelDescriptor, InferenceRequest, InferenceStatus,
    InferenceResponse, InferenceFragmentKind, InferenceFragment,
    DeviceCapabilities,
  * contract: IInferenceBridge,
  * reference impl: LocalProcessInferenceBridge (wraps any IChatGenerator).

The C# ``StreamFragmentsAsync`` is a default-interface method; Python's ABC has
no such thing, so :class:`IInferenceBridge` provides a concrete default that
wraps :meth:`stream_completion_async` and tags every chunk as CONTENT — exactly
the C# default. Subclasses override for real reasoning splitting.
"""
from __future__ import annotations

import time
import uuid
from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from datetime import datetime, timezone
from enum import IntEnum
from typing import AsyncGenerator, Dict, List, Optional, Sequence

from ..inference.inference import GenerationOptions
from ..models.models import ChatFragmentKind, ChatMessage

__all__ = [
    "ModelFormat",
    "ModelDescriptor",
    "InferenceRequest",
    "InferenceStatus",
    "InferenceResponse",
    "InferenceFragmentKind",
    "InferenceFragment",
    "DeviceCapabilities",
    "IInferenceBridge",
    "LocalProcessInferenceBridge",
    "MockInferenceBridge",
]


# ── ModelFormat / ModelDescriptor ─────────────────────────────────────────


class ModelFormat(IntEnum):
    """On-disk encoding format of a model weight artefact. Mirrors ``ModelFormat``."""

    GGUF = 0
    ONNX = 1
    CORE_ML = 2
    TFLITE = 3
    UNKNOWN = 4


@dataclass(frozen=True, slots=True)
class ModelDescriptor:
    """Canonical descriptor for a single loaded model. Mirrors ``ModelDescriptor``."""

    model_id: str
    version: str
    format: ModelFormat
    context_window_tokens: int
    vocab_size: int
    parameter_count: int
    quantisation_label: Optional[str]
    approximate_memory_bytes: int


# ── InferenceRequest ──────────────────────────────────────────────────────


@dataclass(frozen=True, slots=True)
class InferenceRequest:
    """One completion request submitted to an :class:`IInferenceBridge`.
    Mirrors ``CircleAI.Hosting.InferenceBridge.InferenceRequest``.
    """

    id: uuid.UUID
    model_id: str
    prompt: str
    max_output_tokens: int
    temperature: float
    top_p: float
    stop_sequences: Sequence[str]
    metadata: Dict[str, str]
    requested_at: datetime

    @staticmethod
    def create(
        model_id: str,
        prompt: str,
        max_output_tokens: int = 256,
        temperature: float = 0.7,
        top_p: float = 0.95,
    ) -> "InferenceRequest":
        """Stamp a fresh id + timestamp with sensible defaults. Mirrors
        ``InferenceRequest.Create``.
        """
        if not model_id:
            raise ValueError("model_id is required")
        if prompt is None:
            raise ValueError("prompt is required")
        return InferenceRequest(
            id=uuid.uuid4(),
            model_id=model_id,
            prompt=prompt,
            max_output_tokens=max_output_tokens,
            temperature=temperature,
            top_p=top_p,
            stop_sequences=[],
            metadata={},
            requested_at=datetime.now(timezone.utc),
        )


# ── InferenceStatus / InferenceResponse ───────────────────────────────────


class InferenceStatus(IntEnum):
    """Terminal state of a single inference call. Mirrors ``InferenceStatus``."""

    COMPLETED = 0
    STOPPED_BY_TOKEN = 1
    STOPPED_BY_LENGTH = 2
    FAILED = 3
    CANCELLED = 4


@dataclass(frozen=True, slots=True)
class InferenceResponse:
    """Result of a single completion call. Mirrors ``InferenceResponse``."""

    request_id: uuid.UUID
    model_id: str
    output_text: str
    output_token_count: int
    prompt_token_count: int
    status: InferenceStatus
    inference_millis: float
    failure_message: Optional[str]
    completed_at: datetime
    reasoning_text: Optional[str] = None


# ── InferenceFragment ─────────────────────────────────────────────────────


class InferenceFragmentKind(IntEnum):
    """Kind of fragment a streaming bridge emits. Mirrors ``InferenceFragmentKind``."""

    CONTENT = 0
    REASONING = 1


@dataclass(frozen=True, slots=True)
class InferenceFragment:
    """A single fragment emitted by ``stream_fragments_async``. Mirrors
    ``InferenceFragment``.
    """

    kind: InferenceFragmentKind
    text: str


# ── DeviceCapabilities ────────────────────────────────────────────────────


@dataclass(frozen=True, slots=True)
class DeviceCapabilities:
    """Static-ish capabilities report from the device hosting the bridge.
    Mirrors ``CircleAI.Hosting.InferenceBridge.DeviceCapabilities``.
    """

    os_name: str
    os_version: str
    physical_memory_bytes: int
    cpu_core_count: int
    has_gpu: bool
    gpu_name: Optional[str]
    gpu_memory_bytes: Optional[int]
    has_npu: bool
    npu_name: Optional[str]
    has_transport_layer_encryption: bool


# ── IInferenceBridge ──────────────────────────────────────────────────────


class IInferenceBridge(ABC):
    """Cross-OS contract for an inference daemon. Mirrors ``IInferenceBridge``."""

    @abstractmethod
    async def list_loaded_models_async(self, ct: object = None) -> List[ModelDescriptor]: ...

    @abstractmethod
    async def is_model_loaded_async(self, model_id: str, ct: object = None) -> bool: ...

    @abstractmethod
    async def complete_async(
        self, request: InferenceRequest, ct: object = None
    ) -> InferenceResponse: ...

    @abstractmethod
    def stream_completion_async(
        self, request: InferenceRequest, ct: object = None
    ) -> AsyncGenerator[str, None]: ...

    async def stream_fragments_async(
        self, request: InferenceRequest, ct: object = None
    ) -> AsyncGenerator[InferenceFragment, None]:
        """Default: wrap :meth:`stream_completion_async`, tag every chunk as
        CONTENT. Mirrors the C# default-interface method. Subclasses override
        to interleave REASONING.
        """
        async for chunk in self.stream_completion_async(request, ct):
            yield InferenceFragment(InferenceFragmentKind.CONTENT, chunk)

    @abstractmethod
    async def get_device_capabilities_async(self, ct: object = None) -> DeviceCapabilities: ...


# ── LocalProcessInferenceBridge ───────────────────────────────────────────


def _estimate_token_count(text: str) -> int:
    if not text:
        return 0
    return max(1, len(text) // 4)


class LocalProcessInferenceBridge(IInferenceBridge):
    """In-process :class:`IInferenceBridge` wrapping any ``IChatGenerator``.
    Port of ``CircleAI.Hosting.InferenceBridge.LocalProcessInferenceBridge``.

    Transport-layer encryption is reported ``True`` because calls never leave
    the host process. Device capabilities are supplied by an injected
    :class:`DeviceCapabilities` (the C# delegates to ``ICapabilityProbe``;
    Python injects a value directly to keep the bridge self-contained). When
    omitted, a conservative single-CPU default is reported.
    """

    __slots__ = ("_generator", "_descriptor", "_capabilities")

    def __init__(
        self,
        chat_generator,
        descriptor: ModelDescriptor,
        capabilities: Optional[DeviceCapabilities] = None,
    ) -> None:
        if chat_generator is None:
            raise ValueError("chat_generator is required")
        if descriptor is None:
            raise ValueError("descriptor is required")
        self._generator = chat_generator
        self._descriptor = descriptor
        self._capabilities = capabilities or _default_capabilities()

    async def list_loaded_models_async(self, ct: object = None) -> List[ModelDescriptor]:
        return [self._descriptor]

    async def is_model_loaded_async(self, model_id: str, ct: object = None) -> bool:
        if not model_id:
            raise ValueError("model_id is required")
        return self._descriptor.model_id == model_id

    async def complete_async(
        self, request: InferenceRequest, ct: object = None
    ) -> InferenceResponse:
        if request is None:
            raise ValueError("request is required")

        if self._descriptor.model_id != request.model_id:
            return InferenceResponse(
                request_id=request.id,
                model_id=request.model_id,
                output_text="",
                output_token_count=0,
                prompt_token_count=0,
                status=InferenceStatus.FAILED,
                inference_millis=0.0,
                failure_message=(
                    f"Model '{request.model_id}' is not loaded by this bridge "
                    f"(have '{self._descriptor.model_id}')."
                ),
                completed_at=datetime.now(timezone.utc),
            )

        messages = [ChatMessage("user", request.prompt)]
        options = _options_from_request(request)

        started = time.monotonic()
        output = ""
        reasoning: Optional[str] = None
        failure_message: Optional[str] = None
        try:
            response = await self._generator.generate_response_async(messages, options)
            output = response.text
            reasoning = response.reasoning_content
            status = _determine_status(output, request)
        except Exception as ex:  # noqa: BLE001 - mirror C# catch-all failure path
            elapsed = (time.monotonic() - started) * 1000.0
            output = ""
            status = InferenceStatus.FAILED
            failure_message = str(ex)
            return InferenceResponse(
                request_id=request.id,
                model_id=request.model_id,
                output_text=output,
                output_token_count=_estimate_token_count(output),
                prompt_token_count=_estimate_token_count(request.prompt),
                status=status,
                inference_millis=elapsed,
                failure_message=failure_message,
                completed_at=datetime.now(timezone.utc),
            )

        elapsed = (time.monotonic() - started) * 1000.0
        return InferenceResponse(
            request_id=request.id,
            model_id=request.model_id,
            output_text=output,
            output_token_count=_estimate_token_count(output),
            prompt_token_count=_estimate_token_count(request.prompt),
            status=status,
            inference_millis=elapsed,
            failure_message=failure_message,
            completed_at=datetime.now(timezone.utc),
            reasoning_text=reasoning,
        )

    async def stream_completion_async(
        self, request: InferenceRequest, ct: object = None
    ) -> AsyncGenerator[str, None]:
        if request is None:
            raise ValueError("request is required")
        if self._descriptor.model_id != request.model_id:
            return

        messages = [ChatMessage("user", request.prompt)]
        options = _options_from_request(request)

        has_yielded = False
        async for chunk in self._generator.stream_async(messages, options):
            has_yielded = True
            yield chunk

        if not has_yielded:
            full = await self._generator.generate_async(messages, options)
            yield full

    async def stream_fragments_async(
        self, request: InferenceRequest, ct: object = None
    ) -> AsyncGenerator[InferenceFragment, None]:
        if request is None:
            raise ValueError("request is required")
        if self._descriptor.model_id != request.model_id:
            return

        messages = [ChatMessage("user", request.prompt)]
        options = _options_from_request(request)

        async for f in self._generator.stream_fragments_async(messages, options):
            kind = (
                InferenceFragmentKind.REASONING
                if f.kind == ChatFragmentKind.REASONING
                else InferenceFragmentKind.CONTENT
            )
            yield InferenceFragment(kind, f.text)

    async def get_device_capabilities_async(self, ct: object = None) -> DeviceCapabilities:
        return self._capabilities


def _options_from_request(request: InferenceRequest) -> GenerationOptions:
    return GenerationOptions(
        max_tokens=request.max_output_tokens,
        temperature=request.temperature,
        top_p=request.top_p,
        stop_sequences=(
            None if len(request.stop_sequences) == 0 else list(request.stop_sequences)
        ),
    )


def _determine_status(output: str, request: InferenceRequest) -> InferenceStatus:
    if len(request.stop_sequences) > 0:
        for s in request.stop_sequences:
            if s and s in output:
                return InferenceStatus.STOPPED_BY_TOKEN
    produced = _estimate_token_count(output)
    return (
        InferenceStatus.STOPPED_BY_LENGTH
        if produced >= request.max_output_tokens
        else InferenceStatus.COMPLETED
    )


def _default_capabilities() -> DeviceCapabilities:
    return DeviceCapabilities(
        os_name="Unknown",
        os_version="",
        physical_memory_bytes=0,
        cpu_core_count=1,
        has_gpu=False,
        gpu_name=None,
        gpu_memory_bytes=None,
        has_npu=False,
        npu_name=None,
        has_transport_layer_encryption=True,
    )


# ── MockInferenceBridge ───────────────────────────────────────────────────


class MockInferenceBridge(IInferenceBridge):
    """Deterministic :class:`IInferenceBridge` for tests. Returns the same
    canned output for every call and reports a single fixed-mock model as
    loaded. Port of ``CircleAI.Hosting.InferenceBridge.MockInferenceBridge``.
    """

    __slots__ = ("_canned_output", "_latency_millis", "_descriptor")

    def __init__(
        self,
        canned_output: str,
        latency_millis: int = 0,
        model_id: str = "mock-model",
    ) -> None:
        if canned_output is None:
            raise ValueError("canned_output is required")
        if latency_millis < 0:
            raise ValueError("latency_millis must be non-negative.")
        self._canned_output = canned_output
        self._latency_millis = latency_millis
        self._descriptor = ModelDescriptor(
            model_id=model_id,
            version="mock-1.0.0",
            format=ModelFormat.UNKNOWN,
            context_window_tokens=4096,
            vocab_size=32000,
            parameter_count=0,
            quantisation_label=None,
            approximate_memory_bytes=0,
        )

    @property
    def descriptor(self) -> ModelDescriptor:
        """The model descriptor this mock reports as loaded."""
        return self._descriptor

    async def list_loaded_models_async(self, ct: object = None) -> List[ModelDescriptor]:
        return [self._descriptor]

    async def is_model_loaded_async(self, model_id: str, ct: object = None) -> bool:
        if not model_id:
            raise ValueError("model_id is required")
        return self._descriptor.model_id == model_id

    async def complete_async(
        self, request: InferenceRequest, ct: object = None
    ) -> InferenceResponse:
        if request is None:
            raise ValueError("request is required")
        started = time.monotonic()
        if self._latency_millis > 0:
            import asyncio

            await asyncio.sleep(self._latency_millis / 1000.0)
        elapsed = (time.monotonic() - started) * 1000.0
        return InferenceResponse(
            request_id=request.id,
            model_id=self._descriptor.model_id,
            output_text=self._canned_output,
            output_token_count=max(0, len(self._canned_output) // 4),
            prompt_token_count=max(0, len(request.prompt) // 4),
            status=InferenceStatus.COMPLETED,
            inference_millis=elapsed,
            failure_message=None,
            completed_at=datetime.now(timezone.utc),
        )

    async def stream_completion_async(
        self, request: InferenceRequest, ct: object = None
    ) -> AsyncGenerator[str, None]:
        if request is None:
            raise ValueError("request is required")
        if self._latency_millis > 0:
            import asyncio

            await asyncio.sleep(self._latency_millis / 1000.0)
        yield self._canned_output

    async def get_device_capabilities_async(self, ct: object = None) -> DeviceCapabilities:
        return DeviceCapabilities(
            os_name="Mock",
            os_version="1.0",
            physical_memory_bytes=4 * 1024 * 1024 * 1024,
            cpu_core_count=1,
            has_gpu=False,
            gpu_name=None,
            gpu_memory_bytes=None,
            has_npu=False,
            npu_name=None,
            has_transport_layer_encryption=True,
        )
