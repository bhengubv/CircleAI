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
