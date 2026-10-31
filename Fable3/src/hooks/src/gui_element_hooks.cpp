// refii - Fable 3's Gameface elements, as the guest STATES them: every menu, box and HUD screen in
// Fable 3 is written by C++ through one small element API — resolve a dotted path to an element,
// then set its state, its opacity or its text. There is no `g_Menu:` chunk mechanism as in Fable 2
// (the chunk runner sub_82819D40 serves only the HUD lifecycle, the mini-game bar and the display
// box); the rows of the options, hero status, stats, quest and interaction screens are element
// writes. Hooking the resolver and the four setters therefore names and values every element the
// guest shows, for Godot's Fable3Elements, posted as `@Element.*` pseudo-calls on the UI cut's
// queue (core/gui_native_calls.h).
//
// THE RESOLVER sub_82819E40(CCharString* path, parent, presentation) walks `A.B.C` one segment per
// recursive call (`sub_8226E420(path, ".")` at 0x82819EC8), starting at the presentation's "Layer"
// root when the parent is 0 (0x82819E80), and ends in the renderer's lookup
// `vt[84](renderer, sdbmHash(segment), parent)` (0x8281A020; the hash is sub_825C01B8,
// `h = 65599*h + c`). It consumes the path handle (sub_8221F208 at 0x8281A028), so the path is
// read on ENTRY and the element taken from r3 on EXIT; a child's full path is its parent's plus the
// segment. Every wrapper resolves through it: sub_8281A038 (getElement), sub_82819070 (text box),
// sub_8281A428 (state), sub_82819328 (text tag), sub_8281AD48 / sub_8281A5D8 (floats).
//
// THE SETTERS. sub_8227DA00(elem, pres, state) -> `vt[8](states, elem, state, 1)` (0x8227DA8C);
// sub_82819420(elem, pres, f1 opacity) -> the "opacity" attribute (dword_8355EE48) through vt[72];
// text: a text element's box is `*(*(elem+4))` (0x828192A8), whose shown string is the LHString
// handle at box+0x88 — sub_82B45268(box, &string) compares (sub_8229B520 at 0x82B45290) and
// assigns it (sub_8226EE58 at 0x82B452A4), marking +0x84/+0x86 dirty; sub_82819288(elem, tagId)
// resolves the tag through the text manager (sub_8230DCA8 at 0x828192D4) and assigns the same
// field inline (0x828192FC). Both are read AFTER they return, from box+0x88: rep +0 = UTF-16BE
// characters, +4 = length in code units (as the HUD registry's strings, hud_feed.cpp).
//
// Statements, not samples: each (element, kind) posts only when its value changes — the HUD update
// sub_822597B0 restates two states every frame (0x82259978, 0x82259990).

#include <cstdint>
#include <cstring>
#include <mutex>
#include <string>
#include <unordered_map>

#include <rex/ppc.h>

#include "gui_statements.h"

REX_EXTERN(__imp__rex_GuiElement_Resolve);
REX_EXTERN(__imp__rex_GuiElement_SetState);
REX_EXTERN(__imp__rex_GuiElement_SetOpacity);
REX_EXTERN(__imp__rex_GuiElement_SetTextTag);
REX_EXTERN(__imp__rex_GuiTextBox_Commit);

namespace {

constexpr uint32_t kElementTextRef = 4u;    // elem+4 -> pointer -> *that = the text box
constexpr uint32_t kTextBoxString = 0x88u;  // the LHString handle shown
constexpr uint32_t kMaxPathChars = 160u;
constexpr uint32_t kMaxTextUnits = 1024u;
constexpr size_t kMaxCached = 8192u;

using namespace refii::fable3;

struct Known {
  uint32_t presentation = 0;
  std::string path;
};

std::mutex g_mutex;
std::unordered_map<uint32_t, Known> g_elements;   // element -> its presentation and full path
std::unordered_map<uint32_t, uint32_t> g_boxes;   // text box -> element
std::unordered_map<uint64_t, uint32_t> g_states;  // (element, kind) -> last value posted
std::unordered_map<uint32_t, std::string> g_texts;

constexpr uint32_t kKindState = 1u;
constexpr uint32_t kKindOpacity = 2u;

uint64_t Key(uint32_t element, uint32_t kind) { return (static_cast<uint64_t>(element) << 32) | kind; }

/// The element's recorded presentation and path; false when the resolver never named it.
bool Lookup(uint32_t element, Known& out) {
  auto found = g_elements.find(element);
  if (found == g_elements.end()) {
    return false;
  }
  out = found->second;
  return true;
}

/// `@Element.<kind>(presentation hex, element hex, path, value)`.
void Post(const char* name, uint32_t presentation, uint32_t element, const std::string& path,
          refii::vfs::GuiNativeArgument value) {
  refii::vfs::GuiNativeCall call;
  call.name = name;
  call.arguments.push_back(Text(Hex(presentation)));
  call.arguments.push_back(Text(Hex(element)));
  call.arguments.push_back(Text(path));
  call.arguments.push_back(std::move(value));
  refii::vfs::PostGuiNativeCall(std::move(call));
}

void PostValue(const uint8_t* base, uint32_t element, uint32_t presentation, uint32_t kind, uint32_t bits,
               const char* name, float value) {
  (void)base;
  if (!Plausible(element)) {
    return;
  }
  Known known;
  std::lock_guard<std::mutex> lock(g_mutex);
  uint32_t& last = g_states[Key(element, kind)];
  if (last != 0u && last == bits + 1u) {
    return;
  }
  last = bits + 1u;  // 0 is "never posted"
  if (g_states.size() > kMaxCached) {
    g_states.clear();
  }
  const bool named = Lookup(element, known);
  Post(name, named ? known.presentation : presentation, element, named ? known.path : std::string(),
       Number(value));
}

/// The text the box shows now, posted when it changed.
void PostBoxText(const uint8_t* base, uint32_t box) {
  if (!Plausible(box)) {
    return;
  }
  std::string text = ReadLhString(base, box + kTextBoxString);
  std::lock_guard<std::mutex> lock(g_mutex);
  auto last = g_texts.find(box);
  if (last != g_texts.end() && last->second == text) {
    return;
  }
  if (g_texts.size() > kMaxCached) {
    g_texts.clear();
  }
  g_texts[box] = text;

  uint32_t element = 0u;
  auto owner = g_boxes.find(box);
  if (owner != g_boxes.end()) {
    element = owner->second;
  }
  Known known;
  const bool named = element != 0u && Lookup(element, known);
  Post("@Element.Text", named ? known.presentation : 0u, element, named ? known.path : std::string(),
       Text(text));
}

}  // namespace

