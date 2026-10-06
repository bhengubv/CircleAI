// CrmBridge.cs — (0.1.0) Reuse CircleAI.CRM instead of duplicating contacts.
//
// AUDIT NOTE. CircleAI.CRM already ships a contact surface (IContactStore /
// Contact / IActivityLog). BusinessOps does NOT stand up a second generic contact
// store. A billing Client carries extra fields a CRM Contact has no home for (tax
// number, terms, currency), but a client IS a contact — so these adapters project
// between the two and can mirror the client book into any IContactStore. This is
// the "reuse, don't duplicate" seam; it is entirely optional and additive.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.CRM;

namespace CircleAI.BusinessOps;

/// <summary>Adapters between BusinessOps and the CircleAI.CRM contact surface.</summary>
public static class CrmBridge
{
    /// <summary>
    /// Projects a billing <see cref="Client"/> onto a CRM <see cref="Contact"/>.
    /// Billing-only fields (tax number, terms, currency) have no Contact home and
    /// are intentionally dropped.
    /// </summary>
    public static Contact ToContact(this Client client, string? companyId = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        return new Contact(client.ClientId, client.Name, client.Email, client.Phone, companyId);
    }

    /// <summary>Adopts a CRM <see cref="Contact"/> as a billing <see cref="Client"/>, applying billing defaults.</summary>
    public static Client ToClient(this Contact contact, string defaultCurrency = Currencies.DefaultCurrency, int paymentTermsDays = 30)
    {
        ArgumentNullException.ThrowIfNull(contact);
        return new Client(
            ClientId: contact.ContactId,
            Name: contact.FullName,
            Email: contact.Email,
            Phone: contact.Phone,
            DefaultCurrency: defaultCurrency,
            PaymentTermsDays: paymentTermsDays);
    }

    /// <summary>
    /// Mirrors the whole client book into a CRM contact store so the same people
    /// are reachable from CRM tooling. One-way (BusinessOps → CRM) by design;
    /// returns the number of contacts written.
    /// </summary>
    public static async ValueTask<int> MirrorToCrmAsync(this IClientBook clients, IContactStore contacts, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(contacts);
        var all = await clients.ListAsync(ct).ConfigureAwait(false);
        var n = 0;
        foreach (var client in all)
        {
            await contacts.UpsertAsync(client.ToContact(), ct).ConfigureAwait(false);
            n++;
        }
        return n;
    }

    /// <summary>
    /// Represents a reminder (e.g. a completed follow-up) as a CRM
    /// <see cref="Activity"/> against a contact, so client history lands in one
    /// place. The reminder's <see cref="ReminderKind"/> becomes the activity kind.
    /// </summary>
    public static Activity ToActivity(this Reminder reminder, string contactId)
    {
        ArgumentNullException.ThrowIfNull(reminder);
        ArgumentException.ThrowIfNullOrWhiteSpace(contactId);
        return new Activity(reminder.ReminderId, contactId, reminder.Kind.ToString(), reminder.Title, reminder.DueAtUtc);
    }
}
