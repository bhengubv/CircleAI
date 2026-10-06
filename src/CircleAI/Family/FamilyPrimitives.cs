// FamilyPrimitives.cs — (3.3.0)
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Family;

public sealed record FamilyMember(string MemberId, string Name, string Role, DateTime DateOfBirth);
public sealed record FamilyEvent(string EventId, string Title, DateTimeOffset AtUtc, IReadOnlyList<string> MemberIds);
public sealed record SharedExpense(string ExpenseId, string PaidById, decimal Amount, string Currency, string Category, DateTimeOffset AtUtc);

public interface IFamilyBoard
{
    void Add(FamilyMember m);
    FamilyMember? GetMember(string id);
    IReadOnlyList<FamilyMember> Members { get; }
    void Schedule(FamilyEvent e);
    IReadOnlyList<FamilyEvent> EventsForMember(string memberId);
    void Record(SharedExpense e);
    decimal TotalPaidBy(string memberId, DateTimeOffset since);
    decimal SpendByCategory(string category, DateTimeOffset since);
}

public sealed class InMemoryFamilyBoard : IFamilyBoard
{
    private readonly ConcurrentDictionary<string, FamilyMember> _members = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, FamilyEvent> _events = new(StringComparer.Ordinal);
    private readonly List<SharedExpense> _expenses = new();
    private readonly object _lock = new();

    public void Add(FamilyMember m) { ArgumentNullException.ThrowIfNull(m); _members[m.MemberId] = m; }
    public FamilyMember? GetMember(string id) => _members.GetValueOrDefault(id);
    public IReadOnlyList<FamilyMember> Members => _members.Values.OrderBy(m => m.Name).ToArray();
    public void Schedule(FamilyEvent e) { ArgumentNullException.ThrowIfNull(e); _events[e.EventId] = e; }
    public IReadOnlyList<FamilyEvent> EventsForMember(string memberId)
        => _events.Values.Where(e => e.MemberIds.Contains(memberId)).OrderBy(e => e.AtUtc).ToArray();
    public void Record(SharedExpense e) { ArgumentNullException.ThrowIfNull(e); lock (_lock) _expenses.Add(e); }
    public decimal TotalPaidBy(string memberId, DateTimeOffset since)
    { lock (_lock) return _expenses.Where(e => e.PaidById == memberId && e.AtUtc >= since).Sum(e => e.Amount); }
    public decimal SpendByCategory(string category, DateTimeOffset since)
    { lock (_lock) return _expenses.Where(e => string.Equals(e.Category, category, StringComparison.OrdinalIgnoreCase) && e.AtUtc >= since).Sum(e => e.Amount); }
}
