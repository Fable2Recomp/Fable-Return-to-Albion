// The received-item card, stated at the guest's own entry points.
//
// The card (in-game screen 8, "ReceiveItemsScreen.bgf") is pushed by GUI::DisplayReceivedItem
// (`sub_823C4340`), reached from Lua `GUI.DisplayReceivedItem` (chests and quest rewards), the
// world pickup `sub_8293FDA0` (gated on the record's "DisplayReceivedItem") and the gift object
// `sub_82840B48`; its money and discount siblings (`sub_823C44E8`, `sub_823C45E8`) take the same
// road. The opener calls the handler's SET-ITEM (`sub_82537F38`, r3 handler, r4 int** item),
// after which the handler HOLDS the card, verified 2026-10-26:
//   +52  side byte, 1 = front (OnEnter sets it; the input handler's Y toggles it)
//   +56  icon resource hash (IconGraphic, lowercase resource-name hash, `sub_82538DF8`)
//   +60  title text key hash (NameTag through Text_HashKeyNameFnv1)
//   +64  description text key hash (DescriptionTag)
//   +68  extra line key hash (the discount card), +72 its amount
//   +76  the NameAndIcon line, a guest string (weapon better/same/worse, "+N Gold")
//   +80  usable byte (category in {1,5,6,17..25}, cleared by the hero's own flag)
//   +96  preview: the record hash at +0, GUIScreenMesh / ModelFile resource hash at +124
//   +324 the co-op partner's card (SET-PEER-ITEM `sub_82538268`, net message 256)
// The input handler (`sub_82537128`, r4 code) is where the side flips (39); OnLeave
// (`sub_82536F70`) runs on close AND on replace — the next item of a chest re-runs the opener:
// Leave, push 8, Enter, SetItem. Each seam is hooked AFTER the original and posts the handler's
// state as a host-only record (kind 1006, Engine_UiReceiveItemsSurface.StateKind). Godot draws
// the card from that record and nothing else: the presentation's elements were sampled before,
// and the guest rebuilds that presentation under the sampler — a chest's fourth item (2026-10-23)
// and the cards that never drew (2026-10-26) were the cost.
#include <cstdint>
#include <cstdio>
#include <string>

#include <rex/ppc/context.h>
#include <rex/ppc/func.h>

#include "core/guest_build.h"
#include "core/mod_values.h"
#include "core/refii_mod.h"

extern "C" REX_FUNC(__imp__rex_CGUIReceiveItems_SetItem);
extern "C" REX_FUNC(__imp__rex_CGUIReceiveItems_SetPeerItem);
extern "C" REX_FUNC(__imp__rex_lhGuiReceiveItemsScreen_OnScreenAction);
extern "C" REX_FUNC(__imp__rex_CGUIReceiveItems_OnLeave);

