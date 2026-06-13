// PowerBudget.cs
//
// Per-call power budget. CircleAI 1.7.0 introduces RT-11: a declarative knob that
// lets the caller say HOW MUCH device energy this generation is worth, and lets
// the runtime decide WHAT to spend it on (model size, KV compression, context
// window, decode token limit).
//
// The integrator says how much; the runtime says how. This matters most on
// cheap phones where draining 2% of battery on a "hi" reply is unacceptable but
// spending 8% on a long planning reply is fine.

namespace CircleAI.Inference;

/// <summary>
/// Per-call power budget. The runtime maps the budget to context size, KV
/// compression mode, decode token limit, and (when fallback chains are
/// configured) which model in the chain to use.
/// <para>
/// Default behaviour is <see cref="Normal"/>. When the device's battery drops
/// below 15% the runtime auto-downgrades <c>Normal</c> to <c>Low</c>; pass
/// <see cref="None"/> to opt out of automatic adjustment.
/// </para>
/// </summary>
public enum PowerBudget
{
    /// <summary>
    /// Opt out of automatic budget control entirely. The runtime honours
    /// <see cref="GenerationOptions.MaxTokens"/> and the configured KV
    /// compression mode literally.
    /// </summary>
    None = 0,

    /// <summary>
    /// Battery-conscious. Caps tokens at ~64, prefers TQ4 KV compression,
    /// picks the smaller model in a fallback chain when one is configured.
    /// Use for short replies (chat acknowledgements, quick lookups) when the
    /// device is below 30% battery or thermally constrained.
    /// </summary>
    Low = 1,

    /// <summary>
    /// Default balanced behaviour. Honours the caller's <see cref="GenerationOptions.MaxTokens"/>
    /// but caps it at ~512, uses TQ4 KV compression, and picks the chain head.
    /// Automatically downgrades to <see cref="Low"/> when battery is below 15%.
    /// </summary>
    Normal = 2,

    /// <summary>
    /// Quality-first. Allows up to ~2048 tokens, full FP16 KV cache when the
    /// device can afford it, and picks the chain head model. Use sparingly —
    /// long replies, complex reasoning. Auto-throttles to <see cref="Normal"/>
    /// on thermal warnings.
    /// </summary>
    High = 3,
}

/// <summary>
/// The runtime's translation of a <see cref="PowerBudget"/> into concrete
/// generation knobs. Surfaced as a static helper so generators (and tests)
/// agree on the mapping without each having to hard-code it.
/// </summary>
public static class PowerBudgetPolicy
{
    /// <summary>Resolved budget for a single generation call.</summary>
    /// <param name="MaxTokens">Cap on output tokens for this call.</param>
    /// <param name="PreferredKvMode">
    /// Which <see cref="KvCompressionMode"/> the runtime prefers for this
    /// budget. The actual mode applied depends on whether the model handle's
    /// load-time mode allows runtime changes; current MNN builds set this at
    /// load() time so this acts as a HINT for future handles.
    /// </param>
    /// <param name="PreferSmallerModelInChain">
    /// When a fallback chain is configured (RT-08), whether to pick a smaller
    /// model than the chain head. <c>true</c> for <see cref="PowerBudget.Low"/>.
    /// </param>
    public readonly record struct Resolution(
        int               MaxTokens,
        KvCompressionMode PreferredKvMode,
        bool              PreferSmallerModelInChain);

    /// <summary>
    /// Map a budget to concrete knobs. Generators call this with the user's
    /// requested <see cref="GenerationOptions"/>; the returned <see cref="Resolution"/>
    /// caps any over-budget values without altering the caller's struct.
    /// </summary>
    /// <param name="budget">The declared budget.</param>
    /// <param name="requestedMaxTokens">The caller's requested max-tokens.</param>
    /// <param name="batteryLevelPercent">
    /// 0..100 if known, <c>null</c> when the platform doesn't surface it.
    /// Used to auto-downgrade <see cref="PowerBudget.Normal"/> on low battery.
    /// </param>
    /// <param name="thermalThrottled">
    /// <c>true</c> when the platform reports an elevated thermal state.
    /// Used to auto-downgrade <see cref="PowerBudget.High"/>.
    /// </param>
    public static Resolution Resolve(
        PowerBudget budget,
        int         requestedMaxTokens,
        int?        batteryLevelPercent = null,
        bool        thermalThrottled    = false)
    {
        // Auto-downgrade based on device state.
        if (budget == PowerBudget.Normal && batteryLevelPercent is < 15)
            budget = PowerBudget.Low;
        if (budget == PowerBudget.High && thermalThrottled)
            budget = PowerBudget.Normal;

        return budget switch
        {
            PowerBudget.None   => new Resolution(
                MaxTokens:                requestedMaxTokens,
                PreferredKvMode:          KvCompressionMode.TurboQuant4Bit,
                PreferSmallerModelInChain: false),

            PowerBudget.Low    => new Resolution(
                MaxTokens:                System.Math.Min(requestedMaxTokens, 64),
                PreferredKvMode:          KvCompressionMode.TurboQuant4Bit,
                PreferSmallerModelInChain: true),

            PowerBudget.Normal => new Resolution(
                MaxTokens:                System.Math.Min(requestedMaxTokens, 512),
                PreferredKvMode:          KvCompressionMode.TurboQuant4Bit,
                PreferSmallerModelInChain: false),

            PowerBudget.High   => new Resolution(
                MaxTokens:                System.Math.Min(requestedMaxTokens, 2048),
                PreferredKvMode:          KvCompressionMode.Off,
                PreferSmallerModelInChain: false),

            _ => new Resolution(requestedMaxTokens, KvCompressionMode.TurboQuant4Bit, false),
        };
    }
}
