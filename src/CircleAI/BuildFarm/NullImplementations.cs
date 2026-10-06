// NullImplementations.cs — (2.9.0)

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.BuildFarm;

public sealed class NullBuildAgentPool : IBuildAgentPool
{
    public static readonly NullBuildAgentPool Instance = new();
    public string BackendId => "null";
    public ValueTask<BuildAgent?> AcquireAsync(BuildAgentKind k, CancellationToken ct = default) => ValueTask.FromResult<BuildAgent?>(null);
    public ValueTask ReleaseAsync(string id, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask<IReadOnlyList<BuildAgent>> ListAsync(CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<BuildAgent>>(Array.Empty<BuildAgent>());
}

public sealed class NullBuildJobRunner : IBuildJobRunner
{
    public static readonly NullBuildJobRunner Instance = new();
    public string BackendId => "null";
    public ValueTask<BuildJob> StartAsync(string a, string r, string b, CancellationToken ct = default)
        => ValueTask.FromResult(new BuildJob(Guid.Empty.ToString(), a, r, b, BuildJobPhase.Failed, DateTimeOffset.MinValue));
    public ValueTask<BuildJob?> GetAsync(string j, CancellationToken ct = default) => ValueTask.FromResult<BuildJob?>(null);
}

public sealed class NullBuildArtifactStore : IBuildArtifactStore
{
    public static readonly NullBuildArtifactStore Instance = new();
    public string BackendId => "null";
    public ValueTask SaveAsync(BuildArtifact a, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask<BuildArtifact?> GetAsync(string id, CancellationToken ct = default) => ValueTask.FromResult<BuildArtifact?>(null);
}
