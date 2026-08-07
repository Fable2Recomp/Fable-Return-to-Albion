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

bool IsLikelyGuestMemoryAddress(uint32_t guest_address);

bool IsFableSurfaceViewCreateHook(std::string_view symbol) {
  return symbol == "sub_82B98860" || // D3DTexture_GetSurfaceLevel
         symbol == "sub_82B98978" || // D3DCubeTexture_GetCubeMapSurface
         symbol == "sub_82B98A58";   // D3DVolumeTexture_GetVolumeLevel
}

bool IsFableBufferCreateHook(std::string_view symbol) {
  return symbol == "rex_lhGfx_CreateVertexBuffer" ||
         symbol == "rex_lhGfx_CreateIndexBuffer";
}

bool IsFableVertexDeclarationCreateHook(std::string_view symbol) {
  return symbol == "rex_lhGfx_CreateGPUState" ||
         symbol == "sub_82B99720";
}

bool IsFableShaderCreateHook(std::string_view symbol) {
  return symbol == "sub_82B994B8" || // D3DDevice_CreateVertexShader
         symbol == "sub_82B992B0";   // D3DDevice_CreatePixelShader
}

bool IsFableVertexShaderRegisterHook(std::string_view symbol) {
  return symbol == "rex_XGRegisterVertexShader";
}

bool IsFableShaderPatchHelperHook(std::string_view symbol) {
  return symbol == "sub_821D3570" || // D3D::PatchVertexShaderToMatchVertexDeclaration
         symbol == "sub_821E0220" || // D3D::IncrementalShaderPatchAndLoad
         symbol == "sub_821F7650" || // D3D::DirectShaderPatch
         symbol == "sub_82BA3D88" || // D3D::PatchVertexShaderToMatchPixelShader
         symbol == "sub_831CFA58";   // XGRAPHICS::ProcessVertexInputPatch
}

bool IsFableShaderUtilityHook(std::string_view symbol) {
  return symbol == "sub_830FE488" || // XGRAPHICS__ThreadSafeSSMPrecompileShader
         symbol == "sub_831CBAF8";   // XGRAPHICS__TranslateShader
}

bool IsFableTextureCreateHook(std::string_view symbol) {
  return symbol == "sub_82B98B28"; // D3DDevice_CreateTexture
}

bool IsFableSurfaceCreateHook(std::string_view symbol) {
  return symbol == "rex_lhTexture_Create"; // D3DDevice_CreateSurface
}

bool IsFableSurfaceWrapperCreateHook(std::string_view symbol) {
  return symbol == "sub_821FC8D8" || // Fable::CreateSurfaceWrapper
         symbol == "sub_82295380";   // Fable::CreateTiledSurfaceWrapper
}

bool IsFableRenderDepthSurfaceCtorHook(std::string_view symbol) {
  return symbol == "sub_82B8AB30"; // Fable::RenderDepthSurfaceCtor
}

bool IsFableRenderTargetSetupHook(std::string_view symbol) {
  return symbol == "rex_lhGfx_SetupRenderTargets" ||
         symbol == "sub_82B6F040" || // Fable::PresentationScratchSetup
         symbol == "sub_82B6F5C8";   // Fable::PresentationScratchTeardown
}

bool IsFableBinkVideoPlaneSetupHook(std::string_view symbol) {
  return bink::IsFableBinkVideoPlaneSetupSymbol(symbol);
}

bool IsFableBinkVideoPlaneSurfaceSetupHook(std::string_view symbol) {
  return bink::IsFableBinkVideoPlaneSurfaceSetupSymbol(symbol);
}

bool IsFableTextureSurfaceLockHook(std::string_view symbol) {
  return symbol == "sub_82B98930" || // LockTextureRect
         symbol == "sub_82B98C48";   // D3DSurface_LockRect
}

bool IsFableSurfaceCopyHook(std::string_view symbol) {
  return symbol == "sub_82D21C68" || // D3DXTex::InternalCopy
         symbol == "sub_82D160D8";   // D3DXLoadSurfaceFromSurface
}

bool IsFableSurfaceMemoryCopyHook(std::string_view symbol) {
  return symbol == "sub_82D14B20"; // D3DXLoadSurfaceFromMemory
}

bool IsFableTextureFillHook(std::string_view symbol) {
  return symbol == "sub_82D16CF8"; // D3DXFillTexture
}

bool IsFableTextureFileCreateHook(std::string_view symbol) {
  return symbol == "sub_82D17250"; // CreateTextureFromFileInMemoryEx
}

bool IsFableResourceQueryHook(std::string_view symbol) {
  return symbol == "sub_82B98C88" || // D3DSurface_GetContainer
         symbol == "sub_821FAB38" || // D3D::GetTextureDimensions
         symbol == "sub_821FA070" || // D3DSurface_GetDesc
         symbol == "sub_821FABD0" || // D3D::Get2DSurfaceDesc
         symbol == "sub_82B985C8" || // D3D::Get3DSurfaceDesc
         symbol == "sub_821FAD60";   // D3D::GetLevelSize
}

bool IsFableTextureAddressOffsetHook(std::string_view symbol) {
  return symbol == "rex_XGOffsetBaseTextureAddress";
}

bool IsFableTextureHeaderBuildHook(std::string_view symbol) {
  return symbol == "sub_821FBB78" || // XGSetTextureHeader
         symbol == "sub_8300BEE0" || // XGSetLineTextureHeader
         symbol == "sub_8300BF48";   // XGSetCubeTextureHeader
}

bool IsFableTextureHeaderUtilityHook(std::string_view symbol) {
  return symbol == "sub_821FB650" || // inline texture/fetch descriptor builder
         symbol == "sub_821FB968";   // XGRAPHICS::SetBaseTextureHeader
}

bool IsFableGetRenderTargetHook(std::string_view symbol) {
  return symbol == "sub_82B981D0"; // D3DDevice_GetRenderTarget
}

bool IsFableSurfaceSizeHook(std::string_view symbol) {
  return symbol == "sub_821F52A0"; // XGSurfaceSize
}

bool IsFableCpuResourceHelperHook(std::string_view symbol) {
  return symbol == "sub_821FA6C0" || // D3D::FindSurfaceWithinTexture
         symbol == "sub_82278138" || // D3D::LockSurface
         symbol == "sub_8300C290" || // XGRAPHICS__TileSurface
         symbol == "sub_8300CB40" || // j_XGRAPHICS__TileSurface
         symbol == "sub_8300CB48" || // XGTileTextureLevel
         symbol == "sub_83010E60" || // XGRAPHICS__TileVolume
         symbol == "sub_830117B8" || // XGTileVolumeTextureLevel
         symbol == "sub_8300C660" || // XGRAPHICS__UntileSurface
         symbol == "sub_8300CDA0" || // j_XGRAPHICS__UntileSurface
         symbol == "sub_8300CDA8" || // XGUntileTextureLevel
         symbol == "sub_830112F0" || // XGRAPHICS__UntileVolume
         symbol == "sub_83011AE0";   // XGUntileVolumeTextureLevel
}
uint32_t AllocateFableGuestMemory(PPCContext &ctx, uint8_t *base,
                                  uint32_t byte_size, uint32_t flags) {
  PPCContext alloc_ctx = ctx;
  alloc_ctx.r3.u64 = byte_size;
  alloc_ctx.r4.u64 = flags;
  rex_lhMemAlloc(alloc_ctx, base);
  return alloc_ctx.r3.u32;
}

uint32_t AllocateFableGuestObject(PPCContext &ctx, uint8_t *base,
                                  uint32_t byte_size) {
  return AllocateFableGuestMemory(ctx, base, byte_size, 0);
}

uint32_t AllocateFablePlumeGpuScratch(uint32_t byte_size) {
  constexpr uint32_t kScratchBase = 0xF0000000u;
  constexpr uint32_t kScratchEnd = 0xF8000000u;
  constexpr uint32_t kScratchAlignment = 0x100u;
  if (byte_size == 0 || byte_size > 64u * 1024u * 1024u) {
    return 0;
  }
  const uint32_t aligned_size =
      (byte_size + kScratchAlignment - 1u) & ~(kScratchAlignment - 1u);
  const uint32_t address = g_fable_plume_gpu_scratch_next.fetch_add(
      aligned_size, std::memory_order_relaxed);
  if (address < kScratchBase || address > kScratchEnd ||
      aligned_size > kScratchEnd - address) {
    return 0;
  }
  return address;
}

bool CanEmulateFableSurfaceViewCreate(std::string_view symbol,
                                      const RendererHookArgs &args,
                                      uint8_t *base) {
  return RendererReplaceResourcesEnabled() && base != nullptr &&
         IsFableSurfaceViewCreateHook(symbol) &&
         IsLikelyFableObjectAddress(args.r3);
}

uint32_t FableBufferCreateByteSize(std::string_view symbol,
                                   const RendererHookArgs &args) {
  if (symbol == "rex_lhGfx_CreateVertexBuffer") {
    const uint64_t byte_size =
        static_cast<uint64_t>(args.r4) * static_cast<uint64_t>(args.r5);
    return byte_size <= UINT32_MAX ? static_cast<uint32_t>(byte_size) : 0u;
  }
  if (symbol == "rex_lhGfx_CreateIndexBuffer") {
    return args.r4;
  }
  return 0;
}

bool IsLikelyFableWritableDataDwordAddress(uint32_t guest_address) {
  constexpr uint32_t kFableDataAddressMin = 0x83300000u;
  constexpr uint32_t kFableDataAddressMax = 0x84000000u;
  return (guest_address & (alignof(uint32_t) - 1u)) == 0 &&
         (IsLikelyGuestMemoryAddress(guest_address) ||
          (guest_address >= kFableDataAddressMin &&
           guest_address < kFableDataAddressMax));
}

bool IsLikelyFableWritableDataRange(uint32_t guest_address,
                                    uint32_t byte_size) {
  if (byte_size == 0 || guest_address > UINT32_MAX - byte_size) {
    return false;
  }
  constexpr uint32_t kFableDataAddressMin = 0x83300000u;
  constexpr uint32_t kFableDataAddressMax = 0x84000000u;
  const uint32_t end_address = guest_address + byte_size - 1u;
  return IsLikelyFableWritableDataDwordAddress(guest_address) &&
         (IsLikelyGuestMemoryAddress(end_address) ||
          (end_address >= kFableDataAddressMin &&
           end_address < kFableDataAddressMax));
}

bool CanEmulateFableBufferCreate(std::string_view symbol,
                                 const RendererHookArgs &args,
                                 uint8_t *base) {
  const uint32_t byte_size = FableBufferCreateByteSize(symbol, args);
  return RendererReplaceResourcesEnabled() && base != nullptr &&
         IsFableBufferCreateHook(symbol) &&
         IsLikelyFableWritableDataDwordAddress(args.r3) &&
         IsLikelyFableWritableDataDwordAddress(args.r3 + 8u) &&
         byte_size != 0 &&
         byte_size <= 64u * 1024u * 1024u;
}

bool IsLikelyFableVertexDeclarationElementAddress(uint32_t guest_address) {
  constexpr uint32_t kFableStaticDataAddressMin = 0x82000000u;
  constexpr uint32_t kFableStaticDataAddressMax = 0x84000000u;
  return (guest_address & (alignof(uint32_t) - 1u)) == 0 &&
         (IsLikelyGuestMemoryAddress(guest_address) ||
          (guest_address >= kFableStaticDataAddressMin &&
           guest_address < kFableStaticDataAddressMax));
}

bool IsLikelyFableReadableRange(uint32_t guest_address, uint32_t byte_size) {
  if (byte_size == 0 || guest_address > UINT32_MAX - byte_size) {
    return false;
  }
  const uint32_t end_address = guest_address + byte_size - 1u;
  constexpr uint32_t kFableStaticDataAddressMin = 0x82000000u;
  constexpr uint32_t kFableStaticDataAddressMax = 0x84000000u;
  return IsLikelyFableVertexDeclarationElementAddress(guest_address) &&
         (IsLikelyGuestMemoryAddress(end_address) ||
          (end_address >= kFableStaticDataAddressMin &&
           end_address < kFableStaticDataAddressMax));
}

bool IsLikelyFableObjectRange(uint32_t guest_address, uint32_t byte_size) {
  if (byte_size == 0 || guest_address > UINT32_MAX - byte_size) {
    return false;
  }
  const uint32_t end_address = guest_address + byte_size - 1u;
  constexpr uint32_t kFableObjectAddressMax = 0x80000000u;
  return IsLikelyFableObjectAddress(guest_address) &&
         end_address < kFableObjectAddressMax;
}

bool IsLikelyGuestMemoryRange(uint32_t guest_address, uint32_t byte_size) {
  if (byte_size == 0 || guest_address > UINT32_MAX - byte_size) {
    return false;
  }
  const uint32_t end_address = guest_address + byte_size - 1u;
  return IsLikelyGuestMemoryAddress(guest_address) &&
         ((guest_address < 0x80000000u && end_address < 0x80000000u) ||
          (guest_address >= 0xE0000000u && end_address >= 0xE0000000u));
}

FableVertexDeclarationElementCount CountFableVertexDeclarationElements(
    uint8_t *base, uint32_t elements_address) {
  constexpr uint32_t kMaxElements = 64u;
  FableVertexDeclarationElementCount result;
  if (base == nullptr ||
      !IsLikelyFableVertexDeclarationElementAddress(elements_address)) {
    return result;
  }
  for (uint32_t count = 0; count < kMaxElements; ++count) {
    const uint32_t element_address = elements_address + count * 12u;
    if (!IsLikelyFableVertexDeclarationElementAddress(element_address + 8u)) {
      return {};
    }
    const uint16_t stream = LoadGuestU16(base, element_address);
    if (stream == 0xFFu) {
      result.valid = true;
      result.count = count;
      return result;
    }
  }
  return result;
}

bool CanEmulateFableVertexDeclarationCreate(std::string_view symbol,
                                            const RendererHookArgs &args,
                                            uint8_t *base) {
  const FableVertexDeclarationElementCount count =
      CountFableVertexDeclarationElements(base, args.r3);
  return RendererReplaceResourcesEnabled() && base != nullptr &&
         IsFableVertexDeclarationCreateHook(symbol) && count.valid &&
         count.count <= 64u;
}

uint32_t FableShaderObjectHeaderBytes(std::string_view symbol,
                                      uint32_t container_flags) {
  if (symbol == "sub_82B994B8") {
    return (container_flags & 1u) != 0 ? 872u : 40u;
  }
  if (symbol == "sub_82B992B0") {
    return (container_flags & 1u) != 0 ? 872u : 40u;
  }
  return 0;
}

bool CanEmulateFableShaderCreate(std::string_view symbol,
                                 const RendererHookArgs &args,
                                 uint8_t *base) {
  if (!RendererReplaceResourcesEnabled() || base == nullptr ||
      !IsFableShaderCreateHook(symbol) ||
      !IsLikelyFableReadableRange(args.r3, 12u)) {
    return false;
  }

  constexpr uint32_t kMaxShaderContainerBytes = 2u * 1024u * 1024u;
  constexpr uint32_t kMaxShaderBytecodeBytes = 2u * 1024u * 1024u;
  const uint32_t container_flags = LoadGuestU32(base, args.r3 + 0u);
  const uint32_t bytecode_offset = LoadGuestU32(base, args.r3 + 4u);
  const uint32_t bytecode_size = LoadGuestU32(base, args.r3 + 8u);
  const uint32_t object_prefix =
      FableShaderObjectHeaderBytes(symbol, container_flags);
  if (object_prefix == 0 || bytecode_offset == 0 || bytecode_size == 0 ||
      bytecode_offset > kMaxShaderContainerBytes ||
      bytecode_size > kMaxShaderBytecodeBytes ||
      bytecode_size > kMaxShaderContainerBytes - bytecode_offset) {
    return false;
  }
  return IsLikelyFableReadableRange(args.r3, bytecode_offset + bytecode_size);
}

bool CanEmulateFableVertexShaderRegister(std::string_view symbol,
                                         const RendererHookArgs &args,
                                         uint8_t *base) {
  return RendererReplaceResourcesEnabled() && base != nullptr &&
         IsFableVertexShaderRegisterHook(symbol) &&
         IsLikelyFableObjectRange(args.r3, 872u) && args.r4 != 0 &&
         IsLikelyFableReadableRange(args.r4, sizeof(uint32_t));
}

bool CanEmulateFableShaderPatchHelper(std::string_view symbol,
                                      const RendererHookArgs &args,
                                      uint8_t *base) {
  if (!RendererReplaceResourcesEnabled() || base == nullptr ||
      !IsFableShaderPatchHelperHook(symbol)) {
    return false;
  }
  if (symbol == "sub_821D3570") {
    return IsLikelyFableObjectAddress(args.r3) && args.r4 != 0 &&
           IsLikelyFableObjectAddress(args.r5) && args.r6 != 0;
  }
  if (symbol == "sub_821E0220") {
    return IsLikelyFableObjectAddress(args.r3) &&
           IsLikelyFableObjectAddress(args.r5) &&
           IsLikelyFableObjectAddress(args.r7);
  }
  if (symbol == "sub_821F7650") {
    return IsLikelyFableObjectAddress(args.r3) &&
           IsLikelyFableObjectAddress(args.r4) &&
           IsLikelyFableObjectAddress(args.r5);
  }
  if (symbol == "sub_82BA3D88") {
    return args.r3 != 0 && args.r4 != 0 && args.r5 != 0;
  }
  if (symbol == "sub_831CFA58") {
    return args.r3 != 0 && args.r4 != 0;
  }
  return false;
}

bool CanEmulateFableShaderUtility(std::string_view symbol,
                                  const RendererHookArgs &args,
                                  uint8_t *base) {
  if (!RendererReplaceResourcesEnabled() || base == nullptr ||
      !IsFableShaderUtilityHook(symbol)) {
    return false;
  }
  return args.r3 != 0 && args.r4 != 0;
}

uint32_t FableTextureCreateMemoryFlags(uint32_t usage) {
  const uint32_t heap_selector = (((~usage >> 2u) & 1u) | 2u) << 28u;
  return 0x8C800000u | heap_selector;
}

uint32_t AlignFableAllocationSize(uint32_t byte_size) {
  return (byte_size + 15u) & ~15u;
}

bool CanEmulateFableTextureCreate(std::string_view symbol,
                                  const RendererHookArgs &args,
                                  uint8_t *base) {
  constexpr uint32_t kMaxTextureDimension = 8192u;
  return RendererReplaceResourcesEnabled() && base != nullptr &&
         IsFableTextureCreateHook(symbol) && args.r3 != 0 && args.r4 != 0 &&
         args.r3 <= kMaxTextureDimension && args.r4 <= kMaxTextureDimension &&
         args.r6 <= 32u && args.r10 != 0;
}

bool CanEmulateFableSurfaceCreate(std::string_view symbol,
                                  const RendererHookArgs &args,
                                  uint8_t *base) {
  constexpr uint32_t kMaxSurfaceDimension = 8192u;
  return RendererReplaceResourcesEnabled() && base != nullptr &&
         IsFableSurfaceCreateHook(symbol) && args.r3 != 0 && args.r4 != 0 &&
         args.r3 <= kMaxSurfaceDimension && args.r4 <= kMaxSurfaceDimension &&
         (args.r7 == 0 || IsLikelyGuestMemoryRange(args.r7, 16u));
}

