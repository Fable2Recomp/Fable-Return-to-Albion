// refii - Fable 3's UI writers STATED AT THEIR ENTRY: the value each caller passes, before the
// writer looks for the element, its text box, its opacity or its renderable — the things a
// headless presentation does not have. gui_element_hooks.cpp keeps its after-the-fact reads
// (`@Element.Text` from box+0x88, the toggle's opacity) as the cross-check Godot compares these
// against; what Godot draws is what is said here.
//
// Every address is the Fable 3 disc IDB (port 13339); each offset names its base.
//
// THE PRESENTATION'S DOCUMENT. Every .bgf is loaded through ONE function, sub_825C7A90(slot,
// CCharString* name): when `*slot` is empty (0x825C7AA0) it builds the path (sub_821DB270 at
// 0x825C7AC0) and stores the loader's answer `K->vt[7](K, path)` into `*slot` (0x825C7B30). Its
// callers name the file: the HUD `"HUD.bgf"` (sub_825C18C0 at 0x825C18F4), the interaction mode
// `"InteractionMode.bgf"` (sub_825C42C0 at 0x825C42FC) and the screen loader sub_825C79A0 (a
// screen id's name from its map at +160, 0x825C7A3C). `@Presentation.Load(pres, file)` is that
// store: Godot keys every statement by the document, never by the address. Every statement after
// the load carries the document itself (`=hud`, PresentationKey) in place of the address, so a
// load statement the queue dropped (kMaxPending, oldest first) costs nothing but the reload's
// clean slate.
//
// TEXT. sub_82819070(CCharString* path, pres, LHString* text) resolves the path from the
// presentation's root (sub_8281A038 at 0x8281909C) and commits ONLY through elem+4 (0x828190B0..
// 0x828190CC) — the text is r5 at entry (`mr r30,r5` 0x82819090). Its callers include the
// interaction title/description writer sub_8295CE40, which resolves `<NPCInfo>.Name` /
// `.PersonalityTraits` (dword_83558904 / 908, built by sub_831B31B0 / sub_831B3208) and clears
// `.Reward` / `.GuildSealsValue` with the empty string word_82000B68 (0x8295CEA4, 0x8295CECC).
//
// THE INFO PANES' TEXT sub_82BEA6D8(pane, CCharString* field, LHString* text, r6, pres): the
// presentation is r7 or the interaction mode's IM+0 (0x82BEA734); the field resolves from it
// (0x82BEA74C) and its box is committed only when it is a text node (kind 5, 0x82BEA76C). 48 calls:
// the NPC panel (CInteractionModeMenuNPC_vf17 0x82968364 / 0x8296844C ...), real estate
// (sub_82B1ADF8), the world map (sub_82911870).
//
// THE INTERACTION TEXT sub_82BEF428(wrapper, LHString* text): the wrapper's text child is its +44
// path with "1" appended (`sub_822E65C8(&s, 1)` = "%d", 0x82BEF584; joined 0x82BEF594), else
// the +44 path itself (0x82BEF5DC); the text is r4. The presentation is IM+0 (0x82BEF480).
//
// THE INTERACTION BUTTONS sub_82961340(mode, button, LHString* label, CCharString* glyph,
// enabled): opacity 100 enabled / 50 not (0x82961388 / 0x82961390, SetOpacity 0x8296139C), the
// label committed to the button's `Title` (0x829613B0, 0x829613E4), the glyph set on
// `Icon.Container.A_Text` (dword_8355C364 "Icon", dword_8355C36C "Container.A_Text"; SetTexture
// 0x82961464) and the button shown (sub_82819AE0(button, pres, 1) at 0x82961494) — all behind
// `**(mode+20)` (IM+0, 0x82961360). `@Interaction.Button(pres, button path, label, glyph, enabled)`.
//
// THE LIST sub_82963DE0(mode, scroll 0 refresh / 1 down / 2 up): its rows are the mode's own
// (vt+48 label, vt+52 icon) drawn through the button writer above; the cursor is mode+116 and the
// count mode+120 (as sub_8295AEE8 sets them, 0x8295B1D0 / 0x8295B1EC). The whole body is behind
// IM+0 (0x82963DF8 / 0x82963E00), so the cursor is stated at its entry: `@Interaction.List`.
//
// SLIDES BY PATH. sub_8281A428(CCharString* path, pres, slide) and its restarting twin
// sub_8281A500(path, pres, slide) (GoToSlideAndRestart sub_8281A490: SetState 0x8281A4AC, then the
// timeline to 0 at 0x8281A4E0) resolve the path from the presentation's root (sub_8281A038 at
// 0x8281A45C / 0x8281A534) behind `if (pres)` (0x8281A448 / 0x8281A520) and hand the slide to
// SetState (0x8281A470) / GoToSlideAndRestart (0x8281A548): `@Ui.State` at entry. The interaction
// list is written this way and only this way — its refresh 2 (sub_82963BE0 at 0x82963C28), its
// hide 1 (sub_82963C90 at 0x82963CE0, the mode exit sub_82960D58 at 0x82960DC0), its scroll down
// 3 / up 4 (sub_82963D18 at 0x82963D9C, sub_82964248 at 0x82964520).
//
// THE TEXTURE SETTER sub_82818EF0(CCharString* path, elem): `@Ui.Texture`, the path as its caller
// passes it (gui_element_hooks.cpp reads it at entry): the interaction buttons' glyph on
// `Icon.Container.A_Text` (sub_82961340 at 0x82961464) and the orb sub_829614A0 pairs with it on
// `A_Orb` (0x82961644).
//
// THE DYE BAR sub_8295C940(mode, f1 r, f2 g, f3 b, f4 value, f5 min, f6 max, r10 player) and
// THE WEAPON PANEL sub_8295C500(mode, r4 kind, r5 player): both ask sub_829574B0(player) first
// and send a remote player's to the partner (0x8295C9xx / 0x8295C51C); stated at entry as
// `@Interaction.DyeBar` / `@Interaction.SidePanel`, their meters and toggles being the registry
// writers hud_hooks.cpp states.

