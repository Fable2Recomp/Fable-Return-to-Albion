// refii - Fable 3's Gameface elements, as the guest STATES them: every menu, box and HUD screen in
// Fable 3 is written by C++ through one small element API — resolve a dotted path to an element,
// then set its state, its opacity or its text. There is no `g_Menu:` chunk mechanism as in Fable 2
// (the chunk runner sub_82877A48 serves only the HUD lifecycle, the mini-game bar and the display
// box); the rows of the options, hero status, stats, quest and interaction screens are element
// writes. The resolver's hook NAMES every element (ElementName: the dotted path, a registry key, a
// Lua getElement's), for the statements the writers' own calls make (`@Ui.*`, ui_stated_hooks.cpp
// and the hooks below). The setters' own reports are NOT statements: Godot draws hud.bgf,
// questcompletion.bgf, creditsscreen.bgf, the interaction mode and the box screens from stated
// calls alone. What stays of the setter hooks here:
//   - the headless bookkeeping the guest's read-backs need (RememberHeadless: GetOpacity in the
//     visibility toggle at 0x82877834, the credits' GetPosition at 0x82B75014);
//   - THE CENSUS `[fable3-unstated] <doc> <path> <channel> = <value> via <caller>`: every write the
//     setter funnels (SetState, SetOpacity outside the toggle, SetTime, SetPosition, the text box
//     commit, the slide funnel, Lua setAttribute, the .bgf executor) make on a document Godot draws
//     from stated calls (kStatedOnlyDocuments), logged once per (document, path, channel, caller). A
//     caller that is not a stated site (Fable3/versions/Fable3/refiii_config.toml) is a writer the
//     static census missed.
//
// THE RESOLVER sub_82877B48(CCharString* path, parent, presentation) walks `A.B.C` one segment per
// recursive call (`sub_8226CAF0(path, ".")` at 0x82877BD0), starting at the presentation's "Layer"
// root when the parent is 0 (0x82877B88), and ends in the renderer's lookup
// `vt[84](renderer, sdbmHash(segment), parent)` (0x82877D28; the hash is sub_824CBC50,
// `h = 65599*h + c`). It consumes the path handle (sub_8221E2B8 at 0x82877D30), so the path is
// read on ENTRY and the element taken from r3 on EXIT; a child's full path is its parent's plus the
// segment. Every wrapper resolves through it: sub_82877D40 (getElement), sub_82876D78 (text box),
// sub_82878130 (state), sub_82877030 (text tag), sub_82878A50 / sub_828782E0 (floats).
//
// THE SETTERS. sub_8227BC50(elem, pres, state) -> `vt[8](states, elem, state, 1)` (0x8227BCDC);
// sub_82877128(elem, pres, f1 opacity) -> the "opacity" attribute (dword_8359FA60) through vt[72]
// — also the visibility toggle sub_828777E8(elem, pres, shown), which scales the opacity by 1024 or
// 1/1024 across the 0.09765625 threshold (flt_832E23A8, 0x82877840..0x82877870);
// sub_828782E0(elem, pres, f1) -> `pres->vt[9]()->vt[11](timeline, elem, (int)f1)` (fctiwz at
// 0x82878324, call at 0x8287833C): the element's timeline position, which the meters drive through
// sub_82878A50(path, pres, fraction) as `fraction * 1000` (0x82878AAC) — the hero status bars
// (sub_829BE760 at 0x829BE8E8 / 0x829BEA64 / 0x829BEA78) and the options sliders (sub_825F8B28);
// text: a text element's box is `*(*(elem+4))` (0x82876FB0), whose shown string is the LHString
// handle at box+0x88 — sub_82B7A488(box, &string) compares (sub_82299858 at 0x82B7A4B0) and
// assigns it (sub_8226D528 at 0x82B7A4C4), marking +0x84/+0x86 dirty; sub_82876F90(elem, tagId)
// resolves the tag through the text manager (sub_82312870 at disc 0x828192D4) and assigns the same
// field inline (0x82877004); sub_82876EB0(elem, &tagName) resolves a tag NAME (sub_823CBB30 at
// 0x82876EE8) and assigns it inline too (0x82876F0C) — the promise's button labels (sub_827DA840 at
// 0x827DA978 / 0x827DA9B0) and sub_82876F38's six callers. All are read AFTER they return, from box+0x88: rep +0 = UTF-16BE
// characters, +4 = length in code units (as the HUD registry's strings, hud_hooks.cpp).
//
// THE REGISTRY ELEMENTS NEVER PASS THE RESOLVER. The HUD's registry builder sub_8269EB40 walks the
// whole element tree (sub_8269F0F0 recurses the children from sub_82877E40) and hands every element
// to each of fourteen registries' filer, ONE shared body sub_82BCF250(registry, element, pres): it
// asks the registry its attribute name (vt[4], 0x82BCF27C), reads that attribute off the element
// as a string — sub_828760D0(out, element, pres, &name) at 0x82BCF290 — and, when non-empty, files
// the element under the string's FNV-1 (0x82BCF2F8..0x82BCF330). The HUD update then writes those
// elements straight from the records (0x82259028: `sub_8227BC50(*(*entry+4), pres, 2)`), as do the
// TopBox toggles and the pooled world-icon elements (sub_82B866C8 at 0x82B86A98). Hooking the
// filer and the getter names them `<Attribute>:<value>` — the registry's own key, which is what
// the guest addresses them by. A dotted path, when one is later resolved, replaces it.
//
// THE INTERACTION TEXT BOX is committed by a path the resolver saw but a box it had not: the
// text-box wrapper sub_82C20230 keeps the paths (+36 root, +44 `.Text` child) and its commit
// sub_82C203B8 resolves the child (0x82C20544) and then commits `*(*(elem+4))` (0x82C20700) — a
// box that was still null when the element was first resolved (0x82C20584..0x82C20598 guard it).
// A commit on an unknown box therefore looks the box up through the elements' own `+4` slots.
//
// THE POSITION SETTER sub_82876288(elem, pres, f1 x, f2 y, f3 z) writes the element's own
// `position.x/.y/.z` attributes — ids dword_8359FA64/50/54, sdbm("position.x") & 0x07FFFFFF (the
// initialiser at 0x8322AFA4..0x8322AFB0) — through the attribute store `pres->vt[8]()->vt[18]`
// (0x828762F4 check, 0x82876324 / 0x8287634C / 0x82876374), so the values are in the .bgf's own
// units: the element's LOCAL position, as the file authors it. Its writers are the box layout
// sub_82976790, which measures a guest-laid-out text box (`BoxName`) and places each line element
// (0x8297695C), and the HUD-element icon placement sub_82B866C8 (0x82B86B38, the co-op off-screen
// markers clamped to the screen edge). The credits' own calls are stated (`@Ui.Position`,
// headless_commit_hooks.cpp); the box fit's lines are Godot's (Fable3HudDocument.StateStacked).
//
// THE TEXTURE SETTER sub_82876BF8(&path, elem) hands the CCharString path to the element's
// renderable `*(*(elem+4))` (sub_82B77B90 at 0x82876C24), which hashes it (sub_83165FC0 at
// 0x82B77C0C) into its texture slot +512 (0x82B77C20). The interaction buttons take their glyph
// this way: sub_8296FF98 sets `Icon.Container.A_Text` to the button's `…\<b>_text.tex`
// (0x829700BC) and sub_829700F8 maps it to the matching `…\<b>_orb.tex` on `Icon.Container.A_Orb`
// (0x8297029C). Stated as `@Ui.Texture(pres, elem, path, texturePath)`.
//
// THE SCREEN ROOT is written straight through `sub_82877AC0(pres)` — the renderer's root element,
// `pres->vt[8]()->vt[17]()` (0x82877B10 / 0x82877B24): the interaction mode's tick sub_825EE908
// sets InteractionMode.bgf's (IM+0) to 1 / 2 (0x825EEBA0, 0x825EEBEC, 0x825EEC1C, 0x825EED58);
// hud.bgf's is set by sub_824CD7E8 (2), sub_824CD860 (1) and sub_823A5558 (3,
// screen_stated_hooks.cpp states those). The
// resolver names its "Layer" child by passing that root as the parent (0x82877B78..0x82877B98), so
// the root is named `Scene` (the chunk runner's own `getElement('Scene.Layer')`) and contributes no
// prefix: paths stay `Layer.…` as the census has them.
//
// THE GAMEFACE LUA ELEMENT API writes around every setter above. AKLuaEngine's registration
// sub_82C54320 binds the globals (pushcclosure + setfield -10002): getElement sub_82C589E8,
// setAttribute sub_82C59170, goToSlide sub_82C561B0, goToTime sub_82C56160, play / pause /
// goToNext/Previous/BackSlide, fireEvent, clone, … Each takes its presentation from the Lua
// registry through sub_82265E20 (0x82C58AF0, 0x82C5926C, 0x82C56374). Its writes:
// - setAttribute(elem, name, value) hashes the name (sdbm & 0x07FFFFFF, sub_824CBC50 at 0x82C59254)
//   and calls the attribute store `pres->vt[8]()->vt[18](store, elem, id, value)` at 0x82C5958C
//   SYNCHRONOUSLY — not through sub_82877128 or sub_82876288; `active` (dword_8359F994) instead sets
//   the element's flag 0x2000 through vt[20] (0x82C5933C). hud.lua's g_HUD:Enable/Disable is this:
//   `setAttribute(getElement('Scene.Layer'), 'opacity', 100 / 0)`.
// - getElement(path [, parent]) walks the segments itself through the store's lookup vt[21]
//   (0x82C58C14), never the resolver: `Scene` first is the root, `parent` steps up (vt[22]).
// - goToSlide / goToTime / play … QUEUE a command (sub_82C562D0 -> pres->vt[7] = sub_82C69A18 at
//   0x82C565A8, the ring at pres+324), drained in the presentation update (sub_8225D9F0 ->
//   sub_82183B28 -> sub_82C695D0 at 0x82183C1C). The executor is also what the .bgf's own authored
//   actions run through: COMMAND_SETPROPERTY -> store->vt[18] (0x82C69628), COMMAND_GOTOTIME ->
//   `pres->vt[9]()->vt[11]` (0x82C69718), COMMAND_GOTOSLIDE -> `->vt[2]` (0x82C69758),
//   GOTOSLIDENAME -> vt[3] sub_82C6ED88, which resolves the name and calls vt[2] (0x82C6EE18).
//
// THE SLIDE FUNNEL is that vt[2]: sub_82267158(slides = pres+0x238, component, slide, flag), the
// presentation's slide manager (vtable 0x82002AC0, `pres->vt[9]` = disc 0x82193990 `addi r3,r3,0x238`).
// It writes the component's current slide byte +0x2B (0x8226725C) after its range check
// (0x8226719C). Every slide change reaches it: SetState (0x8227BCDC), the executor, the name and
// next/previous variants. It is census only — except INSIDE SetState, whose own census line names
// the element its caller passed (the funnel gets that element's component, sub_8227E390).
// Attribute writes are watched at the Lua method and the executor instead of the store's vt[18]
// (sub_821C9F98): SetOpacity (0x828771E4) and SetPosition reach vt[18] too, and the two entry
// points are disjoint from them — neither calls the other.

