// Co-op: player two is a hero, not a henchman (Fable 3-style co-op, phase 1;
// docs/coop_fable3_plan.md).
//
// Fable 2 makes player two a stand-in bolted onto player one, through single comparisons against
// the henchman inside otherwise player-neutral routines. Each hook below skips exactly one such
// comparison with a mid-function hook ([[midasm_hook]] in versions/*/refii_config.toml: the hook
// runs before the named instruction and then jumps), so the rest of the game's logic stands.
// Fable 3, the same engine's successor, removed the same tests (the disc IDB's counterparts are
// named with each hook). Addresses are TU1 / disc; the two builds' functions are identical in
// size and code, and every hook sits at the same offset in both.
//
//   USE: an object's script runs for its real user. The OnActionUse use routine (TU1 sub_828219F8
//     / disc sub_82753478), after the henchman getter, does `cmplw cr6,r27,r3; bne` (0x82821B88 /
//     0x82753608): a user other than player two runs the object's Lua `Create(thing, user)` at
//     0x82821CF4 / 0x82753774; player two is refused for a sale sign, an inventory holder, a bed,
//     component 80, a dive spot or a knock-door, and otherwise the user is REPLACED by the hero
//     and the routine loops — every chest, lever and door player two used ran as player one.
//     Fable 3's use (sub_829B33B0 → sub_829B3AB8) passes the real user. The scripts that took
//     "the hero" from GetPlayerHero() are answered by the core Lua (Godot/src/host/CoopHeroLua.cs).
//
//   SHOPS: a shopkeeper serves player two where player two is this machine's local player.
//     Villager use (sub_827E1178 / sub_827A8120) says TEXT_AI_HENCHMAN_REJECT to player two at a
//     shopkeeper (`clrlwi r10,r27,24; cmplwi; beq 0x827E1324` at 0x827E12F0 / 0x827A8298; r27 =
//     user is player two). The shop then opens for the LOCAL player: the screen's buyer is
//     `sub_8229EE78(sub_821C7CE0(gui))` (sub_826AF430), and sub_821C7CE0 is player one on the
//     session host — solo and local co-op read as host — else player two. So the refusal is
//     lifted exactly where player two IS the local player (an online joiner); on a host it would
//     have player one pay. Travel agents and the gamesmaster keep theirs (Fable 3 keeps the
//     travel agents' too: sub_827E1C40).
//
//   PROGRESSION: each hero keeps what they earn. Experience (rex_CECExperience_Modify), gold
//     (rex_CECMoney_Modify) and renown (rex_lhHeroStats_ModifyRenown) pool player two's gains
//     into player one and split player one's by the co-op screen's sliders (coopMgr +172/+176/
//     +180). Each routine reaches its own "credit this component" block when there is nothing to
//     split; the hook jumps straight there:
//       experience: before `clrlwi r11,r28,24` (allowSplit) at 0x8256DCBC / 0x8251C2EC, after the
//         amount <= 0 test, to 0x8256DE68 / 0x8251C498 (f30 is already loaded for its clamp);
//       gold: before `cmpwi cr6,r28,0` at 0x8242E73C / 0x8239E85C, to 0x8242E93C / 0x8239EA5C;
//       renown: right after `mr r28,r23` (the amount) at 0x824D6450 / 0x8249D5C0, to 0x824D6630 /
//         0x8249D7A0.
//     The network gates in front of each (only the host credits) are untouched. Fable 3 keeps a
//     purse per hero too (sub_823B0590: a split only between spouses or partners).
//
//   REACTIONS: villagers react to player two. The opinion-reaction filter (sub_8278CAE0 /
//     sub_8275F0F0) refuses player two unless the reaction's data sets ForHenchman (`extrwi
//     r20,r10,1,27`): `cmplw cr6,r22,r3; beq refuse` at 0x8278D998 / 0x8275FFA8 is skipped to
//     0x8278D9A0 / 0x8275FFB0, the path a ForHenchman reaction takes.

#include <array>
#include <atomic>
#include <cstdint>
#include <cstring>
#include <mutex>

#include <rex/logging.h>
#include <rex/ppc/context.h>
#include <rex/ppc/func.h>

#include "core/guest_build.h"
#include "core/guest_state.h"
#include "core/payload_bridge.h"
#include "core/refii_mod.h"


extern "C" REX_FUNC(__imp__rex_CECVillager_OnUse);

namespace {

/// One line the first time each hook runs, so a run says which seams were reached.
void Once(std::atomic<bool>& said, const char* what) {
  if (!said.exchange(true, std::memory_order_relaxed)) {
    REXLOG_INFO("Refii co-op heroes: {}", what);
  }
}

std::atomic<bool> g_said_use{false};
std::atomic<bool> g_said_shop{false};
std::atomic<bool> g_said_experience{false};
std::atomic<bool> g_said_gold{false};
std::atomic<bool> g_said_renown{false};
std::atomic<bool> g_said_reaction{false};

/// Set by the villager-use wrapper for the length of its call: the user is player two AND player
/// two is this machine's local player. The midasm hook inside the same call reads it. Villager
/// use runs on the game thread only.
bool g_shop_for_local_player_two = false;

/// The same call's user is player two in LOCAL co-op (both heroes on this machine).
bool g_shop_user_is_local_two = false;

/// Local player two's shop is up: set when the shopkeeper path opens it for them, dropped on the
/// first read after the shop screen is gone.
std::atomic<bool> g_shopping_player_two{false};

bool Plausible(uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; }

/// The guest's IsLocalCoopGame: game+0x11C == 2.
bool LocalCoopGame(uint8_t* base) {
  const uint32_t root = refii::vfs::LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
  const uint32_t game = Plausible(root) ? refii::vfs::LoadGuestU32(base, root + 0xCu) : 0u;
  return Plausible(game) && refii::vfs::LoadGuestU32(base, game + 0x11Cu) == 2u;
}

/// sub_821C7CE0's rule: the local player is player one when this machine is the session host
/// (net +0x34 and +0x35 — every solo and local co-op game reads as host), else player two.
bool LocalPlayerIsPlayerTwo(uint8_t* base) {
  const uint32_t root = refii::vfs::LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
  const uint32_t game = Plausible(root) ? refii::vfs::LoadGuestU32(base, root + 0xCu) : 0u;
  const uint32_t net = Plausible(game) ? refii::vfs::LoadGuestU32(base, game + 0x8Cu) : 0u;
  if (!Plausible(net)) {
    return false;
  }
  const bool host = refii::vfs::LoadGuestU8(base, net + 0x34u) != 0u && refii::vfs::LoadGuestU8(base, net + 0x35u) != 0u;
  return !host;
}

}  // namespace

// The real user's script path; nothing to decide.
void RefiiCoopUseByRealUser() { Once(g_said_use, "an object used by player two runs for player two"); }

// True: open the shop for player two (jump to the shopkeeper path); false: the shipped test.
bool RefiiCoopShopForLocalPlayerTwo() {
  if (g_shop_for_local_player_two) {
    Once(g_said_shop, "a shop serves player two");
    if (g_shop_user_is_local_two) {
      g_shopping_player_two.store(true, std::memory_order_relaxed);
    }
    return true;
  }
  return false;
}

extern "C" uint8_t* RefiiGuestBase(void);

extern "C" uint8_t* RefiiGuestBase(void);

// CRIME IS THE CRIMINAL'S. AddCrime (`sub_827C9C10`) records a crime against the criminal, but
// for a player criminal that is not hero one it then makes hero one the criminal: `bl
// sub_8222C1B8` (0x827C9F64), `cmplw r28, r3`, `mr r29, r3` (0x827C9F9C) — r29 already holds the
// real criminal (`mr r29, r28`, 0x827C9F50), and everything after uses r29. True skips the remap
// for hero two, so the alarm, the arrest, the fine and the crime screen (which already picks
// player two's, `DisplayCrimeScreen` 0x827CFF08) are player two's own.
bool RefiiCoopCrimeIsTheirs(PPCRegister& r28) {
  uint8_t* base = RefiiGuestBase();
  return base != nullptr && r28.u32 != 0u && r28.u32 == refii_mod_player_hero(base, 2);
}