uint32_t FableSurfaceWrapperD3DFormat(uint8_t *base, uint32_t format_shape) {
  constexpr uint32_t kFableSurfaceFormatTable = 0x8331DAD8u;
  constexpr uint32_t kFableSurfaceFormatStride = 112u;
  if (base == nullptr || !IsLikelyFableWritableDataRange(format_shape, 4u)) {
    return 0;
  }
  const uint32_t format_index = LoadGuestU32(base, format_shape);
  if (format_index >= 256u) {
    return 0;
  }
  return LoadGuestU32(base,
                      kFableSurfaceFormatTable +
                          format_index * kFableSurfaceFormatStride);
}

bool CanEmulateFableSurfaceWrapperCreate(std::string_view symbol,
                                         const RendererHookArgs &args,
                                         uint8_t *base) {
  return RendererReplaceResourcesEnabled() && base != nullptr &&
         IsFableSurfaceWrapperCreateHook(symbol) &&
         IsLikelyFableWritableDataRange(args.r3 + 4u, 8u) &&
         IsLikelyFableReadableRange(args.r4, 8u) &&
         IsLikelyFableWritableDataRange(args.r5, 4u) &&
         FableSurfaceWrapperD3DFormat(base, args.r5) != 0;
}

bool CanEmulateFableRenderDepthSurfaceCtor(std::string_view symbol,
                                           const RendererHookArgs &args,
                                           uint8_t *base) {
  constexpr uint32_t kMaxSurfaceDimension = 8192u;
  if (!RendererReplaceResourcesEnabled() || base == nullptr ||
      !IsFableRenderDepthSurfaceCtorHook(symbol) ||
      !IsLikelyFableObjectRange(args.r3, 32u) ||
      !IsLikelyFableReadableRange(args.r4, 8u) ||
      !IsLikelyFableWritableDataRange(args.r5, 4u) ||
      FableSurfaceWrapperD3DFormat(base, args.r5) == 0) {
    return false;
  }
  const uint32_t width = LoadGuestU32(base, args.r4 + 0u);
  const uint32_t height = LoadGuestU32(base, args.r4 + 4u);
  return width != 0 && height != 0 && width <= kMaxSurfaceDimension &&
         height <= kMaxSurfaceDimension;
}

bool CanEmulateFableRenderTargetSetup(std::string_view symbol, uint8_t *base) {
  return RendererReplaceResourcesEnabled() && base != nullptr &&
         IsFableRenderTargetSetupHook(symbol);
}

bool CanEmulateFableBinkVideoPlaneSetup(std::string_view symbol,
                                        const RendererHookArgs &args,
                                        uint8_t *base) {
  return RendererReplaceResourcesEnabled() &&
         IsFableBinkVideoPlaneSetupHook(symbol) &&
         bink::CanEmulateFableBinkVideoPlaneSetup(args, base);
}

bool CanEmulateFableBinkVideoPlaneSurfaceSetup(std::string_view symbol,
                                               uint8_t *base) {
  return RendererReplaceResourcesEnabled() &&
         IsFableBinkVideoPlaneSurfaceSetupHook(symbol) &&
         bink::CanEmulateFableBinkVideoPlaneSurfaceSetup(base);
}

void ZeroGuestDwords(uint8_t *base, uint32_t guest_address,
                     uint32_t byte_size) {
  for (uint32_t offset = 0; offset < byte_size; offset += sizeof(uint32_t)) {
    StoreGuestU32(base, guest_address + offset, 0);
  }
}

void CopyGuestBytes(uint8_t *base, uint32_t destination, uint32_t source,
                    uint32_t byte_size) {
  if (!IsReadableGuestByteRange(base, destination, byte_size) ||
      !IsReadableGuestByteRange(base, source, byte_size)) {
    return;
  }

  std::memcpy(base + destination + GuestPhysicalHostOffset(destination),
              base + source + GuestPhysicalHostOffset(source), byte_size);
}

bool MoveGuestBytes(uint8_t *base, uint32_t destination, uint32_t source,
                    uint32_t byte_size) {
  if (!IsReadableGuestByteRange(base, destination, byte_size) ||
      !IsReadableGuestByteRange(base, source, byte_size)) {
    return false;
  }

  std::memmove(base + destination + GuestPhysicalHostOffset(destination),
               base + source + GuestPhysicalHostOffset(source), byte_size);
  return true;
}

bool SetGuestBytes(uint8_t *base, uint32_t destination, int value,
                   uint32_t byte_size) {
  if (!IsReadableGuestByteRange(base, destination, byte_size)) {
    return false;
  }

  std::memset(base + destination + GuestPhysicalHostOffset(destination), value,
              byte_size);
  return true;
}

FableGuestResourceEmulation EmulateFableVertexBufferHeader(
    const RendererHookArgs &args, uint8_t *base);
FableGuestResourceEmulation EmulateFableIndexBufferHeader(
    const RendererHookArgs &args, uint8_t *base);

FableGuestResourceEmulation EmulateFableBufferCreate(
    std::string_view symbol, const RendererHookArgs &args, PPCContext &ctx,
    uint8_t *base) {
  FableGuestResourceEmulation emulation;
  if (!RendererReplaceResourcesEnabled() || base == nullptr ||
      !IsFableBufferCreateHook(symbol)) {
    return emulation;
  }
  if (!CanEmulateFableBufferCreate(symbol, args, base)) {
    if (IsLikelyFableWritableDataDwordAddress(args.r3)) {
      StoreGuestU32(base, args.r3 + 0x00u, 0);
    }
    emulation.handled = true;
    emulation.result = 0;
    if (!g_logged_fable_buffer_create_shape) {
      REXLOG_WARN(
          "Refii renderer Plume resource adapter rejected unsupported Fable "
          "buffer creation shape {} r3=0x{:08X} "
          "r4=0x{:08X} r5=0x{:08X} r6=0x{:08X} r7=0x{:08X} r8=0x{:08X}",
          symbol, args.r3, args.r4, args.r5, args.r6, args.r7, args.r8);
      g_logged_fable_buffer_create_shape = true;
    }
    return emulation;
  }

  constexpr uint32_t kWrapperBytes = 40u;
  constexpr uint32_t kFableBufferWrapperVtable = 0x820FB130u;
  const bool vertex_buffer = symbol == "rex_lhGfx_CreateVertexBuffer";
  const uint32_t byte_size = FableBufferCreateByteSize(symbol, args);
  const uint32_t wrapper = AllocateFableGuestObject(ctx, base, kWrapperBytes);
  const uint32_t gpu_scratch = AllocateFablePlumeGpuScratch(byte_size);
  if (!IsLikelyFableObjectAddress(wrapper) ||
      !IsLikelyGuestDwordAddress(wrapper + kWrapperBytes - sizeof(uint32_t)) ||
      gpu_scratch == 0 || !IsLikelyGuestMemoryAddress(gpu_scratch)) {
    StoreGuestU32(base, args.r3 + 0x00u, 0);
    if (vertex_buffer) {
      StoreGuestU32(base, args.r3 + 0x04u, byte_size);
      StoreGuestU32(base, args.r3 + 0x08u, 0);
      StoreGuestU8(base, args.r3 + 0x08u, args.r5 & 0xFFu);
      emulation.result = 1;
    } else {
      StoreGuestU32(base, args.r3 + 0x04u, args.r4);
      StoreGuestU32(base, args.r3 + 0x08u, args.r8);
    }
    emulation.handled = true;
    return emulation;
  }

  ZeroGuestDwords(base, wrapper, kWrapperBytes);
  StoreGuestU32(base, wrapper + 0x00u, kFableBufferWrapperVtable);
  StoreGuestU32(base, wrapper + 0x04u, gpu_scratch);

  RendererHookArgs header_args = {};
  header_args.r3 = byte_size;
  header_args.r4 = (vertex_buffer ? args.r6 : args.r5) | 0x200u;
  header_args.r5 = vertex_buffer ? args.r7 : args.r6;
  header_args.r6 = vertex_buffer ? gpu_scratch : 0u;
  header_args.r7 = vertex_buffer ? wrapper + 0x08u : gpu_scratch;
  header_args.r8 = wrapper + 0x08u;
  if (vertex_buffer) {
    EmulateFableVertexBufferHeader(header_args, base);
  } else {
    EmulateFableIndexBufferHeader(header_args, base);
  }

  StoreGuestU32(base, args.r3 + 0x00u, wrapper);
  if (vertex_buffer) {
    StoreGuestU32(base, args.r3 + 0x04u, byte_size);
    StoreGuestU32(base, args.r3 + 0x08u, 0);
    StoreGuestU8(base, args.r3 + 0x08u, args.r5 & 0xFFu);
    emulation.result = 1;
  } else {
    StoreGuestU32(base, args.r3 + 0x04u, args.r4);
    StoreGuestU32(base, args.r3 + 0x08u, args.r8);
    emulation.result = 1;
  }

  emulation.handled = true;
  if (!g_logged_fable_buffer_create_emulation) {
    REXLOG_INFO(
        "Refii renderer Plume resource replacement now owns D3D buffer "
        "object creation for rex_lhGfx_CreateVertexBuffer/"
        "rex_lhGfx_CreateIndexBuffer");
    g_logged_fable_buffer_create_emulation = true;
  }
  return emulation;
}

FableGuestResourceEmulation EmulateFableVertexDeclarationCreate(
    std::string_view symbol, const RendererHookArgs &args, PPCContext &ctx,
    uint8_t *base) {
  FableGuestResourceEmulation emulation;
  if (!CanEmulateFableVertexDeclarationCreate(symbol, args, base)) {
    return emulation;
  }

  const FableVertexDeclarationElementCount counted_elements =
      CountFableVertexDeclarationElements(base, args.r3);
  const uint32_t element_count = counted_elements.count;
  constexpr uint32_t kHeaderBytes = 56u;
  const uint32_t element_bytes = element_count * 12u;
  const uint32_t object_size = kHeaderBytes + element_bytes;
  const uint32_t object = AllocateFableGuestObject(ctx, base, object_size);
  if (!IsLikelyFableObjectAddress(object) ||
      !IsLikelyGuestDwordAddress(object + object_size - sizeof(uint32_t))) {
    emulation.handled = true;
    emulation.result = 0;
    return emulation;
  }

  ZeroGuestDwords(base, object, object_size);
  uint32_t max_stream = 0;
  uint8_t stream_mask_bytes[16] = {};
  for (uint32_t i = 0; i < element_count; ++i) {
    const uint32_t element_address = args.r3 + i * 12u;
    const uint16_t stream = LoadGuestU16(base, element_address);
    if (stream < 16u) {
      stream_mask_bytes[stream] = 0xFFu;
      max_stream = std::max<uint32_t>(max_stream, stream);
    }
  }

  StoreGuestU32(base, object + 0x00u, 0x00100005u);
  StoreGuestU32(base, object + 0x04u, 1u);
  StoreGuestU32(base, object + 0x14u, 0xFFFF0000u);
  StoreGuestU32(base, object + 0x18u, element_count);
  StoreGuestU32(base, object + 0x1Cu, max_stream);
  for (uint32_t i = 0; i < sizeof(stream_mask_bytes); ++i) {
    StoreGuestU8(base, object + 0x20u + i, stream_mask_bytes[i]);
  }
  CopyGuestBytes(base, object + 0x34u, args.r3, element_bytes);

  emulation.handled = true;
  emulation.result = object;
  if (!g_logged_fable_vertex_declaration_create_emulation) {
    REXLOG_INFO(
        "Refii renderer Plume resource replacement now owns D3D vertex "
        "declaration object creation for rex_lhGfx_CreateGPUState");
    g_logged_fable_vertex_declaration_create_emulation = true;
  }
  return emulation;
}

FableGuestResourceEmulation EmulateFableShaderCreate(
    std::string_view symbol, const RendererHookArgs &args, PPCContext &ctx,
    uint8_t *base) {
  FableGuestResourceEmulation emulation;
  if (!CanEmulateFableShaderCreate(symbol, args, base)) {
    return emulation;
  }

  const bool vertex_shader = symbol == "sub_82B994B8";
  const uint32_t container_flags = LoadGuestU32(base, args.r3 + 0u);
  const uint32_t bytecode_offset = LoadGuestU32(base, args.r3 + 4u);
  const uint32_t bytecode_size = LoadGuestU32(base, args.r3 + 8u);
  const uint32_t object_prefix =
      FableShaderObjectHeaderBytes(symbol, container_flags);
  const uint32_t minimum_object_size = vertex_shader ? 872u : 40u;
  const uint32_t object_size =
      std::max<uint32_t>(minimum_object_size, object_prefix + bytecode_offset);
  const uint32_t object = AllocateFableGuestObject(ctx, base, object_size);
  const uint32_t bytecode = AllocateFableGuestObject(ctx, base, bytecode_size);
  if (!IsLikelyFableObjectRange(object, object_size) ||
      !IsLikelyFableObjectRange(bytecode, bytecode_size)) {
    emulation.handled = true;
    emulation.result = 0;
    return emulation;
  }

  ZeroGuestDwords(base, object, object_size);
  CopyGuestBytes(base, object + object_prefix, args.r3, bytecode_offset);
  CopyGuestBytes(base, bytecode, args.r3 + bytecode_offset, bytecode_size);

  StoreGuestU32(base, object + 0x00u, vertex_shader ? 0x00100006u
                                                    : 0x00100007u);
  StoreGuestU32(base, object + 0x04u, 1u);
  StoreGuestU32(base, object + 0x14u, 0xFFFF0000u);
  StoreGuestU32(base, object + (vertex_shader ? 0x20u : 0x18u), bytecode);

  emulation.handled = true;
  emulation.result = object;
  if (!g_logged_fable_shader_create_emulation) {
    REXLOG_INFO(
        "Refii renderer Plume resource replacement now owns D3D shader "
        "object creation for D3DDevice_CreateVertexShader/"
        "D3DDevice_CreatePixelShader");
    g_logged_fable_shader_create_emulation = true;
  }
  return emulation;
}

FableGuestResourceEmulation EmulateFableVertexShaderRegister(
    std::string_view symbol, const RendererHookArgs &args, PPCContext &ctx,
    uint8_t *base) {
  FableGuestResourceEmulation emulation;
  if (!CanEmulateFableVertexShaderRegister(symbol, args, base)) {
    return emulation;
  }

  PPCContext helper_ctx = ctx;
  __imp__rex_XGRegisterVertexShader(helper_ctx, base);

  emulation.handled = true;
  emulation.result = helper_ctx.r3.u32;
  if (!g_logged_fable_vertex_shader_register_emulation) {
    REXLOG_INFO(
        "Refii renderer Plume resource replacement now owns "
        "XGRegisterVertexShader guest object registration");
    g_logged_fable_vertex_shader_register_emulation = true;
  }
  return emulation;
}

FableGuestResourceEmulation EmulateFableShaderPatchHelper(
    std::string_view symbol, const RendererHookArgs &args, PPCContext &ctx,
    uint8_t *base) {
  FableGuestResourceEmulation emulation;
  if (!CanEmulateFableShaderPatchHelper(symbol, args, base)) {
    return emulation;
  }

  PPCContext helper_ctx = ctx;
  if (symbol == "sub_821D3570") {
    __imp__sub_821D3570(helper_ctx, base);
  } else if (symbol == "sub_821E0220") {
    __imp__sub_821E0220(helper_ctx, base);
  } else if (symbol == "sub_821F7650") {
    __imp__sub_821F7650(helper_ctx, base);
  } else if (symbol == "sub_82BA3D88") {
    __imp__sub_82BA3D88(helper_ctx, base);
  } else if (symbol == "sub_831CFA58") {
    __imp__sub_831CFA58(helper_ctx, base);
  }

  emulation.handled = true;
  emulation.result = helper_ctx.r3.u32;
  if (!g_logged_fable_shader_patch_helper_emulation) {
    REXLOG_INFO(
        "Refii renderer Plume resource replacement now owns D3D shader patch "
        "helper guest-memory mutations");
    g_logged_fable_shader_patch_helper_emulation = true;
  }
  return emulation;
}

FableGuestResourceEmulation EmulateFableShaderUtility(
    std::string_view symbol, const RendererHookArgs &args, PPCContext &ctx,
    uint8_t *base) {
  FableGuestResourceEmulation emulation;
  if (!CanEmulateFableShaderUtility(symbol, args, base)) {
    return emulation;
  }

  PPCContext helper_ctx = ctx;
  if (symbol == "sub_830FE488") {
    __imp__sub_830FE488(helper_ctx, base);
  } else if (symbol == "sub_831CBAF8") {
    __imp__sub_831CBAF8(helper_ctx, base);
  }

  emulation.handled = true;
  emulation.result = helper_ctx.r3.u32;
  if (!g_logged_fable_shader_utility_emulation) {
    REXLOG_INFO(
        "Refii renderer Plume resource replacement now owns XGRAPHICS shader "
        "precompile/translate guest-memory mutations");
    g_logged_fable_shader_utility_emulation = true;
  }
  return emulation;
}

FableGuestResourceEmulation EmulateFableTextureCreate(
    std::string_view symbol, const RendererHookArgs &args, PPCContext &ctx,
    uint8_t *base) {
  FableGuestResourceEmulation emulation;
  if (!CanEmulateFableTextureCreate(symbol, args, base)) {
    return emulation;
  }

  constexpr uint32_t kTextureObjectBytes = 52u;
  constexpr uint32_t kTextureObjectAllocFlags = 0x64800000u;
  constexpr uint32_t kSyntheticStackFrameBytes = 224u;
  constexpr uint32_t kMaxOwnedTextureBytes = 64u * 1024u * 1024u;
  const uint32_t object = AllocateFableGuestMemory(
      ctx, base, kTextureObjectBytes, kTextureObjectAllocFlags);
  if (!IsLikelyFableObjectRange(object, kTextureObjectBytes) ||
      !IsReadableGuestByteRange(base, object, kTextureObjectBytes)) {
    emulation.handled = true;
    emulation.result = 0;
    return emulation;
  }

  ZeroGuestDwords(base, object, kTextureObjectBytes);
  PPCContext header_ctx = ctx;
  header_ctx.r1.u32 = ctx.r1.u32 - kSyntheticStackFrameBytes;
  StoreGuestU32(base, header_ctx.r1.u32 + 84u, 0u);
  StoreGuestU32(base, header_ctx.r1.u32 + 92u, 0u);
  StoreGuestU32(base, header_ctx.r1.u32 + 100u, 0u);
  StoreGuestU32(base, header_ctx.r1.u32 + 108u, object);
  StoreGuestU32(base, header_ctx.r1.u32 + 116u, header_ctx.r1.u32 + 128u);
  StoreGuestU32(base, header_ctx.r1.u32 + 124u, header_ctx.r1.u32 + 132u);
  StoreGuestU32(base, header_ctx.r1.u32 + 128u, 0u);
  StoreGuestU32(base, header_ctx.r1.u32 + 132u, 0u);

  header_ctx.r3.u64 = args.r10;
  header_ctx.r4.u64 = args.r3;
  header_ctx.r5.u64 = args.r4;
  header_ctx.r6.u64 = args.r5;
  header_ctx.r7.u64 = args.r6;
  header_ctx.r8.u64 = args.r7;
  header_ctx.r9.u64 = args.r8;
  header_ctx.r10.u64 = 2u;
  __imp__sub_821FB650(header_ctx, base);

  const uint32_t base_size =
      LoadGuestU32(base, header_ctx.r1.u32 + 128u);
  const uint32_t mip_size = LoadGuestU32(base, header_ctx.r1.u32 + 132u);
  if (base_size == 0 || base_size > kMaxOwnedTextureBytes ||
      mip_size > kMaxOwnedTextureBytes) {
    emulation.handled = true;
    emulation.result = 0;
    return emulation;
  }

  const uint32_t memory_flags = FableTextureCreateMemoryFlags(args.r7);
  const uint32_t base_allocation = AllocateFableGuestMemory(
      ctx, base, AlignFableAllocationSize(base_size), memory_flags);
  const uint32_t mip_allocation =
      mip_size != 0
          ? AllocateFableGuestMemory(
                ctx, base, AlignFableAllocationSize(mip_size), memory_flags)
          : 0u;
  if (!IsLikelyGuestMemoryRange(base_allocation,
                                AlignFableAllocationSize(base_size)) ||
      (mip_size != 0 &&
       !IsLikelyGuestMemoryRange(mip_allocation,
                                 AlignFableAllocationSize(mip_size)))) {
    emulation.handled = true;
    emulation.result = 0;
    return emulation;
  }

  const uint32_t fetch_word1 = LoadGuestU32(base, object + 32u);
  StoreGuestU32(base, object + 32u,
                (fetch_word1 & 0x00000FFFu) |
                    (base_allocation & 0xFFFFF000u));
  const uint32_t fetch_word5 = LoadGuestU32(base, object + 48u);
  StoreGuestU32(base, object + 48u,
                (fetch_word5 & 0x00000FFFu) |
                    (mip_allocation & 0xFFFFF000u));

  emulation.handled = true;
  emulation.result = object;
  if (!g_logged_fable_texture_create_emulation) {
    REXLOG_INFO(
        "Refii renderer Plume resource replacement now owns D3D texture "
        "object creation for D3DDevice_CreateTexture");
    g_logged_fable_texture_create_emulation = true;
  }
  return emulation;
}

