// refii - Fable 3's host bridge feeds
//
// The exports every title shares — the ABI version, the guest's lifecycle, the debug
// channel, audio, input, time, mods, player state, achievements, RexNet — are
// refii_hostcore's (src/hostcore), linked whole into this library. Fable 3 states nothing
// of its own yet: no feeds, and nothing added to the core's lifecycle.
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
