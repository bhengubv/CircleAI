cmake_minimum_required(VERSION 3.20)
project(circle_ai VERSION 3.5.0 LANGUAGES C)
set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)

# Cross-language FP determinism. C# evaluates `a*b + c` as a multiply that
# rounds, then an add that rounds. Clang/GCC may CONTRACT that pattern into a
# single fused multiply-add (fma) that rounds only once, diverging by 1 ULP —
# e.g. the belief-decay `1.0 / (1.0 + idx * 0.1)` came out 0.5882352941176471
# under Apple clang vs the C# reference 0.588235294117647. Disable FP
# contraction so every port produces byte-identical doubles. (MSVC has no such
# flag and does not contract by default.)
if(NOT MSVC)
    add_compile_options(-ffp-contract=off)
endif()

add_library(circle_ai STATIC
    src/models.c
    src/memory.c
    src/identity.c
    src/languages.c
    src/companion.c
    src/inference.c
    src/tools.c
    src/sync.c
    src/security.c
    # 1.5.0 surface
    src/device.c
    src/selector.c
    src/registry.c
    src/prompt.c
    src/agents_v15.c
    src/hosting.c
    src/neuron.c
    # memory-brain port
    src/memory_brain.c
    src/companion_brain.c
    # LLM extractor + consolidation subsystem
    src/llm_extractor.c
    src/consolidation.c
    # memory subsystems port — feedback / RAG / multimodal / compression
    src/feedback_analyser.c
    src/rag.c
    src/multimodal.c
    src/compression.c
    # companion reasoning core — world model / predictive / inner monologue / theory of mind
    src/companion_reason.c
    # HER/Jarvis companion cognition + proactive substrate
    src/herjarvis.c
    src/capability_registry.c
    src/proactive.c
    src/proactive_briefing.c
    src/companion_session_factory.c
    # memory sync + runtime + sync service port
    src/companion_sync.c
    src/goal_store.c
    src/companion_runtime.c
    src/memory_sync_service.c
    # Core model-management runtime + facade + tenant/audit + ShardKvCodec + embeddings
    src/model_runtime.c
    src/circle_engine.c
    src/tenant_audit.c
    src/shard_kv.c
    src/embedding_store.c
    # Inference runtime + server (CircleAI.Inference / .Server / .Server.Enterprise)
    src/inference_rt.c
    src/inference_server.c
    src/inference_server_enterprise.c
    # Hosting runtime + sub-hosts (CircleAI.Hosting / .CloudFallback / .InferenceBridge / .Mcp / .Multiplayer)
    src/host_ai.c
    src/host_cron.c
    src/host_tools_ui.c
    src/host_cloud.c
    src/host_bridge.c
    src/host_mcp.c
    src/host_multiplayer.c
    # Security peer-intelligence pipeline — local immune system + peer trust layer
    src/watchdog.c
    src/peer_security.c
    # Aether contracts + AetherNet bindings (CircleAI.Aether / .AetherNet / .Security.AetherNet)
    src/aether.c
    src/mesh_capability.c
    src/aethernet_security.c
    # Safety guardrails + alignment + safety-domain boards (CircleAI.ContentPolicy / .ModelAlignment / .Safety / .Safety.Child)
    src/content_policy.c
    src/model_alignment.c
    src/safety.c
    src/safety_child.c
    # CircleAI.Networking — unified transport abstraction + in-memory impls
    src/networking.c
    # CircleAI.Networking transports A — AetherNet / Bluetooth / DTN / gRPC / HTTP
    src/net_sync_delta.c
    src/net_aether.c
    src/net_bluetooth.c
    src/net_dtn.c
    src/net_grpc.c
    src/net_http.c
    # CircleAI.Networking transports B — Mqtt / NearLink / Tcp / WebSocket / WiFi
    src/net_mqtt.c
    src/net_nearlink.c
    src/net_tcp.c
    src/net_websocket.c
    src/net_wifi.c
    # CircleAI.Speech / .Speech.Cloud / .Voice — ASR/TTS/wake-word/VAD/AEC/denoise
    # + intent router + voice pipeline (deterministic in-memory impls)
    src/speech.c
    src/speech_cloud.c
    src/voice.c
    # CircleAI.Vision / .Vision.Cloud / .Video — vision detection + media generation
    # (native CV + HTTP image/video generators injected behind vtable seams)
    src/vision.c
    src/vision_cloud.c
    src/video.c
    # CircleAI.Media / .MediaHub — asset catalog + media-server (item library +
    # synced-playback pub/sub) — and CircleAI.Realtime / .Realtime.Cloud — the
    # streaming realtime-AI surface (loopback session/service + injected transport)
    src/media.c
    src/realtime.c
    # CircleAI.Telephony (+ .Twilio / .Telnyx / .Plivo) — carrier-agnostic
    # telephony contract surface (primitives, DtmfToneGenerator, IMediaStream,
    # ICallSession, IInboundCallDispatcher, IToolCallRegistry, ITelephonyCarrier
    # + Null/Fallback) and the three real carrier bindings over an injected HTTP
    # transport (no real network)
    src/telephony.c
    src/telephony_carrier_util.c
    src/telephony_twilio.c
    src/telephony_telnyx.c
    src/telephony_plivo.c
    # CircleAI.Telephony voice-agent layer — pure-logic classes on top of the
    # carrier contract surface: barge-in / IVR-loop / tool circuit-breaker /
    # guardrails / reassurance / speculative-gen / false-interruption / hold-music
    # / warm-transfer / consult-escalation / agent-handoff / LLM-judge / eval /
    # sentence-chunker / latency / dashboard / preamble / stereo-recorder / AMD /
    # telemetry / streaming-tool-progress / voice-loop-as-tool / prompt-vars /
    # local-dev-tunnel / MCP-importer (async/TTS/HTTP/tunnel boundaries = ca_ seams)
    src/telephony_agent.c
    # Domain boards A — health/finance/legal/edu/commerce verticals
    # (CircleAI.Healthcare / .Banking / .Legal / .Education / .Commerce +
    # .Commerce.Accounting / .Finance / .Integration.PayFast / .Integration.Xero +
    # .Personal.Finance / .Personal.Health / .Personal.Mental). Deterministic
    # in-memory boards over linear arrays.
    src/healthcare.c
    src/banking.c
    src/legal.c
    src/education.c
    src/commerce.c
    src/commerce_accounting.c
    src/commerce_finance.c
    src/commerce_payfast.c
    src/commerce_xero.c
    src/personal_finance.c
    src/personal_health.c
    src/personal_mental.c
    # Domain boards B — people/home/logistics verticals (CircleAI.CRM / .HR /
    # .Business / .Retail / .Markets / .Logistics / .RealEstate / .Home / .IoT /
    # .Family / .Parenting / .Pets / .Elderly). Deterministic in-memory boards +
    # backends over linear arrays; Markets adds a subscribe/broadcast quote feed
    # and an order router over the injected instrument catalog.
    src/crm.c
    src/hr.c
    src/business.c
    src/retail.c
    src/markets.c
    src/logistics.c
    src/realestate.c
    src/home.c
    src/iot.c
    src/family.c
    src/parenting.c
    src/pets.c
    src/elderly.c
    # Domain boards C — lifestyle/civic/misc verticals (CircleAI.Sports / .Fitness /
    # .Food / .Agriculture / .Beauty / .Gaming / .Games / .Hospitality / .Tourism /
    # .Travel / .Civic / .Community / .Social / .Relationships / .Faith /
    # .Construction / .Energy / .Creative / .Kids / .Wearable / .Wearable.Biosignals
    # / .Accessibility / .Ambient). Deterministic in-memory boards over linear
    # arrays; Games + Biosignals add subscribe/replay cursors.
    src/sports.c
    src/fitness.c
    src/food.c
    src/agriculture.c
    src/beauty.c
    src/gaming.c
    src/games.c
    src/hospitality.c
    src/tourism.c
    src/travel.c
    src/civic.c
    src/community.c
    src/social.c
    src/relationships.c
    src/faith.c
    src/construction.c
    src/energy.c
    src/creative.c
    src/kids.c
    src/wearable.c
    src/wearable_biosignals.c
    src/accessibility.c
    src/voice_xsampa.c
    src/voice_wav.c
    src/ambient.c

    # CircleAI.Integration (+ .Calendar / .Email / .Geo / .HomeAssistant / .News)
    # — external-integration contracts + deterministic in-memory provider impls
    # (real HTTP connectors are injected dependencies, no real network)
    src/integration.c
    src/integration_calendar.c
    src/integration_email.c
    src/integration_geo.c
    src/integration_home.c
    src/integration_inmemory.c
    src/integration_news.c

    # Serving / agents / runtime work unit — CircleAI.Runtime (+ .Backends) /
    # .Operator / .Orchestration / .Pipelines / .Plugins / .Skills / .Workflows /
    # .MicroAgents / .Federation / .Distribution / .BuildFarm / .Collaboration /
    # .AutonomousBiz. Deterministic in-memory boards + injected native/store/
    # cloud seams. distribution.c carries a self-contained SHA-256 + HMAC.
    src/runtime.c
    src/operator.c
    src/orchestration.c
    src/pipelines.c
    src/plugins.c
    src/skills.c
    src/workflows.c
    src/microagents.c
    src/federation.c
    src/distribution.c
    src/buildfarm.c
    src/collaboration.c
    src/autonomous_biz.c

    # Knowledge / perception / dev-tools work unit — CircleAI.Knowledge / .Search /
    # .Research / .Domain / .Personality / .Observer / .Observability / .Spatial /
    # .Simulation / .Visualization / .Inputs / .CodeUnderstanding / .DevTools /
    # .DepBot / .SDD / .DocAnalytics. Deterministic in-memory boards + injected
    # scraper/native/store seams. (Embeddings + Embeddings.Local already ship in
    # embedding_store.c.)
    src/knowledge.c
    src/search.c
    src/research.c
    src/domain.c
    src/personality.c
    src/observer.c
    src/observability.c
    src/spatial.c
    src/simulation.c
    src/visualization.c
    src/inputs.c
    src/code_understanding.c
    src/devtools.c
    src/depbot.c
    src/sdd.c
    src/doc_analytics.c

    # Close-out work unit — the remaining portable packages (CircleAI.Agents.Peer /
    # .Tools.Catalog / .Languages.Language (packs) / .Languages.Translation /
    # .SelfBench / .Testing / .WindowsAutomation / .Web portable primitives).
    # Deterministic in-memory boards + injected seams: agent bus/protocol,
    # tool-namespace catalog, the 8 culture packs + registries, the on-device
    # LLM translation engine (over the ca_local_chat_generator_t seam), the
    # self-bench harness (over the ca_ai_service_t seam, with a self-contained
    # regex matcher), snapshot/golden testing helpers, a Windows-automation
    # contract surface (native driver injected — no real Win32), and the
    # portable CircleAI.Web primitives (routes/pages/response-cache board).
    src/agents_peer.c
    src/tools_catalog.c
    src/languages_packs.c
    src/languages_translation.c
    src/selfbench.c
    src/testing.c
    src/windows_automation.c
    src/web.c

    # CircleAI.Workflows PACA subsystem — project-management runtime ported from
    # src/CircleAI.Workflows/ (PacaAuth[HMAC-SHA256 JWT + SHA-256 API keys],
    # PacaProjects/InMemoryPacaStore, PacaBoards[kanban columns + sprints +
    # metadata + views], PacaAgents[AgentTemplates + member store], PacaDocs
    # [versioning + @mentions + links], PacaPlugins[reverse-DNS + semver
    # lifecycle], PacaMcp[tool registry + per-agent gate], PacaRealtime
    # [permission-gated rooms + broadcaster seam], PacaSkills[11 built-ins],
    # PacaDeploy[compose + .env generator]). Self-contained SHA-256/HMAC +
    # base64url + a flat-JSON scanner for the JWT payload. Injected seams for the
    # broadcaster, plugin runtime host, and MCP tool handlers.
    src/paca.c

    # CircleAI.Speech.Cloud provider recognizers + synthesizers — the 12 cloud
    # voice backends (OpenAI Whisper/TTS, Deepgram + Aura, Azure STT/TTS, Google
    # STT/TTS, AssemblyAI, Cartesia STT/Sonic, ElevenLabs, PlayHT) ported over an
    # injected ca_speech_http_t fn-ptr transport (the "real HttpClient is
    # injected" seam, like the telephony carriers). Real ported logic: WAV
    # envelope, multipart/form-data, JSON request bodies + response parsing,
    # base64 audio encode/decode, PCM sample->duration maths, fail-soft
    # not-configured behaviour. Plugs into the speech.h result + vtable surface.
    src/speech_cloud_providers.c
)
target_include_directories(circle_ai PUBLIC include)

# security.c + agents_v15.c use BCryptGenRandom on Windows
if(WIN32)
    target_link_libraries(circle_ai PUBLIC bcrypt)
endif()

enable_testing()
add_subdirectory(tests)
