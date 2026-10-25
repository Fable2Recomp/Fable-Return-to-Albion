// refii — what a hard mod's native code calls INTO: the per-tick callbacks and the hero lookup
// declared in core/refii_mod.h. Payload-internal: a mod's sources are compiled into this same
// module, so nothing here crosses the payload/host line.

#include <chrono>
#include <algorithm>
#include <cstdint>
#include <cstring>
#include <unordered_map>
#include <vector>

#include <rex/logging.h>

#include "core/guest_state.h"
#include "core/payload_bridge.h"
#include "core/refii_mod.h"
#include "hooks/ppc_externs.h"
#include "vfs_hooks_private.h"

#include <atomic>
#include <cmath>
#include <cstdio>
#include <string>

// The game's own bodies, by their config names — called, never overridden here.
PPC_EXTERN_FUNC(__imp__rex_CECStats_AbilityExperienceCost);
PPC_EXTERN_FUNC(__imp__rex_CSpellRegistry_FindLive);
PPC_EXTERN_FUNC(__imp__rex_CECFaction_RelationTo);
// Hooked in mod_events.cpp (the events), so a helper that must raise the event calls the name.
PPC_EXTERN_FUNC(rex_CECExperience_Modify);
PPC_EXTERN_FUNC(rex_CECExperience_Spend);
PPC_EXTERN_FUNC(rex_CSpellRegistry_Create);
PPC_EXTERN_FUNC(rex_CECHealth_Modify);
PPC_EXTERN_FUNC(rex_CECMoney_Modify);
PPC_EXTERN_FUNC(rex_CECInventory_AddOneOfRecord);
PPC_EXTERN_FUNC(rex_lhTrackedObject_RemoveHandleRef);
PPC_EXTERN_FUNC(rex_GDBManager_FindEntryByHash);
PPC_EXTERN_FUNC(rex_lhHeroStats_GetRenown);
PPC_EXTERN_FUNC(rex_lhHeroStats_GetMorality);
PPC_EXTERN_FUNC(rex_lhHeroStats_GetPurity);
PPC_EXTERN_FUNC(rex_Village_IsLineOfSight);
PPC_EXTERN_FUNC(rex_lhLuaScriptSystem_CreateEntityAtPosition);
// The lock, the statuses and the hit (refii_mod_lock_target .. refii_mod_stagger). OnHit is the
// Hit event's seam, so the name: a stated hit is a hit.
PPC_EXTERN_FUNC(rex_CPlayer_SetLookAtButtonDown);
PPC_EXTERN_FUNC(rex_SpellLightning_Electrify);
PPC_EXTERN_FUNC(rex_SpellChaos_ApplyToVictim);
PPC_EXTERN_FUNC(rex_SpellFireball_Ignite);
PPC_EXTERN_FUNC(rex_CECHitReceiver_OnHit);
// The opinion setter (refii_mod_modify_opinion): hooked in mod_events.cpp, so the event fires.
PPC_EXTERN_FUNC(rex_CECOpinionReaction_ModifyAxisValue);

namespace refii::vfs {
/// The script system the startup scripts ran on (mod_lua_hooks.cpp), or 0 before a session.
uint32_t ModScriptSystem();
uint32_t HeroEntity(uint8_t* base);
bool IsCoopJoiner(uint8_t* base);
/// guest_tick_pump.cpp: the quick menu's action (1 use an item, 2 perform an expression, 3 equip a
/// weapon, 4 assign a spell) for a player's hero, gated as the quick menu is; true when performed.
bool PerformQuickslot(PPCContext& ctx, uint8_t* base, uint32_t player, uint32_t kind, uint32_t record);
bool CoopSessionLive(uint8_t* base);
bool MenusActForPlayerTwo(uint8_t* base);
void RunModEventsTick(PPCContext& ctx, uint8_t* base, float seconds);

std::atomic<uint32_t> g_lua_generation{0};

void BumpLuaGeneration() {
  g_lua_generation.fetch_add(1u, std::memory_order_relaxed);
  refii_bridge_new_game_generation();
}
}  // namespace refii::vfs

namespace {

constexpr uint32_t kGameRoot = 0x83496AB8u;
constexpr uint32_t kHealthComponent = 36;
constexpr uint32_t kSpellManagerComponent = 56;
constexpr uint32_t kStatsComponent = 57;
constexpr uint32_t kExperienceComponent = 164;
constexpr uint32_t kHealthCurrent = 0x14;
constexpr uint32_t kHealthMax = 0x1C;
constexpr uint32_t kAbilityTable = 0x110;
constexpr uint32_t kExperiencePools = 0x10;
constexpr uint32_t kWorldRegistry = 0x44;
constexpr uint32_t kInventoryComponent = 13;
constexpr uint32_t kMoneyComponent = 81;
constexpr uint32_t kFactionComponent = 133;
constexpr uint32_t kEntityDefinition = 0x1C;
constexpr uint32_t kEntityName = 0x80;
constexpr uint32_t kPlacement = 0x7C;
constexpr uint32_t kSetPositionSlot = 25;
constexpr uint32_t kAppearanceComponent = 3;  // CECGraphicAppearance (ctor sub_82571DF8)
constexpr uint32_t kSetTintVector = 0x82573770u;
// The other component-3 setters (entity_appearance.cpp lists them): r3 the component, the value
// in f1 (alpha, scale) or r4 (a byte).
constexpr uint32_t kSetAlpha = 0x82573220u;           // disc 0x82547EC8
constexpr uint32_t kSetScale = 0x82573078u;           // disc 0x82547D20
constexpr uint32_t kSetSaturationMask = 0x822B8188u;  // disc 0x822B7EA8
constexpr uint32_t kSetAsDrawable = 0x825725A8u;      // disc 0x82547250
constexpr uint32_t kPlacementScaleSlot = 43;          // what SetScale calls on entity+0x7C (0x82573100)
// The target lock (refii_mod.h states each): the player component and its handle's HasMode.
constexpr uint32_t kPlayerComponent = 33;
constexpr uint32_t kTargetingComponent = 77;
constexpr uint32_t kPlayerHandle = 0x14;    // comp33 + 0x14, what HasPlayerMode dereferences (0x821aa5d8)
constexpr uint32_t kForceLockByte = 0x14;   // comp77 + 0x14, SetForceTargetLock's `stb r4,0x14(r3)`
constexpr uint32_t kTargetLockMode = 4;     // PLAYER_MODE_TARGET_LOCK
constexpr uint32_t kHandleHasMode = 0x821E8B90u;        // disc 0x821E8870
constexpr uint32_t kTargetingGetTargetSlot = 17;
constexpr uint32_t kTargetingSetTargetSlot = 19;
constexpr uint32_t kTargetingSwitchSlot = 49;
// The statuses: components, the installers' is-creature test and the removers.
constexpr uint32_t kStatusComponents[5] = {0u, 205u, 206u, 208u, 209u};
constexpr uint32_t kStatusCreatureComponent = 20;  // CECCreature: the installers' is-creature flag
constexpr uint32_t kRemoveElectrified = 0x827427B8u;  // disc 0x827178B8
constexpr uint32_t kRemoveChaos = 0x82749F38u;        // disc 0x8271F038
constexpr uint32_t kRemoveBurning = 0x827469A8u;      // disc 0x8271BAA8
// The hit record: its constructor, name setter and destructor, and the fields a Force Push fills.
constexpr uint32_t kHitReceiverComponent = 31;
constexpr uint32_t kHitCtor = 0x825C4958u;       // disc 0x824831E8
constexpr uint32_t kHitSetName = 0x82275438u;    // disc 0x82275368
constexpr uint32_t kHitDtor = 0x823919C0u;       // disc 0x8237D608
constexpr uint32_t kHitBytes = 0xE0u;            // 212 written by the constructor, rounded up
constexpr uint32_t kHitKnockdown = 0x18;
constexpr uint32_t kHitUnblockable = 0x19;
constexpr uint32_t kHitDamage = 0x38;
constexpr uint32_t kHitFlags = 0x40;
constexpr uint32_t kHitSerial = 0x44;
constexpr uint32_t kHitKindA = 0x50;
constexpr uint32_t kHitSpellType = 0x54;
constexpr uint32_t kHitKindB = 0x58;
constexpr uint32_t kHitOrigin = 0x70;
constexpr uint32_t kHitDirection = 0x90;
constexpr uint32_t kHitDirectionBack = 0xA0;
constexpr uint32_t kHitResponseName = 0xC0;
constexpr uint32_t kForcePushFlag = 0x20000u;
constexpr int32_t kForcePushType = 6;
constexpr uint32_t kManagerDefinitions = 400;
constexpr uint32_t kSpellSerial = 0x44;

bool Plausible(uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; }

bool LuaIdentifier(const char* key) {
  if (key == nullptr || *key == 0) {
    return false;
  }
  for (const char* c = key; *c != 0; ++c) {
    const bool ok = (*c >= 'a' && *c <= 'z') || (*c >= 'A' && *c <= 'Z') || *c == '_' ||
                    (c != key && *c >= '0' && *c <= '9');
    if (!ok) {
      return false;
    }
  }
  return true;
}

/// A guest C string into `out`, bounded; false for a null or implausible pointer.
bool CopyGuestString(uint8_t* base, uint32_t text, char* out, uint32_t size) {
  if (out == nullptr || size == 0) {
    return false;
  }
  out[0] = 0;
  if (!Plausible(text)) {
    return false;
  }
  uint32_t n = 0;
  for (; n + 1 < size && n < 512u; ++n) {
    const char c = static_cast<char>(base[text + n]);
    if (c == 0) {
      break;
    }
    out[n] = c;
  }
  out[n] = 0;
  return true;
}

/// An LHString handle (a pointer to the body whose first word is the char*), or 0.
uint32_t LhStringText(uint8_t* base, uint32_t handle_at) {
  const uint32_t body = refii::vfs::LoadGuestU32(base, handle_at);
  return Plausible(body) ? refii::vfs::LoadGuestU32(base, body) : 0u;
}

}  // namespace

namespace {

// Function-local, so a mod's static registrar can run before this TU's own statics.
std::vector<refii_mod_tick_fn>& Ticks() {
  static std::vector<refii_mod_tick_fn> ticks;
  return ticks;
}

}  // namespace

extern "C" void refii_mod_register_tick(refii_mod_tick_fn fn) {
  if (fn != nullptr) {
    Ticks().push_back(fn);
  }
}

extern "C" uint32_t refii_mod_local_hero(uint8_t* base) { return refii::vfs::HeroEntity(base); }

/// The players' heroes as the guest itself finds them (`sub_824CE278` 0x824ce3c0): the entity
/// registry `*(*(*(*(root)+0xC)+0x58)+4)` (the global at 0x83496B28), then `sub_8222C1B8` reads
/// player 1's handle at +0x6C and `sub_828E5A78` player 2's at +0x74, each a pointer to the
/// entity. Inlined rather than called: sub_828E5A78 is not emitted in the generated module.
extern "C" uint32_t refii_mod_player_hero(uint8_t* base, int32_t player) {
  if (player != 1 && player != 2) {
    return 0u;
  }

  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
  uint32_t at = refii::vfs::LoadGuestU32(base, refii::guest::Address(0x83496AB8u));
  for (uint32_t offset : {0xCu, 0x58u, 0x4u, 0x0u}) {
    if (!plausible(at)) {
      return 0u;
    }
    at = refii::vfs::LoadGuestU32(base, at + offset);
  }

  if (!plausible(at)) {
    return 0u;
  }

  const uint32_t handle = player == 1 ? refii_bridge_hero_one_holder(base, at) : refii::vfs::LoadGuestU32(base, at + 0x74u);
  return plausible(handle) ? refii::vfs::LoadGuestU32(base, handle) : 0u;
}

extern "C" int32_t refii_mod_entity_position(PPCContext* ctx, uint8_t* base, uint32_t entity, float* out_xyz) {
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
  if (ctx == nullptr || out_xyz == nullptr || !plausible(entity)) {
    return 0;
  }
  using refii::vfs::LoadGuestU32;
  const uint32_t placement = LoadGuestU32(base, entity + 0x7Cu);
  const uint32_t vtable = plausible(placement) ? LoadGuestU32(base, placement) : 0u;
  const uint32_t target = plausible(vtable) ? LoadGuestU32(base, vtable + 64u) : 0u;
  // The function table, not a scan of every mapping: this runs once per entity in a radius query.
  PPCFunc* getter = target != 0 && refii_bridge_rex_has_function(target) != 0
                        ? reinterpret_cast<PPCFunc*>(refii_bridge_rex_resolve_indirect_function(target))
                        : nullptr;
  if (getter == nullptr) {
    return 0;
  }
  // Scratch below the caller's frame; the getter stores the vector at r3.
  const uint32_t frame = (ctx->r1.u32 - 0x100u) & ~0xFu;
  const uint32_t out = frame + 0x60u;
  PPCContext call = *ctx;
  call.r1.u32 = frame;
  call.r3.u32 = out;
  call.r4.u32 = placement;
  getter(call, base);
  for (uint32_t i = 0; i < 3u; ++i) {
    const uint32_t bits = LoadGuestU32(base, out + 4u * i);
    std::memcpy(&out_xyz[i], &bits, sizeof(float));
  }
  return 1;
}

extern "C" uint32_t refii_mod_component(uint8_t* base, uint32_t entity, uint32_t id) {
  const auto plausible = [](uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; };
  if (id >= 256u || !plausible(entity)) {
    return 0u;
  }

  using refii::vfs::LoadGuestU32;
  const uint32_t present = LoadGuestU32(base, entity + 0x24u + (id / 32u) * 4u);
  if (((present >> (id % 32u)) & 1u) == 0u) {
    return 0u;
  }

  const uint32_t pairs = LoadGuestU32(base, entity + 0x48u);
  const uint32_t pairs_end = LoadGuestU32(base, entity + 0x4Cu);
  if (!plausible(pairs) || pairs_end < pairs || (pairs_end - pairs) > 8u * 512u) {
    return 0u;
  }

  const uint32_t index = LoadGuestU32(base, entity + 0x8Cu);
  if (plausible(index)) {
    const uint32_t at = pairs + 8u * base[index + id];
    if (at + 8u <= pairs_end && LoadGuestU32(base, at) == id) {
      const uint32_t component = LoadGuestU32(base, at + 4u);
      return plausible(component) ? component : 0u;
    }
  }

  for (uint32_t at = pairs; at + 8u <= pairs_end; at += 8u) {
    if (LoadGuestU32(base, at) == id) {
      const uint32_t component = LoadGuestU32(base, at + 4u);
      return plausible(component) ? component : 0u;
    }
  }
  return 0u;
}

extern "C" int32_t refii_mod_run_game_lua(PPCContext* ctx, uint8_t* base, const char* chunk) {
  if (ctx == nullptr || chunk == nullptr) {
    return 0;
  }
  return refii::vfs::RunGameLua(*ctx, base, chunk) ? 1 : 0;
}

extern "C" int32_t refii_mod_game_lua_int(PPCContext* ctx, uint8_t* base, const char* expr,
                                          int32_t lo, int32_t hi, int32_t* out) {
  if (ctx == nullptr || expr == nullptr || lo > hi) {
    return 0;
  }

  const auto holds = [&](const char* op, int32_t value) {
    char chunk[512];
    std::snprintf(chunk, sizeof(chunk),
                  "local v = (%s) assert(type(v) == 'number' and v %s %d)", expr, op, value);
    return refii::vfs::RunGameLua(*ctx, base, chunk);
  };

  if (!holds(">=", lo) || !holds("<=", hi)) {
    return 0;
  }

  // The greatest integer m with v >= m: `v >= mid` halves the range each run.
  while (lo < hi) {
    const int32_t mid = static_cast<int32_t>(static_cast<int64_t>(lo) + (static_cast<int64_t>(hi) - lo + 1) / 2);
    if (holds(">=", mid)) {
      lo = mid;
    } else {
      hi = mid - 1;
    }
  }

  if (out != nullptr) {
    *out = lo;
  }
  return 1;
}

extern "C" uint32_t refii_mod_lua_generation(void) {
  return refii::vfs::g_lua_generation.load(std::memory_order_relaxed);
}