FableGuestResourceEmulation EmulateFableSurfaceCreate(
    std::string_view symbol, const RendererHookArgs &args, PPCContext &ctx,
    uint8_t *base) {
  FableGuestResourceEmulation emulation;
  if (!CanEmulateFableSurfaceCreate(symbol, args, base)) {
    return emulation;
  }

  constexpr uint32_t kSurfaceObjectBytes = 48u;
  constexpr uint32_t kSurfaceObjectAllocFlags = 0x64800000u;
  constexpr uint32_t kSyntheticStackFrameBytes = 144u;
  const uint32_t object = AllocateFableGuestMemory(
      ctx, base, kSurfaceObjectBytes, kSurfaceObjectAllocFlags);
  if (!IsLikelyFableObjectRange(object, kSurfaceObjectBytes)) {
    emulation.handled = true;
    emulation.result = 0;
    return emulation;
  }

  ZeroGuestDwords(base, object, kSurfaceObjectBytes);
  PPCContext header_ctx = ctx;
  header_ctx.r1.u32 = ctx.r1.u32 - kSyntheticStackFrameBytes;
  StoreGuestU32(base, header_ctx.r1.u32 + 80u, 0u);
  StoreGuestU32(base, header_ctx.r1.u32 + 84u, 0u);
  header_ctx.r3.u64 = args.r3;
  header_ctx.r4.u64 = args.r4;
  header_ctx.r5.u64 = args.r5;
  header_ctx.r6.u64 = args.r6;
  header_ctx.r7.u64 = args.r7;
  header_ctx.r8.u64 = object;
  header_ctx.r9.u64 = header_ctx.r1.u32 + 80u;
  header_ctx.r10.u64 = header_ctx.r1.u32 + 84u;
  __imp__sub_821F5028(header_ctx, base);

  StoreGuestU32(base, object + 0u,
                LoadGuestU32(base, object + 0u) | 0x00100000u);
  if (args.r7 == 0) {
    const uint32_t required_tiles =
        LoadGuestU32(base, header_ctx.r1.u32 + 80u);
    PPCContext allocator_ctx = ctx;
    allocator_ctx.r3.u64 = required_tiles;
    allocator_ctx.r4.u64 = header_ctx.r1.u32 + 80u;
    sub_82BA4AF8(allocator_ctx, base);
    const bool allocated = allocator_ctx.r3.u32 != 0;
    const uint32_t tile_base = LoadGuestU32(base, header_ctx.r1.u32 + 80u);
    if (!allocated || tile_base + required_tiles > 2048u) {
      emulation.handled = true;
      emulation.result = 0;
      return emulation;
    }
    StoreGuestU32(base, object + 0u,
                  LoadGuestU32(base, object + 0u) | 0x80000000u);
    const uint32_t render_target_info = LoadGuestU32(base, object + 28u);
    StoreGuestU32(base, object + 28u,
                  (render_target_info & 0xFFFFF000u) | (tile_base & 0xFFFu));
  }

  emulation.handled = true;
  emulation.result = object;
  if (!g_logged_fable_surface_create_emulation) {
    REXLOG_INFO(
        "Refii renderer Plume resource replacement now owns D3D surface "
        "object creation for D3DDevice_CreateSurface");
    g_logged_fable_surface_create_emulation = true;
  }
  return emulation;
}

FableGuestResourceEmulation EmulateFableResourceLifetime(
    std::string_view symbol, const RendererHookArgs &args, uint8_t *base);

FableGuestResourceEmulation EmulateFableSurfaceWrapperCreate(
    std::string_view symbol, const RendererHookArgs &args, PPCContext &ctx,
    uint8_t *base) {
  FableGuestResourceEmulation emulation;
  if (!CanEmulateFableSurfaceWrapperCreate(symbol, args, base)) {
    return emulation;
  }

  const uint32_t wrapper = args.r3;
  const uint32_t existing_surface = LoadGuestU32(base, wrapper + 4u);
  if (existing_surface != 0 && IsLikelyFableObjectAddress(existing_surface)) {
    RendererHookArgs release_args = {};
    release_args.r3 = existing_surface;
    EmulateFableResourceLifetime("sub_821FC320", release_args, base);
  }
  StoreGuestU32(base, wrapper + 4u, 0u);
  StoreGuestU32(base, wrapper + 8u, 0u);

  constexpr uint32_t kSyntheticStackFrameBytes = 160u;
  const uint32_t stack = ctx.r1.u32 - kSyntheticStackFrameBytes;
  StoreGuestU32(base, stack + 80u, args.r7);
  StoreGuestU32(base, stack + 84u, 0u);
  StoreGuestU32(base, stack + 88u, 0u);
  StoreGuestU32(base, stack + 92u, 0u);

  RendererHookArgs surface_args = {};
  surface_args.r3 = LoadGuestU32(base, args.r4 + 0u);
  surface_args.r4 = LoadGuestU32(base, args.r4 + 4u);
  surface_args.r5 = FableSurfaceWrapperD3DFormat(base, args.r5);
  surface_args.r6 = args.r6;
  surface_args.r7 = stack + 80u;
  FableGuestResourceEmulation surface =
      EmulateFableSurfaceCreate("rex_lhTexture_Create", surface_args, ctx, base);
  if (surface.handled && surface.result != 0) {
    StoreGuestU32(base, wrapper + 4u, static_cast<uint32_t>(surface.result));
  }
  StoreGuestU32(base, wrapper + 8u, 1u);

  emulation.handled = true;
  emulation.result = surface.handled && surface.result != 0 ? 1u : 0u;
  if (!g_logged_fable_surface_wrapper_emulation) {
    REXLOG_INFO(
        "Refii renderer Plume resource replacement now owns Fable surface "
        "wrapper creation for Fable::CreateSurfaceWrapper/"
        "Fable::CreateTiledSurfaceWrapper");
    g_logged_fable_surface_wrapper_emulation = true;
  }
  return emulation;
}

FableGuestResourceEmulation EmulateFableRenderDepthSurfaceCtor(
    std::string_view symbol, const RendererHookArgs &args, PPCContext &ctx,
    uint8_t *base) {
  FableGuestResourceEmulation emulation;
  if (!CanEmulateFableRenderDepthSurfaceCtor(symbol, args, base)) {
    return emulation;
  }

  constexpr uint32_t kRenderDepthVTable = 0x820F6B58u;
  constexpr uint32_t kRenderDepthSurfaceVTable = 0x820F6B68u;
  constexpr uint32_t kRenderDepthWrapperVTable = 0x820F6B74u;
  constexpr uint32_t kRenderDepthWrapperTailVTable = 0x820F6B78u;
  const uint32_t object = args.r3;
  StoreGuestU32(base, object + 0u, kRenderDepthVTable);
  StoreGuestU32(base, object + 4u, 0u);
  StoreGuestU32(base, object + 8u, kRenderDepthSurfaceVTable);
  StoreGuestU32(base, object + 16u, kRenderDepthWrapperVTable);
  StoreGuestU32(base, object + 20u, 0u);
  StoreGuestU32(base, object + 24u, 0u);
  StoreGuestU32(base, object + 28u, kRenderDepthWrapperTailVTable);

  RendererHookArgs texture_args = {};
  texture_args.r3 = LoadGuestU32(base, args.r4 + 0u);
  texture_args.r4 = LoadGuestU32(base, args.r4 + 4u);
  texture_args.r5 = 1u;
  texture_args.r6 = 1u;
  texture_args.r7 = 0x201u;
  texture_args.r8 = FableSurfaceWrapperD3DFormat(base, args.r5);
  texture_args.r9 = 0u;
  texture_args.r10 = 3u;
  const FableGuestResourceEmulation texture =
      EmulateFableTextureCreate("sub_82B98B28", texture_args, ctx, base);
  if (texture.handled && texture.result != 0) {
    StoreGuestU32(base, object + 4u, static_cast<uint32_t>(texture.result));
  }

  RendererHookArgs wrapper_args = {};
  wrapper_args.r3 = object + 16u;
  wrapper_args.r4 = args.r4;
  wrapper_args.r5 = args.r5;
  wrapper_args.r6 = 0u;
  wrapper_args.r7 = 0u;
  const uint32_t format_index = LoadGuestU32(base, args.r5);
  const std::string_view wrapper_symbol =
      format_index == 54u ? "sub_82295380" : "sub_821FC8D8";
  EmulateFableSurfaceWrapperCreate(wrapper_symbol, wrapper_args, ctx, base);

  emulation.handled = true;
  emulation.result = object;
  if (!g_logged_fable_render_depth_surface_ctor_emulation) {
    REXLOG_INFO(
        "Refii renderer Plume resource replacement now owns Fable render/depth "
        "surface construction for Fable::RenderDepthSurfaceCtor");
    g_logged_fable_render_depth_surface_ctor_emulation = true;
  }
  return emulation;
}

FableGuestResourceEmulation EmulateFableRenderTargetSetup(
    std::string_view symbol, PPCContext &ctx, uint8_t *base) {
  FableGuestResourceEmulation emulation;
  if (!CanEmulateFableRenderTargetSetup(symbol, base)) {
    return emulation;
  }

  PPCContext helper_ctx = ctx;
  if (symbol == "rex_lhGfx_SetupRenderTargets") {
    // CAPTURE THE SCENE RENDER EXTENT `E` (2026-08-04) — the stated argument
    // that lets the promotion rule stop matching literal extents.
    //
    // IDA, Render_AllocateSceneRenderTargetSet 0x82A4E6D0: r3 is a pointer to
    // {width, height}, and the function allocates the WHOLE scene set from it
    // — full-res colour (format 58 when the pair equals qword_834A5FE8, 97
    // when it does not: the guest itself encodes "is this the display extent"
    // in the format index), full-res depth, then half and quarter res via
    // `*a1 / 2` @0x82A4E850 and `*a1 / 4` @0x82A4E89C. So the guest's scene
    // chain is exactly {E, E/2, E/4}.
    //
    // Read BEFORE running the guest body: this is the argument as stated.
    // NoteFableSceneRenderTargetExtent validates the pair and ignores a bad
    // one, so an unreadable argument leaves the last good E in place.
    if (ctx.r3.u32 != 0 && IsReadableGuestByteRange(base, ctx.r3.u32, 8u)) {
      NoteFableSceneRenderTargetExtent(LoadGuestU32(base, ctx.r3.u32),
                                       LoadGuestU32(base, ctx.r3.u32 + 4u));
    }
    __imp__rex_lhGfx_SetupRenderTargets(helper_ctx, base);
  } else if (symbol == "sub_82B6F040") {
    __imp__sub_82B6F040(helper_ctx, base);
  } else if (symbol == "sub_82B6F5C8") {
    __imp__sub_82B6F5C8(helper_ctx, base);
  }

  emulation.handled = true;
  emulation.result = helper_ctx.r3.u32;
  if (!g_logged_fable_render_target_setup_emulation) {
    REXLOG_INFO(
        "Refii renderer Plume resource replacement now owns Fable render "
        "target and presentation scratch setup helpers");
    g_logged_fable_render_target_setup_emulation = true;
  }
  return emulation;
}

FableGuestResourceEmulation EmulateFableSurfaceViewCreate(
    std::string_view symbol, const RendererHookArgs &args, PPCContext &ctx,
    uint8_t *base) {
  FableGuestResourceEmulation emulation;
  if (!CanEmulateFableSurfaceViewCreate(symbol, args, base)) {
    return emulation;
  }

  const uint32_t parent = args.r3;
  const bool volume_view = symbol == "sub_82B98A58";
  const uint32_t object_size = volume_view ? 32u : 48u;
  const uint32_t object = AllocateFableGuestObject(ctx, base, object_size);
  if (!IsLikelyFableObjectAddress(object) ||
      !IsLikelyGuestDwordAddress(object + object_size - sizeof(uint32_t))) {
    emulation.handled = true;
    emulation.result = 0;
    return emulation;
  }

  ZeroGuestDwords(base, object, object_size);
  constexpr uint32_t kSurfaceHeader0Base = 0x40100004u;
  StoreGuestU32(base, object + 0x00u,
                kSurfaceHeader0Base | (LoadGuestU32(base, parent) & 0x200000u));
  StoreGuestU32(base, object + 0x04u, 1u);
  StoreGuestU32(base, object + 0x14u, 0xFFFF0000u);
  StoreGuestU32(base, object + 0x18u, parent);

  uint32_t packed_level_face = 0;
  if (symbol == "sub_82B98978") {
    packed_level_face =
        ((args.r4 << 22u) & 0x0FC00000u) | ((args.r5 & 0xFu) << 28u);
  } else {
    packed_level_face = (args.r4 & 0xFu) << 28u;
  }
  StoreGuestU32(base, object + 0x1Cu, packed_level_face);

  const uint32_t ref_count = LoadGuestU32(base, parent + 0x04u);
  if (ref_count != UINT32_MAX) {
    StoreGuestU32(base, parent + 0x04u, ref_count + 1u);
  }

  emulation.handled = true;
  emulation.result = object;
  if (!g_logged_fable_surface_view_emulation) {
    REXLOG_INFO(
        "Refii renderer Plume resource replacement now owns D3D surface-view "
        "object creation for D3DTexture_GetSurfaceLevel/"
        "D3DCubeTexture_GetCubeMapSurface/D3DVolumeTexture_GetVolumeLevel");
    g_logged_fable_surface_view_emulation = true;
  }
  return emulation;
}

bool FableResourceHeaderOwnsChildRef(uint32_t header) {
  return (header & 0xFu) == 4u && (header & 0x40000000u) != 0;
}

FableGuestResourceEmulation EmulateFableResourceLifetimeInternal(
    std::string_view symbol, const RendererHookArgs &args, uint8_t *base,
    uint32_t recursion_depth) {
  FableGuestResourceEmulation emulation;
  if (!RendererReplaceResourcesEnabled() || base == nullptr ||
      !IsFableResourceLifetimeHook(symbol)) {
    return emulation;
  }
  if (!IsLikelyFableObjectAddress(args.r3)) {
    emulation.handled = true;
    emulation.result = 0;
    if (!g_logged_fable_resource_lifetime_shape) {
      REXLOG_WARN(
          "Refii renderer Plume resource adapter rejected unsupported Fable "
          "resource lifetime shape {} r3=0x{:08X}",
          symbol, args.r3);
      g_logged_fable_resource_lifetime_shape = true;
    }
    return emulation;
  }

  constexpr uint32_t kD3DResourceHeaderOffset = 0u;
  constexpr uint32_t kD3DResourceRefCountOffset = 4u;
  constexpr uint32_t kD3DResourceChildOffset = 0x18u;
  const uint32_t current_ref_count =
      LoadGuestU32(base, args.r3 + kD3DResourceRefCountOffset);
  uint32_t next_ref_count = current_ref_count;
  if (symbol == "sub_821FC178") {
    ++next_ref_count;
  } else if (symbol == "sub_821FC320") {
    --next_ref_count;
  } else {
    return emulation;
  }

  StoreGuestU32(base, args.r3 + kD3DResourceRefCountOffset, next_ref_count);

  const uint32_t header = LoadGuestU32(base, args.r3 + kD3DResourceHeaderOffset);
  const bool release = symbol == "sub_821FC320";
  const bool should_recurse_child =
      FableResourceHeaderOwnsChildRef(header) &&
      ((release && next_ref_count == 0u) ||
       (!release && next_ref_count == 1u));
  if (should_recurse_child && recursion_depth < 8u) {
    const uint32_t child =
        LoadGuestU32(base, args.r3 + kD3DResourceChildOffset);
    if (child != 0 && child != args.r3 && IsLikelyFableObjectAddress(child)) {
      RendererHookArgs child_args = {};
      child_args.r3 = child;
      EmulateFableResourceLifetimeInternal(symbol, child_args, base,
                                           recursion_depth + 1u);
    }
  }

  emulation.handled = true;
  emulation.result = next_ref_count;
  if (!g_logged_fable_resource_lifetime_emulation) {
    REXLOG_INFO(
        "Refii renderer Plume resource replacement now owns D3D resource "
        "lifetime bookkeeping for D3DResource_AddRef/D3DResource_Release");
    g_logged_fable_resource_lifetime_emulation = true;
  }
  return emulation;
}

FableGuestResourceEmulation EmulateFableResourceLifetime(
    std::string_view symbol, const RendererHookArgs &args, uint8_t *base) {
  return EmulateFableResourceLifetimeInternal(symbol, args, base, 0u);
}

bool IsLikelyGuestHeaderAddress(uint32_t guest_address, uint32_t byte_size) {
  return IsLikelyGuestDwordAddress(guest_address) &&
         byte_size != 0 && byte_size <= 256u &&
         IsLikelyGuestDwordAddress(guest_address + byte_size - sizeof(uint32_t));
}

void ClearFableGuestHeader(uint8_t *base, uint32_t guest_address,
                           uint32_t byte_size) {
  for (uint32_t offset = 0; offset < byte_size; offset += sizeof(uint32_t)) {
    StoreGuestU32(base, guest_address + offset, 0);
  }
}

FableGuestResourceEmulation EmulateFableVertexBufferHeader(
    const RendererHookArgs &args, uint8_t *base) {
  FableGuestResourceEmulation emulation;
  constexpr uint32_t kHeaderBytes = 32u;
  const uint32_t header = args.r7;
  if (!RendererReplaceResourcesEnabled() || base == nullptr ||
      !IsLikelyGuestHeaderAddress(header, kHeaderBytes)) {
    return emulation;
  }

  ClearFableGuestHeader(base, header, kHeaderBytes);
  uint32_t header0 = 1u;
  if ((args.r4 & 4u) != 0) {
    header0 = 0x00200001u;
  }
  if ((args.r4 & 0x200u) != 0) {
    header0 |= 0x00400000u;
  }
  StoreGuestU32(base, header + 0x00u, header0);
  StoreGuestU32(base, header + 0x04u, 1u);
  StoreGuestU32(base, header + 0x14u, 0xFFFF0000u);
  StoreGuestU32(base, header + 0x18u, args.r6 | 3u);
  StoreGuestU32(base, header + 0x1Cu,
                (args.r3 & 0x03FFFFFCu) | 0x10000002u);
  emulation.handled = true;
  emulation.result = header;
  return emulation;
}

