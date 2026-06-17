// NullImplementations.cs — (2.8.0)

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Workflows;

public sealed class NullWorkflowDefinitionStore : IWorkflowDefinitionStore
{
    public static readonly NullWorkflowDefinitionStore Instance = new();
    public string BackendId => "null";
    public ValueTask UpsertAsync(WorkflowDefinition d, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask<WorkflowDefinition?> GetAsync(string id, CancellationToken ct = default) => ValueTask.FromResult<WorkflowDefinition?>(null);
}

public sealed class NullWorkflowRunner : IWorkflowRunner
{
    public static readonly NullWorkflowRunner Instance = new();
    public string BackendId => "null";
    public ValueTask<WorkflowExecution> StartAsync(string id, IReadOnlyDictionary<string, object?>? inputs = null, CancellationToken ct = default)
        => ValueTask.FromResult(new WorkflowExecution(Guid.Empty.ToString(), id, WorkflowPhase.Failed, DateTimeOffset.MinValue, "NullWorkflowRunner"));
    public ValueTask<WorkflowExecution?> GetAsync(string runId, CancellationToken ct = default) => ValueTask.FromResult<WorkflowExecution?>(null);
    public ValueTask CancelAsync(string runId, CancellationToken ct = default) => ValueTask.CompletedTask;
}

public sealed class NullWorkflowState : IWorkflowState
{
    public static readonly NullWorkflowState Instance = new();
    public string BackendId => "null";
    public ValueTask CheckpointAsync(CheckpointPayload p, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask<CheckpointPayload?> LoadAsync(string runId, string stepId, CancellationToken ct = default)
        => ValueTask.FromResult<CheckpointPayload?>(null);
}
