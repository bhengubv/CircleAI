"""test_accessibility_board.py — CircleAI.Accessibility port.

Covers AccessibilityNeed, InMemoryAccessibilityBoard (profile set/get, derived
adaptation hints in the exact C# order incl. text-scale F2 formatting and
PascalCase need names) and AccessibilityDomainContext. C# is the exact spec.
"""
from __future__ import annotations

from circle_ai import (
    AccessibilityDomainContext,
    AccessibilityNeed,
    AdaptationHint,
    IAccessibilityBoard,
    InMemoryAccessibilityBoard,
    UserAccessibilityProfile,
)


def test_board_is_iaccessibilityboard():
    assert isinstance(InMemoryAccessibilityBoard(), IAccessibilityBoard)


def test_hints_for_unknown_profile_empty():
    assert InMemoryAccessibilityBoard().hints_for("nobody") == []


def test_hints_full_order_and_formatting():
    b = InMemoryAccessibilityBoard()
    b.set_profile(
        UserAccessibilityProfile(
            "u1",
            [AccessibilityNeed.VISUAL, AccessibilityNeed.MOTOR],
            text_scale=1.5,
            high_contrast=True,
            reduced_motion=True,
            screen_reader=True,
        )
    )
    hints = b.hints_for("u1")
    assert hints == [
        AdaptationHint("contrast", "high"),
        AdaptationHint("motion", "reduced"),
        AdaptationHint("aria", "verbose"),
        AdaptationHint("text-scale", "1.50"),
        AdaptationHint("need", "Visual"),
        AdaptationHint("need", "Motor"),
    ]


def test_hints_text_scale_not_emitted_when_one():
    b = InMemoryAccessibilityBoard()
    b.set_profile(
        UserAccessibilityProfile("u1", [], 1.0, False, False, False)
    )
    assert b.hints_for("u1") == []


def test_need_cs_names():
    assert AccessibilityNeed.COGNITIVE.cs_name == "Cognitive"
    assert AccessibilityNeed.SPEECH.cs_name == "Speech"


def test_accessibility_domain_context():
    assert AccessibilityDomainContext.SystemPromptSnippet.startswith("[DOMAIN: Accessibility]")
    assert "WCAG_2_2" in AccessibilityDomainContext.ComplianceFlags
