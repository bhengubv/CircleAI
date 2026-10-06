// LegalPrimitives.cs
//
// (3.3.0) Real domain types + in-memory store for the Legal vertical:
// matters, contracts, deadlines, clause library.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Legal;

public sealed record Matter(string MatterId, string Title, string Jurisdiction, string Client, DateTimeOffset OpenedAtUtc, bool Open);
public sealed record Contract(string ContractId, string MatterId, string Title, DateTime EffectiveDate, DateTime? ExpiryDate, IReadOnlyList<string> Counterparties);
public sealed record LegalDeadline(string DeadlineId, string MatterId, string Description, DateTime DueOn);
public sealed record Clause(string ClauseId, string Title, string Body, IReadOnlyList<string> Tags);

public interface ILegalBoard
{
    void Open(Matter m);
    void Close(string matterId);
    Matter? GetMatter(string id);
    IReadOnlyList<Matter> ActiveMatters { get; }
    void AddContract(Contract c);
    IReadOnlyList<Contract> ContractsExpiringBefore(DateTime date);
    void Add(LegalDeadline d);
    IReadOnlyList<LegalDeadline> UpcomingDeadlines(DateTime now);
    void AddClause(Clause c);
    IReadOnlyList<Clause> ClausesByTag(string tag);
}

public sealed class InMemoryLegalBoard : ILegalBoard
{
    private readonly ConcurrentDictionary<string, Matter> _matters = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Contract> _contracts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, LegalDeadline> _deadlines = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Clause> _clauses = new(StringComparer.Ordinal);

    public void Open(Matter m) { ArgumentNullException.ThrowIfNull(m); _matters[m.MatterId] = m; }

    public void Close(string matterId)
    {
        if (!_matters.TryGetValue(matterId, out var m)) throw new InvalidOperationException($"Unknown matter {matterId}");
        _matters[matterId] = m with { Open = false };
    }

    public Matter? GetMatter(string id) => _matters.GetValueOrDefault(id);
    public IReadOnlyList<Matter> ActiveMatters => _matters.Values.Where(m => m.Open).OrderByDescending(m => m.OpenedAtUtc).ToArray();

    public void AddContract(Contract c) { ArgumentNullException.ThrowIfNull(c); _contracts[c.ContractId] = c; }
    public IReadOnlyList<Contract> ContractsExpiringBefore(DateTime date)
        => _contracts.Values.Where(c => c.ExpiryDate.HasValue && c.ExpiryDate.Value <= date).OrderBy(c => c.ExpiryDate).ToArray();

    public void Add(LegalDeadline d) { ArgumentNullException.ThrowIfNull(d); _deadlines[d.DeadlineId] = d; }
    public IReadOnlyList<LegalDeadline> UpcomingDeadlines(DateTime now)
        => _deadlines.Values.Where(d => d.DueOn >= now).OrderBy(d => d.DueOn).ToArray();

    public void AddClause(Clause c) { ArgumentNullException.ThrowIfNull(c); _clauses[c.ClauseId] = c; }
    public IReadOnlyList<Clause> ClausesByTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) throw new ArgumentException("tag required", nameof(tag));
        return _clauses.Values.Where(c => c.Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))).ToArray();
    }
}
