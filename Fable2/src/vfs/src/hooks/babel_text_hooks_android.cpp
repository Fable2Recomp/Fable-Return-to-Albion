// The double-free guard on a phone, in place of the desktop's in babel_text_hooks.cpp (which
// says what the guard is for, on `sub_83238460` free and `sub_83238598` allocate). CMake builds
// this file on Android only.

#include <array>
#include <atomic>
#include <cstdint>

#include <rex/logging.h>

#include "vfs_hooks_private.h"
#include "core/guest_state.h"
#include "hooks/ppc_externs.h"
#include "core/guest_build.h"

PPC_EXTERN_FUNC(__imp__rex_lhHeap_Free);
PPC_EXTERN_FUNC(__imp__rex_lhHeap_Alloc);

// On the phone the guard keeps refusing a second free but drops the diagnostics: no lock, no
// stack walk, no host allocation. Every guest thread allocates and frees through here, the
// guest is CPU-bound there, and the desktop's map grows to 2M records (~130 MB) on a device
// already near its memory limit. A fixed table of 64K slots, one per pointer hash, holds the
// last freed pointer of each slot: a free of the pointer its slot still holds is the double
// free; an allocation empties its slot only if it still holds that pointer. A collision forgets
// the older pointer, as the desktop's clear at 2M entries forgets all of them.
namespace {

constexpr uint32_t kGuardSlotBits = 16u;
std::array<std::atomic<uint32_t>, 1u << kGuardSlotBits> g_guard_slots{};
std::atomic<uint32_t> g_guard_reports{0};

std::atomic<uint32_t>& GuardSlot(uint32_t block) {
    return g_guard_slots[((block >> 3) * 0x9E3779B1u) >> (32u - kGuardSlotBits)];
}

}  // namespace

REX_EXTERN(rex_lhHeap_Alloc) {
    REX_FUNC_PROLOGUE();
    __imp__rex_lhHeap_Alloc(ctx, base);
    const uint32_t block = ctx.r3.u32;
    if (block != 0u) {
        uint32_t expected = block;
        GuardSlot(block).compare_exchange_strong(expected, 0u, std::memory_order_relaxed);
    }
}

REX_EXTERN(rex_lhHeap_Free) {
    REX_FUNC_PROLOGUE();
    const uint32_t block = ctx.r3.u32;
    if (block != 0u) {
        std::atomic<uint32_t>& slot = GuardSlot(block);
        if (slot.load(std::memory_order_relaxed) == block) {
            if (g_guard_reports.fetch_add(1, std::memory_order_relaxed) < 16u) {
                REXLOG_ERROR("Refii DOUBLE FREE of guest block 0x{:08X} (first word 0x{:08X}) "
                             "freed AGAIN from lr=0x{:08X} (build {}). The second free is "
                             "skipped.",
                             block, refii::vfs::LoadGuestU32(base, block),
                             static_cast<uint32_t>(ctx.lr), refii_guest_build());
            }
            return;
        }
        slot.store(block, std::memory_order_relaxed);
    }
    __imp__rex_lhHeap_Free(ctx, base);
}
