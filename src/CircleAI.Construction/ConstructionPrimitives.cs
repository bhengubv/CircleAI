// ConstructionPrimitives.cs — (3.3.0)
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Construction;

public sealed record Project(string ProjectId, string Name, DateTime StartOn, DateTime? EndOn, decimal Budget, string Currency);
public sealed record ConstructionTask(string ConstructionTaskId, string ProjectId, string Description, DateTime DueOn, bool Completed);
public sealed record CostEntry(string EntryId, string ProjectId, string Category, decimal Amount, DateTimeOffset AtUtc);

public interface IConstructionBoard
{
    void Create(Project p);
    Project? GetProject(string id);
    void Add(ConstructionTask t);
    void Complete(string taskId);
    IReadOnlyList<ConstructionTask> OpenConstructionTasksFor(string projectId);
    void RecordCost(CostEntry c);
    decimal SpendFor(string projectId);
    decimal RemainingBudget(string projectId);
}

public sealed class InMemoryConstructionBoard : IConstructionBoard
{
    private readonly ConcurrentDictionary<string, Project> _projects = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConstructionTask> _tasks = new(StringComparer.Ordinal);
    private readonly List<CostEntry> _costs = new();
    private readonly object _lock = new();

    public void Create(Project p) { ArgumentNullException.ThrowIfNull(p); _projects[p.ProjectId] = p; }
    public Project? GetProject(string id) => _projects.GetValueOrDefault(id);
    public void Add(ConstructionTask t) { ArgumentNullException.ThrowIfNull(t); _tasks[t.ConstructionTaskId] = t; }

    public void Complete(string taskId)
    {
        if (!_tasks.TryGetValue(taskId, out var t)) throw new InvalidOperationException($"Unknown task {taskId}");
        _tasks[taskId] = t with { Completed = true };
    }

    public IReadOnlyList<ConstructionTask> OpenConstructionTasksFor(string projectId)
        => _tasks.Values.Where(t => t.ProjectId == projectId && !t.Completed).OrderBy(t => t.DueOn).ToArray();

    public void RecordCost(CostEntry c) { ArgumentNullException.ThrowIfNull(c); lock (_lock) _costs.Add(c); }
    public decimal SpendFor(string projectId) { lock (_lock) return _costs.Where(c => c.ProjectId == projectId).Sum(c => c.Amount); }
    public decimal RemainingBudget(string projectId)
    {
        if (!_projects.TryGetValue(projectId, out var p)) throw new InvalidOperationException($"Unknown project {projectId}");
        return p.Budget - SpendFor(projectId);
    }
}
