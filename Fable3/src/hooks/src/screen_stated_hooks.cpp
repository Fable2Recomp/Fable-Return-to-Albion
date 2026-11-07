// refii - Fable 3's screens and HUD widgets stated at the calls that carry their values, for
// Godot's Fable3UiStatements: the HUD root's shown/hidden, the save menu, the Options screen's own
// query, the money alert, the objective list, the target names, the crime timer, the job
// multiplier and the box-manager screens' rows and records. Each of these writers puts its value
// on screen only behind a presentation (`*H` for the HUD, the box's sub_8291F4C8 for a box, the
// save menu's cached +20) — the statement is the value as the caller passed it, at the call.
//
// Every address is the Fable 3 disc IDB (port 13339); each offset names its base. GUI =
// *0x83556490, H = *(GUI+0x20) (the HUD), *H its presentation.
//
// THE HUD ROOT. hud.bgf's root (`Scene`) is written by three calls, none of them the interaction
// mode's tick sub_825C3A78 (that one writes InteractionMode.bgf's root, IM+0): HIDE sub_825C1CB0(H)
// sets 2 unless already 2 (GetState 0x825C1CE4, SetState 0x825C1D04) — every box's Show calls it;
// SHOW sub_825C1D28(force) sets 1 unless already 1 and not forced (0x825C1D54..0x825C1D88) — the
// boxes' hides, the HUD tick's enable (0x822599AC, forced), the cutscene rules (0x8249AA84) and
// GUI.HideSideInfoPanel (0x8251EF4C); the CUTSCENE RULES sub_8249A030(rules, a2, a3) end by
// setting 3 whenever their word `*rules` keeps bit 27 (0x08000000: `extrwi r10,r11,1,4`
// 0x8249B6A8, `li r5,3` / SetState 0x8249B6D8 / 0x8249B6DC). `@Hud.Root(state)` on each return.
//
// THE SAVE MENU (*(SM+56), SM = *(GUI+0x1C)): open sub_8296C5D0(menu, allowed, entity) stores
// +0 = 1 (0x8296C6A8), +1 = allowed (0x8296C6D8) and the cursor +12 = allowed ? 1 : 3
// (0x8296C6F4 / 0x8296C700) before its presentation block, and may refuse (0x8296C684); every
// cursor change funnels into sub_8296CB08(menu) with the row at +12 (0x8296CB14: 1 Save, 2 Save
// and Quit, 3 Quit, 4 Character Management); close sub_8296C858(menu) clears +0 (0x8296C874).
// `@SaveMenu.Open(allowed, row)`, `@SaveMenu.Row(row)`, `@SaveMenu.Close`.
//
// THE OPTIONS QUERY sub_825CBEC0(box, tag): root state 6, the question by tag into
// `.Group.QueryBox.Text` (dword_83558948, sub_82819328 at 0x825CBF08) and box+30 = 1
// (0x825CBF10) — all behind the box's presentation (sub_8291F4C8 at 0x825CBED4, beq 0x825CBEE0).
// The question is r4: `@Options.Query(box, tag)`.
//
// THE MONEY ALERT A = *(H+120) player one, *(H+124) player two (sub_825C2150): change
// sub_8281E560(A, old, delta, kingdom) — queued while A+0 is busy (0x8281E56C -> A+60);
// show sub_8281E270(A, amount, kingdom); hide sub_8281EA00(A). The running count is the tick's.
//
// THE OBJECTIVE LIST O = *(H+92): add sub_82953CC8(O, CCharString* tag, done) — a known tag only
// re-marks it (sub_82953DD0 first, 0x82953CE0), at most 5 (O+4 < 5, 0x82953CF8); completion
// sub_82953DD0(O, tag, done) answers 1 when the tag was listed (0x82953E50). THE REDRAW
// sub_82953E68(O) is what puts the list on screen, behind *H (0x82953E90 / 0x82953FB0): no rows
// -> `ObjectiveList.ObjectiveList` slide 1 (0x82953FC0); else that slide count + 1 (0x82953EAC)
// and, per row i from 1 in list order, `…Bar<i>.Text` the row's tag NAME (sub_82819230 ->
// sub_828191A8, 0x82953F10) and `…Bar<i>.tickbox.tick` slide done + 1 (0x82953F64). It runs from
// the add (0x82953DA0), the completion (0x82953E58), GUI.ShowSubObjectives (0x8251D8D8),
// GUI.ClearSubObjectives (0x8251D7F0) and the HUD tick when the add's / show's countdown O+12
// reaches 0 (0x82259AA4..0x82259AB0) — never from GUI.RemoveSubObjective, whose row stays drawn
// until the next redraw. `@Hud.Objective.Redraw` at its entry: Godot draws the rows it was told.
//
// THE TARGET NAMES TN = *(H+112) / *(H+116): show sub_825D9FC8(TN, target, viewer, keep) and clear
// sub_825DA3A0(TN). THE CRIME TIMER CT = *(H+104): fine sub_825E5270(CT, fine) and seconds
// sub_825E5308(CT, secs), both formatted and committed only behind CT's elements and *H. THE JOB
// MULTIPLIER J: sub_82950B60(J, level, LHString* prefix) — J+16 is the level shown (0x82950B80).
//
// THE BOX SCREENS. Quest summary rows sub_82973FB0(box, row 0..6, quest*, selected, isLast)
// (0x82973E9C; refuses rows outside 0..6, 0x82973FD4..0x82973FDC); the stats page sub_8296EC88(box,
// page), which stores +0x34 before its presentation lookup and returns at once on the same page
// (0x8296EC9C..0x8296ECB0); the king's schedule ctor sub_82972D58(box, table) -> day +0x1C
// (0x82972DF0); the promise ctor sub_827A3290(box, entity, tag, accepted) -> +72 tag (0x827A32F4),
// +77 accepted (0x827A3300).
//
// THE SCREENS' CURSORS AND CHOICES live in the box and move only inside its pad handler — no call
// carries them, and every element write of them is behind the presentation — so each handler is
// stated at its RETURN as the fields it moved, `@Screen.Fields(box, kind, false, fields…)` when
// they changed, and at the Show's return (display_box_hooks.cpp, StateScreenShown) as the fields it
// starts on, `@Screen.Fields(box, kind, true, fields…)`. The handlers run on box+12 alone (set by
// Show before its presentation gate), so they run headless. Join / visibility (CSimpleOnlineOptionsBox_HandleInput sub_82976BC8): the type
// +48 (18 join / 19 visibility, `stw r28,0x30` 0x82521A10) and the cursor +52 (`stw r9,0x34(r29)`
// 0x82976EF0). Chat (COrbChatBox_HandleInput sub_829727C8): the working mute byte +36
// (`stb r9,0x24(r31)` 0x82972C28). Friends (sub_82977B78): the cursor +36 (`stw r9,0x24(r29)`
// 0x82977D94). Treasury (CMiniStatementBox_HandleInput sub_82B5A410): the page +28 (`stw
// r11,0x1C(r3)` 0x82B5A6F0), the direction byte +32 (`stb r9,0x20(r29)` 0x82B5A5D4), the amount
// widget+16 = +56 (the widget at +40, sub_82B56830 at 0x82B5A698). Bed
// (CBedInteractionMessageBox_HandleInput sub_82B59348): the row +44, sleep +48, sex +52 (0x82B597BC,
// 0x82B595EC, 0x82B59640). Wedding (CWeddingPickerBox_HandleInput sub_82B5C260): the tier +68
// (0x82B5C33C). Partners (CMultiplayerPartnersBox_HandleInput sub_82B5CA70): the cursor +56 and
// scroll +60 (0x82B5CC10 / 0x82B5CC2C). Divorce (CDivorceMessageBox_HandleInput sub_82928958):
// the reason byte +136 (0x82928AB4) and the Show's page mode +172 (sub_829284D8 at 0x8292859C:
// 0/3 the reason page sub_82928C90, 1/4 sub_82928F78, 2/5 sub_82929378). Promise
// (sub_827A3600): the signed byte +77 (A while unsigned, `+77 = 1` 0x827A36EC). Stats
// (sub_8296DA10): the page +52 (sub_8296EC88), the cursor +68 every page highlights (categories
// 0x8296EE30, statistics 0x8296F50C), the category +72 (`a1[18] = a1[17]` 0x8296DC68) and the
// friends' row +76 (0x8296E098).

