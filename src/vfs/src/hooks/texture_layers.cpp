#include "vfs_hooks_private.h"

#include <cstdint>

#include "hooks/ppc_externs.h"
#include "core/guest_build.h"

// Each character's texture composites, taken where the guest states them.
//
// `sub_8220DE88` (CECGraphicAppearanceMorph secondary vtable slot 1, `this` = comp17+12, owning
// entity at `this`-8) gathers every texture layer a character wears — replacement and skin-set
// textures, the def's TextureMorphs at their `sub_822DA028` weight, tattoos, dyes — sorts each
// original texture's layers by Layer, and builds one command per original texture:
//
//   sub_825B38B0(cmd, renderInstance, &originalTextureId, layers, realtime)   0x8220f8f0
//
// `layers` is a vector header (begin +4, end +8) of 36-byte layers, written by `sub_825B3838`:
// +0 texture id, +4 blend mode, +8 weight, +12 has-tint, +16 tint bytes (B, G, R, A),
// +24 brightness. Layer 0 is the base and is drawn opaque whatever its mode (`sub_82A7EA28`).
//
// The same virtual states each part's SHAPE: per 24-byte built record it lists 8-byte {model
// hash, float weight} — element 0 the part's own model at 1 - sum (inserted at 0x8220e374),
// the rest the MorphTargets whose `sub_822DA028` weight exceeds 1e-4 (0x8220e30c..0x8220e334)
// — and constructs the descriptor with `sub_82A48B90(desc r3, flags r4, list r5, ...)`
// (0x8220e4c0 morphed, 0x8220e660 a part whose targets all weigh nothing), its only two
// callers. `list` is a vector header: begin +4, end +8.
//
// THE CONSTRUCTOR IS THE STATEMENT, NOT THE SUBMIT. `sub_82572988` is called on the unmorphed
// path only (0x8220e6c8); on the morphed path the compiler INLINED it (0x8220e50c..0x8220e58c
// tests the handle, writes the ring or calls `sub_821DE2E8`), so a hook there sees every part
// that has target records and none that is actually morphed — 2866 of 2866 in a measured run.
//
// `sub_8217FA50` is the part rebuild the same virtual runs first when the appearance changed
// (0x8220de9c); what was stated for the entity before it is dropped there.

namespace {

constexpr uint32_t kLayerBytes = 36u;

thread_local uint32_t t_entity = 0u;
thread_local bool t_begun = false;

void Begin() {
  if (t_entity != 0u && !t_begun) {
    t_begun = true;
    refii::vfs::BeginTextureLayers(t_entity);
  }
}

}  // namespace

REX_EXTERN(rex_lhGraphicAppearanceMorph_ApplyTextureLayers) {
  refii::vfs::EnsureAlternateSignalStack();
  REX_FUNC_PROLOGUE();
  const uint32_t outer = t_entity;
  const bool outer_begun = t_begun;
  t_entity = ctx.r3.u32 >= 12u ? refii::vfs::LoadGuestU32(base, ctx.r3.u32 - 8u) : 0u;
  t_begun = false;
  __imp__rex_lhGraphicAppearanceMorph_ApplyTextureLayers(ctx, base);
  t_entity = outer;
  t_begun = outer_begun;
}

REX_EXTERN(rex_lhGraphicAppearanceMorph_RebuildParts) {
  refii::vfs::EnsureAlternateSignalStack();
  REX_FUNC_PROLOGUE();
  Begin();
  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhGraphicAppearanceMorph_RebuildParts);
}

REX_EXTERN(rex_lhMorphDescriptor_Ctor) {
  refii::vfs::EnsureAlternateSignalStack();
  REX_FUNC_PROLOGUE();
  const uint32_t list = ctx.r5.u32;
  if (t_entity != 0u && list != 0u) {
    const uint32_t begin = refii::vfs::LoadGuestU32(base, list + 4u);
    const uint32_t end = refii::vfs::LoadGuestU32(base, list + 8u);
    if (begin != 0u && end > begin && ((end - begin) & 7u) == 0u && (end - begin) <= 8u * 64u) {
      uint32_t pairs[kPartMorphTargets * 2u];
      uint32_t count = 0u;
      for (uint32_t at = begin + 8u; at + 8u <= end && count < kPartMorphTargets; at += 8u) {
        pairs[count * 2u] = refii::vfs::LoadGuestU32(base, at);
        pairs[count * 2u + 1u] = refii::vfs::LoadGuestU32(base, at + 4u);
        ++count;
      }
      refii::vfs::NotePartMorph(t_entity, refii::vfs::LoadGuestU32(base, begin), pairs, count);
    }
  }
  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhMorphDescriptor_Ctor);
}

