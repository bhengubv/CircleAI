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
# WHY PRISM'S FORK AND NOT UPSTREAM. It is a strict SUPERSET: everything upstream
# opens, it opens, plus the ternary packs Bonsai ships in. Upstream REFUSES those
# outright -- prism's model card says it rejects PQ2_0 and PTQ1_0 as unknown types --
# so building upstream would mean shipping a GGUF engine that cannot open the one
# GGUF chat model in the catalogue. The cost is tracking somebody else's fork; the
# cost of not doing it is a catalogue entry that can never be compatible.
#
# The struct is NOT on their master, which carries only upstream's TQ1_0/TQ2_0 --
# alike in name, different in bytes (256-element superblock at 1.6875 bpw, versus
# PTQ1_0's 128-element 28-byte block with its own fp16 scale and a Hadamard rotation
# folded into the weights). The kernels live on the `prism` branch, and this pins its
# tagged release rather than the branch head, for the same reason the upstream pin
# exists.
#
# Licence: MIT on both, which clears the repo's licence-first rule.

cmake_minimum_required(VERSION 3.16)
project(llamabridge LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

# The pins. Two lines to review when upgrading.
set(LLAMA_REPO "https://github.com/PrismML-Eng/llama.cpp.git"
    CACHE STRING "llama.cpp repository — prism's fork, a superset of upstream")
set(LLAMA_TAG  "prism-b10743-adfffbe"
    CACHE STRING "llama.cpp git tag to build against")

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
    GIT_REPOSITORY ${LLAMA_REPO}
    GIT_TAG        ${LLAMA_TAG}
    GIT_SHALLOW    TRUE
  )
  FetchContent_MakeAvailable(llama_cpp)
endif()

add_library(llamabridge SHARED src/llamabridge.cpp)

target_include_directories(llamabridge
  PUBLIC  ${CMAKE_CURRENT_SOURCE_DIR}/include
)

target_compile_definitions(llamabridge PRIVATE
  LLAMABRIDGE_BUILD
  # The engine reports its own identity; the managed side reads it to decide
  # which quantisations it may claim. See LlamaQuantSupport.Loaded.
  LLAMABRIDGE_LLAMA_TAG="${LLAMA_TAG}")
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
