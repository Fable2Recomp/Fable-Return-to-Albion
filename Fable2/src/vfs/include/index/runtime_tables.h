#pragma once

#include "loose_asset_hooks.h"
#include "vfs_hook_types.h"

#include <cstdint>
#include <filesystem>
#include <array>
#include <chrono>
#include <mutex>
#include <optional>
#include <string>
#include <string_view>
#include <unordered_map>
#include <unordered_set>
#include <vector>

namespace refii::vfs {
inline namespace detail {
struct RefiiVfsDatabaseAliasRow {
  std::string alias;
  std::string candidate;
  bool candidate_is_language = false;
};

struct RefiiVfsDatabaseAliasCandidateCache {
  std::string root_key;
  std::unordered_map<std::string, std::vector<std::string>> candidates_by_alias;
  std::unordered_set<std::string> known_aliases;
  std::vector<RefiiVfsDatabaseAliasRow> database_rows;
  bool database_rows_loaded = false;
};

struct RefiiVfsRuntimeAliasShardCache {
  std::string root_key;
  std::unordered_set<uint16_t> loaded_shards;
  std::unordered_map<std::string, std::vector<std::string>> candidates_by_alias;
};

struct RefiiVfsRuntimeBankRecord {
  std::string alias;
  std::string candidate;
  std::string manifest_root;
};

struct RefiiVfsRuntimeBankTableCache {
  std::string root_key;
  bool loaded = false;
  std::vector<RefiiVfsRuntimeBankRecord> records;
  std::unordered_map<std::string, std::vector<RefiiVfsRuntimeBankRecord>>
      records_by_alias;
};

struct RefiiVfsDatabaseCandidateResolutionCacheEntry {
  bool found = false;
  std::filesystem::path host_path;
  std::string provider_relative_path;
  std::string candidate;
  std::string repair_source;
};

struct RefiiMainMenuBlockerTrace {
  std::string last_texture_hash;
  std::string last_texture_selection;
  std::string last_wrapper_view;
  std::string last_wrapper_slot36;
  std::string last_texture_parser;
  std::string last_texture_stream_build;
  std::string last_loading_frame;
  std::chrono::steady_clock::time_point texture_hash_at{};
  std::chrono::steady_clock::time_point texture_selection_at{};
  std::chrono::steady_clock::time_point wrapper_view_at{};
  std::chrono::steady_clock::time_point wrapper_slot36_at{};
  std::chrono::steady_clock::time_point texture_parser_at{};
  std::chrono::steady_clock::time_point texture_stream_build_at{};
  std::chrono::steady_clock::time_point loading_frame_at{};
};

struct ResolvedSyntheticBankOpenAlias {
  std::string resolved_bank;
  std::string reason;
};

std::vector<std::string> LoadRefiiVfsRuntimeCandidates(
    const std::filesystem::path& loose_root);

std::filesystem::path RefiiVfsDatabasePath(
    const std::filesystem::path& loose_root);
std::filesystem::path RefiiVfsRuntimeIndexDirectory(
    const std::filesystem::path& loose_root);
std::filesystem::path RefiiVfsRuntimeAliasDirectory(
    const std::filesystem::path& loose_root);
std::filesystem::path RefiiVfsRuntimeHashDirectory(
    const std::filesystem::path& loose_root);
std::string RefiiVfsRuntimeAliasShardName(uint16_t shard);
uint16_t RefiiVfsRuntimeAliasShardForHash(uint32_t hash);
std::filesystem::path RefiiVfsRuntimeAliasShardPath(
    const std::filesystem::path& loose_root,
    uint16_t shard);
bool RefiiVfsRuntimeAliasIndexExists(const std::filesystem::path& loose_root);
bool RefiiVfsLegacyDatabaseExists(const std::filesystem::path& loose_root);
bool RefiiVfsDatabaseExists(const std::filesystem::path& loose_root);
void AppendUniqueDatabaseCandidate(std::vector<std::string>* candidates,
                                   std::string_view candidate);
bool ParseDecimalUint32(std::string_view text, uint32_t* out_value);
std::filesystem::path RefiiVfsRuntimeCandidateTablePath(
    const std::filesystem::path& loose_root);
std::filesystem::path RefiiVfsRuntimeBankTablePath(
    const std::filesystem::path& loose_root);
std::string ResolveRefiiVfsRuntimeCandidateField(
    const std::filesystem::path& loose_root,
    std::string_view candidate_field);
bool LookupRefiiVfsTypedBankManifest(std::string_view bank_name,
                                     std::filesystem::path* out_manifest_root,
                                     std::filesystem::path* out_manifest_path);
std::vector<RefiiVfsRuntimeBankRecord> GetRefiiVfsRuntimeBankRecordsCached(
    const std::filesystem::path& loose_root);
std::vector<std::string> GetRefiiVfsRuntimeCandidatesCached(
    const std::filesystem::path& loose_root);
bool RefiiVfsRuntimeCandidateSetContains(
    const std::filesystem::path& loose_root,
    std::string_view normalized_candidate);
std::mutex& RefiiVfsDatabaseAliasCandidateCacheMutex();
RefiiVfsDatabaseAliasCandidateCache&
RefiiVfsDatabaseAliasCandidateCacheInstance();
std::mutex& RefiiVfsRuntimeAliasShardCacheMutex();
RefiiVfsRuntimeAliasShardCache& RefiiVfsRuntimeAliasShardCacheInstance();
std::mutex& RefiiVfsRuntimeBankTableCacheMutex();
RefiiVfsRuntimeBankTableCache& RefiiVfsRuntimeBankTableCacheInstance();
std::mutex& ResolvedSyntheticBankOpenAliasMutex();
std::unordered_map<std::string, ResolvedSyntheticBankOpenAlias>&
ResolvedSyntheticBankOpenAliases();
std::string& ResolvedSyntheticBankOpenAliasRootKey();
}  // namespace detail
}  // namespace refii::vfs
