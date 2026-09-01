/**
 * realtime-cloud/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// realtime-cloud/index.ts
//
// Barrel for the CircleAI.Realtime.Cloud port — the 5 vendor connectors that
// implement the carrier-agnostic IRealtimeService from ../realtime, plus the
// shared RealtimeWebSocketSession (vendor JSON envelope demux) and the injected
// WebSocket / HTTP / logger seams. C# is the exact spec.
//
// PLATFORM SEAMS. The connectors are framework-free; the host wires the real
// transports behind interfaces:
//   • WebSocket (ClientWebSocket)  → IRealtimeTransport / IRealtimeTransportFactory
//     (transport.ts), with NullRealtimeTransportFactory as the throwing default.
//   • HTTP (HttpClient, Ultravox)  → IRealtimeHttpClient (ultravox_service.ts),
//     with NullRealtimeHttpClient as the throwing default.
//   • Logging (ILogger)            → IRealtimeLogger, with NullRealtimeLogger.

// WebSocket transport + logger seams.
export { NullRealtimeTransportFactory, NullRealtimeLogger } from "./transport";
export type { IRealtimeTransport, IRealtimeTransportFactory, IRealtimeLogger } from "./transport";

// Concrete session that demuxes vendor JSON envelopes over an IRealtimeTransport.
export { RealtimeWebSocketSession } from "./websocket_session";

// Per-vendor options + builders.
export {
  openAiRealtimeOptions,
  geminiLiveOptions,
  novaSonicOptions,
  elevenLabsConvOptions,
  ultravoxOptions,
} from "./options";
export type {
  OpenAiRealtimeOptions,
  GeminiLiveOptions,
  NovaSonicOptions,
  ElevenLabsConvOptions,
  UltravoxOptions,
} from "./options";

// The 5 vendor connectors.
export { OpenAiRealtimeService } from "./openai_service";
export { GeminiLiveService } from "./gemini_service";
export { NovaSonicService } from "./nova_service";
export { ElevenLabsConvService } from "./elevenlabs_service";
export { UltravoxService, NullRealtimeHttpClient } from "./ultravox_service";
export type { IRealtimeHttpClient, RealtimeHttpResponse } from "./ultravox_service";
