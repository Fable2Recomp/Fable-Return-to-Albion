// refii - Fable 3's world icons and emotion choices, stated at the guest's own entry points into
// the table both modules link (core/payload_bridge.h, src/runtime/src/core/stated_world_icons.cpp);
// the host bridge (Fable3/src/hostbridge/src/icon_feed.cpp) reads only that table. Every hook runs
// the guest's body unchanged: the icon objects and records stay alive and run — the hold fill
// (sub_825EC530 posts message 116 at 0x825ECB20 and runs CECOnActionUse_Use at 0x825ECD64), the
// choice commit (sub_825E91E0 finds its icon through sub_825EDDF8 at 0x825E93BC) and every read-back
// are the guest's. The drawing is Godot's.
//
// Every address is the Fable 3 IDB; each offset names its base.
//
// THE REGISTRY R = *(*(GUI+0x20)+0x18), GUI = *0x83556490, ticked by sub_825EC530(R, time, ctx, draw).
//   - Add (sub_825ED9C8 by entity, sub_825EDB10 by position) only builds a 64-byte request
//     (sub_825EC218) and queues it on R+88 (sub_825EF6D8 at 0x825EDA44 / 0x825EDB9C).
//   - The tick takes each accepted request (sub_825EF220, 0x825EC684) to sub_825EEB28 (0x825EC69C):
//     an icon already held under the same name and entity is initialised again (`vt+4` at
//     0x825EEC80); otherwise a CWorldIcon3D (560 bytes, vtable 0x820029F8 stored 0x82B50314; built
//     0x825EEDD8), a CEmotionWorldIcon (912, type 77, vtable 0x820C37A8 stored 0x829782E0; built
//     0x825EEE18) or a HUD-element icon (192, vtable 0x820D5EBC stored 0x82B52534 by sub_82B52440,
//     0x825EEE24) is built and initialised (`vt+4`, 0x825EEF44). Every Init reaches
//     CWorldIconBase's sub_82B52DB0 (from 0x82B51274 and 0x82B5207C; the emotion Init sub_82978558
//     runs the 3D one at 0x82978574).
//   - Each tick it asks every icon it holds whether it lives (`vt+20`, 0x825EC7C4 / 0x825ECFA4 /
//     0x825ED3E0) and Updates it (`vt+16`, icon, visible, ctx) — 0x825EC824 for the entity-less
//     vector R+20..R+24, 0x825ED000 / 0x825ED04C / 0x825ED46C for the entity map. `visible` is
//     sub_82230C10: the icon's player (+103, written 0x825EEEC0) is the current one (0x82230C54),
//     R+55 may draw world icons, the HUD's hidden bit, and for the opinion and hint kinds R+56 and
//     R+57 (SetCanShowHintWorldIcons writes R+57 at 0x8251C328) — and, in an entity's group, only
//     the icon that wins the group (0x825ED450). A dead icon is erased instead: sub_825EE968(out,
//     R, vector, iterator) is the one erase of an icon (0x825EC858, 0x825ED06C, 0x825ED4A0, Remove
//     sub_825EDCA8 at 0x825EDD68, the record's remover sub_8226CCF0 -> sub_821941A0 at 0x82194254).
//   - The Updates: CWorldIcon3D's sub_82226DA8 (slot 4; the emotion Update sub_82979508 runs it at
//     0x8297954C and then the ring tick sub_8297A400 at 0x829795CC) and the HUD-element icon's
//     sub_82B51B58 (slot 4; `visible` shows its element through `vt+24` at 0x82B51F58). Both start
//     with the base sub_8222F500: seconds left +48 -= dt (0x8222F538), age +68 += dt (0x8222F564).
//   - Every icon class ends in CWorldIconBase's destructor sub_82B52D20 (0x82B505EC, 0x82B519AC,
//     0x82B52CEC).
//
// AN ICON (offsets from the icon object):
//   +16 name CCharString (0x82B52DC8)   +20 type (`vt+8`: 0x82B512D0, 0x82B520E8)
//   +24/+28 entity weak ref, `*(+28)` the node whose first word is the CThing (0x8222F5CC..F5F4)
//   +48 seconds left, +52 persistent (0x82B52EC4..0x82B52EDC)
//   +60..+63 colour, drawn as (+62, +61, +60, +63) / 255 (`vt+52` sub_82B53EA8 copies it,
//   0x82B53EB0..0x82B53EC8; the draw sub_82B50AB8 reads it at 0x82B50E30..0x82B50EBC)
//   +68 age   +100 fading out (cleared by Init 0x82B52DF4, set at age 4 s 0x82226F24)
//   +103 player   +112 the anchor: GetWorldPosition (`vt+48`, sub_8221B4F0 `stvx128 v127, r27, r25`
//   with r25 = 112 at 0x8221B990; the emotion icon's sub_829797B8 adds its orbit, 0x82979FF4) —
//   the liveness test computes it each tick (0x8222F654) and the draw projects it
//   3D and emotion only: +144 texture id (0x82B51304; the emotion's swap sub_8297A130 at 0x8297A148),
//   +148 the GUI image, -1 while none (0x82B50D9C, released 0x82B5137C), +152 scale (0x82226EA8..
//   0x82226EF0), +156 title and +372 opinion LHStrings (`vt+12` sub_82B51420, 0x82B5143C/0x82B5144C)
//   emotion only: +692 ring on (sub_82979188, 0x829791B8), +696 progress 0..300 (0x8297A6A4), +700
//   complete (0x8297A61C), +701 manual (0x829791FC), +720 manual speed (0x8297A544)
//   HUD-element only: +144 is its pooled element slot (0x82B52520), +148 its element state.
//
// THE CHOICE RECORDS. Every creator — Lua Emotion.AddEmotion (sub_825E8C90 0x825E8D08), the shop
// plinth (sub_825E8EC0 0x825E8F8C), OnActionUse (sub_825E8FC0 0x825E9070), sub_825E8DC0 (0x825E8E94),
// the net clone (sub_822951F0) — ends in sub_825E8D30(E, shared_ptr*), which pushes the shared
// pointer onto E's list (sub_8238CFD0 copies its object word into the node at 0x8238D010). The
// record's shared count deletes it through sub_825EBD70 (the deleter sub_825EBCF8 stores at
// 0x825EBD64), which removes its icon (sub_8226CCF0 at 0x825EBDB8). The record (sub_825E8458):
//   +0 action (0x825E847C; the release rewrites it, 0x825E9600)   +4 Name (0x825E84BC)
//   +8/+12 player uid (0x825E86A4/0x825E86AC)   +16 Text LHString (0x825E850C)
//   +20/+24 entity weak ref (0x825E8540/0x825E8548)   +32 Position (0x825E8560)
//   +52 RecordName (0x825E859C)   +56 kind (sub_825EBEE8, 0x825E85A8)   +60 Holdable (0x825E85C0)
//   +61 ManuallyIncreasePower (0x825E85D8)   +62 held (0x825E85DC; press 0x825E9924, release
//   0x825E9608, both in sub_825E91E0)   +64 ManualPowerIncreaseSpeed (0x825E8610)
// The record's icon is the one sub_825EDDF8(R, name, entity) finds: the record's Name and entity
// (sub_825E8938 queues it under rec+4 and the entity at rec+20, 0x825E89C0), matched by
// sub_82267178 on the icon's +16 and, when an entity is given, its +24.

