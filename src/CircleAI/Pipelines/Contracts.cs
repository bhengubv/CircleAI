// Contracts.cs — (2.8.0) Data-pipeline contracts.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Pipelines;

public sealed record PipelineRecord(string Stream, IReadOnlyDictionary<string, object?> Values);

public sealed record PipelineRun(string RunId, string PipelineId, DateTimeOffset StartUtc, DateTimeOffset? EndUtc, long RowsProcessed, string? FailureReason);

public interface IPipelineSource
{
    string BackendId { get; }
    IAsyncEnumerable<PipelineRecord> ReadAsync(string stream, CancellationToken ct = default);
}

public interface IPipelineSink
{
    string BackendId { get; }
    ValueTask WriteAsync(PipelineRecord record, CancellationToken ct = default);
    ValueTask FlushAsync(CancellationToken ct = default);
}

public interface IPipelineExecutor
{
    string BackendId { get; }
    ValueTask<PipelineRun> RunAsync(string pipelineId, CancellationToken ct = default);
    ValueTask<PipelineRun?> GetRunAsync(string runId, CancellationToken ct = default);
}

public sealed record DatabaseQueryResult(IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, int RowCount);

public interface IDatabaseQueryTool
{
    string BackendId { get; }
    ValueTask<DatabaseQueryResult> QueryAsync(string sql, IReadOnlyDictionary<string, object?>? parameters = null, CancellationToken ct = default);
}
