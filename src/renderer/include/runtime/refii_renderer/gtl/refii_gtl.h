#pragma once

// Refii Graphics Translation Layer (design doc §3.1–§3.2).
//
// The wrapper roots (guest D3D twins) feed this layer exact events:
// resources are registered once at their Create call, state-setting calls
// mutate a tracked device state, and each draw snapshots that state into an
// immutable RefiiDrawPacket. Identity is by construction — a guest object
// address maps to exactly one GTL resource for its lifetime. Nothing in this
// layer derives identity or content from guest memory inspection.

#include <array>
#include <cstdint>
#include <vector>

namespace refii::renderer {
struct RendererHookArgs;
}

namespace refii::gtl {

using refii::renderer::RendererHookArgs;

enum class ResourceKind : uint8_t {
  kUnknown = 0,
  kTexture,
  kSurface,
  kVertexBuffer,
  kIndexBuffer,
  kVertexShader,
  kPixelShader,
  kVertexDeclaration,
};

struct Resource {
  ResourceKind kind = ResourceKind::kUnknown;
  uint32_t guest_address = 0;
  // Creation-call facts, verbatim. Semantics depend on kind; unknown fields
  // stay zero until the corresponding Create wrapper's argument order is
  // pinned. They are never re-derived after creation.
  uint32_t width = 0;
  uint32_t height = 0;
  uint32_t levels = 0;
  uint32_t format = 0;
  uint32_t byte_size = 0;
  uint32_t stride = 0;
  uint64_t create_serial = 0;
  // WHICH root registered this resource. Three paths exist and they mean very
  // different things: a real factory Create, a header-init call, or mere
  // ADOPTION at XGOffsetBaseTextureAddress (which names memory for an object
  // whose creation was never seen). Distinguishing them is what turns "the GTL
  // knows this resource" into a statement about how much it actually knows.
  uint32_t create_symbol = 0;
  // Return address of whoever called the registering root. XGSetTextureHeader
  // alone has 20 callers, so the symbol says only "a header was initialised";
  // the LR says WHICH subsystem did it. That is the difference between
  // guessing among candidates and naming one.
  uint32_t create_lr = 0;
  // Content generation: bumped by Unlock / tile-upload events only.
  uint64_t content_generation = 0;
  // Bumped when a Resolve names this resource as its destination — i.e. its
  // content was produced on the GPU (host RT→texture copy) and no CPU bytes
  // exist for it at all.
  //
  // Kept SEPARATE from content_generation on purpose: the two mean different
  // things and a consumer must not treat "the GPU wrote this" as "there are
  // bytes to upload". Its job is to stop §4 counting GPU-produced textures as
  // a CPU-content gap, which would argue for keeping observer capture
  // machinery to answer a question that has no answer.
  uint64_t gpu_content_generation = 0;
  // The console memory this object currently NAMES, as last stated by
  // XGOffsetBaseTextureAddress(pTexture, pBaseAddress, pMipAddress) — the XDK
  // call that re-points an existing header at new memory. Zero until such a
  // call is seen.
  //
  // This is why texture-header objects are NOT stable identities: Fable keeps a
  // pool of header objects and re-points them per use, so one object address
  // legitimately names different memory over time (run 437 measured header
  // 0x40513C10 carrying fetch bases 0x12AE1000 / 0x12BB9000 / 0x12CFD000 with
  // formats 6/7/54). address_generation increments on each re-point, so a
  // consumer can tell "same object, different memory" from "unchanged".
  uint32_t base_address = 0;
  uint32_t mip_address = 0;
  uint64_t address_generation = 0;

  // TENANCY. A header ADDRESS is not an identity — the guest recycles 52-byte
  // header blocks through a free list (`sub_821EE848` pop /`sub_82237020` push)
  // and, worse, re-describes LIVE headers in place via an LRU slot evictor
  // (`sub_821EE570`), so the same address becomes a different texture with no
  // allocation and no create event. Same hazard class as the ring rule "an
  // address is a CURSOR, key on (lap, address)".
  //
  // This counts how many times THIS ADDRESS has been handed to a new texture.
  // Key identity on (guest_address, tenancy_generation).
  //
  // IDA-proven bump points (see the tenancy contract memo):
  //   XGSetTextureHeader 0x821FBB78 / XGSetLineTextureHeader 0x8300BEE0 /
  //   XGSetCubeTextureHeader 0x8300BF48, when pTexture != NULL. That single
  //   family covers fresh allocation, pool-recycled block AND live in-place
  //   rewrite. Every non-NULL pTexture write zeroes 52 bytes and resets
  //   ReferenceCount to 1 (0x821FBAAC), so there is no benign "re-describe the
  //   same texture" case to suppress.
  // Deliberately NOT bumped by:
  //   - D3D_SetTextureHeader 0x821FB650: FOUR of its six callers pass a stack
  //     scratch (the GPU-codec paths, per video frame), so it over-counts on
  //     SP-recycled addresses.
  //   - XGOffsetBaseTextureAddress 0x8225FDF0: pure re-point, writes only the
  //     base/mip dwords. Same texture, new memory — that is what
  //     address_generation above is for. Note the pool defragmenter
  //     (sub_82B822D0) drives it, which is why identity keys on the HEADER
  //     address and never on the base.
  uint64_t tenancy_generation = 0;

  // The byte extent the CPU dirtied, as stated by the unlock root that
  // produced the current content_generation (§4 "content only from Unlock
  // ranges"). Half-open [start, end); both zero when the unlock stated no
  // range. D3D::LockResource accumulates this into the D3DResource itself and
  // D3D::UnlockResource flushes exactly it, so it is call-stated fact, not
  // inference — it says WHAT changed, where content_generation only says THAT
  // something changed.
  //
  // These are the addresses the root names, which for Fable is the
  // write-combined alias.
  //
  // The old note here said a consumer "still resolves raw-vs-decoded itself".
  // That is SETTLED as of 2026-07-22 and no consumer should search any more:
  // a Xenos physical address P is addressable at 0xC0000000|P and nowhere
  // else (see FableGuestPhysicalViewAddress, IDA 0x821F6300 + rexglue
  // map_info). ContentPacket::source_address states the addressable form
  // outright.
  uint32_t dirty_base_start = 0;
  uint32_t dirty_base_end = 0;
  uint32_t dirty_mip_start = 0;
  uint32_t dirty_mip_end = 0;
  // The 128-byte-aligned allocation addresses the same unlock stated, kept so
  // a consumer can turn the absolute range above into an OFFSET into the
  // resource. Required for multi-range resources: run 123 measured a 6-face
  // cube (0x404DF450) unlocking one 8192-byte face at a time across
  // 0xFF489000..0xFF495000, so each range is a sub-write at
  // (dirty_base_start - dirty_base_allocation), not a whole-resource upload.
  uint32_t dirty_base_allocation = 0;
  uint32_t dirty_mip_allocation = 0;

  // Subresource identity exactly as the view-creating call STATED it:
  // D3DTexture_GetSurfaceLevel(pTexture, Level) and
  // D3DCubeTexture_GetCubeMapSurface(pTexture, FaceType, Level) name the
  // parent and the face/level outright. These are verbatim call arguments —
  // the point of recording them is that nothing downstream has to RECOVER a
  // face or level from address arithmetic.
  //
  // Necessary because a cube's faces are separate allocations: run 128/129
  // measured 0x404DF4D0 unlocking six 8192-byte ranges, and the absolute
  // addresses alone cannot say which face each range is. Note also that the
  // parent's own base_address is NOT a reliable anchor — Fable re-points
  // header objects (address_generation), so the GTL probe reported
  // base=0xFA4A7000 for the very object whose unlocks stated 0xFF489000+.
  uint32_t view_parent_texture = 0;
  uint32_t view_face = 0;
  uint32_t view_level = 0;
  bool view_identity_stated = false;

  // §4 fetch-store ingest bookkeeping (`content_from_fetch_ingest`,
  // `fetch_content_hash`) DELETED 2026-08-04 with the rest of the ingest
  // residue. IngestBoundTextureContent — the only thing that ever set them —
  // was removed 2026-07-26, leaving a flag permanently false and a hash
  // permanently zero, plus a guard function that always early-returned.
  // Set when this object is ever bound as a RENDER TARGET. Such objects are
  // draw/copy destinations with a GPU-write + CPU-overlay content lifecycle the
  // observer arbitrates (GUI text PAGES are the canonical case: GPU copies the
  // background, CPU rasterizes text over it). The fetch-ingest must NOT claim
  // them — a bind-time snapshot freezes a pre-overlay state and loses the text.
  bool was_render_target = false;
  // Set when this object is ever bound as the DEPTH-STENCIL surface. Stated by
  // D3DDevice_SetDepthStencilSurface, which names the object outright -- the
  // snapshot feed reports kDepthSurface vs kSurface from this, because the
  // consumer takes the kind verbatim and never infers depth from a format.
  bool was_depth_target = false;
  // The guest texture header's 6-dword Xenos fetch-constant block, read from
  // object+28 -- the SAME offset D3DDevice_Resolve reads its destination's from
  // (see kDestTextureFetchOffset in the resolve handler).
  //
  // Why this is here: it was the ONE field of the observer's FableTextureIdentity
  // with no Resource equivalent (serial/object/parent/base/size/w/h/format/
  // generation all map across already), so identity consumers could not be
  // ported onto gtl::QueryResourceIdentity without it. Fetch constants live
  // only on packets and on DeviceState, i.e. per-DRAW rather than per-RESOURCE.
  //
  // Stated, not inferred: captured at the header-init roots that WRITE the
  // header. `fetch_dwords_stated` distinguishes "read a block of zeros" from
  // "never read one" -- do not test the array against zero.
  //
  // DELIBERATELY NOT refreshed at the XGOffsetBaseTextureAddress re-point: that
  // handler runs at hook ENTER, where the header still holds the PRE-re-point
  // block, so re-reading there would store a stale base behind a fresh-looking
  // flag. After a re-point, `base_address` (which the re-point computes
  // additively and does keep current) is the authoritative base -- prefer it
  // over fetch_dwords[1] whenever the two disagree.
  std::array<uint32_t, 6> fetch_dwords = {};
  bool fetch_dwords_stated = false;

  // ---- SHADER MICROCODE, stated at the Create call -------------------------
  // D3DDevice_CreateVertexShader / CreatePixelShader take ONE argument: a
  // pointer to the Fable shader CONTAINER. The container header states its own
  // payload placement at +4 (offset) and +8 (size), so nothing here is decoded
  // from a hardware descriptor -- the guest named the blob and the blob names
  // its payload.
  //
  // WHY THE CONTAINER AND NOT THE PAYLOAD: the offline shader extractor hashes
  // the WHOLE container (`guest_bytecode_hash` in shader_library.cpp, FNV-1a-64
  // over the .bin file), so the container hash is the key the shader library
  // matches. The library also indexes the payload-only hash
  // (`guest_payload_hash`), which is why both are kept -- a shader whose create
  // we never saw can still be resolved from its live object's payload.
  //
  // Immutable for the object's life: microcode is never rewritten in place.
  uint32_t shader_container_address = 0;
  uint32_t shader_container_size = 0;
  uint32_t shader_bytecode_address = 0;
  uint32_t shader_bytecode_size = 0;
  uint32_t shader_bytecode_offset = 0;
  uint64_t shader_bytecode_hash = 0;

  // ---- VERTEX DECLARATION ELEMENTS, stated at the Create call --------------
  // D3DDevice_CreateVertexDeclaration(pElements) names the element array; the
  // returned object also carries the count at +0x18 and an inline copy of the
  // elements at +0x34. Both are call-stated facts read at the create EXIT,
  // where the factory has finished writing the object.
  //
  // The bytes themselves ride along because a D3D9 input layout cannot be
  // rebuilt from a hash: the consumer decodes these 12-byte elements directly.
  uint32_t decl_elements_address = 0;
  uint32_t decl_elements_size = 0;
  uint32_t decl_element_count = 0;
  uint32_t decl_max_stream = 0;
  uint32_t decl_stream_mask = 0;
  uint32_t decl_flags = 0;
  uint64_t decl_elements_hash = 0;
  std::vector<uint8_t> decl_elements;

