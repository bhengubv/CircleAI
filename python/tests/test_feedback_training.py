"""test_feedback_training.py — FileBackedFeedbackTrainingQueue FIFO + persistence."""
from __future__ import annotations

import pytest

from circle_ai.inference import FileBackedFeedbackTrainingQueue, TrainingSample


def _sample(user: str, pol: int = 1) -> TrainingSample:
    return TrainingSample(
        user_text=user,
        assistant_text=f"reply-{user}",
        preferred_text=f"pref-{user}",
        polarity=pol,
        at_utc="2026-07-08T00:00:00+00:00",
    )


async def test_enqueue_increments_pending(tmp_path):
    q = FileBackedFeedbackTrainingQueue(str(tmp_path / "q.jsonl"))
    assert q.pending == 0
    await q.enqueue_async(_sample("a"))
    await q.enqueue_async(_sample("b"))
    assert q.pending == 2


async def test_drain_is_fifo_and_leaves_remainder(tmp_path):
    q = FileBackedFeedbackTrainingQueue(str(tmp_path / "q.jsonl"))
    for i in range(5):
        await q.enqueue_async(_sample(str(i)))
    taken = await q.drain_async(3)
    assert [t.user_text for t in taken] == ["0", "1", "2"]
    assert q.pending == 2
    rest = await q.drain_async(10)
    assert [t.user_text for t in rest] == ["3", "4"]
    assert q.pending == 0


async def test_drain_roundtrips_all_fields(tmp_path):
    q = FileBackedFeedbackTrainingQueue(str(tmp_path / "q.jsonl"))
    s = TrainingSample("u", "a", "p", -1, "2026-01-01T00:00:00+00:00")
    await q.enqueue_async(s)
    (got,) = await q.drain_async(1)
    assert got == s


async def test_persistence_across_instances(tmp_path):
    path = str(tmp_path / "q.jsonl")
    q1 = FileBackedFeedbackTrainingQueue(path)
    await q1.enqueue_async(_sample("x"))
    await q1.enqueue_async(_sample("y"))
    q2 = FileBackedFeedbackTrainingQueue(path)  # re-open
    assert q2.pending == 2
    taken = await q2.drain_async(2)
    assert [t.user_text for t in taken] == ["x", "y"]


async def test_drain_empty_returns_empty(tmp_path):
    q = FileBackedFeedbackTrainingQueue(str(tmp_path / "q.jsonl"))
    assert await q.drain_async(5) == []


async def test_drain_skips_malformed_line(tmp_path):
    path = str(tmp_path / "q.jsonl")
    q = FileBackedFeedbackTrainingQueue(path)
    await q.enqueue_async(_sample("ok"))
    # Inject a malformed line.
    with open(path, "a", encoding="utf-8") as fh:
        fh.write("not-json\n")
    await q.enqueue_async(_sample("ok2"))
    taken = await q.drain_async(3)
    # malformed line consumed but skipped -> 2 valid samples returned.
    assert [t.user_text for t in taken] == ["ok", "ok2"]
    assert q.pending == 0


def test_ctor_requires_path():
    with pytest.raises(ValueError):
        FileBackedFeedbackTrainingQueue("")


async def test_drain_validates_max_samples(tmp_path):
    q = FileBackedFeedbackTrainingQueue(str(tmp_path / "q.jsonl"))
    with pytest.raises(ValueError):
        await q.drain_async(0)
