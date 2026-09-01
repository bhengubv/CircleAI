//! Built-in protection: noticing threats, and the gate in front of doing
//! anything about them.
//!
//! THE WHOLE MODULE IS DEFENSIVE AND SAYS SO IN ITS SHAPE. Everything in the
//! awareness half ASSESSES and returns a verdict; nothing in it acts. The only
//! path to an action goes through a gate that requires a named person to have
//! consented, in scope and unexpired - and every failure mode of that gate
//! denies.
//!
//! THE DEFAULTS ARE THE DESIGN:
//!
//!   * A null gate DENIES. A build that forgot to configure one can assess and
//!     cannot act. A permissive null gate is how a protective feature becomes an
//!     offensive one by omission.
//!
//!   * A null escalation returns `false`, so nothing believes an alert was
//!     raised when none was. Returning true would be worse than doing nothing,
//!     because something downstream would stop trying.
//!
//!   * A consent with a blank granter is REFUSED at construction. A consent
//!     nobody can be shown to have given is not a consent.
//!
//! AND THE ONE THAT MATTERS MOST: an indicator match is EVIDENCE, not a verdict.
//! A file that matches a hash and a connection to a listed address are both
//! reasons to look, and neither is a reason to act on its own.

use std::collections::{HashMap, HashSet};

// ─────────────────────────────────────────────────────────────────────────────
// What can be noticed

/// What kind of thing an indicator describes.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum IndicatorKind {
    FileHash,
    /// A domain name. Matched on the REGISTRABLE part, so subdomains match.
    Domain,
    Ipv4,
    Ipv4Cidr,
    Url,
    EmailAddress,
    /// A phone number, in E.164. The commonest vector here by a wide margin.
    PhoneNumber,
}

/// Normalises an indicator so lookups cannot miss on spelling.
///
/// Case, a trailing dot on a domain, a `+` on a phone number - each of these
/// makes two spellings of the same thing, and a corpus that stores one and is
/// asked the other reports clean.
pub fn normalise_indicator(kind: IndicatorKind, value: &str) -> String {
    let raw = value.trim();
    match kind {
        IndicatorKind::FileHash => {
            let lower = raw.to_lowercase();
            for prefix in ["sha256:", "sha1:", "md5:"] {
                if let Some(rest) = lower.strip_prefix(prefix) {
                    return rest.to_string();
                }
            }
            lower
        }
        IndicatorKind::Domain => raw
            .to_lowercase()
            .trim_end_matches('.')
            .trim_start_matches("www.")
            .to_string(),
        IndicatorKind::EmailAddress => raw.to_lowercase(),
        // Digits only, with a leading + implied. A number written with spaces,
        // dashes or brackets is the same number.
        IndicatorKind::PhoneNumber => raw.chars().filter(char::is_ascii_digit).collect(),
        IndicatorKind::Url => raw.trim_end_matches('/').to_string(),
        _ => raw.to_string(),
    }
}

/// One thing worth noticing.
#[derive(Debug, Clone, PartialEq)]
pub struct ThreatIndicator {
    pub kind: IndicatorKind,
    /// Normalised at construction, so a lookup cannot miss on spelling.
    pub value: String,
    pub source: String,
    /// 0..1. How much this SOURCE is trusted, not how bad the thing is.
    pub confidence: f32,
    pub note: String,
}

impl ThreatIndicator {
    pub fn new(kind: IndicatorKind, value: &str, source: &str, confidence: f32) -> Self {
        Self {
            kind,
            value: normalise_indicator(kind, value),
            source: source.to_string(),
            confidence: confidence.clamp(0.0, 1.0),
            note: String::new(),
        }
    }
}

/// An indicator about a network endpoint.
#[derive(Debug, Clone, PartialEq)]
pub struct NetworkIndicator {
    pub indicator: ThreatIndicator,
    pub port: u16,
}

/// An indicator about a person's identity being exposed.
#[derive(Debug, Clone, PartialEq)]
pub struct IdentityIndicator {
    pub indicator: ThreatIndicator,
    /// Which breach, in the corpus's own words. Never inferred.
    pub breach_name: String,
    pub breach_date_iso: String,
}

/// Something that was matched.
#[derive(Debug, Clone, PartialEq)]
pub struct IndicatorMatch {
    pub indicator: ThreatIndicator,
    /// What was being checked when it matched.
    pub observed: String,
    /// Carried from the indicator's source, so a low-trust match reads as one.
    pub confidence: f32,
}

/// A file being looked at.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct FileArtifact {
    pub path: String,
    pub sha256: String,
    pub size_bytes: u64,
    /// From the CONTENT where a host can determine it, not the extension - an
    /// extension is what somebody chose to call the file.
    pub detected_type: String,
}

/// How sure the assessment is.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum ThreatAwarenessVerdict {
    /// Nothing matched. NOT the same as safe, and worded so nobody reads it that
    /// way: absence of evidence is what this is.
    #[default]
    NothingKnown,
    /// Something matched, weakly or from a low-trust source. Worth a look.
    WorthChecking,
    /// A strong match from a trusted source.
    Concerning,
    /// The corpus could not be consulted, so nothing was actually checked. NOT
    /// clean - the difference is the whole point of having this value.
    CouldNotCheck,
}

/// What an assessment found.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct ThreatAwarenessResult {
    pub verdict: ThreatAwarenessVerdict,
    pub matches: Vec<IndicatorMatch>,
    /// Written for a PERSON, not a log. This is shown on a screen.
    pub explanation: String,
    /// What they can do. Empty when there is nothing - itself worth saying.
    pub suggestion: String,
}

