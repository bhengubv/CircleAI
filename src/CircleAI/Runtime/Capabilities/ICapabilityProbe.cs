// ICapabilityProbe.cs
//
// One probe per OS — each implementation knows how to read the local
// platform's capability surface (WMI on Windows, /proc on Linux, sysctl on
// macOS, Build.* on Android, etc.) and returns a normalised HostProfile.
//
// CapabilityProbe.Default dispatches to the right implementation based on
// RuntimeInformation.IsOSPlatform() so consumers do not need to wire each
// platform.

namespace CircleAI.Runtime.Capabilities;

/// <summary>
/// Discovers the host's hardware capabilities and returns a normalised
/// <see cref="HostProfile"/>. Implementations are OS-specific.
/// </summary>
public interface ICapabilityProbe
{
    /// <summary>
    /// Runs the probe. Implementations MUST NOT throw on probe failure —
    /// instead, fields the probe could not resolve are returned as
    /// <c>Unknown</c>, <c>null</c>, or <c>0</c> with the probe taking a best
    /// effort. Cancellation is honoured via <paramref name="ct"/>.
    /// </summary>
    Task<HostProfile> ProbeAsync(CancellationToken ct = default);
}
