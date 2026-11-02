// DeviceSelfHealPolicy.cs
//
// The head's answer to the two questions the self-heal loop asks: how autonomous has
// the person set it, and is the brain ready to diagnose. Autonomy is read from the
// per-service setting the Wolverine dashboard writes; readiness is the one shared
// on-device brain's own state — so the loop never calls it cold.
//
// TWO WAYS TO ANSWER, ONE POLICY (S6). An app head has an ISettings and an IBrain. The
// service, which is where the loop now runs, has neither: its settings live in its own
// SqliteAppStore under the same "service.selfheal.autonomy" key, and its brain is the
// supervised inference process. The second constructor takes those two answers as
// delegates, so the rule - Off records only, AutoFixSafe may act, a cold brain is never
// asked - stays in this one class.

using System;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Assistant;
using CircleAI.Hosting.SelfHealing;

namespace CircleAI.Assistant.Device;

/// <summary>The on-device <see cref="ISelfHealPolicy"/>: autonomy from settings, readiness from the brain.</summary>
public sealed class DeviceSelfHealPolicy : ISelfHealPolicy
{
    private readonly Func<CancellationToken, Task<string?>> _autonomy;
    private readonly Func<CancellationToken, Task<bool>> _brainReady;

    public DeviceSelfHealPolicy(ISettings settings, IBrain brain)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(brain);
        _autonomy = ct => settings.ServiceSettingAsync("selfheal", "autonomy", AutonomyLevels.AutoFixSafe, ct);
        _brainReady = async ct => (await brain.StateAsync(ct).ConfigureAwait(false)).Ready;
    }

    /// <summary>For a host with no ISettings or IBrain - the service: the dial's stored value, and the brain's readiness.</summary>
    /// <param name="autonomy">The stored autonomy value ("off", "suggest-only", "auto-fix-safe"); null reads as auto-fix-safe.</param>
    /// <param name="brainReady">Whether the brain is loaded and answering now - never a load.</param>
    public DeviceSelfHealPolicy(Func<CancellationToken, Task<string?>> autonomy, Func<bool> brainReady)
    {
        ArgumentNullException.ThrowIfNull(autonomy);
        ArgumentNullException.ThrowIfNull(brainReady);
        _autonomy = autonomy;
        _brainReady = _ => Task.FromResult(brainReady());
    }

    private async Task<AutonomyLevel> LevelAsync(CancellationToken ct)
        => AutonomyLevels.Parse(await _autonomy(ct).ConfigureAwait(false));

    /// <inheritdoc />
    public async Task<bool> ShouldAnalyseAsync(CancellationToken ct = default)
        => await LevelAsync(ct).ConfigureAwait(false) != AutonomyLevel.Off;

    /// <inheritdoc />
    public async Task<bool> MayAutoFixAsync(CancellationToken ct = default)
        => await LevelAsync(ct).ConfigureAwait(false) == AutonomyLevel.AutoFixSafe;

    /// <inheritdoc />
    public async Task<bool> BrainReadyAsync(CancellationToken ct = default)
    {
        try { return await _brainReady(ct).ConfigureAwait(false); }
        catch { return false; }   // if we cannot tell, assume not ready — the cautious side.
    }
}
