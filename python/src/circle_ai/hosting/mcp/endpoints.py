"""McpEndpoints — port of CircleAI.Hosting.Mcp.McpEndpoints.

The C# file maps an ASP.NET Core JSON-RPC 2.0 endpoint (POST /mcp, GET
/mcp/manifest) and exposes a pure, HttpContext-free dispatcher
(``DispatchAsync``) that is "testable without a HttpContext". This port keeps
that pure dispatcher — :meth:`McpDispatcher.dispatch_async` — plus the batch
handling of POST /mcp via :meth:`McpDispatcher.handle_post_async`. Instead of a
DI ``IServiceProvider`` the dispatcher takes explicit collections of
:class:`IMcpTool` + :class:`IMcpResourceProvider` (the DI equivalent).

Every JSON-RPC response shape (result / error / tool-result / tool-error
envelopes, the ``id`` echoed back as its JSON-string form, the manifest, the
initialize capabilities) matches the C# byte-for-byte.
"""
from __future__ import annotations

import json as _json
from dataclasses import dataclass
from typing import Any, List, Optional, Sequence

from .contracts import IMcpResourceProvider, IMcpTool, McpResource, McpToolException

__all__ = ["McpServerInfo", "McpDispatcher"]

# JSON-RPC error codes (mirror the C# constants used inline).
_PARSE_ERROR = -32700
_INVALID_REQUEST = -32600
_METHOD_NOT_FOUND = -32601
_INTERNAL_ERROR = -32603
_INVALID_PARAMS = -32602


@dataclass(frozen=True, slots=True)
class McpServerInfo:
    """Server identity block. Mirrors ``McpEndpoints.McpServerInfo``."""

    name: str = "circleai-mcp"
    version: str = "3.2.0"
    description: str = "CircleAI MCP endpoint"


