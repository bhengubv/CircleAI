// workflows/paca_mcp.ts
//
// (3.3.0) MCP server for paca workflows (PacaMcp.cs). Tools surface =
// create_task, list_tasks, edit_task, add_comment, create_doc,
// link_doc_to_task, and any plugin-registered MCP tools. Three transports:
// stdio, SSE, HTTP. Per-agent MCP server config so each agent has its own
// toolset.

/** (3.3.0) MCP transport types. Mirrors C# `McpTransportKind`. */
export enum McpTransportKind {
  Stdio = 0,
  ServerSentEvents = 1,
  Http = 2,
}

/** (3.3.0) Per-agent MCP server config. Mirrors C# `AgentMcpConfig`. */
export interface AgentMcpConfig {
  readonly agentMemberId: string;
  readonly transports: readonly McpTransportKind[];
  readonly enabledTools: readonly string[];
  readonly toolSettings: ReadonlyMap<string, string>;
}

/** (3.3.0) MCP tool descriptor. Mirrors C# `PacaMcpTool`. */
export interface PacaMcpTool {
  readonly name: string;
  readonly description: string;
  readonly inputSchema: string;
}

/** Constructs a {@link PacaMcpTool}. */
export function pacaMcpTool(name: string, description: string, inputSchema: string): PacaMcpTool {
  return { name, description, inputSchema };
}

/** (3.3.0) MCP tool handler signature. Mirrors C# `PacaMcpHandler` delegate. */
export type PacaMcpHandler = (argumentsJson: string, signal?: AbortSignal) => Promise<string>;

interface ToolEntry {
  readonly tool: PacaMcpTool;
  readonly handler: PacaMcpHandler;
}

/** (3.3.0) Paca's MCP server: registers built-in workflow tools + plugin tools. Mirrors C# `PacaMcpServer`. */
export class PacaMcpServer {
  // Case-insensitive keying (C# uses StringComparer.OrdinalIgnoreCase). We store
  // by lowercased name and keep the original tool descriptor inside the entry.
  private readonly tools = new Map<string, ToolEntry>();
  private readonly agentConfigs = new Map<string, AgentMcpConfig>();

  get toolList(): readonly PacaMcpTool[] {
    return [...this.tools.values()].map((t) => t.tool);
  }

  registerTool(tool: PacaMcpTool, handler: PacaMcpHandler): void {
    if (tool == null) throw new Error("tool required");
    if (handler == null) throw new Error("handler required");
    this.tools.set(tool.name.toLowerCase(), { tool, handler });
  }

  /** (3.3.0) Configure a per-agent toolset. */
  configureAgent(config: AgentMcpConfig): void {
    if (config == null) throw new Error("config required");
    this.agentConfigs.set(config.agentMemberId, config);
  }

  getAgentConfig(agentMemberId: string): AgentMcpConfig | null {
    return this.agentConfigs.get(agentMemberId) ?? null;
  }

  /** (3.3.0) Invoke a tool for a specific agent — enforces the agent's enabled-tool list. */
  async invokeAsync(agentMemberId: string, toolName: string, argumentsJson: string, signal?: AbortSignal): Promise<string> {
    const entry = this.tools.get(toolName.toLowerCase());
    if (entry === undefined) {
      return wrapError(`Unknown tool '${toolName}'.`);
    }
    const cfg = this.agentConfigs.get(agentMemberId);
    if (cfg !== undefined) {
      if (cfg.enabledTools.length > 0 && !cfg.enabledTools.some((t) => t.toLowerCase() === toolName.toLowerCase())) {
        return wrapError(`Tool '${toolName}' is not enabled for agent '${agentMemberId}'.`);
      }
    }
    try {
      return await entry.handler(argumentsJson, signal);
    } catch (ex) {
      return wrapError(ex instanceof Error ? ex.message : String(ex));
    }
  }

  /** (3.3.0) JSON-RPC tools/list response payload. */
  toolsListJson(): string {
    const tools = [...this.tools.values()].map((t) => ({
      name: t.tool.name,
      description: t.tool.description,
      inputSchema: JSON.parse(t.tool.inputSchema) as unknown,
    }));
    return JSON.stringify({ tools });
  }
}

/** (3.3.0) Built-in workflow tools. Mirrors C# `PacaCoreMcpTools`. */
export const PacaCoreMcpTools = {
  createTask: pacaMcpTool(
    "create_task",
    "Create a new task in a project.",
    '{"type":"object","properties":{"project_id":{"type":"string"},"title":{"type":"string"},"description":{"type":"string"}},"required":["project_id","title"]}',
  ),

  listTasks: pacaMcpTool(
    "list_tasks",
    "List live tasks in a project.",
    '{"type":"object","properties":{"project_id":{"type":"string"}},"required":["project_id"]}',
  ),

  editTask: pacaMcpTool(
    "edit_task",
    "Edit a task (title, description, status).",
    '{"type":"object","properties":{"project_id":{"type":"string"},"number":{"type":"integer"},"title":{"type":"string"},"description":{"type":"string"},"status":{"type":"string"}},"required":["project_id","number"]}',
  ),

  createDoc: pacaMcpTool(
    "create_doc",
    "Create a doc in the project's doc tree.",
    '{"type":"object","properties":{"project_id":{"type":"string"},"title":{"type":"string"},"parent_id":{"type":"string","nullable":true},"content_json":{"type":"string"}},"required":["project_id","title","content_json"]}',
  ),

  linkDocToTask: pacaMcpTool(
    "link_doc_to_task",
    "Link a doc section to a task.",
    '{"type":"object","properties":{"doc_id":{"type":"string"},"section_anchor":{"type":"string"},"project_id":{"type":"string"},"task_number":{"type":"integer"}},"required":["doc_id","section_anchor","project_id","task_number"]}',
  ),
} as const;

function wrapError(message: string): string {
  return JSON.stringify({ error: { message } });
}
