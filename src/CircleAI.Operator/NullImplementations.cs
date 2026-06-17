// NullImplementations.cs
//
// (2.7.0) In-proc defaults — no k8s reconciliation.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Operator;

public sealed class NullModelOperator : IModelOperator
{
    public static readonly NullModelOperator Instance = new();
    public string BackendId => "null";
    public ValueTask ApplyAsync(ModelDeployment deployment, CancellationToken ct = default)  => ValueTask.CompletedTask;
    public ValueTask DeleteAsync(string modelId, string ns, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask<ModelStatus?> GetStatusAsync(string modelId, string ns, CancellationToken ct = default)
        => ValueTask.FromResult<ModelStatus?>(null);
}

public sealed class NullDeploymentObserver : IDeploymentObserver
{
    public static readonly NullDeploymentObserver Instance = new();
    public string BackendId => "null";
    public IDisposable Subscribe(Func<ModelStatus, ValueTask> h) => EmptyDisposable.Instance;

    private sealed class EmptyDisposable : IDisposable
    {
        public static readonly EmptyDisposable Instance = new();
        public void Dispose() { }
    }
}
