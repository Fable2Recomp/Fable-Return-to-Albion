#include "vfs_hooks_private.h"

#include "core/host_asset_reader.h"

#include <rex/system/xthread.h>

namespace refii::vfs {
inline namespace detail {
const char* ToString(RefiiVfsOpenShape shape) {

  switch (shape) {
    case RefiiVfsOpenShape::RawStream:
      return "RawStream";
    case RefiiVfsOpenShape::ResourceWrapper:
      return "ResourceWrapper";
    case RefiiVfsOpenShape::NativeGenerated:
      return "NativeGenerated";
    case RefiiVfsOpenShape::NullOptionalMiss:
      return "NullOptionalMiss";
    case RefiiVfsOpenShape::SyntheticBank:
      return "SyntheticBank";
  }
  return "RawStream";
}

const char* ToString(RefiiVfsOpenDisposition disposition) {
  switch (disposition) {
    case RefiiVfsOpenDisposition::Miss:
      return "Miss";
    case RefiiVfsOpenDisposition::Opened:
      return "Opened";
    case RefiiVfsOpenDisposition::NativeGenerated:
      return "NativeGenerated";
    case RefiiVfsOpenDisposition::OptionalNull:
      return "OptionalNull";
    case RefiiVfsOpenDisposition::DeferredBank:
      return "DeferredBank";
    case RefiiVfsOpenDisposition::OpenFailed:
      return "OpenFailed";
  }
  return "Miss";
}

bool IsHandledRefiiVfsOpenDisposition(RefiiVfsOpenDisposition disposition) {
  return disposition == RefiiVfsOpenDisposition::Opened ||
         disposition == RefiiVfsOpenDisposition::OptionalNull ||
         disposition == RefiiVfsOpenDisposition::DeferredBank;
}

void RecordRefiiVfsOpenDecisionAudit(
    std::string_view api,
    uint32_t caller_lr,
    RefiiVfsOpenShape shape,
    RefiiVfsOpenDisposition disposition,
    std::string_view request_name,
    uint32_t resource_hash,
    const RefiiVfsOwnershipDecision& ownership,
    std::string_view candidate,
    std::string_view provider_relative) {
  static std::mutex audit_mutex;
  static std::unordered_map<std::string, uint64_t> decision_counts;
  static std::unordered_set<std::string> unresolved_once;
  static std::unordered_set<std::string> unique_open_misses;
  static uint64_t unique_open_miss_logs = 0;
  static bool unique_open_miss_cap_logged = false;

  const std::string api_key =
      api.empty() ? std::string("RefiiVfsOpen") : std::string(api);
  const std::string decision_key =
      fmt::format("{}|{}|{}", api_key, ToString(shape), ToString(disposition));
  const std::string normalized_request = NormalizeLooseAssetKey(request_name);
  const std::string normalized_candidate = NormalizeLooseAssetKey(candidate);
  const std::string normalized_provider = NormalizeLooseAssetKey(provider_relative);
  const bool unresolved_disposition =
      disposition == RefiiVfsOpenDisposition::Miss ||
      disposition == RefiiVfsOpenDisposition::OpenFailed;
  const bool optional_probe =
      IsCachedOptionalSyntheticVfsMiss(normalized_request) ||
      IsOptionalSyntheticVfsProbe(normalized_request);
  const bool known_absent_probe = IsKnownAbsentBootProbe(normalized_request);
  uint64_t decision_count = 0;
  bool log_unresolved = false;
  bool log_open_miss = false;
  bool log_open_miss_cap = false;
  uint64_t open_miss_log_index = 0;
  std::string unresolved_key;
  {
    std::lock_guard<std::mutex> lock(audit_mutex);
    decision_count = ++decision_counts[decision_key];
    if (unresolved_disposition &&
        (ownership.kind == RefiiVfsOwnershipKind::RefiiOwned ||
         IsTraceInterestingAsset(request_name) ||
         IsTraceInterestingAsset(candidate) ||
         IsTraceInterestingAsset(provider_relative))) {
      unresolved_key = fmt::format("{}|0x{:08X}|{}|{}",
                                   api_key,
                                   resource_hash,
                                   normalized_request,
                                   normalized_candidate);
      log_unresolved = unresolved_once.insert(unresolved_key).second;
    }

    if (unresolved_disposition && !normalized_request.empty()) {
      const std::string open_miss_key =
          fmt::format("{}|{}|{}|0x{:08X}|{}|{}|{}|{}",
                      api_key,
                      ToString(shape),
                      ToString(disposition),
                      resource_hash,
                      normalized_request,
                      normalized_candidate,
                      normalized_provider,
                      ToString(ownership.kind));
      if (unique_open_misses.insert(open_miss_key).second) {
        constexpr uint64_t kMaxUniqueOpenMissLogs = 96;
        if (unique_open_miss_logs < kMaxUniqueOpenMissLogs) {
          open_miss_log_index = ++unique_open_miss_logs;
          log_open_miss = true;
        } else if (!unique_open_miss_cap_logged) {
          unique_open_miss_cap_logged = true;
          log_open_miss_cap = true;
        }
      }
    }
  }

  if (log_open_miss) {
    REXLOG_INFO("Refii VFS open miss #{:03} api={} shape={} disposition={} "
                "lr=0x{:08X} hash=0x{:08X} request={} candidate={} "
                "provider={} ownership={} optional={} known_absent={} reason={}",
                open_miss_log_index,
                api_key,
                ToString(shape),
                ToString(disposition),
                caller_lr,
                resource_hash,
                normalized_request,
                normalized_candidate,
                normalized_provider,
                ToString(ownership.kind),
                optional_probe,
                known_absent_probe,
                ownership.reason);
  } else if (log_open_miss_cap) {
    REXLOG_INFO("Refii VFS open miss audit suppressed after first {} unique "
                "unresolved opens",
                unique_open_miss_logs);
  }

  if (!VfsTracingEnabled()) {
    return;
  }

  if (decision_count == 1 || (decision_count % 128) == 0) {
    REXLOG_INFO("Refii VFS audit api={} shape={} disposition={} count={} "
                "lr=0x{:08X}",
                api_key,
                ToString(shape),
                ToString(disposition),
                decision_count,
                caller_lr);
  }

  if (log_unresolved) {
    REXLOG_INFO("Refii VFS unresolved audit api={} shape={} disposition={} "
                "lr=0x{:08X} hash=0x{:08X} request={} candidate={} "
                "provider={} ownership={} reason={}",
                api_key,
                ToString(shape),
                ToString(disposition),
                caller_lr,
                resource_hash,
                normalized_request,
                normalized_candidate,
                normalized_provider,
                ToString(ownership.kind),
                ownership.reason);
  }
}

RefiiVfsOpenDisposition TryOpenResolvedRefiiVfsAsset(
    PPCContext& ctx,
    uint8_t* base,
    const PPCContext& original_ctx,
    RefiiVfsOpenShape shape,
    const LooseAssetResolution& resolution,
    const RefiiResolvedVfsOpenRequest& request,
    RefiiVfsOpenResult* out_result) {
  RefiiVfsOpenResult result;
  result.resolution = resolution;
  result.candidate = request.candidate.empty()
                         ? NormalizeLooseAssetKey(request.request_name)
                         : std::string(request.candidate);
  result.source = request.source.empty() ? "resolved" : std::string(request.source);
  const uint32_t caller_lr =
      request.caller_lr != 0 ? request.caller_lr
                             : static_cast<uint32_t>(original_ctx.lr);

  auto finish = [&](RefiiVfsOpenDisposition disposition) {
    result.disposition = disposition;
    RecordRefiiVfsOpenDecisionAudit(request.api,
                                    caller_lr,
                                    shape,
                                    disposition,
                                    request.request_name,
                                    request.resource_hash,
                                    result.ownership,
                                    result.candidate,
                                    result.resolution.provider_relative_path);
    if (out_result != nullptr) {
      *out_result = result;
    }
    return disposition;
  };

  if (request.out_guest == 0) {
    return finish(RefiiVfsOpenDisposition::Miss);
  }
  if (shape == RefiiVfsOpenShape::NativeGenerated) {
    return finish(RefiiVfsOpenDisposition::NativeGenerated);
  }

  PPCContext open_ctx = original_ctx;
  bool opened = false;
  if (shape == RefiiVfsOpenShape::ResourceWrapper) {
    LooseAssetResolution wrapper_resolution = resolution;
    std::string wrapper_provider = resolution.provider_relative_path;
    uint64_t wrapper_offset = request.resource_offset;
    uint32_t wrapper_size = request.resource_size;
    const bool has_read_window =
        request.resource_offset != 0 || request.resource_size != 0;
    const std::string normalized_provider =
        NormalizeLooseAssetKey(resolution.provider_relative_path);
    const std::string normalized_candidate = NormalizeLooseAssetKey(
        request.candidate.empty() ? request.request_name : request.candidate);
    const std::array<uint32_t, 4> wrapper_first_words =
        ReadHostFileFirstWords(wrapper_resolution);
    const bool full_resource_texture =
        IsTextureAssetCandidate(normalized_provider) &&
        !IsTextureHeaderCandidate(normalized_provider) &&
        LooksLikeResourceWrapperHeader(wrapper_first_words);
    if (full_resource_texture && request.resource_offset == 0 &&
        request.resource_size == 84) {
      wrapper_size = 0;
      if (VfsTracingEnabled() ||
          IsMainMenuTextureTraceAsset(normalized_provider)) {
        REXLOG_INFO("Refii VFS ResourceManager full texture wrapper promoted "
                    "api={} lr=0x{:08X} hash=0x{:08X} provider={} "
                    "requested_size={} host_size={} first_words={}",
                    request.api,
                    caller_lr,
                    request.resource_hash,
                    normalized_provider,
                    request.resource_size,
                    HostFileSize(wrapper_resolution),
                    FormatHostFileFirstWords(wrapper_first_words));
      }
    }
    if (has_read_window &&
        (IsTextureHeaderCandidate(normalized_provider) ||
         IsTextureHeaderCandidate(normalized_candidate))) {
      std::error_code header_size_error;
      const uint64_t header_size =
          HostFileSize(resolution);
      LooseAssetResolution payload_resolution;
      std::string payload_candidate;
      const std::string header_key =
          IsTextureHeaderCandidate(normalized_provider) ? normalized_provider
                                                        : normalized_candidate;
      if (!header_size_error && request.resource_offset >= header_size &&
          ResolvePairedTexturePayloadForHeader(
              header_key, &payload_resolution, &payload_candidate)) {
        wrapper_resolution = payload_resolution;
        wrapper_provider = resolution.provider_relative_path;
        wrapper_offset = request.resource_offset - header_size;
        wrapper_size = request.resource_size;
        if (VfsTracingEnabled() || IsMainMenuTextureTraceAsset(header_key)) {
          REXLOG_INFO("Refii VFS ResourceManager texture read window mapped "
                      "api={} lr=0x{:08X} hash=0x{:08X} header={} payload={} "
                      "virtual_offset={} virtual_size={} payload_offset={} "
                      "provider={} host={}",
                      request.api,
                      caller_lr,
                      request.resource_hash,
                      header_key,
                      payload_candidate,
                      request.resource_offset,
                      request.resource_size,
                      wrapper_offset,
                      NormalizeLooseAssetKey(wrapper_provider),
                      payload_resolution.host_path.string());
        }
      } else if (VfsTracingEnabled() || IsMainMenuTextureTraceAsset(header_key)) {
        REXLOG_INFO("Refii VFS ResourceManager texture read window kept on "
                    "header api={} lr=0x{:08X} hash=0x{:08X} header={} "
                    "offset={} size={} header_size={} header_error={}",
                    request.api,
                    caller_lr,
                    request.resource_hash,
                    header_key,
                    request.resource_offset,
                    request.resource_size,
                    header_size_error ? 0 : header_size,
                    header_size_error.message());
      }
    }
    opened = TryOpenLooseAssetResourceWrapper(
        open_ctx,
        base,
        request.out_guest,
        request.mode == 0 ? 1 : request.mode,
        wrapper_resolution,
        wrapper_provider,
        static_cast<uint8_t>(request.resource_flags & 0xFF),
        wrapper_offset,
        wrapper_size);
  } else {
    opened = TryOpenLooseAsset(open_ctx,
                               base,
                               request.out_guest,
                               request.manager_guest,
                               request.mode,
                               resolution,
                               resolution.provider_relative_path);
  }

  if (!opened) {
    if (VfsTracingEnabled() || LooseAssetOverrideTracingEnabled()) {
      REXLOG_WARN("Refii VFS resolved open failed api={} shape={} "
                  "lr=0x{:08X} request={} candidate={} provider_relative={} "
                  "host={}",
                  request.api,
                  ToString(shape),
                  caller_lr,
                  request.request_name,
                  result.candidate,
                  resolution.provider_relative_path,
                  resolution.host_path.string());
    }
    return finish(RefiiVfsOpenDisposition::OpenFailed);
  }

  ctx = open_ctx;
  result.opened_guest = TryReadOutputPointer(base, request.out_guest);
  if (request.count_vfs_hit) {
    result.hit_count =
        LooseStats().vfs_hits.fetch_add(1, std::memory_order_relaxed) + 1;
    LogLooseStatsMilestone("vfs_hits", result.hit_count);
  }

  const bool trace =
      VfsTracingEnabled() || LooseAssetOverrideTracingEnabled() ||
      (request.count_vfs_hit && ShouldLogLooseStatsMilestone(result.hit_count));
  if (trace) {
    REXLOG_INFO("Refii VFS resolved open hit hit={} api={} shape={} "
                "lr=0x{:08X} request={} hash=0x{:08X} source={} "
                "candidate={} provider_relative={} host={} object=0x{:08X} "
                "mode=0x{:08X}",
                result.hit_count,
                request.api,
                ToString(shape),
                caller_lr,
                request.request_name,
                request.resource_hash,
                result.source,
                result.candidate,
                resolution.provider_relative_path,
                resolution.host_path.string(),
                result.opened_guest,
                request.mode);
  }

  return finish(RefiiVfsOpenDisposition::Opened);
}

RefiiVfsOpenDisposition TryOpenRefiiVfsAsset(
    PPCContext& ctx,
    uint8_t* base,
    const PPCContext& original_ctx,
    RefiiVfsOpenShape shape,
    const RefiiVfsOpenRequest& request,
    RefiiVfsOpenResult* out_result) {
  RefiiVfsOpenResult result;
  const uint32_t caller_lr =
      request.caller_lr != 0 ? request.caller_lr
                             : static_cast<uint32_t>(original_ctx.lr);

  auto finish = [&](RefiiVfsOpenDisposition disposition) {
    result.disposition = disposition;
    RecordRefiiVfsOpenDecisionAudit(request.api,
                                    caller_lr,
                                    shape,
                                    disposition,
                                    request.request_name,
                                    request.resource_hash,
                                    result.ownership,
                                    result.candidate,
                                    result.resolution.provider_relative_path);
    if (out_result != nullptr) {
      *out_result = result;
    }
    return disposition;
  };

  if (shape == RefiiVfsOpenShape::SyntheticBank) {
    const std::string bank_name(request.request_name);
    const std::string api_name =
        request.api.empty() ? std::string("RefiiVfsOpen")
                            : std::string(request.api);
    const bool opened =
        TryCreateSyntheticBank(ctx, base, request.out_guest, bank_name, api_name);
    result.opened_guest =
        opened ? TryReadOutputPointer(base, request.out_guest) : 0;
    return finish(opened ? RefiiVfsOpenDisposition::Opened
                         : RefiiVfsOpenDisposition::Miss);
  }

  const uint64_t lookups =
      LooseStats().vfs_lookups.fetch_add(1, std::memory_order_relaxed) + 1;
  LogLooseStatsMilestone("vfs_lookups", lookups);

  if (shape == RefiiVfsOpenShape::NullOptionalMiss) {
    if (request.out_guest != 0) {
      StoreGuestU32(base, request.out_guest, 0);
    }
    ctx.r3.u32 = request.out_guest;
    if (VfsTracingEnabled() || LooseAssetOverrideTracingEnabled()) {
      REXLOG_INFO("Refii VFS open shape={} api={} lr=0x{:08X} "
                  "request={} hash=0x{:08X} out=0x{:08X}",
                  ToString(shape),
                  request.api,
                  caller_lr,
                  NormalizeLooseAssetKey(request.request_name),
                  request.resource_hash,
                  request.out_guest);
    }
    return finish(RefiiVfsOpenDisposition::OptionalNull);
  }

  if (request.out_guest == 0 ||
      (request.request_name.empty() && request.resource_hash == 0)) {
    return finish(RefiiVfsOpenDisposition::Miss);
  }

  if (!request.request_name.empty() &&
      shape != RefiiVfsOpenShape::NativeGenerated &&
      !LookupRefiiVfsTypedBankManifest(request.request_name, nullptr, nullptr) &&
      ShouldDeferLooseBankContainer(request.request_name)) {
    StoreGuestU32(base, request.out_guest, 0);
    ctx.r3.u32 = request.out_guest;
    if (VfsTracingEnabled() || LooseAssetOverrideTracingEnabled()) {
      REXLOG_INFO("Refii VFS open deferred extracted bank container api={} "
                  "lr=0x{:08X} request={} out=0x{:08X}",
                  request.api,
                  caller_lr,
                  request.request_name,
                  request.out_guest);
    }
    return finish(RefiiVfsOpenDisposition::DeferredBank);
  }

  if (!request.request_name.empty()) {
    result.ownership = DecideRefiiVfsOwnershipForName(request.request_name);
    LogRefiiVfsOwnershipDecision(
        request.api, caller_lr, request.request_name, result.ownership);
  }

  if (result.ownership.kind != RefiiVfsOwnershipKind::RefiiOwned &&
      request.resource_hash != 0) {
    result.ownership = DecideRefiiVfsOwnershipForHash(request.resource_hash);
    LogRefiiVfsOwnershipDecision(request.api,
                                 caller_lr,
                                 fmt::format("hash:0x{:08X}",
                                             request.resource_hash),
                                 result.ownership);
  }

  const bool optional_null_asset =
      !request.request_name.empty() &&
      (IsKnownAbsentBootProbe(request.request_name) ||
       IsOptionalSyntheticVfsProbe(request.request_name) ||
       IsOptionalGameFaceBinaryMiss(request.request_name));
  if (request.allow_optional_null_miss &&
      result.ownership.kind != RefiiVfsOwnershipKind::RefiiOwned &&
      optional_null_asset) {
    if (request.out_guest != 0) {
      StoreGuestU32(base, request.out_guest, 0);
    }
    ctx.r3.u32 = request.out_guest;
    if (VfsTracingEnabled() || LooseAssetOverrideTracingEnabled()) {
      REXLOG_INFO("Refii VFS open optional null miss api={} lr=0x{:08X} "
                  "request={} out=0x{:08X} ownership={} reason={}",
                  request.api,
                  caller_lr,
                  request.request_name,
                  request.out_guest,
                  ToString(result.ownership.kind),
                  result.ownership.reason);
    }
    return finish(RefiiVfsOpenDisposition::OptionalNull);
  }

  if (result.ownership.kind != RefiiVfsOwnershipKind::RefiiOwned ||
      !result.ownership.has_resolution) {
    return finish(RefiiVfsOpenDisposition::Miss);
  }

  result.resolution = result.ownership.resolution;
  result.candidate = result.ownership.candidate.empty()
                         ? NormalizeLooseAssetKey(request.request_name)
                         : result.ownership.candidate;
  result.source = result.ownership.source;

  // With the mount as the only source there is one resolution per asset; nothing to upgrade to.
  LogRefiiOwnedTextureResolution(request.api,
                                 caller_lr,
                                 request.resource_hash,
                                 request.request_name,
                                 result.resolution,
                                 result.candidate,
                                 result.source);

  if (shape == RefiiVfsOpenShape::NativeGenerated) {
    if (VfsTracingEnabled() || LooseAssetOverrideTracingEnabled()) {
      REXLOG_INFO("Refii VFS open native-generated api={} lr=0x{:08X} "
                  "request={} candidate={} provider_relative={} host={} "
                  "reason={}",
                  request.api,
                  caller_lr,
                  request.request_name,
                  result.candidate,
                  result.resolution.provider_relative_path,
                  result.resolution.host_path.string(),
                  result.ownership.reason);
    }
    LogRefiiVfsChainVerdict(
        "native_shaped_stream",
        caller_lr,
        request.request_name,
        result.candidate,
        result.resolution.provider_relative_path,
        "native_generated_cache_wrapper",
        request.api,
        "KeepHooked",
        "RequestManager_FindStream");
    return finish(RefiiVfsOpenDisposition::NativeGenerated);
  }

  return TryOpenResolvedRefiiVfsAsset(
      ctx,
      base,
      original_ctx,
      shape,
      result.resolution,
      RefiiResolvedVfsOpenRequest{
          .api = request.api,
          .request_name = request.request_name,
          .candidate = result.candidate,
          .source = result.source,
          .resource_hash = request.resource_hash,
          .out_guest = request.out_guest,
          .manager_guest = request.manager_guest,
          .mode = request.mode,
          .resource_flags = request.resource_flags,
          .resource_offset = request.resource_offset,
          .resource_size = request.resource_size,
          .caller_lr = caller_lr,
          .count_vfs_hit = true,
      },
      out_result);
}

void LogRefiiOwnedTextureStreamBuilt(uint8_t* base,
                                     std::string_view api,
                                     uint32_t caller_lr,
                                     uint32_t resource_hash,
                                     std::string_view request_name,
                                     const LooseAssetResolution& resolution,
                                     std::string_view candidate,
                                     std::string_view source,
                                     uint32_t out_guest,
                                     uint32_t stream_guest,
                                     bool wrapper_shape) {
  if (!VfsTracingEnabled() && !LooseAssetOverrideTracingEnabled()) {
    return;
  }
  if (!IsRefiiTextureResolution(
          request_name, resource_hash, resolution, candidate)) {
    return;
  }
  const std::string normalized_request = NormalizeLooseAssetKey(request_name);
  const std::string normalized_candidate =
      NormalizeLooseAssetKey(candidate.empty() ? resolution.provider_relative_path
                                               : candidate);
  const std::string provider_key =
      NormalizeLooseAssetKey(resolution.provider_relative_path);
  REXLOG_INFO("Refii VFS texture stream build api={} lr=0x{:08X} "
              "hash=0x{:08X} request={} candidate={} source={} "
              "provider_relative={} host={} out=0x{:08X} object=0x{:08X} "
              "shape={} words={}",
              api,
              caller_lr,
              resource_hash,
              normalized_request,
              normalized_candidate,
              source,
              provider_key,
              resolution.host_path.string(),
              out_guest,
              stream_guest,
              wrapper_shape ? "resource_wrapper" : "stream",
              FormatGuestWords(base, stream_guest, 12));
  LogRefiiVfsChainVerdict("texture_parser",
                          caller_lr,
                          normalized_request,
                          normalized_candidate,
                          provider_key,
                          wrapper_shape ? "Refii_resource_wrapper"
                                        : "Refii_host_stream",
                          api,
                          "TraceOnce",
                          wrapper_shape ? "DB20_C340_or_TextureStreamBuild"
                                        : "consumer_stream_read");
  if (IsMainMenuTextureHashTraceAsset(normalized_request) ||
      IsMainMenuTextureHashTraceAsset(normalized_candidate) ||
      IsMainMenuTextureHashTraceAsset(provider_key)) {
    RecordRefiiMainMenuBlockerEvent(
        "texture_stream_build",
        fmt::format("RefiiOwned api={} lr=0x{:08X} hash=0x{:08X} request={} "
                    "candidate={} object=0x{:08X} shape={}",
                    api,
                    caller_lr,
                    resource_hash,
                    normalized_request,
                    normalized_candidate,
                    stream_guest,
                    wrapper_shape ? "resource_wrapper" : "stream"));
  }
}

bool MakeVfsDatabaseCandidateResolution(const std::filesystem::path& loose_root,
                                        std::string_view candidate,
                                        LooseAssetResolution* out_resolution,
                                        std::string* out_candidate);

std::optional<ResolvedSyntheticBankOpenAlias>
GetResolvedSyntheticBankOpenAlias(std::string_view bank_alias) {
  const std::string root_key =
      GetLooseAssetRoot().lexically_normal().string();
  const std::string normalized_alias = NormalizeLooseAssetKey(bank_alias);
  if (root_key.empty() || normalized_alias.empty()) {
    return std::nullopt;
  }

  std::lock_guard<std::mutex> lock(ResolvedSyntheticBankOpenAliasMutex());
  if (ResolvedSyntheticBankOpenAliasRootKey() != root_key) {
    return std::nullopt;
  }
  const auto match = ResolvedSyntheticBankOpenAliases().find(normalized_alias);
  if (match == ResolvedSyntheticBankOpenAliases().end()) {
    return std::nullopt;
  }
  return match->second;
}

bool LookupResolvedSyntheticBankOpenAlias(std::string_view bank_alias,
                                          std::string* out_resolved_bank,
                                          std::string* out_reason) {
  const auto resolved_alias = GetResolvedSyntheticBankOpenAlias(bank_alias);
  if (!resolved_alias.has_value()) {
    return false;
  }
  if (out_resolved_bank != nullptr) {
    *out_resolved_bank = resolved_alias->resolved_bank;
  }
  if (out_reason != nullptr) {
    *out_reason = resolved_alias->reason;
  }
  return true;
}
}  // namespace detail
}  // namespace refii::vfs