#include <atomic>
#include <cstdint>
#include <cstring>
#include <string>

#include <rex/logging.h>
#include <rex/ppc.h>

#include "core/payload_bridge.h"
#include "gui_statements.h"

REX_EXTERN(__imp__rex_WorldIcon_Init);
REX_EXTERN(__imp__rex_WorldIcon3D_Update);
REX_EXTERN(__imp__rex_HudElementIcon_Update);
REX_EXTERN(__imp__rex_EmotionIcon_RingTick);
REX_EXTERN(__imp__rex_IconRegistry_Erase);
REX_EXTERN(__imp__rex_WorldIcon_Destroy);
REX_EXTERN(__imp__rex_EmotionChoices_Add);
REX_EXTERN(__imp__rex_EmotionChoice_Delete);
REX_EXTERN(__imp__rex_EmotionChoices_Input);

namespace {

using refii::fable3::LoadGuestF32;
using refii::fable3::LoadGuestU32;
using refii::fable3::Plausible;
using refii::fable3::ReadCharString;
using refii::fable3::ReadLhString;

constexpr uint32_t kVtWorldIcon3D = 0x820029F8u;
constexpr uint32_t kVtEmotionIcon = 0x820C37A8u;
constexpr uint32_t kVtHudElementIcon = 0x820D5EBCu;

constexpr uint32_t kMaxChoices = 64u;

uint8_t Byte(const uint8_t* base, uint32_t address) { return base[address]; }

/// The CThing behind the weak ref at `at`: `*(*(at+4))`, 0 when it is gone.
uint32_t WeakRefTarget(const uint8_t* base, uint32_t at) {
  const uint32_t node = LoadGuestU32(base, at + 4u);
  if (!Plausible(node)) {
    return 0u;
  }
  const uint32_t thing = LoadGuestU32(base, node);
  return Plausible(thing) ? thing : 0u;
}

uint32_t ClassOf(const uint8_t* base, uint32_t icon) {
  switch (LoadGuestU32(base, icon)) {
    case kVtWorldIcon3D:
      return REFII_WORLD_ICON_CLASS_3D;
    case kVtEmotionIcon:
      return REFII_WORLD_ICON_CLASS_EMOTION;
    case kVtHudElementIcon:
      return REFII_WORLD_ICON_CLASS_HUD_ELEMENT;
    default:
      return 0u;
  }
}

const char* ClassName(uint32_t kind) {
  switch (kind) {
    case REFII_WORLD_ICON_CLASS_3D:
      return "3D";
    case REFII_WORLD_ICON_CLASS_EMOTION:
      return "emotion";
    case REFII_WORLD_ICON_CLASS_HUD_ELEMENT:
      return "HUD element";
    default:
      return "?";
  }
}

std::atomic<uint64_t> g_types_seen[2]{};

/// The icon as its last writer left it; `visible` is the Update's argument, -1 keeps the last one.
void StateIcon(const uint8_t* base, uint32_t icon, int32_t visible) {
  if (!Plausible(icon)) {
    return;
  }
  const uint32_t kind = ClassOf(base, icon);
  if (kind == 0u) {
    return;
  }
  const bool pooled = kind == REFII_WORLD_ICON_CLASS_HUD_ELEMENT;
  const bool emotion = kind == REFII_WORLD_ICON_CLASS_EMOTION;

  uint32_t words[REFII_WORLD_ICON_WORDS]{};
  float values[REFII_WORLD_ICON_VALUES]{};
  const uint32_t type = LoadGuestU32(base, icon + 20u);
  words[0] = kind;
  words[1] = type;
  words[2] = pooled ? 0u : LoadGuestU32(base, icon + 144u);
  words[3] = (static_cast<uint32_t>(Byte(base, icon + 62u)) << 24) |
             (static_cast<uint32_t>(Byte(base, icon + 61u)) << 16) |
             (static_cast<uint32_t>(Byte(base, icon + 60u)) << 8) | Byte(base, icon + 63u);
  words[4] = WeakRefTarget(base, icon + 24u);
  uint32_t flags = 0u;
  if (Byte(base, icon + 52u) != 0u) flags |= REFII_WORLD_ICON_PERSISTENT;
  if (Byte(base, icon + 100u) != 0u) flags |= REFII_WORLD_ICON_FADING;
  if (!pooled && LoadGuestU32(base, icon + 148u) != 0xFFFFFFFFu) flags |= REFII_WORLD_ICON_IMAGE_LIVE;
  if (emotion) {
    if (Byte(base, icon + 692u) != 0u) flags |= REFII_WORLD_ICON_RING_ON;
    if (Byte(base, icon + 700u) != 0u) flags |= REFII_WORLD_ICON_RING_COMPLETE;
    if (Byte(base, icon + 701u) != 0u) flags |= REFII_WORLD_ICON_RING_MANUAL;
  }
  words[5] = flags;
  words[6] = emotion ? LoadGuestU32(base, icon + 696u) : 0u;
  words[7] = Byte(base, icon + 103u);

  values[0] = LoadGuestF32(base, icon + 112u);
  values[1] = LoadGuestF32(base, icon + 116u);
  values[2] = LoadGuestF32(base, icon + 120u);
  values[3] = pooled ? 1.0f : LoadGuestF32(base, icon + 152u);
  values[4] = LoadGuestF32(base, icon + 48u);
  values[5] = LoadGuestF32(base, icon + 68u);
  values[6] = emotion ? LoadGuestF32(base, icon + 720u) : 0.0f;

  const std::string name = ReadCharString(base, icon + 16u);
  std::string text = name;
  text.push_back('\0');
  if (!pooled) {
    text += ReadLhString(base, icon + 156u);
    text.push_back('\0');
    text += ReadLhString(base, icon + 372u);
  } else {
    text.push_back('\0');
  }
  refii_bridge_world_icon_stated(icon, visible, words, values, text.data(), static_cast<uint32_t>(text.size()));

  if (type < 128u) {
    const uint64_t bit = 1ull << (type & 63u);
    if ((g_types_seen[type >> 6].fetch_or(bit) & bit) == 0u) {
      REXLOG_INFO("Refii world icons: first type {} ({}) stated: '{}' entity {:08X} texture {:08X}", type,
                  ClassName(kind), name, words[4], words[2]);
    }
  }
}

/// The choice record as its creator built it or the input handler left it.
void StateChoice(const uint8_t* base, uint32_t record) {
  if (!Plausible(record)) {
    return;
  }
  uint32_t words[REFII_EMOTION_CHOICE_WORDS]{};
  float values[REFII_EMOTION_CHOICE_VALUES]{};
  words[0] = LoadGuestU32(base, record + 0u);
  words[1] = LoadGuestU32(base, record + 8u);
  words[2] = LoadGuestU32(base, record + 12u);
  words[3] = WeakRefTarget(base, record + 20u);
  words[4] = LoadGuestU32(base, record + 56u);
  uint32_t flags = 0u;
  if (Byte(base, record + 60u) != 0u) flags |= REFII_EMOTION_CHOICE_HOLDABLE;
  if (Byte(base, record + 61u) != 0u) flags |= REFII_EMOTION_CHOICE_MANUAL;
  if (Byte(base, record + 62u) != 0u) flags |= REFII_EMOTION_CHOICE_HELD;
  words[5] = flags;
  values[0] = LoadGuestF32(base, record + 32u);
  values[1] = LoadGuestF32(base, record + 36u);
  values[2] = LoadGuestF32(base, record + 40u);
  values[3] = LoadGuestF32(base, record + 64u);

  std::string text = ReadCharString(base, record + 4u);
  text.push_back('\0');
  text += ReadLhString(base, record + 16u);
  text.push_back('\0');
  text += ReadCharString(base, record + 52u);
  refii_bridge_emotion_choice_stated(record, words, values, text.data(), static_cast<uint32_t>(text.size()));
}

uint32_t ChoicesUp() { return refii_bridge_emotion_choices_read(nullptr, nullptr, nullptr, nullptr, 0u, kMaxChoices); }

}  // namespace