  // Last change-journal stamp for this address (see State::changed_resources in
  // refii_gtl.cpp). Monotonic across the whole registry, so a consumer can
  // order two resources' last mutations against each other -- which
  // create_serial and content_generation, being per-resource counters, cannot
  // do. Reported as FableGuestResourceSnapshot::serial.
  uint64_t change_serial = 0;
};

// Content authority (§4). One packet per byte range the guest actually wrote,
// emitted by the root that STATED the write — no scanning, no blank tests, no
// shape matching, no alias search. Every field is a call fact:
//
//   extent      D3D::UnlockResource flushes exactly the range D3D::LockResource
//               accumulated into the D3DResource (+0x14 base, +0x18 mip).
//   placement   the same root names the allocation (r4/r5), so a sub-write is
//               (range start - allocation) with no address arithmetic.
//   identity    GetSurfaceLevel / GetCubeMapSurface named the face and level
//               outright, so a cube face never has to be recovered.
//   address     0xC0000000|P is the only form a physical address is readable
//               at (IDA 0x821F6300 + rexglue map_info).
//
// This is the channel that replaces the observer's upload_data machinery. A
// consumer that finds itself guessing here should treat that as a bug in the
// packet, not add a heuristic.
struct ContentPacket {
  uint32_t resource = 0;  // registry key (guest object address)
  uint64_t content_generation = 0;
  uint64_t serial = 0;
  ResourceKind kind = ResourceKind::kUnknown;
  // false = the base allocation, true = the mip chain. The root states them
  // separately and they are separate allocations.
  bool mip_range = false;
  // Addressable source of these bytes, already in physical-view form when the
  // stated address was a raw physical. Read it directly.
  uint32_t source_address = 0;
  uint32_t byte_size = 0;
  // Offset of this range inside its allocation — the sub-write position for
  // multi-range resources (cube faces, page-by-page streaming).
  uint32_t allocation_offset = 0;
  // Verbatim from the view-creating call; false means the call was not seen,
  // NOT that the resource is single-subresource.
  uint32_t view_parent_texture = 0;
  uint32_t view_face = 0;
  uint32_t view_level = 0;
  bool view_identity_stated = false;
  // Layout of THESE bytes, stated by the root that produced them — never
  // inferred, and never a property of the resource.
  //
  // false: the bytes are the resource's own allocation as the guest holds it,
  //        so the fetch constant's tiled bit applies. This is what an Unlock
  //        range gives: D3D::UnlockResource flushes the D3DResource's own
  //        memory (r4/r5), which for a tiled texture is tiled.
  // true:  the bytes came from a CPU tile call's LINEAR source buffer, so the
  //        tiled bit must be ignored. This is what XGTileTextureLevel and its
  //        siblings give: they read a linear staging buffer and write the
  //        tiled destination, so the source needs no untile.
  //
  // Both forms decode to the same image; they are two encodings, not two
  // qualities. A consumer that ignores this flag will untile bytes that were
  // never tiled.
  bool source_linear = false;
};

inline constexpr uint32_t kMaxTextureSlots = 26;
inline constexpr uint32_t kMaxRenderStates = 32;
inline constexpr uint32_t kMaxSamplerSlots = 32;
constexpr uint32_t kMaxStreamSlots = 16;
inline constexpr uint32_t kMaxRenderTargets = 4;

// ---------------------------------------------------------------------------
// Draw-time device-state snapshot (PHASE 1, CAPTURE + CENSUS ONLY).
// ---------------------------------------------------------------------------
//
// Refii's shader-constant / render-state knowledge is a set of MIRRORS fed by
// hooked D3D setters. Those mirrors go stale, and the staleness has produced
// real rendering defects twice (the all-zero g_WorldViewProjection on
// quad-wrapper draws, and a cull-mode mirror that disagreed with the GPU for
// 82% of draws until the cull word was read live from device+0x2948).
//
// This struct is the alternative: the device object's OWN state, read straight
// out of guest memory at the draw root's ENTRY. It is populated and counted
// only — NOTHING consumes it for rendering. Making it authoritative and
// deleting the mirrors is Phase 2, gated on what the divergence census below
// actually reports.
//
// Layout is from the IDB's fully-typed `D3DDevice` struct (size 0x2B00), not
// hand-derived:
//   0x0000  m_Pending.m_Mask[5]           (UINT64 each, at 00/08/10/18/20)
//   0x0028  m_Predicated_PendingMask2
//   0x0480  m_Constants.Fetch[32]         TextureFetch[26] + VertexFetch[18]
//   0x0780  m_Constants.VertexShaderF[256]   Xenos register base 0x4000
//   0x1780  m_Constants.PixelShaderF[256]    Xenos register base 0x4400
//   0x2780  Flow[40] = VS_B[4] PS_B[4]@0x2790 VS_I[16]@0x27A0 PS_I[16]@0x27E0
//   0x2820  m_ClipPlanes[6][4]
//   0x2880..0x2A70  Destination/Window/Values/Program/Control/Tessellator/
//                   Misc/Point GPU packets (ControlPacket.ModeControl=0x2948)
//
// WHY ENTRY AND NOT EXIT: D3DDevice_BeginVertices opens by draining all five
// pending masks through sub_8221DE68, so at EXIT m_Mask[0..4] are ALL ZERO and
// the per-draw delta no longer exists. (Same lifetime trap as the RB_COPY_*
// registers on a resolve.) The VALUES are still valid at exit; only the masks
// are destroyed, which is exactly why the masks are captured here.
//
// DIRTY-MASK BIT NUMBERING IS FROM THE MSB: sub_8221DE68 walks the mask with
// `cntlzd`, so `bit = (1ull << 63) >> (reg >> 2)`. Mask bit k (k = 0 at the
// MSB) covers vec4 registers [4k .. 4k+3], i.e. the 64 bytes at bank + 64*k,
// i.e. host dwords [16k .. 16k+15] of the 1024-dword bank.
struct DrawDeviceSnapshot {
  bool valid = false;
  // Device object the snapshot came from (args.r3 for the six draw roots;
  // the g_D3DDevice_PresentModule singleton @0x83360364 for the wrapper root
  // that carries no device argument).
  uint32_t device = 0;

  // m_Pending.m_Mask[0..4] @0x00 and m_Predicated_PendingMask2 @0x28.
  //   [0] VS float  (bank 0x780,  Xenos base 0x4000) cleared by DRAW ONLY
  //   [1] PS float  (bank 0x1780, Xenos base 0x4400) cleared by DRAW ONLY
  //   [2] bits 0-11 ControlPacket->0x2200, 12-16 ProgramPacket->0x2180,
  //       17-20 Shaders, 21-31 ValuesPacket->0x2100
  //   [3] fetch constants (0x480); bit 0 = vertex fetch
  //   [4] MiscPacket->0x2300 (mask << 26); bit 0x01000000 -> Flow (0x2780)
  // [0] and [1] are the only clean per-draw deltas: masks 2/3/4 are also
  // zeroed by resolve/clear/present, so their delta is diluted and the regions
  // they describe are read unconditionally below instead of mask-gated.
  std::array<uint64_t, 5> pending_mask = {};
  uint64_t predicated_pending_mask2 = 0;

  // One entry per SET bit of pending_mask[0] / [1]: {bit index k, the 16 host
  // dwords at bank + 64*k}. Deliberately NOT a blind 4 KiB copy of each bank
  // per draw — two such copies were removed from this path already for cost.
  struct ConstantBlock {
    uint8_t bit = 0;
    std::array<uint32_t, 16> dwords = {};
  };
  std::vector<ConstantBlock> vs_dirty_blocks;
  std::vector<ConstantBlock> ps_dirty_blocks;

  // Flow constants @0x2780: VS_B[4], PS_B[4], VS_I[16], PS_I[16] = 40 dwords.
  std::array<uint32_t, 40> flow = {};
  bool flow_valid = false;
  // Fetch constants @0x480: 768 bytes = 192 dwords (26 texture fetches of 6
  // dwords, then 18 vertex fetches of 2 dwords).
  std::array<uint32_t, 192> fetch = {};
  bool fetch_valid = false;
  // m_ClipPlanes @0x2820: float[6][4] = 96 bytes = 24 dwords.
  std::array<uint32_t, 24> clip_planes = {};
  bool clip_planes_valid = false;
  // The contiguous GPU packet block 0x2880..0x2A70 = 0x1F0 bytes = 124 dwords.
  // m_ControlPacket.ModeControl (0x2948) is dword 50 of this block.
  std::array<uint32_t, 124> packet_block = {};
  bool packet_block_valid = false;
  // Convenience view of packet_block[50]: the cull word the GPU consumes.
  // IDA (D3DDevice_SetRenderState_CullMode, 0x8220B470):
  //   lwz r11,0x2948(r3); rlwimi r4,r11,0,0,28; stw r4,0x2948(r3)
  // so bits 29-31 of that dword ARE the D3DRS_CULLMODE value, in the same
  // domain as the render_states[19] mirror.
  uint32_t mode_control = 0;
  bool mode_control_valid = false;
};

// Immutable draw description (§3.2). IDs are guest object addresses; the
// renderer resolves them through the GTL registry (host resources are bound
// to the same addresses at materialization).
struct DrawPacket {
  uint64_t packet_id = 0;
  uint64_t frame_id = 0;
  // GUEST-frame ordinal at capture: stated_present_epoch + 1 at packet build
  // — the guest frame IN PROGRESS when the draw was hooked (frame_id above is
  // the HOST pump frame, which over-runs the guest ~1.75x and cannot identify
  // guest frames). Stamped under g_state.mutex in BuildDrawPacketLocked; the
  // epoch and guest_frame_boundary_packet_id advance in the same critical
  // section at the guest Present hook, so packets drained up to a boundary
  // partition EXACTLY by this ordinal. The backlog supersession in the
  // authoritative replay keys on it (drop draw packets of complete guest
  // frames older than the newest complete frame). 0 = stamped before the
  // ordinal existed — always exempt from supersession.
  uint32_t guest_frame_ordinal = 0;
  // Guest address of the draw root that produced this packet (e.g.
  // 0x8221C3E8 DrawPrimitive) — family identity for consumers/diagnostics.
  uint32_t source_symbol = 0;

  uint32_t vertex_shader = 0;
  uint32_t pixel_shader = 0;

  std::array<uint32_t, kMaxRenderTargets> render_targets = {};
  uint32_t depth_target = 0;

  std::array<uint32_t, kMaxTextureSlots> textures = {};
  std::array<uint32_t, kMaxStreamSlots> vertex_buffers = {};
  std::array<uint32_t, kMaxStreamSlots> vertex_offsets = {};
  std::array<uint32_t, kMaxStreamSlots> vertex_strides = {};
  uint32_t index_buffer = 0;

  uint32_t vertex_declaration = 0;

  // Viewport/scissor decoded at the Set calls (structs complete at the call
  // boundary; IDA sub_821F9F58 / sub_821F9C08): viewport = D3DVIEWPORT9
  // {X,Y,Width,Height dwords, MinZ,MaxZ float bits}; scissor = RECT
  // {left,top,right,bottom int dwords}.
  std::array<uint32_t, 6> viewport = {};
  std::array<uint32_t, 4> scissor = {};

  // D3D render states captured from the per-state wrapper roots (slot
  // mapping = FableRenderStateSlotForAddress); valid_mask bit N set when
  // slot N has been written this session. Packets are self-contained per
  // section 3.2 — the executor derives blend/depth/alpha-test from these.
  std::array<uint32_t, kMaxRenderStates> render_states = {};
  uint32_t render_state_valid_mask = 0;

  // Packed sampler state per D3D sampler slot from the SetSamplerState
  // roots; valid_mask bit N set once slot N was written. Section 3.2
  // self-containment: the converter consumes these instead of reading
  // drain-time current state.
  std::array<uint32_t, kMaxSamplerSlots> sampler_states = {};
  uint32_t sampler_state_valid_mask = 0;

  // Stream data pointers resolved at SetStreamSource (object+0x18 raw GPU
  // base + offset, decoded) — the domain the command pipeline consumes.
  std::array<uint32_t, kMaxStreamSlots> vertex_buffer_pointers = {};
  // Index buffer data pointer resolved at SetIndices (object+0x18, decoded).
  uint32_t index_buffer_pointer = 0;
  // Texture fetch base addresses from the pending fetch-constant dispatch
  // (lazy-dispatch draws bind textures via fetch writes, not SetTexture).
  std::array<uint32_t, 32> fetch_bases = {};
  // FULL per-slot texture fetch constants (6 dwords/slot x 32 slots, big-endian
  // dwords byteswapped to host). This is what command.constants.fetch consumes,
  // and every fetch-based classifier/binder (Bink plane detection + the maze's
  // decoded-plane substitution) reads it — fetch_bases alone (dword1) is not
  // enough. Populated by the self-read at the draw's flush point. valid=false
  // means the draw did not self-read fetch (leave command.constants.fetch as the
  // observer/legacy path set it).
  std::array<uint32_t, 192> fetch_dwords = {};
  bool fetch_dwords_valid = false;

  // LIVE control-packet mode word (D3DDevice + 0x2948, host byte order), read
  // POST-FLUSH in the draw root's exit block. Low 3 bits are the Xenos
  // PA_SU_SC_MODE_CNTL cull field (bit0 cull-front, bit1 cull-back, bit2
  // front-face winding) — the exact dword the GPU consumes for this draw.
  //
  // Why a live read instead of the render-state mirror: the mirror is fed only
  // by the SetRenderState_CullMode root, and IDA (2026-07-25) shows that
  // function is reached through the device's D3DRS call table
  // (triple @0x83315E70+168, installed at device+548 by D3D_ConfigureDeviceState)
  // — it has exactly ONE direct caller in the whole binary, the Scaleform UI.
  // So the mirror's coverage depends entirely on that indirect dispatch always
  // landing in our hook, and any miss leaves render_states[19] holding our
  // session DEFAULT of 6 (cull-back / CW-front) — the value the run-263 capture
  // audit found on the zero-fragment glyph draws, while every draw that renders
  // carries 0. The dword here is what the GPU itself consumes for the draw, so
  // it settles the question instead of inheriting the mirror's blind spots.
  // Same shape as the c0-c3 float bank: authority lives in device memory at
  // flush time.
  uint32_t live_mode_control = 0;
  bool live_mode_control_valid = false;