#include <atomic>
#include <cstdint>
#include <cstring>
#include <functional>
#include <mutex>
#include <string>
#include <unordered_map>

#include <rex/ppc.h>

#include "gui_statements.h"

REX_EXTERN(__imp__rex_GuiPresentation_Load);
REX_EXTERN(__imp__rex_GuiElement_SetTextAt);
REX_EXTERN(__imp__rex_InfoPane_SetText);
REX_EXTERN(__imp__rex_InteractionText_Set);
REX_EXTERN(__imp__rex_InteractionButton_Show);
REX_EXTERN(__imp__rex_InteractionList_Write);
REX_EXTERN(__imp__rex_InteractionMode_TitleDescription);
REX_EXTERN(__imp__rex_InteractionMode_DyeBar);
REX_EXTERN(__imp__rex_InteractionMode_SidePanel);
REX_EXTERN(__imp__rex_GuiElement_SetStateAt);
REX_EXTERN(__imp__rex_GuiElement_RestartStateAt);
REX_EXTERN(__imp__rex_GuiElement_SetTimeAt);
REX_EXTERN(__imp__rex_GuiElement_SetColour);

namespace {

using namespace refii::fable3;

constexpr uint32_t kGuiManager = 0x83556490u;
constexpr uint32_t kGuiInteraction = 0x48u;  // GUI+72 = IM
constexpr uint32_t kImPresentation = 0u;     // IM+0, InteractionMode.bgf
constexpr uint32_t kModeIm = 20u;            // a mode object's IM (sub_82961340 at 0x82961360)
constexpr uint32_t kModeCursor = 116u;
constexpr uint32_t kModeCount = 120u;
constexpr uint32_t kWrapperTextPath = 44u;   // sub_82BEF428 at 0x82BEF580

// The interaction writers' paths, CCharString handles their static initialisers build.
constexpr uint32_t kNpcName = 0x83558904u;            // "<NPCInfo>.Name"           sub_831B31B0
constexpr uint32_t kNpcTraits = 0x83558908u;          // "<NPCInfo>.PersonalityTraits" sub_831B3208
constexpr uint32_t kNpcReward = 0x8355890Cu;          // "<NPCInfo>.Reward"         sub_831B3260
constexpr uint32_t kNpcGuildSeals = 0x83558910u;      // "<NPCInfo>.GuildSealsValue" sub_831B32B8

constexpr size_t kMaxValues = 16384u;

std::mutex g_mutex;
/// (channel, presentation, element, path) -> the hash of the last value posted.
std::unordered_map<size_t, size_t> g_last;

size_t KeyOf(uint32_t channel, uint32_t presentation, uint32_t element, const std::string& path) {
  size_t key = std::hash<std::string>{}(path);
  key ^= (static_cast<size_t>(channel) << 1) + 0x9E3779B97F4A7C15ull + (key << 6) + (key >> 2);
  key ^= (static_cast<size_t>(element) << 3) + 0x9E3779B97F4A7C15ull + (key << 6) + (key >> 2);
  if (element == 0u) {
    key ^= (static_cast<size_t>(presentation) << 5) + 0x9E3779B97F4A7C15ull + (key << 6) + (key >> 2);
  }
  return key;
}

/// Whether the value differs from the last one stated on this key (and records it).
bool Changed(uint32_t channel, uint32_t presentation, uint32_t element, const std::string& path, size_t value) {
  std::lock_guard<std::mutex> lock(g_mutex);
  if (g_last.size() > kMaxValues) {
    g_last.clear();
  }
  auto [it, inserted] = g_last.emplace(KeyOf(channel, presentation, element, path), value);
  if (!inserted && it->second == value) {
    return false;
  }
  it->second = value;
  return true;
}

void Post(const char* name, uint32_t presentation, uint32_t element, const std::string& path,
          refii::vfs::GuiNativeArgument value, const char* via) {
  Statement(name)
      .Add(Text(PresentationKey(presentation)))
      .Add(Text(Hex(element)))
      .Add(Text(path))
      .Add(std::move(value))
      .Add(Text(via))
      .Post();
}

uint32_t InteractionPresentation(const uint8_t* base) {
  const uint32_t gui = LoadGuestU32(base, kGuiManager);
  if (!Plausible(gui)) {
    return 0u;
  }
  const uint32_t im = LoadGuestU32(base, gui + kGuiInteraction);
  return Plausible(im) ? LoadGuestU32(base, im + kImPresentation) : 0u;
}

/// The presentation a mode object draws in: `**(mode+20)`.
uint32_t ModePresentation(const uint8_t* base, uint32_t mode) {
  if (!Plausible(mode)) {
    return 0u;
  }
  const uint32_t im = LoadGuestU32(base, mode + kModeIm);
  return Plausible(im) ? LoadGuestU32(base, im + kImPresentation) : 0u;
}

std::atomic<int64_t> g_list_last{-1};

/// Every presentation's document as its load named it (the file's stem, lower-cased).
std::mutex g_documents_mutex;
std::unordered_map<uint32_t, std::string> g_documents;

std::string DocumentName(const std::string& file) {
  size_t start = file.find_last_of("\\/");
  start = start == std::string::npos ? 0u : start + 1u;
  size_t end = file.find_last_of('.');
  if (end == std::string::npos || end <= start) {
    end = file.size();
  }
  std::string name = file.substr(start, end - start);
  for (char& c : name) {
    if (c >= 'A' && c <= 'Z') {
      c = static_cast<char>(c - 'A' + 'a');
    }
  }
  return name;
}

}  // namespace

