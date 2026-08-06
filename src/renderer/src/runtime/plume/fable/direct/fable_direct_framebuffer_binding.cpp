#include "runtime/refii_renderer/plume/fable/direct/fable_direct_framebuffer_binding.h"

#include <algorithm>

namespace refii::renderer {

DirectFramebufferBinding FrameDatabaseFramebufferBindingForCommand(
    const FableDirectRenderCommand& command) {
  DirectFramebufferBinding binding;
  for (uint32_t slot = 0;
       slot < kFableDirectFramebufferColorCount &&
       slot < command.resources.render_targets.size();
       ++slot) {
    const uint32_t target = command.resources.render_targets[slot];
    if (target == 0) {
      continue;
    }
    binding.color_addresses[slot] = target;
    binding.color_count = std::max(binding.color_count, slot + 1u);
  }
  return binding;
}

}  // namespace refii::renderer
