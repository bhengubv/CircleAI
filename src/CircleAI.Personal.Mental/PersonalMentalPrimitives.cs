// PersonalMentalPrimitives.cs
//
// (3.3.0) Real domain types + in-memory store for the mental-health
// vertical: mood logs, journal entries, coping-strategy library,
// 7-day trend. Privacy: per-user instance only.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Personal.Mental;

public enum Mood { VeryLow, Low, Neutral, Good, Great }

public sealed record MoodLog(Mood Mood, DateTimeOffset AtUtc, string? Note);
public sealed record JournalEntry(string EntryId, string Title, string Body, DateTimeOffset AtUtc);
public sealed record CopingStrategy(string StrategyId, string Title, string Description, IReadOnlyList<string> Tags);

public interface IMentalHealthBoard
{
    void LogMood(MoodLog m);
    IReadOnlyList<MoodLog> Last7Days();
    void AddEntry(JournalEntry e);
    IReadOnlyList<JournalEntry> Entries { get; }
    void RegisterStrategy(CopingStrategy s);
    IReadOnlyList<CopingStrategy> StrategiesByTag(string tag);
    double AvgMood7Day();
}

public sealed class InMemoryMentalHealthBoard : IMentalHealthBoard
{
    private readonly List<MoodLog> _moods = new();
    private readonly ConcurrentDictionary<string, JournalEntry> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CopingStrategy> _strats = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public void LogMood(MoodLog m) { ArgumentNullException.ThrowIfNull(m); lock (_lock) _moods.Add(m); }

    public IReadOnlyList<MoodLog> Last7Days()
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-7);
        lock (_lock) return _moods.Where(m => m.AtUtc >= cutoff).OrderBy(m => m.AtUtc).ToArray();
    }

    public void AddEntry(JournalEntry e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (string.IsNullOrWhiteSpace(e.EntryId)) throw new ArgumentException("EntryId required");
        _entries[e.EntryId] = e;
    }

    public IReadOnlyList<JournalEntry> Entries => _entries.Values.OrderByDescending(e => e.AtUtc).ToArray();

    public void RegisterStrategy(CopingStrategy s) { ArgumentNullException.ThrowIfNull(s); _strats[s.StrategyId] = s; }

    public IReadOnlyList<CopingStrategy> StrategiesByTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) throw new ArgumentException("tag required", nameof(tag));
        return _strats.Values.Where(s => s.Tags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))).ToArray();
    }

    public double AvgMood7Day()
    {
        var items = Last7Days();
        if (items.Count == 0) return double.NaN;
        return items.Select(m => (int)m.Mood).Average();
    }
}
