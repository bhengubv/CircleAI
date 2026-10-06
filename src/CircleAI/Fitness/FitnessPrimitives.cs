// FitnessPrimitives.cs — (3.3.0)
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Fitness;

public sealed record Workout(string WorkoutId, string UserId, string Kind, int DurationMinutes, double CaloriesBurned, DateTimeOffset AtUtc);
public sealed record FitnessGoal(string GoalId, string UserId, string Metric, double Target, DateTime DueOn);
public sealed record ExerciseSet(string SetId, string WorkoutId, string Exercise, int Reps, double WeightKg);

public interface IFitnessBoard
{
    void Log(Workout w);
    IReadOnlyList<Workout> WorkoutsThisWeek(string userId, DateTimeOffset now);
    double TotalCaloriesSince(string userId, DateTimeOffset since);
    void SetGoal(FitnessGoal g);
    IReadOnlyList<FitnessGoal> GoalsFor(string userId);
    void AddSet(ExerciseSet s);
    IReadOnlyList<ExerciseSet> SetsFor(string workoutId);
    int WorkoutCount { get; }
    IReadOnlyList<Workout> WorkoutsByKind(string userId, string kind);
    bool RemoveGoal(string goalId);
    FitnessGoal? GoalByMetric(string userId, string metric);
    double AvgDurationSince(string userId, DateTimeOffset since);
    double TotalVolumeKg(string workoutId);
}

public sealed class InMemoryFitnessBoard : IFitnessBoard
{
    private readonly List<Workout> _workouts = new();
    private readonly ConcurrentDictionary<string, FitnessGoal> _goals = new(StringComparer.Ordinal);
    private readonly List<ExerciseSet> _sets = new();
    private readonly object _lock = new();

    public void Log(Workout w) { ArgumentNullException.ThrowIfNull(w); lock (_lock) _workouts.Add(w); }
    public IReadOnlyList<Workout> WorkoutsThisWeek(string userId, DateTimeOffset now)
    {
        var weekStart = now.Date.AddDays(-(int)now.DayOfWeek);
        lock (_lock) return _workouts.Where(w => w.UserId == userId && w.AtUtc >= weekStart).OrderBy(w => w.AtUtc).ToArray();
    }
    public double TotalCaloriesSince(string userId, DateTimeOffset since)
    { lock (_lock) return _workouts.Where(w => w.UserId == userId && w.AtUtc >= since).Sum(w => w.CaloriesBurned); }
    public void SetGoal(FitnessGoal g) { ArgumentNullException.ThrowIfNull(g); _goals[g.GoalId] = g; }
    public IReadOnlyList<FitnessGoal> GoalsFor(string userId) => _goals.Values.Where(g => g.UserId == userId).ToArray();
    public void AddSet(ExerciseSet s) { ArgumentNullException.ThrowIfNull(s); lock (_lock) _sets.Add(s); }
    public IReadOnlyList<ExerciseSet> SetsFor(string workoutId)
    { lock (_lock) return _sets.Where(s => s.WorkoutId == workoutId).ToArray(); }

    public int WorkoutCount { get { lock (_lock) return _workouts.Count; } }

    public IReadOnlyList<Workout> WorkoutsByKind(string userId, string kind)
    {
        lock (_lock) return _workouts.Where(w => w.UserId == userId && string.Equals(w.Kind, kind, StringComparison.OrdinalIgnoreCase))
                                     .OrderByDescending(w => w.AtUtc).ToArray();
    }

    public bool RemoveGoal(string goalId) => _goals.TryRemove(goalId, out _);

    public FitnessGoal? GoalByMetric(string userId, string metric)
        => _goals.Values.Where(g => g.UserId == userId && string.Equals(g.Metric, metric, StringComparison.OrdinalIgnoreCase))
                        .OrderBy(g => g.DueOn).FirstOrDefault();

    public double AvgDurationSince(string userId, DateTimeOffset since)
    {
        lock (_lock) return _workouts.Where(w => w.UserId == userId && w.AtUtc >= since)
                                     .Select(w => (double)w.DurationMinutes).DefaultIfEmpty(0).Average();
    }

    public double TotalVolumeKg(string workoutId)
    {
        lock (_lock) return _sets.Where(s => s.WorkoutId == workoutId).Sum(s => s.Reps * s.WeightKg);
    }
}
