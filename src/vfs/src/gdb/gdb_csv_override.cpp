#include "gdb/gdb_csv_override.h"

#include "gdb/gdb_database.h"
#include "loose_asset_hooks.h"

#include <chrono>
#include <fstream>
#include <mutex>
#include <string>
#include <system_error>
#include <unordered_map>

#include <rex/logging/macros.h>

namespace refii::vfs {
inline namespace detail {
namespace {

struct CompiledGdbCsv {
  std::filesystem::file_time_type csv_write_time;
  std::filesystem::path compiled_path;
  bool valid = false;
};

std::mutex& CompiledGdbCsvMutex() {
  static std::mutex mutex;
  return mutex;
}

std::unordered_map<std::string, CompiledGdbCsv>& CompiledGdbCsvCache() {
  static std::unordered_map<std::string, CompiledGdbCsv> cache;
  return cache;
}

std::filesystem::path UniqueGdbStagingPath(
    const std::filesystem::path& compiled_path) {
  static std::atomic<uint64_t> sequence{0};
  return compiled_path.string() + ".tmp." +
         std::to_string(sequence.fetch_add(1, std::memory_order_relaxed) + 1);
}

bool PublishCompiledGdb(const std::filesystem::path& staging,
                        const std::filesystem::path& destination,
                        std::error_code* error) {
  std::filesystem::rename(staging, destination, *error);
  if (!*error) {
    return true;
  }

  // Windows rename cannot replace an existing destination. Preserve the last
  // known-good carrier until the new file is safely in place, and roll back
  // if publication fails.
  error->clear();
  const std::filesystem::path backup = destination.string() + ".previous";
  std::filesystem::remove(backup, *error);
  error->clear();
  std::filesystem::rename(destination, backup, *error);
  if (*error) {
    return false;
  }
  std::filesystem::rename(staging, destination, *error);
  if (*error) {
    std::error_code rollback_error;
    std::filesystem::rename(backup, destination, rollback_error);
    return false;
  }
  std::error_code cleanup_error;
  std::filesystem::remove(backup, cleanup_error);
  return true;
}

std::filesystem::path BuildCompiledGdbPath(std::string_view relative_gdb_path) {
  std::filesystem::path relative{std::string(relative_gdb_path)};
  relative = relative.lexically_normal();
  if (relative.empty() || relative.is_absolute() ||
      relative.begin()->string() == "..") {
    return {};
  }
  return GetLooseAssetRoot() / ".cache" / "gdb_compiled" / relative;
}

}  // namespace

bool TryCompileGdbCsvOverride(const std::filesystem::path& csv_host_path,
                              std::string_view relative_gdb_path,
                              std::filesystem::path* out_compiled_path) {
  std::error_code error_code;
  const std::filesystem::file_time_type csv_write_time =
      std::filesystem::last_write_time(csv_host_path, error_code);
  if (error_code) {
    return false;
  }

  const std::string cache_key = csv_host_path.generic_string();
  {
    std::lock_guard<std::mutex> lock(CompiledGdbCsvMutex());
    const auto match = CompiledGdbCsvCache().find(cache_key);
    if (match != CompiledGdbCsvCache().end() &&
        match->second.csv_write_time == csv_write_time) {
      if (match->second.valid && out_compiled_path != nullptr) {
        *out_compiled_path = match->second.compiled_path;
      }
      return match->second.valid;
    }
  }

  CompiledGdbCsv result;
  result.csv_write_time = csv_write_time;
  result.compiled_path = BuildCompiledGdbPath(relative_gdb_path);
  if (result.compiled_path.empty()) {
    REXLOG_WARN("Refii VFS gdb-csv override rejected unsafe relative path "
                "csv={} relative={}",
                csv_host_path.generic_string(), relative_gdb_path);
  } else {
    const std::filesystem::file_time_type compiled_write_time =
        std::filesystem::last_write_time(result.compiled_path, error_code);
    if (!error_code && compiled_write_time >= csv_write_time) {
      result.valid = true;
    } else {
      std::filesystem::create_directories(result.compiled_path.parent_path(),
                                          error_code);
      std::string error;
      const auto started = std::chrono::steady_clock::now();
      const std::filesystem::path staging =
          UniqueGdbStagingPath(result.compiled_path);
      if (!gdb::ConvertCsvFileToGdbFile(csv_host_path, staging, &error)) {
        REXLOG_WARN("Refii VFS gdb-csv override compile failed csv={}: {}",
                    csv_host_path.generic_string(), error);
        std::filesystem::remove(staging, error_code);
      } else {
        if (!PublishCompiledGdb(staging, result.compiled_path, &error_code)) {
          REXLOG_WARN("Refii VFS gdb-csv override stage rename failed {}: {}",
                      result.compiled_path.generic_string(),
                      error_code.message());
          std::filesystem::remove(staging, error_code);
        } else {
          const auto elapsed_ms =
              std::chrono::duration_cast<std::chrono::milliseconds>(
                  std::chrono::steady_clock::now() - started)
                  .count();
          REXLOG_INFO("Refii VFS gdb-csv override compiled csv={} -> {} "
                      "({} ms)",
                      csv_host_path.generic_string(),
                      result.compiled_path.generic_string(), elapsed_ms);
          result.valid = true;
        }
      }
    }
  }

  {
    std::lock_guard<std::mutex> lock(CompiledGdbCsvMutex());
    CompiledGdbCsvCache()[cache_key] = result;
  }
  if (result.valid && out_compiled_path != nullptr) {
    *out_compiled_path = result.compiled_path;
  }
  return result.valid;
}

}  // namespace detail
}  // namespace refii::vfs
