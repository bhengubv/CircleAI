// InferenceServerOptions.cs
//
// Strongly-typed configuration tree binding for the "CircleAIServer" section
// of appsettings.json. Bound at startup via
// IServiceCollection.Configure<InferenceServerOptions>(...).

namespace CircleAI.Inference.Server.Options;

/// <summary>
/// Root configuration for the inference server.
/// </summary>
public sealed class InferenceServerOptions
{
    /// <summary>Top-level config section name.</summary>
    public const string SectionName = "CircleAIServer";

    /// <summary>
    /// Absolute root directory the runtime fetcher writes MNN bundles to.
    /// Expanded for <c>%LOCALAPPDATA%</c> / <c>$HOME</c> at startup.
    /// </summary>
    public string RuntimeCacheRoot { get; set; } = "%LOCALAPPDATA%/CircleAI/runtime";

    /// <summary>
    /// Absolute root directory the model downloader writes model files to.
    /// Expanded for <c>%LOCALAPPDATA%</c> / <c>$HOME</c> at startup.
    /// </summary>
    public string ModelStorageRoot { get; set; } = "%LOCALAPPDATA%/CircleAI/models";

    /// <summary>
    /// Server-wide ceiling on concurrent inference requests. Requests past
    /// this cap return HTTP 503 with a retry hint. Must be ≥ 1.
    /// </summary>
    public int MaxConcurrentRequests { get; set; } = 16;

    /// <summary>
    /// Per-request timeout. The cancellation token passed to the bridge fires
    /// after this many seconds, returning HTTP 504 to the caller.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 120;

    /// <summary>Authentication options.</summary>
    public AuthOptions Auth { get; set; } = new();
}

/// <summary>Auth subtree.</summary>
public sealed class AuthOptions
{
    /// <summary>API-key auth options.</summary>
    public ApiKeyOptions ApiKey { get; set; } = new();

    /// <summary>JWT-bearer auth options.</summary>
    public JwtOptions Jwt { get; set; } = new();
}

/// <summary>API-key auth configuration.</summary>
public sealed class ApiKeyOptions
{
    /// <summary>When <c>true</c>, requests without a valid API key are 401-d.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>HTTP header carrying the API key.</summary>
    public string HeaderName { get; set; } = "X-CircleAI-Api-Key";

    /// <summary>
    /// Allow-listed keys. Stored as opaque strings — production deployments
    /// SHOULD inject these via secrets store, not commit them.
    /// </summary>
    public IList<string> Keys { get; set; } = new List<string>();
}

/// <summary>JWT-bearer auth configuration.</summary>
public sealed class JwtOptions
{
    /// <summary>When <c>true</c>, JWT bearer auth is enabled.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Expected <c>iss</c> claim.</summary>
    public string Issuer { get; set; } = "";

    /// <summary>Expected <c>aud</c> claim.</summary>
    public string Audience { get; set; } = "";

    /// <summary>HS256 signing key (base64 or raw UTF-8). Empty disables validation.</summary>
    public string SigningKey { get; set; } = "";
}
