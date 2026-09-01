//! Personal data, collaboration, benchmarking, charts, and the server's
//! endpoints.
//!
//! THE CONSENT GUARD IS THE SERIOUS PART. Everything in the personal section
//! goes through it, and it is scoped, expiring, revocable and fails closed at
//! every branch - because the version missing any one of those is the one that
//! gets built by default and looks identical from the outside.
//!
//! THE ADAPTERS ARE ALL NULL HERE. A calendar, a contact list and a mailbox live
//! behind a platform API this port cannot reach, and a "port" of one would be a
//! type with the right name and no behaviour - which is worse than its absence,
//! because it would report as done.

use std::collections::{HashMap, HashSet};

use crate::platform_plugins::ConsentScope;

// ─────────────────────────────────────────────────────────────────────────────
// The consent guard

/// One grant: a scope, who asked, and when it lapses.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ConsentGrant {
    pub scope: ConsentScope,
    pub granted_to: String,
    pub granted_at_ms: u64,
    /// When it stops working. A grant with NO EXPIRY is refused: permission
    /// given once and held forever is how an assistant ends up reading a mailbox
    /// nobody remembers letting it near.
    pub expires_at_ms: u64,
    /// What the person was told they were agreeing to. Kept so it can be shown
    /// back to them - a grant nobody can restate is a grant nobody really gave.
    pub prompt_shown: String,
}

impl ConsentGrant {
    /// The longest a grant may run: thirty days.
    pub const MAX_LIFETIME_MS: u64 = 30 * 24 * 60 * 60 * 1000;

    pub fn new(
        scope: ConsentScope,
        granted_to: &str,
        granted_at_ms: u64,
        lifetime_ms: u64,
        prompt_shown: &str,
    ) -> Option<Self> {
        if granted_to.trim().is_empty() || lifetime_ms == 0 {
            return None;
        }
        if prompt_shown.trim().is_empty() {
            return None;
        }
        Some(Self {
            scope,
            granted_to: granted_to.to_string(),
            granted_at_ms,
            expires_at_ms: granted_at_ms + lifetime_ms.min(Self::MAX_LIFETIME_MS),
            prompt_shown: prompt_shown.to_string(),
        })
    }

    /// A grant with a clock that has gone backwards is treated as EXPIRED.
    ///
    /// The alternative is a grant that a clock change makes permanent, and
    /// between "briefly asks again" and "never asks again" the first is the only
    /// acceptable failure.
    pub fn is_live(&self, now_ms: u64) -> bool {
        now_ms >= self.granted_at_ms && now_ms < self.expires_at_ms
    }

    pub fn remaining_ms(&self, now_ms: u64) -> u64 {
        self.expires_at_ms.saturating_sub(now_ms)
    }
}

/// Why something was refused.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ConsentRefusal {
    /// Never granted at all.
    NotGranted(ConsentScope),
    /// Granted once and lapsed. A DIFFERENT message, because "you allowed this
    /// last month, allow it again?" is a fair thing to ask and "may I read your
    /// mail?" out of nowhere is not.
    Expired(ConsentScope),
    /// Taken back deliberately. NOT re-asked automatically - a person who
    /// revoked something meant it.
    Revoked(ConsentScope),
    /// Granted to somebody else. A grant belongs to the thing it was given to,
    /// so one plugin cannot ride another's permission.
    GrantedToAnother { scope: ConsentScope, holder: String },
}

impl ConsentRefusal {
    /// What a person reads. In their words, saying what would fix it.
    pub fn message(&self) -> String {
        match self {
            Self::NotGranted(scope) => format!(
                "that needs access to your {}, which has not been allowed",
                Self::subject(scope)
            ),
            Self::Expired(scope) => format!(
                "the permission to reach your {} has lapsed; it can be allowed again",
                Self::subject(scope)
            ),
            Self::Revoked(scope) => format!(
                "access to your {} was turned off",
                Self::subject(scope)
            ),
            Self::GrantedToAnother { scope, holder } => format!(
                "your {} was allowed for {holder}, not for this",
                Self::subject(scope)
            ),
        }
    }

    fn subject(scope: &ConsentScope) -> &'static str {
        match scope {
            ConsentScope::CalendarRead | ConsentScope::CalendarWrite => "calendar",
            ConsentScope::ContactsRead | ConsentScope::ContactsWrite => "contacts",
            ConsentScope::EmailRead | ConsentScope::EmailSend => "mail",
            ConsentScope::LocationRead => "location",
            ConsentScope::PhotosRead => "photos",
        }
    }
}

/// The one gate everything personal goes through.
///
/// FAILS CLOSED AT EVERY BRANCH. There is no path through `check` that returns
/// permission without a live, unrevoked grant held by the caller asking - and
/// the refusal says which of those was missing, so a person can fix it.
#[derive(Debug, Default)]
pub struct ConsentGuard {
    grants: Vec<ConsentGrant>,
    revoked: HashSet<(String, String)>,
    /// Every check, for the person to read. A permission system whose decisions
    /// cannot be reviewed is a permission system nobody can audit.
    audit: Vec<(u64, String, String, bool)>,
    max_audit: usize,
}

impl ConsentGuard {
    pub fn new() -> Self {
        Self { max_audit: 500, ..Default::default() }
    }

    /// Records a grant, replacing any earlier one for the same pair.
    ///
    /// REPLACES rather than accumulates: two live grants for one scope means two
    /// expiry times, and the longer one silently wins.
    pub fn grant(&mut self, grant: ConsentGrant) {
        let key = (grant.granted_to.clone(), grant.scope.label().to_string());
        self.revoked.remove(&key);
        self.grants
            .retain(|g| !(g.granted_to == grant.granted_to && g.scope == grant.scope));
        self.grants.push(grant);
    }

    /// Takes one back. Immediate, and it stays taken back.
    pub fn revoke(&mut self, granted_to: &str, scope: ConsentScope) {
        self.revoked
            .insert((granted_to.to_string(), scope.label().to_string()));
        self.grants
            .retain(|g| !(g.granted_to == granted_to && g.scope == scope));
    }

    /// Takes back everything at once. What a "turn it all off" control calls.
    pub fn revoke_all(&mut self, granted_to: &str) {
        for scope in ConsentScope::ALL {
            self.revoke(granted_to, *scope);
        }
    }

    /// The whole gate.
    pub fn check(
        &mut self,
        granted_to: &str,
        scope: ConsentScope,
        now_ms: u64,
    ) -> Result<(), ConsentRefusal> {
        let outcome = self.decide(granted_to, scope, now_ms);
        self.audit.push((
            now_ms,
            granted_to.to_string(),
            scope.label().to_string(),
            outcome.is_ok(),
        ));
        while self.audit.len() > self.max_audit {
            self.audit.remove(0);
        }
        outcome
    }

