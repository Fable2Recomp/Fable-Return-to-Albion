#include "vfs_hooks_private.h"

#include "core/installed_cultures.h"

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
  // Every code XLip's own tables name — `dword_832D5E90` (XGetLanguage -> culture) and
  // `dword_832D5EC8` (XGetLocale -> culture), disc build — plus en-uk and zh-cht; the same set
  // the extractor canonicalises with, so a stored alias and its lookup agree. A culture this
  // install ships is one too, whatever the tables name.
  constexpr std::string_view kKnownLanguageLocales[] = {
      "en-uk", "en-us", "ja-jp", "de-de", "fr-fr", "es-es",
      "it-it", "ko-kr", "zh-tw", "zh-cht", "pt-pt", "zh-cn",
      "pl-pl", "ru-ru", "fr-be", "cs-cz", "da-dk", "fi-fi",
      "el-gr", "hu-hu", "es-la", "nl-nl", "nb-no", "sk-sk",
      "sv-se"};
  for (const std::string_view locale : kKnownLanguageLocales) {
    if (value == locale) {
      return true;
    }
  }
  return IsInstalledLanguageCulture(value);
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

std::string CanonicalizeRefiiVfsDatabaseAliasKey(std::string_view normalized_key) {
  // Canonical database alias spelling shared with extraction-time emission:
  // requests reachable by mechanical rewrites collapse onto one stored row.
  // - a redundant data/ root is stripped,
  // - a known locale component after language/ or language_dlc/ is dropped,
  // - the patch_text/ spelling folds onto text/.
  std::string_view key = normalized_key;
  if (StartsWith(key, "data/")) {
    key.remove_prefix(5);
  }

  constexpr std::string_view kLanguagePrefix = "language/";
  constexpr std::string_view kLanguageDlcPrefix = "language_dlc/";
  std::string_view root;
  std::string_view tail;
  if (StartsWith(key, kLanguagePrefix)) {
    root = key.substr(0, kLanguagePrefix.size() - 1);
    tail = key.substr(kLanguagePrefix.size());
  } else if (StartsWith(key, kLanguageDlcPrefix)) {
    root = key.substr(0, kLanguageDlcPrefix.size() - 1);
    tail = key.substr(kLanguageDlcPrefix.size());
  } else {
    return std::string(key);
  }

  const size_t first_separator = tail.find('/');
  if (first_separator != std::string_view::npos &&
      first_separator + 1 < tail.size() &&
      IsKnownLanguageLocale(tail.substr(0, first_separator))) {
    tail = tail.substr(first_separator + 1);
  }

  std::string canonical(root);
  canonical += '/';
  constexpr std::string_view kPatchTextPrefix = "patch_text/";
  if (StartsWith(tail, kPatchTextPrefix)) {
    canonical += "text/";
    canonical += tail.substr(kPatchTextPrefix.size());
  } else {
    canonical += tail;
  }
  return canonical;
}

bool IsLanguageVideoAssetTail(std::string_view asset_tail) {
  return StartsWith(asset_tail, "videos/") && EndsWith(asset_tail, ".bik");
}

int KnownLanguageLocaleRank(std::string_view locale) {
  const std::vector<std::string> installed = InstalledLanguageCultures();
  for (size_t i = 0; i < installed.size(); ++i) {
    if (locale == installed[i]) {
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
    } else if (candidate_locale == kFallbackCulture) {
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
  constexpr std::string_view kPatchTextPrefix = "patch_text/";
  constexpr std::string_view kTextPrefix = "text/";
  const std::vector<std::string> installed_cultures = InstalledLanguageCultures();

  auto add_language_family = [&](std::string_view asset_tail) {
    for (const std::string& locale : installed_cultures) {
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

uint32_t ComputeResourceNameHash(std::string_view value);

uint32_t ComputeResourceNameHash(std::string_view value) {
  // Hex-tail branch first, as the guest's name hash `sub_822CA588` does before FNV: a name at
  // least 40 characters long whose basename (after the last '\\') is exactly 40 characters is
  // a stringified `CFileID`, and its last 8 characters are parsed as hex and returned as the id
  // (`LHVFS_CFileID_HexOctetToU32(&name[len - 8])` at 0x822CA608, returned at 0x822CA624 when
  // non-zero). Loose-file overrides for such names only match if the host agrees.
  if (value.size() >= 40u) {
    const size_t slash = value.find_last_of('\\');
    const size_t base = slash == std::string_view::npos ? 0u : slash + 1u;
    if (value.size() - base == 40u) {
      const std::string_view tail = value.substr(value.size() - 8u);
      uint32_t id = 0u;
      bool hex = true;
      for (const char ch : tail) {
        uint32_t digit;
        if (ch >= '0' && ch <= '9') {
          digit = static_cast<uint32_t>(ch - '0');
        } else if (ch >= 'a' && ch <= 'f') {
          digit = static_cast<uint32_t>(ch - 'a') + 10u;
        } else if (ch >= 'A' && ch <= 'F') {
          digit = static_cast<uint32_t>(ch - 'A') + 10u;
        } else {
          hex = false;
          break;
        }

        id = (id << 4) | digit;
      }

      // Zero falls through to the hash, exactly as the guest's `if (v5)` does.
      if (hex && id != 0u) {
        return id;
      }
    }
  }

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
}  // namespace detail
}  // namespace refii::vfs
