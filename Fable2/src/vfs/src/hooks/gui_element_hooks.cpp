// The guest's element writers (attribute, flags, slide, text): each runs the original and tells
// what it wrote (gui/gui_told_statements.h).

#include <cstdint>

#include "vfs_hooks_private.h"
#include "core/guest_state.h"
#include "gui/gui_told_statements.h"
#include "hooks/ppc_externs.h"

PPC_EXTERN_FUNC(__imp__rex_lhGuiAttributeService_SetElementAttribute);
PPC_EXTERN_FUNC(__imp__rex_lhGuiAttributeService_SetElementFlags);
PPC_EXTERN_FUNC(__imp__rex_lhGuiAnimation_GotoSlide);
PPC_EXTERN_FUNC(__imp__rex_lhGuiElement_SetText);
PPC_EXTERN_FUNC(__imp__rex_lhGuiElement_SetTextFromTag);

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

/// The text writer — `(element, ...)`. Text lives on the text record: `element+4` holder,
/// `*holder` record, `record+140` live string; this marks it dirty (`record+144 = 1`, then
/// `record+138`/`+136` once layout takes). The string is mutated in place and no presentation
/// is recoverable from an element, so only the element address is told.
REX_EXTERN(rex_lhGuiElement_SetText) {
    REX_FUNC_PROLOGUE();

    const uint32_t element = ctx.r3.u32;
    __imp__rex_lhGuiElement_SetText(ctx, base);
    refii::vfs::TellText(base, element);
}

/// The OTHER text writer — same record, but it takes a TAG and resolves it first.
///
/// `sub_82425F40(element, tag)` walks the identical chain (`element+4` holder, `*holder`
/// record, `record+144 = 1`, store into `record+140`, then `record+138`/`+136`), and between
/// the copy and the store it calls `sub_8230E0E8` @0x8230E0E8 — the Babel lookup — so what
/// lands at `+140` is already TRANSLATED. The host needs no lookup of its own for these.
///
/// THE EXPRESSION WHEEL WRITES EVERY ONE OF ITS EIGHT TITLES THROUGH THIS ONE.
/// `sub_826AC5F0` resolves `item<N>/Title` and calls it at 0x826ac704; `sub_826AB1F8` runs
/// that for all eight sectors in the single frame the ring opens. With only the sibling above
/// hooked, no text change was ever noted for the wheel, the cached element list kept its seed,
/// and all eight sectors painted `hud.bgf`'s authored placeholder `QUICK_FLIRTY` — the same
/// string sixteen times in the art, which is why every sector read alike rather than each
/// reading its own name.
REX_EXTERN(rex_lhGuiElement_SetTextFromTag) {
    REX_FUNC_PROLOGUE();

    const uint32_t element = ctx.r3.u32;
    __imp__rex_lhGuiElement_SetTextFromTag(ctx, base);
    refii::vfs::TellText(base, element);
}
