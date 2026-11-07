// refii - THE TEXTS FABLE 3 COMMITS STRAIGHT TO A TEXT BOX, stated where the commit is guarded,
// for Godot's Fable3Elements. Every address is the Fable 3 disc IDB (port 13339); each offset
// names its base.
//
// THE COMMIT. GuiTextBox_Commit sub_82B45268(box, LHString* text) assigns the box's shown string
// (box+0x88, 0x82B452A4). Its callers outside the stated writers (sub_82819070, sub_82BEA6D8,
// sub_82BEF428, sub_82961340, ui_stated_hooks.cpp) reach it the same way: the element `rE`, then
// `lwz r11,4(rE)` / `lwz r11,0(r11)` — the element's renderable node and its text box — each
// tested for 0, and only then `bl sub_82B45268` with the text in r4. A headless element's +4 is 0
// (headless_presentation_hooks.cpp), so the commit is never reached and the text is said nowhere
// else. Each [[midasm_hook]] below sits ON that `lwz r11,4(rE)`, where rE is the element and the
// text is already built: in a register the caller then moves to r4, or in a stack LHString the
// caller takes the address of (`addi r4,r1,X`). The statement is `@Ui.Text(pres, elem, path,
// text, "82B45268")` — the element named by the resolver hook (ElementName), the text exactly as
// the commit would have been given it.
//
// THE SITES (guard address -> element, text), Fable3/versions/Fable3/refiii_config.toml:
//   money alert   sub_8281E678 (show / change) 0x8281E6C4 r11, r4 — `sub_82300020(&s, A+16)` "%d"
//                 at 0x8281E6B0; sub_8281E750 (the count) 0x8281E90C r11, r4 (0x8281E8F8);
//                 the floaties sub_8281DBE8 0x8281DC70 r11, r1+0x50
//   crime timer   fine sub_825E5270 0x825E52C4 r11, r4; seconds sub_825E5308 0x825E535C r11, r4
//   job           sub_82950B60 0x82950BD0 r11, r4 (prefix + level, sub_82D73978 at 0x82950BB4)
//   look-at       sub_82B14CB8 0x82B14E48 r31, r27
//   quest summary sub_82973FB0 0x829741A4 r3, r29; 0x829741F8 r3, r1+0x54; 0x82974244 r3, r1+0x58
//   stats         sub_8296ED30 0x8296EE98 r31, r1+0x50; sub_8296EF78 0x8296F65C r3, r1+0x7C,
//                 0x8296F6D8 r31, r1+0x64, 0x8296F824 r30, r3, 0x8296F85C r27, r1+0x70,
//                 0x8296F8C8 r30, r1+0x80, 0x8296F90C r27, r1+0x84; sub_82970538 0x82970610
//                 r31, r23, 0x82970684 r31, r27, 0x829706F8 r31, r24; sub_829708B0 0x82970D88
//                 r31, r1+0x50, 0x82970F24 r31, r1+0x54, 0x82970F7C r31, r3
//   promise       CPromiseMessageBox_Show sub_827A3398 0x827A3438 r30, r3
//   divorce       sub_82928BB0 0x82928C04 / 0x82928C5C r3, r30; sub_82928C90 0x82928D34 r31, r4,
//                 0x82928F48 r3, r1+0x50; sub_82928F78 0x82929058 r3, r1+0x50, 0x829290B4 r3,
//                 r30, 0x82929178 r30, r4, 0x829291B8 r28, r4, 0x8292928C r27, r1+0x5C,
//                 0x829292B4 r26, r1+0x58, 0x82929314 r3, r1+0x64; sub_82929378 0x82929468 r30,
//                 r4, 0x829294AC r29, r1+0x54, 0x829294E8 r27, r1+0x54, 0x82929594 r26, r1+0x54,
//                 0x829295E0 r3, r1+0x58; sub_829297D8 0x82929840 r31, r4; sub_829298F0
//                 0x82929944 r31, r3
//   food          CFoodItemQueryBox_Show sub_82B579E8 0x82B57C30 r29, r1+0x50, 0x82B57E0C r31, r3
//   bed           CBedInteractionMessageBox_vf5 sub_82B59298 0x82B59304 r11, r1+0x50
//   credits       the roll's init sub_82B3F980 0x82B3FBF0 r31, r1+0x50; its tick sub_82B3FD70
//                 0x82B3FE88 r30, r1+0x50 (the empty line past the end) and 0x82B3FE40 r30 —
//                 the next line, `*(roll+0x1C) + 4 * roll+0x14` (0x82B3FE58..0x82B3FE60), r31 the
//                 roll and r11 its index (0x82B3FE18).
// A text register the caller moves to r4 after the guard (`mr r4,rS`) is not written between the
// guard and that move (the guard touches only r11/r10 and cr6).

#include <cstdint>
#include <string>

#include <rex/ppc/context.h>
#include <rex/system/kernel_state.h>
#include <rex/system/xmemory.h>

#include "gui_statements.h"

