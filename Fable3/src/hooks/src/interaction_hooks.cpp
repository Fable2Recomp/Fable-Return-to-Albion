// refii - Fable 3's interaction mode, stated at its own writers for Godot's interaction document:
// the mode set, its close, and the info panel id the panel's slide is.
//
// Every address is the Fable 3 IDB; each offset names its base.
//
// THE INTERACTION MODE IM (`*(GUI+0x48)`, GUI = *0x83556490). The mode set
// sub_825C4348(IM, mode, a3, a4, table, a6) — GUI.OpenInteractionMode and the C++ openers — clears
// the old mode through sub_825C3F00 (0x825C44B4; IM+64 = 1 at 0x825C400C), stores IM+20 = mode
// (0x825C458C) and the mode's object at IM+24 (0x825C4630; 0 for a mode it does not build,
// 0x825C4838). Its close sub_825C5D08(IM, keepTarget) — the tick on IM+6 (0x825C3DAC) and the
// modes' own exits — drops IM+24 (0x825C5FCC) and rests the panel at 1 (0x825C62F0); IM+20 is
// left as it was, so the guest's own read-backs (GUI.GetEntityPlayerIsInteractingWith…
// 0x82522D10, GUI.IsInInteractionModeMenuDecoration 0x82523644) test IM+24 first. Both run as
// the guest has them.
//
// THE INFO PANEL is IM+64: sub_82963040(mode object, id) follows the object's IM (`*(obj+20)`,
// 0x8296304C) and stores the id (0x8296307C) with IM+9 = 1 (0x82963078); the screen tick then
// writes it as `LowerElements.InfoBox`'s state (dword_835588F4, sub_8227DA00 at 0x825C3B14) —
// 1 closed, 5 NPC, 6 weapon, 9 save, 7 world map, 13/15 the Lua text panels, … Its InfoBoxOpen
// toggle (id > 1, 0x829630B0) is a registry write hud_hooks.cpp states. The id is stated here
// at each of its three writers: the set and close as IM+64 stands when they return, the stager
// as its r4 asks (its own store is behind IM+0), the clear as IM+64 stands when it returns
// (`stw r27,0x40(r29)` 0x825C400C, 1), and the modes that store IM+64 themselves on their stores
// (`RefiiF3InteractionPanelStored`, the value register, IM = `lwz r11,0x14(r31)`): decoration vf1
// 0x82B25998 / 0x82B259B8 (r10 = 2), 0x82B259CC / 0x82B259E0 (r30), vf2 0x82B25DA0 (r10 = 1),
// sub_82B2AB28 0x82B2AC98 (r9 = 13), the shop's vf1 0x82B2E7B0 (r26), the pawn shop's 0x82B36704
// (r30): `@Interaction.Panel(id)`.
//
// THE ROOT. The tick sub_825C3A78(IM) writes InteractionMode.bgf's root (`GuiPresentation_Root(IM+0)`)
// on the edges of two IM flags it owns: IM+7 (a screen over the mode — sub_821F8758, the GUI's
// +28 -> +56 flag, or screen 11/12/13/20 on top, 0x825C3C50..0x825C3CB0) set 0 -> 1 states 2
// (`stb r27,7(r31)` 0x825C3EAC, SetState 0x825C3EC8); cleared once the root's own check passes
// (sub_828197A0 at 0x825C3CE8, `stb r28,7(r31)` 0x825C3CF8) states 1 (0x825C3D10); then IM+8
// (sub_82277308 of the GUI's +28 -> +48 at 0x825C3D20) set states 2 (`stb r27,8(r31)` 0x825C3D40,
// 0x825C3D5C) and cleared states 1 (`stb r28,8(r31)` 0x825C3D70, 0x825C3D8C). Each write is behind
// IM+0 (0x825C3EB4, 0x825C3D48, 0x825C3D78). The flags are the IM's, read on either side of the
// tick, and the edges are replayed in the tick's order: `@Ui.State(pres, root, "Scene", slide)`.
// Slide 1 is the file's `Main` (LowerElements at 100), 2 its `Slide1` (both layers fade out).
//
// THE CLEAR sub_825C3F00(IM) — the mode set's (0x825C44B4) and four other openers' — loads the
// presentation when IM+0 is empty (sub_825C42C0 at 0x825C3F1C), activates the root unless it is
// (sub_828196E0(root, pres, 1, 0) at 0x825C3F60, stated by the active setter's own hook) and puts it
// on slide 1 (SetState 0x825C3FFC) without touching IM+7 / IM+8: `@Ui.State(…, "Scene", 1)` after it.
// The tick's countdown IM+16 deactivates the root again at 1 (0x825C3B58), the same setter.
//
// THE HOLD ICON sub_82961278(mode, button, hold) — the hold tick sub_82961D58 (0x82961E0C) and
// the slot reset sub_8295A0D0 (0x8295A13C) — puts the button's `Icon` (dword_8355C364) and its
// `NewTab.Foreground.LeftSection` (dword_8355C378) on 2 `FillButton` for a hold, else 1
// `NormalButton` (0x829612D0..0x829612DC; SetState 0x829612E8 / 0x82961318), behind `**(mode+20)`
// and the button (0x829612A4), then shows the button (0x82961334, a toggle already stated): both
// slides stated at entry as `@Ui.State` on the button's path. The tick reads the Icon's state
// back (GetState 0x82961DEC == 2 arms the hold), which the headless element answers itself.
//
// THE TRIGGER AND BUMPER BARS, COverlayButtonsBase (vtables 0x820D1CD8 `UpperElements.BumperButtons`
// and 0x820D1D50 `UpperElements.TriggerButtons`, their element tables built by vf5 sub_82B18EF0 /
// sub_82B19150: +8 root, +12 `.Text`, +16 / +20 the two `_Complete` sides, +4 the presentation).
// vf0 sub_82B3EA18(bar, shown) does nothing when `shown` equals +26 (0x82B3EA3C / beq 0x82B3EA40);
// else it sets the root to 1 hidden (0x82B3EAE4) or, shown, 2 `TopBoxOn` while a TopBox channel holds
// a message and neither of the TopBox manager's +0 / +1 bytes is set, 3 `TopBoxOff` otherwise — the
// manager `*(*(GUI+32)+76)` (0x82B3EA58 / 0x82B3EA5C), its channels +76..+80 each empty when
// `(end - begin) & ~3` is 0 (0x82B3EA70..0x82B3EA88), the bytes at 0x82B3EAA8 / 0x82B3EAB4 —
// SetState(+8, +4, slide) at 0x82B3EAF0, +26 stored at 0x82B3EAF4. The slide is stated as that
// body decides it: `@Ui.State`. vf1 sub_82B3EB28(bar, LHString*) commits the text straight into
// `.Text`'s box (`b GuiTextBox_Commit` 0x82B3EB58, behind +12 / +4 / the box) — the localised label
// GUI.Set*Text built from its tag name (sub_82516418 -> sub_823A9728 at 0x825164B4): `@Ui.Text`. The
// sides (vf3 -> sub_82819AE0 at 0x82B3EB9C) and the tag form (vf2 -> SetTextTag at 0x82B3EB1C) are
// the generic writers' statements already.

