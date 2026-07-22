#nullable enable

// NetworkPreflight.cs
//
// Checks the network BEFORE a 433 MB download, and routes around a dead system
// resolver rather than surrendering to it.
//
// WHY NOT "RESTART DNS": an Android app cannot. WifiManager.setWifiEnabled has
// been a no-op for non-system apps since Android 10 (API 29), and there is no
// public API to flush or restart the platform resolver. Toggling Wi-Fi over adb
// fixes it; the app has no such power. So the recovery is not to repair the
// resolver but to BYPASS it:
//
//   1. Ask the system resolver.                     (fast path, ~always works)
//   2. If that fails, resolve over DNS-over-HTTPS   (needs NO system DNS,
//      addressed by IP LITERAL.                      because there is no name
//                                                    to look up)
//   3. Connect to the resulting IP with the correct Host/SNI.
//
// Step 2 is the whole trick: https://1.1.1.1/dns-query is reachable with a
// broken resolver precisely because 1.1.1.1 is already an address.
//
// RESOLVER CHOICE — de-Googled by policy. Cloudflare and Quad9 only; 8.8.8.8 is
// Google and is deliberately absent. Quad9 is second because it is operated by a
// Swiss non-profit, which is a different failure domain from Cloudflare rather
// than a second helping of the same one.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Inference;

/// <summary>Checks reachability and resolves names when the system cannot.</summary>
public interface INetworkPreflight
{
    /// <summary>
    /// Can we reach <paramref name="target"/> right now? Returns a classified
    /// verdict — never throws for network reasons.
    /// </summary>
    Task<NetworkDiagnosis> CheckAsync(Uri target, CancellationToken ct = default);

    /// <summary>
    /// Resolves a hostname, falling back to DoH-over-IP when the system
    /// resolver fails. Empty when every route failed.
    /// </summary>
    Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct = default);
}

/// <summary>Default <see cref="INetworkPreflight"/>: system resolver, then DoH-over-IP.</summary>
public sealed class NetworkPreflight : INetworkPreflight, IDisposable
{
    /// <summary>
    /// DoH endpoints addressed by IP LITERAL so they resolve with no DNS.
    /// Replacing these with hostnames would defeat the entire mechanism.
    /// </summary>
    private static readonly Uri[] DohEndpoints =
    [
        new("https://1.1.1.1/dns-query"),          // Cloudflare
        new("https://9.9.9.9:5053/dns-query"),     // Quad9 (non-profit, separate failure domain)
    ];

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    /// <summary>How long a single probe or DoH query may take.</summary>
    public TimeSpan ProbeTimeout { get; init; } = TimeSpan.FromSeconds(6);

    public NetworkPreflight() : this(NewProbeClient(), ownsHttp: true) { }

    public NetworkPreflight(HttpClient http) : this(http, ownsHttp: false) { }

    private NetworkPreflight(HttpClient http, bool ownsHttp)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _ownsHttp = ownsHttp;
    }

    private static HttpClient NewProbeClient() => new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <inheritdoc />
    public async Task<NetworkDiagnosis> CheckAsync(Uri target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        // Link layer first — cheapest, and distinguishes "no network at all"
        // from "network but broken", which have different remedies.
        if (!System.Net.NetworkInformation.NetworkInterface.GetIsNetworkAvailable())
        {
            return new NetworkDiagnosis(
                NetworkFault.NoLink,
                "no network interface is up",
                "Connect to Wi-Fi or mobile data.",
                IsTransient: true);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeout);

        try
        {
            // HEAD, not GET: we want reachability, not the payload.
            using var req = new HttpRequestMessage(HttpMethod.Head, target);
            using var res = await _http.SendAsync(req, timeout.Token).ConfigureAwait(false);

            // A redirect to an unrelated host on a plain HEAD is the classic
            // captive-portal signature — the network answered for someone else.
            if (IsRedirect(res.StatusCode) &&
                res.Headers.Location is { } loc &&
                loc.IsAbsoluteUri &&
                !string.Equals(loc.Host, target.Host, StringComparison.OrdinalIgnoreCase))
            {
                return new NetworkDiagnosis(
                    NetworkFault.CaptivePortal,
                    $"redirected to {loc.Host}",
                    "This Wi-Fi needs you to sign in first. Open a browser and complete sign-in.",
                    IsTransient: false);
            }

            return NetworkDiagnosis.Healthy;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // caller cancelled — not a network fault
        }
        catch (Exception ex)
        {
            var diagnosis = NetworkDiagnosis.Classify(ex);

            // A DNS failure is not necessarily fatal — we may still be able to
            // resolve out-of-band. Only report it if the bypass ALSO fails,
            // otherwise we would block a download that would have worked.
            if (diagnosis.Fault == NetworkFault.DnsFailure)
            {
                var viaDoh = await ResolveViaDohAsync(target.Host, timeout.Token).ConfigureAwait(false);
                if (viaDoh.Count > 0)
                {
                    return new NetworkDiagnosis(
                        NetworkFault.DnsFailure,
                        $"system resolver failed for '{target.Host}'; resolved {viaDoh[0]} over DoH instead",
                        string.Empty,          // nothing for the user to do — we routed around it
                        IsTransient: true);
                }
            }

            return diagnosis;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        // Already an address — nothing to resolve.
        if (IPAddress.TryParse(host, out var literal))
            return [literal];

        try
        {
            var entries = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
            if (entries.Length > 0) return entries;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Fall through to DoH — this is the case the class exists for.
        }

        return await ResolveViaDohAsync(host, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves via DNS-over-HTTPS, addressing the resolver by IP so the query
    /// itself needs no DNS. Tries each endpoint in turn.
    /// </summary>
    private async Task<IReadOnlyList<IPAddress>> ResolveViaDohAsync(string host, CancellationToken ct)
    {
        foreach (var endpoint in DohEndpoints)
        {
            try
            {
                var url = $"{endpoint}?name={Uri.EscapeDataString(host)}&type=A";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                // RFC 8484 JSON profile — both endpoints serve it.
                req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/dns-json"));

                using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
                if (!res.IsSuccessStatusCode) continue;

                var body = await res.Content
                    .ReadFromJsonAsync<DohResponse>(ct)
                    .ConfigureAwait(false);

                var addresses = body?.Answer?
                    .Where(a => a.Type == 1 && !string.IsNullOrWhiteSpace(a.Data))   // type 1 = A
                    .Select(a => IPAddress.TryParse(a.Data, out var ip) ? ip : null)
                    .Where(ip => ip is not null)
                    .Select(ip => ip!)
                    .ToArray();

                if (addresses is { Length: > 0 }) return addresses;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Try the next resolver. Both being unreachable means the link
                // itself is dead, which CheckAsync reports separately.
            }
        }

        return [];
    }

    private static bool IsRedirect(HttpStatusCode code) =>
        code is HttpStatusCode.Moved
             or HttpStatusCode.Found
             or HttpStatusCode.SeeOther
             or HttpStatusCode.TemporaryRedirect
             or HttpStatusCode.PermanentRedirect;

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }

    // RFC 8484 JSON response shape — only the fields we use.
    private sealed class DohResponse
    {
        [JsonPropertyName("Answer")] public DohAnswer[]? Answer { get; set; }
    }

    private sealed class DohAnswer
    {
        [JsonPropertyName("type")] public int     Type { get; set; }
        [JsonPropertyName("data")] public string? Data { get; set; }
    }
}
