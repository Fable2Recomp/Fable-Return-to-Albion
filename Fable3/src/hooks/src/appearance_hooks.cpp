// refii - Fable 3's character appearance, stated to the host where the guest hands it to its
// renderer: each part's morph-target list and each texture command of a character's rebuild.
//
// Every address is the Fable 3 IDB; each offset names its base.
//
// THE DRAW VIRTUAL. CECGraphicAppearanceMorph (component 20) draws through sub_8222C190 (this =
// comp20+12, the secondary vtable; the owning entity is *(this-8) = comp20+4, the pointer
// `*(a12 - 2)` whose component mask at entity+0x28 and {id, ptr} vector at entity+0x54/+0x58 the
// virtual itself searches). It rebuilds the part list with sub_82774448(comp20) (0x8222C3E0) and
// then walks the 128-byte built records at comp20+0x38..+0x3C (`(v18[12] - v18[11]) >> 7`).
//
// PART MORPHS. For each record the virtual lower_bounds the sorted 12-byte MorphTargets vector at
// comp20+0x70..+0x74 ({OriginalModel +0, MorphType +4, TargetModel +8}, filled from the def's
// "MorphTargets" rows by sub_82771DB8 at 0x82772664..0x8277266C) on the record's model hash
// (0x8222C608..0x8222C668). With a match it asks CECMorph (component 140) for each row's weight,
// `bl sub_822A0A38(morph, MorphType)` at 0x8222C7F4 (`lwz r4,4(r29)`), and pushes {TargetModel,
// weight} (`stw r31` / `stfs f1` at 0x8222C808/0x8222C804, `lwz r31,8(r29)` the target) for
// every weight above flt_82062360 (0x8222C800), then inserts {base model, 1 - sum} at index 0
// (`fsubs f0,f0,f31` 0x8222C838, sub_8277A360 at 0x8222C854). The list is a vector object
// {alloc, begin +4, end +8, cap +12} of 8-byte {model hash +0, weight float +4} (sub_8277A2D0
// writes `*(v4) = hash; *(v4+4) = weight`). It is handed whole to the morph descriptor's
// constructor
//
//   sub_82C4BB20(desc r3, flags r4, list r5, ...)      0x8222CB50 morphed, 0x8222CD50 a part
//                                                      whose rows all weigh nothing ({base, 1})
//
// which copies it to desc+112 (sub_82C4A300). A part with no MorphTargets row never reaches it
// (sub_82C4B900 at 0x8222CEA4). THE CONSTRUCTOR IS THE STATEMENT: it is a real call on both paths.
// Weights: sub_822A0A38 reads CECStats (component 15) through sub_82228128 — MorphType 1/2
// morality above/below the midpoint, 3/4 extreme good/evil, 5 purity, 6 strength (capped by
// flt_8329EC90 when 7 skill exceeds flt_8329EC8C), 7 skill, 8 will, 9 fat (capped by
// flt_8329EC88 when 6 exceeds flt_8329EC84), 10/11 renown above/below the midpoint, 12/13 age
// 18..30 / 30..60 (CECAge, component 145), 14 one; dog stats (component 242) answer 1 and 2 only.
// The host takes the stated weights and never recomputes them.
//
// TEXTURES. Fable 3 keeps no layer blend: a character's texture work is a list of 40-byte entries
// {original texture +0, body +4: texture +0, cluster name +4, weight +8, colour bytes +12..+15,
// colour bytes +16..+19, float +20, float +24, mode +28, flags +32/+33} (ctor sub_82770970, copy
// sub_8277B550) — the def's TextureMorphs (comp20+0x80..+0x84, loaded at 0x827723C8..0x82772448),
// the skin set, the stat textures (sub_82778120: GeneratedTextureMorphs, a filename template with
// "[M]"/"[P]"/"[S]" replaced by the stat's 0..6 step, 0x82778348 `* 6.0`), the worn items'
// TattooTextureMorph (mode 5) and hair (mode 3) textures (sub_82774C98 over component 18's slots)
// and the dyes (mode 10). Sorted by original (sub_8277EC88 compares the first word, 0x8278031C),
// each entry becomes ONE COMMAND to a render instance, `sub_821F5110(handle, cmd, 1)`:
//
//   type 33 (vtable off_820A55E8, serialised by sub_82C4B140(ring r3, cmd r4) to 28 bytes):
//     cmd+24 opcode 11 = REPLACE: cmd+8 the original texture, cmd+12 the replacement
//     (0x8222DF8C/0x8222DF94 and every mode's branch, 0x8222E19C/0x8222E1A4 for the default
//     mode, which sends it to every built part's handle at record+32); opcode 0 = TINT (sub_82686850:
//     cmd+4 the original, cmd+8..+20 four floats from comp20+0x8C..+0x8F x flt_832A8930), sent only
//     to component 6's instance at 0x8222E5B0.
//   type 30 (vtable off_820E3ACC, sub_82C4C0E8(ring r3, cmd r4)): the dye's COLOUR MASK —
//     sub_827771B0 writes cmd+4 the item def's OriginalTexture, cmd+8 its "ColourMask" texture,
//     cmd+12..+15 DyeColour1, cmd+16 DyeBrightness1, cmd+20..+23 DyeColour2, cmd+24 DyeBrightness2
//     (the dye component's +8..+23, sub_82777008 0x8277716C..0x82777184; the fields are named by the
//     dye record's serialiser sub_82ACBEA0 — DyeColour1 +4, DyeColour2 +8, DyeBrightness1 +12,
//     DyeBrightness2 +16 — and written by sub_82632420: colour at +8/+16, the float at +12/+20), to
//     the worn part's handle (0x8222DDA0) or, for a mode-10 entry, component 6's (0x8222E158).
//
// Both serialisers are reached through their vtable's slot 2 (the stubs at 0x82C4B130 and
// 0x82C4C030 swap r3/r4 and branch), synchronously on the drawing thread, so the entity whose
// virtual is running is this thread's. Each statement is one of Fable 2's layer rows: a
// replacement is a one-layer composite (kind 0) at weight 1; a colour mask is kind 3 with two
// layers, the mask at weight 1 with each dye colour as the tint word (the four bytes in memory
// order) and its DyeBrightness in the brightness word. The tint command's lane order is not
// traced, so it is not stated.
//
// WHOLE, ON RETURN. Statements are staged from the rebuild (sub_82774448) and published when the
// virtual returns, so the host never composes from half of a rebuild.

