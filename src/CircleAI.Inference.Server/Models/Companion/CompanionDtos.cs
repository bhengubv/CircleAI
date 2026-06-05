// CompanionDtos.cs
//
// DTOs for /v1/companion/turn and /v1/companion/proactive. Not OpenAI-
// compatible — this is the CircleAI-native Companion contract (richer
// state shape: identity, persona, affect, language).

using System.Text.Json.Serialization;

namespace CircleAI.Inference.Server.Models.Companion;

/// <summary>POST /v1/companion/turn request body.</summary>
public sealed class CompanionTurnRequest
{
    [JsonPropertyName("session_id")]
    public string SessionId { get; set; } = "";

    [JsonPropertyName("identity_id")]
    public string IdentityId { get; set; } = "";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("stream")]
    public bool Stream { get; set; }

    [JsonPropertyName("agentic")]
    public bool Agentic { get; set; }
}

/// <summary>POST /v1/companion/turn response body.</summary>
public sealed class CompanionTurnResponse
{
    [JsonPropertyName("session_id")]
    public string SessionId { get; set; } = "";

    [JsonPropertyName("reply")]
    public string Reply { get; set; } = "";

    [JsonPropertyName("agentic")]
    public bool Agentic { get; set; }

    [JsonPropertyName("turn_index")]
    public int TurnIndex { get; set; }
}