// (icon, request): CWorldIconBase's Init, reached by every class's (`vt+4`).
REX_EXTERN(rex_WorldIcon_Init) {
  const uint32_t icon = ctx.r3.u32;
  __imp__rex_WorldIcon_Init(ctx, base);
  StateIcon(base, icon, -1);
}

// (icon, visible, ctx): CWorldIcon3D's Update, and the first half of the emotion icon's.
REX_EXTERN(rex_WorldIcon3D_Update) {
  const uint32_t icon = ctx.r3.u32;
  const int32_t visible = (ctx.r4.u32 & 0xFFu) != 0u ? 1 : 0;
  __imp__rex_WorldIcon3D_Update(ctx, base);
  StateIcon(base, icon, visible);
}

// (icon, visible, ctx): the HUD-element icon's Update.
REX_EXTERN(rex_HudElementIcon_Update) {
  const uint32_t icon = ctx.r3.u32;
  const int32_t visible = (ctx.r4.u32 & 0xFFu) != 0u ? 1 : 0;
  __imp__rex_HudElementIcon_Update(ctx, base);
  StateIcon(base, icon, visible);
}

// (icon): the emotion icon's ring tick, the one writer of its progress and completion.
REX_EXTERN(rex_EmotionIcon_RingTick) {
  const uint32_t icon = ctx.r3.u32;
  const bool was_complete = Plausible(icon) && Byte(base, icon + 700u) != 0u;
  __imp__rex_EmotionIcon_RingTick(ctx, base);
  StateIcon(base, icon, -1);
  if (Plausible(icon) && !was_complete && Byte(base, icon + 700u) != 0u) {
    REXLOG_INFO("Refii emotion choices: ring complete on '{}' (icon {:08X})", ReadCharString(base, icon + 16u), icon);
  }
}