#include <atomic>
#include <cstdint>
#include <initializer_list>
#include <mutex>
#include <string>
#include <unordered_map>

#include <rex/ppc.h>

#include "gui_statements.h"

REX_EXTERN(__imp__rex_Hud_Hide);
REX_EXTERN(__imp__rex_Hud_Show);
REX_EXTERN(__imp__rex_CutsceneRules_Apply);
REX_EXTERN(__imp__rex_SaveMenu_Open);
REX_EXTERN(__imp__rex_SaveMenu_Row);
REX_EXTERN(__imp__rex_SaveMenu_Close);
REX_EXTERN(__imp__rex_OptionsBox_Query);
REX_EXTERN(__imp__rex_MoneyAlert_Change);
REX_EXTERN(__imp__rex_MoneyAlert_Show);
REX_EXTERN(__imp__rex_MoneyAlert_Hide);
REX_EXTERN(__imp__rex_Objectives_Add);
REX_EXTERN(__imp__rex_Objectives_Mark);
REX_EXTERN(__imp__rex_Objectives_Redraw);
REX_EXTERN(__imp__rex_TargetNames_Show);
REX_EXTERN(__imp__rex_TargetNames_Clear);
REX_EXTERN(__imp__rex_CrimeTimer_Fine);
REX_EXTERN(__imp__rex_CrimeTimer_Seconds);
REX_EXTERN(__imp__rex_JobMultiplier_Set);
REX_EXTERN(__imp__rex_QuestSummary_Row);
REX_EXTERN(__imp__rex_StatsBox_Page);
REX_EXTERN(__imp__rex_KingsSchedule_Ctor);
REX_EXTERN(__imp__rex_PromiseBox_Ctor);
REX_EXTERN(__imp__rex_JoinOptionsBox_HandleInput);
REX_EXTERN(__imp__rex_OrbChatBox_HandleInput);
REX_EXTERN(__imp__rex_FriendsChannelBox_HandleInput);
REX_EXTERN(__imp__rex_MiniStatementBox_HandleInput);
REX_EXTERN(__imp__rex_BedBox_HandleInput);
REX_EXTERN(__imp__rex_WeddingPickerBox_HandleInput);
REX_EXTERN(__imp__rex_PartnersBox_HandleInput);
REX_EXTERN(__imp__rex_DivorceBox_HandleInput);
REX_EXTERN(__imp__rex_PromiseBox_HandleInput);
REX_EXTERN(__imp__rex_StatsBox_HandleInput);
REX_EXTERN(__imp__rex_PartnersBox_Row);
REX_EXTERN(__imp__rex_KingsSchedule_Row);

