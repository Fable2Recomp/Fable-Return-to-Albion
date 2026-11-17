#include "vfs_hooks_private.h"

#include <cstdint>

#include "hooks/ppc_externs.h"

// Each entity's look, taken where the guest states it.
//
// CECGraphicAppearance (component 3, constructor `sub_82571DF8`) is the renderable. Every one
// of its appearance setters stores its argument and then calls `sub_82573318(comp3)`:
//
//   SetAlpha        sub_82573220  +112  store 0x825732cc, call 0x825732f8
//   SetCameraAlpha  sub_82573128  +116  store 0x825731d4, call 0x82573200
//   SetScale        sub_82573078  +108  store 0x825730c0, call 0x82573110
//   SetAsDrawable   sub_825725A8  +197  store 0x825725cc, call 0x8257266c
//   tint (bytes)    sub_82573570  +80   call 0x82573754
//   tint (vector)   sub_82573770  +80   call 0x82573818
//   SaturationMask  sub_822B8188  +196  call 0x822b820c
//
// and `sub_82573318` packs exactly those fields into the appearance record it replicates. It is
// the one place every change passes, so it is hooked once rather than each setter seven times.
// This is how a creature's death reaches Godot: DeathType 5/6/7 ramp SetAlpha(1 - t/T) through
// `sub_82681868`, and every death tints the body {103,103,103,255} through `sub_82573570`
// (`sub_823A4E98`, 0x823a69c4).
//
// A LOAD writes Alpha, TintColour, SaturationMask and Scale straight into the component
// (`sub_82572238`, no setter), so that is hooked too. The constructor and destructor drop what
// was stated for the address: component memory is reused.
//
// THE DEFINITION states +192, DisableAlphaDepthWrites (vf8 `sub_825720A8`, 0x825721b8
// `stb r3, 0xC0(r31)`; disc `sub_82546D50`, 0x82546e60), the class's only writer of it. Each
// replication the command builder (`sub_82572C18`, disc `sub_825478C0`) posts model command 16
// whenever the opacity product is below 1.0 OR +192 is set, its alpha reference 255 when +192 is
// set or the product is under 1e-4, else -1 (0x82572c58..0x82572c94: `lbz r10, 0xC0(r31)` /
// `li r11, -1` / `li r11, 0xFF`). The twin keeps a reference >= 0 at its +228 (0x822d9e7c), 254
// from its constructor (`sub_82AA2658`), and binds it as ALPHAREF under ALPHAFUNC GREATER
// (`dword_83316F5C` = 4) on its alpha-tested draws: the colour draw of a surface that is not a
// cutout only (`sub_82220CA8` 0x82220d50 -> `sub_822251D8` 0x82225ac4) and the depth-only draw
// (`sub_822015B8` 0x822016fc). GREATER 255 passes nothing, so those draws write no colour and no
// depth and a translucent surface is left with its blended draw alone. Shipped, only the base
// definition (#330c5a47) names the field, and false.

namespace {

void StateAppearance(uint8_t* base, uint32_t component) {
  if (component == 0u) {
    return;
  }

  uint32_t words[kAppearanceWords];
  words[0] = refii::vfs::LoadGuestU32(base, component + 112u);
  words[1] = refii::vfs::LoadGuestU32(base, component + 116u);
  words[2] = refii::vfs::LoadGuestU32(base, component + 108u);
  words[3] = refii::vfs::LoadGuestU32(base, component + 80u);
  words[4] = refii::vfs::LoadGuestU32(base, component + 84u);
  words[5] = refii::vfs::LoadGuestU32(base, component + 88u);
  words[6] = refii::vfs::LoadGuestU32(base, component + 92u);
  words[kAppearanceFlags] =
      (refii::vfs::LoadGuestU8(base, component + 197u) != 0u ? kAppearanceDrawable : 0u) |
      (refii::vfs::LoadGuestU8(base, component + 196u) != 0u ? kAppearanceSaturation : 0u) |
      (refii::vfs::LoadGuestU8(base, component + 192u) != 0u ? kAppearanceNoAlphaDepth : 0u);
  refii::vfs::NoteAppearance(component, words);
}

}  // namespace

REX_EXTERN(rex_CECGraphicAppearance_ReplicateAppearance) {
  refii::vfs::EnsureAlternateSignalStack();
  REX_FUNC_PROLOGUE();
  StateAppearance(base, ctx.r3.u32);
  REFII_VFS_NATIVE_PASSTHROUGH(rex_CECGraphicAppearance_ReplicateAppearance);
}

// sub_82572238(CECGraphicAppearance* r3, archive r4, version r5): both directions; a save
// restates what the component already holds, which records nothing new.
REX_EXTERN(rex_CECGraphicAppearance_Serialize) {
  refii::vfs::EnsureAlternateSignalStack();
  REX_FUNC_PROLOGUE();
  const uint32_t component = ctx.r3.u32;
  __imp__rex_CECGraphicAppearance_Serialize(ctx, base);
  StateAppearance(base, component);
}

// vf8 `sub_825720A8(CECGraphicAppearance* r3, definition r4)`: the definition's fields, +192
// among them, written after the constructor.
REX_EXTERN(rex_CECGraphicAppearance_ApplyDefinition) {
  refii::vfs::EnsureAlternateSignalStack();
  REX_FUNC_PROLOGUE();
  const uint32_t component = ctx.r3.u32;
  __imp__rex_CECGraphicAppearance_ApplyDefinition(ctx, base);
  StateAppearance(base, component);
}

REX_EXTERN(rex_CECGraphicAppearance_Ctor) {
  refii::vfs::EnsureAlternateSignalStack();
  REX_FUNC_PROLOGUE();
  refii::vfs::ForgetAppearance(ctx.r3.u32);
  REFII_VFS_NATIVE_PASSTHROUGH(rex_CECGraphicAppearance_Ctor);
}

REX_EXTERN(rex_CECGraphicAppearance_ScalarDtor) {
  refii::vfs::EnsureAlternateSignalStack();
  REX_FUNC_PROLOGUE();
  refii::vfs::ForgetAppearance(ctx.r3.u32);
  REFII_VFS_NATIVE_PASSTHROUGH(rex_CECGraphicAppearance_ScalarDtor);
}