    fn decide(
        &self,
        granted_to: &str,
        scope: ConsentScope,
        now_ms: u64,
    ) -> Result<(), ConsentRefusal> {
        if self
            .revoked
            .contains(&(granted_to.to_string(), scope.label().to_string()))
        {
            return Err(ConsentRefusal::Revoked(scope));
        }
        let Some(grant) = self
            .grants
            .iter()
            .find(|g| g.granted_to == granted_to && g.scope == scope)
        else {
            // Held by somebody ELSE is a different answer from never granted:
            // it tells a person the permission exists and is simply not this
            // caller's, which is the difference between confusion and a fix.
            if let Some(other) = self.grants.iter().find(|g| g.scope == scope) {
                return Err(ConsentRefusal::GrantedToAnother {
                    scope,
                    holder: other.granted_to.clone(),
                });
            }
            return Err(ConsentRefusal::NotGranted(scope));
        };
        if !grant.is_live(now_ms) {
            return Err(ConsentRefusal::Expired(scope));
        }
        Ok(())
    }

    /// What is currently allowed, for a screen that shows it.
    pub fn live_grants(&self, now_ms: u64) -> Vec<ConsentGrant> {
        self.grants
            .iter()
            .filter(|g| g.is_live(now_ms))
            .cloned()
            .collect()
    }

    /// Every decision, newest last.
    pub fn audit_trail(&self) -> &[(u64, String, String, bool)] {
        &self.audit
    }

    /// Drops grants that have lapsed. Housekeeping only - an expired grant
    /// already fails `check`, so this changes nothing about what is allowed.
    pub fn prune(&mut self, now_ms: u64) -> usize {
        let before = self.grants.len();
        self.grants.retain(|g| g.is_live(now_ms));
        before - self.grants.len()
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Personal adapters

/// An appointment.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct CalendarEvent {
    pub event_id: String,
    pub title: String,
    pub starts_at_ms: u64,
    pub ends_at_ms: u64,
    /// Where. A calendar is a record of where somebody will physically be,
    /// which is why reading one is not a small permission.
    pub location: String,
    pub attendees: Vec<String>,
    pub all_day: bool,
}

/// Reaches a calendar.
pub trait CalendarAdapter {
    fn is_available(&self) -> bool;
    fn events_between(&self, from_ms: u64, to_ms: u64) -> Result<Vec<CalendarEvent>, String>;
    /// Writing is SEPARATE from reading, all the way down. An assistant that can
    /// answer "when am I free" does not thereby get to put things in the diary.
    fn create(&mut self, event: &CalendarEvent) -> Result<String, String>;
}

/// Reaches no calendar.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullCalendarAdapter;

impl CalendarAdapter for NullCalendarAdapter {
    fn is_available(&self) -> bool {
        false
    }
    fn events_between(&self, _from_ms: u64, _to_ms: u64) -> Result<Vec<CalendarEvent>, String> {
        Err("no calendar is connected on this device".into())
    }
    fn create(&mut self, _event: &CalendarEvent) -> Result<String, String> {
        Err("no calendar is connected, so nothing was added".into())
    }
}

/// Somebody in a contact list.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct Contact {
    pub contact_id: String,
    pub display_name: String,
    pub emails: Vec<String>,
    pub phones: Vec<String>,
    pub organisation: String,
}

impl Contact {
    /// A name to speak. Falls back to the first address rather than to a blank -
    /// "your contact" is less useful than a bare email nobody named.
    pub fn spoken_name(&self) -> String {
        if !self.display_name.is_empty() {
            return self.display_name.clone();
        }
        self.emails
            .first()
            .or_else(|| self.phones.first())
            .cloned()
            .unwrap_or_else(|| "someone".into())
    }
}

/// Reaches a contact list.
pub trait ContactsAdapter {
    fn is_available(&self) -> bool;
    fn search(&self, query: &str, limit: usize) -> Result<Vec<Contact>, String>;
    fn get(&self, contact_id: &str) -> Result<Contact, String>;
}

/// Reaches none.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullContactsAdapter;

impl ContactsAdapter for NullContactsAdapter {
    fn is_available(&self) -> bool {
        false
    }
    fn search(&self, _query: &str, _limit: usize) -> Result<Vec<Contact>, String> {
        Err("no contact list is connected on this device".into())
    }
    fn get(&self, _contact_id: &str) -> Result<Contact, String> {
        Err("no contact list is connected on this device".into())
    }
}

/// A message.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct EmailMessage {
    pub message_id: String,
    pub from: String,
    pub to: Vec<String>,
    pub cc: Vec<String>,
    pub subject: String,
    pub body: String,
    pub received_at_ms: u64,
    pub unread: bool,
}

impl EmailMessage {
    /// How many people this would reach. Shown before sending, because the
    /// difference between one recipient and forty is the difference between a
    /// reply and an incident.
    pub fn recipient_count(&self) -> usize {
        self.to.len() + self.cc.len()
    }
}

/// Reaches a mailbox.
pub trait EmailAdapter {
    fn is_available(&self) -> bool;
    fn recent(&self, limit: usize) -> Result<Vec<EmailMessage>, String>;
    /// SENDING IS THE MOST CONSEQUENTIAL THING IN THIS FILE. A message sent as
    /// somebody cannot be unsent, is read as theirs, and reaches people who
    /// never agreed to any of this - which is why its scope is never bundled
    /// with reading and why every implementation asks before it goes.
    fn send(&mut self, message: &EmailMessage) -> Result<String, String>;
}

/// Reaches no mailbox.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullEmailAdapter;

impl EmailAdapter for NullEmailAdapter {
    fn is_available(&self) -> bool {
        false
    }
    fn recent(&self, _limit: usize) -> Result<Vec<EmailMessage>, String> {
        Err("no mailbox is connected on this device".into())
    }
    fn send(&mut self, _message: &EmailMessage) -> Result<String, String> {
        Err("no mailbox is connected, so nothing was sent to anyone".into())
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Collaboration

/// Whether somebody is around.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum PresenceState {
    /// Nothing known. NOT offline - a person whose device has not checked in is
    /// not a person who has left, and showing them as away is a small lie the
    /// people messaging them act on.
    #[default]
    Unknown,
    Online,
    Away,
    /// Asked not to be disturbed. A stronger statement than away, and one a
    /// notification should honour.
    Busy,
    Offline,
}

impl PresenceState {
    pub fn should_notify(&self) -> bool {
        !matches!(self, Self::Busy)
    }

    pub fn label(&self) -> &'static str {
        match self {
            Self::Unknown => "not known",
            Self::Online => "here",
            Self::Away => "away",
            Self::Busy => "busy",
            Self::Offline => "offline",
        }
    }
}

/// A place people talk.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct Channel {
    pub channel_id: String,
    pub name: String,
    pub topic: String,
    pub members: Vec<String>,
    /// Whether it is only for the people already in it.
    pub private: bool,
    pub created_at_ms: u64,
}

/// Something somebody said.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ChannelMessage {
    pub message_id: String,
    pub channel_id: String,
    pub author: String,
    pub text: String,
    pub sent_at_ms: u64,
    /// The message this replies to, so a thread is a structure rather than a
    /// convention.
    pub reply_to: String,
}

