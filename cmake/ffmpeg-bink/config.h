// Enables the FFmpeg decoders Godot needs on top of rexglue's audio-only FFmpeg build.
//
// The SDK builds libavutil plus a libavcodec subset for XMA/WMA Pro, and its generated
// config ships CONFIG_BINK_DECODER as 0 — the Bink sources are present but compiled out.
// We want that decoder so Godot can play the game's .bik files directly, without going
// through the guest at all.
//
// XMA1/XMA2 are the same story with none of the cost. The SDK already sets
// CONFIG_WMAPRO_DECODER to 1, so libavcodec/wmaprodec.c is ALREADY COMPILED — and
// ff_xma1_decoder / ff_xma2_decoder are defined in that very file, gated out by
// CONFIG_XMA1_DECODER / CONFIG_XMA2_DECODER being 0. Turning them on therefore needs no
// extra sources and no SIMD stubs, unlike Bink (which needed bink.c, binkdsp, blockdsp
// and hpeldsp added in the root CMakeLists). src/xmadecode consumes them so Godot can
// decode the game's XMA banks without the Fable2AssetBrowser submodule, which
// reimplemented the same decoder and does not build with clang on any architecture.
//
// #include_next rather than a copy: config_linux_x86_64.h is 2,707 generated lines, and
// duplicating it into the shadow would silently drift the moment the SDK regenerates it.
// This pulls in whatever the SDK currently says and overrides exactly one flag.
//
// This directory is placed BEFORE the FFmpeg source dir on libavcodec's include path, so
// FFmpeg's own `#include "config.h"` lands here first.

#pragma once

#include_next "config.h"

#undef CONFIG_BINK_DECODER
#define CONFIG_BINK_DECODER 1

#undef CONFIG_XMA1_DECODER
#define CONFIG_XMA1_DECODER 1

#undef CONFIG_XMA2_DECODER
#define CONFIG_XMA2_DECODER 1
