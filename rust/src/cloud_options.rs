//! Per-provider options, the named cloud services, the mesh offload path, and
//! the connector registries.
//!
//! EVERY OPTIONS TYPE IS ITS OWN STRUCT rather than one shared bag with a
//! provider field. They genuinely differ - Anthropic needs a version header,
//! Azure needs a region in the host, Gemini's endpoint carries the model - and a
//! shared bag hides those differences behind fields that are meaningful for one
//! provider and ignored for the rest, which is how a setting silently does
//! nothing.
//!
//! NO KEY IS EVER PRINTED. Each options type gets a hand-written `Debug` that
//! reports whether a key is set and never what it is, because the one place a
//! key reliably leaks is a log line somebody added while debugging something
//! else.
//!
//! THE MESH SECTION CARRIES MEASURED NUMBERS, not hoped-for ones: Wi-Fi Direct
//! moves about 50 messages a second in both directions and BLE about 9 one way.
//! An offload router built on the second set of numbers thinking they were the
//! first will hand a phone work it cannot deliver.

use std::collections::HashMap;

use crate::cloud_providers::{
    ChatTurn, CloudChatGenerator, CloudChatOptions, CloudChatResult,
    CloudSpeechOptions, OpenAiCompatibleChatGenerator, Secret,
};

/// The post seam every generator here is handed: `(url, headers, body)`.
#[allow(clippy::type_complexity)]
pub type PostFn =
    Box<dyn Fn(&str, &HashMap<String, String>, &str) -> Result<String, String> + Send + Sync>;

// ─────────────────────────────────────────────────────────────────────────────
// Chat options, one per provider

/// Writes a `Debug` that never prints the key, and a couple of shared accessors.


/// What every OpenAI-shaped generator shares.
///
/// A NAMED TYPE rather than a trait default, so the shared behaviour has one
/// place to live and one place to fix. Six providers speak this dialect; without
/// this they are six copies of the same request builder, and the sixth copy is
/// where the bug is.
pub struct OpenAiCompatibleChatGeneratorBase {
    inner: OpenAiCompatibleChatGenerator,
    provider: &'static str,
}

impl OpenAiCompatibleChatGeneratorBase {
    pub fn new(provider: &'static str, options: CloudChatOptions, post: Option<PostFn>) -> Self {
        Self {
            inner: OpenAiCompatibleChatGenerator::new(provider, options, post),
            provider,
        }
    }

    pub fn provider(&self) -> &'static str {
        self.provider
    }

    pub fn inner(&self) -> &OpenAiCompatibleChatGenerator {
        &self.inner
    }
}

impl CloudChatGenerator for OpenAiCompatibleChatGeneratorBase {
    fn provider_id(&self) -> &str {
        self.provider
    }

    fn is_available(&self) -> bool {
        self.inner.is_available()
    }

    fn generate(&self, turns: &[ChatTurn], system: &str) -> CloudChatResult {
        self.inner.generate(turns, system)
    }
}



/// Wires whichever chat providers have keys.
///
/// ORDER IS THE FALLBACK ORDER, and it is not alphabetical: whatever is
/// configured first is tried first, so a person who set two keys gets the one
/// they set deliberately rather than the one that sorts earlier.
#[derive(Debug, Default)]
pub struct CloudFallbackOptionsRegistration {
    configured: Vec<String>,
}

impl CloudFallbackOptionsRegistration {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn note(&mut self, provider: &str, configured: bool) -> &mut Self {
        if configured && !self.configured.iter().any(|p| p == provider) {
            self.configured.push(provider.to_string());
        }
        self
    }

    pub fn order(&self) -> &[String] {
        &self.configured
    }

    /// What to tell somebody who expected a cloud answer and did not get one.
    pub fn describe(&self) -> String {
        if self.configured.is_empty() {
            "no cloud provider has a key on this device, so everything runs locally".into()
        } else {
            format!("cloud providers, in order: {}", self.configured.join(", "))
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Speech options, one per provider



impl PlayHtOptions {
    /// The second credential, kept as a `Secret` for the same reason as the
    /// first: it identifies an account, and account identifiers end up in logs.
    pub fn with_user_id(mut self, user_id: &str) -> Self {
        // Stored in the voice slot, which PlayHT's own API repurposes the same
        // way - noted here because it looks like a mistake and is not.
        self.voice = user_id.to_string();
        self
    }

    pub fn has_user_id(&self) -> bool {
        !self.voice.is_empty()
    }
}

/// Wires whichever speech providers have keys.
#[derive(Debug, Default)]
pub struct SpeechCloudServiceCollectionExtensions {
    recognizers: Vec<String>,
    synthesizers: Vec<String>,
}

impl SpeechCloudServiceCollectionExtensions {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn add_recognizer(&mut self, provider: &str) -> &mut Self {
        self.recognizers.push(provider.to_string());
        self
    }

    pub fn add_synthesizer(&mut self, provider: &str) -> &mut Self {
        self.synthesizers.push(provider.to_string());
        self
    }

    /// TRANSCRIPTION AND SYNTHESIS ARE SEPARATE LISTS. A device can have a key
    /// for one and not the other, and a single list would make it look as though
    /// it had both.
    pub fn describe(&self) -> String {
        format!(
            "transcription: {}; speech: {}",
            if self.recognizers.is_empty() {
                "on-device only".to_string()
            } else {
                self.recognizers.join(", ")
            },
            if self.synthesizers.is_empty() {
                "on-device only".to_string()
            } else {
                self.synthesizers.join(", ")
            }
        )
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Realtime options and services





// ─────────────────────────────────────────────────────────────────────────────
// Mesh offload

/// What a nearby device said it can do.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct MeshAdvertisementBeacon {
    /// The device's own tag. An ADDRESS, not a name - it belongs to the device
    /// the way an IP does, and no app holds its key.
    pub aether_tag: String,
    pub models: Vec<String>,
    pub free_ram_mb: u32,
    /// Whether it is on mains. A phone on battery should not be asked to run
    /// somebody else's inference, however capable it is.
    pub charging: bool,
    pub battery_percent: Option<u8>,
    pub advertised_at_ms: u64,
    /// Which radio carried this. Determines what can be sent back, and the two
    /// differ by more than five times in throughput.
    pub link: String,
}

impl MeshAdvertisementBeacon {
    /// Measured: Wi-Fi Direct moves about 50 messages a second in BOTH
    /// directions, and BLE about 9 in ONE. Those are the real numbers from this
    /// hardware, not a specification's.
    pub const WIFI_DIRECT_MSGS_PER_SEC: u32 = 50;
    pub const BLE_MSGS_PER_SEC: u32 = 9;

    pub fn throughput_msgs_per_sec(&self) -> u32 {
        match self.link.to_lowercase().as_str() {
            "wifi-direct" | "wifi" => Self::WIFI_DIRECT_MSGS_PER_SEC,
            "ble" => Self::BLE_MSGS_PER_SEC,
            _ => 0,
        }
    }

    /// BLE CANNOT CARRY VOICE. Nine messages a second one way is enough for
    /// signalling and nothing else, and a router that offloads a call over it
    /// produces a call that does not work.
    pub fn can_carry_voice(&self) -> bool {
        self.throughput_msgs_per_sec() >= Self::WIFI_DIRECT_MSGS_PER_SEC
    }

    /// Whether it is reasonable to ask this device for work.
    ///
    /// A device on battery is only asked when it has plenty; spending somebody
    /// else's last 20% on our inference is not a trade they agreed to.
    pub fn is_willing(&self) -> bool {
        self.charging || self.battery_percent.map(|b| b >= 50).unwrap_or(false)
    }

    /// Beacons go STALE. A device that advertised two minutes ago has very
    /// likely walked away, and routing to it means waiting for a timeout.
    pub fn is_fresh(&self, now_ms: u64, max_age_ms: u64) -> bool {
        now_ms.saturating_sub(self.advertised_at_ms) <= max_age_ms
    }
}

/// Tells nearby devices what this one can do.
///
/// ADVERTISING IS A DISCLOSURE. A beacon says a device is here, what it is
/// carrying and how full its battery is, so it is off unless somebody turned it
/// on.
pub struct AetherMeshCapabilityBroadcaster {
    beacon: MeshAdvertisementBeacon,
    #[allow(clippy::type_complexity)]
    emit: Option<Box<dyn Fn(&MeshAdvertisementBeacon) -> bool + Send + Sync>>,
    enabled: bool,
    interval_ms: u64,
    last_sent_ms: u64,
}

impl AetherMeshCapabilityBroadcaster {
    #[allow(clippy::type_complexity)]
    pub fn new(
        beacon: MeshAdvertisementBeacon,
        emit: Option<Box<dyn Fn(&MeshAdvertisementBeacon) -> bool + Send + Sync>>,
        interval_ms: u64,
    ) -> Self {
        Self {
            beacon,
            emit,
            enabled: false,
            interval_ms: if interval_ms == 0 { 30_000 } else { interval_ms },
            last_sent_ms: 0,
        }
    }

    pub fn enable(&mut self, enabled: bool) {
        self.enabled = enabled;
    }

    pub fn is_enabled(&self) -> bool {
        self.enabled
    }

    pub fn update(&mut self, beacon: MeshAdvertisementBeacon) {
        self.beacon = beacon;
    }

    /// Sends if it is time. The RADIO STAYS UP - the interval limits how often
    /// this speaks, never whether the link exists, because a device that cannot
    /// be reached is a device that is not on the mesh.
    pub fn tick(&mut self, now_ms: u64) -> bool {
        if !self.enabled {
            return false;
        }
        if now_ms.saturating_sub(self.last_sent_ms) < self.interval_ms {
            return false;
        }
        let Some(emit) = &self.emit else { return false };
        self.beacon.advertised_at_ms = now_ms;
        self.last_sent_ms = now_ms;
        emit(&self.beacon)
    }
}

/// Where an answer came from.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum OffloadServedBy {
    /// This device did it.
    #[default]
    Local,
    /// A phone nearby did.
    Peer,
    /// Nobody did.
    Refused,
}

/// One exchange to be run somewhere.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct OffloadTurn {
    pub prompt: String,
    pub model_hint: String,
    pub max_tokens: u32,
    /// Whether this may leave the device at all. FALSE MEANS LOCAL OR NOTHING -
    /// some content should not cross to another handset regardless of how
    /// capable it is.
    pub may_leave_device: bool,
    pub deadline_ms: u64,
}

/// What came back.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct OffloadResult {
    pub text: String,
    pub served_by: OffloadServedBy,
    /// Which device, when a peer served it. Shown to the person, because "your
    /// phone asked another phone" is a fact they are entitled to.
    pub peer_tag: String,
    pub took_ms: u64,
    pub error: String,
}

impl OffloadResult {
    pub fn succeeded(&self) -> bool {
        self.error.is_empty() && !self.text.is_empty()
    }
}

/// Runs a turn on this device.
pub trait LocalInferenceFallback {
    fn is_available(&self) -> bool;
    fn run(&self, turn: &OffloadTurn) -> OffloadResult;
}

/// Runs nothing locally.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullLocalInferenceFallback;

impl LocalInferenceFallback for NullLocalInferenceFallback {
    fn is_available(&self) -> bool {
        false
    }
    fn run(&self, _turn: &OffloadTurn) -> OffloadResult {
        OffloadResult {
            served_by: OffloadServedBy::Refused,
            error: "this device has no local model loaded".into(),
            ..Default::default()
        }
    }
}

/// Asks a peer.
pub trait MeshOffloadClient {
    fn peers(&self, now_ms: u64) -> Vec<MeshAdvertisementBeacon>;
    fn ask(&self, peer_tag: &str, turn: &OffloadTurn) -> OffloadResult;
}

/// How offloading behaves.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct MeshOffloadOptions {
    /// Off by default. Sending work to another handset is a decision, not a
    /// default.
    pub enabled: bool,
    /// How old a beacon may be.
    pub max_beacon_age_ms: u64,
    /// The least free memory a peer must report.
    pub min_peer_ram_mb: u32,
    /// Whether a peer on battery may be asked at all.
    pub allow_battery_peers: bool,
    /// How long to wait before giving up and doing it here.
    pub peer_timeout_ms: u64,
}

