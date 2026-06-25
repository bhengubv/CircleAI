// InMemoryDevTools.cs
//
// (3.3.0) Real dev-tool implementations — no host-supplied delegates
// required. Inline suggester predicts next tokens from the file's own
// identifier vocabulary; patch planner parses "rename X to Y" /
// "remove X" goals and emits real FileEdits; refactor tool implements
// real Rename + ExtractConstant primitives.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.DevTools;

public sealed class FilesystemCodeEditor : ICodeEditor
{
    public string BackendId => "filesystem";

    public ValueTask<string> ReadAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("path required", nameof(path));
        return new ValueTask<string>(File.ReadAllTextAsync(path, ct));
    }

    public async ValueTask ApplyAsync(IReadOnlyList<FileEdit> edits, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(edits);
        foreach (var byFile in edits.GroupBy(e => e.Path))
        {
            var text = await File.ReadAllTextAsync(byFile.Key, ct).ConfigureAwait(false);
            var ordered = byFile.OrderByDescending(e => e.RangeStart).ToArray();
            var sb = new StringBuilder(text);
            foreach (var e in ordered)
            {
                if (e.RangeStart < 0 || e.RangeEnd > sb.Length || e.RangeEnd < e.RangeStart)
                    throw new ArgumentOutOfRangeException(nameof(edits), $"Invalid edit range {e.RangeStart}..{e.RangeEnd} for {e.Path}");
                sb.Remove(e.RangeStart, e.RangeEnd - e.RangeStart);
                sb.Insert(e.RangeStart, e.Replacement);
            }
            await File.WriteAllTextAsync(byFile.Key, sb.ToString(), ct).ConfigureAwait(false);
        }
    }

    public ValueTask SaveAsync(string path, CancellationToken ct = default) => ValueTask.CompletedTask;
}

/// <summary>(3.3.0) Inline suggester — predicts the next token by collecting
/// identifier candidates from the same file and picking the highest-frequency
/// match for the partial token at the cursor.</summary>
public sealed class TokenContextInlineSuggester : IInlineSuggester
{
    private static readonly Regex IdentifierRx = new(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);

    public string BackendId => "token-context";

    public async ValueTask<InlineSuggestion?> SuggestAsync(string path, int line, int column, string contextBefore, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("path required");
        if (contextBefore is null) throw new ArgumentNullException(nameof(contextBefore));

        var partial = ExtractPartialAtCursor(contextBefore);
        if (partial.Length < 2) return null;

        var fileText = File.Exists(path) ? await File.ReadAllTextAsync(path, ct).ConfigureAwait(false) : contextBefore;
        var freq = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match m in IdentifierRx.Matches(fileText))
        {
            if (m.Value.StartsWith(partial, StringComparison.Ordinal) && m.Value.Length > partial.Length)
                freq[m.Value] = freq.TryGetValue(m.Value, out var n) ? n + 1 : 1;
        }
        if (freq.Count == 0) return null;
        var best = freq.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key.Length).First();
        var completion = best.Key.Substring(partial.Length);
        var confidence = Math.Min(1.0, best.Value / 10.0);
        return new InlineSuggestion(completion, (float)confidence);
    }

    private static string ExtractPartialAtCursor(string contextBefore)
    {
        var i = contextBefore.Length;
        while (i > 0 && (char.IsLetterOrDigit(contextBefore[i - 1]) || contextBefore[i - 1] == '_')) i--;
        return contextBefore[i..];
    }
}

/// <summary>(3.3.0) Agent shell — keeps a turn history with a built-in echo executor.
/// Hosts can pass a smarter executor; the default is a deterministic responder
/// that's good enough for tests + tooling smoke-tests.</summary>
public sealed class InMemoryAgentShell : IAgentShell
{
    private readonly Func<string, CancellationToken, ValueTask<AgentTurn>> _executor;
    private readonly List<AgentTurn> _history = new();
    private readonly object _lock = new();
    private long _seq;

    public InMemoryAgentShell(Func<string, CancellationToken, ValueTask<AgentTurn>>? executor = null)
        => _executor = executor ?? BuiltInExecutor;

    public string BackendId => "in-memory";

    public async ValueTask<AgentTurn> RunTurnAsync(string userPrompt, CancellationToken ct = default)
    {
        if (userPrompt is null) throw new ArgumentNullException(nameof(userPrompt));
        var t    = await _executor(userPrompt, ct).ConfigureAwait(false);
        var turn = string.IsNullOrEmpty(t.TurnId) ? t with { TurnId = $"turn-{Interlocked.Increment(ref _seq)}" } : t;
        lock (_lock) _history.Add(turn);
        return turn;
    }

