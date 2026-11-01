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
// sub_82819420(elem, pres, f1 opacity) -> the "opacity" attribute (dword_8355EE48) through vt[72]
// — also the visibility toggle sub_82819AE0(elem, pres, shown), which scales the opacity by 1024 or
// 1/1024 across the 0.09765625 threshold (flt_832A18DC, 0x82819B38..0x82819B68);
// sub_8281A5D8(elem, pres, f1) -> `pres->vt[9]()->vt[11](timeline, elem, (int)f1)` (fctiwz at
// 0x8281A61C, call at 0x8281A634): the element's timeline position, which the meters drive through
// sub_8281AD48(path, pres, fraction) as `fraction * 1000` (0x8281ADA4) — the hero status bars
// (sub_82975D88 at 0x82975F10 / 0x8297608C / 0x829760A0) and the options sliders (sub_825CDBD0);
// text: a text element's box is `*(*(elem+4))` (0x828192A8), whose shown string is the LHString
// handle at box+0x88 — sub_82B45268(box, &string) compares (sub_8229B520 at 0x82B45290) and
// assigns it (sub_8226EE58 at 0x82B452A4), marking +0x84/+0x86 dirty; sub_82819288(elem, tagId)
// resolves the tag through the text manager (sub_8230DCA8 at 0x828192D4) and assigns the same
// field inline (0x828192FC); sub_828191A8(elem, &tagName) resolves a tag NAME (sub_823A9728 at
// 0x828191E0) and assigns it inline too (0x82819204) — the promise's button labels (sub_827A3398 at
// 0x827A34D0 / 0x827A3508) and sub_82819230's six callers. All are read AFTER they return, from box+0x88: rep +0 = UTF-16BE
// characters, +4 = length in code units (as the HUD registry's strings, hud_feed.cpp).
//
// Statements, not samples: each (element, kind) posts only when its value changes — the HUD update
// sub_822597B0 restates two states every frame (0x82259978, 0x82259990).
//
// THE REGISTRY ELEMENTS NEVER PASS THE RESOLVER. The HUD's registry builder sub_82675288 walks the
// whole element tree (sub_82675848 recurses the children from sub_8281A138) and hands every element
// to each of fourteen registries' filer, ONE shared body sub_82B9A108(registry, element, pres): it
// asks the registry its attribute name (vt[4], 0x82B9A134), reads that attribute off the element
// as a string — sub_828183C8(out, element, pres, &name) at 0x82B9A148 — and, when non-empty, files
// the element under the string's FNV-1 (0x82B9A1B0..0x82B9A1E8). The HUD update then writes those
// elements straight from the records (0x82259978: `sub_8227DA00(*(*entry+4), pres, 2)`), as do the
// TopBox toggles and the pooled world-icon elements (sub_82B51B58 at 0x82B51F28). Hooking the
// filer and the getter names them `<Attribute>:<value>` — the registry's own key, which is what
// the guest addresses them by. A dotted path, when one is later resolved, replaces it.
//
// THE INTERACTION TEXT BOX is committed by a path the resolver saw but a box it had not: the
// text-box wrapper sub_82BEF298 keeps the paths (+36 root, +44 `.Text` child) and its commit
// sub_82BEF428 resolves the child (0x82BEF5B4) and then commits `*(*(elem+4))` (0x82BEF770) — a
// box that was still null when the element was first resolved (0x82BEF5F4..0x82BEF608 guard it).
// A commit on an unknown box therefore looks the box up through the elements' own `+4` slots.
//
// THE POSITION SETTER sub_82818580(elem, pres, f1 x, f2 y, f3 z) writes the element's own
// `position.x/.y/.z` attributes — ids dword_8355EE4C/50/54, sdbm("position.x") & 0x07FFFFFF (the
// initialiser at 0x831F7FD4..0x831F7FE0) — through the attribute store `pres->vt[8]()->vt[18]`
// (0x828185EC check, 0x8281861C / 0x82818644 / 0x8281866C), so the values are in the .bgf's own
// units: the element's LOCAL position, as the file authors it. Its writers are the box layout
// sub_82920610, which measures a guest-laid-out text box (`BoxName`) and places each line element
// (0x829207DC), and the HUD-element icon placement sub_82B51B58 (0x82B51FC8, the co-op off-screen
// markers clamped to the screen edge). Posted as `@Element.Position(pres, elem, path, x, y, z)`.
//
// THE TEXTURE SETTER sub_82818EF0(&path, elem) hands the CCharString path to the element's
// renderable `*(*(elem+4))` (sub_82B42970 at 0x82818F1C), which hashes it (sub_8312F3D8 at
// 0x82B429EC) into its texture slot +512 (0x82B42A00). The interaction buttons take their glyph
// this way: sub_82961340 sets `Icon.Container.A_Text` to the button's `…\<b>_text.tex`
// (0x82961464) and sub_829614A0 maps it to the matching `…\<b>_orb.tex` on `Icon.Container.A_Orb`
// (0x82961644). Posted as `@Element.Texture(pres, elem, path, texturePath)`.
//
// THE SCREEN ROOT is written by the screen tick itself: sub_825C3A78 sets the state of
// `sub_82819DB8(pres)` — the renderer's root element, `pres->vt[8]()->vt[17]()` (0x82819E08 /
// 0x82819E1C) — to 1 showing / 2 hidden (0x825C3D10, 0x825C3D5C, 0x825C3D8C, 0x825C3EC8). The
// resolver names its "Layer" child by passing that root as the parent (0x82819E70..0x82819E90), so
// the root is named `Scene` (the chunk runner's own `getElement('Scene.Layer')`) and contributes no
// prefix: paths stay `Layer.…` as the census has them.

