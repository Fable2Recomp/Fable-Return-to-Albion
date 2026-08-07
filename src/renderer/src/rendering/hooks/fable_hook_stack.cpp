#include "runtime/refii_renderer/hooks/general/fable_hook_stack.h"

#include "runtime/refii_renderer/bik/bink_guest_memory.h"

#include <limits>

namespace refii::renderer {

uint32_t FableHookStackU32(
    uint8_t* base,
    const RendererHookArgs& args,
    uint32_t offset,
    const FableHookGuestAddressCallbacks& callbacks) {
  const uint32_t address = args.r1 + offset;
  return base && callbacks.is_likely_guest_dword_address &&
                 callbacks.is_likely_guest_dword_address(address)
             ? bink::LoadGuestU32(base, address)
             : 0u;
}

bool FableHookAddressNearStack(const RendererHookArgs& args,
                               uint32_t address) {
  if (args.r1 == 0 || address == 0) {
    return false;
  }

  constexpr uint32_t kStackScratchWindow = 0x4000u;
  const uint32_t low =
      args.r1 > kStackScratchWindow ? args.r1 - kStackScratchWindow : 0u;
  const uint32_t high =
      args.r1 <= std::numeric_limits<uint32_t>::max() - kStackScratchWindow
          ? args.r1 + kStackScratchWindow
          : std::numeric_limits<uint32_t>::max();
  return address >= low && address <= high;
}

}  // namespace refii::renderer
