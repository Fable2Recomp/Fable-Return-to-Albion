// refii — PLAYER STATE: a mod's values for one player, the same on both co-op machines, and the
// ones a mod marks SAVED kept with that player's own save.
//
// ONE OWNER PER PLAYER: the machine that plays that hero — the host for player one and for a local
// player two, the joiner for an online player two, this machine for both outside a session. The
// owner's values are the truth. A write on the owner applies at once; a write anywhere else applies
// at once there too (so the writer reads its own write) and goes to the owner as an operation, which
// the owner applies — an ADD merges with the owner's own changes, so neither machine's spend is lost.
// The owner restates its values to the peer whenever they change (and every half second), carrying
// the last operation it applied, so the writer drops what has landed and re-applies what has not.
//
// SAVED VALUES go home with the hero: the payload writes them beside the save the game has just
// written that hero into (refii_player.txt in the save's own folder), and reads them back when the
// game loads that hero from a save — player one's main save, a local player two's chosen save, an
// online joiner's own save on the joiner's machine.
//
// The state lives here, in refii_vfs, because the payload (mods, the hooks) and the host bridge (the
// co-op channel, the save root) both link it. The payload's C entries are declared in
// core/payload_bridge.h; mods reach them through refii_mod_player_* in core/refii_mod.h.

#pragma once

#include <cstddef>
#include <cstdint>
#include <string>
#include <string_view>

namespace refii::players {

/// The file a save's folder keeps its hero's saved values in.
inline constexpr std::string_view kSaveFileName = "refii_player.txt";

// --- the host bridge's side -------------------------------------------------------------------

/// How to find the content root the game's saves live under (the emulator's user_data_root): a
/// save named N is the folder `<root>/<user>/4D5307F1/00000001/<N>/`. The host bridge registers
/// it when it loads; it is asked the first time a save is read or written.
using SaveRootResolver = std::string (*)();
void SetSaveRootResolver(SaveRootResolver resolver);

/// Messages for the co-op peer, one per line, appended to `out` (at most `capacity` bytes): the
/// owned players' values when they changed or are due a restatement, and this machine's
/// operations on the players it does not own. Nothing outside a session (the caller sends nothing
/// then) — returns the bytes written.
size_t TakeOutgoing(char* out, size_t capacity);

/// One message from the peer. False when it is not a player-state message.
bool Receive(std::string_view message);

/// A one-line summary of both players' state, for the debug channel and the log.
std::string Describe();

}  // namespace refii::players