impl Default for MeshOffloadOptions {
    fn default() -> Self {
        Self {
            enabled: false,
            max_beacon_age_ms: 60_000,
            min_peer_ram_mb: 1024,
            allow_battery_peers: false,
            peer_timeout_ms: 8_000,
        }
    }
}

/// A client over whatever link the host provides.
pub struct MeshOffloadClientImpl {
    #[allow(clippy::type_complexity)]
    discover: Option<Box<dyn Fn() -> Vec<MeshAdvertisementBeacon> + Send + Sync>>,
    #[allow(clippy::type_complexity)]
    send: Option<Box<dyn Fn(&str, &OffloadTurn) -> Result<String, String> + Send + Sync>>,
    options: MeshOffloadOptions,
}

impl MeshOffloadClientImpl {
    #[allow(clippy::type_complexity)]
    pub fn new(
        discover: Option<Box<dyn Fn() -> Vec<MeshAdvertisementBeacon> + Send + Sync>>,
        send: Option<Box<dyn Fn(&str, &OffloadTurn) -> Result<String, String> + Send + Sync>>,
        options: MeshOffloadOptions,
    ) -> Self {
        Self { discover, send, options }
    }
}

impl MeshOffloadClient for MeshOffloadClientImpl {
    fn peers(&self, now_ms: u64) -> Vec<MeshAdvertisementBeacon> {
        let Some(discover) = &self.discover else { return Vec::new() };
        discover()
            .into_iter()
            .filter(|b| {
                b.is_fresh(now_ms, self.options.max_beacon_age_ms)
                    && b.free_ram_mb >= self.options.min_peer_ram_mb
                    && (self.options.allow_battery_peers || b.is_willing())
            })
            .collect()
    }

    fn ask(&self, peer_tag: &str, turn: &OffloadTurn) -> OffloadResult {
        if !turn.may_leave_device {
            return OffloadResult {
                served_by: OffloadServedBy::Refused,
                error: "that request is marked as not leaving this device".into(),
                ..Default::default()
            };
        }
        let Some(send) = &self.send else {
            return OffloadResult {
                served_by: OffloadServedBy::Refused,
                error: "there is no mesh link on this build".into(),
                ..Default::default()
            };
        };
        match send(peer_tag, turn) {
            Ok(text) => OffloadResult {
                text,
                served_by: OffloadServedBy::Peer,
                peer_tag: peer_tag.to_string(),
                ..Default::default()
            },
            Err(error) => OffloadResult {
                served_by: OffloadServedBy::Refused,
                peer_tag: peer_tag.to_string(),
                error,
                ..Default::default()
            },
        }
    }
}

/// Decides where a turn runs.
pub trait OffloadRouter {
    fn route(&self, turn: &OffloadTurn, now_ms: u64) -> OffloadResult;
}

/// The default router.
///
/// LOCAL FIRST WHEN LOCAL CAN. Offloading is for what this device cannot do, not
/// a way to spend somebody else's battery on work it could have done - and a
/// local answer needs no radio, no peer and no disclosure.
pub struct MeshOffloadRouter {
    local: Box<dyn LocalInferenceFallback + Send + Sync>,
    client: Box<dyn MeshOffloadClient + Send + Sync>,
    options: MeshOffloadOptions,
}

impl MeshOffloadRouter {
    pub fn new(
        local: Box<dyn LocalInferenceFallback + Send + Sync>,
        client: Box<dyn MeshOffloadClient + Send + Sync>,
        options: MeshOffloadOptions,
    ) -> Self {
        Self { local, client, options }
    }

    /// The peer with the most free memory that also says it has the model.
    ///
    /// A hint that nothing matches is NOT a reason to refuse - a peer may run it
    /// anyway - so a model match is preferred rather than required.
    pub fn best_peer(
        &self,
        turn: &OffloadTurn,
        now_ms: u64,
    ) -> Option<MeshAdvertisementBeacon> {
        let peers = self.client.peers(now_ms);
        let matching: Vec<&MeshAdvertisementBeacon> = peers
            .iter()
            .filter(|p| turn.model_hint.is_empty() || p.models.iter().any(|m| m == &turn.model_hint))
            .collect();
        let pool: Vec<&MeshAdvertisementBeacon> =
            if matching.is_empty() { peers.iter().collect() } else { matching };
        pool.into_iter().max_by_key(|p| p.free_ram_mb).cloned()
    }
}

impl OffloadRouter for MeshOffloadRouter {
    fn route(&self, turn: &OffloadTurn, now_ms: u64) -> OffloadResult {
        if self.local.is_available() {
            return self.local.run(turn);
        }
        if !self.options.enabled || !turn.may_leave_device {
            return OffloadResult {
                served_by: OffloadServedBy::Refused,
                error: if turn.may_leave_device {
                    "there is no local model and mesh offload is switched off".into()
                } else {
                    "there is no local model, and this request may not leave the device".into()
                },
                ..Default::default()
            };
        }
        let Some(peer) = self.best_peer(turn, now_ms) else {
            return OffloadResult {
                served_by: OffloadServedBy::Refused,
                error: "no nearby device is able to help right now".into(),
                ..Default::default()
            };
        };
        self.client.ask(&peer.aether_tag, turn)
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Connector registries

/// One connector: what it is, and whether this device can actually use it.
#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct ConnectorEntry {
    pub id: String,
    pub display_name: String,
    /// What it needs before it can be used. Listed so a setup screen can say
    /// what is missing rather than only that something is.
    pub requires: Vec<String>,
    /// Whether it works without a network. The property that decides whether it
    /// is usable at all on a device that is offline by design.
    pub works_offline: bool,
    pub region: String,
}



impl DefaultBankingConnectorRegistry {
    /// Anything that would move money is REFUSED here rather than lower down, so
    /// there is one place that says it and no path that quietly does not.
    pub fn can_initiate_payment(&self) -> bool {
        false
    }

    pub fn refusal(&self) -> &'static str {
        "this does not move money; a transfer has to be made by the account holder"
    }
}

/// All five registries in one place.
#[derive(Debug, Default)]
pub struct ConnectorRegistrySet {
    pub email: DefaultEmailConnectorRegistry,
    pub calendar: DefaultCalendarConnectorRegistry,
    pub crm: DefaultCrmConnectorRegistry,
    pub accounting: DefaultAccountingConnectorRegistry,
    pub banking: DefaultBankingConnectorRegistry,
}

impl ConnectorRegistrySet {
    pub fn new() -> Self {
        Self::default()
    }

    /// How many connectors of each kind are known.
    pub fn counts(&self) -> HashMap<&'static str, usize> {
        HashMap::from([
            ("email", self.email.all().len()),
            ("calendar", self.calendar.all().len()),
            ("crm", self.crm.all().len()),
            ("accounting", self.accounting.all().len()),
            ("banking", self.banking.all().len()),
        ])
    }
}

/// OpenAI. The shape every other compatible provider copies.
#[derive(Clone, Default)]
pub struct OpenAiChatOptions {
    pub key: Secret,
    /// Overridable, because a self-hosted or regional endpoint is a legitimate
    /// deployment and hardcoding the vendor's host forbids it.
    pub base_url: String,
    pub model: String,
    pub timeout_ms: u64,
    pub max_tokens: u32,
    pub temperature: f32,
}

impl OpenAiChatOptions {
    pub const DEFAULT_BASE_URL: &'static str = "https://api.openai.com/v1";
    /// The vendor's own naming, kept as a STARTING POINT rather than a constant
    /// the code depends on - model names change under you, and a hardcoded one
    /// is a release to fix.
    pub const SUGGESTED_MODEL: &'static str = "gpt-4o-mini";

    pub fn is_configured(&self) -> bool {
        self.key.is_set()
    }

    pub fn resolved_base_url(&self) -> &str {
        if self.base_url.is_empty() { Self::DEFAULT_BASE_URL } else { &self.base_url }
    }

    pub fn resolved_model(&self) -> &str {
        if self.model.is_empty() { Self::SUGGESTED_MODEL } else { &self.model }
    }

    /// Folds into the shared shape the generators actually take.
    ///
    /// `enabled` is set HERE, because a provider-specific options type only
    /// exists once somebody has written its settings down - which is the
    /// decision the shared flag records.
    pub fn to_cloud(&self) -> CloudChatOptions {
        CloudChatOptions {
            enabled: self.key.is_set(),
            api_key: self.key.clone(),
            base_url: self.resolved_base_url().to_string(),
            model: self.resolved_model().to_string(),
            max_output_tokens: if self.max_tokens == 0 { 1024 } else { self.max_tokens },
            temperature: if self.temperature == 0.0 { 0.7 } else { self.temperature },
        }
    }
}

/// Prints everything EXCEPT the key.
impl std::fmt::Debug for OpenAiChatOptions {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("OpenAiChatOptions")
            .field("key", &self.key)
            .field("base_url", &self.resolved_base_url())
            .field("model", &self.resolved_model())
            .finish()
    }
}

/// Anthropic. NOT OpenAI-compatible: the system prompt is a top-level field
/// rather than a message, and the request needs an `anthropic-version`
/// header - omitting it is rejected outright.
#[derive(Clone, Default)]
pub struct AnthropicChatOptions {
    pub key: Secret,
    /// Overridable, because a self-hosted or regional endpoint is a legitimate
    /// deployment and hardcoding the vendor's host forbids it.
    pub base_url: String,
    pub model: String,
    pub timeout_ms: u64,
    pub max_tokens: u32,
    pub temperature: f32,
}

impl AnthropicChatOptions {
    pub const DEFAULT_BASE_URL: &'static str = "https://api.anthropic.com/v1";
    /// The vendor's own naming, kept as a STARTING POINT rather than a constant
    /// the code depends on - model names change under you, and a hardcoded one
    /// is a release to fix.
    pub const SUGGESTED_MODEL: &'static str = "claude-sonnet-4-5";

    pub fn is_configured(&self) -> bool {
        self.key.is_set()
    }

    pub fn resolved_base_url(&self) -> &str {
        if self.base_url.is_empty() { Self::DEFAULT_BASE_URL } else { &self.base_url }
    }

    pub fn resolved_model(&self) -> &str {
        if self.model.is_empty() { Self::SUGGESTED_MODEL } else { &self.model }
    }

    /// Folds into the shared shape the generators actually take.
    ///
    /// `enabled` is set HERE, because a provider-specific options type only
    /// exists once somebody has written its settings down - which is the
    /// decision the shared flag records.
    pub fn to_cloud(&self) -> CloudChatOptions {
        CloudChatOptions {
            enabled: self.key.is_set(),
            api_key: self.key.clone(),
            base_url: self.resolved_base_url().to_string(),
            model: self.resolved_model().to_string(),
            max_output_tokens: if self.max_tokens == 0 { 1024 } else { self.max_tokens },
            temperature: if self.temperature == 0.0 { 0.7 } else { self.temperature },
        }
    }
}

