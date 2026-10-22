// Spell Weaving — every woven cast is its own weave: the base's hits cast the partner, as Fable 3
// does it, and the look rides on what that cast put in the world.
//
// Read from Fable III's binary (see /home/loreaxe/Fable 2/Fable 3/README.md): one cast makes one
// primary spell; at the primary's own "victim affected" moment its class issues a SECOND, real,
// Targeted cast of the partner spell on that victim — the vortex per creature it grabs, the
// fireball on impact, lightning per victim hit, each blade on its stab — through a delayed caster
// (WeaveSpellShotDelayTime, 0.25 s in the vortex records, ±5 %), with the partner's balance at
// 0.7 damage and duration. Burning, shock, push, blades: the partner spell's own code.
//
// ONE WEAVE PER CAST (spell_weaving.h). The launch arms the pair (weave.cpp); the SpellCreated
// event that the same launch raises binds it to the base's spell object and serial; every hit
// that carries that serial casts THAT weave's partner on its victim, however many casts are out
// and whatever the slot's partner has become since.
//
// THE LOOK IS AN EFFECT, PLAYED PER CAST (UpdateLooks). Each pair has its own mod effect,
// fx/effects/spell_weaving_<base>_<overlay>.json, built from the particle bank's own entities so
// the guest's bank carries it and BOTH looks draw it. A weave plays it with refii_mod_play_fx_on on
// each thing its base put in the world — the vortex entity, the fireball's shot, each live sword,
// each victim of a bolt, shove or chaos for its hold — and refii_mod_stop_fx ends each play when
// that thing is gone; with nothing to ride (a fireball's blast, an instant cast that hit nobody)
// it plays once at the point. Two vortices from two slots each carry their own partner's look,
// and fx/recipes.json tints each base effect by the play bound to it ("with"), not by a table.
// THE VORTEX ENTITY TRAVELS WITH ITS TWISTER: the component's update ends by writing the
// twister's point to +0x140 and placing its entity there (sub_82759928, 0x82759bdc..0x82759bf4),
// so the play rides the entity. The host draws an entity with no body where its own live effects
// are (Engine_FxPlayer) under the modern look; under the classic look the guest instance is placed
// at the entity at the end of that same update (src/vfs/src/hooks/spell_fx_owner.cpp), and the
// recipe's `with` binds the base effects the vortex's, the fireball shot's and each sword's own
// update states as theirs — never another cast's, never by distance.
// Each machine plays its own copy: a weave is bound wherever the cast runs (both machines online),
// and a play draws only on the machine that asked for it.
//
// The events (core/refii_mod_events.h): SpellCreated binds the armed pair; Hit, before delivery,
// scales a weave-side spell's damage; HitLanded, after it, queues the partner on the victim.
//
// CO-OP: NO PLUMBING OF ITS OWN; THE MOD API'S RULES. Online, a cast runs on both machines and
// the host's copy is the spell that deals the damage: Hit and HitLanded are decided on the host.
// So partners are cast where refii_mod_world_here holds (the host, or the one machine alone and in
// local co-op) — player two's included — and nowhere else. The API relays each HitLanded with a
// hero in it to the joiner as a replica (refii_mod_event_replica), so the joiner's look follows
// the base's real victims too: a replica only notes its victim, it never casts. Both players are
// the same code: every record is keyed by the caster's player (refii_mod_player_of).
//
// Still hooked by name, as no event covers them (TU1 / disc, named in versions/*/refii_config.toml):
//   rex_VortexComponent_Init / _Shutdown: the live vortex. The registry lookup never returns a
//     Vortex: the vortex spell object marks itself finished in its own post-init (0x828b20b4
//     targeted, 0x828b26f8 area), having only spawned the vortex entity; the live vortex is that
//     entity's component 211, stated by Init (r3 component, owner entity at +4; r4 caster) —
//     inside the factory, before SpellCreated — and Shutdown (r3 component).
//   rex_TargetedFireball_Impact (0x827441F0 / 0x827192F0): the targeted fireball's detonation —
//     r3 the fireball spell object, r4 what it struck (an entity, 0 for ground/wall/water), v1 the
//     impact position, v2 the impact normal. Runs more than once per fireball; the detonation is
//     the exploded byte at spell+0x48 going 0 -> 1 inside it (guard `lbz r11,0x48(r31)` 0x82744234,
//     set `stb r24,0x48(r31)` 0x827446e4). Serial spell+0x44, level spell+0x40, the shot entity
//     the projectile's effects ride behind the weak ref at spell+0x10 (its box at +0x14,
//     sub_82740568 = *(*(spell+0x14))). A detonation that hits nothing
//     alive is Fable 3's "fireball on impact" with no victim: the partner is cast at the impact point.
//   The Blades' swords need no hook: the factory (rex_CSpellRegistry_Create) runs the new
//     spell's vtable slot 1 itself (`lwz r8,4(r9); bctrl` 0x824b1ef8-0x824b1f00), and for the
//     Blades that is the Start that builds the sword controller (sub_825A2D10, 240 bytes) and
//     fills it (sub_825A2E68): +0 the sword count (at most 8), +116 eight sword records
//     (sub_825A5EF8, 640 bytes each). THE CLASS DECIDES WHERE THE CONTROLLER SITS: a targeted
//     cast (r5 set) builds the class at vtable 0x820C4F30, whose Start sub_825A2188 keeps it at
//     spell+0xE4 (`stw r3,0xE4(r31)` 0x825a21d4); an area cast builds 0x820C4F4C, whose Start
//     sub_825A2720 keeps it at spell+0xC8 (0x825a28e0). So by SpellCreated the swords are there,
//     at the offset the event's `targeted` names. Each record's +0 is a weak reference (vtable,
//     body at +4) to the sword entity sub_825A6198 creates from the "DamoclesSword" definition,
//     named "SpellShotDamoclesSword": the entity is *(body) while it lives, and a sword dies 0.35 s
//     after its stab (state 8), its alive bit cleared at once (0x8238d9b8).
//   EACH SWORD WEARS ITS PARTNER'S TINT (TintSwords), through the game's own appearance tint setter
//     (refii_mod_entity_tint: sub_82573770 on the sword's component 3), so the entity feed draws it
//     under both looks and every machine running the cast tints its own copy. The sword's update
//     (sub_825A63A8) restates its SCALE (sub_825F9168 0x825a6558) and ALPHA (sub_825F8FE0
//     0x825a65a8: the definition's +40 x the record's fade) every frame and never its tint: no
//     function of the controller (0x825A2000..0x825A9800) calls either tint setter (sub_82573570,
//     sub_82573770) or the entity tint helper sub_825F92E0, and the setters are reached through no
//     pointer (their only data references are .pdata) — so the tint is set once and held. A sword dies 0.35 s after its stab (sub_825A6698 destroys it, 0x825a678c);
//     its successor at that address is a new component whose constructor states {1, 1, 1, 1}
//     (sub_82571DF8 0x82571f04), so nothing is put back. The colours are settings.json's
//     spell_weaving.sword_tint_<partner>_r/g/b, multipliers of the golden blade's lit colour.
//   THE FACTORY ALSO RESOLVES INSTANT CASTS INSIDE ITSELF: a targeted Lightning's bolt lands its
//     hit from that same slot-1 Start (sub_827407F0 -> sub_825FA680 0x827413cc), BEFORE SpellCreated
//     reports the object. So a base's hit may arrive with no weave yet: the armed pair binds on the
//     hit (OnHitLanded) and SpellCreated then completes the record it finds by serial.

