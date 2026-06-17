// Contracts.cs — (2.8.0) Durable workflow contracts.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Workflows;

public enum WorkflowPhase { Pending, Running, Suspended, Completed, Failed }

public sealed record WorkflowDefinition(string DefinitionId, string Name, string Version, string Description);

public sealed record WorkflowExecution(
    string         RunId,
    string         DefinitionId,
    WorkflowPhase  Phase,
    DateTimeOffset StartUtc,
    string?        FailureReason);

public sealed record CheckpointPayload(string RunId, string StepId, ReadOnlyMemory<byte> StateBlob);

public interface IWorkflowDefinitionStore
{
    string BackendId { get; }
    ValueTask UpsertAsync(WorkflowDefinition d, CancellationToken ct = default);
    ValueTask<WorkflowDefinition?> GetAsync(string id, CancellationToken ct = default);
}

public interface IWorkflowRunner
{
    string BackendId { get; }
    ValueTask<WorkflowExecution> StartAsync(string definitionId, IReadOnlyDictionary<string, object?>? inputs = null, CancellationToken ct = default);
    ValueTask<WorkflowExecution?> GetAsync(string runId, CancellationToken ct = default);
    ValueTask CancelAsync(string runId, CancellationToken ct = default);
}

public interface IWorkflowState
{
    string BackendId { get; }
    ValueTask CheckpointAsync(CheckpointPayload payload, CancellationToken ct = default);
    ValueTask<CheckpointPayload?> LoadAsync(string runId, string stepId, CancellationToken ct = default);
}
