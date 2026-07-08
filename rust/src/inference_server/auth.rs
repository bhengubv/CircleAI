//! auth.rs
//!
//! API-key authentication, ported from
//! `CircleAI.Inference.Server/Auth/ApiKeyAuthHandler.cs` + `AuthSchemes.cs`.
//!
//! The C# handler is an ASP.NET `AuthenticationHandler`; there is no HTTP stack
//! here, so the equivalent logic is exposed as an in-memory
//! [`ApiKeyAuthHandler::authenticate`] over the request headers. The
//! enabled/disabled behaviour, header lookup, and constant-time key comparison
//! reproduce the C# exactly.

use std::collections::BTreeMap;

/// Identifiers for the auth schemes the server registers. Mirrors `AuthSchemes`.
pub struct AuthSchemes;

impl AuthSchemes {
    /// API-key auth scheme name.
    pub const API_KEY: &'static str = "ApiKey";
    /// JWT Bearer auth scheme name.
    pub const JWT: &'static str = "Bearer";
    /// Policy name for endpoints requiring an authenticated caller.
    pub const AUTHENTICATED_POLICY: &'static str = "Authenticated";
}

/// API-key option block. Mirrors the server's `ApiKeyOptions`.
#[derive(Debug, Clone)]
pub struct ApiKeyOptions {
    /// When false the handler succeeds with a synthetic "anonymous" principal so
    /// dev environments don't need keys.
    pub enabled: bool,
    /// The header carrying the key (e.g. `X-API-Key`).
    pub header_name: String,
    /// The allow-list of accepted keys.
    pub keys: Vec<String>,
}

impl Default for ApiKeyOptions {
    fn default() -> Self {
        Self {
            enabled: false,
            header_name: "X-API-Key".to_string(),
            keys: Vec::new(),
        }
    }
}

/// One resolved claim on the authenticated principal.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Claim {
    pub claim_type: String,
    pub value: String,
}

impl Claim {
    fn new(t: impl Into<String>, v: impl Into<String>) -> Self {
        Self {
            claim_type: t.into(),
            value: v.into(),
        }
    }
}

/// The three-way outcome of an authentication attempt — mirrors ASP.NET's
/// `AuthenticateResult` Success / NoResult / Fail.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum AuthResult {
    /// Authenticated — carries the principal's claims.
    Success(Vec<Claim>),
    /// No credential presented (missing/blank header).
    NoResult,
    /// A credential was presented but rejected.
    Fail(String),
}

impl AuthResult {
    /// `true` when the caller is authenticated.
    pub fn is_success(&self) -> bool {
        matches!(self, AuthResult::Success(_))
    }
}

/// API-key authentication handler. Reads the configured header and matches
/// against the option allow-list with a constant-time compare. Mirrors
/// `ApiKeyAuthHandler`.
#[derive(Debug, Clone, Default)]
pub struct ApiKeyAuthHandler {
    options: ApiKeyOptions,
}

impl ApiKeyAuthHandler {
    /// Constructs a handler over the given options.
    pub fn new(options: ApiKeyOptions) -> Self {
        Self { options }
    }

    /// Authenticate a request given its headers (case-insensitive lookup on the
    /// configured header name). Reproduces the C# `HandleAuthenticateAsync`.
    pub fn authenticate(&self, headers: &BTreeMap<String, String>) -> AuthResult {
        let cfg = &self.options;

        if !cfg.enabled {
            // Auth disabled — succeed with a marker identity.
            return AuthResult::Success(vec![
                Claim::new("name", "anonymous"),
                Claim::new("scheme", AuthSchemes::API_KEY),
                Claim::new("auth_disabled", "true"),
            ]);
        }

        let raw = headers
            .iter()
            .find(|(k, _)| k.eq_ignore_ascii_case(&cfg.header_name))
            .map(|(_, v)| v.as_str());

        let raw = match raw {
            Some(v) if !v.trim().is_empty() => v,
            _ => return AuthResult::NoResult,
        };

        if !try_match_key(raw, &cfg.keys) {
            return AuthResult::Fail("Invalid API key.".to_string());
        }

        AuthResult::Success(vec![
            Claim::new("name", "api-key-caller"),
            Claim::new("scheme", AuthSchemes::API_KEY),
        ])
    }
}

/// Constant-time match against any configured key. Mirrors the C# `TryMatchKey`
/// (`FixedTimeEquals`, length-guarded).
fn try_match_key(presented: &str, allowed: &[String]) -> bool {
    if allowed.is_empty() {
        return false;
    }
    let presented_bytes = presented.as_bytes();
    let mut matched = false;
    for k in allowed {
        if k.is_empty() {
            continue;
        }
        let key_bytes = k.as_bytes();
        if key_bytes.len() != presented_bytes.len() {
            continue;
        }
        if fixed_time_eq(key_bytes, presented_bytes) {
            matched = true;
        }
    }
    matched
}

/// Constant-time byte comparison (equal length). Equivalent to
/// `CryptographicOperations.FixedTimeEquals`.
fn fixed_time_eq(a: &[u8], b: &[u8]) -> bool {
    if a.len() != b.len() {
        return false;
    }
    let mut diff: u8 = 0;
    for (x, y) in a.iter().zip(b.iter()) {
        diff |= x ^ y;
    }
    diff == 0
}
