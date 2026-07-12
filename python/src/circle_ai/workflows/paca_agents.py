# paca_agents.py
#
# Port of CircleAI.Workflows PacaAgents.cs (C# — the EXACT spec).
#
# (3.3.0) AI agents as first-class project members (paca port). One table for
# humans + agents — they both have an identity, handle, role, avatar. Agents
# add: LLM config, system prompts (task/doc/chat), capability flags, iteration
# limits + timeout, git identity. Five preset templates ship out of the box.
#
# Records → frozen dataclasses; enums → IntEnum (declaration order == ordinal);
# TimeSpan → datetime.timedelta; Uri? → Optional[str] (URI kept as a string to
# stay self-contained). record-with → dataclasses.replace.

from __future__ import annotations

import threading
from dataclasses import dataclass, replace
from datetime import datetime, timedelta, timezone
from enum import IntEnum
from typing import Callable, Dict, List, Optional


class MemberKind(IntEnum):
    """(3.3.0) Member kind."""

    Human = 0
    Agent = 1


@dataclass(frozen=True, slots=True)
class ProjectMember:
    """(3.3.0) Shared identity for humans + agents in a project.

    ``handle`` is "@sipho" or "@billing-agent"; ``role`` is "owner" /
    "developer" / "agent" / etc."""

    id: str
    project_id: str
    kind: MemberKind
    display_name: str
    handle: str
    role: str
    avatar_url: Optional[str]
    created_at_utc: datetime
    deleted_at_utc: Optional[datetime]


@dataclass(frozen=True, slots=True)
class AgentLlmConfig:
    """(3.3.0) Per-agent LLM config."""

    provider: str
    model: str
    api_key: Optional[str]
    base_address: Optional[str]


@dataclass(frozen=True, slots=True)
class AgentSystemPrompts:
    """(3.3.0) Per-agent context-specific system prompts."""

    task_prompt: Optional[str]
    doc_prompt: Optional[str]
    chat_prompt: Optional[str]


@dataclass(frozen=True, slots=True)
class AgentCapabilities:
    """(3.3.0) Capability flags an agent is permitted to do."""

    can_clone_repos: bool
    can_create_prs: bool
    can_write_files: bool
    can_call_external_tools: bool


@dataclass(frozen=True, slots=True)
class AgentLimits:
    """(3.3.0) Runtime limits an agent must respect."""

    max_iterations: int
    timeout: timedelta


@dataclass(frozen=True, slots=True)
class AgentGitIdentity:
    """(3.3.0) Git identity an agent uses when committing."""

    name: str
    email: str


@dataclass(frozen=True, slots=True)
class AgentTriggers:
    """(3.3.0) Trigger keywords that wake the agent for each event class."""

    task_created: Optional[str]
    chat_mention: Optional[str]
    doc_edit: Optional[str]
    direct_mention: Optional[str]


@dataclass(frozen=True, slots=True)
class AgentProfile:
    """(3.3.0) Full agent profile."""

    member_id: str
    llm: AgentLlmConfig
    prompts: AgentSystemPrompts
    capabilities: AgentCapabilities
    limits: AgentLimits
    git_identity: AgentGitIdentity
    triggers: AgentTriggers


