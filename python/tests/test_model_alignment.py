"""test_model_alignment.py — CircleAI.ModelAlignment port.

Covers the alignment records, the in-memory toolkit (reversible-only apply,
revert, list) and the refuse-aligned-publish auditor, plus the fail-closed
Null* defaults. C# is the exact spec.
"""
from __future__ import annotations

from datetime import datetime, timezone

import pytest

from circle_ai import (
    AlignmentProfile,
    AlignmentResult,
    InMemoryAlignmentToolkit,
    NullAlignmentAuditor,
    NullAlignmentToolkit,
    RefuseAlignedPublishAuditor,
)


def _profile(pid: str = "p1", reversible: bool = True) -> AlignmentProfile:
    return AlignmentProfile(
        profile_id=pid,
        description="d",
        refusal_categories_removed=["cat-a", "cat-b"],
        created_at_utc=datetime.now(timezone.utc),
        is_reversible=reversible,
    )


def test_records_are_frozen():
    p = _profile()
    with pytest.raises(Exception):
        p.profile_id = "other"  # type: ignore[misc]
    r = AlignmentResult("p1", True, None)
    with pytest.raises(Exception):
        r.success = False  # type: ignore[misc]


# ── InMemoryAlignmentToolkit ──────────────────────────────────────────────────

async def test_toolkit_backend_id():
    assert InMemoryAlignmentToolkit().backend_id == "in-memory"


async def test_apply_reversible_succeeds_and_lists():
    tk = InMemoryAlignmentToolkit()
    res = await tk.apply_async("model-x", _profile("p1"))
    assert res == AlignmentResult("p1", True, None)
    applied = await tk.list_applied_async("model-x")
    assert [p.profile_id for p in applied] == ["p1"]


async def test_apply_non_reversible_is_refused():
    tk = InMemoryAlignmentToolkit()
    res = await tk.apply_async("model-x", _profile("p1", reversible=False))
    assert res.success is False
    assert res.profile_id == "p1"
    assert "Non-reversible" in (res.failure_reason or "")
    # Nothing recorded.
    assert await tk.list_applied_async("model-x") == []


async def test_apply_blank_model_id_raises():
    tk = InMemoryAlignmentToolkit()
    for bad in ("", "   ", None):
        with pytest.raises(ValueError):
            await tk.apply_async(bad, _profile())  # type: ignore[arg-type]


async def test_apply_none_profile_raises():
    tk = InMemoryAlignmentToolkit()
    with pytest.raises(ValueError):
        await tk.apply_async("model-x", None)  # type: ignore[arg-type]


async def test_revert_removes_profile():
    tk = InMemoryAlignmentToolkit()
    await tk.apply_async("m", _profile("p1"))
    await tk.apply_async("m", _profile("p2"))
    res = await tk.revert_async("m", "p1")
    assert res == AlignmentResult("p1", True, None)
    assert [p.profile_id for p in await tk.list_applied_async("m")] == ["p2"]


async def test_revert_unknown_model():
    tk = InMemoryAlignmentToolkit()
    res = await tk.revert_async("nope", "p1")
    assert res.success is False
    assert res.failure_reason == "Unknown model"


async def test_revert_profile_not_applied():
    tk = InMemoryAlignmentToolkit()
    await tk.apply_async("m", _profile("p1"))
    res = await tk.revert_async("m", "p-absent")
    assert res.success is False
    assert res.failure_reason == "Profile not applied to this model"


async def test_revert_blank_ids_raise():
    tk = InMemoryAlignmentToolkit()
    with pytest.raises(ValueError):
        await tk.revert_async("", "p1")
    with pytest.raises(ValueError):
        await tk.revert_async("m", "  ")


async def test_list_applied_blank_model_raises():
    tk = InMemoryAlignmentToolkit()
    with pytest.raises(ValueError):
        await tk.list_applied_async("")


async def test_list_applied_unknown_model_empty():
    tk = InMemoryAlignmentToolkit()
    assert await tk.list_applied_async("never-seen") == []


async def test_list_applied_returns_copy():
    tk = InMemoryAlignmentToolkit()
    await tk.apply_async("m", _profile("p1"))
    snapshot = await tk.list_applied_async("m")
    snapshot.clear()  # mutating the returned list must not affect internal state
    assert len(await tk.list_applied_async("m")) == 1


# ── RefuseAlignedPublishAuditor ───────────────────────────────────────────────

async def test_auditor_backend_id_and_null_toolkit_raises():
    tk = InMemoryAlignmentToolkit()
    auditor = RefuseAlignedPublishAuditor(tk)
    assert auditor.backend_id == "refuse-aligned"
    with pytest.raises(ValueError):
        RefuseAlignedPublishAuditor(None)  # type: ignore[arg-type]


async def test_auditor_allows_publish_when_clean():
    tk = InMemoryAlignmentToolkit()
    auditor = RefuseAlignedPublishAuditor(tk)
    # No profiles applied → no raise.
    await auditor.assert_ok_to_publish_async("clean-model")


async def test_auditor_refuses_publish_when_aligned():
    tk = InMemoryAlignmentToolkit()
    await tk.apply_async("m", _profile("p1"))
    auditor = RefuseAlignedPublishAuditor(tk)
    with pytest.raises(RuntimeError) as ei:
        await auditor.assert_ok_to_publish_async("m")
    assert "Cannot publish 'm'" in str(ei.value)
    assert "1 alignment profile" in str(ei.value)


async def test_auditor_blank_model_raises():
    auditor = RefuseAlignedPublishAuditor(InMemoryAlignmentToolkit())
    with pytest.raises(ValueError):
        await auditor.assert_ok_to_publish_async("   ")


# ── Null* defaults ────────────────────────────────────────────────────────────

async def test_null_toolkit_refuses_apply_and_revert():
    tk = NullAlignmentToolkit.Instance
    assert tk.backend_id == "null"
    res = await tk.apply_async("m", _profile("p1"))
    assert res.success is False
    assert res.profile_id == "p1"
    assert "no real backend" in (res.failure_reason or "")
    rev = await tk.revert_async("m", "p1")
    assert rev.success is False
    assert "nothing to revert" in (rev.failure_reason or "")
    assert await tk.list_applied_async("m") == []


async def test_null_auditor_never_raises():
    auditor = NullAlignmentAuditor.Instance
    assert auditor.backend_id == "null"
    assert await auditor.assert_ok_to_publish_async("m") is None


def test_null_singletons_shared():
    assert NullAlignmentToolkit.Instance is NullAlignmentToolkit.Instance
    assert NullAlignmentAuditor.Instance is NullAlignmentAuditor.Instance
