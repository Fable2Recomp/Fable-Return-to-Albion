#pragma once

#include <cstddef>
#include <cstdint>

namespace refii::renderer {

// Guest display dimensions (native-resolution support, 2026-08-02).
//
// Fable publishes its render resolution to exactly ONE guest global,
// qword_834A5FE8 (width u32 at 0x834A5FE8, height at 0x834A5FEC, big-endian;
// writers are device-create 0x82b6f9a0/a4 and resize 0x82b6ec84 only). The
// whole guest RT set, downsample chain and viewport setup derive from it, and
// the sub_82B6EA60 entry hook in d3d_pass_through.cpp rewrites the boot param
// block so this global carries the renderer_native_width/height override.
//
// These helpers are the host-side mirror: every host-side "is this the scene
// framebuffer / display-sized target" test must compare against THESE instead
// of a hardcoded 1280x720. Video-plane CONTENT tests (Bink decodes at the
// movie's authored resolution) must NOT use them.
//
// Fallback order when the guest global is unreadable or implausible:
//   1. last plausible value read from the guest (cached),
//   2. the cvar override aligned by the guest's own resize rules
//      (width up to 160, height up to 16 — sub_82B6EBC0),
//   3. stock 1280x720.
// At stock (cvars unset) the guest global holds 1280x720, so behaviour is
// bit-identical to the old constants.

// Remember the guest memory base so the display pair can be read outside a
// hook context. Called from the hook-observer intake paths that receive it.
void NoteFableGuestDisplayMemoryBase(uint8_t* base);
// The remembered guest memory base, or nullptr before any hook has supplied it.
// Lets non-hook code (e.g. the texture upload path) read guest memory that a
// root STATED the address of, without threading a base through every caller.
uint8_t* FableGuestMemoryBase();

uint32_t FableGuestDisplayWidth();
uint32_t FableGuestDisplayHeight();

inline float FableGuestDisplayWidthF() {
  return static_cast<float>(FableGuestDisplayWidth());
}
inline float FableGuestDisplayHeightF() {
  return static_cast<float>(FableGuestDisplayHeight());
}

// THE SCENE RENDER EXTENT `E` (2026-08-04) — the stated argument that replaces
// every literal in FablePromotedRenderSurfaceExtent.
//
// `Render_AllocateSceneRenderTargetSet` (0x82A4E6D0, our
// rex_lhGfx_SetupRenderTargets) takes a POINTER to a {width, height} pair and
// allocates the entire scene set from it: full-res colour, full-res depth
// (g_ResolvedDepthTexture), plus half and quarter res. `sub_8227A440` then
// derives the rest of the chain the same way — and it does not use literals
// either, it calls D3D_Get2DSurfaceDesc on the target it just made and divides
// by 2 and 4. So the guest's whole scene chain is exactly {E, E/2, E/4}.
//
// E is NOT a constant and is not discoverable statically: it comes from the
// render VIEW descriptor (view+0x5D0 width / +0x5D4 height, read by
// Render_SubPassOrchestrator 0x821C3C60 at 0x821C3C98/9C), falling back to
// qword_834A5FE8 only when both are zero. 1120x720 appears nowhere in the
// binary as an immediate. That is precisely why literal matching kept losing:
// at stock 1280x720 the chain is 1280/640/320, and 640x360 / 320x180 were not
// in the whitelist at all.
//
// This is a STATED API ARGUMENT, not a decoded descriptor, so it is the
// architecturally correct source (CLAUDE.md §5.1).
//
// E IS PER-VIEW, SO THIS IS A SET, NOT A VALUE (measured run 571: E alternated
// 1920x1080 <-> 1120x720 four times in one session, both legitimate — a view
// with an explicit 0x5D0/0x5D4 override states 1120x720, a view without one
// falls back to the display extent). A single last-writer-wins slot silently
// switched promotion off for the whole scene chain whenever the display-sized
// view allocated last, with no log line to show for it.
//
// Empty until the allocator has been observed once. Callers must treat "no
// entries" as "not yet known" and fall back — never as an extent.
void NoteFableSceneRenderTargetExtent(uint32_t width, uint32_t height);
// Enumerate the observed set. Returns false when `index` holds no entry, so
// callers iterate to the first false.
bool FableSceneRenderTargetExtentAt(size_t index, uint32_t& width,
                                    uint32_t& height);
size_t FableSceneRenderTargetExtentCapacity();

}  // namespace refii::renderer
