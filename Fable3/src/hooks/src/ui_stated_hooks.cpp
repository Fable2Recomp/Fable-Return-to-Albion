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
// THE INTERACTION TEXT sub_82BEF428(wrapper, LHString* text) commits to the wrapper's +44 path
// with "1" appended (`sub_822E65C8(&s, 1)` = "%d", 0x82BEF584; joined 0x82BEF594) when that element
// resolves, else to the +44 path itself (0x82BEF5C0..0x82BEF5D0: CrimeInfo's `Contents.Crimes` has
// no `Crimes1`), and defers the whole call while the mask is unsized (the text kept at +32, re-run
// by vf3 0x82BEF3E0). It is stated where it commits, on the box guard 0x82BEF754 with the resolved
// element r30 and the text r26 (headless_commit_hooks.cpp), not at entry.
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
#include <rex/system/kernel_state.h>
#include <rex/system/xmemory.h>

#include "gui_statements.h"

REX_EXTERN(__imp__rex_GuiPresentation_Load);
REX_EXTERN(__imp__rex_GuiElement_SetTextAt);
REX_EXTERN(__imp__rex_InfoPane_SetText);
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

void StateOpacity(uint32_t presentation, uint32_t element, const std::string& path, float opacity, const char* via) {
  uint32_t bits = 0u;
  std::memcpy(&bits, &opacity, sizeof(bits));
  if (!Changed(9u, presentation, element, path, static_cast<size_t>(bits))) {
    return;
  }
  Post("@Ui.Opacity", presentation, element, path, Number(opacity), via);
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

/// THE LIST'S EMPTIED ROWS, stated at sub_82963DE0's own commits. A row past the end (cursor+1 /
/// cursor+2 >= mode+120, 0x82963F08 / 0x82963FFC) and the leaving row DissapearingButton (always,
/// after the scroll filled it at 0x8296414C / 0x829641B0) are not written through the button writer:
/// the body resolves the row's `Title` (dword_8355C37C, built "Title" by sub_831DB6C8) and commits
/// the empty string word_82000B68 (sub_8229B650 into r1+0x50) to its box, then hides the row
/// (sub_82819AE0(row, pres, 0) at 0x82963FEC / 0x829640E0 / 0x8296423C). Each [[midasm_hook]] sits on
/// the box guard `lwz r11,4(rTitle)` (0x82963FA4 r27 / Button2 r28, 0x82964098 r30 / Button1 r26,
/// 0x829641F4 r30 / DissapearingButton r24; the presentation r29 = `**(mode+20)`, 0x82963DF8).
/// `@Ui.Text(pres, title, path, text, "82963DE0")`. The row's channel-3 value (what
/// rex_InteractionButton_Show last stated on it) becomes "emptied", so the writer's next label on the
/// row is stated even when it is the label the row held before it was emptied.
void RefiiF3InteractionRowEmptied(PPCRegister& title, PPCRegister& stack, PPCRegister& row,
                                  PPCRegister& presentation) {
  rex::system::KernelState* state = rex::system::kernel_state();
  rex::memory::Memory* memory = state != nullptr ? state->memory() : nullptr;
  const uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
  if (base == nullptr || !Plausible(title.u32) || !Plausible(row.u32)) {
    return;
  }
  uint32_t row_in = 0u, title_in = 0u;
  std::string row_path, title_path;
  if (!ElementName(row.u32, row_in, row_path) || row_path.empty() ||
      !ElementName(title.u32, title_in, title_path) || title_path.empty()) {
    return;
  }
  const std::string text = ReadLhString(base, stack.u32 + 0x50u);
  if (!Changed(3u, presentation.u32, row.u32, row_path, std::hash<std::string>{}("\x1e" + text) ^ 0x33u)) {
    return;
  }
  Post("@Ui.Text", presentation.u32 != 0u ? presentation.u32 : title_in, title.u32, title_path, Text(text),
       "82963DE0");
}

namespace {

/// A stated call site's own SetState / SetOpacity arguments (a [[midasm_hook]] on the `bl`):
/// the element named by the resolver hook, its presentation as the call passes it.
void StateCallSlide(uint32_t element, uint32_t presentation, int32_t slide, const char* via) {
  uint32_t named_in = 0u;
  std::string path;
  if (presentation != 0u && Plausible(element) && ElementName(element, named_in, path) && !path.empty()) {
    StateState(presentation, element, path, slide, via);
  }
}

void StateCallOpacity(uint32_t element, uint32_t presentation, float opacity, const char* via) {
  uint32_t named_in = 0u;
  std::string path;
  if (presentation != 0u && Plausible(element) && ElementName(element, named_in, path) && !path.empty()) {
    StateOpacity(presentation, element, path, opacity, via);
  }
}

}  // namespace

/// THE MULTIPLIER ROOTS AT THE HUD BUILD. sub_82950A18(J, root path, text path, times path) runs
/// twice from the HUD load sub_825C18C0: J = `*(H+0x44)` with "Multiplier" (dword_8355C0EC,
/// 0x825C19B0) and J = `*(H+0x44)+0x14` with "JobMultiplier" (dword_8355C0F8, 0x825C19D0). Once
/// all three paths resolved (0x82950AB8) it rests the root `*J` on slide 4 — `li r5,4` 0x82950AC4,
/// `lwz r3,0(r31)` 0x82950AC8, `lwz r4,0(**(GUI+0x20))` 0x82950AD0, `bl GuiElement_SetState`
/// 0x82950AD4 — and hides it through sub_82950AF0(J, 0) (0x82950AE0). [[midasm_hook]] on that `bl`
/// [r3, r4, r5]: `@Ui.State(pres, root, path, 4, "82950A18")`.
void RefiiF3MultiplierSlide(PPCRegister& element, PPCRegister& presentation, PPCRegister& slide) {
  StateCallSlide(element.u32, presentation.u32, static_cast<int32_t>(slide.u32), "82950A18");
}

/// THE MULTIPLIER ROOT SHOWN OR HIDDEN, sub_82950AF0(J, shown): f1 = 100 when shown (flt_8206D268,
/// 0x82950B20) else 0 (flt_8206C95C, 0x82950B28) onto `*J` (`lwz r3,0(r31)` 0x82950B30) in the HUD
/// (`lwz r4,0(**(GUI+0x20))` 0x82950B3C), `bl GuiElement_SetOpacity` 0x82950B40; J+12 keeps the bool
/// (0x82950B44). Its callers: the build above (hidden) and SetJobMultiplierLevel's sub_82950B60
/// from level 0 (0x82950C34, shown). [[midasm_hook]] on the `bl` [r3, r4, f1]: `@Ui.Opacity`.
void RefiiF3MultiplierOpacity(PPCRegister& element, PPCRegister& presentation, PPCRegister& opacity) {
  StateCallOpacity(element.u32, presentation.u32, static_cast<float>(opacity.f64), "82950AF0");
}

/// THE HUD LAYER FADE sub_82B14A50(rec, GUI): while rec+4 is set (0x82B14AB8) it fades one of
/// Layer's children — `*(*(H+0x24)+4)[rec+8]` (0x82B14AC4..0x82B14AF0; NPCHighlights, ABXY,
/// SafeFrame, WorldIcons, FillWorldIcon, the two off-screen markers, filled by sub_82675AE0) —
/// to the opacity sub_82B149E8 derives from rec+0, in the HUD (`lwz r4,0(r9)` 0x82B14B1C), `bl
/// GuiElement_SetOpacity` 0x82B14B28. Hud_Update (0x82259BD8) drives it from the map at
/// `*(H+0x28)+4`, which no code found statically inserts into. [[midasm_hook]] on the `bl`
/// [r3, r4, f1]: `@Ui.Opacity(pres, child, path, f1, "82B14A50")`.
void RefiiF3LayerFade(PPCRegister& element, PPCRegister& presentation, PPCRegister& opacity) {
  StateCallOpacity(element.u32, presentation.u32, static_cast<float>(opacity.f64), "82B14A50");
}

/// THE OVERLAY BARS' TOPBOX SLIDE, sub_825C6D78(IM, on): for the bumper bar IM+32 (vtable
/// 0x820D1CD8, stored 0x825C40AC) and the trigger bar IM+36 (vtable 0x820D1D50, stored 0x825C4100),
/// each while shown (+26, `lbz r10,0x1A(r11)` 0x825C6DA0 / 0x825C6DE4): `SetState(bar+8, bar+4,
/// on ? 2 TopBoxOn : 3 TopBoxOff)` — `li r5,2` / `li r5,3` 0x825C6DC4..0x825C6DD0, `bl` 0x825C6DD4;
/// 0x825C6E08..0x825C6E14, `bl` 0x825C6E18. Its one caller is the TopBox publisher sub_8217D880:
/// off when the channels empty (0x8217D98C), on when a message goes up (0x8217D9D8), both behind
/// IM+24 (`lwz r9,0x18(r3)` 0x8217D97C). [[midasm_hook]] on each `bl` [r3, r4, r5]: `@Ui.State`.
void RefiiF3OverlayBarTopBox(PPCRegister& element, PPCRegister& presentation, PPCRegister& slide) {
  StateCallSlide(element.u32, presentation.u32, static_cast<int32_t>(slide.u32), "825C6D78");
}

/// THE MINI-GAME COUNTER, sub_826ECB18(bar, player 1..4, LHString* text) (GUI.SetMiniGameCounter
/// 0x8252301C, the bar's own 0x825C5B24): `Bar.Elements.Text.Counter<n>` (dword_8355A0B8 + "%d",
/// 0x826ECB4C..0x826ECB60) resolved in bar+4 (0x826ECB80) goes to slide 2 — `li r5,2` 0x826ECB8C,
/// `lwz r4,4(r31)` 0x826ECB90, `bl GuiElement_SetState` 0x826ECB94; its `.Counter.Text` text is the
/// commit midasm at 0x826ECBCC (headless_commit_hooks.cpp). [[midasm_hook]] on the `bl` [r3, r4, r5].
void RefiiF3MiniGameCounterSlide(PPCRegister& element, PPCRegister& presentation, PPCRegister& slide) {
  StateCallSlide(element.u32, presentation.u32, static_cast<int32_t>(slide.u32), "826ECB18");
}

/// THE DECORATION MODE'S BUTTON, sub_82961660(mode, button, tag, glyph) (ten calls from
/// CInteractionModeMenuDecoration_vf8 0x82B260D4..0x82B2634C): opacity 100 (flt_8206D268,
/// `lfs f1` 0x829616A0) onto the button (`mr r3,r28` 0x8296169C) in IM+0 (`mr r4,r31` 0x82961698),
/// `bl GuiElement_SetOpacity` 0x829616A4; its `Title` tag (SetTextTag 0x829616D4), glyph (SetTexture
/// 0x82961754) and show (sub_82819AE0 0x82961784) are stated by those writers' own hooks.
/// [[midasm_hook]] on the `bl` [r3, r4, f1]: `@Ui.Opacity`.
void RefiiF3DecorationButtonOpacity(PPCRegister& element, PPCRegister& presentation, PPCRegister& opacity) {
  StateCallOpacity(element.u32, presentation.u32, static_cast<float>(opacity.f64), "82961660");
}

/// THE WORLD MAP'S BUTTON, sub_82910D60(mode, CCharString* path, tag name, glyph) (the map menu
/// sub_82913610, 0x829138A8 ...): after offering it enabled through sub_82961340 (0x82910DD0, stated
/// as `@Interaction.Button`) it sets the path's element to the caller's f1 (`fmr f1,f31` 0x82910DFC)
/// in IM+0 (`mr r4,r31` 0x82910DF8), `bl GuiElement_SetOpacity` 0x82910E00. [[midasm_hook]] [r3, r4, f1].
void RefiiF3WorldMapButtonOpacity(PPCRegister& element, PPCRegister& presentation, PPCRegister& opacity) {
  StateCallOpacity(element.u32, presentation.u32, static_cast<float>(opacity.f64), "82910D60");
}

/// THE WORLD MAP'S REGION, sub_82911F08(map, CCharString* name, slide): the region's element, cached
/// by the name's FNV-1 at map+348 (0x82911F34..0x82912030), goes to the caller's slide (`mr r5,r28`
/// 0x82912034) in IM+0 (`mr r4,r30` 0x82912038), `bl GuiElement_SetState` 0x82912040 (five calls
/// from sub_82912CC0). [[midasm_hook]] on the `bl` [r3, r4, r5].
void RefiiF3WorldMapRegionSlide(PPCRegister& element, PPCRegister& presentation, PPCRegister& slide) {
  StateCallSlide(element.u32, presentation.u32, static_cast<int32_t>(slide.u32), "82911F08");
}

/// THE INTERACTION WORLD PANEL'S FLOOR, CInteractionModeGUIWorld_vf8 sub_82956EF0: when the
/// element's opacity reads under f31 (GetOpacity sub_82819560 0x82957458, `fcmpu` / `bge`
/// 0x8295745C..0x82957460) it is set to f31 (`fmr f1,f31` 0x82957468) on r30 in r24, `bl
/// GuiElement_SetOpacity` 0x82957470 — the call is the statement, made only when the guest's own
/// read decides it. [[midasm_hook]] on the `bl` [r3, r4, f1].
void RefiiF3GuiWorldOpacity(PPCRegister& element, PPCRegister& presentation, PPCRegister& opacity) {
  StateCallOpacity(element.u32, presentation.u32, static_cast<float>(opacity.f64), "82956EF0");
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