namespace {

using namespace refii::fable3;

constexpr uint32_t kGuiManager = 0x83556490u;
constexpr uint32_t kGuiHud = 0x20u;
constexpr uint32_t kHudMoneyOne = 120u;
constexpr uint32_t kHudMoneyTwo = 124u;
constexpr uint32_t kHudTargetsOne = 112u;
constexpr uint32_t kHudTargetsTwo = 116u;
constexpr uint32_t kCutsceneHudBit = 0x08000000u;

constexpr uint32_t kSaveMenuOpen = 0u;
constexpr uint32_t kSaveMenuAllowed = 1u;
constexpr uint32_t kSaveMenuRow = 12u;

constexpr uint32_t kObjectivesCount = 4u;
constexpr uint32_t kMoneyBusy = 0u;
constexpr uint32_t kScheduleDay = 0x1Cu;
constexpr uint32_t kPromiseTag = 72u;
constexpr uint32_t kPromiseAccepted = 77u;

std::atomic<int32_t> g_hud_root{0};
std::atomic<int32_t> g_save_row{0};
std::atomic<int64_t> g_crime_fine{-1};
std::atomic<int64_t> g_crime_seconds{-1};

/// The fields a screen's Show or handler left, by box: a handler's posted when any changed, a
/// Show's always (a box address is reused by the next box).
std::mutex g_fields_mutex;
std::unordered_map<uint32_t, uint64_t> g_fields;

/// `@Screen.Fields(box, kind, shown, values…)`: `shown` for the Show's return, else a handler's.
void StateFields(const uint8_t* base, uint32_t box, const char* kind, std::initializer_list<uint32_t> values,
                 bool shown = false) {
  if (base == nullptr || !Plausible(box)) {
    return;
  }
  uint64_t value = 0xcbf29ce484222325ull;
  for (uint32_t field : values) {
    value = (value ^ field) * 0x100000001b3ull;
  }
  {
    std::lock_guard<std::mutex> lock(g_fields_mutex);
    if (g_fields.size() > 256u) {
      g_fields.clear();
    }
    auto [it, inserted] = g_fields.emplace(box, value);
    if (!shown && !inserted && it->second == value) {
      return;
    }
    it->second = value;
  }
  Statement statement("@Screen.Fields");
  statement.Add(Text(Hex(box))).Add(Text(kind)).Add(Boolean(shown));
  for (uint32_t field : values) {
    statement.Add(Number(static_cast<float>(static_cast<int32_t>(field))));
  }
  statement.Post();
}

/// Each screen's cursor and choice fields (offsets from the box; the file header names each
/// field's writer), under the kind Godot's Fable3ScreenStatements keys them by.
void StateKindFields(const uint8_t* base, uint32_t box, const char* kind, bool shown) {
  const std::string name = kind != nullptr ? kind : "";
  auto word = [&](uint32_t offset) { return LoadGuestU32(base, box + offset); };
  auto byte = [&](uint32_t offset) { return static_cast<uint32_t>(base[box + offset]); };
  if (name == "screen:join" || name == "screen:visibility") {
    StateFields(base, box, "join", {word(0x30u), word(0x34u)}, shown);
  } else if (name == "screen:chat") {
    StateFields(base, box, "chat", {byte(0x24u)}, shown);
  } else if (name == "screen:friends") {
    StateFields(base, box, "friends", {word(0x24u)}, shown);
  } else if (name == "screen:treasury") {
    StateFields(base, box, "treasury", {word(0x1Cu), byte(0x20u), word(0x38u)}, shown);
  } else if (name == "screen:bed") {
    StateFields(base, box, "bed", {word(0x2Cu), word(0x30u), word(0x34u)}, shown);
  } else if (name == "screen:wedding") {
    StateFields(base, box, "wedding", {word(0x44u)}, shown);
  } else if (name == "screen:partners") {
    StateFields(base, box, "partners", {word(0x38u), word(0x3Cu)}, shown);
  } else if (name == "screen:divorce") {
    StateFields(base, box, "divorce", {byte(0x88u), word(0xACu)}, shown);
  } else if (name == "screen:promises") {
    StateFields(base, box, "promise", {byte(0x4Du)}, shown);
  } else if (name == "screen:stats") {
    StateFields(base, box, "stats", {word(0x34u), word(0x44u), word(0x48u), word(0x4Cu)}, shown);
  }
}

uint32_t HudWord(const uint8_t* base, uint32_t offset) {
  const uint32_t gui = LoadGuestU32(base, kGuiManager);
  const uint32_t hud = Plausible(gui) ? LoadGuestU32(base, gui + kGuiHud) : 0u;
  return Plausible(hud) ? LoadGuestU32(base, hud + offset) : 0u;
}

/// 0 or 1 for the widget of player one or two, -1 for neither.
int32_t PlayerOf(const uint8_t* base, uint32_t widget, uint32_t one, uint32_t two) {
  if (base == nullptr || widget == 0u) {
    return -1;
  }
  if (widget == HudWord(base, one)) {
    return 0;
  }
  return widget == HudWord(base, two) ? 1 : -1;
}

void StateRoot(int32_t state, const char* via) {
  if (g_hud_root.exchange(state) == state) {
    return;
  }
  Statement("@Hud.Root").Add(Number(static_cast<float>(state))).Add(Text(via)).Post();
}

Statement& AddInt(Statement& statement, uint32_t value) {
  return statement.Add(Number(static_cast<float>(static_cast<int32_t>(value))));
}

}  // namespace