/// Holds channels.
pub trait ChannelStore {
    fn create(&mut self, channel: Channel) -> Result<String, String>;
    fn get(&self, channel_id: &str) -> Option<Channel>;
    /// Only what this person may see. A private channel they are not in does not
    /// appear - not greyed out, not listed by name.
    fn visible_to(&self, member: &str) -> Vec<Channel>;
    fn join(&mut self, channel_id: &str, member: &str) -> Result<(), String>;
}

/// Channels in memory.
#[derive(Debug, Default)]
pub struct InMemoryChannelStore {
    channels: HashMap<String, Channel>,
}

impl InMemoryChannelStore {
    pub fn new() -> Self {
        Self::default()
    }
}

impl ChannelStore for InMemoryChannelStore {
    fn create(&mut self, channel: Channel) -> Result<String, String> {
        if channel.channel_id.trim().is_empty() {
            return Err("a channel needs an identifier".into());
        }
        if self.channels.contains_key(&channel.channel_id) {
            return Err(format!("channel {} already exists", channel.channel_id));
        }
        let id = channel.channel_id.clone();
        self.channels.insert(id.clone(), channel);
        Ok(id)
    }

    fn get(&self, channel_id: &str) -> Option<Channel> {
        self.channels.get(channel_id).cloned()
    }

    fn visible_to(&self, member: &str) -> Vec<Channel> {
        let mut out: Vec<Channel> = self
            .channels
            .values()
            .filter(|c| !c.private || c.members.iter().any(|m| m == member))
            .cloned()
            .collect();
        out.sort_by(|a, b| a.name.cmp(&b.name));
        out
    }

    fn join(&mut self, channel_id: &str, member: &str) -> Result<(), String> {
        let Some(channel) = self.channels.get_mut(channel_id) else {
            return Err("there is no such channel".into());
        };
        // A private channel is NOT joinable by asking. Somebody already in it
        // has to add you, which is what private means.
        if channel.private {
            return Err("that channel is private; somebody in it has to add you".into());
        }
        if !channel.members.iter().any(|m| m == member) {
            channel.members.push(member.to_string());
        }
        Ok(())
    }
}

/// Holds no channels.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullChannelStore;

impl ChannelStore for NullChannelStore {
    fn create(&mut self, _channel: Channel) -> Result<String, String> {
        Err("no collaboration service is configured on this device".into())
    }
    fn get(&self, _channel_id: &str) -> Option<Channel> {
        None
    }
    fn visible_to(&self, _member: &str) -> Vec<Channel> {
        Vec::new()
    }
    fn join(&mut self, _channel_id: &str, _member: &str) -> Result<(), String> {
        Err("no collaboration service is configured on this device".into())
    }
}

/// Holds messages.
pub trait MessageStore {
    fn append(&mut self, message: ChannelMessage) -> Result<(), String>;
    fn history(&self, channel_id: &str, limit: usize) -> Vec<ChannelMessage>;
    fn thread(&self, message_id: &str) -> Vec<ChannelMessage>;
}

/// Messages in memory.
#[derive(Debug, Default)]
pub struct InMemoryMessageStore {
    messages: Vec<ChannelMessage>,
    max_messages: usize,
}

impl InMemoryMessageStore {
    pub fn new(max_messages: usize) -> Self {
        Self {
            messages: Vec::new(),
            max_messages: if max_messages == 0 { 5000 } else { max_messages },
        }
    }
}

impl MessageStore for InMemoryMessageStore {
    fn append(&mut self, message: ChannelMessage) -> Result<(), String> {
        if message.channel_id.trim().is_empty() || message.message_id.trim().is_empty() {
            return Err("a message needs a channel and an identifier".into());
        }
        // The same message arriving twice is normal on a flaky link, and storing
        // it twice shows the sender saying it twice.
        if self.messages.iter().any(|m| m.message_id == message.message_id) {
            return Ok(());
        }
        self.messages.push(message);
        while self.messages.len() > self.max_messages {
            self.messages.remove(0);
        }
        Ok(())
    }

    fn history(&self, channel_id: &str, limit: usize) -> Vec<ChannelMessage> {
        let mut out: Vec<ChannelMessage> = self
            .messages
            .iter()
            .filter(|m| m.channel_id == channel_id)
            .cloned()
            .collect();
        out.sort_by_key(|m| m.sent_at_ms);
        // The LAST n, not the first: history means what was said recently.
        if limit > 0 && out.len() > limit {
            out.drain(..out.len() - limit);
        }
        out
    }

    fn thread(&self, message_id: &str) -> Vec<ChannelMessage> {
        let mut out: Vec<ChannelMessage> = self
            .messages
            .iter()
            .filter(|m| m.message_id == message_id || m.reply_to == message_id)
            .cloned()
            .collect();
        out.sort_by_key(|m| m.sent_at_ms);
        out
    }
}

/// Holds no messages.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullMessageStore;

impl MessageStore for NullMessageStore {
    fn append(&mut self, _message: ChannelMessage) -> Result<(), String> {
        Err("no message store is configured; nothing was kept".into())
    }
    fn history(&self, _channel_id: &str, _limit: usize) -> Vec<ChannelMessage> {
        Vec::new()
    }
    fn thread(&self, _message_id: &str) -> Vec<ChannelMessage> {
        Vec::new()
    }
}

/// Who is around.
pub trait Presence {
    fn set(&mut self, member: &str, state: PresenceState, now_ms: u64);
    fn get(&self, member: &str, now_ms: u64) -> PresenceState;
    fn online(&self, now_ms: u64) -> Vec<String>;
}

/// Presence in memory.
#[derive(Debug, Default)]
pub struct InMemoryPresence {
    states: HashMap<String, (PresenceState, u64)>,
    /// After this, a state is stale and reads as unknown.
    stale_after_ms: u64,
}

impl InMemoryPresence {
    pub fn new(stale_after_ms: u64) -> Self {
        Self {
            states: HashMap::new(),
            stale_after_ms: if stale_after_ms == 0 { 120_000 } else { stale_after_ms },
        }
    }
}

impl Presence for InMemoryPresence {
    fn set(&mut self, member: &str, state: PresenceState, now_ms: u64) {
        self.states.insert(member.to_string(), (state, now_ms));
    }

    /// A stale state becomes UNKNOWN, not offline.
    ///
    /// A device that stopped checking in has not necessarily gone anywhere, and
    /// telling people someone is offline when they are sitting there is a small
    /// lie that changes whether anybody messages them.
    fn get(&self, member: &str, now_ms: u64) -> PresenceState {
        let Some((state, at)) = self.states.get(member) else {
            return PresenceState::Unknown;
        };
        if now_ms.saturating_sub(*at) > self.stale_after_ms {
            PresenceState::Unknown
        } else {
            *state
        }
    }

    fn online(&self, now_ms: u64) -> Vec<String> {
        let mut out: Vec<String> = self
            .states
            .keys()
            .filter(|m| self.get(m, now_ms) == PresenceState::Online)
            .cloned()
            .collect();
        out.sort();
        out
    }
}

/// Knows nothing about anybody.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullPresence;

