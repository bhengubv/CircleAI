// IToolDescriptor.cs
//
// (2.0.3 skeleton, 2.5.0 fully wired) The atomic unit of the
// CircleAI.Tools.Catalog system — a single callable a model can use.
// Pattern adopted from composio (MIT) under Apache 2.0.

using System.Collections.Generic;

namespace CircleAI.Hosting.Tools;

/// <summary>
/// Describes one tool callable by an LLM. The descriptor is data-only —
/// the actual execution path lives in <see cref="IToolExecutor"/>.
/// </summary>
/// <param name="Name">Stable identifier, e.g. <c>"gmail.send"</c>. Must be unique within a catalog.</param>
/// <param name="Description">One- or two-line summary the model reads to decide whether to call.</param>
/// <param name="Provider">Plug-in id that owns this tool, e.g. <c>"gmail"</c> / <c>"github"</c> / <c>"local"</c>.</param>
/// <param name="JsonSchema">JSON Schema for the argument object. Empty string when arg-less.</param>
/// <param name="AuthScheme">How auth is brokered: <c>"none"</c>, <c>"oauth2"</c>, <c>"api-key"</c>, <c>"host"</c>.</param>
/// <param name="Tags">Free-form tags for filtering — e.g. <c>["communication","oauth"]</c>.</param>
/// <param name="Examples">Optional natural-language examples the catalog surfaces during search.</param>
public sealed record ToolDescriptor(
    string                Name,
    string                Description,
    string                Provider,
    string                JsonSchema     = "",
    string                AuthScheme     = "none",
    IReadOnlyList<string>? Tags          = null,
    IReadOnlyList<string>? Examples      = null);

/// <summary>
/// Result of one tool execution. <see cref="Success"/> says whether the
/// underlying call succeeded; on failure, <see cref="Error"/> carries
/// the reason.
/// </summary>
public sealed record ToolExecutionResult(
    bool    Success,
    object? Result      = null,
    string? Error       = null,
    long    DurationMs  = 0);
