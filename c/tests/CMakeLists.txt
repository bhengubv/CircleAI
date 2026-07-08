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
