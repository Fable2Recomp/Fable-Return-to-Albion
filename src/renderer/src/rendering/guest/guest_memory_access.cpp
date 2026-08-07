#include "runtime/refii_renderer/guest/guest_memory_access.h"

#include <algorithm>
#include <chrono>
#include <cstring>
#include <limits>
#include <utility>
#include <vector>

#if defined(_WIN32)
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#endif

namespace refii::renderer {

uint32_t GuestPhysicalHostOffset(uint32_t guest_address) {
#if defined(_WIN32)
  return guest_address >= 0xE0000000u ? 0x1000u : 0u;
#else
  return 0;
#endif
}

bool IsPlausibleGuestByteRange(uint32_t guest_address, uint32_t byte_size) {
  if (guest_address == 0 || byte_size == 0 ||
      guest_address > UINT32_MAX - (byte_size - 1u)) {
    return false;
  }

  const uint32_t end_address = guest_address + byte_size - 1u;
  if (guest_address >= 0x1000u && end_address < 0x80000000u) {
    return true;
  }

  return guest_address >= 0x80000000u && byte_size <= 64u * 1024u * 1024u;
}

bool IsReadableHostByteRange(const uint8_t *data, size_t size) {
  if (!data || size == 0) {
    return false;
  }

  const uintptr_t begin = reinterpret_cast<uintptr_t>(data);
  if (begin > std::numeric_limits<uintptr_t>::max() - size) {
    return false;
  }

  const uintptr_t end = begin + size;
  uintptr_t current = begin;
#if defined(_WIN32)
  thread_local MEMORY_BASIC_INFORMATION cached_info = {};
  thread_local std::chrono::steady_clock::time_point cached_at{};
  const uintptr_t cached_begin =
      reinterpret_cast<uintptr_t>(cached_info.BaseAddress);
  const uintptr_t cached_end =
      cached_begin + static_cast<uintptr_t>(cached_info.RegionSize);
  if (cached_info.RegionSize != 0 && begin >= cached_begin &&
      end <= cached_end) {
    const auto now = std::chrono::steady_clock::now();
    if (now - cached_at < std::chrono::milliseconds(2)) {
      return true;
    }
  }
  bool cache_candidate_valid = true;
  while (current < end) {
    MEMORY_BASIC_INFORMATION info = {};
    if (VirtualQuery(reinterpret_cast<const void *>(current), &info,
                     sizeof(info)) == 0) {
      return false;
    }

    const DWORD protect = info.Protect & 0xFFu;
    const bool readable =
        protect == PAGE_READONLY || protect == PAGE_READWRITE ||
        protect == PAGE_WRITECOPY || protect == PAGE_EXECUTE_READ ||
        protect == PAGE_EXECUTE_READWRITE ||
        protect == PAGE_EXECUTE_WRITECOPY;
    if (info.State != MEM_COMMIT || (info.Protect & PAGE_GUARD) != 0 ||
        !readable) {
      return false;
    }

    const uintptr_t region_begin =
        reinterpret_cast<uintptr_t>(info.BaseAddress);
    if (region_begin > std::numeric_limits<uintptr_t>::max() -
                           static_cast<uintptr_t>(info.RegionSize)) {
      return false;
    }
    const uintptr_t region_end =
        region_begin + static_cast<uintptr_t>(info.RegionSize);
    if (region_end <= current) {
      return false;
    }
    if (cache_candidate_valid) {
      if (begin >= region_begin && end <= region_end) {
        cached_info = info;
        cached_at = std::chrono::steady_clock::now();
      }
      cache_candidate_valid = false;
    }
    current = std::min(region_end, end);
  }
  return true;
#elif defined(__linux__)
  // Same per-thread readable-region snapshot as the observer's copy in
  // hook_observer.cpp: the guest heap spans several distinct mappings, so a
  // single-region cache degenerated into a full /proc/self/maps parse per
  // guarded LoadGuest* call on hot hook paths (resource-query emulation runs
  // per material at title).
  (void)current;
  struct ReadableRegionSnapshot {
    std::vector<std::pair<uintptr_t, uintptr_t>> regions;
    std::chrono::steady_clock::time_point taken{};
    bool valid = false;
  };
  thread_local ReadableRegionSnapshot snapshot;

  const auto snapshot_covers = [&]() {
    auto it = std::upper_bound(
        snapshot.regions.begin(), snapshot.regions.end(), begin,
        [](uintptr_t value, const std::pair<uintptr_t, uintptr_t> &region) {
          return value < region.second;
        });
    return it != snapshot.regions.end() && it->first <= begin &&
           end <= it->second;
  };

  if (snapshot.valid && snapshot_covers()) {
    return true;
  }
  const auto now = std::chrono::steady_clock::now();
  if (snapshot.valid && now - snapshot.taken < std::chrono::milliseconds(250)) {
    return false;
  }

  std::ifstream maps("/proc/self/maps");
  if (!maps) {
    return false;
  }

  snapshot.regions.clear();
  std::string line;
  while (std::getline(maps, line)) {
    const char *text = line.c_str();
    char *after_begin = nullptr;
    errno = 0;
    const unsigned long long region_begin_value =
        std::strtoull(text, &after_begin, 16);
    if (errno != 0 || after_begin == text || *after_begin != '-') {
      continue;
    }

    char *after_end = nullptr;
    errno = 0;
    const unsigned long long region_end_value =
        std::strtoull(after_begin + 1, &after_end, 16);
    if (errno != 0 || after_end == after_begin + 1 || *after_end != ' ') {
      continue;
    }
    while (*after_end == ' ') {
      ++after_end;
    }
    if (after_end[0] != 'r') {
      continue;
    }

    const uintptr_t region_begin = static_cast<uintptr_t>(region_begin_value);
    const uintptr_t region_end = static_cast<uintptr_t>(region_end_value);
    if (region_end <= region_begin) {
      continue;
    }
    if (!snapshot.regions.empty() &&
        snapshot.regions.back().second == region_begin) {
      snapshot.regions.back().second = region_end;
    } else {
      snapshot.regions.emplace_back(region_begin, region_end);
    }
  }
  snapshot.taken = now;
  snapshot.valid = true;
  return snapshot_covers();
#else
  return true;
#endif
}

bool IsReadableGuestByteRange(uint8_t *base, uint32_t guest_address,
                              uint32_t byte_size) {
  if (!base || !IsPlausibleGuestByteRange(guest_address, byte_size)) {
    return false;
  }

  const uint8_t *data =
      base + guest_address + GuestPhysicalHostOffset(guest_address);
  return IsReadableHostByteRange(data, byte_size);
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

float LoadGuestF32(uint8_t *base, uint32_t guest_address) {
  const uint32_t bits = LoadGuestU32(base, guest_address);
  float value = 0.0f;
  std::memcpy(&value, &bits, sizeof(value));
  return value;
}

void StoreGuestF32(uint8_t *base, uint32_t guest_address, float value) {
  uint32_t bits = 0;
  std::memcpy(&bits, &value, sizeof(bits));
  StoreGuestU32(base, guest_address, bits);
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

uint16_t LoadGuestU16(uint8_t *base, uint32_t guest_address) {
  if (!IsReadableGuestByteRange(base, guest_address, sizeof(uint16_t))) {
    return 0;
  }

  uint16_t raw = 0;
  std::memcpy(&raw,
              base + guest_address + GuestPhysicalHostOffset(guest_address),
              sizeof(raw));
  return __builtin_bswap16(raw);
}

void StoreGuestU16(uint8_t *base, uint32_t guest_address, uint16_t value) {
  if (!IsReadableGuestByteRange(base, guest_address, sizeof(uint16_t))) {
    return;
  }

  const uint16_t raw = __builtin_bswap16(value);
  std::memcpy(base + guest_address + GuestPhysicalHostOffset(guest_address),
              &raw, sizeof(raw));
}

uint64_t LoadGuestU64(uint8_t *base, uint32_t guest_address) {
  const uint64_t high = LoadGuestU32(base, guest_address);
  const uint64_t low = LoadGuestU32(base, guest_address + sizeof(uint32_t));
  return (high << 32u) | low;
}

double LoadGuestF64(uint8_t *base, uint32_t guest_address) {
  const uint64_t bits = LoadGuestU64(base, guest_address);
  double value = 0.0;
  std::memcpy(&value, &bits, sizeof(value));
  return value;
}

void StoreGuestU64(uint8_t *base, uint32_t guest_address, uint64_t value) {
  StoreGuestU32(base, guest_address, static_cast<uint32_t>(value >> 32u));
  StoreGuestU32(base, guest_address + sizeof(uint32_t),
                static_cast<uint32_t>(value));
}

}  // namespace refii::renderer
