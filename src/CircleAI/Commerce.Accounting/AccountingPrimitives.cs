// AccountingPrimitives.cs — (3.3.0)
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Commerce.Accounting;

public sealed record AccountingEntry(string EntryId, DateTime AtUtc, string AccountCode, decimal DebitAmount, decimal CreditAmount, string Memo);
public sealed record TaxRate(string Code, double Percentage);
public sealed record Period(int Year, int Month);

public interface IAccountingBoard
{
    void Post(AccountingEntry e);
    void DefineTax(TaxRate r);
    TaxRate? GetTax(string code);
    decimal AccountBalance(string accountCode);
    decimal Sum(string accountCode, Period p);
    IReadOnlyList<AccountingEntry> ForAccount(string accountCode, Period p);
    decimal NetProfit(Period p, string revenueAccount, string expenseAccount);
}

public sealed class InMemoryAccountingBoard : IAccountingBoard
{
    private readonly List<AccountingEntry> _entries = new();
    private readonly ConcurrentDictionary<string, TaxRate> _tax = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public void Post(AccountingEntry e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.DebitAmount < 0 || e.CreditAmount < 0) throw new ArgumentException("amounts must be non-negative");
        lock (_lock) _entries.Add(e);
    }
    public void DefineTax(TaxRate r) { ArgumentNullException.ThrowIfNull(r); _tax[r.Code] = r; }
    public TaxRate? GetTax(string code) => _tax.GetValueOrDefault(code);
    public decimal AccountBalance(string accountCode)
    { lock (_lock) return _entries.Where(e => e.AccountCode == accountCode).Sum(e => e.DebitAmount - e.CreditAmount); }
    public decimal Sum(string accountCode, Period p)
    { lock (_lock) return _entries.Where(e => e.AccountCode == accountCode && e.AtUtc.Year == p.Year && e.AtUtc.Month == p.Month).Sum(e => e.DebitAmount - e.CreditAmount); }
    public IReadOnlyList<AccountingEntry> ForAccount(string accountCode, Period p)
    { lock (_lock) return _entries.Where(e => e.AccountCode == accountCode && e.AtUtc.Year == p.Year && e.AtUtc.Month == p.Month).OrderBy(e => e.AtUtc).ToArray(); }
    public decimal NetProfit(Period p, string revenueAccount, string expenseAccount)
        => Sum(revenueAccount, p) - Sum(expenseAccount, p);
}
