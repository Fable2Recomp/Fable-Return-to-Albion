// refii — the host's helper calls and queries, run in the guest tick (core/payload_bridge.h:
// refii_bridge_mod_command_poll). A Godot script (RefiiHost) cannot call refii_mod_* itself:
// those need the guest tick thread and its context. So it posts a LINE and the tick runs it
// here with the real context; a query answers through the event queue under
// REFII_MOD_HOST_REPLY_KIND, carrying the id the script gave it.
//
// GRAMMAR (space-separated; numbers decimal; `run_lua` takes the rest of the line):
// Entities and objects are HANDLES (refii_mod_handle: what every event gave the script), never
// addresses; definitions and text hashes are "0x…" strings or decimal.
//   give_gold E D · modify_health E D ATTACKER · set_health E V · add_item E DEFINITION
//   give_experience E POOL AMOUNT · spend_experience E POOL AMOUNT
//   cast_spell CASTER TYPE TARGETED TARGET LEVEL · set_position E X Y Z
//   save_set KEY INT · run_lua CHUNK...
//   player_set P KEY V · player_add P KEY D [SAVED] · player_save P KEY V   (the player store
//     Refii.player_set / _add / _save write: refii_mod_player_*)
//   publish KEY VALUE            -> refii::mods::Publish: the value every reader sees ("mod:<key>")
//   menu_row PLAYER MENU ROW MOD -> raises ModMenuRow for the native and Lua handlers (a row chosen
//                                   in Godot; a space in a name arrives as '_', an empty one as '-')
//   box_answer ID BUTTON PLAYER  -> raises ModMessageBox for the native and Lua handlers (a mod's
//                                   message box answered in Godot: 0 A, 1 B, -1 replaced)
//   query ID health E            -> id,ok,current,max
//   query ID position E          -> id,ok,x,y,z
//   query ID gold E              -> id,ok,gold
//   query ID item_count E DEF    -> id,ok,count
//   query ID experience E POOL   -> id,ok,amount
//   query ID ability_level E T   -> id,ok,level
//   query ID relation A B        -> id,ok,relation
//   query ID alive E             -> id,ok,alive
//   query ID definition E        -> id,ok,definition
//   query ID name E              -> id,ok,name='..'
//   query ID level_name          -> id,ok,level='..'
//   query ID hero P              -> id,ok,hero
//   query ID player_of E         -> id,ok,player
//   query ID player_for E        -> id,ok,player  (refii_mod_player_for: who "the hero" is for E)
//   query ID world_here          -> id,ok,here    (refii_mod_world_here)
//   query ID combat_state P      -> id,ok,midst,close_to_combat,in_any_combat (Refii.combat_state)
//   query ID lua_int EXPR... (last two tokens LO HI) -> id,ok,value
//   query ID renown E -> renown · morality E / purity E -> value · physique E -> fat,height
//   query ID age E -> age · equipped E -> melee,ranged · inventory_stack E I -> definition,count
//   query ID near X Y Z R [CREATURES [OFFSET]] -> total,offset,count,e1..ecount (100 a page)
//   query ID line_of_sight X1 Y1 Z1 X2 Y2 Z2 -> clear
//   query ID spawn DEF X Y Z -> entity
//   query ID local_hero -> hero · coop_mode -> mode · menu_player -> player
//   query ID coop_state -> local_hero,coop_mode,menu_player (the three at once)
//   query ID player_value P KEY [FALLBACK] -> value,has · player_here P -> here
//   query ID player_dog P -> dog · focus P -> entity · opinion E P -> love,fear,attractiveness,
//     morality,respect · uid E -> uid='0x…'
//   query ID discovered_crime E P -> discovered · perceived_renown E P -> renown
//   forgive_crimes E P   (refii_mod_forgive_crimes: player P's crimes the villager E knows of)
//   fx_play ID HASH X Y Z ENTITY OX OY OZ SCALE INTENSITY LOOP
//                        play_fx under the classic look (Engine_FxPlayer): the guest's own particle
//                        instance of effect HASH (its bank key), at X Y Z or riding handle ENTITY
//                        (0 for none) offset by OX OY OZ; SCALE and INTENSITY the instance's own
//                        setters; LOOP 1 spawns it again each time it retires
//   fx_stop ID           ends it: the guest's kill (a zero-frame fade) and our reference released
// A command that does nothing (no such entity, no component) is silent; a query always replies,
// with ok=false when it could not be answered.

#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <atomic>
#include <string>
#include <unordered_map>
#include <vector>

#include "core/guest_build.h"
#include "core/mod_values.h"
#include "core/refii_mod.h"

// guest_tick_pump.cpp: a player's dog (Refii.player_dog's answer).
extern "C" uint32_t RefiiCoopPlayerDog(uint8_t* base, int player);

