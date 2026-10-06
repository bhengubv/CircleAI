// FinancePrimitives.cs — (3.3.0)
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Commerce.Finance;

public sealed record InvoiceLine(string Description, decimal Amount, double TaxPct);
public sealed record Invoice(string InvoiceId, string CustomerId, DateTime IssueDate, DateTime DueDate, IReadOnlyList<InvoiceLine> Lines, string Currency, string Status);
public sealed record FinancePayment(string PaymentId, string InvoiceId, decimal Amount, DateTimeOffset AtUtc);

public interface IInvoiceBoard
{
    void Issue(Invoice i);
    Invoice? Get(string invoiceId);
    void RecordPayment(FinancePayment p);
    void MarkOverdue(DateTime asOf);
    decimal RemainingOn(string invoiceId);
    decimal TotalOutstanding();
    IReadOnlyList<Invoice> Overdue();
}

public sealed class InMemoryInvoiceBoard : IInvoiceBoard
{
    private readonly ConcurrentDictionary<string, Invoice> _invoices = new(StringComparer.Ordinal);
    private readonly List<FinancePayment> _payments = new();
    private readonly object _lock = new();

    public void Issue(Invoice i) { ArgumentNullException.ThrowIfNull(i); _invoices[i.InvoiceId] = i; }
    public Invoice? Get(string invoiceId) => _invoices.GetValueOrDefault(invoiceId);
    public void RecordPayment(FinancePayment p) { ArgumentNullException.ThrowIfNull(p); lock (_lock) _payments.Add(p); }
    public void MarkOverdue(DateTime asOf)
    {
        foreach (var i in _invoices.Values.Where(i => i.DueDate < asOf && !string.Equals(i.Status, "Paid", StringComparison.OrdinalIgnoreCase)))
            _invoices[i.InvoiceId] = i with { Status = "Overdue" };
    }
    public decimal RemainingOn(string invoiceId)
    {
        if (!_invoices.TryGetValue(invoiceId, out var inv)) return 0;
        var billed = inv.Lines.Sum(l => l.Amount * (decimal)(1 + l.TaxPct / 100.0));
        decimal paid;
        lock (_lock) paid = _payments.Where(p => p.InvoiceId == invoiceId).Sum(p => p.Amount);
        return billed - paid;
    }
    public decimal TotalOutstanding() => _invoices.Keys.Sum(id => RemainingOn(id));
    public IReadOnlyList<Invoice> Overdue() => _invoices.Values.Where(i => string.Equals(i.Status, "Overdue", StringComparison.OrdinalIgnoreCase)).ToArray();
}