REX_EXTERN(rex_Hud_Hide) {
  __imp__rex_Hud_Hide(ctx, base);
  StateRoot(2, "825C1CB0");
}

REX_EXTERN(rex_Hud_Show) {
  __imp__rex_Hud_Show(ctx, base);
  StateRoot(1, "825C1D28");
}

/// sub_8249A030(rules, a2, a3): state 3 when the rules' word keeps bit 27 on return.
REX_EXTERN(rex_CutsceneRules_Apply) {
  const uint32_t rules = ctx.r3.u32;
  __imp__rex_CutsceneRules_Apply(ctx, base);
  if (base != nullptr && Plausible(rules) && (LoadGuestU32(base, rules) & kCutsceneHudBit) != 0u) {
    StateRoot(3, "8249A030");
  }
}

REX_EXTERN(rex_SaveMenu_Open) {
  const uint32_t menu = ctx.r3.u32;
  __imp__rex_SaveMenu_Open(ctx, base);
  if (base == nullptr || !Plausible(menu) || base[menu + kSaveMenuOpen] == 0u) {
    return;
  }
  const int32_t row = static_cast<int32_t>(LoadGuestU32(base, menu + kSaveMenuRow));
  g_save_row.store(row);
  Statement("@SaveMenu.Open")
      .Add(Boolean(base[menu + kSaveMenuAllowed] != 0u))
      .Add(Number(static_cast<float>(row)))
      .Post();
}

REX_EXTERN(rex_SaveMenu_Row) {
  if (base != nullptr && Plausible(ctx.r3.u32)) {
    const int32_t row = static_cast<int32_t>(LoadGuestU32(base, ctx.r3.u32 + kSaveMenuRow));
    if (g_save_row.exchange(row) != row) {
      Statement("@SaveMenu.Row").Add(Number(static_cast<float>(row))).Post();
    }
  }
  __imp__rex_SaveMenu_Row(ctx, base);
}

