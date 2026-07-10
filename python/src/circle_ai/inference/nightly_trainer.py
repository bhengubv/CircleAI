"""Nightly LoRA adapter trainer (Phase D3) + LoRA adapter manager (RT-10).

Ports:
  * ``CircleAI.Inference.LoRAAdapterManager`` — apply / read / unapply / train /
    save on a loaded model. The C# manager P/Invokes ``mnnbridge`` LoRA calls;
    Python injects the native seam behind :class:`ILoRANative`, with a
    deterministic in-memory default that computes a reproducible loss and
    persists adapter weights to disk so train/save/apply round-trip.
  * ``CircleAI.Inference.NightlyAdapterTrainerOptions`` + ``NightlyAdapterTrainer``
    — periodically drains the feedback queue, runs LoRA gradient steps, saves
    the adapter, and applies it. The idle-and-charging gate is host-supplied.
    Python drops the ``IHostedService`` background loop (no timer host here) and
    exposes :meth:`run_once_async` as the drain+train pass — the same public
    entry point the C# host triggers manually.
"""
from __future__ import annotations

import json
import os
from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from typing import Callable, List, Optional

from .feedback_training import IFeedbackTrainingQueue, TrainingSample

__all__ = [
    "TrainingNotSupportedError",
    "ILoRANative",
    "InMemoryLoRANative",
    "LoRAAdapterManager",
    "NightlyAdapterTrainerOptions",
    "NightlyAdapterTrainer",
    "char_tokenizer",
]


class TrainingNotSupportedError(RuntimeError):
    """Raised when the native runtime was built without training support.

    Mirrors the C# ``NotSupportedException`` thrown by ``TrainStep`` when the
    bridge returns ``MNNBRIDGE_ERR_TRAINING_DISABLED`` (-12). The trainer
    re-queues the batch and skips the run when this is raised.
    """


class ILoRANative(ABC):
    """Injected native seam over the mnnbridge LoRA ABI. Mirrors the P/Invoke
    surface ``LoRAAdapterManager`` binds to.

    ``train_step`` returns a raw status code (0 success, -12 training disabled,
    other < 0 = error) plus the loss via the return tuple.
    """

    @abstractmethod
    def apply(self, handle: object, adapter_path: str) -> int: ...

    @abstractmethod
    def unapply(self, handle: object) -> int: ...

    @abstractmethod
    def current_adapter(self, handle: object) -> Optional[str]: ...

    @abstractmethod
    def train_step(
        self,
        handle: object,
        input_tokens: List[int],
        target_tokens: List[int],
        learning_rate: float,
        lora_rank: int,
    ) -> tuple[int, float]: ...

    @abstractmethod
    def save_lora(self, handle: object, adapter_path: str) -> int: ...


class InMemoryLoRANative(ILoRANative):
    """Deterministic in-memory stand-in for the mnnbridge LoRA ABI.

    Maintains per-handle "adapter weights" (a running accumulator over training
    steps) so that :meth:`train_step` returns a reproducible, monotonically
    decreasing loss, :meth:`save_lora` persists the weights, :meth:`apply`
    records the last-applied path, and :meth:`current_adapter` reads it back.

    Set ``training_enabled=False`` to simulate a bridge built without
    ``MNN_BUILD_TRAIN`` (train_step returns -12).
    """

    __slots__ = ("_training_enabled", "_weights", "_applied", "_steps")

    def __init__(self, training_enabled: bool = True) -> None:
        self._training_enabled = training_enabled
        self._weights: dict[int, float] = {}
        self._applied: dict[int, str] = {}
        self._steps: dict[int, int] = {}

    def apply(self, handle: object, adapter_path: str) -> int:
        if handle is None:
            return -1
        self._applied[id(handle)] = adapter_path
        return 0

    def unapply(self, handle: object) -> int:
        if handle is None:
            return -1
        self._applied.pop(id(handle), None)
        return 0

    def current_adapter(self, handle: object) -> Optional[str]:
        if handle is None:
            return None
        return self._applied.get(id(handle))

    def train_step(
        self,
        handle: object,
        input_tokens: List[int],
        target_tokens: List[int],
        learning_rate: float,
        lora_rank: int,
    ) -> tuple[int, float]:
        if not self._training_enabled:
            return (-12, 0.0)
        if handle is None:
            return (-1, 0.0)
        hid = id(handle)
        step = self._steps.get(hid, 0) + 1
        self._steps[hid] = step
        # Deterministic "loss": shrinks as steps accumulate, scaled by the
        # input/target token magnitude. Purely for reproducible bookkeeping.
        base = (sum(input_tokens) + sum(target_tokens)) % 97 + 1
        loss = base / (step + lora_rank)
        self._weights[hid] = self._weights.get(hid, 0.0) + learning_rate * loss
        return (0, float(loss))

    def save_lora(self, handle: object, adapter_path: str) -> int:
        if handle is None:
            return -1
        d = os.path.dirname(adapter_path)
        if d:
            os.makedirs(d, exist_ok=True)
        weight = self._weights.get(id(handle), 0.0)
        steps = self._steps.get(id(handle), 0)
        with open(adapter_path, "w", encoding="utf-8") as fh:
            json.dump({"weight": weight, "steps": steps}, fh)
        return 0


