//! agents.rs
//!
//! AgentMessage with auto-synthesised correlation ID (32-char hex).

use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};
use uuid::Uuid;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "PascalCase")]
pub enum AgentMessageKind {
    Discover = 0,
    Greet = 1,
    CapabilityQuery = 2,
    Invoke = 3,
    Response = 4,
    Decline = 5,
    Heartbeat = 6,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AgentMessage {
    pub id: Uuid,
    pub kind: AgentMessageKind,
    #[serde(rename = "fromUhid")]
    pub from_uhid: String,
    #[serde(rename = "toUhid")]
    pub to_uhid: String,
    #[serde(rename = "contentType")]
    pub content_type: String,
    pub payload: Vec<u8>,
    pub signature: Vec<u8>,
    #[serde(rename = "sentAt")]
    pub sent_at: DateTime<Utc>,
    #[serde(rename = "correlationId")]
    pub correlation_id: String,
}

impl AgentMessage {
    pub fn create(
        kind: AgentMessageKind,
        from_uhid: impl Into<String>,
        to_uhid: impl Into<String>,
        content_type: impl Into<String>,
        payload: Vec<u8>,
        signature: Vec<u8>,
        correlation_id: Option<String>,
    ) -> Self {
        let cid = match correlation_id {
            Some(c) if !c.is_empty() => c,
            _ => synth_correlation_id(),
        };
        Self {
            id: Uuid::new_v4(),
            kind,
            from_uhid: from_uhid.into(),
            to_uhid: to_uhid.into(),
            content_type: content_type.into(),
            payload,
            signature,
            sent_at: Utc::now(),
            correlation_id: cid,
        }
    }
}

fn synth_correlation_id() -> String {
    // 16 bytes of random → 32 lowercase hex chars. Matches C# / Go / Swift behaviour.
    let bytes = Uuid::new_v4().as_bytes().to_owned();
    let mut s = String::with_capacity(32);
    for b in bytes {
        use std::fmt::Write;
        let _ = write!(s, "{:02x}", b);
    }
    s
}
