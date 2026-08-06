#pragma once

#include <atomic>
#include <cstdint>

namespace refii::renderer {

enum class RendererHookConfidence {
  High,
  Tentative,
  Unresolved,
};

enum class RendererHookReplacement {
  FullReplacement,
  FableAdapter,
  SideEffectOnly,
  Unresolved,
};

enum class RendererHookSurface {
  RequiredRoot,
  MigrationSupport,
  DebugSupport,
  Unresolved,
};

enum class RendererHookId : uint32_t {
  kCreateDevice = 0,
  kDestructResource,
  kLockTextureRect,
  kUnlockTextureRect,
  kLockVertexBuffer,
  kUnlockVertexBuffer,
  kGetVertexBufferDesc,
  kLockIndexBuffer,
  kUnlockIndexBuffer,
  kGetIndexBufferDesc,
  kGetSurfaceDesc,
  kGetVertexDeclaration,
  kHashVertexDeclaration,
  kPresent,
  kDisplaySwap,
  kGetBackBuffer,
  kGetRenderTarget,
  kCreateTexture,
  kCreateVertexBuffer,
  kCreateIndexBuffer,
  kCreateSurface,
  kStretchRect,
  kSetTextureHeader,
  kPM4SetTextureFetchConstant,
  kFindSurfaceWithinTexture,
  kPrivateTextureRectangleWrapper,
  kPrivateTextureRectangleSetupFlushWrapper,
  kPrivateTextureRectangleSiblingWrapper,
  kPrivateTextureRectangleDraw,
  kSetRenderTarget,
  kSetDepthStencilSurface,
  kClear,
  kSetViewport,
  kSetTexture,
  kSetScissorRect,
  kSceneBatchRecordBuilder,
  kDrawPrimitive,
  kDrawIndexedPrimitive,
  kDrawPrimitiveUP,
  kDrawIndexedUserPrimitives,
  kCreateVertexDeclaration,
  kSetVertexDeclaration,
  kCreateVertexShader,
  kSetVertexShader,
  kSetStreamSource,
  kSetIndices,
  kCreatePixelShader,
  kSetPixelShader,
  kD3DXFillTexture,
  kD3DXFillVolumeTexture,
  kMakePictureData,
  kSetResolution,
  kScreenShaderInit,
  kMemExportLandscapeTessellationPass,
  kMemExportSkinnedMeshPass,
  kMemExportDecalBakePass,
  kEndExportPixRenderVideoFrameCaller,
  kEndExport,
  kInvalidateCaches,
  kSetVertexShaderConstantB,
  kSetPixelShaderConstantB,
  kEmitTessellatedIndexedPackets,
  kEmitTessellatedPrimitivePackets,
  kInitializePresentationParameters,
  kCount,
};

constexpr RendererHookReplacement DefaultRendererHookReplacement(
    RendererHookConfidence confidence) {
  switch (confidence) {
  case RendererHookConfidence::High:
    return RendererHookReplacement::FullReplacement;
  case RendererHookConfidence::Tentative:
    return RendererHookReplacement::FableAdapter;
  case RendererHookConfidence::Unresolved:
    return RendererHookReplacement::Unresolved;
  }
  return RendererHookReplacement::Unresolved;
}

constexpr RendererHookSurface DefaultRendererHookSurface(
    RendererHookReplacement replacement) {
  switch (replacement) {
  case RendererHookReplacement::FullReplacement:
  case RendererHookReplacement::FableAdapter:
  case RendererHookReplacement::SideEffectOnly:
    return RendererHookSurface::RequiredRoot;
  case RendererHookReplacement::Unresolved:
    return RendererHookSurface::Unresolved;
  }
  return RendererHookSurface::Unresolved;
}

struct RendererHookTarget {
  RendererHookId id;
  const char* name;
  uint32_t sonic_address;
  uint32_t fable_address;
  RendererHookConfidence confidence;
  RendererHookReplacement replacement;
  RendererHookSurface surface;
  const char* note;

  constexpr RendererHookTarget(RendererHookId target_id,
                               const char* target_name,
                               uint32_t sonic_target_address,
                               uint32_t fable_target_address,
                               RendererHookConfidence target_confidence,
                               const char* target_note)
      : id(target_id),
        name(target_name),
        sonic_address(sonic_target_address),
        fable_address(fable_target_address),
        confidence(target_confidence),
        replacement(DefaultRendererHookReplacement(target_confidence)),
        surface(DefaultRendererHookSurface(replacement)),
        note(target_note) {}

  constexpr RendererHookTarget(RendererHookId target_id,
                               const char* target_name,
                               uint32_t sonic_target_address,
                               uint32_t fable_target_address,
                               RendererHookConfidence target_confidence,
                               RendererHookReplacement target_replacement,
                               const char* target_note)
      : id(target_id),
        name(target_name),
        sonic_address(sonic_target_address),
        fable_address(fable_target_address),
        confidence(target_confidence),
        replacement(target_replacement),
        surface(DefaultRendererHookSurface(target_replacement)),
        note(target_note) {}