// ─────────────────────────────────────────────────────────────────────────────
// The corpus

/// The indicators this device knows about.
///
/// LOCAL. There is no lookup service here and there will not be one: asking a
/// server whether a file is malicious tells that server what files somebody has,
/// and asking about a phone number tells it who they are talking to.
pub trait LocalIndicatorCorpus {
    fn is_loaded(&self) -> bool;
    fn len(&self) -> usize;
    fn is_empty(&self) -> bool {
        self.len() == 0
    }
    fn lookup(&self, kind: IndicatorKind, value: &str) -> Option<ThreatIndicator>;
}

/// Knows nothing, and reports NOT LOADED.
///
/// The distinction is the entire reason this is not just an empty map: a device
/// with no corpus has not checked anything, and reporting "nothing known" would
/// be a clean bill of health it has no basis for.
#[derive(Debug, Default, Clone, Copy)]
pub struct EmptyIndicatorCorpus;

impl LocalIndicatorCorpus for EmptyIndicatorCorpus {
    fn is_loaded(&self) -> bool {
        false
    }
    fn len(&self) -> usize {
        0
    }
    fn lookup(&self, _kind: IndicatorKind, _value: &str) -> Option<ThreatIndicator> {
        None
    }
}

/// A corpus held in memory.
#[derive(Debug, Default)]
pub struct InMemoryIndicatorCorpus {
    by_kind: HashMap<IndicatorKind, HashMap<String, ThreatIndicator>>,
}

impl InMemoryIndicatorCorpus {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn add(&mut self, indicator: ThreatIndicator) {
        let map = self.by_kind.entry(indicator.kind).or_default();
        // The HIGHER-confidence one wins. A weak source must not overwrite a
        // strong one just by being loaded second.
        match map.get(&indicator.value) {
            Some(existing) if existing.confidence >= indicator.confidence => {}
            _ => {
                map.insert(indicator.value.clone(), indicator);
            }
        }
    }
}

impl LocalIndicatorCorpus for InMemoryIndicatorCorpus {
    fn is_loaded(&self) -> bool {
        true
    }

    fn len(&self) -> usize {
        self.by_kind.values().map(HashMap::len).sum()
    }

