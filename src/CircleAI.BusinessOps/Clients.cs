// Clients.cs — (0.1.0) The client book: the billable customers of the business.
//
// A Client is richer than a CircleAI.CRM.Contact: it carries the billing details
// invoicing needs (tax/VAT number, billing address, default currency, payment
// terms). Rather than stand up a second generic contact store, CrmBridge maps
// Client <-> Contact so the CRM surface is REUSED, not duplicated.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.BusinessOps;

/// <summary>A billable customer of the business.</summary>
/// <param name="ClientId">Stable identifier (host-supplied or generated).</param>
/// <param name="Name">Display / legal name shown on invoices.</param>
/// <param name="Email">Primary billing email, if known.</param>
/// <param name="Phone">Contact number, if known.</param>
/// <param name="BillingAddress">Free-form address block for the invoice header.</param>
/// <param name="TaxNumber">VAT / tax registration number for the invoice header.</param>
/// <param name="DefaultCurrency">Currency new invoices default to (ISO-4217).</param>
/// <param name="PaymentTermsDays">Net terms in days; drives invoice due dates (net-30 by default).</param>
/// <param name="Notes">Any private notes about the client.</param>
/// <param name="CreatedAtUtc">When the record was first stored (stamped by the client book).</param>
public sealed record Client(
    string ClientId,
    string Name,
    string? Email = null,
    string? Phone = null,
    string? BillingAddress = null,
    string? TaxNumber = null,
    string DefaultCurrency = Currencies.DefaultCurrency,
    int PaymentTermsDays = 30,
    string? Notes = null,
    DateTimeOffset CreatedAtUtc = default);

/// <summary>
/// The client-book seam — a small CRM over <see cref="Client"/> records. An
/// in-memory default (<c>ClientBook</c> over <see cref="IBusinessStore"/>) ships
/// in this library; a host may back it with on-device storage. NEVER a central
/// server: business data stays on the device (repo rule: decentralisation-first).
/// </summary>
public interface IClientBook
{
    /// <summary>Identifies the backing implementation, e.g. "in-memory" or "null".</summary>
    string BackendId { get; }

    /// <summary>Inserts or updates a client and returns the stored record (with <see cref="Client.CreatedAtUtc"/> stamped).</summary>
    ValueTask<Client> UpsertAsync(Client client, CancellationToken ct = default);

    /// <summary>Fetches a client by id, or null when absent.</summary>
    ValueTask<Client?> GetAsync(string clientId, CancellationToken ct = default);

    /// <summary>Case-insensitive substring search over name, email and phone.</summary>
    ValueTask<IReadOnlyList<Client>> SearchAsync(string query, int topK = 20, CancellationToken ct = default);

    /// <summary>All clients, ordered by name.</summary>
    ValueTask<IReadOnlyList<Client>> ListAsync(CancellationToken ct = default);

    /// <summary>Removes a client. Returns true if one was removed.</summary>
    ValueTask<bool> RemoveAsync(string clientId, CancellationToken ct = default);
}