class McpDispatcher:
    """Pure MCP JSON-RPC 2.0 dispatcher. Holds the registered tools + resource
    providers (the DI-collection equivalent) and routes requests. Mirrors the
    static ``McpEndpoints`` dispatch surface.
    """

    __slots__ = ("_tools", "_resource_providers", "_info")

    def __init__(
        self,
        tools: Optional[Sequence[IMcpTool]] = None,
        resource_providers: Optional[Sequence[IMcpResourceProvider]] = None,
        info: Optional[McpServerInfo] = None,
    ) -> None:
        self._tools: List[IMcpTool] = list(tools) if tools else []
        self._resource_providers: List[IMcpResourceProvider] = (
            list(resource_providers) if resource_providers else []
        )
        self._info = info or McpServerInfo()

    # ── GET /mcp/manifest ──────────────────────────────────────────────────

    def manifest(self) -> dict:
        """Legacy manifest (GET /mcp/manifest). Mirrors the C# manifest body."""
        return {
            "name": self._info.name,
            "version": self._info.version,
            "description": self._info.description,
            "deprecated": True,
            "deprecationNotice": "Use POST /mcp with JSON-RPC 2.0 instead.",
            "tools": [
                {
                    "name": t.name,
                    "description": t.description,
                    "inputSchema": t.input_schema,
                }
                for t in self._tools
            ],
        }

    # ── POST /mcp (single or batch) ────────────────────────────────────────

    async def handle_post_async(self, raw_body: str, ct: object = None):
        """Handle a POST /mcp body (single request or batch). Returns the
        JSON-serialisable response object (or ``None`` for a notification-only
        request → HTTP 204). Mirrors the ``MapPost("/mcp", …)`` handler.
        """
        try:
            body = _json.loads(raw_body) if raw_body is not None and raw_body != "" else None
        except _json.JSONDecodeError:
            return _mcp_error_obj(None, _PARSE_ERROR, "Parse error")

        if body is None:
            return _mcp_error_obj(None, _INVALID_REQUEST, "Invalid Request")

        if isinstance(body, list):
            responses: List[Any] = []
            for item in body:
                r = await self.dispatch_async(item, ct)
                if r is not None:
                    responses.append(r)
            return responses

        return await self.dispatch_async(body, ct)

    # ── Pure dispatcher ────────────────────────────────────────────────────

    async def dispatch_async(self, req: Optional[dict], ct: object = None):
        """Pure JSON-RPC dispatch entry point — testable without any HTTP layer.
        Returns ``None`` for notifications. Mirrors ``DispatchAsync``.
        """
        if req is None or not isinstance(req, dict):
            return _mcp_error_obj(None, _INVALID_REQUEST, "Invalid Request")

        id_node = req.get("id")
        method = req.get("method") if req.get("jsonrpc") == "2.0" else None
        if method is None:
            return _mcp_error_obj(
                id_node, _INVALID_REQUEST, "Invalid Request: missing jsonrpc or method"
            )

        params = req.get("params")
        try:
            if method == "initialize":
                return self._handle_initialize(id_node)
            if method == "notifications/initialized":
                return None
            if method == "tools/list":
                return self._handle_tools_list(id_node)
            if method == "tools/call":
                return await self._handle_tools_call(id_node, params, ct)
            if method == "resources/list":
                return await self._handle_resources_list(id_node, ct)
            if method == "resources/read":
                return await self._handle_resources_read(id_node, params, ct)
            return _mcp_error_obj(id_node, _METHOD_NOT_FOUND, f"Method not found: {method}")
        except Exception as ex:  # noqa: BLE001 - internal-error envelope, per C#
            return _mcp_error_obj(id_node, _INTERNAL_ERROR, f"Internal error: {ex}")

    def _handle_initialize(self, id_node) -> dict:
        return _mcp_result(
            id_node,
            {
                "protocolVersion": "2024-11-05",
                "serverInfo": {"name": self._info.name, "version": self._info.version},
                "capabilities": {
                    "tools": {"listChanged": False},
                    "resources": {"listChanged": False, "subscribe": False},
                },
            },
        )

    def _handle_tools_list(self, id_node) -> dict:
        tools = [
            {
                "name": t.name,
                "description": t.description,
                "inputSchema": t.input_schema,
            }
            for t in self._tools
        ]
        return _mcp_result(id_node, {"tools": tools})

    async def _handle_tools_call(self, id_node, params, ct) -> dict:
        tool_name = params.get("name") if isinstance(params, dict) else None
        if not tool_name or not str(tool_name).strip():
            return _mcp_error_obj(id_node, _INVALID_PARAMS, "Invalid params: 'name' is required")

        tool = next((t for t in self._tools if t.name == tool_name), None)
        if tool is None:
            return _mcp_error_obj(id_node, _INVALID_PARAMS, f"Unknown tool: {tool_name}")

        args = params.get("arguments") if isinstance(params, dict) else None
        if not isinstance(args, dict):
            args = {}
        try:
            result = await tool.execute_async(args, ct)
            return _mcp_tool_result(id_node, result)
        except McpToolException as ex:
            return _mcp_tool_error(id_node, str(ex))

    async def _handle_resources_list(self, id_node, ct) -> dict:
        resources: List[McpResource] = []
        for p in self._resource_providers:
            page = await p.list_async(ct)
            resources.extend(page)
        return _mcp_result(
            id_node,
            {
                "resources": [
                    {
                        "uri": r.uri,
                        "name": r.name,
                        "description": r.description if r.description is not None else r.name,
                        "mimeType": r.mime_type,
                    }
                    for r in resources
                ]
            },
        )

    async def _handle_resources_read(self, id_node, params, ct) -> dict:
        uri = params.get("uri") if isinstance(params, dict) else None
        if not uri or not str(uri).strip():
            return _mcp_error_obj(id_node, _INVALID_PARAMS, "Invalid params: 'uri' is required")

        provider = next(
            (p for p in self._resource_providers if uri.lower().startswith(p.uri_scheme.lower())),
            None,
        )
        if provider is None:
            return _mcp_error_obj(id_node, _INVALID_PARAMS, f"No provider for URI scheme: {uri}")

        content = await provider.read_async(uri, ct)
        if content is None:
            return _mcp_error_obj(id_node, _INVALID_PARAMS, f"Resource not found: {uri}")

        return _mcp_result(
            id_node,
            {
                "contents": [
                    {"uri": content.uri, "mimeType": content.mime_type, "text": content.text}
                ]
            },
        )


# ── Response builders (mirror the C# helpers) ──────────────────────────────


def _id_to_json_string(id_node) -> Optional[str]:
    """Mirror the C# ``id?.ToJsonString()`` — serialise the id back to its JSON
    string form (numeric ``1`` → ``"1"``, string ``"a"`` → ``"\"a\""``). ``None``
    stays ``None``.
    """
    if id_node is None:
        return None
    return _json.dumps(id_node)


def _mcp_result(id_node, result) -> dict:
    return {"jsonrpc": "2.0", "id": _id_to_json_string(id_node), "result": result}


def _mcp_tool_result(id_node, data) -> dict:
    return _mcp_result(
        id_node,
        {
            "content": [{"type": "text", "text": _json.dumps(data)}],
            "isError": False,
        },
    )


def _mcp_tool_error(id_node, message: str) -> dict:
    return _mcp_result(
        id_node,
        {"content": [{"type": "text", "text": message}], "isError": True},
    )


def _mcp_error_obj(id_node, code: int, message: str) -> dict:
    return {
        "jsonrpc": "2.0",
        "id": _id_to_json_string(id_node),
        "error": {"code": code, "message": message},
    }
