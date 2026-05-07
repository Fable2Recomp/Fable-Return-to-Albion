#include "vfs_hooks_private.h"

namespace refii::vfs {
inline namespace detail {

std::vector<std::string> BuildSyntheticVfsEntryCandidates(
    std::string_view asset_path) {
  std::vector<std::string> candidates;
  const std::string normalized = NormalizeLooseAssetKey(asset_path);
  AddUnique(candidates, normalized);
  const std::string mip0_redirect =
      CanonicalizeMip0TextureHeaderRedirect(normalized);
  if (!mip0_redirect.empty()) {
    AddUnique(candidates, mip0_redirect);
  }

  constexpr std::string_view kWheelIconsPrefix = "art/gui/wheelicons/";
  if (StartsWith(normalized, kWheelIconsPrefix)) {
    const std::string_view tail =
        std::string_view(normalized).substr(kWheelIconsPrefix.size());
    const size_t category_separator = tail.find('/');
    if (category_separator != std::string_view::npos &&
        category_separator + 1 < tail.size()) {
      AddUnique(candidates,
                std::string(kWheelIconsPrefix) +
                    std::string(tail.substr(category_separator + 1)));
    } else if (!tail.empty()) {
      std::vector<std::string_view> wheel_icon_categories;
      if (StartsWith(tail, "minimap")) {
        wheel_icon_categories.push_back("minimap");
      } else if (StartsWith(tail, "reward_")) {
        wheel_icon_categories.push_back("rewards");
      }
      for (const std::string_view category : wheel_icon_categories) {
        AddUnique(candidates,
                  std::string(kWheelIconsPrefix) + std::string(category) +
                      "/" + std::string(tail));
      }
    }
  }

  if (normalized == "art/gui/loadingscreens/screenshot.tex") {
    AddUnique(candidates, "art/gui/persistant/screenshot.tex");
  }

  const std::string_view language_prefix = "language/";
  const std::string_view patch_text_segment = "/patch_text/";
  if (StartsWith(normalized, language_prefix)) {
    const size_t patch_text = normalized.find(patch_text_segment);
    if (patch_text != std::string::npos &&
        patch_text + patch_text_segment.size() < normalized.size()) {
      AddUnique(candidates,
                normalized.substr(0, patch_text) + "/text/" +
                    normalized.substr(patch_text + patch_text_segment.size()));
    }
  }

  if (StartsWith(normalized, "data/")) {
    AddUnique(candidates, std::string(normalized.substr(5)));
  }
  if (StartsWith(normalized, "unpacked/")) {
    AddUnique(candidates, std::string(normalized.substr(9)));
  }
  if (StartsWith(normalized, "data/scripts/")) {
    AddUnique(candidates,
              "scripts/" +
                  std::string(normalized.substr(sizeof("data/scripts/") - 1)));
  }
  if (StartsWith(normalized, "data/scripts_r/")) {
    AddUnique(candidates,
              "scripts_r/" +
                  std::string(normalized.substr(sizeof("data/scripts_r/") - 1)));
  }
  for (const std::string& gameface_candidate :
       BuildGameFaceRedirectCandidates(normalized)) {
    AddUnique(candidates, gameface_candidate);
  }

  return candidates;
}

bool IsKnownLanguageLocale(std::string_view value) {
  constexpr std::string_view kKnownLanguageLocales[] = {
      "en-uk", "hu-hu", "zh-cht", "cs-cz", "ko-kr", "pl-pl", "sk-sk"};
  for (const std::string_view locale : kKnownLanguageLocales) {
    if (value == locale) {
      return true;
    }
  }
  return false;
}

std::string_view GetLanguageAssetTail(std::string_view normalized_alias) {
  constexpr std::string_view kLanguagePrefix = "language/";
  if (!StartsWith(normalized_alias, kLanguagePrefix)) {
    return {};
  }

  const std::string_view language_tail =
      normalized_alias.substr(kLanguagePrefix.size());
  const size_t first_separator = language_tail.find('/');
  if (first_separator == std::string_view::npos) {
    return language_tail;
  }

  const std::string_view first_segment =
      language_tail.substr(0, first_separator);
  if (!IsKnownLanguageLocale(first_segment)) {
    return language_tail;
  }

  if (first_separator + 1 >= language_tail.size()) {
    return {};
  }
  return language_tail.substr(first_separator + 1);
}

std::string_view GetLanguageLocale(std::string_view normalized_alias) {
  constexpr std::string_view kLanguagePrefix = "language/";
  if (!StartsWith(normalized_alias, kLanguagePrefix)) {
    return {};
  }

  const std::string_view language_tail =
      normalized_alias.substr(kLanguagePrefix.size());
  const size_t first_separator = language_tail.find('/');
  if (first_separator == std::string_view::npos) {
    return {};
  }

  const std::string_view first_segment =
      language_tail.substr(0, first_separator);
  return IsKnownLanguageLocale(first_segment) ? first_segment
                                              : std::string_view{};
}

bool IsLanguageVideoAssetTail(std::string_view asset_tail) {
  return StartsWith(asset_tail, "videos/") && EndsWith(asset_tail, ".bik");
}

bool IsLanguageVideoAssetKey(std::string_view normalized_alias) {
  const std::string_view asset_tail = GetLanguageAssetTail(normalized_alias);
  return !asset_tail.empty() && IsLanguageVideoAssetTail(asset_tail);
}

int KnownLanguageLocaleRank(std::string_view locale) {
  constexpr std::string_view kKnownLanguageLocales[] = {
      "en-uk", "hu-hu", "zh-cht", "cs-cz", "ko-kr", "pl-pl", "sk-sk"};
  for (size_t i = 0; i < std::size(kKnownLanguageLocales); ++i) {
    if (locale == kKnownLanguageLocales[i]) {
      return static_cast<int>(i);
    }
  }
  return 100;
}

int ScoreVfsDatabaseAliasCandidate(std::string_view candidate,
                                   const std::vector<std::string>& aliases) {
  const std::string normalized_candidate = NormalizeLooseAssetKey(candidate);
  int best_score = 1000;

  for (const std::string& alias : aliases) {
    const std::string normalized_alias = NormalizeLooseAssetKey(alias);
    if (normalized_candidate == normalized_alias) {
      best_score = std::min(best_score, 0);
      continue;
    }

    const std::string_view alias_tail = GetLanguageAssetTail(normalized_alias);
    const std::string_view candidate_tail =
        GetLanguageAssetTail(normalized_candidate);
    if (alias_tail.empty() || candidate_tail.empty()) {
      continue;
    }

    int tail_score = 0;
    if (alias_tail == candidate_tail) {
      tail_score = 0;
    } else {
      constexpr std::string_view kPatchTextPrefix = "patch_text/";
      constexpr std::string_view kTextPrefix = "text/";
      if (StartsWith(alias_tail, kPatchTextPrefix) &&
          StartsWith(candidate_tail, kTextPrefix) &&
          alias_tail.substr(kPatchTextPrefix.size()) ==
              candidate_tail.substr(kTextPrefix.size())) {
        tail_score = 1;
      } else if (StartsWith(alias_tail, kTextPrefix) &&
                 StartsWith(candidate_tail, kPatchTextPrefix) &&
                 alias_tail.substr(kTextPrefix.size()) ==
                     candidate_tail.substr(kPatchTextPrefix.size())) {
        tail_score = 2;
      } else {
        continue;
      }
    }

    const std::string_view alias_locale = GetLanguageLocale(normalized_alias);
    const std::string_view candidate_locale =
        GetLanguageLocale(normalized_candidate);
    if (!alias_locale.empty() && candidate_locale == alias_locale) {
      best_score = std::min(best_score, 1 + tail_score);
    } else if (candidate_locale == "en-uk") {
      best_score = std::min(best_score, 3 + tail_score);
    } else if (!candidate_locale.empty()) {
      best_score =
          std::min(best_score,
                   10 + tail_score * 10 +
                       KnownLanguageLocaleRank(candidate_locale));
    }
  }

  return best_score;
}

std::vector<std::string> BuildRefiiVfsDatabasePrimeAliases(
    const std::vector<std::string>& aliases) {
  std::vector<std::string> expanded;

  constexpr std::string_view kLanguagePrefix = "language/";
  constexpr std::string_view kKnownLanguageLocales[] = {
      "en-uk", "hu-hu", "zh-cht", "cs-cz", "ko-kr", "pl-pl", "sk-sk"};
  constexpr std::string_view kPatchTextPrefix = "patch_text/";
  constexpr std::string_view kTextPrefix = "text/";

  auto add_language_family = [&](std::string_view asset_tail) {
    for (const std::string_view locale : kKnownLanguageLocales) {
      AddUnique(expanded,
                std::string(kLanguagePrefix) + std::string(locale) + "/" +
                    std::string(asset_tail));
    }
  };

  for (const std::string& alias : aliases) {
    const std::string normalized_alias = NormalizeLooseAssetKey(alias);
    AddUnique(expanded, normalized_alias);

    if (!StartsWith(normalized_alias, kLanguagePrefix)) {
      continue;
    }

    const std::string_view asset_tail = GetLanguageAssetTail(normalized_alias);
    if (asset_tail.empty()) {
      continue;
    }

    if (IsLanguageVideoAssetTail(asset_tail)) {
      continue;
    }

    add_language_family(asset_tail);
    if (StartsWith(asset_tail, kPatchTextPrefix)) {
      add_language_family(std::string(kTextPrefix) +
                          std::string(asset_tail.substr(kPatchTextPrefix.size())));
    } else if (StartsWith(asset_tail, kTextPrefix)) {
      add_language_family(std::string(kPatchTextPrefix) +
                          std::string(asset_tail.substr(kTextPrefix.size())));
    }
  }

  return expanded;
}

std::string FormatCandidateSample(const std::vector<std::string>& candidates,
                                  size_t max_count) {
  std::string sample;
  const size_t count = std::min(max_count, candidates.size());
  for (size_t index = 0; index < count; ++index) {
    if (!sample.empty()) {
      sample += ", ";
    }
    sample += candidates[index];
  }
  if (candidates.size() > count) {
    sample += fmt::format(", ... +{}", candidates.size() - count);
  }
  return sample;
}

uint64_t HashFingerprintString(uint64_t value, std::string_view text);
uint32_t ComputeResourceNameHash(std::string_view value);

std::string MakeSyntheticVfsLookupCacheKey(std::string_view asset_path,
                                           size_t template_count) {
  std::string key = GetLooseAssetRoot().lexically_normal().string();
  key += '\n';
  key += fmt::format("{}", template_count);
  key += '\n';
  key += NormalizeLooseAssetKey(asset_path);
  return key;
}

std::mutex& SyntheticVfsEntryIndexMutex() {
  static std::mutex mutex;
  return mutex;
}

std::shared_ptr<const SyntheticVfsEntryIndex>& CachedSyntheticVfsEntryIndex() {
  static std::shared_ptr<const SyntheticVfsEntryIndex> index;
  return index;
}

std::vector<std::shared_ptr<const RefiiSyntheticBankTemplate>>
GetSyntheticBankTemplateSnapshot() {
  std::vector<std::shared_ptr<const RefiiSyntheticBankTemplate>> templates;
  std::lock_guard<std::mutex> lock(SyntheticBankTemplatesMutex());
  templates.reserve(SyntheticBankTemplates().size());
  for (const auto& [key, bank_template] : SyntheticBankTemplates()) {
    (void)key;
    if (bank_template != nullptr) {
      templates.push_back(bank_template);
    }
  }
  return templates;
}

void AddSyntheticVfsEntryAlias(SyntheticVfsEntryIndex& index,
                               std::string_view alias,
                               const SyntheticVfsEntry& entry) {
  const std::string normalized = NormalizeLooseAssetKey(alias);
  if (!normalized.empty()) {
    index.by_normalized_path.try_emplace(normalized, entry);
  }
}

void IndexSyntheticManifestEntries(SyntheticVfsEntryIndex& index,
                                   const std::filesystem::path& manifest_root,
                                   const std::filesystem::path& manifest_path) {
  const std::string normalized_root =
      NormalizeLooseAssetKey(manifest_root.generic_string());
  if (normalized_root.empty() || IsInternalLooseAssetKey(normalized_root)) {
    return;
  }

  std::ifstream manifest(manifest_path);
  if (!manifest) {
    return;
  }

  ++index.manifest_tables;
  std::string line;
  std::getline(manifest, line);
  while (std::getline(manifest, line)) {
    if (line.empty()) {
      continue;
    }

    BankManifestEntry entry;
    if (!ParseBankManifestEntryLine(manifest_root, line, &entry)) {
      continue;
    }

    ++index.manifest_entries;
    const std::string normalized_entry =
        NormalizeLooseAssetKey(entry.relative_path);
    if (normalized_entry.empty() || IsInternalLooseAssetKey(normalized_entry)) {
      continue;
    }

    const SyntheticVfsEntry vfs_entry{
        manifest_root, entry.relative_path, entry.host_relative_path};
    AddSyntheticVfsEntryAlias(index, normalized_entry, vfs_entry);

    const std::string rooted_entry =
        (manifest_root / ManifestPathToGenericPath(entry.relative_path))
            .generic_string();
    if (!IsInternalLooseAssetKey(rooted_entry)) {
      AddSyntheticVfsEntryAlias(index, rooted_entry, vfs_entry);
      AddSyntheticVfsEntryAlias(index, "data/" + rooted_entry, vfs_entry);
    }
  }
}

uint32_t ComputeResourceNameHash(std::string_view value) {
  uint32_t hash = 0x811C9DC5;
  for (const char ch : value) {
    hash = (hash * 0x01000193) ^
           static_cast<uint8_t>(static_cast<unsigned char>(ch));
  }
  return hash;
}

bool ShouldIndexRefiiResourceHashCandidate(std::string_view candidate) {
  const std::string normalized = NormalizeLooseAssetKey(candidate);
  if (normalized.empty()) {
    return false;
  }
  if (!GetLooseNativeAssetSkipReason(normalized).empty()) {
    return false;
  }
  return normalized != "_manifest.tsv" && !EndsWith(normalized, "/_manifest.tsv");
}

void AddRefiiResourceHashAlias(RefiiResourceHashIndex& index,
                               std::string_view hash_input,
                               std::string_view candidate) {
  if (hash_input.empty() || candidate.empty()) {
    return;
  }

  const uint32_t hash = ComputeResourceNameHash(hash_input);
  auto& candidates = index.candidates_by_hash[hash];
  const std::string normalized_candidate = NormalizeLooseAssetKey(candidate);
  if (std::find(candidates.begin(), candidates.end(), normalized_candidate) ==
      candidates.end()) {
    candidates.push_back(normalized_candidate);
  }
  ++index.hash_aliases;
}

void AddRefiiExplicitResourceHashCandidate(RefiiResourceHashIndex& index,
                                           uint32_t resource_hash,
                                           std::string_view candidate) {
  if (resource_hash == 0 || !ShouldIndexRefiiResourceHashCandidate(candidate)) {
    return;
  }

  auto& candidates = index.candidates_by_hash[resource_hash];
  const std::string normalized_candidate = NormalizeLooseAssetKey(candidate);
  if (std::find(candidates.begin(), candidates.end(), normalized_candidate) !=
      candidates.end()) {
    return;
  }

  candidates.push_back(normalized_candidate);
  ++index.filehash_aliases;
}

void AddRefiiDatabaseAliasCandidate(RefiiResourceHashIndex& index,
                                    std::string_view alias,
                                    std::string_view candidate) {
  if (alias.empty() || !ShouldIndexRefiiResourceHashCandidate(candidate)) {
    return;
  }

  const std::string normalized_alias = NormalizeLooseAssetKey(alias);
  const std::string normalized_candidate = NormalizeLooseAssetKey(candidate);
  if (normalized_alias.empty() || normalized_candidate.empty()) {
    return;
  }

  auto& candidates = index.candidates_by_alias[normalized_alias];
  if (std::find(candidates.begin(), candidates.end(), normalized_candidate) !=
      candidates.end()) {
    return;
  }

  candidates.push_back(normalized_candidate);
  ++index.database_aliases;
}

void AddRefiiResourceHashCandidate(RefiiResourceHashIndex& index,
                                   std::string_view candidate) {
  if (!ShouldIndexRefiiResourceHashCandidate(candidate)) {
    return;
  }

  const std::string normalized = NormalizeLooseAssetKey(candidate);
  std::string original(candidate);
  std::replace(original.begin(), original.end(), '\\', '/');
  while (!original.empty() && original.back() == '\0') {
    original.pop_back();
  }

  std::string original_prefix_check = original;
  std::ranges::transform(original_prefix_check,
                         original_prefix_check.begin(),
                         [](unsigned char ch) {
                           return static_cast<char>(std::tolower(ch));
                         });
  if (StartsWith(original_prefix_check, "game:/")) {
    original.erase(0, 6);
  } else if (StartsWith(original_prefix_check, "game:")) {
    original.erase(0, 5);
  }
  while (!original.empty() && original.front() == '/') {
    original.erase(original.begin());
  }

  std::vector<std::string> aliases;
  AddUnique(aliases, original);
  AddUnique(aliases, normalized);
  if (StartsWith(normalized, "data/")) {
    AddUnique(aliases, std::string(normalized.substr(5)));
  } else {
    AddUnique(aliases, "data/" + normalized);
  }
  if (!original.empty()) {
    std::string original_lower = original;
    std::ranges::transform(original_lower,
                           original_lower.begin(),
                           [](unsigned char ch) {
                             return static_cast<char>(std::tolower(ch));
                           });
    if (StartsWith(original_lower, "data/")) {
      AddUnique(aliases, std::string(original.substr(5)));
    } else {
      AddUnique(aliases, "data/" + original);
    }
  }

  ++index.indexed_paths;
  for (const std::string& alias : aliases) {
    std::string backslash_alias = alias;
    std::replace(backslash_alias.begin(), backslash_alias.end(), '/', '\\');
    AddRefiiResourceHashAlias(index, alias, normalized);
    AddRefiiResourceHashAlias(index, backslash_alias, normalized);
  }
}

void AddRefiiBasenameCandidate(
    std::unordered_map<std::string, std::vector<std::string>>& basename_candidates,
    std::string_view relative_path) {
  const std::string normalized = NormalizeLooseAssetKey(relative_path);
  if (normalized.empty() || !ShouldIndexRefiiResourceHashCandidate(normalized)) {
    return;
  }

  const size_t separator = normalized.find_last_of('/');
  const std::string basename =
      separator == std::string::npos ? normalized : normalized.substr(separator + 1);
  if (basename.empty() || basename == "filehash.csv") {
    return;
  }

  auto& candidates = basename_candidates[basename];
  if (std::find(candidates.begin(), candidates.end(), normalized) ==
      candidates.end()) {
    candidates.push_back(normalized);
  }
}

void AddRefiiFileHashCsvMappings(
    RefiiResourceHashIndex& index,
    const std::filesystem::path& loose_root,
    const std::vector<std::filesystem::path>& filehash_csv_paths,
    const std::unordered_map<std::string, std::vector<std::string>>&
        basename_candidates) {
  for (const std::filesystem::path& csv_path : filehash_csv_paths) {
    std::ifstream csv(csv_path);
    if (!csv) {
      continue;
    }

    const std::filesystem::path csv_relative =
        csv_path.lexically_relative(loose_root);
    const std::string csv_parent =
        NormalizeLooseAssetKey(csv_relative.parent_path().generic_string());
    const std::string csv_prefix =
        csv_parent.empty() ? std::string() : csv_parent + "/";

    std::string line;
    while (std::getline(csv, line)) {
      if (!line.empty() && line.back() == '\r') {
        line.pop_back();
      }
      const size_t comma = line.find(',');
      if (comma == std::string::npos || comma == 0 || comma + 1 >= line.size()) {
        continue;
      }

      uint64_t parsed_hash = 0;
      try {
        parsed_hash = std::stoull(line.substr(0, comma), nullptr, 10);
      } catch (const std::exception&) {
        continue;
      }
      if (parsed_hash == 0 || parsed_hash > std::numeric_limits<uint32_t>::max()) {
        continue;
      }

      const std::string basename = NormalizeLooseAssetKey(line.substr(comma + 1));
      const auto match = basename_candidates.find(basename);
      if (match == basename_candidates.end()) {
        continue;
      }

      bool added_scoped_candidate = false;
      for (const std::string& candidate : match->second) {
        if (!csv_prefix.empty() && !StartsWith(candidate, csv_prefix)) {
          continue;
        }
        AddRefiiExplicitResourceHashCandidate(
            index, static_cast<uint32_t>(parsed_hash), candidate);
        added_scoped_candidate = true;
      }

      if (added_scoped_candidate) {
        continue;
      }

      for (const std::string& candidate : match->second) {
        AddRefiiExplicitResourceHashCandidate(
            index, static_cast<uint32_t>(parsed_hash), candidate);
      }
    }
  }
}

bool ParseHexUint32(std::string_view text, uint32_t* out_value) {
  if (text.empty() || text.size() > 8 || out_value == nullptr) {
    return false;
  }

  uint32_t value = 0;
  for (const char ch : text) {
    value <<= 4;
    if (ch >= '0' && ch <= '9') {
      value |= static_cast<uint32_t>(ch - '0');
    } else if (ch >= 'a' && ch <= 'f') {
      value |= static_cast<uint32_t>(ch - 'a' + 10);
    } else if (ch >= 'A' && ch <= 'F') {
      value |= static_cast<uint32_t>(ch - 'A' + 10);
    } else {
      return false;
    }
  }

  if (value == 0) {
    return false;
  }

  *out_value = value;
  return true;
}


}  // namespace detail
}  // namespace refii::vfs
