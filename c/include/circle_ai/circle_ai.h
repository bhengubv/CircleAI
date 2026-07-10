#ifndef CIRCLE_AI_H
#define CIRCLE_AI_H

/*
 * circle_ai.h — umbrella header for the CircleAI portable C11 SDK.
 * Include this single header to pull in all modules.
 */

#include "models.h"
#include "memory.h"
#include "identity.h"
#include "languages.h"
#include "companion.h"
#include "inference.h"
#include "tools.h"
#include "sync.h"
#include "security.h"

/* Security peer-intelligence pipeline — local immune system + peer trust layer */
#include "watchdog.h"
#include "peer_security.h"

/* 1.5.0 portable surface */
#include "models_v15.h"
#include "device.h"
#include "selector.h"
#include "registry.h"
#include "prompt.h"
#include "agents.h"
#include "hosting.h"

/* Aether contracts + AetherNet bindings (CircleAI.Aether / .AetherNet /
 * .Security.AetherNet) — one-way mesh<->BhenguAI boundary + mesh capability
 * discovery + AetherNet-specific security bindings */
#include "aether.h"
#include "mesh_capability.h"
#include "aethernet_security.h"

/* memory-brain port */
#include "memory_brain.h"
#include "companion_brain.h"

/* LLM extractor + consolidation subsystem */
#include "llm_extractor.h"
#include "consolidation.h"

/* memory subsystems port — feedback / RAG / multimodal / compression */
#include "feedback_analyser.h"
#include "rag.h"
#include "multimodal.h"
#include "compression.h"

/* companion reasoning core — world model / predictive / inner monologue / theory of mind */
#include "companion_reason.h"

/* HER/Jarvis companion cognition — remaining contracts + real impls */
#include "herjarvis.h"

/* external capability absorption registry */
#include "capability_registry.h"

/* proactive scheduling substrate — cron + scheduler + sources/runners */
#include "proactive.h"

/* proactive briefing service + companion session factory */
#include "proactive_briefing.h"
#include "companion_session_factory.h"

/* companion-state sync layer — HLC, syncable store, channel, engine, bridges */
#include "companion_sync.h"

/* full Goal record + IGoalStore (rich, distinct from models.h fixture goal) */
#include "goal_store.h"

/* memory pipeline host runtime */
#include "companion_runtime.h"

/* CircleAI.Sync — MemorySyncService + SyncDelta seam + reconciliation */
#include "memory_sync_service.h"

/* Core model-management runtime — loader / manager / downloader / sources /
 * SafeModelHandle + facade + tenant/audit + ShardKvCodec + embeddings */
#include "model_runtime.h"
#include "circle_engine.h"
#include "tenant_audit.h"
#include "shard_kv.h"
#include "embedding_store.h"

/* Inference runtime + server (CircleAI.Inference / .Inference.Server /
 * .Inference.Server.Enterprise) */
#include "inference_rt.h"
#include "inference_server.h"
#include "inference_server_enterprise.h"

/* Hosting runtime + sub-hosts (CircleAI.Hosting / .CloudFallback /
 * .InferenceBridge / .Mcp / .Multiplayer) */
#include "host_ai.h"
#include "host_cron.h"
#include "host_tools_ui.h"
#include "host_cloud.h"
#include "host_bridge.h"
#include "host_mcp.h"
#include "host_multiplayer.h"

/* Safety guardrails + alignment + safety-domain boards (CircleAI.ContentPolicy /
 * .ModelAlignment / .Safety / .Safety.Child) — content filters, refusal policy,
 * prompt-injection detection, alignment toolkit/auditor, incident + child-safety
 * boards */
#include "content_policy.h"
#include "model_alignment.h"
#include "safety.h"
#include "safety_child.h"

/* CircleAI.Networking — unified transport abstraction (enums, records, policy,
 * INetworkTransport / IMeshNetwork / IMessageChannel / IConnectivityMonitor /
 * ITransportSelector + in-memory implementations) */
#include "networking.h"

/* CircleAI.Networking transports A — AetherNet mesh / Bluetooth GATT / DTN
 * store-and-forward / gRPC channel / HTTP; the shared Networking.SyncDelta the
 * sync channels consume */
#include "net_sync_delta.h"
#include "net_aether.h"
#include "net_bluetooth.h"
#include "net_dtn.h"
#include "net_grpc.h"
#include "net_http.h"

/* CircleAI.Networking transports B — MQTT / NearLink SLE / raw TCP / WebSocket /
 * LAN-UDP WiFi (+ WiFi peer discovery) */
#include "net_mqtt.h"
#include "net_nearlink.h"
#include "net_tcp.h"
#include "net_websocket.h"
#include "net_wifi.h"

/* CircleAI.Speech / .Speech.Cloud / .Voice — the B! Butler voice loop:
 * ASR / TTS / wake-word / VAD / AEC / noise-reduction / end-of-turn +
 * audio-format conversion (Speech), the keyword voice-intent router
 * (Speech.Cloud), and the capture -> VAD -> transcribe -> pipeline plus
 * speech-emotion + speaker-identity (Voice) */
#include "speech.h"
#include "speech_cloud.h"
#include "voice.h"

/* CircleAI.Vision / .Vision.Cloud / .Video — the vision + media-generation
 * surface: video capture + face detect/embed/liveness + document verify + plate
 * recognition + BLE anomaly detection (Vision, native CV SDKs injected), image
 * generation with a fallback chain (Vision.Cloud, HTTP generators injected), and
 * the txtMe Video Mail stack — video generator + style-script rewriter +
 * in-memory style catalogue (Video). */
#include "vision.h"
#include "vision_cloud.h"
#include "video.h"

/* CircleAI.Media / .MediaHub — the media verticals: the audio/video/image asset
 * catalogue (Media: MediaAsset + InMemoryMediaLibrary) and the media-server layer
 * (MediaHub: MediaItem/PlaybackPosition + InMemory/Null MediaLibrary +
 * InMemory/Null SyncedPlayback broadcast-subscribe pub/sub) */
#include "media.h"

/* CircleAI.Realtime / .Realtime.Cloud — carrier-agnostic streaming realtime AI:
 * audio-format/direction enums, session config + tool + audio-frame records, the
 * RealtimeEvent union, the built-in Loopback session/service (echo + silence-TTS)
 * plus Null defaults, and the injected WebSocket transport contract with its
 * NullRealtimeTransportFactory */
#include "realtime.h"

/* CircleAI.Telephony (+ .Twilio / .Telnyx / .Plivo) — the carrier-agnostic
 * telephony surface: call/media primitives, the DtmfToneGenerator, IMediaStream
 * (Manual + Pending) + ICallSession (TestCallSession + carrier MediaCallSession)
 * + IInboundCallDispatcher (InMemory + Null) + IToolCallRegistry
 * (DefaultToolCallRegistry, webhook poster injected) + ITelephonyCarrier
 * (Null + Fallback + binding-wrap), and the three real carrier bindings driven
 * over an injected HTTP transport (no real network). */
#include "telephony.h"
#include "telephony_twilio.h"
#include "telephony_telnyx.h"
#include "telephony_plivo.h"

#endif /* CIRCLE_AI_H */
