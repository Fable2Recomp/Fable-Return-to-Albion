// refiii — which build of Fable 3 is running, and the values that differ between builds.
//
// TWO EXECUTABLES ARE SUPPORTED: the title-updated one (TU3, 0.0.3.3) and the disc one (0.0.0.3).
// The update is a full relink, so no guest address is shared. Functions are bound by NAME through
// the per-build ReXGlue config in Fable3/versions/ (Fable3TU3 and Fable3). Everything else a hook
// or the host bridge holds as a number goes through this header, as Fable 2's do through
// Fable2/src/vfs/include/core/guest_build.h.
//
// THE TU3 VALUE IS THE NAME. Source writes the TU3 address, wrapped:
//
//   LoadGuestU32(base, refii::guest::Address(0x83596F84u))     // an address we read at
//   switch (refii::guest::ToTu3(vtable)) { case 0x820B9A78u: } // a live value we classify
//   refii::guest::Pick(0x54u, 0x50u)                           // a non-address: TU3, disc
//
// so a grep for the address finds it in the TU3 IDB, and the disc value lives in one table,
// Fable3/versions/Fable3/guest_addresses.inc.
//
// THE PAYLOAD STATES THE BUILD. It is compiled per install from the player's own executable, so it
// knows which one that was (REFII_GUEST_BUILD_UNPATCHED, Fable3/src/hooks/src/guest_build_stamp.cpp)
// and says so when it loads; the host bridge, compiled once, asks. C linkage because the payload
// may import no C++ symbol from refiii_vfs.

#pragma once

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/// 0 = TU3 (also the answer before any payload has loaded), 1 = disc.
int32_t refiii_guest_build(void);
void refiii_guest_build_set(int32_t build);

/// The running build's address for a TU3 address. Unchanged under TU3.
uint32_t refiii_guest_address(uint32_t tu3_address);

/// The TU3 address for one read from the running guest. Unchanged under TU3.
uint32_t refiii_guest_address_to_tu3(uint32_t live_address);

#ifdef __cplusplus
}  // extern "C"

namespace refii::guest {

enum class Build : int32_t { kTu3 = 0, kDisc = 1 };

inline Build Current() { return static_cast<Build>(refiii_guest_build()); }

inline uint32_t Address(uint32_t tu3_address) { return refiii_guest_address(tu3_address); }

inline uint32_t ToTu3(uint32_t live_address) { return refiii_guest_address_to_tu3(live_address); }

/// A value that is not an address and differs between the builds.
template <typename T>
inline T Pick(T tu3, T disc) {
  return Current() == Build::kDisc ? disc : tu3;
}

/// A named guest address: declared once with its TU3 value, converts to the running build's on
/// every use. For constants that were `constexpr uint32_t kFoo = 0x8...;`.
class Addr {
 public:
  constexpr explicit Addr(uint32_t tu3_address) : tu3_(tu3_address) {}
  operator uint32_t() const { return refiii_guest_address(tu3_); }  // NOLINT: implicit by design
  constexpr uint32_t tu3() const { return tu3_; }

 private:
  uint32_t tu3_;
};

}  // namespace refii::guest
#endif
