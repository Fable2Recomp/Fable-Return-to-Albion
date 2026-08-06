#include "runtime/refii_renderer/gtl/refii_gtl.h"

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
// renderer_gtl_constant_self_read: device-bank float-constant authority (the
// feed-7 deletion prerequisite; see the cvar definition for the full model).
#include "runtime/cvars.h"
// Guest-format block dimensions for the tile writer's source-size math. The
// SDK's table is the stated authority; the alternative is a hardcoded /4 that
// happens to suit fmt=20 and is wrong for every uncompressed format.
#include <rex/chrono/clock.h>
#include <rex/graphics/pipeline/texture/info.h>
#include "runtime/refii_renderer/hooks/general/fable_hook_stack.h"
#include "runtime/refii_renderer/hooks/root/fable_hook_symbols.h"
#include "runtime/refii_renderer/bik/bink_guest_memory.h"
// For FableDirectPackSamplerStateValue: the PURE sampler packer, used so the
// GTL can pack against its own mirror without mutating direct-pipeline state.
#include "runtime/refii_renderer/plume/fable/direct/fable_direct_state.h"
// The resource-snapshot feed the GTL now owns (the deleted observer's old
// contract) and the FNV-1a-64 guest hasher the shader library keys on.
#include "runtime/refii_renderer/plume/fable/resource/fable_resource_snapshots.h"
#include "runtime/refii_renderer/plume/fable/resource/fable_upload_data.h"
#include "runtime/refii_renderer/plume/general/plume_fable_addressing.h"
#include "runtime/refii_renderer/plume/fable/fable_guest_display.h"

namespace refii::gtl {
namespace {

// Fable ABI twin addresses (renderer_hook_targets.h is the source of truth;
// duplicated here as integral constants so the GTL never includes the hook
// policy machinery).
constexpr uint32_t kCreateTexture = 0x82B98B28;
constexpr uint32_t kCreateSurface = 0x821F4EC0;
constexpr uint32_t kCreateVertexBuffer = 0x82B8B820;
constexpr uint32_t kCreateIndexBuffer = 0x82B8D7A8;
constexpr uint32_t kCreateVertexShader = 0x82B994B8;
constexpr uint32_t kCreatePixelShader = 0x82B992B0;
// FABLE'S OWN SHADER CONSTRUCTORS (IDA, 2026-08-04). The two XDK roots above
// are real D3D9 roots but have exactly ONE caller each -- the Scaleform/GFx
// runtime HLSL compile ("vs_1_1"/"main" @0x82feeb78) that backs Pub Games. They
// NEVER fire in Fable's own renderer, which is why the registry held 0 shader
// resources (kind census: 2:67, 4:45, 1:35, 0:15 -- no 7, no 8) and every draw
// failed `missing_vertex_shader_resource` with pipeline=0/0.
//
// These two ARE Fable's shader creates, dispatched from the shader
// deserializer sub_82B91130 on *(res+12) (1 = pixel, 2 = vertex) and from
// sub_82B91A18:
//   sub_82B87738 / sub_82B87818 (void *pWrapper /*12 bytes*/,
//                                const DWORD *pContainer) -> pWrapper in r3.
// Kept ALONGSIDE the XDK roots rather than replacing them: those still produce
// the Pub Games shaders.
constexpr uint32_t kFableCreateVertexShader = 0x82B87738;
constexpr uint32_t kFableCreatePixelShader = 0x82B87818;
constexpr uint32_t kCreateVertexDeclaration = 0x82B99720;
constexpr uint32_t kDestructResource = 0x821FC320;

constexpr uint32_t kSetTexture = 0x821B6EE0;
constexpr uint32_t kSetRenderTarget = 0x822869A0;
constexpr uint32_t kSetDepthStencilSurface = 0x82264590;
constexpr uint32_t kSetVertexShader = 0x82232510;
constexpr uint32_t kSetPixelShader = 0x82208BB0;
constexpr uint32_t kSetVertexDeclaration = 0x822224A8;
constexpr uint32_t kSetStreamSource = 0x821B6C60;
constexpr uint32_t kSetIndices = 0x8219CC30;
constexpr uint32_t kSetViewport = 0x821F9F58;
constexpr uint32_t kSetScissorRect = 0x821F9C08;

constexpr uint32_t kDrawPrimitive = 0x8221C3E8;
constexpr uint32_t kDrawIndexedPrimitive = 0x8221DFC0;
constexpr uint32_t kDrawPrimitiveUP = 0x82265CD8;

constexpr uint32_t kUnlockTextureRect = 0x82B98950;
constexpr uint32_t kUnlockVertexBuffer = 0x82B9EEC0;
constexpr uint32_t kUnlockIndexBuffer = 0x82B9EED0;
// D3D::UnlockResource — the unlock root that actually carries Fable's content.
// Wired 2026-07-22 on run-117 evidence: every observed dirty extent came
// through here and ZERO through kUnlockTextureRect, so while this stayed
// unmapped the GTL's content_generation bump was blind to essentially every
// CPU texture write.
//
// CORRECTION (run 132): the reason kUnlockTextureRect contributes nothing is
// that FABLE NEVER CALLS IT — sub_82B98950 does not fire, and neither do its
// only callers (D3DXTex_CreateTextureFromTexture, D3DXFillTexture,
// Texture_UploadLockRect). An earlier version of this comment blamed an
// "intra-module call that never re-enters our hooks"; that was wrong and is
// disproved by DrawPrimitive, which fires constantly even though every one of
// its callers is an internal Render_* function. Ordinary intra-module `bl`
// calls DO reach our wrappers. The genuine blind spot is INLINED callees,
// where the compiler duplicated the body and no call instruction exists at
// all.
constexpr uint32_t kUnlockResource = 0x822C2630;
// PHYSICAL-POOL RELOCATION (IDA 2026-08-04). Fable allocates every vertex /
// index / constant buffer payload out of a compacting physical pool, and
// registers `sub_82B87670` as that pool's MOVE CALLBACK
// (Render_PhysicalPoolAlloc @0x82B87258). When the compactor moves a payload it
// memcpys the bytes and then dispatches vtable slot 4 of `off_820FB130`, which
// is this function -- the whole of it, verbatim from the recompiled body:
//
//   stw   r5, 4(r3)     ; obj->current_base = newAddr
//   subf  r4, r4, r5    ; delta = newAddr - oldAddr
//   addi  r3, r3, 8     ; pResource = obj + 8   (the D3D header)
//   b     XGOffsetResourceAddress
//
// so the header's stated GPU address CHANGES under a buffer that no Create and
// no Unlock will ever mention again. Anything the GTL cached about a buffer's
// payload placement is stale from here on; the handler re-reads the header and
// bumps the content generation so the consumer re-uploads from the new memory.
// Signature is (obj /*r3, WRAPPER BASE*/, oldAddr /*r4*/, newAddr /*r5*/) --
// note r3 is the wrapper, so the registry key is r3 + 8.
constexpr uint32_t kRelocateResource = 0x82B876F0;
// Subresource view roots. Both return the new view object in r3 at exit, so
// they pair enter (parent + face/level) with exit (the view) like the other
// factory roots. They state the face/level the GTL would otherwise have to
// recover from addresses — which is impossible for a cube, whose faces are
// separate allocations.
//   D3DTexture_GetSurfaceLevel(pTexture /*r3*/, Level /*r4*/)
//   D3DCubeTexture_GetCubeMapSurface(pTexture /*r3*/, FaceType /*r4*/,
//                                    Level /*r5*/)
constexpr uint32_t kGetSurfaceLevel = 0x82B98860;
constexpr uint32_t kGetCubeMapSurface = 0x82B98978;
// XGCompressSurface: writes surface contents, so it bumps content_generation
// like the Unlock roots. NOTE: this is NOT a resolve and it never calls
// D3DDevice_Resolve — it was previously misnamed kResolveSurface and used as a
// batch boundary, which rested on the superseded "private-rect blit" model.
// The real EDRAM resolve root is kResolve (0x82206888) below.
constexpr uint32_t kXGCompressSurface = 0x8300F928;
// CPU tile roots — §4's second content writer, wired 2026-07-23.
//
// These read a LINEAR staging buffer and write a tiled destination, so they
// state two things the unlock root cannot: that content arrived for a
// resource the CPU never locked, and that the bytes they read need no untile
// (ContentPacket::source_linear = true).
//
// Why this had to be wired: before it, `content_images` had exactly ONE writer
// (the unlock range) while the replacement map claimed the tile linear-src was
// "landed". Run 159 measured the consequence — ~13k refreshes per run where
// the observer had bytes and the GTL had none, most of them tile-delivered.
// Deleting the observer capture machinery on the strength of that stale claim
// would have destroyed the only source of tile content.
//
// Only the TILE direction is wired. The UNTILE siblings (0x8300C660,
// 0x8300CDA0, 0x8300CDA8, 0x830112F0, 0x83011AE0) run the other way — tiled
// source to linear destination — so they read texture content rather than
// deliver it, and are not content writers.
// §4's FOURTH content writer — the streamed texture pool (found 2026-07-23).
//
// Run 180/181 classified the residual content gap and found 120 allocations
// `known=1 gen=0`: properly created (via XGRAPHICS::SetBaseTextureHeader) but
// with NO write root ever firing. This is that root. The observer already knew
// and says so in its own comment at the same hook: "the streamed texel bytes
// just landed in guest memory with no D3D call to observe".
//
// So the migration doc's "content only from Unlock ranges, XGTileTextureLevel
// linear src, Resolve host-copy" was INCOMPLETE — a fourth path delivers
// texture content with no D3D call at all, and the observer's fetch-constant
// scanning exists to compensate for exactly this. That scanning is therefore
// not deletable inference until this root is wired.
//
// Both roots return non-zero only when a stream-in actually completed, and the
// affected resource is reachable from the request by stated structure:
//   result_object = [request + 68]
//   resource      = [[result_object + {40,48}] + 4]
// NOT a completion callback. IDA 2026-07-27: this is TryCancel --
// sub_82A7AF68(StreamedTexture* self, char waitIfRunning) -> BOOL. It cancels
// the in-flight job, releases the PENDING texture-set at self+72, unlinks from
// the pending list and decrements the in-flight count at 0x83496E78. It never
// touches the CURRENT set at +68, so no bytes changed and there is nothing to
// publish. The old name caused us to bump a content generation ON CANCEL,
// against the previous texture.
// D3D::CDevice::KickOffSegment. It calls SetFence (0x82298630) to append the
// two EVENT_WRITE_SHD fence packets, then QueueIndirectBuffer to SUBMIT the
// segment. Its EXIT is therefore the first moment the segment carrying a fence
// has actually been handed over -- the correct retirement point. See
// RetireGuestGpuFenceLocked.
constexpr uint32_t kKickOffSegment = 0x821E8D38;
// D3D::CDevice::KickOff. Calls KickOffSegment internally, so on hardware ONE
// SetFence runs per KickOff. Under Plume the root is suppressed and its
// emulation calls EmulateFableKickOffSegment as a plain C++ call -- no nested
// guest hook fires -- so this root must retire the fence itself. Suppression is
// exactly what makes retiring at both roots safe rather than double-counting.
constexpr uint32_t kKickOff = 0x821E8EC0;
// The two guest waits that spin on the write-back block at *(dev+0x2A90).
// D3D_CDevice_BlockOnSecondaryPosition polls B[+4] (ring position) and is
// reached from D3D_RingBufferDeviceAllocate when the ring is full;
// D3D_CDevice_BlockOnFence polls B[+0] (retired fence) and is reached from
// KickOff, StartNewSegment and every D3D_LockResource of a busy resource.
// Retiring at their ENTER is what breaks the run-571 self-deadlock — see the
// long note in OnHookEnter.
constexpr uint32_t kBlockOnSecondaryPosition = 0x82206400;
constexpr uint32_t kBlockOnFence = 0x82242668;
// Render_FrameCompletionCallback (0x82B90160) state. Every guest Present inserts
// this as a TYPE-1 GPU RING callback via D3D_InsertCallback (0x82B6F274); on
// hardware the command processor fires it when that packet retires. Our port has
// no command processor, so it never fires and the whole frame-sync clock stays
// at its static-init zero. See AdvanceGuestFrameCompletionLocked.
constexpr uint32_t kFrameSyncRecordBase = 0x83491D98;  // 2 records, stride 24
constexpr uint32_t kFrameSyncRecordStride = 24u;
constexpr uint32_t kFrameSyncRecordTimestamp = 0x00u;  // u64, mftb ticks
constexpr uint32_t kFrameSyncRecordPrevTimestamp = 0x08u;  // u64
constexpr uint32_t kFrameSyncRecordCounter = 0x10u;    // u32, +1 per completion
constexpr uint32_t kFrameSyncPublishedIndex = 0x83497124;  // u32, 0 or 1
// These three are written by the guest's OWN Present body — which under Plume is
// SUPPRESSED (rex_lhRenderer_EndScene -> EmulateFableEndScenePresentBoundary
// returns handled=true, so 0x82B6F1D0 never executes). Nothing else in the tree
// writes them (the observer only reads them), so the GTL must perform these
// writes too or they stay at their static-init zero forever.
constexpr uint32_t kGuestPrevPresentTimebase = 0x83491DC8;  // u64 (0x82B6F224)
constexpr uint32_t kGuestLastPresentTimebase = 0x83491DD0;  // u64 (0x82B6F230)
constexpr uint32_t kGuestPresentFrameCount = 0x83497120;    // u32 (0x82B6F290)
constexpr uint32_t kStreamedLoadCancel = 0x82A7AF68;
constexpr uint32_t kStreamedResidencyPoll = 0x82A7B148;
constexpr uint32_t kTileSurface = 0x8300C290;         // XGRAPHICS__TileSurface
constexpr uint32_t kTileSurfaceThunk = 0x8300CB40;    // j_XGRAPHICS__TileSurface
constexpr uint32_t kTileTextureLevel = 0x8300CB48;    // XGTileTextureLevel
constexpr uint32_t kTileVolume = 0x83010E60;          // XGRAPHICS__TileVolume
constexpr uint32_t kTileVolumeTextureLevel = 0x830117B8;
// Header-init creation roots: Fable mostly creates textures by initializing
// 52-byte Xenos headers in place rather than through factory calls. The
// header out-pointer rides the stack for the wide variants (offsets pinned
// by the observer's earlier IDA work: +0x6C inline builder, +0x7C XG
// variants) and r10 for line/cube.
constexpr uint32_t kSetTextureHeaderInline = 0x821FB650;
constexpr uint32_t kSetBaseTextureHeader = 0x821FB968;
constexpr uint32_t kXGSetTextureHeader = 0x821FBB78;
constexpr uint32_t kXGSetLineTextureHeader = 0x8300BEE0;
constexpr uint32_t kXGSetCubeTextureHeader = 0x8300BF48;
// XGOffsetBaseTextureAddress(D3DBaseTexture *pTexture /*r3*/,
//   void *pBaseAddress /*r4*/, void *pMipAddress /*r5*/) — Lumen-named
// (0x8225FDF0), prototype recovered from IDA's XDK types.
//
// This re-points an EXISTING header at new memory, and it is how Fable's
// reused scratch headers get their identity. It only ever dispatches under the
// rex_ shim name, so SymbolAddress() must map it explicitly (see the trap note
// there). Measured 2026-07-17 (run 439): resolve destination header 0x40130CD0
// is defined by exactly this root, and unregistered_dest stayed at 100% (544 of
// 544) precisely because the GTL never saw it.
constexpr uint32_t kOffsetBaseTextureAddress = 0x8225FDF0;
// THE SINGLE-LEVEL SIBLING (2026-08-04). Texture_InitHeaderWrapper 0x821FBD90
// stamps the base through XGOffsetResourceAddress /*0x821fbe98*/ when Levels ==
// 1 and XGOffsetBaseTextureAddress /*0x821fbea8*/ when Levels > 1 — and every
// scratchpad render target is single-level, so they ALL took the root above
// that we were not receiving. Same trap as the 2026-07-17 fix, one function
// along. Measured run 603: `slot=13 tracked=0 image=0 ready=0` x287, the
// unbacked texture the last draw of every frame blends over the whole screen.
constexpr uint32_t kOffsetResourceAddress = 0x8225FD18;

constexpr uint32_t kPresent = 0x82B6F1D0;
constexpr uint32_t kDisplaySwap = 0x82BA34D8;
// Clear roots (register mapping mirrors the observer's QueueClearEventLocked
// call sites). kClearF carries an explicit D3DCLEAR mask; kClearRect is a
// color-slot-0 fill whose depth/stencil are owned by the ClearF path.
// IDB-named 2026-07-18: kClearF = D3DDevice_ClearF(device, Flags r4,
// D3DRECT* r5, D3DVECTOR4* color r6, Z f1, Stencil r8 — 8-byte arg slots skip
// r7 for the double); null rect = full current-RT extent.
// kClearRect = D3D_ClearRect(device, LEFT r4, TOP r5, RIGHT r6, BOTTOM r7,
// color* r8, depth f1) — rect args are LTRB (decompile-proven), never x/y/w/h.
constexpr uint32_t kClearF = 0x821E2FE0;
constexpr uint32_t kClearRect = 0x821EF830;
// Title-path draw roots: inline-vertex submissions carry most GUI/storybook
// draws (device state applies identically).
constexpr uint32_t kBeginVertices = 0x82205F68;
constexpr uint32_t kBeginIndexedVertices = 0x82217DB8;
// D3DDevice_BeginIndexedVertices' 9th argument (ppVertexData) is a STACK arg.
// Xbox 360 ABI: parameters are homed in 8-byte slots from caller_sp+0x10, so
// argument N's low word sits at caller_sp + 0x14 + 8*(N-1); N=9 -> +0x54.
// Confirmed against the guest's own load of it — `lwz r9, 0xE0+arg_54(r1)` at
// 0x82218288, inside a 0xE0 frame — and consistent with the resolve root's
// ClearStencil read at +0x5C (its 10th argument). r1 at hook entry is the
// caller's sp (pre-prologue), the same assumption the resolve capture makes.
constexpr uint32_t kBeginIndexedVerticesVertexOutStackOffset = 0x54u;
// D3DDevice_Resolve (IDA-named 2026-07-17): the Xenos EDRAM resolve. Formerly
// misnamed kPrivateTextureRectangleDraw and modelled as a draw/blit — it is
// neither. Reached directly, via D3D__ResolveColor (0x822326E0, Flags=0/RT0),
// D3D__ResolveDepth (0x822A0A78, Flags=4/DS), and once PER TILE from
// D3DDevice_EndTiling (0x8227F3B0).
constexpr uint32_t kResolve = 0x82206888;
// DrawIndexedUserPrimitives(vertex_count, prim_count, index_ptr, vertex_ptr,
// stride): ring-allocates via kBeginIndexedVertices then memcpys the caller's
// index/vertex payload before returning. 26 callers: the bink movie
// compositor plus the UI/storybook quad families.
constexpr uint32_t kDrawIndexedUserPrimitives = 0x8220A3F0;

// The two PM4 ring writers that bypass every hooked D3D draw root (see
// TessellatedPatchDrawPacket in refii_gtl.h for the full IDA map).
//   0x82207AF8 D3D_EmitDrawIndexedPackets_INFERRED   terrain, DMA-indexed
//   0x8221C898 D3D_EmitDrawPrimitivePackets_INFERRED water, auto-index
// CAPTURE-ONLY: registered so the shape can be measured. Adding them changes
// nothing that renders -- neither symbol is in any IsNativeRenderWorkHook list,
// so the guest body still runs and still writes its own packets.
constexpr uint32_t kEmitTessellatedIndexedPackets = 0x82207AF8;
constexpr uint32_t kEmitTessellatedPrimitivePackets = 0x8221C898;

// Device-object offsets used by the tessellated-patch capture. Every one is
// read off a literal in the two emitters or in the function that consumes it;
// see the header for the per-field evidence.
constexpr uint32_t kDeviceRingWritePointer = 0x30u;   // m_pRing
constexpr uint32_t kDeviceRingGuarantee = 0x38u;      // m_pRingGuarantee
// GPU_CONTROLPACKET::HiControl (m_ControlPacket at 0x2934, HiControl at +0xC) =
// the Xenos register 0x2203 Hi-Z / Hi-Stencil control shadow. It is the third
// argument D3D_SetPending_Predicated receives (BeginVertices: `lwz r5,
// 0x2940(r31)` at 0x82206050), which is why it was mislabelled a predication
// mask until 2026-07-26; see TessellatedPatchDrawPacket::hi_z_control for the
// full writer list.
constexpr uint32_t kDeviceHiZControl = 0x2940u;
// Real predication state. Raw app mask lo/hi, then the resolved value the
// driver actually emits as SET_BIN_SELECT_LO, then the enable flag. All four
// are written by both D3DDevice_SetPredication (0x822655F0) and the ring-direct
// D3D_SetTilePredication_INFERRED (0x82BA1D40).
constexpr uint32_t kDevicePredicationMaskLo = 0x2AB4u;
constexpr uint32_t kDevicePredicationMaskHi = 0x2AB8u;
constexpr uint32_t kDevicePredicationBinSelect = 0x31A4u;
constexpr uint32_t kDevicePredicationEnabled = 0x31ACu;
constexpr uint32_t kDeviceTiledVariantFlag = 0x2ABCu;
constexpr uint32_t kDeviceVizQueryIndex = 0x2F9Bu;
constexpr uint32_t kDeviceIndexBufferSlot = 0x3094u;  // D3DDevice_SetIndices
// Readability extent for a whole-device probe. Not the true object size --
// just past the highest binding slot we read (kDevicePredicationEnabled at
// 0x31AC), so one IsReadableGuestByteRange call covers every device field the
// packet builders touch.
constexpr uint32_t kFableDeviceObjectExtent = 0x31B0u;
// PROVENANCE: the shadow-base -> Xenos-register-BASE mapping below is VERIFIED
// from the binary (each base is the pPacket argument the emitters pass
// alongside a literal regBase to D3D_SetPending_RenderStates, which copies
// shadow[i] into register regBase+i). The individual register NAMES used in the
// diagnostic (RB_SURFACE_INFO, VGT_OUTPUT_PATH_CNTL, ...) come from the public
// Xenos register map, not from this IDB -- so the log always prints the raw
// register number beside the name and the raw PM4 dwords beside the decode.
// The tessellation block's names are strongly corroborated: the emitters' eight
// literal stores land exactly on the offsets that map predicts for
// OUTPUT_PATH_CNTL / HOS_CNTL / GROUP_PRIM_TYPE / GROUP_*_DECR / GROUP_VECT_*,
// with values (path select 1, prim type 14, decr 4-or-1) that match their
// documented meanings.
constexpr uint32_t kDeviceRegShadowRbSurface = 0x2880u;  // Xenos reg 0x2000+i
constexpr uint32_t kDeviceRegShadowPaVgt = 0x28CCu;      // Xenos reg 0x2100+i
constexpr uint32_t kDeviceRegShadowSq = 0x2934u;         // Xenos reg 0x2200+i
constexpr uint32_t kDeviceRegShadowVgtTess = 0x2964u;    // Xenos reg 0x2280+i
constexpr uint32_t kDeviceRegShadowRbMode = 0x29B8u;     // Xenos reg 0x2300+i
constexpr uint32_t kDeviceRegShadowRbCopy = 0x2A50u;     // Xenos reg 0x2380+i
constexpr uint32_t kDeviceVsFloatConstantShadow = 0x780u;  // Xenos reg 0x4000
// g_ShaderBindingTable: 12-byte entries {constant_register, count, ...}. IDA
// sub_822239F8 (the setter both emitters' callers use) indexes it by the
// BINDING index its callers pass (332 / 343 / 396) and writes the float at
// device + 0x780 + 16*constant_register.
constexpr uint32_t kShaderBindingTable = 0x834BDED8u;
constexpr uint32_t kShaderBindingTableStride = 12u;

// Fable's own D3D render-state DEFAULTS, read verbatim from the guest's
// {getter, setter, default} triple table at 0x83315E70 (IDA, 2026-08-02).
// Triples are 12 bytes and a state's triple sits at (Xbox-360 D3DRS enum * 3)
// bytes into the table -- D3DRS_ZFUNC = 44 -> +132, whose setter dword is
// 0x821BFDF0 (D3DDevice_SetRenderState_ZFunc) and whose default dword is 3.
// D3D_ConfigureDeviceState (0x82BA6298) installs these into the device at
// creation, so they are the values in force for every state the title never
// writes itself. Slot numbering is kFableRenderStateAddressSlots
// (fable_hook_symbols.cpp), matched to the table by setter address.
//
// WHY THIS EXISTS -- the black loading screen / black gameplay (runs 520-530):
// this mirror was zero-initialised. Fable never dispatches ZFUNC (run 530:
// 147k state dispatches, bad_slot=2, and the slot still read 0), so every draw
// packed RB_DEPTHCONTROL with ZFUNC=0, which is the raw Xenos enum for NEVER
// -- the setter stores the value unbiased as (value << 4) & 0x70, so 0 is a
// real NEVER, not a spare "unset" encoding. The pipeline builder faithfully
// produced VK_COMPARE_OP_NEVER, so every DEPTH-TESTED draw was discarded after
// its pixel shader ran (RenderDoc pixel history on the loading page's first
// quad: shaderOut=(0.023,0.021,0.018,1) depthTestFail=TRUE, postMod unchanged),
// while depth-DISABLED draws -- menus, Bink video, blits, the post chain --
// were untouched. That is exactly the observed split: title/menu/video fine,
// loading screen and world black.
//
// The observer carried a hand-written baseline for the old pipeline
// (hook_observer.cpp ResetObservedRenderStatesToDefaultsLocked) which died
// with the feed deletion (migration step 3, 2026-07-26). Do not copy it: it
// disagrees with the binary (HiZWriteEnable 0 vs the table's 2). The table is
// the authority.
//
// Values only; the valid mask still means "the guest explicitly wrote this",
// so mask-gated consumers (e.g. the cull snap at kSnapCullRenderStateSlot)
// keep their current meaning. NOTE: D3D_ResetAllState re-imposes these on a
// device reset; if a title-lifetime reset ever shows up in a log, re-seed here
// as well.
constexpr std::array<uint32_t, kMaxRenderStates> MakeDefaultRenderStates() {
  std::array<uint32_t, kMaxRenderStates> states{};
  // Every slot not named below has a table default of 0.
  states[0] = 3u;           // ZFunc                    LEQUAL (Xenos enum)
  states[1] = 0xFu;         // ColorWriteEnable         all channels
  states[3] = 1u;           // SrcBlend                 ONE
  states[4] = 1u;           // ZWriteEnable             on
  states[7] = 1u;           // ZEnable                  on
  states[8] = 2u;           // HiZWriteEnable
  states[11] = 1u;          // SrcBlendAlpha            ONE
  states[19] = 6u;          // CullMode
  states[21] = 7u;          // StencilFunc              ALWAYS
  states[22] = 0xFFFFFFFFu; // StencilMask
  states[24] = 7u;          // AlphaFunc                ALWAYS
  states[27] = 0xFFFFFFFFu; // StencilWriteMask
  states[30] = 2u;          // PackedBlendControl20
  return states;
}

struct DeviceState {
  std::array<uint32_t, kMaxTextureSlots> textures = {};
  std::array<uint32_t, kMaxStreamSlots> vertex_buffers = {};
  std::array<uint32_t, kMaxStreamSlots> vertex_offsets = {};
  std::array<uint32_t, kMaxStreamSlots> vertex_strides = {};
  uint32_t index_buffer = 0;
  uint32_t vertex_shader = 0;
  uint32_t pixel_shader = 0;
  uint32_t vertex_declaration = 0;
  std::array<uint32_t, kMaxRenderTargets> render_targets = {};
  uint32_t depth_target = 0;
  std::array<uint32_t, 6> viewport = {};
  std::array<uint32_t, 4> scissor = {};
  std::array<uint32_t, kMaxRenderStates> render_states =
      MakeDefaultRenderStates();
  uint32_t render_state_valid_mask = 0;
  // Packed sampler state per D3D sampler slot, fed from the SetSamplerState
  // roots (same packing the direct pipeline consumes) — at-draw truth so the
  // converter never reads drain-time current state (stale-state class).
  std::array<uint32_t, kMaxSamplerSlots> sampler_states = {};
  uint32_t sampler_state_valid_mask = 0;
  std::array<uint32_t, kMaxStreamSlots> vertex_buffer_pointers = {};
  // Index buffer object's data pointer (object+0x18, decoded) resolved at
  // SetIndices — same domain the command pipeline binds.
  uint32_t index_buffer_pointer = 0;
  std::array<uint32_t, 32> fetch_bases = {};
  // Dispatch-fed float constant banks (1024 dwords each) — the at-draw
  // truth; the device-memory read in BuildDrawPacketLocked is the fallback
  // until the first dispatch lands per bank.
  std::array<uint32_t, 1024> vs_float_bank = {};
  std::array<uint32_t, 1024> ps_float_bank = {};
  bool vs_float_bank_valid = false;
  bool ps_float_bank_valid = false;
  // Shader-literal ALU overlay (see ApplyLiteralShaderConstants). The whole
  // 512-register file: regs 0-255 vertex, 256-511 pixel, 4 dwords each.
  // `covered` marks the registers a literal has supplied; those override the
  // device-bank read at packet build, because the guest's literal load runs
  // after the shadow flush and lands directly in the GPU constant file.
  std::array<uint32_t, 2048> literal_alu = {};
  std::array<uint8_t, 512> literal_alu_covered = {};
  uint64_t literal_alu_registers_written = 0;
};

struct PendingCreate {
  uint32_t symbol_address = 0;
  // Caller return address, carried so the created resource can name the
  // subsystem that made it (see Resource::create_lr).
  uint32_t lr = 0;
  uint32_t r3 = 0;
  uint32_t r4 = 0;
  uint32_t r5 = 0;
  uint32_t r6 = 0;
  uint32_t r7 = 0;
  uint32_t r8 = 0;
  // OnHookExit receives no guest_base, and the VB/IB roots deliver their object
  // through an out-descriptor that can only be dereferenced at exit. Carry the
  // base from enter, same shape as t_pending_stream_base.
  uint8_t *guest_base = nullptr;
};

struct State {
  std::mutex mutex;
  std::unordered_map<uint32_t, Resource> resources;
  DeviceState device;
  std::vector<DrawPacket> packets;
  std::vector<ResolvePacket> resolve_packets;
  std::vector<ClearPacket> clear_packets;
  std::vector<ContentPacket> content_packets;
  // Capture-only stream (§ TessellatedPatchDrawPacket). Capped in the writer.
  std::vector<TessellatedPatchDrawPacket> tess_packets;
  // Shape keys already reported by the characterisation diagnostic. Bounded so
  // a pathological per-draw-unique key cannot grow this without limit.
  std::unordered_set<uint64_t> tess_logged_shapes;
  // Content store, keyed by (resource << 1) | mip_range. One entry per
  // ALLOCATION, because the unlock root states base and mip separately and
  // they are separate memory.
  //
  // The layout rides WITH the bytes because it is a property of the root that
  // supplied them, not of the resource: an Unlock range is the resource's own
  // (tiled) memory, while a tile call's source is a linear staging buffer.
  // The same resource can legitimately receive both over its lifetime, and
  // whichever wrote LAST is what the store holds.
  struct ContentImage {
    std::vector<uint8_t> bytes;
    bool source_linear = false;
    // `from_ingest` DELETED 2026-08-04 along with the rest of the fetch-store
    // ingest residue: its only producer (IngestBoundTextureContent) was removed
    // 2026-07-26, so nothing ever set it true and the flag's consumer had
    // already gone with it. Every remaining site only assigned false.
  };
  std::unordered_map<uint64_t, ContentImage> content_images;
  uint64_t content_image_bytes = 0;
  Stats stats;
  uint64_t next_packet_id = 1;
  uint64_t frame_id = 0;
  uint64_t next_create_serial = 1;
  // STATED present source (run 199): render_targets[0] at the guest Present
  // (0x82B6F1D0) — the RT0 the guest resolves into g_FrontBufferTexture and
  // swaps. `epoch` increments once per guest Present so the present pump can
  // hold a frame between guest Presents instead of showing partial composes.
  uint32_t stated_present_rt0 = 0;
  uint32_t stated_present_front_buffer = 0;
  uint32_t stated_present_front_buffer_base = 0;
  uint64_t stated_present_epoch = 0;
  // Last device object seen at a draw root (args.r3; the D3D device is a
  // singleton, so remembering it is safe). Present (0x82B6F1D0) is a Fable
  // wrapper whose r3 is NOT the device, and the stated present source needs
  // the device to LIVE-READ the RT0 register at 0x3098 — see the kPresent
  // handler.
  uint32_t last_draw_device_pointer = 0;
  // The next_packet_id at the last guest Present — the boundary up to which all
  // packets form ONE complete guest frame. The pump drains up to here.
  uint64_t guest_frame_boundary_packet_id = 0;
  // Nesting depth into the guest's scene-submit wrapper functions (mirrors
  // hook_observer.cpp's g_observer.scene_submit_depth off the SAME hook
  // stream -- see IsFableGtlSceneSubmitScope below). Stamped onto each
  // DrawPacket at capture so BuildDirectCommandFromGtlDrawPacketLocked can
  // assign RendererDrawEventSource::kSceneObject the way the observer does.
  uint32_t scene_submit_depth = 0;

  // ---- RESOURCE CHANGE JOURNAL (§ GetFableDirectResourceChanges) ----------
  // The direct renderer syncs INCREMENTALLY: it asks "what changed since the
  // serial I last acknowledged" and only re-materializes those. The journal is
  // the GTL's own answer, fed from the roots that already mutate the registry
  // (create, header init, unlock/tile content, resolve destination, release).
  // It is NOT a scan and NOT a diff -- an entry exists because a stated call
  // produced it.
  //
  // Serial semantics match the consumer's protocol exactly:
  //   * every mark stamps a fresh monotonic serial on the address;
  //   * GetChanges is NON-DESTRUCTIVE (the consumer makes several sync calls
  //     per compose off one journal state) and reports the current watermark;
  //   * AcknowledgeChanges(serial) retires only entries stamped <= that
  //     watermark, so a change landing mid-compose survives the ack;
  //   * destroys are acked per-address because the consumer retires them under
  //     its own budget and defers the overflow.
  uint64_t change_serial = 0;
  std::unordered_map<uint32_t, uint64_t> changed_resources;
  std::unordered_map<uint32_t, uint64_t> destroyed_resources;
  // Census: counters, not a per-address log. Non-zero `changes_overflowed`
  // means acknowledgement stopped and the journal was capped -- the sync then
  // degrades to the requested-address path, never to silence.
  uint64_t changes_marked = 0;
  uint64_t changes_overflowed = 0;
  uint64_t snapshot_calls = 0;
  uint64_t snapshots_served = 0;
};

State g_state;

// Journal bound. Both maps are drained every compose by the direct renderer's
// acknowledge calls; the cap only exists so a consumer that stops acking
// cannot grow the GTL without limit (the depth-stencil eviction leak class).
constexpr size_t kMaxJournalledResources = 65536;

// Stamp an address as changed. Called from the registry-mutating roots, always
// with g_state.mutex already held.
void MarkResourceChangedLocked(uint32_t guest_address) {
  if (guest_address == 0) {
    return;
  }
  if (g_state.changed_resources.size() >= kMaxJournalledResources &&
      g_state.changed_resources.find(guest_address) ==
          g_state.changed_resources.end()) {
    g_state.changed_resources.clear();
    ++g_state.changes_overflowed;
  }
  const uint64_t serial = ++g_state.change_serial;
  g_state.changed_resources[guest_address] = serial;
  if (auto it = g_state.resources.find(guest_address);
      it != g_state.resources.end()) {
    it->second.change_serial = serial;
  }
  ++g_state.changes_marked;
}

void MarkResourceDestroyedLocked(uint32_t guest_address) {
  if (guest_address == 0) {
    return;
  }
  // A destroyed address has nothing left to re-sync; the destroy supersedes
  // any pending change for it.
  g_state.changed_resources.erase(guest_address);
  if (g_state.destroyed_resources.size() >= kMaxJournalledResources &&
      g_state.destroyed_resources.find(guest_address) ==
          g_state.destroyed_resources.end()) {
    g_state.destroyed_resources.clear();
    ++g_state.changes_overflowed;
  }
  g_state.destroyed_resources[guest_address] = ++g_state.change_serial;
}

// Registered by the present pump; invoked at the guest Present (kPresent) so the
// pump runs guest-driven. Declared here (before OnHookEnter uses it) rather than
// beside its setter further down.
std::atomic<GuestFramePresentedCallback> g_guest_frame_presented_callback{
    nullptr};

// DropFetchIngestedContentLocked DELETED 2026-08-04. It dropped bind-time
// ingest snapshots when a resolve or render-target bind meant the observer now
// arbitrated the object's content — but its guard was
// `if (!resource.content_from_fetch_ingest) return;`, and that flag has had no
// writer setting it TRUE since IngestBoundTextureContent was removed
// 2026-07-26. So the function returned immediately on every one of its calls.
// Its two call sites (the resolve destination and kSetRenderTarget) are gone
// with it; the surrounding bookkeeping at both is unchanged.

thread_local PendingCreate t_pending_create;
// pThis of an in-flight D3DResource_Release; the erase decision needs the
// return value (the new refcount), which is only known at exit.
thread_local uint32_t t_pending_release_object = 0;

// Subresource view being created, stashed at enter so the exit (which carries
// the returned view object in r3) can record the stated parent/face/level.
struct PendingViewIdentity {
  uint32_t parent_texture = 0;
  uint32_t face = 0;
  uint32_t level = 0;
};
thread_local PendingViewIdentity t_pending_view_identity;
// Begin-family geometry pin: the wrapper returns the ring write pointer and
// the CALLER writes the payload before its next D3D call (the guest
// contract the observer's delayed-finalize approximates). Stash the ring
// pointer at the Begin exit; copy the completed payload into the packet at
// this thread's next GTL hook entry.
struct PendingBeginPin {
  uint64_t packet_id = 0;
  uint32_t ring_pointer = 0;
  uint32_t byte_size = 0;
  uint32_t vertex_count = 0;
  // BeginIndexedVertices only: that root allocates a SECOND ring for the
  // indices and hands it back through *ppIndexData. Zero for BeginVertices,
  // which has no index ring at all.
  uint32_t index_pointer = 0;
  uint32_t index_byte_size = 0;
};
thread_local PendingBeginPin t_pending_begin_pin;
// D3DDevice_BeginIndexedVertices (0x82217DB8) OUT-PARAMETER ADDRESSES, stashed
// at enter because neither survives to the exit: r10 is volatile, and the guest
// pops its frame (`addi r1, r1, 0xE0`) before returning. IDA-verified — this
// root returns an HRESULT in r3 (0 on success, 0x8007000E when either ring
// allocation fails), NOT a ring pointer, and delivers both allocations via
//   *ppIndexData  (r10)                        <- index ring
//   *ppVertexData (caller_sp + 0x54, see above) <- vertex ring
// (guest stores: `stw r26, 0(r17)` and `stw r28, 0(r9)` at 0x82218290/0x8221829C).
// Written only on the STANDALONE capture path (BuildDrawPacketLocked), so a
// nested allocation inside a wrapper never arms a pin.
thread_local uint32_t t_begin_indexed_index_out_address = 0;
thread_local uint32_t t_begin_indexed_vertex_out_address = 0;
// Per-family packet-completeness census (staged observer-removal aid). Keyed
// by draw-root symbol; guarded by g_state.mutex (only touched inside locked
// GTL entry points).
struct FamilyCoverage {
  uint64_t total = 0;
  uint64_t geom = 0;     // inline UP geometry present
  uint64_t streamvb = 0; // a bound vertex buffer present
  uint64_t ident = 0;    // both shaders set
  uint64_t decl = 0;     // vertex declaration set
  uint64_t tex0 = 0;     // slot-0 texture bound
  uint64_t rt0 = 0;      // slot-0 render target set
};
std::unordered_map<uint32_t, FamilyCoverage> g_family_coverage;
// Begin-family pin outcome census (staged observer-removal aid). The pin is the
// ONLY capture point for BeginVertices/BeginIndexedVertices geometry (there is
// no D3DDevice_EndVertices in the title), so a pin that does not fire leaves the
// draw geometry-less and it falls back to the observer. Run 203 measured only 8
// pins against 678 BeginVertices draws; these counters split the 670 losses by
// exact cause so the fix targets the real one. Atomics: touched on the guest
// thread at arm (Begin exit) and drain (next hook enter), read in OnFrameBoundary.
struct BeginPinCensus {
  std::atomic<uint64_t> arm_reached{0};       // Begin-family exit seen
  // arm_reject_depth tests t_draw_indexed_up_depth ONLY (see the exit site) —
  // NOT t_begin_vertices_depth. A large value is EXPECTED and healthy: it
  // equals the sub_8220A3F0 call total exactly, and those draws are packeted
  // with full inline geometry at the wrapper. arm_ok correspondingly equals the
  // sub_82205F68 (standalone BeginVertices) call total exactly — i.e. the pin
  // fires for 100% of eligible calls. Verified runs 271/274/282/283.
  std::atomic<uint64_t> arm_reject_depth{0};  // inside a DrawIndexedUP wrapper
  std::atomic<uint64_t> arm_reject_symbol{0}; // last packet not this Begin
  std::atomic<uint64_t> arm_reject_bytes{0};  // count*stride == 0 or too big
  std::atomic<uint64_t> arm_ok{0};            // t_pending_begin_pin armed
  std::atomic<uint64_t> drain_reached{0};     // next enter saw an armed pin
  std::atomic<uint64_t> drain_unreadable{0};  // ring range not guest-readable
  std::atomic<uint64_t> drain_blank{0};       // ring still all-zero (unwritten)
  std::atomic<uint64_t> drain_packet_gone{0}; // packet already drained to pump
  std::atomic<uint64_t> drain_ok{0};          // geometry copied into packet
};
BeginPinCensus g_begin_pin_census;
// Set by BuildDrawPacketLocked for exit pairing (Begin-family pins).
thread_local uint64_t t_last_packet_id = 0;
thread_local uint32_t t_last_packet_symbol = 0;
// Device pointer and guest base captured when the packet was built, so the
// matching EXIT can re-read staged state. Needed because OnHookExit takes no
// guest_base, and at a Begin-family exit r3 is the returned RING POINTER, not
// the device.
thread_local uint32_t t_last_packet_device = 0;
thread_local uint8_t *t_last_packet_guest_base = nullptr;
// Per-root stash for the AUTHORITATIVE EXIT SELF-READ (step 2, 2026-07-26).
//
// t_last_packet_* is a SINGLE slot and is displaced by any nested hooked call,
// so it cannot carry the non-Begin roots across their own bodies:
//   * kDrawPrimitiveUP (0x82265CD8) calls D3DDevice_BeginVertices, which builds
//     its own packet (displacing t_last_packet_symbol) and then CLEARS
//     t_last_packet_id at its exit — so by the wrapper's exit there is nothing
//     left to pair with.
// One slot per root symbol (never the same root nested inside itself), so a
// wrapper and its callee cannot collide. Three roots use it: kDrawPrimitive,
// kDrawIndexedPrimitive and kDrawPrimitiveUP. The Begin family keeps its
// existing t_last_packet_* pairing untouched (that slot is also what arms the
// ring pin, so it must stay), and kDrawIndexedUserPrimitives keeps resolving
// its device from the g_D3DDevice_PresentModule singleton at exit — its r3 is
// a vertex count, not a device, so a stashed r3 would be garbage.
struct PendingExitSelfRead {
  uint32_t symbol_address = 0;
  uint64_t packet_id = 0;
  uint32_t device = 0;
  uint8_t *guest_base = nullptr;
};
thread_local std::array<PendingExitSelfRead, 3> t_pending_exit_self_reads{};

void RecordPendingExitSelfRead(uint32_t symbol_address, uint64_t packet_id,
                               uint32_t device, uint8_t *guest_base) {
  PendingExitSelfRead *free_slot = nullptr;
  for (PendingExitSelfRead &slot : t_pending_exit_self_reads) {
    if (slot.symbol_address == symbol_address) {
      free_slot = &slot;
      break;
    }
    if (free_slot == nullptr && slot.symbol_address == 0) {
      free_slot = &slot;
    }
  }
  if (free_slot == nullptr) {
    return;
  }
  *free_slot = {symbol_address, packet_id, device, guest_base};
}

PendingExitSelfRead TakePendingExitSelfRead(uint32_t symbol_address) {
  for (PendingExitSelfRead &slot : t_pending_exit_self_reads) {
    if (slot.symbol_address == symbol_address) {
      const PendingExitSelfRead taken = slot;
      slot = {};
      return taken;
    }
  }
  return {};
}

// Header-init exit pairing. Separate from t_pending_exit_self_reads because the
// header roots NEST — XGSetTextureHeader is a thin shim onto
// XGRAPHICS_SetBaseTextureHeader, which itself calls D3D_SetTextureHeader — so a
// single slot would be displaced by the inner call before the outer exit ran.
// One slot per root symbol; a root is never nested inside itself.
//
// Why an exit pairing is needed at all: the 52-byte header write is a memcpy at
// 0x821FBB64 inside XGRAPHICS_SetBaseTextureHeader's body, so at hook ENTER the
// block still holds the OUTGOING tenant's bytes (the allocator does not zero
// recycled blocks, and the free list writes no field on push or pop).
struct PendingHeaderInitRead {
  uint32_t symbol_address = 0;
  uint32_t object = 0;
  uint8_t *guest_base = nullptr;
};
thread_local std::array<PendingHeaderInitRead, 5> t_pending_header_init_reads{};

void RecordPendingHeaderInitRead(uint32_t symbol_address, uint32_t object,
                                 uint8_t *guest_base) {
  if (symbol_address == 0 || object == 0 || guest_base == nullptr) {
    return;
  }
  PendingHeaderInitRead *free_slot = nullptr;
  for (PendingHeaderInitRead &slot : t_pending_header_init_reads) {
    if (slot.symbol_address == symbol_address) {
      free_slot = &slot;
      break;
    }
    if (free_slot == nullptr && slot.symbol_address == 0) {
      free_slot = &slot;
    }
  }
  if (free_slot == nullptr) {
    return;
  }
  *free_slot = {symbol_address, object, guest_base};
}

PendingHeaderInitRead TakePendingHeaderInitRead(uint32_t symbol_address) {
  for (PendingHeaderInitRead &slot : t_pending_header_init_reads) {
    if (slot.symbol_address == symbol_address) {
      const PendingHeaderInitRead taken = slot;
      slot = {};
      return taken;
    }
  }
  return {};
}
// Tessellated-patch emitters. LIFETIME MATTERS HERE (the hard-won rule):
//  - The VGT tessellation register block (device+0x2964) is written by the
//    emitter's own prologue, so at ENTER it still holds the PREVIOUS draw's
//    topology. It is only this draw's contract at EXIT.
//  - The pending/dirty masks at device+0x00/0x08/0x10/0x18/0x20 are CLEARED by
//    the emitter as it flushes them into the ring, so they are only readable at
//    ENTER. Same class as the RB_COPY registers being destroyed before return.
//  - The ring write pointer (device+0x30) advances across the call; the dwords
//    between the enter and exit values ARE the emitted packet stream.
// So the capture is split: arguments + dirty masks + ring-begin at enter,
// everything else at exit.
struct PendingTessellatedPatchCapture {
  uint32_t symbol_address = 0;
  uint32_t device = 0;
  uint32_t caller_lr = 0;
  uint32_t arg_primitive_type = 0;
  uint32_t arg_index_offset = 0;
  uint32_t arg_count = 0;
  uint32_t ring_begin = 0;
  uint8_t *guest_base = nullptr;
  // PHASE 1 (CAPTURE-ONLY): everything else about these two emitters is read
  // at EXIT, but the pending dirty masks do not survive that far -- the
  // emitter drains them -- so the device snapshot has to be taken at entry and
  // carried across in the stash. See DrawDeviceSnapshot.
  DrawDeviceSnapshot device_snapshot;
};
thread_local PendingTessellatedPatchCapture t_pending_tess_capture;
// Streamed-pool completion is decided by the RETURN value, so it has to be
// handled at exit — where OnHookExit is given no guest_base. Stash the request
// pointer and the base at enter, the same shape as t_pending_create.
thread_local uint32_t t_pending_stream_request = 0;
thread_local uint32_t t_pending_kickoff_device = 0;
thread_local uint8_t *t_pending_kickoff_base = nullptr;
thread_local uint8_t *t_pending_present_base = nullptr;
thread_local uint8_t *t_pending_stream_base = nullptr;
// Nonzero while this thread is inside a DrawIndexedUserPrimitives call: the
// wrapper's inner kBeginIndexedVertices allocation belongs to the same draw,
// which is already packeted (with pinned geometry) at the wrapper — one
// draw, one packet.
thread_local uint32_t t_draw_indexed_up_depth = 0;
// Nonzero while inside a BeginVertices wrapper: its inner core-allocator
// call (kBeginIndexedVertices) belongs to the same draw — one draw, one
// packet (the inner packet's r5/r7 are allocator args, not count/stride,
// which is why the exit stash refused and pins never fired in run 117).
thread_local uint32_t t_begin_vertices_depth = 0;
// Nesting guard for the tile roots. XGTileTextureLevel ends by calling
// XGRAPHICS_TileSurface, so the inner call is the SAME operation seen again —
// but only when it is nested. TileSurface is also a public XDK entry point the
// guest may call directly, and those are real, separate operations.
//
// Run 175 settled which half to keep, by regressing to packets=0 when the
// inner was chosen: the mip-tail fixup SUBTRACTS
// (`pDestination -= GetMipTailLevelOffsetCoords(...)`, outer 0xF399B040 ->
// inner 0xF399B000), so the inner pointer is rebased and only lands correctly
// once the point coords are added back. The OUTER destination is the one that
// names a texture allocation.
// A FLAG, not a counter, deliberately. The nesting is provably one level deep
// (TileSurface never calls back into TextureLevel), and a counter that ever
// misses a decrement grows without bound and suppresses direct TileSurface
// calls for the life of the thread — which would read as "the guest stopped
// tiling" rather than as a bug. A flag re-arms on the next outer enter and
// clears on its exit, so a missed exit self-heals within one operation.
thread_local bool t_in_tile_texture_level = false;

uint32_t SymbolAddress(const char *symbol) {
  if (!symbol) {
    return 0;
  }
  if (std::strncmp(symbol, "sub_", 4) == 0) {
    return static_cast<uint32_t>(std::strtoul(symbol + 4, nullptr, 16));
  }
  // Named shims (rexglue-symbolized roots) dispatch under rex_* names; map
  // the GTL-owned ones onto their twin addresses.
  //
  // TRAP: anything not listed here returns 0 and is dropped SILENTLY. That is
  // how XGOffsetBaseTextureAddress was invisible to the GTL until 2026-07-17 —
  // it only ever arrives as "rex_XGOffsetBaseTextureAddress", never as a sub_
  // name, so the GTL never saw the root that DEFINES texture memory.
  if (std::strcmp(symbol, "rex_XGOffsetBaseTextureAddress") == 0) {
    return kOffsetBaseTextureAddress;
  }
  if (std::strcmp(symbol, "rex_XGOffsetResourceAddress") == 0) {
    return kOffsetResourceAddress;
  }
  if (std::strcmp(symbol, "rex_lhTexture_Create") == 0) {
    return kCreateSurface;
  }
  // BUG FIXED 2026-07-17: these two are GTL roots that the GTL DECLARED but
  // never received, because they only ever dispatch under a rex_ shim name and
  // this function silently returns 0 for anything unlisted. Addresses resolved
  // from refii_config.toml (which maps address -> rex_ name) and cross-checked
  // against Lumen:
  //   rex_lhGfx_CreateGPUState  = 0x82B99720 = D3DDevice_CreateVertexDeclaration
  //   rex_lhRenderer_EndScene   = 0x82B6F1D0 = kPresent
  // There is no sub_82B99720 hook at all, so kCreateVertexDeclaration was
  // dropped 100% of the time and the registry never held a single vertex
  // declaration — while DrawPacket.vertex_declaration was populated, so any
  // consumer resolving it through the registry missed every time.
  if (std::strcmp(symbol, "rex_lhGfx_CreateGPUState") == 0) {
    return kCreateVertexDeclaration;
  }
  if (std::strcmp(symbol, "rex_lhRenderer_EndScene") == 0) {
    return kPresent;
  }
  // WIRED 2026-07-22 (was listed here as a deliberate gap): this is the unlock
  // root Fable actually uses. Run 117 logged every dirty extent through it and
  // none through the three 0x82B9* wrappers, so leaving it unmapped meant the
  // GTL never bumped content_generation for CPU texture writes at all.
  if (std::strcmp(symbol, "rex_lhGfx_FlushBuffer") == 0) {
    return kUnlockResource;
  }
  // These two dispatch as sub_ names today, so the strncmp above already maps
  // them; listed here only so the root set is greppable from one place.
  //   sub_82B98860 = kGetSurfaceLevel, sub_82B98978 = kGetCubeMapSurface
  // STILL DROPPED, deliberately — these are hooked under rex_ names but are NOT
  // GTL roots today. Adding them is a design decision (new content/state
  // events), not a bug fix, so they are listed rather than wired:
  //   rex_lhGfx_LockBuffer         0x821F6300  D3D::LockResource — the
  //       accumulator side of the dirty range consumed by kUnlockResource.
  //       Only needed as a GTL root if we ever have to maintain the range
  //       ourselves (i.e. if the guest body stops running on these paths).
  //   rex_lhGfx_SetupRenderTargets 0x82A4E6D0  — REVERSED 2026-07-22 (IDA:
  //       Render_AllocateSceneRenderTargetSet). It does NOT bind anything; it
  //       ALLOCATES the scene render-target set from a {w,h} pair: full-res
  //       fmt 97-or-58, full-res fmt 54 (g_ResolvedDepthTexture), plus HALF
  //       and QUARTER res fmt 54 — a downsample chain. It builds them via
  //       Texture_InitHeaderPair (0x8225FBD0), which is NOT hooked — but its
  //       inner XGSetTextureHeader (0x821FBB78) IS a GTL root and DOES fire
  //       (17 dispatches in run 132), so the GTL already sees these header
  //       inits. What it lacks is only the outer context: which of the four
  //       targets (full colour / full depth / half / quarter) a given init is.
  //       (An earlier version of this note claimed the inner call was
  //       "INTRA-MODULE and so bypasses our hooks" — measurably false, see the
  //       correction at kUnlockResource above.)
  //       The old note here guessed it was "a candidate cause for
  //       resolve_unbound_source". That guess is RETIRED: run 129 measured the
  //       resolve census at unbound_source=0, unregistered_dest=0,
  //       surface_mismatch=0, complete=243/243. Do not wire it for that
  //       reason — the defect does not occur. If it is ever wired, the
  //       GTL-correct carrier is Texture_InitHeaderPair (each call states its
  //       own dims + format index), not this outer function, whose four
  //       targets differ only by constants baked into its code.
  //   rex_XGSetVertexDeclaration   0x82B99630  XGSetVertexDeclaration
  //   rex_XGRegisterVertexShader   0x82B993C0  XGRegisterVertexShader
  //   rex_lhRenderer_PrepareFrame  0x82B6ED10 / rex_lhRenderer_SetupFrame 0x82B6EDE0
  if (std::strcmp(symbol, "rex_lhGfx_CreateVertexBuffer") == 0) {
    return kCreateVertexBuffer;
  }
  if (std::strcmp(symbol, "rex_lhGfx_CreateIndexBuffer") == 0) {
    return kCreateIndexBuffer;
  }
  return 0;
}

bool IsLikelyGuestObjectAddress(uint32_t address) {
  return address >= 0x40000000u && address < 0x50000000u;
}

ResourceKind CreateKindForSymbol(uint32_t symbol_address) {
  switch (symbol_address) {
    case kCreateTexture:
      return ResourceKind::kTexture;
    case kCreateSurface:
      return ResourceKind::kSurface;
    case kCreateVertexBuffer:
      return ResourceKind::kVertexBuffer;
    case kCreateIndexBuffer:
      return ResourceKind::kIndexBuffer;
    case kCreateVertexShader:
    case kFableCreateVertexShader:
      return ResourceKind::kVertexShader;
    case kCreatePixelShader:
    case kFableCreatePixelShader:
      return ResourceKind::kPixelShader;
    case kCreateVertexDeclaration:
      return ResourceKind::kVertexDeclaration;
    default:
      return ResourceKind::kUnknown;
  }
}

// ---------------------------------------------------------------------------
// STATED BUFFER PAYLOAD (IDA-proven 2026-08-04). The vertex/index buffer's
// data pointer, size and index format, read from the D3D header the guest's
// OWN factory wrote.
// ---------------------------------------------------------------------------
//
// WHY THE HEADER AND NOT THE CREATE CALL. Fable's buffer factories
// (0x82B8B820 vertex, 0x82B8D7A8 index) allocate a 40-byte object
//   +0x00 vtable (off_820FB130)  +0x04 current physical base  +0x08 D3D header
// and hand XGSetVertexBufferHeader / XGSetIndexBufferHeader the header at
// obj+8. The buffer is created EMPTY and, worse, its payload RELOCATES under a
// compacting pool (see kRelocateResource). So there is no create-time answer
// to "where are the bytes" -- only a header-time one, and it has to be re-read.
//
// THE HEADER FIELDS, quoted from the two XG header writers:
//   XGSetVertexBufferHeader @0x8300BD38
//     stw r11,0x00(r31)  Common          = 1 (type nibble 1 = VERTEX)
//     stw r8, 0x14(r31)  BaseFlush       = 0xFFFF0000 (idle sentinel)
//     ori r10,r28,3
//     stw r10,0x18(r31)  Format.dword[0] = Offset | 3   (fetch type 3 VERTEX)
//     rlwinm r9,r29,0,6,29 / oris 0x1000 / ori 2
//     stw r11,0x1C(r31)  Format.dword[1] = (Length & 0x03FFFFFC) | 0x10000002
//   XGSetIndexBufferHeader @0x8300BDD0
//     stw r27,0x1C(r31)  Size            = Length        (RAW, unmasked)
//     stw r28,0x18(r31)  Address         = Offset        (no tag bits)
//     stw r11,0x00(r31)  Common          = 2 | (Format << 29)
//
// so size_bytes is `word & 0x03FFFFFC` for a vertex buffer and the raw word for
// an index buffer -- two different encodings that a shared decode would get
// wrong for one of them.
//
// AND THE ADDRESS DECODE IS THE GUEST'S OWN, not ours. D3DDevice_SetStreamSource
// @0x821B6C60 computes the fetch base from the very same word:
//     lwz    r10,0x18(r30)      ; the word below
//     add    r10,r10,r6         ; + OffsetInBytes
//     srwi   r7,r10,20 / addi r11,r7,0x200 / rlwinm r11,r11,0,19,19  ; & 0x1000
//     clrlwi r10,r10,3                                              ; & 0x1FFFFFFF
//     add    r11,r11,r10
// which is exactly DecodeFableGpuMemoryPointer. D3DDevice_DrawIndexedVertices
// @0x8221E200 does the identical thing with 0x18(r24) for indices. We are
// running the guest's arithmetic on the guest's word, not reverse-engineering a
// hardware descriptor.
struct StatedBufferHeader {
  bool valid = false;
  uint32_t common = 0;
  // header+0x18 verbatim, tag bits included.
  uint32_t gpu_word = 0;
  // The addressable form of that word: decoded, then mapped into the physical
  // view. This is the address the GPU fetches from and the address our upload
  // reads from -- there is no second candidate.
  uint32_t data_address = 0;
  uint32_t byte_size = 0;
  // Index element size in BYTES (2 or 4), zero for a vertex buffer. From
  // `Common >> 29`: D3D_EmitDrawIndexedPackets @0x82207E68 tests bit 2 of that
  // field (Common & 0x80000000) to select 32-bit indices, and the draw
  // initiator ORs 0x800 on that branch.
  uint32_t index_stride = 0;
};

StatedBufferHeader ReadStatedBufferHeaderLocked(uint8_t *guest_base,
                                                uint32_t header,
                                                bool index_buffer) {
  StatedBufferHeader out;
  if (guest_base == nullptr || !IsLikelyGuestObjectAddress(header) ||
      !refii::renderer::bink::IsReadableGuestByteRange(guest_base, header,
                                                       0x20u)) {
    return out;
  }
  out.common = refii::renderer::bink::LoadGuestU32(guest_base, header);
  out.gpu_word = refii::renderer::bink::LoadGuestU32(guest_base, header + 0x18u);
  const uint32_t size_word =
      refii::renderer::bink::LoadGuestU32(guest_base, header + 0x1Cu);
  if (index_buffer) {
    out.byte_size = size_word;
    out.index_stride = (out.common & 0x80000000u) != 0 ? 4u : 2u;
  } else {
    out.byte_size = size_word & 0x03FFFFFCu;
  }
  if (out.gpu_word == 0 || out.byte_size == 0) {
    // A header that states no memory or no length is a buffer that has not
    // been given its allocation yet. Report it as invalid rather than as a
    // zero-sized resource: the difference is "ask again later" vs "the guest
    // said there is nothing here".
    return out;
  }
  out.data_address = refii::renderer::FableGuestPhysicalViewAddress(
      refii::renderer::DecodeFableGpuBufferPointer(out.gpu_word));
  out.valid = out.data_address != 0;
  return out;
}

// The bind roots are the GTL's authority on a buffer's payload, because they
// are the only place the guest names the object and its stride in one breath.
// Called from kSetStreamSource / kSetIndices with g_state.mutex held.
//
// `stated_stride` is the SetStreamSource Stride ARGUMENT (r7) and is used
// verbatim (§5: the stated call beats the fetch constant it goes on to write).
// Index buffers pass 0 and take their element size from the header's Common.
//
// ADOPTION. A buffer the create root never delivered is registered here rather
// than dropped. That is not a guess about what the object is: the D3D entry
// point's own parameter type says D3DVertexBuffer* / D3DIndexBuffer*, and the
// header it points at states the matching type nibble. Dropping it would leave
// the draw with a stream the registry cannot answer for, which is the
// `missing_vertex_input` failure this whole path exists to close.
void RefreshStatedBufferBindLocked(uint32_t header, ResourceKind kind,
                                   uint8_t *guest_base,
                                   uint32_t stated_stride) {
  if (!IsLikelyGuestObjectAddress(header)) {
    return;
  }
  const StatedBufferHeader stated = ReadStatedBufferHeaderLocked(
      guest_base, header, kind == ResourceKind::kIndexBuffer);
  auto it = g_state.resources.find(header);
  if (it == g_state.resources.end()) {
    Resource adopted;
    adopted.kind = kind;
    adopted.guest_address = header;
    adopted.create_symbol = kind == ResourceKind::kIndexBuffer ? kSetIndices
                                                               : kSetStreamSource;
    adopted.create_serial = g_state.next_create_serial++;
    it = g_state.resources.emplace(header, std::move(adopted)).first;
    ++g_state.stats.buffers_adopted_at_bind;
    MarkResourceChangedLocked(header);
  }
  Resource &resource = it->second;
  if (resource.kind == ResourceKind::kUnknown) {
    resource.kind = kind;
  }
  const uint32_t stride =
      kind == ResourceKind::kIndexBuffer ? stated.index_stride : stated_stride;
  // Mark changed ONLY on a real delta. SetStreamSource fires several times per
  // draw; journalling every call would swamp the change feed with entries that
  // say nothing changed, and the consumer re-syncs everything the journal
  // names.
  bool changed = false;
  if (stated.valid) {
    if (resource.byte_size != stated.byte_size) {
      resource.byte_size = stated.byte_size;
      changed = true;
    }
    if (resource.base_address != stated.data_address) {
      // The payload moved (or was named for the first time). base_address is
      // the field every content path already reads as "the memory this object
      // NAMES", so the buffer joins the texture model rather than growing a
      // parallel one.
      resource.base_address = stated.data_address;
      ++resource.address_generation;
      changed = true;
    }
  }
  if (stride != 0 && resource.stride != stride) {
    resource.stride = stride;
    changed = true;
  }
  if (changed) {
    MarkResourceChangedLocked(header);
  }
  ++g_state.stats.buffer_binds_stated;
}

// Fable's own shader constructors take (pWrapper, pContainer), NOT the XDK
// roots' single pFunction. Both the container argument and the object
// resolution differ, so every site that reads either has to branch on this.
bool IsFableShaderConstructor(uint32_t symbol_address) {
  return symbol_address == kFableCreateVertexShader ||
         symbol_address == kFableCreatePixelShader;
}

// ---------------------------------------------------------------------------
// Stated microcode / element capture at the Create roots.
// ---------------------------------------------------------------------------
//
// These run at the create EXIT (RegisterCreateLocked's caller), so the factory
// has already written the returned object and the argument the guest passed is
// still in hand. Nothing below scans, searches or infers: every address comes
// either from the call's own argument or from a header field the object itself
// states.

constexpr uint32_t kMaxShaderBytecodeSize = 1u << 20;
constexpr uint32_t kMaxShaderContainerSize = 2u << 20;
constexpr uint32_t kMaxVertexDeclarationElements = 32;

// Fable shader-constructor exit resolution (IDA, 2026-08-04).
//
// sub_82B87738 / sub_82B87818 return pWrapper in r3, and the wrapper is 12
// bytes: {+0 vtable, +4 physical microcode base, +8 D3D shader object}.
//
// THE REGISTRY KEY IS THE OBJECT AT +8, NOT THE WRAPPER. SetVertexShader /
// SetPixelShader bind that object pointer, so it is what every draw resolves
// against; registering the wrapper would key the entry on an address no draw
// ever mentions and leave the census at zero shaders exactly as before.
//
// The object STATES its own stage in Common & 7 (6 = vertex, 7 = pixel), so the
// stage is read from the object rather than assumed from which constructor ran.
// That doubles as the identity check: a wrapper+8 that does not state 6 or 7 is
// not a shader object, and the create is dropped rather than registered under a
// guessed kind. This deliberately replaces IsLikelyGuestObjectAddress for these
// two roots -- a stated stage tag is a stronger and more honest identity test
// than an address-range heuristic, and it is not tied to any allocator range.
struct FableShaderObject {
  uint32_t object = 0;
  ResourceKind kind = ResourceKind::kUnknown;
};

FableShaderObject ResolveFableShaderObjectFromWrapper(uint8_t *guest_base,
                                                      uint32_t wrapper) {
  FableShaderObject resolved;
  if (guest_base == nullptr || wrapper == 0) {
    return resolved;
  }
  if (!refii::renderer::bink::IsReadableGuestByteRange(guest_base, wrapper,
                                                       12u)) {
    return resolved;
  }
  const uint32_t object =
      refii::renderer::bink::LoadGuestU32(guest_base, wrapper + 8u);
  if (object == 0 || !refii::renderer::bink::IsReadableGuestByteRange(
                         guest_base, object, 4u)) {
    return resolved;
  }
  const uint32_t common =
      refii::renderer::bink::LoadGuestU32(guest_base, object + 0u);
  switch (common & 7u) {
    case 6u:
      resolved.kind = ResourceKind::kVertexShader;
      break;
    case 7u:
      resolved.kind = ResourceKind::kPixelShader;
      break;
    default:
      // Not a shader object: leave object 0 so the caller counts a dropped
      // create instead of registering a mystery address.
      return resolved;
  }
  resolved.object = object;
  return resolved;
}

// D3DDevice_Create{Vertex,Pixel}Shader(pFunction). `container` is pFunction --
// the Fable shader blob, whose header states {+4 payload offset, +8 payload
// size}. Hash the WHOLE container: that is the byte range the offline
// extractor hashed into RendererShaderLibraryEntry::guest_bytecode_hash, so it
// is the key the library lookup matches.
bool PopulateShaderFromContainerLocked(Resource &resource, uint8_t *guest_base,
                                       uint32_t container) {
  if (guest_base == nullptr || container == 0) {
    return false;
  }
  if (!refii::renderer::bink::IsReadableGuestByteRange(guest_base, container,
                                                       12u)) {
    return false;
  }
  const uint32_t offset =
      refii::renderer::bink::LoadGuestU32(guest_base, container + 4u);
  const uint32_t size =
      refii::renderer::bink::LoadGuestU32(guest_base, container + 8u);
  if (offset == 0 || size == 0 || size > kMaxShaderBytecodeSize ||
      offset > kMaxShaderContainerSize ||
      size > kMaxShaderContainerSize - offset) {
    return false;
  }
  const uint32_t container_size = offset + size;
  const uint64_t hash = refii::renderer::HashGuestBytesFnv1a64(
      guest_base, container, container_size);
  if (hash == 0) {
    return false;
  }
  resource.shader_container_address = container;
  resource.shader_container_size = container_size;
  resource.shader_bytecode_address = container + offset;
  resource.shader_bytecode_size = size;
  resource.shader_bytecode_offset = offset;
  resource.shader_bytecode_hash = hash;
  return true;
}

// Fallback for a shader whose container argument was unreadable: the live
// shader object points straight at its payload and states the payload size.
// The shader library indexes the payload hash too
// (RendererShaderLibraryEntry::guest_payload_hash), so this resolves as well --
// it is simply the less complete of the two stated views.
//
// Object field offsets are the guest's own D3DVertexShader / D3DPixelShader
// layout, IDA-VERIFIED 2026-08-04 (they were carried forward unverified before
// that):
//   vertex: +0x20 microcode ptr (stw r30,0x20(r31) in XGRegisterVertexShader
//           @0x82B993E8), +0x36C payload offset, +0x370 payload size
//   pixel:  +0x18 microcode ptr (stw r8,0x18(r9) @0x82B87904),
//           +0x2C payload offset, +0x30 payload size
// The offset/size fields are NOT free-standing members: obj+0x368 (vertex) /
// obj+0x28 (pixel) is the start of an inline byte-for-byte COPY of the
// container header that the constructor memcpy'd in, so +0x36C/+0x2C and
// +0x370/+0x30 are that copy's container[1] and container[2]. The guest reads
// the copy back the same way (lwz r11,0x368(r31) @0x82B993FC), and the clone
// path sub_82B91A18 rebuilds a whole container from it.
//
// This fallback hashes the MICROCODE, which D3D_DirectShaderPatch mutates in
// place, so it is strictly the weaker of the two views -- prefer the container.
bool PopulateShaderFromObjectLocked(Resource &resource, uint8_t *guest_base,
                                    uint32_t object) {
  if (guest_base == nullptr || object == 0) {
    return false;
  }
  uint32_t pointer_offset = 0;
  uint32_t offset_field = 0;
  uint32_t size_field = 0;
  if (resource.kind == ResourceKind::kVertexShader) {
    pointer_offset = 32u;
    offset_field = 876u;
    size_field = 880u;
  } else if (resource.kind == ResourceKind::kPixelShader) {
    pointer_offset = 24u;
    offset_field = 44u;
    size_field = 48u;
  } else {
    return false;
  }
  const uint32_t probe_end = std::max(size_field + 4u, pointer_offset + 4u);
  if (!refii::renderer::bink::IsReadableGuestByteRange(guest_base, object,
                                                       probe_end)) {
    return false;
  }
  const uint32_t pointer = refii::renderer::bink::LoadGuestU32(
      guest_base, object + pointer_offset);
  const uint32_t offset =
      refii::renderer::bink::LoadGuestU32(guest_base, object + offset_field);
  const uint32_t size =
      refii::renderer::bink::LoadGuestU32(guest_base, object + size_field);
  if (pointer == 0 || size == 0 || size > kMaxShaderBytecodeSize) {
    return false;
  }
  const uint64_t hash =
      refii::renderer::HashGuestBytesFnv1a64(guest_base, pointer, size);
  if (hash == 0) {
    return false;
  }
  resource.shader_bytecode_address = pointer;
  resource.shader_bytecode_size = size;
  resource.shader_bytecode_offset = offset;
  resource.shader_bytecode_hash = hash;
  return true;
}

// D3DDevice_CreateVertexDeclaration(pElements) -> D3DVertexDeclaration. The
// returned object carries the element COUNT at +0x18 and an inline copy of the
// 12-byte elements at +0x34; +0x1C/+0x20/+0x30 hold the max stream, the stream
// mask and the declaration flags. All stated by the factory, all read after it
// returned.
bool PopulateVertexDeclarationLocked(Resource &resource, uint8_t *guest_base,
                                     uint32_t object) {
  if (guest_base == nullptr || object == 0) {
    return false;
  }
  if (!refii::renderer::bink::IsReadableGuestByteRange(guest_base, object,
                                                       0x34u)) {
    return false;
  }
  const uint32_t element_count =
      refii::renderer::bink::LoadGuestU32(guest_base, object + 0x18u);
  if (element_count == 0 || element_count > kMaxVertexDeclarationElements) {
    return false;
  }
  const uint32_t elements_address = object + 0x34u;
  const uint32_t element_bytes = element_count * 12u;
  if (!refii::renderer::bink::IsReadableGuestByteRange(
          guest_base, elements_address, element_bytes)) {
    return false;
  }
  const uint64_t hash = refii::renderer::HashGuestBytesFnv1a64(
      guest_base, elements_address, element_bytes);
  if (hash == 0) {
    return false;
  }
  resource.decl_elements_address = elements_address;
  resource.decl_elements_size = element_bytes;
  resource.decl_element_count = element_count;
  resource.decl_max_stream =
      refii::renderer::bink::LoadGuestU32(guest_base, object + 0x1Cu);
  resource.decl_stream_mask =
      refii::renderer::bink::LoadGuestU32(guest_base, object + 0x20u);
  resource.decl_flags =
      refii::renderer::bink::LoadGuestU32(guest_base, object + 0x30u);
  resource.decl_elements_hash = hash;
  const uint8_t *src =
      guest_base + elements_address +
      refii::renderer::bink::GuestPhysicalHostOffset(elements_address);
  resource.decl_elements.assign(src, src + element_bytes);
  return true;
}

void RegisterCreateLocked(ResourceKind kind, uint32_t object,
                          const PendingCreate &pending) {
  if (object == 0) {
    // A create that produced no object. Counted because a silently-lost create
    // is indistinguishable from "the factory returned null" otherwise.
    ++g_state.stats.creates_dropped_null_object;
    return;
  }
  Resource resource;
  resource.kind = kind;
  resource.guest_address = object;
  // Creation-call facts, pinned from the REAL prototypes (2026-07-17). These
  // factories take NO device argument and return the object, so the fields
  // start at r3 and the object is paired at exit.
  //
  // Previously texture and surface shared one branch reading (r3,r4,r5,r7) as
  // (width,height,levels,format). Both were wrong: CreateTexture's r5 is Depth
  // and r7 is Usage, and CreateSurface's r5 is Format and r7 is a POINTER
  // (pParameters). Same arg-map-by-assumption class as the resolve and the
  // XGSetTextureHeader offset. Dispatch per factory instead.
  switch (pending.symbol_address) {
    case kCreateTexture:
      // D3DDevice_CreateTexture(DWORD Width, DWORD Height, DWORD Depth,
      //   DWORD Levels, DWORD Usage, D3DFORMAT Format, D3DPOOL UnusedPool,
      //   D3DRESOURCETYPE D3DType) -> D3DBaseTexture*
      resource.width = pending.r3;
      resource.height = pending.r4;
      resource.levels = pending.r6;         // r5 is Depth
      resource.format = pending.r8 & 0x3Fu; // r7 is Usage
      break;
    case kCreateSurface:
      // D3DDevice_CreateSurface(DWORD Width, DWORD Height, D3DFORMAT Format,
      //   D3DMULTISAMPLE_TYPE MultiSample,
      //   const D3DSURFACE_PARAMETERS *pParameters) -> D3DSurface*
      resource.width = pending.r3;
      resource.height = pending.r4;
      resource.levels = 1u;                 // a surface is a single level
      resource.format = pending.r5 & 0x3Fu; // r5 IS the format here
      break;
    case kCreateVertexShader:
    case kCreatePixelShader:
      // D3DDevice_Create{Vertex,Pixel}Shader(const DWORD *pFunction) -> shader.
      // Only r3 is meaningful; it is the microcode pointer, not a size. Leave
      // byte_size/stride/format zero rather than record noise.
      break;
    case kFableCreateVertexShader:
    case kFableCreatePixelShader:
      // Fable's own constructors: (pWrapper, pContainer) -> pWrapper. r3 is the
      // wrapper and r4 is the container; neither is a size, stride or format.
      // Listed explicitly so they do NOT fall into the default arm below, which
      // would record r4/r5/r6 as byte_size/stride/format -- i.e. would write the
      // container pointer into byte_size.
      break;
    case kCreateVertexDeclaration:
      // D3DDevice_CreateVertexDeclaration(const D3DVERTEXELEMENT9 *pElements)
      break;
    case kCreateVertexBuffer:
    case kCreateIndexBuffer:
      // DECOMPILED 2026-08-04, and the inference that used to live in the
      // default arm below (byte_size=r4, stride=r5, format=r6) is DELETED
      // rather than corrected: there is nothing to correct it to. Both roots
      // allocate a 40-byte wrapper and hand XGSet{Vertex,Index}BufferHeader the
      // header at obj+8 with the buffer EMPTY, and the payload subsequently
      // RELOCATES under the compacting physical pool (kRelocateResource). No
      // value read at the create call survives to the draw.
      //
      // The size, the data pointer and the index format are read from the
      // header at the BIND roots instead (RefreshStatedBufferBindLocked), where
      // the guest itself reads them. Leaving these three at zero is the honest
      // state between the create and the first bind.
      break;
    default:
      resource.byte_size = pending.r4;
      resource.stride = pending.r5;
      resource.format = pending.r6;
      break;
  }
  // STATED microcode / element capture. Both run against the create's own
  // argument and the object the factory just returned; a failure leaves the
  // fields zero, which the snapshot feed reports honestly (no hash, no
  // pipeline) rather than papering over.
  switch (kind) {
    case ResourceKind::kVertexShader:
    case ResourceKind::kPixelShader: {
      resource.kind = kind;  // the populators dispatch on it
      // The container argument sits in a DIFFERENT register per root family:
      // the XDK roots take (pFunction) in r3, Fable's constructors take
      // (pWrapper, pContainer) so the container is r4 and r3 is the wrapper.
      // Reading r3 for the Fable roots would hash the 12-byte wrapper.
      const uint32_t container =
          IsFableShaderConstructor(pending.symbol_address) ? pending.r4
                                                           : pending.r3;
      // HASH EXACTLY ONCE, HERE. D3D_DirectShaderPatch @0x821F7650 rewrites
      // 12-byte vfetch constant slots in place in the PHYSICAL microcode
      // allocation from the first draw onward, so a hash re-derived from guest
      // memory later would not match the offline blob. (IDA 2026-08-04: the
      // patch writes only `phys_microcode_base + part_offset`, never the
      // container -- so the container hash is stable either way, but the object
      // fallback below reads the microcode and is NOT.)
      if (!PopulateShaderFromContainerLocked(resource, pending.guest_base,
                                             container)) {
        PopulateShaderFromObjectLocked(resource, pending.guest_base, object);
      }
      break;
    }
    case ResourceKind::kVertexDeclaration:
      PopulateVertexDeclarationLocked(resource, pending.guest_base, object);
      break;
    default:
      break;
  }
  resource.create_symbol = pending.symbol_address;
  resource.create_lr = pending.lr;
  resource.create_serial = g_state.next_create_serial++;
  // Same generation-carry rule as the header-init path: a Create landing on an
  // address we already track is a re-creation of that slot, not a brand-new
  // resource with no history. Resetting to 0 would break monotonicity and read
  // downstream as "no content". See the longer note at the header-init site.
  const auto existing = g_state.resources.find(object);
  if (existing != g_state.resources.end()) {
    resource.content_generation = existing->second.content_generation + 1u;
    resource.gpu_content_generation = existing->second.gpu_content_generation;
    ++g_state.stats.resources_reregistered_generation_carried;
  }
  g_state.resources[object] = resource;
  ++g_state.stats.resources_registered;
  MarkResourceChangedLocked(object);
}

// ---------------------------------------------------------------------------
// Draw-time device-state snapshot + divergence census (PHASE 1: CAPTURE AND
// COUNT ONLY -- nothing here changes what renders).
// ---------------------------------------------------------------------------
//
// See DrawDeviceSnapshot in refii_gtl.h for the layout and the reason this
// runs at hook ENTRY rather than exit.

// Big-endian 64-bit guest load, composed from the existing 32-bit helper so
// this translation unit needs no new dependency. PowerPC is big-endian: the
// high dword is at the lower address.
uint64_t LoadGuestBigEndianU64(uint8_t *guest_base, uint32_t address) {
  const uint64_t hi =
      refii::renderer::bink::LoadGuestU32(guest_base, address);
  const uint64_t lo =
      refii::renderer::bink::LoadGuestU32(guest_base, address + 4u);
  return (hi << 32) | lo;
}

// Read N consecutive big-endian dwords out of the device object into `out`.
// Returns false and leaves `out` untouched when the range is unreadable, so a
// snapshot never carries invented state.
template <std::size_t N>
bool LoadDeviceDwordBlock(uint8_t *guest_base, uint32_t device, uint32_t offset,
                          std::array<uint32_t, N> &out) {
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

// Device-object offsets, straight from the IDB's typed D3DDevice.
constexpr uint32_t kSnapPendingMaskOffset = 0x0000u;      // m_Pending.m_Mask[5]
constexpr uint32_t kSnapPredicatedMask2Offset = 0x0028u;
constexpr uint32_t kSnapFetchOffset = 0x0480u;            // 192 dwords
constexpr uint32_t kSnapVertexFloatOffset = 0x0780u;      // 256 vec4
constexpr uint32_t kSnapPixelFloatOffset = 0x1780u;       // 256 vec4
constexpr uint32_t kSnapFlowOffset = 0x2780u;             // 40 dwords
constexpr uint32_t kSnapClipPlaneOffset = 0x2820u;        // 24 dwords
constexpr uint32_t kSnapPacketBlockOffset = 0x2880u;      // 124 dwords
// m_ControlPacket.ModeControl (0x2948) inside the 0x2880 block.
constexpr uint32_t kSnapModeControlBlockIndex =
    (0x2948u - kSnapPacketBlockOffset) / 4u;  // == 50
// D3DRS_CULLMODE mirror slot (fable_hook_symbols.cpp: sub_8220B470 -> 19).
constexpr uint32_t kSnapCullRenderStateSlot = 19u;

// Copy the 64 bytes a single dirty-mask bit covers. Bit numbering is FROM THE
// MSB (sub_8221DE68 walks with cntlzd), so bit k covers vec4 regs [4k..4k+3]
// == bank + 64*k == host dwords [16k..16k+15].
bool LoadDirtyConstantBlock(uint8_t *guest_base, uint32_t device,
                            uint32_t bank_offset, uint32_t bit,
                            std::array<uint32_t, 16> &out) {
  return LoadDeviceDwordBlock(guest_base, device, bank_offset + bit * 64u, out);
}

// Collect the set bits of a 64-bit dirty mask, MSB first (bit 0 == MSB), the
// same order sub_8221DE68 drains them in.
void CollectSetMaskBits(uint64_t mask, std::vector<uint8_t> &out) {
  out.clear();
  while (mask != 0) {
    const uint32_t bit = static_cast<uint32_t>(__builtin_clzll(mask));
    out.push_back(static_cast<uint8_t>(bit));
    mask &= ~((1ull << 63) >> bit);
  }
}

// ---------------------------------------------------------------------------
// PHASE 2, FETCH CLASS ONLY: device-sourced fetch constants.
// ---------------------------------------------------------------------------
//
// WHY THIS IS SAFE AT HOOK ENTRY (IDA-verified 2026-07-25):
//   * D3DDevice_SetTexture (0x821B6EE0) writes the six dwords of
//     m_Constants.Fetch[Sampler] DIRECTLY (`v12 = &pDevice->m_Constants
//     .Fetch[Sampler]; v12->Texture.dword[0..5] = ...`) and only then sets a
//     bit in m_Pending.m_Mask[3]. There is no pending shadow for fetch
//     constants — the texture POINTER array at device+0x3100 is what
//     SetTexture stages, not the constant.
//   * D3D_SetPending_FetchConstants (0x8221D1B0) only READS that block and
//     copies it into the ring at Xenos reg base 0x4800. It never writes it.
//   Therefore device+0x480 is always-current storage: whatever is there at
//   hook ENTRY is exactly what this draw will be handed, for every fetch
//   constant the guest set BEFORE calling the root.
//
// THE ONE ROOT THIS IS NOT TRUE FOR: sub_8220A3F0 (kDrawIndexedUserPrimitives)
// opens its body with `Render_FlushDirtyRenderStates()` (0x8221E4A0), the
// game's own deferred state drain — it walks g_RenderStateDirtyList and calls
// Render_ApplyDirtyStateVar, which is where the game's staged textures reach
// D3DDevice_SetTexture and therefore device+0x480. At that root's ENTRY the
// fetch block still describes the PREVIOUS draw. That root is excluded here
// and keeps its existing EXIT-time self-read
// (CaptureDeviceFetchConstantsIntoPacketLocked, called from OnHookExit).
//
// GATE: compile-time default plus an env override. DELIBERATELY NOT A CVAR —
// an unregistered key in the launcher's GetRuntimeCVarDefinitions silently
// reads false, which already cost a run. Nothing here depends on the launcher.
//   REFII_GTL_FETCH_DEVICE_SOURCED=0  -> mirror-fed (pre-cutover behaviour)
//   REFII_GTL_FETCH_DEVICE_SOURCED=1  -> device-sourced (the default)
constexpr bool kFetchDeviceSourcedDefault = true;

bool FetchDeviceSourced() {
  static const bool enabled = [] {
    const char *env = std::getenv("REFII_GTL_FETCH_DEVICE_SOURCED");
    if (env == nullptr || env[0] == '\0') {
      return kFetchDeviceSourcedDefault;
    }
    return !(env[0] == '0' && env[1] == '\0');
  }();
  return enabled;
}

// *** THE DEVICE DIRTY-MASK DRAIN — GTL-OWNED (2026-08-04). ***
//
// The guest driver contract is CONSUME-AND-CLEAR: the guest ORs dirty bits into
// m_Pending.m_Mask[5] at dev+0x00..0x20, and the driver is expected to zero
// them once it has acted. Nothing in the tree did this after the observer was
// deleted — it was the sole implementer — and the consequences are not subtle:
//
//   * The guest's own bool/int setters (sub_8226E120 / sub_82B99208) mark the
//     scalar block, and the packed bool words at dev+0x2780 are published on
//     that mark. The observer's own note recorded what a missed publish costs:
//     "every translated shader saw g_Booleans == 0".
//   * Our journal check reads `pending_mask[4] & 0x01000000` to decide whether
//     a flow change was ANNOUNCED. Never clearing means that bit latches set
//     after the first change, so the check silently reads "always journalled"
//     and the divergence census goes blind rather than reporting.
//
// We do NOT reproduce the observer's publish half. That existed to push the
// banks into the observer's own mirror; the GTL reads dev+0x480 (fetch) and
// dev+0x2780 (flow) DIRECTLY in CaptureDrawDeviceSnapshot, so the data is
// already in hand. Only the clear was missing.
//
// MUST run AFTER the snapshot, so the captured pending_mask still carries the
// bits this call is about to consume.
void DrainDeviceDirtyMasksLocked(uint32_t device, uint8_t *guest_base) {
  if (guest_base == nullptr || device == 0) {
    return;
  }
  if (!refii::renderer::bink::IsReadableGuestByteRange(
          guest_base, device + kSnapPendingMaskOffset, 0x28u)) {
    return;
  }
  for (uint32_t i = 0; i < 5u; ++i) {
    const uint32_t at = device + kSnapPendingMaskOffset + i * 8u;
    if (LoadGuestBigEndianU64(guest_base, at) != 0ull) {
      // Zero is byte-order agnostic, so two 32-bit stores clear the qword
      // regardless of endianness. There is no 64-bit guest store helper.
      refii::renderer::bink::StoreGuestU32(guest_base, at, 0u);
      refii::renderer::bink::StoreGuestU32(guest_base, at + 4u, 0u);
      ++g_state.stats.device_dirty_masks_drained;
    }
  }
}

DrawDeviceSnapshot CaptureDrawDeviceSnapshot(uint32_t device,
                                             uint8_t *guest_base) {
  DrawDeviceSnapshot snapshot;
  if (guest_base == nullptr || device == 0) {
    return snapshot;
  }
  // Same two guards every other device self-read in this file uses.
  if (!refii::renderer::IsLikelyFableObjectAddress(device)) {
    return snapshot;
  }
  if (!refii::renderer::bink::IsReadableGuestByteRange(
          guest_base, device + kSnapPendingMaskOffset, 0x30u)) {
    return snapshot;
  }
  snapshot.device = device;
  for (uint32_t i = 0; i < 5u; ++i) {
    snapshot.pending_mask[i] = LoadGuestBigEndianU64(
        guest_base, device + kSnapPendingMaskOffset + i * 8u);
  }
  snapshot.predicated_pending_mask2 =
      LoadGuestBigEndianU64(guest_base, device + kSnapPredicatedMask2Offset);

  // VS/PS float constants: ONLY the 64-byte blocks the guest actually dirtied
  // for this draw. Masks [0] and [1] are cleared by a DRAW and nothing else,
  // so this is a clean per-draw delta. A blind 4 KiB copy of both banks per
  // draw is what this deliberately avoids -- two such copies were already
  // removed from this path for exactly that cost.
  std::vector<uint8_t> bits;
  CollectSetMaskBits(snapshot.pending_mask[0], bits);
  snapshot.vs_dirty_blocks.reserve(bits.size());
  for (const uint8_t bit : bits) {
    DrawDeviceSnapshot::ConstantBlock block;
    block.bit = bit;
    if (LoadDirtyConstantBlock(guest_base, device, kSnapVertexFloatOffset, bit,
                               block.dwords)) {
      snapshot.vs_dirty_blocks.push_back(block);
    }
  }
  CollectSetMaskBits(snapshot.pending_mask[1], bits);
  snapshot.ps_dirty_blocks.reserve(bits.size());
  for (const uint8_t bit : bits) {
    DrawDeviceSnapshot::ConstantBlock block;
    block.bit = bit;
    if (LoadDirtyConstantBlock(guest_base, device, kSnapPixelFloatOffset, bit,
                               block.dwords)) {
      snapshot.ps_dirty_blocks.push_back(block);
    }
  }

  // Masks 2/3/4 are ALSO zeroed by resolve/clear/present, so their delta is
  // diluted and cannot be trusted as a per-draw signal. The regions they
  // describe are small (0x480 fetch, 0x2780 flow, 0x2820 clip, 0x2880..0x2A70
  // packets), so read them unconditionally instead of mask-gating them.
  snapshot.fetch_valid =
      LoadDeviceDwordBlock(guest_base, device, kSnapFetchOffset,
                           snapshot.fetch);
  snapshot.flow_valid =
      LoadDeviceDwordBlock(guest_base, device, kSnapFlowOffset, snapshot.flow);
  snapshot.clip_planes_valid = LoadDeviceDwordBlock(
      guest_base, device, kSnapClipPlaneOffset, snapshot.clip_planes);
  snapshot.packet_block_valid = LoadDeviceDwordBlock(
      guest_base, device, kSnapPacketBlockOffset, snapshot.packet_block);
  if (snapshot.packet_block_valid) {
    snapshot.mode_control = snapshot.packet_block[kSnapModeControlBlockIndex];
    snapshot.mode_control_valid = true;
  }
  snapshot.valid = true;
  return snapshot;
}

// Make the entry-time device fetch block AUTHORITATIVE for this packet
// (Phase 2, fetch class only — see FetchDeviceSourced above for the IDA proof
// that device+0x480 is current at entry, and for the one excluded root).
//
// Two things change relative to the mirror feed:
//   1. All 26 texture-fetch slots come from the device, INCLUDING the ones that
//      read zero. That is the point: the mirror only ever learns about a slot
//      when D3D_SetPending_FetchConstants fires with that slot's dirty bit set,
//      so a slot the guest has since unbound keeps its last nonzero base
//      forever, and a slot bound before the first dispatch never appears at
//      all. "Unbound" must be representable, not merely unlearnable.
//   2. fetch_dwords gets the full 192-dword block, so every fetch-based
//      classifier/binder (format, size, tiling, endian — not just the base)
//      sees the same state the GPU will.
// The vertex-fetch region (slots 26..31 / device+0x6F0) is copied verbatim as
// part of the block but is NOT base-decoded here: those are 2-dword vertex
// fetch constants, not 6-dword texture fetches, and nothing consumes
// fetch_bases past slot 25.
void ApplyDeviceSourcedFetchToPacket(DrawPacket &packet) {
  const DrawDeviceSnapshot &snap = packet.device_snapshot;
  if (!snap.valid || !snap.fetch_valid) {
    return;
  }
  // Both are std::array<uint32_t, 192> over the same device+0x480 block; a
  // shape change on either side breaks this assignment at compile time.
  packet.fetch_dwords = snap.fetch;
  packet.fetch_dwords_valid = true;
  for (uint32_t slot = 0; slot < 26u && slot < packet.fetch_bases.size();
       ++slot) {
    packet.fetch_bases[slot] = snap.fetch[slot * 6u + 1u] & 0xFFFFF000u;
  }
}

// Divergence census (PHASE 1 DELIVERABLE). Two distinct measurements, kept
// separate because they answer different questions:
//
//  CLASS A -- MIRROR COMPARE. For field classes the GTL already mirrors
//  (vs/ps float banks, fetch bases, the cull render state), compare the value
//  freshly read out of the device against what the mirror path put in THIS
//  packet. A disagreement is a stale mirror -- the defect class that produced
//  the WVP-zero bug and the 82%-wrong cull word.
//
//  CLASS B -- JOURNAL COMPARE. Flow constants (0x2780) and the GPU packet
//  block (0x2880..0x2A70) have NO GTL mirror to compare against, so instead
//  measure whether the bytes CHANGED since the previous draw's snapshot while
//  the corresponding dirty-journal bit was CLEAR. That is a write which
//  bypassed the dirty journal entirely -- structurally the same defect, and
//  exactly what the ~28 SetRenderState_*/SetSamplerState_* entry points with
//  direct (mostly GFxRendererXbox360_*) callers are expected to produce. Large
//  numbers on Scaleform/menu frames are CONFIRMATION, not a bug.
struct DivergenceCensus {
  uint64_t draws_examined = 0;
  uint64_t draws_no_snapshot = 0;

  // Class A: units are (compared, diverged).
  uint64_t vs_float_blocks = 0;
  uint64_t vs_float_blocks_diverged = 0;
  uint64_t ps_float_blocks = 0;
  uint64_t ps_float_blocks_diverged = 0;
  // fetch_slots / fetch_slots_diverged compare the device against WHAT THE
  // PACKET CARRIES. Once the fetch cutover is on this is the cutover's own
  // confirmation signal: it must collapse toward zero (see FetchDeviceSourced).
  uint64_t fetch_slots = 0;
  uint64_t fetch_slots_diverged = 0;
  // fetch_mirror_* compare the device against the OnFetchConstantDispatch-fed
  // mirror (g_state.device.fetch_bases) and are NOT affected by the cutover, so
  // the mirror-staleness measurement survives it. Run 284 measured
  // 10420/12244 (85%) here while the two counters above were the same numbers.
  uint64_t fetch_mirror_slots = 0;
  uint64_t fetch_mirror_slots_diverged = 0;
  // Draws whose packet fetch was taken from the device at this root's ENTRY.
  uint64_t fetch_device_sourced_draws = 0;
  // Draws at sub_8220A3F0, the one root whose ENTRY state is pre-
  // Render_FlushDirtyRenderStates and therefore deliberately NOT cut over
  // here (it keeps its EXIT-time self-read). Residual fetch divergence should
  // be attributable to this count.
  uint64_t fetch_preflush_root_draws = 0;
  uint64_t cull_compared = 0;
  uint64_t cull_diverged = 0;

  // Class B: draws whose bytes moved with the journal bit clear.
  uint64_t flow_unjournalled = 0;
  uint64_t packets_unjournalled = 0;

  // Draws where at least one class diverged, and how many distinct shapes.
  uint64_t draws_diverged = 0;
  uint64_t distinct_shapes = 0;
};
DivergenceCensus g_divergence_census;
// Keyed per DISTINCT divergence SHAPE, deliberately not a flat log cap: a flat
// cap fills up with the first repeating pass (Bink redraws one shader pair
// every pump frame) long before anything interesting runs. Bounded so a
// pathological per-draw-unique key cannot grow it without limit.
std::unordered_set<uint64_t> g_divergence_logged_shapes;
// Previous snapshot of the journal-compared regions, per device. Class B is a
// change detector, so it needs the last value.
struct JournalBaseline {
  bool have_flow = false;
  bool have_packets = false;
  std::array<uint32_t, 40> flow = {};
  std::array<uint32_t, 124> packet_block = {};
};
std::unordered_map<uint32_t, JournalBaseline> g_journal_baselines;

// Compare and count. Caller holds g_state.mutex. Reads only; the snapshot and
// the packet are both left exactly as they were.
void AccumulateDeviceSnapshotDivergenceLocked(const DrawPacket &packet) {
  const DrawDeviceSnapshot &snap = packet.device_snapshot;
  if (!snap.valid) {
    ++g_divergence_census.draws_no_snapshot;
    return;
  }
  ++g_divergence_census.draws_examined;

  // --- class A: vs/ps float banks -------------------------------------------
  // packet.vs_float_constants / ps_float_constants are host-order dwords, and
  // so is the snapshot (both go through LoadGuestU32 / bswap), so the compare
  // is apples to apples. Mask bit k covers dwords [16k .. 16k+15].
  uint32_t vs_diverged = 0;
  uint32_t ps_diverged = 0;
  const auto compare_bank =
      [](const std::vector<DrawDeviceSnapshot::ConstantBlock> &blocks,
         const std::vector<uint32_t> &mirror, uint64_t &compared_out,
         uint64_t &diverged_out, uint32_t &diverged_here) {
        for (const auto &block : blocks) {
          const std::size_t first = static_cast<std::size_t>(block.bit) * 16u;
          if (first + 16u > mirror.size()) {
            continue;  // mirror never populated this far; not a divergence
          }
          ++compared_out;
          if (std::memcmp(block.dwords.data(), mirror.data() + first,
                          block.dwords.size() * sizeof(uint32_t)) != 0) {
            ++diverged_out;
            ++diverged_here;
          }
        }
      };
  compare_bank(snap.vs_dirty_blocks, packet.vs_float_constants,
               g_divergence_census.vs_float_blocks,
               g_divergence_census.vs_float_blocks_diverged, vs_diverged);
  compare_bank(snap.ps_dirty_blocks, packet.ps_float_constants,
               g_divergence_census.ps_float_blocks,
               g_divergence_census.ps_float_blocks_diverged, ps_diverged);

  // --- class A: texture fetch bases -----------------------------------------
  // The snapshot's dword1 bits 12..31 are the live base, the SAME decode and
  // the SAME address (device+0x480+slot*24+4, & 0xFFFFF000) the mirror feed
  // uses in hook_observer.cpp's ObservePendingFetchConstantsLocked — so this
  // comparison is apples to apples in domain terms, not raw-vs-physical-view.
  //
  // TWO measurements, deliberately separate:
  //   packet compare  -> device vs what this packet will actually carry.
  //                      Collapses to ~0 once FetchDeviceSourced() is on, for
  //                      every root except the pre-flush wrapper.
  //   mirror compare  -> device vs g_state.device.fetch_bases, the
  //                      OnFetchConstantDispatch-fed cache. Unaffected by the
  //                      cutover, so the staleness number stays observable.
  uint32_t fetch_diverged = 0;
  if (snap.fetch_valid) {
    for (uint32_t slot = 0; slot < 26u && slot < packet.fetch_bases.size();
         ++slot) {
      const uint32_t live = snap.fetch[slot * 6u + 1u] & 0xFFFFF000u;
      const uint32_t carried = packet.fetch_bases[slot];
      if (live != 0 || carried != 0) {
        ++g_divergence_census.fetch_slots;
        if (live != carried) {
          ++g_divergence_census.fetch_slots_diverged;
          ++fetch_diverged;
        }
      }
      const uint32_t mirrored = g_state.device.fetch_bases[slot];
      if (live == 0 && mirrored == 0) {
        continue;  // slot unused by both; nothing to say
      }
      ++g_divergence_census.fetch_mirror_slots;
      if (live != mirrored) {
        ++g_divergence_census.fetch_mirror_slots_diverged;
      }
    }
  }

  // --- class A: the cull word ------------------------------------------------
  // IDA (0x8220B470) preserves bits 29..31 of the caller's Value into
  // device+0x2948, so the low 3 bits of the live dword and the render_states
  // mirror are the SAME domain. Only compared when the mirror was actually
  // written this session (valid_mask bit set) -- otherwise the mirror is the
  // session default and "divergence" would just be measuring the default.
  bool cull_diverged_here = false;
  if (snap.mode_control_valid &&
      (packet.render_state_valid_mask & (1u << kSnapCullRenderStateSlot)) !=
          0u) {
    ++g_divergence_census.cull_compared;
    const uint32_t live_cull = snap.mode_control & 7u;
    const uint32_t mirrored_cull =
        packet.render_states[kSnapCullRenderStateSlot] & 7u;
    if (live_cull != mirrored_cull) {
      ++g_divergence_census.cull_diverged;
      cull_diverged_here = true;
    }
  }

  // --- class B: flow + packet block against the dirty journal ---------------
  // Journal bits, per the mask map:
  //   m_Mask[4] bit 0x01000000 -> Flow (0x2780)
  //   m_Mask[2] (any bit)      -> the 0x2880 packet family
  bool flow_unjournalled = false;
  bool packets_unjournalled = false;
  JournalBaseline &baseline = g_journal_baselines[snap.device];
  if (snap.flow_valid) {
    const bool journalled = (snap.pending_mask[4] & 0x01000000ull) != 0ull;
    if (baseline.have_flow && !journalled &&
        std::memcmp(baseline.flow.data(), snap.flow.data(),
                    snap.flow.size() * sizeof(uint32_t)) != 0) {
      ++g_divergence_census.flow_unjournalled;
      flow_unjournalled = true;
    }
    baseline.flow = snap.flow;
    baseline.have_flow = true;
  }
  if (snap.packet_block_valid) {
    const bool journalled = snap.pending_mask[2] != 0ull;
    if (baseline.have_packets && !journalled &&
        std::memcmp(baseline.packet_block.data(), snap.packet_block.data(),
                    snap.packet_block.size() * sizeof(uint32_t)) != 0) {
      ++g_divergence_census.packets_unjournalled;
      packets_unjournalled = true;
    }
    baseline.packet_block = snap.packet_block;
    baseline.have_packets = true;
  }

  const uint32_t class_bits =
      (vs_diverged != 0 ? 1u : 0u) | (ps_diverged != 0 ? 2u : 0u) |
      (fetch_diverged != 0 ? 4u : 0u) | (cull_diverged_here ? 8u : 0u) |
      (flow_unjournalled ? 16u : 0u) | (packets_unjournalled ? 32u : 0u);
  if (class_bits == 0u) {
    return;
  }
  ++g_divergence_census.draws_diverged;

  // One line per DISTINCT divergence shape. Shape = which draw root, which
  // shader pair, and which classes disagreed -- so a new content class always
  // gets a line no matter how many repeats of an earlier one preceded it.
  uint64_t shape = 1469598103934665603ull;
  const auto mix = [&shape](uint64_t value) {
    shape = (shape ^ value) * 1099511628211ull;
  };
  mix(packet.source_symbol);
  mix(packet.vertex_shader);
  mix(packet.pixel_shader);
  mix(class_bits);
  if (g_divergence_logged_shapes.size() >= 512u ||
      !g_divergence_logged_shapes.insert(shape).second) {
    return;
  }
  ++g_divergence_census.distinct_shapes;
  REXLOG_INFO(
      "Refii GTL device-state DIVERGENCE shape #{} root=sub_{:08X} "
      "vs=0x{:08X} ps=0x{:08X} classes=[vs_float={} ps_float={} fetch={} "
      "cull={} flow_unjournalled={} packets_unjournalled={}] "
      "masks=[{:016X} {:016X} {:016X} {:016X} {:016X}] pred2={:016X} "
      "dirty[vs_blocks={} ps_blocks={}] mode_control=0x{:08X} "
      "mirror_cull=0x{:08X} packet_id={}",
      static_cast<unsigned long long>(g_divergence_census.distinct_shapes),
      packet.source_symbol, packet.vertex_shader, packet.pixel_shader,
      vs_diverged, ps_diverged, fetch_diverged, cull_diverged_here ? 1 : 0,
      flow_unjournalled ? 1 : 0, packets_unjournalled ? 1 : 0,
      static_cast<unsigned long long>(snap.pending_mask[0]),
      static_cast<unsigned long long>(snap.pending_mask[1]),
      static_cast<unsigned long long>(snap.pending_mask[2]),
      static_cast<unsigned long long>(snap.pending_mask[3]),
      static_cast<unsigned long long>(snap.pending_mask[4]),
      static_cast<unsigned long long>(snap.predicated_pending_mask2),
      static_cast<unsigned long long>(snap.vs_dirty_blocks.size()),
      static_cast<unsigned long long>(snap.ps_dirty_blocks.size()),
      snap.mode_control, packet.render_states[kSnapCullRenderStateSlot],
      static_cast<unsigned long long>(packet.packet_id));
}

// The D3D device for a draw root. Most roots take it in r3; the ones that do
// not fetch the g_D3DDevice_PresentModule singleton themselves (IDA), so read
// the same global rather than trusting an argument that is not a pointer.
// Returns 0 when nothing usable resolves, which every caller treats as "no
// live device read".
uint32_t ResolveDrawRootDevicePointer(uint32_t symbol_address, uint32_t r3,
                                      uint8_t *guest_base) {
  constexpr uint32_t kD3DDevicePresentModule = 0x83360364u;
  if (refii::renderer::IsLikelyFableObjectAddress(r3)) {
    return r3;
  }
  if (guest_base == nullptr) {
    return 0u;
  }
  const uint32_t singleton =
      refii::renderer::bink::IsReadableGuestByteRange(
          guest_base, kD3DDevicePresentModule, sizeof(uint32_t))
          ? refii::renderer::bink::LoadGuestU32(guest_base,
                                                kD3DDevicePresentModule)
          : 0u;
  (void)symbol_address;
  return refii::renderer::IsLikelyFableObjectAddress(singleton) ? singleton
                                                                : 0u;
}

void BuildDrawPacketLocked(uint32_t symbol_address,
                           const RendererHookArgs &args, uint8_t *guest_base) {
  DrawPacket packet;
  packet.packet_id = g_state.next_packet_id++;
  t_last_packet_id = packet.packet_id;
  t_last_packet_symbol = symbol_address;
  t_last_packet_device = args.r3;
  t_last_packet_guest_base = guest_base;
  packet.frame_id = g_state.frame_id;
  // Guest-frame stamp for backlog supersession (run 485): the frame in
  // progress at this draw. g_state.mutex is already held (all packet builds
  // and the kPresent epoch increment run under it), so this costs nothing
  // and is exactly coherent with guest_frame_boundary_packet_id — no
  // per-draw acquisition of any renderer/direct-state mutex on the guest
  // thread.
  packet.guest_frame_ordinal =
      static_cast<uint32_t>(g_state.stated_present_epoch) + 1u;
  packet.source_symbol = symbol_address;
  packet.scene_submit_depth = g_state.scene_submit_depth;

  const DeviceState &device = g_state.device;
  packet.vertex_shader = device.vertex_shader;
  packet.pixel_shader = device.pixel_shader;
  packet.render_targets = device.render_targets;
  packet.depth_target = device.depth_target;

  // Backfill shaders / targets from the DEVICE OBJECT at the draw root.
  //
  // Why this is needed: g_state.device is fed by kSetVertexShader /
  // kSetPixelShader / kSetRenderTarget, and Fable barely calls them — run 132
  // logged them 2, 2 and 8 times against ~168 draws PER FRAME, because the
  // game stages state into the device object and flushes at draw time rather
  // than using the D3D setter API (docs/refii_render_path_map.md §2). The exec
  // shadow measured the cost of that: of 45 sampled packets only 15 were
  // executable, missing vs=23 ps=23 rt=17 — while vb/ib missed ZERO, i.e.
  // geometry was complete and only the staged state was absent.
  //
  // Why it is still call-stated and not inference: a draw root IS the flush
  // point. The device's current shader/target at this instant is exactly what
  // this call is about to draw with, so reading it here is reading what the
  // call itself states, in the same category as kSetScissorRect reading the
  // RECT through the pointer its argument names. Offsets are the ones the
  // observer already uses (hook_observer.cpp kFableDevice*Offset).
  //
  // Staged values win: only backfill a field the setters left at zero.
  ++g_state.stats.draw_packet_builds;
  if (packet.vertex_shader != 0) {
    ++g_state.stats.shader_from_device_state;
  }
  // NOT EVERY DRAW ROOT TAKES THE DEVICE IN r3 (IDA, 2026-08-02). The dominant
  // in-game root sub_8220A3F0 is the indexed UP helper
  // `(vertexCount, primCount, pIndices, pVerts, stride)` -- its r3 is a COUNT,
  // and it fetches the device from the g_D3DDevice_PresentModule singleton
  // itself (`v12 = g_D3DDevice_PresentModule` right before
  // D3DDevice_BeginIndexedVertices). It was 15,288 of the world frame's draws
  // in run 535, so every live device read below was addressed off a small
  // integer for the MAJORITY of gameplay draws: the reads silently failed the
  // mapped-range check and fell back to the tracked mirror. The constants
  // block further down already resolved the singleton for this root; do it
  // once here so the render-target / depth / shader / RB_DEPTHCONTROL reads
  // get the same treatment, and validate it either way.
  const uint32_t device_pointer = ResolveDrawRootDevicePointer(
      symbol_address, args.r3, guest_base);
  if (guest_base != nullptr && device_pointer != 0) {
    g_state.last_draw_device_pointer = device_pointer;
    if (packet.vertex_shader == 0) {
      ++g_state.stats.shader_backfill_attempted;
    }
    constexpr uint32_t kDevicePixelShaderOffset = 0x3194;
    constexpr uint32_t kDeviceVertexShaderOffset = 0x3198;
    constexpr uint32_t kDeviceRenderTargetBaseOffset = 0x3098;
    constexpr uint32_t kDeviceDepthStencilOffset = 0x30A8;
    const auto load = [&](uint32_t offset) -> uint32_t {
      return refii::renderer::bink::IsReadableGuestByteRange(
                 guest_base, device_pointer + offset, sizeof(uint32_t))
                 ? refii::renderer::bink::LoadGuestU32(
                       guest_base, device_pointer + offset)
                 : 0u;
    };
    if (packet.vertex_shader == 0) {
      packet.vertex_shader = load(kDeviceVertexShaderOffset);
      if (packet.vertex_shader != 0) {
        ++g_state.stats.shader_backfill_filled;
      }
    }
    if (packet.pixel_shader == 0) {
      packet.pixel_shader = load(kDevicePixelShaderOffset);
    }
    // Targets are LIVE-WINS, not zero-backfill (2026-08-02). "Staged values
    // win" assumed the mirror is either current or zero, but the shadow
    // renderer binds its per-light depth surface through the render-target-set
    // class (sub_8226B078 via sub_8228DCC8, IDA), which the hooked setters
    // never see: the mirror then holds the PREVIOUS pass's nonzero target and
    // the backfill never corrects it — routing shadow-pass draws at the wrong
    // target. By this block's own rationale the draw root is the flush point,
    // so the device register at this instant IS the call's stated target;
    // the mirror remains the fallback when the register is unreadable/zero.
    for (uint32_t rt = 0; rt < kMaxRenderTargets; ++rt) {
      const uint32_t live_target =
          load(kDeviceRenderTargetBaseOffset + rt * sizeof(uint32_t));
      if (live_target != 0) {
        packet.render_targets[rt] = live_target;
      }
    }
    const uint32_t live_depth = load(kDeviceDepthStencilOffset);
    if (live_depth != 0) {
      packet.depth_target = live_depth;
    }
    // RB_DEPTHCONTROL, verbatim (see DrawPacket::live_depth_control). Zero is
    // a legal value here (it means depth+stencil test disabled), so validity
    // rides the READ succeeding, never on the value being non-zero.
    constexpr uint32_t kDeviceDepthControlOffset = 0x2934;
    const uint32_t depth_control_address = device_pointer + kDeviceDepthControlOffset;
    if (refii::renderer::bink::IsReadableGuestByteRange(
            guest_base, depth_control_address, sizeof(uint32_t))) {
      packet.live_depth_control =
          refii::renderer::bink::LoadGuestU32(guest_base, depth_control_address);
      packet.live_depth_control_valid = true;
    }
  }
  packet.textures = device.textures;
  packet.vertex_buffers = device.vertex_buffers;
  packet.vertex_offsets = device.vertex_offsets;
  packet.vertex_strides = device.vertex_strides;
  packet.index_buffer = device.index_buffer;
  packet.vertex_declaration = device.vertex_declaration;
  packet.viewport = device.viewport;
  packet.scissor = device.scissor;
  packet.render_states = device.render_states;
  packet.render_state_valid_mask = device.render_state_valid_mask;
  packet.sampler_states = device.sampler_states;
  packet.sampler_state_valid_mask = device.sampler_state_valid_mask;
  packet.vertex_buffer_pointers = device.vertex_buffer_pointers;
  packet.index_buffer_pointer = device.index_buffer_pointer;
  packet.fetch_bases = device.fetch_bases;

  switch (symbol_address) {
    case kDrawPrimitive:
      packet.primitive_type = args.r4;
      packet.start_index = args.r5;
      packet.primitive_count = args.r6;
      break;
    case kDrawIndexedPrimitive:
      packet.indexed = true;
      packet.primitive_type = args.r4;
      packet.base_vertex = args.r5;
      packet.start_index = args.r6;
      packet.index_count = args.r7;
      break;
    case kDrawPrimitiveUP:
      packet.draw_up = true;
      packet.primitive_type = args.r4;
      packet.primitive_count = args.r5;
      packet.up_vertex_data = args.r6;
      packet.up_vertex_stride = args.r7;
      break;
    case kBeginVertices:
      // IDA-pinned (sub_82205F68): (device r3, PrimitiveType r4,
      // VertexCount r5, VertexStreamZeroStride r6) — the ring allocates
      // (VertexCount*stride)>>2 dwords and VertexCount shifts into the draw
      // initiator. The wrapper RETURNS the ring write pointer (exit r3),
      // pinned at the next hook entry.
      packet.draw_up = true;
      packet.primitive_type = args.r4;
      packet.primitive_count = args.r5;
      packet.up_vertex_stride = args.r6;
      break;
    case kBeginIndexedVertices:
      // SEPARATE ARG MAP — do NOT fold this back into the kBeginVertices arm.
      // These two roots share nothing past r4. Sharing one case (until
      // 2026-07-25) put BaseVertexIndex in primitive_count, NumVertices in
      // up_vertex_stride, and dropped IndexCount / index format / the real
      // vertex stride on the floor.
      //
      // IDA-verified (sub_82217DB8, decompile + disasm 2026-07-25):
      //   r3  pDevice              r4  PrimitiveType     r5  BaseVertexIndex
      //   r6  NumVertices          r7  IndexCount        r8  IndexDataFormat
      //   r9  VertexStreamZeroStride
      //   r10 ppIndexData [out]    caller_sp+0x54 ppVertexData [out]
      // Body proof of the three load-bearing roles:
      //   * `(NumVertices * VertexStreamZeroStride) >> 2` (r6*r9, @0x82217DC8/
      //     0x82217DE4) sizes the VERTEX ring allocation.
      //   * `IndexDataFormat & 4` (@0x82217DE0) selects 32-bit vs 16-bit
      //     indices — it ORs 0x800 into the draw initiator (Xenos INDEX_SIZE).
      //   * the INDEX ring allocation is IndexCount dwords when 32-bit, else
      //     `(IndexCount + 1) >> 1` (@0x82217DF8).
      // IndexCount is also the value that shifts into the draw initiator's
      // num_indices field (`v39 = PrimitiveType & 0x3F | (IndexCount << 16)`).
      //
      // primitive_count keeps its documented MISNOMER meaning (a VERTEX count,
      // see the field comment in refii_gtl.h) — NumVertices is exactly that,
      // and it is what the vertex-payload byte math needs.
      packet.draw_up = true;
      packet.indexed = true;
      packet.primitive_type = args.r4;
      packet.base_vertex = args.r5;
      packet.primitive_count = args.r6;
      packet.index_count = args.r7;
      packet.index_format = args.r8;
      packet.up_vertex_stride = args.r9;
      // The OUT-PARAM ADDRESSES, not the rings — the callee has not written
      // them yet at enter. Stashed for the exit pin (see the thread_locals'
      // comment for why they are unrecoverable at exit).
      t_begin_indexed_index_out_address = args.r10;
      t_begin_indexed_vertex_out_address =
          args.r1 != 0 ? args.r1 + kBeginIndexedVerticesVertexOutStackOffset
                       : 0u;
      break;
    // kResolve DELETED 2026-08-04 with the legacy resolve DrawPacket build in
    // OnHookEnter. Its three assignments were known-meaningless under the
    // corrected arg map (r4 is Flags, r5 is pSourceRect — a resolve has no
    // primitive type and no vertex data), and BuildDrawPacketLocked is no
    // longer reached with this symbol. Read ResolvePacket instead.
    case kDrawIndexedUserPrimitives: {
      // (vertex_count=r3, prim_count=r4, index_ptr=r5, vertex_ptr=r6,
      // stride=r7); triangle list, 16-bit indices (6 bytes per primitive).
      packet.draw_up = true;
      packet.indexed = true;
      packet.primitive_type = 4;  // D3DPT_TRIANGLELIST
      packet.primitive_count = args.r4;
      packet.index_count = args.r4 * 3u;
      packet.up_vertex_count = args.r3;
      packet.up_vertex_data = args.r6;
      packet.up_vertex_stride = args.r7;
      packet.up_index_data = args.r5;
      const uint64_t vertex_bytes = uint64_t(args.r3) * uint64_t(args.r7);
      const uint64_t index_bytes = uint64_t(args.r4) * 6u;
      constexpr uint64_t kMaxInlineGeometryBytes = 1u << 20;
      if (guest_base != nullptr && vertex_bytes != 0 &&
          vertex_bytes <= kMaxInlineGeometryBytes && index_bytes != 0 &&
          index_bytes <= kMaxInlineGeometryBytes &&
          refii::renderer::bink::IsReadableGuestByteRange(
              guest_base, args.r6, static_cast<uint32_t>(vertex_bytes)) &&
          refii::renderer::bink::IsReadableGuestByteRange(
              guest_base, args.r5, static_cast<uint32_t>(index_bytes))) {
        packet.inline_vertex_data.resize(vertex_bytes);
        std::memcpy(packet.inline_vertex_data.data(),
                    guest_base + args.r6 +
                        refii::renderer::bink::GuestPhysicalHostOffset(args.r6),
                    vertex_bytes);
        packet.inline_index_data.resize(index_bytes);
        std::memcpy(packet.inline_index_data.data(),
                    guest_base + args.r5 +
                        refii::renderer::bink::GuestPhysicalHostOffset(args.r5),
                    index_bytes);
      }
      break;
    }
    default:
      break;
  }

  // Stream-0 stride authority for the stride-carrying roots (UP draws and
  // Begin[Indexed]Vertices). The mirror copied above only advances on
  // SetStreamSource, but the guest treats the wrapper's stride argument as
  // stream 0's stride for exactly this draw: Begin*Vertices installs
  // (Stride>>2) into the device's per-slot stride byte (dev+0x30F0)
  // TRANSIENTLY — saved at 0x82205F88/0x82217E04, restored at
  // 0x822061EC/0x82218054 before returning — so every post-return read (ours
  // included) sees the PREVIOUS binding. Run 497: a stale 48-byte stride over
  // real 12-byte quad-list vertices inflated the bind requirement 4x
  // (missing_vertex_input on the draws allocated near the arena top) and
  // mis-strode the survivors. The argument is the same value the guest
  // patches into the VS microcode vfetch for this draw; present it as the
  // stream-0 stride.
  if (packet.draw_up && packet.up_vertex_stride != 0) {
    packet.vertex_strides[0] = packet.up_vertex_stride;
  }

  // Constants capture (§3.2 self-containment): the device object holds the
  // at-draw float banks; function-level interception means they are complete
  // here. The 8220A3F0 wrapper carries no device argument — its callers use
  // the device singleton at 0x83360364.
  {
    constexpr uint32_t kFloatConstantDwords = 1024;
    constexpr uint32_t kVertexBankOffset = 120u * 16u;
    constexpr uint32_t kPixelBankOffset = 376u * 16u;
    // Same resolution as the live-read block above: r3 when it really is the
    // device, else the g_D3DDevice_PresentModule singleton. Previously this
    // special-cased only kDrawIndexedUserPrimitives, so any OTHER root whose
    // r3 is not the device fell through to reading constants off a non-pointer
    // -- and a failed read leaves the bank vector EMPTY, which downstream is
    // indistinguishable from "no constants" and lands as a zero matrix.
    const uint32_t device =
        ResolveDrawRootDevicePointer(symbol_address, args.r3, guest_base);
    // Vertex declaration authority (§3.2): the bound decl lives in the device
    // object (offset 11820), which the observer reads directly in
    // RefreshDeviceInputBindingsLocked. Hook-tracked device.vertex_declaration
    // only sees XGSetVertexDeclaration and misses the shader-patch bind path
    // (the inline-UP / Bink-compose decl), leaving it stale/wrong. Read the
    // live device register here — the same at-draw source the observer trusts —
    // and let it win when present. Guarded non-zero so a transient read never
    // clobbers a validly tracked decl.
    if (guest_base != nullptr && device != 0) {
      constexpr uint32_t kDeviceVertexDeclarationOffset = 11820u;
      const uint32_t live_declaration = refii::renderer::bink::LoadGuestU32(
          guest_base, device + kDeviceVertexDeclarationOffset);
      if (live_declaration != 0) {
        packet.vertex_declaration = live_declaration;
      }
    }
    const auto read_bank = [&](uint32_t bank_offset,
                               std::vector<uint32_t>& out) {
      if (guest_base == nullptr || device == 0) {
        return;
      }
      const uint32_t address = device + bank_offset;
      const uint32_t bytes = kFloatConstantDwords * 4u;
      if (!refii::renderer::bink::IsReadableGuestByteRange(guest_base, address,
                                                           bytes)) {
        return;
      }
      const uint8_t* block =
          guest_base + address +
          refii::renderer::bink::GuestPhysicalHostOffset(address);
      out.resize(kFloatConstantDwords);
      for (uint32_t dword_index = 0; dword_index < kFloatConstantDwords;
           ++dword_index) {
        uint32_t raw = 0;
        std::memcpy(&raw, block + dword_index * 4u, sizeof(raw));
        out[dword_index] = __builtin_bswap32(raw);
      }
    };
    // Float-bank ENTER source (feed-7 deletion, 2026-08-02): with
    // renderer_gtl_constant_self_read ON (default) the DIRECT DEVICE READ is
    // preferred over the observer-fed mirror. The device block at
    // dev+0x780/0x1780 is the guest's own constant staging — the deferred PM4
    // SET_CONSTANT flush lands there, and it also holds writes NO hook feed
    // can see (sub_82214E30's direct VMX memcpy, the WVP-zero carrier), so it
    // is a superset of every fed carrier. The fed mirror remains the FALLBACK
    // when the device pointer/base is unusable at this root (e.g. the quad
    // wrapper's singleton read failing), and the authoritative EXIT self-read
    // still overwrites both banks post-flush regardless. Note the ENTER value
    // is pre-flush either way (previous draw's bank) — the cvar changes the
    // SOURCE, not the staleness class; the exit re-read is what makes the
    // packet correct, and the divergence census there grades fed-vs-device.
    const bool constant_self_read =
        refii::renderer::RendererGtlConstantSelfReadEnabled();
    if (constant_self_read) {
      read_bank(kVertexBankOffset, packet.vs_float_constants);
      if (packet.vs_float_constants.empty() &&
          g_state.device.vs_float_bank_valid) {
        packet.vs_float_constants.assign(g_state.device.vs_float_bank.begin(),
                                         g_state.device.vs_float_bank.end());
      }
    } else if (g_state.device.vs_float_bank_valid) {
      packet.vs_float_constants.assign(g_state.device.vs_float_bank.begin(),
                                       g_state.device.vs_float_bank.end());
    } else {
      read_bank(kVertexBankOffset, packet.vs_float_constants);
    }
    // c0-c3 (WorldViewProjection) live-device-memory read for content that
    // needs it (2026-07-25, IDA-traced to sub_82214E30, a direct VMX memcpy
    // into device memory that bypasses PM4 SET_CONSTANT entirely -- see
    // refii-tree-billboard-actual-root-cause-wvp-zero.md). REVERTED here as
    // a blanket override for every kDrawIndexedUserPrimitives (0x8220A3F0)
    // draw: that root is shared by ~26 content classes (Bink, UI panels,
    // fullscreen composites, tree billboards, ...), most of which get a
    // CORRECT (often intentionally different, e.g. orthographic/identity)
    // transform via the normal PM4 path and don't read this device-memory
    // slot at all. Broadcasting one live read to all of them produced a
    // solid white screen (run 258, 2026-07-25): every consumer got
    // whichever unrelated content's matrix happened to be sitting there
    // last. The fix must be scoped to the specific vertex shader identity
    // proven to need it (0097_vshader_water_tessellator_zpass /
    // kFableTitleWaterTessellatorZPassVertexHash), which this GTL-layer
    // capture code cannot check (shader-hash classification lives in
    // fable_direct_renderer.cpp). Apply the override there instead, at
    // packet->command conversion time, alongside the existing tree-billboard
    // texture-slot override -- captured unconditionally into a SEPARATE
    // field (live_vertex_c0_c3) here so the converter can opt in per-shader
    // instead of this layer having to (and getting it wrong).
    if (symbol_address == kDrawIndexedUserPrimitives && guest_base != nullptr &&
        device != 0) {
      constexpr uint32_t kWorldViewProjectionDwords = 16;
      const uint32_t address = device + kVertexBankOffset;
      if (refii::renderer::bink::IsReadableGuestByteRange(
              guest_base, address, kWorldViewProjectionDwords * 4u)) {
        const uint8_t* block =
            guest_base + address +
            refii::renderer::bink::GuestPhysicalHostOffset(address);
        for (uint32_t dword_index = 0; dword_index < kWorldViewProjectionDwords;
             ++dword_index) {
          uint32_t raw = 0;
          std::memcpy(&raw, block + dword_index * 4u, sizeof(raw));
          packet.live_vertex_c0_c3[dword_index] = __builtin_bswap32(raw);
        }
        packet.live_vertex_c0_c3_valid = true;
      }
    }
    if (constant_self_read) {
      read_bank(kPixelBankOffset, packet.ps_float_constants);
      if (packet.ps_float_constants.empty() &&
          g_state.device.ps_float_bank_valid) {
        packet.ps_float_constants.assign(g_state.device.ps_float_bank.begin(),
                                         g_state.device.ps_float_bank.end());
      }
    } else if (g_state.device.ps_float_bank_valid) {
      packet.ps_float_constants.assign(g_state.device.ps_float_bank.begin(),
                                       g_state.device.ps_float_bank.end());
    } else {
      read_bank(kPixelBankOffset, packet.ps_float_constants);
    }
    // SHADER-LITERAL OVERLAY. The device banks read above are only half the
    // constant file: D3D_SetLiteralShaderConstants loads the bound shader's
    // embedded literals straight into the GPU's ALU registers by PM4 (type-3
    // opcode 0x2F), never touching dev+0x780/0x1780, and it runs AFTER the
    // shadow flush in the same draw root -- so for the registers it covers,
    // the literal is what the hardware samples. Apply it on top, in that
    // order. Registers no literal has written keep the device value.
    // See ApplyLiteralShaderConstants; this is what un-zeroed the
    // WorldViewProjection for the literal-driven draws.
    {
      const auto& covered = g_state.device.literal_alu_covered;
      const auto& literal = g_state.device.literal_alu;
      const auto overlay = [&](std::vector<uint32_t>& bank,
                               uint32_t register_base) {
        if (bank.empty()) {
          return;
        }
        for (uint32_t slot = 0; slot < 256u; ++slot) {
          const uint32_t reg = register_base + slot;
          if (covered[reg] == 0) {
            continue;
          }
          const size_t dword = static_cast<size_t>(slot) * 4u;
          if (dword + 4u > bank.size()) {
            break;
          }
          for (uint32_t component = 0; component < 4u; ++component) {
            bank[dword + component] =
                literal[static_cast<size_t>(reg) * 4u + component];
          }
        }
      };
      overlay(packet.vs_float_constants, 0u);
      overlay(packet.ps_float_constants, 256u);
    }
    // CONSTANT-BANK FORENSICS (2026-08-02). Both device banks read all-zero
    // for every draw class -- UI quads AND world terrain -- so geometry
    // collapses (post-VS clip=(0,0,0,0)). The device pointer is provably good
    // (RT0 at +0x3098 and RB_DEPTHCONTROL at +0x2934 read correct values from
    // it), so this asks the next question directly: is the WHOLE 256-register
    // bank empty (nobody wrote it / wrote elsewhere) or just c0-c3 (in-use
    // gating, or a literal carrier we still miss)? Also reports the guest's
    // own pending dirty mask -- non-zero there means the guest believes it has
    // constants staged for the flush -- and how many registers our literal
    // overlay has claimed.
    {
      static std::atomic<uint32_t> logged_bank_forensics{0};
      const uint32_t n = ++logged_bank_forensics;
      if (n <= 24u && guest_base != nullptr && device != 0) {
        uint32_t vs_nonzero = 0;
        uint32_t vs_first_nonzero_reg = 0xFFFFFFFFu;
        for (uint32_t dword = 0; dword < packet.vs_float_constants.size();
             ++dword) {
          if (packet.vs_float_constants[dword] != 0) {
            ++vs_nonzero;
            if (vs_first_nonzero_reg == 0xFFFFFFFFu) {
              vs_first_nonzero_reg = dword / 4u;
            }
          }
        }
        uint32_t ps_nonzero = 0;
        for (const uint32_t value : packet.ps_float_constants) {
          if (value != 0) {
            ++ps_nonzero;
          }
        }
        const auto load_dword = [&](uint32_t offset) -> uint32_t {
          const uint32_t at = device + offset;
          return refii::renderer::bink::IsReadableGuestByteRange(
                     guest_base, at, sizeof(uint32_t))
                     ? refii::renderer::bink::LoadGuestU32(guest_base, at)
                     : 0xDEADBEEFu;
        };
        REXLOG_INFO(
            "Refii GTL const forensics #{} src=0x{:08X} device=0x{:08X} "
            "vs_nonzero={}/{} first_nz_reg={} ps_nonzero={}/{} "
            "pending_mask0=0x{:08X}{:08X} pending_mask1=0x{:08X}{:08X} "
            "literal_regs={} vs=0x{:08X} ps=0x{:08X}",
            n, symbol_address, device, vs_nonzero,
            static_cast<uint32_t>(packet.vs_float_constants.size()),
            vs_first_nonzero_reg, ps_nonzero,
            static_cast<uint32_t>(packet.ps_float_constants.size()),
            load_dword(0x0u), load_dword(0x4u), load_dword(0x8u),
            load_dword(0xCu),
            static_cast<unsigned long long>(
                g_state.device.literal_alu_registers_written),
            packet.vertex_shader, packet.pixel_shader);
      }
    }
    // WVP-ZERO TRACE (2026-07-25): the quad-wrapper root (0x8220A3F0 /
    // kDrawIndexedUserPrimitives) is used by ~26 different content classes
    // (Bink, UI, tree billboards, procedural primitives, ...) sharing this
    // ONE capture path and the ONE global vs_float_bank. Tree-billboard
    // draws' actual GPU-side WorldViewProjection (registers c0-c3, dwords
    // 0-15) was proven all-zero at Vulkan-submission time (live capture,
    // see refii-tree-billboard-actual-root-cause-wvp-zero.md). This log
    // answers whether it's ALREADY zero right here at GTL capture time
    // (an observer/PM4-decode gap -- some other 0x8220A3F0 caller stomped
    // the global bank before this draw) or still correct here (meaning the
    // bug is downstream, in the packet->command->Vulkan pipeline instead).
    if (symbol_address == kDrawIndexedUserPrimitives) {
      static uint32_t logged_quad_wvp_trace = 0;
      if (logged_quad_wvp_trace < 64u) {
        ++logged_quad_wvp_trace;
        const auto& wvp = packet.vs_float_constants;
        const bool have16 = wvp.size() >= 16u;
        // Bank-identity probe (2026-08-02): the VS bank read here (dev+0x780,
        // vector index 120, written by sub_8221A880) is all-zero for these
        // draws, so their WorldViewProjection is a zero matrix and every
        // vertex collapses to clip=(0,0,0,0) -- RenderDoc post-VS confirms
        // ndc=inf and pixel history finds no draw covering any pixel. The
        // other candidate is dev+0x1780 (index 376, written by sub_82214E30),
        // which the engine's own Render_SetShaderConstantMatrixById
        // (0x82287478) uses for MATRIX constants by binding id. Print the head
        // of BOTH banks so one run names the bank that actually carries the
        // matrix, instead of flipping the source on a guess -- broadcasting an
        // unverified c0-c3 override is exactly what produced the solid white
        // screen in run 258.
        const auto& ps_bank = packet.ps_float_constants;
        const bool ps_have8 = ps_bank.size() >= 8u;
        REXLOG_INFO(
            "Refii GTL quad-wrapper bank probe #{} other_bank(0x1780) "
            "c0=({},{},{},{}) c1=({},{},{},{})",
            logged_quad_wvp_trace,
            ps_have8 ? std::bit_cast<float>(ps_bank[0]) : 0.0f,
            ps_have8 ? std::bit_cast<float>(ps_bank[1]) : 0.0f,
            ps_have8 ? std::bit_cast<float>(ps_bank[2]) : 0.0f,
            ps_have8 ? std::bit_cast<float>(ps_bank[3]) : 0.0f,
            ps_have8 ? std::bit_cast<float>(ps_bank[4]) : 0.0f,
            ps_have8 ? std::bit_cast<float>(ps_bank[5]) : 0.0f,
            ps_have8 ? std::bit_cast<float>(ps_bank[6]) : 0.0f,
            ps_have8 ? std::bit_cast<float>(ps_bank[7]) : 0.0f);
        REXLOG_INFO(
            "Refii GTL quad-wrapper WVP trace #{} vs_float_bank_valid={} "
            "ps=0x{:08X} vs=0x{:08X} c0=({},{},{},{}) c1=({},{},{},{})",
            logged_quad_wvp_trace, g_state.device.vs_float_bank_valid ? 1 : 0,
            packet.pixel_shader, packet.vertex_shader,
            have16 ? std::bit_cast<float>(wvp[0]) : 0.0f,
            have16 ? std::bit_cast<float>(wvp[1]) : 0.0f,
            have16 ? std::bit_cast<float>(wvp[2]) : 0.0f,
            have16 ? std::bit_cast<float>(wvp[3]) : 0.0f,
            have16 ? std::bit_cast<float>(wvp[4]) : 0.0f,
            have16 ? std::bit_cast<float>(wvp[5]) : 0.0f,
            have16 ? std::bit_cast<float>(wvp[6]) : 0.0f,
            have16 ? std::bit_cast<float>(wvp[7]) : 0.0f);
      }
    }

    // PHASE 1 (CAPTURE + COUNT ONLY): the device object's own state, read here
    // at the draw root's ENTRY. Must be at entry -- BeginVertices opens by
    // draining all five pending masks through sub_8221DE68, so by exit
    // m_Mask[0..4] are ALL ZERO and the per-draw delta is gone. Nothing
    // consumes device_snapshot; it exists so the divergence census can measure
    // how stale the mirrors above are before Phase 2 decides what to delete.
    packet.device_snapshot = CaptureDrawDeviceSnapshot(device, guest_base);
    DrainDeviceDirtyMasksLocked(device, guest_base);

    // PHASE 2 CUTOVER (FETCH CLASS ONLY -- nothing else in this packet changes
    // source). Run 284 measured the mirror wrong on 10420 of 12244 compared
    // slots (85%); the mirror is a hook-fed cache that only learns a slot when
    // D3D_SetPending_FetchConstants fires with that slot dirty, while
    // device+0x480 is the storage D3DDevice_SetTexture writes directly. Take
    // the device.
    //
    // EXCLUDED: kDrawIndexedUserPrimitives (sub_8220A3F0). That root runs
    // Render_FlushDirtyRenderStates() as its first statement, so its ENTRY
    // fetch block is the previous draw's -- the classic stale-by-one read this
    // cutover must not introduce. It already re-reads the same block at EXIT
    // (CaptureDeviceFetchConstantsIntoPacketLocked in OnHookExit), which is the
    // correct instant for it, and that path is left exactly as it was.
    //
    // ALSO EXCLUDED: kResolve. sub_82206888 is an EDRAM resolve that reaches
    // this function only through the legacy additive DrawPacket build; it has
    // no texture bindings to source and setting fetch_dwords_valid on it would
    // be a change outside the fetch-binding class this cutover is scoped to.
    const bool draw_root_for_fetch_cutover =
        symbol_address == kBeginVertices ||
        symbol_address == kBeginIndexedVertices ||
        symbol_address == kDrawPrimitive ||
        symbol_address == kDrawIndexedPrimitive ||
        symbol_address == kDrawPrimitiveUP;
    if (FetchDeviceSourced() && draw_root_for_fetch_cutover) {
      ApplyDeviceSourcedFetchToPacket(packet);
      ++g_divergence_census.fetch_device_sourced_draws;
    } else if (symbol_address == kDrawIndexedUserPrimitives) {
      ++g_divergence_census.fetch_preflush_root_draws;
    }
    AccumulateDeviceSnapshotDivergenceLocked(packet);
  }

  // Per-family packet-completeness census (staged observer-removal aid): for
  // each draw-root family, does the GTL packet carry the data needed to OWN
  // the draw (geometry + identity), i.e. is the observer's handling of this
  // family redundant? Accumulated under g_state.mutex; logged in
  // OnFrameBoundary. geom = inline UP bytes present; streamvb = a bound vertex
  // buffer; ident = both shaders set; decl = vertex declaration set.
  {
    FamilyCoverage& cov = g_family_coverage[symbol_address];
    ++cov.total;
    if (!packet.inline_vertex_data.empty() || packet.up_vertex_count != 0) {
      ++cov.geom;
    }
    if (packet.vertex_buffers[0] != 0) {
      ++cov.streamvb;
    }
    if (packet.vertex_shader != 0 && packet.pixel_shader != 0) {
      ++cov.ident;
    }
    if (packet.vertex_declaration != 0) {
      ++cov.decl;
    }
    if (packet.textures[0] != 0) {
      ++cov.tex0;
    }
    if (packet.render_targets[0] != 0) {
      ++cov.rt0;
    }
  }

  if (packet.vertex_shader == 0) {
    ++g_state.stats.shader_still_absent;
    // Name the root that produced a shaderless packet — if these cluster on
    // one source_symbol then args.r3 is not the device there and the backfill
    // never had a chance, which is a different fix from a timing problem.
    static std::unordered_map<uint32_t, uint32_t> absent_by_symbol;
    static uint32_t logged_absent_symbols = 0;
    const uint32_t seen = ++absent_by_symbol[packet.source_symbol];
    if (seen == 1u && logged_absent_symbols < 16u) {
      REXLOG_INFO(
          "Refii GTL packet has no vertex shader (distinct root #{}) "
          "source_symbol=0x{:08X} device_arg=0x{:08X} rt0=0x{:08X}",
          ++logged_absent_symbols, packet.source_symbol, args.r3,
          packet.render_targets[0]);
    }
  }
  g_state.packets.push_back(packet);
  ++g_state.stats.draw_packets;
  if (g_state.packets.size() > 65536u) {
    // A consumer stall must not grow unbounded; drop oldest.
    g_state.packets.erase(g_state.packets.begin(),
                          g_state.packets.begin() + 32768u);
  }
}

// Second packet class: sub_82206888 is D3DDevice_Resolve — a Xenos EDRAM
// resolve, not a vertex draw and not a blit (it emits its own PM4 quad, which
// is why the census sees ident=0, geom=0). Record the op verbatim from the
// wrapper args + tracked device targets.
//
// Arg map IDA-proven 2026-07-17, SUPERSEDING the 2026-07-14 map which was
// inverted (it read r6 as a source texture — it is the DESTINATION; r7 as a
// 4-int source rect — it is a 2-int D3DPOINT; r5 as a dest rect — it is the
// SOURCE rect):
//
//   r4 Flags | r5 pSourceRect (LTRB, RT space) | r6 pDestTexture
//   r7 pDestPoint {x,y} | r8 DestLevel | r9 DestSliceOrFace | r10 pClearColor
//
// The source is EDRAM, named by Flags&7 (0-3 = RT0..3, 4 = DS) — there is no
// source texture — so it comes from the tracked device binding, not an arg.
void BuildResolvePacketLocked(uint32_t symbol_address,
                              const RendererHookArgs &args,
                              uint8_t *guest_base) {
  ResolvePacket packet;
  packet.packet_id = g_state.next_packet_id++;
  packet.frame_id = g_state.frame_id;
  packet.source_symbol = symbol_address;
  packet.flags = args.r4;
  packet.dest_targets = g_state.device.render_targets;
  packet.dest_depth = g_state.device.depth_target;

  const auto read_dwords = [&](uint32_t ptr, uint32_t *out, uint32_t count) {
    if (guest_base == nullptr || ptr == 0 ||
        !refii::renderer::bink::IsReadableGuestByteRange(guest_base, ptr,
                                                         count * 4u)) {
      return false;
    }
    for (uint32_t field = 0; field < count; ++field) {
      out[field] =
          refii::renderer::bink::LoadGuestU32(guest_base, ptr + field * 4u);
    }
    return true;
  };

  // Source: the EDRAM surface Flags&7 names, from the DEVICE's own binding
  // registers, mirror as fallback. The hook mirror only advances on the
  // public SetRenderTarget/SetDepthStencilSurface roots, but the shadow
  // renderer binds its per-light depth surface through the render-target-set
  // class (sub_8226B078 via sub_8228DCC8, IDA 2026-08-01), which the mirror
  // never sees — so mirror-sourced packets named a stale surface for exactly
  // the passes whose resolves were being dropped (run 497). The resolve
  // itself reads the device state for its EDRAM source, so the live register
  // is the authority at this instant; same precedent as the live
  // vertex-declaration read in the draw-packet build above.
  packet.source_select = args.r4 & 7u;
  packet.source_is_depth = packet.source_select == 4u;
  {
    constexpr uint32_t kDeviceRenderTargetBaseOffset = 0x3098;
    constexpr uint32_t kDeviceDepthStencilOffset = 0x30A8;
    uint32_t live_source = 0;
    if (guest_base != nullptr && args.r3 != 0 &&
        (packet.source_is_depth || packet.source_select < kMaxRenderTargets)) {
      const uint32_t offset =
          packet.source_is_depth
              ? kDeviceDepthStencilOffset
              : kDeviceRenderTargetBaseOffset +
                    packet.source_select * static_cast<uint32_t>(sizeof(uint32_t));
      live_source =
          refii::renderer::bink::LoadGuestU32(guest_base, args.r3 + offset);
    }
    if (live_source != 0) {
      packet.source_surface = live_source;
    } else if (packet.source_is_depth) {
      packet.source_surface = g_state.device.depth_target;
    } else if (packet.source_select < kMaxRenderTargets) {
      packet.source_surface =
          g_state.device.render_targets[packet.source_select];
    }
  }
  if (packet.source_surface == 0) {
    ++g_state.stats.resolve_unbound_source;
  }

  // r5: source rect, LTRB. NULL is the ABI default meaning "full surface".
  packet.source_rect_default = args.r5 == 0;
  packet.source_rect_valid =
      !packet.source_rect_default &&
      read_dwords(args.r5, packet.source_rect.data(), 4u);

  // r6: destination texture header — the WRITE target.
  packet.dest_texture = args.r6;
  auto it = g_state.resources.find(args.r6);
  if (it != g_state.resources.end()) {
    packet.dest_registry_object = it->second.guest_address;
    // The memory the header NAMES right now, straight from the registry's
    // record of XGOffsetBaseTextureAddress. Recorded, never derived — this is
    // the destination's only durable identity (the object address is not one:
    // pooled headers get re-pointed).
    packet.dest_base_address = it->second.base_address;
    packet.dest_mip_address = it->second.mip_address;
    packet.dest_address_generation = it->second.address_generation;
  } else if (args.r6 != 0) {
    ++g_state.stats.resolve_unregistered_dest;
    // Dump what the registry ACTUALLY holds when a destination misses. Run 441
    // produced a contradiction that inference cannot settle: the observer shows
    // XGOffsetBaseTextureAddress touching 0x40137C90/0x40137CD0, which ARE
    // resolve destinations, and the GTL saw 618 of those events with every
    // object already registered — yet every destination lookup here misses.
    // Both cannot be true, so print the keys rather than reason about them.
    static uint32_t logged_dest_miss = 0;
    if (logged_dest_miss < 12u) {
      std::string sample;
      uint32_t shown = 0;
      for (const auto &entry : g_state.resources) {
        if (shown++ >= 8u) {
          break;
        }
        sample += fmt::format("{:08X} ", entry.first);
      }
      REXLOG_INFO(
          "Refii GTL resolve dest MISS #{} r6=0x{:08X} registry_size={} "
          "keys=[{}]",
          ++logged_dest_miss, args.r6, g_state.resources.size(), sample);
    }
  }
  // The destination's fetch constant, at the same offset the resolve reads it
  // from (pDestTexture+28..+48) for its tiled-base math. Under the old map this
  // was labelled the source's and its registry misses were explained away as
  // "private-texture proxy headers" — that claim rested on the inverted map and
  // is being re-tested via stats.resolve_unregistered_dest, not inherited.
  constexpr uint32_t kDestTextureFetchOffset = 28u;
  const uint32_t dest_fetch_address = args.r6 + kDestTextureFetchOffset;
  if (args.r6 != 0 && refii::renderer::IsLikelyFableObjectAddress(args.r6) &&
      read_dwords(dest_fetch_address, packet.dest_fetch_dwords.data(), 6u)) {
    for (uint32_t dword : packet.dest_fetch_dwords) {
      packet.dest_fetch_valid |= dword != 0;
    }
  }
  if (!packet.dest_fetch_valid) {
    ++g_state.stats.resolve_no_dest_fetch;
  }

  // r7: dest point {x,y} — TWO ints. NULL is the ABI default meaning {0,0}.
  packet.dest_point_default = args.r7 == 0;
  packet.dest_point_valid = !packet.dest_point_default &&
                            read_dwords(args.r7, packet.dest_point.data(), 2u);
  packet.dest_level = args.r8;
  packet.dest_slice_or_face = args.r9;

  // Clears ride the same call: EndTiling ORs 0x100/0x200 in on every non-final
  // tile to prime EDRAM for the next one, which is why flags&0x300 is common
  // (the old map misread that 0x300 as "batched/RT-select bits").
  if ((packet.flags & 0x100u) != 0) {
    packet.clear_color_valid =
        read_dwords(args.r10, packet.clear_color.data(), 4u);
  }
  if ((packet.flags & 0x200u) != 0) {
    packet.clear_z = static_cast<float>(args.f1);
    // ClearStencil is a STACK arg. The Xbox 360 ABI homes parameters in 8-byte
    // slots from caller_sp+0x10, so ClearZ (slot 8) sits in f1 and ClearStencil
    // (slot 9) lands at caller_sp+0x5C — verified against the resolve's own
    // read (`lwz r6, 0x160+arg_5C(r1)`) and its callers' stores. r1 at hook
    // entry is the caller's sp (pre-prologue). pParameters is at +0x64.
    constexpr uint32_t kClearStencilStackOffset = 0x5Cu;
    uint32_t stencil = 0;
    if (args.r1 != 0 &&
        read_dwords(args.r1 + kClearStencilStackOffset, &stencil, 1u)) {
      packet.clear_stencil = stencil;
    }
  }

  static uint32_t logged_resolve_probe = 0;
  if (logged_resolve_probe < 96u) {
    // dest_fetch is the destination's identity: the header object at r6 is
    // REUSED SCRATCH (run 437: one header carried fetch_base 0x12AE1000 and
    // 0x12CFD000, sizes 207360/829440, fmts 6/7/54), so its address names
    // nothing durable and dest_reg is expected to stay 0. The fetch constant
    // names the console memory this resolve writes into — print it raw so the
    // consumer's fetch-keyed lookup can be checked against the observer's own
    // fetch_base line for the same call.
    REXLOG_INFO(
        "Refii GTL resolve probe #{} flags=0x{:08X} sel={}{} src_surf=0x{:08X} "
        "src_rect=({},{},{},{}){} dest_tex=0x{:08X} dest_reg=0x{:08X} "
        "dest_pt=({},{}){} level={} slice={} dest_fetch_valid={} "
        "dest_fetch=[{:08X} {:08X} {:08X} {:08X} {:08X} {:08X}]",
        ++logged_resolve_probe, packet.flags, packet.source_select,
        packet.source_is_depth ? "(DS)" : "(RT)", packet.source_surface,
        packet.source_rect[0], packet.source_rect[1], packet.source_rect[2],
        packet.source_rect[3], packet.source_rect_default ? "[full]" : "",
        packet.dest_texture, packet.dest_registry_object, packet.dest_point[0],
        packet.dest_point[1], packet.dest_point_default ? "[0,0]" : "",
        packet.dest_level, packet.dest_slice_or_face,
        packet.dest_fetch_valid ? 1 : 0, packet.dest_fetch_dwords[0],
        packet.dest_fetch_dwords[1], packet.dest_fetch_dwords[2],
        packet.dest_fetch_dwords[3], packet.dest_fetch_dwords[4],
        packet.dest_fetch_dwords[5]);
  }

  // Mark the destination as GPU-produced. §4 reads `observer_only` as "content
  // the GTL is missing", but a resolve destination has NO CPU bytes by
  // construction — the content is a host RT→texture copy — so counting it as a
  // content gap overstates the gap and would argue for keeping observer
  // capture machinery that is answering a question nobody should be asking.
  // Recorded against both the stated destination and the registry identity,
  // because the two differ whenever Create never registered the raw header.
  for (const uint32_t key : {packet.dest_registry_object, packet.dest_texture}) {
    if (key == 0) {
      continue;
    }
    auto dest_it = g_state.resources.find(key);
    if (dest_it != g_state.resources.end()) {
      ++dest_it->second.gpu_content_generation;
      MarkResourceChangedLocked(key);
      break;
    }
  }

  // Reproduce the guest's own description of the destination, from the two
  // inputs it derives them from — the Flags argument and the destination
  // texture header. NOT read back from the RB_COPY_* device shadow: the resolve
  // zeroes that shadow before returning, so it reads zero at every hook-visible
  // moment (run 266 measured all four dwords zero on every resolve). See
  // docs/refii_resolve_fields.md §3.1.
  //
  // This is the field the host-copy path should be comparing against, instead
  // of Refii's own tracked destination format — the "fmt MISMATCH" refusals are
  // a comparison between two Refii-side plume enums, not against what the guest
  // actually asked for.
  if (packet.dest_fetch_valid) {
    const uint32_t dest_texture_format =
        ResolveDestTextureFormat(packet.dest_fetch_dwords);
    packet.copy_dest_format = ResolveCopyDestFormat(dest_texture_format);
    packet.copy_dest_number =
        ResolveCopyDestFormatUsesFloatNumber(packet.copy_dest_format) ? 7u : 0u;
    packet.copy_control = ResolveCopyControlFromFlags(packet.flags);
    packet.copy_sample_select = ResolveSampleIndexFromFlags(packet.flags);
    packet.copy_dest_exp_bias = ResolveExpBiasFromFlags(packet.flags);
    packet.copy_fields_valid = true;

    // One line per DISTINCT stated description, not per resolve: the same few
    // passes repeat every frame and a flat cap would fill with the first pass
    // before a storybook/menu resolve ever ran (the log-cap trap from
    // refii-gtl-draw-ownership).
    static std::unordered_map<uint32_t, uint32_t> logged_copy_fields;
    const uint32_t key = (dest_texture_format << 16) |
                         (packet.copy_dest_format << 8) |
                         packet.copy_sample_select;
    if (logged_copy_fields.size() < 64u &&
        logged_copy_fields.try_emplace(key, 0u).second) {
      REXLOG_INFO(
          "Refii GTL resolve COPY-FIELDS (distinct #{}) dest_tex=0x{:08X} "
          "sel={}{} | tex_fmt={} -> copy_fmt={} number={} exp_bias={} | "
          "frag={} sample={} clear[c={} d={}] | raw_ctl=0x{:08X} "
          "flags=0x{:08X} dest_fetch1=0x{:08X}",
          static_cast<uint32_t>(logged_copy_fields.size()), packet.dest_texture,
          packet.source_select, packet.source_is_depth ? "(DS)" : "(RT)",
          dest_texture_format, packet.copy_dest_format, packet.copy_dest_number,
          packet.copy_dest_exp_bias,
          ResolveFragmentSelectFromFlags(packet.flags),
          packet.copy_sample_select,
          ResolveClearsColorFromFlags(packet.flags) ? 1 : 0,
          ResolveClearsDepthFromFlags(packet.flags) ? 1 : 0,
          packet.copy_control, packet.flags, packet.dest_fetch_dwords[1]);
    }
  }

  g_state.resolve_packets.push_back(packet);
  ++g_state.stats.resolve_packets;
  if (g_state.resolve_packets.size() > 65536u) {
    constexpr size_t kTrimCount = 32768u;
    g_state.resolve_packets.erase(g_state.resolve_packets.begin(),
                                  g_state.resolve_packets.begin() + kTrimCount);
  }
}

// Clear capture (§3.4/step-5 submission cutover, clear class). Register mapping
// mirrors the observer's two QueueClearEventLocked call sites verbatim so the
// GTL clear count is directly comparable (census gate before submission owns
// clears). Records only what the wrapper states + the tracked RT/DS binding.
void BuildClearPacketLocked(uint32_t symbol_address,
                            const RendererHookArgs &args, uint8_t *guest_base) {
  ClearPacket packet;
  packet.packet_id = g_state.next_packet_id++;
  packet.frame_id = g_state.frame_id;
  packet.source_symbol = symbol_address;

  uint32_t color_pointer = 0;
  if (symbol_address == kClearF) {
    packet.flags = args.r4;
    color_pointer = args.r6;
    packet.stencil = args.r8;
    // r5 = optional D3DRECT pointer, LTRB int dwords.
    if (guest_base != nullptr && args.r5 != 0 &&
        refii::renderer::bink::IsReadableGuestByteRange(guest_base, args.r5,
                                                        16u)) {
      for (uint32_t i = 0; i < 4u; ++i) {
        packet.rect[i] = static_cast<int32_t>(
            refii::renderer::bink::LoadGuestU32(guest_base,
                                                args.r5 + i * 4u));
      }
      packet.rect_valid = true;
    }
  } else {  // kClearRect: color-slot-0 fill; depth/stencil owned by ClearF.
    packet.flags = 1u;
    color_pointer = args.r8;
    packet.stencil = 0;
    // r4-r7 = LEFT/TOP/RIGHT/BOTTOM — decompile-proven (D3D_ClearRect's tile
    // math treats r4..r6 as left..right and r5..r7 as top..bottom, and its
    // quad batch consumes {r4,r5,r6,r7} as the rect). NOT x/y/w/h; the
    // project-wide rule holds: every Fable rect is LTRB.
    packet.rect[0] = static_cast<int32_t>(args.r4);
    packet.rect[1] = static_cast<int32_t>(args.r5);
    packet.rect[2] = static_cast<int32_t>(args.r6);
    packet.rect[3] = static_cast<int32_t>(args.r7);
    packet.rect_valid = packet.rect[2] > packet.rect[0] &&
                        packet.rect[3] > packet.rect[1];
  }
  packet.depth = std::isfinite(args.f1) ? static_cast<float>(args.f1) : 1.0f;
  if (guest_base != nullptr && color_pointer != 0 &&
      refii::renderer::bink::IsReadableGuestByteRange(guest_base, color_pointer,
                                                      16u)) {
    for (uint32_t i = 0; i < 4u; ++i) {
      packet.color[i] = refii::renderer::bink::LoadGuestU32(
          guest_base, color_pointer + i * 4u);
    }
    packet.color_valid = true;
  }
  packet.scissor = g_state.device.scissor;
  packet.render_targets = g_state.device.render_targets;
  packet.depth_target = g_state.device.depth_target;

  // Predication state (2026-07-25, IDA-traced; offset re-verified 2026-07-26):
  // device + 0x2AB4 is pDevice->m_ZFilter[42], the DWORD
  // D3DDevice_SetPredication (0x822655F0) mirrors its raw mask argument into on
  // every call, both enable and disable. args.r3 is the device pointer for both
  // ClearF and ClearRect (same param-0 convention). See
  // ClearPacket::predication_mask for why this matters.
  {
    const uint32_t device = args.r3;
    if (guest_base != nullptr && device != 0 &&
        refii::renderer::bink::IsReadableGuestByteRange(
            guest_base, device + kDevicePredicationMaskLo, 4u)) {
      packet.predication_mask = refii::renderer::bink::LoadGuestU32(
          guest_base, device + kDevicePredicationMaskLo);
    }
  }

  g_state.clear_packets.push_back(packet);
  ++g_state.stats.clear_packets;
  if (g_state.clear_packets.size() > 65536u) {
    constexpr size_t kTrimCount = 32768u;
    g_state.clear_packets.erase(g_state.clear_packets.begin(),
                                g_state.clear_packets.begin() + kTrimCount);
  }
}

// AUTHORITATIVE EXIT SELF-READ census (step 2, 2026-07-26). Emitted alongside
// the other "Refii GTL stats" lines so a single grep picks it up.
//
// THE FALSIFIER: for every draw root that produces packets, its counter here
// must be > 0. Read it against the per-family coverage lines ("Refii GTL family
// sub_XXXXXXXX total=...") emitted from OnFrameBoundary — a family with
// total > 0 but a zero self-read counter is still being fed by the observer
// and its dispatch feeds must NOT be deleted.
//
// Known-zero-by-design exception: sub_8221DFC0 (draw_indexed) was measured as
// never firing (run 132 — none of its 8 static callers run in the observed
// phase), so total == 0 and self_read == 0 together are consistent.
//
// skip_no_packet / skip_no_device disambiguate a zero: a root that fires but
// whose packet was already drained, or whose device pointer / guest base was
// unusable, lands there instead of silently reading as "never ran".
// Caller holds g_state.mutex.
void LogSelfReadExitCensusLocked() {
  REXLOG_INFO(
      "Refii GTL stats self_read_exit[begin={} begin_indexed={} draw={} "
      "draw_indexed={} draw_up={} quad_up={}] skip[no_packet={} no_device={}] "
      "begin_arm_entries={} state_dispatch_self_read={} "
      "state_dispatch[calls={} bad_args={} not_render={} unchanged={} "
      "bad_slot={}] self_read_sampler={} "
      "exit_feed[self_read_filled={} load_bearing={} both_absent={} "
      "disagreed={} stated_unbind={}]",
      static_cast<unsigned long long>(
          g_state.stats.self_read_exit_begin_vertices),
      static_cast<unsigned long long>(
          g_state.stats.self_read_exit_begin_indexed_vertices),
      static_cast<unsigned long long>(
          g_state.stats.self_read_exit_draw_primitive),
      static_cast<unsigned long long>(
          g_state.stats.self_read_exit_draw_indexed_primitive),
      static_cast<unsigned long long>(
          g_state.stats.self_read_exit_draw_primitive_up),
      static_cast<unsigned long long>(
          g_state.stats.self_read_exit_draw_indexed_user_primitives),
      static_cast<unsigned long long>(
          g_state.stats.self_read_exit_skipped_no_packet),
      static_cast<unsigned long long>(
          g_state.stats.self_read_exit_skipped_no_device),
      // Disambiguates begin=0. Counts how many times the Begin-family exit arm
      // was ENTERED at all. If this is 0, the arm never runs in this scene (the
      // quad wrapper owns the draws and its nested Begin clears the packet slot
      // at its own exit) -- which is a coverage fact, not a self-read failure.
      // If it is non-zero while begin/begin_indexed stay 0, the arm runs but
      // neither applies nor skips, and THAT is a real defect.
      static_cast<unsigned long long>(
          g_state.stats.self_read_exit_begin_arm_entries),
      // Step 1 falsifier: render-state dispatches GTL decoded ITSELF from the
      // guest record. Non-zero proves GTL no longer needs the observer for
      // at-draw render states. (The counter existed but was never logged.)
      static_cast<unsigned long long>(
          g_state.stats.self_read_state_dispatches),
      static_cast<unsigned long long>(g_state.stats.state_dispatch_calls),
      static_cast<unsigned long long>(g_state.stats.state_dispatch_bad_args),
      static_cast<unsigned long long>(g_state.stats.state_dispatch_not_render),
      static_cast<unsigned long long>(g_state.stats.state_dispatch_unchanged),
      static_cast<unsigned long long>(g_state.stats.state_dispatch_bad_slot),
      static_cast<unsigned long long>(
          g_state.stats.self_read_sampler_dispatches),
      static_cast<unsigned long long>(g_state.stats.exit_self_read_filled),
      static_cast<unsigned long long>(g_state.stats.exit_feed_load_bearing),
      static_cast<unsigned long long>(g_state.stats.exit_both_absent),
      static_cast<unsigned long long>(g_state.stats.exit_feed_disagreed),
      static_cast<unsigned long long>(
          g_state.stats.exit_stated_unbind_applied));
  // Float-constant divergence census (feed-7 deletion falsifier). Counters are
  // RUN TOTALS like every other stats field here; the line repeats per compose
  // so two samples give a windowed delta. first_diverged is the FIRST
  // divergence of the run: register index and bank (vs/ps), "none" until one
  // is seen. self_read=1 means the ENTER capture preferred the direct device
  // read (renderer_gtl_constant_self_read).
  const bool census_has_divergence =
      g_state.stats.constant_census_first_diverged_register != 0xFFFFFFFFu;
  REXLOG_INFO(
      "Refii GTL stats constant_census[compared={} diverged={} "
      "dwords_diverged={} unfed={} first_diverged={}{}] self_read={}",
      static_cast<unsigned long long>(
          g_state.stats.constant_census_banks_compared),
      static_cast<unsigned long long>(
          g_state.stats.constant_census_banks_diverged),
      static_cast<unsigned long long>(
          g_state.stats.constant_census_dwords_diverged),
      static_cast<unsigned long long>(
          g_state.stats.constant_census_banks_unfed),
      census_has_divergence
          ? (g_state.stats.constant_census_first_diverged_bank == 1u ? "ps_c"
                                                                     : "vs_c")
          : "none",
      census_has_divergence
          ? std::to_string(g_state.stats.constant_census_first_diverged_register)
          : std::string(),
      refii::renderer::RendererGtlConstantSelfReadEnabled() ? 1 : 0);
}

}  // namespace

bool IsLikelyGuestDwordAddressForStack(uint32_t address) {
  return address >= 0x40000000u && address < 0x80000000u;
}

void RegisterHeaderInitLocked(uint32_t symbol_address,
                              const RendererHookArgs &args,
                              uint8_t *guest_base) {
  const refii::renderer::FableHookGuestAddressCallbacks callbacks = {
      IsLikelyGuestDwordAddressForStack};
  uint32_t object = 0;
  Resource resource;
  resource.kind = ResourceKind::kTexture;
  switch (symbol_address) {
    // Arg maps below are pinned from the real prototypes (Lumen-confirmed names +
    // IDA's own stack-arg detection), 2026-07-17. Stack offsets follow the Xbox
    // 360 ABI: param N (0-based) occupies an 8-byte slot at caller_sp+0x10+N*8,
    // and a 32-bit value sits in the HIGH half, i.e. +4. So param N is read at
    // caller_sp + 0x10 + N*8 + 4.
    case kSetTextureHeaderInline:
      // D3D::SetTextureHeader(_D3DRESOURCETYPE type, uint Width, uint Height,
      //   uint Depth, uint Levels, ulong Usage, _D3DFORMAT Format,
      //   MIPPACKINGTYPE, ulong, int, uint, D3DBaseTexture *pTexture /*param 11
      //   -> +0x6C*/, ulong *pBaseSize, ulong *pMipSize)
      object = refii::renderer::FableHookStackU32(guest_base, args, 0x6Cu,
                                                  callbacks);
      resource.width = args.r4;
      resource.height = args.r5;
      resource.levels = args.r7;  // r6 is Depth, NOT Levels
      resource.format = args.r9 & 0x3Fu;
      break;
    case kSetBaseTextureHeader:
      // XGRAPHICS::SetBaseTextureHeader(uint Width, uint Height, uint Depth,
      //   uint Levels, ulong Usage, _D3DFORMAT Format, MIPPACKINGTYPE, int, int,
      //   uint, uint, uint, _D3DRESOURCETYPE, D3DBaseTexture *pTexture /*param
      //   13 -> +0x7C*/, uint *pBaseSize, uint *pMipSize)
      // Param order confirmed by its three callers, which tail into it as
      // (Width, Height, Depth, Levels, Usage, Format, MipPacking, ...).
      object = refii::renderer::FableHookStackU32(guest_base, args, 0x7Cu,
                                                  callbacks);
      resource.width = args.r3;
      resource.height = args.r4;
      resource.levels = args.r6;         // r5 is Depth, NOT Levels
      resource.format = args.r8 & 0x3Fu; // r7 is Usage, NOT Format
      break;
    case kXGSetTextureHeader:
      // XGSetTextureHeader(UINT Width, UINT Height, UINT Levels, DWORD Usage,
      //   D3DFORMAT Format, D3DPOOL Pool, UINT BaseOffset, UINT MipOffset,
      //   UINT Pitch, D3DTexture *pTexture /*param 9 -> +0x5C*/,
      //   UINT *pBaseSize, UINT *pMipSize)
      // BUG FIXED 2026-07-17: this shared kSetBaseTextureHeader's +0x7C, which
      // is PAST THE END of this function's 12 args — it read garbage, so the
      // texture never registered under its real address. That is a prime suspect
      // for resolve destinations coming back 100% unregistered (run 437/438).
      // IDA's stack-arg detection agrees: incoming args land at +0x54 (Pitch),
      // +0x5C (pTexture), +0x64 (pBaseSize), +0x6C (pMipSize).
      object = refii::renderer::FableHookStackU32(guest_base, args, 0x5Cu,
                                                  callbacks);
      resource.width = args.r3;
      resource.height = args.r4;
      resource.levels = args.r5;
      resource.format = args.r7 & 0x3Fu;
      break;
    case kXGSetLineTextureHeader:
      // XGSetLineTextureHeader(UINT Width, UINT Levels, DWORD Usage,
      //   D3DFORMAT Format, D3DPOOL Pool, UINT BaseOffset, UINT MipOffset,
      //   D3DLineTexture *pTexture /*r10*/, UINT *pBaseSize, UINT *pMipSize)
      object = args.r10;
      resource.width = args.r3;
      resource.height = 1u;              // a line texture is 1 tall; r4 is Levels
      resource.levels = args.r4;
      resource.format = args.r6 & 0x3Fu; // r7 is Pool, NOT Format
      break;
    case kXGSetCubeTextureHeader:
      // XGSetCubeTextureHeader(UINT EdgeLength, UINT Levels, DWORD Usage,
      //   D3DFORMAT Format, D3DPOOL Pool, UINT BaseOffset, UINT MipOffset,
      //   D3DCubeTexture *pCubeTexture /*r10*/, UINT *pBaseSize, UINT *pMipSize)
      object = args.r10;
      resource.width = args.r3;
      resource.height = args.r3;         // cube: height == EdgeLength; r4 is Levels
      resource.levels = args.r4;
      resource.format = args.r6 & 0x3Fu; // r7 is Pool, NOT Format
      break;
    default:
      return;
  }
  if (object == 0) {
    // A NULL out-pointer here is NOT a defect — it is the XDK size-query idiom
    // (pTexture=NULL means "return the sizes, do not write a header"), measured
    // as the overwhelming majority (626 in run 443: addr_ok=1, Width=0x100, an
    // ordinary call). Registering nothing is the correct response.
    //
    // The DEFECT shape is different and must be counted apart: the out-pointer
    // could not be READ because the computed stack address was rejected. That is
    // what a wrong offset looks like (the XGSetTextureHeader +0x7C bug), and
    // FableHookStackU32 returns 0 for BOTH cases — so re-check the address here
    // rather than infer from a bare zero. The offsets themselves are verified
    // against the real prototypes (docs/refii_d3d9_root_prototypes.json).
    const uint32_t stack_offset =
        symbol_address == kSetTextureHeaderInline  ? 0x6Cu
        : symbol_address == kSetBaseTextureHeader  ? 0x7Cu
        : symbol_address == kXGSetTextureHeader    ? 0x5Cu
                                                   : 0u;  // line/cube use r10
    const bool readable =
        stack_offset == 0u  // r10 roots need no stack read
            ? true
            : (guest_base != nullptr &&
               IsLikelyGuestDwordAddressForStack(args.r1 + stack_offset));
    if (readable) {
      ++g_state.stats.header_inits_size_query;
    } else {
      ++g_state.stats.header_inits_unreadable_out_pointer;
      static std::unordered_set<uint32_t> logged_unreadable;
      if (logged_unreadable.insert(symbol_address).second) {
        REXLOG_INFO(
            "Refii GTL header-init UNREADABLE out-pointer sub_{:08X} "
            "r1=0x{:08X} off=0x{:02X} probe_addr=0x{:08X} — arg map suspect",
            symbol_address, args.r1, stack_offset, args.r1 + stack_offset);
      }
    }
    return;
  }
  // Capture the header's own fetch-constant block.
  //
  // *** THIS READ IS AT HOOK ENTER, WHERE THE HEADER IS STILL THE OUTGOING
  // TENANT'S. *** The comment here used to claim "These roots have just WRITTEN
  // the header, so object+28 is stated by this very call". That is false:
  // RegisterHeaderInitLocked is dispatched from OnKnownHookEnter, and the real
  // 52-byte header write is the `memcpy` at 0x821FBB64 inside
  // XGRAPHICS_SetBaseTextureHeader — after the body runs. The allocator does not
  // zero recycled blocks and the free list writes no field on push or pop, so on
  // a RECYCLED header these six dwords are the PREVIOUS texture's fetch block,
  // stored under the incoming object's address with fetch_dwords_stated = true.
  // That is the stale-identity engine this whole track has been chasing.
  //
  // Kept as a FALLBACK only — for a fresh (non-recycled) block it is harmless,
  // and it still covers roots whose exit we cannot pair. The authoritative value
  // is re-read at EXIT by CompleteHeaderInitFetchReadLocked below, which
  // overwrites this. Do not delete it without checking that every stashing root
  // actually reaches its exit.
  {
    constexpr uint32_t kTextureFetchOffset = 28u;
    const uint32_t fetch_address = object + kTextureFetchOffset;
    if (guest_base != nullptr &&
        refii::renderer::IsLikelyFableObjectAddress(object) &&
        refii::renderer::bink::IsReadableGuestByteRange(
            guest_base, fetch_address, 6u * 4u)) {
      for (uint32_t field = 0; field < 6u; ++field) {
        resource.fetch_dwords[field] = refii::renderer::bink::LoadGuestU32(
            guest_base, fetch_address + field * 4u);
      }
      resource.fetch_dwords_stated = true;
      ++g_state.stats.resource_fetch_dwords_stated;
    }
  }
  // Re-initializing a header is a legitimate re-creation: overwrite.
  resource.guest_address = object;
  resource.create_symbol = symbol_address;
  resource.create_lr = args.lr;
  resource.create_serial = g_state.next_create_serial++;
  // CARRY THE CONTENT GENERATION ACROSS RE-REGISTRATION, DO NOT RESET IT.
  // Fable's dominant texture path is header init on POOLED, RE-POINTED headers,
  // so this site runs repeatedly for the same guest address. A fresh Resource
  // would drop content_generation back to 0, which (a) makes the counter
  // non-monotonic per guest address and (b) reads downstream as "no content
  // yet" — the same value that gates uploads off. Re-init genuinely changes the
  // content, so the correct signal is an INCREMENT, not a reset: the counter
  // keeps meaning "this resource's bytes changed N times".
  // gpu_content_generation carries for the same reason.
  const auto existing = g_state.resources.find(object);
  if (existing != g_state.resources.end()) {
    resource.content_generation = existing->second.content_generation + 1u;
    resource.gpu_content_generation = existing->second.gpu_content_generation;
    ++g_state.stats.resources_reregistered_generation_carried;
    // The fresh read above is authoritative when it succeeded (this call just
    // rewrote the header). Only fall back to what we already held when the
    // guest range was unreadable, so an unreadable re-init degrades to stale
    // rather than to nothing.
    if (!resource.fetch_dwords_stated && existing->second.fetch_dwords_stated) {
      resource.fetch_dwords = existing->second.fetch_dwords;
      resource.fetch_dwords_stated = true;
    }
    resource.tenancy_generation = existing->second.tenancy_generation;
  }
  g_state.resources[object] = resource;
  ++g_state.stats.resources_registered;
  MarkResourceChangedLocked(object);

  // Arm the authoritative EXIT re-read, and only for the roots whose own exit
  // is genuinely after the header write.
  //
  //  * The XG family (0x821FBB78 / 0x8300BEE0 / 0x8300BF48) and
  //    XGRAPHICS_SetBaseTextureHeader (0x821FB968) all contain the memcpy at
  //    0x821FBB64 within their own bodies, so at THEIR exit the header is
  //    written. These are also exactly the IDA-proven tenancy points.
  //  * kSetTextureHeaderInline (0x821FB650) is deliberately EXCLUDED. Its
  //    destination is a stack scratch in five of six call sites and the real
  //    copy happens in its CALLER after it returns, so its own exit proves
  //    nothing. (It is also why a tenancy bump must not live here: four of its
  //    six callers are per-video-frame GPU-codec transients on recycled stack
  //    addresses.)
  if (symbol_address != kSetTextureHeaderInline) {
    RecordPendingHeaderInitRead(symbol_address, object, guest_base);
  }
}

// Authoritative post-write read of the header's fetch block, plus the tenancy
// bump. Runs at the header root's EXIT, where the 52-byte memcpy has landed.
void CompleteHeaderInitFetchReadLocked(uint32_t symbol_address) {
  const PendingHeaderInitRead pending =
      TakePendingHeaderInitRead(symbol_address);
  if (pending.symbol_address == 0 || pending.object == 0 ||
      pending.guest_base == nullptr) {
    return;
  }
  const auto it = g_state.resources.find(pending.object);
  if (it == g_state.resources.end()) {
    return;
  }
  Resource &resource = it->second;

  // TENANCY. Every non-NULL pTexture write through this family zeroes 52 bytes
  // and resets ReferenceCount to 1, so it is always a new tenant of this
  // address — fresh block, pool-recycled block or live in-place rewrite alike.
  // There is no benign re-describe through here to suppress.
  ++resource.tenancy_generation;
  ++g_state.stats.header_init_tenancy_bumps;

  constexpr uint32_t kTextureFetchOffset = 28u;
  const uint32_t fetch_address = pending.object + kTextureFetchOffset;
  if (!refii::renderer::IsLikelyFableObjectAddress(pending.object) ||
      !refii::renderer::bink::IsReadableGuestByteRange(pending.guest_base,
                                                       fetch_address,
                                                       6u * 4u)) {
    return;
  }
  std::array<uint32_t, 6> fetch = {};
  for (uint32_t field = 0; field < 6u; ++field) {
    fetch[field] = refii::renderer::bink::LoadGuestU32(
        pending.guest_base, fetch_address + field * 4u);
  }
  // Count how often the enter-side value was actually wrong. This is the direct
  // measure of the stale-identity population: a divergence here is a header
  // whose identity we would previously have published as the OUTGOING tenant's
  // fetch block.
  if (resource.fetch_dwords_stated && resource.fetch_dwords != fetch) {
    ++g_state.stats.header_init_fetch_corrected;
  }
  resource.fetch_dwords = fetch;
  resource.fetch_dwords_stated = true;
  ++g_state.stats.resource_fetch_dwords_stated;
}

// Mirrors hook_observer.cpp's IsFableSceneSubmitScope exactly (same 18
// IDA-verified guest addresses -- kept as a second copy rather than a shared
// header because the GTL operates on resolved symbol_address integers while
// the observer checks the raw symbol string; drift risk is low since this
// list has been stable). These wrapper functions bracket real scene-mesh
// submission; nesting depth into them is the ONLY signal (observer or GTL)
// that distinguishes a scene-object draw from a UI/private-rect wrapper draw
// sharing the same D3D draw roots.
bool IsFableGtlSceneSubmitScope(uint32_t address) {
  switch (address) {
    case 0x821C3C60u:
    case 0x8226E738u:
    case 0x8223CFA0u:
    case 0x822A8F68u:
    case 0x821D4F60u:
    case 0x821D9C98u:
    case 0x822975F8u:
    case 0x82297698u:
    case 0x821CB498u:
    case 0x82231ED8u:
    case 0x82192028u:
    case 0x821A4ED0u:
    case 0x8222C2B0u:
    case 0x822749E0u:
    case 0x82A679C0u:
    case 0x82A73DC8u:
    case 0x82B285F8u:
    case 0x82A657C0u:
      return true;
    default:
      return false;
  }
}

// SUPERSET GATE -- deliberately every GTL symbol constant (52), not just the 50
// the switch below has a case for. Being a superset is the safety property: a
// symbol the switch handles can never be skipped here, while the ~300 hooked
// symbols the GTL knows nothing about stop paying for the lock.
//
// Why this exists: OnHookEnter took g_state.mutex UNCONDITIONALLY, so every one
// of the ~350 hooked guest symbols acquired one global, contended mutex on
// every guest call -- to reach a switch that handles 52 of them and has no
// default case. That is the single hottest lock in the process and ~85% of the
// acquisitions did nothing at all.
//
// Generated from the `constexpr uint32_t k* = 0x8...` declarations in this
// file. If you add a GTL symbol constant, add it here too; a missing entry
// silently disables that root's GTL handling.
bool IsGtlKnownSymbolAddress(uint32_t symbol_address) {
  switch (symbol_address) {
    case kCreateTexture:
    case kCreateSurface:
    case kCreateVertexBuffer:
    case kCreateIndexBuffer:
    case kCreateVertexShader:
    case kCreatePixelShader:
    case kFableCreateVertexShader:
    case kFableCreatePixelShader:
    case kCreateVertexDeclaration:
    case kDestructResource:
    case kSetTexture:
    case kSetRenderTarget:
    case kSetDepthStencilSurface:
    case kSetVertexShader:
    case kSetPixelShader:
    case kSetVertexDeclaration:
    case kSetStreamSource:
    case kSetIndices:
    case kSetViewport:
    case kSetScissorRect:
    case kDrawPrimitive:
    case kDrawIndexedPrimitive:
    case kDrawPrimitiveUP:
    case kUnlockTextureRect:
    case kUnlockVertexBuffer:
    case kUnlockIndexBuffer:
    case kUnlockResource:
    case kRelocateResource:
    case kGetSurfaceLevel:
    case kGetCubeMapSurface:
    case kXGCompressSurface:
    case kKickOffSegment:
    case kKickOff:
    case kStreamedLoadCancel:
    case kStreamedResidencyPoll:
    case kTileSurface:
    case kTileSurfaceThunk:
    case kTileTextureLevel:
    case kTileVolume:
    case kTileVolumeTextureLevel:
    case kSetTextureHeaderInline:
    case kSetBaseTextureHeader:
    case kXGSetTextureHeader:
    case kXGSetLineTextureHeader:
    case kXGSetCubeTextureHeader:
    case kOffsetBaseTextureAddress:
    case kOffsetResourceAddress:
    case kPresent:
    case kDisplaySwap:
    case kClearF:
    case kClearRect:
    case kBeginVertices:
    case kBeginIndexedVertices:
    case kResolve:
    case kDrawIndexedUserPrimitives:
    case kEmitTessellatedIndexedPackets:
    case kEmitTessellatedPrimitivePackets:
    case kShaderBindingTable:
      return true;
    default:
      return false;
  }
}

uint32_t ResolveHookSymbolAddress(const char *symbol) {
  return SymbolAddress(symbol);
}

bool IsSceneSubmitScopeAddress(uint32_t symbol_address) {
  return IsFableGtlSceneSubmitScope(symbol_address);
}

bool IsKnownHookSymbolAddress(uint32_t symbol_address) {
  return IsGtlKnownSymbolAddress(symbol_address);
}

// Begin-family pin drain, extracted from OnHookEnter so the scene-scope
// marker short-circuit performs it too: the drain contract is "this thread's
// NEXT hooked D3D call", and a short-circuited scope marker still counts as
// that next call. Cost when idle is one thread_local compare.
static void DrainPendingBeginPin(uint8_t *guest_base);
static void OnKnownHookEnter(const char *symbol, uint32_t symbol_address,
                             const RendererHookArgs &args,
                             uint8_t *guest_base);

void OnHookEnter(const char *symbol, const RendererHookArgs &args,
                 uint8_t *guest_base) {
  OnHookEnter(symbol, SymbolAddress(symbol), args, guest_base);
}

void OnSceneSubmitScopeEnter(uint8_t *guest_base) {
  {
    std::lock_guard<std::mutex> lock(g_state.mutex);
    ++g_state.scene_submit_depth;
  }
  DrainPendingBeginPin(guest_base);
}

void OnSceneSubmitScopeExit() {
  std::lock_guard<std::mutex> lock(g_state.mutex);
  // Saturating, same as OnHookExit.
  if (g_state.scene_submit_depth != 0) {
    --g_state.scene_submit_depth;
  }
}

void OnHookEnterUnknownSymbolFastPath(uint8_t *guest_base) {
  // See the header comment: for a mapped symbol outside the
  // IsGtlKnownSymbolAddress superset (and outside the scene-scope set) the
  // begin-pin drain is the only OnHookEnter effect, and OnHookExit has none.
  DrainPendingBeginPin(guest_base);
}

// Defined below; called from OnHookEnter's blocker-retire path.
void RetireGuestGpuFenceLocked(uint32_t device, uint8_t *guest_base);

void OnHookEnter(const char *symbol, uint32_t symbol_address,
                 const RendererHookArgs &args, uint8_t *guest_base) {
  if (symbol_address == 0) {
    // Unmapped roots used to vanish here with no trace. Both the
    // XGOffsetBaseTextureAddress and rex_lhGfx_CreateGPUState drops hid behind
    // this return for months: the unknown-root census lives inside the switch
    // below, so it is structurally blind to anything SymbolAddress rejects.
    std::lock_guard<std::mutex> lock(g_state.mutex);
    ++g_state.stats.unmapped_symbol_events;
    // Name them. 2395 drops in run 443 and a bare count cannot say WHICH roots
    // — and "which" is the whole question, since two real bugs have already
    // hidden in exactly this set. Log each distinct symbol once.
    static std::unordered_set<std::string> logged_unmapped;
    if (logged_unmapped.size() < 64u && logged_unmapped.insert(symbol).second) {
      REXLOG_INFO("Refii GTL UNMAPPED root (distinct #{}) symbol={}",
                  logged_unmapped.size(), symbol);
    }
    return;
  }

  // *** THE RUN-571 WEDGE FIX (2026-08-04). ***
  //
  // Retiring the GPU write-back ONLY at KickOff exit is a self-deadlock, and
  // run 571 hit it: the guest render thread wedged permanently 2.5 minutes into
  // gameplay and the process had to be killed.
  //
  // The cycle: `D3D_RingBufferDeviceAllocate` needs ring space, does not have
  // it, and calls `D3D_CDevice_BlockOnSecondaryPosition` (0x82206400), which
  // spins on B[+4] of the write-back block at `*(dev+0x2A90)`. On hardware the
  // CP advances that word. Under Refii only `RetireGuestGpuFenceLocked` does —
  // and it runs at KickOff EXIT, which is DOWNSTREAM of the allocation that is
  // now blocked. So the one thing that could release the wait can no longer be
  // reached. Measured: `KickOff n=1011` frozen for the remaining 3m58s, and
  // `guest_fences_retired` frozen with it.
  //
  // The watchdog cannot save us either: `D3D_CBlocker_Check` (0x82BA1FA8)
  // rearms its 5000-tick deadline whenever B[+0] changes and is not wall-clock,
  // so it never fires — which is exactly why the freeze produced no assert, no
  // trap and no error line of any kind.
  //
  // Same object, three waiters — this also covers
  // `D3D_CDevice_BlockOnFence` (0x82242668, polls B[+0]) and the pool
  // allocator's query spin at 0x82B87434, both of which were stuck in run 571.
  //
  // WHY RETIRING HERE IS NOT "EARLY". The existing KickOff-exit note warns that
  // retiring before a segment is genuinely handed over lets the guest overwrite
  // memory we are still reading. That hazard does not apply at THIS point: the
  // guest is blocked waiting for the GPU to consume ring space belonging to
  // segments it has ALREADY kicked off, and our draw-root hooks captured those
  // draws before the kickoff. Declaring them consumed is the same statement the
  // KickOff-exit retirement already makes — just made at the moment the guest
  // asks, instead of at a moment it can no longer reach.
  //
  // args.r3 is the device for this root, but a wrong r3 is HARMLESS:
  // RetireGuestGpuFenceLocked validates every field it touches and bails with a
  // named counter rather than writing anything.
  if (symbol_address == kBlockOnSecondaryPosition ||
      symbol_address == kBlockOnFence) {
    std::lock_guard<std::mutex> lock(g_state.mutex);
    ++g_state.stats.blocker_retire_attempts;
    RetireGuestGpuFenceLocked(args.r3, guest_base);
    return;
  }

  if (IsFableGtlSceneSubmitScope(symbol_address)) {
    std::lock_guard<std::mutex> lock(g_state.mutex);
    ++g_state.scene_submit_depth;
  }

  // Begin-family pin drain: this thread's next D3D call means the caller
  // finished writing the ring payload (the guest contract) — copy it into
  // the stashed packet now. Skipped silently if the packet already drained
  // to the pump (payload then arrives via the observer path as before).
  DrainPendingBeginPin(guest_base);

  if (CreateKindForSymbol(symbol_address) != ResourceKind::kUnknown) {
    // Result object is only known at exit; stash the creation facts.
    t_pending_create = {symbol_address, args.lr, args.r3,     args.r4,
                        args.r5,        args.r6, args.r7,     args.r8,
                        guest_base};
    return;
  }

  // Nothing below this point concerns a symbol the GTL does not know, so do not
  // take the global lock for it. See IsGtlKnownSymbolAddress.
  if (!IsGtlKnownSymbolAddress(symbol_address)) {
    return;
  }
  OnKnownHookEnter(symbol, symbol_address, args, guest_base);
}

static void DrainPendingBeginPin(uint8_t *guest_base) {
  if (t_pending_begin_pin.packet_id != 0 && guest_base != nullptr) {
    const PendingBeginPin pin = t_pending_begin_pin;
    t_pending_begin_pin = {};
    g_begin_pin_census.drain_reached.fetch_add(1, std::memory_order_relaxed);
    if (refii::renderer::bink::IsReadableGuestByteRange(
            guest_base, pin.ring_pointer, pin.byte_size)) {
      // A blank-payload (all-zero) scan-and-skip used to sit here (the
      // delayed-finalize lesson). DELETED (audit): per the IDA-proven scratch
      // commit contract the allocator NEVER zero-fills blocks, and this drain
      // point is by construction AFTER the caller's ring write (the guest
      // contract above: this thread's next D3D call means the payload is
      // complete) — so zeros read here ARE the caller's vertex data, and the
      // skip silently demoted those draws to the observer path with no census
      // trace. The readable-range check above is the only real precondition
      // (unmapped guest memory) and stays.
      const uint8_t* ring_bytes =
          guest_base + pin.ring_pointer +
          refii::renderer::bink::GuestPhysicalHostOffset(pin.ring_pointer);
      std::lock_guard<std::mutex> pin_lock(g_state.mutex);
      bool found = false;
      for (auto packet_it = g_state.packets.rbegin();
           packet_it != g_state.packets.rend(); ++packet_it) {
        if (packet_it->packet_id != pin.packet_id) {
          continue;
        }
        found = true;
        packet_it->inline_vertex_data.resize(pin.byte_size);
        std::memcpy(packet_it->inline_vertex_data.data(), ring_bytes,
                    pin.byte_size);
        packet_it->up_vertex_data = pin.ring_pointer;
        packet_it->up_vertex_count = pin.vertex_count;
        // BeginIndexedVertices' second ring. Raw big-endian guest bytes, the
        // same contract the DrawIndexedUserPrimitives copy uses — the executor
        // owns the swap (DirectSwapIndexBytes / the byte_swap flag in
        // fable_direct_index_data.cpp), and element width rides on the
        // packet's index_format. Zero-sized for kBeginVertices (no index ring).
        if (pin.index_pointer != 0 && pin.index_byte_size != 0 &&
            refii::renderer::bink::IsReadableGuestByteRange(
                guest_base, pin.index_pointer, pin.index_byte_size)) {
          const uint8_t* index_ring_bytes =
              guest_base + pin.index_pointer +
              refii::renderer::bink::GuestPhysicalHostOffset(pin.index_pointer);
          packet_it->inline_index_data.resize(pin.index_byte_size);
          std::memcpy(packet_it->inline_index_data.data(), index_ring_bytes,
                      pin.index_byte_size);
          packet_it->up_index_data = pin.index_pointer;
        }
        g_begin_pin_census.drain_ok.fetch_add(1, std::memory_order_relaxed);
        static uint32_t logged_begin_pins = 0;
        ++logged_begin_pins;
        if (logged_begin_pins <= 8u || (logged_begin_pins % 1024u) == 0u) {
          REXLOG_INFO(
              "Refii GTL begin-family geometry pinned #{} packet_id={} "
              "ring=0x{:08X} bytes={}",
              logged_begin_pins,
              static_cast<unsigned long long>(pin.packet_id),
              pin.ring_pointer, pin.byte_size);
        }
        break;
      }
      if (!found) {
        g_begin_pin_census.drain_packet_gone.fetch_add(
            1, std::memory_order_relaxed);
      }
    } else {
      g_begin_pin_census.drain_unreadable.fetch_add(1,
                                                    std::memory_order_relaxed);
    }
  }
}

// Tail of OnHookEnter for GTL-known symbols; split out so the address-resolved
// entry point above stays readable. Behaviour is byte-identical to the
// pre-split single function.
static void OnKnownHookEnter(const char *symbol, uint32_t symbol_address,
                             const RendererHookArgs &args,
                             uint8_t *guest_base) {
  std::lock_guard<std::mutex> lock(g_state.mutex);
  const uint64_t total_events =
      g_state.stats.state_events + g_state.stats.draw_packets +
      g_state.stats.resources_registered;
  if (total_events != 0 && total_events % 8192u == 0u) {
    REXLOG_INFO(
        "Refii GTL stats frame={} resources={} (created={} regen_carried={} "
        "fetch_stated={} destroyed={}) "
        "state_events={} draw_packets={} clear_packets={} resolve_packets={} "
        "content_packets={} content_stored={} content_unreadable={} "
        "content_store_mb={} "
        "streamed[done={} events={} unreg={}] "
        "tile_content[packets={} unmatched={} bad_args={} size_unknown={} "
        "unreadable={} off_skip={} outer_skip={} lvl_skip={} "
        "enters[out={} in={}]] "
        "unmatched={} pending_packets={} | vs_from_state={} "
        "vs_backfill_tried={} vs_backfill_filled={} vs_absent={}",
        static_cast<unsigned long long>(g_state.frame_id),
        g_state.resources.size(),
        static_cast<unsigned long long>(g_state.stats.resources_registered),
        static_cast<unsigned long long>(
            g_state.stats.resources_reregistered_generation_carried),
        static_cast<unsigned long long>(
            g_state.stats.resource_fetch_dwords_stated),
        static_cast<unsigned long long>(g_state.stats.resources_destroyed),
        static_cast<unsigned long long>(g_state.stats.state_events),
        static_cast<unsigned long long>(g_state.stats.draw_packets),
        static_cast<unsigned long long>(g_state.stats.clear_packets),
        static_cast<unsigned long long>(g_state.stats.resolve_packets),
        static_cast<unsigned long long>(g_state.stats.content_packets),
        static_cast<unsigned long long>(g_state.stats.content_bytes_stored),
        static_cast<unsigned long long>(g_state.stats.content_unreadable),
        static_cast<unsigned long long>(g_state.content_image_bytes >> 20),
        static_cast<unsigned long long>(g_state.stats.streamed_completions),
        static_cast<unsigned long long>(g_state.stats.streamed_content_events),
        static_cast<unsigned long long>(g_state.stats.streamed_unregistered),
        static_cast<unsigned long long>(g_state.stats.tile_content_packets),
        static_cast<unsigned long long>(g_state.stats.tile_content_unmatched),
        static_cast<unsigned long long>(g_state.stats.tile_content_bad_args),
        static_cast<unsigned long long>(
            g_state.stats.tile_content_size_unknown),
        static_cast<unsigned long long>(g_state.stats.tile_content_unreadable),
        static_cast<unsigned long long>(
            g_state.stats.tile_content_offset_skipped),
        static_cast<unsigned long long>(
            g_state.stats.tile_content_outer_skipped),
        static_cast<unsigned long long>(
            g_state.stats.tile_content_level_skipped),
        static_cast<unsigned long long>(g_state.stats.tile_outer_enters),
        static_cast<unsigned long long>(g_state.stats.tile_inner_enters),
        static_cast<unsigned long long>(g_state.stats.unmatched_set_events),
        g_state.packets.size(),
        static_cast<unsigned long long>(g_state.stats.shader_from_device_state),
        static_cast<unsigned long long>(
            g_state.stats.shader_backfill_attempted),
        static_cast<unsigned long long>(g_state.stats.shader_backfill_filled),
        static_cast<unsigned long long>(g_state.stats.shader_still_absent));
    // The fetch_ingest[...] half of this line was DELETED 2026-08-04. Its eight
    // counters had no increment site anywhere in the tree — IngestBoundTexture
    // Content was removed 2026-07-26 and the stats were "retained deliberately
    // as gap-attribution diagnostics". A field that is structurally incapable
    // of being non-zero attributes nothing; it printed a constant
    // `stored=0 unique=0 unreg=0 unchanged=0 declined[0 0 0 0]` twice per stats
    // dump and read as a measurement. base_alias is LIVE (CopyResourceContent's
    // physical-base fallback) and stays.
    REXLOG_INFO("Refii GTL stats base_alias[hit={} miss={}]",
        static_cast<unsigned long long>(g_state.stats.base_alias_content_hits),
        static_cast<unsigned long long>(g_state.stats.base_alias_content_misses));
    // STATED BUFFER PAYLOAD census. `served` is the number that says the
    // vertex/index feed is alive; `no_header` counts buffers whose header
    // states no allocation yet (expected before the first fill, a defect if it
    // stays high); `reloc` says whether the compactor is actually moving
    // payloads under us.
    REXLOG_INFO("Refii GTL stats buffers[binds={} adopted={}] "
                "payload[served={} no_header={} unreadable={} oversize={}] "
                "reloc[seen={} matched={}]",
        static_cast<unsigned long long>(g_state.stats.buffer_binds_stated),
        static_cast<unsigned long long>(g_state.stats.buffers_adopted_at_bind),
        static_cast<unsigned long long>(g_state.stats.buffer_content_served),
        static_cast<unsigned long long>(g_state.stats.buffer_content_no_header),
        static_cast<unsigned long long>(
            g_state.stats.buffer_content_unreadable),
        static_cast<unsigned long long>(g_state.stats.buffer_content_oversize),
        static_cast<unsigned long long>(g_state.stats.buffer_relocations),
        static_cast<unsigned long long>(
            g_state.stats.buffer_relocations_matched));
    LogSelfReadExitCensusLocked();
  }
  switch (symbol_address) {
    case kSetTextureHeaderInline:
    case kSetBaseTextureHeader:
    case kXGSetTextureHeader:
    case kXGSetLineTextureHeader:
    case kXGSetCubeTextureHeader:
      RegisterHeaderInitLocked(symbol_address, args, guest_base);
      return;
    case kOffsetResourceAddress: {
      // XGOffsetResourceAddress(pResource r3, pBaseAddress r4) — the Levels == 1
      // sibling of the root below. ADOPT ONLY, FOR NOW.
      //
      // Adoption is the whole point here: these objects are built by
      // Texture_InitHeaderWrapper (0x821FBD90) over the render-target
      // scratchpad with NO CreateTexture call anywhere on the path, so this is
      // the first and only moment the GTL can learn they exist. Two features
      // depend on it — the half-res screen grab g_PostProcessResolveTexture
      // (0x8331AB30) that the final composite blends back, and the tree
      // impostor targets from sub_82268C70.
      //
      // The BASE is deliberately left 0 = "unknown" until 0x8225FD18 is
      // decompiled. Its sibling takes r4 as an ADDITIVE REBASE DELTA, not an
      // address, and storing the raw delta there measured unique=0/60 in run
      // 457 because the number matched nothing. The same guess here would be
      // the same bug. This file's own rule applies: "a zero says 'unknown', the
      // delta lies with a plausible-looking number." Fill it in once the
      // arithmetic is proven, not before.
      ++g_state.stats.offset_base_address_events;
      {
        static std::unordered_set<uint32_t> seen_offset_resource_objects;
        if (seen_offset_resource_objects.size() < 64u &&
            seen_offset_resource_objects.insert(args.r3).second) {
          REXLOG_INFO(
              "Refii GTL offset-RESOURCE probe (distinct #{}) obj=0x{:08X} "
              "base_arg=0x{:08X} known={} — Levels==1 scratchpad target, "
              "adopted; base NOT recorded until 0x8225FD18 is decompiled",
              seen_offset_resource_objects.size(), args.r3, args.r4,
              g_state.resources.count(args.r3) ? 1 : 0);
        }
      }
      if (args.r3 == 0) {
        return;
      }
      auto res_it = g_state.resources.find(args.r3);
      if (res_it == g_state.resources.end()) {
        Resource resource;
        resource.kind = ResourceKind::kTexture;
        resource.guest_address = args.r3;
        resource.create_symbol = kOffsetResourceAddress;
        resource.create_lr = args.lr;
        resource.create_serial = g_state.next_create_serial++;
        res_it = g_state.resources.emplace(args.r3, resource).first;
        ++g_state.stats.resources_registered;
        ++g_state.stats.resources_adopted_at_offset;
      }
      ++res_it->second.address_generation;
      MarkResourceChangedLocked(res_it->first);
      ++g_state.stats.state_events;
      return;
    }
    case kOffsetBaseTextureAddress: {
      // XGOffsetBaseTextureAddress(pTexture r3, pBaseAddress r4, pMipAddress r5)
      // re-points an existing header at new memory. Record the stated address;
      // derive nothing.
      //
      // This DOES create a registry entry when the object is unknown, which is
      // a deliberate widening of "registry is written only by Create roots".
      // Justification: this call STATES that an object names some memory, which
      // is exactly the identity a Create root would have supplied. Fable's
      // header objects are pooled and re-pointed, and many are never born
      // through a hooked factory at all — run 439 measured 544/544 resolve
      // destinations unregistered while this root was being dropped. Refusing
      // to register here would keep the destination nameless on principle while
      // the wrapper is plainly telling us what it is. No inference is involved:
      // object and address both come straight from the args.
      ++g_state.stats.offset_base_address_events;
      // Log each DISTINCT object once, not the first N events. The first
      // version capped at 24 and only ever showed the earliest objects
      // (0x401375D0-0x40137690), which said nothing about whether the resolve
      // destinations are touched — the exact question being asked.
      {
        static std::unordered_set<uint32_t> seen_offset_objects;
        if (seen_offset_objects.size() < 64u &&
            seen_offset_objects.insert(args.r3).second) {
          REXLOG_INFO(
              "Refii GTL offset-base probe (distinct #{}) tex=0x{:08X} "
              "base=0x{:08X} mip=0x{:08X} known={}",
              seen_offset_objects.size(), args.r3, args.r4, args.r5,
              g_state.resources.count(args.r3) ? 1 : 0);
        }
      }
      if (args.r3 == 0) {
        return;
      }
      auto it = g_state.resources.find(args.r3);
      if (it == g_state.resources.end()) {
        Resource resource;
        resource.kind = ResourceKind::kTexture;
        resource.guest_address = args.r3;
        // ADOPTED, not created: this root only NAMES memory for an object
        // whose creation was never observed. Marking it distinguishes "the GTL
        // knows this resource" from "the GTL saw it being made".
        resource.create_symbol = kOffsetBaseTextureAddress;
        resource.create_lr = args.lr;
        resource.create_serial = g_state.next_create_serial++;
        it = g_state.resources.emplace(args.r3, resource).first;
        ++g_state.stats.resources_registered;
        ++g_state.stats.resources_adopted_at_offset;
      }
      // ADDITIVE, not absolute (IDA 0x8225FDF0, decompiled 2026-07-17): the
      // XDK writes Format.dword[1] = (r4 + (old & 0xFFFFF000)) & 0xFFFFF000 —
      // r4 is a REBASE DELTA added to the BaseOffset XGSetTextureHeader baked
      // into the header at creation. Storing raw r4 here recorded that delta
      // as if it were the base, which is why a base-keyed lookup measured
      // unique=0/60 (run 457): the number matched nothing because it was not
      // an address. Mirror the XDK's own arithmetic instead. At hook ENTER the
      // header still holds the pre-call value, so old + delta = the post-call
      // base — the same dword D3DDevice_Resolve masks into RB_COPY_DEST_BASE.
      // The mip dword only updates when the header has >1 mip level
      // (dword[4]>>6 & 0xF, +1), so gate identically or we would record a
      // write the XDK never performs.
      {
        constexpr uint32_t kFetchDword1Offset = 32u;  // Format.dword[1]
        constexpr uint32_t kFetchDword4Offset = 44u;  // mip-count field
        constexpr uint32_t kFetchDword5Offset = 48u;  // Format.dword[5]
        uint32_t new_base = 0;
        uint32_t new_mip = 0;
        if (guest_base != nullptr &&
            refii::renderer::bink::IsReadableGuestByteRange(
                guest_base, args.r3 + kFetchDword1Offset, 20u)) {
          const uint32_t old_dword1 = refii::renderer::bink::LoadGuestU32(
              guest_base, args.r3 + kFetchDword1Offset);
          // THE LOW 12 BITS ARE PRESERVED, NOT DISCARDED (corrected 2026-08-04
          // from the disassembly of 0x8225FDF0):
          //   /*0x8225fe10*/ clrrwi r11, r10, 12    ; old & 0xFFFFF000
          //   /*0x8225fe14*/ add    r11, r11, r4    ; + delta
          //   /*0x8225fe18*/ insrwi r11, r10, 12,20 ; bits 20..31 <- OLD low 12
          // `insrwi rA,rS,12,20` == `rlwimi rA,rS,0,20,31` — it INSERTS the
          // original low 12 bits back over the sum. Those bits are the
          // format/endianness/request-size/stacked/clamp-policy field sharing
          // this dword with the 20-bit base, so zeroing them corrupts the fetch
          // constant. The previous expression masked them away; the two agree
          // only when `old & 0xFFF == 0`, which is not the case for a header
          // XGSetTextureHeader has populated.
          //
          // D3DDevice_Resolve masks with 0xFFFFF000 for RB_COPY_DEST_BASE, so
          // the BASE we derive was already right; what was wrong is the header
          // dword itself, and anything reading format or endianness from it.
          new_base = (((old_dword1 & 0xFFFFF000u) + args.r4) & 0xFFFFF000u) |
                     (old_dword1 & 0x00000FFFu);
          const uint32_t dword4 = refii::renderer::bink::LoadGuestU32(
              guest_base, args.r3 + kFetchDword4Offset);
          const uint32_t mip_levels = ((dword4 >> 6u) & 0xFu) + 1u;
          if (mip_levels > 1u) {
            const uint32_t old_dword5 = refii::renderer::bink::LoadGuestU32(
                guest_base, args.r3 + kFetchDword5Offset);
            // Same insrwi shape as the base above /*0x8225fe38*/.
            new_mip = (((old_dword5 & 0xFFFFF000u) + args.r5) & 0xFFFFF000u) |
                      (old_dword5 & 0x00000FFFu);
          }
        }
        // Unreadable header => store nothing rather than the raw delta: a zero
        // says "unknown", the delta lies with a plausible-looking number.
        it->second.base_address = new_base;
        it->second.mip_address = new_mip;
      }
      ++it->second.address_generation;
      // Same object, DIFFERENT memory — the snapshot's base_address changed, so
      // the consumer must re-sync it.
      MarkResourceChangedLocked(it->first);
      ++g_state.stats.state_events;
      return;
    }
    case kRelocateResource: {
      // Physical-pool compaction moved a buffer's payload. See the constant's
      // definition for the whole four-instruction body; the two facts that
      // matter here are that r3 is the WRAPPER (the D3D header the bind roots
      // name is r3 + 8) and that the header's stated address is rewritten by
      // the XGOffsetResourceAddress this function tail-calls.
      //
      // Handled at ENTER. The registry stores nothing that the exit would
      // report more accurately: the address is re-read live at every bind and
      // at every snapshot, so the only job here is to say "the bytes you have
      // are at the old place" -- and a consumer that acts on that between our
      // enter and the guest's store simply re-reads a header that has not
      // moved yet and gets marked again at the next bind.
      ++g_state.stats.buffer_relocations;
      const uint32_t header = args.r3 + 8u;
      auto reloc_it = g_state.resources.find(header);
      if (reloc_it != g_state.resources.end()) {
        ++g_state.stats.buffer_relocations_matched;
        // INVALIDATE, do not repair. base_address is zeroed rather than set to
        // the new address because the new address is not stated HERE -- r5 is
        // the wrapper's own base, not the header's Format.dword[0], and the
        // XDK call that derives one from the other has not run yet. Zero means
        // "ask the header", which is exactly what the snapshot feed does.
        reloc_it->second.base_address = 0;
        ++reloc_it->second.address_generation;
        // The payload is the same bytes at a new place, so the consumer's
        // upload is stale as an ADDRESS even though the content is unchanged.
        // Bumping the content generation is what makes it re-read.
        ++reloc_it->second.content_generation;
        MarkResourceChangedLocked(header);
      }
      ++g_state.stats.state_events;
      return;
    }
    case kDestructResource:
      // D3DResource_Release(D3DResource *pThis) -> ULONG (the NEW refcount).
      //
      // BUG FIXED 2026-07-17: this used to erase unconditionally, on EVERY
      // call. Release is a refcount DECREMENT, not a destructor — it only
      // destroys (sub_821F65C0) when the count reaches 0, and otherwise returns
      // the surviving count. So any ordinary AddRef/Release pair evicted a live
      // resource from the registry. Fable's surface-wrapper code does exactly
      // that around resolve destinations (see Fable__SurfaceWrapper_SetSurface
      // 0x82B81D30, which Releases the old *(wrapper+4), and 0x82B3B2A0 which
      // AddRefs/Releases per iteration).
      //
      // Measured (run 442): the SAME object was known=1 at the
      // XGOffsetBaseTextureAddress probe and a MISS at the resolve moments
      // later — 0x40136C90 and 0x40513C10 both. registry_size had collapsed to
      // 60 against created=3438. This is why unregistered_dest sat at 100% for
      // five runs while every "missing create root" theory came up empty: the
      // creates were landing, then being thrown away.
      //
      // Defer to the exit so the real refcount decides. The object address is
      // stashed here because r3 is the RETURN value at exit, not pThis.
      t_pending_release_object = args.r3;
      return;
    case kGetSurfaceLevel:
      // 2D/array level view: no face, level in r4.
      t_pending_view_identity = {args.r3, 0u, args.r4};
      return;
    case kGetCubeMapSurface:
      // Cube face view: FaceType in r4, level in r5 — verbatim.
      t_pending_view_identity = {args.r3, args.r4, args.r5};
      return;
    case kSetTexture:
      // WHY THIS IS LOGGED (2026-08-04): `stated_tex_kept` measured 0 across
      // runs 562 and 563 even after the Begin-family clear was removed
      // entirely, which means `packet.textures` is empty for EVERY slot at
      // BuildDrawPacketLocked time — yet state_events runs to ~233k, so this
      // root does fire. Either the sampler we record is not the sampler the
      // guest passed, or the binds we care about never reach here. Print the
      // first binds and count sampler 0 specifically; that separates the two
      // without another guess.
      if (args.r4 < kMaxTextureSlots) {
        g_state.device.textures[args.r4] = args.r5;
        ++g_state.stats.state_events;
        if (args.r5 != 0) {
          ++g_state.stats.set_texture_nonzero_binds;
          if (args.r4 == 0) {
            ++g_state.stats.set_texture_slot0_binds;
          }
        }
        static uint32_t logged_set_texture = 0;
        if (logged_set_texture < 24u) {
          ++logged_set_texture;
          REXLOG_INFO("Refii GTL SetTexture #{} sampler={} pTexture=0x{:08X} "
                      "r3=0x{:08X} r6=0x{:08X}",
                      logged_set_texture, args.r4, args.r5, args.r3, args.r6);
        }
      } else {
        ++g_state.stats.set_texture_slot_out_of_range;
      }
      return;
    case kSetRenderTarget:
      if (args.r4 < kMaxRenderTargets) {
        g_state.device.render_targets[args.r4] = args.r5;
        if (auto rt_it = g_state.resources.find(args.r5);
            rt_it != g_state.resources.end()) {
          rt_it->second.was_render_target = true;
        }
        ++g_state.stats.state_events;
      }
      return;
    case kSetDepthStencilSurface:
      g_state.device.depth_target = args.r4;
      // STATED depth classification: this root names the depth-stencil object
      // outright, so the snapshot feed can report kDepthSurface without
      // inferring depth from a format field.
      if (auto ds_it = g_state.resources.find(args.r4);
          ds_it != g_state.resources.end() &&
          !ds_it->second.was_depth_target) {
        ds_it->second.was_depth_target = true;
        MarkResourceChangedLocked(args.r4);
      }
      ++g_state.stats.state_events;
      return;
    case kSetVertexShader:
      g_state.device.vertex_shader = args.r4;
      ++g_state.stats.state_events;
      return;
    case kSetPixelShader:
      g_state.device.pixel_shader = args.r4;
      ++g_state.stats.state_events;
      return;
    case kSetVertexDeclaration:
      g_state.device.vertex_declaration = args.r4;
      ++g_state.stats.state_events;
      return;
    case kSetStreamSource:
      if (args.r4 < kMaxStreamSlots) {
        g_state.device.vertex_buffers[args.r4] = args.r5;
        g_state.device.vertex_offsets[args.r4] = args.r6;
        g_state.device.vertex_strides[args.r4] = args.r7;
        // Resolve the buffer object's data pointer (object+0x18 raw GPU
        // base + offset, decoded) — the representation the command
        // pipeline consumes for stream bindings.
        uint32_t data_pointer = 0;
        if (guest_base != nullptr && IsLikelyGuestObjectAddress(args.r5)) {
          const uint32_t raw_gpu_base =
              refii::renderer::bink::LoadGuestU32(guest_base,
                                                  args.r5 + 0x18u);
          if (raw_gpu_base != 0) {
            data_pointer = refii::renderer::DecodeFableGpuBufferPointer(
                raw_gpu_base + args.r6);
          }
        }
        g_state.device.vertex_buffer_pointers[args.r4] = data_pointer;
        // REGISTRY side of the same bind. The device mirror above only feeds
        // the draw packet; the RESOURCE the consumer materializes is looked up
        // by this same address, and until now nothing ever told the registry
        // where a buffer's bytes are or how big it is (the create root's
        // byte_size/stride were inferred from an unpinned wrapper prototype and
        // are now left at zero -- see RegisterCreateLocked). Stride comes from
        // the ARGUMENT, not from the fetch constant this call goes on to write.
        RefreshStatedBufferBindLocked(args.r5, ResourceKind::kVertexBuffer,
                                      guest_base, args.r7);
        ++g_state.stats.state_events;
      }
      return;
    case kSetIndices: {
      g_state.device.index_buffer = args.r4;
      // Resolve the buffer object's data pointer (object+0x18, decoded) —
      // the representation the command pipeline consumes, mirroring the
      // SetStreamSource resolution (indices carry no offset).
      uint32_t data_pointer = 0;
      if (guest_base != nullptr && IsLikelyGuestObjectAddress(args.r4)) {
        const uint32_t raw_gpu_base = refii::renderer::bink::LoadGuestU32(
            guest_base, args.r4 + 0x18u);
        if (raw_gpu_base != 0) {
          data_pointer =
              refii::renderer::DecodeFableGpuBufferPointer(raw_gpu_base);
        }
      }
      g_state.device.index_buffer_pointer = data_pointer;
      // SetIndices itself reads NOTHING off r4 -- IDA 0x8219CC30 stores it to
      // dev+0x3094 and stops; the address/size/format decode happens at the
      // draw (0x8221E200 reads 0x18(r24) and 0(r24)). We read the same header
      // here because this is the moment the guest names the object, and the
      // header it names is complete by construction (XGSetIndexBufferHeader ran
      // at create). The index ELEMENT SIZE comes from Common>>29, which is
      // stated in that header, not from any bind argument.
      RefreshStatedBufferBindLocked(args.r4, ResourceKind::kIndexBuffer,
                                    guest_base, /*stated_stride=*/0);
      ++g_state.stats.state_events;
      return;
    }
    case kSetViewport:
      // D3DVIEWPORT9 by pointer (r4), pinned in IDA (sub_821F9F58): X, Y,
      // Width, Height as unsigned dwords, MinZ/MaxZ as floats. The struct
      // is complete at the call — decode here, the guest may reuse the
      // stack slot immediately after.
      if (guest_base != nullptr &&
          refii::renderer::bink::IsReadableGuestByteRange(guest_base, args.r4,
                                                          24u)) {
        for (uint32_t field = 0; field < 6u; ++field) {
          g_state.device.viewport[field] = refii::renderer::bink::LoadGuestU32(
              guest_base, args.r4 + field * 4u);
        }
        // IS 1433x897 SOMETHING THE GUEST STATED? (2026-08-04)
        //
        // command.viewport is built verbatim from these six dwords
        // (fable_direct_renderer.cpp ~54530), so a fabricated viewport can only
        // come from here. Run 566 produced private-rect extents of 1433x737 and
        // 1433x897 — one word read twice 0x500 apart — against targets whose
        // real extent is 280x180. Either the guest really states that (absurd
        // for a 280x180 target, and D3D9 forbids a viewport outside its render
        // target), or `args.r4` is not pointing at a D3DVIEWPORT9 and we are
        // decoding unrelated memory as one.
        //
        // Print the pointer and the raw dwords whenever the stated extent is
        // one we know to be impossible for this game's surfaces, plus the first
        // few unconditionally as a baseline. The pointer value is the tell: a
        // stack address is a real viewport struct, a texture-header address is
        // not.
        const uint32_t stated_w = g_state.device.viewport[2];
        const uint32_t stated_h = g_state.device.viewport[3];
        static uint32_t logged_viewport_sets = 0;
        const bool implausible = stated_w > 4096u || stated_h > 4096u;
        if (logged_viewport_sets < 32u && (implausible || logged_viewport_sets < 6u)) {
          ++logged_viewport_sets;
          REXLOG_INFO(
              "Refii GTL SetViewport #{}{} pViewport=0x{:08X} dwords="
              "[0x{:08X} 0x{:08X} 0x{:08X} 0x{:08X} 0x{:08X} 0x{:08X}] "
              "=> x={} y={} {}x{}",
              logged_viewport_sets, implausible ? " IMPLAUSIBLE" : "", args.r4,
              g_state.device.viewport[0], g_state.device.viewport[1],
              g_state.device.viewport[2], g_state.device.viewport[3],
              g_state.device.viewport[4], g_state.device.viewport[5],
              g_state.device.viewport[0], g_state.device.viewport[1], stated_w,
              stated_h);
          // *** THE QUESTION THIS ANSWERS (IDA, 2026-08-04). ***
          //
          // The oversized viewport is GENUINELY STATED BY THE GUEST — this is
          // not a misread on our side. Proven chain:
          //   Render_BindSingleRenderTarget 0x821FC3A0
          //     -> sub_821FA158            (reuses a D3DSURFACE_DESC as a RECT)
          //     -> D3DSurface_GetDesc 0x821FA070
          //          Width  = (surf[0x24] >> 18) + 1
          //          Height = ((surf[0x24] >> 3) & 0x7FFF) + 1
          //     -> sub_821F9FD8            (MinZ=1.0 / MaxZ=0.0 HARDCODED here
          //                                 — that is our 0x3F800000 / 0)
          //     -> D3DDevice_SetViewport 0x821F9F58
          // So the game sets the viewport to the full extent of each render
          // target AS IT BINDS IT, from its own decode of that target's packed
          // word. The defect is one level upstream: the word at surface+0x24.
          //
          // REPLICATING THE GUEST'S CLAMP WOULD NOT HELP, and that is worth
          // stating because it is the obvious wrong fix. D3D_SetViewport
          // 0x821F9D00 clamps against `(dev+0x3098)->[0x24]` — the SAME word,
          // decoded the SAME way. With X=0 and Width == maxW == 4313 the clamp
          // is arithmetically a no-op, so the guest's own clamped copy at
          // dev+0x3170/0x3174 holds 4313x6113 too.
          //
          // Inverting the encode (D3D_SetSurfaceHeader 0x821F51B0:
          // `(8*(h-1)) & 0x3FFF8 | ((w-1) << 18) | flags`) says surf[0x24]
          // must have been 0x4360BF00 for 4313x6113 and 0x21A0DF00 for
          // 2153x7137. BOTH have low byte 0x00 and are 256-byte aligned, and
          // both imply height % 32 == 1 twice running. That is the signature
          // of an ALIGNED POINTER sitting where an extent should be — i.e.
          // dword_834A608C was not a live D3DSurface at that moment.
          //
          // That last step is INFERENCE, not proof — it is runtime state. So
          // dump the object and let the next run settle it. A live surface is
          // 0x30 bytes with Common = 4 | 0x100000 | 0x80000000
          // (D3DDevice_CreateSurface 0x821F4EC0 @0x821F4F2C/0x821F4FD0); if
          // Common is not that shape, the object is dead or recycled and the
          // fix belongs at whoever leaves a stale pointer in the slot.
          if (implausible) {
            constexpr uint32_t kBoundColourTargetSlot0 = 0x834A608Cu;
            const uint32_t surface =
                refii::renderer::bink::LoadGuestU32(guest_base,
                                                    kBoundColourTargetSlot0);
            if (surface != 0 && refii::renderer::bink::IsReadableGuestByteRange(
                                    guest_base, surface, 0x30u)) {
              const uint32_t common =
                  refii::renderer::bink::LoadGuestU32(guest_base, surface);
              REXLOG_INFO(
                  "Refii GTL SetViewport IMPLAUSIBLE source dword_834A608C="
                  "0x{:08X} common=0x{:08X} (live surface iff 0x80100004 bits) "
                  "parent=0x{:08X} rt_info=0x{:08X} packed_extent=0x{:08X} "
                  "d3dfmt=0x{:08X} size=0x{:08X}",
                  surface, common,
                  refii::renderer::bink::LoadGuestU32(guest_base,
                                                     surface + 0x18u),
                  refii::renderer::bink::LoadGuestU32(guest_base,
                                                     surface + 0x1Cu),
                  refii::renderer::bink::LoadGuestU32(guest_base,
                                                     surface + 0x24u),
                  refii::renderer::bink::LoadGuestU32(guest_base,
                                                     surface + 0x28u),
                  refii::renderer::bink::LoadGuestU32(guest_base,
                                                     surface + 0x2Cu));
            } else {
              REXLOG_INFO(
                  "Refii GTL SetViewport IMPLAUSIBLE source dword_834A608C="
                  "0x{:08X} — UNREADABLE or null, so the bound colour target "
                  "slot itself is stale",
                  surface);
            }
          }
        }
      } else {
        g_state.device.viewport = {};
      }
      ++g_state.stats.state_events;
      return;
    case kSetScissorRect:
      // RECT by pointer (r4), pinned in IDA (sub_821F9C08): left, top,
      // right, bottom as int dwords.
      if (guest_base != nullptr &&
          refii::renderer::bink::IsReadableGuestByteRange(guest_base, args.r4,
                                                          16u)) {
        for (uint32_t field = 0; field < 4u; ++field) {
          g_state.device.scissor[field] = refii::renderer::bink::LoadGuestU32(
              guest_base, args.r4 + field * 4u);
        }
      } else {
        g_state.device.scissor = {};
      }
      ++g_state.stats.state_events;
      return;
    case kKickOffSegment:
    case kKickOff:
      // `this` (pDevice) is r3 at ENTER; at EXIT r3 is the return value.
      t_pending_kickoff_device = args.r3;
      t_pending_kickoff_base = guest_base;
      return;
    case kStreamedResidencyPoll:
      // Stash only. Whether anything actually streamed in is stated by the
      // return value, which only exists at exit. The cancel root is no longer
      // stashed at all -- it produces no content event, so carrying its request
      // to exit would only risk it being consumed by mistake.
      t_pending_stream_request = args.r3;
      t_pending_stream_base = guest_base;
      return;
    case kTileSurface:
    case kTileSurfaceThunk:
    case kTileTextureLevel:
    case kTileVolume:
    case kTileVolumeTextureLevel: {
      // Arg maps are IDA-PROVEN from the decompiled prototypes (2026-07-23),
      // not inferred from runtime and not copied from the observer:
      //
      //   XGTileTextureLevel(0x8300CB48) __stdcall
      //     Width r3, Height r4, Level r5, GpuFormat r6, Flags r7,
      //     pDestination r8, pPoint r9, pSource r10, RowPitch sp+84,
      //     pRect sp+92.        Width/Height are TEXELS.
      //
      //   XGRAPHICS_TileSurface(0x8300C290) __fastcall  (and its 4-byte thunk
      //   0x8300CB40)
      //     pDestination r3, WidthInBlocks r4, HeightInBlockRows r5,
      //     pPoint r6, pSource r7, SourceRowPitch r8, pRect r9,
      //     BytesPerBlock r10.  r4/r5 are BLOCKS, already converted.
      //
      // The unit difference is the whole reason to read the prototypes:
      // XGTileTextureLevel converts texels to blocks itself and then calls
      // TileSurface with block counts (`XGRAPHICS_TileSurface(pDestination,
      // v23, v26, pPoint, pSource, v45, &v41, v21)`). Applying a texel->block
      // division to BOTH families would compute a quarter of the real size for
      // TileSurface and store a truncated blob — a bug no log would have shown,
      // because both paths would still "work".
      const bool texture_level_map = symbol_address == kTileTextureLevel;
      const bool surface_map = symbol_address == kTileSurface ||
                               symbol_address == kTileSurfaceThunk;
      if (!texture_level_map && !surface_map) {
        // Volume variants: prototypes not read yet, so their arg map is
        // unproven. Decline rather than assume it matches a sibling.
        ++g_state.stats.tile_content_bad_args;
        return;
      }
      if (texture_level_map) {
        ++g_state.stats.tile_outer_enters;
        // Mark the nesting so the TileSurface call this makes is recognised as
        // the same operation. Cleared at this symbol's exit.
        t_in_tile_texture_level = true;
      } else {
        ++g_state.stats.tile_inner_enters;
      }
      if (surface_map && t_in_tile_texture_level) {
        // Nested inner half of an XGTileTextureLevel — same operation, and its
        // destination is the rebased one. Skip. A TileSurface call arriving
        // with depth == 0 is a genuine direct call and IS processed.
        ++g_state.stats.tile_content_outer_skipped;
        return;
      }
      const uint32_t destination = texture_level_map ? args.r8 : args.r3;
      const uint32_t linear_source = texture_level_map ? args.r10 : args.r7;
      if (guest_base == nullptr || destination == 0 || linear_source == 0 ||
          linear_source == destination) {
        // src == dst would be an in-place tile, where the "linear source" is
        // the destination mid-conversion and copying it would store bytes in
        // neither layout. Not observed (run 159: one reused staging buffer,
        // many destinations), but cheap to refuse.
        //
        // Counted SEPARATELY from unmatched. Run 162 read
        // `unmatched=162 packets=0` and could not say whether the arguments
        // were rejected or the resource lookup failed, because both fed one
        // counter — a diagnostic that cannot distinguish its two failure modes
        // is barely a diagnostic.
        ++g_state.stats.tile_content_bad_args;
        return;
      }
      // Match the tiled destination to a resource by the memory it names.
      // resource.base_address is the physical address XGOffsetBaseTextureAddress
      // stated; the tile destination is the GPU-pointer form of the same
      // memory, so it has to come through the same decode the rest of the
      // renderer uses. This is the one inference-shaped step in the writer,
      // and it is the established decode rather than a new heuristic.
      const uint32_t destination_base =
          refii::renderer::DecodeFableGpuMemoryPointer(destination);
      // Match by RANGE, not equality. Tile destinations are not all allocation
      // starts: run 159 logged 18 distinct destinations of which 3
      // (0xF399B040, 0xF39AB040, 0xF39BB040) decode to base+0x40, so an
      // equality match would have silently dropped them and reported itself as
      // "no content" rather than "missed". The extent used is the resource's
      // OWN stated byte_size, so this stays inside stated facts.
      // ALL matching resources, not the first. Fable aliases headers onto one
      // allocation — run 159 shows 0x442D4088 and 0x442D40BC both naming base
      // 0x13990000 — and `resources` is an unordered_map, so taking the first
      // match would hand the content to an arbitrary one of them. The consumer
      // asks by a specific guest address, so an arbitrary winner is a silent,
      // nondeterministic miss. Store for every alias instead.
      // BOTH sides go through the decode. Run 164 caught this: the destination
      // was decoded (0xF39B7000 -> 0x139B8000) and compared against
      // `base_address` stored RAW (nearest_base=0xF2703000, a GPU-form
      // pointer) — two different address spaces, so the match could never hit.
      // That is what produced `packets=0 unmatched=162` with a provably
      // correct arg map (`bad_args=0`).
      //
      // The registry stores what XGOffsetBaseTextureAddress stated, verbatim
      // and un-normalized; the header says outright that decoding is the
      // consumer's job. This is the consumer.
      //
      // Safe to apply unconditionally: for an already-physical P
      // (< 0x20000000) the +0x1000 term is zero and the mask is identity, so
      // the decode is a no-op and a base stated in either form normalizes to
      // the same number.
      // Size comes from the TILE CALL, not the resource. Run 165 showed the
      // decode fix landing dead on (`delta=0x00000000`) while the match still
      // failed, because the resource it hit reported `byte_size=0`: the
      // registry sets byte_size at exactly ONE Create path, and Fable builds
      // most textures by initialising 52-byte Xenos headers in place, which
      // never states one. Requiring byte_size therefore rejected precisely the
      // textures this writer exists to serve.
      //
      // The tile root states its own geometry, so use that — it is the stated
      // fact, and it describes the LINEAR SOURCE being read, which is what is
      // actually being copied.
      const uint32_t tile_height = texture_level_map ? args.r4 : args.r5;
      const refii::renderer::FableHookGuestAddressCallbacks stack_callbacks = {
          IsLikelyGuestDwordAddressForStack};
      const uint32_t tile_pitch =
          texture_level_map
              ? refii::renderer::FableHookStackU32(guest_base, args, 84u,
                                                   stack_callbacks)
              : args.r8;
      // Source row count. TileSurface is handed block rows already; only
      // XGTileTextureLevel needs the texel->block conversion, and its format
      // is STATED in r6 so the block height needs no resource lookup at all.
      // Source row count, transcribed from the decompile rather than
      // approximated. XGRAPHICS_TileSurface loops `v13 < v51` where v51 is the
      // rect height in BLOCK ROWS and reads the source at
      // `row * SourceRowPitch`, so size = RowPitch * blockRows exactly.
      uint32_t source_rows = 0;
      if (texture_level_map) {
        const rex::graphics::FormatInfo *call_format =
            rex::graphics::FormatInfo::Get(args.r6 & 0x3Fu);
        if (call_format == nullptr || call_format->block_height == 0) {
          ++g_state.stats.tile_content_bad_args;
          return;
        }
        const uint32_t block_height = call_format->block_height;
        const uint32_t flag_bias = args.r7 & 2u;
        const uint32_t level = args.r5;
        if (level != 0u) {
          // A mip level's bytes are NOT the base image, and this writer stores
          // every packet at allocation_offset 0 with mip_range=false. Storing a
          // level here would overwrite the base image with a quarter-size mip
          // while `packets` rose and `real_gap` fell — success by every counter
          // and wrong on screen.
          //
          // Placing it properly needs the level's offset inside the allocation,
          // which the root does not state (it derives it internally via
          // XGRAPHICS_GetMipTailLevelOffsetCoords). Decline and count, so the
          // next run says what this costs; if the count is zero, nothing is
          // lost and the pow2 extent math above is dead code kept for when it
          // is not.
          ++g_state.stats.tile_content_level_skipped;
          return;
        }
        uint32_t texel_extent;
        if (level != 0) {
          // v24 = 1 << (32 - cntlzw(Height - (Flags&2) - 1) + ((Flags&2)!=0))
          //         >> Level, clamped to >= 1.
          // Round (Height - flag_bias) UP to a power of two, double it when
          // flag 2 is set, then shift by the level. A plain `Height >> Level`
          // agrees ONLY for power-of-two heights — which every shape seen so
          // far is (128/512/1024), so this would have sat wrong and invisible.
          if (tile_height <= flag_bias || level >= 32u) {
            texel_extent = 1u;
          } else {
            const uint32_t reduced = tile_height - flag_bias;
            uint32_t pow2 = 1u;
            while (pow2 < reduced && pow2 < 0x40000000u) {
              pow2 <<= 1;
            }
            if (flag_bias != 0u) {
              pow2 <<= 1;
            }
            texel_extent = std::max<uint32_t>(1u, pow2 >> level);
          }
        } else {
          // Level 0: v25 = block_height + Height, rows = (v25 - 1) / blockH.
          texel_extent = tile_height;
        }
        source_rows = (texel_extent + block_height - 1u) / block_height;
      } else {
        // Direct XGRAPHICS_TileSurface. r4/r5 are ALREADY block counts, but
        // they are the loop bound only when pRect (r9) is null: with a rect the
        // decompile uses `v9[3] - v9[1]`, the rect's own block-row span. Using
        // r5 regardless would over-read whenever a sub-rect is tiled.
        source_rows = tile_height;
        const uint32_t rect = args.r9;
        if (rect != 0 &&
            refii::renderer::bink::IsReadableGuestByteRange(guest_base, rect,
                                                            16u)) {
          const uint32_t top =
              refii::renderer::bink::LoadGuestU32(guest_base, rect + 4u);
          const uint32_t bottom =
              refii::renderer::bink::LoadGuestU32(guest_base, rect + 12u);
          if (bottom > top) {
            source_rows = bottom - top;
          }
        }
      }
      if (source_rows == 0) {
        ++g_state.stats.tile_content_bad_args;
        return;
      }
      // Source size = pitch * ceil(height / block_height).
      //
      // PINNED, not guessed. Run 167's SIZE? probe produced the operands and
      // they check exactly against the sizes the observer computes
      // independently: (1024x512, pitch 4096) -> 524288, and (128x128, pitch
      // 512) -> 16384, both matching `pitch * height/4` for fmt=20. So the
      // root's `height` argument is in PIXELS and `pitch` is bytes per BLOCK
      // ROW; the divisor is the format's block height, not a constant 4 — a
      // hardcoded /4 would be wrong for every uncompressed format.
      //
      // The block height comes from the SDK's guest-format table rather than
      // anything invented here. When the format is unknown the size is
      // unknowable, and the writer declines rather than storing a blob of
      // guessed length: the consumer uploads whatever is stored, so a wrong
      // size is worse than no coverage.
      const uint32_t tile_width = texture_level_map ? args.r3 : args.r4;
      if (tile_pitch == 0 || tile_height == 0) {
        ++g_state.stats.tile_content_bad_args;
        return;
      }
      // Sanity bound on the stated size. Now that the prototypes give the real
      // row count this IS the copy size, not an upper bound standing in for
      // one.
      if (static_cast<uint64_t>(tile_pitch) *
              static_cast<uint64_t>(source_rows) >
          (64ull << 20)) {
        ++g_state.stats.tile_content_bad_args;
        return;
      }

      std::vector<uint32_t> targets;
      for (auto &entry : g_state.resources) {
        const Resource &candidate = entry.second;
        if (candidate.base_address == 0) {
          continue;
        }
        const uint32_t candidate_base =
            refii::renderer::DecodeFableGpuMemoryPointer(
                candidate.base_address);
        // Extent for LOCATING only. When the resource states a byte_size, use
        // it. When it does not, match EXACTLY (extent 1) rather than inventing
        // a span: run 167 used pitch*height as a stand-in and produced 234
        // false offset-hits, because a fabricated 64KB span swallows every
        // neighbouring texture and reports them as sub-writes of one resource.
        // An invented extent does not just miss — it manufactures wrong
        // matches, which is the worse failure.
        const uint32_t extent =
            candidate.byte_size != 0 ? candidate.byte_size : 1u;
        if (destination_base >= candidate_base &&
            destination_base < candidate_base + extent) {
          targets.push_back(entry.first);
        }
      }
      if (targets.empty()) {
        ++g_state.stats.tile_content_unmatched;
        // Name the miss. run 162 proved the match fails 100% of the time and
        // an aggregate count cannot say why: wrong dst argument, absent
        // base_address, or a decode that lands nowhere near a resource. Log
        // the destination, its decode, and the NEAREST registered base so the
        // three are distinguishable at a glance.
        // Cap raised 24 -> 512 (2026-07-23). These lines are the ONLY source
        // of the miss-object set that gets intersected with the content gap,
        // and at 24 the intersection was computed from 5 distinct objects —
        // far too few to size the opportunity, while reading like an answer.
        // A sample that feeds a decision needs to be big enough to carry it.
        static uint32_t logged_tile_misses = 0;
        if (logged_tile_misses < 512u) {
          uint32_t nearest_base = 0;
          uint32_t nearest_delta = 0xFFFFFFFFu;
          uint32_t nearest_size = 0;
          uint32_t with_base = 0;
          // The nearest resource's OBJECT address, so a miss can be
          // intersected with the content-gap probe's `guest=` addresses. That
          // intersection is the evidence for whether extending this matcher
          // would actually close gap allocations, or just convert misses into
          // packets nobody was missing. Reporting a base alone cannot answer
          // it — the two probes had no shared key.
          uint32_t nearest_object = 0;
          for (const auto &entry : g_state.resources) {
            if (entry.second.base_address == 0) {
              continue;
            }
            ++with_base;
            // Compare DECODED, and report decoded. The first version of this
            // probe reported the raw base, which is precisely what disguised
            // the address-space mismatch it was built to find.
            const uint32_t candidate_base =
                refii::renderer::DecodeFableGpuMemoryPointer(
                    entry.second.base_address);
            const uint32_t delta = destination_base > candidate_base
                                       ? destination_base - candidate_base
                                       : candidate_base - destination_base;
            if (delta < nearest_delta) {
              nearest_delta = delta;
              nearest_base = candidate_base;
              nearest_size = entry.second.byte_size;
              nearest_object = entry.first;
            }
          }
          REXLOG_INFO(
              "Refii GTL tile MISS #{} symbol=0x{:08X} dst=0x{:08X} "
              "decoded=0x{:08X} src=0x{:08X} resources={} with_base={} "
              "nearest_base=0x{:08X} nearest_size={} delta=0x{:08X} "
              "nearest_object=0x{:08X}",
              ++logged_tile_misses, symbol_address, destination,
              destination_base, linear_source, g_state.resources.size(),
              with_base, nearest_base, nearest_size, nearest_delta,
              nearest_object);
        }
        return;
      }
      for (const uint32_t target_key : targets) {
      auto target_it = g_state.resources.find(target_key);
      if (target_it == g_state.resources.end()) {
        continue;
      }
      Resource *target = &target_it->second;
      const uint32_t allocation_offset =
          destination_base -
          refii::renderer::DecodeFableGpuMemoryPointer(target->base_address);
      if (allocation_offset != 0) {
        // A sub-allocation write. We know WHERE it lands but not how many
        // bytes the call moves: the linear size would have to come from
        // pitch*height, and the pitch argument's units (bytes vs blocks) are
        // not pinned for these roots. Copying "the rest of the allocation"
        // from the linear source would over-read unrelated memory, so decline
        // and count it — an undercount that names itself is recoverable, a
        // wrong blob is not.
        ++g_state.stats.tile_content_offset_skipped;
        continue;
      }
      // Size is `RowPitch * source_rows`, both stated by the call itself. This
      // no longer consults the resource's format or byte_size at all — the
      // prototypes made the call self-sufficient, which is why `size_unknown`
      // should now be structurally impossible rather than merely rare.
      uint32_t copy_size = tile_pitch * source_rows;
      if (copy_size == 0) {
        // Located it, but nothing states how many bytes to take. Log the
        // operands so the relation stays checkable, then decline — an
        // undercount is recoverable, a wrong-sized blob in the content store
        // is not, because the consumer uploads whatever is stored.
        ++g_state.stats.tile_content_size_unknown;
        static uint32_t logged_tile_size_unknown = 0;
        if (logged_tile_size_unknown < 24u) {
          REXLOG_INFO(
              "Refii GTL tile SIZE? #{} symbol=0x{:08X} dst=0x{:08X} "
              "decoded=0x{:08X} resource=0x{:08X} width={} height={} "
              "pitch={} res_wh={}x{} res_fmt={} res_bytes={}",
              ++logged_tile_size_unknown, symbol_address, destination,
              destination_base, target_key, tile_width, tile_height, tile_pitch,
              target->width, target->height, target->format,
              target->byte_size);
        }
        continue;
      }
      const uint32_t readable_source =
          refii::renderer::FableGuestPhysicalViewAddress(linear_source);
      if (copy_size > (64u << 20) ||
          !refii::renderer::bink::IsReadableGuestByteRange(
              guest_base, readable_source, copy_size)) {
        ++g_state.stats.tile_content_unreadable;
        continue;
      }
      constexpr uint64_t kContentStoreBudget = 192ull << 20;
      const uint64_t key = static_cast<uint64_t>(target_key) << 1;  // base
      auto &image = g_state.content_images[key];
      // LAYOUT CHANGE = the stored bytes are not compatible with these.
      //
      // An earlier unlock may have left a LARGER tiled image here. Copying
      // copy_size linear bytes over its front and then flagging the whole
      // buffer `source_linear = true` would leave a TILED TAIL inside a buffer
      // labelled linear — and the consumer uploads `upload_data` whole under
      // that single flag, so the tail decodes as garbage. One flag per image
      // means one layout per image; mixing them is not representable.
      //
      // So when the layout differs, discard rather than overlay: resize to
      // exactly this content, shrinking if needed.
      if (!image.bytes.empty() && image.source_linear != true &&
          image.bytes.size() != copy_size) {
        if (image.bytes.size() > copy_size) {
          g_state.content_image_bytes -= image.bytes.size() - copy_size;
        } else {
          g_state.content_image_bytes += copy_size - image.bytes.size();
        }
        image.bytes.assign(copy_size, 0u);
      } else if (image.bytes.size() < copy_size) {
        const uint64_t growth = copy_size - image.bytes.size();
        if (g_state.content_image_bytes + growth > kContentStoreBudget) {
          ++g_state.stats.content_store_budget_drops;
          continue;
        }
        image.bytes.resize(copy_size, 0u);
        g_state.content_image_bytes += growth;
      }
      // Copy at the call, for the same reason the unlock writer does: this is
      // the moment the root says these bytes are the content.
      std::memcpy(image.bytes.data(),
                  guest_base + readable_source +
                      refii::renderer::bink::GuestPhysicalHostOffset(
                          readable_source),
                  copy_size);
      image.source_linear = true;
      ++target->content_generation;
      MarkResourceChangedLocked(target_key);
      ContentPacket packet;
      packet.resource = target_key;
      packet.content_generation = target->content_generation;
      packet.serial = g_state.next_packet_id++;
      packet.kind = target->kind;
      packet.mip_range = false;
      packet.source_address = readable_source;
      packet.byte_size = copy_size;
      packet.allocation_offset = 0;
      packet.view_parent_texture = target->view_parent_texture;
      packet.view_face = target->view_face;
      packet.view_level = target->view_level;
      packet.view_identity_stated = target->view_identity_stated;
      // The whole point of this writer: the layout is STATED here, because
      // the root read a linear buffer.
      packet.source_linear = true;
      g_state.content_packets.push_back(packet);
      ++g_state.stats.content_packets;
      ++g_state.stats.tile_content_packets;
      ++g_state.stats.content_bytes_stored;
      }
      // Matching a resource but storing nothing (offset-only, unreadable, or
      // budget) is deliberately NOT counted as `unmatched`: separating "the
      // match failed" from "the match worked and we declined the bytes" is
      // the whole point of these counters, and each declining branch above
      // already counted itself.
      return;
    }
    case kUnlockTextureRect:
    case kUnlockVertexBuffer:
    case kUnlockIndexBuffer:
    case kUnlockResource:
    case kXGCompressSurface: {
      auto it = g_state.resources.find(args.r3);
      if (it == g_state.resources.end()) {
        it = g_state.resources.find(args.r4);
      }
      if (it == g_state.resources.end()) {
        ++g_state.stats.unmatched_set_events;
        return;
      }
      ++it->second.content_generation;
      MarkResourceChangedLocked(it->first);
      // §4: state WHAT changed, not just that something did. The lock root
      // accumulated the CPU-dirtied extent into the D3DResource (+0x14 base
      // allocation, +0x18 mip allocation) as packed (start<<16)|end in
      // 128-byte units; this root converts it to bytes against the allocation
      // address and flushes exactly that, then resets it to the 0xFFFF0000
      // empty sentinel — which is why it can only be read here, at entry.
      it->second.dirty_base_start = 0;
      it->second.dirty_base_end = 0;
      it->second.dirty_mip_start = 0;
      it->second.dirty_mip_end = 0;
      it->second.dirty_base_allocation = 0;
      it->second.dirty_mip_allocation = 0;
      if (symbol_address == kUnlockResource && guest_base != nullptr &&
          refii::renderer::bink::IsReadableGuestByteRange(guest_base, args.r3,
                                                          0x1Cu)) {
        const auto decode = [&](uint32_t packed, uint32_t allocation,
                                uint32_t &out_start, uint32_t &out_end) {
          constexpr uint32_t kEmptyDirtyRange = 0xFFFF0000u;
          if (packed == kEmptyDirtyRange || allocation == 0) {
            return;
          }
          const uint32_t start_units = packed >> 16;
          const uint32_t end_units = packed & 0xFFFFu;
          if (end_units <= start_units) {
            return;
          }
          const uint32_t aligned = allocation & 0xFFFFFF80u;
          out_start = aligned + (start_units << 7);
          out_end = aligned + (end_units << 7);
        };
        // D3D::UnlockResource(D3DResource*, base_address, mip_address): the
        // caller states both allocation addresses directly.
        decode(refii::renderer::bink::LoadGuestU32(guest_base, args.r3 + 0x14u),
               args.r4, it->second.dirty_base_start,
               it->second.dirty_base_end);
        decode(refii::renderer::bink::LoadGuestU32(guest_base, args.r3 + 0x18u),
               args.r5, it->second.dirty_mip_start, it->second.dirty_mip_end);
        it->second.dirty_base_allocation = args.r4 & 0xFFFFFF80u;
        it->second.dirty_mip_allocation = args.r5 & 0xFFFFFF80u;
        // Deduped per (object, range start) so a streaming upload re-locking
        // page by page cannot burn the cap (run 117 lost the UI-icon phase
        // that way), while a multi-range resource still reports EVERY range.
        // Keying on the object alone hid five of the icon cube's six faces.
        //
        // alloc= is the decisive field for multi-range placement: it is the
        // allocation the root itself named (r4/r5). If alloc stays FIXED while
        // base= steps, the ranges are offsets into ONE allocation and
        // (start - alloc) is the sub-write offset. If alloc STEPS WITH base,
        // each range is its own allocation and there is no offset to compute —
        // the faces are separate memory and a single record cannot hold them.
        // Content authority: state the ranges as packets. This is the whole
        // content model — the root already told us which bytes changed, where
        // they sit inside their allocation, and (via the view call) which face
        // and level they are. Nothing here scans or guesses.
        const auto emit_content = [&](bool mip, uint32_t start, uint32_t end,
                                      uint32_t allocation) {
          if (end <= start || allocation == 0) {
            return;
          }
          ContentPacket packet;
          packet.resource = it->first;
          packet.content_generation = it->second.content_generation;
          packet.serial = g_state.next_packet_id++;
          packet.kind = it->second.kind;
          packet.mip_range = mip;
          packet.source_address =
              refii::renderer::FableGuestPhysicalViewAddress(start);
          packet.byte_size = end - start;
          packet.allocation_offset = start - allocation;
          packet.view_parent_texture = it->second.view_parent_texture;
          packet.view_face = it->second.view_face;
          packet.view_level = it->second.view_level;
          packet.view_identity_stated = it->second.view_identity_stated;

          // Take the bytes NOW. This is the instant the root flushed them, so
          // it is the only moment the range is guaranteed to hold what the
          // guest wrote. Reading it later is what forced every "is this blank
          // or is it poison" question the observer is made of.
          constexpr uint64_t kContentStoreBudget = 192ull << 20;
          const uint64_t required =
              static_cast<uint64_t>(packet.allocation_offset) +
              packet.byte_size;
          if (guest_base != nullptr && required <= (64ull << 20) &&
              refii::renderer::bink::IsReadableGuestByteRange(
                  guest_base, packet.source_address, packet.byte_size)) {
            const uint64_t key =
                (static_cast<uint64_t>(packet.resource) << 1) | (mip ? 1u : 0u);
            auto &image = g_state.content_images[key];
            // Same layout-compatibility rule as the tile writer, in reverse:
            // if the tile channel left LINEAR bytes here, this unlock's tiled
            // bytes cannot be laid on top of them — one flag per image means
            // one layout per image. Drop the incompatible content first so the
            // buffer only ever holds one encoding.
            if (!image.bytes.empty() && image.source_linear) {
              g_state.content_image_bytes -= image.bytes.size();
              image.bytes.clear();
            }
            if (image.bytes.size() < required) {
              const uint64_t growth = required - image.bytes.size();
              if (g_state.content_image_bytes + growth <= kContentStoreBudget) {
                image.bytes.resize(static_cast<size_t>(required), 0u);
                g_state.content_image_bytes += growth;
              }
            }
            if (image.bytes.size() >= required) {
              std::memcpy(image.bytes.data() + packet.allocation_offset,
                          guest_base + packet.source_address +
                              refii::renderer::bink::GuestPhysicalHostOffset(
                                  packet.source_address),
                          packet.byte_size);
              // Unlock flushes the D3DResource's OWN allocation, so these
              // bytes are in whatever layout the resource holds — tiled when
              // the texture is tiled. Stated, not assumed.
              image.source_linear = false;
              packet.source_linear = false;
              ++g_state.stats.content_bytes_stored;
            } else {
              ++g_state.stats.content_store_budget_drops;
            }
          } else {
            ++g_state.stats.content_unreadable;
          }

          g_state.content_packets.push_back(packet);
          ++g_state.stats.content_packets;
        };
        emit_content(false, it->second.dirty_base_start,
                     it->second.dirty_base_end,
                     it->second.dirty_base_allocation);
        emit_content(true, it->second.dirty_mip_start, it->second.dirty_mip_end,
                     it->second.dirty_mip_allocation);

        static std::unordered_set<uint64_t> logged_dirty_extent_ranges;
        static uint32_t logged_dirty_extents = 0;
        if ((it->second.dirty_base_end > it->second.dirty_base_start ||
             it->second.dirty_mip_end > it->second.dirty_mip_start) &&
            logged_dirty_extents < 96u) {
          const uint64_t range_key =
              (static_cast<uint64_t>(it->first) << 32) |
              it->second.dirty_base_start;
          if (logged_dirty_extent_ranges.insert(range_key).second) {
            REXLOG_INFO(
                "Refii GTL unlock dirty extent #{} object=0x{:08X} gen={} "
                "base=0x{:08X}..0x{:08X} alloc=0x{:08X} "
                "mip=0x{:08X}..0x{:08X} mip_alloc=0x{:08X}",
                ++logged_dirty_extents, it->first,
                static_cast<unsigned long long>(it->second.content_generation),
                it->second.dirty_base_start, it->second.dirty_base_end,
                it->second.dirty_base_allocation, it->second.dirty_mip_start,
                it->second.dirty_mip_end, it->second.dirty_mip_allocation);
          }
        }
      }
      return;
    }
    case kEmitTessellatedIndexedPackets:
    case kEmitTessellatedPrimitivePackets: {
      // Half one of the split capture (see PendingTessellatedPatchCapture).
      // No packet is built here: everything that describes the DRAW is only
      // valid after the emitter's prologue has run.
      //
      // Arg maps DIFFER between the two emitters and are NOT interchangeable:
      //   0x82207AF8 terrain -> r6 = base index, r7 = index/patch count
      //                         (call site 0x82A8F268: r4=0x12, r5=0)
      //   0x8221C898 water   -> r5 = start vertex, r6 = patch count
      //                         (call site 0x8222046C: r4=18, r6=1)
      t_pending_tess_capture = {};
      t_pending_tess_capture.symbol_address = symbol_address;
      t_pending_tess_capture.device = args.r3;
      t_pending_tess_capture.caller_lr = args.lr;
      t_pending_tess_capture.arg_primitive_type = args.r4;
      t_pending_tess_capture.guest_base = guest_base;
      if (symbol_address == kEmitTessellatedIndexedPackets) {
        t_pending_tess_capture.arg_index_offset = args.r6;
        t_pending_tess_capture.arg_count = args.r7;
        ++g_state.stats.tess_terrain_calls;
        ++g_state.stats.tess_terrain_calls_this_frame;
      } else {
        t_pending_tess_capture.arg_index_offset = args.r5;
        t_pending_tess_capture.arg_count = args.r6;
        ++g_state.stats.tess_water_calls;
        ++g_state.stats.tess_water_calls_this_frame;
      }
      if (guest_base != nullptr && args.r3 != 0 &&
          refii::renderer::bink::IsReadableGuestByteRange(
              guest_base, args.r3 + kDeviceRingWritePointer,
              sizeof(uint32_t))) {
        t_pending_tess_capture.ring_begin = refii::renderer::bink::LoadGuestU32(
            guest_base, args.r3 + kDeviceRingWritePointer);
      }
      // PHASE 1 (CAPTURE-ONLY): these two roots are the other half of the
      // six-root constant-push choke point (the callers of sub_8221DE68), so
      // they get the same entry-time device snapshot the DrawPacket roots do.
      // Stashed rather than applied because their packet is only built at exit
      // -- but the masks it records are only valid HERE.
      t_pending_tess_capture.device_snapshot =
          CaptureDrawDeviceSnapshot(args.r3, guest_base);
      DrainDeviceDirtyMasksLocked(args.r3, guest_base);
      return;
    }
    case kDrawIndexedUserPrimitives:
      BuildDrawPacketLocked(symbol_address, args, guest_base);
      ++t_draw_indexed_up_depth;
      return;
    case kBeginIndexedVertices:
      // MEASURED 2026-07-25 (runs 271/274/282/283): this branch is taken by
      // EVERY kBeginIndexedVertices call in the title/menu workload, and the
      // t_draw_indexed_up_depth half is the only one that ever fires.
      //   * IDA xrefs to 0x82217DB8 = {0x821D7550, 0x8220A3F0, 0x8222E120,
      //     0x82AA59B8}. kBeginVertices (0x82205F68) is NOT among them, so the
      //     `t_begin_vertices_depth` clause is structurally dead.
      //   * Runtime confirmation: BeginPinCensus.arm_reject_symbol == 0 in
      //     every run ever logged. A t_begin_vertices_depth suppression would
      //     land there (packet suppressed -> t_last_packet_symbol mismatch at
      //     exit), so zero rejects there means the clause never fired.
      //   * sub_82217DB8 has NEVER appeared in the family-coverage dump, i.e.
      //     no standalone BeginIndexedVertices exists in these workloads:
      //     0x8220A3F0 is its sole live caller and calls it exactly once.
      // Hence arm_reject_depth == the sub_8220A3F0 call total EXACTLY (483/483
      // run 283 @2160, 1503/1503 run 283 @2880, 3243/3243 run 274) — those are
      // suppressed-by-design, not lost: the wrapper packets them with 100%
      // inline geometry (family coverage geom == total for 0x8220A3F0). Do NOT
      // read a high arm_reject_depth as a pin failure.
      // The `|| t_begin_vertices_depth != 0` half of this test was DELETED
      // 2026-07-26 as provably unreachable (both bullets above). The depth
      // counter itself is kept — it costs two instructions and it is what makes
      // the claim falsifiable: were BeginVertices ever to allocate indices, the
      // nested call would now build a packet, displace t_last_packet_symbol,
      // and show up as a nonzero BeginPinCensus.arm_reject_symbol at the outer
      // BeginVertices exit. That counter staying 0 is the standing proof.
      if (t_draw_indexed_up_depth != 0) {
        // Inner ring allocation of a DrawIndexedUserPrimitives call: the draw
        // is already packeted at the wrapper.
        ++g_state.stats.state_events;
        return;
      }
      BuildDrawPacketLocked(symbol_address, args, guest_base);
      return;
    case kBeginVertices:
      BuildDrawPacketLocked(symbol_address, args, guest_base);
      ++t_begin_vertices_depth;
      return;
    case kDrawPrimitive:
    case kDrawIndexedPrimitive:
    case kDrawPrimitiveUP:
      BuildDrawPacketLocked(symbol_address, args, guest_base);
      // ENTER BEHAVIOUR IS UNCHANGED — this only stashes the (packet, device,
      // guest base) triple so the matching EXIT can run the authoritative
      // self-read. r3 IS the device for all three of these roots (IDA:
      // D3DDevice_DrawVertices / _DrawIndexedVertices / _DrawVerticesUP all
      // take pDevice first). See ApplyAuthoritativeExitDeviceStateLocked for
      // the per-root prologue verification.
      RecordPendingExitSelfRead(symbol_address, t_last_packet_id, args.r3,
                                guest_base);
      return;
    case kResolve:
      // sub_82206888 is D3DDevice_Resolve — an EDRAM resolve, not a vertex
      // draw — so it records into the ResolvePacket stream.
      //
      // THE LEGACY DrawPacket BUILD IS DELETED (2026-08-04). It was retained
      // "until §3b flips ownership to the ResolvePacket stream"; that flip has
      // happened — kGtlResolve is the only live resolve executor, and every
      // DrawPacket consumer now rejects this root by source_symbol BEFORE
      // reading any field of the packet: the replay loop
      // (fable_direct_renderer.cpp:59528 `rej_resolve`), the backlog
      // supersession scan (:59506), and the exec shadow (:59166). The video
      // insert path only accepts 0x8220A3F0. So the packet had no consumer at
      // all.
      //
      // It was not free. Every resolve built a full DrawPacket — two
      // 1024-dword constant banks (~8KB) plus a guest-memory refresh of both
      // float banks, the fetch block and the texture array — and run 570
      // measured 7872 of them against 35854 real draw packets, i.e. ~18% of
      // all packet construction doing nothing.
      //
      // It was also actively MISLEADING: because a resolve has no vertex
      // shader, these packets were the entire population of the
      // `vs_absent`/`vs_backfill_tried` counters (run 570: vs_absent ==
      // vs_backfill_tried == resolve_packets == 7872, filled=0). That
      // coincidence had already been root-caused twice and cost several
      // changes chasing a "shader gap" that was this artifact. With the build
      // gone those counters read zero in a healthy run and become a genuine
      // alarm for a real draw that lost its shader.
      BuildResolvePacketLocked(symbol_address, args, guest_base);
      return;
    case kClearF:
    case kClearRect:
      // Clear class, capture-only (census). Records into the ClearPacket
      // stream ordered with draws by packet_id; the observer still produces
      // the executed clears until the submission step consumes this stream.
      BuildClearPacketLocked(symbol_address, args, guest_base);
      return;
    case kDisplaySwap:
    case kPresent: {
      // GPU-LESS BLOCKER BIT (pDevice+0x2ABD |= 2) WAS TRIED HERE AND FROZE
      // THE GAME 2026-07-27. Reverted deliberately, and the reason matters:
      // that bit does TWO things, and we only wanted one of them.
      //   (a) D3D_CDevice_SetFence (0x822986F4) self-satisfies the fence in
      //       software -- DESIRABLE, it is what unblocks the waiters.
      //   (b) D3D_CBlocker_Check (0x82BA1FF0) returns 0 and NEVER BLOCKS --
      //       which lets the guest run ahead of work it must wait for. That is
      //       what froze it.
      // The correct shape is (a) without (b): satisfy the fence at the moment
      // the guest sets it, and leave every blocker check honest. See
      // [[refii-guest-blocker-starvation]].
      // STATED present source probe (2026-07-23 audit). IDA Present
      // (sub_82B6F1D0): D3DDevice_Resolve(RT0 -> g_FrontBufferTexture) then
      // D3DDevice_Swap(device r3, g_FrontBufferTexture r4). So r4 at kDisplaySwap
      // is the guest's chosen present texture, and device.render_targets[0] is
      // the RT0 it resolved. The renderer currently GUESSES the present source
      // (candidate ranking + starvation fallback) which presents partial
      // host-pump composes = the fragmentation. This probe pins the two unknowns
      // the real fix needs: (1) does this hook fire PER-FRAME (memory says guest
      // Present ~8.4fps) or once at boot as the old comment claimed; (2) which
      // tracked target is g_FrontBufferTexture / the RT0 it resolves.
      static uint64_t present_hook_fires = 0;
      const uint32_t stated_front_buffer =
          symbol_address == kDisplaySwap ? args.r4 : 0u;
      ++present_hook_fires;
      // Run 199 PROVED: Present (0x82B6F1D0) fires per guest frame with
      // render_targets[0] as the RT0 it resolves into g_FrontBufferTexture. Store
      // that stated source + a per-guest-Present EPOCH so the present pump can
      // (a) present the frame the guest actually finished, and (b) advance only
      // when the guest does — holding between guest Presents instead of showing
      // partial host-pump composes. Present carries no r4 (uses the global), so
      // keep the last non-zero front buffer seen at the boot Swap.
      if (symbol_address == kPresent) {
        // Carried to exit so the frame-completion callback can be emulated after
        // the guest has written its own mftb stamp and bumped
        // g_PresentFrameCount (both happen inside Present).
        t_pending_present_base = guest_base;
        // LIVE-WINS, same rationale as the draw-packet builder (2026-08-02):
        // the SetRenderTarget mirror is fed by a setter Fable barely calls
        // (run 132: 8 SetRenderTarget hooks against ~168 draws/frame), so at
        // Present it can hold whatever target the last HOOKED set named —
        // run 520 pinned it at the title target 0x4011F370 for the whole
        // in-game session while the world rendered into 0x404B9700, and the
        // present blit showed the stale menu RT forever. The device register
        // at 0x3098 is the RT0 Present is about to resolve into the front
        // buffer; read it live, mirror as fallback when unreadable/zero.
        uint32_t live_present_rt0 = 0;
        const uint32_t present_device = g_state.last_draw_device_pointer;
        if (guest_base != nullptr && present_device != 0) {
          constexpr uint32_t kDeviceRenderTargetBaseOffset = 0x3098;
          const uint32_t rt0_address =
              present_device + kDeviceRenderTargetBaseOffset;
          if (refii::renderer::bink::IsReadableGuestByteRange(
                  guest_base, rt0_address, sizeof(uint32_t))) {
            live_present_rt0 =
                refii::renderer::bink::LoadGuestU32(guest_base, rt0_address);
          }
        }
        g_state.stated_present_rt0 = live_present_rt0 != 0
                                         ? live_present_rt0
                                         : g_state.device.render_targets[0];
        ++g_state.stated_present_epoch;
        // Frame-boundary marker: every packet issued before this Present belongs
        // to the completed guest frame. The pump drains up to here so it composes
        // exactly one guest frame, in order (no next-frame interleave).
        g_state.guest_frame_boundary_packet_id = g_state.next_packet_id;
        // Signal the pump that a complete guest frame is ready. Runs under
        // g_state.mutex, so the callback must only notify (no heavy locks). This
        // is what makes the pump GUEST-DRIVEN instead of timer-driven.
        if (GuestFramePresentedCallback cb =
                g_guest_frame_presented_callback.load(std::memory_order_acquire)) {
          cb();
        }
      }
      if (stated_front_buffer != 0) {
        g_state.stated_present_front_buffer = stated_front_buffer;
        // Decode the GPU base from the STATED header while we are at the
        // present root and the object is known-live. header+0x20 & 0xFFFFF000
        // is the texture's GPU base (D3DDevice_Resolve reads exactly this to
        // build RB_COPY_DEST_BASE, 0x82206974 -> 0x82206998 -> 0x82206BE0).
        // This replaces the deleted observer's display_present_texture_base.
        if (guest_base != nullptr) {
          const uint32_t header_dword1 = refii::renderer::bink::LoadGuestU32(
              guest_base, stated_front_buffer + 0x20u);
          g_state.stated_present_front_buffer_base =
              header_dword1 & 0xFFFFF000u;
        }
      }
      if (present_hook_fires <= 24u || (present_hook_fires % 256u) == 0u) {
        REXLOG_INFO(
            "Refii GTL present-hook #{} sym=0x{:08X} front_buffer=0x{:08X} "
            "rt0=0x{:08X} epoch={}",
            static_cast<unsigned long long>(present_hook_fires), symbol_address,
            g_state.stated_present_front_buffer,
            g_state.stated_present_rt0,
            static_cast<unsigned long long>(g_state.stated_present_epoch));
      }
      ++g_state.stats.state_events;
      return;
    }
    default: {
      // Unknown-roots census for the observer-removal campaign: every
      // hooked symbol the GTL has no handler for is a root the old code may
      // be silently covering — each deletion round consults this list. One
      // line per new symbol, cumulative count every 4096 unknown events.
      static std::unordered_map<uint32_t, uint64_t> unknown_roots;
      static uint64_t unknown_events = 0;
      auto [it, first_seen] = unknown_roots.try_emplace(symbol_address, 0u);
      ++it->second;
      ++unknown_events;
      if (first_seen) {
        REXLOG_INFO("Refii GTL unknown root first-seen sub_{:08X} (#{} known)",
                    symbol_address,
                    static_cast<uint32_t>(unknown_roots.size()));
      } else if ((unknown_events % 4096u) == 0u) {
        const auto top = std::max_element(
            unknown_roots.begin(), unknown_roots.end(),
            [](const auto &a, const auto &b) { return a.second < b.second; });
        REXLOG_INFO(
            "Refii GTL unknown roots census: {} symbols, {} events, "
            "top=sub_{:08X} x{}",
            static_cast<uint32_t>(unknown_roots.size()),
            static_cast<unsigned long long>(unknown_events), top->first,
            static_cast<unsigned long long>(top->second));
      }
      return;
    }
  }
}

// OnFetchConstantDispatch DELETED 2026-07-27. It was one of the observer dispatch feeds; its caller went when the GTL took over that decode, leaving the entry point itself dead. Verified zero references repo-wide (comments only).

// OnRenderTargetBindDispatch DELETED 2026-07-27. It was one of the observer dispatch feeds; its caller went when the GTL took over that decode, leaving the entry point itself dead. Verified zero references repo-wide (comments only).

// OnDepthTargetBindDispatch DELETED 2026-07-27. It was one of the observer dispatch feeds; its caller went when the GTL took over that decode, leaving the entry point itself dead. Verified zero references repo-wide (comments only).

// OnShaderBindDispatch DELETED 2026-07-27. It was one of the observer dispatch feeds; its caller went when the GTL took over that decode, leaving the entry point itself dead. Verified zero references repo-wide (comments only).

void ApplyLiteralShaderConstants(uint32_t first_register, uint32_t dword_count,
                                 uint32_t data_pointer, uint8_t *guest_base) {
  constexpr uint32_t kAluRegisters = 512;
  constexpr uint32_t kAluDwords = kAluRegisters * 4u;
  if (guest_base == nullptr || dword_count == 0 ||
      first_register >= kAluRegisters) {
    return;
  }
  const uint32_t first_dword = first_register * 4u;
  if (first_dword >= kAluDwords) {
    return;
  }
  const uint32_t dwords = std::min(dword_count, kAluDwords - first_dword);
  const uint32_t bytes = dwords * 4u;
  if (!refii::renderer::bink::IsReadableGuestByteRange(guest_base, data_pointer,
                                                       bytes)) {
    return;
  }
  const uint8_t *block =
      guest_base + data_pointer +
      refii::renderer::bink::GuestPhysicalHostOffset(data_pointer);
  std::lock_guard<std::mutex> lock(g_state.mutex);
  for (uint32_t index = 0; index < dwords; ++index) {
    uint32_t raw = 0;
    std::memcpy(&raw, block + index * 4u, sizeof(raw));
    g_state.device.literal_alu[first_dword + index] = __builtin_bswap32(raw);
  }
  // Mark only WHOLE registers as covered: a partial tail would otherwise
  // publish three literal components beside one stale device component.
  for (uint32_t reg = first_register;
       reg < kAluRegisters && (reg + 1u) * 4u <= first_dword + dwords; ++reg) {
    if (g_state.device.literal_alu_covered[reg] == 0) {
      ++g_state.device.literal_alu_registers_written;
    }
    g_state.device.literal_alu_covered[reg] = 1u;
  }
  ++g_state.stats.state_events;
}

void OnShaderConstantDispatch(bool pixel_bank, uint32_t first_dword,
                              uint32_t data_pointer, uint32_t dword_count,
                              uint8_t *guest_base) {
  constexpr uint32_t kBankDwords = 1024;
  if (guest_base == nullptr || data_pointer == 0 || dword_count == 0 ||
      first_dword >= kBankDwords) {
    return;
  }
  const uint32_t dwords = std::min(dword_count, kBankDwords - first_dword);
  const uint32_t bytes = dwords * 4u;
  if (!refii::renderer::bink::IsReadableGuestByteRange(guest_base,
                                                       data_pointer, bytes)) {
    return;
  }
  const uint8_t *block =
      guest_base + data_pointer +
      refii::renderer::bink::GuestPhysicalHostOffset(data_pointer);
  std::lock_guard<std::mutex> lock(g_state.mutex);
  auto &bank =
      pixel_bank ? g_state.device.ps_float_bank : g_state.device.vs_float_bank;
  for (uint32_t dword_index = 0; dword_index < dwords; ++dword_index) {
    uint32_t raw = 0;
    std::memcpy(&raw, block + dword_index * 4u, sizeof(raw));
    bank[first_dword + dword_index] = __builtin_bswap32(raw);
  }
  if (pixel_bank) {
    g_state.device.ps_float_bank_valid = true;
  } else {
    g_state.device.vs_float_bank_valid = true;
  }
  ++g_state.stats.state_events;
}

// The observer's dispatch feed is DELETED (see hook_observer.cpp), so
// OnStateDispatch's own decode is the only producer for these two entry points
// on the deferred-dispatch carrier. The value shadow that proved them
// equivalent (agree=34160/13353, differ=0, slot_mismatch=0 across runs 306/307)
// has been REMOVED along with its thread_local guard: it cost a TLS read on a
// path taken ~235k times per run, and the GTL is meant to be a lean translator,
// not a measurement harness.
//
// What is still genuinely unknown is whether the TWO SURVIVING observer sampler
// calls -- the setter-symbol path (D3DDevice_SetSamplerState*) and the device
// reset -- ever fire. That is now answered structurally instead of with a flag:
// OnStateDispatch calls the Apply* helpers DIRECTLY, so anything arriving at
// the public entry points below came from outside the GTL and is counted.
void ApplyRenderStateLocked(uint32_t slot, uint32_t value) {
  g_state.device.render_states[slot] = value;
  g_state.device.render_state_valid_mask |= 1u << slot;
  ++g_state.stats.state_events;
}

void ApplySamplerStateLocked(uint32_t slot, uint32_t packed_state) {
  g_state.device.sampler_states[slot] = packed_state;
  g_state.device.sampler_state_valid_mask |= 1u << slot;
  ++g_state.stats.state_events;
}

// OnRenderStateDispatch DELETED 2026-07-27. It was one of the observer dispatch feeds; its caller went when the GTL took over that decode, leaving the entry point itself dead. Verified zero references repo-wide (comments only).

// Decode the guest's lazy state-dispatch record DIRECTLY, instead of receiving
// it pre-decoded from the observer.
//
// Why this exists: render states never arrive through the wrapper setters --
// those only store PENDING values that Render_ApplyDirtyStateVar (0x8221E550)
// applies later. Until now the GTL learned them only because the observer
// decoded the record and called OnRenderStateDispatch, which made the observer
// MANDATORY for GTL packets to carry correct at-draw state. Of the seven device
// -state feeds the GTL takes from the observer, this was the only one with no
// self-read equivalent, so it was the single thing keeping the observer alive in
// root-only mode.
//
// The record layout is the guest's own, read straight from guest memory:
//   +0   current value        +4   pending value
//   +16  callback offset      +21  dispatch kind (1 = render state, 2 = sampler)
//   +22  sampler slot (kind 2 only)
// The callback table lives at 0x834970E8; the entry is
// table + offset + bias, bias 64 for render states and 468 for samplers, and
// the resolved target address maps to a slot through the shared
// FableRenderStateSlotForAddress / FableSamplerStateSlotForAddress tables.
// A record whose current == pending is not a change and is skipped, exactly as
// the guest's own dispatcher does.
//
// SAMPLERS ARE HANDLED HERE (see the kind == 2 tail below). The blocker used to
// be that packing went through FableDirectStorePackedSamplerState, which also
// MUTATES direct-pipeline state and would therefore double-store; the pure
// packer FableDirectPackSamplerStateValue was split out to close that, and the
// GTL packs against its own sampler mirror.
//
// VERIFIED 2026-08-02 (run 503, gameplay-covering): self_read_sampler=107k,
// legacy_external_state_feed=0 — the sampler self-read carries the load and
// the observer's OnSamplerStateDispatch feed was deleted on that evidence
// (bad_slot=2 in the same run is a dispatch-record decode artifact, unrelated
// to feed reachability).
void OnStateDispatch(const RendererHookArgs &args, uint8_t *guest_base) {
  ++g_state.stats.state_dispatch_calls;
  if (guest_base == nullptr || args.r3 == 0) {
    ++g_state.stats.state_dispatch_bad_args;
    return;
  }
  const uint8_t dispatch_kind =
      refii::renderer::bink::LoadGuestU8(guest_base, args.r3 + 21);
  if (dispatch_kind != 1u && dispatch_kind != 2u) {
    ++g_state.stats.state_dispatch_not_render;
    return;
  }
  const uint32_t current_value =
      refii::renderer::bink::LoadGuestU32(guest_base, args.r3);
  const uint32_t pending_value =
      refii::renderer::bink::LoadGuestU32(guest_base, args.r3 + 4);
  if (current_value == pending_value) {
    ++g_state.stats.state_dispatch_unchanged;
    return;
  }
  const uint32_t callback_table =
      refii::renderer::bink::LoadGuestU32(guest_base, 0x834970E8u);
  const uint32_t callback_offset =
      refii::renderer::bink::LoadGuestU32(guest_base, args.r3 + 16);
  // Bias differs per kind: render states sit 64 bytes into the callback entry,
  // samplers 468. Measured (run 303): samplers are 210478 of 235692 dispatches
  // -- 89% -- so the sampler half is the BULK of this carrier, not a footnote.
  const uint32_t slot_bias = (dispatch_kind == 1u) ? 64u : 468u;
  const uint32_t callback_slot = callback_table + callback_offset + slot_bias;
  if (callback_slot == 0) {
    return;
  }
  const uint32_t callback_target =
      refii::renderer::bink::LoadGuestU32(guest_base, callback_slot);

  if (dispatch_kind == 1u) {
    const uint32_t render_state_slot =
        refii::renderer::FableRenderStateSlotForAddress(callback_target);
    if (render_state_slot >= kMaxRenderStates) {
      ++g_state.stats.state_dispatch_bad_slot;
      return;
    }
    {
      std::lock_guard<std::mutex> lock(g_state.mutex);
      ApplyRenderStateLocked(render_state_slot, pending_value);
      ++g_state.stats.self_read_state_dispatches;
    }
    return;
  }

  // Sampler dispatch. GTL packs against its OWN sampler mirror using the pure
  // helper, so it never touches direct-pipeline state -- calling
  // FableDirectStorePackedSamplerState here would double-store, which is why
  // this half was deferred until the packer was separated out.
  const uint32_t sampler_slot =
      refii::renderer::bink::LoadGuestU8(guest_base, args.r3 + 22);
  const uint32_t sampler_state_slot =
      refii::renderer::FableSamplerStateSlotForAddress(callback_target);
  if (sampler_slot >= kMaxSamplerSlots || sampler_state_slot == UINT32_MAX) {
    ++g_state.stats.state_dispatch_bad_slot;
    return;
  }
  // READ-MODIFY-WRITE, so the READ must be under the lock too. This used to
  // pack from `g_state.device.sampler_states[sampler_slot]` out here and store
  // the result inside the lock: two threads setting different D3DSAMP states on
  // the SAME sampler slot could both read the old word and the second store
  // would drop the first one's field. Every sampler state is a distinct bitfield
  // of one shared word, so that is a silent lost update, not a benign race.
  {
    std::lock_guard<std::mutex> lock(g_state.mutex);
    const uint32_t packed = refii::renderer::FableDirectPackSamplerStateValue(
        g_state.device.sampler_states[sampler_slot], sampler_state_slot,
        pending_value);
    ApplySamplerStateLocked(sampler_slot, packed);
    ++g_state.stats.self_read_sampler_dispatches;
  }
}

// OnSamplerStateDispatch / OnSamplerStateReset (the observer's external
// sampler feed) DELETED 2026-08-02. The falsifier demanded above arrived:
// run 503 (GTL-authoritative, gameplay clean) showed self_read_sampler=107k
// with legacy_external_state_feed=0 — the external entry points never fired;
// bad_slot=2 was noted in the same run but is a dispatch-record decode
// artifact, unrelated to feed reachability. Samplers are owned by the
// self-read (OnStateDispatch kind==2 -> ApplySamplerStateLocked above).

void OnFrameBoundary() {
  std::lock_guard<std::mutex> lock(g_state.mutex);
  ++g_state.frame_id;
  ++g_state.stats.frames;
  // Hardware-tessellator patch VOLUME. A shape log says what these draws are;
  // this says how many there are, which is the other half of what a rendering
  // design needs. Reported only on frames that actually had some (silent at
  // title/menu, where terrain and water never run), plus a peak watermark.
  {
    const uint64_t frame_calls = g_state.stats.tess_terrain_calls_this_frame +
                                 g_state.stats.tess_water_calls_this_frame;
    if (frame_calls != 0) {
      if (frame_calls > g_state.stats.tess_peak_calls_per_frame) {
        g_state.stats.tess_peak_calls_per_frame = frame_calls;
      }
      REXLOG_INFO(
          "Refii GTL TESS-PATCH census frame={} this_frame[terrain={} water={} "
          "pm4_draws={}] totals[terrain={} water={} pm4_draws={}] "
          "distinct_shapes={} peak_calls_per_frame={} "
          "ring_capture_failed={} device_regs_unreadable={}",
          static_cast<unsigned long long>(g_state.frame_id),
          static_cast<unsigned long long>(
              g_state.stats.tess_terrain_calls_this_frame),
          static_cast<unsigned long long>(
              g_state.stats.tess_water_calls_this_frame),
          static_cast<unsigned long long>(
              g_state.stats.tess_pm4_draws_this_frame),
          static_cast<unsigned long long>(g_state.stats.tess_terrain_calls),
          static_cast<unsigned long long>(g_state.stats.tess_water_calls),
          static_cast<unsigned long long>(g_state.stats.tess_pm4_draws),
          static_cast<unsigned long long>(g_state.stats.tess_distinct_shapes),
          static_cast<unsigned long long>(
              g_state.stats.tess_peak_calls_per_frame),
          static_cast<unsigned long long>(
              g_state.stats.tess_ring_capture_failed),
          static_cast<unsigned long long>(
              g_state.stats.tess_device_regs_unreadable));
    }
    g_state.stats.tess_terrain_calls_this_frame = 0;
    g_state.stats.tess_water_calls_this_frame = 0;
    g_state.stats.tess_pm4_draws_this_frame = 0;
  }
  // Frame-cadenced observer-removal census (every 240 frames, independent of the
  // 512-frame stats block): the begin-pin outcome split + per-family coverage.
  // The 512 cadence missed the menu compose entirely in run 205 (ended frame
  // 1513, next 512-census at 1537), so sample often enough that any reached
  // screen is covered. These are the two numbers driving GTL draw ownership.
  if (g_state.frame_id % 240u == 0u) {
    REXLOG_INFO(
        "Refii GTL begin-pin census frame={} arm[reached={} rej_depth={} "
        "rej_symbol={} rej_bytes={} ok={}] drain[reached={} unreadable={} "
        "blank={} packet_gone={} ok={}]",
        static_cast<unsigned long long>(g_state.frame_id),
        g_begin_pin_census.arm_reached.load(std::memory_order_relaxed),
        g_begin_pin_census.arm_reject_depth.load(std::memory_order_relaxed),
        g_begin_pin_census.arm_reject_symbol.load(std::memory_order_relaxed),
        g_begin_pin_census.arm_reject_bytes.load(std::memory_order_relaxed),
        g_begin_pin_census.arm_ok.load(std::memory_order_relaxed),
        g_begin_pin_census.drain_reached.load(std::memory_order_relaxed),
        g_begin_pin_census.drain_unreadable.load(std::memory_order_relaxed),
        g_begin_pin_census.drain_blank.load(std::memory_order_relaxed),
        g_begin_pin_census.drain_packet_gone.load(std::memory_order_relaxed),
        g_begin_pin_census.drain_ok.load(std::memory_order_relaxed));
    for (const auto& [symbol, cov] : g_family_coverage) {
      REXLOG_INFO(
          "Refii GTL family coverage frame={} sub_{:08X} total={} geom={} "
          "streamvb={} ident={} decl={} tex0={} rt0={}",
          static_cast<unsigned long long>(g_state.frame_id), symbol,
          static_cast<unsigned long long>(cov.total),
          static_cast<unsigned long long>(cov.geom),
          static_cast<unsigned long long>(cov.streamvb),
          static_cast<unsigned long long>(cov.ident),
          static_cast<unsigned long long>(cov.decl),
          static_cast<unsigned long long>(cov.tex0),
          static_cast<unsigned long long>(cov.rt0));
    }
    // PHASE 1 rolling summary of the device-state divergence census. The
    // per-shape lines (one per distinct divergence shape, emitted from
    // AccumulateDeviceSnapshotDivergenceLocked) say WHAT diverges; this says
    // HOW MUCH. Ratios are the readable form: "diverged/compared" per class.
    // EXPECT LARGE class-B numbers on Scaleform/menu frames -- ~28 of the 111
    // SetRenderState_*/SetSamplerState_* entry points have direct callers
    // (mostly GFxRendererXbox360_*) that skip the dirty journal outright. That
    // is confirmation of the premise, not a defect in this census.
    //
    // FETCH READS AS TWO NUMBERS since the Phase-2 fetch cutover:
    //   fetch=<diverged>/<compared>        device vs WHAT THE PACKET CARRIES.
    //                                      Must collapse toward 0/N when
    //                                      fetch_src=1 -- that is the cutover's
    //                                      confirmation. Residual should be
    //                                      attributable to preflush_root.
    //   fetch_mirror=<diverged>/<compared> device vs the OnFetchConstantDispatch
    //                                      mirror. Unchanged by the cutover;
    //                                      run 284 baseline was 10420/12244.
    REXLOG_INFO(
        "Refii GTL device-state divergence census frame={} draws[examined={} "
        "no_snapshot={} diverged={}] mirror[vs_float={}/{} ps_float={}/{} "
        "fetch={}/{} cull={}/{}] fetch_mirror={}/{} "
        "fetch_src={} fetch_device_sourced_draws={} preflush_root_draws={} "
        "journal[flow_unjournalled={} "
        "packets_unjournalled={}] distinct_shapes={}",
        static_cast<unsigned long long>(g_state.frame_id),
        static_cast<unsigned long long>(g_divergence_census.draws_examined),
        static_cast<unsigned long long>(g_divergence_census.draws_no_snapshot),
        static_cast<unsigned long long>(g_divergence_census.draws_diverged),
        static_cast<unsigned long long>(
            g_divergence_census.vs_float_blocks_diverged),
        static_cast<unsigned long long>(g_divergence_census.vs_float_blocks),
        static_cast<unsigned long long>(
            g_divergence_census.ps_float_blocks_diverged),
        static_cast<unsigned long long>(g_divergence_census.ps_float_blocks),
        static_cast<unsigned long long>(
            g_divergence_census.fetch_slots_diverged),
        static_cast<unsigned long long>(g_divergence_census.fetch_slots),
        static_cast<unsigned long long>(g_divergence_census.cull_diverged),
        static_cast<unsigned long long>(g_divergence_census.cull_compared),
        static_cast<unsigned long long>(
            g_divergence_census.fetch_mirror_slots_diverged),
        static_cast<unsigned long long>(g_divergence_census.fetch_mirror_slots),
        FetchDeviceSourced() ? 1 : 0,
        static_cast<unsigned long long>(
            g_divergence_census.fetch_device_sourced_draws),
        static_cast<unsigned long long>(
            g_divergence_census.fetch_preflush_root_draws),
        static_cast<unsigned long long>(
            g_divergence_census.flow_unjournalled),
        static_cast<unsigned long long>(
            g_divergence_census.packets_unjournalled),
        static_cast<unsigned long long>(g_divergence_census.distinct_shapes));
  }
  if (g_state.frame_id % 512u == 1u) {
    REXLOG_INFO(
        "Refii GTL stats frame={} resources={} (created={} regen_carried={} "
        "fetch_stated={} destroyed={}) "
        "state_events={} draw_packets={} clear_packets={} resolve_packets={} "
        "content_packets={} content_stored={} content_unreadable={} "
        "content_store_mb={} "
        "streamed[done={} events={} unreg={}] "
        "tile_content[packets={} unmatched={} bad_args={} size_unknown={} "
        "unreadable={} off_skip={} outer_skip={} lvl_skip={} "
        "enters[out={} in={}]] "
        "unmatched={} pending_packets={} | vs_from_state={} "
        "vs_backfill_tried={} vs_backfill_filled={} vs_absent={}",
        static_cast<unsigned long long>(g_state.frame_id),
        g_state.resources.size(),
        static_cast<unsigned long long>(g_state.stats.resources_registered),
        static_cast<unsigned long long>(
            g_state.stats.resources_reregistered_generation_carried),
        static_cast<unsigned long long>(
            g_state.stats.resource_fetch_dwords_stated),
        static_cast<unsigned long long>(g_state.stats.resources_destroyed),
        static_cast<unsigned long long>(g_state.stats.state_events),
        static_cast<unsigned long long>(g_state.stats.draw_packets),
        static_cast<unsigned long long>(g_state.stats.clear_packets),
        static_cast<unsigned long long>(g_state.stats.resolve_packets),
        static_cast<unsigned long long>(g_state.stats.content_packets),
        static_cast<unsigned long long>(g_state.stats.content_bytes_stored),
        static_cast<unsigned long long>(g_state.stats.content_unreadable),
        static_cast<unsigned long long>(g_state.content_image_bytes >> 20),
        static_cast<unsigned long long>(g_state.stats.streamed_completions),
        static_cast<unsigned long long>(g_state.stats.streamed_content_events),
        static_cast<unsigned long long>(g_state.stats.streamed_unregistered),
        static_cast<unsigned long long>(g_state.stats.tile_content_packets),
        static_cast<unsigned long long>(g_state.stats.tile_content_unmatched),
        static_cast<unsigned long long>(g_state.stats.tile_content_bad_args),
        static_cast<unsigned long long>(
            g_state.stats.tile_content_size_unknown),
        static_cast<unsigned long long>(g_state.stats.tile_content_unreadable),
        static_cast<unsigned long long>(
            g_state.stats.tile_content_offset_skipped),
        static_cast<unsigned long long>(
            g_state.stats.tile_content_outer_skipped),
        static_cast<unsigned long long>(
            g_state.stats.tile_content_level_skipped),
        static_cast<unsigned long long>(g_state.stats.tile_outer_enters),
        static_cast<unsigned long long>(g_state.stats.tile_inner_enters),
        static_cast<unsigned long long>(g_state.stats.unmatched_set_events),
        g_state.packets.size(),
        static_cast<unsigned long long>(g_state.stats.shader_from_device_state),
        static_cast<unsigned long long>(
            g_state.stats.shader_backfill_attempted),
        static_cast<unsigned long long>(g_state.stats.shader_backfill_filled),
        static_cast<unsigned long long>(g_state.stats.shader_still_absent));
    // The fetch_ingest[...] half of this line was DELETED 2026-08-04. Its eight
    // counters had no increment site anywhere in the tree — IngestBoundTexture
    // Content was removed 2026-07-26 and the stats were "retained deliberately
    // as gap-attribution diagnostics". A field that is structurally incapable
    // of being non-zero attributes nothing; it printed a constant
    // `stored=0 unique=0 unreg=0 unchanged=0 declined[0 0 0 0]` twice per stats
    // dump and read as a measurement. base_alias is LIVE (CopyResourceContent's
    // physical-base fallback) and stays.
    REXLOG_INFO("Refii GTL stats base_alias[hit={} miss={}]",
        static_cast<unsigned long long>(g_state.stats.base_alias_content_hits),
        static_cast<unsigned long long>(g_state.stats.base_alias_content_misses));
    // STATED BUFFER PAYLOAD census. `served` is the number that says the
    // vertex/index feed is alive; `no_header` counts buffers whose header
    // states no allocation yet (expected before the first fill, a defect if it
    // stays high); `reloc` says whether the compactor is actually moving
    // payloads under us.
    REXLOG_INFO("Refii GTL stats buffers[binds={} adopted={}] "
                "payload[served={} no_header={} unreadable={} oversize={}] "
                "reloc[seen={} matched={}]",
        static_cast<unsigned long long>(g_state.stats.buffer_binds_stated),
        static_cast<unsigned long long>(g_state.stats.buffers_adopted_at_bind),
        static_cast<unsigned long long>(g_state.stats.buffer_content_served),
        static_cast<unsigned long long>(g_state.stats.buffer_content_no_header),
        static_cast<unsigned long long>(
            g_state.stats.buffer_content_unreadable),
        static_cast<unsigned long long>(g_state.stats.buffer_content_oversize),
        static_cast<unsigned long long>(g_state.stats.buffer_relocations),
        static_cast<unsigned long long>(
            g_state.stats.buffer_relocations_matched));
    // TENANCY census. `corrected` is the direct size of the stale-identity
    // population: header inits whose ENTER-side fetch read had captured the
    // OUTGOING tenant's dwords, which the GTL used to publish as fact.
    REXLOG_INFO("Refii GTL stats tenancy[bumps={} corrected={}] "
                "stated_tex_kept={} stated_tex_disagrees_fetch={} "
                "settex[nonzero={} slot0={} out_of_range={}] "
                "viewport[shadow={} rejected={}]",
                static_cast<unsigned long long>(
                    g_state.stats.header_init_tenancy_bumps),
                static_cast<unsigned long long>(
                    g_state.stats.header_init_fetch_corrected),
                static_cast<unsigned long long>(
                    g_state.stats.stated_texture_kept_over_empty_fetch),
                static_cast<unsigned long long>(
                    g_state.stats.stated_texture_disagrees_fetch),
                static_cast<unsigned long long>(
                    g_state.stats.set_texture_nonzero_binds),
                static_cast<unsigned long long>(
                    g_state.stats.set_texture_slot0_binds),
                static_cast<unsigned long long>(
                    g_state.stats.set_texture_slot_out_of_range),
                static_cast<unsigned long long>(
                    g_state.stats.viewport_from_device_shadow),
                static_cast<unsigned long long>(
                    g_state.stats.viewport_shadow_rejected));
    LogSelfReadExitCensusLocked();
    // §3b census. unbound_source != 0 => flags&7 named a slot the tracked
    // device state had nothing in (a device-state tracking gap); measured 0
    // over 442 resolves in run 437. unregistered_dest is EXPECTED ~100% and is
    // fine — the destination headers are reused scratch (run 437 saw one header
    // carry three different fetch constants), so Create identity does not
    // exist for them. no_dest_fetch is the one that must stay 0: the fetch
    // constant is the only durable name for the memory being written.
    REXLOG_INFO(
        "Refii GTL resolve stats total={} pending={} unbound_source={} "
        "unregistered_dest={} no_dest_fetch={} offset_events={} "
        "adopted_at_offset={} release_events={} released_destroyed={} "
        "released_survived={} release_bad_shape={} unmapped_symbols={} "
        "creates_dropped={} header_size_queries={} header_unreadable={}",
        static_cast<unsigned long long>(g_state.stats.resolve_packets),
        g_state.resolve_packets.size(),
        static_cast<unsigned long long>(g_state.stats.resolve_unbound_source),
        static_cast<unsigned long long>(g_state.stats.resolve_unregistered_dest),
        static_cast<unsigned long long>(g_state.stats.resolve_no_dest_fetch),
        static_cast<unsigned long long>(
            g_state.stats.offset_base_address_events),
        static_cast<unsigned long long>(
            g_state.stats.resources_adopted_at_offset),
        static_cast<unsigned long long>(g_state.stats.resources_release_events),
        static_cast<unsigned long long>(g_state.stats.resources_destroyed),
        static_cast<unsigned long long>(
            g_state.stats.resources_release_survived),
        static_cast<unsigned long long>(
            g_state.stats.resources_release_bad_shape),
        static_cast<unsigned long long>(g_state.stats.unmapped_symbol_events),
        static_cast<unsigned long long>(
            g_state.stats.creates_dropped_null_object),
        static_cast<unsigned long long>(g_state.stats.header_inits_size_query),
        static_cast<unsigned long long>(
            g_state.stats.header_inits_unreadable_out_pointer));
    // Per-family packet-completeness census: one line per draw-root family,
    // so we can read which families the packet already fully owns (geom+ident)
    // and are therefore observer-removal candidates.
    for (const auto& [symbol, cov] : g_family_coverage) {
      REXLOG_INFO(
          "Refii GTL family coverage sub_{:08X} total={} geom={} streamvb={} "
          "ident={} decl={} tex0={} rt0={}",
          symbol, static_cast<unsigned long long>(cov.total),
          static_cast<unsigned long long>(cov.geom),
          static_cast<unsigned long long>(cov.streamvb),
          static_cast<unsigned long long>(cov.ident),
          static_cast<unsigned long long>(cov.decl),
          static_cast<unsigned long long>(cov.tex0),
          static_cast<unsigned long long>(cov.rt0));
    }
    // Begin-family pin outcome census: splits the geometry-capture losses by
    // exact cause so the fix (making the GTL own BeginVertices draws) targets
    // the dominant one. arm_reached should equal the sub_82205F68/BeginIndexed
    // family totals; drain_ok is the count that actually gets geometry.
    REXLOG_INFO(
        "Refii GTL begin-pin census arm[reached={} rej_depth={} rej_symbol={} "
        "rej_bytes={} ok={}] drain[reached={} unreadable={} blank={} "
        "packet_gone={} ok={}]",
        g_begin_pin_census.arm_reached.load(std::memory_order_relaxed),
        g_begin_pin_census.arm_reject_depth.load(std::memory_order_relaxed),
        g_begin_pin_census.arm_reject_symbol.load(std::memory_order_relaxed),
        g_begin_pin_census.arm_reject_bytes.load(std::memory_order_relaxed),
        g_begin_pin_census.arm_ok.load(std::memory_order_relaxed),
        g_begin_pin_census.drain_reached.load(std::memory_order_relaxed),
        g_begin_pin_census.drain_unreadable.load(std::memory_order_relaxed),
        g_begin_pin_census.drain_blank.load(std::memory_order_relaxed),
        g_begin_pin_census.drain_packet_gone.load(std::memory_order_relaxed),
        g_begin_pin_census.drain_ok.load(std::memory_order_relaxed));
  }
}

std::vector<DrawPacket> DrainDrawPackets() {
  std::lock_guard<std::mutex> lock(g_state.mutex);
  std::vector<DrawPacket> drained;
  drained.swap(g_state.packets);
  return drained;
}

std::vector<ResolvePacket> DrainResolvePackets() {
  std::lock_guard<std::mutex> lock(g_state.mutex);
  std::vector<ResolvePacket> drained;
  drained.swap(g_state.resolve_packets);
  return drained;
}

std::vector<ClearPacket> DrainClearPackets() {
  std::lock_guard<std::mutex> lock(g_state.mutex);
  std::vector<ClearPacket> drained;
  drained.swap(g_state.clear_packets);
  return drained;
}

std::vector<TessellatedPatchDrawPacket> DrainTessellatedPatchDrawPackets() {
  std::lock_guard<std::mutex> lock(g_state.mutex);
  std::vector<TessellatedPatchDrawPacket> drained;
  drained.swap(g_state.tess_packets);
  return drained;
}



namespace {
// Drain packets belonging to COMPLETED guest frames (packet_id < boundary),
// leaving in-progress packets (>= boundary) in place for the next drain. This
// gives the authoritative submission one guest frame at a time instead of a
// pump-frame fraction (the observer command batch was implicitly guest-frame
// aligned; the GTL streams are not, and the pump over-runs the guest ~1.75x).
// boundary == 0 (before the first guest Present) drains everything, as the
// unbounded drains do.
template <typename PacketT>
std::vector<PacketT> DrainPacketsUpToBoundaryLocked(
    std::vector<PacketT>& source, uint64_t boundary) {
  if (boundary == 0) {
    std::vector<PacketT> drained;
    drained.swap(source);
    return drained;
  }
  std::vector<PacketT> drained;
  std::vector<PacketT> kept;
  drained.reserve(source.size());
  for (PacketT& packet : source) {
    if (packet.packet_id < boundary) {
      drained.push_back(std::move(packet));
    } else {
      kept.push_back(std::move(packet));
    }
  }
  source.swap(kept);
  return drained;
}
}  // namespace

std::vector<DrawPacket> DrainDrawPacketsUpTo(uint64_t boundary) {
  std::lock_guard<std::mutex> lock(g_state.mutex);
  return DrainPacketsUpToBoundaryLocked(g_state.packets, boundary);
}

std::vector<ResolvePacket> DrainResolvePacketsUpTo(uint64_t boundary) {
  std::lock_guard<std::mutex> lock(g_state.mutex);
  return DrainPacketsUpToBoundaryLocked(g_state.resolve_packets, boundary);
}

std::vector<ClearPacket> DrainClearPacketsUpTo(uint64_t boundary) {
  std::lock_guard<std::mutex> lock(g_state.mutex);
  return DrainPacketsUpToBoundaryLocked(g_state.clear_packets, boundary);
}

std::vector<TessellatedPatchDrawPacket> DrainTessellatedPatchDrawPacketsUpTo(
    uint64_t boundary) {
  std::lock_guard<std::mutex> lock(g_state.mutex);
  return DrainPacketsUpToBoundaryLocked(g_state.tess_packets, boundary);
}

bool HasPendingPacketsUpTo(uint64_t boundary) {
  std::lock_guard<std::mutex> lock(g_state.mutex);
  const auto has_pending = [boundary](const auto& packets) {
    if (boundary == 0) {
      return !packets.empty();
    }
    for (const auto& packet : packets) {
      if (packet.packet_id < boundary) {
        return true;
      }
    }
    return false;
  };
  return has_pending(g_state.packets) || has_pending(g_state.resolve_packets) ||
         has_pending(g_state.clear_packets) ||
         has_pending(g_state.tess_packets);
}

// DELETED 2026-07-26: IngestBoundTextureContent (+ its FnvHashContentBytes
// helper). It was the §4 "store the bytes the observer captured at each texture
// bind" experiment, REVERTED 2026-07-23 because it regressed rendering against
// the Xenia reference (lost menu/save text), and it has had zero callers ever
// since — hook_observer.cpp:2949 only mentions it in a comment. Nothing else
// ever set `Resource::content_from_fetch_ingest` or `ContentImage::from_ingest`
// to true, so both flags were already permanently false.
//
// THE RESIDUE IS NOW GONE TOO (2026-08-04): both flags, `fetch_content_hash`,
// the always-early-returning `DropFetchIngestedContentLocked` and its two call
// sites, and the eight `fetch_content_*` stats have been deleted.
//
// DO NOT re-add a naive version. The four regressions its guards encoded are the
// reason the observer's per-bind fetch capture is load-bearing and NOT
// deletable: it arbitrates render-target GPU-write vs CPU-overlay lifecycles
// (run 192, GUI text pages), resolve-produced GPU content with no CPU bytes,
// stated unlock/tile writers that own real bytes with a stated layout, and
// time-multiplexed pool bases where content is (object,time)-specific (run 194
// off-by-one). The real_gap for fetch-delivered content stays open BY DESIGN.

// Lock-free body, so callers that ALREADY hold g_state.mutex (the resource
// snapshot feed at the bottom of this file) can serve content without dropping
// and re-taking it. The public entry below is the only thing that locks.
bool CopyResourceContentLocked(uint32_t resource, bool mip_range,
                               std::vector<uint8_t> &out,
                               bool &out_source_linear) {
  const uint64_t key =
      (static_cast<uint64_t>(resource) << 1) | (mip_range ? 1u : 0u);
  auto it = g_state.content_images.find(key);
  if (it != g_state.content_images.end() && !it->second.bytes.empty()) {
    out = it->second.bytes;
    out_source_linear = it->second.source_linear;
    return true;
  }
  // Base-keyed SIBLING fallback, reinstated 2026-08-02 read-only and narrow.
  // The 2026-07-23 removal note below assumed "the observer serves it" — the
  // 07-29 draw-event gate ended that, and the designated alarm fired twice:
  // run 480 (title atlas nulls) and run 521 (`exact title GUI sync snapshot
  // known=0` for base 0x1A0D9000 whose bytes sit under the RETIRED sibling
  // header key). Fable recycles heap headers over a stable physical base; the
  // base is the Xbox-semantic identity, object keys are our bookkeeping. On an
  // object-key miss, serve the bytes stored under a sibling object that names
  // the same base. Gates: same base, size match when the requester's size is
  // known; newest content_generation wins among candidates. Store writers are
  // untouched — this changes reads only.
  auto requested = g_state.resources.find(resource);
  if (requested == g_state.resources.end() ||
      requested->second.base_address == 0) {
    return false;
  }
  const uint32_t base = requested->second.base_address;
  const uint32_t requested_size = requested->second.byte_size;
  const State::ContentImage *best = nullptr;
  uint64_t best_generation = 0;
  for (const auto &entry : g_state.resources) {
    if (entry.first == resource || entry.second.base_address != base) {
      continue;
    }
    const uint64_t sibling_key =
        (static_cast<uint64_t>(entry.first) << 1) | (mip_range ? 1u : 0u);
    auto sibling = g_state.content_images.find(sibling_key);
    if (sibling == g_state.content_images.end() ||
        sibling->second.bytes.empty()) {
      continue;
    }
    if (requested_size != 0 &&
        sibling->second.bytes.size() != requested_size) {
      continue;
    }
    if (best == nullptr || entry.second.content_generation >= best_generation) {
      best = &sibling->second;
      best_generation = entry.second.content_generation;
    }
  }
  if (best == nullptr) {
    // *** LAST TIER: READ THE GUEST'S STATED BASE. ***
    //
    // Some content NEVER passes an unlock root, a tile call, or any other
    // observable event — it is written by RAW CPU STORES straight into the
    // resource's memory. Proven case (IDA 2026-08-04): the tonemap's 512x1
    // R32F tone curve. sub_82A7B9A8 creates it over XPhysicalAlloc and
    // publishes it with XGOffsetResourceAddress; sub_8217E428 then fills it
    // EVERY FRAME with plain float stores (`v57 = v54[3]; v61 = (float*)(v57+8);
    // *(v61-2) = ...`) with NO Lock and NO Unlock anywhere on the path.
    // Contrast the scattering LUTs (sub_82A64988) which DO end in
    // D3D_UnlockResource — exactly why those are visible and this is not.
    //
    // A call-observing backend is structurally blind to this. But the GUEST
    // STATED the base address, so reading it is honouring stated data — the
    // §5 rule — not emulating hardware. It belongs HERE, in the GTL, as the
    // resource's own content: there is no observer in this path and none is
    // wanted.
    //
    // Read through the PHYSICAL VIEW. A raw physical base indexes the low
    // virtual heap, which is "frequently committed and non-zero, so reading it
    // fails silently instead of loudly" (plume_fable_addressing.h). Reading
    // the wrong form is why this content was long believed to be all zeros.
    constexpr uint32_t kMaxStatedBaseContentBytes = 1u << 20;
    uint8_t *guest_base = refii::renderer::FableGuestMemoryBase();
    if (guest_base != nullptr && requested_size != 0 &&
        requested_size <= kMaxStatedBaseContentBytes) {
      const uint32_t view =
          refii::renderer::FableGuestPhysicalViewAddress(base);
      if (refii::renderer::bink::IsReadableGuestByteRange(guest_base, view,
                                                          requested_size)) {
        const uint8_t *src = guest_base + view;
        // Validate before trusting. Freshly XPhysicalAlloc'd, never-written
        // memory reads as a UNIFORM poison fill — measured 0xFFC00000 (-NaN)
        // across all 512 curve entries at startup, before the per-frame fill
        // runs. Uploading that is worse than uploading nothing. Two
        // format-agnostic rejections: all-zero, and every 32-bit word
        // identical (a single repeated value is a fill pattern, never a
        // curve or an image).
        bool any_nonzero = false;
        bool uniform_word = true;
        uint32_t first_word = 0;
        const uint32_t whole_words = requested_size / 4u;
        for (uint32_t i = 0; i < requested_size; ++i) {
          if (src[i] != 0u) {
            any_nonzero = true;
            break;
          }
        }
        for (uint32_t i = 0; i < whole_words; ++i) {
          uint32_t w = 0;
          std::memcpy(&w, src + i * 4u, sizeof(w));
          if (i == 0) {
            first_word = w;
          } else if (w != first_word) {
            uniform_word = false;
            break;
          }
        }
        if (any_nonzero && !(whole_words > 1 && uniform_word)) {
          out.assign(src, src + requested_size);
          // The stated base names the resource's OWN allocation, so the bytes
          // carry whatever layout the resource has — same as an unlock range.
          out_source_linear = false;
          ++g_state.stats.stated_base_content_hits;
          return true;
        }
        ++g_state.stats.stated_base_content_rejected;
      }
    }
    ++g_state.stats.base_alias_content_misses;
    return false;
  }
  ++g_state.stats.base_alias_content_hits;
  out = best->bytes;
  out_source_linear = best->source_linear;
  return true;
}

bool CopyResourceContent(uint32_t resource, bool mip_range,
                         std::vector<uint8_t> &out, bool &out_source_linear) {
  std::lock_guard<std::mutex> lock(g_state.mutex);
  return CopyResourceContentLocked(resource, mip_range, out,
                                   out_source_linear);
}
// NOTE (2026-07-23): a base->object fallback lived here (resolve a physical-base
// query to the registered object owning that base). REMOVED with the fetch-store
// ingest — it served base-keyed content the observer already handles, and it
// only mattered for the ingested pool aliases. [2026-08-02: premise expired,
// fallback reinstated above — see its comment.]

bool CopyResourceContent(uint32_t resource, bool mip_range,
                         std::vector<uint8_t> &out) {
  bool ignored_source_linear = false;
  return CopyResourceContent(resource, mip_range, out, ignored_source_linear);
}


StatedPresentSource GetStatedPresentSource() {
  std::lock_guard<std::mutex> lock(g_state.mutex);
  StatedPresentSource out;
  out.rt0 = g_state.stated_present_rt0;
  out.front_buffer = g_state.stated_present_front_buffer;
  out.front_buffer_base = g_state.stated_present_front_buffer_base;
  out.epoch = g_state.stated_present_epoch;
  out.guest_frame_boundary_packet_id = g_state.guest_frame_boundary_packet_id;
  return out;
}

void SetGuestFramePresentedCallback(GuestFramePresentedCallback callback) {
  g_guest_frame_presented_callback.store(callback, std::memory_order_release);
}

ResourceContentStatus QueryResourceContentStatus(uint32_t resource) {
  ResourceContentStatus status;
  if (resource == 0) {
    return status;
  }
  std::lock_guard<std::mutex> lock(g_state.mutex);
  auto it = g_state.resources.find(resource);
  if (it == g_state.resources.end()) {
    return status;
  }
  status.known = true;
  status.create_symbol = it->second.create_symbol;
  status.create_lr = it->second.create_lr;
  status.content_generation = it->second.content_generation;
  status.gpu_content_generation = it->second.gpu_content_generation;
  status.base_address = it->second.base_address;
  const uint64_t base_key = static_cast<uint64_t>(resource) << 1;
  auto image = g_state.content_images.find(base_key);
  status.has_stored_bytes =
      image != g_state.content_images.end() && !image->second.bytes.empty();
  return status;
}

bool ResourceHasGpuProducedContent(uint32_t resource) {
  if (resource == 0) {
    return false;
  }
  std::lock_guard<std::mutex> lock(g_state.mutex);
  auto it = g_state.resources.find(resource);
  return it != g_state.resources.end() &&
         it->second.gpu_content_generation != 0;
}

ResourceIdentity QueryResourceIdentity(uint32_t guest_address) {
  ResourceIdentity identity;
  std::lock_guard<std::mutex> lock(g_state.mutex);
  const auto it = g_state.resources.find(guest_address);
  if (it == g_state.resources.end()) {
    return identity;
  }
  const Resource &resource = it->second;
  identity.known = true;
  identity.create_serial = resource.create_serial;
  identity.guest_address = resource.guest_address;
  identity.parent_texture = resource.view_parent_texture;
  identity.base_address = resource.base_address;
  identity.mip_address = resource.mip_address;
  identity.byte_size = resource.byte_size;
  identity.width = resource.width;
  identity.height = resource.height;
  identity.format = resource.format;
  identity.content_generation = resource.content_generation;
  identity.gpu_content_generation = resource.gpu_content_generation;
  // Absent until 2026-08-03, so no consumer could tell "same address, different
  // texture" from "same texture, changed content".
  identity.tenancy_generation = resource.tenancy_generation;
  identity.fetch_dwords = resource.fetch_dwords;
  identity.fetch_dwords_stated = resource.fetch_dwords_stated;
  identity.was_render_target = resource.was_render_target;
  return identity;
}

ResourceDirtyExtent ConsumeResourceDirtyExtent(uint32_t guest_address) {
  ResourceDirtyExtent extent;
  std::lock_guard<std::mutex> lock(g_state.mutex);
  const auto it = g_state.resources.find(guest_address);
  if (it == g_state.resources.end()) {
    return extent;
  }
  extent.known = true;
  extent.content_generation = it->second.content_generation;
  extent.base_start = it->second.dirty_base_start;
  extent.base_end = it->second.dirty_base_end;
  extent.mip_start = it->second.dirty_mip_start;
  extent.mip_end = it->second.dirty_mip_end;
  extent.base_allocation = it->second.dirty_base_allocation;
  extent.mip_allocation = it->second.dirty_mip_allocation;
  // Single-shot, mirroring the guest root: D3D::UnlockResource resets the
  // packed range at resource+0x14/+0x18 to the 0xFFFF0000 sentinel after
  // handing it to D3D_FlushCachedMemory, so a stated extent describes exactly
  // one flush. Without this reset, an unlock that bypasses this module's
  // entry capture (Plume-replaced hooks never re-state) hands the same stale
  // extent to every later consumer (run 424).
  it->second.dirty_base_start = 0;
  it->second.dirty_base_end = 0;
  it->second.dirty_mip_start = 0;
  it->second.dirty_mip_end = 0;
  it->second.dirty_base_allocation = 0;
  it->second.dirty_mip_allocation = 0;
  return extent;
}


// Self-read the FULL texture fetch-constant block into the packet (option-c).
// The device holds 32 fetch constants of 6 dwords each at device+0x480+slot*24
// (24 bytes/slot), big-endian. command.constants.fetch (and therefore every
// fetch-based classifier/binder — Bink plane detection, the maze's decoded-plane
// substitution) consumes these 6 dwords per slot; fetch_bases (dword1 only) is
// not enough, which is why GTL video draws never classified as Bink and the
// title background rendered white (runs 214-216). Byteswap to host to match the
// observer's LoadTextureFetchDwords. Sets fetch_dwords_valid so the command
// builder knows to populate constants.fetch. Caller holds g_state.mutex.
void CaptureDeviceFetchConstantsIntoPacketLocked(DrawPacket& packet,
                                                 uint32_t device,
                                                 uint8_t* guest_base) {
  if (device == 0 || guest_base == nullptr) {
    return;
  }
  constexpr uint32_t kDeviceFetchConstantBase = 0x480u;
  constexpr uint32_t kDeviceFetchConstantStride = 24u;  // 6 dwords
  constexpr uint32_t kFetchDwordsPerSlot = 6u;
  bool any = false;
  for (uint32_t slot = 0; slot < 32u && slot < packet.fetch_bases.size();
       ++slot) {
    const uint32_t fetch_addr =
        device + kDeviceFetchConstantBase + slot * kDeviceFetchConstantStride;
    if (!refii::renderer::bink::IsReadableGuestByteRange(
            guest_base, fetch_addr, kDeviceFetchConstantStride)) {
      continue;
    }
    for (uint32_t d = 0; d < kFetchDwordsPerSlot; ++d) {
      const uint32_t value = refii::renderer::bink::LoadGuestU32(
          guest_base, fetch_addr + d * 4u);
      const uint32_t out_index = slot * kFetchDwordsPerSlot + d;
      if (out_index < packet.fetch_dwords.size()) {
        packet.fetch_dwords[out_index] = value;
      }
    }
    // dword1 bits 12..31 is the base address (RB_COPY_DEST_BASE decode).
    const uint32_t base =
        packet.fetch_dwords[slot * kFetchDwordsPerSlot + 1u] & 0xFFFFF000u;
    // PHASE 2 (FETCH CLASS): with the cutover on, a readable texture-fetch slot
    // is AUTHORITATIVE even when it reads zero. The old `if (base != 0)` guard
    // meant an unbound slot silently inherited whatever the
    // OnFetchConstantDispatch mirror last cached for it -- and that mirror was
    // measured 85% wrong (run 284). "Unbound" has to be representable.
    // Vertex-fetch slots (>= 26, device+0x6F0) are 2-dword constants, not
    // 6-dword texture fetches; their "base" decode is meaningless, so they keep
    // the old nonzero-only behaviour rather than gaining a decoded garbage
    // value. An UNREADABLE slot still `continue`s above, which leaves the
    // mirror value in place as the fallback.
    // Slots >= 26 are vertex streams, FULL STOP — never record a base for
    // them. The partition is stated by D3DDevice_UnsetAll (0x82BA23E8):
    // samplers 0..25 (`cmplwi cr6, r30, 0x1A`) and vertex streams 0..15
    // mapped onto constants 26..31 (`cmplwi cr6, r30, 0x10`), growing
    // downward, so slot 31 holds streams 0-2 and `dword1 & 0xFFFFF000` there
    // is half of a stream ADDRESS, not a texture base.
    //
    // The old "keep the nonzero-only behaviour" compromise below still let
    // that through whenever the garbage happened to be nonzero — and nonzero
    // garbage is still garbage. A bound vertex stream is nonzero essentially
    // always, so this fired constantly rather than rarely.
    if (slot >= 26u) {
      continue;
    }
    const bool authoritative_slot =
        FetchDeviceSourced() && slot < packet.fetch_bases.size();
    if (base != 0 || authoritative_slot) {
      packet.fetch_bases[slot] = base;
    }
    any = true;
  }
  if (any) {
    packet.fetch_dwords_valid = true;
  }
}

// Read the live D3D control-packet mode word out of device memory at the
// draw's flush point. IDA (D3DDevice_SetRenderState_CullMode, 0x8220B470):
//   lwz r11, 0x2948(r3) ; rlwimi r4, r11, 0,0,28 ; stw r4, 0x2948(r3)
// i.e. the low 3 bits of the dword at device+0x2948 ARE the Xenos
// PA_SU_SC_MODE_CNTL cull field, and device+0x10 |= 0x40 is its dirty tag.
// This is the value the GPU receives for the draw; every mirror we keep of it
// is a guess by comparison (see DrawPacket::live_mode_control).
void CaptureDeviceModeControlIntoPacketLocked(DrawPacket& packet,
                                              uint32_t device,
                                              uint8_t* guest_base) {
  if (device == 0 || guest_base == nullptr) {
    return;
  }
  constexpr uint32_t kDeviceModeControlOffset = 0x2948u;
  const uint32_t at = device + kDeviceModeControlOffset;
  if (!refii::renderer::bink::IsReadableGuestByteRange(guest_base, at,
                                                       sizeof(uint32_t))) {
    return;
  }
  packet.live_mode_control =
      refii::renderer::bink::LoadGuestU32(guest_base, at);
  packet.live_mode_control_valid = true;
}

// ---------------------------------------------------------------------------
// AUTHORITATIVE EXIT CONSTRUCTION (shared by every draw root).
// ---------------------------------------------------------------------------
//
// WHY THIS EXISTS (verbatim reasoning from the original Begin-family block, do
// not lose it): the draw root applies pending shaders/targets/textures/constants
// from INSIDE its own body (IDA: D3DDevice_BeginVertices -> D3D_SetPending_
// Shaders/_RenderStates/_FetchConstants), so at hook ENTER the device's applied
// fields still hold the PREVIOUS draw's values — or zero on the first. Run 137
// measured the result: 13 of 15 vs-misses were ABSENT (packet.vertex_shader ==
// 0) rather than untracked, and the enter-time backfill could not fix them
// because the value did not exist yet. The observer proves the values ARE
// available by exit: it reads these same device fields from
// ObserveRendererHookExit (PatchDrawEventDeviceBindingsLocked,
// hook_observer.cpp) using the same device pointer. So patch the already-built
// packet here rather than moving packet construction to exit, which would
// disturb packet ordering and the Begin-family ring pin.
//
// The old zero-guard (only fill a field the enter read left at zero) left STALE
// NONZERO values in place, which is exactly why Begin packets could not be
// given authority (run 118 black screen). Now the state IS applied, so
// OVERWRITE each axis from the device object. Only overwrite when the fresh
// read is nonzero, so a failed read never blanks a good value.
//
// STEP 2 (2026-07-26) — GENERALISED FROM THE BEGIN FAMILY TO EVERY DRAW ROOT.
// Per-root IDA verification of the staging prologue, done before extending:
//
//   0x82205F68 D3DDevice_BeginVertices        PROLOGUE PRESENT (inline).
//   0x82217DB8 D3DDevice_BeginIndexedVertices PROLOGUE PRESENT (inline).
//   0x8221C3E8 D3DDevice_DrawVertices         PROLOGUE PRESENT (inline):
//       D3D_SetPending_Shaders, _Predicated, three _RenderStates,
//       _FetchConstants, all before it emits its packet.
//   0x8221DFC0 D3DDevice_DrawIndexedVertices  PROLOGUE PRESENT (inline,
//       decompiled 2026-07-26): drains the five pending masks via
//       sub_8221DE68, then D3D_SetPending_Shaders (mask & 0x1E0000),
//       D3D_SetPending_Predicated, D3D_SetPending_RenderStates x3
//       (0xFFF / 0x1F000 / 0xFFE00000) and D3D_SetPending_FetchConstants,
//       before the ring write. Same shape as 0x8221C3E8.
//       (NOTE: run 132 measured this root as NEVER FIRING — none of its 8
//       static callers run in the observed phase. Its counter reading 0 is
//       therefore expected and is NOT evidence the self-read is broken;
//       cross-check against the family-coverage line for the same symbol.)
//   0x82265CD8 D3DDevice_DrawVerticesUP       PROLOGUE **DELEGATED**, NOT
//       inline. IDA shows the whole body is
//         v9 = D3DDevice_BeginVertices(...); if (v9) { memcpy(...); ... }
//       i.e. it owns no staging of its own — but D3DDevice_BeginVertices,
//       which DOES stage inline, has already run and returned by the time this
//       root exits. So the premise (device state is applied by exit) HOLDS,
//       and the self-read is valid here. It is a *different* justification
//       from the inline roots, hence spelled out rather than assumed.
//   0x8220A3F0 (quad/UP wrapper)              PROLOGUE **DELEGATED**, NOT
//       inline. IDA: Render_FlushDirtyRenderStates() as its first statement,
//       then D3DDevice_BeginIndexedVertices(...) (which stages inline), then
//       two memcpys into the returned rings. Again the state IS applied by
//       exit, so the premise holds. This root ALREADY self-read its fetch
//       block, float banks and mode control at exit (that path is the video
//       plane / tree-billboard fix); step 2 adds the shader and target axes it
//       was missing, using the same helper as everyone else.
//
// Note for both delegated roots: the nested Begin call is itself hooked and
// self-reads ITS OWN packet. That does not cover the wrapper's packet — for
// 0x8220A3F0 the nested Begin packet is suppressed outright
// (t_draw_indexed_up_depth), and for 0x82265CD8 the nested Begin exit clears
// t_last_packet_id before the wrapper's exit runs. That is why the wrapper
// roots stash their own (packet_id, device, guest_base) at enter instead of
// relying on t_last_packet_*.
//
// `drop_stale_tracked_bindings` is TRUE only for the inline-UP roots whose
// textures/vertex buffers do not come from the tracked setters at all (see the
// call sites). Caller holds g_state.mutex.
void ApplyAuthoritativeExitDeviceStateLocked(DrawPacket& packet, uint32_t device,
                                             uint8_t* guest_base,
                                             bool drop_stale_tracked_bindings) {
  if (device == 0 || guest_base == nullptr) {
    ++g_state.stats.self_read_exit_skipped_no_device;
    return;
  }
  constexpr uint32_t kDevicePixelShaderOffset = 0x3194;
  constexpr uint32_t kDeviceVertexShaderOffset = 0x3198;
  constexpr uint32_t kDeviceRenderTargetBaseOffset = 0x3098;
  constexpr uint32_t kDeviceDepthStencilOffset = 0x30A8;
  // ZERO IS A LIVE VALUE, AND A FAILED READ IS NOT ZERO. These two were
  // conflated: load_late returned 0 for both "the guest has this unbound" and
  // "the range was unreadable", which forced the nonzero-only overwrite below
  // and made every DELIBERATE unbind invisible.
  //
  // IDA (2026-07-26) settled what these fields are: 0x3194/0x3198/0x3098/0x30A8
  // are PERSISTENT API-set bindings with exactly one writer each
  // (D3DDevice_SetPixelShader @0x82208C34, SetVertexShader @0x822325B0,
  // SetRenderTarget @0x822869BC, SetDepthStencilSurface @0x822645B0), all
  // unconditional and none of them flush products -- so an exit read is valid
  // whether or not the pending flush ran. And zero is ENGINEERED, not stale:
  // D3D_SetPending_Shaders has a full NULL-PS branch (0x8221B08C-0x8221B14C)
  // that forces depth-only rendering, SetRenderTarget(0,NULL) sizes the pass
  // from depth instead, and Render_BindSingleRenderTarget (0x821FC3A0) NULLs
  // slots 1..3 unconditionally 16x/frame.
  //
  // So: apply whatever the read returns INCLUDING zero, but only when the read
  // actually succeeded. Never substitute a previous binding for a stated unbind.
  struct LateRead {
    uint32_t value = 0;
    bool ok = false;
  };
  const auto load_late = [&](uint32_t offset) -> LateRead {
    const uint32_t at = device + offset;
    if (!refii::renderer::bink::IsReadableGuestByteRange(guest_base, at,
                                                         sizeof(uint32_t))) {
      return {};
    }
    return {refii::renderer::bink::LoadGuestU32(guest_base, at), true};
  };
  // FEED-DEPENDENCE CENSUS (migration step 3, the remaining five feeds).
  //
  // The question that gates deleting OnShaderBindDispatch /
  // OnRenderTargetBindDispatch / OnDepthTargetBindDispatch is NOT "do the two
  // sources agree" -- it is "does this self-read ever FAIL to produce a value
  // and silently fall back on what the feed left in the packet?", because the
  // overwrite below is NONZERO-ONLY. Where the fresh device read returns 0 and
  // the packet already holds a feed-supplied value, that feed is LOAD-BEARING
  // and deleting it would blank the field.
  //
  //   self_read_filled      fresh read non-zero -> feed not needed here
  //   feed_load_bearing     fresh read ZERO but packet non-zero -> DO NOT DELETE
  //   both_absent           neither has a value -> nothing to lose
  //   feed_disagreed        both non-zero and DIFFERENT -> the self-read is
  //                         correcting a stale feed; deleting is an improvement,
  //                         not a risk, but the count says how wrong it was.
  const auto census_field = [&](const LateRead& fresh, uint32_t existing) {
    if (!fresh.ok) {
      // The ONLY case where the feed is genuinely load-bearing: we could not
      // read the device at all. A successful read of 0 is an answer, not a gap.
      ++g_state.stats.exit_feed_load_bearing;
      return;
    }
    ++g_state.stats.exit_self_read_filled;
    if (fresh.value == 0 && existing != 0) {
      // Previously the nonzero-only guard silently kept `existing` here,
      // preserving the PREVIOUS draw's binding across a deliberate unbind.
      ++g_state.stats.exit_stated_unbind_applied;
    } else if (existing != 0 && existing != fresh.value) {
      ++g_state.stats.exit_feed_disagreed;
    } else if (existing == 0 && fresh.value == 0) {
      ++g_state.stats.exit_both_absent;
    }
  };
  // Apply a successful read verbatim -- zero included.
  const auto apply_late = [&](const LateRead& fresh, uint32_t& field) {
    if (fresh.ok) {
      field = fresh.value;
    }
  };
  const LateRead vs = load_late(kDeviceVertexShaderOffset);
  census_field(vs, packet.vertex_shader);
  apply_late(vs, packet.vertex_shader);
  const LateRead ps = load_late(kDevicePixelShaderOffset);
  census_field(ps, packet.pixel_shader);
  apply_late(ps, packet.pixel_shader);
  for (uint32_t rt = 0; rt < kMaxRenderTargets; ++rt) {
    const LateRead rtv = load_late(kDeviceRenderTargetBaseOffset + rt * 4u);
    // Census RT0 only: slots 1..3 are NULLed unconditionally by
    // Render_BindSingleRenderTarget (0x821FC3A0) 16x/frame, so censusing them
    // would swamp the counters with a known non-defect. They are still APPLIED
    // faithfully -- that unconditional NULL is exactly the stated unbind this
    // change exists to stop discarding.
    if (rt == 0) {
      census_field(rtv, packet.render_targets[rt]);
    }
    apply_late(rtv, packet.render_targets[rt]);
  }
  const LateRead ds = load_late(kDeviceDepthStencilOffset);
  census_field(ds, packet.depth_target);
  apply_late(ds, packet.depth_target);
  // *** THE VIEWPORT MUST COME FROM THE DEVICE SHADOW, NOT THE ARGUMENT STRUCT.
  // (2026-08-04, IDA-proven — this is the fabricated-extent root.) ***
  //
  // `sub_821F9F58` (which our kSetViewport root hooks) is only a MARSHALLING
  // SHIM: it reads the D3DVIEWPORT9 at r4 and forwards to the real setter
  // `D3D_SetViewport` 0x821F9D00. All the semantics live in the callee.
  //
  // Five of the EIGHT call sites — including the one `D3DDevice_SetRenderTarget`
  // invokes on every render-target bind (sub_82286CE8 @0x82286D74) — pass the
  // global sentinel `dword_82001AA8`, whose 24 bytes read:
  //     00000000 00000000 0000FFFF 0000FFFF 00000000 3F800000
  //     X=0  Y=0  W=65535  H=65535  MinZ=0.0  MaxZ=1.0
  // They deliberately hand over 65535x65535 and rely on D3D_SetViewport to
  // CLAMP it to the bound target:
  //     821f9dfc  lwz    r10, 0x24(r9)     ; RT0 (dev+0x3098) else DS (dev+0x30A8)
  //     821f9e2c  add    r10, r10, r6      ; right = X + Width
  //     821f9e3c/40                        ; r11 = min(right, rtWidth)
  //     821f9e60  subf.  r11, r6, r11      ; clampedWidth = right - X
  // and only then stores the RESULT as floats:
  //     821f9e80 stfs f31,0x3168  X    821f9e88 stfs f30,0x316C  Y
  //     821f9ec8 stfs f26,0x3170  W    821f9eb4 stfs f27,0x3174  H
  //     821f9ea8 stfs f29,0x3178  MinZ 821f9eb0 stfs f28,0x317C  MaxZ
  //
  // So reading r4 at the shim gives us the UNCLAMPED request — 65535x65535 on
  // the majority of calls — while the value the GPU actually receives is the
  // clamped shadow. Same principle already settled for the fetch constants:
  // the device shadow at draw time is the only authoritative source.
  //
  // Note the guest's clamp decodes `surface+0x24` with EXACTLY our formula
  // (width=(p>>18)+1, height=((p>>3)&0x7FFF)+1) — independently confirmed
  // against the builder `D3D_SetSurfaceHeader` 0x821F5028 (`slwi 18` /
  // `insrwi 15,14`). Our arithmetic was never wrong; we were reading it at the
  // wrong boundary, and off the wrong object.
  {
    constexpr uint32_t kDeviceViewportXOffset = 0x3168u;  // 6 consecutive floats
    const uint32_t at = device + kDeviceViewportXOffset;
    if (refii::renderer::bink::IsReadableGuestByteRange(guest_base, at, 24u)) {
      float fields[6] = {};
      bool sane = true;
      for (uint32_t i = 0; i < 6u; ++i) {
        const uint32_t raw =
            refii::renderer::bink::LoadGuestU32(guest_base, at + i * 4u);
        std::memcpy(&fields[i], &raw, sizeof(float));
        if (!std::isfinite(fields[i])) {
          sane = false;
        }
      }
      // A zero-extent shadow means SetViewport early-outed with no bound target
      // (821f9d44) or the clamp collapsed to nothing; keep the tracked value
      // rather than publishing an empty viewport.
      if (sane && fields[2] > 0.0f && fields[3] > 0.0f) {
        packet.viewport[0] = static_cast<uint32_t>(std::lround(fields[0]));
        packet.viewport[1] = static_cast<uint32_t>(std::lround(fields[1]));
        packet.viewport[2] = static_cast<uint32_t>(std::lround(fields[2]));
        packet.viewport[3] = static_cast<uint32_t>(std::lround(fields[3]));
        std::memcpy(&packet.viewport[4], &fields[4], sizeof(float));
        std::memcpy(&packet.viewport[5], &fields[5], sizeof(float));
        ++g_state.stats.viewport_from_device_shadow;
      } else {
        ++g_state.stats.viewport_shadow_rejected;
      }
    }
  }
  // Fetch-constant textures (IDA hook_observer ObservePendingFetchConstants +
  // BeginVertices staging): Fable binds textures for lazy-dispatch draws through
  // the device fetch-constant block, NOT SetTexture. Capture the FULL
  // 6-dword-per-slot fetch block so command.constants.fetch is populated and
  // every fetch-based classifier/binder works (see the helper). Read all 32
  // slots straight from the device so the packet owns its textures without an
  // observer feed.
  CaptureDeviceFetchConstantsIntoPacketLocked(packet, device, guest_base);
  // Float constant banks (c-registers): the enter read caught the previous
  // draw's banks. Re-read fresh from the device now that the draw's flush has
  // run. Same base registers the observer uses (vs=120, ps=376), each register
  // 16 bytes, big-endian in guest memory.
  constexpr uint32_t kFloatConstantDwords = 1024u;
  constexpr uint32_t kVertexBankOffset = 120u * 16u;
  constexpr uint32_t kPixelBankOffset = 376u * 16u;
  const auto refresh_bank = [&](uint32_t bank_offset, std::vector<uint32_t>& out,
                                bool pixel_bank) {
    const uint32_t at = device + bank_offset;
    const uint32_t bytes = kFloatConstantDwords * 4u;
    if (!refii::renderer::bink::IsReadableGuestByteRange(guest_base, at,
                                                         bytes)) {
      return;
    }
    const uint8_t* block = guest_base + at +
                           refii::renderer::bink::GuestPhysicalHostOffset(at);
    out.resize(kFloatConstantDwords);
    for (uint32_t i = 0; i < kFloatConstantDwords; ++i) {
      uint32_t raw = 0;
      std::memcpy(&raw, block + i * 4u, sizeof(raw));
      out[i] = __builtin_bswap32(raw);
    }
    // DIVERGENCE CENSUS (feed-7 / OnShaderConstantDispatch deletion
    // prerequisite, 2026-08-02). The device bank just read is post-flush and
    // carrier-complete; the fed mirror (g_state.device.*_float_bank) holds
    // only what the observer funnel saw (SetShaderConstantF roots, immediate
    // ALU uploads, shader literal tables, deferred PM4 SET_CONSTANT decode).
    // Compared here on the RAW dwords, BEFORE the tonemap sanitize below, so
    // the +inf rewrite can never read as divergence. Both feeds are raw
    // guest dwords, so equality is exact. A diverged dword means the mirror
    // is stale or missing a carrier at draw time — the direction the WVP-zero
    // defect proved possible (VMX memcpy lands in the device block only).
    // Feed deletion requires banks_diverged == 0 on a verified run.
    {
      const bool fed_valid = pixel_bank ? g_state.device.ps_float_bank_valid
                                        : g_state.device.vs_float_bank_valid;
      if (!fed_valid) {
        ++g_state.stats.constant_census_banks_unfed;
      } else {
        const auto& fed = pixel_bank ? g_state.device.ps_float_bank
                                     : g_state.device.vs_float_bank;
        uint32_t diverged_dwords = 0;
        uint32_t first_diverged_dword = 0xFFFFFFFFu;
        for (uint32_t i = 0; i < kFloatConstantDwords; ++i) {
          if (out[i] != fed[i]) {
            if (diverged_dwords == 0) {
              first_diverged_dword = i;
            }
            ++diverged_dwords;
          }
        }
        ++g_state.stats.constant_census_banks_compared;
        if (diverged_dwords != 0) {
          ++g_state.stats.constant_census_banks_diverged;
          g_state.stats.constant_census_dwords_diverged += diverged_dwords;
          if (g_state.stats.constant_census_first_diverged_register ==
              0xFFFFFFFFu) {
            g_state.stats.constant_census_first_diverged_register =
                first_diverged_dword / 4u;
            g_state.stats.constant_census_first_diverged_bank =
                pixel_bank ? 1u : 0u;
          }
        }
      }
    }
    // Tonemap +inf sanitize (replicates the observer's
    // SanitizeFableShaderFloatConstantDword, hook_observer.cpp — read GTL-side
    // here so option-(c) self-read matches the fed path, NOT patched in the
    // observer). Fable's title/menu tonemap PIXEL shader uses +inf as a colour-
    // sensitivity threshold at c47.x (pixel dword 47*4=188); Vulkan turns inf*0
    // into NaN, which packs to a FULLSCREEN WHITE / wash (run 213 overexposed
    // title once the GTL owned the draws). A huge finite value keeps the
    // threshold behaviour without the NaN.
    constexpr uint32_t kTitleToneMapColourSensitivityDword = 47u * 4u;
    if (pixel_bank && kTitleToneMapColourSensitivityDword < out.size() &&
        out[kTitleToneMapColourSensitivityDword] == 0x7F800000u) {
      const float finite = 1.0e20f;
      std::memcpy(&out[kTitleToneMapColourSensitivityDword], &finite,
                  sizeof(finite));
    }
  };
  refresh_bank(kVertexBankOffset, packet.vs_float_constants,
               /*pixel_bank=*/false);
  refresh_bank(kPixelBankOffset, packet.ps_float_constants,
               /*pixel_bank=*/true);
  CaptureDeviceModeControlIntoPacketLocked(packet, device, guest_base);
  if (drop_stale_tracked_bindings) {
    // Drop STALE TRACKED CLAIMS. packet.textures came from
    // g_state.device.textures (fed by the kSetTexture root) and
    // packet.vertex_buffers from the stream roots — neither is how the
    // inline-UP families bind. Begin-family draws are inline-UP and bind their
    // textures through the fetch constants read above, so any tracked value
    // here is the PREVIOUS draw's leftover. Left in place it is worse than
    // nothing: identity matching uses nonzero_agrees(), so a stale-nonzero
    // packet claim that disagrees with the command's live binding REFUSES the
    // match outright (run 206: every composite quad stayed observer-sourced and
    // the fetch->texture fill never ran). Zero means "no claim", which lets the
    // match proceed on the axes this packet actually owns and lets the command
    // builder resolve textures from fetch_bases.
    //
    // NOT applied to the stream-fed roots (kDrawPrimitive /
    // kDrawIndexedPrimitive): those draw from real bound vertex buffers, so
    // packet.vertex_buffers is a genuine claim there and clearing it would
    // destroy the geometry binding rather than a stale leftover.
    // *** CORRECTED 2026-08-04 — THE BLACK-GAMEPLAY DEFECT. ***
    //
    // The premise above ("Begin-family draws bind their textures through the
    // fetch constants, so any tracked value here is the PREVIOUS draw's
    // leftover") is FALSE for the tonemap composite, and the tonemap composite
    // is what the whole frame ends up depending on.
    //
    // `sub_821E5DB8` is a Begin-family draw — a fullscreen RECTLIST via
    // BeginVertices(dev, 8, 3, 8) — and it binds its scene colour through a
    // real `bl D3DDevice_SetTexture` at 0x821E6254. Measured live (run 561, at
    // the guest's own bind site): the shader declares the scene texture at
    // SAMPLER 0, and the pointer it hands over is a genuine texture
    // (0x40133D50 / 0x404A4F10 / 0x404B2C10, holder 0x8331A978, agreeing with
    // dword_8331A97C). We cleared it, fell back to the fetch block, decoded
    // nothing (`fetch_base=0`), and bound the 1x1 null — 64 `UNSTATED texture
    // slot bound null` warnings per run, EVERY ONE of them slot 0.
    //
    // This is the architecture rule inverted: the stated API call must beat the
    // decoded hardware descriptor, and here we discarded the stated call in
    // favour of a descriptor that stated nothing.
    //
    // ATTEMPT 1 (run 562) GATED ON THE WRONG SIGNAL — recorded so it is not
    // retried. It kept the stated texture only where `fetch_bases[slot] == 0`.
    // That measured `stated_tex_kept=0`: the RAW decode
    // (`fetch_dwords[slot*6+1] & 0xFFFFF000`) is non-zero for slot 0, so we
    // still cleared — while the IDENTITY decode the command actually uses
    // (`FableTextureFetchIdentityBaseFromDwords`, which additionally requires
    // `type == kTexture`) rejects the same block and yields nothing. Two
    // different decodes of one register, disagreeing, with the stated pointer
    // thrown away in between.
    //
    // THE STATED CALL WINS. `g_state.device.textures[slot]` is not a "leftover"
    // — D3D9 binding state is PERSISTENT, so it is the live binding until the
    // guest changes it, and D3DDevice_SetTexture is ring-complete
    // (0x821B6EE0 writes dev+0x480+24*Sampler inline, no deferral). There is no
    // second opinion to weigh it against: the fetch block is a hardware MIRROR
    // of this same call. So keep every stated slot, and let the fetch path fill
    // only the slots the guest never stated.
    //
    // The original change existed for run 206, where a non-zero packet claim
    // that disagreed with the command's live binding refused the identity match
    // via nonzero_agrees(). If that recurs it now shows up as a non-zero
    // `stated_tex_disagrees_fetch` rather than as silently-dropped world
    // textures — measure it, do not pre-emptively clear for it.
    for (uint32_t slot = 0; slot < packet.textures.size(); ++slot) {
      if (packet.textures[slot] == 0) {
        continue;
      }
      ++g_state.stats.stated_texture_kept_over_empty_fetch;
      if (slot < packet.fetch_bases.size() && packet.fetch_bases[slot] != 0) {
        ++g_state.stats.stated_texture_disagrees_fetch;
      }
    }
    packet.vertex_buffers = {};
  }
}

// ---------------------------------------------------------------------------
// Hardware-tessellator patch capture (CAPTURE-ONLY).
// ---------------------------------------------------------------------------

// Read `count` consecutive big-endian dwords from the device register shadow.
// Returns false (and leaves the array zeroed) when the range is unreadable, so
// the packet never carries invented state.
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

// *** GUEST GPU FENCE RETIREMENT — the 4 fps fix, IDA-derived 2026-07-27 ***
//
// Every real D3D wait funnels through D3D_CBlocker_Check (0x82BA1FA8) and ends
// up spinning on the 96-byte GPU writeback block B = *(u32*)(pDevice+0x2A90).
// On hardware the CP writes B as it executes two EVENT_WRITE_SHD packets that
// D3D_CDevice_SetFence (0x82298630) appends. Our port never consumes the PM4
// ring, so B never advances, so:
//   D3D_CDevice_BlockOnFence            0x82242668  (KickOff, StartNewSegment,
//       and D3D_LockResource -- EVERY lock of a busy resource)
//   D3D_CDevice_BlockOnSecondaryPosition 0x82206400  (EVERY ring allocation)
// spin FOREVER without yielding. The watchdog escape in CBlocker_Check rearms
// whenever B[0] changes and is not wall-clock, so it never fires either. That
// is the guest being PARKED at ~245 ms/frame while the GPU does 0.277 ms.
//
// WHY HERE AND NOT AT SetFence. SetFence only APPENDS the fence packets to a
// segment that has not been submitted yet -- KickOffSegment calls SetFence and
// THEN QueueIndirectBuffer. Retiring at SetFence reports completion of work we
// have not even received, unconditionally one submission early. That matters
// because D3D_LockResource blocks on a resource's fence BEFORE handing the CPU
// a pointer: retire early and the guest overwrites vertex/index/texture memory
// our renderer is still reading. Intermittent geometry/texture corruption is a
// far worse failure than the stall. KickOffSegment's EXIT is the first moment
// the segment is genuinely handed over, and our draws were already captured by
// the draw-root hooks before it.
//
// VALUES ARE GUEST-STATED, NOT RECONSTRUCTED. SetFence saves its own CPU-side
// shadow of the fence point: dev+0x33B8 = m_pRing as it was BEFORE the fence
// packet, dev+0x33BC = lap. B[4] is exactly (lap & 3) | that ring cursor -- the
// conservative "consumed up to just before this fence" value hardware reports.
// The issued fence is dev+0x2A9C - 2, because SetFence emits the pre-increment
// value and then advances by 2 (fences run 3,5,7,...).
//
// ORDER AND ENDIANNESS ARE LOAD-BEARING. Write B+4 before B+0, matching the
// packet order, so a waiter never sees a retired fence with a stale cursor.
// Both are stored big-endian (the packets carry endian field 2 = 8-in-32);
// StoreGuestU32 byte-swaps, which is what we want. There is no seqlock on B and
// all guest reads are single aligned lwz, so an aligned 32-bit store cannot
// tear.
//
// NOT COVERED HERE (still outstanding, do not assume this fixes them):
//   * B+0x3C ring RPTR write-back -- D3D_CDevice_BlockOnPrimaryRange spins on
//     it independently.
//   * D3DDevice_Swap's swap-ack loop (0x82BA3B78) waits on device counters, not
//     on B; only D3D_SwapCallback releases it.
//   * Occlusion queries: D3D_QueryGetData passes the fence test and then reads
//     the query buffer, treating 0xFFFFFEED as pending.
void RetireGuestGpuFenceLocked(uint32_t device, uint8_t *guest_base) {
  if (device == 0 || guest_base == nullptr) {
    return;
  }
  constexpr uint32_t kDeviceRingCursor = 0x0030u;        // m_pRing
  constexpr uint32_t kDeviceWritebackBlockPtr = 0x2A90u; // B
  constexpr uint32_t kDeviceNextFence = 0x2A9Cu;
  constexpr uint32_t kDeviceFencePointRing = 0x33B8u;
  constexpr uint32_t kDeviceFencePointLap = 0x33BCu;
  constexpr uint32_t kDeviceRingLap = 0x3A48u;
  const auto readable = [&](uint32_t at, uint32_t n) {
    return refii::renderer::bink::IsReadableGuestByteRange(guest_base, at, n);
  };
  // DIAGNOSTIC 2026-07-28 (temporary, remove with the kickoff-exit probe).
  // Each early return below is a silent no-retire; run 364 could not tell them
  // apart from "never called".
  const auto bail = [&](const char *why) {
    static std::atomic<uint32_t> bails{0};
    const uint32_t n = bails.fetch_add(1, std::memory_order_relaxed) + 1u;
    if (n <= 24u || (n % 4096u) == 0u) {
      REXLOG_INFO("Refii GTL fence retire BAILED #{} why={} device=0x{:08X}", n,
                  why, device);
    }
  };
  if (!readable(device + kDeviceRingCursor, 4u) ||
      !readable(device + kDeviceWritebackBlockPtr, 4u) ||
      !readable(device + kDeviceNextFence, 4u) ||
      !readable(device + kDeviceFencePointRing, 8u) ||
      !readable(device + kDeviceRingLap, 4u)) {
    bail("device-fields-unreadable");
    return;
  }
  const uint32_t block =
      refii::renderer::bink::LoadGuestU32(guest_base,
                                          device + kDeviceWritebackBlockPtr);
  if (block == 0 || !readable(block, 8u)) {
    bail(block == 0 ? "writeback-block-null" : "writeback-block-unreadable");
    return;
  }

  // SetFence's ENTIRE bookkeeping, reproduced (0x82298630, verified
  // instruction-by-instruction). Only the two EVENT_WRITE_SHD packets are
  // omitted: Plume owns submission, so nothing consumes them.
  //
  // This has to be reproduced rather than merely observed. On hardware the
  // fence counter dev+0x2A9C is advanced ONLY by SetFence, and SetFence has
  // exactly two callers -- KickOffSegment and StopWorkerQueue. Under Plume
  // KickOffSegment is a suppressed root whose replacement
  // (EmulateFableKickOffSegment) never issues a fence, so the counter is frozen
  // for the life of the process. An earlier version of this function read
  // dev+0x33B8/0x33BC and derived the issued fence as dev+0x2A9C - 2; every one
  // of those fields is written BY SetFence, so all three read stale boot values
  // and the retirement was a no-op against a counter that never moved.
  //
  // Advancing the counter here is what keeps the guest's own invariant true:
  // KickOff (0x821E8EC0) waits on `*(dev+0x2A9C) - 2` as its definition of
  // "everything issued has completed", and BlockOnFence blocks while
  // `(cur - target) < (cur - B[0])`. With B[0] = the pre-increment fence and
  // cur = that + 2, a target stamped before this kickoff yields 2 < 2 = false
  // (passes) while one stamped after yields 0 < 2 = true (still waits, until the
  // next kickoff retires it). Freezing the counter instead would make every
  // stamp equal `cur` and every wait unsatisfiable -- the 4fps park.
  const uint32_t ring_cursor =
      refii::renderer::bink::LoadGuestU32(guest_base, device + kDeviceRingCursor);
  const uint32_t ring_lap =
      refii::renderer::bink::LoadGuestU32(guest_base, device + kDeviceRingLap);
  const uint32_t issued_fence =
      refii::renderer::bink::LoadGuestU32(guest_base, device + kDeviceNextFence);
  const uint32_t position_word = (ring_lap & 3u) | ring_cursor;

  // The fence-point shadow, exactly as SetFence records it.
  refii::renderer::bink::StoreGuestU32(guest_base,
                                       device + kDeviceFencePointRing,
                                       ring_cursor);
  refii::renderer::bink::StoreGuestU32(guest_base, device + kDeviceFencePointLap,
                                       ring_lap);

  const uint32_t already =
      refii::renderer::bink::LoadGuestU32(guest_base, block);
  // B+4 before B+0, matching the packet order the two EVENT_WRITE_SHD writes
  // would have reached memory in.
  refii::renderer::bink::StoreGuestU32(guest_base, block + 4u, position_word);
  refii::renderer::bink::StoreGuestU32(guest_base, block, issued_fence);
  refii::renderer::bink::StoreGuestU32(guest_base, device + kDeviceNextFence,
                                       issued_fence + 2u);
  ++g_state.stats.guest_fences_retired;


  static uint32_t logged_fence_retire = 0;
  if (logged_fence_retire < 16u) {
    REXLOG_INFO("Refii GTL guest fence retired #{} device=0x{:08X} "
                "block=0x{:08X} fence 0x{:08X} -> 0x{:08X} next=0x{:08X} "
                "pos=0x{:08X}",
                ++logged_fence_retire, device, block, already, issued_fence,
                issued_fence + 2u, position_word);
  }
}

// Emulate Render_FrameCompletionCallback (0x82B90160) — the GPU ring callback
// that our port never fires, which freezes ALL rendered content.
//
// THE CHAIN (every link IDA-verified, addresses are the quoted instructions):
//   0x82B6F274  every Present does D3D_InsertCallback(dev, .., 1,
//               Render_FrameCompletionCallback, &g_D3DDevice_FrameSync)
//   0x82B901AC  the callback stores mftb            -> record+0x00
//   0x82B901D0  and ++completion counter            -> record+0x10
//   0x82B901E0  then publishes the record index     -> 0x83497124
//   0x82B90118  Render_GetFrameSyncState reads them -> struct+0x18 / +0x28
//   0x82B6FD4C  sub_82B6FCE8 predicted present =
//               struct+0x18 + (interval/refresh)*(presentCount - counter + 1)
//   0x82388480  renderTime = present - 1/15 - 2*(interval/refresh) - 1/refresh
//   0x822DFE80  sub_822DFE08 gate: lastUpdate[slot] >= renderTime -> NO UPDATE
//
// Never firing it leaves record+0x00 = 0, so struct+0x18 evaluates to
// (0 - dbl_834970A0)/dbl_834970A8 — a large NEGATIVE number. Then:
//   * every one of the 4 command-queue slots takes the `bge -> return` at
//     0x822DFE80 (lastUpdate starts at 0, and 0 >= negative), so slot content
//     is never rebuilt — the frame is a replay of whatever was there;
//   * the scene swap at 0x82388560 (`renderTime >= publishedTimestamp`) is
//     negative >= positive, so a newly published scene is NEVER adopted;
//   * Present's own throttle at 0x82B6F3A8 reads the stuck counter, computes
//     0 - N, takes `ble 1`, and skips its wait entirely.
// Which is exactly the observed symptom: Present free-running at full rate over
// frozen content. It is not a framerate bug, it is a stopped clock.
//
// Fired at Present EXIT: on hardware the callback runs when the GPU retires the
// packet Present inserted, so the completion being reported is this Present's.
void AdvanceGuestFrameCompletionLocked(uint8_t *guest_base) {
  if (guest_base == nullptr) {
    return;
  }
  const auto readable = [&](uint32_t at, uint32_t n) {
    return refii::renderer::bink::IsReadableGuestByteRange(guest_base, at, n);
  };
  if (!readable(kFrameSyncPublishedIndex, 4u) ||
      !readable(kFrameSyncRecordBase, 2u * kFrameSyncRecordStride) ||
      !readable(kGuestPrevPresentTimebase, 8u) ||
      !readable(kGuestLastPresentTimebase, 8u) ||
      !readable(kGuestPresentFrameCount, 4u)) {
    return;
  }

  // Same source the recompiler emits for `mftb` (REX_QUERY_TIMEBASE ->
  // Clock::QueryGuestTickCount), so this timestamp shares the units and origin
  // that dbl_834970A0 / dbl_834970A8 were captured against. Using a host clock
  // of any other rate here would silently skew every derived time.
  const uint64_t now_ticks = rex::chrono::Clock::QueryGuestTickCount();
  const uint32_t now_hi = static_cast<uint32_t>(now_ticks >> 32u);
  const uint32_t now_lo = static_cast<uint32_t>(now_ticks & 0xFFFFFFFFu);

  // PART 1 — what the suppressed guest Present body would have done.
  const uint32_t last_hi =
      refii::renderer::bink::LoadGuestU32(guest_base, kGuestLastPresentTimebase);
  const uint32_t last_lo = refii::renderer::bink::LoadGuestU32(
      guest_base, kGuestLastPresentTimebase + 4u);
  refii::renderer::bink::StoreGuestU32(guest_base, kGuestPrevPresentTimebase,
                                       last_hi);
  refii::renderer::bink::StoreGuestU32(guest_base,
                                       kGuestPrevPresentTimebase + 4u, last_lo);
  refii::renderer::bink::StoreGuestU32(guest_base, kGuestLastPresentTimebase,
                                       now_hi);
  refii::renderer::bink::StoreGuestU32(guest_base,
                                       kGuestLastPresentTimebase + 4u, now_lo);
  const uint32_t present_count =
      refii::renderer::bink::LoadGuestU32(guest_base, kGuestPresentFrameCount) +
      1u;
  refii::renderer::bink::StoreGuestU32(guest_base, kGuestPresentFrameCount,
                                       present_count);

  // Double-buffered records: build the INACTIVE one, publish it last. The reader
  // (0x82B90048) takes every field from whichever index is published, so writing
  // the live record in place would let it observe a half-updated pair.
  const uint32_t index =
      refii::renderer::bink::LoadGuestU32(guest_base, kFrameSyncPublishedIndex) &
      1u;
  const uint32_t next_index = index ^ 1u;
  const uint32_t current = kFrameSyncRecordBase + index * kFrameSyncRecordStride;
  const uint32_t target =
      kFrameSyncRecordBase + next_index * kFrameSyncRecordStride;

  // record[new].prev = record[cur].timestamp   (0x82B901A4)
  const uint32_t prev_hi = refii::renderer::bink::LoadGuestU32(
      guest_base, current + kFrameSyncRecordTimestamp);
  const uint32_t prev_lo = refii::renderer::bink::LoadGuestU32(
      guest_base, current + kFrameSyncRecordTimestamp + 4u);
  refii::renderer::bink::StoreGuestU32(
      guest_base, target + kFrameSyncRecordPrevTimestamp, prev_hi);
  refii::renderer::bink::StoreGuestU32(
      guest_base, target + kFrameSyncRecordPrevTimestamp + 4u, prev_lo);

  // record[new].timestamp = mftb   (0x82B901AC)
  refii::renderer::bink::StoreGuestU32(
      guest_base, target + kFrameSyncRecordTimestamp, now_hi);
  refii::renderer::bink::StoreGuestU32(
      guest_base, target + kFrameSyncRecordTimestamp + 4u, now_lo);

  // record[new].counter   (0x82B901D0). Set EQUAL to g_PresentFrameCount rather
  // than blindly incremented: sub_82B6FCE8 computes
  // (g_PresentFrameCount - counter + 1) @0x82B6FD24, so the two must stay in
  // lockstep. Incrementing this one alone while the suppressed guest body left
  // g_PresentFrameCount at zero would drive that term INCREASINGLY NEGATIVE and
  // push the predicted present time backwards — worse than the stopped clock it
  // is meant to fix.
  const uint32_t counter = refii::renderer::bink::LoadGuestU32(
      guest_base, current + kFrameSyncRecordCounter);
  refii::renderer::bink::StoreGuestU32(
      guest_base, target + kFrameSyncRecordCounter, present_count);

  // Publish LAST (0x82B901E0).
  refii::renderer::bink::StoreGuestU32(guest_base, kFrameSyncPublishedIndex,
                                       next_index);
  ++g_state.stats.guest_frame_completions;

  static uint32_t logged_frame_completion = 0;
  if (logged_frame_completion < 8u) {
    REXLOG_INFO("Refii GTL guest frame completion #{} record={} counter {} -> {} "
                "present_count={} ts=0x{:08X}{:08X}",
                ++logged_frame_completion, next_index, counter, present_count,
                present_count, now_hi, now_lo);
  }
}

void OnHookExit(const char *symbol, const RendererHookArgs &args) {
  OnHookExit(symbol, SymbolAddress(symbol), args);
}

void OnHookExit(const char *symbol, uint32_t symbol_address,
                const RendererHookArgs &args) {
  if (symbol_address == kPresent) {
    uint8_t *present_base = t_pending_present_base;
    t_pending_present_base = nullptr;
    std::lock_guard<std::mutex> lock(g_state.mutex);
    AdvanceGuestFrameCompletionLocked(present_base);
    return;
  }
  if (symbol_address == kKickOffSegment || symbol_address == kKickOff) {
    const uint32_t device = t_pending_kickoff_device;
    uint8_t *kickoff_base = t_pending_kickoff_base;
    t_pending_kickoff_device = 0;
    t_pending_kickoff_base = nullptr;
    // DIAGNOSTIC 2026-07-28 (temporary). Run 364 showed the fence-retirement
    // log never printed across a 137s run that spent 107s in the title at
    // ~4fps, while the title phase issued ZERO PM4 submits. Two candidate
    // mechanisms were checked and BOTH disproved: the Plume replacement path
    // does call gtl::OnHookExit (renderer_runtime.cpp:12232, before the
    // present-boundary early return), and OnHookEnter is delivered via
    // OnRendererGuestHookEnter. So we do not yet know whether this exit is
    // reached at all in the title phase, or is reached with device==0.
    // This distinguishes those two cases in one run. Remove once answered.
    {
      static std::atomic<uint32_t> kickoff_exits{0};
      const uint32_t n = kickoff_exits.fetch_add(1, std::memory_order_relaxed) + 1u;
      if (n <= 24u || (n % 4096u) == 0u) {
        REXLOG_INFO("Refii GTL kickoff exit #{} symbol=0x{:08X} device=0x{:08X} "
                    "base={}",
                    n, symbol_address, device,
                    kickoff_base != nullptr ? "ok" : "null");
      }
    }
    std::lock_guard<std::mutex> lock(g_state.mutex);
    RetireGuestGpuFenceLocked(device, kickoff_base);
    return;
  }
  // Header roots: the 52-byte write lands inside the body (memcpy 0x821FBB64),
  // so the header's own fetch block is only this call's truth at EXIT. This is
  // also the IDA-proven tenancy point. kSetTextureHeaderInline is absent on
  // purpose — see RegisterHeaderInitLocked's arming note.
  if (symbol_address == kSetBaseTextureHeader ||
      symbol_address == kXGSetTextureHeader ||
      symbol_address == kXGSetLineTextureHeader ||
      symbol_address == kXGSetCubeTextureHeader) {
    std::lock_guard<std::mutex> lock(g_state.mutex);
    CompleteHeaderInitFetchReadLocked(symbol_address);
    return;
  }
  if (IsFableGtlSceneSubmitScope(symbol_address)) {
    std::lock_guard<std::mutex> lock(g_state.mutex);
    // Saturating: an unmatched exit (enter missed because SymbolAddress
    // rejected it, or hook registration order) must not underflow into a
    // permanent nonzero state that mislabels every later draw as kSceneObject.
    if (g_state.scene_submit_depth != 0) {
      --g_state.scene_submit_depth;
    }
  }
  // Tessellated-patch emitters: the exit is where the VGT topology block and
  // the emitted PM4 dwords become this draw's truth (see
  // PendingTessellatedPatchCapture). Unconditional on the stashed symbol, not
  // on the return value, so a path that returned early still closes the stash.
  if (symbol_address == kEmitTessellatedIndexedPackets ||
      symbol_address == kEmitTessellatedPrimitivePackets) {
    std::lock_guard<std::mutex> lock(g_state.mutex);
    CompleteTessellatedPatchCaptureLocked(args);
    return;
  }
  // Close the tile nesting scope. This MUST run on every exit of the symbol,
  // including paths that returned early on entry — a leaked depth would
  // suppress every later direct XGRAPHICS_TileSurface call for the life of the
  // thread, which reads as "the guest stopped tiling" rather than as a bug.
  // Saturating decrement so an unmatched exit cannot underflow into that same
  // permanent-suppression state.
  if (symbol_address == kTileTextureLevel) {
    t_in_tile_texture_level = false;
  }
  // kStreamedLoadCancel is deliberately NOT in this condition: it is a cancel,
  // and its exit leaves the current texture-set untouched.
  //
  // kStreamedResidencyPoll (0x82A7B148) is the poll AND the publish point --
  // it makes pending become current at 0x82A7B230 before returning, so reading
  // +68 AT EXIT correctly names the newly published set.
  //
  // KNOWN AMBIGUITY, do not paper over it: the FAILURE branch also returns 1.
  // It releases the pending set, installs a 16x16 dummy via sub_82A7A7A8, and
  // leaves self[17] untouched -- so a non-zero return alone does not mean the
  // stream succeeded. The resource lookup below is what saves us: on failure
  // the walk lands on the unchanged previous set, so the worst case is a
  // redundant generation bump on a resource whose bytes really are still there,
  // never a bump naming the wrong texture.
  if (symbol_address == kStreamedResidencyPoll) {
    const uint32_t request = t_pending_stream_request;
    uint8_t *stream_base = t_pending_stream_base;
    t_pending_stream_request = 0;
    t_pending_stream_base = nullptr;
    // Non-zero return = a stream-in actually completed. Zero is a poll that
    // did nothing, and treating those as content events would bump generations
    // for resources whose bytes never changed.
    if (args.r3 != 0 && request != 0 && stream_base != nullptr) {
      std::lock_guard<std::mutex> lock(g_state.mutex);
      ++g_state.stats.streamed_completions;
      // Identity by stated structure, not by search: the request names its
      // result object, and the result names up to two entries each holding the
      // D3DTexture that received the stream.
      const uint32_t result_object = refii::renderer::bink::LoadGuestU32(
          stream_base, request + 68u);
      if (refii::renderer::bink::IsReadableGuestByteRange(stream_base,
                                                          result_object, 56u)) {
        for (const uint32_t entry_offset : {40u, 48u}) {
          const uint32_t entry = refii::renderer::bink::LoadGuestU32(
              stream_base, result_object + entry_offset);
          if (!refii::renderer::bink::IsReadableGuestByteRange(stream_base,
                                                               entry, 8u)) {
            continue;
          }
          const uint32_t resource =
              refii::renderer::bink::LoadGuestU32(stream_base, entry + 4u);
          if (resource == 0) {
            continue;
          }
          auto it = g_state.resources.find(resource);
          if (it == g_state.resources.end()) {
            ++g_state.stats.streamed_unregistered;
            continue;
          }
          // Generation only, for now. The bytes landed at the resource's own
          // base_address, but the streamed EXTENT is not stated by this root —
          // deriving it from the fetch constant is the next step, and storing a
          // guessed length here would repeat the tile writer's worst bug.
          ++it->second.content_generation;
          MarkResourceChangedLocked(it->first);
          ++g_state.stats.streamed_content_events;
          // Cap 512, not 24: this log is the ONLY source of the streamed
          // resource set that gets intersected with the content gap, and at 24
          // the intersection was computed from a 24-entry sample against 130
          // real events — it could show the populations touch but never size
          // the overlap. Same lesson as the tile-MISS and GAP caps.
          static uint32_t logged_streamed = 0;
          if (logged_streamed < 512u) {
            REXLOG_INFO(
                "Refii GTL streamed completion #{} symbol=0x{:08X} "
                "request=0x{:08X} result=0x{:08X} resource=0x{:08X} "
                "base=0x{:08X} gen={}",
                ++logged_streamed, symbol_address, request, result_object,
                resource, it->second.base_address,
                static_cast<unsigned long long>(it->second.content_generation));
          }
        }
      }
    }
  }
  // Release: r3 at exit is the NEW refcount. Erase only at 0 — that is the only
  // case where D3DResource_Release actually destroys the object. Must run ahead
  // of the create-pairing early-return below.
  if (symbol_address == kDestructResource) {
    const uint32_t object = t_pending_release_object;
    t_pending_release_object = 0;
    // Exit r3 is the NEW refcount, and that holds under Plume too: the
    // suppressed path emulates the real thing — it loads the count from the
    // guest object at +4, decrements, stores it back and returns it
    // (renderer_runtime.cpp EmulateFableResourceLifetimeInternal). The observer
    // uses this same predicate (hook_observer.cpp: sub_821FC320 -> `if (result
    // != 0) return;` before erasing), which is independent confirmation.
    //
    // FALSE-ZERO GUARD: the emulator's bad-shape branch sets handled=true and
    // result=0 when r3 is not a plausible object address. That is a rejection,
    // NOT a destroy, so r3==0 there must not be read as "refcount hit zero".
    // Require the object to look like a guest heap pointer before erasing — the
    // same gate the emulator itself applies.
    // All of these counters are under the lock: they are read as correctness
    // signals, and unlocked increments race (the GTL is entered from >=3 threads
    // concurrently — observed in run 442).
    std::lock_guard<std::mutex> lock(g_state.mutex);
    if (object != 0) {
      ++g_state.stats.resources_release_events;
    }
    if (object != 0 && !IsLikelyGuestObjectAddress(object)) {
      ++g_state.stats.resources_release_bad_shape;
      return;
    }
    if (object != 0 && args.r3 == 0) {
      if (g_state.resources.erase(object) != 0) {
        ++g_state.stats.resources_destroyed;
        MarkResourceDestroyedLocked(object);
      }
    } else if (object != 0) {
      ++g_state.stats.resources_release_survived;
    }
    return;
  }
  if (symbol_address == kGetSurfaceLevel ||
      symbol_address == kGetCubeMapSurface) {
    const PendingViewIdentity pending = t_pending_view_identity;
    t_pending_view_identity = {};
    // r3 at exit is the returned view object.
    const uint32_t view_object = args.r3;
    if (view_object == 0 || pending.parent_texture == 0 ||
        !IsLikelyGuestObjectAddress(view_object)) {
      return;
    }
    std::lock_guard<std::mutex> lock(g_state.mutex);
    Resource &view = g_state.resources[view_object];
    if (view.guest_address == 0) {
      view.kind = ResourceKind::kSurface;
      view.guest_address = view_object;
      view.create_serial = g_state.next_create_serial++;
      ++g_state.stats.resources_registered;
    }
    // Verbatim from the call — not recovered from any address.
    view.view_parent_texture = pending.parent_texture;
    view.view_face = pending.face;
    view.view_level = pending.level;
    view.view_identity_stated = true;
    static std::unordered_set<uint64_t> logged_view_identities;
    static uint32_t logged_views = 0;
    const uint64_t key = (static_cast<uint64_t>(pending.parent_texture) << 32) |
                         ((pending.face & 0xFFFFu) << 16) |
                         (pending.level & 0xFFFFu);
    if (logged_views < 64u && logged_view_identities.insert(key).second) {
      REXLOG_INFO(
          "Refii GTL view identity #{} view=0x{:08X} parent=0x{:08X} face={} "
          "level={}",
          ++logged_views, view_object, pending.parent_texture, pending.face,
          pending.level);
    }
    return;
  }
  if (symbol_address == kDrawIndexedUserPrimitives) {
    if (t_draw_indexed_up_depth != 0) {
      --t_draw_indexed_up_depth;
    }
    // VIDEO PLANE fetch self-read (option-c for the Bink compose root). IDA
    // sub_8220A3F0 has NO device arg — it uses the device singleton
    // g_D3DDevice_PresentModule at 0x83360364 — so the arg-based fetch feed left
    // packet.fetch_bases[0..2] = 0 (run 215 DIAG: known=1, fetch0/1/2=0), and
    // BuildDirectCommandFromGtlDrawPacketLocked's plane-fill bound nothing ->
    // the video sampled the undecoded plane -> WHITE title background. Read the
    // plane bases straight from the device fetch-constant block now, at EXIT,
    // after the wrapper's Render_FlushDirtyRenderStates has applied them. Same
    // decode as ObservePendingFetchConstantsLocked: device+0x480+slot*24, base
    // = dword1(+4) bits 12..31. The command builder then resolves each base to
    // the plane object the Bink upload decoded into.
    // (2026-07-25 tree-billboard trace: symbol_match/guest_base_valid were
    // proven 1/1 for every sampled packet — that guard is not the problem,
    // removed the noisy per-draw log for it. The remaining question is what
    // the shader-pair-gated CAPTURE DIAG below shows for a non-Bink pair.)
    if (t_last_packet_id != 0 &&
        t_last_packet_symbol == kDrawIndexedUserPrimitives &&
        t_last_packet_guest_base != nullptr) {
      constexpr uint32_t kDevicePresentModuleSingleton = 0x83360364u;
      uint32_t device = 0;
      if (refii::renderer::bink::IsReadableGuestByteRange(
              t_last_packet_guest_base, kDevicePresentModuleSingleton,
              sizeof(uint32_t))) {
        device = refii::renderer::bink::LoadGuestU32(
            t_last_packet_guest_base, kDevicePresentModuleSingleton);
      }
      if (device != 0) {
        std::lock_guard<std::mutex> lock(g_state.mutex);
        for (auto packet_it = g_state.packets.rbegin();
             packet_it != g_state.packets.rend(); ++packet_it) {
          if (packet_it->packet_id != t_last_packet_id) {
            continue;
          }
          // Float constant banks POST-FLUSH (2026-07-25): this root was the
          // ONLY draw root missing the re-read the Begin family has always
          // done (see the kBeginVertices/kBeginIndexedVertices exit block:
          // "the enter read caught the PREVIOUS draw's banks. Re-read fresh
          // from the device now that the draw's flush has run"). Without it
          // the packet keeps its enter-time copy of the global PM4-fed bank,
          // whose c0-c3 (WorldViewProjection) this content never writes via
          // PM4 -- it writes them with a direct VMX memcpy (sub_82214E30)
          // that no PM4 decode can see. Result: c0-c3 read as zero, the
          // vertex shader's clip position collapses to (0,0,0,0), nothing
          // rasterizes, and the whole family renders invisibly (the
          // tree-billboard ps=none class). Proven by the working/broken
          // delta: 0156_vshader_gui_text_3d and 0071_vshader_ocean_water
          // come through the Begin roots and get correct constants; this
          // root did not. Refreshing the PACKET bank here is exactly what
          // the Begin family already survives -- distinct from the reverted
          // run-258 attempt, which overrode the COMMAND bank using an
          // ENTER-time (pre-flush, therefore stale) read and whitescreened
          // every other consumer of this shared root.
          //
          // STEP 2 (2026-07-26): the fetch / float-bank / mode-control reads
          // this arm used to spell out inline are now the SHARED helper, which
          // also brings the shader and render/depth-target axes this root was
          // missing. IDA (sub_8220A3F0) shows no inline staging prologue —
          // Render_FlushDirtyRenderStates() then D3DDevice_BeginIndexedVertices
          // (which stages inline) — so by this exit the applied device fields
          // are this draw's, exactly as for the Begin family. The enter-time
          // read could never have had them: this root's r3 is a VERTEX COUNT,
          // not a device, so BuildDrawPacketLocked's arg-based backfill had no
          // usable device pointer at all and the shader axes came from the
          // (setter-fed, rarely-called) g_state.device mirror.
          //
          // drop_stale_tracked_bindings stays FALSE here, preserving this
          // root's existing behaviour: the run-215/tree-billboard work depends
          // on packet.textures surviving into
          // BuildDirectCommandFromGtlDrawPacketLocked's plane-fill, and this
          // arm has never cleared them.
          ApplyAuthoritativeExitDeviceStateLocked(
              *packet_it, device, t_last_packet_guest_base,
              /*drop_stale_tracked_bindings=*/false);
          ++g_state.stats.self_read_exit_draw_indexed_user_primitives;
          // Gate on vs/ps CHANGE, not a raw counter: Bink redraws every pump
          // frame with one fixed shader pair and exhausted a flat 40-line cap
          // in 2.4s of startup video, before the run ever reached a menu/
          // character-select frame where tree billboards (a DIFFERENT shader
          // pair) actually draw. Logging on transition guarantees the first
          // occurrence of every distinct shader pair gets captured regardless
          // of how many repeats of an earlier one preceded it.
          static uint32_t logged_quad_capture_diag = 0;
          static uint32_t last_logged_quad_vs = 0;
          static uint32_t last_logged_quad_ps = 0;
          const bool shader_pair_changed =
              packet_it->vertex_shader != last_logged_quad_vs ||
              packet_it->pixel_shader != last_logged_quad_ps;
          if (shader_pair_changed && logged_quad_capture_diag < 200u) {
            last_logged_quad_vs = packet_it->vertex_shader;
            last_logged_quad_ps = packet_it->pixel_shader;
            REXLOG_INFO(
                "Refii GTL quad-exit CAPTURE DIAG #{} packet_id={} "
                "device=0x{:08X} valid={} fetch0=0x{:08X} fetch13=0x{:08X} "
                "fetch14=0x{:08X} fetch15=0x{:08X} vs=0x{:08X} ps=0x{:08X}",
                ++logged_quad_capture_diag,
                static_cast<unsigned long long>(t_last_packet_id), device,
                packet_it->fetch_dwords_valid ? 1 : 0,
                packet_it->fetch_bases[0], packet_it->fetch_bases[13],
                packet_it->fetch_bases[14], packet_it->fetch_bases[15],
                packet_it->vertex_shader, packet_it->pixel_shader);
          }
          break;
        }
      } else {
        {
          std::lock_guard<std::mutex> lock(g_state.mutex);
          ++g_state.stats.self_read_exit_skipped_no_device;
        }
        static uint32_t logged_quad_device_zero_diag = 0;
        if (logged_quad_device_zero_diag < 40u) {
          REXLOG_INFO(
              "Refii GTL quad-exit DEVICE-ZERO DIAG #{} last_id={}",
              ++logged_quad_device_zero_diag,
              static_cast<unsigned long long>(t_last_packet_id));
        }
      }
    } else {
      std::lock_guard<std::mutex> lock(g_state.mutex);
      ++g_state.stats.self_read_exit_skipped_no_packet;
    }
    return;
  }
  // AUTHORITATIVE EXIT SELF-READ for the three device-argument draw roots
  // (step 2, 2026-07-26). Same construction the Begin family has always used,
  // now generalised: at ENTER each of these packets captured the PREVIOUS
  // draw's shaders/targets/fetch block/float banks, because the root stages its
  // own state before emitting. See ApplyAuthoritativeExitDeviceStateLocked for
  // the per-root IDA verification of that staging prologue (0x8221C3E8 and
  // 0x8221DFC0 stage inline; 0x82265CD8 delegates to D3DDevice_BeginVertices,
  // which has returned by this point).
  //
  // This does NOT touch enter-time behaviour and does not remove any observer
  // feed — the counters it bumps are the falsifier that has to read > 0 for
  // every packet-producing root BEFORE OnFetchConstantDispatch /
  // OnShaderConstantDispatch / OnRenderTargetBindDispatch /
  // OnDepthTargetBindDispatch / OnShaderBindDispatch can be deleted.
  if (symbol_address == kDrawPrimitive ||
      symbol_address == kDrawIndexedPrimitive ||
      symbol_address == kDrawPrimitiveUP) {
    const PendingExitSelfRead pending = TakePendingExitSelfRead(symbol_address);
    std::lock_guard<std::mutex> lock(g_state.mutex);
    if (pending.packet_id == 0) {
      ++g_state.stats.self_read_exit_skipped_no_packet;
      return;
    }
    if (pending.device == 0 || pending.guest_base == nullptr) {
      ++g_state.stats.self_read_exit_skipped_no_device;
      return;
    }
    for (auto packet_it = g_state.packets.rbegin();
         packet_it != g_state.packets.rend(); ++packet_it) {
      if (packet_it->packet_id != pending.packet_id) {
        continue;
      }
      // drop_stale_tracked_bindings is FALSE for kDrawPrimitive /
      // kDrawIndexedPrimitive: those draw from REAL bound vertex/index buffers,
      // so packet.vertex_buffers is a genuine claim, not the inline-UP families'
      // stale leftover. kDrawPrimitiveUP is inline-UP but its geometry comes
      // through up_vertex_data rather than packet.vertex_buffers, and its
      // textures have never been cleared here — leaving it FALSE keeps this a
      // pure state-freshness change with no binding-policy change riding along.
      ApplyAuthoritativeExitDeviceStateLocked(
          *packet_it, pending.device, pending.guest_base,
          /*drop_stale_tracked_bindings=*/false);
      if (symbol_address == kDrawPrimitive) {
        ++g_state.stats.self_read_exit_draw_primitive;
      } else if (symbol_address == kDrawIndexedPrimitive) {
        ++g_state.stats.self_read_exit_draw_indexed_primitive;
      } else {
        ++g_state.stats.self_read_exit_draw_primitive_up;
      }
      return;
    }
    // The packet was already drained to the pump before this exit ran.
    ++g_state.stats.self_read_exit_skipped_no_packet;
    return;
  }
  // Standalone Begin-family exits return the ring write pointer in r3; the
  // payload lands there before this thread's next D3D call. Stash for the
  // next-enter pin (only when this thread's LAST packet came from this very
  // Begin call — nested allocations are already covered by the wrapper).
  if (symbol_address == kBeginVertices && t_begin_vertices_depth != 0) {
    --t_begin_vertices_depth;
  }
  if (symbol_address == kBeginVertices ||
      symbol_address == kBeginIndexedVertices) {
    // Split the arm-gate rejects so the drain census below can be read against
    // a known arm-attempt total.
    g_begin_pin_census.arm_reached.fetch_add(1, std::memory_order_relaxed);
    if (t_draw_indexed_up_depth != 0) {
      g_begin_pin_census.arm_reject_depth.fetch_add(1,
                                                    std::memory_order_relaxed);
    } else if (t_last_packet_symbol != symbol_address ||
               t_last_packet_id == 0) {
      g_begin_pin_census.arm_reject_symbol.fetch_add(1,
                                                     std::memory_order_relaxed);
    }
  }
  if ((symbol_address == kBeginVertices ||
       symbol_address == kBeginIndexedVertices) &&
      t_draw_indexed_up_depth == 0 && t_last_packet_symbol == symbol_address &&
      t_last_packet_id != 0) {
    constexpr uint64_t kMaxInlineGeometryBytes = 1u << 20;
    std::lock_guard<std::mutex> lock(g_state.mutex);
    for (auto packet_it = g_state.packets.rbegin();
         packet_it != g_state.packets.rend(); ++packet_it) {
      if (packet_it->packet_id != t_last_packet_id) {
        continue;
      }
      // NOTE: the `vs_late_bound` counter that used to be incremented here was
      // removed 2026-07-26. It was written for a shader gap that never existed
      // — vs_absent equalled resolve_packets exactly, i.e. the exec shadow was
      // grading resolves by draw criteria (artifact fixed at its source in
      // fable_direct_renderer.cpp). The late-bind below is NOT that diagnostic
      // and IS load-bearing; see AUTHORITATIVE EXIT CONSTRUCTION further down.
      //
      // Late-bind the staged state this packet could not have had at ENTER.
      //
      // The draw root applies pending shaders/targets from inside its own body
      // (D3DDevice_BeginVertices calls D3D_SetPending_Shaders), so at hook
      // enter the device's applied fields still hold the PREVIOUS draw's
      // values — or zero on the first. Run 137 measured the result: 13 of 15
      // vs-misses were ABSENT (packet.vertex_shader == 0) rather than
      // untracked, and the enter-time backfill could not fix them because the
      // value did not exist yet.
      //
      // The observer proves the values ARE available by exit: it reads the
      // same device fields from ObserveRendererHookExit
      // (PatchDrawEventDeviceBindingsLocked, hook_observer.cpp) using this
      // same args.r3-derived pointer. So patch here rather than moving packet
      // construction to exit, which would disturb packet ordering and the
      // Begin-family pin drain below.
      //
      // AUTHORITATIVE EXIT CONSTRUCTION. Was inline here (Begin family only)
      // until 2026-07-26; now the SHARED helper every draw root runs, with the
      // full reasoning (and the per-root IDA prologue verification) at its
      // definition. Behaviour for this family is byte-for-byte what it was:
      // same device offsets (vs 0x3198 / ps 0x3194 / rt 0x3098 / ds 0x30A8, all
      // 32 fetch slots, both float banks, mode control), same
      // IsReadableGuestByteRange guard, same nonzero-only overwrite rule, same
      // stale-tracked-binding drop.
      ++g_state.stats.self_read_exit_begin_arm_entries;
      if (t_last_packet_guest_base != nullptr && t_last_packet_device != 0) {
        ApplyAuthoritativeExitDeviceStateLocked(
            *packet_it, t_last_packet_device, t_last_packet_guest_base,
            /*drop_stale_tracked_bindings=*/true);
        if (symbol_address == kBeginVertices) {
          ++g_state.stats.self_read_exit_begin_vertices;
        } else {
          ++g_state.stats.self_read_exit_begin_indexed_vertices;
        }
      } else {
        ++g_state.stats.self_read_exit_skipped_no_device;
      }
      const uint64_t bytes = uint64_t(packet_it->primitive_count) *
                             uint64_t(packet_it->up_vertex_stride);
      // WHERE THE RING POINTER LIVES IS ROOT-SPECIFIC (corrected 2026-07-25).
      //   kBeginVertices (0x82205F68) RETURNS the vertex ring write pointer in
      //     r3 — its `return result` is the D3D_CDevice_BeginRingAlloc value,
      //     and 0 on failure. `args.r3 != 0` is the right success test there.
      //   kBeginIndexedVertices (0x82217DB8) returns an HRESULT in r3: 0 on
      //     SUCCESS, 0x8007000E when either allocation fails. Its two ring
      //     pointers come back through out-params (*ppVertexData at
      //     caller_sp+0x54, *ppIndexData in r10). Reading r3 as an address here
      //     was doubly wrong — on the success path r3 == 0 so the pin could
      //     never arm, and on the failure path it would have armed on an
      //     HRESULT treated as a guest pointer. It stayed invisible only
      //     because the shared arg map also left bytes == 0.
      uint32_t vertex_ring = 0;
      uint32_t index_ring = 0;
      if (symbol_address == kBeginVertices) {
        vertex_ring = args.r3;
      } else if (args.r3 == 0) {
        // S_OK: both out-params hold real ring pointers. OnHookExit carries no
        // guest_base, so use the one stashed when the packet was built (same
        // pointer the staged-state re-reads above use).
        const auto load_out_param = [&](uint32_t at) -> uint32_t {
          return (at != 0 && t_last_packet_guest_base != nullptr &&
                  refii::renderer::bink::IsReadableGuestByteRange(
                      t_last_packet_guest_base, at, sizeof(uint32_t)))
                     ? refii::renderer::bink::LoadGuestU32(
                           t_last_packet_guest_base, at)
                     : 0u;
        };
        vertex_ring = load_out_param(t_begin_indexed_vertex_out_address);
        index_ring = load_out_param(t_begin_indexed_index_out_address);
      }
      t_begin_indexed_vertex_out_address = 0;
      t_begin_indexed_index_out_address = 0;
      // Index payload size from the root's OWN math: IndexCount elements of
      // 4 bytes when (IndexDataFormat & 4) selects 32-bit, else 2. Both roots
      // are covered — index_format/index_count stay 0 for kBeginVertices, so
      // index_bytes is 0 there and no index pin is armed.
      const uint32_t index_element_bytes =
          (packet_it->index_format & 4u) != 0 ? 4u : 2u;
      const uint64_t index_bytes =
          uint64_t(packet_it->index_count) * uint64_t(index_element_bytes);
      if (vertex_ring != 0 && bytes != 0 && bytes <= kMaxInlineGeometryBytes) {
        t_pending_begin_pin = {};
        t_pending_begin_pin.packet_id = t_last_packet_id;
        t_pending_begin_pin.ring_pointer = vertex_ring;
        t_pending_begin_pin.byte_size = static_cast<uint32_t>(bytes);
        t_pending_begin_pin.vertex_count = packet_it->primitive_count;
        if (index_ring != 0 && index_bytes != 0 &&
            index_bytes <= kMaxInlineGeometryBytes) {
          t_pending_begin_pin.index_pointer = index_ring;
          t_pending_begin_pin.index_byte_size =
              static_cast<uint32_t>(index_bytes);
        }
        g_begin_pin_census.arm_ok.fetch_add(1, std::memory_order_relaxed);
      } else {
        // No ring pointer (allocation failed, or the indexed root's out-param
        // was unreadable), or count*stride == 0 / a >1MB outlier.
        g_begin_pin_census.arm_reject_bytes.fetch_add(
            1, std::memory_order_relaxed);
      }
      break;
    }
    t_last_packet_id = 0;
    t_last_packet_symbol = 0;
    return;
  }
  if (symbol_address == 0 ||
      t_pending_create.symbol_address != symbol_address) {
    return;
  }
  const PendingCreate pending = t_pending_create;
  t_pending_create = {};

  // OBJECT RESOLUTION, corrected 2026-07-27 from IDA. The old rule was
  // "exit r3 if it looks like an object, else fall back to ENTRY r3", and that
  // fallback was wrong for every root here:
  //
  //  * CreateVertexBuffer 0x82B8B820 and CreateIndexBuffer 0x82B8D7A8 are NOT
  //    the XDK exports. They are Fable wrappers that write the object to an
  //    OUT-DESCRIPTOR passed in r3 (`stw r31, 0(r30)`), and return a constant
  //    1 / a leftover D3DRESOURCETYPE. Exit r3 therefore ALWAYS failed the
  //    range test and the fallback registered the DESCRIPTOR POINTER as the
  //    buffer -- an interior pointer into an unrelated game struct. Broken on
  //    100% of calls since it was written. The object is `*(u32*)entry_r3`,
  //    and NULL there is the only failure signal (the return value carries
  //    none).
  //  * GetSurfaceLevel / GetCubeMapSurface hold the PARENT texture in entry
  //    r3, so on an allocation failure the fallback registered the parent as
  //    its own child surface -- a texture<->surface identity collision, which
  //    is exactly the shape that produces self-sampling and wrong-binding.
  //  * CreateVertexShader / CreatePixelShader hold the bytecode BLOB in entry
  //    r3, and CreateTexture / CreateSurface hold `Width`.
  //
  // None of these ever carries the object in entry r3, so the fallback is
  // deleted rather than narrowed. (The device-pointer hazard I suspected does
  // NOT exist: this D3D build has an implicit global device and no root takes
  // pDevice in r3.)
  uint32_t object = 0;
  ResourceKind create_kind = CreateKindForSymbol(symbol_address);
  if (IsFableShaderConstructor(symbol_address)) {
    // Fable's shader constructors return the 12-byte WRAPPER, and the pointer
    // draws bind is the D3D shader object at wrapper+8. Resolve and validate it
    // from its stated Common&7 stage tag; see
    // ResolveFableShaderObjectFromWrapper. The stage read off the object wins
    // over the one implied by the symbol -- both agree in every path IDA shows,
    // and if they ever disagree the object is the one the guest binds.
    const FableShaderObject resolved =
        ResolveFableShaderObjectFromWrapper(pending.guest_base, args.r3);
    object = resolved.object;
    if (resolved.kind != ResourceKind::kUnknown) {
      create_kind = resolved.kind;
    }
  } else if (symbol_address == kCreateVertexBuffer ||
      symbol_address == kCreateIndexBuffer) {
    if (pending.guest_base != nullptr && pending.r3 != 0 &&
        refii::renderer::bink::IsReadableGuestByteRange(pending.guest_base,
                                                        pending.r3, 4u)) {
      object = refii::renderer::bink::LoadGuestU32(pending.guest_base,
                                                   pending.r3);
    }
    if (!IsLikelyGuestObjectAddress(object)) {
      object = 0;
    } else {
      // *** KEY BUFFERS ON THE D3D HEADER, NOT THE WRAPPER BASE. ***
      // (IDA 2026-08-04; this was a 100% identity miss for every buffer.)
      //
      // The factories write the WRAPPER base to their out-descriptor
      // (`stw r31, 0(r30)` at 0x82B8B8BC), but nothing downstream ever names
      // that address again. Every bind site loads it and adds 8 before calling
      // D3D:
      //   Render_BindVertexStream  @0x82220C28  lwz r9,0(r4) / addi r5,r9,8
      //                                         -> D3DDevice_SetStreamSource
      //   Render_BindGeometryStreams@0x82B3A768 lwz r11,0(r11) / addi r4,r11,8
      //                                         -> D3DDevice_SetIndices
      //   (the unlock caller @0x82B3A824 does the same for D3D_UnlockResource)
      // and obj+8 is exactly what XGSet{Vertex,Index}BufferHeader was given.
      //
      // So the registry was keyed on an address the draw path never mentions:
      // every FindDirectResourceLocked(stream.buffer) missed, every unlock fell
      // into unmatched_set_events, and the buffer materialization the consumer
      // does off a found resource never ran. Registering the header closes all
      // three at once, and it is also the relocation-stable key -- the payload
      // moves, the wrapper's +4 changes, the header address does not.
      object += 8u;
    }
  } else if (IsLikelyGuestObjectAddress(args.r3)) {
    object = args.r3;
  }
  std::lock_guard<std::mutex> lock(g_state.mutex);
  RegisterCreateLocked(create_kind, object, pending);
}

}  // namespace refii::gtl

// ===========================================================================
// RESOURCE SNAPSHOT FEED — the GTL's replacement for the deleted observer's
// GetFableDirectResourceSnapshotsForAddresses family.
// ===========================================================================
//
// WHY THIS LIVES HERE. hook_observer.cpp was deleted on 2026-08-04 and its
// inert stub returned {} from every one of these, which starved the direct
// renderer: no snapshots -> no tracked resources -> DirectVertexShaderHashLocked
// returns 0 -> `missing_pipeline` -> nothing renders. The feed is rebuilt on the
// GTL's OWN registry (g_state.resources / g_state.content_images), which is fed
// by the guest's stated Create / header-init / unlock / tile / resolve roots.
// There is no observer behind it and none is wanted.
//
// STATED DATA ONLY (§5). Every field below either comes from a call argument
// the guest passed, from a header the guest's own factory wrote, or from bytes
// a stated write root delivered. Nothing here decodes a Xenos register to
// recover something an API call already said plainly. Where the GTL genuinely
// has no source for a field it is LEFT AT ITS DEFAULT with a comment saying so
// — an honest zero, not an invented value.
//
// KNOWN GAPS, stated rather than papered over:
//   * texture.normalized_fetch_dwords  — the GTL captures the HEADER-stated
//     fetch block (raw_fetch_dwords). "Normalized" is the observer's bind-time
//     view of the same constant; the consumer already prefers raw when
//     normalized is all-zero (PreferredTextureFetchDwords), so leaving it zero
//     selects the stated block. This is the correct answer, not a stub.
//   * texture.upload_hash / buffer.upload_hash — the GTL tracks content by
//     GENERATION, not by content hash. The consumer's upload cache falls back
//     to upload_generation when the hash is zero, so change detection still
//     works; re-hashing every texture per sync would cost more than it saves.
//   * buffer.gpu_address / buffer.header_address — CLOSED 2026-08-04. The
//     premise ("no Create root states the buffer's GPU data pointer") was
//     correct and is exactly why the create call is the wrong place to ask:
//     buffers are created EMPTY and their payload RELOCATES. The answer comes
//     from the D3D header the guest's own bind sites name, re-read per
//     snapshot; see the kVertexBuffer/kIndexBuffer arm below.
//   * surface.render_target_info / depth_stencil_info — these are Xenos
//     RB_*_INFO register words. The GTL never sees them; it sees the STATED
//     D3DFORMAT from D3DDevice_CreateSurface, which is reported in d3d_format
//     (the consumer's own preferred source, metadata line 341).
//   * surface.byte_size — no root states a surface's allocation size. Zero,
//     rather than a width*height*4 guess that is wrong for every non-32bpp
//     format.
//   * content_channel — the GTL does not tag channels. Bink planes arrive
//     through DrainBinkPlaneSnapshotsForPlume, a separate feed.
//   * last_upload_capture_site / residency_capture_seen — §4 census
//     attribution, diagnostic-only in the consumer, and meaningless without
//     the observer's capture sites. Left null/false.

namespace refii::renderer {
namespace {

using refii::gtl::Resource;
using refii::gtl::ResourceKind;

// The GTL's registry kind, translated to the consumer's enum.
//
// kDepthSurface is NOT inferred from a format field — the consumer takes the
// kind verbatim and never re-derives it. A surface becomes kDepthSurface only
// because D3DDevice_SetDepthStencilSurface NAMED it (Resource::was_depth_target).
FableGuestResourceKind PublicResourceKind(const Resource &resource) {
  switch (resource.kind) {
    case ResourceKind::kTexture:
      return FableGuestResourceKind::kTexture;
    case ResourceKind::kSurface:
      return resource.was_depth_target ? FableGuestResourceKind::kDepthSurface
                                       : FableGuestResourceKind::kSurface;
    case ResourceKind::kVertexBuffer:
      return FableGuestResourceKind::kVertexBuffer;
    case ResourceKind::kIndexBuffer:
      return FableGuestResourceKind::kIndexBuffer;
    case ResourceKind::kVertexShader:
      return FableGuestResourceKind::kVertexShader;
    case ResourceKind::kPixelShader:
      return FableGuestResourceKind::kPixelShader;
    case ResourceKind::kVertexDeclaration:
      return FableGuestResourceKind::kVertexDeclaration;
    case ResourceKind::kUnknown:
    default:
      return FableGuestResourceKind::kUnknown;
  }
}

// Surface extent packing. Bit layout verified against the decoder the consumer
// actually runs — DecodeFableSurfaceExtentFromPacked (plume_surface_shape.cpp):
// bits 31..18 hold width-1, bits 17..3 hold height-1 (15 bits), depth is always
// 1. The clamps match the in-tree encoder in fable_direct_renderer.cpp.
uint32_t PackSurfaceExtent(uint32_t width, uint32_t height) {
  if (width == 0 || height == 0) {
    return 0;  // 0 means "no extent stated"; the consumer defaults to 1x1.
  }
  width = width > 0x4000u ? 0x4000u : width;
  height = height > 0x8000u ? 0x8000u : height;
  return ((width - 1u) << 18u) | ((height - 1u) << 3u);
}

bool IsImageKind(FableGuestResourceKind kind) {
  return kind == FableGuestResourceKind::kTexture ||
         kind == FableGuestResourceKind::kSurface ||
         kind == FableGuestResourceKind::kDepthSurface;
}

// Does the consumer already hold this exact content generation?
//
// The upload cache the consumer passes in is the protocol for skipping payload
// copies: it records, per address, the generation it last successfully
// materialized. Re-copying a multi-megabyte texture that the consumer already
// uploaded is pure cost (this path measured 194-230ms per slow sync call in the
// observer era). A resource with generation 0 has no content at all and is
// never skipped.
bool ConsumerHasCurrentUpload(
    const std::unordered_map<uint32_t, FableGuestResourceUploadCacheEntry>
        *known,
    uint32_t guest_address, uint64_t content_generation) {
  if (known == nullptr || content_generation == 0) {
    return false;
  }
  const auto it = known->find(guest_address);
  return it != known->end() &&
         it->second.upload_generation == content_generation;
}

// Build one snapshot. g_state.mutex MUST be held.
FableGuestResourceSnapshot BuildResourceSnapshotLocked(
    const Resource &resource,
    const std::unordered_map<uint32_t, FableGuestResourceUploadCacheEntry>
        *known,
    bool want_upload_data) {
  FableGuestResourceSnapshot snapshot;
  snapshot.kind = PublicResourceKind(resource);
  snapshot.guest_address = resource.guest_address;
  // Verbatim from GetSurfaceLevel / GetCubeMapSurface — the call named the
  // parent outright, so nothing recovers it from address arithmetic.
  snapshot.parent_address = resource.view_parent_texture;
  // Registry-wide monotonic "last mutated" stamp (see Resource::change_serial).
  // Falls back to the create serial for a resource registered before the
  // journal ever stamped it.
  snapshot.serial = resource.change_serial != 0 ? resource.change_serial
                                                : resource.create_serial;
  snapshot.upload_generation = resource.content_generation;

  switch (snapshot.kind) {
    case FableGuestResourceKind::kVertexBuffer:
    case FableGuestResourceKind::kIndexBuffer: {
      snapshot.buffer.object_address = resource.guest_address;
      // For a buffer the registry key IS the D3D header -- see the create-exit
      // note on why buffers are registered at wrapper+8 -- so object and header
      // are the same address here, and deliberately so: the header is the only
      // one of the two that every guest bind site names.
      snapshot.buffer.header_address = resource.guest_address;
      snapshot.buffer.stride_or_index_format = resource.stride;

      // *** RE-READ THE HEADER. NEVER SERVE A CACHED BUFFER ADDRESS. ***
      //
      // A buffer's payload moves under it: the physical pool compacts, memcpys
      // the bytes and dispatches the move callback (kRelocateResource), which
      // rewrites the header's stated address through XGOffsetResourceAddress.
      // The header ADDRESS is stable across that; the address it CONTAINS is
      // not. Reading it here, at the moment the consumer asks, is what makes
      // the answer current -- and it costs two guest loads.
      //
      // The stored fields are the fallback, not the source: they are what the
      // last bind stated, which is right up to the next compaction.
      const auto stated = refii::gtl::ReadStatedBufferHeaderLocked(
          refii::renderer::FableGuestMemoryBase(), resource.guest_address,
          snapshot.kind == FableGuestResourceKind::kIndexBuffer);
      const uint32_t data_address =
          stated.valid ? stated.data_address : resource.base_address;
      const uint32_t byte_size =
          stated.valid ? stated.byte_size : resource.byte_size;
      snapshot.buffer.gpu_address = data_address;
      snapshot.buffer.byte_size = byte_size;
      if (stated.index_stride != 0) {
        // Index element size is a header fact (Common>>29), not a bind
        // argument, so it wins over whatever the registry carried.
        snapshot.buffer.stride_or_index_format = stated.index_stride;
      }

      // THE PAYLOAD. This is the field whose absence produced
      // `missing_vertex_input`: the consumer creates an UPLOAD-heap buffer from
      // byte_size and then fills it from `upload_data`, and buffers were the
      // one kind this feed never gave any (IsImageKind excluded them), so every
      // vertex/index buffer materialized 4 bytes wide and empty.
      //
      // The bytes are read from the address the guest STATED in the header --
      // the same address, through the same decode, that D3DDevice_SetStreamSource
      // writes into the fetch constant and that the GPU would fetch from. That
      // is honouring stated data (§5), not reconstructing a descriptor.
      if (want_upload_data && byte_size != 0 &&
          !ConsumerHasCurrentUpload(known, resource.guest_address,
                                    resource.content_generation)) {
        // Cap: a buffer larger than this is not a buffer we can serve in one
        // copy per sync, and silently truncating would hand the consumer a
        // short view it would then bind as if complete.
        constexpr uint32_t kMaxBufferPayloadBytes = 32u << 20;
        uint8_t *const guest_base = refii::renderer::FableGuestMemoryBase();
        if (data_address == 0 || guest_base == nullptr) {
          ++refii::gtl::g_state.stats.buffer_content_no_header;
        } else if (byte_size > kMaxBufferPayloadBytes) {
          ++refii::gtl::g_state.stats.buffer_content_oversize;
        } else if (!refii::renderer::bink::IsReadableGuestByteRange(
                       guest_base, data_address, byte_size)) {
          ++refii::gtl::g_state.stats.buffer_content_unreadable;
        } else {
          const uint8_t *src =
              guest_base + data_address +
              refii::renderer::bink::GuestPhysicalHostOffset(data_address);
          snapshot.upload_data.assign(src, src + byte_size);
          ++refii::gtl::g_state.stats.buffer_content_served;
        }
      }
      if (!snapshot.upload_data.empty() && snapshot.upload_generation == 0) {
        // Content exists but no write root ever announced it. A vertex buffer
        // filled through Lock/Unlock gets a real generation from that root and
        // a relocation bumps it again; a buffer that has neither is still
        // holding bytes the GPU would fetch, and a zero generation makes every
        // upload path discard them (UploadTrackedBuffer's first test).
        snapshot.upload_generation = 1;
      }
      snapshot.buffer.upload_generation = snapshot.upload_generation;
      // upload_hash: still no GTL source; the GTL tracks content by generation.
      break;
    }

    case FableGuestResourceKind::kTexture:
      snapshot.texture.object_address = resource.guest_address;
      snapshot.texture.parent_address = resource.view_parent_texture;
      // The base a re-point (XGOffsetBaseTextureAddress) last stated. It is
      // authoritative over fetch_dwords[1] — the re-point keeps it current and
      // deliberately does not refresh the fetch block.
      snapshot.texture.base_address = resource.base_address;
      snapshot.texture.byte_size = resource.byte_size;
      if (resource.fetch_dwords_stated) {
        snapshot.texture.raw_fetch_dwords = resource.fetch_dwords;
      }
      snapshot.texture.upload_generation = resource.content_generation;
      break;

    case FableGuestResourceKind::kSurface:
    case FableGuestResourceKind::kDepthSurface:
      snapshot.surface.object_address = resource.guest_address;
      snapshot.surface.parent_address = resource.view_parent_texture;
      // STATED at D3DDevice_CreateSurface(Width, Height, Format, ...). The
      // extent is re-packed, not decoded from a hardware descriptor.
      snapshot.surface.packed_extent =
          PackSurfaceExtent(resource.width, resource.height);
      snapshot.surface.d3d_format = resource.format;
      snapshot.surface.pitch_msaa = resource.width;
      break;

    case FableGuestResourceKind::kVertexShader:
    case FableGuestResourceKind::kPixelShader:
      // THE FIELD THAT UNBLOCKS `missing_pipeline`: bytecode_hash is the only
      // key that finds a compiled artifact
      // (FindRendererShaderLibraryEntryByGuestHash). It is FNV-1a-64 over the
      // whole stated shader container — byte-for-byte the range the offline
      // extractor hashed into RendererShaderLibraryEntry::guest_bytecode_hash.
      snapshot.shader.object_address = resource.guest_address;
      snapshot.shader.bytecode_address = resource.shader_bytecode_address;
      snapshot.shader.bytecode_size = resource.shader_bytecode_size;
      snapshot.shader.bytecode_offset = resource.shader_bytecode_offset;
      snapshot.shader.bytecode_hash = resource.shader_bytecode_hash;
      break;

    case FableGuestResourceKind::kVertexDeclaration:
      snapshot.vertex_declaration.object_address = resource.guest_address;
      snapshot.vertex_declaration.elements_address =
          resource.decl_elements_address;
      snapshot.vertex_declaration.elements_size = resource.decl_elements_size;
      snapshot.vertex_declaration.element_count = resource.decl_element_count;
      snapshot.vertex_declaration.max_stream = resource.decl_max_stream;
      snapshot.vertex_declaration.stream_mask = resource.decl_stream_mask;
      snapshot.vertex_declaration.declaration_flags = resource.decl_flags;
      snapshot.vertex_declaration.elements_hash = resource.decl_elements_hash;
      // A D3D9 input layout cannot be rebuilt from a hash, so the elements
      // ALWAYS ride along: DecodeVertexDeclaration rejects an empty payload
      // outright (plume_vertex_declaration.cpp:181). They are tens of bytes.
      snapshot.upload_data = resource.decl_elements;
      if (!snapshot.upload_data.empty() && snapshot.upload_generation == 0) {
        // The elements are content, delivered with the create. Without a
        // non-zero generation every upload path treats them as absent.
        snapshot.upload_generation = 1;
      }
      break;

    case FableGuestResourceKind::kUnknown:
    default:
      break;
  }

  // Content payload, served from the GTL's own content store (unlock ranges,
  // tile writes, and the stated-base tier added 2026-08-04 for content written
  // by raw CPU stores). Only the BASE allocation is served: the store keys base
  // and mip separately because the guest states them separately, and the
  // consumer's upload path takes one linear payload per snapshot.
  if (want_upload_data && IsImageKind(snapshot.kind) &&
      snapshot.upload_data.empty() &&
      !ConsumerHasCurrentUpload(known, resource.guest_address,
                                resource.content_generation)) {
    bool source_linear = false;
    if (refii::gtl::CopyResourceContentLocked(resource.guest_address,
                                              /*mip_range=*/false,
                                              snapshot.upload_data,
                                              source_linear)) {
      snapshot.upload_data_linear = source_linear;
      // upload_data_mip_level stays 0: this is the base allocation, never a
      // split-mip fallback.
      if (snapshot.upload_generation == 0) {
        // Content exists (the stated-base tier can serve bytes for a resource
        // no write root ever announced). A zero generation would make every
        // upload path discard them.
        snapshot.upload_generation = 1;
      }
    }
  }

  return snapshot;
}

std::vector<FableGuestResourceSnapshot> BuildSnapshotsForAddresses(
    const std::unordered_set<uint32_t> &addresses,
    const std::unordered_map<uint32_t, FableGuestResourceUploadCacheEntry>
        *known_resources,
    const std::unordered_set<uint32_t> *upload_data_filter) {
  std::vector<FableGuestResourceSnapshot> out;
  if (addresses.empty()) {
    return out;
  }
  out.reserve(addresses.size());
  std::lock_guard<std::mutex> lock(refii::gtl::g_state.mutex);
  ++refii::gtl::g_state.snapshot_calls;
  for (const uint32_t address : addresses) {
    const auto it = refii::gtl::g_state.resources.find(address);
    if (it == refii::gtl::g_state.resources.end()) {
      // Not a registered resource. Returning nothing is the honest answer and
      // is what the consumer's starved-request recovery expects.
      continue;
    }
    const bool want_upload_data =
        upload_data_filter == nullptr || upload_data_filter->count(address) != 0;
    out.push_back(
        BuildResourceSnapshotLocked(it->second, known_resources,
                                    want_upload_data));
  }
  refii::gtl::g_state.snapshots_served += out.size();
  return out;
}

}  // namespace

std::vector<FableGuestResourceSnapshot>
GetFableDirectResourceSnapshotsForAddresses(
    const std::unordered_set<uint32_t> &addresses,
    const std::unordered_map<uint32_t, FableGuestResourceUploadCacheEntry>
        *known_resources,
    const std::unordered_set<uint32_t> *upload_data_filter,
    bool *observer_busy) {
  if (observer_busy != nullptr) {
    // There is no observer. The GTL lock is held for the duration of the
    // harvest and never degrades to a cache-only fallback, so this feed is
    // never "busy" — and the consumer's busy-path retries must therefore never
    // fire.
    *observer_busy = false;
  }
  return BuildSnapshotsForAddresses(addresses, known_resources,
                                    upload_data_filter);
}

std::vector<FableGuestResourceSnapshot>
GetFableDirectResourceSnapshotsForAddressesBlocking(
    const std::unordered_set<uint32_t> &addresses,
    const std::unordered_map<uint32_t, FableGuestResourceUploadCacheEntry>
        *known_resources,
    const std::unordered_set<uint32_t> *upload_data_filter) {
  // Identical to the non-blocking form: the GTL harvest ALWAYS waits for its
  // own mutex, so there is no degraded variant to escalate from. The two entry
  // points are kept distinct only because the consumer's lock-ordering
  // discipline differs between them (it drops its registry lock around this
  // one).
  return BuildSnapshotsForAddresses(addresses, known_resources,
                                    upload_data_filter);
}

FableGuestResourceChangeSet GetFableDirectResourceChanges(
    bool /*wait_for_observer*/) {
  FableGuestResourceChangeSet changes;
  std::lock_guard<std::mutex> lock(refii::gtl::g_state.mutex);
  changes.serial = refii::gtl::g_state.change_serial;
  changes.observer_busy = false;
  changes.changed_addresses.reserve(
      refii::gtl::g_state.changed_resources.size());
  for (const auto &[address, serial] : refii::gtl::g_state.changed_resources) {
    (void)serial;
    changes.changed_addresses.push_back(address);
  }
  changes.destroyed_addresses.reserve(
      refii::gtl::g_state.destroyed_resources.size());
  changes.destroyed_serials.reserve(
      refii::gtl::g_state.destroyed_resources.size());
  for (const auto &[address, serial] :
       refii::gtl::g_state.destroyed_resources) {
    changes.destroyed_addresses.push_back(address);
    changes.destroyed_serials.push_back(serial);
  }
  // lock_wait_ms / teardown_drain_ms / refresh_ms describe the observer's three
  // internal phases. None of them exists here; leaving them zero is what the
  // sync timing line should report.
  return changes;
}

void DrainPendingFableDirectTextureIdentityOperations() {
  // NOTHING TO DRAIN, BY CONSTRUCTION.
  //
  // The observer queued texture-identity operations because it discovered
  // identity asynchronously (scans, deferred captures) and had to hand them to
  // the direct renderer out of band. The GTL states identity INLINE: every
  // snapshot carries its own fetch block, base and parent, and the consumer
  // registers shader/declaration identity straight off the snapshot in
  // RegisterDirectShaderAndDeclarationIdentityFromSnapshotLocked. There is no
  // second channel to flush.
  //
  // Kept (rather than deleted with its call sites) because it is on the
  // published snapshot contract and its two callers are ordering barriers in
  // the consumer's sync flow.
}

void AcknowledgeFableDirectResourceChanges(uint64_t serial) {
  std::lock_guard<std::mutex> lock(refii::gtl::g_state.mutex);
  // Retire only what the consumer actually saw. An address re-stamped after
  // the watermark it is acknowledging has changed AGAIN since that harvest and
  // must survive into the next one — this is the whole reason the journal
  // stores a serial per address instead of a flag.
  for (auto it = refii::gtl::g_state.changed_resources.begin();
       it != refii::gtl::g_state.changed_resources.end();) {
    if (it->second <= serial) {
      it = refii::gtl::g_state.changed_resources.erase(it);
    } else {
      ++it;
    }
  }
}

void AcknowledgeFableDirectDestroyedResources(
    const std::vector<uint32_t> &addresses,
    const std::vector<uint64_t> &serials) {
  std::lock_guard<std::mutex> lock(refii::gtl::g_state.mutex);
  for (size_t i = 0; i < addresses.size(); ++i) {
    const auto it =
        refii::gtl::g_state.destroyed_resources.find(addresses[i]);
    if (it == refii::gtl::g_state.destroyed_resources.end()) {
      continue;
    }
    // Per-address, and only for the destruction the consumer named. If the
    // address was destroyed AGAIN after this one (recycled heap objects do
    // exactly that), the newer entry stays pending.
    const uint64_t acknowledged = i < serials.size() ? serials[i] : 0ull;
    if (it->second <= acknowledged) {
      refii::gtl::g_state.destroyed_resources.erase(it);
    }
  }
}

}  // namespace refii::renderer