namespace {

/// Host-only record kind; the host side names it Engine_UiReceiveItemsSurface.StateKind.
constexpr int32_t kCardKind = 1006;

/// The card's in-game screen id, TU1 numbering.
constexpr uint32_t kCardScreen = 8u;

bool Plausible(uint32_t address) { return address >= 0x10000u && address < 0xC0000000u; }

/// The handler's NameAndIcon line: a guest string handle, two hops (`char***`), NUL-terminated.
/// Quotes, commas and line breaks are dropped so the value stays one quoted token on the wire.
std::string NameLine(const uint8_t* base, uint32_t handle) {
  if (!Plausible(handle)) return std::string();
  const uint32_t block = refii::mods::LoadU32(base, handle);
  if (!Plausible(block)) return std::string();
  const uint32_t chars = refii::mods::LoadU32(base, block);
  if (!Plausible(chars)) return std::string();
  std::string out;
  for (uint32_t i = 0; i < 128u; ++i) {
    const char c = static_cast<char>(refii::mods::LoadU8(base, chars + i));
    if (c == 0) break;
    if (c == '\'' || c == ',' || c == '\n' || c == '\r') continue;
    out.push_back(c);
  }
  return out;
}

/// Whether the card is the GUI's current screen: `*(*(GUI+56)+4)`, the id the screen push
/// writes (`sub_8236E630` 0x8236eb20).
bool CardIsCurrent(const uint8_t* base) {
  const uint32_t gui = refii::mods::LoadU32(base, refii::guest::Address(0x83496B08u));
  if (!Plausible(gui)) return false;
  const uint32_t screens = refii::mods::LoadU32(base, gui + 56u);
  if (!Plausible(screens)) return false;
  return refii::guest::ScreenIdToTu1(refii::mods::LoadU32(base, screens + 4u)) == kCardScreen;
}

void PostCard(const uint8_t* base, uint32_t handler, bool up) {
  if (!Plausible(handler)) return;
  char fields[512];
  if (!up) {
    std::snprintf(fields, sizeof(fields), "up=0");
  } else {
    const uint32_t preview = handler + 96u;
    std::snprintf(
        fields, sizeof(fields),
        "up=1,peer=%u,front=%u,usable=%u,record=%u,model=%u,icon=%u,title=%u,desc=%u,extra=%u,"
        "amount=%u,name='%s'",
        refii::mods::LoadU8(base, handler + 324u) != 0u ? 1u : 0u,
        refii::mods::LoadU8(base, handler + 52u) != 0u ? 1u : 0u,
        refii::mods::LoadU8(base, handler + 80u) != 0u ? 1u : 0u,
        refii::mods::LoadU32(base, preview), refii::mods::LoadU32(base, preview + 124u),
        refii::mods::LoadU32(base, handler + 56u), refii::mods::LoadU32(base, handler + 60u),
        refii::mods::LoadU32(base, handler + 64u), refii::mods::LoadU32(base, handler + 68u),
        refii::mods::LoadU32(base, handler + 72u), NameLine(base, handler + 76u).c_str());
  }
  refii::mods::PostEvent(kCardKind, fields);
}

}  // namespace

// SET-ITEM: the local hero's card, filled from the item. TU1 0x82537F38, disc 0x824036D8.
extern "C" REX_FUNC(rex_CGUIReceiveItems_SetItem) {
  REX_FUNC_PROLOGUE();
  const uint32_t handler = ctx.r3.u32;
  __imp__rex_CGUIReceiveItems_SetItem(ctx, base);
  PostCard(base, handler, true);
}

// SET-PEER-ITEM: the co-op partner's card (+324). TU1 0x82538268, disc 0x82403A08.
extern "C" REX_FUNC(rex_CGUIReceiveItems_SetPeerItem) {
  REX_FUNC_PROLOGUE();
  const uint32_t handler = ctx.r3.u32;
  __imp__rex_CGUIReceiveItems_SetPeerItem(ctx, base);
  PostCard(base, handler, true);
}

// INPUT: the side flips on 39 and the usable flag may change on 75; 36 and 75 close through the
// pop, which runs OnLeave (below) inside this call — so the state is posted only while the card is
// still the current screen, or a close would be followed by a stale "up". The function already
// carries its name for the pump's ReceiveItemsAction dispatch. TU1 0x82537128, disc 0x824028C8.
extern "C" REX_FUNC(rex_lhGuiReceiveItemsScreen_OnScreenAction) {
  REX_FUNC_PROLOGUE();
  const uint32_t handler = ctx.r3.u32;
  __imp__rex_lhGuiReceiveItemsScreen_OnScreenAction(ctx, base);
  if (CardIsCurrent(base)) {
    PostCard(base, handler, true);
  }
}

// ON-LEAVE: the card is down — on close, and on replace before the next item's SetItem.
// TU1 0x82536F70, disc 0x82402710.
extern "C" REX_FUNC(rex_CGUIReceiveItems_OnLeave) {
  REX_FUNC_PROLOGUE();
  const uint32_t handler = ctx.r3.u32;
  __imp__rex_CGUIReceiveItems_OnLeave(ctx, base);
  PostCard(base, handler, false);
}
