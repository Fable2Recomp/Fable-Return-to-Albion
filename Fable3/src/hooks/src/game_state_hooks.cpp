// refii - Fable 3's game mode stacks, its GUI screen and its installed cultures, stated at their
// writers into the table both modules link (core/payload_bridge.h, src/runtime/src/core/
// stated_game_state.cpp); the host bridge (Fable3/src/hostbridge/src/level_feed.cpp,
// options_feed.cpp) reads only that table. Every hook runs the guest's body unchanged.
//
// Every address is the Fable 3 IDB; each offset names its base.
//
// THE MODE STACK. A game state's modes are a list S = `*(*(state+0x30)+4)` (state =
// `*(*0x83556540+0xC)`, the pause push at 0x825CEA54 and the run at 0x826ED3C8..0x826ED3D0):
// S+0 the sentinel node, S+4 the depth, a node {+0, +4 links, +8 the mode, +12 its count}; the TOP
// is `*(*(S+0)+4)`'s mode, the one the game loop runs (0x821CBE8C..0x821CBEA4). Its writers:
//   - push sub_8240E7A0(S, shared_ptr<mode>*): the insert sub_8238CFD0 links the new node at the
//     sentinel's +4 (0x8238D06C) and bumps S+4 (0x8238D068), so the pushed mode is the top; the
//     old top's slot 8 runs first (0x8240E7E0), the new one's slot 5 after (0x8240E804).
//   - pop sub_8240E820(S, id): the top when its slot 1 (the mode's id) answers `id` (0x8240E860),
//     else the first mode below it that does (0x8240E918), erased through sub_825EBC88.
//   - the run sub_8240E958(S), per game update (0x826ED3D4): it unlinks every top whose slot 2
//     says it is finished (0x8240E9C8..0x8240EA2C) and runs the first that is not.
// Each is stated as it returns: the top's mode, its vtable and the depth. The GUI's pause mode
// (CGameModePause, ctor sub_824B3B68: vtable 0x82091988 at 0x824B3B94; id 1, slot 1 sub_82C13A28
// `li r3,1`) is pushed by lhGuiManager_EnterGuiMode (0x825CEA54) and popped by id 1 by
// lhGuiManager_ExitScreen (0x825CEB8C) and the credits' end (0x825CF328).
//
// THE GUI SCREEN, `*(SM+4)`, SM = `*(GUI+0x38)`, GUI = `*0x83556490`. Its writers, every
// `lwz rN,0x38(..)` followed by `stw ..,4(rN)` in the image:
//   - the screen set sub_825CE590(G, id, quiet): `stw r28,4(r10)` at 0x825CE900, r28 =
//     the id argument;
//   - lhGuiManager_ExitScreen: 32 at 0x825CED34;
//   - the download message box sub_825CEEB0: sets 0 through sub_825CE590 (0x825CEF00) and puts
//     the previous id back at 0x825CF02C;
//   - the credits' end ScreenManager_EndCredits: 0x825CF35C, 0x825CF46C (its hook is
//     end_screen_hooks.cpp's, which states it through StateGuiScreen below).
// The set is stated from its argument; the others as they return, the field they wrote.
//
// THE CULTURES, Fable 2's table at Fable 3's addresses: rows of 96 bytes at *0x832A0AC8, the
// count at 0x832A0ACC, the row in use at 0x832A0AD0. Each row leads with its code (copied by
// sub_82DB5ED0(0, i, row, 85): 0x822E9148 / 0x822E8CB4) and holds its UTF-16 display name at
// `*(row+88)` (0x822E9158 / 0x822E8CC4; read as a `wchar_t*` by the Options page,
// sub_8229B650(&s, *(row+88)) at 0x825CDB38..0x825CDB40). Its writers:
//   - the boot build sub_822E8FC8: frees, counts, fills, and takes the "en-uk" row as the one in
//     use (0x822E918C);
//   - the refresh sub_822E8A88 (sub_822E9B88, CGUIGameComponent's ctor): the same rebuild,
//     keeping the code in use (0x822E8CFC), else selecting the en-uk row through the switch
//     (0x822E8D60);
//   - the switch sub_822E8D70(index): `index < count` (0x822E8D98) makes it the row in use
//     (0x822E8DE8).
// The builds are stated as they return, the table they wrote; the switch from its argument.

