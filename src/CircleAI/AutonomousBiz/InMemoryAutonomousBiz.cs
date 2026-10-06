// InMemoryAutonomousBiz.cs
//
// (3.3.0) Real in-memory treasury / revenue-loop / decision-log
// implementations. Treasury maintains a running balance from revenue
// events; revenue loop is a fan-out pub/sub with a kept history;
// decision log is an append-only list.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.AutonomousBiz;

public sealed class InMemoryRevenueLoop : IRevenueLoop
{
    private readonly List<RevenueEvent> _history = new();
    private readonly List<Func<RevenueEvent, ValueTask>> _subs = new();
    private readonly object _lock = new();

    public string BackendId => "in-memory";

    public void Publish(RevenueEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        Func<RevenueEvent, ValueTask>[] snap;
        lock (_lock) { _history.Add(e); snap = _subs.ToArray(); }
        foreach (var s in snap)
        {
            try { _ = s(e); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[CircleAI.AutonomousBiz] revenue subscriber threw: {ex.Message}"); }
        }
    }

    public IDisposable Subscribe(Func<RevenueEvent, ValueTask> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_lock) _subs.Add(handler);
        return new Token(this, handler);
    }

    public ValueTask<IReadOnlyList<RevenueEvent>> ReadAsync(DateTimeOffset since, CancellationToken ct = default)
    {
        lock (_lock)
        {
            return ValueTask.FromResult<IReadOnlyList<RevenueEvent>>(
                _history.Where(e => e.AtUtc >= since).ToArray());
        }
    }

    private sealed class Token : IDisposable
    {
        private readonly InMemoryRevenueLoop _o; private readonly Func<RevenueEvent, ValueTask> _h;
        public Token(InMemoryRevenueLoop o, Func<RevenueEvent, ValueTask> h) { _o = o; _h = h; }
        public void Dispose() { lock (_o._lock) _o._subs.Remove(_h); }
    }
}

public sealed class InMemoryTreasury : ITreasury
{
    private readonly IRevenueLoop _loop;
    private readonly string _currency;

    public InMemoryTreasury(IRevenueLoop loop, string currency = "ZAR")
    {
        _loop = loop ?? throw new ArgumentNullException(nameof(loop));
        _currency = currency;
    }

    public string BackendId => "in-memory";

    public async ValueTask<TreasurySnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        var events = await _loop.ReadAsync(DateTimeOffset.MinValue, ct).ConfigureAwait(false);
        var bal = events.Where(e => string.Equals(e.Currency, _currency, StringComparison.OrdinalIgnoreCase))
                        .Sum(e => e.Amount);
        return new TreasurySnapshot(bal, _currency, DateTimeOffset.UtcNow);
    }
}

public sealed class InMemoryDecisionLog : IDecisionLog
{
    private readonly List<AutonomousDecision> _items = new();
    private readonly object _lock = new();
    public string BackendId => "in-memory";

    public ValueTask AppendAsync(AutonomousDecision d, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(d);
        lock (_lock) _items.Add(d);
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<AutonomousDecision>> ReadAsync(int limit = 100, CancellationToken ct = default)
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        lock (_lock)
        {
            return ValueTask.FromResult<IReadOnlyList<AutonomousDecision>>(
                _items.OrderByDescending(d => d.AtUtc).Take(limit).ToArray());
        }
    }
}
