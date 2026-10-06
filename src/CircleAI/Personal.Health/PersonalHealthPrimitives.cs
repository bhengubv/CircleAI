// PersonalHealthPrimitives.cs
//
// (3.3.0) Real domain types + in-memory store for personal health:
// vitals (BP, glucose, weight), allergies, medications, last-reading
// helpers. Privacy: instances are user-scoped and never written to a
// shared store.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Personal.Health;

public enum VitalKind { BloodPressureSystolic, BloodPressureDiastolic, GlucoseMgDl, WeightKg, HeartRateBpm, TemperatureC, OxygenPct, StepsCount }

public sealed record VitalReading(VitalKind Kind, double Value, DateTimeOffset AtUtc, string? Note);
public sealed record Allergy(string AllergyId, string Substance, string Severity);
public sealed record Medication(string MedId, string Name, string Dose, string Frequency, DateTimeOffset StartedAtUtc, DateTimeOffset? EndedAtUtc);

public interface IPersonalHealthBoard
{
    void Record(VitalReading v);
    IReadOnlyList<VitalReading> ReadSince(VitalKind kind, DateTimeOffset since);
    VitalReading? Latest(VitalKind kind);
    void AddAllergy(Allergy a);
    IReadOnlyList<Allergy> Allergies { get; }
    void AddMedication(Medication m);
    void EndMedication(string medId, DateTimeOffset endedAtUtc);
    IReadOnlyList<Medication> ActiveMedications();
}

public sealed class InMemoryPersonalHealthBoard : IPersonalHealthBoard
{
    private readonly List<VitalReading> _vitals = new();
    private readonly ConcurrentDictionary<string, Allergy> _allergies = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Medication> _meds = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public void Record(VitalReading v) { ArgumentNullException.ThrowIfNull(v); lock (_lock) _vitals.Add(v); }

    public IReadOnlyList<VitalReading> ReadSince(VitalKind kind, DateTimeOffset since)
    {
        lock (_lock) return _vitals.Where(v => v.Kind == kind && v.AtUtc >= since).OrderBy(v => v.AtUtc).ToArray();
    }

    public VitalReading? Latest(VitalKind kind)
    {
        lock (_lock) return _vitals.Where(v => v.Kind == kind).OrderByDescending(v => v.AtUtc).FirstOrDefault();
    }

    public void AddAllergy(Allergy a) { ArgumentNullException.ThrowIfNull(a); _allergies[a.AllergyId] = a; }
    public IReadOnlyList<Allergy> Allergies => _allergies.Values.ToArray();

    public void AddMedication(Medication m) { ArgumentNullException.ThrowIfNull(m); _meds[m.MedId] = m; }

    public void EndMedication(string medId, DateTimeOffset endedAtUtc)
    {
        if (!_meds.TryGetValue(medId, out var m)) throw new InvalidOperationException($"Unknown medication {medId}");
        _meds[medId] = m with { EndedAtUtc = endedAtUtc };
    }

    public IReadOnlyList<Medication> ActiveMedications()
        => _meds.Values.Where(m => m.EndedAtUtc is null).OrderBy(m => m.Name).ToArray();
}
