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

#include "vfs_hooks_private.h"
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
REX_EXTERN(sub_823CEB58) {
    const uint32_t box = ctx.r3.u32;
    __imp__sub_823CEB58(ctx, base);
    State(base, "@MessageBox.Open", box);
}

// MessageBox::Close — `RemoveDisplayBox` reaches a SHOWING box through this slot too
// (`sub_826B9AA8` 0x826b9b58).
REX_EXTERN(sub_823CEC98) {
    const uint32_t box = ctx.r3.u32;
    __imp__sub_823CEC98(ctx, base);
    State(base, "@MessageBox.Close", box);
}

// MessageBox::~MessageBox — only a box destroyed while still OPEN is stated: a normal close
// already was, and its 0.75 s fade ends here.
REX_EXTERN(sub_823CEAE8) {
    const uint32_t box = ctx.r3.u32;
    if (refii::vfs::LoadGuestU8(base, box + kBoxOpen) != 0u) {
        State(base, "@MessageBox.Close", box);
    }
    __imp__sub_823CEAE8(ctx, base);
}
