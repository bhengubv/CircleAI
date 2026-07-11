//! distribution.rs
//!
//! Port of `CircleAI.Distribution/` — the peer-to-peer file-sync contracts plus
//! the 77 UBI ("ubiquity") rails: the distribution, onboarding, trust, pricing,
//! localisation, hardware, services, regulator, recovery, failure-mode, cost,
//! network-effect, and cultural pieces that turn the substrate into "everywhere
//! people are." Each rail is a small trait + default; hosts wire real
//! integrations against them.
//!
//! C# → Rust map:
//!   * `FileMetadata` / `Peer`            → same structs
//!   * `IFileSync` / `IPeerAdvertiser`    → `#[async_trait]` traits (C# `ValueTask`)
//!   * `NullFileSync` / `NullPeerAdvertiser` → same names
//!   * `CircleAI.Distribution.Ubiquity.*` contracts + `Default*`/missing-default
//!     implementations → this module's `ubiquity` submodule.
//!
//! Conventions / non-1:1 constructs:
//!   * C# `ValueTask`/`ValueTask<T>` → `#[async_trait] async fn`.
//!   * C# `decimal` (money) → `f64` (crate-wide convention).
//!   * C# `Uri` → `String` (the crate carries no URL type); validation that the
//!     C# `PublicTransparency` did on `Uri.Scheme` is reproduced with a small
//!     absolute-http(s) check.
//!   * C# `ConcurrentDictionary`/`lock`-guarded `List` → `Mutex<…>`.
//!   * Crypto: `DefaultSignedDeltaUpdater` (HMAC-SHA256) and `DefaultVerifiableWipe`
//!     / `DefaultPhonePinBiometricOnboarding` (SHA-256) use `System.Security.
//!     Cryptography` in C#. The Rust crate has no crypto dependency, so those
//!     three carry a small self-contained SHA-256 + HMAC implementation
//!     ([`hashing`] submodule) to preserve real (not stubbed) behaviour and
//!     constant-time comparison.
//!   * `ReadOnlyMemory<byte>` → `Vec<u8>` / `&[u8]`.
//!   * Tuple-returning C# members (e.g. `Sent`) → named structs where the tuple
//!     escapes the type; anonymous-record egress → structs.

use async_trait::async_trait;
use chrono::{DateTime, Utc};

// ─────────────────────────────────────────────────────────────────────────────
// DistributionError
// ─────────────────────────────────────────────────────────────────────────────

/// Failure surface for the distribution subsystem — the C#
/// `ArgumentException`/`InvalidOperationException`/`ArgumentOutOfRangeException`
/// guard rails.
#[derive(Debug)]
pub enum DistributionError {
    /// A required argument was null / empty / invalid.
    InvalidArgument(String),
    /// A state precondition was violated.
    InvalidOperation(String),
    /// A numeric argument was out of the allowed range.
    OutOfRange(String),
}

impl std::fmt::Display for DistributionError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            DistributionError::InvalidArgument(m) => write!(f, "invalid argument: {m}"),
            DistributionError::InvalidOperation(m) => write!(f, "invalid operation: {m}"),
            DistributionError::OutOfRange(m) => write!(f, "out of range: {m}"),
        }
    }
}

impl std::error::Error for DistributionError {}

// ─────────────────────────────────────────────────────────────────────────────
// Core contracts (Contracts.cs)
// ─────────────────────────────────────────────────────────────────────────────

/// Content-addressed file metadata. 1:1 with the C# `FileMetadata` record.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct FileMetadata {
    pub content_hash: String,
    pub name: String,
    pub size_bytes: i64,
}

/// A discovered peer and the content hashes it advertises. 1:1 with `Peer`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Peer {
    pub peer_id: String,
    pub endpoint: String,
    pub available_hashes: Vec<String>,
}

/// Content-addressed file sync backend. `#[async_trait]` (C# `ValueTask`).
#[async_trait]
pub trait IFileSync {
    fn backend_id(&self) -> &str;
    async fn has(&self, content_hash: &str) -> bool;
    async fn fetch(&self, content_hash: &str) -> Option<Vec<u8>>;
    async fn announce(&self, metadata: FileMetadata, payload: Vec<u8>);
}

/// Peer discovery backend. `#[async_trait]` (C# `ValueTask`).
#[async_trait]
pub trait IPeerAdvertiser {
    fn backend_id(&self) -> &str;
    async fn discover(&self) -> Vec<Peer>;
}

// ─────────────────────────────────────────────────────────────────────────────
// Null implementations (NullImplementations.cs)
// ─────────────────────────────────────────────────────────────────────────────

/// No-op [`IFileSync`] — never has, never fetches. 1:1 with `NullFileSync`.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullFileSync;

#[async_trait]
impl IFileSync for NullFileSync {
    fn backend_id(&self) -> &str {
        "null"
    }
    async fn has(&self, _content_hash: &str) -> bool {
        false
    }
    async fn fetch(&self, _content_hash: &str) -> Option<Vec<u8>> {
        None
    }
    async fn announce(&self, _metadata: FileMetadata, _payload: Vec<u8>) {}
}

/// No-op [`IPeerAdvertiser`] — discovers nothing. 1:1 with `NullPeerAdvertiser`.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullPeerAdvertiser;

#[async_trait]
impl IPeerAdvertiser for NullPeerAdvertiser {
    fn backend_id(&self) -> &str {
        "null"
    }
    async fn discover(&self) -> Vec<Peer> {
        Vec::new()
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// UBIQUITY RAILS (UbiquityRails.cs + UbiquityRailsMissingDefaults.cs)
//
// The C# lives in the sibling namespace `CircleAI.Distribution.Ubiquity`. To keep
// the single-file-per-package convention, the whole surface is inlined here.
// ═════════════════════════════════════════════════════════════════════════════

use std::collections::HashMap;
use std::sync::Mutex;

// =====================================================================
// DISTRIBUTION
// =====================================================================

/// An app-store submission package. 1:1 with `AppStorePackage`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AppStorePackage {
    pub store_name: String,
    pub package_path: String,
    pub version: String,
    pub metadata: HashMap<String, String>,
}

#[async_trait]
pub trait IAppStoreSubmitter {
    async fn submit(&self, package: AppStorePackage) -> Result<bool, DistributionError>;
}

/// A signed delta update. 1:1 with `DeltaUpdate`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DeltaUpdate {
    pub channel: String,
    pub from_version: String,
    pub to_version: String,
    pub payload: Vec<u8>,
    pub signature: Vec<u8>,
}

#[async_trait]
pub trait ISignedDeltaUpdater {
    async fn apply(&self, update: DeltaUpdate) -> Result<bool, DistributionError>;
}

pub trait IOemPreloadCatalog {
    fn partners(&self) -> &[String];
}
pub struct DefaultOemPreloadCatalog {
    partners: Vec<String>,
}
impl Default for DefaultOemPreloadCatalog {
    fn default() -> Self {
        Self {
            partners: ["Tecno", "Itel", "Samsung mid-tier", "Xiaomi", "Huawei"]
                .iter()
                .map(|s| s.to_string())
                .collect(),
        }
    }
}
impl IOemPreloadCatalog for DefaultOemPreloadCatalog {
    fn partners(&self) -> &[String] {
        &self.partners
    }
}

pub trait ICarrierPreloadCatalog {
    fn carriers(&self) -> &[String];
}
pub struct DefaultCarrierPreloadCatalog {
    carriers: Vec<String>,
}
impl Default for DefaultCarrierPreloadCatalog {
    fn default() -> Self {
        Self {
            carriers: ["MTN", "Vodacom", "Cell C", "Telkom", "Safaricom", "Airtel"]
                .iter()
                .map(|s| s.to_string())
                .collect(),
        }
    }
}
impl ICarrierPreloadCatalog for DefaultCarrierPreloadCatalog {
    fn carriers(&self) -> &[String] {
        &self.carriers
    }
}

pub trait IPwaFallback {
    fn pwa_url(&self) -> &str;
}
pub struct DefaultPwaFallback {
    pwa_url: String,
}
impl Default for DefaultPwaFallback {
    fn default() -> Self {
        Self {
            pwa_url: "https://app.circle.ai".into(),
        }
    }
}
impl IPwaFallback for DefaultPwaFallback {
    fn pwa_url(&self) -> &str {
        &self.pwa_url
    }
}

pub trait ISideloadChannel {
    fn formats(&self) -> &[String];
}
pub struct DefaultSideloadChannel {
    formats: Vec<String>,
}
impl Default for DefaultSideloadChannel {
    fn default() -> Self {
        Self {
            formats: ["APK", "IPA", "MSIX"].iter().map(|s| s.to_string()).collect(),
        }
    }
}
impl ISideloadChannel for DefaultSideloadChannel {
    fn formats(&self) -> &[String] {
        &self.formats
    }
}

pub trait ILinuxRepoFanout {
    fn repos(&self) -> &[String];
}
pub struct DefaultLinuxRepoFanout {
    repos: Vec<String>,
}
impl Default for DefaultLinuxRepoFanout {
    fn default() -> Self {
        Self {
            repos: ["apt", "yum", "pacman", "brew", "flatpak", "snap"]
                .iter()
                .map(|s| s.to_string())
                .collect(),
        }
    }
}
impl ILinuxRepoFanout for DefaultLinuxRepoFanout {
    fn repos(&self) -> &[String] {
        &self.repos
    }
}

