// refii — PROPERTY CONDITION: Fable 3's numbers and the one place they live, for the payload's
// hooks (hooks/src/property_hooks.cpp) and the host bridge's property list alike.
//
// A building's condition is 0..1, a saved value of the world in player one's store (the host owns
// it) under `refii.condition.<uid>`; one never seen stands at 1. Fable 3's tuning (globals,
// RealEstateGlobals): BuildingConditionDeteriorationPerRentCollection 0.01 per rent drop, rented
// homes only; BuildingValueMultiplierAtMinCondition 0.5 (value × (0.5 + 0.5c), `sub_82894AB0`);
// RepairBillMultiplier 0.5 on (full − current) value (`sub_82896868`); rent 0 at 0.

#pragma once

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <string>

namespace refii::property {

inline constexpr float kDecayPerDrop = 0.01f;
inline constexpr float kValueAtNoCondition = 0.5f;
inline constexpr float kRepairBill = 0.5f;

inline std::string ConditionKey(uint64_t uid) {
  char key[48];
  std::snprintf(key, sizeof(key), "refii.condition.%016llX", static_cast<unsigned long long>(uid));
  return key;
}

/// The value multiplier at `condition`.
inline float ValueScale(float condition) {
  return kValueAtNoCondition + (1.0f - kValueAtNoCondition) * std::clamp(condition, 0.0f, 1.0f);
}

/// A full repair's bill on a building worth `value` at full condition.
inline int32_t RepairCost(int32_t value, float condition) {
  const float full = static_cast<float>(std::max(value, 0));
  return static_cast<int32_t>(std::lround((full - full * ValueScale(condition)) * kRepairBill));
}

}  // namespace refii::property
