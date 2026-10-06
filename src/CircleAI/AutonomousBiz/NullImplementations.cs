// NullImplementations.cs — (3.0.0)

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.AutonomousBiz;

public sealed class NullTreasury : ITreasury
{
    public static readonly NullTreasury Instance = new();
    public string BackendId => "null";
    public ValueTask<TreasurySnapshot> GetSnapshotAsync(CancellationToken ct = default)
        => ValueTask.FromResult(new TreasurySnapshot(0m, "ZAR", DateTimeOffset.MinValue));
}

public sealed class NullRevenueLoop : IRevenueLoop
{
    public static readonly NullRevenueLoop Instance = new();
    public string BackendId => "null";
    public IDisposable Subscribe(Func<RevenueEvent, ValueTask> h) => EmptyDisposable.Instance;
    public ValueTask<IReadOnlyList<RevenueEvent>> ReadAsync(DateTimeOffset since, CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<RevenueEvent>>(Array.Empty<RevenueEvent>());

    private sealed class EmptyDisposable : IDisposable
    {
        public static readonly EmptyDisposable Instance = new();
        public void Dispose() { }
    }
}

public sealed class NullDecisionLog : IDecisionLog
{
    public static readonly NullDecisionLog Instance = new();
    public string BackendId => "null";
    public ValueTask AppendAsync(AutonomousDecision d, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask<IReadOnlyList<AutonomousDecision>> ReadAsync(int limit = 100, CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<AutonomousDecision>>(Array.Empty<AutonomousDecision>());
}