#include <cstdint>
#include <cstring>

#include <rex/ppc.h>

#include "core/payload_bridge.h"

REX_EXTERN(__imp__rex_GraphicAppearanceMorph_Draw);
REX_EXTERN(__imp__rex_GraphicAppearanceMorph_RebuildParts);
REX_EXTERN(__imp__rex_MorphDescriptor_Ctor);
REX_EXTERN(__imp__rex_TextureCommand_Serialise);
REX_EXTERN(__imp__rex_ColourMaskCommand_Serialise);

namespace {

constexpr uint32_t kReplaceOpcode = 11u;
constexpr uint32_t kOne = 0x3F800000u;

uint32_t LoadGuestU32(const uint8_t* base, uint32_t address) {
  uint32_t value = 0;
  std::memcpy(&value, base + address, sizeof(value));
  return __builtin_bswap32(value);
}

bool PlausibleGuest(uint32_t address) { return address >= 0x10000u && address < 0xC0000000u; }

/// The entity whose draw virtual runs on this thread, and whether its rebuild has begun a stage.
thread_local uint32_t t_entity = 0u;
thread_local bool t_begun = false;

void Begin() {
  if (t_entity != 0u && !t_begun) {
    t_begun = true;
    refii_bridge_texture_layers_begin(t_entity);
  }
}

}  // namespace

REX_EXTERN(rex_GraphicAppearanceMorph_Draw) {
  const uint32_t outer = t_entity;
  const bool outer_begun = t_begun;
  const uint32_t self = ctx.r3.u32;
  t_entity = self >= 12u && PlausibleGuest(self - 8u) ? LoadGuestU32(base, self - 8u) : 0u;
  t_begun = false;
  __imp__rex_GraphicAppearanceMorph_Draw(ctx, base);
  if (t_begun) {
    refii_bridge_texture_layers_end(t_entity);
  }
  t_entity = outer;
  t_begun = outer_begun;
}

REX_EXTERN(rex_GraphicAppearanceMorph_RebuildParts) {
  Begin();
  __imp__rex_GraphicAppearanceMorph_RebuildParts(ctx, base);
}

REX_EXTERN(rex_MorphDescriptor_Ctor) {
  const uint32_t list = ctx.r5.u32;
  if (t_entity != 0u && PlausibleGuest(list)) {
    const uint32_t begin = LoadGuestU32(base, list + 4u);
    const uint32_t end = LoadGuestU32(base, list + 8u);
    if (PlausibleGuest(begin) && end > begin && ((end - begin) & 7u) == 0u && (end - begin) <= 8u * 64u) {
      uint32_t pairs[REFII_PART_MORPH_TARGETS * 2u];
      uint32_t count = 0u;
      for (uint32_t at = begin + 8u; at + 8u <= end && count < REFII_PART_MORPH_TARGETS; at += 8u) {
        pairs[count * 2u] = LoadGuestU32(base, at);
        pairs[count * 2u + 1u] = LoadGuestU32(base, at + 4u);
        ++count;
      }
      refii_bridge_part_morph_stated(t_entity, LoadGuestU32(base, begin), pairs, count);
    }
  }
  __imp__rex_MorphDescriptor_Ctor(ctx, base);
}

REX_EXTERN(rex_TextureCommand_Serialise) {
  const uint32_t command = ctx.r4.u32;
  if (t_entity != 0u && PlausibleGuest(command) && LoadGuestU32(base, command + 24u) == kReplaceOpcode) {
    const uint32_t original = LoadGuestU32(base, command + 8u);
    const uint32_t replacement = LoadGuestU32(base, command + 12u);
    if (original != 0u && replacement != 0u) {
      const uint32_t layer[REFII_TEXTURE_LAYER_WORDS] = {replacement, 0u, kOne, 0u, 0u, 0u, 0u};
      Begin();
      refii_bridge_texture_layers_stated(t_entity, original, 0u, layer, 1u);
    }
  }
  __imp__rex_TextureCommand_Serialise(ctx, base);
}

REX_EXTERN(rex_ColourMaskCommand_Serialise) {
  const uint32_t command = ctx.r4.u32;
  if (t_entity != 0u && PlausibleGuest(command)) {
    const uint32_t original = LoadGuestU32(base, command + 4u);
    const uint32_t mask = LoadGuestU32(base, command + 8u);
    if (original != 0u && mask != 0u) {
      const uint32_t layers[2u * REFII_TEXTURE_LAYER_WORDS] = {
          mask, 0u, kOne, 1u, LoadGuestU32(base, command + 12u), LoadGuestU32(base, command + 16u), 0u,
          mask, 0u, kOne, 1u, LoadGuestU32(base, command + 20u), LoadGuestU32(base, command + 24u), 0u,
      };
      Begin();
      refii_bridge_texture_layers_stated(t_entity, original, REFII_TEXTURE_LAYER_KIND_COLOUR_MASK, layers, 2u);
    }
  }
  __imp__rex_ColourMaskCommand_Serialise(ctx, base);
}
