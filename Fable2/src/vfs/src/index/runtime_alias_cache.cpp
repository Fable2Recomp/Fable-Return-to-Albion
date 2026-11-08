#include "vfs_hooks_private.h"

#include "core/installed_cultures.h"

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
      if (StartsWith(normalized_alias, kLanguagePrefix)) {
        const std::string_view asset_tail =
            GetLanguageAssetTail(normalized_alias);
        if (!asset_tail.empty()) {
          if (IsLanguageVideoAssetTail(asset_tail)) {
            continue;
          }
          const std::vector<std::string> installed_cultures =
              InstalledLanguageCultures();
          auto add_language_tail = [&](std::string_view tail) {
            for (const std::string& locale : installed_cultures) {
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

        // The fallback culture's row answers for every installed culture's spelling, as the
        // guest's second remap does (`sub_823728E0`, 0x82372AC4).
        if (locale_end == kFallbackCulture.size() &&
            language_tail.substr(0, locale_end) == kFallbackCulture &&
            locale_end + 1 < language_tail.size() &&
            !IsLanguageVideoAssetTail(asset_tail)) {
          for (const std::string& locale : InstalledLanguageCultures()) {
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
