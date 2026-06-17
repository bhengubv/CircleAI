// Contracts.cs
//
// (2.6.0) Observation-loop contracts. Pattern-port of bhengubv/Observer
// (AGPL upstream → Apache 2.0 fresh write so CircleAI stays license-clean).

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Observer;

/// <summary>One snapshot from one sensor.</summary>
public sealed record SensorReading(
    string                              SensorId,
    string                              Kind,
    DateTimeOffset                      CapturedAtUtc,
    IReadOnlyDictionary<string, string> Values,
    ReadOnlyMemory<byte>?               Payload = null);

/// <summary>(2.6.0) A single perception source — camera / mic / GPS / phone-state / accelerometer.</summary>
public interface ISensor : IAsyncDisposable
{
    string SensorId  { get; }
    string Kind      { get; }
    string BackendId { get; }

    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);

    IDisposable Subscribe(Func<SensorReading, ValueTask> handler);
}

/// <summary>One tool the observer can invoke during its act tick.</summary>
public sealed record ObservationTool(
    string                ToolId,
    string                Description,
    IReadOnlyList<string> Tags,
    Func<IReadOnlyDictionary<string, string>, CancellationToken, ValueTask<string>> Invoke);

/// <summary>(2.6.0) Registry of tools available to the observation loop.</summary>
public interface IObservationToolbox
{
    string BackendId { get; }

    void RegisterTool(ObservationTool tool);
    bool TryGet(string toolId, out ObservationTool? tool);
    IReadOnlyList<ObservationTool> ListTools();
}

/// <summary>One loop tick — what was perceived, what was decided, what was done.</summary>
public sealed record ObservationTick(
    DateTimeOffset            AtUtc,
    IReadOnlyList<SensorReading> Perceived,
    string                    Reasoning,
    IReadOnlyList<string>     ToolsInvoked);

/// <summary>(2.6.0) The perceive-reason-act loop itself.</summary>
public interface IObservationLoop : IAsyncDisposable
{
    string BackendId { get; }

    Task StartAsync(TimeSpan tickInterval, CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);

    IDisposable Subscribe(Func<ObservationTick, ValueTask> handler);
}
