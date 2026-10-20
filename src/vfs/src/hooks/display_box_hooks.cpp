// The display MessageBox, stated to the host at its own Open and Close.
//
// Class vtable 0x820AE668 (ctor `sub_823CEA78`): slot 0 dtor `sub_823CEAE8`, slot 1 Open
// `sub_823CEB58`, slot 2 Close `sub_823CEC98`, slot 3 action input `sub_823CEDD8`. Every one
// goes through the box queue at `*(*(GUI+28)+52)+4` — `GUI.DisplayMessageBox` (0x82302824), the
// modal hints (`sub_82450538` 0x82450654), the pause menu's notices, the sign-in box and ~25 more
// natives build one there — and the queue's tick opens the head when nothing else is up.
//
// Most of them never pass a `GUI.*` native, so the call feed cannot see them, and none of them
// can be answered by a raw pad press: the router hands the raw event to the box's slot +16
// (`sub_82403350` 0x82403634), which for this class is `li r3,0; blr`. Only action code 36
// through slot 3 closes one. Open pushes GameFlow state 8 (`sub_823D0A70(box, 0)` 0x823cec70),
// which takes both players' movement until that close — an unseen box is a frozen game.
//
// So Open and Close post `@MessageBox.Open` / `@MessageBox.Close` onto the GUI call feed, with
// the box's address and its identity (`box+4`, the name `RemoveDisplayBox` matches). The host
// draws the box from its live `messageboxmulti` presentation and answers the stated address.

#include <cstdint>
#include <cstdio>
#include <string>
#include <vector>

#include "vfs_hooks_private.h"
#include "core/guest_build.h"
#include "core/guest_state.h"
#include "gui/gui_native_calls.h"
#include "hooks/ppc_externs.h"

namespace {

/// The open byte every handler gates on — `lbz r11, 0xC(r3)`; Open sets it at 0x823cec7c,
/// Close clears it at 0x823cedb8.
constexpr uint32_t kBoxOpen = 12u;

/// The identity handle, 8-bit class — Close copies it with `strcpy_s(**(char***)(box+4))`
/// at 0x823ced18.
constexpr uint32_t kBoxIdentity = 4u;

void State(uint8_t* base, const char* name, uint32_t box) {
    refii::vfs::GuiNativeCall call;
    call.name = name;

    refii::vfs::GuiNativeArgument address;
    address.kind = refii::vfs::GuiNativeArgument::Kind::kString;
    char hex[9];
    std::snprintf(hex, sizeof hex, "%08X", box);
    address.text = hex;
    call.arguments.push_back(std::move(address));

    refii::vfs::GuiNativeArgument identity;
    identity.kind = refii::vfs::GuiNativeArgument::Kind::kString;
    identity.text = refii::vfs::ReadGuestLhString(base, box + kBoxIdentity);
    call.arguments.push_back(std::move(identity));

    refii::vfs::PostGuiNativeCall(std::move(call));
}

}  // namespace

// MessageBox::Open — stated after the guest's own Open, so the presentation exists and the open
// byte is set by the time the host asks for either.
REX_EXTERN(rex_lhDisplayMessageBox_Open) {
    const uint32_t box = ctx.r3.u32;
    __imp__rex_lhDisplayMessageBox_Open(ctx, base);
    State(base, "@MessageBox.Open", box);
}

// MessageBox::Close — `RemoveDisplayBox` reaches a SHOWING box through this slot too
// (`sub_826B9AA8` 0x826b9b58).
REX_EXTERN(rex_lhDisplayMessageBox_Close) {
    const uint32_t box = ctx.r3.u32;
    __imp__rex_lhDisplayMessageBox_Close(ctx, base);
    State(base, "@MessageBox.Close", box);
}

// MessageBox::~MessageBox — only a box destroyed while still OPEN is stated: a normal close
// already was, and its 0.75 s fade ends here.
REX_EXTERN(rex_lhDisplayMessageBox_Dtor) {
    const uint32_t box = ctx.r3.u32;
    if (refii::vfs::LoadGuestU8(base, box + kBoxOpen) != 0u) {
        State(base, "@MessageBox.Close", box);
    }
    __imp__rex_lhDisplayMessageBox_Dtor(ctx, base);
}