namespace refii::fable3 {

std::string PresentationKey(uint32_t presentation) {
  if (presentation != 0u) {
    std::lock_guard<std::mutex> lock(g_documents_mutex);
    auto found = g_documents.find(presentation);
    if (found != g_documents.end()) {
      return "=" + found->second;
    }
  }
  return Hex(presentation);
}

void StateText(uint32_t presentation, uint32_t element, const std::string& path, const std::string& text,
               const char* via) {
  if (!Changed(1u, presentation, element, path, std::hash<std::string>{}(text) ^ 0x1u)) {
    return;
  }
  Post("@Ui.Text", presentation, element, path, Text(text), via);
}

void StateTextTag(uint32_t presentation, uint32_t element, const std::string& path, uint32_t tag, const char* via) {
  // One text channel: a tag after a literal (or the reverse) is a change.
  if (!Changed(1u, presentation, element, path, (static_cast<size_t>(tag) << 1) ^ 0x2u)) {
    return;
  }
  Post("@Ui.TextTag", presentation, element, path, Text(Hex(tag)), via);
}

void StateTextName(uint32_t presentation, uint32_t element, const std::string& path, const std::string& name,
                   const char* via) {
  if (!Changed(1u, presentation, element, path, std::hash<std::string>{}(name) ^ 0x3u)) {
    return;
  }
  Post("@Ui.TextName", presentation, element, path, Text(name), via);
}

void StateVisible(uint32_t presentation, uint32_t element, const std::string& path, bool shown, const char* via) {
  if (!Changed(2u, presentation, element, path, shown ? 1u : 2u)) {
    return;
  }
  Post("@Ui.Visible", presentation, element, path, Boolean(shown), via);
}

// Every slide write is posted, a repeat of the same slide included: the funnel sub_82268BE0 enters
// the slide each time — it rewrites the slide byte and zeroes the component's time (+0x2B at
// 0x82268CE4, +0x30 / +0x38 at 0x82268D00 / 0x82268CF8) with no comparison — and the restarting
// setter sub_8281A490 also puts its timeline back to 0 (`vt[9]()->vt[15](tl, elem, 0)` at
// 0x8281A4E0). Godot's playback re-enters on each one (Fable3HudDocument.Play).
void StateState(uint32_t presentation, uint32_t element, const std::string& path, int32_t slide, const char* via) {
  Post("@Ui.State", presentation, element, path, Number(static_cast<float>(slide)), via);
}

void StateActive(uint32_t presentation, uint32_t element, const std::string& path, bool on, const char* via) {
  if (!Changed(6u, presentation, element, path, on ? 1u : 2u)) {
    return;
  }
  Post("@Ui.Active", presentation, element, path, Boolean(on), via);
}

void StateTexture(uint32_t presentation, uint32_t element, const std::string& path, const std::string& texture,
                  const char* via) {
  if (!Changed(5u, presentation, element, path, std::hash<std::string>{}(texture))) {
    return;
  }
  Post("@Ui.Texture", presentation, element, path, Text(texture), via);
}

}  // namespace refii::fable3

