#include "vfs_hooks_private.h"

namespace refii::vfs {
inline namespace detail {

std::unordered_map<uint32_t, bool> BuildRefiiHashOwnershipCache(
    const std::unordered_map<uint32_t, std::vector<std::string>>& runtime_names) {
  std::unordered_map<uint32_t, bool> ownership;
  ownership.reserve(runtime_names.size());
  if (runtime_names.empty()) {
    return ownership;
  }

  const std::filesystem::path loose_root = GetLooseAssetRoot();
  const std::string root_key = loose_root.lexically_normal().string();
  const std::unordered_set<uint32_t>* owned_hash_table =
      GetRefiiVfsRuntimeOwnedHashes(loose_root);
  struct PersistentCache {
    std::string root_key;
    std::unordered_map<uint32_t, bool> owned_by_hash;
  };
  static std::mutex persistent_mutex;
  static PersistentCache persistent_cache;

  std::unordered_map<uint8_t, std::unordered_set<uint32_t>> hashes_by_shard;
  size_t cached_total = 0;
  {
    std::lock_guard<std::mutex> lock(persistent_mutex);
    if (persistent_cache.root_key != root_key) {
      persistent_cache = PersistentCache{};
      persistent_cache.root_key = root_key;
    }

    for (const auto& [resource_hash, names] : runtime_names) {
      if (resource_hash == 0) {
        continue;
      }
      if (const auto cached = persistent_cache.owned_by_hash.find(resource_hash);
          cached != persistent_cache.owned_by_hash.end()) {
        if (!cached->second) {
          if (const auto targeted =
                  GetTargetedRequestManagerHashContribution(resource_hash);
              targeted.has_value() &&
              RefiiDatabaseHashHasOwnedResolution(
                  resource_hash, targeted->name, nullptr)) {
            cached->second = true;
            ownership.emplace(resource_hash, true);
            continue;
          }
        }
        ownership.emplace(resource_hash, cached->second);
        continue;
      }
      if (!names.empty() &&
          ShouldPassThroughNativeResourceHash(resource_hash, names)) {
        ownership.emplace(resource_hash, false);
        persistent_cache.owned_by_hash.emplace(resource_hash, false);
        continue;
      }

      bool exact_typed_bank = false;
      for (const std::string& name : names) {
        if (ResolveExactTypedBankManifestLooseAsset(name, nullptr, nullptr)) {
          exact_typed_bank = true;
          break;
        }
      }
      if (exact_typed_bank) {
        ownership.emplace(resource_hash, true);
        persistent_cache.owned_by_hash.emplace(resource_hash, true);
        continue;
      }

      if (const auto targeted =
              GetTargetedRequestManagerHashContribution(resource_hash);
          targeted.has_value() &&
          RefiiDatabaseHashHasOwnedResolution(
              resource_hash, targeted->name, nullptr)) {
        ownership.emplace(resource_hash, true);
        persistent_cache.owned_by_hash.emplace(resource_hash, true);
        continue;
      }

      if (owned_hash_table != nullptr &&
          owned_hash_table->find(resource_hash) != owned_hash_table->end()) {
        ownership.emplace(resource_hash, true);
        persistent_cache.owned_by_hash.emplace(resource_hash, true);
        continue;
      }

      if (RefiiVfsRuntimeHashIndexExists(loose_root)) {
        hashes_by_shard[RefiiVfsRuntimeShardForHash(resource_hash)].insert(
            resource_hash);
      } else {
        ownership.emplace(resource_hash, false);
        persistent_cache.owned_by_hash.emplace(resource_hash, false);
      }
    }
  }

  size_t scanned_shards = 0;
  size_t matched_hashes = 0;
  for (const auto& [shard, hashes] : hashes_by_shard) {
    if (hashes.empty()) {
      continue;
    }
    ++scanned_shards;
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
          hashes.find(parsed_hash) == hashes.end() ||
          ownership.find(parsed_hash) != ownership.end()) {
        continue;
      }

      const std::string candidate =
          NormalizeLooseAssetKey(std::string_view(line).substr(tab + 1));
      const bool owned =
          IsRefiiHashCandidateOwnedByDatabase(loose_root, candidate);
      if (owned) {
        ownership.emplace(parsed_hash, true);
        ++matched_hashes;
      }
    }
  }

  {
    std::lock_guard<std::mutex> lock(persistent_mutex);
    if (persistent_cache.root_key != root_key) {
      persistent_cache = PersistentCache{};
      persistent_cache.root_key = root_key;
    }
    for (const auto& [resource_hash, names] : runtime_names) {
      if (resource_hash == 0) {
        continue;
      }
      auto [owned, inserted] = ownership.emplace(resource_hash, false);
      (void)inserted;
      persistent_cache.owned_by_hash.emplace(resource_hash, owned->second);
    }
    cached_total = persistent_cache.owned_by_hash.size();
  }

  if (VfsTracingEnabled() &&
      ShouldLogRepeatedVfsTraceOperation("RefiiHashOwnershipCache", 16)) {
    size_t owned = 0;
    for (const auto& [resource_hash, value] : ownership) {
      (void)resource_hash;
      if (value) {
        ++owned;
      }
    }
    REXLOG_INFO("Refii VFS hash ownership cache runtime_hashes={} owned={} "
                "scanned_shards={} matched_hashes={} cached_total={}",
                runtime_names.size(),
                owned,
                scanned_shards,
                matched_hashes,
                cached_total);
  }

  return ownership;
}

std::string MakeBackslashResourceName(std::string_view alias) {
  std::string backslash_name(alias);
  std::replace(backslash_name.begin(), backslash_name.end(), '/', '\\');
  return backslash_name;
}

std::vector<std::string> GetRuntimeLanguageFamilyAliases(
    const std::filesystem::path& loose_root,
    std::string_view family_prefix) {
  struct Cache {
    std::string root_key;
    std::unordered_map<std::string, std::vector<std::string>> aliases_by_family;
  };
  static std::mutex mutex;
  static Cache cache;

  const std::string root_key = loose_root.lexically_normal().string();
  const std::string family_key(family_prefix);
  {
    std::lock_guard<std::mutex> lock(mutex);
    if (cache.root_key != root_key) {
      cache = Cache{};
      cache.root_key = root_key;
    }
    const auto cached = cache.aliases_by_family.find(family_key);
    if (cached != cache.aliases_by_family.end()) {
      return cached->second;
    }
  }

  std::vector<std::string> aliases;
  constexpr std::string_view kLanguagePrefix = "language/";
  const std::vector<std::string> candidates =
      LoadRefiiVfsRuntimeCandidates(loose_root);
  for (const std::string& candidate : candidates) {
    const std::string normalized = NormalizeLooseAssetKey(candidate);
    if (!StartsWith(normalized, kLanguagePrefix)) {
      continue;
    }
    const std::string_view language_tail =
        std::string_view(normalized).substr(kLanguagePrefix.size());
    const size_t locale_end = language_tail.find('/');
    if (locale_end == std::string_view::npos ||
        locale_end + 1 >= language_tail.size()) {
      continue;
    }
    const std::string_view asset_tail = language_tail.substr(locale_end + 1);
    if (!StartsWith(asset_tail, family_prefix)) {
      continue;
    }
    AddUnique(aliases, std::string(kLanguagePrefix) + std::string(asset_tail));
    if (ShouldQueryVfsDatabaseAlias(normalized)) {
      AddUnique(aliases, normalized);
    }
  }

  std::lock_guard<std::mutex> lock(mutex);
  if (cache.root_key != root_key) {
    cache = Cache{};
    cache.root_key = root_key;
  }
  auto [iterator, inserted] =
      cache.aliases_by_family.emplace(family_key, aliases);
  (void)inserted;
  return iterator->second;
}

