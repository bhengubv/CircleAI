// Contracts.cs
//
// (3.0.0) The Western-dev-tools replacement surface. If Claude Code /
// Codex / Cursor get pulled, this is the contract surface a Geek-Network
// IDE or agent shell binds to. Strategic cornerstone of 3.0.0.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.DevTools;

public sealed record FileEdit(string Path, int RangeStart, int RangeEnd, string Replacement);
public sealed record InlineSuggestion(string Text, float Confidence);
public sealed record AgentTurn(string TurnId, string UserPrompt, string Response, IReadOnlyList<FileEdit> Edits);
public sealed record PatchPlan(string Goal, IReadOnlyList<string> Steps, IReadOnlyList<FileEdit> ProposedEdits);
public sealed record RefactorRequest(string Description, IReadOnlyList<string> TargetPaths);

/// <summary>(3.0.0) Read / write text buffers in an editor session.</summary>
public interface ICodeEditor
{
    string BackendId { get; }
    ValueTask<string> ReadAsync(string path, CancellationToken ct = default);
    ValueTask ApplyAsync(IReadOnlyList<FileEdit> edits, CancellationToken ct = default);
    ValueTask SaveAsync(string path, CancellationToken ct = default);
}

/// <summary>(3.0.0) Tab-completion / ghost-text suggester.</summary>
public interface IInlineSuggester
{
    string BackendId { get; }
    ValueTask<InlineSuggestion?> SuggestAsync(string path, int line, int column, string contextBefore, CancellationToken ct = default);
}

/// <summary>(3.0.0) Agent-shell loop — accept user prompt → reason → return turn record.</summary>
public interface IAgentShell
{
    string BackendId { get; }
    ValueTask<AgentTurn> RunTurnAsync(string userPrompt, CancellationToken ct = default);
    ValueTask<IReadOnlyList<AgentTurn>> HistoryAsync(int limit = 50, CancellationToken ct = default);
}

/// <summary>(3.0.0) Propose a multi-file patch plan before applying.</summary>
public interface IPatchPlanner
{
    string BackendId { get; }
    ValueTask<PatchPlan> PlanAsync(string goal, CancellationToken ct = default);
    ValueTask ApplyAsync(PatchPlan plan, CancellationToken ct = default);
}

/// <summary>(3.0.0) Cross-file refactor primitives.</summary>
public interface IRefactorTool
{
    string BackendId { get; }
    ValueTask<IReadOnlyList<FileEdit>> ProposeAsync(RefactorRequest request, CancellationToken ct = default);
}