impl Presence for NullPresence {
    fn set(&mut self, _member: &str, _state: PresenceState, _now_ms: u64) {}
    fn get(&self, _member: &str, _now_ms: u64) -> PresenceState {
        PresenceState::Unknown
    }
    fn online(&self, _now_ms: u64) -> Vec<String> {
        Vec::new()
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Benchmarking

/// How an answer is judged.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct BenchScoring {
    pub scorer: String,
    pub expected: String,
    /// For the numeric scorer, in THOUSANDTHS so the spec carries no float.
    pub tolerance_thousandths: i64,
    pub case_sensitive: bool,
}

/// What one case produced.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct BenchResult {
    pub case_id: String,
    pub actual: String,
    /// 0..1. A scorer that only ever returns 0 or 1 says so by returning those.
    pub score: f32,
    pub passed: bool,
    pub took_ms: u64,
    /// Why it failed, in enough detail to act on. "Expected X, got Y" beats
    /// "failed", which sends somebody back to run it again by hand.
    pub detail: String,
}

/// Judges an answer.
pub trait BenchScorer {
    fn name(&self) -> &'static str;
    fn score(&self, actual: &str, scoring: &BenchScoring) -> (f32, String);
}

/// Exactly right or not.
///
/// Whitespace at the ends is trimmed - a trailing newline is a formatting
/// difference, not a wrong answer, and counting it as one buries real failures.
#[derive(Debug, Default, Clone, Copy)]
pub struct ExactMatchScorer;

impl BenchScorer for ExactMatchScorer {
    fn name(&self) -> &'static str {
        "exact"
    }

    fn score(&self, actual: &str, scoring: &BenchScoring) -> (f32, String) {
        let (a, e) = (actual.trim(), scoring.expected.trim());
        let same = if scoring.case_sensitive {
            a == e
        } else {
            a.eq_ignore_ascii_case(e)
        };
        if same {
            (1.0, String::new())
        } else {
            (0.0, format!("expected {e:?}, got {a:?}"))
        }
    }
}

/// Contains the answer.
#[derive(Debug, Default, Clone, Copy)]
pub struct SubstringScorer;

impl BenchScorer for SubstringScorer {
    fn name(&self) -> &'static str {
        "substring"
    }

    fn score(&self, actual: &str, scoring: &BenchScoring) -> (f32, String) {
        let (a, e) = if scoring.case_sensitive {
            (actual.to_string(), scoring.expected.clone())
        } else {
            (actual.to_lowercase(), scoring.expected.to_lowercase())
        };
        if a.contains(&e) {
            (1.0, String::new())
        } else {
            (0.0, format!("{:?} does not appear in the answer", scoring.expected))
        }
    }
}

/// Close enough numerically.
///
/// A number in prose is found rather than required to be the whole answer: a
/// model that says "about 42 degrees" answered the question, and an exact-match
/// scorer would call that wrong.
#[derive(Debug, Default, Clone, Copy)]
pub struct NumericToleranceScorer;

impl NumericToleranceScorer {
    /// The first number in a string, sign and decimal point included.
    pub fn first_number(text: &str) -> Option<f64> {
        let mut current = String::new();
        for c in text.chars() {
            if c.is_ascii_digit()
                || (c == '.' && !current.contains('.') && !current.is_empty())
                || ((c == '-' || c == '+') && current.is_empty())
            {
                current.push(c);
            } else if !current.is_empty() {
                if let Ok(value) = current.trim_end_matches('.').parse::<f64>() {
                    return Some(value);
                }
                current.clear();
            }
        }
        current.trim_end_matches('.').parse::<f64>().ok()
    }
}

impl BenchScorer for NumericToleranceScorer {
    fn name(&self) -> &'static str {
        "numeric"
    }

    fn score(&self, actual: &str, scoring: &BenchScoring) -> (f32, String) {
        let (Some(got), Some(want)) = (
            Self::first_number(actual),
            Self::first_number(&scoring.expected),
        ) else {
            return (0.0, "no number in the answer".into());
        };
        let tolerance = scoring.tolerance_thousandths as f64 / 1000.0;
        let difference = (got - want).abs();
        if difference <= tolerance {
            (1.0, String::new())
        } else {
            (
                0.0,
                format!("expected {want} within {tolerance}, got {got} (off by {difference})"),
            )
        }
    }
}

/// Matches a pattern.
///
/// NO REGEX ENGINE. Pulling one in for a benchmark scorer adds a dependency to
/// every target, so this supports the two things benchmark expectations actually
/// use - anchors and `.*` - and says plainly that it is not a full engine.
#[derive(Debug, Default, Clone, Copy)]
pub struct RegexScorer;

impl RegexScorer {
    /// Handles `^`, `$` and `.*` between literals. Anything else is treated as a
    /// literal, which fails loudly rather than matching something unintended.
    pub fn matches(pattern: &str, text: &str) -> bool {
        let anchored_start = pattern.starts_with('^');
        let anchored_end = pattern.ends_with('$') && !pattern.ends_with("\\$");
        let body = pattern
            .trim_start_matches('^')
            .trim_end_matches('$');
        let parts: Vec<&str> = body.split(".*").collect();

        let mut position = 0usize;
        for (index, part) in parts.iter().enumerate() {
            if part.is_empty() {
                continue;
            }
            let Some(found) = text[position..].find(part) else { return false };
            if index == 0 && anchored_start && found != 0 {
                return false;
            }
            position += found + part.len();
        }
        if anchored_end {
            if let Some(last) = parts.last().filter(|p| !p.is_empty()) {
                return text.ends_with(last);
            }
        }
        true
    }
}

impl BenchScorer for RegexScorer {
    fn name(&self) -> &'static str {
        "regex"
    }

    fn score(&self, actual: &str, scoring: &BenchScoring) -> (f32, String) {
        if Self::matches(&scoring.expected, actual) {
            (1.0, String::new())
        } else {
            (0.0, format!("{:?} does not match {:?}", actual, scoring.expected))
        }
    }
}

/// The scorers that ship.
pub struct BuiltInScorers;

impl BuiltInScorers {
    pub const NAMES: &'static [&'static str] = &["exact", "substring", "numeric", "regex"];

    /// `None` for an unknown name, which is then a REFUSAL rather than a silent
    /// fall back to exact match - a suite scored by the wrong scorer reports
    /// numbers nobody can trust.
    pub fn get(name: &str) -> Option<Box<dyn BenchScorer + Send + Sync>> {
        match name {
            "exact" => Some(Box::new(ExactMatchScorer)),
            "substring" => Some(Box::new(SubstringScorer)),
            "numeric" => Some(Box::new(NumericToleranceScorer)),
            "regex" => Some(Box::new(RegexScorer)),
            _ => None,
        }
    }
}

/// One suite of cases.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct BenchSuite {
    pub suite_id: String,
    pub title: String,
    /// `(case id, prompt, scoring)`.
    pub cases: Vec<(String, String, BenchScoring)>,
}

/// What suites are known.
#[derive(Debug, Default)]
pub struct BenchSuiteRegistry {
    suites: HashMap<String, BenchSuite>,
}