#include <cmath>
#include <cstdio>

#include "core/refii_mod.h"
#include "spell_weaving.h"

REFII_MOD_ORIGINAL(rex_VortexComponent_Init);
REFII_MOD_ORIGINAL(rex_VortexComponent_Shutdown);
REFII_MOD_ORIGINAL(rex_TargetedFireball_Impact);

using namespace spell_weaving;

namespace spell_weaving {

Weave g_weaves[kMaxWeaves];

}  // namespace spell_weaving

namespace {

constexpr uint32_t kSpellManager = 56;
constexpr uint32_t kSpellSerial = 0x44;
constexpr uint32_t kSpellLevel = 0x40;
constexpr uint32_t kSpellExploded = 0x48;
constexpr uint32_t kSpellCasterRef = 0x1C;
constexpr uint32_t kSpellShotBox = 0x14;  // the shot entity's weak ref: +0x10 vtable, +0x14 box
constexpr uint32_t kSpellBladesControllerArea = 0xC8;      // sub_825A2720 `stw r3,0xC8(r31)` 0x825a28e0
constexpr uint32_t kSpellBladesControllerTargeted = 0xE4;  // sub_825A2188 `stw r3,0xE4(r31)` 0x825a21d4
constexpr uint32_t kControllerCount = 0;
constexpr uint32_t kControllerSlots = 116;  // eight sword records; record+0 vtable, +4 the weak-ref body
constexpr uint32_t kRecordRefBody = 4;
constexpr int kLevels = 5;

bool g_applying = false;
float g_clock = 0.0f;
uint32_t g_rand = 0x9E3779B9u;

/// The pair the next created base spell of each player is, armed by the launch.
struct Armed {
  bool set = false;
  int base = -1;
  int overlay = -1;
  int level = 0;
  /// The vortex component stated inside the factory, before the spell object is reported.
  uint32_t vortex = 0;
};
Armed g_armed[2];

/// The partner cast in progress (CastPartner): its hits land inside the factory, before the
/// cast returns its serial, so they are known by caster and type while it runs.
struct Casting {
  bool on = false;
  int player = -1;
  int type = -1;
  uint32_t hero = 0;
};
Casting g_casting;

bool Plausible(uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; }

/// How long a base's look holds after the base finished (or, for an instant base, after the cast).
float HoldSeconds(int base) {
  switch (base) {
    case kLightning:
      return 1.5f;
    case kForcePush:
      return 1.5f;
    case kChaos:
      return 6.0f;
    case kFireball:
      return 1.5f;
    default:
      return 0.75f;
  }
}

/// The sword the weave's record `i` still refers to, by the guest's own weak reference: the body
/// {entity, refcount} holds the entity while it lives. A sword dies 0.35 s after its stab (state 8,
/// sub_825A8528): the destroy (sub_8238D828) clears the alive bit at entity+0x90 at once
/// (`0x8238d9b8`), the record releases the body, and the body is freed once nothing holds it — so
/// a kept body pointer can dangle. The entity is taken only while it still points back at this
/// body (entity+4, what sub_82214898 reads) and carries the alive bit. 0 when gone.
uint32_t SwordOf(uint8_t* base, Weave& w, int i) {
  const uint32_t body = w.sword_refs[i];
  const uint32_t entity = Plausible(body) ? refii::mods::LoadU32(base, body) : 0u;
  const bool live = Plausible(entity) && refii::mods::LoadU32(base, entity + 4u) == body &&
                    refii_mod_entity_alive(base, entity) != 0;
  w.swords[i] = live ? entity : 0u;
  if (!live) {
    w.sword_refs[i] = 0;  // gone for good; never read the body again
  }
  return w.swords[i];
}

/// Whether the weave's base is still out: the vortex component live, a sword hovering, the
/// fireball not yet detonated, an instant cast within its hold.
bool BaseLive(uint8_t* base, Weave& w) {
  switch (w.base) {
    case kVortex:
      return w.vortex != 0;
    case kSwords: {
      if (!w.swords_read) {
        return w.age < 3.0f;  // the Start has not stated the swords yet
      }
      for (int i = 0; i < 8; ++i) {
        if (SwordOf(base, w, i) != 0) {
          return true;
        }
      }
      return false;
    }
    case kFireball:
      return w.done_at < 0.0f && w.age < 6.0f;
    default:
      for (int i = 0; i < 8; ++i) {
        if (w.victims[i] != 0 && g_clock - w.victim_at[i] <= HoldSeconds(w.base)) {
          return true;
        }
      }
      return w.done_at < 0.0f && w.age < 1.0f;
  }
}

/// Ends every play of a weave's look.
void StopLooks(Weave& w) {
  for (int i = 0; i < Weave::kMaxLooks; ++i) {
    if (w.look_id[i] != 0) {
      refii_mod_stop_fx(w.look_id[i]);
    }
    w.look_id[i] = 0;
    w.look_on[i] = 0;
  }
}

Weave* FreeWeave() {
  Weave* oldest = &g_weaves[0];
  for (Weave& w : g_weaves) {
    if (w.serial == 0) {
      return &w;
    }
    if (w.age > oldest->age) {
      oldest = &w;
    }
  }
  StopLooks(*oldest);
  *oldest = Weave{};
  return oldest;
}

/// The weave of a live spell object — by the serial it carries, as the object's address is
/// reused by the next cast of the same class.
Weave* WeaveBySpell(uint8_t* base, uint32_t spell) {
  const uint32_t serial = refii_mod_spell_serial(base, spell);
  for (Weave& w : g_weaves) {
    if (w.serial != 0 && w.serial == serial) {
      return &w;
    }
  }
  return nullptr;
}

Weave* WeaveByVortex(uint32_t component) {
  for (Weave& w : g_weaves) {
    if (w.serial != 0 && w.vortex == component) {
      return &w;
    }
  }
  return nullptr;
}

/// One partner already queued or cast for a victim by one base cast.
struct Recent {
  uint32_t victim = 0;
  uint32_t serial = 0;
  int type = -1;
  float until = 0.0f;
};

Recent g_recent[64];
int g_recent_next = 0;

/// Once per victim per base cast, and never twice within the cooldown.
bool Fresh(uint32_t victim, uint32_t serial, int type) {
  const float hold = Knob("spell_weaving.woven_cooldown_seconds", 0.5f);
  for (Recent& r : g_recent) {
    if (r.victim == victim && r.type == type) {
      if (r.serial == serial || r.until > g_clock) {
        return false;
      }
      r.serial = serial;
      r.until = g_clock + hold;
      return true;
    }
  }
  Recent& slot = g_recent[g_recent_next];
  g_recent_next = (g_recent_next + 1) % 64;
  slot = Recent{victim, serial, type, g_clock + hold};
  return true;
}

/// A partner cast waiting for its delay: on a victim, or — a fireball that hit nothing alive —
/// at a point, facing a direction.
struct Pending {
  bool armed = false;
  int player = -1;
  int type = -1;
  int level = 0;
  uint32_t victim = 0;
  float at = 0.0f;
  float where[3] = {0.0f, 0.0f, 0.0f};
  float facing[3] = {0.0f, 0.0f, 1.0f};
  /// Where the base that queued it stands: a push is away from here.
  float origin[3] = {0.0f, 0.0f, 0.0f};
};

Pending g_pending[24];

/// Partner casts queued so far: a base's moment that hit nobody is told by this not moving.
uint32_t g_queued = 0;

/// The serials of the partner spells this mod cast: their hits are the weave side.
uint32_t g_weave_serials[32];
int g_weave_next = 0;

bool IsWeaveSerial(uint32_t serial) {
  if (serial == 0) {
    return false;
  }
  for (uint32_t s : g_weave_serials) {
    if (s == serial) {
      return true;
    }
  }
  return false;
}

float Jitter() {
  // Fable 3: value * (1 + 0.1 * (rand - 0.5)) — within five per cent either way.
  g_rand = g_rand * 1664525u + 1013904223u;
  return 1.0f + 0.1f * (static_cast<float>(g_rand >> 8) / 16777216.0f - 0.5f);
}

/// The partner spell, cast by the game's own factory with the hero as caster: a Targeted cast
/// on the victim, or — a fireball that hit nothing alive — on no target at the impact point.
///
/// WHERE EACH PARTNER STARTS — FABLE III'S POSITION CAST. Read from Fable III's classes (its
/// disc build): every weave request carries "shot from the caster" = 0 (spell+60), and the
/// Lightning, Fireball and Force Push classes branch on that byte, never on the weave flag:
/// no projectile, no bolt from the caster, no hand effect, no caster/target glow — the spell
/// resolves at once at the request position (the creature the vortex holds, the blade's target,
/// the fireball's impact point) with its "TargetedImpactFX" there and its hit on the target
/// (Lightning Start sub_82B02B10 @0x82b02b58, Fireball Start sub_82AF7F60 @0x82af8110, Force
/// Push Start sub_82AFFB60 @0x82affbd8). The pair reads as ONE spell whose hits carry the
/// second effect.
///
/// Fable 2's targeted classes place from v1 (spell+0x30) along v2 (+0x60) with the target at
/// +0x70: Lightning (sub_827407F0) draws its bolt from v1 to a point on the target, Fireball
/// (sub_82744AE0) spawns its shot at v1, Force Push (sub_828B2CC8) pushes the target away from
/// v1. So the position cast is built by starting the partner ON the victim:
///   Lightning: v1 = the victim — a zero-length bolt, the strike's impact on the victim only.
///   Fireball: v1 = the victim — the shot detonates where it spawns.
///   Force Push: v1 = the BASE's own place (the vortex, the fireball's impact, the stab), so
///     the victim is shoved away from the base, as Fable III's request direction has it.
///   Vortex, Blades, Chaos: at the victim.
/// With no victim (a fireball that struck nothing alive) the cast has no target, and an
/// untargeted Lightning draws v1 + 15 x v2 then rolls a 50% re-acquire of a random creature
/// within 1.35 of v1's height: it starts 15 above the point striking straight down, so the
/// re-acquire finds nobody and the strike lands where the fireball did.
void CastPartner(PPCContext& ctx, uint8_t* base, const Pending& cast, const float* base_origin) {
  const uint32_t hero = refii_mod_player_hero(base, cast.player + 1);
  if (hero == 0 || (cast.victim != 0 && !refii_mod_entity_alive(base, cast.victim))) {
    return;
  }
  const bool at_point = cast.victim == 0;
  float target[3] = {cast.where[0], cast.where[1], cast.where[2]};
  if (!at_point && refii_mod_entity_position(&ctx, base, cast.victim, target) == 0) {
    return;
  }
  // The request direction: from the base to the victim (Fable III: creature - vortex, impact -
  // spell), the cast's own facing when the base stands on the victim.
  float dir[3] = {target[0] - base_origin[0], target[1] - base_origin[1], target[2] - base_origin[2]};
  const float length = std::sqrt(dir[0] * dir[0] + dir[1] * dir[1] + dir[2] * dir[2]);
  if (length >= 0.25f) {
    for (int i = 0; i < 3; ++i) {
      dir[i] /= length;
    }
  } else {
    dir[0] = cast.facing[0];
    dir[1] = cast.facing[1];
    dir[2] = cast.facing[2];
    const float f = std::sqrt(dir[0] * dir[0] + dir[1] * dir[1] + dir[2] * dir[2]);
    if (f > 0.001f) {
      for (int i = 0; i < 3; ++i) {
        dir[i] /= f;
      }
    } else {
      dir[0] = 1.0f;
      dir[1] = 0.0f;
      dir[2] = 0.0f;
    }
  }
  float start[3] = {target[0], target[1], target[2]};
  float facing[3] = {dir[0], dir[1], dir[2]};
  if (at_point && cast.type == kLightning) {
    // Height is lane 2 (the targeted classes compare heights on it).
    start[2] = target[2] + 15.0f;
    facing[0] = 0.0f;
    facing[1] = 0.0f;
    facing[2] = -1.0f;
  } else if (cast.type == kForcePush) {
    for (int i = 0; i < 3; ++i) {
      start[i] = length >= 0.25f ? base_origin[i] : target[i] - dir[i];
    }
  }
  // The cast's hits may land inside the factory (a Lightning's bolt does): OnHit knows them by
  // this while the call runs, and records the serial for the ones that land later.
  g_casting = Casting{true, cast.player, cast.type, hero};
  const uint32_t spell = refii_mod_cast_spell(&ctx, base, hero, cast.type, 1, cast.victim, cast.level, start, facing);
  g_casting = Casting{};
  const uint32_t serial = refii_mod_spell_serial(base, spell);
  if (serial != 0 && !IsWeaveSerial(serial)) {
    g_weave_serials[g_weave_next] = serial;
    g_weave_next = (g_weave_next + 1) % 32;
  }
  if (!at_point) {
    REXLOG_INFO("Spell Weaving: partner {} cast on {:#x} at power {} for player {} (spell {:#x} serial {})", cast.type,
                cast.victim, cast.level + 1, cast.player + 1, spell, serial);
  } else {
    REXLOG_INFO("Spell Weaving: partner {} cast at ({:.1f}, {:.1f}, {:.1f}) at power {} for player {} (spell {:#x} serial {})",
                cast.type, cast.where[0], cast.where[1], cast.where[2], cast.level + 1, cast.player + 1, spell, serial);
  }
}

/// Queues the partner on a victim. One pending cast per victim and type: a second request
/// refreshes it, as Fable 3's delayed caster reuses the slot of the same spell type. Counted
/// (g_queued) on every request, refreshed or new: the fireball's "struck nothing alive" test
/// asks whether its blast asked for any partner at all.
void QueuePartner(int player, int type, int level, uint32_t victim, float delay, const float* origin) {
  ++g_queued;
  for (Pending& p : g_pending) {
    if (p.armed && p.player == player && p.type == type && p.victim == victim) {
      p.level = level;
      return;
    }
  }
  for (Pending& p : g_pending) {
    if (!p.armed) {
      p = Pending{true, player, type, level, victim, g_clock + delay};
      for (int i = 0; i < 3; ++i) {
        p.origin[i] = origin[i];
      }
      return;
    }
  }
}

/// Queues the partner at a point with no victim — each impact is its own cast.
void QueuePartnerAt(int player, int type, int level, const float* where, const float* facing, float delay) {
  for (Pending& p : g_pending) {
    if (!p.armed) {
      p = Pending{true, player, type, level, 0u, g_clock + delay};
      ++g_queued;
      for (int i = 0; i < 3; ++i) {
        p.where[i] = where[i];
        p.facing[i] = facing[i];
        p.origin[i] = where[i];
      }
      return;
    }
  }
}

/// Whether `entity` is already one of another weave's swords.
bool SwordTaken(uint32_t entity, const Weave& self) {
  for (const Weave& w : g_weaves) {
    if (w.serial == 0 || &w == &self) {
      continue;
    }
    for (uint32_t sword : w.swords) {
      if (sword == entity) {
        return true;
      }
    }
  }
  return false;
}

/// The Blades' swords of a cast, as its Start stated them inside the factory: the controller
/// (at the offset its class keeps it, see the header comment), its count at +0 and its sword
/// records at +116, each record's weak-reference body at +4. Keeps the bodies; the entities are
/// resolved through them for as long as the guest keeps them.
void ReadSwords(uint8_t* base, Weave& w, uint32_t controller) {
  w.swords_read = true;
  if (!Plausible(controller)) {
    REXLOG_WARN("Spell Weaving: blades cast serial {} reported with no controller ({:#x})", w.serial, controller);
    return;
  }
  int count = static_cast<int32_t>(refii::mods::LoadU32(base, controller + kControllerCount));
  if (count < 0 || count > 8) {
    count = 8;
  }
  int found = 0;
  for (int i = 0; i < count; ++i) {
    const uint32_t record = refii::mods::LoadU32(base, controller + kControllerSlots + 4u * static_cast<uint32_t>(i));
    const uint32_t body = Plausible(record) ? refii::mods::LoadU32(base, record + kRecordRefBody) : 0u;
    const uint32_t entity = Plausible(body) ? refii::mods::LoadU32(base, body) : 0u;
    if (Plausible(entity) && !SwordTaken(entity, w)) {
      w.sword_refs[found] = body;
      w.swords[found] = entity;
      ++found;
    }
  }
  REXLOG_INFO("Spell Weaving: blades cast serial {} holds {} sword(s) of {} (controller {:#x})", w.serial, found, count,
              controller);
}

/// The partner's tint for a Blades sword, {r, g, b}: the mod's settings over these defaults. The
/// blade is gold and emissive, and the tint multiplies its albedo, emission and specular alike, so a
/// colour reads by what it keeps of the gold — a lane above 1 is what lets blue and purple show on it.
/// Force Push keeps no glow: a dark slate steel, about a third of the gold's light, that reads as a
/// heavier, solid blade — the solidity the tint can give, as opacity is the sword's own fade's (the
/// tint's alpha is drawn by no shader). Tuned in the weave lab (FxWeaveLab REFII_LAB_SWORD_TINT).
/// False for a partner with none.
bool SwordTint(int overlay, float* rgb) {
  struct Default {
    int partner;
    const char* name;
    float rgb[3];
  };
  static constexpr Default kDefaults[] = {
      {kFireball, "fire", {1.35f, 0.24f, 0.10f}},
      {kLightning, "shock", {0.35f, 0.75f, 3.0f}},
      {kChaos, "chaos", {0.5f, 0.22f, 3.0f}},
      {kForcePush, "push", {0.28f, 0.32f, 0.55f}},
  };
  for (const Default& d : kDefaults) {
    if (d.partner != overlay) {
      continue;
    }
    static const char kLanes[3] = {'r', 'g', 'b'};
    for (int i = 0; i < 3; ++i) {
      char key[64];
      std::snprintf(key, sizeof(key), "spell_weaving.sword_tint_%s_%c", d.name, kLanes[i]);
      rgb[i] = Knob(key, d.rgb[i]);
    }
    return true;
  }
  return false;
}

/// Tints each live sword of a Blades weave by its partner, once (see the header comment).
void TintSwords(PPCContext* ctx, uint8_t* base, Weave& w) {
  if (w.base != kSwords) {
    return;
  }
  float rgb[3];
  bool known = false;
  for (int i = 0; i < 8; ++i) {
    const uint32_t sword = w.sword_tinted[i] ? 0u : SwordOf(base, w, i);
    if (sword == 0) {
      continue;
    }
    if (!known && !SwordTint(w.overlay, rgb)) {
      return;
    }
    known = true;
    if (refii_mod_entity_tint(ctx, base, sword, rgb[0], rgb[1], rgb[2], 1.0f) != 0) {
      w.sword_tinted[i] = true;
    }
  }
}

/// A victim the base hit, kept (newest over oldest) for the looks that ride on victims.
void NoteVictim(Weave& w, uint32_t victim) {
  if (victim == 0) {
    return;
  }
  int slot = 0;
  for (int i = 0; i < 8; ++i) {
    if (w.victims[i] == victim) {
      w.victim_at[i] = g_clock;
      return;
    }
    if (w.victim_at[i] < w.victim_at[slot]) {
      slot = i;
    }
  }
  for (int i = 0; i < 8; ++i) {
    if (w.victims[i] == 0) {
      slot = i;
      break;
    }
  }
  w.victims[slot] = victim;
  w.victim_at[slot] = g_clock;
}

/// The pair's own effect (fx/effects): spell_weaving_<base>_<overlay>; null for a pair with none
/// (a spell woven with itself under allow_same).
const char* LookName(int base, int overlay) {
  static const char* const kNames[kSpellTypes] = {"shock", "fire", nullptr, "blades", "vortex", "chaos", "push", nullptr};
  static char names[kSpellTypes][kSpellTypes][48];
  if (base < 0 || base >= kSpellTypes || overlay < 0 || overlay >= kSpellTypes || base == overlay || kNames[base] == nullptr ||
      kNames[overlay] == nullptr) {
    return nullptr;
  }
  char* name = names[base][overlay];
  if (name[0] == 0) {
    std::snprintf(name, sizeof(names[base][overlay]), "spell_weaving_%s_%s", kNames[base], kNames[overlay]);
  }
  return name;
}

/// Whether a base's look is one burst per rider rather than a look that lasts: an instant base
/// (a bolt, a shove) casts its partner once per victim, and its pair's look is Fable III's
/// half-second burst (fxspl_lightning_push), so a repeat over the victim's hold would pulse.
bool PlaysOnce(int base) { return base == kLightning || base == kForcePush; }

/// What a weave's look rides this tick: entities (each its own play), or — with none, while the
/// base is out — the point at the weave's origin (entity 0). Each sword, each victim of a bolt,
/// shove or chaos within its hold, else the anchor (the vortex, the shot in flight), else the
/// origin (an instant cast that hit nobody). A base's end (the vortex's shutdown, the fireball's
/// blast) ends its look: a play restarted at the end point would pop for its short hold.
int Riders(uint8_t* base, Weave& w, bool live, uint32_t* out) {
  int n = 0;
  if (w.base == kSwords) {
    for (int i = 0; i < 8 && n < Weave::kMaxLooks; ++i) {
      if (const uint32_t sword = SwordOf(base, w, i); sword != 0) {
        out[n++] = sword;
      }
    }
    return n;
  }
  if (w.base == kLightning || w.base == kForcePush || w.base == kChaos) {
    for (int i = 0; i < 8 && n < Weave::kMaxLooks; ++i) {
      if (w.victims[i] != 0 && g_clock - w.victim_at[i] <= HoldSeconds(w.base) && refii_mod_entity_alive(base, w.victims[i])) {
        out[n++] = w.victims[i];
      }
    }
    if (n != 0) {
      return n;
    }
  }
  if (w.anchor != 0 || live) {
    out[n++] = w.anchor;  // 0: the point
  }
  return n;
}

/// Each live weave plays its look on what it rides, and stops each play whose rider has gone.
void UpdateLooks(uint8_t* base) {
  for (Weave& w : g_weaves) {
    if (w.serial == 0) {
      continue;
    }
    const bool live = BaseLive(base, w);
    const bool holding = !live && w.done_at >= 0.0f && g_clock - w.done_at <= HoldSeconds(w.base);
    const char* look = LookName(w.base, w.overlay);
    uint32_t riders[Weave::kMaxLooks];
    const int count = (live || holding) && look != nullptr ? Riders(base, w, live, riders) : 0;

    for (int i = 0; i < Weave::kMaxLooks; ++i) {
      if (w.look_id[i] == 0) {
        continue;
      }
      bool wanted = false;
      for (int r = 0; r < count && !wanted; ++r) {
        wanted = riders[r] == w.look_on[i];
      }
      if (!wanted) {
        refii_mod_stop_fx(w.look_id[i]);
        w.look_id[i] = 0;
        w.look_on[i] = 0;
      }
    }

    for (int r = 0; r < count; ++r) {
      bool playing = false;
      int free = -1;
      for (int i = 0; i < Weave::kMaxLooks; ++i) {
        playing = playing || (w.look_id[i] != 0 && w.look_on[i] == riders[r]);
        free = free < 0 && w.look_id[i] == 0 ? i : free;
      }
      if (playing || free < 0) {
        continue;
      }
      refii_mod_fx_options options = refii_mod_fx_default_options();
      options.loop = PlaysOnce(w.base) ? 0 : 1;
      if (riders[r] != 0) {
        w.look_id[free] = refii_mod_play_fx_on(look, riders[r], &options);
      } else {
        // At a point: bounded, so a play whose stop never comes (the mod unmounted) still ends.
        options.duration = HoldSeconds(w.base) + 8.0f;
        w.look_id[free] = refii_mod_play_fx(look, w.origin, &options);
      }
      w.look_on[free] = riders[r];
    }
  }
}

}  // namespace

