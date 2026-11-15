// refii - Fable 3's host bridge feeds
//
// The exports every title shares — the ABI version, the guest's lifecycle, the debug
// channel, audio, input, time, mods, player state, achievements, RexNet — are
// refii_hostcore's (src/hostcore), linked whole into this library. Fable 3's own feeds: the world
// it stands in (level_feed.cpp). It adds nothing to the core's lifecycle yet.
// The shell's title feeds it cannot answer yet are answered as nothing in unstated_feeds.cpp.

#include "host_core.h"

namespace {

/// Fable 3's part of the core's lifecycle and of its title questions: none. The guest runs
/// on the core's lifecycle alone, the probe snapshot has no device to read, and the RexNet
/// join routes have no guest statement to refuse on.
const bool g_fable3_registered = [] {
    refii::hostcore::RegisterTitle(refii::hostcore::TitleCallbacks{});
    return true;
}();

}  // namespace

#include "host_abi.h"
#include "../../vfs/include/fable3_vfs.h"

extern "C" {

/// The debug channel's last probe outcome: the game VM's `debug-gamelua:` chunk, run by the
/// payload's tick (Fable3/src/hooks/src/game_tick_pump.cpp) — 1 and "ok", or 0 and the Lua error.
REFII_HOST_ABI_EXPORT uint32_t RefiiHostGetDebugCallResult(uint64_t* out_seq, uint32_t* out_addr, uint64_t* out_r3,
                                                           char* out_note, uint32_t note_capacity) {
    return refiii_vfs_debug_result(out_seq, out_addr, out_r3, out_note, note_capacity);
}

}  // extern "C"
