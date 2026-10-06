// Contracts.cs — (2.9.0) DepBot contracts.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.DepBot;

public sealed record Dependency(string Ecosystem, string Name, string CurrentVersion, string? LatestVersion);
public sealed record DependencyUpdate(string Ecosystem, string Name, string FromVersion, string ToVersion, bool IsBreaking);

public interface IDependencyAnalyzer
{
    string BackendId { get; }
    ValueTask<IReadOnlyList<Dependency>> ScanAsync(string repoPath, CancellationToken ct = default);
}

public interface IDependencyUpdater
{
    string BackendId { get; }
    ValueTask<IReadOnlyList<DependencyUpdate>> ProposeUpdatesAsync(string repoPath, CancellationToken ct = default);
    ValueTask ApplyUpdateAsync(string repoPath, DependencyUpdate update, CancellationToken ct = default);
}
