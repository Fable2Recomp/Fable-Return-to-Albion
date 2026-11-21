// The guest's element writers (attribute, flags, slide): each runs the original and tells what it
// wrote (gui/gui_told_statements.h). The text writers SetText (TU1 0x82425DD0 / disc 0x823F8268) and
// SetTextFromTag (0x82425F40 / 0x823F83D8) are stated at their entries
// (gui_headless_presentation.cpp RefiiHeadlessSetText / RefiiHeadlessSetTextFromTag): every
// presentation is headless (CGameface vtable TU1 0x82002F68 / disc 0x82002F88 +28, whose create
// `sub_82BBA588` / `sub_82BB3E88` is the only caller of the presentation allocator `sub_82BEDBC0` /
// `sub_82BE75D8`), and a headless element's text holder +4 is 0, which the writers test before any
// write (`lwz r11,4(r3)` / `beq` TU1 0x82425DFC, 0x82425F5C; disc 0x823F8294, 0x823F83F4).

#include <cstdint>

#include "vfs_hooks_private.h"
#include "gui/gui_told_statements.h"
#include "hooks/ppc_externs.h"

PPC_EXTERN_FUNC(__imp__rex_lhGuiAttributeService_SetElementAttribute);
PPC_EXTERN_FUNC(__imp__rex_lhGuiAttributeService_SetElementFlags);
PPC_EXTERN_FUNC(__imp__rex_lhGuiAnimation_GotoSlide);

/// The attribute writer — `(attrSvc, element, u32 id27, u32 valueBits)`; attribute-service
/// vtable `0x820065C0` slot +0x48, also fed by the keyframe interpolator `sub_822846B0`.
/// The value is raw bits; the type is in the element's record
/// (`(*(u32*)rec >> 2) & 7`: 1,2 int · 3 float · 4 bool · 5 string · 6 userdata).
REX_EXTERN(rex_lhGuiAttributeService_SetElementAttribute) {
    REX_FUNC_PROLOGUE();

    const uint32_t told_service = ctx.r3.u32;
    const uint32_t told_element = ctx.r4.u32;
    const uint32_t told_id = ctx.r5.u32;
    const uint32_t told_value = ctx.r6.u32;
    __imp__rex_lhGuiAttributeService_SetElementAttribute(ctx, base);
    refii::vfs::TellAttribute(base, told_service, told_element, told_id, told_value);
}

/// The flag writer — `(attrSvc, element, u16 mask, bool set)`; attribute-service vtable
/// `0x820065C0` slot +0x50. `0x1000` is the draw gate `globalactive`, `0x2000` the authored
/// `active`; neither is an attribute. Only the `0x8000` dirty bit and constructor zero-init
/// write `element+0x1C` without passing here.
REX_EXTERN(rex_lhGuiAttributeService_SetElementFlags) {
    REX_FUNC_PROLOGUE();

    const uint32_t told_service = ctx.r3.u32;
    const uint32_t told_element = ctx.r4.u32;
    const uint32_t told_mask = ctx.r5.u32 & 0xFFFFu;
    const bool told_on = (ctx.r6.u32 & 0xFFu) != 0u;
    __imp__rex_lhGuiAttributeService_SetElementFlags(ctx, base);
    refii::vfs::TellFlags(base, told_service, told_element, told_mask, told_on);
}

/// The slide writer — `(animSvc, element, int slideIndex, bool notify)`; animation-service
/// slot +0x08. Slides live on the animation record (`+43` current, `+44` previous), not as
/// an attribute.
REX_EXTERN(rex_lhGuiAnimation_GotoSlide) {
    REX_FUNC_PROLOGUE();

    // Every explicit entry is the guest's statement (gui_slide_hooks.cpp); the walk's own entry
    // of a component coming live passes `notify` 0 (`sub_822849A0` 0x82284b38).
    const uint32_t told_service = ctx.r3.u32;
    const uint32_t told_element = ctx.r4.u32;
    const uint32_t told_slide = ctx.r5.u32;
    const bool told_stated = (ctx.r6.u32 & 0xFFu) != 0u;
    refii::vfs::TellGotoSlideBegin();
    __imp__rex_lhGuiAnimation_GotoSlide(ctx, base);
    refii::vfs::TellGotoSlideEnd(base, told_service, told_element, told_slide, told_stated);
}