bool RuntimeResourceHashHasLooseResolution(uint32_t resource_hash,
                                           const std::vector<std::string>& names) {
  if (resource_hash == 0) {
    return false;
  }

  const std::filesystem::path loose_root = GetLooseAssetRoot();
  const std::string root_key = loose_root.lexically_normal().string();
  uint64_t names_fingerprint = 1469598103934665603ull;
  for (const std::string& name : names) {
    const std::string normalized = NormalizeLooseAssetKey(name);
    names_fingerprint = HashFingerprintString(names_fingerprint, normalized);
    names_fingerprint ^= 0xFFu;
    names_fingerprint *= 1099511628211ull;
  }
  const std::string cache_key =
      fmt::format("{:08X}:{:016X}", resource_hash, names_fingerprint);
  struct RuntimeHashResolutionCache {
    std::string root_key;
    std::unordered_map<std::string, bool> by_hash_and_names;
  };
  static std::mutex cache_mutex;
  static RuntimeHashResolutionCache cache;

  {
    std::lock_guard<std::mutex> lock(cache_mutex);
    if (cache.root_key != root_key) {
      cache = RuntimeHashResolutionCache{};
      cache.root_key = root_key;
    }
    const auto cached = cache.by_hash_and_names.find(cache_key);
    if (cached != cache.by_hash_and_names.end()) {
      return cached->second;
    }
  }

  auto store_result = [&](bool result) {
    std::lock_guard<std::mutex> lock(cache_mutex);
    if (cache.root_key != root_key) {
      cache = RuntimeHashResolutionCache{};
      cache.root_key = root_key;
    }
    constexpr size_t kMaxRuntimeHashResolutionCacheEntries = 4096;
    if (cache.by_hash_and_names.size() >
        kMaxRuntimeHashResolutionCacheEntries) {
      cache.by_hash_and_names.clear();
    }
    cache.by_hash_and_names.emplace(cache_key, result);
    return result;
  };

  if (!names.empty() && ShouldPassThroughNativeResourceHash(resource_hash, names)) {
    return store_result(false);
  }

  if (RefiiVfsDatabaseExists(loose_root)) {
    for (const std::string& candidate :
         LookupRefiiVfsDatabaseHashCandidates(resource_hash, loose_root)) {
      LooseAssetResolution resolution;
      std::string synthetic_candidate;
      if (MakeVfsDatabaseCandidateResolution(loose_root,
                                             candidate,
                                             &resolution,
                                             &synthetic_candidate) ||
          ResolveSyntheticBankManifestLooseAsset(
              candidate, &resolution, &synthetic_candidate)) {
        return store_result(true);
      }
    }
  }

  for (const std::string& name : names) {
    if (name.empty() || IsLuaScriptAssetKey(name) ||
        IsLanguageVideoAssetKey(NormalizeLooseAssetKey(name)) ||
        ShouldSkipLooseNativeAsset(name, "request_manager_hash_table")) {
      continue;
    }
    if (IsCachedOptionalSyntheticVfsMiss(name)) {
      continue;
    }

    LooseAssetResolution resolution;
    std::string candidate;
    if (ResolveExactTypedBankManifestLooseAsset(
            name, &resolution, &candidate)) {
      return store_result(true);
    }
    if (ResolveLooseAssetOverride(name, &resolution)) {
      return store_result(true);
    }
    if (ResolveVfsDatabaseAliasLooseAsset(name, &resolution, &candidate)) {
      return store_result(true);
    }
    if (ResolveSyntheticBankEntryLooseAsset(name, &resolution, &candidate)) {
      return store_result(true);
    }
  }

  return store_result(false);
}

bool RuntimeResourceHashNamesAreOptionalMisses(
    const std::vector<std::string>& names) {
  if (names.empty()) {
    return false;
  }

  bool saw_optional_probe = false;
  for (const std::string& name : names) {
    if (name.empty()) {
      continue;
    }
    const std::string normalized = NormalizeLooseAssetKey(name);
    if (!IsOptionalSyntheticVfsProbe(normalized)) {
      return false;
    }
    saw_optional_probe = true;
    if (!IsCachedOptionalSyntheticVfsMiss(normalized)) {
      return false;
    }
  }
  return saw_optional_probe;
}

