// Spell Weaving — two spells from one release, Fable 3's gauntlets in Fable 2.
//
// The event (core/refii_mod_events.h): SpellLaunch, raised as one slot is about to launch, with
// the slot's spell type writable for that launch only and `cascading` set while the release
// cascade is running through the lower slots. From the binary: the launch reads the type from
// the manager's slot table (int8 [2][5] at +0x80; the game's own writer stores every
// assignment into BOTH rows), and the release cascade launches slots 0..top-1 in turn, one per
// timer, each at its own power.
//
// ONE COMBINED CAST, as Fable 3 has it. Of the slot's spell and the partner, the higher in the
// hierarchy (spell_weaving.h) is the BASE and is the one spell launched: when the slot holds the
// lower one, the event's type becomes the base's for the length of the game's own launch, so
// position, target, targeted/area and the launch notification (where Souls Combat charges mana,
// when it is enabled) are all the game's. The lower spell, the OVERLAY, is never launched; it is
// cast by the base's hits (woven.cpp): the game's own spell factory makes a Targeted partner
// spell on each victim the base hits, after a short delay, as Fable 3's primary does. A cast
// weaves only up to the Spell Weaving level: level N weaves power N and below.
//
// Spell types: 0 Lightning, 1 Fireball, 2 SlowTime, 3 Swords, 4 Vortex, 5 Chaos, 6 ForcePush,
// 7 DeadRising. Slow Time and Raise Dead are never partners. Learned ranks: the stats
// component's ability levels 7 + type (refii_mod_ability_level).
//
// EACH SLOT HAS ITS OWN PARTNER, as each slot has its own spell. The partner is the ENGINE'S spell
// pick this mod states in ui/spellwheel.json: X on a will on either selector picks it for the
// highlighted slot, Y clears it, and the engine raises SpellPick and keeps the choice as a SAVED
// player value, spell_weaving.partner_<n> (per player, going home with that hero's own save; -1
// none). The pick's "exclude" keeps Slow Time and Raise Dead off it; this file refuses them too
// (SpellPick), so no other surface can store one. What is in effect per slot (chosen, learned, and
// within the Spell Weaving level) is stated beside it, spell_weaving.partner_active_<n>, where the
// hero is played — the selectors read both for the reader's player. Settings (settings.json, live
// through the debug channel's `mod-setting`):
//   spell_weaving.top_only                    1: only the charged slot casts (Fable 3); 0: cascade
//   spell_weaving.allow_same                  1: a partner equal to the slot's spell casts it twice
//   spell_weaving.woven                       1: weaving on; 0: every slot casts its own spell alone

#include <cstdio>
#include <cstring>

#include "core/refii_mod.h"
#include "spell_weaving.h"

namespace spell_weaving {

int PlayerOf(uint8_t* base, uint32_t entity) { return Index(refii_mod_player_of(base, entity)); }

}  // namespace spell_weaving

using namespace spell_weaving;

