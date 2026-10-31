// refii — the hosted guest's Android start-up (lifecycle_android.h). Built on Android only.

#include "lifecycle_android.h"

// Godot owns the Activity and entry point; this library explicitly readies SDL.
#define SDL_MAIN_HANDLED 1
#include <SDL3/SDL_main.h>
#include <SDL3/SDL_hints.h>
#include <rex/main_android.h>

#if defined(__GNUC__) || defined(__clang__)
#pragma GCC visibility push(hidden)
#endif
namespace refii::hostcore {

void StartAndroidPlatform() {
    rex::InitializeAndroidPlatform();
}

void ReadyAndroidSdl() {
    SDL_SetHint("SDL_VIDEO_DRIVER", "dummy");
    SDL_SetHint("SDL_AUDIO_DRIVER", "dummy");
    SDL_SetMainReady();
}

}  // namespace refii::hostcore
#if defined(__GNUC__) || defined(__clang__)
#pragma GCC visibility pop
#endif