#include <cstdint>
#include <array>
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
REX_EXTERN(__imp__rex_GuiRegistry_File);
REX_EXTERN(__imp__rex_GuiElement_AttributeString);
REX_EXTERN(__imp__rex_GuiPresentation_Root);
REX_EXTERN(__imp__rex_GuiElement_SetTime);
REX_EXTERN(__imp__rex_GuiElement_SetTextName);
REX_EXTERN(__imp__rex_GuiElement_SetPosition);
REX_EXTERN(__imp__rex_GuiElement_SetTexture);

namespace {

constexpr uint32_t kElementTextRef = 4u;    // elem+4 -> pointer -> *that = the text box
constexpr uint32_t kTextBoxString = 0x88u;  // the LHString handle shown
constexpr uint32_t kMaxPathChars = 160u;
constexpr uint32_t kMaxTextUnits = 1024u;
/// A session resolves a few thousand element paths across its screens (each Options open resolves
/// its rows anew); the HUD's names must survive all of them, so this is a last-resort bound, not
/// a working size.
constexpr size_t kMaxCached = 65536u;
constexpr size_t kMaxValues = 16384u;

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
std::unordered_map<uint32_t, std::array<uint32_t, 3>> g_positions;  // element -> last x, y, z bits
std::unordered_map<uint32_t, std::string> g_textures;                // element -> last texture path

constexpr uint32_t kKindState = 1u;
constexpr uint32_t kKindOpacity = 2u;
constexpr uint32_t kKindTime = 3u;

/// The presentation root's name; it prefixes nothing.
const char kRootPath[] = "Scene";

/// The element the registry filer sub_82B9A108 is filing on this thread, and the presentation:
/// the attribute getter's hook records the string only for that element.
struct Filing {
  uint32_t element = 0;
  uint32_t presentation = 0;
};
thread_local Filing t_filing;

uint64_t Key(uint32_t element, uint32_t kind) { return (static_cast<uint64_t>(element) << 32) | kind; }

/// The text box an element owns now (`*(*(elem+4))`), or 0.
uint32_t BoxOf(const uint8_t* base, uint32_t element) {
  const uint32_t ref = LoadGuestU32(base, element + kElementTextRef);
  if (!Plausible(ref)) {
    return 0u;
  }
  const uint32_t box = LoadGuestU32(base, ref);
  return Plausible(box) ? box : 0u;
}

/// Names a box's owner after the fact: the element whose `+4` slot holds the box now. Walks the
/// known elements once per unknown box and caches the answer. Caller holds g_mutex.
uint32_t FindBoxOwner(const uint8_t* base, uint32_t box) {
  for (const auto& entry : g_elements) {
    if (BoxOf(base, entry.first) == box) {
      g_boxes[box] = entry.first;
      return entry.first;
    }
  }
  return 0u;
}

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
  if (g_states.size() > kMaxValues) {
    g_states.clear();
  }
  const bool named = Lookup(element, known);
  Post(name, named && known.presentation != 0u ? known.presentation : presentation, element,
       named ? known.path : std::string(), Number(value));
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
  if (g_texts.size() > kMaxValues) {
    g_texts.clear();
  }
  g_texts[box] = text;

