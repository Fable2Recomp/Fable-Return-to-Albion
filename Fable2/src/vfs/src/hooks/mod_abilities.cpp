// refii — MOD ABILITIES AND SPELL PICKS ON THE ENGINE PATH, run in the guest tick from the host's
// command queue (mod_commands.cpp routes the verbs here).
//
// A MOD ABILITY (ui/abilities.json, Engine_UiModAbilities) is declared, not coded: the engine runs
// its purchase, its unlearn, its prices and its saves from the declaration, the same way for every
// mod and both players, and a mod's native code only applies the level's effect where it is used,
// reading the level from the player store. The book posts:
//   ability_clear
//   ability_declare NAME MOD MAX COSTLIKE COSTLIKE_SETTING LEVELKEY POOLS REFUND LEGACY
//       COSTLIKE the ability type (1..14) whose experience table prices the levels; COSTLIKE_SETTING
//       a mod setting that overrides it ('-' none); LEVELKEY the SAVED player value holding the
//       level; POOLS the experience pools the price is split across, comma-separated in the declared
//       order (1 Strength, 2 Skill, 3 Will; '-' for General alone); REFUND 'half' or 'none'; LEGACY
//       the RefiiModSave.<key> a save from before kept player one's level in ('-' none)
//   ability_buy PLAYER NAME · ability_refund PLAYER NAME
//   spell_pick PLAYER SLOT SPELL STORE PICK MOD
//       a pick on a spell slot (ui/spellwheel.json, Engine_UiSpellPicks): STORE the saved player key
//       the pick lives under, its {slot} filled in; '-' for an empty MOD
//   spell_pick_clear
//   spell_pick_declare NAME MOD STORE EXCLUDE LEGACY LEGACY_TWO
//       the selectors declare each mod pick: STORE its key template ({slot} the slot's number 1..5),
//       EXCLUDE the wills it never takes (comma-separated, '-' none), LEGACY / LEGACY_TWO the
//       RefiiModSave key templates a save from before kept player one's / player two's pick in
//
// A DECLARED PICK'S EXCLUDE IS THE ENGINE'S: a spell_pick naming an excluded will is refused
// before the event (whoever posted it), and a handler that rewrites the will to an excluded one is
// refused after it — the slot keeps the pick it had. Its legacy keys are read as an ability's:
// once after each Lua rebuild or save load, on the first tick with the hero present, a slot whose
// store is empty takes the save's Lua value (0..7, not excluded) for each player played here.
//
// THE PURCHASE IS THE SHIPPED ONE (TU1 sub_8246C970), share by share: the next level's price from
// the game's own table (refii_mod_ability_cost), split evenly across the pools (the first named takes
// the odd point), affordable when each share is covered by its pool and what General has left after
// the shares before it (the 0x824E4E28 test, `cost <= (int)pool + (int)General`, applied per
// share), spent through the game's own Spend (a shortfall comes out of General) — then the level is
// saved one higher. THE UNLEARN IS THE SHIPPED ONE (TU1 sub_8246D510): half the given-up level's
// price back into General through the game's own Modify, the level saved one lower. Either raises
// ModAbility first, which may refuse it.
//
// WHAT THE BOOK SHOWS: each tick, for each player played here, the engine states beside the level
// (session values in that player's store, so both machines and every view read them):
//   <levelKey>.cost   the next level's price (-1 at the maximum or with no price table)
//   <levelKey>.refund what an unlearn gives back (0 with nothing to give up, or "refund": "none")
//   <levelKey>.afford 1 when the next level can be bought now
//
// SAVES FROM BEFORE: a mod that used to keep player one's level in the world save's Lua table names
// it (LEGACY); a player-one hero with no saved level takes that one, once after each Lua rebuild or
// save load, on the first tick with the hero present (the game VM is up then).
//
// THE GAME'S OWN PICK (the slot's spell) raises SpellPick at the guest's one writer of the slot table,
// `CECSpellManager::SetSpellSlot` (TU1 sub_82541F30 / disc sub_8242C970, hooked by name below):
// r3 the spell manager (component 56, owner entity at +4), r4 the targeted row (carried into the
// network message only), r5 the slot 0..4, r6 the will (-1..7), r7 NOTIFY. It stores the will in both
// rows of the int8 [2][5] table at +0x80 (`stbx r10,r3,r31` 0x82541f58, `stb r10,5(r11)` 0x82541f60),
// re-validates it (sub_828B9050 twice), and with notify set and the owner locally controlled
// (sub_822D25F0) sends the change to the session (off_820A6204 through sub_82545E08, 0x82541f8c..
// 0x82541fc8). Notify IS THE GUEST'S OWN "THIS IS A CHOICE" FLAG, and the event follows it:
//   notify 1, a pick: the selectors' carousel, which commits on every LEFT/RIGHT (sub_826B5330
//     0x826b53a8, sub_826B5240 0x826b52a8) and copies a row down on A (sub_826B4EF8 0x826b4fc4); the
//     scripts' SetEntitySpellTableItem / SetEntitySpellCastTableItem (sub_82541168 0x8254119c) and
//     SetHeroSpellTableItem (sub_823B96E0 0x823b9894); and the host's own AssignSpell
//     (guest_tick_pump.cpp DispatchSpellAssign, the game pick of Engine_UiSpellPicks).
//   notify 0, not a pick: the template's SurroundSpellTypes defaults while the component is built
//     (sub_8253D568 0x8253d600 `li r7,0`) and the session applying the other machine's change
//     (sub_82392250 0x82399620 / 0x82399720 `li r7,0`).
// A save load is not a pick and never reaches the setter: the component's serialiser reads
// "AssignedSpells<row><slot>" straight into the table (sub_828B9320 via sub_824807C8) and validates
// it with sub_828B9050. A carousel step the player scrolls past is a pick too: the guest has no
// preview — every step is in the slot table, and so in the cast, the moment it is made, and nothing
// writes the earlier will back when the selector closes (the three carousel sites above are its only
// writers). The handlers' verdict goes into the guest's own call — a changed will is the one stored
// and sent to the session, a cancel skips the call — so the other machine applies the verdict.
// The event: pick "spell" and mod "" (Engine_UiSpellPicks.Game), the slot, the will, the will held
// before (the targeted row's cell, as LaunchSlot reads it), the owner's player; an owner that is no
// player (none of the game's callers) passes through. A handler's own assignment is not raised again.

