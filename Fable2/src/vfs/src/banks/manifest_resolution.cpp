#include "vfs_hooks_private.h"

#include "core/installed_cultures.h"

namespace refii::vfs {
inline namespace detail {

void AddUnique(std::vector<std::string>& values, std::string value) {
  if (value.empty()) {
    return;
  }
  while (!value.empty() && value.front() == '/') {
    value.erase(value.begin());
  }
  if (value.empty()) {
    return;
  }
  if (std::find(values.begin(), values.end(), value) == values.end()) {
    values.push_back(std::move(value));
  }
}

std::string BankNameToLooseRoot(std::string_view bank_name) {
  std::string root = NormalizeLooseAssetKey(bank_name);
  if (StartsWith(root, "data/")) {
    root.erase(0, 5);
  }

  constexpr std::string_view kBankExtension = ".bnk";
  if (root.size() >= kBankExtension.size() &&
      root.substr(root.size() - kBankExtension.size()) == kBankExtension) {
    root.erase(root.size() - kBankExtension.size());
  }

  return root;
}

BankManifestIndex LoadBankManifestIndex(const std::filesystem::path& manifest_root,
                                        const std::filesystem::path& manifest_path) {
  BankManifestIndex index;
  std::ifstream manifest(manifest_path);
  if (!manifest) {
    return index;
  }

  std::string line;
  std::getline(manifest, line);
  while (std::getline(manifest, line)) {
    std::istringstream stream(line);
    std::string path;
    std::string offset_text;
    std::string size_text;
    std::string stored_size_text;
    std::string compressed_text;
    std::string chunk_sizes_text;
    std::string format_text;
    std::string host_path;
    if (!std::getline(stream, path, '\t') ||
        !std::getline(stream, offset_text, '\t') ||
        !std::getline(stream, size_text, '\t') || path.empty()) {
      continue;
    }
    std::getline(stream, stored_size_text, '\t');
    std::getline(stream, compressed_text, '\t');
    std::getline(stream, chunk_sizes_text, '\t');
    std::getline(stream, format_text, '\t');
    std::getline(stream, host_path, '\t');

    try {
      BankManifestEntry entry;
      entry.relative_path = std::move(path);
      entry.host_relative_path = host_path.empty() ? entry.relative_path
                                                   : std::move(host_path);
      entry.manifest_root = manifest_root;
      entry.offset = static_cast<uint32_t>(std::stoul(offset_text));
      entry.size = static_cast<uint32_t>(std::stoul(size_text));
      entry.stored_size = stored_size_text.empty()
                              ? entry.size
                              : static_cast<uint32_t>(
                                    std::stoul(stored_size_text));
      entry.compressed =
          !compressed_text.empty() && compressed_text != "0" &&
          compressed_text != "false" && compressed_text != "False";
      const size_t entry_index = index.entries.size();
      index.by_offset_size.try_emplace(
          MakeBankManifestLookupKey(entry.offset, entry.size),
          entry_index);
      if (entry.stored_size != entry.size) {
        index.by_offset_size.try_emplace(
            MakeBankManifestLookupKey(entry.offset, entry.stored_size),
            entry_index);
      }
      index.by_normalized_path.try_emplace(
          NormalizeLooseAssetKey(entry.relative_path),
          entry_index);
      index.entries.push_back(std::move(entry));
    } catch (const std::exception&) {
    }
  }

  return index;
}

bool ParseBankManifestEntryLine(const std::filesystem::path& manifest_root,
                                std::string_view line,
                                BankManifestEntry* out_entry) {
  std::istringstream stream{std::string(line)};
  std::string path;
  std::string offset_text;
  std::string size_text;
  std::string stored_size_text;
  std::string compressed_text;
  std::string chunk_sizes_text;
  std::string format_text;
  std::string host_path;
  if (!std::getline(stream, path, '\t') ||
      !std::getline(stream, offset_text, '\t') ||
      !std::getline(stream, size_text, '\t') || path.empty()) {
    return false;
  }
  std::getline(stream, stored_size_text, '\t');
  std::getline(stream, compressed_text, '\t');
  std::getline(stream, chunk_sizes_text, '\t');
  std::getline(stream, format_text, '\t');
  std::getline(stream, host_path, '\t');

  try {
    BankManifestEntry entry;
    entry.relative_path = std::move(path);
    entry.host_relative_path = host_path.empty() ? entry.relative_path
                                                 : std::move(host_path);
    entry.manifest_root = manifest_root;
    entry.offset = static_cast<uint32_t>(std::stoul(offset_text));
    entry.size = static_cast<uint32_t>(std::stoul(size_text));
    entry.stored_size = stored_size_text.empty()
                            ? entry.size
                            : static_cast<uint32_t>(std::stoul(stored_size_text));
    entry.compressed = !compressed_text.empty() && compressed_text != "0" &&
                       compressed_text != "false" &&
                       compressed_text != "False";
    if (out_entry != nullptr) {
      *out_entry = std::move(entry);
    }
    return true;
  } catch (const std::exception&) {
    return false;
  }
}

bool EqualsIgnoreAsciiCase(std::string_view left, std::string_view right) {
  if (left.size() != right.size()) {
    return false;
  }
  for (size_t index = 0; index < left.size(); ++index) {
    const unsigned char left_ch = static_cast<unsigned char>(left[index]);
    const unsigned char right_ch = static_cast<unsigned char>(right[index]);
    if (std::tolower(left_ch) != std::tolower(right_ch)) {
      return false;
    }
  }
  return true;
}

std::filesystem::path ResolveCaseInsensitiveExistingPath(
    const std::filesystem::path& root,
    const std::filesystem::path& relative_path) {
  std::filesystem::path resolved = root;
  std::error_code error;
  if (!std::filesystem::exists(resolved, error)) {
    return root / relative_path;
  }

  for (const std::filesystem::path& part : relative_path) {
    if (part.empty() || part == ".") {
      continue;
    }
    const std::string target = part.string();
    std::filesystem::path direct = resolved / part;
    error.clear();
    if (std::filesystem::exists(direct, error)) {
      resolved = std::move(direct);
      continue;
    }

    bool matched = false;
    error.clear();
    for (const std::filesystem::directory_entry& entry :
         std::filesystem::directory_iterator(resolved, error)) {
      if (error) {
        break;
      }
      if (EqualsIgnoreAsciiCase(entry.path().filename().string(), target)) {
        resolved = entry.path();
        matched = true;
        break;
      }
    }
    if (!matched) {
      return root / relative_path;
    }
  }

  return resolved;
}

std::filesystem::path ResolveBankManifestPath(
    const std::filesystem::path& manifest_root) {
  const std::filesystem::path relative_path = manifest_root / "_manifest.tsv";
  const std::vector<std::filesystem::path> roots = GetLooseAssetSearchRoots();
  for (const std::filesystem::path& root : roots) {
    if (root.empty()) {
      continue;
    }
    const std::filesystem::path direct_path = root / relative_path;
    std::error_code error;
    if (std::filesystem::is_regular_file(direct_path, error)) {
      return direct_path;
    }
  }

  for (const std::filesystem::path& root : roots) {
    if (root.empty()) {
      continue;
    }
    const std::filesystem::path insensitive_path =
        ResolveCaseInsensitiveExistingPath(root, relative_path);
    std::error_code error;
    if (std::filesystem::is_regular_file(insensitive_path, error)) {
      return insensitive_path;
    }
  }

  return GetLooseAssetRoot() / relative_path;
}

std::shared_ptr<const BankManifestIndex> LoadCachedBankManifestIndex(
    const std::filesystem::path& manifest_root,
    std::filesystem::path* out_resolved_root = nullptr) {
  static std::mutex mutex;
  static std::unordered_map<std::string, std::shared_ptr<const BankManifestIndex>>
      cache;

  std::lock_guard<std::mutex> lock(mutex);
  const std::filesystem::path manifest_path =
      ResolveBankManifestPath(manifest_root);
  const std::string cache_key = manifest_path.lexically_normal().string();
  auto [iterator, inserted] = cache.try_emplace(cache_key);
  if (inserted) {
    iterator->second = std::make_shared<BankManifestIndex>(
        LoadBankManifestIndex(manifest_root, manifest_path));
  }
  if (iterator->second == nullptr || iterator->second->entries.empty()) {
    return {};
  }
  if (out_resolved_root != nullptr) {
    *out_resolved_root = manifest_path.parent_path().lexically_relative(
        GetLooseAssetRoot());
    if (out_resolved_root->empty() || out_resolved_root->string().starts_with("..")) {
      *out_resolved_root = manifest_root;
    }
  }
  return iterator->second;
}

bool LookupCachedBankManifestEntryByName(
    const std::filesystem::path& manifest_root,
    std::string_view entry_name,
    BankManifestEntry* out_entry) {
  const std::shared_ptr<const BankManifestIndex> index =
      LoadCachedBankManifestIndex(manifest_root);
  if (index == nullptr) {
    return false;
  }
  const auto match =
      index->by_normalized_path.find(NormalizeLooseAssetKey(entry_name));
  if (match == index->by_normalized_path.end() ||
      match->second >= index->entries.size()) {
    return false;
  }
  if (out_entry != nullptr) {
    *out_entry = index->entries[match->second];
  }
  return true;
}

std::string CanonicalizeMip0TextureHeaderRedirect(std::string_view value) {
  const std::string normalized = NormalizeLooseAssetKey(value);
  constexpr std::string_view kAliasRoot = "globals/1024mip0_texture_headers";
  constexpr std::string_view kCanonicalRoot = "globals/1024mip0_textures";
  constexpr std::string_view kAliasBank =
      "globals/1024mip0_texture_headers.bnk";
  constexpr std::string_view kCanonicalBank =
      "globals/1024mip0_textures.bnk";

  if (normalized == "1024mip0_texture_headers") {
    return std::string(kCanonicalRoot);
  }
  if (normalized == "1024mip0_texture_headers.bnk") {
    return std::string(kCanonicalBank);
  }
  if (normalized == kAliasRoot) {
    return std::string(kCanonicalRoot);
  }
  if (normalized == kAliasBank) {
    return std::string(kCanonicalBank);
  }

  constexpr std::string_view kAliasPrefix =
      "globals/1024mip0_texture_headers/";
  if (StartsWith(normalized, kAliasPrefix)) {
    return std::string(kCanonicalRoot) + "/" +
           std::string(std::string_view(normalized).substr(kAliasPrefix.size()));
  }
  return {};
}

std::vector<std::filesystem::path> BuildBankManifestRootCandidates(
    const std::string& bank_root) {
  std::vector<std::filesystem::path> candidates;
  auto add_unique = [&candidates](std::filesystem::path path) {
    path = path.lexically_normal();
    if (path.empty()) {
      return;
    }
    if (std::find(candidates.begin(), candidates.end(), path) == candidates.end()) {
      candidates.push_back(std::move(path));
    }
  };

  add_unique(bank_root);
  add_unique(std::filesystem::path("refii_vfs") / "banks" /
             std::filesystem::path(bank_root));
  const std::string mip0_redirect =
      CanonicalizeMip0TextureHeaderRedirect(bank_root);
  if (!mip0_redirect.empty()) {
    add_unique(mip0_redirect);
    add_unique(std::filesystem::path("refii_vfs") / "banks" /
               std::filesystem::path(mip0_redirect));
  }

  constexpr std::string_view kLanguagePrefix = "language/";
  if (StartsWith(bank_root, kLanguagePrefix)) {
    const std::string_view tail =
        std::string_view(bank_root).substr(kLanguagePrefix.size());
    const size_t first_separator = tail.find('/');
    const std::string_view first_component =
        first_separator == std::string_view::npos
            ? tail
            : tail.substr(0, first_separator);
    const bool has_locale_component =
        first_component.find('-') != std::string_view::npos;
    if (!has_locale_component && first_separator != std::string_view::npos) {
      const std::string_view localized_tail = tail;
      for (const std::string& locale : InstalledLanguageCultures()) {
        add_unique(std::filesystem::path("language") /
                   std::string(locale) / std::string(localized_tail));
      }
    }
  }

  if (!StartsWith(bank_root, "levels/")) {
    add_unique(std::filesystem::path("levels") / bank_root);
  }
  if (!StartsWith(bank_root, "streaming/")) {
    add_unique(std::filesystem::path("streaming") / bank_root);
  }

  return candidates;
}

bool LookupRefiiVfsTypedBankManifest(std::string_view bank_name,
                                     std::filesystem::path* out_manifest_root,
                                     std::filesystem::path* out_manifest_path);

bool LookupResolvedSyntheticBankOpenAlias(std::string_view bank_alias,
                                          std::string* out_resolved_bank,
                                          std::string* out_reason);

bool FindSyntheticBankManifest(std::string_view bank_name,
                               std::filesystem::path* out_manifest_root,
                               std::filesystem::path* out_manifest_path) {
  const std::string normalized_bank = NormalizeLooseAssetKey(bank_name);
  if (normalized_bank.empty() || !EndsWith(normalized_bank, ".bnk")) {
    return false;
  }

  if (LookupRefiiVfsTypedBankManifest(
          normalized_bank, out_manifest_root, out_manifest_path)) {
    return true;
  }

  std::string resolved_bank_alias;
  std::string resolved_bank_alias_reason;
  if (LookupResolvedSyntheticBankOpenAlias(
          normalized_bank, &resolved_bank_alias, &resolved_bank_alias_reason) &&
      LookupRefiiVfsTypedBankManifest(
          resolved_bank_alias, out_manifest_root, out_manifest_path)) {
    if (VfsTracingEnabled() &&
        ShouldLogRepeatedVfsTraceOperation("SyntheticBankOpenAlias", 32)) {
      REXLOG_INFO("Refii synthetic bank open alias bank={} resolved={} "
                  "reason={}",
                  normalized_bank,
                  resolved_bank_alias,
                  resolved_bank_alias_reason);
    }
    return true;
  }

  const std::string bank_root = BankNameToLooseRoot(normalized_bank);
  if (bank_root.empty()) {
    return false;
  }

  for (const std::filesystem::path& manifest_root :
       BuildBankManifestRootCandidates(bank_root)) {
    const std::filesystem::path manifest_path =
        ResolveBankManifestPath(manifest_root);
    std::error_code error;
    if (!std::filesystem::is_regular_file(manifest_path, error)) {
      continue;
    }

    std::filesystem::path resolved_root =
        manifest_path.parent_path().lexically_relative(GetLooseAssetRoot());
    if (resolved_root.empty() || resolved_root.string().starts_with("..")) {
      resolved_root = manifest_root;
    }
    if (out_manifest_root != nullptr) {
      *out_manifest_root = resolved_root.empty() ? manifest_root : resolved_root;
    }
    if (out_manifest_path != nullptr) {
      *out_manifest_path = manifest_path;
    }
    return true;
  }

  return false;
}

bool ResolveSyntheticBankManifestLooseAsset(std::string_view bank_name,
                                            LooseAssetResolution* out_resolution,
                                            std::string* out_candidate) {
  std::filesystem::path manifest_root;
  std::filesystem::path manifest_path;
  if (!FindSyntheticBankManifest(bank_name, &manifest_root, &manifest_path)) {
    return false;
  }

  const std::string normalized_bank = NormalizeLooseAssetKey(bank_name);
  const std::string manifest_candidate =
      (manifest_root / "_manifest.tsv").generic_string();
  if (out_resolution != nullptr) {
    out_resolution->host_path = manifest_path.lexically_normal();
    out_resolution->normalized_key = normalized_bank;
    out_resolution->provider_relative_path = normalized_bank;
  }
  if (out_candidate != nullptr) {
    *out_candidate = manifest_candidate;
  }
  return true;
}
}  // namespace detail
}  // namespace refii::vfs