// OVERLAYS. Scars (`sub_825B8420`, 0x8220f938) and blend-mode-0 morphs (0x8220f988) are sent as
// command type 14 through `sub_8222AAC8(modelHandle, cmd, queue)`: cmd+0 vtable 0x820C57A4,
// cmd+4 original texture id, cmd+12..+16 a vector of 20-byte entries {float weight, diffuse,
// spec, bump, morph texture} (`sub_825B39C8`, `sub_825BA570`). The guest draws the mesh again
// per entry with that material, blended, wherever the base material's diffuse is the original.
// A scar fills diffuse and bump; a morph fills only the last word.
REX_EXTERN(rex_lhRenderCommandStream_PostModelCommand) {
  refii::vfs::EnsureAlternateSignalStack();
  REX_FUNC_PROLOGUE();
  const uint32_t kOverlayVtable = refii::guest::Address(0x820C57A4u);
  constexpr uint32_t kEntryBytes = 20u;
  const uint32_t command = ctx.r4.u32;
  if (t_entity != 0u && command != 0u &&
      refii::vfs::LoadGuestU32(base, command) == kOverlayVtable) {
    const uint32_t original = refii::vfs::LoadGuestU32(base, command + 4u);
    const uint32_t begin = refii::vfs::LoadGuestU32(base, command + 12u);
    const uint32_t end = refii::vfs::LoadGuestU32(base, command + 16u);
    if (begin != 0u && end > begin && ((end - begin) % kEntryBytes) == 0u &&
        (end - begin) <= kEntryBytes * 64u) {
      uint32_t words[kTextureLayersPerTexture * kTextureLayerWords];
      uint32_t count = 0u;
      for (uint32_t at = begin; at < end && count < kTextureLayersPerTexture;
           at += kEntryBytes) {
        const uint32_t diffuse = refii::vfs::LoadGuestU32(base, at + 4u);
        const uint32_t morph = refii::vfs::LoadGuestU32(base, at + 16u);
        uint32_t* out = words + count * kTextureLayerWords;
        out[0] = diffuse != 0u ? diffuse : morph;
        out[1] = 1u;
        out[2] = refii::vfs::LoadGuestU32(base, at);
        out[3] = 0u;
        out[4] = 0u;
        out[5] = 0u;
        out[6] = refii::vfs::LoadGuestU32(base, at + 12u);
        ++count;
      }
      Begin();
      refii::vfs::NoteTextureLayers(t_entity, original, 1u, words, count);
    }
  }
  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhRenderCommandStream_PostModelCommand);
}

REX_EXTERN(rex_lhSetTextureLayersCommand_Ctor) {
  refii::vfs::EnsureAlternateSignalStack();
  REX_FUNC_PROLOGUE();
  if (t_entity != 0u && ctx.r5.u32 != 0u && ctx.r6.u32 != 0u) {
    const uint32_t original = refii::vfs::LoadGuestU32(base, ctx.r5.u32);
    const uint32_t begin = refii::vfs::LoadGuestU32(base, ctx.r6.u32 + 4u);
    const uint32_t end = refii::vfs::LoadGuestU32(base, ctx.r6.u32 + 8u);
    if (begin != 0u && end > begin && ((end - begin) % kLayerBytes) == 0u &&
        (end - begin) <= kLayerBytes * 64u) {
      uint32_t words[kTextureLayersPerTexture * kTextureLayerWords];
      uint32_t count = 0u;
      for (uint32_t at = begin; at < end && count < kTextureLayersPerTexture;
           at += kLayerBytes) {
        uint32_t* out = words + count * kTextureLayerWords;
        out[0] = refii::vfs::LoadGuestU32(base, at);
        out[1] = refii::vfs::LoadGuestU32(base, at + 4u);
        out[2] = refii::vfs::LoadGuestU32(base, at + 8u);
        out[3] = refii::vfs::LoadGuestU32(base, at + 12u);
        out[4] = refii::vfs::LoadGuestU32(base, at + 16u);
        out[5] = refii::vfs::LoadGuestU32(base, at + 24u);
        out[6] = 0u;
        ++count;
      }
      Begin();
      refii::vfs::NoteTextureLayers(t_entity, original, 0u, words, count);
    }
  }
  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhSetTextureLayersCommand_Ctor);
}