bool AugmentRequestManagerHashTable(PPCContext& ctx,
                                    uint8_t* base,
                                    uint32_t manager_guest,
                                    std::string_view api) {
  if (base == nullptr ||
      manager_guest == 0) {
    return false;
  }

  const auto augment_started = std::chrono::steady_clock::now();
  const uint32_t container_guest = manager_guest + 52;
  const uint32_t old_begin = LoadGuestU32(base, container_guest + 4);
  const uint32_t old_end = LoadGuestU32(base, container_guest + 8);
  const uint32_t old_capacity_end = LoadGuestU32(base, container_guest + 12);
  if ((old_begin == 0 && old_end != 0) || old_end < old_begin ||
      old_capacity_end < old_end || ((old_end - old_begin) % 12) != 0) {
    if (VfsTracingEnabled()) {
      REXLOG_WARN("Refii VFS {} skipped hash-table augmentation for malformed "
                  "container manager=0x{:08X} begin=0x{:08X} end=0x{:08X} "
                  "capacity=0x{:08X}",
                  api,
                  manager_guest,
                  old_begin,
                  old_end,
                  old_capacity_end);
    }
    return false;
  }

  std::vector<RequestManagerHashEntry> entries;
  const uint32_t old_count =
      old_begin == 0 ? 0 : static_cast<uint32_t>((old_end - old_begin) / 12);

  if constexpr (!kAutoAugmentRequestManagerRuntimeHashes) {
    const auto targeted_snapshot =
        SnapshotTargetedRequestManagerHashContributions();
    if (targeted_snapshot.empty()) {
      return false;
    }

    std::unordered_map<uint32_t, TargetedRequestManagerHashContribution>
        targeted_by_hash;
    targeted_by_hash.reserve(targeted_snapshot.size());
    for (const auto& [resource_hash, contribution] : targeted_snapshot) {
      if (resource_hash != 0) {
        targeted_by_hash.emplace(resource_hash, contribution);
      }
    }

    std::unordered_set<uint32_t> found_targeted_hashes;
    found_targeted_hashes.reserve(targeted_by_hash.size());
    size_t rewritten_in_place = 0;
    size_t native_target_hits = 0;
    size_t refii_target_hits = 0;
    for (uint32_t index = 0; index < old_count; ++index) {
      const uint32_t entry_guest = old_begin + index * 12;
      const uint32_t entry_hash = LoadGuestU32(base, entry_guest + 0);
      const auto targeted = targeted_by_hash.find(entry_hash);
      if (targeted == targeted_by_hash.end()) {
        continue;
      }

      found_targeted_hashes.insert(entry_hash);
      const uint32_t provider_entry_key = LoadGuestU32(base, entry_guest + 8);
      const bool already_refii =
          provider_entry_key == kRefiiRequestManagerEntryKey;
      if (already_refii) {
        ++refii_target_hits;
      } else {
        // Leave the native entry untouched: its provider route is the correct
        // fallback when a refii open misses, and the FindStreamByHashEntry
        // hook pre-empts by hash regardless of the entry's provider fields.
        ++native_target_hits;
      }

      const TargetedRequestManagerHashContribution& contribution =
          targeted->second;
      LogRefiiVfsChainVerdict(
          "request_manager_hash",
          0,
          contribution.name,
          contribution.resolved_candidate.empty()
              ? contribution.name
              : contribution.resolved_candidate,
          contribution.provider_relative,
          "RefiiRequestManagerHashEntry",
          contribution.texture ? "RequestManager_hash_table_texture_fast_patch"
                               : "RequestManager_hash_table_fast_patch",
          "TraceOnce",
          "RequestManager_FindStreamByHashEntry");
    }

    if (found_targeted_hashes.size() == targeted_by_hash.size()) {
      if ((rewritten_in_place != 0 || VfsTracingEnabled()) &&
          ShouldLogRepeatedVfsTraceOperation(
              rewritten_in_place == 0
                  ? "RequestManagerHashAugmentTargetedAlreadyPresentFast"
                  : "RequestManagerHashAugmentTargetedFastPatch",
              64)) {
        const auto elapsed_ms =
            std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() - augment_started);
        REXLOG_INFO("Refii VFS {} targeted request-manager hash fast path "
                    "manager=0x{:08X} entries={} targeted_hashes={} "
                    "rewritten={} native_hits={} refii_hits={} elapsed_ms={}",
                    api,
                    manager_guest,
                    old_count,
                    targeted_by_hash.size(),
                    rewritten_in_place,
                    native_target_hits,
                    refii_target_hits,
                    elapsed_ms.count());
      }
      return rewritten_in_place != 0;
    }

    if (VfsTracingEnabled() &&
        ShouldLogRepeatedVfsTraceOperation(
            "RequestManagerHashAugmentTargetedMissingFast", 64)) {
      const auto elapsed_ms =
          std::chrono::duration_cast<std::chrono::milliseconds>(
              std::chrono::steady_clock::now() - augment_started);
      REXLOG_INFO("Refii VFS {} targeted request-manager hash fast path needs "
                  "rebuild manager=0x{:08X} entries={} targeted_hashes={} "
                  "found={} rewritten={} elapsed_ms={}",
                  api,
                  manager_guest,
                  old_count,
                  targeted_by_hash.size(),
                  found_targeted_hashes.size(),
                  rewritten_in_place,
                  elapsed_ms.count());
    }

    std::vector<RequestManagerHashEntry> missing_targeted_entries;
    missing_targeted_entries.reserve(targeted_by_hash.size() -
                                     found_targeted_hashes.size());
    for (const auto& [resource_hash, contribution] : targeted_by_hash) {
      if (found_targeted_hashes.find(resource_hash) !=
          found_targeted_hashes.end()) {
        continue;
      }
      missing_targeted_entries.push_back({resource_hash,
                                          kRefiiRequestManagerGuestProviderIndex,
                                          kRefiiRequestManagerEntryKey});
      LogRefiiVfsChainVerdict(
          "request_manager_hash",
          0,
          contribution.name,
          contribution.resolved_candidate.empty()
              ? contribution.name
              : contribution.resolved_candidate,
          contribution.provider_relative,
          "RefiiRequestManagerHashEntry",
          contribution.texture ? "RequestManager_hash_table_texture_fast_insert"
                               : "RequestManager_hash_table_fast_insert",
          "TraceOnce",
          "RequestManager_FindStreamByHashEntry");
    }

    if (!missing_targeted_entries.empty()) {
      std::stable_sort(
          missing_targeted_entries.begin(),
          missing_targeted_entries.end(),
          [](const RequestManagerHashEntry& left,
             const RequestManagerHashEntry& right) {
            if (left.hash != right.hash) {
              return left.hash < right.hash;
            }
            if (left.provider_index != right.provider_index) {
              return left.provider_index < right.provider_index;
            }
            return left.provider_entry_key < right.provider_entry_key;
          });

      const size_t new_count =
          static_cast<size_t>(old_count) + missing_targeted_entries.size();
      if (new_count >
          (std::numeric_limits<uint32_t>::max() /
           sizeof(RequestManagerHashEntry))) {
        return false;
      }
      const uint32_t bytes =
          static_cast<uint32_t>(new_count * sizeof(RequestManagerHashEntry));
      const uint32_t new_begin = CallLhMalloc(ctx, base, bytes);
      if (new_begin == 0) {
        REXLOG_WARN("Refii VFS {} failed to allocate targeted request-manager "
                    "hash insertion entries={} bytes={}",
                    api,
                    new_count,
                    bytes);
        return false;
      }

      auto read_old_entry = [&](uint32_t index) {
        const uint32_t entry_guest = old_begin + index * 12;
        return RequestManagerHashEntry{
            LoadGuestU32(base, entry_guest + 0),
            LoadGuestU32(base, entry_guest + 4),
            LoadGuestU32(base, entry_guest + 8)};
      };
      auto less_entry = [](const RequestManagerHashEntry& left,
                           const RequestManagerHashEntry& right) {
        if (left.hash != right.hash) {
          return left.hash < right.hash;
        }
        if (left.provider_index != right.provider_index) {
          return left.provider_index < right.provider_index;
        }
        return left.provider_entry_key < right.provider_entry_key;
      };
      auto write_entry = [&](uint32_t index,
                             const RequestManagerHashEntry& entry) {
        const uint32_t entry_guest = new_begin + index * 12;
        StoreGuestU32(base, entry_guest + 0, entry.hash);
        StoreGuestU32(base, entry_guest + 4, entry.provider_index);
        StoreGuestU32(base, entry_guest + 8, entry.provider_entry_key);
      };

      uint32_t old_index = 0;
      size_t missing_index = 0;
      uint32_t out_index = 0;
      while (old_index < old_count ||
             missing_index < missing_targeted_entries.size()) {
        if (old_index >= old_count) {
          write_entry(out_index++, missing_targeted_entries[missing_index++]);
          continue;
        }
        if (missing_index >= missing_targeted_entries.size()) {
          write_entry(out_index++, read_old_entry(old_index++));
          continue;
        }

        const RequestManagerHashEntry old_entry = read_old_entry(old_index);
        const RequestManagerHashEntry& missing_entry =
            missing_targeted_entries[missing_index];
        if (missing_entry.hash == old_entry.hash) {
          write_entry(out_index++, old_entry);
          ++old_index;
          ++missing_index;
        } else if (less_entry(missing_entry, old_entry)) {
          write_entry(out_index++, missing_entry);
          ++missing_index;
        } else {
          write_entry(out_index++, old_entry);
          ++old_index;
        }
      }

      const uint32_t new_end = new_begin + out_index * 12;
      StoreGuestU32(base, container_guest + 4, new_begin);
      StoreGuestU32(base, container_guest + 8, new_end);
      StoreGuestU32(base, container_guest + 12, new_end);
      if (old_begin != 0) {
        CallLhFree(ctx, base, old_begin);
      }

      if (VfsTracingEnabled() ||
          ShouldLogRepeatedVfsTraceOperation(
              "RequestManagerHashAugmentTargetedFastInsert", 32)) {
        const auto elapsed_ms =
            std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() - augment_started);
        REXLOG_INFO("Refii VFS {} targeted request-manager hash fast insert "
                    "manager=0x{:08X} native_entries={} inserted={} "
                    "rewritten={} total_entries={} begin=0x{:08X} "
                    "end=0x{:08X} elapsed_ms={}",
                    api,
                    manager_guest,
                    old_count,
                    missing_targeted_entries.size(),
                    rewritten_in_place,
                    out_index,
                    new_begin,
                    new_end,
                    elapsed_ms.count());
      }
      return true;
    }
  }

  entries.reserve(static_cast<size_t>(old_count) + 1024);
  for (uint32_t index = 0; index < old_count; ++index) {
    const uint32_t entry_guest = old_begin + index * 12;
    entries.push_back({LoadGuestU32(base, entry_guest + 0),
                       LoadGuestU32(base, entry_guest + 4),
                       LoadGuestU32(base, entry_guest + 8)});
  }

  std::unordered_set<uint32_t> added_hashes;
  added_hashes.reserve(entries.size() + 1024);
  for (const RequestManagerHashEntry& entry : entries) {
    added_hashes.insert(entry.hash);
  }

  const auto runtime_names = SnapshotRuntimeResourceHashNames();
  const uint64_t entry_fingerprint = FingerprintRequestManagerEntries(entries);
  const uint64_t runtime_fingerprint =
      FingerprintRuntimeResourceHashNames(runtime_names);
  const std::string noop_cache_key =
      fmt::format("{}:{:08X}:{:08X}:{:08X}:{}:{}:{:016X}:{:016X}",
                  api,
                  manager_guest,
                  old_begin,
                  old_end,
                  old_count,
                  runtime_names.size(),
                  entry_fingerprint,
                  runtime_fingerprint);
  if (IsCachedRequestManagerHashAugmentNoop(noop_cache_key)) {
    if (VfsTracingEnabled() &&
        ShouldLogRepeatedVfsTraceOperation(
            "RequestManagerHashAugmentCachedNoop", 16)) {
      REXLOG_INFO("Refii VFS {} skipped cached no-op request-manager hash "
                  "augmentation manager=0x{:08X} entries={} runtime_hashes={} "
                  "fingerprint=0x{:016X}",
                  api,
                  manager_guest,
                  old_count,
                  runtime_names.size(),
                  entry_fingerprint);
    }
    return false;
  }

  size_t contributed = 0;
  size_t expanded_alias_contributed = 0;
  size_t rewritten = 0;
  size_t targeted_texture_rewritten = 0;
  std::unordered_map<uint32_t, bool> ownership_by_hash;
  if (kAutoAugmentRequestManagerRuntimeHashes) {
    ownership_by_hash = BuildRefiiHashOwnershipCache(runtime_names);
  }
  for (RequestManagerHashEntry& entry : entries) {
    if (entry.hash == 0 ||
        entry.provider_entry_key == kRefiiRequestManagerEntryKey) {
      continue;
    }

    bool refii_owned_hash = false;
    const auto targeted = GetTargetedRequestManagerHashContribution(entry.hash);
    std::string targeted_resolved_candidate;
    if (targeted.has_value()) {
      refii_owned_hash = true;
      targeted_resolved_candidate =
          targeted->resolved_candidate.empty() ? targeted->name
                                               : targeted->resolved_candidate;
      if (targeted->texture) {
        ++targeted_texture_rewritten;
      }
    }

    if (!refii_owned_hash && kAutoAugmentRequestManagerRuntimeHashes) {
      const auto names = runtime_names.find(entry.hash);
      if (names == runtime_names.end()) {
        continue;
      }
      const auto owned = ownership_by_hash.find(entry.hash);
      if (owned == ownership_by_hash.end() || !owned->second) {
        continue;
      }
      refii_owned_hash = true;
    }

    if (!refii_owned_hash) {
      continue;
    }

    // No mutation: native entries keep their provider route as the fallback.
    // `rewritten` now counts refii-owned hashes that were confirmed present.
    ++rewritten;
    if (targeted.has_value()) {
      LogRefiiVfsChainVerdict(
          "request_manager_hash",
          0,
          targeted->name,
          targeted_resolved_candidate,
          targeted->provider_relative,
          "RefiiRequestManagerHashEntry",
          targeted->texture ? "RequestManager_hash_table_texture_rewrite"
                            : "RequestManager_hash_table_rewrite",
          "TraceOnce",
          "RequestManager_FindStreamByHashEntry");
    }
  }
  const auto rewrite_finished = std::chrono::steady_clock::now();

  size_t deferred_contribution_candidates = 0;
  size_t targeted_contribution_candidates = 0;
  size_t targeted_texture_contribution_candidates = 0;
  size_t auto_contribution_candidates = 0;
  std::vector<std::string> deferred_samples;
  if (kAutoAugmentRequestManagerRuntimeHashes) {
    for (const auto& [resource_hash, names] : runtime_names) {
    if (resource_hash == 0 || added_hashes.find(resource_hash) != added_hashes.end()) {
      continue;
    }
    const auto targeted = GetTargetedRequestManagerHashContribution(resource_hash);
    std::string targeted_resolved_candidate;
    if (targeted.has_value()) {
      targeted_resolved_candidate =
          targeted->resolved_candidate.empty() ? targeted->name
                                               : targeted->resolved_candidate;
      AddRefiiRequestManagerHashEntry(
          entries, added_hashes, resource_hash, &contributed);
      ++targeted_contribution_candidates;
      if (targeted->texture) {
        ++targeted_texture_contribution_candidates;
      }
      if (VfsTracingEnabled() &&
          ShouldLogRepeatedVfsTraceOperation(
              "RequestManagerHashAugmentTargetedHash", 32)) {
        REXLOG_INFO("Refii VFS {} targeted request-manager hash contribution "
                    "manager=0x{:08X} hash=0x{:08X} name={} reason={} texture={} "
                    "resolved_candidate={} provider_relative={} source={} "
                    "runtime_names=[{}]",
                    api,
                    manager_guest,
                    resource_hash,
                    targeted->name,
                    targeted->reason,
                    targeted->texture,
                    targeted_resolved_candidate,
                    targeted->provider_relative,
                    targeted->source,
                    FormatCandidateSample(names, 4));
      }
      LogRefiiVfsChainVerdict("request_manager_hash",
                              0,
                              targeted->name,
                              targeted_resolved_candidate,
                              targeted->provider_relative,
                              "RefiiRequestManagerHashEntry",
                              "RequestManager_hash_table_contribution",
                              "TraceOnce",
                              "RequestManager_FindStreamByHashEntry");
      continue;
    }
    if (RuntimeResourceHashNamesAreOptionalMisses(names)) {
      continue;
    }
    if (RuntimeResourceHashHasLooseResolution(resource_hash, names)) {
      AddRefiiRequestManagerHashEntry(
          entries, added_hashes, resource_hash, &contributed);
      ++auto_contribution_candidates;
      if (VfsTracingEnabled() &&
          ShouldLogRepeatedVfsTraceOperation(
              "RequestManagerHashAugmentAutoHash", 32)) {
        REXLOG_INFO("Refii VFS {} auto request-manager hash contribution "
                    "manager=0x{:08X} hash=0x{:08X} runtime_names=[{}]",
                    api,
                    manager_guest,
                    resource_hash,
                    FormatCandidateSample(names, 4));
      }
      LogRefiiVfsChainVerdict("request_manager_hash",
                              0,
                              FormatCandidateSample(names, 1),
                              FormatCandidateSample(names, 1),
                              "",
                              "RefiiRequestManagerHashEntry",
                              "RequestManager_hash_table_auto_contribution",
                              "TraceOnce",
                              "RequestManager_FindStreamByHashEntry");
      continue;
    }
    ++deferred_contribution_candidates;
    if (deferred_samples.size() < 8) {
      deferred_samples.push_back(
          fmt::format("0x{:08X}:{}", resource_hash, FormatCandidateSample(names, 3)));
    }
    }
  }
  for (const auto& [resource_hash, targeted] :
       SnapshotTargetedRequestManagerHashContributions()) {
    if (resource_hash == 0 ||
        added_hashes.find(resource_hash) != added_hashes.end()) {
      continue;
    }

    AddRefiiRequestManagerHashEntry(
        entries, added_hashes, resource_hash, &contributed);
    ++targeted_contribution_candidates;
    if (targeted.texture) {
      ++targeted_texture_contribution_candidates;
    }
    if (VfsTracingEnabled() &&
        ShouldLogRepeatedVfsTraceOperation(
            "RequestManagerHashAugmentTargetedOnlyHash", 32)) {
      REXLOG_INFO("Refii VFS {} targeted-only request-manager hash contribution "
                  "manager=0x{:08X} hash=0x{:08X} name={} reason={} texture={} "
                  "resolved_candidate={} provider_relative={} source={}",
                  api,
                  manager_guest,
                  resource_hash,
                  targeted.name,
                  targeted.reason,
                  targeted.texture,
                  targeted.resolved_candidate.empty() ? targeted.name
                                                      : targeted.resolved_candidate,
                  targeted.provider_relative,
                  targeted.source);
    }
    LogRefiiVfsChainVerdict(
        "request_manager_hash",
        0,
        targeted.name,
        targeted.resolved_candidate.empty() ? targeted.name
                                            : targeted.resolved_candidate,
        targeted.provider_relative,
        "RefiiRequestManagerHashEntry",
        "RequestManager_hash_table_targeted_only_contribution",
        "TraceOnce",
        "RequestManager_FindStreamByHashEntry");
  }
  if (deferred_contribution_candidates > 0 && VfsTracingEnabled() &&
      ShouldLogRepeatedVfsTraceOperation(
          "RequestManagerHashAugmentDeferredNewHashes", 16)) {
    const auto elapsed_ms = std::chrono::duration_cast<std::chrono::milliseconds>(
        std::chrono::steady_clock::now() - augment_started);
    REXLOG_INFO("Refii VFS {} deferred new request-manager hash contributions "
                "manager=0x{:08X} candidates={} existing_entries={} "
                "runtime_hashes={} targeted_contributed={} "
                "targeted_texture_contributed={} auto_contributed={} "
                "elapsed_ms={} samples=[{}]",
                api,
                manager_guest,
                deferred_contribution_candidates,
                old_count,
                runtime_names.size(),
                targeted_contribution_candidates,
                targeted_texture_contribution_candidates,
                auto_contribution_candidates,
                elapsed_ms.count(),
                JoinStrings(deferred_samples, ", "));
  }

  if (contributed == 0 && rewritten == 0) {
    CacheRequestManagerHashAugmentNoop(noop_cache_key);
    if (VfsTracingEnabled() &&
        ShouldLogRepeatedVfsTraceOperation("RequestManagerHashAugmentEmpty", 16)) {
      const auto elapsed_ms =
          std::chrono::duration_cast<std::chrono::milliseconds>(
              std::chrono::steady_clock::now() - augment_started);
      REXLOG_INFO("Refii VFS {} hash-table augmentation no-op manager=0x{:08X} "
                  "native_entries={} runtime_hashes={} elapsed_ms={}",
                  api,
                  manager_guest,
                  old_count,
                  runtime_names.size(),
                  elapsed_ms.count());
    }
    return false;
  }

  if (contributed == 0) {
    if (old_begin != 0 && entries.size() == old_count) {
      for (uint32_t index = 0; index < old_count; ++index) {
        const uint32_t entry_guest = old_begin + index * 12;
        StoreGuestU32(base, entry_guest + 0, entries[index].hash);
        StoreGuestU32(base, entry_guest + 4, entries[index].provider_index);
        StoreGuestU32(base, entry_guest + 8, entries[index].provider_entry_key);
      }
      if (VfsTracingEnabled() ||
          ShouldLogRepeatedVfsTraceOperation(
              "RequestManagerHashAugmentPatchedInPlace", 16)) {
        const auto elapsed_ms =
            std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() - augment_started);
        const auto rewrite_ms =
            std::chrono::duration_cast<std::chrono::milliseconds>(
                rewrite_finished - augment_started);
        REXLOG_INFO("Refii VFS {} patched request-manager hash table in place "
                    "manager=0x{:08X} entries={} rewritten_entries={} "
                    "refii_entries=0 deferred_new_hashes={} "
                    "targeted_contributed={} auto_contributed={} "
                    "targeted_texture_rewritten={} "
                    "fingerprint=0x{:016X} rewrite_ms={} elapsed_ms={}",
                    api,
                    manager_guest,
                    old_count,
                    rewritten,
                    deferred_contribution_candidates,
                    targeted_contribution_candidates,
                    auto_contribution_candidates,
                    targeted_texture_rewritten,
                    entry_fingerprint,
                    rewrite_ms.count(),
                    elapsed_ms.count());
      }
      const uint64_t patched_fingerprint =
          FingerprintRequestManagerEntries(entries);
      CacheRequestManagerHashAugmentNoop(
          fmt::format("{}:{:08X}:{:08X}:{:08X}:{}:{}:{:016X}:{:016X}",
                      api,
                      manager_guest,
                      old_begin,
                      old_end,
                      old_count,
                      runtime_names.size(),
                      patched_fingerprint,
                      runtime_fingerprint));
      return true;
    }

    if (VfsTracingEnabled() ||
        ShouldLogRepeatedVfsTraceOperation(
            "RequestManagerHashAugmentNativeOnlyRewriteSkipped", 16)) {
      const auto elapsed_ms =
          std::chrono::duration_cast<std::chrono::milliseconds>(
              std::chrono::steady_clock::now() - augment_started);
      REXLOG_INFO("Refii VFS {} skipped native-only request-manager hash "
                  "rewrite manager=0x{:08X} entries={} rewritten_entries={} "
                  "refii_entries=0 deferred_new_hashes={} "
                  "targeted_contributed={} auto_contributed={} "
                  "targeted_texture_rewritten={} "
                  "elapsed_ms={}",
                  api,
                  manager_guest,
                  old_count,
                  rewritten,
                  deferred_contribution_candidates,
                  targeted_contribution_candidates,
                  auto_contribution_candidates,
                  targeted_texture_rewritten,
                  elapsed_ms.count());
    }
    return false;
  }

  std::stable_sort(entries.begin(), entries.end(),
                   [](const RequestManagerHashEntry& left,
                      const RequestManagerHashEntry& right) {
                     if (left.hash != right.hash) {
                       return left.hash < right.hash;
                     }
                     if (left.provider_index != right.provider_index) {
                       return left.provider_index < right.provider_index;
                     }
                     return left.provider_entry_key < right.provider_entry_key;
                   });

  if (entries.size() >
      (std::numeric_limits<uint32_t>::max() / sizeof(RequestManagerHashEntry))) {
    return false;
  }
  const uint32_t bytes =
      static_cast<uint32_t>(entries.size() * sizeof(RequestManagerHashEntry));
  const uint32_t new_begin = CallLhMalloc(ctx, base, bytes);
  if (new_begin == 0) {
    REXLOG_WARN("Refii VFS {} failed to allocate augmented request-manager "
                "hash table entries={} bytes={}",
                api,
                entries.size(),
                bytes);
    return false;
  }

  for (size_t index = 0; index < entries.size(); ++index) {
    const uint32_t entry_guest =
        new_begin + static_cast<uint32_t>(index * sizeof(RequestManagerHashEntry));
    StoreGuestU32(base, entry_guest + 0, entries[index].hash);
    StoreGuestU32(base, entry_guest + 4, entries[index].provider_index);
    StoreGuestU32(base, entry_guest + 8, entries[index].provider_entry_key);
  }

  const uint32_t new_end = new_begin + bytes;
  StoreGuestU32(base, container_guest + 4, new_begin);
  StoreGuestU32(base, container_guest + 8, new_end);
  StoreGuestU32(base, container_guest + 12, new_end);
  if (old_begin != 0) {
    CallLhFree(ctx, base, old_begin);
  }

  if (VfsTracingEnabled() ||
      ShouldLogRepeatedVfsTraceOperation("RequestManagerHashAugment", 16)) {
    const auto elapsed_ms = std::chrono::duration_cast<std::chrono::milliseconds>(
        std::chrono::steady_clock::now() - augment_started);
    REXLOG_INFO("Refii VFS {} rebuilt request-manager hash table with Refii entries "
                "manager=0x{:08X} native_entries={} refii_entries={} "
                "rewritten_entries={} alias_entries={} targeted_entries={} "
                "auto_entries={} "
                "targeted_texture_entries={} targeted_texture_rewritten={} total_entries={} "
                "begin=0x{:08X} end=0x{:08X} elapsed_ms={}",
                api,
                manager_guest,
                old_count,
                contributed,
                rewritten,
                expanded_alias_contributed,
                targeted_contribution_candidates,
                auto_contribution_candidates,
                targeted_texture_contribution_candidates,
                targeted_texture_rewritten,
                entries.size(),
                new_begin,
                new_end,
                elapsed_ms.count());
  }
  return true;
}