namespace {

std::vector<std::string> Split(const char* line, size_t keep_rest_from) {
  std::vector<std::string> words;
  const char* c = line;
  while (*c != 0) {
    while (*c == ' ') {
      ++c;
    }
    if (*c == 0) {
      break;
    }
    if (words.size() == keep_rest_from) {
      words.emplace_back(c);
      break;
    }
    const char* start = c;
    while (*c != 0 && *c != ' ') {
      ++c;
    }
    words.emplace_back(start, static_cast<size_t>(c - start));
  }
  return words;
}

/// A raw word: a hash as "0x…" or decimal.
uint32_t U(const std::vector<std::string>& w, size_t i) {
  return i < w.size() ? static_cast<uint32_t>(std::strtoul(w[i].c_str(), nullptr, 0)) : 0u;
}
/// An entity or object: the script holds a HANDLE (refii_mod_handle), never an address.
uint32_t E(const std::vector<std::string>& w, size_t i) { return refii_mod_handle_address(U(w, i)); }
int32_t I(const std::vector<std::string>& w, size_t i) {
  return i < w.size() ? static_cast<int32_t>(std::strtol(w[i].c_str(), nullptr, 10)) : 0;
}
float F(const std::vector<std::string>& w, size_t i) {
  return i < w.size() ? std::strtof(w[i].c_str(), nullptr) : 0.0f;
}

void Reply(const std::string& id, bool ok, const char* fields) {
  char text[2048];
  std::snprintf(text, sizeof(text), "id=%s,ok=%s%s%s", id.c_str(), ok ? "true" : "false", fields[0] != 0 ? "," : "",
                fields);
  refii::mods::PostEvent(REFII_MOD_HOST_REPLY_KIND, text);
}

/// A text as a single-quoted Lua literal's body (quotes and newlines become underscores).
void Quote(const char* text, char* out, size_t size) {
  size_t n = 0;
  for (const char* c = text; c != nullptr && *c != 0 && n + 1 < size; ++c) {
    out[n++] = (*c == '\'' || *c == '\\' || *c == '\n' || *c == '\r' || *c == ',') ? '_' : *c;
  }
  out[n] = 0;
}

void RunQuery(PPCContext& ctx, uint8_t* base, const std::vector<std::string>& w) {
  const std::string id = w.size() > 1 ? w[1] : "0";
  const std::string what = w.size() > 2 ? w[2] : "";
  char fields[1900] = {0};
  bool ok = false;
  if (what == "health") {
    float current = 0.0f, max = 0.0f;
    ok = refii_mod_health(base, E(w, 3), &current, &max) != 0;
    std::snprintf(fields, sizeof(fields), "current=%g,max=%g", static_cast<double>(current), static_cast<double>(max));
  } else if (what == "position") {
    float xyz[3] = {0, 0, 0};
    ok = refii_mod_entity_position(&ctx, base, E(w, 3), xyz) != 0;
    std::snprintf(fields, sizeof(fields), "x=%g,y=%g,z=%g", static_cast<double>(xyz[0]), static_cast<double>(xyz[1]),
                  static_cast<double>(xyz[2]));
  } else if (what == "gold") {
    int32_t gold = 0;
    ok = refii_mod_gold(base, E(w, 3), &gold) != 0;
    std::snprintf(fields, sizeof(fields), "gold=%d", gold);
  } else if (what == "item_count") {
    ok = true;
    std::snprintf(fields, sizeof(fields), "count=%d", refii_mod_item_count(base, E(w, 3), U(w, 4)));
  } else if (what == "experience") {
    float amount = 0.0f;
    ok = refii_mod_experience(base, E(w, 3), I(w, 4), &amount) != 0;
    std::snprintf(fields, sizeof(fields), "amount=%g", static_cast<double>(amount));
  } else if (what == "ability_level") {
    const int32_t level = refii_mod_ability_level(base, E(w, 3), I(w, 4));
    ok = level >= 0;
    std::snprintf(fields, sizeof(fields), "level=%d", level);
  } else if (what == "relation") {
    const int32_t relation = refii_mod_relation(&ctx, base, E(w, 3), E(w, 4));
    ok = relation != 0;
    std::snprintf(fields, sizeof(fields), "relation=%d", relation);
  } else if (what == "alive") {
    ok = true;
    std::snprintf(fields, sizeof(fields), "alive=%s", refii_mod_entity_alive(base, E(w, 3)) != 0 ? "true" : "false");
  } else if (what == "definition") {
    const uint32_t definition = refii_mod_entity_definition(base, E(w, 3));
    ok = definition != 0;
    std::snprintf(fields, sizeof(fields), "definition='0x%08x'", definition);
  } else if (what == "name") {
    char name[128] = {0};
    ok = refii_mod_entity_name(base, E(w, 3), name, sizeof(name)) != 0;
    char quoted[128];
    Quote(name, quoted, sizeof(quoted));
    std::snprintf(fields, sizeof(fields), "name='%s'", quoted);
  } else if (what == "level_name") {
    char level[128] = {0};
    ok = refii_mod_level_name(base, level, sizeof(level)) != 0;
    char quoted[128];
    Quote(level, quoted, sizeof(quoted));
    std::snprintf(fields, sizeof(fields), "level='%s'", quoted);
  } else if (what == "hero") {
    const uint32_t hero = refii_mod_player_hero(base, I(w, 3));
    ok = hero != 0;
    std::snprintf(fields, sizeof(fields), "hero=%u", refii_mod_handle(hero));
  } else if (what == "player_of") {
    ok = true;
    std::snprintf(fields, sizeof(fields), "player=%d", refii_mod_player_of(base, E(w, 3)));
  } else if (what == "player_for") {
    // refii_mod_player_for: the player the entity deals with (a dog its master's, else the nearer hero).
    const int32_t player = refii_mod_player_for(&ctx, base, E(w, 3));
    ok = player != 0;
    std::snprintf(fields, sizeof(fields), "player=%d", player);
  } else if (what == "world_here") {
    ok = true;
    std::snprintf(fields, sizeof(fields), "here=%s", refii_mod_world_here(base) != 0 ? "true" : "false");
  } else if (what == "combat_state") {
    // The core co-op Lua's Refii.combat_state(p) -> midst, close_to_combat, in_any_combat: asked of
    // the game VM, the one place the rule lives.
    const int32_t player = I(w, 3);
    int32_t state[3] = {0, 0, 0};
    ok = player == 1 || player == 2;
    for (int i = 0; ok && i < 3; ++i) {
      char expr[160];
      std::snprintf(expr, sizeof(expr),
                    "Refii and Refii.combat_state and ((select(%d, Refii.combat_state(%d))) and 1 or 0) or 0", i + 1,
                    player);
      ok = refii_mod_game_lua_int(&ctx, base, expr, 0, 1, &state[i]) != 0;
    }
    std::snprintf(fields, sizeof(fields), "midst=%s,close_to_combat=%s,in_any_combat=%s", state[0] != 0 ? "true" : "false",
                  state[1] != 0 ? "true" : "false", state[2] != 0 ? "true" : "false");
  } else if (what == "renown") {
    int32_t renown = 0;
    ok = refii_mod_renown(&ctx, base, E(w, 3), &renown) != 0;
    std::snprintf(fields, sizeof(fields), "renown=%d", renown);
  } else if (what == "morality" || what == "purity") {
    float value = 0.0f;
    ok = (what == "morality" ? refii_mod_morality(&ctx, base, E(w, 3), &value)
                             : refii_mod_purity(&ctx, base, E(w, 3), &value)) != 0;
    std::snprintf(fields, sizeof(fields), "value=%g", static_cast<double>(value));
  } else if (what == "physique") {
    float fat = 0.0f, height = 0.0f;
    ok = refii_mod_physique(base, E(w, 3), &fat, &height) != 0;
    std::snprintf(fields, sizeof(fields), "fat=%g,height=%g", static_cast<double>(fat), static_cast<double>(height));
  } else if (what == "age") {
    float age = 0.0f;
    ok = refii_mod_age(base, E(w, 3), &age) != 0;
    std::snprintf(fields, sizeof(fields), "age=%g", static_cast<double>(age));
  } else if (what == "equipped") {
    uint32_t melee = 0, ranged = 0;
    ok = refii_mod_equipped(base, E(w, 3), &melee, &ranged) != 0;
    std::snprintf(fields, sizeof(fields), "melee='0x%08x',ranged='0x%08x'", melee, ranged);
  } else if (what == "inventory_stack") {
    uint32_t definition = 0;
    int32_t count = 0;
    ok = refii_mod_inventory_stack(base, E(w, 3), I(w, 4), &definition, &count) != 0;
    std::snprintf(fields, sizeof(fields), "definition='0x%08x',count=%d", definition, count);
  } else if (what == "near") {
    // query ID near X Y Z R [CREATURES [OFFSET]] -> total=n,offset=o,count=k,e1=h..ek=h: up to
    // 100 handles a reply, nearest first, starting at OFFSET; ask again from offset+count for more.
    const float xyz[3] = {F(w, 3), F(w, 4), F(w, 5)};
    constexpr int32_t kNearMax = 2048;
    constexpr int32_t kNearPage = 100;
    static uint32_t found[kNearMax];
    const int32_t total = refii_mod_entities_near(&ctx, base, xyz, F(w, 6), I(w, 7), found, kNearMax);
    int32_t offset = I(w, 8);
    offset = offset < 0 ? 0 : (offset > total ? total : offset);
    const int32_t count = total - offset < kNearPage ? total - offset : kNearPage;
    ok = true;
    int at = std::snprintf(fields, sizeof(fields), "total=%d,offset=%d,count=%d", total, offset, count);
    for (int32_t i = 0; i < count && at > 0 && static_cast<size_t>(at) < sizeof(fields) - 24; ++i) {
      at += std::snprintf(fields + at, sizeof(fields) - static_cast<size_t>(at), ",e%d=%u", i + 1,
                          refii_mod_handle(found[offset + i]));
    }
  } else if (what == "line_of_sight") {
    const float from[3] = {F(w, 3), F(w, 4), F(w, 5)};
    const float to[3] = {F(w, 6), F(w, 7), F(w, 8)};
    ok = true;
    std::snprintf(fields, sizeof(fields), "clear=%s", refii_mod_line_of_sight(&ctx, base, from, to) != 0 ? "true" : "false");
  } else if (what == "spawn") {
    // query ID spawn DEF X Y Z -> entity=h (a query, so the script learns the new entity's handle)
    const float xyz[3] = {F(w, 4), F(w, 5), F(w, 6)};
    const uint32_t entity = refii_mod_spawn(&ctx, base, U(w, 3), xyz);
    ok = entity != 0;
    std::snprintf(fields, sizeof(fields), "entity=%u", refii_mod_handle(entity));
  } else if (what == "local_hero") {
    const uint32_t hero = refii_mod_local_hero(base);
    ok = hero != 0;
    std::snprintf(fields, sizeof(fields), "hero=%u", refii_mod_handle(hero));
  } else if (what == "coop_mode") {
    ok = true;
    std::snprintf(fields, sizeof(fields), "mode=%d", refii_mod_coop_mode(base));
  } else if (what == "menu_player") {
    ok = true;
    std::snprintf(fields, sizeof(fields), "player=%d", refii_mod_menu_player(base));
  } else if (what == "coop_state") {
    ok = true;
    std::snprintf(fields, sizeof(fields), "local_hero=%u,coop_mode=%d,menu_player=%d",
                  refii_mod_handle(refii_mod_local_hero(base)), refii_mod_coop_mode(base), refii_mod_menu_player(base));
  } else if (what == "player_value") {
    // Refii.player_value(p, key [, fallback]): the value, else the fallback, else nothing.
    float value = 0.0f;
    const bool has = w.size() > 4 && refii_mod_player_get(I(w, 3), w[4].c_str(), &value) != 0;
    const bool fallback = w.size() > 5;
    if (!has && fallback) {
      value = F(w, 5);
    }
    ok = has || fallback;
    std::snprintf(fields, sizeof(fields), "value=%.9g,has=%s", static_cast<double>(value), has ? "true" : "false");
  } else if (what == "player_here") {
    ok = true;
    std::snprintf(fields, sizeof(fields), "here=%s", refii_mod_player_here(I(w, 3)) != 0 ? "true" : "false");
  } else if (what == "player_dog") {
    const uint32_t dog = RefiiCoopPlayerDog(base, I(w, 3));
    ok = dog != 0;
    std::snprintf(fields, sizeof(fields), "dog=%u", refii_mod_handle(dog));
  } else if (what == "focus") {
    const uint32_t target = refii_mod_focus(base, I(w, 3));
    ok = target != 0;
    std::snprintf(fields, sizeof(fields), "entity=%u", refii_mod_handle(target));
  } else if (what == "opinion") {
    float axes[5] = {0, 0, 0, 0, 0};
    ok = refii_mod_opinion(base, E(w, 3), I(w, 4), axes) != 0;
    std::snprintf(fields, sizeof(fields), "love=%.9g,fear=%.9g,attractiveness=%.9g,morality=%.9g,respect=%.9g",
                  static_cast<double>(axes[0]), static_cast<double>(axes[1]), static_cast<double>(axes[2]),
                  static_cast<double>(axes[3]), static_cast<double>(axes[4]));
  } else if (what == "discovered_crime") {
    // Refii.discovered_crime(villager, p): the villager has discovered a crime of player p's hero.
    const uint32_t hero = refii_mod_player_hero(base, I(w, 4));
    ok = hero != 0;
    std::snprintf(fields, sizeof(fields), "discovered=%s",
                  ok && refii_mod_discovered_crime(&ctx, base, E(w, 3), hero) != 0 ? "true" : "false");
  } else if (what == "perceived_renown") {
    // Refii.perceived_renown(villager, p): the renown the villager credits player p's hero with.
    int32_t renown = 0;
    ok = refii_mod_perceived_renown(&ctx, base, E(w, 3), refii_mod_player_hero(base, I(w, 4)), &renown) != 0;
    std::snprintf(fields, sizeof(fields), "renown=%d", renown);
  } else if (what == "uid") {
    // Refii.uid(e): the entity's own 64-bit UniqueID (+0x14 high, +0x18 low).
    const uint32_t entity = E(w, 3);
    ok = entity != 0;
    std::snprintf(fields, sizeof(fields), "uid='0x%08x%08x'", ok ? refii::mods::LoadU32(base, entity + 0x14u) : 0u,
                  ok ? refii::mods::LoadU32(base, entity + 0x18u) : 0u);
  } else if (what == "lua_int" && w.size() >= 6) {
    // query ID lua_int <expr words...> LO HI
    std::string expr;
    for (size_t i = 3; i + 2 < w.size(); ++i) {
      expr += (i > 3 ? " " : "") + w[i];
    }
    int32_t value = 0;
    ok = refii_mod_game_lua_int(&ctx, base, expr.c_str(), I(w, w.size() - 2), I(w, w.size() - 1), &value) != 0;
    std::snprintf(fields, sizeof(fields), "value=%d", value);
  }
  Reply(id, ok, fields);
}

/// A menu_row word back to the name Godot sent: '-' stands for an empty one.
const char* Name(const std::vector<std::string>& w, size_t i) {
  return i < w.size() && w[i] != "-" ? w[i].c_str() : "";
}

void RaiseMenuRow(PPCContext& ctx, uint8_t* base, const std::vector<std::string>& w) {
  refii::mods::ModMenuRowEvent e{};
  e.ctx = &ctx;
  e.base = base;
  e.player = I(w, 1);
  e.menu = Name(w, 2);
  e.row = Name(w, 3);
  e.mod = Name(w, 4);
  refii::mods::Dispatch(e);
}

// ---------------------------------------------------------------------------------------------
// play_fx UNDER THE CLASSIC LOOK: the effect is spawned as the guest's own particle instance, so
// the guest simulates it and the classic renderer draws its groups like any other the game plays.
//
// THE SPAWN IS THE GAME'S OWN SEQUENCE, the one the particle component's start `sub_82569C28`
// runs (and fourteen other spawn sites with it), call for call:
//
//   82569c78  bl sub_8322FDC8   the effect's type collection from the registry (dword_8349E568),
//                               by the bank key: lhResourceName_ComputeHash of the lowered name
//   82569c80  lwz r11,var_30 / beq   an unknown key spawns nothing
//   82569c8c  li r3,0x48 / bl Render_AllocD3DObject   (operator new 0x8221F258)
//   82569c9c  bl sub_83233300   CParticleSystemEntityInstanceCollection(type): its entities, +44 = -1
//   82569cb0  bl sub_82273058   the owner's smart pointer takes it (a fresh count block)
//   82569ce8  lwarx/addi 1/stwcx.  a counted copy of that pointer ...
//   82569d00  bl sub_83234D78   ... handed to the manager, which lists it (dword_8349E480, 28-byte
//                               records), writes its slot at instance+44 and releases the copy
//   82569d18  bl sub_822B86C0   the world's own list takes a reference (*(*(dword_83496AB8+12)+88))
//   82569d28  bl sub_821B7B18   the type reference released
//
// The component also writes +40 (its interface) and +4 (the hash); +40 stays 0 here, the state the
// host bridge already reads as "spell-owned" (no emitter component).
//
// WHAT THE OPTIONS ARE ON THE INSTANCE: position `sub_832325A8(inst, v1, v2)` (v1's w is replaced by
// 1.0 at 0x83232624), scale `sub_83232290(inst, f1)` — every element's vtable slot 11, the setter
// behind ParticleAttacher.SetParticleScale (0x8276724c) — and intensity `sub_832323E0(inst, f1)`,
// instance+52 and every emitter's +408, behind SetParticleAlpha (0x8276701c). There is no colour
// on an instance (the constructor initialises +48, +52 and +68 as single floats; a particle's
// colour is its material's keyframe table), so a TINT cannot be honoured on this path.
//
// END OF LIFE: `sub_83232048(inst, frames)` sets every element's +0xC0 = +0xC4 = frames; zero fails
// the liveness test at once and the retire pass `sub_83234338` drops the instance, writing -1 back
// to its +44 (0x832347c8). That word is how a one-shot is known to have run out.
namespace guest_fx {

constexpr uint32_t kOperatorNew = 0x8221F258u;
constexpr uint32_t kOperatorDelete = 0x8221BD38u;
constexpr uint32_t kRefRelease = 0x821B7B18u;
constexpr uint32_t kRefReset = 0x82273058u;
constexpr uint32_t kEffectType = 0x8322FDC8u;
constexpr uint32_t kInstanceCtor = 0x83233300u;
constexpr uint32_t kManagerAdd = 0x83234D78u;
constexpr uint32_t kWorldHold = 0x822B86C0u;
constexpr uint32_t kSetPosition = 0x832325A8u;
constexpr uint32_t kSetScale = 0x83232290u;
constexpr uint32_t kSetIntensity = 0x832323E0u;
constexpr uint32_t kFade = 0x83232048u;
constexpr uint32_t kGameRoot = 0x83496AB8u;
constexpr uint32_t kInstanceBytes = 0x48u;
constexpr uint32_t kManagerSlot = 44u;

struct Played {
  uint32_t holder = 0;  // our smart pointer {instance, count block}, 8 bytes of guest heap
  uint32_t hash = 0;
  uint32_t entity = 0;  // a handle, 0 for a world position
  float at[3] = {0.0f, 0.0f, 0.0f};
  float offset[3] = {0.0f, 0.0f, 0.0f};
  float scale = 1.0f;
  float intensity = 1.0f;
  bool loop = true;
};

// The guest tick thread only.
std::unordered_map<uint32_t, Played> g_played;

bool Plausible(uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; }

bool Call(PPCContext& work, uint8_t* base, uint32_t tu1) {
  return refii::mods::CallGuest(work, base, refii::guest::Address(tu1));
}

uint32_t Instance(uint8_t* base, uint32_t holder) {
  return holder != 0 ? refii::mods::LoadU32(base, holder) : 0u;
}

/// The count word of a guest smart pointer's block, raised the way the game raises it (lwarx/stwcx.).
void Retain(uint8_t* base, uint32_t block) {
  auto* word = reinterpret_cast<std::atomic<uint32_t>*>(base + block);
  uint32_t seen = word->load(std::memory_order_relaxed);
  while (!word->compare_exchange_weak(seen, __builtin_bswap32(__builtin_bswap32(seen) + 1u),
                                      std::memory_order_acq_rel)) {
  }
}

void Release(const PPCContext& frame, uint8_t* base, uint32_t ref) {
  PPCContext work = frame;
  work.r3.u32 = ref;
  Call(work, base, kRefRelease);
}

/// Our holder: released, then its 8 bytes returned to the guest heap.
void Drop(const PPCContext& ctx, uint8_t* base, uint32_t holder) {
  if (holder == 0) {
    return;
  }
  const PPCContext frame = refii::mods::Frame(ctx, 0x300u);
  Release(frame, base, holder);
  PPCContext work = frame;
  work.r3.u32 = holder;
  Call(work, base, kOperatorDelete);
}

/// The game's spawn sequence (above). Returns our holder, or 0 when the bank has no such effect.
uint32_t Spawn(const PPCContext& ctx, uint8_t* base, uint32_t hash) {
  using refii::mods::LoadU32;
  using refii::mods::StoreU32;
  const PPCContext frame = refii::mods::Frame(ctx, 0x300u);
  const uint32_t type_ref = (frame.r1.u32 + 0x100u) & ~0xFu;
  const uint32_t copy = type_ref + 16u;
  StoreU32(base, type_ref, 0u);
  StoreU32(base, type_ref + 4u, 0u);

  PPCContext work = frame;
  work.r3.u32 = type_ref;
  work.r4.u32 = hash;
  if (!Call(work, base, kEffectType) || LoadU32(base, type_ref) == 0u) {
    Release(frame, base, type_ref);
    return 0u;
  }

  work = frame;
  work.r3.u64 = 8u;
  const uint32_t holder = Call(work, base, kOperatorNew) ? work.r3.u32 : 0u;
  work = frame;
  work.r3.u64 = kInstanceBytes;
  const uint32_t memory = Plausible(holder) && Call(work, base, kOperatorNew) ? work.r3.u32 : 0u;
  if (!Plausible(holder) || !Plausible(memory)) {
    Release(frame, base, type_ref);
    if (Plausible(holder)) {
      work = frame;
      work.r3.u32 = holder;
      Call(work, base, kOperatorDelete);
    }
    return 0u;
  }
  StoreU32(base, holder, 0u);
  StoreU32(base, holder + 4u, 0u);

  work = frame;
  work.r3.u32 = memory;
  work.r4.u32 = type_ref;
  Call(work, base, kInstanceCtor);
  const uint32_t instance = work.r3.u32;

  work = frame;
  work.r3.u32 = holder;
  work.r4.u32 = instance;
  Call(work, base, kRefReset);

  const uint32_t block = LoadU32(base, holder + 4u);
  StoreU32(base, copy, LoadU32(base, holder));
  StoreU32(base, copy + 4u, block);
  if (block != 0u) {
    Retain(base, block);
  }
  work = frame;
  work.r3.u32 = copy;
  Call(work, base, kManagerAdd);

  const uint32_t root = LoadU32(base, refii::guest::Address(kGameRoot));
  const uint32_t game = Plausible(root) ? LoadU32(base, root + 12u) : 0u;
  const uint32_t world = Plausible(game) ? LoadU32(base, game + 88u) : 0u;
  if (Plausible(world)) {
    work = frame;
    work.r3.u32 = world;
    work.r4.u32 = holder;
    Call(work, base, kWorldHold);
  }

  Release(frame, base, type_ref);
  return holder;
}

void SetFloat(const PPCContext& ctx, uint8_t* base, uint32_t instance, uint32_t setter, float value) {
  PPCContext work = refii::mods::Frame(ctx, 0x300u);
  work.r3.u32 = instance;
  work.f1.f64 = value;
  Call(work, base, setter);
}

void Place(const PPCContext& ctx, uint8_t* base, uint32_t instance, const float* xyz) {
  PPCContext work = refii::mods::Frame(ctx, 0x300u);
  const float still[3] = {0.0f, 0.0f, 0.0f};
  work.r3.u32 = instance;
  refii::mods::SetVector(work, 1, xyz);
  refii::mods::SetVector(work, 2, still);
  Call(work, base, kSetPosition);
}

void Kill(const PPCContext& ctx, uint8_t* base, uint32_t instance) {
  PPCContext work = refii::mods::Frame(ctx, 0x300u);
  work.r3.u32 = instance;
  work.r4.u32 = 0u;
  Call(work, base, kFade);
}

/// Where the effect stands this tick: its entity's position plus the offset, or its stated point.
/// False when its entity is gone.
bool Where(PPCContext& ctx, uint8_t* base, const Played& p, float* xyz) {
  if (p.entity == 0u) {
    xyz[0] = p.at[0];
    xyz[1] = p.at[1];
    xyz[2] = p.at[2];
    return true;
  }
  const uint32_t entity = refii_mod_handle_address(p.entity);
  if (entity == 0u || refii_mod_entity_position(&ctx, base, entity, xyz) == 0) {
    return false;
  }
  for (int i = 0; i < 3; ++i) {
    xyz[i] += p.offset[i];
  }
  return true;
}

/// Spawns `p` where it stands and states its options; false when nothing was spawned.
bool Start(PPCContext& ctx, uint8_t* base, Played& p) {
  float xyz[3];
  if (!Where(ctx, base, p, xyz)) {
    return false;
  }
  p.holder = Spawn(ctx, base, p.hash);
  const uint32_t instance = Instance(base, p.holder);
  if (instance == 0u) {
    Drop(ctx, base, p.holder);
    p.holder = 0u;
    return false;
  }
  Place(ctx, base, instance, xyz);
  if (p.scale != 1.0f) {
    SetFloat(ctx, base, instance, kSetScale, p.scale);
  }
  SetFloat(ctx, base, instance, kSetIntensity, p.intensity);
  return true;
}

void Stop(PPCContext& ctx, uint8_t* base, uint32_t id) {
  auto found = g_played.find(id);
  if (found == g_played.end()) {
    return;
  }
  const uint32_t instance = Instance(base, found->second.holder);
  if (instance != 0u && refii::mods::LoadU32(base, instance + kManagerSlot) != 0xFFFFFFFFu) {
    Kill(ctx, base, instance);
  }
  Drop(ctx, base, found->second.holder);
  g_played.erase(found);
}

void Play(PPCContext& ctx, uint8_t* base, const std::vector<std::string>& w) {
  if (w.size() < 13) {
    return;
  }
  const uint32_t id = U(w, 1);
  Stop(ctx, base, id);
  Played p;
  p.hash = U(w, 2);
  p.at[0] = F(w, 3);
  p.at[1] = F(w, 4);
  p.at[2] = F(w, 5);
  p.entity = U(w, 6);
  p.offset[0] = F(w, 7);
  p.offset[1] = F(w, 8);
  p.offset[2] = F(w, 9);
  p.scale = F(w, 10);
  p.intensity = F(w, 11);
  p.loop = I(w, 12) != 0;
  if (id == 0u || p.hash == 0u || !Start(ctx, base, p)) {
    return;
  }
  g_played.emplace(id, p);
}

/// Once a tick: an entity's effect follows it, a retired one-shot is let go, a retired loop spawns
/// again, and one whose entity is gone ends with it.
void Pump(PPCContext& ctx, uint8_t* base) {
  std::vector<uint32_t> done;
  for (auto& [id, p] : g_played) {
    const uint32_t instance = Instance(base, p.holder);
    const bool retired =
        instance == 0u || refii::mods::LoadU32(base, instance + kManagerSlot) == 0xFFFFFFFFu;
    float xyz[3];
    const bool placed = Where(ctx, base, p, xyz);
    if (!placed) {
      done.push_back(id);
      continue;
    }
    if (retired) {
      Drop(ctx, base, p.holder);
      p.holder = 0u;
      if (!p.loop || !Start(ctx, base, p)) {
        done.push_back(id);
      }
      continue;
    }
    if (p.entity != 0u) {
      Place(ctx, base, instance, xyz);
    }
  }
  for (uint32_t id : done) {
    Stop(ctx, base, id);
  }
}

}  // namespace guest_fx

void RaiseMessageBox(PPCContext& ctx, uint8_t* base, const std::vector<std::string>& w) {
  refii::mods::ModMessageBoxEvent e{};
  e.ctx = &ctx;
  e.base = base;
  e.id = U(w, 1);
  e.button = I(w, 2);
  e.player = I(w, 3);
  refii::mods::Dispatch(e);
}

void RunOne(PPCContext& ctx, uint8_t* base, const char* line) {
  const std::vector<std::string> w = Split(line, std::strncmp(line, "run_lua ", 8) == 0 ? 1 : 64);
  if (w.empty()) {
    return;
  }
  const std::string& op = w[0];
  if (op == "query") {
    RunQuery(ctx, base, w);
  } else if (op == "give_gold") {
    refii_mod_give_gold(&ctx, base, E(w, 1), I(w, 2));
  } else if (op == "modify_health") {
    refii_mod_modify_health(&ctx, base, E(w, 1), F(w, 2), E(w, 3));
  } else if (op == "set_health") {
    refii_mod_set_health(base, E(w, 1), F(w, 2));
  } else if (op == "add_item") {
    refii_mod_add_item(&ctx, base, E(w, 1), U(w, 2));
  } else if (op == "give_experience") {
    refii_mod_give_experience(&ctx, base, E(w, 1), I(w, 2), F(w, 3));
  } else if (op == "spend_experience") {
    refii_mod_spend_experience(&ctx, base, E(w, 1), I(w, 2), F(w, 3));
  } else if (op == "cast_spell") {
    refii_mod_cast_spell(&ctx, base, E(w, 1), I(w, 2), I(w, 3), E(w, 4), I(w, 5), nullptr, nullptr);
  } else if (op == "set_position") {
    const float xyz[3] = {F(w, 2), F(w, 3), F(w, 4)};
    refii_mod_set_position(&ctx, base, E(w, 1), xyz);
  } else if (op == "player_set" && w.size() >= 4) {
    refii_mod_player_set(I(w, 1), w[2].c_str(), F(w, 3));
  } else if (op == "player_add" && w.size() >= 4) {
    refii_mod_player_add(I(w, 1), w[2].c_str(), F(w, 3), I(w, 4) != 0 ? 1 : 0);
  } else if (op == "player_save" && w.size() >= 4) {
    refii_mod_player_save(I(w, 1), w[2].c_str(), F(w, 3));
  } else if (op == "save_set" && w.size() >= 3) {
    refii_mod_save_set_int(&ctx, base, w[1].c_str(), I(w, 2));
  } else if (op == "forgive_crimes") {
    refii_mod_forgive_crimes(&ctx, base, E(w, 1), refii_mod_player_hero(base, I(w, 2)));
  } else if (op == "publish" && w.size() >= 3) {
    refii::mods::Publish(w[1], F(w, 2));
  } else if (op == "menu_row" && w.size() >= 4) {
    RaiseMenuRow(ctx, base, w);
  } else if (op == "box_answer" && w.size() >= 4) {
    RaiseMessageBox(ctx, base, w);
  } else if (op == "run_lua" && w.size() >= 2) {
    refii_mod_run_game_lua(&ctx, base, w[1].c_str());
  } else if (op == "fx_play") {
    guest_fx::Play(ctx, base, w);
  } else if (op == "fx_stop") {
    guest_fx::Stop(ctx, base, U(w, 1));
  }
}

}  // namespace

namespace refii::vfs {

void RunModCommands(PPCContext& ctx, uint8_t* base) {
  char line[4096];
  // Bounded per tick so a flooding script cannot stall the simulation.
  for (int n = 0; n < 64 && refii_bridge_mod_command_poll(line, sizeof(line)) != 0; ++n) {
    PPCContext work = ctx;
    RunOne(work, base, line);
  }
  PPCContext work = ctx;
  guest_fx::Pump(work, base);
}

}  // namespace refii::vfs
