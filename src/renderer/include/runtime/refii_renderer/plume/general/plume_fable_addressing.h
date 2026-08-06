#pragma once

#include <array>
#include <cstdint>

namespace refii::renderer {

bool IsLikelyFableObjectAddress(uint32_t guest_address);
bool IsLikelyFableGpuByteBufferRange(uint32_t guest_address, uint32_t size);
bool IsLikelyFableHighScratchByteBufferRange(uint32_t guest_address,
                                             uint32_t size);

uint32_t NormalizeFableResolveDestinationBase(uint32_t destination_base);
uint32_t NormalizeFableGpuAddress(uint32_t pointer);
uint32_t NormalizeFableGpuBase(uint32_t base);
uint32_t DecodeFableGpuMemoryPointer(uint32_t pointer);
uint32_t DecodeFableGpuBufferPointer(uint32_t pointer);
uint32_t DecodeFableGpuBase(uint32_t base);

// True when `address` is a raw Xenos PHYSICAL address (a GPU fetch-constant
// base, or the output of DecodeFableGpuMemoryPointer). Physical memory is
// 512MB, so these always land below 0x20000000.
bool IsFableGuestPhysicalAddress(uint32_t address);

// Maps a raw physical address to the guest address that ADDRESSES it.
//
// This is the term DecodeFableGpuMemoryPointer drops. The XDK's own
// D3D::LockResource (0x821F6300) computes, for a GPU-visible lock:
//
//   (((a7 >> 20) + 512) & 0x1000) + (a7 & 0x1FFFFFFF) - 0x40000000
//   \___________ DecodeFableGpuMemoryPointer ________/   \_ this _/
//
// and hands the result to the guest as its write pointer. The final term is
// what turns a physical OFFSET into a guest ADDRESS: -0x40000000 wraps to
// 0xC0000000, the 16MB-page physical view.
//
// The distinction is load-bearing. rexglue maps guest 0xC0000000+P onto the
// same host pages as physical P (see xmemory.cpp map_info), but guest P with
// no prefix is the *low virtual heap* — unrelated memory that is frequently
// committed and non-zero, so reading it fails silently instead of loudly.
uint32_t FableGuestPhysicalViewAddress(uint32_t address);

std::array<uint32_t, 4> FableDisplayBaseCandidates(uint32_t base);

}  // namespace refii::renderer
