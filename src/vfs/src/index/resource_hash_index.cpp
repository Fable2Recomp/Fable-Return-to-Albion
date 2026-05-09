#include "vfs_hooks_private.h"

namespace refii::vfs {
inline namespace detail {

bool LoadRefiiVfsDatabase(RefiiResourceHashIndex& index,
                          const std::filesystem::path& loose_root) {
  if (RefiiVfsRuntimeAliasIndexExists(loose_root) ||
      RefiiVfsRuntimeHashIndexExists(loose_root)) {
    const std::filesystem::path alias_root =
        RefiiVfsRuntimeAliasDirectory(loose_root);
    const std::filesystem::path hash_root =
        RefiiVfsRuntimeHashDirectory(loose_root);
    size_t loaded = 0;

    std::error_code error;
    if (std::filesystem::is_directory(hash_root, error)) {
      for (std::filesystem::directory_iterator iterator(hash_root, error), end;
           !error && iterator != end;
           iterator.increment(error)) {
        const std::filesystem::directory_entry& entry = *iterator;
        std::error_code status_error;
        if (!entry.is_regular_file(status_error) ||
            entry.path().extension() != ".tsv") {
          continue;
        }
        std::ifstream shard_file(entry.path());
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
                              &parsed_hash)) {
            continue;
          }
          const size_t before = index.filehash_aliases;
          AddRefiiExplicitResourceHashCandidate(
              index,
              parsed_hash,
              ResolveRefiiVfsRuntimeCandidateField(
                  loose_root, std::string_view(line).substr(tab + 1)));
          if (index.filehash_aliases != before) {
            ++loaded;
          }
        }
      }
    }

    error.clear();
    if (std::filesystem::is_directory(alias_root, error)) {
      for (std::filesystem::directory_iterator iterator(alias_root, error), end;
           !error && iterator != end;
           iterator.increment(error)) {
        const std::filesystem::directory_entry& entry = *iterator;
        std::error_code status_error;
        if (!entry.is_regular_file(status_error) ||
            entry.path().extension() != ".tsv") {
          continue;
        }
        std::ifstream shard_file(entry.path());
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
          const size_t alias_before = index.database_aliases;
          AddRefiiDatabaseAliasCandidate(
              index,
              std::string_view(line).substr(0, tab),
              ResolveRefiiVfsRuntimeCandidateField(
                  loose_root, std::string_view(line).substr(tab + 1)));
          if (index.database_aliases != alias_before) {
            ++loaded;
          }
        }
      }
    }

    if (VfsTracingEnabled()) {
      REXLOG_INFO("Refii VFS runtime index loaded root={} entries={} hashes={} "
                  "aliases={}",
                  RefiiVfsRuntimeIndexDirectory(loose_root).string(),
                  loaded,
                  index.candidates_by_hash.size(),
                  index.candidates_by_alias.size());
    }
    return loaded != 0;
  }

  const std::filesystem::path database_path = RefiiVfsDatabasePath(loose_root);
  std::ifstream database(database_path);
  if (!database) {
    return false;
  }

  index.candidates_by_hash.reserve(300000);
  index.candidates_by_alias.reserve(200000);

  size_t loaded = 0;
  std::string line;
  while (std::getline(database, line)) {
    if (!line.empty() && line.back() == '\r') {
      line.pop_back();
    }
    if (line.empty() || line[0] == '#') {
      continue;
    }

    std::array<std::string_view, 4> columns{};
    size_t column_count = 0;
    size_t start = 0;
    while (column_count < columns.size()) {
      const size_t tab = line.find('\t', start);
      columns[column_count++] =
          std::string_view(line).substr(start, tab == std::string::npos
                                                   ? std::string::npos
                                                   : tab - start);
      if (tab == std::string::npos) {
        break;
      }
      start = tab + 1;
    }
    if (column_count < 2 || columns[0].empty()) {
      continue;
    }

    uint32_t parsed_hash = 0;
    if (!ParseHexUint32(columns[0], &parsed_hash)) {
      continue;
    }

    const std::string_view alias =
        column_count >= 3 ? columns[1] : columns[1];
    const std::string_view candidate =
        column_count >= 3 ? columns[2] : columns[1];
    if (candidate.empty()) {
      continue;
    }

    const size_t before = index.filehash_aliases;
    AddRefiiExplicitResourceHashCandidate(
        index, parsed_hash, candidate);
    const size_t alias_before = index.database_aliases;
    AddRefiiDatabaseAliasCandidate(
        index, alias.empty() ? candidate : alias, candidate);
    if (index.filehash_aliases != before) {
      ++loaded;
    } else if (index.database_aliases != alias_before) {
      ++loaded;
    }
  }

  if (VfsTracingEnabled()) {
    REXLOG_INFO("Refii VFS database loaded path={} entries={} hashes={} "
                "aliases={}",
                database_path.string(),
                loaded,
                index.candidates_by_hash.size(),
                index.candidates_by_alias.size());
  }
  return loaded != 0;
}

