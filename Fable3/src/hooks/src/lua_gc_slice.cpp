// refii - Fable 3's Lua GC slice, given the real clock it was written against.
//
// `sub_8226DDB8(L_holder, force)` is the per-game-tick GC driver (Fable 3 IDB; the same shape as
// Fable 2's sub_821956A0, Fable2/src/vfs/src/hooks/lua_gc_slice.cpp). Its shipped mode (`force`
// clear, `byte_8355646F` clear: not the "GarbageCollectorStepSize" mode) runs `luaC_step`
// (`sub_8228AE90(g, L)` at 0x8226DEDC, gcstepmul forced to 250 around it) back-to-back until the
// time base passes `LHTiming_GetTime() + dbl_8329ED28`, then parks the collector:
//
//     8226de9c  deadline = dbl_8329ED28 + sub_8220A380()        ; seconds to take + now
//     8226dedc  bl    sub_8228AE90                              ; luaC_step
//     8226deec  bl    sub_8220A360                              ; the time base, raw
//     8226df0c  while ((now - qword_83556BD8) / dbl_83556BD0 < deadline)
//
// dbl_8329ED28 is the Lua-set budget: the `SetMillisecondsForGarbageCollectorToTake` native is
// `sub_822E5748(ms)`: `dbl_8329ED28 = ms * 0.001` (0x822E575C), registered at 0x822E6234.
//
// Under the host-driven time base the raw counter advances only when the host grants a step, so
// the deadline was unreachable mid-tick and the loop marked and swept the whole heap until the
// next frame's grant. The slice here runs on the real clock, for this thread, for the length of
// the call; the `force` path (full collection, no clock) and the step-size mode are untouched.
#include <cstdint>

#include <rex/ppc.h>

#include "core/rex_payload_bridge.h"

REX_EXTERN(__imp__rex_lhLua_GcSliceStep);

REX_EXTERN(rex_lhLua_GcSliceStep) {
  refii_bridge_rex_time_base_real_slice(1);
  __imp__rex_lhLua_GcSliceStep(ctx, base);
  refii_bridge_rex_time_base_real_slice(0);
}
