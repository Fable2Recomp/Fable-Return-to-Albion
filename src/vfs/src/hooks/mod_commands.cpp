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
//   entity_tint E R G B A · entity_untint E   (refii_mod_entity_tint: the game's own tint setter)
//   entity_alpha E A · entity_scale E S · entity_saturation_mask E ON · entity_drawable E ON
//                                (the other component-3 setters, refii_mod_entity_alpha ..)
//   lock_target P E · release_lock P · switch_target P LEFT   (refii_mod_lock_target ..: P 1 or 2,
//                                E 0 for the game's own pick, LEFT 1 left / 0 right)
//   apply_status E KIND SECONDS [LEVEL [SOURCE]] · clear_status E KIND   (refii_mod_apply_status:
//                                1 electrified 2 chaos 3 burning)
//   knockback E X Y Z [FORCE] · stagger E   (refii_mod_knockback / refii_mod_stagger)
//   play_sound CUE E [TAG] · play_sound_at CUE X Y Z [TAG] · stop_sound E TAG [FADE_MS]
//                                (refii_mod_play_sound_on / _at / refii_mod_stop_sound: E 0 for the
//                                point emitters; an empty tag is "-")
//   play_music NAME · stop_music NAME   (refii_mod_play_music / refii_mod_stop_music)
//   play_animation E NAME [LOOPS [BLEND [SPEED]]] · stop_animation E
//                                (refii_mod_play_animation / refii_mod_stop_animation)
//   modify_opinion V H AXIS DELTA · add_deed V H DEED   (refii_mod_modify_opinion / refii_mod_add_deed)
//   npc_follow E T [RADIUS] · npc_go_to E X Y Z · npc_flee E FROM · npc_attack E T
//   npc_set_hostile E OTHER ON · npc_stop E   (refii_mod_npc_*: the quests' own orders, in the game VM)
//   set_time_of_day HOURS · set_weather THEME [WEIGHT [SECONDS]]   (refii_mod_set_time_of_day /
//                                refii_mod_set_weather: the clock's setter, the theme blend)
//   save_set KEY INT · run_lua CHUNK...
//   player_set P KEY V · player_add P KEY D [SAVED] · player_save P KEY V   (the player store
//     Refii.player_set / _add / _save write: refii_mod_player_*)
//   publish KEY VALUE            -> refii::mods::Publish: the value every reader sees ("mod:<key>")
//   menu_row PLAYER MENU ROW MOD -> raises ModMenuRow for the native and Lua handlers (a row chosen
//                                   in Godot; a space in a name arrives as '_', an empty one as '-')
//   box_answer ID BUTTON PLAYER  -> raises ModMessageBox for the native and Lua handlers (a mod's
//                                   message box answered in Godot: 0 A, 1 B, -1 replaced)
//   notice_done ID OUTCOME PLAYER -> raises ModNotice (a mod's notice gone: 0 shown, -1 never shown)
//   menu_box_answer ID OPTION PLAYER -> raises ModMenuBox (a mod's menu box answered: the option, -1 none)
//   query ID health E            -> id,ok,current,max
//   query ID position E          -> id,ok,x,y,z
//   query ID gold E              -> id,ok,gold
//   query ID item_count E DEF    -> id,ok,count
//   query ID experience E POOL   -> id,ok,amount
//   query ID ability_level E T   -> id,ok,level
//   query ID relation A B        -> id,ok,relation
//   query ID alive E             -> id,ok,alive
//   query ID locked_target P     -> id,ok,target  (refii_mod_locked_target: 0 when not locked)
//   query ID has_status E KIND   -> id,ok,has     (refii_mod_has_status)
//   query ID time_of_day         -> id,ok,hours   (refii_mod_time_of_day: the clock, 0..24)
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
//   fx_play ID HASH X Y Z ENTITY OX OY OZ SCALE INTENSITY LOOP [R G B]
//                        play_fx under the classic look (Engine_FxPlayer): the guest's own particle
//                        instance of effect HASH (its bank key), at X Y Z or riding handle ENTITY
//                        (0 for none) in its frame, offset by OX OY OZ along its axes and turned
//                        with it (guest_fx below); SCALE and INTENSITY the instance's own
//                        setters; LOOP 1 spawns it again each time it retires; R G B the tint, on
//                        the instance's own material copy (particle_dress.cpp TintFxInstance)
//   fx_stop ID           ends it: the guest's kill (a zero-frame fade) and our reference released
//   fx_reload            the guest's particle bank read again through its own loader after a
//                        remount (particle_dress.cpp ReloadParticleBank)
//   frontend_lua CHUNK...  a chunk in the front end's mod Lua (mod_frontend_lua.cpp): the current
//                        Gameface presentation's state, the mods' front-end chunk run in it first
//   ability_clear · ability_declare … · ability_buy P NAME · ability_refund P NAME
//                        the mod abilities' engine path (mod_abilities.cpp): the book declares each
//                        ui/abilities.json record, A and Y post the purchase and the unlearn
//   spell_pick P SLOT SPELL STORE PICK MOD
//                        a pick on a spell slot (ui/spellwheel.json): raises SpellPick, then saves
//                        the player value STORE unless a handler refused it (mod_abilities.cpp)
//   partner_send MOD NAME [BASE64]
//                        refii_mod_send_to_partner for a Godot script: the payload base64 on the
//                        line (no spaces), raised on the other machine as PartnerMessage
// A command that does nothing (no such entity, no component) is silent; a query always replies,
// with ok=false when it could not be answered.

