// Guards on the guest's text path. `Babel_MaterializeString` (`sub_8237E040`) allocates
// `*(u32*)(blockDesc+4)` bytes via `operator new` whenever that field is >= `dword_834BE98C`
// (8237e068 selects the allocating branch, 8237e0d8 allocates unmodified). On a garbage field
// `operator new` returns 0 and the guest runs `DbgBreakPoint`, stopping the tick thread.

#include <array>
#include <atomic>
#include <cstdint>
#include <cstdio>
#include <string>
#include <mutex>
#include <unordered_map>

#include <rex/logging.h>

#include "vfs_hooks_private.h"
#include "core/guest_state.h"
#include "hooks/ppc_externs.h"
#include "core/guest_build.h"

PPC_EXTERN_FUNC(__imp__rex_lhBabel_MaterializeString);

namespace {

/// The shared decompression scratch's capacity — `dword_834BE98C`, 18022 from
/// `TextManager_Construct` (`8237a854 li r11, 0x4666`). Smaller blocks never allocate.
constexpr refii::guest::Addr kScratchCapacity{0x834BE98Cu};

/// A size no Babel block can have (`book.babel` is 2.6 MB whole); far above anything real.
constexpr uint32_t kImpossibleBlockBytes = 64u * 1024u * 1024u;

bool Plausible(uint32_t value) { return value >= 0x10000u && value < 0xC0000000u; }

}  // namespace

/// `Babel_MaterializeString(blockDesc, offset, out)`.
REX_EXTERN(rex_lhBabel_MaterializeString) {
    REX_FUNC_PROLOGUE();

    const uint32_t descriptor = ctx.r3.u32;
    const uint32_t offset = ctx.r4.u32;

    if (Plausible(descriptor)) {
        const uint32_t method = refii::vfs::LoadGuestU32(base, descriptor);
        const uint32_t decompressed = refii::vfs::LoadGuestU32(base, descriptor + 4u);
        const uint32_t compressed = refii::vfs::LoadGuestU32(base, descriptor + 8u);
        const uint32_t data = refii::vfs::LoadGuestU32(base, descriptor + 12u);
        const uint32_t scratch = refii::vfs::LoadGuestU32(base, kScratchCapacity);

        static int sampled = 0;
        if (sampled < 4) {
            ++sampled;
            REXLOG_INFO("Refii babel: materialise desc=0x{:08X} method={} decompressed={} "
                        "compressed={} data=0x{:08X} offset={} scratch={} -> {}",
                        descriptor, method, decompressed, compressed, data, offset, scratch,
                        decompressed >= scratch ? "ALLOCATES" : "uses the scratch");
        }

        if (decompressed >= kImpossibleBlockBytes) {
// Refuse: the allocator returns 0 for this size and `operator new` callers then run
// `DbgBreakPoint` (`8221f298`). The caller's output is left untouched (a stale label).
            REXLOG_ERROR("Refii babel: REFUSING a materialise of {} byte(s) "
                         "(0x{:08X}, byte-swaps to {}) — desc=0x{:08X} method={} compressed={} "
                         "data=0x{:08X} offset={} scratch={}. `operator new` answers 0 for this "
                         "and the guest then runs DbgBreakPoint, which is the menu freeze.",
                         decompressed, decompressed, __builtin_bswap32(decompressed),
                         descriptor, method, compressed, data, offset, scratch);
            return;
        }
    }

    __imp__rex_lhBabel_MaterializeString(ctx, base);
}

// ---------------------------------------------------------------------------
// The allocator `sub_8221F2C0`, reported but never refused — its callers dereference the result.
// `sub_8221F258` is the game's `operator new` (forwards here; on null runs `DbgBreakPoint` at
// `0x8221f298`). `refii_config.toml` names `0x8221F2C0` as `rex_lh_malloc_impl`, so codegen emits
// that name and no `sub_8221F2C0` pair — hooking the numeric name fails the module's dlopen.
PPC_EXTERN_FUNC(__imp__rex_lh_malloc_impl);
PPC_EXTERN_FUNC(__imp__rex_lhHeap_Free);
PPC_EXTERN_FUNC(__imp__rex_lhHeap_Alloc);

namespace {

/// A size no single game object has: the guest heap range is 0x40000000..0x70000000 (768 MB).
constexpr uint32_t kImpossibleAllocBytes = 256u * 1024u * 1024u;

}  // namespace

/// The guest heap's allocator, under `operator new` and forty other callers.
REX_EXTERN(rex_lh_malloc_impl) {
    REX_FUNC_PROLOGUE();

    const uint32_t size = ctx.r3.u32;
    if (size >= kImpossibleAllocBytes) {
        // When `lr` is 0x8221F26C the request came through `operator new` (0x8221F258), whose
        // prologue is `mflr r12; stw r12, -8(r1); stwu r1, -0x60(r1)`, so its caller's return
        // address is at r1+0x58 on entry here.
        const uint32_t kOperatorNewReturn = refii::guest::Address(0x8221F26Cu);
        const uint32_t lr = static_cast<uint32_t>(ctx.lr);
        const uint32_t outer_lr =
            lr == kOperatorNewReturn ? refii::vfs::LoadGuestU32(base, ctx.r1.u32 + 0x58u) : lr;
        REXLOG_ERROR("Refii alloc: IMPOSSIBLE operator new of {} byte(s) (0x{:08X}, byte-swaps "
                     "to {}) from lr=0x{:08X}, caller=0x{:08X}. This answers 0, and every "
                     "caller that checks runs DbgBreakPoint (0x8221F298 under operator new), "
                     "which is the freeze. Decompile `caller` and find the field this size was "
                     "read from.",
                     size, size, __builtin_bswap32(size), lr, outer_lr);
    }

    __imp__rex_lh_malloc_impl(ctx, base);
}