// =====================================================================
// ONBOARDING
// =====================================================================

/// 1:1 with `OnboardingSession`. `TimeSpan` → [`chrono::Duration`].
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct OnboardingSession {
    pub session_id: String,
    pub phone_number: String,
    pub biometric_enrolled: bool,
    pub time_to_active: chrono::Duration,
}

#[async_trait]
pub trait IPhonePinBiometricOnboarding {
    async fn start(&self, phone_number: &str) -> Result<OnboardingSession, DistributionError>;
    async fn complete(
        &self,
        session_id: &str,
        pin: &str,
        biometric_ok: bool,
    ) -> Result<(), DistributionError>;
}

#[async_trait]
pub trait INoManualFirstRun {
    async fn show(&self) -> String;
}

#[async_trait]
pub trait IVoiceLedSetup {
    /// Mother-tongue voice-led setup. Returns whether the tongue is supported.
    async fn run(&self, mother_tongue: &str) -> Result<bool, DistributionError>;
}

/// 1:1 with `PersonalityChoice`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PersonalityChoice {
    pub name: String,
}
impl PersonalityChoice {
    pub fn new(name: impl Into<String>) -> Self {
        Self { name: name.into() }
    }
}

#[async_trait]
pub trait IAiPersonalityWizard {
    fn presets(&self) -> &[PersonalityChoice];
    async fn select(
        &self,
        session_id: &str,
        choice: PersonalityChoice,
    ) -> Result<(), DistributionError>;
}

pub struct DefaultAiPersonalityWizard {
    presets: Vec<PersonalityChoice>,
    selections: Mutex<HashMap<String, PersonalityChoice>>,
}
impl Default for DefaultAiPersonalityWizard {
    fn default() -> Self {
        Self {
            presets: ["formal", "warm", "playful", "professional"]
                .iter()
                .map(|s| PersonalityChoice::new(*s))
                .collect(),
            selections: Mutex::new(HashMap::new()),
        }
    }
}
impl DefaultAiPersonalityWizard {
    pub fn selected(&self, session_id: &str) -> Option<PersonalityChoice> {
        self.selections.lock().unwrap().get(session_id).cloned()
    }
}
#[async_trait]
impl IAiPersonalityWizard for DefaultAiPersonalityWizard {
    fn presets(&self) -> &[PersonalityChoice] {
        &self.presets
    }
    async fn select(
        &self,
        session_id: &str,
        choice: PersonalityChoice,
    ) -> Result<(), DistributionError> {
        if session_id.trim().is_empty() {
            return Err(DistributionError::InvalidArgument("sessionId required".into()));
        }
        if !self
            .presets
            .iter()
            .any(|p| p.name.eq_ignore_ascii_case(&choice.name))
        {
            return Err(DistributionError::InvalidOperation(format!(
                "Unknown personality '{}'.",
                choice.name
            )));
        }
        self.selections
            .lock()
            .unwrap()
            .insert(session_id.to_string(), choice);
        Ok(())
    }
}

#[async_trait]
pub trait IPersonalDataImport {
    async fn import(&self, session_id: &str, source: &str) -> Result<(), DistributionError>;
}

/// 1:1 with `HouseholdMember`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct HouseholdMember {
    pub member_id: String,
    pub display_name: String,
    pub role: String,
}

#[async_trait]
pub trait IFamilyOnboarding {
    async fn create_household(
        &self,
        owner_id: &str,
        members: Vec<HouseholdMember>,
    ) -> Result<(), DistributionError>;
}

// =====================================================================
// TRUST
// =====================================================================

pub trait IThirdPartySecurityAuditPublisher {
    fn report_url(&self) -> &str;
}
pub struct DefaultThirdPartySecurityAuditPublisher {
    report_url: String,
}
impl Default for DefaultThirdPartySecurityAuditPublisher {
    fn default() -> Self {
        Self {
            report_url: "https://trust.circle.ai/audit".into(),
        }
    }
}
impl IThirdPartySecurityAuditPublisher for DefaultThirdPartySecurityAuditPublisher {
    fn report_url(&self) -> &str {
        &self.report_url
    }
}

pub trait IComplianceCertifications {
    fn certifications(&self) -> &[String];
}
pub struct DefaultComplianceCertifications {
    certifications: Vec<String>,
}
impl Default for DefaultComplianceCertifications {
    fn default() -> Self {
        Self {
            certifications: ["SOC 2 Type II", "ISO 27001", "ISO 27701"]
                .iter()
                .map(|s| s.to_string())
                .collect(),
        }
    }
}
impl IComplianceCertifications for DefaultComplianceCertifications {
    fn certifications(&self) -> &[String] {
        &self.certifications
    }
}

pub trait IBugBountyChannel {
    fn platform(&self) -> &str;
    fn submission_url(&self) -> &str;
}
pub struct DefaultBugBountyChannel {
    submission_url: String,
}
impl Default for DefaultBugBountyChannel {
    fn default() -> Self {
        Self {
            submission_url: "https://h1.com/circleai".into(),
        }
    }
}
impl IBugBountyChannel for DefaultBugBountyChannel {
    fn platform(&self) -> &str {
        "HackerOne"
    }
    fn submission_url(&self) -> &str {
        &self.submission_url
    }
}

pub trait IPrivacyRegulationCompliance {
    fn laws(&self) -> &[String];
}
pub struct DefaultPrivacyRegulationCompliance {
    laws: Vec<String>,
}
impl Default for DefaultPrivacyRegulationCompliance {
    fn default() -> Self {
        Self {
            laws: ["GDPR", "POPIA", "CCPA", "LGPD"]
                .iter()
                .map(|s| s.to_string())
                .collect(),
        }
    }
}
impl IPrivacyRegulationCompliance for DefaultPrivacyRegulationCompliance {
    fn laws(&self) -> &[String] {
        &self.laws
    }
}

pub trait IVerifiablePrivacyProof {
    fn build_is_reproducible(&self) -> bool;
    fn source_url(&self) -> &str;
}
pub struct DefaultVerifiablePrivacyProof;
impl IVerifiablePrivacyProof for DefaultVerifiablePrivacyProof {
    fn build_is_reproducible(&self) -> bool {
        true
    }
    fn source_url(&self) -> &str {
        "https://github.com/bhengubv/CircleAI"
    }
}

/// 1:1 with `TransparencyReceipt`. `decimal CostUsd` → `f64`.
#[derive(Debug, Clone, PartialEq)]
pub struct TransparencyReceipt {
    pub call_id: String,
    pub actions_taken: Vec<String>,
    pub data_egress: Vec<String>,
    pub cost_usd: f64,
}

#[async_trait]
pub trait IPerCallTransparency {
    async fn receipt_for(&self, call_id: &str) -> Result<TransparencyReceipt, DistributionError>;
}

// =====================================================================
// PRICING
// =====================================================================

/// 1:1 with `PricingTier`. `decimal MonthlyPriceLocal` → `f64`.
#[derive(Debug, Clone, PartialEq)]
pub struct PricingTier {
    pub name: String,
    pub monthly_price_local: f64,
    pub currency: String,
    pub features: Vec<String>,
}

pub trait IPricingMatrix {
    fn all(&self) -> &[PricingTier];
}
pub struct DefaultPricingMatrix {
    all: Vec<PricingTier>,
}
impl Default for DefaultPricingMatrix {
    fn default() -> Self {
        let tier = |name: &str, price: f64, features: &[&str]| PricingTier {
            name: name.into(),
            monthly_price_local: price,
            currency: "ZAR".into(),
            features: features.iter().map(|s| s.to_string()).collect(),
        };
        Self {
            all: vec![
                tier("free", 0.0, &["Local chat", "Family memory cap"]),
                tier("paid", 19.0, &["Unlimited cloud calls", "Priority routing"]),
                tier("family", 49.0, &["Up to 6 members"]),
                tier("stokvel", 99.0, &["Group memory", "Group reporting"]),
                tier("enterprise", 200.0, &["Dedicated brain", "SLA"]),
            ],
        }
    }
}
impl IPricingMatrix for DefaultPricingMatrix {
    fn all(&self) -> &[PricingTier] {
        &self.all
    }
}

pub trait IPluginMarketplaceRevenueShare {
    fn author_share(&self) -> f64;
    fn verified_safe_share(&self) -> f64;
}
pub struct DefaultPluginMarketplaceRevenueShare;
impl IPluginMarketplaceRevenueShare for DefaultPluginMarketplaceRevenueShare {
    fn author_share(&self) -> f64 {
        0.70
    }
    fn verified_safe_share(&self) -> f64 {
        0.50
    }
}

pub trait ICarrierRevenueShare {
    fn carrier_share(&self) -> f64;
}
pub struct DefaultCarrierRevenueShare;
impl ICarrierRevenueShare for DefaultCarrierRevenueShare {
    fn carrier_share(&self) -> f64 {
        0.25
    }
}

// =====================================================================
// LOCALISATION
// =====================================================================

