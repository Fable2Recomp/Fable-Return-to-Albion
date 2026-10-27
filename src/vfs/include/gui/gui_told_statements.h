// What the guest TELLS a presentation's elements, posted to the host as statements — see
// hooks/gui_slide_hooks.cpp. The element writers themselves are hooked in
// hooks/gui_element_hooks.cpp, which calls the Tell* functions (payload side) around the
// originals; they push through the C ABI (`refii_bridge_told_push`, payload_bridge.h) into the
// queues gui/gui_told_statements.cpp keeps in refii_vfs, which the bridge drains with Take*.
#pragma once

#include <cstdint>

namespace refii::vfs {

/// One statement the guest made to an element, keyed `(presentation, root name hash, ordinal)`.
struct ToldStatement {
    enum class Kind : uint32_t {
        kGotoSlide = 1,  // a = slide
        kPaused = 2,     // a = paused
        kAttribute = 3,  // a = attribute id (27 bits), b = value bits
        kActive = 4,     // a = on
        kTexture = 5,    // a = resource hash the element's quad now shows
    };

    Kind kind = Kind::kGotoSlide;
    uint32_t presentation = 0;
    uint32_t root_hash = 0;
    uint32_t ordinal = 0;
    uint32_t a = 0;
    uint32_t b = 0;
};

/// HOST BRIDGE side (gui/gui_told_statements.cpp, in refii_vfs): takes the queued statements, oldest first, six words each (kind, presentation, root hash,
/// ordinal, a, b); returns how many were written.
uint32_t TakeToldStatements(uint32_t* words, uint32_t capacity);

/// Around GotoSlide (`sub_82BB5760`): its own slide-entry writes are not statements.
void TellGotoSlideBegin();
void TellGotoSlideEnd(uint8_t* base, uint32_t timeline, uint32_t element, uint32_t slide, bool stated);

/// After the attribute writer (`sub_82284798`), unless a track or a slide entry made the write.
void TellAttribute(uint8_t* base, uint32_t store, uint32_t element, uint32_t attribute, uint32_t value);

/// After a text writer (`sub_82425DD0` SetText / `sub_82425F40` SetTextFromTag): the element's live
/// string, read back from its text record once the writer has stored it.
void TellText(uint8_t* base, uint32_t element);

/// After one of the front end's texture writers returns (they store `**(element+4)+0x200` inline,
/// 60+ sites game-wide, no setter to hook): every element of the front end's presentation whose
/// stamped texture changed since the last call, queued as `kTexture`.
void TellFrontEndTextures(uint8_t* base);

/// Takes the queued texts, oldest first: four words each (presentation, root name hash, ordinal,
/// UTF-16 length) into `words`, the units back to back into `units`; returns how many.
uint32_t TakeToldTexts(uint32_t* words, uint32_t word_capacity, uint16_t* units, uint32_t unit_capacity);

/// After the flags writer (`sub_82284BE8`): only `active` (0x2000) is a statement.
void TellFlags(uint8_t* base, uint32_t store, uint32_t element, uint32_t mask, bool on);

}  // namespace refii::vfs
