// CreativePrimitives.cs — (3.3.0)
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Creative;

public sealed record CreativeWork(string WorkId, string Title, string Medium, string Author, DateTimeOffset CreatedUtc, IReadOnlyList<string> Tags);
public sealed record Inspiration(string InspirationId, string PromptText, string SourceUrl, DateTimeOffset SeenUtc);
public sealed record Critique(string CritiqueId, string WorkId, string Reviewer, string Body, int Score);

public interface ICreativeBoard
{
    void AddWork(CreativeWork w);
    CreativeWork? GetWork(string id);
    IReadOnlyList<CreativeWork> WorksByTag(string tag);
    void RecordInspiration(Inspiration i);
    IReadOnlyList<Inspiration> RecentInspiration(int limit = 20);
    void AddCritique(Critique c);
    double AvgScore(string workId);
    int WorkCount { get; }
    bool RemoveWork(string workId);
    IReadOnlyList<CreativeWork> WorksByAuthor(string author);
    IReadOnlyList<CreativeWork> WorksByMedium(string medium);
    CreativeWork? TopRatedWork();
    IReadOnlyList<string> AllTags();
}

public sealed class InMemoryCreativeBoard : ICreativeBoard
{
    private readonly ConcurrentDictionary<string, CreativeWork> _works = new(StringComparer.Ordinal);
    private readonly List<Inspiration> _inspiration = new();
    private readonly List<Critique> _critiques = new();
    private readonly object _lock = new();

    public void AddWork(CreativeWork w) { ArgumentNullException.ThrowIfNull(w); _works[w.WorkId] = w; }
    public CreativeWork? GetWork(string id) => _works.GetValueOrDefault(id);
    public IReadOnlyList<CreativeWork> WorksByTag(string tag)
        => _works.Values.Where(w => w.Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))).ToArray();
    public void RecordInspiration(Inspiration i) { ArgumentNullException.ThrowIfNull(i); lock (_lock) _inspiration.Add(i); }
    public IReadOnlyList<Inspiration> RecentInspiration(int limit = 20)
    { lock (_lock) return _inspiration.OrderByDescending(i => i.SeenUtc).Take(limit).ToArray(); }
    public void AddCritique(Critique c) { ArgumentNullException.ThrowIfNull(c); lock (_lock) _critiques.Add(c); }
    public double AvgScore(string workId)
    { lock (_lock) return _critiques.Where(c => c.WorkId == workId).Select(c => (double)c.Score).DefaultIfEmpty(0).Average(); }

    public int WorkCount => _works.Count;

    public bool RemoveWork(string workId)
    {
        var removed = _works.TryRemove(workId, out _);
        if (removed) lock (_lock) _critiques.RemoveAll(c => c.WorkId == workId);
        return removed;
    }

    public IReadOnlyList<CreativeWork> WorksByAuthor(string author)
        => _works.Values.Where(w => string.Equals(w.Author, author, StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(w => w.CreatedUtc).ToArray();

    public IReadOnlyList<CreativeWork> WorksByMedium(string medium)
        => _works.Values.Where(w => string.Equals(w.Medium, medium, StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(w => w.CreatedUtc).ToArray();

    public CreativeWork? TopRatedWork()
    {
        lock (_lock)
        {
            return _critiques.GroupBy(c => c.WorkId, StringComparer.Ordinal)
                             .Select(g => (WorkId: g.Key, Avg: g.Average(c => c.Score)))
                             .OrderByDescending(t => t.Avg)
                             .Select(t => _works.GetValueOrDefault(t.WorkId))
                             .FirstOrDefault(w => w is not null);
        }
    }

    public IReadOnlyList<string> AllTags()
        => _works.Values.SelectMany(w => w.Tags)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToArray();
}
