include_guard(GLOBAL)

set(REFII_ANDROID_GENERATED_DIR "" CACHE PATH
    "Private PC-generated sources from the matching SDK; never packaged with the runtime")
option(REFII_ANDROID_BUILD_SELFTESTS "Build Android runtime semantic tests" OFF)

# The hook and generated callers must agree on this proxy type. A return type is
# absent from the accessor's mangled name, so a wrong header can link successfully
# and then call libc++ on a one-byte proxy as though it were a native mutex.
function(refii_android_payload_contract target)
    target_include_directories(${target} BEFORE PRIVATE
        "${CMAKE_SOURCE_DIR}/src/vfs/include"
        "${CMAKE_SOURCE_DIR}/src/lua/include"
        "${REFII_REXGLUE_SHADOW_DIR}/include")
    target_compile_definitions(${target} PRIVATE REFII_PAYLOAD_LOG_BRIDGE=1)
    target_compile_options(${target} PRIVATE
        "SHELL:-include \"${CMAKE_SOURCE_DIR}/tools/android/payload_abi_guard.h\"")
endfunction()

if(TARGET refii_hooks)
    refii_android_payload_contract(refii_hooks)
endif()

if(REFII_ANDROID_GENERATED_DIR)
    if(REFII_BUILD_GENERATED_PAYLOAD)
        message(FATAL_ERROR "Use external REFII_ANDROID_GENERATED_DIR without REFII_BUILD_GENERATED_PAYLOAD")
    endif()
    if(NOT EXISTS "${REFII_ANDROID_GENERATED_DIR}/sources.cmake")
        message(FATAL_ERROR "Private generated directory has no sources.cmake")
    endif()
    # Older PC generators omitted guest barriers under an x86-only assumption.
    # Prepare a checked private copy in the build tree even for manual CMake
    # callers. Corrected generator output is recognized without duplicate fences;
    # the player's inputs and Windows generated files remain untouched.
    find_package(Python3 COMPONENTS Interpreter REQUIRED)
    execute_process(COMMAND "${Python3_EXECUTABLE}"
        "${CMAKE_SOURCE_DIR}/tools/android/prepare-generated.py"
        --source "${REFII_ANDROID_GENERATED_DIR}"
        --output-root "${CMAKE_BINARY_DIR}/android-generated"
        --manifest "${CMAKE_BINARY_DIR}/android-generated-manifest.json"
        RESULT_VARIABLE _refii_prepare_result
        OUTPUT_VARIABLE _refii_prepared_generated
        ERROR_VARIABLE _refii_prepare_error
        OUTPUT_STRIP_TRAILING_WHITESPACE)
    if(NOT _refii_prepare_result EQUAL 0)
        message(FATAL_ERROR "Private Android source preparation failed: ${_refii_prepare_error}")
    endif()
    if(NOT EXISTS "${_refii_prepared_generated}/sources.cmake")
        message(FATAL_ERROR "Private Android source preparation returned no verified source directory")
    endif()
    include("${_refii_prepared_generated}/sources.cmake")
    target_include_directories(refii_hooks BEFORE PRIVATE "${_refii_prepared_generated}")
    add_library(refii_android_generated_objects OBJECT
        ${GENERATED_SOURCES} "${CMAKE_SOURCE_DIR}/src/payload/src/payload_export.cpp")
    set_target_properties(refii_android_generated_objects PROPERTIES
        POSITION_INDEPENDENT_CODE ON CXX_STANDARD 23 CXX_STANDARD_REQUIRED ON)
    target_include_directories(refii_android_generated_objects PRIVATE
        "${_refii_prepared_generated}" "${CMAKE_SOURCE_DIR}/src"
        "${CMAKE_SOURCE_DIR}/src/vfs/include" "${CMAKE_SOURCE_DIR}/src/lua/include")
    target_link_libraries(refii_android_generated_objects PRIVATE rex::runtime)
    refii_apply_generated_payload_compile_options(refii_android_generated_objects)
    refii_android_payload_contract(refii_android_generated_objects)
    target_compile_options(refii_android_generated_objects PRIVATE
        -O2 -gline-tables-only -fno-strict-aliasing -fno-char8_t -ffp-model=precise -ffp-contract=off)
    target_precompile_headers(refii_android_generated_objects PRIVATE
        "${_refii_prepared_generated}/refii_pch.h")
    # CMake/Clang can include a .pch in TARGET_OBJECTS. Only real ELF objects
    # belong in a module link; the PCH is compiler state.
    add_library(refii_android_game_module SHARED
        "$<FILTER:$<TARGET_OBJECTS:refii_android_generated_objects>,INCLUDE,\\.o$>"
        "$<FILTER:$<TARGET_OBJECTS:refii_hooks>,INCLUDE,\\.o$>")
    target_link_libraries(refii_android_game_module PRIVATE
        refii_core ${REFII_LUA_LIBRARIES} ${REFII_VFS_LIBRARIES})
    # Guest-to-guest calls bind inside the module instead of through ~35k PLT stubs; matches
    # AndroidPhoneCompiler.ModuleLocalCalls.
    target_link_options(refii_android_game_module PRIVATE -Wl,--no-undefined -Wl,-Bsymbolic-functions)
    set_target_properties(refii_android_game_module PROPERTIES
        LINKER_LANGUAGE CXX OUTPUT_NAME refii_generated
        LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/private-module")
    file(GENERATE OUTPUT "${CMAKE_BINARY_DIR}/android-private-object-list.txt"
        CONTENT "$<JOIN:$<FILTER:$<TARGET_OBJECTS:refii_android_generated_objects>,INCLUDE,\\.o$>,\n>\n$<JOIN:$<FILTER:$<TARGET_OBJECTS:refii_hooks>,INCLUDE,\\.o$>,\n>\n")
endif()

if(REFII_ANDROID_BUILD_SELFTESTS)
    # A standalone process deliberately has no SDLActivity/JNI lifecycle setup.
    # It catches an embedded dummy SDL_PollEvent spin without any game data.
    add_executable(refii_android_sdl_probe
        "${CMAKE_SOURCE_DIR}/tools/android/tests/sdl_embedded_probe.c")
    target_link_libraries(refii_android_sdl_probe PRIVATE SDL3::SDL3)
    target_link_options(refii_android_sdl_probe PRIVATE -Wl,--no-undefined)
    set_target_properties(refii_android_sdl_probe PROPERTIES
        RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/tests")

    add_library(refii_android_platform_selftest SHARED
        "${CMAKE_SOURCE_DIR}/tools/android/tests/platform_selftest.cpp")
    target_include_directories(refii_android_platform_selftest BEFORE PRIVATE
        "${REFII_REXGLUE_SHADOW_DIR}/include")
    target_link_libraries(refii_android_platform_selftest PRIVATE rex::runtime)
    target_link_options(refii_android_platform_selftest PRIVATE -Wl,--no-undefined)

    enable_language(ASM)
    add_library(refii_android_fiber_selftest SHARED
        "${CMAKE_SOURCE_DIR}/tools/android/tests/fiber_selftest.cpp"
        "${CMAKE_SOURCE_DIR}/tools/android/tests/register_test_arm64.S")
    target_include_directories(refii_android_fiber_selftest BEFORE PRIVATE
        "${REFII_REXGLUE_SHADOW_DIR}/include")
    target_link_libraries(refii_android_fiber_selftest PRIVATE rex::runtime)
    target_compile_options(refii_android_fiber_selftest PRIVATE
        "$<$<COMPILE_LANGUAGE:CXX>:-O2;-fno-omit-frame-pointer;-ffp-model=strict>")
    target_link_options(refii_android_fiber_selftest PRIVATE -Wl,--no-undefined)
endif()