/// The resolver: the path on entry, the element on exit.
REX_EXTERN(rex_GuiElement_Resolve) {
  const uint32_t path_handle = ctx.r3.u32;
  const uint32_t parent = ctx.r4.u32;
  const uint32_t presentation = ctx.r5.u32;
  const std::string segment = base != nullptr ? ReadCharString(base, path_handle) : std::string();

  __imp__rex_GuiElement_Resolve(ctx, base);

  const uint32_t element = ctx.r3.u32;
  if (base == nullptr || !Plausible(element) || segment.empty()) {
    return;
  }

  std::lock_guard<std::mutex> lock(g_mutex);
  if (g_elements.size() > kMaxCached) {
    g_elements.clear();
    g_boxes.clear();
  }
  Known known;
  known.presentation = presentation;
  Known parent_known;
  known.path = parent != 0u && Lookup(parent, parent_known) && !parent_known.path.empty()
                   ? parent_known.path + "." + segment
                   : segment;
  g_elements[element] = known;

  // The element's text box, so a commit on the box names the element it belongs to.
  const uint32_t ref = LoadGuestU32(base, element + kElementTextRef);
  if (Plausible(ref)) {
    const uint32_t box = LoadGuestU32(base, ref);
    if (Plausible(box)) {
      g_boxes[box] = element;
    }
  }
}

REX_EXTERN(rex_GuiElement_SetState) {
  const uint32_t element = ctx.r3.u32;
  const uint32_t presentation = ctx.r4.u32;
  const uint32_t state = ctx.r5.u32;
  __imp__rex_GuiElement_SetState(ctx, base);
  if (base != nullptr && presentation != 0u) {
    PostValue(base, element, presentation, kKindState, state, "@Element.State", static_cast<float>(static_cast<int32_t>(state)));
  }
}

REX_EXTERN(rex_GuiElement_SetOpacity) {
  const uint32_t element = ctx.r3.u32;
  const uint32_t presentation = ctx.r4.u32;
  const float opacity = static_cast<float>(ctx.f1.f64);
  __imp__rex_GuiElement_SetOpacity(ctx, base);
  if (base != nullptr && presentation != 0u) {
    uint32_t bits = 0u;
    std::memcpy(&bits, &opacity, sizeof(bits));
    PostValue(base, element, presentation, kKindOpacity, bits, "@Element.Opacity", opacity);
  }
}

/// A literal string committed to a text box: `sub_82B45268(box, &string)`.
REX_EXTERN(rex_GuiTextBox_Commit) {
  const uint32_t box = ctx.r3.u32;
  __imp__rex_GuiTextBox_Commit(ctx, base);
  if (base != nullptr) {
    PostBoxText(base, box);
  }
}

/// A text tag resolved and assigned to an element's box: `sub_82819288(elem, tagId)`.
REX_EXTERN(rex_GuiElement_SetTextTag) {
  const uint32_t element = ctx.r3.u32;
  __imp__rex_GuiElement_SetTextTag(ctx, base);
  if (base == nullptr || !Plausible(element)) {
    return;
  }
  const uint32_t ref = LoadGuestU32(base, element + kElementTextRef);
  if (!Plausible(ref)) {
    return;
  }
  const uint32_t box = LoadGuestU32(base, ref);
  {
    std::lock_guard<std::mutex> lock(g_mutex);
    if (Plausible(box) && g_boxes.find(box) == g_boxes.end()) {
      g_boxes[box] = element;
    }
  }
  PostBoxText(base, box);
}
