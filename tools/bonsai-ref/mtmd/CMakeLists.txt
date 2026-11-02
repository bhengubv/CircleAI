# The pinned fork's own multimodal library (tools/mtmd at prism-b10743-adfffbe), built as
# mtmd.dll against the llama.dll and ggml DLLs the repo already ships for Windows.
#
# WHY. Those DLLs were built with LLAMA_BUILD_MTMD=OFF, so the ruler bonsai-ref uses for the
# text fixtures cannot see an image. This compiles exactly the fork's mtmd sources - nothing
# of ours - and links them to that build's import libraries, so the image is resized, encoded
# and decoded by the fork's code running on the fork's shipped ggml. Nothing here ships: the
# DLL is a ruler, like the build it extends.
#
#   cmake -S tools/bonsai-ref/mtmd -B <build dir> -G "Visual Studio 17 2022" -A x64
#         -DFORK_SRC=<llama.cpp source at prism-b10743-adfffbe>
#         -DFORK_LIBS=<the llama_cpp-build folder holding llama.lib, ggml.lib, ggml-base.lib>
#   cmake --build <build dir> --config Release --target mtmd
#
# FORK_LIBS must be the build the shipped DLLs came from: native/llama-bridge/build-x64's
# bin/Release DLLs are byte-identical to src/CircleAI/Inference/runtimes/win-x64/native.

cmake_minimum_required(VERSION 3.16)
project(bonsai_ref_mtmd C CXX)

set(FORK_SRC  "" CACHE PATH "llama.cpp source at the pinned fork tag")
set(FORK_LIBS "" CACHE PATH "the fork build holding llama.lib, ggml.lib and ggml-base.lib")
if (NOT EXISTS "${FORK_SRC}/tools/mtmd/mtmd.cpp")
    message(FATAL_ERROR "FORK_SRC=${FORK_SRC} is not the fork's source tree")
endif()
if (NOT EXISTS "${FORK_LIBS}/src/Release/llama.lib")
    message(FATAL_ERROR "FORK_LIBS=${FORK_LIBS} holds no src/Release/llama.lib")
endif()

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# As the fork's own top-level CMakeLists sets them for MSVC.
add_compile_definitions(_CRT_SECURE_NO_WARNINGS)
add_compile_options("$<$<COMPILE_LANGUAGE:C>:/utf-8>" "$<$<COMPILE_LANGUAGE:CXX>:/utf-8>"
                    "$<$<COMPILE_LANGUAGE:C>:/bigobj>" "$<$<COMPILE_LANGUAGE:CXX>:/bigobj>")

# vendor/hash, as vendor/hash/CMakeLists.txt builds it (sha1.c compiled as C++).
set(HASH "${FORK_SRC}/vendor/hash")
add_library(vendor_hash STATIC "${HASH}/hash.cpp" "${HASH}/xxhash/xxhash.c" "${HASH}/sha1/sha1.c" "${HASH}/sha256/sha256.c")
set_source_files_properties("${HASH}/sha1/sha1.c" PROPERTIES LANGUAGE CXX)
set_source_files_properties("${HASH}/xxhash/xxhash.c" "${HASH}/sha1/sha1.c" "${HASH}/sha256/sha256.c" PROPERTIES COMPILE_OPTIONS /w)
target_include_directories(vendor_hash PUBLIC "${FORK_SRC}/vendor" PRIVATE "${HASH}")

# tools/mtmd, the sources its CMakeLists names (every models/*.cpp), video off as it is
# without LLAMA_SUBPROCESS.
set(MTMD "${FORK_SRC}/tools/mtmd")
file(GLOB MTMD_MODELS "${MTMD}/models/*.cpp")
add_library(mtmd SHARED
    "${MTMD}/mtmd.cpp" "${MTMD}/mtmd-audio.cpp" "${MTMD}/mtmd-image.cpp"
    "${MTMD}/mtmd-helper.cpp" "${MTMD}/mtmd-helper-gen.cpp" "${MTMD}/clip.cpp"
    ${MTMD_MODELS})
target_include_directories(mtmd PRIVATE "${MTMD}" "${FORK_SRC}/include" "${FORK_SRC}/ggml/include" "${FORK_SRC}/vendor")
target_compile_definitions(mtmd PRIVATE LLAMA_BUILD LLAMA_SHARED GGML_SHARED GGML_BACKEND_SHARED)
target_link_libraries(mtmd PRIVATE vendor_hash
    "${FORK_LIBS}/src/Release/llama.lib"
    "${FORK_LIBS}/ggml/src/Release/ggml.lib"
    "${FORK_LIBS}/ggml/src/Release/ggml-base.lib")
set_target_properties(mtmd PROPERTIES WINDOWS_EXPORT_ALL_SYMBOLS ON)
