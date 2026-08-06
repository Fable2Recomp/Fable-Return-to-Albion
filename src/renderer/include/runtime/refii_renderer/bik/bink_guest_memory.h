#pragma once

#include <cstdint>

namespace refii::renderer::bink {

uint32_t GuestPhysicalHostOffset(uint32_t guest_address);

bool IsReadableGuestByteRange(uint8_t *base, uint32_t guest_address,
                              uint32_t byte_size);

uint8_t *GuestMemoryPtr(uint8_t *base, uint32_t guest_address,
                        uint32_t byte_size);

uint8_t LoadGuestU8(uint8_t *base, uint32_t guest_address);
void StoreGuestU8(uint8_t *base, uint32_t guest_address, uint8_t value);

uint32_t LoadGuestU32(uint8_t *base, uint32_t guest_address);
void StoreGuestU32(uint8_t *base, uint32_t guest_address, uint32_t value);

bool FillGuestBytes(uint8_t *base, uint32_t guest_address,
                    uint32_t byte_count, uint8_t value);

} // namespace refii::renderer::bink
