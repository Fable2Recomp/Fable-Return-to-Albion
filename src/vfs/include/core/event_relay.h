// refii — THE EVENT RELAY: mod events decided on one co-op machine, carried to the other.
//
// Online, an event fires where the game decides it: a swing, a roll or an item use where that hero
// is played, a hit landing on the host. A mod whose effect runs on the other machine (the host's AI
// punishing player two's whiff, the joiner's look following the victims of a hit) needs the event
// THERE. The payload (mod_events.cpp, its relay table) posts a compact copy of each such event and
// dispatches the copies that arrive, marked as replicas (refii_mod_event_replica), so no mod keeps
// a cross-machine channel of its own.
//
// THE WIRE (Refii's own co-op channel, one datagram per line, beside the player state's ps.*):
//   ps.e=<epoch>|<seq>:<event>;<seq>:<event>;...
// <epoch> is this process's, <seq> counts every event it posted. Each event is restated with every
// line for kKeep after it was posted (a few lines at kRestate pacing), so a lost datagram costs
// nothing; the receiver keeps the highest <seq> it took per <epoch> and takes only newer ones. An
// <event> is the payload's own text (`<kind>:<field>,<field>,...`): no ';', '|' or newline.
//
// THE ROUTE (the same channel, its own prefix): the core co-op Lua's calls for the player the peer
// plays and their answers (CoopHeroLua.cs, C.Route), which must arrive exactly once and in order —
// a box shown twice, or a camera cleared before it was set, is wrong where a restated swing is not.
//   ps.r=<epoch>,<ack epoch>,<ack seq>,<first>|<seq>:<line>;<seq>:<line>;...
// <epoch>/<seq> are the sender's; <ack epoch>,<ack seq> the last line it took from the peer, in
// order; <first> its oldest line not yet acknowledged. Unacknowledged lines are restated every
// kRouteRestate (oldest first) until the peer's ack covers them, and dropped after kRouteLife (the
// peer is gone). The receiver takes only the line after the last it took; anything before <first>
// was abandoned by the sender, so it moves on from there. An ack with nothing to send goes on its
// own (an empty body) when it changed, and with every restatement.
//
// The state lives here, in refii_vfs, because the payload posts and takes (core/payload_bridge.h)
// and the host bridge carries the lines (its player-state exports, which the Godot side already
// sends and receives on the co-op channel while a peer stands).

#pragma once

#include <cstddef>
#include <string_view>

namespace refii::relay {

/// The relay lines this machine owes the peer, appended to `out` (at most `capacity` bytes, one
/// per line). Returns the bytes written.
size_t TakeOutgoing(char* out, size_t capacity);

/// One message from the peer. False when it is not a relay message.
bool Receive(std::string_view message);

}  // namespace refii::relay
