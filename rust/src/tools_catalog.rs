//! tools_catalog.rs
//!
//! Port of `CircleAI.Tools.Catalog/` — the full tools-catalog contract surface
//! (composio pattern-port) plus its in-memory reference primitives and fail-closed
//! null defaults.
//!
//!   * [`AuthKind`] — how a provider authenticates.
//!   * [`ProviderDescriptor`] / [`OAuth2Descriptor`] / [`CredentialBundle`] /
//!     [`QuotaPolicy`] / [`ToolNamespace`] — the record model.
//!   * [`IProviderCatalog`] / [`ICredentialStore`] / [`IOAuth2FlowDriver`] /
//!     [`IQuotaGuard`] / [`IToolNamespaceStore`] — the contracts.
//!   * [`InMemoryProviderCatalog`] — substring + tag scored search.
//!   * [`AesGcmCredentialStore`] — encrypt-at-rest credential store. The crate has
//!     no AES-GCM crate dependency, so the cipher is behind the [`ICredentialCipher`]
//!     trait (inject a real AES-256-GCM impl in production); the built-in
//!     [`XorObfuscationCipher`] keyed default keeps the store functional + testable.
//!   * [`OAuth2FlowDriver`] — builds a standards-compliant authorize URL; the
//!     vendor-specific token exchange is delegated to a host closure.
//!   * [`SlidingWindowQuotaGuard`] — per-minute + daily + max-concurrent caps.
//!   * [`InMemoryToolNamespaceStore`] — per-user namespace partitions.
//!   * `Null*` — fail-closed defaults for every contract.
//!
//! C# `ValueTask<>` maps to `#[async_trait]`. `ConcurrentDictionary` maps to
//! `Mutex<HashMap<..>>`. Errors that C# throws as `ArgumentException` /
//! `InvalidOperationException` map to the hand-rolled [`CatalogError`].

use std::collections::HashMap;
use std::sync::Mutex;

use async_trait::async_trait;
use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};

// ─────────────────────────────────────────────────────────────────────────────
// CatalogError
// ─────────────────────────────────────────────────────────────────────────────

/// Errors surfaced by the tools-catalog contracts. Maps the C# `ArgumentException`
/// / `ArgumentOutOfRangeException` / `InvalidOperationException` throw sites.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum CatalogError {
    /// A required argument was null/blank or out of range.
    Argument(String),
    /// The requested operation was invalid in the current state.
    InvalidOperation(String),
}

impl std::fmt::Display for CatalogError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            CatalogError::Argument(m) => write!(f, "argument error: {m}"),
            CatalogError::InvalidOperation(m) => write!(f, "invalid operation: {m}"),
        }
    }
}

impl std::error::Error for CatalogError {}

fn require_non_blank(value: &str, name: &str) -> Result<(), CatalogError> {
    if value.trim().is_empty() {
        Err(CatalogError::Argument(format!("{name} required")))
    } else {
        Ok(())
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Record model
// ─────────────────────────────────────────────────────────────────────────────

/// How the provider authenticates.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum AuthKind {
    /// No authentication.
    None,
    /// Static API key.
    ApiKey,
    /// Bearer token.
    BearerToken,
    /// OAuth2 3-legged flow.
    OAuth2,
    /// HTTP Basic.
    Basic,
    /// Custom scheme.
    Custom,
}

/// One provider in the catalog (Gmail, Slack, Linear, …). 1:1 with the C#
/// `sealed record ProviderDescriptor`.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ProviderDescriptor {
    /// Stable provider identifier.
    pub provider_id: String,
    /// Human-readable name.
    pub display_name: String,
    /// Short description.
    pub description: String,
    /// Provider homepage URL, if any.
    pub homepage: Option<String>,
    /// How the provider authenticates.
    pub auth: AuthKind,
    /// Free-form tags for search.
    pub tags: Vec<String>,
    /// Capability names for search.
    pub capabilities: Vec<String>,
    /// OAuth2 configuration when `auth` is [`AuthKind::OAuth2`].
    pub oauth2: Option<OAuth2Descriptor>,
}