class LoRAAdapterManager:
    """RT-10 LoRA adapter manager. Port of ``CircleAI.Inference.LoRAAdapterManager``.

    Wraps a model ``handle`` (opaque) and an injected :class:`ILoRANative`
    (defaults to :class:`InMemoryLoRANative`). Methods mirror the C# surface
    and raise on native error codes.
    """

    __slots__ = ("_handle", "_native")

    def __init__(self, handle: object, native: Optional[ILoRANative] = None) -> None:
        self._handle = handle
        self._native = native if native is not None else InMemoryLoRANative()

    def apply(self, adapter_path: str) -> None:
        if not adapter_path or not adapter_path.strip():
            raise ValueError("adapter_path required")
        if not (os.path.isfile(adapter_path) or os.path.isdir(adapter_path)):
            raise FileNotFoundError(f"LoRA adapter not found: {adapter_path}")
        r = self._native.apply(self._handle, adapter_path)
        if r != 0:
            raise RuntimeError(f"apply_lora failed: {r}")

    def unapply(self) -> None:
        r = self._native.unapply(self._handle)
        if r != 0:
            raise RuntimeError(f"unapply_lora failed: {r}")

    def current_adapter(self) -> Optional[str]:
        return self._native.current_adapter(self._handle)

    def train_step(
        self,
        input_tokens: List[int],
        target_tokens: List[int],
        learning_rate: float = 1e-4,
        lora_rank: int = 8,
    ) -> float:
        """Run one gradient-descent step; return the scalar loss. Raises
        :class:`TrainingNotSupportedError` when the native runtime lacks
        training support (mirrors the C# ``NotSupportedException`` on -12).
        """
        if not input_tokens:
            raise ValueError("input_tokens required")
        if not target_tokens:
            raise ValueError("target_tokens required")
        if learning_rate <= 0:
            raise ValueError("learning_rate must be > 0")
        if lora_rank <= 0:
            raise ValueError("lora_rank must be > 0")
        rc, loss = self._native.train_step(
            self._handle, input_tokens, target_tokens, learning_rate, lora_rank
        )
        if rc == -12:
            raise TrainingNotSupportedError(
                "native runtime was compiled without training support. "
                "Rebuild with training enabled for on-device LoRA fine-tuning."
            )
        if rc != 0:
            raise RuntimeError(f"train_lora_step failed: {rc}")
        return loss

    def save_adapter(self, adapter_path: str) -> None:
        if not adapter_path or not adapter_path.strip():
            raise ValueError("adapter_path required")
        d = os.path.dirname(adapter_path)
        if d:
            os.makedirs(d, exist_ok=True)
        rc = self._native.save_lora(self._handle, adapter_path)
        if rc != 0:
            raise RuntimeError(f"save_lora failed: {rc}")


def char_tokenizer(text: str) -> List[int]:
    """Char-level tokenizer fallback — each char becomes its code-point value.

    Port of ``NightlyAdapterTrainer.CharTokenizer``.
    """
    if not text:
        return []
    return [ord(c) for c in text]