std::mutex& RefiiResourceHashIndexMutex() {
  static std::mutex mutex;
  return mutex;
}

std::shared_ptr<const RefiiResourceHashIndex>& CachedRefiiResourceHashIndex() {
  static std::shared_ptr<const RefiiResourceHashIndex> index;
  return index;
}

std::mutex& RuntimeResourceHashNameMutex() {
  static std::mutex mutex;
  return mutex;
}

std::unordered_map<uint32_t, std::vector<std::string>>&
RuntimeResourceHashNames() {
  static std::unordered_map<uint32_t, std::vector<std::string>> names;
  return names;
}

void RecordRuntimeResourceHashName(uint32_t resource_hash,
                                   std::string_view name) {
  if (resource_hash == 0 || name.empty()) {
    return;
  }

  std::lock_guard<std::mutex> lock(RuntimeResourceHashNameMutex());
  auto& names = RuntimeResourceHashNames()[resource_hash];
  const std::string normalized = NormalizeLooseAssetKey(name);
  const auto duplicate =
      std::find_if(names.begin(), names.end(), [&normalized](const std::string& existing) {
        return NormalizeLooseAssetKey(existing) == normalized;
      });
  if (duplicate == names.end()) {
    names.emplace_back(name);
  }
}

std::vector<std::string> GetRuntimeResourceHashNames(uint32_t resource_hash) {
  std::lock_guard<std::mutex> lock(RuntimeResourceHashNameMutex());
  const auto match = RuntimeResourceHashNames().find(resource_hash);
  if (match == RuntimeResourceHashNames().end()) {
    return {};
  }
  return match->second;
}

std::unordered_map<uint32_t, std::vector<std::string>>
SnapshotRuntimeResourceHashNames() {
  std::lock_guard<std::mutex> lock(RuntimeResourceHashNameMutex());
  return RuntimeResourceHashNames();
}

bool ShouldPassThroughNativeResourceHash(uint32_t resource_hash,
                                         const std::vector<std::string>& names) {
  if (resource_hash == 0 || names.empty()) {
    return false;
  }

  for (const std::string& name : names) {
    if (IsLuaScriptAssetKey(name) &&
        ResolveRefiiLuaScriptAsset(name, nullptr, nullptr)) {
      return false;
    }
  }

  return std::all_of(names.begin(), names.end(), [](const std::string& name) {
    return (IsLuaScriptAssetKey(name) && !LuaVfsBridgeEnabled()) ||
           !GetLooseNativeAssetSkipReason(name).empty();
  });
}

