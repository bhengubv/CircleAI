// Options.cs
//
// (3.3.0) Per-vendor options for the 5 realtime connectors.

using System;

namespace CircleAI.Realtime.Cloud;

/// <summary>(3.3.0) OpenAI Realtime options. Bearer auth + WSS endpoint.</summary>
public sealed class OpenAiRealtimeOptions
{
    public Uri    WebSocketEndpoint { get; init; } = new("wss://api.openai.com/v1/realtime");
    public string? ApiKey            { get; init; }
    public string DefaultModel      { get; init; } = "gpt-4o-realtime-preview-2024-12-17";
    /// <summary>Beta header value required by OpenAI Realtime.</summary>
    public string BetaHeader        { get; init; } = "realtime=v1";
}

/// <summary>(3.3.0) Google Gemini Live options.</summary>
public sealed class GeminiLiveOptions
{
    public Uri    WebSocketEndpoint { get; init; } = new("wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent");
    public string? ApiKey            { get; init; }
    public string DefaultModel      { get; init; } = "models/gemini-2.0-flash-exp";
}

/// <summary>(3.3.0) AWS Nova Sonic options. Uses SigV4 auth on the WS handshake.</summary>
public sealed class NovaSonicOptions
{
    /// <summary>AWS region (e.g. <c>us-east-1</c>).</summary>
    public string Region            { get; init; } = "us-east-1";
    public string? AccessKeyId      { get; init; }
    public string? SecretAccessKey  { get; init; }
    public string? SessionToken     { get; init; }
    public string DefaultModel      { get; init; } = "amazon.nova-sonic-v1:0";
}

/// <summary>(3.3.0) ElevenLabs Conversational AI options.</summary>
public sealed class ElevenLabsConvOptions
{
    public Uri    WebSocketEndpoint { get; init; } = new("wss://api.elevenlabs.io/v1/convai/conversation");
    public string? ApiKey            { get; init; }
    /// <summary>ElevenLabs Agent id created in their dashboard.</summary>
    public string? AgentId           { get; init; }
}

/// <summary>(3.3.0) Ultravox options.</summary>
public sealed class UltravoxOptions
{
    /// <summary>Ultravox HTTP API endpoint (for session creation).</summary>
    public Uri    ApiEndpoint       { get; init; } = new("https://api.ultravox.ai");
    public string? ApiKey            { get; init; }
    public string DefaultModel      { get; init; } = "fixie-ai/ultravox-70B";
    public string DefaultVoice      { get; init; } = "Mark";
}
