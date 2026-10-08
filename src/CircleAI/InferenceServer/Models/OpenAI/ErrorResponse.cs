// ErrorResponse.cs
//
// OpenAI-shaped error envelope. SDKs that target OpenAI parse this exact
// shape; matching it lets clients surface CircleAI errors with the same
// code paths they already use for OpenAI.

using System.Text.Json.Serialization;

namespace CircleAI.Inference.Server.Models.OpenAI;

/// <summary>OpenAI-shaped error envelope: <c>{"error": {...}}</c>.</summary>
public sealed class ErrorResponse
{
    [JsonPropertyName("error")]
    public ErrorBody Error { get; set; } = new();

    public static ErrorResponse Of(string message, string type, string? code = null) =>
        new() { Error = new() { Message = message, Type = type, Code = code } };
}

/// <summary>Inner error body.</summary>
public sealed class ErrorBody
{
    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "invalid_request_error";

    [JsonPropertyName("param")]
    public string? Param { get; set; }

    [JsonPropertyName("code")]
    public string? Code { get; set; }
}