#include <atomic>
#include <cstdint>
#include <string>

#include <rex/logging.h>
#include <rex/ppc.h>

#include "gui_statements.h"

REX_EXTERN(__imp__rex_InteractionMode_Set);
REX_EXTERN(__imp__rex_InteractionMode_Close);
REX_EXTERN(__imp__rex_InteractionMode_SetPanel);
REX_EXTERN(__imp__rex_InteractionMode_Tick);
REX_EXTERN(__imp__rex_InteractionMode_Clear);
REX_EXTERN(__imp__rex_InteractionButton_Hold);
REX_EXTERN(__imp__rex_OverlayBar_Show);
REX_EXTERN(__imp__rex_OverlayBar_CommitText);

namespace {

using namespace refii::fable3;

constexpr uint32_t kImMode = 20u;
constexpr uint32_t kImObject = 24u;
constexpr uint32_t kImPanel = 64u;
/// The mode object's IM (sub_82963040 at 0x8296304C).
constexpr uint32_t kObjectIm = 20u;
/// The panel id the IM is built and closed with (0x825C3698, 0x825C62F0).
constexpr int32_t kPanelClosed = 1;

constexpr uint32_t kImPresentation = 0u;
constexpr uint32_t kImScreenOver = 7u;
constexpr uint32_t kImHiddenByGui = 8u;
constexpr uint32_t kGuiManager = 0x83556490u;
constexpr uint32_t kGuiTopBoxOwner = 32u;  // GUI+32
constexpr uint32_t kTopBoxManager = 76u;   // *(GUI+32)+76
constexpr uint32_t kTopBoxChannels = 76u;  // M+76 .. M+80
constexpr uint32_t kBarPresentation = 4u;
constexpr uint32_t kBarRoot = 8u;
constexpr uint32_t kBarText = 12u;
constexpr uint32_t kBarShown = 26u;

std::atomic<int32_t> g_panel{kPanelClosed};

void StatePanelId(int32_t panel) {
  if (g_panel.exchange(panel) == panel) {
    return;
  }
  Statement("@Interaction.Panel").Add(Number(static_cast<float>(panel))).Post();
}

void StatePanel(const uint8_t* base, uint32_t im) {
  if (base == nullptr || !Plausible(im)) {
    return;
  }
  const int32_t panel = static_cast<int32_t>(LoadGuestU32(base, im + kImPanel));
  if (g_panel.exchange(panel) == panel) {
    return;
  }
  Statement("@Interaction.Panel").Add(Number(static_cast<float>(panel))).Post();
}

}  // namespace

