#include <cassert>
#include <limits>
#include <string>
#include <unordered_map>
#include <vector>
#include "../../src/hooks/src/character_morph_hooks.cpp"

namespace {
std::unordered_map<std::string, float> settings;
uint32_t player = 0x20000;
bool child = false;
int refreshes = 0;
int originals = 0;
refii_mod_tick_fn tick = nullptr;
}

extern "C" void refii_mod_register_tick(refii_mod_tick_fn fn) { tick = fn; }
extern "C" uint32_t refii_mod_local_hero(uint8_t*) { return player; }
extern "C" uint32_t refii_mod_player_hero(uint8_t*, int32_t index) { return index == 1 ? player : 0; }
extern "C" int32_t refii_mod_entity_alive(uint8_t*, uint32_t entity) { return entity != 0; }
extern "C" int32_t refii_mod_run_game_lua(PPCContext*, uint8_t*, const char* source) {
  assert(std::string(source).find("GraphicAppearanceMorph.UpdateTextureMorphs") != std::string::npos);
  ++refreshes;
  return 1;
}
extern "C" float refii_bridge_mod_setting_f32(const char* key, size_t length, float fallback) {
  auto found = settings.find(std::string(key, length));
  return found == settings.end() ? fallback : found->second;
}
extern "C" REX_FUNC(__imp__rex_CECMorph_GetWeight) {
  ++originals;
  ctx.f1.f64 = ctx.r4.u32 == 1 ? (child ? 0 : 1) : 0.25;
}

int main() {
  std::vector<uint8_t> memory(0x40000);
  uint8_t* base = memory.data();
  PPCContext ctx{};
  ctx.r1.u32 = 0x30000;
  const uint32_t component = 0x10000;
  refii::mods::StoreU32(base, component + 4, player);
  const auto untouched = memory;
  auto weight = [&](uint32_t type) {
    ctx.r3.u64 = component;
    ctx.r4.u64 = type;
    rex_CECMorph_GetWeight(ctx, base);
    return ctx.f1.f64;
  };
  auto update = [&](bool automatic, float value, float generation) {
    settings["refii.character_morphs.automatic"] = automatic ? 1 : 0;
    settings["refii.character_morphs.generation"] = generation;
    settings["refii.character_morphs.5"] = value;
    Tick(&ctx, base, 0.06f);
  };
  update(true, 0.75f, 1);
  assert(weight(5) == 0.25);
  assert(refreshes == 0);
  update(false, 0.75f, 2);
  assert(weight(5) == 0.75);
  assert(refreshes == 1);
  for (int i = 0; i < 30; ++i) Tick(&ctx, base, 0.06f);
  assert(refreshes == 1);
  assert(weight(1) == 1);
  assert(weight(19) == 0.25);
  child = true;
  assert(weight(5) == 0);
  child = false;
  assert(weight(5) == 0.75);
  update(false, 0.5f, 3);
  assert(weight(5) == 0.5);
  assert(refreshes == 2);
  assert(memory == untouched);
  refii::mods::StoreU32(base, component + 4, 0x21000);
  assert(weight(5) == 0.25);
  refii::mods::StoreU32(base, component + 4, player);
  update(false, std::numeric_limits<float>::quiet_NaN(), 4);
  assert(weight(5) == 0);
  update(true, 0.5f, 5);
  assert(weight(5) == 0.25);
  assert(refreshes == 4);
  update(false, 0.75f, 6);
  assert(weight(5) == 0.75);
  player = 0;
  Tick(&ctx, base, 0.06f);
  assert(weight(5) == 0.25);
  refii::mods::StoreU32(base, component + 4, 0);
  assert(weight(5) == 0.25);
  player = 0x22000;
  refii::mods::StoreU32(base, component + 4, player);
  Tick(&ctx, base, 0.06f);
  assert(weight(5) == 0.75);
  ctx.r3.u64 = 0;
  rex_CECMorph_GetWeight(ctx, base);
  assert(ctx.f1.f64 == 0.25);
  assert(originals > 0);
  std::puts("Character morph hook: automatic/manual, child, NPC, reload, refresh cadence and stat preservation passed");
}