#include <cmath>
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

namespace refii::vfs {
// particle_dress.cpp
void TintFxInstance(const PPCContext& ctx, uint8_t* base, uint32_t instance, const float* rgb);
bool ReloadParticleBank(PPCContext& ctx, uint8_t* base);
void PumpParticleBank(PPCContext& ctx, uint8_t* base);
// mod_frontend_lua.cpp
bool GameLuaUp(uint8_t* base);
bool RunFrontEndLua(PPCContext& ctx, uint8_t* base, const std::string& chunk);
// mod_abilities.cpp
bool RunModAbilityCommand(PPCContext& ctx, uint8_t* base, const std::vector<std::string>& words);
void PumpModAbilities(PPCContext& ctx, uint8_t* base);
}  // namespace refii::vfs

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

/// Standard base64 back to text (a `partner_send` payload); false for a character outside the alphabet.
bool UnbaseCommand(const std::string& text, std::string& out) {
  uint32_t word = 0;
  int bits = 0;
  for (const char c : text) {
    if (c == '=') {
      break;
    }
    int value;
    if (c >= 'A' && c <= 'Z') {
      value = c - 'A';
    } else if (c >= 'a' && c <= 'z') {
      value = c - 'a' + 26;
    } else if (c >= '0' && c <= '9') {
      value = c - '0' + 52;
    } else if (c == '+') {
      value = 62;
    } else if (c == '/') {
      value = 63;
    } else {
      return false;
    }
    word = (word << 6) | static_cast<uint32_t>(value);
    bits += 6;
    if (bits >= 8) {
      bits -= 8;
      out += static_cast<char>((word >> bits) & 0xFFu);
    }
  }
  return true;
}
int32_t I(const std::vector<std::string>& w, size_t i) {
  return i < w.size() ? static_cast<int32_t>(std::strtol(w[i].c_str(), nullptr, 10)) : 0;
}
float F(const std::vector<std::string>& w, size_t i) {
  return i < w.size() ? std::strtof(w[i].c_str(), nullptr) : 0.0f;
}
/// A sound tag: absent or "-" is the empty tag (a line cannot carry an empty word).
const char* Tag(const std::vector<std::string>& w, size_t i) {
  return i < w.size() && w[i] != "-" ? w[i].c_str() : "";
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
  } else if (what == "locked_target") {
    ok = true;
    std::snprintf(fields, sizeof(fields), "target=%u", refii_mod_handle(refii_mod_locked_target(&ctx, base, I(w, 3))));
  } else if (what == "has_status") {
    ok = true;
    std::snprintf(fields, sizeof(fields), "has=%s", refii_mod_has_status(base, E(w, 3), I(w, 4)) != 0 ? "true" : "false");
  } else if (what == "time_of_day") {
    float hours = 0.0f;
    ok = refii_mod_time_of_day(base, &hours) != 0;
    std::snprintf(fields, sizeof(fields), "hours=%g", static_cast<double>(hours));
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
  // No game VM (the title, the front end): the mods' front-end Lua hears it instead.
  if (!refii::vfs::GameLuaUp(base)) {
    char menu[160];
    char row[160];
    char mod[96];
    Quote(e.menu, menu, sizeof(menu));
    Quote(e.row, row, sizeof(row));
    Quote(e.mod, mod, sizeof(mod));
    char chunk[640];
    std::snprintf(chunk, sizeof(chunk), "if Refii and Refii._fire then Refii._fire(%d, {player=%d,menu='%s',row='%s',mod='%s'}) end",
                  REFII_EVENT_MOD_MENU_ROW, e.player, menu, row, mod);
    refii::vfs::RunFrontEndLua(ctx, base, chunk);
  }
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
// colour is its system's material keyframe table), so a TINT is the instance's emitters pointed at
// a tinted copy of that material (particle_dress.cpp TintFxInstance).
//
// END OF LIFE: `sub_83232048(inst, frames)` sets every element's +0xC0 = +0xC4 = frames; zero fails
// the liveness test at once and the retire pass `sub_83234338` drops the instance, writing -1 back
// to its +44 (0x832347c8). That word is how a one-shot is known to have run out.
//
// A PLAY ON AN ENTITY STANDS IN THE ENTITY'S FRAME, placed the way the guest places its own FX
// entity's instance (sub_825F8900 TU1 / sub_824B8938 disc, identical sequences):
//
//   825f8ac0  lwz r4,0x7C(r30)          the entity's placement component
//   825f8ad0  lwz r10,0x40(r11) / bctrl  its position (sret: buffer in r3, `this` in r4)
//   825f8ae8  lwz r8,0x44(r9) / bctrl    its orientation, the same way; r3 back = the quaternion
//   825f8b04  ld r5,0(r7) / ld r4,8(r7)  its two halves, re-stored and reloaded as r4 = bytes 0..7,
//   825f8b30  ld r4,.. / ld r5,..        r5 = bytes 8..15
//   825f8b2c  bl sub_832325A8           the position on the instance, then
//   825f8b3c  bl sub_83231C40           the orientation: each emitter's sub_83228D78 stores r4:r5
//                                       at emitter+0x10 and recomposes its matrix at +0x20 about
//                                       the position already at +0x50
//
// The per-tick emitter writers (sub_8322E4D0, sub_83230880) rewrite only the matrix's row 3
// (+0x50, `li r7,0x50 / stvx128` 0x8323091c), so the orientation stands until it is stated again.
// The quaternion is (x, y, z, w) and its rows are the entity's local axes (sub_8220C8F0, the form
// GameEntities.ToGodot draws the same entity with), so the offset is laid along them.
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
constexpr uint32_t kSetOrientation = 0x83231C40u;
constexpr uint32_t kEntityPlacement = 0x7Cu;
constexpr uint32_t kPlacementOrientationSlot = 0x44u;
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
  float tint[3] = {1.0f, 1.0f, 1.0f};
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

/// Where an instance stands: a point, and for a play on an entity that entity's orientation as
/// its placement states it (four raw words, x y z w), when it states one that is a rotation.
struct Stand {
  float xyz[3] = {0.0f, 0.0f, 0.0f};
  uint32_t quaternion[4] = {0u, 0u, 0u, 0u};
  bool turned = false;
};

/// The entity's orientation through its placement component's slot 0x44, as sub_825F8900 asks
/// for it (above). False when it has no placement or the answer is not a unit quaternion.
bool Orientation(const PPCContext& ctx, uint8_t* base, uint32_t entity, uint32_t* words, float* q) {
  using refii::mods::LoadU32;
  const uint32_t placement = Plausible(entity) ? LoadU32(base, entity + kEntityPlacement) : 0u;
  const uint32_t vtable = Plausible(placement) ? LoadU32(base, placement) : 0u;
  if (!Plausible(vtable)) {
    return false;
  }
  PPCContext work = refii::mods::Frame(ctx, 0x300u);
  const uint32_t out = (work.r1.u32 + 0x100u) & ~0xFu;
  work.r3.u32 = out;
  work.r4.u32 = placement;
  if (!refii::mods::CallGuest(work, base, LoadU32(base, vtable + kPlacementOrientationSlot))) {
    return false;
  }
  const uint32_t at = Plausible(work.r3.u32) ? work.r3.u32 : out;
  float length = 0.0f;
  for (uint32_t i = 0; i < 4u; ++i) {
    words[i] = LoadU32(base, at + 4u * i);
    std::memcpy(&q[i], &words[i], sizeof(float));
    length += q[i] * q[i];
  }
  return std::isfinite(length) && length > 0.98f && length < 1.02f;
}

void Place(const PPCContext& ctx, uint8_t* base, uint32_t instance, const Stand& at) {
  Place(ctx, base, instance, at.xyz);
  if (!at.turned) {
    return;
  }
  PPCContext work = refii::mods::Frame(ctx, 0x300u);
  work.r3.u32 = instance;
  work.r4.u64 = (static_cast<uint64_t>(at.quaternion[0]) << 32) | at.quaternion[1];
  work.r5.u64 = (static_cast<uint64_t>(at.quaternion[2]) << 32) | at.quaternion[3];
  Call(work, base, kSetOrientation);
}

void Kill(const PPCContext& ctx, uint8_t* base, uint32_t instance) {
  PPCContext work = refii::mods::Frame(ctx, 0x300u);
  work.r3.u32 = instance;
  work.r4.u32 = 0u;
  Call(work, base, kFade);
}

/// Where the effect stands this tick: in its entity's frame (the entity's position, the offset
/// along its axes, its orientation), or at its stated point. An entity whose placement states no
/// rotation keeps the offset in world axes. False when its entity is gone.
bool Where(PPCContext& ctx, uint8_t* base, const Played& p, Stand& at) {
  at = Stand{};
  float* xyz = at.xyz;
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
  float q[4];
  at.turned = Orientation(ctx, base, entity, at.quaternion, q);
  if (!at.turned) {
    for (int i = 0; i < 3; ++i) {
      xyz[i] += p.offset[i];
    }
    return true;
  }
  // The rows of sub_8220C8F0's matrix (s = 2/|q|^2): the entity's local x, y and z.
  const float x = q[0], y = q[1], z = q[2], w = q[3];
  const float s = 2.0f / (x * x + y * y + z * z + w * w);
  const float rows[3][3] = {
      {1.0f - s * (y * y + z * z), s * (x * y + z * w), s * (x * z - y * w)},
      {s * (x * y - z * w), 1.0f - s * (x * x + z * z), s * (y * z + x * w)},
      {s * (x * z + y * w), s * (y * z - x * w), 1.0f - s * (x * x + y * y)},
  };
  for (int i = 0; i < 3; ++i) {
    xyz[i] += p.offset[0] * rows[0][i] + p.offset[1] * rows[1][i] + p.offset[2] * rows[2][i];
  }
  return true;
}

/// Spawns `p` where it stands and states its options; false when nothing was spawned.
bool Start(PPCContext& ctx, uint8_t* base, Played& p) {
  Stand xyz;
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
  // On top of the effect's own tint, which the constructor hook has already put on its material.
  refii::vfs::TintFxInstance(ctx, base, instance, p.tint);
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
  if (w.size() >= 16) {
    for (int c = 0; c < 3; ++c) {
      const float v = F(w, 13 + static_cast<size_t>(c));
      p.tint[c] = v >= 0.0f ? v : 0.0f;
    }
  }
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
    Stand xyz;
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

/// The plays riding `entity`, placed where it stands now: called from inside the update in which
/// the guest moves that entity and its own effects (spell_fx_owner.cpp), so a ridden play stands
/// where the guest put them in the same tick rather than where the entity stood when Pump ran.
void PlaceRiding(PPCContext& ctx, uint8_t* base, uint32_t entity) {
  if (entity == 0u || g_played.empty()) {
    return;
  }
  for (auto& [id, p] : g_played) {
    if (p.entity == 0u || refii_mod_handle_address(p.entity) != entity) {
      continue;
    }
    const uint32_t instance = Instance(base, p.holder);
    if (instance == 0u || refii::mods::LoadU32(base, instance + kManagerSlot) == 0xFFFFFFFFu) {
      continue;
    }
    Stand xyz;
    if (Where(ctx, base, p, xyz)) {
      Place(ctx, base, instance, xyz);
    }
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
  if (!refii::vfs::GameLuaUp(base)) {
    char chunk[256];
    std::snprintf(chunk, sizeof(chunk), "if Refii and Refii._fire then Refii._fire(%d, {id=%u,button=%d,player=%d}) end",
                  REFII_EVENT_MOD_MESSAGE_BOX, e.id, e.button, e.player);
    refii::vfs::RunFrontEndLua(ctx, base, chunk);
  }
}

void RaiseNotice(PPCContext& ctx, uint8_t* base, const std::vector<std::string>& w) {
  refii::mods::ModNoticeEvent e{};
  e.ctx = &ctx;
  e.base = base;
  e.id = U(w, 1);
  e.outcome = I(w, 2);
  e.player = I(w, 3);
  refii::mods::Dispatch(e);
  if (!refii::vfs::GameLuaUp(base)) {
    char chunk[256];
    std::snprintf(chunk, sizeof(chunk), "if Refii and Refii._fire then Refii._fire(%d, {id=%u,outcome=%d,player=%d}) end",
                  REFII_EVENT_MOD_NOTICE, e.id, e.outcome, e.player);
    refii::vfs::RunFrontEndLua(ctx, base, chunk);
  }
}

void RaiseMenuBox(PPCContext& ctx, uint8_t* base, const std::vector<std::string>& w) {
  refii::mods::ModMenuBoxEvent e{};
  e.ctx = &ctx;
  e.base = base;
  e.id = U(w, 1);
  e.option = I(w, 2);
  e.player = I(w, 3);
  refii::mods::Dispatch(e);
  if (!refii::vfs::GameLuaUp(base)) {
    char chunk[256];
    std::snprintf(chunk, sizeof(chunk), "if Refii and Refii._fire then Refii._fire(%d, {id=%u,option=%d,player=%d}) end",
                  REFII_EVENT_MOD_MENU_BOX, e.id, e.option, e.player);
    refii::vfs::RunFrontEndLua(ctx, base, chunk);
  }
}

void RunOne(PPCContext& ctx, uint8_t* base, const char* line) {
  const bool chunk = std::strncmp(line, "run_lua ", 8) == 0 || std::strncmp(line, "frontend_lua ", 13) == 0;
  const std::vector<std::string> w = Split(line, chunk ? 1 : 64);
  if (w.empty()) {
    return;
  }
  const std::string& op = w[0];
  if (refii::vfs::RunModAbilityCommand(ctx, base, w)) {
    return;
  }
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
  } else if (op == "entity_tint" && w.size() >= 5) {
    refii_mod_entity_tint(&ctx, base, E(w, 1), F(w, 2), F(w, 3), F(w, 4), w.size() >= 6 ? F(w, 5) : 1.0f);
  } else if (op == "entity_untint") {
    refii_mod_entity_untint(&ctx, base, E(w, 1));
  } else if (op == "entity_alpha" && w.size() >= 3) {
    refii_mod_entity_alpha(&ctx, base, E(w, 1), F(w, 2));
  } else if (op == "entity_scale" && w.size() >= 3) {
    refii_mod_entity_scale(&ctx, base, E(w, 1), F(w, 2));
  } else if (op == "entity_saturation_mask" && w.size() >= 3) {
    refii_mod_entity_saturation_mask(&ctx, base, E(w, 1), I(w, 2));
  } else if (op == "entity_drawable" && w.size() >= 3) {
    refii_mod_entity_drawable(&ctx, base, E(w, 1), I(w, 2));
  } else if (op == "lock_target" && w.size() >= 2) {
    refii_mod_lock_target(&ctx, base, I(w, 1), w.size() >= 3 ? E(w, 2) : 0u);
  } else if (op == "release_lock" && w.size() >= 2) {
    refii_mod_release_lock(&ctx, base, I(w, 1));
  } else if (op == "switch_target" && w.size() >= 3) {
    refii_mod_switch_target(&ctx, base, I(w, 1), I(w, 2));
  } else if (op == "apply_status" && w.size() >= 4) {
    refii_mod_apply_status(&ctx, base, E(w, 1), I(w, 2), F(w, 3), w.size() >= 5 ? I(w, 4) : 0,
                           w.size() >= 6 ? E(w, 5) : 0u);
  } else if (op == "clear_status" && w.size() >= 3) {
    refii_mod_clear_status(&ctx, base, E(w, 1), I(w, 2));
  } else if (op == "knockback" && w.size() >= 5) {
    const float dir[3] = {F(w, 2), F(w, 3), F(w, 4)};
    refii_mod_knockback(&ctx, base, E(w, 1), dir, w.size() >= 6 ? F(w, 5) : 1.0f);
  } else if (op == "stagger" && w.size() >= 2) {
    refii_mod_stagger(&ctx, base, E(w, 1));
  } else if (op == "play_sound" && w.size() >= 3) {
    refii_mod_play_sound_on(&ctx, base, E(w, 2), w[1].c_str(), Tag(w, 3));
  } else if (op == "play_sound_at" && w.size() >= 5) {
    const float xyz[3] = {F(w, 2), F(w, 3), F(w, 4)};
    refii_mod_play_sound_at(&ctx, base, xyz, w[1].c_str(), Tag(w, 5));
  } else if (op == "stop_sound" && w.size() >= 3) {
    refii_mod_stop_sound(&ctx, base, E(w, 1), Tag(w, 2), w.size() >= 4 ? I(w, 3) : 0);
  } else if (op == "play_music" && w.size() >= 2) {
    refii_mod_play_music(&ctx, base, w[1].c_str());
  } else if (op == "stop_music" && w.size() >= 2) {
    refii_mod_stop_music(&ctx, base, w[1].c_str());
  } else if (op == "play_animation" && w.size() >= 3) {
    refii_mod_play_animation(&ctx, base, E(w, 1), w[2].c_str(), w.size() >= 4 ? I(w, 3) : 1,
                             w.size() >= 5 ? F(w, 4) : 0.1f, w.size() >= 6 ? F(w, 5) : 1.0f);
  } else if (op == "stop_animation" && w.size() >= 2) {
    refii_mod_stop_animation(&ctx, base, E(w, 1));
  } else if (op == "modify_opinion" && w.size() >= 5) {
    refii_mod_modify_opinion(&ctx, base, E(w, 1), E(w, 2), I(w, 3), F(w, 4));
  } else if (op == "add_deed" && w.size() >= 4) {
    refii_mod_add_deed(&ctx, base, E(w, 1), E(w, 2), U(w, 3));
  } else if (op == "npc_follow" && w.size() >= 3) {
    refii_mod_npc_follow(&ctx, base, E(w, 1), E(w, 2), w.size() >= 4 ? F(w, 3) : 2.0f);
  } else if (op == "npc_go_to" && w.size() >= 5) {
    const float xyz[3] = {F(w, 2), F(w, 3), F(w, 4)};
    refii_mod_npc_go_to(&ctx, base, E(w, 1), xyz);
  } else if (op == "npc_flee" && w.size() >= 3) {
    refii_mod_npc_flee(&ctx, base, E(w, 1), E(w, 2));
  } else if (op == "npc_attack" && w.size() >= 3) {
    refii_mod_npc_attack(&ctx, base, E(w, 1), E(w, 2));
  } else if (op == "npc_set_hostile" && w.size() >= 4) {
    refii_mod_npc_set_hostile(&ctx, base, E(w, 1), E(w, 2), I(w, 3) != 0 ? 1 : 0);
  } else if (op == "npc_stop" && w.size() >= 2) {
    refii_mod_npc_stop(&ctx, base, E(w, 1));
  } else if (op == "set_time_of_day" && w.size() >= 2) {
    refii_mod_set_time_of_day(&ctx, base, F(w, 1));
  } else if (op == "set_weather" && w.size() >= 2) {
    refii_mod_set_weather(&ctx, base, w[1].c_str(), w.size() >= 3 ? F(w, 2) : 1.0f, w.size() >= 4 ? F(w, 3) : 1.0f);
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
  } else if (op == "notice_done" && w.size() >= 4) {
    RaiseNotice(ctx, base, w);
  } else if (op == "menu_box_answer" && w.size() >= 4) {
    RaiseMenuBox(ctx, base, w);
  } else if (op == "run_lua" && w.size() >= 2) {
    refii_mod_run_game_lua(&ctx, base, w[1].c_str());
  } else if (op == "partner_send" && w.size() >= 3) {
    // partner_send MOD NAME [BASE64]: a Godot script's refii_mod_send_to_partner; the payload crosses
    // the command line base64 (no spaces) and is decoded here before the API encodes it for the route.
    std::string payload;
    if (w.size() >= 4 && !UnbaseCommand(w[3], payload)) {
      payload.clear();
    }
    refii_mod_send_to_partner(w[1].c_str(), w[2].c_str(), payload.c_str());
  } else if (op == "fx_play") {
    guest_fx::Play(ctx, base, w);
  } else if (op == "fx_stop") {
    guest_fx::Stop(ctx, base, U(w, 1));
  } else if (op == "fx_reload") {
    refii::vfs::ReloadParticleBank(ctx, base);
  } else if (op == "frontend_lua" && w.size() >= 2) {
    refii::vfs::RunFrontEndLua(ctx, base, w[1]);
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
  work = ctx;
  refii::vfs::PumpParticleBank(work, base);
  work = ctx;
  refii::vfs::PumpModAbilities(work, base);
}

/// The classic plays riding `entity` placed at it now (guest_fx::PlaceRiding); guest thread only.
void PlaceFxRiding(PPCContext& ctx, uint8_t* base, uint32_t entity) {
  PPCContext work = ctx;
  guest_fx::PlaceRiding(work, base, entity);
}

}  // namespace refii::vfs
