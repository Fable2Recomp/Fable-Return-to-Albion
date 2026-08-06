#include "runtime/refii_renderer/plume/fable/resource/fable_upload_data.h"

#include "runtime/refii_renderer/bik/bink_guest_memory.h"

#include <algorithm>
#include <cerrno>
#include <chrono>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <limits>
#include <mutex>
#include <string>
#include <unordered_map>
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
namespace {

constexpr uint32_t kMaxScratchUploadSnapshotSize = 16u << 20;
constexpr uint32_t kFableObjectAddressMax = 0x80000000u;

bool IsLikelyGuestByteRangeForUploadHash(uint32_t guest_address,
                                         uint32_t size) {
  if (guest_address == 0 || size == 0 ||
      guest_address > UINT32_MAX - (size - 1u)) {
    return false;
  }

  const uint32_t end_address = guest_address + size - 1u;
  if (guest_address >= 0x1000u && end_address < kFableObjectAddressMax) {
    return true;
  }

  return guest_address >= 0x80000000u && size <= kMaxScratchUploadSnapshotSize;
}

bool IsReadableHostByteRangeForUploadData(const uint8_t *data, size_t size) {
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
        protect == PAGE_EXECUTE_READWRITE || protect == PAGE_EXECUTE_WRITECOPY;
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
    current = std::min(region_end, end);
  }
  return true;
#elif defined(__linux__)
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

}  // namespace

uint64_t HashGuestBytesFnv1a64(uint8_t *base, uint32_t guest_address,
                               uint32_t size) {
  if (!base || !IsLikelyGuestByteRangeForUploadHash(guest_address, size)) {
    return 0;
  }

  const uint8_t *data =
      base + guest_address + bink::GuestPhysicalHostOffset(guest_address);
  if (!IsReadableHostByteRangeForUploadData(data, size)) {
    return 0;
  }

  uint64_t hash = 14695981039346656037ull;
  for (uint32_t i = 0; i < size; ++i) {
    hash ^= data[i];
    hash *= 1099511628211ull;
  }
  return hash;
}

uint64_t HashBytesFnv1a64(const uint8_t *data, size_t size) {
  if (!IsReadableHostByteRangeForUploadData(data, size)) {
    return 0;
  }

  uint64_t hash = 14695981039346656037ull;
  for (size_t i = 0; i < size; ++i) {
    hash ^= data[i];
    hash *= 1099511628211ull;
  }
  return hash;
}

uint64_t HashBytesFast64(const uint8_t *data, size_t size) {
  if (!IsReadableHostByteRangeForUploadData(data, size)) {
    return 0;
  }

  uint64_t hash = 0x9E3779B97F4A7C15ull ^ static_cast<uint64_t>(size);
  size_t offset = 0;
  for (; offset + sizeof(uint64_t) <= size; offset += sizeof(uint64_t)) {
    uint64_t lane = 0;
    std::memcpy(&lane, data + offset, sizeof(lane));
    hash ^= lane + 0x9E3779B97F4A7C15ull + (hash << 6u) + (hash >> 2u);
    hash = (hash << 27u) | (hash >> 37u);
    hash *= 0x3C79AC492BA7B653ull;
  }
  uint64_t tail = 0;
  std::memcpy(&tail, data + offset, size - offset);
  hash ^= tail + 0x1C69B3F74AC4AE35ull + (hash << 6u) + (hash >> 2u);
  hash ^= hash >> 33u;
  hash *= 0xFF51AFD7ED558CCDull;
  hash ^= hash >> 33u;
  hash *= 0xC4CEB9FE1A85EC53ull;
  hash ^= hash >> 33u;
  return hash != 0 ? hash : 1u;
}

uint64_t HashZeroBytesFnv1a64(size_t size) {
  if (size == 0) {
    return 0;
  }

  static std::mutex cache_mutex;
  static std::unordered_map<size_t, uint64_t> cache;
  {
    std::lock_guard<std::mutex> lock(cache_mutex);
    const auto it = cache.find(size);
    if (it != cache.end()) {
      return it->second;
    }
  }

  uint64_t hash = 14695981039346656037ull;
  for (size_t i = 0; i < size; ++i) {
    hash *= 1099511628211ull;
  }

  std::lock_guard<std::mutex> lock(cache_mutex);
  return cache.emplace(size, hash).first->second;
}

uint64_t HashZeroBytesFast64(size_t size) {
  if (size == 0) {
    return 0;
  }

  static std::mutex cache_mutex;
  static std::unordered_map<size_t, uint64_t> cache;
  {
    std::lock_guard<std::mutex> lock(cache_mutex);
    const auto it = cache.find(size);
    if (it != cache.end()) {
      return it->second;
    }
  }

  std::vector<uint8_t> zeroes(size, 0);
  const uint64_t hash = HashBytesFast64(zeroes.data(), zeroes.size());

  std::lock_guard<std::mutex> lock(cache_mutex);
  return cache.emplace(size, hash).first->second;
}

bool HasNonZeroUploadData(const RendererResourceRecord &record) {
  if (record.upload_data.empty()) {
    return false;
  }
  if (record.raw.hash != 0) {
    const size_t upload_size = record.upload_data.size();
    return record.raw.hash != HashZeroBytesFnv1a64(upload_size) &&
           record.raw.hash != HashZeroBytesFast64(upload_size);
  }
  // Byte-scan memoized per upload generation: record matching calls this at
  // high rate on hot hook paths and payloads run to megabytes. Generation 0
  // payloads (never bumped) fall through to a fresh scan each call.
  if (record.upload_generation != 0 &&
      record.nonzero_scan_generation == record.upload_generation) {
    return record.nonzero_scan_result;
  }
  const bool nonzero =
      std::any_of(record.upload_data.begin(), record.upload_data.end(),
                  [](uint8_t value) { return value != 0; });
  if (record.upload_generation != 0) {
    record.nonzero_scan_generation = record.upload_generation;
    record.nonzero_scan_result = nonzero;
  }
  return nonzero;
}

bool HasNonZeroUploadPrefixData(const RendererResourceRecord &record,
                                uint32_t max_scan) {
  if (record.upload_data.empty() || max_scan == 0) {
    return false;
  }

  const size_t scan_size =
      std::min<size_t>(record.upload_data.size(), max_scan);
  return std::any_of(record.upload_data.begin(),
                     record.upload_data.begin() + scan_size,
                     [](uint8_t value) { return value != 0; });
}

bool IsAssetChunkHeaderLike(const uint8_t *data, size_t size) {
  if (!data || size < 4) {
    return false;
  }

  return (data[0] == 0x65u && data[1] == 0x73u && data[2] == 0x49u &&
          data[3] == 0x44u) ||
         (data[0] == 0x73u && data[1] == 0x65u && data[2] == 0x49u &&
          data[3] == 0x44u);
}

}  // namespace refii::renderer
