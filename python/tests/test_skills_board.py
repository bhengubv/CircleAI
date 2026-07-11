"""test_skills_board.py — CircleAI.Skills port.

Covers the InMemorySkillStore (upsert with slug auto-gen, list/search ordered by
name, delete), the GenerateSlug regex chain, the KnownSkillPacks catalogue, the
SKILL.md pack loader/parser, and the SkillPackAutoImporter with an in-memory
downloader. C# is the exact spec.
"""
from __future__ import annotations

import pytest

from circle_ai.skills import (
    InMemoryPackDownloader,
    InMemorySkillStore,
    KnownSkillPacks,
    ParsedSkill,
    SkillDraft,
    SkillPackAutoImporter,
    SkillPackLoader,
    SkillPackManifest,
    SkillPackSource,
    SkillPackSourcesOptions,
    SkillSource,
)


def test_generate_slug():
    assert InMemorySkillStore.generate_slug("My Skill") == "my-skill"
    assert InMemorySkillStore.generate_slug("  Foo   Bar  ") == "foo-bar"
    assert InMemorySkillStore.generate_slug("A/B:C!") == "abc"
    # Non-sluggable -> 32-char hex fallback.
    fallback = InMemorySkillStore.generate_slug("!!!")
    assert len(fallback) == 32 and all(c in "0123456789abcdef" for c in fallback)


async def test_upsert_autogenerates_slug_and_stamps_source():
    store = InMemorySkillStore()
    detail = await store.upsert_async(None, SkillDraft("Calendar Summariser", "sums", "do it", ["cal"]))
    assert detail.id == "calendar-summariser"
    assert detail.source == SkillSource.InMemory
    assert detail.last_modified is not None
    # Explicit id is honoured (trimmed).
    d2 = await store.upsert_async("  custom-id  ", SkillDraft("X", "d", "i", []))
    assert d2.id == "custom-id"


async def test_list_and_search_ordered_by_name():
    store = InMemorySkillStore()
    await store.upsert_async("z", SkillDraft("Zeta", "d", "i", ["tag"]))
    await store.upsert_async("a", SkillDraft("alpha", "productivity helper", "i", ["cal"]))
    listed = await store.list_async()
    assert [s.name for s in listed] == ["alpha", "Zeta"]  # case-insensitive name order

    # Search matches name / description / tags, case-insensitive.
    assert [s.id for s in await store.search_async("PRODUCTIVITY")] == ["a"]
    assert [s.id for s in await store.search_async("cal")] == ["a"]
    assert await store.search_async("") == []


async def test_get_and_delete():
    store = InMemorySkillStore()
    await store.upsert_async("s", SkillDraft("S", "d", "instructions", []))
    got = await store.get_async("s")
    assert got is not None and got.instructions == "instructions"
    await store.delete_async("s")
    assert await store.get_async("s") is None
    with pytest.raises(ValueError):
        await store.get_async("  ")


def test_known_skill_packs_catalogue():
    assert len(KnownSkillPacks.All) == 8
    names = {p.name for p in KnownSkillPacks.All}
    assert "awesome-agent-skills" in names and "Claude-BugHunter" in names
    # Defaults.
    assert KnownSkillPacks.AwesomeAgentSkills.git_ref == "main"
    assert KnownSkillPacks.AwesomeAgentSkills.is_default_enabled is True
    assert KnownSkillPacks.CareerOps.is_default_enabled is False
    assert KnownSkillPacks.ClaudeBugHunter.estimated_skill_count == 51


def test_pack_loader_parse_frontmatter():
    content = (
        "---\n"
        "name: Bug Hunter\n"
        'description: "hunts bugs"\n'
        "tags: [security, bug-bounty]\n"
        "---\n"
        "# Heading\n\nBody instructions here.\n"
    )
    parsed = SkillPackLoader.parse(content, "packs/hunter/SKILL.md")
    assert parsed.id == "bug-hunter"
    assert parsed.name == "Bug Hunter"
    assert parsed.description == "hunts bugs"
    assert parsed.tags == ["security", "bug-bounty"]
    assert parsed.instructions.startswith("# Heading")


def test_pack_loader_parse_no_frontmatter_uses_heading():
    parsed = SkillPackLoader.parse("# My Title\n\nsome body", "x/SKILL.md")
    assert parsed.name == "My Title" and parsed.id == "my-title"


def test_pack_loader_parse_empty_raises():
    with pytest.raises(ValueError):
        SkillPackLoader.parse("", "x/SKILL.md")


def test_pack_loader_block_tags():
    content = "---\ntags:\n  - alpha\n  - beta\n---\n# H\nbody"
    parsed = SkillPackLoader.parse(content, "x/SKILL.md")
    assert parsed.tags == ["alpha", "beta"]


async def test_pack_loader_import_adds_pack_tag():
    store = InMemorySkillStore()
    pack = {
        "one/SKILL.md": "---\nname: One\ndescription: d1\n---\n# One\nbody",
        "two/SKILL.md": "---\nname: Two\ndescription: d2\n---\n# Two\nbody",
        "readme.md": "not a skill",  # ignored (wrong filename)
    }
    manifest = await SkillPackLoader.import_async(store, pack, "MyPack", "v1", "url", "MIT")
    assert isinstance(manifest, SkillPackManifest)
    assert manifest.skill_count == 2
    one = await store.get_async("one")
    assert one is not None and "pack:mypack" in one.tags


async def test_auto_importer_with_in_memory_downloader():
    store = InMemorySkillStore()
    downloader = InMemoryPackDownloader()
    # Pack content nested under the source's SkillSubdir ("skills").
    downloader.register(
        "Claude-BugHunter",
        {"skills/xss/SKILL.md": "---\nname: XSS\ndescription: cross-site\n---\n# XSS\nbody"},
    )
    opts = SkillPackSourcesOptions()
    opts.sources = [KnownSkillPacks.ClaudeBugHunter]
    importer = SkillPackAutoImporter(store, opts, downloader)
    manifests = await importer.import_enabled_async()
    assert len(manifests) == 1 and manifests[0].skill_count == 1
    assert (await store.get_async("xss")) is not None


async def test_auto_importer_missing_subdir_reports_error():
    store = InMemorySkillStore()
    downloader = InMemoryPackDownloader()
    downloader.register("Claude-BugHunter", {"wrong/SKILL.md": "---\nname: X\n---\n# X\nb"})
    opts = SkillPackSourcesOptions()
    opts.sources = [KnownSkillPacks.ClaudeBugHunter]  # SkillSubdir = "skills"
    errors: list = []
    importer = SkillPackAutoImporter(store, opts, downloader)
    manifests = await importer.import_enabled_async(on_error=lambda name, ex: errors.append(name))
    assert manifests == []  # nothing under skills/
    assert errors == ["Claude-BugHunter"]


def test_auto_importer_requires_store_and_options():
    with pytest.raises(ValueError):
        SkillPackAutoImporter(None, SkillPackSourcesOptions())  # type: ignore[arg-type]
    with pytest.raises(ValueError):
        SkillPackAutoImporter(InMemorySkillStore(), None)  # type: ignore[arg-type]
