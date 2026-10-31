# Android's settings for every target, included by the top-level CMakeLists.txt before any target
# is declared (so the compile and link options reach them all).

include_guard(GLOBAL)

if(NOT CMAKE_ANDROID_ARCH_ABI STREQUAL "arm64-v8a")
    message(FATAL_ERROR "Refii's Android runtime currently supports arm64-v8a only")
endif()
# NDK r28 libc++ lacks the std::chrono::clock_time_conversion primary template that
# cross-platform headers specialize; force-include the same shim the payload SDK uses.
add_compile_options("$<$<COMPILE_LANGUAGE:CXX>:-include>"
    "$<$<COMPILE_LANGUAGE:CXX>:${CMAKE_SOURCE_DIR}/src/payload/src/libcxx_chrono_shim.h>")
add_link_options("LINKER:-z,max-page-size=16384" "LINKER:-z,common-page-size=16384")