  uint32_t element = 0u;
  auto owner = g_boxes.find(box);
  if (owner != g_boxes.end()) {
    element = owner->second;
  } else {
    element = FindBoxOwner(base, box);
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
  known.path = parent != 0u && Lookup(parent, parent_known) && !parent_known.path.empty() &&
                       parent_known.path != kRootPath
                   ? parent_known.path + "." + segment
                   : segment;
  g_elements[element] = known;

  // The element's text box, so a commit on the box names the element it belongs to.
  if (const uint32_t box = BoxOf(base, element); box != 0u) {
    g_boxes[box] = element;
  }
}

/// The presentation's root element, named `Scene`; the presentation is the argument when given.
REX_EXTERN(rex_GuiPresentation_Root) {
  const uint32_t presentation = ctx.r3.u32;
  __imp__rex_GuiPresentation_Root(ctx, base);
  const uint32_t root = ctx.r3.u32;
  if (base == nullptr || !Plausible(root)) {
    return;
  }
  std::lock_guard<std::mutex> lock(g_mutex);
  Known known;
  if (Lookup(root, known) && !known.path.empty()) {
    return;
  }
  known.presentation = presentation;
  known.path = kRootPath;
  g_elements[root] = known;
}

/// The registry filer: the element being filed, for the attribute getter below.
REX_EXTERN(rex_GuiRegistry_File) {
  const Filing outer = t_filing;
  t_filing.element = ctx.r4.u32;
  t_filing.presentation = ctx.r5.u32;
  __imp__rex_GuiRegistry_File(ctx, base);
  t_filing = outer;
}

/// The attribute-string getter, `sub_828183C8(out, element, pres, &attributeName)`: during a
/// filing of this element, the string it answers is the registry's key for the element.
REX_EXTERN(rex_GuiElement_AttributeString) {
  const uint32_t out = ctx.r3.u32;
  const uint32_t element = ctx.r4.u32;
  const uint32_t attribute_name = ctx.r6.u32;
  __imp__rex_GuiElement_AttributeString(ctx, base);

  if (base == nullptr || element == 0u || element != t_filing.element || !Plausible(element)) {
    return;
  }
  const std::string value = ReadCharString(base, out);
  if (value.empty()) {
    return;
  }
  const std::string attribute = ReadCharString(base, attribute_name, 64u);

  std::lock_guard<std::mutex> lock(g_mutex);
  Known known;
  if (Lookup(element, known) && !known.path.empty()) {
    return;  // a resolved path outranks the registry key
  }
  if (g_elements.size() > kMaxCached) {
    g_elements.clear();
    g_boxes.clear();
  }
  known.presentation = t_filing.presentation;
  known.path = attribute + ":" + value;
  g_elements[element] = known;
  if (const uint32_t box = BoxOf(base, element); box != 0u) {
    g_boxes[box] = element;
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

/// A timeline position, `sub_8281A5D8(elem, pres, f1)`: posted as the guest passes it (the meters'
/// fraction * 1000), before the truncation the callee applies.
REX_EXTERN(rex_GuiElement_SetTime) {
  const uint32_t element = ctx.r3.u32;
  const uint32_t presentation = ctx.r4.u32;
  const float value = static_cast<float>(ctx.f1.f64);
  __imp__rex_GuiElement_SetTime(ctx, base);
  if (base != nullptr && presentation != 0u) {
    uint32_t bits = 0u;
    std::memcpy(&bits, &value, sizeof(bits));
    PostValue(base, element, presentation, kKindTime, bits, "@Element.Time", value);
  }
}

/// A text set from a tag name, `sub_828191A8(elem, &name)`: the element's box shows the result.
REX_EXTERN(rex_GuiElement_SetTextName) {
  const uint32_t element = ctx.r3.u32;
  __imp__rex_GuiElement_SetTextName(ctx, base);
  if (base == nullptr || !Plausible(element)) {
    return;
  }
  const uint32_t box = BoxOf(base, element);
  if (box == 0u) {
    return;
  }
  {
    std::lock_guard<std::mutex> lock(g_mutex);
    if (g_boxes.find(box) == g_boxes.end()) {
      g_boxes[box] = element;
    }
  }
  PostBoxText(base, box);
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
  const uint32_t box = BoxOf(base, element);
  if (box == 0u) {
    return;
  }
  {
    std::lock_guard<std::mutex> lock(g_mutex);
    if (g_boxes.find(box) == g_boxes.end()) {
      g_boxes[box] = element;
    }
  }
  PostBoxText(base, box);
}

/// A position, `sub_82818580(elem, pres, f1, f2, f3)`: the element's local position attributes,
/// posted when any of the three changed.
REX_EXTERN(rex_GuiElement_SetPosition) {
  const uint32_t element = ctx.r3.u32;
  const uint32_t presentation = ctx.r4.u32;
  const float x = static_cast<float>(ctx.f1.f64);
  const float y = static_cast<float>(ctx.f2.f64);
  const float z = static_cast<float>(ctx.f3.f64);
  __imp__rex_GuiElement_SetPosition(ctx, base);
  if (base == nullptr || presentation == 0u || !Plausible(element)) {
    return;
  }
  std::array<uint32_t, 3> bits{};
  std::memcpy(&bits[0], &x, sizeof(float));
  std::memcpy(&bits[1], &y, sizeof(float));
  std::memcpy(&bits[2], &z, sizeof(float));
  Known known;
  std::lock_guard<std::mutex> lock(g_mutex);
  auto last = g_positions.find(element);
  if (last != g_positions.end() && last->second == bits) {
    return;
  }
  if (g_positions.size() > kMaxValues) {
    g_positions.clear();
  }
  g_positions[element] = bits;
  const bool named = Lookup(element, known);
  refii::vfs::GuiNativeCall call;
  call.name = "@Element.Position";
  call.arguments.push_back(Text(Hex(named && known.presentation != 0u ? known.presentation : presentation)));
  call.arguments.push_back(Text(Hex(element)));
  call.arguments.push_back(Text(named ? known.path : std::string()));
  call.arguments.push_back(Number(x));
  call.arguments.push_back(Number(y));
  call.arguments.push_back(Number(z));
  refii::vfs::PostGuiNativeCall(std::move(call));
}

/// A texture by path, `sub_82818EF0(&path, elem)`: posted when the element's path changed.
REX_EXTERN(rex_GuiElement_SetTexture) {
  const uint32_t path_handle = ctx.r3.u32;
  const uint32_t element = ctx.r4.u32;
  const std::string texture = base != nullptr ? ReadCharString(base, path_handle, 260u) : std::string();
  __imp__rex_GuiElement_SetTexture(ctx, base);
  if (base == nullptr || !Plausible(element) || texture.empty()) {
    return;
  }
  Known known;
  std::lock_guard<std::mutex> lock(g_mutex);
  auto last = g_textures.find(element);
  if (last != g_textures.end() && last->second == texture) {
    return;
  }
  if (g_textures.size() > kMaxValues) {
    g_textures.clear();
  }
  g_textures[element] = texture;
  const bool named = Lookup(element, known);
  Post("@Element.Texture", named ? known.presentation : 0u, element, named ? known.path : std::string(),
       Text(texture));
}