// A DEED'S ALIGNMENT AND RENOWN ARE THE DOER'S. The villager's deed handler
// (`rex_CECOpinionReaction_OnDeed`, `sub_82786F58`) credits morality, purity and renown to hero
// one's stats whoever did the deed (`bl sub_8222C1B8` at 0x8278809C, then `sub_824D62A0`,
// `sub_824D6BE0`, `sub_824D5BC0`). The deed message is copied to the frame at r1+0xC0
// (`sub_82619EA8`, 0x82787F24), its doer the weak reference at message+72 — r1+0x108, {vtable,
// holder} with the entity at *holder. After the call: a deed by hero two credits hero two.
void RefiiCoopDeedDoer(PPCRegister& r1, PPCRegister& r3) {
  using refii::vfs::LoadGuestU32;
  uint8_t* base = RefiiGuestBase();
  if (base == nullptr) {
    return;
  }
  const uint32_t holder = LoadGuestU32(base, r1.u32 + 0x108u + 4u);
  const uint32_t doer = Plausible(holder) ? LoadGuestU32(base, holder) : 0u;
  if (doer != 0u && doer == refii_mod_player_hero(base, 2)) {
    r3.u64 = doer;
  }
}

// EACH PLAYER HEARS WHAT IS AROUND THEM ON SPLIT SCREEN. The mixer hears a sound from the nearest of
// its primary listener (player one's camera) and its secondary listeners — one at each player's body,
// which every Player keeps (CECPlayer +348, `sub_823ED9F0`, moved each frame by `sub_823EDF80`) —
// but only for a sound whose definition says AudibleToSecondaryListeners: the distance test
// `sub_82C5B8D8` takes the flag in r4 (`clrlwi r9, r4, 24` at 0x82C5B8E0) and otherwise measures from
// the camera alone. On split screen every sound takes the nearest listener, so player two hears the
// world around them in their half.
extern "C" int RefiiCoopSplitScreen(void);

void RefiiCoopAudibleToBoth(PPCRegister& r4) {
  if (RefiiCoopSplitScreen() != 0) {
    r4.u64 = 1u;
  }
}

// LOCAL PLAYER TWO SHOPS AS THEMSELVES. The shop screen (22) acts for "the local player"
// (`sub_821C7CE0`, record 1 on one machine; every buy, sell and tattoo removal: sub_826B0768
// 0x826B0948, sub_826AFCF0 0x826AFEA4, sub_826B0D10 0x826B0D6C, sub_826AF430) and judges price and
// tattoos on hero one outright (`sub_8222C1B8` at 0x8246DBB0 in sub_8246DB30, 0x8246E0E8 in
// sub_8246DE50). While player two's shop is up those answer player two and hero two; the flag
// drops on the first read after the shop screen is gone.
namespace {

bool ShopScreenUp(uint8_t* base) {
  using refii::vfs::LoadGuestU32;
  const uint32_t gui = LoadGuestU32(base, refii::guest::Address(0x83496B08u));
  const uint32_t screens = Plausible(gui) ? LoadGuestU32(base, gui + 56u) : 0u;
  const uint32_t id = Plausible(screens) ? LoadGuestU32(base, screens + 4u) : 0xFFFFFFFFu;
  return id != 0xFFFFFFFFu && refii::guest::ScreenIdToTu1(id) == 22u;
}

bool Shopping(uint8_t* base) {
  if (!g_shopping_player_two.load(std::memory_order_relaxed)) {
    return false;
  }
  if (!ShopScreenUp(base)) {
    g_shopping_player_two.store(false, std::memory_order_relaxed);
    return false;
  }
  return true;
}

}  // namespace

extern "C" int RefiiCoopShoppingPlayerTwo(void) {
  return g_shopping_player_two.load(std::memory_order_relaxed) ? 1 : 0;
}

// `sub_821C7CE0`'s tail (`li r4, 1` then `b sub_82264230`): r4 is the local player's number.
void RefiiCoopShopPlayer(PPCRegister& r4) {
  uint8_t* base = RefiiGuestBase();
  if (base != nullptr && r4.u32 == 1u && Shopping(base)) {
    r4.u64 = 2u;
  }
}

// After `bl sub_8222C1B8` in the price and tattoo checks: r3 is hero one; player two's shop asks
// about hero two.
void RefiiCoopShopHero(PPCRegister& r3) {
  uint8_t* base = RefiiGuestBase();
  if (base != nullptr && Shopping(base)) {
    const uint32_t two = refii_mod_player_hero(base, 2);
    if (two != 0u) {
      r3.u64 = two;
    }
  }
}

void RefiiCoopExperienceOwnPool() { Once(g_said_experience, "experience credited to its earner"); }

void RefiiCoopGoldOwnPurse() { Once(g_said_gold, "gold credited to its earner"); }

void RefiiCoopRenownOwnPool() { Once(g_said_renown, "renown credited to its earner"); }

void RefiiCoopReactToPlayerTwo() { Once(g_said_reaction, "a villager reaction accepts player two"); }

// Villager use (r3 villager component, r4 user). Records the shop decision for the call.
REX_EXTERN(rex_CECVillager_OnUse) {
  const uint32_t user = ctx.r4.u32;
  const bool previous = g_shop_for_local_player_two;
  const bool previous_local = g_shop_user_is_local_two;
  const bool two = Plausible(user) && user == refii_mod_player_hero(base, 2);
  g_shop_user_is_local_two = two && LocalCoopGame(base);
  g_shop_for_local_player_two = two && (LocalPlayerIsPlayerTwo(base) || g_shop_user_is_local_two);
  __imp__rex_CECVillager_OnUse(ctx, base);
  g_shop_for_local_player_two = previous;
  g_shop_user_is_local_two = previous_local;
}

// ---------------------------------------------------------------------------------------------
// Each online player their own camera, and the world alive around both players (phases 2-3).
//
//   OWN CAMERA. Shipped, a joiner's camera ran no Lua: the manager update (sub_821B9258), when
//     net +0x34 and +0x36 (in a session, the joiner) are set, builds an empty wrapper and only
//     interpolates toward the HOST's camera, streamed as net message 0xBD (sub_82482CB0). Fable 3
//     ticks each machine's own player's camera (sub_82209680). Four steps:
//     - the update takes its Lua path on a joiner too: the joiner branch's first instruction
//       0x821B92B0 (r26 the manager) jumps to the Lua path 0x821B930C (CameraManager:FrameUpdate
//       with the player's hero, the hero the joiner's Player owns) once the manager's
//       PrimaryCamera entity resolves (manager+36, a weak ref: cell at +40, entity at *cell;
//       spawned by sub_82481670 from the Player's entity setter). ActivateCamera binds the
//       entity into the new wrapper once (sub_82481760 0x82481A6C → sub_825AD080) and the Lua
//       never re-activates a camera it holds as current, so an activation before the spawn
//       leaves the wrapper with no entity and the default pose forever. Until then the shipped
//       joiner branch runs;
//     - the Lua's ActivateCamera works there: sub_82481760 bails unless net +0x34 and +0x35
//       (the session host). The wrapper below marks a joiner's call and the hook before that
//       test (0x824817BC) jumps past it (0x824817EC). Front-end calls, where neither byte is
//       set, keep the shipped refusal;
//     - the live camera block is the machine's own: sub_82265F90 returned manager+0x130 (the
//       streamed copy) on a joiner; its game+0x11C test 0x82265FB0 jumps to the +0x90 path
//       0x82266004 on every machine;
//     - no camera is APPLIED across the wire: a received 0xBD never reaches sub_82481FC0
//       (0x82332604 → the handler's exit 0x82334954), which would MOVE the receiver's camera
//       entity (vt+100). Only the joiner sends its camera (`bl sub_82171560` 0x821B922C), and the
//       host keeps it as the peer view (FREE ROAM below);
//     The Lua side (each camera frames its own hero, the leash switch) is the core camera chunk,
//     Godot/src/host/CoopHeroLua.cs.
//
//   WORLD AROUND BOTH. The LOD distance (sub_822904E8: f1 = |v1 − centre|², the centre cached
//     once per tick at players+0xA0) centres on the MIDPOINT of the two heroes in co-op, so
//     spawners, AI rates and creature LOD die around players who part. Fable 3 takes the nearer
//     view (sub_82205D10). Here: the midpoint store (0x822906A0, positions still in v11/v12)
//     records both heroes, the camera-centre store (0x822906B8) clears them, and after the
//     result load (`lfs f1` 0x822906D0) a recorded pair turns f1 into the nearer hero's
//     distance. Lanes 1..3 are xyz (vmsum3fp128 sums guest elements 0..2).

extern "C" REX_FUNC(__imp__rex_lhCameraManager_ActivateCamera);

