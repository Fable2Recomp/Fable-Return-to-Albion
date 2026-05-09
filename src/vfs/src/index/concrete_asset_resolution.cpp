#include "vfs_hooks_private.h"

namespace refii::vfs {
inline namespace detail {

bool ResolveSyntheticBankEntryLooseAsset(std::string_view asset_path,
                                         LooseAssetResolution* out_resolution,
                                         std::string* out_candidate);

bool SelectResolvedVfsHashCandidate(
    uint32_t resource_hash,
    std::vector<ResolvedVfsHashCandidate>* resolved_candidates,
    bool prefer_resource_manager_texture_shape,
    uint32_t caller_lr,
    LooseAssetResolution* out_resolution,
    std::string* out_candidate);

uint32_t LoadBigEndianU32(const uint8_t* bytes) {
  return (static_cast<uint32_t>(bytes[0]) << 24) |
         (static_cast<uint32_t>(bytes[1]) << 16) |
         (static_cast<uint32_t>(bytes[2]) << 8) |
         static_cast<uint32_t>(bytes[3]);
}

bool IsRegularHostFile(const std::filesystem::path& path) {
  std::error_code error;
  return std::filesystem::is_regular_file(path, error);
}

uint64_t HostFileSize(const std::filesystem::path& path) {
  std::error_code error;
  const uint64_t size = std::filesystem::file_size(path, error);
  return error ? 0 : size;
}

std::array<uint32_t, 4> ReadHostFileFirstWords(
    const std::filesystem::path& path) {
  std::array<uint32_t, 4> words{};
  std::ifstream input(path, std::ios::binary);
  if (!input) {
    return words;
  }

  std::array<uint8_t, 16> bytes{};
  input.read(reinterpret_cast<char*>(bytes.data()), bytes.size());
  const std::streamsize read = input.gcount();
  for (size_t word = 0; word < words.size(); ++word) {
    if (read < static_cast<std::streamsize>((word + 1) * 4)) {
      break;
    }
    words[word] = LoadBigEndianU32(bytes.data() + word * 4);
  }
  return words;
}

std::string FormatHostFileFirstWords(const std::array<uint32_t, 4>& words) {
  return fmt::format("0x{:08X} 0x{:08X} 0x{:08X} 0x{:08X}",
                     words[0],
                     words[1],
                     words[2],
                     words[3]);
}

bool IsTextureAssetCandidate(std::string_view candidate) {
  return EndsWith(NormalizeLooseAssetKey(candidate), ".tex");
}

bool IsTextureHeaderCandidate(std::string_view candidate) {
  const std::string normalized = NormalizeLooseAssetKey(candidate);
  return normalized.find("_texture_headers/") != std::string::npos ||
         normalized.find("/texture_headers/") != std::string::npos ||
         normalized.find("texture_headers/") != std::string::npos;
}

bool IsTexturePayloadBankCandidate(std::string_view candidate) {
  const std::string normalized = NormalizeLooseAssetKey(candidate);
  return !IsTextureHeaderCandidate(normalized) &&
         (normalized.find("_textures/") != std::string::npos ||
          normalized.find("/textures/") != std::string::npos ||
          normalized.find("textures/") != std::string::npos);
}

bool IsKnownFullResourceTextureCandidate(std::string_view candidate) {
  const std::string normalized = NormalizeLooseAssetKey(candidate);
  return normalized.find("globals/1024mip0_textures/") != std::string::npos ||
         normalized.find("/1024mip0_textures/") != std::string::npos;
}

void AddUniqueCandidate(std::vector<std::string>* candidates,
                        std::string candidate) {
  if (candidates == nullptr || candidate.empty() ||
      std::find(candidates->begin(), candidates->end(), candidate) !=
          candidates->end()) {
    return;
  }
  candidates->push_back(std::move(candidate));
}

std::optional<std::string> ReplaceFirst(std::string value,
                                        std::string_view from,
                                        std::string_view to) {
  const size_t position = value.find(from);
  if (position == std::string::npos) {
    return std::nullopt;
  }
  value.replace(position, from.size(), to);
  return value;
}

std::vector<std::string> BuildExtractedTextureLayoutRepairCandidates(
    std::string_view candidate) {
  const std::string normalized = NormalizeLooseAssetKey(candidate);
  std::vector<std::string> repaired;
  if (normalized.empty() || !EndsWith(normalized, ".tex")) {
    return repaired;
  }

  auto add_texture_bank_layouts = [&](std::string logical_path) {
    logical_path = NormalizeLooseAssetKey(logical_path);
    if (logical_path.empty()) {
      return;
    }
    AddUniqueCandidate(&repaired, logical_path);
    if (StartsWith(logical_path, "art/fx/")) {
      AddUniqueCandidate(&repaired,
                         "globals/globals_texture_headers/" + logical_path);
      AddUniqueCandidate(&repaired,
                         "globals/globals_textures/" + logical_path);
    } else if (StartsWith(logical_path, "art/gui/")) {
      AddUniqueCandidate(&repaired,
                         "art/gui/gui_texture_headers/" + logical_path);
      AddUniqueCandidate(&repaired, "art/gui/gui_textures/" + logical_path);
      AddUniqueCandidate(&repaired, "globals/1024mip0_textures/" + logical_path);
    }
  };

  if (const auto pfx_header =
          ReplaceFirst(normalized, "pfx_texture_headers/", "pfx_textures/")) {
    add_texture_bank_layouts(*pfx_header);
  }
  if (const auto header_dir =
          ReplaceFirst(normalized, "/texture_headers/", "/textures/")) {
    add_texture_bank_layouts(*header_dir);
  }
  if (const auto header_root =
          ReplaceFirst(normalized, "_texture_headers/", "_textures/")) {
    add_texture_bank_layouts(*header_root);
  }

  return repaired;
}

std::optional<std::string> DerivePairedTextureHeaderCandidate(
    std::string_view candidate) {
  std::string normalized = NormalizeLooseAssetKey(candidate);
  if (normalized.empty() || IsTextureHeaderCandidate(normalized)) {
    return std::nullopt;
  }

  const std::pair<std::string_view, std::string_view> replacements[] = {
      {"_textures/", "_texture_headers/"},
      {"/textures/", "/texture_headers/"},
  };
  for (const auto& [from, to] : replacements) {
    const size_t position = normalized.find(from);
    if (position == std::string::npos) {
      continue;
    }
    normalized.replace(position, from.size(), to);
    return normalized;
  }

  return std::nullopt;
}

std::optional<std::string> DerivePairedTexturePayloadCandidate(
    std::string_view candidate) {
  std::string normalized = NormalizeLooseAssetKey(candidate);
  if (normalized.empty() || !IsTextureHeaderCandidate(normalized)) {
    return std::nullopt;
  }

  const std::pair<std::string_view, std::string_view> replacements[] = {
      {"_texture_headers/", "_textures/"},
      {"/texture_headers/", "/textures/"},
  };
  for (const auto& [from, to] : replacements) {
    const size_t position = normalized.find(from);
    if (position == std::string::npos) {
      continue;
    }
    normalized.replace(position, from.size(), to);
    return normalized;
  }

  return std::nullopt;
}

bool LooksLikeResourceWrapperHeader(const std::array<uint32_t, 4>& words) {
  return words[0] == 1 && words[1] != 0;
}

bool LooksLikeTextureHeaderDescriptor(const std::array<uint32_t, 4>& words) {
  return words[0] == 0xFFFFFFFEu && words[1] != 0;
}

void ScoreResourceManagerTextureCandidate(ResolvedVfsHashCandidate* candidate) {
  if (candidate == nullptr) {
    return;
  }
  if (!IsTextureAssetCandidate(candidate->candidate)) {
    candidate->score = 500;
    candidate->reason = "non_texture";
    return;
  }

  candidate->first_words = ReadHostFileFirstWords(candidate->resolution.host_path);
  candidate->file_size = HostFileSize(candidate->resolution.host_path);
  if (IsKnownFullResourceTextureCandidate(candidate->candidate) &&
      LooksLikeResourceWrapperHeader(candidate->first_words)) {
    candidate->score = 0;
    candidate->reason = "full_resource_texture";
    return;
  }
  if (IsTextureHeaderCandidate(candidate->candidate) &&
      LooksLikeTextureHeaderDescriptor(candidate->first_words)) {
    candidate->score = 1;
    candidate->reason = "split_texture_header";
    return;
  }
  if (IsTexturePayloadBankCandidate(candidate->candidate) &&
      candidate->paired_header_available) {
    candidate->score = 80;
    candidate->reason = "split_payload_rejected";
    return;
  }
  if (LooksLikeResourceWrapperHeader(candidate->first_words) &&
      !IsTexturePayloadBankCandidate(candidate->candidate)) {
    candidate->score = 2;
    candidate->reason = "resource_header";
    return;
  }
  if (IsTexturePayloadBankCandidate(candidate->candidate)) {
    candidate->score = 40;
    candidate->reason = "payload_without_header";
    return;
  }
  candidate->score = 20;
  candidate->reason = "texture_unknown";
}

std::filesystem::path ManifestEntryHostRelativePath(
    const BankManifestEntry& entry) {
  std::string relative_entry_path = entry.host_relative_path.empty()
                                        ? entry.relative_path
                                        : entry.host_relative_path;
  std::replace(relative_entry_path.begin(), relative_entry_path.end(), '\\', '/');
  return entry.manifest_root / std::filesystem::path(relative_entry_path);
}

bool ResolveBankRootedVfsDatabaseCandidate(
    const std::filesystem::path& loose_root,
    std::string_view normalized_candidate,
    RefiiVfsDatabaseCandidateResolutionCacheEntry* out_entry) {
  if (out_entry == nullptr || normalized_candidate.empty()) {
    return false;
  }

  std::vector<RefiiVfsRuntimeBankRecord> bank_records =
      GetRefiiVfsRuntimeBankRecordsCached(loose_root);
  if (bank_records.empty()) {
    return false;
  }

  std::sort(bank_records.begin(),
            bank_records.end(),
            [](const RefiiVfsRuntimeBankRecord& left,
               const RefiiVfsRuntimeBankRecord& right) {
              return left.manifest_root.size() > right.manifest_root.size();
            });

  std::unordered_set<std::string> visited_roots;
  const std::string candidate_key = std::string(normalized_candidate);
  for (const RefiiVfsRuntimeBankRecord& record : bank_records) {
    const std::string manifest_root =
        NormalizeLooseAssetKey(record.manifest_root);
    if (manifest_root.empty() || !visited_roots.insert(manifest_root).second) {
      continue;
    }
    if (candidate_key.size() <= manifest_root.size() ||
        candidate_key[manifest_root.size()] != '/' ||
        !StartsWith(candidate_key, manifest_root)) {
      continue;
    }

    const std::string entry_key =
        candidate_key.substr(manifest_root.size() + 1);
    BankManifestEntry manifest_entry;
    if (!LookupCachedBankManifestEntryByName(
            manifest_root, entry_key, &manifest_entry)) {
      continue;
    }

    const std::filesystem::path relative_path =
        ManifestEntryHostRelativePath(manifest_entry);
    std::filesystem::path host_path = loose_root / relative_path;
    if (!IsRegularHostFile(host_path)) {
      host_path = ResolveCaseInsensitiveExistingPath(loose_root, relative_path);
    }
    if (!IsRegularHostFile(host_path)) {
      continue;
    }

    out_entry->found = true;
    out_entry->host_path = host_path.lexically_normal();
    out_entry->provider_relative_path = candidate_key;
    out_entry->candidate = candidate_key;
    out_entry->repair_source =
        fmt::format("manifest_root={};entry={}",
                    manifest_root,
                    NormalizeLooseAssetKey(manifest_entry.relative_path));
    return true;
  }

  return false;
}

RefiiVfsDatabaseCandidateResolutionCacheEntry ResolveVfsDatabaseCandidateHostPath(
    const std::filesystem::path& loose_root,
    std::string_view normalized_candidate) {
  const std::string candidate_key = std::string(normalized_candidate);
  const std::string root_key = loose_root.lexically_normal().string();
  struct Cache {
    std::string root_key;
    std::unordered_map<std::string, RefiiVfsDatabaseCandidateResolutionCacheEntry>
        by_candidate;
  };
  static std::mutex mutex;
  static Cache cache;

  {
    std::lock_guard<std::mutex> lock(mutex);
    if (cache.root_key != root_key) {
      cache = Cache{};
      cache.root_key = root_key;
    }
    const auto cached = cache.by_candidate.find(candidate_key);
    if (cached != cache.by_candidate.end()) {
      return cached->second;
    }
  }

  RefiiVfsDatabaseCandidateResolutionCacheEntry resolved;
  resolved.candidate = candidate_key;
  resolved.provider_relative_path = candidate_key;

  std::vector<std::string> candidates;
  AddUniqueCandidate(&candidates, candidate_key);
  for (std::string repaired :
       BuildExtractedTextureLayoutRepairCandidates(candidate_key)) {
    AddUniqueCandidate(&candidates, std::move(repaired));
  }

  for (const std::string& candidate : candidates) {
    const std::filesystem::path direct_path = loose_root / candidate;
    if (IsRegularHostFile(direct_path)) {
      resolved.found = true;
      resolved.candidate = candidate;
      resolved.provider_relative_path = candidate;
      resolved.host_path = direct_path.lexically_normal();
      resolved.repair_source =
          candidate == candidate_key ? "direct" : "extracted_texture_layout";
      break;
    }
    if (ResolveBankRootedVfsDatabaseCandidate(
            loose_root, candidate, &resolved)) {
      if (candidate != candidate_key) {
        resolved.repair_source =
            std::string("extracted_texture_layout;") + resolved.repair_source;
      }
      break;
    }
    const std::filesystem::path case_path =
        ResolveCaseInsensitiveExistingPath(loose_root, candidate);
    if (IsRegularHostFile(case_path)) {
      resolved.found = true;
      resolved.candidate = candidate;
      resolved.provider_relative_path = candidate;
      resolved.host_path = case_path.lexically_normal();
      resolved.repair_source = candidate == candidate_key
                                   ? "case_insensitive"
                                   : "extracted_texture_layout_case";
      break;
    }
  }

  {
    std::lock_guard<std::mutex> lock(mutex);
    if (cache.root_key != root_key) {
      cache = Cache{};
      cache.root_key = root_key;
    }
    constexpr size_t kMaxCandidateResolutionCacheEntries = 131072;
    if (cache.by_candidate.size() > kMaxCandidateResolutionCacheEntries) {
      cache.by_candidate.clear();
    }
    cache.by_candidate.emplace(candidate_key, resolved);
  }

  return resolved;
}

bool ResolveConcreteLuaLooseAsset(std::string_view asset_path,
                                  LooseAssetResolution* out_resolution,
                                  std::string* out_candidate,
                                  std::string* out_source) {
  if (!LooseLuaOverridesEnabled() || !IsLuaScriptAssetKey(asset_path)) {
    return false;
  }

  const std::filesystem::path loose_root = GetLooseAssetRoot();
  if (loose_root.empty()) {
    return false;
  }

  std::vector<std::string> aliases = BuildLuaVfsCarrierCandidates(asset_path);
  AddLuaVfsCarrierCandidate(aliases, std::string(asset_path));

  auto claim_candidate = [&](std::string_view candidate,
                             std::string_view source) {
    const std::string normalized_candidate = NormalizeLooseAssetKey(candidate);
    if (normalized_candidate.empty() ||
        IsInternalLooseAssetKey(normalized_candidate)) {
      return false;
    }

    const RefiiVfsDatabaseCandidateResolutionCacheEntry resolved =
        ResolveVfsDatabaseCandidateHostPath(loose_root, normalized_candidate);
    if (!resolved.found) {
      return false;
    }

    if (out_resolution != nullptr) {
      out_resolution->host_path = resolved.host_path;
      out_resolution->normalized_key = NormalizeLooseAssetKey(asset_path);
      out_resolution->provider_relative_path =
          resolved.provider_relative_path.empty()
              ? normalized_candidate
              : resolved.provider_relative_path;
    }
    if (out_candidate != nullptr) {
      *out_candidate =
          resolved.candidate.empty() ? normalized_candidate : resolved.candidate;
    }
    if (out_source != nullptr) {
      *out_source = std::string(source);
    }
    return true;
  };

  for (const std::string& alias : aliases) {
    if (claim_candidate(alias, "lua_decompiled_override")) {
      return true;
    }
  }

  if (!RefiiVfsDatabaseExists(loose_root)) {
    return false;
  }

  for (const std::string& alias : aliases) {
    const std::string normalized_alias = NormalizeLooseAssetKey(alias);
    if (normalized_alias.empty()) {
      continue;
    }
    if (!PrimeRefiiVfsRuntimeExactAliasCandidate(loose_root,
                                                 normalized_alias)) {
      PrimeRefiiVfsDatabaseAliasCandidates(
          loose_root, std::vector<std::string>{normalized_alias});
    }
    for (const std::string& candidate :
         LookupRefiiVfsDatabaseAliasCandidates(normalized_alias, loose_root)) {
      if (claim_candidate(candidate, "lua_decompiled_database_alias")) {
        return true;
      }
    }
  }

  return false;
}

bool ResolveRefiiLuaScriptAsset(std::string_view asset_path,
                                LooseAssetResolution* out_resolution,
                                std::string* out_candidate,
                                std::string* out_source) {
  std::string source;
  if (ResolveLuaVfsOverride(asset_path, out_resolution, out_candidate)) {
    if (out_source != nullptr) {
      *out_source = "lua_decompiled_override";
    }
    return true;
  }
  if (LooseLuaOverridesEnabled() &&
      ResolveConcreteLuaLooseAsset(
          asset_path, out_resolution, out_candidate, &source)) {
    if (out_source != nullptr) {
      *out_source = source.empty() ? "lua_decompiled_override" : source;
    }
    return true;
  }
  if (!LuaVfsBridgeEnabled()) {
    return false;
  }
  if (ResolveExtractedLuaSourceAsset(asset_path, out_resolution, out_candidate)) {
    if (out_source != nullptr) {
      *out_source = LooseLuaOverridesEnabled()
                        ? "lua_extracted_source_fallback"
                        : "lua_extracted_source";
    }
    return true;
  }
  if (ResolveOriginalDataLuaAsset(asset_path, out_resolution, out_candidate)) {
    if (out_source != nullptr) {
      *out_source = "lua_original_data_fallback";
    }
    return true;
  }
  return false;
}

bool MakeVfsDatabaseCandidateResolution(
    const std::filesystem::path& loose_root,
    std::string_view candidate,
    LooseAssetResolution* out_resolution,
    std::string* out_candidate) {
  std::string normalized_candidate = NormalizeLooseAssetKey(candidate);
  const std::string mip0_redirect =
      CanonicalizeMip0TextureHeaderRedirect(normalized_candidate);
  if (!mip0_redirect.empty()) {
    normalized_candidate = mip0_redirect;
  }
  if (normalized_candidate.empty() ||
      ShouldSkipLooseNativeAsset(normalized_candidate, "vfs_database_candidate")) {
    return false;
  }
  if (EndsWith(normalized_candidate, ".bnk")) {
    if (FindSyntheticBankManifest(normalized_candidate, nullptr, nullptr)) {
      return false;
    }
    std::error_code error;
    if (!std::filesystem::is_regular_file(loose_root / normalized_candidate,
                                          error)) {
      return false;
    }
  }

  const RefiiVfsDatabaseCandidateResolutionCacheEntry resolved =
      ResolveVfsDatabaseCandidateHostPath(loose_root, normalized_candidate);
  if (!resolved.found) {
    if (VfsTracingEnabled() &&
        (IsTraceInterestingAsset(normalized_candidate) ||
         ShouldLogRepeatedVfsTraceOperation("VfsDatabaseCandidateStale", 64))) {
      REXLOG_WARN("Refii VFS database candidate skipped: host file missing "
                  "candidate={} root={}",
                  normalized_candidate,
                  loose_root.string());
    }
    return false;
  }

  if (VfsTracingEnabled() && resolved.repair_source != "direct" &&
      (IsTraceInterestingAsset(normalized_candidate) ||
       ShouldLogRepeatedVfsTraceOperation("VfsDatabaseCandidateRepair", 64))) {
    REXLOG_INFO("Refii VFS database candidate repaired candidate={} "
                "provider_relative={} host={} source={}",
                normalized_candidate,
                resolved.provider_relative_path,
                resolved.host_path.string(),
                resolved.repair_source);
  }

  if (out_resolution != nullptr) {
    out_resolution->host_path = resolved.host_path;
    out_resolution->normalized_key = normalized_candidate;
    out_resolution->provider_relative_path = resolved.provider_relative_path;
  }
  if (out_candidate != nullptr) {
    *out_candidate = resolved.candidate.empty() ? normalized_candidate
                                                : resolved.candidate;
  }
  return true;
}

bool ResolvePairedTexturePayloadForHeader(std::string_view header_key,
                                          LooseAssetResolution* out_resolution,
                                          std::string* out_candidate) {
  const std::optional<std::string> paired_payload =
      DerivePairedTexturePayloadCandidate(header_key);
  if (!paired_payload.has_value()) {
    return false;
  }

  const std::filesystem::path loose_root = GetLooseAssetRoot();
  bool resolved = MakeVfsDatabaseCandidateResolution(loose_root,
                                                     *paired_payload,
                                                     out_resolution,
                                                     out_candidate);
  if (!resolved) {
    resolved = ResolveSyntheticBankManifestLooseAsset(
        *paired_payload, out_resolution, out_candidate);
  }
  if (!resolved) {
    return false;
  }

  if (out_candidate != nullptr && out_candidate->empty()) {
    *out_candidate = *paired_payload;
  }
  return true;
}

void AddPairedTextureHeaderCandidateIfPresent(
    const std::filesystem::path& loose_root,
    std::string_view payload_candidate,
    std::unordered_set<std::string>* seen_candidates,
    std::vector<ResolvedVfsHashCandidate>* resolved_candidates) {
  if (seen_candidates == nullptr || resolved_candidates == nullptr) {
    return;
  }
  const std::optional<std::string> paired_header =
      DerivePairedTextureHeaderCandidate(payload_candidate);
  if (!paired_header.has_value() ||
      !seen_candidates->insert(*paired_header).second) {
    return;
  }

  LooseAssetResolution resolution;
  std::string repaired_candidate;
  bool resolved = MakeVfsDatabaseCandidateResolution(loose_root,
                                                     *paired_header,
                                                     &resolution,
                                                     &repaired_candidate);
  if (!resolved) {
    resolved = ResolveSyntheticBankManifestLooseAsset(
        *paired_header, &resolution, &repaired_candidate);
  }
  if (!resolved || !IsTextureAssetCandidate(repaired_candidate)) {
    return;
  }

  ResolvedVfsHashCandidate resolved_candidate;
  resolved_candidate.resolution = std::move(resolution);
  resolved_candidate.original_candidate = *paired_header;
  resolved_candidate.candidate =
      repaired_candidate.empty() ? *paired_header : repaired_candidate;
  resolved_candidates->push_back(std::move(resolved_candidate));
}

bool ResolveVfsDatabaseAliasLooseAsset(std::string_view asset_path,
                                       LooseAssetResolution* out_resolution,
                                       std::string* out_candidate) {
  if (asset_path.empty() || !ShouldQueryVfsDatabaseAlias(asset_path)) {
    return false;
  }

  const std::filesystem::path loose_root = GetLooseAssetRoot();
  if (RefiiVfsDatabaseExists(loose_root)) {
    const std::string root_key = loose_root.lexically_normal().string();
    const std::string alias_key = NormalizeLooseAssetKey(asset_path);
    struct AliasResolutionCache {
      std::string root_key;
      std::unordered_map<std::string, CachedBankLooseAssetResolution> by_alias;
    };
    static std::mutex alias_resolution_mutex;
    static AliasResolutionCache alias_resolution_cache;

    {
      std::lock_guard<std::mutex> lock(alias_resolution_mutex);
      if (alias_resolution_cache.root_key != root_key) {
        alias_resolution_cache = AliasResolutionCache{};
        alias_resolution_cache.root_key = root_key;
      }
      const auto cached = alias_resolution_cache.by_alias.find(alias_key);
      if (cached != alias_resolution_cache.by_alias.end()) {
        if (cached->second.found) {
          if (out_resolution != nullptr) {
            *out_resolution = cached->second.resolution;
          }
          if (out_candidate != nullptr) {
            *out_candidate = cached->second.candidate;
          }
        }
        return cached->second.found;
      }
    }

    CachedBankLooseAssetResolution cached_resolution;
    for (const std::string& candidate :
         LookupRefiiVfsDatabaseAliasCandidates(asset_path, loose_root)) {
      LooseAssetResolution resolution;
      std::string normalized_candidate;
      bool resolved = MakeVfsDatabaseCandidateResolution(loose_root,
                                                         candidate,
                                                         &resolution,
                                                         &normalized_candidate);
      if (!resolved) {
        resolved = ResolveSyntheticBankManifestLooseAsset(
            candidate, &resolution, &normalized_candidate);
      }
      if (!resolved) {
        continue;
      }

      cached_resolution.found = true;
      cached_resolution.resolution = resolution;
      cached_resolution.candidate = normalized_candidate;
      if (VfsTracingEnabled() &&
          (IsTraceInterestingAsset(asset_path) ||
           ShouldLogRepeatedVfsTraceOperation("VfsDatabaseAlias", 64))) {
        REXLOG_INFO("Refii VFS database alias hit alias={} candidate={} "
                    "provider_relative={} host={}",
                    asset_path,
                    normalized_candidate,
                    resolution.provider_relative_path,
                    resolution.host_path.string());
      }

      if (out_resolution != nullptr) {
        *out_resolution = std::move(resolution);
      }
      if (out_candidate != nullptr) {
        *out_candidate = std::move(normalized_candidate);
      }
      {
        std::lock_guard<std::mutex> lock(alias_resolution_mutex);
        if (alias_resolution_cache.root_key != root_key) {
          alias_resolution_cache = AliasResolutionCache{};
          alias_resolution_cache.root_key = root_key;
        }
        constexpr size_t kMaxAliasResolutionCacheEntries = 65536;
        if (alias_resolution_cache.by_alias.size() >
            kMaxAliasResolutionCacheEntries) {
          alias_resolution_cache.by_alias.clear();
        }
        alias_resolution_cache.by_alias.emplace(alias_key,
                                                std::move(cached_resolution));
      }
      return true;
    }
    {
      std::lock_guard<std::mutex> lock(alias_resolution_mutex);
      if (alias_resolution_cache.root_key != root_key) {
        alias_resolution_cache = AliasResolutionCache{};
        alias_resolution_cache.root_key = root_key;
      }
      constexpr size_t kMaxAliasResolutionCacheEntries = 65536;
      if (alias_resolution_cache.by_alias.size() >
          kMaxAliasResolutionCacheEntries) {
        alias_resolution_cache.by_alias.clear();
      }
      alias_resolution_cache.by_alias.emplace(alias_key,
                                              CachedBankLooseAssetResolution{});
    }
    return false;
  }

  const auto resource_index = GetRefiiResourceHashIndex();
  if (resource_index == nullptr ||
      resource_index->candidates_by_alias.empty()) {
    return false;
  }

  for (const std::string& alias : BuildSyntheticVfsEntryCandidates(asset_path)) {
    const auto match = resource_index->candidates_by_alias.find(alias);
    if (match == resource_index->candidates_by_alias.end()) {
      continue;
    }

    for (const std::string& candidate : match->second) {
      LooseAssetResolution resolution;
      std::string normalized_candidate;
      bool resolved = MakeVfsDatabaseCandidateResolution(loose_root,
                                                         candidate,
                                                         &resolution,
                                                         &normalized_candidate);
      if (!resolved) {
        resolved = ResolveSyntheticBankManifestLooseAsset(
            candidate, &resolution, &normalized_candidate);
      }
      if (!resolved) {
        continue;
      }

      if (VfsTracingEnabled() &&
          (IsTraceInterestingAsset(asset_path) ||
           ShouldLogVfsTraceOperation("VfsDatabaseAlias"))) {
        REXLOG_INFO("Refii VFS database alias hit alias={} candidate={} "
                    "provider_relative={} host={}",
                    asset_path,
                    normalized_candidate,
                    resolution.provider_relative_path,
                    resolution.host_path.string());
      }

      if (out_resolution != nullptr) {
        *out_resolution = std::move(resolution);
      }
      if (out_candidate != nullptr) {
        *out_candidate = std::move(normalized_candidate);
      }
      return true;
    }
  }
  return false;
}

bool ResolveVfsDatabaseAliasLooseAssetForResourceManager(
    std::string_view asset_path,
    uint32_t caller_lr,
    LooseAssetResolution* out_resolution,
    std::string* out_candidate) {
  if (asset_path.empty() || !IsTextureAssetCandidate(asset_path) ||
      !ShouldQueryVfsDatabaseAlias(asset_path)) {
    return false;
  }

  const std::filesystem::path loose_root = GetLooseAssetRoot();
  if (!RefiiVfsDatabaseExists(loose_root)) {
    return false;
  }

  const std::string root_key = loose_root.lexically_normal().string();
  const std::string alias_key = NormalizeLooseAssetKey(asset_path);
  struct ResourceManagerTextureAliasCacheEntry {
    bool found = false;
    LooseAssetResolution resolution;
    std::string candidate;
  };
  struct ResourceManagerTextureAliasCache {
    std::string root_key;
    std::unordered_map<std::string, ResourceManagerTextureAliasCacheEntry>
        by_alias;
  };
  static std::mutex cache_mutex;
  static ResourceManagerTextureAliasCache cache;
  {
    std::lock_guard<std::mutex> lock(cache_mutex);
    if (cache.root_key != root_key) {
      cache = ResourceManagerTextureAliasCache{};
      cache.root_key = root_key;
    }
    const auto cached = cache.by_alias.find(alias_key);
    if (cached != cache.by_alias.end()) {
      if (cached->second.found) {
        if (out_resolution != nullptr) {
          *out_resolution = cached->second.resolution;
        }
        if (out_candidate != nullptr) {
          *out_candidate = cached->second.candidate;
        }
      }
      return cached->second.found;
    }
  }

  std::vector<ResolvedVfsHashCandidate> resolved_candidates;
  std::unordered_set<std::string> seen_candidates;
  for (const std::string& candidate :
       LookupRefiiVfsDatabaseAliasCandidates(asset_path, loose_root)) {
    const std::string normalized_input_candidate =
        NormalizeLooseAssetKey(candidate);
    if (normalized_input_candidate.empty() ||
        !seen_candidates.insert(normalized_input_candidate).second) {
      continue;
    }

    LooseAssetResolution resolution;
    std::string normalized_candidate;
    bool resolved = MakeVfsDatabaseCandidateResolution(loose_root,
                                                       candidate,
                                                       &resolution,
                                                       &normalized_candidate);
    if (!resolved) {
      resolved = ResolveSyntheticBankManifestLooseAsset(
          candidate, &resolution, &normalized_candidate);
    }
    if (!resolved || !IsTextureAssetCandidate(normalized_candidate)) {
      continue;
    }

    ResolvedVfsHashCandidate resolved_candidate;
    resolved_candidate.resolution = std::move(resolution);
    resolved_candidate.original_candidate = normalized_input_candidate;
    resolved_candidate.candidate =
        normalized_candidate.empty() ? resolved_candidate.original_candidate
                                     : normalized_candidate;
    resolved_candidates.push_back(std::move(resolved_candidate));
    AddPairedTextureHeaderCandidateIfPresent(loose_root,
                                             normalized_input_candidate,
                                             &seen_candidates,
                                             &resolved_candidates);
  }

  if (!SelectResolvedVfsHashCandidate(0,
                                      &resolved_candidates,
                                      true,
                                      caller_lr,
                                      out_resolution,
                                      out_candidate)) {
    std::lock_guard<std::mutex> lock(cache_mutex);
    if (cache.root_key != root_key) {
      cache = ResourceManagerTextureAliasCache{};
      cache.root_key = root_key;
    }
    constexpr size_t kMaxResourceManagerTextureAliasCacheEntries = 16384;
    if (cache.by_alias.size() > kMaxResourceManagerTextureAliasCacheEntries) {
      cache.by_alias.clear();
    }
    cache.by_alias.emplace(alias_key, ResourceManagerTextureAliasCacheEntry{});
    return false;
  }

  if (VfsTracingEnabled() &&
      (IsTraceInterestingAsset(asset_path) ||
       ShouldLogRepeatedVfsTraceOperation("ResourceTextureAliasShape", 64))) {
    REXLOG_INFO("Refii VFS ResourceManager texture alias selected "
                "alias={} caller_lr=0x{:08X} candidate={} host={}",
                NormalizeLooseAssetKey(asset_path),
                caller_lr,
                out_candidate == nullptr ? "" : *out_candidate,
                out_resolution == nullptr ? "" : out_resolution->host_path.string());
  }
  if (out_resolution != nullptr && out_candidate != nullptr) {
    ResourceManagerTextureAliasCacheEntry entry;
    entry.found = true;
    entry.resolution = *out_resolution;
    entry.candidate = *out_candidate;
    std::lock_guard<std::mutex> lock(cache_mutex);
    if (cache.root_key != root_key) {
      cache = ResourceManagerTextureAliasCache{};
      cache.root_key = root_key;
    }
    constexpr size_t kMaxResourceManagerTextureAliasCacheEntries = 16384;
    if (cache.by_alias.size() > kMaxResourceManagerTextureAliasCacheEntries) {
      cache.by_alias.clear();
    }
    cache.by_alias.emplace(alias_key, std::move(entry));
  }
  return true;
}

bool ResolveVfsDatabaseExactAliasLooseAsset(std::string_view asset_path,
                                            LooseAssetResolution* out_resolution,
                                            std::string* out_candidate) {
  if (asset_path.empty() || !ShouldQueryVfsDatabaseAlias(asset_path)) {
    return false;
  }

  const std::filesystem::path loose_root = GetLooseAssetRoot();
  if (!RefiiVfsDatabaseExists(loose_root)) {
    return false;
  }

  const std::string alias_key = NormalizeLooseAssetKey(asset_path);
  if (alias_key.empty()) {
    return false;
  }

  if (!PrimeRefiiVfsRuntimeExactAliasCandidate(loose_root, alias_key)) {
    PrimeRefiiVfsDatabaseAliasCandidates(
        loose_root, std::vector<std::string>{alias_key});
  }

  std::vector<std::string> candidates;
  {
    std::lock_guard<std::mutex> lock(
        RefiiVfsDatabaseAliasCandidateCacheMutex());
    const auto& cache = RefiiVfsDatabaseAliasCandidateCacheInstance();
    if (cache.root_key != loose_root.lexically_normal().string()) {
      return false;
    }
    const auto match = cache.candidates_by_alias.find(alias_key);
    if (match == cache.candidates_by_alias.end()) {
      return false;
    }
    candidates = match->second;
  }
  const std::vector<std::string> alias_score_keys{alias_key};
  std::stable_sort(candidates.begin(),
                   candidates.end(),
                   [&](const std::string& lhs, const std::string& rhs) {
                     return ScoreVfsDatabaseAliasCandidate(lhs, alias_score_keys) <
                            ScoreVfsDatabaseAliasCandidate(rhs, alias_score_keys);
                   });

  for (const std::string& candidate : candidates) {
    LooseAssetResolution resolution;
    std::string normalized_candidate;
    bool resolved = MakeVfsDatabaseCandidateResolution(loose_root,
                                                       candidate,
                                                       &resolution,
                                                       &normalized_candidate);
    if (!resolved) {
      resolved = ResolveSyntheticBankManifestLooseAsset(
          candidate, &resolution, &normalized_candidate);
    }
    if (!resolved) {
      continue;
    }

    if (VfsTracingEnabled() &&
        (IsTraceInterestingAsset(asset_path) ||
         ShouldLogRepeatedVfsTraceOperation("VfsDatabaseExactAlias", 64))) {
      REXLOG_INFO("Refii VFS exact database alias hit alias={} candidate={} "
                  "provider_relative={} host={}",
                  alias_key,
                  normalized_candidate,
                  resolution.provider_relative_path,
                  resolution.host_path.string());
    }

    if (out_resolution != nullptr) {
      *out_resolution = std::move(resolution);
    }
    if (out_candidate != nullptr) {
      *out_candidate = std::move(normalized_candidate);
    }
    return true;
  }

  return false;
}

void MarkPairedTextureHeaderAvailability(
    std::vector<ResolvedVfsHashCandidate>* resolved_candidates) {
  if (resolved_candidates == nullptr || resolved_candidates->empty()) {
    return;
  }

  std::unordered_set<std::string> candidate_keys;
  candidate_keys.reserve(resolved_candidates->size());
  for (const ResolvedVfsHashCandidate& candidate : *resolved_candidates) {
    candidate_keys.insert(NormalizeLooseAssetKey(candidate.candidate));
  }

  for (ResolvedVfsHashCandidate& candidate : *resolved_candidates) {
    const std::optional<std::string> paired_header =
        DerivePairedTextureHeaderCandidate(candidate.candidate);
    candidate.paired_header_available =
        paired_header.has_value() &&
        candidate_keys.find(*paired_header) != candidate_keys.end();
  }
}

bool SelectResolvedVfsHashCandidate(
    uint32_t resource_hash,
    std::vector<ResolvedVfsHashCandidate>* resolved_candidates,
    bool prefer_resource_manager_texture_shape,
    uint32_t caller_lr,
    LooseAssetResolution* out_resolution,
    std::string* out_candidate) {
  if (resolved_candidates == nullptr || resolved_candidates->empty()) {
    return false;
  }

  auto selected = resolved_candidates->begin();
  const bool score_texture_candidates =
      prefer_resource_manager_texture_shape &&
      std::any_of(resolved_candidates->begin(),
                  resolved_candidates->end(),
                  [](const ResolvedVfsHashCandidate& candidate) {
                    return IsTextureAssetCandidate(candidate.candidate);
                  });

  if (score_texture_candidates) {
    MarkPairedTextureHeaderAvailability(resolved_candidates);
    for (ResolvedVfsHashCandidate& candidate : *resolved_candidates) {
      ScoreResourceManagerTextureCandidate(&candidate);
    }
    selected = std::min_element(
        resolved_candidates->begin(),
        resolved_candidates->end(),
        [](const ResolvedVfsHashCandidate& left,
           const ResolvedVfsHashCandidate& right) {
          if (left.score != right.score) {
            return left.score < right.score;
          }
          return left.candidate < right.candidate;
        });

    if (VfsTracingEnabled() &&
        ShouldLogRepeatedVfsTraceOperation(
            fmt::format("ResourceTextureCandidateShape:{:08X}:{}",
                        resource_hash,
                        NormalizeLooseAssetKey(selected->candidate)),
            IsTraceInterestingAsset(selected->candidate) ? 32 : 96)) {
      std::vector<std::string> candidate_names;
      candidate_names.reserve(resolved_candidates->size());
      for (const ResolvedVfsHashCandidate& candidate : *resolved_candidates) {
        candidate_names.push_back(
            fmt::format("{}:{}:{}:paired_header={}",
                        candidate.candidate,
                        candidate.reason.empty() ? "unscored" : candidate.reason,
                        FormatHostFileFirstWords(candidate.first_words),
                        candidate.paired_header_available));
      }
      REXLOG_INFO("Refii VFS ResourceManager texture candidate selected "
                  "hash=0x{:08X} caller_lr=0x{:08X} selected={} "
                  "reason={} score={} size={} first_words={} candidates=[{}]",
                  resource_hash,
                  caller_lr,
                  selected->candidate,
                  selected->reason,
                  selected->score,
                  selected->file_size,
                  FormatHostFileFirstWords(selected->first_words),
                  FormatCandidateSample(candidate_names, 6));
    }
  }

  if (VfsTracingEnabled() &&
      ShouldLogRepeatedVfsTraceOperation(
          fmt::format("ResourceHashDatabaseHit:{:08X}:{}",
                      resource_hash,
                      NormalizeLooseAssetKey(selected->candidate)),
          IsTraceInterestingAsset(selected->candidate) ? 32 : 64)) {
    REXLOG_INFO("Refii VFS resource hash database hit hash=0x{:08X} "
                "candidate={} provider_relative={} host={} mode=lazy_hash",
                resource_hash,
                selected->candidate,
                selected->resolution.provider_relative_path,
                selected->resolution.host_path.string());
  }
  if (out_resolution != nullptr) {
    *out_resolution = std::move(selected->resolution);
  }
  if (out_candidate != nullptr) {
    *out_candidate = std::move(selected->candidate);
  }
  return true;
}


}  // namespace detail
}  // namespace refii::vfs