FableGuestResourceEmulation EmulateFableIndexBufferHeader(
    const RendererHookArgs &args, uint8_t *base) {
  FableGuestResourceEmulation emulation;
  constexpr uint32_t kHeaderBytes = 32u;
  const uint32_t header = args.r8;
  if (!RendererReplaceResourcesEnabled() || base == nullptr ||
      !IsLikelyGuestHeaderAddress(header, kHeaderBytes)) {
    return emulation;
  }

  ClearFableGuestHeader(base, header, kHeaderBytes);
  uint32_t header0 = 2u;
  if ((args.r4 & 4u) != 0) {
    header0 = 0x00200002u;
  }
  if ((args.r4 & 0x200u) != 0) {
    header0 |= 0x00400000u;
  }
  header0 |= args.r5 << 29u;
  StoreGuestU32(base, header + 0x00u, header0);
  StoreGuestU32(base, header + 0x04u, 1u);
  StoreGuestU32(base, header + 0x14u, 0xFFFF0000u);
  StoreGuestU32(base, header + 0x18u, args.r7);
  StoreGuestU32(base, header + 0x1Cu, args.r3);
  emulation.handled = true;
  emulation.result = header;
  return emulation;
}

FableGuestResourceEmulation EmulateFableConstantBufferHeader(
    const RendererHookArgs &args, uint8_t *base) {
  FableGuestResourceEmulation emulation;
  constexpr uint32_t kHeaderBytes = 32u;
  const uint32_t header = args.r6;
  if (!RendererReplaceResourcesEnabled() || base == nullptr ||
      !IsLikelyGuestHeaderAddress(header, kHeaderBytes)) {
    return emulation;
  }

  ClearFableGuestHeader(base, header, kHeaderBytes);
  uint32_t header0 = 8u;
  if ((args.r4 & 4u) != 0) {
    header0 = 0x00200008u;
  }
  if ((args.r4 & 0x200u) != 0) {
    header0 |= 0x00400000u;
  }
  StoreGuestU32(base, header + 0x00u, header0);
  StoreGuestU32(base, header + 0x04u, 1u);
  StoreGuestU32(base, header + 0x14u, 0xFFFF0000u);
  StoreGuestU32(base, header + 0x18u, args.r5);
  StoreGuestU32(base, header + 0x1Cu, args.r3 * 16u);
  emulation.handled = true;
  emulation.result = header;
  return emulation;
}

FableGuestResourceEmulation EmulateFableResourceHeaderBuild(
    std::string_view symbol, const RendererHookArgs &args, uint8_t *base) {
  FableGuestResourceEmulation emulation;
  if (symbol == "sub_8300BD38") {
    emulation = EmulateFableVertexBufferHeader(args, base);
  } else if (symbol == "sub_8300BDD0") {
    emulation = EmulateFableIndexBufferHeader(args, base);
  } else if (symbol == "sub_8300BE60") {
    emulation = EmulateFableConstantBufferHeader(args, base);
  }
  if (emulation.handled && !g_logged_fable_resource_header_emulation) {
    REXLOG_INFO(
        "Refii renderer Plume resource replacement now owns XG buffer header "
        "builds for XGSetVertexBufferHeader/XGSetIndexBufferHeader/"
        "XGSetConstantBufferHeader");
    g_logged_fable_resource_header_emulation = true;
  }
  return emulation;
}

FableGuestResourceEmulation EmulateFableResourceUpdate(
    std::string_view symbol, const RendererHookArgs &args, uint8_t *base) {
  FableGuestResourceEmulation emulation;
  if (!IsFableResourceUpdateReplacementHook(symbol) ||
      !RendererReplaceResourcesEnabled() || base == nullptr ||
      !IsLikelyGuestHeaderAddress(args.r3, 32u)) {
    return emulation;
  }

  const uint32_t resource = args.r3;
  const uint32_t previous_header0 = LoadGuestU32(base, resource);
  StoreGuestU32(base, resource, previous_header0 - 0x100u);
  if ((previous_header0 & 0xF00u) == 0x100u) {
    constexpr uint32_t kD3DInvalidDirtyRange = 0xFFFF0000u;
    if (LoadGuestU32(base, resource + 0x14u) != kD3DInvalidDirtyRange) {
      StoreGuestU32(base, resource + 0x14u, kD3DInvalidDirtyRange);
    }
    const bool has_secondary_dirty_range =
        args.r5 != 0 ||
        (symbol == "sub_82B98950" &&
         IsLikelyGuestDwordAddress(resource + 0x30u) &&
         (LoadGuestU32(base, resource + 0x30u) & 0xFFFFF000u) != 0);
    if (has_secondary_dirty_range &&
        LoadGuestU32(base, resource + 0x18u) != kD3DInvalidDirtyRange) {
      StoreGuestU32(base, resource + 0x18u, kD3DInvalidDirtyRange);
    }
  }

  emulation.handled = true;
  emulation.result = resource;
  if (!g_logged_fable_resource_unlock_emulation) {
    REXLOG_INFO(
        "Refii renderer Plume resource replacement now owns D3D resource "
        "unlock/update bookkeeping for UnlockTextureRect, UnlockVertexBuffer, "
        "UnlockIndexBuffer, and D3D::UnlockResource");
    g_logged_fable_resource_unlock_emulation = true;
  }
  return emulation;
}

void UpdateFableLockDirtyRange(uint8_t *base, uint32_t resource,
                               uint32_t range_offset,
                               uint32_t range_base,
                               uint32_t lock_address,
                               uint32_t lock_size) {
  if (range_base == 0 || lock_address < (range_base & ~0x7Fu) ||
      lock_size == 0) {
    return;
  }

  const uint32_t relative = lock_address - (range_base & ~0x7Fu);
  const uint32_t start_block = relative >> 7u;
  const uint32_t end_block = (relative + lock_size + 0x7Fu) >> 7u;
  const uint32_t current = LoadGuestU32(base, resource + range_offset);
  const uint32_t current_start = current >> 16u;
  const uint32_t current_end = current & 0xFFFFu;
  const uint32_t next_start = std::min(current_start, start_block);
  const uint32_t next_end = std::max(current_end, end_block);
  StoreGuestU32(base, resource + range_offset,
                (next_start << 16u) | (next_end & 0xFFFFu));
}

uint32_t FableTextureFormatBytesPerPixel(
    rex::graphics::xenos::TextureFormat format) {
  using rex::graphics::xenos::TextureFormat;
  switch (format) {
  case TextureFormat::k_8:
  case TextureFormat::k_8_A:
  case TextureFormat::k_8_B:
  case TextureFormat::k_8_INTERLACED:
    return 1u;
  case TextureFormat::k_1_5_5_5:
  case TextureFormat::k_5_6_5:
  case TextureFormat::k_6_5_5:
  case TextureFormat::k_8_8:
  case TextureFormat::k_4_4_4_4:
  case TextureFormat::k_16_MPEG:
  case TextureFormat::k_16_INTERLACED:
  case TextureFormat::k_16_MPEG_INTERLACED:
    return 2u;
  case TextureFormat::k_8_8_8_8:
  case TextureFormat::k_8_8_8_8_A:
  case TextureFormat::k_2_10_10_10:
  case TextureFormat::k_10_11_11:
  case TextureFormat::k_11_11_10:
  case TextureFormat::k_16:
  case TextureFormat::k_16_FLOAT:
  case TextureFormat::k_16_16:
  case TextureFormat::k_16_16_FLOAT:
  case TextureFormat::k_32:
  case TextureFormat::k_32_FLOAT:
  case TextureFormat::k_8_8_8_8_GAMMA_EDRAM:
    return 4u;
  case TextureFormat::k_16_16_16_16:
  case TextureFormat::k_16_16_16_16_FLOAT:
  case TextureFormat::k_32_32:
  case TextureFormat::k_32_32_FLOAT:
    return 8u;
  case TextureFormat::k_32_32_32_32:
  case TextureFormat::k_32_32_32_32_FLOAT:
    return 16u;
  default:
    return 4u;
  }
}

struct FableTextureBlockLayout {
  uint32_t width = 1;
  uint32_t height = 1;
  uint32_t bytes = 0;
};

FableTextureBlockLayout FableTextureFormatBlockLayout(
    rex::graphics::xenos::TextureFormat format) {
  using rex::graphics::xenos::TextureFormat;
  switch (format) {
  case TextureFormat::k_DXT1:
  case TextureFormat::k_DXT1_AS_16_16_16_16:
  case TextureFormat::k_DXT3A:
  case TextureFormat::k_DXT3A_AS_1_1_1_1:
  case TextureFormat::k_DXT5A:
    return {.width = 4u, .height = 4u, .bytes = 8u};
  case TextureFormat::k_DXT2_3:
  case TextureFormat::k_DXT4_5:
  case TextureFormat::k_DXT2_3_AS_16_16_16_16:
  case TextureFormat::k_DXT4_5_AS_16_16_16_16:
    return {.width = 4u, .height = 4u, .bytes = 16u};
  default:
    return {.width = 1u,
            .height = 1u,
            .bytes = FableTextureFormatBytesPerPixel(format)};
  }
}

uint32_t FableD3DFormatForTextureFormat(
    uint8_t *base, rex::graphics::xenos::TextureFormat format) {
  constexpr uint32_t kFableSurfaceFormatTable = 0x8331DAD8u;
  constexpr uint32_t kFableSurfaceFormatTableEnd = 0x83320938u;
  constexpr uint32_t kFableSurfaceFormatStride = 112u;
  if (base == nullptr) {
    return 0;
  }

  const uint32_t format_low = static_cast<uint32_t>(format) & 0x3Fu;
  for (uint32_t address = kFableSurfaceFormatTable;
       address < kFableSurfaceFormatTableEnd;
       address += kFableSurfaceFormatStride) {
    if (!IsLikelyFableReadableRange(address, 4u)) {
      break;
    }
    const uint32_t d3d_format = LoadGuestU32(base, address);
    if (d3d_format == 0u || d3d_format == UINT32_MAX) {
      continue;
    }
    if ((d3d_format & 0x3Fu) == format_low) {
      return d3d_format;
    }
  }

  return 0;
}

rex::graphics::xenos::xe_gpu_texture_fetch_t LoadFableTextureFetch(
    uint8_t *base, uint32_t texture) {
  rex::graphics::xenos::xe_gpu_texture_fetch_t fetch = {};
  fetch.dword_0 = LoadGuestU32(base, texture + 28u);
  fetch.dword_1 = LoadGuestU32(base, texture + 32u);
  fetch.dword_2 = LoadGuestU32(base, texture + 36u);
  fetch.dword_3 = LoadGuestU32(base, texture + 40u);
  fetch.dword_4 = LoadGuestU32(base, texture + 44u);
  fetch.dword_5 = LoadGuestU32(base, texture + 48u);
  return fetch;
}

uint32_t FableTextureFetchWidth(
    const rex::graphics::xenos::xe_gpu_texture_fetch_t &fetch) {
  using rex::graphics::xenos::DataDimension;
  if (fetch.dimension == DataDimension::k1D) {
    return fetch.size_1d.width + 1u;
  }
  if (fetch.dimension == DataDimension::k3D) {
    return fetch.size_3d.width + 1u;
  }
  return fetch.size_2d.width + 1u;
}

uint32_t FableTextureFetchHeight(
    const rex::graphics::xenos::xe_gpu_texture_fetch_t &fetch) {
  using rex::graphics::xenos::DataDimension;
  if (fetch.dimension == DataDimension::k3D) {
    return fetch.size_3d.height + 1u;
  }
  if (fetch.dimension == DataDimension::k1D) {
    return 1u;
  }
  return fetch.size_2d.height + 1u;
}

FableGuestResourceEmulation EmulateFableBinkVideoPlaneSetup(
    std::string_view symbol, const RendererHookArgs &args, PPCContext &ctx,
    uint8_t *base) {
  FableGuestResourceEmulation emulation;
  if (!CanEmulateFableBinkVideoPlaneSetup(symbol, args, base)) {
    return emulation;
  }

  g_fable_bink_video_plane_setup_count.fetch_add(1,
                                                 std::memory_order_relaxed);

  constexpr uint32_t kHeaderStackBytes = 192u;
  constexpr uint32_t kTextureHeaderFlags = 0x04900002u;
  bink::FableBinkVideoPlaneSetupCallbacks callbacks;
  callbacks.allocate_guest_memory =
      [&](uint32_t byte_size, uint32_t flags) -> uint32_t {
    return AllocateFableGuestMemory(ctx, base, byte_size, flags);
  };
  callbacks.initialize_texture_header =
      [&](uint32_t texture, uint32_t width,
          uint32_t height) -> bink::FableBinkVideoPlaneHeaderInfo {
    PPCContext header_ctx = ctx;
    header_ctx.r1.u32 = ctx.r1.u32 - kHeaderStackBytes;
    StoreGuestU32(base, header_ctx.r1.u32 + 84u, 0u);
    StoreGuestU32(base, header_ctx.r1.u32 + 92u, texture);
    StoreGuestU32(base, header_ctx.r1.u32 + 100u, 0u);
    StoreGuestU32(base, header_ctx.r1.u32 + 108u, 0u);
    header_ctx.r3.u64 = width;
    header_ctx.r4.u64 = height;
    header_ctx.r5.u64 = 1u;
    header_ctx.r6.u64 = 4u;
    header_ctx.r7.u64 = kTextureHeaderFlags;
    header_ctx.r8.u64 = 0u;
    header_ctx.r9.u64 = 0u;
    header_ctx.r10.u64 = UINT32_MAX;
    __imp__sub_821FBB78(header_ctx, base);

    const auto fetch = LoadFableTextureFetch(base, texture);
    const uint32_t pitch_pixels =
        fetch.pitch != 0 ? (fetch.pitch << 5u) : FableTextureFetchWidth(fetch);
    const uint32_t bytes_per_pixel =
        FableTextureFormatBytesPerPixel(fetch.format);
    const uint64_t row_pitch_bytes =
        uint64_t(pitch_pixels) * uint64_t(bytes_per_pixel);
    return bink::FableBinkVideoPlaneHeaderInfo{
        .header_plane_bytes = header_ctx.r3.u32,
        .row_pitch_bytes = row_pitch_bytes <= UINT32_MAX
                               ? static_cast<uint32_t>(row_pitch_bytes)
                               : 0u,
        .texture_height = FableTextureFetchHeight(fetch),
        .bytes_per_pixel = bytes_per_pixel,
    };
  };

  const bink::FableBinkVideoPlaneSetupResult result =
      bink::EmulateFableBinkVideoPlaneSetup(args, base, callbacks);
  emulation.handled = result.handled;
  emulation.result = result.texture;
  RegisterBinkVideoPlaneBindingForPlume(result.texture, result.plane_memory,
                                        result.plane_bytes,
                                        result.row_pitch_bytes, args.r4);
  if (!g_logged_fable_bink_video_plane_emulation) {
    REXLOG_INFO(
        "Refii renderer Plume resource replacement now owns Fable Bink video "
        "plane texture allocation and cached output buffers for sub_82A42FA0");
    g_logged_fable_bink_video_plane_emulation = true;
  }
  return emulation;
}

FableGuestResourceEmulation EmulateFableBinkVideoPlaneSurfaceSetup(
    std::string_view symbol, uint8_t *base) {
  FableGuestResourceEmulation emulation;
  if (!CanEmulateFableBinkVideoPlaneSurfaceSetup(symbol, base)) {
    return emulation;
  }

  emulation.handled = bink::EmulateFableBinkVideoPlaneSurfaceSetup(base);
  if (!g_logged_fable_bink_video_plane_emulation) {
    REXLOG_INFO(
        "Refii renderer Plume resource replacement now owns Fable Bink video "
        "plane surface setup bookkeeping for sub_82A4E8D0");
    g_logged_fable_bink_video_plane_emulation = true;
  }
  return emulation;
}

struct FableTextureSurfaceLockShape {
  uint32_t texture = 0;
  uint32_t locked_rect = 0;
  uint32_t rect = 0;
};

bool TryGetFableTextureSurfaceLockShape(std::string_view symbol,
                                        const RendererHookArgs &args,
                                        uint8_t *base,
                                        FableTextureSurfaceLockShape &shape) {
  if (!RendererReplaceResourcesEnabled() || base == nullptr ||
      !IsFableTextureSurfaceLockHook(symbol)) {
    return false;
  }

  if (symbol == "sub_82B98930") {
    shape.texture = args.r3;
    shape.locked_rect = args.r5;
    shape.rect = args.r6;
  } else if (symbol == "sub_82B98C48") {
    if (!IsLikelyFableObjectRange(args.r3, 32u)) {
      return false;
    }
    shape.texture = LoadGuestU32(base, args.r3 + 24u);
    shape.locked_rect = args.r4;
    shape.rect = args.r5;
  }

  if (!IsLikelyFableObjectRange(shape.texture, 52u) ||
      !IsLikelyFableWritableDataRange(shape.locked_rect, 8u) ||
      (shape.rect != 0 && !IsLikelyFableReadableRange(shape.rect, 16u))) {
    return false;
  }

  const auto fetch = LoadFableTextureFetch(base, shape.texture);
  const uint32_t base_address = fetch.base_address << 12u;
  const FableTextureBlockLayout block_layout =
      FableTextureFormatBlockLayout(fetch.format);
  const uint32_t pitch_pixels =
      fetch.pitch != 0 ? (fetch.pitch << 5u) : FableTextureFetchWidth(fetch);
  return base_address != 0 && block_layout.bytes != 0 &&
         block_layout.width != 0 && block_layout.height != 0 &&
         pitch_pixels != 0 &&
         IsLikelyGuestMemoryAddress(base_address);
}

bool CanEmulateFableTextureSurfaceLock(std::string_view symbol,
                                       const RendererHookArgs &args,
                                       uint8_t *base) {
  FableTextureSurfaceLockShape shape;
  return TryGetFableTextureSurfaceLockShape(symbol, args, base, shape);
}