  uint32_t primitive_type = 0;
  uint32_t start_index = 0;
  uint32_t index_count = 0;
  uint32_t base_vertex = 0;
  // MISNOMER, KEPT DELIBERATELY: this holds a VERTEX count, not a primitive
  // count. Do NOT "fix" it by dividing by vertices-per-primitive — the whole
  // pipeline uses the name this way and the semantics are consistent end to end:
  //   * writers: kBeginVertices r5, kDrawPrimitiveUP r5, kDrawPrimitive r6 —
  //     all VertexCount (the guest roots are really D3DDevice_DrawVertices /
  //     DrawVerticesUP; their last arg feeds the PM4 draw initiator's
  //     num_indices field directly, IDA-proven 2026-07-17).
  //   * the only in-GTL consumer computes `bytes = primitive_count *
  //     up_vertex_stride` for the Begin-family geometry pin — correct only for
  //     a vertex count.
  //   * RendererDrawEventSnapshot.draw.primitive_count carries the same
  //     meaning: hook_observer.cpp sets it from a variable literally named
  //     `index_or_vertex_count`. (The scene-batch inline vertex-count helper
  //     that also fed it died with the draw-event builder, 2026-08-03.)
  // So the copy into event.draw.primitive_count is CONSISTENT, not a 3x bug.
  // Renaming this field is a project-wide change; the lie is documented instead.
  uint32_t primitive_count = 0;
  bool indexed = false;
  bool draw_up = false;

  // Nesting depth into the guest's known "scene submit" wrapper functions at
  // capture time (mirrors hook_observer.cpp's g_observer.scene_submit_depth /
  // IsFableSceneSubmitScope, tracked independently in refii_gtl.cpp's
  // OnHookEnter/OnHookExit off the SAME hook stream). Nonzero means this draw
  // was issued from inside real scene-mesh submission, not a UI/private-rect
  // wrapper. BuildDirectCommandFromGtlDrawPacketLocked uses this to assign
  // RendererDrawEventSource::kSceneObject the same way the observer does —
  // without it every GTL-authoritative scene draw defaults to kD3DWrapper/
  // kScratchWrapper and the entire family of kSceneObject-gated title-scene
  // classifiers (tree-billboard/gui-atlas texture-slot masks etc.) is
  // unreachable. See refii-tree-billboard-stride-gate memory for the bug this
  // was found fixing.
  uint32_t scene_submit_depth = 0;

  // Guest pointer + layout for DrawPrimitiveUP-style inline data.
  uint32_t up_vertex_data = 0;
  uint32_t up_vertex_stride = 0;
  uint32_t up_vertex_count = 0;
  // Index source pointer for DrawIndexedUserPrimitives (r5), or the index ring
  // the BeginIndexedVertices root handed back through *ppIndexData.
  uint32_t up_index_data = 0;
  // D3DDevice_BeginIndexedVertices IndexDataFormat (r8), raw and undecoded.
  // Added 2026-07-25 with the split of that root's arg map: it is the only
  // root that states an index width, and without it a consumer of
  // inline_index_data cannot tell 2-byte from 4-byte elements.
  //
  // IDA-verified (sub_82217DB8): only bit 2 is decoded by the guest.
  // (IndexDataFormat & 4) != 0 means 32-BIT indices — it is what ORs 0x800
  // into the PM4 draw initiator (the Xenos INDEX_SIZE bit) and what switches
  // the index ring allocation from (IndexCount+1)>>1 dwords (16-bit) to
  // IndexCount dwords (32-bit). Bits 0-1 land in the index-base word's endian
  // field. Every other root leaves this 0, which reads as 16-bit — the
  // pre-existing assumption those roots already carried.
  uint32_t index_format = 0;

  // Float shader constant banks read from the device object at the draw
  // hook (VS bank at device+120*16, PS bank at device+376*16, 1024 dwords
  // each — the same blocks the observer snapshots per draw event). Raw
  // byte-swapped guest dwords, unsanitized; interpretation happens at
  // materialization. Empty when the device block was unreadable.
  std::vector<uint32_t> vs_float_constants;
  std::vector<uint32_t> ps_float_constants;

  // c0-c3 (WorldViewProjection, 16 dwords) read LIVE from device memory at
  // capture time, independent of vs_float_constants above (2026-07-25).
  // Some quad-wrapper (0x8220A3F0) content delivers its vertex transform via
  // a direct VMX memcpy into device memory (sub_82214E30) that never goes
  // through a PM4 SET_CONSTANT packet, so vs_float_constants' c0-c3 reads as
  // stale/zero for that content even when vs_float_bank_valid is true. This
  // field is the always-fresh alternative — captured unconditionally here
  // (cheap: 16 dwords) but only APPLIED by the converter for the specific
  // shader identities proven to need it (see BuildDirectCommandFromGtl
  // DrawPacketLocked), never broadcast to every 0x8220A3F0 consumer: doing
  // that once produced a solid white screen (run 258) by overwriting
  // unrelated content's correct, intentionally-different transforms.
  std::array<uint32_t, 16> live_vertex_c0_c3 = {};
  bool live_vertex_c0_c3_valid = false;

  // LIVE RB_DEPTHCONTROL (dev+0x2934) read at the draw root, same "the device
  // register at this instant IS what this call states" argument the render
  // target / shader reads below use. The D3D9 depth render states are not a
  // separate authority: IDA (2026-08-02) shows SetRenderState_ZFunc /
  // _ZEnable / _ZWriteEnable / _StencilEnable / _StencilFunc / the stencil-op
  // setters all read-modify-writing THIS word in canonical Xenos layout, then
  // just flagging the dirty bit at dev+0x10. Our render-state slot mirror is a
  // reconstruction of data the guest has already resolved here, and it defaults
  // to zero -- which for ZFUNC means NEVER and silently discarded every
  // depth-tested draw (runs 520-530).
  uint32_t live_depth_control = 0;
  bool live_depth_control_valid = false;

  // Geometry pinned at the DrawIndexedUserPrimitives root (sub_8220A3F0):
  // the caller's source buffers are complete inputs at entry, so the copy
  // here is race-free — unlike the ring destination, which the guest reuses.
  // Raw big-endian guest bytes; the executor swaps at materialization.
  std::vector<uint8_t> inline_vertex_data;
  std::vector<uint8_t> inline_index_data;

  // PHASE 1, CAPTURE-ONLY: the device object's own state at the draw root's
  // ENTRY (see DrawDeviceSnapshot). No consumer reads this — it exists so the
  // divergence census can say, per field class, how far the mirrors above have
  // drifted from what the GPU is actually about to be handed. Nothing about
  // what renders depends on it.
  DrawDeviceSnapshot device_snapshot;
};

// Second packet class (§ "Two packet classes"): sub_82206888 is
// D3DDevice_Resolve — a Xenos EDRAM resolve, not a vertex draw and not a
// blit. It emits its own PM4 quad internally, so there is no vertex/shader
// identity to carry (this is why the family census sees ident=0, geom=0).
//
// IDA-proven arg map (2026-07-17). This SUPERSEDES the 2026-07-14
// "private-rect blit" map, which was inverted — it read r6 as a source
// texture (it is the DESTINATION), r7 as a 4-int source rect (it is a 2-int
// D3DPOINT), and r5 as a dest rect (it is the SOURCE rect):
//
//   r3  pDevice        r4  Flags            r5  pSourceRect (LTRB, RT space)
//   r6  pDestTexture   r7  pDestPoint {x,y} r8  DestLevel
//   r9  DestSliceOrFace r10 pClearColor     f1  ClearZ
//   ClearStencil @ caller_sp+0x5C, pParameters @ caller_sp+0x64
//
// There is NO source texture: a resolve reads the EDRAM surface named by
// Flags&7 (0-3 = RENDERTARGET0..3, 4 = DEPTHSTENCIL) and writes into
// pDestTexture. Placement is a DELTA — the resolve offsets the dest base by
// (pDestPoint - pSourceRect.topLeft).
//
// Deliberately Plume-free (uint32_t/arrays only) so the VFS layer can consume
// it as a compose record. Note the direction correction: this keys on the
// DESTINATION, so it records what the game composed where — it is NOT an
// asset-substitution point (the source is EDRAM, not an asset).
struct ResolvePacket {
  uint64_t packet_id = 0;
  uint64_t frame_id = 0;
  // Root symbol that produced this (0x82206888); family identity.
  uint32_t source_symbol = 0;
  // r4: source-select (bits 0-2) | fragment bits (0x10/0x20/0x40) |
  // clear bits (0x100 CLEARRENDERTARGET, 0x200 CLEARDEPTHSTENCIL).
  uint32_t flags = 0;

  // --- Source: EDRAM, named by flags&7 (NOT a texture) ---
  // The tracked device binding flags&7 selects: render_targets[flags&7] for
  // 0-3, depth_target for 4. Zero means the tracked device state had nothing
  // bound in that slot, which is a device-state tracking gap worth reporting.
  uint32_t source_surface = 0;
  // flags&7 verbatim, so a consumer can tell RT3 from DS without re-deriving.
  uint32_t source_select = 0;
  bool source_is_depth = false;
  // r5 -> D3DRECT {left,top,right,bottom} in render-target space. LTRB, not
  // x/y/w/h: width=right-left, height=bottom-top. The resolve rounds right and
  // bottom UP to a multiple of 8. NULL r5 is part of the ABI and means "full
  // surface" — source_rect_default records that.
  std::array<uint32_t, 4> source_rect = {};
  bool source_rect_default = false;
  bool source_rect_valid = false;

  // --- Destination: the texture written INTO ---
  // r6: guest address of the destination texture header.
  uint32_t dest_texture = 0;
  // Registry identity for dest_texture (0 when Create never registered it):
  // the registry's OBJECT address (Resource::guest_address), NOT a memory
  // base — use dest_base_address below for the memory. (Renamed from the
  // dest_base misnomer 2026-07-19; conflating the namespaces collapsed a
  // source and destination onto one image, run 454.)
  uint32_t dest_registry_object = 0;
  // The console MEMORY this destination header currently NAMES, as last stated
  // by XGOffsetBaseTextureAddress — i.e. Resource::base_address from the
  // registry, not decoded from the fetch constant and not inferred.
  //
  // This is the durable destination identity. The header OBJECT is not one:
  // Fable pools header objects and re-points them per use, so the same object
  // address legitimately names different memory over time. address_generation
  // lets a consumer tell "same object, different memory" from "unchanged", and
  // pins WHICH re-point this packet was captured against.
  //
  // Zero when the registry has no re-point on record for r6 yet.
  uint32_t dest_base_address = 0;
  uint32_t dest_mip_address = 0;
  uint64_t dest_address_generation = 0;
  // The DESTINATION's 6-dword Xenos fetch constant, read from the r6 header at
  // +28 — the same words the resolve itself reads (pDestTexture+28..+48) to do
  // its tiled-base math. Raw and un-normalized; decoding is the consumer's job.
  std::array<uint32_t, 6> dest_fetch_dwords = {};
  // The header was addressable and the fetch constant read back non-zero. Says
  // nothing about whether it decodes to a usable texture — consumers check.
  bool dest_fetch_valid = false;
  // r7 -> D3DPOINT {x,y}. TWO ints, not four — the old map read 4 here and
  // overran the struct. NULL r7 is part of the ABI and means {0,0}.
  std::array<uint32_t, 2> dest_point = {};
  bool dest_point_default = false;
  bool dest_point_valid = false;
  // r8 / r9.
  uint32_t dest_level = 0;
  uint32_t dest_slice_or_face = 0;

  // --- Optional clears (resolve doubles as a clear; EndTiling uses this to
  // prime EDRAM for the next tile, which is why flags&0x300 is common) ---
  // r10 -> D3DVECTOR4, raw host-order float dwords; only when flags & 0x100.
  std::array<uint32_t, 4> clear_color = {};
  bool clear_color_valid = false;
  // f1 / stack; only meaningful when flags & 0x200.
  float clear_z = 0.0f;
  uint32_t clear_stencil = 0;

  // Tracked device targets at the resolve, for cross-checking source_surface.
  std::array<uint32_t, kMaxRenderTargets> dest_targets = {};
  uint32_t dest_depth = 0;