@dataclass(frozen=True, slots=True)
class NightlyAdapterTrainerOptions:
    """Options for the nightly trainer. Mirrors
    ``CircleAI.Inference.NightlyAdapterTrainerOptions``.

    * ``min_batch_size`` — minimum samples to bother training; skip otherwise.
    * ``max_samples_per_run`` — cap per run so a backlog can't lock the device.
    * ``learning_rate`` — LR for the LoRA adapter parameters.
    * ``lora_rank`` — rank of the LoRA decomposition.
    * ``adapter_path`` — where to persist the trained adapter file.
    * ``interval_seconds`` — how often to check whether to train (default 6h).
    * ``should_fire_now`` — optional gate (battery/charging/idle); default fires.
    * ``tokenizer`` — text -> int IDs; falls back to :func:`char_tokenizer`.
    """

    min_batch_size: int = 16
    max_samples_per_run: int = 256
    learning_rate: float = 1e-4
    lora_rank: int = 8
    adapter_path: str = "circleai-lora.mnn"
    interval_seconds: float = 6 * 60 * 60
    should_fire_now: Optional[Callable[[], bool]] = None
    tokenizer: Optional[Callable[[str], List[int]]] = None


@dataclass(frozen=True, slots=True)
class NightlyRunResult:
    """Outcome of a single :meth:`NightlyAdapterTrainer.run_once_async`.

    Not present in C# (there the method is ``void``), added here so callers can
    assert on what happened without scraping logs.
    """

    trained_steps: int
    average_loss: float
    skipped_reason: Optional[str] = None
    saved: bool = False


class NightlyAdapterTrainer:
    """Drains the feedback queue and trains a LoRA adapter. Port of
    ``CircleAI.Inference.NightlyAdapterTrainer``.

    The C# type is an ``IHostedService`` with a timer loop; the drain+train
    logic lives in ``RunOnceAsync``. Python ports ``run_once_async`` faithfully
    and drops the timer host (a caller schedules it). ``should_fire_now`` is
    honoured inside :meth:`run_once_async` so the gate is testable.
    """

    __slots__ = ("_queue", "_adapter", "_opts")

    def __init__(
        self,
        queue: IFeedbackTrainingQueue,
        adapter: LoRAAdapterManager,
        opts: NightlyAdapterTrainerOptions,
    ) -> None:
        if queue is None:
            raise ValueError("queue is required")
        if adapter is None:
            raise ValueError("adapter is required")
        if opts is None:
            raise ValueError("opts is required")
        self._queue = queue
        self._adapter = adapter
        self._opts = opts

    async def run_once_async(self, ct: object = None) -> NightlyRunResult:
        """Drain + train in one pass. Mirrors ``RunOnceAsync``.

        * Skips when fewer than ``min_batch_size`` samples are pending.
        * Tokenizes each sample (target = preferred for polarity>=0, else the
          assistant text), runs a train step, and averages the loss.
        * On :class:`TrainingNotSupportedError`, re-queues the whole batch and
          bails (matching the C# re-queue-and-return).
        * Saves + applies the adapter when any step ran.
        """
        # Optional host gate.
        if self._opts.should_fire_now is not None and not self._opts.should_fire_now():
            return NightlyRunResult(0, 0.0, skipped_reason="gate_closed")

        if self._queue.pending < self._opts.min_batch_size:
            return NightlyRunResult(
                0,
                0.0,
                skipped_reason=f"pending {self._queue.pending} < min {self._opts.min_batch_size}",
            )

        samples = await self._queue.drain_async(self._opts.max_samples_per_run, ct)
        if len(samples) == 0:
            return NightlyRunResult(0, 0.0, skipped_reason="drained_empty")

        tokenizer = self._opts.tokenizer or char_tokenizer
        total_loss = 0.0
        step_count = 0
        for sample in samples:
            try:
                inp = tokenizer(sample.user_text)
                target = tokenizer(
                    sample.preferred_text if sample.polarity >= 0 else sample.assistant_text
                )
                if len(inp) == 0 or len(target) == 0:
                    continue
                loss = self._adapter.train_step(
                    inp, target, self._opts.learning_rate, self._opts.lora_rank
                )
                total_loss += loss
                step_count += 1
            except TrainingNotSupportedError:
                # Native training unavailable — re-queue and bail.
                for s in samples:
                    await self._queue.enqueue_async(s, ct)
                return NightlyRunResult(0, 0.0, skipped_reason="training_disabled")
            except Exception:
                # Per-sample step failure — skip this sample, keep going.
                continue

        saved = False
        avg_loss = 0.0
        if step_count > 0:
            avg_loss = total_loss / step_count
            try:
                self._adapter.save_adapter(self._opts.adapter_path)
                self._adapter.apply(self._opts.adapter_path)
                saved = True
            except Exception:
                saved = False

        return NightlyRunResult(step_count, avg_loss, saved=saved)
