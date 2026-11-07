// refii - what Fable 3's guest TELLS its headless presentations, for the shell
// (Fable3/src/hooks/src/headless_presentation_hooks.cpp, the shared headless core
// core/headless_presentation.h). The queues and the headless registry are the shared runtime's
// (core/told_statements.h); these are Fable 2's exports, export for export
// (Fable2/src/hostbridge/src/host_bridge.cpp), so the shell's one declaration binds either title.
//
// A headless presentation's create is told with the file it names and each element it makes with
// its parent's id and its name hash, so the shell resolves an element id against the presentation's
// .bgf (RefiiHostToldHeadlessFile / RefiiHostToldHeadlessElement). Fable 3's element writes reach
// Godot as statements on the UI cut's queue (`@Element.*`, `@Ui.*`), keyed by document and path, or
// by `#<file ordinal>` for a headless element no path named; the told queues carry what the shared
// core itself tells (TellAttribute / TellActive / TellTexture / TellText) — nothing in Fable 3's
// hooks calls those yet, so they stay empty.

#include <cstdint>

#include "core/told_statements.h"
#include "host_abi.h"

extern "C" {

/// The statements told to presentations' elements, oldest first, six words each (kind,
/// presentation, root hash or serial, ordinal or `0x80000000 | id`, a, b). Takes them.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostTakeToldStatements(uint32_t* out_words, uint32_t word_capacity) {
    return refii::vfs::TakeToldStatements(out_words, word_capacity);
}

/// The texts told to presentations' elements, oldest first: four words each (presentation, root
/// hash or serial, ordinal, UTF-16 length), the units back to back in `units`. Takes them.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostTakeToldTexts(uint32_t* out_words, uint32_t word_capacity, uint16_t* units,
                                                      uint32_t unit_capacity) {
    return refii::vfs::TakeToldTexts(out_words, word_capacity, units, unit_capacity);
}

/// A headless presentation's file as its create named it, NUL-terminated into `out`; the length,
/// 0 when `serial` is not the one at `presentation`.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostToldHeadlessFile(uint32_t presentation, uint32_t serial, char* out_path,
                                                         uint32_t capacity) {
    return refii::vfs::ToldHeadlessFile(presentation, serial, out_path, capacity);
}

/// One element of a headless presentation by its id: its parent's id (0 the root) and its name
/// hash. 1 when known, else 0.
REFII_HOST_ABI_EXPORT int32_t RefiiHostToldHeadlessElement(uint32_t presentation, uint32_t serial, uint32_t id,
                                                           uint32_t* out_parent_id, uint32_t* out_name_hash) {
    return refii::vfs::ToldHeadlessElement(presentation, serial, id, out_parent_id, out_name_hash) ? 1 : 0;
}

}  // extern "C"
