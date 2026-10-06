// GamingPrimitives.cs — (3.3.0)
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Gaming;

public sealed record GameTitle(string TitleId, string Name, string Genre, string Platform);
public sealed record PlaySession(string SessionId, string UserId, string TitleId, TimeSpan Duration, DateTimeOffset AtUtc);
public sealed record AchievementUnlock(string UnlockId, string UserId, string TitleId, string Achievement, DateTimeOffset AtUtc);

public interface IGamingBoard
{
    void AddTitle(GameTitle t);
    GameTitle? GetTitle(string id);
    IReadOnlyList<GameTitle> TitlesByGenre(string genre);
    void RecordSession(PlaySession s);
    TimeSpan TotalPlayTime(string userId, string titleId);
    void Unlock(AchievementUnlock u);
    IReadOnlyList<AchievementUnlock> AchievementsFor(string userId);
    IReadOnlyList<GameTitle> MostPlayed(string userId, int topK = 5);
}

public sealed class InMemoryGamingBoard : IGamingBoard
{
    private readonly ConcurrentDictionary<string, GameTitle> _titles = new(StringComparer.Ordinal);
    private readonly List<PlaySession> _sessions = new();
    private readonly List<AchievementUnlock> _unlocks = new();
    private readonly object _lock = new();

    public void AddTitle(GameTitle t) { ArgumentNullException.ThrowIfNull(t); _titles[t.TitleId] = t; }
    public GameTitle? GetTitle(string id) => _titles.GetValueOrDefault(id);
    public IReadOnlyList<GameTitle> TitlesByGenre(string genre)
        => _titles.Values.Where(t => string.Equals(t.Genre, genre, StringComparison.OrdinalIgnoreCase)).ToArray();
    public void RecordSession(PlaySession s) { ArgumentNullException.ThrowIfNull(s); lock (_lock) _sessions.Add(s); }
    public TimeSpan TotalPlayTime(string userId, string titleId)
    {
        lock (_lock)
        {
            var ms = _sessions.Where(s => s.UserId == userId && s.TitleId == titleId).Sum(s => s.Duration.TotalMilliseconds);
            return TimeSpan.FromMilliseconds(ms);
        }
    }
    public void Unlock(AchievementUnlock u) { ArgumentNullException.ThrowIfNull(u); lock (_lock) _unlocks.Add(u); }
    public IReadOnlyList<AchievementUnlock> AchievementsFor(string userId)
    { lock (_lock) return _unlocks.Where(u => u.UserId == userId).OrderByDescending(u => u.AtUtc).ToArray(); }
    public IReadOnlyList<GameTitle> MostPlayed(string userId, int topK = 5)
    {
        if (topK <= 0) throw new ArgumentOutOfRangeException(nameof(topK));
        lock (_lock)
        {
            return _sessions.Where(s => s.UserId == userId)
                .GroupBy(s => s.TitleId)
                .OrderByDescending(g => g.Sum(s => s.Duration.TotalMilliseconds))
                .Take(topK)
                .Select(g => _titles.TryGetValue(g.Key, out var t) ? t : null)
                .Where(t => t is not null)
                .Cast<GameTitle>()
                .ToArray();
        }
    }
}