    public ValueTask<IReadOnlyList<AgentTurn>> HistoryAsync(int limit = 50, CancellationToken ct = default)
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        lock (_lock)
        {
            return ValueTask.FromResult<IReadOnlyList<AgentTurn>>(
                _history.AsEnumerable().Reverse().Take(limit).Reverse().ToArray());
        }
    }

    private static ValueTask<AgentTurn> BuiltInExecutor(string prompt, CancellationToken ct)
    {
        var trimmed = prompt.Trim();
        string response;
        if (trimmed.StartsWith("read ", StringComparison.OrdinalIgnoreCase))
            response = $"Reading {trimmed[5..]} ...";
        else if (trimmed.StartsWith("write ", StringComparison.OrdinalIgnoreCase))
            response = $"Writing {trimmed[6..]} ...";
        else if (trimmed.Contains('?'))
            response = "Acknowledged the question; need more context to give a useful answer.";
        else
            response = $"Acknowledged: {trimmed}.";
        return ValueTask.FromResult(new AgentTurn(TurnId: "", UserPrompt: prompt, Response: response, Edits: Array.Empty<FileEdit>()));
    }
}

/// <summary>(3.3.0) Patch planner that parses goal text and emits real FileEdits.
/// Recognised goals:
///   "rename X to Y"           — substring rename across one or more files
///   "remove line N from F"    — delete a line
///   "append <text> to F"      — append text at end of file</summary>
public sealed class PatternMatchPatchPlanner : IPatchPlanner
{
    private static readonly Regex RenameRx = new(@"^rename\s+(\S+)\s+to\s+(\S+)(?:\s+in\s+(.+))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RemoveRx = new(@"^remove\s+line\s+(\d+)\s+from\s+(.+)$",         RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AppendRx = new(@"^append\s+(.+?)\s+to\s+(.+)$",                   RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ICodeEditor _editor;
    public PatternMatchPatchPlanner(ICodeEditor editor) => _editor = editor ?? throw new ArgumentNullException(nameof(editor));
    public string BackendId => "pattern-match";

    public async ValueTask<PatchPlan> PlanAsync(string goal, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(goal)) throw new ArgumentException("goal required");
        var rename = RenameRx.Match(goal);
        if (rename.Success)
        {
            var oldName = rename.Groups[1].Value;
            var newName = rename.Groups[2].Value;
            var scope   = rename.Groups[3].Success ? rename.Groups[3].Value : Directory.GetCurrentDirectory();
            var edits = await ComputeRenameEdits(scope, oldName, newName, ct).ConfigureAwait(false);
            return new PatchPlan(goal, new[] { $"Rename '{oldName}' -> '{newName}' across {edits.Count} location(s)" }, edits);
        }
        var remove = RemoveRx.Match(goal);
        if (remove.Success)
        {
            var lineNo = int.Parse(remove.Groups[1].Value);
            var path   = remove.Groups[2].Value.Trim();
            var edits  = await ComputeRemoveLineEdits(path, lineNo, ct).ConfigureAwait(false);
            return new PatchPlan(goal, new[] { $"Remove line {lineNo} from {path}" }, edits);
        }
        var append = AppendRx.Match(goal);
        if (append.Success)
        {
            var text = append.Groups[1].Value.Trim().Trim('"');
            var path = append.Groups[2].Value.Trim();
            var len  = File.Exists(path) ? (await File.ReadAllTextAsync(path, ct).ConfigureAwait(false)).Length : 0;
            var edits = new[] { new FileEdit(path, len, len, text) };
            return new PatchPlan(goal, new[] { $"Append to {path}" }, edits);
        }
        return new PatchPlan(goal, new[] { "no recognised intent" }, Array.Empty<FileEdit>());
    }

    public ValueTask ApplyAsync(PatchPlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return _editor.ApplyAsync(plan.ProposedEdits, ct);
    }

