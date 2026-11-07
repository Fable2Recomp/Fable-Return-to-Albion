// refii - Fable 3's epilogue (quest completion) and credits screens, stated as the screen manager
// opens and closes them, for Godot's Fable3EndScreens. Neither is a box-manager box: they are
// screen-manager screens (SM = *(GUI+0x1C), GUI = *0x83556490), keyed in SM+64 by the .bgf id
// (6 questcompletion, 8 creditsscreen; built in sub_825CDEE0 at 0x825CE2A0 / 0x825CE330), so no
// @Box.Open names them. Their texts and positions are element writes the element hooks already
// state; what only these calls state is that the screen is up, which presentation is it, and the
// epilogue's image.
//
// EPILOGUE E = SM[6] (28 bytes, vtable off_8209D2FC). Show sub_8276B460(E, title, description,
// renown, image, flag) — QuestTracker.ShowEpilogueScreen through sub_824F0CE8 (0x824F0E8C) and the
// co-op net handler sub_82370150 (0x82373E54) — returns at once unless E+0x14 is clear
// (`lbz r11,0x14(r3)` 0x8276B46C), sets it (0x8276B498), loads the screen (sub_825C79A0(*(GUI+0x38),
// 6) at 0x8276B4A8) and attaches its presentation *(*(GUI+0x38)+0x20) (0x8276B4B4) to the HUD
// holder (sub_826757E0 at 0x8276B4CC). The image is r7, a CCharString handle (`lwz r11,0(r26)` /
// `lwz r4,0(r11)` at 0x8276B744..0x8276B750); it reaches the `ScreenShot` texture by a raw store
// (`stw r9,0x200(r11)` 0x8276B7F8), never SetTexture, so this is the only place it is stated.
// Hide sub_8276B9F8(E) acts only when E+0x14 is set (0x8276BA04..0x8276BA10) and clears it
// (0x8276BA1C).
//
// CREDITS. Start sub_825CF128(SM) — ShowCreditsScreen (0x8251D198) and the net handler
// (0x82374190) — runs the roll's init sub_82B3F980 only while SM+0x23 is clear (0x825CF1AC) and
// sets it (0x825CF264); the roll writes `Text.Text0..23` under the presentation
// *(*(GUI+0x38)+0x9C) (`lwz r6,0x38(r11)` / `lwz r28,0x9C(r6)` 0x82B3FB0C..0x82B3FB18). End
// sub_825CF280(SM) — the roll running out (0x82B3FF48), GUI_SELECT / GUI_CANCEL (0x82B3FFD0,
// 0x82B4001C) and the net handler (0x823741A4) — closes the screen and, with SM+0x23 set
// (0x825CF2F4), pops the credits component.

#include <cstdint>

#include <rex/ppc.h>
#include <rex/system/kernel_state.h>
#include <rex/system/xmemory.h>

#include "gui_statements.h"

REX_EXTERN(__imp__rex_Epilogue_Show);
REX_EXTERN(__imp__rex_Epilogue_Hide);
REX_EXTERN(__imp__rex_ScreenManager_StartCredits);
REX_EXTERN(__imp__rex_ScreenManager_EndCredits);

namespace {

using namespace refii::fable3;

constexpr uint32_t kGuiManager = 0x83556490u;
/// GUI+0x38: the screen loader. sub_825C79A0(loader, id) keeps a screen in slot `4 * (id + 2)`
/// (0x825C7A34), or — for the ids sub_825C7F90 picks — in the one exclusive slot +0x9C with the id
/// at +0x98 (0x825C79D4..0x825C79DC). The credits (id 8) load just before the start
/// (ShowCreditsScreen 0x8251D178), so at the start +0x9C is theirs.
constexpr uint32_t kGuiScreenLoader = 0x38u;
/// The loader's epilogue slot (id 6, 0x8276B4B4) and its exclusive slot (0x82B3FB18).
constexpr uint32_t kEpiloguePresentation = 0x20u;
constexpr uint32_t kCreditsPresentation = 0x9Cu;
constexpr uint32_t kEpilogueOpen = 0x14u;
constexpr uint32_t kCreditsRunning = 0x23u;
/// The epilogue's title and description text names, FNV-1 words (0x8276B500 / 0x8276B524).
constexpr uint32_t kEpilogueTitleName = 0x8355AC24u;
constexpr uint32_t kEpilogueDescriptionName = 0x8355AC28u;
/// The screenshot's ImageName registry name, an FNV-1 word (0x8276B798).
constexpr uint32_t kEpilogueImageName = 0x8355AC44u;

uint32_t LoaderWord(const uint8_t* base, uint32_t offset) {
  const uint32_t gui = LoadGuestU32(base, kGuiManager);
  if (!Plausible(gui)) {
    return 0u;
  }
  const uint32_t loader = LoadGuestU32(base, gui + kGuiScreenLoader);
  return Plausible(loader) ? LoadGuestU32(base, loader + offset) : 0u;
}

}  // namespace

