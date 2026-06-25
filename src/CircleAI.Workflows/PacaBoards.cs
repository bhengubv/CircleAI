// PacaBoards.cs
//
// (3.3.0) Sprintboard surface ported from paca: rich JSON description,
// custom fields, story points, importance, parent/child relations,
// status columns with position-ordered workflow, drag-and-drop status
// transitions, sprints with lifecycle states, Scrumban swimlanes,
// per-view persistent configs (filters + sort + visible fields),
// tags, lazy-load pagination per column.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Workflows;

/// <summary>(3.3.0) Sprint lifecycle.</summary>
public enum SprintState { Planning, Active, Completed }

/// <summary>(3.3.0) Status column in the workflow.</summary>
public sealed record StatusColumn(
    string  Name,             // "todo" / "in_progress" / "in_review" / "done"
    string  Category,         // "open" / "in-flight" / "review" / "closed" / "cancelled" / "blocked"
    int     Position,
    bool    Collapsed);

/// <summary>(3.3.0) Sprint.</summary>
public sealed record PacaSprint(
    string         Id,
    string         ProjectId,
    string         Name,
    string         Goal,
    DateTimeOffset StartDate,
    DateTimeOffset EndDate,
    SprintState    State);

/// <summary>(3.3.0) Extra board-only metadata on top of <see cref="PacaTask"/>.</summary>
public sealed record TaskBoardMetadata(
    string             ProjectId,
    int                Number,
    int                StoryPoints,
    int                Importance,           // 0..5
    string?            AssigneeMemberId,
    string?            ReporterMemberId,
    int?               ParentTaskNumber,
    string?            SprintId,
    IReadOnlyList<string> Tags,
    IReadOnlyDictionary<string, string> CustomFields,
    int                PositionInColumn);

/// <summary>(3.3.0) A per-user / per-board "named view".</summary>
public sealed record BoardView(
    string                              Name,
    string?                             FilterTagsCsv,
    string?                             FilterAssignee,
    string?                             SortBy,           // "importance" / "story_points" / "newest"
    bool                                SortDescending,
    IReadOnlyList<string>               VisibleColumns,
    IReadOnlyList<string>               VisibleFields);

/// <summary>(3.3.0) Board service over a project. Sprints + columns + per-task metadata + views.</summary>
public sealed class PacaBoard
{
    private readonly InMemoryPacaStore _tasks;
    private readonly ConcurrentDictionary<string, StatusColumn>          _columns      = new();
    private readonly ConcurrentDictionary<string, PacaSprint>            _sprints      = new();
    private readonly ConcurrentDictionary<(string ProjectId, int Number), TaskBoardMetadata> _metadata = new();
    private readonly ConcurrentDictionary<string, BoardView>             _views        = new();
    private readonly Func<DateTimeOffset> _clock;

    public PacaBoard(InMemoryPacaStore tasks, Func<DateTimeOffset>? clock = null)
    {
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);

        AddDefaultColumns();
    }

    private void AddDefaultColumns()
    {
        _columns["todo"]        = new StatusColumn("todo",        "open",      0, false);
        _columns["in_progress"] = new StatusColumn("in_progress", "in-flight", 1, false);
        _columns["in_review"]   = new StatusColumn("in_review",   "review",    2, false);
        _columns["done"]        = new StatusColumn("done",        "closed",    3, false);
        _columns["cancelled"]   = new StatusColumn("cancelled",   "cancelled", 4, false);
        _columns["blocked"]     = new StatusColumn("blocked",     "blocked",   5, true);
    }

    public IReadOnlyList<StatusColumn> Columns => _columns.Values.OrderBy(c => c.Position).ToList();

    public void AddColumn(StatusColumn col)
    {
        ArgumentNullException.ThrowIfNull(col);
        _columns[col.Name] = col;
    }

    public void CollapseColumn(string name, bool collapsed)
    {
        if (_columns.TryGetValue(name, out var col))
        {
            _columns[name] = col with { Collapsed = collapsed };
        }
    }

    /// <summary>(3.3.0) Move a task between status columns, updating its in-column position.</summary>
    public void MoveTask(string projectId, int number, string newStatus, int newPosition)
    {
        var task = _tasks.GetTaskByReference(projectId, $"{projectId}-{number}")
                   ?? _tasks.ListTasks(projectId).FirstOrDefault(t => t.Number == number);
        if (task is null) throw new InvalidOperationException("Task not found.");
        if (!_columns.ContainsKey(newStatus)) throw new ArgumentException($"Unknown status '{newStatus}'.", nameof(newStatus));

        _tasks.UpdateTask(task with { Status = newStatus });
        var meta = GetOrCreateMetadata(projectId, number) with { PositionInColumn = newPosition };
        _metadata[(projectId, number)] = meta;
    }

    /// <summary>(3.3.0) Attach board metadata to an existing task.</summary>
    public void SetTaskMetadata(TaskBoardMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        _metadata[(metadata.ProjectId, metadata.Number)] = metadata;
    }

    public TaskBoardMetadata? GetTaskMetadata(string projectId, int number)
        => _metadata.TryGetValue((projectId, number), out var meta) ? meta : null;

    /// <summary>(3.3.0) Paginated column read for lazy loading.</summary>
    public IReadOnlyList<PacaTask> TasksInColumn(string projectId, string status, int skip = 0, int take = 50)
    {
        var live = _tasks.ListTasks(projectId).Where(t => t.Status == status);
        return live
            .OrderBy(t => GetOrCreateMetadata(t.ProjectId, t.Number).PositionInColumn)
            .Skip(skip).Take(take).ToList();
    }

    /// <summary>(3.3.0) Tasks bucketed by sprint, useful for the Scrumban board.</summary>
    public IReadOnlyList<PacaTask> TasksInSprint(string sprintId)
    {
        return _metadata.Values
            .Where(m => m.SprintId == sprintId)
            .Select(m => _tasks.ListTasks(m.ProjectId).FirstOrDefault(t => t.Number == m.Number))
            .Where(t => t is not null)
            .Select(t => t!)
            .ToList();
    }

    /// <summary>(3.3.0) Create a sprint in Planning.</summary>
    public PacaSprint CreateSprint(string id, string projectId, string name, string goal, DateTimeOffset start, DateTimeOffset end)
    {
        var s = new PacaSprint(id, projectId, name, goal, start, end, SprintState.Planning);
        _sprints[id] = s;
        return s;
    }

    public PacaSprint? GetSprint(string id) => _sprints.TryGetValue(id, out var s) ? s : null;

    public PacaSprint StartSprint(string id)     => Transition(id, SprintState.Active);
    public PacaSprint CompleteSprint(string id)  => Transition(id, SprintState.Completed);

    private PacaSprint Transition(string id, SprintState to)
    {
        if (!_sprints.TryGetValue(id, out var sprint))
            throw new InvalidOperationException($"Sprint '{id}' not found.");
        var updated = sprint with { State = to };
        _sprints[id] = updated;
        return updated;
    }

    /// <summary>(3.3.0) Save a named view (filters + sort + visible fields).</summary>
    public void SaveView(BoardView view) => _views[view.Name] = view;

    public BoardView? GetView(string name) => _views.TryGetValue(name, out var v) ? v : null;

    public IReadOnlyList<BoardView> ListViews() => _views.Values.OrderBy(v => v.Name).ToList();

    private TaskBoardMetadata GetOrCreateMetadata(string projectId, int number)
    {
        return _metadata.GetOrAdd((projectId, number),
            _ => new TaskBoardMetadata(projectId, number, 0, 3, null, null, null, null,
                Array.Empty<string>(), new Dictionary<string, string>(), 0));
    }
}