void RerunKnownRequestManagerHashAugmentation(PPCContext& ctx,
                                              uint8_t* base,
                                              std::string_view api,
                                              uint32_t resource_hash,
                                              std::string_view name) {
  if (base == nullptr) {
    return;
  }

  const std::vector<uint32_t> managers = SnapshotKnownRequestManagerGuests();
  size_t attempted = 0;
  size_t patched = 0;
  for (const uint32_t manager_guest : managers) {
    if (manager_guest == 0) {
      continue;
    }
    PPCContext augment_ctx = ctx;
    ++attempted;
    if (AugmentRequestManagerHashTable(
            augment_ctx, base, manager_guest, api)) {
      ++patched;
    }
  }

  if (VfsTracingEnabled() &&
      ShouldLogRepeatedVfsTraceOperation("TextureHashWakeRequestManager", 64)) {
    REXLOG_INFO("Refii VFS texture hash wake request-manager api={} "
                "hash=0x{:08X} name={} managers={} patched={}",
                api,
                resource_hash,
                NormalizeLooseAssetKey(name),
                attempted,
                patched);
  }
}

void RegisterRefiiOwnedTextureHashContribution(PPCContext& ctx,
                                               uint8_t* base,
                                               uint32_t resource_hash,
                                               std::string_view name,
                                               uint32_t caller_lr) {
  if (resource_hash == 0 ||
      !IsTextureAssetCandidate(name)) {
    return;
  }

  const bool static_resource_hash =
      caller_lr >= 0x83200000 && caller_lr < 0x83300000;
  if (static_resource_hash) {
    const std::filesystem::path loose_root = GetLooseAssetRoot();
    const std::unordered_set<uint32_t>* owned_hashes =
        GetRefiiVfsRuntimeOwnedHashes(loose_root);
    if (owned_hashes != nullptr) {
      if (owned_hashes->find(resource_hash) == owned_hashes->end()) {
        return;
      }

      const bool contribution_changed =
          RegisterTargetedRequestManagerHashContribution(
              resource_hash,
              name,
              "texture_static_hash_refii_owned",
              {},
              {},
              "hash_ownership_table");
      if (VfsTracingEnabled() &&
          ShouldLogRepeatedVfsTraceOperation(
              "ResourceNameHashStaticOwnedFastPath", 64)) {
        REXLOG_INFO("Refii VFS texture_static_hash_refii_owned lr=0x{:08X} "
                    "hash=0x{:08X} name={} source=hash_ownership_table "
                    "targeted_hashes={} note=deferred_resolution_until_open",
                    caller_lr,
                    resource_hash,
                    NormalizeLooseAssetKey(name),
                    TargetedRequestManagerHashContributionCount());
      }
      LogRefiiVfsChainVerdict("request_manager_hash",
                              caller_lr,
                              name,
                              "",
                              "",
                              "RefiiRequestManagerHashEntry",
                              "ResourceNameHash_targeted_registration",
                              "TraceOnce",
                              "RequestManager_BuildProviderHash");
      if (contribution_changed) {
        if (FrontendTextureConsumerTraceActive()) {
          DeferFrontendTextureHashWake();
        } else {
          RerunKnownRequestManagerHashAugmentation(
              ctx, base, "ResourceNameHash_TextureWake", resource_hash, name);
        }
      }
      return;
    }
  }

  const std::string normalized_name = NormalizeLooseAssetKey(name);
  if (const auto existing =
          GetTargetedRequestManagerHashContribution(resource_hash);
      existing.has_value() && existing->texture &&
      existing->name == normalized_name &&
      !existing->resolved_candidate.empty() &&
      !existing->provider_relative.empty()) {
    if ((VfsTracingEnabled() || LooseAssetOverrideTracingEnabled()) &&
        ShouldLogRepeatedVfsTraceOperation(
            "ResourceNameHashTextureAlreadyOwned", 64)) {
      REXLOG_INFO("Refii VFS texture_hash_refii_owned reused lr=0x{:08X} "
                  "hash=0x{:08X} name={} candidate={} provider_relative={} "
                  "source={} targeted_hashes={}",
                  caller_lr,
                  resource_hash,
                  normalized_name,
                  existing->resolved_candidate,
                  existing->provider_relative,
                  existing->source,
                  TargetedRequestManagerHashContributionCount());
    }
    LogRefiiVfsChainVerdict("request_manager_hash",
                            caller_lr,
                            normalized_name,
                            existing->resolved_candidate,
                            existing->provider_relative,
                            "RefiiRequestManagerHashEntry",
                            "ResourceNameHash_targeted_registration_cached",
                            "TraceOnce",
                            "RequestManager_FindStreamByHashEntry");
    if (IsMainMenuTextureHashTraceAsset(normalized_name)) {
      RecordRefiiMainMenuBlockerEvent(
          "texture_selection",
          fmt::format("texture_hash_refii_owned_cached lr=0x{:08X} "
                      "hash=0x{:08X} name={} candidate={} source={}",
                      caller_lr,
                      resource_hash,
                      normalized_name,
                      existing->resolved_candidate,
                      existing->source));
    }
    return;
  }

  LooseAssetResolution resolution;
  std::string candidate;
  std::string source;
  if (!ResolveRefiiOwnedTextureResolution("ResourceNameHash",
                                          resource_hash,
                                          name,
                                          caller_lr,
                                          &resolution,
                                          &candidate,
                                          &source)) {
    if (VfsTracingEnabled() && IsMainMenuTextureHashTraceAsset(name)) {
      RecordRefiiMainMenuBlockerEvent(
          "texture_selection",
          fmt::format("texture_hash_refii_miss lr=0x{:08X} hash=0x{:08X} "
                      "name={}",
                      caller_lr,
                      resource_hash,
                      NormalizeLooseAssetKey(name)));
    }
    return;
  }

  const char* contribution_reason =
      static_resource_hash ? "texture_static_hash_refii_owned"
                           : "texture_hash_refii_owned";
  const bool contribution_changed =
      RegisterTargetedRequestManagerHashContribution(resource_hash,
                                                     name,
                                                     contribution_reason,
                                                     candidate,
                                                     resolution.provider_relative_path,
                                                     source);
  // (Texel-bridge registration is centralized in ResolveRefiiOwnedTextureResolution.)
  LogRefiiOwnedTextureResolution("ResourceNameHash",
                                 caller_lr,
                                 resource_hash,
                                 name,
                                 resolution,
                                 candidate,
                                 source);
  if (VfsTracingEnabled() || LooseAssetOverrideTracingEnabled() ||
      IsMainMenuTextureHashTraceAsset(name)) {
    REXLOG_INFO("Refii VFS {} lr=0x{:08X} "
                "hash=0x{:08X} name={} source={} candidate={} "
                "provider_relative={} host={} targeted_hashes={} note={}",
                contribution_reason,
                caller_lr,
                resource_hash,
                NormalizeLooseAssetKey(name),
                source,
                candidate,
                resolution.provider_relative_path,
                resolution.host_path.string(),
                TargetedRequestManagerHashContributionCount(),
                static_resource_hash ? "precomputed_resource_id_not_stream_open"
                                     : "");
  }
  LogRefiiVfsChainVerdict("request_manager_hash",
                          caller_lr,
                          name,
                          candidate,
                          resolution.provider_relative_path,
                          "RefiiRequestManagerHashEntry",
                          "ResourceNameHash_targeted_registration",
                          "TraceOnce",
                          "RequestManager_BuildProviderHash");
  if (!static_resource_hash) {
    RecordRefiiMainMenuBlockerEvent(
        "texture_selection",
        fmt::format("texture_hash_refii_owned lr=0x{:08X} hash=0x{:08X} "
                    "name={} candidate={} source={}",
                    caller_lr,
                    resource_hash,
                    NormalizeLooseAssetKey(name),
                    candidate,
                    source));
  }
  if (contribution_changed) {
    if (FrontendTextureConsumerTraceActive()) {
      DeferFrontendTextureHashWake();
      if (VfsTracingEnabled() &&
          ShouldLogRepeatedVfsTraceOperation(
              "ResourceNameHashTextureWakeDeferredInFrontend", 32)) {
        REXLOG_INFO("Refii VFS deferred texture hash request-manager wake "
                    "inside frontend consumer lr=0x{:08X} hash=0x{:08X} "
                    "name={} targeted_hashes={}",
                    caller_lr,
                    resource_hash,
                    NormalizeLooseAssetKey(name),
                    TargetedRequestManagerHashContributionCount());
      }
    } else {
      RerunKnownRequestManagerHashAugmentation(
          ctx, base, "ResourceNameHash_TextureWake", resource_hash, name);
    }
  }
}

