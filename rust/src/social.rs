//! social — CircleAI social-board primitives.
//!
//! Full Rust port of `src/CircleAI.Social/SocialPrimitives.cs`:
//!
//! - Records [`SocialPost`] / [`Reaction`] / [`Follow`], the [`ISocialBoard`]
//!   contract, and the deterministic in-memory [`InMemorySocialBoard`] (posts +
//!   reactions + follow graph + simple feed).
//!
//! Sync-only; `DateTimeOffset` → [`chrono::DateTime<Utc>`].

use std::collections::{HashMap, HashSet};
use std::sync::Mutex;

use chrono::{DateTime, Utc};

/// Default `limit` for [`ISocialBoard::feed_for`] (C# `limit = 20`).
pub const DEFAULT_FEED_LIMIT: i32 = 20;

/// (Social) A post.
///
/// Mirrors `sealed record SocialPost(string PostId, string AuthorId,
/// string Body, DateTimeOffset AtUtc, IReadOnlyList<string> Tags)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SocialPost {
    pub post_id: String,
    pub author_id: String,
    pub body: String,
    pub at_utc: DateTime<Utc>,
    pub tags: Vec<String>,
}

impl SocialPost {
    /// Constructs a post, mirroring the positional C# record constructor.
    pub fn new(
        post_id: impl Into<String>,
        author_id: impl Into<String>,
        body: impl Into<String>,
        at_utc: DateTime<Utc>,
        tags: Vec<String>,
    ) -> Self {
        Self {
            post_id: post_id.into(),
            author_id: author_id.into(),
            body: body.into(),
            at_utc,
            tags,
        }
    }
}

/// (Social) A reaction to a post.
///
/// Mirrors `sealed record Reaction(string PostId, string UserId, string Kind,
/// DateTimeOffset AtUtc)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Reaction {
    pub post_id: String,
    pub user_id: String,
    pub kind: String,
    pub at_utc: DateTime<Utc>,
}

impl Reaction {
    /// Constructs a reaction, mirroring the positional C# record constructor.
    pub fn new(
        post_id: impl Into<String>,
        user_id: impl Into<String>,
        kind: impl Into<String>,
        at_utc: DateTime<Utc>,
    ) -> Self {
        Self {
            post_id: post_id.into(),
            user_id: user_id.into(),
            kind: kind.into(),
            at_utc,
        }
    }
}

/// (Social) A follow edge.
///
/// Mirrors `sealed record Follow(string FollowerId, string FolloweeId,
/// DateTimeOffset AtUtc)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Follow {
    pub follower_id: String,
    pub followee_id: String,
    pub at_utc: DateTime<Utc>,
}

impl Follow {
    /// Constructs a follow, mirroring the positional C# record constructor.
    pub fn new(follower_id: impl Into<String>, followee_id: impl Into<String>, at_utc: DateTime<Utc>) -> Self {
        Self {
            follower_id: follower_id.into(),
            followee_id: followee_id.into(),
            at_utc,
        }
    }
}

/// (Social) The social-board contract.
///
/// Mirrors `interface ISocialBoard`.
pub trait ISocialBoard {
    /// Posts (or overwrites) a post.
    fn post(&self, p: SocialPost);
    /// A post by id, if any.
    fn get_post(&self, id: &str) -> Option<SocialPost>;
    /// Records a reaction.
    fn react(&self, r: Reaction);
    /// The number of reactions of `kind` (case-insensitive) on a post.
    fn reaction_count(&self, post_id: &str, kind: &str) -> i32;
    /// Adds a follow edge. Panics on self-follow (mirrors the C#
    /// `InvalidOperationException`).
    fn follow(&self, f: Follow);
    /// Removes every follow edge matching `(follower, followee)`.
    fn unfollow(&self, follower_id: &str, followee_id: &str);
    /// A user's feed: posts by followed authors, newest first (default
    /// [`DEFAULT_FEED_LIMIT`]). Panics when `limit <= 0`.
    fn feed_for(&self, user_id: &str, limit: i32) -> Vec<SocialPost>;
    /// The ids of a user's followers.
    fn followers(&self, user_id: &str) -> Vec<String>;
}

/// (Social) In-memory [`ISocialBoard`].
///
/// Mirrors `sealed class InMemorySocialBoard`.
pub struct InMemorySocialBoard {
    posts: Mutex<HashMap<String, SocialPost>>,
    reacts: Mutex<Vec<Reaction>>,
    follows: Mutex<Vec<Follow>>,
}

impl InMemorySocialBoard {
    /// Creates an empty board.
    pub fn new() -> Self {
        Self {
            posts: Mutex::new(HashMap::new()),
            reacts: Mutex::new(Vec::new()),
            follows: Mutex::new(Vec::new()),
        }
    }
}

impl Default for InMemorySocialBoard {
    fn default() -> Self {
        Self::new()
    }
}

impl ISocialBoard for InMemorySocialBoard {
    fn post(&self, p: SocialPost) {
        self.posts.lock().unwrap().insert(p.post_id.clone(), p);
    }

    fn get_post(&self, id: &str) -> Option<SocialPost> {
        self.posts.lock().unwrap().get(id).cloned()
    }

    fn react(&self, r: Reaction) {
        self.reacts.lock().unwrap().push(r);
    }

    fn reaction_count(&self, post_id: &str, kind: &str) -> i32 {
        self.reacts
            .lock()
            .unwrap()
            .iter()
            .filter(|r| r.post_id == post_id && r.kind.eq_ignore_ascii_case(kind))
            .count() as i32
    }

    fn follow(&self, f: Follow) {
        if f.follower_id == f.followee_id {
            panic!("Cannot follow yourself.");
        }
        self.follows.lock().unwrap().push(f);
    }

    fn unfollow(&self, follower_id: &str, followee_id: &str) {
        self.follows
            .lock()
            .unwrap()
            .retain(|f| !(f.follower_id == follower_id && f.followee_id == followee_id));
    }

    fn feed_for(&self, user_id: &str, limit: i32) -> Vec<SocialPost> {
        if limit <= 0 {
            panic!("limit must be positive");
        }
        let following: HashSet<String> = self
            .follows
            .lock()
            .unwrap()
            .iter()
            .filter(|f| f.follower_id == user_id)
            .map(|f| f.followee_id.clone())
            .collect();
        let mut hits: Vec<SocialPost> = self
            .posts
            .lock()
            .unwrap()
            .values()
            .filter(|p| following.contains(&p.author_id))
            .cloned()
            .collect();
        hits.sort_by(|a, b| b.at_utc.cmp(&a.at_utc));
        hits.truncate(limit as usize);
        hits
    }

    fn followers(&self, user_id: &str) -> Vec<String> {
        self.follows
            .lock()
            .unwrap()
            .iter()
            .filter(|f| f.followee_id == user_id)
            .map(|f| f.follower_id.clone())
            .collect()
    }
}