#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>

#include <rex/logging.h>

#include "core/mod_values.h"
#include "core/refii_mod.h"
#include "core/refii_mod_events.h"
#include "hooks/ppc_externs.h"

namespace {

constexpr int32_t kGeneralPool = 0;

struct Ability {
  std::string name;
  std::string mod;
  int max_level = 5;
  int cost_like = 0;
  std::string cost_like_setting;
  std::string level_key;
  std::vector<int32_t> pools;
  bool refund_half = false;
  std::string legacy;
  /// Per player: the (hero, level, table) the stated prices were read for.
  uint32_t priced_hero[2] = {0, 0};
  int priced_level[2] = {-1, -1};
  int priced_table[2] = {-1, -1};
  int next_cost[2] = {-1, -1};
  int refund_value[2] = {0, 0};
};

std::vector<Ability> g_abilities;
uint32_t g_seen_generation = 0xFFFFFFFFu;
bool g_reread_legacy = true;

/// A mod's spell slot pick as the selectors declared it (spell_pick_declare).
struct DeclaredPick {
  std::string name;
  std::string mod;
  std::string store;
  uint32_t excluded = 0;  // bit w: will w is never picked
  std::string legacy[2];
};

std::vector<DeclaredPick> g_picks;

/// Per player: the declared picks' legacy keys are still to be read for that player's hero.
bool g_reread_picks[2] = {true, true};

/// A word, '-' standing for an empty one.
std::string Word(const std::vector<std::string>& w, size_t i) {
  return i < w.size() && w[i] != "-" ? w[i] : std::string();
}

int32_t Int(const std::vector<std::string>& w, size_t i, int32_t fallback = 0) {
  return i < w.size() ? static_cast<int32_t>(std::strtol(w[i].c_str(), nullptr, 10)) : fallback;
}

Ability* Find(const std::string& name) {
  for (Ability& a : g_abilities) {
    if (a.name == name) {
      return &a;
    }
  }
  return nullptr;
}

int Level(const Ability& a, int32_t player) {
  const int level = static_cast<int>(refii_mod_player_value(player, a.level_key.c_str(), 0.0f));
  return level < 0 ? 0 : (level > a.max_level ? a.max_level : level);
}

int Table(const Ability& a) {
  const int stated = a.cost_like_setting.empty()
                         ? a.cost_like
                         : static_cast<int>(refii::mods::Setting(a.cost_like_setting, static_cast<float>(a.cost_like)));
  return stated >= 1 && stated <= 14 ? stated : a.cost_like;
}

int CostOf(PPCContext& ctx, uint8_t* base, uint32_t hero, const Ability& a, int level) {
  PPCContext work = ctx;
  return refii_mod_ability_cost(&work, base, hero, Table(a), level);
}

int PoolOf(uint8_t* base, uint32_t hero, int32_t pool) {
  float value = 0.0f;
  return refii_mod_experience(base, hero, pool, &value) != 0 ? static_cast<int>(value) : 0;
}

/// The price split across the pools: the first named takes the odd point. General alone takes all.
std::vector<int> Shares(const Ability& a, int cost) {
  std::vector<int> shares;
  if (a.pools.empty()) {
    shares.push_back(cost);
    return shares;
  }
  const int n = static_cast<int>(a.pools.size());
  for (int i = 0; i < n; ++i) {
    shares.push_back(cost / n + (i < cost % n ? 1 : 0));
  }
  return shares;
}

/// Each share against its own pool, the shortfalls against General, in the declared order.
bool Affordable(uint8_t* base, uint32_t hero, const Ability& a, int cost) {
  if (cost <= 0) {
    return false;
  }
  int general = PoolOf(base, hero, kGeneralPool);
  if (a.pools.empty()) {
    return cost <= general;
  }
  const std::vector<int> shares = Shares(a, cost);
  for (size_t i = 0; i < shares.size(); ++i) {
    const int have = PoolOf(base, hero, a.pools[i]);
    general -= shares[i] > have ? shares[i] - have : 0;
    if (general < 0) {
      return false;
    }
  }
  return true;
}

void Declare(const std::vector<std::string>& w) {
  Ability a;
  a.name = Word(w, 1);
  a.mod = Word(w, 2);
  a.max_level = Int(w, 3, 5);
  a.cost_like = Int(w, 4, 0);
  a.cost_like_setting = Word(w, 5);
  a.level_key = Word(w, 6);
  const std::string pools = Word(w, 7);
  for (size_t at = 0; at < pools.size();) {
    const size_t comma = pools.find(',', at);
    const int32_t pool = static_cast<int32_t>(std::strtol(pools.substr(at, comma - at).c_str(), nullptr, 10));
    if (pool >= 1 && pool <= 3) {
      a.pools.push_back(pool);
    }
    at = comma == std::string::npos ? pools.size() : comma + 1;
  }
  a.refund_half = Word(w, 8) == "half";
  a.legacy = Word(w, 9);
  if (a.name.empty() || a.level_key.empty() || a.max_level < 1) {
    REXLOG_WARN("Mod abilities: a declaration with no name, level key or levels is ignored");
    return;
  }
  for (Ability& existing : g_abilities) {
    if (existing.name == a.name) {
      existing = a;
      return;
    }
  }
  g_abilities.push_back(a);
  g_reread_legacy = true;
}

void Buy(PPCContext& ctx, uint8_t* base, int32_t player, Ability& a) {
  const uint32_t hero = refii_mod_player_hero(base, player);
  if (hero == 0) {
    return;
  }
  const int level = Level(a, player);
  if (level >= a.max_level) {
    REXLOG_INFO("Mod abilities: player {} {} is already at level {}", player, a.name, level);
    return;
  }
  float probe = 0.0f;
  if (refii_mod_experience(base, hero, kGeneralPool, &probe) == 0) {
    REXLOG_WARN("Mod abilities: no experience component on player {}'s hero; {} not bought", player, a.name);
    return;
  }
  const int cost = CostOf(ctx, base, hero, a, level + 1);
  if (!Affordable(base, hero, a, cost)) {
    REXLOG_INFO("Mod abilities: {} {} costs {}; player {} cannot pay it", a.name, level + 1, cost, player);
    return;
  }

  refii::mods::ModAbilityEvent e{};
  PPCContext event_ctx = ctx;
  e.ctx = &event_ctx;
  e.base = base;
  e.ability = a.name.c_str();
  e.mod = a.mod.c_str();
  e.refund = false;
  e.previous = level;
  e.level = level + 1;
  e.experience = cost;
  e.player = player;
  refii::mods::Dispatch(e);
  if (e.cancel) {
    REXLOG_INFO("Mod abilities: a handler refused player {}'s {} {}", player, a.name, level + 1);
    return;
  }

  const std::vector<int> shares = Shares(a, cost);
  for (size_t i = 0; i < shares.size(); ++i) {
    if (shares[i] > 0) {
      PPCContext work = ctx;
      refii_mod_spend_experience(&work, base, hero, a.pools.empty() ? kGeneralPool : a.pools[i],
                                 static_cast<float>(shares[i]));
    }
  }
  refii_mod_player_save(player, a.level_key.c_str(), static_cast<float>(level + 1));
  a.priced_level[player - 1] = -1;
  REXLOG_INFO("Mod abilities: player {} bought {} {} for {} experience", player, a.name, level + 1, cost);
}

void Refund(PPCContext& ctx, uint8_t* base, int32_t player, Ability& a) {
  const uint32_t hero = refii_mod_player_hero(base, player);
  const int level = Level(a, player);
  if (hero == 0 || level <= 0 || !a.refund_half) {
    return;
  }
  const int cost = CostOf(ctx, base, hero, a, level);
  if (cost <= 0) {
    return;  // no price table (no stats component): nothing to give back, nothing to unlearn
  }
  const int refund = cost / 2;

  refii::mods::ModAbilityEvent e{};
  PPCContext event_ctx = ctx;
  e.ctx = &event_ctx;
  e.base = base;
  e.ability = a.name.c_str();
  e.mod = a.mod.c_str();
  e.refund = true;
  e.previous = level;
  e.level = level - 1;
  e.experience = refund;
  e.player = player;
  refii::mods::Dispatch(e);
  if (e.cancel) {
    REXLOG_INFO("Mod abilities: a handler refused player {}'s unlearn of {} {}", player, a.name, level);
    return;
  }

  PPCContext work = ctx;
  if (refii_mod_give_experience(&work, base, hero, kGeneralPool, static_cast<float>(refund)) == 0) {
    return;
  }
  refii_mod_player_save(player, a.level_key.c_str(), static_cast<float>(level - 1));
  a.priced_level[player - 1] = -1;
  REXLOG_INFO("Mod abilities: player {} unlearned {} {}, {} experience back to General", player, a.name, level, refund);
}

/// A save from before: player one's level from the world save's Lua table, when the store has none.
void ReadLegacy(PPCContext& ctx, uint8_t* base) {
  for (const Ability& a : g_abilities) {
    int32_t legacy = 0;
    PPCContext work = ctx;
    if (!a.legacy.empty() && refii_mod_player_get(1, a.level_key.c_str(), nullptr) == 0 &&
        refii_mod_save_get_int(&work, base, a.legacy.c_str(), 1, a.max_level, &legacy) != 0) {
      refii_mod_player_save(1, a.level_key.c_str(), static_cast<float>(legacy));
      REXLOG_INFO("Mod abilities: player 1's {} {} taken from the save's Lua table ({})", a.name, legacy, a.legacy);
    }
  }
}

void State(PPCContext& ctx, uint8_t* base, Ability& a, int32_t player, uint32_t hero) {
  const int index = player - 1;
  const int level = Level(a, player);
  const int table = Table(a);
  if (a.priced_hero[index] != hero || a.priced_level[index] != level || a.priced_table[index] != table) {
    a.priced_hero[index] = hero;
    a.priced_level[index] = level;
    a.priced_table[index] = table;
    const int next = level < a.max_level ? CostOf(ctx, base, hero, a, level + 1) : -1;
    a.next_cost[index] = next > 0 ? next : -1;
    const int held = level > 0 && a.refund_half ? CostOf(ctx, base, hero, a, level) : 0;
    a.refund_value[index] = held > 0 ? held / 2 : 0;
  }
  const std::string& key = a.level_key;
  refii_mod_player_set(player, (key + ".cost").c_str(), static_cast<float>(a.next_cost[index]));
  refii_mod_player_set(player, (key + ".refund").c_str(), static_cast<float>(a.refund_value[index]));
  refii_mod_player_set(player, (key + ".afford").c_str(),
                       a.next_cost[index] > 0 && Affordable(base, hero, a, a.next_cost[index]) ? 1.0f : 0.0f);
}

const DeclaredPick* FindPick(const std::string& name) {
  for (const DeclaredPick& p : g_picks) {
    if (p.name == name) {
      return &p;
    }
  }
  return nullptr;
}

bool Excluded(const DeclaredPick* pick, int32_t spell) {
  return pick != nullptr && spell >= 0 && spell <= 7 && (pick->excluded & (1u << spell)) != 0;
}

/// A key template with its {slot} filled in: the slot's number, 1..5.
std::string SlotKey(const std::string& tmpl, int32_t slot) {
  std::string key = tmpl;
  const size_t at = key.find("{slot}");
  if (at != std::string::npos) {
    key.replace(at, 6, std::to_string(slot + 1));
  }
  return key;
}

void DeclarePick(const std::vector<std::string>& w) {
  DeclaredPick p;
  p.name = Word(w, 1);
  p.mod = Word(w, 2);
  p.store = Word(w, 3);
  const std::string exclude = Word(w, 4);
  for (size_t at = 0; at < exclude.size();) {
    const size_t comma = exclude.find(',', at);
    const int32_t will = static_cast<int32_t>(std::strtol(exclude.c_str() + at, nullptr, 10));
    if (will >= 0 && will <= 7) {
      p.excluded |= 1u << will;
    }
    at = comma == std::string::npos ? exclude.size() : comma + 1;
  }
  p.legacy[0] = Word(w, 5);
  p.legacy[1] = Word(w, 6);
  if (p.name.empty() || p.store.empty()) {
    return;
  }
  for (DeclaredPick& existing : g_picks) {
    if (existing.name == p.name) {
      existing = p;
      return;
    }
  }
  g_picks.push_back(p);
}

/// A save from before: `player`'s slots each declared pick's store leaves empty, from the world save's Lua table.
void ReadLegacyPicks(PPCContext& ctx, uint8_t* base, int32_t player) {
  for (const DeclaredPick& p : g_picks) {
    {
      const std::string& tmpl = p.legacy[player - 1];
      if (tmpl.empty()) {
        continue;
      }
      for (int32_t slot = 0; slot < 5; ++slot) {
        const std::string store = SlotKey(p.store, slot);
        int32_t legacy = -1;
        PPCContext work = ctx;
        if (refii_mod_player_get(player, store.c_str(), nullptr) == 0 &&
            refii_mod_save_get_int(&work, base, SlotKey(tmpl, slot).c_str(), 0, 7, &legacy) != 0 && !Excluded(&p, legacy)) {
          refii_mod_player_save(player, store.c_str(), static_cast<float>(legacy));
          REXLOG_INFO("Spell picks: player {}'s {} on slot {} = {} taken from the save's Lua table", player, p.name, slot + 1,
                      legacy);
        }
      }
    }
  }
}

/// The pick's previous will, the event, and the store written when no handler refused it.
void Pick(PPCContext& ctx, uint8_t* base, const std::vector<std::string>& w) {
  const int32_t player = Int(w, 1);
  const int32_t slot = Int(w, 2, -1);
  const std::string store = Word(w, 4);
  const std::string pick = Word(w, 5);
  const std::string mod = Word(w, 6);
  if ((player != 1 && player != 2) || slot < 0 || slot > 4 || store.empty()) {
    return;
  }
  const int32_t previous = static_cast<int32_t>(refii_mod_player_value(player, store.c_str(), -1.0f));
  const DeclaredPick* declared = FindPick(pick);
  if (Excluded(declared, Int(w, 3, -1))) {
    REXLOG_INFO("Spell picks: {} never takes will {}; player {}'s slot {} keeps {}", pick, Int(w, 3, -1), player, slot + 1,
                previous);
    return;
  }

  refii::mods::SpellPickEvent e{};
  PPCContext event_ctx = ctx;
  e.ctx = &event_ctx;
  e.base = base;
  e.pick = pick.c_str();
  e.mod = mod.c_str();
  e.slot = slot;
  e.spell = Int(w, 3, -1);
  e.previous = previous;
  e.player = player;
  refii::mods::Dispatch(e);
  if (e.cancel) {
    REXLOG_INFO("Spell picks: a handler kept player {}'s {} on slot {} at {}", player, pick, slot + 1, previous);
    return;
  }
  const int32_t spell = e.spell >= 0 && e.spell <= 7 ? e.spell : -1;
  if (Excluded(declared, spell)) {
    REXLOG_INFO("Spell picks: a handler rewrote {} to will {}, which it never takes; player {}'s slot {} keeps {}", pick,
                spell, player, slot + 1, previous);
    return;
  }
  refii_mod_player_save(player, store.c_str(), static_cast<float>(spell));
  REXLOG_INFO("Spell picks: player {} {} on slot {}: {} (was {})", player, pick, slot + 1, spell, previous);
}

}  // namespace

