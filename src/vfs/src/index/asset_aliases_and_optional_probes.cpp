#include "vfs_hooks_private.h"

namespace refii::vfs {
inline namespace detail {
bool IsDefaultExecutablePatchProbe(std::string_view normalized) {
  return normalized == "default.xexpp" ||
         normalized == "data/default.xexpp" ||
         normalized == "device/harddisk0/partition1/default.xexpp" ||
         EndsWith(normalized, "/default.xexpp");
}

bool IsOptionalLocalizedDirectoryProbe(std::string_view normalized) {
  constexpr std::string_view kLanguagePrefix = "language/";
  const std::string language_probe =
      StartsWith(normalized, "data/language/") ? std::string(normalized.substr(5))
                                               : std::string(normalized);
  if (!StartsWith(language_probe, kLanguagePrefix)) {
    return false;
  }

  const std::string_view after_language =
      std::string_view(language_probe).substr(kLanguagePrefix.size());
  const size_t locale_separator = after_language.find('/');
  if (locale_separator == std::string_view::npos ||
      locale_separator + 1 >= after_language.size()) {
    return false;
  }

  const std::string_view locale = after_language.substr(0, locale_separator);
  const std::string_view locale_tail =
      after_language.substr(locale_separator + 1);
  if (locale == "en-uk") {
    return false;
  }
  return locale_tail == "fonts" ||
         locale_tail == "speech" ||
         locale_tail == "lipsync" ||
         locale_tail == "videos";
}

std::vector<std::string> LookupRefiiVfsDatabaseAliasCandidates(
    std::string_view asset_path,
    const std::filesystem::path& loose_root);

bool IsGameFaceBinaryAssetKey(std::string_view asset_path) {
  const std::string normalized = NormalizeLooseAssetKey(asset_path);
  if (!StartsWith(normalized, "art/gui/gameface/") &&
      !StartsWith(normalized, "data/art/gui/gameface/") &&
      normalized.find("/art/gui/gameface/") == std::string::npos) {
    return false;
  }

  return EndsWith(normalized, ".bgf") || EndsWith(normalized, ".bsg") ||
         EndsWith(normalized, ".fac");
}

bool IsOptionalGameFaceBinaryMiss(std::string_view asset_path) {
  const std::string normalized = NormalizeLooseAssetKey(asset_path);
  if (!IsGameFaceBinaryAssetKey(normalized)) {
    return false;
  }

  // The startup package drives the post-video frontend handoff. If the loose
  // VFS has no override for it, native fallback must get a chance to provide
  // the generated/cache shape instead of us completing the package as absent.
  return normalized == "art/gui/gameface/scrollingbackground.bgf" ||
         normalized == "art/gui/gameface/scrollingbackground.bsg" ||
         normalized == "art/gui/gameface/scrollingbackground.fac" ||
         normalized == "data/art/gui/gameface/scrollingbackground.bgf" ||
         normalized == "data/art/gui/gameface/scrollingbackground.bsg" ||
         normalized == "data/art/gui/gameface/scrollingbackground.fac";
}

bool ResolveExactGameFaceBinaryCarrierLooseAsset(
    std::string_view asset_path,
    LooseAssetResolution* out_resolution,
    std::string* out_candidate) {
  const std::string normalized = NormalizeLooseAssetKey(asset_path);
  if (!IsGameFaceBinaryAssetKey(normalized)) {
    return false;
  }

  const std::string requested_extension =
      std::filesystem::path(normalized).extension().generic_string();
  auto same_extension = [&](std::string_view candidate) {
    return NormalizeLooseAssetKey(
               std::filesystem::path(std::string(candidate))
                   .extension()
                   .generic_string()) == requested_extension;
  };

  const std::filesystem::path loose_root = GetLooseAssetRoot();
  for (const std::string& alias : BuildGameFaceRedirectCandidates(normalized)) {
    for (const std::string& candidate :
         LookupRefiiVfsDatabaseAliasCandidates(alias, loose_root)) {
      if (!same_extension(candidate)) {
        continue;
      }

      const std::string normalized_candidate = NormalizeLooseAssetKey(candidate);
      if (normalized_candidate.empty()) {
        continue;
      }

      std::vector<std::pair<std::filesystem::path, std::string>>
          host_candidates;
      host_candidates.emplace_back(std::filesystem::path(normalized_candidate),
                                   normalized_candidate);
      host_candidates.emplace_back(std::filesystem::path("luasource") /
                                       normalized_candidate,
                                   (std::filesystem::path("luasource") /
                                    normalized_candidate)
                                       .generic_string());

      for (const auto& [relative, provider_relative] : host_candidates) {
        std::error_code error;
        const std::filesystem::path host_path = loose_root / relative;
        if (!std::filesystem::is_regular_file(host_path, error)) {
          continue;
        }

        if (out_resolution != nullptr) {
          out_resolution->host_path = host_path.lexically_normal();
          out_resolution->normalized_key = NormalizeLooseAssetKey(alias);
          out_resolution->provider_relative_path = provider_relative;
        }
        if (out_candidate != nullptr) {
          *out_candidate = normalized_candidate;
        }
        return true;
      }
    }
  }

  std::vector<std::string> candidates;
  auto add_candidate = [&](std::string value) {
    value = NormalizeLooseAssetKey(value);
    if (!value.empty() &&
        std::find(candidates.begin(), candidates.end(), value) ==
            candidates.end()) {
      candidates.push_back(std::move(value));
    }
  };
  for (const std::string& candidate : BuildGameFaceRedirectCandidates(normalized)) {
    add_candidate(candidate);
  }
  add_candidate(normalized);
  if (StartsWith(normalized, "data/")) {
    add_candidate(std::string(normalized.substr(5)));
  }

  for (const std::string& candidate : candidates) {
    if (!same_extension(candidate)) {
      continue;
    }
    std::vector<std::filesystem::path> relatives;
    auto add_relative = [&](std::filesystem::path relative) {
      const std::string normalized_relative =
          NormalizeLooseAssetKey(relative.generic_string());
      if (normalized_relative.empty()) {
        return;
      }
      const auto duplicate =
          std::find_if(relatives.begin(),
                       relatives.end(),
                       [&](const std::filesystem::path& existing) {
                         return NormalizeLooseAssetKey(existing.generic_string()) ==
                                normalized_relative;
                       });
      if (duplicate == relatives.end()) {
        relatives.push_back(std::move(relative));
      }
    };
    add_relative(std::filesystem::path(candidate));
    add_relative(std::filesystem::path("luasource") / candidate);
    if (!StartsWith(candidate, "guiscripts/")) {
      add_relative(std::filesystem::path("luasource") / "guiscripts" / candidate);
    }
    for (const std::filesystem::path& relative : relatives) {
      std::error_code error;
      const std::filesystem::path host_path = loose_root / relative;
      if (!std::filesystem::is_regular_file(host_path, error)) {
        continue;
      }
      if (out_resolution != nullptr) {
        out_resolution->host_path = host_path.lexically_normal();
        out_resolution->normalized_key = NormalizeLooseAssetKey(candidate);
        out_resolution->provider_relative_path = relative.generic_string();
      }
      if (out_candidate != nullptr) {
        *out_candidate = relative.generic_string();
      }
      return true;
    }
  }

  return false;
}

bool IsInternalLooseAssetKey(std::string_view asset_path) {
  const std::string normalized = NormalizeLooseAssetKey(asset_path);
  return StartsWith(normalized, "luasource/") ||
         StartsWith(normalized, "refii_vfs/") ||
         StartsWith(normalized, ".baseline/") ||
         StartsWith(normalized, ".cache/") ||
         StartsWith(normalized, "_disasm/");
}

bool IsLooseLuaWorkspaceRootKey(std::string_view asset_path) {
  const std::string normalized = NormalizeLooseAssetKey(asset_path);
  return StartsWith(normalized, "scripts/") ||
         StartsWith(normalized, "scripts_r/") ||
         StartsWith(normalized, "data/scripts/") ||
         StartsWith(normalized, "data/scripts_r/");
}

std::string_view GetResourceManagerCachedDirectBypassReason(
    std::string_view asset_path) {
  const std::string normalized = NormalizeLooseAssetKey(asset_path);
  if (EndsWith(normalized, ".bik")) {
    return "video_native_cache_wrapper";
  }
  if (EndsWith(normalized, ".sbk")) {
    return "soundbank_native_cache_wrapper";
  }
  if (EndsWith(normalized, ".amp")) {
    return "audio_metadata_native_cache_wrapper";
  }
  if (EndsWith(normalized, ".adb")) {
    return "audio_database_native_cache_wrapper";
  }
  if (EndsWith(normalized, ".bnk")) {
    return "bank_native_cache_wrapper";
  }
  if (IsGameFaceBinaryAssetKey(normalized)) {
    return "gameface_native_cache_wrapper";
  }
  if (normalized == "miscellaneous/loadingscreendata.txt" ||
      normalized == "data/miscellaneous/loadingscreendata.txt") {
    return "loading_screen_data_native_cache_wrapper";
  }
  if (EndsWith(normalized, ".lua")) {
    return "lua_native_cache_wrapper";
  }
  if (EndsWith(normalized, ".vfsconfig")) {
    return "vfsconfig_native_cache_wrapper";
  }
  if (normalized == "gold_version.txt" ||
      EndsWith(normalized, "/gold_version.txt") ||
      normalized == "build_version.txt" ||
      EndsWith(normalized, "/build_version.txt") ||
      normalized == "version.txt" || EndsWith(normalized, "/version.txt")) {
    return "version_probe_native_cache_wrapper";
  }
  return {};
}

std::string_view GetLooseNativeAssetSkipReason(std::string_view asset_path) {
  if (IsInternalLooseAssetKey(asset_path)) {
    return "internal_workspace";
  }
  if (IsLooseLuaWorkspaceRootKey(asset_path)) {
    return "lua_workspace";
  }
  if (IsGameFaceBinaryAssetKey(asset_path) &&
      !LooseGameFaceBinaryOverridesEnabled()) {
    return "gameface_binary";
  }
  return {};
}

bool IsOptionalSyntheticVfsProbe(std::string_view asset_path);
bool LookupRefiiVfsTypedBankManifest(std::string_view bank_name,
                                     std::filesystem::path* out_manifest_root,
                                     std::filesystem::path* out_manifest_path);

bool IsOptionalSyntheticVfsProbe(std::string_view asset_path) {
  const std::string normalized = NormalizeLooseAssetKey(asset_path);
  if (normalized.empty()) {
    return false;
  }

  if (IsDefaultExecutablePatchProbe(normalized)) {
    return true;
  }
  if (StartsWith(normalized, "update:/") || StartsWith(normalized, "update/")) {
    return true;
  }
  if (normalized == "tu1_data.bnk" ||
      normalized == "data/tu1_data.bnk" ||
      normalized == "update:/data/tu1_data.bnk" ||
      normalized == "update/data/tu1_data.bnk") {
    return true;
  }
  if (normalized == "padding.dat" ||
      normalized == "data/padding.dat" ||
      normalized == "spa.bin" ||
      normalized == "data/spa.bin" ||
      normalized == "gold_version.txt" ||
      normalized == "data/gold_version.txt" ||
      normalized == "game:/gold_version.txt" ||
      normalized == "game/gold_version.txt" ||
      normalized == "game:/data/gold_version.txt" ||
      normalized == "game/data/gold_version.txt" ||
      normalized == "build_version.txt" ||
      normalized == "data/build_version.txt" ||
      normalized == "game:/build_version.txt" ||
      normalized == "game/build_version.txt" ||
      normalized == "game:/data/build_version.txt" ||
      normalized == "game/data/build_version.txt" ||
      normalized == "update:/build_version.txt" ||
      normalized == "update/build_version.txt" ||
      normalized == "episodic_exec.txt" ||
      normalized == "data/episodic_exec.txt" ||
      normalized == "game:/data/episodic_exec.txt" ||
      normalized == "game/data/episodic_exec.txt") {
    return true;
  }
  if (normalized == "gamescripts.bnk" ||
      normalized == "data/gamescripts.bnk" ||
      normalized == "gamescripts_r.bnk" ||
      normalized == "data/gamescripts_r.bnk" ||
      normalized == "guiscripts.bnk" ||
      normalized == "data/guiscripts.bnk" ||
      normalized == "language/cultures.txt" ||
      normalized == "data/language/cultures.txt" ||
      normalized == "miscellaneous/levelconnectiongraph.txt" ||
      normalized == "data/miscellaneous/levelconnectiongraph.txt" ||
      normalized == "miscellaneous/fasttravellist.txt" ||
      normalized == "data/miscellaneous/fasttravellist.txt" ||
      normalized == "miscellaneous/e3physicsmeshes.txt" ||
      normalized == "data/miscellaneous/e3physicsmeshes.txt" ||
      normalized == "miscellaneous/tipoftheday.txt" ||
      normalized == "data/miscellaneous/tipoftheday.txt") {
    return true;
  }
  if (StartsWith(normalized, "texture_morph_wildcard_")) {
    return true;
  }
  if (StartsWith(normalized, "language_dlc/")) {
    return true;
  }
  if (IsOptionalLocalizedDirectoryProbe(normalized)) {
    return true;
  }
  constexpr std::string_view kLanguagePrefix = "language/";
  const std::string language_probe =
      StartsWith(normalized, "data/language/") ? normalized.substr(5)
                                               : normalized;
  if (!StartsWith(language_probe, kLanguagePrefix)) {
    return false;
  }

  const std::string_view language_path(language_probe);
  const std::string_view after_language =
      language_path.substr(kLanguagePrefix.size());
  const size_t locale_separator = after_language.find('/');
  if (locale_separator == std::string_view::npos ||
      locale_separator + 1 >= after_language.size()) {
    return false;
  }

  const std::string_view locale_tail =
      after_language.substr(locale_separator + 1);
  return locale_tail == "speech/speech.bnk" ||
         locale_tail == "lipsync/lipsync.bnk" ||
         locale_tail == "lang.ini";
}

bool IsKnownAbsentBootProbe(std::string_view asset_path) {
  const std::string normalized = NormalizeLooseAssetKey(asset_path);
  if (IsDefaultExecutablePatchProbe(normalized)) {
    return true;
  }
  if (IsOptionalLocalizedDirectoryProbe(normalized)) {
    return true;
  }

  constexpr std::string_view kLanguagePrefix = "language/";
  const std::string language_probe =
      StartsWith(normalized, "data/language/") ? normalized.substr(5)
                                               : normalized;
  if (StartsWith(language_probe, kLanguagePrefix)) {
    const std::string_view after_language =
        std::string_view(language_probe).substr(kLanguagePrefix.size());
    const size_t locale_separator = after_language.find('/');
    if (locale_separator != std::string_view::npos &&
        locale_separator + 1 < after_language.size() &&
        after_language.substr(locale_separator + 1) == "lang.ini") {
      return true;
    }
  }

  return normalized == "padding.dat" ||
         normalized == "data/padding.dat" ||
         normalized == "spa.bin" ||
         normalized == "data/spa.bin" ||
         normalized == "gold_version.txt" ||
         normalized == "data/gold_version.txt" ||
         normalized == "game:/gold_version.txt" ||
         normalized == "game/gold_version.txt" ||
         normalized == "game:/data/gold_version.txt" ||
         normalized == "game/data/gold_version.txt" ||
         normalized == "build_version.txt" ||
         normalized == "data/build_version.txt" ||
         normalized == "game:/build_version.txt" ||
         normalized == "game/build_version.txt" ||
         normalized == "game:/data/build_version.txt" ||
         normalized == "game/data/build_version.txt" ||
         normalized == "update:/build_version.txt" ||
         normalized == "update/build_version.txt" ||
         normalized == "episodic_exec.txt" ||
         normalized == "data/episodic_exec.txt" ||
         normalized == "game:/data/episodic_exec.txt" ||
         normalized == "game/data/episodic_exec.txt" ||
         normalized == "gamescripts.bnk" ||
         normalized == "data/gamescripts.bnk" ||
         normalized == "gamescripts_r.bnk" ||
         normalized == "data/gamescripts_r.bnk" ||
         normalized == "guiscripts.bnk" ||
         normalized == "data/guiscripts.bnk" ||
         normalized == "language/cultures.txt" ||
         normalized == "data/language/cultures.txt" ||
         normalized == "miscellaneous/levelconnectiongraph.txt" ||
         normalized == "data/miscellaneous/levelconnectiongraph.txt" ||
         normalized == "miscellaneous/fasttravellist.txt" ||
         normalized == "data/miscellaneous/fasttravellist.txt" ||
         normalized == "miscellaneous/e3physicsmeshes.txt" ||
         normalized == "data/miscellaneous/e3physicsmeshes.txt" ||
         normalized == "miscellaneous/tipoftheday.txt" ||
         normalized == "data/miscellaneous/tipoftheday.txt" ||
         StartsWith(normalized, "texture_morph_wildcard_") ||
         StartsWith(normalized, "language_dlc/");
}

bool ShouldSkipLooseNativeAsset(std::string_view asset_path,
                                std::string_view context) {
  const std::string_view reason = GetLooseNativeAssetSkipReason(asset_path);
  if (reason.empty()) {
    return false;
  }

  if (LooseAssetOverrideTracingEnabled()) {
    REXLOG_INFO("Loose asset override skipped {} asset={} reason={}",
                context,
                asset_path,
                reason);
  }
  return true;
}
}  // namespace detail
}  // namespace refii::vfs
