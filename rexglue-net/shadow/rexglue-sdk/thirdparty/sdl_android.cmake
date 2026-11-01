# Android's SDL options, included by thirdparty/CMakeLists.txt after its own defaults and before
# SDL is added, so these replace them.

# All embedded modules must share SDL readiness, hints, TLS and event queues.
# Bionic's local library groups do not coalesce separate static SDL copies.
set(SDL_SHARED ON CACHE BOOL "" FORCE)
set(SDL_STATIC OFF CACHE BOOL "" FORCE)

# Explicitly selectable for headless guest bring-up inside Godot, without an SDL Activity.
set(SDL_DUMMYAUDIO ON CACHE BOOL "" FORCE)
set(SDL_DUMMYVIDEO ON CACHE BOOL "" FORCE)
