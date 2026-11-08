// States which build of Fable 3 this payload was recompiled from.
//
// COMPILED INTO THE PAYLOAD, so it runs when the module loads -- before any hook does. The
// launcher defines REFII_GUEST_BUILD_UNPATCHED when the executable had no title update beside it
// (Engine_Title.IsUnpatched); the table it selects is Fable3/src/vfs/src/guest_build.cpp's.

#include "../../vfs/include/fable3_guest_build.h"

namespace {

struct GuestBuildStamp {
  GuestBuildStamp() {
#if defined(REFII_GUEST_BUILD_UNPATCHED)
    refiii_guest_build_set(1);
#else
    refiii_guest_build_set(0);
#endif
  }
};

const GuestBuildStamp g_guest_build_stamp;

}  // namespace
