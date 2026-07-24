// NullImplementations.cs — (0.1.0) Fail-closed seams.
//
// The house pattern: a "null" implementation that persists nothing and reads
// empty, so a caller can wire the surface up before a real store exists without
// any risk of silently trusting phantom data. Mutations that are obliged to
// return a domain object throw a clear NotSupportedException rather than
// fabricating one.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.BusinessOps;

/// <summary>A store that keeps nothing. Reads are empty; writes are swallowed.</summary>
public sealed class NullBusinessStore : IBusinessStore
{
    /// <summary>Shared instance.</summary>
    public static readonly NullBusinessStore Instance = new();
    /// <inheritdoc/>
    public string BackendId => "null";
    /// <inheritdoc/>
    public IClientRepository Clients { get; } = new NullClientRepository();
    /// <inheritdoc/>
    public IInvoiceRepository Invoices { get; } = new NullInvoiceRepository();
    /// <inheritdoc/>
    public IReminderRepository Reminders { get; } = new NullReminderRepository();
}

internal sealed class NullClientRepository : IClientRepository
{
    public ValueTask UpsertAsync(Client client, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask<Client?> GetAsync(string clientId, CancellationToken ct = default) => ValueTask.FromResult<Client?>(null);
    public ValueTask<IReadOnlyList<Client>> ListAsync(CancellationToken ct = default) => ValueTask.FromResult<IReadOnlyList<Client>>(Array.Empty<Client>());
    public ValueTask<bool> RemoveAsync(string clientId, CancellationToken ct = default) => ValueTask.FromResult(false);
}

internal sealed class NullInvoiceRepository : IInvoiceRepository
{
    public ValueTask UpsertAsync(Invoice invoice, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask<Invoice?> GetAsync(string invoiceId, CancellationToken ct = default) => ValueTask.FromResult<Invoice?>(null);
    public ValueTask<IReadOnlyList<Invoice>> ListAsync(CancellationToken ct = default) => ValueTask.FromResult<IReadOnlyList<Invoice>>(Array.Empty<Invoice>());
    public ValueTask<bool> RemoveAsync(string invoiceId, CancellationToken ct = default) => ValueTask.FromResult(false);
}

internal sealed class NullReminderRepository : IReminderRepository
{
    public ValueTask UpsertAsync(Reminder reminder, CancellationToken ct = default) => ValueTask.CompletedTask;
    public ValueTask<Reminder?> GetAsync(string reminderId, CancellationToken ct = default) => ValueTask.FromResult<Reminder?>(null);
    public ValueTask<IReadOnlyList<Reminder>> ListAsync(CancellationToken ct = default) => ValueTask.FromResult<IReadOnlyList<Reminder>>(Array.Empty<Reminder>());
    public ValueTask<bool> RemoveAsync(string reminderId, CancellationToken ct = default) => ValueTask.FromResult(false);
}

/// <summary>A client book that stores nothing.</summary>
public sealed class NullClientBook : IClientBook
{
    /// <summary>Shared instance.</summary>
    public static readonly NullClientBook Instance = new();
    /// <inheritdoc/>
    public string BackendId => "null";
    /// <inheritdoc/>
    public ValueTask<Client> UpsertAsync(Client client, CancellationToken ct = default) => ValueTask.FromResult(client);
    /// <inheritdoc/>
    public ValueTask<Client?> GetAsync(string clientId, CancellationToken ct = default) => ValueTask.FromResult<Client?>(null);
    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<Client>> SearchAsync(string query, int topK = 20, CancellationToken ct = default) => ValueTask.FromResult<IReadOnlyList<Client>>(Array.Empty<Client>());
    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<Client>> ListAsync(CancellationToken ct = default) => ValueTask.FromResult<IReadOnlyList<Client>>(Array.Empty<Client>());
    /// <inheritdoc/>
    public ValueTask<bool> RemoveAsync(string clientId, CancellationToken ct = default) => ValueTask.FromResult(false);
}

/// <summary>A scheduler that stores nothing.</summary>
public sealed class NullReminderScheduler : IReminderScheduler
{
    /// <summary>Shared instance.</summary>
    public static readonly NullReminderScheduler Instance = new();
    /// <inheritdoc/>
    public string BackendId => "null";
    /// <inheritdoc/>
    public ValueTask<Reminder> ScheduleAsync(Reminder reminder, CancellationToken ct = default) => ValueTask.FromResult(reminder);
    /// <inheritdoc/>
    public ValueTask<Reminder> ScheduleFollowUpAsync(string relatedEntityId, string title, DateTimeOffset dueAtUtc, RecurrenceRule? repeat = null, CancellationToken ct = default)
        => throw new NotSupportedException("NullReminderScheduler cannot create reminders. Use ReminderScheduler over an IBusinessStore.");
    /// <inheritdoc/>
    public ValueTask<Reminder?> GetAsync(string reminderId, CancellationToken ct = default) => ValueTask.FromResult<Reminder?>(null);
    /// <inheritdoc/>
    public ValueTask<Reminder?> CompleteAsync(string reminderId, CancellationToken ct = default) => ValueTask.FromResult<Reminder?>(null);
    /// <inheritdoc/>
    public ValueTask<bool> CancelAsync(string reminderId, CancellationToken ct = default) => ValueTask.FromResult(false);
    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<Reminder>> ListDueAsync(DateTimeOffset asOf, CancellationToken ct = default) => ValueTask.FromResult<IReadOnlyList<Reminder>>(Array.Empty<Reminder>());
    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<Reminder>> ListPendingAsync(CancellationToken ct = default) => ValueTask.FromResult<IReadOnlyList<Reminder>>(Array.Empty<Reminder>());
    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<Reminder>> ListForEntityAsync(string relatedEntityId, CancellationToken ct = default) => ValueTask.FromResult<IReadOnlyList<Reminder>>(Array.Empty<Reminder>());
}

/// <summary>An invoicing service that stores nothing; mutations fail closed.</summary>
public sealed class NullInvoiceService : IInvoiceService
{
    /// <summary>Shared instance.</summary>
    public static readonly NullInvoiceService Instance = new();
    /// <inheritdoc/>
    public string BackendId => "null";

