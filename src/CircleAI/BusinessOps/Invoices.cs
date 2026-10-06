// Invoices.cs — (0.1.0) Invoicing: models, lifecycle service, numbering, PDF seam.
//
// The model carries the accounts-receivable lifecycle a small business actually
// runs: draft -> sent -> (partially) paid, or overdue, or cancelled. Totals and
// tax are computed from the lines so a caller can never hold an invoice whose
// header disagrees with its arithmetic. All money is currency-checked.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.BusinessOps;

/// <summary>Where an invoice sits in the accounts-receivable lifecycle.</summary>
public enum InvoiceStatus
{
    /// <summary>Being prepared; not yet sent, no number required.</summary>
    Draft = 0,
    /// <summary>Issued to the client, awaiting payment.</summary>
    Sent,
    /// <summary>Some payment received; a balance remains.</summary>
    PartiallyPaid,
    /// <summary>Settled in full.</summary>
    Paid,
    /// <summary>Past its due date and not fully paid.</summary>
    Overdue,
    /// <summary>Voided; never collected.</summary>
    Cancelled,
}

/// <summary>
/// One line on an invoice. Tax is per-line so mixed-rate invoices (e.g. some
/// zero-rated items) are expressible. <paramref name="TaxRate"/> is a fraction:
/// <c>0.15m</c> == 15% (South African VAT), <c>0m</c> == zero-rated.
/// </summary>
public sealed record InvoiceLine(
    string Description,
    decimal Quantity,
    Money UnitPrice,
    decimal TaxRate = 0m)
{
    /// <summary>Quantity × unit price, before tax.</summary>
    public Money LineSubtotal => (UnitPrice * Quantity).Round();

    /// <summary>Tax on this line.</summary>
    public Money LineTax => (UnitPrice * Quantity * TaxRate).Round();

    /// <summary>Subtotal + tax.</summary>
    public Money LineTotal => LineSubtotal + LineTax;
}

/// <summary>
/// An invoice. Header amounts (<see cref="Subtotal"/>, <see cref="TaxTotal"/>,
/// <see cref="Total"/>, <see cref="BalanceDue"/>) are DERIVED from the lines and
/// payments — there is no stored total to drift out of sync.
/// </summary>
public sealed record Invoice
{
    /// <summary>Stable internal id.</summary>
    public required string InvoiceId { get; init; }

    /// <summary>Human-facing number (e.g. "INV-2026-0001"), assigned when issued.</summary>
    public string? Number { get; init; }

    /// <summary>The client being billed (<see cref="Client.ClientId"/>).</summary>
    public required string ClientId { get; init; }

    /// <summary>Invoice currency (ISO-4217). Every line must match it.</summary>
    public required string Currency { get; init; }

    /// <summary>The line items. Never null.</summary>
    public IReadOnlyList<InvoiceLine> Lines { get; init; } = Array.Empty<InvoiceLine>();

    /// <summary>Lifecycle status.</summary>
    public InvoiceStatus Status { get; init; } = InvoiceStatus.Draft;

    /// <summary>Date the invoice is issued.</summary>
    public DateOnly IssueDate { get; init; }

    /// <summary>Date payment is due.</summary>
    public DateOnly DueDate { get; init; }

    /// <summary>Running total of payments received.</summary>
    public Money AmountPaid { get; init; }

    /// <summary>Free-form footer note (e.g. banking details, thanks).</summary>
    public string? Notes { get; init; }

    /// <summary>When the record was created.</summary>
    public DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>When the record last changed.</summary>
    public DateTimeOffset UpdatedAtUtc { get; init; }

    /// <summary>Sum of line subtotals (pre-tax).</summary>
    public Money Subtotal => Fold(static l => l.LineSubtotal);

    /// <summary>Sum of line tax.</summary>
    public Money TaxTotal => Fold(static l => l.LineTax);

    /// <summary>Grand total (subtotal + tax).</summary>
    public Money Total => Fold(static l => l.LineTotal);

    /// <summary>Payments received to date, normalised into the invoice currency.</summary>
    public Money PaidToDate => string.IsNullOrEmpty(AmountPaid.Currency) ? Money.Zero(Currency) : AmountPaid;

    /// <summary>Outstanding balance (total − paid). Negative means overpaid.</summary>
    public Money BalanceDue => (Total - PaidToDate).Round();

    /// <summary>True once the balance is zero or negative.</summary>
    public bool IsSettled => BalanceDue.Amount <= 0m;

    /// <summary>True when past <see cref="DueDate"/> and still owing (drafts and cancellations never count).</summary>
    public bool IsOverdue(DateOnly asOf)
        => !IsSettled
           && Status is not InvoiceStatus.Cancelled and not InvoiceStatus.Draft
           && asOf > DueDate;

    // Sum a selector across the lines, starting from zero in the invoice currency
    // so an empty invoice still yields a well-formed zero (never a default(Money)
    // with a null currency).
    private Money Fold(Func<InvoiceLine, Money> selector)
    {
        var acc = Money.Zero(Currency);
        foreach (var line in Lines) acc += selector(line);
        return acc.Round();
    }
}

/// <summary>
/// The invoicing seam. Orchestrates the lifecycle (create/issue/pay/cancel) and
/// the derived views (by status, by client, overdue). Backed by
/// <see cref="IBusinessStore"/>; the default implementation ships in this library.
/// </summary>
public interface IInvoiceService
{
    /// <summary>Identifies the backing implementation.</summary>
    string BackendId { get; }

