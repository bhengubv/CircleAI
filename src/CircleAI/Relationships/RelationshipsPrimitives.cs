// RelationshipsPrimitives.cs
//
// (3.3.0) Real domain types + in-memory CRM-lite for personal
// relationships: contacts, important dates, last-contact tracker.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Relationships;

public sealed record PersonContact(string ContactId, string Name, string Relationship, string? Notes);
public sealed record ImportantDate(string DateId, string ContactId, string Kind, DateTime Date);
public sealed record ContactEvent(string ContactId, string Kind, DateTimeOffset AtUtc, string? Note);

public interface IRelationshipsBoard
{
    void AddContact(PersonContact c);
    PersonContact? GetContact(string id);
    IReadOnlyList<PersonContact> Contacts { get; }
    void AddImportantDate(ImportantDate d);
    IReadOnlyList<ImportantDate> UpcomingThisMonth();
    void RecordTouchpoint(ContactEvent e);
    DateTimeOffset? LastContact(string contactId);
    IReadOnlyList<PersonContact> NotContactedSince(DateTimeOffset cutoff);
}

public sealed class InMemoryRelationshipsBoard : IRelationshipsBoard
{
    private readonly ConcurrentDictionary<string, PersonContact> _contacts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ImportantDate> _dates = new(StringComparer.Ordinal);
    private readonly List<ContactEvent> _events = new();
    private readonly object _lock = new();

    public void AddContact(PersonContact c) { ArgumentNullException.ThrowIfNull(c); _contacts[c.ContactId] = c; }
    public PersonContact? GetContact(string id) => _contacts.GetValueOrDefault(id);
    public IReadOnlyList<PersonContact> Contacts => _contacts.Values.OrderBy(c => c.Name).ToArray();

    public void AddImportantDate(ImportantDate d) { ArgumentNullException.ThrowIfNull(d); _dates[d.DateId] = d; }

    public IReadOnlyList<ImportantDate> UpcomingThisMonth()
    {
        var now = DateTime.UtcNow;
        return _dates.Values.Where(d => d.Date.Month == now.Month).OrderBy(d => d.Date.Day).ToArray();
    }

    public void RecordTouchpoint(ContactEvent e) { ArgumentNullException.ThrowIfNull(e); lock (_lock) _events.Add(e); }

    public DateTimeOffset? LastContact(string contactId)
    {
        lock (_lock)
        {
            return _events.Where(e => e.ContactId == contactId).OrderByDescending(e => e.AtUtc).FirstOrDefault()?.AtUtc;
        }
    }

    public IReadOnlyList<PersonContact> NotContactedSince(DateTimeOffset cutoff)
    {
        return _contacts.Values.Where(c =>
        {
            var last = LastContact(c.ContactId);
            return last is null || last < cutoff;
        }).ToArray();
    }
}
