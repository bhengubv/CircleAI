#nullable enable

// Invoice.cs
//
// The invoice schema — the third document KIND. Same double duty as the CV and
// cover letter (typed layout input AND the model's JSON target), with one hard
// difference: money is REAL. Amounts are decimal, never double or string — a
// cent lost to binary floating point is a cent that does not reconcile.
//
// The totals are DERIVED, not stored: Subtotal, VatAmount and Total are computed
// properties over the line items and the VAT rate, so they can never disagree
// with the lines a person can see and add up themselves. The template shows what
// the model computes; nobody types a total that the lines contradict.
//
// Currency is carried as an ISO code (e.g. "ZAR", "USD") so this is genuinely
// multi-country — the template maps the code to a symbol for display. VAT is a
// plain percentage, defaulting in the sample to South Africa's 15%.
//
// Dates are STRINGS on purpose, exactly as in CvDocument: an invoice prints
// "23 July 2026", the model emits text, and a DateTime buys no layout benefit.

using System;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Documents;

/// <summary>One party on an invoice — the issuer (From) or the customer (To).</summary>
/// <param name="Name">Person or business name, e.g. "Thabo Mokoena Studio".</param>
/// <param name="Address">Optional street / postal address, one line.</param>
/// <param name="Email">Optional.</param>
/// <param name="Phone">Optional.</param>
/// <param name="TaxNumber">Optional VAT / tax registration number — required on a South African tax invoice when shown.</param>
public sealed record InvoiceParty(
    string  Name,
    string? Address   = null,
    string? Email     = null,
    string? Phone     = null,
    string? TaxNumber = null);

/// <summary>One priced line on an invoice.</summary>
/// <param name="Description">What is being charged for, e.g. "Website design — 3 pages".</param>
/// <param name="Quantity">How many units. Decimal so "1.5 hours" or "0.75 days" are first-class.</param>
/// <param name="UnitPrice">Price per unit, before VAT, in the invoice's currency.</param>
public sealed record InvoiceLineItem(
    string  Description,
    decimal Quantity,
    decimal UnitPrice)
{
    /// <summary>Raw line total (Quantity × UnitPrice). Rounding for display is the invoice's job.</summary>
    public decimal LineTotal => Quantity * UnitPrice;
}

/// <summary>A complete invoice, ready to lay out. Also the model's JSON target.</summary>
/// <param name="From">Who is issuing (and being paid for) the invoice.</param>
/// <param name="To">Who is being billed.</param>
/// <param name="InvoiceNumber">Human reference, e.g. "INV-2026-014". Also drives the file name.</param>
/// <param name="IssueDate">Free-text date the invoice is issued, e.g. "23 July 2026".</param>
/// <param name="DueDate">Free-text date payment is due, e.g. "22 August 2026".</param>
/// <param name="LineItems">The priced lines. May be empty (a zero invoice is still a valid artifact).</param>
/// <param name="VatPercent">VAT rate as a percentage, e.g. 15 for South Africa. 0 means no VAT charged.</param>
/// <param name="CurrencyCode">ISO 4217 code, e.g. "ZAR", "USD", "EUR". The template maps it to a symbol.</param>
/// <param name="PaymentNote">Optional banking / payment instructions. Newlines split into separate lines.</param>
public sealed record Invoice(
    InvoiceParty                   From,
    InvoiceParty                   To,
    string                         InvoiceNumber,
    string                         IssueDate,
    string                         DueDate,
    IReadOnlyList<InvoiceLineItem> LineItems,
    decimal                        VatPercent,
    string                         CurrencyCode = "ZAR",
    string?                        PaymentNote  = null)
{
    /// <summary>
    /// The single rounding authority for money on this invoice: 2 decimal places,
    /// half away from zero — the commercial convention a customer expects, and the
    /// same rule the template uses when it formats a figure, so a displayed line
    /// and a displayed subtotal never round different directions.
    /// </summary>
    public static decimal Round2(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    /// <summary>Sum of the (rounded) line totals, before VAT. Zero when there are no lines.</summary>
    public decimal Subtotal =>
        Round2((LineItems ?? Array.Empty<InvoiceLineItem>()).Sum(li => Round2(li.LineTotal)));

    /// <summary>VAT charged, computed from <see cref="Subtotal"/> at <see cref="VatPercent"/>.</summary>
    public decimal VatAmount => Round2(Subtotal * VatPercent / 100m);

    /// <summary>Grand total the customer pays: subtotal plus VAT.</summary>
    public decimal Total => Subtotal + VatAmount;

    /// <summary>
    /// A safe, non-null invoice built from only the essentials. This is the
    /// DETERMINISTIC FALLBACK, mirroring <see cref="CvDocument.Minimal"/>: even a
    /// zero-line invoice renders a real PDF rather than nothing.
    /// </summary>
    public static Invoice Minimal(
        InvoiceParty from, InvoiceParty to, string number, string issueDate, string dueDate) =>
        new(from, to, number, issueDate, dueDate,
            LineItems:  Array.Empty<InvoiceLineItem>(),
            VatPercent: 0m);

    /// <summary>
    /// A fully-populated South African example (ZAR, 15% VAT, local banking note),
    /// for previews and tests. Numbers are chosen to exercise a fractional quantity
    /// and multiple lines so the totals maths is visible.
    /// </summary>
    public static Invoice Sample() =>
        new(
            From: new InvoiceParty(
                Name:      "Thabo Mokoena Studio",
                Address:   "42 Vilakazi Street, Soweto, Johannesburg, 1804",
                Email:     "billing@thabostudio.co.za",
                Phone:     "+27 82 555 0142",
                TaxNumber: "4820314567"),
            To: new InvoiceParty(
                Name:      "Aurora Digital (Pty) Ltd",
                Address:   "12 Rivonia Road, Sandton, Johannesburg, 2196",
                Email:     "accounts@auroradigital.co.za",
                TaxNumber: "4130298811"),
            InvoiceNumber: "INV-2026-014",
            IssueDate:     "23 July 2026",
            DueDate:       "22 August 2026",
            LineItems: new[]
            {
                new InvoiceLineItem("Landing-page design (desktop + mobile)", 1m,   6500.00m),
                new InvoiceLineItem("Content updates and revisions (hours)",  4.5m,  650.00m),
                new InvoiceLineItem("On-device performance tuning (hours)",   3m,    850.00m),
            },
            VatPercent:   15m,
            CurrencyCode: "ZAR",
            PaymentNote:
                "Payment by EFT within 30 days.\n" +
                "Bank: Bank Zero\n" +
                "Account name: Thabo Mokoena Studio\n" +
                "Account number: 1055 0142 88\n" +
                "Reference: INV-2026-014");
}
