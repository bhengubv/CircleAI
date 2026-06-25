# mnnbridge — CircleAI's C ABI wrapper around MNN-LLM
#
# Build:
#   cmake -S . -B build -DMNN_ROOT="C:/path/to/extracted/mnn_3.5.0_<os>_<arch>"
#   cmake --build build --config Release
#
# Output: build/Release/mnnbridge.dll  (Windows)
#         build/libmnnbridge.so        (Linux/Android)
#         build/libmnnbridge.dylib     (macOS/iOS)
#
# MNN_ROOT must point at the root of the extracted Alibaba MNN bundle —
# the directory that contains lib/, include/, etc. The CMake config below
# detects each platform's layout convention.

cmake_minimum_required(VERSION 3.16)
project(mnnbridge VERSION 1.0.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

# ── Required: MNN_ROOT ───────────────────────────────────────────────────

if(NOT DEFINED MNN_ROOT)
    message(FATAL_ERROR
        "MNN_ROOT is required. Point it at the extracted Alibaba MNN bundle root, e.g.\n"
        "  cmake -S . -B build -DMNN_ROOT=\"C:/tmp/mnn_3.5.0_windows_x64_cpu_opencl\"")
endif()

if(NOT EXISTS "${MNN_ROOT}")
    message(FATAL_ERROR "MNN_ROOT does not exist: ${MNN_ROOT}")
endif()

# ── Per-platform MNN library + include resolution ────────────────────────

if(WIN32)
    # Windows bundle: lib/x64/Release/Dynamic/MD/{MNN.dll,MNN.lib}
    set(MNN_LIB_DIR     "${MNN_ROOT}/lib/x64/Release/Dynamic/MD")
    set(MNN_LIB_FILE    "${MNN_LIB_DIR}/MNN.lib")
    set(MNN_DLL_FILE    "${MNN_LIB_DIR}/MNN.dll")
    set(MNN_INCLUDE_DIR "${MNN_ROOT}/include")
elseif(APPLE)
    # macOS bundle: Dynamic/MNN.framework/Versions/A/{MNN, Headers}
    # iOS   bundle: Static/MNN.framework/{MNN, Headers}  (flat — no Versions/A)
    # Static iOS / Catalyst static link must point at Static/.
    if(CMAKE_SYSTEM_NAME STREQUAL "iOS" OR BUILD_SHARED_LIBS STREQUAL "OFF")
        set(MNN_FRAMEWORK "${MNN_ROOT}/Static/MNN.framework")
    else()
        set(MNN_FRAMEWORK "${MNN_ROOT}/Dynamic/MNN.framework")
    endif()
    set(MNN_LIB_FILE "${MNN_FRAMEWORK}")
    # macOS frameworks nest under Versions/A; iOS frameworks are flat.
    if(EXISTS "${MNN_FRAMEWORK}/Versions/A/Headers")
        set(MNN_INCLUDE_DIR "${MNN_FRAMEWORK}/Versions/A/Headers")
    else()
        set(MNN_INCLUDE_DIR "${MNN_FRAMEWORK}/Headers")
    endif()
    # Inside MNN.framework, headers `#include <MNN/expr/Expr.hpp>` even
    # though no `MNN/` subdir exists under Headers/. The framework idiom is
    # to resolve `<FrameworkName/...>` against the FRAMEWORK's parent dir
    # via -F. Capture that parent so the target can add -F<parent>.
    get_filename_component(MNN_FRAMEWORK_PARENT "${MNN_FRAMEWORK}" DIRECTORY)
elseif(ANDROID)
    # Android bundle: jni/{abi}/libMNN.so
    set(MNN_LIB_FILE    "${MNN_ROOT}/jni/${ANDROID_ABI}/libMNN.so")
    set(MNN_INCLUDE_DIR "${MNN_ROOT}/include")
else()
    # Linux bundle: lib/x64/libMNN.so + include/
    set(MNN_LIB_FILE    "${MNN_ROOT}/lib/x64/libMNN.so")
    set(MNN_INCLUDE_DIR "${MNN_ROOT}/include")
endif()

if(NOT EXISTS "${MNN_LIB_FILE}" AND NOT WIN32 AND NOT APPLE)
    # Some Linux bundles drop the x64 subdir, others ship Release/Debug
    # split subdirs (lib/Release/libMNN.so + lib/Debug/libMNN.so). We
    # always prefer the Release variant — a Debug libMNN linked into
    # consumers' production binaries would silently regress perf by 5-20×.
    file(GLOB MNN_SO_CANDIDATES
         "${MNN_ROOT}/lib/Release/libMNN.so"
         "${MNN_ROOT}/lib/x64/libMNN.so"
         "${MNN_ROOT}/lib/libMNN.so"
         "${MNN_ROOT}/lib/*/libMNN.so")
    foreach(candidate ${MNN_SO_CANDIDATES})
        if(EXISTS "${candidate}" AND NOT "${candidate}" MATCHES "/Debug/")
            set(MNN_LIB_FILE "${candidate}")
            break()
        endif()
    endforeach()
endif()

if(NOT EXISTS "${MNN_INCLUDE_DIR}/MNN/Tensor.hpp" AND
   NOT EXISTS "${MNN_INCLUDE_DIR}/Tensor.hpp")
    # Try common header layouts the bundle may use.
    foreach(candidate
        "${MNN_ROOT}/include"
        "${MNN_ROOT}/include/MNN"
        "${MNN_ROOT}/Dynamic/MNN.framework/Versions/A/Headers")
        if(EXISTS "${candidate}/llm/llm.hpp" OR EXISTS "${candidate}/MNN/llm/llm.hpp")
            set(MNN_INCLUDE_DIR "${candidate}")
            break()
        endif()
    endforeach()
endif()

message(STATUS "mnnbridge: MNN_ROOT       = ${MNN_ROOT}")
message(STATUS "mnnbridge: MNN_LIB_FILE   = ${MNN_LIB_FILE}")
message(STATUS "mnnbridge: MNN_INCLUDE_DIR= ${MNN_INCLUDE_DIR}")

if(NOT EXISTS "${MNN_LIB_FILE}")
    message(FATAL_ERROR "MNN library not found at: ${MNN_LIB_FILE}")
endif()

# ── Library target ───────────────────────────────────────────────────────
# Shared by default. iOS builds with BUILD_SHARED_LIBS=OFF get a static
# .a — iOS prohibits dlopen of bundled dynamic libraries from the app
# sandbox, so the bridge MUST be link-time integrated by the consuming
# MAUI app via NativeReference.

if(CMAKE_SYSTEM_NAME STREQUAL "iOS" OR BUILD_SHARED_LIBS STREQUAL "OFF")
    add_library(mnnbridge STATIC
        src/mnnbridge.cpp
        src/turboquant.cpp
    )
else()
    add_library(mnnbridge SHARED
        src/mnnbridge.cpp
        src/turboquant.cpp
    )
endif()

target_include_directories(mnnbridge
    PUBLIC  include
    PRIVATE
        "${MNN_INCLUDE_DIR}"
        # Vendored MNN LLM header — Alibaba ships this only in the macOS
        # framework bundle. The Llm class symbols live in MNN.dll/.so/.dylib
        # regardless; we just need the header to compile.
        "${CMAKE_CURRENT_SOURCE_DIR}/third_party/mnn-llm/include"
)

# On macOS, MNN's own headers use `<MNN/expr/Expr.hpp>` — i.e. the
# framework-name prefix. That convention only resolves when the FRAMEWORK's
# parent directory is on the framework search path. Adding -F lets the
# compiler find `MNN.framework/Headers/...` via `<MNN/...>`.
if(APPLE AND DEFINED MNN_FRAMEWORK_PARENT)
    target_compile_options(mnnbridge PRIVATE "-F${MNN_FRAMEWORK_PARENT}")
endif()

target_compile_definitions(mnnbridge PRIVATE MNNBRIDGE_EXPORTS)

if(WIN32)
    target_link_libraries(mnnbridge PRIVATE "${MNN_LIB_FILE}")
    # Match .NET's CRT linkage.
    set_property(TARGET mnnbridge PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreadedDLL")
    # Strip 'lib' prefix on Windows (CMake adds it on MinGW).
    set_target_properties(mnnbridge PROPERTIES PREFIX "")
elseif(APPLE)
    if(CMAKE_SYSTEM_NAME STREQUAL "iOS" OR BUILD_SHARED_LIBS STREQUAL "OFF")
        # iOS static .a: the consuming MAUI app's link step pulls in MNN +
        # its system frameworks (Metal, Foundation, CoreGraphics, …) via
        # NativeReference. Our static shim therefore deliberately does NOT
        # link MNN here — it only needs the headers for compile, not the
        # symbols. Otherwise ld fails on Metal/ObjC symbols our shim
        # doesn't reference but MNN's headers indirectly drag in.
        # No target_link_libraries — static .a is just compiled .o output.
    else()
        target_link_libraries(mnnbridge PRIVATE
            "-framework MNN"
            "-F${MNN_ROOT}/Dynamic")
    endif()
elseif(ANDROID)
    target_link_libraries(mnnbridge PRIVATE
        "${MNN_LIB_FILE}"
        log
        android)
else()
    target_link_libraries(mnnbridge PRIVATE
        "${MNN_LIB_FILE}"
        pthread
        dl)
    set_target_properties(mnnbridge PROPERTIES
        BUILD_RPATH "$ORIGIN"
        INSTALL_RPATH "$ORIGIN")
endif()

# ── Output naming + symbol visibility ────────────────────────────────────

set_target_properties(mnnbridge PROPERTIES
    OUTPUT_NAME "mnnbridge"
    CXX_VISIBILITY_PRESET hidden
    VISIBILITY_INLINES_HIDDEN ON)

# ── Smoke test executable ────────────────────────────────────────────────
#
# Optional — built when MNNBRIDGE_BUILD_TEST is set. Loads a config.json
# and prints version/context/vocab so we can confirm the .dll loaded and
# linked without invoking actual inference.

option(MNNBRIDGE_BUILD_TEST "Build the mnnbridge_smoke executable" OFF)

if(MNNBRIDGE_BUILD_TEST)
    add_executable(mnnbridge_smoke test/smoke.cpp)
    target_link_libraries(mnnbridge_smoke PRIVATE mnnbridge)
    target_include_directories(mnnbridge_smoke PRIVATE include)
endif()
