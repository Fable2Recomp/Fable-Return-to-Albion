#include "runtime/refii_renderer/gtl/refii_gtl.h"
#include "runtime/refii_renderer/gtl/refii_gtl_state.h"
#include "runtime/refii_renderer/gtl/refii_gtl_symbols.h"

#include "runtime/refii_renderer/gtl/refii_gtl_state.h"
#include "runtime/refii_renderer/gtl/refii_gtl_symbols.h"
#include <algorithm>
#include <array>
#include <bit>
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <atomic>
#include <mutex>
#include <string>
#include <unordered_map>
#include <unordered_set>
#include "renderer_runtime.h"
#include "rex/logging.h"
#include "runtime/cvars.h"
#include <rex/chrono/clock.h>
#include <rex/graphics/pipeline/texture/info.h>
#include "runtime/refii_renderer/hooks/general/fable_hook_stack.h"
#include "runtime/refii_renderer/hooks/root/fable_hook_symbols.h"
#include "runtime/refii_renderer/bik/bink_guest_memory.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_state.h"
#include "runtime/refii_renderer/plume/fable/resource/fable_resource_snapshots.h"
#include "runtime/refii_renderer/plume/fable/resource/fable_upload_data.h"
#include "runtime/refii_renderer/plume/general/plume_fable_addressing.h"

