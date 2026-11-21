// What the guest TELLS a presentation's elements, posted to the host as statements — see
// hooks/gui_slide_hooks.cpp. The element writers themselves are hooked in
// hooks/gui_element_hooks.cpp, which calls the Tell* functions (payload side) around the
// originals; they push through the C ABI (`refii_bridge_told_push`, core/payload_bridge.h) into
// the shared queues (src/runtime/src/core/told_statements.cpp), which the bridge drains with the
// Take* functions core/told_statements.h declares.
#pragma once

#include <cstdint>

#include "core/told_statements.h"

namespace refii::vfs {

/// PAYLOAD side: a headless presentation's element key (the shared headless core's), or false
/// when `element` is not one.
bool HeadlessElementKey(uint32_t element, uint32_t* presentation, uint32_t* serial, uint32_t* id);

/// Around GotoSlide (`sub_82BB5760`): its own slide-entry writes are not statements.
void TellGotoSlideBegin();
void TellGotoSlideEnd(uint8_t* base, uint32_t timeline, uint32_t element, uint32_t slide, bool stated);

/// After the attribute writer (`sub_82284798`), unless a track or a slide entry made the write.
void TellAttribute(uint8_t* base, uint32_t store, uint32_t element, uint32_t attribute, uint32_t value);

/// After the flags writer (`sub_82284BE8`): only `active` (0x2000) is a statement.
void TellFlags(uint8_t* base, uint32_t store, uint32_t element, uint32_t mask, bool on);

}  // namespace refii::vfs