namespace refii::vfs {

/// One of the verbs above; false for any other.
bool RunModAbilityCommand(PPCContext& ctx, uint8_t* base, const std::vector<std::string>& w) {
  if (w.empty()) {
    return false;
  }
  const std::string& op = w[0];
  if (op == "ability_clear") {
    g_abilities.clear();
  } else if (op == "ability_declare") {
    Declare(w);
  } else if (op == "ability_buy" || op == "ability_refund") {
    const int32_t player = Int(w, 1);
    Ability* a = Find(Word(w, 2));
    if (a != nullptr && (player == 1 || player == 2)) {
      if (op == "ability_buy") {
        Buy(ctx, base, player, *a);
      } else {
        Refund(ctx, base, player, *a);
      }
    }
  } else if (op == "spell_pick") {
    Pick(ctx, base, w);
  } else if (op == "spell_pick_clear") {
    g_picks.clear();
  } else if (op == "spell_pick_declare") {
    DeclarePick(w);
    g_reread_picks[0] = g_reread_picks[1] = true;
  } else {
    return false;
  }
  return true;
}

/// Every tick: saves from before, and the prices the book shows, for each player played here.
void PumpModAbilities(PPCContext& ctx, uint8_t* base) {
  if (g_abilities.empty() && g_picks.empty()) {
    return;
  }
  const uint32_t generation = refii_mod_lua_generation();
  if (generation != g_seen_generation) {
    g_seen_generation = generation;
    g_reread_legacy = true;
    g_reread_picks[0] = g_reread_picks[1] = true;
  }
  if (g_reread_legacy && refii_mod_player_hero(base, 1) != 0 && refii_mod_player_here(1) != 0) {
    g_reread_legacy = false;
    ReadLegacy(ctx, base);
  }
  for (int32_t player = 1; player <= 2; ++player) {
    if (g_reread_picks[player - 1] && refii_mod_player_hero(base, player) != 0 && refii_mod_player_here(player) != 0) {
      g_reread_picks[player - 1] = false;
      ReadLegacyPicks(ctx, base, player);
    }
  }
  for (int32_t player = 1; player <= 2; ++player) {
    const uint32_t hero = refii_mod_player_hero(base, player);
    if (hero == 0 || refii_mod_player_here(player) == 0) {
      continue;
    }
    for (Ability& a : g_abilities) {
      State(ctx, base, a, player, hero);
    }
  }
}

}  // namespace refii::vfs