namespace refii::gtl {

template <std::size_t N>
bool LoadDeviceRegisterShadow(uint8_t *guest_base, uint32_t device,
                              uint32_t offset, std::array<uint32_t, N> &out) {
  const uint32_t at = device + offset;
  if (!refii::renderer::bink::IsReadableGuestByteRange(
          guest_base, at, static_cast<uint32_t>(N * sizeof(uint32_t)))) {
    return false;
  }
  for (std::size_t i = 0; i < N; ++i) {
    out[i] = refii::renderer::bink::LoadGuestU32(
        guest_base, at + static_cast<uint32_t>(i * sizeof(uint32_t)));
  }
  return true;
}

float TessConstantAsFloat(uint32_t bits) {
  float value = 0.0f;
  std::memcpy(&value, &bits, sizeof(value));
  return value;
}

// Resolve one of the game's SHADER BINDING indices (332/342/343/396) to its
// constant register and current value. See sub_822239F8: entry = table +
// 12*index, entry[0] = register, entry[1] = count, value at
// device + 0x780 + 16*register.
TessellatedPatchDrawPacket::ShaderConstantProbe ProbeShaderBinding(
    uint8_t *guest_base, uint32_t device, uint32_t binding_index) {
  TessellatedPatchDrawPacket::ShaderConstantProbe probe;
  probe.binding_index = binding_index;
  const uint32_t entry =
      kShaderBindingTable + binding_index * kShaderBindingTableStride;
  if (!refii::renderer::bink::IsReadableGuestByteRange(guest_base, entry, 8u)) {
    return probe;
  }
  probe.constant_register =
      refii::renderer::bink::LoadGuestU32(guest_base, entry);
  probe.constant_count =
      refii::renderer::bink::LoadGuestU32(guest_base, entry + 4u);
  // 256 vec4 in the vertex ALU bank (0x4000..0x43FF); anything else is not a
  // register this shadow holds and is reported unresolved rather than guessed.
  if (probe.constant_register >= 256u) {
    return probe;
  }
  const uint32_t at =
      device + kDeviceVsFloatConstantShadow + probe.constant_register * 16u;
  if (!refii::renderer::bink::IsReadableGuestByteRange(guest_base, at, 16u)) {
    return probe;
  }
  for (uint32_t component = 0; component < 4u; ++component) {
    probe.value[component] = refii::renderer::bink::LoadGuestU32(
        guest_base, at + component * 4u);
  }
  probe.valid = true;
  return probe;
}

uint64_t TessHashMix(uint64_t hash, uint64_t value) {
  hash ^= value + 0x9E3779B97F4A7C15ull + (hash << 6) + (hash >> 2);
  return hash;
}

// Walk the emitted PM4 stream and pull out the DRAW_INDX packets. PM4 headers:
// bits 31:30 = type; type-3 carries (count-1) in bits 29:16 and the opcode in
// bits 15:8; type-0 is a register write run with (count-1) in bits 29:16;
// type-2 is a single-dword NOP. DRAW_INDX is opcode 0x22 (both emitters write
// it with the predicate bit 0 set: 0xC0032201 / 0xC0012201).
void DecodeEmittedDrawPackets(TessellatedPatchDrawPacket &packet) {
  const std::vector<uint32_t> &dwords = packet.emitted_dwords;
  std::size_t index = 0;
  while (index < dwords.size()) {
    const uint32_t header = dwords[index];
    const uint32_t type = header >> 30u;
    if (type == 3u) {
      const uint32_t body = ((header >> 16u) & 0x3FFFu) + 1u;
      const uint32_t opcode = (header >> 8u) & 0xFFu;
      if (opcode == 0x22u) {
        ++packet.draw_packet_count;
        if (packet.draw_header == 0u && index + body < dwords.size()) {
          packet.draw_header = header;
          packet.draw_viz_query = dwords[index + 1u];
          if (body >= 2u) {
            packet.draw_initiator = dwords[index + 2u];
          }
          if (body >= 4u) {
            packet.draw_index_base = dwords[index + 3u];
            packet.draw_index_size = dwords[index + 4u];
          }
        }
      }
      index += 1u + body;
      continue;
    }
    if (type == 0u) {
      index += 1u + (((header >> 16u) & 0x3FFFu) + 1u);
      continue;
    }
    if (type == 2u) {
      ++index;
      continue;
    }
    // Type-1 is not produced by this driver; stop rather than mis-walk.
    break;
  }
}

// Emit the once-per-DISTINCT-SHAPE characterisation record. A flat per-draw cap
// is useless here: terrain emits one call per contiguous run of visible
// sub-tiles and water four per patch, so a cap fills with the first repeating
// pass long before anything else runs. Caller holds g_state.mutex.
void LogTessellatedPatchShapeLocked(const TessellatedPatchDrawPacket &packet) {
  if (g_state.tess_logged_shapes.size() >= 256u ||
      !g_state.tess_logged_shapes.insert(packet.shape_key).second) {
    return;
  }
  ++g_state.stats.tess_distinct_shapes;
  const uint32_t shape_number =
      static_cast<uint32_t>(g_state.tess_logged_shapes.size());
  const bool terrain = packet.source_symbol == kEmitTessellatedIndexedPackets;
  const auto &vgt = packet.vgt_tess_regs;

  REXLOG_INFO(
      "Refii GTL TESS-PATCH shape #{} key=0x{:016X} emitter={} (0x{:08X}) "
      "lr=0x{:08X} frame={} packet_id={} args[prim=0x{:X} index_offset={} "
      "count={} indexed={}]",
      shape_number, static_cast<unsigned long long>(packet.shape_key),
      terrain ? "TERRAIN/bg-patch" : "WATER/heightfield", packet.source_symbol,
      packet.caller_lr, static_cast<unsigned long long>(packet.frame_id),
      static_cast<unsigned long long>(packet.packet_id),
      packet.arg_primitive_type, packet.arg_index_offset, packet.arg_count,
      packet.indexed ? 1 : 0);

  REXLOG_INFO(
      "Refii GTL TESS-PATCH shape #{} vgt regs_valid={} "
      "OUTPUT_PATH_CNTL(0x2284)=0x{:08X} HOS_CNTL(0x2285)=0x{:08X} "
      "tess_mode={} HOS_MAX_TESS(0x2286)={:.4f} HOS_MIN_TESS(0x2287)={:.4f} "
      "HOS_REUSE_DEPTH(0x2288)=0x{:08X} GROUP_PRIM_TYPE(0x2289)=0x{:08X} "
      "FIRST_DECR(0x228A)={} DECR(0x228B)={} VECT0_CNTL(0x228C)=0x{:08X} "
      "VECT1_CNTL(0x228D)=0x{:08X} VECT0_FMT(0x228E)=0x{:08X} "
      "VECT1_FMT(0x228F)=0x{:08X}",
      shape_number, packet.device_regs_valid ? 1 : 0, vgt[4], vgt[5],
      vgt[5] & 3u, TessConstantAsFloat(vgt[6]), TessConstantAsFloat(vgt[7]),
      vgt[8], vgt[9], vgt[10], vgt[11], vgt[12], vgt[13], vgt[14], vgt[15]);

  // VGT_DRAW_INITIATOR decode. Field positions are the ones the emitters
  // construct: PRIM_TYPE bits 5:0, SOURCE_SELECT bits 7:6 (0 = DMA index
  // buffer, 2 = auto-index), bit 11 = 32-bit index format (the emitter ORs
  // 0x800 exactly when the bound index buffer's flag word has bit 0 set),
  // count in bits 31:16.
  const uint32_t initiator = packet.draw_initiator;
  REXLOG_INFO(
      "Refii GTL TESS-PATCH shape #{} pm4 ring=0x{:08X}..0x{:08X} valid={} "
      "dwords={} draw_packets={} header=0x{:08X} viz_query=0x{:08X} "
      "initiator=0x{:08X}[prim=0x{:02X} src_sel={} idx32={} count={}] "
      "index_base=0x{:08X} index_size=0x{:08X}[size={} endian={}]",
      shape_number, packet.ring_begin, packet.ring_end,
      packet.ring_capture_valid ? 1 : 0,
      static_cast<uint32_t>(packet.emitted_dwords.size()),
      packet.draw_packet_count, packet.draw_header, packet.draw_viz_query,
      initiator, initiator & 0x3Fu, (initiator >> 6u) & 3u,
      (initiator >> 11u) & 1u, initiator >> 16u, packet.draw_index_base,
      packet.draw_index_size, packet.draw_index_size & 0x00FFFFFFu,
      packet.draw_index_size >> 30u);

  REXLOG_INFO(
      "Refii GTL TESS-PATCH shape #{} bind vs=0x{:08X} ps=0x{:08X} "
      "rt=[0x{:08X} 0x{:08X} 0x{:08X} 0x{:08X}] depth=0x{:08X} "
      "hi_z_control(0x2203)=0x{:08X} predication[enabled={} mask=0x{:08X}:{:08X} "
      "bin_select=0x{:08X}] tiled_variant={} viz_index={} "
      "index_buffer[obj=0x{:08X} flags=0x{:08X} gpu_base=0x{:08X} fmt={}]",
      shape_number, packet.vertex_shader, packet.pixel_shader,
      packet.render_targets[0], packet.render_targets[1],
      packet.render_targets[2], packet.render_targets[3], packet.depth_target,
      packet.hi_z_control, packet.predication_enabled ? 1 : 0,
      packet.predication_mask_hi, packet.predication_mask,
      packet.predication_bin_select, packet.tiled_variant ? 1 : 0,
      packet.viz_query_index, packet.index_buffer_object,
      packet.index_buffer_flags, packet.index_buffer_gpu_base,
      packet.index_32bit ? 32 : 16);

  REXLOG_INFO(
      "Refii GTL TESS-PATCH shape #{} regs RB_SURFACE_INFO(0x2000)=0x{:08X} "
      "RB_COLOR_INFO(0x2001)=0x{:08X} RB_COLOR1(0x2002)=0x{:08X} "
      "RB_DEPTH_INFO(0x2010)=0x{:08X} VGT_MAX_VTX_INDX(0x2100)=0x{:08X} "
      "VGT_MIN_VTX_INDX(0x2101)=0x{:08X} VGT_INDX_OFFSET(0x2102)=0x{:08X} "
      "SQ_PROGRAM_CNTL(0x2200)=0x{:08X} RB_MODECONTROL(0x2300)=0x{:08X} "
      "RB_DEPTHCONTROL(0x2301)=0x{:08X} RB_COPY_CONTROL(0x2380)=0x{:08X}",
      shape_number, packet.rb_surface_regs[0], packet.rb_surface_regs[1],
      packet.rb_surface_regs[2], packet.rb_surface_regs[16],
      packet.pa_vgt_regs[0], packet.pa_vgt_regs[1], packet.pa_vgt_regs[2],
      packet.sq_regs[0], packet.rb_mode_regs[0], packet.rb_mode_regs[1],
      packet.rb_copy_regs[0]);

  for (const auto &probe : packet.constant_probes) {
    if (probe.binding_index == 0u) {
      continue;
    }
    REXLOG_INFO(
        "Refii GTL TESS-PATCH shape #{} const binding={} valid={} reg={} "
        "count={} value=({:.6f} {:.6f} {:.6f} {:.6f}) raw=0x{:08X}",
        shape_number, probe.binding_index, probe.valid ? 1 : 0,
        probe.constant_register, probe.constant_count,
        TessConstantAsFloat(probe.value[0]),
        TessConstantAsFloat(probe.value[1]),
        TessConstantAsFloat(probe.value[2]),
        TessConstantAsFloat(probe.value[3]), probe.value[0]);
  }

  // Fetch constants are where the patch VERTEX data and the sampled
  // heightfield/atlas actually come from on these paths (there is no D3D
  // stream source). Log every populated slot: dword1 bits 31:12 is the base
  // address, dword0 carries type/stride/endian and dword2 the size.
  if (packet.fetch_dwords_valid) {
    for (uint32_t slot = 0; slot < 32u; ++slot) {
      const uint32_t at = slot * 6u;
      const uint32_t base = packet.fetch_dwords[at + 1u] & 0xFFFFF000u;
      if (base == 0u) {
        continue;
      }
      REXLOG_INFO(
          "Refii GTL TESS-PATCH shape #{} fetch slot={} base=0x{:08X} "
          "dwords=[0x{:08X} 0x{:08X} 0x{:08X} 0x{:08X} 0x{:08X} 0x{:08X}]",
          shape_number, slot, base, packet.fetch_dwords[at],
          packet.fetch_dwords[at + 1u], packet.fetch_dwords[at + 2u],
          packet.fetch_dwords[at + 3u], packet.fetch_dwords[at + 4u],
          packet.fetch_dwords[at + 5u]);
    }
  }

  // The raw stream, capped. This is the ground truth every field above is
  // decoded from; without it a wrong decode is unfalsifiable.
  const std::size_t dump = std::min<std::size_t>(packet.emitted_dwords.size(),
                                                 64u);
  std::string dump_text;
  dump_text.reserve(dump * 11u);
  char scratch[16];
  for (std::size_t i = 0; i < dump; ++i) {
    std::snprintf(scratch, sizeof(scratch), "%08X ", packet.emitted_dwords[i]);
    dump_text += scratch;
  }
  REXLOG_INFO("Refii GTL TESS-PATCH shape #{} pm4_dump[{} of {}] {}",
              shape_number, static_cast<uint32_t>(dump),
              static_cast<uint32_t>(packet.emitted_dwords.size()), dump_text);
}

// Exit half of the split capture. Caller holds g_state.mutex.
void CompleteTessellatedPatchCaptureLocked(const RendererHookArgs & /*args*/) {
  const PendingTessellatedPatchCapture pending = t_pending_tess_capture;
  t_pending_tess_capture = {};
  if (pending.symbol_address == 0) {
    return;
  }

  TessellatedPatchDrawPacket packet;
  packet.packet_id = g_state.next_packet_id++;
  packet.frame_id = g_state.frame_id;
  packet.source_symbol = pending.symbol_address;
  packet.caller_lr = pending.caller_lr;
  packet.device = pending.device;
  packet.arg_primitive_type = pending.arg_primitive_type;
  packet.arg_index_offset = pending.arg_index_offset;
  packet.arg_count = pending.arg_count;
  packet.indexed =
      pending.symbol_address == kEmitTessellatedIndexedPackets;
  packet.ring_begin = pending.ring_begin;
  // Carried from entry (see PendingTessellatedPatchCapture::device_snapshot).
  // Capture-only; no consumer, and the tess packets have no float-bank/cull
  // mirror to compare against, so they contribute to the snapshot stream but
  // not to the class-A mirror-divergence counters.
  packet.device_snapshot = pending.device_snapshot;

  const DeviceState &device_state = g_state.device;
  packet.vertex_shader = device_state.vertex_shader;
  packet.pixel_shader = device_state.pixel_shader;
  packet.render_targets = device_state.render_targets;
  packet.depth_target = device_state.depth_target;
  packet.textures = device_state.textures;
  // Same source and same mirror BuildDrawPacketLocked uses (refii_gtl.cpp
  // `packet.viewport = device.viewport`). The tess capture never carried one,
  // which left every tess command without the extent evidence the render-target
  // mint gate requires.
  packet.viewport = device_state.viewport;

  uint8_t *guest_base = pending.guest_base;
  const uint32_t device = pending.device;
  if (guest_base != nullptr && device != 0) {
    // Same device-object offsets BuildDrawPacketLocked backfills from; Fable
    // stages shader/target binding rather than calling the D3D setters, so the
    // tracked state is usually stale.
    const auto load = [&](uint32_t offset) -> uint32_t {
      return refii::renderer::bink::LoadGuestU32(guest_base, device + offset);
    };
    // LIVE READ WINS, ZERO INCLUDED -- matching what every other draw root
    // does through ApplyAuthoritativeExitDeviceStateLocked. This used to be
    // gated on `== 0`, i.e. the setter-fed g_state.device mirror won and the
    // device was consulted only to fill a hole. That is the exact defect
    // already fixed for the non-tess roots, and it is what produced
    // `TESS-DRAW tess-variant VS MISS vs=0x4CF4F1A0 hash=0` while the correct
    // landscape-tessellation shader 0x4CAD7F40 sat linked and healthy in the
    // registry: a stale mirror pointer that no shader record was ever minted
    // for, so no bytecode hash, so no tess variant, so every terrain patch
    // draw was skipped and gtl_tess_draws stayed 0.
    //
    // IDA proves the live read is safe to trust here. device+0x3198 has
    // exactly ONE writer in the whole image -- D3DDevice_SetVertexShader's
    // `stw r29, 0x3198(r30)` at 0x822325B0, which sits at the join point every
    // preceding branch falls into and so is unconditional on entry (NULL
    // binds included). The tess emitter 0x82207AF8 never reads or writes it
    // (its 0x3094 load is the INDEX BUFFER), and none of its callees appear in
    // the writer list, so the register is valid and unmodified at exit. Same
    // single-writer story for 0x3194/0x3098/0x30A8.
    const bool device_readable = refii::renderer::bink::IsReadableGuestByteRange(
        guest_base, device, kFableDeviceObjectExtent);
    if (device_readable) {
      packet.vertex_shader = load(0x3198u);
      packet.pixel_shader = load(0x3194u);
      for (uint32_t rt = 0; rt < kMaxRenderTargets; ++rt) {
        packet.render_targets[rt] = load(0x3098u + rt * 4u);
      }
      packet.depth_target = load(0x30A8u);
    }

    packet.device_regs_valid =
        LoadDeviceRegisterShadow(guest_base, device, kDeviceRegShadowVgtTess,
                                 packet.vgt_tess_regs);
    LoadDeviceRegisterShadow(guest_base, device, kDeviceRegShadowRbSurface,
                             packet.rb_surface_regs);
    LoadDeviceRegisterShadow(guest_base, device, kDeviceRegShadowPaVgt,
                             packet.pa_vgt_regs);
    LoadDeviceRegisterShadow(guest_base, device, kDeviceRegShadowSq,
                             packet.sq_regs);
    LoadDeviceRegisterShadow(guest_base, device, kDeviceRegShadowRbMode,
                             packet.rb_mode_regs);
    LoadDeviceRegisterShadow(guest_base, device, kDeviceRegShadowRbCopy,
                             packet.rb_copy_regs);
    if (!packet.device_regs_valid) {
      ++g_state.stats.tess_device_regs_unreadable;
    }

    packet.hi_z_control = load(kDeviceHiZControl);
    packet.predication_mask = load(kDevicePredicationMaskLo);
    packet.predication_mask_hi = load(kDevicePredicationMaskHi);
    packet.predication_bin_select = load(kDevicePredicationBinSelect);
    packet.predication_enabled = load(kDevicePredicationEnabled) != 0u;
    packet.tiled_variant =
        (refii::renderer::bink::LoadGuestU8(
             guest_base, device + kDeviceTiledVariantFlag) &
         1u) != 0u;
    packet.viz_query_index = refii::renderer::bink::LoadGuestU8(
        guest_base, device + kDeviceVizQueryIndex);

    packet.index_buffer_object = load(kDeviceIndexBufferSlot);
    if (packet.index_buffer_object != 0 &&
        refii::renderer::bink::IsReadableGuestByteRange(
            guest_base, packet.index_buffer_object, 0x1Cu)) {
      packet.index_buffer_flags =
          refii::renderer::bink::LoadGuestU32(guest_base,
                                              packet.index_buffer_object);
      packet.index_buffer_gpu_base = refii::renderer::bink::LoadGuestU32(
          guest_base, packet.index_buffer_object + 0x18u);
      packet.index_32bit = (packet.index_buffer_flags & 1u) != 0u;
    }

    // Fetch constants: device+0x480 + slot*24, 6 dwords each.
    bool any_fetch = false;
    for (uint32_t slot = 0; slot < 32u; ++slot) {
      const uint32_t fetch_at = device + 0x480u + slot * 24u;
      if (!refii::renderer::bink::IsReadableGuestByteRange(guest_base, fetch_at,
                                                           24u)) {
        continue;
      }
      for (uint32_t d = 0; d < 6u; ++d) {
        packet.fetch_dwords[slot * 6u + d] =
            refii::renderer::bink::LoadGuestU32(guest_base, fetch_at + d * 4u);
      }
      any_fetch = true;
    }
    packet.fetch_dwords_valid = any_fetch;

    // Float constant banks, read post-flush (this is hook EXIT — the emitter
    // has already uploaded its shadow to the ring, so dev+0x780/0x1780 hold
    // the at-draw values). Same offsets and byte-swap as the DrawPacket exit
    // self-read (VS = reg 120*16, PS = reg 376*16, big-endian guest dwords).
    // No tonemap +inf sanitize here: that rewrite is specific to the title
    // tonemap PIXEL shader's c47.x threshold, which never rides these two
    // emitters (terrain/water materials).
    const auto read_tess_bank = [&](uint32_t bank_offset,
                                    std::vector<uint32_t>& out) {
      constexpr uint32_t kTessFloatConstantDwords = 1024u;
      const uint32_t at = device + bank_offset;
      const uint32_t bytes = kTessFloatConstantDwords * 4u;
      if (!refii::renderer::bink::IsReadableGuestByteRange(guest_base, at,
                                                           bytes)) {
        return;
      }
      const uint8_t* block =
          guest_base + at + refii::renderer::bink::GuestPhysicalHostOffset(at);
      out.resize(kTessFloatConstantDwords);
      for (uint32_t i = 0; i < kTessFloatConstantDwords; ++i) {
        uint32_t raw = 0;
        std::memcpy(&raw, block + i * 4u, sizeof(raw));
        out[i] = __builtin_bswap32(raw);
      }
    };
    read_tess_bank(120u * 16u, packet.vs_float_constants);
    read_tess_bank(376u * 16u, packet.ps_float_constants);

    // Binding-index probes, per emitter (see the header for the call sites).
    const std::array<uint32_t, 4> bindings =
        packet.indexed ? std::array<uint32_t, 4>{332u, 0u, 0u, 0u}
                       : std::array<uint32_t, 4>{343u, 396u, 342u, 0u};
    for (std::size_t i = 0; i < bindings.size(); ++i) {
      if (bindings[i] == 0u) {
        continue;
      }
      packet.constant_probes[i] =
          ProbeShaderBinding(guest_base, device, bindings[i]);
    }

    // Ring delta. The emitters use `stwu rX, 4(ring)`, so device+0x30 always
    // points AT the last dword written: the payload is (begin, end]. A KickOff
    // or a SetPending_Split inside the call moves the pointer non-monotonically
    // -- refuse rather than capture a bogus span.
    const uint32_t ring_end =
        load(kDeviceRingWritePointer);
    packet.ring_end = ring_end;
    constexpr uint32_t kMaxRingSpanBytes = 64u * 1024u;
    if (pending.ring_begin != 0 && ring_end > pending.ring_begin &&
        (ring_end - pending.ring_begin) <= kMaxRingSpanBytes &&
        refii::renderer::bink::IsReadableGuestByteRange(
            guest_base, pending.ring_begin + 4u,
            ring_end - pending.ring_begin)) {
      const uint32_t dword_count = (ring_end - pending.ring_begin) / 4u;
      packet.emitted_dwords.reserve(dword_count);
      for (uint32_t i = 0; i < dword_count; ++i) {
        packet.emitted_dwords.push_back(refii::renderer::bink::LoadGuestU32(
            guest_base, pending.ring_begin + 4u + i * 4u));
      }
      packet.ring_capture_valid = true;
      DecodeEmittedDrawPackets(packet);
    } else {
      ++g_state.stats.tess_ring_capture_failed;
    }
  }

  g_state.stats.tess_pm4_draws += packet.draw_packet_count;
  g_state.stats.tess_pm4_draws_this_frame += packet.draw_packet_count;

  // Shape key: everything a rendering pass would have to branch on. Deliberately
  // EXCLUDES per-draw quantities (count, index offset, the sub-tile constant)
  // so one shape covers a whole pass instead of one draw.
  uint64_t key = TessHashMix(0xCBF29CE484222325ull, packet.source_symbol);
  key = TessHashMix(key, packet.caller_lr);
  key = TessHashMix(key, packet.draw_initiator & 0xFFFFu);
  key = TessHashMix(key, packet.draw_header);
  for (std::size_t i = 4; i < 16 && i < packet.vgt_tess_regs.size(); ++i) {
    key = TessHashMix(key, packet.vgt_tess_regs[i]);
  }
  key = TessHashMix(key, packet.vertex_shader);
  key = TessHashMix(key, packet.pixel_shader);
  key = TessHashMix(key, packet.render_targets[0]);
  key = TessHashMix(key, packet.depth_target);
  key = TessHashMix(key, packet.rb_surface_regs[0]);
  key = TessHashMix(key, packet.rb_mode_regs[0]);
  key = TessHashMix(key, packet.rb_mode_regs[1]);
  key = TessHashMix(key, packet.index_32bit ? 1u : 0u);
  key = TessHashMix(key, packet.tiled_variant ? 1u : 0u);
  // Predication as a BOOLEAN, not as its mask value. Whether a draw is
  // predicated changes whether it runs at all, so it is a genuine shape
  // discriminator; the mask VALUE is per-draw churn (the shadow-cascade loop at
  // 0x821CD310 sets 2<<slice, so hashing the value would mint one "shape" per
  // cascade slice) and this key deliberately excludes per-draw quantities.
  //
  // dev+0x2940 (now hi_z_control) is NOT hashed. It used to be, under the
  // predication name -- that silently keyed terrain/water shapes on Hi-Z
  // configuration. Hi-Z is an EDRAM early-rejection accelerator with no analog
  // in Refii's non-tiled Vulkan pipeline: it changes how fast a draw rejects,
  // never what it draws. Worse, SetDepthStencilSurface rewrites the word's low
  // nibble (0x822646E4 `clrrwi r11, r11, 4`) on every depth-target change, so
  // it toggles independently of the geometry and spuriously splits shapes. The
  // real depth/stencil behaviour is already in the key as rb_mode_regs[1]
  // (Xenos 0x2301 RB_DEPTHCONTROL).
  key = TessHashMix(key, packet.predication_enabled ? 1u : 0u);
  packet.shape_key = key;

  LogTessellatedPatchShapeLocked(packet);

  // T2 (2026-08-02): this stream is now DRAINED by the direct renderer's
  // authoritative replay (DrainTessellatedPatchDrawPackets*), interleaved at
  // packet_id position like the resolve/clear streams. The cap is a safety
  // valve for a stalled pump only; drop-oldest keeps the newest guest frame.
  constexpr std::size_t kMaxRetainedTessPackets = 1024u;
  if (g_state.tess_packets.size() >= kMaxRetainedTessPackets) {
    g_state.tess_packets.erase(g_state.tess_packets.begin());
  }
  g_state.tess_packets.push_back(std::move(packet));
}

}  // namespace refii::gtl