  constexpr RendererHookTarget(RendererHookId target_id,
                               const char* target_name,
                               uint32_t sonic_target_address,
                               uint32_t fable_target_address,
                               RendererHookConfidence target_confidence,
                               RendererHookReplacement target_replacement,
                               RendererHookSurface target_surface,
                               const char* target_note)
      : id(target_id),
        name(target_name),
        sonic_address(sonic_target_address),
        fable_address(fable_target_address),
        confidence(target_confidence),
        replacement(target_replacement),
        surface(target_surface),
        note(target_note) {}
};

// Per-hook-symbol immutable descriptor, resolved ONCE per wrapper call site
// (function-local static in the pass-through macros) and then passed through
// OnRendererGuestHookEnter/Exit/Suppressed. Every field is a pure function of
// (symbol, kind) computed by the EXISTING string-based helpers — the helpers
// stay the single source of truth; this is a cache, not a second table.
// Decisions that depend on runtime state (cvars, video phase, args) are NOT
// cached here; the fast paths re-read the cvars and combine them with these
// symbol-derived constants exactly as the string ladders did.
//
// Rationale (2026-07-29): at -O0 the hook wrapper ENTER path measured
// 131.8ms/frame across 3,096 calls (42.6us/call) with only 40.8ms of real
// observer bodies — the rest was per-call re-derivation of symbol identity
// through string_view compare ladders (IsNativeRenderWorkHook,
// IsMandatoryFableResourceReplacementHook, the GTL's strtoul SymbolAddress,
// the wait-probe root scan, the observer dispatch heads).
// RendererHookArgs capture level for a hooked symbol. kLight fills only the
// fields any generic consumer reads (r1, r3-r10, lr, r4_u64) and leaves
// r14-r31 at their deterministic zero defaults; kFull additionally copies
// r14-r31 from the PPC context. Audit 2026-07-29 (grep of every
// `args.r14`..`args.r31` / `args.f1`..`args.f6` read across src/): the ONLY
// handlers that read r14-r31 from a RendererHookArgs are the hook_observer
// CPU texture-layout probes (LogTileHelperDeepProbeLocked and the
// sub_8300CB48 tile-destination refresh/association helpers), all gated on
// IsFableCpuTextureLayoutHelperHook — so exactly the 10 tile/untile symbols
// get kFull. f1-f6 were NEVER captured by CaptureRendererHookArgs even
// before this split (every args.fN reader has always seen 0.0), so neither
// level fills them and behaviour is unchanged.
enum class FableHookArgsCaptureLevel : uint8_t {
  kLight = 0,
  kFull = 1,
};

struct FableHookSymbolDesc {
  const char* symbol = nullptr;   // stable copy owned by the desc registry
  uint32_t address = 0;           // GTL symbol address (rex_* shims mapped)
  uint32_t exact_state_address = 0; // observer FableExactStateSymbolAddress
  uint8_t kind = 0;               // RendererHookKind
  int16_t wait_probe_index = -1;  // RendererWaitProbeNote root index, -1=none
  // Enter AND exit args-capture width for this symbol; see
  // FableHookArgsCaptureLevel above. Set from obs_cpu_texture_layout_helper
  // in ResolveFableHookSymbolDesc; default kLight.
  FableHookArgsCaptureLevel capture_level = FableHookArgsCaptureLevel::kLight;
  const RendererHookTarget* target = nullptr; // stats/policy table entry

  // renderer_runtime policy constants.
  bool physical_wrapper_required_root = false;
  bool title_scene_population = false;
  bool exact_stub = false; // FableUnleashedExactHookKindForSymbol == kStub
  // IsNativeRenderWorkHook split by the runtime cvar that gates each group.
  bool native_work_plume_owned = false;
  bool native_work_replace_resources = false;
  bool native_work_replace_present = false;
  bool shader_patch_helper = false;
  bool get_render_target_hook = false;
  bool shader_constant_replacement = false;
  bool shader_constant_command_hook = false;
  bool shader_constant_state_hook = false;
  bool private_texture_rect_hook = false;
  bool surface_size_hook = false;
  bool scene_stream_boundary = false;
  bool scene_factory_boundary = false;
  bool deep_observer_draw_boundary = false;

  // GTL constants.
  bool gtl_known = false;
  bool gtl_scene_scope = false;

  // hook_observer dispatch-head constants
  // (filled by FillFableHookSymbolDescObserverFields).
  bool obs_scene_submit_scope = false;
  bool obs_present_hook = false;
  bool obs_scene_enable_probe = false;
  bool obs_scene_factory_observer = false;
  bool obs_scene_stream_observer = false;
  bool obs_cpu_texture_layout_helper = false;
  bool obs_texture_decoder_probe = false;
  bool obs_resource_lock = false;
  bool obs_resource_unlock = false;
  bool obs_private_rect_wrapper = false;
  bool obs_command_write = false;        // IsFableCommandWriteHook
  bool obs_presented_draw_capture = false; // IsFablePresentedDrawCaptureHook
  uint32_t render_state_slot = UINT32_MAX;  // FableRenderStateSlotForSymbol
  uint32_t sampler_state_slot = UINT32_MAX; // FableSamplerStateSlotForSymbol
  uint8_t draw_submit_kind = 0;  // FableDrawSubmitKind
  uint8_t vertex_decl_kind = 0;  // FableVertexDeclarationHookKind

