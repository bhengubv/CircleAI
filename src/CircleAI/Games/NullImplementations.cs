// NullImplementations.cs — (3.0.0)

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Games;

public sealed class NullGameLoop : IGameLoop
{
    public string BackendId => "null";
    public Task StartAsync(double fps = 60, CancellationToken ct = default) => Task.CompletedTask;
    public Task StopAsync(CancellationToken ct = default)                   => Task.CompletedTask;
    public IDisposable Subscribe(Func<GameTick, ValueTask> h)               => EmptyDisposable.Instance;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class EmptyDisposable : IDisposable
    {
        public static readonly EmptyDisposable Instance = new();
        public void Dispose() { }
    }
}

public sealed class NullInputMap : IInputMap
{
    public static readonly NullInputMap Instance = new();
    public string BackendId => "null";
    public IDisposable Subscribe(Func<InputEvent, ValueTask> h) => EmptyDisposable.Instance;

    private sealed class EmptyDisposable : IDisposable
    {
        public static readonly EmptyDisposable Instance = new();
        public void Dispose() { }
    }
}

public sealed class NullSceneGraph : ISceneGraph
{
    public static readonly NullSceneGraph Instance = new();
    public string BackendId => "null";
    public ValueTask AddAsync(SceneNode n, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask RemoveAsync(string id, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask<IReadOnlyList<SceneNode>> SnapshotAsync(CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<SceneNode>>(Array.Empty<SceneNode>());
}
