// NullImplementations.cs
//
// (2.6.0) Fail-safe defaults for the Observer pack.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Observer;

public sealed class NullSensor : ISensor
{
    public string SensorId  { get; } = "null";
    public string Kind      { get; } = "null";
    public string BackendId => "null";

    public Task StartAsync(CancellationToken ct = default)        => Task.CompletedTask;
    public Task StopAsync(CancellationToken ct = default)         => Task.CompletedTask;
    public IDisposable Subscribe(Func<SensorReading, ValueTask> h) => EmptyDisposable.Instance;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class EmptyDisposable : IDisposable
    {
        public static readonly EmptyDisposable Instance = new();
        public void Dispose() { }
    }
}

public sealed class InMemoryObservationToolbox : IObservationToolbox
{
    public string BackendId => "in-memory";
    private readonly ConcurrentDictionary<string, ObservationTool> _tools = new();

    public void RegisterTool(ObservationTool tool) => _tools[tool.ToolId] = tool;

    public bool TryGet(string toolId, out ObservationTool? tool)
    {
        var ok = _tools.TryGetValue(toolId, out var got);
        tool = got;
        return ok;
    }

    public IReadOnlyList<ObservationTool> ListTools()
    {
        var snap = new List<ObservationTool>(_tools.Count);
        foreach (var kv in _tools) snap.Add(kv.Value);
        return snap;
    }
}

public sealed class NullObservationLoop : IObservationLoop
{
    public string BackendId => "null";
    public Task StartAsync(TimeSpan tickInterval, CancellationToken ct = default) => Task.CompletedTask;
    public Task StopAsync(CancellationToken ct = default)                         => Task.CompletedTask;
    public IDisposable Subscribe(Func<ObservationTick, ValueTask> h)              => EmptyDisposable.Instance;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class EmptyDisposable : IDisposable
    {
        public static readonly EmptyDisposable Instance = new();
        public void Dispose() { }
    }
}
