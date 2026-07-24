// SampleData.cs — (0.1.0) Small sample-data factories for demos and tests.
//
// Deterministic fixtures (fixed ids and dates) so a demo screen or a test can
// seed a store and get predictable output. Everything here is on-device sample
// content — nothing leaves the machine, and the data is fictional.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.BusinessOps;

/// <summary>Fictional, deterministic sample data for the operator core.</summary>
public static class BusinessOpsSampleData
{
    /// <summary>A few sample clients across South Africa and Nigeria (multi-currency).</summary>
    public static IReadOnlyList<Client> Clients() => new[]
    {
        new Client("cl-nandi", "Nandi Dlamini Design", "nandi@example.co.za", "+27 82 555 0142",
            "12 Long St, Cape Town, 8001", "4470112345", "ZAR", 30),
        new Client("cl-thabo", "Thabo Trading CC", "accounts@thabo.example", "+27 71 555 0199",
            "5 Jan Smuts Ave, Johannesburg, 2196", "4990556677", "ZAR", 14),
        new Client("cl-amara", "Amara Studios (Lagos)", "hello@amara.example", "+234 802 555 0101",
            "3 Awolowo Rd, Ikoyi, Lagos", null, "NGN", 30),
    };

    /// <summary>A sample issued invoice with two VAT-rated lines (defaults to ZAR / Nandi).</summary>
    public static Invoice SampleInvoice(string invoiceId = "inv-sample-1", string clientId = "cl-nandi", string currency = "ZAR")
    {
        var issue = new DateOnly(2026, 7, 1);
        var stamp = new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero);
        var lines = new[]
        {
            new InvoiceLine("Brand identity — logo suite", 1m, new Money(8500m, currency), 0.15m),
            new InvoiceLine("Business cards — design", 2m, new Money(750m, currency), 0.15m),
        };
        return new Invoice
        {
            InvoiceId = invoiceId,
            Number = "INV-2026-0001",
            ClientId = clientId,
            Currency = currency,
            Lines = lines,
            Status = InvoiceStatus.Sent,
            IssueDate = issue,
            DueDate = issue.AddDays(30),
            AmountPaid = Money.Zero(currency),
            Notes = "Thank you for your business. Banking details overleaf.",
            CreatedAtUtc = stamp,
            UpdatedAtUtc = stamp,
        };
    }

    /// <summary>A one-off invoice-chase and a recurring monthly check-in.</summary>
    public static IReadOnlyList<Reminder> Reminders() => new[]
    {
        new Reminder
        {
            ReminderId = "rem-chase-inv1",
            Title = "Follow up on INV-2026-0001",
            DueAtUtc = new DateTimeOffset(2026, 7, 20, 8, 0, 0, TimeSpan.Zero),
            Kind = ReminderKind.InvoiceDue,
            RelatedEntityId = "inv-sample-1",
            CreatedAtUtc = new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero),
        },
        new Reminder
        {
            ReminderId = "rem-checkin-thabo",
            Title = "Monthly check-in call",
            DueAtUtc = new DateTimeOffset(2026, 8, 1, 8, 0, 0, TimeSpan.Zero),
            Repeat = new RecurrenceRule(Recurrence.Monthly),
            Kind = ReminderKind.FollowUp,
            RelatedEntityId = "cl-thabo",
            CreatedAtUtc = new DateTimeOffset(2026, 7, 1, 9, 0, 0, TimeSpan.Zero),
        },
    };

    /// <summary>
    /// Seeds a store with the sample clients, one invoice and the reminders. Handy
    /// for a demo screen or as a test fixture. All on-device; nothing leaves.
    /// </summary>
    public static async ValueTask SeedAsync(IBusinessStore store, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        foreach (var client in Clients())
            await store.Clients.UpsertAsync(client, ct).ConfigureAwait(false);
        await store.Invoices.UpsertAsync(SampleInvoice(), ct).ConfigureAwait(false);
        foreach (var reminder in Reminders())
            await store.Reminders.UpsertAsync(reminder, ct).ConfigureAwait(false);
    }
}
