# paca_mcp.py
#
# Port of CircleAI.Workflows PacaMcp.cs (C# — the EXACT spec).
#
# (3.3.0) MCP server for paca workflows. Tools surface = create_task,
# list_tasks, edit_task, add_comment, create_doc, link_doc_to_task, and any
# plugin-registered MCP tools. Three transports: stdio, SSE, HTTP. Per-agent MCP
# server config so each agent has its own toolset.
#
# The C# PacaMcpHandler delegate (ValueTask<string>(string, CancellationToken))
# maps to an async callable. tool registration + per-agent enabled-tool gating
# are ported verbatim; error responses use the same {"error":{"message":...}}
# JSON shape and tools/list uses {"tools":[{name,description,inputSchema}]} with
# inputSchema parsed from each tool's schema string.

from __future__ import annotations

import json
import threading
from dataclasses import dataclass
from enum import IntEnum
from typing import Awaitable, Callable, Dict, List, Optional, Tuple


class McpTransportKind(IntEnum):
    """(3.3.0) MCP transport types."""

    Stdio = 0
    ServerSentEvents = 1
    Http = 2


@dataclass(frozen=True, slots=True)
class AgentMcpConfig:
    """(3.3.0) Per-agent MCP server config."""

    agent_member_id: str
    transports: List[McpTransportKind]
    enabled_tools: List[str]
    tool_settings: Dict[str, str]


@dataclass(frozen=True, slots=True)
class PacaMcpTool:
    """(3.3.0) MCP tool descriptor."""

    name: str
    description: str
    input_schema: str


# (3.3.0) MCP tool handler signature: (arguments_json, ct) -> awaitable[str].
PacaMcpHandler = Callable[[str, Optional[object]], Awaitable[str]]


class PacaMcpServer:
    """(3.3.0) Paca's MCP server: registers built-in workflow tools + plugin
    tools."""

    def __init__(self) -> None:
        self._tools: Dict[str, Tuple[PacaMcpTool, PacaMcpHandler]] = {}
        self._agent_configs: Dict[str, AgentMcpConfig] = {}
        self._lock = threading.Lock()

    @property
    def tools(self) -> List[PacaMcpTool]:
        with self._lock:
            return [t for (t, _h) in self._tools.values()]

    def register_tool(self, tool: PacaMcpTool, handler: PacaMcpHandler) -> None:
        if tool is None:
            raise ValueError("tool must not be None")
        if handler is None:
            raise ValueError("handler must not be None")
        with self._lock:
            self._tools[tool.name.casefold()] = (tool, handler)

    def configure_agent(self, config: AgentMcpConfig) -> None:
        """(3.3.0) Configure a per-agent toolset."""
        if config is None:
            raise ValueError("config must not be None")
        with self._lock:
            self._agent_configs[config.agent_member_id] = config

    def get_agent_config(self, agent_member_id: str) -> Optional[AgentMcpConfig]:
        with self._lock:
            return self._agent_configs.get(agent_member_id)

    async def invoke_async(
        self,
        agent_member_id: str,
        tool_name: str,
        arguments_json: str,
        ct: Optional[object] = None,
    ) -> str:
        """(3.3.0) Invoke a tool for a specific agent — enforces the agent's
        enabled-tool list."""
        with self._lock:
            entry = self._tools.get(tool_name.casefold())
            cfg = self._agent_configs.get(agent_member_id)
        if entry is None:
            return self._wrap_error(f"Unknown tool '{tool_name}'.")
        if cfg is not None:
            if len(cfg.enabled_tools) > 0 and not any(
                t.casefold() == tool_name.casefold() for t in cfg.enabled_tools
            ):
                return self._wrap_error(
                    f"Tool '{tool_name}' is not enabled for agent '{agent_member_id}'."
                )
        try:
            handler = entry[1]
            return await handler(arguments_json, ct)
        except Exception as ex:  # noqa: BLE001 — mirror C# catch (Exception ex)
            return self._wrap_error(str(ex))

    def tools_list_json(self) -> str:
        """(3.3.0) JSON-RPC tools/list response payload."""
        with self._lock:
            entries = [t for (t, _h) in self._tools.values()]
        tools = [
            {
                "name": t.name,
                "description": t.description,
                "inputSchema": json.loads(t.input_schema),
            }
            for t in entries
        ]
        return json.dumps({"tools": tools})

    @staticmethod
    def _wrap_error(message: str) -> str:
        return json.dumps({"error": {"message": message}})


class PacaCoreMcpTools:
    """(3.3.0) Built-in workflow tools."""

    CreateTask = PacaMcpTool(
        name="create_task",
        description="Create a new task in a project.",
        input_schema='{"type":"object","properties":{"project_id":{"type":"string"},"title":{"type":"string"},"description":{"type":"string"}},"required":["project_id","title"]}',
    )

    ListTasks = PacaMcpTool(
        name="list_tasks",
        description="List live tasks in a project.",
        input_schema='{"type":"object","properties":{"project_id":{"type":"string"}},"required":["project_id"]}',
    )

    EditTask = PacaMcpTool(
        name="edit_task",
        description="Edit a task (title, description, status).",
        input_schema='{"type":"object","properties":{"project_id":{"type":"string"},"number":{"type":"integer"},"title":{"type":"string"},"description":{"type":"string"},"status":{"type":"string"}},"required":["project_id","number"]}',
    )

    CreateDoc = PacaMcpTool(
        name="create_doc",
        description="Create a doc in the project's doc tree.",
        input_schema='{"type":"object","properties":{"project_id":{"type":"string"},"title":{"type":"string"},"parent_id":{"type":"string","nullable":true},"content_json":{"type":"string"}},"required":["project_id","title","content_json"]}',
    )

    LinkDocToTask = PacaMcpTool(
        name="link_doc_to_task",
        description="Link a doc section to a task.",
        input_schema='{"type":"object","properties":{"doc_id":{"type":"string"},"section_anchor":{"type":"string"},"project_id":{"type":"string"},"task_number":{"type":"integer"}},"required":["doc_id","section_anchor","project_id","task_number"]}',
    )
