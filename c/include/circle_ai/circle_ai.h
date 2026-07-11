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

/* Domain boards A — deterministic in-memory verticals (CircleAI.Healthcare /
 * .Banking / .Legal / .Education / .Commerce (+ .Accounting / .Finance /
 * .Integration.PayFast / .Integration.Xero) / .Personal.Finance /
 * .Personal.Health / .Personal.Mental). Each exposes an I<Domain>Board interface
 * + record types over linear-array stores with deep-copy getters. */
#include "healthcare.h"
#include "banking.h"
#include "legal.h"
#include "education.h"
#include "commerce.h"
#include "commerce_accounting.h"
#include "commerce_finance.h"
#include "commerce_payfast.h"
#include "commerce_xero.h"
#include "personal_finance.h"
#include "personal_health.h"
#include "personal_mental.h"

/* Domain boards B — deterministic in-memory verticals (CircleAI.CRM / .HR /
 * .Business / .Retail / .Markets / .Logistics / .RealEstate / .Home / .IoT /
 * .Family / .Parenting / .Pets / .Elderly). CRM ships IContactStore /
 * IDealPipeline / IActivityLog; Markets ships IMarketDataFeed (subscribe/
 * broadcast quotes) / IInstrumentCatalog / IOrderRouter (+ OrderSide/OrderType);
 * the rest expose an I<Domain>Board interface + record types over linear-array
 * stores with deep-copy getters. */
#include "crm.h"
#include "hr.h"
#include "business.h"
#include "retail.h"
#include "markets.h"
#include "logistics.h"
#include "realestate.h"
#include "home.h"
#include "iot.h"
#include "family.h"
#include "parenting.h"
#include "pets.h"
#include "elderly.h"

/* Domain boards C — lifestyle/civic/misc verticals (CircleAI.Sports / .Fitness /
 * .Food / .Agriculture / .Beauty / .Gaming / .Games / .Hospitality / .Tourism /
 * .Travel / .Civic / .Community / .Social / .Relationships / .Faith /
 * .Construction / .Energy / .Creative / .Kids / .Wearable / .Wearable.Biosignals /
 * .Accessibility / .Ambient). Each exposes an I<Domain>Board interface + record
 * types over linear-array stores with deep-copy getters. Games ships IGameLoop /
 * IInputMap / ISceneGraph (tick/input fan-out + node set); Wearable.Biosignals
 * ships IBiosignalSource (Null + Recorded replay cursor) + BiosignalSample. */
#include "sports.h"
#include "fitness.h"
#include "food.h"
#include "agriculture.h"
#include "beauty.h"
#include "gaming.h"
#include "games.h"
#include "hospitality.h"
#include "tourism.h"
#include "travel.h"
#include "civic.h"
#include "community.h"
#include "social.h"
#include "relationships.h"
#include "faith.h"
#include "construction.h"
#include "energy.h"
#include "creative.h"
#include "kids.h"
#include "wearable.h"
#include "wearable_biosignals.h"
#include "accessibility.h"
#include "ambient.h"

/* CircleAI.Integration (+ .Calendar / .Email / .Geo / .HomeAssistant / .News) —
 * the external-integration layer: the shared contracts (CalendarEvent /
 * EmailMessage / NewsItem / WeatherSample / RouteEstimate / HaEntity + the
 * ICalendarConnector / IEmailConnector / INewsSource / IWeatherProvider /
 * IRoutingProvider / IHomeAutomationConnector vtables), plus deterministic
 * in-memory implementations for each provider (CalDav/Google/MsGraph calendar,
 * Gmail/Imap/MsGraph mail, OpenMeteo weather + Osrm routing, HomeAssistant, and
 * the Bluesky/Mastodon/NewsApi/Rss news sources) — the real HTTP connectors are
 * injected dependencies (no real network). */
#include "integration.h"
#include "integration_calendar.h"
#include "integration_email.h"
#include "integration_geo.h"
#include "integration_home.h"
#include "integration_news.h"