REX_EXTERN(rex_SaveMenu_Close) {
  const bool open = base != nullptr && Plausible(ctx.r3.u32) && base[ctx.r3.u32 + kSaveMenuOpen] != 0u;
  __imp__rex_SaveMenu_Close(ctx, base);
  if (open) {
    g_save_row.store(0);
    Statement("@SaveMenu.Close").Post();
  }
}

REX_EXTERN(rex_OptionsBox_Query) {
  Statement("@Options.Query").Add(Text(Hex(ctx.r3.u32))).Add(Text(Hex(ctx.r4.u32))).Post();
  __imp__rex_OptionsBox_Query(ctx, base);
}

/// sub_8281E560(A, old, delta, kingdom).
REX_EXTERN(rex_MoneyAlert_Change) {
  const int32_t player = PlayerOf(base, ctx.r3.u32, kHudMoneyOne, kHudMoneyTwo);
  const bool busy = base != nullptr && Plausible(ctx.r3.u32) && base[ctx.r3.u32 + kMoneyBusy] != 0u;
  Statement statement("@Hud.Money.Change");
  statement.Add(Number(static_cast<float>(player)));
  AddInt(statement, ctx.r4.u32);
  AddInt(statement, ctx.r5.u32);
  statement.Add(Boolean((ctx.r6.u32 & 0xFFu) != 0u)).Add(Boolean(busy)).Post();
  __imp__rex_MoneyAlert_Change(ctx, base);
}

/// sub_8281E270(A, amount, kingdom).
REX_EXTERN(rex_MoneyAlert_Show) {
  const int32_t player = PlayerOf(base, ctx.r3.u32, kHudMoneyOne, kHudMoneyTwo);
  Statement statement("@Hud.Money.Show");
  statement.Add(Number(static_cast<float>(player)));
  AddInt(statement, ctx.r4.u32);
  statement.Add(Boolean((ctx.r5.u32 & 0xFFu) != 0u)).Post();
  __imp__rex_MoneyAlert_Show(ctx, base);
}

REX_EXTERN(rex_MoneyAlert_Hide) {
  const int32_t player = PlayerOf(base, ctx.r3.u32, kHudMoneyOne, kHudMoneyTwo);
  Statement("@Hud.Money.Hide").Add(Number(static_cast<float>(player))).Post();
  __imp__rex_MoneyAlert_Hide(ctx, base);
}

/// sub_82953CC8(O, tag, done): `@Hud.Objective.Add` when the list grew.
REX_EXTERN(rex_Objectives_Add) {
  const uint32_t list = ctx.r3.u32;
  const std::string tag = base != nullptr ? ReadCharString(base, ctx.r4.u32) : std::string();
  const bool done = (ctx.r5.u32 & 0xFFu) != 0u;
  const uint32_t before = base != nullptr && Plausible(list) ? LoadGuestU32(base, list + kObjectivesCount) : 0u;
  __imp__rex_Objectives_Add(ctx, base);
  if (base != nullptr && Plausible(list) && LoadGuestU32(base, list + kObjectivesCount) > before) {
    Statement("@Hud.Objective.Add").Add(Text(tag)).Add(Boolean(done)).Post();
  }
}

/// sub_82953DD0(O, tag, done): `@Hud.Objective.Mark` when the tag was listed.
REX_EXTERN(rex_Objectives_Mark) {
  const std::string tag = base != nullptr ? ReadCharString(base, ctx.r4.u32) : std::string();
  const bool done = (ctx.r5.u32 & 0xFFu) != 0u;
  __imp__rex_Objectives_Mark(ctx, base);
  if ((ctx.r3.u32 & 0xFFu) != 0u) {
    Statement("@Hud.Objective.Mark").Add(Text(tag)).Add(Boolean(done)).Post();
  }
}

/// sub_82953E68(O): `@Hud.Objective.Redraw` when *H is up — the redraw's own gate (0x82953E90).
REX_EXTERN(rex_Objectives_Redraw) {
  if (base != nullptr && HudWord(base, 0u) != 0u) {
    Statement("@Hud.Objective.Redraw").Post();
  }
  __imp__rex_Objectives_Redraw(ctx, base);
}

/// sub_825D9FC8(TN, target, viewer, keep).
REX_EXTERN(rex_TargetNames_Show) {
  const int32_t player = PlayerOf(base, ctx.r3.u32, kHudTargetsOne, kHudTargetsTwo);
  Statement("@Hud.Target.Show")
      .Add(Number(static_cast<float>(player)))
      .Add(Text(Hex(ctx.r4.u32)))
      .Add(Text(Hex(ctx.r5.u32)))
      .Add(Boolean((ctx.r6.u32 & 0xFFu) != 0u))
      .Post();
  __imp__rex_TargetNames_Show(ctx, base);
}