/// `@Presentation.Load(pres, file)` when the load filled an empty slot; every stated value is
/// forgotten, so the new document hears its texts again.
REX_EXTERN(rex_GuiPresentation_Load) {
  const uint32_t slot = ctx.r3.u32;
  const bool empty = base != nullptr && Plausible(slot) && LoadGuestU32(base, slot) == 0u;
  const std::string file = empty ? ReadCharString(base, ctx.r4.u32, 260u) : std::string();
  __imp__rex_GuiPresentation_Load(ctx, base);
  if (!empty) {
    return;
  }
  const uint32_t presentation = LoadGuestU32(base, slot);
  if (presentation == 0u) {
    return;
  }
  {
    std::lock_guard<std::mutex> lock(g_mutex);
    g_last.clear();
  }
  {
    std::lock_guard<std::mutex> lock(g_documents_mutex);
    g_documents[presentation] = DocumentName(file);
  }
  g_list_last.store(-1);
  Statement("@Presentation.Load").Add(Text(Hex(presentation))).Add(Text(file)).Post();
}

/// sub_82819070(path, pres, text): the text at entry.
REX_EXTERN(rex_GuiElement_SetTextAt) {
  if (base != nullptr) {
    const std::string path = ReadCharString(base, ctx.r3.u32);
    if (!path.empty()) {
      StateText(ctx.r4.u32, 0u, path, ReadLhString(base, ctx.r5.u32), "82819070");
    }
  }
  __imp__rex_GuiElement_SetTextAt(ctx, base);
}

/// sub_82BEA6D8(pane, field, text, r6, pres).
REX_EXTERN(rex_InfoPane_SetText) {
  if (base != nullptr) {
    const std::string field = ReadCharString(base, ctx.r4.u32);
    if (!field.empty()) {
      const uint32_t presentation = ctx.r7.u32 != 0u ? ctx.r7.u32 : InteractionPresentation(base);
      StateText(presentation, 0u, field, ReadLhString(base, ctx.r5.u32), "82BEA6D8");
    }
  }
  __imp__rex_InfoPane_SetText(ctx, base);
}

/// sub_82BEF428(wrapper, text): the wrapper's `+44` + "1".
REX_EXTERN(rex_InteractionText_Set) {
  if (base != nullptr && Plausible(ctx.r3.u32)) {
    const std::string child = ReadCharString(base, ctx.r3.u32 + kWrapperTextPath);
    if (!child.empty()) {
      StateText(InteractionPresentation(base), 0u, child + "1", ReadLhString(base, ctx.r4.u32), "82BEF428");
    }
  }
  __imp__rex_InteractionText_Set(ctx, base);
}