#include <atomic>
#include <cstdint>
#include <string>

#include <rex/logging.h>
#include <rex/ppc.h>

#include "box_answers.h"
#include "core/payload_bridge.h"
#include "gui_statements.h"

REX_EXTERN(__imp__rex_GameModeStack_Push);
REX_EXTERN(__imp__rex_GameModeStack_Pop);
REX_EXTERN(__imp__rex_GameModeStack_Run);
REX_EXTERN(__imp__rex_GuiManager_SetScreen);
REX_EXTERN(__imp__rex_GuiManager_ExitScreen);
REX_EXTERN(__imp__rex_GuiManager_DownloadBox);
REX_EXTERN(__imp__rex_Cultures_Build);
REX_EXTERN(__imp__rex_Cultures_Refresh);
REX_EXTERN(__imp__rex_Culture_Select);

namespace {

using refii::fable3::AppendUtf8;
using refii::fable3::LoadGuestU16;
using refii::fable3::LoadGuestU32;
using refii::fable3::Plausible;

constexpr uint32_t kGuiManager = 0x83556490u;
constexpr uint32_t kGuiScreens = 0x38u;
constexpr uint32_t kScreenId = 0x4u;
constexpr uint32_t kGuiPauseMode = 0x82091988u;

constexpr uint32_t kCultureTable = 0x832A0AC8u;
constexpr uint32_t kCultureCount = 0x832A0ACCu;
constexpr uint32_t kCultureCurrent = 0x832A0AD0u;
constexpr uint32_t kCultureStride = 96u;
constexpr uint32_t kCultureCode = 85u;
constexpr uint32_t kCultureName = 88u;
constexpr uint32_t kMaxCultures = 64u;

uint32_t Word(const uint8_t* base, uint32_t at) { return Plausible(at) ? LoadGuestU32(base, at) : 0u; }

/// The top of mode stack `stack` as its writer left it.
void StateStack(const uint8_t* base, uint32_t stack) {
  if (!Plausible(stack)) {
    return;
  }
  const uint32_t depth = LoadGuestU32(base, stack + 4u);
  const uint32_t top = depth != 0u ? Word(base, Word(base, Word(base, stack) + 4u) + 8u) : 0u;
  const uint32_t vtable = Word(base, top);
  refii_bridge_mode_stack_stated(stack, top, vtable, depth);

  static std::atomic<uint64_t> last{~0ull};
  const uint64_t key = (static_cast<uint64_t>(stack) << 32) | vtable;
  if (last.exchange(key) != key) {
    REXLOG_INFO("[fable3-modes] stack {:08X}: top {:08X} (vtable {:08X}{}), depth {}", stack, top, vtable,
                vtable == kGuiPauseMode ? ", the GUI's pause" : "", depth);
  }
}

void StateScreen(uint32_t id) {
  refii_bridge_gui_screen_stated(id);
  static std::atomic<uint32_t> last{~0u};
  if (last.exchange(id) != id) {
    REXLOG_INFO("[fable3-modes] GUI screen {}", id);
  }
}

/// The UTF-16BE string at `wide` as UTF-8, bounded.
std::string ReadWide(const uint8_t* base, uint32_t wide) {
  std::string text;
  for (uint32_t i = 0; Plausible(wide) && i < 128u; ++i) {
    uint32_t unit = LoadGuestU16(base, wide + (i * 2u));
    if (unit == 0u) {
      break;
    }
    if (unit >= 0xD800u && unit < 0xDC00u) {
      const uint32_t low = LoadGuestU16(base, wide + ((i + 1u) * 2u));
      if (low < 0xDC00u || low >= 0xE000u) {
        break;
      }
      unit = 0x10000u + ((unit - 0xD800u) << 10) + (low - 0xDC00u);
      ++i;
    } else if (unit >= 0xDC00u && unit < 0xE000u) {
      break;
    }
    AppendUtf8(text, unit);
  }
  return text;
}

/// The culture table as a build left it.
void StateCultures(const uint8_t* base, const char* writer) {
  const uint32_t table = LoadGuestU32(base, kCultureTable);
  uint32_t count = LoadGuestU32(base, kCultureCount);
  const int32_t current = static_cast<int32_t>(LoadGuestU32(base, kCultureCurrent));
  if (!Plausible(table) || count > kMaxCultures) {
    count = 0u;
  }
  std::string codes;
  std::string names;
  for (uint32_t i = 0; i < count; ++i) {
    const uint32_t row = table + (i * kCultureStride);
    for (uint32_t c = 0; c < kCultureCode && base[row + c] != 0u; ++c) {
      codes.push_back(static_cast<char>(base[row + c]));
    }
    codes.push_back('\0');
    names += ReadWide(base, LoadGuestU32(base, row + kCultureName));
    names.push_back('\0');
  }
  refii_bridge_cultures_stated(count, current, codes.data(), static_cast<uint32_t>(codes.size()), names.data(),
                               static_cast<uint32_t>(names.size()));
  char code[kCultureCode + 1u]{};
  REXLOG_INFO("[fable3-culture] {}: {} cultures, row {} ('{}')", writer, count, current,
              refii_bridge_culture_code_read(code, sizeof(code)) != 0u ? code : "");
}

}  // namespace

