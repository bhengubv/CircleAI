# tool_manifest_generator.py
#
# Port of CircleAI.Tools ToolManifestGenerator.cs (C# — the EXACT spec).
#
# Renders ToolDefinition collections into formats consumable by LLMs:
#   - JSON in OpenAI/Qwen function-calling format (tool_choice / tools fields).
#   - Markdown for inclusion in a system prompt as documentation.
#
# The JSON output mirrors System.Text.Json with WriteIndented = true
# (2-space indent) and DefaultIgnoreCondition = WhenWritingNull (the `enum`
# property is omitted when absent). json.dumps(indent=2) matches this layout.

from __future__ import annotations

import json
from typing import Dict, List

from .tool_types import ToolDefinition

_TGN_PREFIX = "tgn."


class ToolManifestGenerator:
    """Renders :class:`ToolDefinition` collections into LLM-consumable JSON and
    Markdown. Mirrors ``CircleAI.Tools.ToolManifestGenerator`` (a static class;
    all methods here are ``@staticmethod``).
    """

    @staticmethod
    def generate_json_manifest(tools: List[ToolDefinition]) -> str:
        """Render ``tools`` as a JSON array in OpenAI/Qwen function-calling
        format. Each element is
        ``{ "type": "function", "function": { "name", "description",
        "parameters": { ... } } }``.
        """
        if tools is None:
            raise ValueError("tools must not be None")

        array: List[dict] = []
        for tool in tools:
            properties: Dict[str, dict] = {}
            for key, value in tool.parameters.items():
                prop: Dict[str, object] = {
                    "type": value.type,
                    "description": value.description,
                }
                if value.enum:  # non-null and non-empty (C# `{ Length: > 0 }`)
                    prop["enum"] = list(value.enum)
                properties[key] = prop

            parameters = {
                "type": "object",
                "properties": properties,
                "required": list(tool.required_parameters),
            }

            array.append(
                {
                    "type": "function",
                    "function": {
                        "name": tool.name,
                        "description": tool.description,
                        "parameters": parameters,
                    },
                }
            )

        return json.dumps(array, indent=2)

    @staticmethod
    def generate_markdown_manifest(tools: List[ToolDefinition]) -> str:
        """Render ``tools`` as a human-readable Markdown summary, suitable for
        inclusion in a system prompt. Tools are grouped by API (the first
        segment after the ``tgn.`` prefix) and groups are emitted in sorted
        (ordinal) order.
        """
        if tools is None:
            raise ValueError("tools must not be None")

        lines: List[str] = []
        lines.append("# Available Tools")
        lines.append("")
        lines.append(f"Total: {len(tools)} tools.")
        lines.append("")

        groups: Dict[str, List[ToolDefinition]] = {}
        for tool in tools:
            group_key = ToolManifestGenerator._extract_api_slug(tool.name)
            groups.setdefault(group_key, []).append(tool)

        for group_key in sorted(groups.keys()):
            lines.append(f"## {group_key}")
            lines.append("")
            for tool in groups[group_key]:
                lines.append(f"### `{tool.name}`")
                lines.append("")
                lines.append(tool.description)
                lines.append("")

                if len(tool.parameters) == 0:
                    lines.append("_No parameters._")
                    lines.append("")
                    continue

                lines.append("Parameters:")
                lines.append("")
                lines.append("| Name | Type | Required | Description |")
                lines.append("|------|------|----------|-------------|")

                required_set = set(tool.required_parameters)
                for key, value in tool.parameters.items():
                    required = "yes" if key in required_set else "no"
                    desc = ToolManifestGenerator._escape_pipe(value.description)
                    if value.enum:
                        desc += " Allowed values: " + ", ".join(value.enum) + "."
                    lines.append(f"| `{key}` | {value.type} | {required} | {desc} |")
                lines.append("")

        # C# StringBuilder.AppendLine writes a trailing newline after every line;
        # joining with "\n" and adding a final "\n" reproduces that exactly.
        return "\n".join(lines) + "\n"

    @staticmethod
    def _extract_api_slug(tool_name: str) -> str:
        # Tool names are "tgn.<api>.<verb>". Group by "tgn.<api>".
        if not tool_name.startswith(_TGN_PREFIX):
            return tool_name
        rest = tool_name[len(_TGN_PREFIX):]
        dot = rest.find(".")
        return tool_name if dot < 0 else _TGN_PREFIX + rest[:dot]

    @staticmethod
    def _escape_pipe(s: str) -> str:
        return s.replace("|", "\\|")