    private static ValueTask<Invoice> Unsupported()
        => throw new NotSupportedException("NullInvoiceService cannot mutate invoices. Use InvoiceService over an IBusinessStore.");

    /// <inheritdoc/>
    public ValueTask<Invoice> CreateDraftAsync(string clientId, string currency, IEnumerable<InvoiceLine> lines, DateOnly issueDate, int? paymentTermsDays = null, string? notes = null, CancellationToken ct = default) => Unsupported();
    /// <inheritdoc/>
    public ValueTask<Invoice?> GetAsync(string invoiceId, CancellationToken ct = default) => ValueTask.FromResult<Invoice?>(null);
    /// <inheritdoc/>
    public ValueTask<Invoice> IssueAsync(string invoiceId, DateOnly? issueDate = null, int paymentTermsDays = 30, CancellationToken ct = default) => Unsupported();
    /// <inheritdoc/>
    public ValueTask<Invoice> RecordPaymentAsync(string invoiceId, Money amount, CancellationToken ct = default) => Unsupported();
    /// <inheritdoc/>
    public ValueTask<Invoice> MarkPaidAsync(string invoiceId, CancellationToken ct = default) => Unsupported();
    /// <inheritdoc/>
    public ValueTask<Invoice> CancelAsync(string invoiceId, CancellationToken ct = default) => Unsupported();
    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<Invoice>> ListAsync(InvoiceStatus? status = null, CancellationToken ct = default) => ValueTask.FromResult<IReadOnlyList<Invoice>>(Array.Empty<Invoice>());
    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<Invoice>> ListByClientAsync(string clientId, CancellationToken ct = default) => ValueTask.FromResult<IReadOnlyList<Invoice>>(Array.Empty<Invoice>());
    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<Invoice>> ListOverdueAsync(DateOnly asOf, CancellationToken ct = default) => ValueTask.FromResult<IReadOnlyList<Invoice>>(Array.Empty<Invoice>());
    /// <inheritdoc/>
    public ValueTask<int> RefreshOverdueAsync(DateOnly asOf, CancellationToken ct = default) => ValueTask.FromResult(0);
}
