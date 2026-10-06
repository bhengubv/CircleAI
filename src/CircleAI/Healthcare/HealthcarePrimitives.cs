// HealthcarePrimitives.cs — (3.3.0)
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Healthcare;

public sealed record Patient(string PatientId, string Name, DateTime DateOfBirth);
public sealed record HealthAppointment(string ApptId, string PatientId, string Provider, DateTimeOffset AtUtc, string Status);
public sealed record Prescription(string RxId, string PatientId, string MedicationName, string Dose, string Frequency, DateTimeOffset PrescribedUtc);

public interface IHealthcareBoard
{
    void Register(Patient p);
    Patient? GetPatient(string id);
    void Schedule(HealthAppointment a);
    void UpdateStatus(string apptId, string status);
    IReadOnlyList<HealthAppointment> AppointmentsFor(string patientId);
    void Prescribe(Prescription r);
    IReadOnlyList<Prescription> PrescriptionsFor(string patientId);
}

public sealed class InMemoryHealthcareBoard : IHealthcareBoard
{
    private readonly ConcurrentDictionary<string, Patient> _patients = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, HealthAppointment> _appts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Prescription> _rx = new(StringComparer.Ordinal);

    public void Register(Patient p) { ArgumentNullException.ThrowIfNull(p); _patients[p.PatientId] = p; }
    public Patient? GetPatient(string id) => _patients.GetValueOrDefault(id);
    public void Schedule(HealthAppointment a) { ArgumentNullException.ThrowIfNull(a); _appts[a.ApptId] = a; }
    public void UpdateStatus(string apptId, string status)
    {
        if (!_appts.TryGetValue(apptId, out var a)) throw new InvalidOperationException($"Unknown appointment {apptId}");
        _appts[apptId] = a with { Status = status };
    }
    public IReadOnlyList<HealthAppointment> AppointmentsFor(string patientId)
        => _appts.Values.Where(a => a.PatientId == patientId).OrderBy(a => a.AtUtc).ToArray();
    public void Prescribe(Prescription r) { ArgumentNullException.ThrowIfNull(r); _rx[r.RxId] = r; }
    public IReadOnlyList<Prescription> PrescriptionsFor(string patientId)
        => _rx.Values.Where(p => p.PatientId == patientId).OrderByDescending(p => p.PrescribedUtc).ToArray();
}