namespace spell_weaving {

void ArmWeave(int player, int base, int overlay, int level) {
  if (player < 0 || player > 1) {
    return;
  }
  g_armed[player] = Armed{true, base, overlay, level, 0u};
}

Weave* WeaveBySerial(uint32_t serial) {
  if (serial == 0) {
    return nullptr;
  }
  for (Weave& w : g_weaves) {
    if (w.serial == serial) {
      return &w;
    }
  }
  return nullptr;
}

}  // namespace spell_weaving

namespace {

/// The armed pair becomes a weave: on the launch's spell object here, or already on its first
/// hit when that landed inside the factory (OnHitLanded), in which case this completes the
/// record. Every cast comes here, the mod's own partner casts included (g_applying), so only an
/// armed player's base of the armed type binds.
void OnSpellCreated(refii::mods::SpellCreatedEvent& e) {
  const int player = Index(e.caster_player);
  if (player < 0 || g_applying || !g_armed[player].set || e.type != g_armed[player].base) {
    return;
  }
  Armed armed = g_armed[player];
  g_armed[player] = Armed{};
  Weave* bound = WeaveBySerial(e.serial);
  Weave& w = bound != nullptr ? *bound : *FreeWeave();
  if (bound == nullptr) {
    w = Weave{};
    w.serial = e.serial;
    w.player = player;
    w.base = armed.base;
    w.overlay = armed.overlay;
  }
  w.spell = e.spell;
  w.level = e.level >= 0 && e.level < kLevels ? e.level : armed.level;
  // The vortex component keeps the cast's serial at +0x18 (sub_827587F8 0x827588f4, from
  // *(spell+0x44) at 0x828b23d0): the one stated inside this factory call is this cast's.
  w.vortex = armed.vortex != 0 && refii::mods::LoadU32(e.base, armed.vortex + 0x18u) == e.serial ? armed.vortex : 0u;
  refii_mod_entity_position(e.ctx, e.base, e.caster, w.origin);
  if (w.base == kVortex && w.vortex != 0) {
    const uint32_t entity = refii::mods::LoadU32(e.base, w.vortex + 4u);
    float probe[3];
    w.anchor = Plausible(entity) && refii_mod_entity_position(e.ctx, e.base, entity, probe) != 0 ? entity : 0u;
  } else if (w.base == kFireball) {
    const uint32_t box = refii::mods::LoadU32(e.base, e.spell + kSpellShotBox);
    const uint32_t shot = Plausible(box) ? refii::mods::LoadU32(e.base, box) : 0u;
    float probe[3];
    w.anchor = Plausible(shot) && refii_mod_entity_position(e.ctx, e.base, shot, probe) != 0 ? shot : 0u;
  } else if (w.base == kSwords) {
    // The swords are there: the factory ran the class's Start (see the header comment).
    const uint32_t at = e.targeted ? kSpellBladesControllerTargeted : kSpellBladesControllerArea;
    ReadSwords(e.base, w, refii::mods::LoadU32(e.base, e.spell + at));
    TintSwords(e.ctx, e.base, w);
  } else if ((w.base == kLightning || w.base == kForcePush || w.base == kChaos) && e.targeted && e.target != 0 &&
             refii_mod_world_here(e.base) == 0) {
    // Where hits are not decided (an online joiner) the victims arrive as the host's relayed hits,
    // a network moment later: until then the look rides on the target the cast states.
    NoteVictim(w, e.target);
  }
  REXLOG_INFO("Spell Weaving: weave serial {} = {} carrying {} at power {} for player {} (anchor {:#x}, vortex {:#x})",
              w.serial, w.base, w.overlay, w.level + 1, player + 1, w.anchor, w.vortex);
}
REFII_MOD_ON(SpellCreated, OnSpellCreated);

void OnTick(refii::mods::TickEvent& e) {
  g_clock += e.seconds;

  // The partner casts whose delay has run out.
  for (Pending& p : g_pending) {
    if (p.armed && g_clock >= p.at) {
      p.armed = false;
      g_applying = true;
      CastPartner(*e.ctx, e.base, p, p.origin);
      g_applying = false;
    }
  }

  // The weaves age; a finished base's record holds for its fade, then frees.
  for (Weave& w : g_weaves) {
    if (w.serial == 0) {
      continue;
    }
    w.age += e.seconds;
    const bool live = BaseLive(e.base, w);
    if (!live && w.done_at < 0.0f) {
      w.done_at = g_clock;
    }
    if ((w.done_at >= 0.0f && g_clock - w.done_at > HoldSeconds(w.base) + 1.0f) || w.age > 60.0f ||
        refii_mod_player_hero(e.base, w.player + 1) == 0) {
      REXLOG_INFO("Spell Weaving: weave serial {} over", w.serial);
      StopLooks(w);
      w = Weave{};
    }
  }

  // An arm is consumed inside the launch that set it (the factory runs within LaunchSlot); one
  // that reaches a tick belonged to a launch that made no spell (no will, no target) and must
  // not bind the next plain cast.
  g_armed[0] = Armed{};
  g_armed[1] = Armed{};

  for (Weave& w : g_weaves) {
    if (w.serial != 0) {
      TintSwords(e.ctx, e.base, w);
    }
  }
  UpdateLooks(e.base);
}
REFII_MOD_ON(Tick, OnTick);

/// A weave-side spell's hit lands at the weave's share: Fable 3's GeneralDamageMultiplier. A hit
/// landing inside the partner's own factory call (a Lightning bolt) carries a serial nobody has
/// seen yet: it is the cast in progress when its type and caster match, and its serial is
/// recorded here.
void OnHit(refii::mods::HitEvent& e) {
  if (Knob("spell_weaving.woven", 1.0f) == 0.0f) {
    return;
  }
  if (g_applying) {
    if (!g_casting.on || e.spell_type != g_casting.type || e.attacker != g_casting.hero || e.spell_serial == 0) {
      return;
    }
    if (!IsWeaveSerial(e.spell_serial)) {
      g_weave_serials[g_weave_next] = e.spell_serial;
      g_weave_next = (g_weave_next + 1) % 32;
    }
  } else if (!IsWeaveSerial(e.spell_serial)) {
    return;
  }
  e.damage *= Knob("spell_weaving.weave_damage", 0.7f);
}
REFII_MOD_ON(Hit, OnHit);

/// The weave a hit relayed from the host belongs to: by serial when this machine's copy of the cast
/// carries the host's number, else the newest weave of the attacker's player whose base is the
/// hit's spell. Null when none is out.
Weave* WeaveForReplica(const refii::mods::HitLandedEvent& e) {
  if (Weave* w = WeaveBySerial(e.spell_serial)) {
    return w;
  }
  const int player = Index(e.attacker_player);
  Weave* newest = nullptr;
  for (Weave& w : g_weaves) {
    if (w.serial != 0 && w.player == player && w.base == e.spell_type && (newest == nullptr || w.age < newest->age)) {
      newest = &w;
    }
  }
  return newest;
}

/// The hit landed first, as the game delivers it; the partner's cast follows it — the partner
/// of the WEAVE whose serial the hit carries.
void OnHitLanded(refii::mods::HitLandedEvent& e) {
  if (g_applying || Knob("spell_weaving.woven", 1.0f) == 0.0f || e.spell_type < 0 || IsWeaveSerial(e.spell_serial)) {
    return;  // a weave-side spell never casts a partner of its own
  }
  if (refii_mod_event_replica() != 0) {
    // The host's hit, relayed (an online joiner): the look follows its victim; the host casts.
    if (Weave* w = WeaveForReplica(e); w != nullptr && e.spell_type == w->base) {
      NoteVictim(*w, e.victim);
    }
    return;
  }
  Weave* w = WeaveBySerial(e.spell_serial);
  if (w == nullptr) {
    // A hit inside the factory, before SpellCreated: the armed base of this caster binds here
    // and SpellCreated completes the record (the spell object, its anchor) by serial.
    const int player = Index(e.attacker_player);
    if (player < 0 || !g_armed[player].set || e.spell_type != g_armed[player].base || e.spell_serial == 0) {
      return;
    }
    w = FreeWeave();
    *w = Weave{};
    w->serial = e.spell_serial;
    w->player = player;
    w->base = g_armed[player].base;
    w->overlay = g_armed[player].overlay;
    w->level = e.spell_level >= 0 && e.spell_level < kLevels ? e.spell_level : g_armed[player].level;
    refii_mod_entity_position(e.ctx, e.base, e.attacker, w->origin);
    REXLOG_INFO("Spell Weaving: weave serial {} = {} carrying {} bound on its first hit for player {}", w->serial,
                w->base, w->overlay, player + 1);
  }
  if (e.spell_type != w->base || w->overlay < 0) {
    return;
  }
  NoteVictim(*w, e.victim);
  if (!refii_mod_entity_alive(e.base, e.victim) || e.victim == e.attacker || !Fresh(e.victim, e.spell_serial, w->overlay)) {
    return;
  }
  const int level = e.spell_level >= 0 && e.spell_level < kLevels ? e.spell_level : w->level;
  // Fable III: WeaveSpellShotDelayTime, 0.25 s in the vortex records and 1 s by default, ±5 %.
  const float delay = (w->base == kVortex ? Knob("spell_weaving.shot_delay_vortex", 0.25f)
                                           : Knob("spell_weaving.shot_delay", 1.0f)) * Jitter();
  float origin[3] = {w->origin[0], w->origin[1], w->origin[2]};
  if (w->base == kVortex && w->vortex != 0) {
    // The twister's point: the vortex component's +0x140 — the vector its update hands every
    // ground effect each tick (sub_827590E0: `li r28,0x140` 0x82759210, `lvx128 v1,r31,r28`
    // 0x82759238 before sub_825F86F0) and then its entity (sub_82759928 0x82759bdc). The push is
    // away from the twister.
    for (int i = 0; i < 3; ++i) {
      origin[i] = refii::mods::LoadF32(e.base, w->vortex + 0x140u + 4u * static_cast<uint32_t>(i));
    }
  } else if (w->anchor != 0 && refii_mod_entity_alive(e.base, w->anchor)) {
    refii_mod_entity_position(e.ctx, e.base, w->anchor, origin);
  } else if (w->base == kSwords || w->base == kLightning) {
    // A stab or a bolt: the base stands where the hit landed; push away from the hero's side.
    refii_mod_entity_position(e.ctx, e.base, e.attacker, origin);
  }
  QueuePartner(w->player, w->overlay, level, e.victim, delay < 0.0f ? 0.0f : delay, origin);
}
REFII_MOD_ON(HitLanded, OnHitLanded);

}  // namespace