// (out, R, vector, iterator): the registry erases the icon at *iterator.
REX_EXTERN(rex_IconRegistry_Erase) {
  const uint32_t at = ctx.r6.u32;
  if (Plausible(at)) {
    const uint32_t icon = LoadGuestU32(base, at);
    if (icon != 0u) {
      refii_bridge_world_icon_removed(icon);
    }
  }
  __imp__rex_IconRegistry_Erase(ctx, base);
}

// (icon): CWorldIconBase's destructor, which every class's ends in.
REX_EXTERN(rex_WorldIcon_Destroy) {
  refii_bridge_world_icon_removed(ctx.r3.u32);
  __imp__rex_WorldIcon_Destroy(ctx, base);
}

// (E, shared_ptr*): a record joins the choice list.
REX_EXTERN(rex_EmotionChoices_Add) {
  const uint32_t holder = ctx.r4.u32;
  const uint32_t record = Plausible(holder) ? LoadGuestU32(base, holder) : 0u;
  __imp__rex_EmotionChoices_Add(ctx, base);
  if (Plausible(record)) {
    StateChoice(base, record);
    REXLOG_INFO("Refii emotion choices: + '{}' ('{}', record {:08X}, action {}, holdable {}, manual {}; {} up)",
                ReadCharString(base, record + 4u), ReadCharString(base, record + 52u), record,
                LoadGuestU32(base, record), Byte(base, record + 60u), Byte(base, record + 61u), ChoicesUp());
  }
}