#include <cstdint>
#include <array>
#include <cstring>
#include <functional>
#include <mutex>
#include <string>
#include <unordered_map>
#include <unordered_set>
#include <vector>

#include <rex/logging.h>
#include <rex/ppc.h>

#include "core/headless_presentation.h"
#include "gui_statements.h"
#include "../../vfs/include/fable3_guest_build.h"

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
REX_EXTERN(__imp__rex_GuiSlides_GoTo);
REX_EXTERN(__imp__rex_GamefaceLua_Presentation);
REX_EXTERN(__imp__rex_GamefaceLua_GetElement);
REX_EXTERN(__imp__rex_GamefaceLua_SetAttribute);
REX_EXTERN(__imp__rex_GamefacePresentation_Execute);
REX_EXTERN(__imp__rex_GuiElement_SetVisible);
REX_EXTERN(__imp__rex_GuiElement_SetFlagActive);

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
std::unordered_map<uint32_t, std::string> g_textures;  // element -> last texture path stated

/// The presentation root's name; it prefixes nothing.
const char kRootPath[] = "Scene";

/// The element the registry filer sub_82BCF250 is filing on this thread, and the presentation:
/// the attribute getter's hook records the string only for that element.
struct Filing {
  uint32_t element = 0;
  uint32_t presentation = 0;
};
thread_local Filing t_filing;