namespace refii::vfs {

/// PLAYER STATE'S OWNERS AND HEROES (core/player_state.h), stated before the mods tick.
///
/// - Who plays which hero: both here outside an online session; inside one (a peer standing,
///   CoopSessionLive) the host plays player one and the joiner player two (IsCoopJoiner). A
///   joiner's player two came from the save it picked on the co-op screen (the co-op hero load
///   fills it, player_state_hooks.cpp).
/// - A new hero entity for a player drops that player's session values (a pool belongs to a
///   body); the saved ones stay until a save load replaces them.
/// - Player two leaving — the game's partner mode (game+0x11C: 1 online, 2 local co-op) dropping to
///   none — empties player two here: those values were the partner's and went home with them.
/// - A NEW GAME: player one's save name (SaveMgr+0, game+148, the LHString the save driver names
///   player one's package by, sub_82444C40 0x82444D14) changing to a name no load gave it. New Game
///   (sub_82379628) creates a fresh package and assigns its name there (sub_82445050, 0x82445358);
///   the only other writers are the load (0x82446b34, which player_state_hooks.cpp sees) and the
///   driver's first save of a game with no name (0x824455a4). Player one's values were the old
///   save's hero's; the new hero starts with none.
void DrivePlayerState(uint8_t* base) {
  static uint32_t heroes[2] = {0u, 0u};
  static uint32_t partner_mode = 0u;
  static std::string named;

  const bool live = CoopSessionLive(base);
  refii_bridge_player_set_owned(!live ? 3u : (IsCoopJoiner(base) ? 2u : 1u));

  const uint32_t root = LoadGuestU32(base, refii::guest::Address(kGameRoot));
  const uint32_t game = Plausible(root) ? LoadGuestU32(base, root + 12u) : 0u;
  if (!Plausible(game)) {
    return;
  }

  const uint32_t mode = LoadGuestU32(base, game + 0x11Cu);
  if (partner_mode != 0u && mode == 0u) {
    refii_bridge_player_clear(2, 0);
  }
  partner_mode = mode;

  constexpr uint32_t kSaveManager = 148u;
  const uint32_t rep = LoadGuestU32(base, game + kSaveManager);
  const uint32_t text = Plausible(rep) ? LoadGuestU32(base, rep) : 0u;
  const uint32_t length = Plausible(rep) ? LoadGuestU32(base, rep + 4u) : 0u;
  const std::string name = Plausible(text) && length != 0u && length <= 64u
                               ? std::string(reinterpret_cast<const char*>(base + text), length)
                               : std::string();
  if (name != named) {
    if (!named.empty() && !name.empty() && refii_bridge_player_loaded_from(1, name.c_str()) == 0) {
      refii_bridge_player_clear(1, 1);
    }
    named = name;
  }

  for (int32_t player = 1; player <= 2; ++player) {
    const uint32_t hero = refii_mod_player_hero(base, player);
    if (hero != 0u && hero != heroes[player - 1]) {
      refii_bridge_player_new_hero(player);
    }
    if (hero != 0u) {
      heroes[player - 1] = hero;
      // Its identity for Fable 3's partnerships (core/hero_partners.cpp), made where it is played.
      refii_bridge_hero_id_ensure(player);
    }
  }
}

/// Runs every mod's tick callback with the real seconds since the last guest tick. Called from
/// OnGuestTick, on the guest tick thread, where guest calls are legal.
void RunModTicks(PPCContext& ctx, uint8_t* base) {
  DrivePlayerState(base);
  std::vector<refii_mod_tick_fn>& ticks = Ticks();

  using Clock = std::chrono::steady_clock;
  static Clock::time_point last = Clock::now();
  const Clock::time_point now = Clock::now();
  float seconds = std::chrono::duration<float>(now - last).count();
  last = now;
  // A hitch (a load screen, a breakpoint) is not time the player spent regenerating.
  if (seconds > 0.25f) {
    seconds = 0.25f;
  }

  DriveHouseholds(ctx, base, seconds);
  for (refii_mod_tick_fn fn : ticks) {
    PPCContext work = ctx;
    fn(&work, base, seconds);
  }
  RunModEventsTick(ctx, base, seconds);
}

}  // namespace refii::vfs

// ---------------------------------------------------------------------------------------------
// Entities, stats, spells, the save
// ---------------------------------------------------------------------------------------------

extern "C" uint32_t refii_mod_handle(uint32_t address) { return refii_bridge_mod_handle(address); }

extern "C" uint32_t refii_mod_handle_address(uint32_t handle) { return refii_bridge_mod_handle_address(handle); }

// ---------------------------------------------------------------------------------------------
// Effects: a request for Godot, posted on the host-only kind REFII_MOD_HOST_FX_KIND.
// ---------------------------------------------------------------------------------------------

namespace {

/// The payload's ids are 1 .. 2^23-1; Godot's own play_fx counts from 2^23 (Engine_FxPlayer), so
/// every id is exact as a Lua number (a 32-bit float) and no surface's id collides with another's.
constexpr uint32_t kFxIdSpan = 0x7FFFFFu;
std::atomic<uint32_t> g_fx_ids{0};

/// The fields text RefiiHost parses: key=value, a 'quoted' text never carrying a quote.
uint32_t PostPlayFx(const char* name, const float* xyz, uint32_t handle, const refii_mod_fx_options* options) {
  if (name == nullptr || name[0] == 0) {
    return 0;
  }
  char clean[128];
  size_t n = 0;
  for (; name[n] != 0 && n + 1 < sizeof(clean); ++n) {
    const char c = name[n];
    clean[n] = (c == '\'' || c == ',' || static_cast<unsigned char>(c) < 0x20) ? '_' : c;
  }
  clean[n] = 0;
  const refii_mod_fx_options o = options != nullptr ? *options : refii_mod_fx_default_options();
  const float origin[3] = {xyz != nullptr ? xyz[0] : 0.0f, xyz != nullptr ? xyz[1] : 0.0f, xyz != nullptr ? xyz[2] : 0.0f};
  const uint32_t id = (g_fx_ids.fetch_add(1u, std::memory_order_relaxed) % kFxIdSpan) + 1u;
  // The helper point (options->at), as a quoted field: a name never carries a quote or a comma.
  char at[96];
  size_t m = 0;
  for (const char* c = o.at; c != nullptr && *c != 0 && m + 1 < sizeof(at); ++c) {
    at[m++] = (*c == '\'' || *c == ',' || static_cast<unsigned char>(*c) < 0x20) ? '_' : *c;
  }
  at[m] = 0;
  char fields[640];
  const int written = std::snprintf(
      fields, sizeof(fields),
      "op='play',id=%u,name='%s',x=%.9g,y=%.9g,z=%.9g,entity=%u,duration=%.9g,scale=%.9g,r=%.9g,g=%.9g,b=%.9g,"
      "intensity=%.9g,loop=%d,ox=%.9g,oy=%.9g,oz=%.9g,at='%s'",
      id, clean, origin[0], origin[1], origin[2], handle, o.duration, o.scale, o.tint[0], o.tint[1], o.tint[2],
      o.intensity, o.loop != 0 ? 1 : 0, o.offset[0], o.offset[1], o.offset[2], at);
  if (written <= 0 || static_cast<size_t>(written) >= sizeof(fields)) {
    return 0;
  }
  refii::mods::PostEvent(REFII_MOD_HOST_FX_KIND, std::string_view(fields, static_cast<size_t>(written)));
  return id;
}

}  // namespace

extern "C" uint32_t refii_mod_play_fx(const char* name, const float* xyz, const refii_mod_fx_options* options) {
  return PostPlayFx(name, xyz, 0u, options);
}

extern "C" uint32_t refii_mod_play_fx_on(const char* name, uint32_t entity, const refii_mod_fx_options* options) {
  if (entity == 0) {
    return 0;
  }
  return PostPlayFx(name, nullptr, refii_mod_handle(entity), options);
}

namespace refii::vfs {

/// The Lua surface's form: the entity is already a handle (Refii.play_fx_on).
uint32_t PlayModFxOnHandle(const char* name, const float* xyz, uint32_t handle, const refii_mod_fx_options* options) {
  return PostPlayFx(name, xyz, handle, options);
}

}  // namespace refii::vfs

extern "C" uint32_t refii_mod_play_fx_at(const char* name, uint32_t entity, const char* helper,
                                         const refii_mod_fx_options* options) {
  refii_mod_fx_options o = options != nullptr ? *options : refii_mod_fx_default_options();
  o.at = helper;
  return refii_mod_play_fx_on(name, entity, &o);
}

extern "C" void refii_mod_stop_fx(uint32_t id) {
  if (id == 0) {
    return;
  }
  char fields[64];
  const int written = std::snprintf(fields, sizeof(fields), "op='stop',id=%u", id);
  if (written > 0 && static_cast<size_t>(written) < sizeof(fields)) {
    refii::mods::PostEvent(REFII_MOD_HOST_FX_KIND, std::string_view(fields, static_cast<size_t>(written)));
  }
}

// ---------------------------------------------------------------------------------------------
// Lights: a request for Godot on the effect kind (Engine_ModLights reads op='light' / 'light_off')
// ---------------------------------------------------------------------------------------------

namespace {

/// The payload's light ids share the effects' span (1 .. 2^23-1); Godot's own count from 2^23.
std::atomic<uint32_t> g_light_ids{0};

/// The light request as the bus carries it: the entity as a handle (0 for the world point).
uint32_t PostLight(uint32_t handle, const float* xyz, const float* rgb, float energy, float range,
                   const refii_mod_light_options* options) {
  if (rgb == nullptr) {
    return 0;
  }
  const uint32_t id = (g_light_ids.fetch_add(1u, std::memory_order_relaxed) % kFxIdSpan) + 1u;
  const float origin[3] = {xyz != nullptr ? xyz[0] : 0.0f, xyz != nullptr ? xyz[1] : 0.0f, xyz != nullptr ? xyz[2] : 0.0f};
  const float offset[3] = {options != nullptr ? options->offset[0] : 0.0f, options != nullptr ? options->offset[1] : 0.0f,
                           options != nullptr ? options->offset[2] : 0.0f};
  char at[96];
  size_t m = 0;
  for (const char* c = options != nullptr ? options->at : nullptr; c != nullptr && *c != 0 && m + 1 < sizeof(at); ++c) {
    at[m++] = (*c == '\'' || *c == ',' || static_cast<unsigned char>(*c) < 0x20) ? '_' : *c;
  }
  at[m] = 0;
  // The spot fields (dx dy dz cone atten) ride every request; cone 0 is the omni light.
  const float direction[3] = {options != nullptr ? options->direction[0] : 0.0f,
                              options != nullptr ? options->direction[1] : 0.0f,
                              options != nullptr ? options->direction[2] : 0.0f};
  char fields[512];
  const int written = std::snprintf(
      fields, sizeof(fields),
      "op='light',id=%u,entity=%u,x=%.9g,y=%.9g,z=%.9g,r=%.9g,g=%.9g,b=%.9g,energy=%.9g,range=%.9g,ox=%.9g,oy=%.9g,oz=%.9g,"
      "at='%s',flicker=%.9g,dx=%.9g,dy=%.9g,dz=%.9g,cone=%.9g,atten=%.9g",
      id, handle, origin[0], origin[1], origin[2], rgb[0], rgb[1], rgb[2], energy, range, offset[0], offset[1], offset[2], at,
      options != nullptr ? options->flicker : 0.0f, direction[0], direction[1], direction[2],
      options != nullptr ? options->cone : 0.0f, options != nullptr ? options->attenuation : 0.0f);
  if (written <= 0 || static_cast<size_t>(written) >= sizeof(fields)) {
    return 0;
  }
  refii::mods::PostEvent(REFII_MOD_HOST_FX_KIND, std::string_view(fields, static_cast<size_t>(written)));
  return id;
}

}  // namespace

extern "C" uint32_t refii_mod_light(uint32_t entity, const float* xyz, const float* rgb, float energy, float range,
                                    const refii_mod_light_options* options) {
  return PostLight(entity != 0u ? refii_mod_handle(entity) : 0u, entity != 0u ? nullptr : xyz, rgb, energy, range, options);
}

namespace refii::vfs {

/// The Lua surface's form: the entity is already a handle (Refii.light).
uint32_t PlaceModLightOnHandle(uint32_t handle, const float* xyz, const float* rgb, float energy, float range,
                               const refii_mod_light_options* options) {
  return PostLight(handle, handle != 0u ? nullptr : xyz, rgb, energy, range, options);
}

}  // namespace refii::vfs

extern "C" void refii_mod_light_remove(uint32_t id) {
  if (id == 0) {
    return;
  }
  char fields[64];
  const int written = std::snprintf(fields, sizeof(fields), "op='light_off',id=%u", id);
  if (written > 0 && static_cast<size_t>(written) < sizeof(fields)) {
    refii::mods::PostEvent(REFII_MOD_HOST_FX_KIND, std::string_view(fields, static_cast<size_t>(written)));
  }
}

// ---------------------------------------------------------------------------------------------
// The UI: a request for Godot, posted on the host-only kind REFII_MOD_HOST_UI_KIND
// ---------------------------------------------------------------------------------------------

namespace {

/// A text for the bus's quoted field: no quote (the field's end) and no tab or line feed (the
/// record's); a line break crosses as the two characters \n, which Godot reads back.
std::string QuotedField(const char* text, size_t limit) {
  std::string out;
  for (const char* c = text; c != nullptr && *c != 0 && out.size() < limit; ++c) {
    if (*c == '\'') {
      out += '`';
    } else if (*c == '\n') {
      out += "\\n";
    } else if (static_cast<unsigned char>(*c) < 0x20u) {
      out += ' ';
    } else {
      out += *c;
    }
  }
  return out;
}

/// The payload's box ids are 1 .. 2^23-1; Godot's own message_box counts from 2^23 (Engine_UiModRequest).
std::atomic<uint32_t> g_box_ids{0};

int32_t ClampPlayer(int32_t player) { return player == 1 || player == 2 ? player : 0; }

}  // namespace

extern "C" int32_t refii_mod_open_menu(int32_t player, const char* name) {
  const std::string menu = QuotedField(name, 96);
  if (menu.empty()) {
    return 0;
  }
  const std::string fields = "op='open_menu',player=" + std::to_string(ClampPlayer(player)) + ",name='" + menu + "'";
  refii::mods::PostEvent(REFII_MOD_HOST_UI_KIND, fields);
  return 1;
}

extern "C" uint32_t refii_mod_message_box(int32_t player, const char* title, const char* text, const char* accept,
                                          const char* cancel) {
  const std::string head = QuotedField(title, 128);
  const std::string body = QuotedField(text, 480);
  if (head.empty() && body.empty()) {
    return 0;
  }
  const uint32_t id = (g_box_ids.fetch_add(1u, std::memory_order_relaxed) % kFxIdSpan) + 1u;
  const std::string fields = "op='message_box',id=" + std::to_string(id) + ",player=" +
                             std::to_string(ClampPlayer(player)) + ",title='" + head + "',text='" + body +
                             "',accept='" + QuotedField(accept, 64) + "',cancel='" + QuotedField(cancel, 64) +
                             "',buttons=" + (cancel != nullptr ? "2" : "1");
  refii::mods::PostEvent(REFII_MOD_HOST_UI_KIND, fields);
  return id;
}

extern "C" uint32_t refii_mod_notice(int32_t player, const char* title, const char* text, float seconds) {
  const std::string head = QuotedField(title, 128);
  const std::string body = QuotedField(text, 480);
  if (head.empty() && body.empty()) {
    return 0;
  }
  const uint32_t id = (g_box_ids.fetch_add(1u, std::memory_order_relaxed) % kFxIdSpan) + 1u;
  const float shown = std::isfinite(seconds) && seconds > 0.0f ? seconds : 0.0f;
  char length[32];
  std::snprintf(length, sizeof(length), "%.3f", static_cast<double>(shown));
  const std::string fields = "op='notice',id=" + std::to_string(id) + ",player=" + std::to_string(ClampPlayer(player)) +
                             ",title='" + head + "',text='" + body + "',seconds=" + length;
  refii::mods::PostEvent(REFII_MOD_HOST_UI_KIND, fields);
  return id;
}

extern "C" uint32_t refii_mod_menu_box(int32_t player, const char* title, const char* text, const char* const* options,
                                       int32_t count, int32_t cancelable) {
  constexpr int32_t kMaxOptions = 16;  // Engine_UiModRequest.MaxMenuOptions
  const int32_t n = options == nullptr || count < 0 ? 0 : (count > kMaxOptions ? kMaxOptions : count);
  if (n == 0) {
    return 0;
  }
  const uint32_t id = (g_box_ids.fetch_add(1u, std::memory_order_relaxed) % kFxIdSpan) + 1u;
  std::string fields = "op='menu_box',id=" + std::to_string(id) + ",player=" + std::to_string(ClampPlayer(player)) +
                       ",title='" + QuotedField(title, 128) + "',text='" + QuotedField(text, 480) +
                       "',cancel=" + (cancelable != 0 ? "1" : "0") + ",count=" + std::to_string(n);
  for (int32_t i = 0; i < n; ++i) {
    fields += ",o" + std::to_string(i) + "='" + QuotedField(options[i], 96) + "'";
  }
  refii::mods::PostEvent(REFII_MOD_HOST_UI_KIND, fields);
  return id;
}

// ---------------------------------------------------------------------------------------------
// HUD: markers, fades and meters — requests for Godot on the UI kind (Engine_UiModBoxes hands
// them to Engine_HudModMarkers, Engine_UiModFade and Engine_HudModMeters). Never an event.
// ---------------------------------------------------------------------------------------------

namespace {

/// Marker ids: the payload's 1 .. 2^23-1, Godot's from 2^23 (Engine_HudModMarkers.FirstHostId).
std::atomic<uint32_t> g_mark_ids{0};

std::string Float(float value) {
  char text[32];
  std::snprintf(text, sizeof(text), "%.9g", static_cast<double>(std::isfinite(value) ? value : 0.0f));
  return text;
}

}  // namespace

extern "C" uint32_t refii_mod_mark(int32_t player, uint32_t entity, const float* xyz, const char* icon,
                                   const char* text) {
  const std::string picture = QuotedField(icon, 160);
  if (picture.empty()) {
    return 0;
  }
  const uint32_t id = (g_mark_ids.fetch_add(1u, std::memory_order_relaxed) % kFxIdSpan) + 1u;
  const uint32_t handle = entity != 0u ? refii_mod_handle(entity) : 0u;
  const std::string fields = "op='mark',id=" + std::to_string(id) + ",player=" + std::to_string(ClampPlayer(player)) +
                             ",entity=" + std::to_string(handle) + ",x=" + Float(xyz != nullptr ? xyz[0] : 0.0f) +
                             ",y=" + Float(xyz != nullptr ? xyz[1] : 0.0f) + ",z=" + Float(xyz != nullptr ? xyz[2] : 0.0f) +
                             ",icon='" + picture + "',text='" + QuotedField(text, 160) + "'";
  refii::mods::PostEvent(REFII_MOD_HOST_UI_KIND, fields);
  return id;
}

extern "C" void refii_mod_unmark(uint32_t id) {
  if (id == 0u) {
    return;
  }
  refii::mods::PostEvent(REFII_MOD_HOST_UI_KIND, "op='unmark',id=" + std::to_string(id));
}

extern "C" int32_t refii_mod_fade(int32_t player, int32_t to_black, float seconds) {
  const std::string fields = "op='fade',player=" + std::to_string(ClampPlayer(player)) + ",to=" +
                             (to_black != 0 ? "1" : "0") + ",seconds=" + Float(seconds > 0.0f ? seconds : 0.0f);
  refii::mods::PostEvent(REFII_MOD_HOST_UI_KIND, fields);
  return 1;
}

