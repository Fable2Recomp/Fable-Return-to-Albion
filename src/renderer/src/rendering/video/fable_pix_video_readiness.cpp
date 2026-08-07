#include "runtime/refii_renderer/plume/fable/video/fable_pix_video_readiness.h"

namespace refii::renderer {

bool FablePixVideoPlaneSetReady(const FablePixVideoPlaneReadinessFacts& facts,
                                uint32_t* missing_slot,
                                uint32_t* missing_guest) {
  if (!facts.pix_video_draw) {
    return true;
  }

  for (uint32_t slot = 0; slot < kFablePixVideoPlaneCount; ++slot) {
    if (facts.guests[slot] != 0 && facts.ready[slot]) {
      continue;
    }
    if (missing_slot) {
      *missing_slot = slot;
    }
    if (missing_guest) {
      *missing_guest = facts.guests[slot];
    }
    return false;
  }
  return true;
}

FablePixVideoPlaneTextureInterests FablePixVideoCollectPlaneTextureInterests(
    const std::array<uint32_t, kRendererD3DTextureSlots>& textures) {
  FablePixVideoPlaneTextureInterests interests;
  for (uint32_t slot = 0; slot < kFablePixVideoPlaneCount; ++slot) {
    const uint32_t texture = textures[slot];
    if (texture == 0) {
      continue;
    }
    interests.textures[interests.count++] = texture;
  }
  return interests;
}

}  // namespace refii::renderer
