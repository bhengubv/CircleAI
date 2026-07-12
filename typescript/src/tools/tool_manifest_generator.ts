// tools/tool_manifest_generator.ts
//
// Renders ToolDefinition collections into formats consumable by LLMs. Port of
// CircleAI.Tools.ToolManifestGenerator:
//   - JSON in OpenAI/Qwen function-calling format (tools / tool_choice fields).
//   - Markdown for inclusion in a system prompt as documentation.

import type { ToolDefinition } from "./index.js";

/**
 * Renders {@link ToolDefinition} collections into LLM-consumable formats.
 * Mirrors `CircleAI.Tools.ToolManifestGenerator`.
 */
export const ToolManifestGenerator = {
  /**
   * Renders the given tools as a JSON array in OpenAI/Qwen function-calling
   * format. Each element is
   * `{ "type": "function", "function": { name, description, parameters: {...} } }`.
   *
   * Emitted with 2-space indentation, matching the C# `WriteIndented = true`
   * (System.Text.Json indents with two spaces). Null/absent `enum` is omitted
   * (mirrors `JsonIgnoreCondition.WhenWritingNull`).
   */
  generateJsonManifest(tools: readonly ToolDefinition[]): string {
    if (tools === null || tools === undefined) throw new Error("tools is required.");

    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    const array: any[] = [];
    for (const tool of tools) {
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      const properties: Record<string, any> = {};
      for (const key of Object.keys(tool.parameters)) {
        const p = tool.parameters[key];
        // eslint-disable-next-line @typescript-eslint/no-explicit-any
        const prop: Record<string, any> = { type: p.type, description: p.description };
        if (p.enum !== undefined && p.enum.length > 0) {
          prop.enum = p.enum;
        }
        properties[key] = prop;
      }

      array.push({
        type: "function",
        function: {
          name: tool.name,
          description: tool.description,
          parameters: {
            type: "object",
            properties,
            required: [...tool.requiredParameters],
          },
        },
      });
    }

    return JSON.stringify(array, null, 2);
  },

  /**
   * Renders the given tools as a human-readable Markdown summary. Tools are
   * grouped by API (the `tgn.<api>` prefix), groups sorted ordinally.
   */
  generateMarkdownManifest(tools: readonly ToolDefinition[]): string {
    if (tools === null || tools === undefined) throw new Error("tools is required.");

    const lines: string[] = [];
    lines.push("# Available Tools");
    lines.push("");
    lines.push(`Total: ${tools.length} tools.`);
    lines.push("");

    const groups = new Map<string, ToolDefinition[]>();
    for (const tool of tools) {
      const key = extractApiSlug(tool.name);
      let list = groups.get(key);
      if (list === undefined) {
        list = [];
        groups.set(key, list);
      }
      list.push(tool);
    }

    // SortedDictionary(Ordinal) — ordinal (UTF-16 code-unit) key ordering.
    const sortedKeys = [...groups.keys()].sort(ordinalCompare);

    for (const key of sortedKeys) {
      lines.push(`## ${key}`);
      lines.push("");
      const list = groups.get(key)!;
      for (const tool of list) {
        lines.push(`### \`${tool.name}\``);
        lines.push("");
        lines.push(tool.description);
        lines.push("");

        const paramKeys = Object.keys(tool.parameters);
        if (paramKeys.length === 0) {
          lines.push("_No parameters._");
          lines.push("");
          continue;
        }

        lines.push("Parameters:");
        lines.push("");
        lines.push("| Name | Type | Required | Description |");
        lines.push("|------|------|----------|-------------|");

        const requiredSet = new Set(tool.requiredParameters);
        for (const pk of paramKeys) {
          const p = tool.parameters[pk];
          const required = requiredSet.has(pk) ? "yes" : "no";
          let desc = escapePipe(p.description);
          if (p.enum !== undefined && p.enum.length > 0) {
            desc += " Allowed values: " + p.enum.join(", ") + ".";
          }
          lines.push(`| \`${pk}\` | ${p.type} | ${required} | ${desc} |`);
        }
        lines.push("");
      }
    }

    // C# StringBuilder.AppendLine uses Environment.NewLine; the manifest is
    // consumed as text, so "\n"-joined lines (with a trailing newline) is the
    // faithful, platform-neutral rendering.
    return lines.join("\n") + "\n";
  },
};

function extractApiSlug(toolName: string): string {
  // Tool names are "tgn.<api>.<verb>". Group by "tgn.<api>".
  const prefix = "tgn.";
  if (!toolName.startsWith(prefix)) return toolName;
  const rest = toolName.substring(prefix.length);
  const dot = rest.indexOf(".");
  return dot < 0 ? prefix + rest : prefix + rest.substring(0, dot);
}

function escapePipe(s: string): string {
  return s.split("|").join("\\|");
}

function ordinalCompare(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}