/// Inside SetState on this thread: the slide funnel leaves the post to it.
thread_local uint32_t t_set_state_depth = 0u;

/// Inside the visibility toggle sub_828777E8 on this thread: the opacity its SetOpacity writes
/// (0x82877870) is derived from a read-back (GetOpacity at 0x82877834); the toggle's own bool is the
/// statement (`@Ui.Visible`), so that SetOpacity is neither reported nor counted.
thread_local uint32_t t_toggle_depth = 0u;

/// The presentation the Gameface Lua method running on this thread took from the registry
/// (sub_82265E20's answer), 0 before it asked.
thread_local uint32_t t_lua_presentation = 0u;

// The presentation class (sub_82C68D68 stores the vtable at 0x82C68D90) and its slide manager.
const refii::guest::Addr kPresentationVtable{0x82007628u};
constexpr uint32_t kSlideManagerOffset = 0x238u;
constexpr uint32_t kComponentSlide = 0x2Bu;  // byte: the component's current slide (0x8226725C)

// lua_State and TValue (gui_natives.cpp's LAYOUTS): top +28, base +32; TValue 8 bytes, value +0,
// tag +4; TString length `*ts & 0x3FFFFFFF`, characters at ts+8; a userdata's payload at +16.
constexpr uint32_t kLuaTop = 28u;
constexpr uint32_t kLuaBase = 32u;
constexpr uint32_t kTValueSize = 8u;
constexpr uint32_t kTValueTag = 4u;
constexpr uint32_t kLuaTBoolean = 1u;
constexpr uint32_t kLuaTLightUserdata = 2u;
constexpr uint32_t kLuaTNumber = 3u;
constexpr uint32_t kLuaTString = 4u;
constexpr uint32_t kLuaTUserdata = 7u;
constexpr uint32_t kUdataPayload = 16u;

// The executor's command record (sub_82C69A18 fills it, 0x82C69A28..0x82C69A60): +0 target,
// +4 the command's hash, +8 / +12 its two arguments. Hashes are `sdbm("COMMAND_…") & 0x7FFFFFFF`
// (sub_8322A560: `clrlwi r11,r3,1`).
constexpr uint32_t kCommandTarget = 0u;
constexpr uint32_t kCommandId = 4u;
constexpr uint32_t kCommandFirst = 8u;
constexpr uint32_t kCommandSecond = 12u;
constexpr uint32_t kAttributeIdMask = 0x07FFFFFFu;

/// sub_824CBC50: `h = 65599*h + (signed char)c` over at most 100 characters, the terminator
/// included (the loop tests the character after adding it).
uint32_t Sdbm(const std::string& text) {
  uint32_t hash = 0u;
  for (size_t i = 0; i < 100u; ++i) {
    const int8_t ch = i < text.size() ? static_cast<int8_t>(text[i]) : 0;
    hash = hash * 65599u + static_cast<uint32_t>(static_cast<int32_t>(ch));
    if (ch == 0) {
      break;
    }
  }
  return hash;
}

