#include "fable3_guest_build.h"

#include <atomic>

#include "core/guest_address_table.h"

namespace {

// Sorted by TU3 address: { TU3, disc }.
constexpr refii::guest::AddressRow kDisc[] = {
#include "../../../versions/Fable3/guest_addresses.inc"
};

const refii::guest::AddressTable<std::size(kDisc)>& Table() {
  static const refii::guest::AddressTable<std::size(kDisc)> table(kDisc);
  return table;
}

std::atomic<int32_t> g_build{0};

}  // namespace

extern "C" {

int32_t refiii_guest_build(void) { return g_build.load(std::memory_order_relaxed); }

void refiii_guest_build_set(int32_t build) { g_build.store(build, std::memory_order_relaxed); }

uint32_t refiii_guest_address(uint32_t tu3_address) {
  return g_build.load(std::memory_order_relaxed) == 0 ? tu3_address : Table().ToOther(tu3_address);
}

uint32_t refiii_guest_address_to_tu3(uint32_t live_address) {
  return g_build.load(std::memory_order_relaxed) == 0 ? live_address : Table().ToNamed(live_address);
}

}  // extern "C"
