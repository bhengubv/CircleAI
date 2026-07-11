# mcp_tool_importer.py
#
# Port of CircleAI.Telephony McpToolImporter.cs (C# — the EXACT spec).
#
# (3.3.0) Pull tool definitions from an MCP (Model Context Protocol) server at
# call start. Each remote tool registers into the local IToolCallRegistry as a
# webhook-style tool that forwards calls back to the MCP server.
#
# C# HttpClient -> the shared circle_ai.integration.http.IHttpFetcher. C#
# ILogger -> stdlib logging. JSON handling mirrors the C# JsonDocument reads:
# result.tools[] -> name/description/inputSchema, skipping entries without a
# name. inputSchema.GetRawText() (the raw JSON text) -> json.dumps of the parsed
# sub-object. AppendQuery is reproduced with urllib so the forwarding URL matches
# byte-for-byte (query key "remote_tool", value URL-escaped).

from __future__ import annotations

import json
import logging
from abc import ABC, abstractmethod
from dataclasses import dataclass
from typing import List, Optional
from urllib.parse import quote, urlsplit, urlunsplit

from ..integration.http import HttpRequest, IHttpFetcher
from .tool_calling import IToolCallRegistry, ToolDefinition

_logger = logging.getLogger("CircleAI.Telephony.McpToolImporter")


@dataclass(frozen=True, slots=True)
class McpToolDescriptor:
    """(3.3.0) Description of one MCP tool returned from ``tools/list``.

    Mirrors ``record(string Name, string Description, string InputJsonSchema)``.
    """

    name: str
    description: str
    input_json_schema: str


@dataclass(frozen=True, slots=True)
class McpServerConfig:
    """(3.3.0) MCP server descriptor.

    ``server_endpoint``: HTTP endpoint of the MCP server.
    ``authorization_header``: optional ``Authorization`` header (e.g. ``Bearer ...``).
    ``tool_name_prefix``: optional prefix applied to imported tool names to avoid
    collisions.
    """

    server_endpoint: str
    authorization_header: Optional[str] = None
    tool_name_prefix: Optional[str] = None


class IMcpToolImporter(ABC):
    """(3.3.0) Imports tools from MCP servers into a tool registry."""

    @abstractmethod
    async def import_async(
        self,
        registry: IToolCallRegistry,
        server: McpServerConfig,
        *,
        ct: Optional[object] = None,
    ) -> List[ToolDefinition]:
        ...


class HttpMcpToolImporter(IMcpToolImporter):
    """(3.3.0) HTTP-backed importer (tools list + invoke via JSON-RPC over HTTP)."""

    def __init__(self, http: IHttpFetcher, logger: Optional[logging.Logger] = None) -> None:
        if http is None:
            raise ValueError("http must not be None")
        self._http = http
        self._logger = logger if logger is not None else _logger

    async def import_async(
        self,
        registry: IToolCallRegistry,
        server: McpServerConfig,
        *,
        ct: Optional[object] = None,
    ) -> List[ToolDefinition]:
        if registry is None:
            raise ValueError("registry must not be None")
        if server is None:
            raise ValueError("server must not be None")

        list_request = {"jsonrpc": "2.0", "id": 1, "method": "tools/list", "params": {}}
        headers = {}
        if server.authorization_header and not server.authorization_header.isspace():
            headers["Authorization"] = server.authorization_header

        resp = await self._http.send_async(
            HttpRequest(
                method="POST",
                url=server.server_endpoint,
                headers=headers,
                body_json=list_request,
            )
        )
        if not resp.is_success:
            self._logger.warning(
                "MCP server %s returned %s", server.server_endpoint, resp.status_code
            )
            return []

        doc = resp.json()
        result = doc.get("result") if isinstance(doc, dict) else None
        if not isinstance(result, dict):
            return []
        tools = result.get("tools")
        if not isinstance(tools, list):
            return []

        imported: List[ToolDefinition] = []
        for entry in tools:
            if not isinstance(entry, dict):
                continue
            name = entry.get("name")
            description = entry.get("description") or ""
            input_schema = entry.get("inputSchema")
            schema = json.dumps(input_schema) if input_schema is not None else "{}"
            if not name or (isinstance(name, str) and name.isspace()):
                continue

            local_name = (
                name
                if not server.tool_name_prefix or server.tool_name_prefix.isspace()
                else f"{server.tool_name_prefix}{name}"
            )
            definition = ToolDefinition(local_name, description, schema)

            # Register a webhook-style entry whose invocation forwards back to the
            # MCP server's tools/call method.
            invoke_url = _append_query(server.server_endpoint, "remote_tool", name)
            registry.register_webhook(definition, invoke_url)
            imported.append(definition)

        return imported


def _append_query(base_uri: str, key: str, value: str) -> str:
    """C# ``UriBuilder`` + query append, preserving any existing query string and
    URL-escaping the value (``Uri.EscapeDataString``)."""
    parts = urlsplit(base_uri)
    existing = parts.query
    separator = "" if not existing else "&"
    new_query = existing + separator + key + "=" + quote(value, safe="")
    return urlunsplit((parts.scheme, parts.netloc, parts.path, new_query, parts.fragment))
