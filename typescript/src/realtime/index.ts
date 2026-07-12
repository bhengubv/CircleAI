// realtime/index.ts
//
// Barrel for the CircleAI.Realtime port — carrier-agnostic streaming realtime
// AI contracts + the in-process loopback service + fail-closed Null defaults.
// Concrete cloud vendors (OpenAI Realtime, Gemini Live, Nova Sonic, ElevenLabs
// Conversational, Ultravox) live in the ../realtime-cloud module behind an
// injected WebSocket transport seam. C# is the exact spec.

// Contracts + records + the discriminated RealtimeEvent union + constructors.
export {
  RealtimeAudioFormat,
  RealtimeDirection,
  RealtimeEventKind,
  realtimeTool,
  realtimeSessionConfig,
  realtimeAudioFrame,
  speechStartedEvent,
  speechEndedEvent,
  transcriptDeltaEvent,
  transcriptFinalEvent,
  toolCallEvent,
  turnCompleteEvent,
  sessionErrorEvent,
  sampleRateOf,
} from "./contracts.js";
export type {
  RealtimeTool,
  RealtimeSessionConfig,
  RealtimeAudioFrame,
  RealtimeEvent,
  SpeechStartedEvent,
  SpeechEndedEvent,
  TranscriptDeltaEvent,
  TranscriptFinalEvent,
  ToolCallEvent,
  TurnCompleteEvent,
  SessionErrorEvent,
  IRealtimeSession,
  IRealtimeService,
} from "./contracts.js";

// NOTE: the unbounded async queue (Channel.CreateUnbounded analogue) that backs
// the sessions is an internal implementation detail — the C# `CircleAI.Realtime`
// surface does not expose its `System.Threading.Channels` usage, so `AsyncQueue`
// is intentionally NOT re-exported here (and it would collide with the companion
// module's AsyncQueue at the package root under `export *`). Import it directly
// from "./async_queue.js" if a host truly needs it.

// In-process loopback service + session + the text-to-audio seam.
export { LoopbackRealtimeService, LoopbackRealtimeSession, silenceTextToAudio } from "./loopback.js";
export type { LoopbackTextToAudio } from "./loopback.js";

// Fail-closed Null defaults.
export { NullRealtimeService, NullRealtimeSession } from "./null_impls.js";
