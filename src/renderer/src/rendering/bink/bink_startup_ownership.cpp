#include "runtime/refii_renderer/bik/bink_startup_ownership.h"

#include "../../../include/runtime/cvars.h"
#include "runtime/refii_renderer/bik/bink_guest_layout.h"

namespace refii::renderer::bink {

bool BinkPlumeOwnsMovie(uint8_t *base, uint32_t movie_guest) {
  // A valid Bink guest object is not, by itself, evidence that Plume owns its
  // decode/pacing lifecycle.  These hooks are also installed for the Xenia
  // backend, where the native renderer and Bink worker must remain in charge.
  return PlumeRendererBackendEnabled() &&
         LooksLikeBinkMovie(base, movie_guest);
}

} // namespace refii::renderer::bink
