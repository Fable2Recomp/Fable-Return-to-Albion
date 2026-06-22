/**
 ******************************************************************************
 * Xenia : Xbox 360 Emulator Research Project                                 *
 ******************************************************************************
 * Copyright 2022 Ben Vanik. All rights reserved.                             *
 * Released under the BSD license - see LICENSE in the root for more details. *
 ******************************************************************************
 *
 * @modified    Tom Clay, 2026 - Adapted for ReXGlue runtime
 */

#pragma once

#include <memory>
#include <string>

#include <rex/ui/window.h>

#include <SDL3/SDL_events.h>
#include <SDL3/SDL_video.h>

#if defined(_WIN32)
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#endif

namespace rex {
namespace ui {

class SDLWindow : public Window {
 public:
  SDLWindow(WindowedAppContext& app_context, const std::string_view title,
            uint32_t desired_logical_width, uint32_t desired_logical_height);
  ~SDLWindow() override;

  SDL_Window* window() const { return window_; }
  NativeWindowSystem GetNativeWindowSystem() const override;
  void* GetNativeDisplayHandle() const override;
  void* GetNativeWindowHandle() const override;

  void HandleSDLEvent(const SDL_Event& event);
  void OnSDLPaintEvent();

 protected:
  bool OpenImpl() override;
  void RequestCloseImpl() override;

  void ApplyNewFullscreen() override;
  void ApplyNewTitle() override;
  void LoadAndApplyIcon(const void* buffer, size_t size,
                        bool can_apply_state_in_current_phase) override;
  void ApplyNewMouseCapture() override;
  void ApplyNewMouseRelease() override;
  void ApplyNewCursorVisibility(CursorVisibility old_cursor_visibility) override;
  void FocusImpl() override;

  std::unique_ptr<Surface> CreateSurfaceImpl(Surface::TypeFlags allowed_types) override;
  void RequestPaintImpl() override;

 private:
  void CloseFromSDL();
  void HandleSizeUpdate(WindowDestructionReceiver& destruction_receiver);
  void HandleKeyboard(const SDL_KeyboardEvent& event,
                      WindowDestructionReceiver& destruction_receiver);
  void HandleTextInput(const SDL_TextInputEvent& event,
                       WindowDestructionReceiver& destruction_receiver);
  void HandleMouseMotion(const SDL_MouseMotionEvent& event,
                         WindowDestructionReceiver& destruction_receiver);
  void HandleMouseButton(const SDL_MouseButtonEvent& event,
                         WindowDestructionReceiver& destruction_receiver);
  void HandleMouseWheel(const SDL_MouseWheelEvent& event,
                        WindowDestructionReceiver& destruction_receiver);

  SDL_Window* window_ = nullptr;
  SDL_WindowID window_id_ = 0;
};

}  // namespace ui
}  // namespace rex
