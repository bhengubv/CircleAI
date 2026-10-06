// NullImplementations.cs — (3.0.0)

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.CodeUnderstanding;

public sealed class NullCodeIndexer : ICodeIndexer
{
    public static readonly NullCodeIndexer Instance = new();
    public string BackendId => "null";
    public ValueTask IndexAsync(string repo, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask<int> CountSymbolsAsync(string repo, CancellationToken ct = default) => ValueTask.FromResult(0);
}

public sealed class NullCodeSearch : ICodeSearch
{
    public static readonly NullCodeSearch Instance = new();
    public string BackendId => "null";
    public ValueTask<IReadOnlyList<CodeMatch>> SearchAsync(string q, int topK = 10, CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<CodeMatch>>(Array.Empty<CodeMatch>());
    public ValueTask<IReadOnlyList<CodeMatch>> SemanticSearchAsync(string q, int topK = 10, CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<CodeMatch>>(Array.Empty<CodeMatch>());
}

public sealed class NullSymbolGraph : ISymbolGraph
{
    public static readonly NullSymbolGraph Instance = new();
    public string BackendId => "null";
    public ValueTask<IReadOnlyList<SymbolEdge>> CallersOfAsync(CodeSymbol s, CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<SymbolEdge>>(Array.Empty<SymbolEdge>());
    public ValueTask<IReadOnlyList<SymbolEdge>> CalleesOfAsync(CodeSymbol s, CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<SymbolEdge>>(Array.Empty<SymbolEdge>());
}
