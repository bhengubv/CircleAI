// FaithPrimitives.cs — (3.3.0)
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Faith;

public sealed record FaithService(string ServiceId, string CommunityName, string Title, DateTimeOffset StartUtc, string Location);
public sealed record PrayerRequest(string RequestId, string Author, string Body, DateTimeOffset SubmittedUtc, bool IsAnonymous);
public sealed record ScriptureReference(string ReferenceId, string Tradition, string Book, int Chapter, int Verse, string Text);

public interface IFaithBoard
{
    void Schedule(FaithService s);
    IReadOnlyList<FaithService> ServicesBetween(DateTimeOffset start, DateTimeOffset end);
    void SubmitPrayer(PrayerRequest r);
    IReadOnlyList<PrayerRequest> RecentPrayers(int limit = 20);
    void AddScripture(ScriptureReference r);
    ScriptureReference? Lookup(string tradition, string book, int chapter, int verse);
    IReadOnlyList<ScriptureReference> ByTradition(string tradition);
    int ServiceCount { get; }
    bool RemoveService(string serviceId);
    IReadOnlyList<FaithService> ServicesAt(string location);
    IReadOnlyList<PrayerRequest> PrayersByAuthor(string author);
    int AnonymousPrayerCount();
    IReadOnlyList<ScriptureReference> ChapterVerses(string tradition, string book, int chapter);
}

public sealed class InMemoryFaithBoard : IFaithBoard
{
    private readonly ConcurrentDictionary<string, FaithService> _services = new(StringComparer.Ordinal);
    private readonly List<PrayerRequest> _prayers = new();
    private readonly ConcurrentDictionary<string, ScriptureReference> _scripture = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public void Schedule(FaithService s) { ArgumentNullException.ThrowIfNull(s); _services[s.ServiceId] = s; }
    public IReadOnlyList<FaithService> ServicesBetween(DateTimeOffset start, DateTimeOffset end)
        => _services.Values.Where(s => s.StartUtc >= start && s.StartUtc <= end).OrderBy(s => s.StartUtc).ToArray();
    public void SubmitPrayer(PrayerRequest r) { ArgumentNullException.ThrowIfNull(r); lock (_lock) _prayers.Add(r); }
    public IReadOnlyList<PrayerRequest> RecentPrayers(int limit = 20)
    { lock (_lock) return _prayers.OrderByDescending(p => p.SubmittedUtc).Take(limit).ToArray(); }
    public void AddScripture(ScriptureReference r) { ArgumentNullException.ThrowIfNull(r); _scripture[r.ReferenceId] = r; }
    public ScriptureReference? Lookup(string tradition, string book, int chapter, int verse)
        => _scripture.Values.FirstOrDefault(r => r.Tradition == tradition && r.Book == book && r.Chapter == chapter && r.Verse == verse);
    public IReadOnlyList<ScriptureReference> ByTradition(string tradition)
        => _scripture.Values.Where(r => string.Equals(r.Tradition, tradition, StringComparison.OrdinalIgnoreCase)).ToArray();

    public int ServiceCount => _services.Count;

    public bool RemoveService(string serviceId) => _services.TryRemove(serviceId, out _);

    public IReadOnlyList<FaithService> ServicesAt(string location)
        => _services.Values.Where(s => string.Equals(s.Location, location, StringComparison.OrdinalIgnoreCase))
                           .OrderBy(s => s.StartUtc).ToArray();

    public IReadOnlyList<PrayerRequest> PrayersByAuthor(string author)
    {
        lock (_lock) return _prayers.Where(p => !p.IsAnonymous && string.Equals(p.Author, author, StringComparison.OrdinalIgnoreCase))
                                    .OrderByDescending(p => p.SubmittedUtc).ToArray();
    }

    public int AnonymousPrayerCount()
    {
        lock (_lock) return _prayers.Count(p => p.IsAnonymous);
    }

    public IReadOnlyList<ScriptureReference> ChapterVerses(string tradition, string book, int chapter)
        => _scripture.Values.Where(r => string.Equals(r.Tradition, tradition, StringComparison.OrdinalIgnoreCase)
                                     && string.Equals(r.Book, book, StringComparison.OrdinalIgnoreCase)
                                     && r.Chapter == chapter)
                            .OrderBy(r => r.Verse).ToArray();
}
