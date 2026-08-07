#include "runtime/refii_renderer/bik/bink_plane_publish.h"

#include "runtime/hook_observer.h"
#include "runtime/refii_renderer/bik/bink_guest_memory.h"
#include "runtime/refii_renderer/plume/fable/resource/fable_resource_snapshots.h"

#include <cstring>
#include <mutex>
#include <string_view>
#include <vector>

#include <rex/logging.h>

namespace refii::renderer {
namespace {

constexpr size_t kMaxBinkPlaneBindings = 16;
constexpr uint32_t kMaxBinkPlanePublishBytes = 16u * 1024u * 1024u;

struct BinkPlanePublishBinding {
  uint32_t texture = 0;
  uint32_t plane_memory = 0;
  uint32_t plane_bytes = 0;
  uint32_t row_pitch = 0;
  uint32_t width_bytes = 0;
  uint64_t last_hash = 0;
};

struct BinkPlanePublishState {
  std::mutex mutex;
  std::vector<BinkPlanePublishBinding> bindings;
  std::vector<FableGuestResourceSnapshot> pending;
  uint64_t next_generation = 1;
  uint32_t published_count = 0;
};

BinkPlanePublishState g_bink_plane_publish;

uint64_t HashBinkPlaneBytes(const uint8_t* data, size_t size) {
  uint64_t hash = 14695981039346656037ull;
  for (size_t i = 0; i < size; ++i) {
    hash ^= data[i];
    hash *= 1099511628211ull;
  }
  return hash != 0 ? hash : 1u;
}

void RefreshBinkPlanePublishes(uint8_t* base) {
  if (base == nullptr) {
    return;
  }
  std::lock_guard<std::mutex> lock(g_bink_plane_publish.mutex);
  for (BinkPlanePublishBinding& binding : g_bink_plane_publish.bindings) {
    if (binding.texture == 0 || binding.plane_memory == 0 ||
        binding.plane_bytes == 0 ||
        binding.plane_bytes > kMaxBinkPlanePublishBytes) {
      continue;
    }
    uint8_t* source = bink::GuestMemoryPtr(base, binding.plane_memory,
                                           binding.plane_bytes);
    if (source == nullptr) {
      continue;
    }
    const uint32_t row_pitch =
        binding.row_pitch != 0 ? binding.row_pitch : binding.width_bytes;
    const uint32_t width_bytes =
        binding.width_bytes != 0 && binding.width_bytes <= row_pitch
            ? binding.width_bytes
            : row_pitch;
    const uint32_t rows =
        row_pitch != 0 ? binding.plane_bytes / row_pitch : 0u;
    if (rows == 0 || width_bytes == 0) {
      continue;
    }
    std::vector<uint8_t> tight;
    tight.resize(static_cast<size_t>(rows) * width_bytes);
    if (width_bytes == row_pitch) {
      std::memcpy(tight.data(), source, tight.size());
    } else {
      for (uint32_t row = 0; row < rows; ++row) {
        std::memcpy(tight.data() + static_cast<size_t>(row) * width_bytes,
                    source + static_cast<size_t>(row) * row_pitch,
                    width_bytes);
      }
    }
    const uint64_t hash = HashBinkPlaneBytes(tight.data(), tight.size());
    if (hash == binding.last_hash) {
      continue;
    }
    binding.last_hash = hash;
    FableGuestResourceSnapshot* snapshot = nullptr;
    for (FableGuestResourceSnapshot& pending : g_bink_plane_publish.pending) {
      if (pending.guest_address == binding.texture) {
        snapshot = &pending;
        break;
      }
    }
    if (snapshot == nullptr) {
      g_bink_plane_publish.pending.emplace_back();
      snapshot = &g_bink_plane_publish.pending.back();
    }
    snapshot->kind = FableGuestResourceKind::kTexture;
    snapshot->guest_address = binding.texture;
    snapshot->upload_data = std::move(tight);
    snapshot->upload_data_linear = true;
    snapshot->upload_data_mip_level = 0;
    snapshot->upload_generation = g_bink_plane_publish.next_generation++;
    snapshot->serial = snapshot->upload_generation;
    snapshot->content_channel = FableGuestContentChannel::kBink;
    snapshot->last_upload_capture_site = "bink_plane_publish";
    if (g_bink_plane_publish.published_count < 12u) {
      ++g_bink_plane_publish.published_count;
      REXLOG_INFO(
          "Refii bink plane publish #{} texture=0x{:08X} plane=0x{:08X} "
          "bytes={} pitch={} gen={}",
          g_bink_plane_publish.published_count, binding.texture,
          binding.plane_memory, binding.plane_bytes, binding.row_pitch,
          static_cast<unsigned long long>(snapshot->upload_generation));
    }
  }
}

}  // namespace

void RegisterBinkVideoPlaneBindingForPlume(uint32_t texture,
                                           uint32_t plane_memory,
                                           uint32_t plane_bytes,
                                           uint32_t row_pitch,
                                           uint32_t width_bytes) {
  if (texture == 0 || plane_memory == 0 || plane_bytes == 0) {
    return;
  }
  std::lock_guard<std::mutex> lock(g_bink_plane_publish.mutex);
  BinkPlanePublishBinding* binding = nullptr;
  for (BinkPlanePublishBinding& existing : g_bink_plane_publish.bindings) {
    if (existing.texture == texture) {
      binding = &existing;
      break;
    }
  }
  if (binding == nullptr) {
    if (g_bink_plane_publish.bindings.size() >= kMaxBinkPlaneBindings) {
      g_bink_plane_publish.bindings.erase(
          g_bink_plane_publish.bindings.begin());
    }
    g_bink_plane_publish.bindings.emplace_back();
    binding = &g_bink_plane_publish.bindings.back();
  }
  binding->texture = texture;
  binding->plane_memory = plane_memory;
  binding->plane_bytes = plane_bytes;
  binding->row_pitch = row_pitch;
  binding->width_bytes = width_bytes;
  binding->last_hash = 0;
  const uint32_t neutral_pitch = row_pitch != 0 ? row_pitch : width_bytes;
  const uint32_t neutral_width =
      width_bytes != 0 && width_bytes <= neutral_pitch ? width_bytes
                                                       : neutral_pitch;
  const uint32_t neutral_rows =
      neutral_pitch != 0 ? plane_bytes / neutral_pitch : 0u;
  if (neutral_rows != 0 && neutral_width != 0 &&
      plane_bytes <= kMaxBinkPlanePublishBytes) {
    std::vector<uint8_t> neutral(
        static_cast<size_t>(neutral_rows) * neutral_width, 0x80u);
    FableGuestResourceSnapshot* snapshot = nullptr;
    for (FableGuestResourceSnapshot& pending : g_bink_plane_publish.pending) {
      if (pending.guest_address == texture) {
        snapshot = &pending;
        break;
      }
    }
    if (snapshot == nullptr) {
      g_bink_plane_publish.pending.emplace_back();
      snapshot = &g_bink_plane_publish.pending.back();
    }
    snapshot->kind = FableGuestResourceKind::kTexture;
    snapshot->guest_address = texture;
    snapshot->upload_data = std::move(neutral);
    snapshot->upload_data_linear = true;
    snapshot->upload_data_mip_level = 0;
    snapshot->upload_generation = g_bink_plane_publish.next_generation++;
    snapshot->serial = snapshot->upload_generation;
    snapshot->content_channel = FableGuestContentChannel::kBink;
    snapshot->last_upload_capture_site = "bink_plane_register_neutral";
  }
  REXLOG_INFO(
      "Refii bink plane binding registered texture=0x{:08X} plane=0x{:08X} "
      "bytes={} pitch={} width={} total={}",
      texture, plane_memory, plane_bytes, row_pitch, width_bytes,
      static_cast<uint32_t>(g_bink_plane_publish.bindings.size()));
}

void RefreshBinkVideoUploadsFromRendererHook(std::string_view,
                                             const RendererHookArgs&,
                                             uint8_t* base) {
  RefreshBinkPlanePublishes(base);
}

void RefreshBinkVideoUploadsFromRendererHook(std::string_view, uint8_t* base) {
  RefreshBinkPlanePublishes(base);
}

std::vector<FableGuestResourceSnapshot> DrainBinkPlaneSnapshotsForPlume() {
  std::vector<FableGuestResourceSnapshot> drained;
  std::lock_guard<std::mutex> lock(g_bink_plane_publish.mutex);
  drained.swap(g_bink_plane_publish.pending);
  return drained;
}

void RequeueBinkPlaneSnapshotsForPlume(
    std::vector<FableGuestResourceSnapshot>&& snapshots) {
  std::lock_guard<std::mutex> lock(g_bink_plane_publish.mutex);
  for (FableGuestResourceSnapshot& snapshot : snapshots) {
    bool replaced = false;
    for (FableGuestResourceSnapshot& pending : g_bink_plane_publish.pending) {
      if (pending.guest_address == snapshot.guest_address) {
        replaced = true;
        break;
      }
    }
    if (!replaced) {
      g_bink_plane_publish.pending.push_back(std::move(snapshot));
    }
  }
}

}  // namespace refii::renderer