FableGuestResourceEmulation EmulateFableTextureSurfaceLock(
    std::string_view symbol, const RendererHookArgs &args, uint8_t *base) {
  FableGuestResourceEmulation emulation;
  FableTextureSurfaceLockShape shape;
  if (!TryGetFableTextureSurfaceLockShape(symbol, args, base, shape)) {
    return emulation;
  }

  const auto fetch = LoadFableTextureFetch(base, shape.texture);
  const uint32_t base_address = fetch.base_address << 12u;
  const FableTextureBlockLayout block_layout =
      FableTextureFormatBlockLayout(fetch.format);
  const uint32_t pitch_pixels =
      fetch.pitch != 0 ? (fetch.pitch << 5u) : FableTextureFetchWidth(fetch);
  const uint32_t pitch_blocks =
      (pitch_pixels + block_layout.width - 1u) / block_layout.width;
  if (pitch_blocks > UINT32_MAX / block_layout.bytes) {
    emulation.handled = true;
    emulation.result = 0;
    return emulation;
  }
  const uint32_t pitch_bytes = pitch_blocks * block_layout.bytes;
  uint32_t left = 0;
  uint32_t top = 0;
  uint32_t right = FableTextureFetchWidth(fetch);
  uint32_t bottom = FableTextureFetchHeight(fetch);
  if (shape.rect != 0) {
    left = LoadGuestU32(base, shape.rect + 0u);
    top = LoadGuestU32(base, shape.rect + 4u);
    right = LoadGuestU32(base, shape.rect + 8u);
    bottom = LoadGuestU32(base, shape.rect + 12u);
  }
  const uint32_t left_block = left / block_layout.width;
  const uint32_t top_block = top / block_layout.height;
  const uint32_t right_block =
      (right + block_layout.width - 1u) / block_layout.width;
  const uint32_t bottom_block =
      (bottom + block_layout.height - 1u) / block_layout.height;
  if (right_block <= left_block || bottom_block <= top_block ||
      right_block > pitch_blocks) {
    emulation.handled = true;
    emulation.result = 0;
    return emulation;
  }
  const uint64_t bit_offset =
      uint64_t(top_block) * pitch_bytes +
      uint64_t(left_block) * block_layout.bytes;
  if (bit_offset > UINT32_MAX - base_address) {
    emulation.handled = true;
    emulation.result = 0;
    return emulation;
  }

  const uint32_t bits = base_address + static_cast<uint32_t>(bit_offset);
  StoreGuestU32(base, shape.locked_rect + 0u, pitch_bytes);
  StoreGuestU32(base, shape.locked_rect + 4u, bits);

  const uint32_t lock_rows =
      bottom_block > top_block ? bottom_block - top_block : 1u;
  const uint32_t lock_size =
      pitch_bytes != 0 && lock_rows <= UINT32_MAX / pitch_bytes
          ? pitch_bytes * lock_rows
          : pitch_bytes;
  const uint32_t header0 = LoadGuestU32(base, shape.texture);
  UpdateFableLockDirtyRange(base, shape.texture, 0x14u, base_address, bits,
                            lock_size);
  StoreGuestU32(base, shape.texture, header0 + 0x100u);

  emulation.handled = true;
  emulation.result = bits;
  if (!g_logged_fable_texture_surface_lock_emulation) {
    REXLOG_INFO(
        "Refii renderer Plume resource replacement now owns texture/surface "
        "lock output for LockTextureRect and D3DSurface_LockRect");
    g_logged_fable_texture_surface_lock_emulation = true;
  }
  return emulation;
}

struct FableSurfacePixelView {
  uint32_t surface = 0;
  uint32_t texture = 0;
  uint32_t bits = 0;
  uint32_t base_address = 0;
  uint32_t bytes_per_pixel = 0;
  uint32_t block_width = 1;
  uint32_t block_height = 1;
  uint32_t block_bytes = 0;
  uint32_t pitch_bytes = 0;
  uint32_t row_bytes = 0;
  uint32_t rows = 0;
};

bool TryGetFableSurfacePixelView(uint8_t *base, uint32_t surface,
                                 uint32_t rect,
                                 FableSurfacePixelView &view) {
  if (base == nullptr || !IsLikelyFableObjectRange(surface, 32u) ||
      (rect != 0 && !IsLikelyFableReadableRange(rect, 16u))) {
    return false;
  }

  uint32_t texture = LoadGuestU32(base, surface + 24u);
  if (!IsLikelyFableObjectRange(texture, 52u)) {
    texture = surface;
  }
  if (!IsLikelyFableObjectRange(texture, 52u)) {
    return false;
  }

  const auto fetch = LoadFableTextureFetch(base, texture);
  const uint32_t base_address = fetch.base_address << 12u;
  const FableTextureBlockLayout block_layout =
      FableTextureFormatBlockLayout(fetch.format);
  const uint32_t width = FableTextureFetchWidth(fetch);
  const uint32_t height = FableTextureFetchHeight(fetch);
  const uint32_t pitch_pixels = fetch.pitch != 0 ? (fetch.pitch << 5u) : width;
  if (base_address == 0 || block_layout.bytes == 0 ||
      block_layout.width == 0 || block_layout.height == 0 || width == 0 ||
      height == 0 || pitch_pixels == 0 ||
      !IsLikelyGuestMemoryAddress(base_address)) {
    return false;
  }

  uint32_t left = 0;
  uint32_t top = 0;
  uint32_t right = width;
  uint32_t bottom = height;
  if (rect != 0) {
    left = std::min(LoadGuestU32(base, rect + 0u), width);
    top = std::min(LoadGuestU32(base, rect + 4u), height);
    right = std::min(LoadGuestU32(base, rect + 8u), width);
    bottom = std::min(LoadGuestU32(base, rect + 12u), height);
  }
  if (right <= left || bottom <= top) {
    return false;
  }

  const uint32_t pitch_blocks =
      (pitch_pixels + block_layout.width - 1u) / block_layout.width;
  if (pitch_blocks > UINT32_MAX / block_layout.bytes) {
    return false;
  }
  const uint32_t pitch_bytes = pitch_blocks * block_layout.bytes;
  const uint32_t left_block = left / block_layout.width;
  const uint32_t top_block = top / block_layout.height;
  const uint32_t right_block =
      (right + block_layout.width - 1u) / block_layout.width;
  const uint32_t bottom_block =
      (bottom + block_layout.height - 1u) / block_layout.height;
  if (right_block <= left_block || bottom_block <= top_block ||
      right_block > pitch_blocks) {
    return false;
  }
  const uint32_t row_blocks = right_block - left_block;
  if (row_blocks > UINT32_MAX / block_layout.bytes) {
    return false;
  }
  const uint32_t row_bytes = row_blocks * block_layout.bytes;
  const uint32_t rows = bottom_block - top_block;
  const uint64_t bit_offset =
      uint64_t(top_block) * pitch_bytes +
      uint64_t(left_block) * block_layout.bytes;
  if (bit_offset > UINT32_MAX - base_address || row_bytes > pitch_bytes) {
    return false;
  }
  const uint32_t bits = base_address + static_cast<uint32_t>(bit_offset);
  const uint64_t span =
      rows > 0 ? uint64_t(rows - 1u) * pitch_bytes + row_bytes : 0u;
  if (span == 0 || span > UINT32_MAX ||
      !IsLikelyGuestMemoryRange(bits, static_cast<uint32_t>(span))) {
    return false;
  }

  view.surface = surface;
  view.texture = texture;
  view.bits = bits;
  view.base_address = base_address;
  view.bytes_per_pixel = block_layout.width == 1u && block_layout.height == 1u
                             ? block_layout.bytes
                             : 0u;
  view.block_width = block_layout.width;
  view.block_height = block_layout.height;
  view.block_bytes = block_layout.bytes;
  view.pitch_bytes = pitch_bytes;
  view.row_bytes = row_bytes;
  view.rows = rows;
  return true;
}

struct FableSurfaceCopyShape {
  FableSurfacePixelView source;
  FableSurfacePixelView destination;
};

bool TryGetFableSurfaceCopyShape(std::string_view symbol,
                                 const RendererHookArgs &args, uint8_t *base,
                                 FableSurfaceCopyShape &shape) {
  if (!RendererReplaceResourcesEnabled() || base == nullptr ||
      !IsFableSurfaceCopyHook(symbol)) {
    return false;
  }

  uint32_t source_surface = 0;
  uint32_t destination_surface = 0;
  uint32_t source_rect = 0;
  uint32_t destination_rect = 0;
  if (symbol == "sub_82D21C68") {
    source_surface = args.r3;
    destination_surface = args.r4;
  } else if (symbol == "sub_82D160D8") {
    destination_surface = args.r3;
    destination_rect = args.r5;
    source_surface = args.r6;
    source_rect = args.r8;
  }

  return TryGetFableSurfacePixelView(base, source_surface, source_rect,
                                     shape.source) &&
         TryGetFableSurfacePixelView(base, destination_surface,
                                     destination_rect, shape.destination);
}

bool CanEmulateFableSurfaceCopy(std::string_view symbol,
                                const RendererHookArgs &args, uint8_t *base) {
  FableSurfaceCopyShape shape;
  return TryGetFableSurfaceCopyShape(symbol, args, base, shape);
}

FableGuestResourceEmulation EmulateFableSurfaceCopy(
    std::string_view symbol, const RendererHookArgs &args, uint8_t *base) {
  FableGuestResourceEmulation emulation;
  FableSurfaceCopyShape shape;
  if (!TryGetFableSurfaceCopyShape(symbol, args, base, shape)) {
    if (RendererReplaceResourcesEnabled() && base != nullptr &&
        IsFableSurfaceCopyHook(symbol)) {
      emulation.handled = true;
      emulation.result = 0;
      if (!g_logged_fable_surface_copy_shape) {
        REXLOG_WARN(
            "Refii renderer Plume resource adapter rejected unsupported Fable "
            "D3DX surface copy shape {} r3=0x{:08X} "
            "r4=0x{:08X} r5=0x{:08X} r6=0x{:08X} r8=0x{:08X}",
            symbol, args.r3, args.r4, args.r5, args.r6, args.r8);
        g_logged_fable_surface_copy_shape = true;
      }
    }
    return emulation;
  }

  const uint32_t rows = std::min(shape.source.rows, shape.destination.rows);
  const uint32_t row_bytes =
      std::min(shape.source.row_bytes, shape.destination.row_bytes);
  uint8_t *const source_base =
      base + shape.source.bits + GuestPhysicalHostOffset(shape.source.bits);
  uint8_t *const destination_base =
      base + shape.destination.bits +
      GuestPhysicalHostOffset(shape.destination.bits);
  for (uint32_t row = 0; row < rows; ++row) {
    const uint64_t source_offset = uint64_t(row) * shape.source.pitch_bytes;
    const uint64_t destination_offset =
        uint64_t(row) * shape.destination.pitch_bytes;
    if (source_offset > UINT32_MAX - shape.source.bits ||
        destination_offset > UINT32_MAX - shape.destination.bits ||
        !IsReadableGuestByteRange(
            base, shape.source.bits + static_cast<uint32_t>(source_offset),
            row_bytes) ||
        !IsReadableGuestByteRange(
            base,
            shape.destination.bits +
                static_cast<uint32_t>(destination_offset),
            row_bytes)) {
      return emulation;
    }

    std::memmove(destination_base + destination_offset,
                 source_base + source_offset, row_bytes);
  }

  const uint32_t dirty_size =
      rows > 0 && shape.destination.pitch_bytes <=
                      UINT32_MAX / std::max<uint32_t>(rows, 1u)
          ? shape.destination.pitch_bytes * rows
          : row_bytes;
  UpdateFableLockDirtyRange(base, shape.destination.texture, 0x14u,
                            shape.destination.base_address,
                            shape.destination.bits, dirty_size);

  emulation.handled = true;
  emulation.result = 0;
  if (!g_logged_fable_surface_copy_emulation) {
    REXLOG_INFO(
        "Refii renderer Plume resource replacement now owns D3DX surface "
        "copy for D3DXLoadSurfaceFromSurface and D3DXTex::InternalCopy");
    g_logged_fable_surface_copy_emulation = true;
  }
  return emulation;
}

struct FableSurfaceMemoryCopyShape {
  FableSurfacePixelView destination;
  uint32_t source_bits = 0;
  uint32_t source_pitch_bytes = 0;
  uint32_t source_row_bytes = 0;
  uint32_t rows = 0;
};

bool GuestRowRangeHasNonZero(uint8_t *base, uint32_t bits,
                             uint32_t pitch_bytes, uint32_t row_bytes,
                             uint32_t rows) {
  if (base == nullptr || bits == 0 || pitch_bytes == 0 || row_bytes == 0 ||
      rows == 0) {
    return false;
  }

  constexpr uint32_t kMaxProbeBytes = 4u * 1024u * 1024u;
  uint32_t probed_bytes = 0;
  for (uint32_t row = 0; row < rows && probed_bytes < kMaxProbeBytes; ++row) {
    const uint64_t row_offset = uint64_t(row) * pitch_bytes;
    if (row_offset > UINT32_MAX - bits) {
      return false;
    }
    const uint32_t row_bits = bits + static_cast<uint32_t>(row_offset);
    if (!IsReadableGuestByteRange(base, row_bits, row_bytes)) {
      return false;
    }

    const uint8_t *const row_base =
        base + row_bits + GuestPhysicalHostOffset(row_bits);
    const uint32_t scan_bytes =
        std::min<uint32_t>(row_bytes, kMaxProbeBytes - probed_bytes);
    for (uint32_t i = 0; i < scan_bytes; ++i) {
      if (row_base[i] != 0) {
        return true;
      }
    }
    probed_bytes += scan_bytes;
  }
  return false;
}

bool CopyFableSurfaceMemoryRowsIfDestinationIsBlank(
    uint8_t *base, const FableSurfaceMemoryCopyShape &shape) {
  const uint32_t rows = shape.rows;
  const uint32_t row_bytes =
      std::min(shape.source_row_bytes, shape.destination.row_bytes);
  if (rows == 0 || row_bytes == 0) {
    return false;
  }

  if (!GuestRowRangeHasNonZero(base, shape.source_bits,
                               shape.source_pitch_bytes, row_bytes, rows) ||
      GuestRowRangeHasNonZero(base, shape.destination.bits,
                              shape.destination.pitch_bytes, row_bytes,
                              rows)) {
    return false;
  }

  uint8_t *const source_base =
      base + shape.source_bits + GuestPhysicalHostOffset(shape.source_bits);
  uint8_t *const destination_base =
      base + shape.destination.bits +
      GuestPhysicalHostOffset(shape.destination.bits);
  for (uint32_t row = 0; row < rows; ++row) {
    const uint64_t source_offset = uint64_t(row) * shape.source_pitch_bytes;
    const uint64_t destination_offset =
        uint64_t(row) * shape.destination.pitch_bytes;
    if (source_offset > UINT32_MAX - shape.source_bits ||
        destination_offset > UINT32_MAX - shape.destination.bits ||
        !IsReadableGuestByteRange(
            base, shape.source_bits + static_cast<uint32_t>(source_offset),
            row_bytes) ||
        !IsReadableGuestByteRange(
            base,
            shape.destination.bits +
                static_cast<uint32_t>(destination_offset),
            row_bytes)) {
      return false;
    }

    std::memmove(destination_base + destination_offset,
                 source_base + source_offset, row_bytes);
  }
  return true;
}

bool TryGetFableSurfaceMemoryCopyShape(std::string_view symbol,
                                       const RendererHookArgs &args,
                                       uint8_t *base,
                                       FableSurfaceMemoryCopyShape &shape) {
  if (!RendererReplaceResourcesEnabled() || base == nullptr ||
      !IsFableSurfaceMemoryCopyHook(symbol) || args.r6 == 0 ||
      args.r8 == 0) {
    return false;
  }

  if (!TryGetFableSurfacePixelView(base, args.r3, args.r5,
                                   shape.destination)) {
    return false;
  }

  uint32_t source_left = 0;
  uint32_t source_top = 0;
  uint32_t source_right = shape.destination.row_bytes;
  uint32_t source_bottom = shape.destination.rows;
  if (args.r10 != 0) {
    if (!IsLikelyFableReadableRange(args.r10, 16u)) {
      return false;
    }
    source_left = LoadGuestU32(base, args.r10 + 0u);
    source_top = LoadGuestU32(base, args.r10 + 4u);
    source_right = LoadGuestU32(base, args.r10 + 8u);
    source_bottom = LoadGuestU32(base, args.r10 + 12u);
  }

  const uint32_t source_width =
      source_right > source_left ? source_right - source_left : 0u;
  const uint32_t source_rows =
      source_bottom > source_top ? source_bottom - source_top : 0u;
  const uint32_t source_left_block =
      source_left / shape.destination.block_width;
  const uint32_t source_top_block =
      source_top / shape.destination.block_height;
  const uint32_t source_right_block =
      (source_left + source_width + shape.destination.block_width - 1u) /
      shape.destination.block_width;
  const uint32_t source_bottom_block =
      (source_top + source_rows + shape.destination.block_height - 1u) /
      shape.destination.block_height;
  if (source_right_block <= source_left_block ||
      source_bottom_block <= source_top_block) {
    return false;
  }
  const uint64_t source_row_bytes =
      uint64_t(source_right_block - source_left_block) *
      uint64_t(shape.destination.block_bytes);
  const uint32_t row_bytes =
      args.r10 != 0
          ? std::min<uint32_t>(shape.destination.row_bytes,
                               source_row_bytes <= UINT32_MAX
                                   ? static_cast<uint32_t>(source_row_bytes)
                                   : UINT32_MAX)
          : shape.destination.row_bytes;
  const uint32_t rows =
      args.r10 != 0 ? std::min<uint32_t>(
                          shape.destination.rows,
                          source_bottom_block - source_top_block)
                    : shape.destination.rows;
  if (row_bytes == 0 || rows == 0 || args.r8 < row_bytes) {
    return false;
  }

  const uint64_t source_offset =
      uint64_t(source_top_block) * args.r8 +
      uint64_t(source_left_block) * shape.destination.block_bytes;
  if (source_offset > UINT32_MAX - args.r6) {
    return false;
  }

  const uint32_t source_bits = args.r6 + static_cast<uint32_t>(source_offset);
  const uint64_t source_span =
      uint64_t(rows - 1u) * args.r8 + uint64_t(row_bytes);
  if (source_span == 0 || source_span > UINT32_MAX ||
      !IsLikelyGuestMemoryRange(source_bits, static_cast<uint32_t>(source_span))) {
    return false;
  }

  shape.source_bits = source_bits;
  shape.source_pitch_bytes = args.r8;
  shape.source_row_bytes = row_bytes;
  shape.rows = rows;
  return true;
}

bool CanEmulateFableSurfaceMemoryCopy(std::string_view symbol,
                                      const RendererHookArgs &args,
                                      uint8_t *base) {
  (void)args;
  return RendererReplaceResourcesEnabled() && base != nullptr &&
         IsFableSurfaceMemoryCopyHook(symbol);
}

FableGuestResourceEmulation EmulateFableSurfaceMemoryCopy(
    std::string_view symbol, const RendererHookArgs &args, PPCContext &ctx,
    uint8_t *base) {
  FableGuestResourceEmulation emulation;
  if (!RendererReplaceResourcesEnabled() || base == nullptr ||
      !IsFableSurfaceMemoryCopyHook(symbol)) {
    return emulation;
  }

  PPCContext helper_ctx = ctx;
  __imp__sub_82D14B20(helper_ctx, base);
  emulation.handled = true;
  emulation.result = helper_ctx.r3.u32;
  if (!g_logged_fable_surface_memory_copy_emulation) {
    REXLOG_INFO(
        "Refii renderer Plume resource replacement now owns native "
        "D3DXLoadSurfaceFromMemory CPU-to-surface uploads");
    g_logged_fable_surface_memory_copy_emulation = true;
  }

  FableSurfaceMemoryCopyShape shape;
  if (!TryGetFableSurfaceMemoryCopyShape(symbol, args, base, shape)) {
    if (!g_logged_fable_surface_memory_copy_shape) {
      REXLOG_INFO(
          "Refii renderer Plume D3DX surface upload adapter handled shape "
          "outside linear dirty-range model {} r3=0x{:08X} r5=0x{:08X} "
          "r6=0x{:08X} "
          "r8=0x{:08X} r10=0x{:08X} result=0x{:08X}",
          symbol, args.r3, args.r5, args.r6, args.r8, args.r10,
          emulation.result);
      g_logged_fable_surface_memory_copy_shape = true;
    }
    return emulation;
  }

  const bool copied_linear_rows =
      CopyFableSurfaceMemoryRowsIfDestinationIsBlank(base, shape);
  const uint32_t dirty_size =
      shape.rows > 0 &&
              shape.destination.pitch_bytes <= UINT32_MAX / shape.rows
          ? shape.destination.pitch_bytes * shape.rows
          : shape.source_row_bytes;
  UpdateFableLockDirtyRange(base, shape.destination.texture, 0x14u,
                            shape.destination.base_address,
                            shape.destination.bits, dirty_size);
  if (copied_linear_rows &&
      !g_logged_fable_surface_memory_copy_linear_path) {
    REXLOG_INFO(
        "Refii renderer copied D3DXLoadSurfaceFromMemory linear rows after "
        "blank native destination dst_surface=0x{:08X} texture=0x{:08X} "
        "dst_bits=0x{:08X} src_bits=0x{:08X} rows={} row_bytes={} "
        "dst_pitch={} src_pitch={}",
        shape.destination.surface, shape.destination.texture,
        shape.destination.bits, shape.source_bits, shape.rows,
        shape.source_row_bytes, shape.destination.pitch_bytes,
        shape.source_pitch_bytes);
    g_logged_fable_surface_memory_copy_linear_path = true;
  }
  return emulation;
}