pub trait ICurrencyFormatter {
    fn format(&self, amount: f64, iso_currency_code: &str) -> String;
}
pub struct DefaultCurrencyFormatter;
impl ICurrencyFormatter for DefaultCurrencyFormatter {
    fn format(&self, amount: f64, iso_currency_code: &str) -> String {
        format!("{amount:.2} {iso_currency_code}")
    }
}

pub trait IPhoneNumberFormatter {
    fn format(&self, e164: &str, country_code_iso_alpha2: &str) -> String;
}
pub struct DefaultPhoneNumberFormatter;
impl IPhoneNumberFormatter for DefaultPhoneNumberFormatter {
    fn format(&self, e164: &str, _country_code_iso_alpha2: &str) -> String {
        e164.to_string()
    }
}

pub trait ICulturalNameRecogniser {
    fn recognises_language(&self, iso_language: &str) -> bool;
}
pub struct DefaultCulturalNameRecogniser;
impl ICulturalNameRecogniser for DefaultCulturalNameRecogniser {
    fn recognises_language(&self, iso_language: &str) -> bool {
        const SUPPORTED: [&str; 10] = [
            "zul", "xho", "tsn", "sot", "yor", "ibo", "twi", "swa", "hin", "ben",
        ];
        SUPPORTED
            .iter()
            .any(|s| s.eq_ignore_ascii_case(iso_language))
    }
}

pub trait ICulturalGreetings {
    fn greeting_for(&self, iso_language: &str) -> String;
}
pub struct DefaultCulturalGreetings;
impl ICulturalGreetings for DefaultCulturalGreetings {
    fn greeting_for(&self, iso_language: &str) -> String {
        match iso_language {
            "zul" | "zu" => "Sawubona",
            "xho" | "xh" => "Molo",
            "yor" => "Ẹ kú àárọ̀",
            "hin" => "नमस्ते",
            _ => "Hello",
        }
        .to_string()
    }
}

pub trait ISaServiceConnectors {
    fn banks(&self) -> &[String];
    fn wallets(&self) -> &[String];
}
pub struct DefaultSaServiceConnectors {
    banks: Vec<String>,
    wallets: Vec<String>,
}
impl Default for DefaultSaServiceConnectors {
    fn default() -> Self {
        Self {
            banks: ["Capitec", "FNB", "Standard", "Absa", "Nedbank"]
                .iter()
                .map(|s| s.to_string())
                .collect(),
            wallets: ["PayFast", "SnapScan"].iter().map(|s| s.to_string()).collect(),
        }
    }
}
impl ISaServiceConnectors for DefaultSaServiceConnectors {
    fn banks(&self) -> &[String] {
        &self.banks
    }
    fn wallets(&self) -> &[String] {
        &self.wallets
    }
}

pub trait ICrossBorderCorridors {
    fn corridors(&self) -> &[String];
}
pub struct DefaultCrossBorderCorridors {
    corridors: Vec<String>,
}
impl Default for DefaultCrossBorderCorridors {
    fn default() -> Self {
        Self {
            corridors: ["SADC", "ECOWAS", "EAC"].iter().map(|s| s.to_string()).collect(),
        }
    }
}
impl ICrossBorderCorridors for DefaultCrossBorderCorridors {
    fn corridors(&self) -> &[String] {
        &self.corridors
    }
}

pub trait IIndigenousKnowledgeProtocols {
    fn requires_elder_review(&self, iso_language: &str) -> bool;
}
pub struct DefaultIndigenousKnowledgeProtocols;
impl IIndigenousKnowledgeProtocols for DefaultIndigenousKnowledgeProtocols {
    fn requires_elder_review(&self, _iso_language: &str) -> bool {
        true
    }
}

// =====================================================================
// HARDWARE
// =====================================================================

pub trait ILowRamPhoneSupport {
    fn supports_ram_mb(&self, ram_mb: i32) -> bool;
}
pub struct DefaultLowRamPhoneSupport;
impl ILowRamPhoneSupport for DefaultLowRamPhoneSupport {
    fn supports_ram_mb(&self, ram_mb: i32) -> bool {
        ram_mb >= 512
    }
}

pub trait ILowCpuOptimization {
    fn supports_clock_mhz(&self, clock_mhz: i32) -> bool;
}
pub struct DefaultLowCpuOptimization;
impl ILowCpuOptimization for DefaultLowCpuOptimization {
    fn supports_clock_mhz(&self, clock_mhz: i32) -> bool {
        clock_mhz >= 600
    }
}

#[async_trait]
pub trait IOfflineQueuedOperation {
    async fn enqueue(&self, operation_json: &str) -> Result<(), DistributionError>;
    fn pending(&self) -> Vec<String>;
    fn try_dequeue(&self) -> Option<String>;
}
#[derive(Default)]
pub struct DefaultOfflineQueuedOperation {
    q: Mutex<std::collections::VecDeque<String>>,
}
#[async_trait]
impl IOfflineQueuedOperation for DefaultOfflineQueuedOperation {
    async fn enqueue(&self, operation_json: &str) -> Result<(), DistributionError> {
        if operation_json.trim().is_empty() {
            return Err(DistributionError::InvalidArgument("operationJson required".into()));
        }
        self.q.lock().unwrap().push_back(operation_json.to_string());
        Ok(())
    }
    fn pending(&self) -> Vec<String> {
        self.q.lock().unwrap().iter().cloned().collect()
    }
    fn try_dequeue(&self) -> Option<String> {
        self.q.lock().unwrap().pop_front()
    }
}

/// A record of one SMS answer sent — the C# `(string Phone, string Question,
/// DateTimeOffset At)` tuple, named for Rust.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SmsSent {
    pub phone: String,
    pub question: String,
    pub at: DateTime<Utc>,
}

#[async_trait]
pub trait ISmsFallback {
    async fn answer_via_sms(&self, phone_number: &str, question: &str)
        -> Result<(), DistributionError>;
    fn sent(&self) -> Vec<SmsSent>;
}
#[derive(Default)]
pub struct DefaultSmsFallback {
    sent: Mutex<Vec<SmsSent>>,
}
#[async_trait]
impl ISmsFallback for DefaultSmsFallback {
    async fn answer_via_sms(
        &self,
        phone_number: &str,
        question: &str,
    ) -> Result<(), DistributionError> {
        if phone_number.trim().is_empty() {
            return Err(DistributionError::InvalidArgument("phoneNumber required".into()));
        }
        if question.trim().is_empty() {
            return Err(DistributionError::InvalidArgument("question required".into()));
        }
        self.sent.lock().unwrap().push(SmsSent {
            phone: phone_number.to_string(),
            question: question.to_string(),
            at: Utc::now(),
        });
        Ok(())
    }
    fn sent(&self) -> Vec<SmsSent> {
        self.sent.lock().unwrap().clone()
    }
}

#[async_trait]
pub trait IUssdFallback {
    async fn respond(&self, ussd_session: &str, input: &str) -> Result<String, DistributionError>;
}

/// USSD menu node — prompt text + input-to-next-key routing.
struct UssdMenu {
    prompt: &'static str,
    routes: &'static [(&'static str, &'static str)],
}

/// Real USSD menu state machine — 1:1 with `DefaultUssdFallback`.
#[derive(Default)]
pub struct DefaultUssdFallback {
    sessions: Mutex<HashMap<String, String>>,
}
impl DefaultUssdFallback {
    fn menu(key: &str) -> Option<UssdMenu> {
        match key {
            "root" => Some(UssdMenu {
                prompt: "CircleAI:\n1. Balance\n2. Ask AI\n3. Help",
                routes: &[("1", "balance"), ("2", "ask"), ("3", "help")],
            }),
            "balance" => Some(UssdMenu {
                prompt: "Balance: R0.00\n0. Back",
                routes: &[("0", "root")],
            }),
            "ask" => Some(UssdMenu {
                prompt: "Type question, then send.\n0. Back",
                routes: &[("0", "root")],
            }),
            "help" => Some(UssdMenu {
                prompt: "Dial *120*CIRCLE# anytime.\n0. Back",
                routes: &[("0", "root")],
            }),
            _ => None,
        }
    }
}
#[async_trait]
impl IUssdFallback for DefaultUssdFallback {
    async fn respond(&self, ussd_session: &str, input: &str) -> Result<String, DistributionError> {
        if ussd_session.trim().is_empty() {
            return Err(DistributionError::InvalidArgument("ussdSession required".into()));
        }
        let mut sessions = self.sessions.lock().unwrap();
        let current = sessions
            .entry(ussd_session.to_string())
            .or_insert_with(|| "root".to_string())
            .clone();
        let menu = match Self::menu(&current) {
            Some(m) => m,
            None => {
                sessions.insert(ussd_session.to_string(), "root".to_string());
                return Ok(Self::menu("root").unwrap().prompt.to_string());
            }
        };
        if let Some((_, next)) = menu.routes.iter().find(|(k, _)| *k == input.trim()) {
            sessions.insert(ussd_session.to_string(), (*next).to_string());
            return Ok(Self::menu(next).unwrap().prompt.to_string());
        }
        Ok(menu.prompt.to_string())
    }
}

pub trait IKaiOsSupport {
    fn is_compiled(&self) -> bool;
}
pub struct DefaultKaiOsSupport;
impl IKaiOsSupport for DefaultKaiOsSupport {
    fn is_compiled(&self) -> bool {
        true
    }
}

