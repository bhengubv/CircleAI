find_library(MATH_LIB m)

function(add_circle_test name)
    add_executable(${name} ${name}.c)
    if(MATH_LIB)
        target_link_libraries(${name} circle_ai ${MATH_LIB})
    else()
        target_link_libraries(${name} circle_ai)
    endif()
    add_test(NAME ${name} COMMAND ${name})
endfunction()

add_circle_test(test_affect_state)
add_circle_test(test_biometric_matcher)
add_circle_test(test_language_registry)
add_circle_test(test_goal_progress)
add_circle_test(test_companion_types)
add_circle_test(test_identity)
add_circle_test(test_anomaly_signal)
add_circle_test(test_affect_vad)
add_circle_test(test_upgrade)

# memory-brain port
add_circle_test(test_episodic_store)
add_circle_test(test_knowledge_graph)
add_circle_test(test_fused_recall)
add_circle_test(test_kg_extractor)
add_circle_test(test_belief_integrity)
add_circle_test(test_memory_encoder)
add_circle_test(test_companion_session)

# LLM extractor + consolidation subsystem
add_circle_test(test_llm_extractor)
add_circle_test(test_consolidation)

# memory subsystems port — feedback / RAG / multimodal / compression
add_circle_test(test_feedback_analyser)
add_circle_test(test_rag)
add_circle_test(test_multimodal)
add_circle_test(test_compression)

# companion reasoning core — world model / predictive / inner monologue / theory of mind
add_circle_test(test_companion_reason)

# HER/Jarvis companion cognition + proactive substrate
add_circle_test(test_herjarvis)
add_circle_test(test_capability_registry)

# Neuron (CircleAI.Hosting.Neuron)
add_circle_test(test_neuron)
add_circle_test(test_proactive)
add_circle_test(test_proactive_briefing)
add_circle_test(test_companion_session_factory)

# memory sync + runtime + sync service port
add_circle_test(test_companion_sync)
add_circle_test(test_goal_store)
add_circle_test(test_companion_runtime)
add_circle_test(test_memory_sync_service)

# Core model-management runtime + facade + tenant/audit + ShardKvCodec + embeddings
add_circle_test(test_model_runtime)
add_circle_test(test_circle_engine)
add_circle_test(test_tenant_audit)
add_circle_test(test_shard_kv)
add_circle_test(test_embedding_store)

# Inference runtime + server (CircleAI.Inference / .Server / .Server.Enterprise)
add_circle_test(test_inference_rt)
add_circle_test(test_inference_server)
add_circle_test(test_inference_server_enterprise)

# Hosting runtime + sub-hosts (CircleAI.Hosting / .CloudFallback / .InferenceBridge / .Mcp / .Multiplayer)
add_circle_test(test_host_ai)
add_circle_test(test_host_cron)
add_circle_test(test_host_tools_ui)
add_circle_test(test_host_cloud)
add_circle_test(test_host_bridge)
add_circle_test(test_host_mcp)
add_circle_test(test_host_multiplayer)

# Security peer-intelligence pipeline — watchdog + peer trust layer
add_circle_test(test_watchdog)
add_circle_test(test_peer_security)

# Aether contracts + AetherNet bindings (CircleAI.Aether / .AetherNet / .Security.AetherNet)
add_circle_test(test_aether)
add_circle_test(test_mesh_capability)
add_circle_test(test_aethernet_security)

# Safety guardrails + alignment + safety-domain boards (CircleAI.ContentPolicy / .ModelAlignment / .Safety / .Safety.Child)
add_circle_test(test_content_policy)
add_circle_test(test_model_alignment)
add_circle_test(test_safety)
add_circle_test(test_safety_child)

# CircleAI.Networking — unified transport abstraction + in-memory impls
add_circle_test(test_networking)

# CircleAI.Networking transports A — AetherNet / Bluetooth / DTN / gRPC / HTTP
add_circle_test(test_net_aether)
add_circle_test(test_net_bluetooth)
add_circle_test(test_net_dtn)
add_circle_test(test_net_grpc)
add_circle_test(test_net_http)

# CircleAI.Networking transports B — Mqtt / NearLink / Tcp / WebSocket / WiFi
add_circle_test(test_net_mqtt)
add_circle_test(test_net_nearlink)
add_circle_test(test_net_tcp)
add_circle_test(test_net_websocket)
add_circle_test(test_net_wifi)