// ---------------------------------------------------------------------------------------------
// A BOX FOR ONE PLAYER (local split screen). The box classes name no player: `GUI.DisplayMessageBox`
// (`sub_823027B0`) and `GUI.DisplayYesNoQuestion` (`sub_823044A8`) build their box and push it on the
// owner's +4 queue (`sub_826B99D8(owner + 4, box)` at 0x82302824 / 0x82304520), the menu boxes on the
// +0x20 one, and the queue's tick opens it later. The core co-op Lua states whose a box is around the
// one guest call that builds it (Refii._ui_seat): every box that call queued is that player's, stated
// on the GUI call feed as `@Box.Seat <address> <player>` so the host seats it in their half and gives
// it their pad. A queue `{vtable, ?, begin +8, end +12, capacity +16, current +20}` holds 8-byte
// elements whose first word is the box (`sub_822F5948`); the current box is +20.
// ---------------------------------------------------------------------------------------------

namespace {

constexpr uint32_t kGuiGlobal = 0x83496B08u;
constexpr uint32_t kQueues[] = {4u, 0x20u};
constexpr uint32_t kMaxQueued = 64u;

bool PlausibleBox(uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; }

uint32_t BoxOwner(uint8_t* base) {
    const uint32_t gui = refii::vfs::LoadGuestU32(base, refii::guest::Address(kGuiGlobal));
    const uint32_t holder = PlausibleBox(gui) ? refii::vfs::LoadGuestU32(base, gui + 28u) : 0u;
    const uint32_t owner = PlausibleBox(holder) ? refii::vfs::LoadGuestU32(base, holder + 52u) : 0u;
    return PlausibleBox(owner) ? owner : 0u;
}

/// Every box the two queues hold now, current ones included.
std::vector<uint32_t> QueuedBoxes(uint8_t* base) {
    std::vector<uint32_t> boxes;
    const uint32_t owner = BoxOwner(base);
    if (owner == 0u) {
        return boxes;
    }
    for (const uint32_t queue : kQueues) {
        const uint32_t begin = refii::vfs::LoadGuestU32(base, owner + queue + 8u);
        const uint32_t end = refii::vfs::LoadGuestU32(base, owner + queue + 12u);
        if (PlausibleBox(begin) && end > begin && (end - begin) / 8u <= kMaxQueued) {
            for (uint32_t at = begin; at < end; at += 8u) {
                const uint32_t box = refii::vfs::LoadGuestU32(base, at);
                if (PlausibleBox(box)) {
                    boxes.push_back(box);
                }
            }
        }
        const uint32_t current = refii::vfs::LoadGuestU32(base, owner + queue + 20u);
        if (PlausibleBox(current)) {
            boxes.push_back(current);
        }
    }
    return boxes;
}

struct SeatScope {
    int32_t player = 0;
    std::vector<uint32_t> before;
};

/// Guest tick thread only (the Lua natives run there).
std::vector<SeatScope> g_seat_scopes;

}  // namespace

namespace refii::vfs {

void BoxSeatEnter(uint8_t* base, int32_t player) {
    if (g_seat_scopes.size() >= 8u) {
        return;
    }
    g_seat_scopes.push_back(SeatScope{player, QueuedBoxes(base)});
}

void BoxSeatLeave(uint8_t* base) {
    if (g_seat_scopes.empty()) {
        return;
    }
    const SeatScope scope = std::move(g_seat_scopes.back());
    g_seat_scopes.pop_back();
    for (const uint32_t box : QueuedBoxes(base)) {
        bool known = false;
        for (const uint32_t was : scope.before) {
            known = known || was == box;
        }
        if (known) {
            continue;
        }
        refii::vfs::GuiNativeCall call;
        call.name = "@Box.Seat";
        refii::vfs::GuiNativeArgument address;
        address.kind = refii::vfs::GuiNativeArgument::Kind::kString;
        char hex[9];
        std::snprintf(hex, sizeof hex, "%08X", box);
        address.text = hex;
        call.arguments.push_back(std::move(address));
        refii::vfs::GuiNativeArgument player;
        player.kind = refii::vfs::GuiNativeArgument::Kind::kString;
        player.text = std::to_string(scope.player);
        call.arguments.push_back(std::move(player));
        refii::vfs::PostGuiNativeCall(std::move(call));
    }
}

}  // namespace refii::vfs
