// KidsPrimitives.cs — (3.3.0)
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Kids;

public enum AgeAppropriateness { Toddler, Preschool, EarlyPrimary, LatePrimary, PreTeen, Teen }

public sealed record KidsContent(string ContentId, string Title, AgeAppropriateness AgeBand, string Kind, IReadOnlyList<string> Tags);
public sealed record DailyTime(string KidName, TimeSpan ScreenLimit, TimeSpan ReadingLimit);
public sealed record TimeLog(string KidName, string Kind, TimeSpan Duration, DateTimeOffset AtUtc);

public interface IKidsBoard
{
    void AddContent(KidsContent c);
    IReadOnlyList<KidsContent> ContentFor(AgeAppropriateness band);
    void SetLimits(DailyTime d);
    DailyTime? LimitsFor(string kidName);
    void RecordTime(TimeLog t);
    TimeSpan UsedToday(string kidName, string kind, DateTimeOffset now);
    bool OverLimit(string kidName, string kind, DateTimeOffset now);
}

public sealed class InMemoryKidsBoard : IKidsBoard
{
    private readonly ConcurrentDictionary<string, KidsContent> _content = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DailyTime> _limits = new(StringComparer.Ordinal);
    private readonly List<TimeLog> _logs = new();
    private readonly object _lock = new();

    public void AddContent(KidsContent c) { ArgumentNullException.ThrowIfNull(c); _content[c.ContentId] = c; }
    public IReadOnlyList<KidsContent> ContentFor(AgeAppropriateness band)
        => _content.Values.Where(c => c.AgeBand == band).OrderBy(c => c.Title).ToArray();
    public void SetLimits(DailyTime d) { ArgumentNullException.ThrowIfNull(d); _limits[d.KidName] = d; }
    public DailyTime? LimitsFor(string kidName) => _limits.GetValueOrDefault(kidName);
    public void RecordTime(TimeLog t) { ArgumentNullException.ThrowIfNull(t); lock (_lock) _logs.Add(t); }
    public TimeSpan UsedToday(string kidName, string kind, DateTimeOffset now)
    {
        lock (_lock)
        {
            var ms = _logs.Where(l => l.KidName == kidName && l.Kind == kind && l.AtUtc.Date == now.Date).Sum(l => l.Duration.TotalMilliseconds);
            return TimeSpan.FromMilliseconds(ms);
        }
    }
    public bool OverLimit(string kidName, string kind, DateTimeOffset now)
    {
        if (!_limits.TryGetValue(kidName, out var limits)) return false;
        var used = UsedToday(kidName, kind, now);
        var cap = kind.Equals("screen", StringComparison.OrdinalIgnoreCase) ? limits.ScreenLimit
                : kind.Equals("reading", StringComparison.OrdinalIgnoreCase) ? limits.ReadingLimit
                : TimeSpan.MaxValue;
        return used > cap;
    }
}