namespace refii::fable3 {

void StateGuiScreen(const uint8_t* base) {
  const uint32_t screens = Word(base, Word(base, kGuiManager) + kGuiScreens);
  if (Plausible(screens)) {
    StateScreen(LoadGuestU32(base, screens + kScreenId));
  }
}

}  // namespace refii::fable3

// (S, shared_ptr<mode>*).
REX_EXTERN(rex_GameModeStack_Push) {
  const uint32_t stack = ctx.r3.u32;
  __imp__rex_GameModeStack_Push(ctx, base);
  StateStack(base, stack);
}

// (S, id).
REX_EXTERN(rex_GameModeStack_Pop) {
  const uint32_t stack = ctx.r3.u32;
  __imp__rex_GameModeStack_Pop(ctx, base);
  StateStack(base, stack);
}

// (S): the stack's per-update run.
REX_EXTERN(rex_GameModeStack_Run) {
  const uint32_t stack = ctx.r3.u32;
  __imp__rex_GameModeStack_Run(ctx, base);
  StateStack(base, stack);
}

// (G, id, quiet): the screen id is the argument (0x825CE900).
REX_EXTERN(rex_GuiManager_SetScreen) {
  const uint32_t id = ctx.r4.u32;
  __imp__rex_GuiManager_SetScreen(ctx, base);
  StateScreen(id);
}

// (G).
REX_EXTERN(rex_GuiManager_ExitScreen) {
  __imp__rex_GuiManager_ExitScreen(ctx, base);
  refii::fable3::StateGuiScreen(base);
}

// (G, text LHString, download): puts the previous screen id back as it returns. When G+36 was 1
// the box came up (+36 = 2 at 0x825CEEE4): stated with the text and the download flag, read
// before the call, which destroys the text as it returns (after 0x825CF02C).
REX_EXTERN(rex_GuiManager_DownloadBox) {
  const uint32_t manager = ctx.r3.u32;
  const bool takes = base != nullptr && Plausible(manager) && LoadGuestU32(base, manager + 36u) == 1u;
  const std::string text = takes ? refii::fable3::ReadLhString(base, ctx.r4.u32) : std::string();
  const bool download = (ctx.r5.u32 & 0xFFu) != 0u;
  __imp__rex_GuiManager_DownloadBox(ctx, base);
  refii::fable3::StateGuiScreen(base);
  if (takes) {
    refii::fable3::StateModalMessageBox(manager, text, download);
  }
}

// The boot build of the culture table.
REX_EXTERN(rex_Cultures_Build) {
  __imp__rex_Cultures_Build(ctx, base);
  StateCultures(base, "build");
}

// (force): the rebuild.
REX_EXTERN(rex_Cultures_Refresh) {
  __imp__rex_Cultures_Refresh(ctx, base);
  StateCultures(base, "refresh");
}

// (index).
REX_EXTERN(rex_Culture_Select) {
  const uint32_t index = ctx.r3.u32;
  const uint32_t count = LoadGuestU32(base, kCultureCount);
  __imp__rex_Culture_Select(ctx, base);
  if (index < count) {
    refii_bridge_culture_selected(index);
    char code[kCultureCode + 1u]{};
    REXLOG_INFO("[fable3-culture] select row {} ('{}')", index,
                refii_bridge_culture_code_read(code, sizeof(code)) != 0u ? code : "");
  }
}
