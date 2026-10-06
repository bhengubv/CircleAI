// SportsPrimitives.cs
//
// (3.3.0) Real domain types + in-memory store for the Sports vertical:
// workouts, sessions, personal bests, weekly volume.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Sports;

public enum DistanceKind { Run, Bike, Swim, Walk, Row }

public sealed record Activity(string ActivityId, string UserId, DistanceKind Kind, double DistanceKm, TimeSpan Duration, DateTimeOffset AtUtc);
public sealed record PersonalBest(string UserId, DistanceKind Kind, double DistanceKm, TimeSpan Time, DateTimeOffset AchievedUtc);
public sealed record TrainingSession(string SessionId, string UserId, string Plan, DateTimeOffset ScheduledUtc, bool Completed);

public interface ISportsBoard
{
    void Log(Activity a);
    IReadOnlyList<Activity> History(string userId, int limit = 50);
    double TotalKmThisWeek(string userId, DistanceKind kind, DateTimeOffset now);
    PersonalBest? Best(string userId, DistanceKind kind, double distanceKm);
    void Schedule(TrainingSession s);
    void Complete(string sessionId);
    IReadOnlyList<TrainingSession> Upcoming(string userId);
}

public sealed class InMemorySportsBoard : ISportsBoard
{
    private readonly List<Activity> _activities = new();
    private readonly ConcurrentDictionary<string, TrainingSession> _sessions = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public void Log(Activity a) { ArgumentNullException.ThrowIfNull(a); lock (_lock) _activities.Add(a); }

    public IReadOnlyList<Activity> History(string userId, int limit = 50)
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        lock (_lock) return _activities.Where(a => a.UserId == userId).OrderByDescending(a => a.AtUtc).Take(limit).ToArray();
    }

    public double TotalKmThisWeek(string userId, DistanceKind kind, DateTimeOffset now)
    {
        var weekStart = now.Date.AddDays(-(int)now.DayOfWeek);
        lock (_lock) return _activities.Where(a => a.UserId == userId && a.Kind == kind && a.AtUtc >= weekStart).Sum(a => a.DistanceKm);
    }

    public PersonalBest? Best(string userId, DistanceKind kind, double distanceKm)
    {
        lock (_lock)
        {
            var hit = _activities.Where(a => a.UserId == userId && a.Kind == kind && a.DistanceKm >= distanceKm)
                                 .OrderBy(a => a.Duration).FirstOrDefault();
            return hit is null ? null : new PersonalBest(userId, kind, distanceKm, hit.Duration, hit.AtUtc);
        }
    }

    public void Schedule(TrainingSession s) { ArgumentNullException.ThrowIfNull(s); _sessions[s.SessionId] = s; }

    public void Complete(string sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var s)) throw new InvalidOperationException($"Unknown session {sessionId}");
        _sessions[sessionId] = s with { Completed = true };
    }

    public IReadOnlyList<TrainingSession> Upcoming(string userId)
        => _sessions.Values.Where(s => s.UserId == userId && !s.Completed && s.ScheduledUtc >= DateTimeOffset.UtcNow)
                           .OrderBy(s => s.ScheduledUtc).ToArray();
}
