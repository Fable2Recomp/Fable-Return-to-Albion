// C++ stages attributes onto the "Parameters" presentation element (Lua's `g_Param.element`),
// then runs a one-line `g_Menu:` chunk that reads them back (28 `g_Menu:` + 3 `g_MenuFormatter:`
// literals, all via `sub_82426470`). Presentation-element state (`sub_82426798`, `sub_82426260`,
// `sub_82425DD0`, ...) does not pass through here.

#include <cstdint>
#include <string>

#include "vfs_hooks_private.h"
#include "core/guest_state.h"
#include "gui/gui_menu_chunks.h"
#include "hooks/ppc_externs.h"
#include "core/guest_build.h"
#include "core/payload_bridge_fable2.h"

// The attribute stagers. `r3` is dead on both: each resolves the "Parameters" element from
// `dword_8349AF78` and overwrites r3 before use (`lwz r3, 0x38(r11)` at 0x826B9534 / 0x826B9468).
PPC_EXTERN_FUNC(__imp__rex_lhGuiPresentation_SetStringParam);
PPC_EXTERN_FUNC(__imp__rex_lhGuiPresentation_SetFloatParam);

/// String stager — `r4` attribute name, `r5` value, `r6` presentation (0 = current).
REX_EXTERN(rex_lhGuiPresentation_SetStringParam) {
    REX_FUNC_PROLOGUE();

    refii::vfs::StageMenuAttribute(
        refii::vfs::ReadGuestLhString(base, ctx.r4.u32),
        refii::vfs::ReadGuestLhString(base, ctx.r5.u32),
        0.0f,
        /*is_number=*/false);

    __imp__rex_lhGuiPresentation_SetStringParam(ctx, base);
}

/// Float stager — `r4` attribute name, `f1` value (a double in the register, stored narrowed),
/// `r6` presentation.
REX_EXTERN(rex_lhGuiPresentation_SetFloatParam) {
    REX_FUNC_PROLOGUE();

    refii::vfs::StageMenuAttribute(
        refii::vfs::ReadGuestLhString(base, ctx.r4.u32),
        std::string(),
        static_cast<float>(ctx.f1.f64),
        /*is_number=*/true);

    __imp__rex_lhGuiPresentation_SetFloatParam(ctx, base);
}

// The chunk runner `sub_82426470` is hooked in resource_manager_open.cpp, before that hook's
// native-passthrough guard.

/// THE HIGHLIGHTED ROW, ANSWERED FROM GODOT'S LIST. `sub_826C1B50(menuHelper, out)` (TU1, disc
/// `sub_8259DB00`; 48 callers across every list screen) runs `g_Menu:GetHighlightedMenuItemData()`
/// in the screen's Gameface Lua and copies back `out[0] = (int)Float3` (the row's event type),
/// `out[1] = ((int)Float2 << 16) + (int)Float1` (its id), `out[2]` String1 and `out[3]` String2
/// (0x826c1c90..0x826c1d6c). While Godot states the row for the CURRENT screen
/// (`RefiiHostSetHighlightedRow`), those four are written from the statement and the Lua is not
/// asked — the guest's handlers act on the row the player is on, whatever the hidden cursor
/// holds. Any other screen, or no statement, runs the original.
REX_EXTERN(rex_lhGuiMenu_GetHighlightedItemData) {
    const uint32_t out = ctx.r4.u32;
    const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };

    uint32_t stated_screen = 0;
    int32_t event_type = 0;
    int32_t id = 0;
    char name[128] = {};
    char label[128] = {};
    const bool stated = refii_bridge_get_stated_highlight(&stated_screen, &event_type, &id, name, label,
                                                          sizeof(name));

    uint32_t current = 0;
    const uint32_t gui = refii::vfs::LoadGuestU32(base, refii::guest::Address(0x83496B08u));
    const uint32_t manager = plausible(gui) ? refii::vfs::LoadGuestU32(base, gui + 56u) : 0u;
    if (plausible(manager)) {
        current = refii::guest::ScreenIdToTu1(refii::vfs::LoadGuestU32(base, manager + 4u));
    }

    if (!stated || !plausible(out) || current == 0u || current != stated_screen) {
        __imp__rex_lhGuiMenu_GetHighlightedItemData(ctx, base);
        return;
    }

    refii::vfs::StoreGuestU32(base, out + 0u, static_cast<uint32_t>(event_type));
    refii::vfs::StoreGuestU32(base, out + 4u, static_cast<uint32_t>(id));

    // String1 / String2: a narrow CCharString built on a scratch frame (`sub_8222CED0`), assigned
    // into the out slot as the original does (`sub_822651A0`), then released (`sub_82214DD8`).
    const PPCContext saved = ctx;
    const uint32_t frame = (saved.r1.u32 - 0x300u) & ~0xFu;
    const uint32_t text = frame + 0x100u;
    const uint32_t handle = frame + 0x200u;
    const auto call = [&](uint32_t tu1, uint32_t r3, uint32_t r4, uint32_t r5) {
        const uint32_t address = refii::guest::Address(tu1);
        PPCFunc* fn = refii::vfs::FindGuestFunction(address);
        if (fn == nullptr) return;
        ctx = saved;
        ctx.r1.u32 = frame;
        ctx.r3.u32 = r3;
        ctx.r4.u32 = r4;
        ctx.r5.u32 = r5;
        ctx.ctr.u32 = address;
        fn(ctx, base);
        ctx = saved;
    };
    const auto assign = [&](uint32_t slot, const char* value) {
        uint32_t i = 0;
        for (; value[i] != '\0' && i < 127u; ++i) {
            base[text + i] = static_cast<uint8_t>(value[i]);
        }
        base[text + i] = 0;
        refii::vfs::StoreGuestU32(base, handle, 0u);
        call(0x8222CED0u, handle, text, 0xFFFFFFFFu);
        call(0x822651A0u, slot, handle, 0u);
        call(0x82214DD8u, handle, 0u, 0u);
    };
    assign(out + 8u, name);
    assign(out + 12u, label);
    ctx = saved;
}
