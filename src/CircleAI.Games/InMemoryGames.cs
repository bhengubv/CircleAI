// InMemoryGames.cs
//
// (3.3.0) Real game-loop primitive — uses a System.Threading.Timer at
// the requested FPS, fans out ticks to subscribers. Input map + scene
// graph are concurrent dictionaries.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Games;

public sealed class TimerGameLoop : IGameLoop
{
    private readonly List<Func<GameTick, ValueTask>> _subs = new();
    private readonly object _lock = new();
    private Timer? _timer;
    private int _frame;
    private DateTime _start;

    public string BackendId => "timer";

    public Task StartAsync(double targetFps = 60, CancellationToken ct = default)
    {
        if (targetFps <= 0) throw new ArgumentOutOfRangeException(nameof(targetFps));
        if (_timer is not null) throw new InvalidOperationException("already started");
        var ms = Math.Max(1, (int)(1000.0 / targetFps));
        _start = DateTime.UtcNow;
        _timer = new Timer(_ => OnTick(), null, ms, ms);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        _timer?.Dispose();
        _timer = null;
        return Task.CompletedTask;
    }

    public IDisposable Subscribe(Func<GameTick, ValueTask> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_lock) _subs.Add(handler);
        return new Token(this, handler);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private void OnTick()
    {
        var frame = Interlocked.Increment(ref _frame);
        var tick = new GameTick(frame, DateTime.UtcNow - _start);
        Func<GameTick, ValueTask>[] snap;
        lock (_lock) snap = _subs.ToArray();
        foreach (var s in snap)
        {
            try { _ = s(tick); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[CircleAI.Games] tick subscriber threw: {ex.Message}"); }
        }
    }

    private sealed class Token : IDisposable
    {
        private readonly TimerGameLoop _o; private readonly Func<GameTick, ValueTask> _h;
        public Token(TimerGameLoop o, Func<GameTick, ValueTask> h) { _o = o; _h = h; }
        public void Dispose() { lock (_o._lock) _o._subs.Remove(_h); }
    }
}

public sealed class InMemoryInputMap : IInputMap
{
    private readonly List<Func<InputEvent, ValueTask>> _subs = new();
    private readonly object _lock = new();

    public string BackendId => "in-memory";

    public void Raise(InputEvent ev)
    {
        ArgumentNullException.ThrowIfNull(ev);
        Func<InputEvent, ValueTask>[] snap;
        lock (_lock) snap = _subs.ToArray();
        foreach (var s in snap)
        {
            try { _ = s(ev); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[CircleAI.Games] input subscriber threw: {ex.Message}"); }
        }
    }

    public IDisposable Subscribe(Func<InputEvent, ValueTask> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_lock) _subs.Add(handler);
        return new Token(this, handler);
    }

    private sealed class Token : IDisposable
    {
        private readonly InMemoryInputMap _o; private readonly Func<InputEvent, ValueTask> _h;
        public Token(InMemoryInputMap o, Func<InputEvent, ValueTask> h) { _o = o; _h = h; }
        public void Dispose() { lock (_o._lock) _o._subs.Remove(_h); }
    }
}

public sealed class InMemorySceneGraph : ISceneGraph
{
    private readonly ConcurrentDictionary<string, SceneNode> _nodes = new(StringComparer.Ordinal);
    public string BackendId => "in-memory";

    public ValueTask AddAsync(SceneNode node, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (string.IsNullOrWhiteSpace(node.NodeId)) throw new ArgumentException("NodeId required");
        _nodes[node.NodeId] = node;
        return ValueTask.CompletedTask;
    }

    public ValueTask RemoveAsync(string nodeId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(nodeId)) throw new ArgumentException("nodeId required", nameof(nodeId));
        _nodes.TryRemove(nodeId, out _);
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<SceneNode>> SnapshotAsync(CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<SceneNode>>(_nodes.Values.ToArray());
}