  // --- What the resolve tells the GPU about its destination ---
  //
  // DERIVED, not read back. The RB_COPY_* device shadow (device+0x2A18..0x2A24)
  // looks like the obvious source and is NOT usable: D3DDevice_Resolve disarms
  // the copy state before returning (`li r27, 0` @0x822073E4 feeding
  // `stw r27, 0x2A18/0x2A24` @0x82207638/0x8220763C), so the shadow reads ZERO
  // at exit and equally zero at the next resolve's enter. Run 266 measured
  // exactly that -- all four dwords zero on every resolve. The data is
  // DESTROYED, not stale, so no hook placement recovers it. Do not retry.
  //
  // These instead reproduce the guest's own derivation from inputs this packet
  // already holds: `flags` (the Flags argument) and `dest_fetch_dwords` (the
  // destination texture header). See docs/refii_resolve_fields.md 3.1/3.2.
  uint32_t copy_dest_format = 0;    // Xenos colour format after the remap below
  uint32_t copy_dest_number = 0;    // numeric type; 7 = the float-format set
  // RB_COPY_CONTROL exactly as the guest builds it, INCLUDING the borrow its
  // `- 0x10` produces when the caller names no fragment. Kept for reference
  // only — do NOT read fields out of it, they are corrupted in the common case
  // (Flags=0 gives 0xFFFFFFF0). Read from `flags` via the accessors instead.
  uint32_t copy_control = 0;
  // 0-based Xenos sample index, normalised from the Flags fragment select
  // without the guest's borrow. 0 when the caller named no fragment.
  uint32_t copy_sample_select = 0;
  // Signed 2^bias scale the resolve applies to colour. Nonzero means a consumer
  // that cannot apply it must refuse, not ignore — the failure is silent.
  int32_t copy_dest_exp_bias = 0;
  bool copy_fields_valid = false;
};

// The destination texture's Xenos format, from its own fetch constant: dword1
// bits 0-5 = format. (Bits 12-31 are the base address -- the same dword the
// existing `& 0xFFFFF000` base decode already relies on, so this layout is
// already load-bearing elsewhere.)
inline uint32_t ResolveDestTextureFormat(
    const std::array<uint32_t, 6>& dest_fetch_dwords) {
  return dest_fetch_dwords[1] & 0x3Fu;
}

// The guest's OWN texture-format -> copy-format remap, transcribed from the
// fall-through compare chain at 0x82206AA8-0x82206B1C (`mr r11, r30` @0x82206A98
// loads the incoming format; each arm branches to the join at loc_82206B1C).
// COMPLETE: any format not listed falls through unchanged.
inline uint32_t ResolveCopyDestFormat(uint32_t dest_texture_format) {
  switch (dest_texture_format) {
    case 0x36u: return 0x07u;
    case 0x37u: return 0x10u;
    case 0x38u: return 0x11u;
    case 0x1Bu: return 0x1Eu;
    case 0x1Cu: return 0x1Fu;
    case 0x1Du: return 0x20u;
    case 0x16u:
    case 0x17u: return 0x06u;
    default:    return dest_texture_format;
  }
}

// Second pass at 0x82206B34-0x82206B74: these copy formats force the numeric
// type to 7. The "is this really the number field" question is now settled --
// the `li r10, 7` value survives `insrwi r10, r9, 29,0` in r10's low 3 bits,
// `insrwi r11, r10, 26,0` places it just above the 6-bit format, and the final
// `slwi r11, r11, 7` lands format at bits 7-12 and this at bits 13-15.
inline bool ResolveCopyDestFormatUsesFloatNumber(uint32_t copy_dest_format) {
  switch (copy_dest_format) {
    case 0x1Eu: case 0x1Fu: case 0x20u:
    case 0x24u: case 0x25u: case 0x26u:
    case 0x39u: case 0x3Fu:
      return true;
    default:
      return false;
  }
}

// Reproduce RB_COPY_CONTROL's low half exactly as the guest builds it:
//     andi. r11, r25, 0x377   (r25 = Flags)
//     addi  r11, r11, -0x10
// The D3DRESOLVE flag bits map 1:1 onto RB_COPY_CONTROL bit positions, which is
// why a plain mask works: 0x1-0x7 source select (bits 0-2), 0x10/0x20/0x40
// fragment select (bits 4-6), 0x100 CLEARRENDERTARGET = color_clear_enable
// (bit 8), 0x200 CLEARDEPTHSTENCIL = depth_clear_enable (bit 9) — all four
// landing exactly where the register wants them.
//
// The `- 0x10` is reproduced rather than "cleaned up": with Flags=0 (which
// Fable really does pass — Render_ResolveBoundTarget calls
// D3DDevice_Resolve(device, 0, ...)) it underflows to 0xFFFFFFF0, and that
// underflowed word is what the hardware actually receives. Do not sanitise it
// into something tidier; extract fields from it the way the GPU would.
inline uint32_t ResolveCopyControlFromFlags(uint32_t flags) {
  return (flags & 0x377u) - 0x10u;
}

// *** READ FIELDS FROM `Flags`, NOT FROM THE RECONSTRUCTED CONTROL WORD. ***
//
// Run 268 proved why. The `- 0x10` BORROWS whenever no fragment bit is set:
//   Flags=0x300 -> 0x2F0  (borrow eats bit 8, losing CLEARRENDERTARGET)
//   Flags=0x000 -> 0xFFFFFFF0  (borrows all the way; every field junk)
// Only Flags=0x14 (which carries FRAGMENT0=0x10) survives the subtraction
// intact. Fable really does call D3DDevice_Resolve with Flags=0
// (Render_ResolveBoundTarget), so this is the common case, not an edge case.
//
// The subtraction is a 1-based -> 0-based fragment normalisation
// (FRAGMENT0=0x10 -> sample 0), and it presumes a caller that always passes a
// fragment flag. The guest's own behavioural decisions do NOT come from the
// resulting word — they branch on `Flags` directly:
//   0x82206BFC  rlwinm. r11, r25, 0,22,22  (bit 9) -> D3D__SetDepthStencilClear
//   0x82206C28  rlwinm. r11, r25, 0,23,23  (bit 8) -> D3D_SetColorClear
// so the corrupted bits 8/9 in the control word never drive anything. Refii
// must read Flags the same way.


// Bits 4-6 of Flags: the D3DRESOLVE fragment select, as the CALLER states it.
// FRAGMENT0=0x10 -> 1, FRAGMENT1=0x20 -> 2, ALLFRAGMENTS=0x40 -> 4; 0 = the
// caller named no fragment. This is the EDRAM multisample resolve mode and the
// field that decides whether a higher-sample internal target resolves
// correctly, so a shader resolve must read it rather than assume sample 0.
inline uint32_t ResolveFragmentSelectFromFlags(uint32_t flags) {
  return (flags >> 4u) & 0x7u;
}

// The 0-based Xenos sample index the hardware field wants. Reproduces the
// guest's `- 0x10` normalisation WITHOUT its borrow: an unstated fragment means
// sample 0, which is what a single-sampled resolve needs and what the borrow
// was accidentally turning into 6 (ALLFRAGMENTS).
inline uint32_t ResolveSampleIndexFromFlags(uint32_t flags) {
  const uint32_t fragment = ResolveFragmentSelectFromFlags(flags);
  return fragment != 0u ? fragment - 1u : 0u;
}

// Bit 8 / bit 9 of Flags — D3DRESOLVE_CLEARRENDERTARGET / _CLEARDEPTHSTENCIL.
// The resolve doubles as a clear, which is why Flags&0x300 is common at tile
// boundaries. Read from Flags exactly as 0x82206C28 / 0x82206BFC do.
inline bool ResolveClearsColorFromFlags(uint32_t flags) {
  return (flags & 0x100u) != 0u;
}
inline bool ResolveClearsDepthFromFlags(uint32_t flags) {
  return (flags & 0x200u) != 0u;
}

// PINNED, no longer inferred: the exponent bias is the TOP 6 BITS OF `Flags`.
//
//   0x82206A94  srwi   r9, r25, 26     ; r9 = Flags >> 26   (r25 = Flags)
//   0x82206B78  insrwi r9, r7, 1,23    ; r9 |= swap_bit << 8
//   0x82206B80  andi.  r9, r9, 0x13F   ; keep bits 0-5 and bit 8
//   0x82206B84  insrwi r10, r9, 29,0   ; r10 = number(low 3) | r9 << 3
//   0x82206B8C  insrwi r11, r10, 26,0  ; r11 = format(low 6) | r10 << 6
//   0x82206B94  slwi   r11, r11, 7     ; payload starts at DEST_INFO bit 7
//
// so r9's bits 0-5 land at DEST_INFO bits 16-21 (exp_bias) and r9's bit 8 lands
// at bit 24 (swap). r9 is not clobbered anywhere between 0x82206A94 and
// 0x82206B78 — checked instruction by instruction. r25 = Flags is confirmed
// twice over: 0x82206BFC tests its bit 9 then calls D3D__SetDepthStencilClear,
// and 0x82206C28 tests its bit 8 then calls D3D_SetColorClear, matching
// D3DRESOLVE_CLEARDEPTHSTENCIL (0x200) and _CLEARRENDERTARGET (0x100).
//
// SIGNED: the resolve scales colour by 2^bias. A wrong bias is silent — the
// image is simply the wrong brightness — so a consumer that cannot apply it
// must REFUSE on a nonzero value rather than ignore it.
inline int32_t ResolveExpBiasFromFlags(uint32_t flags) {
  const uint32_t raw = (flags >> 26u) & 0x3Fu;
  return (raw & 0x20u) != 0u ? static_cast<int32_t>(raw) - 64
                             : static_cast<int32_t>(raw);
}

// NOT PINNED — endian and swap are assembled from registers (r24, r29) whose
// provenance is not yet traced:
//   swap:   r7 = ((r29 & 0xE) != 0), via rlwinm/cntlzw/extrwi/xori @0x82206A54-AA4
//   endian: extrwi r9, r24, 2,24  ->  (r24 >> 6) & 3   @0x82206B90
// Both affect byte order of the resolved texels. Until they are traced, a
// shader resolve should assume the common case and refuse anything that
// disagrees, rather than guess. Do not invent values for these.

// Third packet class: the D3DDevice clear roots (sub_821E2FE0 = ClearF with an
// explicit D3DCLEAR mask; sub_821EF830 = ClearRect, a color-slot-0 clear).
// Neither a vertex draw nor a resolve — a distinct primitive that fills the
// bound targets. Captured verbatim from the wrapper args + the tracked RT/depth
// binding so the submission pump can drive clears IN ORDER alongside DrawPackets
// (packet_id is drawn from the same counter, giving one global order). §3.4/step
// 5 submission cutover: with draws + clears both packet-sourced the observer
// batch is no longer the clear producer.
struct ClearPacket {
  uint64_t packet_id = 0;
  uint64_t frame_id = 0;
  uint32_t source_symbol = 0;  // 0x821E2FE0 (ClearF) or 0x821EF830 (ClearRect)
  // D3DCLEAR mask: low 4 bits = color slots, 0x10 = depth, 0x20 = stencil.
  uint32_t flags = 0;
  // Clear color as raw host-order float dwords (LoadGuestU32 unswaps the guest
  // bits); bit_cast to float at materialization. color_valid=false when the
  // pointer was null/unreadable.
  std::array<uint32_t, 4> color = {};
  bool color_valid = false;
  float depth = 1.0f;
  uint32_t stencil = 0;
  // Device scissor (LTRB) tracked at call time; all-zero = full-target clear.
  std::array<uint32_t, 4> scissor = {};
  // The CALL's own clear rect (LTRB; rect_valid=false = full-target): ClearF
  // r5 is a rect POINTER (decoded when readable), ClearRect r4-r7 are
  // x/y/w/h. The device scissor above is ambient state, not the clear's rect
  // — section 3.2 self-containment requires the argument itself.
  std::array<int32_t, 4> rect = {};
  bool rect_valid = false;
  std::array<uint32_t, kMaxRenderTargets> render_targets = {};
  uint32_t depth_target = 0;

