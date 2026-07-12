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
export { NullRealtimeTransportFactory, NullRealtimeLogger } from "./transport.js";
export type { IRealtimeTransport, IRealtimeTransportFactory, IRealtimeLogger } from "./transport.js";

// Concrete session that demuxes vendor JSON envelopes over an IRealtimeTransport.
export { RealtimeWebSocketSession } from "./websocket_session.js";

// Per-vendor options + builders.
export {
  openAiRealtimeOptions,
  geminiLiveOptions,
  novaSonicOptions,
  elevenLabsConvOptions,
  ultravoxOptions,
} from "./options.js";
export type {
  OpenAiRealtimeOptions,
  GeminiLiveOptions,
  NovaSonicOptions,
  ElevenLabsConvOptions,
  UltravoxOptions,
} from "./options.js";

// The 5 vendor connectors.
export { OpenAiRealtimeService } from "./openai_service.js";
export { GeminiLiveService } from "./gemini_service.js";
export { NovaSonicService } from "./nova_service.js";
export { ElevenLabsConvService } from "./elevenlabs_service.js";
export { UltravoxService, NullRealtimeHttpClient } from "./ultravox_service.js";
export type { IRealtimeHttpClient, RealtimeHttpResponse } from "./ultravox_service.js";
