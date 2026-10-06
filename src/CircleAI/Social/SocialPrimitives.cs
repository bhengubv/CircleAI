// SocialPrimitives.cs
//
// (3.3.0) Real domain types + in-memory store for the Social vertical:
// posts, reactions, follows, simple feed.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Social;

public sealed record SocialPost(string PostId, string AuthorId, string Body, DateTimeOffset AtUtc, IReadOnlyList<string> Tags);
public sealed record Reaction(string PostId, string UserId, string Kind, DateTimeOffset AtUtc);
public sealed record Follow(string FollowerId, string FolloweeId, DateTimeOffset AtUtc);

public interface ISocialBoard
{
    void Post(SocialPost p);
    SocialPost? GetPost(string id);
    void React(Reaction r);
    int ReactionCount(string postId, string kind);
    void Follow(Follow f);
    void Unfollow(string followerId, string followeeId);
    IReadOnlyList<SocialPost> FeedFor(string userId, int limit = 20);
    IReadOnlyList<string> Followers(string userId);
}

public sealed class InMemorySocialBoard : ISocialBoard
{
    private readonly ConcurrentDictionary<string, SocialPost> _posts = new(StringComparer.Ordinal);
    private readonly List<Reaction> _reacts = new();
    private readonly List<Follow> _follows = new();
    private readonly object _lock = new();

    public void Post(SocialPost p) { ArgumentNullException.ThrowIfNull(p); _posts[p.PostId] = p; }
    public SocialPost? GetPost(string id) => _posts.GetValueOrDefault(id);

    public void React(Reaction r) { ArgumentNullException.ThrowIfNull(r); lock (_lock) _reacts.Add(r); }

    public int ReactionCount(string postId, string kind)
    {
        lock (_lock) return _reacts.Count(r => r.PostId == postId && string.Equals(r.Kind, kind, StringComparison.OrdinalIgnoreCase));
    }

    public void Follow(Follow f)
    {
        ArgumentNullException.ThrowIfNull(f);
        if (f.FollowerId == f.FolloweeId) throw new InvalidOperationException("Cannot follow yourself.");
        lock (_lock) _follows.Add(f);
    }

    public void Unfollow(string followerId, string followeeId)
    {
        lock (_lock) _follows.RemoveAll(f => f.FollowerId == followerId && f.FolloweeId == followeeId);
    }

    public IReadOnlyList<SocialPost> FeedFor(string userId, int limit = 20)
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        HashSet<string> following;
        lock (_lock) following = _follows.Where(f => f.FollowerId == userId).Select(f => f.FolloweeId).ToHashSet();
        return _posts.Values.Where(p => following.Contains(p.AuthorId))
            .OrderByDescending(p => p.AtUtc).Take(limit).ToArray();
    }

    public IReadOnlyList<string> Followers(string userId)
    {
        lock (_lock) return _follows.Where(f => f.FolloweeId == userId).Select(f => f.FollowerId).ToArray();
    }
}