  // GPU-hardware predication state at the ClearF/ClearRect call (2026-07-25,
  // IDA-traced; re-verified 2026-07-26: D3DDevice_SetPredication, 0x822655F0,
  // writes a REAL PM4 type-3 SET_BIN_SELECT_LO packet (0xC0006200) into the
  // ring AND mirrors its raw mask argument into pDevice->m_ZFilter[42]
  // (dev+0x2AB4) as a plain DWORD). Nonzero here means the
  // clear that follows is one of the pair D3DDevice_ClearF calls inside
  // Render_BeginPredicatedTileClears_INFERRED (0x821C6478), which exist
  // solely to avoid re-clearing EDRAM tiles a PREVIOUS tiling pass already
  // initialized -- an EDRAM-tile-size-constraint optimization that has no
  // analog in Refii's non-tiled, full-target Vulkan rendering. Replaying
  // these as unconditional full-target clears (the only option without true
  // GPU predication/conditional-rendering support) produced a reproducible
  // solid white storybook-menu screen; see
  // refii-predicated-clear-white-screen.md.
  uint32_t predication_mask = 0;
};

// ---------------------------------------------------------------------------
// Fourth packet class: Xenos HARDWARE-TESSELLATOR patch draws.
//
// CAPTURE-ONLY (2026-07-25). Nothing consumes this stream; it exists so a
// future rendering pass is designed from measured fields instead of guesses.
//
// Fable's terrain and water do NOT go through any of the eight hooked D3D draw
// roots. They call two ring writers that emit PM4 DRAW_INDX packets straight
// into the command ring, so Refii currently sees ZERO draws from either system:
//
//   TERRAIN  D3D_EmitDrawIndexedPackets_INFERRED    0x82207AF8
//            header 0xC0032201 (type-3, 4 body dwords, opcode 0x22 DRAW_INDX,
//            predicated bit 0). Sole caller sub_82A8F1D8, itself reached from
//            sub_8226D908 -> Render_ScreenSpaceIndexedPass_INFERRED 0x82236508
//            -> sub_822059E8. Content proven by the "bg patch %d, opaque" /
//            "bg patch %d, blended" sprintfs (0x8200169C / 0x82001B8C).
//
//   WATER    D3D_EmitDrawPrimitivePackets_INFERRED  0x8221C898
//            header 0xC0012201 (2 body dwords -> auto-index, no index buffer).
//            Sole caller sub_8221FC40, reached from sub_821DD618 ->
//            sub_8223DB60. Water attribution is INFERRED (counters "Water
//            patches before sorting: %d" 0x820F85DC / "Water quads rendererd:
//            %d" 0x820F8620 sit on that chain); the TESSELLATED-PATCH nature is
//            VERIFIED from the registers below.
//
// Both set VGT_OUTPUT_PATH_CNTL (Xenos reg 0x2284) path-select to 1 =
// TESSELLATE and VGT_GROUP_PRIM_TYPE (0x2289) to 14, and both emit a
// VGT_DRAW_INITIATOR whose PRIM_TYPE field is 0x12 -- a QUAD PATCH, outside the
// D3DPRIMITIVETYPE identity table (which ends at 13). That is why none of this
// can ride on DrawPacket: `primitive_type` there is a D3DPRIMITIVETYPE,
// `index_count` counts indices (here it counts PATCHES), and in adaptive
// tessellation the bound "index buffer" carries per-edge FLOAT tessellation
// factors, not indices. A separate stream is also inert by construction, which
// is what a capture-only pass requires.
struct TessellatedPatchDrawPacket {
  uint64_t packet_id = 0;
  uint64_t frame_id = 0;
  // 0x82207AF8 (terrain, indexed) or 0x8221C898 (water, auto-index).
  uint32_t source_symbol = 0;
  uint32_t caller_lr = 0;
  uint32_t device = 0;

  // --- call arguments (IDA-pinned per emitter; the two arg maps DIFFER) ---
  // Terrain sub_82A8F1D8: (r3 dev, r4 0x12, r5 0, r6 baseIndex, r7 count).
  // Water   sub_8221FC40: (r3 dev, r4 18,   r5 startVertex, r6 1 patch).
  uint32_t arg_primitive_type = 0;  // r4; 0x12 at BOTH call sites
  uint32_t arg_index_offset = 0;    // terrain r6 / water r5
  uint32_t arg_count = 0;           // terrain r7 / water r6
  bool indexed = false;             // terrain true (DMA), water false (auto)

  // --- Xenos register shadow blocks read out of the device object ---
  // The shadow bases are VERIFIED: each is the `pPacket` argument the emitter
  // hands D3D_SetPending_RenderStates alongside a literal Xenos `regBase`, and
  // that function (0x8221C7D8) copies shadow[i] into register regBase+i. Block
  // lengths are the gaps between consecutive shadow bases.
  //   dev+0x2880 -> reg 0x2000 (RB_SURFACE_INFO / RB_COLOR*_INFO / RB_DEPTH_INFO)
  //   dev+0x28CC -> reg 0x2100 (VGT_MAX/MIN_VTX_INDX, VGT_INDX_OFFSET, PA_SC_*)
  //   dev+0x2934 -> reg 0x2200 (SQ_PROGRAM_CNTL and friends)
  //   dev+0x2964 -> reg 0x2280 (the VGT tessellation block, below)
  //   dev+0x29B8 -> reg 0x2300 (RB_MODECONTROL / RB_DEPTHCONTROL / blend)
  //   dev+0x2A50 -> reg 0x2380 (RB_COPY_*)
  std::array<uint32_t, 19> rb_surface_regs = {};  // 0x2000 + i
  std::array<uint32_t, 21> pa_vgt_regs = {};      // 0x2100 + i
  std::array<uint32_t, 12> sq_regs = {};          // 0x2200 + i
  std::array<uint32_t, 21> vgt_tess_regs = {};    // 0x2280 + i
  std::array<uint32_t, 38> rb_mode_regs = {};     // 0x2300 + i
  std::array<uint32_t, 8> rb_copy_regs = {};      // 0x2380 + i
  bool device_regs_valid = false;

  // Same 6-word D3DVIEWPORT9 layout DrawPacket::viewport carries, from the same
  // g_state.device.viewport mirror. Added 2026-08-03: without it a tess command
  // reached DirectCommandCanMaterializePrivateRectTargetLocked with no
  // viewport, no scissor and no private-rect corners, so the extent test at the
  // end of that function always returned false and a tessellated draw could
  // NEVER mint a render target -- terrain was unmintable by construction.
  std::array<uint32_t, 6> viewport = {};

  // Named views into vgt_tess_regs, indices VERIFIED against the emitters'
  // literal stores (terrain 0x82207B30-0x82207BE4, water 0x8221C8C4-0x8221C90C):
  //   [4]  0x2284 VGT_OUTPUT_PATH_CNTL   both do `&= ~3; |= 1` (TESSELLATE)
  //   [5]  0x2285 VGT_HOS_CNTL           terrain BRANCHES on (&3)==2; never set here
  //   [6]  0x2286 VGT_HOS_MAX_TESS_LEVEL
  //   [7]  0x2287 VGT_HOS_MIN_TESS_LEVEL
  //   [8]  0x2288 VGT_HOS_REUSE_DEPTH
  //   [9]  0x2289 VGT_GROUP_PRIM_TYPE    written 14 by both
  //   [10] 0x228A VGT_GROUP_FIRST_DECR   4 when HOS_CNTL&3==2 else 1
  //   [11] 0x228B VGT_GROUP_DECR         same
  //   [12] 0x228C VGT_GROUP_VECT_0_CNTL
  //   [13] 0x228D VGT_GROUP_VECT_1_CNTL
  //   [14] 0x228E VGT_GROUP_VECT_0_FMT_CNTL
  //   [15] 0x228F VGT_GROUP_VECT_1_FMT_CNTL
  // These are the tessellation FACTORS/topology contract a future expansion
  // pass has to honour; they are only correct at hook EXIT, because the
  // emitter writes them in its own prologue.

  // --- the PM4 the call actually wrote ---
  // Ring write pointer (device+0x30) sampled at enter and at exit. The dwords
  // between them ARE this draw: the D3D_SetPending_* register loads followed by
  // the DRAW_INDX packet(s). Captured at exit; ring_capture_valid is false when
  // KickOff/D3D_SetPending_Split wrapped the ring inside the call (the range is
  // then not contiguous and is NOT guessed at).
  uint32_t ring_begin = 0;
  uint32_t ring_end = 0;
  bool ring_capture_valid = false;
  std::vector<uint32_t> emitted_dwords;  // host order

  // First DRAW_INDX found in emitted_dwords, decoded. Zero when not found.
  uint32_t draw_header = 0;         // 0xC0032201 terrain / 0xC0012201 water
  uint32_t draw_viz_query = 0;      // body[0]
  uint32_t draw_initiator = 0;      // body[1] = VGT_DRAW_INITIATOR
  uint32_t draw_index_base = 0;     // body[2], indexed form only
  uint32_t draw_index_size = 0;     // body[3], indexed form only
  uint32_t draw_packet_count = 0;   // how many DRAW_INDX packets this call wrote

  // --- index source (terrain only; water is auto-index) ---
  // device+0x3094 is the bound index buffer (VERIFIED at D3DDevice_SetIndices,
  // 0x8219CC30, whose only store is `stw pIndexData, 0x3094(pDevice)`). The
  // emitter reads flags at +0 (bit 0 set = 32-bit) and the GPU base at +0x18.
  uint32_t index_buffer_object = 0;
  uint32_t index_buffer_flags = 0;
  uint32_t index_buffer_gpu_base = 0;
  bool index_32bit = false;

  // --- bindings at the draw ---
  uint32_t vertex_shader = 0;
  uint32_t pixel_shader = 0;
  std::array<uint32_t, kMaxRenderTargets> render_targets = {};
  uint32_t depth_target = 0;
  std::array<uint32_t, kMaxTextureSlots> textures = {};
  // device+0x2940 is NOT predication (mislabelled until 2026-07-26). It is
  // GPU_CONTROLPACKET::HiControl -- the IDB types dev+0x2934 as
  // m_ControlPacket (GPU_CONTROLPACKET) and HiControl sits at +0xC. It is the
  // shadow of Xenos register 0x2203, the Hi-Z / Hi-Stencil early-rejection
  // control word. VERIFIED writers, all storing to 0x2940:
  //   D3DDevice_SetRenderState_HiStencilEnable       0x822A61A8 (bit 3)
  //   D3DDevice_SetRenderState_HiStencilWriteEnable  0x8226AE80 (bit 2)
  //   D3DDevice_SetRenderState_HiStencilFunc         0x821E0C00 (bit 5)
  //   D3DDevice_SetDepthStencilSurface               0x822645E0 / 0x822646E4
  //   D3D_SetPending_HiZEnable                       0x8221AE7C (bit 0) /
  //                                                  0x8221AF34 (bit 1)
  // D3D_SetPending_Predicated (0x822154B0) takes it as its THIRD argument
  // (D3DDevice_BeginVertices loads it with `lwz r5, 0x2940(r31)` at
  // 0x82206050) and, per tile, re-emits it to register 0x2203 as
  // (perTileHiZBase << 17) | (value & 0x1FFFF) with bit 0 conditionally
  // cleared. The "Predicated" in that function's name describes WHEN it runs,
  // not what this word holds.
  uint32_t hi_z_control = 0;

  // Real GPU predication state, sourced from the fields both predication
  // writers agree on (VERIFIED 2026-07-26 against D3DDevice_SetPredication
  // 0x822655F0 and D3D_SetTilePredication_INFERRED 0x82BA1D40, which are the
  // same routine with the ring-direct variant taking a 64-bit mask):
  //   dev+0x2AB4 raw app mask LO, stored verbatim on EVERY call including the
  //              disable call (which stores 0) -- so nonzero here means "this
  //              draw is predicated", the same test ClearPacket uses.
  //   dev+0x2AB8 raw app mask HI. Always 0 from D3DDevice_SetPredication,
  //              which hardcodes `*(&m_ZFilter[46]) = 0`; captured so the
  //              "effective predication is 32-bit" assumption stays falsifiable.
  //   dev+0x31A4 the RESOLVED value actually emitted, as the payload of the
  //              PM4 type-3 SET_BIN_SELECT_LO (0xC0006200) both writers push
  //              into the ring on the spot. Equals the app mask when
  //              predication is on and 0xFFFFFFFF ("select every bin") when it
  //              is off, so unlike the raw mask it is never ambiguous with an
  //              untouched device.
  //   dev+0x31AC the enable flag (1/0). Corroborated independently by
  //              D3DDevice_SetDepthStencilSurface, which reads it at
  //              0x822646F8 to decide whether its Hi-Z fast path is legal.
  // Predication itself is bin-based: tile i owns bins {2i, 2i+1}, 0x15555555
  // selects all even bins ("if" variant) and 0x2AAAAAAA all odd ("else").
  uint32_t predication_mask = 0;     // dev+0x2AB4, raw LO
  uint32_t predication_mask_hi = 0;  // dev+0x2AB8, raw HI
  // dev+0x31A4, the resolved SET_BIN_SELECT_LO payload.
  uint32_t predication_bin_select = 0;
  bool predication_enabled = false;  // dev+0x31AC != 0
  // device+0x2ABC bit 0 selects the SECOND emit block, which wraps the draw in
  // a CRingAllocList patch entry plus 0xC0006000 / 0xC0015A01 / 0xC0004601
  // packets -- the predicated-tiling variant.
  bool tiled_variant = false;
  // device+0x2F9B indexes the viz-query table at device+0x2F5A; the byte read
  // out of it becomes body[0] masked with 0x13F.
  uint32_t viz_query_index = 0;

  // Vertex/texture fetch constants, device+0x480 + slot*24, 6 dwords each
  // (VERIFIED: D3D_SetPending_FetchConstants 0x8221D1B0 loads exactly there and
  // emits them at Xenos reg base 0x4800). This is where the patch VERTEX data
  // and the sampled heightfield/atlas come from -- there is no D3D stream
  // source on these paths.
  std::array<uint32_t, 192> fetch_dwords = {};
  bool fetch_dwords_valid = false;

