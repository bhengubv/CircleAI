// CommunityPrimitives.cs — (3.3.0)
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Community;

public sealed record CommunityGroup(string GroupId, string Name, string Purpose, IReadOnlyList<string> MemberIds);
public sealed record Announcement(string AnnouncementId, string GroupId, string Title, string Body, DateTimeOffset AtUtc);
public sealed record VolunteerOpportunity(string OppId, string GroupId, string Description, int VolunteersNeeded, DateTimeOffset WhenUtc);

public interface ICommunityBoard
{
    void Create(CommunityGroup g);
    CommunityGroup? GetGroup(string id);
    IReadOnlyList<CommunityGroup> GroupsForMember(string memberId);
    void Post(Announcement a);
    IReadOnlyList<Announcement> AnnouncementsFor(string groupId, int limit = 20);
    void List(VolunteerOpportunity o);
    IReadOnlyList<VolunteerOpportunity> Opportunities();
}

public sealed class InMemoryCommunityBoard : ICommunityBoard
{
    private readonly ConcurrentDictionary<string, CommunityGroup> _groups = new(StringComparer.Ordinal);
    private readonly List<Announcement> _annc = new();
    private readonly ConcurrentDictionary<string, VolunteerOpportunity> _opps = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public void Create(CommunityGroup g) { ArgumentNullException.ThrowIfNull(g); _groups[g.GroupId] = g; }
    public CommunityGroup? GetGroup(string id) => _groups.GetValueOrDefault(id);
    public IReadOnlyList<CommunityGroup> GroupsForMember(string memberId)
        => _groups.Values.Where(g => g.MemberIds.Contains(memberId)).ToArray();
    public void Post(Announcement a) { ArgumentNullException.ThrowIfNull(a); lock (_lock) _annc.Add(a); }
    public IReadOnlyList<Announcement> AnnouncementsFor(string groupId, int limit = 20)
    { lock (_lock) return _annc.Where(a => a.GroupId == groupId).OrderByDescending(a => a.AtUtc).Take(limit).ToArray(); }
    public void List(VolunteerOpportunity o) { ArgumentNullException.ThrowIfNull(o); _opps[o.OppId] = o; }
    public IReadOnlyList<VolunteerOpportunity> Opportunities() => _opps.Values.Where(o => o.WhenUtc >= DateTimeOffset.UtcNow).OrderBy(o => o.WhenUtc).ToArray();
}