namespace {

using namespace refii::fable3;

constexpr const char* kVia = "82B45268";

uint8_t* GuestBase() {
  rex::system::KernelState* state = rex::system::kernel_state();
  rex::memory::Memory* memory = state != nullptr ? state->memory() : nullptr;
  return memory != nullptr ? memory->virtual_membase() : nullptr;
}

void StateCommit(uint32_t element, uint32_t text) {
  uint8_t* base = GuestBase();
  if (base == nullptr || !Plausible(element) || !Plausible(text)) {
    return;
  }
  uint32_t presentation = 0u;
  std::string path;
  if (!ElementName(element, presentation, path)) {
    return;
  }
  StateText(presentation, element, path, ReadLhString(base, text), kVia);
}

}  // namespace

/// The element and the text's LHString in registers.
void RefiiF3StateCommit(PPCRegister& element, PPCRegister& text) { StateCommit(element.u32, text.u32); }

// The element in a register, the text a stack LHString at r1 + the offset in the name.
#define REFII_F3_STACK_COMMIT(offset)                                                 \
  void RefiiF3StateCommitAt##offset(PPCRegister& element, PPCRegister& stack) {       \
    StateCommit(element.u32, stack.u32 + 0x##offset##u);                              \
  }
REFII_F3_STACK_COMMIT(50)
REFII_F3_STACK_COMMIT(54)
REFII_F3_STACK_COMMIT(58)
REFII_F3_STACK_COMMIT(5C)
REFII_F3_STACK_COMMIT(64)
REFII_F3_STACK_COMMIT(70)
REFII_F3_STACK_COMMIT(7C)
REFII_F3_STACK_COMMIT(80)
REFII_F3_STACK_COMMIT(84)
#undef REFII_F3_STACK_COMMIT

/// The credits roll's next line: `*(roll+0x1C) + 4 * index` (sub_82B3FD70 at 0x82B3FE58..0x82B3FE60).
void RefiiF3StateCreditsLine(PPCRegister& element, PPCRegister& roll, PPCRegister& index) {
  uint8_t* base = GuestBase();
  if (base == nullptr || !Plausible(roll.u32)) {
    return;
  }
  const uint32_t lines = LoadGuestU32(base, roll.u32 + 0x1Cu);
  if (!Plausible(lines)) {
    return;
  }
  StateCommit(element.u32, lines + 4u * index.u32);
}

/// THE CREDITS' POSITIONS, stated at the roll's own SetPosition calls (sub_82818580: r3 the
/// element, r4 the presentation, f1..f3 x, y, z): each row at the roll's init (0x82B3FC50, y
/// stepping down by 32, `fsubs f31,f31,f29` 0x82B3FC58) and tick (0x82B3FED0, y = the read-back
/// plus the step, `fadds f31,f13,f30` 0x82B3FDF8), the logo sheet at init (0x82B3FCE8) and as it
/// scrolls once the lines ran out (0x82B3FF1C, +0x8C its y). `@Ui.Position(pres, elem, path, x,
/// y, z, via)`: Godot takes the credits' positions from these alone (Fable3Elements' cut drops
/// the setter's own `@Element.Position` for the credits document).
void RefiiF3CreditsPosition(PPCRegister& element, PPCRegister& presentation, PPCRegister& x, PPCRegister& y,
                            PPCRegister& z) {
  uint32_t named_in = 0u;
  std::string path;
  if (!Plausible(element.u32) || !ElementName(element.u32, named_in, path)) {
    return;
  }
  Statement("@Ui.Position")
      .Add(Text(PresentationKey(presentation.u32 != 0u ? presentation.u32 : named_in)))
      .Add(Text(Hex(element.u32)))
      .Add(Text(path))
      .Add(Number(static_cast<float>(x.f64)))
      .Add(Number(static_cast<float>(y.f64)))
      .Add(Number(static_cast<float>(z.f64)))
      .Add(Text("82B3FD70"))
      .Post();
}

/// THE LOOK-AT ALERT'S SLIDE, as its writer sub_82B14CB8(LA, record, hero) chose it from the
/// record the HUD tick sub_8226A600 polled (0x8226A714): 2 for type 0, 4 for 1, 5 for 2 (`lwz
/// v3,+4` 0x82B14CC4..0x82B14CFC), 8 when the record's +8 flag is set (`lbz r11,8(r27)`
/// 0x82B14D00, `li r29,8` 0x82B14D10), 1 while the interaction mode's IM+24 is set (0x82B14D1C..
/// 0x82B14D2C) — r29 at 0x82B14D30, before the element's SetState (0x82B14D48). The tick's hide is
/// slide 6 (`li r5,6` 0x8226A740, SetState 0x8226A750), reached at 0x8226A730.
/// hud.bgf `LookAt.LookAt.LookAt` (#510): 1 hidden, 2 shown, 4/5 blink, 6 to hidden, 8 in active.
void RefiiF3LookAtSlide(PPCRegister& slide, PPCRegister& record) {
  uint8_t* base = GuestBase();
  const uint32_t type = base != nullptr && Plausible(record.u32) ? LoadGuestU32(base, record.u32 + 4u) : 0u;
  Statement("@Hud.LookAt")
      .Add(Number(static_cast<float>(static_cast<int32_t>(slide.u32))))
      .Add(Number(static_cast<float>(static_cast<int32_t>(type))))
      .Post();
}

void RefiiF3LookAtHidden() { Statement("@Hud.LookAt").Add(Number(6.0f)).Add(Number(-1.0f)).Post(); }