/// `@Epilogue.Open(presentation, image, title, description, renown, titleName, descriptionName)`
/// when the show took (E+0x14 clear before, set after). The show keeps its arguments (`mr r28,r4;
/// mr r27,r5; mr r29,r6; mr r26,r7` 0x8276B470..0x8276B47C) and writes them only behind the
/// screen's presentation (`lwz r30,0x20(r10)` 0x8276B4B4 / beq 0x8276B4BC): the title into the
/// text registry under `*dword_8355AC24` (r5 = r28, 0x8276B4F8..0x8276B510), the description
/// under `*dword_8355AC28` (r5 = r27, 0x8276B51C..0x8276B534); the renown r6 is formatted into
/// AC30 / AC34 (0x8276B598..0x8276B5EC). The names' hashes are read from those words (their
/// static initialisers store the FNV-1, sub_831C7360 / sub_831C73A0).
REX_EXTERN(rex_Epilogue_Show) {
  const uint32_t epilogue = ctx.r3.u32;
  const bool closed = base != nullptr && Plausible(epilogue) && base[epilogue + kEpilogueOpen] == 0u;
  const std::string image = closed ? ReadCharString(base, ctx.r7.u32) : std::string();
  const std::string title = closed ? ReadLhString(base, ctx.r4.u32) : std::string();
  const std::string description = closed ? ReadLhString(base, ctx.r5.u32) : std::string();
  const int32_t renown = static_cast<int32_t>(ctx.r6.u32);
  __imp__rex_Epilogue_Show(ctx, base);
  if (closed && base[epilogue + kEpilogueOpen] != 0u) {
    Statement("@Epilogue.Open")
        .Add(Text(Hex(LoaderWord(base, kEpiloguePresentation))))
        .Add(Text(image))
        .Add(Text(title))
        .Add(Text(description))
        .Add(Number(static_cast<float>(renown)))
        .Add(Text(Hex(LoadGuestU32(base, kEpilogueTitleName))))
        .Add(Text(Hex(LoadGuestU32(base, kEpilogueDescriptionName))))
        .Post();
  }
}

/// THE SCREENSHOT. After `bl sub_826EFBD8` at 0x8276B788 (r3 = r1+0x58 out, r4 = r1+0x50 the
/// image's path from sub_82510688 at 0x8276B758, r5 = `*dword_8355AC58` "ScreenShot"), the word at
/// r1+0x58 is the texture hash the show then stores raw into +512 of every element the ImageName
/// registry (R+0x30, `lwz r3,0x30(r7)` 0x8276B7A4) files under `*dword_8355AC44` (`lwz r9,
/// 0xA0+var_48(r1)` 0x8276B7C4, `stw r9,0x200(r11)` 0x8276B7F8) — only through the element's node.
/// A [[midasm_hook]] at 0x8276B78C: `@Epilogue.Image(name hash, texture hash, path)`.
void RefiiF3EpilogueScreenshot(PPCRegister& stack) {
  rex::system::KernelState* state = rex::system::kernel_state();
  rex::memory::Memory* memory = state != nullptr ? state->memory() : nullptr;
  const uint8_t* base = memory != nullptr ? memory->virtual_membase() : nullptr;
  if (base == nullptr) {
    return;
  }
  Statement("@Epilogue.Image")
      .Add(Text(Hex(LoadGuestU32(base, kEpilogueImageName))))
      .Add(Text(Hex(LoadGuestU32(base, stack.u32 + 0x58u))))
      .Add(Text(ReadCharString(base, stack.u32 + 0x50u, 260u)))
      .Post();
}

/// `@Epilogue.Close` when the hide had an open screen to drop.
REX_EXTERN(rex_Epilogue_Hide) {
  const uint32_t epilogue = ctx.r3.u32;
  const bool open = base != nullptr && Plausible(epilogue) && base[epilogue + kEpilogueOpen] != 0u;
  __imp__rex_Epilogue_Hide(ctx, base);
  if (open) {
    Statement("@Epilogue.Close").Post();
  }
}

/// `@Credits.Open(presentation)` when the start ran the roll's init (SM+0x23 clear before).
REX_EXTERN(rex_ScreenManager_StartCredits) {
  const uint32_t manager = ctx.r3.u32;
  const bool idle = base != nullptr && Plausible(manager) && base[manager + kCreditsRunning] == 0u;
  __imp__rex_ScreenManager_StartCredits(ctx, base);
  if (idle) {
    Statement("@Credits.Open").Add(Text(Hex(LoaderWord(base, kCreditsPresentation)))).Post();
  }
}

/// `@Credits.Close` when the end popped a running roll (SM+0x23 set before).
REX_EXTERN(rex_ScreenManager_EndCredits) {
  const uint32_t manager = ctx.r3.u32;
  const bool running = base != nullptr && Plausible(manager) && base[manager + kCreditsRunning] != 0u;
  __imp__rex_ScreenManager_EndCredits(ctx, base);
  // Its stores to the GUI screen id (0x825CF35C, 0x825CF46C), stated as it returns.
  refii::fable3::StateGuiScreen(base);
  if (running) {
    Statement("@Credits.Close").Post();
  }
}
