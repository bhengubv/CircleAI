// NullImplementations.cs — (3.0.0) Fail-closed dev-tools defaults.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.DevTools;

public sealed class NullCodeEditor : ICodeEditor
{
    public static readonly NullCodeEditor Instance = new();
    public string BackendId => "null";
    public ValueTask<string> ReadAsync(string path, CancellationToken ct = default) => ValueTask.FromResult("");
    public ValueTask ApplyAsync(IReadOnlyList<FileEdit> edits, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask SaveAsync(string path, CancellationToken ct = default) => ValueTask.CompletedTask;
}

public sealed class NullInlineSuggester : IInlineSuggester
{
    public static readonly NullInlineSuggester Instance = new();
    public string BackendId => "null";
    public ValueTask<InlineSuggestion?> SuggestAsync(string path, int line, int col, string ctx, CancellationToken ct = default)
        => ValueTask.FromResult<InlineSuggestion?>(null);
}

public sealed class NullAgentShell : IAgentShell
{
    public static readonly NullAgentShell Instance = new();
    public string BackendId => "null";
    public ValueTask<AgentTurn> RunTurnAsync(string prompt, CancellationToken ct = default)
        => ValueTask.FromResult(new AgentTurn(Guid.Empty.ToString(), prompt, "", Array.Empty<FileEdit>()));
    public ValueTask<IReadOnlyList<AgentTurn>> HistoryAsync(int limit = 50, CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<AgentTurn>>(Array.Empty<AgentTurn>());
}

public sealed class NullPatchPlanner : IPatchPlanner
{
    public static readonly NullPatchPlanner Instance = new();
    public string BackendId => "null";
    public ValueTask<PatchPlan> PlanAsync(string goal, CancellationToken ct = default)
        => ValueTask.FromResult(new PatchPlan(goal, Array.Empty<string>(), Array.Empty<FileEdit>()));
    public ValueTask ApplyAsync(PatchPlan plan, CancellationToken ct = default) => ValueTask.CompletedTask;
}

public sealed class NullRefactorTool : IRefactorTool
{
    public static readonly NullRefactorTool Instance = new();
    public string BackendId => "null";
    public ValueTask<IReadOnlyList<FileEdit>> ProposeAsync(RefactorRequest r, CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<FileEdit>>(Array.Empty<FileEdit>());
}
