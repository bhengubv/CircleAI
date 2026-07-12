# paca_skills.py
#
# Port of CircleAI.Workflows PacaSkills.cs (C# — the EXACT spec).
#
# (3.3.0) Eleven built-in Claude Code skills ported from paca: paca, paca-epic,
# paca-breakdown, paca-clarify, paca-sprint, paca-estimate, paca-prioritize,
# paca-do, paca-test, paca-doc, paca-setup. Plus a skill installer that strips
# frontmatter and drops the markdown into ~/.claude/commands, and templates for
# the nine creator skills (epic / breakdown / clarify / sprint / estimate /
# prioritize / do / test / doc).

from __future__ import annotations

import os
import re
from dataclasses import dataclass
from typing import Iterable, List, Optional


@dataclass(frozen=True, slots=True)
class PacaSkill:
    """(3.3.0) A skill definition: frontmatter metadata + body."""

    name: str
    description: str
    body: str

    def to_markdown(self) -> str:
        """(3.3.0) Render as a Claude-Code-compatible markdown file with
        frontmatter."""
        return f"---\nname: {self.name}\ndescription: {self.description}\n---\n\n{self.body}"

    def to_body_only(self) -> str:
        """(3.3.0) Render as the bare body (frontmatter stripped) for the
        installer."""
        return self.body


class SkillTemplates:
    """(3.3.0) The nine creator-skill templates (markdown body)."""

    Epic = "You are running paca-epic. Use only the paca MCP tools. Output structure: title, problem statement, success criteria, scope, out-of-scope, risks."
    Breakdown = "You are running paca-breakdown. Use only the paca MCP tools. Take the supplied epic and produce a numbered list of tasks with title + acceptance criteria."
    Clarify = "You are running paca-clarify. Pose the smallest set of clarifying questions needed to estimate the supplied task."
    Sprint = "You are running paca-sprint. Use the create_sprint / start_sprint / complete_sprint MCP tools."
    Estimate = "You are running paca-estimate. For each task, propose story points (1-13). Cite assumptions."
    Prioritize = "You are running paca-prioritize. Reorder the backlog by importance (0-5). Cite reasoning."
    Do = "You are running paca-do. Pick the next-best ready task, mark in_progress, execute, then mark done."
    Test = "You are running paca-test. Write and run unit + integration tests for the current change."
    Doc = "You are running paca-doc. Update the living document with the smallest accurate diff."


class PacaSkillLibrary:
    """(3.3.0) The eleven built-in paca skills."""

    All: List[PacaSkill] = [
        PacaSkill("paca", "Run the paca workflow on the current ask.", "Use the paca MCP tools to plan and execute the user's request."),
        PacaSkill("paca-epic", "Capture a large initiative as a paca epic.", SkillTemplates.Epic),
        PacaSkill("paca-breakdown", "Break a paca epic into actionable tasks.", SkillTemplates.Breakdown),
        PacaSkill("paca-clarify", "Ask the right clarifying questions before estimating.", SkillTemplates.Clarify),
        PacaSkill("paca-sprint", "Form / close a sprint with the paca sprint surface.", SkillTemplates.Sprint),
        PacaSkill("paca-estimate", "Estimate story points for a set of tasks.", SkillTemplates.Estimate),
        PacaSkill("paca-prioritize", "Reorder the backlog by importance.", SkillTemplates.Prioritize),
        PacaSkill("paca-do", "Pick the next-best task and start it.", SkillTemplates.Do),
        PacaSkill("paca-test", "Generate and run tests for the current change.", SkillTemplates.Test),
        PacaSkill("paca-doc", "Update the project's living doc to reflect the latest change.", SkillTemplates.Doc),
        PacaSkill("paca-setup", "First-run setup: pick project, configure agents, install plugins.", "Walk the user through paca first-run setup."),
    ]

    @staticmethod
    def find(name: str) -> Optional[PacaSkill]:
        return next((s for s in PacaSkillLibrary.All if s.name.casefold() == name.casefold()), None)


# The C# regex is compiled with Singleline (DOTALL) so "." matches newlines.
_FRONTMATTER_PATTERN = re.compile(r"^\s*---.*?---\s*\n", re.DOTALL)


class PacaSkillInstaller:
    """(3.3.0) Installer that drops bare skill bodies into ~/.claude/commands/."""

    def __init__(self, commands_dir: str) -> None:
        if commands_dir is None or commands_dir.strip() == "":
            raise ValueError("commandsDir required")
        self._commands_dir = commands_dir

    def install_all(self) -> List[str]:
        """(3.3.0) Install all built-in skills."""
        return self.install_each(PacaSkillLibrary.All)

    def install_each(self, skills: Iterable[PacaSkill]) -> List[str]:
        """(3.3.0) Install a custom set of skills."""
        os.makedirs(self._commands_dir, exist_ok=True)
        installed: List[str] = []
        for skill in skills:
            path = os.path.join(self._commands_dir, f"{skill.name}.md")
            body = self.strip_frontmatter(skill.to_markdown())
            with open(path, "w", encoding="utf-8", newline="") as fh:
                fh.write(body)
            installed.append(path)
        return installed

    def uninstall_by_name(self, names: Iterable[str]) -> int:
        """(3.3.0) Uninstall a set of skills by name."""
        count = 0
        for name in names:
            path = os.path.join(self._commands_dir, f"{name}.md")
            if os.path.exists(path):
                os.remove(path)
                count += 1
        return count

    @staticmethod
    def strip_frontmatter(markdown: str) -> str:
        """(3.3.0) Strip the frontmatter block from a markdown skill file."""
        if markdown is None or markdown == "":
            return ""
        match = _FRONTMATTER_PATTERN.match(markdown)
        if match is None or match.start() != 0:
            return markdown.lstrip()
        return markdown[match.end() :].lstrip()
