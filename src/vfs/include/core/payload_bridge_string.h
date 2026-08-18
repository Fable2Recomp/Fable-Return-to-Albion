// Caller-buffer helpers for the payload/host C ABI.
//
// The bridge never returns a string, because a std::string allocated on one side and freed
// on the other corrupts once the two sides are built by different compilers. Every entry
// that produces text writes into a caller buffer and returns the length it NEEDED, whether
// or not that fitted. These helpers turn that contract back into a std::string on the
// caller's own heap.

#pragma once

#include <cstddef>
#include <string>
#include <utility>

namespace refii::bridge {

/// One stack attempt, then one exact-sized heap attempt if it did not fit.
///
/// 512 bytes covers every asset key and almost every host path, so the common case makes a
/// single call and no allocation. `fill` is invoked as fill(char*, size_t) -> size_t.
template <typename Fill>
inline std::string TakeString(Fill&& fill) {
  char stack[512];
  size_t needed = fill(stack, sizeof stack);
  if (needed <= sizeof stack) {
    return std::string(stack, needed);
  }

  std::string out(needed, '\0');
  size_t written = fill(out.data(), out.size());
  out.resize(written < out.size() ? written : out.size());
  return out;
}

}  // namespace refii::bridge
