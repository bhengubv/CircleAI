// InMemoryBuildFarm.cs
//
// (3.3.0) Real in-memory build-farm primitives: agent pool, job runner
// (state machine: Pending → Running → Succeeded/Failed), artifact
// store. Hosts that integrate real CI swap in a real impl.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.BuildFarm;

public sealed class InMemoryBuildAgentPool : IBuildAgentPool
{
    private readonly ConcurrentDictionary<string, BuildAgent> _all = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte>       _busy = new(StringComparer.Ordinal);

    public string BackendId => "in-memory";

    public void Register(BuildAgent a) { ArgumentNullException.ThrowIfNull(a); _all[a.AgentId] = a; }

    public ValueTask<BuildAgent?> AcquireAsync(BuildAgentKind kind, CancellationToken ct = default)
    {
        foreach (var a in _all.Values.Where(x => x.Kind == kind))
        {
            if (_busy.TryAdd(a.AgentId, 0)) return ValueTask.FromResult<BuildAgent?>(a);
        }
        return ValueTask.FromResult<BuildAgent?>(null);
    }

    public ValueTask ReleaseAsync(string agentId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(agentId)) throw new ArgumentException("agentId required", nameof(agentId));
        _busy.TryRemove(agentId, out _);
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<BuildAgent>> ListAsync(CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<BuildAgent>>(_all.Values.ToArray());
}

public sealed class InMemoryBuildJobRunner : IBuildJobRunner
{
    private readonly ConcurrentDictionary<string, BuildJob> _jobs = new(StringComparer.Ordinal);
    private long _seq;

    public string BackendId => "in-memory";

    public ValueTask<BuildJob> StartAsync(string agentId, string repo, string branch, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(agentId)) throw new ArgumentException("agentId required");
        if (string.IsNullOrWhiteSpace(repo))    throw new ArgumentException("repo required");
        if (string.IsNullOrWhiteSpace(branch))  throw new ArgumentException("branch required");
        var jobId = $"job-{Interlocked.Increment(ref _seq)}";
        var job = new BuildJob(jobId, agentId, repo, branch, BuildJobPhase.Running, DateTimeOffset.UtcNow);
        _jobs[jobId] = job;
        return ValueTask.FromResult(job);
    }

    public ValueTask<BuildJob?> GetAsync(string jobId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(jobId)) throw new ArgumentException("jobId required", nameof(jobId));
        _jobs.TryGetValue(jobId, out var j);
        return ValueTask.FromResult(j);
    }

    public void Complete(string jobId, bool success)
    {
        if (!_jobs.TryGetValue(jobId, out var j)) throw new InvalidOperationException($"Unknown job {jobId}");
        _jobs[jobId] = j with { Phase = success ? BuildJobPhase.Succeeded : BuildJobPhase.Failed };
    }
}

public sealed class InMemoryBuildArtifactStore : IBuildArtifactStore
{
    private readonly ConcurrentDictionary<string, BuildArtifact> _items = new(StringComparer.Ordinal);
    public string BackendId => "in-memory";

    public ValueTask SaveAsync(BuildArtifact artifact, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (string.IsNullOrWhiteSpace(artifact.ArtifactId)) throw new ArgumentException("ArtifactId required");
        _items[artifact.ArtifactId] = artifact;
        return ValueTask.CompletedTask;
    }

    public ValueTask<BuildArtifact?> GetAsync(string artifactId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(artifactId)) throw new ArgumentException("artifactId required", nameof(artifactId));
        _items.TryGetValue(artifactId, out var a);
        return ValueTask.FromResult(a);
    }
}
