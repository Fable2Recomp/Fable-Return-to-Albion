// Which call inside Game_InitDatabases does not return.
//
// Starting a New Game parks between 0x82311020 and 0x82311648, and the function is silent:
// it neither logs nor sets a progress flag until its last statement, so from outside a
// stall is indistinguishable from a guest with nothing to do. Elimination has already
// cleared the render fence, the loading-screen wait, the databases, the level data and the
// command-stream ring — every one of those refuted by measurement rather than argument.
//
// What no static pass could settle is the two INDIRECT calls in the window, 0x82311234 and
// 0x823112C4: a `bctrl` is invisible to a call-graph walk, and each investigation named
// them as its blind spot. Rather than guess through them, this brackets the direct calls
// that surround them. The last "enter" with no matching "exit" IS the answer, and it needs
// no addresses beyond the ones already proven.
//
// Order in the window (from the disassembly walk):
//   0x82311068  GDB_LoadDatabaseWithTU   "InteractiveCutscenes.gdb"
//   0x82311234  bctrl                    <- blind spot
//   0x82311244  sub_8234EF58
//   0x823112C4  bctrl                    <- blind spot
//   0x82311318  sub_82459280             Lua bring-up: 7 shipped scripts, synchronous
//   0x823113CC  sub_823272E0             font/cache path
//   0x823115D0  GameFlow_QueueCommand    the level change — never reached
//   0x8231162C  sub_823BEDC0             StartupConsoleScript.lua
//
// Deliberately entry/exit rather than a sampling probe: a spin and a deadlock look
// identical from outside, but "entered and never left" names the callee either way.

#include <atomic>
#include <cstdint>

#include <rex/logging.h>

#include "vfs_hooks_private.h"
#include "hooks/ppc_externs.h"

namespace {

/// One line in, one line out, once each — a one-shot init, so no rate limiting.
#define REFII_TRACE_GAME_INIT_CALL(symbol)                                    \
    REX_EXTERN(symbol) {                                                      \
        REX_FUNC_PROLOGUE();                                                  \
        static std::atomic<bool> entered{false};                              \
        static std::atomic<bool> left{false};                                 \
        if (!entered.exchange(true, std::memory_order_relaxed)) {             \
            REXLOG_INFO("Refii Game_InitDatabases ENTER " #symbol);           \
        }                                                                     \
        __imp__##symbol(ctx, base);                                           \
        if (!left.exchange(true, std::memory_order_relaxed)) {                \
            REXLOG_INFO("Refii Game_InitDatabases EXIT  " #symbol);           \
        }                                                                     \
    }

}  // namespace

REFII_TRACE_GAME_INIT_CALL(sub_8234EF58)
REFII_TRACE_GAME_INIT_CALL(sub_82459280)
REFII_TRACE_GAME_INIT_CALL(sub_823272E0)
REFII_TRACE_GAME_INIT_CALL(sub_823BEDC0)

// Second pass. sub_823272E0 (0x823113CC) ENTERED AND RETURNED, and sub_823BEDC0
// (0x8231162C) never enters — so the stall is between them. These bracket that span,
// which is where GameFlow_QueueCommand(CmdLevelChange) at 0x823115D0 lives.
REFII_TRACE_GAME_INIT_CALL(sub_824732A8)
REFII_TRACE_GAME_INIT_CALL(sub_82473C60)
REFII_TRACE_GAME_INIT_CALL(sub_8247A720)
REFII_TRACE_GAME_INIT_CALL(sub_82487790)
REFII_TRACE_GAME_INIT_CALL(sub_824926A8)
REFII_TRACE_GAME_INIT_CALL(sub_82882C88)
REFII_TRACE_GAME_INIT_CALL(sub_821E3108)

// One level deeper. sub_82459280 (the gameplay Lua bring-up at 0x82311318) is entered and
// never returns, and it is NOT our hook — the stall is identical with
// REFII_LUA_NATIVE_TRACE=0. Inside it are seven shipped-script load+run calls
// (sub_82C68F30) and the protected calls that run them (sub_8219A9A0), so the last call
// site to enter without leaving names the script.
//
// LR-filtered to sub_82459280's own body, and NOT once-only: there are seven call sites and
// which one matters, so each hit logs its return address.
#define REFII_TRACE_LUA_INIT_CALL(symbol)                                     \
    REX_EXTERN(symbol) {                                                      \
        REX_FUNC_PROLOGUE();                                                  \
        const uint32_t lr = ctx.lr;                                           \
        const bool inside = lr >= 0x82459280u && lr <= 0x82459900u;           \
        if (inside) {                                                         \
            REXLOG_INFO("Refii LuaInit ENTER " #symbol " (lr=0x{:08X})", lr);  \
        }                                                                     \
        __imp__##symbol(ctx, base);                                           \
        if (inside) {                                                         \
            REXLOG_INFO("Refii LuaInit EXIT  " #symbol " (lr=0x{:08X})", lr);  \
        }                                                                     \
    }

REFII_TRACE_LUA_INIT_CALL(sub_82C68F30)
REFII_TRACE_LUA_INIT_CALL(sub_8219A9A0)
REFII_TRACE_LUA_INIT_CALL(sub_82458C88)
// sub_821956A0 (the Lua GC driver) keeps this same trace inside its own hook, lua_gc_slice.cpp.
