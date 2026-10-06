#nullable enable

// NetworkDiagnosis.cs
//
// Turns a raw transport exception into an ACTIONABLE verdict.
//
// Before this, a failed model download surfaced as a bare HttpRequestException.
// On a Huawei P30 Lite with a dead system resolver that read as:
//
//   System.Net.Http.HttpRequestException: Connection failure
//    ---> Java.Net.UnknownHostException: Unable to resolve host "modelscope.cn"
//
// which is indistinguishable, to the caller and to the user, from "the mirror is
// down", "you're offline", "the hotel wifi wants you to log in", or "the file
// 404'd". They have completely different remedies, and only one of them is the
// user's to fix. Shipping the raw exception makes every one of them read as
// "CircleAI is broken".
//
// NOTE ON ANDROID: the inner exception there is Java.Net.UnknownHostException, a
// Java type that does not exist in a plain net10.0 library and cannot be caught
// by type. Classification therefore matches on type NAME and message text as
// well as on SocketError. That is deliberate, not lazy — see MatchesDnsFailure.

using System;
using System.Net.Http;
using System.Net.Sockets;

namespace CircleAI.Inference;

/// <summary>What actually went wrong, as a category a caller can branch on.</summary>
public enum NetworkFault
{
    /// <summary>No fault — the probe succeeded.</summary>
    None = 0,

    /// <summary>No usable network interface at all. Aeroplane mode, no wifi, no SIM.</summary>
    NoLink,

    /// <summary>
    /// The link is up but name resolution failed. The single most common
    /// real-world failure, and the one that looks most like a broken app.
    /// </summary>
    DnsFailure,

    /// <summary>
    /// Connected to a network that is intercepting traffic pending sign-in
    /// (hotel / airport / campus wifi). Requests "succeed" with the wrong body.
    /// </summary>
    CaptivePortal,

    /// <summary>Name resolved, but the host refused or was unreachable.</summary>
    HostUnreachable,

    /// <summary>TLS handshake or certificate validation failed.</summary>
    TlsFailure,

    /// <summary>The request timed out — slow link, or a stalled transfer.</summary>
    Timeout,

    /// <summary>The server answered with an error status (404, 403, 5xx).</summary>
    HttpError,

    /// <summary>Could not be classified. Never claim more than this.</summary>
    Unknown,
}

