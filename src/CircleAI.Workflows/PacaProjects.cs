// PacaProjects.cs
//
// (3.3.0) Project + task primitives ported from paca. Auto-generates
// task IDs as <PROJECT_PREFIX>-N. Soft deletes via DeletedAtUtc.
// Row-level project scoping via every query taking a projectId.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace CircleAI.Workflows;

/// <summary>(3.3.0) A workspace that contains tasks.</summary>
/// <param name="Id">Stable project id.</param>
/// <param name="Name">Display name.</param>
/// <param name="Prefix">Task-id prefix (e.g. "PACA").</param>
/// <param name="SettingsJson">Free-form JSON configuration bag.</param>
/// <param name="CreatedAtUtc">Creation timestamp.</param>
/// <param name="DeletedAtUtc">Soft-delete timestamp; null = live.</param>
public sealed record PacaProject(
    string         Id,
    string         Name,
    string         Prefix,
    string         SettingsJson,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? DeletedAtUtc);

/// <summary>(3.3.0) A unit of work inside a project.</summary>
/// <param name="ProjectId">Owning project.</param>
/// <param name="Number">Sequential id within the project (PACA-1, PACA-2, …).</param>
/// <param name="Title">Short title.</param>
/// <param name="DescriptionJson">Rich-text JSON body (BlockNote shape).</param>
/// <param name="Status">Current status name.</param>
/// <param name="CreatedAtUtc">Creation timestamp.</param>
/// <param name="DeletedAtUtc">Soft-delete timestamp; null = live.</param>
public sealed record PacaTask(
    string         ProjectId,
    int            Number,
    string         Title,
    string         DescriptionJson,
    string         Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? DeletedAtUtc)
{
    public string Reference(string prefix) => $"{prefix}-{Number}";
}

/// <summary>(3.3.0) In-memory project + task store. Replace for production storage.</summary>
public sealed class InMemoryPacaStore
{
    private readonly ConcurrentDictionary<string, PacaProject> _projects = new();
    private readonly ConcurrentDictionary<string, List<PacaTask>> _tasksByProject = new();
    private readonly ConcurrentDictionary<string, int> _nextNumber = new();
    private readonly Func<DateTimeOffset> _clock;

    public InMemoryPacaStore(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>(3.3.0) Create a new project. Throws if the id already exists.</summary>
    public PacaProject CreateProject(string id, string name, string prefix, string? settingsJson = null)
    {
        if (string.IsNullOrWhiteSpace(id))     throw new ArgumentException("id required",     nameof(id));
        if (string.IsNullOrWhiteSpace(name))   throw new ArgumentException("name required",   nameof(name));
        if (string.IsNullOrWhiteSpace(prefix)) throw new ArgumentException("prefix required", nameof(prefix));

        var project = new PacaProject(
            Id:           id,
            Name:         name,
            Prefix:       prefix,
            SettingsJson: settingsJson ?? "{}",
            CreatedAtUtc: _clock(),
            DeletedAtUtc: null);

        if (!_projects.TryAdd(id, project))
        {
            throw new InvalidOperationException($"Project '{id}' already exists.");
        }
        _tasksByProject[id] = new List<PacaTask>();
        _nextNumber[id]     = 1;
        return project;
    }

    /// <summary>(3.3.0) Get a live project by id (excludes soft-deleted).</summary>
    public PacaProject? GetProject(string id)
        => _projects.TryGetValue(id, out var p) && p.DeletedAtUtc is null ? p : null;

    /// <summary>(3.3.0) Soft-delete a project. Idempotent.</summary>
    public void DeleteProject(string id)
    {
        if (!_projects.TryGetValue(id, out var existing) || existing.DeletedAtUtc is not null) return;
        _projects[id] = existing with { DeletedAtUtc = _clock() };
    }

    /// <summary>(3.3.0) Update the JSON settings bag on a project.</summary>
    public PacaProject UpdateProjectSettings(string projectId, string newSettingsJson)
    {
        var existing = GetProject(projectId) ?? throw new InvalidOperationException($"Project '{projectId}' not found.");
        var updated  = existing with { SettingsJson = newSettingsJson ?? "{}" };
        _projects[projectId] = updated;
        return updated;
    }

    /// <summary>(3.3.0) Add a task to a project. Auto-numbers it.</summary>
    public PacaTask AddTask(string projectId, string title, string? descriptionJson = null, string status = "todo")
    {
        var project = GetProject(projectId) ?? throw new InvalidOperationException($"Project '{projectId}' not found.");
        int number;
        lock (_nextNumber)
        {
            number = _nextNumber[projectId];
            _nextNumber[projectId] = number + 1;
        }
        var task    = new PacaTask(
            ProjectId:       projectId,
            Number:          number,
            Title:           title ?? "",
            DescriptionJson: descriptionJson ?? "{}",
            Status:          status ?? "todo",
            CreatedAtUtc:    _clock(),
            DeletedAtUtc:    null);

        var list = _tasksByProject[projectId];
        lock (list) list.Add(task);
        return task;
    }

    /// <summary>(3.3.0) List live tasks for a project, ordered by number ascending.</summary>
    public IReadOnlyList<PacaTask> ListTasks(string projectId)
    {
        if (!_tasksByProject.TryGetValue(projectId, out var list)) return Array.Empty<PacaTask>();
        lock (list)
        {
            return list.Where(t => t.DeletedAtUtc is null).OrderBy(t => t.Number).ToArray();
        }
    }

    /// <summary>(3.3.0) Find one task by reference like "PACA-3".</summary>
    public PacaTask? GetTaskByReference(string projectId, string reference)
    {
        var project = GetProject(projectId);
        if (project is null) return null;
        var expectedPrefix = project.Prefix + "-";
        if (!reference.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase)) return null;
        if (!int.TryParse(reference[expectedPrefix.Length..], out var n)) return null;
        return _tasksByProject.TryGetValue(projectId, out var list)
            ? list.FirstOrDefault(t => t.Number == n && t.DeletedAtUtc is null)
            : null;
    }

    /// <summary>(3.3.0) Update a task in place. Caller mutates via record-with.</summary>
    public void UpdateTask(PacaTask updated)
    {
        ArgumentNullException.ThrowIfNull(updated);
        if (!_tasksByProject.TryGetValue(updated.ProjectId, out var list)) return;
        lock (list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Number == updated.Number)
                {
                    list[i] = updated;
                    return;
                }
            }
        }
    }

    /// <summary>(3.3.0) Soft-delete a task.</summary>
    public void DeleteTask(string projectId, int number)
    {
        if (!_tasksByProject.TryGetValue(projectId, out var list)) return;
        lock (list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Number == number)
                {
                    list[i] = list[i] with { DeletedAtUtc = _clock() };
                    return;
                }
            }
        }
    }

}