/// The float attributes a .bgf action's COMMAND_SETPROPERTY may carry, by id.
const std::unordered_map<uint32_t, std::string>& FloatAttributes() {
  static const std::unordered_map<uint32_t, std::string> names = [] {
    std::unordered_map<uint32_t, std::string> out;
    for (const char* name : {"opacity", "position.x", "position.y", "position.z", "scale.x", "scale.y", "scale.z",
                             "rotation.x", "rotation.y", "rotation.z", "pivot.x", "pivot.y", "textcolor.r",
                             "textcolor.g", "textcolor.b", "boxwidth", "boxheight", "fontsize"}) {
      out[Sdbm(name) & kAttributeIdMask] = name;
    }
    return out;
  }();
  return names;
}

uint32_t CommandHash(const char* name) { return Sdbm(name) & 0x7FFFFFFFu; }

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

/// The element's name for a statement: the recorded path, else — for a headless element no
/// resolver step or filing named (a child walk's, a pooled element) — `#<file ordinal>`, the
/// .bgf element table's index Godot's document is indexed by (core/headless_presentation.h,
/// FileOrdinal). Caller holds g_mutex.
bool Named(uint32_t element, Known& out) {
  const bool found = Lookup(element, out);
  if (found && !out.path.empty()) {
    return true;
  }
  const int32_t ordinal = refii::headless::FileOrdinal(element);
  uint32_t presentation = 0u, serial = 0u, id = 0u;
  if (ordinal < 0 || !refii::headless::ElementKey(element, &presentation, &serial, &id)) {
    return found;
  }
  out.presentation = presentation;
  out.path = "#" + std::to_string(ordinal);
  return true;
}

/// THE DOCUMENTS GODOT DRAWS FROM STATED CALLS, what the census watches: the HUD and the end screens
/// (Fable3Hud, Fable3EndScreens), the interaction mode (Fable3Hud.InteractionGroups), Options
/// (Fable3Options) and the box screens' documents (Fable3StatedScreen, Fable3ScreenFrames).
constexpr const char* kStatedOnlyDocuments[] = {
    "=hud",           "=questcompletion",   "=creditsscreen",      "=interactionmode", "=options",
    "=herostatus",    "=statsscreen",       "=promises",           "=questsummary",    "=kingsschedule",
    "=joinoptions",   "=visability",        "=friendschannel",     "=chatchannel",     "=treasureaccounts",
    "=bedmenubox",    "=foodquerybox",      "=compweddingpicker",  "=cooppartnersscreen",
    "=proposalquerybox", "=divorcemessagebox", "=savemenu"};
constexpr size_t kMaxCensus = 4096u;

std::mutex g_census_mutex;
std::unordered_set<std::string> g_census;

/// `[fable3-unstated] <doc> <path> <channel> = <value> via <caller>`, once per (document, path,
/// channel, caller), for a write on a stated-only document.
void Unstated(uint32_t element, uint32_t presentation, const char* channel, const std::string& value, uint32_t via) {
  if (!Plausible(element)) {
    return;
  }
  Known known;
  bool named = false;
  {
    std::lock_guard<std::mutex> lock(g_mutex);
    named = Named(element, known);
  }
  const std::string document = PresentationKey(named && known.presentation != 0u ? known.presentation : presentation);
  bool watched = false;
  for (const char* stated : kStatedOnlyDocuments) {
    watched = watched || document == stated;
  }
  if (!watched) {
    return;
  }
  const std::string path = named && !known.path.empty() ? known.path : "<unnamed " + Hex(element) + ">";
  {
    std::lock_guard<std::mutex> lock(g_census_mutex);
    if (g_census.size() >= kMaxCensus || !g_census.insert(document + ' ' + path + ' ' + channel + ' ' + Hex(via)).second) {
      return;
    }
  }
  REXLOG_INFO("[fable3-unstated] {} {} {} = {} via {:08X}", document.substr(1), path, channel, value, via);
}

std::string Shown(float value) { return std::to_string(value); }

/// The element whose text box `box` is (the resolver's or a commit's earlier answer), or 0.
uint32_t BoxOwner(const uint8_t* base, uint32_t box) {
  std::lock_guard<std::mutex> lock(g_mutex);
  auto owner = g_boxes.find(box);
  return owner != g_boxes.end() ? owner->second : FindBoxOwner(base, box);
}

// THE EXISTENCE-CHECKED SETTERS on a headless element (headless_presentation_hooks.cpp). SetOpacity
// writes `opacity` (dword_8359FA60, `lwz r5,dword_8359FA60@l(r9)` 0x82877174) only when
// `sub_821C9F40` finds it (0x8287717C); SetPosition checks `position.x` through service vf +12
// (0x828762F4) and then writes x/y/z (dword_8359FA64 / 50 / 54, 0x82876318 / 0x82876344 /
// 0x8287636C). A file element carries what the .bgf authored; a fileless one (a name the file has
// not got) is given the attributes here, so the guest's own write lands and its read-backs
// (GetPosition, the visibility toggle's GetOpacity) echo it. Either way the write is stated by the
// hook from its arguments.
const refii::guest::Addr kAttrOpacityIds[] = {refii::guest::Addr{0x8359FA60u}};
const refii::guest::Addr kAttrPositionIds[] = {refii::guest::Addr{0x8359FA64u}, refii::guest::Addr{0x8359FA68u},
                                                refii::guest::Addr{0x8359FA6Cu}};

