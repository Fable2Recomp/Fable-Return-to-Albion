#include "vfs_hooks_private.h"

namespace refii::vfs {
inline namespace detail {

const char* ToString(RefiiVfsOwnershipKind kind) {
  switch (kind) {
    case RefiiVfsOwnershipKind::RefiiOwned:
      return "RefiiOwned";
    case RefiiVfsOwnershipKind::NativeOnly:
      return "NativeOnly";
    case RefiiVfsOwnershipKind::OptionalMiss:
      return "OptionalMiss";
    case RefiiVfsOwnershipKind::Unresolved:
      return "Unresolved";
  }
  return "Unresolved";
}

bool IsRefiiNativeShapeSensitiveAsset(std::string_view asset_path) {
  return !GetResourceManagerCachedDirectBypassReason(asset_path).empty();
}

RefiiVfsOwnershipDecision DecideRefiiVfsOwnershipForName(
    std::string_view asset_path,
    uint32_t resource_hash,
    bool prefer_resource_manager_texture_shape,
    uint32_t caller_lr) {
  RefiiVfsOwnershipDecision decision;
  decision.normalized = NormalizeLooseAssetKey(asset_path);
  MaybeRecordFirstRefiiPostFrontendConsumerAssetRequest(
      "DecideRefiiVfsOwnershipForName",
      decision.normalized,
      resource_hash,
      caller_lr);
  decision.native_shaped = IsRefiiNativeShapeSensitiveAsset(decision.normalized);
  decision.reason =
      decision.native_shaped
          ? std::string(GetResourceManagerCachedDirectBypassReason(decision.normalized))
          : std::string{};

  if (!RefiiLooseAssetBackendEnabled() || decision.normalized.empty()) {
    decision.kind = RefiiVfsOwnershipKind::Unresolved;
    decision.reason = decision.reason.empty() ? "backend_disabled_or_empty"
                                             : decision.reason;
    return decision;
  }

  auto claim = [&](std::string source,
                   LooseAssetResolution resolution,
                   std::string candidate) {
    decision.kind = RefiiVfsOwnershipKind::RefiiOwned;
    decision.source = std::move(source);
    decision.resolution = std::move(resolution);
    decision.candidate =
        candidate.empty() ? decision.normalized : NormalizeLooseAssetKey(candidate);
    decision.has_resolution = true;
    LooseStats().refii_owned_decisions.fetch_add(
        1, std::memory_order_relaxed);
    if (decision.native_shaped) {
      LooseStats().refii_owned_native_shaped.fetch_add(
          1, std::memory_order_relaxed);
    }
  };

  LooseAssetResolution resolution;
  std::string candidate;
  std::string lua_source;
  if (IsLuaScriptAssetKey(decision.normalized) &&
      ResolveRefiiLuaScriptAsset(
          decision.normalized, &resolution, &candidate, &lua_source)) {
    claim(lua_source.empty() ? "lua_extracted_source" : std::move(lua_source),
          std::move(resolution),
          std::move(candidate));
    return decision;
  }

  if (IsGameFaceBinaryAssetKey(decision.normalized) &&
      ResolveExactGameFaceBinaryCarrierLooseAsset(
          decision.normalized, &resolution, &candidate)) {
    claim("gameface_carrier", std::move(resolution), std::move(candidate));
    return decision;
  }
  if (IsGameFaceBinaryAssetKey(decision.normalized)) {
    decision.kind = RefiiVfsOwnershipKind::NativeOnly;
    decision.reason = decision.reason.empty() ? "gameface_binary_native_cache"
                                             : decision.reason;
    LooseStats().refii_native_fallbacks.fetch_add(
        1, std::memory_order_relaxed);
    return decision;
  }

  const std::string_view native_skip =
      GetLooseNativeAssetSkipReason(decision.normalized);
  if (!native_skip.empty()) {
    decision.kind = RefiiVfsOwnershipKind::NativeOnly;
    decision.reason = std::string(native_skip);
    LooseStats().refii_native_fallbacks.fetch_add(
        1, std::memory_order_relaxed);
    return decision;
  }

  if (ResolveExactTypedBankManifestLooseAsset(
          decision.normalized, &resolution, &candidate)) {
    claim("typed_bank_manifest", std::move(resolution), std::move(candidate));
    return decision;
  }

  if (ResolveSyntheticBankManifestLooseAsset(
          decision.normalized, &resolution, &candidate)) {
    claim("synthetic_bank_manifest", std::move(resolution), std::move(candidate));
    return decision;
  }

  if (ResolveLooseAssetOverride(decision.normalized, &resolution)) {
    claim("loose_override", std::move(resolution), decision.normalized);
    return decision;
  }

  bool resolved = false;
  if (prefer_resource_manager_texture_shape &&
      IsTextureAssetCandidate(decision.normalized)) {
    resolved = ResolveVfsDatabaseAliasLooseAssetForResourceManager(
        decision.normalized, caller_lr, &resolution, &candidate);
  }
  if (!resolved) {
    resolved = ResolveVfsDatabaseExactAliasLooseAsset(
        decision.normalized, &resolution, &candidate);
  }
  if (!resolved) {
    resolved = ResolveVfsDatabaseAliasLooseAsset(
        decision.normalized, &resolution, &candidate);
  }
  if (resolved) {
    if (prefer_resource_manager_texture_shape) {
      const uint32_t texture_hash =
          resource_hash != 0 ? resource_hash
                             : ComputeResourceNameHash(decision.normalized);
      MaybeUpgradeResourceManagerTextureResolution(texture_hash,
                                                   decision.normalized,
                                                   caller_lr,
                                                   &resolution,
                                                   &candidate);
    }
    claim("database_alias", std::move(resolution), std::move(candidate));
    return decision;
  }

  if (resource_hash != 0 &&
      ResolveResourceHashLooseAsset(resource_hash,
                                    &resolution,
                                    &candidate,
                                    prefer_resource_manager_texture_shape,
                                    caller_lr)) {
    claim("resource_hash", std::move(resolution), std::move(candidate));
    return decision;
  }

  if (ResolveSyntheticBankEntryLooseAsset(
          decision.normalized, &resolution, &candidate)) {
    claim("synthetic_bank_entry", std::move(resolution), std::move(candidate));
    return decision;
  }

  if (ResolveOriginalGameDataAsset(decision.normalized, &resolution, &candidate)) {
    claim("original_game_data", std::move(resolution), std::move(candidate));
    return decision;
  }

  if (IsCachedOptionalSyntheticVfsMiss(decision.normalized) ||
      IsOptionalSyntheticVfsProbe(decision.normalized) ||
      IsKnownAbsentBootProbe(decision.normalized)) {
    decision.kind = RefiiVfsOwnershipKind::OptionalMiss;
    decision.reason = decision.reason.empty() ? "known_optional_probe"
                                             : decision.reason;
    LooseStats().refii_optional_misses.fetch_add(1, std::memory_order_relaxed);
    return decision;
  }

  decision.kind = RefiiVfsOwnershipKind::Unresolved;
  decision.reason = decision.reason.empty() ? "not_owned" : decision.reason;
  return decision;
}

RefiiVfsOwnershipDecision DecideRefiiVfsOwnershipForHash(
    uint32_t resource_hash,
    bool prefer_resource_manager_texture_shape,
    uint32_t caller_lr) {
  RefiiVfsOwnershipDecision decision;
  if (!RefiiLooseAssetBackendEnabled() || resource_hash == 0) {
    decision.kind = RefiiVfsOwnershipKind::Unresolved;
    decision.reason = "backend_disabled_or_zero_hash";
    return decision;
  }

  if (ResolveResourceHashLooseAsset(resource_hash,
                                    &decision.resolution,
                                    &decision.candidate,
                                    prefer_resource_manager_texture_shape,
                                    caller_lr)) {
    decision.kind = RefiiVfsOwnershipKind::RefiiOwned;
    decision.source = "resource_hash";
    decision.normalized = NormalizeLooseAssetKey(decision.candidate);
    decision.has_resolution = true;
    decision.native_shaped = IsRefiiNativeShapeSensitiveAsset(decision.normalized);
    decision.reason =
        decision.native_shaped
            ? std::string(GetResourceManagerCachedDirectBypassReason(
                  decision.normalized))
            : std::string{};
    LooseStats().refii_owned_decisions.fetch_add(
        1, std::memory_order_relaxed);
    if (decision.native_shaped) {
      LooseStats().refii_owned_native_shaped.fetch_add(
          1, std::memory_order_relaxed);
    }
    return decision;
  }

  decision.kind = RefiiVfsOwnershipKind::Unresolved;
  decision.reason = "hash_not_owned";
  return decision;
}

void LogRefiiVfsOwnershipDecision(std::string_view api,
                                  uint32_t lr,
                                  std::string_view request,
                                  const RefiiVfsOwnershipDecision& decision) {
  if (!VfsTracingEnabled() && !LooseAssetOverrideTracingEnabled()) {
    return;
  }

  const std::string normalized_request = NormalizeLooseAssetKey(request);
  const std::string normalized_candidate =
      NormalizeLooseAssetKey(decision.candidate);
  const bool interesting =
      IsTraceInterestingAsset(normalized_request) ||
      IsTraceInterestingAsset(normalized_candidate);
  const bool owned_or_native =
      decision.kind == RefiiVfsOwnershipKind::RefiiOwned ||
      decision.kind == RefiiVfsOwnershipKind::NativeOnly;
  if (owned_or_native) {
    const std::string key = fmt::format("RefiiVfsOwnership:{}:{}:{}",
                                        api,
                                        normalized_request,
                                        normalized_candidate);
    static std::mutex logged_mutex;
    static std::unordered_set<std::string> logged_keys;
    bool first_log = false;
    {
      std::lock_guard<std::mutex> lock(logged_mutex);
      first_log = logged_keys.insert(key).second;
      constexpr size_t kMaxLoggedOwnershipKeys = 4096;
      if (logged_keys.size() > kMaxLoggedOwnershipKeys) {
        logged_keys.clear();
        logged_keys.insert(key);
        first_log = true;
      }
    }

    if (!first_log &&
        !ShouldLogRepeatedVfsTraceOperation(key, interesting ? 32 : 128)) {
      return;
    }
  }

  const bool should_log =
      owned_or_native ||
      interesting ||
      ShouldLogRepeatedVfsTraceOperation("RefiiVfsOwnership", 96);
  if (!should_log) {
    return;
  }

  REXLOG_INFO("Refii VFS ownership api={} lr=0x{:08X} request={} "
              "kind={} source={} candidate={} provider_relative={} host={} "
              "native_shaped={} reason={}",
              api,
              lr,
              normalized_request,
              ToString(decision.kind),
              decision.source,
              normalized_candidate,
              decision.has_resolution
                  ? decision.resolution.provider_relative_path
                  : std::string{},
              decision.has_resolution ? decision.resolution.host_path.string()
                                      : std::string{},
              decision.native_shaped,
              decision.reason);
}

uint32_t ResolveRefiiStreamExpectedSize(uint8_t* base,
                                        uint32_t stream_guest,
                                        const LooseStreamRecord& record,
                                        bool has_record,
                                        const RefiiVfsOwnershipDecision& ownership) {
  if (has_record && record.expected_size != 0) {
    return static_cast<uint32_t>(std::min<uint64_t>(
        record.expected_size, std::numeric_limits<uint32_t>::max()));
  }

  if (ownership.has_resolution && !ownership.resolution.host_path.empty()) {
    std::error_code error;
    const uint64_t file_size =
        std::filesystem::file_size(ownership.resolution.host_path, error);
    if (!error && file_size != 0) {
      return static_cast<uint32_t>(
          std::min<uint64_t>(file_size, std::numeric_limits<uint32_t>::max()));
    }
  }

  if (base != nullptr && stream_guest >= 0x10000) {
    const uint64_t inline_size = LoadGuestU64(base, stream_guest + 20);
    if (inline_size != 0) {
      return static_cast<uint32_t>(
          std::min<uint64_t>(inline_size, std::numeric_limits<uint32_t>::max()));
    }
  }

  return 0;
}

bool TryRepairNativeShapedGetCachedOutput(PPCContext& ctx,
                                          uint8_t* base,
                                          const PPCContext& original_ctx,
                                          uint32_t out_guest,
                                          std::string_view name,
                                          std::string_view reason,
                                          const RefiiVfsOwnershipDecision& ownership) {
  if (base == nullptr || out_guest == 0 ||
      !IsNativeGetCachedShapeRepairCandidate(name)) {
    return false;
  }

  const uint32_t raw_stream_guest = TryReadOutputPointer(base, out_guest);
  if (!IsRefiiHostDiskStream(base, raw_stream_guest)) {
    return false;
  }

  LooseStreamRecord record;
  std::string match;
  const bool has_record =
      FindLooseStreamRecord(base, raw_stream_guest, &record, &match);
  const std::string record_path =
      has_record ? record.path : ownership.resolution.host_path.string();
  const std::string record_provider =
      has_record ? record.provider_name
                 : ownership.resolution.provider_relative_path;
  const uint32_t expected_size = ResolveRefiiStreamExpectedSize(
      base, raw_stream_guest, record, has_record, ownership);
  const bool trace =
      VfsTracingEnabled() ||
      ShouldTraceStartupBikStreamShape(
          base, raw_stream_guest, "GetCachedNativeShapeRepair", 8);
  if (trace) {
    REXLOG_INFO(
        "Refii VFS GetCached native-shape repair enter lr=0x{:08X} "
        "name={} reason={} ownership={} source={} candidate={} "
        "out=0x{:08X} raw_stream=0x{:08X} match={} expected_size={} "
        "provider_relative={} host={} raw_words={} raw_shape=[{}]",
        original_ctx.lr,
        name,
        reason.empty() ? "native_shape_sensitive" : reason,
        ToString(ownership.kind),
        ownership.source,
        ownership.candidate,
        out_guest,
        raw_stream_guest,
        has_record ? match : "host_record",
        expected_size,
        record_provider,
        record_path,
        FormatGuestWords(base, raw_stream_guest, 12),
        FormatBikPpcStreamSummary(base, raw_stream_guest));
  }

  PPCContext wrapper_ctx = ctx;
  wrapper_ctx.r3.u32 = out_guest;
  wrapper_ctx.r4.u32 = raw_stream_guest;
  wrapper_ctx.r5.u32 = expected_size;
  wrapper_ctx.r6.u32 = 0;
  LogLooseStreamWrapperCall("GetCachedNativeShapeRepair/CreateBuffered",
                            base,
                            wrapper_ctx,
                            out_guest,
                            raw_stream_guest);
  const PPCContext before_wrapper_ctx = wrapper_ctx;
  ::__imp__rex_lhStream_CreateBuffered(wrapper_ctx, base);
  LogLooseStreamWrapperResult("GetCachedNativeShapeRepair/CreateBuffered",
                              base,
                              before_wrapper_ctx,
                              wrapper_ctx,
                              out_guest,
                              raw_stream_guest);
  RecordLooseStreamAliasFromOutput(
      base,
      "GetCachedNativeShapeRepair/CreateBuffered/r3out",
      out_guest,
      raw_stream_guest);
  RecordLooseStreamAlias(base,
                         "GetCachedNativeShapeRepair/CreateBuffered/r3ret",
                         wrapper_ctx.r3.u32,
                         raw_stream_guest);

  const uint32_t wrapped_stream_guest = TryReadOutputPointer(base, out_guest);
  const uint32_t wrapped_vtable =
      wrapped_stream_guest != 0 ? LoadGuestU32(base, wrapped_stream_guest) : 0;
  const uint32_t wrapped_size28 =
      wrapped_stream_guest != 0 ? LoadGuestU32(base, wrapped_stream_guest + 28)
                                : 0;
  const uint32_t wrapped_size32 =
      wrapped_stream_guest != 0 ? LoadGuestU32(base, wrapped_stream_guest + 32)
                                : 0;
  const bool wrapped_vtable_ok = wrapped_vtable == kNativeBufferedStreamVTable;
  const bool wrapped_size_matches =
      expected_size == 0 || wrapped_size28 == expected_size ||
      wrapped_size32 == expected_size;
  const bool repaired = wrapped_stream_guest != 0 &&
                        wrapped_stream_guest != raw_stream_guest &&
                        !IsRefiiHostDiskStream(base, wrapped_stream_guest) &&
                        wrapped_vtable_ok && wrapped_size_matches;
  RecordRefiiStartupVideoBufferedWrapper(base,
                                         record_provider.empty() ? name
                                                                 : record_provider,
                                         expected_size,
                                         raw_stream_guest,
                                         wrapped_stream_guest,
                                         wrapper_ctx.r5.u32,
                                         wrapper_ctx.r6.u32,
                                         repaired);
  if (trace || VfsTracingEnabled()) {
    REXLOG_INFO(
        "Refii VFS GetCached native-shape repair exit lr=0x{:08X} "
        "name={} reason={} out=0x{:08X} raw_stream=0x{:08X} "
        "wrapped_stream=0x{:08X} result=0x{:08X} repaired={} "
        "expected_size={} wrapped_vtable=0x{:08X} "
        "wrapped_size28=0x{:08X} wrapped_size32=0x{:08X} "
        "wrapped_vtable_ok={} wrapped_size_matches={} "
        "wrapped_words={} wrapped_shape=[{}]",
        original_ctx.lr,
        name,
        reason.empty() ? "native_shape_sensitive" : reason,
        out_guest,
        raw_stream_guest,
        wrapped_stream_guest,
        wrapper_ctx.r3.u32,
        repaired,
        expected_size,
        wrapped_vtable,
        wrapped_size28,
        wrapped_size32,
        wrapped_vtable_ok,
        wrapped_size_matches,
        FormatGuestWords(base, wrapped_stream_guest, 20),
        FormatBikBufferedNativeShape(base, wrapped_stream_guest));
    LogRefiiVfsChainVerdict(
        "native_shaped_stream",
        static_cast<uint32_t>(original_ctx.lr),
        name,
        record_path.empty() ? ownership.candidate : record_path,
        record_provider,
        repaired ? "native_buffered_stream" : "raw_refii_host_stream",
        "ResourceManager_GetCached_native_shape_repair",
        "KeepHooked",
        repaired ? "BIK_cursor_status_or_finalizer"
                 : "CreateBuffered_shape_failure");
  }

  if (!repaired) {
    return false;
  }

  if (IsStartupBikAssetName(name)) {
    const uint32_t raw_ref_before =
        FindRefiiHostStreamRecord(raw_stream_guest) != nullptr
            ? LoadGuestU32(base, raw_stream_guest + 4)
            : 0;
    ReleaseGuestObject(ctx, base, raw_stream_guest);
    bool raw_active_after = FindRefiiHostStreamRecord(raw_stream_guest) != nullptr;
    uint32_t raw_ref_after =
        raw_active_after ? LoadGuestU32(base, raw_stream_guest + 4) : 0;
    if (raw_active_after && raw_ref_after == 0) {
      EraseRefiiHostStreamRecord(raw_stream_guest);
      EraseLooseStreamRecord(raw_stream_guest);
      raw_active_after = false;
    }
    if (VfsTracingEnabled() || LooseAssetOverrideTracingEnabled() ||
        ShouldTraceStartupBikStreamShape(
            base, raw_stream_guest, "GetCachedNativeShapeRepairReleaseRaw", 8)) {
      REXLOG_INFO("Refii VFS GetCached startup BIK released raw source ref "
                  "lr=0x{:08X} name={} raw_stream=0x{:08X} wrapped_stream=0x{:08X} "
                  "ref_before={} ref_after={} active_after={} next={}",
                  original_ctx.lr,
                  name,
                  raw_stream_guest,
                  wrapped_stream_guest,
                  raw_ref_before,
                  raw_ref_after,
                  raw_active_after,
                  "read_request_release_or_BIK_finalizer");
    }
  }

  // Preserve GetCached's return ABI while replacing the out smart pointer shape.
  ctx.r3.u32 = out_guest;
  return true;
}

bool HasRuntimeTextureName(uint32_t resource_hash) {
  if (resource_hash == 0) {
    return false;
  }
  const std::vector<std::string> names =
      GetRuntimeResourceHashNames(resource_hash);
  return std::any_of(names.begin(), names.end(), [](const std::string& name) {
    return IsTextureAssetCandidate(name);
  });
}

bool IsRefiiTextureResolution(std::string_view request_name,
                              uint32_t resource_hash,
                              const LooseAssetResolution& resolution,
                              std::string_view candidate) {
  return IsTextureAssetCandidate(request_name) ||
         IsTextureAssetCandidate(candidate) ||
         IsTextureAssetCandidate(resolution.provider_relative_path) ||
         HasRuntimeTextureName(resource_hash);
}

std::string TextureResolutionCacheKey(uint32_t resource_hash,
                                      std::string_view request_name,
                                      std::string_view candidate) {
  return fmt::format("{}|0x{:08X}|{}|{}",
                     GetLooseAssetRoot().lexically_normal().string(),
                     resource_hash,
                     NormalizeLooseAssetKey(request_name),
                     NormalizeLooseAssetKey(candidate));
}

void LogRefiiOwnedTextureResolution(std::string_view api,
                                    uint32_t caller_lr,
                                    uint32_t resource_hash,
                                    std::string_view request_name,
                                    const LooseAssetResolution& resolution,
                                    std::string_view candidate,
                                    std::string_view source) {
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
  const std::string cache_key =
      TextureResolutionCacheKey(resource_hash,
                                normalized_request,
                                normalized_candidate.empty() ? provider_key
                                                             : normalized_candidate);
  const bool main_menu_texture =
      IsMainMenuTextureHashTraceAsset(normalized_request) ||
      IsMainMenuTextureHashTraceAsset(normalized_candidate) ||
      IsMainMenuTextureHashTraceAsset(provider_key);

  if (!VfsTracingEnabled() && !LooseAssetOverrideTracingEnabled()) {
    if (main_menu_texture) {
      RecordRefiiMainMenuBlockerEvent(
          "texture_selection",
          fmt::format("RefiiOwned api={} lr=0x{:08X} hash=0x{:08X} "
                      "request={} candidate={} provider={} source={}",
                      api,
                      caller_lr,
                      resource_hash,
                      normalized_request,
                      normalized_candidate,
                      provider_key,
                      source));
    }
    return;
  }

  static std::mutex logged_mutex;
  static std::unordered_set<std::string> logged_keys;
  bool first_log = false;
  {
    std::lock_guard<std::mutex> lock(logged_mutex);
    first_log = logged_keys.insert(cache_key).second;
    constexpr size_t kMaxLoggedTextureResolutionKeys = 4096;
    if (logged_keys.size() > kMaxLoggedTextureResolutionKeys) {
      logged_keys.clear();
      logged_keys.insert(cache_key);
      first_log = true;
    }
  }

  if (!first_log &&
      !ShouldLogRepeatedVfsTraceOperation(
          fmt::format("RefiiTextureOwnership:{:08X}:{}",
                      resource_hash,
                      normalized_request),
          main_menu_texture ? 32 : 48)) {
    return;
  }

  uint64_t header_size = 0;
  uint64_t payload_size = 0;
  uint64_t combined_size = HostFileSize(resolution.host_path);
  bool split = false;
  std::string payload_candidate;
  if (IsTextureHeaderCandidate(provider_key) ||
      IsTextureHeaderCandidate(normalized_candidate)) {
    split = true;
    header_size = HostFileSize(resolution.host_path);
    LooseAssetResolution payload_resolution;
    std::string paired_candidate;
    const std::string header_key =
        IsTextureHeaderCandidate(provider_key) ? provider_key
                                               : normalized_candidate;
    if (ResolvePairedTexturePayloadForHeader(
            header_key, &payload_resolution, &paired_candidate)) {
      payload_size = HostFileSize(payload_resolution.host_path);
      payload_candidate = paired_candidate;
      combined_size = header_size + payload_size;
    }
  }

  const std::array<uint32_t, 4> first_words =
      ReadHostFileFirstWords(resolution.host_path);
  REXLOG_INFO("Refii VFS texture ownership verdict api={} lr=0x{:08X} "
              "hash=0x{:08X} request={} kind=RefiiOwned source={} "
              "candidate={} provider_relative={} host={} split={} "
              "header_size={} payload_size={} combined_size={} "
              "payload_candidate={} first_words={} cache_key={}",
              api,
              caller_lr,
              resource_hash,
              normalized_request,
              source,
              normalized_candidate,
              provider_key,
              resolution.host_path.string(),
              split,
              header_size,
              payload_size,
              combined_size,
              payload_candidate,
              FormatHostFileFirstWords(first_words),
              cache_key);

  if (main_menu_texture) {
    RecordRefiiMainMenuBlockerEvent(
        "texture_selection",
        fmt::format("RefiiOwned api={} lr=0x{:08X} hash=0x{:08X} request={} "
                    "candidate={} provider={} split={} header={} payload={} "
                    "combined={} source={}",
                    api,
                    caller_lr,
                    resource_hash,
                    normalized_request,
                    normalized_candidate,
                    provider_key,
                    split,
                    header_size,
                    payload_size,
                    combined_size,
                    source));
  }
}


}  // namespace detail
}  // namespace refii::vfs