namespace {

std::atomic<bool> g_said_own_camera{false};
std::atomic<bool> g_said_lod{false};

/// Set by the ActivateCamera wrapper for its call: this machine is the online joiner.
bool g_activate_as_joiner = false;

bool IsJoiner(uint8_t* base) {
  const uint32_t root = refii::vfs::LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
  const uint32_t game = Plausible(root) ? refii::vfs::LoadGuestU32(base, root + 0xCu) : 0u;
  const uint32_t net = Plausible(game) ? refii::vfs::LoadGuestU32(base, game + 0x8Cu) : 0u;
  return Plausible(net) && refii::vfs::LoadGuestU8(base, net + 0x34u) != 0u &&
         refii::vfs::LoadGuestU8(base, net + 0x36u) != 0u;
}

/// The two heroes this tick when the LOD centre is their midpoint. Written once a tick by the
/// centre's recompute, read by every query after it.
struct LodPair {
  std::atomic<bool> two{false};
  float a[3] = {0.0f, 0.0f, 0.0f};
  float b[3] = {0.0f, 0.0f, 0.0f};
};
LodPair g_lod;

float DistanceSquared(const float* p, const PPCVRegister& v) {
  const float dx = v.f32[1] - p[0];
  const float dy = v.f32[2] - p[1];
  const float dz = v.f32[3] - p[2];
  return dx * dx + dy * dy + dz * dz;
}

}  // namespace

extern "C" uint8_t* RefiiGuestBase(void);

bool RefiiCoopOwnCameraUpdate(PPCRegister& r26) {
  uint8_t* base = RefiiGuestBase();
  const uint32_t cell = base != nullptr ? refii::vfs::LoadGuestU32(base, r26.u32 + 40u) : 0u;
  if (!Plausible(cell) || !Plausible(refii::vfs::LoadGuestU32(base, cell))) {
    return false;
  }
  Once(g_said_own_camera, "a joiner's camera runs its own Lua");
  return true;
}

bool RefiiCoopActivateCameraAsJoiner() { return g_activate_as_joiner; }

// PLAYER TWO'S D-PAD FILL LEAVES THE DIG HINT ALONE. `sub_823E51A0`'s third argument also shows
// and dismisses the shared tutorial hints 14 and 15 (`bl sub_821B5260` 0x823E5EE0, `bl
// sub_8244DC88` 0x823E5F18 and 0x823E5F34); the pump fills player two's d-pad through it
// (SamplePlayerTwoHud), and those three calls are skipped while it does, so the hint stays the
// local hero's. Each next instruction reloads what it reads.
bool RefiiCoopDpadQuerySkip() { return refii_bridge_dpad_query_two() != 0; }

void RefiiCoopOwnLiveCamera() {}

void RefiiCoopLodPair(PPCVRegister& v11, PPCVRegister& v12) {
  for (int i = 0; i < 3; ++i) {
    g_lod.a[i] = v12.f32[i + 1];
    g_lod.b[i] = v11.f32[i + 1];
  }
  g_lod.two.store(true, std::memory_order_release);
  Once(g_said_lod, "the world centres on the nearer player");
}

void RefiiCoopLodCamera() { g_lod.two.store(false, std::memory_order_release); }

void RefiiCoopLodNearer(PPCRegister& f1, PPCVRegister& v127) {
  if (!g_lod.two.load(std::memory_order_acquire)) {
    return;
  }
  const float a = DistanceSquared(g_lod.a, v127);
  const float b = DistanceSquared(g_lod.b, v127);
  f1.f64 = static_cast<double>(a < b ? a : b);
}

// ---------------------------------------------------------------------------------------------
// Local split screen: player two's own camera (phase 4, the guest half).
//
//   The Player constructor (sub_82389938, misnamed rex_lhSceneNode_Create) builds a camera
//   manager only for id < 2 or the online joiner (`cmplwi cr6,r4,2; blt` 0x82389974..0x823899a0,
//   then net +0x34/+0x36); the entity setter (sub_82389D60) spawns the camera entities through
//   sub_82481670 under the same gate (0x82389f64). On split screen, the hook after each id test
//   (0x823899A4 → 0x823899E0, 0x82389F6C → 0x82389FA4) lets player two's Player through: the
//   constructor's hook records the Player it built a manager for (r31), and the setter's hook
//   (r27) only takes Players so recorded — its path dereferences Player+4.
//   One manager is ticked per frame (0x821d4a68: sub_821F87C8 "the camera" → sub_821B9258). The
//   update wrapper ticks player two's manager straight after player one's, with the three
//   latch bytes the update shares across managers (byte_83496BE9/BEA/BEB) kept per manager,
//   and marks the manager it is ticking: LuaAPI_ActivateCamera always targets sub_821F87C8's
//   (player one's) manager, so the ActivateCamera wrapper points a call made during player
//   two's tick at player two's manager. Player two's camera wrapper is owned by player two's
//   Player (sub_825AD080, owner id at +36), so the input dispatch (sub_821CCED8) already routes
//   player two's pad to it. Each camera frames only its own player (the core camera chunk).
//   The camera's DOF/post message (sub_82175D50, vtable off_820A645C) is not sent either
//   (`bl sub_82485478` 0x82175EC8 → 0x82175ECC): with each machine's own camera, applying the
//   peer's would overwrite one's own.

extern "C" int RefiiCoopSplitScreen(void);
extern "C" REX_FUNC(__imp__rex_lhCameraManager_Update);

namespace {

/// The Players the constructor built a camera manager for on split screen (player two's; one or
/// two entries over a session). Read only by the entity setter on the same thread.
std::array<uint32_t, 4> g_split_players{};

void RememberSplitPlayer(uint32_t player, bool built) {
  for (uint32_t& slot : g_split_players) {
    if (slot == player) {
      slot = built ? player : 0u;
      return;
    }
  }
  if (built) {
    for (uint32_t& slot : g_split_players) {
      if (slot == 0u) {
        slot = player;
        return;
      }
    }
  }
}

/// The manager player two's tick is running for, while it runs; else 0.
uint32_t g_ticking_manager = 0;

/// Player two's copies of the update's three latch bytes.
std::array<uint8_t, 3> g_second_latches{};

constexpr std::array<uint32_t, 3> kLatches = {0x83496BE9u, 0x83496BEAu, 0x83496BEBu};

/// A player's camera manager: the player map at game+0x9C ({key, Player, _} entries from
/// container+8 to +0xC), Player+4 the holder, holder+4 the manager. 0 when there is none.
uint32_t ManagerForKey(uint8_t* base, uint32_t key) {
  const uint32_t root = refii::vfs::LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
  const uint32_t game = Plausible(root) ? refii::vfs::LoadGuestU32(base, root + 0xCu) : 0u;
  const uint32_t container = Plausible(game) ? refii::vfs::LoadGuestU32(base, game + 0x9Cu) : 0u;
  if (!Plausible(container)) {
    return 0u;
  }
  const uint32_t begin = refii::vfs::LoadGuestU32(base, container + 0x8u);
  const uint32_t end = refii::vfs::LoadGuestU32(base, container + 0xCu);
  if (!Plausible(begin) || end <= begin || end - begin > 0x1000u) {
    return 0u;
  }
  for (uint32_t entry = begin; entry + 12u <= end; entry += 12u) {
    if (refii::vfs::LoadGuestU32(base, entry) != key) {
      continue;
    }
    const uint32_t player = refii::vfs::LoadGuestU32(base, entry + 4u);
    const uint32_t holder = Plausible(player) ? refii::vfs::LoadGuestU32(base, player + 4u) : 0u;
    const uint32_t manager = Plausible(holder) ? refii::vfs::LoadGuestU32(base, holder + 4u) : 0u;
    return Plausible(manager) ? manager : 0u;
  }
  return 0u;
}

std::atomic<bool> g_said_split_camera{false};

}  // namespace

bool RefiiCoopSplitPlayerCamera(PPCRegister& r31) {
  const bool split = RefiiCoopSplitScreen() != 0;
  RememberSplitPlayer(r31.u32, split);
  if (split) {
    Once(g_said_split_camera, "player two gets their own camera (split screen)");
  }
  return split;
}

bool RefiiCoopSplitPlayerCameraEntities(PPCRegister& r27) {
  for (uint32_t slot : g_split_players) {
    if (slot != 0u && slot == r27.u32) {
      return true;
    }
  }
  return false;
}

void RefiiCoopNoCameraPostSend() {}

