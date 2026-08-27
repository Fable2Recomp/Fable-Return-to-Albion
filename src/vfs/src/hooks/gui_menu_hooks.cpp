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

// The attribute stagers. `r3` is dead on both: each resolves the "Parameters" element from
// `dword_8349AF78` and overwrites r3 before use (`lwz r3, 0x38(r11)` at 0x826B9534 / 0x826B9468).
PPC_EXTERN_FUNC(__imp__sub_826B9510);
PPC_EXTERN_FUNC(__imp__sub_826B9440);

/// String stager — `r4` attribute name, `r5` value, `r6` presentation (0 = current).
REX_EXTERN(sub_826B9510) {
    REX_FUNC_PROLOGUE();

    refii::vfs::StageMenuAttribute(
        refii::vfs::ReadGuestLhString(base, ctx.r4.u32),
        refii::vfs::ReadGuestLhString(base, ctx.r5.u32),
        0.0f,
        /*is_number=*/false);

    __imp__sub_826B9510(ctx, base);
}

/// Float stager — `r4` attribute name, `f1` value (a double in the register, stored narrowed),
/// `r6` presentation.
REX_EXTERN(sub_826B9440) {
    REX_FUNC_PROLOGUE();

    refii::vfs::StageMenuAttribute(
        refii::vfs::ReadGuestLhString(base, ctx.r4.u32),
        std::string(),
        static_cast<float>(ctx.f1.f64),
        /*is_number=*/true);

    __imp__sub_826B9440(ctx, base);
}

// The chunk runner `sub_82426470` is hooked in resource_manager_open.cpp, before that hook's
// native-passthrough guard.