void RememberHeadless(const uint8_t* base, uint32_t element, const refii::guest::Addr* globals, const float* values,
                      size_t count) {
  if (!Plausible(element)) {
    return;
  }
  uint32_t ids[3] = {};
  for (size_t i = 0; i < count && i < 3u; ++i) {
    ids[i] = LoadGuestU32(base, globals[i]) & kAttributeIdMask;
  }
  refii::headless::RememberWrite(const_cast<uint8_t*>(base), element, ids, values, count);
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

uint32_t refii::fable3::FilingElement() { return t_filing.element; }

/// The registry filer: the element being filed, for the attribute getter below and the
/// registry insert's hook (hud_hooks.cpp).
REX_EXTERN(rex_GuiRegistry_File) {
  const Filing outer = t_filing;
  t_filing.element = ctx.r4.u32;
  t_filing.presentation = ctx.r5.u32;
  __imp__rex_GuiRegistry_File(ctx, base);
  t_filing = outer;
}

/// The attribute-string getter, `sub_828760D0(out, element, pres, &attributeName)`: a headless
/// element answers its FILE's string attribute (headless_presentation_hooks.cpp); during a filing
/// of this element, the string it answers is the registry's key for the element.
REX_EXTERN(rex_GuiElement_AttributeString) {
  const uint32_t out = ctx.r3.u32;
  const uint32_t element = ctx.r4.u32;
  const uint32_t presentation = ctx.r5.u32;
  const uint32_t attribute_name = ctx.r6.u32;
  __imp__rex_GuiElement_AttributeString(ctx, base);
  if (base != nullptr && HeadlessAnswerAttributeString(ctx, base, out, element, presentation, attribute_name)) {
    ctx.r3.u32 = out;  // the read answers `out` (`mr r3,r29` 0x828761A8)
  }

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

/// SetState sub_8227BC50(elem, pres, slide): census only.
REX_EXTERN(rex_GuiElement_SetState) {
  const uint32_t element = ctx.r3.u32;
  const uint32_t presentation = ctx.r4.u32;
  const int32_t state = static_cast<int32_t>(ctx.r5.u32);
  const uint32_t via = static_cast<uint32_t>(ctx.lr);
  ++t_set_state_depth;
  __imp__rex_GuiElement_SetState(ctx, base);
  --t_set_state_depth;
  if (base != nullptr && presentation != 0u) {
    Unstated(element, presentation, "State", std::to_string(state), via);
  }
}

/// SetOpacity sub_82877128(elem, pres, f1): the headless store the read-backs answer from, and the
/// census — not for the visibility toggle's own write (its bool is the statement).
REX_EXTERN(rex_GuiElement_SetOpacity) {
  const uint32_t element = ctx.r3.u32;
  const uint32_t presentation = ctx.r4.u32;
  const float opacity = static_cast<float>(ctx.f1.f64);
  const uint32_t via = static_cast<uint32_t>(ctx.lr);
  if (base != nullptr && presentation != 0u) {
    RememberHeadless(base, element, kAttrOpacityIds, &opacity, 1u);
  }
  __imp__rex_GuiElement_SetOpacity(ctx, base);
  if (base != nullptr && presentation != 0u && t_toggle_depth == 0u) {
    Unstated(element, presentation, "Opacity", Shown(opacity), via);
  }
}

/// A timeline position, `sub_828782E0(elem, pres, f1)` (the meters' fraction * 1000): census only.
REX_EXTERN(rex_GuiElement_SetTime) {
  const uint32_t element = ctx.r3.u32;
  const uint32_t presentation = ctx.r4.u32;
  const float value = static_cast<float>(ctx.f1.f64);
  const uint32_t via = static_cast<uint32_t>(ctx.lr);
  __imp__rex_GuiElement_SetTime(ctx, base);
  if (base != nullptr && presentation != 0u) {
    Unstated(element, presentation, "Time", Shown(value), via);
  }
}

/// A text set from a tag name, `sub_82876EB0(elem, &name)`: the element's box shows the result.
REX_EXTERN(rex_GuiElement_SetTextName) {
  const uint32_t element = ctx.r3.u32;
  if (base != nullptr && Plausible(element)) {
    // The name as given (r4, a CCharString handle; resolved by sub_823CBB30 at 0x82876EE8 only
    // behind elem+4, 0x82876EC4..0x82876ED0): stated whether or not the element has a box.
    uint32_t presentation = 0u;
    std::string path;
    ElementName(element, presentation, path);
    StateTextName(presentation, element, path, ReadCharString(base, ctx.r4.u32), "828191A8");
  }
  __imp__rex_GuiElement_SetTextName(ctx, base);
}

/// A string committed to a text box, `sub_82B7A488(box, LHString*)`: census only — the commits
/// Godot draws are stated on their guards (headless_commit_hooks.cpp) or by their writer's entry.
REX_EXTERN(rex_GuiTextBox_Commit) {
  const uint32_t box = ctx.r3.u32;
  const std::string text = base != nullptr && Plausible(box) ? ReadLhString(base, ctx.r4.u32, kMaxTextUnits) : std::string();
  const uint32_t via = static_cast<uint32_t>(ctx.lr);
  __imp__rex_GuiTextBox_Commit(ctx, base);
  if (base != nullptr && Plausible(box)) {
    if (const uint32_t element = BoxOwner(base, box); element != 0u) {
      Unstated(element, 0u, "Text", "'" + text + "'", via);
    }
  }
}

/// A text tag resolved and assigned to an element's box: `sub_82876F90(elem, tagId)`.
REX_EXTERN(rex_GuiElement_SetTextTag) {
  const uint32_t element = ctx.r3.u32;
  if (base != nullptr && Plausible(element)) {
    // The tag as given (r4, its FNV-1; looked up by sub_82312870 at disc 0x828192D4 only behind
    // elem+4, 0x82876FA4..0x82876FB0): stated whether or not the element has a box.
    uint32_t presentation = 0u;
    std::string path;
    ElementName(element, presentation, path);
    StateTextTag(presentation, element, path, ctx.r4.u32, "82819288");
  }
  __imp__rex_GuiElement_SetTextTag(ctx, base);
}

/// A position, `sub_82876288(elem, pres, f1, f2, f3)`: the element's local position attributes,
/// kept for the headless read-backs (GetPosition), and the census.
REX_EXTERN(rex_GuiElement_SetPosition) {
  const uint32_t element = ctx.r3.u32;
  const uint32_t presentation = ctx.r4.u32;
  const float x = static_cast<float>(ctx.f1.f64);
  const float y = static_cast<float>(ctx.f2.f64);
  const float z = static_cast<float>(ctx.f3.f64);
  const uint32_t via = static_cast<uint32_t>(ctx.lr);
  if (base != nullptr && presentation != 0u) {
    const float xyz[3] = {x, y, z};
    RememberHeadless(base, element, kAttrPositionIds, xyz, 3u);
  }
  __imp__rex_GuiElement_SetPosition(ctx, base);
  if (base != nullptr && presentation != 0u) {
    Unstated(element, presentation, "Position", Shown(x) + ", " + Shown(y) + ", " + Shown(z), via);
  }
}

/// A texture by path, `sub_82876BF8(&path, elem)`: `@Ui.Texture` when the element's path changed.
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
  const bool named = Named(element, known);
  StateTexture(named ? known.presentation : 0u, element, named ? known.path : std::string(), texture, "82818EF0");
}

namespace {

constexpr uint32_t kMaxLuaPath = 256u;

/// A TString by its stated length, bounded; empty when it is not one.
std::string LuaString(const uint8_t* base, uint32_t tstring, uint32_t max_chars) {
  if (!Plausible(tstring)) {
    return std::string();
  }
  const uint32_t length = LoadGuestU32(base, tstring) & 0x3FFFFFFFu;
  if (length == 0u || length > max_chars) {
    return std::string();
  }
  return std::string(reinterpret_cast<const char*>(base + tstring + 8u), length);
}

/// The element a Gameface method takes from a TValue: a light userdata is the element, a full
/// userdata's payload address is (setAttribute 0x82C591E8..0x82C59200, getElement 0x82C58AC4).
uint32_t LuaElement(const uint8_t* base, uint32_t slot) {
  const uint32_t tag = LoadGuestU32(base, slot + kTValueTag);
  const uint32_t value = LoadGuestU32(base, slot);
  if (tag == kLuaTLightUserdata) {
    return value;
  }
  if (tag == kLuaTUserdata && Plausible(value)) {
    return value + kUdataPayload;
  }
  return 0u;
}

/// The presentation a slide manager belongs to, or 0 when it is not the known class's.
uint32_t PresentationOfSlides(const uint8_t* base, uint32_t slides) {
  if (!Plausible(slides) || slides < kSlideManagerOffset) {
    return 0u;
  }
  const uint32_t presentation = slides - kSlideManagerOffset;
  return LoadGuestU32(base, presentation) == kPresentationVtable ? presentation : 0u;
}

/// Splits a dotted path.
std::vector<std::string> Segments(const std::string& path) {
  std::vector<std::string> out;
  size_t start = 0;
  while (start <= path.size()) {
    const size_t dot = path.find('.', start);
    const size_t end = dot == std::string::npos ? path.size() : dot;
    if (end > start) {
      out.push_back(path.substr(start, end - start));
    }
    if (dot == std::string::npos) {
      break;
    }
    start = dot + 1;
  }
  return out;
}

/// A getElement path in the resolver's spelling: segments from the root, `Scene` not spelled,
/// `Layer` dropped when something follows it (the resolver starts beneath it), the root `Scene`.
std::string Spelled(const std::vector<std::string>& from_root) {
  if (from_root.empty()) {
    return kRootPath;
  }
  size_t first = from_root.size() > 1u && from_root[0] == "Layer" ? 1u : 0u;
  std::string out;
  for (size_t i = first; i < from_root.size(); ++i) {
    if (!out.empty()) {
      out.push_back('.');
    }
    out += from_root[i];
  }
  return out;
}

/// The segments from the root a known path stands for, false when it is not a dotted path (a
/// registry key names no place in the tree).
bool FromRoot(const std::string& known, std::vector<std::string>& out) {
  out.clear();
  if (known.find(':') != std::string::npos) {
    return false;
  }
  if (known == kRootPath) {
    return true;
  }
  out = Segments(known);
  if (!out.empty() && out[0] != "Layer") {
    out.insert(out.begin(), "Layer");
  }
  return true;
}

}  // namespace

/// The slide funnel, `sub_82267158(slides, component, slide, flag)`: census of a slide the component
/// took (the range check refuses others, 0x8226719C) outside SetState, which counts its own.
REX_EXTERN(rex_GuiSlides_GoTo) {
  const uint32_t slides = ctx.r3.u32;
  const uint32_t component = ctx.r4.u32;
  const uint32_t slide = ctx.r5.u32;
  const uint32_t via = static_cast<uint32_t>(ctx.lr);
  __imp__rex_GuiSlides_GoTo(ctx, base);
  if (base == nullptr || t_set_state_depth != 0u || !Plausible(component) ||
      base[component + kComponentSlide] != (slide & 0xFFu)) {
    return;
  }
  Unstated(component, PresentationOfSlides(base, slides), "Slide", std::to_string(static_cast<int32_t>(slide)), via);
}

/// The Gameface Lua methods' presentation getter (L): its answer, for the method hooks below.
REX_EXTERN(rex_GamefaceLua_Presentation) {
  __imp__rex_GamefaceLua_Presentation(ctx, base);
  t_lua_presentation = ctx.r3.u32;
}

/// Lua `getElement(path [, parent])`: names the element it answers (the top TValue on return).
REX_EXTERN(rex_GamefaceLua_GetElement) {
  const uint32_t state = ctx.r3.u32;
  std::string path;
  uint32_t parent = 0u;
  bool has_parent = false;
  if (base != nullptr) {
    const uint32_t args = LoadGuestU32(base, state + kLuaBase);
    const uint32_t top = LoadGuestU32(base, state + kLuaTop);
    if (args < top && LoadGuestU32(base, args + kTValueTag) == kLuaTString) {
      path = LuaString(base, LoadGuestU32(base, args), kMaxLuaPath);
    }
    if (top - args == 2u * kTValueSize) {  // the parent is read only when exactly two (0x82C58A5C)
      has_parent = true;
      parent = LuaElement(base, args + kTValueSize);
    }
  }
  t_lua_presentation = 0u;
  __imp__rex_GamefaceLua_GetElement(ctx, base);
  if (base == nullptr || path.empty()) {
    return;
  }
  const uint32_t result = LoadGuestU32(base, state + kLuaTop) - kTValueSize;
  if (LoadGuestU32(base, result + kTValueTag) != kLuaTLightUserdata) {
    return;
  }
  const uint32_t element = LoadGuestU32(base, result);
  if (!Plausible(element)) {
    return;
  }

  std::lock_guard<std::mutex> lock(g_mutex);
  Known known;
  if (Lookup(element, known) && !known.path.empty()) {
    return;  // the resolver's (or an earlier lookup's) name stands
  }
  std::vector<std::string> from_root;
  const std::vector<std::string> segments = Segments(path);
  size_t i = 0;
  if (has_parent && parent != 0u && !(segments.size() > 0u && segments[0] == "Scene")) {
    Known above;
    if (!Lookup(parent, above) || above.path.empty() || !FromRoot(above.path, from_root)) {
      return;  // relative to an element nobody named
    }
  } else if (!segments.empty() && segments[0] == "Scene") {
    i = 1;  // the root (0x82C58C24..0x82C58C50)
  }
  for (; i < segments.size(); ++i) {
    if (segments[i] == "parent") {  // dword_8359F98C: the store's parent-of (vt[22], 0x82C58BF4)
      if (from_root.empty()) {
        return;
      }
      from_root.pop_back();
    } else {
      from_root.push_back(segments[i]);
    }
  }
  if (g_elements.size() > kMaxCached) {
    g_elements.clear();
    g_boxes.clear();
  }
  known.presentation = t_lua_presentation;
  known.path = Spelled(from_root);
  g_elements[element] = known;
  if (const uint32_t box = BoxOf(base, element); box != 0u) {
    g_boxes[box] = element;
  }
}

/// Lua `setAttribute(elem, name, value)`: the census.
REX_EXTERN(rex_GamefaceLua_SetAttribute) {
  const uint32_t state = ctx.r3.u32;
  uint32_t element = 0u;
  std::string name;
  uint32_t value_tag = 0u;
  uint32_t value = 0u;
  std::string text;
  if (base != nullptr) {
    const uint32_t args = LoadGuestU32(base, state + kLuaBase);
    const uint32_t top = LoadGuestU32(base, state + kLuaTop);
    if (args + 3u * kTValueSize <= top) {
      element = LuaElement(base, args);
      if (LoadGuestU32(base, args + kTValueSize + kTValueTag) == kLuaTString) {
        name = LuaString(base, LoadGuestU32(base, args + kTValueSize), 64u);
      }
      value_tag = LoadGuestU32(base, args + 2u * kTValueSize + kTValueTag);
      value = LoadGuestU32(base, args + 2u * kTValueSize);
      if (value_tag == kLuaTString) {
        text = LuaString(base, value, kMaxTextUnits);
      }
    }
  }
  t_lua_presentation = 0u;
  __imp__rex_GamefaceLua_SetAttribute(ctx, base);
  if (base == nullptr || !Plausible(element) || name.empty()) {
    return;
  }
  const uint32_t presentation = t_lua_presentation;
  float number = 0.0f;
  std::memcpy(&number, &value, sizeof(number));

  const std::string shown = value_tag == kLuaTNumber    ? Shown(number)
                            : value_tag == kLuaTString  ? "'" + text + "'"
                            : value_tag == kLuaTBoolean ? (value != 0u ? "true" : "false")
                                                        : "?";
  Unstated(element, presentation, ("Lua." + name).c_str(), shown, 0x82C59170u);
}

/// The presentation's command executor, `sub_82C695D0(pres, &command)`: the queued Lua commands
/// and the .bgf's authored actions. Its slide commands reach the funnel above; its property and
/// timeline commands are counted here.
REX_EXTERN(rex_GamefacePresentation_Execute) {
  static const uint32_t kSetProperty = CommandHash("COMMAND_SETPROPERTY");
  static const uint32_t kGoToTime = CommandHash("COMMAND_GOTOTIME");
  const uint32_t presentation = ctx.r3.u32;
  const uint32_t command = ctx.r4.u32;
  uint32_t target = 0u, id = 0u, first = 0u, second = 0u;
  if (base != nullptr && Plausible(command)) {
    target = LoadGuestU32(base, command + kCommandTarget);
    id = LoadGuestU32(base, command + kCommandId);
    first = LoadGuestU32(base, command + kCommandFirst);
    second = LoadGuestU32(base, command + kCommandSecond);
  }
  __imp__rex_GamefacePresentation_Execute(ctx, base);
  if (base == nullptr || !Plausible(target)) {
    return;
  }
  if (id == kGoToTime) {  // vt[11](slides, target, ms) at 0x82C69718: SetTime's unit
    Unstated(target, presentation, "Execute.Time", std::to_string(static_cast<int32_t>(first)), 0x82C695D0u);
    return;
  }
  if (id != kSetProperty) {
    return;
  }
  const uint32_t attribute = first & kAttributeIdMask;  // 0x82C69614
  const auto& names = FloatAttributes();
  auto found = names.find(attribute);
  if (found == names.end()) {
    return;
  }
  float number = 0.0f;
  std::memcpy(&number, &second, sizeof(number));
  Unstated(target, presentation, ("Execute." + found->second).c_str(), Shown(number), 0x82C695D0u);
}

bool refii::fable3::ElementName(uint32_t element, uint32_t& presentation, std::string& path) {
  std::lock_guard<std::mutex> lock(g_mutex);
  Known known;
  if (!Named(element, known)) {
    return false;
  }
  presentation = known.presentation;
  path = known.path;
  return true;
}

/// THE VISIBILITY TOGGLE sub_828777E8(elem, pres, shown): stated as the bool its caller asks for
/// (r5, `mr r30,r5` 0x82877804). Its body compares the request with IsVisible (sub_82877900 at
/// 0x82877808), reads the opacity back (GetOpacity sub_82877268 at 0x82877834) and scales it by
/// 1024 or 1/1024 across 0.09765625 (flt_832E23A8, 0x82877840..0x8287786C) — a read-back, so its
/// SetOpacity is neither reported nor counted.
REX_EXTERN(rex_GuiElement_SetVisible) {
  const uint32_t element = ctx.r3.u32;
  const uint32_t presentation = ctx.r4.u32;
  const bool shown = (ctx.r5.u32 & 0xFFu) != 0u;
  if (base != nullptr && Plausible(element)) {
    uint32_t named_in = 0u;
    std::string path;
    ElementName(element, named_in, path);
    StateVisible(presentation != 0u ? presentation : named_in, element, path, shown, "82819AE0");
  }
  ++t_toggle_depth;
  __imp__rex_GuiElement_SetVisible(ctx, base);
  --t_toggle_depth;
}

/// THE ACTIVE FLAG sub_828773E8(elem, pres, on, force): flag 0x2000 through the store's vt+80
/// (0x8287747C), skipped when the store's vt+76 already answers `on` unless forced (0x8287745C)
/// — a read-back. Stated as asked (`@Ui.Active`).
REX_EXTERN(rex_GuiElement_SetFlagActive) {
  const uint32_t element = ctx.r3.u32;
  const uint32_t presentation = ctx.r4.u32;
  const bool on = (ctx.r5.u32 & 0xFFu) != 0u;
  if (base != nullptr && Plausible(element) && presentation != 0u) {
    uint32_t named_in = 0u;
    std::string path;
    if (ElementName(element, named_in, path) && !path.empty()) {
      StateActive(presentation, element, path, on, "828196E0");
    }
  }
  __imp__rex_GuiElement_SetFlagActive(ctx, base);
}