bool ShouldRegisterBootVfsResourceHashContribution(std::string_view normalized) {
  if (normalized.empty()) {
    return false;
  }

  if (IsLuaScriptAssetKey(normalized) ||
      StartsWith(normalized, "luasource/guiscripts/") ||
      StartsWith(normalized, "scripts/gameface/") ||
      StartsWith(normalized, "art/gui/gameface/")) {
    return true;
  }

  if (StartsWith(normalized, "art/videos/") && EndsWith(normalized, ".bik")) {
    return true;
  }

  if (StartsWith(normalized, "art/gui/")) {
    return EndsWith(normalized, ".bnk") || EndsWith(normalized, ".bsg") ||
           EndsWith(normalized, ".bgf") || EndsWith(normalized, ".fac");
  }

  if (StartsWith(normalized, "globals/")) {
    return EndsWith(normalized, ".bnk");
  }

  if (!StartsWith(normalized, "language/") &&
      !StartsWith(normalized, "language_dlc/")) {
    return false;
  }

  if (normalized.find("/lipsync/lipsync/") != std::string_view::npos) {
    return false;
  }

  return EndsWith(normalized, ".bft") || EndsWith(normalized, ".babel") ||
         EndsWith(normalized, ".adb") || EndsWith(normalized, ".bnk") ||
         EndsWith(normalized, ".bik");
}

