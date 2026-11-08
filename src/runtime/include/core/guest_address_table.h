// refii — a title's guest address table between its two supported executables.
//
// A title update is a full relink, so a title with one supported executable per build names every
// address in its source in ONE of them (the "named" build, the title-updated one) and keeps the
// other build's address for each in a table of { named, other } rows sorted by the named address.
// This is the lookup both ways; each title's vfs owns its table and the running build
// (Fable2/src/vfs/src/core/guest_build.cpp, Fable3/src/vfs/src/guest_build.cpp).
//
// An address with no row is returned unchanged, which is only right for a value that is not an
// address at all.

#pragma once

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>

namespace refii::guest {

struct AddressRow {
  uint32_t named;
  uint32_t other;
};

template <size_t N>
class AddressTable {
 public:
  explicit AddressTable(const AddressRow (&rows)[N]) {
    std::copy(std::begin(rows), std::end(rows), by_named_.begin());
    by_other_ = by_named_;
    std::sort(by_other_.begin(), by_other_.end(),
              [](const AddressRow& a, const AddressRow& b) { return a.other < b.other; });
  }

  /// The other build's address for an address the source names.
  uint32_t ToOther(uint32_t named) const {
    const auto row = std::lower_bound(
        by_named_.begin(), by_named_.end(), named,
        [](const AddressRow& candidate, uint32_t value) { return candidate.named < value; });
    return row != by_named_.end() && row->named == named ? row->other : named;
  }

  /// The named address for one read from a guest running the other build.
  uint32_t ToNamed(uint32_t other) const {
    const auto row = std::lower_bound(
        by_other_.begin(), by_other_.end(), other,
        [](const AddressRow& candidate, uint32_t value) { return candidate.other < value; });
    return row != by_other_.end() && row->other == other ? row->named : other;
  }

 private:
  std::array<AddressRow, N> by_named_{};
  std::array<AddressRow, N> by_other_{};
};

}  // namespace refii::guest
