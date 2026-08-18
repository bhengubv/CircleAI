# libopenjtalk_g2p — Japanese G2P for Android, built from the vendored
# r9y9/open_jtalk 1.11 tree in upstream/.
#
# This wraps upstream's own src/CMakeLists.txt rather than reproducing its
# source lists. Upstream builds one static `openjtalk` library out of the whole
# analysis chain (text2mecab -> mecab -> njd_* -> jpcommon) and deliberately
# excludes hts_engine, which is exactly the split we want: we need readings, not
# an HTS acoustic model, because the acoustic half is a VITS ONNX graph.
#
# Build (one ABI at a time — see the serialisation note below):
#
#   cmake -B build/arm64-v8a -S . -G Ninja ^
#     -DCMAKE_TOOLCHAIN_FILE=%NDK%/build/cmake/android.toolchain.cmake ^
#     -DANDROID_ABI=arm64-v8a -DANDROID_PLATFORM=android-24 ^
#     -DCMAKE_BUILD_TYPE=Release
#   cmake --build build/arm64-v8a

cmake_minimum_required(VERSION 3.22)
project(openjtalk_g2p C CXX)

set(OJT_SRC ${CMAKE_CURRENT_SOURCE_DIR}/upstream/src)
if(NOT EXISTS ${OJT_SRC}/CMakeLists.txt)
  message(FATAL_ERROR "upstream/ is missing. Vendored source is part of this repo — see native/open-jtalk/README.md")
endif()

# MECAB WRITES A GENERATED HEADER INTO THE SOURCE TREE.
#
# Upstream's configure_file() emits mecab/src/config.h next to the sources
# rather than into the build directory. Two ABIs configuring at the same moment
# therefore race on one file, and the loser links against the other's
# assumptions — a failure that shows up as a corrupt reading on one
# architecture only. Build ABIs SERIALLY. This is recorded here because the
# obvious speed-up (cmake --build for every ABI in parallel) is the exact thing
# that breaks it.
add_subdirectory(${OJT_SRC} ${CMAKE_CURRENT_BINARY_DIR}/openjtalk EXCLUDE_FROM_ALL)

add_library(openjtalk_g2p SHARED openjtalk_g2p.c)

target_include_directories(openjtalk_g2p PRIVATE
  ${OJT_SRC}/jpcommon
  ${OJT_SRC}/mecab/src
  ${OJT_SRC}/mecab2njd
  ${OJT_SRC}/njd
  ${OJT_SRC}/njd2jpcommon
  ${OJT_SRC}/njd_set_accent_phrase
  ${OJT_SRC}/njd_set_accent_type
  ${OJT_SRC}/njd_set_digit
  ${OJT_SRC}/njd_set_long_vowel
  ${OJT_SRC}/njd_set_pronunciation
  ${OJT_SRC}/njd_set_unvoiced_vowel
  ${OJT_SRC}/text2mecab
)

# CHARSET_UTF_8 must match how upstream was configured (it defaults to utf8).
# The headers switch struct layouts on it, so a mismatch between this wrapper
# and the library is an ABI mismatch, not a text-encoding preference.
target_compile_definitions(openjtalk_g2p PRIVATE
  CHARSET_UTF_8
  MECAB_UTF8_USE_ONLY
  HAVE_CONFIG_H
)

# Mecab is C++, so the final .so needs a C++ runtime even though the wrapper is
# C. Static libc++ keeps this self-contained: the app already ships ONNX
# Runtime and MNN with their own runtimes, and a shared libc++_shared.so would
# add a fourth thing that has to agree on a version.
target_link_libraries(openjtalk_g2p PRIVATE openjtalk)
set_target_properties(openjtalk_g2p PROPERTIES C_VISIBILITY_PRESET hidden)

if(ANDROID)
  target_link_libraries(openjtalk_g2p PRIVATE log)
  # -Wl,--exclude-libs strips the static library's symbols from the .so export
  # table, so only the openjtalk_g2p_* entry points are visible. Without it the
  # library exports several hundred mecab symbols, some of which collide with
  # other native libraries in the same process.
  target_link_options(openjtalk_g2p PRIVATE -Wl,--exclude-libs,ALL)
endif()