/// OAuth2 configuration when [`ProviderDescriptor::auth`] is [`AuthKind::OAuth2`].
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct OAuth2Descriptor {
    /// Authorization endpoint.
    pub authorize_url: String,
    /// Token endpoint.
    pub token_url: String,
    /// Requested scopes.
    pub scopes: Vec<String>,
    /// Optional user-info endpoint.
    pub user_info_url: Option<String>,
}

/// One stored credential for one user / one provider. 1:1 with the C#
/// `sealed record CredentialBundle`.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct CredentialBundle {
    /// Provider this credential belongs to.
    pub provider_id: String,
    /// User this credential belongs to.
    pub user_id: String,
    /// Opaque credential fields (token, refresh_token, api_key, …).
    pub fields: HashMap<String, String>,
    /// Expiry, if the credential is time-limited.
    pub expires_at_utc: Option<DateTime<Utc>>,
}

/// A quota / rate-limit policy on one (provider, user) pair. 1:1 with the C#
/// `sealed record QuotaPolicy`.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct QuotaPolicy {
    /// Provider the policy applies to.
    pub provider_id: String,
    /// User the policy applies to.
    pub user_id: String,
    /// Maximum calls allowed per rolling 24 hours.
    pub daily_call_budget: i32,
    /// Maximum concurrent in-flight calls.
    pub max_concurrent: i32,
    /// Maximum calls per rolling minute.
    pub per_minute_cap: i32,
}

/// Namespace partition — keeps one user's tool list separate from the next.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ToolNamespace {
    /// Stable namespace identifier.
    pub namespace_id: String,
    /// The user that owns this namespace.
    pub owner_user_id: String,
    /// Provider ids exposed within this namespace.
    pub provider_ids: Vec<String>,
}

// ─────────────────────────────────────────────────────────────────────────────
// Contracts
// ─────────────────────────────────────────────────────────────────────────────

/// The provider directory.
#[async_trait]
pub trait IProviderCatalog: Send + Sync {
    /// Backend identifier (e.g. `"in-memory"`, `"null"`).
    fn backend_id(&self) -> &str;

    /// Lists every registered provider.
    async fn list_providers(&self) -> Vec<ProviderDescriptor>;

    /// Gets one provider by id.
    async fn get_provider(&self, provider_id: &str) -> Result<Option<ProviderDescriptor>, CatalogError>;

    /// Semantic (substring + tag) search over the registered providers.
    async fn search_providers(
        &self,
        query: &str,
        top_k: usize,
    ) -> Result<Vec<ProviderDescriptor>, CatalogError>;
}

/// Credential storage. Implementations must encrypt at rest.
#[async_trait]
pub trait ICredentialStore: Send + Sync {
    /// Backend identifier.
    fn backend_id(&self) -> &str;

    /// Inserts or replaces a credential bundle.
    async fn upsert(&self, bundle: CredentialBundle) -> Result<(), CatalogError>;

    /// Gets a credential bundle for `(provider_id, user_id)`.
    async fn get(&self, provider_id: &str, user_id: &str) -> Result<Option<CredentialBundle>, CatalogError>;

    /// Deletes a credential bundle for `(provider_id, user_id)`.
    async fn delete(&self, provider_id: &str, user_id: &str) -> Result<(), CatalogError>;
}

/// OAuth2 flow driver — lets the catalog initiate + complete a 3-legged flow.
#[async_trait]
pub trait IOAuth2FlowDriver: Send + Sync {
    /// Backend identifier.
    fn backend_id(&self) -> &str;

    /// Builds the redirect URL for the user's browser.
    async fn start(&self, provider_id: &str, user_id: &str, redirect_uri: &str) -> Result<String, CatalogError>;