namespace refii::vfs {
/// mod_api.cpp: the slots refii_mod_charge_spell has lent come back as this pick says.
void ModChargeLendPicked(int32_t player, int32_t slot, int32_t spell);
}  // namespace refii::vfs

namespace {

constexpr uint32_t kManagerOwner = 4u;
constexpr uint32_t kManagerSlotTable = 0x80u;
constexpr uint32_t kSlotTableRow = 5u;
bool g_in_spell_slot_pick = false;

}  // namespace

// CECSpellManager::SetSpellSlot — the slot table's one writer (see the header comment).
REX_EXTERN(rex_CECSpellManager_SetSpellSlot) {
  REX_FUNC_PROLOGUE();
  const uint32_t manager = ctx.r3.u32;
  const int32_t slot = static_cast<int32_t>(ctx.r5.u32);
  const bool notify = (ctx.r7.u32 & 0xFFu) != 0u;
  if (g_in_spell_slot_pick || !notify || manager == 0u || slot < 0 || slot > 4) {
    __imp__rex_CECSpellManager_SetSpellSlot(ctx, base);
    return;
  }
  const int32_t player = refii_mod_player_of(base, refii::mods::LoadU32(base, manager + kManagerOwner));
  if (player != 1 && player != 2) {
    __imp__rex_CECSpellManager_SetSpellSlot(ctx, base);
    return;
  }
  const int32_t previous = static_cast<int8_t>(
      refii::mods::LoadU8(base, manager + kManagerSlotTable + kSlotTableRow + static_cast<uint32_t>(slot)));

  refii::mods::SpellPickEvent e{};
  PPCContext event_ctx = ctx;
  e.ctx = &event_ctx;
  e.base = base;
  e.pick = "spell";
  e.mod = "";
  e.slot = slot;
  e.spell = static_cast<int8_t>(ctx.r6.u32 & 0xFFu);
  e.previous = previous;
  e.player = player;
  g_in_spell_slot_pick = true;
  refii::mods::Dispatch(e);
  g_in_spell_slot_pick = false;
  if (e.cancel) {
    REXLOG_INFO("Spell picks: a handler kept player {}'s slot {} at {}", player, slot + 1, previous);
    return;
  }
  if (e.spell >= -1 && e.spell <= 7) {
    ctx.r6.u32 = static_cast<uint32_t>(e.spell);
  }
  __imp__rex_CECSpellManager_SetSpellSlot(ctx, base);
  // A real pick while refii_mod_charge_spell has lent the slots is what comes back for this slot.
  refii::vfs::ModChargeLendPicked(player, slot, static_cast<int8_t>(ctx.r6.u32 & 0xFFu));
}
