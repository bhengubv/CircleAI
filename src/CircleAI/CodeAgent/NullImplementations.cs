// NullImplementations.cs — fail-closed CodeAgent default.
//
// Matches the repo convention (CircleAI.DevTools.NullImplementations): a
// no-dependency default that declines cleanly, so a host can bind the interface
// before any brain / model is wired without a null-reference surprise.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Core;      // DeviceProbe
using CircleAI.DevTools;  // FileEdit
using CircleAI.Inference; // SelectionQuality

namespace CircleAI.CodeAgent;

/// <summary>
/// Fail-closed <see cref="ICodeAgent"/>: always declines with
/// <see cref="SelectionQuality.Unavailable"/>. The safe default when on-device
/// coding is not wired on this build.
/// </summary>
public sealed class NullCodeAgent : ICodeAgent
{
    /// <summary>Shared instance — holds no state.</summary>
    public static readonly NullCodeAgent Instance = new();

    /// <inheritdoc/>
    public ValueTask<CodeAgentRunResult> RunAsync(
        string task,
        string workspaceRoot,
        DeviceProbe? probe = null,
        CancellationToken ct = default) =>
        ValueTask.FromResult(new CodeAgentRunResult(
            Available:    false,
            Quality:      SelectionQuality.Unavailable,
            Reason:       "null code agent: on-device coding is not wired on this build.",
            Steps:        Array.Empty<CodeAgentStep>(),
            AppliedEdits: Array.Empty<FileEdit>(),
            FinalSummary: string.Empty));
}
