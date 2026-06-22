/**
 ******************************************************************************
 * Xenia : Xbox 360 Emulator Research Project                                 *
 ******************************************************************************
 * Copyright 2021 Ben Vanik. All rights reserved.                             *
 * Released under the BSD license - see LICENSE in the root for more details. *
 ******************************************************************************
 *
 * @modified    Tom Clay, 2026 - Adapted for ReXGlue runtime
 */

#include <rex/ui/windowed_app_context_sdl.h>

#include <rex/assert.h>
#include <rex/ui/window_sdl.h>

#define SDL_MAIN_HANDLED
#include <SDL3/SDL_init.h>
#include <SDL3/SDL_main.h>
#include <SDL3/SDL_timer.h>

namespace rex {
namespace ui {

SDLWindowedAppContext::~SDLWindowedAppContext() {
  if (!HasQuitFromUIThread()) {
    PlatformQuitFromUIThread();
  }
  SDL_QuitSubSystem(SDL_INIT_VIDEO | SDL_INIT_EVENTS);
}

bool SDLWindowedAppContext::Initialize() {
  SDL_SetMainReady();
  SDL_SetAppMetadata("Fable II Recompiled", nullptr, "com.refii.refii");
  if (!SDL_InitSubSystem(SDL_INIT_VIDEO | SDL_INIT_EVENTS)) {
    return false;
  }

  pending_functions_event_type_ = SDL_RegisterEvents(2);
  if (pending_functions_event_type_ == static_cast<uint32_t>(-1)) {
    return false;
  }
  paint_event_type_ = pending_functions_event_type_ + 1;
  return true;
}

int SDLWindowedAppContext::RunMainSDLLoop() {
  if (HasQuitFromUIThread()) {
    return 0;
  }

  SDL_Event event;
  while (!HasQuitFromUIThread()) {
    while (SDL_PollEvent(&event)) {
      DispatchEvent(event);
      if (HasQuitFromUIThread()) {
        break;
      }
    }
    ExecutePendingFunctionsFromUIThread();
    SDL_Delay(1);
  }

  return 0;
}

void SDLWindowedAppContext::RegisterWindow(SDLWindow* window,
                                           SDL_WindowID window_id) {
  windows_[window_id] = window;
}

void SDLWindowedAppContext::UnregisterWindow(SDL_WindowID window_id) {
  windows_.erase(window_id);
}

void SDLWindowedAppContext::RequestWindowPaint(SDL_WindowID window_id) {
  if (!paint_event_type_) {
    return;
  }

  SDL_Event event = {};
  event.type = paint_event_type_;
  event.user.windowID = window_id;
  SDL_PushEvent(&event);
}

void SDLWindowedAppContext::NotifyUILoopOfPendingFunctions() {
  if (!pending_functions_event_type_) {
    return;
  }

  SDL_Event event = {};
  event.type = pending_functions_event_type_;
  SDL_PushEvent(&event);
}

void SDLWindowedAppContext::PlatformQuitFromUIThread() {
  SDL_Event event = {};
  event.type = SDL_EVENT_QUIT;
  SDL_PushEvent(&event);
}

void SDLWindowedAppContext::DispatchEvent(const SDL_Event& event) {
  if (pending_functions_event_type_ && event.type == pending_functions_event_type_) {
    ExecutePendingFunctionsFromUIThread();
    return;
  }

  if (paint_event_type_ && event.type == paint_event_type_) {
    auto it = windows_.find(event.user.windowID);
    if (it != windows_.end() && it->second) {
      it->second->OnSDLPaintEvent();
    }
    return;
  }

  if (event.type == SDL_EVENT_QUIT) {
    QuitFromUIThread();
    return;
  }

  SDL_WindowID window_id = 0;
  switch (event.type) {
    case SDL_EVENT_WINDOW_CLOSE_REQUESTED:
    case SDL_EVENT_WINDOW_DESTROYED:
    case SDL_EVENT_WINDOW_FOCUS_GAINED:
    case SDL_EVENT_WINDOW_FOCUS_LOST:
    case SDL_EVENT_WINDOW_RESIZED:
    case SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED:
    case SDL_EVENT_WINDOW_EXPOSED:
      window_id = event.window.windowID;
      break;
    case SDL_EVENT_KEY_DOWN:
    case SDL_EVENT_KEY_UP:
      window_id = event.key.windowID;
      break;
    case SDL_EVENT_TEXT_INPUT:
      window_id = event.text.windowID;
      break;
    case SDL_EVENT_MOUSE_MOTION:
      window_id = event.motion.windowID;
      break;
    case SDL_EVENT_MOUSE_BUTTON_DOWN:
    case SDL_EVENT_MOUSE_BUTTON_UP:
      window_id = event.button.windowID;
      break;
    case SDL_EVENT_MOUSE_WHEEL:
      window_id = event.wheel.windowID;
      break;
    default:
      return;
  }

  auto it = windows_.find(window_id);
  if (it != windows_.end() && it->second) {
    it->second->HandleSDLEvent(event);
  }
}

}  // namespace ui
}  // namespace rex
