#pragma once

#include <cstdint>
#include <functional>
#include <string_view>

namespace refii::renderer {

struct RendererHookArgs;

namespace bink {

struct FableBinkVideoPlaneHeaderInfo {
  uint32_t header_plane_bytes = 0;
  uint32_t row_pitch_bytes = 0;
  uint32_t texture_height = 0;
  uint32_t bytes_per_pixel = 0;
};

struct FableBinkVideoPlaneSetupCallbacks {
  std::function<uint32_t(uint32_t byte_size, uint32_t flags)>
      allocate_guest_memory;
  std::function<FableBinkVideoPlaneHeaderInfo(uint32_t texture,
                                              uint32_t width,
                                              uint32_t height)>
      initialize_texture_header;
};

struct FableBinkVideoPlaneSetupResult {
  bool handled = false;
  uint32_t texture = 0;
  uint32_t plane_memory = 0;
  uint32_t plane_bytes = 0;
  uint32_t row_pitch_bytes = 0;
};

bool IsFableBinkVideoPlaneSetupSymbol(const char* symbol);
bool IsFableBinkVideoPlaneSetupSymbol(std::string_view symbol);
bool IsFableBinkVideoPlaneSurfaceSetupSymbol(const char* symbol);
bool IsFableBinkVideoPlaneSurfaceSetupSymbol(std::string_view symbol);

bool CanEmulateFableBinkVideoPlaneSetup(const RendererHookArgs& args,
                                        uint8_t* base);
bool CanEmulateFableBinkVideoPlaneSurfaceSetup(uint8_t* base);

FableBinkVideoPlaneSetupResult EmulateFableBinkVideoPlaneSetup(
    const RendererHookArgs& args,
    uint8_t* base,
    const FableBinkVideoPlaneSetupCallbacks& callbacks);

bool EmulateFableBinkVideoPlaneSurfaceSetup(uint8_t* base);

}  // namespace bink
}  // namespace refii::renderer
