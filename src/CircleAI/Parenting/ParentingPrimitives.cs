// ParentingPrimitives.cs
//
// (3.3.0) Real domain types + in-memory store for the Parenting
// vertical: children, milestones, school-day routines.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Parenting;

public sealed record Child(string ChildId, string Name, DateTime DateOfBirth, string? Gender);
public sealed record Milestone(string MilestoneId, string ChildId, string Category, string Description, DateTimeOffset AchievedAtUtc);
public sealed record RoutineEntry(string Time, string Activity);
public sealed record Routine(string ChildId, DayOfWeek DayOfWeek, IReadOnlyList<RoutineEntry> Entries);

public interface IParentingBoard
{
    void AddChild(Child c);
    Child? GetChild(string id);
    IReadOnlyList<Child> Children { get; }
    void RecordMilestone(Milestone m);
    IReadOnlyList<Milestone> MilestonesFor(string childId);
    void SetRoutine(Routine r);
    Routine? GetRoutine(string childId, DayOfWeek dow);
    TimeSpan AgeAsOf(string childId, DateTime at);
}

public sealed class InMemoryParentingBoard : IParentingBoard
{
    private readonly ConcurrentDictionary<string, Child> _children = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, List<Milestone>> _milestones = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Routine> _routines = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public void AddChild(Child c) { ArgumentNullException.ThrowIfNull(c); _children[c.ChildId] = c; }
    public Child? GetChild(string id) => _children.GetValueOrDefault(id);
    public IReadOnlyList<Child> Children => _children.Values.OrderBy(c => c.Name).ToArray();

    public void RecordMilestone(Milestone m)
    {
        ArgumentNullException.ThrowIfNull(m);
        if (string.IsNullOrWhiteSpace(m.ChildId)) throw new ArgumentException("ChildId required");
        lock (_lock)
        {
            var list = _milestones.GetOrAdd(m.ChildId, _ => new List<Milestone>());
            list.Add(m);
        }
    }

    public IReadOnlyList<Milestone> MilestonesFor(string childId)
    {
        lock (_lock)
        {
            if (!_milestones.TryGetValue(childId, out var list)) return Array.Empty<Milestone>();
            return list.OrderByDescending(m => m.AchievedAtUtc).ToArray();
        }
    }

    public void SetRoutine(Routine r)
    {
        ArgumentNullException.ThrowIfNull(r);
        _routines[Key(r.ChildId, r.DayOfWeek)] = r;
    }

    public Routine? GetRoutine(string childId, DayOfWeek dow)
        => _routines.GetValueOrDefault(Key(childId, dow));

    public TimeSpan AgeAsOf(string childId, DateTime at)
    {
        if (!_children.TryGetValue(childId, out var c)) throw new InvalidOperationException($"Unknown child {childId}");
        return at - c.DateOfBirth;
    }

    private static string Key(string childId, DayOfWeek d) => $"{childId}/{d}";
}
