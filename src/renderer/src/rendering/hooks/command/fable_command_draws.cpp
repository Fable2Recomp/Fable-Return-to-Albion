#include "renderer_runtime.h"
#include "runtime/refii_renderer/guest/guest_memory_access.h"
#include "hooks/ppc_externs.h"
#include "runtime/refii_renderer/bik/bink_startup_audio_pump.h"
#include "runtime/refii_renderer/bik/bink_plane_publish.h"
#include "runtime/refii_renderer/bik/bink_video_plane_setup.h"
#include "renderdoc_debugger.h"
#include "runtime/cvars.h"
#include "runtime/hook_observer.h"
#include "runtime/refii_renderer/gtl/refii_gtl.h"
#include "runtime/renderer_hook_policy.h"
#include "runtime/refii_renderer/hooks/general/fable_scene_boundary_hooks.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_present_state.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_renderer.h"
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_state.h"
#include "runtime/refii_renderer/plume/fable/title/fable_title_scene_population.h"
#include "runtime/refii_renderer/plume/general/plume_fable_addressing.h"
#include "runtime/plume/general/plume_backend.h"
#include "runtime/refii_renderer/hooks/root/fable_hook_symbols.h"
#include <rex/cvar.h>
#include <rex/logging.h>
#include <rex/graphics/pipeline/texture/info.h>
#include <rex/graphics/pipeline/texture/util.h>
#include <rex/graphics/xenos.h>
#include <rex/ppc/context.h>
#include <rex/system/xthread.h>
#include <tracy/Tracy.hpp>
#include <algorithm>
#include <array>
#include <atomic>
#include <cerrno>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <cmath>
#include <deque>
#include <fstream>
#include <limits>
#include <memory>
#include <mutex>
#include <string>
#include <string_view>
#include <thread>
#include <unordered_map>
#include <unordered_set>
#include <windows.h>
#include "runtime/refii_renderer/hooks/renderer_runtime_internal.h"

