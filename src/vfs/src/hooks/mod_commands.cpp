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
//   query ID lua_int EXPR... (last two tokens LO HI) -> id,ok,value
//   query ID renown E -> renown · morality E / purity E -> value · physique E -> fat,height
//   query ID age E -> age · equipped E -> melee,ranged · inventory_stack E I -> definition,count
//   query ID near X Y Z R [CREATURES [OFFSET]] -> total,offset,count,e1..ecount (100 a page)
//   query ID line_of_sight X1 Y1 Z1 X2 Y2 Z2 -> clear
//   query ID spawn DEF X Y Z -> entity
// A command that does nothing (no such entity, no component) is silent; a query always replies,
// with ok=false when it could not be answered.

#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>

#include "core/mod_values.h"
#include "core/refii_mod.h"

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
  } else if (op == "save_set" && w.size() >= 3) {
    refii_mod_save_set_int(&ctx, base, w[1].c_str(), I(w, 2));
  } else if (op == "run_lua" && w.size() >= 2) {
    refii_mod_run_game_lua(&ctx, base, w[1].c_str());
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
}

}  // namespace refii::vfs
