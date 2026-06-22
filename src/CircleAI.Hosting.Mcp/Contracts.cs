// Contracts.cs
//
// (3.2.0) MCP tool + resource provider contracts. Hosts implement
// IMcpTool for each tool they want to expose; the dispatcher routes
// tools/call by Name. IMcpResourceProvider handles resources/list and
// resources/read.

using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Hosting.Mcp;

/// <summary>(3.2.0) One MCP tool the host exposes.</summary>
public interface IMcpTool
{
    /// <summary>Unique tool name (snake_case by convention).</summary>
    string Name { get; }

    /// <summary>One-line description shown in tool listings.</summary>
    string Description { get; }

    /// <summary>
    /// JSON Schema describing the tool's <c>arguments</c> object. The
    /// dispatcher includes this verbatim in <c>tools/list</c>.
    /// </summary>
    object InputSchema { get; }

    /// <summary>
    /// Execute the tool. Return any value that JSON-serialises; the
    /// dispatcher wraps it in MCP's <c>{content:[{type:"text",text:"..."}]}</c>
    /// envelope. Throw <see cref="McpToolException"/> to signal a
    /// tool-level error (returned as <c>isError:true</c>).
    /// </summary>
    Task<object> ExecuteAsync(JsonObject arguments, CancellationToken ct = default);
}

/// <summary>
/// (3.2.0) One MCP resource provider. The dispatcher walks every
/// registered provider for <c>resources/list</c>; for
/// <c>resources/read</c> it picks the first provider whose
/// <see cref="UriScheme"/> matches the leading scheme of the request.
/// </summary>
public interface IMcpResourceProvider
{
    /// <summary>e.g. <c>"vault://"</c>, <c>"models://"</c>.</summary>
    string UriScheme { get; }

    /// <summary>List every resource this provider serves.</summary>
    Task<IReadOnlyList<McpResource>> ListAsync(CancellationToken ct = default);

    /// <summary>Read one resource by uri. Returns null on not-found.</summary>
    Task<McpResourceContent?> ReadAsync(string uri, CancellationToken ct = default);
}

/// <summary>(3.2.0) One MCP resource descriptor.</summary>
public sealed record McpResource(
    string  Uri,
    string  Name,
    string? Description,
    string  MimeType);

/// <summary>(3.2.0) One MCP resource content (returned by resources/read).</summary>
public sealed record McpResourceContent(
    string Uri,
    string MimeType,
    string Text);

/// <summary>
/// (3.2.0) Thrown from inside <see cref="IMcpTool.ExecuteAsync"/> to
/// signal a tool-level error (vs an MCP protocol error). The dispatcher
/// returns this as <c>{content:[{type:"text",text:msg}], isError:true}</c>.
/// </summary>
public sealed class McpToolException : System.Exception
{
    public McpToolException(string message) : base(message) { }
}
