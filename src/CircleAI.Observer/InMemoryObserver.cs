// InMemoryObserver.cs
//
// (3.3.0) Real observation-loop runtime + tool registry + sensor base
// class. The loop ticks at a configured interval, collects last
// readings from registered sensors, asks a host-supplied "reason"
// function for the rationale + tools to invoke, runs each tool, and
// fans out the ObservationTick to subscribers.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Observer;

/// <summary>(3.3.0) Captures the latest reading from a sensor.</summary>
public sealed class SensorRecorder : IDisposable
{
    private readonly IDisposable _sub;
    private SensorReading? _latest;
    public SensorRecorder(ISensor sensor)
    {
        ArgumentNullException.ThrowIfNull(sensor);
        _sub = sensor.Subscribe(r => { _latest = r; return ValueTask.CompletedTask; });
    }
    public SensorReading? Latest => _latest;
    public void Dispose() => _sub.Dispose();
}

/// <summary>(3.3.0) Decision shape returned by the reasoner.</summary>
public sealed record ObserverDecision(string Reasoning, IReadOnlyList<string> ToolsToInvoke, IReadOnlyDictionary<string, string>? ToolArgs = null);

/// <summary>(3.3.0) The perceive-reason-act loop.</summary>
public sealed class InMemoryObservationLoop : IObservationLoop
{
    private readonly IReadOnlyList<SensorRecorder> _recorders;
    private readonly IObservationToolbox _toolbox;
    private readonly Func<IReadOnlyList<SensorReading>, CancellationToken, ValueTask<ObserverDecision>> _reason;
    private readonly List<Func<ObservationTick, ValueTask>> _subs = new();
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private Task? _runTask;

    public InMemoryObservationLoop(
        IEnumerable<ISensor> sensors,
        IObservationToolbox toolbox,
        Func<IReadOnlyList<SensorReading>, CancellationToken, ValueTask<ObserverDecision>> reason)
    {
        ArgumentNullException.ThrowIfNull(sensors);
        _toolbox = toolbox ?? throw new ArgumentNullException(nameof(toolbox));
        _reason  = reason  ?? throw new ArgumentNullException(nameof(reason));
        _recorders = sensors.Select(s => new SensorRecorder(s)).ToArray();
    }

    public string BackendId => "in-memory";

    public Task StartAsync(TimeSpan tickInterval, CancellationToken ct = default)
    {
        if (_cts is not null) throw new InvalidOperationException("already started");
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _runTask = Task.Run(() => RunAsync(tickInterval, _cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_cts is null) return;
        _cts.Cancel();
        try { if (_runTask is not null) await _runTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { /* expected */ }
        _cts.Dispose(); _cts = null; _runTask = null;
    }

    public IDisposable Subscribe(Func<ObservationTick, ValueTask> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_lock) _subs.Add(handler);
        return new Token(this, handler);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        foreach (var r in _recorders) r.Dispose();
    }

    private async Task RunAsync(TimeSpan interval, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var readings = _recorders.Select(r => r.Latest).Where(r => r is not null).Cast<SensorReading>().ToArray();
                var decision = await _reason(readings, ct).ConfigureAwait(false);
                var invoked = new List<string>();
                foreach (var toolId in decision.ToolsToInvoke)
                {
                    if (_toolbox.TryGet(toolId, out var tool) && tool is not null)
                    {
                        try
                        {
                            await tool.Invoke(decision.ToolArgs ?? new Dictionary<string, string>(), ct).ConfigureAwait(false);
                            invoked.Add(toolId);
                        }
                        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[CircleAI.Observer] tool '{toolId}' threw: {ex.Message}"); }
                    }
                }
                var tick = new ObservationTick(DateTimeOffset.UtcNow, readings, decision.Reasoning, invoked);
                Func<ObservationTick, ValueTask>[] snap;
                lock (_lock) snap = _subs.ToArray();
                foreach (var s in snap)
                {
                    try { await s(tick).ConfigureAwait(false); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[CircleAI.Observer] subscriber threw: {ex.Message}"); }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[CircleAI.Observer] reasoner threw, skipping tick: {ex.Message}"); }
            try { await Task.Delay(interval, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
        }
    }

    private sealed class Token : IDisposable
    {
        private readonly InMemoryObservationLoop _o; private readonly Func<ObservationTick, ValueTask> _h;
        public Token(InMemoryObservationLoop o, Func<ObservationTick, ValueTask> h) { _o = o; _h = h; }
        public void Dispose() { lock (_o._lock) _o._subs.Remove(_h); }
    }
}