impl BenchSuiteRegistry {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn register(&mut self, suite: BenchSuite) -> Result<(), String> {
        if suite.suite_id.trim().is_empty() {
            return Err("a suite needs an identifier".into());
        }
        // A case whose scorer does not exist would score zero forever and look
        // like a failing model. Refused at registration, where it is fixable.
        for (case_id, _, scoring) in &suite.cases {
            if BuiltInScorers::get(&scoring.scorer).is_none() {
                return Err(format!(
                    "case '{case_id}' asks for scorer '{}', which does not exist",
                    scoring.scorer
                ));
            }
        }
        self.suites.insert(suite.suite_id.clone(), suite);
        Ok(())
    }

    pub fn get(&self, suite_id: &str) -> Option<BenchSuite> {
        self.suites.get(suite_id).cloned()
    }

    pub fn names(&self) -> Vec<String> {
        let mut out: Vec<String> = self.suites.keys().cloned().collect();
        out.sort();
        out
    }
}

/// Runs a suite.
pub struct BenchRunner {
    answer: Option<Box<dyn Fn(&str) -> String + Send + Sync>>,
}

impl BenchRunner {
    pub fn new(answer: Option<Box<dyn Fn(&str) -> String + Send + Sync>>) -> Self {
        Self { answer }
    }

    pub fn is_available(&self) -> bool {
        self.answer.is_some()
    }

    /// Runs every case. Returns results even when some fail - a runner that
    /// stops at the first failure hides how many there are.
    pub fn run(&self, suite: &BenchSuite) -> Vec<BenchResult> {
        let Some(answer) = &self.answer else {
            return suite
                .cases
                .iter()
                .map(|(case_id, _, _)| BenchResult {
                    case_id: case_id.clone(),
                    detail: "no model is wired up, so nothing was scored".into(),
                    ..Default::default()
                })
                .collect();
        };
        suite
            .cases
            .iter()
            .map(|(case_id, prompt, scoring)| {
                let actual = answer(prompt);
                let Some(scorer) = BuiltInScorers::get(&scoring.scorer) else {
                    return BenchResult {
                        case_id: case_id.clone(),
                        actual,
                        detail: format!("scorer '{}' does not exist", scoring.scorer),
                        ..Default::default()
                    };
                };
                let (score, detail) = scorer.score(&actual, scoring);
                BenchResult {
                    case_id: case_id.clone(),
                    actual,
                    score,
                    passed: score >= 1.0,
                    took_ms: 0,
                    detail,
                }
            })
            .collect()
    }

    /// The pass rate. Reported as a FRACTION with the counts alongside, because
    /// "80%" over five cases and over five hundred are different claims.
    pub fn summarise(results: &[BenchResult]) -> (usize, usize, f32) {
        let passed = results.iter().filter(|r| r.passed).count();
        let total = results.len();
        (
            passed,
            total,
            if total == 0 { 0.0 } else { passed as f32 / total as f32 },
        )
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Charts

/// What kind of chart.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum ChartType {
    #[default]
    Line,
    Bar,
    /// Stacked bars. Its own type, because reading a stack means reading
    /// segments and a grouped bar means reading heights.
    StackedBar,
    Scatter,
    Area,
    /// Deliberately last, and rarely right: people compare angles badly, and
    /// anything past about five slices is unreadable.
    Pie,
}

/// One point.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct ChartDataPoint {
    pub x: f64,
    pub y: f64,
    /// What to print at it, when the number is not the label. Empty means use
    /// the value.
    pub label: String,
}

/// One line or set of bars.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct ChartSeries {
    pub name: String,
    pub points: Vec<ChartDataPoint>,
    /// Empty means the palette picks. A colour named here overrides it, which is
    /// how a series keeps its colour across charts.
    pub colour: String,
}

impl ChartSeries {
    /// The value range. `None` for an empty series rather than (0,0), which
    /// would silently drag every axis to include zero.
    pub fn y_range(&self) -> Option<(f64, f64)> {
        let mut iter = self.points.iter().map(|p| p.y);
        let first = iter.next()?;
        Some(iter.fold((first, first), |(lo, hi), v| (lo.min(v), hi.max(v))))
    }
}

/// How it looks.
#[derive(Debug, Clone, PartialEq)]
pub struct ChartStyle {
    pub width: u32,
    pub height: u32,
    pub background: String,
    pub foreground: String,
    /// The series colours, in order. Chosen to stay distinguishable in greyscale
    /// and to remain separable for the commonest colour vision deficiency -
    /// hue alone is not enough to encode a series.
    pub palette: Vec<String>,
    pub show_grid: bool,
    pub show_legend: bool,
    pub margin: u32,
}

impl Default for ChartStyle {
    fn default() -> Self {
        Self {
            width: 720,
            height: 420,
            background: "#ffffff".into(),
            foreground: "#2c3e50".into(),
            palette: vec![
                "#2196f3".into(),
                "#2c3e50".into(),
                "#00897b".into(),
                "#8e24aa".into(),
                "#546e7a".into(),
                "#c62828".into(),
            ],
            show_grid: true,
            show_legend: true,
            margin: 48,
        }
    }
}

impl ChartStyle {
    /// The drawing area inside the margins.
    ///
    /// Saturating, because a margin larger than the canvas would otherwise wrap
    /// to an enormous width and draw nothing visible.
    pub fn plot_area(&self) -> (u32, u32) {
        (
            self.width.saturating_sub(self.margin * 2).max(1),
            self.height.saturating_sub(self.margin * 2).max(1),
        )
    }

    pub fn colour_for(&self, index: usize) -> String {
        if self.palette.is_empty() {
            self.foreground.clone()
        } else {
            self.palette[index % self.palette.len()].clone()
        }
    }
}

/// Text sizes, in points.
///
/// A TABLE, not measurements: measuring text needs a font engine, which is a
/// platform dependency. These are enough to lay out axes and stop labels
/// overlapping, and the estimate is deliberately generous so a long label is cut
/// rather than drawn over its neighbour.
pub struct ChartFonts;

impl ChartFonts {
    pub const TITLE_PT: f32 = 16.0;
    pub const AXIS_PT: f32 = 11.0;
    pub const LEGEND_PT: f32 = 11.0;

    /// About how wide a string will be. Averages 0.6 em per character, which
    /// over-estimates for narrow text and is the right direction to be wrong in.
    pub fn approx_width_px(text: &str, size_pt: f32) -> f32 {
        text.chars().count() as f32 * size_pt * 0.6
    }

    /// Shortens with an ellipsis so it fits.
    pub fn fit(text: &str, size_pt: f32, max_px: f32) -> String {
        if Self::approx_width_px(text, size_pt) <= max_px {
            return text.to_string();
        }
        let per_char = size_pt * 0.6;
        let room = ((max_px / per_char) as usize).saturating_sub(1);
        if room == 0 {
            return String::new();
        }
        text.chars().take(room).collect::<String>() + "\u{2026}"
    }
}

/// A whole chart.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct ChartSpec {
    pub chart_type: ChartType,
    pub title: String,
    pub x_label: String,
    pub y_label: String,
    pub series: Vec<ChartSeries>,
    pub style: ChartStyle,
}