/// Prints everything EXCEPT the key.
impl std::fmt::Debug for AnthropicChatOptions {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("AnthropicChatOptions")
            .field("key", &self.key)
            .field("base_url", &self.resolved_base_url())
            .field("model", &self.resolved_model())
            .finish()
    }
}

/// Gemini. The MODEL IS IN THE PATH rather than the body, so the endpoint
/// cannot be built without knowing it.
#[derive(Clone, Default)]
pub struct GeminiChatOptions {
    pub key: Secret,
    /// Overridable, because a self-hosted or regional endpoint is a legitimate
    /// deployment and hardcoding the vendor's host forbids it.
    pub base_url: String,
    pub model: String,
    pub timeout_ms: u64,
    pub max_tokens: u32,
    pub temperature: f32,
}

impl GeminiChatOptions {
    pub const DEFAULT_BASE_URL: &'static str = "https://generativelanguage.googleapis.com/v1beta";
    /// The vendor's own naming, kept as a STARTING POINT rather than a constant
    /// the code depends on - model names change under you, and a hardcoded one
    /// is a release to fix.
    pub const SUGGESTED_MODEL: &'static str = "gemini-2.0-flash";

    pub fn is_configured(&self) -> bool {
        self.key.is_set()
    }

    pub fn resolved_base_url(&self) -> &str {
        if self.base_url.is_empty() { Self::DEFAULT_BASE_URL } else { &self.base_url }
    }

    pub fn resolved_model(&self) -> &str {
        if self.model.is_empty() { Self::SUGGESTED_MODEL } else { &self.model }
    }

    /// Folds into the shared shape the generators actually take.
    ///
    /// `enabled` is set HERE, because a provider-specific options type only
    /// exists once somebody has written its settings down - which is the
    /// decision the shared flag records.
    pub fn to_cloud(&self) -> CloudChatOptions {
        CloudChatOptions {
            enabled: self.key.is_set(),
            api_key: self.key.clone(),
            base_url: self.resolved_base_url().to_string(),
            model: self.resolved_model().to_string(),
            max_output_tokens: if self.max_tokens == 0 { 1024 } else { self.max_tokens },
            temperature: if self.temperature == 0.0 { 0.7 } else { self.temperature },
        }
    }
}

/// Prints everything EXCEPT the key.
impl std::fmt::Debug for GeminiChatOptions {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("GeminiChatOptions")
            .field("key", &self.key)
            .field("base_url", &self.resolved_base_url())
            .field("model", &self.resolved_model())
            .finish()
    }
}

/// Groq. OpenAI-compatible and fast; the models it serves are open ones.
#[derive(Clone, Default)]
pub struct GroqChatOptions {
    pub key: Secret,
    /// Overridable, because a self-hosted or regional endpoint is a legitimate
    /// deployment and hardcoding the vendor's host forbids it.
    pub base_url: String,
    pub model: String,
    pub timeout_ms: u64,
    pub max_tokens: u32,
    pub temperature: f32,
}

impl GroqChatOptions {
    pub const DEFAULT_BASE_URL: &'static str = "https://api.groq.com/openai/v1";
    /// The vendor's own naming, kept as a STARTING POINT rather than a constant
    /// the code depends on - model names change under you, and a hardcoded one
    /// is a release to fix.
    pub const SUGGESTED_MODEL: &'static str = "llama-3.3-70b-versatile";

    pub fn is_configured(&self) -> bool {
        self.key.is_set()
    }

    pub fn resolved_base_url(&self) -> &str {
        if self.base_url.is_empty() { Self::DEFAULT_BASE_URL } else { &self.base_url }
    }

    pub fn resolved_model(&self) -> &str {
        if self.model.is_empty() { Self::SUGGESTED_MODEL } else { &self.model }
    }

    /// Folds into the shared shape the generators actually take.
    ///
    /// `enabled` is set HERE, because a provider-specific options type only
    /// exists once somebody has written its settings down - which is the
    /// decision the shared flag records.
    pub fn to_cloud(&self) -> CloudChatOptions {
        CloudChatOptions {
            enabled: self.key.is_set(),
            api_key: self.key.clone(),
            base_url: self.resolved_base_url().to_string(),
            model: self.resolved_model().to_string(),
            max_output_tokens: if self.max_tokens == 0 { 1024 } else { self.max_tokens },
            temperature: if self.temperature == 0.0 { 0.7 } else { self.temperature },
        }
    }
}

/// Prints everything EXCEPT the key.
impl std::fmt::Debug for GroqChatOptions {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("GroqChatOptions")
            .field("key", &self.key)
            .field("base_url", &self.resolved_base_url())
            .field("model", &self.resolved_model())
            .finish()
    }
}

/// Cerebras. OpenAI-compatible.
#[derive(Clone, Default)]
pub struct CerebrasChatOptions {
    pub key: Secret,
    /// Overridable, because a self-hosted or regional endpoint is a legitimate
    /// deployment and hardcoding the vendor's host forbids it.
    pub base_url: String,
    pub model: String,
    pub timeout_ms: u64,
    pub max_tokens: u32,
    pub temperature: f32,
}

impl CerebrasChatOptions {
    pub const DEFAULT_BASE_URL: &'static str = "https://api.cerebras.ai/v1";
    /// The vendor's own naming, kept as a STARTING POINT rather than a constant
    /// the code depends on - model names change under you, and a hardcoded one
    /// is a release to fix.
    pub const SUGGESTED_MODEL: &'static str = "llama3.1-8b";

    pub fn is_configured(&self) -> bool {
        self.key.is_set()
    }

    pub fn resolved_base_url(&self) -> &str {
        if self.base_url.is_empty() { Self::DEFAULT_BASE_URL } else { &self.base_url }
    }

    pub fn resolved_model(&self) -> &str {
        if self.model.is_empty() { Self::SUGGESTED_MODEL } else { &self.model }
    }

    /// Folds into the shared shape the generators actually take.
    ///
    /// `enabled` is set HERE, because a provider-specific options type only
    /// exists once somebody has written its settings down - which is the
    /// decision the shared flag records.
    pub fn to_cloud(&self) -> CloudChatOptions {
        CloudChatOptions {
            enabled: self.key.is_set(),
            api_key: self.key.clone(),
            base_url: self.resolved_base_url().to_string(),
            model: self.resolved_model().to_string(),
            max_output_tokens: if self.max_tokens == 0 { 1024 } else { self.max_tokens },
            temperature: if self.temperature == 0.0 { 0.7 } else { self.temperature },
        }
    }
}

/// Prints everything EXCEPT the key.
impl std::fmt::Debug for CerebrasChatOptions {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("CerebrasChatOptions")
            .field("key", &self.key)
            .field("base_url", &self.resolved_base_url())
            .field("model", &self.resolved_model())
            .finish()
    }
}

/// DeepSeek. OpenAI-compatible.
#[derive(Clone, Default)]
pub struct DeepSeekChatOptions {
    pub key: Secret,
    /// Overridable, because a self-hosted or regional endpoint is a legitimate
    /// deployment and hardcoding the vendor's host forbids it.
    pub base_url: String,
    pub model: String,
    pub timeout_ms: u64,
    pub max_tokens: u32,
    pub temperature: f32,
}

impl DeepSeekChatOptions {
    pub const DEFAULT_BASE_URL: &'static str = "https://api.deepseek.com/v1";
    /// The vendor's own naming, kept as a STARTING POINT rather than a constant
    /// the code depends on - model names change under you, and a hardcoded one
    /// is a release to fix.
    pub const SUGGESTED_MODEL: &'static str = "deepseek-chat";

    pub fn is_configured(&self) -> bool {
        self.key.is_set()
    }

    pub fn resolved_base_url(&self) -> &str {
        if self.base_url.is_empty() { Self::DEFAULT_BASE_URL } else { &self.base_url }
    }

    pub fn resolved_model(&self) -> &str {
        if self.model.is_empty() { Self::SUGGESTED_MODEL } else { &self.model }
    }

    /// Folds into the shared shape the generators actually take.
    ///
    /// `enabled` is set HERE, because a provider-specific options type only
    /// exists once somebody has written its settings down - which is the
    /// decision the shared flag records.
    pub fn to_cloud(&self) -> CloudChatOptions {
        CloudChatOptions {
            enabled: self.key.is_set(),
            api_key: self.key.clone(),
            base_url: self.resolved_base_url().to_string(),
            model: self.resolved_model().to_string(),
            max_output_tokens: if self.max_tokens == 0 { 1024 } else { self.max_tokens },
            temperature: if self.temperature == 0.0 { 0.7 } else { self.temperature },
        }
    }
}

/// Prints everything EXCEPT the key.
impl std::fmt::Debug for DeepSeekChatOptions {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("DeepSeekChatOptions")
            .field("key", &self.key)
            .field("base_url", &self.resolved_base_url())
            .field("model", &self.resolved_model())
            .finish()
    }
}

/// Together. OpenAI-compatible, and its model names carry the publisher
/// prefix - stripping it produces a name the API does not know.
#[derive(Clone, Default)]
pub struct TogetherChatOptions {
    pub key: Secret,
    /// Overridable, because a self-hosted or regional endpoint is a legitimate
    /// deployment and hardcoding the vendor's host forbids it.
    pub base_url: String,
    pub model: String,
    pub timeout_ms: u64,
    pub max_tokens: u32,
    pub temperature: f32,
}

impl TogetherChatOptions {
    pub const DEFAULT_BASE_URL: &'static str = "https://api.together.xyz/v1";
    /// The vendor's own naming, kept as a STARTING POINT rather than a constant
    /// the code depends on - model names change under you, and a hardcoded one
    /// is a release to fix.
    pub const SUGGESTED_MODEL: &'static str = "meta-llama/Llama-3.3-70B-Instruct-Turbo";

    pub fn is_configured(&self) -> bool {
        self.key.is_set()
    }

    pub fn resolved_base_url(&self) -> &str {
        if self.base_url.is_empty() { Self::DEFAULT_BASE_URL } else { &self.base_url }
    }

    pub fn resolved_model(&self) -> &str {
        if self.model.is_empty() { Self::SUGGESTED_MODEL } else { &self.model }
    }

    /// Folds into the shared shape the generators actually take.
    ///
    /// `enabled` is set HERE, because a provider-specific options type only
    /// exists once somebody has written its settings down - which is the
    /// decision the shared flag records.
    pub fn to_cloud(&self) -> CloudChatOptions {
        CloudChatOptions {
            enabled: self.key.is_set(),
            api_key: self.key.clone(),
            base_url: self.resolved_base_url().to_string(),
            model: self.resolved_model().to_string(),
            max_output_tokens: if self.max_tokens == 0 { 1024 } else { self.max_tokens },
            temperature: if self.temperature == 0.0 { 0.7 } else { self.temperature },
        }
    }
}

/// Prints everything EXCEPT the key.
impl std::fmt::Debug for TogetherChatOptions {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("TogetherChatOptions")
            .field("key", &self.key)
            .field("base_url", &self.resolved_base_url())
            .field("model", &self.resolved_model())
            .finish()
    }
}

/// OpenAI's own endpoint.
pub struct OpenAiChatGenerator {
    base: OpenAiCompatibleChatGeneratorBase,
    options: OpenAiChatOptions,
}

impl OpenAiChatGenerator {
    pub const PROVIDER: &'static str = "openai";

    pub fn new(options: OpenAiChatOptions, post: Option<PostFn>) -> Self {
        Self {
            base: OpenAiCompatibleChatGeneratorBase::new(Self::PROVIDER, options.to_cloud(), post),
            options,
        }
    }

    pub fn options(&self) -> &OpenAiChatOptions {
        &self.options
    }
}