/// sub_82961340(mode, button, label, glyph, enabled).
REX_EXTERN(rex_InteractionButton_Show) {
  if (base != nullptr && Plausible(ctx.r4.u32)) {
    uint32_t named_in = 0u;
    std::string path;
    if (ElementName(ctx.r4.u32, named_in, path) && !path.empty()) {
      const uint32_t presentation = ModePresentation(base, ctx.r3.u32);
      const std::string label = ReadLhString(base, ctx.r5.u32);
      const std::string glyph = ReadCharString(base, ctx.r6.u32, 260u);
      const bool enabled = (ctx.r7.u32 & 0xFFu) != 0u;
      const size_t value = std::hash<std::string>{}(label + '\x1f' + glyph) ^ (enabled ? 0x55u : 0xAAu);
      if (Changed(3u, presentation, ctx.r4.u32, path, value)) {
        Statement("@Interaction.Button")
            .Add(Text(PresentationKey(presentation != 0u ? presentation : named_in)))
            .Add(Text(path))
            .Add(Text(label))
            .Add(Text(glyph))
            .Add(Boolean(enabled))
            .Post();
      }
    }
  }
  __imp__rex_InteractionButton_Show(ctx, base);
}

/// sub_82963DE0(mode, scroll): the cursor and count it lays out, at entry.
REX_EXTERN(rex_InteractionList_Write) {
  if (base != nullptr && Plausible(ctx.r3.u32)) {
    const int32_t cursor = static_cast<int32_t>(LoadGuestU32(base, ctx.r3.u32 + kModeCursor));
    const int32_t count = static_cast<int32_t>(LoadGuestU32(base, ctx.r3.u32 + kModeCount));
    const int32_t scroll = static_cast<int32_t>(ctx.r4.u32);
    const int64_t key = (static_cast<int64_t>(cursor & 0xFFFF) << 24) | ((count & 0xFFFF) << 8) | (scroll & 0xFF);
    if (g_list_last.exchange(key) != key) {
      Statement("@Interaction.List")
          .Add(Number(static_cast<float>(cursor)))
          .Add(Number(static_cast<float>(count)))
          .Add(Number(static_cast<float>(scroll)))
          .Post();
    }
  }
  __imp__rex_InteractionList_Write(ctx, base);
}

/// sub_8295CE40(mode, title, description): the four texts its body writes, at entry.
REX_EXTERN(rex_InteractionMode_TitleDescription) {
  if (base != nullptr && Plausible(ctx.r3.u32)) {
    const uint32_t presentation = ModePresentation(base, ctx.r3.u32);
    const char* via = "8295CE40";
    StateText(presentation, 0u, ReadCharString(base, kNpcName), ReadLhString(base, ctx.r4.u32), via);
    StateText(presentation, 0u, ReadCharString(base, kNpcTraits), ReadLhString(base, ctx.r5.u32), via);
    StateText(presentation, 0u, ReadCharString(base, kNpcReward), std::string(), via);
    StateText(presentation, 0u, ReadCharString(base, kNpcGuildSeals), std::string(), via);
  }
  __imp__rex_InteractionMode_TitleDescription(ctx, base);
}

/// sub_8295C940(mode, f1..f6, r10 player).
REX_EXTERN(rex_InteractionMode_DyeBar) {
  Statement("@Interaction.DyeBar")
      .Add(Number(static_cast<float>(ctx.f1.f64)))
      .Add(Number(static_cast<float>(ctx.f2.f64)))
      .Add(Number(static_cast<float>(ctx.f3.f64)))
      .Add(Number(static_cast<float>(ctx.f4.f64)))
      .Add(Number(static_cast<float>(ctx.f5.f64)))
      .Add(Number(static_cast<float>(ctx.f6.f64)))
      .Add(Text(Hex(ctx.r10.u32)))
      .Post();
  __imp__rex_InteractionMode_DyeBar(ctx, base);
}

/// sub_8295C500(mode, kind, player).
REX_EXTERN(rex_InteractionMode_SidePanel) {
  Statement("@Interaction.SidePanel")
      .Add(Number(static_cast<float>(static_cast<int32_t>(ctx.r4.u32))))
      .Add(Text(Hex(ctx.r5.u32)))
      .Post();
  __imp__rex_InteractionMode_SidePanel(ctx, base);
}

