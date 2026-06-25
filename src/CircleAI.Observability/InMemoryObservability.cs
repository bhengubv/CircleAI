// InMemoryObservability.cs
//
// (3.3.0) Real in-memory metric sink, trace sink, and dashboard
// publisher. Metric sink aggregates per-name counters / gauges; trace
// sink stores spans per traceId; dashboard publisher round-trips
// specs by id.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Observability;

public sealed class InMemoryMetricSink : IMetricSink
{
    private readonly ConcurrentDictionary<string, List<MetricSample>> _byName = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public string BackendId => "in-memory";

    public ValueTask EmitAsync(MetricSample sample, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (string.IsNullOrWhiteSpace(sample.Name)) throw new ArgumentException("Name required");
        lock (_lock)
        {
            var list = _byName.GetOrAdd(sample.Name, _ => new List<MetricSample>());
            list.Add(sample);
        }
        return ValueTask.CompletedTask;
    }

    public IReadOnlyList<MetricSample> Read(string name)
    {
        lock (_lock)
        {
            if (!_byName.TryGetValue(name, out var list)) return Array.Empty<MetricSample>();
            return list.ToArray();
        }
    }

    public IReadOnlyList<string> MetricNames => _byName.Keys.OrderBy(k => k).ToArray();
}

public sealed class InMemoryTraceSink : ITraceSink
{
    private readonly ConcurrentDictionary<string, List<TraceSpan>> _byTrace = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public string BackendId => "in-memory";

    public ValueTask EmitAsync(TraceSpan span, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(span);
        if (string.IsNullOrWhiteSpace(span.TraceId)) throw new ArgumentException("TraceId required");
        lock (_lock)
        {
            var list = _byTrace.GetOrAdd(span.TraceId, _ => new List<TraceSpan>());
            list.Add(span);
        }
        return ValueTask.CompletedTask;
    }

    public IReadOnlyList<TraceSpan> Read(string traceId)
    {
        lock (_lock)
        {
            if (!_byTrace.TryGetValue(traceId, out var list)) return Array.Empty<TraceSpan>();
            return list.OrderBy(s => s.StartUtc).ToArray();
        }
    }
}

public sealed class InMemoryDashboardPublisher : IDashboardPublisher
{
    private readonly ConcurrentDictionary<string, DashboardSpec> _specs = new(StringComparer.Ordinal);

    public string BackendId => "in-memory";

    public ValueTask PublishAsync(DashboardSpec spec, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (string.IsNullOrWhiteSpace(spec.DashboardId)) throw new ArgumentException("DashboardId required");
        _specs[spec.DashboardId] = spec;
        return ValueTask.CompletedTask;
    }

    public DashboardSpec? Get(string dashboardId) => _specs.GetValueOrDefault(dashboardId);
    public IReadOnlyList<DashboardSpec> All => _specs.Values.OrderBy(s => s.DashboardId).ToArray();
}
