// Contracts.cs — (2.9.0) Build-farm contracts.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.BuildFarm;

public enum BuildAgentKind { Linux, Mac, Windows, Android, Ios }
public enum BuildJobPhase  { Pending, Running, Succeeded, Failed }

public sealed record BuildAgent(string AgentId, BuildAgentKind Kind, string Os, string? Hardware);
public sealed record BuildJob(string JobId, string AgentId, string Repo, string Branch, BuildJobPhase Phase, DateTimeOffset StartUtc);
public sealed record BuildArtifact(string ArtifactId, string JobId, string Name, ReadOnlyMemory<byte> Payload);

public interface IBuildAgentPool
{
    string BackendId { get; }
    ValueTask<BuildAgent?> AcquireAsync(BuildAgentKind kind, CancellationToken ct = default);
    ValueTask ReleaseAsync(string agentId, CancellationToken ct = default);
    ValueTask<IReadOnlyList<BuildAgent>> ListAsync(CancellationToken ct = default);
}

public interface IBuildJobRunner
{
    string BackendId { get; }
    ValueTask<BuildJob> StartAsync(string agentId, string repo, string branch, CancellationToken ct = default);
    ValueTask<BuildJob?> GetAsync(string jobId, CancellationToken ct = default);
}

public interface IBuildArtifactStore
{
    string BackendId { get; }
    ValueTask SaveAsync(BuildArtifact artifact, CancellationToken ct = default);
    ValueTask<BuildArtifact?> GetAsync(string artifactId, CancellationToken ct = default);
}