// ---------------------------------------------------------------------------
// THE DOUBLE-FREE GUARD. Every co-op crash since 2026-10-07 is one corruption surfacing wherever
// the broken allocator lists are next walked: `InsertFreeNode` reading 0x9, `HeapPool_Alloc`
// reading 0x1, a write to 0x820018C3 — and twice the allocator has handed out a VTABLE
// (0x82001770, 0x820018C0) as a block. Both vtables belong to the animation blend ramps made in
// `sub_8219B598` / `sub_821B7BA0`: a live ramp freed a second time puts its vtable word on the
// free list. This names the code that does it. The guard sits on the two functions every path
// reaches — `sub_83238460` (free: under `operator delete` 0x8221BD38, realloc `sub_82BF1170` and
// the allocator table at 0x8215A8A0) and `sub_83238598` (allocate: under `sub_8221F2C0`, realloc
// and the same table): a free records the pointer with its caller chain, an allocation clears a
// pointer handed out again, and a second free of a pointer still recorded logs BOTH chains
// (first free, second free) — the double free itself, at the moment it happens.
// ---------------------------------------------------------------------------
#if defined(__ANDROID__)
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
#else
namespace {

constexpr std::size_t kGuardFrames = 8u;
constexpr std::size_t kGuardMaxEntries = 1u << 21;

struct FreeRecord {
    std::array<uint32_t, kGuardFrames> chain{};
    uint64_t seq = 0u;
};

std::mutex g_guard_mutex;
std::unordered_map<uint32_t, FreeRecord> g_guard_freed;
uint64_t g_guard_seq = 0u;
std::atomic<uint32_t> g_guard_reports{0};

/// The guest caller chain: the return address, then the title's own back chain (word at [r1],
/// saved LR at back-chain - 8), as the thread dumper walks it — ONLY inside this thread's stack.
/// The bounds are the PCR's (r13): stack base (high) at +0x70, stack end (low) at +0x74. The
/// outermost frame's back chain is the stack's top, an unmapped word; reading it faulted every
/// thread at boot (run 2026-10-19 17:36, read of 0x700E0000).
std::array<uint32_t, kGuardFrames> GuestChain(const PPCContext& ctx, uint8_t* base) {
    std::array<uint32_t, kGuardFrames> chain{};
    chain[0] = static_cast<uint32_t>(ctx.lr);
    const uint32_t pcr = ctx.r13.u32;
    if (pcr < 0x10000u || pcr >= 0xC0000000u) {
        return chain;
    }
    const uint32_t high = refii::vfs::LoadGuestU32(base, pcr + 0x70u);
    const uint32_t low = refii::vfs::LoadGuestU32(base, pcr + 0x74u);
    if (low < 0x10000u || high <= low || high - low > 64u * 1024u * 1024u) {
        return chain;
    }
    uint32_t sp = ctx.r1.u32;
    for (std::size_t i = 1; i < kGuardFrames; ++i) {
        if (sp < low || sp + 4u > high) break;
        const uint32_t back = refii::vfs::LoadGuestU32(base, sp);
        if (back <= sp || back >= high || back - 8u < low) break;
        chain[i] = refii::vfs::LoadGuestU32(base, back - 8u);
        sp = back;
    }
    return chain;
}

std::string ChainText(const std::array<uint32_t, kGuardFrames>& chain) {
    std::string text;
    for (uint32_t lr : chain) {
        if (lr == 0u) break;
        char buf[16];
        std::snprintf(buf, sizeof(buf), " %08X", lr);
        text += buf;
    }
    return text;
}

}  // namespace

REX_EXTERN(rex_lhHeap_Alloc) {
    REX_FUNC_PROLOGUE();
    __imp__rex_lhHeap_Alloc(ctx, base);
    const uint32_t block = ctx.r3.u32;
    if (block != 0u) {
        std::lock_guard<std::mutex> lock(g_guard_mutex);
        g_guard_freed.erase(block);
    }
}

REX_EXTERN(rex_lhHeap_Free) {
    REX_FUNC_PROLOGUE();
    const uint32_t block = ctx.r3.u32;
    if (block != 0u) {
        const std::array<uint32_t, kGuardFrames> chain = GuestChain(ctx, base);
        std::unique_lock<std::mutex> lock(g_guard_mutex);
        auto found = g_guard_freed.find(block);
        if (found != g_guard_freed.end()) {
            const FreeRecord first = found->second;
            lock.unlock();
            if (g_guard_reports.fetch_add(1, std::memory_order_relaxed) < 16u) {
                REXLOG_ERROR("Refii DOUBLE FREE of guest block 0x{:08X} (first word 0x{:08X}) — "
                             "first freed (#{}) from{}; freed AGAIN from{} (build {} addresses, "
                             "innermost first). The second free is skipped.",
                             block, refii::vfs::LoadGuestU32(base, block), first.seq,
                             ChainText(first.chain), ChainText(chain), refii_guest_build());
            }
            // A second free corrupts the allocator for good; refusing it keeps the run alive
            // long enough to read the report, and leaks one block.
            return;
        }
        if (g_guard_freed.size() >= kGuardMaxEntries) {
            g_guard_freed.clear();
        }
        g_guard_freed[block] = FreeRecord{chain, ++g_guard_seq};
    }
    __imp__rex_lhHeap_Free(ctx, base);
}
#endif
