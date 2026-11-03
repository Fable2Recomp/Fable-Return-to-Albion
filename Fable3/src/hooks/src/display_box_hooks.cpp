// refii - Fable 3's display boxes and menu box, stated at the moment the guest shows and drops
// them, for Godot's Fable3GuiNatives / Fable3Boxes. The natives that QUEUE a box
// (DisplayMessageBox, DisplayYesNoQuestion, …) are not the moment it is on screen — a box waits
// behind the one showing, and many boxes are queued by C++ and never pass a native — so the
// statement is taken from the box manager itself.
//
// THE BOX MANAGER BM = *(*(GUI+0x1C)+0x30) (GUI = *0x83556490) holds two queues of 28 bytes at
// BM+4 (Q0: messages, questions, adjusters, quest boxes) and BM+32 (Q1: intermission and info
// cards, button boxes): +4/+8 the pending vector's begin/end (8-byte {Box*, refcount*} entries),
// +20 the SHOWING box's shared_ptr (+20 Box*, +24 count). Each tick the screen manager runs the
// queue's Update sub_822878B0(queue) (0x821B65DC / 0x821B660C):
//   - a showing box's vt[5] Update returning 0 releases it (0x82287910, `*(queue+20) = 0` at
//     0x82287918) — the ONE exit for lifetime, Hide and RemoveDisplayBox;
//   - with none showing and a pending box, unless the menu box is open (`*(SM+0x34)` +12, or its
//     close timer +4 > 0), the queue's own vt[0] (sub_825FC030 for Q0) takes the front into +20
//     (0x825FC060) and calls the box's vt[1] Show (0x825FC09C).
// So the shared_ptr at queue+20 before and after that call states every open and close.
//
// THE BOX (base sub_8291ECD8): +0 vtable, +4 name CCharString, +8 resolved text LHString, +12
// shown, +16 lifetime, +24 pad filter. The class is its vtable: yes/no off_820B45E0 (+32/+36 the
// yes/no tags, empty = GUI_YES / GUI_NO; OnAction 41 ANSWER_QUESTION_YES -> 1, 42 -> 0, reply event
// 68) and the natal query off_820D8868; message off_820B45C0 (73 GUI_CANCEL hides, event 66);
// numeric adjuster off_820B4600 (+100 min, +104 max, +108 increment, +112 value; 31/32 step, 41/42
// -> events 69/70 with the value); quest box off_820B4620 (73/61 -> event 67); intermission
// off_820D87D4 and info card off_820D71CC (+36 Title, +40 ShowAButton, +41 ShowYButton, +44
// AcceptText, +48 HelpText; A/Y -> event 64) and button box off_820D87F4 on Q1. The guest routes
// the pad to the showing box itself (sub_825D6A08 -> sub_825C8B68(BM, ev) -> vt[4]), so Godot
// draws and never answers.
//
// THE MENU BOX MB = *(SM+0x34): +0 selected, +4 close timer, +12 open, +13 cancelled, +14
// cancelable, +16/+20 the rows vector (8-byte rows: +0 TextTag CCharString, +4 Enabled byte, six
// at most), +32 title CCharString. Open sub_825FA220(MB) returns 1 when it opened (`+12 = 1` at
// 0x825FA3F4); input sub_825FA030(MB, ev, pad) moves +0 (72/74 select, 73/75 cancel, 29/30 up and
// down); close sub_825FA420(MB) posts event 65 with `cancelled ? 0 : selected + 1` (0x825FA4C0).

#include <cstdint>
#include <mutex>
#include <string>
#include <unordered_map>

#include <rex/ppc.h>

#include "gui_statements.h"

REX_EXTERN(__imp__rex_BoxQueue_Update);
REX_EXTERN(__imp__rex_MenuBox_Open);
REX_EXTERN(__imp__rex_MenuBox_Close);
REX_EXTERN(__imp__rex_MenuBox_Input);