// =====================================================================
// SERVICES
// =====================================================================

/// A record of one WhatsApp message sent.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct WhatsAppOut {
    pub phone: String,
    pub body: String,
    pub at: DateTime<Utc>,
}

#[async_trait]
pub trait IWhatsAppIntegration {
    async fn send(&self, phone_number: &str, message: &str) -> Result<(), DistributionError>;
    fn outbox(&self) -> Vec<WhatsAppOut>;
}
#[derive(Default)]
pub struct DefaultWhatsAppIntegration {
    out: Mutex<Vec<WhatsAppOut>>,
}
#[async_trait]
impl IWhatsAppIntegration for DefaultWhatsAppIntegration {
    async fn send(&self, phone_number: &str, message: &str) -> Result<(), DistributionError> {
        if phone_number.trim().is_empty() {
            return Err(DistributionError::InvalidArgument("phoneNumber required".into()));
        }
        if message.trim().is_empty() {
            return Err(DistributionError::InvalidArgument("message required".into()));
        }
        if !is_valid_e164(phone_number) {
            return Err(DistributionError::InvalidArgument(format!(
                "Invalid E.164 phone '{phone_number}'."
            )));
        }
        self.out.lock().unwrap().push(WhatsAppOut {
            phone: phone_number.to_string(),
            body: message.to_string(),
            at: Utc::now(),
        });
        Ok(())
    }
    fn outbox(&self) -> Vec<WhatsAppOut> {
        self.out.lock().unwrap().clone()
    }
}

/// A record of one Telegram message sent.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TelegramOut {
    pub chat: String,
    pub body: String,
    pub at: DateTime<Utc>,
}

#[async_trait]
pub trait ITelegramIntegration {
    async fn send(&self, chat_id: &str, message: &str) -> Result<(), DistributionError>;
    fn outbox(&self) -> Vec<TelegramOut>;
}
#[derive(Default)]
pub struct DefaultTelegramIntegration {
    out: Mutex<Vec<TelegramOut>>,
}
#[async_trait]
impl ITelegramIntegration for DefaultTelegramIntegration {
    async fn send(&self, chat_id: &str, message: &str) -> Result<(), DistributionError> {
        if chat_id.trim().is_empty() {
            return Err(DistributionError::InvalidArgument("chatId required".into()));
        }
        if message.trim().is_empty() {
            return Err(DistributionError::InvalidArgument("message required".into()));
        }
        self.out.lock().unwrap().push(TelegramOut {
            chat: chat_id.to_string(),
            body: message.to_string(),
            at: Utc::now(),
        });
        Ok(())
    }
    fn outbox(&self) -> Vec<TelegramOut> {
        self.out.lock().unwrap().clone()
    }
}

macro_rules! connector_registry {
    ($trait_name:ident, $default_name:ident, $accessor:ident, [$($provider:literal),* $(,)?]) => {
        pub trait $trait_name {
            fn $accessor(&self) -> &[String];
        }
        pub struct $default_name {
            $accessor: Vec<String>,
        }
        impl Default for $default_name {
            fn default() -> Self {
                Self {
                    $accessor: [$($provider),*].iter().map(|s| s.to_string()).collect(),
                }
            }
        }
        impl $trait_name for $default_name {
            fn $accessor(&self) -> &[String] {
                &self.$accessor
            }
        }
    };
}

connector_registry!(
    IEmailConnectorRegistry,
    DefaultEmailConnectorRegistry,
    providers,
    ["Gmail", "Outlook", "iCloud", "ProtonMail", "Yandex", "Yahoo", "IMAP"]
);
connector_registry!(
    ICalendarConnectorRegistry,
    DefaultCalendarConnectorRegistry,
    providers,
    ["Google", "Outlook", "Apple", "Yahoo", "CalDAV"]
);
connector_registry!(
    ICrmConnectorRegistry,
    DefaultCrmConnectorRegistry,
    providers,
    ["HubSpot", "Salesforce", "Pipedrive", "Zoho", "Bitrix"]
);
connector_registry!(
    IAccountingConnectorRegistry,
    DefaultAccountingConnectorRegistry,
    providers,
    ["Xero", "Sage", "QuickBooks", "Wave", "Manager.io"]
);
connector_registry!(
    IBankingConnectorRegistry,
    DefaultBankingConnectorRegistry,
    providers,
    ["open-banking-ZA", "open-banking-NG", "open-banking-KE"]
);

// =====================================================================
// REGULATOR
// =====================================================================

pub trait ISarbSandboxStatus {
    fn approved(&self) -> bool;
}
pub struct DefaultSarbSandboxStatus;
impl ISarbSandboxStatus for DefaultSarbSandboxStatus {
    fn approved(&self) -> bool {
        false
    }
}

pub trait IIcasaApprovalStatus {
    fn approved(&self) -> bool;
}
pub struct DefaultIcasaApprovalStatus;
impl IIcasaApprovalStatus for DefaultIcasaApprovalStatus {
    fn approved(&self) -> bool {
        false
    }
}

pub trait IGlobalRegulatorEngagement {
    fn active_jurisdictions(&self) -> &[String];
}
pub struct DefaultGlobalRegulatorEngagement {
    active_jurisdictions: Vec<String>,
}
impl Default for DefaultGlobalRegulatorEngagement {
    fn default() -> Self {
        Self {
            active_jurisdictions: ["ZA", "NG", "KE", "US", "CA", "UK", "EU"]
                .iter()
                .map(|s| s.to_string())
                .collect(),
        }
    }
}
impl IGlobalRegulatorEngagement for DefaultGlobalRegulatorEngagement {
    fn active_jurisdictions(&self) -> &[String] {
        &self.active_jurisdictions
    }
}

pub trait ITaxInvoiceRegistry {
    fn schemes(&self) -> &[String];
}
pub struct DefaultTaxInvoiceRegistry {
    schemes: Vec<String>,
}
impl Default for DefaultTaxInvoiceRegistry {
    fn default() -> Self {
        Self {
            schemes: ["VAT", "GST", "Sales Tax", "DST"]
                .iter()
                .map(|s| s.to_string())
                .collect(),
        }
    }
}
impl ITaxInvoiceRegistry for DefaultTaxInvoiceRegistry {
    fn schemes(&self) -> &[String] {
        &self.schemes
    }
}

pub trait ILawfulInterceptCompliance {
    fn posture(&self) -> &str;
}
pub struct DefaultLawfulInterceptCompliance;
impl ILawfulInterceptCompliance for DefaultLawfulInterceptCompliance {
    fn posture(&self) -> &str {
        "Money decryptable to law, comms permanently blind"
    }
}

// =====================================================================
// RECOVERY
// =====================================================================

#[async_trait]
pub trait ILostDeviceFlow {
    async fn remote_wipe(&self, device_id: &str) -> Result<(), DistributionError>;
    fn is_wiped(&self, device_id: &str) -> bool;
}
#[derive(Default)]
pub struct DefaultLostDeviceFlow {
    wiped: Mutex<HashMap<String, DateTime<Utc>>>,
}
#[async_trait]
impl ILostDeviceFlow for DefaultLostDeviceFlow {
    async fn remote_wipe(&self, device_id: &str) -> Result<(), DistributionError> {
        if device_id.trim().is_empty() {
            return Err(DistributionError::InvalidArgument("deviceId required".into()));
        }
        self.wiped
            .lock()
            .unwrap()
            .insert(device_id.to_string(), Utc::now());
        Ok(())
    }
    fn is_wiped(&self, device_id: &str) -> bool {
        self.wiped.lock().unwrap().contains_key(device_id)
    }
}

#[async_trait]
pub trait IInheritanceProtocol {
    async fn designate(&self, owner_id: &str, designee_id: &str)
        -> Result<(), DistributionError>;
    fn designee_for(&self, owner_id: &str) -> Option<String>;
}
#[derive(Default)]
pub struct DefaultInheritanceProtocol {
    designees: Mutex<HashMap<String, String>>,
}
#[async_trait]
impl IInheritanceProtocol for DefaultInheritanceProtocol {
    async fn designate(&self, owner_id: &str, designee_id: &str) -> Result<(), DistributionError> {
        if owner_id.trim().is_empty() {
            return Err(DistributionError::InvalidArgument("ownerId required".into()));
        }
        if designee_id.trim().is_empty() {
            return Err(DistributionError::InvalidArgument("designeeId required".into()));
        }
        if owner_id == designee_id {
            return Err(DistributionError::InvalidOperation(
                "Designee cannot equal owner.".into(),
            ));
        }
        self.designees
            .lock()
            .unwrap()
            .insert(owner_id.to_string(), designee_id.to_string());
        Ok(())
    }
    fn designee_for(&self, owner_id: &str) -> Option<String> {
        self.designees.lock().unwrap().get(owner_id).cloned()
    }
}