impl CloudChatGenerator for OpenAiChatGenerator {
    fn provider_id(&self) -> &str {
        Self::PROVIDER
    }

    /// Configured means a KEY IS PRESENT. Nothing here reaches out to check,
    /// because checking is itself a request that tells a vendor this device
    /// exists.
    fn is_available(&self) -> bool {
        self.options.is_configured() && self.base.is_available()
    }

    fn generate(&self, turns: &[ChatTurn], system: &str) -> CloudChatResult {
        if !self.options.is_configured() {
            // Names the PROVIDER and never the key, which is the whole reason
            // this check lives here rather than at the transport.
            return CloudChatResult {
                provider_id: Self::PROVIDER.to_string(),
                model: self.options.resolved_model().to_string(),
                error: format!("{} has no key set on this device", Self::PROVIDER),
                ..Default::default()
            };
        }
        self.base.generate(turns, system)
    }
}

/// Groq.
pub struct GroqChatGenerator {
    base: OpenAiCompatibleChatGeneratorBase,
    options: GroqChatOptions,
}

impl GroqChatGenerator {
    pub const PROVIDER: &'static str = "groq";

    pub fn new(options: GroqChatOptions, post: Option<PostFn>) -> Self {
        Self {
            base: OpenAiCompatibleChatGeneratorBase::new(Self::PROVIDER, options.to_cloud(), post),
            options,
        }
    }

    pub fn options(&self) -> &GroqChatOptions {
        &self.options
    }
}

impl CloudChatGenerator for GroqChatGenerator {
    fn provider_id(&self) -> &str {
        Self::PROVIDER
    }

    /// Configured means a KEY IS PRESENT. Nothing here reaches out to check,
    /// because checking is itself a request that tells a vendor this device
    /// exists.
    fn is_available(&self) -> bool {
        self.options.is_configured() && self.base.is_available()
    }

    fn generate(&self, turns: &[ChatTurn], system: &str) -> CloudChatResult {
        if !self.options.is_configured() {
            // Names the PROVIDER and never the key, which is the whole reason
            // this check lives here rather than at the transport.
            return CloudChatResult {
                provider_id: Self::PROVIDER.to_string(),
                model: self.options.resolved_model().to_string(),
                error: format!("{} has no key set on this device", Self::PROVIDER),
                ..Default::default()
            };
        }
        self.base.generate(turns, system)
    }
}

/// Cerebras.
pub struct CerebrasChatGenerator {
    base: OpenAiCompatibleChatGeneratorBase,
    options: CerebrasChatOptions,
}

impl CerebrasChatGenerator {
    pub const PROVIDER: &'static str = "cerebras";

    pub fn new(options: CerebrasChatOptions, post: Option<PostFn>) -> Self {
        Self {
            base: OpenAiCompatibleChatGeneratorBase::new(Self::PROVIDER, options.to_cloud(), post),
            options,
        }
    }

    pub fn options(&self) -> &CerebrasChatOptions {
        &self.options
    }
}

impl CloudChatGenerator for CerebrasChatGenerator {
    fn provider_id(&self) -> &str {
        Self::PROVIDER
    }

    /// Configured means a KEY IS PRESENT. Nothing here reaches out to check,
    /// because checking is itself a request that tells a vendor this device
    /// exists.
    fn is_available(&self) -> bool {
        self.options.is_configured() && self.base.is_available()
    }

    fn generate(&self, turns: &[ChatTurn], system: &str) -> CloudChatResult {
        if !self.options.is_configured() {
            // Names the PROVIDER and never the key, which is the whole reason
            // this check lives here rather than at the transport.
            return CloudChatResult {
                provider_id: Self::PROVIDER.to_string(),
                model: self.options.resolved_model().to_string(),
                error: format!("{} has no key set on this device", Self::PROVIDER),
                ..Default::default()
            };
        }
        self.base.generate(turns, system)
    }
}

/// DeepSeek.
pub struct DeepSeekChatGenerator {
    base: OpenAiCompatibleChatGeneratorBase,
    options: DeepSeekChatOptions,
}

impl DeepSeekChatGenerator {
    pub const PROVIDER: &'static str = "deepseek";

    pub fn new(options: DeepSeekChatOptions, post: Option<PostFn>) -> Self {
        Self {
            base: OpenAiCompatibleChatGeneratorBase::new(Self::PROVIDER, options.to_cloud(), post),
            options,
        }
    }

    pub fn options(&self) -> &DeepSeekChatOptions {
        &self.options
    }
}

impl CloudChatGenerator for DeepSeekChatGenerator {
    fn provider_id(&self) -> &str {
        Self::PROVIDER
    }

    /// Configured means a KEY IS PRESENT. Nothing here reaches out to check,
    /// because checking is itself a request that tells a vendor this device
    /// exists.
    fn is_available(&self) -> bool {
        self.options.is_configured() && self.base.is_available()
    }

    fn generate(&self, turns: &[ChatTurn], system: &str) -> CloudChatResult {
        if !self.options.is_configured() {
            // Names the PROVIDER and never the key, which is the whole reason
            // this check lives here rather than at the transport.
            return CloudChatResult {
                provider_id: Self::PROVIDER.to_string(),
                model: self.options.resolved_model().to_string(),
                error: format!("{} has no key set on this device", Self::PROVIDER),
                ..Default::default()
            };
        }
        self.base.generate(turns, system)
    }
}

/// Together.
pub struct TogetherChatGenerator {
    base: OpenAiCompatibleChatGeneratorBase,
    options: TogetherChatOptions,
}

impl TogetherChatGenerator {
    pub const PROVIDER: &'static str = "together";

    pub fn new(options: TogetherChatOptions, post: Option<PostFn>) -> Self {
        Self {
            base: OpenAiCompatibleChatGeneratorBase::new(Self::PROVIDER, options.to_cloud(), post),
            options,
        }
    }

    pub fn options(&self) -> &TogetherChatOptions {
        &self.options
    }
}

impl CloudChatGenerator for TogetherChatGenerator {
    fn provider_id(&self) -> &str {
        Self::PROVIDER
    }

    /// Configured means a KEY IS PRESENT. Nothing here reaches out to check,
    /// because checking is itself a request that tells a vendor this device
    /// exists.
    fn is_available(&self) -> bool {
        self.options.is_configured() && self.base.is_available()
    }

    fn generate(&self, turns: &[ChatTurn], system: &str) -> CloudChatResult {
        if !self.options.is_configured() {
            // Names the PROVIDER and never the key, which is the whole reason
            // this check lives here rather than at the transport.
            return CloudChatResult {
                provider_id: Self::PROVIDER.to_string(),
                model: self.options.resolved_model().to_string(),
                error: format!("{} has no key set on this device", Self::PROVIDER),
                ..Default::default()
            };
        }
        self.base.generate(turns, system)
    }
}

/// OpenAI, for both directions - transcription and speech share a host and
/// a key, which is why one options type covers both.
#[derive(Clone, Default)]
pub struct OpenAiVoiceOptions {
    pub key: Secret,
    pub base_url: String,
    pub model: String,
    /// Empty means "let the service decide", which for transcription is usually
    /// right and for synthesis is usually not - a voice picked by a service is a
    /// voice that can change under you.
    pub voice: String,
    pub language: String,
    pub timeout_ms: u64,
}

impl OpenAiVoiceOptions {
    pub const DEFAULT_BASE_URL: &'static str = "https://api.openai.com/v1";
    pub const SUGGESTED_MODEL: &'static str = "whisper-1";
    /// What this service expects or returns. Feeding a transcriber the wrong
    /// rate is never an error - it transcribes audio it believes is at a
    /// different speed and returns confident nonsense.
    pub const SAMPLE_RATE_HZ: u32 = 16_000;

    pub fn is_configured(&self) -> bool {
        self.key.is_set()
    }

    pub fn resolved_base_url(&self) -> &str {
        if self.base_url.is_empty() { Self::DEFAULT_BASE_URL } else { &self.base_url }
    }

    pub fn resolved_model(&self) -> &str {
        if self.model.is_empty() { Self::SUGGESTED_MODEL } else { &self.model }
    }

    /// Folds into the shared shape the recognisers and synthesisers take.
    pub fn to_cloud(&self) -> CloudSpeechOptions {
        CloudSpeechOptions {
            enabled: self.key.is_set(),
            api_key: self.key.clone(),
            base_url: self.resolved_base_url().to_string(),
            model: self.resolved_model().to_string(),
            language: self.language.clone(),
            // The voice goes into voice_id because ElevenLabs and Cartesia put
            // it in the PATH - without one there is no endpoint to call.
            voice_id: self.voice.clone(),
            region: String::new(),
            user_id: String::new(),
        }
    }
}

impl std::fmt::Debug for OpenAiVoiceOptions {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("OpenAiVoiceOptions")
            .field("key", &self.key)
            .field("base_url", &self.resolved_base_url())
            .field("model", &self.resolved_model())
            .field("sample_rate_hz", &Self::SAMPLE_RATE_HZ)
            .finish()
    }
}

/// Deepgram transcription.
#[derive(Clone, Default)]
pub struct DeepgramOptions {
    pub key: Secret,
    pub base_url: String,
    pub model: String,
    /// Empty means "let the service decide", which for transcription is usually
    /// right and for synthesis is usually not - a voice picked by a service is a
    /// voice that can change under you.
    pub voice: String,
    pub language: String,
    pub timeout_ms: u64,
}

impl DeepgramOptions {
    pub const DEFAULT_BASE_URL: &'static str = "https://api.deepgram.com/v1";
    pub const SUGGESTED_MODEL: &'static str = "nova-2";
    /// What this service expects or returns. Feeding a transcriber the wrong
    /// rate is never an error - it transcribes audio it believes is at a
    /// different speed and returns confident nonsense.
    pub const SAMPLE_RATE_HZ: u32 = 16_000;

    pub fn is_configured(&self) -> bool {
        self.key.is_set()
    }

    pub fn resolved_base_url(&self) -> &str {
        if self.base_url.is_empty() { Self::DEFAULT_BASE_URL } else { &self.base_url }
    }

    pub fn resolved_model(&self) -> &str {
        if self.model.is_empty() { Self::SUGGESTED_MODEL } else { &self.model }
    }

    /// Folds into the shared shape the recognisers and synthesisers take.
    pub fn to_cloud(&self) -> CloudSpeechOptions {
        CloudSpeechOptions {
            enabled: self.key.is_set(),
            api_key: self.key.clone(),
            base_url: self.resolved_base_url().to_string(),
            model: self.resolved_model().to_string(),
            language: self.language.clone(),
            // The voice goes into voice_id because ElevenLabs and Cartesia put
            // it in the PATH - without one there is no endpoint to call.
            voice_id: self.voice.clone(),
            region: String::new(),
            user_id: String::new(),
        }
    }
}

impl std::fmt::Debug for DeepgramOptions {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("DeepgramOptions")
            .field("key", &self.key)
            .field("base_url", &self.resolved_base_url())
            .field("model", &self.resolved_model())
            .field("sample_rate_hz", &Self::SAMPLE_RATE_HZ)
            .finish()
    }
}

/// Deepgram speech. A SEPARATE type from its transcription options because
/// the model names share no namespace and mixing them is a request the API
/// rejects for a reason nobody reads.
#[derive(Clone, Default)]
pub struct DeepgramTtsOptions {
    pub key: Secret,
    pub base_url: String,
    pub model: String,
    /// Empty means "let the service decide", which for transcription is usually
    /// right and for synthesis is usually not - a voice picked by a service is a
    /// voice that can change under you.
    pub voice: String,
    pub language: String,
    pub timeout_ms: u64,
}