namespace {

using namespace refii::fable3;

constexpr uint32_t kGuiManager = 0x83556490u;
constexpr uint32_t kQueueShowing = 20u;
constexpr uint32_t kQueueStride = 28u;

constexpr uint32_t kBoxName = 4u;
constexpr uint32_t kBoxText = 8u;

/// The box classes, by vtable (Fable 3 IDB, disc build).
constexpr uint32_t kVtMessage = 0x820B45C0u;
constexpr uint32_t kVtYesNo = 0x820B45E0u;
constexpr uint32_t kVtNatalQuery = 0x820D8868u;
constexpr uint32_t kVtAdjuster = 0x820B4600u;
constexpr uint32_t kVtQuest = 0x820B4620u;
constexpr uint32_t kVtMarriage = 0x820B4640u;
constexpr uint32_t kVtIntermission = 0x820D87D4u;
/// CAcceptHoldBox (type 8; the PC RTTI class map: `Paper.HoldButton`, `Paper.Accept`).
constexpr uint32_t kVtInfoCard = 0x820D71CCu;
constexpr uint32_t kVtButtonBox = 0x820D87F4u;
constexpr uint32_t kVtWeddingPicker = 0x820D85A4u;
/// Q0's CTutorialBox (type 26, ctor sub_824B1BC8: its Show hides the B button and both natal
/// choices, 0x824B27C0..0x824B27E8; its input takes only 41, 0x824B29DC) and Q1's CAcceptBox
/// (type 6, built by the card factory sub_825FB988 case 6; its input slot is `li r3, 0`) — class
/// names from the PC build's RTTI (scratch f3/gaps/rtti/classmap.tsv).
constexpr uint32_t kVtNotice = 0x82091964u;
constexpr uint32_t kVtDisplay = 0x820D71ECu;

/// The box-manager SCREENS, by vtable: each class's slot 7 returns its box type (`li r3, N` at
/// the function the vtable's +28 names) and its constructor stores the vtable — Options
/// 0x8209D230 (sub_825C95F0 at 0x825c9680, type 15), hero status 0x820C345C (0x8252188c, 13),
/// promises 0x820AFD94 (0x827a32e0, 12), stats 0x820C2E28 (0x8296d310, 20), quest summary
/// 0x820C3214 (0x829735f8, 27), king's schedule 0x820C30E0 (0x82972da8, 25).
constexpr uint32_t kVtOptions = 0x8209D230u;
constexpr uint32_t kVtHeroStatus = 0x820C345Cu;
constexpr uint32_t kVtPromises = 0x820AFD94u;
constexpr uint32_t kVtStats = 0x820C2E28u;
constexpr uint32_t kVtQuestSummary = 0x820C3214u;
constexpr uint32_t kVtKingsSchedule = 0x820C30E0u;

/// The rest of the box-manager screens, each writing its own .bgf's elements (the presentation
/// slot is sub_8291F4C8's switch on the type; the slot's file is sub_82B3EC40's table):
/// join / visibility options 0x820C35F8 (ctor sub_82521928, ONE class whose slot 7 returns its
/// +48 — 18 JoinOptions.bgf, 19 Visability.bgf, `lwz r3, 0x30(r3)` at 0x82A0AF50), friends
/// channel 0x820C3640 (sub_82521AF0, 21), chat channel 0x820C2F58 (sub_825208D8, 22), the
/// treasury statement 0x820D84A4 (sub_82B59C40, 17), the bed menu 0x820D7508 (sub_825FB858, 14),
/// the food query 0x820D72F4 (sub_825FB3D0 / sub_825FB4A8, 9), the wedding picker 0x820D85A4
/// (sub_82B5BCF0, 23), co-op partners 0x820D8754 (sub_82B5C5D0, 24), the marriage proposal
/// 0x820B4640 (sub_825FB730, 10) and the divorce message 0x820C0088 (sub_829281C8, 11).
constexpr uint32_t kVtJoinOptions = 0x820C35F8u;
constexpr uint32_t kVtFriendsChannel = 0x820C3640u;
constexpr uint32_t kVtChatChannel = 0x820C2F58u;
constexpr uint32_t kVtTreasury = 0x820D84A4u;
constexpr uint32_t kVtBedMenu = 0x820D7508u;
constexpr uint32_t kVtFoodQuery = 0x820D72F4u;
constexpr uint32_t kVtPartners = 0x820D8754u;
constexpr uint32_t kVtDivorce = 0x820C0088u;

/// The join/visibility class's box type (its slot 7 reads +48).
constexpr uint32_t kJoinOptionsType = 48u;
constexpr uint32_t kTypeVisibility = 19u;

/// The message box's download form (DisplayDownloadMessageBox, sub_82516B18): +33 set (0x82516BAC)
/// shows `paper.MessageBox.Action` and names its X button by the tag at +36 (sub_8281AFA8 at
/// 0x8281B048 / 0x8281B07C); X (76) is taken only while +33 is set (sub_8281B390, 0x8281B410).
constexpr uint32_t kMessageDownload = 33u;
constexpr uint32_t kMessageActionTag = 36u;

constexpr uint32_t kMenuSelected = 0u;
constexpr uint32_t kMenuOpen = 12u;
constexpr uint32_t kMenuCancelled = 13u;
constexpr uint32_t kMenuCancelable = 14u;
constexpr uint32_t kMenuRowsBegin = 16u;
constexpr uint32_t kMenuRowsEnd = 20u;
constexpr uint32_t kMenuTitle = 32u;
constexpr uint32_t kMenuRowStride = 8u;
constexpr uint32_t kMenuMaxRows = 6u;

const char* KindOf(const uint8_t* base, uint32_t box, uint32_t vtable) {
  switch (vtable) {
    case kVtMessage: return "message";
    case kVtYesNo: return "question";
    case kVtNatalQuery: return "question";
    case kVtNotice: return "notice";
    case kVtDisplay: return "display";
    case kVtAdjuster: return "adjuster";
    case kVtQuest: return "quest";
    case kVtIntermission: return "intermission";
    case kVtInfoCard: return "card";
    case kVtButtonBox: return "buttons";
    case kVtJoinOptions:
      return LoadGuestU32(base, box + kJoinOptionsType) == kTypeVisibility ? "screen:visibility" : "screen:join";
    case kVtFriendsChannel: return "screen:friends";
    case kVtChatChannel: return "screen:chat";
    case kVtTreasury: return "screen:treasury";
    case kVtBedMenu: return "screen:bed";
    case kVtFoodQuery: return "screen:food";
    case kVtWeddingPicker: return "screen:wedding";
    case kVtPartners: return "screen:partners";
    case kVtMarriage: return "screen:proposal";
    case kVtDivorce: return "screen:divorce";
    case kVtOptions: return "screen:options";
    case kVtHeroStatus: return "screen:herostatus";
    case kVtPromises: return "screen:promises";
    case kVtStats: return "screen:stats";
    case kVtQuestSummary: return "screen:quests";
    case kVtKingsSchedule: return "screen:schedule";
    default: return "unknown";
  }
}

/// Which of the manager's two queues `queue` is, or -1.
int32_t QueueIndex(const uint8_t* base, uint32_t queue) {
  const uint32_t gui = LoadGuestU32(base, kGuiManager);
  if (!Plausible(gui)) {
    return -1;
  }
  const uint32_t screens = LoadGuestU32(base, gui + 0x1Cu);
  if (!Plausible(screens)) {
    return -1;
  }
  const uint32_t manager = LoadGuestU32(base, screens + 0x30u);
  if (!Plausible(manager) || queue < manager + 4u) {
    return -1;
  }
  const uint32_t offset = queue - (manager + 4u);
  return offset % kQueueStride == 0u && offset / kQueueStride < 2u ? static_cast<int32_t>(offset / kQueueStride) : -1;
}

/// `@Box.Open(queue, box, kind, name, text, …)` — the trailing arguments by kind: message
/// download, actionTag; question yesTag, noTag; adjuster min, max, increment, value; card title,
/// acceptTag, helpTag, showA, showY.
void PostOpen(const uint8_t* base, int32_t queue, uint32_t box) {
  const uint32_t vtable = LoadGuestU32(base, box);
  Statement open("@Box.Open");
  open.Add(Number(static_cast<float>(queue)))
      .Add(Text(Hex(box)))
      .Add(Text(KindOf(base, box, vtable)))
      .Add(Text(ReadCharString(base, box + kBoxName)))
      .Add(Text(ReadLhString(base, box + kBoxText)));
  switch (vtable) {
    case kVtMessage:
      open.Add(Boolean(base[box + kMessageDownload] != 0u)).Add(Text(ReadCharString(base, box + kMessageActionTag)));
      break;
    case kVtYesNo:
    case kVtNatalQuery:
      open.Add(Text(ReadCharString(base, box + 32u))).Add(Text(ReadCharString(base, box + 36u)));
      break;
    case kVtAdjuster:
      open.Add(Number(static_cast<float>(static_cast<int32_t>(LoadGuestU32(base, box + 100u)))))
          .Add(Number(static_cast<float>(static_cast<int32_t>(LoadGuestU32(base, box + 104u)))))
          .Add(Number(static_cast<float>(static_cast<int32_t>(LoadGuestU32(base, box + 108u)))))
          .Add(Number(static_cast<float>(static_cast<int32_t>(LoadGuestU32(base, box + 112u)))));
      break;
    case kVtInfoCard:
      open.Add(Text(ReadCharString(base, box + 36u)))
          .Add(Text(ReadCharString(base, box + 44u)))
          .Add(Text(ReadCharString(base, box + 48u)))
          .Add(Boolean(base[box + 40u] != 0u))
          .Add(Boolean(base[box + 41u] != 0u));
      break;
    default:
      break;
  }
  open.Post();
}

std::mutex g_mutex;
std::unordered_map<uint32_t, int32_t> g_adjuster_values;  // box -> last value posted

}  // namespace

