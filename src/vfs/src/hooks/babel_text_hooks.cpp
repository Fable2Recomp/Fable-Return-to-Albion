// Guards on the guest's text path. `Babel_MaterializeString` (`sub_8237E040`) allocates
// `*(u32*)(blockDesc+4)` bytes via `operator new` whenever that field is >= `dword_834BE98C`
// (8237e068 selects the allocating branch, 8237e0d8 allocates unmodified). On a garbage field
// `operator new` returns 0 and the guest runs `DbgBreakPoint`, stopping the tick thread.

#include <cstdint>

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