# CircleAI.Speech / .Speech.Cloud / .Voice — ASR/TTS/wake-word/VAD/AEC/denoise +
# intent router + voice pipeline
add_circle_test(test_speech)
add_circle_test(test_speech_cloud)
add_circle_test(test_voice)

# CircleAI.Vision / .Vision.Cloud / .Video — vision detection + media generation
add_circle_test(test_vision)
add_circle_test(test_vision_cloud)
add_circle_test(test_video)

# CircleAI.Media / .MediaHub — asset catalog + media-server (item library +
# synced-playback pub/sub)
add_circle_test(test_media)

# CircleAI.Realtime / .Realtime.Cloud — streaming realtime-AI loopback
# session/service + injected transport factory
add_circle_test(test_realtime)

# CircleAI.Telephony (+ .Twilio / .Telnyx / .Plivo) — carrier-agnostic telephony
# contracts + DtmfToneGenerator + Test/Media call sessions + inbound dispatcher +
# tool registry + Null/Fallback carriers, and the three carrier bindings driven
# over a deterministic fake HTTP transport
add_circle_test(test_telephony)

# Domain boards A — health/finance/legal/edu/commerce verticals
# (CircleAI.Healthcare / .Banking / .Legal / .Education / .Commerce +
# .Commerce.Accounting / .Finance / .Integration.PayFast / .Integration.Xero +
# .Personal.Finance / .Personal.Health / .Personal.Mental)
add_circle_test(test_healthcare)
add_circle_test(test_banking)
add_circle_test(test_legal)
add_circle_test(test_education)
add_circle_test(test_commerce)
add_circle_test(test_commerce_accounting)
add_circle_test(test_commerce_finance)
add_circle_test(test_commerce_payfast)
add_circle_test(test_commerce_xero)
add_circle_test(test_personal_finance)
add_circle_test(test_personal_health)
add_circle_test(test_personal_mental)

# Domain boards B — people/home/logistics verticals (CircleAI.CRM / .HR /
# .Business / .Retail / .Markets / .Logistics / .RealEstate / .Home / .IoT /
# .Family / .Parenting / .Pets / .Elderly)
add_circle_test(test_crm)
add_circle_test(test_hr)
add_circle_test(test_business)
add_circle_test(test_retail)
add_circle_test(test_markets)
add_circle_test(test_logistics)
add_circle_test(test_realestate)
add_circle_test(test_home)
add_circle_test(test_iot)
add_circle_test(test_family)
add_circle_test(test_parenting)
add_circle_test(test_pets)
add_circle_test(test_elderly)

# Domain boards C — lifestyle/civic/misc verticals (CircleAI.Sports / .Fitness /
# .Food / .Agriculture / .Beauty / .Gaming / .Games / .Hospitality / .Tourism /
# .Travel / .Civic / .Community / .Social / .Relationships / .Faith /
# .Construction / .Energy / .Creative / .Kids / .Wearable / .Wearable.Biosignals /
# .Accessibility / .Ambient)
add_circle_test(test_sports)
add_circle_test(test_fitness)
add_circle_test(test_food)
add_circle_test(test_agriculture)
add_circle_test(test_beauty)
add_circle_test(test_gaming)
add_circle_test(test_games)
add_circle_test(test_hospitality)
add_circle_test(test_tourism)
add_circle_test(test_travel)
add_circle_test(test_civic)
add_circle_test(test_community)
add_circle_test(test_social)
add_circle_test(test_relationships)
add_circle_test(test_faith)
add_circle_test(test_construction)
add_circle_test(test_energy)
add_circle_test(test_creative)
add_circle_test(test_kids)
add_circle_test(test_wearable)
add_circle_test(test_wearable_biosignals)
add_circle_test(test_accessibility)
add_circle_test(test_ambient)

# CircleAI.Integration (+ .Calendar / .Email / .Geo / .HomeAssistant / .News) —
# external-integration contracts + deterministic in-memory provider impls
# (CalDav/Google/MsGraph calendar, Gmail/Imap/MsGraph mail, OpenMeteo + Osrm geo,
# HomeAssistant, Bluesky/Mastodon/NewsApi/Rss news)
add_circle_test(test_integration)
add_circle_test(test_integration_calendar)
add_circle_test(test_integration_email)
add_circle_test(test_integration_geo)
add_circle_test(test_integration_home)
add_circle_test(test_integration_news)
add_circle_test(test_voice_parity)
add_circle_test(test_voice_wav)