namespace {

constexpr int kFirstSpellAbility = 7;
constexpr int kSlots = 5;

/// Where saves from before kept the choices: the world save's Lua table.
void LegacyKey(char* out, size_t size, int player, int slot) {
  std::snprintf(out, size, "SpellWeavingPartner%s%d", player == 0 ? "" : "2_", slot + 1);
}

/// The player value holding a slot's partner — the pick's store (ui/spellwheel.json).
void PartnerKey(char* out, size_t size, int slot) { std::snprintf(out, size, "spell_weaving.partner_%d", slot + 1); }

/// What is in effect on a slot, stated where the hero is played (the selectors' "inEffect").
void ActiveKey(char* out, size_t size, int slot) {
  std::snprintf(out, size, "spell_weaving.partner_active_%d", slot + 1);
}

/// A player's chosen partner on a slot (by index), -1 for none.
int Partner(int player, int slot) {
  char key[40];
  PartnerKey(key, sizeof(key), slot);
  const int type = static_cast<int>(refii_mod_player_value(player + 1, key, -1.0f));
  return Weavable(type) ? type : -1;
}

bool HasPartnerValue(int player, int slot) {
  char key[40];
  PartnerKey(key, sizeof(key), slot);
  return refii_mod_player_get(player + 1, key, nullptr) != 0;
}

/// The slot's partner if the hero has learned at least one rank of it, else -1.
int LearnedPartner(uint8_t* base, int player, uint32_t hero, int slot) {
  const int type = Partner(player, slot);
  if (!Weavable(type)) {
    return -1;
  }
  return refii_mod_ability_level(base, hero, kFirstSpellAbility + type) > 0 ? type : -1;
}

/// The partner pick (SpellPick, the engine's, for ui/spellwheel.json's "spell_weaving.partner"):
/// Slow Time and Raise Dead can never be woven, so a pick of either is refused, whatever surface
/// made it. The engine stores the pick after this.
void OnSpellPick(refii::mods::SpellPickEvent& e) {
  if (std::strcmp(e.pick, "spell_weaving.partner") != 0 || e.spell < 0 || Weavable(e.spell)) {
    return;
  }
  REXLOG_INFO("Spell Weaving: {} cannot be a partner; player {} slot {} keeps {}", e.spell, e.player, e.slot + 1,
              e.previous);
  e.cancel = true;
}
REFII_MOD_ON(SpellPick, OnSpellPick);

/// A save from before keeps the choices in its Lua table. The Lua and SaveLoaded events say the
/// table changed; the read waits for the next tick with the hero present, where the game VM is up
/// and a chunk can run.
bool g_reread = true;

void SaveChanged(refii::mods::LuaEvent& e) {
  (void)e;
  g_reread = true;
}
REFII_MOD_ON(Lua, SaveChanged);

void SaveLoaded(refii::mods::SaveLoadedEvent& e) { SaveChanged(e); }
REFII_MOD_ON(SaveLoaded, SaveLoaded);

/// For each player played here, a slot the hero's saved values leave empty takes the old Lua
/// table's choice. Run after every Lua rebuild and save load, so it never races the save's own values.
void ReadSaved(PPCContext* ctx, uint8_t* base) {
  for (int player = 0; player < 2; ++player) {
    for (int slot = 0; slot < kSlots; ++slot) {
      if (refii_mod_player_here(player + 1) == 0 || HasPartnerValue(player, slot)) {
        continue;
      }
      char legacy[40];
      LegacyKey(legacy, sizeof(legacy), player, slot);
      int32_t saved = -1;
      if (refii_mod_save_get_int(ctx, base, legacy, 0, kSpellTypes - 1, &saved) != 0 && Weavable(saved)) {
        char key[40];
        PartnerKey(key, sizeof(key), slot);
        refii_mod_player_save(player + 1, key, static_cast<float>(saved));
        REXLOG_INFO("Spell Weaving: player {} slot {} partner {} taken from the save's Lua table", player + 1, slot + 1,
                    saved);
      }
    }
  }
}

void OnTick(refii::mods::TickEvent& e) {
  if (refii_mod_player_hero(e.base, 1) == 0 && refii_mod_player_hero(e.base, 2) == 0) {
    return;  // no session: neither hero exists
  }

  if (g_reread) {
    g_reread = false;
    ReadSaved(e.ctx, e.base);
  }

  // What is in effect per slot (chosen, learned, and the slot within the Spell Weaving level),
  // stated where the hero is played: the selectors draw the chosen pick faint when it is not this.
  for (int player = 0; player < 2; ++player) {
    const uint32_t hero = refii_mod_player_hero(e.base, player + 1);
    if (hero == 0 || refii_mod_player_here(player + 1) == 0) {
      continue;
    }
    for (int slot = 0; slot < kSlots; ++slot) {
      char key[48];
      ActiveKey(key, sizeof(key), slot);
      const int active = slot < Level(player) ? LearnedPartner(e.base, player, hero, slot) : -1;
      refii_mod_player_set(player + 1, key, static_cast<float>(active));
    }
  }
}
REFII_MOD_ON(Tick, OnTick);

/// THE HUD GLYPH (ui/hud.json): while a hero charges, the partner the charged slot would weave is
/// stated in that player's store as spell_weaving.hud_partner (-1 when none, or not charging),
/// where the hero is played; the glyph reads player:spell_weaving.hud_partner beside each player's
/// own spell column —
/// the charge level (the top slot, 0..4) arrives every frame of the charge through
/// SpellChargeLevel and goes stale a third of a second after the last one; a launch ends it at
/// once. The glyph shows what the launch would weave: the slot's own spell must be weavable and
/// differ from the partner (allow_same aside), read from the manager's slot table, as the launch
/// reads it.
constexpr uint32_t kSpellManagerComponent = 56;
constexpr uint32_t kManagerSlotTable = 0x80;

int SlotSpell(uint8_t* base, uint32_t hero, int slot) {
  const uint32_t manager = refii_mod_component(base, hero, kSpellManagerComponent);
  if (manager == 0) {
    return -1;
  }
  return static_cast<int8_t>(refii::mods::LoadU8(base, manager + kManagerSlotTable + static_cast<uint32_t>(slot)));
}
constexpr float kChargeHoldSeconds = 0.3f;
int g_charging_slot[2] = {-1, -1};
float g_charging_age[2] = {99.0f, 99.0f};

void OnSpellChargeLevel(refii::mods::SpellChargeLevelEvent& e) {
  const int player = Index(e.player);
  if (player < 0) {
    return;
  }
  g_charging_slot[player] = e.level;
  g_charging_age[player] = 0.0f;
}
REFII_MOD_ON(SpellChargeLevel, OnSpellChargeLevel);

void OnSpellLaunched(refii::mods::SpellLaunchedEvent& e) {
  const int player = Index(e.player);
  if (player >= 0) {
    g_charging_slot[player] = -1;
    g_charging_age[player] = 99.0f;
  }
}
REFII_MOD_ON(SpellLaunched, OnSpellLaunched);

void PublishHudPartner(refii::mods::TickEvent& e) {
  for (int player = 0; player < 2; ++player) {
    g_charging_age[player] += e.seconds;
    const uint32_t hero = refii_mod_player_hero(e.base, player + 1);
    if (hero == 0 || refii_mod_player_here(player + 1) == 0) {
      continue;
    }
    const int slot = g_charging_slot[player];
    int partner = -1;
    if (hero != 0 && slot >= 0 && slot < kSlots && g_charging_age[player] <= kChargeHoldSeconds && slot < Level(player) &&
        Knob("spell_weaving.woven", 1.0f) != 0.0f) {
      const int primary = SlotSpell(e.base, hero, slot);
      partner = LearnedPartner(e.base, player, hero, slot);
      if (!Weavable(primary) || (primary == partner && Knob("spell_weaving.allow_same", 0.0f) == 0.0f)) {
        partner = -1;
      }
    }
    refii_mod_player_set(player + 1, "spell_weaving.hud_partner", static_cast<float>(partner));
  }
}
REFII_MOD_ON(Tick, PublishHudPartner);

void OnSpellLaunch(refii::mods::SpellLaunchEvent& e) {
  const int player = Index(e.player);
  if (player < 0 || e.slot < 0 || e.slot >= kSlots) {
    return;
  }

  // Fable 3 pacing: one cast at the charged power. The cascade's lower slots are skipped; the
  // cascade itself runs on and reaches the top slot regardless of what the launch did.
  if (e.cascading && Knob("spell_weaving.top_only", 1.0f) != 0.0f) {
    e.cancel = true;
    return;
  }

  const int primary = e.type;
  const int partner = LearnedPartner(e.base, player, e.caster, e.slot);
  const bool weave = Knob("spell_weaving.woven", 1.0f) != 0.0f && e.slot + 1 <= Level(player) && Weavable(primary) &&
                     partner >= 0 && (primary != partner || Knob("spell_weaving.allow_same", 0.0f) != 0.0f);
  if (!weave) {
    return;
  }

  const int top = kRank[primary] <= kRank[partner] ? primary : partner;
  const int under = top == primary ? partner : primary;
  // The base is the one cast; the overlay is never launched here — woven.cpp binds the pair to
  // the spell this launch creates and casts the overlay on each victim that spell hits.
  ArmWeave(player, top, under, e.slot);
  e.type = top;
  REXLOG_INFO("Spell Weaving: cast {} carrying {} at power {} for player {} (slot held {})", top, under, e.slot + 1,
              player + 1, primary);
}
REFII_MOD_ON(SpellLaunch, OnSpellLaunch);

}  // namespace