  // Load-bearing vertex shader float constants, resolved through the game's
  // binding table (VERIFIED: sub_822239F8 indexes g_ShaderBindingTable at
  // 0x834BDED8 with a 12-byte stride, takes reg=entry[0]/count=entry[1], and
  // writes the float at device + 0x780 + 16*reg -- device+0x780 being the VS
  // ALU constant shadow that the emitter uploads at Xenos reg base 0x4000).
  // So the numbers below are BINDING INDICES, not register numbers.
  //   terrain 332: sub_82A8F1D8 sets it to (float)first-set-bit of the sub-tile
  //                visibility mask, immediately before each emit (0x82A8F24C).
  //   water   343: float4 from patchRecord+16 (0x822202FC).
  //   water   396: scalar, flt_833216C8 or -1.0 per sub-quad (0x82220384).
  //   water   342: NOT a constant -- it is fed to
  //                NGraphics_CShaderManager_VSConstantIndexToSamplerStage and
  //                the result is a SetTexture stage. Recorded raw, unresolved.
  struct ShaderConstantProbe {
    uint32_t binding_index = 0;
    uint32_t constant_register = 0;
    uint32_t constant_count = 0;
    std::array<uint32_t, 4> value = {};  // host order; bit_cast to float
    bool valid = false;
  };
  std::array<ShaderConstantProbe, 4> constant_probes = {};

  // Float constant banks read at hook EXIT (post-flush, same base registers
  // the DrawPacket exit self-read uses: VS dev+0x780 / reg 0x4000, PS
  // dev+0x1780 / reg 0x4400 — sub_8221DE68). Captured for the T3 execution
  // path: the tess-variant VS consumes the same c-registers as the base VS
  // (patch placement, WVP, atlas transforms), so the executor binds these
  // exactly like DrawPacket banks. Empty when the device block was unreadable.
  std::vector<uint32_t> vs_float_constants;
  std::vector<uint32_t> ps_float_constants;

  // Stable identity of the SHAPE (see the diagnostic): everything a renderer
  // would have to branch on, hashed. Used to log once per distinct shape
  // instead of once per draw.
  uint64_t shape_key = 0;

  // PHASE 1, CAPTURE-ONLY (see DrawDeviceSnapshot). Taken at the emitter's
  // ENTRY and carried across by PendingTessellatedPatchCapture, because these
  // two roots also drain the pending masks — everything else in this packet is
  // read at exit, but the masks do not survive that far.
  DrawDeviceSnapshot device_snapshot;
};

struct Stats {
  // Guest GPU fences retired by RetireGuestGpuFenceLocked. Zero means the
  // writeback block is never advancing and every D3D blocker is still parked.
  uint64_t guest_fences_retired = 0;
  // Times a guest wait on the *(dev+0x2A90) write-back block drove a
  // retirement (BlockOnSecondaryPosition / BlockOnFence enter). Non-zero means
  // the run-571 self-deadlock path is being broken; zero in a run that reaches
  // gameplay means the blocker roots are not dispatching to the GTL.
  uint64_t blocker_retire_attempts = 0;
  // Emulated Render_FrameCompletionCallback fires. Zero means the frame-sync
  // clock is still stopped and ALL rendered content is frozen regardless of the
  // present rate — see AdvanceGuestFrameCompletionLocked.
  uint64_t guest_frame_completions = 0;
  uint64_t resources_registered = 0;
  // Registrations that landed on an address already tracked, where
  // content_generation was carried forward + 1 instead of reset to 0. Expected
  // to be LARGE: Fable re-inits headers on pooled, re-pointed textures, so this
  // is the normal path for streamed content, not an anomaly.
  uint64_t resources_reregistered_generation_carried = 0;
  // Registrations where the header's 6-dword fetch block at object+28 was
  // readable and stored on the Resource. The gap between this and
  // resources_registered is how many resources identity consumers could NOT be
  // ported onto gtl::QueryResourceIdentity for (migration step 6).
  uint64_t resource_fetch_dwords_stated = 0;
  // legacy_external_state_feed_calls DELETED 2026-08-02 with the last external
  // state feed (OnSamplerStateDispatch): run 503 measured it 0 across a
  // gameplay-covering run while self_read_sampler=107k.
  // Feed-dependence census at the draw-root exit self-read. Gates deleting the
  // remaining five observer feeds (shader bind, RT bind, depth bind, and by
  // extension the fetch/constant banks). The decisive counter is
  // exit_feed_load_bearing: it must be 0 across a gameplay-covering run before
  // those feeds can go, because the self-read overwrite is NONZERO-ONLY and a
  // zero fresh read silently keeps whatever the feed left behind.
  uint64_t exit_self_read_filled = 0;
  uint64_t exit_feed_load_bearing = 0;
  uint64_t exit_both_absent = 0;
  uint64_t exit_feed_disagreed = 0;
  // Draws where the device stated an UNBIND (a successful read of 0) and the
  // packet still carried a previous binding. Before 2026-07-26 the nonzero-only
  // overwrite silently kept the stale binding here, so a deliberate unbind --
  // e.g. the engineered NULL-pixel-shader depth-only path, or
  // Render_BindSingleRenderTarget NULLing slots 1..3 -- never reached the
  // packet. Non-zero here means the fix is doing work.
  uint64_t exit_stated_unbind_applied = 0;
  uint64_t resources_destroyed = 0;
  // D3DResource_Release calls that did NOT destroy (refcount > 0 afterwards).
  // These used to erase the resource anyway — the eviction bug that kept
  // resolve destinations out of the registry (fixed 2026-07-17). Expect this to
  // dwarf resources_destroyed; if it is 0 while releases happen, the exit
  // pairing is not seeing the real return value.
  uint64_t resources_release_survived = 0;
  // Releases whose pThis was not a plausible guest object. Plume's emulator
  // returns a FALSE ZERO for these (handled=true, result=0) — a rejection, not
  // a destroy — so they must not be read as "refcount reached zero".
  uint64_t resources_release_bad_shape = 0;
  // D3DResource_Release ENTERS. Without this, resources_release_survived == 0 is
  // ambiguous between "the root never dispatches", "it always destroys", and
  // "bad_shape ate them all" — the counter-conflation trap that has cost this
  // project several runs.
  uint64_t resources_release_events = 0;
  // Registrations lost because the object came back 0. Split by cause, because
  // "a create silently vanished" is exactly how the XGSetTextureHeader +0x7C
  // offset bug hid: a wrong stack offset yields object==0 and nothing counted it.
  uint64_t creates_dropped_null_object = 0;
  // Header-init calls whose out-pointer was NULL. This is NORMAL, not a defect:
  // it is the XDK size-query idiom — XGSetTextureHeader(..., pTexture=NULL,
  // &baseSize, &mipSize) asks for the size without writing a header, and the
  // caller then allocates and calls again with a real pointer. Measured 626 of
  // these in run 443 with addr_ok=1 and Width=256, i.e. ordinary calls.
  // Registering nothing for them is correct.
  uint64_t header_inits_size_query = 0;
  // Tenancy bumps applied at a header root's EXIT (one per new tenant of an
  // address).
  uint64_t header_init_tenancy_bumps = 0;
  // How many of those found the ENTER-side fetch read had captured DIFFERENT
  // dwords — i.e. the outgoing tenant's block. This is the direct size of the
  // stale-identity population that used to be published as fact.
  uint64_t header_init_fetch_corrected = 0;
  // Begin-family (inline-UP) slots where the guest STATED a texture through
  // D3DDevice_SetTexture and the fetch block claimed nothing, so the stated
  // value was kept instead of being cleared. This is the black-gameplay fix:
  // the tonemap composite's scene colour is exactly such a slot. A count of 0
  // in a gameplay run means the fix is not reaching the draw that needs it.
  uint64_t stated_texture_kept_over_empty_fetch = 0;
  // Of those, how many ALSO had a non-zero raw fetch base — i.e. the two
  // sources of truth for one slot disagree in principle. This is the run-206
  // shape (a non-zero packet claim refusing the identity match). Non-zero here
  // is not automatically a defect; it is the number to look at if world
  // textures start dropping again.
  uint64_t stated_texture_disagrees_fetch = 0;
  // kSetTexture arrival census. `slot0` is the tonemap's scene-colour sampler.
  // If nonzero_binds is large while the packet still carries no textures, the
  // loss is between this root and BuildDrawPacketLocked, not in the guest.
  uint64_t set_texture_nonzero_binds = 0;
  uint64_t set_texture_slot0_binds = 0;
  uint64_t set_texture_slot_out_of_range = 0;
  // Draws whose viewport was taken from the device shadow (dev+0x3168..0x317C),
  // i.e. the CLAMPED value D3D_SetViewport actually committed, instead of the
  // unclamped D3DVIEWPORT9 the shim was handed. `rejected` counts shadows that
  // were unreadable or zero-extent (SetViewport with no bound target early-outs
  // without storing anything at 0x821F9D44).
  uint64_t viewport_from_device_shadow = 0;
  uint64_t viewport_shadow_rejected = 0;
  // Header-init calls where the out-pointer could NOT be read: the computed
  // stack address failed the range check. THIS is the defect counter — it is the
  // shape a wrong stack offset takes (the XGSetTextureHeader +0x7C bug). Must
  // stay 0; non-zero means an arg map is wrong.
  uint64_t header_inits_unreadable_out_pointer = 0;
  // Hook symbols the GTL was handed but could not map to a root (SymbolAddress
  // returned 0). This is how BOTH the XGOffsetBaseTextureAddress and
  // rex_lhGfx_CreateGPUState drops stayed invisible: unmapped roots never
  // reached the switch, so the unknown-root census could not see them either.
  uint64_t unmapped_symbol_events = 0;
  uint64_t state_events = 0;
  // Render-state dispatches the GTL decoded ITSELF from the guest record,
  // rather than receiving pre-decoded from the observer. Non-zero proves the
  // GTL no longer depends on the observer for at-draw render states.
  uint64_t self_read_state_dispatches = 0;
  // Times the Begin-family exit arm was entered, regardless of outcome. Exists
  // purely so a zero in self_read_exit_begin_* can be read unambiguously.
  uint64_t self_read_exit_begin_arm_entries = 0;
  // OnStateDispatch early-return census. state_dispatch_self_read fires ~once
  // per run while the observer sees ~178k state events, so the decode is
  // rejecting almost everything. These name WHICH branch rejects it instead of
  // leaving it to guesswork.
  uint64_t state_dispatch_calls = 0;        // reached the decoder at all
  uint64_t state_dispatch_bad_args = 0;     // null base / r3 == 0
  uint64_t state_dispatch_not_render = 0;   // dispatch_kind != 1 (sampler etc.)
  uint64_t state_dispatch_unchanged = 0;    // current == pending, no-op
  uint64_t state_dispatch_bad_slot = 0;     // slot mapping out of range
  // Sampler dispatches GTL decoded and packed itself (89% of the carrier).
  uint64_t self_read_sampler_dispatches = 0;
  // AUTHORITATIVE EXIT SELF-READ census, one counter per draw root (step 2,
  // 2026-07-26). Every draw root stages its shaders/targets/constants from
  // INSIDE its own body (or, for the two thin wrappers, from inside the Begin
  // callee it invokes), so the packet built at ENTER holds the PREVIOUS draw's
  // state. The exit self-read re-reads the device object at the flush point and
  // overwrites each axis. These counters are the falsifier for the
  // observer-feed deletion that follows: for EVERY root that produces packets,
  // its counter here must be > 0. A root with packets but a zero counter is
  // still depending on the observer's OnFetchConstantDispatch /
  // OnShaderConstantDispatch / OnRenderTargetBindDispatch / OnShaderBindDispatch
  // feeds and must NOT have them deleted.
  uint64_t self_read_exit_begin_vertices = 0;              // 0x82205F68
  uint64_t self_read_exit_begin_indexed_vertices = 0;      // 0x82217DB8
  uint64_t self_read_exit_draw_primitive = 0;              // 0x8221C3E8
  uint64_t self_read_exit_draw_indexed_primitive = 0;      // 0x8221DFC0
  uint64_t self_read_exit_draw_primitive_up = 0;           // 0x82265CD8
  uint64_t self_read_exit_draw_indexed_user_primitives = 0;  // 0x8220A3F0
  // Exit arms that REACHED the self-read site but could not run it. Split so a
  // zero root counter is never ambiguous between "the root never fires", "its
  // packet went missing" and "the device pointer / guest base was unusable"
  // — the counter-conflation trap that has cost this project several runs.
  uint64_t self_read_exit_skipped_no_packet = 0;
  uint64_t self_read_exit_skipped_no_device = 0;
  // Float-constant divergence census (feed-7 / OnShaderConstantDispatch
  // deletion prerequisite, 2026-08-02). Graded at the authoritative EXIT
  // self-read, where the freshly re-read device bank (dev+0x780/0x1780 —
  // post-flush, so it holds every carrier including the PM4 SET_CONSTANT
  // deferred flush and sub_82214E30's VMX memcpy) is compared dword-for-dword
  // against the observer-fed bank mirror (g_state.device.*_float_bank). The
  // comparison uses the RAW device dwords, before the tonemap +inf sanitize,
  // so the sanitize can never read as divergence. banks_unfed counts exits
  // where the mirror had never been fed (nothing to compare — a coverage
  // fact, not divergence). Deleting the feed requires a verified run with
  // banks_diverged == 0 AND banks_unfed staying explainable.
  uint64_t constant_census_banks_compared = 0;
  uint64_t constant_census_banks_diverged = 0;
  uint64_t constant_census_dwords_diverged = 0;
  uint64_t constant_census_banks_unfed = 0;
  // First divergence of the RUN (sticky): register index (0..255) and bank
  // (0 = vertex, 1 = pixel). 0xFFFFFFFF until a divergence is seen. Names
  // WHICH register class disagrees without needing a per-draw log.
  uint32_t constant_census_first_diverged_register = 0xFFFFFFFFu;
  uint32_t constant_census_first_diverged_bank = 0xFFFFFFFFu;
  uint64_t draw_packets = 0;
  uint64_t frames = 0;
  uint64_t unmatched_set_events = 0;
  // Draw-packet shader backfill instrumentation. run 137 measured 13 of 15
  // vs-misses as ABSENT (packet.vertex_shader == 0) rather than untracked, so
  // the value is not reaching the packet — these say whether the backfill even
  // runs and whether the device read yields anything.
  uint64_t draw_packet_builds = 0;
  uint64_t shader_from_device_state = 0;   // staged setters already had it
  uint64_t shader_backfill_attempted = 0;  // device pointer + base usable
  uint64_t shader_backfill_filled = 0;     // device read returned nonzero
  uint64_t shader_still_absent = 0;        // packet ends with vs == 0
  // (removed 2026-07-26: shader_late_bound. Measured 0 in every run; the gap it
  // targeted was a grading artifact, vs_absent == resolve_packets.)
  uint64_t resolve_packets = 0;
  uint64_t clear_packets = 0;
  uint64_t content_packets = 0;
  uint64_t content_bytes_stored = 0;
  uint64_t content_unreadable = 0;
  uint64_t content_store_budget_drops = 0;
  // §4 second content writer (CPU tile roots). Split out from content_packets
  // so the tile channel's contribution is legible on its own — the whole
  // reason it was wired is that unlock coverage alone left ~13k refreshes per
  // run with no GTL content at all.
  uint64_t tile_content_packets = 0;
  // Tile calls whose destination matched no registered resource. This is the
  // writer's own honesty check: it is the decode/matching step, the one
  // inference-shaped part, so a high count here means the match is wrong
  // rather than the content being absent.
  uint64_t tile_content_unmatched = 0;
  // Tile calls rejected on their ARGUMENTS (null base, zero dst/src, or an
  // in-place src==dst). Split from unmatched because run 162 could not tell
  // the two apart and so could not say whether the arg map or the resource
  // lookup was at fault.
  uint64_t tile_content_bad_args = 0;
  // Tile calls that LOCATED their resource but had no stated byte count to
  // copy (header-init textures never state byte_size, and the pitch->size
  // relation for a 4x4-block format is not yet pinned from this root's own
  // arguments). Declined deliberately: a wrong-sized blob would be uploaded as
  // content, which is worse than storing nothing.
  uint64_t tile_content_size_unknown = 0;
  // XGTileTextureLevel calls skipped because they are the OUTER half of a
  // nested pair — the inner XGRAPHICS_TileSurface carries the same operation
  // with a post-mip-adjustment destination. Should track the surface-family
  // packet count closely; a large divergence means the nesting assumption is
  // wrong for some caller.
  uint64_t tile_content_outer_skipped = 0;
  // Raw enter counts per family, to settle a discrepancy the derived counters
  // could not: run 176 saw 177 outer calls but only 49 nested inner skips,
  // while IDA shows XGTileTextureLevel ending in an unconditional
  // `bl XGRAPHICS_TileSurface` (0x8300CD90) — not a tail call. So the inner
  // call ALWAYS happens and the shortfall is in whether its HOOK fires, which
  // is a plumbing question these two numbers answer directly.
  // Tile calls for a NON-ZERO mip level, declined because this writer stores
  // every packet at allocation_offset 0: a level's bytes would overwrite the
  // base image while every counter reported success. Non-zero here is coverage
  // deliberately left on the table, not a failure.
  uint64_t tile_content_level_skipped = 0;
  // §4's fourth writer: the streamed texture pool. `completions` counts
  // stream-ins that actually finished, `content_events` the resources they
  // named, `unregistered` those the registry did not know — which would mean
  // the pool streams into objects no Create/header root ever registered.
  uint64_t streamed_completions = 0;
  uint64_t streamed_content_events = 0;
  uint64_t streamed_unregistered = 0;
  uint64_t tile_outer_enters = 0;
  uint64_t tile_inner_enters = 0;
  // Tile calls whose linear source was not readable for the resource's size.
  uint64_t tile_content_unreadable = 0;
  // Tile calls that matched a resource but landed at a NON-ZERO offset inside
  // it (run 159: 3 of 18 destinations decode to base+0x40). Declined rather
  // than guessed, because the byte count of a sub-write would have to come
  // from pitch*height and the pitch units are not pinned for these roots.
  // Non-zero here is coverage left on the table, deliberately and visibly.
  uint64_t tile_content_offset_skipped = 0;
  // Census (§3b gate): resolves whose flags&7 named a slot with nothing bound
  // in the tracked device state — a device-state tracking gap.
  uint64_t resolve_unbound_source = 0;
  // Census (§3b gate): resolves whose r6 destination header was not registered
  // by any Create root. Measured 0 since the release-eviction fix (run 448:
  // every probe shows dest_reg == dest_tex). It formerly ran ~100%, which was
  // read as "destination headers are reused scratch the game re-points, so
  // Create never gives them durable identity" (run 437) — that was the eviction
  // bug, not the game's behaviour. Non-zero here is a real gap again.
  uint64_t resolve_unregistered_dest = 0;
  // Census (§3b gate): resolves whose destination fetch constant could NOT be
  // read from the r6 header. THIS is the number that matters — the fetch
  // constant is the only durable name for the memory a resolve writes into, so
  // a non-zero count is a real hole in destination identity.
  uint64_t resolve_no_dest_fetch = 0;
  // Times the GTL actually SAW XGOffsetBaseTextureAddress. Kept separate from
  // resources_adopted_at_offset on purpose: "adopted == 0" is ambiguous between
  // "the root never reached us" and "the object was already registered", and
  // conflating those wasted a run (440). This counter disambiguates.
  uint64_t offset_base_address_events = 0;
  // Objects first seen at XGOffsetBaseTextureAddress rather than at a Create
  // root — i.e. header objects no hooked factory ever created. Only meaningful
  // when offset_base_address_events > 0.
  uint64_t resources_adopted_at_offset = 0;
  // The eight §4 fetch-store ingest counters (fetch_content_stored /
  // _stored_unique / _unregistered / _unchanged / _declined_{gpu,target,stated,
  // aliased}) were DELETED 2026-08-04. Their only producer,
  // IngestBoundTextureContent, was removed 2026-07-26; every one of them had
  // zero increment sites and printed a constant zero. See the deletion note at
  // the top of refii_gtl.cpp's content-store section for what ingest did.
  // Base->object content lookups: a CopyResourceContent query keyed by a
  // physical BASE that the registry keys by OBJECT, resolved to the owning
  // object's content (closes the 26 alias-shadow addresses, replaces the
  // observer's alias propagation). `hit` served the fallback; `miss` found no
  // owning object with content.
  uint64_t base_alias_content_hits = 0;
  // Guest dirty-mask qwords (dev+0x00..0x20) consumed and cleared per draw.
  uint64_t device_dirty_masks_drained = 0;
  // Content served straight from the guest's STATED base, for resources the
  // guest fills with raw CPU stores and no Lock/Unlock (the tone curve).
  uint64_t stated_base_content_hits = 0;
  uint64_t stated_base_content_rejected = 0;
  uint64_t base_alias_content_misses = 0;