    /// <summary>
    /// Creates a <see cref="InvoiceStatus.Draft"/>. Validates that every line's
    /// currency matches <paramref name="currency"/>. The due date is derived from
    /// <paramref name="paymentTermsDays"/>, falling back to the client's terms,
    /// then net-30.
    /// </summary>
    ValueTask<Invoice> CreateDraftAsync(
        string clientId,
        string currency,
        IEnumerable<InvoiceLine> lines,
        DateOnly issueDate,
        int? paymentTermsDays = null,
        string? notes = null,
        CancellationToken ct = default);

    /// <summary>Fetches an invoice by id, or null.</summary>
    ValueTask<Invoice?> GetAsync(string invoiceId, CancellationToken ct = default);

    /// <summary>
    /// Moves a draft to <see cref="InvoiceStatus.Sent"/>, assigning a human number
    /// (if absent) and a due date derived from the terms when one is not already set.
    /// </summary>
    ValueTask<Invoice> IssueAsync(string invoiceId, DateOnly? issueDate = null, int paymentTermsDays = 30, CancellationToken ct = default);

    /// <summary>Records a (possibly partial) payment, flipping status to PartiallyPaid or Paid.</summary>
    ValueTask<Invoice> RecordPaymentAsync(string invoiceId, Money amount, CancellationToken ct = default);

    /// <summary>Settles the whole outstanding balance in one call.</summary>
    ValueTask<Invoice> MarkPaidAsync(string invoiceId, CancellationToken ct = default);

    /// <summary>Voids an invoice. A fully-paid invoice cannot be cancelled (issue a credit note instead).</summary>
    ValueTask<Invoice> CancelAsync(string invoiceId, CancellationToken ct = default);

    /// <summary>Lists invoices, optionally filtered by status, newest issue date first.</summary>
    ValueTask<IReadOnlyList<Invoice>> ListAsync(InvoiceStatus? status = null, CancellationToken ct = default);

    /// <summary>Lists invoices for one client, newest issue date first.</summary>
    ValueTask<IReadOnlyList<Invoice>> ListByClientAsync(string clientId, CancellationToken ct = default);

    /// <summary>Lists invoices past due and unsettled as of <paramref name="asOf"/> (computed, no write).</summary>
    ValueTask<IReadOnlyList<Invoice>> ListOverdueAsync(DateOnly asOf, CancellationToken ct = default);

    /// <summary>Persists the <see cref="InvoiceStatus.Overdue"/> flip for any now-overdue invoice; returns how many changed.</summary>
    ValueTask<int> RefreshOverdueAsync(DateOnly asOf, CancellationToken ct = default);
}

/// <summary>Produces the human-facing invoice number assigned at issue time.</summary>
public interface IInvoiceNumberGenerator
{
    /// <summary>Returns the next number in sequence.</summary>
    string Next();
}

/// <summary>
/// Monotonic, offline invoice numbers of the form "{Prefix}{yyyy}-{seq:D4}", e.g.
/// "INV-2026-0001". Thread-safe. When rehydrating a store, <paramref name="seed"/>
/// the counter from the highest existing number so numbers never collide across
/// restarts (there is no server handing out ids).
/// </summary>
public sealed class SequentialInvoiceNumberGenerator : IInvoiceNumberGenerator
{
    private readonly string _prefix;
    private readonly int _year;
    private long _seq;

    /// <summary>Creates a generator. Defaults to prefix "INV-" and the current UTC year.</summary>
    public SequentialInvoiceNumberGenerator(string prefix = "INV-", int? year = null, long seed = 0)
    {
        _prefix = prefix ?? "";
        _year = year ?? DateTime.UtcNow.Year;
        _seq = seed;
    }

    /// <inheritdoc/>
    public string Next()
    {
        var n = Interlocked.Increment(ref _seq);
        return $"{_prefix}{_year}-{n:D4}";
    }
}

/// <summary>
/// Renders an invoice to PDF bytes. Kept as a SEAM so BusinessOps carries no PDF
/// dependency of its own. The concrete renderer maps an <see cref="Invoice"/> onto
/// CircleAI.Documents' <c>DocumentKind.Invoice</c> template and lives in an
/// integration/hosting project — deliberately not here, because wiring to that
/// still-landing invoice model would couple this library to in-flight code.
/// </summary>
public interface IInvoicePdfRenderer
{
    /// <summary>Identifies the backing renderer.</summary>
    string BackendId { get; }

    /// <summary>Renders the invoice (and optional client details) to PDF bytes.</summary>
    ValueTask<byte[]> RenderAsync(Invoice invoice, Client? client = null, CancellationToken ct = default);
}

/// <summary>
/// Fail-loud default: no on-device PDF engine is wired in. Throws a clear error
/// rather than returning empty bytes that masquerade as a valid (blank) PDF.
/// </summary>
public sealed class NullInvoicePdfRenderer : IInvoicePdfRenderer
{
    /// <summary>Shared instance.</summary>
    public static readonly NullInvoicePdfRenderer Instance = new();

    /// <inheritdoc/>
    public string BackendId => "null";

    /// <inheritdoc/>
    public ValueTask<byte[]> RenderAsync(Invoice invoice, Client? client = null, CancellationToken ct = default)
        => throw new NotSupportedException(
            "No IInvoicePdfRenderer is configured. Wire a CircleAI.Documents-backed renderer (DocumentKind.Invoice) at the host layer.");
}
