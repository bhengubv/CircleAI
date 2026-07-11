// social/index.ts
// Full-parity port of CircleAI.Social (C#). C# is the exact spec.
//
// Domain types + in-memory store for the Social vertical: posts, reactions,
// follows, and a follow-graph feed. Plus the static SocialDomainContext.
//
// NOTE: The C# SocialCompanionAdapter (an ICompanionSession LLM-prompt wrapper)
// is intentionally NOT ported — consistent with the sibling domain-board ports.
//
// Type mappings (C# → TS):
//   record                           → readonly interface (+ positional factory)
//   IReadOnlyList<string> Tags       → readonly string[]
//   int limit (return count)         → number
//   DateTimeOffset AtUtc             → Date
//   ConcurrentDictionary (Ordinal)   → Map<string,T>
//   List<Reaction> / List<Follow>    → arrays (single-threaded; C# lock is a no-op)
//
// SEMANTICS PARITY:
//   React         — appends.
//   ReactionCount — count for (postId, Kind ordinal case-insensitive).
//   Follow        — throws when FollowerId == FolloweeId; appends (duplicates OK).
//   Unfollow      — removes all matching (FollowerId, FolloweeId) edges.
//   FeedFor       — posts by anyone the user follows, AtUtc descending, take limit.
//                   limit<=0 throws.
//   Followers     — follower ids for the given followee (edge order).

/** A social post. Mirrors C# `SocialPost` record. */
export interface SocialPost {
  readonly postId: string;
  readonly authorId: string;
  readonly body: string;
  /** UTC instant of the post (C# `DateTimeOffset AtUtc`). */
  readonly atUtc: Date;
  readonly tags: readonly string[];
}

/** Constructs a {@link SocialPost}. */
export function socialPost(
  postId: string,
  authorId: string,
  body: string,
  atUtc: Date,
  tags: readonly string[],
): SocialPost {
  return { postId, authorId, body, atUtc, tags };
}

/** A reaction to a post. Mirrors C# `Reaction` record. */
export interface Reaction {
  readonly postId: string;
  readonly userId: string;
  readonly kind: string;
  /** UTC instant of the reaction (C# `DateTimeOffset AtUtc`). */
  readonly atUtc: Date;
}

/** Constructs a {@link Reaction}. */
export function reaction(postId: string, userId: string, kind: string, atUtc: Date): Reaction {
  return { postId, userId, kind, atUtc };
}

/** A follow edge. Mirrors C# `Follow` record. */
export interface Follow {
  readonly followerId: string;
  readonly followeeId: string;
  /** UTC instant of the follow (C# `DateTimeOffset AtUtc`). */
  readonly atUtc: Date;
}

/** Constructs a {@link Follow}. */
export function follow(followerId: string, followeeId: string, atUtc: Date): Follow {
  return { followerId, followeeId, atUtc };
}

/** The social board contract. Mirrors C# `ISocialBoard`. */
export interface ISocialBoard {
  post(p: SocialPost): void;
  getPost(id: string): SocialPost | undefined;
  react(r: Reaction): void;
  reactionCount(postId: string, kind: string): number;
  follow(f: Follow): void;
  unfollow(followerId: string, followeeId: string): void;
  feedFor(userId: string, limit?: number): readonly SocialPost[];
  followers(userId: string): readonly string[];
}

/** Deterministic in-memory {@link ISocialBoard}. */
export class InMemorySocialBoard implements ISocialBoard {
  private readonly posts = new Map<string, SocialPost>();
  private readonly reacts: Reaction[] = [];
  private readonly follows: Follow[] = [];

  post(p: SocialPost): void {
    if (p == null) throw new Error("p required");
    this.posts.set(p.postId, p);
  }

  getPost(id: string): SocialPost | undefined {
    return this.posts.get(id);
  }

  react(r: Reaction): void {
    if (r == null) throw new Error("r required");
    this.reacts.push(r);
  }

  reactionCount(postId: string, kind: string): number {
    const k = kind.toLowerCase();
    return this.reacts.filter((r) => r.postId === postId && r.kind.toLowerCase() === k).length;
  }

  follow(f: Follow): void {
    if (f == null) throw new Error("f required");
    if (f.followerId === f.followeeId) throw new Error("Cannot follow yourself.");
    this.follows.push(f);
  }

  unfollow(followerId: string, followeeId: string): void {
    for (let i = this.follows.length - 1; i >= 0; i--) {
      const f = this.follows[i];
      if (f.followerId === followerId && f.followeeId === followeeId) this.follows.splice(i, 1);
    }
  }

  feedFor(userId: string, limit = 20): readonly SocialPost[] {
    if (limit <= 0) throw new Error("limit");
    const following = new Set<string>();
    for (const f of this.follows) {
      if (f.followerId === userId) following.add(f.followeeId);
    }
    return [...this.posts.values()]
      .filter((p) => following.has(p.authorId))
      .sort((a, b) => b.atUtc.getTime() - a.atUtc.getTime())
      .slice(0, limit);
  }

  followers(userId: string): readonly string[] {
    return this.follows.filter((f) => f.followeeId === userId).map((f) => f.followerId);
  }
}

/**
 * Static domain context for the Social vertical. Mirrors C#
 * `SocialDomainContext`.
 */
export const SocialDomainContext = {
  systemPromptSnippet:
    "[DOMAIN: Social] Expert social media and community management assistant. Help with platform-specific content creation (LinkedIn, Instagram, TikTok, X, Facebook), engagement strategy, hashtag research, influencer brief writing, community moderation guidelines, and social analytics. Apply scroll-stopping creative principles. Compliance: POPIA, ASA Advertising Code, platform community standards.",
  complianceFlags: ["POPIA", "ASA_Advertising_Code", "Platform_Community_Standards"] as readonly string[],
  suggestedTools: ["social_media_api", "analytics", "content_planner", "image_tools"] as readonly string[],
} as const;