void TraceLuaVfsBridgeResolution(std::string_view context,
                                 uint32_t resource_hash,
                                 std::string_view script_key) {
  if (!LuaVfsBridgeEnabled()) {
    return;
  }
  if (!VfsTracingEnabled() && !LooseAssetOverrideTracingEnabled()) {
    return;
  }
  if (script_key.empty() || !IsLuaScriptAssetKey(script_key) ||
      !ShouldLogRepeatedVfsTraceOperation("LuaVfsBridge", 32)) {
    return;
  }

  LooseAssetResolution decompiled_resolution;
  std::string decompiled_candidate;
  const bool has_decompiled_override =
      ResolveLuaVfsOverride(
          script_key, &decompiled_resolution, &decompiled_candidate);

  LooseAssetResolution carrier_resolution;
  std::string carrier_candidate;
  const bool has_vfs_carrier =
      ResolveExtractedLuaSourceAsset(
          script_key, &carrier_resolution, &carrier_candidate);

  if (has_decompiled_override) {
    REXLOG_INFO("Refii VFS Lua bridge decompiled hit context={} hash=0x{:08X} "
                "script={} source=lua_decompiled_override decompiled={} "
                "extracted={} extracted_host={} host={}",
                context,
                resource_hash,
                decompiled_candidate,
                decompiled_resolution.provider_relative_path,
                has_vfs_carrier ? carrier_resolution.provider_relative_path : "",
                has_vfs_carrier ? carrier_resolution.host_path.string() : "",
                decompiled_resolution.host_path.string());
    return;
  }

  if (has_vfs_carrier) {
    REXLOG_INFO("Refii VFS Lua bridge extracted-source context={} hash=0x{:08X} "
                "script={} source=lua_extracted_source extracted={} host={} "
                "reason=no_decompiled_lua_override",
                context,
                resource_hash,
                script_key,
                carrier_resolution.provider_relative_path,
                carrier_resolution.host_path.string());
    return;
  }

  REXLOG_INFO("Refii VFS Lua bridge pass-through context={} hash=0x{:08X} "
              "script={} reason=no_lua_extracted_source_or_decompiled_override",
              context,
              resource_hash,
              script_key);
}

void TraceLuaVfsBridgeResolution(std::string_view context,
                                 uint32_t resource_hash,
                                 const std::vector<std::string>& names) {
  for (const std::string& name : names) {
    if (IsLuaScriptAssetKey(name)) {
      TraceLuaVfsBridgeResolution(context, resource_hash, name);
    }
  }
}

std::shared_ptr<const SyntheticVfsEntryIndex> GetSyntheticVfsEntryIndex() {
  const std::vector<std::shared_ptr<const RefiiSyntheticBankTemplate>> templates =
      GetSyntheticBankTemplateSnapshot();
  const std::filesystem::path loose_root = GetLooseAssetRoot();
  const std::string root_key = loose_root.lexically_normal().string();
  const std::vector<RefiiVfsRuntimeBankRecord> runtime_bank_records =
      GetRefiiVfsRuntimeBankRecordsCached(loose_root);

  {
    std::lock_guard<std::mutex> lock(SyntheticVfsEntryIndexMutex());
    const auto& cached = CachedSyntheticVfsEntryIndex();
    if (cached != nullptr && cached->root_key == root_key &&
        cached->template_count == templates.size() &&
        cached->runtime_bank_record_count == runtime_bank_records.size()) {
      return cached;
    }
  }

  auto index = std::make_shared<SyntheticVfsEntryIndex>();
  index->root_key = root_key;
  index->template_count = templates.size();
  index->runtime_bank_record_count = runtime_bank_records.size();
  std::unordered_set<std::string> indexed_manifest_roots;

  const auto index_manifest = [&](const std::filesystem::path& manifest_root,
                                  const std::filesystem::path& manifest_path) {
    const std::string normalized_root =
        NormalizeLooseAssetKey(manifest_root.generic_string());
    if (normalized_root.empty() ||
        !indexed_manifest_roots.insert(normalized_root).second) {
      return;
    }
    IndexSyntheticManifestEntries(*index, manifest_root, manifest_path);
  };

  for (const auto& bank_template : templates) {
    if (bank_template == nullptr) {
      continue;
    }

    index_manifest(bank_template->manifest_root, bank_template->manifest_path);
  }

  for (const RefiiVfsRuntimeBankRecord& record : runtime_bank_records) {
    const std::filesystem::path manifest_root(record.manifest_root);
    const std::filesystem::path manifest_path =
        loose_root / manifest_root / "_manifest.tsv";
    index_manifest(manifest_root, manifest_path);
  }

  {
    std::lock_guard<std::mutex> lock(SyntheticVfsEntryIndexMutex());
    auto& cached = CachedSyntheticVfsEntryIndex();
    if (cached != nullptr && cached->root_key == root_key &&
        cached->template_count == templates.size() &&
        cached->runtime_bank_record_count == runtime_bank_records.size()) {
      return cached;
    }
    cached = index;
  }

  if (VfsTracingEnabled() ||
      ShouldLogLooseStatsMilestone(indexed_manifest_roots.size())) {
    REXLOG_INFO("Refii synthetic VFS entry index rebuilt templates={} "
                "runtime_banks={} indexed_manifests={} indexed_paths={} "
                "manifest_entries={} manifest_tables={}",
                templates.size(),
                runtime_bank_records.size(),
                indexed_manifest_roots.size(),
                index->by_normalized_path.size(),
                index->manifest_entries,
                index->manifest_tables);
  }

  return index;
}