#[async_trait]
pub trait IVerifiableWipe {
    async fn wipe_and_certify(&self, owner_id: &str) -> Result<Vec<u8>, DistributionError>;
}
pub struct DefaultVerifiableWipe;
#[async_trait]
impl IVerifiableWipe for DefaultVerifiableWipe {
    async fn wipe_and_certify(&self, owner_id: &str) -> Result<Vec<u8>, DistributionError> {
        if owner_id.trim().is_empty() {
            return Err(DistributionError::InvalidArgument("ownerId required".into()));
        }
        // Certificate = SHA-256 over "wipe|ownerId|iso-timestamp|nonce".
        let nonce = hashing::random_bytes(16);
        let payload = format!(
            "wipe|{}|{}|{}",
            owner_id,
            Utc::now().to_rfc3339(),
            hashing::base64_encode(&nonce)
        );
        Ok(hashing::sha256(payload.as_bytes()).to_vec())
    }
}

#[async_trait]
pub trait IDataPortabilityExport {
    /// The C# returns a `Stream`; the Rust port returns the serialised bytes.
    async fn export(&self, owner_id: &str) -> Result<Vec<u8>, DistributionError>;
}
pub struct DefaultDataPortabilityExport;
#[async_trait]
impl IDataPortabilityExport for DefaultDataPortabilityExport {
    async fn export(&self, owner_id: &str) -> Result<Vec<u8>, DistributionError> {
        if owner_id.trim().is_empty() {
            return Err(DistributionError::InvalidArgument("ownerId required".into()));
        }
        let bundle = serde_json::json!({
            "owner_id": owner_id,
            "exported_at": Utc::now().to_rfc3339(),
            "schema": "circleai/portability/v1",
            "note": "Host overrides export to stream actual user data (memory, contacts, transcripts).",
        });
        Ok(serde_json::to_vec(&bundle).unwrap_or_default())
    }
}

#[async_trait]
pub trait IAccountCompromiseRecovery {
    async fn begin(&self, owner_id: &str) -> Result<(), DistributionError>;
    fn in_recovery(&self, owner_id: &str) -> bool;
    fn complete(&self, owner_id: &str);
}
#[derive(Default)]
pub struct DefaultAccountCompromiseRecovery {
    active: Mutex<HashMap<String, DateTime<Utc>>>,
}
#[async_trait]
impl IAccountCompromiseRecovery for DefaultAccountCompromiseRecovery {
    async fn begin(&self, owner_id: &str) -> Result<(), DistributionError> {
        if owner_id.trim().is_empty() {
            return Err(DistributionError::InvalidArgument("ownerId required".into()));
        }
        self.active
            .lock()
            .unwrap()
            .insert(owner_id.to_string(), Utc::now());
        Ok(())
    }
    fn in_recovery(&self, owner_id: &str) -> bool {
        self.active.lock().unwrap().contains_key(owner_id)
    }
    fn complete(&self, owner_id: &str) {
        self.active.lock().unwrap().remove(owner_id);
    }
}

// =====================================================================
// FAILURE MODES
// =====================================================================

pub trait IBrainUnreachableMode {
    fn local_takeover_enabled(&self) -> bool;
}
pub struct DefaultBrainUnreachableMode;
impl IBrainUnreachableMode for DefaultBrainUnreachableMode {
    fn local_takeover_enabled(&self) -> bool {
        true
    }
}

pub trait INoInternetCacheTarget {
    fn hit_rate_target(&self) -> f32;
}
pub struct DefaultNoInternetCacheTarget;
impl INoInternetCacheTarget for DefaultNoInternetCacheTarget {
    fn hit_rate_target(&self) -> f32 {
        0.80
    }
}

pub trait IStorageFullDegradationPolicy {
    fn degrade_order(&self) -> &str;
}
pub struct DefaultStorageFullDegradationPolicy;
impl IStorageFullDegradationPolicy for DefaultStorageFullDegradationPolicy {
    fn degrade_order(&self) -> &str {
        "cache > old-snapshots > chat-history > nothing"
    }
}

#[async_trait]
pub trait IImpairedUserMode {
    async fn engage(&self, owner_id: &str) -> Result<(), DistributionError>;
    fn is_engaged(&self, owner_id: &str) -> bool;
    async fn disengage(&self, owner_id: &str) -> Result<(), DistributionError>;
}
#[derive(Default)]
pub struct DefaultImpairedUserMode {
    engaged: Mutex<HashMap<String, u8>>,
}
#[async_trait]
impl IImpairedUserMode for DefaultImpairedUserMode {
    async fn engage(&self, owner_id: &str) -> Result<(), DistributionError> {
        if owner_id.trim().is_empty() {
            return Err(DistributionError::InvalidArgument("ownerId required".into()));
        }
        self.engaged.lock().unwrap().insert(owner_id.to_string(), 1);
        Ok(())
    }
    fn is_engaged(&self, owner_id: &str) -> bool {
        self.engaged.lock().unwrap().contains_key(owner_id)
    }
    async fn disengage(&self, owner_id: &str) -> Result<(), DistributionError> {
        self.engaged.lock().unwrap().remove(owner_id);
        Ok(())
    }
}

#[async_trait]
pub trait IAbusiveEnvironmentMode {
    async fn engage(&self, owner_id: &str) -> Result<(), DistributionError>;
    /// Test phrase the user can speak to silently invoke abuse-safe mode.
    fn safety_phrase(&self, owner_id: &str) -> Result<String, DistributionError>;
    fn is_engaged(&self, owner_id: &str) -> bool;
}
#[derive(Default)]
pub struct DefaultAbusiveEnvironmentMode {
    engaged: Mutex<HashMap<String, u8>>,
    phrases: Mutex<HashMap<String, String>>,
}
#[async_trait]
impl IAbusiveEnvironmentMode for DefaultAbusiveEnvironmentMode {
    async fn engage(&self, owner_id: &str) -> Result<(), DistributionError> {
        if owner_id.trim().is_empty() {
            return Err(DistributionError::InvalidArgument("ownerId required".into()));
        }
        self.engaged.lock().unwrap().insert(owner_id.to_string(), 1);
        Ok(())
    }
    fn safety_phrase(&self, owner_id: &str) -> Result<String, DistributionError> {
        if owner_id.trim().is_empty() {
            return Err(DistributionError::InvalidArgument("ownerId required".into()));
        }
        let mut phrases = self.phrases.lock().unwrap();
        if let Some(p) = phrases.get(owner_id) {
            return Ok(p.clone());
        }
        // Deterministic per-owner safety phrase from a 4-word benign vocabulary.
        // Matches the C# unchecked (uint)string.GetHashCode() indexing scheme by
        // using a stable FNV-1a hash (the C# hash is not portable, but the
        // determinism + word-table contract is preserved).
        const WORDS: [&str; 8] = [
            "thunder", "river", "amber", "field", "rain", "stone", "harbor", "linen",
        ];
        let h = fnv1a_32(owner_id);
        let phrase = format!(
            "the {} {} is {}",
            WORDS[(h % 8) as usize],
            WORDS[((h >> 8) % 8) as usize],
            WORDS[((h >> 16) % 8) as usize]
        );
        phrases.insert(owner_id.to_string(), phrase.clone());
        Ok(phrase)
    }
    fn is_engaged(&self, owner_id: &str) -> bool {
        self.engaged.lock().unwrap().contains_key(owner_id)
    }
}

pub trait IPublicDisasterMode {
    fn current_state(&self) -> &str;
}
pub struct DefaultPublicDisasterMode;
impl IPublicDisasterMode for DefaultPublicDisasterMode {
    fn current_state(&self) -> &str {
        "normal"
    }
}

// =====================================================================
// COST
// =====================================================================

pub trait ISustainablePerUserCostMath {
    fn monthly_revenue_per_user(&self) -> f64;
    fn monthly_marginal_cost_per_user(&self) -> f64;
}
pub struct DefaultSustainablePerUserCostMath;
impl ISustainablePerUserCostMath for DefaultSustainablePerUserCostMath {
    fn monthly_revenue_per_user(&self) -> f64 {
        19.0
    }
    fn monthly_marginal_cost_per_user(&self) -> f64 {
        3.8
    }
}

pub trait IPerCallCostCeiling {
    fn ceiling_usd(&self) -> f64;
}
pub struct DefaultPerCallCostCeiling;
impl IPerCallCostCeiling for DefaultPerCallCostCeiling {
    fn ceiling_usd(&self) -> f64 {
        0.40
    }
}

pub trait IFreeTierCostCapping {
    fn monthly_cap_usd(&self) -> f64;
}
pub struct DefaultFreeTierCostCapping;
impl IFreeTierCostCapping for DefaultFreeTierCostCapping {
    fn monthly_cap_usd(&self) -> f64 {
        0.20
    }
}

pub trait ILocalFirstRouting {
    fn preferred(&self) -> bool;
}
pub struct DefaultLocalFirstRouting;
impl ILocalFirstRouting for DefaultLocalFirstRouting {
    fn preferred(&self) -> bool {
        true
    }
}

// =====================================================================
// NETWORK EFFECTS
// =====================================================================

pub trait IReferralProgramme {
    fn reward_local(&self) -> f64;
    fn currency(&self) -> &str;
}
pub struct DefaultReferralProgramme;
impl IReferralProgramme for DefaultReferralProgramme {
    fn reward_local(&self) -> f64 {
        19.0
    }
    fn currency(&self) -> &str {
        "ZAR"
    }
}