namespace refii::renderer {

bool IsFableGuestShaderConstantStateHook(std::string_view symbol) {
  return symbol == "sub_8221A880" || // D3DDevice_SetVertexShaderConstantF
         symbol == "sub_82214E30" || // D3DDevice_SetPixelShaderConstantF
         symbol == "sub_82B991B0" || // D3DDevice_SetVertexShaderConstantI
         symbol == "sub_82B99208" || // D3DDevice_SetPixelShaderConstantI
         symbol == "sub_821C6708" || // D3DDevice_SetVertexShaderConstantB
         symbol == "sub_8226E120";   // D3DDevice_SetPixelShaderConstantB
}

bool IsFableGuestShaderConstantCommandHook(std::string_view symbol) {
  return symbol == "sub_82BA76F8" || // D3D::PM4LoadShaderConstantsImmediate
         symbol == "sub_82BA77D0" || // D3D::PM4SetTextureFetchConstant
         symbol == "sub_8222BFA0";   // D3D::SetLiteralShaderConstants
}

bool IsFableGuestShaderConstantReplacementHook(std::string_view symbol) {
  return IsFableGuestShaderConstantStateHook(symbol) ||
         IsFableGuestShaderConstantCommandHook(symbol);
}

FableGuestCommandSubmitEmulation EmulateFableShaderConstantState(
    std::string_view symbol, const RendererHookArgs &args, uint8_t *base) {
  if (!IsFableGuestShaderConstantStateHook(symbol)) {
    return {};
  }
  if (symbol == "sub_821C6708" || symbol == "sub_8226E120") {
    static uint32_t logged_bool_setter_entries = 0;
    if (logged_bool_setter_entries < 8u) {
      REXLOG_INFO(
          "Refii renderer bool setter entry #{} symbol={} r3=0x{:08X} "
          "r4={} r5=0x{:08X} r6={} base={}",
          ++logged_bool_setter_entries, symbol, args.r3, args.r4, args.r5,
          args.r6, base != nullptr ? 1 : 0);
    }
  }

  FableGuestCommandSubmitEmulation emulation;
  emulation.handled = true;
  emulation.result = args.r3;

  // The guest routines have no null check and no validation of any kind, so
  // the only gate that belongs here is host memory safety: the device object
  // is 0x2B00 bytes and the highest field these setters touch is the pixel
  // bool word at +0x2790, so requiring the whole object to be addressable is
  // both sufficient and faithful. IsLikelyFableObjectAddress was NOT that --
  // it is a heuristic address-range test, and failing it dropped the copy AND
  // the dirty-mask publish with no guest counterpart, which is the same
  // silent-drop class already fixed on the draw roots.
  constexpr uint32_t kFableDeviceObjectSize = 0x2B00;
  if (base == nullptr ||
      !IsReadableGuestByteRange(base, args.r3, kFableDeviceObjectSize)) {
    return emulation;
  }

  // D3DDevice_SetVertexShaderConstantF writes to (first + 0x78) * 16.
  constexpr uint32_t kVertexFloatConstantBaseOffset = 0x0780;
  constexpr uint32_t kPixelFloatConstantBaseOffset = 0x1780;
  constexpr uint32_t kVertexIntConstantBaseOffset = 0x27A0;
  constexpr uint32_t kPixelIntConstantBaseOffset = 0x27E0;
  constexpr uint32_t kVertexBoolConstantBaseWord = 0x2780;
  constexpr uint32_t kPixelBoolConstantBaseWord = 0x2790;
  constexpr uint32_t kVertexFloatDirtyQwordOffset = 0x0000;
  constexpr uint32_t kPixelFloatDirtyQwordOffset = 0x0008;
  constexpr uint32_t kScalarConstantDirtyQwordOffset = 0x0020;
  constexpr uint32_t kScalarConstantDirtyBit = 0x01000000u;

  const uint32_t device = args.r3;
  const uint32_t first_register = args.r4;
  const uint32_t source = args.r5;
  const uint32_t count = args.r6;

  if (symbol == "sub_8221A880" || symbol == "sub_82214E30") {
    // IDA sub_8221A880 / sub_82214E30 (they are instruction-for-instruction
    // identical apart from the base offset and the dirty qword):
    //
    //   addi r10, r4, 0x78   ; 0x178 for the pixel twin
    //   slwi r10, r10, 4
    //   add  r10, r10, r3    ; device + 0x780 + first*16
    //   ... lvlx/lvrx/vor/stvx128 -- verbatim 16-byte copies, no transform
    //   ld   r11, 0(r3)      ; 8(r3) for the pixel twin
    //   or   r11, r11, r7    ; FULL 64-bit OR
    //   std  r11, 0(r3)
    //
    // The mask arrives in r7 as a genuine 64-bit input (the function never
    // writes r7; the caller builds it, e.g. 0x83048A64). Bit numbering is
    // MSB-first: register group g = reg>>2 sets bit 63-g, so 4 vec4 per bit,
    // 64 bits x 4 = the full 256-register file. The whole meaningful range
    // for first_register < 128 therefore lives in bits 63..32 -- ORing only
    // the low half wrote nothing at all.
    const bool vertex_bank = symbol == "sub_8221A880";
    const uint32_t dirty_qword =
        device + (vertex_bank ? kVertexFloatDirtyQwordOffset
                              : kPixelFloatDirtyQwordOffset);
    // The guest has NO null check, NO count clamp and NO range check -- the
    // whole routine is 56 instructions with one conditional branch pair. Its
    // count==0 path branches straight to the ld/or/std, so a zero count still
    // dirties the bank. Anything we refuse to copy must therefore still mark
    // the bank dirty, or the driver's flush (sub_8221DE68, which sources the
    // shadow at device+0x780 and clears the mask) skips registers the guest
    // believes it published.
    constexpr uint32_t kMaxVectorConstants = 512;
    const uint32_t source_bytes = count * 16u;
    if (count != 0 && count <= kMaxVectorConstants &&
        IsReadableGuestByteRange(base, source, source_bytes)) {
      const uint32_t destination_base =
          device + (vertex_bank ? kVertexFloatConstantBaseOffset
                                : kPixelFloatConstantBaseOffset) +
          first_register * 16u;
      CopyGuestBytes(base, destination_base, source, source_bytes);
    }
    OrFableGuestQword(base, dirty_qword, args.r7_u64);
    return emulation;
  }

  // The count==0 fallthrough above is IDA-proven for the two float setters
  // ONLY. The int/bool setters below have not been decompiled, so they keep
  // the original early-out rather than inherit an unverified behaviour.
  if (count == 0) {
    return emulation;
  }

  if (symbol == "sub_82B991B0" || symbol == "sub_82B99208") {
    constexpr uint32_t kMaxIntConstants = 64;
    const uint32_t source_bytes = count * 16u;
    if (count > kMaxIntConstants ||
        !IsReadableGuestByteRange(base, source, source_bytes)) {
      return emulation;
    }
    const uint32_t destination_base =
        device + (symbol == "sub_82B991B0" ? kVertexIntConstantBaseOffset
                                           : kPixelIntConstantBaseOffset) +
        first_register * sizeof(uint32_t);
    for (uint32_t i = 0; i < count; ++i) {
      const uint32_t source_vector = source + i * 16u;
      const uint32_t packed =
          (uint32_t(LoadGuestU8(base, source_vector + 11u)) << 16u) |
          (uint32_t(LoadGuestU8(base, source_vector + 7u)) << 8u) |
          uint32_t(LoadGuestU8(base, source_vector + 3u));
      StoreGuestU32(base, destination_base + i * sizeof(uint32_t), packed);
    }
    OrFableGuestQwordLow(base, device + kScalarConstantDirtyQwordOffset,
                         kScalarConstantDirtyBit);
    return emulation;
  }

  constexpr uint32_t kMaxBoolConstants = 256;
  const uint32_t source_bytes = count * sizeof(uint32_t);
  static uint32_t logged_bool_setter_emulations = 0;
  if (logged_bool_setter_emulations < 8u) {
    REXLOG_INFO(
        "Refii renderer bool setter emulation #{} symbol={} device=0x{:08X} "
        "first={} count={} src=0x{:08X} readable={} first_value=0x{:08X}",
        ++logged_bool_setter_emulations, symbol, device, first_register, count,
        source,
        IsReadableGuestByteRange(base, source, source_bytes) ? 1 : 0,
        IsReadableGuestByteRange(base, source, 4u) ? LoadGuestU32(base, source)
                                                   : 0u);
  }
  if (count > kMaxBoolConstants ||
      !IsReadableGuestByteRange(base, source, source_bytes)) {
    return emulation;
  }
  const uint32_t bool_base_word =
      symbol == "sub_821C6708" ? kVertexBoolConstantBaseWord
                               : kPixelBoolConstantBaseWord;
  for (uint32_t i = 0; i < count; ++i) {
    const uint32_t slot = first_register + i;
    const uint32_t word_address =
        device + bool_base_word + (slot >> 5u) * sizeof(uint32_t);
    const uint32_t mask = 1u << (slot & 31u);
    const uint32_t enabled =
        (LoadGuestU32(base, source + i * sizeof(uint32_t)) & 1u) != 0 ? mask : 0u;
    StoreGuestU32(base, word_address,
                  (LoadGuestU32(base, word_address) & ~mask) | enabled);
  }
  OrFableGuestQwordLow(base, device + kScalarConstantDirtyQwordOffset,
                       kScalarConstantDirtyBit);
  return emulation;
}

uint32_t EmulateFableReserveCommandWords(uint32_t device, uint8_t *base,
                                         uint32_t word_count) {
  constexpr uint32_t kCommandPointerOffset = 0x0030;
  constexpr uint32_t kCommandEndOffset = 0x0034;
  constexpr uint32_t kCommandLimitOffset = 0x0038;

  if (base == nullptr || !IsLikelyFableObjectAddress(device) ||
      word_count == 0) {
    return 0;
  }

  uint32_t command_pointer = LoadGuestU32(base, device + kCommandPointerOffset);
  const uint32_t command_limit = LoadGuestU32(base, device + kCommandLimitOffset);
  if (!IsLikelyGuestDwordAddress(command_pointer) ||
      command_pointer + word_count * sizeof(uint32_t) >= command_limit) {
    EmulateFableAllocateCommandSegment(device, base, word_count);
    command_pointer = LoadGuestU32(base, device + kCommandPointerOffset);
  }

  if (!IsLikelyGuestDwordAddress(command_pointer) ||
      command_pointer + word_count * sizeof(uint32_t) >
          LoadGuestU32(base, device + kCommandEndOffset)) {
    return 0;
  }

  return command_pointer;
}

FableGuestCommandSubmitEmulation
EmulateFablePm4LoadShaderConstantsImmediate(const RendererHookArgs &args,
                                            uint8_t *base) {
  FableGuestCommandSubmitEmulation emulation;
  emulation.handled = true;
  emulation.result = 0;

  if (base == nullptr || !IsLikelyFableObjectAddress(args.r3) ||
      !IsLikelyGuestMemoryAddress(args.r5) || args.r6 == 0) {
    return emulation;
  }

  constexpr uint32_t kCommandPointerOffset = 0x0030;
  constexpr uint32_t kMaxVectorConstants = 512;
  if (args.r6 > kMaxVectorConstants) {
    return emulation;
  }

  const uint32_t first_aligned = args.r4 & ~3u;
  const uint32_t leading_pad_words = 4u * (args.r4 - first_aligned);
  const uint32_t total_payload_words =
      4u * (((args.r4 + args.r6 + 3u) & ~3u) - first_aligned);
  if (total_payload_words == 0) {
    emulation.result = 1;
    return emulation;
  }
  const uint32_t trailing_pad_words =
      total_payload_words - args.r6 * 4u - leading_pad_words;
  const uint32_t packet_words = total_payload_words + 1u;
  uint32_t command_pointer =
      EmulateFableReserveCommandWords(args.r3, base, packet_words);
  if (command_pointer == 0) {
    return emulation;
  }

  auto write_word = [&](uint32_t value) {
    command_pointer += sizeof(uint32_t);
    StoreGuestU32(base, command_pointer, value);
  };

  write_word((4u * (first_aligned + 4096u)) |
             ((total_payload_words - 1u) << 16u));
  for (uint32_t i = 0; i < leading_pad_words; ++i) {
    write_word(0);
  }
  for (uint32_t i = 0; i < args.r6 * 4u; ++i) {
    write_word(LoadGuestU32(base, args.r5 + i * sizeof(uint32_t)));
  }
  for (uint32_t i = 0; i < trailing_pad_words; ++i) {
    write_word(0);
  }

  StoreGuestU32(base, args.r3 + kCommandPointerOffset, command_pointer);
  emulation.result = 1;
  return emulation;
}

FableGuestCommandSubmitEmulation
EmulateFablePm4SetTextureFetchConstant(const RendererHookArgs &args,
                                       uint8_t *base) {
  FableGuestCommandSubmitEmulation emulation;
  emulation.handled = true;
  emulation.result = 0;

  if (base == nullptr || !IsLikelyFableObjectAddress(args.r3) ||
      !IsLikelyGuestMemoryAddress(args.r5)) {
    return emulation;
  }

  constexpr uint32_t kCommandPointerOffset = 0x0030;
  constexpr uint32_t kPredicationEnabledOffset = 0x31AC;
  constexpr uint32_t kPacketWords = 8;
  const uint32_t slot = args.r4 & 0xFFFFu;
  uint32_t command_pointer =
      EmulateFableReserveCommandWords(args.r3, base, kPacketWords);
  if (command_pointer == 0) {
    return emulation;
  }

  const uint32_t dword0 = LoadGuestU32(base, args.r5);
  const uint32_t dword1 = LoadGuestU32(base, args.r5 + 4u);
  const uint32_t dword2 = LoadGuestU32(base, args.r5 + 8u);
  const uint32_t dword3 = LoadGuestU32(base, args.r5 + 12u);
  const uint32_t dword4 = LoadGuestU32(base, args.r5 + 16u);
  const uint32_t dword5 = LoadGuestU32(base, args.r5 + 20u);

  auto write_word = [&](uint32_t value) {
    command_pointer += sizeof(uint32_t);
    StoreGuestU32(base, command_pointer, value);
  };
  auto gpu_fetch_pointer = [](uint32_t value) {
    return ((((value >> 20u) + 512u) & 0x1000u) +
            (value & 0x1FFFF000u)) |
           (value & 0xFFFu);
  };

  write_word(LoadGuestU32(base, args.r3 + kPredicationEnabledOffset) |
             0xC0062D00u);
  write_word(0x00010000u | ((6u * slot) & 0x7FFu));
  write_word(dword0);
  write_word(gpu_fetch_pointer(dword1));
  write_word(dword2);
  write_word(dword3);
  write_word(dword4);
  write_word(gpu_fetch_pointer(dword5));

  StoreGuestU32(base, args.r3 + kCommandPointerOffset, command_pointer);
  emulation.result = command_pointer;
  return emulation;
}

FableGuestCommandSubmitEmulation
EmulateFableSetLiteralShaderConstants(const RendererHookArgs &args,
                                      uint8_t *base) {
  FableGuestCommandSubmitEmulation emulation;
  emulation.handled = true;
  emulation.result = 0;

  if (base == nullptr || !IsLikelyFableObjectAddress(args.r3) ||
      !IsLikelyGuestMemoryAddress(args.r4) ||
      !IsLikelyGuestMemoryAddress(args.r5)) {
    return emulation;
  }

  constexpr uint32_t kCommandPointerOffset = 0x0030;
  constexpr uint32_t kMaxLiteralEntries = 512;
  const uint32_t table_offset = LoadGuestU32(base, args.r4 + 20u);
  if (table_offset == 0) {
    return emulation;
  }

  const uint32_t table = args.r4 + table_offset;
  if (!IsLikelyGuestMemoryAddress(table)) {
    return emulation;
  }
  const uint32_t entry_bytes = LoadGuestU32(base, table + 16u);
  uint32_t cursor = table + 20u;
  const uint32_t end = cursor + entry_bytes;
  uint32_t entry_count = 0;
  uint32_t command_pointer = 0;
  while (cursor + 8u <= end && entry_count++ < kMaxLiteralEntries) {
    const uint32_t first_register = LoadGuestU16(base, cursor);
    const uint32_t count = LoadGuestU16(base, cursor + 2u);
    const uint32_t source_offset = LoadGuestU32(base, cursor + 4u);
    cursor += 8u;
    if (count == 0) {
      break;
    }

    command_pointer = EmulateFableReserveCommandWords(args.r3, base, 4u);
    if (command_pointer == 0) {
      return emulation;
    }

    const uint32_t source = args.r5 + source_offset;
    // Feed the GTL the literal payload as well as emitting the guest's PM4.
    // Under GTL authority nothing consumes the ring, so this LOAD_ALU_CONSTANT
    // would otherwise vanish -- and it is the only float carrier that never
    // lands in dev+0x780/0x1780. `first_register` is the absolute ALU register
    // (the packet below writes 4*first_register as the dword offset) and
    // `count` is a dword count (the guest copies 4*count bytes).
    refii::gtl::ApplyLiteralShaderConstants(first_register, count, source,
                                            base);
    auto write_word = [&](uint32_t value) {
      command_pointer += sizeof(uint32_t);
      StoreGuestU32(base, command_pointer, value);
    };
    write_word(0xC0022F00u);
    write_word(FableGpuAddress(source));
    write_word(4u * first_register);
    write_word(count);
    StoreGuestU32(base, args.r3 + kCommandPointerOffset, command_pointer);
  }

  emulation.result = command_pointer;
  return emulation;
}

FableGuestCommandSubmitEmulation EmulateFableShaderConstantCommand(
    std::string_view symbol, const RendererHookArgs &args, uint8_t *base) {
  if (symbol == "sub_82BA76F8") {
    return EmulateFablePm4LoadShaderConstantsImmediate(args, base);
  }
  if (symbol == "sub_82BA77D0") {
    return EmulateFablePm4SetTextureFetchConstant(args, base);
  }
  if (symbol == "sub_8222BFA0") {
    return EmulateFableSetLiteralShaderConstants(args, base);
  }
  return {};
}

FableGuestCommandSubmitEmulation EmulateFableSetPredicationState(
    const RendererHookArgs &args, uint8_t *base) {
  if (base == nullptr || !IsLikelyFableObjectAddress(args.r3)) {
    return {};
  }

  constexpr uint32_t kCommandPointerOffset = 0x0030;
  constexpr uint32_t kCommandLimitOffset = 0x0038;
  constexpr uint32_t kPredicationFlagsOffset = 0x2ABC;
  constexpr uint32_t kPredicationStateOffset = 0x2AB4;
  constexpr uint32_t kPredicationStateRestoreOffset = 0x2AB8;
  constexpr uint32_t kPrimitiveModeOffset = 0x2F9B;
  constexpr uint32_t kPredicationCompareBaseOffset = 0x3098;
  constexpr uint32_t kPredicationMaskOffset = 0x31A4;
  constexpr uint32_t kPredicationMaskRestoreOffset = 0x31A8;
  constexpr uint32_t kPredicationEnabledOffset = 0x31AC;
  constexpr uint32_t kPredicationTrackedBaseOffset = 0x31B8;
  constexpr uint32_t kPredicationPacket = 0xC0006000u;

  const uint32_t device = args.r3;
  const uint32_t predication = args.r4;
  const uint8_t old_flags = LoadGuestU8(base, device + kPredicationFlagsOffset);
  uint8_t new_flags = old_flags & 0xFEu;

  if (predication != 0) {
    StoreGuestU32(base, device + kPredicationMaskOffset, predication);
    StoreGuestU32(base, device + kPredicationEnabledOffset, 1);
  } else {
    StoreGuestU32(base, device + kPredicationEnabledOffset, 0);
    bool use_unconditional_predication = false;
    if ((old_flags & 0x0Cu) == 0 &&
        LoadGuestU8(base, device + kPrimitiveModeOffset) == 0) {
      use_unconditional_predication = (old_flags & 0x10u) != 0;
      if (!use_unconditional_predication && (old_flags & 0x20u) != 0) {
        use_unconditional_predication = true;
        for (uint32_t i = 0; i < 5u; ++i) {
          const uint32_t compare_value =
              LoadGuestU32(base, device + kPredicationCompareBaseOffset +
                                     i * sizeof(uint32_t));
          const uint32_t tracked_value =
              LoadGuestU32(base, device + kPredicationTrackedBaseOffset +
                                     i * sizeof(uint32_t));
          if (tracked_value != compare_value && compare_value != 0) {
            use_unconditional_predication = false;
            break;
          }
        }
      }
    }
    if (use_unconditional_predication) {
      new_flags |= 1u;
    }
    StoreGuestU32(base, device + kPredicationMaskOffset, UINT32_MAX);
  }

  StoreGuestU8(base, device + kPredicationFlagsOffset, new_flags);
  StoreGuestU32(base, device + kPredicationMaskRestoreOffset, 0);
  StoreGuestU32(base, device + kPredicationStateOffset, predication);
  StoreGuestU32(base, device + kPredicationStateRestoreOffset, 0);

  uint32_t command_pointer = LoadGuestU32(base, device + kCommandPointerOffset);
  if (command_pointer > LoadGuestU32(base, device + kCommandLimitOffset)) {
    RendererHookArgs kick_args = args;
    kick_args.r3 = device;
    const FableGuestCommandSubmitEmulation kick_emulation =
        EmulateFableKickOff(kick_args, base);
    command_pointer =
        kick_emulation.handled
            ? static_cast<uint32_t>(kick_emulation.result)
            : LoadGuestU32(base, device + kCommandPointerOffset);
  }
  if (!IsLikelyGuestDwordAddress(command_pointer)) {
    EmulateFableAllocateCommandSegment(device, base, 2u);
    command_pointer = LoadGuestU32(base, device + kCommandPointerOffset);
  }
  if (IsLikelyGuestDwordAddress(command_pointer)) {
    command_pointer += sizeof(uint32_t);
    StoreGuestU32(base, command_pointer, kPredicationPacket);
    command_pointer += sizeof(uint32_t);
    StoreGuestU32(base, command_pointer,
                  LoadGuestU32(base, device + kPredicationMaskOffset));
    StoreGuestU32(base, device + kCommandPointerOffset, command_pointer);
  }

  return {.handled = true, .result = command_pointer};
}

bool IsFableGuestPlainRootDrawHook(std::string_view symbol) {
  return symbol == "sub_8221C3E8" || // D3DDevice_DrawPrimitive
         symbol == "sub_8221DFC0";   // D3DDevice_DrawIndexedPrimitive
}

bool IsFableGuestPrivateTextureRectangleHook(std::string_view symbol) {
  return symbol == "sub_82206888"; // D3D::PrivateTextureRectangleDraw
}

bool IsFableGuestPrivateTextureRectangleWrapperHook(std::string_view symbol) {
  return symbol == "sub_821ECD30" || // private-rect wrapper; active surface slot
         symbol == "sub_822326E0" || // compact private-rect wrapper
         symbol == "sub_82263D28" || // private-rect wrapper; source object arg
         symbol == "sub_8227F3B0" || // batched private-rect wrapper
         symbol == "sub_822A0A78" || // private-rect wrapper; 64-bit rect arg
         symbol == "sub_822A0B08" || // private-rect wrapper; source object field
         symbol == "sub_822C40C8" || // private-rect wrapper; source object
         symbol == "sub_822C4290" || // private-rect wrapper; title/private global source
         symbol == "sub_82A6E698" || // private-rect wrapper; postprocess source field
         symbol == "sub_82A7FDE0" || // private-rect wrapper; shared source global
         symbol == "sub_82A7FF20" || // batched private-rect wrapper; shared source global
         symbol == "sub_82A82FE8" || // private-rect wrapper; render object source field
         symbol == "sub_82A6A458" || // private-rect wrapper; shared source global
         symbol == "sub_82A6A598" || // private-rect wrapper; postprocess source global
         symbol == "sub_82A6AA90" || // private-rect wrapper; postprocess source global
         symbol == "sub_8228AB70" || // setup/private-rect wrapper; global source
         symbol == "sub_8228ACB8" || // setup/flush private-rect wrapper
         symbol == "sub_82242108";   // title/private sibling wrapper
}

bool IsFableGuestBeginVerticesWrapperHook(std::string_view symbol) {
  return symbol == "sub_821EBF30" || // screen-rect wrapper; 0x834C3DB8 payload
         symbol == "sub_82242C58" ||
         symbol == "sub_82A61860" ||
         symbol == "sub_82B667F0";
}

FableGuestCommandSubmitEmulation
EmulateFablePrivateTextureRectangleSubmission(std::string_view symbol,
                                              const RendererHookArgs &args,
                                              uint8_t *base) {
  if (!IsFableGuestPrivateTextureRectangleHook(symbol) ||
      !IsLikelyFableObjectAddress(args.r3)) {
    return {};
  }

  if (base != nullptr) {
    constexpr uint32_t kCommandPointerOffset = 0x0030;
    constexpr uint32_t kWideDirtyQwordOffset = 0x0020;
    constexpr uint32_t kCurrentBackBufferOffset = 0x2A9C;
    constexpr uint32_t kPrivateRectStateOffset = 0x2A18;
    constexpr uint32_t kPrivateRectFetchStateOffset = 0x2A24;
    constexpr uint32_t kPrivateTextureRectangleDirtyBits = 0x00003C00u;
    constexpr uint32_t kPrivateDepthClearDirtyBits = 0x00000300u;
    constexpr uint32_t kPrivateColorClearDirtyBits = 0x000000C0u;
    const uint32_t flags = args.r4;
    OrFableGuestQwordLow(base, args.r3 + kWideDirtyQwordOffset,
                         kPrivateTextureRectangleDirtyBits);
    if ((flags & 0x200u) != 0) {
      OrFableGuestQwordLow(base, args.r3 + kWideDirtyQwordOffset,
                           kPrivateDepthClearDirtyBits);
    }
    if ((flags & 0x100u) != 0) {
      OrFableGuestQwordLow(base, args.r3 + kWideDirtyQwordOffset,
                           kPrivateColorClearDirtyBits);
    }

    const uint32_t current_back_buffer =
        LoadGuestU32(base, args.r3 + kCurrentBackBufferOffset);
    if (IsLikelyFableObjectAddress(args.r6)) {
      StoreGuestU32(base, args.r6 + 8u, current_back_buffer);
      if (current_back_buffer != 0) {
        StoreGuestU32(base, args.r6 + 12u, current_back_buffer);
      }
    }

    StoreGuestU32(base, args.r3 + kPrivateRectStateOffset, 0);
    StoreGuestU32(base, args.r3 + kPrivateRectFetchStateOffset, 0);

    const uint32_t command_pointer =
        LoadGuestU32(base, args.r3 + kCommandPointerOffset);
    if (IsLikelyGuestDwordAddress(command_pointer)) {
      return {.handled = true, .result = command_pointer};
    }
  }

  // The observer records this as a typed private-texture rectangle command on
  // skipped hook exit. Native execution only emits the PM4 wrapper we replace.
  return {.handled = true, .result = 0};
}

void EmulatePlumeOwnedFablePixCaptureControlBookkeeping(uint32_t device,
                                                        uint8_t *base);

FableGuestCommandSubmitEmulation EmulateFablePixCaptureVideoFrame(
    std::string_view symbol, const RendererHookArgs &args, uint8_t *base) {
  if (symbol != "sub_82BA95E0" || base == nullptr ||
      !IsLikelyFableObjectAddress(args.r3)) {
    return {};
  }

  constexpr uint32_t kPixCaptureFormatOffset = 0x54B0;
  constexpr uint32_t kPixCaptureWidthOffset = 0x54B4;
  constexpr uint32_t kPixCaptureHeightOffset = 0x54B8;
  constexpr uint32_t kPixCaptureFrameOffset = 0x54BC;
  constexpr uint32_t kPixCaptureScalerOffset = 0x54C0;
  constexpr uint32_t kPixCaptureGammaOffset = 0x54C4;

  StoreGuestU32(base, args.r3 + kPixCaptureFormatOffset, args.r7);
  StoreGuestU32(base, args.r3 + kPixCaptureWidthOffset, args.r8);
  StoreGuestU32(base, args.r3 + kPixCaptureHeightOffset, args.r9);
  StoreGuestU32(base, args.r3 + kPixCaptureFrameOffset, args.r6);
  StoreGuestU32(base, args.r3 + kPixCaptureScalerOffset, args.r10);
  StoreGuestU32(base, args.r3 + kPixCaptureGammaOffset,
                LoadHookStackU32(base, args, 84u));

  if (!g_logged_fable_pix_capture_emulation) {
    REXLOG_INFO(
        "Refii plume is emulating Fable PixCaptureVideoFrame bookkeeping "
        "device=0x{:08X} frame=0x{:08X} size={}x{} fmt=0x{:08X}",
        args.r3, args.r6, args.r8, args.r9, args.r7);
    g_logged_fable_pix_capture_emulation = true;
  }

  // Native PixCapture queues GPU copy work into the old D3D command stream.
  // Plume owns that stream now; keep the guest-visible capture fields above and
  // let Plume present from its tracked render targets instead of a stale native
  // capture buffer.
  return {.handled = true, .result = args.r3};
}

FableGuestCommandSubmitEmulation EmulateFableDisplayIntervalSync(
    std::string_view symbol, const RendererHookArgs &args, uint8_t *base) {
  if (symbol != "sub_82BA2F68" || base == nullptr ||
      !IsLikelyFableObjectAddress(args.r3)) {
    return {};
  }

  constexpr uint32_t kDisplayFlagsOffset = 0x2ABD;
  const uint32_t device = args.r3;
  StoreGuestU8(base, device + kDisplayFlagsOffset,
               static_cast<uint8_t>(
                   LoadGuestU8(base, device + kDisplayFlagsOffset) | 0x10u));
  EmulatePlumeOwnedFablePixCaptureControlBookkeeping(device, base);

  RendererHookArgs kick_args = args;
  kick_args.r3 = device;
  const FableGuestCommandSubmitEmulation kick_emulation =
      EmulateFableKickOff(kick_args, base);

  if (!g_logged_fable_display_interval_emulation) {
    REXLOG_INFO(
        "Refii plume is emulating Fable display interval bookkeeping "
        "device=0x{:08X} mode=0x{:08X}",
        device, args.r4);
    g_logged_fable_display_interval_emulation = true;
  }

  return {.handled = true,
          .result = kick_emulation.handled ? kick_emulation.result : device};
}

FableGuestCommandSubmitEmulation EmulateFableComplexSwap(
    std::string_view symbol, const RendererHookArgs &args, uint8_t *base) {
  if (symbol != "sub_82BA32F8" || base == nullptr ||
      !IsLikelyFableObjectAddress(args.r3)) {
    return {};
  }

  constexpr uint32_t kDisplayControlOffset = 0x2ABC;
  constexpr uint32_t kDisplayFlagsOffset = 0x2ABD;
  constexpr uint32_t kDisplayScalerDirtyOffset = 0x2ABE;
  constexpr uint32_t kDisplayInlineFlagsOffset = 0x2ABF;
  constexpr uint32_t kPersistModeOffset = 0x31AC;
  constexpr uint32_t kPendingDisplayModeOffset = 0x5DAC;

  const uint32_t device = args.r3;
  const uint8_t flags = static_cast<uint8_t>(args.r4);

  if ((LoadGuestU32(base, device + kPendingDisplayModeOffset) & 4u) != 0) {
    RendererHookArgs interval_args = args;
    interval_args.r3 = device;
    interval_args.r4 = 0;
    interval_args.r4_u64 = 0;
    EmulateFableDisplayIntervalSync("sub_82BA2F68", interval_args, base);
  }

  if ((flags & 4u) != 0) {
    StoreGuestU8(base, device + kDisplayScalerDirtyOffset,
                 static_cast<uint8_t>(
                     LoadGuestU8(base, device + kDisplayScalerDirtyOffset) |
                     0x10u));
  }
  if ((flags & 2u) != 0) {
    StoreGuestU32(base, device + kPersistModeOffset, args.r10);
  }

  StoreGuestU8(base, device + kDisplayControlOffset,
               static_cast<uint8_t>(
                   LoadGuestU8(base, device + kDisplayControlOffset) | 1u));
  StoreGuestU8(base, device + kDisplayFlagsOffset,
               static_cast<uint8_t>(
                   LoadGuestU8(base, device + kDisplayFlagsOffset) | 0x10u));
  EmulatePlumeOwnedFablePixCaptureControlBookkeeping(device, base);
  StoreGuestU8(base, device + kDisplayInlineFlagsOffset,
               static_cast<uint8_t>(
                   LoadGuestU8(base, device + kDisplayInlineFlagsOffset) |
                   0x40u));

  RendererHookArgs kick_args = args;
  kick_args.r3 = device;
  const FableGuestCommandSubmitEmulation kick_emulation =
      EmulateFableKickOff(kick_args, base);

  if (!g_logged_fable_complex_swap_emulation) {
    REXLOG_INFO(
        "Refii plume is emulating Fable ComplexSwap display bookkeeping "
        "device=0x{:08X} flags=0x{:02X} scanout=0x{:08X} fmt=0x{:08X} "
        "size={}x{}",
        device, flags, args.r5, args.r6, args.r7, args.r8);
    g_logged_fable_complex_swap_emulation = true;
  }

  return {.handled = true,
          .result = kick_emulation.handled ? kick_emulation.result : device};
}

// D3DQuery_Issue(D3DQuery *pThis /*r3*/, DWORD IssueFlags /*r4*/) -- Lumen-confirmed
// (0x822194B8). Formerly misnamed "Fable::SceneBatchCommandToggle": it is a D3D
// QUERY issue, not a scene-batch toggle. The emulation below is behaviourally
// faithful and was NOT changed -- only the name was wrong. Field map matches the
// decompile exactly: pThis+0x00=device, +0x04=query type (9=OCCLUSION ->
// D3D::IssueSampleCountQuery, 10=EVENT), +0x94=present index (pThis+37 dwords);
// IssueFlags&2=D3DISSUE_BEGIN, &1=D3DISSUE_END.
// NOTE: still registered via REFII_RENDERER_DRAW_HOOK -- a query is not a draw.
// Left as-is deliberately (changing the hook class is a behaviour change, not a
// rename); revisit if the draw-class registration ever matters.
FableGuestCommandSubmitEmulation EmulateD3DQueryIssue(
    std::string_view symbol, const RendererHookArgs &args, uint8_t *base) {
  if (symbol != "sub_822194B8" || base == nullptr ||
      !IsLikelyFableObjectRange(args.r3, 0x98u)) {
    return {};
  }

  constexpr uint32_t kRecordDeviceOffset = 0x00;
  constexpr uint32_t kRecordKindOffset = 0x04;
  constexpr uint32_t kRecordStateOffset = 0x10;
  constexpr uint32_t kRecordFlagsOffset = 0x14;
  constexpr uint32_t kRecordFenceOffset = 0x18;
  constexpr uint32_t kRecordPresentIndexOffset = 0x94;
  constexpr uint32_t kDevicePrimaryDirtyQwordOffset = 0x10;
  constexpr uint32_t kDeviceCommandCursorOffset = 0x30;
  constexpr uint32_t kDeviceDisplayFlagsOffset = 0x2ABC;
  constexpr uint32_t kDeviceInlineFlagsOffset = 0x2ABF;
  constexpr uint32_t kDeviceFenceOffset = 0x2A9C;
  constexpr uint32_t kDevicePresentIndexOffset = 0x31CC;
  constexpr uint32_t kSceneBatchDirtyBits = 0x03000008u;
  constexpr uint32_t kPoisonPatchValue = 0xFFFFFEEDu;

  const uint32_t record = args.r3;
  const uint32_t device = LoadGuestU32(base, record + kRecordDeviceOffset);
  if (!IsLikelyFableObjectAddress(device)) {
    return {.handled = true, .result = record};
  }

  const uint32_t kind = LoadGuestU32(base, record + kRecordKindOffset);
  const uint8_t mode = static_cast<uint8_t>(args.r4);
  if ((mode & 2u) != 0) {
    const uint32_t present_index =
        (LoadGuestU8(base, device + kDeviceDisplayFlagsOffset) & 0x20u) != 0
            ? LoadGuestU32(base, device + kDevicePresentIndexOffset)
            : 1u;
    StoreGuestU32(base, record + kRecordPresentIndexOffset, present_index);
    if (kind == 9u) {
      OrFableGuestQwordLow(base, device + kDevicePrimaryDirtyQwordOffset,
                           kSceneBatchDirtyBits);
    } else if (kind == 10u) {
      StoreGuestU8(base, device + kDeviceInlineFlagsOffset,
                   LoadGuestU8(base, device + kDeviceInlineFlagsOffset) | 0x80u);
      StoreGuestU32(base, device + kDeviceCommandCursorOffset,
                    LoadGuestU32(base, device + kDeviceCommandCursorOffset));
    }
    StoreGuestU8(base, record + kRecordStateOffset, 2u);
  } else if ((mode & 1u) != 0) {
    if (kind == 9u) {
      const uint32_t present_index =
          LoadGuestU32(base, record + kRecordPresentIndexOffset);
      const uint32_t patch_record =
          present_index <= 31u
              ? LoadGuestU32(base,
                             record + (present_index + 6u) * sizeof(uint32_t))
              : 0u;
      const bool already_submitted =
          LoadGuestU32(base, record + kRecordFenceOffset) != 0 &&
          (LoadGuestU8(base, record + kRecordFlagsOffset) & 0x80u) == 0;
      if (!already_submitted &&
          patch_record <= UINT32_MAX - 20u &&
          IsLikelyGuestDwordAddress(patch_record + 20u)) {
        StoreGuestU8(base, record + kRecordFlagsOffset,
                     LoadGuestU8(base, record + kRecordFlagsOffset) & 0x7Fu);
        StoreGuestU32(base, patch_record + 16u, kPoisonPatchValue);
        StoreGuestU32(base, patch_record + 20u, kPoisonPatchValue);
      }
      OrFableGuestQwordLow(base, device + kDevicePrimaryDirtyQwordOffset,
                           kSceneBatchDirtyBits);
    } else if (kind == 10u) {
      StoreGuestU8(base, device + kDeviceInlineFlagsOffset,
                   LoadGuestU8(base, device + kDeviceInlineFlagsOffset) & 0x7Fu);
      StoreGuestU32(base, device + kDeviceCommandCursorOffset,
                    LoadGuestU32(base, device + kDeviceCommandCursorOffset));
    }
    StoreGuestU8(base, record + kRecordStateOffset, 1u);
    StoreGuestU32(base, record + kRecordFenceOffset,
                  LoadGuestU32(base, device + kDeviceFenceOffset));
    StoreGuestU8(base, record + kRecordFlagsOffset,
                 LoadGuestU8(base, record + kRecordFlagsOffset) | 0x40u);
  }

  if (!g_logged_fable_scene_batch_toggle_emulation) {
    REXLOG_INFO(
        "Refii plume is emulating Fable scene batch command toggle "
        "record=0x{:08X} device=0x{:08X}",
        record, device);
    g_logged_fable_scene_batch_toggle_emulation = true;
  }

  return {.handled = true, .result = record};
}

FableGuestCommandSubmitEmulation EmulateFableSceneBatchDrawPrimitiveBridge(
    std::string_view symbol, const RendererHookArgs &args, uint8_t *base) {
  if (symbol != "sub_82219740" || base == nullptr ||
      !IsLikelyFableObjectRange(args.r3, 60u) ||
      args.r4 > UINT32_MAX - 32u ||
      (args.r4 & (alignof(uint32_t) - 1u)) != 0 ||
      !IsLikelyGuestMemoryAddress(args.r4) ||
      !IsLikelyGuestMemoryAddress(args.r4 + 31u) ||
      args.r5 > UINT32_MAX - 4u ||
      !IsLikelyGuestDwordAddress(args.r5 + 4u)) {
    return {};
  }

  constexpr uint32_t kSubmitManagerOffset = 0x08;
  constexpr uint32_t kSubmitDeviceOffset = 0x38;
  constexpr uint32_t kManagerStreamBaseOffset = 0x24;
  constexpr uint32_t kManagerEntryCountOffset = 0x28;
  constexpr uint32_t kManagerByteCursorOffset = 0x2C;
  constexpr uint32_t kMaxSceneBatchEntries = 0x7D0u;
  constexpr uint32_t kCopiedSceneBatchBytes = 32u;
  constexpr uint32_t kSceneBatchByteStride = 0x30u;

  const uint32_t manager = LoadGuestU32(base, args.r3 + kSubmitManagerOffset);
  const uint32_t device = LoadGuestU32(base, args.r3 + kSubmitDeviceOffset);
  if (!IsLikelyFableObjectRange(manager, 48u) ||
      !IsLikelyFableObjectAddress(device)) {
    return {.handled = true, .result = 0};
  }

  const uint32_t entry_count =
      LoadGuestU32(base, manager + kManagerEntryCountOffset);
  const uint32_t old_cursor =
      LoadGuestU32(base, manager + kManagerByteCursorOffset);
  if (entry_count >= kMaxSceneBatchEntries) {
    return {.handled = true, .result = 0};
  }

  const uint32_t stream_base =
      LoadGuestU32(base, manager + kManagerStreamBaseOffset);
  const uint64_t stream_write64 =
      uint64_t(stream_base) + uint64_t(entry_count) * kCopiedSceneBatchBytes;
  const uint32_t stream_write =
      stream_write64 <= UINT32_MAX ? static_cast<uint32_t>(stream_write64) : 0u;
  if (stream_write != 0 &&
      stream_write <= UINT32_MAX - kCopiedSceneBatchBytes &&
      IsLikelyGuestMemoryAddress(stream_write) &&
      IsLikelyGuestMemoryAddress(stream_write + kCopiedSceneBatchBytes - 1u)) {
    const uint64_t first_qword = LoadGuestU64(base, args.r4);
    const uint64_t second_qword = LoadGuestU64(base, args.r4 + 8u);
    StoreGuestU32(base, stream_write + 0u, stream_write);
    StoreGuestU32(base, stream_write + 4u,
                  static_cast<uint32_t>(first_qword));
    StoreGuestU64(base, stream_write + 8u, second_qword);

    const uint32_t nested_record =
        static_cast<uint32_t>(second_qword >> 32u);
    if (IsLikelyGuestMemoryAddress(nested_record + 31u)) {
      StoreGuestU64(base, stream_write + 16u,
                    LoadGuestU64(base, nested_record + 16u));
      StoreGuestU64(base, stream_write + 24u,
                    LoadGuestU64(base, nested_record + 24u));
    } else {
      StoreGuestU64(base, stream_write + 16u,
                    LoadGuestU64(base, args.r4 + 16u));
      StoreGuestU64(base, stream_write + 24u,
                    LoadGuestU64(base, args.r4 + 24u));
    }
  }

  StoreGuestU32(base, manager + kManagerEntryCountOffset, entry_count + 1u);
  StoreGuestU32(base, manager + kManagerByteCursorOffset,
                old_cursor + kSceneBatchByteStride);

  const uint32_t batch_record = LoadGuestU32(base, args.r5 + 4u);
  if (batch_record != 0) {
    RendererHookArgs toggle_args = args;
    toggle_args.r3 = batch_record;
    toggle_args.r4 = 2u;
    toggle_args.r4_u64 = 2u;
    EmulateD3DQueryIssue("sub_822194B8", toggle_args, base);
    toggle_args.r4 = 1u;
    toggle_args.r4_u64 = 1u;
    EmulateD3DQueryIssue("sub_822194B8", toggle_args, base);
  }

  return {.handled = true, .result = 1};
}

FableGuestCommandSubmitEmulation EmulateFableDrawVerticesUpSubmission(
    std::string_view symbol, const RendererHookArgs &args, uint8_t *base) {
  if (symbol != "sub_82265CD8" || base == nullptr ||
      !IsLikelyFableObjectAddress(args.r3) || args.r5 == 0 || args.r7 == 0 ||
      !IsLikelyGuestMemoryAddress(args.r6)) {
    return {};
  }

  constexpr uint32_t kCommandPointerOffset = 0x0030;
  constexpr uint32_t kCommandEndOffset = 0x0034;
  constexpr uint32_t kCommandLimitOffset = 0x0038;
  constexpr uint32_t kDirtyQwordOffset = 0x0010;
  constexpr uint32_t kFetchDirtyQwordOffset = 0x0018;
  constexpr uint32_t kStreamStrideWordsOffset = 0x2E38;
  constexpr uint32_t kPrivateInlineStateOffset = 0x30F0;
  constexpr uint32_t kInlineStreamValidOffset = 0x3198;
  constexpr uint32_t kPrimitiveModeOffset = 0x2F9B;
  constexpr uint32_t kPrimitiveModeBaseOffset = 0x2F5A;
  constexpr uint32_t kConditionalPacketEnableOffset = 0x2ABC;
  constexpr uint32_t kScratchTailOffset = 0x3484;
  constexpr uint32_t kScratchVertexBufferOffset = 0x3488;
  constexpr uint32_t kScratchIndexBufferOffset = 0x348C;
  constexpr uint32_t kScratchVertexWordsOffset = 0x3490;
  constexpr uint32_t kScratchIndexWordsOffset = 0x3494;

  const uint32_t device = args.r3;
  const uint64_t byte_count64 = uint64_t(args.r5) * uint64_t(args.r7);
  if (byte_count64 == 0 ||
      byte_count64 > uint64_t(std::numeric_limits<uint32_t>::max())) {
    return {.handled = true, .result = 0};
  }

  const uint32_t byte_count = static_cast<uint32_t>(byte_count64);
  const uint32_t word_count = (byte_count + 3u) >> 2u;
  const uint8_t inline_stride_words = static_cast<uint8_t>(args.r7 >> 2u);
  const uint8_t saved_inline_stride =
      LoadGuestU8(base, device + kPrivateInlineStateOffset);
  StoreGuestU8(base, device + kPrivateInlineStateOffset, inline_stride_words);
  if (inline_stride_words !=
          LoadGuestU8(base, device + kStreamStrideWordsOffset) &&
      LoadGuestU32(base, device + kInlineStreamValidOffset) != 0) {
    OrFableGuestQwordLow(base, device + kDirtyQwordOffset, 0x00080000u);
  }
  StoreGuestU64(base, device + kFetchDirtyQwordOffset,
                LoadGuestU64(base, device + kFetchDirtyQwordOffset) & ~1ull);

  const uint32_t vertex_buffer =
      EmulateFableAllocateAuxiliaryCommandWords(device, base, word_count, 16u);
  StoreGuestU8(base, device + kPrivateInlineStateOffset, saved_inline_stride);
  if (vertex_buffer == 0) {
    StoreGuestU32(base, device + kCommandPointerOffset,
                  LoadGuestU32(base, device + kCommandPointerOffset));
    return {.handled = true, .result = 0};
  }

  if (!MoveGuestBytes(base, vertex_buffer, args.r6, byte_count)) {
    return {.handled = true, .result = 0};
  }
  const uint32_t padded_bytes = word_count * sizeof(uint32_t);
  if (padded_bytes > byte_count && byte_count <= UINT32_MAX - vertex_buffer) {
    SetGuestBytes(base, vertex_buffer + byte_count, 0,
                  padded_bytes - byte_count);
  }

  uint32_t command_pointer = LoadGuestU32(base, device + kCommandPointerOffset);
  const uint32_t command_limit = LoadGuestU32(base, device + kCommandLimitOffset);
  constexpr uint32_t kSimplePacketWords = 16u;
  constexpr uint32_t kConditionalPacketWords = 25u;
  const bool conditional_packet =
      (LoadGuestU8(base, device + kConditionalPacketEnableOffset) & 1u) != 0;
  const uint32_t packet_words =
      conditional_packet ? kConditionalPacketWords : kSimplePacketWords;
  if (!IsLikelyGuestDwordAddress(command_pointer) ||
      command_pointer + packet_words * sizeof(uint32_t) >= command_limit) {
    EmulateFableAllocateCommandSegment(device, base, packet_words);
    command_pointer = LoadGuestU32(base, device + kCommandPointerOffset);
  }

  uint32_t packet_tail = command_pointer;
  if (IsLikelyGuestDwordAddress(command_pointer) &&
      command_pointer + packet_words * sizeof(uint32_t) <=
          LoadGuestU32(base, device + kCommandEndOffset)) {
    auto write_packet_word = [&](uint32_t value) {
      packet_tail += sizeof(uint32_t);
      StoreGuestU32(base, packet_tail, value);
    };

    const uint8_t primitive_mode =
        LoadGuestU8(base, device + kPrimitiveModeOffset);
    const uint32_t primitive_state =
        ((primitive_mode != 0) ? 0x100u : 0u) |
        (LoadGuestU8(base, device + kPrimitiveModeBaseOffset + primitive_mode) &
         0x3Fu);
    const uint32_t draw_packet =
        (args.r4 & 0x3Fu) | ((args.r5 & 0xFFFFu) << 16u) | 0x80u;

    write_packet_word(0x00002102u);
    write_packet_word(0);
    write_packet_word(0x000548BAu);
    write_packet_word(1);
    write_packet_word(0);
    write_packet_word(1);
    write_packet_word(0);
    write_packet_word(FableGpuAddress(vertex_buffer) | 3u);
    write_packet_word((byte_count & 0x03FFFFFCu) | 0x10000002u);
    write_packet_word(0x00025000u);
    write_packet_word(0);
    write_packet_word(0);
    write_packet_word(0);
    if (conditional_packet) {
      write_packet_word(0xC0006000u);
      write_packet_word(0xFFFFFFFFu);
      write_packet_word(0xC0012201u);
      write_packet_word(primitive_state);
      write_packet_word(draw_packet);
      write_packet_word(0xC0006000u);
      write_packet_word(0x80000000u);
      write_packet_word(0xC0015A01u);
      write_packet_word(26);
      write_packet_word(0);
      write_packet_word(0xC00145C1u);
      write_packet_word(25);
    } else {
      write_packet_word(0xC0012201u);
      write_packet_word(primitive_state);
      write_packet_word(draw_packet);
    }
  }

  StoreGuestU32(base, device + kScratchTailOffset, packet_tail);
  StoreGuestU32(base, device + kCommandPointerOffset, packet_tail);
  StoreGuestU32(base, device + kScratchVertexBufferOffset, vertex_buffer);
  StoreGuestU32(base, device + kScratchIndexBufferOffset, 0);
  StoreGuestU32(base, device + kScratchVertexWordsOffset, word_count);
  StoreGuestU32(base, device + kScratchIndexWordsOffset, 0);
  StoreGuestU64(base, device + kFetchDirtyQwordOffset,
                LoadGuestU64(base, device + kFetchDirtyQwordOffset) | 1ull);

  if (!g_logged_fable_draw_vertices_up_emulation) {
    REXLOG_INFO(
        "Refii plume is emulating Fable DrawVerticesUP scratch copy "
        "device=0x{:08X}",
        device);
    g_logged_fable_draw_vertices_up_emulation = true;
  }

  return {.handled = true, .result = vertex_buffer};
}

FableGuestCommandSubmitEmulation EmulateFableBeginIndexedVerticesSubmission(
    std::string_view symbol, const RendererHookArgs &args, uint8_t *base) {
  if (symbol != "sub_82217DB8" || base == nullptr ||
      !IsLikelyFableObjectAddress(args.r3)) {
    return {};
  }

  constexpr uint32_t kScratchTailOffset = 0x3484;
  constexpr uint32_t kScratchVertexBufferOffset = 0x3488;
  constexpr uint32_t kScratchIndexBufferOffset = 0x348C;
  constexpr uint32_t kScratchVertexWordsOffset = 0x3490;
  constexpr uint32_t kScratchIndexWordsOffset = 0x3494;
  constexpr uint32_t kCommandPointerOffset = 0x0030;
  constexpr uint32_t kCommandEndOffset = 0x0034;
  constexpr uint32_t kCommandLimitOffset = 0x0038;
  constexpr uint32_t kFetchDirtyQwordOffset = 0x0018;
  constexpr uint32_t kPrimitiveModeOffset = 0x2F9B;
  constexpr uint32_t kPrimitiveModeBaseOffset = 0x2F5A;
  constexpr uint32_t kConditionalPacketEnableOffset = 0x2ABC;
  constexpr uint64_t kOutOfMemory = 0x8007000Eull;

  const uint32_t device = args.r3;
  const uint32_t vertex_out_slot_pointer =
      IsLikelyGuestDwordAddress(args.r1 + 84u)
          ? LoadGuestU32(base, args.r1 + 84u)
          : 0;
  const bool has_vertex_out_slot =
      IsLikelyGuestDwordAddress(vertex_out_slot_pointer);
  const bool has_index_out_slot = IsLikelyGuestDwordAddress(args.r10);

  const uint64_t vertex_bytes =
      uint64_t(args.r6) * uint64_t(std::max<uint32_t>(args.r9, 1u));
  const uint32_t vertex_words =
      vertex_bytes <= uint64_t(std::numeric_limits<uint32_t>::max())
          ? static_cast<uint32_t>((vertex_bytes + 3u) >> 2u)
          : 0u;
  const uint32_t index_words =
      (args.r8 & 4u) != 0 ? args.r7 : ((args.r7 + 1u) >> 1u);

  uint32_t vertex_buffer = 0;
  uint32_t index_buffer = 0;
  const uint64_t total_scratch_bytes =
      uint64_t(vertex_words) * sizeof(uint32_t) + 15u +
      uint64_t(index_words) * sizeof(uint32_t);
  const uint32_t total_scratch_words =
      total_scratch_bytes <= uint64_t(std::numeric_limits<uint32_t>::max())
          ? static_cast<uint32_t>((total_scratch_bytes + 3u) >> 2u)
          : 0u;
  const uint32_t scratch_buffer =
      total_scratch_words != 0
          ? EmulateFableAllocateAuxiliaryCommandWords(device, base,
                                                      total_scratch_words, 16u)
          : 0u;
  if (scratch_buffer != 0) {
    vertex_buffer = scratch_buffer;
    index_buffer =
        AlignGuestU32(vertex_buffer + vertex_words * sizeof(uint32_t), 16u);
  }

  FableGuestCommandSubmitEmulation emulation;
  emulation.handled = true;
  if (scratch_buffer == 0 || (vertex_words != 0 && vertex_buffer == 0) ||
      (index_words != 0 && index_buffer == 0) || !has_vertex_out_slot ||
      !has_index_out_slot) {
    if (has_vertex_out_slot) {
      StoreGuestU32(base, vertex_out_slot_pointer, 0);
    }
    if (has_index_out_slot) {
      StoreGuestU32(base, args.r10, 0);
    }
    emulation.result = kOutOfMemory;
    return emulation;
  }

  if (vertex_buffer != 0) {
    SetGuestBytes(base, vertex_buffer, 0, vertex_words * sizeof(uint32_t));
  }
  if (index_buffer != 0) {
    SetGuestBytes(base, index_buffer, 0, index_words * sizeof(uint32_t));
  }

  StoreGuestU32(base, vertex_out_slot_pointer, vertex_buffer);
  StoreGuestU32(base, args.r10, index_buffer);

  uint32_t command_pointer = LoadGuestU32(base, device + kCommandPointerOffset);
  const uint32_t command_limit = LoadGuestU32(base, device + kCommandLimitOffset);
  constexpr uint32_t kSimplePacketWords = 18u;
  constexpr uint32_t kConditionalPacketWords = 25u;
  const bool conditional_packet =
      (LoadGuestU8(base, device + kConditionalPacketEnableOffset) & 1u) != 0;
  const uint32_t packet_words =
      conditional_packet ? kConditionalPacketWords : kSimplePacketWords;
  if (!IsLikelyGuestDwordAddress(command_pointer) ||
      command_pointer + packet_words * sizeof(uint32_t) >= command_limit) {
    EmulateFableAllocateCommandSegment(device, base, packet_words);
    command_pointer = LoadGuestU32(base, device + kCommandPointerOffset);
  }

  uint32_t packet_tail = command_pointer;
  if (IsLikelyGuestDwordAddress(command_pointer) &&
      command_pointer + packet_words * sizeof(uint32_t) <=
          LoadGuestU32(base, device + kCommandEndOffset)) {
    auto write_packet_word = [&](uint32_t value) {
      packet_tail += sizeof(uint32_t);
      StoreGuestU32(base, packet_tail, value);
    };

    const uint8_t primitive_mode =
        LoadGuestU8(base, device + kPrimitiveModeOffset);
    const uint32_t primitive_state =
        ((primitive_mode != 0) ? 0x100u : 0u) |
        (LoadGuestU8(base, device + kPrimitiveModeBaseOffset + primitive_mode) &
         0x3Fu);
    uint32_t draw_packet =
        (args.r4 & 0x3Fu) | ((args.r7 & 0xFFFFu) << 16u);
    uint32_t index_count_packet =
        (args.r7 & 0x00FFFFFFu) | ((args.r8 & 0x3u) << 30u);
    if ((args.r8 & 4u) != 0) {
      draw_packet |= 0x800u;
      index_count_packet =
          ((args.r7 * 2u) & 0x00FFFFFEu) | ((args.r8 & 0x3u) << 30u);
    }

    write_packet_word(0x00002102u);
    write_packet_word(args.r5);
    write_packet_word(0x000548BAu);
    write_packet_word(0);
    write_packet_word(0);
    write_packet_word(0);
    write_packet_word(0);
    write_packet_word(FableGpuAddress(vertex_buffer) | 3u);
    write_packet_word((vertex_words * sizeof(uint32_t)) & 0x03FFFFFCu |
                      0x10000002u);
    write_packet_word(0x00025000u);
    write_packet_word(0);
    write_packet_word(0);
    write_packet_word(0);
    if (conditional_packet) {
      write_packet_word(0xC0006000u);
      write_packet_word(0xFFFFFFFFu);
      write_packet_word(0xC0032201u);
      write_packet_word(primitive_state);
      write_packet_word(draw_packet);
      write_packet_word(FableGpuAddress(index_buffer));
      write_packet_word(index_count_packet);
      write_packet_word(0xC0006000u);
      write_packet_word(0x80000000u);
      write_packet_word(0xC0015A01u);
      write_packet_word(26);
      write_packet_word(FableGpuAddress(packet_tail + sizeof(uint32_t)) | 1u);
      write_packet_word(0xC0004601u);
      write_packet_word(25);
    } else {
      write_packet_word(0xC0032201u);
      write_packet_word(primitive_state);
      write_packet_word(draw_packet);
      write_packet_word(FableGpuAddress(index_buffer));
      write_packet_word(index_count_packet);
    }
  }

  StoreGuestU32(base, device + kScratchTailOffset, packet_tail);
  StoreGuestU32(base, device + kCommandPointerOffset, packet_tail);
  StoreGuestU32(base, device + kScratchVertexBufferOffset, vertex_buffer);
  StoreGuestU32(base, device + kScratchIndexBufferOffset, index_buffer);
  StoreGuestU32(base, device + kScratchVertexWordsOffset, vertex_words);
  StoreGuestU32(base, device + kScratchIndexWordsOffset, index_words);
  StoreGuestU64(base, device + kFetchDirtyQwordOffset,
                LoadGuestU64(base, device + kFetchDirtyQwordOffset) | 1ull);

  if (!g_logged_fable_begin_indexed_vertices_emulation) {
    REXLOG_INFO(
        "Refii plume is emulating Fable BeginIndexedVertices scratch "
        "allocation device=0x{:08X}",
        device);
    g_logged_fable_begin_indexed_vertices_emulation = true;
  }

  emulation.result = 0;
  return emulation;
}

FableGuestCommandSubmitEmulation EmulateFableBeginVerticesSubmission(
    std::string_view symbol, const RendererHookArgs &args, uint8_t *base) {
  if (symbol != "sub_82205F68" || base == nullptr ||
      !IsLikelyFableObjectAddress(args.r3)) {
    return {};
  }

  constexpr uint32_t kCommandPointerOffset = 0x0030;
  constexpr uint32_t kCommandEndOffset = 0x0034;
  constexpr uint32_t kCommandLimitOffset = 0x0038;
  constexpr uint32_t kDirtyQwordOffset = 0x0010;
  constexpr uint32_t kFetchDirtyQwordOffset = 0x0018;
  constexpr uint32_t kStreamStrideWordsOffset = 0x2E38;
  constexpr uint32_t kPrivateInlineStateOffset = 0x30F0;
  constexpr uint32_t kInlineStreamValidOffset = 0x3198;
  constexpr uint32_t kPrimitiveModeOffset = 0x2F9B;
  constexpr uint32_t kPrimitiveModeBaseOffset = 0x2F5A;
  constexpr uint32_t kConditionalPacketEnableOffset = 0x2ABC;
  constexpr uint32_t kScratchTailOffset = 0x3484;
  constexpr uint32_t kScratchVertexBufferOffset = 0x3488;
  constexpr uint32_t kScratchIndexBufferOffset = 0x348C;
  constexpr uint32_t kScratchVertexWordsOffset = 0x3490;
  constexpr uint32_t kScratchIndexWordsOffset = 0x3494;

  const uint32_t device = args.r3;
  const uint64_t vertex_bytes =
      uint64_t(args.r5) * uint64_t(std::max<uint32_t>(args.r6, 1u));
  const uint32_t vertex_words =
      vertex_bytes <= uint64_t(std::numeric_limits<uint32_t>::max())
          ? static_cast<uint32_t>((vertex_bytes + 3u) >> 2u)
          : 0u;

  const uint8_t inline_stride_words = static_cast<uint8_t>(args.r6 >> 2u);
  const uint8_t saved_inline_stride =
      LoadGuestU8(base, device + kPrivateInlineStateOffset);
  StoreGuestU8(base, device + kPrivateInlineStateOffset, inline_stride_words);
  if (inline_stride_words !=
          LoadGuestU8(base, device + kStreamStrideWordsOffset) &&
      LoadGuestU32(base, device + kInlineStreamValidOffset) != 0) {
    OrFableGuestQwordLow(base, device + kDirtyQwordOffset, 0x00080000u);
  }
  StoreGuestU64(base, device + kFetchDirtyQwordOffset,
                LoadGuestU64(base, device + kFetchDirtyQwordOffset) & ~1ull);

  const uint32_t vertex_buffer =
      vertex_words != 0
          ? EmulateFableAllocateAuxiliaryCommandWords(device, base,
                                                      vertex_words, 16u)
          : 0u;
  StoreGuestU8(base, device + kPrivateInlineStateOffset, saved_inline_stride);

  FableGuestCommandSubmitEmulation emulation;
  emulation.handled = true;
  emulation.result = vertex_buffer;
  if (vertex_buffer == 0) {
    StoreGuestU32(base, device + kScratchVertexBufferOffset, 0);
    StoreGuestU32(base, device + kScratchIndexBufferOffset, 0);
    StoreGuestU32(base, device + kScratchVertexWordsOffset, 0);
    StoreGuestU32(base, device + kScratchIndexWordsOffset, 0);
    return emulation;
  }

  SetGuestBytes(base, vertex_buffer, 0, vertex_words * sizeof(uint32_t));

  uint32_t command_pointer = LoadGuestU32(base, device + kCommandPointerOffset);
  const uint32_t command_limit = LoadGuestU32(base, device + kCommandLimitOffset);
  constexpr uint32_t kSimplePacketWords = 16u;
  constexpr uint32_t kConditionalPacketWords = 25u;
  const bool conditional_packet =
      (LoadGuestU8(base, device + kConditionalPacketEnableOffset) & 1u) != 0;
  const uint32_t packet_words =
      conditional_packet ? kConditionalPacketWords : kSimplePacketWords;
  if (!IsLikelyGuestDwordAddress(command_pointer) ||
      command_pointer + packet_words * sizeof(uint32_t) >= command_limit) {
    EmulateFableAllocateCommandSegment(device, base, packet_words);
    command_pointer = LoadGuestU32(base, device + kCommandPointerOffset);
  }

  uint32_t packet_tail = command_pointer;
  if (IsLikelyGuestDwordAddress(command_pointer) &&
      command_pointer + packet_words * sizeof(uint32_t) <=
          LoadGuestU32(base, device + kCommandEndOffset)) {
    auto write_packet_word = [&](uint32_t value) {
      packet_tail += sizeof(uint32_t);
      StoreGuestU32(base, packet_tail, value);
    };

    const uint8_t primitive_mode =
        LoadGuestU8(base, device + kPrimitiveModeOffset);
    const uint32_t primitive_state =
        ((primitive_mode != 0) ? 0x100u : 0u) |
        (LoadGuestU8(base, device + kPrimitiveModeBaseOffset + primitive_mode) &
         0x3Fu);
    const uint32_t draw_packet =
        (args.r4 & 0x3Fu) | ((args.r5 & 0xFFFFu) << 16u) | 0x80u;

    write_packet_word(0x00002102u);
    write_packet_word(0);
    write_packet_word(0x000548BAu);
    write_packet_word(1);
    write_packet_word(0);
    write_packet_word(1);
    write_packet_word(0);
    write_packet_word(FableGpuAddress(vertex_buffer) | 3u);
    write_packet_word((vertex_words * sizeof(uint32_t)) & 0x03FFFFFCu |
                      0x10000002u);
    write_packet_word(0x00025000u);
    write_packet_word(0);
    write_packet_word(0);
    write_packet_word(0);
    if (conditional_packet) {
      write_packet_word(0xC0006000u);
      write_packet_word(0xFFFFFFFFu);
      write_packet_word(0xC0012201u);
      write_packet_word(primitive_state);
      write_packet_word(draw_packet);
      write_packet_word(0xC0006000u);
      write_packet_word(0x80000000u);
      write_packet_word(0xC0015A01u);
      write_packet_word(26);
      write_packet_word(0);
      write_packet_word(0xC00145C1u);
      write_packet_word(25);
    } else {
      write_packet_word(0xC0012201u);
      write_packet_word(primitive_state);
      write_packet_word(draw_packet);
    }
  }

  StoreGuestU32(base, device + kScratchTailOffset, packet_tail);
  StoreGuestU32(base, device + kCommandPointerOffset, packet_tail);
  StoreGuestU32(base, device + kScratchVertexBufferOffset, vertex_buffer);
  StoreGuestU32(base, device + kScratchIndexBufferOffset, 0);
  StoreGuestU32(base, device + kScratchVertexWordsOffset, vertex_words);
  StoreGuestU32(base, device + kScratchIndexWordsOffset, 0);
  StoreGuestU64(base, device + kFetchDirtyQwordOffset,
                LoadGuestU64(base, device + kFetchDirtyQwordOffset) | 1ull);

  if (!g_logged_fable_begin_vertices_emulation) {
    REXLOG_INFO(
        "Refii plume is emulating Fable BeginVertices scratch allocation "
        "device=0x{:08X}",
        device);
    g_logged_fable_begin_vertices_emulation = true;
  }

  return emulation;
}

FableGuestCommandSubmitEmulation EmulateFablePlainRootDrawSubmission(
    std::string_view symbol) {
  if (!IsFableGuestPlainRootDrawHook(symbol)) {
    return {};
  }

  // The observer records these as typed FableDirect draw commands on hook enter.
  // Original execution only builds/submits the D3D command buffer Plume replaces.
  return {.handled = true, .result = 0};
}

void EmulatePlumeOwnedFableDisplaySwapBookkeeping(
    std::string_view symbol, const RendererHookArgs &args, uint8_t *base);

// Local sub_XXXXXXXX parser for desc-less callers of the command-emulation
// selection (identical result to the GTL SymbolAddress strtoul for sub_
// names; rex_* names return 0 and keep their string branches).
uint32_t FableParsedSubSymbolAddress(std::string_view symbol) {
  constexpr std::string_view kPrefix = "sub_";
  if (symbol.size() != kPrefix.size() + 8u ||
      symbol.substr(0, kPrefix.size()) != kPrefix) {
    return 0;
  }
  uint32_t parsed = 0;
  for (size_t i = kPrefix.size(); i < symbol.size(); ++i) {
    const char c = symbol[i];
    uint32_t digit;
    if (c >= '0' && c <= '9') {
      digit = static_cast<uint32_t>(c - '0');
    } else if (c >= 'a' && c <= 'f') {
      digit = static_cast<uint32_t>(c - 'a' + 10);
    } else if (c >= 'A' && c <= 'F') {
      digit = static_cast<uint32_t>(c - 'A' + 10);
    } else {
      return 0;
    }
    parsed = (parsed << 4u) | digit;
  }
  return parsed;
}

FableGuestCommandSubmitEmulation EmulateFableDirectCommandHook(
    std::string_view symbol, uint32_t symbol_address,
    const RendererHookArgs &args, uint8_t *base) {
  // Address compares first, string fallbacks only for the rare
  // descriptor-less (address 0) callers: this prologue runs for EVERY
  // suppressed hook call (6.9M/run, run 464), so a string ladder here is a
  // per-call tax on all of them.
  if (symbol_address == 0x82B981D0u ||
      (symbol_address == 0u && IsFableGetRenderTargetHook(symbol))) {
    return EmulateFableGetRenderTarget(args, base);
  }
  if (symbol_address == 0x8221C3E8u || symbol_address == 0x8221DFC0u ||
      (symbol_address == 0u && IsFableGuestPlainRootDrawHook(symbol))) {
    return EmulateFablePlainRootDrawSubmission(symbol);
  }
  if (symbol_address == 0x82BA2F68u) {
    return EmulateFableDisplayIntervalSync(symbol, args, base);
  }
  if (symbol_address == 0x82BA32F8u) {
    return EmulateFableComplexSwap(symbol, args, base);
  }
  if (symbol_address == 0x82BA34D8u) {
    EmulatePlumeOwnedFableDisplaySwapBookkeeping(symbol, args, base);
    return {.handled = true, .result = 0};
  }
  if (symbol_address == 0x82BA2748u) {
    return EmulateFableBlockUntilIdle(symbol, args, base);
  }

  // This switch replaces the FableUnleashedExactHookKindForSymbol linear
  // string scan (~40 entries, per suppressed call) that used to gate the
  // address ladder below. Every case here is a kFableUnleashedExactHooks
  // kHook entry, so switch membership IS the old gate for them; every other
  // symbol — non-table, kStub, or table kHook without a case — fell through
  // the address compares to `return {}` and still does.
  switch (symbol_address) {
  case 0x822869A0u: // SetRenderTarget
    return EmulateFableSetRenderTarget(args, base);
  case 0x82264590u: // SetDepthStencilSurface
    return EmulateFableSetDepthStencilSurface(args, base);
  case 0x821E2FE0u: // Clear
    return EmulateFableClearCommand(symbol, args, base);
  case 0x821F9F58u: // SetViewport
    return EmulateFableSetViewportPointer(args, base);
  case 0x821B6EE0u: // SetTexture
    return EmulateFableSetTexture(args, base);
  case 0x821F9C08u: // SetScissorRect
    return EmulateFableSetScissorPointer(args, base);
  case 0x82265CD8u: // DrawPrimitiveUP
    return EmulateFableDrawVerticesUpSubmission(symbol, args, base);
  case 0x822224A8u: // SetVertexDeclaration
    return EmulateFableSetVertexDeclaration(args, base);
  case 0x82232510u: // SetVertexShader
    return EmulateFableSetVertexShader(args, base);
  case 0x821B6C60u: // SetStreamSource
    return EmulateFableSetStreamSource(args, base);
  case 0x8219CC30u: // SetIndices
    return EmulateFableSetIndices(args, base);
  case 0x82208BB0u: // SetPixelShader
    return EmulateFableSetPixelShader(args, base);
  default:
    return {};
  }
}

bool IsFablePlumeCommandBufferBoundary(std::string_view symbol) {
  return IsFableCommandWriteHook(symbol) || IsFableCommandSubmitHook(symbol) ||
		         IsFableGuestShaderConstantCommandHook(symbol) ||
		         symbol == "sub_8221B010" ||
		         symbol == "sub_8228EF80" ||
		         symbol == "sub_82265CD8" ||
	         symbol == "sub_82217DB8" ||
	         symbol == "sub_822189B0" ||
	         symbol == "sub_82219740" ||
	         symbol == "sub_822194B8" ||
         symbol == "sub_82BA32F8" ||
         symbol == "sub_82BA34D8" ||
         symbol == "sub_82BA2F68" ||
         symbol == "sub_821BA0F0" ||
         symbol == "sub_821C3C60" ||
	         symbol == "sub_8226E180" ||
	         symbol == "sub_8226E738" ||
         symbol == "sub_8223CFA0" ||
         symbol == "sub_822A8F68" ||
         IsFableSceneStreamBoundaryHook(symbol) ||
         IsFableSceneFactoryBoundaryHook(symbol) ||
         IsFableTitleScenePopulationHook(symbol) ||
         IsFableGuestPlainRootDrawHook(symbol) ||
         IsFableGuestPrivateTextureRectangleHook(symbol) ||
         IsFableGuestPrivateTextureRectangleWrapperHook(symbol) ||
         IsFableGuestBeginVerticesWrapperHook(symbol);
}

bool IsFableDeepObserverDrawCommandBufferBoundary(std::string_view symbol) {
  return symbol == "sub_821BA0F0" ||
         symbol == "sub_821C3C60" ||
         symbol == "sub_8226E180" ||
         symbol == "sub_8226E738" ||
         symbol == "sub_8223CFA0" ||
         symbol == "sub_822A8F68" ||
         IsFableTitleScenePopulationHook(symbol);
}

bool IsFableDeepObserverCommandBufferBoundary(std::string_view symbol,
                                              RendererHookKind kind) {
  if (!RendererDeepObserverHooksEnabled()) {
    return false;
  }
  if (kind == RendererHookKind::kDraw) {
    return IsFableDeepObserverDrawCommandBufferBoundary(symbol);
  }
  if (kind == RendererHookKind::kGeneral) {
    return IsFableSceneStreamBoundaryHook(symbol) ||
           IsFableSceneFactoryBoundaryHook(symbol);
  }
  return false;
}

bool IsValidFableSceneStreamOffset(uint32_t offset, uint32_t cap) {
  constexpr uint32_t kMaxSceneStreamProbeOffset = 16u * 1024u * 1024u;
  return cap != 0 && cap <= kMaxSceneStreamProbeOffset && offset <= cap;
}
bool RepairFableSceneStreamPublishHandoff(std::string_view reason,
                                          uint8_t *base) {
  if (!RendererPlumeOwnsCommandSubmissionEnabled() || base == nullptr ||
      !RendererSceneStreamRepairEnabled()) {
    return false;
  }

  constexpr uint32_t kSchedulerStreamsAddress = 0x8349F9B0u;
  bool repaired_any = false;

  for (uint32_t slot = 0; slot < 4u; ++slot) {
    const uint32_t object =
        LoadGuestU32(base, kSchedulerStreamsAddress + slot * sizeof(uint32_t));
    if (!IsLikelyFableObjectRange(object, 72u)) {
      continue;
    }

    const uint32_t read_cursor = LoadGuestU32(base, object + 12u);
    const uint32_t visible_end = LoadGuestU32(base, object + 16u);
    const uint32_t read_limit = LoadGuestU32(base, object + 20u);
    const uint32_t last_read = LoadGuestU32(base, object + 24u);
    const uint32_t read_cap = LoadGuestU32(base, object + 28u);
    const uint32_t write_cursor = LoadGuestU32(base, object + 32u);
    const uint32_t write_consumed = LoadGuestU32(base, object + 36u);
    const uint32_t write_mark = LoadGuestU32(base, object + 40u);
    const uint32_t write_wrap = LoadGuestU32(base, object + 52u);
    const uint32_t published_end = LoadGuestU32(base, object + 60u);
    const uint32_t reader_consumed = LoadGuestU32(base, object + 64u);
    const uint32_t published_cap = LoadGuestU32(base, object + 68u);
    const uint32_t capacity = LoadGuestU32(base, object + 8u);
    if (!IsValidFableSceneStreamOffset(read_cursor, read_cap) ||
        !IsValidFableSceneStreamOffset(write_cursor, capacity) ||
        !IsValidFableSceneStreamOffset(write_mark, capacity) ||
        !IsValidFableSceneStreamOffset(write_wrap, capacity) ||
        !IsValidFableSceneStreamOffset(reader_consumed, capacity)) {
      continue;
    }

    bool repaired_slot = false;

    // Mirrors Fable::SceneStreamSyncPublishCursor. This publishes the current
    // writer message boundary, and imports the last reader-consumed cursor for
    // producer-side ring-space checks. It does not consume unread messages.
    uint32_t next_published_end = write_mark;
    uint32_t next_published_cap = write_wrap;
    if (reader_consumed < write_cursor || reader_consumed > write_wrap) {
      next_published_cap = capacity;
    }
    if (next_published_end == next_published_cap) {
      next_published_end = 0;
    }

    if (write_consumed != reader_consumed) {
      StoreGuestU32(base, object + 36u, reader_consumed);
      repaired_slot = true;
    }
    if (published_end != next_published_end) {
      StoreGuestU32(base, object + 60u, next_published_end);
      repaired_slot = true;
    }
    if (published_cap != next_published_cap) {
      StoreGuestU32(base, object + 68u, next_published_cap);
      repaired_slot = true;
    }

    // Mirrors Fable::SceneStreamRefresh so a render-owner consumer that wakes
    // after the pump sees the same reader window it would have seen after the
    // native refresh call.
    uint32_t next_reader_consumed =
        last_read == 0 ? capacity - 1u : last_read - 1u;
    uint32_t next_read_cursor = read_cursor;
    uint32_t next_read_limit = 0;
    if (next_read_cursor == next_published_cap) {
      next_read_cursor = 0;
    }
    next_read_limit = next_published_end < next_read_cursor
                          ? next_published_cap
                          : next_published_end;
    if (!IsValidFableSceneStreamOffset(next_reader_consumed, capacity) ||
        !IsValidFableSceneStreamOffset(next_read_cursor, next_published_cap) ||
        !IsValidFableSceneStreamOffset(next_read_limit, next_published_cap)) {
      continue;
    }

    if (reader_consumed != next_reader_consumed) {
      StoreGuestU32(base, object + 64u, next_reader_consumed);
      repaired_slot = true;
    }
    if (read_cursor != next_read_cursor) {
      StoreGuestU32(base, object + 12u, next_read_cursor);
      repaired_slot = true;
    }
    if (visible_end != next_published_end) {
      StoreGuestU32(base, object + 16u, next_published_end);
      repaired_slot = true;
    }
    if (read_limit != next_read_limit) {
      StoreGuestU32(base, object + 20u, next_read_limit);
      repaired_slot = true;
    }
    if (read_cap != next_published_cap) {
      StoreGuestU32(base, object + 28u, next_published_cap);
      repaired_slot = true;
    }

    if (!repaired_slot) {
      continue;
    }

    repaired_any = true;
    const uint64_t count =
        g_fable_scene_stream_handoff_repair_count.fetch_add(
            1, std::memory_order_relaxed) +
        1u;
    if (RendererDeepObserverHooksEnabled() &&
        (count <= 8u || (count & (count - 1u)) == 0)) {
      REXLOG_INFO(
          "Refii renderer exact Fable scene-stream publish handoff proof #{} "
          "reason={} slot={} object=0x{:08X} read={}->{} "
          "visible={}->{} limit={}->{} cap={}->{} last_read={} "
          "write={} mark={} wrap={} writer_consumed={}->{} "
          "reader_consumed={}->{} published={}/{}->{}/{}",
          count, reason, slot, object, read_cursor, next_read_cursor,
          visible_end, next_published_end, read_limit, next_read_limit,
          read_cap, next_published_cap, last_read, write_cursor, write_mark,
          write_wrap, write_consumed, reader_consumed, reader_consumed,
          next_reader_consumed, published_end, published_cap,
          next_published_end, next_published_cap);
    }
  }

  if (repaired_any && !g_logged_fable_scene_stream_handoff_repair) {
    REXLOG_INFO(
        "Refii renderer exact Fable scene-stream publish handoff active: "
        "Plume owns the guest-visible command queue and mirrors native "
        "SceneStreamSyncPublishCursor/SceneStreamRefresh cursor semantics");
    g_logged_fable_scene_stream_handoff_repair = true;
  }
  return repaired_any;
}

void CaptureFablePlumeCommandBufferBoundary(std::string_view symbol) {
  if (!IsFablePlumeCommandBufferBoundary(symbol)) {
    return;
  }

  const uint64_t submit =
      g_fable_plume_command_buffer_submit_count.fetch_add(
          1, std::memory_order_relaxed) +
      1u;
  // Command, scene, and wrapper boundaries prove ordered actions but not a
  // completed display frame. The guest RenderFrame exit owns non-video
  // publication; PIX/Bink producers use their explicit immediate path.
  FableDirectCaptureSubmittedRenderCommands(submit, false);
  if (!g_logged_fable_plume_command_buffer_capture) {
    REXLOG_INFO(
        "Refii plume is capturing Fable direct commands at Fable "
        "command-submit boundaries");
    g_logged_fable_plume_command_buffer_capture = true;
  }
}

FableGuestCommandSubmitEmulation EmulateFableQueryBufferSpace(
    const RendererHookArgs &args, uint8_t *base) {
  FableGuestCommandSubmitEmulation emulation;
  if (base == nullptr || args.r4 == 0 || args.r5 == 0 ||
      !IsLikelyFableWritableDataDwordAddress(args.r4) ||
      !IsLikelyFableWritableDataDwordAddress(args.r5)) {
    return emulation;
  }

  constexpr uint32_t kPrimaryCommandBaseOffset = 0x33B0;
  constexpr uint32_t kPrimaryCommandEpochOffset = 0x33B4;
  constexpr uint32_t kMinimumReportedFreeBytes = 256u * 1024u;
  constexpr uint32_t kMaxReportedFreeBytes = 4u * 1024u * 1024u;

  uint32_t contiguous_free = kMinimumReportedFreeBytes;
  uint32_t wrapped_free = 0;

  if (args.r3 != 0 && IsLikelyFableReadableRange(args.r3, 0x3A4Cu)) {
    const uint32_t primary_cursor =
        LoadGuestU32(base, args.r3 + kPrimaryCommandBaseOffset);
    const uint32_t primary_begin = LoadGuestU32(base, args.r3 + 0x3A38u);
    const uint32_t primary_end = LoadGuestU32(base, args.r3 + 0x3A3Cu);
    const uint32_t primary_write = LoadGuestU32(base, args.r3 + 0x30u);
    const uint32_t primary_epoch =
        LoadGuestU32(base, args.r3 + kPrimaryCommandEpochOffset);
    const uint32_t primary_write_epoch = LoadGuestU32(base, args.r3 + 0x3A48u);

    if (primary_cursor != 0 && primary_begin != 0 && primary_end > primary_begin &&
        primary_end - primary_begin <= kMaxReportedFreeBytes &&
        primary_write >= primary_begin && primary_write <= primary_end) {
      const uint32_t total_bytes = primary_end - primary_begin;
      // Plume owns guest command submission, so the D3D command buffer producer
      // can no longer wait for the original GPU path to advance this cursor.
      // Make Plume the owner of the guest-visible ring by marking everything
      // written so far as consumed before reporting available space. Reporting
      // synthetic free bytes without updating the cursor lets Fable overrun its
      // own ring state during the video/title handoff.
      StoreGuestU32(base, args.r3 + kPrimaryCommandBaseOffset, primary_write);
      StoreGuestU32(base, args.r3 + kPrimaryCommandEpochOffset,
                    primary_write_epoch);
      contiguous_free = std::clamp(total_bytes, kMinimumReportedFreeBytes,
                                   kMaxReportedFreeBytes);
      wrapped_free = 0;
    }
  }

  StoreGuestU32(base, args.r4, contiguous_free);
  StoreGuestU32(base, args.r5, wrapped_free);
  emulation.handled = true;
  emulation.result = 0;
  if (!g_logged_fable_query_buffer_space_emulation) {
    REXLOG_INFO(
        "Refii renderer Plume command replacement now owns "
        "D3DDevice_QueryBufferSpace guest-visible queue space");
    g_logged_fable_query_buffer_space_emulation = true;
  }
  return emulation;
}

FableGuestCommandSubmitEmulation EmulateFableBlockOnFence(
    const RendererHookArgs &args, uint8_t *base) {
  if (base == nullptr || !IsLikelyFableObjectRange(args.r3, 0x2AA0u)) {
    return {};
  }

  constexpr uint32_t kCurrentFenceOffset = 0x2A9C;
  constexpr uint32_t kConsumedFencePointerOffset = 0x2A90;

  const uint32_t device = args.r3;
  const uint32_t fence = args.r4;
  const uint32_t current_fence = LoadGuestU32(base, device + kCurrentFenceOffset);
  const uint32_t consumed_fence_pointer =
      LoadGuestU32(base, device + kConsumedFencePointerOffset);

  if (fence != 0 && fence == current_fence) {
    RendererHookArgs kick_args = args;
    kick_args.r3 = device;
    EmulateFableKickOff(kick_args, base);
  }
  if (IsLikelyGuestDwordAddress(consumed_fence_pointer)) {
    // P2 BEFORE P1 (2026-07-28). The write-back block holds TWO progress
    // values, and until now we published only the fence at +0x00:
    //   +0x00 = GPU fence                    -> releases D3D_CDevice_BlockOnFence
    //   +0x04 = secondary-ring consumed pos  -> releases
    //           D3D_CDevice_BlockOnSecondaryPosition (0x82206400)
    //
    // WHY THIS MATTERS. BlockOnSecondaryPosition is NOT hooked, so the real
    // guest loop runs, and it is reached from D3D_RingBufferDeviceAllocate --
    // the secondary-buffer path of every ring allocation. Its only escape is
    // D3D_CBlocker_Check's watchdog, which compares against KTHREAD+0x58.
    // In our port that field is `uint8_t unk_58[0x4]` in X_KTHREAD and nothing
    // ever writes it, so `v6 - a1[3]` is permanently 0, permanently < 0x1388,
    // and CBlocker_Check ALWAYS returns 1 = keep spinning. A wait whose own
    // condition is never satisfied therefore spins forever, without yielding.
    //
    // ORDER IS LOAD-BEARING: +0x04 first, matching the packet order emitted by
    // D3D_CDevice_SetFence (0x822986C4 writes writeback+4, 0x822986D4 writes
    // writeback+0). A consumer that sees a new fence with a stale position is
    // safe; the reverse tells the guest that ring memory is free while a later
    // fence is still outstanding.
    //
    // Values are GUEST-STATED, not reconstructed: SetFence saves the ring
    // cursor as it was BEFORE the fence packet at dev+0x33B8 and the lap at
    // dev+0x33BC. This mirrors RetireGuestGpuFenceLocked in refii_gtl.cpp,
    // which derives the identical word but never executes (its KickOff exit
    // does not fire).
    constexpr uint32_t kDeviceFencePointRing = 0x33B8;
    constexpr uint32_t kDeviceFencePointLap = 0x33BC;
    const uint32_t ring_cursor =
        LoadGuestU32(base, device + kDeviceFencePointRing);
    const uint32_t ring_lap = LoadGuestU32(base, device + kDeviceFencePointLap);
    if (ring_cursor != 0) {
      StoreGuestU32(base, consumed_fence_pointer + 4u,
                    (ring_lap & 3u) | (ring_cursor & ~3u));
    }
    StoreGuestU32(base, consumed_fence_pointer, current_fence);
  }

  return {.handled = true, .result = device};
}

// Present-decoupling STEP 6: the capture-tied gate body. See the block comment
// at the definitions of g_capture_ack_* (top of file) for the model.
void WaitForCaptureTiedAckAtPresentBoundary() {
  if (!REXCVAR_GET(renderer_capture_tied_ack)) {
    return;
  }
  // Count EVERY evaluation before the disarm guards (run-513 lesson: with
  // checks counted after them, a run segment ending inside the startup-video
  // phase showed checks=0 and read as "gate never executed" — a disarmed
  // phase must be distinguishable from a dead hook site in the census).
  g_capture_ack_gate_checks.fetch_add(1, std::memory_order_relaxed);
  // Disarm until the record path has published at least once (the pipeline
  // that satisfies the gate is proven live — protects boot) and while the
  // startup-video pump owns presentation (video exempt, as in the old gate).
  if (g_capture_ack_publishes.load(std::memory_order_acquire) == 0 ||
      IsFableStartupVideoPumpActive()) {
    g_capture_ack_gate_disarms.fetch_add(1, std::memory_order_relaxed);
    return;
  }
  // At Present N's hook ENTER the boundary signal for N has not fired yet
  // (gtl::OnHookExit runs AdvanceGuestFrameCompletionLocked AFTER the
  // command-submit emulation), so g_guest_present_signal reads N-1 here —
  // exactly the boundary whose captures must be complete before this present
  // acks. Frame N-1 sealed and signaled at Present N-1's exit, so its record
  // is already the pump's business; nothing here depends on this guest thread
  // making further progress — no self-deadlock, and the wait is bounded
  // regardless. No renderer mutex is held on this path (the direct-state seal
  // happens later in the hook exit tail).
  const uint64_t required_boundary =
      g_guest_present_signal.load(std::memory_order_acquire);
  const uint64_t captured =
      g_capture_ack_captured_boundary.load(std::memory_order_acquire);
  const uint64_t outstanding =
      required_boundary > captured ? required_boundary - captured : 0u;
  g_capture_ack_gate_last_outstanding.store(outstanding,
                                            std::memory_order_relaxed);
  uint64_t max_outstanding =
      g_capture_ack_gate_max_outstanding.load(std::memory_order_relaxed);
  while (outstanding > max_outstanding &&
         !g_capture_ack_gate_max_outstanding.compare_exchange_weak(
             max_outstanding, outstanding, std::memory_order_relaxed)) {
  }
  if (outstanding == 0) {
    return;
  }
  // Bounded: a record slower than this releases the guest anyway (counted as
  // a timeout in the census) — the gate degrades to the status quo, it never
  // hangs the guest thread. ~2x the title-phase record median (run 458).
  constexpr std::chrono::milliseconds kCaptureAckWaitBound{250};
  const auto wait_t0 = std::chrono::steady_clock::now();
  bool satisfied = false;
  {
    std::unique_lock<std::mutex> lock(g_capture_ack_mutex);
    satisfied = g_capture_ack_cv.wait_for(lock, kCaptureAckWaitBound, [&] {
      return g_capture_ack_captured_boundary.load(std::memory_order_acquire) >=
                 required_boundary ||
             g_plume_present_pump_shutdown_requested.load(
                 std::memory_order_acquire) ||
             !REXCVAR_GET(renderer_capture_tied_ack);
    });
  }
  g_capture_ack_gate_waits.fetch_add(1, std::memory_order_relaxed);
  g_capture_ack_gate_wait_us.fetch_add(
      static_cast<uint64_t>(
          std::chrono::duration_cast<std::chrono::microseconds>(
              std::chrono::steady_clock::now() - wait_t0)
              .count()),
      std::memory_order_relaxed);
  if (!satisfied) {
    g_capture_ack_gate_timeouts.fetch_add(1, std::memory_order_relaxed);
  }
}

FableGuestCommandSubmitEmulation EmulateFableEndScenePresentBoundary() {
  // The Plume backend owns presentation; the guest EndScene body only advances
  // native D3D presentation work that the replacement path performs elsewhere.
  // STEP 6: this hook return IS the guest's synthesized present ack — hold it
  // until the previous boundary's captures completed (capture-then-ack).
  WaitForCaptureTiedAckAtPresentBoundary();
  return {.handled = true, .result = 0};
}

FableGuestCommandSubmitEmulation EmulateFableGuestCommandSubmission(
    std::string_view symbol, const RendererHookArgs &args, uint8_t *base,
    const FableHookSymbolDesc *desc) {
  FableGuestCommandSubmitEmulation emulation;
  // Selection below is symbol-only; every sub_ compare is an integer compare
  // on the resolved address and the category lookups are cached desc bits.
  // Every caller resolves a descriptor at its call site, so desc is never
  // null here.
  const uint32_t symbol_address = desc->address;
  if (desc->surface_size_hook) {
    return EmulateFableSurfaceSize(symbol, args, base);
  }
  if (!RendererPlumeOwnsCommandSubmissionEnabled()) {
    return emulation;
  }

  if (const FableGuestCommandSubmitEmulation direct =
          EmulateFableDirectCommandHook(symbol, symbol_address, args, base);
      direct.handled) {
    emulation = direct;
  } else if (symbol_address == 0x82B6F1D0u ||
             (symbol_address == 0u && symbol == "rex_lhRenderer_EndScene")) {
    // rex_lhRenderer_EndScene maps to the GTL kPresent address 0x82B6F1D0.
    emulation = EmulateFableEndScenePresentBoundary();
  } else if (symbol_address == 0x822866E0u) {
    emulation = EmulateFableAddCallsToPrimaryBuffer(args, base);
  } else if (symbol_address == 0x82BA25C0u) {
    emulation = EmulateFableAddCommandsToPrimaryBuffer(args, base);
  } else if (symbol_address == 0x822868C8u) {
    emulation = EmulateFableQueueIndirectBuffer(args, base);
  } else if (symbol_address == 0x821E8D38u) {
    emulation = EmulateFableKickOffSegment(args, base);
  } else if (symbol_address == 0x821E8EC0u) {
    emulation = EmulateFableKickOff(args, base);
  } else if (symbol_address == 0x8221F2E0u) {
    emulation = EmulateFableFlushPendingGpuState(args, base);
  } else if (symbol_address == 0x8221E4A0u) {
    emulation = EmulateFableDeferredStateDispatchHook(args, base);
  } else if (symbol_address == 0x8221E550u) {
    emulation = EmulateFablePendingStateDispatchHook(args, base);
  } else if (symbol_address == 0x8221F348u) {
    emulation = EmulateFablePendingStateQueueHook(args, base);
  } else if (symbol_address == 0x822C6BA8u) {
    emulation = EmulateFableInvalidateCaches(args, base);
  } else if (symbol_address == 0x82196628u) {
    emulation = EmulateFableBeginExport(args, base);
  } else if (symbol_address == 0x82196750u) {
    emulation = EmulateFableEndExport(args, base);
  } else if (symbol_address == 0x8217F3E8u) {
    emulation = EmulateFableQueryBufferSpace(args, base);
  } else if (symbol_address == 0x821F6270u) {
    // D3DDevice_IsFencePending takes a fence token and returns whether native
    // GPU execution is still behind it. Once Plume owns command submission
    // there is no D3D fence to wait for, but the original helper
    // also kicks the current device if the queried token is the current fence.
    const uint32_t device =
        g_last_fable_device.load(std::memory_order_relaxed);
    constexpr uint32_t kCurrentFenceOffset = 0x2A9C;
    if (base != nullptr && device != 0 &&
        args.r3 == LoadGuestU32(base, device + kCurrentFenceOffset)) {
      RendererHookArgs kick_args = args;
      kick_args.r3 = device;
      EmulateFableKickOff(kick_args, base);
    }
    emulation.handled = true;
    emulation.result = 0;
  } else if (symbol_address == 0x82242668u) {
    emulation = EmulateFableBlockOnFence(args, base);
  } else if (symbol_address == 0x8221B010u) {
    emulation = EmulateFableSetPendingShaders(args, base);
  } else if (symbol_address == 0x8221C7D8u) {
    emulation = EmulateFableSetPendingRenderStates(args, base);
  } else if (symbol_address == 0x8221D1B0u) {
    emulation = EmulateFableSetPendingFetchConstants(args, base);
  } else if (symbol_address == 0x8228EF80u) {
    emulation = EmulateFableSetPendingSplit(args, base);
  } else if (symbol_address == 0x822154B0u) {
    emulation = EmulateFableSetPendingPredicated(args, base);
  } else if (symbol_address == 0x822B4008u) {
    emulation = EmulateFableSetPendingClipPlanes(args, base);
  } else if (symbol_address == 0x821F9D00u) {
    emulation = EmulateFableSetViewportFloats(args, base);
  } else if (symbol_address == 0x821F9918u) {
    emulation = EmulateFableSetSurfaceClip(args, base);
  } else if (symbol_address == 0x8219CD68u) {
    emulation = EmulateFableSetShaderGPRAllocation(args, base);
  } else if (symbol_address == 0x82B98248u || symbol_address == 0x82B98318u) {
    emulation = EmulateFableSetGammaRamp(symbol, args, base);
  } else if (symbol_address == 0x822AF568u) {
    emulation = EmulateFableSetScreenExtentQueryMode(args, base);
  } else if (symbol_address == 0x82BAEC38u) {
    emulation = EmulateFableSetScalerParameters(args, base);
  } else if (symbol_address == 0x82BAEA88u) {
    emulation = EmulateFableSetupScaler(args, base);
  } else if (symbol_address == 0x822655F0u) {
    emulation = EmulateFableSetPredicationState(args, base);
  } else if (desc->shader_constant_command_hook) {
    emulation = EmulateFableShaderConstantCommand(symbol, args, base);
  } else if (desc->shader_constant_state_hook) {
    emulation = EmulateFableShaderConstantState(symbol, args, base);
  } else if (desc->render_state_slot != UINT32_MAX) {
    emulation = EmulateFableSetRenderState(symbol, args, base);
  } else if (desc->sampler_state_slot != UINT32_MAX) {
    emulation = EmulateFableSetSamplerState(symbol, args, base);
  } else if (symbol_address == 0x821EF830u) {
    emulation = EmulateFableClearCommand(symbol, args, base);
  } else if (symbol_address == 0x822A09A0u) {
    emulation = EmulateFableSetColorClear(args, base);
  } else if (symbol_address == 0x82BA95E0u) {
    emulation = EmulateFablePixCaptureVideoFrame(symbol, args, base);
  } else if (symbol_address == 0x82219740u) {
    emulation = EmulateFableSceneBatchDrawPrimitiveBridge(symbol, args, base);
  } else if (symbol_address == 0x822194B8u) {
    emulation = EmulateD3DQueryIssue(symbol, args, base);
  } else if (desc->private_texture_rect_hook) {
    emulation = EmulateFablePrivateTextureRectangleSubmission(symbol, args,
                                                              base);
  } else if (symbol_address == 0x82205F68u) {
    emulation = EmulateFableBeginVerticesSubmission(symbol, args, base);
  } else if (symbol_address == 0x82217DB8u) {
    emulation = EmulateFableBeginIndexedVerticesSubmission(symbol, args, base);
  }

  if (emulation.handled && !g_logged_fable_command_submit_emulation) {
    REXLOG_INFO(
        "Refii plume is emulating Fable guest command submit bookkeeping "
        "target=0x{:08X}",
        args.r3);
    g_logged_fable_command_submit_emulation = true;
  }
  if (symbol_address == 0x821E8D38u && emulation.handled &&
      !g_logged_fable_kickoff_segment_emulation) {
    REXLOG_INFO("Refii plume is emulating Fable KickOffSegment bookkeeping "
                "device=0x{:08X}",
                args.r3);
    g_logged_fable_kickoff_segment_emulation = true;
  }
  if (symbol_address == 0x821E8EC0u && emulation.handled &&
      !g_logged_fable_kickoff_emulation) {
    REXLOG_INFO(
        "Refii plume is emulating Fable KickOff bookkeeping device=0x{:08X}",
        args.r3);
    g_logged_fable_kickoff_emulation = true;
  }
  return emulation;
}

}  // namespace refii::renderer
