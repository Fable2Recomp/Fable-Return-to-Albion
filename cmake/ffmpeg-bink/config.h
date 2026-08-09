// Enables FFmpeg's Bink VIDEO decoder on top of rexglue's audio-only FFmpeg build.
//
// The SDK builds libavutil plus a libavcodec subset for XMA/WMA Pro, and its generated
// config ships CONFIG_BINK_DECODER as 0 — the Bink sources are present but compiled out.
// We want that decoder so Godot can play the game's .bik files directly, without going
// through the guest at all.
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
