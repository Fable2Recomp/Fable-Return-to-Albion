#include "renderer_runtime.h"

#include "hooks/ppc_externs.h"
#include "renderdoc_debugger.h"
#include "runtime/cvars.h"
#include "runtime/hook_observer.h"
#include "runtime/plume/direct/fable_direct_state.h"
#include "runtime/plume/direct/fable_hook_symbols.h"
#include "runtime/plume/general/plume_backend.h"

#include <rex/logging.h>
#include <rex/graphics/pipeline/texture/info.h>
#include <rex/graphics/pipeline/texture/util.h>
#include <rex/graphics/xenos.h>
#include <rex/ppc/context.h>
#include <tracy/Tracy.hpp>

#include <algorithm>
#include <atomic>
#include <cerrno>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <cstdlib>
#include <cstring>
#include <cmath>
#include <fstream>
#include <limits>
#include <mutex>
#include <string>
#include <string_view>
#include <thread>
#include <unordered_map>
#include <unordered_set>

#if defined(_WIN32)
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#endif

namespace refii::renderer {
namespace {

bool g_logged_plume_unavailable = false;
bool g_logged_plume_observer = false;
bool g_logged_plume_present_driver = false;
bool g_logged_plume_end_scene_present_driver = false;
bool g_logged_plume_present_pump_driver = false;
bool g_logged_plume_present_failed = false;
bool g_logged_fable_renderer_setup_bridge = false;
bool g_logged_fable_present_bookkeeping = false;
bool g_logged_plume_pump_fable_bookkeeping = false;
bool g_logged_plume_pump_fable_bookkeeping_advance = false;
bool g_logged_plume_pump_fable_bookkeeping_complete = false;
bool g_logged_plume_pump_fable_bookkeeping_deferred_for_work = false;
bool g_logged_fable_command_submit_emulation = false;
bool g_logged_fable_kickoff_segment_emulation = false;
bool g_logged_fable_kickoff_emulation = false;
bool g_logged_fable_query_buffer_space_emulation = false;
std::atomic<uint32_t> g_logged_fable_end_export_emulation{0};
bool g_logged_fable_plume_command_buffer_capture = false;
bool g_logged_fable_primary_list_space_emulation = false;
bool g_logged_fable_deferred_list_space_emulation = false;
bool g_logged_fable_begin_vertices_emulation = false;
bool g_logged_fable_begin_indexed_vertices_emulation = false;
bool g_logged_fable_draw_vertices_up_emulation = false;
bool g_logged_fable_pix_capture_emulation = false;
bool g_logged_fable_display_interval_emulation = false;
bool g_logged_fable_complex_swap_emulation = false;
bool g_logged_fable_scene_batch_toggle_emulation = false;
bool g_logged_fable_resource_lifetime_emulation = false;
bool g_logged_fable_resource_lifetime_shape = false;
bool g_logged_fable_resource_header_emulation = false;
bool g_logged_fable_resource_lock_emulation = false;
bool g_logged_fable_texture_surface_lock_emulation = false;
bool g_logged_fable_surface_copy_emulation = false;
bool g_logged_fable_surface_copy_shape = false;
bool g_logged_fable_surface_memory_copy_emulation = false;
bool g_logged_fable_surface_memory_copy_shape = false;
bool g_logged_fable_surface_memory_copy_linear_fallback = false;
bool g_logged_fable_texture_fill_emulation = false;
bool g_logged_fable_texture_file_create_emulation = false;
bool g_logged_fable_resource_query_emulation = false;
bool g_logged_fable_texture_offset_emulation = false;
bool g_logged_fable_texture_header_emulation = false;
bool g_logged_fable_texture_header_utility_emulation = false;
bool g_logged_fable_cpu_resource_helper_emulation = false;
bool g_logged_fable_cpu_texture_layout_fast_emulation = false;
bool g_logged_fable_cpu_texture_layout_fast_tiling = false;
bool g_logged_fable_surface_size_emulation = false;
bool g_logged_fable_surface_view_emulation = false;
bool g_logged_fable_resource_unlock_emulation = false;
bool g_logged_fable_buffer_create_emulation = false;
bool g_logged_fable_buffer_create_shape = false;
bool g_logged_fable_vertex_declaration_create_emulation = false;
bool g_logged_fable_shader_create_emulation = false;
bool g_logged_fable_vertex_shader_register_emulation = false;
bool g_logged_fable_shader_patch_helper_emulation = false;
bool g_logged_fable_shader_utility_emulation = false;
bool g_logged_fable_set_pending_shaders_emulation = false;
bool g_logged_fable_texture_create_emulation = false;
bool g_logged_fable_surface_create_emulation = false;
bool g_logged_fable_surface_wrapper_emulation = false;
bool g_logged_fable_time_delta_repair = false;
bool g_logged_fable_surface_wrapper_shape = false;
bool g_logged_fable_render_depth_surface_ctor_emulation = false;
bool g_logged_fable_render_depth_surface_ctor_shape = false;
bool g_logged_fable_render_target_setup_emulation = false;
bool g_logged_fable_bink_video_plane_emulation = false;
bool g_logged_fable_bink_video_plane_shape = false;
bool g_logged_fable_get_render_target_emulation = false;
std::atomic_bool g_seen_plume_display_swap_hook = false;
std::atomic_bool g_plume_present_pump_running = false;
std::atomic_bool g_plume_present_pump_stop_requested = false;
std::atomic_bool g_plume_present_pump_shutdown_requested = false;
std::atomic<int64_t> g_last_renderer_hook_enter_ns{0};
std::atomic<int64_t> g_last_draw_hook_enter_ns{0};
std::atomic<int64_t> g_last_display_present_hook_ns{0};
std::atomic<int64_t> g_last_plume_present_request_ns{0};
std::atomic<int64_t> g_last_plume_present_pump_request_ns{0};
std::atomic<uint64_t> g_hook_enter_count{0};
std::atomic<uint64_t> g_renderer_setup_hook_count{0};
std::atomic<uint64_t> g_draw_hook_enter_count{0};
std::atomic<uint64_t> g_display_present_hook_count{0};
std::atomic<uint64_t> g_plume_present_request_count{0};
std::atomic<uint64_t> g_plume_present_pump_request_count{0};
std::atomic<uint64_t> g_plume_present_pump_fable_advance_count{0};
std::atomic<uint64_t> g_native_skipped_hook_count{0};
std::atomic<uint64_t> g_fable_plume_command_buffer_submit_count{0};
std::atomic<uint32_t> g_fable_plume_gpu_scratch_next{0xF0000000u};
std::atomic<int64_t> g_last_hook_gap_us{0};
std::atomic<int64_t> g_last_draw_hook_gap_us{0};
std::atomic<int64_t> g_last_display_present_gap_us{0};
std::atomic<int64_t> g_last_plume_present_request_gap_us{0};
std::atomic<int64_t> g_last_plume_present_pump_gap_us{0};
std::atomic<uint32_t> g_last_fable_device{0};
std::atomic<uint32_t> g_last_fable_renderer{0};
std::atomic<uint32_t> g_last_fable_renderer_owner{0};
std::atomic<uint32_t> g_last_fable_game{0};
std::atomic<uintptr_t> g_last_fable_memory_base{0};
std::atomic<uint32_t> g_last_fable_present_submitted{0};
std::atomic<uint32_t> g_last_fable_present_completed{0};
std::atomic<uint32_t> g_last_fable_swap_callbacks{0};
std::atomic<uint32_t> g_last_fable_present_pending{0};
std::mutex g_hook_timing_mutex;
std::mutex g_present_pump_mutex;
std::condition_variable g_present_pump_cv;
std::thread g_present_pump_thread;
std::once_flag g_present_pump_atexit_once;
char g_last_hook_symbol[64] = {};
char g_last_renderer_setup_symbol[64] = {};
char g_last_display_present_symbol[64] = {};
char g_last_plume_present_symbol[64] = {};
char g_last_plume_present_reason[32] = {};
char g_last_native_skipped_symbol[64] = {};
std::unordered_set<std::string> g_logged_hook_decisions;
std::unordered_set<std::string> g_logged_hook_suppressed;
std::mutex g_fable_native_fallback_shape_mutex;
std::unordered_map<std::string, uint64_t> g_fable_native_fallback_shape_counts;
uint32_t g_logged_fable_cpu_layout_source_alias_remaps = 0;
uint32_t g_logged_fable_cpu_layout_source_alias_blanks = 0;
uint32_t g_logged_fable_cpu_layout_blank_fast_path_skips = 0;

using RendererTraceClock = std::chrono::steady_clock;

int64_t RendererTraceNowNs() {
  return std::chrono::duration_cast<std::chrono::nanoseconds>(
             RendererTraceClock::now().time_since_epoch())
      .count();
}

void ConfigureRendererTracyPlots() {
  static std::once_flag configure_once;
  std::call_once(configure_once, [] {
    TracyPlotConfig("Refii Renderer Hook Gap Us", tracy::PlotFormatType::Number,
                    false, false, 0x38BDF8);
    TracyPlotConfig("Refii Renderer Draw Hook Gap Us",
                    tracy::PlotFormatType::Number, false, false, 0x22C55E);
    TracyPlotConfig("Refii Fable Present Hook Gap Us",
                    tracy::PlotFormatType::Number, false, false, 0xF59E0B);
    TracyPlotConfig("Refii Plume Present Request Gap Us",
                    tracy::PlotFormatType::Number, false, false, 0xA855F7);
    TracyPlotConfig("Refii Plume Present Pump Gap Us",
                    tracy::PlotFormatType::Number, false, false, 0x14B8A6);
    TracyPlotConfig("Refii Plume Present Result", tracy::PlotFormatType::Number,
                    true, false, 0x84CC16);
    TracyPlotConfig("Refii Renderer Hook Kind", tracy::PlotFormatType::Number,
                    true, false, 0x60A5FA);
    TracyPlotConfig("Refii Fable Presents Pending",
                    tracy::PlotFormatType::Number, true, false, 0xFB7185);
  });
}

void CopyTimingText(char *destination, size_t destination_size,
                    std::string_view text) {
  if (destination_size == 0) {
    return;
  }
  const size_t copy_size = std::min(destination_size - 1, text.size());
  std::memcpy(destination, text.data(), copy_size);
  destination[copy_size] = '\0';
}

void SetLastTimingText(char *destination, size_t destination_size,
                       std::string_view text) {
  std::lock_guard lock(g_hook_timing_mutex);
  CopyTimingText(destination, destination_size, text);
}

int64_t PlotDeltaUs(const char *name, std::atomic<int64_t> &previous_ns,
                    int64_t now_ns) {
  const int64_t prior_ns =
      previous_ns.exchange(now_ns, std::memory_order_relaxed);
  if (prior_ns == 0) {
    return 0;
  }
  const int64_t delta_us = (now_ns - prior_ns) / 1000;
  TracyPlot(name, delta_us);
  return delta_us;
}

std::chrono::nanoseconds PlumePresentPumpInterval(uint32_t target_fps) {
  target_fps = std::clamp<uint32_t>(target_fps, 1, 240);
  return std::chrono::nanoseconds(1000000000ull / target_fps);
}

uint32_t GuestPhysicalHostOffset(uint32_t guest_address) {
#if defined(_WIN32)
  return guest_address >= 0xE0000000u ? 0x1000u : 0u;
#else
  return 0;
#endif
}

bool IsPlausibleGuestByteRange(uint32_t guest_address, uint32_t byte_size) {
  if (guest_address == 0 || byte_size == 0 ||
      guest_address > UINT32_MAX - (byte_size - 1u)) {
    return false;
  }

  const uint32_t end_address = guest_address + byte_size - 1u;
  if (guest_address >= 0x1000u && end_address < 0x80000000u) {
    return true;
  }

  return guest_address >= 0x80000000u && byte_size <= 64u * 1024u * 1024u;
}

bool IsReadableHostByteRange(const uint8_t *data, size_t size) {
  if (!data || size == 0) {
    return false;
  }

  const uintptr_t begin = reinterpret_cast<uintptr_t>(data);
  if (begin > std::numeric_limits<uintptr_t>::max() - size) {
    return false;
  }

  const uintptr_t end = begin + size;
  uintptr_t current = begin;
#if defined(_WIN32)
  while (current < end) {
    MEMORY_BASIC_INFORMATION info = {};
    if (VirtualQuery(reinterpret_cast<const void *>(current), &info,
                     sizeof(info)) == 0) {
      return false;
    }

    const DWORD protect = info.Protect & 0xFFu;
    const bool readable =
        protect == PAGE_READONLY || protect == PAGE_READWRITE ||
        protect == PAGE_WRITECOPY || protect == PAGE_EXECUTE_READ ||
        protect == PAGE_EXECUTE_READWRITE ||
        protect == PAGE_EXECUTE_WRITECOPY;
    if (info.State != MEM_COMMIT || (info.Protect & PAGE_GUARD) != 0 ||
        !readable) {
      return false;
    }

    const uintptr_t region_begin =
        reinterpret_cast<uintptr_t>(info.BaseAddress);
    if (region_begin > std::numeric_limits<uintptr_t>::max() -
                           static_cast<uintptr_t>(info.RegionSize)) {
      return false;
    }
    const uintptr_t region_end =
        region_begin + static_cast<uintptr_t>(info.RegionSize);
    if (region_end <= current) {
      return false;
    }
    current = std::min(region_end, end);
  }
#elif defined(__linux__)
  thread_local uintptr_t cached_readable_begin = 0;
  thread_local uintptr_t cached_readable_end = 0;
  if (begin >= cached_readable_begin && end <= cached_readable_end) {
    return true;
  }

  std::ifstream maps("/proc/self/maps");
  if (!maps) {
    return false;
  }

  std::string line;
  while (current < end && std::getline(maps, line)) {
    const char *text = line.c_str();
    char *after_begin = nullptr;
    errno = 0;
    const unsigned long long region_begin_value =
        std::strtoull(text, &after_begin, 16);
    if (errno != 0 || after_begin == text || *after_begin != '-') {
      continue;
    }

    char *after_end = nullptr;
    errno = 0;
    const unsigned long long region_end_value =
        std::strtoull(after_begin + 1, &after_end, 16);
    if (errno != 0 || after_end == after_begin + 1 || *after_end != ' ') {
      continue;
    }
    while (*after_end == ' ') {
      ++after_end;
    }

    const uintptr_t region_begin =
        static_cast<uintptr_t>(region_begin_value);
    const uintptr_t region_end = static_cast<uintptr_t>(region_end_value);
    if (region_end <= current) {
      continue;
    }
    if (region_begin > current || after_end[0] != 'r') {
      return false;
    }
    if (current == begin && region_end >= end) {
      cached_readable_begin = region_begin;
      cached_readable_end = region_end;
      return true;
    }
    current = std::min(region_end, end);
  }

  if (current < end) {
    return false;
  }
#endif

  return true;
}

bool IsReadableGuestByteRange(uint8_t *base, uint32_t guest_address,
                              uint32_t byte_size) {
  if (!base || !IsPlausibleGuestByteRange(guest_address, byte_size)) {
    return false;
  }

  const uint8_t *data =
      base + guest_address + GuestPhysicalHostOffset(guest_address);
  return IsReadableHostByteRange(data, byte_size);
}

uint32_t LoadGuestU32(uint8_t *base, uint32_t guest_address) {
  if (!IsReadableGuestByteRange(base, guest_address, sizeof(uint32_t))) {
    return 0;
  }

  uint32_t raw = 0;
  std::memcpy(&raw,
              base + guest_address + GuestPhysicalHostOffset(guest_address),
              sizeof(raw));
  return __builtin_bswap32(raw);
}

void StoreGuestU32(uint8_t *base, uint32_t guest_address, uint32_t value) {
  if (!IsReadableGuestByteRange(base, guest_address, sizeof(uint32_t))) {
    return;
  }

  const uint32_t raw = __builtin_bswap32(value);
  std::memcpy(base + guest_address + GuestPhysicalHostOffset(guest_address),
              &raw, sizeof(raw));
}

float LoadGuestF32(uint8_t *base, uint32_t guest_address) {
  const uint32_t bits = LoadGuestU32(base, guest_address);
  float value = 0.0f;
  std::memcpy(&value, &bits, sizeof(value));
  return value;
}

void StoreGuestF32(uint8_t *base, uint32_t guest_address, float value) {
  uint32_t bits = 0;
  std::memcpy(&bits, &value, sizeof(bits));
  StoreGuestU32(base, guest_address, bits);
}

uint8_t LoadGuestU8(uint8_t *base, uint32_t guest_address) {
  if (!IsReadableGuestByteRange(base, guest_address, sizeof(uint8_t))) {
    return 0;
  }

  return *(base + guest_address + GuestPhysicalHostOffset(guest_address));
}

void StoreGuestU8(uint8_t *base, uint32_t guest_address, uint8_t value) {
  if (!IsReadableGuestByteRange(base, guest_address, sizeof(uint8_t))) {
    return;
  }

  *(base + guest_address + GuestPhysicalHostOffset(guest_address)) = value;
}

uint16_t LoadGuestU16(uint8_t *base, uint32_t guest_address) {
  if (!IsReadableGuestByteRange(base, guest_address, sizeof(uint16_t))) {
    return 0;
  }

  uint16_t raw = 0;
  std::memcpy(&raw,
              base + guest_address + GuestPhysicalHostOffset(guest_address),
              sizeof(raw));
  return __builtin_bswap16(raw);
}

uint64_t LoadGuestU64(uint8_t *base, uint32_t guest_address) {
  const uint64_t high = LoadGuestU32(base, guest_address);
  const uint64_t low = LoadGuestU32(base, guest_address + sizeof(uint32_t));
  return (high << 32u) | low;
}

void StoreGuestU64(uint8_t *base, uint32_t guest_address, uint64_t value) {
  StoreGuestU32(base, guest_address, static_cast<uint32_t>(value >> 32u));
  StoreGuestU32(base, guest_address + sizeof(uint32_t),
                static_cast<uint32_t>(value));
}

struct FableNativeCommandSubmitEmulation {
  bool handled = false;
  uint64_t result = 0;
};

struct FableNativeResourceEmulation {
  bool handled = false;
  uint64_t result = 0;
};

struct FableVertexDeclarationElementCount {
  bool valid = false;
  uint32_t count = 0;
};

bool IsLikelyFableObjectAddress(uint32_t guest_address);
bool IsLikelyGuestDwordAddress(uint32_t guest_address);
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
  return symbol == "sub_82A42FA0"; // Fable::BinkVideoPlaneSetup
}

bool IsFableBinkVideoPlaneSurfaceSetupHook(std::string_view symbol) {
  return symbol == "sub_82A4E8D0"; // Fable::BinkVideoPlaneSurfaceSetup
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

bool IsFableNativeCpuTilingHelperHook(std::string_view symbol) {
  return symbol == "sub_8300C290" || // XGRAPHICS__TileSurface
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
  constexpr uint32_t kMaxBinkPlaneDimension = 4096u;
  return RendererReplaceResourcesEnabled() && base != nullptr &&
         IsFableBinkVideoPlaneSetupHook(symbol) && args.r4 != 0 &&
         args.r5 != 0 && args.r4 <= kMaxBinkPlaneDimension &&
         args.r5 <= kMaxBinkPlaneDimension &&
         IsLikelyFableWritableDataRange(args.r6 + 4u, 8u) &&
         IsLikelyFableWritableDataRange(args.r7, 4u);
}

bool CanEmulateFableBinkVideoPlaneSurfaceSetup(std::string_view symbol,
                                               uint8_t *base) {
  return RendererReplaceResourcesEnabled() && base != nullptr &&
         IsFableBinkVideoPlaneSurfaceSetupHook(symbol);
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

FableNativeResourceEmulation EmulateFableVertexBufferHeader(
    const RendererHookArgs &args, uint8_t *base);
FableNativeResourceEmulation EmulateFableIndexBufferHeader(
    const RendererHookArgs &args, uint8_t *base);

FableNativeResourceEmulation EmulateFableBufferCreate(
    std::string_view symbol, const RendererHookArgs &args, PPCContext &ctx,
    uint8_t *base) {
  FableNativeResourceEmulation emulation;
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
          "Refii renderer Plume resource replacement suppressed native D3D "
          "buffer creation for unsupported Fable shape {} r3=0x{:08X} "
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

FableNativeResourceEmulation EmulateFableVertexDeclarationCreate(
    std::string_view symbol, const RendererHookArgs &args, PPCContext &ctx,
    uint8_t *base) {
  FableNativeResourceEmulation emulation;
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

FableNativeResourceEmulation EmulateFableShaderCreate(
    std::string_view symbol, const RendererHookArgs &args, PPCContext &ctx,
    uint8_t *base) {
  FableNativeResourceEmulation emulation;
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

FableNativeResourceEmulation EmulateFableVertexShaderRegister(
    std::string_view symbol, const RendererHookArgs &args, PPCContext &ctx,
    uint8_t *base) {
  FableNativeResourceEmulation emulation;
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

FableNativeResourceEmulation EmulateFableShaderPatchHelper(
    std::string_view symbol, const RendererHookArgs &args, PPCContext &ctx,
    uint8_t *base) {
  FableNativeResourceEmulation emulation;
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

FableNativeResourceEmulation EmulateFableShaderUtility(
    std::string_view symbol, const RendererHookArgs &args, PPCContext &ctx,
    uint8_t *base) {
  FableNativeResourceEmulation emulation;
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

FableNativeResourceEmulation EmulateFableTextureCreate(
    std::string_view symbol, const RendererHookArgs &args, PPCContext &ctx,
    uint8_t *base) {
  FableNativeResourceEmulation emulation;
  if (!CanEmulateFableTextureCreate(symbol, args, base)) {
    return emulation;
  }

  constexpr uint32_t kTextureObjectBytes = 52u;
  constexpr uint32_t kTextureObjectAllocFlags = 0x64800000u;
  constexpr uint32_t kSyntheticStackFrameBytes = 224u;
  constexpr uint32_t kMaxOwnedTextureBytes = 64u * 1024u * 1024u;
  const uint32_t object = AllocateFableGuestMemory(
      ctx, base, kTextureObjectBytes, kTextureObjectAllocFlags);
  if (!IsLikelyFableObjectRange(object, kTextureObjectBytes)) {
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

FableNativeResourceEmulation EmulateFableSurfaceCreate(
    std::string_view symbol, const RendererHookArgs &args, PPCContext &ctx,
    uint8_t *base) {
  FableNativeResourceEmulation emulation;
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

FableNativeResourceEmulation EmulateFableResourceLifetime(
    std::string_view symbol, const RendererHookArgs &args, uint8_t *base);

FableNativeResourceEmulation EmulateFableSurfaceWrapperCreate(
    std::string_view symbol, const RendererHookArgs &args, PPCContext &ctx,
    uint8_t *base) {
  FableNativeResourceEmulation emulation;
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
  FableNativeResourceEmulation surface =
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

FableNativeResourceEmulation EmulateFableRenderDepthSurfaceCtor(
    std::string_view symbol, const RendererHookArgs &args, PPCContext &ctx,
    uint8_t *base) {
  FableNativeResourceEmulation emulation;
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
  const FableNativeResourceEmulation texture =
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

FableNativeResourceEmulation EmulateFableRenderTargetSetup(
    std::string_view symbol, PPCContext &ctx, uint8_t *base) {
  FableNativeResourceEmulation emulation;
  if (!CanEmulateFableRenderTargetSetup(symbol, base)) {
    return emulation;
  }

  PPCContext helper_ctx = ctx;
  if (symbol == "rex_lhGfx_SetupRenderTargets") {
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

FableNativeResourceEmulation EmulateFableSurfaceViewCreate(
    std::string_view symbol, const RendererHookArgs &args, PPCContext &ctx,
    uint8_t *base) {
  FableNativeResourceEmulation emulation;
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

FableNativeResourceEmulation EmulateFableResourceLifetimeInternal(
    std::string_view symbol, const RendererHookArgs &args, uint8_t *base,
    uint32_t recursion_depth) {
  FableNativeResourceEmulation emulation;
  if (!RendererReplaceResourcesEnabled() || base == nullptr ||
      !IsFableResourceLifetimeHook(symbol)) {
    return emulation;
  }
  if (!IsLikelyFableObjectAddress(args.r3)) {
    emulation.handled = true;
    emulation.result = 0;
    if (!g_logged_fable_resource_lifetime_shape) {
      REXLOG_WARN(
          "Refii renderer Plume resource replacement suppressed native D3D "
          "resource lifetime for unsupported Fable shape {} r3=0x{:08X}",
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

FableNativeResourceEmulation EmulateFableResourceLifetime(
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

FableNativeResourceEmulation EmulateFableVertexBufferHeader(
    const RendererHookArgs &args, uint8_t *base) {
  FableNativeResourceEmulation emulation;
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

FableNativeResourceEmulation EmulateFableIndexBufferHeader(
    const RendererHookArgs &args, uint8_t *base) {
  FableNativeResourceEmulation emulation;
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

FableNativeResourceEmulation EmulateFableConstantBufferHeader(
    const RendererHookArgs &args, uint8_t *base) {
  FableNativeResourceEmulation emulation;
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

FableNativeResourceEmulation EmulateFableResourceHeaderBuild(
    std::string_view symbol, const RendererHookArgs &args, uint8_t *base) {
  FableNativeResourceEmulation emulation;
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

FableNativeResourceEmulation EmulateFableResourceUpdate(
    std::string_view symbol, const RendererHookArgs &args, uint8_t *base) {
  FableNativeResourceEmulation emulation;
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

uint32_t AlignFableBinkPlaneBytes(uint32_t byte_size) {
  constexpr uint32_t kBinkPlaneAlignment = 4096u;
  return (byte_size + kBinkPlaneAlignment - 1u) & ~(kBinkPlaneAlignment - 1u);
}

FableNativeResourceEmulation EmulateFableBinkVideoPlaneSetup(
    std::string_view symbol, const RendererHookArgs &args, PPCContext &ctx,
    uint8_t *base) {
  FableNativeResourceEmulation emulation;
  if (!CanEmulateFableBinkVideoPlaneSetup(symbol, args, base)) {
    return emulation;
  }

  constexpr uint32_t kTextureObjectBytes = 52u;
  constexpr uint32_t kHeaderStackBytes = 192u;
  constexpr uint32_t kTextureHeaderFlags = 0x04900002u;
  constexpr uint32_t kMaxPlaneBytes = 16u * 1024u * 1024u;
  const uint32_t texture =
      AllocateFableGuestObject(ctx, base, kTextureObjectBytes);
  if (!IsLikelyFableObjectRange(texture, kTextureObjectBytes)) {
    StoreGuestU32(base, args.r7, 0u);
    StoreGuestU32(base, args.r6 + 4u, 0u);
    StoreGuestU32(base, args.r6 + 8u, 0u);
    emulation.handled = true;
    return emulation;
  }

  ZeroGuestDwords(base, texture, kTextureObjectBytes);
  StoreGuestU32(base, args.r7, texture);

  PPCContext header_ctx = ctx;
  header_ctx.r1.u32 = ctx.r1.u32 - kHeaderStackBytes;
  StoreGuestU32(base, header_ctx.r1.u32 + 84u, 0u);
  StoreGuestU32(base, header_ctx.r1.u32 + 92u, texture);
  StoreGuestU32(base, header_ctx.r1.u32 + 100u, 0u);
  StoreGuestU32(base, header_ctx.r1.u32 + 108u, 0u);
  header_ctx.r3.u64 = args.r4;
  header_ctx.r4.u64 = args.r5;
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
  const uint32_t bytes_per_pixel = FableTextureFormatBytesPerPixel(fetch.format);
  const uint64_t row_pitch_bytes =
      uint64_t(pitch_pixels) * uint64_t(bytes_per_pixel);
  const uint32_t computed_plane_bytes =
      row_pitch_bytes != 0 && row_pitch_bytes <= UINT32_MAX &&
              FableTextureFetchHeight(fetch) <= UINT32_MAX / row_pitch_bytes
          ? static_cast<uint32_t>(row_pitch_bytes *
                                  FableTextureFetchHeight(fetch))
          : 0u;
  uint32_t plane_bytes = std::max(header_ctx.r3.u32, computed_plane_bytes);
  if (plane_bytes == 0 || plane_bytes > kMaxPlaneBytes) {
    plane_bytes = args.r4 <= UINT32_MAX / args.r5 ? args.r4 * args.r5 : 0u;
  }
  if (plane_bytes == 0 || plane_bytes > kMaxPlaneBytes) {
    StoreGuestU32(base, args.r7, 0u);
    StoreGuestU32(base, args.r6 + 4u, 0u);
    StoreGuestU32(base, args.r6 + 8u, 0u);
    emulation.handled = true;
    return emulation;
  }

  const uint32_t aligned_plane_bytes = AlignFableBinkPlaneBytes(plane_bytes);
  const uint32_t plane_memory = AllocateFableGuestMemory(
      ctx, base, aligned_plane_bytes, FableTextureCreateMemoryFlags(0));
  if (!IsLikelyGuestMemoryRange(plane_memory, aligned_plane_bytes)) {
    StoreGuestU32(base, args.r7, 0u);
    StoreGuestU32(base, args.r6 + 4u, 0u);
    StoreGuestU32(base, args.r6 + 8u, 0u);
    emulation.handled = true;
    return emulation;
  }

  if (IsReadableGuestByteRange(base, plane_memory, aligned_plane_bytes)) {
    std::memset(base + plane_memory + GuestPhysicalHostOffset(plane_memory), 0,
                aligned_plane_bytes);
  }
  const uint32_t fetch_base = LoadGuestU32(base, texture + 32u);
  StoreGuestU32(base, texture + 32u,
                ((fetch_base & 0xFFFFF000u) + plane_memory) |
                    (fetch_base & 0xFFFu));
  StoreGuestU32(base, args.r6 + 4u, plane_memory);
  StoreGuestU32(base, args.r6 + 8u,
                row_pitch_bytes != 0 && row_pitch_bytes <= UINT32_MAX
                    ? static_cast<uint32_t>(row_pitch_bytes)
                    : args.r4 * bytes_per_pixel);
  UpdateFableLockDirtyRange(base, texture, 0x14u, plane_memory, plane_memory,
                            plane_bytes);
  StoreGuestU32(base, texture, LoadGuestU32(base, texture) + 0x100u);

  emulation.handled = true;
  emulation.result = texture;
  if (!g_logged_fable_bink_video_plane_emulation) {
    REXLOG_INFO(
        "Refii renderer Plume resource replacement now owns Fable Bink video "
        "plane texture allocation and lock addresses for sub_82A42FA0");
    g_logged_fable_bink_video_plane_emulation = true;
  }
  return emulation;
}

FableNativeResourceEmulation EmulateFableBinkVideoPlaneSurfaceSetup(
    std::string_view symbol, uint8_t *base) {
  FableNativeResourceEmulation emulation;
  if (!CanEmulateFableBinkVideoPlaneSurfaceSetup(symbol, base)) {
    return emulation;
  }

  emulation.handled = true;
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

FableNativeResourceEmulation EmulateFableTextureSurfaceLock(
    std::string_view symbol, const RendererHookArgs &args, uint8_t *base) {
  FableNativeResourceEmulation emulation;
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

FableNativeResourceEmulation EmulateFableSurfaceCopy(
    std::string_view symbol, const RendererHookArgs &args, uint8_t *base) {
  FableNativeResourceEmulation emulation;
  FableSurfaceCopyShape shape;
  if (!TryGetFableSurfaceCopyShape(symbol, args, base, shape)) {
    if (RendererReplaceResourcesEnabled() && base != nullptr &&
        IsFableSurfaceCopyHook(symbol)) {
      emulation.handled = true;
      emulation.result = 0;
      if (!g_logged_fable_surface_copy_shape) {
        REXLOG_WARN(
            "Refii renderer Plume resource replacement suppressed native "
            "D3DX surface copy for unsupported Fable shape {} r3=0x{:08X} "
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

FableNativeResourceEmulation EmulateFableSurfaceMemoryCopy(
    std::string_view symbol, const RendererHookArgs &args, PPCContext &ctx,
    uint8_t *base) {
  FableNativeResourceEmulation emulation;
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
          "Refii renderer native D3DX surface upload handled shape outside "
          "linear dirty-range model {} r3=0x{:08X} r5=0x{:08X} r6=0x{:08X} "
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
      !g_logged_fable_surface_memory_copy_linear_fallback) {
    REXLOG_INFO(
        "Refii renderer copied D3DXLoadSurfaceFromMemory linear rows after "
        "blank native destination dst_surface=0x{:08X} texture=0x{:08X} "
        "dst_bits=0x{:08X} src_bits=0x{:08X} rows={} row_bytes={} "
        "dst_pitch={} src_pitch={}",
        shape.destination.surface, shape.destination.texture,
        shape.destination.bits, shape.source_bits, shape.rows,
        shape.source_row_bytes, shape.destination.pitch_bytes,
        shape.source_pitch_bytes);
    g_logged_fable_surface_memory_copy_linear_fallback = true;
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

FableNativeResourceEmulation EmulateFableTextureFill(
    std::string_view symbol, const RendererHookArgs &args, uint8_t *base) {
  FableNativeResourceEmulation emulation;
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

FableNativeResourceEmulation EmulateFableTextureFileCreate(
    std::string_view symbol, const RendererHookArgs &args, PPCContext &ctx,
    uint8_t *base) {
  FableNativeResourceEmulation emulation;
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

FableNativeResourceEmulation EmulateFableResourceQuery(
    std::string_view symbol, const RendererHookArgs &args, uint8_t *base) {
  FableNativeResourceEmulation emulation;
  if (!CanEmulateFableResourceQuery(symbol, args, base)) {
    return emulation;
  }

  if (symbol == "sub_82B98C88") {
    emulation.result = LoadGuestU32(base, args.r3 + 24u);
  } else if (symbol == "sub_821FAB38") {
    FableResourceQueryDesc desc;
    TryGetFableTextureQueryDesc(base, args.r3, 0, desc);
    StoreGuestU32(base, args.r4, desc.width);
    StoreGuestU32(base, args.r5, desc.height);
    StoreGuestU32(base, args.r6, desc.depth);
    emulation.result = args.r3;
  } else if (symbol == "sub_821FA070") {
    FableResourceQueryDesc desc;
    TryGetFableSurfaceQueryDesc(base, args.r3, desc);
    StoreFableSurfaceDesc(base, args.r4, desc);
    emulation.result = args.r3;
  } else if (symbol == "sub_821FABD0") {
    FableResourceQueryDesc desc;
    TryGetFableTextureQueryDesc(base, args.r3, args.r4, desc);
    StoreFableSurfaceDesc(base, args.r5, desc);
    emulation.result = args.r3;
  } else if (symbol == "sub_82B985C8") {
    FableResourceQueryDesc desc;
    TryGetFableTextureQueryDesc(base, args.r3, args.r4, desc);
    StoreFableVolumeDesc(base, args.r5, desc,
                         FableResourceTypeFromTexture(base, args.r3));
    emulation.result = args.r3;
  } else if (symbol == "sub_821FAD60") {
    FableResourceQueryDesc desc;
    TryGetFableTextureQueryDesc(base, args.r3, args.r4, desc);
    StoreFableLevelSize(base, args, desc,
                        LoadFableTextureFetch(base, args.r3).format);
    emulation.result = args.r3;
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

FableNativeResourceEmulation EmulateFableTextureAddressOffset(
    std::string_view symbol, const RendererHookArgs &args, uint8_t *base) {
  FableNativeResourceEmulation emulation;
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

FableNativeResourceEmulation EmulateFableTextureHeaderBuild(
    std::string_view symbol, PPCContext &ctx, uint8_t *base) {
  FableNativeResourceEmulation emulation;
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

FableNativeResourceEmulation EmulateFableTextureHeaderUtility(
    std::string_view symbol, PPCContext &ctx, uint8_t *base) {
  FableNativeResourceEmulation emulation;
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

  if (g_logged_fable_cpu_layout_blank_fast_path_skips < 16u) {
    REXLOG_INFO(
        "Refii renderer skipped Fable CPU layout fast path for blank source "
        "#{} hook={} source=0x{:08X} size={} scan={}",
        g_logged_fable_cpu_layout_blank_fast_path_skips + 1u, symbol, source,
        size, scan_size);
    ++g_logged_fable_cpu_layout_blank_fast_path_skips;
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

FableNativeResourceEmulation EmulateFableCpuResourceHelper(
    std::string_view symbol, PPCContext &ctx, uint8_t *base) {
  FableNativeResourceEmulation emulation;
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

FableNativeResourceEmulation EmulateFableResourceLock(
    std::string_view symbol, const RendererHookArgs &args, uint8_t *base) {
  FableNativeResourceEmulation emulation;
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

FableNativeResourceEmulation EmulateFableDirectResourceHook(
    std::string_view symbol, const RendererHookArgs &args, PPCContext &ctx,
    uint8_t *base) {
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

FableNativeResourceEmulation EmulateFableNativeResourceHook(
    std::string_view symbol, const RendererHookArgs &args, PPCContext &ctx,
    uint8_t *base) {
  if (const FableNativeResourceEmulation exact =
          EmulateFableDirectResourceHook(symbol, args, ctx, base);
      exact.handled) {
    return exact;
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

uint32_t EmulateFableAllocateCommandSegment(uint32_t device, uint8_t *base,
                                            uint32_t requested_words);
FableNativeCommandSubmitEmulation ApplyFableScissorState(
    uint8_t *base, uint32_t device, int32_t left, int32_t top, int32_t right,
    int32_t bottom, bool store_requested_rect);

bool IsLikelyFableObjectAddress(uint32_t guest_address) {
  constexpr uint32_t kFableObjectAddressMin = 0x40000000u;
  constexpr uint32_t kFableObjectAddressMax = 0x80000000u;
  return guest_address >= kFableObjectAddressMin &&
         guest_address < kFableObjectAddressMax &&
         (guest_address & (alignof(uint32_t) - 1u)) == 0;
}

bool IsLikelyGuestDwordAddress(uint32_t guest_address) {
  constexpr uint32_t kFableObjectAddressMax = 0x80000000u;
  return guest_address >= 0x1000u && guest_address < kFableObjectAddressMax &&
         (guest_address & (alignof(uint32_t) - 1u)) == 0;
}

uint32_t LoadHookStackU32(uint8_t *base, const RendererHookArgs &args,
                          uint32_t offset) {
  const uint32_t address = args.r1 + offset;
  return base && IsLikelyGuestDwordAddress(address) ? LoadGuestU32(base, address)
                                                    : 0u;
}

bool IsLikelyGuestMemoryAddress(uint32_t guest_address) {
  constexpr uint32_t kFableObjectAddressMax = 0x80000000u;
  return guest_address >= 0x1000u &&
         (guest_address < kFableObjectAddressMax ||
          guest_address >= 0xE0000000u);
}

uint32_t CountLeadingZeroBits64(uint64_t value) {
#if defined(__GNUC__) || defined(__clang__)
  return value == 0 ? 64u : static_cast<uint32_t>(__builtin_clzll(value));
#else
  if (value == 0) {
    return 64u;
  }
  uint32_t count = 0;
  while ((value & 0x8000000000000000ull) == 0) {
    value <<= 1u;
    ++count;
  }
  return count;
#endif
}

struct FableSurfaceExtent {
  uint32_t width = 0;
  uint32_t height = 0;
};

bool IsPlausibleFableSurfaceExtent(uint32_t width, uint32_t height) {
  constexpr uint32_t kMaxFableSurfaceExtent = 2048;
  constexpr uint64_t kMaxFableSurfacePixels =
      static_cast<uint64_t>(kMaxFableSurfaceExtent) *
      static_cast<uint64_t>(kMaxFableSurfaceExtent);
  return width != 0 && height != 0 && width <= kMaxFableSurfaceExtent &&
         height <= kMaxFableSurfaceExtent &&
         static_cast<uint64_t>(width) * height <= kMaxFableSurfacePixels;
}

FableSurfaceExtent DecodeFableSurfaceExtent(uint8_t *base, uint32_t surface) {
  if (base == nullptr || !IsLikelyFableObjectAddress(surface)) {
    return {};
  }

  constexpr uint32_t kSurfacePackedExtentOffset = 0x0024;
  const uint32_t packed_extent =
      LoadGuestU32(base, surface + kSurfacePackedExtentOffset);
  const uint32_t width = (packed_extent >> 18u) + 1u;
  const uint32_t height = ((packed_extent >> 3u) & 0x7FFFu) + 1u;
  if (!IsPlausibleFableSurfaceExtent(width, height)) {
    return {};
  }
  return {.width = width, .height = height};
}

FableSurfaceExtent CurrentFableDrawSurfaceExtent(uint8_t *base,
                                                 uint32_t device) {
  if (base == nullptr || !IsLikelyFableObjectAddress(device)) {
    return {};
  }

  constexpr uint32_t kRenderTargetBaseOffset = 0x3098;
  constexpr uint32_t kDepthStencilOffset = 0x30A8;
  const uint32_t color_surface =
      LoadGuestU32(base, device + kRenderTargetBaseOffset);
  FableSurfaceExtent extent = DecodeFableSurfaceExtent(base, color_surface);
  if (extent.width != 0 && extent.height != 0) {
    return extent;
  }

  const uint32_t depth_surface = LoadGuestU32(base, device + kDepthStencilOffset);
  return DecodeFableSurfaceExtent(base, depth_surface);
}

FableSurfaceExtent CurrentFableViewportClampExtent(uint8_t *base,
                                                   uint32_t device) {
  if (base == nullptr || !IsLikelyFableObjectAddress(device)) {
    return {};
  }

  constexpr uint32_t kSurfaceFlagsOffset = 0x2ABC;
  constexpr uint32_t kRenderTargetBaseOffset = 0x3098;
  constexpr uint32_t kDepthStencilOffset = 0x30A8;
  constexpr uint32_t kTrackedSurfaceBaseOffset = 0x31B8;
  constexpr uint32_t kTrackedDepthOffset = 0x31C8;
  constexpr uint32_t kViewportClampWidthOffset = 0x337C;
  constexpr uint32_t kViewportClampHeightOffset = 0x3380;

  const uint8_t flags = LoadGuestU8(base, device + kSurfaceFlagsOffset);
  bool use_override_extent = (flags & 0x10u) != 0;
  if (!use_override_extent && (flags & 0x20u) != 0) {
    use_override_extent = true;
    for (uint32_t slot = 0; slot < 4u; ++slot) {
      const uint32_t surface =
          LoadGuestU32(base, device + kRenderTargetBaseOffset + slot * 4u);
      const uint32_t tracked =
          LoadGuestU32(base, device + kTrackedSurfaceBaseOffset + slot * 4u);
      if (surface != 0 && tracked != surface) {
        use_override_extent = false;
        break;
      }
    }
    const uint32_t depth_surface = LoadGuestU32(base, device + kDepthStencilOffset);
    const uint32_t tracked_depth = LoadGuestU32(base, device + kTrackedDepthOffset);
    if (depth_surface != 0 && tracked_depth != depth_surface) {
      use_override_extent = false;
    }
  }

  if (use_override_extent) {
    const uint32_t width = LoadGuestU32(base, device + kViewportClampWidthOffset);
    const uint32_t height = LoadGuestU32(base, device + kViewportClampHeightOffset);
    if (IsPlausibleFableSurfaceExtent(width, height)) {
      return {.width = width, .height = height};
    }
  }

  return CurrentFableDrawSurfaceExtent(base, device);
}

RendererScissorSnapshot MakeFableScissor(int32_t left, int32_t top,
                                         int32_t right, int32_t bottom) {
  RendererScissorSnapshot scissor;
  if (right > left && bottom > top) {
    scissor.left = left;
    scissor.top = top;
    scissor.right = right;
    scissor.bottom = bottom;
    scissor.valid = true;
  }
  return scissor;
}

RendererViewportSnapshot MakeFableViewport(float x, float y, float width,
                                           float height, float min_depth,
                                           float max_depth) {
  RendererViewportSnapshot viewport;
  if (std::isfinite(x) && std::isfinite(y) && std::isfinite(width) &&
      std::isfinite(height) && std::isfinite(min_depth) &&
      std::isfinite(max_depth) && width >= 0.0f && height >= 0.0f) {
    viewport.x = x;
    viewport.y = y;
    viewport.width = width;
    viewport.height = height;
    viewport.min_depth = min_depth;
    viewport.max_depth = max_depth;
    viewport.valid = true;
  }
  return viewport;
}

void OrFableGuestQwordLow(uint8_t *base, uint32_t guest_address,
                          uint32_t bits) {
  StoreGuestU32(base, guest_address + sizeof(uint32_t),
                LoadGuestU32(base, guest_address + sizeof(uint32_t)) | bits);
}

void OrFableGuestQword(uint8_t *base, uint32_t guest_address, uint64_t bits) {
  StoreGuestU64(base, guest_address, LoadGuestU64(base, guest_address) | bits);
}

void AndNotFableGuestQword(uint8_t *base, uint32_t guest_address,
                           uint64_t mask) {
  StoreGuestU64(base, guest_address, LoadGuestU64(base, guest_address) & ~mask);
}

void RetireFableBoundResourceObject(uint8_t *base, uint32_t device,
                                    uint32_t object) {
  if (base == nullptr || device == 0 || object == 0) {
    return;
  }

  constexpr uint32_t kCurrentBackBufferOffset = 0x2A9C;
  constexpr uint32_t kBackBufferDirtyMaskOffset = 0x2AA0;
  constexpr uint32_t kExportListWriteOffset = 0x34D4;
  constexpr uint32_t kExportListLimitOffset = 0x34D8;

  const uint32_t current_back_buffer =
      LoadGuestU32(base, device + kCurrentBackBufferOffset);
  if (current_back_buffer != 0) {
    StoreGuestU32(base, object + 8u, current_back_buffer);
    return;
  }

  if ((LoadGuestU32(base, device + kBackBufferDirtyMaskOffset) &
       LoadGuestU32(base, object)) == 0) {
    return;
  }

  const uint32_t write = LoadGuestU32(base, device + kExportListWriteOffset);
  const uint32_t limit = LoadGuestU32(base, device + kExportListLimitOffset);
  if (write != 0 && write + 2u * sizeof(uint32_t) <= limit) {
    StoreGuestU32(base, write, (object >> 2u) & 0xBFFFFFFFu);
    StoreGuestU32(base, write + sizeof(uint32_t), 0xFFFFFFFFu);
    StoreGuestU32(base, device + kExportListWriteOffset,
                  write + 2u * sizeof(uint32_t));
  }
}

void ApplyFableShaderPatchStream(uint8_t *base, uint32_t device,
                                 uint32_t header, uint32_t dirty_qword_offset) {
  if (base == nullptr || device == 0 || header == 0) {
    return;
  }

  constexpr uint32_t kShaderConstantsBaseOffset = 0x0480;
  constexpr uint32_t kDeviceWideDirtyQwordOffset = 0x0020;
  constexpr uint64_t kShaderPatchWideDirtyBit = 0x0100000000000000ull;
  constexpr uint32_t kMaxShaderPatchBytes = 0x10000;

  AndNotFableGuestQword(base, device + dirty_qword_offset,
                        LoadGuestU64(base, header));
  if (LoadGuestU64(base, header + 8u) != 0) {
    OrFableGuestQword(base, device + kDeviceWideDirtyQwordOffset,
                      kShaderPatchWideDirtyBit);
  }

  const uint32_t patch_size = LoadGuestU32(base, header + 0x10u);
  if (patch_size == 0 || patch_size > kMaxShaderPatchBytes) {
    return;
  }

  uint32_t cursor = header + 0x14u;
  const uint32_t end = cursor + patch_size;
  while (cursor + 4u <= end) {
    const uint16_t count = LoadGuestU16(base, cursor + 2u);
    cursor += 4u;
    if (count == 0) {
      break;
    }
    cursor += 4u;
  }

  if (cursor >= end) {
    return;
  }

  while (cursor + 4u <= end) {
    const uint16_t destination = LoadGuestU16(base, cursor);
    const uint16_t count = LoadGuestU16(base, cursor + 2u);
    cursor += 4u;
    if (count == 0) {
      break;
    }
    const uint32_t bytes = count * sizeof(uint32_t);
    if (cursor + bytes > end) {
      return;
    }
    for (uint32_t i = 0; i < count; ++i) {
      StoreGuestU32(base,
                    device + kShaderConstantsBaseOffset + destination +
                        i * sizeof(uint32_t),
                    LoadGuestU32(base, cursor + i * sizeof(uint32_t)));
    }
    cursor += bytes;
  }

  while (cursor + 4u <= end) {
    const uint16_t destination = LoadGuestU16(base, cursor);
    uint16_t count = LoadGuestU16(base, cursor + 2u);
    cursor += 4u;
    if (count == 0) {
      break;
    }
    uint32_t destination_address =
        device + kShaderConstantsBaseOffset + destination;
    do {
      if (cursor + 2u * sizeof(uint32_t) > end) {
        return;
      }
      const uint32_t mask = LoadGuestU32(base, cursor);
      const uint32_t value = LoadGuestU32(base, cursor + sizeof(uint32_t));
      StoreGuestU32(base, destination_address,
                    (LoadGuestU32(base, destination_address) & mask) | value);
      cursor += 2u * sizeof(uint32_t);
      destination_address += sizeof(uint32_t);
      count = static_cast<uint16_t>(count - 2u);
    } while (count != 0);
  }
}

void EmulateFablePendingStateDispatch(uint32_t record, uint8_t *base,
                                      uint32_t source_lr,
                                      bool notify_observer) {
  if (record == 0) {
    return;
  }

  const uint8_t dispatch_kind = LoadGuestU8(base, record + 21u);
  if (dispatch_kind != 1u && dispatch_kind != 2u) {
    StoreGuestU8(base, record + 20u, 0);
    return;
  }

  const uint32_t current_value = LoadGuestU32(base, record);
  const uint32_t pending_value = LoadGuestU32(base, record + 4u);
  StoreGuestU8(base, record + 20u, 0);
  if (current_value == pending_value) {
    return;
  }

  if (notify_observer) {
    RendererHookArgs dispatch_args = {};
    dispatch_args.r3 = record;
    dispatch_args.lr = source_lr;
    dispatch_args.r3_u64 = record;
    ObserveRendererStateDispatch(dispatch_args, base);
  }
  StoreGuestU32(base, record, pending_value);
}

void EmulateFableDeferredStateDispatch(uint8_t *base, uint32_t source_lr) {
  if (base == nullptr) {
    return;
  }

  constexpr uint32_t kDeferredStateCount = 0x83490DB0;
  constexpr uint32_t kDeferredStateRecords = 0x83490DB8;
  constexpr uint32_t kPendingQueueBegin = 0x834B2390;
  constexpr uint32_t kMaxMirroredDeferredRecords =
      (kPendingQueueBegin - kDeferredStateRecords) / sizeof(uint32_t);

  const uint32_t count =
      std::min(LoadGuestU32(base, kDeferredStateCount),
               kMaxMirroredDeferredRecords);
  for (uint32_t i = 0; i < count; ++i) {
    const uint32_t record =
        LoadGuestU32(base, kDeferredStateRecords + i * sizeof(uint32_t));
    EmulateFablePendingStateDispatch(record, base, source_lr, true);
  }
  StoreGuestU32(base, kDeferredStateCount, 0);
}

void EmulateFableDeferredStatePostDispatch(uint8_t *base) {
  if (base == nullptr) {
    return;
  }

  constexpr uint32_t kDeviceAddress = 0x834970E8;
  constexpr uint32_t kDepthModeEnableAddress = 0x834B644C;
  constexpr uint32_t kSrcBlendAddress = 0x834B6494;
  constexpr uint32_t kDestBlendAddress = 0x834B64AC;
  constexpr uint32_t kHighPrecisionBlendOffset = 12028;
  constexpr uint32_t kBlendControlOffset = 10372;
  constexpr uint32_t kBlendControlValidOffset = 12440;
  constexpr uint32_t kDirtyQwordOffset = 16;
  constexpr uint32_t kBlendControlDirtyBit = 0x01000000;

  uint32_t high_precision_blend = 0;
  if (LoadGuestU32(base, kDepthModeEnableAddress) == 1u) {
    const uint32_t src_blend = LoadGuestU32(base, kSrcBlendAddress);
    const uint32_t dest_blend = LoadGuestU32(base, kDestBlendAddress);
    high_precision_blend =
        ((src_blend == 1u && dest_blend != 1u) ||
         (src_blend == 14u && dest_blend != 15u))
            ? 1u
            : 0u;
  }

  const uint32_t device = LoadGuestU32(base, kDeviceAddress);
  if (device == 0) {
    return;
  }

  StoreGuestU32(base, device + kHighPrecisionBlendOffset,
                high_precision_blend);
  if (LoadGuestU32(base, device + kBlendControlValidOffset) == 0) {
    return;
  }

  const uint32_t blend_control = LoadGuestU32(base, device + kBlendControlOffset);
  const uint32_t mode = (blend_control >> 16u) & 0xFu;
  if ((mode == 2u || mode == 3u || mode == 10u || mode == 12u) &&
      (((blend_control >> 19u) & 1u) != high_precision_blend)) {
    const uint32_t replacement =
        (((~(high_precision_blend - 1u) << 16u) & ((mode + 3u) << 17u)) |
         ((((mode >> 1u) - 3u) & (high_precision_blend - 1u)) << 16u)) &
        0x000F0000u;
    StoreGuestU32(base, device + kBlendControlOffset,
                  (blend_control & 0xFFF0FFFFu) | replacement);
    OrFableGuestQwordLow(base, device + kDirtyQwordOffset,
                         kBlendControlDirtyBit);
  }
}

FableNativeCommandSubmitEmulation EmulateFableDeferredStateDispatchHook(
    const RendererHookArgs &args, uint8_t *base) {
  FableNativeCommandSubmitEmulation emulation;
  if (base == nullptr) {
    return emulation;
  }

  emulation.handled = true;
  emulation.result = LoadGuestU32(base, 0x834970E8);
  EmulateFableDeferredStateDispatch(base, args.lr);
  EmulateFableDeferredStatePostDispatch(base);
  return emulation;
}

void AppendFableDeferredStateRecord(uint32_t record, uint8_t *base,
                                    uint32_t source_lr) {
  constexpr uint32_t kDeferredStateCount = 0x83490DB0;
  constexpr uint32_t kDeferredStateRecords = 0x83490DB8;
  constexpr uint32_t kPendingQueueBegin = 0x834B2390;
  constexpr uint32_t kMaxMirroredDeferredRecords =
      (kPendingQueueBegin - kDeferredStateRecords) / sizeof(uint32_t);

  const uint32_t count = LoadGuestU32(base, kDeferredStateCount);
  if (count >= kMaxMirroredDeferredRecords) {
    EmulateFablePendingStateDispatch(record, base, source_lr, true);
    return;
  }

  StoreGuestU8(base, record + 20u, 1);
  StoreGuestU32(base, kDeferredStateCount, count + 1u);
  StoreGuestU32(base, kDeferredStateRecords + count * sizeof(uint32_t),
                record);
}

uint32_t EmulateFablePendingStateQueue(uint32_t queue, uint8_t *base,
                                       uint32_t source_lr) {
  constexpr uint32_t kQueueScaleOffset = 0x2000;
  constexpr uint32_t kQueueCountOffset = 0x2004;

  uint32_t count = LoadGuestU32(base, queue + kQueueCountOffset);
  if (count == 0) {
    StoreGuestU32(base, queue + kQueueScaleOffset,
                  LoadGuestU32(base, queue + kQueueScaleOffset) >> 1u);
    return 0;
  }

  --count;
  StoreGuestU32(base, queue + kQueueCountOffset, count);
  uint32_t record = LoadGuestU32(base, queue + count * 8u);
  while (record != 0 && LoadGuestU8(base, record + 21u) != 3u) {
    const uint32_t queue_scale = LoadGuestU32(base, queue + kQueueScaleOffset);
    StoreGuestU32(base, record + 8u,
                  LoadGuestU32(base, record + 8u) - queue_scale);
    StoreGuestU32(base, record + 4u,
                  LoadGuestU32(base, queue + count * 8u + 4u));

    const bool immediate = LoadGuestU8(base, record + 12u) != 0;
    const bool pending = LoadGuestU8(base, record + 20u) != 0;
    if (immediate) {
      if (pending) {
        EmulateFablePendingStateDispatch(record, base, source_lr, true);
      }
    } else if (!pending) {
      AppendFableDeferredStateRecord(record, base, source_lr);
    }

    count = LoadGuestU32(base, queue + kQueueCountOffset);
    if (count == 0) {
      break;
    }
    --count;
    StoreGuestU32(base, queue + kQueueCountOffset, count);
    record = LoadGuestU32(base, queue + count * 8u);
  }

  StoreGuestU32(base, queue + kQueueScaleOffset,
                LoadGuestU32(base, queue + kQueueScaleOffset) >> 1u);
  return record;
}

FableNativeCommandSubmitEmulation EmulateFablePendingStateDispatchHook(
    const RendererHookArgs &args, uint8_t *base) {
  FableNativeCommandSubmitEmulation emulation;
  if (base == nullptr || args.r3 == 0) {
    return emulation;
  }

  emulation.handled = true;
  emulation.result = args.r3;
  EmulateFablePendingStateDispatch(args.r3, base, args.lr, false);
  return emulation;
}

FableNativeCommandSubmitEmulation EmulateFablePendingStateQueueHook(
    const RendererHookArgs &args, uint8_t *base) {
  FableNativeCommandSubmitEmulation emulation;
  if (base == nullptr || args.r3 == 0) {
    return emulation;
  }

  emulation.handled = true;
  emulation.result = EmulateFablePendingStateQueue(args.r3, base, args.lr);
  return emulation;
}

FableNativeCommandSubmitEmulation EmulateFableFlushPendingGpuState(
    const RendererHookArgs &args, uint8_t *base) {
  FableNativeCommandSubmitEmulation emulation;
  if (base == nullptr || args.r3 == 0) {
    return emulation;
  }

  constexpr uint32_t kPendingQueueBegin = 0x834B2390;
  constexpr uint32_t kPendingQueueEnd = 0x834B63A0;
  constexpr uint32_t kPendingQueueStride = 0x2008;

  emulation.handled = true;
  emulation.result = args.r3;
  if (LoadGuestU8(base, args.r3) == 0) {
    return emulation;
  }

  StoreGuestU8(base, args.r3, 0);
  for (uint32_t queue = kPendingQueueBegin; queue < kPendingQueueEnd;
       queue += kPendingQueueStride) {
    (void)EmulateFablePendingStateQueue(queue, base, args.lr);
  }
  return emulation;
}

uint32_t FableGpuAddress(uint32_t guest_address) {
  return ((((guest_address >> 20u) + 512u) & 0x1000u) +
          (guest_address & 0x1FFFFFFFu));
}

uint32_t FableGpuSegmentPointer(uint32_t guest_address) {
  return FableGpuAddress(guest_address) - 0x40000000u;
}

uint32_t AlignGuestU32(uint32_t value, uint32_t alignment) {
  return (value + alignment - 1u) & ~(alignment - 1u);
}

uint32_t EmulateFableFallbackCommandBuffer(uint32_t device, uint8_t *base) {
  constexpr uint32_t kCommandPointerOffset = 0x0030;
  constexpr uint32_t kCommandEndOffset = 0x0034;
  constexpr uint32_t kCommandLimitOffset = 0x0038;
  constexpr uint32_t kDeviceFlags2Offset = 0x2ABD;
  constexpr uint32_t kInlineCommandBufferOffset = 0x4158;
  constexpr uint32_t kFallbackCommandBufferSize = 4800;
  constexpr uint32_t kFallbackCommandReserve = 160;

  const uint32_t buffer =
      LoadGuestU32(base, device + kInlineCommandBufferOffset);
  if (buffer != 0) {
    StoreGuestU32(base, device + kCommandPointerOffset, buffer);
    StoreGuestU32(base, device + kCommandEndOffset,
                  buffer + kFallbackCommandBufferSize);
    StoreGuestU32(base, device + kCommandLimitOffset,
                  buffer + kFallbackCommandBufferSize -
                      kFallbackCommandReserve);
  } else {
    StoreGuestU32(base, device + kCommandPointerOffset,
                  device + kInlineCommandBufferOffset);
    StoreGuestU32(base, device + kCommandEndOffset,
                  device + kInlineCommandBufferOffset +
                      kFallbackCommandBufferSize);
    StoreGuestU32(base, device + kCommandLimitOffset,
                  device + kInlineCommandBufferOffset +
                      kFallbackCommandBufferSize - kFallbackCommandReserve);
  }
  // Fable's transient scratch allocator treats bit 0x20 here as a hard
  // allocation failure latch. When Plume owns submission, this fallback buffer
  // is our usable command space, so keep the guest allocator live.
  StoreGuestU8(base, device + kDeviceFlags2Offset,
               LoadGuestU8(base, device + kDeviceFlags2Offset) & ~0x20u);
  return 0;
}

uint32_t EmulateFableReserveCommandSpace(uint32_t device, uint8_t *base,
                                         uint32_t &size_bytes,
                                         uint32_t alignment) {
  constexpr uint32_t kCommandPointerOffset = 0x0030;
  constexpr uint32_t kCommandEndOffset = 0x0034;
  constexpr uint32_t kCommandLimitOffset = 0x0038;
  constexpr uint32_t kDeferredFenceOffset = 0x33B0;
  constexpr uint32_t kDeferredEpochOffset = 0x33B4;
  constexpr uint32_t kSegmentRingBaseOffset = 0x3A38;
  constexpr uint32_t kSegmentRingEndOffset = 0x3A3C;
  constexpr uint32_t kSegmentReserveSizeOffset = 0x3A40;
  constexpr uint32_t kSegmentScratchOffset = 0x3A44;
  constexpr uint32_t kSegmentRingTailOffset = 0x3A4C;
  constexpr uint32_t kSegmentEpochOffset = 0x3A48;
  constexpr uint32_t kSegmentMaxPendingSpanOffset = 0x3A58;
  constexpr uint32_t kDeviceFlags3Offset = 0x2ABE;

  const uint32_t requested_words = size_bytes >> 2u;
  uint32_t cursor = LoadGuestU32(base, device + kSegmentScratchOffset);
  if (cursor == 0) {
    cursor = LoadGuestU32(base, device + kCommandPointerOffset) + 4u;
  }

  uint32_t start = AlignGuestU32(cursor, alignment);
  uint32_t words = requested_words != 0 ? requested_words : 54u;
  words = std::max(
      words, LoadGuestU32(base, device + kSegmentReserveSizeOffset) >> 2u);
  const uint32_t reserve_bytes = words * sizeof(uint32_t);
  uint32_t end = start + reserve_bytes;
  const uint32_t ring_end = LoadGuestU32(base, device + kSegmentRingEndOffset);
  if (end > ring_end) {
    end = ring_end;
  }

  const uint32_t primary_base =
      LoadGuestU32(base, device + kSegmentRingBaseOffset);
  const uint32_t ring_tail =
      LoadGuestU32(base, device + kSegmentRingTailOffset);
  bool wrap = ((end - start) >> 2u) < words;
  if (((ring_tail - primary_base) & ~3u) >
      LoadGuestU32(base, device + kSegmentMaxPendingSpanOffset)) {
    wrap = true;
  }
  if (wrap) {
    StoreGuestU32(base, device + kSegmentRingTailOffset, primary_base);
    start = AlignGuestU32(ring_tail, alignment);
    StoreGuestU32(base, device + kSegmentEpochOffset,
                  LoadGuestU32(base, device + kSegmentEpochOffset) + 1u);
    StoreGuestU8(base, device + kDeviceFlags3Offset,
                 LoadGuestU8(base, device + kDeviceFlags3Offset) | 4u);
    end = start + reserve_bytes;
  }

  const uint32_t deferred_fence =
      LoadGuestU32(base, device + kDeferredFenceOffset);
  if (deferred_fence != 0) {
    const uint32_t deferred_epoch =
        LoadGuestU32(base, device + kDeferredEpochOffset);
    const uint32_t current_epoch =
        LoadGuestU32(base, device + kSegmentEpochOffset);
    if (deferred_epoch - current_epoch == UINT32_MAX ||
        (deferred_epoch == current_epoch && deferred_fence < end)) {
      end = deferred_fence;
      if (current_epoch != deferred_epoch ||
          ((deferred_fence - start) >> 2u) < words) {
        return 0;
      }
    }
  }

  size_bytes = ((end - start) >> 2u) * sizeof(uint32_t);
  return start;
}

uint32_t EmulateFableAllocateCommandSegment(uint32_t device, uint8_t *base,
                                            uint32_t requested_words) {
  constexpr uint32_t kCommandPointerOffset = 0x0030;
  constexpr uint32_t kCommandEndOffset = 0x0034;
  constexpr uint32_t kCommandLimitOffset = 0x0038;
  constexpr uint32_t kDeviceFlags2Offset = 0x2ABD;
  constexpr uint32_t kSegmentReserveSizeOffset = 0x3A40;
  constexpr uint32_t kSegmentStartOffset = 0x3A50;
  constexpr uint32_t kSegmentScratchOffset = 0x3A44;
  constexpr uint32_t kSegmentUsedSizeOffset = 0x3A54;
  constexpr uint32_t kCommandReserve = 160;
  constexpr uint32_t kSegmentHeaderWords = 14;

  if ((LoadGuestU8(base, device + kDeviceFlags2Offset) & 0x20u) != 0) {
    return EmulateFableFallbackCommandBuffer(device, base);
  }

  uint32_t size_bytes =
      requested_words != 0
          ? (requested_words + kSegmentHeaderWords) * sizeof(uint32_t)
          : 0;
  const uint32_t minimum_size =
      LoadGuestU32(base, device + kSegmentReserveSizeOffset);
  if (minimum_size != 0) {
    size_bytes = std::max(size_bytes, minimum_size);
  }

  const uint32_t allocation =
      EmulateFableReserveCommandSpace(device, base, size_bytes, 32u);
  if (allocation == 0) {
    return EmulateFableFallbackCommandBuffer(device, base);
  }

  StoreGuestU32(base, device + kSegmentStartOffset, allocation);
  StoreGuestU32(base, device + kCommandPointerOffset, allocation - 4u);
  StoreGuestU32(base, device + kSegmentScratchOffset, 0);
  StoreGuestU32(base, device + kSegmentUsedSizeOffset, 0);
  const uint32_t command_end =
      allocation +
      (((size_bytes >> 2u) - kSegmentHeaderWords) * sizeof(uint32_t)) - 4u;
  StoreGuestU32(base, device + kCommandEndOffset, command_end);
  StoreGuestU32(base, device + kCommandLimitOffset,
                command_end - kCommandReserve);
  return allocation;
}

uint32_t EmulateFableWriteCommandBufferEpilogue(uint32_t device, uint8_t *base,
                                                uint32_t command_pointer) {
  constexpr uint32_t kCommandPointerOffset = 0x0030;
  constexpr uint32_t kDeferredFenceOffset = 0x33B0;
  constexpr uint32_t kDeferredEpochOffset = 0x33B4;
  constexpr uint32_t kFencePointerOffset = 0x2A90;
  constexpr uint32_t kFenceValueOffset = 0x2A9C;
  constexpr uint32_t kFenceDisableOffset = 0x541C;
  constexpr uint32_t kDeviceFlags2Offset = 0x2ABD;
  constexpr uint32_t kWaitUntilPacket = 0xC0004000;
  constexpr uint32_t kWaitUntilMask = 0x00007FFF;
  constexpr uint32_t kWriteRegisterPacket = 0xC0025C00;

  const uint32_t current_command =
      LoadGuestU32(base, device + kCommandPointerOffset);
  const uint32_t command_epoch = LoadGuestU32(base, device + 0x3A48);
  const uint32_t fence_pointer =
      LoadGuestU32(base, device + kFencePointerOffset);
  const uint32_t fence_value = LoadGuestU32(base, device + kFenceValueOffset);
  const uint32_t command_token = current_command | (command_epoch & 3u);

  StoreGuestU32(base, device + kDeferredFenceOffset, current_command);
  StoreGuestU32(base, device + kDeferredEpochOffset, command_epoch);

  uint32_t out = command_pointer + sizeof(uint32_t);
  StoreGuestU32(base, out, kWaitUntilPacket);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, kWaitUntilMask);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, kWriteRegisterPacket);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, 3);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out,
                FableGpuAddress(fence_pointer + sizeof(uint32_t)) | 2u);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, command_token);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, kWriteRegisterPacket);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, 3);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, FableGpuAddress(fence_pointer) | 2u);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, fence_value);

  if (LoadGuestU32(base, device + kFenceDisableOffset) == 0 &&
      (LoadGuestU8(base, device + kDeviceFlags2Offset) & 2u) != 0 &&
      fence_pointer != 0) {
    StoreGuestU32(base, fence_pointer, fence_value);
    StoreGuestU32(base, fence_pointer + sizeof(uint32_t), command_token);
  }
  StoreGuestU32(base, device + kFenceValueOffset, fence_value + 2u);
  return out;
}

uint32_t EmulateFableAllocateAuxiliaryCommandWords(uint32_t device,
                                                   uint8_t *base,
                                                   uint32_t word_count,
                                                   uint32_t alignment) {
  constexpr uint32_t kCommandPointerOffset = 0x0030;
  constexpr uint32_t kCommandEndOffset = 0x0034;
  constexpr uint32_t kCommandLimitOffset = 0x0038;
  constexpr uint32_t kDeferredCommandContextOffset = 0x33B0;
  constexpr uint32_t kDirectPrimaryContextOffset = 0x34CC;
  constexpr uint32_t kDeviceFlags2Offset = 0x2ABD;
  constexpr uint32_t kAuxiliaryScratchOffset = 0x3A44;
  constexpr uint32_t kSegmentRingBaseOffset = 0x3A38;
  constexpr uint32_t kSegmentRingEndOffset = 0x3A3C;
  constexpr uint32_t kSegmentRingTailOffset = 0x3A4C;
  constexpr uint32_t kAuxiliaryBytesUsedOffset = 0x3A54;
  constexpr uint32_t kAuxiliaryBytesLimitOffset = 0x3A58;
  constexpr uint32_t kCommandReserve = 160;

  if (base == nullptr || device == 0 || word_count == 0 || alignment == 0) {
    return 0;
  }
  if ((LoadGuestU8(base, device + kDeviceFlags2Offset) & 0x20u) != 0) {
    return 0;
  }

  const uint32_t byte_count = word_count * sizeof(uint32_t);
  const uint32_t direct_context =
      LoadGuestU32(base, device + kDirectPrimaryContextOffset);
  if (direct_context != 0 && LoadGuestU32(base, direct_context + 0x98u) != 0) {
    uint32_t high_water = LoadGuestU32(base, device + kAuxiliaryScratchOffset);
    if (high_water == 0) {
      high_water = (LoadGuestU32(base, direct_context + 0x9Cu) & ~3u) +
                   LoadGuestU32(base, direct_context + 0x98u);
    }
    const uint32_t allocation =
        (high_water - byte_count) & ~(alignment - 1u);
    if (allocation >= LoadGuestU32(base, device + kCommandPointerOffset) +
                          kCommandReserve + sizeof(uint32_t)) {
      StoreGuestU32(base, device + kAuxiliaryScratchOffset, allocation);
      const uint32_t consumed = high_water - allocation;
      StoreGuestU32(base, device + kCommandEndOffset,
                    LoadGuestU32(base, device + kCommandEndOffset) - consumed);
      StoreGuestU32(base, device + kCommandLimitOffset,
                    LoadGuestU32(base, device + kCommandLimitOffset) -
                        consumed);
      return allocation;
    }
    StoreGuestU8(base, device + kDeviceFlags2Offset,
                 LoadGuestU8(base, device + kDeviceFlags2Offset) | 0x20u);
    return 0;
  }

  uint32_t size_bytes = byte_count;
  const uint32_t allocation =
      EmulateFableReserveCommandSpace(device, base, size_bytes, alignment);
  if (allocation != 0 && size_bytes >= byte_count) {
    StoreGuestU32(base, device + kAuxiliaryBytesUsedOffset,
                  LoadGuestU32(base, device + kAuxiliaryBytesUsedOffset) +
                      byte_count);
    if (LoadGuestU32(base, device + kAuxiliaryBytesUsedOffset) >
            LoadGuestU32(base, device + kAuxiliaryBytesLimitOffset) &&
        LoadGuestU32(base, device + kDeferredCommandContextOffset) == 0) {
      const uint32_t command_pointer =
          LoadGuestU32(base, device + kCommandPointerOffset);
      StoreGuestU32(base, device + kCommandEndOffset,
                    command_pointer + kCommandReserve);
      StoreGuestU32(base, device + kCommandLimitOffset, command_pointer);
      if (LoadGuestU32(base, device + kSegmentRingTailOffset) !=
          LoadGuestU32(base, device + kSegmentRingBaseOffset)) {
        StoreGuestU32(base, device + kAuxiliaryScratchOffset,
                      LoadGuestU32(base, device + kSegmentRingEndOffset));
      }
    }
    return allocation;
  }

  StoreGuestU8(base, device + kDeviceFlags2Offset,
               LoadGuestU8(base, device + kDeviceFlags2Offset) | 0x20u);
  return 0;
}

uint32_t EmulateFableMakePrimaryCallListSpace(uint32_t device, uint8_t *base,
                                              uint32_t head_offset,
                                              uint32_t write_offset,
                                              uint32_t limit_offset,
                                              uint32_t context_link_offset,
                                              uint32_t word_count,
                                              uint32_t list_byte_size) {
  constexpr uint32_t kDirectPrimaryContextOffset = 0x34CC;
  constexpr uint32_t kDeviceFlags2Offset = 0x2ABD;
  constexpr uint32_t kInlineCommandBufferOffset = 0x4158;

  const uint32_t previous_head = LoadGuestU32(base, device + head_offset);
  uint32_t allocation = EmulateFableAllocateAuxiliaryCommandWords(
      device, base, word_count, 128u);
  if ((LoadGuestU8(base, device + kDeviceFlags2Offset) & 0x20u) != 0 ||
      allocation == 0) {
    allocation = device + kInlineCommandBufferOffset;
  } else {
    StoreGuestU32(base, device + head_offset, allocation);
    StoreGuestU32(base, allocation, 0);
    const uint32_t chain_pointer = FableGpuSegmentPointer(allocation);
    if (previous_head != 0) {
      StoreGuestU32(base, previous_head, chain_pointer);
      const uint32_t previous_write = LoadGuestU32(base, device + write_offset);
      StoreGuestU32(base, previous_head + sizeof(uint32_t),
                    (previous_write - previous_head - 8u) >> 3u);
    } else {
      const uint32_t direct_context =
          LoadGuestU32(base, device + kDirectPrimaryContextOffset);
      if (direct_context != 0) {
        StoreGuestU32(base, direct_context + context_link_offset,
                      chain_pointer);
      }
    }
  }

  const uint32_t write = allocation + 8u;
  StoreGuestU32(base, device + write_offset, write);
  StoreGuestU32(base, device + limit_offset, allocation + list_byte_size);
  if (!g_logged_fable_primary_list_space_emulation) {
    REXLOG_INFO("Refii plume exact Fable primary call-list allocation "
                "device=0x{:08X} list=0x{:08X}",
                device, allocation);
    g_logged_fable_primary_list_space_emulation = true;
  }
  return write;
}

uint32_t EmulateFableMakeDeferredCallListSpace(uint32_t device, uint8_t *base,
                                               uint32_t list_offset) {
  constexpr uint32_t kListHeadOffset = 0x0;
  constexpr uint32_t kListPreviousLinkOffset = 0x4;
  constexpr uint32_t kListWriteOffset = 0x8;
  constexpr uint32_t kListLimitOffset = 0xC;
  constexpr uint32_t kInlineCommandBufferOffset = 0x4158;
  constexpr uint32_t kDeferredCallListWords = 1056;
  constexpr uint32_t kDeferredCallListByteSize = 4224;

  const uint32_t list = device + list_offset;
  const uint32_t previous_link =
      LoadGuestU32(base, list + kListPreviousLinkOffset);
  if (previous_link != 0) {
    StoreGuestU32(base, previous_link,
                  FableGpuSegmentPointer(
                      LoadGuestU32(base, list + kListWriteOffset)));
  }

  uint32_t allocation = EmulateFableAllocateAuxiliaryCommandWords(
      device, base, kDeferredCallListWords, 128u);
  if (allocation != 0) {
    StoreGuestU32(base, allocation,
                  FableGpuSegmentPointer(allocation + sizeof(uint32_t)));
    const uint32_t current_write =
        LoadGuestU32(base, list + kListWriteOffset);
    if (current_write != 0) {
      StoreGuestU32(base, current_write, FableGpuSegmentPointer(allocation));
    } else {
      StoreGuestU32(base, list + kListHeadOffset, allocation);
    }
  } else {
    allocation = device + kInlineCommandBufferOffset;
    StoreGuestU32(base, list + kListHeadOffset, 0);
  }

  const uint32_t write = allocation + sizeof(uint32_t);
  StoreGuestU32(base, list + kListPreviousLinkOffset, allocation);
  StoreGuestU32(base, list + kListWriteOffset, write);
  StoreGuestU32(base, list + kListLimitOffset,
                allocation + kDeferredCallListByteSize - sizeof(uint32_t));
  if (!g_logged_fable_deferred_list_space_emulation) {
    REXLOG_INFO("Refii plume exact Fable secondary call-list allocation "
                "device=0x{:08X} list=0x{:08X}",
                device, allocation);
    g_logged_fable_deferred_list_space_emulation = true;
  }
  return write;
}

uint32_t WriteFableCacheRangeCommand(uint8_t *base, uint32_t out,
                                     uint32_t wait_mask,
                                     uint32_t range_start,
                                     uint32_t range_end) {
  constexpr uint32_t kWaitPacket = 0x00000A31;
  constexpr uint32_t kReadPacket = 0x00010A2F;
  constexpr uint32_t kWriteRegisterPacket = 0xC0043C00;
  constexpr uint32_t kEndPacket = 0x80000000;

  if (range_end <= range_start) {
    return out;
  }
  StoreGuestU32(base, out, kWaitPacket);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, wait_mask);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, kReadPacket);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, range_end - range_start);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, range_start);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, kWriteRegisterPacket);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, 3);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, kWaitPacket);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, 0);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, kEndPacket);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, 8);
  out += sizeof(uint32_t);
  return out;
}

bool EmulateFableBuildKickOffPrefixCommands(uint32_t device, uint8_t *base,
                                            uint32_t &gpu_address,
                                            uint32_t &word_count) {
  constexpr uint32_t kQueuedCacheFenceOffset = 0x2E30;
  constexpr uint32_t kDeviceFlags3Offset = 0x2ABE;
  constexpr uint32_t kSegmentRingBaseOffset = 0x3A38;
  constexpr uint32_t kSegmentRingEndOffset = 0x3A3C;
  constexpr uint32_t kFenceWaitMask = 0x03000000;
  constexpr uint32_t kRingWaitMask = 0x01000000;
  constexpr uint32_t kMaxPrefixWords = 22;

  gpu_address = 0;
  word_count = 0;
  if (base == nullptr || device == 0) {
    return false;
  }

  const bool has_queued_cache_fence =
      LoadGuestU32(base, device + kQueuedCacheFenceOffset +
                            sizeof(uint32_t)) != UINT32_MAX;
  const bool needs_ring_cache_flush =
      ((LoadGuestU8(base, device + kDeviceFlags3Offset) >> 2u) & 1u) != 0;
  word_count = 11u * (static_cast<uint32_t>(has_queued_cache_fence) +
                      static_cast<uint32_t>(needs_ring_cache_flush));
  if (word_count == 0) {
    return false;
  }

  const uint32_t command_buffer = EmulateFableAllocateAuxiliaryCommandWords(
      device, base, word_count, 32u);
  if (command_buffer == 0) {
    word_count = 0;
    return false;
  }

  uint32_t out = command_buffer;
  if (has_queued_cache_fence) {
    const uint32_t range_start =
        FableGpuAddress(command_buffer - sizeof(uint32_t)) & 0xFFFFF000u;
    const uint32_t range_end =
        (FableGpuAddress(command_buffer - sizeof(uint32_t)) + 4095u) &
        0xFFFFF000u;
    out = WriteFableCacheRangeCommand(base, out, kFenceWaitMask, range_start,
                                      range_end);
    StoreGuestU64(base, device + kQueuedCacheFenceOffset, UINT64_MAX);
  }

  if (needs_ring_cache_flush) {
    const uint32_t ring_base =
        FableGpuAddress(LoadGuestU32(base, device + kSegmentRingBaseOffset)) &
        0xFFFFF000u;
    const uint32_t ring_end =
        (FableGpuAddress(LoadGuestU32(base, device + kSegmentRingEndOffset)) +
         4095u) &
        0xFFFFF000u;
    out = WriteFableCacheRangeCommand(base, out, kRingWaitMask, ring_base,
                                      ring_end);
  }

  word_count = (out - command_buffer) / sizeof(uint32_t);
  if (word_count == 0) {
    return false;
  }
  gpu_address = FableGpuAddress(command_buffer);
  return true;
}

uint32_t AddFablePrimaryIndirectBufferCall(uint32_t device, uint8_t *base,
                                           uint32_t command_address,
                                           uint32_t word_count) {
  constexpr uint32_t kPrimaryRingBaseOffset = 0x3A30;
  constexpr uint32_t kPrimaryRingMaskOffset = 0x3A34;
  constexpr uint32_t kPrimaryRingWriteOffset = 0x2AC8;
  constexpr uint32_t kPrimaryIndirectBufferPacket = 0xC0013F00;

  const uint32_t ring_base =
      LoadGuestU32(base, device + kPrimaryRingBaseOffset);
  const uint32_t ring_mask =
      LoadGuestU32(base, device + kPrimaryRingMaskOffset);
  uint32_t write_index = LoadGuestU32(base, device + kPrimaryRingWriteOffset);
  if (ring_base == 0 || ring_mask == 0) {
    return write_index;
  }

  const uint32_t packet_index = write_index;
  const uint32_t address_index = (packet_index + 1u) & ring_mask;
  const uint32_t count_index = (address_index + 1u) & ring_mask;
  StoreGuestU32(base, ring_base + packet_index * sizeof(uint32_t),
                kPrimaryIndirectBufferPacket);
  StoreGuestU32(base, ring_base + address_index * sizeof(uint32_t),
                command_address);
  StoreGuestU32(base, ring_base + count_index * sizeof(uint32_t),
                word_count & 0x00FFFFFFu);

  write_index = (count_index + 1u) & ring_mask;
  StoreGuestU32(base, device + kPrimaryRingWriteOffset, write_index);
  return write_index;
}

FableNativeCommandSubmitEmulation
EmulateFableAddCallsToPrimaryBuffer(const RendererHookArgs &args,
                                    uint8_t *base) {
  FableNativeCommandSubmitEmulation emulation;
  if (base == nullptr || args.r3 == 0) {
    return emulation;
  }

  constexpr uint32_t kDeviceFlagsOffset = 0x2ABD;
  constexpr uint32_t kDebugCommandSinkOffset = 0x541C;
  const uint32_t device = args.r3;
  const uint32_t descriptor_base = args.r4;
  const uint32_t descriptor_count = args.r5;

  emulation.handled = true;
  emulation.result = LoadGuestU32(base, device + kDebugCommandSinkOffset);
  if ((LoadGuestU8(base, device + kDeviceFlagsOffset) & 2u) != 0 ||
      descriptor_base == 0 || descriptor_count == 0) {
    return emulation;
  }

  for (uint32_t index = 0; index < descriptor_count; ++index) {
    const uint32_t descriptor = descriptor_base + index * 8u;
    const uint32_t word_count = LoadGuestU32(base, descriptor) & 0x00FFFFFFu;
    const uint32_t command_address =
        LoadGuestU32(base, descriptor + sizeof(uint32_t));
    AddFablePrimaryIndirectBufferCall(device, base, command_address,
                                      word_count);
  }
  return emulation;
}

FableNativeCommandSubmitEmulation
EmulateFableAddCommandsToPrimaryBuffer(const RendererHookArgs &args,
                                       uint8_t *base) {
  FableNativeCommandSubmitEmulation emulation;
  if (base == nullptr || !IsLikelyFableObjectAddress(args.r3)) {
    return emulation;
  }

  constexpr uint32_t kPrimaryRingWriteOffset = 0x2AC8;
  constexpr uint32_t kPrimaryRingBaseOffset = 0x3A30;
  constexpr uint32_t kPrimaryRingMaskOffset = 0x3A34;
  constexpr uint32_t kMaxDirectPrimaryCopyDwords = 1u << 20u;

  const uint32_t device = args.r3;
  const uint32_t command_pointer = args.r4;
  const uint32_t command_word_count = args.r5;

  emulation.handled = true;
  emulation.result = 0;
  if (command_word_count == 0) {
    return emulation;
  }
  if (!IsLikelyGuestDwordAddress(command_pointer) ||
      command_word_count > kMaxDirectPrimaryCopyDwords) {
    return emulation;
  }

  const uint32_t ring_base =
      LoadGuestU32(base, device + kPrimaryRingBaseOffset);
  const uint32_t ring_mask =
      LoadGuestU32(base, device + kPrimaryRingMaskOffset);
  if (!IsLikelyGuestDwordAddress(ring_base) || ring_mask == 0) {
    return emulation;
  }

  uint32_t write_index = LoadGuestU32(base, device + kPrimaryRingWriteOffset);
  for (uint32_t index = 0; index < command_word_count; ++index) {
    StoreGuestU32(base, ring_base + write_index * sizeof(uint32_t),
                  LoadGuestU32(base, command_pointer +
                                         index * sizeof(uint32_t)));
    write_index = (write_index + 1u) & ring_mask;
  }
  StoreGuestU32(base, device + kPrimaryRingWriteOffset, write_index);
  return emulation;
}

FableNativeCommandSubmitEmulation
EmulateFableQueueIndirectBuffer(const RendererHookArgs &args, uint8_t *base) {
  FableNativeCommandSubmitEmulation emulation;
  if (base == nullptr || args.r3 == 0) {
    return emulation;
  }

  constexpr uint32_t kQueuedIndirectDwordCountOffset = 0x2B00;
  const uint32_t device = args.r3;
  const uint32_t output_buffer = args.r4;
  const uint32_t command_address = args.r5;
  const uint32_t word_count = args.r6;
  const uint32_t queued_dwords = args.r7;
  const uint32_t previous_queued_dwords =
      LoadGuestU32(base, device + kQueuedIndirectDwordCountOffset);

  emulation.handled = true;
  emulation.result = output_buffer;
  StoreGuestU32(base, device + kQueuedIndirectDwordCountOffset,
                previous_queued_dwords + queued_dwords);

  if (previous_queued_dwords != 0) {
    if (output_buffer != 0) {
      StoreGuestU32(base, output_buffer + sizeof(uint32_t),
                    word_count | 0x81000000u);
      StoreGuestU32(base, output_buffer + 2u * sizeof(uint32_t),
                    command_address);
      StoreGuestU32(base, output_buffer + 3u * sizeof(uint32_t), 0xC0000000u);
      emulation.result = output_buffer + 4u * sizeof(uint32_t);
    }
    return emulation;
  }

  AddFablePrimaryIndirectBufferCall(device, base, command_address,
                                    word_count & 0x00FFFFFFu);
  return emulation;
}

FableNativeCommandSubmitEmulation
EmulateFableKickOffSegment(const RendererHookArgs &args, uint8_t *base) {
  FableNativeCommandSubmitEmulation emulation;
  if (base == nullptr || args.r3 == 0) {
    return emulation;
  }

  constexpr uint32_t kCommandPointerOffset = 0x0030;
  constexpr uint32_t kCommandLimitOffset = 0x0038;
  constexpr uint32_t kDeviceFlagsOffset = 0x2ABC;
  constexpr uint32_t kDeviceFlags2Offset = 0x2ABD;
  constexpr uint32_t kDeferredCommandContextOffset = 0x33B0;
  constexpr uint32_t kDeferredCallListOffset = 0x3438;
  constexpr uint32_t kDeferredListWriteOffset = 0x3440;
  constexpr uint32_t kDeferredListLimitOffset = 0x3444;
  constexpr uint32_t kDirectPrimaryHeadOffset = 0x34DC;
  constexpr uint32_t kDirectPrimaryContextOffset = 0x34CC;
  constexpr uint32_t kDirectPrimaryWriteOffset = 0x34E0;
  constexpr uint32_t kDirectPrimaryLimitOffset = 0x34E4;
  constexpr uint32_t kDirectPrimaryContextLinkOffset = 0x74;
  constexpr uint32_t kSegmentStartOffset = 0x3A50;
  constexpr uint32_t kDirectPrimaryCallListWords = 34;
  constexpr uint32_t kDirectPrimaryCallListByteSize = 136;
  const uint32_t device = args.r3;
  const uint32_t segment_start =
      LoadGuestU32(base, device + kSegmentStartOffset);
  uint32_t segment_end =
      LoadGuestU32(base, device + kCommandPointerOffset) + sizeof(uint32_t);

  emulation.handled = true;
  emulation.result = args.r3;

  if ((LoadGuestU8(base, device + kDeviceFlags2Offset) & 0x20u) == 0) {
    const uint32_t word_count = (segment_end - segment_start) >> 2u;
    if ((LoadGuestU32(base, device + kDeviceFlagsOffset) & 0x80u) != 0) {
      const uint32_t direct_context =
          LoadGuestU32(base, device + kDirectPrimaryContextOffset);
      uint32_t write = LoadGuestU32(base, device + kDirectPrimaryWriteOffset);
      const uint32_t limit =
          LoadGuestU32(base, device + kDirectPrimaryLimitOffset);
      if (direct_context != 0 &&
          LoadGuestU32(base, direct_context + 152u) == 0 && word_count != 0 &&
          write + 2u * sizeof(uint32_t) > limit) {
        write = EmulateFableMakePrimaryCallListSpace(
            device, base, kDirectPrimaryHeadOffset, kDirectPrimaryWriteOffset,
            kDirectPrimaryLimitOffset, kDirectPrimaryContextLinkOffset,
            kDirectPrimaryCallListWords, kDirectPrimaryCallListByteSize);
      }
      if (direct_context != 0 &&
          LoadGuestU32(base, direct_context + 152u) == 0 && word_count != 0 &&
          write + 2u * sizeof(uint32_t) <=
              LoadGuestU32(base, device + kDirectPrimaryLimitOffset)) {
        StoreGuestU32(base, write, word_count | 0x81000000u);
        StoreGuestU32(base, write + sizeof(uint32_t),
                      FableGpuAddress(segment_start));
        StoreGuestU32(base, device + kDirectPrimaryWriteOffset,
                      write + 2u * sizeof(uint32_t));
      }
    } else if (LoadGuestU32(base, device + kDeferredCommandContextOffset) !=
               0) {
      uint32_t write = LoadGuestU32(base, device + kDeferredListWriteOffset);
      const uint32_t limit =
          LoadGuestU32(base, device + kDeferredListLimitOffset);
      if (word_count != 0 && write + 2u * sizeof(uint32_t) > limit) {
        write = EmulateFableMakeDeferredCallListSpace(
            device, base, kDeferredCallListOffset);
      }
      if (word_count != 0 && write + 2u * sizeof(uint32_t) <=
                                 LoadGuestU32(base, device +
                                                        kDeferredListLimitOffset)) {
        StoreGuestU32(base, write, word_count | 0x81000000u);
        StoreGuestU32(base, write + sizeof(uint32_t),
                      FableGpuAddress(segment_start));
        StoreGuestU32(base, device + kDeferredListWriteOffset,
                      write + 2u * sizeof(uint32_t));
      }
    } else {
      const uint32_t epilogue_end = EmulateFableWriteCommandBufferEpilogue(
          device, base, segment_end - 4u);
      RendererHookArgs queue_args = args;
      queue_args.r4 = epilogue_end + sizeof(uint32_t);
      queue_args.r5 = FableGpuAddress(segment_start);
      queue_args.r6 = (queue_args.r4 - segment_start) / sizeof(uint32_t);
      queue_args.r7 = 0;
      queue_args.r8 = device + 0x3438u;
      queue_args.r4_u64 = queue_args.r4;
      queue_args.r5_u64 = queue_args.r5;
      queue_args.r6_u64 = queue_args.r6;
      const FableNativeCommandSubmitEmulation queue_emulation =
          EmulateFableQueueIndirectBuffer(queue_args, base);
      segment_end =
          queue_emulation.handled ? queue_emulation.result : queue_args.r4;
    }
  }

  const uint32_t aligned_end = AlignGuestU32(segment_end, 32u);
  if (aligned_end <= LoadGuestU32(base, device + kCommandLimitOffset)) {
    StoreGuestU32(base, device + kSegmentStartOffset, aligned_end);
    StoreGuestU32(base, device + kCommandPointerOffset,
                  aligned_end - sizeof(uint32_t));
    return emulation;
  }

  StoreGuestU32(base, device + kCommandPointerOffset,
                segment_end - sizeof(uint32_t));
  EmulateFableAllocateCommandSegment(device, base, 0);
  return emulation;
}

FableNativeCommandSubmitEmulation EmulateFableKickOff(
    const RendererHookArgs &args, uint8_t *base) {
  FableNativeCommandSubmitEmulation emulation;
  if (base == nullptr || args.r3 == 0) {
    return emulation;
  }

  constexpr uint32_t kCommandPointerOffset = 0x0030;
  constexpr uint32_t kDeviceFlagsOffset = 0x2ABC;
  constexpr uint32_t kDeviceFlags2Offset = 0x2ABD;
  constexpr uint32_t kDeferredCommandContextOffset = 0x33B0;
  constexpr uint32_t kGpuKickModeOffset = 0x2A9C;
  constexpr uint32_t kKickOffFenceGlobal = 0x83497144;
  const uint32_t device = args.r3;

  emulation.handled = true;
  emulation.result = LoadGuestU32(base, device + kCommandPointerOffset);

  if (LoadGuestU32(base, device + kDeferredCommandContextOffset) == 0 &&
      (LoadGuestU8(base, device + kDeviceFlagsOffset) & 0x80u) == 0) {
    uint32_t prefix_gpu_address = 0;
    uint32_t prefix_word_count = 0;
    if (EmulateFableBuildKickOffPrefixCommands(
            device, base, prefix_gpu_address, prefix_word_count)) {
      const uint32_t output_buffer = EmulateFableAllocateAuxiliaryCommandWords(
          device, base, 4u, alignof(uint32_t));
      if (output_buffer != 0) {
        RendererHookArgs queue_args = args;
        queue_args.r4 = output_buffer;
        queue_args.r5 = prefix_gpu_address;
        queue_args.r6 = prefix_word_count;
        queue_args.r7 = 0;
        queue_args.r8 = device + 0x3438u;
        queue_args.r4_u64 = queue_args.r4;
        queue_args.r5_u64 = queue_args.r5;
        queue_args.r6_u64 = queue_args.r6;
        EmulateFableQueueIndirectBuffer(queue_args, base);
      }
    }
  }

  RendererHookArgs segment_args = args;
  EmulateFableKickOffSegment(segment_args, base);

  if ((LoadGuestU8(base, device + kDeviceFlagsOffset) & 0x80u) == 0 &&
      LoadGuestU32(base, kKickOffFenceGlobal) != 0 &&
      (LoadGuestU8(base, device + kDeviceFlags2Offset) & 2u) == 0) {
    StoreGuestU8(base, device + kDeviceFlags2Offset,
                 LoadGuestU8(base, device + kDeviceFlags2Offset) | 2u);
    if (LoadGuestU32(base, device + kGpuKickModeOffset) != 2u) {
      REXLOG_TRACE("Refii plume skipped native Fable KickOff fence wait "
                   "device=0x{:08X}",
                   device);
    }
  }

  emulation.result = LoadGuestU32(base, device + kCommandPointerOffset);
  return emulation;
}

FableNativeCommandSubmitEmulation EmulateFableInvalidateCaches(
    const RendererHookArgs &args, uint8_t *base) {
  FableNativeCommandSubmitEmulation emulation;
  if (base == nullptr || args.r3 == 0) {
    return emulation;
  }

  constexpr uint32_t kCommandPointerOffset = 0x0030;
  constexpr uint32_t kCommandLimitOffset = 0x0038;
  constexpr uint32_t kWaitPacket = 0x00000A31;
  constexpr uint32_t kCacheInvalidateMask = 0x03000200;
  constexpr uint32_t kReadPacket = 0x00010A2F;
  constexpr uint32_t kWriteRegisterPacket = 0xC0043C00;
  constexpr uint32_t kEndPacket = 0x80000000;
  const uint32_t device = args.r3;

  emulation.handled = true;
  uint32_t command_pointer = LoadGuestU32(base, device + kCommandPointerOffset);
  if (command_pointer > LoadGuestU32(base, device + kCommandLimitOffset)) {
    RendererHookArgs kick_args = args;
    const FableNativeCommandSubmitEmulation kick_emulation =
        EmulateFableKickOff(kick_args, base);
    command_pointer = kick_emulation.handled
                          ? kick_emulation.result
                          : LoadGuestU32(base, device + kCommandPointerOffset);
  }

  const uint32_t gpu_address = FableGpuAddress(args.r4);
  const uint32_t range_start = gpu_address & 0xFFFFF000u;
  const uint32_t range_end =
      (gpu_address + args.r5 + 4095u) & 0xFFFFF000u;
  const uint32_t range_size = range_end - range_start;

  uint32_t out = command_pointer + sizeof(uint32_t);
  StoreGuestU32(base, out, kWaitPacket);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, kCacheInvalidateMask);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, kReadPacket);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, range_size);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, range_start);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, kWriteRegisterPacket);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, 3);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, kWaitPacket);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, 0);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, kEndPacket);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, 8);

  StoreGuestU32(base, device + kCommandPointerOffset, out);
  emulation.result = out;
  return emulation;
}

uint32_t LoadFableExportBaseAddress(uint8_t *base, uint32_t export_descriptor) {
  const uint32_t export_type = LoadGuestU32(base, export_descriptor) & 0xFu;
  if (export_type == 1u) {
    return LoadGuestU32(base, export_descriptor + 0x18u) & 0xFFFFFFFCu;
  }
  if (export_type == 3u) {
    const uint32_t primary =
        LoadGuestU32(base, export_descriptor + 0x20u) & 0xFFFFF000u;
    if (primary != 0) {
      return primary;
    }
    return LoadGuestU32(base, export_descriptor + 0x30u) & 0xFFFFF000u;
  }
  return LoadGuestU32(base, export_descriptor + 0x18u);
}

void QueueSyntheticFableInvalidateCaches(uint32_t device, uint8_t *base,
                                         uint32_t gpu_address,
                                         uint32_t byte_size,
                                         uint32_t source_lr,
                                         const RendererHookArgs &source_args) {
  if (gpu_address == 0 || byte_size == 0) {
    return;
  }

  RendererHookArgs invalidate_args = source_args;
  invalidate_args.r3 = device;
  invalidate_args.r4 = gpu_address;
  invalidate_args.r5 = byte_size;
  invalidate_args.lr = source_lr;
  invalidate_args.r3_u64 = device;
  invalidate_args.r4_u64 = gpu_address;
  invalidate_args.r5_u64 = byte_size;
  ObserveRendererHookEnter("sub_822C6BA8", RendererHookKind::kDraw,
                           invalidate_args, base);
  EmulateFableInvalidateCaches(invalidate_args, base);
}

void EmulateFableEndExportStateRestore(uint8_t *base, uint32_t device,
                                       bool nested_export) {
  if (base == nullptr || device == 0) {
    return;
  }

  constexpr uint32_t kDirtyQword0Offset = 0x0010;
  constexpr uint32_t kDirtyQword1Offset = 0x0018;
  constexpr uint32_t kDirtyQword2Offset = 0x0020;
  constexpr uint32_t kPackedSurfaceClipMinOffset = 0x28C4;
  constexpr uint32_t kPackedSurfaceClipMaxOffset = 0x28C8;
  constexpr uint32_t kNestedExportDirty0 = 0x00080388u;
  constexpr uint32_t kFinalExportDirty0 = 0x00181BE8u;
  constexpr uint32_t kFinalExportDirty1 = 0x00400008u;
  constexpr uint32_t kFinalExportDirty2 = 0x00080008u;

  if (nested_export) {
    OrFableGuestQwordLow(base, device + kDirtyQword0Offset,
                         kNestedExportDirty0);
    return;
  }

  OrFableGuestQwordLow(base, device + kDirtyQword0Offset, kFinalExportDirty0);
  OrFableGuestQwordLow(base, device + kDirtyQword1Offset, kFinalExportDirty1);
  OrFableGuestQwordLow(base, device + kDirtyQword2Offset, kFinalExportDirty2);

  const uint32_t packed_min =
      LoadGuestU32(base, device + kPackedSurfaceClipMinOffset);
  const uint32_t packed_max =
      LoadGuestU32(base, device + kPackedSurfaceClipMaxOffset);
  const int32_t left = static_cast<int32_t>(packed_min << 17u) >> 17;
  const int32_t top = static_cast<int32_t>(packed_min << 1u) >> 17;
  const int32_t right = static_cast<int32_t>(packed_max << 17u) >> 17;
  const int32_t bottom = static_cast<int32_t>(packed_max << 1u) >> 17;
  ApplyFableScissorState(base, device, left, top, right, bottom,
                         false /* store_requested_rect */);
}

FableNativeCommandSubmitEmulation EmulateFableBeginExport(
    const RendererHookArgs &args, uint8_t *base) {
  FableNativeCommandSubmitEmulation emulation;
  if (base == nullptr || args.r3 == 0 || args.r5 == 0) {
    return emulation;
  }

  constexpr uint32_t kCommandPointerOffset = 0x0030;
  constexpr uint32_t kCommandLimitOffset = 0x0038;
  constexpr uint32_t kDirtyMaskLowOffset = 0x0014;
  constexpr uint32_t kExportBaseOffset = 0x2898;
  constexpr uint32_t kExportDepthOffset = 0x34C0;
  constexpr uint32_t kExportPendingOffset = 0x34C4;
  constexpr uint32_t kExportScratchOffset = 0x34C8;
  const uint32_t device = args.r3;
  const uint32_t export_descriptor = args.r5;

  emulation.handled = true;
  uint32_t command_pointer = LoadGuestU32(base, device + kCommandPointerOffset);
  if (command_pointer > LoadGuestU32(base, device + kCommandLimitOffset)) {
    RendererHookArgs kick_args = args;
    const FableNativeCommandSubmitEmulation kick_emulation =
        EmulateFableKickOff(kick_args, base);
    command_pointer = kick_emulation.handled
                          ? kick_emulation.result
                          : LoadGuestU32(base, device + kCommandPointerOffset);
  }

  const uint32_t export_base =
      LoadFableExportBaseAddress(base, export_descriptor);
  StoreGuestU32(base, device + kExportBaseOffset,
                FableGpuAddress(export_base));
  StoreGuestU32(base, device + kDirtyMaskLowOffset,
                LoadGuestU32(base, device + kDirtyMaskLowOffset) | 0x00080000u);

  uint32_t out = command_pointer + sizeof(uint32_t);
  StoreGuestU32(base, out, 0x000005C8u);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, 0x00020000u);
  StoreGuestU32(base, device + kCommandPointerOffset, out);

  const uint32_t export_depth = LoadGuestU32(base, device + kExportDepthOffset);
  StoreGuestU32(base, device + kExportScratchOffset, 0);
  StoreGuestU32(base, device + kExportPendingOffset, 0);
  StoreGuestU32(base, device + kExportDepthOffset, export_depth + 1u);

  emulation.result = LoadGuestU32(base, device + kCommandPointerOffset);
  return emulation;
}

FableNativeCommandSubmitEmulation EmulateFableEndExport(
    const RendererHookArgs &args, uint8_t *base) {
  FableNativeCommandSubmitEmulation emulation;
  if (base == nullptr || args.r3 == 0 || args.r5 == 0) {
    return emulation;
  }

  constexpr uint32_t kCommandPointerOffset = 0x0030;
  constexpr uint32_t kCommandLimitOffset = 0x0038;
  constexpr uint32_t kDirtyMaskLowOffset = 0x0014;
  constexpr uint32_t kExportBaseOffset = 0x2898;
  constexpr uint32_t kExportDepthOffset = 0x34C0;
  constexpr uint32_t kExportPendingOffset = 0x34C4;
  constexpr uint32_t kCurrentBackBufferOffset = 0x2A9C;
  constexpr uint32_t kBackBufferDirtyMaskOffset = 0x2AA0;
  constexpr uint32_t kExportListHeadOffset = 0x34D0;
  constexpr uint32_t kExportListWriteOffset = 0x34D4;
  constexpr uint32_t kExportListLimitOffset = 0x34D8;
  constexpr uint32_t kExportListContextLinkOffset = 0x70;
  constexpr uint32_t kExportCallListWords = 502;
  constexpr uint32_t kExportCallListByteSize = 2008;
  const uint32_t device = args.r3;
  const uint32_t export_descriptor = args.r5;

  emulation.handled = true;
  StoreGuestU32(base, device + kExportBaseOffset, 0);

  uint32_t command_pointer = LoadGuestU32(base, device + kCommandPointerOffset);
  if (command_pointer > LoadGuestU32(base, device + kCommandLimitOffset)) {
    RendererHookArgs kick_args = args;
    const FableNativeCommandSubmitEmulation kick_emulation =
        EmulateFableKickOff(kick_args, base);
    command_pointer = kick_emulation.handled
                          ? kick_emulation.result
                          : LoadGuestU32(base, device + kCommandPointerOffset);
  }

  uint32_t out = command_pointer + sizeof(uint32_t);
  StoreGuestU32(base, out, 0x00002006u);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, 0);
  StoreGuestU32(base, device + kCommandPointerOffset, out);

  const uint32_t current_back_buffer =
      LoadGuestU32(base, device + kCurrentBackBufferOffset);
  if (current_back_buffer != 0) {
    StoreGuestU32(base, export_descriptor + 8u, current_back_buffer);
    StoreGuestU32(base, export_descriptor + 12u, current_back_buffer);
  } else if ((LoadGuestU32(base, device + kBackBufferDirtyMaskOffset) &
              LoadGuestU32(base, export_descriptor)) != 0) {
    uint32_t write = LoadGuestU32(base, device + kExportListWriteOffset);
    if (write + 8u > LoadGuestU32(base, device + kExportListLimitOffset)) {
      write = EmulateFableMakePrimaryCallListSpace(
          device, base, kExportListHeadOffset, kExportListWriteOffset,
          kExportListLimitOffset, kExportListContextLinkOffset,
          kExportCallListWords, kExportCallListByteSize);
    }
    if (write != 0 &&
        write + 8u <= LoadGuestU32(base, device + kExportListLimitOffset)) {
      StoreGuestU32(base, write, (export_descriptor >> 2u) | 0x40000000u);
      StoreGuestU32(base, write + sizeof(uint32_t), 0xFFFFFFFFu);
      StoreGuestU32(base, device + kExportListWriteOffset,
                    write + 2u * sizeof(uint32_t));
    }
  }

  const uint32_t export_type = LoadGuestU32(base, export_descriptor) & 0xFu;
  const uint32_t export_log_index =
      g_logged_fable_end_export_emulation.fetch_add(1u);
  if (export_log_index < 24u) {
    REXLOG_INFO(
        "Refii plume EndExport emulation #{} device=0x{:08X} "
        "descriptor=0x{:08X} type={} w18=0x{:08X} w1C=0x{:08X} "
        "w20=0x{:08X} w2C=0x{:08X} w30=0x{:08X} current_bb=0x{:08X} "
        "lr=0x{:08X}",
        export_log_index + 1u, device, export_descriptor, export_type,
        LoadGuestU32(base, export_descriptor + 0x18u),
        LoadGuestU32(base, export_descriptor + 0x1Cu),
        LoadGuestU32(base, export_descriptor + 0x20u),
        LoadGuestU32(base, export_descriptor + 0x2Cu),
        LoadGuestU32(base, export_descriptor + 0x30u), current_back_buffer,
        args.lr);
  }
  if (export_type == 1u) {
    const uint32_t base_address =
        LoadGuestU32(base, export_descriptor + 0x18u) & 0xFFFFFFFCu;
    const uint32_t byte_size =
        LoadGuestU32(base, export_descriptor + 0x1Cu) & 0x03FFFFFCu;
    QueueSyntheticFableInvalidateCaches(
        device, base, FableGpuAddress(base_address), byte_size, 0x821968CCu,
        args);
  } else if (export_type == 3u) {
    const uint32_t primary =
        LoadGuestU32(base, export_descriptor + 0x20u) & 0xFFFFF000u;
    const uint32_t secondary =
        LoadGuestU32(base, export_descriptor + 0x30u) & 0xFFFFF000u;
    constexpr uint32_t kConservativeExportInvalidateSize = 0x1000u;
    if (primary != 0) {
      QueueSyntheticFableInvalidateCaches(
          device, base, FableGpuAddress(primary),
          kConservativeExportInvalidateSize, 0x821968A4u, args);
    }
    if (secondary != 0) {
      QueueSyntheticFableInvalidateCaches(
          device, base, FableGpuAddress(secondary),
          kConservativeExportInvalidateSize, 0x821968CCu, args);
    }
  }

  const uint32_t export_depth = LoadGuestU32(base, device + kExportDepthOffset);
  StoreGuestU32(base, device + kExportDepthOffset,
                export_depth != 0 ? export_depth - 1u : 0);

  command_pointer = LoadGuestU32(base, device + kCommandPointerOffset);
  if (command_pointer > LoadGuestU32(base, device + kCommandLimitOffset)) {
    RendererHookArgs kick_args = args;
    const FableNativeCommandSubmitEmulation kick_emulation =
        EmulateFableKickOff(kick_args, base);
    command_pointer = kick_emulation.handled
                          ? kick_emulation.result
                          : LoadGuestU32(base, device + kCommandPointerOffset);
  }

  out = command_pointer + sizeof(uint32_t);
  StoreGuestU32(base, out, 0xC0004600u);
  out += sizeof(uint32_t);
  StoreGuestU32(base, out, 6);
  StoreGuestU32(base, device + kCommandPointerOffset, out);

  const bool nested_export = LoadGuestU32(base, device + kExportDepthOffset) != 0;
  if (!nested_export) {
    command_pointer = LoadGuestU32(base, device + kCommandPointerOffset);
    if (command_pointer > LoadGuestU32(base, device + kCommandLimitOffset)) {
      RendererHookArgs kick_args = args;
      const FableNativeCommandSubmitEmulation kick_emulation =
          EmulateFableKickOff(kick_args, base);
      command_pointer =
          kick_emulation.handled
              ? kick_emulation.result
              : LoadGuestU32(base, device + kCommandPointerOffset);
    }
    out = command_pointer + sizeof(uint32_t);
    StoreGuestU32(base, out, 0x000005C8u);
    out += sizeof(uint32_t);
    StoreGuestU32(base, out, 0x00020000u);
    StoreGuestU32(base, device + kCommandPointerOffset, out);
  }
  EmulateFableEndExportStateRestore(base, device, nested_export);

  StoreGuestU32(base, device + kDirtyMaskLowOffset,
                LoadGuestU32(base, device + kDirtyMaskLowOffset) | 8u);
  StoreGuestU32(base, device + kExportPendingOffset, 0);

  emulation.result = LoadGuestU32(base, device + kCommandPointerOffset);
  return emulation;
}

FableNativeCommandSubmitEmulation EmulateFableSetVertexDeclaration(
    const RendererHookArgs &args, uint8_t *base) {
  FableNativeCommandSubmitEmulation emulation;
  if (base == nullptr || args.r3 == 0) {
    return emulation;
  }

  constexpr uint32_t kVertexDeclarationOffset = 0x2E2C;
  constexpr uint32_t kDirtyQwordOffset = 0x0010;
  constexpr uint32_t kVertexDeclarationDirtyBit = 0x00080000u;

  const uint32_t device = args.r3;
  emulation.handled = true;
  emulation.result = device;
  StoreGuestU32(base, device + kVertexDeclarationOffset, args.r4);
  OrFableGuestQwordLow(base, device + kDirtyQwordOffset,
                       kVertexDeclarationDirtyBit);
  return emulation;
}

FableNativeCommandSubmitEmulation EmulateFableSetPendingRenderStates(
    const RendererHookArgs &args, uint8_t *base) {
  FableNativeCommandSubmitEmulation emulation;
  if (base == nullptr || args.r3 == 0 || args.r4_u64 == 0 ||
      !IsLikelyGuestDwordAddress(args.r6)) {
    return emulation;
  }

  constexpr uint32_t kCommandPointerOffset = 0x0030;
  constexpr uint32_t kCommandLimitOffset = 0x0038;

  const uint32_t device = args.r3;
  uint64_t mask = args.r4_u64;
  uint32_t register_base = args.r5;
  uint32_t source = args.r6;

  emulation.handled = true;
  emulation.result = device;

  while (mask != 0) {
    const uint32_t leading_zeroes = CountLeadingZeroBits64(mask);
    mask <<= leading_zeroes;
    source += leading_zeroes * sizeof(uint32_t);
    const uint32_t first_register = register_base + leading_zeroes;

    const uint32_t run_length = CountLeadingZeroBits64(~mask);
    if (run_length == 0 || run_length > 64u ||
        !IsLikelyGuestDwordAddress(source + (run_length - 1u) *
                                                sizeof(uint32_t))) {
      break;
    }

    const uint32_t required_words = run_length + 1u;
    uint32_t command_pointer = LoadGuestU32(base, device + kCommandPointerOffset);
    const uint32_t command_limit = LoadGuestU32(base, device + kCommandLimitOffset);
    if (command_pointer + required_words * sizeof(uint32_t) >= command_limit) {
      EmulateFableAllocateCommandSegment(device, base, required_words);
      command_pointer = LoadGuestU32(base, device + kCommandPointerOffset);
    }

    uint32_t out = command_pointer + sizeof(uint32_t);
    StoreGuestU32(base, out, ((run_length - 1u) << 16u) | first_register);
    for (uint32_t i = 0; i < run_length; ++i) {
      out += sizeof(uint32_t);
      StoreGuestU32(base, out, LoadGuestU32(base, source + i * sizeof(uint32_t)));
    }
    StoreGuestU32(base, device + kCommandPointerOffset, out);

    if (run_length >= 64u) {
      mask = 0;
    } else {
      mask <<= run_length;
    }
    source += run_length * sizeof(uint32_t);
    register_base = first_register + run_length;
  }

  return emulation;
}

FableNativeCommandSubmitEmulation EmulateFableSetPendingFetchConstants(
    const RendererHookArgs &args, uint8_t *base) {
  FableNativeCommandSubmitEmulation emulation;
  if (base == nullptr || args.r3 == 0) {
    return emulation;
  }

  uint64_t dirty_mask = args.r4_u64;
  if ((dirty_mask >> 32u) == 0 && args.r4 != 0) {
    dirty_mask = static_cast<uint64_t>(args.r4) << 32u;
  }

  emulation.handled = true;
  emulation.result = args.r3;
  (void)dirty_mask;
  // Plume consumes fetch constants directly from the guest device mirror in
  // ObservePendingFetchConstantsLocked on the skipped-hook exit path. Native
  // D3D only used this helper to write PM4 packets, so do not perturb Fable's
  // command cursor while Plume owns submission.
  return emulation;
}

FableNativeCommandSubmitEmulation EmulateFableSetPendingShaders(
    const RendererHookArgs &args, uint8_t *base) {
  FableNativeCommandSubmitEmulation emulation;
  if (base == nullptr || args.r3 == 0) {
    return emulation;
  }

  emulation.handled = true;
  emulation.result = args.r3;
  // Plume consumes shader objects, declarations, and literal shader constants
  // from the direct-state mirror. The native helper only lowers pending shader
  // state into PM4 packets after those guest-visible values are already tracked.
  if (!g_logged_fable_set_pending_shaders_emulation) {
    REXLOG_INFO(
        "Refii renderer Plume command replacement now owns "
        "D3D::SetPending_Shaders PM4 lowering");
    g_logged_fable_set_pending_shaders_emulation = true;
  }
  return emulation;
}

FableNativeCommandSubmitEmulation EmulateFableSetPendingSplit(
    const RendererHookArgs &args, uint8_t *base) {
  FableNativeCommandSubmitEmulation emulation;
  emulation.handled = true;
  emulation.result = args.r4;

  if (base == nullptr || args.r3 == 0 || args.r4 == 0 ||
      !IsLikelyGuestDwordAddress(args.r4) || args.r7 == 0 || args.r8 == 0 ||
      !IsLikelyGuestDwordAddress(args.r6 + sizeof(uint32_t))) {
    return emulation;
  }

  constexpr uint32_t kCommandPointerOffset = 0x0030;
  constexpr uint32_t kCommandEndOffset = 0x0034;

  const uint32_t device = args.r3;
  uint32_t command_pointer = args.r4;
  uint32_t register_base = args.r5;
  uint32_t source = args.r6;
  uint32_t remaining_words = args.r7;
  const uint32_t group_words = args.r8;

  uint32_t guard = 0;
  while (remaining_words != 0 && ++guard < 256u) {
    const uint32_t command_end = LoadGuestU32(base, device + kCommandEndOffset);
    uint32_t writable_words = 0;
    if (command_end > command_pointer + sizeof(uint32_t)) {
      writable_words = ((command_end - command_pointer) >> 2u) - 1u;
    }
    uint32_t write_words = (writable_words / group_words) * group_words;
    if (write_words > remaining_words) {
      write_words = remaining_words;
    }

    if (write_words != 0) {
      uint32_t out = command_pointer + sizeof(uint32_t);
      StoreGuestU32(base, out, ((write_words - 1u) << 16u) | register_base);
      for (uint32_t i = 0; i < write_words; ++i) {
        source += sizeof(uint32_t);
        out += sizeof(uint32_t);
        StoreGuestU32(base, out, LoadGuestU32(base, source));
      }
      command_pointer = out;
      register_base += write_words;
      remaining_words -= write_words;
      StoreGuestU32(base, device + kCommandPointerOffset, command_pointer);
    }

    if (remaining_words == 0) {
      break;
    }

    RendererHookArgs kick_args = args;
    kick_args.r3 = device;
    const uint32_t before_kick = command_pointer;
    const FableNativeCommandSubmitEmulation kick_emulation =
        EmulateFableKickOff(kick_args, base);
    command_pointer = kick_emulation.handled
                          ? kick_emulation.result
                          : LoadGuestU32(base, device + kCommandPointerOffset);
    if (command_pointer == before_kick) {
      EmulateFableAllocateCommandSegment(device, base, remaining_words);
      command_pointer = LoadGuestU32(base, device + kCommandPointerOffset);
      if (command_pointer == before_kick) {
        break;
      }
    }
  }

  emulation.result = command_pointer;
  return emulation;
}

FableNativeCommandSubmitEmulation EmulateFableSetPendingPredicated(
    const RendererHookArgs &args, uint8_t *base) {
  FableNativeCommandSubmitEmulation emulation;
  emulation.handled = true;
  emulation.result = args.r4_u64;

  if (base == nullptr || args.r3 == 0) {
    return emulation;
  }

  constexpr uint32_t kCommandPointerOffset = 0x0030;
  constexpr uint32_t kCommandLimitOffset = 0x0038;
  constexpr uint32_t kDirtyMaskOffset = 0x0028;
  constexpr uint64_t kPredicationDirtyBit = 0x0000000000000100ull;
  constexpr uint64_t kDepthControlDirtyBit = 0x0000000002000000ull;
  constexpr uint64_t kClipControlDirtyBit = 0x0000000000000020ull;
  constexpr uint32_t kPredicationFlagsOffset = 0x2ABC;
  constexpr uint32_t kPredicationModeOffset = 0x2ABF;
  constexpr uint32_t kDepthControlOffset = 0x2E4C;
  constexpr uint32_t kViewportZOffset = 0x2880;
  constexpr uint32_t kViewportZRestoreOffset = 0x3378;
  constexpr uint32_t kClipControlOffset = 0x28DC;
  constexpr uint32_t kPredicationCompareBaseOffset = 0x3098;
  constexpr uint32_t kPredicationTrackedBaseOffset = 0x31B8;
  constexpr uint32_t kPredicationMaskOffset = 0x31A4;
  constexpr uint32_t kPredicationMaskRestoreOffset = 0x31A8;
  constexpr uint32_t kPredicationCountOffset = 0x31CC;
  constexpr uint32_t kPredicationValueBaseOffset = 0x3338;
  constexpr uint32_t kWriteMaskedPacket = 0xC0006000u;
  constexpr uint32_t kWriteFullPacket = 0xC0006100u;
  constexpr uint32_t kWriteRegisterPacket = 0xC0012D01u;

  const uint32_t device = args.r3;
  uint64_t dirty_mask = args.r4_u64;
  const uint32_t predication_value = args.r5;
  uint32_t command_pointer = LoadGuestU32(base, device + kCommandPointerOffset);

  auto kick_if_needed = [&]() {
    if (command_pointer > LoadGuestU32(base, device + kCommandLimitOffset)) {
      StoreGuestU32(base, device + kCommandPointerOffset, command_pointer);
      RendererHookArgs kick_args = args;
      kick_args.r3 = device;
      const FableNativeCommandSubmitEmulation kick_emulation =
          EmulateFableKickOff(kick_args, base);
      command_pointer =
          kick_emulation.handled
              ? kick_emulation.result
              : LoadGuestU32(base, device + kCommandPointerOffset);
    }
    if (!IsLikelyGuestDwordAddress(command_pointer)) {
      EmulateFableAllocateCommandSegment(device, base, 32u);
      command_pointer = LoadGuestU32(base, device + kCommandPointerOffset);
    }
  };

  auto write_word = [&](uint32_t value) {
    command_pointer += sizeof(uint32_t);
    StoreGuestU32(base, command_pointer, value);
  };

  kick_if_needed();
  if (!IsLikelyGuestDwordAddress(command_pointer)) {
    return emulation;
  }

  uint32_t restore_predication = UINT32_MAX;
  uint32_t restore_depth_control = UINT32_MAX;
  uint32_t restore_clip_control = UINT32_MAX;

  if ((dirty_mask & kPredicationDirtyBit) != 0) {
    const uint8_t predication_mode =
        LoadGuestU8(base, device + kPredicationModeOffset);
    const uint8_t predication_flags =
        LoadGuestU8(base, device + kPredicationFlagsOffset);
    bool expanded_predication = false;
    if ((predication_mode & 0x20u) != 0) {
      if ((predication_flags & 0x10u) != 0) {
        expanded_predication = true;
      } else if ((predication_flags & 0x20u) != 0) {
        expanded_predication = true;
        for (uint32_t i = 0; i < 5u; ++i) {
          const uint32_t compare_value =
              LoadGuestU32(base, device + kPredicationCompareBaseOffset +
                                     i * sizeof(uint32_t));
          const uint32_t tracked_value =
              LoadGuestU32(base, device + kPredicationTrackedBaseOffset +
                                     i * sizeof(uint32_t));
          if (tracked_value != compare_value && compare_value != 0) {
            expanded_predication = false;
            break;
          }
        }
      }
    }

    if (expanded_predication) {
      write_word(kWriteFullPacket);
      write_word(0);

      uint32_t last_written_predication = predication_value;
      uint32_t count = LoadGuestU32(base, device + kPredicationCountOffset);
      if (count > 16u) {
        count = 16u;
      }
      for (uint32_t index = count; index > 0; --index) {
        const uint32_t slot = index - 1u;
        const uint32_t slot_value =
            LoadGuestU32(base, device + kPredicationValueBaseOffset +
                                   slot * sizeof(uint32_t));
        uint32_t value = (slot_value << 17u) | (predication_value & 0x1FFFFu);
        last_written_predication = value;

        const uint32_t depth_mode =
            (LoadGuestU32(base, device + kDepthControlOffset) >> 17u) & 7u;
        if (depth_mode == 0 ||
            (depth_mode == 2u &&
             (slot != 0 || (predication_mode & 0x10u) == 0))) {
          last_written_predication = value & ~1u;
        }

        write_word(kWriteMaskedPacket);
        write_word(3u << (2u * slot));
        write_word(kWriteRegisterPacket);
        write_word(0x00040203u);
        write_word(last_written_predication);
        StoreGuestU32(base, device + kCommandPointerOffset, command_pointer);
        kick_if_needed();
      }

      if (last_written_predication != predication_value &&
          (predication_flags & 0x40u) != 0) {
        write_word(kWriteMaskedPacket);
        write_word(1);
        write_word(kWriteRegisterPacket);
        write_word(0x00040203u);
        write_word(predication_value);
      }

      write_word(kWriteMaskedPacket);
      write_word(LoadGuestU32(base, device + kPredicationMaskOffset));
      write_word(kWriteFullPacket);
      write_word(LoadGuestU32(base, device + kPredicationMaskRestoreOffset));
    } else {
      uint32_t value = predication_value;
      if ((LoadGuestU32(base, device + kDepthControlOffset) & 0x000E0000u) ==
          0) {
        value &= ~1u;
      }
      write_word(0x00002203u);
      write_word(value);
      if (value != predication_value && (predication_flags & 0x40u) != 0) {
        restore_predication = predication_value;
      }
    }

    dirty_mask &= ~kPredicationDirtyBit;
  }

  const uint64_t effective_dirty =
      LoadGuestU64(base, device + kDirtyMaskOffset) & dirty_mask;
  if ((effective_dirty & kDepthControlDirtyBit) != 0) {
    const uint32_t viewport_z = LoadGuestU32(base, device + kViewportZOffset);
    write_word(0x00002000u);
    write_word(viewport_z);
    dirty_mask &= ~kDepthControlDirtyBit;
    restore_depth_control =
        (viewport_z & 0xFFFFC000u) |
        (LoadGuestU32(base, device + kViewportZRestoreOffset) & 0x3FFFu);
  }

  if ((effective_dirty & kClipControlDirtyBit) != 0) {
    write_word(0x00002104u);
    write_word(LoadGuestU32(base, device + kClipControlOffset));
    dirty_mask &= ~kClipControlDirtyBit;
    restore_clip_control = 0;
  }

  if ((restore_predication & restore_depth_control & restore_clip_control) !=
      UINT32_MAX) {
    write_word(kWriteFullPacket);
    write_word(0);
    write_word(kWriteMaskedPacket);
    write_word(0x15555555u);

    if (restore_predication != UINT32_MAX) {
      write_word(kWriteRegisterPacket);
      write_word(0x00040203u);
      write_word(restore_predication);
    }
    if (restore_depth_control != UINT32_MAX) {
      write_word(kWriteRegisterPacket);
      write_word(0x00040000u);
      write_word(restore_depth_control);
    }
    if (restore_clip_control != UINT32_MAX) {
      write_word(kWriteRegisterPacket);
      write_word(0x00040104u);
      write_word(restore_clip_control);
    }

    write_word(kWriteMaskedPacket);
    write_word(LoadGuestU32(base, device + kPredicationMaskOffset));
    write_word(kWriteFullPacket);
    write_word(LoadGuestU32(base, device + kPredicationMaskRestoreOffset));
  }

  StoreGuestU32(base, device + kCommandPointerOffset, command_pointer);
  emulation.result = dirty_mask;
  return emulation;
}

FableNativeCommandSubmitEmulation EmulateFableSetPendingClipPlanes(
    const RendererHookArgs &args, uint8_t *base) {
  FableNativeCommandSubmitEmulation emulation;
  if (base == nullptr || args.r3 == 0) {
    return emulation;
  }

  constexpr uint32_t kCommandPointerOffset = 0x0030;
  constexpr uint32_t kCommandLimitOffset = 0x0038;
  constexpr uint32_t kClipControlOffset = 0x289C;
  constexpr uint32_t kClipPlaneConstantsOffset = 0x2820;
  constexpr uint32_t kClipPlaneRegisterBase = 0x2388;
  constexpr uint32_t kClipPlaneDwords = 4;
  constexpr uint32_t kClipPlaneCount = 32;
  constexpr uint32_t kInitialPacketWords = 13;

  const uint32_t device = args.r3;
  uint64_t dirty_mask = args.r4_u64;
  if ((dirty_mask >> 32u) == 0 && args.r4 != 0) {
    dirty_mask = static_cast<uint64_t>(args.r4) << 32u;
  }

  emulation.handled = true;
  emulation.result = device;

  uint32_t command_pointer = LoadGuestU32(base, device + kCommandPointerOffset);

  auto ensure_words = [&](uint32_t words) {
    if (!IsLikelyGuestDwordAddress(command_pointer) ||
        command_pointer + words * sizeof(uint32_t) >=
            LoadGuestU32(base, device + kCommandLimitOffset)) {
      EmulateFableAllocateCommandSegment(device, base, words);
      command_pointer = LoadGuestU32(base, device + kCommandPointerOffset);
    }
    return IsLikelyGuestDwordAddress(command_pointer);
  };

  auto write_word = [&](uint32_t value) {
    command_pointer += sizeof(uint32_t);
    StoreGuestU32(base, command_pointer, value);
  };

  if (!ensure_words(kInitialPacketWords)) {
    return emulation;
  }

  write_word(0x00002007u);
  write_word(LoadGuestU32(base, device + kClipControlOffset));
  write_word(0x00000A31u);
  write_word(0x00010000u);
  write_word(0x00010A2Fu);
  write_word(0);
  write_word(0x00001000u);
  write_word(0xC0043C00u);
  write_word(3);
  write_word(0x00000A31u);
  write_word(0);
  write_word(0x80000000u);
  write_word(8);
  StoreGuestU32(base, device + kCommandPointerOffset, command_pointer);

  for (uint32_t slot = 0; slot < kClipPlaneCount;) {
    if ((dirty_mask & (1ull << (63u - slot))) == 0) {
      ++slot;
      continue;
    }

    const uint32_t first_slot = slot;
    do {
      ++slot;
    } while (slot < kClipPlaneCount &&
             (dirty_mask & (1ull << (63u - slot))) != 0);

    const uint32_t slot_count = slot - first_slot;
    const uint32_t dword_count = slot_count * kClipPlaneDwords;
    const uint32_t source =
        device + kClipPlaneConstantsOffset +
        first_slot * kClipPlaneDwords * sizeof(uint32_t);
    if (!IsLikelyGuestDwordAddress(source + (dword_count - 1u) *
                                                sizeof(uint32_t)) ||
        !ensure_words(dword_count + 1u)) {
      break;
    }

    write_word(((dword_count - 1u) << 16u) |
               (kClipPlaneRegisterBase + first_slot * kClipPlaneDwords));
    for (uint32_t i = 0; i < dword_count; ++i) {
      write_word(LoadGuestU32(base, source + i * sizeof(uint32_t)));
    }
    StoreGuestU32(base, device + kCommandPointerOffset, command_pointer);
  }

  emulation.result = command_pointer;
  return emulation;
}

FableNativeCommandSubmitEmulation EmulateFableSetIndices(
    const RendererHookArgs &args, uint8_t *base) {
  FableNativeCommandSubmitEmulation emulation;
  if (base == nullptr || args.r3 == 0) {
    return emulation;
  }

  constexpr uint32_t kIndexBufferOffset = 0x3094;

  const uint32_t device = args.r3;
  emulation.handled = true;
  emulation.result = device;
  RetireFableBoundResourceObject(base, device,
                                 LoadGuestU32(base, device + kIndexBufferOffset));
  StoreGuestU32(base, device + kIndexBufferOffset, args.r4);
  return emulation;
}

FableNativeCommandSubmitEmulation EmulateFableSetStreamSource(
    const RendererHookArgs &args, uint8_t *base) {
  FableNativeCommandSubmitEmulation emulation;
  if (base == nullptr || args.r3 == 0) {
    return emulation;
  }

  constexpr uint32_t kStreamGpuBaseOffset = 0x18;
  constexpr uint32_t kStreamSizeOffset = 0x1C;
  constexpr uint32_t kStreamResourceBaseOffset = 0x30AC;
  constexpr uint32_t kStreamStrideBaseOffset = 0x30F0;
  constexpr uint32_t kVertexDeclarationStrideBaseOffset = 0x2E38;
  constexpr uint32_t kStreamFetchDirtyQwordOffset = 0x0018;
  constexpr uint32_t kVertexDeclarationDirtyQwordOffset = 0x0010;
  constexpr uint32_t kVertexDeclarationDirtyBit = 0x00080000u;

  const uint32_t device = args.r3;
  const uint32_t stream = args.r4;
  const uint32_t buffer = args.r5;
  const uint32_t offset = args.r6;
  const uint32_t stride_words = args.r7 >> 2u;

  emulation.handled = true;
  emulation.result = device;
  if (stream >= 18u) {
    return emulation;
  }

  if (buffer != 0) {
    const uint32_t base_address =
        LoadGuestU32(base, buffer + kStreamGpuBaseOffset) + offset;
    const uint32_t remaining_size =
        LoadGuestU32(base, buffer + kStreamSizeOffset) - offset;
    const uint32_t reversed_stream_offset = 8u * (17u - stream);
    StoreGuestU32(base, device + 0x06F4u + reversed_stream_offset,
                  FableGpuAddress(base_address));
    StoreGuestU32(base, device + 0x06F8u + reversed_stream_offset,
                  remaining_size);
    OrFableGuestQwordLow(base, device + kStreamFetchDirtyQwordOffset, args.r8);
  }

  const uint32_t slot_address =
      device + kStreamResourceBaseOffset + stream * sizeof(uint32_t);
  RetireFableBoundResourceObject(base, device, LoadGuestU32(base, slot_address));
  StoreGuestU32(base, slot_address, buffer);
  StoreGuestU8(base, device + kStreamStrideBaseOffset + stream,
               static_cast<uint8_t>(stride_words));
  if (stride_words != 0 &&
      stride_words !=
          LoadGuestU8(base, device + kVertexDeclarationStrideBaseOffset +
                                 stream)) {
    OrFableGuestQwordLow(base, device + kVertexDeclarationDirtyQwordOffset,
                         kVertexDeclarationDirtyBit);
  }
  return emulation;
}

FableNativeCommandSubmitEmulation EmulateFableSetTexture(
    const RendererHookArgs &args, uint8_t *base) {
  FableNativeCommandSubmitEmulation emulation;
  if (base == nullptr || args.r3 == 0) {
    return emulation;
  }

  constexpr uint32_t kTextureBindingBaseOffset = 0x3100;
  constexpr uint32_t kTextureRegisterBaseOffset = 0x0480;
  constexpr uint32_t kTextureFetchDirtyQwordOffset = 0x0018;
  constexpr uint32_t kTextureMaxLodBaseOffset = 0x2EAE;
  constexpr uint32_t kTextureMinLodBaseOffset = 0x2EC8;

  const uint32_t device = args.r3;
  const uint32_t slot = args.r4;
  const uint32_t texture = args.r5;

  emulation.handled = true;
  emulation.result = device;
  if (slot >= 16u) {
    return emulation;
  }

  const uint32_t binding_address =
      device + kTextureBindingBaseOffset + slot * sizeof(uint32_t);
  const uint32_t previous_texture = LoadGuestU32(base, binding_address);

  if (texture != 0) {
    const uint32_t registers = device + kTextureRegisterBaseOffset + slot * 24u;
    const uint32_t reg0 = LoadGuestU32(base, registers);
    const uint32_t reg1 = LoadGuestU32(base, registers + 4u);
    const uint32_t reg3 = LoadGuestU32(base, registers + 12u);
    const uint32_t reg4 = LoadGuestU32(base, registers + 16u);
    const uint32_t reg5 = LoadGuestU32(base, registers + 20u);
    const uint32_t base_address = LoadGuestU32(base, texture + 0x20u);
    const uint32_t texture_1c = LoadGuestU32(base, texture + 0x1Cu);
    const uint32_t texture_24 = LoadGuestU32(base, texture + 0x24u);
    const uint32_t texture_28 = LoadGuestU32(base, texture + 0x28u);
    const uint32_t texture_2c = LoadGuestU32(base, texture + 0x2Cu);
    const uint32_t texture_30 = LoadGuestU32(base, texture + 0x30u);

    StoreGuestU32(base, registers + 8u, texture_24);
    StoreGuestU32(base, registers + 16u,
                  (reg4 & 0xFFFFFC03u) | (texture_2c & 0x000003FCu));
    StoreGuestU32(base, registers,
                  (reg0 & 0x003FFC00u) | (texture_1c & 0xFFC003FFu));
    StoreGuestU32(base, registers + 4u,
                  (reg1 & 0x00000800u) | (FableGpuAddress(base_address) &
                                           0xFFFFF7FFu));
    StoreGuestU32(base, registers + 12u,
                  (reg3 & 0x7FF80000u) | (texture_28 & 0x8007FFFFu));
    StoreGuestU32(base, registers + 20u,
                  (reg5 & 0x000001FFu) |
                      (FableGpuAddress(texture_30) & 0xFFFFFE00u));

    uint32_t max_lod = LoadGuestU8(base, device + kTextureMaxLodBaseOffset + slot);
    max_lod = std::max(max_lod, (texture_2c >> 2u) & 0xFu);
    uint32_t reg4_lod =
        ((4u * max_lod) & 0x3Cu) |
        (LoadGuestU32(base, registers + 16u) & 0xFFFFFC03u) |
        (texture_2c & 0x000003C0u);
    StoreGuestU32(base, registers + 16u, reg4_lod);

    uint32_t min_lod = LoadGuestU8(base, device + kTextureMinLodBaseOffset + slot);
    min_lod = std::min(min_lod, (texture_2c >> 6u) & 0xFu);
    StoreGuestU32(base, registers + 16u,
                  ((min_lod << 6u) & 0x3C0u) |
                      (reg4_lod & 0xFFFFFC3Fu));
    OrFableGuestQwordLow(base, device + kTextureFetchDirtyQwordOffset, args.r6);
  }

  StoreGuestU32(base, binding_address, texture);
  RetireFableBoundResourceObject(base, device, previous_texture);
  return emulation;
}

FableNativeCommandSubmitEmulation EmulateFableSetVertexShader(
    const RendererHookArgs &args, uint8_t *base) {
  FableNativeCommandSubmitEmulation emulation;
  if (base == nullptr || args.r3 == 0) {
    return emulation;
  }

  constexpr uint32_t kDirtyQwordOffset = 0x0010;
  constexpr uint32_t kVertexShaderDirtyBit = 0x00080000u;
  constexpr uint32_t kVertexShaderOffset = 0x3198;
  constexpr uint32_t kDeviceFlags3Offset = 0x2ABE;
  constexpr uint32_t kShaderPatchBaseOffset = 0x0368;
  constexpr uint32_t kShaderPatchOffsetOffset = 0x037C;

  const uint32_t device = args.r3;
  const uint32_t shader = args.r4;
  emulation.handled = true;
  emulation.result = device;
  if (shader != 0) {
    OrFableGuestQwordLow(base, device + kDirtyQwordOffset,
                         kVertexShaderDirtyBit);
  }

  RetireFableBoundResourceObject(base, device,
                                 LoadGuestU32(base, device + kVertexShaderOffset));
  StoreGuestU32(base, device + kVertexShaderOffset, shader);
  StoreGuestU8(base, device + kDeviceFlags3Offset,
               LoadGuestU8(base, device + kDeviceFlags3Offset) & 0x7Fu);

  if (shader != 0) {
    const uint32_t patch_offset =
        LoadGuestU32(base, shader + kShaderPatchOffsetOffset);
    if (patch_offset != 0) {
      ApplyFableShaderPatchStream(base, device,
                                  shader + kShaderPatchBaseOffset +
                                      patch_offset,
                                  0x0000);
    }
  }
  return emulation;
}

FableNativeCommandSubmitEmulation EmulateFableSetPixelShader(
    const RendererHookArgs &args, uint8_t *base) {
  FableNativeCommandSubmitEmulation emulation;
  if (base == nullptr || args.r3 == 0) {
    return emulation;
  }

  constexpr uint32_t kDirtyQwordOffset = 0x0010;
  constexpr uint32_t kPixelShaderDirtyBits = 0x00120000u;
  constexpr uint32_t kPixelShaderOffset = 0x3194;
  constexpr uint32_t kShaderPatchBaseOffset = 0x0028;
  constexpr uint32_t kShaderPatchOffsetOffset = 0x003C;

  const uint32_t device = args.r3;
  const uint32_t shader = args.r4;
  emulation.handled = true;
  emulation.result = device;
  RetireFableBoundResourceObject(base, device,
                                 LoadGuestU32(base, device + kPixelShaderOffset));
  StoreGuestU32(base, device + kPixelShaderOffset, shader);
  OrFableGuestQwordLow(base, device + kDirtyQwordOffset,
                       kPixelShaderDirtyBits);

  if (shader != 0) {
    const uint32_t patch_offset =
        LoadGuestU32(base, shader + kShaderPatchOffsetOffset);
    if (patch_offset != 0) {
      ApplyFableShaderPatchStream(base, device,
                                  shader + kShaderPatchBaseOffset +
                                      patch_offset,
                                  0x0008);
    }
  }
  return emulation;
}

void StoreFableViewportScissor(uint8_t *base, uint32_t device, int32_t left,
                               int32_t top, int32_t right, int32_t bottom) {
  constexpr uint32_t kPackedScissorMinOffset = 0x28C4;
  constexpr uint32_t kPackedScissorMaxOffset = 0x28C8;

  const uint32_t packed_min_old =
      LoadGuestU32(base, device + kPackedScissorMinOffset);
  const uint32_t packed_max_old =
      LoadGuestU32(base, device + kPackedScissorMaxOffset);
  StoreGuestU32(base, device + kPackedScissorMinOffset,
                (packed_min_old & 0x80008000u) |
                    (static_cast<uint32_t>(left) & 0x7FFFu) |
                    ((static_cast<uint32_t>(top) << 16u) & 0x7FFF0000u));
  StoreGuestU32(base, device + kPackedScissorMaxOffset,
                (packed_max_old & 0x80008000u) |
                    (static_cast<uint32_t>(right) & 0x7FFFu) |
                    ((static_cast<uint32_t>(bottom) << 16u) & 0x7FFF0000u));
}

FableNativeCommandSubmitEmulation ApplyFableViewportState(
    uint8_t *base, uint32_t device, float x, float y, float width,
    float height, float min_depth, float max_depth) {
  FableNativeCommandSubmitEmulation emulation;
  if (base == nullptr || device == 0 || !std::isfinite(x) ||
      !std::isfinite(y) || !std::isfinite(width) || !std::isfinite(height) ||
      !std::isfinite(min_depth) || !std::isfinite(max_depth) || width < 0.0f ||
      height < 0.0f) {
    return emulation;
  }

  constexpr uint32_t kViewportTransformHalfWidthOffset = 0x2908;
  constexpr uint32_t kViewportTransformCenterXOffset = 0x290C;
  constexpr uint32_t kViewportTransformNegHalfHeightOffset = 0x2910;
  constexpr uint32_t kViewportTransformCenterYOffset = 0x2914;
  constexpr uint32_t kViewportTransformDepthScaleOffset = 0x2918;
  constexpr uint32_t kViewportTransformDepthBiasOffset = 0x291C;
  constexpr uint32_t kDirtyQwordOffset = 0x0010;
  constexpr uint32_t kViewportDirtyBits = 0x07E00000u;
  constexpr uint32_t kViewportXOffset = 0x3168;
  constexpr uint32_t kViewportYOffset = 0x316C;
  constexpr uint32_t kViewportWidthOffset = 0x3170;
  constexpr uint32_t kViewportHeightOffset = 0x3174;
  constexpr uint32_t kViewportMinZOffset = 0x3178;
  constexpr uint32_t kViewportMaxZOffset = 0x317C;
  constexpr uint32_t kViewportUnknownOffset = 0x3180;

  const FableSurfaceExtent extent =
      CurrentFableViewportClampExtent(base, device);
  if (extent.width != 0 && extent.height != 0 && x >= 0.0f && y >= 0.0f) {
    width =
        std::min(width, std::max(0.0f, static_cast<float>(extent.width) - x));
    height =
        std::min(height, std::max(0.0f, static_cast<float>(extent.height) - y));
  }

  emulation.handled = true;
  emulation.result = device;
  StoreGuestF32(base, device + kViewportXOffset, x);
  StoreGuestF32(base, device + kViewportYOffset, y);
  StoreGuestF32(base, device + kViewportWidthOffset, width);
  StoreGuestF32(base, device + kViewportHeightOffset, height);
  StoreGuestF32(base, device + kViewportMinZOffset, min_depth);
  StoreGuestF32(base, device + kViewportMaxZOffset, max_depth);
  StoreGuestU32(base, device + kViewportUnknownOffset, 0);

  const float half_width = width * 0.5f;
  const float half_height = height * 0.5f;
  StoreGuestF32(base, device + kViewportTransformHalfWidthOffset, half_width);
  StoreGuestF32(base, device + kViewportTransformCenterXOffset,
                half_width + x);
  StoreGuestF32(base, device + kViewportTransformNegHalfHeightOffset,
                -half_height);
  StoreGuestF32(base, device + kViewportTransformCenterYOffset,
                half_height + y);
  StoreGuestF32(base, device + kViewportTransformDepthScaleOffset,
                max_depth - min_depth);
  StoreGuestF32(base, device + kViewportTransformDepthBiasOffset, min_depth);

  const int32_t left = static_cast<int32_t>(std::floor(x));
  const int32_t top = static_cast<int32_t>(std::floor(y));
  const int32_t right = static_cast<int32_t>(std::ceil(x + width));
  const int32_t bottom = static_cast<int32_t>(std::ceil(y + height));
  StoreFableViewportScissor(base, device, left, top, right, bottom);
  FableDirectMirrorViewportState(
      device, MakeFableViewport(x, y, width, height, min_depth, max_depth));
  FableDirectMirrorScissorState(device,
                                MakeFableScissor(left, top, right, bottom));
  OrFableGuestQwordLow(base, device + kDirtyQwordOffset, kViewportDirtyBits);
  return emulation;
}

FableNativeCommandSubmitEmulation EmulateFableSetViewportPointer(
    const RendererHookArgs &args, uint8_t *base) {
  if (base == nullptr || args.r3 == 0 || args.r4 == 0) {
    return {};
  }

  const uint32_t device = args.r3;
  const uint32_t viewport = args.r4;
  const float x = static_cast<float>(LoadGuestU32(base, viewport));
  const float y = static_cast<float>(LoadGuestU32(base, viewport + 4u));
  const float width = static_cast<float>(LoadGuestU32(base, viewport + 8u));
  const float height = static_cast<float>(LoadGuestU32(base, viewport + 12u));
  const float min_depth = LoadGuestF32(base, viewport + 16u);
  const float max_depth = LoadGuestF32(base, viewport + 20u);
  return ApplyFableViewportState(base, device, x, y, width, height, min_depth,
                                 max_depth);
}

FableNativeCommandSubmitEmulation EmulateFableSetViewportFloats(
    const RendererHookArgs &args, uint8_t *base) {
  return ApplyFableViewportState(base, args.r3, static_cast<float>(args.f1),
                                 static_cast<float>(args.f2),
                                 static_cast<float>(args.f3),
                                 static_cast<float>(args.f4),
                                 static_cast<float>(args.f5),
                                 static_cast<float>(args.f6));
}

FableNativeCommandSubmitEmulation ApplyFableScissorState(
    uint8_t *base, uint32_t device, int32_t left, int32_t top, int32_t right,
    int32_t bottom, bool store_requested_rect) {
  FableNativeCommandSubmitEmulation emulation;
  if (base == nullptr || device == 0 || right <= left || bottom <= top) {
    return emulation;
  }

  constexpr uint32_t kRequestedScissorLeftOffset = 0x3184;
  constexpr uint32_t kRequestedScissorTopOffset = 0x3188;
  constexpr uint32_t kRequestedScissorRightOffset = 0x318C;
  constexpr uint32_t kRequestedScissorBottomOffset = 0x3190;

  emulation.handled = true;
  emulation.result = device;
  if (store_requested_rect) {
    StoreGuestU32(base, device + kRequestedScissorLeftOffset,
                  static_cast<uint32_t>(left));
    StoreGuestU32(base, device + kRequestedScissorTopOffset,
                  static_cast<uint32_t>(top));
    StoreGuestU32(base, device + kRequestedScissorRightOffset,
                  static_cast<uint32_t>(right));
    StoreGuestU32(base, device + kRequestedScissorBottomOffset,
                  static_cast<uint32_t>(bottom));
  }
  StoreFableViewportScissor(base, device, left, top, right, bottom);
  FableDirectMirrorScissorState(device,
                                MakeFableScissor(left, top, right, bottom));
  return emulation;
}

FableNativeCommandSubmitEmulation EmulateFableSetScissorPointer(
    const RendererHookArgs &args, uint8_t *base) {
  if (base == nullptr || args.r3 == 0 || args.r4 == 0) {
    return {};
  }

  const uint32_t device = args.r3;
  const uint32_t scissor = args.r4;
  const int32_t left = static_cast<int32_t>(LoadGuestU32(base, scissor));
  const int32_t top = static_cast<int32_t>(LoadGuestU32(base, scissor + 4u));
  const int32_t right = static_cast<int32_t>(LoadGuestU32(base, scissor + 8u));
  const int32_t bottom =
      static_cast<int32_t>(LoadGuestU32(base, scissor + 12u));
  return ApplyFableScissorState(base, device, left, top, right, bottom,
                                true /* store_requested_rect */);
}

FableNativeCommandSubmitEmulation EmulateFableSetSurfaceClip(
    const RendererHookArgs &args, uint8_t *base) {
  return ApplyFableScissorState(base, args.r3, static_cast<int32_t>(args.r4),
                                static_cast<int32_t>(args.r5),
                                static_cast<int32_t>(args.r6),
                                static_cast<int32_t>(args.r7),
                                false /* store_requested_rect */);
}

FableNativeCommandSubmitEmulation EmulateFableSetShaderGPRAllocation(
    const RendererHookArgs &args, uint8_t *base) {
  FableNativeCommandSubmitEmulation emulation;
  if (!base || !IsLikelyFableObjectAddress(args.r3)) {
    return emulation;
  }

  constexpr uint32_t kShaderGprAllocationOffset = 2730u * sizeof(uint32_t);
  constexpr uint32_t kPackedSurfaceClipMinOffset = 0x28C4;
  constexpr uint32_t kPackedSurfaceClipMaxOffset = 0x28C8;

  const uint32_t device = args.r3;
  uint32_t vertex_gprs = args.r5;
  uint32_t pixel_gprs = args.r6;
  if (vertex_gprs == 0 && pixel_gprs == 0) {
    vertex_gprs = 64;
    pixel_gprs = 64;
  }

  uint32_t allocation = 0;
  if ((args.r4 & 1u) == 0) {
    const uint32_t packed =
        ((vertex_gprs << 8u) & 0x00007F00u) | (pixel_gprs & 0xFFFF80FFu);
    const uint32_t shifted = packed << 4u;
    allocation = (shifted & 0x0007F000u) | (shifted & 0x000007F0u) |
                 (LoadGuestU32(base, device + kShaderGprAllocationOffset) &
                  0xFFF8080Fu);
  }
  StoreGuestU32(base, device + kShaderGprAllocationOffset, allocation);

  const uint32_t packed_min =
      LoadGuestU32(base, device + kPackedSurfaceClipMinOffset);
  const uint32_t packed_max =
      LoadGuestU32(base, device + kPackedSurfaceClipMaxOffset);
  const int32_t left = static_cast<int32_t>(packed_min << 17u) >> 17;
  const int32_t top = static_cast<int32_t>(packed_min << 1u) >> 17;
  const int32_t right = static_cast<int32_t>(packed_max << 17u) >> 17;
  const int32_t bottom = static_cast<int32_t>(packed_max << 1u) >> 17;
  ApplyFableScissorState(base, device, left, top, right, bottom,
                         false /* store_requested_rect */);

  emulation.handled = true;
  emulation.result = device;
  return emulation;
}

FableNativeCommandSubmitEmulation EmulateFableSetScreenExtentQueryMode(
    const RendererHookArgs &args, uint8_t *base) {
  if (!base || !IsLikelyFableObjectAddress(args.r3)) {
    return {};
  }

  constexpr uint32_t kScreenExtentQueryModeOffset = 0x2AA4;
  const uint32_t device = args.r3;
  const uint32_t current =
      LoadGuestU32(base, device + kScreenExtentQueryModeOffset);
  StoreGuestU32(base, device + kScreenExtentQueryModeOffset,
                ((args.r4 << 12u) & 0x00003000u) |
                    (current & 0xFFFFCFFFu));
  return {.handled = true, .result = device};
}

FableNativeCommandSubmitEmulation EmulateFableSetScalerParameters(
    const RendererHookArgs &args, uint8_t *base) {
  if (!base || !IsLikelyFableObjectAddress(args.r3) ||
      !IsLikelyGuestDwordAddress(args.r4)) {
    return {};
  }

  constexpr uint32_t kScalerParametersOffset = 0x359C;
  constexpr uint32_t kScalerDirtyFlagsOffset = 0x2ABE;
  constexpr uint32_t kScalerParameterDwords = 14;
  const uint32_t device = args.r3;
  const uint32_t parameter_pointer = args.r4;

  for (uint32_t index = 0; index < kScalerParameterDwords; ++index) {
    StoreGuestU32(base, device + kScalerParametersOffset +
                            index * sizeof(uint32_t),
                  0);
  }
  for (uint32_t index = 0; index < 6u; ++index) {
    StoreGuestU32(base, device + kScalerParametersOffset +
                            index * sizeof(uint32_t),
                  LoadGuestU32(base, parameter_pointer +
                                      index * sizeof(uint32_t)));
  }
  const uint32_t filter_enabled =
      LoadGuestU32(base, parameter_pointer + 6u * sizeof(uint32_t)) != 0 ? 3u
                                                                        : 0u;
  StoreGuestU32(base, device + kScalerParametersOffset +
                          6u * sizeof(uint32_t),
                filter_enabled);
  StoreGuestU32(base, device + kScalerParametersOffset +
                          10u * sizeof(uint32_t),
                filter_enabled);
  StoreGuestU8(base, device + kScalerDirtyFlagsOffset,
               static_cast<uint8_t>(
                   LoadGuestU8(base, device + kScalerDirtyFlagsOffset) |
                   0x10u));
  return {.handled = true, .result = device};
}

FableNativeCommandSubmitEmulation EmulateFableSetupScaler(
    const RendererHookArgs &args, uint8_t *base) {
  if (!base || !IsLikelyFableObjectAddress(args.r3)) {
    return {};
  }

  return {.handled = true, .result = args.r3};
}

bool FableColorSurfaceUsesFloatDepthCompare(uint8_t *base, uint32_t surface) {
  if (!base || !IsLikelyFableObjectAddress(surface)) {
    return false;
  }

  const uint32_t format = LoadGuestU32(base, surface + 0x1Cu) & 0xFu;
  return format == 5u || format == 7u || format == 15u;
}

bool FableBoundTargetsMatchTrackedTargets(uint8_t *base, uint32_t device) {
  if (!base || !IsLikelyFableObjectAddress(device)) {
    return false;
  }

  constexpr uint32_t kDisplayFlagsOffset = 0x2ABC;
  constexpr uint32_t kRenderTargetBaseOffset = 0x3098;
  constexpr uint32_t kDepthStencilOffset = 0x30A8;
  constexpr uint32_t kTrackedTargetBaseOffset = 0x31B8;
  constexpr uint32_t kTrackedDepthOffset = 0x31C8;
  constexpr uint32_t kPredicateCountOffset = 0x31CC;
  if (LoadGuestU32(base, device + kPredicateCountOffset) != 0 ||
      (LoadGuestU8(base, device + kDisplayFlagsOffset) & 0x0Cu) != 0) {
    return false;
  }
  if ((LoadGuestU8(base, device + kDisplayFlagsOffset) & 0x10u) != 0) {
    return true;
  }
  if ((LoadGuestU8(base, device + kDisplayFlagsOffset) & 0x20u) == 0) {
    return false;
  }

  for (uint32_t i = 0; i < 4u; ++i) {
    const uint32_t target =
        LoadGuestU32(base, device + kRenderTargetBaseOffset +
                               i * sizeof(uint32_t));
    const uint32_t tracked =
        LoadGuestU32(base, device + kTrackedTargetBaseOffset +
                               i * sizeof(uint32_t));
    if (target != 0 && tracked != target) {
      return false;
    }
  }

  const uint32_t depth = LoadGuestU32(base, device + kDepthStencilOffset);
  return depth == 0 || LoadGuestU32(base, device + kTrackedDepthOffset) == depth;
}

void UpdateFableTargetPredicateFlag(uint8_t *base, uint32_t device) {
  constexpr uint32_t kDisplayFlagsOffset = 0x2ABC;
  const uint8_t flags = LoadGuestU8(base, device + kDisplayFlagsOffset);
  const uint8_t predicate_ready =
      FableBoundTargetsMatchTrackedTargets(base, device) ? 1u : 0u;
  StoreGuestU8(base, device + kDisplayFlagsOffset,
               static_cast<uint8_t>((flags & 0xFEu) | predicate_ready));
}

void ApplyFableSurfaceExtentState(uint8_t *base, uint32_t device,
                                  uint32_t surface) {
  if (!base || !IsLikelyFableObjectAddress(device) ||
      !IsLikelyFableObjectAddress(surface)) {
    return;
  }

  constexpr uint32_t kSurfaceExtentOffset = 0x18;
  constexpr uint32_t kSurfaceExtentRegisterOffset = 0x2880;
  constexpr uint32_t kSurfaceExtentAuxOffset = 0x29BC;
  constexpr uint32_t kPrimaryDirtyQwordOffset = 0x0010;
  constexpr uint32_t kWideDirtyQwordOffset = 0x0020;
  constexpr uint32_t kDefaultViewportPointer = 0x82001AA8;

  const uint32_t extent_register = LoadGuestU32(base, surface + kSurfaceExtentOffset);
  uint32_t aux = 0;
  if ((extent_register & 0x00030000u) != 0) {
    aux = (((extent_register >> 16u) & 3u) == 1u) ? 0x8001u : 0xC003u;
  }

  StoreGuestU32(base, device + kSurfaceExtentAuxOffset, aux);
  StoreGuestU32(base, device + kSurfaceExtentRegisterOffset, extent_register);
  OrFableGuestQwordLow(base, device + kPrimaryDirtyQwordOffset, 0x02000000u);
  OrFableGuestQwordLow(base, device + kWideDirtyQwordOffset, 0x00000010u);
  StoreGuestU32(base, device + 0x3184u, 0);
  StoreGuestU32(base, device + 0x3188u, 0);
  StoreGuestU32(base, device + 0x318Cu, 0xFFFFu);
  StoreGuestU32(base, device + 0x3190u, 0xFFFFu);

  RendererHookArgs viewport_args = {};
  viewport_args.r3 = device;
  viewport_args.r4 = kDefaultViewportPointer;
  viewport_args.r3_u64 = device;
  viewport_args.r4_u64 = kDefaultViewportPointer;
  EmulateFableSetViewportPointer(viewport_args, base);
}

void UpdateFableDepthControlBits(uint8_t *base, uint32_t device,
                                 uint32_t surface) {
  if (!base || !IsLikelyFableObjectAddress(device)) {
    return;
  }

  constexpr uint32_t kDepthControlOffset = 0x2934;
  constexpr uint32_t kDepthEnableStateOffset = 0x2E64;
  constexpr uint32_t kDepthWriteStateOffset = 0x2E68;
  constexpr uint32_t kPrimaryDirtyQwordOffset = 0x0010;

  uint32_t depth_control = LoadGuestU32(base, device + kDepthControlOffset);
  uint32_t depth_enable = LoadGuestU32(base, device + kDepthEnableStateOffset);
  uint32_t depth_write = LoadGuestU32(base, device + kDepthWriteStateOffset);
  if (surface == 0) {
    depth_enable = 0;
    depth_write = 0;
  }

  depth_control = (depth_control & ~0x3u) | (depth_write & 1u) |
                  ((depth_enable << 1u) & 2u);
  StoreGuestU32(base, device + kDepthControlOffset, depth_control);
  OrFableGuestQwordLow(base, device + kPrimaryDirtyQwordOffset, 0x00020800u);
}

FableNativeCommandSubmitEmulation EmulateFableSetRenderTarget(
    const RendererHookArgs &args, uint8_t *base) {
  if (!base || !IsLikelyFableObjectAddress(args.r3) || args.r4 >= 4u) {
    return {};
  }

  constexpr uint32_t kPrimaryDirtyQwordOffset = 0x0010;
  constexpr uint32_t kRenderTargetBaseOffset = 0x3098;
  constexpr uint32_t kRenderTargetRegisterBaseOffset = 0x2884;
  constexpr uint32_t kMrtEnableRegisterOffset = 0x28DC;
  constexpr uint32_t kDisplayFlagsOffset = 0x2ABC;
  constexpr uint32_t kDisplayFlags3Offset = 0x2ABF;
  constexpr uint64_t kRenderTargetDirtyMaskBase =
      0x0002010700000000ull | 0x80000000ull;

  const uint32_t device = args.r3;
  const uint32_t slot = args.r4;
  const uint32_t surface = args.r5;
  StoreGuestU32(base,
                device + kRenderTargetBaseOffset + slot * sizeof(uint32_t),
                surface);
  if (surface != 0) {
    const uint32_t register_index = slot == 0 ? 0u : slot + 1u;
    StoreGuestU32(base,
                  device + kRenderTargetRegisterBaseOffset +
                      register_index * sizeof(uint32_t),
                  LoadGuestU32(base, surface + 0x1Cu));
    OrFableGuestQword(base, device + kPrimaryDirtyQwordOffset,
                      kRenderTargetDirtyMaskBase >> (register_index + 7u));
  }

  if (slot == 0) {
    const uint8_t flags3 =
        static_cast<uint8_t>(LoadGuestU8(base, device + kDisplayFlags3Offset) &
                             ~0x08u);
    StoreGuestU8(base, device + kDisplayFlags3Offset, flags3);
    if (FableColorSurfaceUsesFloatDepthCompare(base, surface)) {
      StoreGuestU8(base, device + kDisplayFlags3Offset,
                   static_cast<uint8_t>(flags3 | 0x08u));
      OrFableGuestQwordLow(base, device + kPrimaryDirtyQwordOffset,
                           0x00100000u);
    }
    if (surface != 0) {
      ApplyFableSurfaceExtentState(base, device, surface);
    } else {
      const uint32_t depth = LoadGuestU32(base, device + 0x30A8u);
      if (depth != 0) {
        ApplyFableSurfaceExtentState(base, device, depth);
      }
    }
  }

  uint32_t mrt_mask = LoadGuestU32(base, device + kMrtEnableRegisterOffset);
  const uint32_t render_target_state =
      LoadGuestU32(base, device + 0x2E54u + slot * sizeof(uint32_t));
  if (surface == 0) {
    mrt_mask &= ~(0xFu << (slot * 4u));
  } else {
    mrt_mask = (mrt_mask & ~(0xFu << (slot * 4u))) |
               ((render_target_state & 0xFu) << (slot * 4u));
  }
  StoreGuestU32(base, device + kMrtEnableRegisterOffset, mrt_mask);
  OrFableGuestQwordLow(base, device + kPrimaryDirtyQwordOffset, 0x00000010u);
  UpdateFableTargetPredicateFlag(base, device);
  return {.handled = true, .result = args.r3};
}

FableNativeCommandSubmitEmulation EmulateFableSetDepthStencilSurface(
    const RendererHookArgs &args, uint8_t *base) {
  if (!base || !IsLikelyFableObjectAddress(args.r3)) {
    return {};
  }

  constexpr uint32_t kPrimaryDirtyQwordOffset = 0x0010;
  constexpr uint32_t kDepthStencilOffset = 0x30A8;
  constexpr uint32_t kDepthSurfaceRegisterOffset = 0x2888;
  constexpr uint32_t kDepthControlOffset = 0x2940;
  constexpr uint32_t kDisplayFlagsOffset = 0x2ABC;
  constexpr uint32_t kDisplayFlags2Offset = 0x2ABE;
  const uint32_t device = args.r3;
  const uint32_t surface = args.r4;

  StoreGuestU32(base, device + kDepthStencilOffset, surface);
  if (surface != 0) {
    if (LoadGuestU32(base, device + 0x3098u) == 0) {
      ApplyFableSurfaceExtentState(base, device, surface);
    }
    StoreGuestU32(base, device + kDepthSurfaceRegisterOffset,
                  LoadGuestU32(base, surface + 0x1Cu));
    const uint32_t depth_control = LoadGuestU32(base, surface + 0x20u);
    StoreGuestU32(base, device + kDepthControlOffset, depth_control);
    StoreGuestU8(base, device + kDisplayFlags2Offset,
                 static_cast<uint8_t>(
                     (LoadGuestU8(base, device + kDisplayFlags2Offset) &
                      ~0x20u) |
                     ((depth_control & 1u) != 0 ? 0x20u : 0u)));
    OrFableGuestQwordLow(base, device + kPrimaryDirtyQwordOffset, 0x00820100u);
  } else {
    StoreGuestU32(base, device + kDepthControlOffset,
                  LoadGuestU32(base, device + kDepthControlOffset) &
                      0xFFFFFFF0u);
    StoreGuestU8(base, device + kDisplayFlags2Offset,
                 static_cast<uint8_t>(
                     LoadGuestU8(base, device + kDisplayFlags2Offset) &
                     ~0x20u));
    OrFableGuestQwordLow(base, device + kPrimaryDirtyQwordOffset, 0x00000100u);
  }

  UpdateFableTargetPredicateFlag(base, device);
  UpdateFableDepthControlBits(base, device, surface);
  OrFableGuestQwordLow(base, device + kPrimaryDirtyQwordOffset, 0x00020000u);
  (void)kDisplayFlagsOffset;
  return {.handled = true, .result = args.r3};
}

FableNativeCommandSubmitEmulation EmulateFableGetRenderTarget(
    const RendererHookArgs &args, uint8_t *base) {
  if (base == nullptr || !IsLikelyFableObjectAddress(args.r3) ||
      args.r4 >= 4u) {
    return {};
  }

  constexpr uint32_t kRenderTargetBaseOffset = 0x3098;
  const FableDirectStateSnapshot direct_state =
      GetFableDirectStateSnapshot();
  uint32_t surface = direct_state.targets.color[args.r4];
  if (surface == 0) {
    surface = LoadGuestU32(
        base, args.r3 + kRenderTargetBaseOffset + args.r4 * sizeof(uint32_t));
  } else {
    StoreGuestU32(base,
                  args.r3 + kRenderTargetBaseOffset +
                      args.r4 * sizeof(uint32_t),
                  surface);
  }

  if (surface != 0) {
    RendererHookArgs add_ref_args = {};
    add_ref_args.r3 = surface;
    EmulateFableResourceLifetime("sub_821FC178", add_ref_args, base);
  }

  if (!g_logged_fable_get_render_target_emulation) {
    REXLOG_INFO(
        "Refii renderer Plume resource replacement now owns "
        "D3DDevice_GetRenderTarget/GetBackBuffer compatibility reads");
    g_logged_fable_get_render_target_emulation = true;
  }

  return {.handled = true, .result = surface};
}

FableNativeCommandSubmitEmulation EmulateFableSurfaceSize(
    std::string_view symbol, const RendererHookArgs &args, uint8_t *base) {
  if (!RendererReplaceResourcesEnabled() || !IsFableSurfaceSizeHook(symbol)) {
    return {};
  }

  if (!g_logged_fable_surface_size_emulation) {
    REXLOG_INFO(
        "Refii renderer Plume resource replacement now owns XGSurfaceSize "
        "surface byte-size queries; reporting zero to keep Fable off the "
        "native EDRAM tiling path");
    g_logged_fable_surface_size_emulation = true;
  }

  return {.handled = true, .result = 0};
}

FableNativeCommandSubmitEmulation EmulateFableSetRenderState(
    std::string_view symbol, const RendererHookArgs &args, uint8_t *base) {
  if (!base || !IsLikelyFableObjectAddress(args.r3) ||
      FableRenderStateSlotForSymbol(symbol) == UINT32_MAX) {
    return {};
  }

  return {.handled = true, .result = args.r3};
}

FableNativeCommandSubmitEmulation EmulateFableSetSamplerState(
    std::string_view symbol, const RendererHookArgs &args, uint8_t *base) {
  if (!base || !IsLikelyFableObjectAddress(args.r3) ||
      FableSamplerStateSlotForSymbol(symbol) == UINT32_MAX ||
      args.r4 >= kRendererD3DSamplerStateSlots) {
    return {};
  }

  return {.handled = true, .result = args.r3};
}

void MarkFableClearCommandDirty(uint8_t *base, uint32_t device,
                                bool full_device_clear) {
  if (!base || !IsLikelyFableObjectAddress(device)) {
    return;
  }

  constexpr uint32_t kPrimaryDirtyQwordOffset = 0x0010;
  constexpr uint32_t kSecondaryDirtyQwordOffset = 0x0018;
  constexpr uint32_t kWideDirtyQwordOffset = 0x0020;
  constexpr uint32_t kDisplayFlagsOffset = 0x2ABD;

  // D3DDevice_ClearF eventually returns through the device path and sets the
  // same dirty families as the native command builder. Keep those cheap
  // guest-visible bits moving while Plume owns the actual clear.
  if (full_device_clear) {
    OrFableGuestQword(base, device + kPrimaryDirtyQwordOffset, 1ull << 37u);
    OrFableGuestQwordLow(base, device + kPrimaryDirtyQwordOffset, 0x00000600u);
    StoreGuestU8(base, device + kDisplayFlagsOffset,
                 LoadGuestU8(base, device + kDisplayFlagsOffset) | 4u);
  }

  // D3D::ClearRect marks the target/clear/scissor related dirty groups after
  // writing the clear packet.
  OrFableGuestQwordLow(base, device + kSecondaryDirtyQwordOffset, 0x80000000u);
  OrFableGuestQword(base, device + kPrimaryDirtyQwordOffset,
                    (1ull << 57u) | (1ull << 56u));
  OrFableGuestQword(base, device + kWideDirtyQwordOffset, 1ull << 36u);
  OrFableGuestQwordLow(base, device + kPrimaryDirtyQwordOffset, 0x00000808u);
}

FableNativeCommandSubmitEmulation EmulateFableClearCommand(
    std::string_view symbol, const RendererHookArgs &args, uint8_t *base) {
  if (!base || !IsLikelyFableObjectAddress(args.r3)) {
    return {};
  }

  MarkFableClearCommandDirty(base, args.r3, symbol == "sub_821E2FE0");
  return {.handled = true, .result = args.r3};
}

FableNativeCommandSubmitEmulation EmulateFableSetColorClear(
    const RendererHookArgs &args, uint8_t *base) {
  if (!base || !IsLikelyFableObjectAddress(args.r3)) {
    return {};
  }

  constexpr uint32_t kWideDirtyQwordOffset = 0x0020;
  constexpr uint32_t kColorClearDirtyBits = 0x000000C0u;
  OrFableGuestQwordLow(base, args.r3 + kWideDirtyQwordOffset,
                       kColorClearDirtyBits);
  return {.handled = true, .result = 0};
}

bool IsFableNativeShaderConstantStateHook(std::string_view symbol) {
  return symbol == "sub_8221A880" || // D3DDevice_SetVertexShaderConstantF
         symbol == "sub_82214E30" || // D3DDevice_SetPixelShaderConstantF
         symbol == "sub_82B991B0" || // D3DDevice_SetVertexShaderConstantI
         symbol == "sub_82B99208" || // D3DDevice_SetPixelShaderConstantI
         symbol == "sub_821C6708" || // D3DDevice_SetVertexShaderConstantB
         symbol == "sub_8226E120";   // D3DDevice_SetPixelShaderConstantB
}

bool IsFableNativeShaderConstantCommandHook(std::string_view symbol) {
  return symbol == "sub_82BA76F8" || // D3D::PM4LoadShaderConstantsImmediate
         symbol == "sub_82BA77D0" || // D3D::PM4SetTextureFetchConstant
         symbol == "sub_8222BFA0";   // D3D::SetLiteralShaderConstants
}

bool IsFableNativeShaderConstantReplacementHook(std::string_view symbol) {
  return IsFableNativeShaderConstantStateHook(symbol) ||
         IsFableNativeShaderConstantCommandHook(symbol);
}

FableNativeCommandSubmitEmulation EmulateFableShaderConstantState(
    std::string_view symbol, const RendererHookArgs &args, uint8_t *base) {
  if (!IsFableNativeShaderConstantStateHook(symbol)) {
    return {};
  }

  FableNativeCommandSubmitEmulation emulation;
  emulation.handled = true;
  emulation.result = args.r3;

  if (base == nullptr || !IsLikelyFableObjectAddress(args.r3)) {
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
  if (count == 0) {
    return emulation;
  }

  if (symbol == "sub_8221A880" || symbol == "sub_82214E30") {
    constexpr uint32_t kMaxVectorConstants = 512;
    if (!IsLikelyGuestMemoryAddress(source) || count > kMaxVectorConstants) {
      return emulation;
    }
    const uint32_t destination_base =
        device + (symbol == "sub_8221A880" ? kVertexFloatConstantBaseOffset
                                           : kPixelFloatConstantBaseOffset) +
        first_register * 16u;
    CopyGuestBytes(base, destination_base, source, count * 16u);
    OrFableGuestQwordLow(
        base,
        device + (symbol == "sub_8221A880" ? kVertexFloatDirtyQwordOffset
                                           : kPixelFloatDirtyQwordOffset),
        args.r7);
    return emulation;
  }

  if (symbol == "sub_82B991B0" || symbol == "sub_82B99208") {
    constexpr uint32_t kMaxIntConstants = 64;
    if (!IsLikelyGuestMemoryAddress(source) || count > kMaxIntConstants) {
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
  if (!IsLikelyGuestMemoryAddress(source) || count > kMaxBoolConstants) {
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

FableNativeCommandSubmitEmulation
EmulateFablePm4LoadShaderConstantsImmediate(const RendererHookArgs &args,
                                            uint8_t *base) {
  FableNativeCommandSubmitEmulation emulation;
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

FableNativeCommandSubmitEmulation
EmulateFablePm4SetTextureFetchConstant(const RendererHookArgs &args,
                                       uint8_t *base) {
  FableNativeCommandSubmitEmulation emulation;
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

FableNativeCommandSubmitEmulation
EmulateFableSetLiteralShaderConstants(const RendererHookArgs &args,
                                      uint8_t *base) {
  FableNativeCommandSubmitEmulation emulation;
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

FableNativeCommandSubmitEmulation EmulateFableShaderConstantCommand(
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

FableNativeCommandSubmitEmulation EmulateFableSetPredicationState(
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
    const FableNativeCommandSubmitEmulation kick_emulation =
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

bool IsFableNativePlainRootDrawHook(std::string_view symbol) {
  return symbol == "sub_8221C3E8" || // D3DDevice_DrawPrimitive
         symbol == "sub_8221DFC0";   // D3DDevice_DrawIndexedPrimitive
}

bool IsFableNativePrivateTextureRectangleHook(std::string_view symbol) {
  return symbol == "sub_82206888"; // D3D::PrivateTextureRectangleDraw
}

bool IsFableNativePrivateTextureRectangleWrapperHook(std::string_view symbol) {
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

bool IsFableNativeBeginVerticesWrapperHook(std::string_view symbol) {
  return symbol == "sub_821EBF30" || // screen-rect wrapper; 0x834C3DB8 payload
         symbol == "sub_82242C58" ||
         symbol == "sub_82A61860" ||
         symbol == "sub_82B667F0";
}

FableNativeCommandSubmitEmulation
EmulateFablePrivateTextureRectangleSubmission(std::string_view symbol,
                                              const RendererHookArgs &args,
                                              uint8_t *base) {
  if (!IsFableNativePrivateTextureRectangleHook(symbol) ||
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

FableNativeCommandSubmitEmulation EmulateFablePixCaptureVideoFrame(
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

FableNativeCommandSubmitEmulation EmulateFableDisplayIntervalSync(
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

  RendererHookArgs kick_args = args;
  kick_args.r3 = device;
  kick_args.r3_u64 = device;
  const FableNativeCommandSubmitEmulation kick_emulation =
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

FableNativeCommandSubmitEmulation EmulateFableComplexSwap(
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
    interval_args.r3_u64 = device;
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
  StoreGuestU8(base, device + kDisplayInlineFlagsOffset,
               static_cast<uint8_t>(
                   LoadGuestU8(base, device + kDisplayInlineFlagsOffset) |
                   0x40u));

  RendererHookArgs kick_args = args;
  kick_args.r3 = device;
  kick_args.r3_u64 = device;
  const FableNativeCommandSubmitEmulation kick_emulation =
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

FableNativeCommandSubmitEmulation EmulateFableSceneBatchCommandToggle(
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

FableNativeCommandSubmitEmulation EmulateFableSceneBatchDrawPrimitiveBridge(
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
    toggle_args.r3_u64 = batch_record;
    toggle_args.r4 = 2u;
    toggle_args.r4_u64 = 2u;
    EmulateFableSceneBatchCommandToggle("sub_822194B8", toggle_args, base);
    toggle_args.r4 = 1u;
    toggle_args.r4_u64 = 1u;
    EmulateFableSceneBatchCommandToggle("sub_822194B8", toggle_args, base);
  }

  return {.handled = true, .result = 1};
}

FableNativeCommandSubmitEmulation EmulateFableDrawVerticesUpSubmission(
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

FableNativeCommandSubmitEmulation EmulateFableBeginIndexedVerticesSubmission(
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

  FableNativeCommandSubmitEmulation emulation;
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

FableNativeCommandSubmitEmulation EmulateFableBeginVerticesSubmission(
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

  FableNativeCommandSubmitEmulation emulation;
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

FableNativeCommandSubmitEmulation EmulateFablePlainRootDrawSubmission(
    std::string_view symbol) {
  if (!IsFableNativePlainRootDrawHook(symbol)) {
    return {};
  }

  // The observer records these as typed FableDirect draw commands on hook enter.
  // Native execution only builds/submits the D3D command buffer we are replacing.
  return {.handled = true, .result = 0};
}

void EmulateSuppressedFableDisplaySwapBookkeeping(std::string_view symbol,
                                                  const RendererHookArgs &args,
                                                  uint8_t *base);

FableNativeCommandSubmitEmulation EmulateFableDirectCommandHook(
    std::string_view symbol, const RendererHookArgs &args, uint8_t *base) {
  if (IsFableGetRenderTargetHook(symbol)) {
    return EmulateFableGetRenderTarget(args, base);
  }
  if (IsFableNativePlainRootDrawHook(symbol)) {
    return EmulateFablePlainRootDrawSubmission(symbol);
  }
  if (symbol == "sub_82BA2F68") {
    return EmulateFableDisplayIntervalSync(symbol, args, base);
  }
  if (symbol == "sub_82BA32F8") {
    return EmulateFableComplexSwap(symbol, args, base);
  }
  if (symbol == "sub_82BA34D8") {
    EmulateSuppressedFableDisplaySwapBookkeeping(symbol, args, base);
    return {.handled = true, .result = 0};
  }
  if (FableUnleashedExactHookKindForSymbol(symbol) !=
      FableUnleashedExactHookKind::kHook) {
    return {};
  }

  if (symbol == "sub_822869A0") { // SetRenderTarget
    return EmulateFableSetRenderTarget(args, base);
  }
  if (symbol == "sub_82264590") { // SetDepthStencilSurface
    return EmulateFableSetDepthStencilSurface(args, base);
  }
  if (symbol == "sub_821E2FE0") { // Clear
    return EmulateFableClearCommand(symbol, args, base);
  }
  if (symbol == "sub_821F9F58") { // SetViewport
    return EmulateFableSetViewportPointer(args, base);
  }
  if (symbol == "sub_821B6EE0") { // SetTexture
    return EmulateFableSetTexture(args, base);
  }
  if (symbol == "sub_821F9C08") { // SetScissorRect
    return EmulateFableSetScissorPointer(args, base);
  }
  if (symbol == "sub_82265CD8") { // DrawPrimitiveUP
    return EmulateFableDrawVerticesUpSubmission(symbol, args, base);
  }
  if (symbol == "sub_822224A8") { // SetVertexDeclaration
    return EmulateFableSetVertexDeclaration(args, base);
  }
  if (symbol == "sub_82232510") { // SetVertexShader
    return EmulateFableSetVertexShader(args, base);
  }
  if (symbol == "sub_821B6C60") { // SetStreamSource
    return EmulateFableSetStreamSource(args, base);
  }
  if (symbol == "sub_8219CC30") { // SetIndices
    return EmulateFableSetIndices(args, base);
  }
  if (symbol == "sub_82208BB0") { // SetPixelShader
    return EmulateFableSetPixelShader(args, base);
  }

  return {};
}

bool IsFablePlumeCommandBufferBoundary(std::string_view symbol) {
  return IsFableCommandWriteHook(symbol) || IsFableCommandSubmitHook(symbol) ||
		         IsFableNativeShaderConstantCommandHook(symbol) ||
		         symbol == "sub_8221B010" ||
		         symbol == "sub_8228EF80" ||
		         symbol == "sub_82265CD8" ||
	         symbol == "sub_82217DB8" ||
	         symbol == "sub_82219740" ||
	         symbol == "sub_822194B8" ||
         symbol == "sub_82BA32F8" ||
         symbol == "sub_82BA34D8" ||
         symbol == "sub_82BA2F68" ||
	         symbol == "sub_821C3C60" ||
	         symbol == "sub_8226E180" ||
	         symbol == "sub_8226E738" ||
         symbol == "sub_8223CFA0" ||
         symbol == "sub_822A8F68" ||
         IsFableNativePlainRootDrawHook(symbol) ||
         IsFableNativePrivateTextureRectangleHook(symbol) ||
         IsFableNativePrivateTextureRectangleWrapperHook(symbol) ||
         IsFableNativeBeginVerticesWrapperHook(symbol);
}

bool ShouldPublishFablePlumeCommandBufferBoundary(std::string_view symbol) {
  if (IsFableCommandSubmitHook(symbol)) {
    return true;
  }

  // These are higher-level frame/scene boundaries. Root draw and state hooks
  // are intentionally staged only; publishing them immediately lets the Plume
  // present pump drain partial Fable frames one draw at a time.
  return symbol == "sub_821BA0F0" ||
         symbol == "sub_821C3C60" ||
         symbol == "sub_8226E180" ||
         symbol == "sub_8226E738" ||
         symbol == "sub_8223CFA0" ||
         symbol == "sub_822A8F68" ||
         symbol == "sub_82BA32F8" ||
         symbol == "sub_82BA34D8" ||
         symbol == "sub_82BA2F68";
}

void CaptureFablePlumeCommandBufferBoundary(std::string_view symbol) {
  if (!IsFablePlumeCommandBufferBoundary(symbol)) {
    return;
  }

  const uint64_t submit =
      g_fable_plume_command_buffer_submit_count.fetch_add(
          1, std::memory_order_relaxed) +
      1u;
  FableDirectCaptureSubmittedRenderCommands(
      submit, ShouldPublishFablePlumeCommandBufferBoundary(symbol));
  if (!g_logged_fable_plume_command_buffer_capture) {
    REXLOG_INFO(
        "Refii plume is capturing Fable direct commands at native submit "
        "boundaries");
    g_logged_fable_plume_command_buffer_capture = true;
  }
}

FableNativeCommandSubmitEmulation EmulateFableQueryBufferSpace(
    const RendererHookArgs &args, uint8_t *base) {
  FableNativeCommandSubmitEmulation emulation;
  if (base == nullptr || args.r4 == 0 || args.r5 == 0 ||
      !IsLikelyFableWritableDataDwordAddress(args.r4) ||
      !IsLikelyFableWritableDataDwordAddress(args.r5)) {
    return emulation;
  }

  constexpr uint32_t kPrimaryCommandBaseOffset = 0x33B0;
  constexpr uint32_t kPrimaryCommandEpochOffset = 0x33B4;
  constexpr uint32_t kFallbackFreeBytes = 256u * 1024u;
  constexpr uint32_t kMaxReportedFreeBytes = 4u * 1024u * 1024u;

  uint32_t contiguous_free = kFallbackFreeBytes;
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
      // Native command submission is suppressed, so the D3D command buffer
      // producer can no longer wait for the native GPU to advance this cursor.
      // Make Plume the owner of the guest-visible ring by marking everything
      // written so far as consumed before reporting available space. Reporting
      // synthetic free bytes without updating the cursor lets Fable overrun its
      // own ring state during the video/title handoff.
      StoreGuestU32(base, args.r3 + kPrimaryCommandBaseOffset, primary_write);
      StoreGuestU32(base, args.r3 + kPrimaryCommandEpochOffset,
                    primary_write_epoch);
      contiguous_free = std::clamp(total_bytes, kFallbackFreeBytes,
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

FableNativeCommandSubmitEmulation EmulateFableBlockOnFence(
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
    kick_args.r3_u64 = device;
    EmulateFableKickOff(kick_args, base);
  }
  if (IsLikelyGuestDwordAddress(consumed_fence_pointer)) {
    StoreGuestU32(base, consumed_fence_pointer, current_fence);
  }

  return {.handled = true, .result = device};
}

FableNativeCommandSubmitEmulation EmulateFableNativeCommandSubmission(
    std::string_view symbol, const RendererHookArgs &args, uint8_t *base) {
  FableNativeCommandSubmitEmulation emulation;
  if (IsFableSurfaceSizeHook(symbol)) {
    return EmulateFableSurfaceSize(symbol, args, base);
  }
  if (!RendererSuppressNativeCommandSubmissionEnabled()) {
    return emulation;
  }

  if (const FableNativeCommandSubmitEmulation direct =
          EmulateFableDirectCommandHook(symbol, args, base);
      direct.handled) {
    emulation = direct;
  } else if (symbol == "sub_822866E0") {
    emulation = EmulateFableAddCallsToPrimaryBuffer(args, base);
  } else if (symbol == "sub_82BA25C0") {
    emulation = EmulateFableAddCommandsToPrimaryBuffer(args, base);
  } else if (symbol == "sub_822868C8") {
    emulation = EmulateFableQueueIndirectBuffer(args, base);
  } else if (symbol == "sub_821E8D38") {
    emulation = EmulateFableKickOffSegment(args, base);
  } else if (symbol == "sub_821E8EC0") {
    emulation = EmulateFableKickOff(args, base);
  } else if (symbol == "sub_8221F2E0") {
    emulation = EmulateFableFlushPendingGpuState(args, base);
  } else if (symbol == "sub_8221E4A0") {
    emulation = EmulateFableDeferredStateDispatchHook(args, base);
  } else if (symbol == "sub_8221E550") {
    emulation = EmulateFablePendingStateDispatchHook(args, base);
  } else if (symbol == "sub_8221F348") {
    emulation = EmulateFablePendingStateQueueHook(args, base);
  } else if (symbol == "sub_822C6BA8") {
    emulation = EmulateFableInvalidateCaches(args, base);
  } else if (symbol == "sub_82196628") {
    emulation = EmulateFableBeginExport(args, base);
  } else if (symbol == "sub_82196750") {
    emulation = EmulateFableEndExport(args, base);
  } else if (symbol == "sub_8217F3E8") {
    emulation = EmulateFableQueryBufferSpace(args, base);
  } else if (symbol == "sub_821F6270") {
    // D3DDevice_IsFencePending takes a fence token and returns whether native
    // GPU execution is still behind it. Once Plume suppresses native command
    // submission there is no D3D fence to wait for, but the original helper
    // also kicks the current device if the queried token is the current fence.
    const uint32_t device =
        g_last_fable_device.load(std::memory_order_relaxed);
    constexpr uint32_t kCurrentFenceOffset = 0x2A9C;
    if (base != nullptr && device != 0 &&
        args.r3 == LoadGuestU32(base, device + kCurrentFenceOffset)) {
      RendererHookArgs kick_args = args;
      kick_args.r3 = device;
      kick_args.r3_u64 = device;
      EmulateFableKickOff(kick_args, base);
    }
    emulation.handled = true;
    emulation.result = 0;
  } else if (symbol == "sub_82242668") {
    emulation = EmulateFableBlockOnFence(args, base);
  } else if (symbol == "sub_8221B010") {
    emulation = EmulateFableSetPendingShaders(args, base);
  } else if (symbol == "sub_8221C7D8") {
    emulation = EmulateFableSetPendingRenderStates(args, base);
  } else if (symbol == "sub_8221D1B0") {
    emulation = EmulateFableSetPendingFetchConstants(args, base);
  } else if (symbol == "sub_8228EF80") {
    emulation = EmulateFableSetPendingSplit(args, base);
  } else if (symbol == "sub_822154B0") {
    emulation = EmulateFableSetPendingPredicated(args, base);
  } else if (symbol == "sub_822B4008") {
    emulation = EmulateFableSetPendingClipPlanes(args, base);
  } else if (symbol == "sub_821F9D00") {
    emulation = EmulateFableSetViewportFloats(args, base);
  } else if (symbol == "sub_821F9918") {
    emulation = EmulateFableSetSurfaceClip(args, base);
  } else if (symbol == "sub_8219CD68") {
    emulation = EmulateFableSetShaderGPRAllocation(args, base);
  } else if (symbol == "sub_822AF568") {
    emulation = EmulateFableSetScreenExtentQueryMode(args, base);
  } else if (symbol == "sub_82BAEC38") {
    emulation = EmulateFableSetScalerParameters(args, base);
  } else if (symbol == "sub_82BAEA88") {
    emulation = EmulateFableSetupScaler(args, base);
  } else if (symbol == "sub_822655F0") {
    emulation = EmulateFableSetPredicationState(args, base);
  } else if (IsFableNativeShaderConstantCommandHook(symbol)) {
    emulation = EmulateFableShaderConstantCommand(symbol, args, base);
  } else if (IsFableNativeShaderConstantStateHook(symbol)) {
    emulation = EmulateFableShaderConstantState(symbol, args, base);
  } else if (FableRenderStateSlotForSymbol(symbol) != UINT32_MAX) {
    emulation = EmulateFableSetRenderState(symbol, args, base);
  } else if (FableSamplerStateSlotForSymbol(symbol) != UINT32_MAX) {
    emulation = EmulateFableSetSamplerState(symbol, args, base);
  } else if (symbol == "sub_821EF830") {
    emulation = EmulateFableClearCommand(symbol, args, base);
  } else if (symbol == "sub_822A09A0") {
    emulation = EmulateFableSetColorClear(args, base);
  } else if (symbol == "sub_82BA95E0") {
    emulation = EmulateFablePixCaptureVideoFrame(symbol, args, base);
  } else if (symbol == "sub_82219740") {
    emulation = EmulateFableSceneBatchDrawPrimitiveBridge(symbol, args, base);
  } else if (symbol == "sub_822194B8") {
    emulation = EmulateFableSceneBatchCommandToggle(symbol, args, base);
  } else if (IsFableNativePrivateTextureRectangleHook(symbol)) {
    emulation = EmulateFablePrivateTextureRectangleSubmission(symbol, args,
                                                              base);
  } else if (symbol == "sub_82205F68") {
    emulation = EmulateFableBeginVerticesSubmission(symbol, args, base);
  } else if (symbol == "sub_82217DB8") {
    emulation = EmulateFableBeginIndexedVerticesSubmission(symbol, args, base);
  }

  if (emulation.handled && !g_logged_fable_command_submit_emulation) {
    REXLOG_INFO(
        "Refii plume is emulating Fable native command submit bookkeeping "
        "target=0x{:08X}",
        args.r3);
    g_logged_fable_command_submit_emulation = true;
  }
  if (symbol == "sub_821E8D38" && emulation.handled &&
      !g_logged_fable_kickoff_segment_emulation) {
    REXLOG_INFO("Refii plume is emulating Fable KickOffSegment bookkeeping "
                "device=0x{:08X}",
                args.r3);
    g_logged_fable_kickoff_segment_emulation = true;
  }
  if (symbol == "sub_821E8EC0" && emulation.handled &&
      !g_logged_fable_kickoff_emulation) {
    REXLOG_INFO(
        "Refii plume is emulating Fable KickOff bookkeeping device=0x{:08X}",
        args.r3);
    g_logged_fable_kickoff_emulation = true;
  }
  return emulation;
}

void StoreFablePresentCounters(uint32_t device, uint32_t submitted,
                               uint32_t callbacks, uint32_t completed) {
  g_last_fable_device.store(device, std::memory_order_relaxed);
  g_last_fable_present_submitted.store(submitted, std::memory_order_relaxed);
  g_last_fable_swap_callbacks.store(callbacks, std::memory_order_relaxed);
  g_last_fable_present_completed.store(completed, std::memory_order_relaxed);
  g_last_fable_present_pending.store(submitted - completed,
                                     std::memory_order_relaxed);
  TracyPlot("Refii Fable Presents Pending",
            static_cast<int64_t>(submitted - completed));
}

void RememberFableMemoryBase(uint8_t *base) {
  if (base != nullptr) {
    g_last_fable_memory_base.store(reinterpret_cast<uintptr_t>(base),
                                   std::memory_order_relaxed);
  }
}

void EmulateSuppressedFableSyncIntervalBookkeeping(uint32_t device,
                                                   uint8_t *base) {
  if (base == nullptr || device == 0) {
    return;
  }

  constexpr uint32_t kDisplayControlOffset = 0x2ABC;
  constexpr uint32_t kDisplayFlags2Offset = 0x2ABD;
  constexpr uint32_t kScanoutSurfaceOffset = 0x2AB4;
  constexpr uint32_t kScanoutAuxOffset = 0x2AB8;
  constexpr uint32_t kPersistSurfaceOffset = 0x31A4;
  constexpr uint32_t kPersistAuxOffset = 0x31A8;
  constexpr uint32_t kPersistModeOffset = 0x31AC;

  StoreGuestU32(base, device + kPersistSurfaceOffset, 0xFFFFFFFFu);
  StoreGuestU32(base, device + kPersistAuxOffset, 0);
  StoreGuestU32(base, device + kPersistModeOffset, 0);
  StoreGuestU32(base, device + kScanoutSurfaceOffset, 0);
  StoreGuestU32(base, device + kScanoutAuxOffset, 0);
  StoreGuestU8(base, device + kDisplayControlOffset,
               LoadGuestU8(base, device + kDisplayControlOffset) | 1u);
  StoreGuestU8(base, device + kDisplayFlags2Offset,
               LoadGuestU8(base, device + kDisplayFlags2Offset) | 0x10u);
}

void CompleteFableDisplaySwapBookkeeping(uint32_t device, uint8_t *base,
                                         uint32_t submitted) {
  if (base == nullptr || device == 0 || submitted == 0) {
    return;
  }

  constexpr uint32_t kSwapCallbackOffset = 0x40B4;
  constexpr uint32_t kCompletedOffset = 0x40B8;
  constexpr uint32_t kCurrentBackBufferOffset = 0x2A9C;
  constexpr uint32_t kLastBackBufferOffset = 0x2AB0;
  constexpr uint32_t kDisplayFlagsOffset = 0x2ABD;

  const uint32_t callbacks = std::max(
      LoadGuestU32(base, device + kSwapCallbackOffset) + 1u, submitted);
  const uint32_t completed =
      std::max(LoadGuestU32(base, device + kCompletedOffset), submitted);
  StoreGuestU32(base, device + kSwapCallbackOffset, callbacks);
  StoreGuestU32(base, device + kCompletedOffset, completed);
  EmulateSuppressedFableSyncIntervalBookkeeping(device, base);

  const uint32_t current_back_buffer =
      LoadGuestU32(base, device + kCurrentBackBufferOffset);
  StoreGuestU32(base, device + kLastBackBufferOffset, current_back_buffer);
  StoreGuestU8(base, device + kDisplayFlagsOffset,
               LoadGuestU8(base, device + kDisplayFlagsOffset) & 0xEFu);

  StoreFablePresentCounters(device, submitted, callbacks, completed);
  if (!g_logged_fable_present_bookkeeping) {
    REXLOG_INFO("Refii plume is emulating suppressed Fable display bookkeeping "
                "device=0x{:08X} submitted={} completed={} callbacks={}",
                device, submitted, completed, callbacks);
    g_logged_fable_present_bookkeeping = true;
  }
}

void EmulateSuppressedFableDisplaySwapBookkeeping(std::string_view symbol,
                                                  const RendererHookArgs &args,
                                                  uint8_t *base) {
  if (symbol != "sub_82BA34D8" || base == nullptr || args.r3 == 0) {
    return;
  }

  constexpr uint32_t kSubmittedOffset = 0x40B0;
  const uint32_t device = args.r3;
  RememberFableMemoryBase(base);
  const uint32_t submitted = LoadGuestU32(base, device + kSubmittedOffset) + 1u;
  StoreGuestU32(base, device + kSubmittedOffset, submitted);
  CompleteFableDisplaySwapBookkeeping(device, base, submitted);
}

void CompleteNativeFableDisplaySwapBookkeeping(const RendererHookArgs &args,
                                               uint8_t *base) {
  if (base == nullptr || args.r3 == 0) {
    return;
  }

  constexpr uint32_t kSubmittedOffset = 0x40B0;
  const uint32_t device = args.r3;
  RememberFableMemoryBase(base);
  CompleteFableDisplaySwapBookkeeping(
      device, base, LoadGuestU32(base, device + kSubmittedOffset));
}

void LogPlumePumpFablePresentationPolicy() {
  if (!g_logged_plume_pump_fable_bookkeeping) {
    REXLOG_INFO(
        "Refii plume present pump may advance Fable display bookkeeping when "
        "suppressed display-swap hooks stop arriving");
    g_logged_plume_pump_fable_bookkeeping = true;
  }
}

bool TryAdvanceFablePresentationFromPump(uint32_t target_fps) {
  if (!RendererPlumePresentPumpFableBookkeepingEnabled()) {
    return false;
  }

  uint8_t *base =
      reinterpret_cast<uint8_t *>(g_last_fable_memory_base.load(
          std::memory_order_relaxed));
  const uint32_t device = g_last_fable_device.load(std::memory_order_relaxed);
  if (base == nullptr || device == 0) {
    return false;
  }

  const int64_t now_ns = RendererTraceNowNs();
  const int64_t last_display_ns =
      g_last_display_present_hook_ns.load(std::memory_order_relaxed);
  const int64_t interval_ns =
      static_cast<int64_t>(PlumePresentPumpInterval(target_fps).count());
  if (last_display_ns != 0 && now_ns - last_display_ns < interval_ns * 2) {
    return false;
  }

  constexpr uint32_t kSubmittedOffset = 0x40B0;
  constexpr uint32_t kCompletedOffset = 0x40B8;
  const uint32_t submitted = LoadGuestU32(base, device + kSubmittedOffset);
  const uint32_t completed = LoadGuestU32(base, device + kCompletedOffset);
  if (submitted != completed) {
    CompleteFableDisplaySwapBookkeeping(device, base, submitted);
    g_plume_present_pump_fable_advance_count.fetch_add(
        1, std::memory_order_relaxed);
    if (!g_logged_plume_pump_fable_bookkeeping_complete) {
      REXLOG_INFO(
          "Refii plume present pump is completing pending Fable display "
          "bookkeeping device=0x{:08X} submitted={} completed={}",
          device, submitted, completed);
      g_logged_plume_pump_fable_bookkeeping_complete = true;
    }
    return true;
  }

  const uint32_t next_submitted = submitted + 1u;
  StoreGuestU32(base, device + kSubmittedOffset, next_submitted);
  CompleteFableDisplaySwapBookkeeping(device, base, next_submitted);
  g_plume_present_pump_fable_advance_count.fetch_add(
      1, std::memory_order_relaxed);

  if (!g_logged_plume_pump_fable_bookkeeping_advance) {
    REXLOG_INFO(
        "Refii plume present pump is advancing Fable display bookkeeping "
        "device=0x{:08X} submitted={}",
        device, next_submitted);
    g_logged_plume_pump_fable_bookkeeping_advance = true;
  }
  return true;
}

bool HasActiveFableDirectCommands(const FableDirectStateSnapshot &snapshot) {
  return snapshot.draw_count != 0 || snapshot.indexed_draw_count != 0 ||
         snapshot.last_command_boundary_pending_draws != 0 ||
         snapshot.last_submitted_draw_event_count != 0;
}

bool PresentPlumeFrameFromRendererHook(const char *symbol,
                                       std::string_view reason,
                                       bool drive_fable_frame = false) {
  ZoneNamedN(tracy_present_zone, "RefiiRenderer/PresentPlumeFrameFromHook",
             true);
  ZoneTextV(tracy_present_zone, symbol, std::strlen(symbol));
  ZoneTextV(tracy_present_zone, reason.data(), reason.size());
  ConfigureRendererTracyPlots();

  const int64_t now_ns = RendererTraceNowNs();
  g_plume_present_request_count.fetch_add(1, std::memory_order_relaxed);
  g_last_plume_present_request_gap_us.store(
      PlotDeltaUs("Refii Plume Present Request Gap Us",
                  g_last_plume_present_request_ns, now_ns),
      std::memory_order_relaxed);
  {
    std::lock_guard lock(g_hook_timing_mutex);
    CopyTimingText(g_last_plume_present_symbol,
                   sizeof(g_last_plume_present_symbol), symbol);
    CopyTimingText(g_last_plume_present_reason,
                   sizeof(g_last_plume_present_reason), reason);
  }

  const bool presented = PresentPlumeFrame(drive_fable_frame);
  TracyPlot("Refii Plume Present Result",
            static_cast<int64_t>(presented ? 1 : 0));
  return presented;
}

void PlumePresentPumpMain() {
  ZoneScopedN("RefiiRenderer/PlumePresentPumpMain");
  ConfigureRendererTracyPlots();
  REXLOG_INFO("Refii plume present pump started");

  auto next_present = RendererTraceClock::now();
  while (!g_plume_present_pump_stop_requested.load(std::memory_order_acquire)) {
    const uint32_t target_fps = RendererPlumePresentTargetFps();
    const std::chrono::nanoseconds interval =
        PlumePresentPumpInterval(target_fps);
    next_present += interval;

    {
      std::unique_lock lock(g_present_pump_mutex);
      g_present_pump_cv.wait_until(lock, next_present, [] {
        return g_plume_present_pump_stop_requested.load(
            std::memory_order_acquire);
      });
    }

    if (g_plume_present_pump_stop_requested.load(std::memory_order_acquire)) {
      break;
    }
    if (!RendererPlumePresentPumpEnabled()) {
      next_present = RendererTraceClock::now();
      continue;
    }

    const int64_t now_ns = RendererTraceNowNs();
    g_plume_present_pump_request_count.fetch_add(1, std::memory_order_relaxed);
    g_last_plume_present_pump_gap_us.store(
        PlotDeltaUs("Refii Plume Present Pump Gap Us",
                    g_last_plume_present_pump_request_ns, now_ns),
        std::memory_order_relaxed);

    const FableDirectStateSnapshot pre_present_direct =
        GetFableDirectStateSnapshot();
    const bool presented = PresentPlumeFrameFromRendererHook(
        "plume_present_pump", "fps-pump", true);
    if (presented) {
      LogPlumePumpFablePresentationPolicy();
      const FableDirectStateSnapshot post_present_direct =
          GetFableDirectStateSnapshot();
      const bool has_fable_direct_work =
          HasActiveFableDirectCommands(post_present_direct);
      if (has_fable_direct_work) {
        if (!g_logged_plume_pump_fable_bookkeeping_deferred_for_work) {
          REXLOG_INFO(
              "Refii plume present pump is deferring Fable display "
              "bookkeeping while direct render work remains active "
              "pre={}/{}/{}/{}/{} post={}/{}/{}/{}/{}",
              pre_present_direct.render_command_count,
              pre_present_direct.submitted_render_command_count,
              pre_present_direct.draw_event_count,
              pre_present_direct.draw_count,
              pre_present_direct.last_command_boundary_pending_draws,
              post_present_direct.render_command_count,
              post_present_direct.submitted_render_command_count,
              post_present_direct.draw_event_count,
              post_present_direct.draw_count,
              post_present_direct.last_command_boundary_pending_draws);
          g_logged_plume_pump_fable_bookkeeping_deferred_for_work = true;
        }
      } else {
        TryAdvanceFablePresentationFromPump(target_fps);
      }
    }
    if (presented && !g_logged_plume_present_pump_driver) {
      REXLOG_INFO(
          "Refii plume output is presenting from the Plume FPS pump at {} FPS",
          target_fps);
      g_logged_plume_present_pump_driver = true;
    }

    const auto now = RendererTraceClock::now();
    if (next_present + interval < now) {
      next_present = now;
    }
  }

  REXLOG_INFO("Refii plume present pump stopped");
}

void StopPlumePresentPumpThread() {
  g_plume_present_pump_shutdown_requested.store(true,
                                                std::memory_order_release);
  g_plume_present_pump_stop_requested.store(true, std::memory_order_release);
  g_present_pump_cv.notify_all();

  if (!g_plume_present_pump_running.exchange(false,
                                             std::memory_order_acq_rel) &&
      !g_present_pump_thread.joinable()) {
    return;
  }

  if (g_present_pump_thread.joinable()) {
    if (g_present_pump_thread.get_id() == std::this_thread::get_id()) {
      g_present_pump_thread.detach();
    } else {
      g_present_pump_thread.join();
    }
  }
}

void EnsurePlumePresentPumpStarted() {
  if (g_plume_present_pump_shutdown_requested.load(std::memory_order_acquire)) {
    return;
  }
  if (!RendererPlumePresentPumpEnabled()) {
    return;
  }

  bool expected = false;
  if (!g_plume_present_pump_running.compare_exchange_strong(
          expected, true, std::memory_order_acq_rel)) {
    return;
  }

  std::call_once(g_present_pump_atexit_once,
                 [] { std::atexit([] { StopPlumePresentPumpThread(); }); });
  if (g_plume_present_pump_shutdown_requested.load(std::memory_order_acquire)) {
    g_plume_present_pump_running.store(false, std::memory_order_release);
    return;
  }
  g_plume_present_pump_stop_requested.store(false, std::memory_order_release);
  g_present_pump_thread = std::thread(PlumePresentPumpMain);
}

const char *RendererBackendName(RendererBackend backend) {
  switch (backend) {
  case RendererBackend::kNative:
    return "native";
  case RendererBackend::kPlume:
    return "plume";
  case RendererBackend::kDisabled:
    return "disabled";
  }

  return "unknown";
}

const char *RendererGraphicsApiName(RendererGraphicsApi api) {
  switch (api) {
  case RendererGraphicsApi::kVulkan:
    return "vulkan";
  case RendererGraphicsApi::kD3D12:
    return "d3d12";
  case RendererGraphicsApi::kMetal:
    return "metal";
  }

  return "unknown";
}

bool ShouldTraceHook(RendererHookKind kind) {
  if (RendererHookTracingEnabled()) {
    return true;
  }

  switch (kind) {
  case RendererHookKind::kResource:
    return RendererResourceTracingEnabled();
  case RendererHookKind::kDraw:
    return RendererDrawTracingEnabled();
  case RendererHookKind::kGeneral:
    return false;
  }

  return false;
}

bool IsNativePresentationWorkHook(std::string_view symbol) {
  // D3DDevice_Swap owns Fable's PixCaptureVideoFrame call, so the wrapper must
  // run even when Plume owns presentation. Suppress the lower swap/sync helpers
  // that submit native presentation work instead.
  return symbol == "sub_82BA32F8" || // ComplexSwap
         symbol == "sub_82BA2DB8" || // SwapCallback
         symbol == "sub_82BA2F68" || // SynchronizeToPresentationInterval
         symbol == "sub_82BA2748";   // D3DDevice_BlockUntilIdle
}

bool IsFableNativeStateSubmissionHook(std::string_view symbol) {
  if (FableRenderStateSlotForSymbol(symbol) != UINT32_MAX ||
      FableSamplerStateSlotForSymbol(symbol) != UINT32_MAX ||
      FableVertexDeclarationHookKindForSymbol(symbol) !=
          FableVertexDeclarationHookKind::kNone ||
      FableBoundShaderKindForSymbol(symbol) !=
          FableHookResourceKind::kUnknown) {
    return true;
  }

  return symbol == "sub_822869A0" || // D3DDevice_SetRenderTarget
         symbol == "sub_82264590" || // D3DDevice_SetDepthStencilSurface
         symbol == "sub_821F9D00" || // D3D::SetViewport
         symbol == "sub_821F9918" || // D3D::SetSurfaceClip
         symbol == "sub_821F9F58" || // D3DDevice_SetViewport
         symbol == "sub_821F9C08" || // D3DDevice_SetScissorRect
         symbol == "sub_821B6EE0" || // D3DDevice_SetTexture
         symbol == "sub_821B6C60" || // D3DDevice_SetStreamSource
         symbol == "sub_8219CC30" || // D3DDevice_SetIndices
         symbol == "sub_8219CD68" || // D3DDevice_SetShaderGPRAllocation
         symbol == "sub_821E2FE0" || // D3DDevice_ClearF
         symbol == "sub_821EF830" || // D3D::ClearRect
         symbol == "sub_822A09A0" || // D3D::SetColorClear
         symbol == "sub_8221E550";   // pending state dispatch trampoline
}

bool IsFableNativeDrawSubmissionHook(std::string_view symbol) {
  if (IsFableRootDrawSubmitHook(symbol) ||
      IsFableWrappedDrawSubmitHook(symbol) || IsFablePendingStateHook(symbol) ||
      IsFableCommandWriteHook(symbol) || IsFableCommandSubmitHook(symbol)) {
    return true;
  }

  return symbol == "sub_8221F2E0" || // flush pending per-context state
         symbol == "sub_822866E0" || // AddCallsToPrimaryBuffer
         symbol == "sub_822868C8" || // QueueIndirectBuffer
         symbol == "sub_821E8D38" || // KickOffSegment(void)
         symbol == "sub_821E8EC0" || // KickOff(void)
         symbol == "sub_8221E4A0" || // deferred pending-state dispatch
         symbol == "sub_8221F348" || // process one pending-state queue
         symbol == "sub_82196628" || // BeginExport
         symbol == "sub_82196750" || // EndExport
         symbol == "sub_822C6BA8";   // InvalidateCaches
}

bool IsFableNativeCommandSubmissionHook(std::string_view symbol) {
  // Keep replacement static at the boundary where work would reach native D3D,
  // but let Fable's pending-state helpers run for now. Those helpers advance
  // guest-side command/state bookkeeping used by Bink/title flow; KickOff and
  // primary-buffer submission remain suppressed below, so native GPU submission
  // is still cut off while Plume owns presentation.
  return symbol == "sub_82BA25C0" || // AddCommandsToPrimaryBuffer
         symbol == "sub_822866E0" || // AddCallsToPrimaryBuffer
         symbol == "sub_822868C8" || // QueueIndirectBuffer
         symbol == "sub_821E8D38" || // KickOffSegment
         symbol == "sub_821E8EC0" || // KickOff
         symbol == "sub_82196628" || // BeginExport
         symbol == "sub_82196750" || // EndExport
         symbol == "sub_8217F3E8" || // D3DDevice_QueryBufferSpace
         symbol == "sub_821F6270" || // D3DDevice_IsFencePending
         symbol == "sub_82242668" || // D3D::CDevice::BlockOnFence
         symbol == "sub_822C6BA8";   // InvalidateCaches
}

bool IsFableNativeStateEmulationHook(std::string_view symbol) {
  // Suppress state hooks only after their guest-visible device writes have
  // direct emulation. Shader hooks still patch inline constant state natively.
  return FableRenderStateSlotForSymbol(symbol) != UINT32_MAX ||
         FableSamplerStateSlotForSymbol(symbol) != UINT32_MAX ||
         symbol == "sub_821E2FE0" || // D3DDevice_ClearF
         symbol == "sub_821EF830" || // D3D::ClearRect
         symbol == "sub_822A09A0" || // D3D::SetColorClear
         symbol == "sub_822224A8" || // SetVertexDeclaration
         symbol == "sub_821F9F58" || // D3DDevice_SetViewport
         symbol == "sub_821F9C08" || // D3DDevice_SetScissorRect
         symbol == "sub_821F9D00" || // D3D::SetViewport
         symbol == "sub_821F9918" || // D3D::SetSurfaceClip
         symbol == "sub_8219CD68" || // D3DDevice_SetShaderGPRAllocation
         symbol == "sub_822655F0" || // D3DDevice_SetPredication
         symbol == "sub_822AF568" || // D3DDevice_SetScreenExtentQueryMode
         symbol == "sub_82BAEC38" || // D3DDevice_SetScalerParameters
         symbol == "sub_82BAEA88" || // D3D::SetupScaler
         symbol == "sub_822869A0" || // D3DDevice_SetRenderTarget
         symbol == "sub_82264590";   // D3DDevice_SetDepthStencilSurface
}

bool IsFableNativePendingStateEmulationHook(std::string_view symbol) {
  // These pending-state packet builders are now mirrored directly. Keep shader
  // patch helpers native until their inline guest-memory side effects are fully
  // represented in Plume commands. D3D::SetPending_Shaders is deliberately not
  // suppressed: IDA shows it calls Fable's DirectShaderPatch and
  // IncrementalShaderPatchAndLoad paths before writing PM4, and the direct
  // renderer needs those guest-visible patched shader/declaration side effects.
  return symbol == "sub_8221E550" || // pending state dispatch trampoline
         symbol == "sub_8221C7D8" || // D3D::SetPending_RenderStates
         symbol == "sub_8221D1B0" || // D3D::SetPending_FetchConstants
         symbol == "sub_822154B0" || // D3D::SetPending_Predicated
         symbol == "sub_8228EF80" || // D3D::SetPending_Split
         symbol == "sub_822B4008";   // D3D::SetPending_ClipPlanes
}

bool IsFableNativeClearSubmissionHook(std::string_view symbol) {
  return symbol == "sub_821E2FE0" || // D3DDevice_ClearF
         symbol == "sub_821EF830";   // D3D::ClearRect
}

bool IsFableNativeBindingStateSubmissionHook(std::string_view symbol) {
  return symbol == "sub_821B6C60" || // D3DDevice_SetStreamSource
         symbol == "sub_8219CC30" || // D3DDevice_SetIndices
         symbol == "sub_821B6EE0" || // D3DDevice_SetTexture
         symbol == "sub_82232510" || // D3DDevice_SetVertexShader
         symbol == "sub_82208BB0";   // D3DDevice_SetPixelShader
}

bool IsFableNativeTerminalDrawSubmissionHook(std::string_view symbol) {
  return IsFableNativePrivateTextureRectangleHook(symbol) ||
         symbol == "sub_82BA95E0" ||
         symbol == "sub_82219740" ||
         symbol == "sub_822194B8" ||
         symbol == "sub_82205F68" ||
         symbol == "sub_82265CD8" ||
         symbol == "sub_82217DB8" ||
         IsFableNativePlainRootDrawHook(symbol);
}

bool IsNativeRenderWorkHook(std::string_view symbol) {
  if (RendererSuppressNativeCommandSubmissionEnabled() &&
      FableUnleashedExactHookKindForSymbol(symbol) ==
          FableUnleashedExactHookKind::kStub) {
    return true;
  }
  if (RendererReplaceResourcesEnabled() &&
      (IsFableResourceLifetimeHook(symbol) ||
       IsFableResourceHeaderBuildHook(symbol) ||
       IsFableResourceUpdateReplacementHook(symbol) ||
       IsFableBufferCreateHook(symbol) ||
       IsFableSurfaceCopyHook(symbol) ||
       IsFableSurfaceMemoryCopyHook(symbol) ||
       IsFableTextureHeaderUtilityHook(symbol) ||
       IsFableGetRenderTargetHook(symbol) ||
       IsFableSurfaceSizeHook(symbol))) {
    return true;
  }
  if (RendererReplacePresentEnabled() && IsNativePresentationWorkHook(symbol)) {
    return true;
  }
  if (RendererSuppressNativeCommandSubmissionEnabled() &&
      IsFableNativeShaderConstantReplacementHook(symbol)) {
    return true;
  }
  if (RendererSuppressNativeCommandSubmissionEnabled() &&
      IsFableNativeCommandSubmissionHook(symbol)) {
    return true;
  }
  if (RendererSuppressNativeCommandSubmissionEnabled() &&
      IsFableNativeClearSubmissionHook(symbol)) {
    return true;
  }
  if (RendererSuppressNativeCommandSubmissionEnabled() &&
      IsFableNativeBindingStateSubmissionHook(symbol)) {
    return true;
  }
  if (RendererSuppressNativeCommandSubmissionEnabled() &&
      IsFableNativeStateEmulationHook(symbol)) {
    return true;
  }
  if (RendererSuppressNativeCommandSubmissionEnabled() &&
      IsFableNativePendingStateEmulationHook(symbol)) {
    return true;
  }
  if (RendererSuppressNativeCommandSubmissionEnabled() &&
      IsFableNativeTerminalDrawSubmissionHook(symbol)) {
    return true;
  }

  return false;
}

const char *PlumeGuestPathDescription(RendererHookKind kind) {
  switch (kind) {
  case RendererHookKind::kResource:
    return "guest resource path active for Plume-owned resource tracking";
  case RendererHookKind::kDraw:
    return "guest draw/state path active for Plume staging";
  case RendererHookKind::kGeneral:
    return "guest utility path active for Plume tracking";
  }

  return "guest path active for Plume tracking";
}

std::string FableHookLogName(const char *symbol) {
  const char *lumen_name = FableHookName(symbol);
  if (lumen_name == nullptr || lumen_name[0] == '\0' ||
      std::string_view(lumen_name) == symbol) {
    return std::string(symbol);
  }
  return std::string(lumen_name) + " (" + symbol + ")";
}

const char *FableNativeFallbackCategory(const char *symbol) {
  if (IsFableVertexDeclarationCreateHook(symbol)) {
    return "vertex-declaration-create";
  }
  if (IsFableShaderCreateHook(symbol)) {
    return "shader-create";
  }
  if (IsFableTextureCreateHook(symbol)) {
    return "texture-create";
  }
  if (IsFableSurfaceCreateHook(symbol)) {
    return "surface-create";
  }
  if (IsFableRenderDepthSurfaceCtorHook(symbol)) {
    return "render-depth-surface-ctor";
  }
  if (IsFableBinkVideoPlaneSetupHook(symbol)) {
    return "bink-video-plane-setup";
  }
  if (IsFableBinkVideoPlaneSurfaceSetupHook(symbol)) {
    return "bink-video-plane-surface-setup";
  }
  if (IsFableSurfaceWrapperCreateHook(symbol)) {
    return "surface-wrapper-create";
  }
  if (IsFableBufferCreateHook(symbol)) {
    return "buffer-create";
  }
  if (IsFableSurfaceViewCreateHook(symbol)) {
    return "surface-view-create";
  }
  if (IsFableResourceQueryHook(symbol)) {
    return "resource-query";
  }
  if (IsFableTextureAddressOffsetHook(symbol)) {
    return "texture-address-offset";
  }
  if (IsFableTextureHeaderBuildHook(symbol)) {
    return "texture-header-build";
  }
  if (IsFableTextureHeaderUtilityHook(symbol)) {
    return "texture-header-utility";
  }
  if (IsFableCpuResourceHelperHook(symbol)) {
    return "cpu-resource-helper";
  }
  if (IsFableSurfaceMemoryCopyHook(symbol)) {
    return "surface-memory-copy";
  }
  if (IsFableTextureFillHook(symbol)) {
    return "texture-fill";
  }
  if (IsFableTextureFileCreateHook(symbol)) {
    return "texture-file-create";
  }
  if (IsFableSurfaceCopyHook(symbol)) {
    return "surface-copy";
  }
  if (IsFableTextureSurfaceLockHook(symbol)) {
    return "texture-surface-lock";
  }
  if (std::string_view(symbol) == "rex_lhGfx_LockBuffer") {
    return "resource-lock";
  }
  if (IsFableResourceLifetimeHook(symbol)) {
    return "resource-lifetime";
  }
  if (IsFableResourceUpdateReplacementHook(symbol)) {
    return "resource-update";
  }
  if (IsFableShaderPatchHelperHook(symbol)) {
    return "shader-patch-helper";
  }
  if (IsFableShaderUtilityHook(symbol)) {
    return "shader-utility";
  }

  return "resource-other";
}

bool IsMandatoryFableResourceReplacementHook(std::string_view symbol) {
  return IsFableSurfaceViewCreateHook(symbol) ||
         IsFableBufferCreateHook(symbol) ||
         IsFableVertexDeclarationCreateHook(symbol) ||
         IsFableShaderCreateHook(symbol) ||
         IsFableVertexShaderRegisterHook(symbol) ||
         IsFableShaderUtilityHook(symbol) ||
         IsFableTextureCreateHook(symbol) ||
         IsFableSurfaceCreateHook(symbol) ||
         IsFableSurfaceWrapperCreateHook(symbol) ||
         IsFableRenderDepthSurfaceCtorHook(symbol) ||
         IsFableRenderTargetSetupHook(symbol) ||
         IsFableBinkVideoPlaneSetupHook(symbol) ||
         IsFableBinkVideoPlaneSurfaceSetupHook(symbol) ||
         IsFableTextureSurfaceLockHook(symbol) ||
         IsFableSurfaceCopyHook(symbol) ||
         IsFableSurfaceMemoryCopyHook(symbol) ||
         IsFableTextureFillHook(symbol) ||
         IsFableTextureFileCreateHook(symbol) ||
         IsFableResourceQueryHook(symbol) ||
         IsFableTextureAddressOffsetHook(symbol) ||
         IsFableTextureHeaderBuildHook(symbol) ||
         IsFableTextureHeaderUtilityHook(symbol) ||
         IsFableCpuResourceHelperHook(symbol) ||
         IsFableResourceLockHook(symbol) ||
         IsFableResourceUnlockHook(symbol) ||
         IsFableResourceLifetimeHook(symbol) ||
         IsFableResourceHeaderBuildHook(symbol) ||
         IsFableResourceUpdateReplacementHook(symbol) ||
         IsFableGetRenderTargetHook(symbol) ||
         IsFableSurfaceSizeHook(symbol);
}

bool CanEmulateMandatoryFableResourceReplacementHook(
    std::string_view symbol, const RendererHookArgs &args, uint8_t *base) {
  if (IsFableTextureSurfaceLockHook(symbol)) {
    return CanEmulateFableTextureSurfaceLock(symbol, args, base);
  }
  if (symbol == "sub_8225F980" || symbol == "sub_82196980" ||
      symbol == "rex_lhGfx_LockBuffer") {
    return CanEmulateFableResourceLock(symbol, args, base);
  }
  if (symbol == "sub_821FA070" || IsFableResourceQueryHook(symbol)) {
    return CanEmulateFableResourceQuery(symbol, args, base);
  }
  if (symbol == "sub_82B98B28") {
    return CanEmulateFableTextureCreate(symbol, args, base);
  }
  if (IsFableResourceUpdateReplacementHook(symbol) ||
      IsFableResourceHeaderBuildHook(symbol) ||
      IsFableTextureHeaderUtilityHook(symbol) ||
      IsFableGetRenderTargetHook(symbol) ||
      IsFableSurfaceSizeHook(symbol) ||
      IsFableRenderTargetSetupHook(symbol) ||
      IsFableBinkVideoPlaneSurfaceSetupHook(symbol) ||
      IsFableCpuResourceHelperHook(symbol)) {
    return RendererReplaceResourcesEnabled() && base != nullptr;
  }
  if (IsFableResourceLifetimeHook(symbol)) {
    return RendererReplaceResourcesEnabled() && base != nullptr &&
           IsLikelyFableObjectAddress(args.r3);
  }
  if (IsFableBufferCreateHook(symbol)) {
    return CanEmulateFableBufferCreate(symbol, args, base);
  }
  if (IsFableVertexDeclarationCreateHook(symbol)) {
    return CanEmulateFableVertexDeclarationCreate(symbol, args, base);
  }
  if (IsFableShaderCreateHook(symbol)) {
    return CanEmulateFableShaderCreate(symbol, args, base);
  }
  if (IsFableVertexShaderRegisterHook(symbol)) {
    return CanEmulateFableVertexShaderRegister(symbol, args, base);
  }
  if (IsFableShaderUtilityHook(symbol)) {
    return CanEmulateFableShaderUtility(symbol, args, base);
  }
  if (IsFableSurfaceCreateHook(symbol)) {
    return CanEmulateFableSurfaceCreate(symbol, args, base);
  }
  if (IsFableSurfaceWrapperCreateHook(symbol)) {
    return CanEmulateFableSurfaceWrapperCreate(symbol, args, base);
  }
  if (IsFableRenderDepthSurfaceCtorHook(symbol)) {
    return CanEmulateFableRenderDepthSurfaceCtor(symbol, args, base);
  }
  if (IsFableBinkVideoPlaneSetupHook(symbol)) {
    return CanEmulateFableBinkVideoPlaneSetup(symbol, args, base);
  }
  if (IsFableSurfaceViewCreateHook(symbol)) {
    return CanEmulateFableSurfaceViewCreate(symbol, args, base);
  }
  if (IsFableSurfaceCopyHook(symbol)) {
    return CanEmulateFableSurfaceCopy(symbol, args, base);
  }
  if (IsFableSurfaceMemoryCopyHook(symbol)) {
    return CanEmulateFableSurfaceMemoryCopy(symbol, args, base);
  }
  if (IsFableTextureFillHook(symbol)) {
    return CanEmulateFableTextureFill(symbol, args, base);
  }
  if (IsFableTextureFileCreateHook(symbol)) {
    return CanEmulateFableTextureFileCreate(symbol, args, base);
  }
  if (IsFableTextureAddressOffsetHook(symbol)) {
    return CanEmulateFableTextureAddressOffset(symbol, args, base);
  }
  if (IsFableTextureHeaderBuildHook(symbol)) {
    return CanEmulateFableTextureHeaderBuild(symbol, base);
  }
  return false;
}

bool ShouldLogFableNativeFallbackCount(uint64_t count) {
  return count <= 8 || (count & (count - 1u)) == 0;
}

void LogFableNativeFallbackShape(const char *symbol,
                                 const RendererHookArgs &args,
                                 uint8_t *base) {
  const char *category = FableNativeFallbackCategory(symbol);
  uint64_t count = 0;
  {
    std::lock_guard lock(g_fable_native_fallback_shape_mutex);
    const std::string key = std::string(category) + ":" + symbol;
    count = ++g_fable_native_fallback_shape_counts[key];
  }
  if (!ShouldLogFableNativeFallbackCount(count)) {
    return;
  }

  REXLOG_WARN(
      "Refii renderer resource replacement suppressed unsupported Fable "
      "adapter shape: count={} "
      "category={} symbol={} r3=0x{:08X} r4=0x{:08X} r5=0x{:08X} "
      "r6=0x{:08X} r7=0x{:08X} r8=0x{:08X} r9=0x{:08X} r10=0x{:08X} "
      "base={} lr=0x{:08X}",
      count, category, FableHookLogName(symbol), args.r3, args.r4, args.r5,
      args.r6, args.r7, args.r8, args.r9, args.r10, base != nullptr,
      args.lr);
}

void TraceHookDecision(const char *symbol, RendererHookKind kind) {
  if (!ShouldTraceHook(kind)) {
    return;
  }

  const bool run_native = ShouldRunNativeRendererHook(symbol, kind);
  const std::string log_key = std::string(symbol) + ":" +
                              RendererBackendName(GetRendererBackend()) + ":" +
                              (run_native ? "guest-run" : "suppressed");
  if (!g_logged_hook_decisions.insert(log_key).second) {
    return;
  }

  const RendererBackend backend = GetRendererBackend();
  const std::string hook_name = FableHookLogName(symbol);
  const bool exact_unleashed = IsFableUnleashedExactReplacementHook(symbol);
  const char *exact_role =
      exact_unleashed ? FableUnleashedExactReplacementRole(symbol) : nullptr;
  const char *exact_source =
      exact_unleashed ? FableUnleashedExactReplacementSource(symbol) : nullptr;
  if (backend == RendererBackend::kPlume) {
    if (run_native) {
      if (exact_unleashed) {
        REXLOG_INFO(
            "Refii renderer exact Unleashed hook {} ({}, from {}) observed "
            "for plume/{}; {}",
            hook_name, exact_role ? exact_role : "unknown",
            exact_source ? exact_source : "unknown",
            RendererGraphicsApiName(GetRendererGraphicsApi()),
            PlumeGuestPathDescription(kind));
      } else {
        REXLOG_INFO("Refii renderer hook {} observed for plume/{}; {}",
                    hook_name,
                    RendererGraphicsApiName(GetRendererGraphicsApi()),
                    PlumeGuestPathDescription(kind));
      }
    } else {
      if (exact_unleashed) {
        REXLOG_INFO(
            "Refii renderer exact Unleashed hook {} ({}, from {}) observed "
            "for plume/{}; native renderer work will be suppressed",
            hook_name, exact_role ? exact_role : "unknown",
            exact_source ? exact_source : "unknown",
            RendererGraphicsApiName(GetRendererGraphicsApi()));
      } else {
        REXLOG_INFO(
            "Refii renderer hook {} observed for plume/{}; native renderer "
            "work will be suppressed",
            hook_name, RendererGraphicsApiName(GetRendererGraphicsApi()));
      }
    }
    return;
  }

  REXLOG_INFO("Refii renderer hook {} observed for {}; native backend active",
              hook_name, RendererBackendName(backend));
}

void MaybeLogRendererBackendState() {
  if (!RendererHooksEnabled() ||
      GetRendererBackend() != RendererBackend::kPlume) {
    return;
  }

  if (!PlumeAvailable()) {
    if (!g_logged_plume_unavailable) {
      REXLOG_WARN(
          "Refii renderer backend is set to plume/{}, but Plume is not linked "
          "into this build. Select renderer_backend=xenia to use the original "
          "Fable 2 renderer.",
          RendererGraphicsApiName(GetRendererGraphicsApi()));
      g_logged_plume_unavailable = true;
    }
    return;
  }

  if (!g_logged_plume_observer) {
    REXLOG_INFO(
        "Refii renderer backend is set to plume/{}. Plume owns presentation "
        "and suppresses mirrored Fable command/state/draw submission. "
        "Resource hooks feed Plume-owned guest resources; guest-visible setup, "
        "allocation, upload, and descriptor side effects are emulated through "
        "the Fable adapter layer rather than native D3D rendering or "
        "presentation.",
        RendererGraphicsApiName(GetRendererGraphicsApi()));
    g_logged_plume_observer = true;
  }

  EnsurePlumePresentPumpStarted();
}

} // namespace

bool PlumeAvailable() { return REFII_RENDERER_HAS_PLUME != 0; }

void ShutdownRendererPresentPump() { StopPlumePresentPumpThread(); }

RendererHookTimingSnapshot GetRendererHookTimingSnapshot() {
  RendererHookTimingSnapshot snapshot;
  snapshot.hook_enters = g_hook_enter_count.load(std::memory_order_relaxed);
  snapshot.renderer_setup_hooks =
      g_renderer_setup_hook_count.load(std::memory_order_relaxed);
  snapshot.draw_hook_enters =
      g_draw_hook_enter_count.load(std::memory_order_relaxed);
  snapshot.display_present_hooks =
      g_display_present_hook_count.load(std::memory_order_relaxed);
  snapshot.plume_present_requests =
      g_plume_present_request_count.load(std::memory_order_relaxed);
  snapshot.plume_present_pump_requests =
      g_plume_present_pump_request_count.load(std::memory_order_relaxed);
  snapshot.plume_present_pump_fable_advances =
      g_plume_present_pump_fable_advance_count.load(std::memory_order_relaxed);
  snapshot.native_skipped_hooks =
      g_native_skipped_hook_count.load(std::memory_order_relaxed);
  snapshot.last_hook_gap_us =
      g_last_hook_gap_us.load(std::memory_order_relaxed);
  snapshot.last_draw_hook_gap_us =
      g_last_draw_hook_gap_us.load(std::memory_order_relaxed);
  snapshot.last_display_present_gap_us =
      g_last_display_present_gap_us.load(std::memory_order_relaxed);
  snapshot.last_plume_present_request_gap_us =
      g_last_plume_present_request_gap_us.load(std::memory_order_relaxed);
  snapshot.last_plume_present_pump_gap_us =
      g_last_plume_present_pump_gap_us.load(std::memory_order_relaxed);
  snapshot.fable_renderer =
      g_last_fable_renderer.load(std::memory_order_relaxed);
  snapshot.fable_renderer_owner =
      g_last_fable_renderer_owner.load(std::memory_order_relaxed);
  snapshot.fable_game = g_last_fable_game.load(std::memory_order_relaxed);
  snapshot.fable_device = g_last_fable_device.load(std::memory_order_relaxed);
  snapshot.fable_present_submitted =
      g_last_fable_present_submitted.load(std::memory_order_relaxed);
  snapshot.fable_present_completed =
      g_last_fable_present_completed.load(std::memory_order_relaxed);
  snapshot.fable_swap_callbacks =
      g_last_fable_swap_callbacks.load(std::memory_order_relaxed);
  snapshot.fable_present_pending =
      g_last_fable_present_pending.load(std::memory_order_relaxed);
  snapshot.plume_present_pump_target_fps = RendererPlumePresentTargetFps();
  snapshot.seen_display_swap_hook =
      g_seen_plume_display_swap_hook.load(std::memory_order_relaxed);
  snapshot.plume_present_pump_enabled = RendererPlumePresentPumpEnabled();

  std::lock_guard lock(g_hook_timing_mutex);
  CopyTimingText(snapshot.last_renderer_setup_symbol,
                 sizeof(snapshot.last_renderer_setup_symbol),
                 g_last_renderer_setup_symbol);
  CopyTimingText(snapshot.last_hook_symbol, sizeof(snapshot.last_hook_symbol),
                 g_last_hook_symbol);
  CopyTimingText(snapshot.last_display_present_symbol,
                 sizeof(snapshot.last_display_present_symbol),
                 g_last_display_present_symbol);
  CopyTimingText(snapshot.last_plume_present_symbol,
                 sizeof(snapshot.last_plume_present_symbol),
                 g_last_plume_present_symbol);
  CopyTimingText(snapshot.last_plume_present_reason,
                 sizeof(snapshot.last_plume_present_reason),
                 g_last_plume_present_reason);
  CopyTimingText(snapshot.last_native_skipped_symbol,
                 sizeof(snapshot.last_native_skipped_symbol),
                 g_last_native_skipped_symbol);
  return snapshot;
}

RendererHookArgs CaptureRendererHookArgs(const PPCContext &ctx) {
  return {
      .r3 = ctx.r3.u32,
      .r4 = ctx.r4.u32,
      .r5 = ctx.r5.u32,
      .r6 = ctx.r6.u32,
      .r7 = ctx.r7.u32,
      .r8 = ctx.r8.u32,
      .r9 = ctx.r9.u32,
      .r10 = ctx.r10.u32,
      .r14 = ctx.r14.u32,
      .r15 = ctx.r15.u32,
      .r16 = ctx.r16.u32,
      .r17 = ctx.r17.u32,
      .r18 = ctx.r18.u32,
      .r19 = ctx.r19.u32,
      .r20 = ctx.r20.u32,
      .r21 = ctx.r21.u32,
      .r22 = ctx.r22.u32,
      .r23 = ctx.r23.u32,
      .r24 = ctx.r24.u32,
      .r25 = ctx.r25.u32,
      .r26 = ctx.r26.u32,
      .r27 = ctx.r27.u32,
      .r28 = ctx.r28.u32,
      .r29 = ctx.r29.u32,
      .r30 = ctx.r30.u32,
      .r31 = ctx.r31.u32,
      .r1 = ctx.r1.u32,
      .lr = static_cast<uint32_t>(ctx.lr),
      .r3_u64 = ctx.r3.u64,
      .r4_u64 = ctx.r4.u64,
      .r5_u64 = ctx.r5.u64,
      .r6_u64 = ctx.r6.u64,
      .f1 = ctx.f1.f64,
      .f2 = ctx.f2.f64,
      .f3 = ctx.f3.f64,
      .f4 = ctx.f4.f64,
      .f5 = ctx.f5.f64,
      .f6 = ctx.f6.f64,
  };
}

void OnRendererSetupHook(const RendererHookArgs &args, uint8_t *base) {
  ZoneScopedN("RefiiRenderer/RendererSetupHook");
  if (!RendererHooksEnabled()) {
    return;
  }

  RememberFableMemoryBase(base);
  g_renderer_setup_hook_count.fetch_add(1, std::memory_order_relaxed);
  g_last_fable_renderer.store(args.r3, std::memory_order_relaxed);
  g_last_fable_renderer_owner.store(args.r4, std::memory_order_relaxed);
  g_last_fable_game.store(args.r5, std::memory_order_relaxed);
  SetLastTimingText(g_last_renderer_setup_symbol,
                    sizeof(g_last_renderer_setup_symbol),
                    "rex_lhRenderer_Setup");

  MaybeLogRendererBackendState();
  if (PlumeRendererBackendEnabled()) {
    ResetFableDirectState();
  }

  if (!g_logged_fable_renderer_setup_bridge) {
    REXLOG_INFO(
        "Refii renderer direct lifecycle bridge observed Fable renderer setup "
        "renderer=0x{:08X} owner=0x{:08X} game=0x{:08X}",
        args.r3,
        args.r4,
        args.r5);
    g_logged_fable_renderer_setup_bridge = true;
  }
}

void OnRendererGuestHookEnter(const char *symbol, RendererHookKind kind,
                              const RendererHookArgs &args, uint8_t *base) {
  ZoneNamedN(tracy_hook_zone, "RefiiRenderer/GuestHookEnter", true);
  ZoneTextV(tracy_hook_zone, symbol, std::strlen(symbol));
  if (!RendererHooksEnabled()) {
    return;
  }
  RememberFableMemoryBase(base);

  ConfigureRendererTracyPlots();
  const int64_t now_ns = RendererTraceNowNs();
  g_hook_enter_count.fetch_add(1, std::memory_order_relaxed);
  g_last_hook_gap_us.store(PlotDeltaUs("Refii Renderer Hook Gap Us",
                                       g_last_renderer_hook_enter_ns, now_ns),
                           std::memory_order_relaxed);
  SetLastTimingText(g_last_hook_symbol, sizeof(g_last_hook_symbol), symbol);
  TracyPlot("Refii Renderer Hook Kind", static_cast<int64_t>(kind));
  if (kind == RendererHookKind::kDraw) {
    g_draw_hook_enter_count.fetch_add(1, std::memory_order_relaxed);
    g_last_draw_hook_gap_us.store(PlotDeltaUs("Refii Renderer Draw Hook Gap Us",
                                              g_last_draw_hook_enter_ns,
                                              now_ns),
                                  std::memory_order_relaxed);
  }

  MaybeLogRendererBackendState();
  PollRenderDocCaptureRequest();
  if (PlumeRendererBackendEnabled()) {
    NotifyPlumeBackendHook(symbol, kind);
  }
  ObserveRendererHookEnter(symbol, kind, args, base);
  TraceHookDecision(symbol, kind);
}

void OnRendererStateDispatchHook(const RendererHookArgs &args, uint8_t *base) {
  ZoneScopedN("RefiiRenderer/StateDispatchHook");
  if (!RendererHooksEnabled()) {
    return;
  }

  MaybeLogRendererBackendState();
  PollRenderDocCaptureRequest();
  if (PlumeRendererBackendEnabled()) {
    NotifyPlumeBackendHook("sub_8221E550", RendererHookKind::kDraw);
  }
  ObserveRendererStateDispatch(args, base);
  TraceHookDecision("sub_8221E550", RendererHookKind::kDraw);
}

void OnRendererPassQueueHook(const RendererHookArgs &args, uint8_t *base) {
  ZoneScopedN("RefiiRenderer/PassQueueHook");
  OnRendererGuestHookEnter("sub_82B668D0", RendererHookKind::kDraw, args);
  if (!RendererHooksEnabled()) {
    return;
  }

  ObserveRendererPassQueue(args, base);
}

void OnRendererPassQueueSnapshot(const RendererHookArgs &args, uint8_t *base) {
  ZoneScopedN("RefiiRenderer/PassQueueSnapshot");
  if (!RendererHooksEnabled()) {
    return;
  }

  ObserveRendererPassQueue(args, base);
}

void RepairFableRendererGetTimeDeltaResult(const RendererHookArgs& args,
                                           PPCContext& ctx, uint8_t* base) {
  const double native_time = ctx.f1.f64;
  if (std::isfinite(native_time) && native_time > 0.0) {
    return;
  }

  static const RendererTraceClock::time_point start_time =
      RendererTraceClock::now();
  const RendererTraceClock::time_point now = RendererTraceClock::now();
  const double elapsed_seconds =
      std::chrono::duration<double>(now - start_time).count();
  const double repaired_time = std::max(1.0 + elapsed_seconds, 1.0 / 60.0);
  ctx.f1.f64 = repaired_time;

  if (!g_logged_fable_time_delta_repair) {
    REXLOG_WARN(
        "Refii renderer repaired Fable GetTimeDelta result native={:.9f} "
        "repaired={:.9f} lr=0x{:08X} base={}",
        native_time, repaired_time, args.lr, base != nullptr);
    g_logged_fable_time_delta_repair = true;
  }
}

void OnRendererGuestHookExit(const char *symbol, RendererHookKind kind,
                             const RendererHookArgs &args,
                             const PPCContext &ctx, uint8_t *base) {
  ZoneNamedN(tracy_hook_exit_zone, "RefiiRenderer/GuestHookExit", true);
  ZoneTextV(tracy_hook_exit_zone, symbol, std::strlen(symbol));
  if (!RendererHooksEnabled()) {
    return;
  }

  const RendererHookArgs exit_args = CaptureRendererHookArgs(ctx);
  ObserveRendererHookExit(symbol, kind, args, ctx.r3.u32, base, &exit_args);
  if (PlumeRendererBackendEnabled() && kind == RendererHookKind::kDraw &&
      std::string_view(symbol) == "sub_82BA34D8") {
    CompleteNativeFableDisplaySwapBookkeeping(args, base);
    const int64_t now_ns = RendererTraceNowNs();
    ConfigureRendererTracyPlots();
    g_display_present_hook_count.fetch_add(1, std::memory_order_relaxed);
    g_last_display_present_gap_us.store(
        PlotDeltaUs("Refii Fable Present Hook Gap Us",
                    g_last_display_present_hook_ns, now_ns),
        std::memory_order_relaxed);
    SetLastTimingText(g_last_display_present_symbol,
                      sizeof(g_last_display_present_symbol), symbol);
    g_seen_plume_display_swap_hook.store(true, std::memory_order_relaxed);
  }
  if (PlumeRendererBackendEnabled() && kind == RendererHookKind::kDraw) {
    const std::string_view symbol_view(symbol);
	    if (symbol_view == "sub_821BA0F0" ||
		        symbol_view == "sub_821C3C60" || symbol_view == "sub_8226E180" ||
		        symbol_view == "sub_8226E738" || symbol_view == "sub_8223CFA0" ||
		        symbol_view == "sub_822A8F68") {
	      CaptureFablePlumeCommandBufferBoundary(symbol_view);
	    }
  }
}

bool ShouldRunNativeRendererHook(const char *symbol, RendererHookKind kind) {
  ZoneScopedN("RefiiRenderer/ShouldRunNativeHook");
  if (GetRendererBackend() != RendererBackend::kPlume) {
    return true;
  }
  if (std::string_view(symbol) == "rex_lhRenderer_EndScene") {
    return !RendererReplacePresentEnabled();
  }

  if (kind != RendererHookKind::kDraw) {
    return !IsNativeRenderWorkHook(symbol);
  }

  return !IsNativeRenderWorkHook(symbol);
}

bool ShouldRunNativeRendererHook(const char *symbol, RendererHookKind kind,
                                 const RendererHookArgs &args, uint8_t *base) {
  ZoneScopedN("RefiiRenderer/ShouldRunNativeHookWithArgs");
  if (GetRendererBackend() != RendererBackend::kPlume) {
    return true;
  }
  if (std::string_view(symbol) == "rex_lhRenderer_EndScene") {
    return !RendererReplacePresentEnabled();
  }

  if (kind == RendererHookKind::kResource &&
      CanEmulateFableDirectResourceHook(symbol, args, base)) {
    return false;
  }
  if (kind == RendererHookKind::kResource &&
      CanEmulateFableVertexShaderRegister(symbol, args, base)) {
    return false;
  }
  if (kind == RendererHookKind::kGeneral &&
      CanEmulateFableShaderPatchHelper(symbol, args, base)) {
    return false;
  }
  if (kind == RendererHookKind::kResource &&
      CanEmulateFableShaderUtility(symbol, args, base)) {
    return false;
  }
  if (kind == RendererHookKind::kResource &&
      CanEmulateFableRenderDepthSurfaceCtor(symbol, args, base)) {
    return false;
  }
  if (kind == RendererHookKind::kResource &&
      CanEmulateFableRenderTargetSetup(symbol, base)) {
    return false;
  }
  if (kind == RendererHookKind::kResource &&
      RendererReplaceResourcesEnabled() &&
      IsFableRenderDepthSurfaceCtorHook(symbol) &&
      !g_logged_fable_render_depth_surface_ctor_shape) {
    const bool shape_mem =
        base != nullptr && IsLikelyFableReadableRange(args.r4, 8u);
    const bool format_mem =
        base != nullptr && IsLikelyFableWritableDataRange(args.r5, 4u);
    REXLOG_WARN(
        "Refii renderer suppressed unsupported Fable render/depth surface ctor "
        "replacement shape: symbol={} object=0x{:08X} "
        "shape=0x{:08X} format_shape=0x{:08X} base={} object_mem={} "
        "shape_mem={} format_mem={} width={} height={} d3d_format=0x{:08X}",
        FableHookLogName(symbol), args.r3, args.r4, args.r5, base != nullptr,
        IsLikelyFableObjectRange(args.r3, 32u), shape_mem, format_mem,
        shape_mem ? LoadGuestU32(base, args.r4 + 0u) : 0u,
        shape_mem ? LoadGuestU32(base, args.r4 + 4u) : 0u,
        format_mem ? FableSurfaceWrapperD3DFormat(base, args.r5) : 0u);
    g_logged_fable_render_depth_surface_ctor_shape = true;
  }
  if (kind == RendererHookKind::kResource &&
      CanEmulateFableBinkVideoPlaneSetup(symbol, args, base)) {
    return false;
  }
  if (kind == RendererHookKind::kResource &&
      CanEmulateFableBinkVideoPlaneSurfaceSetup(symbol, base)) {
    return false;
  }
  if (kind == RendererHookKind::kResource &&
      RendererReplaceResourcesEnabled() &&
      IsFableBinkVideoPlaneSetupHook(symbol) &&
      !g_logged_fable_bink_video_plane_shape) {
    REXLOG_WARN(
        "Refii renderer suppressed unsupported Fable Bink video plane setup "
        "replacement shape: symbol={} movie=0x{:08X} "
        "size={}x{} plane=0x{:08X} output_slot=0x{:08X} base={} "
        "plane_mem={} output_mem={}",
        FableHookLogName(symbol), args.r3, args.r4, args.r5, args.r6, args.r7,
        base != nullptr, IsLikelyFableWritableDataRange(args.r6 + 4u, 8u),
        IsLikelyFableWritableDataRange(args.r7, 4u));
    g_logged_fable_bink_video_plane_shape = true;
  }
  if (kind == RendererHookKind::kResource &&
      CanEmulateFableSurfaceWrapperCreate(symbol, args, base)) {
    return false;
  }
  if (kind == RendererHookKind::kResource &&
      RendererReplaceResourcesEnabled() &&
      IsFableSurfaceWrapperCreateHook(symbol) &&
      !g_logged_fable_surface_wrapper_shape) {
    REXLOG_WARN(
        "Refii renderer suppressed unsupported Fable surface wrapper create "
        "replacement shape: symbol={} wrapper=0x{:08X} "
        "shape=0x{:08X} format_shape=0x{:08X} msaa=0x{:08X} "
        "tile_or_flags=0x{:08X} base={} wrapper_mem={} shape_mem={} "
        "format_mem={} d3d_format=0x{:08X}",
        FableHookLogName(symbol), args.r3, args.r4, args.r5, args.r6, args.r7,
        base != nullptr, IsLikelyFableWritableDataRange(args.r3 + 4u, 8u),
        IsLikelyFableReadableRange(args.r4, 8u),
        IsLikelyFableWritableDataRange(args.r5, 4u),
        FableSurfaceWrapperD3DFormat(base, args.r5));
    g_logged_fable_surface_wrapper_shape = true;
  }
  if (kind == RendererHookKind::kResource &&
      CanEmulateFableSurfaceViewCreate(symbol, args, base)) {
    return false;
  }
  if (RendererReplaceResourcesEnabled() &&
      ((kind == RendererHookKind::kResource &&
        IsMandatoryFableResourceReplacementHook(symbol)) ||
       (kind == RendererHookKind::kGeneral &&
        IsFableShaderPatchHelperHook(symbol)))) {
    const bool shape_supported =
        kind == RendererHookKind::kGeneral
            ? CanEmulateFableShaderPatchHelper(symbol, args, base)
            : CanEmulateMandatoryFableResourceReplacementHook(symbol, args,
                                                              base);
    if (!shape_supported) {
      LogFableNativeFallbackShape(symbol, args, base);
    }
    return false;
  }

  const bool run_native = ShouldRunNativeRendererHook(symbol, kind);
  if (run_native && RendererReplaceResourcesEnabled() &&
      (kind == RendererHookKind::kResource ||
       (kind == RendererHookKind::kGeneral &&
        IsFableShaderPatchHelperHook(symbol)))) {
    LogFableNativeFallbackShape(symbol, args, base);
  }

  return run_native;
}

void OnRendererGuestHookNativeSkipped(const char *symbol, RendererHookKind kind,
                                      const RendererHookArgs &args,
                                      PPCContext &ctx, uint8_t *base) {
  ZoneNamedN(tracy_skip_zone, "RefiiRenderer/NativeHookSkipped", true);
  ZoneTextV(tracy_skip_zone, symbol, std::strlen(symbol));
  g_native_skipped_hook_count.fetch_add(1, std::memory_order_relaxed);
  SetLastTimingText(g_last_native_skipped_symbol,
                    sizeof(g_last_native_skipped_symbol), symbol);
  const uint32_t previous_result = ctx.r3.u32;
  const FableNativeCommandSubmitEmulation command_submit_emulation =
      EmulateFableNativeCommandSubmission(symbol, args, base);
  const FableNativeResourceEmulation resource_emulation =
      (kind == RendererHookKind::kResource ||
       (kind == RendererHookKind::kGeneral &&
        IsFableShaderPatchHelperHook(symbol)))
          ? EmulateFableNativeResourceHook(symbol, args, ctx, base)
          : FableNativeResourceEmulation{};
  const bool exact_unleashed_stub =
      FableUnleashedExactHookKindForSymbol(symbol) ==
      FableUnleashedExactHookKind::kStub;
  const uint32_t observer_result =
      command_submit_emulation.handled
          ? static_cast<uint32_t>(command_submit_emulation.result)
          : resource_emulation.handled
          ? static_cast<uint32_t>(resource_emulation.result)
          : previous_result;
  if (kind == RendererHookKind::kDraw ||
      (kind == RendererHookKind::kResource && resource_emulation.handled) ||
      (kind == RendererHookKind::kGeneral && resource_emulation.handled &&
       IsFableShaderPatchHelperHook(symbol)) ||
      (kind == RendererHookKind::kGeneral && command_submit_emulation.handled &&
       IsFableGetRenderTargetHook(symbol)) ||
      (kind == RendererHookKind::kGeneral &&
       IsFableNativeShaderConstantReplacementHook(symbol))) {
    ObserveRendererHookExit(symbol, kind, args, observer_result, base);
  }
  if (command_submit_emulation.handled) {
    CaptureFablePlumeCommandBufferBoundary(symbol);
  }

  if (command_submit_emulation.handled) {
    ctx.r3.u64 = command_submit_emulation.result;
  } else if (resource_emulation.handled) {
    ctx.r3.u64 = resource_emulation.result;
  } else if (exact_unleashed_stub) {
    ctx.r3.u64 = previous_result;
  } else {
    ctx.r3.u64 = 0;
  }

  if (RendererHookTracingEnabled() &&
      g_logged_hook_suppressed.insert(symbol).second) {
    REXLOG_INFO("Refii renderer hook {} suppressed native Fable 2 path",
                FableHookLogName(symbol));
  }

  if (kind == RendererHookKind::kDraw) {
    const std::string_view symbol_view(symbol);
    const bool is_end_scene_present =
        symbol_view == "rex_lhRenderer_EndScene";
    const bool is_display_swap = symbol_view == "sub_82BA34D8";
    const bool is_complex_swap = symbol_view == "sub_82BA32F8";
    const bool is_native_present = is_display_swap || is_complex_swap;
    const bool is_present_boundary = is_end_scene_present || is_display_swap;
    if (is_end_scene_present || is_native_present) {
      const int64_t now_ns = RendererTraceNowNs();
      ConfigureRendererTracyPlots();
      if (is_display_swap && !command_submit_emulation.handled) {
        EmulateSuppressedFableDisplaySwapBookkeeping(symbol_view, args, base);
      }
      g_display_present_hook_count.fetch_add(1, std::memory_order_relaxed);
      if (is_display_swap) {
        g_last_display_present_gap_us.store(
            PlotDeltaUs("Refii Fable Present Hook Gap Us",
                        g_last_display_present_hook_ns, now_ns),
            std::memory_order_relaxed);
      }
      SetLastTimingText(g_last_display_present_symbol,
                        sizeof(g_last_display_present_symbol), symbol);
    }
    if (!is_present_boundary) {
      return;
    }

    if (is_display_swap) {
      g_seen_plume_display_swap_hook.store(true, std::memory_order_relaxed);
    }
    if (RendererPlumePresentPumpEnabled()) {
      return;
    }
    const char *present_reason =
        is_end_scene_present ? "end-scene" : "display-swap";
    const bool presented = PresentPlumeFrameFromRendererHook(symbol,
                                                             present_reason);
    if (presented &&
        ((is_display_swap && !g_logged_plume_present_driver) ||
         (is_end_scene_present && !g_logged_plume_end_scene_present_driver))) {
      if (is_display_swap) {
        REXLOG_INFO(
            "Refii plume output is presenting from suppressed Fable display "
            "swap hook {}",
            symbol);
        g_logged_plume_present_driver = true;
      } else if (is_end_scene_present &&
                 !g_logged_plume_end_scene_present_driver) {
        REXLOG_INFO(
            "Refii plume output is presenting from suppressed Fable EndScene "
            "hook {}",
            symbol);
        g_logged_plume_end_scene_present_driver = true;
      }
    } else if (!presented && !g_logged_plume_present_failed) {
      REXLOG_WARN("Refii plume output could not present from Fable {} hook {}",
                  present_reason, symbol);
      g_logged_plume_present_failed = true;
    }
  }
}

void OnRendererGuestHook(const char *symbol, RendererHookKind kind) {
  OnRendererGuestHookEnter(symbol, kind, RendererHookArgs{});
}

} // namespace refii::renderer
