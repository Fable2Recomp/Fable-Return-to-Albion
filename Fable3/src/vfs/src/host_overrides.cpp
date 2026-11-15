// refii - Fable 3's host overrides: the guest's resource hash to the host mount's file, when the
// host states that file an override.
//
// THE STATEMENT is the host's, made once for both sides (Engine_GameAssets.EnsureTextureLoader):
// its hash index (Engine_AssetHashIndex — the mount's paths keyed by FNV-1 of their lower-case,
// backslashed spelling, with and without `data\`), its override query (FileIsOverride: a mod's
// file, a file the host composes such as a patched database, or a loose file) and its reader.
// The guest's root table is keyed by the same hash of the same spelling — every bank record on
// the disc is the FNV-1 of its stored lower-case, backslashed, data-relative name — so a hash
// the guest asks for names the host's file without a second index here.
//
// WHO OWNS IT is stated as a kind, not a name: the host's query answers 2 for a file a mod
// contributed or the host composed from mods (a text book with a mod's rows merged in), 1 for any
// other override, the game's own loose files among them. Which mod (BnkFile.SourceMod) does not
// cross, so a hit names the mount, not the mod.

#include "fable3_vfs.h"

#include <atomic>
#include <cstring>
#include <string>

#include <rex/logging.h>

#include "core/host_asset_reader.h"

namespace {

std::atomic<uint64_t> g_served{0};
std::atomic<uint64_t> g_left_to_disk{0};
std::atomic<uint64_t> g_added{0};

}  // namespace

extern "C" int32_t refiii_vfs_find_override(uint32_t hash, uint32_t* out_size, char* out_path,
                                            size_t path_capacity) {
  if (out_size != nullptr) {
    *out_size = 0;
  }
  if (out_path != nullptr && path_capacity != 0u) {
    out_path[0] = '\0';
  }
  if (hash == 0u || out_path == nullptr || path_capacity == 0u) {
    return 0;
  }

  const std::string path = refii::vfs::HostAssetNameForHash(hash);
  if (path.empty() || path.size() >= path_capacity) {
    return 0;
  }
  const int32_t kind = refii::vfs::HostAssetOverrideKind(path);
  if (kind <= 0) {
    return 0;
  }

  uint32_t size = 0;
  if (!refii::vfs::HostAssetSize(path, size)) {
    return 0;
  }
  if (size == 0u) {
    REXLOG_WARN("[vfs] fable3 override: {} (0x{:08X}) is empty — not served, the guest's own "
                "file stands",
                path, hash);
    return 0;
  }

  std::memcpy(out_path, path.c_str(), path.size() + 1u);
  if (out_size != nullptr) {
    *out_size = size;
  }
  return kind >= REFIII_VFS_MOD_OWNED ? REFIII_VFS_MOD_OWNED : REFIII_VFS_OVERRIDE;
}

extern "C" int32_t refiii_vfs_read_override(const char* path, uint8_t* out, uint32_t size) {
  if (path == nullptr || out == nullptr || size == 0u) {
    return 0;
  }

  uint32_t written = 0;
  if (refii_bridge_read_host_asset(path, std::strlen(path), out, size, &written) == 0) {
    REXLOG_WARN("[vfs] fable3 override: the host mount refused to read {}", path);
    return 0;
  }
  if (written != size) {
    REXLOG_WARN("[vfs] fable3 override: {} read {} bytes of the {} the host stated", path,
                written, size);
    return 0;
  }
  return 1;
}

extern "C" void refiii_vfs_note_override(uint32_t hash, const char* path, uint32_t size,
                                         int32_t served) {
  const char* shown = path != nullptr ? path : "";
  if (served != 0) {
    g_served.fetch_add(1, std::memory_order_relaxed);
    REXLOG_INFO("[vfs] fable3 override: {} (0x{:08X}) from the host mount, {} bytes", shown, hash,
                size);
    return;
  }

  g_left_to_disk.fetch_add(1, std::memory_order_relaxed);
  REXLOG_INFO("[vfs] fable3 override not applied: {} (0x{:08X}) — the guest reads it from disk, "
              "and the host does not state whether a mod or the game's own loose file is behind it",
              shown, hash);
}

extern "C" void refiii_vfs_note_added(uint32_t hash, const char* path) {
  g_added.fetch_add(1, std::memory_order_relaxed);
  REXLOG_INFO("[vfs] fable3 added: {} (0x{:08X}) from the host mount", path != nullptr ? path : "",
              hash);
}

extern "C" void refiii_vfs_override_counts(uint64_t* out_served, uint64_t* out_left_to_disk,
                                           uint64_t* out_added) {
  if (out_served != nullptr) {
    *out_served = g_served.load(std::memory_order_relaxed);
  }
  if (out_left_to_disk != nullptr) {
    *out_left_to_disk = g_left_to_disk.load(std::memory_order_relaxed);
  }
  if (out_added != nullptr) {
    *out_added = g_added.load(std::memory_order_relaxed);
  }
}
