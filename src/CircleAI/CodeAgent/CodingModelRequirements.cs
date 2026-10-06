// CodingModelRequirements.cs
//
// The on-device floor a REAL coding model must clear before "code from my
// mobile" is honest. A 3-7B coding model is not a toy chat model — it needs a
// capable phone's RAM budget and several GB of free storage for the bundle.
// Below this floor the answer is not "a smaller coding model," it is "not on
// this device" — so the gate returns Unavailable rather than pretending.

using CircleAI.Core;       // DeviceTier
using CircleAI.Inference;  // ChatCapability

namespace CircleAI.CodeAgent;

/// <summary>
/// The hardware + capability floor for running a coding model on-device. The
/// <see cref="CodingCapabilityPlanner"/> gates against this before it ever
/// consults the catalogue, so a weak phone is declined on hardware grounds
/// alone.
/// </summary>
/// <remarks>
/// THE NUMBERS HERE ARE PROVISIONAL AND HOST-OVERRIDABLE. The repo's selector
/// discipline is explicit that real functional thresholds must come from
/// on-device <c>CircleAI.SelfBench</c> measurements rather than being invented
/// in source and hardened into a contract everything downstream trusts
/// (see <c>IModelSelector.BestFit(..., int minQualityRank)</c>). These defaults
/// encode the coarse, defensible fact that a 3-7B model does not fit a
/// sub-6&#160;GB phone; a host with measured data should pass its own instance.
/// </remarks>
/// <param name="MinParametersBillion">
/// Smallest coding model worth running on-device, in billions of parameters.
/// A model below this is not a coding model, it is autocomplete.
/// </param>
/// <param name="MinRamGb">
/// Minimum available RAM. A quantised 3-7B model plus its KV cache does not fit
/// a 4&#160;GB phone (the P30 Lite case) — that device is Unavailable by design.
/// </param>
/// <param name="MinFreeStorageGb">Minimum free storage for the downloaded bundle.</param>
/// <param name="MinDeviceTier">
/// Minimum <see cref="DeviceTier"/>. An 8&#160;GB phone classifies as
/// <see cref="DeviceTier.Tablet"/> under <see cref="DeviceProbe.Classify"/>
/// (the RAM&#160;&#8805;&#160;6&#160;GB rule), so <c>Tablet</c> is the honest
/// "capable phone" floor and <see cref="DeviceTier.Phone"/>/<c>Wearable</c> are
/// declined.
/// </param>
/// <param name="RequiredCapabilities">
/// Capability flags a chat model must declare to be usable as a coding brain:
/// <see cref="ChatCapability.Tools"/> (it must emit tool-call blocks so the loop
/// can act), plus reasoning and long context. Passed straight to the catalogue
/// filter.
/// </param>
public sealed record CodingModelRequirements(
    int            MinParametersBillion,
    double         MinRamGb,
    double         MinFreeStorageGb,
    DeviceTier     MinDeviceTier,
    ChatCapability RequiredCapabilities)
{
    /// <summary>
    /// Provisional default floor: a 3B+ coding model, ~8&#160;GB RAM, ~6&#160;GB
    /// free storage, at least <see cref="DeviceTier.Tablet"/>, and a
    /// tool-calling / reasoning / long-context chat brain. Override with
    /// SelfBench-measured thresholds where available.
    /// </summary>
    public static CodingModelRequirements Default { get; } = new(
        MinParametersBillion: 3,
        MinRamGb:             8.0,
        MinFreeStorageGb:     6.0,
        MinDeviceTier:        DeviceTier.Tablet,
        RequiredCapabilities: ChatCapability.Tools
                            | ChatCapability.Reasoning
                            | ChatCapability.LongContext);
}
