"""test_capability_registry.py

Verifies the ExternalCapabilityRegistry ported from CircleAI.Companion
(CapabilityRegistry.cs): the full entry set, case-insensitive lookup by id, and
by-target-package filtering.
"""
from __future__ import annotations

from circle_ai.companion.capability_registry import (
    CapabilityEntry,
    ExternalCapabilityRegistry,
)


def test_has_all_thirty_entries() -> None:
    assert len(ExternalCapabilityRegistry.All) == 30


def test_entries_are_frozen_records() -> None:
    e = ExternalCapabilityRegistry.All[0]
    assert isinstance(e, CapabilityEntry)
    import pytest

    with pytest.raises(Exception):
        e.id = "x"  # type: ignore[misc]


def test_ids_are_unique() -> None:
    ids = [c.id for c in ExternalCapabilityRegistry.All]
    assert len(ids) == len(set(ids))


def test_find_is_case_insensitive() -> None:
    assert ExternalCapabilityRegistry.find("claude-mem") is not None
    assert ExternalCapabilityRegistry.find("CLAUDE-MEM") is not None
    assert ExternalCapabilityRegistry.find("HippoRAG").repo == "OSU-NLP-Group/HippoRAG"


def test_find_unknown_returns_none() -> None:
    assert ExternalCapabilityRegistry.find("does-not-exist") is None


def test_by_package_filters() -> None:
    speech = ExternalCapabilityRegistry.by_package("CircleAI.Speech")
    ids = {c.id for c in speech}
    assert ids == {"Amphion", "yapsnap"}


def test_by_package_case_insensitive() -> None:
    a = ExternalCapabilityRegistry.by_package("circleai.games")
    assert {c.id for c in a} == {"aimangastudio", "flame"}


def test_known_entry_fields() -> None:
    claude_mem = ExternalCapabilityRegistry.find("claude-mem")
    assert claude_mem.license == "MIT"
    assert claude_mem.strategy == "pattern-port"
    assert claude_mem.target_package == "CircleAI.Memory"
    assert "Token economy tracking" in claude_mem.value_bullets


def test_apache_and_ccby_licenses_present() -> None:
    licenses = {c.license for c in ExternalCapabilityRegistry.All}
    assert "Apache-2.0" in licenses
    assert "CC-BY-4.0" in licenses
    assert "MIT" in licenses
