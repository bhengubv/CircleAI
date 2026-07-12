// realtime-cloud/options.ts
//
// (3.3.0) Per-vendor options for the 5 realtime connectors (Options.cs). Each
// interface + builder mirrors the C# `init`-only class defaults. Uri → string.

/** (3.3.0) OpenAI Realtime options. Bearer auth + WSS endpoint. Mirrors C# `OpenAiRealtimeOptions`. */
export interface OpenAiRealtimeOptions {
  readonly webSocketEndpoint: string;
  readonly apiKey: string | null;
  readonly defaultModel: string;
  /** Beta header value required by OpenAI Realtime. */
  readonly betaHeader: string;
}

/** Builds {@link OpenAiRealtimeOptions} with the C# defaults. */
export function openAiRealtimeOptions(overrides: Partial<OpenAiRealtimeOptions> = {}): OpenAiRealtimeOptions {
  return {
    webSocketEndpoint: overrides.webSocketEndpoint ?? "wss://api.openai.com/v1/realtime",
    apiKey: overrides.apiKey ?? null,
    defaultModel: overrides.defaultModel ?? "gpt-4o-realtime-preview-2024-12-17",
    betaHeader: overrides.betaHeader ?? "realtime=v1",
  };
}

/** (3.3.0) Google Gemini Live options. Mirrors C# `GeminiLiveOptions`. */
export interface GeminiLiveOptions {
  readonly webSocketEndpoint: string;
  readonly apiKey: string | null;
  readonly defaultModel: string;
}

/** Builds {@link GeminiLiveOptions} with the C# defaults. */
export function geminiLiveOptions(overrides: Partial<GeminiLiveOptions> = {}): GeminiLiveOptions {
  return {
    webSocketEndpoint:
      overrides.webSocketEndpoint ??
      "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent",
    apiKey: overrides.apiKey ?? null,
    defaultModel: overrides.defaultModel ?? "models/gemini-2.0-flash-exp",
  };
}

/** (3.3.0) AWS Nova Sonic options. Uses SigV4 auth on the WS handshake. Mirrors C# `NovaSonicOptions`. */
export interface NovaSonicOptions {
  /** AWS region (e.g. `us-east-1`). */
  readonly region: string;
  readonly accessKeyId: string | null;
  readonly secretAccessKey: string | null;
  readonly sessionToken: string | null;
  readonly defaultModel: string;
}

/** Builds {@link NovaSonicOptions} with the C# defaults. */
export function novaSonicOptions(overrides: Partial<NovaSonicOptions> = {}): NovaSonicOptions {
  return {
    region: overrides.region ?? "us-east-1",
    accessKeyId: overrides.accessKeyId ?? null,
    secretAccessKey: overrides.secretAccessKey ?? null,
    sessionToken: overrides.sessionToken ?? null,
    defaultModel: overrides.defaultModel ?? "amazon.nova-sonic-v1:0",
  };
}

/** (3.3.0) ElevenLabs Conversational AI options. Mirrors C# `ElevenLabsConvOptions`. */
export interface ElevenLabsConvOptions {
  readonly webSocketEndpoint: string;
  readonly apiKey: string | null;
  /** ElevenLabs Agent id created in their dashboard. */
  readonly agentId: string | null;
}

/** Builds {@link ElevenLabsConvOptions} with the C# defaults. */
export function elevenLabsConvOptions(overrides: Partial<ElevenLabsConvOptions> = {}): ElevenLabsConvOptions {
  return {
    webSocketEndpoint: overrides.webSocketEndpoint ?? "wss://api.elevenlabs.io/v1/convai/conversation",
    apiKey: overrides.apiKey ?? null,
    agentId: overrides.agentId ?? null,
  };
}

/** (3.3.0) Ultravox options. Mirrors C# `UltravoxOptions`. */
export interface UltravoxOptions {
  /** Ultravox HTTP API endpoint (for session creation). */
  readonly apiEndpoint: string;
  readonly apiKey: string | null;
  readonly defaultModel: string;
  readonly defaultVoice: string;
}

/** Builds {@link UltravoxOptions} with the C# defaults. */
export function ultravoxOptions(overrides: Partial<UltravoxOptions> = {}): UltravoxOptions {
  return {
    apiEndpoint: overrides.apiEndpoint ?? "https://api.ultravox.ai",
    apiKey: overrides.apiKey ?? null,
    defaultModel: overrides.defaultModel ?? "fixie-ai/ultravox-70B",
    defaultVoice: overrides.defaultVoice ?? "Mark",
  };
}
