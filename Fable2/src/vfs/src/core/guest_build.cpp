#include "core/guest_build.h"

#include <atomic>

#include "core/guest_address_table.h"

namespace {

// Sorted by TU1 address: { TU1, unpatched }.
constexpr refii::guest::AddressRow kUnpatched[] = {
#include "../../../../versions/Fable2/guest_addresses.inc"
};

const refii::guest::AddressTable<std::size(kUnpatched)>& Table() {
  static const refii::guest::AddressTable<std::size(kUnpatched)> table(kUnpatched);
  return table;
}

std::atomic<int32_t> g_build{0};

}  // namespace

extern "C" {

int32_t refii_guest_build(void) { return g_build.load(std::memory_order_relaxed); }

void refii_guest_build_set(int32_t build) { g_build.store(build, std::memory_order_relaxed); }

uint32_t refii_guest_address(uint32_t tu1_address) {
  return g_build.load(std::memory_order_relaxed) == 0 ? tu1_address : Table().ToOther(tu1_address);
}

uint32_t refii_guest_address_to_tu1(uint32_t live_address) {
  return g_build.load(std::memory_order_relaxed) == 0 ? live_address : Table().ToNamed(live_address);
}

}  // extern "C"
