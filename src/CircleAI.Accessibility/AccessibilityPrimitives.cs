// AccessibilityPrimitives.cs — (3.3.0)
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Accessibility;

public enum AccessibilityNeed { Visual, Hearing, Motor, Cognitive, Speech }

public sealed record UserAccessibilityProfile(string UserId, IReadOnlyList<AccessibilityNeed> Needs, double TextScale, bool HighContrast, bool ReducedMotion, bool ScreenReader);
public sealed record AdaptationHint(string Kind, string Value);

public interface IAccessibilityBoard
{
    void SetProfile(UserAccessibilityProfile p);
    UserAccessibilityProfile? GetProfile(string userId);
    IReadOnlyList<AdaptationHint> HintsFor(string userId);
    int Count { get; }
    bool Remove(string userId);
    IReadOnlyList<UserAccessibilityProfile> WithNeed(AccessibilityNeed need);
    IReadOnlyList<UserAccessibilityProfile> ScreenReaderUsers();
    double AverageTextScale();
    bool NeedsLargeText(string userId, double threshold = 1.3);
}

public sealed class InMemoryAccessibilityBoard : IAccessibilityBoard
{
    private readonly ConcurrentDictionary<string, UserAccessibilityProfile> _profiles = new(StringComparer.Ordinal);

    public void SetProfile(UserAccessibilityProfile p) { ArgumentNullException.ThrowIfNull(p); _profiles[p.UserId] = p; }
    public UserAccessibilityProfile? GetProfile(string userId) => _profiles.GetValueOrDefault(userId);

    public IReadOnlyList<AdaptationHint> HintsFor(string userId)
    {
        if (!_profiles.TryGetValue(userId, out var p)) return Array.Empty<AdaptationHint>();
        var hints = new List<AdaptationHint>();
        if (p.HighContrast)   hints.Add(new AdaptationHint("contrast", "high"));
        if (p.ReducedMotion)  hints.Add(new AdaptationHint("motion", "reduced"));
        if (p.ScreenReader)   hints.Add(new AdaptationHint("aria", "verbose"));
        if (p.TextScale > 1)  hints.Add(new AdaptationHint("text-scale", p.TextScale.ToString("F2")));
        foreach (var n in p.Needs) hints.Add(new AdaptationHint("need", n.ToString()));
        return hints;
    }

    public int Count => _profiles.Count;

    public bool Remove(string userId) => _profiles.TryRemove(userId, out _);

    public IReadOnlyList<UserAccessibilityProfile> WithNeed(AccessibilityNeed need)
        => _profiles.Values.Where(p => p.Needs.Contains(need))
                           .OrderBy(p => p.UserId, StringComparer.OrdinalIgnoreCase).ToArray();

    public IReadOnlyList<UserAccessibilityProfile> ScreenReaderUsers()
        => _profiles.Values.Where(p => p.ScreenReader)
                           .OrderBy(p => p.UserId, StringComparer.OrdinalIgnoreCase).ToArray();

    public double AverageTextScale()
        => _profiles.Values.Select(p => p.TextScale).DefaultIfEmpty(1.0).Average();

    public bool NeedsLargeText(string userId, double threshold = 1.3)
        => _profiles.TryGetValue(userId, out var p) && p.TextScale >= threshold;
}