// (IM, mode, a3, a4, table, a6).
REX_EXTERN(rex_InteractionMode_Set) {
  const uint32_t im = ctx.r3.u32;
  const uint32_t mode = ctx.r4.u32;
  __imp__rex_InteractionMode_Set(ctx, base);
  if (base == nullptr || !Plausible(im)) {
    return;
  }
  if (LoadGuestU32(base, im + kImMode) == mode) {
    REXLOG_INFO("Refii interaction mode: mode {} ({})", mode,
                LoadGuestU32(base, im + kImObject) != 0u ? "open" : "no mode object");
  }
  StatePanel(base, im);
}

// (IM, keepTarget).
REX_EXTERN(rex_InteractionMode_Close) {
  const uint32_t im = ctx.r3.u32;
  const bool open = base != nullptr && Plausible(im) && LoadGuestU32(base, im + kImObject) != 0u;
  __imp__rex_InteractionMode_Close(ctx, base);
  if (open) {
    REXLOG_INFO("Refii interaction mode: mode {} closed", LoadGuestU32(base, im + kImMode));
  }
  StatePanel(base, im);
}

// (mode object, id): the id as asked, at entry — its store `stw r4,0x40(r11)` (0x8296307C) sits
// behind the IM's presentation (`lwz r29,0(r11)` 0x8296305C / beq 0x82963064), so IM+64 read
// after the call is the id only while InteractionMode.bgf is built.
REX_EXTERN(rex_InteractionMode_SetPanel) {
  const uint32_t object = ctx.r3.u32;
  const int32_t panel = static_cast<int32_t>(ctx.r4.u32);
  const uint32_t im = base != nullptr && Plausible(object) ? LoadGuestU32(base, object + kObjectIm) : 0u;
  __imp__rex_InteractionMode_SetPanel(ctx, base);
  if (Plausible(im)) {
    StatePanelId(panel);
  }
}

namespace {

/// The element's name as the element hooks know it; empty when nothing named it.
std::string PathOf(uint32_t element) {
  uint32_t presentation = 0u;
  std::string path;
  return Plausible(element) && ElementName(element, presentation, path) ? path : std::string();
}

/// The bar's shown slide as vf0 decides it (0x82B3EA4C..0x82B3EAD4).
int32_t BarShownSlide(const uint8_t* base) {
  const uint32_t gui = LoadGuestU32(base, kGuiManager);
  const uint32_t owner = Plausible(gui) ? LoadGuestU32(base, gui + kGuiTopBoxOwner) : 0u;
  const uint32_t manager = Plausible(owner) ? LoadGuestU32(base, owner + kTopBoxManager) : 0u;
  if (!Plausible(manager)) {
    return 3;
  }
  const uint32_t begin = LoadGuestU32(base, manager + kTopBoxChannels);
  const uint32_t end = LoadGuestU32(base, manager + kTopBoxChannels + 4u);
  bool queued = false;
  for (uint32_t at = begin; Plausible(at) && at < end && (end - at) <= 0x400u; at += 4u) {
    const uint32_t channel = LoadGuestU32(base, at);
    if (Plausible(channel) &&
        ((LoadGuestU32(base, channel + 8u) - LoadGuestU32(base, channel + 4u)) & ~3u) != 0u) {
      queued = true;
      break;
    }
  }
  if (!queued) {
    return 3;
  }
  return base[manager] != 0u || base[manager + 1u] != 0u ? 3 : 2;
}

}  // namespace

