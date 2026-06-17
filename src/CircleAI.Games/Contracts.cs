// Contracts.cs — (3.0.0) Game-runtime contracts.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Games;

public sealed record GameTick(int Frame, TimeSpan Elapsed);
public sealed record InputEvent(string Action, IReadOnlyDictionary<string, string>? Payload = null);
public sealed record SceneNode(string NodeId, string Kind, double X, double Y, double Z);

public interface IGameLoop : IAsyncDisposable
{
    string BackendId { get; }
    Task StartAsync(double targetFps = 60, CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    IDisposable Subscribe(Func<GameTick, ValueTask> handler);
}

public interface IInputMap
{
    string BackendId { get; }
    IDisposable Subscribe(Func<InputEvent, ValueTask> handler);
}

public interface ISceneGraph
{
    string BackendId { get; }
    ValueTask AddAsync(SceneNode node, CancellationToken ct = default);
    ValueTask RemoveAsync(string nodeId, CancellationToken ct = default);
    ValueTask<IReadOnlyList<SceneNode>> SnapshotAsync(CancellationToken ct = default);
}
