// Contracts.cs
//
// (2.7.0) Kubernetes-operator contracts. kagent-pattern.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Operator;

public enum ModelLifecyclePhase { Pending, Downloading, Loading, Ready, Brownout, Unloading, Failed }

public sealed record ModelDeployment(
    string ModelId,
    string Namespace,
    int    Replicas,
    string TargetTierLabel);

public sealed record ModelStatus(
    string             ModelId,
    string             Namespace,
    ModelLifecyclePhase Phase,
    int                ReadyReplicas,
    string?            LastError);

/// <summary>(2.7.0) Reconcile model deployments against CRDs.</summary>
public interface IModelOperator
{
    string BackendId { get; }

    ValueTask ApplyAsync(ModelDeployment deployment, CancellationToken ct = default);
    ValueTask DeleteAsync(string modelId, string @namespace, CancellationToken ct = default);
    ValueTask<ModelStatus?> GetStatusAsync(string modelId, string @namespace, CancellationToken ct = default);
}

/// <summary>(2.7.0) Lifecycle observer — fire when phase changes.</summary>
public interface IDeploymentObserver
{
    string BackendId { get; }

    IDisposable Subscribe(Func<ModelStatus, ValueTask> handler);
}
