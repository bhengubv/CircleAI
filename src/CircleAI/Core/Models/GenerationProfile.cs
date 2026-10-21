// GenerationProfile.cs
//
// HOW A MODEL IS ASKED TO GENERATE, AS REGISTRY DATA.
//
// MNN reads its sampler from the bundle's config.json at load and ignores the per-call
// GenerationOptions.Temperature/TopP/TopK entirely - so the answers on the P30 were
// sampled by settings nobody on this side had ever read. The profile is applied through
// mnn_llm_set_config BEFORE load, which is the only moment MNN reads it.

using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CircleAI.Core.Models;

/// <summary>The sampler MNN builds at load.</summary>
/// <param name="SamplerType">MNN's sampler_type: "greedy" or "mixed".</param>
/// <param name="Temperature">Sampling temperature.</param>
/// <param name="TopP">Nucleus cutoff.</param>
/// <param name="TopK">Top-k cutoff.</param>
/// <param name="MinP">Min-p cutoff.</param>
/// <param name="Penalty">Repetition penalty (1.0 = none).</param>
public sealed record SamplerProfile(
    string SamplerType = "mixed",
    double Temperature = 0.7,
    double TopP = 0.8,
    int TopK = 20,
    double MinP = 0.0,
    double Penalty = 1.05);

/// <summary>Thinking, sampler and answer lengths for one model.</summary>
/// <param name="EnableThinking">Whether the model may think out loud. Always false for the brain.</param>
/// <param name="Sampler">The sampler, or null for the bundle's own.</param>
/// <param name="MaxTokensSpoken">The longest answer to a spoken question.</param>
/// <param name="MaxTokensTyped">The longest answer to a typed question.</param>
public sealed record GenerationProfile(
    bool EnableThinking = false,
    SamplerProfile? Sampler = null,
    int MaxTokensSpoken = 160,
    int MaxTokensTyped = 512)
{
    /// <summary>The MNN config JSON for the sampler, or null when the bundle's own is kept.</summary>
    /// <remarks>
    /// MNN's own key names (transformers/README.md): sampler_type, mixed_samplers,
    /// temperature, topK, topP, minP, penalty.
    /// <para>
    /// GREEDY IS ITS TYPE AND NOTHING ELSE, and "penalty" is the repetition penalty and
    /// then greedy - the two configs measured on the P30 on 2026-10-20, sent exactly as
    /// measured. Greedy answered one prompt the same six times out of six, and looped on
    /// two of eight typed answers ("Use coupons on your phone" twice; a history repeated
    /// whole). Penalty-then-greedy was as deterministic and looped on none. "mixed" with
    /// topK 1 was NOT greedy on this MNN build: one prompt, six different answers.
    /// </para>
    /// </remarks>
    public string? SamplerConfigJson()
    {
        if (Sampler is not { } s) return null;
        if (s.SamplerType == "greedy") return "{\"sampler_type\":\"greedy\"}";
        if (s.SamplerType == "penalty")
            return "{\"sampler_type\":\"penalty\",\"penalty\":" + D(s.Penalty) + ",\"penalty_sampler\":\"greedy\"}";
        var mixed = new List<string>();
        if (s.Penalty > 1.0) mixed.Add("penalty");
        mixed.AddRange(new[] { "topK", "topP", "minP", "temperature" });

        var sb = new StringBuilder("{");
        sb.Append("\"sampler_type\":").Append(JsonSerializer.Serialize(s.SamplerType));
        if (s.SamplerType == "mixed")
            sb.Append(",\"mixed_samplers\":").Append(JsonSerializer.Serialize(mixed));
        sb.Append(",\"temperature\":").Append(D(s.Temperature));
        sb.Append(",\"topK\":").Append(s.TopK.ToString(CultureInfo.InvariantCulture));
        sb.Append(",\"topP\":").Append(D(s.TopP));
        sb.Append(",\"minP\":").Append(D(s.MinP));
        sb.Append(",\"penalty\":").Append(D(s.Penalty));
        return sb.Append('}').ToString();
    }

    private static string D(double v) => v.ToString("0.0###", CultureInfo.InvariantCulture);
}
