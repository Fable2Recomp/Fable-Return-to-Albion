// Which display box is SHOWING — the one the guest's input router hands events to.
//
// The box owner `*(*(GUI+28)+52)` holds two queues `{vt 0x820CEF3C, ?, begin, end, cap, current}`:
// one at +4 (current at +24) and one at +0x20 (current at +52). The router `sub_82403350` picks:
//
//   if ( !owner[6] && (!owner[3] || !((owner[4] - owner[3]) >> 3)) )  box = owner[13];
//   else                                                              box = owner[6];
//
// so the +4 queue wins whenever its current is set or it holds anything, and only an idle +4
// queue falls back to +52. `GUI.DisplayMessageBox` and the numeric adjuster sit in +4; a reader
// of +52 alone misses them.

#ifndef REFII_VFS_GUI_GUI_DISPLAY_BOX_H
#define REFII_VFS_GUI_GUI_DISPLAY_BOX_H

#include <cstdint>

namespace refii::vfs {

/// The showing display box under `owner`, 0 when none. `read32` reads one guest word.
template <typename ReadWord>
uint32_t ShowingDisplayBox(uint32_t owner, ReadWord read32) {
  const uint32_t current = read32(owner + 24u);
  const uint32_t begin = read32(owner + 12u);
  const uint32_t end = read32(owner + 16u);
  const bool queued = begin != 0u && static_cast<int32_t>(end - begin) >> 3 != 0;
  return current != 0u || queued ? current : read32(owner + 52u);
}

}  // namespace refii::vfs

#endif  // REFII_VFS_GUI_GUI_DISPLAY_BOX_H
