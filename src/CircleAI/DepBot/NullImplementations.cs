// NullImplementations.cs — (2.9.0)

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.DepBot;

public sealed class NullDependencyAnalyzer : IDependencyAnalyzer
{
    public static readonly NullDependencyAnalyzer Instance = new();
    public string BackendId => "null";
    public ValueTask<IReadOnlyList<Dependency>> ScanAsync(string repo, CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<Dependency>>(Array.Empty<Dependency>());
}

public sealed class NullDependencyUpdater : IDependencyUpdater
{
    public static readonly NullDependencyUpdater Instance = new();
    public string BackendId => "null";
    public ValueTask<IReadOnlyList<DependencyUpdate>> ProposeUpdatesAsync(string repo, CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<DependencyUpdate>>(Array.Empty<DependencyUpdate>());
    public ValueTask ApplyUpdateAsync(string repo, DependencyUpdate u, CancellationToken ct = default) => ValueTask.CompletedTask;
}