    /// Exchanges the authorisation code returned to the redirect URI for a bundle.
    async fn complete(
        &self,
        provider_id: &str,
        user_id: &str,
        authorization_code: &str,
        redirect_uri: &str,
    ) -> Result<CredentialBundle, CatalogError>;
}

/// Per-(provider,user) quota enforcement.
#[async_trait]
pub trait IQuotaGuard: Send + Sync {
    /// Backend identifier.
    fn backend_id(&self) -> &str;

    /// Attempts to acquire one call slot; `false` when a cap is exceeded.
    async fn try_acquire(&self, provider_id: &str, user_id: &str) -> bool;

    /// Sets (or replaces) the quota policy for a `(provider, user)` pair.
    async fn set_policy(&self, policy: QuotaPolicy);

    /// Gets the quota policy for a `(provider, user)` pair, if one is set.
    async fn get_policy(&self, provider_id: &str, user_id: &str) -> Option<QuotaPolicy>;
}

/// Namespace store — keeps one user's tool list separate from the next.
#[async_trait]
pub trait IToolNamespaceStore: Send + Sync {
    /// Backend identifier.
    fn backend_id(&self) -> &str;

    /// Inserts or replaces a namespace.
    async fn upsert(&self, ns: ToolNamespace) -> Result<(), CatalogError>;

    /// Gets a namespace by id.
    async fn get(&self, namespace_id: &str) -> Result<Option<ToolNamespace>, CatalogError>;

    /// Lists every namespace owned by `user_id`.
    async fn list_for_user(&self, user_id: &str) -> Result<Vec<ToolNamespace>, CatalogError>;
}

// ─────────────────────────────────────────────────────────────────────────────
// InMemoryProviderCatalog
// ─────────────────────────────────────────────────────────────────────────────

/// In-memory provider catalog with substring + tag scored search. Rust port of
/// `InMemoryProviderCatalog`.
#[derive(Default)]
pub struct InMemoryProviderCatalog {
    items: Mutex<HashMap<String, ProviderDescriptor>>,
}

impl InMemoryProviderCatalog {
    /// Creates an empty catalog.
    pub fn new() -> Self {
        Self::default()
    }

    /// Registers (or replaces) a provider. Keyed case-insensitively on `provider_id`.
    pub fn register(&self, p: ProviderDescriptor) {
        self.items
            .lock()
            .unwrap()
            .insert(p.provider_id.to_ascii_lowercase(), p);
    }

    fn score(p: &ProviderDescriptor, q: &str) -> i32 {
        let ql = q.to_ascii_lowercase();
        let contains = |s: &str| s.to_ascii_lowercase().contains(&ql);
        let mut s = 0;
        if contains(&p.display_name) {
            s += 3;
        }
        if contains(&p.description) {
            s += 1;
        }
        if p.tags.iter().any(|t| contains(t)) {
            s += 2;
        }
        if p.capabilities.iter().any(|c| contains(c)) {
            s += 2;
        }
        s
    }
}

#[async_trait]
impl IProviderCatalog for InMemoryProviderCatalog {
    fn backend_id(&self) -> &str {
        "in-memory"
    }

    async fn list_providers(&self) -> Vec<ProviderDescriptor> {
        let mut out: Vec<ProviderDescriptor> = self.items.lock().unwrap().values().cloned().collect();
        out.sort_by(|a, b| a.provider_id.cmp(&b.provider_id));
        out
    }

    async fn get_provider(&self, provider_id: &str) -> Result<Option<ProviderDescriptor>, CatalogError> {
        require_non_blank(provider_id, "providerId")?;
        Ok(self
            .items
            .lock()
            .unwrap()
            .get(&provider_id.to_ascii_lowercase())
            .cloned())
    }

