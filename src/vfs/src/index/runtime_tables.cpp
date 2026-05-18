#include "vfs_hooks_private.h"

namespace refii::vfs {
inline namespace detail {

std::filesystem::path RefiiVfsDatabasePath(
    const std::filesystem::path& loose_root) {
  return loose_root / "_refii_vfs.tsv";
}

std::filesystem::path RefiiVfsRuntimeIndexDirectory(
    const std::filesystem::path& loose_root) {
  return loose_root / "refii_vfs";
}

std::filesystem::path RefiiVfsRuntimeAliasDirectory(
    const std::filesystem::path& loose_root) {
  return RefiiVfsRuntimeIndexDirectory(loose_root) / "aliases";
}

std::filesystem::path RefiiVfsRuntimeHashDirectory(
    const std::filesystem::path& loose_root) {
  return RefiiVfsRuntimeIndexDirectory(loose_root) / "hashes";
}

std::string RefiiVfsRuntimeShardName(uint8_t shard) {
  return fmt::format("{:02x}.tsv", static_cast<unsigned>(shard));
}

std::string RefiiVfsRuntimeAliasShardName(uint16_t shard) {
  return fmt::format("{:03x}.tsv", static_cast<unsigned>(shard & 0x0FFFu));
}

uint8_t RefiiVfsRuntimeShardForHash(uint32_t hash) {
  return static_cast<uint8_t>((hash >> 24) & 0xFFu);
}

uint16_t RefiiVfsRuntimeAliasShardForHash(uint32_t hash) {
  return static_cast<uint16_t>((hash >> 20) & 0xFFFu);
}

std::filesystem::path RefiiVfsRuntimeAliasShardPath(
    const std::filesystem::path& loose_root,
    uint16_t shard) {
  return RefiiVfsRuntimeAliasDirectory(loose_root) /
         RefiiVfsRuntimeAliasShardName(shard);
}

std::filesystem::path RefiiVfsRuntimeHashShardPath(
    const std::filesystem::path& loose_root,
    uint8_t shard) {
  return RefiiVfsRuntimeHashDirectory(loose_root) /
         RefiiVfsRuntimeShardName(shard);
}

struct RefiiVfsDatabasePresence {
  bool alias_index = false;
  bool hash_index = false;
  bool legacy_database = false;
};

RefiiVfsDatabasePresence GetRefiiVfsDatabasePresence(
    const std::filesystem::path& loose_root) {
  static std::mutex mutex;
  static std::unordered_map<std::string, RefiiVfsDatabasePresence> cache;

  const std::string root_key = loose_root.lexically_normal().string();
  if (root_key.empty()) {
    return {};
  }

  {
    std::lock_guard<std::mutex> lock(mutex);
    if (const auto cached = cache.find(root_key); cached != cache.end()) {
      return cached->second;
    }
  }

  std::error_code error;
  RefiiVfsDatabasePresence presence;
  presence.alias_index =
      std::filesystem::is_directory(RefiiVfsRuntimeAliasDirectory(loose_root),
                                    error);
  error.clear();
  presence.hash_index =
      std::filesystem::is_directory(RefiiVfsRuntimeHashDirectory(loose_root),
                                    error);
  error.clear();
  presence.legacy_database =
      std::filesystem::is_regular_file(RefiiVfsDatabasePath(loose_root), error);

  std::lock_guard<std::mutex> lock(mutex);
  return cache.emplace(root_key, presence).first->second;
}

bool RefiiVfsRuntimeAliasIndexExists(const std::filesystem::path& loose_root) {
  return GetRefiiVfsDatabasePresence(loose_root).alias_index;
}

bool RefiiVfsRuntimeHashIndexExists(const std::filesystem::path& loose_root) {
  return GetRefiiVfsDatabasePresence(loose_root).hash_index;
}

bool RefiiVfsLegacyDatabaseExists(const std::filesystem::path& loose_root) {
  return GetRefiiVfsDatabasePresence(loose_root).legacy_database;
}

bool RefiiVfsDatabaseExists(const std::filesystem::path& loose_root) {
  const RefiiVfsDatabasePresence presence =
      GetRefiiVfsDatabasePresence(loose_root);
  return presence.alias_index || presence.hash_index || presence.legacy_database;
}

void AppendUniqueDatabaseCandidate(std::vector<std::string>* candidates,
                                   std::string_view candidate) {
  if (candidates == nullptr || !ShouldIndexRefiiResourceHashCandidate(candidate)) {
    return;
  }

  const std::string normalized = NormalizeLooseAssetKey(candidate);
  if (normalized.empty() ||
      std::find(candidates->begin(), candidates->end(), normalized) !=
          candidates->end()) {
    return;
  }

  candidates->push_back(normalized);
}

struct RefiiVfsRuntimeCandidateTable {
  std::string root_key;
  std::vector<std::string> candidates;
  bool loaded = false;
};

struct RefiiVfsRuntimeHashShardCache {
  std::string root_key;
  std::unordered_set<uint8_t> loaded_shards;
  std::unordered_map<uint32_t, std::vector<std::string>> candidates_by_hash;
};

std::optional<TargetedRequestManagerHashContribution>
GetTargetedRequestManagerHashContribution(uint32_t resource_hash);

std::mutex& RefiiVfsDatabaseAliasCandidateCacheMutex() {
  static std::mutex mutex;
  return mutex;
}

RefiiVfsDatabaseAliasCandidateCache&
RefiiVfsDatabaseAliasCandidateCacheInstance() {
  static RefiiVfsDatabaseAliasCandidateCache cache;
  return cache;
}

std::mutex& RefiiVfsRuntimeCandidateTableMutex() {
  static std::mutex mutex;
  return mutex;
}

RefiiVfsRuntimeCandidateTable& RefiiVfsRuntimeCandidateTableInstance() {
  static RefiiVfsRuntimeCandidateTable table;
  return table;
}

std::mutex& RefiiFindDirectoryIndexMutex() {
  static std::mutex mutex;
  return mutex;
}

RefiiFindDirectoryIndex& RefiiFindDirectoryIndexInstance() {
  static RefiiFindDirectoryIndex index;
  return index;
}

std::mutex& RefiiFindDirectoryParentCacheMutex() {
  static std::mutex mutex;
  return mutex;
}

RefiiFindDirectoryParentCache& RefiiFindDirectoryParentCacheInstance() {
  static RefiiFindDirectoryParentCache cache;
  return cache;
}

std::mutex& RefiiVfsRuntimeAliasShardCacheMutex() {
  static std::mutex mutex;
  return mutex;
}

RefiiVfsRuntimeAliasShardCache& RefiiVfsRuntimeAliasShardCacheInstance() {
  static RefiiVfsRuntimeAliasShardCache cache;
  return cache;
}

std::mutex& RefiiVfsRuntimeHashShardCacheMutex() {
  static std::mutex mutex;
  return mutex;
}

RefiiVfsRuntimeHashShardCache& RefiiVfsRuntimeHashShardCacheInstance() {
  static RefiiVfsRuntimeHashShardCache cache;
  return cache;
}

std::mutex& RefiiVfsRuntimeBankTableCacheMutex() {
  static std::mutex mutex;
  return mutex;
}

RefiiVfsRuntimeBankTableCache& RefiiVfsRuntimeBankTableCacheInstance() {
  static RefiiVfsRuntimeBankTableCache cache;
  return cache;
}

std::mutex& RefiiVfsRuntimeLuaTableCacheMutex() {
  static std::mutex mutex;
  return mutex;
}

RefiiVfsRuntimeLuaTableCache& RefiiVfsRuntimeLuaTableCacheInstance() {
  static RefiiVfsRuntimeLuaTableCache cache;
  return cache;
}

std::mutex& RefiiVfsRuntimeHashOwnershipCacheMutex() {
  static std::mutex mutex;
  return mutex;
}

RefiiVfsRuntimeHashOwnershipCache&
RefiiVfsRuntimeHashOwnershipCacheInstance() {
  static RefiiVfsRuntimeHashOwnershipCache cache;
  return cache;
}

std::mutex& TargetedRequestManagerHashContributionMutex() {
  static std::mutex mutex;
  return mutex;
}

std::unordered_map<uint32_t, TargetedRequestManagerHashContribution>&
TargetedRequestManagerHashContributions() {
  static std::unordered_map<uint32_t, TargetedRequestManagerHashContribution>
      contributions;
  return contributions;
}

std::mutex& ResolvedSyntheticBankOpenAliasMutex() {
  static std::mutex mutex;
  return mutex;
}

std::unordered_map<std::string, ResolvedSyntheticBankOpenAlias>&
ResolvedSyntheticBankOpenAliases() {
  static std::unordered_map<std::string, ResolvedSyntheticBankOpenAlias> aliases;
  return aliases;
}

std::string& ResolvedSyntheticBankOpenAliasRootKey() {
  static std::string root_key;
  return root_key;
}

std::string& TargetedRequestManagerHashContributionRootKey() {
  static std::string root_key;
  return root_key;
}


bool ParseDecimalUint32(std::string_view text, uint32_t* out_value) {
  if (text.empty() || out_value == nullptr) {
    return false;
  }
  uint32_t value = 0;
  for (const char ch : text) {
    if (ch < '0' || ch > '9') {
      return false;
    }
    const uint32_t digit = static_cast<uint32_t>(ch - '0');
    if (value > (std::numeric_limits<uint32_t>::max() - digit) / 10) {
      return false;
    }
    value = value * 10 + digit;
  }
  *out_value = value;
  return true;
}

std::filesystem::path RefiiVfsRuntimeCandidateTablePath(
    const std::filesystem::path& loose_root) {
  return RefiiVfsRuntimeIndexDirectory(loose_root) / "candidates.tsv";
}

std::filesystem::path RefiiVfsRuntimeDirectoryTablePath(
    const std::filesystem::path& loose_root) {
  return RefiiVfsRuntimeIndexDirectory(loose_root) / "directories.tsv";
}

std::filesystem::path RefiiVfsRuntimeDirectoryEntryTablePath(
    const std::filesystem::path& loose_root) {
  return RefiiVfsRuntimeIndexDirectory(loose_root) / "directory_entries.tsv";
}

std::filesystem::path RefiiVfsRuntimeDirectoryEntryBinaryPath(
    const std::filesystem::path& loose_root) {
  return RefiiVfsRuntimeIndexDirectory(loose_root) / "directory_entries.bin";
}

std::filesystem::path RefiiVfsRuntimeBankTablePath(
    const std::filesystem::path& loose_root) {
  return RefiiVfsRuntimeIndexDirectory(loose_root) / "banks.tsv";
}

std::filesystem::path RefiiVfsRuntimeLuaTablePath(
    const std::filesystem::path& loose_root) {
  return RefiiVfsRuntimeIndexDirectory(loose_root) / "lua.tsv";
}

std::filesystem::path RefiiVfsRuntimeHashOwnershipTablePath(
    const std::filesystem::path& loose_root) {
  return RefiiVfsRuntimeIndexDirectory(loose_root) / "hash_ownership.tsv";
}

std::vector<std::string> LoadRefiiVfsRuntimeCandidates(
    const std::filesystem::path& loose_root) {
  std::vector<std::string> candidates;
  std::ifstream input(RefiiVfsRuntimeCandidateTablePath(loose_root));
  std::string line;
  while (std::getline(input, line)) {
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
    uint32_t id = 0;
    if (!ParseDecimalUint32(std::string_view(line).substr(0, tab), &id)) {
      continue;
    }
    if (id >= candidates.size()) {
      candidates.resize(static_cast<size_t>(id) + 1);
    }
    candidates[id] = NormalizeLooseAssetKey(std::string_view(line).substr(tab + 1));
  }
  return candidates;
}

std::vector<std::string> GetRefiiVfsRuntimeCandidatesCached(
    const std::filesystem::path& loose_root) {
  const std::string root_key = loose_root.lexically_normal().string();
  if (root_key.empty()) {
    return {};
  }

  {
    std::lock_guard<std::mutex> lock(RefiiVfsRuntimeCandidateTableMutex());
    const auto& table = RefiiVfsRuntimeCandidateTableInstance();
    if (table.root_key == root_key && table.loaded) {
      return table.candidates;
    }
  }

  std::vector<std::string> loaded_candidates =
      LoadRefiiVfsRuntimeCandidates(loose_root);
  if (VfsTracingEnabled() &&
      ShouldLogRepeatedVfsTraceOperation("VfsRuntimeCandidatesLoaded", 8)) {
    REXLOG_INFO("Refii VFS runtime candidate table loaded candidates={} root={}",
                loaded_candidates.size(),
                loose_root.string());
  }

  {
    std::lock_guard<std::mutex> lock(RefiiVfsRuntimeCandidateTableMutex());
    auto& table = RefiiVfsRuntimeCandidateTableInstance();
    if (table.root_key != root_key || !table.loaded) {
      table.root_key = root_key;
      table.candidates = std::move(loaded_candidates);
      table.loaded = true;
    }
    return table.candidates;
  }
}

std::string ResolveRefiiVfsRuntimeCandidateField(
    const std::filesystem::path& loose_root,
    std::string_view candidate_field) {
  uint32_t candidate_id = 0;
  if (!ParseDecimalUint32(candidate_field, &candidate_id)) {
    return NormalizeLooseAssetKey(candidate_field);
  }

  const std::string root_key = loose_root.lexically_normal().string();
  {
    std::lock_guard<std::mutex> lock(RefiiVfsRuntimeCandidateTableMutex());
    auto& table = RefiiVfsRuntimeCandidateTableInstance();
    if (table.root_key == root_key && table.loaded) {
      if (candidate_id < table.candidates.size()) {
        return table.candidates[candidate_id];
      }
      return {};
    }
  }

  const std::vector<std::string> candidates =
      GetRefiiVfsRuntimeCandidatesCached(loose_root);
  if (candidate_id < candidates.size()) {
    return candidates[candidate_id];
  }
  return {};
}

std::vector<RefiiVfsRuntimeBankRecord> LoadRefiiVfsRuntimeBankRecords(
    const std::filesystem::path& loose_root) {
  std::vector<RefiiVfsRuntimeBankRecord> records;
  std::ifstream input(RefiiVfsRuntimeBankTablePath(loose_root));
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

    RefiiVfsRuntimeBankRecord record;
    record.alias = NormalizeLooseAssetKey(std::string_view(line).substr(0, first_tab));
    record.candidate = NormalizeLooseAssetKey(
        std::string_view(line).substr(first_tab + 1, second_tab - first_tab - 1));
    record.manifest_root =
        NormalizeLooseAssetKey(std::string_view(line).substr(second_tab + 1));
    if (record.alias.empty() || record.candidate.empty() ||
        record.manifest_root.empty()) {
      continue;
    }
    records.push_back(std::move(record));
  }
  return records;
}

bool LookupRefiiVfsTypedBankManifest(std::string_view bank_name,
                                     std::filesystem::path* out_manifest_root,
                                     std::filesystem::path* out_manifest_path) {
  const std::filesystem::path loose_root = GetLooseAssetRoot();
  const std::string root_key = loose_root.lexically_normal().string();
  const std::string normalized_bank = NormalizeLooseAssetKey(bank_name);
  if (root_key.empty() || normalized_bank.empty()) {
    return false;
  }

  {
    std::lock_guard<std::mutex> lock(RefiiVfsRuntimeBankTableCacheMutex());
    auto& cache = RefiiVfsRuntimeBankTableCacheInstance();
    if (cache.root_key == root_key && cache.loaded) {
      const auto match = cache.records_by_alias.find(normalized_bank);
      if (match == cache.records_by_alias.end()) {
        return false;
      }
      for (const RefiiVfsRuntimeBankRecord& record : match->second) {
        const std::filesystem::path manifest_path =
            loose_root / record.manifest_root / "_manifest.tsv";
        std::error_code error;
        if (!std::filesystem::is_regular_file(manifest_path, error)) {
          continue;
        }
        if (out_manifest_root != nullptr) {
          *out_manifest_root = record.manifest_root;
        }
        if (out_manifest_path != nullptr) {
          *out_manifest_path = manifest_path.lexically_normal();
        }
        return true;
      }
      return false;
    }
  }

  std::vector<RefiiVfsRuntimeBankRecord> loaded_records =
      LoadRefiiVfsRuntimeBankRecords(loose_root);
  {
    std::lock_guard<std::mutex> lock(RefiiVfsRuntimeBankTableCacheMutex());
    auto& cache = RefiiVfsRuntimeBankTableCacheInstance();
    if (cache.root_key != root_key || !cache.loaded) {
      cache.root_key = root_key;
      cache.records = loaded_records;
      cache.records_by_alias.clear();
      for (const RefiiVfsRuntimeBankRecord& record : cache.records) {
        cache.records_by_alias[record.alias].push_back(record);
      }
      cache.loaded = true;
    }
  }

  return LookupRefiiVfsTypedBankManifest(
      normalized_bank, out_manifest_root, out_manifest_path);
}

std::vector<RefiiVfsRuntimeBankRecord> GetRefiiVfsRuntimeBankRecordsCached(
    const std::filesystem::path& loose_root) {
  const std::string root_key = loose_root.lexically_normal().string();
  if (root_key.empty()) {
    return {};
  }

  {
    std::lock_guard<std::mutex> lock(RefiiVfsRuntimeBankTableCacheMutex());
    const auto& cache = RefiiVfsRuntimeBankTableCacheInstance();
    if (cache.root_key == root_key && cache.loaded) {
      return cache.records;
    }
  }

  const std::vector<RefiiVfsRuntimeBankRecord> loaded_records =
      LoadRefiiVfsRuntimeBankRecords(loose_root);
  {
    std::lock_guard<std::mutex> lock(RefiiVfsRuntimeBankTableCacheMutex());
    auto& cache = RefiiVfsRuntimeBankTableCacheInstance();
    if (cache.root_key != root_key || !cache.loaded) {
      cache.root_key = root_key;
      cache.records = loaded_records;
      cache.records_by_alias.clear();
      for (const RefiiVfsRuntimeBankRecord& record : cache.records) {
        cache.records_by_alias[record.alias].push_back(record);
      }
      cache.loaded = true;
    }
    return cache.records;
  }
}

bool ResolveExactTypedBankManifestLooseAsset(
    std::string_view bank_name,
    LooseAssetResolution* out_resolution,
    std::string* out_candidate) {
  std::string normalized_bank = NormalizeLooseAssetKey(bank_name);
  const std::string mip0_redirect =
      CanonicalizeMip0TextureHeaderRedirect(normalized_bank);
  if (!mip0_redirect.empty()) {
    normalized_bank = mip0_redirect;
  }
  std::filesystem::path manifest_root;
  std::filesystem::path manifest_path;
  if (normalized_bank.empty() ||
      !LookupRefiiVfsTypedBankManifest(
          normalized_bank, &manifest_root, &manifest_path)) {
    return false;
  }

  if (out_resolution != nullptr) {
    out_resolution->host_path = manifest_path.lexically_normal();
    out_resolution->normalized_key = normalized_bank;
    out_resolution->provider_relative_path = normalized_bank;
  }
  if (out_candidate != nullptr) {
    *out_candidate = (manifest_root / "_manifest.tsv").generic_string();
  }
  return true;
}


}  // namespace detail
}  // namespace refii::vfs