extern "C" int32_t refii_mod_meter(int32_t player, const char* id, const char* label, float value, float max,
                                   const char* style) {
  const std::string key = QuotedField(id, 96);
  if (key.empty()) {
    return 0;
  }
  const std::string fields = "op='meter',player=" + std::to_string(ClampPlayer(player)) + ",id='" + key + "',label='" +
                             QuotedField(label, 128) + "',value=" + Float(value) + ",max=" + Float(max) + ",style='" +
                             QuotedField(style, 32) + "'";
  refii::mods::PostEvent(REFII_MOD_HOST_UI_KIND, fields);
  return 1;
}

extern "C" int32_t refii_mod_meter_remove(const char* id) {
  const std::string key = QuotedField(id, 96);
  if (key.empty()) {
    return 0;
  }
  refii::mods::PostEvent(REFII_MOD_HOST_UI_KIND, "op='meter_remove',id='" + key + "'");
  return 1;
}

// Input claims and HUD overrides: state Godot's pad publish and HUD feed read (ModInput,
// Engine_UiHudOverrides), posted on the host-only UI kind like the meters.

extern "C" int32_t refii_mod_input_claim(int32_t player, const char* action, int32_t on) {
  const std::string name = QuotedField(action, 96);
  if (name.empty()) {
    return 0;
  }
  refii::mods::PostEvent(REFII_MOD_HOST_UI_KIND, "op='input_claim',player=" + std::to_string(ClampPlayer(player)) +
                                                     ",action='" + name + "',on=" + (on != 0 ? "1" : "0"));
  return 1;
}

extern "C" int32_t refii_mod_hud_icon(int32_t player, const char* element, const char* texture) {
  const std::string name = QuotedField(element, 96);
  if (name.empty()) {
    return 0;
  }
  refii::mods::PostEvent(REFII_MOD_HOST_UI_KIND, "op='hud_icon',player=" + std::to_string(ClampPlayer(player)) +
                                                     ",element='" + name + "',texture='" + QuotedField(texture, 160) + "'");
  return 1;
}

extern "C" int32_t refii_mod_hud_slide(int32_t player, const char* element, int32_t slide) {
  const std::string name = QuotedField(element, 96);
  if (name.empty() || slide < 0) {
    return 0;
  }
  refii::mods::PostEvent(REFII_MOD_HOST_UI_KIND, "op='hud_slide',player=" + std::to_string(ClampPlayer(player)) +
                                                     ",element='" + name + "',slide=" + std::to_string(slide));
  return 1;
}

extern "C" int32_t refii_mod_hud_hold(int32_t player, const char* piece, int32_t on) {
  const std::string name = QuotedField(piece, 96);
  if (name.empty()) {
    return 0;
  }
  refii::mods::PostEvent(REFII_MOD_HOST_UI_KIND, "op='hud_hold',player=" + std::to_string(ClampPlayer(player)) +
                                                     ",piece='" + name + "',on=" + (on != 0 ? "1" : "0"));
  return 1;
}

// ---------------------------------------------------------------------------------------------
// Menu rows and host screens: a request for Godot, posted on the host-only kind
// REFII_MOD_HOST_MENU_KIND (Engine_UiModRows reads it)
// ---------------------------------------------------------------------------------------------

extern "C" int32_t refii_mod_set_menu_rows(int32_t player, const char* menu, const char* rows) {
  const std::string list = QuotedField(menu, 96);
  if (list.empty()) {
    return 0;
  }
  const std::string fields = "op='menu_rows',player=" + std::to_string(ClampPlayer(player)) + ",menu='" + list +
                             "',rows='" + QuotedField(rows, 1800) + "'";
  refii::mods::PostEvent(REFII_MOD_HOST_MENU_KIND, fields);
  return 1;
}

extern "C" int32_t refii_mod_open_screen(int32_t player, uint32_t screen) {
  if (screen == 0u) {
    return 0;
  }
  const std::string fields =
      "op='open_screen',player=" + std::to_string(ClampPlayer(player)) + ",screen=" + std::to_string(screen);
  refii::mods::PostEvent(REFII_MOD_HOST_MENU_KIND, fields);
  return 1;
}

extern "C" uint32_t refii_mod_fnv1(const char* text) {
  uint32_t hash = 0x811C9DC5u;
  for (const char* c = text; c != nullptr && *c != 0; ++c) {
    hash = (hash * 0x01000193u) ^ static_cast<uint8_t>(*c);
  }
  return hash;
}

extern "C" int32_t refii_mod_player_of(uint8_t* base, uint32_t entity) {
  if (entity == 0) {
    return 0;
  }
  if (entity == refii_mod_player_hero(base, 1)) {
    return 1;
  }
  if (entity == refii_mod_player_hero(base, 2)) {
    return 2;
  }
  return 0;
}

extern "C" int32_t refii_mod_menu_player(uint8_t* base) {
  return refii::vfs::IsCoopJoiner(base) || refii::vfs::MenusActForPlayerTwo(base) ? 2 : 1;
}

// How this game is shared: 0 alone, 1 local co-op (game+0x11C == 2, the guest's IsLocalCoopGame),
// 2 online with this machine hosting, 3 online with this machine joined. A partner counts once their
// hero exists.
extern "C" int32_t refii_mod_coop_mode(uint8_t* base) {
  if (base == nullptr || refii_mod_player_hero(base, 2) == 0u) {
    return 0;
  }
  if (refii::vfs::IsCoopJoiner(base)) {
    return 3;
  }
  const uint32_t root = refii::mods::LoadU32(base, refii::guest::Address(0x83496AB8u));
  const uint32_t game = root >= 0x10000u && root < 0xC0000000u ? refii::mods::LoadU32(base, root + 12u) : 0u;
  return game >= 0x10000u && game < 0xC0000000u && refii::mods::LoadU32(base, game + 0x11Cu) == 2u ? 1 : 2;
}

extern "C" int32_t refii_mod_world_here(uint8_t* base) { return refii_mod_coop_mode(base) == 3 ? 0 : 1; }

// guest_tick_pump.cpp: a player's dog (players+196's for player one, the co-op record for two).
extern "C" uint32_t RefiiCoopPlayerDog(uint8_t* base, int player);

// The player an entity deals with (core/refii_mod.h): its own if a hero, its master's if a dog,
// else the nearer living hero with 3 m of hysteresis — the rule the core co-op Lua's
// Refii.player_for states. The last answer per entity is kept for the hysteresis; the table is
// small (the entities a mod asks about) and starts over when it grows past its bound.
extern "C" int32_t refii_mod_player_for(PPCContext* ctx, uint8_t* base, uint32_t entity) {
  if (base == nullptr) {
    return 0;
  }
  const uint32_t one = refii_mod_player_hero(base, 1);
  const uint32_t two = refii_mod_player_hero(base, 2);
  const bool one_alive = one != 0u && refii_mod_entity_alive(base, one) != 0;
  const bool two_alive = two != 0u && refii_mod_entity_alive(base, two) != 0;
  if (one == 0u && two == 0u) {
    return 0;
  }
  if (const int32_t own = refii_mod_player_of(base, entity); own != 0) {
    return own;
  }
  if (entity != 0u) {
    if (entity == RefiiCoopPlayerDog(base, 1)) {
      return 1;
    }
    if (entity == RefiiCoopPlayerDog(base, 2)) {
      return 2;
    }
  }
  if (!one_alive || !two_alive) {
    // One hero standing is everyone's; with neither standing, the one that exists.
    return one_alive ? 1 : (two_alive ? 2 : (one != 0u ? 1 : 2));
  }
  float at[3];
  float p1[3];
  float p2[3];
  if (ctx == nullptr || entity == 0u || refii_mod_entity_position(ctx, base, entity, at) == 0 ||
      refii_mod_entity_position(ctx, base, one, p1) == 0 || refii_mod_entity_position(ctx, base, two, p2) == 0) {
    return 1;
  }
  const auto distance = [&at](const float* p) {
    const float dx = p[0] - at[0];
    const float dy = p[1] - at[1];
    const float dz = p[2] - at[2];
    return std::sqrt(dx * dx + dy * dy + dz * dz);
  };
  const float d1 = distance(p1);
  const float d2 = distance(p2);
  constexpr float kHysteresis = 3.0f;
  static std::unordered_map<uint32_t, int32_t> last;
  if (last.size() > 4096u) {
    last.clear();
  }
  auto found = last.find(entity);
  int32_t answer = d2 < d1 ? 2 : 1;
  if (found != last.end()) {
    // Keep the last answer until the other hero is clearly nearer.
    answer = found->second == 1 ? (d2 + kHysteresis < d1 ? 2 : 1) : (d1 + kHysteresis < d2 ? 1 : 2);
  }
  last[entity] = answer;
  return answer;
}

// A villager's opinion of either hero (core/coop_opinion.cpp): player one's is the opinion
// component's own current axes (+44, five floats); player two's the set kept for them, its current
// axes at block+20 in guest byte order, or the villager's base attitudes (+24) before player two
// has done anything there.
extern "C" int32_t refii_mod_opinion(uint8_t* base, uint32_t villager, int32_t player, float* out) {
  const uint32_t opinion = villager != 0u ? refii_mod_component(base, villager, 47u) : 0u;
  if (opinion == 0u || out == nullptr || (player != 1 && player != 2)) {
    return 0;
  }
  uint8_t two[REFII_OPINION_BLOCK_BYTES];
  const bool kept = player == 2 && refii_bridge_opinion_two_load(opinion, two) != 0;
  for (uint32_t axis = 0; axis < 5u; ++axis) {
    if (kept) {
      uint32_t raw = 0u;
      std::memcpy(&raw, two + 20u + (4u * axis), sizeof(raw));
      raw = __builtin_bswap32(raw);
      std::memcpy(&out[axis], &raw, sizeof(float));
    } else {
      out[axis] = refii::mods::LoadF32(base, opinion + (player == 1 ? 44u : 24u) + (4u * axis));
    }
  }
  return 1;
}

// The entity a hero is focused on: component 77's target (`GetFocusTarget` `sub_82843C40`,
// `*(*(comp + 0xC8))`), when it is alive (+144 & 0x40).
extern "C" uint32_t refii_mod_focus(uint8_t* base, int32_t player) {
  const uint32_t hero = refii_mod_player_hero(base, player);
  const uint32_t targeting = hero != 0u ? refii_mod_component(base, hero, 77u) : 0u;
  const uint32_t holder = targeting != 0u ? refii::mods::LoadU32(base, targeting + 0xC8u) : 0u;
  const uint32_t target = holder >= 0x10000u && holder < 0xC0000000u ? refii::mods::LoadU32(base, holder) : 0u;
  if (target < 0x10000u || target >= 0xC0000000u || (refii::mods::LoadU8(base, target + 144u) & 0x40u) == 0u) {
    return 0u;
  }
  return target;
}

// --- player state (core/player_state.h) -------------------------------------------------------

extern "C" float refii_mod_player_value(int32_t player, const char* key, float fallback) {
  float value = fallback;
  return key != nullptr && refii_bridge_player_get(player, key, std::strlen(key), &value) != 0 ? value : fallback;
}

extern "C" int32_t refii_mod_player_get(int32_t player, const char* key, float* out) {
  return key != nullptr ? refii_bridge_player_get(player, key, std::strlen(key), out) : 0;
}

extern "C" void refii_mod_player_set(int32_t player, const char* key, float value) {
  if (key != nullptr) {
    refii_bridge_player_write(player, key, std::strlen(key), value, 0, 0);
  }
}

extern "C" void refii_mod_player_add(int32_t player, const char* key, float delta, int32_t saved) {
  if (key != nullptr) {
    refii_bridge_player_write(player, key, std::strlen(key), delta, 1, saved);
  }
}

extern "C" void refii_mod_player_save(int32_t player, const char* key, float value) {
  if (key != nullptr) {
    refii_bridge_player_write(player, key, std::strlen(key), value, 0, 1);
  }
}

extern "C" int32_t refii_mod_event_replica(void) { return refii_bridge_player_in_replica(); }

extern "C" int32_t refii_mod_player_here(int32_t player) {
  return (player == 1 || player == 2) && ((refii_bridge_player_owned() >> (player - 1)) & 1u) != 0u ? 1 : 0;
}

// --- partner messages: the route's mod lane (core/event_relay.h, `pm:` lines) -------------------

