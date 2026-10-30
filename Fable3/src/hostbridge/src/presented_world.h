// refii - which of Fable 3's worlds is on screen.
//
// In the retail front end two game components stand at once, each with its own game, world and
// entity manager: the front-end wrapper (vtable 0x82095030; the white room, then the Sanctuary)
// and the main game (0x82001AE8) it creates on its first Run. Only one runs at a time, and the one
// running is the one presented. The list cursor and dword_83556540 are not that statement — the
// latter is retargeted briefly by ~80 swap sites.
//
// THE STATEMENT. sub_82344C48(game) makes a game current: `mr r26,r3` at entry, then
// `stw r26,dword_83556534` at 0x82344C90 (and again at 0x82345838). Those are the only writers, so
// *0x83556534 is the game last made current. It is never cleared, so it is accepted only while a
// world component in the list (G = *0x83556448: sentinel *(G+0), nodes {next, prev, component})
// still owns it (*(component+0xC) == game).

#pragma once

#include <cstdint>

#include "host_core.h"

namespace refii::fable3 {

constexpr uint32_t kComponentManager = 0x83556448u;
constexpr uint32_t kPresentedGame = 0x83556534u;
constexpr uint32_t kMainGameComponent = 0x82001AE8u;
constexpr uint32_t kGuiWorldComponent = 0x82095030u;

inline bool PlausibleGuest(uint32_t address) { return address >= 0x10000u && address < 0xC0000000u; }

inline uint32_t GuestWord(const uint8_t* base, uint32_t address) {
    return PlausibleGuest(address) ? refii::hostcore::GuestWordAt(base, address) : 0u;
}

/// The game of the world on screen, or 0 when none of the world components owns it.
inline uint32_t PresentedGame(const uint8_t* base) {
    const uint32_t game = GuestWord(base, kPresentedGame);
    if (!PlausibleGuest(game)) {
        return 0u;
    }

    const uint32_t sentinel = GuestWord(base, GuestWord(base, kComponentManager));
    uint32_t node = GuestWord(base, sentinel);
    for (uint32_t walked = 0; PlausibleGuest(node) && node != sentinel && walked < 8u; ++walked) {
        const uint32_t component = GuestWord(base, node + 8u);
        const uint32_t vtable = GuestWord(base, component);
        if ((vtable == kMainGameComponent || vtable == kGuiWorldComponent) &&
            GuestWord(base, component + 0xCu) == game) {
            return game;
        }

        node = GuestWord(base, node);
    }

    return 0u;
}

}  // namespace refii::fable3
