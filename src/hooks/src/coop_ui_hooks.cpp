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
#include <mutex>
#include <unordered_map>

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

// PLAYER ONE'S USER INDEX, read with no null test. Four places look up Player 1
// (`sub_82264230(pm, 1)`) and read its Xbox user index (+0x28) the next instruction: the
// logbook's populate (0x829b871c, before XamUserGetSigninState), and three others that hand it
// to a user call (0x82444d6c, 0x824496dc, 0x826d9664). A joiner's player map has no key 1, and
// shipped, the henchman never opened a logbook — player two doing so crashed reading 0x28
// (2026-10-20). With no Player 1 here, the user is this machine's own: Player 2.
void RefiiCoopPlayerOneOrLocal(PPCRegister& r3) {
  uint8_t* base = RefiiGuestBase();
  if (base == nullptr || r3.u32 != 0u) {
    return;
  }
  const uint32_t local = PlayerWithId(base, 2u);
  if (Plausible(local)) {
    r3.u64 = local;
  }
}

// EXPERIENCE ORBS GO TO WHOEVER EARNED THEM. A kill's reward records its killer as a weak handle
// at reward+0x50 (`sub_82772260`, 0x82772830), but the orb spawned from it (`sub_827730F0`) keeps
// only the creature that died; the orb's update (`sub_8255C858`, host only) then flies to and
// credits hero one — a "sucker" that is hero two is redirected to hero one (0x8255cac8), and with
// none set the target is hero one outright (0x8255cadc). Player two's kills paid player one.
// The killer is noted per orb at spawn, and where every target path meets (0x8255cc24, r31 the
// target) an orb a hero earned flies to that hero; the credit (`sub_8255EF58` → comp164) follows.
namespace {
std::mutex g_orb_mutex;
std::unordered_map<uint32_t, uint32_t> g_orb_earner;  // orb component → earner's mod handle
}  // namespace

// After the orb's creator store (`r31` the orb component, `r14` the reward).
void RefiiCoopOrbEarner(PPCRegister& r31, PPCRegister& r14) {
  uint8_t* base = RefiiGuestBase();
  if (base == nullptr || !Plausible(r31.u32)) {
    return;
  }
  const uint32_t holder = Plausible(r14.u32) ? refii::vfs::LoadGuestU32(base, r14.u32 + 0x54u) : 0u;
  const uint32_t killer = Plausible(holder) ? refii::vfs::LoadGuestU32(base, holder) : 0u;
  const bool hero = killer != 0u &&
                    (killer == refii_mod_player_hero(base, 1) || killer == refii_mod_player_hero(base, 2));
  std::lock_guard<std::mutex> lock(g_orb_mutex);
  if (g_orb_earner.size() > 4096u) {
    g_orb_earner.clear();
  }
  if (hero) {
    g_orb_earner[r31.u32] = refii_mod_handle(killer);
  } else {
    g_orb_earner.erase(r31.u32);
  }
}

// The orb's target (`r31`, a hero) where its paths meet; `r30` set means "measure to the sucker".
// The orb component is the update's `this`, kept in `r21` (`mr r21, r3` at 0x8255c884).
void RefiiCoopOrbTarget(PPCRegister& r31, PPCRegister& r30, PPCRegister& orb) {
  uint8_t* base = RefiiGuestBase();
  if (base == nullptr || !Plausible(r31.u32)) {
    return;
  }
  const uint32_t one = refii_mod_player_hero(base, 1);
  const uint32_t two = refii_mod_player_hero(base, 2);
  if (two == 0u || (r31.u32 != one && r31.u32 != two)) {
    return;
  }
  uint32_t earner = 0u;
  {
    std::lock_guard<std::mutex> lock(g_orb_mutex);
    const auto found = g_orb_earner.find(orb.u32);
    earner = found != g_orb_earner.end() ? refii_mod_handle_address(found->second) : 0u;
  }
  if ((earner == one || earner == two) && earner != r31.u32 && refii_mod_entity_alive(base, earner) != 0) {
    r31.u64 = earner;
    r30.u64 = 0u;
  }
}