bool CanEmulateFableTextureFill(std::string_view symbol,
                                const RendererHookArgs &args, uint8_t *base) {
  if (!RendererReplaceResourcesEnabled() || base == nullptr ||
      !IsFableTextureFillHook(symbol) || args.r4 != 0x82BA2150u ||
      !IsLikelyFableObjectRange(args.r3, 52u)) {
    return false;
  }

  const auto fetch = LoadFableTextureFetch(base, args.r3);
  const uint32_t width = FableTextureFetchWidth(fetch);
  const uint32_t height = FableTextureFetchHeight(fetch);
  const uint32_t base_address = fetch.base_address << 12u;
  return width == 1u && height == 1u &&
         base_address != 0 && IsLikelyGuestMemoryRange(base_address, 1u);
}

uint32_t FableTextureFillBlockBytes(
    rex::graphics::xenos::TextureFormat format) {
  using rex::graphics::xenos::TextureFormat;
  switch (format) {
  case TextureFormat::k_DXT1:
  case TextureFormat::k_DXT1_AS_16_16_16_16:
  case TextureFormat::k_DXT3A:
  case TextureFormat::k_DXT3A_AS_1_1_1_1:
  case TextureFormat::k_DXT5A:
    return 8u;
  case TextureFormat::k_DXT2_3:
  case TextureFormat::k_DXT4_5:
  case TextureFormat::k_DXT2_3_AS_16_16_16_16:
  case TextureFormat::k_DXT4_5_AS_16_16_16_16:
    return 16u;
  default:
    return FableTextureFormatBytesPerPixel(format);
  }
}

bool CanEmulateFableTextureFileCreate(std::string_view symbol,
                                      const RendererHookArgs &args,
                                      uint8_t *base) {
  constexpr uint32_t kMaxTextureFileBytes = 64u * 1024u * 1024u;
  return RendererReplaceResourcesEnabled() && base != nullptr &&
         IsFableTextureFileCreateHook(symbol) && args.r3 != 0 &&
         args.r4 != 0 && args.r5 != 0 && args.r5 <= kMaxTextureFileBytes &&
         IsLikelyGuestMemoryRange(args.r4, args.r5);
}

FableGuestResourceEmulation EmulateFableTextureFill(
    std::string_view symbol, const RendererHookArgs &args, uint8_t *base) {
  FableGuestResourceEmulation emulation;
  if (!CanEmulateFableTextureFill(symbol, args, base)) {
    return emulation;
  }

  const auto fetch = LoadFableTextureFetch(base, args.r3);
  const uint32_t base_address = fetch.base_address << 12u;
  const uint32_t fill_bytes = FableTextureFillBlockBytes(fetch.format);
  if (!IsReadableGuestByteRange(base, base_address, fill_bytes)) {
    return emulation;
  }

  uint8_t *const destination =
      base + base_address + GuestPhysicalHostOffset(base_address);
  std::memset(destination, 0xFF, fill_bytes);
  using rex::graphics::xenos::TextureFormat;
  switch (fetch.format) {
  case TextureFormat::k_DXT1:
  case TextureFormat::k_DXT1_AS_16_16_16_16:
    // BC1: two white 565 endpoints, all selector bits choosing endpoint 0.
    destination[4] = destination[5] = destination[6] = destination[7] = 0;
    break;
  case TextureFormat::k_DXT2_3:
  case TextureFormat::k_DXT2_3_AS_16_16_16_16:
    // BC2: explicit alpha all 1, then the same white color block as BC1.
    destination[12] = destination[13] = destination[14] = destination[15] = 0;
    break;
  case TextureFormat::k_DXT4_5:
  case TextureFormat::k_DXT4_5_AS_16_16_16_16:
    // BC3: alpha endpoints 1/1, zero alpha selectors, white color endpoints.
    std::memset(destination + 2, 0, 6);
    destination[12] = destination[13] = destination[14] = destination[15] = 0;
    break;
  case TextureFormat::k_DXT3A:
  case TextureFormat::k_DXT3A_AS_1_1_1_1:
    // DXT3A is alpha-only, so 0xFF in every nibble is already full white.
    break;
  case TextureFormat::k_DXT5A:
    // BC4/DXT5A: endpoints 1/1, zero selectors.
    std::memset(destination + 2, 0, 6);
    break;
  default:
    break;
  }
  UpdateFableLockDirtyRange(base, args.r3, 0x14u, base_address, base_address,
                            fill_bytes);
  StoreGuestU32(base, args.r3, LoadGuestU32(base, args.r3) + 0x100u);

  emulation.handled = true;
  emulation.result = 0;
  if (!g_logged_fable_texture_fill_emulation) {
    REXLOG_INFO(
        "Refii renderer Plume resource replacement now owns the "
        "D3DXFillTexture 1x1 white texture upload path");
    g_logged_fable_texture_fill_emulation = true;
  }
  return emulation;
}

FableGuestResourceEmulation EmulateFableTextureFileCreate(
    std::string_view symbol, const RendererHookArgs &args, PPCContext &ctx,
    uint8_t *base) {
  FableGuestResourceEmulation emulation;
  if (!CanEmulateFableTextureFileCreate(symbol, args, base)) {
    return emulation;
  }

  PPCContext helper_ctx = ctx;
  __imp__sub_82D17250(helper_ctx, base);

  emulation.handled = true;
  emulation.result = helper_ctx.r3.u32;
  if (!g_logged_fable_texture_file_create_emulation) {
    REXLOG_INFO(
        "Refii renderer Plume resource replacement now owns "
        "CreateTextureFromFileInMemoryEx decode/create/upload sequencing");
    g_logged_fable_texture_file_create_emulation = true;
  }
  return emulation;
}

struct FableResourceQueryDesc {
  uint32_t resource = 0;
  uint32_t width = 0;
  uint32_t height = 0;
  uint32_t depth = 1;
  uint32_t format = 0;
  uint32_t multisample = 0;
};

uint32_t RotateLeft32(uint32_t value, uint32_t shift) {
  shift &= 31u;
  return shift == 0 ? value : (value << shift) | (value >> (32u - shift));
}

uint32_t ScaleFableMipDimension(uint32_t value, uint32_t level) {
  return std::max<uint32_t>(1u, value >> std::min<uint32_t>(level, 31u));
}

bool TryGetFableTextureQueryDesc(uint8_t *base, uint32_t texture,
                                 uint32_t level,
                                 FableResourceQueryDesc &desc) {
  constexpr uint32_t kMaxTextureDimension = 8192u;
  if (base == nullptr || !IsLikelyFableObjectRange(texture, 52u)) {
    return false;
  }

  const auto fetch = LoadFableTextureFetch(base, texture);
  const uint32_t width = FableTextureFetchWidth(fetch);
  const uint32_t height = FableTextureFetchHeight(fetch);
  const uint32_t depth = fetch.dimension == rex::graphics::xenos::DataDimension::k3D
                             ? fetch.size_3d.depth + 1u
                             : 1u;
  if (width == 0 || height == 0 || depth == 0 ||
      width > kMaxTextureDimension || height > kMaxTextureDimension ||
      depth > kMaxTextureDimension) {
    return false;
  }

  desc.resource = texture;
  desc.width = ScaleFableMipDimension(width, level);
  desc.height = ScaleFableMipDimension(height, level);
  desc.depth = ScaleFableMipDimension(depth, level);
  desc.format = FableD3DFormatForTextureFormat(base, fetch.format);
  if (desc.format == 0u) {
    desc.format = LoadGuestU32(base, texture + 40u);
  }
  return true;
}

bool TryGetFableSurfaceQueryDesc(uint8_t *base, uint32_t surface,
                                 FableResourceQueryDesc &desc) {
  constexpr uint32_t kMaxSurfaceDimension = 8192u;
  if (base == nullptr || !IsLikelyFableObjectRange(surface, 32u)) {
    return false;
  }

  const uint32_t header = LoadGuestU32(base, surface + 0u);
  const uint32_t parent = LoadGuestU32(base, surface + 24u);
  if ((header & 0x40000000u) != 0 &&
      TryGetFableTextureQueryDesc(base, parent,
                                  (LoadGuestU32(base, surface + 28u) >> 28u) &
                                      0xFu,
                                  desc)) {
    desc.resource = surface;
    return true;
  }

  if (TryGetFableTextureQueryDesc(base, surface, 0, desc)) {
    return true;
  }

  if (!IsLikelyFableObjectRange(surface, 48u)) {
    return false;
  }
  const uint32_t packed_extent = LoadGuestU32(base, surface + 36u);
  const uint32_t width = (RotateLeft32(packed_extent, 14u) & 0x3FFFu) + 1u;
  const uint32_t height = (RotateLeft32(packed_extent, 29u) & 0x7FFFu) + 1u;
  if (width == 0 || height == 0 || width > kMaxSurfaceDimension ||
      height > kMaxSurfaceDimension) {
    return false;
  }
  desc.resource = surface;
  desc.width = width;
  desc.height = height;
  desc.depth = 1u;
  desc.format = LoadGuestU32(base, surface + 40u);
  desc.multisample = LoadGuestU16(base, surface + 24u) & 0x3u;
  return true;
}

void StoreFableSurfaceDesc(uint8_t *base, uint32_t desc_address,
                           const FableResourceQueryDesc &desc) {
  StoreGuestU32(base, desc_address + 0u, desc.format);
  StoreGuestU32(base, desc_address + 4u, 4u);
  StoreGuestU32(base, desc_address + 8u, 0u);
  StoreGuestU32(base, desc_address + 12u, 0u);
  StoreGuestU32(base, desc_address + 16u, desc.multisample);
  StoreGuestU32(base, desc_address + 20u, 0u);
  StoreGuestU32(base, desc_address + 24u, desc.width);
  StoreGuestU32(base, desc_address + 28u, desc.height);
}

uint32_t FableResourceTypeFromTexture(uint8_t *base, uint32_t texture) {
  if (base == nullptr || !IsLikelyFableObjectRange(texture, 52u)) {
    return 0;
  }

  const uint32_t header = LoadGuestU32(base, texture + 0u);
  uint32_t resource_type = header & 0xFu;
  if (resource_type == 3u) {
    const uint32_t flags =
        RotateLeft32(LoadGuestU32(base, texture + 48u), 23u) & 0x3u;
    if (flags == 3u) {
      return 18u;
    }
    if (flags == 2u) {
      return 17u;
    }
    if (flags == 0u) {
      return 20u;
    }
    if ((LoadGuestU32(base, texture + 32u) & 0x400u) != 0) {
      return 19u;
    }
  } else if (resource_type == 4u && (header & 0x40000000u) != 0) {
    const uint32_t parent = LoadGuestU32(base, texture + 24u);
    if (IsLikelyFableObjectRange(parent, 52u) &&
        (LoadGuestU32(base, parent + 48u) & 0x600u) == 0x400u) {
      return 16u;
    }
  }

  return resource_type;
}

void StoreFableVolumeDesc(uint8_t *base, uint32_t desc_address,
                          const FableResourceQueryDesc &desc,
                          uint32_t resource_type) {
  StoreGuestU32(base, desc_address + 0u, desc.format);
  StoreGuestU32(base, desc_address + 4u, resource_type);
  StoreGuestU32(base, desc_address + 8u, 0u);
  StoreGuestU32(base, desc_address + 12u, 0u);
  StoreGuestU32(base, desc_address + 16u, desc.width);
  StoreGuestU32(base, desc_address + 20u, desc.height);
  StoreGuestU32(base, desc_address + 24u, desc.depth);
}

void StoreFableLevelSize(uint8_t *base, const RendererHookArgs &args,
                         const FableResourceQueryDesc &desc,
                         rex::graphics::xenos::TextureFormat format) {
  const FableTextureBlockLayout block_layout =
      FableTextureFormatBlockLayout(format);
  const uint32_t width_blocks =
      (std::max<uint32_t>(1u, desc.width) + block_layout.width - 1u) /
      block_layout.width;
  const uint32_t height_blocks =
      (std::max<uint32_t>(1u, desc.height) + block_layout.height - 1u) /
      block_layout.height;
  const uint64_t row_bytes64 =
      static_cast<uint64_t>(width_blocks) *
      std::max<uint32_t>(1u, block_layout.bytes);
  const uint32_t row_bytes =
      row_bytes64 > UINT32_MAX ? UINT32_MAX : static_cast<uint32_t>(row_bytes64);
  const uint64_t slice_bytes64 =
      static_cast<uint64_t>(row_bytes) * height_blocks;
  const uint32_t slice_bytes = slice_bytes64 > UINT32_MAX
                                   ? UINT32_MAX
                                   : static_cast<uint32_t>(slice_bytes64);
  const uint64_t total_bytes64 =
      static_cast<uint64_t>(slice_bytes) * std::max<uint32_t>(1u, desc.depth);
  const uint32_t total_bytes = total_bytes64 > UINT32_MAX
                                   ? UINT32_MAX
                                   : static_cast<uint32_t>(total_bytes64);

  StoreGuestU32(base, args.r5, desc.width);
  StoreGuestU32(base, args.r6, desc.height);
  StoreGuestU32(base, args.r7, desc.depth);
  StoreGuestU32(base, args.r8, slice_bytes);
  StoreGuestU32(base, args.r9, total_bytes);
}

bool CanEmulateFableResourceQuery(std::string_view symbol,
                                  const RendererHookArgs &args,
                                  uint8_t *base) {
  if (!RendererReplaceResourcesEnabled() || base == nullptr ||
      !IsFableResourceQueryHook(symbol)) {
    return false;
  }
  if (symbol == "sub_82B98C88") {
    return IsLikelyFableObjectRange(args.r3, 32u) &&
           IsLikelyFableObjectAddress(LoadGuestU32(base, args.r3 + 24u));
  }
  if (symbol == "sub_821FAB38") {
    FableResourceQueryDesc desc;
    return IsLikelyFableWritableDataRange(args.r4, 4u) &&
           IsLikelyFableWritableDataRange(args.r5, 4u) &&
           IsLikelyFableWritableDataRange(args.r6, 4u) &&
           TryGetFableTextureQueryDesc(base, args.r3, 0, desc);
  }
  if (symbol == "sub_821FA070") {
    FableResourceQueryDesc desc;
    return IsLikelyFableWritableDataRange(args.r4, 32u) &&
           TryGetFableSurfaceQueryDesc(base, args.r3, desc);
  }
  if (symbol == "sub_821FABD0") {
    FableResourceQueryDesc desc;
    return IsLikelyFableWritableDataRange(args.r5, 32u) &&
           TryGetFableTextureQueryDesc(base, args.r3, args.r4, desc);
  }
  if (symbol == "sub_82B985C8") {
    FableResourceQueryDesc desc;
    return IsLikelyFableWritableDataRange(args.r5, 28u) &&
           TryGetFableTextureQueryDesc(base, args.r3, args.r4, desc);
  }
  if (symbol == "sub_821FAD60") {
    FableResourceQueryDesc desc;
    return IsLikelyFableWritableDataRange(args.r5, 4u) &&
           IsLikelyFableWritableDataRange(args.r6, 4u) &&
           IsLikelyFableWritableDataRange(args.r7, 4u) &&
           IsLikelyFableWritableDataRange(args.r8, 4u) &&
           IsLikelyFableWritableDataRange(args.r9, 4u) &&
           TryGetFableTextureQueryDesc(base, args.r3, args.r4, desc);
  }
  return false;
}

FableGuestResourceEmulation EmulateFableResourceQuery(
    std::string_view symbol, const RendererHookArgs &args, uint8_t *base) {
  FableGuestResourceEmulation emulation;
  // Each branch validates its own shape inline — the exact conditions of
  // CanEmulateFableResourceQuery — and returns un-handled on mismatch. The
  // old Can-then-emulate split decoded the query desc twice per call (and
  // three times through the suppressed-hook ladder), a measured slice of
  // Get2DSurfaceDesc's 61us/call (run 462). Gate callers still use
  // CanEmulateFableResourceQuery; this body no longer re-runs it.
  if (!RendererReplaceResourcesEnabled() || base == nullptr) {
    return emulation;
  }

  if (symbol == "sub_82B98C88") {
    if (!IsLikelyFableObjectRange(args.r3, 32u)) {
      return emulation;
    }
    const uint32_t container = LoadGuestU32(base, args.r3 + 24u);
    if (!IsLikelyFableObjectAddress(container)) {
      return emulation;
    }
    emulation.result = container;
  } else if (symbol == "sub_821FAB38") {
    FableResourceQueryDesc desc;
    if (!IsLikelyFableWritableDataRange(args.r4, 4u) ||
        !IsLikelyFableWritableDataRange(args.r5, 4u) ||
        !IsLikelyFableWritableDataRange(args.r6, 4u) ||
        !TryGetFableTextureQueryDesc(base, args.r3, 0, desc)) {
      return emulation;
    }
    StoreGuestU32(base, args.r4, desc.width);
    StoreGuestU32(base, args.r5, desc.height);
    StoreGuestU32(base, args.r6, desc.depth);
    emulation.result = args.r3;
  } else if (symbol == "sub_821FA070") {
    FableResourceQueryDesc desc;
    if (!IsLikelyFableWritableDataRange(args.r4, 32u) ||
        !TryGetFableSurfaceQueryDesc(base, args.r3, desc)) {
      return emulation;
    }
    StoreFableSurfaceDesc(base, args.r4, desc);
    emulation.result = args.r3;
  } else if (symbol == "sub_821FABD0") {
    FableResourceQueryDesc desc;
    if (!IsLikelyFableWritableDataRange(args.r5, 32u) ||
        !TryGetFableTextureQueryDesc(base, args.r3, args.r4, desc)) {
      return emulation;
    }
    StoreFableSurfaceDesc(base, args.r5, desc);
    emulation.result = args.r3;
  } else if (symbol == "sub_82B985C8") {
    FableResourceQueryDesc desc;
    if (!IsLikelyFableWritableDataRange(args.r5, 28u) ||
        !TryGetFableTextureQueryDesc(base, args.r3, args.r4, desc)) {
      return emulation;
    }
    StoreFableVolumeDesc(base, args.r5, desc,
                         FableResourceTypeFromTexture(base, args.r3));
    emulation.result = args.r3;
  } else if (symbol == "sub_821FAD60") {
    FableResourceQueryDesc desc;
    if (!IsLikelyFableWritableDataRange(args.r5, 4u) ||
        !IsLikelyFableWritableDataRange(args.r6, 4u) ||
        !IsLikelyFableWritableDataRange(args.r7, 4u) ||
        !IsLikelyFableWritableDataRange(args.r8, 4u) ||
        !IsLikelyFableWritableDataRange(args.r9, 4u) ||
        !TryGetFableTextureQueryDesc(base, args.r3, args.r4, desc)) {
      return emulation;
    }
    StoreFableLevelSize(base, args, desc,
                        LoadFableTextureFetch(base, args.r3).format);
    emulation.result = args.r3;
  } else {
    return emulation;
  }

  emulation.handled = true;
  if (!g_logged_fable_resource_query_emulation) {
    REXLOG_INFO(
        "Refii renderer Plume resource replacement now owns D3D resource "
        "queries for surface containers, texture dimensions, surface/volume "
        "descriptions, and level sizes");
    g_logged_fable_resource_query_emulation = true;
  }
  return emulation;
}