/* Serving / agents / runtime work unit — CircleAI.Runtime (+ .Backends) /
 * .Operator / .Orchestration / .Pipelines / .Plugins / .Skills / .Workflows /
 * .MicroAgents / .Federation / .Distribution / .BuildFarm / .Collaboration /
 * .AutonomousBiz. Deterministic in-memory boards + injected native/store/cloud
 * seams (capability probe, native-runtime fetcher, pipeline/workflow runners,
 * conversation + micro-agent executors, federation signature validator,
 * skill-pack downloader). */
#include "runtime.h"
#include "operator.h"
#include "orchestration.h"
#include "pipelines.h"
#include "plugins.h"
#include "skills.h"
#include "workflows.h"
#include "microagents.h"
#include "federation.h"
#include "distribution.h"
#include "buildfarm.h"
#include "collaboration.h"
#include "autonomous_biz.h"

/* Knowledge / perception / dev-tools work unit — CircleAI.Knowledge / .Search /
 * .Research / .Domain / .Personality / .Observer / .Observability / .Spatial /
 * .Simulation / .Visualization / .Inputs / .CodeUnderstanding / .DevTools /
 * .DepBot / .SDD / .DocAnalytics. Markdown knowledge notes + YAML frontmatter,
 * cosine/tokenise search helpers, research corpora + citation graph, the Domain
 * specialist seams (food/finance/presentation/job/mempalace/hippo/swarm/LoRA),
 * user-declared Persona + conflict resolvers + prompt builder, the
 * perceive-reason-act observation loop, metric/trace/dashboard sinks, geo/radar/
 * sky/3D spatial sources, the network-health simulation engine + graph, dashboard/
 * apidoc/site builders, input adapters (scraper/stealth/video/mcp/terminal-cast),
 * code index/search/symbol-graph, dev-tool editor/suggester/agent-shell/patch-
 * planner/refactor, dependency analyzer/updater, spec store/validator/scaffold,
 * and document view tracking + insights. (Embeddings + Embeddings.Local ship in
 * embedding_store.h.) */
#include "knowledge.h"
#include "search.h"
#include "research.h"
#include "domain.h"
#include "personality.h"
#include "observer.h"
#include "observability.h"
#include "spatial.h"
#include "simulation.h"
#include "visualization.h"
#include "inputs.h"
#include "code_understanding.h"
#include "devtools.h"
#include "depbot.h"
#include "sdd.h"
#include "doc_analytics.h"

/* Close-out work unit — the remaining portable packages. CircleAI.Agents.Peer
 * (in-process agent bus + peer protocol + PeerAgent + AgentMessage/Envelope),
 * CircleAI.Tools.Catalog (ToolNamespace/ToolDescriptor catalog + In-Memory/Null
 * impls), CircleAI.Languages.Language (the 8 culture packs — Afrikaans/Amharic/
 * Arabic/Hausa/Portuguese/Sesotho/Swahili/isiZulu — + the base contracts and the
 * two registries), CircleAI.Languages.Translation (LlmTranslationEngine +
 * live-conversation translator over the ca_local_chat_generator_t seam),
 * CircleAI.SelfBench (BenchRunner / AbBenchRunner + regression gate +
 * BenchSuiteRegistry with the built-in "default" suite, over the ca_ai_service_t
 * seam and a self-contained regex matcher), CircleAI.Testing (SnapshotDiff +
 * golden store + snapshot comparer + DeterministicIds + FrozenClock),
 * CircleAI.WindowsAutomation (UI-automation contract surface — element tree /
 * actions / input — with the native driver injected behind a vtable seam), and
 * the portable primitives of CircleAI.Web (RouteDescriptor / PageMetadata /
 * CachedResponse + InMemoryWebBoard; the Blazor/DI adapter is intentionally not
 * ported). */
#include "agents_peer.h"
#include "tools_catalog.h"
#include "languages_packs.h"
#include "languages_translation.h"
#include "selfbench.h"
#include "testing.h"
#include "windows_automation.h"
#include "web.h"

#endif /* CIRCLE_AI_H */
