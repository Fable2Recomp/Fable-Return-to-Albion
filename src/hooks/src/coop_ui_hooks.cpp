// Co-op: each player's own expression wheel and tutorials, where the guest reads hero one.
//
// THE EXPRESSION WHEEL is one widget W = *(*(GUI+32)+60) that either player opens. Opening it
// stores the opener's player id at W+24 (`sub_826A9A60`, 0x826a9a80), and Accept's flag
// ("the owner is player one", `sub_826AA230`) already makes the chosen expression play on hero
// two (`sub_826AAD88`, 0x826aae1c). What still reads hero one whoever owns it:
//   - the list build `sub_826ABCD0` (`bl sub_8222C1B8` at 0x826abd80): the expression book
//     (component 154) and the inventory (13) the entries come from;
//   - the perform step `sub_826AA790` (0x826aa818): the inventory behind item entries;
//   - the sector-6 enable in `sub_826AB1F8` (inline players+108 read, 0x826ab238);
//   - the child flag W+31 in `sub_826A9A60` (inline read, 0x826a9aa4).
// Each answers the owner's hero here (RefiiCoopWheelHero). The list and the item component are
// what the wheel shows and uses; the other two keep the safety toggle and the child rule on the
// right hero.
//
// TUTORIALS run only on the session host and only for hero one: DisplayTutorial
// (`sub_821B5260`) refuses unless net+52 && +53 (join at 0x821b52bc), the queue tick
// `sub_8227BF90` the same (0x8227c008), and the tick holds or drops tutorials on hero one's
// Player component (+59/+64 at 0x8227c094, +40 at 0x8227c3e0). Fable 3 queues them on every
// machine for its own hero (`sub_821D11E0`, no host gate). On an online joiner both gates pass
// and both hero reads answer the joiner's own hero (hero two); the host-sent ones
// (ShouldSendToClient, messages 177/178/254) still arrive as shipped.
//
// JOBS: the job meter's handler `sub_823D11C8` takes Player 1's pad only (0x823d1228); while
// player two works a job (the core Lua's statement) it takes player two's.
//
// PROPERTY: the real-estate screen resolves its buyer as hero one (`sub_826B1A08`, stored at
// screen+144, 0x826b1a6c); opened from a sale sign player two used, it is player two.

#include <atomic>
#include <cstdint>

#include <rex/ppc/context.h>

#include "core/guest_build.h"
#include "core/guest_state.h"
#include "core/refii_mod.h"

extern "C" uint8_t* RefiiGuestBase(void);
extern "C" int RefiiCoopJobWorker(void);
extern "C" int RefiiCoopTakeEstateBuyer(void);
extern "C" uint32_t RefiiCoopPlayerWithId(uint8_t* base, uint32_t id);

namespace {

bool Plausible(uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; }

/// This machine is the online joiner: net manager +52 and +54 set, +53 clear.
bool IsJoiner(uint8_t* base) {
  const uint32_t root = refii::vfs::LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
  const uint32_t game = Plausible(root) ? refii::vfs::LoadGuestU32(base, root + 0xCu) : 0u;
  const uint32_t net = Plausible(game) ? refii::vfs::LoadGuestU32(base, game + 0x8Cu) : 0u;
  return Plausible(net) && refii::vfs::LoadGuestU8(base, net + 0x34u) != 0u &&
         refii::vfs::LoadGuestU8(base, net + 0x36u) != 0u &&
         refii::vfs::LoadGuestU8(base, net + 0x35u) == 0u;
}

/// The Player for an id (`sub_82264230`'s map, read): the PlayerManager at game+156.
uint32_t PlayerWithId(uint8_t* base, uint32_t id) { return RefiiCoopPlayerWithId(base, id); }

/// Player two's hero when it carries `component`, else 0.
uint32_t HeroTwoWith(uint8_t* base, uint32_t component) {
  const uint32_t hero = refii_mod_player_hero(base, 2);
  return hero != 0u && refii_mod_component(base, hero, component) != 0u ? hero : 0u;
}

}  // namespace

// A hero-one read inside the wheel (`hero`), with the wheel W in `wheel`: player two's hero
// when player two owns the open wheel.
void RefiiCoopWheelHero(PPCRegister& hero, PPCRegister& wheel) {
  uint8_t* base = RefiiGuestBase();
  if (base == nullptr || !Plausible(wheel.u32) || refii::vfs::LoadGuestU32(base, wheel.u32 + 24u) != 2u) {
    return;
  }
  const uint32_t two = refii_mod_player_hero(base, 2);
  if (two != 0u) {
    hero.u64 = two;
  }
}

// The tutorial manager's host gates (`r11` = "host"): a joiner runs its own queue.
void RefiiCoopTutorialsHere(PPCRegister& r11) {
  uint8_t* base = RefiiGuestBase();
  if (base != nullptr && IsJoiner(base)) {
    r11.u64 = 1u;
  }
}

// The tutorial tick's hero-one reads (`r3`): on a joiner, the joiner's own hero.
void RefiiCoopTutorialHero(PPCRegister& r3) {
  uint8_t* base = RefiiGuestBase();
  if (base == nullptr || !IsJoiner(base)) {
    return;
  }
  const uint32_t own = refii_mod_player_hero(base, 2);
  if (own != 0u) {
    r3.u64 = own;
  }
}

// The job meter's Player (`r3`, Player 1): player two's while player two works the job.
void RefiiCoopJobMeterPlayer(PPCRegister& r3) {
  uint8_t* base = RefiiGuestBase();
  if (base == nullptr || RefiiCoopJobWorker() != 2) {
    return;
  }
  const uint32_t two = PlayerWithId(base, 2u);
  if (Plausible(two)) {
    r3.u64 = two;
  }
}

// The real-estate screen's buyer (`r6`, hero one) about to be stored at screen+144: player two
// when the sale sign was player two's.
void RefiiCoopEstateBuyer(PPCRegister& r6) {
  uint8_t* base = RefiiGuestBase();
  if (base == nullptr || RefiiCoopTakeEstateBuyer() != 2) {
    return;
  }
  // The screen reads the buyer's properties (69), use target (77) and purse (81).
  uint32_t two = HeroTwoWith(base, 69u);
  if (two != 0u && refii_mod_component(base, two, 77u) != 0u && refii_mod_component(base, two, 81u) != 0u) {
    r6.u64 = two;
  }
}
