// SafetyPrimitives.cs
//
// (3.3.0) Real domain types + in-memory store for the Safety vertical:
// incidents, hazards, emergency contacts, severity-routing.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Safety;

public enum IncidentSeverity { Info, Warning, Critical, Emergency }

public sealed record Incident(string IncidentId, IncidentSeverity Severity, string Description, double? Latitude, double? Longitude, DateTimeOffset AtUtc);
public sealed record Hazard(string HazardId, string Description, string Category, DateTimeOffset NotedUtc);
public sealed record EmergencyContact(string ContactId, string Name, string Phone, string Relationship);

public interface ISafetyBoard
{
    void Log(Incident i);
    IReadOnlyList<Incident> Active { get; }
    int IncidentCount { get; }
    IReadOnlyList<Incident> AtOrAboveSeverity(IncidentSeverity minimum);
    void NoteHazard(Hazard h);
    IReadOnlyList<Hazard> Hazards { get; }
    IReadOnlyList<Hazard> HazardsByCategory(string category);
    void AddContact(EmergencyContact c);
    bool RemoveContact(string contactId);
    EmergencyContact? FirstContact { get; }
    IReadOnlyList<EmergencyContact> Contacts { get; }
    IReadOnlyList<EmergencyContact> ContactsByRelationship(string relationship);
}

public sealed class InMemorySafetyBoard : ISafetyBoard
{
    private readonly List<Incident> _incidents = new();
    private readonly ConcurrentDictionary<string, Hazard> _hazards = new(StringComparer.Ordinal);
    private readonly List<EmergencyContact> _contacts = new();
    private readonly object _lock = new();

    public void Log(Incident i) { ArgumentNullException.ThrowIfNull(i); lock (_lock) _incidents.Add(i); }

    public IReadOnlyList<Incident> Active { get { lock (_lock) return _incidents.OrderByDescending(i => i.AtUtc).ToArray(); } }

    /// <summary>Total number of incidents logged since this board was created.</summary>
    public int IncidentCount { get { lock (_lock) return _incidents.Count; } }

    public IReadOnlyList<Incident> AtOrAboveSeverity(IncidentSeverity minimum)
    {
        lock (_lock) return _incidents.Where(i => (int)i.Severity >= (int)minimum).OrderByDescending(i => i.AtUtc).ToArray();
    }

    public void NoteHazard(Hazard h) { ArgumentNullException.ThrowIfNull(h); _hazards[h.HazardId] = h; }
    public IReadOnlyList<Hazard> Hazards => _hazards.Values.OrderByDescending(h => h.NotedUtc).ToArray();

    /// <summary>Hazards filed under a given category (case-insensitive), newest-first.</summary>
    public IReadOnlyList<Hazard> HazardsByCategory(string category)
    {
        if (string.IsNullOrEmpty(category)) return Array.Empty<Hazard>();
        return _hazards.Values
            .Where(h => string.Equals(h.Category, category, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(h => h.NotedUtc)
            .ToArray();
    }

    public void AddContact(EmergencyContact c) { ArgumentNullException.ThrowIfNull(c); lock (_lock) _contacts.Add(c); }

    /// <summary>Remove the first emergency contact matching an id. Returns true if one was removed.</summary>
    public bool RemoveContact(string contactId)
    {
        if (string.IsNullOrEmpty(contactId)) return false;
        lock (_lock)
        {
            var idx = _contacts.FindIndex(c => string.Equals(c.ContactId, contactId, StringComparison.Ordinal));
            if (idx < 0) return false;
            _contacts.RemoveAt(idx);
            return true;
        }
    }

    public EmergencyContact? FirstContact { get { lock (_lock) return _contacts.FirstOrDefault(); } }
    public IReadOnlyList<EmergencyContact> Contacts { get { lock (_lock) return _contacts.ToArray(); } }

    /// <summary>Emergency contacts with a given relationship (e.g. "spouse", "doctor"), case-insensitive.</summary>
    public IReadOnlyList<EmergencyContact> ContactsByRelationship(string relationship)
    {
        if (string.IsNullOrEmpty(relationship)) return Array.Empty<EmergencyContact>();
        lock (_lock)
            return _contacts
                .Where(c => string.Equals(c.Relationship, relationship, StringComparison.OrdinalIgnoreCase))
                .ToArray();
    }
}
