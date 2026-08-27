#include "vfs_hooks_private.h"

#include "core/host_asset_reader.h"

namespace refii::vfs {
inline namespace detail {

bool MaybeUpgradeResourceManagerTextureResolution(
    uint32_t resource_hash,
    std::string_view request_name,
    uint32_t caller_lr,
    LooseAssetResolution* inout_resolution,
    std::string* inout_candidate) {
  if (inout_resolution == nullptr || inout_candidate == nullptr) {
    return false;
  }

  const std::string normalized_request = NormalizeLooseAssetKey(request_name);
  const std::string original_candidate =
      NormalizeLooseAssetKey(inout_candidate->empty()
                                 ? inout_resolution->provider_relative_path
                                 : *inout_candidate);
  const bool texture_request = IsTextureAssetCandidate(normalized_request) ||
                               IsTextureAssetCandidate(original_candidate) ||
                               IsTextureAssetCandidate(
                                   inout_resolution->provider_relative_path);
  if (!texture_request) {
    return false;
  }

  const std::filesystem::path loose_root = GetLooseAssetRoot();
  if (!RefiiVfsDatabaseExists(loose_root)) {
    return false;
  }

  std::vector<ResolvedVfsHashCandidate> resolved_candidates;
  std::unordered_set<std::string> seen_candidates;
  auto add_resolved_candidate =
      [&](std::string_view candidate_key,
          const LooseAssetResolution* known_resolution = nullptr) {
        std::string normalized_candidate = NormalizeLooseAssetKey(candidate_key);
        if (normalized_candidate.empty() ||
            !IsTextureAssetCandidate(normalized_candidate) ||
            !seen_candidates.insert(normalized_candidate).second) {
          return;
        }

        LooseAssetResolution resolution;
        std::string repaired_candidate;
        if (known_resolution != nullptr) {
          resolution = *known_resolution;
          repaired_candidate = normalized_candidate;
        } else {
          bool resolved = MakeVfsDatabaseCandidateResolution(loose_root,
                                                             normalized_candidate,
                                                             &resolution,
                                                             &repaired_candidate);
          if (!resolved) {
            resolved = ResolveSyntheticBankManifestLooseAsset(
                normalized_candidate, &resolution, &repaired_candidate);
          }
          if (!resolved || !IsTextureAssetCandidate(repaired_candidate)) {
            return;
          }
          normalized_candidate =
              repaired_candidate.empty() ? normalized_candidate
                                         : NormalizeLooseAssetKey(repaired_candidate);
          if (!seen_candidates.insert(normalized_candidate).second &&
              normalized_candidate != NormalizeLooseAssetKey(candidate_key)) {
            return;
          }
        }

        ResolvedVfsHashCandidate resolved_candidate;
        resolved_candidate.resolution = std::move(resolution);
        resolved_candidate.original_candidate = NormalizeLooseAssetKey(candidate_key);
        resolved_candidate.candidate =
            repaired_candidate.empty() ? normalized_candidate : repaired_candidate;
        resolved_candidates.push_back(std::move(resolved_candidate));
        AddPairedTextureHeaderCandidateIfPresent(loose_root,
                                                 normalized_candidate,
                                                 &seen_candidates,
                                                 &resolved_candidates);
      };

  if (!original_candidate.empty()) {
    add_resolved_candidate(original_candidate, inout_resolution);
  }
  if (!normalized_request.empty()) {
    for (const std::string& candidate :
         LookupRefiiVfsDatabaseAliasCandidates(normalized_request, loose_root)) {
      add_resolved_candidate(candidate);
    }
  }
  if (!original_candidate.empty() && original_candidate != normalized_request) {
    for (const std::string& candidate :
         LookupRefiiVfsDatabaseAliasCandidates(original_candidate, loose_root)) {
      add_resolved_candidate(candidate);
    }
  }
  if (resource_hash != 0) {
    for (const std::string& candidate :
         LookupRefiiVfsDatabaseHashCandidates(resource_hash, loose_root)) {
      add_resolved_candidate(candidate);
    }
  }

  LooseAssetResolution selected_resolution;
  std::string selected_candidate;
  if (!SelectResolvedVfsHashCandidate(resource_hash,
                                      &resolved_candidates,
                                      true,
                                      caller_lr,
                                      &selected_resolution,
                                      &selected_candidate)) {
    return false;
  }

  const std::string selected_key = NormalizeLooseAssetKey(selected_candidate);
  const bool changed = selected_key != original_candidate ||
                       selected_resolution.host_path != inout_resolution->host_path;
  if (VfsTracingEnabled() &&
      (changed ||
       ShouldLogRepeatedVfsTraceOperation(
           fmt::format("ResourceTextureFinalShape:{:08X}:{}:{}",
                       resource_hash,
                       normalized_request,
                       selected_key),
           (IsTraceInterestingAsset(normalized_request) ||
            IsTraceInterestingAsset(original_candidate))
               ? 32
               : 96))) {
    const std::array<uint32_t, 4> original_words =
        ReadHostFileFirstWords(inout_resolution->host_path);
    const std::array<uint32_t, 4> selected_words =
        ReadHostFileFirstWords(selected_resolution);
    REXLOG_INFO("Refii VFS ResourceManager texture final shape "
                "hash=0x{:08X} caller_lr=0x{:08X} request={} "
                "previous={} previous_words={} selected={} "
                "selected_words={} changed={} candidates={}",
                resource_hash,
                caller_lr,
                normalized_request,
                original_candidate,
                FormatHostFileFirstWords(original_words),
                selected_key,
                FormatHostFileFirstWords(selected_words),
                changed,
                resolved_candidates.size());
  }

  const bool track_menu_texture =
      IsMainMenuTextureTraceAsset(normalized_request) ||
      IsMainMenuTextureTraceAsset(original_candidate) ||
      IsMainMenuTextureTraceAsset(selected_key);
  if (changed || track_menu_texture) {
    const std::string selected_words =
        VfsTracingEnabled()
            ? FormatHostFileFirstWords(
                  ReadHostFileFirstWords(selected_resolution))
            : std::string{"suppressed"};
    RecordRefiiMainMenuBlockerEvent(
        "texture_selection",
        fmt::format("select hash=0x{:08X} lr=0x{:08X} request={} previous={} "
                    "selected={} words={} changed={} candidates={}",
                    resource_hash,
                    caller_lr,
                    normalized_request,
                    original_candidate,
                    selected_key,
                    selected_words,
                    changed,
                    resolved_candidates.size()));
  }

  if (!changed) {
    return false;
  }

  *inout_resolution = std::move(selected_resolution);
  *inout_candidate = std::move(selected_candidate);
  return true;
}

bool ResolveIndexedResourceHashLooseAsset(
    uint32_t resource_hash,
    LooseAssetResolution* out_resolution,
    std::string* out_candidate,
    bool prefer_resource_manager_texture_shape = false,
    uint32_t caller_lr = 0) {
  const std::filesystem::path loose_root = GetLooseAssetRoot();
  if (RefiiVfsDatabaseExists(loose_root)) {
    std::vector<ResolvedVfsHashCandidate> resolved_candidates;
    std::unordered_set<std::string> seen_candidates;
    for (const std::string& candidate :
         LookupRefiiVfsDatabaseHashCandidates(resource_hash, loose_root)) {
      const std::string normalized_input_candidate =
          NormalizeLooseAssetKey(candidate);
      if (normalized_input_candidate.empty() ||
          !seen_candidates.insert(normalized_input_candidate).second) {
        continue;
      }

      LooseAssetResolution resolution;
      std::string synthetic_candidate;
      bool resolved = MakeVfsDatabaseCandidateResolution(loose_root,
                                                         candidate,
                                                         &resolution,
                                                         &synthetic_candidate);
      if (!resolved) {
        resolved = ResolveSyntheticBankManifestLooseAsset(
            candidate, &resolution, &synthetic_candidate);
      }
      if (!resolved) {
        continue;
      }

      ResolvedVfsHashCandidate resolved_candidate;
      resolved_candidate.resolution = std::move(resolution);
      resolved_candidate.original_candidate = normalized_input_candidate;
      resolved_candidate.candidate =
          synthetic_candidate.empty() ? resolved_candidate.original_candidate
                                      : synthetic_candidate;
      resolved_candidates.push_back(std::move(resolved_candidate));
      if (prefer_resource_manager_texture_shape &&
          IsTextureAssetCandidate(resolved_candidates.back().candidate)) {
        AddPairedTextureHeaderCandidateIfPresent(
            loose_root,
            resolved_candidates.back().candidate,
            &seen_candidates,
            &resolved_candidates);
      }
      if (!prefer_resource_manager_texture_shape ||
          !IsTextureAssetCandidate(resolved_candidates.back().candidate)) {
        break;
      }
    }
    return SelectResolvedVfsHashCandidate(resource_hash,
                                          &resolved_candidates,
                                          prefer_resource_manager_texture_shape,
                                          caller_lr,
                                          out_resolution,
                                          out_candidate);
  }

  const auto index = GetRefiiResourceHashIndex();
  if (index == nullptr) {
    return false;
  }

  const auto match = index->candidates_by_hash.find(resource_hash);
  if (match == index->candidates_by_hash.end()) {
    return false;
  }

  std::vector<ResolvedVfsHashCandidate> resolved_candidates;
  for (const std::string& candidate : match->second) {
    LooseAssetResolution resolution;
    std::string synthetic_candidate;
    bool resolved = MakeVfsDatabaseCandidateResolution(GetLooseAssetRoot(),
                                                       candidate,
                                                       &resolution,
                                                       &synthetic_candidate);
    if (!resolved) {
      resolved = ResolveSyntheticBankManifestLooseAsset(
          candidate, &resolution, &synthetic_candidate);
    }
    if (!resolved) {
      if (prefer_resource_manager_texture_shape &&
          IsTextureAssetCandidate(candidate)) {
        resolved = ResolveVfsDatabaseAliasLooseAssetForResourceManager(
            candidate, caller_lr, &resolution, &synthetic_candidate);
      }
      if (!resolved) {
        resolved = ResolveVfsDatabaseAliasLooseAsset(
            candidate, &resolution, &synthetic_candidate);
      }
    }
    if (!resolved) {
      resolved = ResolveSyntheticBankEntryLooseAsset(
          candidate, &resolution, &synthetic_candidate);
    }

    // The hash has already been turned back into candidate names and Godot's mount is keyed by
    // those names. Size only — reading here would inflate a bank entry per candidate.
    if (!resolved) {
      uint32_t host_size = 0;
      if (HostAssetSize(candidate, host_size)) {
        static std::atomic<uint32_t> served_by_hash{0};
        const uint32_t nth =
            served_by_hash.fetch_add(1, std::memory_order_relaxed) + 1u;
        if (nth <= 8u || (nth & (nth - 1u)) == 0u) {
          REXLOG_INFO("Refii VFS: hash 0x{:08X} served as '{}' from GODOT's mount "
                      "({} byte(s)) — #{} so far.",
                      resource_hash, candidate, host_size, nth);
        }

        resolution.host_path.clear();
        resolution.normalized_key = candidate;
        resolution.provider_relative_path = candidate;
        resolution.host_served = true;
        synthetic_candidate = candidate;
        resolved = true;
      }
    }

    if (!resolved) {
      continue;
    }

    ResolvedVfsHashCandidate resolved_candidate;
    resolved_candidate.resolution = std::move(resolution);
    resolved_candidate.original_candidate = NormalizeLooseAssetKey(candidate);
    resolved_candidate.candidate =
        synthetic_candidate.empty() ? resolved_candidate.original_candidate
                                    : synthetic_candidate;
    resolved_candidates.push_back(std::move(resolved_candidate));
    if (!prefer_resource_manager_texture_shape ||
        !IsTextureAssetCandidate(resolved_candidates.back().candidate)) {
      break;
    }
  }

  return SelectResolvedVfsHashCandidate(resource_hash,
                                        &resolved_candidates,
                                        prefer_resource_manager_texture_shape,
                                        caller_lr,
                                        out_resolution,
                                        out_candidate);
}

bool ResolveTargetedRequestManagerHashLooseAsset(
    uint32_t resource_hash,
    LooseAssetResolution* out_resolution,
    std::string* out_candidate,
    bool prefer_resource_manager_texture_shape = false,
    uint32_t caller_lr = 0) {
  if (resource_hash == 0) {
    return false;
  }

  const auto targeted = GetTargetedRequestManagerHashContribution(resource_hash);
  if (!targeted.has_value()) {
    return false;
  }

  std::vector<std::string> candidates;
  auto add_candidate = [&candidates](std::string_view candidate) {
    const std::string normalized = NormalizeLooseAssetKey(candidate);
    if (!normalized.empty()) {
      AddUnique(candidates, normalized);
    }
  };
  add_candidate(targeted->resolved_candidate);
  add_candidate(targeted->provider_relative);
  add_candidate(targeted->name);

  for (const std::string& candidate : candidates) {
    if (ShouldSkipLooseNativeAsset(candidate, "targeted_request_manager_hash")) {
      continue;
    }

    LooseAssetResolution resolution;
    std::string resolved_candidate;
    bool resolved = ResolveExactTypedBankManifestLooseAsset(
        candidate, &resolution, &resolved_candidate);
    if (!resolved) {
      resolved = ResolveSyntheticBankManifestLooseAsset(
          candidate, &resolution, &resolved_candidate);
    }
    if (!resolved && ResolveLooseAssetOverride(candidate, &resolution)) {
      resolved = true;
      resolved_candidate = candidate;
    }
    if (!resolved) {
      if (prefer_resource_manager_texture_shape &&
          IsTextureAssetCandidate(candidate)) {
        resolved = ResolveVfsDatabaseAliasLooseAssetForResourceManager(
            candidate, caller_lr, &resolution, &resolved_candidate);
      }
      if (!resolved) {
        resolved = ResolveVfsDatabaseExactAliasLooseAsset(
            candidate, &resolution, &resolved_candidate);
      }
      if (!resolved) {
        resolved = ResolveVfsDatabaseAliasLooseAsset(
            candidate, &resolution, &resolved_candidate);
      }
    }
    if (!resolved) {
      resolved = ResolveSyntheticBankEntryLooseAsset(
          candidate, &resolution, &resolved_candidate);
    }
    if (!resolved) {
      continue;
    }

    if (prefer_resource_manager_texture_shape) {
      MaybeUpgradeResourceManagerTextureResolution(
          resource_hash,
          candidate,
          caller_lr,
          &resolution,
          &resolved_candidate);
    }

    if (out_resolution != nullptr) {
      *out_resolution = std::move(resolution);
    }
    if (out_candidate != nullptr) {
      *out_candidate =
          resolved_candidate.empty() ? candidate : resolved_candidate;
    }
    return true;
  }

  return false;
}

bool ResolveResourceHashLooseAsset(uint32_t resource_hash,
                                   LooseAssetResolution* out_resolution,
                                   std::string* out_candidate,
                                   bool prefer_resource_manager_texture_shape = false,
                                   uint32_t caller_lr = 0) {
  if (resource_hash == 0) {
    return false;
  }

  for (const std::string& candidate :
       GetRuntimeResourceHashNames(resource_hash)) {
    if (IsLuaScriptAssetKey(candidate)) {
      LooseAssetResolution resolution;
      std::string lua_candidate;
      if (!ResolveRefiiLuaScriptAsset(
              candidate, &resolution, &lua_candidate)) {
        continue;
      }
      if (out_resolution != nullptr) {
        *out_resolution = std::move(resolution);
      }
      if (out_candidate != nullptr) {
        *out_candidate =
            lua_candidate.empty() ? candidate : lua_candidate;
      }
      return true;
    }

    if (ShouldSkipLooseNativeAsset(candidate, "resource_hash")) {
      continue;
    }

    LooseAssetResolution resolution;
    std::string synthetic_candidate;
    bool resolved = ResolveExactTypedBankManifestLooseAsset(
        candidate, &resolution, &synthetic_candidate);
    if (!resolved) {
      resolved = ResolveLooseAssetOverride(candidate, &resolution);
    }
    if (!resolved) {
      if (prefer_resource_manager_texture_shape &&
          IsTextureAssetCandidate(candidate)) {
        resolved = ResolveVfsDatabaseAliasLooseAssetForResourceManager(
            candidate, caller_lr, &resolution, &synthetic_candidate);
      }
      if (!resolved) {
        resolved = ResolveVfsDatabaseAliasLooseAsset(
            candidate, &resolution, &synthetic_candidate);
      }
    }
    if (!resolved) {
      resolved = ResolveSyntheticBankEntryLooseAsset(
          candidate, &resolution, &synthetic_candidate);
    }
    if (!resolved) {
      continue;
    }

    if (prefer_resource_manager_texture_shape) {
      MaybeUpgradeResourceManagerTextureResolution(resource_hash,
                                                   candidate,
                                                   caller_lr,
                                                   &resolution,
                                                   &synthetic_candidate);
    }

    if (out_resolution != nullptr) {
      *out_resolution = std::move(resolution);
    }
    if (out_candidate != nullptr) {
      *out_candidate =
          synthetic_candidate.empty() ? candidate : synthetic_candidate;
    }
    return true;
  }

  if (ResolveTargetedRequestManagerHashLooseAsset(
          resource_hash,
          out_resolution,
          out_candidate,
          prefer_resource_manager_texture_shape,
          caller_lr)) {
    return true;
  }

  return ResolveIndexedResourceHashLooseAsset(
      resource_hash,
      out_resolution,
      out_candidate,
      prefer_resource_manager_texture_shape,
      caller_lr);
}


}  // namespace detail
}  // namespace refii::vfs
