// PacaMcp.cs
//
// (3.3.0) MCP server for paca workflows. Tools surface = create_task,
// list_tasks, edit_task, add_comment, create_doc, link_doc_to_task,
// and any plugin-registered MCP tools. Three transports: stdio, SSE,
// HTTP. Per-agent MCP server config so each agent has its own toolset.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Workflows;

/// <summary>(3.3.0) MCP transport types.</summary>
public enum McpTransportKind { Stdio, ServerSentEvents, Http }

/// <summary>(3.3.0) Per-agent MCP server config.</summary>
public sealed record AgentMcpConfig(
    string                              AgentMemberId,
    IReadOnlyList<McpTransportKind>     Transports,
    IReadOnlyList<string>               EnabledTools,
    IReadOnlyDictionary<string, string> ToolSettings);

/// <summary>(3.3.0) MCP tool descriptor.</summary>
public sealed record PacaMcpTool(string Name, string Description, string InputSchema);

/// <summary>(3.3.0) MCP tool handler signature.</summary>
public delegate ValueTask<string> PacaMcpHandler(string argumentsJson, CancellationToken ct);

/// <summary>(3.3.0) Paca's MCP server: registers built-in workflow tools + plugin tools.</summary>
public sealed class PacaMcpServer
{
    private readonly ConcurrentDictionary<string, (PacaMcpTool Tool, PacaMcpHandler Handler)> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, AgentMcpConfig> _agentConfigs = new();

    public PacaMcpServer()
    {
    }

    public IReadOnlyList<PacaMcpTool> Tools => _tools.Values.Select(t => t.Tool).ToList();

    public void RegisterTool(PacaMcpTool tool, PacaMcpHandler handler)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(handler);
        _tools[tool.Name] = (tool, handler);
    }

    /// <summary>(3.3.0) Configure a per-agent toolset.</summary>
    public void ConfigureAgent(AgentMcpConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _agentConfigs[config.AgentMemberId] = config;
    }

    public AgentMcpConfig? GetAgentConfig(string agentMemberId)
        => _agentConfigs.TryGetValue(agentMemberId, out var c) ? c : null;

    /// <summary>(3.3.0) Invoke a tool for a specific agent — enforces the agent's enabled-tool list.</summary>
    public async ValueTask<string> InvokeAsync(string agentMemberId, string toolName, string argumentsJson, CancellationToken ct = default)
    {
        if (!_tools.TryGetValue(toolName, out var entry))
        {
            return WrapError($"Unknown tool '{toolName}'.");
        }
        if (_agentConfigs.TryGetValue(agentMemberId, out var cfg))
        {
            if (cfg.EnabledTools.Count > 0 && !cfg.EnabledTools.Contains(toolName, StringComparer.OrdinalIgnoreCase))
            {
                return WrapError($"Tool '{toolName}' is not enabled for agent '{agentMemberId}'.");
            }
        }
        try
        {
            return await entry.Handler(argumentsJson, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return WrapError(ex.Message);
        }
    }

    /// <summary>(3.3.0) JSON-RPC tools/list response payload.</summary>
    public string ToolsListJson()
    {
        var tools = _tools.Values.Select(t => new
        {
            name        = t.Tool.Name,
            description = t.Tool.Description,
            inputSchema = JsonDocument.Parse(t.Tool.InputSchema).RootElement,
        }).ToArray();
        return JsonSerializer.Serialize(new { tools });
    }

    private static string WrapError(string message)
        => JsonSerializer.Serialize(new { error = new { message } });
}

/// <summary>(3.3.0) Built-in workflow tools.</summary>
public static class PacaCoreMcpTools
{
    public static PacaMcpTool CreateTask { get; } = new(
        Name:        "create_task",
        Description: "Create a new task in a project.",
        InputSchema: """
        {"type":"object","properties":{"project_id":{"type":"string"},"title":{"type":"string"},"description":{"type":"string"}},"required":["project_id","title"]}
        """);

    public static PacaMcpTool ListTasks { get; } = new(
        Name:        "list_tasks",
        Description: "List live tasks in a project.",
        InputSchema: """
        {"type":"object","properties":{"project_id":{"type":"string"}},"required":["project_id"]}
        """);

    public static PacaMcpTool EditTask { get; } = new(
        Name:        "edit_task",
        Description: "Edit a task (title, description, status).",
        InputSchema: """
        {"type":"object","properties":{"project_id":{"type":"string"},"number":{"type":"integer"},"title":{"type":"string"},"description":{"type":"string"},"status":{"type":"string"}},"required":["project_id","number"]}
        """);

    public static PacaMcpTool CreateDoc { get; } = new(
        Name:        "create_doc",
        Description: "Create a doc in the project's doc tree.",
        InputSchema: """
        {"type":"object","properties":{"project_id":{"type":"string"},"title":{"type":"string"},"parent_id":{"type":"string","nullable":true},"content_json":{"type":"string"}},"required":["project_id","title","content_json"]}
        """);

    public static PacaMcpTool LinkDocToTask { get; } = new(
        Name:        "link_doc_to_task",
        Description: "Link a doc section to a task.",
        InputSchema: """
        {"type":"object","properties":{"doc_id":{"type":"string"},"section_anchor":{"type":"string"},"project_id":{"type":"string"},"task_number":{"type":"integer"}},"required":["doc_id","section_anchor","project_id","task_number"]}
        """);
}
