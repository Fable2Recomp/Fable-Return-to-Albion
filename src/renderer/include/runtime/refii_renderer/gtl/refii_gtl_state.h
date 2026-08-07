#pragma once

#include "runtime/refii_renderer/gtl/refii_gtl.h"
#include "renderer_runtime.h"

#include <array>
#include <cstdint>
#include <mutex>
#include <atomic>
#include <unordered_map>
#include <unordered_set>
#include <vector>

namespace refii::gtl {

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

extern thread_local PendingTessellatedPatchCapture t_pending_tess_capture;

struct FamilyCoverage {
  uint64_t total = 0;
  uint64_t geom = 0;     // inline UP geometry present
  uint64_t streamvb = 0; // a bound vertex buffer present
  uint64_t ident = 0;    // both shaders set
  uint64_t decl = 0;     // vertex declaration set
  uint64_t tex0 = 0;     // slot-0 texture bound
  uint64_t rt0 = 0;      // slot-0 render target set
};

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

extern std::unordered_map<uint32_t, FamilyCoverage> g_family_coverage;
extern DivergenceCensus g_divergence_census;
extern thread_local uint64_t t_last_packet_id;
extern thread_local uint32_t t_last_packet_symbol;
extern thread_local uint32_t t_last_packet_device;
extern thread_local uint8_t *t_last_packet_guest_base;
extern thread_local uint32_t t_begin_indexed_index_out_address;
extern thread_local uint32_t t_begin_indexed_vertex_out_address;
void MarkResourceChangedLocked(uint32_t guest_address);

bool FetchDeviceSourced();

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

extern BeginPinCensus g_begin_pin_census;

extern std::atomic<GuestFramePresentedCallback> g_guest_frame_presented_callback;
void LogSelfReadExitCensusLocked();

extern State g_state;

void BuildDrawPacketLocked(uint32_t symbol_address,
                           const RendererHookArgs &args, uint8_t *guest_base);

void BuildResolvePacketLocked(uint32_t symbol_address,
                              const RendererHookArgs &args,
                              uint8_t *guest_base);

void BuildClearPacketLocked(uint32_t symbol_address,
                            const RendererHookArgs &args, uint8_t *guest_base);

DrawDeviceSnapshot CaptureDrawDeviceSnapshot(uint32_t device,
                                             uint8_t *guest_base);

void DrainDeviceDirtyMasksLocked(uint32_t device, uint8_t *guest_base);

void AccumulateDeviceSnapshotDivergenceLocked(const DrawPacket &packet);


void CompleteTessellatedPatchCaptureLocked(const RendererHookArgs &args);

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
                                                bool index_buffer);

bool CopyResourceContentLocked(uint32_t resource, bool mip_range,
                               std::vector<uint8_t> &out,
                               bool &out_source_linear);

}  // namespace refii::gtl
