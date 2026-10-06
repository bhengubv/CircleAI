// CapabilityProbe.cs
//
// Default cross-platform probe. Inspects the current OS at construction
// time and delegates ProbeAsync to the right OS-specific implementation.
// Consumers don't need to wire each platform — DI with
// `services.AddSingleton<ICapabilityProbe, CapabilityProbe>()` is sufficient.

using CircleAI.Runtime.Capabilities.Internal;

namespace CircleAI.Runtime.Capabilities;

/// <summary>
/// Default <see cref="ICapabilityProbe"/> — dispatches to a platform-specific
/// probe (Windows / Linux / macOS / Android) based on the running OS.
/// Returns a synthetic <see cref="HostProfile"/> with <see cref="OperatingSystemKind.Unknown"/>
/// when no probe is available, so consumers never see a thrown exception.
/// </summary>
public sealed class CapabilityProbe : ICapabilityProbe
{
    private readonly ICapabilityProbe _inner;

    /// <summary>
    /// Construct the default probe — picks the right platform implementation
    /// at instantiation time. Cheap; safe to call from DI containers and
    /// to retain as a singleton.
    /// </summary>
    public CapabilityProbe()
    {
        _inner = ArchHelpers.ResolveOsKind() switch
        {
            OperatingSystemKind.Windows => new WindowsCapabilityProbe(),
            OperatingSystemKind.Linux   => new LinuxCapabilityProbe(),
            OperatingSystemKind.MacOS   => new MacOSCapabilityProbe(),
            OperatingSystemKind.Android => new AndroidCapabilityProbe(),
            // iOS + HarmonyOS reach this branch — there is no in-process
            // probe path on those platforms. Hosts register a port-specific
            // probe (e.g. via CircleAI.Maui) which replaces this default.
            _ => new UnknownCapabilityProbe(),
        };
    }

    /// <summary>
    /// Construct with an explicit inner probe. Useful in tests and when
    /// custom port packages (HarmonyOS, iOS via MAUI) need to substitute
    /// their own probe implementation.
    /// </summary>
    public CapabilityProbe(ICapabilityProbe inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    /// <inheritdoc/>
    public Task<HostProfile> ProbeAsync(CancellationToken ct = default) =>
        _inner.ProbeAsync(ct);
}

/// <summary>
/// Returned on platforms where no in-process probe is registered. All fields
/// fall back to <c>Unknown</c> / <c>0</c> / <c>null</c>. Hosts should register
/// a real probe via <see cref="CapabilityProbe(ICapabilityProbe)"/> or
/// <see cref="Microsoft.Extensions.DependencyInjection.IServiceCollection"/>.
/// </summary>
internal sealed class UnknownCapabilityProbe : ICapabilityProbe
{
    public Task<HostProfile> ProbeAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new HostProfile(
            OperatingSystemKind.Unknown,
            Environment.OSVersion.Version.ToString(),
            Internal.ArchHelpers.FromRuntime(System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture),
            "Unknown CPU",
            Environment.ProcessorCount, Environment.ProcessorCount, 0,
            null, null,
            DateTimeOffset.UtcNow));
    }
}
