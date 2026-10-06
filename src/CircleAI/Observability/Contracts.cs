// Contracts.cs
//
// (2.7.0) Observability contracts.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Observability;

public sealed record MetricSample(string Name, double Value, IReadOnlyDictionary<string, string>? Tags = null);

public sealed record TraceSpan(
    string                              TraceId,
    string                              SpanId,
    string?                             ParentSpanId,
    string                              Name,
    DateTimeOffset                      StartUtc,
    TimeSpan                            Duration,
    IReadOnlyDictionary<string, string>? Attributes = null);

public sealed record DashboardSpec(string DashboardId, string Title, string JsonBlob);

/// <summary>(2.7.0) Metric sink — Prometheus / OTel.</summary>
public interface IMetricSink
{
    string BackendId { get; }
    ValueTask EmitAsync(MetricSample sample, CancellationToken ct = default);
}

/// <summary>(2.7.0) Trace sink — OTel.</summary>
public interface ITraceSink
{
    string BackendId { get; }
    ValueTask EmitAsync(TraceSpan span, CancellationToken ct = default);
}

/// <summary>(2.7.0) Dashboard publisher — Grafana / claude-team-dashboard.</summary>
public interface IDashboardPublisher
{
    string BackendId { get; }
    ValueTask PublishAsync(DashboardSpec spec, CancellationToken ct = default);
}