uint32_t FableTextureMipLevelCount(uint8_t *base, uint32_t texture) {
  if (base == nullptr || !IsLikelyFableObjectRange(texture, 52u)) {
    return 1u;
  }
  return (RotateLeft32(LoadGuestU32(base, texture + 44u), 26u) & 0xFu) + 1u;
}

bool CanEmulateFableTextureAddressOffset(std::string_view symbol,
                                         const RendererHookArgs &args,
                                         uint8_t *base) {
  return RendererReplaceResourcesEnabled() && base != nullptr &&
         IsFableTextureAddressOffsetHook(symbol) &&
         IsLikelyFableObjectRange(args.r3, 52u);
}

FableGuestResourceEmulation EmulateFableTextureAddressOffset(
    std::string_view symbol, const RendererHookArgs &args, uint8_t *base) {
  FableGuestResourceEmulation emulation;
  if (!CanEmulateFableTextureAddressOffset(symbol, args, base)) {
    return emulation;
  }

  auto offset_fetch_base = [&](uint32_t dword_offset, uint32_t byte_offset) {
    const uint32_t current = LoadGuestU32(base, args.r3 + dword_offset);
    const uint32_t updated =
        ((current & 0xFFFFF000u) + byte_offset) | (current & 0xFFFu);
    StoreGuestU32(base, args.r3 + dword_offset, updated);
  };
  offset_fetch_base(32u, args.r4);
  if (FableTextureMipLevelCount(base, args.r3) > 1u) {
    offset_fetch_base(48u, args.r5);
  }

  emulation.handled = true;
  emulation.result = args.r3;
  if (!g_logged_fable_texture_offset_emulation) {
    REXLOG_INFO(
        "Refii renderer Plume resource replacement now owns "
        "XGOffsetBaseTextureAddress texture fetch address mutation");
    g_logged_fable_texture_offset_emulation = true;
  }
  return emulation;
}

bool CanEmulateFableTextureHeaderBuild(std::string_view symbol,
                                       uint8_t *base) {
  return RendererReplaceResourcesEnabled() && base != nullptr &&
         IsFableTextureHeaderBuildHook(symbol);
}

bool CanEmulateFableTextureHeaderUtility(std::string_view symbol,
                                         uint8_t *base) {
  return RendererReplaceResourcesEnabled() && base != nullptr &&
         IsFableTextureHeaderUtilityHook(symbol);
}

FableGuestResourceEmulation EmulateFableTextureHeaderBuild(
    std::string_view symbol, PPCContext &ctx, uint8_t *base) {
  FableGuestResourceEmulation emulation;
  if (!CanEmulateFableTextureHeaderBuild(symbol, base)) {
    return emulation;
  }

  PPCContext header_ctx = ctx;
  if (symbol == "sub_821FBB78") {
    __imp__sub_821FBB78(header_ctx, base);
  } else if (symbol == "sub_8300BEE0") {
    __imp__sub_8300BEE0(header_ctx, base);
  } else if (symbol == "sub_8300BF48") {
    __imp__sub_8300BF48(header_ctx, base);
  } else {
    return emulation;
  }

  emulation.handled = true;
  emulation.result = header_ctx.r3.u32;
  if (!g_logged_fable_texture_header_emulation) {
    REXLOG_INFO(
        "Refii renderer Plume resource replacement now owns XG texture "
        "header builds for XGSetTextureHeader/XGSetLineTextureHeader/"
        "XGSetCubeTextureHeader");
    g_logged_fable_texture_header_emulation = true;
  }
  return emulation;
}

FableGuestResourceEmulation EmulateFableTextureHeaderUtility(
    std::string_view symbol, PPCContext &ctx, uint8_t *base) {
  FableGuestResourceEmulation emulation;
  if (!CanEmulateFableTextureHeaderUtility(symbol, base)) {
    return emulation;
  }

  PPCContext header_ctx = ctx;
  if (symbol == "sub_821FB650") {
    __imp__sub_821FB650(header_ctx, base);
  } else if (symbol == "sub_821FB968") {
    __imp__sub_821FB968(header_ctx, base);
  } else {
    return emulation;
  }

  emulation.handled = true;
  emulation.result = header_ctx.r3.u32;
  if (!g_logged_fable_texture_header_utility_emulation) {
    REXLOG_INFO(
        "Refii renderer Plume resource replacement now owns Fable/XG texture "
        "header utility builders");
    g_logged_fable_texture_header_utility_emulation = true;
  }
  return emulation;
}

bool CanEmulateFableCpuResourceHelper(std::string_view symbol, uint8_t *base) {
  return RendererReplaceResourcesEnabled() && base != nullptr &&
         IsFableCpuResourceHelperHook(symbol);
}

uint32_t NormalizeFableGpuAliasAddress(uint32_t pointer) {
  if (pointer >= 0x41000000u && pointer < 0x50000000u) {
    return pointer - 0x41000000u;
  }
  if (pointer >= 0x40000000u && pointer < 0x41000000u) {
    return pointer - 0x40000000u;
  }
  return pointer;
}

uint32_t DecodeFableGpuMemoryAliasAddress(uint32_t pointer) {
  return ((((pointer >> 20u) + 512u) & 0x1000u) +
          (pointer & 0x1FFFFFFFu));
}

uint32_t FindFirstNonZeroGuestByte(uint8_t *base, uint32_t guest_address,
                                   uint32_t size, uint32_t max_scan) {
  if (!base || guest_address == 0 || size == 0 || max_scan == 0 ||
      guest_address > UINT32_MAX - size ||
      !IsLikelyGuestMemoryRange(guest_address, size)) {
    return UINT32_MAX;
  }

  const uint32_t scan_size = std::min(size, max_scan);
  if (!IsReadableGuestByteRange(base, guest_address, scan_size)) {
    return UINT32_MAX;
  }

  const uint8_t *data =
      base + guest_address + GuestPhysicalHostOffset(guest_address);
  for (uint32_t i = 0; i < scan_size; ++i) {
    if (data[i] != 0) {
      return i;
    }
  }
  return UINT32_MAX;
}

uint32_t FableCpuLayoutHelperProbeSize(uint32_t width, uint32_t height,
                                       uint32_t bytes_per_unit) {
  bytes_per_unit = std::clamp<uint32_t>(bytes_per_unit, 1u, 16u);
  uint64_t size = static_cast<uint64_t>(width) * height * bytes_per_unit;
  if (size == 0) {
    size = 64u * 1024u;
  }
  return static_cast<uint32_t>(std::min<uint64_t>(size, 512u * 1024u));
}

uint32_t RemapBlankFableCpuLayoutSourceAlias(std::string_view symbol,
                                             uint8_t *base, uint32_t source,
                                             uint32_t probe_size) {
  if (!base || source == 0 || probe_size == 0) {
    return source;
  }
  if (FindFirstNonZeroGuestByte(base, source, probe_size, probe_size) !=
      UINT32_MAX) {
    return source;
  }

  const std::array<uint32_t, 3> candidates = {
      NormalizeFableGpuAliasAddress(source),
      DecodeFableGpuMemoryAliasAddress(source & ~3u),
      DecodeFableGpuMemoryAliasAddress(source),
  };
  for (const uint32_t candidate : candidates) {
    if (candidate == 0 || candidate == source) {
      continue;
    }
    const uint32_t candidate_nonzero =
        FindFirstNonZeroGuestByte(base, candidate, probe_size, probe_size);
    if (candidate_nonzero == UINT32_MAX) {
      continue;
    }

    if (g_logged_fable_cpu_layout_source_alias_remaps < 16u) {
      REXLOG_INFO(
          "Refii renderer remapped Fable CPU layout helper source alias #{} "
          "hook={} raw=0x{:08X} chosen=0x{:08X} probe={} first_nz={}",
          g_logged_fable_cpu_layout_source_alias_remaps + 1u, symbol, source,
          candidate, probe_size, candidate_nonzero);
      ++g_logged_fable_cpu_layout_source_alias_remaps;
    }
    return candidate;
  }

  if (((source >= 0x40000000u && source < 0x50000000u) ||
       source >= 0xF0000000u) &&
      g_logged_fable_cpu_layout_source_alias_blanks < 16u) {
    const uint32_t normalized = NormalizeFableGpuAliasAddress(source);
    const uint32_t decoded_aligned =
        DecodeFableGpuMemoryAliasAddress(source & ~3u);
    const uint32_t decoded = DecodeFableGpuMemoryAliasAddress(source);
    const uint32_t normalized_nonzero =
        normalized != source
            ? FindFirstNonZeroGuestByte(base, normalized, probe_size,
                                        std::min(probe_size, 64u * 1024u))
            : UINT32_MAX;
    const uint32_t decoded_aligned_nonzero =
        decoded_aligned != source
            ? FindFirstNonZeroGuestByte(base, decoded_aligned, probe_size,
                                        std::min(probe_size, 64u * 1024u))
            : UINT32_MAX;
    const uint32_t decoded_nonzero =
        decoded != source
            ? FindFirstNonZeroGuestByte(base, decoded, probe_size,
                                        std::min(probe_size, 64u * 1024u))
            : UINT32_MAX;
    const int normalized_offset =
        normalized_nonzero == UINT32_MAX
            ? -1
            : static_cast<int>(normalized_nonzero);
    const int decoded_aligned_offset =
        decoded_aligned_nonzero == UINT32_MAX
            ? -1
            : static_cast<int>(decoded_aligned_nonzero);
    const int decoded_offset =
        decoded_nonzero == UINT32_MAX ? -1
                                      : static_cast<int>(decoded_nonzero);
    REXLOG_INFO(
        "Refii renderer Fable CPU layout helper source alias stayed blank #{} "
        "hook={} raw=0x{:08X} probe={} norm=0x{:08X}/{} "
        "dec4=0x{:08X}/{} dec=0x{:08X}/{}",
        g_logged_fable_cpu_layout_source_alias_blanks + 1u, symbol, source,
        probe_size, normalized, normalized_offset, decoded_aligned,
        decoded_aligned_offset, decoded, decoded_offset);
    ++g_logged_fable_cpu_layout_source_alias_blanks;
  }

  return source;
}

bool IsPowerOfTwo32(uint32_t value) {
  return value != 0 && (value & (value - 1u)) == 0;
}

uint32_t Log2Floor32(uint32_t value) {
  uint32_t result = 0;
  while (value > 1u) {
    value >>= 1u;
    ++result;
  }
  return result;
}

bool FableCpuLayoutSourceHasNonZeroData(std::string_view symbol, uint8_t *base,
                                        uint32_t source, uint32_t size) {
  if (!base || source == 0 || size == 0 ||
      !IsLikelyGuestMemoryRange(source, size)) {
    return false;
  }

  const uint32_t scan_size = std::min<uint32_t>(size, 512u * 1024u);
  const uint32_t first_nonzero =
      FindFirstNonZeroGuestByte(base, source, size, scan_size);
  if (first_nonzero != UINT32_MAX) {
    return true;
  }

  if (g_logged_fable_cpu_layout_blank_fast_path_rejects < 16u) {
    REXLOG_INFO(
        "Refii renderer exact Fable CPU layout fast path blank-source reject "
        "#{} hook={} source=0x{:08X} size={} scan={}",
        g_logged_fable_cpu_layout_blank_fast_path_rejects + 1u, symbol, source,
        size, scan_size);
    ++g_logged_fable_cpu_layout_blank_fast_path_rejects;
  }
  return false;
}

bool TryFastTileFable2DTextureLevel(uint8_t *base, uint32_t destination,
                                    uint32_t source, uint32_t width_texels,
                                    uint32_t height_texels,
                                    rex::graphics::xenos::TextureFormat format,
                                    uint32_t source_pitch_bytes,
                                    std::string_view symbol) {
  constexpr uint32_t kMaxFastTileBytes = 16u * 1024u * 1024u;
  if (base == nullptr || destination == 0 || source == 0 ||
      width_texels == 0 || height_texels == 0) {
    return false;
  }

  const rex::graphics::FormatInfo *format_info =
      rex::graphics::FormatInfo::Get(format);
  if (format_info == nullptr) {
    return false;
  }
  const uint32_t bytes_per_block = format_info->bytes_per_block();
  if (!IsPowerOfTwo32(bytes_per_block)) {
    return false;
  }

  const uint32_t width_blocks =
      std::max<uint32_t>(1u, (width_texels + format_info->block_width - 1u) /
                                 format_info->block_width);
  const uint32_t height_blocks =
      std::max<uint32_t>(1u, (height_texels + format_info->block_height - 1u) /
                                 format_info->block_height);
  const uint32_t row_bytes = width_blocks * bytes_per_block;
  if (source_pitch_bytes == 0) {
    source_pitch_bytes = row_bytes;
  }
  const uint32_t source_pitch_blocks =
      std::max<uint32_t>(width_blocks, source_pitch_bytes / bytes_per_block);
  const uint32_t tiled_bytes =
      rex::graphics::texture_util::GetTiledAddressUpperBound2D(
          width_blocks, height_blocks, source_pitch_blocks,
          Log2Floor32(bytes_per_block));
  const uint64_t source_bytes64 =
      uint64_t(source_pitch_bytes) * (height_blocks - 1u) + row_bytes;
  if (tiled_bytes == 0 || tiled_bytes > kMaxFastTileBytes ||
      source_bytes64 == 0 || source_bytes64 > kMaxFastTileBytes ||
      !IsLikelyGuestMemoryRange(source, static_cast<uint32_t>(source_bytes64)) ||
      !IsLikelyGuestMemoryRange(destination, tiled_bytes) ||
      !IsReadableGuestByteRange(base, source,
                                static_cast<uint32_t>(source_bytes64)) ||
      !IsReadableGuestByteRange(base, destination, tiled_bytes)) {
    return false;
  }
  if (!FableCpuLayoutSourceHasNonZeroData(
          symbol, base, source, static_cast<uint32_t>(source_bytes64))) {
    return false;
  }

  const uint8_t *source_bytes =
      base + source + GuestPhysicalHostOffset(source);
  uint8_t *destination_bytes =
      base + destination + GuestPhysicalHostOffset(destination);
  for (uint32_t y = 0; y < height_blocks; ++y) {
    for (uint32_t x = 0; x < width_blocks; ++x) {
      const uint32_t source_offset =
          y * source_pitch_bytes + x * bytes_per_block;
      const uint32_t tiled_offset = static_cast<uint32_t>(
          rex::graphics::texture_util::GetTiledOffset2D(
              static_cast<int32_t>(x), static_cast<int32_t>(y),
              source_pitch_blocks, Log2Floor32(bytes_per_block)));
      std::memcpy(destination_bytes + tiled_offset, source_bytes + source_offset,
                  bytes_per_block);
    }
  }

  if (!g_logged_fable_cpu_texture_layout_fast_tiling) {
    REXLOG_INFO(
        "Refii renderer Plume resource replacement is fast-tiling Fable "
        "XG 2D texture uploads into Xenos block layout");
    g_logged_fable_cpu_texture_layout_fast_tiling = true;
  }
  return true;
}

void RemapFableCpuLayoutHelperSourceAliases(std::string_view symbol,
                                            PPCContext &ctx, uint8_t *base) {
  if (symbol == "sub_8300CB48") { // XGTileTextureLevel
    const uint32_t probe_size =
        FableCpuLayoutHelperProbeSize(ctx.r3.u32, ctx.r4.u32, 4u);
    ctx.r10.u64 =
        RemapBlankFableCpuLayoutSourceAlias(symbol, base, ctx.r10.u32,
                                            probe_size);
  } else if (symbol == "sub_8300C290" || symbol == "sub_8300CB40") {
    const uint32_t probe_size =
        FableCpuLayoutHelperProbeSize(ctx.r4.u32, ctx.r5.u32, ctx.r10.u32);
    ctx.r7.u64 =
        RemapBlankFableCpuLayoutSourceAlias(symbol, base, ctx.r7.u32,
                                            probe_size);
  }
}

bool TryFastEmulateFableCpuTextureLayoutHelper(std::string_view symbol,
                                               PPCContext &ctx,
                                               uint8_t *base) {
  uint32_t destination = 0;
  uint32_t source = 0;
  uint32_t width = 0;
  uint32_t height = 0;
  uint32_t bytes_per_unit = 4u;

  if (symbol == "sub_8300CB48") { // XGTileTextureLevel
    width = ctx.r3.u32;
    height = ctx.r4.u32;
    destination = ctx.r8.u32;
    source = ctx.r10.u32;
    const uint32_t level = ctx.r5.u32;
    const uint32_t flags = ctx.r7.u32;
    const uint32_t point = ctx.r9.u32;
    const uint32_t source_pitch =
        IsLikelyGuestDwordAddress(ctx.r1.u32 + 84u)
            ? LoadGuestU32(base, ctx.r1.u32 + 84u)
            : 0u;
    const uint32_t source_rect =
        IsLikelyGuestDwordAddress(ctx.r1.u32 + 92u)
            ? LoadGuestU32(base, ctx.r1.u32 + 92u)
            : 0u;
    if (level == 0u && flags == 0u && point == 0u && source_rect == 0u &&
        ctx.r6.u32 < 64u &&
        TryFastTileFable2DTextureLevel(
            base, destination, source, width, height,
            static_cast<rex::graphics::xenos::TextureFormat>(ctx.r6.u32),
            source_pitch, symbol)) {
      ctx.r3.u64 = 0;
      return true;
    }
    return false;
  } else if (symbol == "sub_8300C290" || symbol == "sub_8300CB40") {
    destination = ctx.r3.u32;
    width = ctx.r4.u32;
    height = ctx.r5.u32;
    source = ctx.r7.u32;
    bytes_per_unit = ctx.r10.u32;
  } else {
    return false;
  }

  const uint32_t copy_size =
      FableCpuLayoutHelperProbeSize(width, height, bytes_per_unit);
  if (copy_size == 0 || !IsLikelyGuestMemoryRange(source, copy_size) ||
      !IsLikelyGuestMemoryRange(destination, copy_size)) {
    return false;
  }
  if (!FableCpuLayoutSourceHasNonZeroData(symbol, base, source, copy_size)) {
    return false;
  }

  if (!MoveGuestBytes(base, destination, source, copy_size)) {
    return false;
  }

  ctx.r3.u64 = 0;
  if (!g_logged_fable_cpu_texture_layout_fast_emulation) {
    REXLOG_INFO(
        "Refii renderer Plume resource replacement is fast-emulating Fable "
        "XG CPU texture layout helpers without running the slow original "
        "tiler");
    g_logged_fable_cpu_texture_layout_fast_emulation = true;
  }
  return true;
}

