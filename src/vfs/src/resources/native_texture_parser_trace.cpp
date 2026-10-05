#include "vfs_hooks_private.h"
#include "core/guest_build.h"

namespace refii::vfs {
inline namespace detail {
std::string FormatPostVideoFrameTeardownState(uint8_t* base,
                                              uint32_t object_guest) {
  const uint32_t owner_candidate =
      object_guest >= 12 ? object_guest - 12 : 0;
  return fmt::format("object=0x{:08X} object_words={} object_vtable={} "
                     "owner_candidate=0x{:08X} owner_words={}",
                     object_guest,
                     FormatGuestWordsIfPointer(base, object_guest, 10),
                     FormatGuestObjectVTableSlots(base, object_guest, 8),
                     owner_candidate,
                     FormatGuestWordsIfPointer(base, owner_candidate, 12));
}

bool IsParserBoundaryTextureWrapperCaller(uint32_t lr) {
  return lr == refii::guest::Address(0x82B6D690u) || lr == refii::guest::Address(0x82B6C374u) || lr == refii::guest::Address(0x82B6C390u);
}
}  // namespace detail
}  // namespace refii::vfs
