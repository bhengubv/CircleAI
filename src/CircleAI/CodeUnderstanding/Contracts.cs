// Contracts.cs — (3.0.0) Code-understanding contracts.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.CodeUnderstanding;

public sealed record CodeSymbol(string Path, int Line, string Name, string Kind);
public sealed record CodeMatch(string Path, int Line, string Snippet, float Score);
public sealed record SymbolEdge(CodeSymbol From, CodeSymbol To, string Kind);

public interface ICodeIndexer
{
    string BackendId { get; }
    ValueTask IndexAsync(string repoPath, CancellationToken ct = default);
    ValueTask<int> CountSymbolsAsync(string repoPath, CancellationToken ct = default);
}

public interface ICodeSearch
{
    string BackendId { get; }
    ValueTask<IReadOnlyList<CodeMatch>> SearchAsync(string query, int topK = 10, CancellationToken ct = default);
    ValueTask<IReadOnlyList<CodeMatch>> SemanticSearchAsync(string query, int topK = 10, CancellationToken ct = default);
}

public interface ISymbolGraph
{
    string BackendId { get; }
    ValueTask<IReadOnlyList<SymbolEdge>> CallersOfAsync(CodeSymbol s, CancellationToken ct = default);
    ValueTask<IReadOnlyList<SymbolEdge>> CalleesOfAsync(CodeSymbol s, CancellationToken ct = default);
}
