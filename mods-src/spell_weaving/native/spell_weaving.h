// Spell Weaving — the state the native parts share.

#pragma once

#include <cstdint>

#include "core/refii_mod.h"

namespace spell_weaving {

constexpr int kMaxLevel = 5;

/// EACH PLAYER'S CHOICES LIVE IN THE PLAYER STORE (refii_mod_player_*, core/refii_mod.h), SAVED with
/// the hero, so they go home with that hero's save and are the same on both co-op machines:
///   spell_weaving.ability       the Spell Weaving level, 0..kMaxLevel: the ENGINE buys and unlearns it
///                               from ui/abilities.json (price, refund, saves from before); level N
///                               weaves casts up to power N, 0 weaves nothing
///   spell_weaving.partner_1..5  the partner picked for each slot, -1 for none: the ENGINE's spell
///                               pick of ui/spellwheel.json writes it (weave.cpp refuses Slow Time
///                               and Raise Dead)
/// and, stated where the hero is played (session values): spell_weaving.partner_active_1..5 (what
/// is in effect per slot) and spell_weaving.hud_partner (the glyph while charging) — weave.cpp.
inline constexpr const char* kAbilityKey = "spell_weaving.ability";

/// A player's Spell Weaving level, by player index (0 player 1, 1 player 2).
inline int Level(int index) {
  const int level = static_cast<int>(refii_mod_player_value(index + 1, kAbilityKey, 0.0f));
  return level < 0 ? 0 : (level > kMaxLevel ? kMaxLevel : level);
}

constexpr int kSpellTypes = 8;
constexpr int kLightning = 0;
constexpr int kFireball = 1;
constexpr int kSlowTime = 2;
constexpr int kSwords = 3;
constexpr int kVortex = 4;
constexpr int kChaos = 5;
constexpr int kForcePush = 6;
constexpr int kDeadRising = 7;

/// THE HIERARCHY: Vortex > Blades > Fireball > Shock > Force Push > Chaos — Fable 3's own
/// primaries (the vortex casts its partner per creature it holds, a blade per stab, a fireball
/// on impact). Of a woven pair the higher spell is the BASE, the one cast; the lower is the
/// OVERLAY, cast on the base's victims and applied onto the base's look. Lower rank is higher.
constexpr int kRank[kSpellTypes] = {3, 2, 99, 1, 0, 5, 4, 99};

inline bool Weavable(int type) {
  return type >= 0 && type < kSpellTypes && type != kSlowTime && type != kDeadRising;
}

/// ONE WEAVE PER CAST. Every woven release is its own record from its launch until its base has
/// finished: the base's serial (what its hits carry), the pair, the power, and what the look
/// rides on — the vortex entity, the fireball's shot, each of the Blades' swords. Two vortices
/// from two slots keep two partners; a slot re-picked between casts changes nothing already out.
struct Weave {
  /// The base cast's serial, 0 for a free record.
  uint32_t serial = 0;
  /// The base's spell object, as the factory returned it.
  uint32_t spell = 0;
  int player = -1;
  int base = -1;
  int overlay = -1;
  /// The power the base was cast at, 0..4.
  int level = 0;
  /// The entity the look rides: the vortex entity, the fireball's shot entity; 0 when none.
  uint32_t anchor = 0;
  /// The live vortex component, when the base is a vortex.
  uint32_t vortex = 0;
  /// The Blades' swords, when the base is blades: the guest's own weak-reference bodies from the
  /// cast's controller (0 = none), and the entity each resolved to last (0 once the sword is gone).
  uint32_t sword_refs[8] = {0, 0, 0, 0, 0, 0, 0, 0};
  uint32_t swords[8] = {0, 0, 0, 0, 0, 0, 0, 0};
  /// The Blades' Start has stated its swords (it runs after the factory, on the cast's first update).
  bool swords_read = false;
  /// Each sword carries its partner's tint (woven.cpp TintSwords): set once, held until it dies.
  bool sword_tinted[8] = {false, false, false, false, false, false, false, false};
  /// Where the cast started: the fallback position of the look.
  float origin[3] = {0.0f, 0.0f, 0.0f};
  /// The victims the base hit, for a base with no thing of its own in the world (lightning,
  /// push, chaos): the look rides on them while the base holds.
  uint32_t victims[8] = {0, 0, 0, 0, 0, 0, 0, 0};
  float victim_at[8] = {0, 0, 0, 0, 0, 0, 0, 0};
  /// Seconds since the launch, and whether the base has finished (the look then fades out).
  float age = 0.0f;
  float done_at = -1.0f;
  /// THE LOOK, PLAYED PER CAST (woven.cpp): the pair's own effect (fx/effects/spell_weaving_<base>_<overlay>)
  /// on each thing it rides — `look_on[i]` the entity (0 for the one play at a point), `look_id[i]`
  /// the id refii_mod_play_fx gave it (0 for a free slot), stopped with refii_mod_stop_fx.
  static constexpr int kMaxLooks = 10;
  uint32_t look_on[kMaxLooks] = {0, 0, 0, 0, 0, 0, 0, 0, 0, 0};
  uint32_t look_id[kMaxLooks] = {0, 0, 0, 0, 0, 0, 0, 0, 0, 0};
};

constexpr int kMaxWeaves = 8;
extern Weave g_weaves[kMaxWeaves];

/// From the launch: the pair the next created spell of `player` is (woven.cpp binds it).
void ArmWeave(int player, int base, int overlay, int level);

/// The live weave whose base cast carries `serial`, or null.
Weave* WeaveBySerial(uint32_t serial);

/// A tunable: the mod's settings.json value under `key`, or the constant it replaces.
inline float Knob(const char* key, float fallback) { return refii::mods::Setting(key, fallback); }

/// The pool index of an event's `player` field (1 or 2): 0 or 1, and -1 for anything else.
inline int Index(int32_t player) { return player == 1 ? 0 : (player == 2 ? 1 : -1); }

/// 0 for player 1's hero, 1 for player 2's, -1 for anything else.
int PlayerOf(uint8_t* base, uint32_t entity);

}  // namespace spell_weaving
