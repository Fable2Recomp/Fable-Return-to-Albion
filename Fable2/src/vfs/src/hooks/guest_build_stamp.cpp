// States which build of the game this payload was recompiled from.
//
// COMPILED INTO THE PAYLOAD, so it runs when the module loads -- before any hook does. The
// launcher (and the dev build) define REFII_GUEST_BUILD_UNPATCHED when the executable had no
// title update beside it; see GameCodeBuild.GameVersionName.

#include "core/guest_build.h"

namespace {

struct GuestBuildStamp {
  GuestBuildStamp() {
#if defined(REFII_GUEST_BUILD_UNPATCHED)
    refii_guest_build_set(1);
#else
    refii_guest_build_set(0);
#endif
  }
};

const GuestBuildStamp g_guest_build_stamp;

}  // namespace