// Inside the factory, before SpellCreated: the armed vortex cast of this caster takes it.
REX_EXTERN(rex_VortexComponent_Init) {
  REX_FUNC_PROLOGUE();
  const uint32_t component = ctx.r3.u32;
  const uint32_t caster = ctx.r4.u32;
  __imp__rex_VortexComponent_Init(ctx, base);
  const int player = PlayerOf(base, caster);
  if (component != 0 && player >= 0 && g_armed[player].set && g_armed[player].base == kVortex) {
    g_armed[player].vortex = component;
  }
}

// The look holds for its fade where the twister ended, not where the cast began.
REX_EXTERN(rex_VortexComponent_Shutdown) {
  REX_FUNC_PROLOGUE();
  if (Weave* w = WeaveByVortex(ctx.r3.u32)) {
    for (int i = 0; i < 3; ++i) {
      w->origin[i] = refii::mods::LoadF32(base, w->vortex + 0x140u + 4u * static_cast<uint32_t>(i));
    }
    w->vortex = 0;
    w->anchor = 0;
  }
  __imp__rex_VortexComponent_Shutdown(ctx, base);
}

REX_EXTERN(rex_TargetedFireball_Impact) {
  REX_FUNC_PROLOGUE();
  const uint32_t spell = ctx.r3.u32;
  const bool exploded = spell != 0 && refii::mods::LoadU8(base, spell + kSpellExploded) != 0;
  float where[3];
  float facing[3];
  refii::mods::GetVector(ctx, 1, where);
  refii::mods::GetVector(ctx, 2, facing);
  const uint32_t queued = g_queued;
  Weave* w = spell != 0 && !exploded ? WeaveBySpell(base, spell) : nullptr;
  if (w != nullptr) {
    // The blast's hits land INSIDE this call (sub_827478D8 0x82744608 per victim -> the
    // receiver), each queuing the partner from the weave's origin: the impact point is the
    // origin from here on, and the shot entity — which keeps falling after — is no anchor.
    for (int i = 0; i < 3; ++i) {
      w->origin[i] = where[i];
    }
    w->anchor = 0;
  }
  __imp__rex_TargetedFireball_Impact(ctx, base);

  // The detonation, once: the exploded byte went up inside the call. Its blast delivered its
  // hits before returning, and each victim's hit queued the partner on that victim above; a
  // detonation that queued nothing struck nothing alive, and the partner goes to the point — on
  // the machine that runs the world only: an online joiner's blast delivers no hits, and the
  // host's copy queues for it.
  if (w == nullptr || refii::mods::LoadU8(base, spell + kSpellExploded) == 0) {
    return;
  }
  // The look holds on the blast for its fade; the projectile is gone.
  w->done_at = g_clock;
  if (g_queued != queued || g_applying || Knob("spell_weaving.woven", 1.0f) == 0.0f || w->overlay < 0 ||
      refii_mod_world_here(base) == 0) {
    return;
  }
  int level = static_cast<int32_t>(refii::mods::LoadU32(base, spell + kSpellLevel));
  if (level < 0 || level >= kLevels) {
    level = w->level;
  }
  const float delay = Knob("spell_weaving.shot_delay", 1.0f) * Jitter();
  QueuePartnerAt(w->player, w->overlay, level, where, facing, delay < 0.0f ? 0.0f : delay);
  REXLOG_INFO("Spell Weaving: fireball serial {} struck nothing alive at ({:.1f}, {:.1f}, {:.1f}); partner {} queued there",
              w->serial, where[0], where[1], where[2], w->overlay);
}
