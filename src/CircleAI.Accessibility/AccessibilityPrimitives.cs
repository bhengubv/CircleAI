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
}