// (record): the record's deleter.
REX_EXTERN(rex_EmotionChoice_Delete) {
  const uint32_t record = ctx.r3.u32;
  if (Plausible(record)) {
    const uint32_t up = ChoicesUp();
    refii_bridge_emotion_choice_removed(record);
    if (const uint32_t now = ChoicesUp(); now < up) {
      REXLOG_INFO("Refii emotion choices: - '{}' (record {:08X}; {} up)", ReadCharString(base, record + 4u), record,
                  now);
    }
  }
  __imp__rex_EmotionChoice_Delete(ctx, base);
}

// (E, &event, player): the press and release handler; its writes are rec+0 and rec+62. A commit
// can delete a record inside it (the deleter above runs first), so only the records still stated
// after it are read.
REX_EXTERN(rex_EmotionChoices_Input) {
  uint32_t records[kMaxChoices];
  uint32_t before[kMaxChoices * REFII_EMOTION_CHOICE_WORDS];
  const uint32_t count = refii_bridge_emotion_choices_read(records, before, nullptr, nullptr, 0u, kMaxChoices);
  __imp__rex_EmotionChoices_Input(ctx, base);
  const uint32_t handled = ctx.r3.u32;
  uint32_t live[kMaxChoices];
  const uint32_t live_count = refii_bridge_emotion_choices_read(live, nullptr, nullptr, nullptr, 0u, kMaxChoices);
  for (uint32_t i = 0; i < count; ++i) {
    const uint32_t record = records[i];
    bool still = false;
    for (uint32_t j = 0; j < live_count && !still; ++j) {
      still = live[j] == record;
    }
    if (!still) {
      continue;
    }
    StateChoice(base, record);
    const uint32_t* was = before + (i * REFII_EMOTION_CHOICE_WORDS);
    const bool held = Byte(base, record + 62u) != 0u;
    const bool was_held = (was[5] & REFII_EMOTION_CHOICE_HELD) != 0u;
    if (held != was_held || LoadGuestU32(base, record) != was[0]) {
      REXLOG_INFO("Refii emotion choices: '{}' {} (action {} -> {}, handler returned {})",
                  ReadCharString(base, record + 4u), held ? "held" : "released", was[0], LoadGuestU32(base, record),
                  handled);
    }
  }
}
