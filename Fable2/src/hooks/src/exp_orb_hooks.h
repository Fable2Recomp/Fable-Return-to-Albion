#pragma once

#include <cstdint>

namespace refii::hooks {

/// Repairs Fable 2 experience orbs that drop below valid terrain sectors (e.g. Z = -999.47) or
/// stall against cemetery/dungeon obstacles and the hero's capsule while RT sucking is active.
void FixupExperienceOrb(uint8_t* base, uint32_t target_hero, uint32_t orb);

}  // namespace refii::hooks
