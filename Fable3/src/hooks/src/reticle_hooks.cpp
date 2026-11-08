// refii - Fable 3's aim reticules, stated to the host at their own setters.
//
// Every address is the Fable 3 TU3 IDB ("disc" marks one TU3 lacks); each offset names its base.
//
// NOT A GAMEFACE ELEMENT. The HUD's draw sub_82285218(H) (from the GUI draw sub_82254D20 at
// 0x82254FDC / 0x8225507C, H = *(GUI+0x20), GUI = *0x83596F84) walks the reticule manager
// M = *(H+0x3C) (0x8228525C): two arrays, player one's at *(M+4) and player two's at *(M+0x14),
// each two 48-byte records (`addi r31, r31, 0x30` / `cmpwi r31, 0x60`, 0x8228529C..0x822852A4).
// For each record it calls sub_8228E9B0(record, 0) and bumps the record's +0x28 counter. That draw
// pushes ONE `Art\GUI\Persistant\target[A].tex` sprite as a render-scene object
// (RenderScene_AddObject at 0x8228ED2C, update sub_821C20E0 at 0x8228ED3C), which nothing draws
// under the null device. A record:
//   +0x00 active (byte)                +0x04/+0x08 the point, guest pixels from the screen centre,
//   +0x0C lock progress                  y up (sub_829734A0 stores both at 0x82973518)
//   +0x10..+0x1F RGBA floats            +0x20 mode     +0x24 the sprite's render object id
//   +0x28 frames since the mode changed (zeroed by each setter when the mode changes)
//
// RECORD 1 IS THE RANGED AIM. sub_828AFD90(aimer, active) writes it: `stb r30, 0(r31)` at
// disc 0x82853718 into `*(M+4)+0x30` when the aimer's owner is player one, `*(M+0x14)+0x30` otherwise
// (sub_8238DD58(aimer, P1) at 0x828AFE08, `addi r31, r11, 0x30` disc 0x82853708). While active it
// projects the aim point aimer+0x80 (`li r10, 0x80; lvx128 v1, r29, r10` disc 0x82853728/0x828AFE58)
// through the guest camera (sub_828B1A08) into +4/+8, and sets the mode (0 free, 1 and 2 locked:
// 0x828AFF2C..disc 0x828538A8) and the colour (disc 0x820FCF80 player one, disc 0x820FCF70 player two). It
// returns before writing when there is no game, when `*(game+0xC)+0xBA` is clear, or when
// sub_8234F058(*(game+0xC)) holds (`lwz r3,0xC(r11)` / `bl sub_8234F058` 0x828AFDD4..0x828AFDE4) —
// that is +0xB4 or +0xB9 set (the disc's disc sub_821B5738(game) tests +0xB4 only), its other clause
// ending in game vtable 0x82001A84 slot 6, which is sub_82F5D180 `return 0`. The aim point is a WORLD anchor, so the host projects it with Godot's
// camera; this hook states which aimer, as Fable 2's reticule_hooks.cpp does.
//
// RECORD 0 IS THE SPELL TARGET. sub_82727330(caster), run each tick from sub_82177348
// (0x82177590), writes `*(M+4)` for a caster whose +0x236F byte is set, else `*(M+0x14)`
// (0x827274FC..0x82727524): active (`stb r30, 0(r31)` 0x8272752C) while caster+0x2358 > 0 and the
// target `*(*(caster+4))` is live; the point is that target's sub_825D6F38 position projected and
// clamped inside the safe area (disc 0x826F377C..0x827274F8); mode = sub_825D36E0(target) (0x82727548);
// colour (1, 1, 1, caster+0x2358) (disc 0x826F3938..0x827275AC). The point is computed, never stored,
// so the record's +4/+8 is the guest's statement of where it goes.
//
// WHAT THE DRAW MAKES OF IT. sub_8228E9B0(record, 0) colours the sprite with the record's RGBA,
// or qword_8359CF00 (copied from disc 0x820FD640 = (0.906, 0.271, 0.251, 0.8)) when the mode is not 0
// (`cmpwi cr6,r4,0` on +0x20 at 0x8228EB64, the copy at 0x8228EC38..0x8228EC54), in the guest's
// backbuffer pixels (height 0x835A78B4).
//
// Each hook runs the guest's body first, then states the record it wrote, as it left it, to the
// table the host bridge reads (core/payload_bridge.h: refii_bridge_reticle_stated): the point,
// the progress, the colour as drawn, the mode and +0x28, and for the ranged aim the aim point
// aimer+0x80 the setter projected (its WORLD anchor, which the host projects with Godot's camera).
// +0x28 is the draw's own count (bumped per draw, zeroed by a setter's mode change), so it is
// stated as of the setter. It also posts `@Reticule(player, slot, object, active)` when that
// player's slot changes.