impl ChartSpec {
    /// The range across every series.
    ///
    /// INCLUDES ZERO for bars, because a bar chart whose axis starts elsewhere
    /// misrepresents every comparison on it - a bar twice as tall reads as twice
    /// as much, and with a cut axis it is not.
    pub fn y_range(&self) -> (f64, f64) {
        let ranges: Vec<(f64, f64)> = self.series.iter().filter_map(|s| s.y_range()).collect();
        if ranges.is_empty() {
            return (0.0, 1.0);
        }
        let (mut lo, mut hi) = ranges.iter().fold(
            (f64::INFINITY, f64::NEG_INFINITY),
            |(lo, hi), (a, b)| (lo.min(*a), hi.max(*b)),
        );
        if matches!(self.chart_type, ChartType::Bar | ChartType::StackedBar | ChartType::Area) {
            lo = lo.min(0.0);
            hi = hi.max(0.0);
        }
        // A flat series has no range, and dividing by it produces infinities all
        // the way through the layout.
        if (hi - lo).abs() < f64::EPSILON {
            return (lo - 0.5, hi + 0.5);
        }
        (lo, hi)
    }

    /// Ticks by WHOLE-STEP INDEXING, never repeated addition.
    ///
    /// Accumulating a float step drops the top tick on ranges like 0.001..0.009,
    /// because the accumulated value lands a hair above the end.
    pub fn ticks(&self, count: usize) -> Vec<f64> {
        let (lo, hi) = self.y_range();
        let steps = count.max(2);
        let step = (hi - lo) / (steps - 1) as f64;
        (0..steps).map(|i| lo + step * i as f64).collect()
    }
}

/// Makes specs for the common cases.
#[derive(Debug, Default, Clone, Copy)]
pub struct ChartSpecFactory;

impl ChartSpecFactory {
    /// A line over time, x as a timestamp.
    pub fn time_series(title: &str, name: &str, points: Vec<(u64, f64)>) -> ChartSpec {
        ChartSpec {
            chart_type: ChartType::Line,
            title: title.to_string(),
            series: vec![ChartSeries {
                name: name.to_string(),
                points: points
                    .into_iter()
                    .map(|(at, value)| ChartDataPoint {
                        x: at as f64,
                        y: value,
                        label: String::new(),
                    })
                    .collect(),
                colour: String::new(),
            }],
            ..Default::default()
        }
    }

    /// Named quantities as bars.
    pub fn categories(title: &str, values: Vec<(String, f64)>) -> ChartSpec {
        ChartSpec {
            chart_type: ChartType::Bar,
            title: title.to_string(),
            series: vec![ChartSeries {
                name: title.to_string(),
                points: values
                    .into_iter()
                    .enumerate()
                    .map(|(i, (label, y))| ChartDataPoint { x: i as f64, y, label })
                    .collect(),
                colour: String::new(),
            }],
            style: ChartStyle { show_legend: false, ..Default::default() },
            ..Default::default()
        }
    }

    /// Proportions.
    ///
    /// Returns a BAR chart past five slices. A pie with a dozen wedges cannot be
    /// read, and quietly producing one because it was asked for is not a
    /// kindness.
    pub fn proportions(title: &str, values: Vec<(String, f64)>) -> ChartSpec {
        let too_many = values.len() > 5;
        let mut spec = Self::categories(title, values);
        spec.chart_type = if too_many { ChartType::Bar } else { ChartType::Pie };
        spec
    }
}

/// Draws a chart.
pub trait ChartRenderer {
    fn supports(&self, chart_type: ChartType) -> bool;
    fn render(&self, spec: &ChartSpec) -> Result<Vec<u8>, String>;
}

/// The default renderer.
///
/// Named for the C# class it mirrors, and it draws SVG rather than PDF: SVG
/// needs no font engine and no PDF writer, and every head here can display it.
#[derive(Debug, Default, Clone, Copy)]
pub struct PdfSharpChartRenderer;

impl PdfSharpChartRenderer {
    fn escape(text: &str) -> String {
        text.replace('&', "&amp;")
            .replace('<', "&lt;")
            .replace('>', "&gt;")
    }
}

impl ChartRenderer for PdfSharpChartRenderer {
    /// Everything except pie, which needs arc geometry this does not carry -
    /// and which `ChartSpecFactory` already steers away from.
    fn supports(&self, chart_type: ChartType) -> bool {
        chart_type != ChartType::Pie
    }

