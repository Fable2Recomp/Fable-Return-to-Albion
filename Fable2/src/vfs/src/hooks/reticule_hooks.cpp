// The ranged-aim reticule, stated to the host at its own setter.
//
// The four rotating `target` petals are not a Gameface element: `sub_82179778` pushes each as a
// 2D sprite straight into the command stream (`sub_821DE2E8`), from a per-player record at
// `*(*(GUI+32)+72)` (+64 for player two). The record states everything but the aim point:
// +0 active, +4/+8 the point projected to the guest's 4:3 screen, +12 lock progress, +16 RGBA,
// +32 mode (0 free, 1 and 2 locked), +52 the intro counter.
//
// The aim point is a WORLD anchor — `vector at object+0x90`, which the setter projects through
// `sub_8287EAF0` (0x8287c720 `lvx128 v1, r29, 0x90`). The guest believes it is 4:3, so its
// projection is not ours; the host projects the anchor with Godot's own camera. The setter
// `sub_8287C658(object, active)` is the one statement of which object a player aims with, so it
// posts `@Reticule` — player, object, active — when that pair changes. The host reads the
// anchor and the record live.

#include <cstdint>
#include <cstdio>
#include <string>

#include "vfs_hooks_private.h"
#include "core/guest_state.h"
#include "core/payload_bridge_fable2.h"
#include "core/gui_native_calls.h"
#include "hooks/ppc_externs.h"
#include "core/guest_build.h"

namespace {

/// The game root, and the byte the setter requires with it (0x8287c678..0x8287c69c).
constexpr refii::guest::Addr kGameRoot{0x83496AB8u};
constexpr refii::guest::Addr kInWorld{0x83496A5Eu};

bool Plausible(uint32_t at) { return at >= 0x10000u && at < 0xC0000000u; }

/// Player one's entity — `sub_8222C1B8(**(*(*(game+12)+88)+4))`: the handle block at `EM+104`,
/// its object at `+108`, one more deref.
uint32_t PlayerOneEntity(uint8_t* base) {
    uint32_t at = refii::vfs::LoadGuestU32(base, kGameRoot);
    for (uint32_t offset : {12u, 88u, 4u, 0u}) {
        if (!Plausible(at)) return 0u;
        at = refii::vfs::LoadGuestU32(base, at + offset);
    }
    if (!Plausible(at)) return 0u;
    at = refii_bridge_hero_one_holder(base, at);
    if (!Plausible(at)) return 0u;
    at = refii::vfs::LoadGuestU32(base, at);
    return Plausible(at) ? at : 0u;
}

/// The entity that owns the aiming object — `sub_824CFCB8`'s `*(*(object+4))`, 0 on a dead
/// handle (the guest compares 0 in that case too).
uint32_t Owner(uint8_t* base, uint32_t object) {
    const uint32_t handle = refii::vfs::LoadGuestU32(base, object + 4u);
    return Plausible(handle) ? refii::vfs::LoadGuestU32(base, handle) : 0u;
}

struct Stated {
    uint32_t object = 0;
    bool active = false;
};

/// Per record: what was last posted. The setter runs every frame while a player aims.
Stated g_stated[2];

void State(int player, uint32_t object, bool active) {
    refii::vfs::GuiNativeCall call;
    call.name = "@Reticule";

    char text[9];
    refii::vfs::GuiNativeArgument slot;
    slot.kind = refii::vfs::GuiNativeArgument::Kind::kString;
    slot.text = player == 0 ? "0" : "1";
    call.arguments.push_back(std::move(slot));

    refii::vfs::GuiNativeArgument address;
    address.kind = refii::vfs::GuiNativeArgument::Kind::kString;
    std::snprintf(text, sizeof text, "%08X", object);
    address.text = text;
    call.arguments.push_back(std::move(address));

    refii::vfs::GuiNativeArgument on;
    on.kind = refii::vfs::GuiNativeArgument::Kind::kString;
    on.text = active ? "1" : "0";
    call.arguments.push_back(std::move(on));

    refii::vfs::PostGuiNativeCall(std::move(call));
}

}  // namespace

// sub_8287C658(object, active) — after the guest's own body, and only past its own early-outs
// (no game, not in the world, or the world's +260 byte, 0x8287c6a4), where it wrote nothing.
REX_EXTERN(rex_lhGui_Reticule_SetActiveForObject) {
    const uint32_t object = ctx.r3.u32;
    const bool active = (ctx.r4.u32 & 0xFFu) != 0u;
    __imp__rex_lhGui_Reticule_SetActiveForObject(ctx, base);

    const uint32_t game = refii::vfs::LoadGuestU32(base, kGameRoot);
    if (!Plausible(game) || refii::vfs::LoadGuestU8(base, kInWorld) == 0u) return;
    const uint32_t world = refii::vfs::LoadGuestU32(base, game + 12u);
    if (!Plausible(world) || refii::vfs::LoadGuestU8(base, world + refii::guest::GameField(260u)) != 0u) return;

    // The setter's own choice of record (0x8287c6c8..0x8287c6f0): player one's is the first.
    const int player = Owner(base, object) == PlayerOneEntity(base) ? 0 : 1;
    if (g_stated[player].object == object && g_stated[player].active == active) return;

    g_stated[player] = {object, active};
    State(player, object, active);
}
