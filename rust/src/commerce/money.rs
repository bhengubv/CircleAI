//! money.rs
//!
//! Helpers for rendering `decimal` money values inside companion-adapter prompt
//! strings. The C# adapters interpolate money two ways:
//!
//! - `{value:C}` — the .NET currency format specifier (culture-dependent). The
//!   surrounding prompt text is the load-bearing part (these strings are handed
//!   to an LLM, never asserted byte-for-byte cross-language), so [`currency`]
//!   renders a stable 2-decimal form. It intentionally does NOT emit a culture
//!   symbol, matching the invariant-culture-safe posture of this port.
//! - `{value}` — the plain `decimal.ToString()`. [`plain`] reproduces the
//!   ".NET drops trailing zeros only past the scale" behaviour closely enough
//!   for prompt text: it prints an integer with no decimal point, otherwise the
//!   shortest round-trippable form.

/// Renders a money value like the C# `{value:C}` slot (2 decimals, no symbol).
pub(crate) fn currency(value: f64) -> String {
    format!("{value:.2}")
}

/// Renders a money value like the C# `{value}` slot (plain `ToString`).
pub(crate) fn plain(value: f64) -> String {
    if value.fract() == 0.0 {
        format!("{}", value as i64)
    } else {
        format!("{value}")
    }
}
