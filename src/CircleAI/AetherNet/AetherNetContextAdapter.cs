// ──────────────────────────────────────────────────────────────────────────
// AetherNetContextAdapter
//
// Implements CircleAI.Aether.IAetherContext on top of the live AetherNet
// runtime. Reports the mesh protocol version, the configured minimum, and
// the currently active state.
//
// Install level is fixed at App for this adapter — AetherNet runs as an
// in-process library; OS-managed instances are surfaced by a separate
// platform-specific adapter (MAUI / TGN OS).
// ──────────────────────────────────────────────────────────────────────────

using CircleAI.Aether;
using AetherNet.Constants;

namespace CircleAI.AetherNet;

/// <summary>
/// Reports the presence and capability of AetherNet to CircleAI consumers
/// via the <see cref="IAetherContext"/> contract.
/// </summary>
public sealed class AetherNetContextAdapter : IAetherContext
{
    /// <summary>
    /// Constructs the adapter.
    /// </summary>
    /// <param name="minimumRequired">
    /// Minimum AetherNet protocol version the consuming app requires.
    /// When null, any installed version is considered sufficient.
    /// </param>
    /// <param name="isEnabled">
    /// Whether AetherNet is currently enabled in this process. Defaults
    /// to true — the assumption when this adapter is wired in is that the
    /// host wants AetherNet active.
    /// </param>
    public AetherNetContextAdapter(Version? minimumRequired = null, bool isEnabled = true)
    {
        MinimumRequired = minimumRequired;
        IsEnabled = isEnabled;
        RuntimeVersion = new Version(ProtocolConstants.CurrentProtocolVersion, 0, 0, 0);
    }

    /// <inheritdoc/>
    public AetherInstallLevel InstallLevel => AetherInstallLevel.App;

    /// <inheritdoc/>
    public bool IsAvailable => true;

    /// <inheritdoc/>
    public Version? RuntimeVersion { get; }

    /// <inheritdoc/>
    public Version? MinimumRequired { get; }

    /// <inheritdoc/>
    public bool IsSufficient =>
        MinimumRequired is null || (RuntimeVersion is not null && RuntimeVersion >= MinimumRequired);

    /// <inheritdoc/>
    public bool RequiresAuth => InstallLevel == AetherInstallLevel.OS;

    /// <inheritdoc/>
    public bool IsEnabled { get; }
}