    fn render(&self, spec: &ChartSpec) -> Result<Vec<u8>, String> {
        if !self.supports(spec.chart_type) {
            return Err("this renderer does not draw pie charts".into());
        }
        if spec.series.iter().all(|s| s.points.is_empty()) {
            return Err("there is nothing to plot".into());
        }

        let style = &spec.style;
        let (plot_w, plot_h) = style.plot_area();
        let (lo, hi) = spec.y_range();
        let span = hi - lo;
        let margin = style.margin as f64;

        let mut svg = format!(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{}\" height=\"{}\" \
viewBox=\"0 0 {} {}\"><rect width=\"100%\" height=\"100%\" fill=\"{}\"/>",
            style.width, style.height, style.width, style.height, style.background
        );

        if !spec.title.is_empty() {
            svg.push_str(&format!(
                "<text x=\"{}\" y=\"{}\" font-size=\"{}\" fill=\"{}\" text-anchor=\"middle\">{}</text>",
                style.width / 2,
                style.margin as f32 * 0.6,
                ChartFonts::TITLE_PT,
                style.foreground,
                Self::escape(&ChartFonts::fit(
                    &spec.title,
                    ChartFonts::TITLE_PT,
                    plot_w as f32
                ))
            ));
        }

        if style.show_grid {
            for tick in spec.ticks(5) {
                let y = margin + plot_h as f64 * (1.0 - (tick - lo) / span);
                svg.push_str(&format!(
                    "<line x1=\"{margin}\" y1=\"{y:.1}\" x2=\"{:.1}\" y2=\"{y:.1}\" \
stroke=\"{}\" stroke-opacity=\"0.15\"/><text x=\"{:.1}\" y=\"{:.1}\" font-size=\"{}\" \
fill=\"{}\" text-anchor=\"end\">{tick:.2}</text>",
                    margin + plot_w as f64,
                    style.foreground,
                    margin - 6.0,
                    y + 4.0,
                    ChartFonts::AXIS_PT,
                    style.foreground
                ));
            }
        }

        for (index, series) in spec.series.iter().enumerate() {
            if series.points.is_empty() {
                continue;
            }
            let colour = if series.colour.is_empty() {
                style.colour_for(index)
            } else {
                series.colour.clone()
            };
            // A single point has no x range to divide by, so it is placed in the
            // middle rather than producing a NaN that draws nothing.
            let last = series.points.len().saturating_sub(1).max(1) as f64;
            let at = |i: usize, point: &ChartDataPoint| {
                (
                    margin + plot_w as f64 * (i as f64 / last),
                    margin + plot_h as f64 * (1.0 - (point.y - lo) / span),
                )
            };
            match spec.chart_type {
                ChartType::Bar | ChartType::StackedBar => {
                    let width = (plot_w as f64 / series.points.len() as f64) * 0.7;
                    let zero = margin + plot_h as f64 * (1.0 - (0.0 - lo) / span);
                    for (i, point) in series.points.iter().enumerate() {
                        let (x, y) = at(i, point);
                        svg.push_str(&format!(
                            "<rect x=\"{:.1}\" y=\"{:.1}\" width=\"{width:.1}\" \
height=\"{:.1}\" fill=\"{colour}\"/>",
                            x - width / 2.0,
                            y.min(zero),
                            (zero - y).abs()
                        ));
                    }
                }
                ChartType::Scatter => {
                    for (i, point) in series.points.iter().enumerate() {
                        let (x, y) = at(i, point);
                        svg.push_str(&format!(
                            "<circle cx=\"{x:.1}\" cy=\"{y:.1}\" r=\"3\" fill=\"{colour}\"/>"
                        ));
                    }
                }
                _ => {
                    let path: Vec<String> = series
                        .points
                        .iter()
                        .enumerate()
                        .map(|(i, point)| {
                            let (x, y) = at(i, point);
                            format!("{}{x:.1},{y:.1}", if i == 0 { "M" } else { "L" })
                        })
                        .collect();
                    svg.push_str(&format!(
                        "<path d=\"{}\" fill=\"none\" stroke=\"{colour}\" stroke-width=\"2\"/>",
                        path.join(" ")
                    ));
                }
            }
        }

        if style.show_legend {
            for (index, series) in spec.series.iter().enumerate() {
                let y = margin + 14.0 * index as f64;
                svg.push_str(&format!(
                    "<rect x=\"{:.1}\" y=\"{:.1}\" width=\"10\" height=\"10\" fill=\"{}\"/>\
<text x=\"{:.1}\" y=\"{:.1}\" font-size=\"{}\" fill=\"{}\">{}</text>",
                    margin + plot_w as f64 - 120.0,
                    y,
                    if series.colour.is_empty() {
                        style.colour_for(index)
                    } else {
                        series.colour.clone()
                    },
                    margin + plot_w as f64 - 104.0,
                    y + 9.0,
                    ChartFonts::LEGEND_PT,
                    style.foreground,
                    Self::escape(&ChartFonts::fit(&series.name, ChartFonts::LEGEND_PT, 100.0))
                ));
            }
        }

        svg.push_str("</svg>");
        Ok(svg.into_bytes())
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Server endpoints and options

/// How a request proves who it is.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct AuthOptions {
    /// OFF means the server takes any request. Only sane on a loopback socket,
    /// and the server says so rather than assuming nobody else can reach it.
    pub require_auth: bool,
    pub api_keys: Vec<String>,
    pub allow_loopback_without_key: bool,
}

impl AuthOptions {
    /// Constant-time comparison over the WHOLE key.
    ///
    /// `==` on a string returns as soon as two bytes differ, and the timing
    /// difference is measurable over a network - which is how a key gets guessed
    /// one byte at a time.
    pub fn accepts(&self, presented: &str, from_loopback: bool) -> bool {
        if !self.require_auth {
            return true;
        }
        if from_loopback && self.allow_loopback_without_key {
            return true;
        }
        let presented = presented.as_bytes();
        let mut matched = false;
        for key in &self.api_keys {
            let key = key.as_bytes();
            let mut difference = (key.len() ^ presented.len()) as u8;
            for i in 0..key.len().max(presented.len()) {
                difference |= key.get(i).copied().unwrap_or(0)
                    ^ presented.get(i).copied().unwrap_or(0);
            }
            matched |= difference == 0;
        }
        matched
    }
}

/// Token settings.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct JwtOptions {
    pub issuer: String,
    pub audience: String,
    /// How long a token stays good. SHORT by default - a long-lived token is a
    /// password that cannot be changed.
    pub lifetime_seconds: u64,
    /// Allowed clock difference. Small, because a large one lets an expired
    /// token keep working.
    pub clock_skew_seconds: u64,
    pub require_expiry: bool,
}

impl JwtOptions {
    pub fn sane_defaults() -> Self {
        Self {
            issuer: String::new(),
            audience: String::new(),
            lifetime_seconds: 3600,
            clock_skew_seconds: 30,
            require_expiry: true,
        }
    }

    /// A token with NO expiry is rejected when `require_expiry` is on, which is
    /// the default: a token that never expires is one that cannot be withdrawn.
    pub fn is_valid_window(&self, expires_at: Option<u64>, now: u64) -> bool {
        match expires_at {
            Some(expiry) => now <= expiry.saturating_add(self.clock_skew_seconds),
            None => !self.require_expiry,
        }
    }
}

/// What an endpoint answered.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct HttpReply {
    pub status: u16,
    pub body: String,
    pub content_type: String,
}

impl HttpReply {
    pub fn ok(body: &str) -> Self {
        Self {
            status: 200,
            body: body.to_string(),
            content_type: "application/json".into(),
        }
    }

    /// The message NEVER names a key, a token or a path outside the request.
    /// An error body is the easiest place to leak what the server knows.
    pub fn error(status: u16, message: &str) -> Self {
        Self {
            status,
            body: format!("{{\"error\":{:?}}}", message),
            content_type: "application/json".into(),
        }
    }

    pub fn unauthorized() -> Self {
        Self::error(401, "this request is not authorised")
    }
}

/// Chat completions.
///
/// The OpenAI-shaped endpoint, so anything that speaks to OpenAI speaks to this
/// server - which is the entire reason for the shape.
pub struct ChatCompletionsEndpoint {
    auth: AuthOptions,
    #[allow(clippy::type_complexity)]
    complete: Option<Box<dyn Fn(&str) -> Result<String, String> + Send + Sync>>,
}

impl ChatCompletionsEndpoint {
    pub const PATH: &'static str = "/v1/chat/completions";

    #[allow(clippy::type_complexity)]
    pub fn new(
        auth: AuthOptions,
        complete: Option<Box<dyn Fn(&str) -> Result<String, String> + Send + Sync>>,
    ) -> Self {
        Self { auth, complete }
    }

    pub fn handle(&self, key: &str, from_loopback: bool, body: &str) -> HttpReply {
        if !self.auth.accepts(key, from_loopback) {
            return HttpReply::unauthorized();
        }
        let Some(complete) = &self.complete else {
            return HttpReply::error(503, "no model is loaded on this device");
        };
        match complete(body) {
            Ok(text) => HttpReply::ok(&text),
            Err(error) => HttpReply::error(500, &error),
        }
    }
}

/// Embeddings.
pub struct EmbeddingsEndpoint {
    auth: AuthOptions,
    #[allow(clippy::type_complexity)]
    embed: Option<Box<dyn Fn(&[String]) -> Result<Vec<Vec<f32>>, String> + Send + Sync>>,
}

impl EmbeddingsEndpoint {
    pub const PATH: &'static str = "/v1/embeddings";
    /// Batches are CAPPED. An unbounded batch is a way to make a phone allocate
    /// until it is killed, from one request.
    pub const MAX_BATCH: usize = 64;