REX_EXTERN(rex_TargetNames_Clear) {
  const int32_t player = PlayerOf(base, ctx.r3.u32, kHudTargetsOne, kHudTargetsTwo);
  Statement("@Hud.Target.Clear").Add(Number(static_cast<float>(player))).Post();
  __imp__rex_TargetNames_Clear(ctx, base);
}

REX_EXTERN(rex_CrimeTimer_Fine) {
  const int64_t fine = static_cast<int32_t>(ctx.r4.u32);
  if (g_crime_fine.exchange(fine) != fine) {
    Statement("@Hud.Crime.Fine").Add(Number(static_cast<float>(fine))).Post();
  }
  __imp__rex_CrimeTimer_Fine(ctx, base);
}

REX_EXTERN(rex_CrimeTimer_Seconds) {
  const int64_t seconds = static_cast<int32_t>(ctx.r4.u32);
  if (g_crime_seconds.exchange(seconds) != seconds) {
    Statement("@Hud.Crime.Seconds").Add(Number(static_cast<float>(seconds))).Post();
  }
  __imp__rex_CrimeTimer_Seconds(ctx, base);
}

/// THE CRIME TIMER SHOWN OR HIDDEN, as the crime manager's tick sub_828393F0 decides it: CT+0 = 2
/// shown / 1 hidden, `li r9,2|1` then `stw r9,0(r11)` (r11 = CT) at 0x82839598 / 0x828395CC and
/// 0x82839838 / 0x8283985C — each store made only when the value changes (`cmpwi r9,2|1` /
/// `beq` before it) and followed by the root's SetState to the same value (hud.bgf CrimeTimer
/// slide 2 "show", 1 "hide"). A [[midasm_hook]] on each store: `@Hud.Crime.Shown(shown)`.
void RefiiF3CrimeTimerShown(PPCRegister& timer, PPCRegister& value) {
  (void)timer;
  Statement("@Hud.Crime.Shown").Add(Boolean(value.u32 == 2u)).Post();
}

/// sub_82950B60(J, level, prefix).
REX_EXTERN(rex_JobMultiplier_Set) {
  if (base != nullptr) {
    Statement statement("@Hud.Job");
    AddInt(statement, ctx.r4.u32);
    statement.Add(Text(ReadLhString(base, ctx.r5.u32))).Post();
  }
  __imp__rex_JobMultiplier_Set(ctx, base);
}

/// sub_82973FB0(box, row, quest, selected, isLast).
REX_EXTERN(rex_QuestSummary_Row) {
  const int32_t row = static_cast<int32_t>(ctx.r4.u32);
  if (row >= 0 && row <= 6) {
    Statement statement("@Screen.QuestRow");
    statement.Add(Text(Hex(ctx.r3.u32)));
    AddInt(statement, ctx.r4.u32);
    statement.Add(Text(Hex(ctx.r5.u32)))
        .Add(Boolean((ctx.r6.u32 & 0xFFu) != 0u))
        .Add(Boolean((ctx.r7.u32 & 0xFFu) != 0u))
        .Post();
  }
  __imp__rex_QuestSummary_Row(ctx, base);
}

/// sub_8296EC88(box, page): stated when it moves the page.
REX_EXTERN(rex_StatsBox_Page) {
  const uint32_t box = ctx.r3.u32;
  const uint32_t page = ctx.r4.u32;
  const bool moves = base != nullptr && Plausible(box) && LoadGuestU32(base, box + 0x34u) != page;
  if (moves) {
    Statement statement("@Screen.StatsPage");
    statement.Add(Text(Hex(box)));
    AddInt(statement, page).Post();
  }
  __imp__rex_StatsBox_Page(ctx, base);
}

REX_EXTERN(rex_KingsSchedule_Ctor) {
  const uint32_t box = ctx.r3.u32;
  __imp__rex_KingsSchedule_Ctor(ctx, base);
  if (base != nullptr && Plausible(box)) {
    Statement statement("@Screen.Schedule");
    statement.Add(Text(Hex(box)));
    AddInt(statement, LoadGuestU32(base, box + kScheduleDay)).Post();
  }
}

REX_EXTERN(rex_PromiseBox_Ctor) {
  const uint32_t box = ctx.r3.u32;
  __imp__rex_PromiseBox_Ctor(ctx, base);
  if (base != nullptr && Plausible(box)) {
    Statement("@Screen.Promise")
        .Add(Text(Hex(box)))
        .Add(Text(Hex(LoadGuestU32(base, box + kPromiseTag))))
        .Add(Boolean(base[box + kPromiseAccepted] != 0u))
        .Post();
  }
}