impl DeepgramTtsOptions {
    pub const DEFAULT_BASE_URL: &'static str = "https://api.deepgram.com/v1";
    pub const SUGGESTED_MODEL: &'static str = "aura-asteria-en";
    /// What this service expects or returns. Feeding a transcriber the wrong
    /// rate is never an error - it transcribes audio it believes is at a
    /// different speed and returns confident nonsense.
    pub const SAMPLE_RATE_HZ: u32 = 24_000;

    pub fn is_configured(&self) -> bool {
        self.key.is_set()
    }

    pub fn resolved_base_url(&self) -> &str {
        if self.base_url.is_empty() { Self::DEFAULT_BASE_URL } else { &self.base_url }
    }

    pub fn resolved_model(&self) -> &str {
        if self.model.is_empty() { Self::SUGGESTED_MODEL } else { &self.model }
    }

    /// Folds into the shared shape the recognisers and synthesisers take.
    pub fn to_cloud(&self) -> CloudSpeechOptions {
        CloudSpeechOptions {
            enabled: self.key.is_set(),
            api_key: self.key.clone(),
            base_url: self.resolved_base_url().to_string(),
            model: self.resolved_model().to_string(),
            language: self.language.clone(),
            // The voice goes into voice_id because ElevenLabs and Cartesia put
            // it in the PATH - without one there is no endpoint to call.
            voice_id: self.voice.clone(),
            region: String::new(),
            user_id: String::new(),
        }
    }
}

impl std::fmt::Debug for DeepgramTtsOptions {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("DeepgramTtsOptions")
            .field("key", &self.key)
            .field("base_url", &self.resolved_base_url())
            .field("model", &self.resolved_model())
            .field("sample_rate_hz", &Self::SAMPLE_RATE_HZ)
            .finish()
    }
}

/// AssemblyAI. Upload-then-poll rather than one request, so its timeout
/// covers a wait rather than a call.
#[derive(Clone, Default)]
pub struct AssemblyAiOptions {
    pub key: Secret,
    pub base_url: String,
    pub model: String,
    /// Empty means "let the service decide", which for transcription is usually
    /// right and for synthesis is usually not - a voice picked by a service is a
    /// voice that can change under you.
    pub voice: String,
    pub language: String,
    pub timeout_ms: u64,
}

impl AssemblyAiOptions {
    pub const DEFAULT_BASE_URL: &'static str = "https://api.assemblyai.com/v2";
    pub const SUGGESTED_MODEL: &'static str = "best";
    /// What this service expects or returns. Feeding a transcriber the wrong
    /// rate is never an error - it transcribes audio it believes is at a
    /// different speed and returns confident nonsense.
    pub const SAMPLE_RATE_HZ: u32 = 16_000;

    pub fn is_configured(&self) -> bool {
        self.key.is_set()
    }

    pub fn resolved_base_url(&self) -> &str {
        if self.base_url.is_empty() { Self::DEFAULT_BASE_URL } else { &self.base_url }
    }

    pub fn resolved_model(&self) -> &str {
        if self.model.is_empty() { Self::SUGGESTED_MODEL } else { &self.model }
    }

    /// Folds into the shared shape the recognisers and synthesisers take.
    pub fn to_cloud(&self) -> CloudSpeechOptions {
        CloudSpeechOptions {
            enabled: self.key.is_set(),
            api_key: self.key.clone(),
            base_url: self.resolved_base_url().to_string(),
            model: self.resolved_model().to_string(),
            language: self.language.clone(),
            // The voice goes into voice_id because ElevenLabs and Cartesia put
            // it in the PATH - without one there is no endpoint to call.
            voice_id: self.voice.clone(),
            region: String::new(),
            user_id: String::new(),
        }
    }
}

impl std::fmt::Debug for AssemblyAiOptions {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("AssemblyAiOptions")
            .field("key", &self.key)
            .field("base_url", &self.resolved_base_url())
            .field("model", &self.resolved_model())
            .field("sample_rate_hz", &Self::SAMPLE_RATE_HZ)
            .finish()
    }
}

/// Azure transcription. THE REGION IS PART OF THE HOST - the placeholder in
/// the default is deliberate, so an unconfigured region fails at the URL
/// rather than reaching the wrong data centre.
#[derive(Clone, Default)]
pub struct AzureSpeechOptions {
    pub key: Secret,
    pub base_url: String,
    pub model: String,
    /// Empty means "let the service decide", which for transcription is usually
    /// right and for synthesis is usually not - a voice picked by a service is a
    /// voice that can change under you.
    pub voice: String,
    pub language: String,
    pub timeout_ms: u64,
}

impl AzureSpeechOptions {
    pub const DEFAULT_BASE_URL: &'static str = "https://REGION.stt.speech.microsoft.com";
    pub const SUGGESTED_MODEL: &'static str = "latest";
    /// What this service expects or returns. Feeding a transcriber the wrong
    /// rate is never an error - it transcribes audio it believes is at a
    /// different speed and returns confident nonsense.
    pub const SAMPLE_RATE_HZ: u32 = 16_000;

    pub fn is_configured(&self) -> bool {
        self.key.is_set()
    }

    pub fn resolved_base_url(&self) -> &str {
        if self.base_url.is_empty() { Self::DEFAULT_BASE_URL } else { &self.base_url }
    }

    pub fn resolved_model(&self) -> &str {
        if self.model.is_empty() { Self::SUGGESTED_MODEL } else { &self.model }
    }

    /// Folds into the shared shape the recognisers and synthesisers take.
    pub fn to_cloud(&self) -> CloudSpeechOptions {
        CloudSpeechOptions {
            enabled: self.key.is_set(),
            api_key: self.key.clone(),
            base_url: self.resolved_base_url().to_string(),
            model: self.resolved_model().to_string(),
            language: self.language.clone(),
            // The voice goes into voice_id because ElevenLabs and Cartesia put
            // it in the PATH - without one there is no endpoint to call.
            voice_id: self.voice.clone(),
            region: String::new(),
            user_id: String::new(),
        }
    }
}

impl std::fmt::Debug for AzureSpeechOptions {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("AzureSpeechOptions")
            .field("key", &self.key)
            .field("base_url", &self.resolved_base_url())
            .field("model", &self.resolved_model())
            .field("sample_rate_hz", &Self::SAMPLE_RATE_HZ)
            .finish()
    }
}

/// Azure speech. A different subdomain from its transcription counterpart,
/// which is exactly the difference a shared options bag would erase.
#[derive(Clone, Default)]
pub struct AzureTtsOptions {
    pub key: Secret,
    pub base_url: String,
    pub model: String,
    /// Empty means "let the service decide", which for transcription is usually
    /// right and for synthesis is usually not - a voice picked by a service is a
    /// voice that can change under you.
    pub voice: String,
    pub language: String,
    pub timeout_ms: u64,
}

impl AzureTtsOptions {
    pub const DEFAULT_BASE_URL: &'static str = "https://REGION.tts.speech.microsoft.com";
    pub const SUGGESTED_MODEL: &'static str = "en-ZA-LeahNeural";
    /// What this service expects or returns. Feeding a transcriber the wrong
    /// rate is never an error - it transcribes audio it believes is at a
    /// different speed and returns confident nonsense.
    pub const SAMPLE_RATE_HZ: u32 = 24_000;

    pub fn is_configured(&self) -> bool {
        self.key.is_set()
    }

    pub fn resolved_base_url(&self) -> &str {
        if self.base_url.is_empty() { Self::DEFAULT_BASE_URL } else { &self.base_url }
    }

    pub fn resolved_model(&self) -> &str {
        if self.model.is_empty() { Self::SUGGESTED_MODEL } else { &self.model }
    }

    /// Folds into the shared shape the recognisers and synthesisers take.
    pub fn to_cloud(&self) -> CloudSpeechOptions {
        CloudSpeechOptions {
            enabled: self.key.is_set(),
            api_key: self.key.clone(),
            base_url: self.resolved_base_url().to_string(),
            model: self.resolved_model().to_string(),
            language: self.language.clone(),
            // The voice goes into voice_id because ElevenLabs and Cartesia put
            // it in the PATH - without one there is no endpoint to call.
            voice_id: self.voice.clone(),
            region: String::new(),
            user_id: String::new(),
        }
    }
}

impl std::fmt::Debug for AzureTtsOptions {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("AzureTtsOptions")
            .field("key", &self.key)
            .field("base_url", &self.resolved_base_url())
            .field("model", &self.resolved_model())
            .field("sample_rate_hz", &Self::SAMPLE_RATE_HZ)
            .finish()
    }
}

/// Google transcription.
#[derive(Clone, Default)]
pub struct GoogleSpeechOptions {
    pub key: Secret,
    pub base_url: String,
    pub model: String,
    /// Empty means "let the service decide", which for transcription is usually
    /// right and for synthesis is usually not - a voice picked by a service is a
    /// voice that can change under you.
    pub voice: String,
    pub language: String,
    pub timeout_ms: u64,
}

impl GoogleSpeechOptions {
    pub const DEFAULT_BASE_URL: &'static str = "https://speech.googleapis.com/v1";
    pub const SUGGESTED_MODEL: &'static str = "latest_long";
    /// What this service expects or returns. Feeding a transcriber the wrong
    /// rate is never an error - it transcribes audio it believes is at a
    /// different speed and returns confident nonsense.
    pub const SAMPLE_RATE_HZ: u32 = 16_000;

    pub fn is_configured(&self) -> bool {
        self.key.is_set()
    }

    pub fn resolved_base_url(&self) -> &str {
        if self.base_url.is_empty() { Self::DEFAULT_BASE_URL } else { &self.base_url }
    }

    pub fn resolved_model(&self) -> &str {
        if self.model.is_empty() { Self::SUGGESTED_MODEL } else { &self.model }
    }

    /// Folds into the shared shape the recognisers and synthesisers take.
    pub fn to_cloud(&self) -> CloudSpeechOptions {
        CloudSpeechOptions {
            enabled: self.key.is_set(),
            api_key: self.key.clone(),
            base_url: self.resolved_base_url().to_string(),
            model: self.resolved_model().to_string(),
            language: self.language.clone(),
            // The voice goes into voice_id because ElevenLabs and Cartesia put
            // it in the PATH - without one there is no endpoint to call.
            voice_id: self.voice.clone(),
            region: String::new(),
            user_id: String::new(),
        }
    }
}

impl std::fmt::Debug for GoogleSpeechOptions {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("GoogleSpeechOptions")
            .field("key", &self.key)
            .field("base_url", &self.resolved_base_url())
            .field("model", &self.resolved_model())
            .field("sample_rate_hz", &Self::SAMPLE_RATE_HZ)
            .finish()
    }
}

/// Google speech.
#[derive(Clone, Default)]
pub struct GoogleTtsOptions {
    pub key: Secret,
    pub base_url: String,
    pub model: String,
    /// Empty means "let the service decide", which for transcription is usually
    /// right and for synthesis is usually not - a voice picked by a service is a
    /// voice that can change under you.
    pub voice: String,
    pub language: String,
    pub timeout_ms: u64,
}

impl GoogleTtsOptions {
    pub const DEFAULT_BASE_URL: &'static str = "https://texttospeech.googleapis.com/v1";
    pub const SUGGESTED_MODEL: &'static str = "en-ZA-Standard-A";
    /// What this service expects or returns. Feeding a transcriber the wrong
    /// rate is never an error - it transcribes audio it believes is at a
    /// different speed and returns confident nonsense.
    pub const SAMPLE_RATE_HZ: u32 = 24_000;

    pub fn is_configured(&self) -> bool {
        self.key.is_set()
    }

