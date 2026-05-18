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

#pragma once

#include <cstdint>
#include <unordered_map>

#include <rex/ui/windowed_app_context.h>

#include <SDL3/SDL_events.h>
#include <SDL3/SDL_video.h>

namespace rex {
namespace ui {

class SDLWindow;

class SDLWindowedAppContext final : public WindowedAppContext {
 public:
  SDLWindowedAppContext() = default;
  ~SDLWindowedAppContext() override;

  bool Initialize();
  int RunMainSDLLoop();

  void RegisterWindow(SDLWindow* window, SDL_WindowID window_id);
  void UnregisterWindow(SDL_WindowID window_id);
  void RequestWindowPaint(SDL_WindowID window_id);

  void NotifyUILoopOfPendingFunctions() override;
  void PlatformQuitFromUIThread() override;

 private:
  void DispatchEvent(const SDL_Event& event);

  uint32_t pending_functions_event_type_ = 0;
  uint32_t paint_event_type_ = 0;
  std::unordered_map<SDL_WindowID, SDLWindow*> windows_;
};

}  // namespace ui
}  // namespace rex
