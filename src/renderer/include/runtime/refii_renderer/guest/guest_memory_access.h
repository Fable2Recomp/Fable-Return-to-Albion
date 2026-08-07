#pragma once

#include <cstddef>
#include <cstdint>

namespace refii::renderer {

uint32_t GuestPhysicalHostOffset(uint32_t guest_address);
bool IsPlausibleGuestByteRange(uint32_t guest_address, uint32_t byte_size);
bool IsReadableHostByteRange(const uint8_t *data, size_t size);
bool IsReadableGuestByteRange(uint8_t *base, uint32_t guest_address,
                              uint32_t byte_size);
uint32_t LoadGuestU32(uint8_t *base, uint32_t guest_address);
void StoreGuestU32(uint8_t *base, uint32_t guest_address, uint32_t value);
float LoadGuestF32(uint8_t *base, uint32_t guest_address);
void StoreGuestF32(uint8_t *base, uint32_t guest_address, float value);
uint8_t LoadGuestU8(uint8_t *base, uint32_t guest_address);
void StoreGuestU8(uint8_t *base, uint32_t guest_address, uint8_t value);
uint16_t LoadGuestU16(uint8_t *base, uint32_t guest_address);
void StoreGuestU16(uint8_t *base, uint32_t guest_address, uint16_t value);
uint64_t LoadGuestU64(uint8_t *base, uint32_t guest_address);
double LoadGuestF64(uint8_t *base, uint32_t guest_address);
void StoreGuestU64(uint8_t *base, uint32_t guest_address, uint64_t value);

}  // namespace refii::renderer