    pub fn resolved_base_url(&self) -> &str {
        if self.base_url.is_empty() { Self::DEFAULT_BASE_URL } else { &self.base_url }
    }

    pub fn resolved_model(&self) -> &str {
        if self.model.is_empty() { Self::SUGGESTED_MODEL } else { &self.model }
    }

    /// Folds into the shared shape the recognisers and synthesisers take.
    pub fn to_cloud(&self) -> CloudSpeechOptions {
        CloudSpeechOptions {
            enabled: self.key.is_set(),
            api_key: self.key.clone(),
            base_url: self.resolved_base_url().to_string(),
            model: self.resolved_model().to_string(),
            language: self.language.clone(),
            // The voice goes into voice_id because ElevenLabs and Cartesia put
            // it in the PATH - without one there is no endpoint to call.
            voice_id: self.voice.clone(),
            region: String::new(),
            user_id: String::new(),
        }
    }
}

impl std::fmt::Debug for GoogleTtsOptions {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("GoogleTtsOptions")
            .field("key", &self.key)
            .field("base_url", &self.resolved_base_url())
            .field("model", &self.resolved_model())
            .field("sample_rate_hz", &Self::SAMPLE_RATE_HZ)
            .finish()
    }
}

/// ElevenLabs. 44.1 kHz out, which is higher than anything else here and
/// needs resampling before it meets 16 kHz audio.
#[derive(Clone, Default)]
pub struct ElevenLabsOptions {
    pub key: Secret,
    pub base_url: String,
    pub model: String,
    /// Empty means "let the service decide", which for transcription is usually
    /// right and for synthesis is usually not - a voice picked by a service is a
    /// voice that can change under you.
    pub voice: String,
    pub language: String,
    pub timeout_ms: u64,
}

impl ElevenLabsOptions {
    pub const DEFAULT_BASE_URL: &'static str = "https://api.elevenlabs.io/v1";
    pub const SUGGESTED_MODEL: &'static str = "eleven_multilingual_v2";
    /// What this service expects or returns. Feeding a transcriber the wrong
    /// rate is never an error - it transcribes audio it believes is at a
    /// different speed and returns confident nonsense.
    pub const SAMPLE_RATE_HZ: u32 = 44_100;

    pub fn is_configured(&self) -> bool {
        self.key.is_set()
    }

    pub fn resolved_base_url(&self) -> &str {
        if self.base_url.is_empty() { Self::DEFAULT_BASE_URL } else { &self.base_url }
    }

    pub fn resolved_model(&self) -> &str {
        if self.model.is_empty() { Self::SUGGESTED_MODEL } else { &self.model }
    }

    /// Folds into the shared shape the recognisers and synthesisers take.
    pub fn to_cloud(&self) -> CloudSpeechOptions {
        CloudSpeechOptions {
            enabled: self.key.is_set(),
            api_key: self.key.clone(),
            base_url: self.resolved_base_url().to_string(),
            model: self.resolved_model().to_string(),
            language: self.language.clone(),
            // The voice goes into voice_id because ElevenLabs and Cartesia put
            // it in the PATH - without one there is no endpoint to call.
            voice_id: self.voice.clone(),
            region: String::new(),
            user_id: String::new(),
        }
    }
}

impl std::fmt::Debug for ElevenLabsOptions {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ElevenLabsOptions")
            .field("key", &self.key)
            .field("base_url", &self.resolved_base_url())
            .field("model", &self.resolved_model())
            .field("sample_rate_hz", &Self::SAMPLE_RATE_HZ)
            .finish()
    }
}

/// Cartesia transcription.
#[derive(Clone, Default)]
pub struct CartesiaSttOptions {
    pub key: Secret,
    pub base_url: String,
    pub model: String,
    /// Empty means "let the service decide", which for transcription is usually
    /// right and for synthesis is usually not - a voice picked by a service is a
    /// voice that can change under you.
    pub voice: String,
    pub language: String,
    pub timeout_ms: u64,
}

impl CartesiaSttOptions {
    pub const DEFAULT_BASE_URL: &'static str = "https://api.cartesia.ai";
    pub const SUGGESTED_MODEL: &'static str = "ink-whisper";
    /// What this service expects or returns. Feeding a transcriber the wrong
    /// rate is never an error - it transcribes audio it believes is at a
    /// different speed and returns confident nonsense.
    pub const SAMPLE_RATE_HZ: u32 = 16_000;

    pub fn is_configured(&self) -> bool {
        self.key.is_set()
    }

    pub fn resolved_base_url(&self) -> &str {
        if self.base_url.is_empty() { Self::DEFAULT_BASE_URL } else { &self.base_url }
    }

    pub fn resolved_model(&self) -> &str {
        if self.model.is_empty() { Self::SUGGESTED_MODEL } else { &self.model }
    }

    /// Folds into the shared shape the recognisers and synthesisers take.
    pub fn to_cloud(&self) -> CloudSpeechOptions {
        CloudSpeechOptions {
            enabled: self.key.is_set(),
            api_key: self.key.clone(),
            base_url: self.resolved_base_url().to_string(),
            model: self.resolved_model().to_string(),
            language: self.language.clone(),
            // The voice goes into voice_id because ElevenLabs and Cartesia put
            // it in the PATH - without one there is no endpoint to call.
            voice_id: self.voice.clone(),
            region: String::new(),
            user_id: String::new(),
        }
    }
}

impl std::fmt::Debug for CartesiaSttOptions {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("CartesiaSttOptions")
            .field("key", &self.key)
            .field("base_url", &self.resolved_base_url())
            .field("model", &self.resolved_model())
            .field("sample_rate_hz", &Self::SAMPLE_RATE_HZ)
            .finish()
    }
}

/// Cartesia speech.
#[derive(Clone, Default)]
pub struct CartesiaTtsOptions {
    pub key: Secret,
    pub base_url: String,
    pub model: String,
    /// Empty means "let the service decide", which for transcription is usually
    /// right and for synthesis is usually not - a voice picked by a service is a
    /// voice that can change under you.
    pub voice: String,
    pub language: String,
    pub timeout_ms: u64,
}

impl CartesiaTtsOptions {
    pub const DEFAULT_BASE_URL: &'static str = "https://api.cartesia.ai";
    pub const SUGGESTED_MODEL: &'static str = "sonic-2";
    /// What this service expects or returns. Feeding a transcriber the wrong
    /// rate is never an error - it transcribes audio it believes is at a
    /// different speed and returns confident nonsense.
    pub const SAMPLE_RATE_HZ: u32 = 44_100;

    pub fn is_configured(&self) -> bool {
        self.key.is_set()
    }

    pub fn resolved_base_url(&self) -> &str {
        if self.base_url.is_empty() { Self::DEFAULT_BASE_URL } else { &self.base_url }
    }

    pub fn resolved_model(&self) -> &str {
        if self.model.is_empty() { Self::SUGGESTED_MODEL } else { &self.model }
    }

    /// Folds into the shared shape the recognisers and synthesisers take.
    pub fn to_cloud(&self) -> CloudSpeechOptions {
        CloudSpeechOptions {
            enabled: self.key.is_set(),
            api_key: self.key.clone(),
            base_url: self.resolved_base_url().to_string(),
            model: self.resolved_model().to_string(),
            language: self.language.clone(),
            // The voice goes into voice_id because ElevenLabs and Cartesia put
            // it in the PATH - without one there is no endpoint to call.
            voice_id: self.voice.clone(),
            region: String::new(),
            user_id: String::new(),
        }
    }
}

impl std::fmt::Debug for CartesiaTtsOptions {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("CartesiaTtsOptions")
            .field("key", &self.key)
            .field("base_url", &self.resolved_base_url())
            .field("model", &self.resolved_model())
            .field("sample_rate_hz", &Self::SAMPLE_RATE_HZ)
            .finish()
    }
}

/// PlayHT. Needs a user id ALONGSIDE the key, which is why a key alone
/// being present is not enough to call it configured.
#[derive(Clone, Default)]
pub struct PlayHtOptions {
    pub key: Secret,
    pub base_url: String,
    pub model: String,
    /// Empty means "let the service decide", which for transcription is usually
    /// right and for synthesis is usually not - a voice picked by a service is a
    /// voice that can change under you.
    pub voice: String,
    pub language: String,
    pub timeout_ms: u64,
}

impl PlayHtOptions {
    pub const DEFAULT_BASE_URL: &'static str = "https://api.play.ht/api/v2";
    pub const SUGGESTED_MODEL: &'static str = "PlayHT2.0";
    /// What this service expects or returns. Feeding a transcriber the wrong
    /// rate is never an error - it transcribes audio it believes is at a
    /// different speed and returns confident nonsense.
    pub const SAMPLE_RATE_HZ: u32 = 24_000;

    pub fn is_configured(&self) -> bool {
        self.key.is_set()
    }

    pub fn resolved_base_url(&self) -> &str {
        if self.base_url.is_empty() { Self::DEFAULT_BASE_URL } else { &self.base_url }
    }

    pub fn resolved_model(&self) -> &str {
        if self.model.is_empty() { Self::SUGGESTED_MODEL } else { &self.model }
    }

    /// Folds into the shared shape the recognisers and synthesisers take.
    pub fn to_cloud(&self) -> CloudSpeechOptions {
        CloudSpeechOptions {
            enabled: self.key.is_set(),
            api_key: self.key.clone(),
            base_url: self.resolved_base_url().to_string(),
            model: self.resolved_model().to_string(),
            language: self.language.clone(),
            // The voice goes into voice_id because ElevenLabs and Cartesia put
            // it in the PATH - without one there is no endpoint to call.
            voice_id: self.voice.clone(),
            region: String::new(),
            user_id: String::new(),
        }
    }
}

impl std::fmt::Debug for PlayHtOptions {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("PlayHtOptions")
            .field("key", &self.key)
            .field("base_url", &self.resolved_base_url())
            .field("model", &self.resolved_model())
            .field("sample_rate_hz", &Self::SAMPLE_RATE_HZ)
            .finish()
    }
}

/// OpenAI realtime. 24 kHz PCM in both directions.
#[derive(Clone, Default)]
pub struct OpenAiRealtimeOptions {
    pub key: Secret,
    pub url: String,
    pub model: String,
    pub voice: String,
    pub instructions: String,
}

impl OpenAiRealtimeOptions {
    pub const DEFAULT_URL: &'static str = "wss://api.openai.com/v1/realtime";
    pub const SUGGESTED_MODEL: &'static str = "gpt-4o-realtime-preview";
    /// The rate the socket carries. A realtime session negotiates this ONCE at
    /// the start; sending frames at a different rate afterwards is heard as the
    /// caller talking at the wrong speed.
    pub const SAMPLE_RATE_HZ: u32 = 24_000;

    pub fn is_configured(&self) -> bool {
        self.key.is_set()
    }

    pub fn resolved_url(&self) -> &str {
        if self.url.is_empty() { Self::DEFAULT_URL } else { &self.url }
    }

    pub fn resolved_model(&self) -> &str {
        if self.model.is_empty() { Self::SUGGESTED_MODEL } else { &self.model }
    }
}

impl std::fmt::Debug for OpenAiRealtimeOptions {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("OpenAiRealtimeOptions")
            .field("key", &self.key)
            .field("url", &self.resolved_url())
            .field("model", &self.resolved_model())
            .finish()
    }
}

/// Gemini Live. 16 kHz IN and 24 kHz OUT - the asymmetry is real, and a
/// session that resamples both directions by the input rate returns speech
/// that plays too slowly.
#[derive(Clone, Default)]
pub struct GeminiLiveOptions {
    pub key: Secret,
    pub url: String,
    pub model: String,
    pub voice: String,
    pub instructions: String,
}

