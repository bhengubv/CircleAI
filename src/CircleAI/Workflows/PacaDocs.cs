// PacaDocs.cs
//
// (3.3.0) Project-level living documents with folders, version
// snapshots, activity feed, task/epic linkage, and @mentions of
// humans + agents (paca port).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace CircleAI.Workflows;

/// <summary>(3.3.0) A doc node (folder OR document).</summary>
public sealed record DocNode(
    string         Id,
    string         ProjectId,
    string?        ParentId,
    bool           IsFolder,
    string         Title,
    string         ContentJson,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? DeletedAtUtc);

/// <summary>(3.3.0) One immutable snapshot of a doc.</summary>
public sealed record DocVersion(
    string         VersionId,
    string         DocId,
    string         ContentJson,
    DateTimeOffset SavedAtUtc,
    string         AuthorMemberId);

/// <summary>(3.3.0) One document-activity event.</summary>
public sealed record DocActivity(
    string         ActivityId,
    string         DocId,
    string         AuthorMemberId,
    string         Action,           // "created" / "edited" / "ai-edited" / "linked" / "commented"
    string?        Detail,
    DateTimeOffset At);

/// <summary>(3.3.0) Link between a doc section and a task / epic.</summary>
public sealed record DocLink(
    string         LinkId,
    string         DocId,
    string         SectionAnchor,
    string         ProjectId,
    int            TaskNumber);

/// <summary>(3.3.0) In-memory doc service.</summary>
public sealed class PacaDocService
{
    private readonly ConcurrentDictionary<string, DocNode>            _nodes      = new();
    private readonly ConcurrentDictionary<string, List<DocVersion>>   _versions   = new();
    private readonly ConcurrentDictionary<string, List<DocActivity>>  _activity   = new();
    private readonly ConcurrentDictionary<string, List<DocLink>>      _links      = new();
    private readonly Func<DateTimeOffset> _clock;

    private static readonly Regex MentionPattern = new(@"@([a-zA-Z0-9_\-]+)", RegexOptions.Compiled);

    public PacaDocService(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public DocNode CreateFolder(string id, string projectId, string? parentId, string title)
        => Create(id, projectId, parentId, isFolder: true, title, contentJson: "{}", authorMemberId: "system");

    public DocNode CreateDocument(string id, string projectId, string? parentId, string title, string contentJson, string authorMemberId)
        => Create(id, projectId, parentId, isFolder: false, title, contentJson, authorMemberId);

    private DocNode Create(string id, string projectId, string? parentId, bool isFolder, string title, string contentJson, string authorMemberId)
    {
        if (string.IsNullOrWhiteSpace(id))        throw new ArgumentException("id required", nameof(id));
        if (string.IsNullOrWhiteSpace(projectId)) throw new ArgumentException("projectId required", nameof(projectId));
        var node = new DocNode(id, projectId, parentId, isFolder, title ?? "", contentJson ?? "{}", _clock(), null);
        if (!_nodes.TryAdd(id, node)) throw new InvalidOperationException($"Doc '{id}' already exists.");

        if (!isFolder)
        {
            _versions[id] = new List<DocVersion>();
            _activity[id] = new List<DocActivity> { new(Guid.NewGuid().ToString("n"), id, authorMemberId, "created", null, _clock()) };
        }
        return node;
    }

    public DocNode? Get(string id) => _nodes.TryGetValue(id, out var n) && n.DeletedAtUtc is null ? n : null;

    public IReadOnlyList<DocNode> ListChildren(string projectId, string? parentId)
        => _nodes.Values.Where(n => n.ProjectId == projectId && n.ParentId == parentId && n.DeletedAtUtc is null)
            .OrderBy(n => n.Title).ToList();

    /// <summary>(3.3.0) Edit a document: writes a new version + activity entry, returns mentioned handles.</summary>
    public IReadOnlyList<string> Edit(string id, string newContentJson, string authorMemberId, bool isAiEdit = false)
    {
        if (!_nodes.TryGetValue(id, out var node) || node.IsFolder || node.DeletedAtUtc is not null)
        {
            throw new InvalidOperationException($"Doc '{id}' is not editable.");
        }

        var updated = node with { ContentJson = newContentJson ?? "{}" };
        _nodes[id] = updated;

        var version = new DocVersion(Guid.NewGuid().ToString("n"), id, node.ContentJson, _clock(), authorMemberId);
        _versions[id].Add(version);

        _activity[id].Add(new DocActivity(Guid.NewGuid().ToString("n"), id, authorMemberId,
            isAiEdit ? "ai-edited" : "edited", null, _clock()));

        return ExtractMentions(newContentJson ?? "");
    }

    public IReadOnlyList<DocVersion> Versions(string docId)
        => _versions.TryGetValue(docId, out var list) ? list.ToArray() : Array.Empty<DocVersion>();

    /// <summary>(3.3.0) Cheap diff between two versions — returns added + removed text lines.</summary>
    public (IReadOnlyList<string> Added, IReadOnlyList<string> Removed) DiffLines(string before, string after)
    {
        var b = (before ?? "").Split('\n').ToHashSet();
        var a = (after  ?? "").Split('\n').ToHashSet();
        return (a.Except(b).ToList(), b.Except(a).ToList());
    }

    public IReadOnlyList<DocActivity> Activity(string docId)
        => _activity.TryGetValue(docId, out var list) ? list.ToArray() : Array.Empty<DocActivity>();

    public DocLink Link(string docId, string sectionAnchor, string projectId, int taskNumber)
    {
        var link = new DocLink(Guid.NewGuid().ToString("n"), docId, sectionAnchor, projectId, taskNumber);
        var bucket = _links.GetOrAdd(docId, _ => new List<DocLink>());
        lock (bucket) bucket.Add(link);
        _activity[docId].Add(new DocActivity(Guid.NewGuid().ToString("n"), docId, "system", "linked",
            $"{projectId}-{taskNumber}@{sectionAnchor}", _clock()));
        return link;
    }

    public IReadOnlyList<DocLink> Links(string docId)
        => _links.TryGetValue(docId, out var list) ? list.ToArray() : Array.Empty<DocLink>();

    private static IReadOnlyList<string> ExtractMentions(string content)
    {
        var matches = MentionPattern.Matches(content);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in matches) set.Add(m.Groups[1].Value);
        return set.ToList();
    }
}
