/**
 ******************************************************************************
 * Xenia : Xbox 360 Emulator Research Project                                 *
 ******************************************************************************
 * Copyright 2020 Ben Vanik. All rights reserved.                             *
 * Released under the BSD license - see LICENSE in the root for more details. *
 ******************************************************************************
 *
 * @modified    Tom Clay, 2026 - Adapted for ReXGlue runtime
 */

#include <cerrno>
#include <cstddef>
#include <cstdio>
#include <cstring>
#include <cstdint>
#include <fstream>
#include <map>
#include <mutex>
#include <string>

#include <fcntl.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <unistd.h>

#include <rex/math.h>
#include <rex/memory/utils.h>
#include <rex/platform.h>
#include <rex/string.h>

#if REX_PLATFORM_ANDROID
#include <string.h>

#include <dlfcn.h>
#include <sys/ioctl.h>

#include <linux/ashmem.h>

// TODO(tomc): Android or maybe na. idk
// #include "xenia/base/main_android.h"
#endif

namespace rex {
namespace memory {

// Convert filesystem path to valid shm_open name (must start with /, no other slashes)
static std::string MakeShmName(const std::filesystem::path& path) {
  std::string name = path.string();
  for (char& c : name) {
    if (c == '/')
      c = '_';
  }
  if (name.empty() || name[0] != '/') {
    name.insert(name.begin(), '/');
  }
  return name;
}

#if REX_PLATFORM_ANDROID
// May be null if no dynamically loaded functions are required.
static void* libandroid_;
// API 26+.
static int (*android_ASharedMemory_create_)(const char* name, size_t size);

void AndroidInitialize() {
  if (rex::GetAndroidApiLevel() >= 26) {
    libandroid_ = dlopen("libandroid.so", RTLD_NOW);
    assert_not_null(libandroid_);
    if (libandroid_) {
      android_ASharedMemory_create_ = reinterpret_cast<decltype(android_ASharedMemory_create_)>(
          dlsym(libandroid_, "ASharedMemory_create"));
      assert_not_null(android_ASharedMemory_create_);
    }
  }
}

void AndroidShutdown() {
  android_ASharedMemory_create_ = nullptr;
  if (libandroid_) {
    dlclose(libandroid_);
    libandroid_ = nullptr;
  }
}
#endif

size_t page_size() {
  return getpagesize();
}
size_t allocation_granularity() {
  return page_size();
}

uint32_t ToPosixProtectFlags(PageAccess access) {
  switch (access) {
    case PageAccess::kNoAccess:
      return PROT_NONE;
    case PageAccess::kReadOnly:
      return PROT_READ;
    case PageAccess::kReadWrite:
      return PROT_READ | PROT_WRITE;
    case PageAccess::kExecuteReadOnly:
      return PROT_READ | PROT_EXEC;
    case PageAccess::kExecuteReadWrite:
      return PROT_READ | PROT_WRITE | PROT_EXEC;
    default:
      assert_unhandled_case(access);
      return PROT_NONE;
  }
}

bool IsWritableExecutableMemorySupported() {
  return true;
}

// TODO(tomc): this needs to go somewhere else. we should utilize the platform namespace more.
#if REX_PLATFORM_LINUX
namespace {

struct LinuxMapEntry {
  uintptr_t start = 0;
  uintptr_t end = 0;
  char perms[5] = {};
};

// Parse a line from /proc/self/maps into a LinuxMapEntry
static bool ParseProcMapsLine(const std::string& line, LinuxMapEntry& out) {
  out = LinuxMapEntry{};
  unsigned long long start = 0, end = 0;
  char perms[5] = {};
  const int matched = std::sscanf(line.c_str(), "%llx-%llx %4s", &start, &end, perms);
  if (matched < 3)
    return false;
  out.start = static_cast<uintptr_t>(start);
  out.end = static_cast<uintptr_t>(end);
  std::memcpy(out.perms, perms, sizeof(out.perms));
  return out.start < out.end;
}

// Find the mapping entry in /proc/self/maps that contains the given address
static bool FindEntryForAddress(void* address, LinuxMapEntry& out_entry) {
  const uintptr_t addr = reinterpret_cast<uintptr_t>(address);
  std::ifstream maps("/proc/self/maps");
  if (!maps.is_open())
    return false;
  std::string line;
  while (std::getline(maps, line)) {
    LinuxMapEntry e;
    if (!ParseProcMapsLine(line, e))
      continue;
    if (addr >= e.start && addr < e.end) {
      out_entry = e;
      return true;
    }
  }
  return false;
}

// Convert /proc/self/maps permission chars to PageAccess
static PageAccess PermsToPageAccess(const char perms[5]) {
  const bool r = perms[0] == 'r';
  const bool w = perms[1] == 'w';
  const bool x = perms[2] == 'x';

  if (!r && !w && !x)
    return PageAccess::kNoAccess;
  if (x)
    return w ? PageAccess::kExecuteReadWrite : PageAccess::kExecuteReadOnly;
  return w ? PageAccess::kReadWrite : PageAccess::kReadOnly;
}

}  // namespace
#endif  // REX_PLATFORM_LINUX


// ---------------------------------------------------------------------------
// Protection shadow.
//
// CONTRACT: every mapping and every protection change made THROUGH THIS FILE is recorded in
// one interval map, and Protect/QueryProtect answer from it. /proc/self/maps is a text file
// describing the ENTIRE process, regenerated per read by a kernel walk of every VMA — this
// process carries ~2,900 of them (mostly the guest arena's views split by per-page mprotect),
// so one parse costs ~258 KB of kernel time. Measured live: AllocFixed's commit pre-scan alone
// pinned the main thread for minutes at 38k reads/s during a level change, and the MMIO fault
// recheck runs the same parse UNDER THE GLOBAL CRITICAL REGION, serialising every thread.
//
// The shadow is authoritative for exactly the ranges this file created or reprotected — the
// guest arena and its views — which is every address the SDK ever queries. Addresses outside
// it (host allocations this file never touched) fall back to the old scan, and the fallback's
// answer is deliberately NOT seeded into the shadow: nothing tells us when a foreign region's
// protection changes, so caching it would go stale silently.
// ---------------------------------------------------------------------------
namespace {

struct ProtShadowRange {
  uintptr_t end;
  PageAccess access;
};

std::mutex g_prot_shadow_mutex;
// Key = range start. Ranges never overlap; adjacent ranges with equal access are coalesced so
// the map stays VMA-sized instead of page-sized.
std::map<uintptr_t, ProtShadowRange> g_prot_shadow;

// Removes every tracked page in [begin, end), splitting boundary ranges. Caller holds the lock.
void ProtShadowRemoveLocked(uintptr_t begin, uintptr_t end) {
  auto it = g_prot_shadow.lower_bound(begin);
  if (it != g_prot_shadow.begin()) {
    auto prev = std::prev(it);
    if (prev->second.end > begin) {
      const uintptr_t prev_end = prev->second.end;
      const PageAccess prev_access = prev->second.access;
      prev->second.end = begin;
      if (prev_end > end) {
        g_prot_shadow.emplace(end, ProtShadowRange{prev_end, prev_access});
      }
    }
  }
  it = g_prot_shadow.lower_bound(begin);
  while (it != g_prot_shadow.end() && it->first < end) {
    if (it->second.end > end) {
      ProtShadowRange tail{it->second.end, it->second.access};
      g_prot_shadow.erase(it);
      g_prot_shadow.emplace(end, tail);
      break;
    }
    it = g_prot_shadow.erase(it);
  }
}

void ProtShadowTrack(void* base, size_t length, PageAccess access) {
  if (base == nullptr || length == 0) {
    return;
  }
  uintptr_t begin = reinterpret_cast<uintptr_t>(base);
  uintptr_t end = begin + length;
  if (end < begin) {
    return;
  }
  std::lock_guard<std::mutex> lock(g_prot_shadow_mutex);
  ProtShadowRemoveLocked(begin, end);
  // Coalesce with equal-access neighbours so the map tracks regions, not pages.
  auto it = g_prot_shadow.lower_bound(begin);
  if (it != g_prot_shadow.begin()) {
    auto prev = std::prev(it);
    if (prev->second.end == begin && prev->second.access == access) {
      begin = prev->first;
      g_prot_shadow.erase(prev);
    }
  }
  it = g_prot_shadow.find(end);
  if (it != g_prot_shadow.end() && it->second.access == access) {
    end = it->second.end;
    g_prot_shadow.erase(it);
  }
  g_prot_shadow.emplace(begin, ProtShadowRange{end, access});
}

void ProtShadowErase(void* base, size_t length) {
  if (base == nullptr || length == 0) {
    return;
  }
  const uintptr_t begin = reinterpret_cast<uintptr_t>(base);
  const uintptr_t end = begin + length;
  if (end < begin) {
    return;
  }
  std::lock_guard<std::mutex> lock(g_prot_shadow_mutex);
  ProtShadowRemoveLocked(begin, end);
}

bool ProtShadowLookup(const void* address, uintptr_t& out_end, PageAccess& out_access) {
  const uintptr_t addr = reinterpret_cast<uintptr_t>(address);
  std::lock_guard<std::mutex> lock(g_prot_shadow_mutex);
  auto it = g_prot_shadow.upper_bound(addr);
  if (it == g_prot_shadow.begin()) {
    return false;
  }
  --it;
  if (addr >= it->second.end) {
    return false;
  }
  out_end = it->second.end;
  out_access = it->second.access;
  return true;
}

}  // namespace

void* AllocFixed(void* base_address, size_t length, AllocationType allocation_type,
                 PageAccess access) {
  // Emulates Windows VirtualAlloc behavior:
  // - Reserve: create PROT_NONE mapping to hold address space
  // - Commit on existing reservation: mprotect to enable access (EEXIST path)
  // - New allocation: mmap with MAP_FIXED_NOREPLACE (never silently replace)
  const uint32_t prot_requested = ToPosixProtectFlags(access);

  // Determine initial protection based on allocation type
  int prot_initial = 0;
  switch (allocation_type) {
    case AllocationType::kReserve:
      prot_initial = PROT_NONE;
      break;
    case AllocationType::kCommit:
    case AllocationType::kReserveCommit:
    default:
      prot_initial = static_cast<int>(prot_requested);
      break;
  }

  // Build flags - always use MAP_FIXED_NOREPLACE for fixed addresses
  int flags = MAP_PRIVATE | MAP_ANONYMOUS;
#if defined(MAP_FIXED_NOREPLACE)
  if (base_address) {
    flags |= MAP_FIXED_NOREPLACE;
  }
#else
  if (base_address) {
    flags |= MAP_FIXED;
  }
#endif

  void* result = mmap(base_address, length, prot_initial, flags, -1, 0);
  if (result != MAP_FAILED) {
    ProtShadowTrack(result, length,
                    allocation_type == AllocationType::kReserve ? PageAccess::kNoAccess : access);
    return result;
  }
#if defined(MAP_FIXED_NOREPLACE) && REX_PLATFORM_LINUX
  // Handle EEXIST: address already has a mapping (e.g., from prior Reserve)
  // This is the "commit on existing reservation" path
  if (errno == EEXIST && base_address &&
      (allocation_type == AllocationType::kCommit ||
       allocation_type == AllocationType::kReserveCommit)) {
    // NO PRE-SCAN. `IsRangeFullyMapped` read the whole of /proc/self/maps to check for gaps,
    // and mprotect already enforces exactly that: Linux fails it with ENOMEM when any page in
    // [addr, addr+len) is unmapped. The check was therefore redundant, and it was not cheap —
    // the kernel regenerates that file per read by walking every VMA, and this process carries
    // 2,877 of them across 258 KB. Measured on a live stall: the main thread pinned at one full
    // core, 88% of it SYSTEM time with zero page faults, ~38,000 read syscalls and 145 MB/s
    // against fd -> /proc/self/maps, sustained for over two minutes during a level change and
    // then recovering. Godot stopped mid-log-line for the duration, which reads as a freeze.
    if (mprotect(base_address, length, static_cast<int>(prot_requested)) == 0) {
      ProtShadowTrack(base_address, length, access);
      return base_address;
    }
  }
#endif

  return nullptr;
}

bool DeallocFixed(void* base_address, size_t length, DeallocationType deallocation_type) {
  switch (deallocation_type) {
    case DeallocationType::kDecommit: {
      // Decommit: remove access first, then release physical pages
      if (mprotect(base_address, length, PROT_NONE) != 0) {
        return false;
      }
      ProtShadowTrack(base_address, length, PageAccess::kNoAccess);
#if defined(MADV_DONTNEED)
      (void)madvise(base_address, length, MADV_DONTNEED);
#endif
      return true;
    }
    case DeallocationType::kRelease: {
      if (munmap(base_address, length) != 0) {
        return false;
      }
      ProtShadowErase(base_address, length);
      return true;
    }
    default:
      // how we get here? :(
      assert_always();
      return false;
  }
}

bool Protect(void* base_address, size_t length, PageAccess access, PageAccess* out_old_access) {
  if (out_old_access) {
    *out_old_access = PageAccess::kNoAccess;
  }

#if REX_PLATFORM_LINUX
  // NOTE(tomc): we may want to look at doing this differently. it should work for now
  //             but there is a TOCTOU window between reading and changing.
  //             This really shouldn't be an issue since VirtualProtect on Windows isn't truly
  //             atomic in a mutli-threaded process either, but it's something to be aware of.
  // Query old access before changing, if the caller needs it. The shadow answers for
  // everything this file ever mapped or reprotected; the maps scan is only for foreign
  // regions, which no SDK caller actually passes here.
  if (out_old_access) {
    uintptr_t shadow_end;
    PageAccess shadow_access;
    if (ProtShadowLookup(base_address, shadow_end, shadow_access)) {
      *out_old_access = shadow_access;
    } else {
      LinuxMapEntry e;
      if (FindEntryForAddress(base_address, e)) {
        *out_old_access = PermsToPageAccess(e.perms);
      }
    }
  }
#endif

  uint32_t prot = ToPosixProtectFlags(access);
  if (mprotect(base_address, length, prot) != 0) {
    return false;
  }
  ProtShadowTrack(base_address, length, access);
  return true;
}

bool QueryProtect(void* base_address, size_t& length, PageAccess& access_out) {
#if !REX_PLATFORM_LINUX
  access_out = PageAccess::kNoAccess;
  length = 0;
  return false;
#else
  access_out = PageAccess::kNoAccess;
  length = 0;

  // Shadow first: this is the MMIO fault recheck's path, and it runs under the global
  // critical region — the scan fallback below is what serialised every thread while the
  // arena was fragmented. The fallback's answer is not seeded into the shadow because
  // foreign regions' protections change without telling us.
  {
    uintptr_t shadow_end;
    PageAccess shadow_access;
    if (ProtShadowLookup(base_address, shadow_end, shadow_access)) {
      length = static_cast<size_t>(shadow_end - reinterpret_cast<uintptr_t>(base_address));
      access_out = shadow_access;
      return true;
    }
  }

  LinuxMapEntry e;
  if (!FindEntryForAddress(base_address, e)) {
    return false;
  }

  const uintptr_t addr = reinterpret_cast<uintptr_t>(base_address);
  length = static_cast<size_t>(e.end - addr);
  access_out = PermsToPageAccess(e.perms);

  return true;
#endif
}

FileMappingHandle CreateFileMappingHandle(const std::filesystem::path& path, size_t length,
                                          PageAccess access, bool commit) {
#if REX_PLATFORM_ANDROID
  // TODO(Triang3l): Check if memfd can be used instead on API 30+.
  if (android_ASharedMemory_create_) {
    int sharedmem_fd = android_ASharedMemory_create_(path.c_str(), length);
    return sharedmem_fd >= 0 ? static_cast<FileMappingHandle>(sharedmem_fd)
                             : kFileMappingHandleInvalid;
  }

  // Use /dev/ashmem on API versions below 26, which added ASharedMemory.
  // /dev/ashmem was disabled on API 29 for apps targeting it.
  // https://chromium.googlesource.com/chromium/src/+/master/third_party/ashmem/ashmem-dev.c
  int ashmem_fd = open("/" ASHMEM_NAME_DEF, O_RDWR);
  if (ashmem_fd < 0) {
    return kFileMappingHandleInvalid;
  }
  char ashmem_name[ASHMEM_NAME_LEN];
  strlcpy(ashmem_name, path.c_str(), rex::countof(ashmem_name));
  if (ioctl(ashmem_fd, ASHMEM_SET_NAME, ashmem_name) < 0 ||
      ioctl(ashmem_fd, ASHMEM_SET_SIZE, length) < 0) {
    close(ashmem_fd);
    return kFileMappingHandleInvalid;
  }
  return static_cast<FileMappingHandle>(ashmem_fd);
#else
  int oflag;
  switch (access) {
    case PageAccess::kNoAccess:
      oflag = 0;
      break;
    case PageAccess::kReadOnly:
    case PageAccess::kExecuteReadOnly:
      oflag = O_RDONLY;
      break;
    case PageAccess::kReadWrite:
    case PageAccess::kExecuteReadWrite:
      oflag = O_RDWR;
      break;
    default:
      assert_always();
      return kFileMappingHandleInvalid;
  }
  oflag |= O_CREAT;
  auto full_path = MakeShmName(path);
  int ret = shm_open(full_path.c_str(), oflag, 0777);
  if (ret < 0) {
    return kFileMappingHandleInvalid;
  }
  if (ftruncate64(ret, static_cast<off_t>(length)) != 0) {
    close(ret);
    shm_unlink(full_path.c_str());
    return kFileMappingHandleInvalid;
  }
  return static_cast<FileMappingHandle>(ret);
#endif
}

void CloseFileMappingHandle(FileMappingHandle handle, const std::filesystem::path& path) {
  close(static_cast<int>(handle));
#if !REX_PLATFORM_ANDROID
  auto full_path = MakeShmName(path);
  shm_unlink(full_path.c_str());
#endif
}

void* MapFileView(FileMappingHandle handle, void* base_address, size_t length, PageAccess access,
                  size_t file_offset) {
  // file_offset must be page-aligned
  const size_t page = page_size();
  if (file_offset % page != 0) {
    return nullptr;
  }

  int flags = MAP_SHARED;

  // For file views, we need MAP_FIXED to replace existing reservations.
  // The emulator reserves address space first, then maps file views into it.
  // MAP_FIXED_NOREPLACE would fail with EEXIST in this case.
  if (base_address) {
    flags |= MAP_FIXED;
  }

  uint32_t prot = ToPosixProtectFlags(access);
  void* result = mmap64(base_address, length, prot, flags, static_cast<int>(handle),
                        static_cast<off_t>(file_offset));
  if (result == MAP_FAILED) {
    return nullptr;
  }

  // Verify we got the address we asked for
  if (base_address && result != base_address) {
    munmap(result, length);
    return nullptr;
  }

  // The arena's views are the ranges every SDK memory query targets; tracking them at their
  // birth access means no arena address can ever miss the shadow and fall back to a scan.
  ProtShadowTrack(result, length, access);

  return result;
}

bool UnmapFileView(FileMappingHandle handle, void* base_address, size_t length) {
  if (munmap(base_address, length) != 0) {
    return false;
  }
  ProtShadowErase(base_address, length);
  return true;
}

}  // namespace memory
}  // namespace rex
