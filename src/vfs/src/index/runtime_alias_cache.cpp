#include "vfs_hooks_private.h"

#include <iterator>

namespace refii::vfs {
inline namespace detail {

std::vector<std::string> LookupRefiiVfsDatabaseAliasCandidates(
    std::string_view asset_path,
    const std::filesystem::path& loose_root);

bool IsCachedOptionalSyntheticVfsMiss(std::string_view asset_path) {
  const std::string normalized = NormalizeLooseAssetKey(asset_path);
  if (normalized.empty() || !IsOptionalSyntheticVfsProbe(normalized)) {
    return false;
  }

  const std::filesystem::path loose_root = GetLooseAssetRoot();
  const std::string root_key = loose_root.lexically_normal().string();
  const std::string cache_key = root_key + "\n" + normalized;
  static std::mutex mutex;
  static std::unordered_map<std::string, bool> miss_by_key;

  if (IsKnownAbsentBootProbe(normalized)) {
    bool owned_by_database = false;
    if (IsGameFaceBinaryAssetKey(normalized)) {
      LooseAssetResolution resolution;
      std::string candidate;
      owned_by_database = ResolveExactGameFaceBinaryCarrierLooseAsset(
          normalized, &resolution, &candidate);
    } else if (EndsWith(normalized, ".bnk")) {
      owned_by_database =
          LookupRefiiVfsTypedBankManifest(normalized, nullptr, nullptr);
    } else {
      std::error_code error;
      owned_by_database =
          std::filesystem::is_regular_file(loose_root / normalized, error);
      if (!owned_by_database && StartsWith(normalized, "data/")) {
        error.clear();
        owned_by_database = std::filesystem::is_regular_file(
            loose_root / normalized.substr(5), error);
      }
    }
    const bool miss = !owned_by_database;
    std::lock_guard<std::mutex> lock(mutex);
    miss_by_key.emplace(cache_key, miss);
    return miss;
  }

  {
    std::lock_guard<std::mutex> lock(mutex);
    const auto cached = miss_by_key.find(cache_key);
    if (cached != miss_by_key.end()) {
      return cached->second;
    }
  }

  bool owned_by_database = false;
  if (EndsWith(normalized, ".bnk")) {
    owned_by_database =
        LookupRefiiVfsTypedBankManifest(normalized, nullptr, nullptr);
  } else if (RefiiVfsDatabaseExists(loose_root)) {
    owned_by_database =
        !LookupRefiiVfsDatabaseAliasCandidates(normalized, loose_root).empty();
  }

  if (!owned_by_database) {
    std::error_code error;
    owned_by_database =
        std::filesystem::is_regular_file(loose_root / normalized, error);
    if (!owned_by_database && StartsWith(normalized, "data/")) {
      error.clear();
      owned_by_database = std::filesystem::is_regular_file(
          loose_root / normalized.substr(5), error);
    }
  }

  const bool miss = !owned_by_database;
  {
    std::lock_guard<std::mutex> lock(mutex);
    miss_by_key.emplace(cache_key, miss);
  }

  if (miss && VfsTracingEnabled() &&
      ShouldLogRepeatedVfsTraceOperation("OptionalSyntheticVfsMiss", 32)) {
    REXLOG_INFO("Refii VFS optional miss cached asset={} root={}",
                normalized,
                root_key);
  } else if (!miss && VfsTracingEnabled() &&
             ShouldLogRepeatedVfsTraceOperation("OptionalSyntheticVfsOwned", 16)) {
    REXLOG_INFO("Refii VFS optional probe owned by database asset={} root={}",
                normalized,
                root_key);
  }
  return miss;
}

std::vector<RefiiVfsRuntimeLuaRecord> LoadRefiiVfsRuntimeLuaRecords(
    const std::filesystem::path& loose_root) {
  std::vector<RefiiVfsRuntimeLuaRecord> records;
  auto load_table = [&](const std::filesystem::path& table_path) {
    std::ifstream input(table_path);
    std::string line;
    while (std::getline(input, line)) {
      if (!line.empty() && line.back() == '\r') {
        line.pop_back();
      }
      if (line.empty() || line[0] == '#') {
        continue;
      }

      const size_t first_tab = line.find('\t');
      if (first_tab == std::string::npos || first_tab == 0) {
        continue;
      }
      const size_t second_tab = line.find('\t', first_tab + 1);
      if (second_tab == std::string::npos || second_tab + 1 >= line.size()) {
        continue;
      }

      RefiiVfsRuntimeLuaRecord record;
      record.alias =
          NormalizeLooseAssetKey(std::string_view(line).substr(0, first_tab));
      record.candidate = NormalizeLooseAssetKey(std::string_view(line).substr(
          first_tab + 1, second_tab - first_tab - 1));
      record.kind =
          NormalizeLooseAssetKey(std::string_view(line).substr(second_tab + 1));
      if (record.alias.empty() || record.candidate.empty() ||
          record.kind.empty()) {
        continue;
      }
      records.push_back(std::move(record));
    }
  };

  const std::filesystem::path runtime_table =
      RefiiVfsRuntimeLuaTablePath(loose_root);
  const std::filesystem::path extracted_table =
      loose_root / "luasource" / "refii_vfs" / "lua.tsv";
  load_table(runtime_table);
  if (extracted_table.lexically_normal() != runtime_table.lexically_normal()) {
    load_table(extracted_table);
  }
  return records;
}

std::vector<RefiiVfsRuntimeLuaRecord> LoadRefiiVfsRuntimeLuaRecordsForSearchRoots(
    const std::filesystem::path& loose_root) {
  std::vector<RefiiVfsRuntimeLuaRecord> records;
  std::vector<std::filesystem::path> roots;
  if (loose_root.lexically_normal() == GetLooseAssetRoot().lexically_normal()) {
    roots = GetLooseAssetSearchRoots();
  }
  if (roots.empty()) {
    roots.push_back(loose_root);
  }

  for (const std::filesystem::path& root : roots) {
    if (root.empty()) {
      continue;
    }
    std::vector<RefiiVfsRuntimeLuaRecord> root_records =
        LoadRefiiVfsRuntimeLuaRecords(root);
    records.insert(records.end(),
                   std::make_move_iterator(root_records.begin()),
                   std::make_move_iterator(root_records.end()));
  }
  return records;
}

bool ResolveTypedLuaVfsAsset(std::string_view asset_path,
                             bool carrier,
                             LooseAssetResolution* out_resolution,
                             std::string* out_candidate) {
  const std::filesystem::path loose_root = GetLooseAssetRoot();
  const std::string root_key = loose_root.lexically_normal().string();
  const std::string normalized_asset = NormalizeLooseAssetKey(asset_path);
  const std::string wanted_kind = carrier ? "carrier" : "override";
  if (root_key.empty() || normalized_asset.empty()) {
    return false;
  }

  auto find_record = [&](const RefiiVfsRuntimeLuaTableCache& cache)
      -> const RefiiVfsRuntimeLuaRecord* {
    const auto match = cache.records_by_alias.find(normalized_asset);
    if (match == cache.records_by_alias.end()) {
      return nullptr;
    }
    const RefiiVfsRuntimeLuaRecord* extracted_source_record = nullptr;
    for (const RefiiVfsRuntimeLuaRecord& record : match->second) {
      if (record.kind == wanted_kind) {
        return &record;
      }
      if (carrier && record.kind == "override" &&
          IsGameFaceBoundaryPath(normalized_asset)) {
        extracted_source_record = &record;
      }
    }
    return extracted_source_record;
  };

  auto resolve_record_host =
      [&](const RefiiVfsRuntimeLuaRecord& record,
          std::filesystem::path* out_host_path,
          std::string* out_provider_relative) {
        if (out_host_path == nullptr || out_provider_relative == nullptr) {
          return false;
        }

        const bool extracted_override_record =
            carrier && record.kind == "override" &&
            IsGameFaceBoundaryPath(normalized_asset);
        const std::filesystem::path direct_relative = record.candidate;
        const std::string normalized_candidate =
            NormalizeLooseAssetKey(record.candidate);
        std::error_code error;
        auto resolve_relative = [&](const std::filesystem::path& relative) {
          std::vector<std::filesystem::path> roots;
          if (loose_root.lexically_normal() ==
              GetLooseAssetRoot().lexically_normal()) {
            roots = GetLooseAssetSearchRoots();
          }
          if (roots.empty()) {
            roots.push_back(loose_root);
          }
          for (const std::filesystem::path& root : roots) {
            if (root.empty()) {
              continue;
            }
            error.clear();
            const std::filesystem::path host = root / relative;
            if (!std::filesystem::is_regular_file(host, error)) {
              continue;
            }
            *out_host_path = host;
            *out_provider_relative = relative.generic_string();
            return true;
          }
          return false;
        };

        if (carrier || extracted_override_record) {
          if (resolve_relative(direct_relative)) {
            return true;
          }
          if (!StartsWith(normalized_candidate, "luasource/")) {
            return resolve_relative(std::filesystem::path("luasource") /
                                    direct_relative);
          }
          return false;
        }

        return resolve_relative(direct_relative);
  };

  {
    std::lock_guard<std::mutex> lock(RefiiVfsRuntimeLuaTableCacheMutex());
    auto& cache = RefiiVfsRuntimeLuaTableCacheInstance();
    if (cache.root_key == root_key && cache.loaded) {
      const RefiiVfsRuntimeLuaRecord* record = find_record(cache);
      if (record == nullptr) {
        return false;
      }
      std::filesystem::path host_path;
      std::string provider_relative;
      if (!resolve_record_host(*record, &host_path, &provider_relative)) {
        return false;
      }
      if (out_resolution != nullptr) {
        out_resolution->host_path = host_path.lexically_normal();
        out_resolution->normalized_key = normalized_asset;
        out_resolution->provider_relative_path = provider_relative;
      }
      if (out_candidate != nullptr) {
        *out_candidate = provider_relative;
      }
      return true;
    }
  }

  std::vector<RefiiVfsRuntimeLuaRecord> loaded_records =
      LoadRefiiVfsRuntimeLuaRecordsForSearchRoots(loose_root);
  {
    std::lock_guard<std::mutex> lock(RefiiVfsRuntimeLuaTableCacheMutex());
    auto& cache = RefiiVfsRuntimeLuaTableCacheInstance();
    if (cache.root_key != root_key || !cache.loaded) {
      cache.root_key = root_key;
      cache.records_by_alias.clear();
      for (const RefiiVfsRuntimeLuaRecord& record : loaded_records) {
        cache.records_by_alias[record.alias].push_back(record);
      }
      cache.loaded = true;
    }
  }

  return ResolveTypedLuaVfsAsset(
      normalized_asset, carrier, out_resolution, out_candidate);
}

std::vector<RefiiVfsRuntimeLuaRecord> GetRefiiVfsRuntimeLuaRecordsCached(
    const std::filesystem::path& loose_root) {
  const std::string root_key = loose_root.lexically_normal().string();
  if (root_key.empty()) {
    return {};
  }

  auto snapshot_records = [](const RefiiVfsRuntimeLuaTableCache& cache) {
    std::vector<RefiiVfsRuntimeLuaRecord> records;
    for (const auto& [alias, aliases] : cache.records_by_alias) {
      (void)alias;
      records.insert(records.end(), aliases.begin(), aliases.end());
    }
    return records;
  };

  {
    std::lock_guard<std::mutex> lock(RefiiVfsRuntimeLuaTableCacheMutex());
    const auto& cache = RefiiVfsRuntimeLuaTableCacheInstance();
    if (cache.root_key == root_key && cache.loaded) {
      return snapshot_records(cache);
    }
  }

  const std::vector<RefiiVfsRuntimeLuaRecord> loaded_records =
      LoadRefiiVfsRuntimeLuaRecords(loose_root);
  {
    std::lock_guard<std::mutex> lock(RefiiVfsRuntimeLuaTableCacheMutex());
    auto& cache = RefiiVfsRuntimeLuaTableCacheInstance();
    if (cache.root_key != root_key || !cache.loaded) {
      cache.root_key = root_key;
      cache.records_by_alias.clear();
      for (const RefiiVfsRuntimeLuaRecord& record : loaded_records) {
        cache.records_by_alias[record.alias].push_back(record);
      }
      cache.loaded = true;
    }
    return snapshot_records(cache);
  }
}

std::unordered_map<std::string, std::vector<std::string>>
LoadRefiiVfsRuntimeAliasShard(const std::filesystem::path& loose_root,
                              uint16_t shard) {
  std::unordered_map<std::string, std::vector<std::string>> loaded_aliases;
  std::ifstream shard_file(RefiiVfsRuntimeAliasShardPath(loose_root, shard));
  std::string line;
  while (std::getline(shard_file, line)) {
    if (!line.empty() && line.back() == '\r') {
      line.pop_back();
    }
    if (line.empty() || line[0] == '#') {
      continue;
    }

    const size_t tab = line.find('\t');
    if (tab == std::string::npos || tab == 0 || tab + 1 >= line.size()) {
      continue;
    }
    const std::string normalized_alias =
        NormalizeLooseAssetKey(std::string_view(line).substr(0, tab));
    if (normalized_alias.empty()) {
      continue;
    }
    AppendUniqueDatabaseCandidate(
        &loaded_aliases[normalized_alias],
        ResolveRefiiVfsRuntimeCandidateField(
            loose_root, std::string_view(line).substr(tab + 1)));
  }
  return loaded_aliases;
}

std::unordered_map<uint32_t, std::vector<std::string>>
LoadRefiiVfsRuntimeHashShard(const std::filesystem::path& loose_root,
                             uint8_t shard) {
  std::unordered_map<uint32_t, std::vector<std::string>> loaded_hashes;
  std::ifstream shard_file(RefiiVfsRuntimeHashShardPath(loose_root, shard));
  std::string line;
  while (std::getline(shard_file, line)) {
    if (!line.empty() && line.back() == '\r') {
      line.pop_back();
    }
    if (line.empty() || line[0] == '#') {
      continue;
    }

    const size_t tab = line.find('\t');
    if (tab == std::string::npos || tab == 0 || tab + 1 >= line.size()) {
      continue;
    }

    uint32_t parsed_hash = 0;
    if (!ParseHexUint32(std::string_view(line).substr(0, tab), &parsed_hash)) {
      continue;
    }
    AppendUniqueDatabaseCandidate(
        &loaded_hashes[parsed_hash],
        ResolveRefiiVfsRuntimeCandidateField(
            loose_root, std::string_view(line).substr(tab + 1)));
  }
  return loaded_hashes;
}

std::vector<std::string> LookupRefiiVfsRuntimeHashShardCandidates(
    const std::filesystem::path& loose_root,
    uint8_t shard,
    uint32_t requested_hash) {
  std::vector<std::string> candidates;
  std::ifstream shard_file(RefiiVfsRuntimeHashShardPath(loose_root, shard));
  std::string line;
  while (std::getline(shard_file, line)) {
    if (!line.empty() && line.back() == '\r') {
      line.pop_back();
    }
    if (line.empty() || line[0] == '#') {
      continue;
    }

    const size_t tab = line.find('\t');
    if (tab == std::string::npos || tab == 0 || tab + 1 >= line.size()) {
      continue;
    }

    uint32_t parsed_hash = 0;
    if (!ParseHexUint32(std::string_view(line).substr(0, tab),
                        &parsed_hash) ||
        parsed_hash != requested_hash) {
      continue;
    }
    AppendUniqueDatabaseCandidate(
        &candidates,
        ResolveRefiiVfsRuntimeCandidateField(
            loose_root, std::string_view(line).substr(tab + 1)));
  }
  return candidates;
}

std::vector<std::filesystem::path> GetRefiiVfsMetadataSearchRoots(
    const std::filesystem::path& loose_root) {
  std::vector<std::filesystem::path> roots;
  if (loose_root.lexically_normal() == GetLooseAssetRoot().lexically_normal()) {
    roots = GetLooseAssetSearchRoots();
  }
  if (roots.empty()) {
    roots.push_back(loose_root);
  }
  roots.erase(std::remove_if(roots.begin(),
                             roots.end(),
                             [](const std::filesystem::path& root) {
                               return root.empty();
                             }),
              roots.end());
  return roots;
}

bool AnyRefiiVfsRuntimeAliasIndexExists(
    const std::filesystem::path& loose_root) {
  for (const std::filesystem::path& root :
       GetRefiiVfsMetadataSearchRoots(loose_root)) {
    if (RefiiVfsRuntimeAliasIndexExists(root)) {
      return true;
    }
  }
  return false;
}

bool AnyRefiiVfsDatabaseExists(const std::filesystem::path& loose_root) {
  for (const std::filesystem::path& root :
       GetRefiiVfsMetadataSearchRoots(loose_root)) {
    if (RefiiVfsDatabaseExists(root)) {
      return true;
    }
  }
  return false;
}

bool AnyRefiiVfsLegacyDatabaseExists(
    const std::filesystem::path& loose_root) {
  for (const std::filesystem::path& root :
       GetRefiiVfsMetadataSearchRoots(loose_root)) {
    if (RefiiVfsLegacyDatabaseExists(root)) {
      return true;
    }
  }
  return false;
}

bool AnyRefiiVfsRuntimeHashIndexExists(
    const std::filesystem::path& loose_root) {
  for (const std::filesystem::path& root :
       GetRefiiVfsMetadataSearchRoots(loose_root)) {
    if (RefiiVfsRuntimeHashIndexExists(root)) {
      return true;
    }
  }
  return false;
}

std::unordered_map<std::string, std::vector<std::string>>
LoadRefiiVfsRuntimeAliasShardForSearchRoots(
    const std::filesystem::path& loose_root,
    uint16_t shard) {
  std::unordered_map<std::string, std::vector<std::string>> loaded_aliases;
  for (const std::filesystem::path& root :
       GetRefiiVfsMetadataSearchRoots(loose_root)) {
    if (!RefiiVfsRuntimeAliasIndexExists(root)) {
      continue;
    }
    std::unordered_map<std::string, std::vector<std::string>> root_aliases =
        LoadRefiiVfsRuntimeAliasShard(root, shard);
    for (auto& [alias, candidates] : root_aliases) {
      auto& merged_candidates = loaded_aliases[alias];
      for (std::string& candidate : candidates) {
        AddUnique(merged_candidates, std::move(candidate));
      }
    }
  }
  return loaded_aliases;
}

struct RefiiVfsDatabaseAliasLoadSummary {
  bool loaded_now = false;
  size_t rows = 0;
};

RefiiVfsDatabaseAliasLoadSummary EnsureRefiiVfsDatabaseAliasRowsLoaded(
    const std::filesystem::path& loose_root,
    std::string_view root_key) {
  {
    std::lock_guard<std::mutex> lock(
        RefiiVfsDatabaseAliasCandidateCacheMutex());
    auto& cache = RefiiVfsDatabaseAliasCandidateCacheInstance();
    if (cache.root_key != root_key) {
      cache = RefiiVfsDatabaseAliasCandidateCache{};
      cache.root_key = std::string(root_key);
    }
    if (cache.database_rows_loaded) {
      return {};
    }
  }

  std::vector<RefiiVfsDatabaseAliasRow> loaded_rows;
  loaded_rows.reserve(600000);

  std::string line;
  for (const std::filesystem::path& root :
       GetRefiiVfsMetadataSearchRoots(loose_root)) {
    if (!RefiiVfsLegacyDatabaseExists(root)) {
      continue;
    }
    std::ifstream database(RefiiVfsDatabasePath(root));
    while (std::getline(database, line)) {
      if (!line.empty() && line.back() == '\r') {
        line.pop_back();
      }
      if (line.empty() || line[0] == '#') {
        continue;
      }

      const size_t first_tab = line.find('\t');
      if (first_tab == std::string::npos) {
        continue;
      }
      const size_t second_tab = line.find('\t', first_tab + 1);
      if (second_tab == std::string::npos) {
        continue;
      }

      const size_t third_tab = line.find('\t', second_tab + 1);
      const std::string_view row_alias =
          std::string_view(line).substr(first_tab + 1,
                                        second_tab - first_tab - 1);
      const std::string_view candidate =
          std::string_view(line).substr(second_tab + 1,
                                        third_tab == std::string::npos
                                            ? std::string::npos
                                            : third_tab - second_tab - 1);

      if (!StartsWith(candidate, "language/") &&
          !StartsWith(candidate, "language\\")) {
        continue;
      }
      if (!ShouldIndexRefiiResourceHashCandidate(candidate)) {
        continue;
      }

      const std::string normalized_candidate = NormalizeLooseAssetKey(candidate);
      if (normalized_candidate.empty()) {
        continue;
      }
      loaded_rows.push_back({NormalizeLooseAssetKey(row_alias),
                             normalized_candidate,
                             StartsWith(normalized_candidate, "language/")});
    }
  }

  std::lock_guard<std::mutex> lock(RefiiVfsDatabaseAliasCandidateCacheMutex());
  auto& cache = RefiiVfsDatabaseAliasCandidateCacheInstance();
  if (cache.root_key != root_key) {
    cache = RefiiVfsDatabaseAliasCandidateCache{};
    cache.root_key = std::string(root_key);
  }
  if (cache.database_rows_loaded) {
    return {};
  }

  RefiiVfsDatabaseAliasLoadSummary summary;
  summary.loaded_now = true;
  summary.rows = loaded_rows.size();
  cache.database_rows = std::move(loaded_rows);
  cache.database_rows_loaded = true;
  return summary;
}

bool PrimeRefiiVfsRuntimeAliasCandidates(
    const std::filesystem::path& loose_root,
    const std::vector<std::string>& aliases) {
  if (aliases.empty() || !AnyRefiiVfsRuntimeAliasIndexExists(loose_root)) {
    return false;
  }

  const std::string root_key = loose_root.lexically_normal().string();
  // Each requested alias resolves through a small probe set: its normalized
  // spelling plus the canonical database spelling (data/ root stripped,
  // language locale dropped, patch_text folded onto text). The database only
  // stores canonical rows; other spellings are rewritten here at lookup time.
  struct RequestedAliasProbes {
    std::string normalized;
    std::vector<std::string> probes;
  };
  std::vector<RequestedAliasProbes> requested_probes;
  std::vector<std::string> aliases_to_load;
  {
    std::lock_guard<std::mutex> lock(
        RefiiVfsDatabaseAliasCandidateCacheMutex());
    auto& cache = RefiiVfsDatabaseAliasCandidateCacheInstance();
    if (cache.root_key != root_key) {
      cache = RefiiVfsDatabaseAliasCandidateCache{};
      cache.root_key = root_key;
    }
    auto add_alias_to_load = [&](std::string_view normalized_alias) {
      if (normalized_alias.empty() ||
          std::find(aliases_to_load.begin(), aliases_to_load.end(),
                    normalized_alias) != aliases_to_load.end()) {
        return;
      }
      aliases_to_load.emplace_back(normalized_alias);
    };

    for (const std::string& alias : aliases) {
      std::string normalized_alias = NormalizeLooseAssetKey(alias);
      if (normalized_alias.empty() ||
          cache.known_aliases.find(normalized_alias) !=
              cache.known_aliases.end() ||
          std::find_if(requested_probes.begin(), requested_probes.end(),
                       [&](const RequestedAliasProbes& existing) {
                         return existing.normalized == normalized_alias;
                       }) != requested_probes.end()) {
        continue;
      }

      RequestedAliasProbes request;
      request.probes.push_back(normalized_alias);
      std::string canonical =
          CanonicalizeRefiiVfsDatabaseAliasKey(normalized_alias);
      if (!canonical.empty() && canonical != normalized_alias) {
        request.probes.push_back(std::move(canonical));
      }
      for (const std::string& probe : request.probes) {
        add_alias_to_load(probe);
      }
      request.normalized = std::move(normalized_alias);
      requested_probes.push_back(std::move(request));
    }
  }

  if (aliases_to_load.empty()) {
    return true;
  }

  const auto started = std::chrono::steady_clock::now();
  std::unordered_map<uint16_t, std::vector<std::string>> aliases_by_shard;
  for (const std::string& alias : aliases_to_load) {
    const uint16_t shard =
        RefiiVfsRuntimeAliasShardForHash(ComputeResourceNameHash(alias));
    aliases_by_shard[shard].push_back(alias);
  }

  std::vector<uint16_t> shards_to_load;
  size_t scanned_shards = 0;
  for (const auto& [shard, shard_aliases] : aliases_by_shard) {
    (void)shard_aliases;
    std::lock_guard<std::mutex> lock(RefiiVfsRuntimeAliasShardCacheMutex());
    auto& shard_cache = RefiiVfsRuntimeAliasShardCacheInstance();
    if (shard_cache.root_key != root_key) {
      shard_cache = RefiiVfsRuntimeAliasShardCache{};
      shard_cache.root_key = root_key;
    }
    if (shard_cache.loaded_shards.find(shard) == shard_cache.loaded_shards.end()) {
      shards_to_load.push_back(shard);
    }
  }

  for (const uint16_t shard : shards_to_load) {
    std::unordered_map<std::string, std::vector<std::string>> loaded_shard =
        LoadRefiiVfsRuntimeAliasShardForSearchRoots(loose_root, shard);
    ++scanned_shards;

    std::lock_guard<std::mutex> lock(RefiiVfsRuntimeAliasShardCacheMutex());
    auto& shard_cache = RefiiVfsRuntimeAliasShardCacheInstance();
    if (shard_cache.root_key != root_key) {
      shard_cache = RefiiVfsRuntimeAliasShardCache{};
      shard_cache.root_key = root_key;
    }
    if (!shard_cache.loaded_shards.insert(shard).second) {
      continue;
    }
    for (auto& [alias, candidates] : loaded_shard) {
      auto& cached_candidates = shard_cache.candidates_by_alias[alias];
      for (std::string& candidate : candidates) {
        AddUnique(cached_candidates, std::move(candidate));
      }
    }
  }

  std::unordered_map<std::string, std::vector<std::string>> loaded_candidates;
  loaded_candidates.reserve(aliases_to_load.size());
  {
    std::lock_guard<std::mutex> lock(RefiiVfsRuntimeAliasShardCacheMutex());
    const auto& shard_cache = RefiiVfsRuntimeAliasShardCacheInstance();
    if (shard_cache.root_key == root_key) {
      for (const std::string& alias : aliases_to_load) {
        if (const auto match = shard_cache.candidates_by_alias.find(alias);
            match != shard_cache.candidates_by_alias.end()) {
          loaded_candidates.emplace(alias, match->second);
        }
      }
    }
  }

  // A probe key that is itself an indexed file resolves to itself; the
  // database does not materialize identity rows.
  std::unordered_map<std::string, std::vector<std::string>>
      candidates_by_request;
  candidates_by_request.reserve(requested_probes.size());
  for (const RequestedAliasProbes& request : requested_probes) {
    std::vector<std::string>& candidates =
        candidates_by_request[request.normalized];
    for (const std::string& probe : request.probes) {
      if (const auto match = loaded_candidates.find(probe);
          match != loaded_candidates.end()) {
        for (const std::string& candidate : match->second) {
          AddUnique(candidates, candidate);
        }
      }
      if (RefiiVfsRuntimeCandidateSetContains(loose_root, probe)) {
        AddUnique(candidates, probe);
      }
    }
  }

  size_t hit_aliases = 0;
  size_t loaded_candidates_count = 0;
  {
    std::lock_guard<std::mutex> lock(
        RefiiVfsDatabaseAliasCandidateCacheMutex());
    auto& cache = RefiiVfsDatabaseAliasCandidateCacheInstance();
    if (cache.root_key != root_key) {
      cache = RefiiVfsDatabaseAliasCandidateCache{};
      cache.root_key = root_key;
    }
    for (auto& [request_alias, candidates] : candidates_by_request) {
      if (!candidates.empty()) {
        ++hit_aliases;
        loaded_candidates_count += candidates.size();
      }
      cache.candidates_by_alias.emplace(request_alias, std::move(candidates));
      cache.known_aliases.insert(request_alias);
    }
  }

  if (VfsTracingEnabled() &&
      ShouldLogRepeatedVfsTraceOperation("VfsDatabaseAliasPrime", 32)) {
    const auto elapsed_ms =
        std::chrono::duration_cast<std::chrono::milliseconds>(
            std::chrono::steady_clock::now() - started)
            .count();
    REXLOG_INFO("Refii VFS runtime alias prime aliases={} hits={} "
                "candidates={} shards={} elapsed_ms={} sample=[{}]",
                aliases_to_load.size(),
                hit_aliases,
                loaded_candidates_count,
                scanned_shards,
                elapsed_ms,
                FormatCandidateSample(aliases_to_load, 4));
  }
  return true;
}

bool PrimeRefiiVfsRuntimeExactAliasCandidate(
    const std::filesystem::path& loose_root,
    std::string_view alias) {
  const std::string normalized_alias = NormalizeLooseAssetKey(alias);
  if (normalized_alias.empty()) {
    return false;
  }
  // The batched prime already handles the canonical probe rewrites and the
  // candidate-set identity check for a single alias.
  return PrimeRefiiVfsRuntimeAliasCandidates(
      loose_root, std::vector<std::string>{normalized_alias});
}

void PrimeRefiiVfsDatabaseAliasCandidates(
    const std::filesystem::path& loose_root,
    const std::vector<std::string>& aliases) {
  if (aliases.empty()) {
    return;
  }
  if (PrimeRefiiVfsRuntimeAliasCandidates(loose_root, aliases)) {
    return;
  }
  if (!AnyRefiiVfsLegacyDatabaseExists(loose_root)) {
    return;
  }

  const std::string root_key = loose_root.lexically_normal().string();
  std::vector<std::string> aliases_to_load;
  {
    std::lock_guard<std::mutex> lock(
        RefiiVfsDatabaseAliasCandidateCacheMutex());
    auto& cache = RefiiVfsDatabaseAliasCandidateCacheInstance();
    if (cache.root_key != root_key) {
      cache = RefiiVfsDatabaseAliasCandidateCache{};
      cache.root_key = root_key;
    }
    auto add_alias_to_load = [&](std::string normalized_alias) {
      if (normalized_alias.empty() ||
          cache.known_aliases.find(normalized_alias) !=
              cache.known_aliases.end() ||
          std::find(aliases_to_load.begin(), aliases_to_load.end(),
                    normalized_alias) != aliases_to_load.end()) {
        return;
      }
      aliases_to_load.push_back(std::move(normalized_alias));
    };
    for (const std::string& alias : aliases) {
      const std::string normalized_alias = NormalizeLooseAssetKey(alias);
      add_alias_to_load(normalized_alias);

      constexpr std::string_view kLanguagePrefix = "language/";
      constexpr std::string_view kKnownLanguageLocales[] = {
          "en-uk", "hu-hu", "zh-cht", "cs-cz",
          "ko-kr", "pl-pl", "sk-sk"};
      if (StartsWith(normalized_alias, kLanguagePrefix)) {
        const std::string_view asset_tail =
            GetLanguageAssetTail(normalized_alias);
        if (!asset_tail.empty()) {
          if (IsLanguageVideoAssetTail(asset_tail)) {
            continue;
          }
          auto add_language_tail = [&](std::string_view tail) {
            for (const std::string_view locale : kKnownLanguageLocales) {
              add_alias_to_load(std::string(kLanguagePrefix) +
                                std::string(locale) + "/" +
                                std::string(tail));
            }
          };
          add_language_tail(asset_tail);
          constexpr std::string_view kPatchTextPrefix = "patch_text/";
          constexpr std::string_view kTextPrefix = "text/";
          if (StartsWith(asset_tail, kPatchTextPrefix)) {
            add_language_tail(std::string(kTextPrefix) +
                              std::string(
                                  asset_tail.substr(kPatchTextPrefix.size())));
          } else if (StartsWith(asset_tail, kTextPrefix)) {
            add_language_tail(std::string(kPatchTextPrefix) +
                              std::string(
                                  asset_tail.substr(kTextPrefix.size())));
          }
        }
      }
    }
  }

  if (aliases_to_load.empty()) {
    return;
  }

  std::unordered_set<std::string> alias_set;
  alias_set.reserve(aliases_to_load.size());
  std::unordered_set<std::string> language_tails_to_prime;
  bool prime_language_family = false;
  bool aliases_are_language_only = true;
  for (const std::string& alias : aliases_to_load) {
    alias_set.insert(alias);
    if (!StartsWith(alias, "language/")) {
      aliases_are_language_only = false;
    } else {
      prime_language_family = true;
      const std::string_view asset_tail_view = GetLanguageAssetTail(alias);
      if (!asset_tail_view.empty()) {
        const std::string asset_tail(asset_tail_view);
        language_tails_to_prime.insert(asset_tail);
      }
    }
  }

  const auto started = std::chrono::steady_clock::now();
  RefiiVfsDatabaseAliasLoadSummary load_summary;

  std::unordered_map<std::string, std::vector<std::string>> loaded_candidates;
  loaded_candidates.reserve(alias_set.size());
  if (prime_language_family && aliases_are_language_only) {
    load_summary = EnsureRefiiVfsDatabaseAliasRowsLoaded(loose_root, root_key);
    std::lock_guard<std::mutex> lock(
        RefiiVfsDatabaseAliasCandidateCacheMutex());
    const auto& cache = RefiiVfsDatabaseAliasCandidateCacheInstance();
    if (cache.root_key == root_key) {
      for (const RefiiVfsDatabaseAliasRow& row : cache.database_rows) {
        const std::string& row_alias =
            row.alias.empty() && row.candidate_is_language ? row.candidate
                                                           : row.alias;
        if (!row_alias.empty() && alias_set.find(row_alias) != alias_set.end()) {
          AppendUniqueDatabaseCandidate(&loaded_candidates[row_alias],
                                        row.candidate);
        }

        if (!row.candidate_is_language || !prime_language_family) {
          continue;
        }

        constexpr std::string_view kLanguagePrefix = "language/";
        const std::string_view language_tail =
            std::string_view(row.candidate).substr(kLanguagePrefix.size());
        const size_t locale_end = language_tail.find('/');
        if (locale_end == std::string_view::npos ||
            locale_end + 1 >= language_tail.size()) {
          continue;
        }

        const std::string_view asset_tail =
            language_tail.substr(locale_end + 1);
        const bool should_preload_language_tail =
            language_tails_to_prime.find(std::string(asset_tail)) !=
                language_tails_to_prime.end();
        if (!should_preload_language_tail) {
          continue;
        }

        AppendUniqueDatabaseCandidate(&loaded_candidates[row.candidate],
                                      row.candidate);

        constexpr std::string_view kEnglishLocale = "en-uk";
        constexpr std::string_view kKnownLanguageLocales[] = {
            "en-uk", "hu-hu", "zh-cht", "cs-cz",
            "ko-kr", "pl-pl", "sk-sk"};
        if (locale_end == kEnglishLocale.size() &&
            language_tail.substr(0, locale_end) == kEnglishLocale &&
            locale_end + 1 < language_tail.size() &&
            !IsLanguageVideoAssetTail(asset_tail)) {
          for (const std::string_view locale : kKnownLanguageLocales) {
            AppendUniqueDatabaseCandidate(
                &loaded_candidates[std::string(kLanguagePrefix) +
                                   std::string(locale) + "/" +
                                   std::string(asset_tail)],
                row.candidate);
          }
        }
      }
    }
  } else {
    for (const std::filesystem::path& root :
         GetRefiiVfsMetadataSearchRoots(loose_root)) {
      if (!RefiiVfsLegacyDatabaseExists(root)) {
        continue;
      }
      std::ifstream database(RefiiVfsDatabasePath(root));
      std::string line;
      while (std::getline(database, line)) {
        if (!line.empty() && line.back() == '\r') {
          line.pop_back();
        }
        if (line.empty() || line[0] == '#') {
          continue;
        }

        const size_t first_tab = line.find('\t');
        if (first_tab == std::string::npos) {
          continue;
        }
        const size_t second_tab = line.find('\t', first_tab + 1);
        if (second_tab == std::string::npos) {
          continue;
        }

        const size_t third_tab = line.find('\t', second_tab + 1);
        const std::string_view row_alias =
            std::string_view(line).substr(first_tab + 1,
                                          second_tab - first_tab - 1);
        if (row_alias.empty()) {
          continue;
        }
        const std::string normalized_alias = NormalizeLooseAssetKey(row_alias);
        if (alias_set.find(normalized_alias) == alias_set.end()) {
          continue;
        }

        const std::string_view candidate =
            std::string_view(line).substr(second_tab + 1,
                                          third_tab == std::string::npos
                                              ? std::string::npos
                                              : third_tab - second_tab - 1);
        AppendUniqueDatabaseCandidate(&loaded_candidates[normalized_alias],
                                      candidate);
      }
    }
  }

  size_t hit_aliases = 0;
  size_t loaded_candidates_count = 0;
  {
    std::lock_guard<std::mutex> lock(
        RefiiVfsDatabaseAliasCandidateCacheMutex());
    auto& cache = RefiiVfsDatabaseAliasCandidateCacheInstance();
    if (cache.root_key != root_key) {
      cache = RefiiVfsDatabaseAliasCandidateCache{};
      cache.root_key = root_key;
    }
    for (const std::string& alias : aliases_to_load) {
      std::vector<std::string> candidates;
      if (auto match = loaded_candidates.find(alias);
          match != loaded_candidates.end()) {
        candidates = std::move(match->second);
        if (!candidates.empty()) {
          ++hit_aliases;
          loaded_candidates_count += candidates.size();
        }
      }
      cache.candidates_by_alias.emplace(alias, std::move(candidates));
      cache.known_aliases.insert(alias);
    }
  }

  if (VfsTracingEnabled() &&
      ShouldLogRepeatedVfsTraceOperation("VfsDatabaseAliasPrime", 32)) {
    const auto elapsed_ms =
        std::chrono::duration_cast<std::chrono::milliseconds>(
            std::chrono::steady_clock::now() - started)
            .count();
    REXLOG_INFO("Refii VFS database alias prime aliases={} hits={} "
                "candidates={} db_loaded={} db_rows={} elapsed_ms={} "
                "sample=[{}]",
                aliases_to_load.size(),
                hit_aliases,
                loaded_candidates_count,
                load_summary.loaded_now,
                load_summary.rows,
                elapsed_ms,
                FormatCandidateSample(aliases_to_load, 4));
  }
}

std::vector<std::string> LookupRefiiVfsDatabaseHashCandidates(
    uint32_t resource_hash,
    const std::filesystem::path& loose_root) {
  if (resource_hash == 0 || !AnyRefiiVfsDatabaseExists(loose_root)) {
    return {};
  }

  struct Cache {
    std::string root_key;
    std::unordered_map<uint32_t, std::vector<std::string>> candidates_by_hash;
  };
  static std::mutex mutex;
  static Cache cache;

  const std::string root_key = loose_root.lexically_normal().string();
  {
    std::lock_guard<std::mutex> lock(mutex);
    if (cache.root_key != root_key) {
      cache = Cache{};
      cache.root_key = root_key;
    }
    const auto cached = cache.candidates_by_hash.find(resource_hash);
    if (cached != cache.candidates_by_hash.end()) {
      return cached->second;
    }
  }

  std::vector<std::string> candidates;
  if (AnyRefiiVfsRuntimeHashIndexExists(loose_root)) {
    const uint8_t shard = RefiiVfsRuntimeShardForHash(resource_hash);
    for (const std::filesystem::path& root :
         GetRefiiVfsMetadataSearchRoots(loose_root)) {
      if (!RefiiVfsRuntimeHashIndexExists(root)) {
        continue;
      }
      for (std::string& candidate :
           LookupRefiiVfsRuntimeHashShardCandidates(root, shard, resource_hash)) {
        AddUnique(candidates, std::move(candidate));
      }
    }
  } else {
    for (const std::filesystem::path& root :
         GetRefiiVfsMetadataSearchRoots(loose_root)) {
      if (!RefiiVfsLegacyDatabaseExists(root)) {
        continue;
      }
      std::ifstream database(RefiiVfsDatabasePath(root));
      std::string line;
      while (std::getline(database, line)) {
        if (!line.empty() && line.back() == '\r') {
          line.pop_back();
        }
        if (line.empty() || line[0] == '#') {
          continue;
        }

        const size_t first_tab = line.find('\t');
        if (first_tab == std::string::npos || first_tab == 0) {
          continue;
        }

        uint32_t parsed_hash = 0;
        if (!ParseHexUint32(std::string_view(line).substr(0, first_tab),
                            &parsed_hash) ||
            parsed_hash != resource_hash) {
          continue;
        }

        const size_t second_tab = line.find('\t', first_tab + 1);
        const size_t candidate_start =
            second_tab == std::string::npos ? first_tab + 1 : second_tab + 1;
        const size_t third_tab = line.find('\t', candidate_start);
        const std::string_view candidate =
            std::string_view(line).substr(candidate_start,
                                          third_tab == std::string::npos
                                              ? std::string::npos
                                              : third_tab - candidate_start);
        AppendUniqueDatabaseCandidate(&candidates, candidate);
      }
    }
  }

  {
    std::lock_guard<std::mutex> lock(mutex);
    if (cache.root_key != root_key) {
      cache = Cache{};
      cache.root_key = root_key;
    }
    auto [iterator, inserted] =
        cache.candidates_by_hash.emplace(resource_hash, candidates);
    (void)inserted;
    return iterator->second;
  }
}

bool LoadRefiiVfsRuntimeHashOwnershipTable(
    const std::filesystem::path& loose_root,
    std::unordered_set<uint32_t>* out_owned_hashes) {
  if (out_owned_hashes == nullptr) {
    return false;
  }

  const std::filesystem::path table_path =
      RefiiVfsRuntimeHashOwnershipTablePath(loose_root);
  std::ifstream input(table_path);
  if (!input) {
    return false;
  }

  std::unordered_set<uint32_t> owned_hashes;
  std::string line;
  while (std::getline(input, line)) {
    if (!line.empty() && line.back() == '\r') {
      line.pop_back();
    }
    if (line.empty() || line[0] == '#') {
      continue;
    }

    uint32_t hash = 0;
    if (ParseHexUint32(line, &hash) && hash != 0) {
      owned_hashes.insert(hash);
    }
  }

  *out_owned_hashes = std::move(owned_hashes);
  return true;
}

bool LoadRefiiVfsRuntimeHashOwnershipTableForSearchRoots(
    const std::filesystem::path& loose_root,
    std::unordered_set<uint32_t>* out_owned_hashes) {
  if (out_owned_hashes == nullptr) {
    return false;
  }

  bool loaded_any = false;
  std::unordered_set<uint32_t> owned_hashes;
  for (const std::filesystem::path& root :
       GetRefiiVfsMetadataSearchRoots(loose_root)) {
    std::unordered_set<uint32_t> root_hashes;
    if (!LoadRefiiVfsRuntimeHashOwnershipTable(root, &root_hashes)) {
      continue;
    }
    loaded_any = true;
    owned_hashes.insert(root_hashes.begin(), root_hashes.end());
  }
  if (!loaded_any) {
    return false;
  }

  *out_owned_hashes = std::move(owned_hashes);
  return true;
}

const std::unordered_set<uint32_t>* GetRefiiVfsRuntimeOwnedHashes(
    const std::filesystem::path& loose_root) {
  const std::string root_key = loose_root.lexically_normal().string();
  if (root_key.empty()) {
    return nullptr;
  }

  std::lock_guard<std::mutex> lock(RefiiVfsRuntimeHashOwnershipCacheMutex());
  auto& cache = RefiiVfsRuntimeHashOwnershipCacheInstance();
  if (cache.root_key != root_key) {
    cache = RefiiVfsRuntimeHashOwnershipCache{};
    cache.root_key = root_key;
  }
  if (!cache.loaded) {
    cache.exists = LoadRefiiVfsRuntimeHashOwnershipTableForSearchRoots(
        loose_root, &cache.owned_hashes);
    cache.loaded = true;
    if (cache.exists && VfsTracingEnabled()) {
      REXLOG_INFO("Refii VFS hash ownership table loaded root={} hashes={}",
                  root_key,
                  cache.owned_hashes.size());
    }
  }
  return cache.exists ? &cache.owned_hashes : nullptr;
}

std::vector<std::string> LookupRefiiVfsDatabaseAliasCandidates(
    std::string_view asset_path,
    const std::filesystem::path& loose_root) {
  if (asset_path.empty() || !AnyRefiiVfsDatabaseExists(loose_root)) {
    return {};
  }

  const std::vector<std::string> aliases =
      BuildSyntheticVfsEntryCandidates(asset_path);
  if (aliases.empty()) {
    return {};
  }
  const std::vector<std::string> lookup_aliases =
      BuildRefiiVfsDatabasePrimeAliases(aliases);

  const std::string root_key = loose_root.lexically_normal().string();
  PrimeRefiiVfsDatabaseAliasCandidates(loose_root, lookup_aliases);

  std::vector<std::string> candidates;
  {
    std::lock_guard<std::mutex> lock(
        RefiiVfsDatabaseAliasCandidateCacheMutex());
    const auto& cache = RefiiVfsDatabaseAliasCandidateCacheInstance();
    if (cache.root_key != root_key) {
      return {};
    }
    for (const std::string& alias : lookup_aliases) {
      const std::string normalized_alias = NormalizeLooseAssetKey(alias);
      const auto match = cache.candidates_by_alias.find(normalized_alias);
      if (match == cache.candidates_by_alias.end()) {
        continue;
      }
      for (const std::string& candidate : match->second) {
        AddUnique(candidates, candidate);
      }
    }
  }
  std::stable_sort(candidates.begin(),
                   candidates.end(),
                   [&](const std::string& lhs, const std::string& rhs) {
                     return ScoreVfsDatabaseAliasCandidate(lhs, aliases) <
                            ScoreVfsDatabaseAliasCandidate(rhs, aliases);
                   });
  return candidates;
}


}  // namespace detail
}  // namespace refii::vfs