/// The queue's tick: the showing box before and after is the open/close statement.
REX_EXTERN(rex_BoxQueue_Update) {
  const uint32_t queue = ctx.r3.u32;
  const uint32_t before = base != nullptr && Plausible(queue) ? LoadGuestU32(base, queue + kQueueShowing) : 0u;

  __imp__rex_BoxQueue_Update(ctx, base);

  if (base == nullptr || !Plausible(queue)) {
    return;
  }
  const uint32_t after = LoadGuestU32(base, queue + kQueueShowing);
  const int32_t index = QueueIndex(base, queue);

  if (before != 0u && before != after) {
    Statement("@Box.Close").Add(Number(static_cast<float>(index))).Add(Text(Hex(before))).Post();
    std::lock_guard<std::mutex> lock(g_mutex);
    g_adjuster_values.erase(before);
  }
  if (after != 0u && after != before && Plausible(after)) {
    PostOpen(base, index, after);
  }

  // The adjuster's value moves on the guest's own left/right handling; restated on change.
  if (after != 0u && Plausible(after) && LoadGuestU32(base, after) == kVtAdjuster) {
    const int32_t value = static_cast<int32_t>(LoadGuestU32(base, after + 112u));
    std::lock_guard<std::mutex> lock(g_mutex);
    auto last = g_adjuster_values.find(after);
    if (last == g_adjuster_values.end() || last->second != value) {
      g_adjuster_values[after] = value;
      Statement("@Box.Value").Add(Text(Hex(after))).Add(Number(static_cast<float>(value))).Post();
    }
  }
}