    #[allow(clippy::type_complexity)]
    pub fn new(
        auth: AuthOptions,
        embed: Option<Box<dyn Fn(&[String]) -> Result<Vec<Vec<f32>>, String> + Send + Sync>>,
    ) -> Self {
        Self { auth, embed }
    }

    pub fn handle(&self, key: &str, from_loopback: bool, inputs: &[String]) -> HttpReply {
        if !self.auth.accepts(key, from_loopback) {
            return HttpReply::unauthorized();
        }
        if inputs.len() > Self::MAX_BATCH {
            return HttpReply::error(
                413,
                &format!("at most {} inputs per request", Self::MAX_BATCH),
            );
        }
        let Some(embed) = &self.embed else {
            return HttpReply::error(503, "no embedding model is loaded on this device");
        };
        match embed(inputs) {
            Ok(vectors) => HttpReply::ok(&format!("{{\"count\":{}}}", vectors.len())),
            Err(error) => HttpReply::error(500, &error),
        }
    }
}

/// Health and counters.
pub struct DiagnosticsEndpoint {
    #[allow(clippy::type_complexity)]
    snapshot: Option<Box<dyn Fn() -> String + Send + Sync>>,
}

impl DiagnosticsEndpoint {
    pub const PATH: &'static str = "/diagnostics";

    #[allow(clippy::type_complexity)]
    pub fn new(snapshot: Option<Box<dyn Fn() -> String + Send + Sync>>) -> Self {
        Self { snapshot }
    }

    /// UNAUTHENTICATED, and it carries only counters - no model names, no paths,
    /// no keys. A health check that needs a key cannot be used by the thing that
    /// restarts the server, and one that leaks configuration is reconnaissance.
    pub fn handle(&self) -> HttpReply {
        match &self.snapshot {
            Some(snapshot) => HttpReply::ok(&snapshot()),
            None => HttpReply::ok("{\"status\":\"ok\"}"),
        }
    }
}

/// Loading and unloading models.
pub struct AdminEndpoints {
    auth: AuthOptions,
    #[allow(clippy::type_complexity)]
    load: Option<Box<dyn Fn(&str) -> Result<(), String> + Send + Sync>>,
    #[allow(clippy::type_complexity)]
    unload: Option<Box<dyn Fn(&str) -> Result<(), String> + Send + Sync>>,
}

impl AdminEndpoints {
    pub const LOAD_PATH: &'static str = "/admin/load";
    pub const UNLOAD_PATH: &'static str = "/admin/unload";

    #[allow(clippy::type_complexity)]
    pub fn new(
        auth: AuthOptions,
        load: Option<Box<dyn Fn(&str) -> Result<(), String> + Send + Sync>>,
        unload: Option<Box<dyn Fn(&str) -> Result<(), String> + Send + Sync>>,
    ) -> Self {
        Self { auth, load, unload }
    }

    /// NEVER reachable without a key, even on loopback.
    ///
    /// Loading a model is the one thing here that changes what the server is,
    /// and the loopback exemption that is reasonable for inference is not
    /// reasonable for this - anything running on the device could otherwise
    /// swap the model out.
    pub fn handle_load(&self, key: &str, model_id: &str) -> HttpReply {
        if !self.auth.accepts(key, false) {
            return HttpReply::unauthorized();
        }
        let Some(load) = &self.load else {
            return HttpReply::error(503, "this server cannot load models");
        };
        match load(model_id) {
            Ok(()) => HttpReply::ok("{\"loaded\":true}"),
            Err(error) => HttpReply::error(500, &error),
        }
    }

    pub fn handle_unload(&self, key: &str, model_id: &str) -> HttpReply {
        if !self.auth.accepts(key, false) {
            return HttpReply::unauthorized();
        }
        let Some(unload) = &self.unload else {
            return HttpReply::error(503, "this server cannot unload models");
        };
        match unload(model_id) {
            Ok(()) => HttpReply::ok("{\"unloaded\":true}"),
            Err(error) => HttpReply::error(500, &error),
        }
    }
}

/// The companion surface.
pub struct CompanionEndpoint {
    auth: AuthOptions,
    #[allow(clippy::type_complexity)]
    respond: Option<Box<dyn Fn(&str) -> Result<String, String> + Send + Sync>>,
    consent: ConsentGuard,
}

impl CompanionEndpoint {
    pub const PATH: &'static str = "/companion";

    #[allow(clippy::type_complexity)]
    pub fn new(
        auth: AuthOptions,
        respond: Option<Box<dyn Fn(&str) -> Result<String, String> + Send + Sync>>,
    ) -> Self {
        Self { auth, respond, consent: ConsentGuard::new() }
    }

    pub fn consent_mut(&mut self) -> &mut ConsentGuard {
        &mut self.consent
    }

    /// Anything touching personal data goes through the guard FIRST.
    ///
    /// The check happens before the model sees the request, not after: a model
    /// that has already read a calendar cannot un-read it because a permission
    /// check failed afterwards.
    pub fn handle(
        &mut self,
        key: &str,
        from_loopback: bool,
        caller: &str,
        scopes: &[ConsentScope],
        body: &str,
        now_ms: u64,
    ) -> HttpReply {
        if !self.auth.accepts(key, from_loopback) {
            return HttpReply::unauthorized();
        }
        for scope in scopes {
            if let Err(refusal) = self.consent.check(caller, *scope, now_ms) {
                return HttpReply::error(403, &refusal.message());
            }
        }
        let Some(respond) = &self.respond else {
            return HttpReply::error(503, "no companion model is loaded on this device");
        };
        match respond(body) {
            Ok(text) => HttpReply::ok(&text),
            Err(error) => HttpReply::error(500, &error),
        }
    }
}

/// The entry point.
///
/// A NAMED TYPE rather than a `main`, because this port is a library: the head
/// that starts a server is the platform's, and a `main` here would be dead code
/// on every target that embeds this.
pub struct Program;

impl Program {
    /// What a host wires up, in order.
    pub fn endpoints() -> Vec<(&'static str, &'static str)> {
        vec![
            ("POST", ChatCompletionsEndpoint::PATH),
            ("POST", EmbeddingsEndpoint::PATH),
            ("POST", CompanionEndpoint::PATH),
            ("GET", DiagnosticsEndpoint::PATH),
            ("POST", AdminEndpoints::LOAD_PATH),
            ("POST", AdminEndpoints::UNLOAD_PATH),
        ]
    }

    /// Refuses to start unbound-and-unauthenticated.
    ///
    /// A server listening on every interface with no key is an open inference
    /// endpoint on whatever network the phone joins next. Loopback with no key
    /// is fine; the combination is not.
    pub fn preflight(bind_address: &str, auth: &AuthOptions) -> Result<(), String> {
        let loopback = bind_address.starts_with("127.")
            || bind_address == "localhost"
            || bind_address == "::1";
        if !loopback && !auth.require_auth {
            return Err(format!(
                "refusing to listen on {bind_address} with no authentication - \
                 bind to loopback or set a key"
            ));
        }
        Ok(())
    }
}