std::shared_ptr<const RefiiResourceHashIndex> GetRefiiResourceHashIndex() {
  const std::filesystem::path loose_root = GetLooseAssetRoot();
  if (loose_root.empty()) {
    return {};
  }

  const std::string root_key = loose_root.lexically_normal().string();
  const size_t synthetic_template_count =
      GetSyntheticBankTemplateSnapshot().size();

  {
    std::lock_guard<std::mutex> lock(RefiiResourceHashIndexMutex());
    const auto& cached = CachedRefiiResourceHashIndex();
    if (cached != nullptr && cached->root_key == root_key &&
        (cached->database_loaded ||
         cached->synthetic_template_count == synthetic_template_count)) {
      return cached;
    }
  }

  auto index = std::make_shared<RefiiResourceHashIndex>();
  index->root_key = root_key;
  index->synthetic_template_count = synthetic_template_count;

  const bool loaded_database = LoadRefiiVfsDatabase(*index, loose_root);
  index->database_loaded = loaded_database;
  size_t filehash_table_count = 0;

  if (!loaded_database) {
    const auto synthetic_index = GetSyntheticVfsEntryIndex();
    std::unordered_map<std::string, std::vector<std::string>> basename_candidates;
    std::vector<std::filesystem::path> filehash_csv_paths;

    std::error_code error;
    for (std::filesystem::recursive_directory_iterator iterator(
             loose_root,
             std::filesystem::directory_options::skip_permission_denied,
             error),
         end;
         !error && iterator != end;
         iterator.increment(error)) {
      const std::filesystem::directory_entry& entry = *iterator;
      std::error_code status_error;
      if (!entry.is_regular_file(status_error)) {
        continue;
      }

      const std::filesystem::path relative_path =
          entry.path().lexically_relative(loose_root);
      const std::string relative_key = relative_path.generic_string();
      AddRefiiResourceHashCandidate(*index, relative_key);
      AddRefiiBasenameCandidate(basename_candidates, relative_key);

      std::string filename = entry.path().filename().generic_string();
      std::ranges::transform(filename, filename.begin(), [](unsigned char ch) {
        return static_cast<char>(std::tolower(ch));
      });
      if (filename == "filehash.csv") {
        filehash_csv_paths.push_back(entry.path());
      }
    }

    if (synthetic_index != nullptr) {
      for (const auto& [alias, entry] : synthetic_index->by_normalized_path) {
        (void)entry;
        AddRefiiResourceHashCandidate(*index, alias);
        AddRefiiBasenameCandidate(basename_candidates, alias);
      }
    }

    filehash_table_count = filehash_csv_paths.size();
    AddRefiiFileHashCsvMappings(
        *index, loose_root, filehash_csv_paths, basename_candidates);
  }

  {
    std::lock_guard<std::mutex> lock(RefiiResourceHashIndexMutex());
    auto& cached = CachedRefiiResourceHashIndex();
    if (cached != nullptr && cached->root_key == root_key &&
        (cached->database_loaded ||
         cached->synthetic_template_count == synthetic_template_count)) {
      return cached;
    }
    cached = index;
  }

  if (VfsTracingEnabled()) {
    REXLOG_INFO("Refii resource hash index rebuilt root={} paths={} "
                "aliases={} filehash_aliases={} database_aliases={} hashes={} "
                "path_aliases={} synthetic_templates={} database={} "
                "filehash_tables={}",
                root_key,
                index->indexed_paths,
                index->hash_aliases,
                index->filehash_aliases,
                index->database_aliases,
                index->candidates_by_hash.size(),
                index->candidates_by_alias.size(),
                synthetic_template_count,
                loaded_database ? "loaded" : "fallback_scan",
                filehash_table_count);
  }

  return index;
}


}  // namespace detail
}  // namespace refii::vfs