// A PARTNER'S PAUSE PAUSES NOBODY ELSE (Fable 3: each player roams while the other is in a menu).
// Shipped, the inbound pause message (sub_8232B518, pause byte r1+0x74, UI byte r1+0x75) closes
// the receiver's own GUI (sub_8236ED98) and runs sub_8232B2B0: net+217 = 1, GameFlow mode 1
// (sub_823D4C48; mode 8 for "looking at UI") pushed — the world stops — and the
// GUI_NET_PAUSED_KICK_CLIENT box. Its resume (0x8232C384) acts only while net+217 is set. The
// hook at the pause byte's load (0x8232C2E8, disc 0x8230EDB8) goes to the handler's own exit
// (0x8232C378 `mr r3,r25`, disc 0x8230EE48): neither message changes the receiver.
std::atomic<bool> g_said_partner_pause{false};

bool RefiiCoopPartnerPauseRoams() {
  Once(g_said_partner_pause, "a partner's pause leaves this player roaming");
  return true;
}

// ActivateCamera (r3 camera manager, r4 the Lua camera table). Records a joiner's call, and
// points a call made during player two's tick at player two's manager.
REX_EXTERN(rex_lhCameraManager_ActivateCamera) {
  const bool previous = g_activate_as_joiner;
  g_activate_as_joiner = IsJoiner(base);
  if (g_ticking_manager != 0u) {
    ctx.r3.u32 = g_ticking_manager;
  }
  __imp__rex_lhCameraManager_ActivateCamera(ctx, base);
  g_activate_as_joiner = previous;
}

// The camera manager update (r3 manager). After player one's, player two's on split screen.
REX_EXTERN(rex_lhCameraManager_Update) {
  const uint32_t manager = ctx.r3.u32;
  __imp__rex_lhCameraManager_Update(ctx, base);
  if (g_ticking_manager != 0u || RefiiCoopSplitScreen() == 0) {
    return;
  }
  const uint32_t second = ManagerForKey(base, 2u);
  if (second == 0u || second == manager || manager != ManagerForKey(base, 1u)) {
    return;
  }

  std::array<uint8_t, 3> first_latches{};
  for (std::size_t i = 0; i < kLatches.size(); ++i) {
    const uint32_t at = refii::guest::Address(kLatches[i]);
    first_latches[i] = refii::vfs::LoadGuestU8(base, at);
    refii::vfs::StoreGuestU8(base, at, g_second_latches[i]);
  }

  g_ticking_manager = second;
  PPCContext work = ctx;
  work.r3.u32 = second;
  __imp__rex_lhCameraManager_Update(work, base);
  g_ticking_manager = 0u;

  for (std::size_t i = 0; i < kLatches.size(); ++i) {
    const uint32_t at = refii::guest::Address(kLatches[i]);
    g_second_latches[i] = refii::vfs::LoadGuestU8(base, at);
    refii::vfs::StoreGuestU8(base, at, first_latches[i]);
  }
}

// THE ACTIVE CAMERA FOLLOWS ITS RESPAWNED ENTITY. Player::SetHero (sub_82389D60) respawns the
// manager's camera entities every time a hero is set: sub_82481670 (disc sub_82422EB0) creates
// new "CameraBase" entities (sub_824832D0 → sub_8238B9C8) for PrimaryCamera (manager+36) and
// SecondaryCamera (+52). A camera wrapper already active (+44 primary, built from +36; +60
// secondary, from +52 — sub_82481760) keeps a handle to the old entity at wrapper+8, zeroed once
// it is resolved dead (sub_82193E38), and its Lua instance cached that entity in Activate
// (CameraBase:Activate's `camera`, passed by sub_825AD7E8 from wrapper+8). Re-activating the same
// camera only restarts that wrapper (0x824818D4/0x824818E4), so the camera never comes back: the
// joiner's hero is set again after its camera starts, and its view sat at the default pose. In a
// co-op session, a wrapper whose entity is not the manager's live one gets the game's own
// re-activation, with the handle rebound first: stop (sub_825ADAD0), assign (sub_82214898), start
// (sub_825AD7E8 with no blend, as sub_82481760 restarts). A wrapper with no Lua camera (+20) is the
// joiner branch's placeholder and is left alone.
extern "C" REX_FUNC(__imp__rex_lhCameraManager_SpawnCameraEntities);

namespace {
std::atomic<bool> g_said_camera_rebound{false};
}  // namespace

REX_EXTERN(rex_lhCameraManager_SpawnCameraEntities) {
  using refii::vfs::LoadGuestU32;
  const uint32_t manager = ctx.r3.u32;
  const PPCContext entry = ctx;
  __imp__rex_lhCameraManager_SpawnCameraEntities(ctx, base);

  const uint32_t root = LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
  const uint32_t game = Plausible(root) ? LoadGuestU32(base, root + 0xCu) : 0u;
  const uint32_t net = Plausible(game) ? LoadGuestU32(base, game + 0x8Cu) : 0u;
  const bool session = Plausible(net) && refii::vfs::LoadGuestU8(base, net + 0x34u) != 0u;
  if (!Plausible(manager) || (!session && RefiiCoopSplitScreen() == 0)) {
    return;
  }

  static PPCFunc* const stop = refii::vfs::FindGuestFunction(refii::guest::Address(0x825ADAD0u));
  static PPCFunc* const assign = refii::vfs::FindGuestFunction(refii::guest::Address(0x82214898u));
  static PPCFunc* const start = refii::vfs::FindGuestFunction(refii::guest::Address(0x825AD7E8u));
  if (stop == nullptr || assign == nullptr || start == nullptr) {
    return;
  }

  struct Slot {
    uint32_t wrapper;
    uint32_t handle;
  };
  for (const Slot slot : {Slot{44u, 36u}, Slot{60u, 52u}}) {
    const uint32_t wrapper = LoadGuestU32(base, manager + slot.wrapper);
    const uint32_t cell = LoadGuestU32(base, manager + slot.handle + 4u);
    const uint32_t entity = Plausible(cell) ? LoadGuestU32(base, cell) : 0u;
    if (!Plausible(wrapper) || !Plausible(entity) || LoadGuestU32(base, wrapper + 20u) == 0u) {
      continue;
    }
    const uint32_t bound = LoadGuestU32(base, wrapper + 12u);
    if (bound == cell) {
      continue;
    }
    PPCContext call = entry;
    call.r3.u32 = wrapper;
    stop(call, base);
    call = entry;
    call.r3.u32 = wrapper + 8u;
    call.r4.u32 = entity;
    assign(call, base);
    call = entry;
    call.r3.u32 = wrapper;
    call.r4.u64 = 0u;
    call.r5.u64 = 0u;
    start(call, base);
    Once(g_said_camera_rebound, "an active camera follows its respawned camera entity");
  }
}

// ---------------------------------------------------------------------------------------------
// FREE ROAM: the host simulates around BOTH players (Fable 3's peer view).
//
//   Fable 2's host measures what it simulates and sends from its OWN camera and hero only; Fable 3
//   sends each machine's camera (0x17B) and the host ORs the peer's view into the same tests
//   (`sub_821B4C38`, `sub_82205D10`, `sub_821FE8D8`; generators `sub_82A003A0` / `sub_82A00548`).
//   Here, with the guest's own code everywhere:
//   - the joiner sends its camera (0xBD, `sub_821B8FD8`); the host stores the decoded block
//     (r1+0x860 at 0x82332604 / disc 0x82314FDC: pos +0, up +0x10, fwd +0x20, hfov +0x30) and
//     never applies it. On local split screen the peer view is player two's own manager (+0x90);
//   - "the camera" for a peer query is that block: the camera-block getter `sub_82211598` answers
//     with it while t_peer_view is set, so the guest's own cone builder (`sub_82294468`), LOD tier
//     (`sub_822A1710`) and animation LOD (`sub_82211DF8`) run for the peer view unchanged;
//   - visibility refresh `sub_822141A8(comp, 0)` (distance² +104, in view +203; read by the transform
//     sender `sub_822BCA68`, the 0xAF sender `sub_8239CC80` and generator despawn `sub_827BDA20`)
//     runs again through its own cone path for the peer cone: nearer distance, in either view;
//   - creature LOD tier: the finer of the two; animation LOD: within either view's range;
//   - creature generators: spawn when EITHER hero is inside the create radius (+64), despawn only
//     when BOTH are beyond the destroy radius (+68), as `sub_82A003A0` / `sub_82A00548`.

extern "C" REX_FUNC(__imp__rex_lhCamera_CurrentBlock);
extern "C" REX_FUNC(__imp__rex_lhVisibility_Refresh);
extern "C" REX_FUNC(__imp__rex_lhCreature_LodTier);
extern "C" REX_FUNC(__imp__rex_lhAnim_WithinLodDistance);
extern "C" REX_FUNC(__imp__rex_CECCreatureGenerator_InCreateRange);
extern "C" REX_FUNC(__imp__rex_CECCreatureGenerator_OutOfDestroyRange);

