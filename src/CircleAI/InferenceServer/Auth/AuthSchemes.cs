// AuthSchemes.cs
//
// Constant scheme names so Endpoint/Controller code and the auth handler
// agree on the policy / scheme identifiers.

namespace CircleAI.Inference.Server.Auth;

/// <summary>Identifiers for the auth schemes the server registers.</summary>
public static class AuthSchemes
{
    /// <summary>API-key auth scheme name.</summary>
    public const string ApiKey = "ApiKey";

    /// <summary>JWT Bearer auth scheme name (matches Microsoft's default constant).</summary>
    public const string Jwt = "Bearer";

    /// <summary>Policy name for endpoints requiring an authenticated caller.</summary>
    public const string AuthenticatedPolicy = "Authenticated";
}
