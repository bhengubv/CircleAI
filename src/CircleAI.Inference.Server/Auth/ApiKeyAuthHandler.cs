// ApiKeyAuthHandler.cs
//
// ASP.NET Core AuthenticationHandler that reads the configured header,
// looks the value up in the option-supplied allow-list, and returns an
// AuthenticationTicket when matched. Constant-time comparison guards
// against timing-attack key discovery.

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using CircleAI.Inference.Server.Options;

namespace CircleAI.Inference.Server.Auth;

/// <summary>
/// API-key authentication handler. Reads the header named
/// <see cref="ApiKeyOptions.HeaderName"/> and matches against
/// <see cref="ApiKeyOptions.Keys"/>. When the option block has
/// <c>Enabled=false</c> the handler succeeds with a synthetic "anonymous"
/// principal so dev environments don't need keys.
/// </summary>
public sealed class ApiKeyAuthHandler : AuthenticationHandler<ApiKeyAuthSchemeOptions>
{
    private readonly IOptionsMonitor<InferenceServerOptions> _serverOptions;

    public ApiKeyAuthHandler(
        IOptionsMonitor<ApiKeyAuthSchemeOptions> options,
        ILoggerFactory loggerFactory,
        UrlEncoder encoder,
        IOptionsMonitor<InferenceServerOptions> serverOptions)
        : base(options, loggerFactory, encoder)
    {
        _serverOptions = serverOptions;
    }

    /// <inheritdoc/>
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var cfg = _serverOptions.CurrentValue.Auth.ApiKey;

        if (!cfg.Enabled)
        {
            // Auth disabled — succeed with a marker identity.
            var anon = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.Name, "anonymous"),
                new Claim("scheme", AuthSchemes.ApiKey),
                new Claim("auth_disabled", "true"),
            }, AuthSchemes.ApiKey);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(anon), AuthSchemes.ApiKey)));
        }

        if (!Request.Headers.TryGetValue(cfg.HeaderName, out var raw) ||
            string.IsNullOrWhiteSpace(raw))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!TryMatchKey(raw.ToString(), cfg.Keys))
        {
            Logger.LogWarning("API key rejected for {Path}", Request.Path);
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
        }

        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.Name, "api-key-caller"),
            new Claim("scheme", AuthSchemes.ApiKey),
        }, AuthSchemes.ApiKey);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), AuthSchemes.ApiKey)));
    }

    /// <summary>Constant-time match against any configured key.</summary>
    private static bool TryMatchKey(string presented, IList<string> allowed)
    {
        if (allowed is null || allowed.Count == 0) return false;
        var presentedBytes = Encoding.UTF8.GetBytes(presented);
        foreach (var k in allowed)
        {
            if (string.IsNullOrEmpty(k)) continue;
            var bytes = Encoding.UTF8.GetBytes(k);
            if (bytes.Length != presentedBytes.Length) continue;
            if (CryptographicOperations.FixedTimeEquals(bytes, presentedBytes)) return true;
        }
        return false;
    }
}

/// <summary>Scheme options for the API-key handler.</summary>
public sealed class ApiKeyAuthSchemeOptions : AuthenticationSchemeOptions { }