    private static async ValueTask<IReadOnlyList<FileEdit>> ComputeRenameEdits(string scope, string oldName, string newName, CancellationToken ct)
    {
        if (!Directory.Exists(scope) && !File.Exists(scope))
            throw new DirectoryNotFoundException(scope);
        var files = File.Exists(scope) ? new[] { scope } : Directory.GetFiles(scope, "*.cs", SearchOption.AllDirectories);
        var edits = new List<FileEdit>();
        foreach (var f in files)
        {
            if (f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
            if (f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
            var text = await File.ReadAllTextAsync(f, ct).ConfigureAwait(false);
            var rx   = new Regex($@"\b{Regex.Escape(oldName)}\b");
            foreach (Match m in rx.Matches(text))
                edits.Add(new FileEdit(f, m.Index, m.Index + m.Length, newName));
        }
        return edits;
    }

    private static async ValueTask<IReadOnlyList<FileEdit>> ComputeRemoveLineEdits(string path, int lineNo, CancellationToken ct)
    {
        if (!File.Exists(path)) throw new FileNotFoundException(path);
        var text = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        var offset = 0;
        var current = 1;
        for (var i = 0; i < text.Length; i++)
        {
            if (current == lineNo)
            {
                offset = i;
                var end = text.IndexOf('\n', i);
                var rangeEnd = end < 0 ? text.Length : end + 1;
                return new[] { new FileEdit(path, offset, rangeEnd, "") };
            }
            if (text[i] == '\n') current++;
        }
        return Array.Empty<FileEdit>();
    }
}

/// <summary>(3.3.0) Refactor tool — implements real Rename + ExtractConstant
/// primitives using regex pattern matching. Hosts can subclass to add more.</summary>
public sealed class RegexRefactorTool : IRefactorTool
{
    public string BackendId => "regex";

    public async ValueTask<IReadOnlyList<FileEdit>> ProposeAsync(RefactorRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.TargetPaths);
        var description = request.Description?.Trim() ?? "";
        if (description.StartsWith("rename ", StringComparison.OrdinalIgnoreCase))
        {
            var m = Regex.Match(description, @"^rename\s+(\S+)\s+to\s+(\S+)", RegexOptions.IgnoreCase);
            if (!m.Success) return Array.Empty<FileEdit>();
            return await RenameInFilesAsync(request.TargetPaths, m.Groups[1].Value, m.Groups[2].Value, ct).ConfigureAwait(false);
        }
        if (description.StartsWith("extract ", StringComparison.OrdinalIgnoreCase))
        {
            var m = Regex.Match(description, @"^extract\s+constant\s+from\s+""([^""]+)""\s+as\s+(\S+)", RegexOptions.IgnoreCase);
            if (!m.Success) return Array.Empty<FileEdit>();
            return await ExtractConstantAsync(request.TargetPaths, m.Groups[1].Value, m.Groups[2].Value, ct).ConfigureAwait(false);
        }
        return Array.Empty<FileEdit>();
    }

    private static async Task<IReadOnlyList<FileEdit>> RenameInFilesAsync(IReadOnlyList<string> paths, string oldName, string newName, CancellationToken ct)
    {
        var edits = new List<FileEdit>();
        foreach (var p in paths)
        {
            if (!File.Exists(p)) continue;
            var text = await File.ReadAllTextAsync(p, ct).ConfigureAwait(false);
            var rx = new Regex($@"\b{Regex.Escape(oldName)}\b");
            foreach (Match m in rx.Matches(text))
                edits.Add(new FileEdit(p, m.Index, m.Index + m.Length, newName));
        }
        return edits;
    }

    private static async Task<IReadOnlyList<FileEdit>> ExtractConstantAsync(IReadOnlyList<string> paths, string literal, string constantName, CancellationToken ct)
    {
        var edits = new List<FileEdit>();
        var quoted = "\"" + literal + "\"";
        foreach (var p in paths)
        {
            if (!File.Exists(p)) continue;
            var text = await File.ReadAllTextAsync(p, ct).ConfigureAwait(false);
            var first = text.IndexOf(quoted, StringComparison.Ordinal);
            if (first < 0) continue;
            // Inject a private const at the top of the first class declaration.
            var classIdx = text.IndexOf("class ", StringComparison.Ordinal);
            if (classIdx < 0) continue;
            var brace = text.IndexOf('{', classIdx);
            if (brace < 0) continue;
            var insertion = $"\n    private const string {constantName} = {quoted};\n";
            edits.Add(new FileEdit(p, brace + 1, brace + 1, insertion));
            // Replace every literal occurrence.
            for (var idx = first; idx >= 0; idx = text.IndexOf(quoted, idx + 1, StringComparison.Ordinal))
                edits.Add(new FileEdit(p, idx, idx + quoted.Length, constantName));
        }
        return edits;
    }
}
