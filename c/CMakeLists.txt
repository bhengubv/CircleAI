cmake_minimum_required(VERSION 3.20)
project(circle_ai VERSION 1.5.0 LANGUAGES C)
set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)

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
)
target_include_directories(circle_ai PUBLIC include)

# security.c + agents_v15.c use BCryptGenRandom on Windows
if(WIN32)
    target_link_libraries(circle_ai PUBLIC bcrypt)
endif()

enable_testing()
add_subdirectory(tests)