  // Wrapper fast-path eligibility (2026-07-30): TRUE when this symbol has NO
  // per-call consumer in OnRendererGuestHookEnter/Exit beyond the bare call
  // counters under the shipping hook policy (root-only, no deep observer, no
  // telemetry, Plume backend). Computed in ResolveFableHookSymbolDesc from
  // symbol constants ONLY — no cvar value is baked in, so it never needs
  // invalidation when a cvar flips: the runtime half of the decision is
  // re-read from the cvars on every call (RendererHookWrapperFastPathModeActive
  // in renderer_runtime.cpp), exactly like every other desc field. Implies
  // capture_level == kLight (obs_cpu_texture_layout_helper is excluded).
  bool fast_path_eligible = false;

  // Per-symbol fire count for the desc-covered census (replaces the memoised
  // ResolveRendererHookTarget symbol census on the desc fast path).
  mutable std::atomic<uint64_t> hits{0};
  // TraceHookDecision emits ONE log per (symbol, decision); under
  // renderer_trace_hooks=true (the shipping toml, run 429) the un-cached
  // check built a std::string key + set lookup + a string-ladder
  // ShouldRunGuestRendererHook on EVERY call. Bit 1 = guest-run logged,
  // bit 2 = plume-owned logged.
  mutable std::atomic<uint8_t> trace_decisions_logged{0};
};

constexpr RendererHookTarget kRendererHookTargets[] = {
    {RendererHookId::kCreateDevice, "CreateDevice", 0x82BD99B0, 0x82387908,
     RendererHookConfidence::Tentative, RendererHookReplacement::SideEffectOnly,
     RendererHookSurface::MigrationSupport,
     "Fable high-level renderer setup; generated symbol is rex_lhRenderer_Setup. "
     "This is not a direct D3D9 device ABI twin and is already part of the "
     "startup/VFS lifecycle."},
    {RendererHookId::kDestructResource, "DestructResource", 0x82BE6230, 0x821FC320,
     RendererHookConfidence::Tentative, RendererHookReplacement::FableAdapter,
     "Broad Fable GPU resource refcount release/destruct helper. This is a "
     "resource-lifetime choke point, not a narrow Sonic D3D9 ABI twin."},
    {RendererHookId::kLockTextureRect, "LockTextureRect", 0x82BE9300, 0x82B98930,
     RendererHookConfidence::High, RendererHookReplacement::FullReplacement,
     "Exact opcode-shape match."},
    {RendererHookId::kUnlockTextureRect, "UnlockTextureRect", 0x82BE7780, 0x82B98950,
     RendererHookConfidence::High, "Exact opcode-shape match."},
    {RendererHookId::kLockVertexBuffer, "LockVertexBuffer", 0x82BE6B98, 0x8225F980,
     RendererHookConfidence::High, "Exact opcode-shape match."},
    {RendererHookId::kUnlockVertexBuffer, "UnlockVertexBuffer", 0x82BE6BE8, 0x82B9EEC0,
     RendererHookConfidence::High, "Exact opcode-shape match."},
    {RendererHookId::kGetVertexBufferDesc, "GetVertexBufferDesc", 0x82BE61D0, 0x82B8B820,
     RendererHookConfidence::Tentative, RendererHookReplacement::FableAdapter,
     RendererHookSurface::MigrationSupport,
     "No standalone Fable ABI twin. Compatibility derives the desc from the "
     "typed vertex-buffer object built by rex_lhGfx_CreateVertexBuffer and "
     "refreshed through LockVertexBuffer/XGSetVertexBufferHeader."},
    {RendererHookId::kLockIndexBuffer, "LockIndexBuffer", 0x82BE6CA8, 0x82196980,
     RendererHookConfidence::High, "Exact opcode-shape match."},
    {RendererHookId::kUnlockIndexBuffer, "UnlockIndexBuffer", 0x82BE6CF0, 0x82B9EED0,
     RendererHookConfidence::High, "Exact opcode-shape match."},
    {RendererHookId::kGetIndexBufferDesc, "GetIndexBufferDesc", 0x82BE6200, 0x82B8D7A8,
     RendererHookConfidence::Tentative, RendererHookReplacement::FableAdapter,
     RendererHookSurface::MigrationSupport,
     "No standalone Fable ABI twin. Compatibility derives the desc from the "
     "typed index-buffer object built by rex_lhGfx_CreateIndexBuffer and "
     "refreshed through LockIndexBuffer/XGSetIndexBufferHeader."},
    {RendererHookId::kGetSurfaceDesc, "GetSurfaceDesc", 0x82BE96F0, 0x821FA070,
     RendererHookConfidence::High, "Exact opcode-shape match."},
    {RendererHookId::kGetVertexDeclaration, "GetVertexDeclaration", 0x82BE04B0, 0x822224A8,
     RendererHookConfidence::Tentative, RendererHookReplacement::FableAdapter,
     RendererHookSurface::MigrationSupport,
     "No standalone Fable ABI twin. Current declaration is the device slot "
     "mirrored from SetVertexDeclaration/XGSetVertexDeclaration; declaration "
     "objects store count at +24 and 12-byte elements at +52."},
    {RendererHookId::kHashVertexDeclaration, "HashVertexDeclaration", 0x82BE0530, 0x82B99720,
     RendererHookConfidence::Tentative, RendererHookReplacement::FableAdapter,
     RendererHookSurface::MigrationSupport,
     "No standalone Fable ABI twin. Compatibility hashes the element stream "
     "captured from rex_lhGfx_CreateGPUState/CreateVertexDeclaration."},
    {RendererHookId::kPresent, "Present", 0x82BDA8C0, 0x82B6F1D0,
     RendererHookConfidence::Tentative,
     "Fable renderer end-scene path; generated symbol is rex_lhRenderer_EndScene "
     "and it reaches the display swap helper after frame work is flushed."},
    {RendererHookId::kDisplaySwap, "DisplaySwap", 0, 0x82BA34D8,
     RendererHookConfidence::Tentative,
     "Fable display/swap helper called by the end-scene path after frame work "
     "is flushed. Hooked separately so Plume can own display "
     "handoff while preserving higher-level EndScene bookkeeping."},
    {RendererHookId::kGetBackBuffer, "GetBackBuffer", 0x82BDD330, 0x82B981D0,
     RendererHookConfidence::Tentative,
     "No exact Sonic ABI twin. Fable's D3DDevice_GetRenderTarget plus the "
     "0x82B6F040 presentation scratch refresh expose the same current "
     "backbuffer/render-target state for replacement."},
    {RendererHookId::kGetRenderTarget, "GetRenderTarget", 0, 0x82B981D0,
     RendererHookConfidence::High,
     "Lumen names this exact Fable entry D3DDevice_GetRenderTarget. IDA shows "
     "it reads device + 4 * (slot + 3110), AddRefs the surface if present, and "
     "returns the guest surface pointer. Hooked as a general native-running "
     "getter; Plume mirrors suppressed SetRenderTarget writes into the same "
     "device slot table so this remains coherent without native D3D9 draws."},
    {RendererHookId::kCreateTexture, "CreateTexture", 0x82BE9498, 0x82B98B28,
     RendererHookConfidence::High, "Exact opcode-shape match."},
    {RendererHookId::kCreateVertexBuffer, "CreateVertexBuffer", 0x82BE6AD0, 0x82B8B820,
     RendererHookConfidence::Tentative,
     "Fable engine-level GPU vertex-buffer constructor; generated symbol is "
     "rex_lhGfx_CreateVertexBuffer, not an exact Sonic D3D9 ABI twin."},
    {RendererHookId::kCreateIndexBuffer, "CreateIndexBuffer", 0x82BE6BF8, 0x82B8D7A8,
     RendererHookConfidence::Tentative,
     "Fable engine-level GPU index-buffer constructor; generated symbol is "
     "rex_lhGfx_CreateIndexBuffer, not an exact Sonic D3D9 ABI twin."},
    {RendererHookId::kCreateSurface, "CreateSurface", 0x82BE95B8, 0x821F4EC0,
     RendererHookConfidence::High,
     "Lumen names this exact Fable entry D3DDevice_CreateSurface; generated "
     "symbol is rex_lhTexture_Create. Surface-view constructors 0x82B98978 "
     "and 0x82B98A58 are still hooked as Fable-specific view paths, alongside "
     "render-target/depth wrappers 0x821FC8D8, 0x82295380, and constructor "
     "0x82B8AB30."},
    {RendererHookId::kStretchRect, "StretchRect", 0x82BF6400, 0x82D21C68,
     RendererHookConfidence::Tentative,
     "No exact Sonic ABI twin. Fable splits this across surface-copy and "
     "display-copy choke points including D3DXLoadSurfaceFromSurface, "
     "InternalCopy, and D3DXLoadSurfaceFromMemory; Plume aliases those copies "
     "as the StretchRect compatibility path."},
    {RendererHookId::kSetTextureHeader, "SetTextureHeader", 0, 0x821FB650,
     RendererHookConfidence::Tentative, RendererHookReplacement::SideEffectOnly,
     "Fable-only texture identity root. Lumen names D3D::SetTextureHeader; "
     "IDA confirms the 0x318-byte header builder. Refii treats this as the "
     "stable texture object/shape/format source that maps to the "
     "Unleashed/Marathon producer-edge model."},
    {RendererHookId::kPM4SetTextureFetchConstant, "PM4SetTextureFetchConstant", 0, 0x82BA77D0,
     RendererHookConfidence::Tentative, RendererHookReplacement::SideEffectOnly,
     "Fable-only sampled-texture identity root. Lumen names "
     "D3D::PM4SetTextureFetchConstant; IDA confirms the compact 0xc4-byte "
     "six-fetch-dword writer used to connect sampled texture identity to "
     "direct packets. Conditionally exercised: its only callers are the "
     "pix-video roots sub_82BA7B28/sub_82BA83C0, so it stays silent while "
     "Plume owns video; fetch identity is then derived from PM4 segment "
     "parsing instead. Zero hits at boot->title is expected."},
    {RendererHookId::kFindSurfaceWithinTexture, "FindSurfaceWithinTexture", 0, 0x821FA6C0,
     RendererHookConfidence::Tentative, RendererHookReplacement::SideEffectOnly,
     "Fable-only surface/slice identity helper called by private-rectangle "
     "and surface wrappers. This is exact subresource identity evidence, not "
     "a broad scan."},
    {RendererHookId::kPrivateTextureRectangleWrapper, "ResolveColor", 0, 0x822326E0,
     RendererHookConfidence::Tentative, RendererHookReplacement::FableAdapter,
     RendererHookSurface::MigrationSupport,
     "Compact private-rectangle wrapper around "
     "D3D::PrivateTextureRectangleDraw. Parity notes compare it to the "
     "Unleashed/Marathon ProcStretchRect producer-time sourceSurface "
     "publication; Refii uses wrapper call/context as exact producer "
     "identity."},
    {RendererHookId::kPrivateTextureRectangleSetupFlushWrapper, "PrivateTextureRectangleSetupFlushWrapper", 0, 0x8228ACB8,
     RendererHookConfidence::Tentative, RendererHookReplacement::FableAdapter,
     RendererHookSurface::MigrationSupport,
     "Setup/flush private-rectangle wrapper around "
     "D3D::PrivateTextureRectangleDraw. Captures setup globals/source object "
     "so the following action joins by wrapper call serial/context instead "
     "of sibling-surface scoring."},
    {RendererHookId::kPrivateTextureRectangleSiblingWrapper, "PrivateTextureRectangleSiblingWrapper", 0, 0x82242108,
     RendererHookConfidence::Tentative, RendererHookReplacement::FableAdapter,
     RendererHookSurface::MigrationSupport,
     "Sibling/private-rectangle wrapper around "
     "D3D::PrivateTextureRectangleDraw. IDA confirms the compact 0xc8-byte "
     "wrapper; parity validation promotes it to producer-time targeted "
     "publication for exact color/title alias edges."},
    {RendererHookId::kPrivateTextureRectangleDraw, "Resolve", 0, 0x82206888,
     RendererHookConfidence::Tentative, RendererHookReplacement::FableAdapter,
     RendererHookSurface::MigrationSupport,
     "Fable private-rectangle publisher. Lumen names "
     "D3D::PrivateTextureRectangleDraw and IDA shows a 0xe40-byte function "
     "with many semantic callers, so Refii treats caller/wrapper context as "
     "action identity instead of treating the publisher as one fuzzy draw "
     "shape."},
    {RendererHookId::kSetRenderTarget, "SetRenderTarget", 0x82BDD9F0, 0x822869A0,
     RendererHookConfidence::High, "Exact opcode-shape match."},
    {RendererHookId::kSetDepthStencilSurface, "SetDepthStencilSurface", 0x82BDDD38, 0x82264590,
     RendererHookConfidence::High, "Exact opcode-shape match."},
    {RendererHookId::kClear, "Clear", 0x82BFE4C8, 0x821E2FE0, RendererHookConfidence::High,
     "Exact opcode-shape match."},
    {RendererHookId::kSetViewport, "SetViewport", 0x82BDD8C0, 0x821F9F58,
     RendererHookConfidence::High, "Exact opcode-shape match."},
    {RendererHookId::kSetTexture, "SetTexture", 0x82BE9818, 0x821B6EE0, RendererHookConfidence::High,
     "Exact opcode-shape match."},
    {RendererHookId::kSetScissorRect, "SetScissorRect", 0x82BDCFB0, 0x821F9C08,
     RendererHookConfidence::High, "Exact opcode-shape match."},
    {RendererHookId::kSceneBatchRecordBuilder, "SceneBatchRecordBuilder", 0,
     0x822189B0, RendererHookConfidence::Tentative,
     RendererHookReplacement::FableAdapter,
     "Fable scene-object record builder. IDA shows traversal feeding this "
     "builder before it calls the scene batch DrawPrimitive bridge; Refii uses "
     "the native post-call record as a typed scene draw packet source."},
    {RendererHookId::kDrawPrimitive, "DrawPrimitive", 0x82BE5900, 0x8221C3E8,
     RendererHookConfidence::High, "Exact opcode-shape match."},
    {RendererHookId::kDrawIndexedPrimitive, "DrawIndexedPrimitive", 0x82BE5CF0, 0x8221DFC0,
     RendererHookConfidence::High, "Exact opcode-shape match."},
    {RendererHookId::kDrawPrimitiveUP, "DrawPrimitiveUP", 0x82BE52F8, 0x82265CD8,
     RendererHookConfidence::High, "Exact opcode-shape match."},
    {RendererHookId::kDrawIndexedUserPrimitives, "DrawIndexedUserPrimitives", 0,
     0x8220A3F0, RendererHookConfidence::High,
     "IDA 2026-07-13: ring-allocates via sub_82217DB8 then memcpys the "
     "caller's index (6*r4 bytes, 16-bit) and vertex (r3*r7 bytes) data "
     "before returning — geometry is complete at exit. 26 callers: bink "
     "compositor sub_82A41F10 + the UI/storybook quad family."},
    {RendererHookId::kCreateVertexDeclaration, "CreateVertexDeclaration", 0x82BE0428, 0x82B99720,
     RendererHookConfidence::High,
     "Allocates/finalizes D3D vertex declaration state; generated symbol is "
     "rex_lhGfx_CreateGPUState."},
    {RendererHookId::kSetVertexDeclaration, "SetVertexDeclaration", 0x82BE02E0, 0x822224A8,
     RendererHookConfidence::High, "Exact opcode-shape match."},
    {RendererHookId::kCreateVertexShader, "CreateVertexShader", 0x82BE1A80, 0x82B994B8,
     RendererHookConfidence::High, "Exact opcode-shape match."},
    {RendererHookId::kSetVertexShader, "SetVertexShader", 0x82BE0110, 0x82232510,
     RendererHookConfidence::High, "Exact opcode-shape match."},
    {RendererHookId::kSetStreamSource, "SetStreamSource", 0x82BDD0F8, 0x821B6C60,
     RendererHookConfidence::High, "Exact opcode-shape match."},
    {RendererHookId::kSetIndices, "SetIndices", 0x82BDD218, 0x8219CC30, RendererHookConfidence::High,
     "Exact opcode-shape match."},
    {RendererHookId::kCreatePixelShader, "CreatePixelShader", 0x82BE1990, 0x82B992B0,
     RendererHookConfidence::High,
     "Pixel-shader object creator; sibling vertex-shader creator is "
     "0x82B994B8."},
    {RendererHookId::kSetPixelShader, "SetPixelShader", 0x82BDFE58, 0x82208BB0,
     RendererHookConfidence::High, "Exact opcode-shape match."},
    {RendererHookId::kD3DXFillTexture, "D3DXFillTexture", 0x82C003B8, 0x82D16CF8,
     RendererHookConfidence::High, "Exact opcode-shape match."},
    {RendererHookId::kD3DXFillVolumeTexture, "D3DXFillVolumeTexture", 0x82C00910, 0,
     RendererHookConfidence::Unresolved,
     "No confirmed Fable ABI twin. Lumen identifies 0x82D17250 as "
     "CreateTextureFromFileInMemoryEx, so Refii keeps that as a Fable-specific "
     "texture-file create/decode path instead of treating it as Sonic's volume "
     "texture fill hook."},
    {RendererHookId::kMakePictureData, "MakePictureData", 0x82E43FC8, 0, RendererHookConfidence::Unresolved,
     "No Fable generated-PPC exact-shape match. This is Sonic-specific picture "
     "asset decoding and texture recreation logic, including game-specific DDS "
     "patches/cubemap fixes; Refii uses Fable's own texture-file create/decode "
     "path instead."},
    {RendererHookId::kSetResolution, "SetResolution", 0x82E9EE38, 0, RendererHookConfidence::Unresolved,
     "No Fable generated-PPC exact-shape match. Sonic's hook only stores "
     "scaled viewport dimensions into Sonic's device structure; Fable's Plume "
     "replacement derives presentation size from its own display/end-scene "
     "bookkeeping."},
    {RendererHookId::kScreenShaderInit, "ScreenShaderInit", 0x82AE2BF8, 0,
     RendererHookConfidence::Unresolved,
     "No Fable generated-PPC exact-shape match. This installs Sonic movie "
     "screen shaders and a Sonic vertex declaration; Refii keeps Fable's "
     "SBK/XenosRecomp shader artifacts and video/private texture adapters."},
    {RendererHookId::kMemExportLandscapeTessellationPass,
     "MemExportLandscapeTessellationPass", 0, 0x8222EA20,
     RendererHookConfidence::Tentative,
     RendererHookReplacement::SideEffectOnly, RendererHookSurface::DebugSupport,
     "IDA-verified: sub_8222EA20 is a complete MemExport (GPU stream-out) "
     "compute pass, not a scene submit. It brackets D3DDevice_BeginExport "
     "(0x8222EA84) / D3DDevice_EndExport (0x8222EDC4) around a per-cell loop "
     "that issues one D3DPT_POINTLIST DrawVertices per visible cell. One "
     "export stream constant at binding 313, format k_32_32_32_32_FLOAT "
     "(16 B/element), export address advancing 1024 B per cell = exactly 64 "
     "elements. Per-cell AABB min/max + Width/Height + a distance-squared "
     "<= 4096.0 LOD test match the shipped adaptive-tessellation vertex "
     "shaders (blobs 0016/0017 landscape, 0068 water). Also invoked from "
     "inside the \"Decal Render\" scope (sub_82A63140) to re-tessellate the "
     "surfaces decals project onto."},
    {RendererHookId::kMemExportSkinnedMeshPass, "MemExportSkinnedMeshPass", 0,
     0x8227B548, RendererHookConfidence::Tentative,
     RendererHookReplacement::SideEffectOnly, RendererHookSurface::DebugSupport,
     "IDA-verified: sub_8227B548 is a complete MemExport (GPU stream-out) "
     "compute pass. NOT title-related and NOT a copy. It writes three export "
     "stream constants at bindings 315, 316 and 314, binds the base vertex "
     "buffer on stream 0 and a second (bone/matrix palette) buffer on stream "
     "3, then loops 64-byte submesh records issuing one D3DPT_POINTLIST "
     "DrawVertices each, bracketed by D3DDevice_BeginExport / "
     "D3DDevice_EndExport (0x8227B73C). Matches the shipped "
     "vshader_mem_export_skin vertex shader (blob 0107)."},
    {RendererHookId::kMemExportDecalBakePass, "MemExportDecalBakePass", 0,
     0x82A63310, RendererHookConfidence::Tentative,
     RendererHookReplacement::SideEffectOnly, RendererHookSurface::DebugSupport,
     "IDA-verified: sub_82A63310 is a complete MemExport (GPU stream-out) "
     "compute pass. NOT PIX and NOT video. It writes three export stream "
     "constants at bindings 315, 316 and 314, uploads the literals "
     "17.0/17.0/8.0/8.0 at constant 357, and issues a single DrawVertices of "
     "289 points = a 17x17 lattice, bracketed by D3DDevice_BeginExport / "
     "D3DDevice_EndExport (0x82A63A84). Its export target is slot "
     "sub_82A629B8() of the 50-entry ring at unk_8349FBD0 (stride 96); the "
     "caller sub_82181298 ages that ring by a per-entry lifetime and the "
     "enclosing scope carries the literal PIX annotation \"Decal Render\"."},
    {RendererHookId::kEndExportPixRenderVideoFrameCaller,
     "EndExportPixRenderVideoFrameCaller", 0, 0x82BA83C0,
     RendererHookConfidence::Tentative, RendererHookReplacement::SideEffectOnly,
     RendererHookSurface::DebugSupport,
     "IDA xrefs show sub_82BA83C0 calls D3DDevice_EndExport at 0x82BA88FC. "
     "Lumen/Refii name this path D3D::PixRenderVideoFrame; Refii keeps it "
     "visible as the fourth export caller root for active resolve/export "
     "traversal proof."},
    {RendererHookId::kEndExport, "EndExport", 0, 0x82196750,
     RendererHookConfidence::Tentative, RendererHookReplacement::SideEffectOnly,
     "Lumen names this exact Fable entry D3DDevice_EndExport. IDA confirms "
     "sub_82196750 as a 0x230-byte resolve/export root; Refii observes it as "
     "a producer for explicit resolve/copy graph packets rather than a draw "
     "or texture-bind substitute. Conditionally exercised: reachable only "
     "from the four DebugSupport caller sites plus a render-command handler "
     "table slot (0x8210C2A0); at boot->title resolve packets are produced "
     "from PM4 segment decode (sub_821E8D38), so zero hits is expected."},
    {RendererHookId::kInvalidateCaches, "InvalidateCaches", 0, 0x822C6BA8,
     RendererHookConfidence::Tentative, RendererHookReplacement::SideEffectOnly,
     "Lumen names this exact Fable entry D3D::InvalidateCaches. Refii uses it "
     "as a side-effect resolve/export invalidation root that can publish a "
     "typed resolve/copy packet into the Refii render graph. Conditionally "
     "exercised: IDA shows its only code callers are two sites inside "
     "EndExport (sub_82196750), so it is silent whenever EndExport is."},
    {RendererHookId::kSetVertexShaderConstantB, "SetVertexShaderConstantB", 0,
     0x821C6708, RendererHookConfidence::High,
     RendererHookReplacement::SideEffectOnly,
     "D3DDevice_SetVertexShaderConstantB (Lumen name, IDA-verified packed "
     "bool-bit writer into the device shadow). Translated shaders branch on "
     "g_Booleans (586 of 1178 use it), so the observer must capture bool "
     "uploads on the Plume-owned replacement path; without this root the "
     "suppressed-hook exit is policy-denied and every bool reads 0."},
    {RendererHookId::kSetPixelShaderConstantB, "SetPixelShaderConstantB", 0,
     0x8226E120, RendererHookConfidence::High,
     RendererHookReplacement::SideEffectOnly,
     "D3DDevice_SetPixelShaderConstantB; same packed bool-bit writer as the "
     "vertex variant (registers are 0-based per stage at the API, IDA "
     "sub_82220CA8 passes b5/b6). Pixel bools land in g_Booleans bits 16+ "
     "for translated shaders. Conditionally exercised: boot-to-title uses "
     "the vertex bool setter but does not reach the pixel b5/b6 caller; the "
     "wrapper remains required for later scene/material paths."},
    {RendererHookId::kEmitTessellatedIndexedPackets,
     "EmitTessellatedIndexedPackets", 0, 0x82207AF8,
     RendererHookConfidence::High, RendererHookReplacement::SideEffectOnly,
     RendererHookSurface::DebugSupport,
     "IDA D3D_EmitDrawIndexedPackets_INFERRED. TERRAIN. Writes PM4 header "
     "0xC0032201 (type-3, 4 body dwords, opcode 0x22 DRAW_INDX, predicated) "
     "DIRECTLY into the command ring at 0x82207F24, so it bypasses all eight "
     "hooked D3D draw roots and Refii sees zero landscape draws. Sets "
     "VGT_OUTPUT_PATH_CNTL |= TESSELLATE and VGT_GROUP_PRIM_TYPE = 14 "
     "(device+0x2964 shadow = Xenos reg 0x2280+i), and emits a "
     "VGT_DRAW_INITIATOR of (count<<16)|0x112 or |0x912 -- PRIM_TYPE 0x12, a "
     "quad patch, outside the D3DPRIMITIVETYPE identity table at 0x82006788 "
     "which ends at 13. Sole caller sub_82A8F1D8 emits one draw per contiguous "
     "run of set bits in a sub-tile visibility mask; chain sub_8226D908 -> "
     "Render_ScreenSpaceIndexedPass_INFERRED 0x82236508 -> sub_822059E8. "
     "Content proven by \"bg patch %d, opaque\"/\"bg patch %d, blended\" "
     "(0x8200169C/0x82001B8C). CAPTURE-ONLY: hooked so the GTL can record a "
     "TessellatedPatchDrawPacket; the guest body still runs and nothing "
     "rendered changes."},
    {RendererHookId::kEmitTessellatedPrimitivePackets,
     "EmitTessellatedPrimitivePackets", 0, 0x8221C898,
     RendererHookConfidence::High, RendererHookReplacement::SideEffectOnly,
     RendererHookSurface::DebugSupport,
     "IDA D3D_EmitDrawPrimitivePackets_INFERRED. WATER / second heightfield. "
     "Same direct-to-ring bypass as 0x82207AF8 but header 0xC0012201 (2 body "
     "dwords = auto-index, no index buffer) and initiator (count<<16)|0x192 -- "
     "SOURCE_SELECT 2, PRIM_TYPE 0x12. Writes VGT_INDX_OFFSET (type-0 packet, "
     "reg 0x2102) with its start-vertex argument, unlike the terrain emitter "
     "which writes 0. Sole caller sub_8221FC40 loops 4 sub-quads per patch "
     "behind vmsum3fp128/vcmpgtfp clip-plane tests and sets predication per "
     "sub-quad; chain sub_821DD618 -> sub_8223DB60. Tessellated-patch nature "
     "is VERIFIED from the registers; the WATER attribution is INFERRED from "
     "sub_8223DB60 being the only other toucher of g_StateVar_TessellationMode "
     "plus \"Water patches before sorting: %d\" (0x820F85DC) / \"Water quads "
     "rendererd: %d\" (0x820F8620). CAPTURE-ONLY, as above."},
    // ADDED 2026-07-29 — WITHOUT THIS ENTRY THE RESOLUTION PATCH CANNOT RUN.
    // ResolveRendererHookTarget() returns nullptr for any symbol missing from
    // this table, and RendererHookAllowedBySurface(nullptr) is FALSE under the
    // active policy (root_only=true, deep_observer=false). That gates
    // ObserveRendererHookEnter off entirely, so
    // PatchFablePresentationParametersLocked never executed -- runs 404 and 405
    // produced no presentation-parameter log line of any kind, even an entry
    // one. The hook itself fires (it logs "observed for plume/vulkan"); it was
    // the observer feed behind it that was switched off.
    {RendererHookId::kInitializePresentationParameters,
     "InitializePresentationParameters", 0, 0x82BA6508,
     RendererHookConfidence::Tentative,
     "Fable presentation-parameter init. Required root because the native "
     "render resolution is applied here: the extent is written into the "
     "guest's own parameter struct so the guest renders at output resolution "
     "rather than 1280x720 upscaled at present."},
};

constexpr uint32_t kRendererHookTargetCount =
    static_cast<uint32_t>(sizeof(kRendererHookTargets) /
                          sizeof(kRendererHookTargets[0]));

constexpr const RendererHookTarget* RendererHookTargetById(RendererHookId id) {
  const uint32_t index = static_cast<uint32_t>(id);
  return index < kRendererHookTargetCount ? &kRendererHookTargets[index]
                                          : nullptr;
}

constexpr bool RendererHookTargetIdsAreStable() {
  if (kRendererHookTargetCount != static_cast<uint32_t>(RendererHookId::kCount)) {
    return false;
  }
  for (uint32_t index = 0; index < kRendererHookTargetCount; ++index) {
    if (static_cast<uint32_t>(kRendererHookTargets[index].id) != index) {
      return false;
    }
  }
  return true;
}

constexpr uint32_t RendererHookSurfaceCount(RendererHookSurface surface) {
  uint32_t count = 0;
  for (const RendererHookTarget& target : kRendererHookTargets) {
    if (target.surface == surface) {
      ++count;
    }
  }
  return count;
}

static_assert(RendererHookTargetIdsAreStable(),
              "Renderer hook target ids must match kRendererHookTargets order");
// 44 -> 45 on 2026-07-29: kInitializePresentationParameters added as a required
// root so the native-resolution patch is reachable at all (see its table note).
static_assert(RendererHookSurfaceCount(RendererHookSurface::RequiredRoot) == 45,
              "Renderer final replacement root count changed");
static_assert(RendererHookSurfaceCount(RendererHookSurface::MigrationSupport) == 9,
              "Renderer migration-support root count changed");
static_assert(RendererHookSurfaceCount(RendererHookSurface::DebugSupport) == 6,
              "Renderer debug/proof root count changed");
static_assert(RendererHookSurfaceCount(RendererHookSurface::Unresolved) == 4,
              "Renderer unresolved root count changed");

}  // namespace refii::renderer
