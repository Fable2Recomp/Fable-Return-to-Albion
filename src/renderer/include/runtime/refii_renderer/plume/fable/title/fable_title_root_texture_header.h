#pragma once

#include "renderer_runtime.h"
#include "runtime/refii_renderer/plume/fable/resource/fable_texture_fetch.h"

#include <array>
#include <cstdint>
#include <string_view>

namespace refii::renderer {

struct FableTitleRootTextureHeaderAddressCallbacks {
  bool (*is_likely_guest_dword_address)(uint32_t guest_address) = nullptr;
};

struct FableTitleRootTextureHeaderPacket {
  bool valid = false;
  uint32_t texture_header = 0;
  uint32_t fetch_address = 0;
  std::array<uint32_t, 6> fetch_dwords = {};
  FableTextureFetchInfo fetch = {};
};

struct FableTitleRootTextureHeaderCaptureCallbacks {
  FableTitleRootTextureHeaderAddressCallbacks address = {};
  bool (*is_likely_fable_object_address)(uint32_t guest_address) = nullptr;
  bool (*is_stack_scratch)(const RendererHookArgs& args,
                           uint32_t texture_header) = nullptr;
  void (*upsert_texture_from_fetch_dwords)(
      const std::array<uint32_t, 6>& dwords,
      uint8_t* base) = nullptr;
  void (*store_texture_object_fetch_dwords)(uint32_t texture_object,
                                            uint32_t fetch_address,
                                            uint32_t slot,
                                            uint8_t* base,
                                            uint32_t lr) = nullptr;
  bool (*find_texture_parent)(uint32_t texture_object,
                              uint32_t& parent_texture) = nullptr;
  bool (*is_plausible_extent)(uint32_t width, uint32_t height) = nullptr;
  void (*remember_last_root_texture_header)(uint32_t texture_header,
                                            uint32_t width,
                                            uint32_t height,
                                            uint32_t format) = nullptr;
  uint32_t (*exact_texture_producer_symbol_address)(
      std::string_view symbol) = nullptr;
  void (*remember_exact_title_texture_refresh_candidate)(
      uint32_t texture_header,
      const TextureFetchSnapshotInfo& fetch_info,
      std::string_view source_name,
      uint32_t producer_symbol_address,
      uint32_t lr) = nullptr;
  void (*register_exact_texture_header_action)(
      std::string_view symbol,
      const RendererHookArgs& args,
      uint32_t texture_header,
      uint32_t fetch_address,
      const std::array<uint32_t, 6>& dwords,
      const TextureFetchSnapshotInfo& fetch_info,
      bool stack_scratch,
      uint32_t texture_object,
      uint32_t parent_texture) = nullptr;
  bool (*should_log)() = nullptr;
  uint32_t (*reserve_log_index)() = nullptr;
};

FableTitleRootTextureHeaderPacket FableReadTitleRootTextureHeaderPacket(
    uint8_t* base,
    uint32_t texture_header,
    const FableTitleRootTextureHeaderAddressCallbacks& callbacks);

void FableCaptureTitleRootTextureHeader(
    std::string_view symbol,
    const RendererHookArgs& args,
    uint8_t* base,
    uint32_t texture_header,
    std::string_view source_name,
    const FableTitleRootTextureHeaderCaptureCallbacks& callbacks);

}  // namespace refii::renderer