// (IM): the root's slide on each flag edge the tick made, in its order.
REX_EXTERN(rex_InteractionMode_Tick) {
  const uint32_t im = ctx.r3.u32;
  const bool readable = base != nullptr && Plausible(im);
  const uint8_t over = readable ? base[im + kImScreenOver] : 0u;
  const uint8_t hidden = readable ? base[im + kImHiddenByGui] : 0u;
  __imp__rex_InteractionMode_Tick(ctx, base);
  if (!readable) {
    return;
  }
  const uint32_t presentation = LoadGuestU32(base, im + kImPresentation);
  if (presentation == 0u) {
    return;
  }
  const uint8_t over_now = base[im + kImScreenOver];
  const uint8_t hidden_now = base[im + kImHiddenByGui];
  int32_t slide = 0;
  if (over == 0u && over_now != 0u) {
    slide = 2;
  } else {
    if (over != 0u && over_now == 0u) {
      slide = 1;
    }
    if (hidden == 0u && hidden_now != 0u) {
      slide = 2;
    } else if (hidden != 0u && hidden_now == 0u) {
      slide = 1;
    }
  }
  if (slide != 0) {
    StateState(presentation, 0u, "Scene", slide, "825C3A78");
  }
}

// (IM): the root on slide 1.
REX_EXTERN(rex_InteractionMode_Clear) {
  const uint32_t im = ctx.r3.u32;
  __imp__rex_InteractionMode_Clear(ctx, base);
  if (base == nullptr || !Plausible(im)) {
    return;
  }
  const uint32_t presentation = LoadGuestU32(base, im + kImPresentation);
  if (presentation != 0u) {
    StateState(presentation, 0u, "Scene", 1, "825C3F00");
  }
  StatePanel(base, im);
}

/// A mode's own IM+64 store (the value register at the `stw rX,0x40(r11)`).
void RefiiF3InteractionPanelStored(PPCRegister& value) { StatePanelId(static_cast<int32_t>(value.u32)); }

// (mode, button, hold).
REX_EXTERN(rex_InteractionButton_Hold) {
  const uint32_t mode = ctx.r3.u32;
  const uint32_t button = ctx.r4.u32;
  if (base != nullptr && Plausible(mode) && Plausible(button)) {
    const uint32_t im = LoadGuestU32(base, mode + kObjectIm);
    const uint32_t presentation = Plausible(im) ? LoadGuestU32(base, im + kImPresentation) : 0u;
    const std::string path = PathOf(button);
    if (presentation != 0u && !path.empty() && path[0] != '#') {
      const int32_t slide = (ctx.r5.u32 & 0xFFu) != 0u ? 2 : 1;
      StateState(presentation, 0u, path + ".Icon", slide, "82961278");
      StateState(presentation, 0u, path + ".NewTab.Foreground.LeftSection", slide, "82961278");
    }
  }
  __imp__rex_InteractionButton_Hold(ctx, base);
}

// (bar, shown).
REX_EXTERN(rex_OverlayBar_Show) {
  const uint32_t bar = ctx.r3.u32;
  const uint8_t shown = static_cast<uint8_t>(ctx.r4.u32 & 0xFFu);
  if (base != nullptr && Plausible(bar) && base[bar + kBarShown] != shown) {
    const uint32_t presentation = LoadGuestU32(base, bar + kBarPresentation);
    const uint32_t root = LoadGuestU32(base, bar + kBarRoot);
    const std::string path = PathOf(root);
    if (Plausible(presentation) && !path.empty()) {
      StateState(presentation, root, path, shown != 0u ? BarShownSlide(base) : 1, "82B3EA18");
    }
  }
  __imp__rex_OverlayBar_Show(ctx, base);
}

// (bar, LHString*).
REX_EXTERN(rex_OverlayBar_CommitText) {
  const uint32_t bar = ctx.r3.u32;
  if (base != nullptr && Plausible(bar)) {
    const uint32_t presentation = LoadGuestU32(base, bar + kBarPresentation);
    const uint32_t text = LoadGuestU32(base, bar + kBarText);
    const std::string path = PathOf(text);
    if (Plausible(presentation) && !path.empty()) {
      StateText(presentation, text, path, ReadLhString(base, ctx.r4.u32), "82B3EB28");
    }
  }
  __imp__rex_OverlayBar_CommitText(ctx, base);
}
