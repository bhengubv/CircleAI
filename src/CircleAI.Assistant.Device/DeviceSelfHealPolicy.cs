// DeviceSelfHealPolicy.cs
//
// The head's answer to the two questions the self-heal loop asks: how autonomous has
// the person set it, and is the brain ready to diagnose. Autonomy is read from the
// per-service setting the Wolverine dashboard writes; readiness is the one shared
// on-device brain's own state — so the loop never calls it cold.

using System.Threading;
using System.Threading.Tasks;
using CircleAI.Assistant;
using CircleAI.Hosting.SelfHealing;

namespace CircleAI.Assistant.Device;

/// <summary>The on-device <see cref="ISelfHealPolicy"/>: autonomy from settings, readiness from the brain.</summary>
public sealed class DeviceSelfHealPolicy : ISelfHealPolicy
{
    private readonly ISettings _settings;
    private readonly IBrain _brain;

    public DeviceSelfHealPolicy(ISettings settings, IBrain brain)
    {
        _settings = settings ?? throw new System.ArgumentNullException(nameof(settings));
        _brain = brain ?? throw new System.ArgumentNullException(nameof(brain));
    }

    private async Task<AutonomyLevel> LevelAsync(CancellationToken ct)
        => AutonomyLevels.Parse(await _settings
            .ServiceSettingAsync("selfheal", "autonomy", AutonomyLevels.AutoFixSafe, ct)
            .ConfigureAwait(false));

    /// <inheritdoc />
    public async Task<bool> ShouldAnalyseAsync(CancellationToken ct = default)
        => await LevelAsync(ct).ConfigureAwait(false) != AutonomyLevel.Off;

    /// <inheritdoc />
    public async Task<bool> MayAutoFixAsync(CancellationToken ct = default)
        => await LevelAsync(ct).ConfigureAwait(false) == AutonomyLevel.AutoFixSafe;

    /// <inheritdoc />
    public async Task<bool> BrainReadyAsync(CancellationToken ct = default)
    {
        try { return (await _brain.StateAsync(ct).ConfigureAwait(false)).Ready; }
        catch { return false; }   // if we cannot tell, assume not ready — the cautious side.
    }
}
