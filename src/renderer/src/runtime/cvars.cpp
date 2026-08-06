#include "runtime/cvars.h"

#include "renderer_runtime.h"

#include <algorithm>
#include <atomic>
#include <cctype>
#include <cstdint>
#include <mutex>
#include <string>

#include <rex/cvar.h>
#include <rex/graphics/flags.h>
#include <rex/graphics/video_mode_util.h>

namespace refii::renderer {
namespace {

std::mutex g_renderdoc_capture_reason_mutex;
std::string g_renderdoc_capture_reason;

REXCVAR_DEFINE_STRING(renderer_backend, "xenia", "Renderer",
                      "Renderer backend: plume for the Refii RHI path, xenia "
                      "for the original Fable 2 renderer.")
    .lifecycle(rex::cvar::Lifecycle::kRequiresRestart);

REXCVAR_DEFINE_STRING(renderer_plume_api, "vulkan", "Renderer",
                      "Plume graphics API: vulkan, d3d12, or metal.")
    .lifecycle(rex::cvar::Lifecycle::kRequiresRestart);

REXCVAR_DEFINE_STRING(renderer_scaling, "1x", "Renderer",
                      "Fable 2 internal viewport supersampling scale: "
                      "1x, 2x, 3x, or 4x.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(renderer_trace_hooks, false, "Renderer",
                    "Log renderer hook dispatch and suppression decisions.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(renderer_gtl_executor_shadow, true, "Renderer",
                    "Measure GTL draw-packet executability every frame "
                    "(section 3.4 executor cutover-readiness metric).")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);


REXCVAR_DEFINE_BOOL(renderer_gtl_executor, true, "Renderer",
                    "GTL packet identity authority: classic draws execute "
                    "with packet-sourced bindings (section 3.4 cutover; "
                    "verified at behavioral and visual parity).")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

// Default ON (2026-07-16): the section-3.4/step-5 cutover — the GTL DrawPacket
// stream DRIVES the title vertex draws (in-place identity-matched replacement of
// the observer draws, preserving draw<->composite order). Proven via live sweeps
// to render the full Fable title scene visually equivalent to the observer, with
// NO temp scaffolds. Required fixes (all landed): bink observer-sourced (blank-
// geometry), viewport_guest_space + executor guest->output scale (was rendering
// black), sampler_states read in the converter, and the ordered in-place merge
// (was rendering flat). Non-vertex classes (clears/resolves/private-rect/video)
// stay observer-sourced until their packet streams drive submission too. GATE:
// only live on-screen sweeps prove correctness here — RenderDoc replay is useless
// (bindless heap not reconstructed -> always white) and record-success is not
// correctness.
REXCVAR_DEFINE_BOOL(renderer_gtl_submission, true, "Renderer",
                    "GTL packet stream drives submission: the frame draw stream "
                    "is built from GTL packets, bypassing the observer replay "
                    "(section 3.4 executor cutover / step 5).")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

// DEFAULT OFF pending the run-475 bisect: run 474's title record runaway was
// the backlog feedback loop this filter breaks, but run 475 (filter live)
// froze EARLIER, in the video phase, guest parked at FrameDirector — and the
// filter's video exemption provably missed the Bink COMPOSE quads (their
// draw.source is kScratchWrapper/kD3DWrapper/kSceneObject, not kPixVideo —
// see FableVideoCommandHasBinkWrapperSource), while the immediate Bink
// publish path minted the generations that superseded them. The
// classification is fixed (sealed-publish-only generations), but the gate
// Default ON since run 479: with seal-only generation stamping the video
// edge is closed (immediate Bink/PIX segments are gen-0, never superseded),
// and run 479 frame 3964 showed the exact failure it prevents — a 3,280-draw
// backlog burst composing for 5.9s. REX_RENDERER_BACKLOG_SUPERSESSION=0
// restores capture-everything for A/B.
REXCVAR_DEFINE_BOOL(renderer_backlog_supersession, true, "Renderer",
                    "Drop non-video draws of superseded complete guest frames "
                    "when the compose captures a multi-frame backlog, "
                    "mirroring the guest ring interpreter's own staleness "
                    "rule (sub_822DFE08: newer complete list supersedes). "
                    "Non-draw commands (clears/resolves/state) always replay.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

// Safety valve behind the ordinal-keyed supersession above: that pass needs
// the guest Present boundary to be advancing; a stalled boundary (or a single
// pathological frame) can still hand the record loop an unbounded draw set.
// This cap is keyed on COUNT only — if the batch still carries more than ~3
// guest frames of draws after supersession, the oldest non-video draws are
// dropped down to the cap, mirroring the guest interpreter's own >150ms
// backlog discard at the blunt end. Non-draw commands always survive.
REXCVAR_DEFINE_BOOL(renderer_record_backlog_cap, true, "Renderer",
                    "Count-keyed record backlog cap: drop oldest non-video "
                    "draws when a single compose would record more than ~3 "
                    "guest frames of draws even after backlog supersession. "
                    "Works when frame boundaries stall (count, not ordinals).")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

// The full cutover: do NOT walk the observer command batch at all. Replay the
// GTL packet streams (draws incl. video, resolves = private-rect, clears) purely
// by packet_id. Every class is GTL-owned with full self-read state, so the frame
// is reproduced with zero observer input — the step that lets the observer be
// deleted.
//
// DEFAULT ON (user directive 2026-07-24): remove the observer from submission
// and fix the GTL gaps from IDA as they surface. KNOWN OPEN GAP — a present-loop
// ownership DEADLOCK: IDA sub_82A1B560 is the guest present/display loop; it
// CLAIMS device ownership (dword_83360350 = tid), runs a present loop that waits
// on a display flip (XNotifyGetNext type 9 + Render_PresentTexture, while(!v21)),
// and only RELEASES ownership (dword_83360350 = -1) at the very end. The
// authoritative replay currently emits a PARTIAL frame (guest-frame drain vs the
// observer's full batch), so the front buffer never becomes a complete
// presentable frame, sub_82A1B560's loop never completes, ownership stays held,
// and the guest render thread spins in Render_D3DDeviceOwnership_WaitAndReclaim
// (sub_82294830 x526k) — chicken-and-egg. FIX PATH: make the replay emit the
// COMPLETE guest frame (correct boundary / full packet coverage) so the present
// loop completes; the merge avoids the deadlock precisely because it emits the
// full frame. Toggle false for the working in-place merge A/B.
REXCVAR_DEFINE_BOOL(renderer_gtl_authoritative_submission, true, "Renderer",
                    "Replay the GTL packet streams by packet_id and never walk "
                    "the observer command batch (observer submission cutover). "
                    "Known open: present-loop ownership deadlock on partial "
                    "frames; see the comment above.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(renderer_plume_guest_driven_present, true, "Renderer",
                    "Present pump is driven by the guest Present (one complete "
                    "guest frame per guest Present) instead of an independent "
                    "host FPS timer that slices guest frames (the fragmentation "
                    "root cause, run 200). Toggle off to A/B the timer pump.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(renderer_gtl_stated_present_source, true, "Renderer",
                    "Present the STATED source captured at the guest Present root "
                    "(render_targets[0], the RT0 the guest resolves into the "
                    "front buffer), instead of the candidate-ranking + starvation "
                    "heuristic that showed partial host-pump composes / latched a "
                    "stale frame. Toggle off to A/B the old heuristic.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

// Video draws through the GTL packet stream. The observer video funnel
// (immediate publish -> deferred replay) measurably DROPS the YCrCb compose
// draws — pixvideo-scoped captures (frame 1013, 2026-07-18) show pump frames
// containing only the present blit of an already-white scene target; the
// compose never executes. The GTL packet for the compose root (0x8220A3F0)
// carries race-free inline geometry and the plane texture bindings, so video
// rides the same ordered stream as everything else: matched observer video
// draws are GTL-sourced in place, and packets the observer DROPPED are
// inserted at their packet_id position instead of being lost.
REXCVAR_DEFINE_BOOL(renderer_gtl_video_draws, true, "Renderer",
                    "GTL packet stream drives Bink/Pix video draws (matched "
                    "in place + dropped packets inserted).")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

// Partial clear cutover (stocktake item 3, first half): ClearPackets replace
// ONLY the observer clears whose root is one of the two D3D clear wrappers
// (0x821E2FE0 Clear / 0x821EF830 ClearRect) — matched in place, order
// preserved. Observer synthetic clears (private-rect setup, per-target) are
// untouched: their producers are not GTL-owned yet (the run-379 lesson — a
// full clear cutover collapsed 265 clears/frame to the 2-10 D3D-root ones).
// A Xenos resolve doubles as an EDRAM clear: D3DDevice_Resolve Flags 0x100
// (CLEARRENDERTARGET) / 0x200 (CLEARDEPTHSTENCIL) clear the SOURCE surface
// after the copy — EndTiling primes the next tile this way (flags 0x300 per
// tile). The ResolvePacket carries pClearColor/ClearZ/ClearStencil verbatim;
// this emits the clear right after each interleaved kGtlResolve executes.
// DEFAULT ON since run 559 (user in-game drive: EndTiling parity or=3622 ~
// rc=3624 at gameplay scale).
REXCVAR_DEFINE_BOOL(renderer_gtl_resolve_clears, true, "Renderer",
                    "Emit the resolve's own EDRAM source clear (Flags 0x300) "
                    "after each GTL-interleaved resolve.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

// Full clear ownership: the observer clear stream is exactly two families —
// the D3D clear roots (ClearPackets are a strict superset) and the EndTiling
// resolve-clears. With both packet-sourced, the observer's kClear commands
// are redundant: emit ClearPackets at their packet_id positions and drop the
// observer clears entirely. DEFAULT ON since run 559 (user in-game drive,
// dropped 20294 == emitted 16680 + resolve 3624 — full parity; the partial
// match-in-place mode and its renderer_gtl_clears cvar are deleted).
REXCVAR_DEFINE_BOOL(renderer_gtl_clears_authoritative, true, "Renderer",
                    "ClearPackets emitted at packet_id positions own ALL "
                    "clears; observer kClear commands are dropped.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

// Cull state read LIVE from the guest device control packet (device+0x2948,
// low 3 bits = PA_SU_SC_MODE_CNTL cull field) at each draw root's POST-FLUSH
// exit, instead of the render-state mirror. The mirror only advances when the
// SetRenderState_CullMode root (0x8220B470) fires, and IDA 2026-07-25 shows
// the engine reaches that function through the device's D3DRS call table
// rather than a direct call (one direct caller binary-wide: the Scaleform UI).
// Any gap in that indirect dispatch leaves slot 19 on our session DEFAULT of 6
// (cull-back / CW-front) — exactly the value the run-263 capture audit found
// on the zero-fragment glyph draws, while every draw that renders carries 0.
// The device dword is what the GPU consumes, so it settles the question
// instead of inheriting the mirror's blind spots. OFF restores the mirror; the
// gtl_live_cull_diverged counter measures how much the two disagree.
// Rewrite a texture slot that holds a raw fetch BASE into the guest OBJECT that
// owns it, so the sampling resolver's single exact route can answer it.
// DEFAULT OFF — it caused a full black screen on its first run (2026-07-25).
// The title screen renders fine without it; it only ever mattered for the
// storybook menu panels. Turning it on rewrote slots far more widely than
// intended and lost the whole picture, so it needs a per-draw scope (and proof
// that the object it picks is the right one) before it can default on again.
REXCVAR_DEFINE_BOOL(renderer_gtl_texture_base_normalisation, false, "Renderer",
                    "Rewrite raw fetch-base texture slots to the owning guest "
                    "object before binding.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(renderer_gtl_live_cull_authority, true, "Renderer",
                    "GTL draw packets take their cull state from the live "
                    "device control packet instead of the render-state "
                    "mirror.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

// Depth resolves (Flags&7==4, D32S8 source -> R32F dest — the pinned format
// pair) through the same conversion-draw shape as color: plume images carry
// SAMPLED usage and views select the depth aspect automatically. In-game runs
// defer ~250-1800 of these per session; OFF keeps the deferral.
// DEFAULT ON since run 579 (user in-game drive: depth_def=0 across 2100+
// live resolves, NO-VIEW=0, clean exit).
REXCVAR_DEFINE_BOOL(renderer_gtl_depth_resolves, true, "Renderer",
                    "Execute depth resolves via the conversion draw (OFF = "
                    "defer, the pre-existing behavior).")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

// Bink audio clock source. The emulated XAudio voice has no real consumer, so
// the shim advances its cursor itself. The frame-driven advance ties audio
// time to video-frame progress while Bink's decode waits on the audio drain —
// the two starve each other into the longstanding ~4-8fps video crawl. ON =
// advance cursors by wall-clock elapsed from the Plume pump (real-voice
// semantics: audio time flows at 1x regardless of decode progress; the
// per-plane 3-movie-frame lead cap still bounds audio ahead of the drain).
// OFF = the legacy frame-driven advance.
REXCVAR_DEFINE_BOOL(renderer_bink_realtime_audio_clock, true, "Renderer",
                    "Advance Bink audio cursors in wall-clock time from the "
                    "Plume pump instead of per video-frame advance.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

// §4 Stage B root fix, family 1 (docs/refii_content_authority_plan.md):
// Bink plane buffers are channel-owned content — the decode publish delivers
// their bytes at the guest's own sequence points. Generic bind-time capture
// racing the decoder over those buffers was the largest bind-capture family
// (census runs 023/025: fmt=2 plane aliases, ~1MB memcmp per bind). ON =
// ShouldCaptureTextureUploadFromFetchDwords refuses channel-owned records
// and any fetch whose base lands inside a published plane range.
REXCVAR_DEFINE_BOOL(renderer_bink_channel_capture_gate, true, "Renderer",
                    "Generic texture capture skips Bink-channel-owned "
                    "records and plane buffer ranges.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

// §4 content authority (docs/refii_gtl_migration.md replacement map: "content
// only from Unlock ranges"). D3D::LockResource accumulates the CPU-dirtied
// extent into the D3DResource; the unlock root flushes exactly that range.
// With this ON the unlock capture reads the range the root states instead of
// the lock-time pointer, which is what the alias/decoded disagreement rule is
// currently guessing around. OFF keeps the lock-time behaviour for A/B.
REXCVAR_DEFINE_BOOL(renderer_unlock_dirty_ranges, false, "Renderer",
                    "Unlock capture uses the dirty extent stated by the "
                    "unlock root instead of the lock-time pointer/size.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

// Content model (Unleashed/Marathon shape, docs/refii_renderer_hook_ida_audit
// "Volume 3 target shape for lock-path textures"). We already OWN
// D3D::LockResource and choose the pointer it returns; today we hand back the
// address the caller passed, which for Fable is the write-combined ALIAS
// (run 132: lock returns 0xFA5xxxxx while our readers look at 0x1A5xxxxx, and
// rexglue's flat map does NOT alias the two). With this ON the lock returns
// the DECODED view instead, so the guest writes exactly where every capture
// path already reads and the alias/decoded split stops existing.
//
// IDA support (0x8225F980 D3DVertexBuffer_Lock, 0x82278138 D3D_LockSurface):
// both return D3D_LockResource's value straight to the caller, so redirecting
// it redirects the writes. All 35 observed locks are write locks
// (flags bit0 clear), so nothing needs pre-populating.
// §4 content authority cutover. Texture bytes come from the GTL content store
// — the ranges D3D::UnlockResource stated, copied at the flush — instead of
// the observer's after-the-fact capture. Only the BYTES move; refresh timing
// still rides the observer's upload_generation, so an A/B here is attributable
// to content alone.
//
// This is the switch that makes the observer's content machinery deletable:
// upload_data, RecentCpuTiledTextureUpload and its dimension whitelist,
// ResolveGuestPhysicalTexelView, GuestTextureSourceHasContentCached, the
// high-alias search and the preserved-nonzero guards all exist only to answer
// "where are the bytes", which the unlock root states outright.
// DEFAULT ON since 2026-07-22 (run 148): the GTL channel supplied strictly
// more content than the observer — 4 resources larger, 20 equal, 0 smaller,
// two of them going from 0 bytes to ~240KB/~221KB — at aggregate parity
// (92 distinct textures with content, both runs) and 0 errors.
REXCVAR_DEFINE_BOOL(renderer_gtl_content_authority, true, "Renderer",
                    "Texture content comes from the GTL unlock-range store "
                    "instead of the observer's capture machinery.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

// Float shader constants: feed-7 (OnShaderConstantDispatch) deletion
// prerequisite. The device object's own float banks (dev+0x780 vertex /
// dev+0x1780 pixel — the SAME staging the guest's deferred PM4 SET_CONSTANT
// flush lands in, and the block sub_82214E30's direct VMX memcpy writes,
// which NO hook feed can ever see) become the GTL's sole at-draw constant
// authority: the packet-build ENTER capture prefers the direct device read
// over the observer-fed bank mirror, and the authoritative EXIT self-read
// (which all six draw roots already run) stays the post-flush overwrite it
// has always been. The observer feed keeps dual-writing the mirror while the
// divergence census (constant_census[...] stats line) grades fed-vs-device
// agreement; deletion of the feed requires a verified run with divergence=0.
// OFF restores the fed-bank-preferred ENTER capture for A/B.
REXCVAR_DEFINE_BOOL(renderer_gtl_constant_self_read, true, "Renderer",
                    "GTL reads float shader-constant banks directly from the "
                    "guest device object at draw capture instead of "
                    "preferring the observer-fed bank mirror (feed-7 "
                    "deletion prerequisite).")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

// Physical-view reads (IDA 0x821F6300 D3D::LockResource). A Xenos texture
// fetch constant stores a PHYSICAL base (fetch.base_address << 12, e.g.
// 0x1ECFF000). Refii reads guest memory as `membase + guest_address`, so a
// physical base read raw lands in the LOW VIRTUAL HEAP — different pages
// entirely (rexglue maps 0x00000000-0x3FFFFFFF from file offset 0, physical
// from 0x100000000). That heap is usually committed and non-zero, so the
// wrong read looks like content and never raises an error.
//
// With this ON, physical addresses are read through 0xC0000000|P, which the
// XDK itself hands the guest (the `- 0x40000000` term our decode helper
// drops). OFF keeps the raw read for A/B.
// STAYS DEFAULT OFF — do not flip it, the A/B was already paid for.
// Runs 144 (off) vs 145 (on) measured a NET REGRESSION: distinct textures with
// content 95 -> 77 (20 lost, 2 gained). This flag is wired into
// fable_texture_fetch.cpp, i.e. the OBSERVER capture path, whose blank-vs-
// content guards are calibrated against the very reads that were never valid;
// correcting the address underneath them stops the preservation guard firing
// and a blank fetch-base capture then OVERWRITES good content
// (store_object_fetch ADVANCED 35 -> 39, preserved-nonzero 49 -> 43).
//
// The addressing claim itself is CONFIRMED and keeps being re-confirmed — the
// run-264 drain-watch census sampled the same textures through both forms 2953
// times: 1788 blank-at-raw-but-content-through-the-view, 1151 blank in both,
// only 14 with content at the raw address. That is real, and it IS why the
// storybook menu panels capture empty (560x360 fmt=6 bases like 0x12CFD000,
// logged "rejected unproven blank", after which the draw binds nothing).
// But the fix belongs in the GTL content path, where no guard maze depends on
// the read address — NOT here. See refii-physical-view-addressing.md.
REXCVAR_DEFINE_BOOL(renderer_physical_view_reads, false, "Renderer",
                    "Read guest texture content through the 0xC0000000 "
                    "physical view instead of treating a Xenos physical base "
                    "as a low virtual address.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(renderer_lock_returns_decoded_view, false, "Renderer",
                    "Resource lock emulation returns the decoded guest view "
                    "instead of the write-combined alias, so guest writes land "
                    "where the capture paths read.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

// Migration step 3b (docs/refii_gtl_migration.md Sequencing addendum): the GTL
// ResolvePacket stream owning the resolve compose.
//
// RE-WIRED 2026-07-17 (it was inert after the bind-seam source override was
// deleted). §3b's census now passes its doc gates — every D3DDevice_Resolve is
// captured, flags&7 always names a bound RT/DS, and the destination fetch
// constant always reads — which was the stated condition for re-wiring.
//
// First real reader: AddGtlPacketResourceInterestsLocked claims each
// ResolvePacket's dest_texture as materialization interest. Resolve
// destinations that are never sampled/bound had no host resource at all
// (measured ~26% of destinations, run 449), because only the DrawPacket stream
// drove materialization.
// DEFAULT ON since 2026-07-18: live interleaved execution verified — title
// composes correctly across passive runs (screenshots + rdc frame-4405
// structural proof: resolve draws write dests at packet_id positions, guest
// shaders sample them downstream), no crash/hang, refusal counters honest
// (depth deferred, ambiguous identity refused). Per-destination ownership
// suppresses observer replay only for GTL-recorded dests; export-invalidate
// page tiles keep the observer fallback (blanket suppression scrambled the
// storybook strip — see fable_refii_replay_builders.cpp). Menu/in-game sweep
// still owed; flip OFF via REX_RENDERER_GTL_PRIVATE_RECT_COMPOSE=0 to A/B.
REXCVAR_DEFINE_BOOL(renderer_gtl_private_rect_compose, true, "Renderer",
                    "GTL ResolvePacket resolve-compose ownership (step 3b): "
                    "interleaved kGtlResolve execution + packet-driven "
                    "destination materialization.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(renderer_trace_resources, false, "Renderer",
                    "Log renderer resource create/lock/unlock/destroy flow.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(renderer_trace_draws, false, "Renderer",
                    "Log draw, state, and presentation calls.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(
    renderer_trace_verbose, false, "Renderer",
    "Log high-volume renderer details while tracing is enabled.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(
    renderer_hook_telemetry, false, "Renderer/Debug",
    "Per-hooked-call Tracy plots, gap timers and last-symbol text. Measured "
    "2026-07-28 at ~37us per call over 2.4M calls = 43% of wall time, on a path "
    "that runs for every guest D3D call. Tracy is compiled in, so each plot "
    "costs work even with no profiler attached. DEFAULT OFF; enable only when "
    "actually profiling with Tracy.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(
    renderer_observer_resource_exit, false, "Renderer/Replacement",
    "Run the observer's RESOURCE-kind exit path. DEFAULT OFF 2026-07-28, the "
    "follow-up to the draw gate: with draws bypassed, "
    "rex_XGOffsetBaseTextureAddress became the single largest cost at 2.38ms "
    "per call over 5158 calls (12.3s) -- for a guest function IDA shows is ~10 "
    "instructions that rebase a texture header field, with no allocation, no "
    "lock and no device contact. The GTL has owned that root since the "
    "2026-07-17 kOffsetBaseTextureAddress fix. "
    "RISK: observer FETCH capture is load-bearing for texture content; if "
    "textures go missing or stale, restore this before anything else. "
    "DOES NOT GATE D3DResource_AddRef/Release (IsFableResourceLifetimeHook) -- "
    "that exemption was added the same day after this cvar was found to have "
    "disabled the only resource-retirement path, making the table grow "
    "unbounded and the title degrade 742 -> 1650 ms/frame. See the comment at "
    "OnRendererGuestHookSuppressedForPlume.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(
    renderer_observer_draw_exit, false, "Renderer/Replacement",
    "Run the observer's DRAW-kind exit path. DEFAULT OFF 2026-07-28: measured at "
    "95% of ALL hook-wrapper cost on the guest thread (observe_ms=20871 against "
    "submit=360, resource=841, gtlexit=8) during a run where the guest collapsed "
    "to 0.3 frames/sec, while IDA shows every hooked guest function in that set "
    "is a straight-line O(1) PM4 ring writer. Draws are already 100% "
    "GTL-authoritative, so this path should be redundant for kDraw. "
    "Set TRUE to restore it if the picture regresses -- the failure mode is "
    "silent visual corruption, not a crash, so judge it on the image and not on "
    "the absence of errors.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(
    renderer_root_only_hooks, true, "Renderer/Replacement",
    "Run the default Plume renderer path with only root/wrapper hooks active; "
    "proof-only observer hooks require renderer_deep_observer_hooks.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(
    renderer_deep_observer_hooks, false, "Renderer/Debug",
    "Enable high-volume renderer observer and proof hooks while "
    "renderer_root_only_hooks is active.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(
    renderer_scene_stream_repair, false, "Renderer/Replacement",
    "Mirror Fable scene-stream publish cursors on the Plume path "
    "(SceneStreamSyncPublishCursor/SceneStreamRefresh emulation). Proven "
    "2026-07-09 to corrupt scene-stream message framing: with it on, the "
    "title GUI geometry/queue world-commands never decode and the title "
    "renders blank. Keep off unless diagnosing the startup publish stall.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(
    renderer_observe_hooks_on_native, false, "Renderer/Debug",
    "Run renderer hook observers under the non-Plume (xenia) backend for "
    "traversal diagnostics. Guest bodies always run; no replacement or "
    "suppression happens on the native backend.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(
    renderer_replace_resources, true, "Renderer/Replacement",
    "Enable Plume-backed resource creation, upload, and lifetime "
    "replacement while the Plume backend is selected.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(
    renderer_replace_state, true, "Renderer/Replacement",
    "Enable Plume-owned Fable render-state replacement while the "
    "Plume backend is selected.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(renderer_replace_draws, true, "Renderer/Replacement",
                    "Enable Plume-backed Fable draw command execution while "
                    "the Plume backend is selected.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(renderer_replace_present, true, "Renderer/Replacement",
                    "Enable Plume-backed Fable present/backbuffer replacement "
                    "while the Plume backend is selected.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(renderer_plume_owns_command_submission, true,
                    "Renderer/Replacement",
                    "Let Plume own Fable indirect command-buffer submission "
                    "hooks after advancing their guest-visible bookkeeping.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(
    renderer_shader_require_precompiled, true, "Renderer/Replacement",
    "Require a precompiled shader artifact before a Fable shader "
    "can participate in Plume draw replacement.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(renderer_imgui_enable, false, "Renderer/ImGui",
                    "Enable the ReXGlue ImGui interface on the Plume renderer path.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(
    renderer_imgui_show_stats, false, "Renderer/ImGui",
    "Show Refii renderer stats windows when ImGui is enabled.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(
    renderer_imgui_dim_fable, false, "Renderer/ImGui",
    "Draw a dimmer between Fable 2 and the Plume ImGui overlay.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_DOUBLE(renderer_imgui_dim_alpha, 0.55, "Renderer/ImGui",
                      "Opacity for the Plume ImGui dimmer over Fable 2.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload)
    .range(0.0, 0.95);

REXCVAR_DEFINE_BOOL(renderer_debug_test_triangle, false, "Renderer/Debug",
                    "Draw a Plume-native diagnostic triangle over the game "
                    "output to validate swapchain and pipeline rendering.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(renderer_plume_vsync, false, "Renderer/Plume",
                    "Enable Plume swapchain vsync. Disable to let the "
                    "replacement renderer present as fast as Fable submits "
                    "frames.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

// Native resolution override (2026-08-02). Fable calls XGetVideoMode then
// IGNORES it, hardcoding 1280x720 at sub_82A1AC98 (li 0x500/li 0x2D0) into
// the param block it hands Render_InitPresentationAndFirstSwap (0x82B6EA60).
// The entry hook in d3d_pass_through.cpp rewrites the block's width/height
// when both cvars are nonzero; device creation then publishes the new dims
// to the guest's single source of truth (qword_834A5FE8, ~60 readers, no
// other writer) so the whole RT set, downsample chain and pass setup follow.
// Values are aligned to the guest's own resize rules (width to 160, height
// to 16, both rounded UP — sub_82B6EBC0). 0/0 = stock 1280x720.
REXCVAR_DEFINE_UINT32(renderer_native_width, 0, "Renderer",
                      "Guest render width override (0 = stock 1280; aligned "
                      "up to a multiple of 160).");
REXCVAR_DEFINE_UINT32(renderer_native_height, 0, "Renderer",
                      "Guest render height override (0 = stock 720; aligned "
                      "up to a multiple of 16).");

REXCVAR_DEFINE_UINT32(renderer_plume_swapchain_images, 3, "Renderer/Plume",
                      "Requested Plume swapchain image count. Higher values "
                      "can reduce acquire stalls while replacing the original "
                      "renderer.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(renderer_plume_present_pump, true, "Renderer/Plume",
                    "Drive Plume presents from a renderer-owned FPS pump "
                    "instead of waiting for Fable's Plume-owned display-swap "
                    "hook cadence.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(
    renderer_plume_present_pump_fable_bookkeeping, true, "Renderer/Plume",
    "Let the Plume present pump advance Fable's guest-visible display "
    "submitted/completed counters when Plume-owned display-swap hooks stop "
    "arriving.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_UINT32(renderer_plume_present_fps, 60, "Renderer/Plume",
                      "Target frame rate for the Plume present pump.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

// Present-decoupling step 4 (2026-07-30): the pump's guest-driven wait wakes
// at the target-present interval instead of only on a guest present or the
// 250ms stall heartbeat. Wakes with no new guest content take the step-3
// re-present fast path (a compose-target blit), so the added cost per extra
// wake is ~a blit, never a re-record. OFF restores the guest-signal +
// 250ms-heartbeat wait exactly (one env flip for A/B).
REXCVAR_DEFINE_BOOL(renderer_present_fast_wake, true, "Renderer/Plume",
                    "Wake the Plume present pump at the target-present "
                    "interval (renderer_plume_present_fps) even without a "
                    "guest present signal. Disable to restore the 250ms "
                    "guest-stall heartbeat as the only timer wake.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

// Present-decoupling step 5 (2026-07-30): the pump->UI-thread present slot
// (g_plume_async_present_pending) used to be held until the queued present
// FINISHED executing, so every request arriving during execution was dropped
// — a rate halver at fast-wake rates. ON releases the slot when execution
// starts: at most one present queues behind the one executing (back-to-back
// presents pipeline), anything faster still coalesces into that single queued
// slot. OFF restores the drop-while-executing single slot for A/B.
REXCVAR_DEFINE_BOOL(renderer_present_uithread_pipeline, true, "Renderer/Plume",
                    "Re-arm the UI-thread present coalescer when a queued "
                    "present starts executing instead of when it finishes, "
                    "so back-to-back presents pipeline instead of being "
                    "dropped.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_STRING(renderer_vulkan_shader_root,
                      "assets/unpacked/shaders/vulkan", "Renderer/Vulkan",
                      "Directory containing compiled Vulkan shader artifacts.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(renderer_vulkan_validation, false, "Renderer/Vulkan",
                    "Enable Vulkan validation for the Plume renderer backend.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_STRING(
    renderer_d3d12_shader_root, "assets/unpacked/shaders/D3D12",
    "Renderer/D3D12",
    "Directory containing compiled DirectX 12 shader artifacts.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(
    renderer_d3d12_debug_layer, false, "Renderer/D3D12",
    "Enable the DirectX 12 debug layer for the Plume renderer backend.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_STRING(renderer_metal_shader_root,
                      "assets/unpacked/shaders/metal", "Renderer/Metal",
                      "Directory containing compiled Metal shader artifacts.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(
    renderer_renderdoc_enable, false, "Renderer/RenderDoc",
    "Enable RenderDoc in-application capture integration for the "
    "Refii renderer.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(renderer_renderdoc_capture_next_frame, false,
                    "Renderer/RenderDoc",
                    "Capture the next Plume-rendered frame with RenderDoc.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(
    renderer_renderdoc_trigger_capture, false, "Renderer/RenderDoc",
    "One-shot RenderDoc-owned capture request. This mirrors pressing "
    "RenderDoc's capture hotkey so Refii observes the capture and writes "
    "sidecars without owning EndFrameCapture.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_UINT32(
    renderer_renderdoc_capture_frame, 0, "Renderer/RenderDoc",
    "Capture a specific Plume frame with RenderDoc. 0 disables frame-index "
    "capture.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_UINT32(
    renderer_renderdoc_capture_draw, 0, "Renderer/RenderDoc",
    "Capture the first Plume frame whose queued direct draw packet has this "
    "draw ID. 0 disables draw-index capture.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_STRING(
    renderer_renderdoc_capture_draw_target, "", "Renderer/RenderDoc",
    "Capture the first Plume frame whose queued direct draw, record-time "
    "copy/resolve, or hook-root state matches this deep-observer target. "
    "Supported values: "
    "treebillboard, particlematerial, overdrawvisualise, "
    "postprocessingdownscale4, postprocessinggaussblur, screenfacingsprite, "
    "decal, texturedquadpreservestencil, titlehandoff, titlevisible, "
    "titleguistrip, binkvideo, pixvideo, surfacedestination, surfacecopy, "
    "resolvecopy, pm4resolve, allroots, rootcoverage, hookroots, "
    "root:HookName, root:0xFableAddress. Use titleguistrip for the early "
    "GUI-tonemap strip diagnostic; use titlevisible for logo/title billboard "
    "proof. Use allroots/rootcoverage for hook-root coverage; pm4resolve only "
    "watches packet-backed resolve-copy commands.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_STRING(
    renderer_renderdoc_capture_pass, "", "Renderer/RenderDoc",
    "Capture the first Plume frame whose queued direct draw belongs to this "
    "classified pass. Supported values include MainScene, Video, PrivateRect, "
    "DepthOnly, PostProcessOrUI, Immediate, PM4, and WrapperDraw.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(
    renderer_renderdoc_overlay, true, "Renderer/RenderDoc",
    "Show RenderDoc's capture overlay when the RenderDoc API is available.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(
    renderer_renderdoc_launch_replay, false, "Renderer/RenderDoc",
    "Ask RenderDoc to launch or focus its replay UI when a capture "
    "is triggered from Refii.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_BOOL(
    renderer_renderdoc_capture_on_error, false, "Renderer/RenderDoc",
    "Automatically capture the next Plume frame when a serious direct renderer "
    "error such as a final failed draw or resolve is reported.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_STRING(renderer_renderdoc_runtime_path, "", "Renderer/RenderDoc",
                      "Optional RenderDoc runtime library path. Empty searches "
                      "common system and local librenderdoc locations.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

REXCVAR_DEFINE_STRING(
    renderer_renderdoc_capture_path, "renderdoc/captures",
    "Renderer/RenderDoc",
    "Deprecated. Refii RenderDoc captures and sidecars are always written "
    "beside refii under renderdoc/captures.")
    .lifecycle(rex::cvar::Lifecycle::kHotReload);

bool EqualsAsciiIgnoreCase(std::string_view value,
                           std::string_view expected) {
  if (value.size() != expected.size()) {
    return false;
  }
  for (size_t i = 0; i < value.size(); ++i) {
    if (std::tolower(static_cast<unsigned char>(value[i])) !=
        std::tolower(static_cast<unsigned char>(expected[i]))) {
      return false;
    }
  }
  return true;
}

RendererBackend ParseRendererBackend(std::string_view backend) {
  if (EqualsAsciiIgnoreCase(backend, "plume")) {
    return RendererBackend::kPlume;
  }
  if (EqualsAsciiIgnoreCase(backend, "xenia") ||
      EqualsAsciiIgnoreCase(backend, "native")) {
    return RendererBackend::kNative;
  }
  if (EqualsAsciiIgnoreCase(backend, "disabled")) {
    return RendererBackend::kDisabled;
  }
  return RendererBackend::kNative;
}

RendererGraphicsApi ParseRendererGraphicsApi(std::string_view api) {
  if (EqualsAsciiIgnoreCase(api, "d3d12") ||
      EqualsAsciiIgnoreCase(api, "directx12") ||
      EqualsAsciiIgnoreCase(api, "directx 12")) {
    return RendererGraphicsApi::kD3D12;
  }
  if (EqualsAsciiIgnoreCase(api, "metal") ||
      EqualsAsciiIgnoreCase(api, "mtl")) {
    return RendererGraphicsApi::kMetal;
  }
  return RendererGraphicsApi::kVulkan;
}

std::string NormalizedRendererScaling() {
  std::string scaling = REXCVAR_GET(renderer_scaling);
  scaling.erase(
      std::remove_if(scaling.begin(), scaling.end(),
                     [](unsigned char ch) { return std::isspace(ch) != 0; }),
      scaling.end());
  std::transform(
      scaling.begin(), scaling.end(), scaling.begin(),
      [](unsigned char ch) { return static_cast<char>(std::tolower(ch)); });
  return scaling;
}

} // namespace

bool RendererSceneStreamRepairEnabled() {
  return REXCVAR_GET(renderer_scene_stream_repair);
}

bool RendererHooksEnabled() {
  return GetRendererBackend() == RendererBackend::kPlume ||
         REXCVAR_GET(renderer_observe_hooks_on_native);
}

bool RendererHookTracingEnabled() {
  return RendererHooksEnabled() && REXCVAR_GET(renderer_trace_hooks);
}

bool RendererResourceTracingEnabled() {
  return RendererHooksEnabled() && REXCVAR_GET(renderer_trace_resources);
}

bool RendererDrawTracingEnabled() {
  return RendererHooksEnabled() && REXCVAR_GET(renderer_trace_draws);
}

bool RendererVerboseTracingEnabled() {
  return RendererHooksEnabled() && REXCVAR_GET(renderer_trace_verbose);
}

bool RendererHookTelemetryEnabled() {
  return REXCVAR_GET(renderer_hook_telemetry);
}

bool RendererObserverDrawExitEnabled() {
  return REXCVAR_GET(renderer_observer_draw_exit);
}

bool RendererObserverResourceExitEnabled() {
  return REXCVAR_GET(renderer_observer_resource_exit);
}

bool RendererRootOnlyHooksEnabled() {
  return RendererHooksEnabled() && REXCVAR_GET(renderer_root_only_hooks);
}

bool RendererDeepObserverHooksEnabled() {
  return RendererHooksEnabled() && REXCVAR_GET(renderer_deep_observer_hooks);
}

bool RendererGtlExecutorShadowEnabled() {
  return RendererHooksEnabled() && REXCVAR_GET(renderer_gtl_executor_shadow);
}

bool RendererGtlVideoDrawsEnabled() {
  return RendererHooksEnabled() && REXCVAR_GET(renderer_gtl_video_draws);
}

bool RendererGtlResolveClearsEnabled() {
  return RendererHooksEnabled() && REXCVAR_GET(renderer_gtl_resolve_clears);
}

bool RendererGtlClearsAuthoritativeEnabled() {
  return RendererHooksEnabled() &&
         REXCVAR_GET(renderer_gtl_clears_authoritative);
}

bool RendererGtlDepthResolvesEnabled() {
  return RendererHooksEnabled() && REXCVAR_GET(renderer_gtl_depth_resolves);
}


bool RendererGtlLiveCullAuthorityEnabled() {
  return RendererHooksEnabled() &&
         REXCVAR_GET(renderer_gtl_live_cull_authority);
}

bool RendererBinkRealtimeAudioClockEnabled() {
  return RendererHooksEnabled() &&
         REXCVAR_GET(renderer_bink_realtime_audio_clock);
}

bool RendererBinkChannelCaptureGateEnabled() {
  return RendererHooksEnabled() &&
         REXCVAR_GET(renderer_bink_channel_capture_gate);
}

bool RendererUnlockDirtyRangesEnabled() {
  return RendererHooksEnabled() && REXCVAR_GET(renderer_unlock_dirty_ranges);
}

bool RendererGtlContentAuthorityEnabled() {
  return RendererHooksEnabled() &&
         REXCVAR_GET(renderer_gtl_content_authority);
}

bool RendererGtlConstantSelfReadEnabled() {
  return RendererHooksEnabled() &&
         REXCVAR_GET(renderer_gtl_constant_self_read);
}

bool RendererPhysicalViewReadsEnabled() {
  return RendererHooksEnabled() && REXCVAR_GET(renderer_physical_view_reads);
}

bool RendererLockReturnsDecodedViewEnabled() {
  return RendererHooksEnabled() &&
         REXCVAR_GET(renderer_lock_returns_decoded_view);
}


bool RendererGtlExecutorEnabled() {
  return RendererHooksEnabled() && REXCVAR_GET(renderer_gtl_executor);
}

bool RendererGtlSubmissionEnabled() {
  return RendererHooksEnabled() && REXCVAR_GET(renderer_gtl_submission);
}

bool RendererBacklogSupersessionEnabled() {
  return RendererHooksEnabled() && REXCVAR_GET(renderer_backlog_supersession);
}

bool RendererRecordBacklogCapEnabled() {
  return RendererHooksEnabled() && REXCVAR_GET(renderer_record_backlog_cap);
}

bool RendererGtlStatedPresentSourceEnabled() {
  return RendererHooksEnabled() &&
         REXCVAR_GET(renderer_gtl_stated_present_source);
}

bool RendererGtlAuthoritativeSubmissionEnabled() {
  return RendererGtlSubmissionEnabled() &&
         REXCVAR_GET(renderer_gtl_authoritative_submission);
}

bool RendererPlumeGuestDrivenPresentEnabled() {
  return RendererHooksEnabled() &&
         REXCVAR_GET(renderer_plume_guest_driven_present);
}

bool RendererGtlPrivateRectComposeEnabled() {
  return RendererHooksEnabled() &&
         REXCVAR_GET(renderer_gtl_private_rect_compose);
}

bool RendererReplaceResourcesEnabled() {
  return PlumeRendererBackendEnabled() &&
         REXCVAR_GET(renderer_replace_resources);
}

bool RendererReplaceStateEnabled() {
  return PlumeRendererBackendEnabled() && REXCVAR_GET(renderer_replace_state);
}

bool RendererReplaceDrawsEnabled() {
  return PlumeRendererBackendEnabled() && REXCVAR_GET(renderer_replace_draws);
}

bool RendererReplacePresentEnabled() {
  return PlumeRendererBackendEnabled() && REXCVAR_GET(renderer_replace_present);
}

bool RendererPlumeOwnsCommandSubmissionEnabled() {
  return RendererReplaceDrawsEnabled() &&
         REXCVAR_GET(renderer_plume_owns_command_submission);
}

bool RendererPlumePacketQueueReplayEnabled() {
  return RendererReplaceDrawsEnabled();
}

bool RendererShaderRequirePrecompiled() {
  return PlumeRendererBackendEnabled() &&
         REXCVAR_GET(renderer_shader_require_precompiled);
}

bool RendererImGuiEnabled() {
  return PlumeRendererBackendEnabled() && REXCVAR_GET(renderer_imgui_enable);
}

bool RendererImGuiStatsVisible() {
  return RendererImGuiEnabled() && REXCVAR_GET(renderer_imgui_show_stats);
}

bool RendererImGuiDimFableEnabled() {
  return RendererImGuiEnabled() && REXCVAR_GET(renderer_imgui_dim_fable);
}

float RendererImGuiDimFableAlpha() {
  return static_cast<float>(
      std::clamp(REXCVAR_GET(renderer_imgui_dim_alpha), 0.0, 0.95));
}

bool RendererDebugTestTriangleEnabled() {
  return PlumeRendererBackendEnabled() &&
         REXCVAR_GET(renderer_debug_test_triangle);
}

bool RendererPlumeVsyncEnabled() {
  return PlumeRendererBackendEnabled() && REXCVAR_GET(renderer_plume_vsync);
}

uint32_t RendererPlumeSwapchainImageCount() {
  return std::clamp<uint32_t>(REXCVAR_GET(renderer_plume_swapchain_images), 2,
                              4);
}

// *** WE DO NOT TELL THE GUEST ITS RESOLUTION. *** (2026-08-04)
//
// These feed the sub_82B6EA60 entry hook in d3d_pass_through.cpp, the only
// lever that reaches qword_834A5FE8. Between 2026-08-03 and 2026-08-04 they
// fell back to RendererOutputWidth/Height, which turned an opt-in override
// into an ALWAYS-ON guest patch: the guest was told it renders at the window
// size. That is reverted. The fallback is gone; the cvars default to 0, so
// the hook's `requested != 0` guard keeps it inert unless someone explicitly
// asks for it.
//
// WHY, from the binary (2026-08-04):
//  * The guest HARDCODES 1280x720. sub_82A1AC98 @0x82A1AC98 calls
//    XGetVideoMode and consumes ONLY the refresh rate:
//      /*0x82a1ad48*/ li r10, 0x500   (1280)
//      /*0x82a1ad50*/ li r9,  0x2D0   (720)
//      /*0x82a1ad54*/ v17[5] = RefreshRate
//  * Any height we state is requantized. sub_82B6EBC0 @0x82B6EC08 does
//    `(a2 + 15) & 0xFFFFFFF0`, so 1080 becomes 1088 — the guest then composes
//    on a 1088 grid while we present 1080. A 0.74% vertical error on every
//    2D rect and every resolve. THIS is what "everything other than 720p is
//    out of whack" was.
//  * Render_AllocateSceneRenderTargetSet @0x82A4E708 picks the main scene
//    colour format 58 vs 97 on an EQUALITY test against this value, and two
//    more zero-tolerance equality tests key off it — one of them
//    (Render_LitMaterialSubmit @0x821E61F4) on EVERY LIT DRAW.
//  * The EDRAM budget gate sub_82295380 RELEASES the old surface first, then
//    returns SUCCESS WITH A NULL SURFACE above 0x800 tiles (= 10 MB, the whole
//    EDRAM). 1920x1088 at 2xMSAA is 3264 tiles.
//
// None of that constrains how many pixels WE rasterize. It is the guest
// managing an EDRAM it no longer owns. Native resolution is achieved
// host-side instead: the guest keeps its self-consistent 720p bookkeeping,
// our attachments are allocated at the output extent by
// FablePromotedRenderSurfaceExtent, and the ONE SCALER converts guest-space
// viewports/scissors at rasterization. Geometry is world-space and clip space
// is resolution-independent, so that is TRUE native rendering, not an upscale.
//
// Corollary that must hold: never write host-space values into guest-readable
// device fields (dev+0x3168..0x3174). The guest reads those back to build its
// own 2D compose canvas.
uint32_t RendererNativeWidthOverride() {
  return REXCVAR_GET(renderer_native_width);
}

uint32_t RendererNativeHeightOverride() {
  return REXCVAR_GET(renderer_native_height);
}

// THE REQUESTED EXTENT IS PASSED THROUGH VERBATIM. Ask for 1920x1080 and the
// guest renders exactly 1920x1080 -- no rounding, no padding.
//
// A previous version rounded here (width up to 160, height up to 16) because
// the guest's RESIZE path sub_82B6EBC0 does that. **That was wrong at boot.**
// IDA on the boot path sub_82B6F6C0 shows our two param-block dwords reaching
// BOTH consumers untouched:
//     v9  = a1[2];                     /*0x82b6f79c*/   width
//     v10 = a1[3];                     /*0x82b6f7a4*/   height
//     v45[0] = v9; v45[1] = v10;                        -> presentation params
//     HIDWORD(qword_834A5FE8) = v9;    /*0x82b6f9a0*/   -> resolution global
//     LODWORD(qword_834A5FE8) = v10;   /*0x82b6f9a4*/
// There is no alignment on this path. The 160/16 rounding exists ONLY in the
// runtime resize path, which does not run at boot, so pre-applying it just
// handed the guest 1088 rows nobody asked for (run 545).
//
// Nothing downstream needs the padding either: the EDRAM tiler aligns its own
// slice (align_up(H,32)), XGSurfaceSize aligns internally for tile counting,
// and Fable_D3D_CreateDisplayOrRenderTargetSurfaces aligns width to 80 itself.
//
// These remain a SINGLE SHARED helper on purpose: every host path that states
// an extent to the guest must go through them, or the paths drift. Run 544 is
// what drift costs -- the boot hook said 1920x1088 while the presentation
// patch said 1920x1080 26ms later, and the guest allocated its scene
// render-target set TWICE (a 960x544/480x272 chain and a 960x540/480x270 chain
// in the same frame).
uint32_t RendererGuestAlignedRenderWidth(uint32_t width) { return width; }

uint32_t RendererGuestAlignedRenderHeight(uint32_t height) { return height; }

// Diagnostic only: distinguishes "the launcher resolution drove this" from "a
// renderer_native_* cvar was set explicitly", so the boot log says which.
bool RendererNativeWidthOverrideIsExplicit() {
  return REXCVAR_GET(renderer_native_width) != 0 &&
         REXCVAR_GET(renderer_native_height) != 0;
}

bool RendererPlumePresentPumpEnabled() {
  return PlumeRendererBackendEnabled() &&
         REXCVAR_GET(renderer_plume_present_pump);
}

bool RendererPlumePresentPumpFableBookkeepingEnabled() {
  return RendererPlumePresentPumpEnabled() &&
         REXCVAR_GET(renderer_plume_present_pump_fable_bookkeeping);
}

uint32_t RendererPlumePresentTargetFps() {
  return std::clamp<uint32_t>(REXCVAR_GET(renderer_plume_present_fps), 1, 240);
}

bool RendererPresentFastWakeEnabled() {
  return PlumeRendererBackendEnabled() &&
         REXCVAR_GET(renderer_present_fast_wake);
}

bool RendererPresentUIThreadPipelineEnabled() {
  return PlumeRendererBackendEnabled() &&
         REXCVAR_GET(renderer_present_uithread_pipeline);
}

bool RenderDocDebuggerEnabled() {
  // Backend-agnostic 2026-07-14: RenderDoc drives capture through the loaded
  // API + Vulkan layer, which work for whichever backend created the Vulkan
  // instance (Plume's or rexglue's for the native/xenia backend). The old
  // PlumeRendererBackendEnabled() requirement short-circuited every RenderDoc
  // function under backend=xenia, so xenia runs captured nothing (run 147);
  // the native branch of OnRendererGuestHookEnter now arms the layer + API
  // itself. Plume mode is unchanged (the cvar is the only condition there).
  return REXCVAR_GET(renderer_renderdoc_enable);
}

// Native/xenia RenderDoc arming. DEFAULT ON (2026-07-19 evening): the black
// xenia window initially blamed on the layer was actually the
// REX_UI_HAS_WAYLAND_WINDOW ABI split (see the SDK shadow CMakeLists) —
// with layouts unified the layer is proven compatible, and the
// xenia-reference capture workflow (F12) needs arming to follow
// renderer_renderdoc_enable like it always did. Kept as a kill-switch:
// REX_RENDERER_RENDERDOC_NATIVE_ARM=0 isolates the layer in one run.
REXCVAR_DEFINE_BOOL(renderer_renderdoc_native_arm, true, "Renderer/RenderDoc",
                    "Arm the RenderDoc Vulkan layer for the native/xenia "
                    "backend (F12 captures) when renderdoc is enabled. "
                    "Kill-switch for isolating layer interactions.")
    .lifecycle(rex::cvar::Lifecycle::kInitOnly);

bool RenderDocNativeArmEnabled() {
  return RenderDocDebuggerEnabled() &&
         REXCVAR_GET(renderer_renderdoc_native_arm);
}

bool RenderDocOverlayEnabled() {
  return RenderDocDebuggerEnabled() && REXCVAR_GET(renderer_renderdoc_overlay);
}

bool RenderDocReplayLaunchEnabled() {
  return RenderDocDebuggerEnabled() &&
         REXCVAR_GET(renderer_renderdoc_launch_replay);
}

bool RenderDocCaptureOnErrorEnabled() {
  return RenderDocDebuggerEnabled() &&
         REXCVAR_GET(renderer_renderdoc_capture_on_error);
}

static std::string NormalizeRenderDocCaptureReason(const char* reason) {
  if (reason == nullptr || reason[0] == '\0') {
    return {};
  }

  std::string normalized;
  for (const unsigned char* ch =
           reinterpret_cast<const unsigned char*>(reason);
       *ch != '\0'; ++ch) {
    if (std::isalnum(*ch) != 0) {
      normalized.push_back(static_cast<char>(std::tolower(*ch)));
    } else if (!normalized.empty() && normalized.back() != '_') {
      normalized.push_back('_');
    }
  }
  while (!normalized.empty() && normalized.back() == '_') {
    normalized.pop_back();
  }
  return normalized;
}

bool RequestRenderDocFrameCapture() {
  if (!RenderDocDebuggerEnabled()) {
    return false;
  }

  {
    std::lock_guard<std::mutex> lock(g_renderdoc_capture_reason_mutex);
    g_renderdoc_capture_reason.clear();
  }
  REXCVAR_SET(renderer_renderdoc_capture_next_frame, true);
  return true;
}

bool RequestRenderDocFrameCaptureWithReason(const char* reason) {
  if (!RequestRenderDocFrameCapture()) {
    return false;
  }

  const std::string normalized_reason = NormalizeRenderDocCaptureReason(reason);
  if (!normalized_reason.empty()) {
    std::lock_guard<std::mutex> lock(g_renderdoc_capture_reason_mutex);
    g_renderdoc_capture_reason = normalized_reason;
  }
  return true;
}

bool RenderDocTriggerCaptureRequestPending() {
  if (!RenderDocDebuggerEnabled() ||
      !REXCVAR_GET(renderer_renderdoc_trigger_capture)) {
    return false;
  }

  return true;
}

void ClearRenderDocTriggerCaptureRequest() {
  REXCVAR_SET(renderer_renderdoc_trigger_capture, false);
}

bool ConsumeRenderDocTriggerCaptureRequest() {
  if (!RenderDocTriggerCaptureRequestPending()) {
    return false;
  }

  ClearRenderDocTriggerCaptureRequest();
  return true;
}

bool RenderDocCaptureRequestPending() {
  if (!RenderDocDebuggerEnabled() ||
      !REXCVAR_GET(renderer_renderdoc_capture_next_frame)) {
    return false;
  }

  return true;
}

void ClearRenderDocCaptureRequest() {
  REXCVAR_SET(renderer_renderdoc_capture_next_frame, false);
}


uint32_t RenderDocCaptureFrameIndex() {
  return RenderDocDebuggerEnabled()
             ? REXCVAR_GET(renderer_renderdoc_capture_frame)
             : 0u;
}

uint32_t RenderDocCaptureDrawIndex() {
  return RenderDocDebuggerEnabled()
             ? REXCVAR_GET(renderer_renderdoc_capture_draw)
             : 0u;
}

std::string RenderDocCaptureDrawTarget() {
  if (!RenderDocDebuggerEnabled()) {
    return {};
  }

  std::string target = REXCVAR_GET(renderer_renderdoc_capture_draw_target);
  target.erase(
      std::remove_if(target.begin(), target.end(),
                     [](unsigned char ch) { return std::isspace(ch) != 0; }),
      target.end());
  std::transform(
      target.begin(), target.end(), target.begin(),
      [](unsigned char ch) { return static_cast<char>(std::tolower(ch)); });
  return target;
}

std::string RenderDocCapturePassName() {
  if (!RenderDocDebuggerEnabled()) {
    return {};
  }

  std::string pass_name = REXCVAR_GET(renderer_renderdoc_capture_pass);
  pass_name.erase(
      std::remove_if(pass_name.begin(), pass_name.end(),
                     [](unsigned char ch) { return std::isalnum(ch) == 0; }),
      pass_name.end());
  std::transform(
      pass_name.begin(), pass_name.end(), pass_name.begin(),
      [](unsigned char ch) { return static_cast<char>(std::tolower(ch)); });
  return pass_name;
}

std::string RenderDocCaptureReason() {
  std::lock_guard<std::mutex> lock(g_renderdoc_capture_reason_mutex);
  return g_renderdoc_capture_reason;
}

void ClearRenderDocCaptureReason() {
  std::lock_guard<std::mutex> lock(g_renderdoc_capture_reason_mutex);
  g_renderdoc_capture_reason.clear();
}

// Backend cache: -1 = not frozen (parse the cvar string each call — needed
// during early boot while TOML + launcher env overrides are still landing).
// FreezeRendererBackendCache() latches the final value at OnPreSetup, after
// the backend-split banner; from then on every hook-path query is a load
// instead of a string parse (GetRendererBackend sat in the per-D3D-call
// profile of xenia runs — core 390038 caught a thread inside the
// string_view compare).
static std::atomic<int> g_renderer_backend_cache{-1};

RendererBackend GetRendererBackend() {
  const int cached = g_renderer_backend_cache.load(std::memory_order_relaxed);
  if (cached >= 0) {
    return static_cast<RendererBackend>(cached);
  }
  // Runtime settings are loaded from TOML and then environment overrides are
  // reapplied by the launcher.  Caching the first observation can therefore
  // pin hook ownership to the TOML backend even though the effective setting
  // (and startup banner) says otherwise — hence the explicit freeze point.
  return ParseRendererBackend(REXCVAR_GET(renderer_backend));
}

void FreezeRendererBackendCache() {
  const RendererBackend backend =
      ParseRendererBackend(REXCVAR_GET(renderer_backend));
  g_renderer_backend_cache.store(static_cast<int>(backend),
                                 std::memory_order_relaxed);
}

RendererGraphicsApi GetRendererGraphicsApi() {
  return ParseRendererGraphicsApi(REXCVAR_GET(renderer_plume_api));
}

bool PlumeRendererBackendEnabled() {
  return GetRendererBackend() == RendererBackend::kPlume && PlumeAvailable();
}






int RendererGuestVideoWidth() {
  int32_t preset_width = 0;
  int32_t preset_height = 0;
  if (rex::graphics::video_mode_util::TryGetResolutionPresetFromCVar(
          preset_width, preset_height)) {
    return std::max(1, preset_width);
  }
  return std::max(1, REXCVAR_GET(video_mode_width));
}

// Restored 2026-07-29 alongside RendererOutputHeight — this had been deleted
// when the output size was pinned to a 720p constant, leaving only the Width
// half of the pair.
int RendererGuestVideoHeight() {
  int32_t preset_width = 0;
  int32_t preset_height = 0;
  if (rex::graphics::video_mode_util::TryGetResolutionPresetFromCVar(
          preset_width, preset_height)) {
    return std::max(1, preset_height);
  }
  return std::max(1, REXCVAR_GET(video_mode_height));
}


// RESTORED 2026-07-29 — render at the REQUESTED resolution, not a hardcoded
// 1280x720.
//
// These had been pinned to a `kFableGuestNativeWidth/Height = 1280/720`
// constant, which meant DesiredFableNativeRenderExtent() always computed the
// resolution the guest already had. PatchFablePresentationParametersLocked
// then took its "old == desired, nothing to do" early-out, so the whole
// native-resolution path was inert and every run rendered 720p and upscaled at
// present. That constant was never committed — it was a working-tree change.
//
// video_mode_width/height are the SAME cvars that size the SDL window
// (rexglue shadow window_sdl.cpp), so RendererGuestVideoWidth/Height IS the
// requested output resolution. Feeding it here makes the GUEST render at that
// resolution: PatchFablePresentationParametersLocked writes the extent into the
// guest's own presentation parameters, so the guest's coordinate space becomes
// the output space and no per-draw rescale is needed. That is a different axis
// from supersampling a promoted target while the guest still believes it is
// 1280x720 — the fragile case the previous comment warned about.
//
// The guest-space rescale paths stay as the safety net for anything that still
// reports native-sized viewports: ScaleGuestViewportToCurrentDrawSurface on the
// observer path, and the vp_scale_x/y block in the GTL executor
// (fable_direct_renderer.cpp) which only scales when the framebuffer exceeds
// 1280x720 AND the draw's viewport is native-or-smaller — so a genuinely
// 1080p-space viewport is left alone rather than double-scaled.
//
// STILL DELIBERATELY SEPARATE: RendererInternalScale (renderer_scaling 2x/3x/4x)
// multiplies on top of this. Supersampling has its own unresolved issue with
// kScratchWrapper composite quads on the observer path; leave it at 1x until the
// GTL owns all draws.
int RendererOutputWidth() { return RendererGuestVideoWidth(); }

int RendererOutputHeight() { return RendererGuestVideoHeight(); }

int RendererInternalScale() {
  const std::string scaling = NormalizedRendererScaling();
  if (scaling == "4" || scaling == "4x") {
    return 4;
  }
  if (scaling == "3" || scaling == "3x") {
    return 3;
  }
  if (scaling == "2" || scaling == "2x") {
    return 2;
  }
  return 1;
}

inline namespace detail {



std::string ConfiguredRendererVulkanShaderRoot() {
  return REXCVAR_GET(renderer_vulkan_shader_root);
}

std::string ConfiguredRendererD3D12ShaderRoot() {
  return REXCVAR_GET(renderer_d3d12_shader_root);
}

std::string ConfiguredRendererMetalShaderRoot() {
  return REXCVAR_GET(renderer_metal_shader_root);
}

std::string ConfiguredRendererRenderDocRuntimePath() {
  return REXCVAR_GET(renderer_renderdoc_runtime_path);
}


} // namespace detail
} // namespace refii::renderer
