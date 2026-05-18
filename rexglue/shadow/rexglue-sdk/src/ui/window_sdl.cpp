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

#include <rex/ui/window_sdl.h>

#include <algorithm>
#include <cstdint>
#include <string>

#include <rex/assert.h>
#include <rex/cvar.h>
#include <rex/graphics/flags.h>
#include <rex/graphics/video_mode_util.h>
#include <rex/logging.h>
#include <rex/ui/flags.h>
#if defined(__linux__)
#include <rex/ui/surface_gnulinux.h>
#elif defined(_WIN32)
#include <rex/ui/surface_win.h>
#endif
#include <rex/ui/virtual_key.h>
#include <rex/ui/windowed_app_context_sdl.h>

#include <SDL3/SDL_error.h>
#include <SDL3/SDL_keyboard.h>
#include <SDL3/SDL_keycode.h>
#include <SDL3/SDL_mouse.h>
#include <SDL3/SDL_properties.h>

#if defined(__linux__)
#include <X11/Xlib-xcb.h>
#endif

namespace {

using rex::ui::VirtualKey;

VirtualKey TranslateSDLKeycodeToVirtualKey(SDL_Keycode keycode) {
  if (keycode >= SDLK_A && keycode <= SDLK_Z) {
    return VirtualKey(uint16_t(VirtualKey::kA) + uint16_t(keycode - SDLK_A));
  }
  if (keycode >= SDLK_0 && keycode <= SDLK_9) {
    return VirtualKey(uint16_t(VirtualKey::k0) + uint16_t(keycode - SDLK_0));
  }
  if (keycode >= SDLK_F1 && keycode <= SDLK_F12) {
    return VirtualKey(uint16_t(VirtualKey::kF1) + uint16_t(keycode - SDLK_F1));
  }
  if (keycode >= SDLK_F13 && keycode <= SDLK_F24) {
    return VirtualKey(uint16_t(VirtualKey::kF13) + uint16_t(keycode - SDLK_F13));
  }

  switch (keycode) {
    case SDLK_ESCAPE:
      return VirtualKey::kEscape;
    case SDLK_RETURN:
    case SDLK_RETURN2:
      return VirtualKey::kReturn;
    case SDLK_SPACE:
      return VirtualKey::kSpace;
    case SDLK_TAB:
    case SDLK_LEFT_TAB:
      return VirtualKey::kTab;
    case SDLK_BACKSPACE:
      return VirtualKey::kBack;
    case SDLK_DELETE:
      return VirtualKey::kDelete;
    case SDLK_INSERT:
      return VirtualKey::kInsert;
    case SDLK_HOME:
      return VirtualKey::kHome;
    case SDLK_END:
      return VirtualKey::kEnd;
    case SDLK_PAGEUP:
      return VirtualKey::kPrior;
    case SDLK_PAGEDOWN:
      return VirtualKey::kNext;
    case SDLK_LEFT:
      return VirtualKey::kLeft;
    case SDLK_RIGHT:
      return VirtualKey::kRight;
    case SDLK_UP:
      return VirtualKey::kUp;
    case SDLK_DOWN:
      return VirtualKey::kDown;
    case SDLK_LSHIFT:
    case SDLK_RSHIFT:
      return VirtualKey::kShift;
    case SDLK_LCTRL:
    case SDLK_RCTRL:
      return VirtualKey::kControl;
    case SDLK_LALT:
    case SDLK_RALT:
      return VirtualKey::kMenu;
    case SDLK_LGUI:
      return VirtualKey::kLWin;
    case SDLK_RGUI:
      return VirtualKey::kRWin;
    case SDLK_APPLICATION:
      return VirtualKey::kApps;
    case SDLK_CAPSLOCK:
      return VirtualKey::kCapital;
    case SDLK_NUMLOCKCLEAR:
      return VirtualKey::kNumLock;
    case SDLK_SCROLLLOCK:
      return VirtualKey::kScroll;
    case SDLK_PRINTSCREEN:
      return VirtualKey::kSnapshot;
    case SDLK_PAUSE:
      return VirtualKey::kPause;
    case SDLK_KP_0:
      return VirtualKey::kNumpad0;
    case SDLK_KP_1:
      return VirtualKey::kNumpad1;
    case SDLK_KP_2:
      return VirtualKey::kNumpad2;
    case SDLK_KP_3:
      return VirtualKey::kNumpad3;
    case SDLK_KP_4:
      return VirtualKey::kNumpad4;
    case SDLK_KP_5:
      return VirtualKey::kNumpad5;
    case SDLK_KP_6:
      return VirtualKey::kNumpad6;
    case SDLK_KP_7:
      return VirtualKey::kNumpad7;
    case SDLK_KP_8:
      return VirtualKey::kNumpad8;
    case SDLK_KP_9:
      return VirtualKey::kNumpad9;
    case SDLK_KP_PLUS:
      return VirtualKey::kAdd;
    case SDLK_KP_MINUS:
      return VirtualKey::kSubtract;
    case SDLK_KP_MULTIPLY:
      return VirtualKey::kMultiply;
    case SDLK_KP_DIVIDE:
      return VirtualKey::kDivide;
    case SDLK_KP_DECIMAL:
      return VirtualKey::kDecimal;
    case SDLK_GRAVE:
      return VirtualKey::kOem3;
    case SDLK_MINUS:
      return VirtualKey::kOemMinus;
    case SDLK_EQUALS:
      return VirtualKey::kOemPlus;
    case SDLK_COMMA:
      return VirtualKey::kOemComma;
    case SDLK_PERIOD:
      return VirtualKey::kOemPeriod;
    case SDLK_SLASH:
      return VirtualKey::kOem2;
    case SDLK_SEMICOLON:
      return VirtualKey::kOem1;
    case SDLK_LEFTBRACKET:
      return VirtualKey::kOem4;
    case SDLK_BACKSLASH:
      return VirtualKey::kOem5;
    case SDLK_RIGHTBRACKET:
      return VirtualKey::kOem6;
    case SDLK_APOSTROPHE:
      return VirtualKey::kOem7;
    default:
      return VirtualKey::kNone;
  }
}

rex::ui::MouseEvent::Button TranslateSDLMouseButton(uint8_t button) {
  switch (button) {
    case SDL_BUTTON_LEFT:
      return rex::ui::MouseEvent::Button::kLeft;
    case SDL_BUTTON_RIGHT:
      return rex::ui::MouseEvent::Button::kRight;
    case SDL_BUTTON_MIDDLE:
      return rex::ui::MouseEvent::Button::kMiddle;
    case SDL_BUTTON_X1:
      return rex::ui::MouseEvent::Button::kX1;
    case SDL_BUTTON_X2:
      return rex::ui::MouseEvent::Button::kX2;
    default:
      return rex::ui::MouseEvent::Button::kNone;
  }
}

}  // namespace

