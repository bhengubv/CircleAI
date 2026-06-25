// CivicPrimitives.cs — (3.3.0)
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Civic;

public sealed record CivicIssue(string IssueId, string Category, string Description, double Lat, double Lon, DateTimeOffset ReportedUtc, string Status);
public sealed record Representative(string RepId, string Name, string Office, string ContactEmail, string? District);
public sealed record CivicEvent(string EventId, string Title, DateTimeOffset AtUtc, string Location, string Audience);

public interface ICivicBoard
{
    void Report(CivicIssue i);
    void Resolve(string issueId, string status);
    IReadOnlyList<CivicIssue> OpenIssues();
    void AddRep(Representative r);
    IReadOnlyList<Representative> RepsForDistrict(string district);
    void Schedule(CivicEvent e);
    IReadOnlyList<CivicEvent> UpcomingEvents();
}

public sealed class InMemoryCivicBoard : ICivicBoard
{
    private readonly ConcurrentDictionary<string, CivicIssue> _issues = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Representative> _reps = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CivicEvent> _events = new(StringComparer.Ordinal);

    public void Report(CivicIssue i) { ArgumentNullException.ThrowIfNull(i); _issues[i.IssueId] = i; }
    public void Resolve(string issueId, string status)
    {
        if (!_issues.TryGetValue(issueId, out var i)) throw new InvalidOperationException($"Unknown issue {issueId}");
        _issues[issueId] = i with { Status = status };
    }
    public IReadOnlyList<CivicIssue> OpenIssues() => _issues.Values.Where(i => !string.Equals(i.Status, "Resolved", StringComparison.OrdinalIgnoreCase)).ToArray();
    public void AddRep(Representative r) { ArgumentNullException.ThrowIfNull(r); _reps[r.RepId] = r; }
    public IReadOnlyList<Representative> RepsForDistrict(string district)
        => _reps.Values.Where(r => string.Equals(r.District, district, StringComparison.OrdinalIgnoreCase)).ToArray();
    public void Schedule(CivicEvent e) { ArgumentNullException.ThrowIfNull(e); _events[e.EventId] = e; }
    public IReadOnlyList<CivicEvent> UpcomingEvents() => _events.Values.Where(e => e.AtUtc >= DateTimeOffset.UtcNow).OrderBy(e => e.AtUtc).ToArray();
}
