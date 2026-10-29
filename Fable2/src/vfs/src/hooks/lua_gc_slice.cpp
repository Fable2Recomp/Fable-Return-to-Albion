// The guest's Lua GC slice, given the real clock it was written against.
//
// `sub_821956A0(L, force)` is the per-game-tick GC driver (the last call in the tick update
// `sub_821C8FB0`). Its shipped mode (`byte_83496BC5 == 0`) runs `luaC_step` back-to-back until
// `mftb` passes `LHTiming_GetTime() + dbl_833193C8` (0.002 s, "SetMillisecondsForGarbageCollector
// ToTake"), restarting a mark cycle whenever one finishes, then parks the collector
// (`GCthreshold = MAX_LUMEM`, 0x82195838) until the next tick:
//
//     821957c8  lfd   f0, dbl_833193C8        ; 0.002
//     821957cc  fadd  f31, f0, f1             ; deadline = LHTiming_GetTime() + 0.002
//     821957e8  bl    luaC_step
//     8219580c  bl    sub_8221EA28            ; mftb
//     82195828  fcmpu cr6, f9, f31
//     8219582c  blt   cr6, loc_821957D0       ; now < deadline -> again
//
// Under the host-driven time base `mftb` advances only when the host grants a step, so the
// deadline was unreachable mid-tick and the loop marked and swept the whole heap until the
// next frame's grant. The slice here runs on the real clock, for this thread, for the length of the call; the
// guest's full-collection paths (`force`, the 11.7 MiB pool threshold every 30 s) and the
// work-bounded mode are untouched — they do not read the clock.
//
// Keeps the LuaInit bring-up trace this symbol carried in game_init_trace.cpp.
#include <cstdint>

#include <rex/logging.h>

#include "vfs_hooks_private.h"
#include "core/rex_payload_bridge.h"
#include "hooks/ppc_externs.h"
#include "core/guest_build.h"

PPC_EXTERN_FUNC(__imp__rex_lhLua_GcSliceStep);

REX_EXTERN(rex_lhLua_GcSliceStep) {
    REX_FUNC_PROLOGUE();
    const uint32_t lr = ctx.lr;
    const bool inside = lr >= refii::guest::Address(0x82459280u) && lr <= refii::guest::Address(0x82459900u);
    if (inside) {
        REXLOG_INFO("Refii LuaInit ENTER sub_821956A0 (lr=0x{:08X})", lr);
    }

    refii_bridge_rex_time_base_real_slice(1);
    __imp__rex_lhLua_GcSliceStep(ctx, base);
    refii_bridge_rex_time_base_real_slice(0);

    if (inside) {
        REXLOG_INFO("Refii LuaInit EXIT  sub_821956A0 (lr=0x{:08X})", lr);
    }
}