    fn lookup(&self, kind: IndicatorKind, value: &str) -> Option<ThreatIndicator> {
        let normalised = normalise_indicator(kind, value);
        if let Some(found) = self.by_kind.get(&kind).and_then(|m| m.get(&normalised)) {
            return Some(found.clone());
        }
        if kind != IndicatorKind::Domain {
            return None;
        }
        // A domain indicator covers its SUBDOMAINS. A listed `example.com`
        // should match `login.example.com`, which is where the interesting ones
        // live.
        let map = self.by_kind.get(&IndicatorKind::Domain)?;
        let parts: Vec<&str> = normalised.split('.').collect();
        for i in 1..parts.len().saturating_sub(1) {
            if let Some(found) = map.get(&parts[i..].join(".")) {
                return Some(found.clone());
            }
        }
        None
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Assessment - which never acts

/// Above this a single match is Concerning. Below, it is Worth checking.
///
/// The threshold is stated once so it cannot drift between the file path and the
/// network path, which is exactly how a system ends up strict about one and
/// permissive about the other.
pub const TRUSTED_CONFIDENCE: f32 = 0.8;

fn settle(
    corpus: &dyn LocalIndicatorCorpus,
    matches: Vec<IndicatorMatch>,
    subject: &str,
) -> ThreatAwarenessResult {
    if !corpus.is_loaded() {
        return ThreatAwarenessResult {
            verdict: ThreatAwarenessVerdict::CouldNotCheck,
            explanation: "this device has no threat list, so nothing was checked".into(),
            suggestion: "connect to Wi-Fi so the list can be downloaded".into(),
            ..Default::default()
        };
    }
    if matches.is_empty() {
        return ThreatAwarenessResult {
            verdict: ThreatAwarenessVerdict::NothingKnown,
            explanation: format!("nothing on this device's list matches {subject}"),
            ..Default::default()
        };
    }
    let best = matches
        .iter()
        .map(|m| m.confidence)
        .fold(0.0f32, f32::max);
    let concerning = best >= TRUSTED_CONFIDENCE || matches.len() > 1;
    ThreatAwarenessResult {
        verdict: if concerning {
            ThreatAwarenessVerdict::Concerning
        } else {
            ThreatAwarenessVerdict::WorthChecking
        },
        matches,
        explanation: if concerning {
            format!("{subject} matches something known to be harmful")
        } else {
            format!("{subject} matches something worth checking")
        },
        suggestion: if concerning {
            "do not open it, and do not enter anything into it".into()
        } else {
            "have a look before you go further".into()
        },
    }
}

/// Assesses a file.
pub trait FileThreatAwareness {
    fn assess(&self, file: &FileArtifact) -> ThreatAwarenessResult;
}

/// Assesses a network endpoint.
pub trait NetworkThreatAwareness {
    fn assess(&self, host: &str, port: u16) -> ThreatAwarenessResult;
}

/// Assesses whether an identity is exposed.
pub trait BreachExposureAwareness {
    fn assess(&self, email_or_phone: &str) -> ThreatAwarenessResult;
}

/// Assesses a file against the corpus.
pub struct FileThreatAwarenessAssessor<C: LocalIndicatorCorpus> {
    corpus: C,
}

impl<C: LocalIndicatorCorpus> FileThreatAwarenessAssessor<C> {
    pub fn new(corpus: C) -> Self {
        Self { corpus }
    }
}

impl<C: LocalIndicatorCorpus> FileThreatAwareness for FileThreatAwarenessAssessor<C> {
    fn assess(&self, file: &FileArtifact) -> ThreatAwarenessResult {
        let mut matches = Vec::new();
        if !file.sha256.is_empty() {
            if let Some(found) = self.corpus.lookup(IndicatorKind::FileHash, &file.sha256) {
                matches.push(IndicatorMatch {
                    observed: file.sha256.clone(),
                    confidence: found.confidence,
                    indicator: found,
                });
            }
        }
        // The NAME is not matched against anything. A file called
        // `invoice.pdf.exe` is suspicious to a person, and matching on names
        // produces false positives that teach people to ignore warnings.
        settle(&self.corpus, matches, "that file")
    }
}

/// Assesses a network endpoint against the corpus.
pub struct NetworkThreatAwarenessAssessor<C: LocalIndicatorCorpus> {
    corpus: C,
}

impl<C: LocalIndicatorCorpus> NetworkThreatAwarenessAssessor<C> {
    pub fn new(corpus: C) -> Self {
        Self { corpus }
    }
}

impl<C: LocalIndicatorCorpus> NetworkThreatAwareness for NetworkThreatAwarenessAssessor<C> {
    fn assess(&self, host: &str, _port: u16) -> ThreatAwarenessResult {
        let kind = if Ipv4Cidr::is_ipv4(host) {
            IndicatorKind::Ipv4
        } else {
            IndicatorKind::Domain
        };
        let matches = self
            .corpus
            .lookup(kind, host)
            .map(|found| {
                vec![IndicatorMatch {
                    observed: host.to_string(),
                    confidence: found.confidence,
                    indicator: found,
                }]
            })
            .unwrap_or_default();
        settle(&self.corpus, matches, if host.is_empty() { "that connection" } else { host })
    }
}

/// Assesses whether an identity appears in a known breach.
pub struct BreachExposureAssessor<C: LocalIndicatorCorpus> {
    corpus: C,
}

impl<C: LocalIndicatorCorpus> BreachExposureAssessor<C> {
    pub fn new(corpus: C) -> Self {
        Self { corpus }
    }
}

impl<C: LocalIndicatorCorpus> BreachExposureAwareness for BreachExposureAssessor<C> {
    fn assess(&self, email_or_phone: &str) -> ThreatAwarenessResult {
        let kind = if email_or_phone.contains('@') {
            IndicatorKind::EmailAddress
        } else {
            IndicatorKind::PhoneNumber
        };
        let matches = self
            .corpus
            .lookup(kind, email_or_phone)
            .map(|found| {
                vec![IndicatorMatch {
                    observed: email_or_phone.to_string(),
                    confidence: found.confidence,
                    indicator: found,
                }]
            })
            .unwrap_or_default();
        let empty = matches.is_empty();
        let mut result = settle(&self.corpus, matches, "that address");
        if !empty {
            // The suggestion for a breach is DIFFERENT from the generic one,
            // because the action is different: change a password, not avoid a
            // file.
            result.suggestion = "change that password anywhere you have reused it".into();
        }
        result
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// The gate

/// What a defensive action can do.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum AntibodyCapability {
    /// Look at a file or a connection. Read-only, and the least of them.
    Inspect,
    /// Stop a connection. Affects this device only.
    BlockConnection,
    /// Move a file somewhere it will not run. Reversible on purpose.
    QuarantineFile,
    /// Tell a person something is happening.
    NotifyOwner,
    /// Raise an alarm beyond this device. The only one that reaches anybody
    /// else, and the one that needs the most consent.
    EscalateSos,
}

/// How bad something is.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Default)]
pub enum ThreatSeverity {
    #[default]
    Informational = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    Critical = 4,
}

/// What is happening, for the gate to weigh.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct DefensiveThreatContext {
    pub severity: ThreatSeverity,
    pub summary: String,
    pub matches: Vec<IndicatorMatch>,
    pub at_ms: u64,
}

/// A request to do something about a threat.
#[derive(Debug, Clone, PartialEq)]
pub struct AuthorizedUseRequest {
    pub capability: AntibodyCapability,
    pub context: DefensiveThreatContext,
    /// Which device this affects. Never another device: nothing here reaches off
    /// this machine except an escalation to its owner.
    pub device_id: String,
    pub reason: String,
}

/// Somebody's agreement to a capability, for a while.
///
/// NO OPEN-ENDED CONSENT IS CONSTRUCTIBLE. `new` returns `None` when the expiry
/// is not after the grant, or when nobody is named - so "forever" is something a
/// caller has to write out rather than get by leaving a field alone.
#[derive(Debug, Clone, PartialEq)]
pub struct AuthorizedUseConsent {
    capabilities: HashSet<AntibodyCapability>,
    pub expires_at_ms: u64,
    pub granted_at_ms: u64,
    /// Who agreed. Blank is REFUSED: a consent nobody can be shown to have given
    /// is not a consent.
    pub granted_by: String,
    pub purpose: String,
}

impl AuthorizedUseConsent {
    pub fn new(
        capabilities: &[AntibodyCapability],
        expires_at_ms: u64,
        granted_at_ms: u64,
        granted_by: &str,
        purpose: &str,
    ) -> Option<Self> {
        if capabilities.is_empty() || granted_by.trim().is_empty() || expires_at_ms <= granted_at_ms
        {
            return None;
        }
        Some(Self {
            capabilities: capabilities.iter().copied().collect(),
            expires_at_ms,
            granted_at_ms,
            granted_by: granted_by.to_string(),
            purpose: purpose.to_string(),
        })
    }

    pub fn is_valid_at(&self, now_ms: u64) -> bool {
        now_ms >= self.granted_at_ms && now_ms < self.expires_at_ms
    }

    pub fn covers(&self, capability: AntibodyCapability) -> bool {
        self.capabilities.contains(&capability)
    }

    pub fn capabilities(&self) -> &HashSet<AntibodyCapability> {
        &self.capabilities
    }
}

/// Whether an action may proceed.
#[derive(Debug, Clone, PartialEq)]
pub struct AuthorizationDecision {
    pub allowed: bool,
    /// ALWAYS populated, including on allow. A decision without a reason is a
    /// decision nobody can review, and these decisions act on somebody's device.
    pub reason: String,
    pub capability: AntibodyCapability,
}

/// Holds consents.
pub trait AuthorizedUseConsentStore {
    fn grant(&mut self, consent: AuthorizedUseConsent);
    fn revoke(&mut self, capability: AntibodyCapability);
    fn active(&self, now_ms: u64) -> Vec<AuthorizedUseConsent>;
    fn is_revoked(&self, capability: AntibodyCapability) -> bool;
}

/// The default store.
#[derive(Debug, Default)]
pub struct InMemoryAuthorizedUseConsentStore {
    consents: Vec<AuthorizedUseConsent>,
    revoked: HashSet<AntibodyCapability>,
}

impl InMemoryAuthorizedUseConsentStore {
    pub fn new() -> Self {
        Self::default()
    }
}

impl AuthorizedUseConsentStore for InMemoryAuthorizedUseConsentStore {
    fn grant(&mut self, consent: AuthorizedUseConsent) {
        // Granting CLEARS a previous revocation for those capabilities. Somebody
        // who revokes and then agrees again means the second thing.
        for capability in consent.capabilities() {
            self.revoked.remove(capability);
        }
        self.consents.push(consent);
    }

    /// Revocation is by CAPABILITY, not by consent.
    ///
    /// Revoking one consent would leave any other consent carrying the same
    /// capability working, and somebody who says "stop doing that" means all of
    /// it.
    fn revoke(&mut self, capability: AntibodyCapability) {
        self.revoked.insert(capability);
    }

    fn active(&self, now_ms: u64) -> Vec<AuthorizedUseConsent> {
        self.consents
            .iter()
            .filter(|c| c.is_valid_at(now_ms))
            .cloned()
            .collect()
    }

    fn is_revoked(&self, capability: AntibodyCapability) -> bool {
        self.revoked.contains(&capability)
    }
}

/// Decides whether a defensive action may proceed.
pub trait AuthorizedUseGate {
    fn authorize(&self, request: &AuthorizedUseRequest) -> AuthorizationDecision;
}

/// Denies everything.
///
/// THE DEFAULT, and the most important type in this file. A build that forgot to
/// configure a gate can assess and cannot act. A permissive null gate is how a
/// protective feature becomes an offensive one by omission.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullAuthorizedUseGate;

impl AuthorizedUseGate for NullAuthorizedUseGate {
    fn authorize(&self, request: &AuthorizedUseRequest) -> AuthorizationDecision {
        AuthorizationDecision {
            allowed: false,
            reason: "no authorisation gate is configured, so nothing will be acted on".into(),
            capability: request.capability,
        }
    }
}

/// Allows only what somebody has explicitly consented to.
///
/// FAILS CLOSED at every branch: no consent, wrong capability, expired, revoked,
/// or a clock that will not answer. The one thing it must never do is allow
/// something because it could not work out whether to refuse.
pub struct ExplicitConsentAuthorizedUseGate<S: AuthorizedUseConsentStore> {
    store: S,
    now: Option<Box<dyn Fn() -> Option<u64> + Send + Sync>>,
}

impl<S: AuthorizedUseConsentStore> ExplicitConsentAuthorizedUseGate<S> {
    /// Escalation needs BOTH consent and a severity that warrants it. A consent
    /// to escalate is not a consent to escalate about anything - it is the only
    /// capability that reaches another person, and a low-severity alarm at 3am
    /// teaches somebody to ignore the next one.
    pub const ESCALATION_FLOOR: ThreatSeverity = ThreatSeverity::High;

    pub fn new(store: S, now: Option<Box<dyn Fn() -> Option<u64> + Send + Sync>>) -> Self {
        Self { store, now }
    }

    pub fn store_mut(&mut self) -> &mut S {
        &mut self.store
    }
}

impl<S: AuthorizedUseConsentStore> AuthorizedUseGate for ExplicitConsentAuthorizedUseGate<S> {
    fn authorize(&self, request: &AuthorizedUseRequest) -> AuthorizationDecision {
        let capability = request.capability;
        let deny = |reason: String| AuthorizationDecision { allowed: false, reason, capability };

        if self.store.is_revoked(capability) {
            // Checked FIRST. Revocation beats a consent that is otherwise
            // perfectly valid.
            return deny(format!("you turned off {capability:?}"));
        }

        // A clock that will not answer means no. Assuming a time here would let
        // a broken clock become an open door.
        let Some(now_ms) = self.now.as_ref().and_then(|f| f()) else {
            return deny("this device cannot tell the time, so it will not act".into());
        };

        let live: Vec<AuthorizedUseConsent> = self
            .store
            .active(now_ms)
            .into_iter()
            .filter(|c| c.covers(capability))
            .collect();
        if live.is_empty() {
            return deny(format!("nobody has agreed to {capability:?} on this device"));
        }
        if capability == AntibodyCapability::EscalateSos
            && request.context.severity < Self::ESCALATION_FLOOR
        {
            return deny("this is not serious enough to raise an alarm with anybody".into());
        }
        if request.device_id.trim().is_empty() {
            // An action with no device named is an action with no scope, and the
            // whole point of the scope is that it is this device.
            return deny("no device was named".into());
        }
        AuthorizationDecision {
            allowed: true,
            reason: format!("{} agreed to {capability:?}", live[0].granted_by),
            capability,
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Blocklists

/// An IPv4 range, and matching against it.
///
/// Rust's integer types make the signed-shift trap that catches every other
/// language a non-issue here - `u32` is `u32` - but the /0 case still needs
/// care: shifting a `u32` by 32 is undefined and panics in a debug build.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Ipv4Cidr {
    pub network: u32,
    pub mask: u32,
    pub bits: u8,
}

impl Ipv4Cidr {
    pub fn new(prefix: &str, bits: u8) -> Option<Self> {
        if bits > 32 {
            return None;
        }
        // A /0 mask must be 0, and `!0u32 << 32` panics rather than producing
        // it. Special-cased rather than discovered by a /0 that matches nothing.
        let mask = if bits == 0 { 0 } else { !0u32 << (32 - bits) };
        Some(Self {
            network: Self::to_number(prefix)? & mask,
            mask,
            bits,
        })
    }

    pub fn is_ipv4(text: &str) -> bool {
        let parts: Vec<&str> = text.split('.').collect();
        parts.len() == 4
            && parts
                .iter()
                .all(|p| !p.is_empty() && p.len() <= 3 && p.parse::<u8>().is_ok())
    }

    pub fn to_number(address: &str) -> Option<u32> {
        let parts: Vec<u8> = address
            .split('.')
            .map(str::parse::<u8>)
            .collect::<Result<_, _>>()
            .ok()?;
        if parts.len() != 4 {
            return None;
        }
        Some(u32::from_be_bytes([parts[0], parts[1], parts[2], parts[3]]))
    }

    pub fn parse(text: &str) -> Option<Self> {
        let trimmed = text.trim();
        let (prefix, bits) = match trimmed.split_once('/') {
            // A bare address is a /32 - one host. Defaulting to /0 would make a
            // single listed address match the entire internet.
            None => (trimmed, 32u8),
            Some((p, b)) => (p, b.parse().ok()?),
        };
        if !Self::is_ipv4(prefix) {
            return None;
        }
        Self::new(prefix, bits)
    }

    pub fn contains(&self, address: &str) -> bool {
        match Self::to_number(address) {
            Some(n) => n & self.mask == self.network,
            None => false,
        }
    }
}

impl std::fmt::Display for Ipv4Cidr {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        let b = self.network.to_be_bytes();
        write!(f, "{}.{}.{}.{}/{}", b[0], b[1], b[2], b[3], self.bits)
    }
}

/// One line of a blocklist, parsed.
#[derive(Debug, Clone, PartialEq)]
pub struct ParsedIndicator {
    pub kind: IndicatorKind,
    pub value: String,
    pub comment: String,
}

/// Reads the blocklist formats that actually exist.
///
/// Hosts files, plain lists, and lists with comments. A parser that only handles
/// one silently reads a hosts file's `0.0.0.0` column as the indicator and
/// blocks nothing except the address 0.0.0.0.
pub struct BlocklistParser;

impl BlocklistParser {
    pub fn parse_line(line: &str) -> Option<ParsedIndicator> {
        let (body, comment) = match line.split_once('#') {
            Some((b, c)) => (b.trim(), c.trim().to_string()),
            None => (line.trim(), String::new()),
        };
        if body.is_empty() {
            return None;
        }
        let fields: Vec<&str> = body.split_whitespace().collect();
        // A hosts-file line is `0.0.0.0 bad.example`. The indicator is the
        // SECOND field; reading the first blocks the sinkhole address instead of
        // the site.
        let value = if fields.len() >= 2 && matches!(fields[0], "0.0.0.0" | "127.0.0.1") {
            fields[1]
        } else {
            fields[0]
        };
        if value.is_empty() || value == "localhost" {
            return None;
        }
        let kind = if value.contains('/') && Ipv4Cidr::parse(value).is_some() {
            IndicatorKind::Ipv4Cidr
        } else if Ipv4Cidr::is_ipv4(value) {
            IndicatorKind::Ipv4
        } else if value.starts_with("http://") || value.starts_with("https://") {
            IndicatorKind::Url
        } else if value.contains('@') {
            IndicatorKind::EmailAddress
        } else if value.len() >= 32
            && value.len() <= 64
            && value.chars().all(|c| c.is_ascii_hexdigit())
        {
            IndicatorKind::FileHash
        } else if value.contains('.') {
            IndicatorKind::Domain
        } else {
            return None;
        };
        Some(ParsedIndicator { kind, value: value.to_string(), comment })
    }

    pub fn parse(text: &str) -> Vec<ParsedIndicator> {
        text.lines().filter_map(Self::parse_line).collect()
    }
}

/// Where indicators come from.
pub trait IndicatorSource {
    fn name(&self) -> &str;
    fn is_loaded(&self) -> bool;
    fn matches(&self, kind: IndicatorKind, value: &str) -> Option<IndicatorMatch>;
}

/// A source backed by a parsed blocklist.
pub struct BlocklistIndicatorSource {
    name: String,
    /// How much this list is trusted. A community list is not a vendor feed and
    /// should not produce the same verdict.
    confidence: f32,
    corpus: InMemoryIndicatorCorpus,
    ranges: Vec<Ipv4Cidr>,
    loaded: bool,
}

impl BlocklistIndicatorSource {
    pub fn new(name: &str, confidence: f32) -> Self {
        Self {
            name: name.to_string(),
            confidence: confidence.clamp(0.0, 1.0),
            corpus: InMemoryIndicatorCorpus::new(),
            ranges: Vec::new(),
            loaded: false,
        }
    }

    pub fn load(&mut self, text: &str) -> usize {
        let mut count = 0usize;
        for parsed in BlocklistParser::parse(text) {
            if parsed.kind == IndicatorKind::Ipv4Cidr {
                if let Some(range) = Ipv4Cidr::parse(&parsed.value) {
                    self.ranges.push(range);
                    count += 1;
                }
                continue;
            }
            let mut indicator =
                ThreatIndicator::new(parsed.kind, &parsed.value, &self.name, self.confidence);
            indicator.note = parsed.comment;
            self.corpus.add(indicator);
            count += 1;
        }
        self.loaded = true;
        count
    }
}

impl IndicatorSource for BlocklistIndicatorSource {
    fn name(&self) -> &str {
        &self.name
    }

    fn is_loaded(&self) -> bool {
        self.loaded
    }

    fn matches(&self, kind: IndicatorKind, value: &str) -> Option<IndicatorMatch> {
        if let Some(direct) = self.corpus.lookup(kind, value) {
            return Some(IndicatorMatch {
                observed: value.to_string(),
                confidence: direct.confidence,
                indicator: direct,
            });
        }
        if kind != IndicatorKind::Ipv4 {
            return None;
        }
        // CIDR ranges are checked after exact addresses, because an exact entry
        // usually carries a better comment than the range that also covers it.
        let range = self.ranges.iter().find(|r| r.contains(value))?;
        Some(IndicatorMatch {
            indicator: ThreatIndicator::new(
                IndicatorKind::Ipv4Cidr,
                &range.to_string(),
                &self.name,
                self.confidence,
            ),
            observed: value.to_string(),
            confidence: self.confidence,
        })
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Watching

/// Which way traffic was going.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ThreatDirection {
    /// Something reached in. Usually the more serious of the two.
    Inbound,
    /// This device reached out - which, for malware, is the interesting one.
    Outbound,
}

/// One connection this device made or received.
#[derive(Debug, Clone, PartialEq)]
pub struct NetworkObservation {
    pub host: String,
    pub port: u16,
    pub direction: ThreatDirection,
    pub at_ms: u64,
    /// Which app, where a host can tell. Empty rather than guessed.
    pub process_name: String,
}

/// Where observations come from.
pub trait NetworkObservationFeed {
    fn is_available(&self) -> bool;
    fn drain(&mut self) -> Vec<NetworkObservation>;
}

/// What kind of threat.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum ThreatCategory {
    #[default]
    Network,
    File,
    Identity,
    /// Somebody being manipulated rather than something being exploited. The
    /// commonest category by a wide margin, and the one no scanner catches.
    SocialEngineering,
}

/// Something worth telling somebody about.
#[derive(Debug, Clone, PartialEq, Default)]
pub struct ThreatSignal {
    pub category: ThreatCategory,
    pub severity: ThreatSeverity,
    pub summary: String,
    pub matches: Vec<IndicatorMatch>,
    pub at_ms: u64,
}

/// Somewhere a signal goes.
pub trait ThreatSink {
    fn accept(&mut self, signal: &ThreatSignal);
}

/// Accepts and discards. The default: noticing is not reporting.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullThreatSink;

impl ThreatSink for NullThreatSink {
    fn accept(&mut self, _signal: &ThreatSignal) {}
}

/// Wraps a closure as a sink.
pub struct DelegateThreatSink {
    handler: Box<dyn FnMut(&ThreatSignal) + Send + Sync>,
}

impl DelegateThreatSink {
    pub fn new(handler: Box<dyn FnMut(&ThreatSignal) + Send + Sync>) -> Self {
        Self { handler }
    }
}

impl ThreatSink for DelegateThreatSink {
    fn accept(&mut self, signal: &ThreatSignal) {
        (self.handler)(signal);
    }
}

/// Sends a signal to several sinks.
///
/// Every sink is called even if an earlier one did nothing useful - one sink
/// writing to a full disk should not prevent the one that shows a person a
/// warning.
pub struct CompositeThreatSink {
    sinks: Vec<Box<dyn ThreatSink + Send + Sync>>,
}

impl CompositeThreatSink {
    pub fn new(sinks: Vec<Box<dyn ThreatSink + Send + Sync>>) -> Self {
        Self { sinks }
    }
}

impl ThreatSink for CompositeThreatSink {
    fn accept(&mut self, signal: &ThreatSignal) {
        for sink in self.sinks.iter_mut() {
            sink.accept(signal);
        }
    }
}

/// Watches for threats.
pub trait ThreatMonitor {
    fn is_running(&self) -> bool;
    fn start(&mut self);
    fn stop(&mut self);
    /// Examines whatever has arrived and returns what it found.
    fn poll(&mut self) -> Vec<ThreatSignal>;
}

/// Watches network observations against blocklists.
pub struct BlocklistThreatMonitor<F: NetworkObservationFeed> {
    feed: F,
    sources: Vec<Box<dyn IndicatorSource + Send + Sync>>,
    running: bool,
}

impl<F: NetworkObservationFeed> BlocklistThreatMonitor<F> {
    pub fn new(feed: F, sources: Vec<Box<dyn IndicatorSource + Send + Sync>>) -> Self {
        Self { feed, sources, running: false }
    }

    fn consider(&self, observation: &NetworkObservation) -> Option<ThreatSignal> {
        let kind = if Ipv4Cidr::is_ipv4(&observation.host) {
            IndicatorKind::Ipv4
        } else {
            IndicatorKind::Domain
        };
        let matches: Vec<IndicatorMatch> = self
            .sources
            .iter()
            .filter_map(|s| s.matches(kind, &observation.host))
            .collect();
        if matches.is_empty() {
            return None;
        }
        let best = matches.iter().map(|m| m.confidence).fold(0.0f32, f32::max);
        // An OUTBOUND connection to a listed host is treated more seriously than
        // an inbound one: inbound is the internet knocking, which happens
        // constantly; outbound means something on this device chose to go there.
        let severity = match (observation.direction, best >= TRUSTED_CONFIDENCE) {
            (ThreatDirection::Outbound, true) => ThreatSeverity::High,
            (_, true) => ThreatSeverity::Medium,
            _ => ThreatSeverity::Low,
        };
        Some(ThreatSignal {
            category: ThreatCategory::Network,
            severity,
            summary: match observation.direction {
                ThreatDirection::Outbound => {
                    format!("something on this device connected to {}", observation.host)
                }
                ThreatDirection::Inbound => {
                    format!("{} connected to this device", observation.host)
                }
            },
            matches,
            at_ms: observation.at_ms,
        })
    }
}

impl<F: NetworkObservationFeed> ThreatMonitor for BlocklistThreatMonitor<F> {
    fn is_running(&self) -> bool {
        self.running
    }

    fn start(&mut self) {
        if self.feed.is_available() {
            self.running = true;
        }
    }

    fn stop(&mut self) {
        self.running = false;
    }

    fn poll(&mut self) -> Vec<ThreatSignal> {
        if !self.running {
            return Vec::new();
        }
        let observations = self.feed.drain();
        observations
            .iter()
            .filter_map(|o| self.consider(o))
            .collect()
    }
}

/// Raises an alarm beyond this device.
pub trait SosEscalation {
    fn is_available(&self) -> bool;
    fn escalate(&self, signal: &ThreatSignal) -> bool;
}

/// Escalates nothing, and returns FALSE.
///
/// False rather than true is the whole point: nothing downstream should believe
/// an alert was raised when none was. Returning true would be worse than doing
/// nothing, because something else would stop trying.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullSosEscalation;

impl SosEscalation for NullSosEscalation {
    fn is_available(&self) -> bool {
        false
    }
    fn escalate(&self, _signal: &ThreatSignal) -> bool {
        false
    }
}

/// Wraps a closure as an escalation.
pub struct DelegateSosEscalation {
    handler: Box<dyn Fn(&ThreatSignal) -> bool + Send + Sync>,
}

impl DelegateSosEscalation {
    pub fn new(handler: Box<dyn Fn(&ThreatSignal) -> bool + Send + Sync>) -> Self {
        Self { handler }
    }
}

impl SosEscalation for DelegateSosEscalation {
    fn is_available(&self) -> bool {
        true
    }
    fn escalate(&self, signal: &ThreatSignal) -> bool {
        (self.handler)(signal)
    }
}

/// A sink that escalates, but only through the gate.
///
/// THE GATE IS ASKED EVERY TIME, not once at construction. A consent expires
/// while a device is running, and a sink that cached its answer would keep
/// escalating for hours after somebody's agreement ran out.
pub struct SosThreatSink<E: SosEscalation, G: AuthorizedUseGate> {
    escalation: E,
    gate: G,
    device_id: String,
}

impl<E: SosEscalation, G: AuthorizedUseGate> SosThreatSink<E, G> {
    pub fn new(escalation: E, gate: G, device_id: String) -> Self {
        Self { escalation, gate, device_id }
    }
}

impl<E: SosEscalation, G: AuthorizedUseGate> ThreatSink for SosThreatSink<E, G> {
    fn accept(&mut self, signal: &ThreatSignal) {
        let decision = self.gate.authorize(&AuthorizedUseRequest {
            capability: AntibodyCapability::EscalateSos,
            context: DefensiveThreatContext {
                severity: signal.severity,
                summary: signal.summary.clone(),
                matches: signal.matches.clone(),
                at_ms: signal.at_ms,
            },
            device_id: self.device_id.clone(),
            reason: signal.summary.clone(),
        });
        if decision.allowed {
            self.escalation.escalate(signal);
        }
    }
}

/// A sink that pokes a watchdog, so a stalled monitor is noticed.
pub struct WatchdogThreatSink<S: ThreatSink> {
    inner: S,
    last_at_ms: u64,
}

impl<S: ThreatSink> WatchdogThreatSink<S> {
    /// How long without a signal before the monitor is presumed stalled.
    pub const STALE_MS: u64 = 15 * 60 * 1000;

    pub fn new(inner: S) -> Self {
        Self { inner, last_at_ms: 0 }
    }

    /// Quiet is NOT the same as healthy.
    ///
    /// A monitor that has crashed and one that has seen nothing look identical
    /// from outside, and the whole reason to watch is that the first one is
    /// silent in exactly the way the second one is.
    pub fn is_stale(&self, now_ms: u64) -> bool {
        self.last_at_ms > 0 && now_ms.saturating_sub(self.last_at_ms) > Self::STALE_MS
    }
}

impl<S: ThreatSink> ThreatSink for WatchdogThreatSink<S> {
    fn accept(&mut self, signal: &ThreatSignal) {
        self.last_at_ms = signal.at_ms;
        self.inner.accept(signal);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// The whole thing

/// How the defensive module is configured.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct DefenseOptions {
    /// OFF. Watching a network is a decision, not a default.
    pub enabled: bool,
    pub blocklist_urls: Vec<String>,
    pub refresh_hours: u32,
    /// Escalation is off SEPARATELY from watching, because they are different
    /// agreements: noticing something is not telling anybody about it.
    pub escalation_enabled: bool,
}

/// Defence that runs while the device does.
pub trait AutonomicDefense {
    fn is_running(&self) -> bool;
    fn start(&mut self);
    fn stop(&mut self);
    fn last_signal(&self) -> Option<&ThreatSignal>;
}

/// The always-on sentinel.
///
/// IT ONLY WATCHES. Every action it could take goes through the gate, and with
/// no gate configured it takes none - so the worst a misconfigured deployment
/// does is notice things and tell nobody.
pub struct AlwaysOnDefenseSentinel {
    monitors: Vec<Box<dyn ThreatMonitor + Send + Sync>>,
    options: DefenseOptions,
    running: bool,
    last: Option<ThreatSignal>,
}

impl AlwaysOnDefenseSentinel {
    pub fn new(
        monitors: Vec<Box<dyn ThreatMonitor + Send + Sync>>,
        options: DefenseOptions,
    ) -> Self {
        Self { monitors, options, running: false, last: None }
    }

    /// Polls every monitor and records the most severe thing seen.
    pub fn tick(&mut self) -> Vec<ThreatSignal> {
        if !self.running {
            return Vec::new();
        }
        let mut all = Vec::new();
        for monitor in self.monitors.iter_mut() {
            all.extend(monitor.poll());
        }
        // The MOST SEVERE, not the most recent. A critical signal followed by an
        // informational one should not read as "things are fine now".
        if let Some(worst) = all.iter().max_by_key(|s| s.severity) {
            self.last = Some(worst.clone());
        }
        all
    }
}

impl AutonomicDefense for AlwaysOnDefenseSentinel {
    fn is_running(&self) -> bool {
        self.running
    }

    fn start(&mut self) {
        if !self.options.enabled || self.running {
            return;
        }
        for monitor in self.monitors.iter_mut() {
            monitor.start();
        }
        self.running = true;
    }

    fn stop(&mut self) {
        for monitor in self.monitors.iter_mut() {
            monitor.stop();
        }
        self.running = false;
    }

    fn last_signal(&self) -> Option<&ThreatSignal> {
        self.last.as_ref()
    }
}

/// The system that assesses and, through the gate, acts.
pub trait DefensiveAntibodySystemTrait {
    fn assess_file(&self, file: &FileArtifact) -> ThreatAwarenessResult;
    fn assess_host(&self, host: &str, port: u16) -> ThreatAwarenessResult;
    fn assess_identity(&self, email_or_phone: &str) -> ThreatAwarenessResult;
    fn act(&self, request: &AuthorizedUseRequest) -> AuthorizationDecision;
}

/// Assessment, and a single door to action.
///
/// The assessors are free to run because they only look. `act` is the one method
/// that can change anything, and it does nothing on its own - it asks the gate
/// and returns the decision. Whether the caller then does something is the
/// caller's business, and the DECISION is the record of whether it was allowed.
pub struct DefensiveAntibodySystem<G: AuthorizedUseGate> {
    files: Box<dyn FileThreatAwareness + Send + Sync>,
    network: Box<dyn NetworkThreatAwareness + Send + Sync>,
    breaches: Box<dyn BreachExposureAwareness + Send + Sync>,
    gate: G,
}

impl<G: AuthorizedUseGate> DefensiveAntibodySystem<G> {
    pub fn new(
        files: Box<dyn FileThreatAwareness + Send + Sync>,
        network: Box<dyn NetworkThreatAwareness + Send + Sync>,
        breaches: Box<dyn BreachExposureAwareness + Send + Sync>,
        gate: G,
    ) -> Self {
        Self { files, network, breaches, gate }
    }
}

impl<G: AuthorizedUseGate> DefensiveAntibodySystemTrait for DefensiveAntibodySystem<G> {
    fn assess_file(&self, file: &FileArtifact) -> ThreatAwarenessResult {
        self.files.assess(file)
    }
    fn assess_host(&self, host: &str, port: u16) -> ThreatAwarenessResult {
        self.network.assess(host, port)
    }
    fn assess_identity(&self, email_or_phone: &str) -> ThreatAwarenessResult {
        self.breaches.assess(email_or_phone)
    }
    fn act(&self, request: &AuthorizedUseRequest) -> AuthorizationDecision {
        self.gate.authorize(request)
    }
}

/// Assembles the defensive module a host has agreed to.
pub struct DefenseModule;

impl DefenseModule {
    pub fn build(
        options: DefenseOptions,
        monitors: Vec<Box<dyn ThreatMonitor + Send + Sync>>,
    ) -> AlwaysOnDefenseSentinel {
        // A DISABLED module returns a sentinel that will not start, rather than
        // failing. A disabled module is a normal configuration, not an error.
        if !options.enabled {
            return AlwaysOnDefenseSentinel::new(Vec::new(), options);
        }
        AlwaysOnDefenseSentinel::new(monitors, options)
    }
}
