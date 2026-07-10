"""test_nightly_trainer.py — NightlyAdapterTrainer + LoRAAdapterManager."""
from __future__ import annotations

import os

import pytest

from circle_ai.inference import (
    FileBackedFeedbackTrainingQueue,
    InMemoryLoRANative,
    LoRAAdapterManager,
    NightlyAdapterTrainer,
    NightlyAdapterTrainerOptions,
    TrainingNotSupportedError,
    TrainingSample,
    char_tokenizer,
)


def _sample(user: str, pol: int = 1) -> TrainingSample:
    return TrainingSample(user, f"asst-{user}", f"pref-{user}", pol, "2026-07-08T00:00:00+00:00")


# ── char tokenizer ───────────────────────────────────────────────────────


def test_char_tokenizer():
    assert char_tokenizer("AB") == [65, 66]
    assert char_tokenizer("") == []


# ── LoRAAdapterManager ───────────────────────────────────────────────────


def test_lora_train_step_returns_loss():
    mgr = LoRAAdapterManager(handle=object())
    loss = mgr.train_step([1, 2, 3], [4, 5, 6])
    assert loss > 0


def test_lora_train_step_validates():
    mgr = LoRAAdapterManager(handle=object())
    with pytest.raises(ValueError):
        mgr.train_step([], [1])
    with pytest.raises(ValueError):
        mgr.train_step([1], [])
    with pytest.raises(ValueError):
        mgr.train_step([1], [1], learning_rate=0)
    with pytest.raises(ValueError):
        mgr.train_step([1], [1], lora_rank=0)


def test_lora_train_step_raises_when_training_disabled():
    mgr = LoRAAdapterManager(handle=object(), native=InMemoryLoRANative(training_enabled=False))
    with pytest.raises(TrainingNotSupportedError):
        mgr.train_step([1], [2])


def test_lora_save_and_apply_roundtrip(tmp_path):
    handle = object()
    mgr = LoRAAdapterManager(handle=handle)
    mgr.train_step([1, 2], [3, 4])
    path = str(tmp_path / "adapter" / "lora.mnn")
    mgr.save_adapter(path)
    assert os.path.isfile(path)
    mgr.apply(path)
    assert mgr.current_adapter() == path
    mgr.unapply()
    assert mgr.current_adapter() is None


def test_lora_apply_missing_file_raises():
    mgr = LoRAAdapterManager(handle=object())
    with pytest.raises(FileNotFoundError):
        mgr.apply("/no/such/adapter.mnn")


# ── NightlyAdapterTrainer.run_once ───────────────────────────────────────


async def test_run_once_skips_below_min_batch(tmp_path):
    q = FileBackedFeedbackTrainingQueue(str(tmp_path / "q.jsonl"))
    await q.enqueue_async(_sample("a"))
    mgr = LoRAAdapterManager(handle=object())
    opts = NightlyAdapterTrainerOptions(min_batch_size=5, adapter_path=str(tmp_path / "l.mnn"))
    trainer = NightlyAdapterTrainer(q, mgr, opts)
    res = await trainer.run_once_async()
    assert res.trained_steps == 0
    assert res.skipped_reason and "min" in res.skipped_reason
    assert q.pending == 1  # not drained


async def test_run_once_trains_and_saves(tmp_path):
    q = FileBackedFeedbackTrainingQueue(str(tmp_path / "q.jsonl"))
    for i in range(4):
        await q.enqueue_async(_sample(f"u{i}"))
    handle = object()
    mgr = LoRAAdapterManager(handle=handle)
    adapter_path = str(tmp_path / "lora.mnn")
    opts = NightlyAdapterTrainerOptions(min_batch_size=2, adapter_path=adapter_path)
    trainer = NightlyAdapterTrainer(q, mgr, opts)
    res = await trainer.run_once_async()
    assert res.trained_steps == 4
    assert res.average_loss > 0
    assert res.saved is True
    assert os.path.isfile(adapter_path)
    assert mgr.current_adapter() == adapter_path  # applied after save
    assert q.pending == 0


async def test_run_once_gate_closed_skips(tmp_path):
    q = FileBackedFeedbackTrainingQueue(str(tmp_path / "q.jsonl"))
    for i in range(4):
        await q.enqueue_async(_sample(f"u{i}"))
    mgr = LoRAAdapterManager(handle=object())
    opts = NightlyAdapterTrainerOptions(
        min_batch_size=1, should_fire_now=lambda: False, adapter_path=str(tmp_path / "l.mnn")
    )
    trainer = NightlyAdapterTrainer(q, mgr, opts)
    res = await trainer.run_once_async()
    assert res.trained_steps == 0
    assert res.skipped_reason == "gate_closed"
    assert q.pending == 4  # untouched


async def test_run_once_requeues_when_training_disabled(tmp_path):
    q = FileBackedFeedbackTrainingQueue(str(tmp_path / "q.jsonl"))
    for i in range(3):
        await q.enqueue_async(_sample(f"u{i}"))
    mgr = LoRAAdapterManager(handle=object(), native=InMemoryLoRANative(training_enabled=False))
    opts = NightlyAdapterTrainerOptions(min_batch_size=1, adapter_path=str(tmp_path / "l.mnn"))
    trainer = NightlyAdapterTrainer(q, mgr, opts)
    res = await trainer.run_once_async()
    assert res.trained_steps == 0
    assert res.skipped_reason == "training_disabled"
    # Samples re-queued after the disabled failure.
    assert q.pending == 3


async def test_run_once_uses_assistant_text_for_negative_polarity(tmp_path):
    # Empty preferred_text with polarity>=0 would yield empty target -> skipped;
    # with polarity<0 the assistant text is used, so the step runs.
    q = FileBackedFeedbackTrainingQueue(str(tmp_path / "q.jsonl"))
    await q.enqueue_async(TrainingSample("hi", "the-assistant-answer", "", -1, "t"))
    mgr = LoRAAdapterManager(handle=object())
    opts = NightlyAdapterTrainerOptions(min_batch_size=1, adapter_path=str(tmp_path / "l.mnn"))
    trainer = NightlyAdapterTrainer(q, mgr, opts)
    res = await trainer.run_once_async()
    assert res.trained_steps == 1


def test_trainer_ctor_validation(tmp_path):
    q = FileBackedFeedbackTrainingQueue(str(tmp_path / "q.jsonl"))
    mgr = LoRAAdapterManager(handle=object())
    opts = NightlyAdapterTrainerOptions()
    with pytest.raises(ValueError):
        NightlyAdapterTrainer(None, mgr, opts)
    with pytest.raises(ValueError):
        NightlyAdapterTrainer(q, None, opts)
    with pytest.raises(ValueError):
        NightlyAdapterTrainer(q, mgr, None)
