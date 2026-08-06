#include "runtime/refii_renderer/plume/fable/title/fable_title_root_texture_header.h"

#include <rex/logging.h>

namespace refii::renderer {
namespace {

bool FableTitleRootTextureHeaderCaptureCallbacksReady(
    const FableTitleRootTextureHeaderCaptureCallbacks& callbacks) {
  return callbacks.address.is_likely_guest_dword_address &&
         callbacks.is_likely_fable_object_address &&
         callbacks.is_stack_scratch &&
         callbacks.upsert_texture_from_fetch_dwords &&
         callbacks.store_texture_object_fetch_dwords &&
         callbacks.find_texture_parent && callbacks.is_plausible_extent &&
         callbacks.remember_last_root_texture_header &&
         callbacks.exact_texture_producer_symbol_address &&
         callbacks.remember_exact_title_texture_refresh_candidate &&
         callbacks.register_exact_texture_header_action &&
         callbacks.should_log && callbacks.reserve_log_index;
}

}  // namespace

FableTitleRootTextureHeaderPacket FableReadTitleRootTextureHeaderPacket(
    uint8_t* base,
    uint32_t texture_header,
    const FableTitleRootTextureHeaderAddressCallbacks& callbacks) {
  FableTitleRootTextureHeaderPacket packet;
  if (base == nullptr || texture_header == 0 ||
      callbacks.is_likely_guest_dword_address == nullptr ||
      !callbacks.is_likely_guest_dword_address(texture_header) ||
      !callbacks.is_likely_guest_dword_address(texture_header + 48u)) {
    return packet;
  }

  constexpr uint32_t kTextureFetchOffset = 28u;
  packet.texture_header = texture_header;
  packet.fetch_address = texture_header + kTextureFetchOffset;
  packet.fetch_dwords = FableLoadTextureFetchDwords(base, packet.fetch_address);
  if (!FableTextureFetchHasDwords(packet.fetch_dwords)) {
    return packet;
  }

  packet.fetch = FableTextureFetchInfoFromDwords(packet.fetch_dwords);
  packet.valid = packet.fetch.valid;
  return packet;
}

void FableCaptureTitleRootTextureHeader(
    std::string_view symbol,
    const RendererHookArgs& args,
    uint8_t* base,
    uint32_t texture_header,
    std::string_view source_name,
    const FableTitleRootTextureHeaderCaptureCallbacks& callbacks) {
  if (!FableTitleRootTextureHeaderCaptureCallbacksReady(callbacks)) {
    return;
  }

  const FableTitleRootTextureHeaderPacket header_packet =
      FableReadTitleRootTextureHeaderPacket(base, texture_header,
                                            callbacks.address);
  if (!header_packet.valid) {
    return;
  }

  const uint32_t fetch_address = header_packet.fetch_address;
  const std::array<uint32_t, 6>& dwords = header_packet.fetch_dwords;

  callbacks.upsert_texture_from_fetch_dwords(dwords, base);
  const TextureFetchSnapshotInfo fetch_info =
      GetTextureFetchSnapshotInfo(dwords);

  const bool stack_scratch = callbacks.is_stack_scratch(args, texture_header);
  bool tracked_object = false;
  uint32_t texture_object = 0;
  uint32_t parent_texture = 0;
  if (!stack_scratch &&
      callbacks.is_likely_fable_object_address(texture_header)) {
    callbacks.store_texture_object_fetch_dwords(texture_header, fetch_address,
                                                32u, base, args.lr);
    tracked_object = true;
    texture_object = texture_header;
    callbacks.find_texture_parent(texture_header, parent_texture);
    if (callbacks.is_plausible_extent(fetch_info.width, fetch_info.height)) {
      callbacks.remember_last_root_texture_header(
          texture_header, fetch_info.width, fetch_info.height,
          fetch_info.format);
    }
  }

  if (tracked_object) {
    callbacks.remember_exact_title_texture_refresh_candidate(
        texture_header, fetch_info, source_name,
        callbacks.exact_texture_producer_symbol_address(symbol), args.lr);
  }

  callbacks.register_exact_texture_header_action(
      symbol, args, texture_header, fetch_address, dwords, fetch_info,
      stack_scratch, texture_object, parent_texture);

  if (callbacks.should_log()) {
    const uint32_t log_index = callbacks.reserve_log_index();
    if (log_index != 0) {
      REXLOG_INFO(
          "Refii renderer root texture header #{} symbol={} source={} "
          "header=0x{:08X} stack={} object={} base=0x{:08X} size={} "
          "extent={}x{} fmt={} "
          "dwords={:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X} lr=0x{:08X}",
          log_index, symbol, source_name, texture_header,
          stack_scratch ? 1 : 0, tracked_object ? 1 : 0,
          fetch_info.base_address, fetch_info.size, fetch_info.width,
          fetch_info.height, fetch_info.format, dwords[0], dwords[1],
          dwords[2], dwords[3], dwords[4], dwords[5], args.lr);
    }
  }
}

}  // namespace refii::renderer
