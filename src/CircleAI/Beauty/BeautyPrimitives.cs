// BeautyPrimitives.cs — (3.3.0)
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Beauty;

public sealed record Treatment(string TreatmentId, string Name, int DurationMinutes, decimal Price, string Currency);
public sealed record Appointment(string ApptId, string ClientName, string TreatmentId, DateTimeOffset AtUtc, string? Notes);
public sealed record SkinProfile(string ClientName, string SkinType, IReadOnlyList<string> Concerns);

public interface IBeautyBoard
{
    void AddTreatment(Treatment t);
    Treatment? GetTreatment(string id);
    void Book(Appointment a);
    IReadOnlyList<Appointment> AppointmentsBetween(DateTimeOffset start, DateTimeOffset end);
    void SaveProfile(SkinProfile p);
    SkinProfile? GetProfile(string clientName);
    IReadOnlyList<Treatment> RecommendFor(string clientName);
    int TreatmentCount { get; }
    bool CancelAppointment(string apptId);
    IReadOnlyList<Appointment> AppointmentsForClient(string clientName);
    IReadOnlyList<Treatment> TreatmentsUnder(decimal maxPrice);
    Appointment? NextAppointmentFor(string clientName, DateTimeOffset now);
    decimal ScheduledRevenueBetween(DateTimeOffset start, DateTimeOffset end);
}

public sealed class InMemoryBeautyBoard : IBeautyBoard
{
    private readonly ConcurrentDictionary<string, Treatment> _treatments = new(StringComparer.Ordinal);
    private readonly List<Appointment> _appts = new();
    private readonly ConcurrentDictionary<string, SkinProfile> _profiles = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public void AddTreatment(Treatment t) { ArgumentNullException.ThrowIfNull(t); _treatments[t.TreatmentId] = t; }
    public Treatment? GetTreatment(string id) => _treatments.GetValueOrDefault(id);
    public void Book(Appointment a) { ArgumentNullException.ThrowIfNull(a); lock (_lock) _appts.Add(a); }
    public IReadOnlyList<Appointment> AppointmentsBetween(DateTimeOffset start, DateTimeOffset end)
    { lock (_lock) return _appts.Where(a => a.AtUtc >= start && a.AtUtc <= end).OrderBy(a => a.AtUtc).ToArray(); }
    public void SaveProfile(SkinProfile p) { ArgumentNullException.ThrowIfNull(p); _profiles[p.ClientName] = p; }
    public SkinProfile? GetProfile(string clientName) => _profiles.GetValueOrDefault(clientName);

    public IReadOnlyList<Treatment> RecommendFor(string clientName)
    {
        if (!_profiles.TryGetValue(clientName, out var p)) return Array.Empty<Treatment>();
        return _treatments.Values.Where(t => p.Concerns.Any(c => t.Name.Contains(c, StringComparison.OrdinalIgnoreCase))).ToArray();
    }

    public int TreatmentCount => _treatments.Count;

    public bool CancelAppointment(string apptId)
    {
        lock (_lock) return _appts.RemoveAll(a => string.Equals(a.ApptId, apptId, StringComparison.Ordinal)) > 0;
    }

    public IReadOnlyList<Appointment> AppointmentsForClient(string clientName)
    {
        lock (_lock) return _appts.Where(a => string.Equals(a.ClientName, clientName, StringComparison.OrdinalIgnoreCase))
                                  .OrderBy(a => a.AtUtc).ToArray();
    }

    public IReadOnlyList<Treatment> TreatmentsUnder(decimal maxPrice)
        => _treatments.Values.Where(t => t.Price <= maxPrice).OrderBy(t => t.Price).ToArray();

    public Appointment? NextAppointmentFor(string clientName, DateTimeOffset now)
    {
        lock (_lock) return _appts.Where(a => string.Equals(a.ClientName, clientName, StringComparison.OrdinalIgnoreCase) && a.AtUtc >= now)
                                  .OrderBy(a => a.AtUtc).FirstOrDefault();
    }

    public decimal ScheduledRevenueBetween(DateTimeOffset start, DateTimeOffset end)
    {
        lock (_lock)
        {
            return _appts.Where(a => a.AtUtc >= start && a.AtUtc <= end && _treatments.ContainsKey(a.TreatmentId))
                         .Sum(a => _treatments[a.TreatmentId].Price);
        }
    }
}
