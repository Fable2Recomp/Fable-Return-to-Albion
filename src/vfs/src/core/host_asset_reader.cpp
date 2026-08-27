#include "core/host_asset_reader.h"

#include <atomic>
#include <string>

#include <rex/logging.h>

namespace refii::vfs {

namespace {

/// Atomic: installed from Godot's thread, called from the guest's.
std::atomic<HostAssetReaderFn> g_reader{nullptr};

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

std::string HostAssetNameForHash(uint32_t hash) {
  const HostAssetNameForHashFn resolver = g_name_for_hash.load(std::memory_order_acquire);
  if (resolver == nullptr) {
    g_hash_asks_before_ready.fetch_add(1, std::memory_order_relaxed);
    return {};
  }
  if (hash == 0u) {
    return {};
  }

  char name[kMaxAssetNameBytes];
  const uint32_t written = resolver(hash, name, kMaxAssetNameBytes);

  // A length that fills the buffer is a miss, not a name: half a path names a different file.
  if (written == 0u || written >= kMaxAssetNameBytes) {
    return {};
  }

  return std::string(name, written);
}

bool HostAssetReaderReady() {
  return g_reader.load(std::memory_order_acquire) != nullptr;
}

bool ReadHostAsset(std::string_view path, std::vector<uint8_t>& out) {
  out.clear();

  const HostAssetReaderFn reader = g_reader.load(std::memory_order_acquire);
  if (reader == nullptr) {
    g_asks_before_ready.fetch_add(1, std::memory_order_relaxed);
    return false;
  }
  if (path.empty()) {
    return false;
  }

  // NUL-terminated, because the boundary is a C ABI and `string_view` is not.
  const std::string terminated(path);

  // Size first. A zero-length asset is a real answer: success with nothing in `out`.
  uint32_t size = 0;
  if (reader(terminated.c_str(), nullptr, 0u, &size) == 0) {
    return false;
  }

  if (size == 0u) {
    return true;
  }

  if (size > kMaxAssetBytes) {
    return false;
  }

  out.resize(size);
  uint32_t written = 0;
  if (reader(terminated.c_str(), out.data(), size, &written) == 0) {
    out.clear();
    return false;
  }

  // Shorter is not a failure (the mount changed between the two reads); longer cannot be
  // trusted at all.
  if (written > size) {
    out.clear();
    return false;
  }

  out.resize(written);
  return true;
}

bool HostAssetSize(std::string_view path, uint32_t& out_size) {
  out_size = 0;

  const HostAssetReaderFn reader = g_reader.load(std::memory_order_acquire);
  if (reader == nullptr) {
    g_asks_before_ready.fetch_add(1, std::memory_order_relaxed);
    return false;
  }
  if (path.empty()) {
    return false;
  }

  const std::string terminated(path);
  uint32_t size = 0;
  if (reader(terminated.c_str(), nullptr, 0u, &size) == 0) {
    return false;
  }

  out_size = size;
  return true;
}

bool ReadHostAssetPrefix(std::string_view path, uint32_t count,
                         std::vector<uint8_t>& out) {
  out.clear();
  if (count == 0u) {
    return HostAssetReaderReady();
  }

  std::vector<uint8_t> whole;
  if (!ReadHostAsset(path, whole)) {
    return false;
  }

  // Shorter than asked is not a failure: a sniff on a file smaller than the window is a real
  // answer.
  const uint32_t take =
      count < whole.size() ? count : static_cast<uint32_t>(whole.size());
  out.assign(whole.begin(), whole.begin() + take);
  return true;
}

}  // namespace refii::vfs