void RegisterRefiiOwnedResourceHashContribution(PPCContext& ctx,
                                                uint8_t* base,
                                                uint32_t resource_hash,
                                                std::string_view name,
                                                uint32_t caller_lr) {
  if (resource_hash == 0 ||
      name.empty() || IsTextureAssetCandidate(name)) {
    return;
  }

  const std::string normalized_name = NormalizeLooseAssetKey(name);
  if (normalized_name.empty() ||
      !GetLooseNativeAssetSkipReason(normalized_name).empty() ||
      IsCachedOptionalSyntheticVfsMiss(normalized_name) ||
      !ShouldRegisterBootVfsResourceHashContribution(normalized_name)) {
    return;
  }

  const std::filesystem::path loose_root = GetLooseAssetRoot();
  LooseAssetResolution resolution;
  std::string candidate;
  std::string provider_relative;
  std::string source;
  bool owned = false;

  if (ResolveExactTypedBankManifestLooseAsset(normalized_name,
                                              &resolution,
                                              &candidate)) {
    owned = true;
    provider_relative = NormalizeLooseAssetKey(resolution.provider_relative_path);
    source = "exact_typed_bank_hash";
  } else if (ResolveVfsDatabaseExactAliasLooseAsset(normalized_name,
                                                    &resolution,
                                                    &candidate)) {
    owned = true;
    provider_relative = NormalizeLooseAssetKey(resolution.provider_relative_path);
    source = "database_alias_hash";
  } else if (RefiiDatabaseHashHasExactOwnedCandidate(resource_hash,
                                                     normalized_name)) {
    owned = true;
    candidate = normalized_name;
    source = "database_exact_hash";
  } else if (const std::unordered_set<uint32_t>* owned_hashes =
                 GetRefiiVfsRuntimeOwnedHashes(loose_root);
             owned_hashes != nullptr &&
             owned_hashes->find(resource_hash) != owned_hashes->end()) {
    owned = true;
    source = "hash_ownership_table";
  }

  if (!owned) {
    return;
  }

  const bool contribution_changed =
      RegisterTargetedRequestManagerHashContribution(resource_hash,
                                                     normalized_name,
                                                     "resource_hash_refii_owned",
                                                     candidate,
                                                     provider_relative,
                                                     source);
  LogRefiiVfsChainVerdict("request_manager_hash",
                          caller_lr,
                          normalized_name,
                          candidate,
                          provider_relative,
                          "RefiiRequestManagerHashEntry",
                          "ResourceNameHash_resource_registration",
                          "TraceOnce",
                          "RequestManager_BuildProviderHash");
  if (VfsTracingEnabled() &&
      ShouldLogRepeatedVfsTraceOperation(
          "ResourceNameHashResourceOwned", 64)) {
    REXLOG_INFO("Refii VFS resource_hash_refii_owned lr=0x{:08X} "
                "hash=0x{:08X} name={} source={} candidate={} "
                "provider_relative={} targeted_hashes={}",
                caller_lr,
                resource_hash,
                normalized_name,
                source,
                candidate,
                provider_relative,
                TargetedRequestManagerHashContributionCount());
  }

  if (!contribution_changed) {
    return;
  }
  if (FrontendTextureConsumerTraceActive()) {
    DeferFrontendTextureHashWake();
  } else {
    RerunKnownRequestManagerHashAugmentation(
        ctx, base, "ResourceNameHash_ResourceWake", resource_hash, normalized_name);
  }
}

