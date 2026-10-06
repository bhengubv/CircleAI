// NullImplementations.cs — (2.8.0)

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Pipelines;

public sealed class NullPipelineSource : IPipelineSource
{
    public static readonly NullPipelineSource Instance = new();
    public string BackendId => "null";
    public async IAsyncEnumerable<PipelineRecord> ReadAsync(string stream, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        yield break;
    }
}

public sealed class NullPipelineSink : IPipelineSink
{
    public static readonly NullPipelineSink Instance = new();
    public string BackendId => "null";
    public ValueTask WriteAsync(PipelineRecord r, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask FlushAsync(CancellationToken ct = default)                   => ValueTask.CompletedTask;
}

public sealed class NullPipelineExecutor : IPipelineExecutor
{
    public static readonly NullPipelineExecutor Instance = new();
    public string BackendId => "null";
    public ValueTask<PipelineRun> RunAsync(string id, CancellationToken ct = default)
        => ValueTask.FromResult(new PipelineRun(Guid.Empty.ToString(), id, DateTimeOffset.MinValue, DateTimeOffset.MinValue, 0, "NullPipelineExecutor"));
    public ValueTask<PipelineRun?> GetRunAsync(string runId, CancellationToken ct = default)
        => ValueTask.FromResult<PipelineRun?>(null);
}

public sealed class NullDatabaseQueryTool : IDatabaseQueryTool
{
    public static readonly NullDatabaseQueryTool Instance = new();
    public string BackendId => "null";
    public ValueTask<DatabaseQueryResult> QueryAsync(string sql, IReadOnlyDictionary<string, object?>? p = null, CancellationToken ct = default)
        => ValueTask.FromResult(new DatabaseQueryResult(Array.Empty<IReadOnlyDictionary<string, object?>>(), 0));
}