namespace {

constexpr uint32_t kCameraBlockBytes = 64u;
constexpr uint32_t kPeerConeOffset = 64u;  // the cone (48 bytes) follows the block

/// The peer camera block as received (guest byte order), and whether one arrived this session.
std::mutex g_peer_mutex;
std::array<uint8_t, kCameraBlockBytes> g_peer_received{};
bool g_peer_have = false;

/// 16-aligned guest buffer: the block the getter hands out, then the cone built from it.
uint32_t g_peer_guest = 0;
uint32_t g_peer_built_frame = 0xFFFFFFFFu;

/// Set only around the guest calls that ask for the peer view.
thread_local bool t_peer_view = false;

std::atomic<bool> g_said_peer_view{false};
std::atomic<bool> g_said_generators{false};

float LoadF32(uint8_t* base, uint32_t at) {
  const uint32_t bits = refii::vfs::LoadGuestU32(base, at);
  float value;
  std::memcpy(&value, &bits, sizeof(value));
  return value;
}

void StoreF32(uint8_t* base, uint32_t at, float value) {
  uint32_t bits;
  std::memcpy(&bits, &value, sizeof(bits));
  refii::vfs::StoreGuestU32(base, at, bits);
}

uint32_t NetManager(uint8_t* base) {
  const uint32_t root = refii::vfs::LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
  const uint32_t game = Plausible(root) ? refii::vfs::LoadGuestU32(base, root + 0xCu) : 0u;
  return Plausible(game) ? refii::vfs::LoadGuestU32(base, game + 0x8Cu) : 0u;
}

/// The session host (net +0x34 in a session, +0x35 the host). Clears the peer view once the
/// session is gone, so the next session starts without one.
bool IsSessionHost(uint8_t* base) {
  const uint32_t net = NetManager(base);
  const bool session = Plausible(net) && refii::vfs::LoadGuestU8(base, net + 0x34u) != 0u;
  if (!session) {
    std::lock_guard<std::mutex> lock(g_peer_mutex);
    g_peer_have = false;
  }
  return session && refii::vfs::LoadGuestU8(base, net + 0x35u) != 0u;
}

/// The peer view's cone, built this frame, or 0 when there is no peer view.
uint32_t PeerCone(const PPCContext& ctx, uint8_t* base) {
  uint32_t split_manager = 0u;
  if (RefiiCoopSplitScreen() != 0) {
    split_manager = ManagerForKey(base, 2u);
  }
  bool online = false;
  if (split_manager == 0u && IsSessionHost(base)) {
    std::lock_guard<std::mutex> lock(g_peer_mutex);
    online = g_peer_have;
  }
  if (split_manager == 0u && !online) {
    return 0u;
  }

  if (g_peer_guest == 0u) {
    static PPCFunc* const alloc = refii::vfs::FindGuestFunction(refii::guest::Address(0x8221F258u));
    if (alloc == nullptr) {
      return 0u;
    }
    PPCContext call = ctx;
    call.r3.u64 = kCameraBlockBytes + 48u + 16u;
    alloc(call, base);
    if (!Plausible(call.r3.u32)) {
      return 0u;
    }
    g_peer_guest = (call.r3.u32 + 15u) & ~15u;
  }

  const uint32_t frame = refii::vfs::LoadGuestU32(base, refii::guest::Address(0x83319650u));
  if (frame != g_peer_built_frame) {
    static PPCFunc* const build_cone = refii::vfs::FindGuestFunction(refii::guest::Address(0x82294468u));
    if (build_cone == nullptr) {
      return 0u;
    }
    if (split_manager != 0u) {
      std::memmove(base + g_peer_guest, base + split_manager + 0x90u, kCameraBlockBytes);
    } else {
      std::lock_guard<std::mutex> lock(g_peer_mutex);
      std::memcpy(base + g_peer_guest, g_peer_received.data(), kCameraBlockBytes);
    }
    PPCContext call = ctx;
    call.r3.u32 = g_peer_guest + kPeerConeOffset;
    t_peer_view = true;
    build_cone(call, base);
    t_peer_view = false;
    g_peer_built_frame = frame;
    Once(g_said_peer_view, "the host simulates around both players (the peer's camera view)");
  }
  return g_peer_guest + kPeerConeOffset;
}

/// Squared distance from player two's hero to `entity`, or -1 when either has no position.
float PlayerTwoDistanceSquared(const PPCContext& ctx, uint8_t* base, uint32_t entity) {
  const uint32_t two = refii_mod_player_hero(base, 2);
  if (!Plausible(two) || (refii::vfs::LoadGuestU8(base, two + 144u) & 0x40u) == 0u || !Plausible(entity)) {
    return -1.0f;
  }
  float a[3];
  float b[3];
  PPCContext work = ctx;
  if (refii_mod_entity_position(&work, base, two, a) == 0) {
    return -1.0f;
  }
  work = ctx;
  if (refii_mod_entity_position(&work, base, entity, b) == 0) {
    return -1.0f;
  }
  const float dx = a[0] - b[0];
  const float dy = a[1] - b[1];
  const float dz = a[2] - b[2];
  return dx * dx + dy * dy + dz * dz;
}

}  // namespace

/// Only the online joiner sends its camera (true skips the send).
bool RefiiCoopNoCameraSend() {
  uint8_t* base = RefiiGuestBase();
  return base == nullptr || !IsJoiner(base);
}

/// A received 0xBD: kept as the peer view, never applied.
void RefiiCoopNoCameraReceive(PPCRegister& r1) {
  uint8_t* base = RefiiGuestBase();
  if (base == nullptr) {
    return;
  }
  std::lock_guard<std::mutex> lock(g_peer_mutex);
  std::memcpy(g_peer_received.data(), base + r1.u32 + 0x860u, kCameraBlockBytes);
  g_peer_have = true;
}

// PLAYER TWO'S SCREEN. "On screen" for the guest is player one's camera: `sub_821DD3B0(out,
// manager)` copies the manager's block (`sub_82265F90`) and the screen tests, the dog's
// off-screen teleport (`sub_82811A70`) and IsEntity/PositionVisibleOnScreen take it from
// `sub_821F87C8`'s manager. While code runs for player two (their dog), the block is player
// two's view — the same block the host keeps for the free-roam peer view (split screen: player
// two's manager; online: the joiner's streamed camera). Without one, the shipped block.
namespace {
thread_local int t_partner_screen = 0;
}  // namespace

extern "C" void RefiiCoopPartnerScreen(int on) { t_partner_screen += on != 0 ? 1 : -1; }

extern "C" REX_FUNC(__imp__rex_lhCamera_ScreenBlock);

REX_EXTERN(rex_lhCamera_ScreenBlock) {
  if (t_partner_screen > 0 && PeerCone(ctx, base) != 0u && g_peer_guest != 0u) {
    std::memmove(base + ctx.r3.u32, base + g_peer_guest, kCameraBlockBytes);
    return;
  }
  __imp__rex_lhCamera_ScreenBlock(ctx, base);
}

REX_EXTERN(rex_lhCamera_CurrentBlock) {
  if (t_peer_view && g_peer_guest != 0u) {
    ctx.r3.u64 = g_peer_guest;
    return;
  }
  __imp__rex_lhCamera_CurrentBlock(ctx, base);
}

REX_EXTERN(rex_lhVisibility_Refresh) {
  using refii::vfs::LoadGuestU32;
  using refii::vfs::StoreGuestU32;
  const PPCContext entry = ctx;
  const uint32_t comp = ctx.r3.u32;
  const uint32_t view = ctx.r4.u32;
  __imp__rex_lhVisibility_Refresh(ctx, base);
  if (view != 0u || !Plausible(comp)) {
    return;
  }
  const uint32_t frame = LoadGuestU32(base, refii::guest::Address(0x83319650u));
  if (LoadGuestU32(base, comp + 232u) != frame) {
    return;  // not refreshed this frame: the stored answer already holds both views
  }
  const uint32_t cone = PeerCone(entry, base);
  if (cone == 0u) {
    return;
  }
  const float local_distance = LoadF32(base, comp + 104u);
  const uint8_t local_visible = refii::vfs::LoadGuestU8(base, comp + 203u);
  StoreGuestU32(base, comp + 100u, frame - 1u);  // the refresh runs once per frame...
  StoreGuestU32(base, comp + 232u, frame - 6u);  // ...and staggers unless 5 frames passed
  PPCContext peer = entry;
  peer.r4.u32 = cone;
  __imp__rex_lhVisibility_Refresh(peer, base);
  const float peer_distance = LoadF32(base, comp + 104u);
  const uint8_t peer_visible = refii::vfs::LoadGuestU8(base, comp + 203u);
  StoreF32(base, comp + 104u, peer_distance < local_distance ? peer_distance : local_distance);
  refii::vfs::StoreGuestU8(base, comp + 203u, (local_visible != 0u || peer_visible != 0u) ? 1u : 0u);
  StoreGuestU32(base, comp + 100u, frame);
  StoreGuestU32(base, comp + 232u, frame);
}

