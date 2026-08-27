// Element state captured at its three guest writers (attribute, flags, slide) plus the text
// writer — see gui/gui_element_state.h.

#include <cstdint>

#include "vfs_hooks_private.h"
#include "core/guest_state.h"
#include "gui/gui_element_state.h"
#include "hooks/ppc_externs.h"

PPC_EXTERN_FUNC(__imp__sub_82284798);
PPC_EXTERN_FUNC(__imp__sub_82284BE8);
PPC_EXTERN_FUNC(__imp__sub_82BB5760);
PPC_EXTERN_FUNC(__imp__sub_82425DD0);

namespace {

/// The attribute service lives at `presentation + 0x1EC` (`82BAFBF0 stw r4, 0x1EC(r31)`);
/// changes are keyed on the presentation, not the service, because presentation addresses recycle.
constexpr uint32_t kAttributeServiceOffset = 0x1ECu;

/// The animation service lives at `presentation + 0x238` (`82BAFBF8 stw r3, 0x238(r31)`).
constexpr uint32_t kAnimationServiceOffset = 0x238u;

bool Plausible(uint32_t value) { return value >= 0x10000u && value < 0xC0000000u; }

/// `elem+0` is the element's name hash (`sub_82BB5658` compares it directly).
uint32_t NameHashOf(uint8_t* base, uint32_t element) {
    return Plausible(element) ? refii::vfs::LoadGuestU32(base, element) : 0u;
}

}  // namespace

/// The attribute writer — `(attrSvc, element, u32 id27, u32 valueBits)`; attribute-service
/// vtable `0x820065C0` slot +0x48, also fed by the keyframe interpolator `sub_822846B0`.
/// The value is raw bits; the type is in the element's record
/// (`(*(u32*)rec >> 2) & 7`: 1,2 int · 3 float · 4 bool · 5 string · 6 userdata).
REX_EXTERN(sub_82284798) {
    REX_FUNC_PROLOGUE();

    const uint32_t service = ctx.r3.u32;
    const uint32_t element = ctx.r4.u32;
    if (Plausible(service) && Plausible(element)) {
        refii::vfs::GuiElementChange change;
        change.kind = refii::vfs::GuiElementChange::Kind::kAttribute;
        change.presentation = service - kAttributeServiceOffset;
        change.element = element;
        change.name_hash = NameHashOf(base, element);
        change.id = ctx.r5.u32;
        change.value = ctx.r6.u32;
        refii::vfs::NoteGuiElementChange(change);
    }

    __imp__sub_82284798(ctx, base);
}

/// The flag writer — `(attrSvc, element, u16 mask, bool set)`; attribute-service vtable
/// `0x820065C0` slot +0x50. `0x1000` is the draw gate `globalactive`, `0x2000` the authored
/// `active`; neither is an attribute. Only the `0x8000` dirty bit and constructor zero-init
/// write `element+0x1C` without passing here. `sub_822849A0` calls this for every element every
/// frame with no dirty check, so the guest's own no-op filter (0x82284c48) is applied here too.
/// Read the halfword BEFORE the call — the original mutates `element+0x1C`.
REX_EXTERN(sub_82284BE8) {
    REX_FUNC_PROLOGUE();

    const uint32_t service = ctx.r3.u32;
    const uint32_t element = ctx.r4.u32;
    if (Plausible(service) && Plausible(element)) {
        const uint32_t mask = ctx.r5.u32 & 0xFFFFu;
        const bool set = ctx.r6.u32 != 0u;

        // `element+0x1C` is a big-endian halfword — the same one the walk reads.
        const uint32_t before = refii::vfs::LoadGuestU16(base, element + 0x1Cu);
        const uint32_t after = set ? (before | mask) : (before & ~mask);
        if (after != before) {
            refii::vfs::GuiElementChange change;
            change.kind = refii::vfs::GuiElementChange::Kind::kFlags;
            change.presentation = service - kAttributeServiceOffset;
            change.element = element;
            change.name_hash = NameHashOf(base, element);
            change.id = mask;
            change.value = set ? 1u : 0u;
            refii::vfs::NoteGuiElementChange(change);
        }
    }

    __imp__sub_82284BE8(ctx, base);
}

/// The slide writer — `(animSvc, element, int slideIndex, bool notify)`; animation-service
/// slot +0x08. Slides live on the animation record (`+43` current, `+44` previous), not as
/// an attribute.
REX_EXTERN(sub_82BB5760) {
    REX_FUNC_PROLOGUE();

    const uint32_t service = ctx.r3.u32;
    const uint32_t element = ctx.r4.u32;
    if (Plausible(service) && Plausible(element)) {
        refii::vfs::GuiElementChange change;
        change.kind = refii::vfs::GuiElementChange::Kind::kSlide;
        change.presentation = service - kAnimationServiceOffset;
        change.element = element;
        change.name_hash = NameHashOf(base, element);
        change.value = ctx.r5.u32;
        refii::vfs::NoteGuiElementChange(change);
    }

    __imp__sub_82BB5760(ctx, base);
}

/// The text writer — `(element, ...)`. Text lives on the text record: `element+4` holder,
/// `*holder` record, `record+140` live string; this marks it dirty (`record+144 = 1`, then
/// `record+138`/`+136` once layout takes). The string is mutated in place and no presentation
/// is recoverable from an element, so only the element address is recorded.
REX_EXTERN(sub_82425DD0) {
    REX_FUNC_PROLOGUE();

    const uint32_t element = ctx.r3.u32;
    if (Plausible(element)) {
        refii::vfs::GuiElementChange change;
        change.kind = refii::vfs::GuiElementChange::Kind::kText;
        change.presentation = 0u;
        change.element = element;
        change.name_hash = NameHashOf(base, element);
        refii::vfs::NoteGuiElementChange(change);
    }

    __imp__sub_82425DD0(ctx, base);
}
