#include "runtime/refii_renderer/plume/fable/video/fable_pix_video_state.h"

#include <algorithm>

namespace refii::renderer {

FablePixVideoReplacementStateRestore FablePixVideoCaptureReplacementState(
    const FableDirectStateSnapshot& state) {
  FablePixVideoReplacementStateRestore restore;
  const auto& fetch_constants = state.constants.fetch;
  for (uint32_t slot = 0; slot < kFablePixVideoPlaneCount; ++slot) {
    restore.textures[slot] = state.textures.textures[slot];
    const uint32_t fetch_dword = slot * kFablePixVideoFetchConstantDwords;
    if (fetch_dword + kFablePixVideoFetchConstantDwords <=
        fetch_constants.size()) {
      std::copy_n(fetch_constants.begin() + fetch_dword,
                  kFablePixVideoFetchConstantDwords,
                  restore.fetches[slot].begin());
    }
  }
  restore.stream0 = state.input.streams[0];
  return restore;
}

void FablePixVideoRestoreReplacementState(
    uint32_t device,
    const FablePixVideoReplacementStateRestore& state) {
  for (uint32_t slot = 0; slot < kFablePixVideoPlaneCount; ++slot) {
    FableDirectMirrorTextureState(device, slot, state.textures[slot]);
    FableDirectSetFetchConstantRange(
        slot * kFablePixVideoFetchConstantDwords,
        state.fetches[slot].data(),
        static_cast<uint32_t>(state.fetches[slot].size()));
  }
  FableDirectMirrorStreamState(device, 0, state.stream0);
}

}  // namespace refii::renderer
