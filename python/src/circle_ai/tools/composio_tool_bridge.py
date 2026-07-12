# composio_tool_bridge.py
#
# Port of CircleAI.Tools ComposioToolBridge.cs (C# — the EXACT spec).
#
# Routes B! tool calls to a Composio MCP server via JSON-RPC 2.0 over HTTP.
# Composio provides 250+ integrations (Gmail, Slack, GitHub, Calendar, etc.)
# through a single MCP endpoint. See: https://composio.dev/mcp
#
# The C# uses an injected HttpClient; the Python port injects the IHttpFetcher
# seam (circle_ai.integration.http) so the JSON-RPC + discovery logic is fully
# testable with InMemoryHttpFetcher and does no real network I/O.
#
# Porting notes:
#   * invoke_async sends a `tools/call` JSON-RPC 2.0 request to
#     `{server}/tools/{name}/invoke` and interprets the `{ "result", "error" }`
#     envelope into a ToolResult (result -> Ok; error.message -> Failure).
#   * get_available_tools_async does `GET {server}/tools` and maps each entry —
#     root array OR `{ "tools": [...] }` — to a ToolDefinition, reading
#     `inputSchema.properties` + `inputSchema.required`.
#   * The X-API-Key header is attached per request (mirrors
#     `TryAddWithoutValidation("X-API-Key", …)`); Accept is set to
#     application/json.
#   * Non-2xx responses return Failure with the JSON-RPC error message when
#     present, else "HTTP <code> <reason>".

from __future__ import annotations

from typing import Any, Dict, List, Optional

from ..integration.http import HttpRequest, IHttpFetcher
from .tool_types import IToolBridge, ToolDefinition, ToolInvocation, ToolParameter, ToolResult

_DEFAULT_SERVER_URI = "https://mcp.composio.dev/"


class ComposioToolBridge(IToolBridge):
    """Routes tool calls to a Composio MCP server via JSON-RPC 2.0 over HTTP.
    Mirrors ``CircleAI.Tools.ComposioToolBridge``.
    """

    def __init__(
        self,
        composio_api_key: str,
        http_fetcher: IHttpFetcher,
        server_uri: Optional[str] = None,
    ) -> None:
        if composio_api_key is None or composio_api_key.strip() == "":
            raise ValueError("composio_api_key must be non-empty")
        if http_fetcher is None:
            raise ValueError("http_fetcher must not be None")

        self._api_key = composio_api_key
        self._http = http_fetcher
        base = server_uri if server_uri else _DEFAULT_SERVER_URI
        self._server_uri = base if base.endswith("/") else base + "/"
        self._available_tools: List[ToolDefinition] = []

    @property
    def available_tools(self) -> List[ToolDefinition]:
        """Synchronous available-tools list. Empty by default; call
        :meth:`get_available_tools_async` to populate via the Composio API.
        """
        return self._available_tools

    async def invoke_async(
        self,
        invocation: ToolInvocation,
        *,
        ct: Optional[object] = None,
    ) -> ToolResult:
        if invocation is None:
            raise ValueError("invocation must not be None")
        if invocation.tool_name is None or invocation.tool_name.strip() == "":
            raise ValueError("tool_name must not be null or whitespace")

        request_body = {
            "jsonrpc": "2.0",
            "method": "tools/call",
            "id": 1,
            "params": {
                "name": invocation.tool_name,
                "arguments": invocation.arguments,
            },
        }

        from urllib.parse import quote

        endpoint = self._server_uri + f"tools/{quote(invocation.tool_name, safe='')}/invoke"

        try:
            request = HttpRequest(
                method="POST",
                url=endpoint,
                headers=self._headers(),
                body_json=request_body,
            )
            response = await self._http.send_async(request)
            body = self._parse_json(response)

            if not response.is_success:
                http_error = f"HTTP {response.status_code} {response.reason}".rstrip()
                return ToolResult.failure(
                    invocation.tool_name, self._extract_error(body, http_error)
                )

            # Standard JSON-RPC 2.0 response: { "result": ..., "error": ... }
            if isinstance(body, dict):
                err = body.get("error")
                if err is not None:
                    if isinstance(err, dict) and err.get("message") is not None:
                        return ToolResult.failure(invocation.tool_name, str(err["message"]))
                    return ToolResult.failure(invocation.tool_name, str(err))

                if "result" in body:
                    return ToolResult.ok(invocation.tool_name, body["result"])

            # No result / error — treat as success with null payload.
            return ToolResult.ok(invocation.tool_name)
        except Exception as ex:  # noqa: BLE001 — mirror the C# catch-all.
            return ToolResult.failure(invocation.tool_name, str(ex))

    async def get_available_tools_async(
        self, *, ct: Optional[object] = None
    ) -> List[ToolDefinition]:
        """Fetch the list of tools available on the Composio MCP server
        (``GET {server}/tools``) and cache it in :attr:`available_tools`.
        Returns an empty list on any failure (mirrors the C# fallbacks).
        """
        endpoint = self._server_uri + "tools"
        try:
            request = HttpRequest(method="GET", url=endpoint, headers=self._headers())
            response = await self._http.send_async(request)
            if not response.is_success:
                return []
            root = self._parse_json(response)
            tools = self._parse_tool_list(root)
            self._available_tools = tools
            return tools
        except Exception:  # noqa: BLE001 — mirror the C# catch -> empty list.
            return []

    # ── Internal helpers ────────────────────────────────────────────────────

    def _headers(self) -> Dict[str, str]:
        return {"X-API-Key": self._api_key, "Accept": "application/json"}

    @staticmethod
    def _parse_json(response: object) -> Any:
        text = getattr(response, "text", "") or ""
        if text == "":
            return None
        import json as _json

        try:
            return _json.loads(text)
        except (ValueError, TypeError):
            return None

    @staticmethod
    def _parse_tool_list(root: Any) -> List[ToolDefinition]:
        # Composio may return an array at root, or { "tools": [...] }.
        if isinstance(root, list):
            tools_array = root
        elif isinstance(root, dict) and isinstance(root.get("tools"), list):
            tools_array = root["tools"]
        else:
            return []

        result: List[ToolDefinition] = []
        for item in tools_array:
            if not isinstance(item, dict):
                continue
            name = item.get("name")
            desc = item.get("description", "")

            if name is None or str(name).strip() == "":
                continue

            parameters: Dict[str, ToolParameter] = {}
            required: List[str] = []

            schema = item.get("inputSchema")
            if isinstance(schema, dict):
                props = schema.get("properties")
                if isinstance(props, dict):
                    for prop_name, prop_val in props.items():
                        if isinstance(prop_val, dict):
                            ptype = prop_val.get("type") or "string"
                            pdesc = prop_val.get("description") or ""
                        else:
                            ptype, pdesc = "string", ""
                        parameters[prop_name] = ToolParameter(type=str(ptype), description=str(pdesc))

                    req = schema.get("required")
                    if isinstance(req, list):
                        for r in req:
                            if r is not None and str(r).strip() != "":
                                required.append(str(r))

            result.append(
                ToolDefinition(
                    name=str(name),
                    description=str(desc) if desc is not None else "",
                    parameters=parameters,
                    required_parameters=required,
                )
            )

        return result

    @staticmethod
    def _extract_error(body: Any, fallback: str) -> str:
        if isinstance(body, dict):
            err = body.get("error")
            if isinstance(err, dict):
                if err.get("message") is not None:
                    return str(err["message"])
                return str(err)
            if err is not None:
                return str(err)
        return fallback