pub trait IFamilyAiSharing {
    fn max_members(&self) -> i32;
}
pub struct DefaultFamilyAiSharing;
impl IFamilyAiSharing for DefaultFamilyAiSharing {
    fn max_members(&self) -> i32 {
        6
    }
}

pub trait ICrossProviderFederation {
    fn enabled(&self) -> bool;
}
pub struct DefaultCrossProviderFederation;
impl ICrossProviderFederation for DefaultCrossProviderFederation {
    fn enabled(&self) -> bool {
        true
    }
}

pub trait IGroupNetworkEffects {
    fn group_types(&self) -> &[String];
}
pub struct DefaultGroupNetworkEffects {
    group_types: Vec<String>,
}
impl Default for DefaultGroupNetworkEffects {
    fn default() -> Self {
        Self {
            group_types: ["Stokvel", "Church", "Community"]
                .iter()
                .map(|s| s.to_string())
                .collect(),
        }
    }
}
impl IGroupNetworkEffects for DefaultGroupNetworkEffects {
    fn group_types(&self) -> &[String] {
        &self.group_types
    }
}

pub trait IUserGrowthFlywheel {
    fn mechanic(&self) -> &str;
}
pub struct DefaultUserGrowthFlywheel;
impl IUserGrowthFlywheel for DefaultUserGrowthFlywheel {
    fn mechanic(&self) -> &str {
        "user invites friend; both get a month free"
    }
}

// =====================================================================
// CULTURAL
// =====================================================================

pub trait IThirdPartyHarmLiability {
    fn framework(&self) -> &str;
}
pub struct DefaultThirdPartyHarmLiability;
impl IThirdPartyHarmLiability for DefaultThirdPartyHarmLiability {
    fn framework(&self) -> &str {
        "Operator-of-record indemnity backed by insurance pool"
    }
}

/// One quiet-mode window — the C# `(string Reason, DateTimeOffset StartedAt,
/// DateTimeOffset EndsAt)` tuple, named.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct QuietWindow {
    pub reason: String,
    pub started_at: DateTime<Utc>,
    pub ends_at: DateTime<Utc>,
}

#[async_trait]
pub trait IQuietMode {
    async fn engage(&self, reason: &str, duration: chrono::Duration)
        -> Result<(), DistributionError>;
    fn is_quiet_at(&self, moment: DateTime<Utc>) -> bool;
    fn active_windows(&self) -> Vec<QuietWindow>;
}
#[derive(Default)]
pub struct DefaultQuietMode {
    windows: Mutex<Vec<QuietWindow>>,
}
#[async_trait]
impl IQuietMode for DefaultQuietMode {
    async fn engage(
        &self,
        reason: &str,
        duration: chrono::Duration,
    ) -> Result<(), DistributionError> {
        if reason.trim().is_empty() {
            return Err(DistributionError::InvalidArgument("reason required".into()));
        }
        if duration <= chrono::Duration::zero() {
            return Err(DistributionError::OutOfRange("duration".into()));
        }
        let now = Utc::now();
        self.windows.lock().unwrap().push(QuietWindow {
            reason: reason.to_string(),
            started_at: now,
            ends_at: now + duration,
        });
        Ok(())
    }
    fn is_quiet_at(&self, moment: DateTime<Utc>) -> bool {
        self.windows
            .lock()
            .unwrap()
            .iter()
            .any(|w| moment >= w.started_at && moment <= w.ends_at)
    }
    fn active_windows(&self) -> Vec<QuietWindow> {
        let now = Utc::now();
        self.windows
            .lock()
            .unwrap()
            .iter()
            .filter(|w| w.ends_at >= now)
            .cloned()
            .collect()
    }
}

pub trait IChildProtectionMode {
    fn coppa_compliant(&self) -> bool;
    fn gdpr_k_compliant(&self) -> bool;
}
pub struct DefaultChildProtectionMode;
impl IChildProtectionMode for DefaultChildProtectionMode {
    fn coppa_compliant(&self) -> bool {
        true
    }
    fn gdpr_k_compliant(&self) -> bool {
        true
    }
}

pub trait IReligiousAccommodation {
    fn supported_modes(&self) -> &[String];
}
pub struct DefaultReligiousAccommodation {
    supported_modes: Vec<String>,
}
impl Default for DefaultReligiousAccommodation {
    fn default() -> Self {
        Self {
            supported_modes: ["prayer times", "Shabbat mode", "Eid silence"]
                .iter()
                .map(|s| s.to_string())
                .collect(),
        }
    }
}
impl IReligiousAccommodation for DefaultReligiousAccommodation {
    fn supported_modes(&self) -> &[String] {
        &self.supported_modes
    }
}

pub trait IIndigenousDataSovereignty {
    fn standard(&self) -> &str;
}
pub struct DefaultIndigenousDataSovereignty;
impl IIndigenousDataSovereignty for DefaultIndigenousDataSovereignty {
    fn standard(&self) -> &str {
        "CARE Principles"
    }
}

/// One linked-evidence claim — the C# `(string Claim, Uri Evidence,
/// DateTimeOffset At)` tuple, named. `Uri` → `String`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct LinkedEvidence {
    pub claim: String,
    pub evidence: String,
    pub at: DateTime<Utc>,
}

#[async_trait]
pub trait IPublicTransparency {
    async fn link_evidence(&self, claim: &str, evidence_url: &str)
        -> Result<(), DistributionError>;
    fn linked(&self) -> Vec<LinkedEvidence>;
}
#[derive(Default)]
pub struct DefaultPublicTransparency {
    links: Mutex<Vec<LinkedEvidence>>,
}
#[async_trait]
impl IPublicTransparency for DefaultPublicTransparency {
    async fn link_evidence(
        &self,
        claim: &str,
        evidence_url: &str,
    ) -> Result<(), DistributionError> {
        if claim.trim().is_empty() {
            return Err(DistributionError::InvalidArgument("claim required".into()));
        }
        if !is_absolute_http(evidence_url) {
            return Err(DistributionError::InvalidArgument(
                "evidenceUrl must be absolute http/https".into(),
            ));
        }
        self.links.lock().unwrap().push(LinkedEvidence {
            claim: claim.to_string(),
            evidence: evidence_url.to_string(),
            at: Utc::now(),
        });
        Ok(())
    }
    fn linked(&self) -> Vec<LinkedEvidence> {
        self.links.lock().unwrap().clone()
    }
}

// =====================================================================
// MISSING DEFAULTS (UbiquityRailsMissingDefaults.cs)
// =====================================================================

/// Default app-store submitter — validates the package and records the
/// submission. 1:1 with `DefaultAppStoreSubmitter`.
#[derive(Default)]
pub struct DefaultAppStoreSubmitter {
    submitted: Mutex<HashMap<String, AppStorePackage>>,
}
impl DefaultAppStoreSubmitter {
    fn known_store(name: &str) -> bool {
        const KNOWN: [&str; 6] = [
            "PlayStore",
            "AppStore",
            "Galaxy Store",
            "Huawei AppGallery",
            "Microsoft Store",
            "F-Droid",
        ];
        KNOWN.iter().any(|s| s.eq_ignore_ascii_case(name))
    }
    pub fn submitted(&self) -> Vec<AppStorePackage> {
        self.submitted.lock().unwrap().values().cloned().collect()
    }
}
#[async_trait]
impl IAppStoreSubmitter for DefaultAppStoreSubmitter {
    async fn submit(&self, package: AppStorePackage) -> Result<bool, DistributionError> {
        if package.store_name.trim().is_empty() {
            return Err(DistributionError::InvalidArgument("StoreName required".into()));
        }
        if package.package_path.trim().is_empty() {
            return Err(DistributionError::InvalidArgument("PackagePath required".into()));
        }
        if package.version.trim().is_empty() {
            return Err(DistributionError::InvalidArgument("Version required".into()));
        }
        if !Self::known_store(&package.store_name) {
            return Ok(false);
        }
        let key = format!("{}/{}", package.store_name, package.version);
        self.submitted.lock().unwrap().insert(key, package);
        Ok(true)
    }
}

/// Signed delta updater — verifies HMAC-SHA256 before applying. 1:1 with
/// `DefaultSignedDeltaUpdater`.
pub struct DefaultSignedDeltaUpdater {
    hmac_key: Vec<u8>,
    channel_version: Mutex<HashMap<String, String>>,
}
impl DefaultSignedDeltaUpdater {
    /// `hmac_key` must be at least 16 bytes (the C# guard).
    pub fn new(hmac_key: Vec<u8>) -> Result<Self, DistributionError> {
        if hmac_key.len() < 16 {
            return Err(DistributionError::InvalidArgument(
                "hmacKey must be at least 16 bytes".into(),
            ));
        }
        Ok(Self {
            hmac_key,
            channel_version: Mutex::new(HashMap::new()),
        })
    }
    pub fn current_version(&self, channel: &str) -> Option<String> {
        self.channel_version.lock().unwrap().get(channel).cloned()
    }
}
#[async_trait]
impl ISignedDeltaUpdater for DefaultSignedDeltaUpdater {
    async fn apply(&self, update: DeltaUpdate) -> Result<bool, DistributionError> {
        if update.channel.trim().is_empty() || update.to_version.trim().is_empty() {
            return Ok(false);
        }
        {
            let cv = self.channel_version.lock().unwrap();
            if let Some(current) = cv.get(&update.channel) {
                if current != &update.from_version {
                    return Ok(false);
                }
            }
        }
        // HMAC over Channel|FromVersion|ToVersion|Payload.
        let mut msg = format!(
            "{}|{}|{}|",
            update.channel, update.from_version, update.to_version
        )
        .into_bytes();
        msg.extend_from_slice(&update.payload);
        let expected = hashing::hmac_sha256(&self.hmac_key, &msg);
        if !hashing::fixed_time_eq(&expected, &update.signature) {
            return Ok(false);
        }
        self.channel_version
            .lock()
            .unwrap()
            .insert(update.channel.clone(), update.to_version.clone());
        Ok(true)
    }
}

