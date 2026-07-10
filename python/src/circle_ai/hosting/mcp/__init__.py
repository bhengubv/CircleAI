"""circle_ai.hosting.mcp — port of CircleAI.Hosting.Mcp.

MCP tool + resource-provider contracts and a transport-agnostic JSON-RPC 2.0
dispatcher (the HttpContext-free core of the C# McpEndpoints).
"""
from __future__ import annotations

from .contracts import (
    IMcpResourceProvider,
    IMcpTool,
    McpResource,
    McpResourceContent,
    McpToolException,
)
from .endpoints import McpDispatcher, McpServerInfo

__all__ = [
    "IMcpTool",
    "IMcpResourceProvider",
    "McpResource",
    "McpResourceContent",
    "McpToolException",
    "McpDispatcher",
    "McpServerInfo",
]
