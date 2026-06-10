//! selector.rs
//!
//! ChatCapability bitflags, IModelSelector, DeviceAwareModelSelector.

use bitflags::bitflags;
use serde::{Deserialize, Serialize};

use crate::catalog::ModelEntry;
use crate::device::{DeviceSnapshot, DeviceTier, IDeviceContext};

bitflags! {
    /// What features a chat model can do. Bitwise composable.
    #[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
    pub struct ChatCapability: u32 {
        const NONE        = 0b0000_0000;
        const TEXT        = 0b0000_0001;
        const TOOLS       = 0b0000_0010;
        const VISION      = 0b0000_0100;
        const AUDIO       = 0b0000_1000;
        const LONG_CTX    = 0b0001_0000;
        const REASONING   = 0b0010_0000;
        const STREAMING   = 0b0100_0000;
        const DEFAULT     = Self::TEXT.bits() | Self::STREAMING.bits();
    }
}

/// Parse a "Capabilities" field from a catalog entry (comma- or space-separated).
pub fn parse_capabilities(raw: &str) -> ChatCapability {
    let mut out = ChatCapability::NONE;
    for tok in raw.split(|c: char| c == ',' || c.is_whitespace()) {
        match tok.trim().to_ascii_lowercase().as_str() {
            "" => continue,
            "text" => out |= ChatCapability::TEXT,
            "tools" => out |= ChatCapability::TOOLS,
            "vision" => out |= ChatCapability::VISION,
            "audio" => out |= ChatCapability::AUDIO,
            "longctx" | "long_ctx" | "long-ctx" => out |= ChatCapability::LONG_CTX,
            "reasoning" => out |= ChatCapability::REASONING,
            "streaming" => out |= ChatCapability::STREAMING,
            _ => {}
        }
    }
    out
}

/// Outcome of a selector call.
#[derive(Debug, Clone)]
pub struct ModelSelection {
    pub entry: ModelEntry,
    pub reason: String,
}

/// Strategy that picks a model given a device snapshot + required capability set.
pub trait IModelSelector: Send + Sync {
    fn select(
        &self,
        candidates: &[ModelEntry],
        device: &DeviceSnapshot,
        required: ChatCapability,
    ) -> Option<ModelSelection>;
}

/// Default selector. Filters by capability + tier ceiling, picks smallest
/// `total_bytes` that fits.
pub struct DeviceAwareModelSelector<'a> {
    pub device_context: &'a dyn IDeviceContext,
}

impl<'a> DeviceAwareModelSelector<'a> {
    pub fn new(device_context: &'a dyn IDeviceContext) -> Self {
        Self { device_context }
    }
}

impl<'a> IModelSelector for DeviceAwareModelSelector<'a> {
    fn select(
        &self,
        candidates: &[ModelEntry],
        device: &DeviceSnapshot,
        required: ChatCapability,
    ) -> Option<ModelSelection> {
        let max_bytes_for_tier = match device.tier {
            DeviceTier::Wearable => 200 * 1024 * 1024,
            DeviceTier::Embedded => 500 * 1024 * 1024,
            DeviceTier::Phone => 2_500_000_000,
            DeviceTier::Tablet => 6_000_000_000,
            DeviceTier::Laptop => 20_000_000_000,
            DeviceTier::Workstation => 60_000_000_000,
        };

        let mut viable: Vec<&ModelEntry> = candidates
            .iter()
            .filter(|c| {
                let caps = parse_capabilities(c.capabilities.as_deref().unwrap_or("Text,Streaming"));
                caps.contains(required) && c.total_bytes <= max_bytes_for_tier
            })
            .collect();

        if viable.is_empty() {
            return None;
        }
        viable.sort_by_key(|c| c.total_bytes);
        let chosen = viable[0].clone();
        let reason = format!(
            "tier={:?} ram={}MB required={:?} → {} ({} bytes)",
            device.tier,
            device.ram_bytes / (1024 * 1024),
            required.bits(),
            chosen.name,
            chosen.total_bytes
        );
        Some(ModelSelection { entry: chosen, reason })
    }
}