  // ---- STATED BUFFER PAYLOAD census ---------------------------------------
  // `binds` is every SetStreamSource / SetIndices that named a buffer object;
  // `adopted` is how many of those objects no Create root had delivered. A
  // large `adopted` is not a defect -- the bind roots are a complete authority
  // -- but a NON-ZERO one says the create path is missing buffers, which is
  // worth knowing separately from the bind path working.
  uint64_t buffer_binds_stated = 0;
  uint64_t buffers_adopted_at_bind = 0;
  // Payload reads at the snapshot feed. `served` copied bytes from the stated
  // GPU address; `no_header` found no readable header (buffer not yet given
  // its allocation); `unreadable` had a header but the payload range was not
  // mapped; `oversize` exceeded the per-buffer cap.
  uint64_t buffer_content_served = 0;
  uint64_t buffer_content_no_header = 0;
  uint64_t buffer_content_unreadable = 0;
  uint64_t buffer_content_oversize = 0;
  // Physical-pool compactor moves seen at kRelocateResource, and how many
  // named a buffer the registry already held.
  uint64_t buffer_relocations = 0;
  uint64_t buffer_relocations_matched = 0;

  // Hardware-tessellator patch draws (TessellatedPatchDrawPacket). `calls` is
  // one per emitter invocation; the emitters LOOP, chunking at 0xFFFE, so
  // `pm4_draws` (DRAW_INDX packets actually decoded out of the ring) can exceed
  // it. Split terrain/water because they are different systems on different
  // chains. `frame` counters are reset at each OnFrameBoundary so the census
  // reports per-frame volume, which is the number a rendering design needs.
  uint64_t tess_terrain_calls = 0;
  uint64_t tess_water_calls = 0;
  uint64_t tess_pm4_draws = 0;
  uint64_t tess_ring_capture_failed = 0;
  uint64_t tess_device_regs_unreadable = 0;
  uint64_t tess_distinct_shapes = 0;
  uint64_t tess_terrain_calls_this_frame = 0;
  uint64_t tess_water_calls_this_frame = 0;
  uint64_t tess_pm4_draws_this_frame = 0;
  uint64_t tess_peak_calls_per_frame = 0;
};

// Wire-in from the renderer hook dispatch. `symbol` is the guest function
// name ("sub_82......"); events not owned by the GTL are ignored cheaply.
void OnHookEnter(const char *symbol, const RendererHookArgs &args,
                 uint8_t *guest_base);
// Exit pairing for constructor/factory roots (created object is in the
// return register).
void OnHookExit(const char *symbol, const RendererHookArgs &args);

// Pre-resolved variants: `symbol_address` MUST be the value
// ResolveHookSymbolAddress(symbol) would return. The string-taking overloads
// above resolve per call (a strtoul on every hooked guest call, measured on
// the title frame); the hook wrappers resolve once per call site via
// FableHookSymbolDesc and pass the address through.
void OnHookEnter(const char *symbol, uint32_t symbol_address,
                 const RendererHookArgs &args, uint8_t *guest_base);
void OnHookExit(const char *symbol, uint32_t symbol_address,
                 const RendererHookArgs &args);
// Pure symbol->address mapping (rex_* shims mapped to their twin guest
// addresses; unmapped names return 0). For descriptor construction.
uint32_t ResolveHookSymbolAddress(const char *symbol);
bool IsSceneSubmitScopeAddress(uint32_t symbol_address);
bool IsKnownHookSymbolAddress(uint32_t symbol_address);
// Wrapper fast-path entry for hooked symbols the GTL provably ignores
// (mapped non-zero address, not a GTL symbol constant per the
// IsGtlKnownSymbolAddress SUPERSET gate, not a scene-submit scope): the ONLY
// work OnHookEnter performs for such a symbol is the Begin-family pin drain,
// whose contract is "this thread's NEXT hooked D3D call" and therefore must
// run even when everything else is skipped. OnHookExit is a pure no-op for
// these symbols (its entire body is an address ladder over GTL constants),
// so the wrapper fast path calls nothing at exit.
void OnHookEnterUnknownSymbolFastPath(uint8_t *guest_base);
// Scene-scope marker short-circuit support: exactly the work OnHookEnter/
// OnHookExit perform for a scene-submit-scope symbol (begin-pin drain +
// scene_submit_depth ++/--) with everything else skipped.
void OnSceneSubmitScopeEnter(uint8_t *guest_base);
void OnSceneSubmitScopeExit();

// Frame boundary tick. Under Plume the guest Present/DisplaySwap roots are
// suppressed and never run, so the renderer's frame advance is the
// authoritative boundary; call this once per Plume frame.
void OnFrameBoundary();

// OnSamplerStateDispatch / OnSamplerStateReset (observer sampler feed)
// DELETED 2026-08-02 — run 503: self_read_sampler=107k,
// legacy_external_state_feed=0; samplers are self-read by OnStateDispatch.

// Decodes the state-dispatch record (sub_8221E550) DIRECTLY from guest memory,
// so GTL's at-draw render states no longer require the observer to be running.
// Handles render states (kind 1) and samplers (kind 2).
void OnStateDispatch(const RendererHookArgs &args, uint8_t *guest_base);

// Texture fetch constants arrive via the pending dispatch (SetPending_
// FetchConstants) for the lazy-dispatch draw families; the observer decodes
// the record once and feeds the base address per slot here.

// Bound vertex/pixel shader objects, fed from the observer's post-flush read
// of the device. Needed because Fable stages shaders as PENDING and the draw
// root flushes them inside a guest body Plume suppresses, so the device's
// applied fields are stale when a packet is built at hook enter. Zero means
// "unchanged" — the caller need not know the previous value.

// Bound render/depth targets, fed from the observer's bind points for the same
// reason as the shader pair: Fable stages target binding rather than calling
// kSetRenderTarget (8 dispatches vs ~168 draws/frame). Unlike the shader feed,
// zero is meaningful (an unbind) and is recorded verbatim.

// (removed 2026-07-26: IngestBoundTextureContent, the §4 fetch-store content
// ingest. Reverted 2026-07-23 for regressing rendering vs the Xenia reference,
// zero callers ever since. See the note at its old site in refii_gtl.cpp for
// why the observer's per-bind fetch capture must stay and why a naive re-add
// is a trap.)

// Float shader constants are lazily dispatched like render states: the
// setters stage values a dispatcher applies later, so device memory at a
// draw hook can be one frame stale (the WITH_ALPHA fade flash). The feed
// point is the observer's normalized store funnel (StoreShaderConstant-
// DwordsLocked), which every float carrier reaches — SetShaderConstantF
// vectors, the immediate ALU-space uploads, and shader literal tables —
// with bank guessing and the pixel-register rebase already applied.
// Feeding only the SetShaderConstantF roots loses the other carriers once
// the bank latches valid (the menu WVP-zero panel defect). Dword
// granularity because literal-table entries are sub-vector. Values are
// read raw from guest memory here (byte-swapped, unsanitized) per the
// DrawPacket bank contract; packets snapshot these banks as the at-draw
// truth.
void OnShaderConstantDispatch(bool pixel_bank, uint32_t first_dword,
                              uint32_t data_pointer, uint32_t dword_count,
                              uint8_t *guest_base);

// SHADER LITERAL CONSTANTS -- the one float carrier that never reaches the
// device banks (IDA 2026-08-02). D3D_SetLiteralShaderConstants (0x8222BFA0)
// walks the shader's embedded literal table and emits a PM4 type-3 opcode
// 0x2F LOAD_ALU_CONSTANT per entry, loading the values STRAIGHT into the GPU
// ALU constant file from the shader blob; dev+0x780/0x1780 are untouched. It
// runs from D3D_SetPending_Shaders AFTER the two shadow flushes in the same
// draw root (the roots do `ld mask[0]` -> emit type-0 from dev+0x780 to Xenos
// reg 0x4000, then clear the mask), so for the registers a literal covers the
// literal is what the GPU actually samples.
//
// That breaks the premise renderer_gtl_constant_self_read was built on ("the
// device bank is the staging every carrier flushes into"): under GTL
// authority we never execute the ring, so a literal-supplied register reads
// as ZERO. Measured consequence (runs 530-533): the loading screen and world
// draw with a zero WorldViewProjection -- post-VS clip=(0,0,0,0), ndc=inf,
// nothing rasterises, screen black.
//
// `first_register` is the ABSOLUTE ALU register (0-255 vertex, 256-511 pixel,
// matching the 0x4000/0x4400 reg bases); `dword_count` is in DWORDS (the
// emitter writes offset = 4*first_register and count = dwords). Values are
// read raw and byte-swapped here, same contract as the bank reads.
void ApplyLiteralShaderConstants(uint32_t first_register, uint32_t dword_count,
                                 uint32_t data_pointer, uint8_t *guest_base);

// Drains packets recorded since the last call (pump-side consumer).
std::vector<DrawPacket> DrainDrawPackets();

// Drains resolve packets recorded since the last call. Separate stream from
// DrawPackets — a different primitive class (§ "Two packet classes").
std::vector<ResolvePacket> DrainResolvePackets();

// Drains clear packets recorded since the last call. Ordered with the draw
// stream by shared packet_id so the pump can interleave clears and draws.
std::vector<ClearPacket> DrainClearPackets();

// Drains hardware-tessellator patch draw packets (T2, 2026-08-02). Ordered
// with the other streams by the shared packet_id counter so the authoritative
// replay can interleave terrain/water patch draws at their captured position.
std::vector<TessellatedPatchDrawPacket> DrainTessellatedPatchDrawPackets();


// Guest-frame-aligned drains: return only packets with packet_id < boundary
// (a COMPLETED guest frame; see StatedPresentSource::guest_frame_boundary_
// packet_id), leaving in-progress packets for the next drain. boundary == 0
// drains everything. Used by the authoritative submission replay so a guest
// frame is not split across pump over-runs.
std::vector<DrawPacket> DrainDrawPacketsUpTo(uint64_t boundary);
std::vector<ResolvePacket> DrainResolvePacketsUpTo(uint64_t boundary);
std::vector<ClearPacket> DrainClearPacketsUpTo(uint64_t boundary);
std::vector<TessellatedPatchDrawPacket> DrainTessellatedPatchDrawPacketsUpTo(
    uint64_t boundary);

// Read-only probe for the present-pump re-present fast path: true when any
// recorded draw/resolve/clear packet would be returned by the boundary-aligned
// drains above (packet_id < boundary; boundary == 0 counts everything).
// Drains nothing.
bool HasPendingPacketsUpTo(uint64_t boundary);


// The STATED present source, captured at the guest Present root (0x82B6F1D0,
// run 199: fires per guest frame). `rt0` is device.render_targets[0] — the RT0
// the guest resolves into the front buffer and swaps — i.e. the finished frame
// the game wants displayed. `epoch` increments once per guest Present so the
// present pump can present that frame HELD between guest Presents instead of
// the candidate-ranking/starvation heuristic that showed partial host-pump
// composes (the fragmentation + title-freeze). `front_buffer` is
// g_FrontBufferTexture from the boot Swap (0x82BA34D8, r4), kept for reference.
struct StatedPresentSource {
  uint32_t rt0 = 0;
  uint32_t front_buffer = 0;
  // GPU base of the front buffer, decoded from the STATED header at capture
  // time: `*(front_buffer + 0x20) & 0xFFFFF000`. That offset is the texture's
  // GPU base, proven three ways in the binary (sub_82A1C3D0 @0x82A1C410;
  // D3DDevice_Resolve @0x82206974 -> 0x82206998 -> RB_COPY_DEST_BASE
  // @0x82206BE0). Replaces the observer's display_present_texture_base.
  uint32_t front_buffer_base = 0;
  uint64_t epoch = 0;
  // next_packet_id captured at the last guest Present: every packet with
  // packet_id < this belongs to a COMPLETED guest frame. Lets the authoritative
  // submission replay emit whole guest frames and carry the in-progress
  // remainder, instead of splitting a frame across pump over-runs.
  uint64_t guest_frame_boundary_packet_id = 0;
};
StatedPresentSource GetStatedPresentSource();

// The present pump registers a callback here; the GTL invokes it at the guest
// Present (0x82B6F1D0) so the pump can compose + present exactly ONE complete
// guest frame per guest Present (frame-aligned), instead of ticking on an
// independent host timer that slices guest frames (the fragmentation root cause,
// run 200). The callback must only SIGNAL (e.g. notify a condition variable) —
// it runs under the GTL state mutex, so it must not block or take heavy locks.
using GuestFramePresentedCallback = void (*)();
void SetGuestFramePresentedCallback(GuestFramePresentedCallback callback);

// The accumulated bytes for an allocation, assembled from the ranges the
// unlock roots stated. Copied AT UNLOCK, when the root flushed them — not
// re-read later, so a consumer sees what the guest actually wrote rather than
// whatever that memory holds by the time a frame is recorded.
//
// Returns false when nothing has been stated for this allocation yet. `out` is
// sized to the highest offset written so far; bytes in gaps the guest never
// wrote are zero.
bool CopyResourceContent(uint32_t resource, bool mip_range,
                         std::vector<uint8_t> &out);

// As above, and additionally reports the LAYOUT the storing root stated (see
// ContentPacket::source_linear). A consumer that uploads these bytes must pass
// this through; uploading linear bytes as tiled untiles something that was
// never tiled.
bool CopyResourceContent(uint32_t resource, bool mip_range,
                         std::vector<uint8_t> &out, bool &out_source_linear);


// True when a Resolve has named this resource as its destination, i.e. its
// content is GPU-produced and no CPU byte source exists for it.
//
// Use it to avoid mistaking "no CPU content" for "missing content". A texture
// that only ever receives resolves SHOULD have nothing in the content store;
// that is the design working, not a coverage gap.
bool ResourceHasGpuProducedContent(uint32_t resource);

// Registry/content status for one guest address, for diagnosing the content
// gap. Distinguishes three cases a single "the GTL has no bytes" cannot:
//
//   known=false            -> the resource was never registered at all; the
//                             Create/header root that makes it is unhooked.
//   known, generation==0   -> registered, but NO write root ever fired for it:
//                             its content arrives by a path the GTL does not
//                             see (and which the observer finds by scanning).
//   known, generation>0    -> a write WAS seen, so bytes should exist; an empty
//                             store then points at storage or the consumer.
//
// Each implies completely different work, which is why the gap probe reports
// this rather than just the absence.
struct ResourceContentStatus {
  bool known = false;
  uint32_t create_symbol = 0;
  uint32_t create_lr = 0;
  uint64_t content_generation = 0;
  uint64_t gpu_content_generation = 0;
  uint32_t base_address = 0;
  bool has_stored_bytes = false;
};
ResourceContentStatus QueryResourceContentStatus(uint32_t resource);

// NOTE: a `const Resource *FindResource(uint32_t)` used to live here. It was
// DELETED 2026-07-26: it had zero callers for its whole life, and it was unsafe
// by construction -- it released g_state.mutex before returning the pointer, so
// the pointee could be mutated or rehashed by the guest render thread while the
// caller read it. Use QueryResourceIdentity below, which copies.

// The CPU-dirtied extent the unlock root last stated for a resource, copied
// under the GTL lock (unlike FindResource, which hands out a pointer into
// live state). `known` distinguishes "resource is not in the GTL registry"
// from "registered, but the unlock stated no range". Consuming CLEARS the
// stored range (single-shot, matching the guest root's own sentinel reset
// after D3D_FlushCachedMemory): an extent may feed exactly one capture, so
// unlocks that bypass the GTL entry capture can never re-adopt a stale one.
struct ResourceDirtyExtent {
  uint64_t content_generation = 0;
  uint32_t base_start = 0;
  uint32_t base_end = 0;
  uint32_t mip_start = 0;
  uint32_t mip_end = 0;
  uint32_t base_allocation = 0;
  uint32_t mip_allocation = 0;
  bool known = false;