REX_EXTERN(rex_lhCreature_LodTier) {
  const PPCContext entry = ctx;
  __imp__rex_lhCreature_LodTier(ctx, base);
  if (ctx.r3.u32 == 0u || PeerCone(entry, base) == 0u) {
    return;
  }
  PPCContext peer = entry;
  t_peer_view = true;
  __imp__rex_lhCreature_LodTier(peer, base);
  t_peer_view = false;
  if (peer.r3.u32 < ctx.r3.u32) {
    ctx.r3.u64 = peer.r3.u32;
  }
}

REX_EXTERN(rex_lhAnim_WithinLodDistance) {
  const PPCContext entry = ctx;
  __imp__rex_lhAnim_WithinLodDistance(ctx, base);
  if ((ctx.r3.u32 & 0xFFu) != 0u || PeerCone(entry, base) == 0u) {
    return;
  }
  PPCContext peer = entry;
  t_peer_view = true;
  __imp__rex_lhAnim_WithinLodDistance(peer, base);
  t_peer_view = false;
  if ((peer.r3.u32 & 0xFFu) != 0u) {
    ctx.r3.u64 = 1u;
  }
}

REX_EXTERN(rex_CECCreatureGenerator_InCreateRange) {
  const PPCContext entry = ctx;
  const uint32_t generator = ctx.r3.u32;
  __imp__rex_CECCreatureGenerator_InCreateRange(ctx, base);
  if ((ctx.r3.u32 & 0xFFu) != 0u || !Plausible(generator) ||
      refii::vfs::LoadGuestU8(base, generator + 63u) == 0u) {
    return;
  }
  const float d2 = PlayerTwoDistanceSquared(entry, base, refii::vfs::LoadGuestU32(base, generator + 4u));
  const float radius = LoadF32(base, generator + 64u);
  if (d2 >= 0.0f && d2 < radius * radius) {
    ctx.r3.u64 = 1u;
    Once(g_said_generators, "creature generators answer either player");
  }
}

REX_EXTERN(rex_CECCreatureGenerator_OutOfDestroyRange) {
  const PPCContext entry = ctx;
  const uint32_t generator = ctx.r3.u32;
  __imp__rex_CECCreatureGenerator_OutOfDestroyRange(ctx, base);
  if ((ctx.r3.u32 & 0xFFu) == 0u || !Plausible(generator)) {
    return;
  }
  const float d2 = PlayerTwoDistanceSquared(entry, base, refii::vfs::LoadGuestU32(base, generator + 4u));
  const float radius = LoadF32(base, generator + 68u);
  if (d2 >= 0.0f && d2 <= radius * radius) {
    ctx.r3.u64 = 0u;
  }
}

// ---------------------------------------------------------------------------------------------
// TRAVEL: a region exit waits for both players (Fable 3's partner-range rule).
//
//   The exit update (sub_821ED378 / disc sub_821ED1E8) returns at once on an online joiner
//   (net +0x34 && +0x36, 0x821ED39C..0x821ED3C8) and otherwise tests only the HERO
//   (game+0x58 → players → sub_8222C1B8, 0x821ED418..0x821ED428) against Radius (exit+0x24,
//   squared, 0x821ED498..0x821ED4C4); the level change then carries player two from wherever
//   they are, which free roam makes anywhere. Fable 3's exit (sub_829BD0A8) refuses while the
//   partner is out of range — a walk-in exit's partner must be inside its radius, an interaction
//   exit's within 30 (x and y, flt_8329E3D8) of the one who entered — and tells the one who
//   entered TEXT_TUTORIAL_EXIT_LEVEL_BLOCKED_BY_PLAYER_DISTANCE once per approach (latch +0x68).
//   The wrapper decides for its call; the hook before `clrlwi r8,r11,24` (0x821ED4D4 /
//   0x821ED344, r11 = the hero is inside Radius) clears r11 on a refusal, which leaves the
//   message-radius banner and the leave/re-enter test (both from the same f13) as shipped.
//   Player two walking into a walk-in exit alone is told on the machine they play on; the exit
//   fires only for the hero, so nothing else changes. Fable 2 ships no text for this: the host
//   shows its own notice (refii_bridge_note_exit_refused).

extern "C" REX_FUNC(__imp__rex_CECLevelExit_Update);
extern "C" REX_FUNC(__imp__rex_CECLevelExit_Fire);
extern "C" int RefiiCoopPeekExitUse(uint8_t* base, float* out_xyz);
extern "C" void RefiiCoopClearExitUse(void);