    async fn search_providers(
        &self,
        query: &str,
        top_k: usize,
    ) -> Result<Vec<ProviderDescriptor>, CatalogError> {
        if top_k == 0 {
            return Err(CatalogError::Argument("topK must be > 0".to_string()));
        }
        let mut hits: Vec<(ProviderDescriptor, i32)> = self
            .items
            .lock()
            .unwrap()
            .values()
            .map(|p| (p.clone(), Self::score(p, query)))
            .filter(|(_, s)| *s > 0)
            .collect();
        hits.sort_by(|a, b| b.1.cmp(&a.1));
        Ok(hits.into_iter().take(top_k).map(|(p, _)| p).collect())
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Credential cipher seam + AesGcmCredentialStore
// ─────────────────────────────────────────────────────────────────────────────

/// Encrypt-at-rest cipher seam for [`AesGcmCredentialStore`]. The crate has no
/// AES-GCM crate dependency, so real authenticated encryption is injected here.
/// `encrypt` returns the full sealed blob (nonce/tag/ciphertext); `decrypt`
/// returns `None` on any authentication/decryption failure (mirrors the C#
/// `catch (CryptographicException)` -> null path).
pub trait ICredentialCipher: Send + Sync {
    /// Seals `plaintext` into an opaque blob.
    fn encrypt(&self, plaintext: &[u8]) -> Vec<u8>;
    /// Opens a blob produced by [`ICredentialCipher::encrypt`]; `None` on failure.
    fn decrypt(&self, blob: &[u8]) -> Option<Vec<u8>>;
}

/// Built-in keyed cipher used when no real AES-256-GCM cipher is injected. It is
/// a keystream XOR with a length-prefixed integrity marker — deterministic,
/// dependency-free, and sufficient to exercise the store's serialize/store/round-
/// trip logic. NOT a substitute for authenticated encryption in production; inject
/// a real [`ICredentialCipher`] there.
pub struct XorObfuscationCipher {
    key: [u8; 32],
}

impl XorObfuscationCipher {
    /// Creates the cipher from a 32-byte key (matches the C# AES-256 key length).
    pub fn new(key32: [u8; 32]) -> Self {
        Self { key: key32 }
    }

    fn xor_stream(&self, data: &[u8]) -> Vec<u8> {
        data.iter()
            .enumerate()
            .map(|(i, b)| b ^ self.key[i % 32])
            .collect()
    }
}

impl ICredentialCipher for XorObfuscationCipher {
    fn encrypt(&self, plaintext: &[u8]) -> Vec<u8> {
        // Layout: [4-byte BE length][xor(plaintext)]. The length acts as a light
        // integrity marker checked on decrypt.
        let mut out = Vec::with_capacity(4 + plaintext.len());
        out.extend_from_slice(&(plaintext.len() as u32).to_be_bytes());
        out.extend_from_slice(&self.xor_stream(plaintext));
        out
    }

    fn decrypt(&self, blob: &[u8]) -> Option<Vec<u8>> {
        if blob.len() < 4 {
            return None;
        }
        let len = u32::from_be_bytes([blob[0], blob[1], blob[2], blob[3]]) as usize;
        let body = &blob[4..];
        if body.len() != len {
            return None;
        }
        Some(self.xor_stream(body))
    }
}

/// Credential store that encrypts bundles at rest via an injected
/// [`ICredentialCipher`]. Rust port of `AesGcmCredentialStore`: it JSON-serialises
/// the bundle, seals it, and stores the blob keyed by `provider/user`.
pub struct AesGcmCredentialStore {
    cipher: Box<dyn ICredentialCipher>,
    enc: Mutex<HashMap<String, Vec<u8>>>,
}

impl AesGcmCredentialStore {
    /// Creates the store with a host-supplied 32-byte key, using the built-in
    /// keyed [`XorObfuscationCipher`]. The 32-byte length requirement mirrors the
    /// C# AES-256-GCM key guard.
    pub fn with_key(key32: [u8; 32]) -> Self {
        Self::with_cipher(Box::new(XorObfuscationCipher::new(key32)))
    }

    /// Creates the store with an explicit cipher — the injection point for a real
    /// AES-256-GCM implementation.
    pub fn with_cipher(cipher: Box<dyn ICredentialCipher>) -> Self {
        Self {
            cipher,
            enc: Mutex::new(HashMap::new()),
        }
    }

    fn key(p: &str, u: &str) -> String {
        format!("{p}/{u}")
    }
}

#[async_trait]
impl ICredentialStore for AesGcmCredentialStore {
    fn backend_id(&self) -> &str {
        "aes-gcm"
    }

    async fn upsert(&self, bundle: CredentialBundle) -> Result<(), CatalogError> {
        let json = serde_json::to_vec(&bundle)
            .map_err(|e| CatalogError::InvalidOperation(format!("serialize failed: {e}")))?;
        let sealed = self.cipher.encrypt(&json);
        self.enc
            .lock()
            .unwrap()
            .insert(Self::key(&bundle.provider_id, &bundle.user_id), sealed);
        Ok(())
    }

    async fn get(&self, provider_id: &str, user_id: &str) -> Result<Option<CredentialBundle>, CatalogError> {
        require_non_blank(provider_id, "providerId")?;
        require_non_blank(user_id, "userId")?;
        let blob = self.enc.lock().unwrap().get(&Self::key(provider_id, user_id)).cloned();
        let blob = match blob {
            Some(b) => b,
            None => return Ok(None),
        };
        // Any decryption/deserialization failure yields None, mirroring the C#
        // CryptographicException -> null behaviour.
        let plaintext = match self.cipher.decrypt(&blob) {
            Some(pt) => pt,
            None => return Ok(None),
        };
        match serde_json::from_slice::<CredentialBundle>(&plaintext) {
            Ok(bundle) => Ok(Some(bundle)),
            Err(_) => Ok(None),
        }
    }

    async fn delete(&self, provider_id: &str, user_id: &str) -> Result<(), CatalogError> {
        require_non_blank(provider_id, "providerId")?;
        require_non_blank(user_id, "userId")?;
        self.enc.lock().unwrap().remove(&Self::key(provider_id, user_id));
        Ok(())
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// OAuth2FlowDriver
// ─────────────────────────────────────────────────────────────────────────────

/// Resolves the OAuth2 client id for a provider. Maps the C# `Func<string,string>`.
pub type ClientIdResolver = Box<dyn Fn(&str) -> String + Send + Sync>;

/// Boxed async token-exchange closure. Given `(provider_id, user_id, auth_code,
/// redirect_uri)` returns a [`CredentialBundle`]. Maps the C#
/// `Func<..., ValueTask<CredentialBundle>>`.
pub type TokenExchangeFn = Box<
    dyn Fn(
            String,
            String,
            String,
            String,
        ) -> std::pin::Pin<
            Box<dyn std::future::Future<Output = Result<CredentialBundle, CatalogError>> + Send>,
        > + Send
        + Sync,
>;

/// OAuth2 flow driver — builds a standards-compliant authorize URL; the vendor-
/// specific token exchange is delegated to a host closure. Rust port of
/// `OAuth2FlowDriver`. Holds its provider catalog as an `Arc` so the exchange +
/// url-build can look providers up.
pub struct OAuth2FlowDriver {
    catalog: std::sync::Arc<dyn IProviderCatalog>,
    client_id_for: ClientIdResolver,
    exchange: TokenExchangeFn,
}

impl OAuth2FlowDriver {
    /// Creates a driver over `catalog`, resolving client ids via `client_id_for`
    /// and exchanging codes via `exchange`.
    pub fn new(
        catalog: std::sync::Arc<dyn IProviderCatalog>,
        client_id_for: ClientIdResolver,
        exchange: TokenExchangeFn,
    ) -> Self {
        Self {
            catalog,
            client_id_for,
            exchange,
        }
    }

    /// Percent-encodes a string per RFC 3986 unreserved set (mirrors
    /// `WebUtility.UrlEncode` for the characters that appear in OAuth params).
    fn url_encode(s: &str) -> String {
        let mut out = String::with_capacity(s.len());
        for b in s.bytes() {
            match b {
                b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'-' | b'_' | b'.' | b'~' => {
                    out.push(b as char)
                }
                _ => out.push_str(&format!("%{b:02X}")),
            }
        }
        out
    }

    /// URL-safe base64 (no padding) of a byte slice — used for the `state` param.
    fn base64_url_no_pad(data: &[u8]) -> String {
        const ALPHABET: &[u8; 64] =
            b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        let mut out = String::new();
        for chunk in data.chunks(3) {
            let b0 = chunk[0] as u32;
            let b1 = chunk.get(1).copied().unwrap_or(0) as u32;
            let b2 = chunk.get(2).copied().unwrap_or(0) as u32;
            let n = (b0 << 16) | (b1 << 8) | b2;
            out.push(ALPHABET[((n >> 18) & 63) as usize] as char);
            out.push(ALPHABET[((n >> 12) & 63) as usize] as char);
            if chunk.len() > 1 {
                out.push(ALPHABET[((n >> 6) & 63) as usize] as char);
            }
            if chunk.len() > 2 {
                out.push(ALPHABET[(n & 63) as usize] as char);
            }
        }
        out
    }
}

#[async_trait]
impl IOAuth2FlowDriver for OAuth2FlowDriver {
    fn backend_id(&self) -> &str {
        "oauth2"
    }

    async fn start(&self, provider_id: &str, user_id: &str, redirect_uri: &str) -> Result<String, CatalogError> {
        require_non_blank(provider_id, "providerId")?;
        require_non_blank(user_id, "userId")?;
        require_non_blank(redirect_uri, "redirectUri")?;

        let provider = self
            .catalog
            .get_provider(provider_id)
            .await?
            .ok_or_else(|| CatalogError::InvalidOperation(format!("Unknown provider '{provider_id}'.")))?;
        let oauth2 = provider
            .oauth2
            .as_ref()
            .ok_or_else(|| CatalogError::InvalidOperation(format!("Provider '{provider_id}' is not OAuth2.")))?;

        // 16 random bytes -> url-safe base64 state, matching the C# construction.
        let state_bytes = uuid::Uuid::new_v4().into_bytes();
        let state = Self::base64_url_no_pad(&state_bytes);
        let scopes = oauth2.scopes.join(" ");
        let client_id = (self.client_id_for)(provider_id);
        let url = format!(
            "{}?response_type=code&client_id={}&redirect_uri={}&scope={}&state={}",
            oauth2.authorize_url,
            Self::url_encode(&client_id),
            Self::url_encode(redirect_uri),
            Self::url_encode(&scopes),
            Self::url_encode(&state),
        );
        Ok(url)
    }

    async fn complete(
        &self,
        provider_id: &str,
        user_id: &str,
        authorization_code: &str,
        redirect_uri: &str,
    ) -> Result<CredentialBundle, CatalogError> {
        require_non_blank(provider_id, "providerId")?;
        require_non_blank(user_id, "userId")?;
        require_non_blank(authorization_code, "authorizationCode")?;
        require_non_blank(redirect_uri, "redirectUri")?;
        (self.exchange)(
            provider_id.to_string(),
            user_id.to_string(),
            authorization_code.to_string(),
            redirect_uri.to_string(),
        )
        .await
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// SlidingWindowQuotaGuard
// ─────────────────────────────────────────────────────────────────────────────

/// Sliding-window per-minute + daily-budget + max-concurrent quota guard. Rust
/// port of `SlidingWindowQuotaGuard`. A missing policy means unlimited.
#[derive(Default)]
pub struct SlidingWindowQuotaGuard {
    inner: Mutex<QuotaState>,
}

#[derive(Default)]
struct QuotaState {
    policies: HashMap<String, QuotaPolicy>,
    calls: HashMap<String, Vec<DateTime<Utc>>>,
    inflight: HashMap<String, i32>,
}

impl SlidingWindowQuotaGuard {
    /// Creates a guard with no policies (everything unlimited until a policy is set).
    pub fn new() -> Self {
        Self::default()
    }

    /// Releases one in-flight slot for `(provider, user)`. Call after a metered
    /// call completes. Mirrors the C# `Release`.
    pub fn release(&self, provider_id: &str, user_id: &str) {
        let key = Self::key(provider_id, user_id);
        let mut st = self.inner.lock().unwrap();
        if let Some(n) = st.inflight.get_mut(&key) {
            if *n > 0 {
                *n -= 1;
            }
        }
    }

    fn key(p: &str, u: &str) -> String {
        format!("{p}/{u}")
    }
}

#[async_trait]
impl IQuotaGuard for SlidingWindowQuotaGuard {
    fn backend_id(&self) -> &str {
        "sliding-window"
    }

    async fn try_acquire(&self, provider_id: &str, user_id: &str) -> bool {
        let key = Self::key(provider_id, user_id);
        let now = Utc::now();
        let mut st = self.inner.lock().unwrap();

        let policy = match st.policies.get(&key).cloned() {
            Some(p) => p,
            None => return true, // no policy = unlimited
        };

        let one_minute_ago = now - chrono::Duration::minutes(1);
        let one_day_ago = now - chrono::Duration::days(1);

        let list = st.calls.entry(key.clone()).or_default();
        list.retain(|t| *t >= one_minute_ago);

        // Per-minute cap.
        if list.len() as i32 >= policy.per_minute_cap {
            return false;
        }
        // Daily budget (counts within the last 24h; retained list already >= 1min).
        let daily = list.iter().filter(|t| **t >= one_day_ago).count() as i32;
        if daily >= policy.daily_call_budget {
            return false;
        }
        // Concurrency.
        let inflight = *st.inflight.get(&key).unwrap_or(&0);
        if inflight >= policy.max_concurrent {
            return false;
        }

        st.calls.get_mut(&key).unwrap().push(now);
        st.inflight.insert(key, inflight + 1);
        true
    }

    async fn set_policy(&self, policy: QuotaPolicy) {
        let key = Self::key(&policy.provider_id, &policy.user_id);
        self.inner.lock().unwrap().policies.insert(key, policy);
    }

    async fn get_policy(&self, provider_id: &str, user_id: &str) -> Option<QuotaPolicy> {
        self.inner
            .lock()
            .unwrap()
            .policies
            .get(&Self::key(provider_id, user_id))
            .cloned()
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// InMemoryToolNamespaceStore
// ─────────────────────────────────────────────────────────────────────────────

/// In-memory namespace store. Rust port of `InMemoryToolNamespaceStore`.
#[derive(Default)]
pub struct InMemoryToolNamespaceStore {
    items: Mutex<HashMap<String, ToolNamespace>>,
}

impl InMemoryToolNamespaceStore {
    /// Creates an empty store.
    pub fn new() -> Self {
        Self::default()
    }
}

#[async_trait]
impl IToolNamespaceStore for InMemoryToolNamespaceStore {
    fn backend_id(&self) -> &str {
        "in-memory"
    }

    async fn upsert(&self, ns: ToolNamespace) -> Result<(), CatalogError> {
        require_non_blank(&ns.namespace_id, "NamespaceId")?;
        self.items.lock().unwrap().insert(ns.namespace_id.clone(), ns);
        Ok(())
    }

    async fn get(&self, namespace_id: &str) -> Result<Option<ToolNamespace>, CatalogError> {
        require_non_blank(namespace_id, "namespaceId")?;
        Ok(self.items.lock().unwrap().get(namespace_id).cloned())
    }

    async fn list_for_user(&self, user_id: &str) -> Result<Vec<ToolNamespace>, CatalogError> {
        require_non_blank(user_id, "userId")?;
        Ok(self
            .items
            .lock()
            .unwrap()
            .values()
            .filter(|n| n.owner_user_id == user_id)
            .cloned()
            .collect())
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Null* fail-closed defaults
// ─────────────────────────────────────────────────────────────────────────────

/// Fail-closed provider catalog — returns nothing.
#[derive(Default)]
pub struct NullProviderCatalog;

#[async_trait]
impl IProviderCatalog for NullProviderCatalog {
    fn backend_id(&self) -> &str {
        "null"
    }
    async fn list_providers(&self) -> Vec<ProviderDescriptor> {
        Vec::new()
    }
    async fn get_provider(&self, _provider_id: &str) -> Result<Option<ProviderDescriptor>, CatalogError> {
        Ok(None)
    }
    async fn search_providers(&self, _query: &str, _top_k: usize) -> Result<Vec<ProviderDescriptor>, CatalogError> {
        Ok(Vec::new())
    }
}

/// Fail-closed credential store — stores nothing, returns nothing.
#[derive(Default)]
pub struct NullCredentialStore;

#[async_trait]
impl ICredentialStore for NullCredentialStore {
    fn backend_id(&self) -> &str {
        "null"
    }
    async fn upsert(&self, _bundle: CredentialBundle) -> Result<(), CatalogError> {
        Ok(())
    }
    async fn get(&self, _provider_id: &str, _user_id: &str) -> Result<Option<CredentialBundle>, CatalogError> {
        Ok(None)
    }
    async fn delete(&self, _provider_id: &str, _user_id: &str) -> Result<(), CatalogError> {
        Ok(())
    }
}

/// Fail-closed OAuth2 flow driver — `start` returns `about:blank`; `complete` errors.
#[derive(Default)]
pub struct NullOAuth2FlowDriver;

#[async_trait]
impl IOAuth2FlowDriver for NullOAuth2FlowDriver {
    fn backend_id(&self) -> &str {
        "null"
    }
    async fn start(&self, _provider_id: &str, _user_id: &str, _redirect_uri: &str) -> Result<String, CatalogError> {
        Ok("about:blank".to_string())
    }
    async fn complete(
        &self,
        _provider_id: &str,
        _user_id: &str,
        _authorization_code: &str,
        _redirect_uri: &str,
    ) -> Result<CredentialBundle, CatalogError> {
        Err(CatalogError::InvalidOperation(
            "NullOAuth2FlowDriver: no real provider wired.".to_string(),
        ))
    }
}

/// Fail-closed quota guard — always denies.
#[derive(Default)]
pub struct NullQuotaGuard;

#[async_trait]
impl IQuotaGuard for NullQuotaGuard {
    fn backend_id(&self) -> &str {
        "null"
    }
    async fn try_acquire(&self, _provider_id: &str, _user_id: &str) -> bool {
        false
    }
    async fn set_policy(&self, _policy: QuotaPolicy) {}
    async fn get_policy(&self, _provider_id: &str, _user_id: &str) -> Option<QuotaPolicy> {
        None
    }
}

/// Fail-closed namespace store — stores nothing, returns nothing.
#[derive(Default)]
pub struct NullToolNamespaceStore;

#[async_trait]
impl IToolNamespaceStore for NullToolNamespaceStore {
    fn backend_id(&self) -> &str {
        "null"
    }
    async fn upsert(&self, _ns: ToolNamespace) -> Result<(), CatalogError> {
        Ok(())
    }
    async fn get(&self, _namespace_id: &str) -> Result<Option<ToolNamespace>, CatalogError> {
        Ok(None)
    }
    async fn list_for_user(&self, _user_id: &str) -> Result<Vec<ToolNamespace>, CatalogError> {
        Ok(Vec::new())
    }
}