/// `@MenuBox.Open(title, cancelable, selected, row…)` — each row its TextTag, prefixed `+`
/// (enabled) or `-` (ghosted).
REX_EXTERN(rex_MenuBox_Open) {
  const uint32_t menu = ctx.r3.u32;
  __imp__rex_MenuBox_Open(ctx, base);
  if (base == nullptr || ctx.r3.u32 != 1u || !Plausible(menu)) {
    return;
  }

  Statement open("@MenuBox.Open");
  open.Add(Text(ReadCharString(base, menu + kMenuTitle)))
      .Add(Boolean(base[menu + kMenuCancelable] != 0u))
      .Add(Number(static_cast<float>(static_cast<int32_t>(LoadGuestU32(base, menu + kMenuSelected)))));
  const uint32_t begin = LoadGuestU32(base, menu + kMenuRowsBegin);
  const uint32_t end = LoadGuestU32(base, menu + kMenuRowsEnd);
  if (Plausible(begin) && end > begin && (end - begin) / kMenuRowStride <= kMenuMaxRows) {
    for (uint32_t row = begin; row < end; row += kMenuRowStride) {
      std::string tag = base[row + 4u] != 0u ? "+" : "-";
      tag += ReadCharString(base, row);
      open.Add(Text(tag));
    }
  }
  open.Post();
}

/// `@MenuBox.Close(result)` — 0 cancelled, else the 1-based row, as event 65 carries it.
REX_EXTERN(rex_MenuBox_Close) {
  const uint32_t menu = ctx.r3.u32;
  const bool was_open = base != nullptr && Plausible(menu) && base[menu + kMenuOpen] != 0u &&
                        LoadGuestU32(base, menu + 8u) != 0u;
  int32_t result = 0;
  if (was_open && base[menu + kMenuCancelled] == 0u) {
    result = static_cast<int32_t>(LoadGuestU32(base, menu + kMenuSelected)) + 1;
  }
  __imp__rex_MenuBox_Close(ctx, base);
  if (was_open) {
    Statement("@MenuBox.Close").Add(Number(static_cast<float>(result))).Post();
  }
}

/// `@MenuBox.Select(selected)` when the guest's own input moved the row.
REX_EXTERN(rex_MenuBox_Input) {
  const uint32_t menu = ctx.r3.u32;
  const bool open = base != nullptr && Plausible(menu) && base[menu + kMenuOpen] != 0u;
  const uint32_t before = open ? LoadGuestU32(base, menu + kMenuSelected) : 0u;
  __imp__rex_MenuBox_Input(ctx, base);
  if (!open) {
    return;
  }
  const uint32_t after = LoadGuestU32(base, menu + kMenuSelected);
  if (after != before) {
    Statement("@MenuBox.Select").Add(Number(static_cast<float>(static_cast<int32_t>(after)))).Post();
  }
}