namespace {

/// A partner message's name or mod: letters, digits, '_', '.', '-', 1 .. REFII_MOD_PARTNER_NAME_MAX.
bool PartnerWord(const char* text) {
  if (text == nullptr || text[0] == 0) {
    return false;
  }
  size_t n = 0;
  for (const char* c = text; *c != 0; ++c, ++n) {
    const bool ok = (*c >= 'a' && *c <= 'z') || (*c >= 'A' && *c <= 'Z') || (*c >= '0' && *c <= '9') || *c == '_' ||
                    *c == '.' || *c == '-';
    if (!ok || n >= REFII_MOD_PARTNER_NAME_MAX) {
      return false;
    }
  }
  return true;
}

/// Standard base64 (the route line carries no ';', '|' or line break; the payload may hold any byte).
std::string Base64(const char* bytes, size_t size) {
  static constexpr char kAlphabet[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
  std::string out;
  out.reserve((size + 2) / 3 * 4);
  for (size_t i = 0; i < size; i += 3) {
    const uint32_t a = static_cast<uint8_t>(bytes[i]);
    const uint32_t b = i + 1 < size ? static_cast<uint8_t>(bytes[i + 1]) : 0u;
    const uint32_t c = i + 2 < size ? static_cast<uint8_t>(bytes[i + 2]) : 0u;
    const uint32_t word = (a << 16) | (b << 8) | c;
    out += kAlphabet[(word >> 18) & 63u];
    out += kAlphabet[(word >> 12) & 63u];
    out += i + 1 < size ? kAlphabet[(word >> 6) & 63u] : '=';
    out += i + 2 < size ? kAlphabet[word & 63u] : '=';
  }
  return out;
}

}  // namespace

extern "C" int32_t refii_mod_send_to_partner(const char* mod, const char* name, const char* payload) {
  // Only inside an online session: outside one both players are owned here and there is no partner
  // machine (a line posted then would wait on the route for the next peer).
  const uint32_t owned = refii_bridge_player_owned();
  if (owned != 1u && owned != 2u) {
    return 0;
  }
  if (!PartnerWord(mod) || !PartnerWord(name)) {
    return 0;
  }
  const size_t size = payload != nullptr ? std::strlen(payload) : 0u;
  if (size > REFII_MOD_PARTNER_PAYLOAD_MAX) {
    return 0;
  }
  // pm:<from>:<mod>:<name>:<base64 payload> — the lane Engine/mod_events.cpp drains on the peer.
  const std::string line = "pm:" + std::to_string(owned) + ":" + mod + ":" + name + ":" + Base64(payload, size);
  refii_bridge_route_post(line.c_str());
  return 1;
}

extern "C" int32_t refii_mod_entity_alive(uint8_t* base, uint32_t entity) {
  return Plausible(entity) && (base[entity + 0x90u] & 0x40u) != 0 ? 1 : 0;
}

extern "C" uint32_t refii_mod_world(uint8_t* base) {
  using refii::vfs::LoadGuestU32;
  const uint32_t root = LoadGuestU32(base, refii::guest::Address(kGameRoot));
  const uint32_t game = Plausible(root) ? LoadGuestU32(base, root + 0xCu) : 0u;
  const uint32_t holder = Plausible(game) ? LoadGuestU32(base, game + 0x58u) : 0u;
  const uint32_t world = Plausible(holder) ? LoadGuestU32(base, holder + 4u) : 0u;
  return Plausible(world) ? world : 0u;
}

extern "C" int32_t refii_mod_health(uint8_t* base, uint32_t entity, float* out_current, float* out_max) {
  const uint32_t health = refii_mod_component(base, entity, kHealthComponent);
  if (health == 0) {
    return 0;
  }
  if (out_current != nullptr) {
    *out_current = refii::mods::LoadF32(base, health + kHealthCurrent);
  }
  if (out_max != nullptr) {
    *out_max = refii::mods::LoadF32(base, health + kHealthMax);
  }
  return 1;
}

extern "C" int32_t refii_mod_set_health(uint8_t* base, uint32_t entity, float value) {
  const uint32_t health = refii_mod_component(base, entity, kHealthComponent);
  if (health == 0 || std::isnan(value)) {
    return 0;
  }
  const float max = refii::mods::LoadF32(base, health + kHealthMax);
  refii::mods::StoreF32(base, health + kHealthCurrent, value < 0.0f ? 0.0f : (value > max ? max : value));
  return 1;
}

extern "C" int32_t refii_mod_ability_level(uint8_t* base, uint32_t entity, int32_t type) {
  const uint32_t stats = refii_mod_component(base, entity, kStatsComponent);
  if (stats == 0 || type < 0 || type > 14) {
    return -1;
  }
  return static_cast<int32_t>(refii::mods::LoadU32(base, stats + kAbilityTable + 4u * static_cast<uint32_t>(type)));
}

extern "C" int32_t refii_mod_experience(uint8_t* base, uint32_t entity, int32_t pool, float* out) {
  const uint32_t experience = refii_mod_component(base, entity, kExperienceComponent);
  const uint32_t pools = experience != 0 ? refii::mods::LoadU32(base, experience + kExperiencePools) : 0u;
  if (!Plausible(pools) || pool < 0 || pool > 3) {
    return 0;
  }
  if (out != nullptr) {
    *out = refii::mods::LoadF32(base, pools + 4u * static_cast<uint32_t>(pool));
  }
  return 1;
}

extern "C" int32_t refii_mod_ability_cost(PPCContext* ctx, uint8_t* base, uint32_t entity, int32_t type, int32_t level) {
  const uint32_t stats = ctx != nullptr ? refii_mod_component(base, entity, kStatsComponent) : 0u;
  if (stats == 0 || type < 1 || type > 14 || level < 1) {
    return -1;
  }
  PPCContext work = *ctx;
  work.r3.u32 = stats;
  work.r4.u32 = static_cast<uint32_t>(type);
  work.r5.u32 = static_cast<uint32_t>(level);
  __imp__rex_CECStats_AbilityExperienceCost(work, base);
  return static_cast<int32_t>(work.r3.u32);
}

extern "C" int32_t refii_mod_give_experience(PPCContext* ctx, uint8_t* base, uint32_t entity, int32_t pool, float amount) {
  const uint32_t experience = ctx != nullptr ? refii_mod_component(base, entity, kExperienceComponent) : 0u;
  if (experience == 0 || pool < 0 || pool > 3) {
    return 0;
  }
  PPCContext work = *ctx;
  work.r3.u32 = experience;
  work.r4.u32 = static_cast<uint32_t>(pool);
  work.f1.f64 = static_cast<double>(amount);
  work.r6.u64 = 0;
  work.r7.u64 = 0;
  work.r8.u64 = 0;
  rex_CECExperience_Modify(work, base);
  return 1;
}

extern "C" int32_t refii_mod_spend_experience(PPCContext* ctx, uint8_t* base, uint32_t entity, int32_t pool, float amount) {
  const uint32_t experience = ctx != nullptr ? refii_mod_component(base, entity, kExperienceComponent) : 0u;
  if (experience == 0 || pool < 0 || pool > 3) {
    return 0;
  }
  PPCContext work = *ctx;
  work.r3.u32 = experience;
  work.r4.u32 = static_cast<uint32_t>(pool);
  work.f1.f64 = static_cast<double>(amount);
  rex_CECExperience_Spend(work, base);
  return 1;
}

extern "C" uint32_t refii_mod_cast_spell(PPCContext* ctx, uint8_t* base, uint32_t caster, int32_t type, int32_t targeted,
                                         uint32_t target, int32_t level, const float* pos, const float* dir) {
  if (ctx == nullptr || type < 0 || type > 7 || !Plausible(caster)) {
    return 0;
  }
  const uint32_t manager = refii_mod_component(base, caster, kSpellManagerComponent);
  const uint32_t world = refii_mod_world(base);
  const uint32_t registry = world != 0 ? refii::mods::LoadU32(base, world + kWorldRegistry) : 0u;
  if (manager == 0 || !Plausible(registry) || (target != 0 && !refii_mod_entity_alive(base, target))) {
    return 0;
  }
  float at[3] = {0.0f, 0.0f, 0.0f};
  float facing[3] = {0.0f, 0.0f, 1.0f};
  if (pos != nullptr) {
    at[0] = pos[0];
    at[1] = pos[1];
    at[2] = pos[2];
  } else if (refii_mod_entity_position(ctx, base, target != 0 ? target : caster, at) == 0) {
    return 0;
  }
  if (dir != nullptr) {
    facing[0] = dir[0];
    facing[1] = dir[1];
    facing[2] = dir[2];
  } else if (target != 0) {
    float from[3];
    if (refii_mod_entity_position(ctx, base, caster, from) != 0) {
      const float d[3] = {at[0] - from[0], at[1] - from[1], at[2] - from[2]};
      const float length = std::sqrt(d[0] * d[0] + d[1] * d[1] + d[2] * d[2]);
      if (length > 0.001f) {
        for (int i = 0; i < 3; ++i) {
          facing[i] = d[i] / length;
        }
      }
    }
  }
  PPCContext work = refii::mods::Frame(*ctx);
  work.r3.u32 = registry;
  work.r4.u32 = static_cast<uint32_t>(type);
  work.r5.u32 = targeted != 0 ? 1u : 0u;
  work.r6.u32 = target;
  work.r7.u32 = static_cast<uint32_t>(level < 0 ? 0 : (level > 4 ? 4 : level));
  work.r8.u32 = caster;
  work.r9.u32 = refii::mods::LoadU32(base, manager + kManagerDefinitions);
  refii::mods::SetVector(work, 1, at);
  refii::mods::SetVector(work, 2, facing);
  rex_CSpellRegistry_Create(work, base);
  return Plausible(work.r3.u32) ? work.r3.u32 : 0u;
}

extern "C" uint32_t refii_mod_spell_serial(uint8_t* base, uint32_t spell) {
  return Plausible(spell) ? refii::mods::LoadU32(base, spell + kSpellSerial) : 0u;
}

extern "C" uint32_t refii_mod_live_spell(PPCContext* ctx, uint8_t* base, int32_t type, uint32_t caster) {
  const uint32_t world = ctx != nullptr ? refii_mod_world(base) : 0u;
  const uint32_t registry = world != 0 ? refii::mods::LoadU32(base, world + kWorldRegistry) : 0u;
  if (!Plausible(registry) || type < 0 || type > 7) {
    return 0;
  }
  PPCContext work = *ctx;
  work.r3.u32 = registry;
  work.r4.u32 = static_cast<uint32_t>(type);
  work.r5.u32 = static_cast<uint32_t>(-2);  // targeted or area
  work.r6.u32 = caster;
  __imp__rex_CSpellRegistry_FindLive(work, base);
  return Plausible(work.r3.u32) ? work.r3.u32 : 0u;
}

extern "C" int32_t refii_mod_save_set_int(PPCContext* ctx, uint8_t* base, const char* key, int32_t value) {
  if (ctx == nullptr || !LuaIdentifier(key)) {
    return 0;
  }
  char chunk[160];
  std::snprintf(chunk, sizeof(chunk), "RefiiModSave = RefiiModSave or {} RefiiModSave.%s = %d", key, value);
  return refii_mod_run_game_lua(ctx, base, chunk);
}

extern "C" int32_t refii_mod_save_get_int(PPCContext* ctx, uint8_t* base, const char* key, int32_t lo, int32_t hi,
                                          int32_t* out) {
  if (ctx == nullptr || !LuaIdentifier(key)) {
    return 0;
  }
  char expr[160];
  std::snprintf(expr, sizeof(expr), "RefiiModSave and RefiiModSave.%s", key);
  return refii_mod_game_lua_int(ctx, base, expr, lo, hi, out);
}

// ---------------------------------------------------------------------------------------------
// Entity identity, factions, placement, gold, inventory, the level
// ---------------------------------------------------------------------------------------------

extern "C" uint32_t refii_mod_entity_definition(uint8_t* base, uint32_t entity) {
  return Plausible(entity) ? refii::mods::LoadU32(base, entity + kEntityDefinition) : 0u;
}

extern "C" int32_t refii_mod_entity_name(uint8_t* base, uint32_t entity, char* out, uint32_t size) {
  if (!Plausible(entity)) {
    if (out != nullptr && size != 0) {
      out[0] = 0;
    }
    return 0;
  }
  return CopyGuestString(base, LhStringText(base, entity + kEntityName), out, size) ? 1 : 0;
}

extern "C" int32_t refii_mod_relation(PPCContext* ctx, uint8_t* base, uint32_t entity, uint32_t other) {
  const uint32_t faction = ctx != nullptr ? refii_mod_component(base, entity, kFactionComponent) : 0u;
  if (faction == 0 || !Plausible(other)) {
    return 0;
  }
  PPCContext work = refii::mods::Frame(*ctx);
  work.r3.u32 = faction;
  work.r4.u32 = other;
  __imp__rex_CECFaction_RelationTo(work, base);
  return static_cast<int32_t>(work.r3.u32);
}

extern "C" int32_t refii_mod_set_position(PPCContext* ctx, uint8_t* base, uint32_t entity, const float* xyz) {
  if (ctx == nullptr || xyz == nullptr || !Plausible(entity)) {
    return 0;
  }
  const uint32_t placement = refii::mods::LoadU32(base, entity + kPlacement);
  if (!Plausible(placement)) {
    return 0;
  }
  PPCContext work = refii::mods::Frame(*ctx);
  refii::mods::SetVector(work, 1, xyz);
  return refii::mods::CallVirtual(work, base, placement, kSetPositionSlot) ? 1 : 0;
}

// THE TINT SETTER, sub_82573770(CECGraphicAppearance* r3, const float r4[4]) (disc sub_82548418,
// the same 48 instructions): `lvx128 v0,r0,r4` (16-byte aligned {r, g, b, a}), skips when every
// lane is within 1e-4 of +80 (`vcmpgefp.` against flt_82099A80 = 1e-4, 0x825737bc), else stores
// +80..+95 (0x825737e0, 0x825737e8), marks the component dirty (+0x90 |= 0x200) and calls
// sub_82573318, which packs the record the entity feed and co-op state.
extern "C" int32_t refii_mod_entity_tint(PPCContext* ctx, uint8_t* base, uint32_t entity, float r, float g, float b,
                                         float a) {
  const float lanes[4] = {r, g, b, a};
  for (const float lane : lanes) {
    if (!std::isfinite(lane)) {
      return 0;
    }
  }
  const uint32_t look = ctx != nullptr ? refii_mod_component(base, entity, kAppearanceComponent) : 0u;
  if (look == 0) {
    return 0;
  }
  // Scratch below the caller's frame, above the setter's own.
  const uint32_t frame = (ctx->r1.u32 - 0x100u) & ~0xFu;
  const uint32_t rgba = frame + 0x60u;
  for (uint32_t i = 0; i < 4u; ++i) {
    refii::mods::StoreF32(base, rgba + 4u * i, lanes[i] > 0.0f ? lanes[i] : 0.0f);
  }
  PPCContext work = *ctx;
  work.r1.u32 = frame;
  work.r3.u32 = look;
  work.r4.u32 = rgba;
  return refii::mods::CallGuest(work, base, refii::guest::Address(kSetTintVector)) ? 1 : 0;
}

extern "C" int32_t refii_mod_entity_untint(PPCContext* ctx, uint8_t* base, uint32_t entity) {
  return refii_mod_entity_tint(ctx, base, entity, 1.0f, 1.0f, 1.0f, 1.0f);
}

// ---------------------------------------------------------------------------------------------
// The other appearance setters (refii_mod.h states each setter's instructions).
// ---------------------------------------------------------------------------------------------

namespace {

/// `setter(comp3, f1 = value)`: the alpha and scale setters take their float in f1.
int32_t SetAppearanceFloat(PPCContext* ctx, uint8_t* base, uint32_t entity, uint32_t setter, float value) {
  if (ctx == nullptr || !std::isfinite(value)) {
    return 0;
  }
  const uint32_t look = refii_mod_component(base, entity, kAppearanceComponent);
  if (look == 0) {
    return 0;
  }
  PPCContext work = refii::mods::Frame(*ctx);
  work.r3.u32 = look;
  work.f1.f64 = static_cast<double>(value);
  return refii::mods::CallGuest(work, base, refii::guest::Address(setter)) ? 1 : 0;
}

/// `setter(comp3, r4 = byte)`: the saturation-mask and drawable setters take a byte in r4.
int32_t SetAppearanceByte(PPCContext* ctx, uint8_t* base, uint32_t entity, uint32_t setter, bool on) {
  const uint32_t look = ctx != nullptr ? refii_mod_component(base, entity, kAppearanceComponent) : 0u;
  if (look == 0) {
    return 0;
  }
  PPCContext work = refii::mods::Frame(*ctx);
  work.r3.u32 = look;
  work.r4.u64 = on ? 1u : 0u;
  return refii::mods::CallGuest(work, base, refii::guest::Address(setter)) ? 1 : 0;
}

}  // namespace

extern "C" int32_t refii_mod_entity_alpha(PPCContext* ctx, uint8_t* base, uint32_t entity, float alpha) {
  if (!std::isfinite(alpha)) {
    return 0;
  }
  return SetAppearanceFloat(ctx, base, entity, kSetAlpha, std::clamp(alpha, 0.0f, 1.0f));
}

extern "C" int32_t refii_mod_entity_scale(PPCContext* ctx, uint8_t* base, uint32_t entity, float scale) {
  // The setter tells the placement through its vtable (`lwz r3,0x7C(r11)` 0x825730f8, no null
  // test), so an entity without one would crash inside it.
  if (!(scale > 0.0f) || !std::isfinite(scale) || !Plausible(entity) ||
      !Plausible(refii::mods::LoadU32(base, entity + kPlacement))) {
    return 0;
  }
  return SetAppearanceFloat(ctx, base, entity, kSetScale, scale);
}

extern "C" int32_t refii_mod_entity_saturation_mask(PPCContext* ctx, uint8_t* base, uint32_t entity, int32_t on) {
  return SetAppearanceByte(ctx, base, entity, kSetSaturationMask, on != 0);
}

extern "C" int32_t refii_mod_entity_drawable(PPCContext* ctx, uint8_t* base, uint32_t entity, int32_t on) {
  return SetAppearanceByte(ctx, base, entity, kSetAsDrawable, on != 0);
}

// ---------------------------------------------------------------------------------------------
// The target lock (refii_mod.h states the three switches and their instructions).
// ---------------------------------------------------------------------------------------------

namespace {

struct LockParts {
  uint32_t hero = 0;
  uint32_t player = 0;     // component 33
  uint32_t targeting = 0;  // component 77
};

LockParts PartsOf(uint8_t* base, int32_t player_index) {
  LockParts parts;
  parts.hero = refii_mod_player_hero(base, player_index);
  if (parts.hero != 0) {
    parts.player = refii_mod_component(base, parts.hero, kPlayerComponent);
    parts.targeting = refii_mod_component(base, parts.hero, kTargetingComponent);
  }
  return parts;
}

/// The targeting component's own GetTarget (slot 17), 0 for none.
uint32_t TargetOf(PPCContext& ctx, uint8_t* base, uint32_t targeting) {
  PPCContext query = refii::mods::Frame(ctx);
  return targeting != 0 && refii::mods::CallVirtual(query, base, targeting, kTargetingGetTargetSlot) ? query.r3.u32
                                                                                                        : 0u;
}

/// The player's look-at flag through its own writer; false when the game has no body for it.
bool SetLookAt(PPCContext& ctx, uint8_t* base, uint32_t player, bool on) {
  PPCContext work = refii::mods::Frame(ctx);
  work.r3.u32 = player;
  work.r4.u64 = on ? 1u : 0u;
  rex_CPlayer_SetLookAtButtonDown(work, base);
  return true;
}

/// Whether the player's handle holds mode `mode`: `sub_821E8B90(*(comp33+0x14), mode)`.
bool HasPlayerMode(PPCContext& ctx, uint8_t* base, uint32_t player, uint32_t mode) {
  const uint32_t handle = player != 0 ? refii::mods::LoadU32(base, player + kPlayerHandle) : 0u;
  if (!Plausible(handle)) {
    return false;
  }
  PPCContext query = refii::mods::Frame(ctx);
  query.r3.u32 = handle;
  query.r4.u64 = mode;
  return refii::mods::CallGuest(query, base, refii::guest::Address(kHandleHasMode)) && (query.r3.u32 & 0xFFu) != 0;
}

}  // namespace

extern "C" int32_t refii_mod_lock_target(PPCContext* ctx, uint8_t* base, int32_t player, uint32_t entity) {
  const LockParts parts = ctx != nullptr ? PartsOf(base, player) : LockParts{};
  if (parts.player == 0 || parts.targeting == 0) {
    return 0;
  }
  if (entity != 0) {
    if (!refii_mod_entity_alive(base, entity)) {
      return 0;
    }
    PPCContext work = refii::mods::Frame(*ctx);
    work.r4.u32 = entity;
    refii::mods::CallVirtual(work, base, parts.targeting, kTargetingSetTargetSlot);
  }
  refii::mods::StoreU8(base, parts.targeting + kForceLockByte, 1u);
  SetLookAt(*ctx, base, parts.player, true);
  return 1;
}

extern "C" int32_t refii_mod_release_lock(PPCContext* ctx, uint8_t* base, int32_t player) {
  const LockParts parts = ctx != nullptr ? PartsOf(base, player) : LockParts{};
  if (parts.player == 0 || parts.targeting == 0) {
    return 0;
  }
  refii::mods::StoreU8(base, parts.targeting + kForceLockByte, 0u);
  SetLookAt(*ctx, base, parts.player, false);
  return 1;
}

extern "C" uint32_t refii_mod_switch_target(PPCContext* ctx, uint8_t* base, int32_t player, int32_t left) {
  const LockParts parts = ctx != nullptr ? PartsOf(base, player) : LockParts{};
  if (parts.targeting == 0) {
    return 0;
  }
  PPCContext work = refii::mods::Frame(*ctx);
  work.r4.u64 = left != 0 ? 1u : 0u;
  if (!refii::mods::CallVirtual(work, base, parts.targeting, kTargetingSwitchSlot)) {
    return 0;
  }
  return TargetOf(*ctx, base, parts.targeting);
}

extern "C" uint32_t refii_mod_locked_target(PPCContext* ctx, uint8_t* base, int32_t player) {
  const LockParts parts = ctx != nullptr ? PartsOf(base, player) : LockParts{};
  if (parts.player == 0 || parts.targeting == 0) {
    return 0;
  }
  const bool locked = refii::mods::LoadU8(base, parts.targeting + kForceLockByte) != 0 ||
                      HasPlayerMode(*ctx, base, parts.player, kTargetLockMode);
  return locked ? TargetOf(*ctx, base, parts.targeting) : 0u;
}

// ---------------------------------------------------------------------------------------------
// Cutscenes (core/refii_mod_events.h CutsceneEvent quotes each instruction).
// ---------------------------------------------------------------------------------------------

namespace {

/// comp33 +0x3B: `Player.SetForegroundCutsceneInEffect`'s `stb r4,0x3B(r3)` (0x823e72dc, disc 0x823c5d0c).
constexpr uint32_t kForegroundCutscene = 0x3B;
/// comp33 +0xC8: the LOOK_AT scope of `Player.SetOverrideCameraMode` (`stw r4,0xC8(r3)` 0x823e31cc /
/// 0x823e31e0, disc 0x823c1b94 / 0x823c1ba8); 0 is CAMERA_MODE_DEFAULT.
constexpr uint32_t kLookAtCameraMode = 0xC8;
/// `GUI.IsFullCutscenePlaying` (disc 0x82907D08): the top game-flow state's type == 7.
constexpr uint32_t kIsFullCutscenePlaying = 0x823060E0u;
/// The game-flow list it reads: *(*(*(*(game+12)+128)+4)+4); the node is *(list+4).
constexpr uint32_t kFlowChain[] = {12u, 128u, 4u, 4u};

/// The game's own `GUI.IsFullCutscenePlaying`, asked only past the empty-list test it traps on
/// (`cmplw r11,r10; bne; twui` 0x8230610c..14): false with no game, no flow list or no state.
bool FullCutscenePlaying(PPCContext& ctx, uint8_t* base) {
  uint32_t at = refii::mods::LoadU32(base, refii::guest::Address(kGameRoot));
  for (const uint32_t offset : kFlowChain) {
    if (!Plausible(at)) {
      return false;
    }
    at = refii::mods::LoadU32(base, at + offset);
  }
  if (!Plausible(at)) {
    return false;
  }
  const uint32_t node = refii::mods::LoadU32(base, at + 4u);
  if (node == at || !Plausible(node) || !Plausible(refii::mods::LoadU32(base, node + 8u))) {
    return false;
  }
  PPCContext query = refii::mods::Frame(ctx);
  return refii::mods::CallGuest(query, base, refii::guest::Address(kIsFullCutscenePlaying)) && (query.r3.u32 & 0xFFu) != 0;
}

/// The hero's own statements (INTERACTIVE, LOOK_AT) on its player component; 0 without one.
int32_t HeroCutsceneKinds(uint8_t* base, int32_t player) {
  const uint32_t hero = refii_mod_player_hero(base, player);
  const uint32_t comp = hero != 0 ? refii_mod_component(base, hero, kPlayerComponent) : 0u;
  if (comp == 0) {
    return 0;
  }
  int32_t kinds = 0;
  if (refii::mods::LoadU8(base, comp + kForegroundCutscene) != 0) {
    kinds |= REFII_CUTSCENE_INTERACTIVE;
  }
  if (refii::mods::LoadU32(base, comp + kLookAtCameraMode) != 0) {
    kinds |= REFII_CUTSCENE_LOOK_AT;
  }
  return kinds;
}

}  // namespace

extern "C" int32_t refii_mod_in_cutscene(PPCContext* ctx, uint8_t* base, int32_t player) {
  if (ctx == nullptr || base == nullptr) {
    return 0;
  }
  int32_t kinds = FullCutscenePlaying(*ctx, base) ? REFII_CUTSCENE_FULL : 0;
  if (player == 1 || player == 2) {
    kinds |= HeroCutsceneKinds(base, player);
  } else if (player == 0) {
    kinds |= HeroCutsceneKinds(base, 1) | HeroCutsceneKinds(base, 2);
  }
  return kinds;
}

namespace refii::vfs {

/// The tick's poll (mod_events.cpp): the full cutscene, and one player's own statements.
bool ModFullCutscenePlaying(PPCContext& ctx, uint8_t* base) { return FullCutscenePlaying(ctx, base); }
int32_t ModHeroCutsceneKinds(uint8_t* base, int32_t player) { return HeroCutsceneKinds(base, player); }

}  // namespace refii::vfs

// ---------------------------------------------------------------------------------------------
// Movement speed, actions, the living test, the slot table (refii_mod.h quotes each instruction).
// ---------------------------------------------------------------------------------------------

namespace {

/// comp33 +0x1E: `Player.SetPlayerMovementSpeedOverridden` (`stb r4,0x1E(r3)` 0x823dff78).
constexpr uint32_t kSpeedOverridden = 0x1E;
/// comp33 +0xCC: `Player.SetOverriddenMovementSpeedMultiplier` (`stfs f1,0xCC(r3)` 0x8283ecc8).
constexpr uint32_t kSpeedMultiplier = 0xCC;
constexpr uint32_t kActionManager = 49;
/// manager +0x10: the current action (`stw r28,0(r31)` 0x822a496c, r31 = manager+0x10).
constexpr uint32_t kManagerCurrentAction = 0x10;
/// action +0x4B stopped (`stb r11,0x4B(r30)` 0x82709ff8), +0x4C holds the permission back, +0x4D
/// the permission (`sub_822CB150` 0x822cb1b4..c4; the handler's `stb r25,0x4D(r26)` 0x82709cb8).
constexpr uint32_t kActionStopped = 0x4B;
constexpr uint32_t kActionNoInterrupt = 0x4C;
constexpr uint32_t kActionInterruptAllowed = 0x4D;
/// The action's clock, vtable slot 7 (`sub_8226D418`).
constexpr uint32_t kActionElapsedSlot = 7;
constexpr uint32_t kCreature = 20;
/// comp20 +0x18: 1 from the constructor (0x823a318c), 0 once rex_CECCreature_Die ran (0x823a4ec8).
constexpr uint32_t kCreatureNotDied = 0x18;
/// comp56 +0x80: the int8 [2][5] slot table (`sub_82541F30` writes both rows, the launch reads
/// row = its flag, `lbz r9,0x80(r10)` 0x8253f0e8).
constexpr uint32_t kSlotTable = 0x80;
constexpr int32_t kSpellSlots = 5;

uint32_t PlayerComponentOf(uint8_t* base, int32_t player) {
  const uint32_t hero = refii_mod_player_hero(base, player);
  return hero != 0 ? refii_mod_component(base, hero, kPlayerComponent) : 0u;
}

}  // namespace

extern "C" int32_t refii_mod_set_speed_multiplier(uint8_t* base, int32_t player, float factor) {
  const uint32_t component = PlayerComponentOf(base, player);
  if (component == 0 || !std::isfinite(factor) || factor < 0.0f) {
    return 0;
  }
  // The scripts' order: the multiplier first, then the byte the locomotion tests.
  refii::mods::StoreF32(base, component + kSpeedMultiplier, factor);
  refii::mods::StoreU8(base, component + kSpeedOverridden, 1u);
  return 1;
}

extern "C" int32_t refii_mod_clear_speed_multiplier(uint8_t* base, int32_t player) {
  const uint32_t component = PlayerComponentOf(base, player);
  if (component == 0) {
    return 0;
  }
  refii::mods::StoreU8(base, component + kSpeedOverridden, 0u);
  return 1;
}

extern "C" int32_t refii_mod_speed_multiplier(uint8_t* base, int32_t player, float* out) {
  const uint32_t component = PlayerComponentOf(base, player);
  if (component == 0) {
    return 0;
  }
  if (out != nullptr) {
    *out = refii::mods::LoadF32(base, component + kSpeedMultiplier);
  }
  return refii::mods::LoadU8(base, component + kSpeedOverridden) != 0 ? 1 : 0;
}

extern "C" uint32_t refii_mod_current_action(uint8_t* base, uint32_t entity) {
  const uint32_t manager = refii_mod_component(base, entity, kActionManager);
  const uint32_t action = manager != 0 ? refii::mods::LoadU32(base, manager + kManagerCurrentAction) : 0u;
  if (!Plausible(action) || refii::mods::LoadU8(base, action + kActionStopped) != 0) {
    return 0;
  }
  return action;
}

extern "C" int32_t refii_mod_action_elapsed(PPCContext* ctx, uint8_t* base, uint32_t action, float* out) {
  if (ctx == nullptr || !Plausible(action) || !Plausible(refii::mods::LoadU32(base, action))) {
    return 0;
  }
  PPCContext work = refii::mods::Frame(*ctx);
  if (!refii::mods::CallVirtual(work, base, action, kActionElapsedSlot)) {
    return 0;
  }
  if (out != nullptr) {
    *out = static_cast<float>(work.f1.f64);
  }
  return 1;
}

extern "C" int32_t refii_mod_action_interruptible(uint8_t* base, uint32_t action) {
  return Plausible(action) && refii::mods::LoadU8(base, action + kActionNoInterrupt) == 0 &&
                 refii::mods::LoadU8(base, action + kActionInterruptAllowed) != 0
             ? 1
             : 0;
}

extern "C" int32_t refii_mod_allow_action_interrupt(uint8_t* base, uint32_t action) {
  if (!Plausible(action) || refii::mods::LoadU8(base, action + kActionNoInterrupt) != 0) {
    return 0;
  }
  refii::mods::StoreU8(base, action + kActionInterruptAllowed, 1u);
  return 1;
}

extern "C" int32_t refii_mod_entity_living(uint8_t* base, uint32_t entity) {
  if (!refii_mod_entity_alive(base, entity)) {
    return 0;
  }
  const uint32_t creature = refii_mod_component(base, entity, kCreature);
  return creature == 0 || refii::mods::LoadU8(base, creature + kCreatureNotDied) != 0 ? 1 : 0;
}

extern "C" int32_t refii_mod_spell_in_slot(uint8_t* base, int32_t player, int32_t slot) {
  const uint32_t hero = refii_mod_player_hero(base, player);
  const uint32_t manager = hero != 0 ? refii_mod_component(base, hero, kSpellManagerComponent) : 0u;
  if (manager == 0 || slot < 0 || slot >= kSpellSlots) {
    return -1;
  }
  const int32_t type = static_cast<int8_t>(refii::mods::LoadU8(base, manager + kSlotTable + static_cast<uint32_t>(slot)));
  return type >= 0 && type <= 7 ? type : -1;
}

// ---------------------------------------------------------------------------------------------
// Status effects (refii_mod.h states each installer's registers and the bytes it reads).
// ---------------------------------------------------------------------------------------------

namespace {

/// The status component id for a kind, 0 for an unknown kind.
uint32_t StatusComponent(int32_t kind) {
  return kind >= 1 && kind <= 4 ? kStatusComponents[kind] : 0u;
}

constexpr int32_t kNoCastSerial = -1;

}  // namespace

extern "C" int32_t refii_mod_has_status(uint8_t* base, uint32_t entity, int32_t kind) {
  const uint32_t component = StatusComponent(kind);
  return component != 0 && refii_mod_component(base, entity, component) != 0 ? 1 : 0;
}

extern "C" int32_t refii_mod_apply_status(PPCContext* ctx, uint8_t* base, uint32_t entity, int32_t kind,
                                          float seconds, int32_t level, uint32_t source) {
  if (ctx == nullptr || !(seconds > 0.0f) || !std::isfinite(seconds) || !refii_mod_entity_alive(base, entity) ||
      StatusComponent(kind) == 0 || kind == REFII_STATUS_VORTEX) {
    return 0;
  }
  if (!Plausible(source) || !refii_mod_entity_alive(base, source)) {
    source = entity;
  }
  level = std::clamp(level, 0, 4);
  float xyz[3] = {0.0f, 0.0f, 0.0f};
  refii_mod_entity_position(ctx, base, entity, xyz);
  PPCContext work = refii::mods::Frame(*ctx, 0x200u);
  switch (kind) {
    case REFII_STATUS_ELECTRIFIED:
      work.r3.u32 = source;
      work.r4.u32 = entity;
      work.r5.s64 = kNoCastSerial;
      work.r6.s64 = level;
      work.f1.f64 = static_cast<double>(seconds);  // +40 duration
      work.f2.f64 = 0.0;                           // total damage: none
      work.f3.f64 = 0.5;                           // first tick delay (+60); moot at 0 damage
      work.f4.f64 = 0.5;                           // tick interval (+64)
      work.f5.f64 = 0.0;                           // +68
      rex_SpellLightning_Electrify(work, base);
      return 1;
    case REFII_STATUS_CHAOS:
      work.r3.u32 = source;
      work.r4.u32 = entity;
      work.r5.s64 = kNoCastSerial;
      work.r6.u64 = 0;
      work.r7.u64 = refii_mod_component(base, entity, kStatusCreatureComponent) != 0 ? 1u : 0u;
      work.r8.u64 = 0;  // not a replica: in co-op the game sends it itself
      work.f1.f64 = static_cast<double>(seconds);
      refii::mods::SetVector(work, 1, xyz);
      rex_SpellChaos_ApplyToVictim(work, base);
      return 1;
    case REFII_STATUS_BURNING: {
      // The three bytes the installer reads from ITS CALLER's frame (0x120+arg_6F/74/7F): the
      // frame `work` stands on is that caller's.
      refii::mods::StoreU8(base, work.r1.u32 + 0x6Fu, 1u);
      refii::mods::StoreU32(base, work.r1.u32 + 0x74u, 0u);
      refii::mods::StoreU8(base, work.r1.u32 + 0x7Fu, 1u);
      work.r3.u32 = entity;
      work.r4.u32 = source;
      work.r5.s64 = kNoCastSerial;
      work.r6.s64 = level;
      work.f1.f64 = 1.0;                           // strength, at distance 0
      work.f2.f64 = 0.0;                           // no falloff
      work.f3.f64 = static_cast<double>(seconds);  // burn time
      work.f4.f64 = 0.0;                           // burn damage: none
      work.f5.f64 = 1.0;                           // heat
      refii::mods::SetVector(work, 1, xyz);
      rex_SpellFireball_Ignite(work, base);
      return 1;
    }
    default:
      return 0;
  }
}

extern "C" int32_t refii_mod_clear_status(PPCContext* ctx, uint8_t* base, uint32_t entity, int32_t kind) {
  if (ctx == nullptr || refii_mod_has_status(base, entity, kind) == 0) {
    return 0;
  }
  uint32_t remover = 0;
  switch (kind) {
    case REFII_STATUS_ELECTRIFIED:
      remover = kRemoveElectrified;
      break;
    case REFII_STATUS_CHAOS:
      remover = kRemoveChaos;
      break;
    case REFII_STATUS_BURNING:
      remover = kRemoveBurning;
      break;
    default:
      return 0;
  }
  PPCContext work = refii::mods::Frame(*ctx);
  work.r3.u32 = entity;
  return refii::mods::CallGuest(work, base, refii::guest::Address(remover)) ? 1 : 0;
}

// ---------------------------------------------------------------------------------------------
// Knockback and stagger: one hit record, the Force Push's shape (refii_mod.h quotes the builder).
// ---------------------------------------------------------------------------------------------

namespace {

/// The hit this thread is delivering through refii_mod_knockback / refii_mod_stagger, or 0.
thread_local uint32_t g_stated_hit = 0;

/// Builds the record on `frame` and delivers it; `vector` null for a stagger.
int32_t DeliverStatedHit(PPCContext* ctx, uint8_t* base, uint32_t entity, const float* vector) {
  if (ctx == nullptr || !refii_mod_entity_alive(base, entity)) {
    return 0;
  }
  const uint32_t receiver = refii_mod_component(base, entity, kHitReceiverComponent);
  // The receiver's own gates (0x825c5248..0x825c5278): a hit of our kind is dropped unless both
  // bytes stand, and a dropped hit would still have been built and destroyed — so refuse first.
  if (receiver == 0 || refii::mods::LoadU8(base, receiver + 0x18u) == 0 ||
      refii::mods::LoadU8(base, receiver + 0x19u) == 0) {
    return 0;
  }
  float xyz[3] = {0.0f, 0.0f, 0.0f};
  refii_mod_entity_position(ctx, base, entity, xyz);

  // Scratch below the caller's frame, above the callees' own and above the register home area a
  // callee may spill into (r1+0x54..0x94): the record, then the name.
  PPCContext work = refii::mods::Frame(*ctx, 0x300u);
  const uint32_t hit = work.r1.u32 + 0x100u;
  const uint32_t name = hit + kHitBytes;
  work.r3.u32 = hit;
  if (!refii::mods::CallGuest(work, base, refii::guest::Address(kHitCtor))) {
    return 0;
  }
  refii::mods::StoreF32(base, hit + kHitDamage, 0.0f);
  refii::mods::StoreU32(base, hit + kHitSerial, static_cast<uint32_t>(kNoCastSerial));
  if (vector != nullptr) {
    refii::mods::StoreU8(base, hit + kHitKnockdown, 1u);
    refii::mods::StoreU8(base, hit + kHitUnblockable, 1u);
    refii::mods::StoreU32(base, hit + kHitFlags, kForcePushFlag);
    refii::mods::StoreU32(base, hit + kHitKindA, 3u);
    refii::mods::StoreU32(base, hit + kHitSpellType, static_cast<uint32_t>(kForcePushType));
    refii::mods::StoreU32(base, hit + kHitKindB, 1u);
    for (uint32_t i = 0; i < 3u; ++i) {
      refii::mods::StoreF32(base, hit + kHitOrigin + 4u * i, xyz[i] - vector[i]);
      refii::mods::StoreF32(base, hit + kHitDirection + 4u * i, vector[i]);
      refii::mods::StoreF32(base, hit + kHitDirectionBack + 4u * i, -vector[i]);
    }
    refii::mods::StoreF32(base, hit + kHitOrigin + 12u, 0.0f);
    refii::mods::StoreF32(base, hit + kHitDirection + 12u, 0.0f);
    refii::mods::StoreF32(base, hit + kHitDirectionBack + 12u, 0.0f);
    static constexpr char kResponse[] = "ResponseToKnockdownUp";
    std::memcpy(base + name, kResponse, sizeof(kResponse));
    PPCContext naming = work;
    naming.r3.u32 = hit + kHitResponseName;
    naming.r4.u32 = name;
    refii::mods::CallGuest(naming, base, refii::guest::Address(kHitSetName));
  }

  PPCContext deliver = work;
  deliver.r3.u32 = receiver;
  deliver.r4.u32 = hit;
  deliver.r5.u64 = 1;
  g_stated_hit = hit;
  rex_CECHitReceiver_OnHit(deliver, base);
  g_stated_hit = 0;

  PPCContext destroy = work;
  destroy.r3.u32 = hit;
  refii::mods::CallGuest(destroy, base, refii::guest::Address(kHitDtor));
  return 1;
}

}  // namespace

bool refii::vfs::IsModStatedHit(uint32_t hit) { return hit != 0 && hit == g_stated_hit; }

extern "C" int32_t refii_mod_knockback(PPCContext* ctx, uint8_t* base, uint32_t entity, const float* dir, float force) {
  if (dir == nullptr || !std::isfinite(force)) {
    return 0;
  }
  float vector[3];
  for (int i = 0; i < 3; ++i) {
    if (!std::isfinite(dir[i])) {
      return 0;
    }
    vector[i] = dir[i] * force;
  }
  if (vector[0] == 0.0f && vector[1] == 0.0f && vector[2] == 0.0f) {
    return 0;
  }
  return DeliverStatedHit(ctx, base, entity, vector);
}

extern "C" int32_t refii_mod_stagger(PPCContext* ctx, uint8_t* base, uint32_t entity) {
  return DeliverStatedHit(ctx, base, entity, nullptr);
}

// ---------------------------------------------------------------------------------------------
// Sound: the game's own cue player and its music box (refii_mod.h quotes each body).
// ---------------------------------------------------------------------------------------------

namespace {

constexpr uint32_t kSoundManager = 0x83496D9Cu;       // disc 0x83496BCC: the manager the natives use
constexpr uint32_t kLhStringFromChars = 0x8222CED0u;  // (slot, chars)      disc 0x8222CF18
constexpr uint32_t kLhStringRelease = 0x82214DD8u;    // (slot)             disc 0x82214F08
constexpr uint32_t kCueCtor = 0x822671A8u;            // (cue, slot)        disc 0x82267168
constexpr uint32_t kCueDtor = 0x82289A48u;            // (cue)              disc 0x82289530
constexpr uint32_t kCueKnown = 0x82287DE0u;           // (manager+12, hash) disc 0x822878C8
constexpr uint32_t kPlayOnComponent = 0x821D9608u;    // (out, comp135, cue, tag slot) disc 0x821D9258
constexpr uint32_t kPlayOnEmitter = 0x821E0F48u;      // (out, emitter, cue, tag slot) disc 0x821E0B08
constexpr uint32_t kReleaseVoice = 0x82C05EF8u;       // (out)              disc 0x822ADE08
constexpr uint32_t kCreateEmitter = 0x829FD018u;      // (manager, v1 xyz)  disc 0x829F8250
constexpr uint32_t kFreeEmitter = 0x829FD168u;        // (manager, emitter) disc 0x829F83A0
constexpr uint32_t kStopTagged = 0x829FDEF0u;         // (emitter, tag slot, fade ms) disc 0x829F9128
constexpr uint32_t kPlayMusic = 0x8255BB28u;          // (name slot)        disc 0x825204B0
constexpr uint32_t kStopMusic = 0x8255BC20u;          // (name slot)        disc 0x825205A8
constexpr uint32_t kSoundComponent = 135;             // what PlayEventOnHero looks up (0x823ba99c)
constexpr uint32_t kComponentEmitter = 44;            // comp135 + 44, the emitter `sub_821D9608` makes
constexpr uint32_t kCueHash = 8;                      // cue + 8: `sub_82265220`'s a1[2]
constexpr uint32_t kPointEmitters = 8;

// The scratch a sound call lays out above the callees' spill area (Frame(ctx, 0x300)).
constexpr uint32_t kCueSlot = 0x100;
constexpr uint32_t kTagSlot = 0x110;
constexpr uint32_t kVoiceOut = 0x120;
constexpr uint32_t kCueRecord = 0x130;  // 72 bytes (`sub_82275380` writes to +65)
constexpr uint32_t kCueChars = 0x180;   // 128
constexpr uint32_t kTagChars = 0x200;   // 128
constexpr uint32_t kPointVector = 0x280;
constexpr uint32_t kNameBytes = 128;

/// A cue, tag or music name: printable ASCII without spaces, 1..127 bytes.
bool SoundName(const char* name, bool empty_ok) {
  if (name == nullptr) {
    return false;
  }
  const size_t n = std::strlen(name);
  if (n >= kNameBytes || (n == 0 && !empty_ok)) {
    return false;
  }
  for (size_t i = 0; i < n; ++i) {
    const unsigned char c = static_cast<unsigned char>(name[i]);
    if (c <= 0x20 || c >= 0x7F) {
      return false;
    }
  }
  return true;
}

uint32_t SoundManager(uint8_t* base) {
  const uint32_t manager = refii::mods::LoadU32(base, refii::guest::Address(kSoundManager));
  return Plausible(manager) ? manager : 0u;
}

/// An LHString slot on the frame holding `text`; released by ReleaseLhString.
bool MakeLhString(PPCContext& work, uint8_t* base, uint32_t slot, uint32_t chars, const char* text) {
  const size_t n = std::strlen(text);
  std::memcpy(base + chars, text, n + 1);
  refii::mods::StoreU32(base, slot, 0u);
  PPCContext call = work;
  call.r3.u32 = slot;
  call.r4.u32 = chars;
  call.r5.u64 = 0xFFFFFFFFu;  // NUL-terminated
  return refii::mods::CallGuest(call, base, refii::guest::Address(kLhStringFromChars));
}

void ReleaseLhString(PPCContext& work, uint8_t* base, uint32_t slot) {
  PPCContext call = work;
  call.r3.u32 = slot;
  refii::mods::CallGuest(call, base, refii::guest::Address(kLhStringRelease));
}

/// The point emitters refii_mod_play_sound_at made, oldest first; a slot is reused by its index.
uint32_t g_point_emitters[kPointEmitters] = {};
uint32_t g_point_next = 0;

/// Whether `emitter` is still in the manager's list (+4 the sentinel, nodes {next, prev, emitter}):
/// the game frees emitters of its own accord, and a freed one must not be walked.
bool EmitterListed(uint8_t* base, uint32_t manager, uint32_t emitter) {
  const uint32_t sentinel = refii::mods::LoadU32(base, manager + 4u);
  if (!Plausible(sentinel) || emitter == 0u) {
    return false;
  }
  uint32_t node = refii::mods::LoadU32(base, sentinel);
  for (uint32_t guard = 0; guard < 4096u && Plausible(node) && node != sentinel; ++guard) {
    if (refii::mods::LoadU32(base, node + 8u) == emitter) {
      return true;
    }
    node = refii::mods::LoadU32(base, node);
  }
  return false;
}

/// Builds the cue record and the tag on `work`'s frame and starts the voice on `target` through
/// `player` (the component player or the emitter player); the voice released as the native does.
bool PlayCue(PPCContext& work, uint8_t* base, uint32_t player, uint32_t target, const char* cue, const char* tag) {
  const uint32_t frame = work.r1.u32;
  if (!MakeLhString(work, base, frame + kCueSlot, frame + kCueChars, cue)) {
    return false;
  }
  if (!MakeLhString(work, base, frame + kTagSlot, frame + kTagChars, tag)) {
    ReleaseLhString(work, base, frame + kCueSlot);
    return false;
  }
  bool started = false;
  PPCContext call = work;
  call.r3.u32 = frame + kCueRecord;
  call.r4.u32 = frame + kCueSlot;
  if (refii::mods::CallGuest(call, base, refii::guest::Address(kCueCtor))) {
    bool known = true;
    if (player == kPlayOnEmitter) {
      // The component player checks the hash itself (0x821d9698); the emitter player does not.
      PPCContext ask = work;
      ask.r3.u32 = SoundManager(base) + 12u;
      ask.r4.u32 = refii::mods::LoadU32(base, frame + kCueRecord + kCueHash);
      known = refii::mods::CallGuest(ask, base, refii::guest::Address(kCueKnown)) && (ask.r3.u32 & 0xFFu) != 0;
    }
    if (known) {
      refii::mods::StoreU32(base, frame + kVoiceOut, 0u);
      refii::mods::StoreU32(base, frame + kVoiceOut + 4u, 0u);
      PPCContext play = work;
      play.r3.u32 = frame + kVoiceOut;
      play.r4.u32 = target;
      play.r5.u32 = frame + kCueRecord;
      play.r6.u32 = frame + kTagSlot;
      if (refii::mods::CallGuest(play, base, refii::guest::Address(player))) {
        started = refii::mods::LoadU32(base, frame + kVoiceOut) != 0u;
        PPCContext release = work;
        release.r3.u32 = frame + kVoiceOut;
        refii::mods::CallGuest(release, base, refii::guest::Address(kReleaseVoice));
      }
    }
    PPCContext destroy = work;
    destroy.r3.u32 = frame + kCueRecord;
    refii::mods::CallGuest(destroy, base, refii::guest::Address(kCueDtor));
  }
  ReleaseLhString(work, base, frame + kTagSlot);
  ReleaseLhString(work, base, frame + kCueSlot);
  return started;
}

int32_t StopTagged(PPCContext& work, uint8_t* base, uint32_t emitter, const char* tag, int32_t fade_ms) {
  const uint32_t frame = work.r1.u32;
  if (!MakeLhString(work, base, frame + kTagSlot, frame + kTagChars, tag)) {
    return 0;
  }
  PPCContext call = work;
  call.r3.u32 = emitter;
  call.r4.u32 = frame + kTagSlot;
  call.r5.s64 = fade_ms < 0 ? 0 : fade_ms;
  const bool ok = refii::mods::CallGuest(call, base, refii::guest::Address(kStopTagged));
  ReleaseLhString(work, base, frame + kTagSlot);
  return ok ? 1 : 0;
}

int32_t MusicCall(PPCContext* ctx, uint8_t* base, uint32_t function, const char* name) {
  if (ctx == nullptr || !SoundName(name, false) || SoundManager(base) == 0u) {
    return 0;
  }
  PPCContext work = refii::mods::Frame(*ctx, 0x300u);
  const uint32_t frame = work.r1.u32;
  if (!MakeLhString(work, base, frame + kCueSlot, frame + kCueChars, name)) {
    return 0;
  }
  PPCContext call = work;
  call.r3.u32 = frame + kCueSlot;
  const bool ran = refii::mods::CallGuest(call, base, refii::guest::Address(function));
  ReleaseLhString(work, base, frame + kCueSlot);
  return ran && (function != kPlayMusic || call.r3.u32 != 0u) ? 1 : 0;
}

}  // namespace

extern "C" int32_t refii_mod_play_sound_at(PPCContext* ctx, uint8_t* base, const float* xyz, const char* cue,
                                           const char* tag) {
  if (ctx == nullptr || xyz == nullptr || !SoundName(cue, false) || !SoundName(tag == nullptr ? "" : tag, true)) {
    return 0;
  }
  for (int i = 0; i < 3; ++i) {
    if (!std::isfinite(xyz[i])) {
      return 0;
    }
  }
  const uint32_t manager = SoundManager(base);
  if (manager == 0u) {
    return 0;
  }
  PPCContext work = refii::mods::Frame(*ctx, 0x300u);
  // The oldest of our emitters goes back to the manager before its slot is reused.
  uint32_t& slot = g_point_emitters[g_point_next % kPointEmitters];
  if (slot != 0u && EmitterListed(base, manager, slot)) {
    PPCContext give = work;
    give.r3.u32 = manager;
    give.r4.u32 = slot;
    refii::mods::CallGuest(give, base, refii::guest::Address(kFreeEmitter));
  }
  slot = 0u;
  PPCContext create = work;
  create.r3.u32 = manager;
  refii::mods::SetVector(create, 1, xyz);
  if (!refii::mods::CallGuest(create, base, refii::guest::Address(kCreateEmitter)) || !Plausible(create.r3.u32)) {
    return 0;
  }
  slot = create.r3.u32;
  g_point_next = (g_point_next + 1u) % kPointEmitters;
  return PlayCue(work, base, kPlayOnEmitter, slot, cue, tag == nullptr ? "" : tag) ? 1 : 0;
}

extern "C" int32_t refii_mod_play_sound_on(PPCContext* ctx, uint8_t* base, uint32_t entity, const char* cue,
                                           const char* tag) {
  if (ctx == nullptr || !refii_mod_entity_alive(base, entity) || !SoundName(cue, false) ||
      !SoundName(tag == nullptr ? "" : tag, true) || SoundManager(base) == 0u) {
    return 0;
  }
  const uint32_t sound = refii_mod_component(base, entity, kSoundComponent);
  if (sound == 0u) {
    float xyz[3] = {0.0f, 0.0f, 0.0f};
    return refii_mod_entity_position(ctx, base, entity, xyz) != 0 ? refii_mod_play_sound_at(ctx, base, xyz, cue, tag) : 0;
  }
  PPCContext work = refii::mods::Frame(*ctx, 0x300u);
  return PlayCue(work, base, kPlayOnComponent, sound, cue, tag == nullptr ? "" : tag) ? 1 : 0;
}

extern "C" int32_t refii_mod_stop_sound(PPCContext* ctx, uint8_t* base, uint32_t entity, const char* tag,
                                        int32_t fade_ms) {
  if (ctx == nullptr || !SoundName(tag == nullptr ? "" : tag, true)) {
    return 0;
  }
  const uint32_t manager = SoundManager(base);
  if (manager == 0u) {
    return 0;
  }
  PPCContext work = refii::mods::Frame(*ctx, 0x300u);
  if (entity != 0u) {
    const uint32_t sound = refii_mod_entity_alive(base, entity) != 0 ? refii_mod_component(base, entity, kSoundComponent) : 0u;
    const uint32_t emitter = sound != 0u ? refii::mods::LoadU32(base, sound + kComponentEmitter) : 0u;
    return Plausible(emitter) ? StopTagged(work, base, emitter, tag == nullptr ? "" : tag, fade_ms) : 0;
  }
  int32_t walked = 0;
  for (uint32_t emitter : g_point_emitters) {
    if (emitter != 0u && EmitterListed(base, manager, emitter)) {
      walked |= StopTagged(work, base, emitter, tag == nullptr ? "" : tag, fade_ms);
    }
  }
  return walked;
}

extern "C" int32_t refii_mod_play_music(PPCContext* ctx, uint8_t* base, const char* name) {
  return MusicCall(ctx, base, kPlayMusic, name);
}

extern "C" int32_t refii_mod_stop_music(PPCContext* ctx, uint8_t* base, const char* name) {
  return MusicCall(ctx, base, kStopMusic, name);
}

// ---------------------------------------------------------------------------------------------
// Animation: the scripted action the shipped AI plays a named animation through.
// ---------------------------------------------------------------------------------------------

namespace {

/// An animation record name as the action table takes it: letters, digits and '_', 1..63 bytes.
bool AnimationName(const char* name) {
  if (name == nullptr || *name == 0) {
    return false;
  }
  size_t n = 0;
  for (const char* c = name; *c != 0; ++c, ++n) {
    const bool ok = (*c >= 'a' && *c <= 'z') || (*c >= 'A' && *c <= 'Z') || (*c >= '0' && *c <= '9') || *c == '_';
    if (!ok || n >= 63u) {
      return false;
    }
  }
  return true;
}

}  // namespace

extern "C" int32_t refii_mod_play_animation(PPCContext* ctx, uint8_t* base, uint32_t entity, const char* name,
                                            int32_t loops, float blend_seconds, float speed) {
  if (ctx == nullptr || !AnimationName(name) || !refii_mod_entity_alive(base, entity) || !std::isfinite(blend_seconds) ||
      !std::isfinite(speed) || !(speed > 0.0f)) {
    return 0;
  }
  if (blend_seconds < 0.0f) {
    blend_seconds = 0.0f;
  }
  char chunk[512];
  // BehaviourBase:PlayAnimation's table, and PlayLoopedAnimation's batch around it for loops.
  std::snprintf(chunk, sizeof(chunk),
                "local e = Refii.entity(%u) if e then "
                "local a = { Type = EScriptableAction.PLAY_ANIMATION, Anim = \"%s\", OverrideLooking = true, "
                "SpeedMultiplier = %.4f, BlendInTime = %.4f } "
                "if %d > 1 then a = { Type = EScriptableAction.BATCH, LoopAction = a, NumLoops = %d, KillFXOnFinish = true } end "
                "Action.SetCurrentAction(e, a) end",
                refii_mod_handle(entity), name, static_cast<double>(speed), static_cast<double>(blend_seconds), loops, loops);
  return refii_mod_run_game_lua(ctx, base, chunk);
}

extern "C" int32_t refii_mod_stop_animation(PPCContext* ctx, uint8_t* base, uint32_t entity) {
  if (ctx == nullptr || !refii_mod_entity_alive(base, entity)) {
    return 0;
  }
  char chunk[128];
  std::snprintf(chunk, sizeof(chunk), "local e = Refii.entity(%u) if e then Action.FinishScriptedActions(e) end",
                refii_mod_handle(entity));
  return refii_mod_run_game_lua(ctx, base, chunk);
}

// ---------------------------------------------------------------------------------------------
// Opinion and deeds: the game's own setters, per hero (refii_mod.h quotes each).
// ---------------------------------------------------------------------------------------------

extern "C" int32_t refii_mod_opinion_enter(PPCContext* ctx, uint8_t* base, uint32_t villager, uint32_t hero);
extern "C" void refii_mod_opinion_leave(void);

extern "C" int32_t refii_mod_modify_opinion(PPCContext* ctx, uint8_t* base, uint32_t villager, uint32_t hero,
                                            int32_t axis, float delta) {
  const uint32_t opinion = ctx != nullptr && refii_mod_entity_alive(base, villager) != 0 ? refii_mod_component(base, villager, 47u) : 0u;
  if (opinion == 0u || axis < 0 || axis > 4 || !std::isfinite(delta) || !refii_mod_entity_alive(base, hero)) {
    return 0;
  }
  const bool scoped = refii_mod_opinion_enter(ctx, base, villager, hero) != 0;
  PPCContext work = refii::mods::Frame(*ctx);
  work.r3.u32 = opinion;
  work.r4.s64 = axis;
  work.f1.f64 = static_cast<double>(delta);
  rex_CECOpinionReaction_ModifyAxisValue(work, base);
  if (scoped) {
    refii_mod_opinion_leave();
  }
  return 1;
}

extern "C" int32_t refii_mod_add_deed(PPCContext* ctx, uint8_t* base, uint32_t villager, uint32_t hero, uint32_t deed) {
  if (ctx == nullptr || deed == 0u || !refii_mod_entity_alive(base, villager) || !refii_mod_entity_alive(base, hero) ||
      refii_mod_component(base, villager, 47u) == 0u) {
    return 0;
  }
  // The deed record, by the hash a mod names it with; its id is the record's first word.
  const uint32_t gdb = refii::vfs::LoadGuestU32(base, refii::guest::Address(0x83496DA0u));
  if (!Plausible(gdb)) {
    return 0;
  }
  PPCContext lookup = refii::mods::Frame(*ctx);
  lookup.r3.u32 = gdb;
  lookup.r4.u32 = deed;
  rex_GDBManager_FindEntryByHash(lookup, base);
  if (!Plausible(lookup.r3.u32)) {
    return 0;
  }
  const uint32_t id = refii::mods::LoadU32(base, lookup.r3.u32);
  // The message system, as the game reaches it (0x823ff8f4): *(*(*(root+12)+88)+4)+16.
  const uint32_t root = refii::mods::LoadU32(base, refii::guest::Address(kGameRoot));
  const uint32_t game = Plausible(root) ? refii::mods::LoadU32(base, root + 12u) : 0u;
  const uint32_t manager = Plausible(game) ? refii::mods::LoadU32(base, game + 88u) : 0u;
  const uint32_t cell = Plausible(manager) ? refii::mods::LoadU32(base, manager + 4u) : 0u;
  const uint32_t messages = Plausible(cell) ? refii::mods::LoadU32(base, cell + 16u) : 0u;
  float xyz[3] = {0.0f, 0.0f, 0.0f};
  if (!Plausible(messages) || refii_mod_entity_position(ctx, base, hero, xyz) == 0) {
    return 0;
  }
  PPCContext post = refii::mods::Frame(*ctx, 0x200u);
  const uint32_t pos = post.r1.u32 + 0x100u;
  for (uint32_t i = 0; i < 3u; ++i) {
    refii::mods::StoreF32(base, pos + 4u * i, xyz[i]);
  }
  refii::mods::StoreF32(base, pos + 12u, 0.0f);
  post.r3.u32 = messages;
  post.r4.u64 = 50u;  // MESSAGE_EVENT_DEED_PERSON (miscellaneous/messageeventenum.lua)
  post.r5.u32 = pos;
  post.r6.u32 = hero;
  post.r7.u32 = villager;
  post.r8.u64 = 101u;
  post.r9.u32 = id;
  return refii::mods::CallGuest(post, base, refii::guest::Address(0x82279B80u)) ? 1 : 0;
}

// ---------------------------------------------------------------------------------------------
// NPC behaviour: the quests' own orders, run in the game VM (refii_mod.h quotes each entry).
// ---------------------------------------------------------------------------------------------

namespace {

/// Runs `body` with `e` = entity's game userdata and `t` = other's (nil when other is 0);
/// refused before the VM for a dead entity or other.
int32_t NpcChunk(PPCContext* ctx, uint8_t* base, uint32_t entity, uint32_t other, const char* body) {
  if (ctx == nullptr || !refii_mod_entity_alive(base, entity) || (other != 0u && !refii_mod_entity_alive(base, other))) {
    return 0;
  }
  char chunk[768];
  std::snprintf(chunk, sizeof(chunk),
                "local e = Refii.entity(%u) local t = Refii.entity(%u) if e and (t or %u == 0) then %s end",
                refii_mod_handle(entity), other != 0u ? refii_mod_handle(other) : 0u, other != 0u ? 1u : 0u, body);
  return refii_mod_run_game_lua(ctx, base, chunk);
}

}  // namespace

extern "C" int32_t refii_mod_npc_follow(PPCContext* ctx, uint8_t* base, uint32_t entity, uint32_t target, float radius) {
  if (target == 0u || entity == target || !std::isfinite(radius)) {
    return 0;
  }
  char body[160];
  // qr_assassination.lua: Follow.FollowEntity(GuardA, TargetNPC, 2.0).
  std::snprintf(body, sizeof(body), "if Follow.IsAvailable(e) then Follow.FollowEntity(e, t, %.3f) end",
                static_cast<double>(radius > 0.0f ? radius : 2.0f));
  return NpcChunk(ctx, base, entity, target, body);
}

extern "C" int32_t refii_mod_npc_go_to(PPCContext* ctx, uint8_t* base, uint32_t entity, const float* xyz) {
  if (xyz == nullptr || !std::isfinite(xyz[0]) || !std::isfinite(xyz[1]) || !std::isfinite(xyz[2])) {
    return 0;
  }
  char body[200];
  // quests/miscfunctions.lua main.25, as qc080_monksquest.lua calls it (wait = false: no yield).
  std::snprintf(body, sizeof(body),
                "ScriptFunction.StartScriptControlledMovement(e, CVector3(%.3f, %.3f, %.3f), false)",
                static_cast<double>(xyz[0]), static_cast<double>(xyz[1]), static_cast<double>(xyz[2]));
  return NpcChunk(ctx, base, entity, 0u, body);
}

extern "C" int32_t refii_mod_npc_flee(PPCContext* ctx, uint8_t* base, uint32_t entity, uint32_t from) {
  if (from == 0u || entity == from) {
    return 0;
  }
  // ai/aibase.lua main.41, BehaviourBase:FleeFromEntityNoWait's call.
  return NpcChunk(ctx, base, entity, from,
                  "if Navigation.IsAvailable(e) then Navigation.FleeFromEntity(e, t, ENavigationSpeed.NAV_SPEED_SPRINT, true) end");
}

extern "C" int32_t refii_mod_npc_attack(PPCContext* ctx, uint8_t* base, uint32_t entity, uint32_t target) {
  if (target == 0u || entity == target) {
    return 0;
  }
  // ai/combatregister.lua main.19 (ignore the chase radius; drop the target once unattackable).
  return NpcChunk(ctx, base, entity, target, "CombatRegister:SetScriptTarget(e, t, true, false)");
}

extern "C" int32_t refii_mod_npc_set_hostile(PPCContext* ctx, uint8_t* base, uint32_t entity, uint32_t other, int32_t on) {
  if (other == 0u || entity == other) {
    return 0;
  }
  return NpcChunk(ctx, base, entity, other,
                  on != 0
                      ? "if Faction.IsAvailable(e) then Faction.AddTemporaryEntityRelationship(e, t, EFactionStatus.FACTION_STATUS_ENEMY) end"
                      : "if Faction.IsAvailable(e) then Faction.RemoveTemporaryEntityRelationship(e, t) end");
}

extern "C" int32_t refii_mod_npc_stop(PPCContext* ctx, uint8_t* base, uint32_t entity) {
  return NpcChunk(ctx, base, entity, 0u,
                  "if Follow.IsAvailable(e) then Follow.StopFollowing(e) end "
                  "ScriptFunction.StopScriptControlledMovement(e) CombatRegister:ClearScriptTarget(e)");
}

// ---------------------------------------------------------------------------------------------
// Time of day and weather (refii_mod.h quotes the setter and the blend).
// ---------------------------------------------------------------------------------------------

namespace {

constexpr uint32_t kSetTimeOfDayAbsolute = 0x82515AA0u;  // disc 0x8249A790

/// The time manager the Timing natives are bound to (0x82459000..14): the world cell's +0x14
/// (+0x10 is the message system refii_mod_add_deed posts through).
uint32_t TimeManager(uint8_t* base) {
  const uint32_t root = refii::mods::LoadU32(base, refii::guest::Address(kGameRoot));
  const uint32_t game = Plausible(root) ? refii::mods::LoadU32(base, root + 0xCu) : 0u;
  const uint32_t world = Plausible(game) ? refii::mods::LoadU32(base, game + 0x58u) : 0u;
  const uint32_t cell = Plausible(world) ? refii::mods::LoadU32(base, world + 4u) : 0u;
  const uint32_t manager = Plausible(cell) ? refii::mods::LoadU32(base, cell + 0x14u) : 0u;
  return Plausible(manager) ? manager : 0u;
}

/// An environment-theme record name: letters, digits and '_', 1..63 bytes.
bool ThemeName(const char* name) {
  if (name == nullptr || *name == 0) {
    return false;
  }
  size_t n = 0;
  for (const char* c = name; *c != 0; ++c, ++n) {
    const bool ok = (*c >= 'a' && *c <= 'z') || (*c >= 'A' && *c <= 'Z') || (*c >= '0' && *c <= '9') || *c == '_';
    if (!ok || n >= 63u) {
      return false;
    }
  }
  return true;
}

}  // namespace

extern "C" int32_t refii_mod_set_time_of_day(PPCContext* ctx, uint8_t* base, float hours) {
  const uint32_t manager = ctx != nullptr && std::isfinite(hours) ? TimeManager(base) : 0u;
  if (manager == 0u) {
    return 0;
  }
  float fraction = hours / 24.0f;
  fraction = fraction < 0.0f ? 0.0f : (fraction > 1.0f ? 1.0f : fraction);
  PPCContext work = refii::mods::Frame(*ctx);
  work.r3.u32 = manager;
  work.f1.f64 = static_cast<double>(fraction);
  return refii::mods::CallGuest(work, base, refii::guest::Address(kSetTimeOfDayAbsolute)) ? 1 : 0;
}

extern "C" int32_t refii_mod_time_of_day(uint8_t* base, float* hours) {
  const uint32_t manager = TimeManager(base);
  if (hours != nullptr) {
    *hours = manager != 0u ? refii::mods::LoadF32(base, manager + 4u) * 24.0f : 0.0f;
  }
  return manager != 0u ? 1 : 0;
}

extern "C" int32_t refii_mod_set_weather(PPCContext* ctx, uint8_t* base, const char* theme, float weight, float seconds) {
  if (ctx == nullptr || !ThemeName(theme) || !std::isfinite(weight) || !std::isfinite(seconds) || seconds < 0.0f) {
    return 0;
  }
  char chunk[192];
  // qc090_garth.lua: EnvironmentTheme.BlendToEnvironmentTheme("EnvThemeWestcliffNight", 1.0, 1.0).
  std::snprintf(chunk, sizeof(chunk), "EnvironmentTheme.BlendToEnvironmentTheme(\"%s\", %.4f, %.4f)", theme,
                static_cast<double>(weight < 0.0f ? 0.0f : (weight > 1.0f ? 1.0f : weight)), static_cast<double>(seconds));
  return refii_mod_run_game_lua(ctx, base, chunk);
}

extern "C" int32_t refii_mod_modify_health(PPCContext* ctx, uint8_t* base, uint32_t entity, float delta, uint32_t attacker) {
  const uint32_t health = ctx != nullptr ? refii_mod_component(base, entity, kHealthComponent) : 0u;
  if (health == 0 || std::isnan(delta)) {
    return 0;
  }
  PPCContext work = refii::mods::Frame(*ctx);
  work.r3.u32 = health;
  work.f1.f64 = static_cast<double>(delta);
  work.r4.u64 = 0;
  work.r5.u32 = Plausible(attacker) ? attacker : 0u;
  rex_CECHealth_Modify(work, base);
  return 1;
}

extern "C" int32_t refii_mod_level_name(uint8_t* base, char* out, uint32_t size) {
  if (out != nullptr && size != 0) {
    out[0] = 0;
  }
  // The world manager is *(*(world+8)+4), as the game reaches it (0x824DA760, sub_82446928).
  const uint32_t world = refii_mod_world(base);
  const uint32_t holder8 = world != 0 ? refii::vfs::LoadGuestU32(base, world + 8u) : 0u;
  const uint32_t manager = Plausible(holder8) ? refii::vfs::LoadGuestU32(base, holder8 + 4u) : 0u;
  const uint32_t holder = Plausible(manager) ? refii::vfs::LoadGuestU32(base, manager + 0x24u) : 0u;
  const uint32_t level = Plausible(holder) ? refii::vfs::LoadGuestU32(base, holder + 4u) : 0u;
  if (!Plausible(level)) {
    return 0;
  }
  return CopyGuestString(base, LhStringText(base, level + 0xACu), out, size) ? 1 : 0;
}

extern "C" int32_t refii_mod_gold(uint8_t* base, uint32_t entity, int32_t* out) {
  const uint32_t purse = refii_mod_component(base, entity, kMoneyComponent);
  if (purse == 0) {
    return 0;
  }
  if (out != nullptr) {
    *out = static_cast<int32_t>(refii::mods::LoadU32(base, purse + 12u));
  }
  return 1;
}

extern "C" int32_t refii_mod_give_gold(PPCContext* ctx, uint8_t* base, uint32_t entity, int32_t delta) {
  const uint32_t purse = ctx != nullptr ? refii_mod_component(base, entity, kMoneyComponent) : 0u;
  if (purse == 0) {
    return 0;
  }
  // The game's own "give gold" thunk: Modify(purse, delta, 0, 1, 0).
  PPCContext work = refii::mods::Frame(*ctx);
  work.r3.u32 = purse;
  work.r4.s32 = delta;
  work.r5.u64 = 0;
  work.r6.u64 = 1;
  work.r7.u64 = 0;
  rex_CECMoney_Modify(work, base);
  return 1;
}

extern "C" int32_t refii_mod_item_count(uint8_t* base, uint32_t entity, uint32_t definition) {
  const uint32_t inventory = refii_mod_component(base, entity, kInventoryComponent);
  const uint32_t sentinel = inventory != 0 ? refii::mods::LoadU32(base, inventory + 16u) : 0u;
  if (!Plausible(sentinel)) {
    return 0;
  }
  int32_t count = 0;
  uint32_t node = refii::mods::LoadU32(base, sentinel);
  for (uint32_t walked = 0; Plausible(node) && node != sentinel && walked < 4096u; ++walked) {
    const uint32_t item = refii::mods::LoadU32(base, node + 8u);
    if (Plausible(item) && refii::mods::LoadU32(base, item + 4u) == definition) {
      count += static_cast<int32_t>(refii::mods::LoadU32(base, item + 12u));
    }
    node = refii::mods::LoadU32(base, node);
  }
  return count;
}

extern "C" uint32_t refii_mod_add_item(PPCContext* ctx, uint8_t* base, uint32_t entity, uint32_t definition) {
  const uint32_t inventory = ctx != nullptr ? refii_mod_component(base, entity, kInventoryComponent) : 0u;
  if (inventory == 0 || definition == 0) {
    return 0;
  }
  // The shared pointer the add fills: four words on our own frame below the caller's.
  PPCContext work = refii::mods::Frame(*ctx);
  const uint32_t out = work.r1.u32 + 0x40u;
  for (uint32_t i = 0; i < 4; ++i) {
    refii::mods::StoreU32(base, out + 4u * i, 0u);
  }
  work.r3.u32 = out;
  work.r4.u32 = inventory;
  work.r5.u32 = definition;
  work.r6.u64 = 1;
  rex_CECInventory_AddOneOfRecord(work, base);
  const uint32_t item = refii::mods::LoadU32(base, out);
  if (!Plausible(item)) {
    return 0u;
  }
  // The add registered the out slot as a reference holder on the item (+40); every game caller
  // releases it before its frame goes (0x823B815C), or the item keeps a pointer into a dead frame.
  PPCContext release = refii::mods::Frame(*ctx);
  release.r3.u32 = item;
  release.r4.u32 = out;
  rex_lhTrackedObject_RemoveHandleRef(release, base);
  return item;
}

// ---------------------------------------------------------------------------------------------
// Hero stats, physique, inventory, weapons, space, spawning (v2 helpers)
// ---------------------------------------------------------------------------------------------

namespace {

constexpr uint32_t kAgeComponent = 122;
constexpr uint32_t kWeaponSetComponent = 28;
constexpr uint32_t kCreatureComponent = 20;
constexpr uint32_t kStatsFat = 56;
constexpr uint32_t kStatsHeight = 60;
constexpr uint32_t kAgeYears = 12;
constexpr uint32_t kInventoryList = 16;
constexpr uint32_t kWeaponMelee = 84;
constexpr uint32_t kWeaponRanged = 88;
/// The world's entity manager (TU1 0x83496B28): +8 the intrusive list's head (each node is
/// entity+8, node +0 the next node), the link rex_lhEntityManager_Add makes.
constexpr uint32_t kEntityManager = 0x83496B28u;
constexpr uint32_t kManagerList = 8;
constexpr uint32_t kEntityNode = 8;
constexpr uint32_t kEntityFlags = 0x90;
constexpr uint8_t kInWorld = 0x40;
/// Presence of component 2 (the placement the sphere test reads: entity+0x24 bit 2), as
/// sub_822000E8 checks before it asks for a position.
constexpr uint32_t kPresenceWord = 0x24;
constexpr uint32_t kHasPlacementBit = 1u << 2;
constexpr int32_t kNearScanLimit = 20000;

uint32_t StatsOf(uint8_t* base, uint32_t entity) {
  return Plausible(entity) ? refii_mod_component(base, entity, kStatsComponent) : 0u;
}

}  // namespace

extern "C" int32_t refii_mod_renown(PPCContext* ctx, uint8_t* base, uint32_t entity, int32_t* out) {
  const uint32_t stats = ctx != nullptr ? StatsOf(base, entity) : 0u;
  if (stats == 0) {
    return 0;
  }
  PPCContext work = refii::mods::Frame(*ctx);
  work.r3.u32 = stats;
  rex_lhHeroStats_GetRenown(work, base);
  if (out != nullptr) {
    *out = static_cast<int32_t>(work.r3.u32);
  }
  return 1;
}

extern "C" int32_t refii_mod_morality(PPCContext* ctx, uint8_t* base, uint32_t entity, float* out) {
  const uint32_t stats = ctx != nullptr ? StatsOf(base, entity) : 0u;
  if (stats == 0) {
    return 0;
  }
  PPCContext work = refii::mods::Frame(*ctx);
  work.r3.u32 = stats;
  rex_lhHeroStats_GetMorality(work, base);
  if (out != nullptr) {
    *out = static_cast<float>(work.f1.f64);
  }
  return 1;
}

extern "C" int32_t refii_mod_purity(PPCContext* ctx, uint8_t* base, uint32_t entity, float* out) {
  const uint32_t stats = ctx != nullptr ? StatsOf(base, entity) : 0u;
  if (stats == 0) {
    return 0;
  }
  PPCContext work = refii::mods::Frame(*ctx);
  work.r3.u32 = stats;
  rex_lhHeroStats_GetPurity(work, base);
  if (out != nullptr) {
    *out = static_cast<float>(work.f1.f64);
  }
  return 1;
}

extern "C" int32_t refii_mod_physique(uint8_t* base, uint32_t entity, float* out_fat, float* out_height) {
  const uint32_t stats = StatsOf(base, entity);
  if (stats == 0) {
    return 0;
  }
  if (out_fat != nullptr) {
    *out_fat = refii::mods::LoadF32(base, stats + kStatsFat);
  }
  if (out_height != nullptr) {
    *out_height = refii::mods::LoadF32(base, stats + kStatsHeight);
  }
  return 1;
}

extern "C" int32_t refii_mod_age(uint8_t* base, uint32_t entity, float* out) {
  const uint32_t age = Plausible(entity) ? refii_mod_component(base, entity, kAgeComponent) : 0u;
  if (age == 0) {
    return 0;
  }
  if (out != nullptr) {
    *out = refii::mods::LoadF32(base, age + kAgeYears);
  }
  return 1;
}

extern "C" int32_t refii_mod_inventory_stack(uint8_t* base, uint32_t entity, int32_t index, uint32_t* out_definition,
                                             int32_t* out_count) {
  const uint32_t inventory = Plausible(entity) ? refii_mod_component(base, entity, kInventoryComponent) : 0u;
  if (inventory == 0 || index < 0) {
    return 0;
  }
  const uint32_t head = refii::vfs::LoadGuestU32(base, inventory + kInventoryList);
  if (!Plausible(head)) {
    return 0;
  }
  uint32_t node = refii::vfs::LoadGuestU32(base, head);
  for (int32_t i = 0; Plausible(node) && node != head && i < 4096; ++i, node = refii::vfs::LoadGuestU32(base, node)) {
    if (i != index) {
      continue;
    }
    const uint32_t item = refii::vfs::LoadGuestU32(base, node + 8u);
    if (!Plausible(item)) {
      return 0;
    }
    if (out_definition != nullptr) {
      *out_definition = refii::vfs::LoadGuestU32(base, item + 4u);
    }
    if (out_count != nullptr) {
      *out_count = static_cast<int32_t>(refii::vfs::LoadGuestU32(base, item + 12u));
    }
    return 1;
  }
  return 0;
}

extern "C" int32_t refii_mod_equipped(uint8_t* base, uint32_t entity, uint32_t* out_melee, uint32_t* out_ranged) {
  const uint32_t set = Plausible(entity) ? refii_mod_component(base, entity, kWeaponSetComponent) : 0u;
  if (set == 0) {
    return 0;
  }
  if (out_melee != nullptr) {
    *out_melee = refii::vfs::LoadGuestU32(base, set + kWeaponMelee);
  }
  if (out_ranged != nullptr) {
    *out_ranged = refii::vfs::LoadGuestU32(base, set + kWeaponRanged);
  }
  return 1;
}

// ---------------------------------------------------------------------------------------------
// The quick menu's actions, the performer's expressions, the spell tables, a record's strings
// ---------------------------------------------------------------------------------------------

namespace {

constexpr uint32_t kPerformerComponent = 154;
constexpr uint32_t kPerformerKnownBegin = 0x88;
constexpr uint32_t kPerformerKnownEnd = 0x8C;
constexpr uint32_t kPerformerEntryBytes = 20;
constexpr uint32_t kPerformerEntryUses = 12;
constexpr uint32_t kPerformerKnownMax = 512;
constexpr uint32_t kSpellTiers = 5;
constexpr uint32_t kSpellTypes = 8;
constexpr uint32_t kSpellKnown = 156;    // manager + 156 + 5 * type + tier: the hero knows it
constexpr uint32_t kSpellOffered = 196;  // manager + 196 + 5 * type + tier: the tier may take it
constexpr uint32_t kGuiObject = 0x83496B08u;           // disc 0x834968A4
constexpr uint32_t kGdbManager = 0x83496DA0u;          // disc 0x83496BD0
constexpr uint32_t kRecordStringGetter = 0x82323540u;  // disc 0x82345978: (entry, "Name") -> char*
constexpr uint32_t kRecordTypedGetter = 0x82A006C8u;   // disc 0x829FB978: GDBEntry_GetPropTyped(entry, hash, type) -> value*
constexpr uint32_t kGdbTypeInt = 1;                     // GDB property types, as the database states them
constexpr uint32_t kGdbTypeEnum = 5;
// The expression wheel (the HUD's own reader): the
// controller at *(*(GUI + 32) + 60); 15 words read as one.
constexpr uint32_t kWheelOpen = 0;
constexpr uint32_t kWheelCursor = 5;
constexpr uint32_t kWheelPlayer = 6;
constexpr uint32_t kWheelBegin = 13;
constexpr uint32_t kWheelEnd = 14;
constexpr uint32_t kWheelEntryBytes = 12;
constexpr uint32_t kWheelEntriesMax = 64;

int32_t Quickslot(PPCContext* ctx, uint8_t* base, int32_t player, uint32_t kind, uint32_t record) {
  if (ctx == nullptr || player < 1 || player > 2) {
    return 0;
  }
  PPCContext work = *ctx;
  return refii::vfs::PerformQuickslot(work, base, static_cast<uint32_t>(player), kind, record) ? 1 : 0;
}

/// Player `player`'s performer (component 154), or 0.
uint32_t PlayerPerformer(uint8_t* base, int32_t player) {
  const uint32_t hero = player >= 1 && player <= 2 ? refii_mod_player_hero(base, player) : 0u;
  return Plausible(hero) ? refii_mod_component(base, hero, kPerformerComponent) : 0u;
}

/// The performer's known-expression vector, validated; false when it is not one.
bool KnownExpressions(uint8_t* base, uint32_t performer, uint32_t& begin, uint32_t& end) {
  begin = refii::mods::LoadU32(base, performer + kPerformerKnownBegin);
  end = refii::mods::LoadU32(base, performer + kPerformerKnownEnd);
  return Plausible(begin) && end >= begin && end - begin <= kPerformerEntryBytes * kPerformerKnownMax &&
         (end - begin) % kPerformerEntryBytes == 0;
}

}  // namespace

extern "C" int32_t refii_mod_use_item(PPCContext* ctx, uint8_t* base, int32_t player, uint32_t definition) {
  return Quickslot(ctx, base, player, 1u, definition);
}

extern "C" int32_t refii_mod_perform_expression(PPCContext* ctx, uint8_t* base, int32_t player, uint32_t expression) {
  return Quickslot(ctx, base, player, 2u, expression);
}

extern "C" int32_t refii_mod_equip_weapon(PPCContext* ctx, uint8_t* base, int32_t player, uint32_t definition) {
  return Quickslot(ctx, base, player, 3u, definition);
}

extern "C" int32_t refii_mod_assign_spell(PPCContext* ctx, uint8_t* base, int32_t player, int32_t spell) {
  if (spell < 0 || spell >= static_cast<int32_t>(kSpellTypes)) {
    return 0;
  }
  return Quickslot(ctx, base, player, 4u, static_cast<uint32_t>(spell));
}

extern "C" int32_t refii_mod_expression(uint8_t* base, int32_t player, int32_t index, uint32_t* out_record,
                                        int32_t* out_uses) {
  const uint32_t performer = PlayerPerformer(base, player);
  uint32_t begin = 0, end = 0;
  if (performer == 0 || index < 0 || !KnownExpressions(base, performer, begin, end)) {
    return 0;
  }
  const uint32_t at = begin + kPerformerEntryBytes * static_cast<uint32_t>(index);
  if (at + kPerformerEntryBytes > end) {
    return 0;
  }
  if (out_record != nullptr) {
    *out_record = refii::mods::LoadU32(base, at);
  }
  if (out_uses != nullptr) {
    *out_uses = static_cast<int32_t>(refii::mods::LoadU32(base, at + kPerformerEntryUses));
  }
  return 1;
}

extern "C" int32_t refii_mod_spells_known(uint8_t* base, int32_t player, uint32_t* out_mask) {
  const uint32_t hero = player >= 1 && player <= 2 ? refii_mod_player_hero(base, player) : 0u;
  const uint32_t manager = Plausible(hero) ? refii_mod_component(base, hero, kSpellManagerComponent) : 0u;
  if (manager == 0) {
    return 0;
  }
  uint32_t mask = 0;
  for (uint32_t type = 0; type < kSpellTypes; ++type) {
    for (uint32_t tier = 0; tier < kSpellTiers; ++tier) {
      const uint32_t index = kSpellTiers * type + tier;
      if (refii::mods::LoadU8(base, manager + kSpellKnown + index) != 0 &&
          refii::mods::LoadU8(base, manager + kSpellOffered + index) != 0) {
        mask |= 1u << type;
        break;
      }
    }
  }
  if (out_mask != nullptr) {
    *out_mask = mask;
  }
  return 1;
}

extern "C" uint32_t refii_mod_selected_expression(uint8_t* base, int32_t player) {
  if (player < 1 || player > 2) {
    return 0;
  }
  const uint32_t gui = refii::vfs::LoadGuestU32(base, refii::guest::Address(kGuiObject));
  const uint32_t hud = Plausible(gui) ? refii::mods::LoadU32(base, gui + 32u) : 0u;
  const uint32_t wheel = Plausible(hud) ? refii::mods::LoadU32(base, hud + 60u) : 0u;
  if (!Plausible(wheel) || refii::mods::LoadU32(base, wheel + 4u * kWheelOpen) != 1u ||
      refii::mods::LoadU32(base, wheel + 4u * kWheelPlayer) != static_cast<uint32_t>(player)) {
    return 0;
  }
  const uint32_t begin = refii::mods::LoadU32(base, wheel + 4u * kWheelBegin);
  const uint32_t end = refii::mods::LoadU32(base, wheel + 4u * kWheelEnd);
  const uint32_t cursor = refii::mods::LoadU32(base, wheel + 4u * kWheelCursor);
  if (!Plausible(begin) || end < begin || end - begin > kWheelEntryBytes * kWheelEntriesMax ||
      (end - begin) % kWheelEntryBytes != 0 || cursor >= (end - begin) / kWheelEntryBytes) {
    return 0;
  }
  const uint32_t entry = begin + cursor * kWheelEntryBytes;
  const uint32_t record = refii::mods::LoadU32(base, entry);
  const uint32_t flags = refii::mods::LoadU32(base, entry + 4u);
  const uint32_t tail = refii::mods::LoadU32(base, entry + 8u);
  if (((flags >> 16) & 0xFFu) == 0u || (flags & 0xFFu) != 0u || (tail & 0xFFFF0000u) != 0u) {
    return 0;
  }
  return record;
}

extern "C" int32_t refii_mod_record_string(PPCContext* ctx, uint8_t* base, uint32_t definition, const char* field,
                                           char* out, uint32_t size) {
  if (out != nullptr && size != 0) {
    out[0] = 0;
  }
  if (ctx == nullptr || definition == 0 || field == nullptr || field[0] == 0 || out == nullptr || size == 0) {
    return 0;
  }
  const uint32_t gdb = refii::vfs::LoadGuestU32(base, refii::guest::Address(kGdbManager));
  if (!Plausible(gdb)) {
    return 0;
  }
  PPCContext lookup = refii::mods::Frame(*ctx);
  lookup.r3.u32 = gdb;
  lookup.r4.u32 = definition;
  rex_GDBManager_FindEntryByHash(lookup, base);
  const uint32_t entry = lookup.r3.u32;
  if (!Plausible(entry)) {
    return 0;
  }
  // The property name on our own frame: the getter hashes it (FNV-1) and asks the entry.
  PPCContext work = refii::mods::Frame(*ctx);
  const uint32_t name = work.r1.u32 + 0x80u;
  size_t n = std::strlen(field);
  if (n > 63) {
    n = 63;
  }
  std::memcpy(base + name, field, n);
  base[name + n] = 0;
  work.r3.u32 = entry;
  work.r4.u32 = name;
  if (!refii::mods::CallGuest(work, base, refii::guest::Address(kRecordStringGetter))) {
    return 0;
  }
  const uint32_t text = work.r3.u32;
  if (!Plausible(text)) {
    return 0;
  }
  uint32_t i = 0;
  for (; i + 1 < size && base[text + i] != 0; ++i) {
    out[i] = static_cast<char>(base[text + i]);
  }
  out[i] = 0;
  return 1;
}

extern "C" int32_t refii_mod_record_int(PPCContext* ctx, uint8_t* base, uint32_t definition, const char* field,
                                        int32_t* out) {
  if (out != nullptr) {
    *out = 0;
  }
  if (ctx == nullptr || definition == 0 || field == nullptr || field[0] == 0 || out == nullptr) {
    return 0;
  }
  const uint32_t gdb = refii::vfs::LoadGuestU32(base, refii::guest::Address(kGdbManager));
  if (!Plausible(gdb)) {
    return 0;
  }
  PPCContext lookup = refii::mods::Frame(*ctx);
  lookup.r3.u32 = gdb;
  lookup.r4.u32 = definition;
  rex_GDBManager_FindEntryByHash(lookup, base);
  const uint32_t entry = lookup.r3.u32;
  if (!Plausible(entry)) {
    return 0;
  }
  // The string getter hashes the name itself (sub_82323540 -> sub_821D4D90 -> GetPropTyped with
  // type 4); an integer takes the typed getter directly, as an int and then as an enum.
  const uint32_t hash = refii_mod_fnv1(field);
  for (uint32_t type : {kGdbTypeInt, kGdbTypeEnum}) {
    PPCContext work = refii::mods::Frame(*ctx);
    work.r3.u32 = entry;
    work.r4.u32 = hash;
    work.r5.u32 = type;
    if (!refii::mods::CallGuest(work, base, refii::guest::Address(kRecordTypedGetter))) {
      return 0;
    }
    const uint32_t value = work.r3.u32;
    if (Plausible(value)) {
      *out = static_cast<int32_t>(refii::mods::LoadU32(base, value));
      return 1;
    }
  }
  return 0;
}

extern "C" int32_t refii_mod_entities_near(PPCContext* ctx, uint8_t* base, const float* xyz, float radius,
                                           int32_t creatures_only, uint32_t* out, int32_t max) {
  if (ctx == nullptr || xyz == nullptr || out == nullptr || max <= 0 || !(radius > 0.0f)) {
    return 0;
  }
  const uint32_t manager = refii::vfs::LoadGuestU32(base, refii::guest::Address(kEntityManager));
  if (!Plausible(manager)) {
    return 0;
  }
  struct Found {
    uint32_t entity;
    float distance2;
  };
  std::vector<Found> found;
  const float limit = radius * radius;
  uint32_t node = refii::vfs::LoadGuestU32(base, manager + kManagerList);
  for (int32_t i = 0; Plausible(node) && i < kNearScanLimit; ++i, node = refii::vfs::LoadGuestU32(base, node)) {
    const uint32_t entity = node - kEntityNode;
    if ((base[entity + kEntityFlags] & kInWorld) == 0 ||
        (refii::vfs::LoadGuestU32(base, entity + kPresenceWord) & kHasPlacementBit) == 0) {
      continue;
    }
    if (creatures_only != 0 && refii_mod_component(base, entity, kCreatureComponent) == 0) {
      continue;
    }
    float at[3];
    if (refii_mod_entity_position(ctx, base, entity, at) == 0) {
      continue;
    }
    const float dx = at[0] - xyz[0], dy = at[1] - xyz[1], dz = at[2] - xyz[2];
    const float d2 = dx * dx + dy * dy + dz * dz;
    if (d2 < limit) {
      found.push_back(Found{entity, d2});
    }
  }
  std::sort(found.begin(), found.end(), [](const Found& a, const Found& b) { return a.distance2 < b.distance2; });
  const int32_t n = static_cast<int32_t>(found.size()) < max ? static_cast<int32_t>(found.size()) : max;
  for (int32_t i = 0; i < n; ++i) {
    out[i] = found[static_cast<size_t>(i)].entity;
  }
  return n;
}

extern "C" int32_t refii_mod_line_of_sight(PPCContext* ctx, uint8_t* base, const float* from, const float* to) {
  if (ctx == nullptr || from == nullptr || to == nullptr) {
    return 0;
  }
  // The physics object the raycast is made on (*(world+0x18)): none before a session.
  const uint32_t world = refii_mod_world(base);
  if (world == 0 || !Plausible(refii::vfs::LoadGuestU32(base, world + 0x18u))) {
    return 0;
  }
  PPCContext work = refii::mods::Frame(*ctx, 0x800u);
  refii::mods::SetVector(work, 1, from);
  refii::mods::SetVector(work, 2, to);
  rex_Village_IsLineOfSight(work, base);
  return (work.r3.u32 & 0xFFu) != 0 ? 1 : 0;
}

extern "C" uint32_t refii_mod_spawn(PPCContext* ctx, uint8_t* base, uint32_t definition, const float* xyz) {
  const uint32_t system = refii::vfs::ModScriptSystem();
  if (ctx == nullptr || xyz == nullptr || definition == 0 || !Plausible(system)) {
    return 0;
  }
  // The game creates an entity even for a hash its database lacks; refuse that here. The manager
  // is g_GDBManager (TU1 0x83496DA0), its lookup GDBManager_FindEntryByHash (r3 manager, r4 hash).
  const uint32_t gdb = refii::vfs::LoadGuestU32(base, refii::guest::Address(0x83496DA0u));
  if (!Plausible(gdb)) {
    return 0;
  }
  PPCContext lookup = refii::mods::Frame(*ctx);
  lookup.r3.u32 = gdb;
  lookup.r4.u32 = definition;
  rex_GDBManager_FindEntryByHash(lookup, base);
  if (!Plausible(lookup.r3.u32)) {
    return 0;
  }
  // The name argument is an LHString handle the entity's name is copied from: an empty handle,
  // on our own frame, names nothing.
  PPCContext work = refii::mods::Frame(*ctx, 0x400u);
  const uint32_t name = work.r1.u32 + 0x80u;
  refii::mods::StoreU32(base, name, 0u);
  work.r3.u32 = system;
  work.r4.u32 = definition;
  work.r5.u32 = name;
  refii::mods::SetVector(work, 1, xyz);
  rex_lhLuaScriptSystem_CreateEntityAtPosition(work, base);
  return Plausible(work.r3.u32) ? work.r3.u32 : 0u;
}
