#nullable enable

// ModelDownloadGate.cs
//
// Enforces AIOptions.WifiOnlyModelDownload, which until 2026-07-20 was INERT.
//
// The property existed, defaulted to true, and was documented as "only
// downloads over Wi-Fi / Ethernet ... to protect mobile data". Nothing read it.
// ModelDownloadService has no network awareness whatsoever. So the SDK
// documented a protection it did not provide, and the smallest catalogued
// bundle is 433 MB — real money on a South African prepaid data bundle.
//
// The honest difficulty
// ─────────────────────────────────────────────────────────────────────────
// IDeviceContext.NetworkType is DOCUMENTED as "wifi" / "cellular" / "none" /
// "mesh", but DefaultDeviceContext can only answer "online" or "none" — it
// cannot distinguish metered from unmetered. So on a default host the
// guarantee is genuinely unenforceable.
//
// Failing CLOSED on "online" would break every desktop host (they would never
// download at all). Failing OPEN silently recreates the original bug on the
// exact devices it was meant to protect.
//
// So: fail open, but never silently. IsEnforceable reports whether the
// guarantee actually holds, so a host can surface "we cannot tell if you are
// on mobile data" rather than the SDK pretending it checked. A mobile host
// that supplies a real NetworkType gets real enforcement.

using System;
using CircleAI.Core;

namespace CircleAI.Inference;

/// <summary>
/// Decides whether a large model download may proceed right now.
/// </summary>
public interface IModelDownloadGate
{
    /// <summary>
    /// <c>null</c> when the download may proceed; otherwise a human-readable
    /// reason it must not. The reason is surfaced to the user, so write it for
    /// a person, not a log.
    /// </summary>
    string? BlockReason(long estimatedBytes);

    /// <summary>
    /// <c>false</c> when the policy cannot actually be enforced — e.g. wifi-only
    /// is requested but the host's <see cref="IDeviceContext"/> cannot tell wifi
    /// from cellular. Hosts should warn rather than imply a guarantee.
    /// </summary>
    bool IsEnforceable { get; }
}

/// <summary>Thrown when a model download is refused by the active gate.</summary>
public sealed class ModelDownloadBlockedException : Exception
{
    public ModelDownloadBlockedException(string message) : base(message) { }
}

/// <summary>
/// Default gate: blocks large downloads on a connection the host reports as
/// metered. Allows everything when <c>wifiOnly</c> is off.
/// </summary>
public sealed class MeteredNetworkDownloadGate : IModelDownloadGate
{
    private readonly IDeviceContext? _device;
    private readonly bool _wifiOnly;

    public MeteredNetworkDownloadGate(IDeviceContext? device, bool wifiOnly = true)
    {
        _device   = device;
        _wifiOnly = wifiOnly;
    }

    /// <inheritdoc />
    public bool IsEnforceable
    {
        get
        {
            if (!_wifiOnly) return true;          // nothing to enforce
            var net = Normalise(_device?.NetworkType);
            // "online" / null mean the host cannot distinguish metered links.
            return net is "wifi" or "ethernet" or "unmetered"
                       or "cellular" or "mobile" or "metered" or "none";
        }
    }

    /// <inheritdoc />
    public string? BlockReason(long estimatedBytes)
    {
        if (!_wifiOnly) return null;

        var net = Normalise(_device?.NetworkType);

        if (net is "cellular" or "mobile" or "metered")
        {
            var mb = estimatedBytes > 0
                ? $"{estimatedBytes / 1024.0 / 1024:F0} MB"
                : "a large";
            return $"This download is {mb} and you appear to be on mobile data. " +
                   "Connect to Wi-Fi, or allow mobile downloads in settings.";
        }

        if (net is "none")
            return "No network connection is available for the model download.";

        // "wifi" / "ethernet" / "unmetered" → allowed.
        // "online" / null / "mesh" / anything else → allowed, but see
        // IsEnforceable: we could not actually verify this is unmetered.
        return null;
    }

    private static string? Normalise(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
}
