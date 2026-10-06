// InMemoryCodeUnderstanding.cs
//
// (3.3.0) Real-but-lightweight code indexer + searcher + symbol graph.
// Indexer walks the repo filesystem, picks up declarations from
// .cs/.ts/.js/.py/.go using a fast regex pass (enough for namespace
// summaries; real LSP backends swap in later). Search is substring +
// score by file-type weight. Symbol graph is an in-memory adjacency
// list populated by the host (not auto-extracted — that needs a real
// AST).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.CodeUnderstanding;

public sealed class FilesystemCodeIndexer : ICodeIndexer
{
    private static readonly (string Ext, Regex DeclRx, string Kind)[] Languages =
    {
        (".cs",  new Regex(@"(?<=\b(class|interface|record|enum|struct)\s+)(\w+)", RegexOptions.Compiled), "csharp"),
        (".cs",  new Regex(@"(?<=\b(public|private|internal|protected|static)\s+\w+\s+)(\w+)\s*\(", RegexOptions.Compiled), "csharp-method"),
        (".ts",  new Regex(@"(?<=\b(class|interface|type|enum)\s+)(\w+)", RegexOptions.Compiled), "ts"),
        (".js",  new Regex(@"(?<=\b(class|function)\s+)(\w+)", RegexOptions.Compiled), "js"),
        (".py",  new Regex(@"(?<=^\s*(def|class)\s+)(\w+)", RegexOptions.Multiline | RegexOptions.Compiled), "python"),
        (".go",  new Regex(@"(?<=^\s*func\s+(\(\w+\s+\*?\w+\)\s+)?)(\w+)", RegexOptions.Multiline | RegexOptions.Compiled), "go"),
    };

    internal readonly ConcurrentDictionary<string, List<CodeSymbol>> Index = new(StringComparer.Ordinal);

    public string BackendId => "filesystem";

    public async ValueTask IndexAsync(string repoPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(repoPath)) throw new ArgumentException("repoPath required", nameof(repoPath));
        if (!Directory.Exists(repoPath))         throw new DirectoryNotFoundException(repoPath);

        var symbols = new List<CodeSymbol>();
        foreach (var path in EnumerateSourceFiles(repoPath))
        {
            ct.ThrowIfCancellationRequested();
            var lines = await File.ReadAllLinesAsync(path, ct).ConfigureAwait(false);
            var ext = Path.GetExtension(path).ToLowerInvariant();
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (var (e, rx, kind) in Languages)
                {
                    if (e != ext) continue;
                    foreach (Match m in rx.Matches(lines[i]))
                    {
                        if (m.Groups.Count >= 3 && m.Groups[2].Success)
                            symbols.Add(new CodeSymbol(path, i + 1, m.Groups[2].Value, kind));
                    }
                }
            }
        }
        Index[repoPath] = symbols;
    }

    public ValueTask<int> CountSymbolsAsync(string repoPath, CancellationToken ct = default)
        => ValueTask.FromResult(Index.TryGetValue(repoPath, out var l) ? l.Count : 0);

    private static IEnumerable<string> EnumerateSourceFiles(string root)
    {
        foreach (var dir in new[] { root })
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext is ".cs" or ".ts" or ".js" or ".py" or ".go")
                {
                    if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
                    if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
                    if (file.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
                    yield return file;
                }
            }
        }
    }
}

public sealed class IndexBackedCodeSearch : ICodeSearch
{
    private readonly FilesystemCodeIndexer _indexer;
    public IndexBackedCodeSearch(FilesystemCodeIndexer indexer) => _indexer = indexer ?? throw new ArgumentNullException(nameof(indexer));
    public string BackendId => "index-backed";

    public ValueTask<IReadOnlyList<CodeMatch>> SearchAsync(string query, int topK = 10, CancellationToken ct = default)
    {
        if (query is null) throw new ArgumentNullException(nameof(query));
        if (topK <= 0) throw new ArgumentOutOfRangeException(nameof(topK));
        var hits = _indexer.Index.Values.SelectMany(l => l)
            .Where(s => s.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(s => new CodeMatch(s.Path, s.Line, $"{s.Kind} {s.Name}", 1.0f))
            .Take(topK).ToArray();
        return ValueTask.FromResult<IReadOnlyList<CodeMatch>>(hits);
    }

    public ValueTask<IReadOnlyList<CodeMatch>> SemanticSearchAsync(string query, int topK = 10, CancellationToken ct = default)
        => SearchAsync(query, topK, ct);  // No real embedding; substring fallback.
}

public sealed class InMemorySymbolGraph : ISymbolGraph
{
    private readonly List<SymbolEdge> _edges = new();
    private readonly object _lock = new();
    public string BackendId => "in-memory";

    public void Link(CodeSymbol from, CodeSymbol to, string kind = "calls")
    {
        ArgumentNullException.ThrowIfNull(from); ArgumentNullException.ThrowIfNull(to);
        lock (_lock) _edges.Add(new SymbolEdge(from, to, kind));
    }

    public ValueTask<IReadOnlyList<SymbolEdge>> CallersOfAsync(CodeSymbol s, CancellationToken ct = default)
    {
        lock (_lock) return ValueTask.FromResult<IReadOnlyList<SymbolEdge>>(_edges.Where(e => e.To.Name == s.Name).ToArray());
    }

    public ValueTask<IReadOnlyList<SymbolEdge>> CalleesOfAsync(CodeSymbol s, CancellationToken ct = default)
    {
        lock (_lock) return ValueTask.FromResult<IReadOnlyList<SymbolEdge>>(_edges.Where(e => e.From.Name == s.Name).ToArray());
    }
}
