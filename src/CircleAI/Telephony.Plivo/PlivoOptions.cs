// PlivoOptions.cs
//
// (3.3.0) Plivo v1 API credentials + AnswerUrl base for media-stream
// XML. Empty AuthId/AuthToken → fail-soft.

using System;

namespace CircleAI.Telephony.Plivo;

/// <summary>(3.3.0) Plivo account credentials + endpoint.</summary>
public sealed class PlivoOptions
{
    /// <summary>Plivo v1 API base address. Default <c>https://api.plivo.com</c>.</summary>
    public Uri BaseAddress { get; init; } = new("https://api.plivo.com");

    /// <summary>Plivo Auth ID (starts with "MA..." or similar).</summary>
    public string? AuthId { get; init; }

    /// <summary>Plivo Auth Token.</summary>
    public string? AuthToken { get; init; }

    /// <summary>
    /// (Required for dial) HTTPS URL the host serves that, given a
    /// <c>?stream=&lt;url-encoded wss://...&gt;</c> query parameter, returns
    /// Plivo XML containing the matching <c>&lt;Stream/&gt;</c> verb.
    /// </summary>
    public Uri? AnswerUrlBase { get; init; }
}