/// Phone-pin biometric onboarding — real session tracking with PIN strength +
/// biometric flag. 1:1 with `DefaultPhonePinBiometricOnboarding`.
#[derive(Default)]
pub struct DefaultPhonePinBiometricOnboarding {
    sessions: Mutex<HashMap<String, OnboardingSession>>,
    pin_hashes: Mutex<HashMap<String, String>>,
}
impl DefaultPhonePinBiometricOnboarding {
    fn pin_hash(pin: &str, phone: &str) -> String {
        hashing::to_hex(&hashing::sha256(format!("{pin}{phone}").as_bytes()))
    }
    pub fn verify_pin(&self, phone_number: &str, pin: &str) -> bool {
        let hashes = self.pin_hashes.lock().unwrap();
        match hashes.get(phone_number) {
            Some(h) => hashing::fixed_time_eq(
                h.as_bytes(),
                Self::pin_hash(pin, phone_number).as_bytes(),
            ),
            None => false,
        }
    }
}
#[async_trait]
impl IPhonePinBiometricOnboarding for DefaultPhonePinBiometricOnboarding {
    async fn start(&self, phone_number: &str) -> Result<OnboardingSession, DistributionError> {
        if phone_number.trim().is_empty() {
            return Err(DistributionError::InvalidArgument("phoneNumber required".into()));
        }
        if !is_valid_e164(phone_number) {
            return Err(DistributionError::InvalidArgument(format!(
                "Invalid E.164 phone '{phone_number}'."
            )));
        }
        let sid = uuid::Uuid::new_v4().simple().to_string();
        let session = OnboardingSession {
            session_id: sid.clone(),
            phone_number: phone_number.to_string(),
            biometric_enrolled: false,
            time_to_active: chrono::Duration::zero(),
        };
        self.sessions.lock().unwrap().insert(sid, session.clone());
        Ok(session)
    }
    async fn complete(
        &self,
        session_id: &str,
        pin: &str,
        biometric_ok: bool,
    ) -> Result<(), DistributionError> {
        if session_id.trim().is_empty() {
            return Err(DistributionError::InvalidArgument("sessionId required".into()));
        }
        if pin.trim().is_empty() || pin.len() < 4 || !pin.chars().all(|c| c.is_ascii_digit()) {
            return Err(DistributionError::InvalidArgument(
                "PIN must be at least 4 digits".into(),
            ));
        }
        let mut sessions = self.sessions.lock().unwrap();
        let s = sessions
            .get(session_id)
            .cloned()
            .ok_or_else(|| DistributionError::InvalidOperation(format!("Unknown session {session_id}")))?;
        // Placeholder elapsed of 1 minute — mirrors the C# placeholder.
        let elapsed = chrono::Duration::minutes(1);
        self.pin_hashes
            .lock()
            .unwrap()
            .insert(s.phone_number.clone(), Self::pin_hash(pin, &s.phone_number));
        sessions.insert(
            session_id.to_string(),
            OnboardingSession {
                biometric_enrolled: biometric_ok,
                time_to_active: elapsed,
                ..s
            },
        );
        Ok(())
    }
}

/// No-manual first-run — shows a single welcome card. 1:1 with
/// `DefaultNoManualFirstRun`.
pub struct DefaultNoManualFirstRun {
    welcome: String,
}
impl DefaultNoManualFirstRun {
    pub fn new(welcome_card: Option<&str>) -> Self {
        Self {
            welcome: welcome_card
                .unwrap_or("Welcome to Circle AI. Tap the mic and say hello — that's it.")
                .to_string(),
        }
    }
}
impl Default for DefaultNoManualFirstRun {
    fn default() -> Self {
        Self::new(None)
    }
}
#[async_trait]
impl INoManualFirstRun for DefaultNoManualFirstRun {
    async fn show(&self) -> String {
        self.welcome.clone()
    }
}

/// Voice-led setup — accepts supported mother tongues; rejects unknown ones.
/// 1:1 with `DefaultVoiceLedSetup`.
pub struct DefaultVoiceLedSetup;
impl DefaultVoiceLedSetup {
    fn supported(prefix: &str) -> bool {
        const SUPPORTED: [&str; 22] = [
            "en", "af", "zu", "xh", "st", "tn", "ts", "ss", "ve", "nr", "nso", // SA official
            "sw", "ha", "yo", "ig", "am", "fr", "pt", "ar", "hi", "bn", "es", // continent + global
        ];
        SUPPORTED.iter().any(|s| s.eq_ignore_ascii_case(prefix))
    }
}
#[async_trait]
impl IVoiceLedSetup for DefaultVoiceLedSetup {
    async fn run(&self, mother_tongue: &str) -> Result<bool, DistributionError> {
        if mother_tongue.trim().is_empty() {
            return Err(DistributionError::InvalidArgument("motherTongue required".into()));
        }
        let prefix = mother_tongue.split('-').next().unwrap_or(mother_tongue);
        Ok(Self::supported(prefix))
    }
}

/// Personal data import — accepts a registered source name; records the import.
/// 1:1 with `DefaultPersonalDataImport`.
#[derive(Default)]
pub struct DefaultPersonalDataImport {
    imports: Mutex<HashMap<String, Vec<String>>>,
}
impl DefaultPersonalDataImport {
    fn known_source(source: &str) -> bool {
        const KNOWN: [&str; 7] = [
            "google-takeout",
            "apple-data-export",
            "whatsapp-archive",
            "icloud",
            "csv",
            "vcard",
            "ics",
        ];
        KNOWN.iter().any(|s| s.eq_ignore_ascii_case(source))
    }
    pub fn imports_for(&self, session_id: &str) -> Vec<String> {
        self.imports
            .lock()
            .unwrap()
            .get(session_id)
            .cloned()
            .unwrap_or_default()
    }
}
#[async_trait]
impl IPersonalDataImport for DefaultPersonalDataImport {
    async fn import(&self, session_id: &str, source: &str) -> Result<(), DistributionError> {
        if session_id.trim().is_empty() {
            return Err(DistributionError::InvalidArgument("sessionId required".into()));
        }
        if source.trim().is_empty() {
            return Err(DistributionError::InvalidArgument("source required".into()));
        }
        if !Self::known_source(source) {
            return Err(DistributionError::InvalidOperation(format!(
                "Unsupported import source '{source}'."
            )));
        }
        self.imports
            .lock()
            .unwrap()
            .entry(session_id.to_string())
            .or_default()
            .push(source.to_string());
        Ok(())
    }
}

/// Family onboarding — household + member roster with role validation. 1:1 with
/// `DefaultFamilyOnboarding`.
#[derive(Default)]
pub struct DefaultFamilyOnboarding {
    households: Mutex<HashMap<String, Vec<HouseholdMember>>>,
}
impl DefaultFamilyOnboarding {
    fn valid_role(role: &str) -> bool {
        const VALID: [&str; 7] = [
            "owner", "parent", "child", "guardian", "elder", "partner", "guest",
        ];
        VALID.iter().any(|s| s.eq_ignore_ascii_case(role))
    }
    pub fn members_of(&self, owner_id: &str) -> Vec<HouseholdMember> {
        self.households
            .lock()
            .unwrap()
            .get(owner_id)
            .cloned()
            .unwrap_or_default()
    }
}
#[async_trait]
impl IFamilyOnboarding for DefaultFamilyOnboarding {
    async fn create_household(
        &self,
        owner_id: &str,
        members: Vec<HouseholdMember>,
    ) -> Result<(), DistributionError> {
        if owner_id.trim().is_empty() {
            return Err(DistributionError::InvalidArgument("ownerId required".into()));
        }
        for m in &members {
            if m.member_id.trim().is_empty() {
                return Err(DistributionError::InvalidArgument("MemberId required".into()));
            }
            if m.display_name.trim().is_empty() {
                return Err(DistributionError::InvalidArgument("DisplayName required".into()));
            }
            if !Self::valid_role(&m.role) {
                return Err(DistributionError::InvalidOperation(format!(
                    "Unknown role '{}'.",
                    m.role
                )));
            }
        }
        self.households
            .lock()
            .unwrap()
            .insert(owner_id.to_string(), members);
        Ok(())
    }
}

