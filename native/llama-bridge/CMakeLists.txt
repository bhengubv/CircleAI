# CircleAI llama.cpp bridge
#
# Builds libllamabridge (.dll / .so) — the C shim in include/llamabridge.h.
#
# WHY A PINNED TAG AND NOT master. llama.cpp's C API moves: the vocab split out
# of the model, llama_new_context_with_model became llama_init_from_model,
# llama_kv_self_clear became llama_memory_clear. Tracking master means the
# break surfaces on a device build. Bump LLAMA_TAG deliberately and fix
# src/llamabridge.cpp in the same commit.
#
# Licence: llama.cpp is MIT, which clears the repo's licence-first rule.

cmake_minimum_required(VERSION 3.16)
project(llamabridge LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

# The pin. One line to review when upgrading.
set(LLAMA_TAG "b4644" CACHE STRING "llama.cpp git tag to build against")

option(LLAMABRIDGE_USE_SYSTEM_LLAMA "Link an already-built llama.cpp instead of fetching" OFF)

if(LLAMABRIDGE_USE_SYSTEM_LLAMA)
  # For a fork (e.g. a ternary-capable build): point LLAMA_DIR at it and skip
  # the fetch entirely. Nothing else in this file changes.
  find_package(llama REQUIRED)
else()
  include(FetchContent)
  set(LLAMA_BUILD_TESTS    OFF CACHE BOOL "" FORCE)
  set(LLAMA_BUILD_EXAMPLES OFF CACHE BOOL "" FORCE)
  set(LLAMA_BUILD_SERVER   OFF CACHE BOOL "" FORCE)
  set(LLAMA_CURL           OFF CACHE BOOL "" FORCE)
  FetchContent_Declare(
    llama_cpp
    GIT_REPOSITORY https://github.com/ggml-org/llama.cpp.git
    GIT_TAG        ${LLAMA_TAG}
    GIT_SHALLOW    TRUE
  )
  FetchContent_MakeAvailable(llama_cpp)
endif()

add_library(llamabridge SHARED src/llamabridge.cpp)

target_include_directories(llamabridge
  PUBLIC  ${CMAKE_CURRENT_SOURCE_DIR}/include
)

target_compile_definitions(llamabridge PRIVATE LLAMABRIDGE_BUILD)
target_link_libraries(llamabridge PRIVATE llama)

# Hide everything except the LLAMABRIDGE_API surface, so the .so does not
# re-export llama.cpp's symbols and collide with anything else loaded.
if(NOT MSVC)
  set_target_properties(llamabridge PROPERTIES
    C_VISIBILITY_PRESET   hidden
    CXX_VISIBILITY_PRESET hidden
    VISIBILITY_INLINES_HIDDEN ON)
endif()

# Android: the managed side loads "libllamabridge.so" by that exact name, and
# the .NET Android packaging expects it under runtimes/<rid>/native/.
if(ANDROID)
  set_target_properties(llamabridge PROPERTIES OUTPUT_NAME "llamabridge")
endif()

install(TARGETS llamabridge
  RUNTIME DESTINATION bin
  LIBRARY DESTINATION lib
  ARCHIVE DESTINATION lib)
