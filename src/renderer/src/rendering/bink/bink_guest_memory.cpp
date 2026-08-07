#include "runtime/refii_renderer/bik/bink_guest_memory.h"

#include "runtime/refii_renderer/guest/guest_memory_access.h"

#include <cstring>

namespace refii::renderer::bink {

uint32_t GuestPhysicalHostOffset(uint32_t guest_address) {
#if defined(_WIN32)
  return guest_address >= 0xE0000000u ? 0x1000u : 0u;
#else
  (void)guest_address;
  return 0;
#endif
}

bool IsReadableGuestByteRange(uint8_t *base, uint32_t guest_address,
                              uint32_t byte_size) {
  constexpr uint32_t kLowestMappedGuestAddress = 0x10000u;
  if (base == nullptr || guest_address == 0 || byte_size == 0 ||
      guest_address < kLowestMappedGuestAddress ||
      guest_address > UINT32_MAX - (byte_size - 1u)) {
    return false;
  }

  return refii::renderer::IsReadableHostByteRange(
      base + guest_address + GuestPhysicalHostOffset(guest_address), byte_size);
}

uint8_t *GuestMemoryPtr(uint8_t *base, uint32_t guest_address,
                        uint32_t byte_size) {
  if (base == nullptr || guest_address == 0) {
    return nullptr;
  }
  if (byte_size != 0 &&
      !IsReadableGuestByteRange(base, guest_address, byte_size)) {
    return nullptr;
  }
  return base + guest_address + GuestPhysicalHostOffset(guest_address);
}

uint8_t LoadGuestU8(uint8_t *base, uint32_t guest_address) {
  if (!IsReadableGuestByteRange(base, guest_address, sizeof(uint8_t))) {
    return 0;
  }
  return *(base + guest_address + GuestPhysicalHostOffset(guest_address));
}

void StoreGuestU8(uint8_t *base, uint32_t guest_address, uint8_t value) {
  if (!IsReadableGuestByteRange(base, guest_address, sizeof(uint8_t))) {
    return;
  }
  *(base + guest_address + GuestPhysicalHostOffset(guest_address)) = value;
}

uint32_t LoadGuestU32(uint8_t *base, uint32_t guest_address) {
  if (!IsReadableGuestByteRange(base, guest_address, sizeof(uint32_t))) {
    return 0;
  }

  uint32_t raw = 0;
  std::memcpy(&raw,
              base + guest_address + GuestPhysicalHostOffset(guest_address),
              sizeof(raw));
  return __builtin_bswap32(raw);
}

void StoreGuestU32(uint8_t *base, uint32_t guest_address, uint32_t value) {
  if (!IsReadableGuestByteRange(base, guest_address, sizeof(uint32_t))) {
    return;
  }

  const uint32_t raw = __builtin_bswap32(value);
  std::memcpy(base + guest_address + GuestPhysicalHostOffset(guest_address),
              &raw, sizeof(raw));
}

bool FillGuestBytes(uint8_t *base, uint32_t guest_address,
                    uint32_t byte_count, uint8_t value) {
  uint8_t *dst = GuestMemoryPtr(base, guest_address, byte_count);
  if (dst == nullptr) {
    return false;
  }
  std::memset(dst, value, byte_count);
  return true;
}

} // namespace refii::renderer::bink
