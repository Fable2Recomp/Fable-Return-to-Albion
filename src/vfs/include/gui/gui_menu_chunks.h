// The menu contract at the C++ boundary: attributes are staged onto the "Parameters" element
// (`g_Param.element`), then a one-line `g_Menu:` / `g_MenuFormatter:` Lua chunk reads them.
// See docs/ui_native_cut.md.
//   sub_826B9510  string stager  r4 = attr name, r5 = value, r6 = presentation (0 = current)
//   sub_826B9440  float  stager  r4 = attr name, f1 = value, r6 = presentation
//   sub_82426470  the runner     r3 = chunk source, r4 = presentation
// `r3` is dead on both stagers; do not read it. Chunk text is carried verbatim because some
// chunks bake their argument into the source. Every record carries its presentation: the
// non-expandable menu's `menu/menuemphasisandghosting` redefines
// `SetItemEmphasis`/`SetItemGhosting` with `Float1` as a boolean.

#ifndef REFII_VFS_GUI_GUI_MENU_CHUNKS_H
#define REFII_VFS_GUI_GUI_MENU_CHUNKS_H

#include <cstdint>
#include <string>
#include <vector>

namespace refii::vfs {

/// One staged attribute, as the C++ handed it to the stager.
struct MenuChunkAttribute {
    std::string name;   // "String1", "Float3", …
    std::string text;   // the string value, empty for a float
    float number = 0.0f;
    bool is_number = false;
};

/// One `g_Menu:` / `g_MenuFormatter:` mutation: the chunk and what was staged for it.
struct MenuChunkCall {
    std::string chunk;                       // the Lua source, verbatim
    uint32_t presentation = 0;               // which menu it was dispatched to
    std::vector<MenuChunkAttribute> staged;
    uint64_t sequence = 0;
};

/// Records a staged attribute. Called from the two stagers.
void StageMenuAttribute(std::string name, std::string text, float number, bool is_number);

/// Records a chunk and consumes the staged bag; a leftover would attach to the next chunk.
void RunMenuChunk(std::string chunk, uint32_t presentation);

/// Takes every recorded call. Empties the queue.
std::vector<MenuChunkCall> TakeMenuChunkCalls();

/// Recorded, pending, and how many were dropped to the ceiling.
void MenuChunkCounters(uint64_t* out_recorded, uint32_t* out_pending, uint64_t* out_dropped);

}  // namespace refii::vfs

#endif  // REFII_VFS_GUI_GUI_MENU_CHUNKS_H
