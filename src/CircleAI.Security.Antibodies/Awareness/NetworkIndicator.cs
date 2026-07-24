// NetworkIndicator.cs
//
// A URL / IP / domain the USER is about to trust. The subject is always something
// the user is about to connect to — a pre-connect warning — never a target to probe.

namespace CircleAI.Security.Antibodies.Awareness;

/// <summary>
/// A network location the user is about to open or connect to, so it can be checked
/// against the local corpus before they trust it.
/// </summary>
/// <param name="Kind">One of <see cref="IndicatorKind.Url"/>, <see cref="IndicatorKind.IpAddress"/>, or <see cref="IndicatorKind.DomainName"/>.</param>
/// <param name="Value">The raw value; the assessor normalizes it before lookup.</param>
public sealed record NetworkIndicator(IndicatorKind Kind, string Value)
{
    /// <summary>A URL the user is about to open.</summary>
    public static NetworkIndicator ForUrl(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        return new NetworkIndicator(IndicatorKind.Url, url);
    }

    /// <summary>An IP address the user is about to connect to.</summary>
    public static NetworkIndicator ForIp(string ip)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ip);
        return new NetworkIndicator(IndicatorKind.IpAddress, ip);
    }

    /// <summary>A domain / host name the user is about to trust.</summary>
    public static NetworkIndicator ForDomain(string domain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        return new NetworkIndicator(IndicatorKind.DomainName, domain);
    }
}