/// Per-call transparency receipt — real receipt store. 1:1 with
/// `DefaultPerCallTransparency`.
#[derive(Default)]
pub struct DefaultPerCallTransparency {
    receipts: Mutex<HashMap<String, TransparencyReceipt>>,
}
impl DefaultPerCallTransparency {
    pub fn record(&self, receipt: TransparencyReceipt) -> Result<(), DistributionError> {
        if receipt.call_id.trim().is_empty() {
            return Err(DistributionError::InvalidArgument("CallId required".into()));
        }
        self.receipts
            .lock()
            .unwrap()
            .insert(receipt.call_id.clone(), receipt);
        Ok(())
    }
}
#[async_trait]
impl IPerCallTransparency for DefaultPerCallTransparency {
    async fn receipt_for(&self, call_id: &str) -> Result<TransparencyReceipt, DistributionError> {
        if call_id.trim().is_empty() {
            return Err(DistributionError::InvalidArgument("callId required".into()));
        }
        Ok(self
            .receipts
            .lock()
            .unwrap()
            .get(call_id)
            .cloned()
            .unwrap_or_else(|| TransparencyReceipt {
                call_id: call_id.to_string(),
                actions_taken: Vec::new(),
                data_egress: Vec::new(),
                cost_usd: 0.0,
            }))
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Shared small helpers
// ─────────────────────────────────────────────────────────────────────────────

/// E.164-ish validation matching the C# regex `^\+?[1-9]\d{6,14}$`.
fn is_valid_e164(phone: &str) -> bool {
    let bytes = phone.as_bytes();
    let mut i = 0;
    if bytes.first() == Some(&b'+') {
        i = 1;
    }
    if i >= bytes.len() || !(b'1'..=b'9').contains(&bytes[i]) {
        return false;
    }
    let digits = bytes.len() - i; // includes the leading 1-9
    if !bytes[i..].iter().all(|b| b.is_ascii_digit()) {
        return false;
    }
    // Leading digit + 6..14 more = total 7..15.
    (7..=15).contains(&digits)
}

/// True when `url` is an absolute http(s) URL — the C# `Uri.IsAbsoluteUri` +
/// scheme check.
fn is_absolute_http(url: &str) -> bool {
    let lower = url.to_lowercase();
    lower.starts_with("http://") || lower.starts_with("https://")
}

/// FNV-1a 32-bit — deterministic, portable stand-in for the (non-portable) C#
/// `string.GetHashCode()` used only for safety-phrase word selection.
fn fnv1a_32(s: &str) -> u32 {
    let mut hash: u32 = 0x811c9dc5;
    for b in s.as_bytes() {
        hash ^= *b as u32;
        hash = hash.wrapping_mul(0x0100_0193);
    }
    hash
}

// ─────────────────────────────────────────────────────────────────────────────
// hashing — self-contained SHA-256 + HMAC-SHA256 + helpers. Stands in for
// System.Security.Cryptography (the crate has no crypto dependency). Used by
// DefaultSignedDeltaUpdater / DefaultVerifiableWipe / DefaultPhonePinBiometric-
// Onboarding to preserve real (not stubbed) crypto behaviour.
// ─────────────────────────────────────────────────────────────────────────────
mod hashing {
    const H0: [u32; 8] = [
        0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab,
        0x5be0cd19,
    ];
    const K: [u32; 64] = [
        0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4,
        0xab1c5ed5, 0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe,
        0x9bdc06a7, 0xc19bf174, 0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f,
        0x4a7484aa, 0x5cb0a9dc, 0x76f988da, 0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7,
        0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967, 0x27b70a85, 0x2e1b2138, 0x4d2c6dfc,
        0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85, 0xa2bfe8a1, 0xa81a664b,
        0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070, 0x19a4c116,
        0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
        0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7,
        0xc67178f2,
    ];

    /// SHA-256 over `data`, 32-byte digest.
    pub fn sha256(data: &[u8]) -> [u8; 32] {
        let mut h = H0;
        let ml = (data.len() as u64).wrapping_mul(8);
        let mut msg = data.to_vec();
        msg.push(0x80);
        while msg.len() % 64 != 56 {
            msg.push(0);
        }
        msg.extend_from_slice(&ml.to_be_bytes());

        for chunk in msg.chunks_exact(64) {
            let mut w = [0u32; 64];
            for (i, wi) in w.iter_mut().enumerate().take(16) {
                *wi = u32::from_be_bytes([
                    chunk[i * 4],
                    chunk[i * 4 + 1],
                    chunk[i * 4 + 2],
                    chunk[i * 4 + 3],
                ]);
            }
            for i in 16..64 {
                let s0 = w[i - 15].rotate_right(7) ^ w[i - 15].rotate_right(18) ^ (w[i - 15] >> 3);
                let s1 = w[i - 2].rotate_right(17) ^ w[i - 2].rotate_right(19) ^ (w[i - 2] >> 10);
                w[i] = w[i - 16]
                    .wrapping_add(s0)
                    .wrapping_add(w[i - 7])
                    .wrapping_add(s1);
            }
            let mut a = h[0];
            let mut b = h[1];
            let mut c = h[2];
            let mut d = h[3];
            let mut e = h[4];
            let mut f = h[5];
            let mut g = h[6];
            let mut hh = h[7];
            for i in 0..64 {
                let s1 = e.rotate_right(6) ^ e.rotate_right(11) ^ e.rotate_right(25);
                let ch = (e & f) ^ ((!e) & g);
                let t1 = hh
                    .wrapping_add(s1)
                    .wrapping_add(ch)
                    .wrapping_add(K[i])
                    .wrapping_add(w[i]);
                let s0 = a.rotate_right(2) ^ a.rotate_right(13) ^ a.rotate_right(22);
                let maj = (a & b) ^ (a & c) ^ (b & c);
                let t2 = s0.wrapping_add(maj);
                hh = g;
                g = f;
                f = e;
                e = d.wrapping_add(t1);
                d = c;
                c = b;
                b = a;
                a = t1.wrapping_add(t2);
            }
            h[0] = h[0].wrapping_add(a);
            h[1] = h[1].wrapping_add(b);
            h[2] = h[2].wrapping_add(c);
            h[3] = h[3].wrapping_add(d);
            h[4] = h[4].wrapping_add(e);
            h[5] = h[5].wrapping_add(f);
            h[6] = h[6].wrapping_add(g);
            h[7] = h[7].wrapping_add(hh);
        }

        let mut out = [0u8; 32];
        for (i, word) in h.iter().enumerate() {
            out[i * 4..i * 4 + 4].copy_from_slice(&word.to_be_bytes());
        }
        out
    }

    /// HMAC-SHA256(key, msg), 32-byte tag.
    pub fn hmac_sha256(key: &[u8], msg: &[u8]) -> [u8; 32] {
        const BLOCK: usize = 64;
        let mut k = if key.len() > BLOCK {
            sha256(key).to_vec()
        } else {
            key.to_vec()
        };
        k.resize(BLOCK, 0);
        let mut ipad = [0x36u8; BLOCK];
        let mut opad = [0x5cu8; BLOCK];
        for i in 0..BLOCK {
            ipad[i] ^= k[i];
            opad[i] ^= k[i];
        }
        let mut inner = ipad.to_vec();
        inner.extend_from_slice(msg);
        let inner_hash = sha256(&inner);
        let mut outer = opad.to_vec();
        outer.extend_from_slice(&inner_hash);
        sha256(&outer)
    }

    /// Constant-time byte-slice equality — the C# `CryptographicOperations.
    /// FixedTimeEquals`.
    pub fn fixed_time_eq(a: &[u8], b: &[u8]) -> bool {
        if a.len() != b.len() {
            return false;
        }
        let mut diff = 0u8;
        for (x, y) in a.iter().zip(b.iter()) {
            diff |= x ^ y;
        }
        diff == 0
    }

    /// Uppercase hex — the C# `Convert.ToHexString`.
    pub fn to_hex(bytes: &[u8]) -> String {
        let mut s = String::with_capacity(bytes.len() * 2);
        for b in bytes {
            s.push_str(&format!("{b:02X}"));
        }
        s
    }

    /// Standard base64 (with padding) — the C# `Convert.ToBase64String`, enough
    /// for the wipe-certificate nonce.
    pub fn base64_encode(data: &[u8]) -> String {
        const TABLE: &[u8; 64] =
            b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        let mut out = String::new();
        for chunk in data.chunks(3) {
            let b0 = chunk[0] as u32;
            let b1 = *chunk.get(1).unwrap_or(&0) as u32;
            let b2 = *chunk.get(2).unwrap_or(&0) as u32;
            let n = (b0 << 16) | (b1 << 8) | b2;
            out.push(TABLE[((n >> 18) & 63) as usize] as char);
            out.push(TABLE[((n >> 12) & 63) as usize] as char);
            if chunk.len() > 1 {
                out.push(TABLE[((n >> 6) & 63) as usize] as char);
            } else {
                out.push('=');
            }
            if chunk.len() > 2 {
                out.push(TABLE[(n & 63) as usize] as char);
            } else {
                out.push('=');
            }
        }
        out
    }

    /// 16 pseudo-random bytes derived from a UUIDv4 (the crate already depends on
    /// `uuid`), sufficient for a non-secret certificate nonce.
    pub fn random_bytes(n: usize) -> Vec<u8> {
        let mut out = Vec::with_capacity(n);
        while out.len() < n {
            out.extend_from_slice(uuid::Uuid::new_v4().as_bytes());
        }
        out.truncate(n);
        out
    }
}