/// <summary>
/// A classified network failure, with a remedy phrased for a person.
/// </summary>
/// <param name="Fault">The category.</param>
/// <param name="Detail">Technical detail for logs — may name hosts and errno.</param>
/// <param name="Remedy">
/// What the USER can do, in plain language. Empty when there is nothing they
/// could usefully do (a dead mirror is not their problem to fix).
/// </param>
/// <param name="IsTransient">
/// Whether retrying the same request might succeed without anything changing.
/// Drives the backoff loop — retrying a 404 forever is just a slower failure.
/// </param>
public sealed record NetworkDiagnosis(
    NetworkFault Fault,
    string       Detail,
    string       Remedy,
    bool         IsTransient)
{
    /// <summary>Everything is fine.</summary>
    public static readonly NetworkDiagnosis Healthy =
        new(NetworkFault.None, "reachable", string.Empty, false);

    /// <summary>True when the download path should not even be attempted.</summary>
    public bool ShouldBlockDownload => Fault is not NetworkFault.None;

    /// <summary>One line suitable for a transcript or a toast.</summary>
    public override string ToString() =>
        Fault == NetworkFault.None
            ? "network: ok"
            : string.IsNullOrEmpty(Remedy)
                ? $"network: {Fault} — {Detail}"
                : $"network: {Fault} — {Detail}. {Remedy}";

    /// <summary>
    /// Classifies a transport exception. Walks the whole inner chain, because
    /// the informative exception is usually two or three levels down.
    /// </summary>
    public static NetworkDiagnosis Classify(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            // DNS — check before the generic SocketException arm, because a
            // resolution failure IS a SocketException with a specific errno.
            if (MatchesDnsFailure(e))
            {
                return new NetworkDiagnosis(
                    NetworkFault.DnsFailure,
                    e.Message,
                    "Your device is connected but cannot look up addresses. " +
                    "Turning Wi-Fi off and on again usually fixes it.",
                    IsTransient: true);
            }

            if (e is SocketException se)
            {
                return se.SocketErrorCode switch
                {
                    SocketError.NetworkDown or
                    SocketError.NetworkUnreachable => new NetworkDiagnosis(
                        NetworkFault.NoLink, e.Message,
                        "There is no network connection. Connect to Wi-Fi or mobile data.",
                        IsTransient: true),

                    SocketError.TimedOut => new NetworkDiagnosis(
                        NetworkFault.Timeout, e.Message,
                        "The connection is very slow or stalled. Try again on a better signal.",
                        IsTransient: true),

                    SocketError.ConnectionRefused or
                    SocketError.HostUnreachable or
                    SocketError.HostDown => new NetworkDiagnosis(
                        NetworkFault.HostUnreachable, e.Message,
                        string.Empty,   // a dead mirror is not the user's to fix
                        IsTransient: true),

                    _ => new NetworkDiagnosis(
                        NetworkFault.Unknown, $"socket error {se.SocketErrorCode}: {e.Message}",
                        string.Empty, IsTransient: true),
                };
            }

            if (e is System.Security.Authentication.AuthenticationException)
            {
                return new NetworkDiagnosis(
                    NetworkFault.TlsFailure, e.Message,
                    "The secure connection could not be verified. If you are on public " +
                    "Wi-Fi, sign in to the network first.",
                    // Not transient: a failed handshake repeats identically.
                    IsTransient: false);
            }

            if (e is TaskCanceledException or TimeoutException)
            {
                return new NetworkDiagnosis(
                    NetworkFault.Timeout, e.Message,
                    "The download timed out. Try again on a stronger connection.",
                    IsTransient: true);
            }

            if (e is HttpRequestException hre && hre.StatusCode is { } status)
            {
                var code = (int)status;
                return new NetworkDiagnosis(
                    NetworkFault.HttpError,
                    $"HTTP {code} {status}",
                    string.Empty,
                    // 5xx and 429 may pass; 4xx will not, so do not spin on them.
                    IsTransient: code >= 500 || code == 429);
            }
        }

        return new NetworkDiagnosis(
            NetworkFault.Unknown, ex.Message, string.Empty, IsTransient: true);
    }

    /// <summary>
    /// Recognises a name-resolution failure across the runtimes we ship on.
    /// </summary>
    /// <remarks>
    /// Three separate shapes, all of which mean the same thing:
    /// <list type="number">
    ///   <item><see cref="SocketError.HostNotFound"/> / <c>TryAgain</c> /
    ///         <c>NoData</c> — desktop CoreCLR.</item>
    ///   <item><c>Java.Net.UnknownHostException</c> — Android. A Java type that
    ///         a net10.0 library cannot reference, so it is matched by type NAME.
    ///         Catching it by type would require a per-platform build of this
    ///         file for one string comparison.</item>
    ///   <item>Message text — last resort, for runtimes that surface neither.</item>
    /// </list>
    /// Matching on strings is normally a smell; here the alternative is either a
    /// platform-split assembly or silently misclassifying the single most common
    /// field failure, which is worse.
    /// </remarks>
    private static bool MatchesDnsFailure(Exception e)
    {
        if (e is SocketException s &&
            s.SocketErrorCode is SocketError.HostNotFound
                              or SocketError.TryAgain
                              or SocketError.NoData)
        {
            return true;
        }

        var typeName = e.GetType().FullName ?? string.Empty;
        if (typeName.Contains("UnknownHostException", StringComparison.Ordinal))
            return true;

        var msg = e.Message;
        return msg.Contains("Unable to resolve host", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("No address associated with hostname", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("EAI_NODATA", StringComparison.Ordinal)
            || msg.Contains("EAI_NONAME", StringComparison.Ordinal)
            || msg.Contains("Name or service not known", StringComparison.OrdinalIgnoreCase);
    }
}