namespace {

constexpr float kInteractPartnerRange = 30.0f;  // Fable 3's flt_8329E3D8

std::atomic<bool> g_said_exit{false};

/// Set by the exit-update wrapper for its call: this exit waits for player two.
bool g_exit_refused = false;

/// Set by the exit-update wrapper for its call: the player the exit tests when not the hero.
uint32_t g_exit_tester = 0u;

/// Set by the exit-update wrapper for its call: an online joiner runs this interaction exit (its
/// prompt, the hold, the partner rule) for its own player; the fire becomes a request.
bool g_joiner_runs_exit = false;

std::atomic<bool> g_said_joiner_exit{false};

/// The frame an interaction exit last raised its prompt for player two (their hold is its own).
uint32_t g_exit_prompt_two_frame = 0xFFFFFFF0u;

/// Exits a player stands refused at, so each approach is told once. Cleared when they leave.
struct ExitLatch {
  uint32_t exit = 0u;
  uint8_t told = 0u;  // bit 0 player one, bit 1 player two
};
std::array<ExitLatch, 16> g_exit_latches{};

ExitLatch& LatchFor(uint32_t exit) {
  ExitLatch* empty = nullptr;
  for (ExitLatch& latch : g_exit_latches) {
    if (latch.exit == exit) {
      return latch;
    }
    if (empty == nullptr && latch.told == 0u) {
      empty = &latch;
    }
  }
  ExitLatch& slot = empty != nullptr ? *empty : g_exit_latches[0];
  slot = ExitLatch{exit, 0u};
  return slot;
}

/// Tells `player` once per approach; `standing` false ends the approach.
void Tell(uint32_t exit, int32_t player, bool standing, bool local) {
  const uint8_t bit = player == 1 ? 1u : 2u;
  ExitLatch& latch = LatchFor(exit);
  if (!standing) {
    latch.told &= static_cast<uint8_t>(~bit);
    return;
  }
  if ((latch.told & bit) == 0u) {
    latch.told |= bit;
    if (local) {
      refii_bridge_note_exit_refused(player);
    }
  }
}

/// Whether this machine's player stood waiting at any exit this frame, published when the next
/// frame begins (refii_bridge_set_exit_waiting).
struct Waiting {
  uint32_t frame = 0xFFFFFFFFu;
  bool now = false;
};
Waiting g_waiting;

void WaitingFrame(uint8_t* base) {
  const uint32_t frame = refii::vfs::LoadGuestU32(base, refii::guest::Address(0x83319650u));
  if (frame != g_waiting.frame) {
    refii_bridge_set_exit_waiting(g_waiting.now ? 1 : 0);
    g_waiting.frame = frame;
    g_waiting.now = false;
  }
}

void NoteWaiting(uint8_t* base) {
  WaitingFrame(base);
  g_waiting.now = true;
}

float DistanceSquared3(const float* a, const float* b) {
  const float dx = a[0] - b[0];
  const float dy = a[1] - b[1];
  const float dz = a[2] - b[2];
  return dx * dx + dy * dy + dz * dz;
}

/// Whether this exit waits for player two this tick, and the notices that go with it.
bool ExitRefused(PPCContext& ctx, uint8_t* base, uint32_t exit) {
  const uint32_t root = refii::vfs::LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
  const uint32_t game = Plausible(root) ? refii::vfs::LoadGuestU32(base, root + 0xCu) : 0u;
  const uint32_t mode = Plausible(game) ? refii::vfs::LoadGuestU32(base, game + 0x11Cu) : 0u;
  if (mode == 0u || !Plausible(exit)) {
    return false;  // solo
  }
  const uint32_t hero = refii_mod_player_hero(base, 1);
  const uint32_t partner = refii_mod_player_hero(base, 2);
  const uint32_t entity = refii::vfs::LoadGuestU32(base, exit + 4u);
  if (!Plausible(hero) || !Plausible(partner) || !Plausible(entity) ||
      refii::vfs::LoadGuestU8(base, exit + 0x37u) == 0u) {
    return false;  // no partner, or the exit is inactive
  }

  float at[3], one[3], two[3];
  if (refii_mod_entity_position(&ctx, base, entity, at) == 0 ||
      refii_mod_entity_position(&ctx, base, hero, one) == 0 ||
      refii_mod_entity_position(&ctx, base, partner, two) == 0) {
    return false;
  }
  const uint32_t radius_bits = refii::vfs::LoadGuestU32(base, exit + 0x24u);
  float radius = 0.0f;
  std::memcpy(&radius, &radius_bits, sizeof(radius));
  const float reach = radius * radius;
  const bool interaction = refii::vfs::LoadGuestU8(base, exit + 0x3Bu) != 0u ||
                           refii::vfs::LoadGuestU8(base, exit + 0x3Cu) != 0u;

  const bool hero_in = DistanceSquared3(one, at) < reach;
  const bool partner_in = DistanceSquared3(two, at) < reach;

  // An interaction exit or dive spot answers the NEARER player in local co-op (Fable 3: either
  // player takes an exit): the update tests that player (RefiiCoopExitTester), raises its hold
  // prompt for them, and a dive queues on their own action manager (component 49, read off the
  // tested player at 0x821EDD20). A walk-in exit tests the hero as shipped; both must stand in it.
  const bool dive = refii::vfs::LoadGuestU8(base, exit + 0x3Bu) != 0u;
  const bool joiner = IsJoiner(base);
  const bool two_can = !dive || refii_mod_component(base, partner, 49u) != 0u;
  // An online joiner's own player is player two; their machine runs the exit for them.
  g_joiner_runs_exit = joiner && mode == 1u && interaction && two_can;
  const bool two_tests = g_joiner_runs_exit ||
                         (interaction && mode == 2u && two_can &&
                          DistanceSquared3(two, at) < DistanceSquared3(one, at));
  const float* tester = two_tests ? two : one;
  const float* other = two_tests ? one : two;
  const bool tester_in = two_tests ? partner_in : hero_in;
  const float dx = other[0] - tester[0];
  const float dy = other[1] - tester[1];
  const bool partner_ok = interaction
                              ? dx * dx + dy * dy < kInteractPartnerRange * kInteractPartnerRange
                              : partner_in;

  // Player one is this machine's player off the joiner; player two is the joiner's, or local
  // player two in local co-op (mode 2).
  const bool refused = tester_in && !partner_ok;
  const bool partner_alone = !interaction && partner_in && !hero_in;
  g_exit_tester = two_tests ? partner : 0u;
  if (two_tests && tester_in && !refused) {
    g_exit_prompt_two_frame = refii::vfs::LoadGuestU32(base, refii::guest::Address(0x83319650u));
  }
  Tell(exit, two_tests ? 2 : 1, refused, two_tests ? true : !joiner);
  Tell(exit, two_tests ? 1 : 2, partner_alone, joiner || mode == 2u);
  // Online, this machine's player waiting at an exit is restated to the other machine (Fable 3's
  // net message 0x141 and its _REMOTE text): the host's player is one, the joiner's two.
  if (mode == 1u && (joiner ? partner_alone || (g_joiner_runs_exit && refused) : refused)) {
    NoteWaiting(base);
  }
  if (refused) {
    Once(g_said_exit, "a region exit waits for both players");
  }
  return refused;
}

/// The host's half of the joiner's request: the exit standing within a metre of the requested
/// position is marked interacted (+0x3D, what its Lua `Interact` sets), so this update fires it
/// at its top (0x821ED3EC). The joiner already applied the partner rule, as Fable 3's host trusts
/// its client's (`sub_829BF5F8` re-checks only the region lock, which the update keeps).
void FireRequestedExit(PPCContext& ctx, uint8_t* base, uint32_t exit) {
  float wanted[3];
  if (!Plausible(exit) || RefiiCoopPeekExitUse(base, wanted) == 0) {
    return;
  }
  const uint32_t entity = refii::vfs::LoadGuestU32(base, exit + 4u);
  float at[3];
  if (!Plausible(entity) || refii_mod_entity_position(&ctx, base, entity, at) == 0 ||
      DistanceSquared3(at, wanted) > 1.0f) {
    return;
  }
  refii::vfs::StoreGuestU8(base, exit + 0x3Du, 1u);
  RefiiCoopClearExitUse();
  REXLOG_INFO("Refii co-op heroes: the joiner's exit at ({:.1f}, {:.1f}, {:.1f}) fires on the host",
              at[0], at[1], at[2]);
}

}  // namespace

void RefiiCoopJoinerRunsExit(PPCRegister& r11) {
  if (g_joiner_runs_exit) {
    r11.u64 = 0u;
  }
}

void RefiiCoopExitTester(PPCRegister& r3) {
  if (g_exit_tester != 0u) {
    r3.u64 = g_exit_tester;
  }
}

void RefiiCoopExitWaitsForPartner(PPCRegister& r11) {
  if (g_exit_refused) {
    r11.u64 = 0u;
  }
}

// The level exit's update (r3 the CECLevelExit component).
REX_EXTERN(rex_CECLevelExit_Update) {
  WaitingFrame(base);
  const uint32_t exit = ctx.r3.u32;
  const bool previous = g_exit_refused;
  const uint32_t previous_tester = g_exit_tester;
  const bool previous_joiner = g_joiner_runs_exit;
  g_exit_tester = 0u;
  g_joiner_runs_exit = false;
  g_exit_refused = ExitRefused(ctx, base, exit);
  if (!IsJoiner(base)) {
    FireRequestedExit(ctx, base, exit);
  }
  __imp__rex_CECLevelExit_Update(ctx, base);
  g_exit_refused = previous;
  g_exit_tester = previous_tester;
  g_joiner_runs_exit = previous_joiner;
}

// The level exit's fire (r3 the CECLevelExit component). On an online joiner it is a request:
// Fable 2 never fires an exit there (the update returns at its joiner test), and Fable 3's client
// sends net message 0x145 for the host to fire. Here the exit's position goes to the host on
// our channel (refii_bridge_set_exit_request) and the host's update fires the exit standing there.
REX_EXTERN(rex_CECLevelExit_Fire) {
  const uint32_t exit = ctx.r3.u32;
  if (!IsJoiner(base)) {
    __imp__rex_CECLevelExit_Fire(ctx, base);
    return;
  }
  const uint32_t entity = Plausible(exit) ? refii::vfs::LoadGuestU32(base, exit + 4u) : 0u;
  float at[3];
  if (Plausible(entity) && refii_mod_entity_position(&ctx, base, entity, at) != 0) {
    refii_bridge_set_exit_request(at[0], at[1], at[2]);
    Once(g_said_joiner_exit, "the joiner's exit use goes to the host");
  }
}

// ---------------------------------------------------------------------------------------------
// INTERACTION: the world's use prompts and hold-to-use answer player two too.
//
//   Pressing A on a thing is per player already: CAbilityActionPlayerInteract (sub_82975BD0 /
//   sub_82976588) takes the acting creature's OWN targeting (component 77) and runs the use for
//   it. Three single-player ties stand around it:
//   - PROMPT: a usable thing's update (CECOnActionUse, sub_82821E88 / disc sub_82753908) shows
//     its world icon only from the HERO (`bl sub_8222C1B8; mr r31,r3` at 0x8282251C/0x82822520 /
//     0x82753F9C/0x82753FA0; r31 then runs the player's checks and the WorldIconRadius test at
//     0x828226B0). The hook before that `mr` hands it the NEARER player, so a thing by player two
//     prompts for player two. Player two is taken only when they carry component 33, the
//     player's checks' own (0x82822550..0x8282267C); without it the hero stands.
//   - HOLD INPUT: the prompt manager's A handler (sub_823DD318 / disc sub_823B6A00) counts only
//     player record 1's pad (r31, 0x823DD3A8..0x823DD3B8) and SWALLOWS every other A while a
//     hold prompt is up — player two could not swing a sword while player one stood at a chest.
//     The hook before `li r11,1` (0x823DD42C / 0x823B6B14) takes player two's press as the hold's
//     when player two's own target is a hold-to-use thing (its component 78 byte +0x1A, what the
//     completion below requires), and otherwise returns 0 (0x823DD338 / 0x823B6A20): not consumed,
//     so the press reaches player two's own controls.
//   - HOLD USE: a full hold (sub_821C5BB0 / disc sub_821C5C20) uses the HERO's target as the hero
//     (`mr r31,r3` at 0x821C6018 / 0x821C6088). The hook there hands it the player who held, so
//     the use is their target's and runs for them. Every step after is null-safe.