#include <cstdint>
#include <cstdio>
#include <string>

#include <rex/ppc.h>

#include "core/payload_bridge.h"
#include "gui_statements.h"
#include "../../vfs/include/fable3_guest_build.h"

REX_EXTERN(__imp__rex_HudReticule_SetRangedAim);
REX_EXTERN(__imp__rex_HudReticule_UpdateSpellTarget);

namespace {

using refii::fable3::LoadGuestF32;
using refii::fable3::LoadGuestU32;
using refii::fable3::Plausible;

const refii::guest::Addr kGame{0x83597034u};
const refii::guest::Addr kGuiManager{0x83596F84u};
const refii::guest::Addr kLockedColour{0x8359CF00u};
const refii::guest::Addr kBackBufferHeight{0x835A78B4u};
constexpr uint32_t kRecordBytes = 0x30u;
constexpr uint32_t kAimPoint = 0x80u;

uint32_t Word(const uint8_t* base, uint32_t at) { return Plausible(at) ? LoadGuestU32(base, at) : 0u; }

/// The game state `*(game+0xC)`, 0 when none.
uint32_t GameState(const uint8_t* base) { return Word(base, Word(base, kGame) + 0xCu); }

/// What a WeakRef at `holder+4` resolves to: sub_82691588 / sub_8238DD58's `*(*(x+4))`, 0 when dead.
uint32_t Resolve(const uint8_t* base, uint32_t holder) {
  const uint32_t node = Word(base, holder + 4u);
  return Plausible(node) ? LoadGuestU32(base, node) : 0u;
}

/// Player one's entity as the setter takes it: X = *(*(*(state+0x1C)+4)+4)
/// (0x828AFDF0..0x828AFDF8), then sub_821FB318(X) = `*(*(X+0x80))`.
uint32_t PlayerOneEntity(const uint8_t* base, uint32_t state) {
  const uint32_t x = Word(base, Word(base, Word(base, state + 0x1Cu) + 4u) + 4u);
  return Plausible(x) ? Resolve(base, x + 0x7Cu) : 0u;
}

/// Player `player`'s record array, `*(M+4)` or `*(M+0x14)`, M = *(*(GUI+0x20)+0x3C).
uint32_t RecordArray(const uint8_t* base, int player) {
  const uint32_t manager = Word(base, Word(base, Word(base, kGuiManager) + 0x20u) + 0x3Cu);
  return Plausible(manager) ? Word(base, manager + (player == 0 ? 0x4u : 0x14u)) : 0u;
}

struct Stated {
  uint32_t object = 0;
  bool active = false;
  bool posted = false;
};

/// What each player's slot last posted ([player][slot]). Each setter runs on the game thread.
Stated g_stated[2][2];

std::string Hex(uint32_t value) {
  char text[9];
  std::snprintf(text, sizeof text, "%08X", value);
  return text;
}

void State(int player, int slot, uint32_t object, bool active) {
  Stated& last = g_stated[player][slot];
  if (last.posted && last.object == object && last.active == active) {
    return;
  }
  last = {object, active, true};

  refii::vfs::GuiNativeCall call;
  call.name = "@Reticule";
  for (std::string text : {std::string(player == 0 ? "0" : "1"), std::string(slot == 0 ? "0" : "1"),
                           Hex(object), std::string(active ? "1" : "0")}) {
    refii::vfs::GuiNativeArgument argument;
    argument.kind = refii::vfs::GuiNativeArgument::Kind::kString;
    argument.text = std::move(text);
    call.arguments.push_back(std::move(argument));
  }
  refii::vfs::PostGuiNativeCall(std::move(call));
}

/// Player `player`'s `slot` record as its setter left it; `aimer` the ranged aimer (0 for none).
void StateRecord(const uint8_t* base, int player, int slot, uint32_t aimer) {
  const uint32_t array = RecordArray(base, player);
  if (!Plausible(array)) {
    return;
  }
  const uint32_t record = array + (static_cast<uint32_t>(slot) * kRecordBytes);
  const bool active = base[record] != 0u;
  if (!active) {
    refii_bridge_reticle_stated(static_cast<uint32_t>(player), static_cast<uint32_t>(slot), 0, nullptr, nullptr);
    return;
  }
  const uint32_t mode = LoadGuestU32(base, record + 0x20u);
  const uint32_t colour = mode != 0u ? kLockedColour : record + 0x10u;
  const float height = static_cast<float>(LoadGuestU32(base, kBackBufferHeight));
  const bool anchored = Plausible(aimer);
  const float values[REFII_RETICLE_VALUES] = {
      LoadGuestF32(base, record + 0x4u),
      LoadGuestF32(base, record + 0x8u),
      LoadGuestF32(base, record + 0xCu),
      LoadGuestF32(base, colour),
      LoadGuestF32(base, colour + 0x4u),
      LoadGuestF32(base, colour + 0x8u),
      LoadGuestF32(base, colour + 0xCu),
      anchored ? LoadGuestF32(base, aimer + kAimPoint) : 0.0f,
      anchored ? LoadGuestF32(base, aimer + kAimPoint + 0x4u) : 0.0f,
      anchored ? LoadGuestF32(base, aimer + kAimPoint + 0x8u) : 0.0f,
      height > 0.0f ? height : 720.0f,
  };
  const uint32_t words[REFII_RETICLE_WORDS] = {mode, LoadGuestU32(base, record + 0x28u)};
  refii_bridge_reticle_stated(static_cast<uint32_t>(player), static_cast<uint32_t>(slot), 1, values, words);
}

}  // namespace

