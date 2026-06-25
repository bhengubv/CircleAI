// InMemoryPipelines.cs
//
// (3.3.0) Real in-memory pipeline source/sink/executor and an
// in-memory database-query tool that operates on a dictionary of
// in-memory tables. The executor wires registered pipelines (a
// function that reads from a source and writes to a sink) and tracks
// runs in a dictionary.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace CircleAI.Pipelines;

public sealed class InMemoryPipelineSource : IPipelineSource
{
    private readonly ConcurrentDictionary<string, Channel<PipelineRecord>> _streams = new(StringComparer.Ordinal);

    public string BackendId => "in-memory";

    public void Push(string stream, PipelineRecord record)
    {
        if (string.IsNullOrWhiteSpace(stream)) throw new ArgumentException("stream required");
        ArgumentNullException.ThrowIfNull(record);
        var ch = _streams.GetOrAdd(stream, _ => Channel.CreateUnbounded<PipelineRecord>());
        ch.Writer.TryWrite(record);
    }

    public void Complete(string stream)
    {
        if (_streams.TryGetValue(stream, out var ch)) ch.Writer.TryComplete();
    }

    public async IAsyncEnumerable<PipelineRecord> ReadAsync(
        string stream,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(stream)) throw new ArgumentException("stream required");
        var ch = _streams.GetOrAdd(stream, _ => Channel.CreateUnbounded<PipelineRecord>());
        while (await ch.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            while (ch.Reader.TryRead(out var record)) yield return record;
        }
    }
}

public sealed class InMemoryPipelineSink : IPipelineSink
{
    private readonly List<PipelineRecord> _records = new();
    private readonly object _lock = new();

    public string BackendId => "in-memory";

    public ValueTask WriteAsync(PipelineRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_lock) _records.Add(record);
        return ValueTask.CompletedTask;
    }

    public ValueTask FlushAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public IReadOnlyList<PipelineRecord> Records
    {
        get { lock (_lock) return _records.ToArray(); }
    }
}

public sealed class InMemoryPipelineExecutor : IPipelineExecutor
{
    private readonly ConcurrentDictionary<string, Func<CancellationToken, Task<long>>> _pipelines = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PipelineRun> _runs = new(StringComparer.Ordinal);
    private long _runSeq;

    public string BackendId => "in-memory";

    public void Register(string pipelineId, Func<CancellationToken, Task<long>> runner)
    {
        if (string.IsNullOrWhiteSpace(pipelineId)) throw new ArgumentException("pipelineId required");
        ArgumentNullException.ThrowIfNull(runner);
        _pipelines[pipelineId] = runner;
    }

    public async ValueTask<PipelineRun> RunAsync(string pipelineId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(pipelineId)) throw new ArgumentException("pipelineId required");
        if (!_pipelines.TryGetValue(pipelineId, out var runner))
            throw new InvalidOperationException($"Unknown pipeline '{pipelineId}'.");

        var runId = $"run-{Interlocked.Increment(ref _runSeq)}";
        var start = DateTimeOffset.UtcNow;
        long rows = 0;
        string? err = null;
        try { rows = await runner(ct).ConfigureAwait(false); }
        catch (Exception ex) { err = ex.Message; }
        var run = new PipelineRun(runId, pipelineId, start, DateTimeOffset.UtcNow, rows, err);
        _runs[runId] = run;
        return run;
    }

    public ValueTask<PipelineRun?> GetRunAsync(string runId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(runId)) throw new ArgumentException("runId required");
        _runs.TryGetValue(runId, out var run);
        return ValueTask.FromResult(run);
    }
}

/// <summary>(3.3.0) Tiny in-memory database — supports simple SELECTs against registered tables.</summary>
public sealed class InMemoryDatabaseQueryTool : IDatabaseQueryTool
{
    private readonly ConcurrentDictionary<string, List<Dictionary<string, object?>>> _tables = new(StringComparer.OrdinalIgnoreCase);

    public string BackendId => "in-memory";

    public void Insert(string tableName, IReadOnlyDictionary<string, object?> row)
    {
        if (string.IsNullOrWhiteSpace(tableName)) throw new ArgumentException("tableName required");
        ArgumentNullException.ThrowIfNull(row);
        var list = _tables.GetOrAdd(tableName, _ => new List<Dictionary<string, object?>>());
        lock (list) list.Add(new Dictionary<string, object?>(row, StringComparer.Ordinal));
    }

    public ValueTask<DatabaseQueryResult> QueryAsync(string sql, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sql)) throw new ArgumentException("sql required", nameof(sql));
        var trimmed = sql.Trim();
        if (!trimmed.StartsWith("SELECT ", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Only SELECT queries are supported by InMemoryDatabaseQueryTool.");

        // "SELECT * FROM <table>" (extremely simple parser; sufficient for in-memory use).
        var fromIdx = trimmed.IndexOf("FROM ", StringComparison.OrdinalIgnoreCase);
        if (fromIdx < 0) throw new InvalidOperationException("SELECT requires a FROM clause.");
        var rest = trimmed[(fromIdx + 5)..].Trim();
        var spaceIdx = rest.IndexOfAny(new[] { ' ', ';' });
        var tableName = spaceIdx > 0 ? rest[..spaceIdx] : rest;

        if (!_tables.TryGetValue(tableName, out var list))
            return ValueTask.FromResult(new DatabaseQueryResult(Array.Empty<IReadOnlyDictionary<string, object?>>(), 0));

        IReadOnlyDictionary<string, object?>[] rows;
        lock (list) rows = list.Cast<IReadOnlyDictionary<string, object?>>().ToArray();
        return ValueTask.FromResult(new DatabaseQueryResult(rows, rows.Length));
    }
}