// The screens' pad handlers (box, ev*, player): their moved fields at return.
#define REFII_F3_SCREEN_HANDLER(hook, kind)              \
  REX_EXTERN(rex_##hook) {                                \
    const uint32_t box = ctx.r3.u32;                      \
    __imp__rex_##hook(ctx, base);                         \
    if (base != nullptr && Plausible(box)) {              \
      StateKindFields(base, box, kind, false);            \
    }                                                     \
  }

REFII_F3_SCREEN_HANDLER(JoinOptionsBox_HandleInput, "screen:join")
REFII_F3_SCREEN_HANDLER(OrbChatBox_HandleInput, "screen:chat")
REFII_F3_SCREEN_HANDLER(FriendsChannelBox_HandleInput, "screen:friends")
REFII_F3_SCREEN_HANDLER(MiniStatementBox_HandleInput, "screen:treasury")
REFII_F3_SCREEN_HANDLER(BedBox_HandleInput, "screen:bed")
REFII_F3_SCREEN_HANDLER(WeddingPickerBox_HandleInput, "screen:wedding")
REFII_F3_SCREEN_HANDLER(PartnersBox_HandleInput, "screen:partners")
REFII_F3_SCREEN_HANDLER(DivorceBox_HandleInput, "screen:divorce")
REFII_F3_SCREEN_HANDLER(PromiseBox_HandleInput, "screen:promises")
REFII_F3_SCREEN_HANDLER(StatsBox_HandleInput, "screen:stats")

#undef REFII_F3_SCREEN_HANDLER

namespace refii::fable3 {

/// A box-manager screen's fields as its Show left them (display_box_hooks.cpp, after the queue
/// tick's Show): the cursor each Show starts on — join/visibility's saved setting (sub_829770A8),
/// chat's saved mute (0x8297245C), friends' 1 (0x82977A64), the ctor's treasury page and bed row.
void StateScreenShown(const uint8_t* base, uint32_t box, const char* kind) {
  if (base != nullptr && Plausible(box)) {
    StateKindFields(base, box, kind, true);
  }
}

}  // namespace refii::fable3

/// THE BED'S OPENER sub_825FB858(queue, a2, a3, offered, group, cycle) keeps r6/r7/r8 in r27/r26/r25
/// (0x825FB874..0x825FB87C) and stores them at box+56/+57/+58 (`stb r27,0x38(r31)` 0x825FB940,
/// 0x825FB954, 0x825FB958). Offered: the Show halves the Sex row's opacity without it (sub_82B59BB8
/// on `BedMenu.Sex` / `.Sex.Title`, 0x82B59030..0x82B59054) and the stick never moves onto it
/// (`+56` tested at 0x82B59800); group picks the Sex title's tag (0x82B58FE8); cycle lets left /
/// right change the Sex option (0x82B59628 / 0x82B59744). A [[midasm_hook]] at 0x825FB940.
void RefiiF3BedOpened(PPCRegister& box, PPCRegister& offered, PPCRegister& group, PPCRegister& cycle) {
  Statement("@Screen.Opened")
      .Add(Text(Hex(box.u32)))
      .Add(Text("bed"))
      .Add(Number(static_cast<float>(offered.u32 & 0xFFu)))
      .Add(Number(static_cast<float>(group.u32 & 0xFFu)))
      .Add(Number(static_cast<float>(cycle.u32 & 0xFFu)))
      .Post();
}

/// THE WEDDING PICKER'S CTOR sub_82B5BCF0 stores its three affordable bytes (stack args 34/36/38)
/// at box+88/+89/+90 from r5 / r3 / r10 (`stb` 0x82B5BDC4 / 0x82B5BDC8 / 0x82B5BDCC). A tier
/// books only when its byte is set (sub_82B5C260, 0x82B5C408..0x82B5C43C) and the Show marks an
/// unaffordable price (dword_8355DBBC appended, 0x82B5BF2C / 0x82B5BFA8 / 0x82B5C01C). A
/// [[midasm_hook]] at 0x82B5BDD0, the stores done.
void RefiiF3WeddingOpened(PPCRegister& box, PPCRegister& first, PPCRegister& second, PPCRegister& third) {
  Statement("@Screen.Opened")
      .Add(Text(Hex(box.u32)))
      .Add(Text("wedding"))
      .Add(Number(static_cast<float>(first.u32 & 0xFFu)))
      .Add(Number(static_cast<float>(second.u32 & 0xFFu)))
      .Add(Number(static_cast<float>(third.u32 & 0xFFu)))
      .Post();
}

