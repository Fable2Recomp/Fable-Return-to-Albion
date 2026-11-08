#pragma once

#include <chrono>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <future>
#include <mutex>
#include <string>
#include <vector>

namespace refii::vfs {

struct LooseStreamRecord {
  std::string path;
  std::string provider_root;
  std::string provider_name;
  uint64_t expected_size = 0;
};

struct RefiiHostStreamRecord {
  std::filesystem::path path;
  uint64_t size = 0;
  uint64_t position = 0;
  uint32_t materialized_buffer_guest = 0;
  uint32_t materialized_bytes = 0;
  uint64_t read_calls = 0;
  uint64_t read_bytes = 0;
  uint64_t read_since_load = 0;
  std::ifstream file;

  /// GODOT'S BYTES, WHEN GODOT IS THE ONE HOLDING THEM.
  ///
  /// A resolution that reports `host_served` has no host path to open, so `file` stays shut
  /// and every read is served from here instead. The payload is fetched once at open: the
  /// guest's stream object is a handle it may read in any order, and re-asking Godot per read
  /// would make a seek cost a whole transfer.
  std::vector<uint8_t> bytes;
  bool host_served = false;

  /// What to ask Godot for again: the guest can hold a stream for good after reading all of it,
  /// so `bytes` goes once read whole (ReleaseRefiiHostStreamBytes) and a later read fetches it.
  std::string host_key;

  /// The digest of the bytes released, which a re-fetch must match: a live mod remount can
  /// serve `host_key` from another file of the same size, and one stream never mixes two files.
  uint64_t released_digest = 0;
  bool released = false;

  std::mutex mutex;
};

struct RefiiResourceWrapperView {
  uint32_t buffer_guest = 0;
  uint32_t size = 0;
  uint32_t view_offset = 0;
  uint32_t inner_guest = 0;
  uint32_t source_stream_guest = 0;
  std::string provider_key;
};

struct RefiiReadRequestRecord {
  /// Set when the read was postponed because the destination was not resolved yet.
  bool deferred_for_destination = false;

  uint32_t request_guest = 0;
  uint32_t stream_guest = 0;
  uint32_t buffer_guest = 0;
  uint64_t offset = 0;
  uint32_t requested_bytes = 0;
  uint32_t bytes_read = 0;
  bool uses_stream_destination = false;
  bool completed = false;
  bool attempted = false;
  bool completion_counted = false;
  std::chrono::steady_clock::time_point queued_at{};
  std::chrono::steady_clock::time_point started_at{};
  std::chrono::steady_clock::time_point completed_at{};
  std::mutex mutex;
};
}  // namespace refii::vfs
