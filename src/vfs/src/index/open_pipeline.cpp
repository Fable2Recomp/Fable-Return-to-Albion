#include "vfs_hooks_private.h"

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

  const std::string api_key =
      api.empty() ? std::string("RefiiVfsOpen") : std::string(api);
  const std::string decision_key =
      fmt::format("{}|{}|{}", api_key, ToString(shape), ToString(disposition));
  uint64_t decision_count = 0;
  bool log_unresolved = false;
  std::string unresolved_key;
  {
    std::lock_guard<std::mutex> lock(audit_mutex);
    decision_count = ++decision_counts[decision_key];
    if ((disposition == RefiiVfsOpenDisposition::Miss ||
         disposition == RefiiVfsOpenDisposition::OpenFailed) &&
        (ownership.kind == RefiiVfsOwnershipKind::RefiiOwned ||
         IsTraceInterestingAsset(request_name) ||
         IsTraceInterestingAsset(candidate) ||
         IsTraceInterestingAsset(provider_relative))) {
      unresolved_key = fmt::format("{}|0x{:08X}|{}|{}",
                                   api_key,
                                   resource_hash,
                                   NormalizeLooseAssetKey(request_name),
                                   NormalizeLooseAssetKey(candidate));
      log_unresolved = unresolved_once.insert(unresolved_key).second;
    }
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
                NormalizeLooseAssetKey(request_name),
                NormalizeLooseAssetKey(candidate),
                NormalizeLooseAssetKey(provider_relative),
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
    result.handled = IsHandledRefiiVfsOpenDisposition(disposition);
    result.opened = disposition == RefiiVfsOpenDisposition::Opened;
    result.native_generated =
        disposition == RefiiVfsOpenDisposition::NativeGenerated;
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
        ReadHostFileFirstWords(wrapper_resolution.host_path);
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
                    HostFileSize(wrapper_resolution.host_path),
                    FormatHostFileFirstWords(wrapper_first_words));
      }
    }
    if (has_read_window &&
        (IsTextureHeaderCandidate(normalized_provider) ||
         IsTextureHeaderCandidate(normalized_candidate))) {
      std::error_code header_size_error;
      const uint64_t header_size =
          std::filesystem::file_size(resolution.host_path, header_size_error);
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
        wrapper_resolution.host_path,
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
                               resolution.host_path,
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
    result.handled = IsHandledRefiiVfsOpenDisposition(disposition);
    result.opened = disposition == RefiiVfsOpenDisposition::Opened;
    result.native_generated =
        disposition == RefiiVfsOpenDisposition::NativeGenerated;
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

  if (!RefiiLooseAssetBackendEnabled()) {
    return finish(RefiiVfsOpenDisposition::Miss);
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
    result.ownership = DecideRefiiVfsOwnershipForName(
        request.request_name,
        request.resource_hash,
        request.prefer_resource_manager_texture_shape,
        caller_lr);
    LogRefiiVfsOwnershipDecision(
        request.api, caller_lr, request.request_name, result.ownership);
  }

  if (result.ownership.kind != RefiiVfsOwnershipKind::RefiiOwned &&
      request.resource_hash != 0) {
    result.ownership = DecideRefiiVfsOwnershipForHash(
        request.resource_hash,
        request.prefer_resource_manager_texture_shape,
        caller_lr);
    LogRefiiVfsOwnershipDecision(request.api,
                                 caller_lr,
                                 fmt::format("hash:0x{:08X}",
                                             request.resource_hash),
                                 result.ownership);
  }

  const bool optional_null_asset =
      !request.request_name.empty() &&
      (IsGameFaceBinaryAssetKey(request.request_name) ||
       IsKnownAbsentBootProbe(request.request_name) ||
       IsOptionalSyntheticVfsProbe(request.request_name));
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

  if (request.prefer_resource_manager_texture_shape) {
    MaybeUpgradeResourceManagerTextureResolution(
        request.resource_hash,
        request.request_name.empty() ? result.candidate : request.request_name,
        caller_lr,
        &result.resolution,
        &result.candidate);
  }
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

RefiiVfsOpenDisposition TryOpenRefiiBankEntryAsset(
    PPCContext& ctx,
    uint8_t* base,
    const PPCContext& original_ctx,
    const RefiiBankEntryOpenRequest& request,
    RefiiVfsOpenResult* out_result) {
  RefiiVfsOpenResult result;
  const uint32_t caller_lr =
      request.caller_lr != 0 ? request.caller_lr
                             : static_cast<uint32_t>(original_ctx.lr);

  auto finish = [&](RefiiVfsOpenDisposition disposition) {
    result.disposition = disposition;
    result.handled = IsHandledRefiiVfsOpenDisposition(disposition);
    result.opened = disposition == RefiiVfsOpenDisposition::Opened;
    result.native_generated =
        disposition == RefiiVfsOpenDisposition::NativeGenerated;
    RecordRefiiVfsOpenDecisionAudit(request.api,
                                    caller_lr,
                                    RefiiVfsOpenShape::RawStream,
                                    disposition,
                                    request.entry_name,
                                    0,
                                    result.ownership,
                                    result.candidate,
                                    result.resolution.provider_relative_path);
    if (out_result != nullptr) {
      *out_result = result;
    }
    return disposition;
  };

  if (!RefiiLooseAssetBackendEnabled() || request.out_guest == 0 ||
      request.entry_name.empty()) {
    return finish(RefiiVfsOpenDisposition::Miss);
  }

  if (IsLuaScriptAssetKey(request.entry_name)) {
    if (!request.lua_trace_tag.empty()) {
      TraceLuaVfsBridgeResolution(request.lua_trace_tag, 0, request.entry_name);
    }
    LooseAssetResolution resolution;
    std::string candidate;
    std::string source = "lua_extracted_source";
    if (!ResolveRefiiLuaScriptAsset(
            request.entry_name, &resolution, &candidate, &source)) {
      return finish(RefiiVfsOpenDisposition::Miss);
    }
    PPCContext loose_ctx = original_ctx;
    const RefiiVfsOpenDisposition disposition = TryOpenResolvedRefiiVfsAsset(
        loose_ctx,
        base,
        original_ctx,
        RefiiVfsOpenShape::RawStream,
        resolution,
        RefiiResolvedVfsOpenRequest{
            .api = request.api,
            .request_name = request.entry_name,
            .candidate = candidate,
            .source = source,
            .out_guest = request.out_guest,
            .manager_guest = request.manager_guest,
            .mode = request.mode,
            .caller_lr = caller_lr,
            .count_vfs_hit = false,
        },
        out_result);
    if (disposition == RefiiVfsOpenDisposition::Opened) {
      ctx = loose_ctx;
    }
    return disposition;
  }

  if (ShouldSkipLooseNativeAsset(request.entry_name, request.lookup_label)) {
    return finish(RefiiVfsOpenDisposition::Miss);
  }

  bool log_lookup_detail = false;
  if (request.count_bank_lookup) {
    const uint64_t lookups =
        LooseStats().bank_lookups.fetch_add(1, std::memory_order_relaxed) + 1;
    LogLooseStatsMilestone("bank_lookups", lookups);
    log_lookup_detail = ShouldLogLooseStatsMilestone(lookups);
  }

  LooseAssetResolution resolution;
  std::string candidate;
  const bool resolved =
      request.manifest_entry != nullptr
          ? ResolveManifestEntryLooseAsset(
                request.manifest_entry->manifest_root,
                request.manifest_entry->relative_path,
                &resolution,
                &candidate,
                request.manifest_entry->host_relative_path)
          : ResolveBankLooseAssetOverride(request.bank_name,
                                          request.entry_name,
                                          &resolution,
                                          &candidate);
  if (!resolved) {
    if (log_lookup_detail || VfsTracingEnabled() ||
        LooseAssetOverrideTracingEnabled()) {
      REXLOG_INFO("Refii VFS bank entry miss api={} reason=not_resolved "
                  "bank={} entry={} lookup={} root={}",
                  request.api,
                  request.bank_name,
                  request.entry_name,
                  request.lookup_label,
                  GetLooseAssetRoot().string());
    }
    return finish(RefiiVfsOpenDisposition::Miss);
  }

  PPCContext loose_ctx = original_ctx;
  const RefiiVfsOpenDisposition disposition = TryOpenResolvedRefiiVfsAsset(
      loose_ctx,
      base,
      original_ctx,
      RefiiVfsOpenShape::RawStream,
      resolution,
      RefiiResolvedVfsOpenRequest{
          .api = request.api,
          .request_name = request.entry_name,
          .candidate = candidate,
          .source = request.manifest_entry != nullptr ? "bank_manifest"
                                                      : "bank_override",
          .out_guest = request.out_guest,
          .manager_guest = request.manager_guest,
          .mode = request.mode,
          .caller_lr = caller_lr,
          .count_vfs_hit = false,
      },
      out_result);
  if (disposition == RefiiVfsOpenDisposition::Opened) {
    ctx = loose_ctx;
  } else if (log_lookup_detail) {
    REXLOG_INFO("Refii VFS bank entry miss api={} reason=open_failed bank={} "
                "entry={} candidate={} provider_relative={} host={} "
                "manager=0x{:08X} mode={}",
                request.api,
                request.bank_name,
                request.entry_name,
                candidate,
                resolution.provider_relative_path,
                resolution.host_path.string(),
                request.manager_guest,
                request.mode);
  }
  return disposition;
}

bool ResolveRefiiOwnedTextureResolution(
    std::string_view api,
    uint32_t resource_hash,
    std::string_view request_name,
    uint32_t caller_lr,
    LooseAssetResolution* out_resolution,
    std::string* out_candidate,
    std::string* out_source) {
  if (!RefiiLooseAssetBackendEnabled()) {
    return false;
  }

  const std::string normalized_request = NormalizeLooseAssetKey(request_name);
  const bool texture_request =
      IsTextureAssetCandidate(normalized_request) ||
      HasRuntimeTextureName(resource_hash);
  if (!texture_request) {
    return false;
  }

  RefiiVfsOwnershipDecision decision;
  if (!normalized_request.empty() &&
      IsTextureAssetCandidate(normalized_request)) {
    decision = DecideRefiiVfsOwnershipForName(
        normalized_request, resource_hash, true, caller_lr);
    LogRefiiVfsOwnershipDecision(api, caller_lr, normalized_request, decision);
  }

  if (decision.kind != RefiiVfsOwnershipKind::RefiiOwned &&
      resource_hash != 0) {
    decision =
        DecideRefiiVfsOwnershipForHash(resource_hash, true, caller_lr);
    LogRefiiVfsOwnershipDecision(
        api, caller_lr, fmt::format("hash:0x{:08X}", resource_hash), decision);
  }

  if (decision.kind != RefiiVfsOwnershipKind::RefiiOwned ||
      !decision.has_resolution ||
      !IsRefiiTextureResolution(normalized_request,
                                resource_hash,
                                decision.resolution,
                                decision.candidate)) {
    return false;
  }

  LogRefiiOwnedTextureResolution(api,
                                 caller_lr,
                                 resource_hash,
                                 normalized_request,
                                 decision.resolution,
                                 decision.candidate,
                                 decision.source);
  if (out_resolution != nullptr) {
    *out_resolution = decision.resolution;
  }
  if (out_candidate != nullptr) {
    *out_candidate = decision.candidate;
  }
  if (out_source != nullptr) {
    *out_source = decision.source;
  }
  return true;
}

bool ResolveRefiiOwnedTextureDescriptorHash(std::string_view api,
                                            uint32_t resource_hash,
                                            uint32_t caller_lr,
                                            LooseAssetResolution* out_resolution,
                                            std::string* out_name,
                                            std::string* out_candidate,
                                            std::string* out_source) {
  if (resource_hash == 0) {
    return false;
  }

  const std::vector<std::string> runtime_names =
      GetRuntimeResourceHashNames(resource_hash);
  std::string selected_name;
  for (const std::string& name : runtime_names) {
    if (IsTextureAssetCandidate(name)) {
      selected_name = name;
      break;
    }
  }
  if (selected_name.empty()) {
    selected_name = std::string(MainMenuTextureNameForResourceHash(resource_hash));
  }
  if (selected_name.empty()) {
    return false;
  }

  LooseAssetResolution resolution;
  std::string candidate;
  std::string source;
  if (!ResolveRefiiOwnedTextureResolution(api,
                                          resource_hash,
                                          selected_name,
                                          caller_lr,
                                          &resolution,
                                          &candidate,
                                          &source)) {
    if (VfsTracingEnabled() && IsMainMenuTextureHashTraceAsset(selected_name) &&
        ShouldLogRepeatedVfsTraceOperation("TextureDescriptorMiss", 32)) {
      RecordRefiiMainMenuBlockerEvent(
          "texture_descriptor",
          fmt::format("{} miss lr=0x{:08X} hash=0x{:08X} name={}",
                      api,
                      caller_lr,
                      resource_hash,
                      NormalizeLooseAssetKey(selected_name)));
    }
    return false;
  }

  if (out_resolution != nullptr) {
    *out_resolution = resolution;
  }
  if (out_name != nullptr) {
    *out_name = selected_name;
  }
  if (out_candidate != nullptr) {
    *out_candidate = candidate;
  }
  if (out_source != nullptr) {
    *out_source = source;
  }
  return true;
}

void BuildTextureResourceDescriptor(PPCContext& source_ctx,
                                    uint8_t* base,
                                    uint32_t descriptor_guest,
                                    uint32_t mode,
                                    uint32_t hash_pointer_guest,
                                    uint32_t ref_pointer_guest) {
  constexpr uint32_t kTextureResourceDescriptorVTable = 0x820F4540;

  const uint32_t resource_hash =
      hash_pointer_guest == 0 ? 0 : LoadGuestU32(base, hash_pointer_guest);
  const uint32_t ref_object =
      ref_pointer_guest == 0 ? 0 : LoadGuestU32(base, ref_pointer_guest);

  StoreGuestU32(base, descriptor_guest + 0, kTextureResourceDescriptorVTable);
  StoreGuestU32(base, descriptor_guest + 4, 0);
  StoreGuestU32(base, descriptor_guest + 8, mode);
  StoreGuestU32(base, descriptor_guest + 12, resource_hash);
  StoreGuestU32(base, descriptor_guest + 16, ref_object);
  if (ref_object != 0) {
    AddRefGuestObject(source_ctx, base, ref_object);
  }
  StoreGuestU32(base, descriptor_guest + 20, 1024);
  StoreGuestU32(base, descriptor_guest + 24, 1);
  StoreGuestU32(base, descriptor_guest + 28, 0xFFFFFFFFu);
}

bool TryPackTextureResourceDescriptorConstantTable(PPCContext& source_ctx,
                                                  uint8_t* base,
                                                  uint32_t table_object,
                                                  uint32_t descriptor_guest,
                                                  std::string* out_failure) {
  auto fail = [&](std::string_view reason) {
    if (out_failure != nullptr) {
      *out_failure = std::string(reason);
    }
    return false;
  };

  if (base == nullptr || !LooksLikeGuestPointer(table_object) ||
      !LooksLikeGuestPointer(descriptor_guest)) {
    return fail("invalid_texture_descriptor_pack_args");
  }

  constexpr uint32_t kTextureResourceNativeDescriptorVTable = 0x82144540u;
  constexpr uint32_t kConstantTablePatchMarker = 0xF0F0F0F0u;

  PPCContext table_ctx = source_ctx;
  table_ctx.r3.u32 = table_object;
  table_ctx.r4.u32 = 4;
  table_ctx.r5.u32 = 4;
  sub_8222ABE0(table_ctx, base);
  const uint32_t patch_guest = table_ctx.r3.u32;
  if (!LooksLikeGuestPointer(patch_guest)) {
    return fail("texture_descriptor_patch_slot_missing");
  }
  StoreGuestU32(base, patch_guest, kConstantTablePatchMarker);

  table_ctx = source_ctx;
  table_ctx.r3.u32 = table_object;
  table_ctx.r4.u32 = 4;
  table_ctx.r5.u32 = 4;
  sub_8222ABE0(table_ctx, base);
  if (LooksLikeGuestPointer(table_ctx.r3.u32)) {
    StoreGuestU32(base, table_ctx.r3.u32, 4);
  }

  table_ctx = source_ctx;
  table_ctx.r3.u32 = table_object;
  table_ctx.r4.u32 = 32;
  table_ctx.r5.u32 = 4;
  sub_8222ABE0(table_ctx, base);
  const uint32_t packed_guest = table_ctx.r3.u32;
  if (!LooksLikeGuestPointer(packed_guest)) {
    StoreGuestU32(base, patch_guest, LoadGuestU32(base, table_object + 32));
    source_ctx.r3.u32 = 0;
    return fail("texture_descriptor_payload_slot_missing");
  }

  StoreGuestU32(base, packed_guest + 0, kTextureResourceNativeDescriptorVTable);
  StoreGuestU32(base, packed_guest + 4, LoadGuestU32(base, descriptor_guest + 4));
  StoreGuestU32(base, packed_guest + 8, LoadGuestU32(base, descriptor_guest + 8));
  StoreGuestU32(base, packed_guest + 12, LoadGuestU32(base, descriptor_guest + 12));
  const uint32_t ref_object = LoadGuestU32(base, descriptor_guest + 16);
  StoreGuestU32(base, packed_guest + 16, ref_object);
  if (LooksLikeGuestPointer(ref_object)) {
    AddRefGuestObject(source_ctx, base, ref_object);
  }
  StoreGuestU32(base, packed_guest + 20, LoadGuestU32(base, descriptor_guest + 20));
  StoreGuestU32(base, packed_guest + 24, LoadGuestU32(base, descriptor_guest + 24));
  StoreGuestU32(base, packed_guest + 28, LoadGuestU32(base, descriptor_guest + 28));

  StoreGuestU32(base, patch_guest, LoadGuestU32(base, table_object + 32));
  source_ctx.r3.u32 = packed_guest;
  return true;
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

bool TryResolveRefiiFrontendTextureDescriptor(
    PPCContext& ctx,
    uint8_t* base,
    uint32_t descriptor_guest,
    uint32_t resource_hash,
    uint32_t caller_lr,
    uint32_t table_index,
    uint32_t table_object_guest,
    const LooseAssetResolution& resolution,
    std::string_view name,
    std::string_view candidate,
    std::string_view source,
    uint32_t* out_resolver_result,
    std::string* out_failure_reason) {
  auto fail = [&](std::string_view reason) {
    if (out_failure_reason != nullptr) {
      *out_failure_reason = std::string(reason);
    }
    return false;
  };

  if (out_resolver_result != nullptr) {
    *out_resolver_result = 0;
  }
  if (base == nullptr || descriptor_guest < 0x10000) {
    return fail("invalid_descriptor");
  }

  const PPCContext caller_ctx = ctx;
  constexpr uint32_t kFrontendResolverPoolBase = 0x8349F850;
  constexpr uint32_t kFrontendResolverTableObjectBase = 0x8349F9B0;
  if (table_index >= 16) {
    return fail("table_index_out_of_range");
  }

  const uint32_t vector_guest =
      kFrontendResolverPoolBase + 16 + table_index * 16;
  uint32_t vector_begin = LoadGuestU32(base, vector_guest + 4);
  uint32_t vector_end = LoadGuestU32(base, vector_guest + 8);
  if (vector_end == vector_begin) {
    PPCContext fill_ctx = caller_ctx;
    fill_ctx.r3.u32 = kFrontendResolverPoolBase;
    fill_ctx.r4.u32 = table_index;
    sub_82A8F6E8(fill_ctx, base);
    vector_begin = LoadGuestU32(base, vector_guest + 4);
    vector_end = LoadGuestU32(base, vector_guest + 8);
  }
  if (vector_end == vector_begin || vector_end < vector_begin + 4) {
    return fail("resolver_handle_pool_empty");
  }

  const uint32_t resolver_result = LoadGuestU32(base, vector_end - 4);
  StoreGuestU32(base, vector_guest + 8, vector_end - 4);
  if (resolver_result == 0xFFFFFFFFu) {
    return fail("resolver_handle_invalid");
  }

  const uint32_t table_entry_guest =
      kFrontendResolverTableObjectBase + table_index * 4;
  const uint32_t table_object =
      table_object_guest != 0 ? table_object_guest
                              : LoadGuestU32(base, table_entry_guest);
  if (table_object == 0) {
    return fail("table_object_missing");
  }

  PPCContext table_ctx = caller_ctx;
  table_ctx.r3.u32 = table_object;
  table_ctx.r4.u32 = 3;
  sub_8222AB78(table_ctx, base);

  table_ctx.r3.u32 = table_object;
  table_ctx.r4.u32 = 4;
  table_ctx.r5.u32 = 4;
  sub_8222ABE0(table_ctx, base);
  if (table_ctx.r3.u32 != 0) {
    StoreGuestU32(base, table_ctx.r3.u32, resolver_result);
  }

  table_ctx.r3.u32 = table_object;
  table_ctx.r4.u32 = 4;
  table_ctx.r5.u32 = 4;
  sub_8222ABE0(table_ctx, base);
  if (table_ctx.r3.u32 != 0) {
    StoreGuestU32(base, table_ctx.r3.u32, caller_ctx.r4.u32);
  }

  const uint32_t descriptor_vtable =
      descriptor_guest == 0 ? 0 : LoadGuestU32(base, descriptor_guest);
  const uint32_t descriptor_slot12 =
      descriptor_vtable == 0 ? 0 : LoadGuestU32(base, descriptor_vtable + 12);
  const uint32_t descriptor_slot16 =
      descriptor_vtable == 0 ? 0 : LoadGuestU32(base, descriptor_vtable + 16);
  bool packed_descriptor = false;
  if (descriptor_vtable == 0x820F4540u && descriptor_slot12 == 0x82A41748u &&
      descriptor_slot16 == 0x82EA63A8u) {
    std::string pack_failure;
    table_ctx = caller_ctx;
    packed_descriptor = TryPackTextureResourceDescriptorConstantTable(
        table_ctx, base, table_object, descriptor_guest, &pack_failure);
    if (!packed_descriptor &&
        (VfsTracingEnabled() || LooseAssetOverrideTracingEnabled() ||
         IsMainMenuTextureHashTraceAsset(name) ||
         IsMainMenuTextureHashTraceAsset(candidate) ||
         IsMainMenuTextureHashTraceAsset(resolution.provider_relative_path))) {
      REXLOG_WARN("Refii VFS frontend texture descriptor direct pack failed; "
                  "falling back to serializer lr=0x{:08X} hash=0x{:08X} "
                  "descriptor=0x{:08X} table_object=0x{:08X} reason={}",
                  caller_lr,
                  resource_hash,
                  descriptor_guest,
                  table_object,
                  pack_failure);
    }
  }
  if (!packed_descriptor) {
    PPCFunc* descriptor_serializer = FindGuestFunction(descriptor_slot12);
    if (descriptor_serializer == nullptr) {
      return fail("descriptor_serializer_missing");
    }

    table_ctx.r3.u32 = descriptor_guest;
    table_ctx.r4.u32 = table_object;
    table_ctx.ctr.u32 = descriptor_slot12;
    descriptor_serializer(table_ctx, base);
  }

  const uint32_t table_writeback_guest = LoadGuestU32(base, table_object + 44);
  const uint32_t table_cursor = LoadGuestU32(base, table_object + 32);
  if (table_writeback_guest != 0) {
    StoreGuestU32(base, table_writeback_guest, table_cursor);
  }
  StoreGuestU32(base, table_object + 40, table_cursor);

  ctx = caller_ctx;
  ctx.r3.u32 = resolver_result;
  if (out_resolver_result != nullptr) {
    *out_resolver_result = resolver_result;
  }

  if (VfsTracingEnabled() || LooseAssetOverrideTracingEnabled() ||
      IsMainMenuTextureHashTraceAsset(name) ||
      IsMainMenuTextureHashTraceAsset(candidate) ||
      IsMainMenuTextureHashTraceAsset(resolution.provider_relative_path)) {
    REXLOG_INFO("Refii VFS frontend texture descriptor table fallback "
                "lr=0x{:08X} hash=0x{:08X} descriptor=0x{:08X} "
                "name={} candidate={} source={} provider_relative={} "
                "host={} table_index={} table_object=0x{:08X} "
                "resolver_result=0x{:08X} vector=0x{:08X} "
                "descriptor_words=[{}] table_words=[{}]",
                caller_lr,
                resource_hash,
                descriptor_guest,
                NormalizeLooseAssetKey(name),
                NormalizeLooseAssetKey(candidate),
                source,
                NormalizeLooseAssetKey(resolution.provider_relative_path),
                resolution.host_path.string(),
                table_index,
                table_object,
                resolver_result,
                vector_guest,
                FormatGuestWordsIfPointer(base, descriptor_guest, 8),
                FormatGuestWordsIfPointer(base, table_object, 14));
    RecordRefiiMainMenuBlockerEvent(
        "frontend_descriptor_refii_fallback",
        fmt::format("lr=0x{:08X} hash=0x{:08X} name={} candidate={} "
                    "resolver_result=0x{:08X} table_index={} "
                    "table_object=0x{:08X}",
                    caller_lr,
                    resource_hash,
                    NormalizeLooseAssetKey(name),
                    NormalizeLooseAssetKey(candidate),
                    resolver_result,
                    table_index,
                    table_object));
  }

  LogRefiiVfsChainVerdict("frontend_texture_consumer",
                          caller_lr,
                          name,
                          candidate,
                          resolution.provider_relative_path,
                          "Fable2_constant_table_descriptor",
                          "sub_821DE2E8_refii_fallback",
                          "TraceOnce",
                          "ResourceManager_Open_or_DB20");
  return true;
}

bool IsGameFaceVirtualDirectory(std::string_view path);
std::string StripRefiiDataPrefix(std::string_view value);
std::shared_ptr<const RefiiFindDirectoryIndex> GetRefiiFindDirectoryIndex(
    const std::filesystem::path& loose_root);
std::vector<RefiiPrivateFindEntry> GetRefiiDirectoryEntriesForParentFromDatabase(
    const std::filesystem::path& loose_root,
    std::string_view parent);
std::vector<std::string> BuildRefiiDirectoryAliasParents(std::string_view parent);

bool ResolveRefiiVirtualFileSystemProbe(std::string_view asset_path,
                                        bool* out_is_directory,
                                        LooseAssetResolution* out_resolution,
                                        std::string* out_candidate) {
  if (!RefiiLooseAssetBackendEnabled() || asset_path.empty()) {
    return false;
  }

  const std::string normalized = NormalizeLooseAssetKey(asset_path);
  if (normalized.empty() || normalized.find('*') != std::string::npos ||
      normalized.find('?') != std::string::npos ||
      normalized.find(':') != std::string::npos ||
      IsCachedOptionalSyntheticVfsMiss(normalized)) {
    return false;
  }

  LooseAssetResolution resolution;
  std::string candidate;
  bool resolved = false;
  const bool should_probe_file_aliases =
      LooksLikeConcreteVfsFilePath(normalized);
  if (!should_probe_file_aliases && IsGameFaceBoundaryPath(normalized) &&
      IsGameFaceVirtualDirectory(normalized)) {
    if (out_is_directory != nullptr) {
      *out_is_directory = true;
    }
    if (out_candidate != nullptr) {
      *out_candidate = StripTrailingSlashes(normalized);
    }
    return true;
  }
  if (IsGameFaceBoundaryPath(normalized)) {
    if (should_probe_file_aliases && IsLuaScriptAssetKey(normalized)) {
      resolved = ResolveRefiiLuaScriptAsset(
          normalized, &resolution, &candidate);
    }
    if (!resolved && should_probe_file_aliases) {
      for (const std::string& gameface_candidate :
           BuildGameFaceRedirectCandidates(normalized)) {
        if (ResolveVfsDatabaseAliasLooseAsset(
                gameface_candidate, &resolution, &candidate)) {
          resolved = true;
          break;
        }
      }
    }
  }
  if (!ShouldSkipLooseNativeAsset(normalized, "filesystem_probe")) {
    if (!resolved) {
      resolved = ResolveSyntheticBankManifestLooseAsset(
          normalized, &resolution, &candidate);
    }
    if (!resolved && should_probe_file_aliases) {
      resolved = ResolveVfsDatabaseAliasLooseAsset(
          normalized, &resolution, &candidate);
    }
    if (!resolved && should_probe_file_aliases) {
      resolved = ResolveSyntheticBankEntryLooseAsset(
          normalized, &resolution, &candidate);
    }
  }

  if (resolved) {
    const LooseAssetResolution resolution_for_log = resolution;
    if (out_is_directory != nullptr) {
      *out_is_directory = false;
    }
    if (out_resolution != nullptr) {
      *out_resolution = std::move(resolution);
    }
    if (out_candidate != nullptr) {
      *out_candidate = candidate.empty() ? normalized : candidate;
    }
    if (VfsTracingEnabled() || LooseAssetOverrideTracingEnabled()) {
      RefiiVfsOwnershipDecision decision;
      decision.kind = RefiiVfsOwnershipKind::RefiiOwned;
      decision.normalized = normalized;
      decision.candidate = candidate.empty() ? normalized : candidate;
      decision.source = "filesystem_probe";
      decision.resolution = resolution_for_log;
      decision.has_resolution = true;
      LogRefiiVfsOwnershipDecision("FileApiProbe", 0, normalized, decision);
    }
    return true;
  }

  if (!should_probe_file_aliases) {
    const std::filesystem::path loose_root = GetLooseAssetRoot();
    if (!loose_root.empty()) {
      std::string directory_candidate = normalized;
      if (StartsWith(directory_candidate, "data/")) {
        directory_candidate.erase(0, 5);
      }
      const std::string stripped_directory =
          StripRefiiDataPrefix(directory_candidate);
      if (RefiiVfsDatabaseExists(loose_root)) {
        if (!GetRefiiDirectoryEntriesForParentFromDatabase(
                 loose_root, stripped_directory)
                 .empty()) {
          if (out_is_directory != nullptr) {
            *out_is_directory = true;
          }
          if (out_candidate != nullptr) {
            *out_candidate = stripped_directory;
          }
          return true;
        }
        for (const std::string& alias_parent :
             BuildRefiiDirectoryAliasParents(stripped_directory)) {
          if (GetRefiiDirectoryEntriesForParentFromDatabase(
                  loose_root, alias_parent)
                  .empty()) {
            continue;
          }
          if (out_is_directory != nullptr) {
            *out_is_directory = true;
          }
          if (out_candidate != nullptr) {
            *out_candidate = alias_parent;
          }
          return true;
        }
        return false;
      }
      if (const auto index = GetRefiiFindDirectoryIndex(loose_root);
          index != nullptr &&
          index->entries_by_parent.find(stripped_directory) !=
              index->entries_by_parent.end()) {
        if (out_is_directory != nullptr) {
          *out_is_directory = true;
        }
        if (out_candidate != nullptr) {
          *out_candidate = stripped_directory;
        }
        return true;
      }
      const std::filesystem::path host_directory =
          loose_root / std::filesystem::path(directory_candidate);
      std::error_code error;
      if (std::filesystem::is_directory(host_directory, error)) {
        if (out_is_directory != nullptr) {
          *out_is_directory = true;
        }
        if (out_candidate != nullptr) {
          *out_candidate = directory_candidate;
        }
        return true;
      }
    }
  }

  return false;
}

std::mutex& RefiiPrivateHandleMutex() {
  static std::mutex mutex;
  return mutex;
}

std::unordered_map<uint32_t, std::shared_ptr<RefiiPrivateFileHandleRecord>>&
RefiiPrivateFileHandles() {
  static std::unordered_map<uint32_t,
                            std::shared_ptr<RefiiPrivateFileHandleRecord>>
      handles;
  return handles;
}

std::unordered_map<uint32_t, std::shared_ptr<RefiiPrivateFindHandleRecord>>&
RefiiPrivateFindHandles() {
  static std::unordered_map<uint32_t,
                            std::shared_ptr<RefiiPrivateFindHandleRecord>>
      handles;
  return handles;
}

uint32_t AllocateRefiiPrivateHandleId() {
  static std::atomic<uint32_t> next_id{1};
  for (;;) {
    const uint32_t id =
        (next_id.fetch_add(1, std::memory_order_relaxed) & 0x00FFFFFFu);
    const uint32_t handle = kRefiiPrivateHandleBase | (id == 0 ? 1 : id);
    std::lock_guard<std::mutex> lock(RefiiPrivateHandleMutex());
    if (!RefiiPrivateFileHandles().contains(handle) &&
        !RefiiPrivateFindHandles().contains(handle)) {
      return handle;
    }
  }
}

bool IsRefiiPrivateHandle(uint32_t handle) {
  return (handle & 0xFF000000u) == kRefiiPrivateHandleBase &&
         handle != kRefiiPrivateHandleInvalid;
}

uint32_t RegisterRefiiPrivateFileHandle(
    std::shared_ptr<RefiiPrivateFileHandleRecord> record) {
  if (record == nullptr) {
    return kRefiiPrivateHandleInvalid;
  }
  const uint32_t handle = AllocateRefiiPrivateHandleId();
  {
    std::lock_guard<std::mutex> lock(RefiiPrivateHandleMutex());
    RefiiPrivateFileHandles().emplace(handle, std::move(record));
  }
  LooseStats().active_private_file_handles.fetch_add(
      1, std::memory_order_relaxed);
  return handle;
}

uint32_t RegisterRefiiPrivateFindHandle(
    std::shared_ptr<RefiiPrivateFindHandleRecord> record) {
  if (record == nullptr) {
    return kRefiiPrivateHandleInvalid;
  }
  const uint32_t handle = AllocateRefiiPrivateHandleId();
  {
    std::lock_guard<std::mutex> lock(RefiiPrivateHandleMutex());
    RefiiPrivateFindHandles().emplace(handle, std::move(record));
  }
  LooseStats().active_private_find_handles.fetch_add(
      1, std::memory_order_relaxed);
  return handle;
}

std::shared_ptr<RefiiPrivateFileHandleRecord> FindRefiiPrivateFileHandle(
    uint32_t handle) {
  std::lock_guard<std::mutex> lock(RefiiPrivateHandleMutex());
  const auto match = RefiiPrivateFileHandles().find(handle);
  return match == RefiiPrivateFileHandles().end() ? nullptr : match->second;
}

std::shared_ptr<RefiiPrivateFindHandleRecord> FindRefiiPrivateFindHandle(
    uint32_t handle) {
  std::lock_guard<std::mutex> lock(RefiiPrivateHandleMutex());
  const auto match = RefiiPrivateFindHandles().find(handle);
  return match == RefiiPrivateFindHandles().end() ? nullptr : match->second;
}

bool CloseRefiiPrivateHandle(uint32_t handle) {
  std::lock_guard<std::mutex> lock(RefiiPrivateHandleMutex());
  const size_t erased_file = RefiiPrivateFileHandles().erase(handle);
  if (erased_file != 0) {
    LooseStats().active_private_file_handles.fetch_sub(
        erased_file, std::memory_order_relaxed);
    return true;
  }
  const size_t erased_find = RefiiPrivateFindHandles().erase(handle);
  if (erased_find != 0) {
    LooseStats().active_private_find_handles.fetch_sub(
        erased_find, std::memory_order_relaxed);
    return true;
  }
  return false;
}

size_t RefiiPrivateHandleCount() {
  std::lock_guard<std::mutex> lock(RefiiPrivateHandleMutex());
  return RefiiPrivateFileHandles().size() + RefiiPrivateFindHandles().size();
}

void LogRefiiVfsRuntimePolicyOnce();

bool TryCreateRefiiPrivateFileHandle(std::string_view path,
                                     uint32_t desired_access,
                                     uint32_t creation_disposition,
                                     uint32_t* out_handle,
                                     LooseAssetResolution* out_resolution,
                                     std::string* out_candidate) {
  if (out_handle == nullptr || !RefiiLooseAssetBackendEnabled()) {
    return false;
  }
  LogRefiiVfsRuntimePolicyOnce();

  constexpr uint32_t kGenericWrite = 0x40000000u;
  constexpr uint32_t kCreateAlways = 2;
  constexpr uint32_t kOpenExisting = 3;
  constexpr uint32_t kOpenAlways = 4;
  if ((desired_access & kGenericWrite) != 0 ||
      (creation_disposition != 0 &&
       creation_disposition != kOpenExisting &&
       creation_disposition != kOpenAlways &&
       creation_disposition != kCreateAlways)) {
    return false;
  }

  bool is_directory = false;
  LooseAssetResolution resolution;
  std::string candidate;
  if (!ResolveRefiiVirtualFileSystemProbe(
          path, &is_directory, &resolution, &candidate) ||
      is_directory) {
    return false;
  }

  auto record = std::make_shared<RefiiPrivateFileHandleRecord>();
  record->path = resolution.host_path.lexically_normal();
  record->virtual_path = resolution.provider_relative_path;
  std::error_code error;
  record->size = std::filesystem::file_size(record->path, error);
  if (error) {
    return false;
  }
  record->file.open(record->path, std::ios::binary);
  if (!record->file) {
    return false;
  }

  *out_handle = RegisterRefiiPrivateFileHandle(record);
  if (out_resolution != nullptr) {
    *out_resolution = std::move(resolution);
  }
  if (out_candidate != nullptr) {
    *out_candidate = std::move(candidate);
  }
  return *out_handle != kRefiiPrivateHandleInvalid;
}

uint64_t GetOverlappedOffset(uint8_t* base, uint32_t overlapped_guest) {
  if (overlapped_guest == 0) {
    return 0;
  }
  return static_cast<uint64_t>(LoadGuestU32(base, overlapped_guest + 8)) |
         (static_cast<uint64_t>(LoadGuestU32(base, overlapped_guest + 12)) << 32);
}

uint32_t ReadRefiiPrivateFileHandleBytes(uint8_t* base,
                                         uint32_t handle,
                                         uint32_t buffer_guest,
                                         uint32_t requested_bytes,
                                         uint32_t overlapped_guest) {
  ZoneScopedN("RefiiVFS/ReadPrivateFileHandle");
  ConfigureRefiiVfsTracyPlots();
  TracyPlot("Refii VFS Read Request Bytes",
            static_cast<int64_t>(requested_bytes));
  if (base == nullptr || buffer_guest == 0) {
    return 0;
  }

  const auto record = FindRefiiPrivateFileHandle(handle);
  if (record == nullptr) {
    return 0;
  }

  std::lock_guard<std::mutex> lock(record->mutex);
  uint64_t offset =
      overlapped_guest == 0 ? record->position
                            : GetOverlappedOffset(base, overlapped_guest);
  if (offset > record->size) {
    return 0;
  }

  requested_bytes = static_cast<uint32_t>(
      std::min<uint64_t>(requested_bytes, record->size - offset));
  record->file.clear();
  record->file.seekg(static_cast<std::streamoff>(offset), std::ios::beg);

  uint32_t total_read = 0;
  std::array<uint8_t, 64 * 1024> scratch{};
  while (total_read < requested_bytes) {
    const uint32_t remaining = requested_bytes - total_read;
    const uint32_t chunk_size =
        std::min<uint32_t>(remaining, static_cast<uint32_t>(scratch.size()));
    record->file.read(reinterpret_cast<char*>(scratch.data()),
                      static_cast<std::streamsize>(chunk_size));
    const uint32_t read =
        static_cast<uint32_t>(std::max<std::streamsize>(record->file.gcount(), 0));
    if (read == 0) {
      break;
    }

    uint32_t copied = 0;
    while (copied < read) {
      const uint32_t guest = buffer_guest + total_read + copied;
      const uint32_t page_remaining = 0x1000u - (guest & 0x0FFFu);
      const uint32_t copy_size = std::min<uint32_t>(read - copied, page_remaining);
      std::memcpy(rex::memory::GuestPtr<uint8_t*>(base, guest),
                  scratch.data() + copied,
                  copy_size);
      copied += copy_size;
    }
    total_read += read;
    if (read != chunk_size) {
      break;
    }
  }

  if (overlapped_guest == 0) {
    record->position = offset + total_read;
  }
  const uint64_t read_end = offset + total_read;
  record->max_read_end = std::max<uint64_t>(record->max_read_end, read_end);
  if (!record->fully_read_reported && record->size != 0 &&
      record->max_read_end >= record->size &&
      IsRefiiPostLoadingMiddlewareVideoPath(record->virtual_path)) {
    record->fully_read_reported = true;
    RecordRefiiPostLoadingMiddlewareVideoReadComplete(*record, handle, read_end);
  }
  TracyPlot("Refii VFS Read Completed Bytes",
            static_cast<int64_t>(total_read));
  return total_read;
}

bool TryReadRefiiPrivateFileHandle(PPCContext& ctx,
                                   uint8_t* base,
                                   uint32_t handle,
                                   uint32_t buffer_guest,
                                   uint32_t requested_bytes,
                                   uint32_t bytes_read_guest,
                                   uint32_t overlapped_guest) {
  const auto record = FindRefiiPrivateFileHandle(handle);
  if (record == nullptr) {
    return false;
  }
  const bool was_post_loading_middleware_complete =
      RefiiPostLoadingMiddlewareVideoReadComplete();
  if (bytes_read_guest != 0) {
    StoreGuestU32(base, bytes_read_guest, 0);
  }
  const uint32_t bytes_read = ReadRefiiPrivateFileHandleBytes(
      base, handle, buffer_guest, requested_bytes, overlapped_guest);
  if (bytes_read_guest != 0) {
    StoreGuestU32(base, bytes_read_guest, bytes_read);
  }
  if (overlapped_guest != 0) {
    StoreGuestU32(base, overlapped_guest + 0, 0);
    StoreGuestU32(base, overlapped_guest + 4, bytes_read);
  }
  if (!was_post_loading_middleware_complete &&
      RefiiPostLoadingMiddlewareVideoReadComplete() &&
      IsRefiiPostLoadingMiddlewareVideoPath(record->virtual_path)) {
    constexpr uint32_t kAfterHolderSingleton = 0x834C20B8u;
    PPCContext holder_ctx = ctx;
    holder_ctx.r3.u32 = kAfterHolderSingleton;
    holder_ctx.r4.u32 = SelectRefiiPostLoadingAfterHolderRenderOwner();
    holder_ctx.lr = static_cast<uint32_t>(ctx.lr);
    const bool ready_marked = TryMarkRefiiPostLoadingAfterHolderReady(
        holder_ctx,
        base,
        kAfterHolderSingleton,
        "private_middleware_bik_read_complete");
    const bool advanced =
        !ready_marked && TryAdvanceRefiiPostLoadingAfterHolderMode4(
                             holder_ctx,
                             base,
                             kAfterHolderSingleton,
                             "private_middleware_bik_read_complete");
    const bool finalized =
        !ready_marked && !advanced &&
        TryFinalizeRefiiOwnerlessPostLoadingAfterHolderState(
            holder_ctx,
            base,
            kAfterHolderSingleton,
            "private_middleware_bik_read_complete");
    if (VfsTracingEnabled() ||
        ShouldLogRepeatedVfsTraceOperation(
            "PostLoadingMiddlewareBikAfterHolderKick", 8)) {
      REXLOG_INFO("Refii VFS post-loading middleware BIK completion kick "
                  "handle=0x{:08X} path={} ready_marked={} advanced={} "
                  "finalized={} holder=[{}] handoff=[{}] main_menu=[{}]",
                  handle,
                  record->virtual_path,
                  ready_marked ? "true" : "false",
                  advanced ? "true" : "false",
                  finalized ? "true" : "false",
                  FormatRefiiPostLoadingAfterHolderState(
                      base, kAfterHolderSingleton),
                  FormatRefiiPostVideoHandoffTraceForLog(),
                  FormatRefiiMainMenuBlockerTraceForLog());
    }
  }
  ctx.r3.u32 = 1;
  return true;
}

bool TryGetRefiiPrivateFileSize(uint32_t handle, uint64_t* out_size) {
  if (out_size == nullptr) {
    return false;
  }
  const auto record = FindRefiiPrivateFileHandle(handle);
  if (record == nullptr) {
    return false;
  }
  std::lock_guard<std::mutex> lock(record->mutex);
  *out_size = record->size;
  return true;
}

bool TrySetRefiiPrivateFilePointer(PPCContext& ctx,
                                   uint8_t* base,
                                   uint32_t handle,
                                   int32_t distance_low,
                                   uint32_t distance_high_guest,
                                   uint32_t move_method) {
  const auto record = FindRefiiPrivateFileHandle(handle);
  if (record == nullptr) {
    return false;
  }

  int64_t distance = static_cast<int64_t>(distance_low);
  if (distance_high_guest != 0) {
    const uint64_t high =
        static_cast<uint64_t>(LoadGuestU32(base, distance_high_guest));
    distance = static_cast<int64_t>((high << 32) |
                                    static_cast<uint32_t>(distance_low));
  }

  std::lock_guard<std::mutex> lock(record->mutex);
  int64_t origin = 0;
  switch (move_method) {
    case 0:  // FILE_BEGIN
      origin = 0;
      break;
    case 1:  // FILE_CURRENT
      origin = static_cast<int64_t>(record->position);
      break;
    case 2:  // FILE_END
      origin = static_cast<int64_t>(record->size);
      break;
    default:
      ctx.r3.u32 = 0xFFFFFFFFu;
      if (distance_high_guest != 0) {
        StoreGuestU32(base, distance_high_guest, 0xFFFFFFFFu);
      }
      return true;
  }

  const int64_t target = origin + distance;
  if (target < 0) {
    ctx.r3.u32 = 0xFFFFFFFFu;
    if (distance_high_guest != 0) {
      StoreGuestU32(base, distance_high_guest, 0xFFFFFFFFu);
    }
    return true;
  }

  record->position = static_cast<uint64_t>(target);
  if (distance_high_guest != 0) {
    StoreGuestU32(base,
                  distance_high_guest,
                  static_cast<uint32_t>(record->position >> 32));
  }
  ctx.r3.u32 = static_cast<uint32_t>(record->position);
  return true;
}

bool MatchesFindPattern(std::string_view name, std::string_view pattern) {
  if (pattern.empty() || pattern == "*") {
    return true;
  }
  size_t name_index = 0;
  size_t pattern_index = 0;
  size_t star = std::string_view::npos;
  size_t star_name = 0;
  while (name_index < name.size()) {
    if (pattern_index < pattern.size() &&
        (pattern[pattern_index] == '?' ||
         std::tolower(static_cast<unsigned char>(pattern[pattern_index])) ==
             std::tolower(static_cast<unsigned char>(name[name_index])))) {
      ++name_index;
      ++pattern_index;
    } else if (pattern_index < pattern.size() && pattern[pattern_index] == '*') {
      star = pattern_index++;
      star_name = name_index;
    } else if (star != std::string_view::npos) {
      pattern_index = star + 1;
      name_index = ++star_name;
    } else {
      return false;
    }
  }
  while (pattern_index < pattern.size() && pattern[pattern_index] == '*') {
    ++pattern_index;
  }
  return pattern_index == pattern.size();
}

void WriteRefiiFindData(uint8_t* base,
                        uint32_t find_data_guest,
                        const RefiiPrivateFindEntry& entry) {
  if (base == nullptr || find_data_guest == 0) {
    return;
  }
  std::memset(rex::memory::GuestPtr<uint8_t*>(base, find_data_guest), 0, 328);
  StoreGuestU32(base, find_data_guest + 0, entry.directory ? 0x10u : 0x81u);
  StoreGuestU32(base, find_data_guest + 28,
                static_cast<uint32_t>(entry.size >> 32));
  StoreGuestU32(base, find_data_guest + 32,
                static_cast<uint32_t>(entry.size));
  auto* name_out = rex::memory::GuestPtr<uint8_t*>(base, find_data_guest + 44);
  const size_t count = std::min<size_t>(entry.name.size(), 259);
  std::memcpy(name_out, entry.name.data(), count);
  name_out[count] = 0;
}

void ClearRefiiFindData(uint8_t* base, uint32_t find_data_guest) {
  if (base == nullptr || find_data_guest == 0) {
    return;
  }
  std::memset(rex::memory::GuestPtr<uint8_t*>(base, find_data_guest), 0, 328);
}

void LogRefiiVfsRuntimePolicyOnce() {
  if (!VfsTracingEnabled()) {
    return;
  }
  static std::atomic<bool> logged{false};
  bool expected = false;
  if (!logged.compare_exchange_strong(expected, true,
                                      std::memory_order_relaxed)) {
    return;
  }
  REXLOG_INFO("Refii VFS runtime policy backend=refii ownership=typed-db "
              "find_policy=runtime-parent-cache "
              "directory_probe=refii_vfs-parent+host-fallback");
}

std::string StripRefiiDataPrefix(std::string_view value) {
  std::string normalized = NormalizeLooseAssetKey(value);
  if (StartsWith(normalized, "data/")) {
    normalized.erase(0, 5);
  }
  return StripTrailingSlashes(std::move(normalized));
}

std::string RefiiFindParent(std::string_view normalized,
                            std::string* out_pattern) {
  const size_t slash = normalized.find_last_of('/');
  if (slash == std::string::npos) {
    if (out_pattern != nullptr) {
      *out_pattern = std::string(normalized);
    }
    return {};
  }
  if (out_pattern != nullptr) {
    *out_pattern = std::string(normalized.substr(slash + 1));
  }
  return std::string(normalized.substr(0, slash));
}

void AddRefiiFindEntry(std::vector<RefiiPrivateFindEntry>* entries,
                       std::unordered_set<std::string>* seen,
                       std::string_view name,
                       bool directory,
                       uint64_t size,
                       std::string_view pattern);
std::shared_ptr<const SyntheticVfsEntryIndex> GetSyntheticVfsEntryIndex();

void AddRefiiDirectoryIndexEntry(
    RefiiFindDirectoryIndex* index,
    std::unordered_map<std::string, std::unordered_set<std::string>>* seen,
    std::string_view path,
    bool directory) {
  if (index == nullptr || seen == nullptr) {
    return;
  }

  const std::string stripped = StripRefiiDataPrefix(path);
  if (stripped.empty() || IsInternalLooseAssetKey(stripped)) {
    return;
  }

  std::string name;
  const std::string parent = RefiiFindParent(stripped, &name);
  if (name.empty()) {
    return;
  }

  std::string normalized_name(name);
  std::ranges::transform(normalized_name, normalized_name.begin(),
                         [](unsigned char ch) {
                           return static_cast<char>(std::tolower(ch));
                         });
  auto& parent_seen = (*seen)[parent];
  if (!parent_seen.insert(normalized_name).second) {
    return;
  }

  index->entries_by_parent[parent].push_back({std::move(name), directory, 0});
  if (directory) {
    ++index->directory_count;
  } else {
    ++index->file_count;
  }
}

std::shared_ptr<const RefiiFindDirectoryIndex> GetRefiiFindDirectoryIndex(
    const std::filesystem::path& loose_root) {
  const std::string root_key = loose_root.lexically_normal().string();
  if (root_key.empty()) {
    return {};
  }

  {
    std::lock_guard<std::mutex> lock(RefiiFindDirectoryIndexMutex());
    const auto& cached = RefiiFindDirectoryIndexInstance();
    if (cached.root_key == root_key && cached.loaded) {
      return std::shared_ptr<const RefiiFindDirectoryIndex>(
          &cached, [](const RefiiFindDirectoryIndex*) {});
    }
  }

  auto built = std::make_shared<RefiiFindDirectoryIndex>();
  built->root_key = root_key;
  std::unordered_map<std::string, std::unordered_set<std::string>> seen;

  std::ifstream directories(RefiiVfsRuntimeDirectoryTablePath(loose_root));
  std::string line;
  while (std::getline(directories, line)) {
    if (!line.empty() && line.back() == '\r') {
      line.pop_back();
    }
    if (line.empty() || line[0] == '#') {
      continue;
    }
    AddRefiiDirectoryIndexEntry(built.get(), &seen, line, true);
  }

  for (const std::string& candidate :
       GetRefiiVfsRuntimeCandidatesCached(loose_root)) {
    AddRefiiDirectoryIndexEntry(built.get(), &seen, candidate, false);
  }

  if (const auto synthetic_index = GetSyntheticVfsEntryIndex();
      synthetic_index != nullptr) {
    for (const auto& [alias, entry] : synthetic_index->by_normalized_path) {
      (void)entry;
      AddRefiiDirectoryIndexEntry(built.get(), &seen, alias, false);
    }
  }

  for (auto& [parent, entries] : built->entries_by_parent) {
    (void)parent;
    std::sort(entries.begin(), entries.end(),
              [](const auto& left, const auto& right) {
                if (left.directory != right.directory) {
                  return left.directory && !right.directory;
                }
                return left.name < right.name;
              });
  }
  built->loaded = true;

  {
    std::lock_guard<std::mutex> lock(RefiiFindDirectoryIndexMutex());
    auto& cached = RefiiFindDirectoryIndexInstance();
    if (cached.root_key != root_key || !cached.loaded) {
      cached = std::move(*built);
      if (VfsTracingEnabled() ||
          ShouldLogLooseStatsMilestone(cached.entries_by_parent.size())) {
        REXLOG_INFO("Refii VFS FindFirstFileA directory index loaded "
                    "parents={} directories={} files={} root={}",
                    cached.entries_by_parent.size(),
                    cached.directory_count,
                    cached.file_count,
                    loose_root.string());
      }
    }
    return std::shared_ptr<const RefiiFindDirectoryIndex>(
        &cached, [](const RefiiFindDirectoryIndex*) {});
  }
}

std::vector<RefiiPrivateFindEntry> GetRefiiDirectoryEntriesForParentFromDatabase(
    const std::filesystem::path& loose_root,
    std::string_view parent) {
  const std::string root_key = loose_root.lexically_normal().string();
  const std::string stripped_parent = StripRefiiDataPrefix(parent);
  if (root_key.empty() || stripped_parent.empty()) {
    return {};
  }

  {
    std::lock_guard<std::mutex> lock(RefiiFindDirectoryParentCacheMutex());
    auto& cache = RefiiFindDirectoryParentCacheInstance();
    if (cache.root_key != root_key) {
      cache = RefiiFindDirectoryParentCache{};
      cache.root_key = root_key;
    }
    if (cache.loaded_parents.contains(stripped_parent)) {
      const auto match = cache.entries_by_parent.find(stripped_parent);
      return match == cache.entries_by_parent.end()
                 ? std::vector<RefiiPrivateFindEntry>{}
                 : match->second;
    }
  }

  std::vector<RefiiPrivateFindEntry> entries;
  std::unordered_set<std::string> seen;
  auto add_path = [&](std::string_view path, bool directory) {
    const std::string stripped = StripRefiiDataPrefix(path);
    if (stripped.empty() || IsInternalLooseAssetKey(stripped)) {
      return;
    }

    std::string name;
    const std::string entry_parent = RefiiFindParent(stripped, &name);
    bool entry_directory = directory;
    if (entry_parent != stripped_parent) {
      const std::string parent_prefix = stripped_parent + "/";
      if (!StartsWith(stripped, parent_prefix)) {
        return;
      }
      std::string_view tail(stripped);
      tail.remove_prefix(parent_prefix.size());
      const size_t separator = tail.find('/');
      if (separator == std::string_view::npos || separator == 0) {
        return;
      }
      name = std::string(tail.substr(0, separator));
      entry_directory = true;
    }
    if (name.empty()) {
      return;
    }

    std::string normalized_name(name);
    std::ranges::transform(normalized_name,
                           normalized_name.begin(),
                           [](unsigned char ch) {
                             return static_cast<char>(std::tolower(ch));
                           });
    if (!seen.insert(normalized_name).second) {
      return;
    }
    entries.push_back({std::move(name), entry_directory, 0});
  };

  std::ifstream directories(RefiiVfsRuntimeDirectoryTablePath(loose_root));
  std::string line;
  while (std::getline(directories, line)) {
    if (!line.empty() && line.back() == '\r') {
      line.pop_back();
    }
    if (line.empty() || line[0] == '#') {
      continue;
    }
    add_path(line, true);
  }

  for (const std::string& candidate :
       GetRefiiVfsRuntimeCandidatesCached(loose_root)) {
    add_path(candidate, false);
  }

  for (const RefiiVfsRuntimeLuaRecord& record :
       GetRefiiVfsRuntimeLuaRecordsCached(loose_root)) {
    add_path(record.alias, false);
    if (record.candidate != record.alias) {
      add_path(record.candidate, false);
    }
  }

  if (const auto synthetic_index = GetSyntheticVfsEntryIndex();
      synthetic_index != nullptr) {
    for (const auto& [alias, entry] : synthetic_index->by_normalized_path) {
      (void)entry;
      add_path(alias, false);
    }
  }

  std::sort(entries.begin(), entries.end(),
            [](const auto& left, const auto& right) {
              if (left.directory != right.directory) {
                return left.directory && !right.directory;
              }
              return left.name < right.name;
            });

  {
    std::lock_guard<std::mutex> lock(RefiiFindDirectoryParentCacheMutex());
    auto& cache = RefiiFindDirectoryParentCacheInstance();
    if (cache.root_key != root_key) {
      cache = RefiiFindDirectoryParentCache{};
      cache.root_key = root_key;
    }
    cache.loaded_parents.insert(stripped_parent);
    cache.entries_by_parent[stripped_parent] = entries;
  }

  if ((VfsTracingEnabled() || LooseAssetOverrideTracingEnabled()) &&
      ShouldLogRepeatedVfsTraceOperation("FindFirstFileADatabaseParent", 32)) {
    REXLOG_INFO("Refii VFS FindFirstFileA database parent loaded parent={} "
                "entries={} root={}",
                stripped_parent,
                entries.size(),
                loose_root.string());
  }

  return entries;
}

void AddRefiiIndexedDirectoryEntries(
    std::vector<RefiiPrivateFindEntry>* entries,
    std::unordered_set<std::string>* seen,
    const std::filesystem::path& loose_root,
    std::string_view parent,
    std::string_view pattern) {
  if (entries == nullptr || seen == nullptr || loose_root.empty()) {
    return;
  }

  const std::string stripped_parent = StripRefiiDataPrefix(parent);
  if (RefiiVfsDatabaseExists(loose_root)) {
    const std::vector<RefiiPrivateFindEntry> parent_entries =
        GetRefiiDirectoryEntriesForParentFromDatabase(
            loose_root, stripped_parent);
    for (const RefiiPrivateFindEntry& entry : parent_entries) {
      AddRefiiFindEntry(
          entries, seen, entry.name, entry.directory, entry.size, pattern);
    }
    return;
  }

  const auto index = GetRefiiFindDirectoryIndex(loose_root);
  if (index != nullptr) {
    const auto match = index->entries_by_parent.find(stripped_parent);
    if (match != index->entries_by_parent.end()) {
      for (const RefiiPrivateFindEntry& entry : match->second) {
        AddRefiiFindEntry(
            entries, seen, entry.name, entry.directory, entry.size, pattern);
      }
      return;
    }
  }
}

std::vector<std::string> BuildRefiiDirectoryAliasParents(std::string_view parent) {
  const std::string stripped = StripRefiiDataPrefix(parent);
  std::vector<std::string> aliases;
  const auto add_alias = [&](std::string_view alias) {
    const std::string normalized = NormalizeLooseAssetKey(alias);
    if (normalized.empty() ||
        std::find(aliases.begin(), aliases.end(), normalized) != aliases.end()) {
      return;
    }
    aliases.push_back(normalized);
  };

  constexpr std::string_view kLanguageSpeech = "language/speech/";
  constexpr std::string_view kLanguageLipsync = "language/lipsync/";
  if (stripped == "language/speech") {
    add_alias("language/en-uk/speech/speech/language/speech");
    add_alias("language/en-uk/lipsync/lipsync/language/speech");
  } else if (StartsWith(stripped, kLanguageSpeech)) {
    const std::string_view tail = std::string_view(stripped).substr(
        kLanguageSpeech.size());
    add_alias("language/en-uk/speech/speech/language/speech/" +
              std::string(tail));
    add_alias("language/en-uk/lipsync/lipsync/language/speech/" +
              std::string(tail));
  } else if (stripped == "language/lipsync" ||
             stripped == "language/lipsync/lipsync") {
    add_alias("language/en-uk/lipsync/lipsync");
  } else if (StartsWith(stripped, "language/lipsync/lipsync/")) {
    const std::string_view tail = std::string_view(stripped).substr(
        std::string_view("language/lipsync/lipsync/").size());
    add_alias("language/en-uk/lipsync/lipsync/" + std::string(tail));
  } else if (StartsWith(stripped, kLanguageLipsync)) {
    const std::string_view tail = std::string_view(stripped).substr(
        kLanguageLipsync.size());
    add_alias("language/en-uk/lipsync/lipsync/" + std::string(tail));
  }

  constexpr std::string_view kScriptsGameface = "scripts/gameface";
  constexpr std::string_view kArtGameface = "art/gui/gameface";
  if (stripped == kScriptsGameface) {
    add_alias("guiscripts/scripts/gameface");
  } else if (StartsWith(stripped, "scripts/gameface/")) {
    const std::string_view tail = std::string_view(stripped).substr(
        std::string_view("scripts/gameface/").size());
    add_alias("guiscripts/scripts/gameface/" + std::string(tail));
  } else if (stripped == kArtGameface) {
    add_alias("guiscripts/art/gui/gameface");
  } else if (StartsWith(stripped, "art/gui/gameface/")) {
    const std::string_view tail = std::string_view(stripped).substr(
        std::string_view("art/gui/gameface/").size());
    add_alias("guiscripts/art/gui/gameface/" + std::string(tail));
  }

  return aliases;
}

void AddRefiiIndexedDirectoryEntriesWithAliases(
    std::vector<RefiiPrivateFindEntry>* entries,
    std::unordered_set<std::string>* seen,
    const std::filesystem::path& loose_root,
    std::string_view parent,
    std::string_view pattern) {
  AddRefiiIndexedDirectoryEntries(entries, seen, loose_root, parent, pattern);
  for (const std::string& alias_parent : BuildRefiiDirectoryAliasParents(parent)) {
    AddRefiiIndexedDirectoryEntries(
        entries, seen, loose_root, alias_parent, pattern);
  }
}

bool IsRefiiLanguageCultureDirectoryName(std::string_view name) {
  if (name.size() < 5 || name.size() > 6 || name[2] != '-') {
    return false;
  }
  for (size_t index = 0; index < name.size(); ++index) {
    if (index == 2) {
      continue;
    }
    if (!std::isalpha(static_cast<unsigned char>(name[index]))) {
      return false;
    }
  }
  return true;
}

bool IsRefiiLanguageCulturePath(std::string_view stripped_path) {
  constexpr std::string_view kLanguagePrefix = "language/";
  if (!StartsWith(stripped_path, kLanguagePrefix)) {
    return false;
  }

  std::string_view tail = stripped_path.substr(kLanguagePrefix.size());
  const size_t separator = tail.find('/');
  const std::string_view culture =
      separator == std::string_view::npos ? tail : tail.substr(0, separator);
  return IsRefiiLanguageCultureDirectoryName(culture);
}

bool IsRefiiFindAllowedFileParent(std::string_view parent) {
  const std::string stripped = StripRefiiDataPrefix(parent);
  return stripped == "language" ||
         stripped == "language/fonts" ||
         IsRefiiLanguageCulturePath(stripped);
}

bool IsGameFaceVirtualDirectory(std::string_view path) {
  const std::filesystem::path loose_root = GetLooseAssetRoot();
  if (loose_root.empty()) {
    return false;
  }

  for (const std::string& mapped_parent :
       BuildGameFaceRedirectCandidates(StripRefiiDataPrefix(path))) {
    const std::string prefix = StripTrailingSlashes(mapped_parent) + "/";
    for (const RefiiVfsRuntimeLuaRecord& record :
         GetRefiiVfsRuntimeLuaRecordsCached(loose_root)) {
      if (StartsWith(record.alias, prefix)) {
        return true;
      }
    }
  }
  return false;
}

bool IsRefiiFindClaimedPrefix(std::string_view path) {
  const std::string stripped = StripRefiiDataPrefix(path);
  constexpr std::string_view kClaimedPrefixes[] = {
      "language/",
      "art/",
      "audio/",
      "globals/",
      "levels/",
      "streaming/",
      "skeletalmorphs/",
  };
  if (stripped == "language" || stripped == "art" || stripped == "audio" ||
      stripped == "globals" || stripped == "levels" ||
      stripped == "streaming" || stripped == "skeletalmorphs") {
    return true;
  }
  if (StartsWith(stripped, "scripts/gameface") ||
      StartsWith(stripped, "guiscripts/scripts/gameface") ||
      StartsWith(stripped, "guiscripts/art/gui/gameface") ||
      StartsWith(stripped, "art/gui/gameface")) {
    return true;
  }
  for (const std::string_view prefix : kClaimedPrefixes) {
    if (StartsWith(stripped, prefix)) {
      return true;
    }
  }
  return false;
}

void AddRefiiFindEntry(std::vector<RefiiPrivateFindEntry>* entries,
                       std::unordered_set<std::string>* seen,
                       std::string_view name,
                       bool directory,
                       uint64_t size,
                       std::string_view pattern) {
  if (entries == nullptr || seen == nullptr || name.empty() ||
      !MatchesFindPattern(name, pattern)) {
    return;
  }
  std::string normalized_name(name);
  std::ranges::transform(normalized_name, normalized_name.begin(),
                         [](unsigned char ch) {
                           return static_cast<char>(std::tolower(ch));
                         });
  if (!seen->insert(normalized_name).second) {
    return;
  }
  entries->push_back({std::string(name), directory, size});
}

bool TryAddRefiiManifestBankDirectoryAsContainer(
    std::vector<RefiiPrivateFindEntry>* entries,
    std::unordered_set<std::string>* seen,
    const std::filesystem::directory_entry& directory_entry,
    std::string_view pattern) {
  if (entries == nullptr || seen == nullptr) {
    return false;
  }

  const std::filesystem::path manifest_path =
      directory_entry.path() / "_manifest.tsv";
  std::error_code error;
  if (!std::filesystem::is_regular_file(manifest_path, error)) {
    return false;
  }

  const std::string bank_name =
      directory_entry.path().filename().generic_string() + ".bnk";
  const uint64_t size = std::filesystem::file_size(manifest_path, error);
  AddRefiiFindEntry(entries,
                    seen,
                    bank_name,
                    false,
                    error ? 0 : size,
                    pattern);
  if (VfsTracingEnabled() &&
      ShouldLogRepeatedVfsTraceOperation("FindFirstFileAManifestBankContainer",
                                         64)) {
    REXLOG_INFO("Refii VFS FindFirstFileA manifest bank container "
                "directory={} bank={} manifest={}",
                directory_entry.path().generic_string(),
                bank_name,
                manifest_path.generic_string());
  }
  return true;
}

void AddRefiiGameFaceLuaEntries(std::vector<RefiiPrivateFindEntry>* entries,
                                std::unordered_set<std::string>* seen,
                                const std::filesystem::path& loose_root,
                                std::string_view parent,
                                std::string_view pattern) {
  if (entries == nullptr || seen == nullptr || loose_root.empty()) {
    return;
  }

  for (const std::string& mapped_parent :
       BuildGameFaceRedirectCandidates(StripRefiiDataPrefix(parent))) {
    const std::string prefix = StripTrailingSlashes(mapped_parent) + "/";
    if (!IsGameFaceBoundaryPath(prefix)) {
      continue;
    }

    for (const RefiiVfsRuntimeLuaRecord& record :
         GetRefiiVfsRuntimeLuaRecordsCached(loose_root)) {
      if (!StartsWith(record.alias, prefix) ||
          (record.kind != "carrier" && record.kind != "override")) {
        continue;
      }

      std::string_view tail(record.alias);
      tail.remove_prefix(prefix.size());
      if (tail.empty()) {
        continue;
      }

      const size_t separator = tail.find('/');
      const bool directory = separator != std::string_view::npos;
      const std::string name =
          directory ? std::string(tail.substr(0, separator)) : std::string(tail);
      uint64_t size = 0;
      if (!directory) {
        std::error_code error;
        const std::filesystem::path host_path = loose_root / record.candidate;
        if (std::filesystem::is_regular_file(host_path, error)) {
          size = std::filesystem::file_size(host_path, error);
          if (error) {
            size = 0;
          }
        }
      }
      AddRefiiFindEntry(entries, seen, name, directory, size, pattern);
    }
  }
}

void AddRefiiFindHostFiles(std::vector<RefiiPrivateFindEntry>* entries,
                           std::unordered_set<std::string>* seen,
                           const std::filesystem::path& loose_root,
                           std::string_view parent,
                           std::string_view pattern) {
  if (entries == nullptr || seen == nullptr ||
      !IsRefiiFindAllowedFileParent(parent)) {
    return;
  }
  const std::string stripped_parent = StripRefiiDataPrefix(parent);
  const std::filesystem::path directory =
      loose_root / std::filesystem::path(stripped_parent);
  std::error_code error;
  if (!std::filesystem::is_directory(directory, error)) {
    return;
  }

  for (std::filesystem::directory_iterator it(directory, error), end;
       it != end; it.increment(error)) {
    if (error) {
      error.clear();
      continue;
    }
    const std::string name = it->path().filename().generic_string();
    if (it->is_directory(error)) {
      if (TryAddRefiiManifestBankDirectoryAsContainer(
              entries, seen, *it, pattern)) {
        error.clear();
        continue;
      }
      if (stripped_parent != "language" ||
          IsRefiiLanguageCultureDirectoryName(name)) {
        AddRefiiFindEntry(entries, seen, name, true, 0, pattern);
      }
      error.clear();
      continue;
    }
    error.clear();
    if (!it->is_regular_file(error)) {
      error.clear();
      continue;
    }
    const uint64_t size = it->file_size(error);
    AddRefiiFindEntry(entries, seen, name, false, error ? 0 : size, pattern);
    error.clear();
  }
}

void AddRefiiFindHostFilesWithAliases(
    std::vector<RefiiPrivateFindEntry>* entries,
    std::unordered_set<std::string>* seen,
    const std::filesystem::path& loose_root,
    std::string_view parent,
    std::string_view pattern) {
  AddRefiiFindHostFiles(entries, seen, loose_root, parent, pattern);
  for (const std::string& alias_parent : BuildRefiiDirectoryAliasParents(parent)) {
    AddRefiiFindHostFiles(entries, seen, loose_root, alias_parent, pattern);
  }
}

void AddRefiiLocaleRootEntries(std::vector<RefiiPrivateFindEntry>* entries,
                               std::unordered_set<std::string>* seen,
                               std::string_view stripped_parent,
                               std::string_view pattern) {
  if (entries == nullptr || seen == nullptr) {
    return;
  }

  if (stripped_parent == "language/en-uk") {
    AddRefiiFindEntry(entries, seen, "fonts", true, 0, pattern);
    AddRefiiFindEntry(entries, seen, "speechdoc", true, 0, pattern);
    AddRefiiFindEntry(entries, seen, "text", true, 0, pattern);
    AddRefiiFindEntry(entries, seen, "videos", true, 0, pattern);
  }
}

bool RefiiStartupLanguageEnumerationDeferralActive() {
  if (!RefiiLooseAssetBackendEnabled() || SkipStartupVideosEnabled() ||
      RefiiStartupVideosSettledForMenuHandoff()) {
    return false;
  }
  if (RefiiStartupVideosPlaybackCriticalActive()) {
    return true;
  }

  // The first language validation pass can happen before the BIK objects have
  // reached the trace state, so protect that tiny pre-video boot window too.
  constexpr int64_t kPreTraceStartupLanguageGuardMs = 15000;
  const int64_t boot_age_ms = RefiiBootTraceAgeMs();
  return boot_age_ms >= 0 && boot_age_ms < kPreTraceStartupLanguageGuardMs;
}

bool IsRefiiStartupDeferredLanguageParent(std::string_view stripped_parent) {
  if (stripped_parent == "language" || stripped_parent == "language/en-uk" ||
      stripped_parent == "language/en-uk/fonts" ||
      stripped_parent == "language/en-uk/text") {
    return true;
  }
  return StartsWith(stripped_parent, "language/en-uk/fonts/") ||
         StartsWith(stripped_parent, "language/en-uk/text/");
}

void AddRefiiStartupDeferredLocaleRootEntries(
    std::vector<RefiiPrivateFindEntry>* entries,
    std::unordered_set<std::string>* seen,
    std::string_view pattern) {
  AddRefiiFindEntry(entries, seen, "fonts", true, 0, pattern);
  AddRefiiFindEntry(entries, seen, "text", true, 0, pattern);
}

std::vector<RefiiPrivateFindEntry> BuildRefiiFindEntries(std::string_view path) {
  std::vector<RefiiPrivateFindEntry> entries;
  const std::filesystem::path loose_root = GetLooseAssetRoot();
  const std::string normalized = NormalizeLooseAssetKey(path);
  if (loose_root.empty() || normalized.empty()) {
    return entries;
  }

  std::string pattern;
  const std::string parent = RefiiFindParent(normalized, &pattern);
  const std::string stripped_parent = StripRefiiDataPrefix(parent);
  std::unordered_set<std::string> seen;
  const bool playback_critical = RefiiStartupVideosPlaybackCriticalActive();
  const bool defer_language_boot_scan =
      RefiiStartupLanguageEnumerationDeferralActive();
  const bool directory_index_authoritative =
      RefiiVfsDatabaseExists(loose_root);
  const bool host_directory_first =
      IsRefiiFindAllowedFileParent(stripped_parent);
  const auto add_host_then_indexed = [&](std::string_view entry_parent,
                                         bool include_aliases) {
    if (host_directory_first) {
      if (include_aliases) {
        AddRefiiFindHostFilesWithAliases(
            &entries, &seen, loose_root, entry_parent, pattern);
      } else {
        AddRefiiFindHostFiles(&entries, &seen, loose_root, entry_parent, pattern);
      }
      if (!entries.empty()) {
        return;
      }
    }
    if (include_aliases) {
      AddRefiiIndexedDirectoryEntriesWithAliases(
          &entries, &seen, loose_root, entry_parent, pattern);
    } else {
      AddRefiiIndexedDirectoryEntries(
          &entries, &seen, loose_root, entry_parent, pattern);
    }
    if (!playback_critical && !directory_index_authoritative &&
        entries.empty()) {
      if (include_aliases) {
        AddRefiiFindHostFilesWithAliases(
            &entries, &seen, loose_root, entry_parent, pattern);
      } else {
        AddRefiiFindHostFiles(&entries, &seen, loose_root, entry_parent, pattern);
      }
    }
  };
  const auto add_indexed_then_host = [&](std::string_view entry_parent) {
    add_host_then_indexed(entry_parent, true);
  };

  const bool is_language_parent =
      stripped_parent == "language" || StartsWith(stripped_parent, "language/");

  if (defer_language_boot_scan && is_language_parent &&
      !IsRefiiStartupDeferredLanguageParent(stripped_parent)) {
    if (VfsTracingEnabled() &&
        ShouldLogRepeatedVfsTraceOperation(
            "FindFirstFileAStartupLanguageDeferred", 16)) {
      REXLOG_INFO("Refii VFS FindFirstFileA startup language subtree "
                  "deferred parent={} pattern={}",
                  stripped_parent,
                  pattern);
    }
  } else if (stripped_parent == "language") {
    if (defer_language_boot_scan) {
      AddRefiiFindEntry(&entries, &seen, "en-uk", true, 0, pattern);
    } else {
      add_indexed_then_host(stripped_parent);
      if (entries.empty()) {
        AddRefiiFindEntry(&entries, &seen, "en-uk", true, 0, pattern);
      }
    }
  } else if (defer_language_boot_scan && stripped_parent == "language/en-uk") {
    AddRefiiStartupDeferredLocaleRootEntries(&entries, &seen, pattern);
  } else if (stripped_parent == "language/en-uk") {
    AddRefiiLocaleRootEntries(&entries, &seen, stripped_parent, pattern);
    add_host_then_indexed(stripped_parent, true);
  } else if (IsRefiiLanguageCulturePath(stripped_parent)) {
    add_indexed_then_host(stripped_parent);
  } else if (IsGameFaceBoundaryPath(stripped_parent)) {
    AddRefiiGameFaceLuaEntries(
        &entries, &seen, loose_root, stripped_parent, pattern);
    AddRefiiIndexedDirectoryEntriesWithAliases(
        &entries, &seen, loose_root, stripped_parent, pattern);
  } else {
    AddRefiiIndexedDirectoryEntriesWithAliases(
        &entries, &seen, loose_root, stripped_parent, pattern);
    if (!directory_index_authoritative && entries.empty()) {
      AddRefiiFindHostFiles(&entries, &seen, loose_root, stripped_parent, pattern);
    }
  }

  std::sort(entries.begin(), entries.end(), [](const auto& left, const auto& right) {
    if (left.directory != right.directory) {
      return left.directory && !right.directory;
    }
    return left.name < right.name;
  });
  return entries;
}

bool TryFindFirstRefiiPrivate(PPCContext& ctx,
                              uint8_t* base,
                              std::string_view path,
                              uint32_t find_data_guest) {
  if (!RefiiLooseAssetBackendEnabled()) {
    return false;
  }
  LogRefiiVfsRuntimePolicyOnce();
  const std::string normalized = NormalizeLooseAssetKey(path);
  if (normalized.find(':') != std::string::npos ||
      (normalized.find('*') == std::string::npos &&
       normalized.find('?') == std::string::npos)) {
    return false;
  }

  std::vector<RefiiPrivateFindEntry> entries = BuildRefiiFindEntries(path);
  if (entries.empty()) {
    if (IsRefiiFindClaimedPrefix(normalized)) {
      ClearRefiiFindData(base, find_data_guest);
      ctx.r3.u32 = kRefiiPrivateHandleInvalid;
      if (VfsTracingEnabled() &&
          ShouldLogRepeatedVfsTraceOperation("FindFirstFileARefiiRejected", 64)) {
        REXLOG_INFO("Refii VFS FindFirstFileA rejected broad probe path={}",
                    normalized);
      }
      return true;
    }
    return false;
  }

  WriteRefiiFindData(base, find_data_guest, entries.front());
  auto record = std::make_shared<RefiiPrivateFindHandleRecord>();
  record->pattern = normalized;
  record->entries = std::move(entries);
  record->next_index = 1;
  ctx.r3.u32 = RegisterRefiiPrivateFindHandle(record);
  return true;
}

bool TryFindNextRefiiPrivate(PPCContext& ctx,
                             uint8_t* base,
                             uint32_t handle,
                             uint32_t find_data_guest) {
  const auto record = FindRefiiPrivateFindHandle(handle);
  if (record == nullptr) {
    return false;
  }
  std::lock_guard<std::mutex> lock(record->mutex);
  if (record->next_index >= record->entries.size()) {
    record->exhausted = true;
    ctx.r3.u32 = 0;
    if (VfsTracingEnabled() &&
        ShouldLogRepeatedVfsTraceOperation("LastVfsOperationBeforeTrap", 16)) {
      REXLOG_INFO("Refii VFS last operation context before possible native "
                  "directory trap api=FindNextFileA pattern={} handle=0x{:08X} "
                  "entries={} next_index={} result=miss shape=false_preserved",
                  record->pattern,
                  handle,
                  record->entries.size(),
                  record->next_index);
    }
    return true;
  }
  WriteRefiiFindData(base, find_data_guest, record->entries[record->next_index++]);
  ctx.r3.u32 = 1;
  return true;
}

uint64_t HashFingerprintBytes(uint64_t value, const void* data, size_t size) {
  const auto* bytes = static_cast<const uint8_t*>(data);
  for (size_t index = 0; index < size; ++index) {
    value ^= bytes[index];
    value *= 1099511628211ull;
  }
  return value;
}

uint64_t HashFingerprintString(uint64_t value, std::string_view text) {
  return HashFingerprintBytes(value, text.data(), text.size());
}

uint64_t FingerprintRequestManagerEntries(
    const std::vector<RequestManagerHashEntry>& entries) {
  uint64_t fingerprint = 1469598103934665603ull;
  for (const RequestManagerHashEntry& entry : entries) {
    fingerprint = HashFingerprintBytes(fingerprint, &entry.hash, sizeof(entry.hash));
    fingerprint = HashFingerprintBytes(
        fingerprint, &entry.provider_index, sizeof(entry.provider_index));
    fingerprint = HashFingerprintBytes(
        fingerprint, &entry.provider_entry_key, sizeof(entry.provider_entry_key));
  }
  return fingerprint;
}

uint64_t FingerprintRuntimeResourceHashNames(
    const std::unordered_map<uint32_t, std::vector<std::string>>& runtime_names) {
  uint64_t fingerprint = 1469598103934665603ull;
  for (const auto& [resource_hash, names] : runtime_names) {
    uint64_t entry = 1469598103934665603ull;
    entry = HashFingerprintBytes(entry, &resource_hash, sizeof(resource_hash));
    for (const std::string& name : names) {
      const std::string normalized = NormalizeLooseAssetKey(name);
      entry = HashFingerprintString(entry, normalized);
      entry ^= 0xFFu;
      entry *= 1099511628211ull;
    }
    fingerprint ^= entry;
    fingerprint *= 1099511628211ull;
  }
  return fingerprint;
}

std::mutex& RequestManagerHashAugmentNoopCacheMutex() {
  static std::mutex mutex;
  return mutex;
}

std::unordered_set<std::string>& RequestManagerHashAugmentNoopCache() {
  static std::unordered_set<std::string> cached_noops;
  return cached_noops;
}

bool IsCachedRequestManagerHashAugmentNoop(std::string_view cache_key) {
  std::lock_guard<std::mutex> lock(RequestManagerHashAugmentNoopCacheMutex());
  const auto& cached_noops = RequestManagerHashAugmentNoopCache();
  return cached_noops.find(std::string(cache_key)) != cached_noops.end();
}

void CacheRequestManagerHashAugmentNoop(std::string cache_key) {
  std::lock_guard<std::mutex> lock(RequestManagerHashAugmentNoopCacheMutex());
  auto& cached_noops = RequestManagerHashAugmentNoopCache();
  constexpr size_t kMaxNoopAugmentCacheEntries = 4096;
  if (cached_noops.size() > kMaxNoopAugmentCacheEntries) {
    cached_noops.clear();
  }
  cached_noops.insert(std::move(cache_key));
}

void ClearRequestManagerHashAugmentNoopCache() {
  std::lock_guard<std::mutex> lock(RequestManagerHashAugmentNoopCacheMutex());
  RequestManagerHashAugmentNoopCache().clear();
}

std::mutex& KnownRequestManagerGuestMutex() {
  static std::mutex mutex;
  return mutex;
}

std::unordered_set<uint32_t>& KnownRequestManagerGuests() {
  static std::unordered_set<uint32_t> managers;
  return managers;
}

void RecordKnownRequestManagerGuest(uint32_t manager_guest,
                                    std::string_view source) {
  if (manager_guest == 0) {
    return;
  }

  bool inserted = false;
  {
    std::lock_guard<std::mutex> lock(KnownRequestManagerGuestMutex());
    inserted = KnownRequestManagerGuests().insert(manager_guest).second;
  }

  if (inserted && VfsTracingEnabled() &&
      ShouldLogRepeatedVfsTraceOperation("KnownRequestManagerGuest", 32)) {
    REXLOG_INFO("Refii VFS recorded request-manager guest manager=0x{:08X} "
                "source={}",
                manager_guest,
                source);
  }
}

std::vector<uint32_t> SnapshotKnownRequestManagerGuests() {
  std::lock_guard<std::mutex> lock(KnownRequestManagerGuestMutex());
  return {KnownRequestManagerGuests().begin(), KnownRequestManagerGuests().end()};
}

std::string FormatKnownRequestManagerGuestsForLog(size_t max_count) {
  const std::vector<uint32_t> managers = SnapshotKnownRequestManagerGuests();
  if (managers.empty()) {
    return "none";
  }

  std::string result;
  const size_t count = std::min(max_count, managers.size());
  for (size_t index = 0; index < count; ++index) {
    if (!result.empty()) {
      result += ",";
    }
    result += fmt::format("0x{:08X}", managers[index]);
  }
  if (managers.size() > count) {
    result += fmt::format(",+{}", managers.size() - count);
  }
  return result;
}

void AddRefiiRequestManagerHashEntry(
    std::vector<RequestManagerHashEntry>& entries,
    std::unordered_set<uint32_t>& added_hashes,
    uint32_t resource_hash,
    size_t* contributed) {
  if (resource_hash == 0 ||
      added_hashes.find(resource_hash) != added_hashes.end()) {
    return;
  }

  entries.push_back({resource_hash,
                     kRefiiRequestManagerProviderIndex,
                     kRefiiRequestManagerEntryKey});
  added_hashes.insert(resource_hash);
  if (contributed != nullptr) {
    ++*contributed;
  }
}

bool RequestManagerHashTableContainsHash(uint8_t* base,
                                         uint32_t begin_guest,
                                         uint32_t count,
                                         uint32_t resource_hash) {
  if (base == nullptr || begin_guest == 0 || count == 0 ||
      resource_hash == 0) {
    return false;
  }

  uint32_t lower = 0;
  uint32_t upper = count;
  while (lower < upper) {
    const uint32_t middle = lower + ((upper - lower) / 2);
    const uint32_t middle_hash =
        LoadGuestU32(base, begin_guest + middle * 12);
    if (middle_hash < resource_hash) {
      lower = middle + 1;
    } else {
      upper = middle;
    }
  }

  return lower < count &&
         LoadGuestU32(base, begin_guest + lower * 12) == resource_hash;
}

bool MakeVfsDatabaseCandidateResolution(const std::filesystem::path& loose_root,
                                        std::string_view candidate,
                                        LooseAssetResolution* out_resolution,
                                        std::string* out_candidate);

bool IsRefiiHashCandidateOwnedByDatabase(
    const std::filesystem::path& loose_root,
    std::string_view candidate) {
  if (candidate.empty()) {
    return false;
  }

  LooseAssetResolution resolution;
  std::string resolved_candidate;
  return MakeVfsDatabaseCandidateResolution(loose_root,
                                            candidate,
                                            &resolution,
                                            &resolved_candidate) ||
         ResolveSyntheticBankManifestLooseAsset(
             candidate, &resolution, &resolved_candidate);
}

bool RefiiDatabaseHashHasExactOwnedCandidate(uint32_t resource_hash,
                                             std::string_view candidate) {
  if (resource_hash == 0 || candidate.empty()) {
    return false;
  }

  const std::filesystem::path loose_root = GetLooseAssetRoot();
  const std::string normalized_candidate = NormalizeLooseAssetKey(candidate);
  if (normalized_candidate.empty()) {
    return false;
  }

  if (ResolveExactTypedBankManifestLooseAsset(
          normalized_candidate, nullptr, nullptr)) {
    return true;
  }

  const std::vector<std::string> hash_candidates =
      LookupRefiiVfsDatabaseHashCandidates(resource_hash, loose_root);
  for (const std::string& hash_candidate : hash_candidates) {
    const std::string normalized_hash_candidate =
        NormalizeLooseAssetKey(hash_candidate);
    if (normalized_hash_candidate == normalized_candidate &&
        IsRefiiHashCandidateOwnedByDatabase(loose_root, normalized_hash_candidate)) {
      return true;
    }
  }
  return false;
}

bool RefiiDatabaseHashHasOwnedResolution(uint32_t resource_hash,
                                         std::string_view candidate,
                                         std::string* out_resolved_candidate) {
  if (resource_hash == 0 || candidate.empty()) {
    return false;
  }

  const std::string normalized_candidate = NormalizeLooseAssetKey(candidate);
  if (normalized_candidate.empty()) {
    return false;
  }

  if (RefiiDatabaseHashHasExactOwnedCandidate(resource_hash,
                                              normalized_candidate)) {
    if (out_resolved_candidate != nullptr) {
      *out_resolved_candidate = normalized_candidate;
    }
    return true;
  }

  if (EndsWith(normalized_candidate, ".bnk")) {
    return false;
  }

  LooseAssetResolution alias_resolution;
  std::string alias_candidate;
  if (!ResolveVfsDatabaseExactAliasLooseAsset(
          normalized_candidate, &alias_resolution, &alias_candidate)) {
    return false;
  }

  const std::string resolved_candidate =
      NormalizeLooseAssetKey(alias_candidate.empty()
                                 ? alias_resolution.provider_relative_path
                                 : alias_candidate);
  if (resolved_candidate.empty() ||
      ShouldSkipLooseNativeAsset(resolved_candidate,
                                 "request_manager_alias_resolution")) {
    return false;
  }

  if (out_resolved_candidate != nullptr) {
    *out_resolved_candidate = resolved_candidate;
  }
  return true;
}

bool RegisterTargetedRequestManagerHashContribution(uint32_t resource_hash,
                                                    std::string_view name,
                                                    std::string_view reason,
                                                    std::string_view resolved_candidate = {},
                                                    std::string_view provider_relative = {},
                                                    std::string_view source = {}) {
  if (resource_hash == 0 || name.empty()) {
    return false;
  }

  const std::string root_key =
      GetLooseAssetRoot().lexically_normal().string();
  const std::string normalized_name = NormalizeLooseAssetKey(name);
  const std::string normalized_candidate =
      NormalizeLooseAssetKey(resolved_candidate);
  const std::string normalized_provider =
      NormalizeLooseAssetKey(provider_relative);
  const std::string source_text(source);
  if (root_key.empty() || normalized_name.empty()) {
    return false;
  }

  std::lock_guard<std::mutex> lock(
      TargetedRequestManagerHashContributionMutex());
  std::string& stored_root = TargetedRequestManagerHashContributionRootKey();
  if (stored_root != root_key) {
    stored_root = root_key;
    TargetedRequestManagerHashContributions().clear();
    ClearRequestManagerHashAugmentNoopCache();
  }

  auto& contribution = TargetedRequestManagerHashContributions()[resource_hash];
  const std::string reason_text(reason);
  const bool changed = contribution.name != normalized_name ||
                       contribution.resolved_candidate != normalized_candidate ||
                       contribution.provider_relative != normalized_provider ||
                       contribution.source != source_text ||
                       contribution.reason != reason_text ||
                       contribution.texture !=
                           (IsTextureAssetCandidate(normalized_name) ||
                            IsTextureAssetCandidate(normalized_candidate) ||
                            IsTextureAssetCandidate(normalized_provider));
  contribution.name = normalized_name;
  contribution.resolved_candidate = normalized_candidate;
  contribution.provider_relative = normalized_provider;
  contribution.source = source_text;
  contribution.reason = std::move(reason_text);
  contribution.texture = IsTextureAssetCandidate(normalized_name) ||
                         IsTextureAssetCandidate(normalized_candidate) ||
                         IsTextureAssetCandidate(normalized_provider);
  if (changed) {
    ClearRequestManagerHashAugmentNoopCache();
  }
  return changed;
}

std::optional<TargetedRequestManagerHashContribution>
GetTargetedRequestManagerHashContribution(uint32_t resource_hash) {
  if (resource_hash == 0) {
    return std::nullopt;
  }

  const std::string root_key =
      GetLooseAssetRoot().lexically_normal().string();
  std::lock_guard<std::mutex> lock(
      TargetedRequestManagerHashContributionMutex());
  if (TargetedRequestManagerHashContributionRootKey() != root_key) {
    return std::nullopt;
  }
  const auto match =
      TargetedRequestManagerHashContributions().find(resource_hash);
  if (match == TargetedRequestManagerHashContributions().end()) {
    return std::nullopt;
  }
  return match->second;
}

std::vector<std::pair<uint32_t, TargetedRequestManagerHashContribution>>
SnapshotTargetedRequestManagerHashContributions() {
  const std::string root_key =
      GetLooseAssetRoot().lexically_normal().string();
  std::vector<std::pair<uint32_t, TargetedRequestManagerHashContribution>>
      snapshot;
  std::lock_guard<std::mutex> lock(
      TargetedRequestManagerHashContributionMutex());
  if (TargetedRequestManagerHashContributionRootKey() != root_key) {
    return snapshot;
  }
  snapshot.reserve(TargetedRequestManagerHashContributions().size());
  for (const auto& [resource_hash, contribution] :
       TargetedRequestManagerHashContributions()) {
    snapshot.emplace_back(resource_hash, contribution);
  }
  return snapshot;
}

size_t TargetedRequestManagerHashContributionCount() {
  const std::string root_key =
      GetLooseAssetRoot().lexically_normal().string();
  std::lock_guard<std::mutex> lock(
      TargetedRequestManagerHashContributionMutex());
  if (TargetedRequestManagerHashContributionRootKey() != root_key) {
    return 0;
  }
  return TargetedRequestManagerHashContributions().size();
}

std::atomic<bool>& DeferredFrontendTextureHashWakeFlag() {
  static std::atomic<bool> pending{false};
  return pending;
}

void DeferFrontendTextureHashWake() {
  DeferredFrontendTextureHashWakeFlag().store(true, std::memory_order_release);
}

bool ConsumeDeferredFrontendTextureHashWake() {
  return DeferredFrontendTextureHashWakeFlag().exchange(
      false, std::memory_order_acq_rel);
}

std::string FormatTargetedTextureHashTableAudit(uint8_t* base) {
  if (base == nullptr) {
    return "base=null";
  }

  std::vector<std::pair<uint32_t, TargetedRequestManagerHashContribution>>
      targets;
  {
    const std::string root_key =
        GetLooseAssetRoot().lexically_normal().string();
    std::lock_guard<std::mutex> lock(
        TargetedRequestManagerHashContributionMutex());
    if (TargetedRequestManagerHashContributionRootKey() == root_key) {
      for (const auto& [hash, contribution] :
           TargetedRequestManagerHashContributions()) {
        if (contribution.texture) {
          targets.emplace_back(hash, contribution);
        }
      }
    }
  }
  if (targets.empty()) {
    return "targets=0";
  }

  const std::vector<uint32_t> managers = SnapshotKnownRequestManagerGuests();
  if (managers.empty()) {
    return fmt::format("targets={} managers=0", targets.size());
  }

  std::vector<std::string> manager_summaries;
  manager_summaries.reserve(std::min<size_t>(managers.size(), 4));
  for (const uint32_t manager_guest : managers) {
    if (manager_guest == 0 || manager_summaries.size() >= 4) {
      continue;
    }

    const uint32_t container_guest = manager_guest + 52;
    const uint32_t begin = LoadGuestU32(base, container_guest + 4);
    const uint32_t end = LoadGuestU32(base, container_guest + 8);
    const uint32_t capacity_end = LoadGuestU32(base, container_guest + 12);
    if ((begin == 0 && end != 0) || end < begin || capacity_end < end ||
        ((end - begin) % 12) != 0) {
      manager_summaries.push_back(
          fmt::format("manager=0x{:08X} malformed begin=0x{:08X} "
                      "end=0x{:08X} capacity=0x{:08X}",
                      manager_guest,
                      begin,
                      end,
                      capacity_end));
      continue;
    }

    const uint32_t entry_count =
        begin == 0 ? 0 : static_cast<uint32_t>((end - begin) / 12);
    size_t found = 0;
    size_t refii = 0;
    size_t native = 0;
    std::vector<std::string> samples;
    for (const auto& [hash, contribution] : targets) {
      bool matched = false;
      bool matched_refii = false;
      uint32_t matched_provider_index = 0;
      uint32_t matched_entry_key = 0;
      for (uint32_t index = 0; index < entry_count; ++index) {
        const uint32_t entry_guest = begin + index * 12;
        if (LoadGuestU32(base, entry_guest + 0) != hash) {
          continue;
        }
        matched = true;
        matched_provider_index = LoadGuestU32(base, entry_guest + 4);
        matched_entry_key = LoadGuestU32(base, entry_guest + 8);
        matched_refii =
            matched_provider_index == kRefiiRequestManagerProviderIndex &&
            matched_entry_key == kRefiiRequestManagerEntryKey;
        break;
      }

      if (matched) {
        ++found;
        if (matched_refii) {
          ++refii;
        } else {
          ++native;
          if (samples.size() < 4) {
            samples.push_back(fmt::format(
                "native:0x{:08X}:{}->{}:{}/0x{:08X}",
                hash,
                contribution.name,
                contribution.resolved_candidate.empty()
                    ? contribution.name
                    : contribution.resolved_candidate,
                matched_provider_index,
                matched_entry_key));
          }
        }
      } else if (samples.size() < 4) {
        samples.push_back(fmt::format(
            "missing:0x{:08X}:{}->{}",
            hash,
            contribution.name,
            contribution.resolved_candidate.empty() ? contribution.name
                                                    : contribution.resolved_candidate));
      }
    }

    manager_summaries.push_back(fmt::format(
        "manager=0x{:08X} entries={} targets={} found={} refii={} native={} "
        "missing={} samples=[{}]",
        manager_guest,
        entry_count,
        targets.size(),
        found,
        refii,
        native,
        targets.size() - found,
        samples.empty() ? std::string("none") : JoinStrings(samples, "; ")));
  }

  return fmt::format("targets={} managers={} [{}]",
                     targets.size(),
                     managers.size(),
                     JoinStrings(manager_summaries, " | "));
}

void RegisterResolvedSyntheticBankOpenAlias(std::string_view bank_alias,
                                            std::string_view resolved_bank,
                                            std::string_view reason) {
  const std::string root_key =
      GetLooseAssetRoot().lexically_normal().string();
  const std::string normalized_alias = NormalizeLooseAssetKey(bank_alias);
  const std::string normalized_resolved = NormalizeLooseAssetKey(resolved_bank);
  if (root_key.empty() || normalized_alias.empty() ||
      normalized_resolved.empty() || !EndsWith(normalized_alias, ".bnk") ||
      !EndsWith(normalized_resolved, ".bnk")) {
    return;
  }

  std::lock_guard<std::mutex> lock(ResolvedSyntheticBankOpenAliasMutex());
  std::string& stored_root = ResolvedSyntheticBankOpenAliasRootKey();
  if (stored_root != root_key) {
    stored_root = root_key;
    ResolvedSyntheticBankOpenAliases().clear();
  }
  ResolvedSyntheticBankOpenAliases()[normalized_alias] =
      ResolvedSyntheticBankOpenAlias{normalized_resolved, std::string(reason)};
}

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

std::mutex& RefiiLastVfsOperationMutex() {
  static std::mutex mutex;
  return mutex;
}

RefiiLastVfsOperation& RefiiLastVfsOperationState() {
  static RefiiLastVfsOperation operation;
  return operation;
}

void RecordRefiiLastVfsAliasOperation(std::string_view api,
                                      std::string_view requested,
                                      std::string_view resolved,
                                      uint32_t requested_hash,
                                      uint32_t resolved_hash,
                                      uint32_t manager_guest,
                                      std::string_view ownership) {
  std::lock_guard<std::mutex> lock(RefiiLastVfsOperationMutex());
  auto& operation = RefiiLastVfsOperationState();
  operation.api = std::string(api);
  operation.requested = std::string(requested);
  operation.resolved = std::string(resolved);
  operation.ownership = std::string(ownership);
  operation.requested_hash = requested_hash;
  operation.resolved_hash = resolved_hash;
  operation.manager_guest = manager_guest;
  operation.targeted_hashes = TargetedRequestManagerHashContributionCount();
  operation.timestamp = std::chrono::steady_clock::now();
}

std::string FormatRefiiLastVfsOperationForLog() {
  std::lock_guard<std::mutex> lock(RefiiLastVfsOperationMutex());
  const auto& operation = RefiiLastVfsOperationState();
  if (operation.api.empty()) {
    return "none";
  }
  const auto age_ms = std::chrono::duration_cast<std::chrono::milliseconds>(
                          std::chrono::steady_clock::now() - operation.timestamp)
                          .count();
  return fmt::format("{} requested={} resolved={} requested_hash=0x{:08X} "
                     "resolved_hash=0x{:08X} manager=0x{:08X} ownership={} "
                     "targeted_hashes={} age_ms={}",
                     operation.api,
                     operation.requested,
                     operation.resolved,
                     operation.requested_hash,
                     operation.resolved_hash,
                     operation.manager_guest,
                     operation.ownership,
                     operation.targeted_hashes,
                     age_ms);
}

}  // namespace detail
}  // namespace refii::vfs
