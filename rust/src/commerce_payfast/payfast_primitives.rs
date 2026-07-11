//! payfast_primitives.rs
//!
//! (3.3.0) PayFast integration primitives — Rust port of
//! `src/CircleAI.Commerce.Integration.PayFast/PayFastPrimitives.cs`: real
//! signature builder, ITN validation params, in-memory webhook recorder. The
//! HTTP-side callbacks are wired by the host.
//!
//! The C# `SignatureFor` iterates an `IReadOnlyDictionary` in order; to preserve
//! that ordering faithfully the Rust API takes an ordered slice of
//! `(key, value)` pairs. URL-encoding matches .NET `WebUtility.UrlEncode`
//! (uppercase `%XX`, space → `+`, unreserved `A-Za-z0-9-_.!*()`) followed by the
//! redundant `.Replace("%20", "+")`. The MD5 + lowercase-hex mirror
//! `MD5.ComputeHash(...)` + `Convert.ToHexString(...).ToLowerInvariant()`.

use std::sync::Mutex;

use super::md5;

/// (3.3.0) PayFast merchant configuration.
///
/// Mirrors `sealed record PayFastConfig(string MerchantId, string MerchantKey,
/// string Passphrase, bool Sandbox)`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PayFastConfig {
    pub merchant_id: String,
    pub merchant_key: String,
    pub passphrase: String,
    pub sandbox: bool,
}

impl PayFastConfig {
    /// Constructs a config, mirroring the positional C# record constructor.
    pub fn new(
        merchant_id: impl Into<String>,
        merchant_key: impl Into<String>,
        passphrase: impl Into<String>,
        sandbox: bool,
    ) -> Self {
        Self {
            merchant_id: merchant_id.into(),
            merchant_key: merchant_key.into(),
            passphrase: passphrase.into(),
            sandbox,
        }
    }
}

/// (3.3.0) An Instant Transaction Notification payload.
///
/// Mirrors `sealed record PayFastItnPayload(string MerchantId, string PaymentId,
/// string PaymentStatus, decimal Amount, string MPaymentId, string Signature)`.
/// `decimal Amount` → [`f64`].
#[derive(Debug, Clone, PartialEq)]
pub struct PayFastItnPayload {
    pub merchant_id: String,
    pub payment_id: String,
    pub payment_status: String,
    pub amount: f64,
    pub m_payment_id: String,
    pub signature: String,
}

impl PayFastItnPayload {
    /// Constructs an ITN payload, mirroring the positional C# record constructor.
    pub fn new(
        merchant_id: impl Into<String>,
        payment_id: impl Into<String>,
        payment_status: impl Into<String>,
        amount: f64,
        m_payment_id: impl Into<String>,
        signature: impl Into<String>,
    ) -> Self {
        Self {
            merchant_id: merchant_id.into(),
            payment_id: payment_id.into(),
            payment_status: payment_status.into(),
            amount,
            m_payment_id: m_payment_id.into(),
            signature: signature.into(),
        }
    }
}

/// URL-encodes `value` like .NET `WebUtility.UrlEncode` then applies the C#
/// `.Replace("%20", "+")`. Unreserved: `A-Za-z0-9` and `- _ . ! * ( )`; space →
/// `+`; every other byte → uppercase `%XX` over the UTF-8 encoding.
fn url_encode_payfast(value: &str) -> String {
    let mut out = String::with_capacity(value.len());
    for &b in value.as_bytes() {
        let is_unreserved = b.is_ascii_alphanumeric()
            || matches!(b, b'-' | b'_' | b'.' | b'!' | b'*' | b'(' | b')');
        if is_unreserved {
            out.push(b as char);
        } else if b == b' ' {
            out.push('+');
        } else {
            out.push('%');
            out.push_str(&format!("{b:02X}"));
        }
    }
    // The C# also does `.Replace("%20", "+")`; space is already `+` above so this
    // is a no-op, but reproduce it for exactness against the source.
    out.replace("%20", "+")
}

/// (3.3.0) The PayFast board contract.
///
/// Mirrors `interface IPayFastBoard`. The `Config` getter becomes
/// [`config`](IPayFastBoard::config).
pub trait IPayFastBoard {
    /// The merchant configuration.
    fn config(&self) -> &PayFastConfig;
    /// Builds the MD5 signature for an ordered set of fields (+ passphrase).
    fn signature_for(&self, ordered_fields: &[(String, String)]) -> String;
    /// Verifies an ITN payload (merchant-id match — the host adds transport
    /// checks).
    fn verify_itn(&self, p: &PayFastItnPayload) -> bool;
    /// Records a received webhook.
    fn record_webhook(&self, p: PayFastItnPayload);
    /// Up to `limit` most-recent webhooks, newest-first.
    fn recent_webhooks(&self, limit: usize) -> Vec<PayFastItnPayload>;
}

/// (3.3.0) In-memory [`IPayFastBoard`].
pub struct InMemoryPayFastBoard {
    config: PayFastConfig,
    webhooks: Mutex<Vec<PayFastItnPayload>>,
}

impl InMemoryPayFastBoard {
    /// Wraps a config with an empty webhook log.
    pub fn new(cfg: PayFastConfig) -> Self {
        Self {
            config: cfg,
            webhooks: Mutex::new(Vec::new()),
        }
    }
}

impl IPayFastBoard for InMemoryPayFastBoard {
    fn config(&self) -> &PayFastConfig {
        &self.config
    }

    fn signature_for(&self, ordered_fields: &[(String, String)]) -> String {
        let mut sb = String::new();
        for (key, value) in ordered_fields {
            sb.push_str(key);
            sb.push('=');
            sb.push_str(&url_encode_payfast(value));
            sb.push('&');
        }
        if !self.config.passphrase.is_empty() {
            sb.push_str("passphrase=");
            sb.push_str(&url_encode_payfast(&self.config.passphrase));
        } else if sb.ends_with('&') {
            // Drop the trailing '&' (C# `sb.Length--`).
            sb.pop();
        }
        let hash = md5::compute(sb.as_bytes());
        md5::to_hex_lower(&hash)
    }

    fn verify_itn(&self, p: &PayFastItnPayload) -> bool {
        p.merchant_id == self.config.merchant_id
    }

    fn record_webhook(&self, p: PayFastItnPayload) {
        self.webhooks.lock().unwrap().push(p);
    }

    fn recent_webhooks(&self, limit: usize) -> Vec<PayFastItnPayload> {
        // C#: `_webhooks.AsEnumerable().Reverse().Take(limit)` — newest-first.
        self.webhooks
            .lock()
            .unwrap()
            .iter()
            .rev()
            .take(limit)
            .cloned()
            .collect()
    }
}

/// The default `recent_webhooks` limit in the C# `RecentWebhooks(int limit = 20)`.
pub const DEFAULT_WEBHOOK_LIMIT: usize = 20;
