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
)
target_include_directories(circle_ai PUBLIC include)

# security.c + agents_v15.c use BCryptGenRandom on Windows
if(WIN32)
    target_link_libraries(circle_ai PUBLIC bcrypt)
endif()

enable_testing()
add_subdirectory(tests)
