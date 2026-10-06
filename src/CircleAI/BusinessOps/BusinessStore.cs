// BusinessStore.cs — (0.1.0) The persistence seam. In-memory default.
//
// IBusinessStore is the ONE persistence abstraction the operator core sits on. The
// shipped default keeps everything in process memory; a host is expected to
// implement it over on-device SQLite for durability. There is deliberately NO
// server-backed implementation anywhere in this library — business data is the
// user's and stays on the device (repo rule: decentralisation-first).
//
// The store is split into three tiny repositories (clients, invoices, reminders),
// each pure CRUD. Domain behaviour — numbering, status transitions, recurrence,
// search — lives in the services (see Services.cs), not here.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.BusinessOps;

/// <summary>Persistence for <see cref="Client"/> records.</summary>
public interface IClientRepository
{
    /// <summary>Inserts or replaces a client.</summary>
    ValueTask UpsertAsync(Client client, CancellationToken ct = default);
    /// <summary>Fetches a client by id, or null.</summary>
    ValueTask<Client?> GetAsync(string clientId, CancellationToken ct = default);
    /// <summary>All clients.</summary>
    ValueTask<IReadOnlyList<Client>> ListAsync(CancellationToken ct = default);
    /// <summary>Removes a client. True if one was removed.</summary>
    ValueTask<bool> RemoveAsync(string clientId, CancellationToken ct = default);
}

/// <summary>Persistence for <see cref="Invoice"/> records.</summary>
public interface IInvoiceRepository
{
    /// <summary>Inserts or replaces an invoice.</summary>
    ValueTask UpsertAsync(Invoice invoice, CancellationToken ct = default);
    /// <summary>Fetches an invoice by id, or null.</summary>
    ValueTask<Invoice?> GetAsync(string invoiceId, CancellationToken ct = default);
    /// <summary>All invoices.</summary>
    ValueTask<IReadOnlyList<Invoice>> ListAsync(CancellationToken ct = default);
    /// <summary>Removes an invoice. True if one was removed.</summary>
    ValueTask<bool> RemoveAsync(string invoiceId, CancellationToken ct = default);
}

/// <summary>Persistence for <see cref="Reminder"/> records.</summary>
public interface IReminderRepository
{
    /// <summary>Inserts or replaces a reminder.</summary>
    ValueTask UpsertAsync(Reminder reminder, CancellationToken ct = default);
    /// <summary>Fetches a reminder by id, or null.</summary>
    ValueTask<Reminder?> GetAsync(string reminderId, CancellationToken ct = default);
    /// <summary>All reminders.</summary>
    ValueTask<IReadOnlyList<Reminder>> ListAsync(CancellationToken ct = default);
    /// <summary>Removes a reminder. True if one was removed.</summary>
    ValueTask<bool> RemoveAsync(string reminderId, CancellationToken ct = default);
}

/// <summary>
/// The on-device persistence abstraction for the whole operator core. Bind this to
/// an in-memory store (default) or an on-device database — never to a server.
/// </summary>
public interface IBusinessStore
{
    /// <summary>Identifies the backing store, e.g. "in-memory" or "null".</summary>
    string BackendId { get; }
    /// <summary>Client persistence.</summary>
    IClientRepository Clients { get; }
    /// <summary>Invoice persistence.</summary>
    IInvoiceRepository Invoices { get; }
    /// <summary>Reminder persistence.</summary>
    IReminderRepository Reminders { get; }
}

/// <summary>
/// The default store: everything in process memory, thread-safe. Perfect for a
/// single device session, demos and tests. A host swaps in an SQLite-backed
/// <see cref="IBusinessStore"/> for durability without touching the services.
/// </summary>
public sealed class InMemoryBusinessStore : IBusinessStore
{
    /// <inheritdoc/>
    public string BackendId => "in-memory";
    /// <inheritdoc/>
    public IClientRepository Clients { get; } = new InMemoryClientRepository();
    /// <inheritdoc/>
    public IInvoiceRepository Invoices { get; } = new InMemoryInvoiceRepository();
    /// <inheritdoc/>
    public IReminderRepository Reminders { get; } = new InMemoryReminderRepository();
}

internal sealed class InMemoryClientRepository : IClientRepository
{
    private readonly ConcurrentDictionary<string, Client> _items = new(StringComparer.Ordinal);

    public ValueTask UpsertAsync(Client client, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(client.ClientId, nameof(client));
        _items[client.ClientId] = client;
        return ValueTask.CompletedTask;
    }

    public ValueTask<Client?> GetAsync(string clientId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        return ValueTask.FromResult(_items.TryGetValue(clientId, out var c) ? c : null);
    }

    public ValueTask<IReadOnlyList<Client>> ListAsync(CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<Client>>(
            _items.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToArray());

    public ValueTask<bool> RemoveAsync(string clientId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        return ValueTask.FromResult(_items.TryRemove(clientId, out _));
    }
}

internal sealed class InMemoryInvoiceRepository : IInvoiceRepository
{
    private readonly ConcurrentDictionary<string, Invoice> _items = new(StringComparer.Ordinal);

    public ValueTask UpsertAsync(Invoice invoice, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        ArgumentException.ThrowIfNullOrWhiteSpace(invoice.InvoiceId, nameof(invoice));
        _items[invoice.InvoiceId] = invoice;
        return ValueTask.CompletedTask;
    }

    public ValueTask<Invoice?> GetAsync(string invoiceId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invoiceId);
        return ValueTask.FromResult(_items.TryGetValue(invoiceId, out var i) ? i : null);
    }

    public ValueTask<IReadOnlyList<Invoice>> ListAsync(CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<Invoice>>(_items.Values.ToArray());

    public ValueTask<bool> RemoveAsync(string invoiceId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invoiceId);
        return ValueTask.FromResult(_items.TryRemove(invoiceId, out _));
    }
}

internal sealed class InMemoryReminderRepository : IReminderRepository
{
    private readonly ConcurrentDictionary<string, Reminder> _items = new(StringComparer.Ordinal);

    public ValueTask UpsertAsync(Reminder reminder, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reminder);
        ArgumentException.ThrowIfNullOrWhiteSpace(reminder.ReminderId, nameof(reminder));
        _items[reminder.ReminderId] = reminder;
        return ValueTask.CompletedTask;
    }

    public ValueTask<Reminder?> GetAsync(string reminderId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reminderId);
        return ValueTask.FromResult(_items.TryGetValue(reminderId, out var r) ? r : null);
    }

    public ValueTask<IReadOnlyList<Reminder>> ListAsync(CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<Reminder>>(_items.Values.ToArray());

    public ValueTask<bool> RemoveAsync(string reminderId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reminderId);
        return ValueTask.FromResult(_items.TryRemove(reminderId, out _));
    }
}
