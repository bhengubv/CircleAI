// TelnyxOptions.cs
//
// (3.3.0) Telnyx v2 API credentials + Call Control application id.
// Empty key → fail-soft (carrier reports IsConfigured=false; operations
// throw a helpful message).

using System;

namespace CircleAI.Telephony.Telnyx;

/// <summary>(3.3.0) Telnyx account credentials + endpoint.</summary>
public sealed class TelnyxOptions
{
    /// <summary>Telnyx v2 API base address. Default <c>https://api.telnyx.com</c>.</summary>
    public Uri BaseAddress { get; init; } = new("https://api.telnyx.com");

    /// <summary>Telnyx v2 API key (Bearer). Found in the portal under "API Keys".</summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// (Optional) Telnyx Call Control Application id used as the Connection for
    /// outbound calls and as the webhook owner for inbound calls. Required to dial.
    /// </summary>
    public string? CallControlConnectionId { get; init; }
}
