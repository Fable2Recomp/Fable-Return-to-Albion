#include "core/guest_build.h"

#include <algorithm>
#include <atomic>
#include <iterator>

namespace {

struct Row {
  uint32_t tu1;
  uint32_t unpatched;
};

// Sorted by TU1 address.
constexpr Row kUnpatched[] = {
#include "../../../../versions/Fable2/guest_addresses.inc"
};

std::atomic<int32_t> g_build{0};

// The same rows by unpatched address, for classifying a value read from the guest.
const Row* ByUnpatched(uint32_t live_address) {
  static const auto sorted = [] {
    struct Sorted {
      Row rows[std::size(kUnpatched)];
    } out{};
    std::copy(std::begin(kUnpatched), std::end(kUnpatched), out.rows);
    std::sort(std::begin(out.rows), std::end(out.rows),
              [](const Row& a, const Row& b) { return a.unpatched < b.unpatched; });
    return out;
  }();
  const Row* end = std::end(sorted.rows);
  const Row* row = std::lower_bound(
      std::begin(sorted.rows), end, live_address,
      [](const Row& candidate, uint32_t value) { return candidate.unpatched < value; });
  return row != end && row->unpatched == live_address ? row : nullptr;
}

}  // namespace

extern "C" {

int32_t refii_guest_build(void) { return g_build.load(std::memory_order_relaxed); }

void refii_guest_build_set(int32_t build) { g_build.store(build, std::memory_order_relaxed); }

uint32_t refii_guest_address(uint32_t tu1_address) {
  if (g_build.load(std::memory_order_relaxed) == 0) {
    return tu1_address;
  }
  const Row* end = std::end(kUnpatched);
  const Row* row = std::lower_bound(
      std::begin(kUnpatched), end, tu1_address,
      [](const Row& candidate, uint32_t value) { return candidate.tu1 < value; });
  return row != end && row->tu1 == tu1_address ? row->unpatched : tu1_address;
}

uint32_t refii_guest_address_to_tu1(uint32_t live_address) {
  if (g_build.load(std::memory_order_relaxed) == 0) {
    return live_address;
  }
  const Row* row = ByUnpatched(live_address);
  return row != nullptr ? row->tu1 : live_address;
}

}  // extern "C"
