#include "vfs_hooks_private.h"

#include "core/host_asset_reader.h"

namespace refii::vfs {
inline namespace detail {
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

uint64_t HostFileSize(const LooseAssetResolution& resolution) {
  if (resolution.host_served) {
    uint32_t size = 0;
    return HostAssetSize(resolution.normalized_key, size) ? size : 0;
  }

  // The path overload — typed local so this cannot become a recursive call.
  const std::filesystem::path& disk_path = resolution.host_path;
  return HostFileSize(disk_path);
}

std::array<uint32_t, 4> ReadHostFileFirstWords(
    const LooseAssetResolution& resolution) {
  if (!resolution.host_served) {
    // The path overload — typed local, see HostFileSize.
    const std::filesystem::path& disk_path = resolution.host_path;
    return ReadHostFileFirstWords(disk_path);
  }

  std::array<uint32_t, 4> words{};
  std::vector<uint8_t> prefix;
  if (!ReadHostAssetPrefix(resolution.normalized_key, 16u, prefix)) {
    return words;
  }

  // Short is not an error: a file smaller than the window leaves the remaining words zero,
  // as the disk path does.
  for (size_t word = 0; word < words.size(); ++word) {
    if (prefix.size() < (word + 1) * 4) {
      break;
    }
    words[word] = LoadBigEndianU32(prefix.data() + word * 4);
  }

  return words;
}

bool ReadResolutionBytes(const LooseAssetResolution& resolution, uint32_t count,
                         std::vector<uint8_t>& out) {
  out.clear();

  if (resolution.host_served) {
    return count == 0u
               ? ReadHostAsset(resolution.normalized_key, out)
               : ReadHostAssetPrefix(resolution.normalized_key, count, out);
  }

  std::ifstream input(resolution.host_path, std::ios::binary);
  if (!input) {
    return false;
  }

  if (count == 0u) {
    out.assign(std::istreambuf_iterator<char>(input),
               std::istreambuf_iterator<char>());
    return true;
  }

  out.resize(count);
  input.read(reinterpret_cast<char*>(out.data()), count);

  // Short is not a failure; the caller decides, same as the host-served side.
  out.resize(static_cast<size_t>(input.gcount()));
  return true;
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

std::filesystem::path ManifestEntryHostRelativePath(
    const BankManifestEntry& entry) {
  std::string relative_entry_path = entry.host_relative_path.empty()
                                        ? entry.relative_path
                                        : entry.host_relative_path;
  std::replace(relative_entry_path.begin(), relative_entry_path.end(), '\\', '/');
  if (!entry.host_relative_path.empty()) {
    return std::filesystem::path(relative_entry_path);
  }
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
  const std::filesystem::path base_root = GetLooseAssetRoot().lexically_normal();
  if (loose_root.lexically_normal() != base_root) {
    const std::vector<RefiiVfsRuntimeBankRecord> base_bank_records =
        GetRefiiVfsRuntimeBankRecordsCached(base_root);
    bank_records.insert(bank_records.end(),
                        base_bank_records.begin(),
                        base_bank_records.end());
  }
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

bool MakeVfsDatabaseCandidateResolutionAtRoot(
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

bool MakeVfsDatabaseCandidateResolution(
    const std::filesystem::path& loose_root,
    std::string_view candidate,
    LooseAssetResolution* out_resolution,
    std::string* out_candidate) {
  if (loose_root.empty()) {
    return false;
  }

  const std::filesystem::path base_root = GetLooseAssetRoot().lexically_normal();
  const std::filesystem::path requested_root = loose_root.lexically_normal();
  if (requested_root != base_root) {
    return MakeVfsDatabaseCandidateResolutionAtRoot(
        loose_root, candidate, out_resolution, out_candidate);
  }

  for (const std::filesystem::path& root : GetLooseAssetSearchRoots()) {
    if (root.empty()) {
      continue;
    }
    if (MakeVfsDatabaseCandidateResolutionAtRoot(
            root, candidate, out_resolution, out_candidate)) {
      return true;
    }
  }
  return false;
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
}  // namespace detail
}  // namespace refii::vfs