namespace rex {
namespace ui {

SDLWindow::SDLWindow(WindowedAppContext& app_context, const std::string_view title,
                     uint32_t desired_logical_width, uint32_t desired_logical_height)
    : Window(app_context, title, desired_logical_width, desired_logical_height) {}

SDLWindow::~SDLWindow() {
  EnterDestructor();
  if (window_) {
    static_cast<SDLWindowedAppContext&>(app_context()).UnregisterWindow(window_id_);
    SDL_DestroyWindow(window_);
    window_ = nullptr;
    window_id_ = 0;
  }
}

void* SDLWindow::GetNativeDisplayHandle() const {
  if (!window_) {
    return nullptr;
  }
#if defined(__linux__)
  SDL_PropertiesID properties = SDL_GetWindowProperties(window_);
  void* wayland_display = SDL_GetPointerProperty(
      properties, SDL_PROP_WINDOW_WAYLAND_DISPLAY_POINTER, nullptr);
  if (wayland_display) {
    return wayland_display;
  }
  return SDL_GetPointerProperty(properties, SDL_PROP_WINDOW_X11_DISPLAY_POINTER,
                                nullptr);
#else
  return nullptr;
#endif
}

Window::NativeWindowSystem SDLWindow::GetNativeWindowSystem() const {
  if (!window_) {
    return NativeWindowSystem::kUnknown;
  }
#if defined(_WIN32)
  return GetNativeWindowHandle() ? NativeWindowSystem::kWin32
                                 : NativeWindowSystem::kUnknown;
#elif defined(__linux__)
  SDL_PropertiesID properties = SDL_GetWindowProperties(window_);
  if (SDL_GetPointerProperty(properties, SDL_PROP_WINDOW_WAYLAND_DISPLAY_POINTER,
                             nullptr) &&
      SDL_GetPointerProperty(properties, SDL_PROP_WINDOW_WAYLAND_SURFACE_POINTER,
                             nullptr)) {
    return NativeWindowSystem::kWayland;
  }
  if (SDL_GetPointerProperty(properties, SDL_PROP_WINDOW_X11_DISPLAY_POINTER,
                             nullptr) &&
      SDL_GetNumberProperty(properties, SDL_PROP_WINDOW_X11_WINDOW_NUMBER, 0) !=
          0) {
    return NativeWindowSystem::kX11;
  }
  return NativeWindowSystem::kUnknown;
#else
  return NativeWindowSystem::kUnknown;
#endif
}

void* SDLWindow::GetNativeWindowHandle() const {
  if (!window_) {
    return nullptr;
  }
#if defined(_WIN32)
  SDL_PropertiesID properties = SDL_GetWindowProperties(window_);
  return SDL_GetPointerProperty(properties, SDL_PROP_WINDOW_WIN32_HWND_POINTER,
                                nullptr);
#elif defined(__linux__)
  SDL_PropertiesID properties = SDL_GetWindowProperties(window_);
  void* wayland_surface = SDL_GetPointerProperty(
      properties, SDL_PROP_WINDOW_WAYLAND_SURFACE_POINTER, nullptr);
  if (wayland_surface) {
    return wayland_surface;
  }
  const Sint64 window =
      SDL_GetNumberProperty(properties, SDL_PROP_WINDOW_X11_WINDOW_NUMBER, 0);
  return reinterpret_cast<void*>(static_cast<uintptr_t>(window));
#else
  return window_;
#endif
}

bool SDLWindow::OpenImpl() {
  SDL_WindowFlags flags =
      SDL_WINDOW_VULKAN | SDL_WINDOW_RESIZABLE | SDL_WINDOW_HIGH_PIXEL_DENSITY;
  if (IsFullscreen()) {
    flags |= SDL_WINDOW_FULLSCREEN;
  }

  window_ = SDL_CreateWindow(GetTitle().c_str(), int(GetDesiredLogicalWidth()),
                             int(GetDesiredLogicalHeight()), flags);
  if (!window_) {
    REXLOG_ERROR("SDLWindow: SDL_CreateWindow failed: {}", SDL_GetError());
    return false;
  }

  window_id_ = SDL_GetWindowID(window_);
  static_cast<SDLWindowedAppContext&>(app_context()).RegisterWindow(this, window_id_);
  SDL_StartTextInput(window_);

  WindowDestructionReceiver destruction_receiver(this);
  HandleSizeUpdate(destruction_receiver);
  if (destruction_receiver.IsWindowDestroyedOrClosed()) {
    return true;
  }

  OnFocusUpdate(true, destruction_receiver);
  return true;
}

void SDLWindow::RequestCloseImpl() {
  CloseFromSDL();
}

void SDLWindow::ApplyNewFullscreen() {
  SDL_SetWindowFullscreen(window_, IsFullscreen());
}

void SDLWindow::ApplyNewTitle() {
  SDL_SetWindowTitle(window_, GetTitle().c_str());
}

void SDLWindow::ApplyNewMouseCapture() {
  SDL_SetWindowMouseGrab(window_, true);
}

void SDLWindow::ApplyNewMouseRelease() {
  SDL_SetWindowMouseGrab(window_, false);
}

void SDLWindow::ApplyNewCursorVisibility(CursorVisibility old_cursor_visibility) {
  (void)old_cursor_visibility;
  if (GetCursorVisibility() == CursorVisibility::kVisible) {
    SDL_ShowCursor();
  } else {
    SDL_HideCursor();
  }
}

void SDLWindow::FocusImpl() {
  SDL_RaiseWindow(window_);
}

std::unique_ptr<Surface> SDLWindow::CreateSurfaceImpl(Surface::TypeFlags allowed_types) {
#if defined(_WIN32)
  if (allowed_types & Surface::kTypeFlag_Win32Hwnd) {
    SDL_PropertiesID properties = SDL_GetWindowProperties(window_);
    HWND hwnd = static_cast<HWND>(SDL_GetPointerProperty(
        properties, SDL_PROP_WINDOW_WIN32_HWND_POINTER, nullptr));
    HINSTANCE hinstance = static_cast<HINSTANCE>(SDL_GetPointerProperty(
        properties, SDL_PROP_WINDOW_WIN32_INSTANCE_POINTER, nullptr));
    if (hwnd && hinstance) {
      return std::make_unique<Win32HwndSurface>(hinstance, hwnd);
    }
  }
#elif defined(__linux__)
  if (allowed_types & Surface::kTypeFlag_WaylandWindow) {
    SDL_PropertiesID properties = SDL_GetWindowProperties(window_);
    auto* display = static_cast<wl_display*>(SDL_GetPointerProperty(
        properties, SDL_PROP_WINDOW_WAYLAND_DISPLAY_POINTER, nullptr));
    auto* surface = static_cast<wl_surface*>(SDL_GetPointerProperty(
        properties, SDL_PROP_WINDOW_WAYLAND_SURFACE_POINTER, nullptr));
    if (display && surface) {
      return std::make_unique<WaylandWindowSurface>(
          display, surface, [this](uint32_t& width_out, uint32_t& height_out) {
            width_out = GetActualPhysicalWidth();
            height_out = GetActualPhysicalHeight();
            return width_out != 0 && height_out != 0;
          });
    }
  }
  if (allowed_types & Surface::kTypeFlag_XcbWindow) {
    SDL_PropertiesID properties = SDL_GetWindowProperties(window_);
    Display* display = static_cast<Display*>(SDL_GetPointerProperty(
        properties, SDL_PROP_WINDOW_X11_DISPLAY_POINTER, nullptr));
    const Sint64 window = SDL_GetNumberProperty(
        properties, SDL_PROP_WINDOW_X11_WINDOW_NUMBER, 0);
    if (display && window != 0) {
      return std::make_unique<XcbWindowSurface>(
          XGetXCBConnection(display), static_cast<xcb_window_t>(window));
    }
  }
#else
  (void)allowed_types;
#endif
  return nullptr;
}

void SDLWindow::RequestPaintImpl() {
  if (window_id_) {
    static_cast<SDLWindowedAppContext&>(app_context()).RequestWindowPaint(window_id_);
  }
}

void SDLWindow::CloseFromSDL() {
  if (!window_) {
    return;
  }

  WindowDestructionReceiver destruction_receiver(this);
  OnBeforeClose(destruction_receiver);
  if (destruction_receiver.IsWindowDestroyed()) {
    return;
  }

  SDL_Window* window = window_;
  window_ = nullptr;
  static_cast<SDLWindowedAppContext&>(app_context()).UnregisterWindow(window_id_);
  window_id_ = 0;
  SDL_DestroyWindow(window);
  OnAfterClose();
}

void SDLWindow::HandleSizeUpdate(WindowDestructionReceiver& destruction_receiver) {
  int width = 0;
  int height = 0;
  if (!SDL_GetWindowSizeInPixels(window_, &width, &height)) {
    return;
  }
  OnActualSizeUpdate(uint32_t(std::max(width, 0)), uint32_t(std::max(height, 0)),
                     destruction_receiver);
}

void SDLWindow::HandleKeyboard(const SDL_KeyboardEvent& event,
                               WindowDestructionReceiver& destruction_receiver) {
  const SDL_Keymod modifiers = event.mod;
  KeyEvent e(this, TranslateSDLKeycodeToVirtualKey(event.key), event.repeat ? 2 : 1,
             !event.down, (modifiers & SDL_KMOD_SHIFT) != 0,
             (modifiers & SDL_KMOD_CTRL) != 0, (modifiers & SDL_KMOD_ALT) != 0,
             (modifiers & SDL_KMOD_GUI) != 0);
  if (event.down) {
    OnKeyDown(e, destruction_receiver);
  } else {
    OnKeyUp(e, destruction_receiver);
  }
}

void SDLWindow::HandleTextInput(const SDL_TextInputEvent& event,
                                WindowDestructionReceiver& destruction_receiver) {
  if (!event.text) {
    return;
  }

  const SDL_Keymod modifiers = SDL_GetModState();
  for (const char* p = event.text; *p != '\0'; ++p) {
    const unsigned char c = static_cast<unsigned char>(*p);
    if (c < 0x20 || c >= 0x80) {
      continue;
    }
    KeyEvent e(this, VirtualKey(c), 1, false, (modifiers & SDL_KMOD_SHIFT) != 0,
               (modifiers & SDL_KMOD_CTRL) != 0, (modifiers & SDL_KMOD_ALT) != 0,
               (modifiers & SDL_KMOD_GUI) != 0);
    OnKeyChar(e, destruction_receiver);
    if (destruction_receiver.IsWindowDestroyedOrClosed()) {
      return;
    }
  }
}

void SDLWindow::HandleMouseMotion(const SDL_MouseMotionEvent& event,
                                  WindowDestructionReceiver& destruction_receiver) {
  MouseEvent e(this, MouseEvent::Button::kNone, int32_t(event.x), int32_t(event.y));
  OnMouseMove(e, destruction_receiver);
}

void SDLWindow::HandleMouseButton(const SDL_MouseButtonEvent& event,
                                  WindowDestructionReceiver& destruction_receiver) {
  MouseEvent e(this, TranslateSDLMouseButton(event.button), int32_t(event.x),
               int32_t(event.y));
  if (event.down) {
    OnMouseDown(e, destruction_receiver);
  } else {
    OnMouseUp(e, destruction_receiver);
  }
}

void SDLWindow::HandleMouseWheel(const SDL_MouseWheelEvent& event,
                                 WindowDestructionReceiver& destruction_receiver) {
  const float direction =
      event.direction == SDL_MOUSEWHEEL_FLIPPED ? -1.0f : 1.0f;
  MouseEvent e(this, MouseEvent::Button::kNone, int32_t(event.mouse_x),
               int32_t(event.mouse_y),
               int32_t(event.x * direction * MouseEvent::kScrollPerDetent),
               int32_t(event.y * direction * MouseEvent::kScrollPerDetent));
  OnMouseWheel(e, destruction_receiver);
}

void SDLWindow::HandleSDLEvent(const SDL_Event& event) {
  if (!window_) {
    return;
  }

  WindowDestructionReceiver destruction_receiver(this);
  switch (event.type) {
    case SDL_EVENT_WINDOW_CLOSE_REQUESTED:
    case SDL_EVENT_WINDOW_DESTROYED:
      CloseFromSDL();
      return;
    case SDL_EVENT_WINDOW_FOCUS_GAINED:
      OnFocusUpdate(true, destruction_receiver);
      break;
    case SDL_EVENT_WINDOW_FOCUS_LOST:
      OnFocusUpdate(false, destruction_receiver);
      break;
    case SDL_EVENT_WINDOW_RESIZED:
    case SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED:
      HandleSizeUpdate(destruction_receiver);
      break;
    case SDL_EVENT_WINDOW_EXPOSED:
      OnPaint(true);
      break;
    case SDL_EVENT_KEY_DOWN:
    case SDL_EVENT_KEY_UP:
      HandleKeyboard(event.key, destruction_receiver);
      break;
    case SDL_EVENT_TEXT_INPUT:
      HandleTextInput(event.text, destruction_receiver);
      break;
    case SDL_EVENT_MOUSE_MOTION:
      HandleMouseMotion(event.motion, destruction_receiver);
      break;
    case SDL_EVENT_MOUSE_BUTTON_DOWN:
    case SDL_EVENT_MOUSE_BUTTON_UP:
      HandleMouseButton(event.button, destruction_receiver);
      break;
    case SDL_EVENT_MOUSE_WHEEL:
      HandleMouseWheel(event.wheel, destruction_receiver);
      break;
    default:
      break;
  }
}

void SDLWindow::OnSDLPaintEvent() {
  if (window_) {
    OnPaint();
  }
}

}  // namespace ui
}  // namespace rex