extern "C" REX_FUNC(__imp__rex_CECOnActionUse_Update);
extern "C" REX_FUNC(__imp__rex_lhPromptManager_HoldInput);
extern "C" REX_FUNC(__imp__rex_lhPromptManager_Update);

namespace {

std::atomic<bool> g_said_prompt{false};
std::atomic<bool> g_said_hold{false};

/// The player a usable thing's update tests, for its call (0: the hero, as shipped).
uint32_t g_use_viewer = 0u;

/// For the hold handler's call: player two's own target is a hold-to-use thing.
bool g_two_may_hold = false;

/// Who holds A on the hold prompt now: 1 or 2.
int32_t g_hold_player = 1;

/// For the prompt manager's update: the creature a completed hold uses as (0: the hero).
uint32_t g_hold_creature = 0u;

/// Both players' positions, once a frame (the frame counter the use update itself reads).
struct PlayerSpots {
  uint32_t frame = 0xFFFFFFFFu;
  uint32_t one = 0u, two = 0u;
  float a[3] = {}, b[3] = {};
  bool valid = false;
};
PlayerSpots g_spots;

bool CoopGame(uint8_t* base) {
  const uint32_t root = refii::vfs::LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
  const uint32_t game = Plausible(root) ? refii::vfs::LoadGuestU32(base, root + 0xCu) : 0u;
  return Plausible(game) && refii::vfs::LoadGuestU32(base, game + 0x11Cu) != 0u;
}

const PlayerSpots& Spots(PPCContext& ctx, uint8_t* base) {
  const uint32_t frame = refii::vfs::LoadGuestU32(base, refii::guest::Address(0x83319650u));
  if (frame != g_spots.frame) {
    g_spots.frame = frame;
    g_spots.one = refii_mod_player_hero(base, 1);
    g_spots.two = refii_mod_player_hero(base, 2);
    g_spots.valid = Plausible(g_spots.one) && Plausible(g_spots.two) &&
                    refii_mod_entity_position(&ctx, base, g_spots.one, g_spots.a) != 0 &&
                    refii_mod_entity_position(&ctx, base, g_spots.two, g_spots.b) != 0;
  }
  return g_spots;
}

/// The player a usable thing prompts for, when it is not the hero: local co-op, player two when
/// they stand nearer `thing`; an online joiner, always player two (this machine's player — the
/// shipped joiner was prompted only near the host's hero); an online host, the hero as shipped.
/// Player two only when they can run the player's checks (component 33).
uint32_t NearerPlayerTwo(PPCContext& ctx, uint8_t* base, uint32_t thing) {
  if (!Plausible(thing) || !CoopGame(base)) {
    return 0u;
  }
  const uint32_t root = refii::vfs::LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
  const uint32_t game = Plausible(root) ? refii::vfs::LoadGuestU32(base, root + 0xCu) : 0u;
  const uint32_t mode = Plausible(game) ? refii::vfs::LoadGuestU32(base, game + 0x11Cu) : 0u;
  if (mode == 1u) {
    const uint32_t two = IsJoiner(base) ? refii_mod_player_hero(base, 2) : 0u;
    return Plausible(two) && refii_mod_component(base, two, 33u) != 0u ? two : 0u;
  }
  const PlayerSpots& spots = Spots(ctx, base);
  float at[3];
  if (!spots.valid || refii_mod_component(base, spots.two, 33u) == 0u ||
      refii_mod_entity_position(&ctx, base, thing, at) == 0) {
    return 0u;
  }
  return DistanceSquared3(spots.b, at) < DistanceSquared3(spots.a, at) ? spots.two : 0u;
}

/// Whether player two's own targeting (component 77, vfunc +0x44) holds a live thing whose
/// use is hold-to-use (component 78 byte +0x1A) — what a full hold's use requires.
bool PlayerTwoTargetsHoldUse(PPCContext& ctx, uint8_t* base) {
  const uint32_t two = refii_mod_player_hero(base, 2);
  const uint32_t targeting = Plausible(two) ? refii_mod_component(base, two, 77u) : 0u;
  if (targeting == 0u) {
    return false;
  }
  PPCContext work = refii::mods::Frame(ctx);
  if (!refii::mods::CallVirtual(work, base, targeting, 0x44u / 4u)) {
    return false;
  }
  const uint32_t target = work.r3.u32;
  if (!Plausible(target) || (refii::vfs::LoadGuestU8(base, target + 0x90u) & 0x40u) == 0u) {
    return false;
  }
  const uint32_t use = refii_mod_component(base, target, 78u);
  return use != 0u && refii::vfs::LoadGuestU8(base, use + 0x1Au) != 0u;
}

}  // namespace

void RefiiCoopPromptForNearerPlayer(PPCRegister& r3) {
  if (g_use_viewer != 0u) {
    r3.u64 = g_use_viewer;
    Once(g_said_prompt, "a use prompt answers the nearer player");
  }
}

bool RefiiCoopHoldByEitherPlayer(PPCRegister& r31) {
  if ((r31.u32 & 0xFFu) != 0u) {
    g_hold_player = 1;
    return false;
  }
  if (g_two_may_hold) {
    r31.u64 = 1u;
    g_hold_player = 2;
    Once(g_said_hold, "player two holds a hold-to-use prompt");
    return false;
  }
  return true;  // not the hold's: leave the press to player two's controls
}

void RefiiCoopHoldUsesHolder(PPCRegister& r3) {
  if (g_hold_creature != 0u) {
    r3.u64 = g_hold_creature;
  }
}

namespace {

/// The things answering player two, gathered over a frame and published when the next begins
/// (refii_bridge_set_prompts_two): the host seats their prompt icons in player two's half.
struct PromptsTwo {
  uint32_t frame = 0xFFFFFFFFu;
  std::array<uint32_t, 64> things{};
  int32_t count = 0;
};
PromptsTwo g_prompts_two;

void NotePromptTwo(uint8_t* base, uint32_t thing) {
  const uint32_t frame = refii::vfs::LoadGuestU32(base, refii::guest::Address(0x83319650u));
  if (frame != g_prompts_two.frame) {
    refii_bridge_set_prompts_two(g_prompts_two.things.data(), g_prompts_two.count);
    g_prompts_two.frame = frame;
    g_prompts_two.count = 0;
  }
  if (thing != 0u && g_prompts_two.count < static_cast<int32_t>(g_prompts_two.things.size())) {
    g_prompts_two.things[static_cast<std::size_t>(g_prompts_two.count++)] = thing;
  }
}

}  // namespace

// A usable thing's update (r3 its CECOnActionUse component; the thing at +4).
REX_EXTERN(rex_CECOnActionUse_Update) {
  const uint32_t previous = g_use_viewer;
  const uint32_t use = ctx.r3.u32;
  const uint32_t thing = Plausible(use) ? refii::vfs::LoadGuestU32(base, use + 4u) : 0u;
  g_use_viewer = NearerPlayerTwo(ctx, base, thing);
  NotePromptTwo(base, g_use_viewer != 0u ? thing : 0u);
  __imp__rex_CECOnActionUse_Update(ctx, base);
  g_use_viewer = previous;
}

// The prompt manager's A handler (r3 manager, r4 the input event: +8 button, +44 kind).
REX_EXTERN(rex_lhPromptManager_HoldInput) {
  const bool previous = g_two_may_hold;
  const uint32_t event = ctx.r4.u32;
  const uint32_t frame = refii::vfs::LoadGuestU32(base, refii::guest::Address(0x83319650u));
  g_two_may_hold = Plausible(event) && refii::vfs::LoadGuestU8(base, event + 8u) == 11u &&
                   CoopGame(base) &&
                   (frame - g_exit_prompt_two_frame <= 2u || PlayerTwoTargetsHoldUse(ctx, base));
  __imp__rex_lhPromptManager_HoldInput(ctx, base);
  g_two_may_hold = previous;
}

// The prompt manager's update: a full hold uses as the player who held.
REX_EXTERN(rex_lhPromptManager_Update) {
  const uint32_t previous = g_hold_creature;
  g_hold_creature = g_hold_player == 2 && CoopGame(base) ? refii_mod_player_hero(base, 2) : 0u;
  __imp__rex_lhPromptManager_Update(ctx, base);
  g_hold_creature = previous;
}
