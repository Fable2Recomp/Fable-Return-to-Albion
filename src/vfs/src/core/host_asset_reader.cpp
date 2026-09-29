#include "core/host_asset_reader.h"

#include <atomic>
#include <cstring>
#include <string>
#include <vector>

#include <rex/logging.h>

namespace refii::vfs {

namespace {

/// Atomic: installed from Godot's thread, called from the guest's.
std::atomic<HostAssetReaderFn> g_reader{nullptr};

/// Godot's asset override query; same threading as g_reader.
std::atomic<HostAssetIsOverrideFn> g_is_override{nullptr};

/// Godot's hash index; same threading as g_reader.
std::atomic<HostAssetNameForHashFn> g_name_for_hash{nullptr};

/// How many hash lookups arrived before Godot installed its index.
std::atomic<uint64_t> g_hash_asks_before_ready{0};

/// How many lookups arrived before Godot installed its reader.
std::atomic<uint64_t> g_asks_before_ready{0};

/// A cap, not a budget: a garbled size must not turn into a multi-gigabyte reserve.
constexpr uint32_t kMaxAssetBytes = 256u * 1024u * 1024u;

/// Stack-sized path buffer; a name that does not fit is reported as a miss.
constexpr uint32_t kMaxAssetNameBytes = 512u;

std::string Terminated(const char* path, size_t length) {
  if (path == nullptr || length == 0u) {
    return {};
  }
  return std::string(path, length);
}

HostAssetReaderFn ReaderOrCount() {
  const HostAssetReaderFn reader = g_reader.load(std::memory_order_acquire);
  if (reader == nullptr) {
    g_asks_before_ready.fetch_add(1, std::memory_order_relaxed);
  }
  return reader;
}

bool QuerySize(HostAssetReaderFn reader, const std::string& path, uint32_t& out_size) {
  out_size = 0;
  if (reader == nullptr || path.empty()) {
    return false;
  }

  uint32_t size = 0;
  if (reader(path.c_str(), nullptr, 0u, &size) == 0) {
    return false;
  }
  if (size > kMaxAssetBytes) {
    return false;
  }

  out_size = size;
  return true;
}

}  // namespace

void SetHostAssetReader(HostAssetReaderFn reader) {
  g_reader.store(reader, std::memory_order_release);

  // "No such asset" and "not answering yet" are indistinguishable at the call sites, so
  // installation is logged.
  REXLOG_INFO("Refii host assets: Godot's reader {} — guest asset lookups {} reach it",
              reader != nullptr ? "INSTALLED" : "withdrawn",
              reader != nullptr ? "now" : "no longer");

  if (reader != nullptr) {
    const uint64_t missed = g_asks_before_ready.load(std::memory_order_relaxed);
    if (missed != 0u) {
      REXLOG_WARN("Refii host assets: {} lookup(s) had already been refused before Godot's "
                  "reader was installed — those are misses caused by ORDER, not by absence. "
                  "The guest boots before the mount completes.",
                  missed);
    }
  }
}

void SetHostAssetNameResolver(HostAssetNameForHashFn resolver) {
  g_name_for_hash.store(resolver, std::memory_order_release);

  REXLOG_INFO("Refii host assets: Godot's hash index {} — resource-hash lookups {} reach it",
              resolver != nullptr ? "INSTALLED" : "withdrawn",
              resolver != nullptr ? "now" : "no longer");

  if (resolver != nullptr) {
    const uint64_t missed = g_hash_asks_before_ready.load(std::memory_order_relaxed);
    if (missed != 0u) {
      REXLOG_WARN("Refii host assets: {} resource-hash lookup(s) were refused before Godot's "
                  "index existed — ORDER, not absence.",
                  missed);
    }
  }
}

void SetHostAssetOverrideQuery(HostAssetIsOverrideFn query) {
  g_is_override.store(query, std::memory_order_release);
  REXLOG_INFO("Refii host assets: Godot's asset override query {} — mod virtual files {} take precedence",
              query != nullptr ? "INSTALLED" : "withdrawn",
              query != nullptr ? "now" : "no longer");
}

bool HostAssetReaderReady() {
  return g_reader.load(std::memory_order_acquire) != nullptr;
}

extern "C" int32_t refii_bridge_host_asset_is_override(const char* path, size_t length) {
  const HostAssetIsOverrideFn query = g_is_override.load(std::memory_order_acquire);
  if (query == nullptr || path == nullptr || length == 0u) {
    return 0;
  }
  return query(path, length);
}

extern "C" int32_t refii_bridge_host_asset_size(const char* path, size_t length,
                                                uint32_t* out_size) {
  uint32_t size = 0;
  const bool ok = QuerySize(ReaderOrCount(), Terminated(path, length), size);
  if (out_size != nullptr) {
    *out_size = size;
  }
  return ok ? 1 : 0;
}

extern "C" int32_t refii_bridge_read_host_asset(const char* path, size_t length,
                                                uint8_t* out, uint32_t capacity,
                                                uint32_t* out_size) {
  if (out_size != nullptr) {
    *out_size = 0;
  }

  const HostAssetReaderFn reader = ReaderOrCount();
  const std::string terminated = Terminated(path, length);
  uint32_t size = 0;
  if (!QuerySize(reader, terminated, size)) {
    return 0;
  }

  if (size == 0u) {
    return 1;
  }

  if (out == nullptr || capacity == 0u) {
    if (out_size != nullptr) {
      *out_size = size;
    }
    return 1;
  }

  uint32_t written = 0;
  if (capacity >= size) {
    if (reader(terminated.c_str(), out, size, &written) == 0) {
      return 0;
    }
    if (written > size) {
      return 0;
    }
  } else {
    std::vector<uint8_t> whole(size);
    if (reader(terminated.c_str(), whole.data(), size, &written) == 0) {
      return 0;
    }
    if (written > size) {
      return 0;
    }
    if (written > capacity) {
      written = capacity;
    }
    std::memcpy(out, whole.data(), written);
  }

  if (out_size != nullptr) {
    *out_size = written;
  }
  return 1;
}

extern "C" int32_t refii_bridge_read_host_asset_prefix(const char* path, size_t length,
                                                       uint32_t count, uint8_t* out,
                                                       uint32_t capacity,
                                                       uint32_t* out_size) {
  if (out_size != nullptr) {
    *out_size = 0;
  }

  if (count == 0u) {
    return HostAssetReaderReady() ? 1 : 0;
  }

  const HostAssetReaderFn reader = ReaderOrCount();
  const std::string terminated = Terminated(path, length);
  uint32_t size = 0;
  if (!QuerySize(reader, terminated, size)) {
    return 0;
  }

  // Shorter than asked is not a failure: a sniff on a file smaller than the window is a real
  // answer.
  const uint32_t take = count < size ? count : size;
  if (take == 0u) {
    return 1;
  }

  if (out == nullptr || capacity == 0u) {
    if (out_size != nullptr) {
      *out_size = take;
    }
    return 1;
  }

  std::vector<uint8_t> whole(size);
  uint32_t written = 0;
  if (reader(terminated.c_str(), whole.data(), size, &written) == 0) {
    return 0;
  }
  if (written > size) {
    return 0;
  }

  uint32_t copy = written < take ? written : take;
  if (copy > capacity) {
    copy = capacity;
  }
  std::memcpy(out, whole.data(), copy);

  if (out_size != nullptr) {
    *out_size = copy;
  }
  return 1;
}

extern "C" size_t refii_bridge_host_asset_name_for_hash(uint32_t hash, char* out,
                                                        size_t capacity) {
  const HostAssetNameForHashFn resolver = g_name_for_hash.load(std::memory_order_acquire);
  if (resolver == nullptr) {
    g_hash_asks_before_ready.fetch_add(1, std::memory_order_relaxed);
    return 0;
  }
  if (hash == 0u) {
    return 0;
  }

  char name[kMaxAssetNameBytes];
  const uint32_t written = resolver(hash, name, kMaxAssetNameBytes);

  // A length that fills the buffer is a miss, not a name: half a path names a different file.
  if (written == 0u || written >= kMaxAssetNameBytes) {
    return 0;
  }

  if (out != nullptr && capacity != 0u) {
    const size_t copy = capacity < written ? capacity : written;
    std::memcpy(out, name, copy);
  }

  return written;
}

}  // namespace refii::vfs
