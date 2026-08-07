#pragma once

#include "renderer_runtime.h"
#include <rex/cvar.h>

#include <atomic>
#include <condition_variable>
#include <cstdint>
#include <mutex>
#include <string>
#include <string_view>
#include <unordered_map>
#include <unordered_set>
#include <vector>

namespace refii::renderer {

struct FableGuestCommandSubmitEmulation {
  bool handled = false;
  uint64_t result = 0;
};

struct FableGuestResourceEmulation {
  bool handled = false;
  uint64_t result = 0;
};

struct FableVertexDeclarationElementCount {
  bool valid = false;
  uint32_t count = 0;
};

bool IsLikelyGuestDwordAddress(uint32_t guest_address);
bool IsLikelyGuestMemoryAddress(uint32_t guest_address);

extern std::atomic<uint64_t> g_capture_ack_captured_boundary;
extern std::condition_variable g_capture_ack_cv;
extern std::atomic<uint64_t> g_capture_ack_gate_checks;
extern std::atomic<uint64_t> g_capture_ack_gate_disarms;
extern std::atomic<uint64_t> g_capture_ack_gate_last_outstanding;
extern std::atomic<uint64_t> g_capture_ack_gate_max_outstanding;
extern std::atomic<uint64_t> g_capture_ack_gate_timeouts;
extern std::atomic<uint64_t> g_capture_ack_gate_wait_us;
extern std::atomic<uint64_t> g_capture_ack_gate_waits;
extern std::mutex g_capture_ack_mutex;
extern std::atomic<uint64_t> g_capture_ack_publishes;
extern std::atomic<uint64_t> g_fable_bink_video_plane_setup_count;
extern std::atomic<uint64_t> g_fable_plume_command_buffer_submit_count;
extern std::atomic<uint32_t> g_fable_plume_gpu_scratch_next;
extern std::atomic<uint64_t> g_fable_scene_stream_handoff_repair_count;
extern std::atomic<uint64_t> g_guest_present_signal;
extern std::atomic<uint32_t> g_last_fable_device;
extern std::atomic<uintptr_t> g_last_fable_memory_base;
extern bool g_logged_fable_begin_indexed_vertices_emulation;
extern bool g_logged_fable_begin_vertices_emulation;
extern bool g_logged_fable_bink_video_plane_emulation;
extern bool g_logged_fable_buffer_create_emulation;
extern bool g_logged_fable_buffer_create_shape;
extern bool g_logged_fable_command_submit_emulation;
extern bool g_logged_fable_complex_swap_emulation;
extern uint32_t g_logged_fable_cpu_layout_blank_fast_path_rejects;
extern uint32_t g_logged_fable_cpu_layout_source_alias_blanks;
extern uint32_t g_logged_fable_cpu_layout_source_alias_remaps;
extern bool g_logged_fable_cpu_resource_helper_emulation;
extern bool g_logged_fable_cpu_texture_layout_fast_emulation;
extern bool g_logged_fable_cpu_texture_layout_fast_tiling;
extern bool g_logged_fable_deferred_list_space_emulation;
extern bool g_logged_fable_display_interval_emulation;
extern bool g_logged_fable_draw_vertices_up_emulation;
extern std::atomic<uint32_t> g_logged_fable_end_export_emulation;
extern bool g_logged_fable_get_render_target_emulation;
extern bool g_logged_fable_kickoff_emulation;
extern bool g_logged_fable_kickoff_segment_emulation;
extern bool g_logged_fable_pix_capture_emulation;
extern bool g_logged_fable_plume_command_buffer_capture;
extern bool g_logged_fable_primary_list_space_emulation;
extern bool g_logged_fable_query_buffer_space_emulation;
extern bool g_logged_fable_render_depth_surface_ctor_emulation;
extern bool g_logged_fable_render_target_setup_emulation;
extern bool g_logged_fable_resource_header_emulation;
extern bool g_logged_fable_resource_lifetime_emulation;
extern bool g_logged_fable_resource_lifetime_shape;
extern bool g_logged_fable_resource_lock_emulation;
extern bool g_logged_fable_resource_query_emulation;
extern bool g_logged_fable_resource_unlock_emulation;
extern bool g_logged_fable_scene_batch_toggle_emulation;
extern bool g_logged_fable_scene_stream_handoff_repair;
extern bool g_logged_fable_set_pending_shaders_emulation;
extern bool g_logged_fable_shader_create_emulation;
extern bool g_logged_fable_shader_patch_helper_emulation;
extern bool g_logged_fable_shader_utility_emulation;
extern bool g_logged_fable_surface_copy_emulation;
extern bool g_logged_fable_surface_copy_shape;
extern bool g_logged_fable_surface_create_emulation;
extern bool g_logged_fable_surface_memory_copy_emulation;
extern bool g_logged_fable_surface_memory_copy_linear_path;
extern bool g_logged_fable_surface_memory_copy_shape;
extern bool g_logged_fable_surface_size_emulation;
extern bool g_logged_fable_surface_view_emulation;
extern bool g_logged_fable_surface_wrapper_emulation;
extern bool g_logged_fable_texture_create_emulation;
extern bool g_logged_fable_texture_file_create_emulation;
extern bool g_logged_fable_texture_fill_emulation;
extern bool g_logged_fable_texture_header_emulation;
extern bool g_logged_fable_texture_header_utility_emulation;
extern bool g_logged_fable_texture_offset_emulation;
extern bool g_logged_fable_texture_surface_lock_emulation;
extern bool g_logged_fable_vertex_declaration_create_emulation;
extern bool g_logged_fable_vertex_shader_register_emulation;
extern std::atomic_bool g_plume_present_pump_shutdown_requested;
extern std::atomic<uint64_t> g_release_dispatch_hits;

void CopyGuestBytes(uint8_t *base, uint32_t destination, uint32_t source,
                    uint32_t byte_size);

uint32_t EmulateFableAllocateCommandSegment(uint32_t device, uint8_t *base,
                                            uint32_t requested_words);
FableGuestCommandSubmitEmulation ApplyFableScissorState(
    uint8_t *base, uint32_t device, int32_t left, int32_t top, int32_t right,
    int32_t bottom, bool store_requested_rect);

bool IsLikelyGuestDwordAddress(uint32_t guest_address);

FableGuestCommandSubmitEmulation EmulateFableKickOff(
    const RendererHookArgs &args, uint8_t *base);

FableGuestResourceEmulation EmulateFableResourceLifetime(
    std::string_view symbol, const RendererHookArgs &args, uint8_t *base);

FableGuestResourceEmulation EmulateFableSurfaceWrapperCreate(
    std::string_view symbol, const RendererHookArgs &args, PPCContext &ctx,
    uint8_t *base);

uint32_t FableGpuAddress(uint32_t guest_address);

bool FableRendererHookAllowedInCurrentMode(std::string_view symbol,
                                           RendererHookKind kind);

bool IsFableBinkVideoPlaneSetupHook(std::string_view symbol);

bool IsFableBinkVideoPlaneSurfaceSetupHook(std::string_view symbol);

bool IsFableBufferCreateHook(std::string_view symbol);

bool IsFableGetRenderTargetHook(std::string_view symbol);

bool IsFableGuestPlainRootDrawHook(std::string_view symbol);

bool IsFableGuestPrivateTextureRectangleHook(std::string_view symbol);

bool IsFableGuestShaderConstantReplacementHook(std::string_view symbol);

bool IsFableRenderDepthSurfaceCtorHook(std::string_view symbol);

bool IsFableShaderCreateHook(std::string_view symbol);

bool IsFableSurfaceCopyHook(std::string_view symbol);

bool IsFableSurfaceCreateHook(std::string_view symbol);

bool IsFableSurfaceMemoryCopyHook(std::string_view symbol);

bool IsFableSurfaceSizeHook(std::string_view symbol);

bool IsFableSurfaceWrapperCreateHook(std::string_view symbol);

bool IsFableTextureCreateHook(std::string_view symbol);

bool IsFableTextureHeaderUtilityHook(std::string_view symbol);

bool IsFableVertexDeclarationCreateHook(std::string_view symbol);

bool IsLikelyFableObjectRange(uint32_t guest_address, uint32_t byte_size);

uint32_t LoadHookStackU32(uint8_t *base, const RendererHookArgs &args,
                          uint32_t offset);

void OrFableGuestQword(uint8_t *base, uint32_t guest_address, uint64_t bits);

void OrFableGuestQwordLow(uint8_t *base, uint32_t guest_address,
                          uint32_t bits);

bool RendererStateDispatchObserverAllowed();

bool RepairFableSceneStreamPublishHandoff(std::string_view reason,
                                          uint8_t *base);


uint32_t AlignGuestU32(uint32_t value, uint32_t alignment);

uint32_t EmulateFableAllocateAuxiliaryCommandWords(uint32_t device,
                                                   uint8_t *base,
                                                   uint32_t word_count,
                                                   uint32_t alignment);

FableGuestCommandSubmitEmulation EmulateFableBlockUntilIdle(
    std::string_view symbol, const RendererHookArgs &args, uint8_t *base);

FableGuestCommandSubmitEmulation EmulateFableClearCommand(
    std::string_view symbol, const RendererHookArgs &args, uint8_t *base);

FableGuestCommandSubmitEmulation EmulateFableGetRenderTarget(
    const RendererHookArgs &args, uint8_t *base);

FableGuestCommandSubmitEmulation EmulateFableSetDepthStencilSurface(
    const RendererHookArgs &args, uint8_t *base);

FableGuestCommandSubmitEmulation EmulateFableSetRenderTarget(
    const RendererHookArgs &args, uint8_t *base);

FableGuestCommandSubmitEmulation EmulateFableSetScissorPointer(
    const RendererHookArgs &args, uint8_t *base);

FableGuestCommandSubmitEmulation EmulateFableSetTexture(
    const RendererHookArgs &args, uint8_t *base);

FableGuestCommandSubmitEmulation EmulateFableSetVertexDeclaration(
    const RendererHookArgs &args, uint8_t *base);

FableGuestCommandSubmitEmulation EmulateFableSetViewportPointer(
    const RendererHookArgs &args, uint8_t *base);

bool IsFableCpuResourceHelperHook(std::string_view symbol);

bool IsFableRenderTargetSetupHook(std::string_view symbol);

bool IsFableResourceQueryHook(std::string_view symbol);

bool IsFableShaderPatchHelperHook(std::string_view symbol);

bool IsFableShaderUtilityHook(std::string_view symbol);

bool IsFableSurfaceViewCreateHook(std::string_view symbol);

bool IsFableTextureAddressOffsetHook(std::string_view symbol);

bool IsFableTextureFileCreateHook(std::string_view symbol);

bool IsFableTextureFillHook(std::string_view symbol);

bool IsFableTextureHeaderBuildHook(std::string_view symbol);

bool IsFableTextureSurfaceLockHook(std::string_view symbol);

bool IsFableVertexShaderRegisterHook(std::string_view symbol);

bool MoveGuestBytes(uint8_t *base, uint32_t destination, uint32_t source,
                    uint32_t byte_size);


bool CanEmulateFableBinkVideoPlaneSetup(std::string_view symbol,
                                        const RendererHookArgs &args,
                                        uint8_t *base);

bool CanEmulateFableBufferCreate(std::string_view symbol,
                                 const RendererHookArgs &args,
                                 uint8_t *base);

bool CanEmulateFableRenderDepthSurfaceCtor(std::string_view symbol,
                                           const RendererHookArgs &args,
                                           uint8_t *base);

bool CanEmulateFableResourceLock(std::string_view symbol,
                                 const RendererHookArgs &args,
                                 uint8_t *base);

bool CanEmulateFableResourceQuery(std::string_view symbol,
                                  const RendererHookArgs &args,
                                  uint8_t *base);

bool CanEmulateFableShaderCreate(std::string_view symbol,
                                 const RendererHookArgs &args,
                                 uint8_t *base);

bool CanEmulateFableShaderUtility(std::string_view symbol,
                                  const RendererHookArgs &args,
                                  uint8_t *base);

bool CanEmulateFableSurfaceCopy(std::string_view symbol,
                                const RendererHookArgs &args, uint8_t *base);

bool CanEmulateFableSurfaceCreate(std::string_view symbol,
                                  const RendererHookArgs &args,
                                  uint8_t *base);

bool CanEmulateFableSurfaceMemoryCopy(std::string_view symbol,
                                      const RendererHookArgs &args,
                                      uint8_t *base);

bool CanEmulateFableSurfaceViewCreate(std::string_view symbol,
                                      const RendererHookArgs &args,
                                      uint8_t *base);

bool CanEmulateFableSurfaceWrapperCreate(std::string_view symbol,
                                         const RendererHookArgs &args,
                                         uint8_t *base);

bool CanEmulateFableTextureCreate(std::string_view symbol,
                                  const RendererHookArgs &args,
                                  uint8_t *base);

bool CanEmulateFableTextureFill(std::string_view symbol,
                                const RendererHookArgs &args, uint8_t *base);

bool CanEmulateFableTextureSurfaceLock(std::string_view symbol,
                                       const RendererHookArgs &args,
                                       uint8_t *base);

bool CanEmulateFableVertexDeclarationCreate(std::string_view symbol,
                                            const RendererHookArgs &args,
                                            uint8_t *base);

bool CanEmulateFableVertexShaderRegister(std::string_view symbol,
                                         const RendererHookArgs &args,
                                         uint8_t *base);

FableGuestCommandSubmitEmulation EmulateFableSetIndices(
    const RendererHookArgs &args, uint8_t *base);

FableGuestCommandSubmitEmulation EmulateFableSetPixelShader(
    const RendererHookArgs &args, uint8_t *base);

FableGuestCommandSubmitEmulation EmulateFableSetStreamSource(
    const RendererHookArgs &args, uint8_t *base);

FableGuestCommandSubmitEmulation EmulateFableSetVertexShader(
    const RendererHookArgs &args, uint8_t *base);

FableGuestCommandSubmitEmulation EmulateFableSurfaceSize(
    std::string_view symbol, const RendererHookArgs &args, uint8_t *base);

bool IsFableStartupVideoPumpActive();

bool IsLikelyFableReadableRange(uint32_t guest_address, uint32_t byte_size);

bool IsLikelyFableWritableDataDwordAddress(uint32_t guest_address);

bool SetGuestBytes(uint8_t *base, uint32_t destination, int value,
                   uint32_t byte_size);


bool CanEmulateFableBinkVideoPlaneSurfaceSetup(std::string_view symbol,
                                               uint8_t *base);

bool CanEmulateFableDirectResourceHook(std::string_view symbol,
                                       const RendererHookArgs &args,
                                       uint8_t *base);

bool CanEmulateFableRenderTargetSetup(std::string_view symbol, uint8_t *base);

bool CanEmulateFableShaderPatchHelper(std::string_view symbol,
                                      const RendererHookArgs &args,
                                      uint8_t *base);

bool CanEmulateFableTextureAddressOffset(std::string_view symbol,
                                         const RendererHookArgs &args,
                                         uint8_t *base);

bool CanEmulateFableTextureFileCreate(std::string_view symbol,
                                      const RendererHookArgs &args,
                                      uint8_t *base);

bool CanEmulateFableTextureHeaderBuild(std::string_view symbol,
                                       uint8_t *base);

void CaptureFablePlumeCommandBufferBoundary(std::string_view symbol);

FableGuestCommandSubmitEmulation EmulateFableBeginExport(
    const RendererHookArgs &args, uint8_t *base);

FableGuestCommandSubmitEmulation EmulateFableDeferredStateDispatchHook(
    const RendererHookArgs &args, uint8_t *base);

FableGuestCommandSubmitEmulation EmulateFableEndExport(
    const RendererHookArgs &args, uint8_t *base);

FableGuestCommandSubmitEmulation EmulateFableFlushPendingGpuState(
    const RendererHookArgs &args, uint8_t *base);

FableGuestCommandSubmitEmulation EmulateFableInvalidateCaches(
    const RendererHookArgs &args, uint8_t *base);

FableGuestCommandSubmitEmulation EmulateFablePendingStateDispatchHook(
    const RendererHookArgs &args, uint8_t *base);

FableGuestCommandSubmitEmulation EmulateFablePendingStateQueueHook(
    const RendererHookArgs &args, uint8_t *base);

FableGuestCommandSubmitEmulation EmulateFableSetPendingClipPlanes(
    const RendererHookArgs &args, uint8_t *base);

FableGuestCommandSubmitEmulation EmulateFableSetPendingFetchConstants(
    const RendererHookArgs &args, uint8_t *base);

FableGuestCommandSubmitEmulation EmulateFableSetPendingPredicated(
    const RendererHookArgs &args, uint8_t *base);

FableGuestCommandSubmitEmulation EmulateFableSetPendingRenderStates(
    const RendererHookArgs &args, uint8_t *base);

FableGuestCommandSubmitEmulation EmulateFableSetPendingShaders(
    const RendererHookArgs &args, uint8_t *base);

FableGuestCommandSubmitEmulation EmulateFableSetPendingSplit(
    const RendererHookArgs &args, uint8_t *base);

FableGuestCommandSubmitEmulation EmulateFableSetSurfaceClip(
    const RendererHookArgs &args, uint8_t *base);

FableGuestCommandSubmitEmulation EmulateFableSetViewportFloats(
    const RendererHookArgs &args, uint8_t *base);

uint32_t FableSurfaceWrapperD3DFormat(uint8_t *base, uint32_t format_shape);

bool IsFableDeepObserverDrawCommandBufferBoundary(std::string_view symbol);

bool IsFableGuestShaderConstantCommandHook(std::string_view symbol);

bool IsFableGuestShaderConstantStateHook(std::string_view symbol);


FableGuestCommandSubmitEmulation
EmulateFableAddCallsToPrimaryBuffer(const RendererHookArgs &args,
                                    uint8_t *base);

FableGuestCommandSubmitEmulation
EmulateFableAddCommandsToPrimaryBuffer(const RendererHookArgs &args,
                                       uint8_t *base);

FableGuestCommandSubmitEmulation
EmulateFableKickOffSegment(const RendererHookArgs &args, uint8_t *base);

FableGuestCommandSubmitEmulation
EmulateFableQueueIndirectBuffer(const RendererHookArgs &args, uint8_t *base);


FableGuestCommandSubmitEmulation EmulateFableGuestCommandSubmission(
    std::string_view symbol, const RendererHookArgs &args, uint8_t *base,
    const FableHookSymbolDesc *desc);

FableGuestResourceEmulation EmulateFableGuestResourceHook(
    std::string_view symbol, const RendererHookArgs &args, PPCContext &ctx,
    uint8_t *base, uint32_t symbol_address);

FableGuestCommandSubmitEmulation EmulateFableSetColorClear(
    const RendererHookArgs &args, uint8_t *base);

FableGuestCommandSubmitEmulation EmulateFableSetGammaRamp(
    std::string_view symbol, const RendererHookArgs &args, uint8_t *base);

FableGuestCommandSubmitEmulation EmulateFableSetRenderState(
    std::string_view symbol, const RendererHookArgs &args, uint8_t *base);

FableGuestCommandSubmitEmulation EmulateFableSetSamplerState(
    std::string_view symbol, const RendererHookArgs &args, uint8_t *base);

FableGuestCommandSubmitEmulation EmulateFableSetScalerParameters(
    const RendererHookArgs &args, uint8_t *base);

FableGuestCommandSubmitEmulation EmulateFableSetScreenExtentQueryMode(
    const RendererHookArgs &args, uint8_t *base);

FableGuestCommandSubmitEmulation EmulateFableSetShaderGPRAllocation(
    const RendererHookArgs &args, uint8_t *base);

FableGuestCommandSubmitEmulation EmulateFableSetupScaler(
    const RendererHookArgs &args, uint8_t *base);


bool IsLikelyFableWritableDataRange(uint32_t guest_address,
                                    uint32_t byte_size);

bool CanEmulateFableDirectResourceHook(std::string_view symbol,
                                       uint32_t symbol_address,
                                       const RendererHookArgs &args,
                                       uint8_t *base);

bool RendererPhysicalWrapperRequiredForRootPath(std::string_view symbol,
                                                RendererHookKind kind);
bool FableStartupVideoUpdateComplete(uint8_t *base, uint32_t update_guest);
bool FableStartupVideoRecentBinkUploadActive();
bool FableStartupVideoObservedBinkActive(uint8_t *base);

void EmulatePlumeOwnedFablePixCaptureControlBookkeeping(uint32_t device,
                                                       uint8_t *base);

REXCVAR_DECLARE(bool, renderer_capture_tied_ack);

}  // namespace refii::renderer

