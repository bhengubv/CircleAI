# tool_calling.py
#
# Port of CircleAI.Telephony ToolCalling.cs (C# — the EXACT spec).
#
# (3.3.0) Tool-calling for the voice loop. The AI emits a tool call during a
# turn; the orchestrator dispatches it to either a local handler or an HTTPS
# webhook and returns the result for the next turn.
#
# C# HttpClient -> the shared circle_ai.integration.http.IHttpFetcher (the tree's
# canonical injectable HTTP seam; real hosts wire a network fetcher, tests inject
# InMemoryHttpFetcher). C# ILogger -> stdlib logging. C# ConcurrentDictionary ->
# a plain dict guarded by a lock. C# delegate LocalToolHandler -> an async
# Callable. C# Uri -> str (absolute-URI guard preserved).

from __future__ import annotations

import json
import logging
import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from typing import Awaitable, Callable, Dict, List, Optional, Tuple
from urllib.parse import urlsplit

from ..integration.http import HttpRequest, IHttpFetcher

_logger = logging.getLogger("CircleAI.Telephony.ToolCalling")

# (3.3.0) In-process tool handler. C# ``delegate ValueTask<string>
# LocalToolHandler(string argumentsJson, CancellationToken ct)``.
LocalToolHandler = Callable[..., Awaitable[str]]


def _is_absolute_uri(uri: str) -> bool:
    parts = urlsplit(uri)
    return bool(parts.scheme) and bool(parts.netloc)


@dataclass(frozen=True, slots=True)
class ToolDefinition:
    """(3.3.0) Tool definition surfaced to the LLM.

    Mirrors ``record(string Name, string Description, string ArgumentsJsonSchema)``.
    """

    name: str
    description: str
    arguments_json_schema: str


@dataclass(frozen=True, slots=True)
class ToolInvocation:
    """(3.3.0) An invocation of one tool by the model.

    Mirrors ``record(string CallId, string ToolName, string ArgumentsJson)``.
    """

    call_id: str
    tool_name: str
    arguments_json: str


@dataclass(frozen=True, slots=True)
class ToolResult:
    """(3.3.0) Result of a tool invocation.

    Mirrors ``record(string CallId, bool Succeeded, string ResultJson, string? Error)``.
    """

    call_id: str
    succeeded: bool
    result_json: str
    error: Optional[str] = None


class IToolCallRegistry(ABC):
    """(3.3.0) Tool registry: register local handlers OR HTTPS webhook URLs
    against a tool name; the orchestrator dispatches."""

    @property
    @abstractmethod
    def definitions(self) -> List[ToolDefinition]:
        """All registered tool definitions."""

    @abstractmethod
    def register_local(self, definition: ToolDefinition, handler: LocalToolHandler) -> None:
        """Register a local handler for ``definition``."""

    @abstractmethod
    def register_webhook(self, definition: ToolDefinition, webhook: str) -> None:
        """Register a webhook URL; the orchestrator POSTs arguments JSON."""

    @abstractmethod
    async def invoke_async(self, invocation: ToolInvocation, *, ct: Optional[object] = None) -> ToolResult:
        """Invoke one tool call."""


class DefaultToolCallRegistry(IToolCallRegistry):
    """(3.3.0) Default in-memory registry. Thread-safe."""

    def __init__(self, http: IHttpFetcher, logger: Optional[logging.Logger] = None) -> None:
        if http is None:
            raise ValueError("http must not be None")
        self._http = http
        self._logger = logger if logger is not None else _logger
        self._lock = threading.Lock()
        # tool name (casefold) -> (definition, local handler | None, webhook | None)
        self._tools: Dict[str, Tuple[ToolDefinition, Optional[LocalToolHandler], Optional[str]]] = {}

    @property
    def definitions(self) -> List[ToolDefinition]:
        with self._lock:
            return [entry[0] for entry in self._tools.values()]

    def register_local(self, definition: ToolDefinition, handler: LocalToolHandler) -> None:
        if definition is None:
            raise ValueError("definition must not be None")
        if handler is None:
            raise ValueError("handler must not be None")
        if not definition.name or definition.name.isspace():
            raise ValueError("Tool name is required")
        with self._lock:
            self._tools[definition.name.casefold()] = (definition, handler, None)

    def register_webhook(self, definition: ToolDefinition, webhook: str) -> None:
        if definition is None:
            raise ValueError("definition must not be None")
        if webhook is None:
            raise ValueError("webhook must not be None")
        if not _is_absolute_uri(webhook):
            raise ValueError("Webhook URL must be absolute.")
        if not definition.name or definition.name.isspace():
            raise ValueError("Tool name is required")
        with self._lock:
            self._tools[definition.name.casefold()] = (definition, None, webhook)

    async def invoke_async(self, invocation: ToolInvocation, *, ct: Optional[object] = None) -> ToolResult:
        if invocation is None:
            raise ValueError("invocation must not be None")
        with self._lock:
            entry = self._tools.get(invocation.tool_name.casefold())
        if entry is None:
            return ToolResult(
                invocation.call_id, False, "{}", f"Tool '{invocation.tool_name}' is not registered."
            )

        _definition, local, webhook = entry
        try:
            if local is not None:
                result_json = await local(invocation.arguments_json, ct)
                return ToolResult(invocation.call_id, True, result_json if result_json else "{}")

            if webhook is not None:
                payload = {
                    "call_id": invocation.call_id,
                    "tool": invocation.tool_name,
                    "arguments": json.loads(invocation.arguments_json),
                }
                resp = await self._http.send_async(
                    HttpRequest(method="POST", url=webhook, body_json=payload)
                )
                if not resp.is_success:
                    error = resp.text
                    self._logger.warning(
                        "Tool webhook %s returned %s", invocation.tool_name, resp.status_code
                    )
                    return ToolResult(
                        invocation.call_id,
                        False,
                        "{}",
                        f"Webhook {resp.status_code}: {_truncate(error, 240)}",
                    )
                body = resp.text
                return ToolResult(invocation.call_id, True, "{}" if not body or body.isspace() else body)

            return ToolResult(
                invocation.call_id,
                False,
                "{}",
                f"Tool '{invocation.tool_name}' is registered without a local handler or webhook.",
            )
        except Exception as ex:
            self._logger.warning("Tool %s invocation failed: %s", invocation.tool_name, ex)
            return ToolResult(invocation.call_id, False, "{}", str(ex))


def _truncate(s: str, max_len: int) -> str:
    return s if len(s) <= max_len else s[:max_len] + "…"
