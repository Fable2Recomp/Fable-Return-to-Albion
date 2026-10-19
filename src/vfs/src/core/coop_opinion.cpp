// refii — A VILLAGER'S OPINION OF EACH HERO (Fable 3 keeps one record per hero, `comp+864`, and
// pushes the visitor's to them, msg 0x1E2). Fable 2's opinion component (CECOpinionReaction, 47)
// holds ONE set, of "the hero": base axes at +24 and current axes at +44 (five floats each: love,
// fear, attractiveness, morality, respect — `GetCurrentOpinion` 0x82786DD0, `ModifyAxisValue`
// 0x827915D8) and a deed-modifier list per axis at +176 (five 12-byte vectors). Player two's set
// is kept here and swapped into the component while it handles a deed of player two's
// (mod_events.cpp, the deed seam), so their deeds move the villager's feelings about THEM; the
// host reads it for player two's targeted readout. Here, in refii_vfs, because both link it.

#include <array>
#include <cstdint>
#include <cstring>
#include <mutex>
#include <unordered_map>

#include "core/payload_bridge.h"

namespace {

struct State {
  std::mutex mutex;
  std::unordered_map<uint32_t, std::array<uint8_t, REFII_OPINION_BLOCK_BYTES>> sets;
};

State& Tables() {
  static State state;
  return state;
}

}  // namespace

extern "C" int32_t refii_bridge_opinion_two_load(uint32_t component, uint8_t* out) {
  State& s = Tables();
  std::lock_guard lock(s.mutex);
  const auto found = s.sets.find(component);
  if (found == s.sets.end()) {
    return 0;
  }
  std::memcpy(out, found->second.data(), REFII_OPINION_BLOCK_BYTES);
  return 1;
}

extern "C" void refii_bridge_opinion_two_store(uint32_t component, const uint8_t* in) {
  State& s = Tables();
  std::lock_guard lock(s.mutex);
  std::memcpy(s.sets[component].data(), in, REFII_OPINION_BLOCK_BYTES);
}

extern "C" void refii_bridge_opinion_two_forget(uint32_t component) {
  State& s = Tables();
  std::lock_guard lock(s.mutex);
  s.sets.erase(component);
}
