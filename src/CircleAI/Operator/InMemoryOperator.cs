// InMemoryOperator.cs
//
// (3.3.0) Real in-memory IModelOperator + IDeploymentObserver. Applies
// deployments through a lifecycle state machine
// (Pending → Downloading → Loading → Ready) and notifies subscribers
// on every phase transition. Hosts that integrate with real
// Kubernetes / kagent swap in a real implementation behind the same
// contract.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Operator;

/// <summary>(3.3.0) In-memory model deployment store + lifecycle observers.</summary>
public sealed class InMemoryModelOperator : IModelOperator, IDeploymentObserver
{
    private readonly ConcurrentDictionary<string, ModelStatus> _statuses = new(StringComparer.Ordinal);
    private readonly List<Func<ModelStatus, ValueTask>> _observers = new();
    private readonly object _obsLock = new();

    public string BackendId => "in-memory";

    public async ValueTask ApplyAsync(ModelDeployment deployment, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        if (string.IsNullOrWhiteSpace(deployment.ModelId))   throw new ArgumentException("ModelId required");
        if (string.IsNullOrWhiteSpace(deployment.Namespace)) throw new ArgumentException("Namespace required");
        if (deployment.Replicas < 0)                          throw new ArgumentOutOfRangeException(nameof(deployment));

        var key = Key(deployment.ModelId, deployment.Namespace);

        await TransitionAsync(key, deployment, ModelLifecyclePhase.Pending,     0, ct).ConfigureAwait(false);
        await TransitionAsync(key, deployment, ModelLifecyclePhase.Downloading, 0, ct).ConfigureAwait(false);
        await TransitionAsync(key, deployment, ModelLifecyclePhase.Loading,     0, ct).ConfigureAwait(false);
        await TransitionAsync(key, deployment, ModelLifecyclePhase.Ready, deployment.Replicas, ct).ConfigureAwait(false);
    }

    public ValueTask DeleteAsync(string modelId, string @namespace, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(modelId))   throw new ArgumentException("modelId required");
        if (string.IsNullOrWhiteSpace(@namespace)) throw new ArgumentException("namespace required");
        _statuses.TryRemove(Key(modelId, @namespace), out _);
        return ValueTask.CompletedTask;
    }

    public ValueTask<ModelStatus?> GetStatusAsync(string modelId, string @namespace, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(modelId))   throw new ArgumentException("modelId required");
        if (string.IsNullOrWhiteSpace(@namespace)) throw new ArgumentException("namespace required");
        _statuses.TryGetValue(Key(modelId, @namespace), out var s);
        return ValueTask.FromResult(s);
    }

    public IDisposable Subscribe(Func<ModelStatus, ValueTask> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_obsLock) _observers.Add(handler);
        return new ObserverToken(this, handler);
    }

    private async ValueTask TransitionAsync(string key, ModelDeployment d, ModelLifecyclePhase phase, int readyReplicas, CancellationToken ct)
    {
        var status = new ModelStatus(d.ModelId, d.Namespace, phase, readyReplicas, LastError: null);
        _statuses[key] = status;
        Func<ModelStatus, ValueTask>[] snap;
        lock (_obsLock) snap = _observers.ToArray();
        foreach (var o in snap)
        {
            try { await o(status).ConfigureAwait(false); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[CircleAI.Operator] deployment observer threw: {ex.Message}"); }
        }
    }

    private static string Key(string id, string ns) => $"{ns}/{id}";

    private sealed class ObserverToken : IDisposable
    {
        private readonly InMemoryModelOperator _o; private readonly Func<ModelStatus, ValueTask> _h;
        public ObserverToken(InMemoryModelOperator o, Func<ModelStatus, ValueTask> h) { _o = o; _h = h; }
        public void Dispose() { lock (_o._obsLock) _o._observers.Remove(_h); }
    }
}
