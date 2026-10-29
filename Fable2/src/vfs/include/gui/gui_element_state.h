// Element state change feed, taken at the writers.
//   sub_82284798(attrSvc, element, u32 id27, u32 valueBits)  attributes (vtable 0x820065C0
//                 slot +0x48); the guest's own dirty list (`presentation+68` data / `+76`
//                 count) is refilled during the frame, so it is not readable off-thread
//   sub_82284BE8  element FLAGS — 0x1000 the draw gate, 0x2000 active
//   sub_82BB5760  SLIDE — anim record +43 current, +44 previous
//   sub_82425DD0  TEXT — the new text is not an argument, so this is a notification
//   sub_82425C58  COLOUR — not hooked; the colour channel reads `diffuse.r/g/b` attributes,
//                 which this writer bypasses
// Key on (presentation, name hash): `elem+0` is the name hash and presentation addresses
// recycle.

#ifndef REFII_VFS_GUI_GUI_ELEMENT_STATE_H
#define REFII_VFS_GUI_GUI_ELEMENT_STATE_H

#include <cstdint>
#include <vector>

#include "core/payload_bridge_fable2.h"

namespace refii::vfs {

/// One state change, as its writer stated it.
struct GuiElementChange {
    enum class Kind : uint8_t {
        kAttribute = 0,  ///< `id` is the 27-bit attribute id, `value` its raw bits.
        kFlags = 1,      ///< `id` is the 16-bit mask, `value` 1 to set and 0 to clear.
        kSlide = 2,      ///< `value` is the new slide index.
        kText = 3,       ///< the element's TEXT changed; neither `id` nor `value` carries it.
    };

    uint32_t presentation = 0;
    uint32_t element = 0;    ///< the raw pointer, for the frame it is valid in
    uint32_t name_hash = 0;  ///< `elem+0` — the durable half of the key
    uint32_t id = 0;
    uint32_t value = 0;
    Kind kind = Kind::kAttribute;
};

/// Records one change. Called from the writers, on the guest's own thread.
inline void NoteGuiElementChange(GuiElementChange change) {
  refii_bridge_note_gui_element_change(&change);
}

/// Takes every recorded change. Empties the queue.
std::vector<GuiElementChange> TakeGuiElementChanges();

/// Recorded, pending and dropped; a non-zero `dropped` means a frame was truncated.
void GuiElementCounters(uint64_t* out_recorded, uint32_t* out_pending, uint64_t* out_dropped);

}  // namespace refii::vfs

#endif  // REFII_VFS_GUI_GUI_ELEMENT_STATE_H
