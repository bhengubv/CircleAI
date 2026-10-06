// Contracts.cs — (3.0.0) Autonomous business contracts.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.AutonomousBiz;

public sealed record TreasurySnapshot(decimal Balance, string Currency, DateTimeOffset AtUtc);
public sealed record RevenueEvent(string EventId, decimal Amount, string Currency, string Source, DateTimeOffset AtUtc);
public sealed record AutonomousDecision(string DecisionId, string Rationale, string ChosenAction, DateTimeOffset AtUtc);

public interface ITreasury
{
    string BackendId { get; }
    ValueTask<TreasurySnapshot> GetSnapshotAsync(CancellationToken ct = default);
}

public interface IRevenueLoop
{
    string BackendId { get; }
    IDisposable Subscribe(Func<RevenueEvent, ValueTask> handler);
    ValueTask<IReadOnlyList<RevenueEvent>> ReadAsync(DateTimeOffset since, CancellationToken ct = default);
}

public interface IDecisionLog
{
    string BackendId { get; }
    ValueTask AppendAsync(AutonomousDecision d, CancellationToken ct = default);
    ValueTask<IReadOnlyList<AutonomousDecision>> ReadAsync(int limit = 100, CancellationToken ct = default);
}
