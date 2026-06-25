// PersonalFinancePrimitives.cs
//
// (3.3.0) Real domain types + in-memory store for personal finance:
// accounts, transactions, budgets, simple monthly summary.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Personal.Finance;

public sealed record Account(string AccountId, string Name, decimal Balance, string Currency);
public sealed record FinanceTransaction(string TxId, string AccountId, decimal Amount, string Category, string? Note, DateTimeOffset AtUtc);
public sealed record BudgetLine(string Category, decimal MonthlyLimit);
public sealed record MonthSummary(int Year, int Month, decimal TotalIn, decimal TotalOut, IReadOnlyDictionary<string, decimal> ByCategory);

public interface IPersonalFinanceBoard
{
    void Upsert(Account a);
    Account? GetAccount(string id);
    void Record(FinanceTransaction t);
    IReadOnlyList<FinanceTransaction> ListForMonth(string accountId, int year, int month);
    void SetBudget(BudgetLine b);
    IReadOnlyList<BudgetLine> Budgets { get; }
    MonthSummary Summarise(string accountId, int year, int month);
}

public sealed class InMemoryPersonalFinanceBoard : IPersonalFinanceBoard
{
    private readonly ConcurrentDictionary<string, Account> _accounts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, BudgetLine> _budgets = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FinanceTransaction> _txns = new();
    private readonly object _lock = new();

    public void Upsert(Account a) { ArgumentNullException.ThrowIfNull(a); _accounts[a.AccountId] = a; }
    public Account? GetAccount(string id) => _accounts.GetValueOrDefault(id);

    public void Record(FinanceTransaction t)
    {
        ArgumentNullException.ThrowIfNull(t);
        if (!_accounts.ContainsKey(t.AccountId)) throw new InvalidOperationException($"Unknown account {t.AccountId}");
        lock (_lock)
        {
            _txns.Add(t);
            var a = _accounts[t.AccountId];
            _accounts[t.AccountId] = a with { Balance = a.Balance + t.Amount };
        }
    }

    public IReadOnlyList<FinanceTransaction> ListForMonth(string accountId, int year, int month)
    {
        lock (_lock)
        {
            return _txns.Where(t => t.AccountId == accountId && t.AtUtc.Year == year && t.AtUtc.Month == month).ToArray();
        }
    }

    public void SetBudget(BudgetLine b) { ArgumentNullException.ThrowIfNull(b); _budgets[b.Category] = b; }
    public IReadOnlyList<BudgetLine> Budgets => _budgets.Values.OrderBy(b => b.Category).ToArray();

    public MonthSummary Summarise(string accountId, int year, int month)
    {
        var rows = ListForMonth(accountId, year, month);
        var byCat = rows.GroupBy(t => t.Category).ToDictionary(g => g.Key, g => g.Sum(t => t.Amount));
        var inSum = rows.Where(t => t.Amount > 0).Sum(t => t.Amount);
        var outSum = -rows.Where(t => t.Amount < 0).Sum(t => t.Amount);
        return new MonthSummary(year, month, inSum, outSum, byCat);
    }
}