bool ResolveSyntheticBankEntryLooseAsset(std::string_view asset_path,
                                         LooseAssetResolution* out_resolution,
                                         std::string* out_candidate) {
  if (asset_path.empty()) {
    return false;
  }
  if (IsCachedOptionalSyntheticVfsMiss(asset_path)) {
    return false;
  }

  const uint64_t lookups =
      LooseStats().synthetic_vfs_lookups.fetch_add(1,
                                                   std::memory_order_relaxed) +
      1;
  LogLooseStatsMilestone("synthetic_vfs_lookups", lookups);

  static std::mutex mutex;
  static std::unordered_map<std::string, CachedBankLooseAssetResolution> cache;

  const std::filesystem::path loose_root = GetLooseAssetRoot();
  const bool database_backed = RefiiVfsDatabaseExists(loose_root);
  std::shared_ptr<const SyntheticVfsEntryIndex> index;
  if (!database_backed) {
    index = GetSyntheticVfsEntryIndex();
  }
  const size_t template_count = index != nullptr ? index->template_count : 0;
  const std::string key =
      MakeSyntheticVfsLookupCacheKey(asset_path, template_count);
  {
    std::lock_guard<std::mutex> lock(mutex);
    const auto cached = cache.find(key);
    if (cached != cache.end()) {
      if (cached->second.found) {
        const uint64_t hits =
            LooseStats().synthetic_vfs_hits.fetch_add(
                1, std::memory_order_relaxed) +
            1;
        LogLooseStatsMilestone("synthetic_vfs_hits", hits);
        if (out_resolution != nullptr) {
          *out_resolution = cached->second.resolution;
        }
        if (out_candidate != nullptr) {
          *out_candidate = cached->second.candidate;
        }
      }
      if (!cached->second.found &&
          (VfsTracingEnabled() || IsTraceInterestingAsset(asset_path) ||
           ShouldLogLooseStatsMilestone(lookups))) {
        const std::vector<std::string> candidates =
            BuildSyntheticVfsEntryCandidates(asset_path);
        REXLOG_INFO("Refii synthetic VFS cached miss lookup={} asset={} "
                    "templates={} indexed_paths={} candidates=[{}]",
                    lookups,
                    asset_path,
                    template_count,
                    index != nullptr ? index->by_normalized_path.size() : 0,
                    FormatCandidateSample(candidates));
      }
      return cached->second.found;
    }
  }

  CachedBankLooseAssetResolution resolved;
  const std::vector<std::string> candidates =
      BuildSyntheticVfsEntryCandidates(asset_path);
  for (const std::string& candidate : candidates) {
    if (ResolveLooseAssetOverride(candidate, &resolved.resolution)) {
      resolved.found = true;
      resolved.candidate = candidate;
      break;
    }
  }
  for (const std::string& candidate : resolved.found ? std::vector<std::string>{}
                                                     : candidates) {
    if (ResolveVfsDatabaseAliasLooseAsset(candidate,
                                         &resolved.resolution,
                                         &resolved.candidate)) {
      resolved.found = true;
      break;
    }
  }
  for (const std::string& candidate : resolved.found ? std::vector<std::string>{}
                                                     : candidates) {
    if (ResolveOriginalGameDataAsset(candidate,
                                     &resolved.resolution,
                                     &resolved.candidate)) {
      resolved.found = true;
      break;
    }
  }
  if (!database_backed && index != nullptr) {
    for (const std::string& candidate : resolved.found ? std::vector<std::string>{}
                                                       : candidates) {
      const auto match = index->by_normalized_path.find(candidate);
      if (match == index->by_normalized_path.end()) {
        continue;
      }

      resolved.found = ResolveManifestEntryLooseAsset(match->second.manifest_root,
                                                      match->second.relative_path,
                                                      &resolved.resolution,
                                                      &resolved.candidate,
                                                      match->second.host_relative_path);
      if (resolved.found) {
        break;
      }
    }
  }

  if (!resolved.found &&
      (VfsTracingEnabled() || IsTraceInterestingAsset(asset_path) ||
       ShouldLogLooseStatsMilestone(lookups))) {
    REXLOG_INFO("Refii synthetic VFS miss lookup={} asset={} templates={} "
                "indexed_paths={} candidates=[{}]",
                lookups,
                asset_path,
                template_count,
                index != nullptr ? index->by_normalized_path.size() : 0,
                FormatCandidateSample(candidates));
  }

  {
    std::lock_guard<std::mutex> lock(mutex);
    constexpr size_t kMaxSyntheticVfsLookupCacheEntries = 65536;
    if (cache.size() > kMaxSyntheticVfsLookupCacheEntries) {
      cache.clear();
    }
    const auto [iterator, inserted] =
        cache.try_emplace(key, std::move(resolved));
    (void)inserted;
    const CachedBankLooseAssetResolution& value = iterator->second;
    if (value.found) {
      const uint64_t hits =
          LooseStats().synthetic_vfs_hits.fetch_add(
              1, std::memory_order_relaxed) +
          1;
      LogLooseStatsMilestone("synthetic_vfs_hits", hits);
      if (out_resolution != nullptr) {
        *out_resolution = value.resolution;
      }
      if (out_candidate != nullptr) {
        *out_candidate = value.candidate;
      }
    }
    return value.found;
  }
}

}  // namespace detail
}  // namespace refii::vfs
