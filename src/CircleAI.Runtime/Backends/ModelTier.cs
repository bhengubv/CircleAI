// ModelTier.cs
//
// Tier-based sizing for Qwen / DeepSeek / GLM / Kimi families. Picked by
// IBackendSelector based on available compute headroom. The exact model ID
// per tier comes from the model registry (CircleAI.Core embedded_registry.json
// and downstream model managers); the tier here is the routing key.

namespace CircleAI.Runtime.Backends;

/// <summary>
/// Capability tier that maps to a Qwen / DeepSeek / GLM / Kimi model size band.
/// Higher tiers require more RAM / VRAM. Tier0 is the always-runnable floor
/// (≈600 MB footprint); Tier4 targets 24 GB+ VRAM frontier models.
/// </summary>
public enum ModelTier
{
    /// <summary>Tier 0 — Qwen3-0.6B class. ≈600 MB. CPU-friendly. Always available.</summary>
    Tier0_Tiny = 0,
    /// <summary>Tier 1 — 1.7B–4B class. ≈2 GB. CPU usable but slow; GPU preferred.</summary>
    Tier1_Small = 1,
    /// <summary>Tier 2 — 7B–9B class Q4. ≈6 GB. Needs ≥8 GB VRAM or ≥16 GB RAM.</summary>
    Tier2_Medium = 2,
    /// <summary>Tier 3 — 14B–32B class Q4. ≈12 GB. Needs ≥12 GB VRAM or ≥32 GB RAM.</summary>
    Tier3_Large = 3,
    /// <summary>Tier 4 — 70B+ class Q4, or 32B Q6. ≈24 GB+. Frontier / data-centre tier.</summary>
    Tier4_Frontier = 4,
}
