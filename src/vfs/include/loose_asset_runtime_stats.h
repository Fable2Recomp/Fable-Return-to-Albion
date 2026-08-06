#pragma once

#include <atomic>
#include <cstdint>

namespace refii::vfs {

struct LooseAssetRuntimeStats {
  std::atomic<uint64_t> vfs_lookups{0};
  std::atomic<uint64_t> vfs_hits{0};
  std::atomic<uint64_t> bank_lookups{0};
  std::atomic<uint64_t> bank_hits{0};
  std::atomic<uint64_t> streams_opened{0};
  std::atomic<uint64_t> active_stream_records{0};
  std::atomic<uint64_t> stream_records_pruned{0};
  std::atomic<uint64_t> active_private_file_handles{0};
  std::atomic<uint64_t> active_private_find_handles{0};
  std::atomic<uint64_t> synthetic_banks_opened{0};
  std::atomic<uint64_t> active_synthetic_banks{0};
  std::atomic<uint64_t> synthetic_vfs_lookups{0};
  std::atomic<uint64_t> synthetic_vfs_hits{0};
  std::atomic<uint64_t> read_requests_completed{0};
  std::atomic<uint64_t> active_read_requests{0};
  std::atomic<uint64_t> refii_owned_decisions{0};
  std::atomic<uint64_t> refii_owned_native_shaped{0};
  std::atomic<uint64_t> refii_optional_misses{0};
  std::atomic<uint64_t> refii_native_fallbacks{0};
  std::atomic<uint64_t> refii_owned_streams_opened{0};
  std::atomic<uint64_t> refii_owned_streams_closed{0};
  std::atomic<uint64_t> refii_owned_reads_completed{0};
  std::atomic<uint64_t> refii_lua_streams_opened{0};
  std::atomic<uint64_t> refii_synthetic_streams_opened{0};
  std::atomic<uint64_t> refii_database_streams_opened{0};
  std::atomic<uint64_t> refii_texture_streams_opened{0};
  std::atomic<uint64_t> refii_mesh_streams_opened{0};
  std::atomic<uint64_t> refii_audio_streams_opened{0};
  std::atomic<uint64_t> refii_other_streams_opened{0};
};

}  // namespace refii::vfs