/// THE PARTNERS' ROW WRITER sub_82B5D9E0(box, CCharString* row path, record): the window row's
/// record as sub_82B5D6A8 passes it (0x82B5D8E4) after storing the online / joinable results of
/// sub_82382FB0 / sub_82383020 at record+43 / +42 (0x82B5D730 / 0x82B5D74C; a local player's
/// record +42 = 0, +43 = 1, 0x82B5D70C). +40 is the marriage: the writer states the children
/// only for one (0x82B5DC30) and the row's slide is +2 without it (0x82B5D8FC..0x82B5D920).
/// `@Screen.PartnerRow(box, path, married, online, joinable)`.
REX_EXTERN(rex_PartnersBox_Row) {
  if (base != nullptr && Plausible(ctx.r3.u32) && Plausible(ctx.r5.u32)) {
    const uint32_t record = ctx.r5.u32;
    Statement("@Screen.PartnerRow")
        .Add(Text(Hex(ctx.r3.u32)))
        .Add(Text(ReadCharString(base, ctx.r4.u32)))
        .Add(Boolean(base[record + 40u] != 0u))
        .Add(Boolean(base[record + 43u] != 0u))
        .Add(Boolean(base[record + 42u] != 0u))
        .Post();
  }
  __imp__rex_PartnersBox_Row(ctx, base);
}

/// THE KING'S SCHEDULE ROW sub_82973328(box, row 1..4, tick slide, tag): the Show's call per row
/// (0x82973270) — the table's tick for a listed task, 4 and tag 0 past the day's tasks
/// (0x82973264 / 0x82973268); the row writer sets the tag on `TaskN` and the tick on `TickBoxN`
/// (0x829733E0 / 0x829733F0). `@Screen.ScheduleRow(box, row, tick, tag)`.
REX_EXTERN(rex_KingsSchedule_Row) {
  const int32_t row = static_cast<int32_t>(ctx.r4.u32);
  if (row >= 1 && row <= 4) {
    Statement statement("@Screen.ScheduleRow");
    statement.Add(Text(Hex(ctx.r3.u32)));
    AddInt(statement, ctx.r4.u32);
    AddInt(statement, ctx.r5.u32);
    statement.Add(Text(Hex(ctx.r6.u32))).Post();
  }
  __imp__rex_KingsSchedule_Row(ctx, base);
}

/// THE STATS SCREEN'S WINDOWS: each page's writer decides its first row, and the cursor `+68` is
/// highlighted where `start + row == cursor`. `@Screen.StatsWindow(box, page, start, count, rows)`
/// at the instruction after the decision:
///   - categories sub_8296ED30: count = the categories / 20 (`divw r7` 0x8296ED58); start r24 = 0, or
///     cursor - 8 when count > 8 and cursor >= 8 (0x8296ED5C..0x8296ED74); rows 0..7 (`cmplwi r28,8`
///     0x8296EED0). Midasm at 0x8296ED7C (box r27).
///   - statistics sub_8296EF78: count r30 (0x8296F1C0); rows r27 = 6 when the profile is online and
///     listed (0x8296F1C8..0x8296F1D4), else 9 (0x8296F1E8); start r31 = 0, or cursor - rows + 1
///     when count > rows and cursor >= rows (0x8296F200..0x8296F220). Midasm at 0x8296F224 (box r28).
///   - friends sub_829708B0: count r31 (stored at box+80, 0x82970B58); start r10 = 0, or cursor - 8
///     when count > 9 and cursor >= 9 (0x82970B54..0x82970B6C); rows 0..8. Midasm at 0x82970B70
///     (box r28).
namespace {

void StateStatsWindow(uint32_t box, int32_t page, uint32_t start, uint32_t count, int32_t rows) {
  Statement statement("@Screen.StatsWindow");
  statement.Add(Text(Hex(box))).Add(Number(static_cast<float>(page)));
  AddInt(statement, start);
  AddInt(statement, count);
  statement.Add(Number(static_cast<float>(rows))).Post();
}

}  // namespace

void RefiiF3StatsCategories(PPCRegister& box, PPCRegister& start, PPCRegister& count) {
  StateStatsWindow(box.u32, 2, start.u32, count.u32, 8);
}

void RefiiF3StatsStatistics(PPCRegister& box, PPCRegister& start, PPCRegister& count, PPCRegister& rows) {
  StateStatsWindow(box.u32, 3, start.u32, count.u32, static_cast<int32_t>(rows.u32));
}

void RefiiF3StatsFriends(PPCRegister& box, PPCRegister& start, PPCRegister& count) {
  StateStatsWindow(box.u32, 4, start.u32, count.u32, 9);
}
