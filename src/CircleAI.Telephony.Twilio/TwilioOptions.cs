// TwilioOptions.cs
//
// (3.3.0) Twilio REST API credentials + base address. AccountSid +
// AuthToken come from the Twilio console. Empty key → fail-soft (carrier
// reports IsConfigured=false; operations throw with a helpful message).

using System;

namespace CircleAI.Telephony.Twilio;

/// <summary>(3.3.0) Twilio account credentials + endpoint.</summary>
public sealed class TwilioOptions
{
    /// <summary>Twilio REST API base address. Default <c>https://api.twilio.com</c>.</summary>
    public Uri BaseAddress { get; init; } = new("https://api.twilio.com");

    /// <summary>Twilio Account SID (starts with "AC...").</summary>
    public string? AccountSid { get; init; }

    /// <summary>Twilio Auth Token.</summary>
    public string? AuthToken { get; init; }
}