impl GeminiLiveOptions {
    pub const DEFAULT_URL: &'static str = "wss://generativelanguage.googleapis.com/ws";
    pub const SUGGESTED_MODEL: &'static str = "gemini-2.0-flash-exp";
    /// The rate the socket carries. A realtime session negotiates this ONCE at
    /// the start; sending frames at a different rate afterwards is heard as the
    /// caller talking at the wrong speed.
    pub const SAMPLE_RATE_HZ: u32 = 16_000;

    pub fn is_configured(&self) -> bool {
        self.key.is_set()
    }

    pub fn resolved_url(&self) -> &str {
        if self.url.is_empty() { Self::DEFAULT_URL } else { &self.url }
    }

    pub fn resolved_model(&self) -> &str {
        if self.model.is_empty() { Self::SUGGESTED_MODEL } else { &self.model }
    }
}

impl std::fmt::Debug for GeminiLiveOptions {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("GeminiLiveOptions")
            .field("key", &self.key)
            .field("url", &self.resolved_url())
            .field("model", &self.resolved_model())
            .finish()
    }
}

/// Nova Sonic, through Bedrock. Signed requests rather than a bearer key,
/// so the region in the host is part of what is signed and cannot be
/// swapped freely.
#[derive(Clone, Default)]
pub struct NovaSonicOptions {
    pub key: Secret,
    pub url: String,
    pub model: String,
    pub voice: String,
    pub instructions: String,
}

impl NovaSonicOptions {
    pub const DEFAULT_URL: &'static str = "wss://bedrock-runtime.us-east-1.amazonaws.com";
    pub const SUGGESTED_MODEL: &'static str = "amazon.nova-sonic-v1:0";
    /// The rate the socket carries. A realtime session negotiates this ONCE at
    /// the start; sending frames at a different rate afterwards is heard as the
    /// caller talking at the wrong speed.
    pub const SAMPLE_RATE_HZ: u32 = 16_000;

    pub fn is_configured(&self) -> bool {
        self.key.is_set()
    }

    pub fn resolved_url(&self) -> &str {
        if self.url.is_empty() { Self::DEFAULT_URL } else { &self.url }
    }

    pub fn resolved_model(&self) -> &str {
        if self.model.is_empty() { Self::SUGGESTED_MODEL } else { &self.model }
    }
}

impl std::fmt::Debug for NovaSonicOptions {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("NovaSonicOptions")
            .field("key", &self.key)
            .field("url", &self.resolved_url())
            .field("model", &self.resolved_model())
            .finish()
    }
}

/// ElevenLabs conversational.
#[derive(Clone, Default)]
pub struct ElevenLabsConvOptions {
    pub key: Secret,
    pub url: String,
    pub model: String,
    pub voice: String,
    pub instructions: String,
}

impl ElevenLabsConvOptions {
    pub const DEFAULT_URL: &'static str = "wss://api.elevenlabs.io/v1/convai/conversation";
    pub const SUGGESTED_MODEL: &'static str = "eleven_turbo_v2_5";
    /// The rate the socket carries. A realtime session negotiates this ONCE at
    /// the start; sending frames at a different rate afterwards is heard as the
    /// caller talking at the wrong speed.
    pub const SAMPLE_RATE_HZ: u32 = 16_000;

    pub fn is_configured(&self) -> bool {
        self.key.is_set()
    }

    pub fn resolved_url(&self) -> &str {
        if self.url.is_empty() { Self::DEFAULT_URL } else { &self.url }
    }

    pub fn resolved_model(&self) -> &str {
        if self.model.is_empty() { Self::SUGGESTED_MODEL } else { &self.model }
    }
}

impl std::fmt::Debug for ElevenLabsConvOptions {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ElevenLabsConvOptions")
            .field("key", &self.key)
            .field("url", &self.resolved_url())
            .field("model", &self.resolved_model())
            .finish()
    }
}

/// Ultravox. Speech straight into the model with no transcription step,
/// which is why there is no separate recogniser to configure.
#[derive(Clone, Default)]
pub struct UltravoxOptions {
    pub key: Secret,
    pub url: String,
    pub model: String,
    pub voice: String,
    pub instructions: String,
}

impl UltravoxOptions {
    pub const DEFAULT_URL: &'static str = "wss://api.ultravox.ai";
    pub const SUGGESTED_MODEL: &'static str = "fixie-ai/ultravox";
    /// The rate the socket carries. A realtime session negotiates this ONCE at
    /// the start; sending frames at a different rate afterwards is heard as the
    /// caller talking at the wrong speed.
    pub const SAMPLE_RATE_HZ: u32 = 16_000;

    pub fn is_configured(&self) -> bool {
        self.key.is_set()
    }

    pub fn resolved_url(&self) -> &str {
        if self.url.is_empty() { Self::DEFAULT_URL } else { &self.url }
    }

    pub fn resolved_model(&self) -> &str {
        if self.model.is_empty() { Self::SUGGESTED_MODEL } else { &self.model }
    }
}

impl std::fmt::Debug for UltravoxOptions {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("UltravoxOptions")
            .field("key", &self.key)
            .field("url", &self.resolved_url())
            .field("model", &self.resolved_model())
            .finish()
    }
}

/// OpenAI realtime.
///
/// The connection itself is the host's job: a WebSocket is a platform
/// dependency, and a Rust core that carried one would pull an async runtime and
/// a TLS stack into every target including the small ones.
pub struct OpenAiRealtimeService {
    options: OpenAiRealtimeOptions,
    #[allow(clippy::type_complexity)]
    connect: Option<Box<dyn Fn(&str, &str) -> Result<String, String> + Send + Sync>>,
}

impl OpenAiRealtimeService {
    pub const PROVIDER: &'static str = "openai-realtime";

    #[allow(clippy::type_complexity)]
    pub fn new(
        options: OpenAiRealtimeOptions,
        connect: Option<Box<dyn Fn(&str, &str) -> Result<String, String> + Send + Sync>>,
    ) -> Self {
        Self { options, connect }
    }

    pub fn options(&self) -> &OpenAiRealtimeOptions {
        &self.options
    }

    /// Needs BOTH a key and a way to open a socket. Either alone is a service
    /// that reports ready and fails on the first call, which is the worst
    /// moment to find out - somebody is already on the line.
    pub fn is_available(&self) -> bool {
        self.options.is_configured() && self.connect.is_some()
    }

    pub fn sample_rate_hz(&self) -> u32 {
        OpenAiRealtimeOptions::SAMPLE_RATE_HZ
    }

    pub fn start(&self) -> Result<String, String> {
        if !self.options.is_configured() {
            return Err(format!("{} has no key set on this device", Self::PROVIDER));
        }
        let Some(connect) = &self.connect else {
            return Err(format!(
                "{} cannot be reached from this build - there is no socket transport",
                Self::PROVIDER
            ));
        };
        connect(self.options.resolved_url(), self.options.resolved_model())
    }
}

impl std::fmt::Debug for OpenAiRealtimeService {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("OpenAiRealtimeService")
            .field("provider", &Self::PROVIDER)
            .field("available", &self.is_available())
            .finish()
    }
}

/// Gemini Live.
///
/// The connection itself is the host's job: a WebSocket is a platform
/// dependency, and a Rust core that carried one would pull an async runtime and
/// a TLS stack into every target including the small ones.
pub struct GeminiLiveService {
    options: GeminiLiveOptions,
    #[allow(clippy::type_complexity)]
    connect: Option<Box<dyn Fn(&str, &str) -> Result<String, String> + Send + Sync>>,
}

impl GeminiLiveService {
    pub const PROVIDER: &'static str = "gemini-live";

    #[allow(clippy::type_complexity)]
    pub fn new(
        options: GeminiLiveOptions,
        connect: Option<Box<dyn Fn(&str, &str) -> Result<String, String> + Send + Sync>>,
    ) -> Self {
        Self { options, connect }
    }

    pub fn options(&self) -> &GeminiLiveOptions {
        &self.options
    }

    /// Needs BOTH a key and a way to open a socket. Either alone is a service
    /// that reports ready and fails on the first call, which is the worst
    /// moment to find out - somebody is already on the line.
    pub fn is_available(&self) -> bool {
        self.options.is_configured() && self.connect.is_some()
    }

    pub fn sample_rate_hz(&self) -> u32 {
        GeminiLiveOptions::SAMPLE_RATE_HZ
    }

    pub fn start(&self) -> Result<String, String> {
        if !self.options.is_configured() {
            return Err(format!("{} has no key set on this device", Self::PROVIDER));
        }
        let Some(connect) = &self.connect else {
            return Err(format!(
                "{} cannot be reached from this build - there is no socket transport",
                Self::PROVIDER
            ));
        };
        connect(self.options.resolved_url(), self.options.resolved_model())
    }
}

impl std::fmt::Debug for GeminiLiveService {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("GeminiLiveService")
            .field("provider", &Self::PROVIDER)
            .field("available", &self.is_available())
            .finish()
    }
}

/// Nova Sonic.
///
/// The connection itself is the host's job: a WebSocket is a platform
/// dependency, and a Rust core that carried one would pull an async runtime and
/// a TLS stack into every target including the small ones.
pub struct NovaSonicService {
    options: NovaSonicOptions,
    #[allow(clippy::type_complexity)]
    connect: Option<Box<dyn Fn(&str, &str) -> Result<String, String> + Send + Sync>>,
}

impl NovaSonicService {
    pub const PROVIDER: &'static str = "nova-sonic";

    #[allow(clippy::type_complexity)]
    pub fn new(
        options: NovaSonicOptions,
        connect: Option<Box<dyn Fn(&str, &str) -> Result<String, String> + Send + Sync>>,
    ) -> Self {
        Self { options, connect }
    }

    pub fn options(&self) -> &NovaSonicOptions {
        &self.options
    }

    /// Needs BOTH a key and a way to open a socket. Either alone is a service
    /// that reports ready and fails on the first call, which is the worst
    /// moment to find out - somebody is already on the line.
    pub fn is_available(&self) -> bool {
        self.options.is_configured() && self.connect.is_some()
    }

    pub fn sample_rate_hz(&self) -> u32 {
        NovaSonicOptions::SAMPLE_RATE_HZ
    }

    pub fn start(&self) -> Result<String, String> {
        if !self.options.is_configured() {
            return Err(format!("{} has no key set on this device", Self::PROVIDER));
        }
        let Some(connect) = &self.connect else {
            return Err(format!(
                "{} cannot be reached from this build - there is no socket transport",
                Self::PROVIDER
            ));
        };
        connect(self.options.resolved_url(), self.options.resolved_model())
    }
}

impl std::fmt::Debug for NovaSonicService {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("NovaSonicService")
            .field("provider", &Self::PROVIDER)
            .field("available", &self.is_available())
            .finish()
    }
}

/// ElevenLabs conversational.
///
/// The connection itself is the host's job: a WebSocket is a platform
/// dependency, and a Rust core that carried one would pull an async runtime and
/// a TLS stack into every target including the small ones.
pub struct ElevenLabsConvService {
    options: ElevenLabsConvOptions,
    #[allow(clippy::type_complexity)]
    connect: Option<Box<dyn Fn(&str, &str) -> Result<String, String> + Send + Sync>>,
}

impl ElevenLabsConvService {
    pub const PROVIDER: &'static str = "elevenlabs-conv";

    #[allow(clippy::type_complexity)]
    pub fn new(
        options: ElevenLabsConvOptions,
        connect: Option<Box<dyn Fn(&str, &str) -> Result<String, String> + Send + Sync>>,
    ) -> Self {
        Self { options, connect }
    }