class AgentTemplates:
    """(3.3.0) Five preset agent templates from paca."""

    @staticmethod
    def development_agent(member_id: str, api_key: str, base_address: Optional[str] = None) -> AgentProfile:
        return AgentProfile(
            member_id=member_id,
            llm=AgentLlmConfig("openai", "gpt-4o-mini", api_key, base_address),
            prompts=AgentSystemPrompts(
                task_prompt="You are a senior developer. Implement requested changes, write tests, open PRs.",
                doc_prompt="You write engineering docs that are precise and example-driven.",
                chat_prompt="You answer engineering questions with concrete code samples.",
            ),
            capabilities=AgentCapabilities(
                can_clone_repos=True, can_create_prs=True, can_write_files=True, can_call_external_tools=True
            ),
            limits=AgentLimits(max_iterations=25, timeout=timedelta(minutes=10)),
            git_identity=AgentGitIdentity("CircleAI Dev Agent", "dev-agent@circleai.local"),
            triggers=AgentTriggers("dev", "@dev", None, "dev"),
        )

    @staticmethod
    def product_manager_agent(member_id: str, api_key: str) -> AgentProfile:
        return AgentProfile(
            member_id=member_id,
            llm=AgentLlmConfig("openai", "gpt-4o-mini", api_key, None),
            prompts=AgentSystemPrompts(
                task_prompt="You are a product manager. Triage tasks, break them down, assign owners.",
                doc_prompt="You write product specs and PRDs.",
                chat_prompt="You answer product/priority questions.",
            ),
            capabilities=AgentCapabilities(
                can_clone_repos=False, can_create_prs=False, can_write_files=True, can_call_external_tools=True
            ),
            limits=AgentLimits(max_iterations=15, timeout=timedelta(minutes=5)),
            git_identity=AgentGitIdentity("CircleAI PM Agent", "pm-agent@circleai.local"),
            triggers=AgentTriggers("pm", "@pm", "@pm", "pm"),
        )

    @staticmethod
    def designer_agent(member_id: str, api_key: str) -> AgentProfile:
        return AgentProfile(
            member_id=member_id,
            llm=AgentLlmConfig("openai", "gpt-4o-mini", api_key, None),
            prompts=AgentSystemPrompts(
                task_prompt="You are a designer. Sketch UI ideas, write copy, propose flows.",
                doc_prompt="You write design memos.",
                chat_prompt="You answer design questions and propose concepts.",
            ),
            capabilities=AgentCapabilities(
                can_clone_repos=False, can_create_prs=False, can_write_files=True, can_call_external_tools=False
            ),
            limits=AgentLimits(max_iterations=10, timeout=timedelta(minutes=5)),
            git_identity=AgentGitIdentity("CircleAI Design Agent", "design-agent@circleai.local"),
            triggers=AgentTriggers("design", "@design", "@design", "design"),
        )

    @staticmethod
    def qa_agent(member_id: str, api_key: str) -> AgentProfile:
        return AgentProfile(
            member_id=member_id,
            llm=AgentLlmConfig("openai", "gpt-4o-mini", api_key, None),
            prompts=AgentSystemPrompts(
                task_prompt="You are a QA engineer. Write test plans, generate test cases, validate against AC.",
                doc_prompt="You write QA reports.",
                chat_prompt="You answer QA questions and propose test strategies.",
            ),
            capabilities=AgentCapabilities(
                can_clone_repos=True, can_create_prs=False, can_write_files=True, can_call_external_tools=True
            ),
            limits=AgentLimits(max_iterations=20, timeout=timedelta(minutes=7)),
            git_identity=AgentGitIdentity("CircleAI QA Agent", "qa-agent@circleai.local"),
            triggers=AgentTriggers("qa", "@qa", None, "qa"),
        )

    @staticmethod
    def code_reviewer_agent(member_id: str, api_key: str) -> AgentProfile:
        return AgentProfile(
            member_id=member_id,
            llm=AgentLlmConfig("openai", "gpt-4o-mini", api_key, None),
            prompts=AgentSystemPrompts(
                task_prompt="You are a senior code reviewer. Comment for clarity, correctness, security.",
                doc_prompt="You write code review checklists.",
                chat_prompt="You answer questions about code patterns and best practices.",
            ),
            capabilities=AgentCapabilities(
                can_clone_repos=True, can_create_prs=False, can_write_files=False, can_call_external_tools=True
            ),
            limits=AgentLimits(max_iterations=15, timeout=timedelta(minutes=7)),
            git_identity=AgentGitIdentity("CircleAI Reviewer Agent", "reviewer-agent@circleai.local"),
            triggers=AgentTriggers(None, "@review", None, "review"),
        )

    PresetNames: List[str] = ["development", "pm", "design", "qa", "review"]


class InMemoryPacaMemberStore:
    """(3.3.0) In-memory store for members + agent profiles."""

    def __init__(self, clock: Optional[Callable[[], datetime]] = None) -> None:
        self._clock = clock if clock is not None else (lambda: datetime.now(timezone.utc))
        self._members: Dict[str, ProjectMember] = {}
        self._profiles: Dict[str, AgentProfile] = {}
        self._lock = threading.Lock()

    def add_human(
        self,
        id: str,
        project_id: str,
        display_name: str,
        handle: str,
        role: str = "developer",
        avatar: Optional[str] = None,
    ) -> ProjectMember:
        return self._add_member(id, project_id, MemberKind.Human, display_name, handle, role, avatar)

    def add_agent(
        self,
        id: str,
        project_id: str,
        display_name: str,
        handle: str,
        profile: AgentProfile,
        avatar: Optional[str] = None,
    ) -> ProjectMember:
        member = self._add_member(id, project_id, MemberKind.Agent, display_name, handle, "agent", avatar)
        with self._lock:
            self._profiles[id] = replace(profile, member_id=id)
        return member

    def _add_member(
        self,
        id: str,
        project_id: str,
        kind: MemberKind,
        display_name: str,
        handle: str,
        role: str,
        avatar: Optional[str],
    ) -> ProjectMember:
        if id is None or id.strip() == "":
            raise ValueError("id required")
        if project_id is None or project_id.strip() == "":
            raise ValueError("projectId required")
        if display_name is None or display_name.strip() == "":
            raise ValueError("displayName required")
        if handle is None or handle.strip() == "":
            raise ValueError("handle required")

        member = ProjectMember(id, project_id, kind, display_name, handle, role, avatar, self._clock(), None)
        with self._lock:
            if id in self._members:
                raise RuntimeError(f"Member '{id}' already exists.")
            self._members[id] = member
        return member

    def get_member(self, id: str) -> Optional[ProjectMember]:
        with self._lock:
            m = self._members.get(id)
            return m if (m is not None and m.deleted_at_utc is None) else None

    def get_agent_profile(self, member_id: str) -> Optional[AgentProfile]:
        with self._lock:
            return self._profiles.get(member_id)

    def list_members(self, project_id: str, kind: Optional[MemberKind] = None) -> List[ProjectMember]:
        with self._lock:
            members = [
                m
                for m in self._members.values()
                if m.project_id == project_id
                and m.deleted_at_utc is None
                and (kind is None or m.kind == kind)
            ]
        return sorted(members, key=lambda m: m.display_name)

    def remove_member(self, id: str) -> None:
        with self._lock:
            existing = self._members.get(id)
            if existing is None or existing.deleted_at_utc is not None:
                return
            self._members[id] = replace(existing, deleted_at_utc=self._clock())

    def update_agent_profile(self, member_id: str, updated: AgentProfile) -> AgentProfile:
        member = self.get_member(member_id)
        if member is None or member.kind != MemberKind.Agent:
            raise RuntimeError(f"Member '{member_id}' is not an agent.")
        with self._lock:
            self._profiles[member_id] = replace(updated, member_id=member_id)
            return self._profiles[member_id]
