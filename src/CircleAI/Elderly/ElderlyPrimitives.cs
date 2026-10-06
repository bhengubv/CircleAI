// ElderlyPrimitives.cs — (3.3.0)
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Elderly;

public sealed record CarePlan(string PlanId, string ResidentName, IReadOnlyList<string> MedicalConditions, IReadOnlyList<string> Allergies, string CarerNotes);
public sealed record MedReminder(string ReminderId, string ResidentName, string Medication, TimeSpan DailyAt, bool Active);
public sealed record CheckIn(string CheckInId, string ResidentName, DateTimeOffset AtUtc, string Status, string? Note);

public interface IElderlyCareBoard
{
    void SetPlan(CarePlan p);
    CarePlan? GetPlan(string resident);
    void AddReminder(MedReminder r);
    void DeactivateReminder(string reminderId);
    IReadOnlyList<MedReminder> ActiveRemindersFor(string resident);
    void RecordCheckIn(CheckIn c);
    CheckIn? LatestCheckIn(string resident);
    bool MissedCheckIn(string resident, DateTimeOffset since);
}

public sealed class InMemoryElderlyCareBoard : IElderlyCareBoard
{
    private readonly ConcurrentDictionary<string, CarePlan> _plans = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, MedReminder> _reminders = new(StringComparer.Ordinal);
    private readonly List<CheckIn> _checkIns = new();
    private readonly object _lock = new();

    public void SetPlan(CarePlan p) { ArgumentNullException.ThrowIfNull(p); _plans[p.ResidentName] = p; }
    public CarePlan? GetPlan(string resident) => _plans.GetValueOrDefault(resident);
    public void AddReminder(MedReminder r) { ArgumentNullException.ThrowIfNull(r); _reminders[r.ReminderId] = r; }
    public void DeactivateReminder(string reminderId)
    {
        if (!_reminders.TryGetValue(reminderId, out var r)) throw new InvalidOperationException($"Unknown reminder {reminderId}");
        _reminders[reminderId] = r with { Active = false };
    }
    public IReadOnlyList<MedReminder> ActiveRemindersFor(string resident)
        => _reminders.Values.Where(r => r.ResidentName == resident && r.Active).ToArray();
    public void RecordCheckIn(CheckIn c) { ArgumentNullException.ThrowIfNull(c); lock (_lock) _checkIns.Add(c); }
    public CheckIn? LatestCheckIn(string resident)
    { lock (_lock) return _checkIns.Where(c => c.ResidentName == resident).OrderByDescending(c => c.AtUtc).FirstOrDefault(); }
    public bool MissedCheckIn(string resident, DateTimeOffset since)
    {
        var latest = LatestCheckIn(resident);
        return latest is null || latest.AtUtc < since;
    }
}
