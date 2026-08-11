cmake_minimum_required(VERSION 3.20)

# Standalone-configurable so this library can be built on its own, without
# configuring the whole refii project (recompiled PPC, renderer, shaders). It is
# still an add_subdirectory of the top-level build when that is what you want.
if(NOT DEFINED PROJECT_NAME)
    project(refii_assetbrowser CXX)
endif()

# Vendored headers live in the refii tree; find them whether we are standalone or
# a subdirectory.
if(DEFINED CMAKE_SOURCE_DIR AND EXISTS ${CMAKE_SOURCE_DIR}/src/launcher/vendors/imgui)
    set(REFII_AB_VENDORS ${CMAKE_SOURCE_DIR}/src/launcher/vendors)
else()
    set(REFII_AB_VENDORS ${CMAKE_CURRENT_SOURCE_DIR}/../launcher/vendors)
endif()

# Fable2AssetBrowser, merged into refii.
#
# Upstream: https://github.com/adarec1994/Fable2AssetBrowser (same author). The two
# projects are converging into one; the browser's ImGui UI is being migrated to Godot
# exactly as the launcher was, so what matters here is the CORE — the parsers and
# converters — built as a shared library that the Godot C# side consumes by P/Invoke.
#
# That is the same shape as refii_bnk_extractor: native format code, Godot UI on top.
# The upstream CMakeLists is kept as CMakeLists.upstream.txt for reference; it builds
# the ImGui application, which we do not.

set(REFII_AB_ROOT ${CMAKE_CURRENT_SOURCE_DIR})
# The SDK is a submodule of Fable2AssetBrowser, whose sources live under source/src.
# The merge originally vendored a flattened, pruned copy with that level removed; a
# submodule tracks upstream's real layout, so the extra level is back.
set(REFII_AB_SRC ${REFII_AB_ROOT}/assetbrowser-sdk/source/src)
set(REFII_AB_SHADOW ${REFII_AB_ROOT}/shadow/assetbrowser-sdk)

# The 18 translation units the .mdl -> glTF converter actually needs, found by
# linking. Deliberately NOT "every non-UI source": compiling that pulls in ImGui,
# Quest::Bp::Register*Nodes and Level::Creation::*, none of which the converter
# touches. textures/export/TextureExport.cpp is excluded for the same reason — it is
# an ImGui menu.
set(REFII_AB_CORE_SOURCES
    ${REFII_AB_SRC}/BNKCore.cpp
    ${REFII_AB_SRC}/ISO/IsoMount.cpp
    ${REFII_AB_SRC}/MDL/mdl_converter.cpp
    ${REFII_AB_SRC}/MDL/MdlFbxExport.cpp
    ${REFII_AB_SRC}/MDL/MdlTexExport.cpp
    ${REFII_AB_SRC}/MDL/ModelParser.cpp
    ${REFII_AB_SRC}/textures/TexParser.cpp
    ${REFII_AB_SRC}/textures/LhTexCodec.cpp
    ${REFII_AB_SRC}/textures/export/DdsExport.cpp
    ${REFII_AB_SRC}/textures/export/TiffExport.cpp
    ${REFII_AB_SRC}/textures/export/PngExport.cpp
    ${REFII_AB_SRC}/textures/export/JpgExport.cpp
    ${REFII_AB_SRC}/animations/AnimBank.cpp
    ${REFII_AB_SRC}/animations/AnimDataFile.cpp
    ${REFII_AB_SRC}/animations/AnimDecoder.cpp
    ${REFII_AB_SRC}/animations/AnimPlayer.cpp
    ${REFII_AB_SRC}/animations/Locomotion.cpp
    ${REFII_AB_SRC}/Utilities/DebugLog.cpp
    ${REFII_AB_SRC}/Utilities/Files.cpp
    ${REFII_AB_SRC}/Utilities/Utils.cpp
)

set(REFII_AB_AUDIO_SOURCES
    ${REFII_AB_SRC}/Audio/XmaDecoder.cpp
    ${REFII_AB_SRC}/Audio/Xma2Codec/XmaParser.cpp
    ${REFII_AB_SRC}/Audio/Xma2Codec/XmaBitstream.cpp
    ${REFII_AB_SRC}/Audio/Xma2Codec/XmaSupport.cpp
    ${REFII_AB_SRC}/Audio/Xma2Codec/XmaTx.cpp
    ${REFII_AB_SRC}/Audio/Xma2Codec/XmaWmaCore.cpp
    ${REFII_AB_SRC}/Audio/Xma2Codec/XmaWmaProDecoder.cpp
)

add_library(refii_assetbrowser SHARED
    ${REFII_AB_CORE_SOURCES}
    ${REFII_AB_AUDIO_SOURCES}
    ${REFII_AB_SHADOW}/abi/refii_mdl_abi.cpp
    ${REFII_AB_SHADOW}/abi/refii_audio_abi.cpp
    ${REFII_AB_SHADOW}/abi/coreshim.cpp
)

target_include_directories(refii_assetbrowser
    PUBLIC
        ${REFII_AB_SHADOW}/abi
    PRIVATE
        ${REFII_AB_SRC}
)

# State.h reaches for ImGui key enums only; the migration removes that, but until
# then the headers are needed to compile. stb provides the image writers the
# texture-export path references.
target_include_directories(refii_assetbrowser PRIVATE
    ${REFII_AB_VENDORS}/imgui
    ${REFII_AB_VENDORS}/stb
)

# zlib is REQUIRED: the bank reader inflates every entry. A quiet find that silently
# fails leaves inflate/uncompress unresolved in the .so, which only surfaces when
# something links against it.
if(TARGET ZLIB::ZLIB)
    message(STATUS "refii_assetbrowser: using the ZLIB::ZLIB target already defined")
else()
    find_package(ZLIB REQUIRED)
endif()
target_link_libraries(refii_assetbrowser PUBLIC ZLIB::ZLIB)

set_target_properties(refii_assetbrowser PROPERTIES
    CXX_STANDARD 20
    CXX_STANDARD_REQUIRED ON
    WINDOWS_EXPORT_ALL_SYMBOLS ON
)

# Stage into Godot/build/bin, the same convention the host bridge uses.
#
# This library carries the .tex/.mdl readers and the XMA decoder behind
# Engine_XmaDecoder, so it is not an editor-only convenience: a sound has to be
# playable in the Godot editor, in the editor we package OUTSIDE Godot, and in the
# running game, and all three go through the same C# path.
#
# Without this it was found only because Engine_NativeLibraries walks out/build looking
# for the newest copy — a DEV-TREE ACCIDENT that does not survive an export, exactly as
# src/hostbridge/CMakeLists.txt warns. In a packaged build every audio preview failed
# with "refii_assetbrowser not found", and only in the packaged build, which is the worst
# place for a difference to first appear.
if(REFII_STAGE_GODOT_BUILD AND DEFINED REFII_GODOT_BUILD_BIN_DIR)
    add_custom_command(TARGET refii_assetbrowser POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E make_directory "${REFII_GODOT_BUILD_BIN_DIR}"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            "$<TARGET_FILE:refii_assetbrowser>" "${REFII_GODOT_BUILD_BIN_DIR}"
        COMMENT "Staging refii_assetbrowser into Godot/build/bin"
        VERBATIM
    )
endif()
