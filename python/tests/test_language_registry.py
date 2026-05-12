# test_language_registry.py
#
# Validates KnownLanguages and DefaultLanguageRegistry against the 20 entries
# in fixtures/language_tags.json.

from __future__ import annotations

import json
import pathlib
import sys

import pytest

sys.path.insert(0, str(pathlib.Path(__file__).parent.parent / "src"))

from circle_ai.languages import KnownLanguages, DefaultLanguageRegistry, WritingSystem


FIXTURES_DIR = pathlib.Path(__file__).parent.parent.parent / "fixtures"


def _load_fixture() -> dict:
    with open(FIXTURES_DIR / "language_tags.json", encoding="utf-8") as f:
        return json.load(f)


FIXTURE = _load_fixture()
FIXTURE_LANGS = FIXTURE["languages"]


# ---------------------------------------------------------------------------
# Count
# ---------------------------------------------------------------------------

def test_total_count() -> None:
    """KnownLanguages.ALL must have exactly 20 entries."""
    assert len(KnownLanguages.ALL) == FIXTURE["assertions"]["totalCount"] == 20


# ---------------------------------------------------------------------------
# Per-entry field checks (parametrised)
# ---------------------------------------------------------------------------

@pytest.mark.parametrize("entry", FIXTURE_LANGS, ids=[e["bcpTag"] for e in FIXTURE_LANGS])
def test_language_entry_fields(entry: dict) -> None:
    registry = DefaultLanguageRegistry()
    tag = registry.get_by_bcp_tag(entry["bcpTag"])

    assert tag is not None, f"BCP tag not found: {entry['bcpTag']!r}"

    assert tag.bcp_tag      == entry["bcpTag"],       f"bcpTag mismatch for {entry['bcpTag']}"
    assert tag.display_name == entry["englishName"],  f"englishName mismatch for {entry['bcpTag']}"
    assert tag.native_name  == entry["nativeName"],   f"nativeName mismatch for {entry['bcpTag']}"
    assert tag.script       == WritingSystem(entry["writingSystem"]), \
        f"writingSystem mismatch for {entry['bcpTag']}"
    assert tag.is_rtl       == entry["isRtl"],        f"isRtl mismatch for {entry['bcpTag']}"
    assert tag.iso_region   == entry["primaryRegion"],f"primaryRegion mismatch for {entry['bcpTag']}"


# ---------------------------------------------------------------------------
# Declaration order
# ---------------------------------------------------------------------------

def test_declaration_order() -> None:
    """KnownLanguages.ALL must be in the same order as the fixture."""
    fixture_tags = [e["bcpTag"] for e in FIXTURE_LANGS]
    actual_tags  = [lang.bcp_tag for lang in KnownLanguages.ALL]
    assert actual_tags == fixture_tags, "KnownLanguages.ALL order does not match fixture"


# ---------------------------------------------------------------------------
# RTL languages
# ---------------------------------------------------------------------------

def test_rtl_languages() -> None:
    """Only Arabic (ar) must be RTL."""
    rtl = [lang.bcp_tag for lang in KnownLanguages.ALL if lang.is_rtl]
    assert rtl == FIXTURE["assertions"]["rtlLanguages"]


# ---------------------------------------------------------------------------
# Registry: is_supported
# ---------------------------------------------------------------------------

def test_is_supported() -> None:
    registry = DefaultLanguageRegistry()
    for entry in FIXTURE_LANGS:
        assert registry.is_supported(entry["bcpTag"]), \
            f"is_supported returned False for {entry['bcpTag']!r}"
    assert not registry.is_supported("xx")


# ---------------------------------------------------------------------------
# Registry: get_all returns 20
# ---------------------------------------------------------------------------

def test_registry_get_all() -> None:
    registry = DefaultLanguageRegistry()
    assert len(registry.get_all()) == 20


# ---------------------------------------------------------------------------
# Registry: get_for_region
# ---------------------------------------------------------------------------

def test_get_for_region_za() -> None:
    """ZA must have isiZulu, Sesotho, Afrikaans, isiXhosa, Sepedi, Setswana (6 entries)."""
    registry = DefaultLanguageRegistry()
    za_langs = registry.get_for_region("ZA")
    za_tags  = {lang.bcp_tag for lang in za_langs}
    assert za_tags == {"zu", "st", "af", "xh", "nso", "tn"}
