#include "vfs_hooks_private.h"

#include <cstdint>

#include "core/guest_state.h"
#include "hooks/ppc_externs.h"
#include "core/guest_build.h"

// SAVED PARTICLE CHILDREN ARE NOT RESTORED; THEIR OWNERS MAKE NEW ONES.
//
// A particle entity attached to a saving parent inherits game-saving: the attach `sub_825EBBC8`
// runs `sub_825EA450`, which sets the child's `+144 |= 1` when the parent saves. So the hero's
// Morph particles — the good halo, the impure flies, the shadow-bandit eyes — are saved as
// entities, and component 22's serialiser `sub_825EEAC8` writes each as an "AttachedEntity"
// {EntityUID, Dummy, DummyIndex}. On load the resolver `sub_825EEF58` (component 22 slot 11)
// re-attaches them by UID through `sub_825EC6A8` — never through the emitter's own attach
// `sub_8256A0F0` — while the Morph rebuild `sub_825F33C8` kills only its own list and makes a
// fresh set. Every save/load left the previous set alive: seventeen halos stacked on one hero,
// and red shadow-bandit eyes on a pure-good one long after PinkEye was cleared.
//
// The guest's own idiom for a transient particle is the ParticleAttacher's `+145 |= 0x40`
// (`sub_827661B0`, 0x8276640c), which keeps it out of the save; the Morph rebuild never sets
// it. Here every restored child that is a particle emitter (component 85) is killed with the
// entity kill the Morph rebuild itself uses (`sub_8238D828`) before the resolver runs. The kill
// clears `+144 & 0x40`, so the resolver's own alive test (0x825ef0c4) skips the record. It
// cleans saves that already carry the leak as well as new ones.

namespace {

constexpr uint32_t kParticleEmitterComponent = 85u;

bool HasComponent(uint8_t* base, uint32_t entity, uint32_t id) {
  const uint32_t word = refii::vfs::LoadGuestU32(base, entity + 36u + (4u * (id >> 5)));
  return ((word >> (id & 31u)) & 1u) != 0u;
}

uint32_t CallGuest(PPCContext& ctx, uint8_t* base, const PPCContext& saved, uint32_t frame,
                   uint32_t address, uint32_t r3, uint32_t r4 = 0u) {
  PPCFunc* fn = refii::vfs::FindGuestFunction(address);
  if (fn == nullptr) {
    return 0u;
  }
  ctx = saved;
  ctx.r1.u32 = frame;
  ctx.r3.u32 = r3;
  ctx.r4.u32 = r4;
  ctx.ctr.u32 = address;
  fn(ctx, base);
  const uint32_t result = ctx.r3.u32;
  ctx = saved;
  return result;
}

}  // namespace

REX_EXTERN(rex_lhObjectAttachment_ResolveLoaded) {
  refii::vfs::EnsureAlternateSignalStack();
  REX_FUNC_PROLOGUE();
  const uint32_t component = ctx.r3.u32;
  const uint32_t pending = component != 0u ? refii::vfs::LoadGuestU32(base, component + 36u) : 0u;
  if (pending != 0u) {
    // The world the resolver itself looks entities up in (0x825eef74..0x825eef8c).
    const uint32_t root = refii::vfs::LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
    const uint32_t game = root != 0u ? refii::vfs::LoadGuestU32(base, root + 12u) : 0u;
    const uint32_t holder = game != 0u ? refii::vfs::LoadGuestU32(base, game + 88u) : 0u;
    const uint32_t slot = holder != 0u ? refii::vfs::LoadGuestU32(base, holder + 4u) : 0u;
    const uint32_t world = slot != 0u ? refii::vfs::LoadGuestU32(base, slot) : 0u;
    const uint32_t begin = refii::vfs::LoadGuestU32(base, pending + 4u);
    const uint32_t end = refii::vfs::LoadGuestU32(base, pending + 8u);
    if (world != 0u && begin != 0u && end > begin && ((end - begin) & 15u) == 0u &&
        (end - begin) <= 16u * 1024u) {
      const PPCContext saved = ctx;
      const uint32_t frame = (saved.r1.u32 - 0x40u) & ~0xFu;
      for (uint32_t record = begin; record < end; record += 16u) {
        const uint32_t child = CallGuest(ctx, base, saved, frame,
                                         refii::guest::Address(0x82264488u), world, record);
        if (child != 0u && (refii::vfs::LoadGuestU8(base, child + 144u) & 0x40u) != 0u &&
            HasComponent(base, child, kParticleEmitterComponent)) {
          CallGuest(ctx, base, saved, frame, refii::guest::Address(0x8238D828u), child);
        }
      }
      ctx = saved;
    }
  }
  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhObjectAttachment_ResolveLoaded);
}