// sub_828AFD90(aimer, active): stated past the guest's own early-outs, where it wrote nothing.
REX_EXTERN(rex_HudReticule_SetRangedAim) {
  const uint32_t aimer = ctx.r3.u32;
  const bool active = (ctx.r4.u32 & 0xFFu) != 0u;
  __imp__rex_HudReticule_SetRangedAim(ctx, base);

  const uint32_t state = GameState(base);
  // TU3's own early-out (sub_8234F058) also holds on +0xB9 (`lbz r11,0xB9(r3)` / `bne` 0x8234F074);
  // the disc's (disc sub_821B5738) tests +0xB4 only.
  const bool held = base[state + 0xB4u] != 0u ||
                    (refii::guest::Current() == refii::guest::Build::kTu3 && base[state + 0xB9u] != 0u);
  if (!Plausible(state) || base[state + 0xBAu] == 0u || held) {
    return;
  }

  const int player = Resolve(base, aimer) == PlayerOneEntity(base, state) ? 0 : 1;
  StateRecord(base, player, 1, aimer);
  State(player, 1, aimer, active);
}

// sub_82727330(caster): its record's active byte is written on every call (0x8272752C).
REX_EXTERN(rex_HudReticule_UpdateSpellTarget) {
  const uint32_t caster = ctx.r3.u32;
  __imp__rex_HudReticule_UpdateSpellTarget(ctx, base);
  if (!Plausible(caster)) {
    return;
  }

  const int player = base[caster + 0x236Fu] != 0u ? 0 : 1;
  const uint32_t record = RecordArray(base, player);
  if (!Plausible(record)) {
    return;
  }

  const bool active = base[record] != 0u;
  StateRecord(base, player, 0, 0u);
  State(player, 0, active ? Resolve(base, caster) : 0u, active);
}
