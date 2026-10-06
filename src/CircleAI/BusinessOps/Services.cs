// Services.cs — (0.1.0) Default implementations of the operator-core seams.
//
// ClientBook, InvoiceService and ReminderScheduler are the working behaviour on
// top of IBusinessStore. They are the "real" implementations — no stubs. A
// TimeProvider is injected so time-dependent behaviour (created/updated stamps,
// "today") is deterministic and testable; it defaults to the system clock.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.BusinessOps;

// Id minting for entities the services create. GUID "N" form: compact, offline,
// collision-free without any coordinating server.
internal static class BusinessOpsIds
{
    public static string New() => Guid.NewGuid().ToString("N");
}

/// <summary>The default client book over an <see cref="IBusinessStore"/>.</summary>
public sealed class ClientBook : IClientBook
{
    private readonly IClientRepository _repo;
    private readonly TimeProvider _clock;

    /// <summary>Creates a client book. <paramref name="clock"/> defaults to the system clock.</summary>
    public ClientBook(IBusinessStore store, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _repo = store.Clients;
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public string BackendId => "default";

    /// <inheritdoc/>
    public async ValueTask<Client> UpsertAsync(Client client, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(client.ClientId, nameof(client));
        // Stamp creation time on first store; preserve it on later updates.
        var stamped = client.CreatedAtUtc == default ? client with { CreatedAtUtc = _clock.GetUtcNow() } : client;
        await _repo.UpsertAsync(stamped, ct).ConfigureAwait(false);
        return stamped;
    }

    /// <inheritdoc/>
    public ValueTask<Client?> GetAsync(string clientId, CancellationToken ct = default)
        => _repo.GetAsync(clientId, ct);

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<Client>> SearchAsync(string query, int topK = 20, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (topK <= 0) throw new ArgumentOutOfRangeException(nameof(topK));
        var all = await _repo.ListAsync(ct).ConfigureAwait(false);
        return all.Where(c =>
                   c.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                   || (c.Email?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                   || (c.Phone?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
               .Take(topK)
               .ToArray();
    }

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<Client>> ListAsync(CancellationToken ct = default)
        => _repo.ListAsync(ct);

    /// <inheritdoc/>
    public ValueTask<bool> RemoveAsync(string clientId, CancellationToken ct = default)
        => _repo.RemoveAsync(clientId, ct);
}

/// <summary>The default invoicing service over an <see cref="IBusinessStore"/>.</summary>
public sealed class InvoiceService : IInvoiceService
{
    private readonly IInvoiceRepository _invoices;
    private readonly IClientRepository _clients;
    private readonly IInvoiceNumberGenerator _numbers;
    private readonly TimeProvider _clock;

    /// <summary>
    /// Creates the service. <paramref name="numbers"/> defaults to a fresh
    /// <see cref="SequentialInvoiceNumberGenerator"/>; <paramref name="clock"/> to
    /// the system clock.
    /// </summary>
    public InvoiceService(IBusinessStore store, IInvoiceNumberGenerator? numbers = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _invoices = store.Invoices;
        _clients = store.Clients;
        _numbers = numbers ?? new SequentialInvoiceNumberGenerator();
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public string BackendId => "default";

    /// <inheritdoc/>
    public async ValueTask<Invoice> CreateDraftAsync(
        string clientId,
        string currency,
        IEnumerable<InvoiceLine> lines,
        DateOnly issueDate,
        int? paymentTermsDays = null,
        string? notes = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        ArgumentNullException.ThrowIfNull(lines);

        var cur = currency.Trim().ToUpperInvariant();
        var lineList = lines.ToArray();
        foreach (var l in lineList)
        {
            if (!string.Equals(l.UnitPrice.Currency, cur, StringComparison.Ordinal))
                throw new ArgumentException(
                    $"Line \"{l.Description}\" is priced in {l.UnitPrice.Currency} but the invoice is {cur}.", nameof(lines));
        }

        // Terms precedence: explicit argument, else the client's default, else net-30.
        var terms = paymentTermsDays
                    ?? (await _clients.GetAsync(clientId, ct).ConfigureAwait(false))?.PaymentTermsDays
                    ?? 30;

        var now = _clock.GetUtcNow();
        var invoice = new Invoice
        {
            InvoiceId = BusinessOpsIds.New(),
            ClientId = clientId,
            Currency = cur,
            Lines = lineList,
            Status = InvoiceStatus.Draft,
            IssueDate = issueDate,
            DueDate = issueDate.AddDays(terms),
            AmountPaid = Money.Zero(cur),
            Notes = notes,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        await _invoices.UpsertAsync(invoice, ct).ConfigureAwait(false);
        return invoice;
    }

    /// <inheritdoc/>
    public ValueTask<Invoice?> GetAsync(string invoiceId, CancellationToken ct = default)
        => _invoices.GetAsync(invoiceId, ct);

    /// <inheritdoc/>
    public async ValueTask<Invoice> IssueAsync(string invoiceId, DateOnly? issueDate = null, int paymentTermsDays = 30, CancellationToken ct = default)
    {
        var inv = await RequireAsync(invoiceId, ct).ConfigureAwait(false);
        if (inv.Status is InvoiceStatus.Cancelled)
            throw new InvalidOperationException("A cancelled invoice cannot be issued.");

        var issue = issueDate ?? inv.IssueDate;
        if (issue == default) issue = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        var due = inv.DueDate == default ? issue.AddDays(paymentTermsDays) : inv.DueDate;

        var updated = inv with
        {
            Status = inv.Status == InvoiceStatus.Draft ? InvoiceStatus.Sent : inv.Status,
            Number = inv.Number ?? _numbers.Next(),
            IssueDate = issue,
            DueDate = due,
            UpdatedAtUtc = _clock.GetUtcNow(),
        };
        await _invoices.UpsertAsync(updated, ct).ConfigureAwait(false);
        return updated;
    }

    /// <inheritdoc/>
    public async ValueTask<Invoice> RecordPaymentAsync(string invoiceId, Money amount, CancellationToken ct = default)
    {
        var inv = await RequireAsync(invoiceId, ct).ConfigureAwait(false);
        if (inv.Status == InvoiceStatus.Cancelled)
            throw new InvalidOperationException("Cannot record a payment against a cancelled invoice.");
        if (!string.Equals(amount.Currency, inv.Currency, StringComparison.Ordinal))
            throw new ArgumentException($"Payment currency {amount.Currency} does not match invoice currency {inv.Currency}.", nameof(amount));
        if (amount.Amount <= 0m)
            throw new ArgumentOutOfRangeException(nameof(amount), "A payment must be a positive amount.");

        var paid = (inv.PaidToDate + amount).Round();
        var status = paid.Amount >= inv.Total.Amount ? InvoiceStatus.Paid : InvoiceStatus.PartiallyPaid;
        var updated = inv with { AmountPaid = paid, Status = status, UpdatedAtUtc = _clock.GetUtcNow() };
        await _invoices.UpsertAsync(updated, ct).ConfigureAwait(false);
        return updated;
    }

    /// <inheritdoc/>
    public async ValueTask<Invoice> MarkPaidAsync(string invoiceId, CancellationToken ct = default)
    {
        var inv = await RequireAsync(invoiceId, ct).ConfigureAwait(false);
        if (inv.Status == InvoiceStatus.Cancelled)
            throw new InvalidOperationException("Cannot pay a cancelled invoice.");

        var balance = inv.BalanceDue;
        if (balance.Amount <= 0m)
        {
            // Already settled — just normalise the status to Paid.
            if (inv.Status == InvoiceStatus.Paid) return inv;
            var already = inv with { Status = InvoiceStatus.Paid, UpdatedAtUtc = _clock.GetUtcNow() };
            await _invoices.UpsertAsync(already, ct).ConfigureAwait(false);
            return already;
        }
        return await RecordPaymentAsync(invoiceId, balance, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask<Invoice> CancelAsync(string invoiceId, CancellationToken ct = default)
    {
        var inv = await RequireAsync(invoiceId, ct).ConfigureAwait(false);
        if (inv.Status == InvoiceStatus.Paid)
            throw new InvalidOperationException("A paid invoice cannot be cancelled; issue a credit note instead.");
        var updated = inv with { Status = InvoiceStatus.Cancelled, UpdatedAtUtc = _clock.GetUtcNow() };
        await _invoices.UpsertAsync(updated, ct).ConfigureAwait(false);
        return updated;
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<Invoice>> ListAsync(InvoiceStatus? status = null, CancellationToken ct = default)
    {
        var all = await _invoices.ListAsync(ct).ConfigureAwait(false);
        IEnumerable<Invoice> q = all;
        if (status is { } s) q = q.Where(i => i.Status == s);
        return q.OrderByDescending(i => i.IssueDate).ToArray();
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<Invoice>> ListByClientAsync(string clientId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        var all = await _invoices.ListAsync(ct).ConfigureAwait(false);
        return all.Where(i => string.Equals(i.ClientId, clientId, StringComparison.Ordinal))
                  .OrderByDescending(i => i.IssueDate)
                  .ToArray();
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<Invoice>> ListOverdueAsync(DateOnly asOf, CancellationToken ct = default)
    {
        var all = await _invoices.ListAsync(ct).ConfigureAwait(false);
        return all.Where(i => i.IsOverdue(asOf)).OrderBy(i => i.DueDate).ToArray();
    }

    /// <inheritdoc/>
    public async ValueTask<int> RefreshOverdueAsync(DateOnly asOf, CancellationToken ct = default)
    {
        var all = await _invoices.ListAsync(ct).ConfigureAwait(false);
        var changed = 0;
        foreach (var inv in all)
        {
            if (inv.IsOverdue(asOf) && inv.Status != InvoiceStatus.Overdue)
            {
                var updated = inv with { Status = InvoiceStatus.Overdue, UpdatedAtUtc = _clock.GetUtcNow() };
                await _invoices.UpsertAsync(updated, ct).ConfigureAwait(false);
                changed++;
            }
        }
        return changed;
    }

    private async ValueTask<Invoice> RequireAsync(string invoiceId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invoiceId);
        var inv = await _invoices.GetAsync(invoiceId, ct).ConfigureAwait(false);
        return inv ?? throw new KeyNotFoundException($"Invoice {invoiceId} not found.");
    }
}

/// <summary>The default scheduler over an <see cref="IBusinessStore"/>.</summary>
public sealed class ReminderScheduler : IReminderScheduler
{
    private readonly IReminderRepository _repo;
    private readonly TimeProvider _clock;

    /// <summary>Creates the scheduler. <paramref name="clock"/> defaults to the system clock.</summary>
    public ReminderScheduler(IBusinessStore store, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _repo = store.Reminders;
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public string BackendId => "default";

    /// <inheritdoc/>
    public async ValueTask<Reminder> ScheduleAsync(Reminder reminder, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reminder);
        ArgumentException.ThrowIfNullOrWhiteSpace(reminder.ReminderId, nameof(reminder));
        ArgumentException.ThrowIfNullOrWhiteSpace(reminder.Title, nameof(reminder));
        var stamped = reminder.CreatedAtUtc == default ? reminder with { CreatedAtUtc = _clock.GetUtcNow() } : reminder;
        await _repo.UpsertAsync(stamped, ct).ConfigureAwait(false);
        return stamped;
    }

    /// <inheritdoc/>
    public ValueTask<Reminder> ScheduleFollowUpAsync(
        string relatedEntityId,
        string title,
        DateTimeOffset dueAtUtc,
        RecurrenceRule? repeat = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relatedEntityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        var reminder = new Reminder
        {
            ReminderId = BusinessOpsIds.New(),
            Title = title,
            DueAtUtc = dueAtUtc,
            Repeat = repeat ?? RecurrenceRule.Once,
            Kind = ReminderKind.FollowUp,
            RelatedEntityId = relatedEntityId,
            CreatedAtUtc = _clock.GetUtcNow(),
        };
        return ScheduleAsync(reminder, ct);
    }

    /// <inheritdoc/>
    public ValueTask<Reminder?> GetAsync(string reminderId, CancellationToken ct = default)
        => _repo.GetAsync(reminderId, ct);

    /// <inheritdoc/>
    public async ValueTask<Reminder?> CompleteAsync(string reminderId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reminderId);
        var existing = await _repo.GetAsync(reminderId, ct).ConfigureAwait(false)
                       ?? throw new KeyNotFoundException($"Reminder {reminderId} not found.");

        var done = existing with { Completed = true };
        await _repo.UpsertAsync(done, ct).ConfigureAwait(false);

        // Roll a recurring reminder forward to its next occurrence.
        if (!existing.Repeat.IsRecurring) return null;
        var next = existing.Repeat.Next(existing.DueAtUtc);
        if (next is null) return null;

        var followOn = existing with
        {
            ReminderId = BusinessOpsIds.New(),
            DueAtUtc = next.Value,
            Completed = false,
            CreatedAtUtc = _clock.GetUtcNow(),
        };
        await _repo.UpsertAsync(followOn, ct).ConfigureAwait(false);
        return followOn;
    }

    /// <inheritdoc/>
    public ValueTask<bool> CancelAsync(string reminderId, CancellationToken ct = default)
        => _repo.RemoveAsync(reminderId, ct);

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<Reminder>> ListDueAsync(DateTimeOffset asOf, CancellationToken ct = default)
    {
        var all = await _repo.ListAsync(ct).ConfigureAwait(false);
        return all.Where(r => r.IsDue(asOf)).OrderBy(r => r.DueAtUtc).ToArray();
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<Reminder>> ListPendingAsync(CancellationToken ct = default)
    {
        var all = await _repo.ListAsync(ct).ConfigureAwait(false);
        return all.Where(r => !r.Completed).OrderBy(r => r.DueAtUtc).ToArray();
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<Reminder>> ListForEntityAsync(string relatedEntityId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relatedEntityId);
        var all = await _repo.ListAsync(ct).ConfigureAwait(false);
        return all.Where(r => string.Equals(r.RelatedEntityId, relatedEntityId, StringComparison.Ordinal))
                  .OrderBy(r => r.DueAtUtc)
                  .ToArray();
    }
}
