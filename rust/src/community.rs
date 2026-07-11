//! community — CircleAI community-board primitives.
//!
//! Full Rust port of `src/CircleAI.Community/CommunityPrimitives.cs`:
//!
//! - Records [`CommunityGroup`] / [`Announcement`] / [`VolunteerOpportunity`],
//!   the [`ICommunityBoard`] contract, and the deterministic in-memory
//!   [`InMemoryCommunityBoard`] (groups + membership lookup + announcements +
//!   volunteer opportunities).
//!
//! Sync-only; `DateTimeOffset` → [`chrono::DateTime<Utc>`].

use std::collections::HashMap;
use std::sync::Mutex;

use chrono::{DateTime, Utc};

/// Default `limit` for [`ICommunityBoard::announcements_for`] (C# `limit = 20`).
pub const DEFAULT_ANNOUNCEMENT_LIMIT: i32 = 20;

/// (Community) A community group.
///
/// Mirrors `sealed record CommunityGroup(string GroupId, string Name,
/// string Purpose, IReadOnlyList<string> MemberIds)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CommunityGroup {
    pub group_id: String,
    pub name: String,
    pub purpose: String,
    pub member_ids: Vec<String>,
}

impl CommunityGroup {
    /// Constructs a group, mirroring the positional C# record constructor.
    pub fn new(
        group_id: impl Into<String>,
        name: impl Into<String>,
        purpose: impl Into<String>,
        member_ids: Vec<String>,
    ) -> Self {
        Self {
            group_id: group_id.into(),
            name: name.into(),
            purpose: purpose.into(),
            member_ids,
        }
    }
}

/// (Community) A group announcement.
///
/// Mirrors `sealed record Announcement(string AnnouncementId, string GroupId,
/// string Title, string Body, DateTimeOffset AtUtc)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Announcement {
    pub announcement_id: String,
    pub group_id: String,
    pub title: String,
    pub body: String,
    pub at_utc: DateTime<Utc>,
}

impl Announcement {
    /// Constructs an announcement, mirroring the positional C# record constructor.
    pub fn new(
        announcement_id: impl Into<String>,
        group_id: impl Into<String>,
        title: impl Into<String>,
        body: impl Into<String>,
        at_utc: DateTime<Utc>,
    ) -> Self {
        Self {
            announcement_id: announcement_id.into(),
            group_id: group_id.into(),
            title: title.into(),
            body: body.into(),
            at_utc,
        }
    }
}

/// (Community) A volunteer opportunity.
///
/// Mirrors `sealed record VolunteerOpportunity(string OppId, string GroupId,
/// string Description, int VolunteersNeeded, DateTimeOffset WhenUtc)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct VolunteerOpportunity {
    pub opp_id: String,
    pub group_id: String,
    pub description: String,
    pub volunteers_needed: i32,
    pub when_utc: DateTime<Utc>,
}

impl VolunteerOpportunity {
    /// Constructs an opportunity, mirroring the positional C# record constructor.
    pub fn new(
        opp_id: impl Into<String>,
        group_id: impl Into<String>,
        description: impl Into<String>,
        volunteers_needed: i32,
        when_utc: DateTime<Utc>,
    ) -> Self {
        Self {
            opp_id: opp_id.into(),
            group_id: group_id.into(),
            description: description.into(),
            volunteers_needed,
            when_utc,
        }
    }
}

/// (Community) The community-board contract.
///
/// Mirrors `interface ICommunityBoard`.
pub trait ICommunityBoard {
    /// Creates (or overwrites) a group.
    fn create(&self, g: CommunityGroup);
    /// A group by id, if any.
    fn get_group(&self, id: &str) -> Option<CommunityGroup>;
    /// Groups a member belongs to.
    fn groups_for_member(&self, member_id: &str) -> Vec<CommunityGroup>;
    /// Posts an announcement.
    fn post(&self, a: Announcement);
    /// A group's announcements, newest first (default [`DEFAULT_ANNOUNCEMENT_LIMIT`]).
    fn announcements_for(&self, group_id: &str, limit: i32) -> Vec<Announcement>;
    /// Lists (or overwrites) a volunteer opportunity.
    fn list(&self, o: VolunteerOpportunity);
    /// Opportunities at/after now, earliest first.
    fn opportunities(&self) -> Vec<VolunteerOpportunity>;
}

/// (Community) In-memory [`ICommunityBoard`].
///
/// Mirrors `sealed class InMemoryCommunityBoard`.
pub struct InMemoryCommunityBoard {
    groups: Mutex<HashMap<String, CommunityGroup>>,
    annc: Mutex<Vec<Announcement>>,
    opps: Mutex<HashMap<String, VolunteerOpportunity>>,
}

impl InMemoryCommunityBoard {
    /// Creates an empty board.
    pub fn new() -> Self {
        Self {
            groups: Mutex::new(HashMap::new()),
            annc: Mutex::new(Vec::new()),
            opps: Mutex::new(HashMap::new()),
        }
    }
}

impl Default for InMemoryCommunityBoard {
    fn default() -> Self {
        Self::new()
    }
}

impl ICommunityBoard for InMemoryCommunityBoard {
    fn create(&self, g: CommunityGroup) {
        self.groups.lock().unwrap().insert(g.group_id.clone(), g);
    }

    fn get_group(&self, id: &str) -> Option<CommunityGroup> {
        self.groups.lock().unwrap().get(id).cloned()
    }

    fn groups_for_member(&self, member_id: &str) -> Vec<CommunityGroup> {
        self.groups
            .lock()
            .unwrap()
            .values()
            .filter(|g| g.member_ids.iter().any(|m| m == member_id))
            .cloned()
            .collect()
    }

    fn post(&self, a: Announcement) {
        self.annc.lock().unwrap().push(a);
    }

    fn announcements_for(&self, group_id: &str, limit: i32) -> Vec<Announcement> {
        let mut hits: Vec<Announcement> = self
            .annc
            .lock()
            .unwrap()
            .iter()
            .filter(|a| a.group_id == group_id)
            .cloned()
            .collect();
        hits.sort_by(|a, b| b.at_utc.cmp(&a.at_utc));
        if limit >= 0 {
            hits.truncate(limit as usize);
        }
        hits
    }

    fn list(&self, o: VolunteerOpportunity) {
        self.opps.lock().unwrap().insert(o.opp_id.clone(), o);
    }

    fn opportunities(&self) -> Vec<VolunteerOpportunity> {
        let now = Utc::now();
        let mut hits: Vec<VolunteerOpportunity> = self
            .opps
            .lock()
            .unwrap()
            .values()
            .filter(|o| o.when_utc >= now)
            .cloned()
            .collect();
        hits.sort_by(|a, b| a.when_utc.cmp(&b.when_utc));
        hits
    }
}
