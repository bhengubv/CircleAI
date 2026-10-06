// PetsPrimitives.cs
//
// (3.3.0) Real domain types + in-memory store for the Pets vertical:
// pets, vaccinations, weight history, vet appointments.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Pets;

public sealed record Pet(string PetId, string Name, string Species, string? Breed, DateTime DateOfBirth);
public sealed record Vaccination(string PetId, string Vaccine, DateTimeOffset AdministeredUtc, DateTimeOffset? BoosterDueUtc);
public sealed record WeightSample(string PetId, double WeightKg, DateTimeOffset AtUtc);
public sealed record VetAppointment(string ApptId, string PetId, string Reason, DateTimeOffset AtUtc, string Vet);

public interface IPetsBoard
{
    void Add(Pet p);
    Pet? GetPet(string id);
    IReadOnlyList<Pet> Pets { get; }
    void RecordVaccination(Vaccination v);
    IReadOnlyList<Vaccination> VaccinationsFor(string petId);
    void RecordWeight(WeightSample s);
    IReadOnlyList<WeightSample> WeightHistory(string petId);
    void Schedule(VetAppointment a);
    IReadOnlyList<VetAppointment> UpcomingAppointments();
}

public sealed class InMemoryPetsBoard : IPetsBoard
{
    private readonly ConcurrentDictionary<string, Pet> _pets = new(StringComparer.Ordinal);
    private readonly List<Vaccination> _vax = new();
    private readonly List<WeightSample> _weights = new();
    private readonly ConcurrentDictionary<string, VetAppointment> _appts = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public void Add(Pet p) { ArgumentNullException.ThrowIfNull(p); _pets[p.PetId] = p; }
    public Pet? GetPet(string id) => _pets.GetValueOrDefault(id);
    public IReadOnlyList<Pet> Pets => _pets.Values.OrderBy(p => p.Name).ToArray();

    public void RecordVaccination(Vaccination v) { ArgumentNullException.ThrowIfNull(v); lock (_lock) _vax.Add(v); }
    public IReadOnlyList<Vaccination> VaccinationsFor(string petId)
    { lock (_lock) return _vax.Where(v => v.PetId == petId).OrderByDescending(v => v.AdministeredUtc).ToArray(); }

    public void RecordWeight(WeightSample s) { ArgumentNullException.ThrowIfNull(s); lock (_lock) _weights.Add(s); }
    public IReadOnlyList<WeightSample> WeightHistory(string petId)
    { lock (_lock) return _weights.Where(w => w.PetId == petId).OrderBy(w => w.AtUtc).ToArray(); }

    public void Schedule(VetAppointment a) { ArgumentNullException.ThrowIfNull(a); _appts[a.ApptId] = a; }
    public IReadOnlyList<VetAppointment> UpcomingAppointments()
        => _appts.Values.Where(a => a.AtUtc >= DateTimeOffset.UtcNow).OrderBy(a => a.AtUtc).ToArray();
}
