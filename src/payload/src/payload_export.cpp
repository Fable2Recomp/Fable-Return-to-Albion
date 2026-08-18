// The payload's side of the host ABI.
//
// Compiled INTO the recompiled guest module — by our build in the dev tree, and by the
// install-time project on the player's machine from the same source file. It is the only
// hand-written translation unit inside the payload, and the only symbol the host needs
// exported by name to bring the guest up.

#include "refii_init.h"

#include <rex/image_info.h>

#if defined(_WIN32)
#define REFII_PAYLOAD_EXPORT __declspec(dllexport)
#else
#define REFII_PAYLOAD_EXPORT __attribute__((visibility("default")))
#endif

// Name must match REFII_PAYLOAD_ENTRY_SYMBOL in refii_payload/payload.h. Spelled out
// rather than macro-pasted: the install-time build compiles this file without refii's
// include tree, so it cannot reach that header.
extern "C" REFII_PAYLOAD_EXPORT const rex::PPCImageInfo* refii_payload_v1_image_config() {
  return &PPCImageConfig;
}
