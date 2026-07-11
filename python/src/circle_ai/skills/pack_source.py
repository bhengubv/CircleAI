# pack_source.py
#
# Port of CircleAI.Skills SkillPackSource.cs (C# — the EXACT spec).
#
# (2.0.2) Declarative description of a remote skill pack + the default
# catalogue KnownSkillPacks. C# record with default parameters maps to a frozen
# dataclass with field defaults (DefaultTags default None -> callers treat as
# empty).

from __future__ import annotations

from dataclasses import dataclass, field
from typing import List, Optional


@dataclass(frozen=True, slots=True)
class SkillPackSource:
    """Mirrors ``CircleAI.Skills.SkillPackSource`` — ``record(string Name,
    string RepoUrl, string GitRef = "main", string License = "unknown",
    string SkillSubdir = "", int EstimatedSkillCount = 0,
    bool IsDefaultEnabled = true, IReadOnlyList<string>? DefaultTags = null)``."""

    name: str
    repo_url: str
    git_ref: str = "main"
    license: str = "unknown"
    skill_subdir: str = ""
    estimated_skill_count: int = 0
    is_default_enabled: bool = True
    default_tags: Optional[List[str]] = None


class KnownSkillPacks:
    """(2.0.2) Default catalogue of skill packs CircleAI imports on first run.
    Mirrors the C# ``static class KnownSkillPacks`` static readonly fields."""

    AwesomeAgentSkills = SkillPackSource(
        name="awesome-agent-skills",
        repo_url="https://github.com/bhengubv/awesome-agent-skills",
        license="Apache-2.0",
        skill_subdir="skills",
        estimated_skill_count=1000,
        default_tags=["community"],
    )

    AnthropicCybersecurity = SkillPackSource(
        name="Anthropic-Cybersecurity-Skills",
        repo_url="https://github.com/mukul975/Anthropic-Cybersecurity-Skills",
        license="Apache-2.0",
        skill_subdir="skills",
        estimated_skill_count=754,
        default_tags=["security", "mitre"],
    )

    PrivacyDataProtection = SkillPackSource(
        name="Privacy-Data-Protection-Skills",
        repo_url="https://github.com/mukul975/Privacy-Data-Protection-Skills",
        license="Apache-2.0",
        skill_subdir="skills",
        estimated_skill_count=282,
        default_tags=["privacy", "compliance"],
    )

    ClaudeBugHunter = SkillPackSource(
        name="Claude-BugHunter",
        repo_url="https://github.com/bhengubv/Claude-BugHunter",
        license="Apache-2.0",
        skill_subdir="skills",
        estimated_skill_count=51,
        default_tags=["security", "bug-bounty"],
    )

    Last30Days = SkillPackSource(
        name="last30days-skill",
        repo_url="https://github.com/bhengubv/last30days-skill",
        license="MIT",
        estimated_skill_count=1,
        default_tags=["research"],
    )

    EdubaBrand = SkillPackSource(
        name="eduba-brand",
        repo_url="https://github.com/bhengubv/eduba-brand",
        license="n/a (pattern-port)",
        skill_subdir=".agents/skills/eduba-brand",
        estimated_skill_count=1,
        default_tags=["branding", "eduba"],
    )

    CareerOps = SkillPackSource(
        name="career-ops",
        repo_url="https://github.com/bhengubv/career-ops",
        license="MIT",
        estimated_skill_count=14,
        is_default_enabled=False,
        default_tags=["job-search", "career", "thejobcenter"],
    )

    BuildYourOwnX = SkillPackSource(
        name="build-your-own-x",
        repo_url="https://github.com/bhengubv/build-your-own-x",
        license="MIT",
        estimated_skill_count=0,
        is_default_enabled=False,
        default_tags=["education", "tutorial"],
    )

    All: List[SkillPackSource] = [
        AwesomeAgentSkills,
        AnthropicCybersecurity,
        PrivacyDataProtection,
        ClaudeBugHunter,
        Last30Days,
        EdubaBrand,
        CareerOps,
        BuildYourOwnX,
    ]
