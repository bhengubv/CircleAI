cmake_minimum_required(VERSION 3.20)
project(circle_ai C)
set(CMAKE_C_STANDARD 99)
set(CMAKE_C_STANDARD_REQUIRED ON)

add_library(circle_ai STATIC
    src/memory.c
    src/identity.c
    src/languages.c
    src/companion.c
)
target_include_directories(circle_ai PUBLIC include)

enable_testing()
add_subdirectory(tests)
