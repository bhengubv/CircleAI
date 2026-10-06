// ToolCalling.cs
//
// (3.3.0) Tool-calling for the voice loop. The AI emits a tool call
// during a turn; the orchestrator dispatches it to either a local
// handler or an HTTPS webhook and returns the result for the next turn.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CircleAI.Telephony;

/// <summary>(3.3.0) Tool definition surfaced to the LLM.</summary>
/// <param name="Name">Tool name (function call name).</param>
/// <param name="Description">Human description used to pick the tool.</param>
/// <param name="ArgumentsJsonSchema">JSON Schema describing the arguments.</param>
public sealed record ToolDefinition(string Name, string Description, string ArgumentsJsonSchema);

/// <summary>(3.3.0) An invocation of one tool by the model.</summary>
public sealed record ToolInvocation(string CallId, string ToolName, string ArgumentsJson);

/// <summary>(3.3.0) Result of a tool invocation.</summary>
public sealed record ToolResult(string CallId, bool Succeeded, string ResultJson, string? Error = null);

/// <summary>(3.3.0) In-process tool handler.</summary>
public delegate ValueTask<string> LocalToolHandler(string argumentsJson, CancellationToken ct);

/// <summary>
/// (3.3.0) Tool registry: register local handlers OR HTTPS webhook URLs
/// against a tool name; the orchestrator dispatches.
/// </summary>
public interface IToolCallRegistry
{
    /// <summary>All registered tool definitions.</summary>
    IReadOnlyList<ToolDefinition> Definitions { get; }

    /// <summary>Register a local handler for <paramref name="definition"/>.</summary>
    void RegisterLocal(ToolDefinition definition, LocalToolHandler handler);

    /// <summary>Register a webhook URL; the orchestrator POSTs arguments JSON.</summary>
    void RegisterWebhook(ToolDefinition definition, Uri webhook);

    /// <summary>Invoke one tool call.</summary>
    ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default);
}

/// <summary>(3.3.0) Default in-memory registry. Thread-safe.</summary>
public sealed class DefaultToolCallRegistry : IToolCallRegistry
{
    private readonly ConcurrentDictionary<string, (ToolDefinition Def, LocalToolHandler? Local, Uri? Webhook)> _tools
        = new(StringComparer.OrdinalIgnoreCase);

    private readonly HttpClient _http;
    private readonly ILogger _logger;

    public DefaultToolCallRegistry(HttpClient http, ILogger<DefaultToolCallRegistry>? logger = null)
    {
        _http   = http   ?? throw new ArgumentNullException(nameof(http));
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public IReadOnlyList<ToolDefinition> Definitions
    {
        get
        {
            var list = new List<ToolDefinition>(_tools.Count);
            foreach (var entry in _tools.Values) list.Add(entry.Def);
            return list;
        }
    }

    public void RegisterLocal(ToolDefinition definition, LocalToolHandler handler)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(handler);
        if (string.IsNullOrWhiteSpace(definition.Name)) throw new ArgumentException("Tool name is required", nameof(definition));
        _tools[definition.Name] = (definition, handler, null);
    }

    public void RegisterWebhook(ToolDefinition definition, Uri webhook)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(webhook);
        if (!webhook.IsAbsoluteUri) throw new ArgumentException("Webhook URL must be absolute.", nameof(webhook));
        if (string.IsNullOrWhiteSpace(definition.Name)) throw new ArgumentException("Tool name is required", nameof(definition));
        _tools[definition.Name] = (definition, null, webhook);
    }

    public async ValueTask<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        if (!_tools.TryGetValue(invocation.ToolName, out var entry))
        {
            return new ToolResult(invocation.CallId, false, "{}", $"Tool '{invocation.ToolName}' is not registered.");
        }

        try
        {
            if (entry.Local is not null)
            {
                var resultJson = await entry.Local(invocation.ArgumentsJson, ct).ConfigureAwait(false);
                return new ToolResult(invocation.CallId, true, resultJson ?? "{}");
            }

            if (entry.Webhook is not null)
            {
                using var content = JsonContent.Create(new
                {
                    call_id   = invocation.CallId,
                    tool      = invocation.ToolName,
                    arguments = JsonDocument.Parse(invocation.ArgumentsJson).RootElement,
                });
                using var resp = await _http.PostAsync(entry.Webhook, content, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    var error = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    _logger.LogWarning("Tool webhook {Tool} returned {Status}", invocation.ToolName, resp.StatusCode);
                    return new ToolResult(invocation.CallId, false, "{}",
                        $"Webhook {(int)resp.StatusCode}: {Truncate(error, 240)}");
                }
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return new ToolResult(invocation.CallId, true, string.IsNullOrWhiteSpace(body) ? "{}" : body);
            }

            return new ToolResult(invocation.CallId, false, "{}",
                $"Tool '{invocation.ToolName}' is registered without a local handler or webhook.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tool {Tool} invocation failed", invocation.ToolName);
            return new ToolResult(invocation.CallId, false, "{}", ex.Message);
        }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