  bool base_valid() const { return base_end > base_start; }
  bool mip_valid() const { return mip_end > mip_start; }
  bool any() const { return base_valid() || mip_valid(); }
};
ResourceDirtyExtent ConsumeResourceDirtyExtent(uint32_t guest_address);

// The GTL's answer to the question the observer's FableTextureIdentity answers:
// "what do we know about the texture at this guest object address?" Migration
// step 6 ports the identity consumers onto this.
//
// COPIES, deliberately — the same reason ConsumeResourceDirtyExtent copies.
// `FindResource` returns a Resource* AFTER releasing g_state.mutex, so the
// pointee can be mutated or rehashed out from under the caller by the guest
// render thread. Do not port consumers onto FindResource; port them onto this.
//
// Field mapping to the observer's FableTextureIdentity, so a port can be
// checked term by term:
//   serial            <- create_serial
//   object_texture    <- guest_address
//   parent_texture    <- view_parent_texture
//   base_address, byte_size, width, height, format   direct
//   upload_generation <- content_generation
//   fetch_dwords      <- fetch_dwords (stated at the header-init roots)
// `meaningful_upload` has NO equivalent and is not modelled here: it is an
// observer judgement about captured bytes, not a fact the guest stated.
struct ResourceIdentity {
  uint64_t create_serial = 0;
  uint32_t guest_address = 0;
  uint32_t parent_texture = 0;
  uint32_t base_address = 0;
  uint32_t mip_address = 0;
  uint32_t byte_size = 0;
  uint32_t width = 0;
  uint32_t height = 0;
  uint32_t format = 0;
  uint64_t content_generation = 0;
  uint64_t gpu_content_generation = 0;
  // How many times this ADDRESS has been handed to a new texture. A consumer
  // that cached an identity must compare this before trusting it: equal address
  // with a different tenancy_generation is a DIFFERENT texture, not a changed
  // one. content_generation cannot answer that — it is deliberately carried
  // ACROSS re-registration (+1), so it hides re-tenancy by design.
  uint64_t tenancy_generation = 0;
  std::array<uint32_t, 6> fetch_dwords = {};
  bool fetch_dwords_stated = false;
  bool was_render_target = false;
  // False = the GTL has never registered this address at all. Distinguish from
  // "registered but nothing stated yet" (known == true, fetch_dwords_stated ==
  // false), which is a different porting problem.
  bool known = false;
};
ResourceIdentity QueryResourceIdentity(uint32_t guest_address);


}  // namespace refii::gtl