FableGuestResourceEmulation EmulateFableCpuResourceHelper(
    std::string_view symbol, PPCContext &ctx, uint8_t *base) {
  FableGuestResourceEmulation emulation;
  if (!CanEmulateFableCpuResourceHelper(symbol, base)) {
    return emulation;
  }

  PPCContext helper_ctx = ctx;
  RemapFableCpuLayoutHelperSourceAliases(symbol, helper_ctx, base);
  if (TryFastEmulateFableCpuTextureLayoutHelper(symbol, helper_ctx, base)) {
    // Already handled above.
  } else if (symbol == "sub_821FA6C0") {
    __imp__sub_821FA6C0(helper_ctx, base);
  } else if (symbol == "sub_82278138") {
    __imp__sub_82278138(helper_ctx, base);
  } else if (symbol == "sub_8300C290") {
    __imp__sub_8300C290(helper_ctx, base);
  } else if (symbol == "sub_8300CB40") {
    __imp__sub_8300CB40(helper_ctx, base);
  } else if (symbol == "sub_8300CB48") {
    __imp__sub_8300CB48(helper_ctx, base);
  } else if (symbol == "sub_83010E60") {
    __imp__sub_83010E60(helper_ctx, base);
  } else if (symbol == "sub_830117B8") {
    __imp__sub_830117B8(helper_ctx, base);
  } else if (symbol == "sub_8300C660") {
    __imp__sub_8300C660(helper_ctx, base);
  } else if (symbol == "sub_8300CDA0") {
    __imp__sub_8300CDA0(helper_ctx, base);
  } else if (symbol == "sub_8300CDA8") {
    __imp__sub_8300CDA8(helper_ctx, base);
  } else if (symbol == "sub_830112F0") {
    __imp__sub_830112F0(helper_ctx, base);
  } else if (symbol == "sub_83011AE0") {
    __imp__sub_83011AE0(helper_ctx, base);
  } else {
    return emulation;
  }

  emulation.handled = true;
  emulation.result = helper_ctx.r3.u32;
  if (!g_logged_fable_cpu_resource_helper_emulation) {
    REXLOG_INFO(
        "Refii renderer Plume resource replacement now owns D3D/XG CPU "
        "resource layout helpers for surface lookup/lock and tile/untile");
    g_logged_fable_cpu_resource_helper_emulation = true;
  }
  return emulation;
}

struct FableResourceLockShape {
  uint32_t resource = 0;
  uint32_t lock_address = 0;
  uint32_t lock_size = 0;
  uint32_t range_base = 0;
  uint32_t range_offset = 0x14u;
};

bool TryGetFableResourceLockShape(std::string_view symbol,
                                  const RendererHookArgs &args,
                                  uint8_t *base,
                                  FableResourceLockShape &shape) {
  if (!RendererReplaceResourcesEnabled() || base == nullptr ||
      !IsLikelyGuestHeaderAddress(args.r3, 32u)) {
    return false;
  }

  shape.resource = args.r3;
  if (symbol == "rex_lhGfx_LockBuffer") {
    if (args.r9 == 0 || args.r10 == 0 ||
        !IsLikelyGuestMemoryAddress(args.r9)) {
      return false;
    }
    const bool range1 = args.r6 != 0 && args.r8 != 0;
    shape.lock_address = args.r9;
    shape.lock_size = args.r10;
    shape.range_base = range1 ? args.r8 : args.r7;
    shape.range_offset = range1 ? 0x18u : 0x14u;
    return true;
  }

  if (symbol == "sub_8225F980") { // D3DVertexBuffer_Lock
    shape.lock_address = LoadGuestU32(base, args.r3 + 0x18u) & 0xFFFFFFFCu;
    shape.lock_size = LoadGuestU32(base, args.r3 + 0x1Cu) & 0x03FFFFFCu;
    shape.range_base = shape.lock_address;
    return shape.lock_address != 0 && shape.lock_size != 0 &&
           IsLikelyGuestMemoryAddress(shape.lock_address);
  }

  if (symbol == "sub_82196980") { // D3DIndexBuffer_Lock
    shape.lock_address = LoadGuestU32(base, args.r3 + 0x18u);
    shape.lock_size = LoadGuestU32(base, args.r3 + 0x1Cu);
    shape.range_base = shape.lock_address;
    return shape.lock_address != 0 && shape.lock_size != 0 &&
           IsLikelyGuestMemoryAddress(shape.lock_address);
  }

  return false;
}

bool CanEmulateFableResourceLock(std::string_view symbol,
                                 const RendererHookArgs &args,
                                 uint8_t *base) {
  FableResourceLockShape shape;
  return TryGetFableResourceLockShape(symbol, args, base, shape);
}

FableGuestResourceEmulation EmulateFableResourceLock(
    std::string_view symbol, const RendererHookArgs &args, uint8_t *base) {
  FableGuestResourceEmulation emulation;
  FableResourceLockShape shape;
  if (!TryGetFableResourceLockShape(symbol, args, base, shape)) {
    return emulation;
  }

  const uint32_t header0 = LoadGuestU32(base, shape.resource);
  UpdateFableLockDirtyRange(base, shape.resource, shape.range_offset,
                            shape.range_base, shape.lock_address,
                            shape.lock_size);
  StoreGuestU32(base, shape.resource, header0 + 0x100u);

  emulation.handled = true;
  emulation.result = shape.lock_address;

  // Content model: redirect the guest's writes to the view we actually read.
  //
  // We own this lock (mandatory replacement, guest body suppressed) and the
  // returned pointer is what the caller writes through — IDA-confirmed for
  // both wrappers: D3DVertexBuffer_Lock (0x8225F980) returns
  // D3D_LockResource's value directly, and D3D_LockSurface (0x82278138) does
  // `result = D3D_LockResource(...); *a6 = result + *a28; return result;`.
  // Neither recomputes the address afterwards.
  //
  // Today we hand back the caller's address, which for Fable is the
  // write-combined ALIAS. rexglue's flat map does not alias the WC and decoded
  // views, so the bytes land somewhere none of our capture paths look — which
  // is the entire origin of the alias/decoded disagreement, the deferral rule
  // and the physical-window machinery. Returning the decoded view instead
  // makes the guest write exactly where every reader already looks.
  //
  // Only redirect when decoding actually MOVES the pointer and the result is
  // still a plausible guest address; otherwise keep the caller's value.
  if (RendererLockReturnsDecodedViewEnabled()) {
    const uint32_t decoded = DecodeFableGpuBufferPointer(shape.lock_address);
    if (decoded != 0 && decoded != shape.lock_address &&
        IsLikelyGuestMemoryAddress(decoded)) {
      emulation.result = decoded;
      static uint32_t logged_lock_decoded_redirects = 0;
      if (logged_lock_decoded_redirects < 32u) {
        REXLOG_INFO(
            "Refii renderer lock returns decoded view #{} resource=0x{:08X} "
            "alias=0x{:08X} decoded=0x{:08X} size={}",
            ++logged_lock_decoded_redirects, shape.resource,
            shape.lock_address, decoded, shape.lock_size);
      }
    }
  }
  if (!g_logged_fable_resource_lock_emulation) {
    REXLOG_INFO(
        "Refii renderer Plume resource replacement now owns D3D::LockResource "
        "header bookkeeping for emulatable guest pointers and "
        "D3DVertexBuffer/D3DIndexBuffer lock wrappers");
    g_logged_fable_resource_lock_emulation = true;
  }
  return emulation;
}

bool CanEmulateFableDirectResourceHook(std::string_view symbol,
                                       const RendererHookArgs &args,
                                       uint8_t *base) {
  if (symbol == "sub_82B98930") { // LockTextureRect
    return CanEmulateFableTextureSurfaceLock(symbol, args, base);
  }
  if (symbol == "sub_8225F980" || symbol == "sub_82196980") {
    return CanEmulateFableResourceLock(symbol, args, base);
  }
  if (symbol == "sub_821FA070") { // GetSurfaceDesc
    return CanEmulateFableResourceQuery(symbol, args, base);
  }
  if (symbol == "sub_82B98B28") { // CreateTexture
    return CanEmulateFableTextureCreate(symbol, args, base);
  }
  if (IsFableResourceUpdateReplacementHook(symbol)) {
    return RendererReplaceResourcesEnabled() && base != nullptr;
  }
  if (IsFableResourceLifetimeHook(symbol)) {
    return RendererReplaceResourcesEnabled() && base != nullptr;
  }
  if (IsFableResourceHeaderBuildHook(symbol)) {
    return RendererReplaceResourcesEnabled() && base != nullptr;
  }
  if (IsFableBufferCreateHook(symbol)) {
    return RendererReplaceResourcesEnabled() && base != nullptr;
  }
  if (CanEmulateFableVertexDeclarationCreate(symbol, args, base)) {
    return true;
  }
  if (CanEmulateFableShaderCreate(symbol, args, base)) {
    return true;
  }
  if (CanEmulateFableSurfaceCreate(symbol, args, base)) {
    return true;
  }
  if (IsFableSurfaceCopyHook(symbol) || IsFableSurfaceMemoryCopyHook(symbol)) {
    return RendererReplaceResourcesEnabled() && base != nullptr;
  }
  if (CanEmulateFableTextureFill(symbol, args, base)) {
    return true;
  }
  if (CanEmulateFableTextureFileCreate(symbol, args, base)) {
    return true;
  }
  if (CanEmulateFableTextureSurfaceLock(symbol, args, base)) {
    return true;
  }
  if (CanEmulateFableResourceLock(symbol, args, base)) {
    return true;
  }
  if (CanEmulateFableResourceQuery(symbol, args, base)) {
    return true;
  }
  if (CanEmulateFableTextureAddressOffset(symbol, args, base)) {
    return true;
  }
  if (CanEmulateFableTextureHeaderBuild(symbol, base)) {
    return true;
  }
  if (CanEmulateFableTextureHeaderUtility(symbol, base)) {
    return true;
  }
  if (CanEmulateFableCpuResourceHelper(symbol, base)) {
    return true;
  }

  return false;
}

// Address-dispatch head for the SUPPRESSION DECISION, twin of the switch at
// the top of EmulateFableDirectResourceHook (run 462). The kResource arm of
// ShouldRunGuestRendererHook re-ran the whole symbol-string ladder above on
// EVERY resource call to decide suppress-vs-run — for D3DResource_Release
// (532,929 calls run 462; 1.05M run 466) that was ~11 string_view compares
// per call just to reach the IsFableResourceLifetimeHook branch. Each address
// below belongs to exactly ONE family (same exclusivity argument the Emulate
// switch already relies on), so hoisting cannot change which branch decides;
// the case bodies are the LITERAL expressions of the ladder branches they
// replace. sub_* symbols map 1:1 onto their hex address and none of the rex_*
// twin addresses (kUnlockResource, kCreateSurface, ...) collide with a case,
// so string-resolved and address-resolved callers stay in agreement; address
// 0 (rex_* shims, desc-less callers) keeps the full string ladder.
bool CanEmulateFableDirectResourceHook(std::string_view symbol,
                                       uint32_t symbol_address,
                                       const RendererHookArgs &args,
                                       uint8_t *base) {
  switch (symbol_address) {
  case 0x821FC320u: // D3DResource_Release
  case 0x821FC178u: // D3DResource_AddRef
    // Ladder branch: IsFableResourceLifetimeHook(symbol).
    return RendererReplaceResourcesEnabled() && base != nullptr;
  case 0x82B98950u: // UnlockTextureRect
  case 0x82B9EEC0u: // UnlockVertexBuffer
  case 0x82B9EED0u: // UnlockIndexBuffer
    // Ladder branch: IsFableResourceUpdateReplacementHook(symbol). The fourth
    // member, rex_lhGfx_FlushBuffer, arrives with the kUnlockResource twin
    // address (not a case here) and keeps the string ladder.
    return RendererReplaceResourcesEnabled() && base != nullptr;
  case 0x8300BD38u: // XGSetVertexBufferHeader
  case 0x8300BDD0u: // XGSetIndexBufferHeader
  case 0x8300BE60u: // XGSetConstantBufferHeader
    // Ladder branch: IsFableResourceHeaderBuildHook(symbol).
    return RendererReplaceResourcesEnabled() && base != nullptr;
  case 0x82B98C88u: // D3DSurface_GetContainer
  case 0x821FAB38u: // D3D::GetTextureDimensions
  case 0x821FA070u: // D3DSurface_GetDesc
  case 0x821FABD0u: // D3D::Get2DSurfaceDesc
  case 0x82B985C8u: // D3D::Get3DSurfaceDesc
  case 0x821FAD60u: // D3D::GetLevelSize
    // Ladder route for all six: CanEmulateFableResourceQuery (sub_821FA070
    // via its early compare, the rest by falling through to the query check).
    return CanEmulateFableResourceQuery(symbol, args, base);
  default:
    break;
  }
  return CanEmulateFableDirectResourceHook(symbol, args, base);
}

FableGuestResourceEmulation EmulateFableDirectResourceHook(
    std::string_view symbol, uint32_t symbol_address,
    const RendererHookArgs &args, PPCContext &ctx, uint8_t *base) {
  // Census-hot kResource roots dispatch on the resolved descriptor address
  // before the symbol-string ladder below (run 462: Release 532,929 calls at
  // 148us, Get2DSurfaceDesc 143,297 at 61us — a large slice of both was this
  // ladder's ~20 Can*/Is* string checks at -O0). Each address belongs to
  // exactly one family, so hoisting cannot change which handler wins; the
  // handlers themselves keep their shape validation and return un-handled on
  // mismatch exactly as the ladder did.
  switch (symbol_address) {
  case 0x821FC320u: // D3DResource_Release
    g_release_dispatch_hits.fetch_add(1, std::memory_order_relaxed);
    [[fallthrough]];
  case 0x821FC178u: // D3DResource_AddRef
    return EmulateFableResourceLifetime(symbol, args, base);
  case 0x82B98C88u: // D3DSurface_GetContainer
  case 0x821FAB38u: // D3D::GetTextureDimensions
  case 0x821FA070u: // D3DSurface_GetDesc
  case 0x821FABD0u: // D3D::Get2DSurfaceDesc
  case 0x82B985C8u: // D3D::Get3DSurfaceDesc
  case 0x821FAD60u: // D3D::GetLevelSize
    return EmulateFableResourceQuery(symbol, args, base);
  default:
    break;
  }

  if (symbol == "sub_82B98930") { // LockTextureRect
    return EmulateFableTextureSurfaceLock(symbol, args, base);
  }
  if (symbol == "sub_8225F980" || symbol == "sub_82196980") {
    return EmulateFableResourceLock(symbol, args, base);
  }
  if (symbol == "sub_821FA070") { // GetSurfaceDesc
    return EmulateFableResourceQuery(symbol, args, base);
  }
  if (symbol == "sub_82B98B28") { // CreateTexture
    return EmulateFableTextureCreate(symbol, args, ctx, base);
  }
  if (IsFableResourceUpdateReplacementHook(symbol)) {
    return EmulateFableResourceUpdate(symbol, args, base);
  }
  if (IsFableResourceLifetimeHook(symbol)) {
    return EmulateFableResourceLifetime(symbol, args, base);
  }
  if (IsFableResourceHeaderBuildHook(symbol)) {
    return EmulateFableResourceHeaderBuild(symbol, args, base);
  }
  if (IsFableBufferCreateHook(symbol)) {
    return EmulateFableBufferCreate(symbol, args, ctx, base);
  }
  if (CanEmulateFableVertexDeclarationCreate(symbol, args, base)) {
    return EmulateFableVertexDeclarationCreate(symbol, args, ctx, base);
  }
  if (CanEmulateFableShaderCreate(symbol, args, base)) {
    return EmulateFableShaderCreate(symbol, args, ctx, base);
  }
  if (CanEmulateFableSurfaceCreate(symbol, args, base)) {
    return EmulateFableSurfaceCreate(symbol, args, ctx, base);
  }
  if (IsFableSurfaceCopyHook(symbol)) {
    return EmulateFableSurfaceCopy(symbol, args, base);
  }
  if (IsFableSurfaceMemoryCopyHook(symbol)) {
    return EmulateFableSurfaceMemoryCopy(symbol, args, ctx, base);
  }
  if (CanEmulateFableTextureFill(symbol, args, base)) {
    return EmulateFableTextureFill(symbol, args, base);
  }
  if (CanEmulateFableTextureFileCreate(symbol, args, base)) {
    return EmulateFableTextureFileCreate(symbol, args, ctx, base);
  }
  if (CanEmulateFableTextureSurfaceLock(symbol, args, base)) {
    return EmulateFableTextureSurfaceLock(symbol, args, base);
  }
  if (CanEmulateFableResourceLock(symbol, args, base)) {
    return EmulateFableResourceLock(symbol, args, base);
  }
  if (CanEmulateFableResourceQuery(symbol, args, base)) {
    return EmulateFableResourceQuery(symbol, args, base);
  }
  if (CanEmulateFableTextureAddressOffset(symbol, args, base)) {
    return EmulateFableTextureAddressOffset(symbol, args, base);
  }
  if (CanEmulateFableTextureHeaderBuild(symbol, base)) {
    return EmulateFableTextureHeaderBuild(symbol, ctx, base);
  }
  if (CanEmulateFableTextureHeaderUtility(symbol, base)) {
    return EmulateFableTextureHeaderUtility(symbol, ctx, base);
  }
  if (CanEmulateFableCpuResourceHelper(symbol, base)) {
    return EmulateFableCpuResourceHelper(symbol, ctx, base);
  }

  return {};
}

FableGuestResourceEmulation EmulateFableGuestResourceHook(
    std::string_view symbol, const RendererHookArgs &args, PPCContext &ctx,
    uint8_t *base, uint32_t symbol_address) {
  if (const FableGuestResourceEmulation exact =
          EmulateFableDirectResourceHook(symbol, symbol_address, args, ctx,
                                         base);
      exact.handled) {
    return exact;
  }
  // Address dispatch for the surface-wrapper creators (24,519 calls, run
  // 462): no Can* between the direct ladder and
  // CanEmulateFableSurfaceWrapperCreate matches these two symbols, and the
  // handler re-validates its own shape, so this is a pure reorder.
  if (symbol_address == 0x821FC8D8u || // Fable::CreateSurfaceWrapper
      symbol_address == 0x82295380u) { // Fable::CreateTiledSurfaceWrapper
    return EmulateFableSurfaceWrapperCreate(symbol, args, ctx, base);
  }
  if (CanEmulateFableVertexShaderRegister(symbol, args, base)) {
    return EmulateFableVertexShaderRegister(symbol, args, ctx, base);
  }
  if (CanEmulateFableShaderPatchHelper(symbol, args, base)) {
    return EmulateFableShaderPatchHelper(symbol, args, ctx, base);
  }
  if (CanEmulateFableShaderUtility(symbol, args, base)) {
    return EmulateFableShaderUtility(symbol, args, ctx, base);
  }
  if (CanEmulateFableRenderDepthSurfaceCtor(symbol, args, base)) {
    return EmulateFableRenderDepthSurfaceCtor(symbol, args, ctx, base);
  }
  if (CanEmulateFableRenderTargetSetup(symbol, base)) {
    return EmulateFableRenderTargetSetup(symbol, ctx, base);
  }
  if (CanEmulateFableBinkVideoPlaneSetup(symbol, args, base)) {
    return EmulateFableBinkVideoPlaneSetup(symbol, args, ctx, base);
  }
  if (CanEmulateFableBinkVideoPlaneSurfaceSetup(symbol, base)) {
    return EmulateFableBinkVideoPlaneSurfaceSetup(symbol, base);
  }
  if (CanEmulateFableSurfaceWrapperCreate(symbol, args, base)) {
    return EmulateFableSurfaceWrapperCreate(symbol, args, ctx, base);
  }
  if (CanEmulateFableSurfaceViewCreate(symbol, args, base)) {
    return EmulateFableSurfaceViewCreate(symbol, args, ctx, base);
  }
  return {};
}

}  // namespace refii::renderer
