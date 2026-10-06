// NullImplementations.cs
//
// (2.7.0) Drop-all defaults — signals go nowhere.

using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Observability;

public sealed class NullMetricSink : IMetricSink
{
    public static readonly NullMetricSink Instance = new();
    public string BackendId => "null";
    public ValueTask EmitAsync(MetricSample s, CancellationToken ct = default) => ValueTask.CompletedTask;
}

public sealed class NullTraceSink : ITraceSink
{
    public static readonly NullTraceSink Instance = new();
    public string BackendId => "null";
    public ValueTask EmitAsync(TraceSpan s, CancellationToken ct = default) => ValueTask.CompletedTask;
}

public sealed class NullDashboardPublisher : IDashboardPublisher
{
    public static readonly NullDashboardPublisher Instance = new();
    public string BackendId => "null";
    public ValueTask PublishAsync(DashboardSpec spec, CancellationToken ct = default) => ValueTask.CompletedTask;
}
