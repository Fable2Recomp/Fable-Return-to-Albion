#pragma once

#include <cstdint>

namespace refii::gtl {

// Fable ABI twin addresses (renderer_hook_targets.h is the source of truth;
// duplicated here as integral constants so the GTL never includes the hook
// policy machinery).
inline constexpr uint32_t kCreateTexture = 0x82B98B28;
inline constexpr uint32_t kCreateSurface = 0x821F4EC0;
inline constexpr uint32_t kCreateVertexBuffer = 0x82B8B820;
inline constexpr uint32_t kCreateIndexBuffer = 0x82B8D7A8;
inline constexpr uint32_t kCreateVertexShader = 0x82B994B8;
inline constexpr uint32_t kCreatePixelShader = 0x82B992B0;
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
inline constexpr uint32_t kFableCreateVertexShader = 0x82B87738;
inline constexpr uint32_t kFableCreatePixelShader = 0x82B87818;
inline constexpr uint32_t kCreateVertexDeclaration = 0x82B99720;
inline constexpr uint32_t kDestructResource = 0x821FC320;

inline constexpr uint32_t kSetTexture = 0x821B6EE0;
inline constexpr uint32_t kSetRenderTarget = 0x822869A0;
inline constexpr uint32_t kSetDepthStencilSurface = 0x82264590;
inline constexpr uint32_t kSetVertexShader = 0x82232510;
inline constexpr uint32_t kSetPixelShader = 0x82208BB0;
inline constexpr uint32_t kSetVertexDeclaration = 0x822224A8;
inline constexpr uint32_t kSetStreamSource = 0x821B6C60;
inline constexpr uint32_t kSetIndices = 0x8219CC30;
inline constexpr uint32_t kSetViewport = 0x821F9F58;
inline constexpr uint32_t kSetScissorRect = 0x821F9C08;

inline constexpr uint32_t kDrawPrimitive = 0x8221C3E8;
inline constexpr uint32_t kDrawIndexedPrimitive = 0x8221DFC0;
inline constexpr uint32_t kDrawPrimitiveUP = 0x82265CD8;

inline constexpr uint32_t kUnlockTextureRect = 0x82B98950;
inline constexpr uint32_t kUnlockVertexBuffer = 0x82B9EEC0;
inline constexpr uint32_t kUnlockIndexBuffer = 0x82B9EED0;
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
inline constexpr uint32_t kUnlockResource = 0x822C2630;
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
inline constexpr uint32_t kRelocateResource = 0x82B876F0;
// Subresource view roots. Both return the new view object in r3 at exit, so
// they pair enter (parent + face/level) with exit (the view) like the other
// factory roots. They state the face/level the GTL would otherwise have to
// recover from addresses — which is impossible for a cube, whose faces are
// separate allocations.
//   D3DTexture_GetSurfaceLevel(pTexture /*r3*/, Level /*r4*/)
//   D3DCubeTexture_GetCubeMapSurface(pTexture /*r3*/, FaceType /*r4*/,
//                                    Level /*r5*/)
inline constexpr uint32_t kGetSurfaceLevel = 0x82B98860;
inline constexpr uint32_t kGetCubeMapSurface = 0x82B98978;
// XGCompressSurface: writes surface contents, so it bumps content_generation
// like the Unlock roots. NOTE: this is NOT a resolve and it never calls
// D3DDevice_Resolve — it was previously misnamed kResolveSurface and used as a
// batch boundary, which rested on the superseded "private-rect blit" model.
// The real EDRAM resolve root is kResolve (0x82206888) below.
inline constexpr uint32_t kXGCompressSurface = 0x8300F928;
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
inline constexpr uint32_t kKickOffSegment = 0x821E8D38;
// D3D::CDevice::KickOff. Calls KickOffSegment internally, so on hardware ONE
// SetFence runs per KickOff. Under Plume the root is suppressed and its
// emulation calls EmulateFableKickOffSegment as a plain C++ call -- no nested
// guest hook fires -- so this root must retire the fence itself. Suppression is
// exactly what makes retiring at both roots safe rather than double-counting.
inline constexpr uint32_t kKickOff = 0x821E8EC0;
// The two guest waits that spin on the write-back block at *(dev+0x2A90).
// D3D_CDevice_BlockOnSecondaryPosition polls B[+4] (ring position) and is
// reached from D3D_RingBufferDeviceAllocate when the ring is full;
// D3D_CDevice_BlockOnFence polls B[+0] (retired fence) and is reached from
// KickOff, StartNewSegment and every D3D_LockResource of a busy resource.
// Retiring at their ENTER is what breaks the run-571 self-deadlock — see the
// long note in OnHookEnter.
inline constexpr uint32_t kBlockOnSecondaryPosition = 0x82206400;
inline constexpr uint32_t kBlockOnFence = 0x82242668;
// Render_FrameCompletionCallback (0x82B90160) state. Every guest Present inserts
// this as a TYPE-1 GPU RING callback via D3D_InsertCallback (0x82B6F274); on
// hardware the command processor fires it when that packet retires. Our port has
// no command processor, so it never fires and the whole frame-sync clock stays
// at its static-init zero. See AdvanceGuestFrameCompletionLocked.
inline constexpr uint32_t kFrameSyncRecordBase = 0x83491D98;  // 2 records, stride 24
inline constexpr uint32_t kFrameSyncRecordStride = 24u;
inline constexpr uint32_t kFrameSyncRecordTimestamp = 0x00u;  // u64, mftb ticks
inline constexpr uint32_t kFrameSyncRecordPrevTimestamp = 0x08u;  // u64
inline constexpr uint32_t kFrameSyncRecordCounter = 0x10u;    // u32, +1 per completion
inline constexpr uint32_t kFrameSyncPublishedIndex = 0x83497124;  // u32, 0 or 1
// These three are written by the guest's OWN Present body — which under Plume is
// SUPPRESSED (rex_lhRenderer_EndScene -> EmulateFableEndScenePresentBoundary
// returns handled=true, so 0x82B6F1D0 never executes). Nothing else in the tree
// writes them (the observer only reads them), so the GTL must perform these
// writes too or they stay at their static-init zero forever.
inline constexpr uint32_t kGuestPrevPresentTimebase = 0x83491DC8;  // u64 (0x82B6F224)
inline constexpr uint32_t kGuestLastPresentTimebase = 0x83491DD0;  // u64 (0x82B6F230)
inline constexpr uint32_t kGuestPresentFrameCount = 0x83497120;    // u32 (0x82B6F290)
inline constexpr uint32_t kStreamedLoadCancel = 0x82A7AF68;
inline constexpr uint32_t kStreamedResidencyPoll = 0x82A7B148;
inline constexpr uint32_t kTileSurface = 0x8300C290;         // XGRAPHICS__TileSurface
inline constexpr uint32_t kTileSurfaceThunk = 0x8300CB40;    // j_XGRAPHICS__TileSurface
inline constexpr uint32_t kTileTextureLevel = 0x8300CB48;    // XGTileTextureLevel
inline constexpr uint32_t kTileVolume = 0x83010E60;          // XGRAPHICS__TileVolume
inline constexpr uint32_t kTileVolumeTextureLevel = 0x830117B8;
// Header-init creation roots: Fable mostly creates textures by initializing
// 52-byte Xenos headers in place rather than through factory calls. The
// header out-pointer rides the stack for the wide variants (offsets pinned
// by the observer's earlier IDA work: +0x6C inline builder, +0x7C XG
// variants) and r10 for line/cube.
inline constexpr uint32_t kSetTextureHeaderInline = 0x821FB650;
inline constexpr uint32_t kSetBaseTextureHeader = 0x821FB968;
inline constexpr uint32_t kXGSetTextureHeader = 0x821FBB78;
inline constexpr uint32_t kXGSetLineTextureHeader = 0x8300BEE0;
inline constexpr uint32_t kXGSetCubeTextureHeader = 0x8300BF48;
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
inline constexpr uint32_t kOffsetBaseTextureAddress = 0x8225FDF0;
// THE SINGLE-LEVEL SIBLING (2026-08-04). Texture_InitHeaderWrapper 0x821FBD90
// stamps the base through XGOffsetResourceAddress /*0x821fbe98*/ when Levels ==
// 1 and XGOffsetBaseTextureAddress /*0x821fbea8*/ when Levels > 1 — and every
// scratchpad render target is single-level, so they ALL took the root above
// that we were not receiving. Same trap as the 2026-07-17 fix, one function
// along. Measured run 603: `slot=13 tracked=0 image=0 ready=0` x287, the
// unbacked texture the last draw of every frame blends over the whole screen.
inline constexpr uint32_t kOffsetResourceAddress = 0x8225FD18;

inline constexpr uint32_t kPresent = 0x82B6F1D0;
inline constexpr uint32_t kDisplaySwap = 0x82BA34D8;
// Clear roots (register mapping mirrors the observer's QueueClearEventLocked
// call sites). kClearF carries an explicit D3DCLEAR mask; kClearRect is a
// color-slot-0 fill whose depth/stencil are owned by the ClearF path.
// IDB-named 2026-07-18: kClearF = D3DDevice_ClearF(device, Flags r4,
// D3DRECT* r5, D3DVECTOR4* color r6, Z f1, Stencil r8 — 8-byte arg slots skip
// r7 for the double); null rect = full current-RT extent.
// kClearRect = D3D_ClearRect(device, LEFT r4, TOP r5, RIGHT r6, BOTTOM r7,
// color* r8, depth f1) — rect args are LTRB (decompile-proven), never x/y/w/h.
inline constexpr uint32_t kClearF = 0x821E2FE0;
inline constexpr uint32_t kClearRect = 0x821EF830;
// Title-path draw roots: inline-vertex submissions carry most GUI/storybook
// draws (device state applies identically).
inline constexpr uint32_t kBeginVertices = 0x82205F68;
inline constexpr uint32_t kBeginIndexedVertices = 0x82217DB8;
// D3DDevice_BeginIndexedVertices' 9th argument (ppVertexData) is a STACK arg.
// Xbox 360 ABI: parameters are homed in 8-byte slots from caller_sp+0x10, so
// argument N's low word sits at caller_sp + 0x14 + 8*(N-1); N=9 -> +0x54.
// Confirmed against the guest's own load of it — `lwz r9, 0xE0+arg_54(r1)` at
// 0x82218288, inside a 0xE0 frame — and consistent with the resolve root's
// ClearStencil read at +0x5C (its 10th argument). r1 at hook entry is the
// caller's sp (pre-prologue), the same assumption the resolve capture makes.
inline constexpr uint32_t kBeginIndexedVerticesVertexOutStackOffset = 0x54u;
// D3DDevice_Resolve (IDA-named 2026-07-17): the Xenos EDRAM resolve. Formerly
// misnamed kPrivateTextureRectangleDraw and modelled as a draw/blit — it is
// neither. Reached directly, via D3D__ResolveColor (0x822326E0, Flags=0/RT0),
// D3D__ResolveDepth (0x822A0A78, Flags=4/DS), and once PER TILE from
// D3DDevice_EndTiling (0x8227F3B0).
inline constexpr uint32_t kResolve = 0x82206888;
// DrawIndexedUserPrimitives(vertex_count, prim_count, index_ptr, vertex_ptr,
// stride): ring-allocates via kBeginIndexedVertices then memcpys the caller's
// index/vertex payload before returning. 26 callers: the bink movie
// compositor plus the UI/storybook quad families.
inline constexpr uint32_t kDrawIndexedUserPrimitives = 0x8220A3F0;

// The two PM4 ring writers that bypass every hooked D3D draw root (see
// TessellatedPatchDrawPacket in refii_gtl.h for the full IDA map).
//   0x82207AF8 D3D_EmitDrawIndexedPackets_INFERRED   terrain, DMA-indexed
//   0x8221C898 D3D_EmitDrawPrimitivePackets_INFERRED water, auto-index
// CAPTURE-ONLY: registered so the shape can be measured. Adding them changes
// nothing that renders -- neither symbol is in any IsNativeRenderWorkHook list,
// so the guest body still runs and still writes its own packets.
inline constexpr uint32_t kEmitTessellatedIndexedPackets = 0x82207AF8;
inline constexpr uint32_t kEmitTessellatedPrimitivePackets = 0x8221C898;

// Device-object offsets used by the tessellated-patch capture. Every one is
// read off a literal in the two emitters or in the function that consumes it;
// see the header for the per-field evidence.
inline constexpr uint32_t kDeviceRingWritePointer = 0x30u;   // m_pRing
inline constexpr uint32_t kDeviceRingGuarantee = 0x38u;      // m_pRingGuarantee
// GPU_CONTROLPACKET::HiControl (m_ControlPacket at 0x2934, HiControl at +0xC) =
// the Xenos register 0x2203 Hi-Z / Hi-Stencil control shadow. It is the third
// argument D3D_SetPending_Predicated receives (BeginVertices: `lwz r5,
// 0x2940(r31)` at 0x82206050), which is why it was mislabelled a predication
// mask until 2026-07-26; see TessellatedPatchDrawPacket::hi_z_control for the
// full writer list.
inline constexpr uint32_t kDeviceHiZControl = 0x2940u;
// Real predication state. Raw app mask lo/hi, then the resolved value the
// driver actually emits as SET_BIN_SELECT_LO, then the enable flag. All four
// are written by both D3DDevice_SetPredication (0x822655F0) and the ring-direct
// D3D_SetTilePredication_INFERRED (0x82BA1D40).
inline constexpr uint32_t kDevicePredicationMaskLo = 0x2AB4u;
inline constexpr uint32_t kDevicePredicationMaskHi = 0x2AB8u;
inline constexpr uint32_t kDevicePredicationBinSelect = 0x31A4u;
inline constexpr uint32_t kDevicePredicationEnabled = 0x31ACu;
inline constexpr uint32_t kDeviceTiledVariantFlag = 0x2ABCu;
inline constexpr uint32_t kDeviceVizQueryIndex = 0x2F9Bu;
inline constexpr uint32_t kDeviceIndexBufferSlot = 0x3094u;  // D3DDevice_SetIndices
// Readability extent for a whole-device probe. Not the true object size --
// just past the highest binding slot we read (kDevicePredicationEnabled at
// 0x31AC), so one IsReadableGuestByteRange call covers every device field the
// packet builders touch.
inline constexpr uint32_t kFableDeviceObjectExtent = 0x31B0u;
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
inline constexpr uint32_t kDeviceRegShadowRbSurface = 0x2880u;  // Xenos reg 0x2000+i
inline constexpr uint32_t kDeviceRegShadowPaVgt = 0x28CCu;      // Xenos reg 0x2100+i
inline constexpr uint32_t kDeviceRegShadowSq = 0x2934u;         // Xenos reg 0x2200+i
inline constexpr uint32_t kDeviceRegShadowVgtTess = 0x2964u;    // Xenos reg 0x2280+i
inline constexpr uint32_t kDeviceRegShadowRbMode = 0x29B8u;     // Xenos reg 0x2300+i
inline constexpr uint32_t kDeviceRegShadowRbCopy = 0x2A50u;     // Xenos reg 0x2380+i
inline constexpr uint32_t kDeviceVsFloatConstantShadow = 0x780u;  // Xenos reg 0x4000
// g_ShaderBindingTable: 12-byte entries {constant_register, count, ...}. IDA
// sub_822239F8 (the setter both emitters' callers use) indexes it by the
// BINDING index its callers pass (332 / 343 / 396) and writes the float at
// device + 0x780 + 16*constant_register.
inline constexpr uint32_t kShaderBindingTable = 0x834BDED8u;
inline constexpr uint32_t kShaderBindingTableStride = 12u;

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

// Journal bound. Both maps are drained every compose by the direct renderer's
// acknowledge calls; the cap only exists so a consumer that stops acking
// cannot grow the GTL without limit (the depth-stencil eviction leak class).
inline constexpr size_t kMaxJournalledResources = 65536;

// Stamp an address as changed. Called from the registry-mutating roots, always

}  // namespace refii::gtl