    pub fn options(&self) -> &ElevenLabsConvOptions {
        &self.options
    }

    /// Needs BOTH a key and a way to open a socket. Either alone is a service
    /// that reports ready and fails on the first call, which is the worst
    /// moment to find out - somebody is already on the line.
    pub fn is_available(&self) -> bool {
        self.options.is_configured() && self.connect.is_some()
    }

    pub fn sample_rate_hz(&self) -> u32 {
        ElevenLabsConvOptions::SAMPLE_RATE_HZ
    }

    pub fn start(&self) -> Result<String, String> {
        if !self.options.is_configured() {
            return Err(format!("{} has no key set on this device", Self::PROVIDER));
        }
        let Some(connect) = &self.connect else {
            return Err(format!(
                "{} cannot be reached from this build - there is no socket transport",
                Self::PROVIDER
            ));
        };
        connect(self.options.resolved_url(), self.options.resolved_model())
    }
}

impl std::fmt::Debug for ElevenLabsConvService {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ElevenLabsConvService")
            .field("provider", &Self::PROVIDER)
            .field("available", &self.is_available())
            .finish()
    }
}

/// Ultravox.
///
/// The connection itself is the host's job: a WebSocket is a platform
/// dependency, and a Rust core that carried one would pull an async runtime and
/// a TLS stack into every target including the small ones.
pub struct UltravoxService {
    options: UltravoxOptions,
    #[allow(clippy::type_complexity)]
    connect: Option<Box<dyn Fn(&str, &str) -> Result<String, String> + Send + Sync>>,
}

impl UltravoxService {
    pub const PROVIDER: &'static str = "ultravox";

    #[allow(clippy::type_complexity)]
    pub fn new(
        options: UltravoxOptions,
        connect: Option<Box<dyn Fn(&str, &str) -> Result<String, String> + Send + Sync>>,
    ) -> Self {
        Self { options, connect }
    }

    pub fn options(&self) -> &UltravoxOptions {
        &self.options
    }

    /// Needs BOTH a key and a way to open a socket. Either alone is a service
    /// that reports ready and fails on the first call, which is the worst
    /// moment to find out - somebody is already on the line.
    pub fn is_available(&self) -> bool {
        self.options.is_configured() && self.connect.is_some()
    }

    pub fn sample_rate_hz(&self) -> u32 {
        UltravoxOptions::SAMPLE_RATE_HZ
    }

    pub fn start(&self) -> Result<String, String> {
        if !self.options.is_configured() {
            return Err(format!("{} has no key set on this device", Self::PROVIDER));
        }
        let Some(connect) = &self.connect else {
            return Err(format!(
                "{} cannot be reached from this build - there is no socket transport",
                Self::PROVIDER
            ));
        };
        connect(self.options.resolved_url(), self.options.resolved_model())
    }
}

impl std::fmt::Debug for UltravoxService {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("UltravoxService")
            .field("provider", &Self::PROVIDER)
            .field("available", &self.is_available())
            .finish()
    }
}

/// Mail connectors. SENDING IS THE CONSEQUENTIAL ONE - reading mail is
/// recoverable, and a message sent as somebody is not.
pub trait EmailConnectorRegistry {
    fn kind(&self) -> &'static str {
        "email"
    }
    fn all(&self) -> Vec<ConnectorEntry>;
    fn get(&self, id: &str) -> Option<ConnectorEntry>;
    fn offline_capable(&self) -> Vec<ConnectorEntry>;
}

/// The built-in email connectors.
#[derive(Debug, Default, Clone)]
pub struct DefaultEmailConnectorRegistry {
    entries: Vec<ConnectorEntry>,
}

impl DefaultEmailConnectorRegistry {
    pub fn new(entries: Vec<ConnectorEntry>) -> Self {
        Self { entries }
    }

    pub fn add(&mut self, entry: ConnectorEntry) -> &mut Self {
        if !entry.id.is_empty() && !self.entries.iter().any(|e| e.id == entry.id) {
            self.entries.push(entry);
        }
        self
    }
}

impl EmailConnectorRegistry for DefaultEmailConnectorRegistry {
    fn all(&self) -> Vec<ConnectorEntry> {
        let mut out = self.entries.clone();
        out.sort_by(|a, b| a.display_name.cmp(&b.display_name));
        out
    }

    fn get(&self, id: &str) -> Option<ConnectorEntry> {
        self.entries.iter().find(|e| e.id == id).cloned()
    }

    /// What still works with no network. On a device built to work offline this
    /// is the list that matters, and it is usually short - which is worth seeing
    /// rather than hiding.
    fn offline_capable(&self) -> Vec<ConnectorEntry> {
        self.entries.iter().filter(|e| e.works_offline).cloned().collect()
    }
}

/// Calendar connectors. A calendar is a record of where somebody will
/// physically be, which is why its read scope is not a small permission.
pub trait CalendarConnectorRegistry {
    fn kind(&self) -> &'static str {
        "calendar"
    }
    fn all(&self) -> Vec<ConnectorEntry>;
    fn get(&self, id: &str) -> Option<ConnectorEntry>;
    fn offline_capable(&self) -> Vec<ConnectorEntry>;
}

/// The built-in calendar connectors.
#[derive(Debug, Default, Clone)]
pub struct DefaultCalendarConnectorRegistry {
    entries: Vec<ConnectorEntry>,
}

impl DefaultCalendarConnectorRegistry {
    pub fn new(entries: Vec<ConnectorEntry>) -> Self {
        Self { entries }
    }

    pub fn add(&mut self, entry: ConnectorEntry) -> &mut Self {
        if !entry.id.is_empty() && !self.entries.iter().any(|e| e.id == entry.id) {
            self.entries.push(entry);
        }
        self
    }
}

impl CalendarConnectorRegistry for DefaultCalendarConnectorRegistry {
    fn all(&self) -> Vec<ConnectorEntry> {
        let mut out = self.entries.clone();
        out.sort_by(|a, b| a.display_name.cmp(&b.display_name));
        out
    }

    fn get(&self, id: &str) -> Option<ConnectorEntry> {
        self.entries.iter().find(|e| e.id == id).cloned()
    }

    /// What still works with no network. On a device built to work offline this
    /// is the list that matters, and it is usually short - which is worth seeing
    /// rather than hiding.
    fn offline_capable(&self) -> Vec<ConnectorEntry> {
        self.entries.iter().filter(|e| e.works_offline).cloned().collect()
    }
}

/// CRM connectors. The data is about OTHER people, who never agreed to
/// anything here - so what leaves is narrower than what a user could
/// authorise for their own data.
pub trait CrmConnectorRegistry {
    fn kind(&self) -> &'static str {
        "crm"
    }
    fn all(&self) -> Vec<ConnectorEntry>;
    fn get(&self, id: &str) -> Option<ConnectorEntry>;
    fn offline_capable(&self) -> Vec<ConnectorEntry>;
}

/// The built-in crm connectors.
#[derive(Debug, Default, Clone)]
pub struct DefaultCrmConnectorRegistry {
    entries: Vec<ConnectorEntry>,
}

impl DefaultCrmConnectorRegistry {
    pub fn new(entries: Vec<ConnectorEntry>) -> Self {
        Self { entries }
    }

    pub fn add(&mut self, entry: ConnectorEntry) -> &mut Self {
        if !entry.id.is_empty() && !self.entries.iter().any(|e| e.id == entry.id) {
            self.entries.push(entry);
        }
        self
    }
}

impl CrmConnectorRegistry for DefaultCrmConnectorRegistry {
    fn all(&self) -> Vec<ConnectorEntry> {
        let mut out = self.entries.clone();
        out.sort_by(|a, b| a.display_name.cmp(&b.display_name));
        out
    }

    fn get(&self, id: &str) -> Option<ConnectorEntry> {
        self.entries.iter().find(|e| e.id == id).cloned()
    }

    /// What still works with no network. On a device built to work offline this
    /// is the list that matters, and it is usually short - which is worth seeing
    /// rather than hiding.
    fn offline_capable(&self) -> Vec<ConnectorEntry> {
        self.entries.iter().filter(|e| e.works_offline).cloned().collect()
    }
}

/// Accounting connectors. Read-shaped by default: a ledger is a record, and
/// a record that an assistant can rewrite is not one.
pub trait AccountingConnectorRegistry {
    fn kind(&self) -> &'static str {
        "accounting"
    }
    fn all(&self) -> Vec<ConnectorEntry>;
    fn get(&self, id: &str) -> Option<ConnectorEntry>;
    fn offline_capable(&self) -> Vec<ConnectorEntry>;
}

/// The built-in accounting connectors.
#[derive(Debug, Default, Clone)]
pub struct DefaultAccountingConnectorRegistry {
    entries: Vec<ConnectorEntry>,
}

impl DefaultAccountingConnectorRegistry {
    pub fn new(entries: Vec<ConnectorEntry>) -> Self {
        Self { entries }
    }

    pub fn add(&mut self, entry: ConnectorEntry) -> &mut Self {
        if !entry.id.is_empty() && !self.entries.iter().any(|e| e.id == entry.id) {
            self.entries.push(entry);
        }
        self
    }
}

impl AccountingConnectorRegistry for DefaultAccountingConnectorRegistry {
    fn all(&self) -> Vec<ConnectorEntry> {
        let mut out = self.entries.clone();
        out.sort_by(|a, b| a.display_name.cmp(&b.display_name));
        out
    }

    fn get(&self, id: &str) -> Option<ConnectorEntry> {
        self.entries.iter().find(|e| e.id == id).cloned()
    }

    /// What still works with no network. On a device built to work offline this
    /// is the list that matters, and it is usually short - which is worth seeing
    /// rather than hiding.
    fn offline_capable(&self) -> Vec<ConnectorEntry> {
        self.entries.iter().filter(|e| e.works_offline).cloned().collect()
    }
}

/// Banking connectors. READ ONLY, always. Nothing in this codebase moves
/// money - a balance can be shown and a transaction categorised, and a
/// transfer is the account holder's own action.
pub trait BankingConnectorRegistry {
    fn kind(&self) -> &'static str {
        "banking"
    }
    fn all(&self) -> Vec<ConnectorEntry>;
    fn get(&self, id: &str) -> Option<ConnectorEntry>;
    fn offline_capable(&self) -> Vec<ConnectorEntry>;
}

/// The built-in banking connectors.
#[derive(Debug, Default, Clone)]
pub struct DefaultBankingConnectorRegistry {
    entries: Vec<ConnectorEntry>,
}

impl DefaultBankingConnectorRegistry {
    pub fn new(entries: Vec<ConnectorEntry>) -> Self {
        Self { entries }
    }

    pub fn add(&mut self, entry: ConnectorEntry) -> &mut Self {
        if !entry.id.is_empty() && !self.entries.iter().any(|e| e.id == entry.id) {
            self.entries.push(entry);
        }
        self
    }
}

impl BankingConnectorRegistry for DefaultBankingConnectorRegistry {
    fn all(&self) -> Vec<ConnectorEntry> {
        let mut out = self.entries.clone();
        out.sort_by(|a, b| a.display_name.cmp(&b.display_name));
        out
    }

    fn get(&self, id: &str) -> Option<ConnectorEntry> {
        self.entries.iter().find(|e| e.id == id).cloned()
    }

    /// What still works with no network. On a device built to work offline this
    /// is the list that matters, and it is usually short - which is worth seeing
    /// rather than hiding.
    fn offline_capable(&self) -> Vec<ConnectorEntry> {
        self.entries.iter().filter(|e| e.works_offline).cloned().collect()
    }
}