/// sub_8281A428(path, pres, slide): the slide at entry, behind the setter's own `if (pres)`.
REX_EXTERN(rex_GuiElement_SetStateAt) {
  if (base != nullptr && ctx.r4.u32 != 0u) {
    const std::string path = ReadCharString(base, ctx.r3.u32);
    if (!path.empty()) {
      StateState(ctx.r4.u32, 0u, path, static_cast<int32_t>(ctx.r5.u32), "8281A428");
    }
  }
  __imp__rex_GuiElement_SetStateAt(ctx, base);
}

/// sub_8281A500(path, pres, slide): the same, for the restarting GoToSlide.
REX_EXTERN(rex_GuiElement_RestartStateAt) {
  if (base != nullptr && ctx.r4.u32 != 0u) {
    const std::string path = ReadCharString(base, ctx.r3.u32);
    if (!path.empty()) {
      StateState(ctx.r4.u32, 0u, path, static_cast<int32_t>(ctx.r5.u32), "8281A500");
    }
  }
  __imp__rex_GuiElement_RestartStateAt(ctx, base);
}

/// sub_8281AD48(path, pres, f1 fraction): a meter by path — behind `if (pres)` (0x8281AD68) it
/// resolves the path and sets the timeline to `(int)(f1 * 1000)` (0x8281ADA4, SetTime 0x8281ADB8):
/// `@Ui.Time` at entry, in the timeline's units (hero status's Morality / Attractiveness / Weight,
/// sub_82975D88 at 0x82975F10 / 0x8297608C / 0x829760A0).
REX_EXTERN(rex_GuiElement_SetTimeAt) {
  if (base != nullptr && ctx.r4.u32 != 0u) {
    const std::string path = ReadCharString(base, ctx.r3.u32);
    if (!path.empty()) {
      const int32_t time = static_cast<int32_t>(static_cast<float>(ctx.f1.f64) * 1000.0f);
      if (Changed(7u, ctx.r4.u32, 0u, path, static_cast<size_t>(static_cast<uint32_t>(time)))) {
        Post("@Ui.Time", ctx.r4.u32, 0u, path, Number(static_cast<float>(time)), "8281AD48");
      }
    }
  }
  __imp__rex_GuiElement_SetTimeAt(ctx, base);
}

/// sub_828190E8(elem, pres, colour*): an element's colour at entry — behind `elem && pres`
/// (0x828190EC / 0x828190F4) it writes the renderable's diffuse `*(*(elem+4))+0x40` as
/// (byte2, byte1, byte0, byte3) / 255 (0x82819110..0x8281919C, flt_832A8930 = 1/255) and marks it
/// dirty (+0x84, 0x828191A0): the four bytes are B, G, R, A. The money alert's text
/// (`MoneyAlert…Group.Text1`, A+56 resolved by sub_8281DE10 at 0x8281DE60) takes white on show
/// and hide (dword_832A503C; 0x8281E2D0, 0x8281EA94), green for a gain and red for a loss
/// (byte_832A5058 / byte_832A5054; 0x8281E5DC, the queued change 0x8281E3AC); the world map's
/// region text white / red (sub_82915F98 at 0x829160C4), the pooled world icons
/// (sub_82B52958). `@Ui.Colour(pres, elem, path, r, g, b, a, via)`, 0..255.
REX_EXTERN(rex_GuiElement_SetColour) {
  const uint32_t element = ctx.r3.u32;
  const uint32_t presentation = ctx.r4.u32;
  if (base != nullptr && Plausible(element) && presentation != 0u && Plausible(ctx.r5.u32)) {
    const uint32_t bgra = LoadGuestU32(base, ctx.r5.u32);
    uint32_t named_in = 0u;
    std::string path;
    if (ElementName(element, named_in, path) && !path.empty() &&
        Changed(8u, presentation, element, path, static_cast<size_t>(bgra))) {
      Statement("@Ui.Colour")
          .Add(Text(PresentationKey(named_in != 0u ? named_in : presentation)))
          .Add(Text(Hex(element)))
          .Add(Text(path))
          .Add(Number(static_cast<float>((bgra >> 8) & 0xFFu)))
          .Add(Number(static_cast<float>((bgra >> 16) & 0xFFu)))
          .Add(Number(static_cast<float>(bgra >> 24)))
          .Add(Number(static_cast<float>(bgra & 0xFFu)))
          .Add(Text("828190E8"))
          .Post();
    }
  }
  __imp__rex_GuiElement_SetColour(ctx, base);
}
