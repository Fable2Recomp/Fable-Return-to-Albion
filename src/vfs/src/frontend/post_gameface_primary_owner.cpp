#include "vfs_hooks_private.h"

namespace refii::vfs {
inline namespace detail {

std::mutex& RefiiPostGameFacePrimaryOwnerMutex() {
  static std::mutex mutex;
  return mutex;
}

std::unordered_set<uint32_t>& RefiiPostGameFacePrimaryOwnerRequests() {
  static std::unordered_set<uint32_t> requests;
  return requests;
}

std::unordered_map<uint32_t, uint32_t>& RefiiPostGameFacePrimaryOwnerSlots() {
  static std::unordered_map<uint32_t, uint32_t> slots;
  return slots;
}

std::atomic<uint32_t>& RefiiPostGameFacePrimaryOwnerRequest() {
  static std::atomic<uint32_t> request{0};
  return request;
}

std::unordered_set<uint32_t>& RefiiPostGameFacePrimaryOwnerRuns() {
  static std::unordered_set<uint32_t> owners;
  return owners;
}

std::atomic<bool>& RefiiPostLoadingFrontendFinalHandoffWakeSeen() {
  static std::atomic<bool> seen{false};
  return seen;
}

bool RefiiPostLoadingFrontendFinalHandoffWakeEmitted() {
  return RefiiPostLoadingFrontendFinalHandoffWakeSeen().load(
      std::memory_order_relaxed);
}

bool RefiiPostGameFacePackageReadyForGeneratedRelease() {
  return RefiiPostGameFacePackageComplete() ||
         RefiiPostGameFacePackageNativeFallbackReady();
}

bool RefiiPostGameFacePrimaryOwnerReady(uint8_t* base) {
  if (base == nullptr) {
    return false;
  }
  constexpr uint32_t kLoadingGlobalBase = 0x83490000u;
  constexpr uint32_t kPrimaryOwnerGlobal = kLoadingGlobalBase + 27320u;
  constexpr uint32_t kPrimaryReadyGlobal = kLoadingGlobalBase + 27230u;
  return LooksLikeGuestPointer(LoadGuestU32(base, kPrimaryOwnerGlobal)) &&
         LoadGuestU8(base, kPrimaryReadyGlobal) != 0;
}

bool TryGetRefiiPostGameFacePrimaryRenderRequest(uint8_t* base,
                                                 uint32_t* request_guest_out,
                                                 uint32_t* owner_guest_out,
                                                 uint32_t* handoff_guest_out) {
  if (base == nullptr) {
    return false;
  }

  uint32_t request_guest =
      RefiiPostGameFacePrimaryOwnerRequest().load(std::memory_order_relaxed);
  if (!IsNativeFrontendRenderRequest(base, request_guest)) {
    std::lock_guard<std::mutex> lock(RefiiPostGameFacePrimaryOwnerMutex());
    for (const uint32_t published_request :
         RefiiPostGameFacePrimaryOwnerRequests()) {
      if (IsNativeFrontendRenderRequest(base, published_request)) {
        request_guest = published_request;
        break;
      }
    }
  }
  if (!IsNativeFrontendRenderRequest(base, request_guest)) {
    return false;
  }

  const uint32_t owner_guest = LoadGuestU32(base, request_guest + 24);
  const uint32_t handoff_guest = LoadGuestU32(base, request_guest + 40);
  if (request_guest_out != nullptr) {
    *request_guest_out = request_guest;
  }
  if (owner_guest_out != nullptr) {
    *owner_guest_out = owner_guest;
  }
  if (handoff_guest_out != nullptr) {
    *handoff_guest_out = handoff_guest;
  }
  return true;
}

uint32_t ResolveRefiiPostGameFacePrimaryOwnerSlotObject(
    uint8_t* base,
    uint32_t request_guest,
    uint32_t secondary_owner) {
  if (!IsNativeFrontendRenderRequest(base, request_guest)) {
    return 0;
  }
  auto is_frontend_render_slot = [&](uint32_t slot_guest) {
    return LooksLikeGuestPointer(slot_guest) &&
           LoadGuestU32(base, slot_guest) ==
               kNativeFrontendRenderRequestSlotVTable &&
           LoadGuestU32(base, slot_guest + 12) == request_guest;
  };
  if (is_frontend_render_slot(secondary_owner)) {
    return secondary_owner;
  }

  {
    std::lock_guard<std::mutex> lock(RefiiPostGameFacePrimaryOwnerMutex());
    const auto it = RefiiPostGameFacePrimaryOwnerSlots().find(request_guest);
    if (it != RefiiPostGameFacePrimaryOwnerSlots().end() &&
        is_frontend_render_slot(it->second)) {
      return it->second;
    }
  }

  if (is_frontend_render_slot(request_guest)) {
    return request_guest;
  }

  return 0;
}

bool TryPublishRefiiPostGameFacePrimaryOwner(
    const PPCContext& source_ctx,
    uint8_t* base,
    std::string_view asset_path,
    std::string_view source_api,
    uint32_t package_mask) {
  const std::string normalized_asset = NormalizeLooseAssetKey(asset_path);
  auto log_skip = [&](std::string_view reason,
                      uint32_t secondary_owner = 0,
                      uint32_t request_guest = 0) {
    if (VfsTracingEnabled() &&
        ShouldLogRepeatedVfsTraceOperation(
            "PostGameFacePrimaryOwnerPublishSkipped", 12)) {
      REXLOG_INFO("Refii VFS post-GameFace primary owner publish skipped "
                  "reason={} asset={} source={} package_mask=0x{:02X} "
                  "secondary=0x{:08X} request=0x{:08X} request_state=[{}] "
                  "globals=[{}] handoff=[{}] main_menu=[{}]",
                  reason,
                  normalized_asset,
                  source_api,
                  package_mask,
                  secondary_owner,
                  request_guest,
                  FormatNativeVfsAsyncRequestState(base, request_guest),
                  FormatRefiiLoadingScreenSequenceGlobals(base),
                  FormatRefiiPostVideoHandoffTraceForLog(),
                  FormatRefiiMainMenuBlockerTraceForLog());
    }
  };

  if (base == nullptr) {
    log_skip("base_null");
    return false;
  }
  if (!RefiiLooseAssetBackendEnabled()) {
    return false;
  }
  const bool native_fallback_package =
      (package_mask & 0x7u) != 0x7u &&
      RefiiPostGameFacePackageNativeFallbackReady() &&
      EndsWith(normalized_asset, "frontendstartupscreen.fac");
  if ((package_mask & 0x7u) != 0x7u && !native_fallback_package) {
    log_skip("package_incomplete");
    return false;
  }
  if (!RefiiStartupVideosReadyForMenuHandoff()) {
    log_skip("startup_videos_not_ready");
    return false;
  }

  constexpr uint32_t kLoadingGlobalBase = 0x83490000u;
  constexpr uint32_t kPrimaryOwnerGlobal = kLoadingGlobalBase + 27320u;
  constexpr uint32_t kPrimaryReadyGlobal = kLoadingGlobalBase + 27230u;
  constexpr uint32_t kSecondaryOwnerGlobal = kLoadingGlobalBase + 27328u;
  constexpr uint32_t kSecondaryReadyGlobal = kLoadingGlobalBase + 27332u;
  constexpr uint32_t kLoadingPrimaryModeGlobal = kLoadingGlobalBase + 27392u;

  const uint32_t primary_before = LoadGuestU32(base, kPrimaryOwnerGlobal);
  const uint8_t primary_ready_before = LoadGuestU8(base, kPrimaryReadyGlobal);
  if (LooksLikeGuestPointer(primary_before) && primary_ready_before != 0) {
    log_skip("primary_already_ready", 0, primary_before);
    LogRefiiVfsChainVerdict("frontend_post_video",
                            static_cast<uint32_t>(source_ctx.lr),
                            normalized_asset,
                            "",
                            "",
                            "Refii_gameface_package_complete",
                            "post_gameface_primary_owner_publish",
                            "Opened",
                            "native_primary_loading_owner_already_ready");
    return true;
  }

  uint32_t secondary_owner = LoadGuestU32(base, kSecondaryOwnerGlobal);
  uint32_t request_guest =
      LooksLikeGuestPointer(secondary_owner)
          ? LoadGuestU32(base, secondary_owner + 12)
          : 0;
  bool recovered_request = false;
  bool recovered_secondary_owner = false;
  if (!IsNativeFrontendRenderRequest(base, request_guest)) {
    const uint32_t remembered_request =
        RefiiPostGameFacePrimaryOwnerRequest().load(std::memory_order_relaxed);
    if (IsNativeFrontendRenderRequest(base, remembered_request)) {
      request_guest = remembered_request;
      recovered_request = true;
    }
  }
  if (!IsNativeFrontendRenderRequest(base, request_guest)) {
    std::lock_guard<std::mutex> lock(RefiiPostGameFacePrimaryOwnerMutex());
    for (const uint32_t published_request :
         RefiiPostGameFacePrimaryOwnerRequests()) {
      if (IsNativeFrontendRenderRequest(base, published_request)) {
        request_guest = published_request;
        recovered_request = true;
        break;
      }
    }
  }
  if (IsNativeFrontendRenderRequest(base, request_guest)) {
    const uint32_t resolved_secondary =
        ResolveRefiiPostGameFacePrimaryOwnerSlotObject(base,
                                                       request_guest,
                                                       secondary_owner);
    if (LooksLikeGuestPointer(resolved_secondary) &&
        resolved_secondary != secondary_owner) {
      secondary_owner = resolved_secondary;
      StoreGuestU32(base, kSecondaryOwnerGlobal, secondary_owner);
      recovered_secondary_owner = true;
    }
  }
  const uint8_t secondary_ready_before = LoadGuestU8(base, kSecondaryReadyGlobal);
  if (!LooksLikeGuestPointer(secondary_owner) &&
      !IsNativeFrontendRenderRequest(base, request_guest)) {
    log_skip("secondary_missing", secondary_owner, request_guest);
    return false;
  }
  if (!IsNativeFrontendRenderRequest(base, request_guest)) {
    if (VfsTracingEnabled() &&
        ShouldLogRepeatedVfsTraceOperation(
            "PostGameFacePrimaryOwnerBadRequest", 8)) {
      REXLOG_INFO("Refii VFS post-GameFace primary owner publish skipped "
                  "bad request asset={} source={} secondary=0x{:08X} "
                  "request=0x{:08X} request_state=[{}] globals=[{}] "
                  "handoff=[{}] main_menu=[{}]",
                  NormalizeLooseAssetKey(asset_path),
                  source_api,
                  secondary_owner,
                  request_guest,
                  FormatNativeVfsAsyncRequestState(base, request_guest),
                  FormatRefiiLoadingScreenSequenceGlobals(base),
                  FormatRefiiPostVideoHandoffTraceForLog(),
                  FormatRefiiMainMenuBlockerTraceForLog());
    }
    log_skip("bad_frontend_request", secondary_owner, request_guest);
    return false;
  }
  if (!LooksLikeGuestPointer(secondary_owner) &&
      (!RefiiPostVideoFrontendChainConsumed() ||
       !RefiiPostVfsChainReadyForLoadingOwnerRelease() ||
       !RefiiMainMenuTraceHasParsedTextureForGeneratedReadyPayload())) {
    log_skip("secondary_missing", secondary_owner, request_guest);
    return false;
  }

  bool promoted_secondary_ready = false;
  if (secondary_ready_before == 0) {
    if (!RefiiPostVideoFrontendChainConsumed() ||
        !RefiiPostVfsChainReadyForLoadingOwnerRelease()) {
      log_skip("secondary_not_ready", secondary_owner, request_guest);
      return false;
    }
    // The Refii-owned post-video path has already delivered the native render
    // request in the secondary slot. Mirror the generated ready gate so the
    // primary owner publish can consume the same VFS-facing shape.
    StoreGuestU8(base, kSecondaryReadyGlobal, 1);
    promoted_secondary_ready =
        LoadGuestU8(base, kSecondaryReadyGlobal) != secondary_ready_before;
  }
  if (recovered_request || recovered_secondary_owner) {
    RecordRefiiMainMenuBlockerEvent(
        "post_gameface_primary_owner_recovered",
        fmt::format("asset={} source={} request=0x{:08X} secondary=0x{:08X} "
                    "request_recovered={} secondary_recovered={} globals=[{}]",
                    normalized_asset,
                    source_api,
                    request_guest,
                    secondary_owner,
                    recovered_request ? "true" : "false",
                    recovered_secondary_owner ? "true" : "false",
                    FormatRefiiLoadingScreenSequenceGlobals(base)));
    if (VfsTracingEnabled() ||
        ShouldLogRepeatedVfsTraceOperation(
            "PostGameFacePrimaryOwnerRecovered", 8)) {
      REXLOG_INFO("Refii VFS post-GameFace primary owner recovered "
                  "asset={} source={} request=0x{:08X} secondary=0x{:08X} "
                  "request_recovered={} secondary_recovered={} "
                  "request_state=[{}] globals=[{}] handoff=[{}] "
                  "main_menu=[{}]",
                  normalized_asset,
                  source_api,
                  request_guest,
                  secondary_owner,
                  recovered_request ? "true" : "false",
                  recovered_secondary_owner ? "true" : "false",
                  FormatNativeVfsAsyncRequestState(base, request_guest),
                  FormatRefiiLoadingScreenSequenceGlobals(base),
                  FormatRefiiPostVideoHandoffTraceForLog(),
                  FormatRefiiMainMenuBlockerTraceForLog());
    }
  }

  {
    std::lock_guard<std::mutex> lock(RefiiPostGameFacePrimaryOwnerMutex());
    if (!RefiiPostGameFacePrimaryOwnerRequests().insert(request_guest).second) {
      if (LooksLikeGuestPointer(secondary_owner)) {
        RefiiPostGameFacePrimaryOwnerSlots()[request_guest] = secondary_owner;
      }
      if (LooksLikeGuestPointer(primary_before) && primary_ready_before != 0) {
        log_skip("request_already_published", secondary_owner, request_guest);
        return false;
      }
    }
    if (LooksLikeGuestPointer(secondary_owner)) {
      RefiiPostGameFacePrimaryOwnerSlots()[request_guest] = secondary_owner;
    }
  }

  const uint8_t request_done_before = LoadGuestU8(base, request_guest + 92);
  const uint32_t request_owner_before = LoadGuestU32(base, request_guest + 132);
  const uint32_t request_resource_before = LoadGuestU32(base, request_guest + 136);
  const uint32_t loading_mode_before =
      LoadGuestU32(base, kLoadingPrimaryModeGlobal);

  // Generated frontend completion callers set this flag immediately before
  // sub_82319928 publishes the primary loading owner.
  StoreGuestU8(base, request_guest + 92, 1);
  if (loading_mode_before == 0) {
    StoreGuestU32(base, kLoadingPrimaryModeGlobal, 1);
  }

  PPCContext publish_ctx = source_ctx;
  publish_ctx.lr = 0x82379818u;
  publish_ctx.r3.u32 = request_guest;
  sub_82319928(publish_ctx, base);

  const uint32_t primary_after = LoadGuestU32(base, kPrimaryOwnerGlobal);
  uint8_t primary_ready_after = LoadGuestU8(base, kPrimaryReadyGlobal);
  const uint32_t request_owner_after = LoadGuestU32(base, request_guest + 132);
  const uint32_t primary_inner_after =
      LooksLikeGuestPointer(primary_after) ? LoadGuestU32(base, primary_after + 12)
                                           : 0;
  uint32_t primary_inner_mode_after = 0;
  if (LooksLikeGuestPointer(primary_inner_after)) {
    primary_inner_mode_after = LoadGuestU32(base, primary_inner_after + 284);
    if (primary_inner_mode_after == 0) {
      StoreGuestU32(base,
                    primary_inner_after + 284,
                    LoadGuestU32(base, kLoadingPrimaryModeGlobal));
      primary_inner_mode_after = LoadGuestU32(base, primary_inner_after + 284);
    }
  }

  const bool published =
      LooksLikeGuestPointer(primary_after) &&
      (request_owner_after == primary_after ||
       LooksLikeGuestPointer(request_owner_after));
  if (published) {
    RefiiPostGameFacePrimaryOwnerRequest().store(request_guest,
                                                std::memory_order_relaxed);
  }
  bool promoted_primary_ready = false;
  if (published && primary_ready_after == 0 &&
      RefiiPostGameFacePackageHandoffSignalEmitted() &&
      RefiiStartupVideosReadyForMenuHandoff()) {
    // Generated primary-owner setup normally raises this ready gate before the
    // loading FX/resource phase reads the primary resource tables. Refii owns
    // the post-GameFace package completion here, so mirror that VFS-facing
    // state transition after the native owner has been published.
    StoreGuestU8(base, kPrimaryReadyGlobal, 1);
    primary_ready_after = LoadGuestU8(base, kPrimaryReadyGlobal);
    promoted_primary_ready = primary_ready_after != 0;
  }
  if (!published) {
    std::lock_guard<std::mutex> lock(RefiiPostGameFacePrimaryOwnerMutex());
    RefiiPostGameFacePrimaryOwnerRequests().erase(request_guest);
    RefiiPostGameFacePrimaryOwnerSlots().erase(request_guest);
  }

  RecordRefiiMainMenuBlockerEvent(
      "post_gameface_primary_owner_publish",
      fmt::format("asset={} source={} request=0x{:08X} package_mask=0x{:02X} "
                  "published={} primary=0x{:08X}/{}->0x{:08X}/{} "
                  "secondary=0x{:08X}/{}->{} "
                  "request_done=0x{:02X}->0x{:02X} "
                  "request_owner=0x{:08X}->0x{:08X} "
                  "request_resource=0x{:08X} primary_inner=0x{:08X} "
                  "primary_inner_mode=0x{:08X} loading_mode=0x{:08X}->0x{:08X} "
                  "secondary_ready_promoted={} primary_ready_promoted={} "
                  "result=0x{:08X} globals=[{}]",
                  normalized_asset,
                  source_api,
                  request_guest,
                  package_mask,
                  published ? "true" : "false",
                  primary_before,
                  primary_ready_before,
                  primary_after,
                  primary_ready_after,
                  secondary_owner,
                  secondary_ready_before,
                  LoadGuestU8(base, kSecondaryReadyGlobal),
                  request_done_before,
                  LoadGuestU8(base, request_guest + 92),
                  request_owner_before,
                  request_owner_after,
                  request_resource_before,
                  primary_inner_after,
                  primary_inner_mode_after,
                  loading_mode_before,
                  LoadGuestU32(base, kLoadingPrimaryModeGlobal),
                  promoted_secondary_ready ? "true" : "false",
                  promoted_primary_ready ? "true" : "false",
                  publish_ctx.r3.u32,
                  FormatRefiiLoadingScreenSequenceGlobals(base)));

  if (VfsTracingEnabled() ||
      ShouldLogRepeatedVfsTraceOperation("PostGameFacePrimaryOwnerPublish", 8)) {
    REXLOG_INFO("Refii VFS post-GameFace primary owner publish asset={} "
                "source={} request=0x{:08X} package_mask=0x{:02X} "
                "published={} result=0x{:08X} primary=0x{:08X}/{}->0x{:08X}/{} "
                "secondary=0x{:08X}/{}->{} "
                "request_owner=0x{:08X}->0x{:08X} "
                "primary_inner=0x{:08X} primary_inner_mode=0x{:08X} "
                "secondary_ready_promoted={} primary_ready_promoted={} "
                "request_state=[{}] globals=[{}] handoff=[{}] main_menu=[{}]",
                normalized_asset,
                source_api,
                request_guest,
                package_mask,
                published ? "true" : "false",
                publish_ctx.r3.u32,
                primary_before,
                primary_ready_before,
                primary_after,
                primary_ready_after,
                secondary_owner,
                secondary_ready_before,
                LoadGuestU8(base, kSecondaryReadyGlobal),
                request_owner_before,
                request_owner_after,
                primary_inner_after,
                primary_inner_mode_after,
                promoted_secondary_ready ? "true" : "false",
                promoted_primary_ready ? "true" : "false",
                FormatNativeVfsAsyncRequestState(base, request_guest),
                FormatRefiiLoadingScreenSequenceGlobals(base),
                FormatRefiiPostVideoHandoffTraceForLog(),
                FormatRefiiMainMenuBlockerTraceForLog());
  }

  LogRefiiVfsChainVerdict("frontend_post_video",
                          static_cast<uint32_t>(source_ctx.lr),
                          normalized_asset,
                          "",
                          "",
                          "Refii_gameface_package_complete",
                          "post_gameface_primary_owner_publish",
                          published ? "Opened" : "OpenFailed",
                          published ? "native_primary_loading_owner"
                                    : "native_primary_owner_missing");
  return published;
}

bool TryPublishRefiiPostGameFacePrimaryOwnerFromTrace(PPCContext& source_ctx,
                                                      uint8_t* base,
                                                      std::string_view source_api) {
  std::string asset;
  uint32_t package_mask = 0;
  bool native_fallback_package = false;
  {
    std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
    const auto& trace = RefiiPostVideoHandoffTraceState();
    package_mask = RefiiCompletedPostGameFacePackageMaskLocked(trace);
    native_fallback_package =
        trace.post_gameface_package_native_fallback_required &&
        trace.post_frontend_consumer_handoff_signal_emitted &&
        trace.frontend_consumer_seen;
    if (!trace.post_gameface_package_handoff_signal_emitted &&
        !native_fallback_package) {
      return false;
    }
    if (!native_fallback_package && (package_mask & 0x7u) != 0x7u &&
        !EndsWith(trace.post_gameface_package_handoff_asset, ".fac")) {
      return false;
    }
    asset = trace.post_gameface_package_handoff_asset;
    if (asset.empty() && native_fallback_package) {
      asset = "art/gui/gameface/frontendstartupscreen.fac";
    }
    if (!EndsWith(asset, ".fac")) {
      if (!trace.post_gameface_completed_package_key.empty()) {
        asset = trace.post_gameface_completed_package_key + ".fac";
      } else {
        const size_t dot = asset.find_last_of('.');
        asset = (dot == std::string::npos ? asset : asset.substr(0, dot)) +
                ".fac";
      }
    }
  }
  return TryPublishRefiiPostGameFacePrimaryOwner(source_ctx,
                                                 base,
                                                 asset,
                                                 source_api,
                                                 package_mask);
}

bool TryRunRefiiPostGameFacePrimaryOwnerNativeFromTrace(
    const PPCContext& source_ctx,
    uint8_t* base,
    std::string_view source_api) {
  if (base == nullptr || !RefiiLooseAssetBackendEnabled() ||
      !RefiiPostGameFacePackageHandoffSignalEmitted() ||
      !RefiiStartupVideosReadyForMenuHandoff()) {
    return false;
  }

  constexpr uint32_t kLoadingGlobalBase = 0x83490000u;
  constexpr uint32_t kPrimaryOwnerGlobal = kLoadingGlobalBase + 27320u;
  constexpr uint32_t kPrimaryReadyGlobal = kLoadingGlobalBase + 27230u;

  const uint32_t primary_owner = LoadGuestU32(base, kPrimaryOwnerGlobal);
  const uint8_t primary_ready = LoadGuestU8(base, kPrimaryReadyGlobal);
  if (!LooksLikeGuestPointer(primary_owner) || primary_ready == 0) {
    if (VfsTracingEnabled() &&
        ShouldLogRepeatedVfsTraceOperation(
            "PostGameFacePrimaryOwnerNativeRunSkipped", 8)) {
      REXLOG_INFO("Refii VFS post-GameFace primary owner native run skipped "
                  "source={} primary=0x{:08X}/{} globals=[{}] handoff=[{}] "
                  "main_menu=[{}]",
                  source_api,
                  primary_owner,
                  primary_ready,
                  FormatRefiiLoadingScreenSequenceGlobals(base),
                  FormatRefiiPostVideoHandoffTraceForLog(),
                  FormatRefiiMainMenuBlockerTraceForLog());
    }
    return false;
  }

  const std::string before_state =
      FormatGuestWordsIfPointer(base, primary_owner, 16);
  const uint32_t before_inner = LoadGuestU32(base, primary_owner + 12);
  if (source_api == "post_gameface_package_handoff_after_publish") {
    RecordRefiiMainMenuBlockerEvent(
        "post_gameface_primary_owner_nonblocking_ready",
        fmt::format("source={} owner=0x{:08X} inner=0x{:08X} "
                    "globals=[{}]",
                    source_api,
                    primary_owner,
                    before_inner,
                    FormatRefiiLoadingScreenSequenceGlobals(base)));
    if (VfsTracingEnabled() ||
        ShouldLogRepeatedVfsTraceOperation(
            "PostGameFacePrimaryOwnerNonblockingReady", 8)) {
      REXLOG_INFO("Refii VFS post-GameFace primary owner nonblocking ready "
                  "source={} owner=0x{:08X} inner=0x{:08X} state=[{}] "
                  "globals=[{}] handoff=[{}] main_menu=[{}]",
                  source_api,
                  primary_owner,
                  before_inner,
                  before_state,
                  FormatRefiiLoadingScreenSequenceGlobals(base),
                  FormatRefiiPostVideoHandoffTraceForLog(),
                  FormatRefiiMainMenuBlockerTraceForLog());
    }
    LogRefiiVfsChainVerdict("frontend_post_video",
                            static_cast<uint32_t>(source_ctx.lr),
                            "post_gameface_primary_owner",
                            "",
                            "",
                            "native_primary_loading_owner",
                            "post_gameface_primary_owner_nonblocking_ready",
                            "Opened",
                            "frontend_render_request_or_loading_owner_pipeline");
    return true;
  }

  {
    std::lock_guard<std::mutex> lock(RefiiPostGameFacePrimaryOwnerMutex());
    if (!RefiiPostGameFacePrimaryOwnerRuns().insert(primary_owner).second) {
      return false;
    }
  }

  PPCContext run_ctx = source_ctx;
  run_ctx.r3.u32 = primary_owner;
  run_ctx.lr = 0x82379820u;
  __imp__sub_82313270(run_ctx, base);

  const uint32_t after_inner = LoadGuestU32(base, primary_owner + 12);
  RecordRefiiMainMenuBlockerEvent(
      "post_gameface_primary_owner_native_run",
      fmt::format("source={} owner=0x{:08X} result=0x{:08X} "
                  "inner=0x{:08X}->0x{:08X} globals=[{}]",
                  source_api,
                  primary_owner,
                  run_ctx.r3.u32,
                  before_inner,
                  after_inner,
                  FormatRefiiLoadingScreenSequenceGlobals(base)));

  if (VfsTracingEnabled() ||
      ShouldLogRepeatedVfsTraceOperation(
          "PostGameFacePrimaryOwnerNativeRun", 8)) {
    REXLOG_INFO("Refii VFS post-GameFace primary owner native run "
                "source={} owner=0x{:08X} result=0x{:08X} "
                "inner=0x{:08X}->0x{:08X} before=[{}] after=[{}] "
                "globals=[{}] handoff=[{}] main_menu=[{}]",
                source_api,
                primary_owner,
                run_ctx.r3.u32,
                before_inner,
                after_inner,
                before_state,
                FormatGuestWordsIfPointer(base, primary_owner, 16),
                FormatRefiiLoadingScreenSequenceGlobals(base),
                FormatRefiiPostVideoHandoffTraceForLog(),
                FormatRefiiMainMenuBlockerTraceForLog());
  }

  LogRefiiVfsChainVerdict("frontend_post_video",
                          static_cast<uint32_t>(source_ctx.lr),
                          "post_gameface_primary_owner",
                          "",
                          "",
                          "native_primary_loading_owner",
                          "post_gameface_primary_owner_native_run",
                          "Opened",
                          "loading_screen_owner_pipeline");
  return true;
}

bool TryRunRefiiPostGameFaceFrontendRenderRequestFromTrace(
    PPCContext& source_ctx,
    uint8_t* base,
    std::string_view source_api) {
  if (base == nullptr || !RefiiPostGameFacePackageHandoffSignalEmitted()) {
    return false;
  }

  constexpr uint32_t kLoadingGlobalBase = 0x83490000u;
  constexpr uint32_t kPrimaryOwnerGlobal = kLoadingGlobalBase + 27320u;
  constexpr uint32_t kSecondaryOwnerGlobal = kLoadingGlobalBase + 27328u;

  uint32_t primary_owner = LoadGuestU32(base, kPrimaryOwnerGlobal);
  uint32_t secondary_owner = LoadGuestU32(base, kSecondaryOwnerGlobal);
  uint32_t request_guest =
      LooksLikeGuestPointer(secondary_owner)
          ? LoadGuestU32(base, secondary_owner + 12)
          : 0;
  if (!IsNativeFrontendRenderRequest(base, request_guest)) {
    std::lock_guard<std::mutex> lock(RefiiPostGameFacePrimaryOwnerMutex());
    for (const uint32_t published_request :
         RefiiPostGameFacePrimaryOwnerRequests()) {
      if (IsNativeFrontendRenderRequest(base, published_request)) {
        request_guest = published_request;
        break;
      }
    }
  }
  if (!IsNativeFrontendRenderRequest(base, request_guest)) {
    const uint32_t remembered_request =
        RefiiPostGameFacePrimaryOwnerRequest().load(std::memory_order_relaxed);
    if (IsNativeFrontendRenderRequest(base, remembered_request)) {
      request_guest = remembered_request;
    }
  }
  if (!LooksLikeGuestPointer(primary_owner) ||
      !IsNativeFrontendRenderRequest(base, request_guest)) {
    if (VfsTracingEnabled() &&
        ShouldLogRepeatedVfsTraceOperation(
            "PostGameFaceFrontendRenderRunSkipped", 8)) {
      REXLOG_INFO("Refii VFS post-GameFace frontend render run skipped "
                  "source={} primary=0x{:08X} secondary=0x{:08X} "
                  "request=0x{:08X} request_state=[{}] globals=[{}] "
                  "handoff=[{}] main_menu=[{}]",
                  source_api,
                  primary_owner,
                  secondary_owner,
                  request_guest,
                  FormatNativeVfsAsyncRequestState(base, request_guest),
                  FormatRefiiLoadingScreenSequenceGlobals(base),
                  FormatRefiiPostVideoHandoffTraceForLog(),
                  FormatRefiiMainMenuBlockerTraceForLog());
    }
    return false;
  }

  const bool use_nonblocking_completion_shape =
      source_api == "post_gameface_package_handoff_signal" ||
      source_api == "post_gameface_followup_boundary" ||
      source_api == "post_loading_frontend_render_handoff" ||
      source_api == "sub_8235D5E8_sequence_bridge" ||
      source_api == "sub_8235D5E8_sequence_ready" ||
      source_api == "sub_8235E1E0_sequence_bridge" ||
      source_api == "sub_8235E1E0_sequence_ready" ||
      source_api == "sub_82360418_sequence_bridge" ||
      source_api == "sub_82360418_sequence_ready" ||
      source_api == "sub_82360618_sequence_bridge" ||
      source_api == "sub_82360618_sequence_ready" ||
      source_api == "sub_82360968_sequence_bridge" ||
      source_api == "sub_82360968_sequence_ready";
  if (use_nonblocking_completion_shape) {
    return TryRunRefiiFrontendRenderRequestInline(source_ctx,
                                                  base,
                                                  request_guest,
                                                  source_api,
                                                  true,
                                                  true);
  }

  PPCContext native_run_ctx = source_ctx;
  if (TryRunRefiiFrontendRenderRequestInline(native_run_ctx,
                                             base,
                                             request_guest,
                                             source_api,
                                             true,
                                             false)) {
    return true;
  }

  return TryRunRefiiFrontendRenderRequestInline(source_ctx,
                                                base,
                                                request_guest,
                                                source_api,
                                                true,
                                                true);
}

bool TryCompleteRefiiPostLoadingFrontendRenderProducer(
    const PPCContext& source_ctx,
    uint8_t* base,
    std::string_view source_api,
    uint32_t request_guest,
    uint32_t owner_guest,
    uint32_t handoff_guest,
    uint32_t source124,
    uint32_t source128) {
  auto log_skip = [&](std::string_view reason,
                      uint32_t current_handoff = 0,
                      uint32_t signal_handle = 0,
                      uint32_t wait_handle = 0) {
    if (VfsTracingEnabled() ||
        ShouldLogRepeatedVfsTraceOperation(
            "PostLoadingRenderProducerCompleteSkipped", 8)) {
      REXLOG_INFO("Refii VFS post-loading render producer complete skipped "
                  "source={} reason={} request=0x{:08X} owner=0x{:08X} "
                  "handoff=0x{:08X} current_handoff=0x{:08X} "
                  "source_pair=0x{:08X}/0x{:08X} signal=0x{:08X} "
                  "wait=0x{:08X} producer=[{}] globals=[{}] "
                  "handoff_trace=[{}] main_menu=[{}]",
                  source_api,
                  reason,
                  request_guest,
                  owner_guest,
                  handoff_guest,
                  current_handoff,
                  source124,
                  source128,
                  signal_handle,
                  wait_handle,
                  FormatPostVideoRenderProducerState(base, handoff_guest),
                  FormatRefiiLoadingScreenSequenceGlobals(base),
                  FormatRefiiPostVideoHandoffTraceForLog(),
                  FormatRefiiMainMenuBlockerTraceForLog());
    }
  };

  if (base == nullptr || !RefiiLooseAssetBackendEnabled()) {
    return false;
  }
  if (!LooksLikeGuestPointer(owner_guest) ||
      !LooksLikeGuestPointer(handoff_guest)) {
    log_skip("bad_pointer");
    return false;
  }

  const uint32_t current_handoff = LoadGuestU32(base, owner_guest + 4);
  const uint32_t signal_handle = LoadGuestU32(base, handoff_guest + 456);
  const uint32_t wait_object = handoff_guest + 460;
  const uint32_t wait_handle =
      LoadGuestU32(base, wait_object + 128);
  if (current_handoff != handoff_guest) {
    log_skip("owner_handoff_mismatch",
             current_handoff,
             signal_handle,
             wait_handle);
    return false;
  }
  if (source124 == 0xFFFFFFFFu || source128 == 0xFFFFFFFFu) {
    log_skip("missing_source_pair",
             current_handoff,
             signal_handle,
             wait_handle);
    return false;
  }

  {
    std::lock_guard<std::mutex> lock(RefiiInlineFrontendRenderRequestMutex());
    if (!RefiiPostLoadingFrontendRenderProducerCompletions()
             .insert(handoff_guest)
             .second) {
      return true;
    }
  }

  const uint8_t complete_before = LoadGuestU8(base, handoff_guest + 168);
  const uint8_t dirty_before = LoadGuestU8(base, handoff_guest + 132);
  const uint8_t flag244_before = LoadGuestU8(base, handoff_guest + 244);
  const uint32_t result116_before = LoadGuestU32(base, handoff_guest + 116);
  const uint32_t frame120_before = LoadGuestU32(base, handoff_guest + 120);
  const std::string before_state =
      FormatPostVideoRenderProducerState(base, handoff_guest);

  if (frame120_before != source128) {
    StoreGuestU8(base, handoff_guest + 132, 1);
  }
  StoreGuestU32(base, handoff_guest + 120, source128);
  StoreGuestU32(base, handoff_guest + 116, source124);
  StoreGuestU8(base, handoff_guest + 168, 1);
  StoreGuestU8(base, handoff_guest + 244, 1);

  const auto looks_like_event_handle = [](uint32_t handle) {
    return (handle & 0xFF000000u) == 0xF8000000u;
  };

  PPCContext signal_ctx = source_ctx;
  signal_ctx.r3.u32 = 0;
  if (looks_like_event_handle(signal_handle)) {
    signal_ctx.r3.u32 = signal_handle;
    ::__imp__sub_822C9388(signal_ctx, base);
  }

  PPCContext wait_signal_ctx = source_ctx;
  wait_signal_ctx.r3.u32 = 0;
  if (looks_like_event_handle(wait_handle) && wait_handle != signal_handle) {
    wait_signal_ctx.r3.u32 = wait_handle;
    ::__imp__sub_822C9388(wait_signal_ctx, base);
  }

  // Keep the scheduled producer attached to the render owner. Generated
  // sub_82388A88 only queues the handoff; later owner/frame code observes and
  // retires it. Clearing it here hides the completed handoff from that native
  // transition.
  const uint32_t owner_after = LoadGuestU32(base, owner_guest + 4);

  const uint8_t complete_after = LoadGuestU8(base, handoff_guest + 168);
  const uint8_t dirty_after = LoadGuestU8(base, handoff_guest + 132);
  const uint8_t flag244_after = LoadGuestU8(base, handoff_guest + 244);
  const uint32_t result116_after = LoadGuestU32(base, handoff_guest + 116);
  const uint32_t frame120_after = LoadGuestU32(base, handoff_guest + 120);

  RecordRefiiMainMenuBlockerEvent(
      "post_loading_render_producer_complete",
      fmt::format("source={} request=0x{:08X} owner=0x{:08X} "
                  "handoff=0x{:08X} source_pair=0x{:08X}/0x{:08X} "
                  "signal=0x{:08X} wait_object=0x{:08X} wait=0x{:08X} "
                  "complete=0x{:02X}->0x{:02X} dirty=0x{:02X}->0x{:02X} "
                  "flag244=0x{:02X}->0x{:02X} "
                  "result116=0x{:08X}->0x{:08X} "
                  "frame120=0x{:08X}->0x{:08X} "
                  "owner_plus4_after=0x{:08X} "
                  "signal_result=0x{:08X} wait_signal_result=0x{:08X} "
                  "main_menu=[{}]",
                  source_api,
                  request_guest,
                  owner_guest,
                  handoff_guest,
                  source124,
                  source128,
                  signal_handle,
                  wait_object,
                  wait_handle,
                  complete_before,
                  complete_after,
                  dirty_before,
                  dirty_after,
                  flag244_before,
                  flag244_after,
                  result116_before,
                  result116_after,
                  frame120_before,
                  frame120_after,
                  owner_after,
                  signal_ctx.r3.u32,
                  wait_signal_ctx.r3.u32,
                  FormatRefiiMainMenuBlockerTraceForLog()));

  if (VfsTracingEnabled() ||
      ShouldLogRepeatedVfsTraceOperation("PostLoadingRenderProducerComplete",
                                         8)) {
    REXLOG_INFO("Refii VFS post-loading render producer complete "
                "source={} request=0x{:08X} owner=0x{:08X} "
                "handoff=0x{:08X} source_pair=0x{:08X}/0x{:08X} "
                "signal=0x{:08X} wait_object=0x{:08X} wait=0x{:08X} "
                "complete=0x{:02X}->0x{:02X} dirty=0x{:02X}->0x{:02X} "
                "flag244=0x{:02X}->0x{:02X} "
                "result116=0x{:08X}->0x{:08X} "
                "frame120=0x{:08X}->0x{:08X} "
                "owner_plus4_after=0x{:08X} "
                "signal_result=0x{:08X} wait_signal_result=0x{:08X} "
                "before=[{}] after=[{}] signal_state=[{}] wait_state=[{}] "
                "handoff_trace=[{}] main_menu=[{}]",
                source_api,
                request_guest,
                owner_guest,
                handoff_guest,
                source124,
                source128,
                signal_handle,
                wait_object,
                wait_handle,
                complete_before,
                complete_after,
                dirty_before,
                dirty_after,
                flag244_before,
                flag244_after,
                result116_before,
                result116_after,
                frame120_before,
                frame120_after,
                owner_after,
                signal_ctx.r3.u32,
                wait_signal_ctx.r3.u32,
                before_state,
                FormatPostVideoRenderProducerState(base, handoff_guest),
                FormatGeneratedEventSetState(base, signal_handle),
                FormatGeneratedEventSetState(base, wait_handle),
                FormatRefiiPostVideoHandoffTraceForLog(),
                FormatRefiiMainMenuBlockerTraceForLog());
  }

  LogRefiiVfsChainVerdict("frontend_post_video",
                          static_cast<uint32_t>(source_ctx.lr),
                          "post_loading_frontend_render_request",
                          "",
                          "",
                          "Refii_post_loading_render_producer",
                          "post_loading_render_producer_complete",
                          "Opened",
                          "completed_and_signaled_native_handoff");
  return true;
}

bool TryAttachRefiiPostGameFaceRenderHandoffToActiveFrame(
    const PPCContext& source_ctx,
    uint8_t* base,
    std::string_view source_api) {
  if (base == nullptr || !RefiiLooseAssetBackendEnabled() ||
      !RefiiStartupVideosReadyForMenuHandoff() ||
      !RefiiPostGameFacePackageHandoffSignalEmitted() ||
      !RefiiPostVideoFrontendChainConsumed() ||
      !RefiiMainMenuTraceHasParsedTextureForGeneratedReadyPayload() ||
      !RefiiPostGameFacePrimaryOwnerReady(base)) {
    return false;
  }

  uint32_t request_guest =
      RefiiPostGameFacePrimaryOwnerRequest().load(std::memory_order_relaxed);
  if (!IsNativeFrontendRenderRequest(base, request_guest)) {
    std::lock_guard<std::mutex> lock(RefiiPostGameFacePrimaryOwnerMutex());
    for (const uint32_t published_request :
         RefiiPostGameFacePrimaryOwnerRequests()) {
      if (IsNativeFrontendRenderRequest(base, published_request)) {
        request_guest = published_request;
        break;
      }
    }
  }
  if (!IsNativeFrontendRenderRequest(base, request_guest)) {
    return false;
  }

  const uint32_t global_frame_owner = FindPostVideoOwnerFromGlobalRoot(base);
  const uint32_t request_inline_owner = LoadGuestU32(base, request_guest + 24);
  const uint32_t request_render_source = LoadGuestU32(base, request_guest + 28);
  const uint32_t request_handoff = LoadGuestU32(base, request_guest + 40);
  const uint32_t request_owner = LoadGuestU32(base, request_guest + 132);
  const uint32_t call_owner = source_ctx.r3.u32;
  const bool call_owner_matches_request =
      LooksLikeGuestPointer(call_owner) &&
      (call_owner == request_inline_owner || call_owner == request_owner);
  const uint32_t frame_owner =
      call_owner_matches_request ? call_owner : global_frame_owner;
  uint32_t existing_frame_handoff =
      LooksLikeGuestPointer(frame_owner) ? LoadGuestU32(base, frame_owner + 4)
                                         : 0;
  constexpr uint32_t kRenderSourceOwnerDelta = 0x30u;
  const bool source_matches_target_owner =
      request_render_source == frame_owner ||
      (request_render_source >= kRenderSourceOwnerDelta &&
       request_render_source - kRenderSourceOwnerDelta == frame_owner);
  const bool source_matches_global_frame =
      request_render_source == global_frame_owner ||
      (request_render_source >= kRenderSourceOwnerDelta &&
       request_render_source - kRenderSourceOwnerDelta == global_frame_owner);
  const bool source_matches_frame =
      source_matches_target_owner ||
      (call_owner_matches_request && source_matches_global_frame);
  const bool inline_owner_matches =
      source_ctx.r3.u32 == 0 || request_inline_owner == 0 ||
      request_inline_owner == source_ctx.r3.u32;
  const bool ownerless_request_matches_frame =
      request_render_source == 0 && inline_owner_matches;

  auto resolve_source_pair = [&](uint32_t handoff_guest,
                                 uint32_t* source124_out,
                                 uint32_t* source128_out) {
    uint32_t source124 = 0xFFFFFFFFu;
    uint32_t source128 = 0xFFFFFFFFu;

    const uint32_t render_source_head =
        LooksLikeGuestPointer(request_render_source)
            ? LoadGuestU32(base, request_render_source + 4)
            : 0;
    if (LooksLikeGuestPointer(render_source_head)) {
      source128 = LoadGuestU32(base, render_source_head);
      source124 = LoadGuestU32(base, render_source_head + 8);
    }

    if ((source124 == 0xFFFFFFFFu || source128 == 0xFFFFFFFFu) &&
        LooksLikeGuestPointer(handoff_guest)) {
      const uint32_t scheduled_source124 =
          LoadGuestU32(base, handoff_guest + 124);
      const uint32_t scheduled_source128 =
          LoadGuestU32(base, handoff_guest + 128);
      if (scheduled_source124 != 0xFFFFFFFFu &&
          scheduled_source128 != 0xFFFFFFFFu) {
        source124 = scheduled_source124;
        source128 = scheduled_source128;
      }
    }

    if (source124 == 0xFFFFFFFFu || source128 == 0xFFFFFFFFu) {
      std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
      const auto& trace = RefiiPostVideoHandoffTraceState();
      const bool nonidle_matches =
          trace.last_nonidle_render_schedule_owner == frame_owner &&
          trace.last_nonidle_render_schedule_handoff == handoff_guest;
      const bool render_matches =
          trace.last_render_schedule_owner == frame_owner &&
          trace.last_render_schedule_handoff == handoff_guest;
      if (nonidle_matches) {
        source124 = trace.last_nonidle_render_schedule_source124;
        source128 = trace.last_nonidle_render_schedule_source128;
      } else if (render_matches) {
        source124 = trace.last_render_schedule_source124;
        source128 = trace.last_render_schedule_source128;
      }
    }

    if (source124 == 0xFFFFFFFFu || source128 == 0xFFFFFFFFu) {
      return false;
    }
    if (source124_out != nullptr) {
      *source124_out = source124;
    }
    if (source128_out != nullptr) {
      *source128_out = source128;
    }
    return true;
  };

  auto looks_like_dynamic_render_object = [&](uint32_t object_guest) {
    if (!LooksLikeGuestPointer(object_guest)) {
      return false;
    }
    const uint32_t vtable = LoadGuestU32(base, object_guest);
    if (!LooksLikeGuestPointer(vtable)) {
      return false;
    }
    return LooksLikeGuestPointer(LoadGuestU32(base, vtable + 16));
  };

  auto resolve_dynamic_render_object = [&](uint32_t handoff_guest) {
    if (LooksLikeGuestPointer(handoff_guest)) {
      const uint32_t existing = LoadGuestU32(base, handoff_guest + 240);
      if (looks_like_dynamic_render_object(existing)) {
        return existing;
      }
    }

    const uint32_t request_render_owner = LoadGuestU32(base, request_guest + 24);
    const uint32_t request_render_handoff =
        LooksLikeGuestPointer(request_render_owner)
            ? LoadGuestU32(base, request_render_owner + 4)
            : 0;
    if (LooksLikeGuestPointer(request_render_handoff)) {
      const uint32_t request_dynamic =
          LoadGuestU32(base, request_render_handoff + 240);
      if (looks_like_dynamic_render_object(request_dynamic)) {
        return request_dynamic;
      }
    }

    std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
    const auto& trace = RefiiPostVideoHandoffTraceState();
    if (looks_like_dynamic_render_object(trace.last_ready_poll_word0)) {
      return trace.last_ready_poll_word0;
    }
    return 0u;
  };

  auto seed_dynamic_render_object = [&](uint32_t handoff_guest) {
    if (!LooksLikeGuestPointer(handoff_guest)) {
      return 0u;
    }
    const uint32_t existing = LoadGuestU32(base, handoff_guest + 240);
    if (looks_like_dynamic_render_object(existing)) {
      return existing;
    }
    const uint32_t dynamic_object = resolve_dynamic_render_object(handoff_guest);
    if (looks_like_dynamic_render_object(dynamic_object)) {
      StoreGuestU32(base, handoff_guest + 240, dynamic_object);
      return dynamic_object;
    }
    return 0u;
  };

  auto log_skip = [&](std::string_view reason) {
    if (VfsTracingEnabled() &&
        ShouldLogRepeatedVfsTraceOperation(
            "PostGameFaceFrameHandoffAttachSkipped", 8)) {
      REXLOG_INFO("Refii VFS post-GameFace frame handoff attach skipped "
                  "source={} reason={} lr=0x{:08X} root=0x{:08X} "
                  "frame_owner=0x{:08X} frame_handoff=0x{:08X} "
                  "request=0x{:08X} inline_owner=0x{:08X} "
                  "render_source=0x{:08X} request_handoff=0x{:08X} "
                  "request_owner=0x{:08X} global_frame_owner=0x{:08X} "
                  "call_owner_matches_request={} request_state=[{}] "
                  "frame_chain=[{}] globals=[{}] handoff=[{}] main_menu=[{}]",
                  source_api,
                  reason,
                  static_cast<uint32_t>(source_ctx.lr),
                  source_ctx.r3.u32,
                  frame_owner,
                  existing_frame_handoff,
                  request_guest,
                  request_inline_owner,
                  request_render_source,
                  request_handoff,
                  request_owner,
                  global_frame_owner,
                  call_owner_matches_request ? "true" : "false",
                  FormatNativeVfsAsyncRequestState(base, request_guest),
                  FormatCurrentPostVideoFrameChainState(base),
                  FormatRefiiLoadingScreenSequenceGlobals(base),
                  FormatRefiiPostVideoHandoffTraceForLog(),
                  FormatRefiiMainMenuBlockerTraceForLog());
    }
  };

  if (!LooksLikeGuestPointer(frame_owner)) {
    log_skip("frame_owner_missing");
    return false;
  }
  if (LooksLikeGuestPointer(existing_frame_handoff)) {
    if (existing_frame_handoff != request_handoff) {
      const bool post_gameface_renderer_repair =
          source_api == "Renderer_BeginScene_PreNativePostGameFaceFrame" &&
          RefiiPostVfsChainReadyForLoadingOwnerRelease();
      const bool post_gameface_package_repair =
          source_api == "PostGameFacePackage_Ready_PreFrame" &&
          RefiiPostVfsChainReadyForLoadingOwnerRelease();
      const bool post_gameface_followup_repair =
          source_api == "post_gameface_followup_boundary" &&
          RefiiPostVfsChainReadyForLoadingOwnerRelease();
      const bool post_gameface_scheduler_repair =
          (source_api == "NativeVfsRetryInsert" ||
           source_api == "NativeVfsSchedulerInsertPending") &&
          RefiiPostVfsChainReadyForLoadingOwnerRelease();
      const bool post_gameface_completed_request_repair =
          (source_api ==
               "post_loading_frontend_render_handoff_completed_primary_attach" ||
           source_api ==
               "completed_loading_owner_renderer_reentry_completed_primary_attach" ||
           source_api ==
               "sub_8235D5E8_sequence_bridge_completed_primary_attach" ||
           source_api ==
               "sub_8235D5E8_sequence_ready_completed_primary_attach" ||
           source_api ==
               "sub_8235E1E0_sequence_bridge_completed_primary_attach" ||
           source_api ==
               "sub_8235E1E0_sequence_ready_completed_primary_attach" ||
           source_api ==
               "sub_82360418_sequence_bridge_completed_primary_attach" ||
           source_api ==
               "sub_82360418_sequence_ready_completed_primary_attach" ||
           source_api ==
               "sub_82360618_sequence_bridge_completed_primary_attach" ||
           source_api ==
               "sub_82360618_sequence_ready_completed_primary_attach" ||
           source_api ==
               "sub_82360968_sequence_bridge_completed_primary_attach" ||
           source_api ==
               "sub_82360968_sequence_ready_completed_primary_attach") &&
          RefiiPostVfsChainReadyForLoadingOwnerRelease();
      const bool can_replace_stale_inline_handoff =
          call_owner_matches_request &&
          (source_api == "PostVideoFrame_82389210_PreNative" ||
           post_gameface_renderer_repair || post_gameface_package_repair ||
           post_gameface_followup_repair || post_gameface_scheduler_repair ||
           post_gameface_completed_request_repair) &&
          LooksLikeGuestPointer(request_handoff);
      if (!can_replace_stale_inline_handoff) {
        log_skip("frame_handoff_busy");
        return false;
      }
      StoreGuestU32(base, frame_owner + 4, request_handoff);
      existing_frame_handoff = request_handoff;
    }

    uint32_t source124 = 0xFFFFFFFFu;
    uint32_t source128 = 0xFFFFFFFFu;
    const bool has_source_pair =
        resolve_source_pair(request_handoff, &source124, &source128);
    const uint32_t dynamic_object =
        seed_dynamic_render_object(request_handoff);
    const uint8_t complete_before = LoadGuestU8(base, request_handoff + 168);
    const uint8_t flag244_before = LoadGuestU8(base, request_handoff + 244);
    const bool completion_ran =
        has_source_pair &&
        TryCompleteRefiiPostLoadingFrontendRenderProducer(
            source_ctx,
            base,
            fmt::format("{}_existing", source_api),
            request_guest,
            frame_owner,
            request_handoff,
            source124,
            source128);

    if (VfsTracingEnabled() ||
        ShouldLogRepeatedVfsTraceOperation(
            "PostGameFaceFrameHandoffExistingComplete", 8)) {
      REXLOG_INFO("Refii VFS post-GameFace frame handoff existing "
                  "source={} lr=0x{:08X} root=0x{:08X} frame_owner=0x{:08X} "
                  "request=0x{:08X} request_handoff=0x{:08X} "
                  "dynamic_object=0x{:08X} source_pair=0x{:08X}/0x{:08X} "
                  "source_pair_ready={} completion_ran={} "
                  "complete=0x{:02X}->0x{:02X} flag244=0x{:02X}->0x{:02X} "
                  "request_state=[{}] frame_chain=[{}] globals=[{}] "
                  "handoff=[{}] main_menu=[{}]",
                  source_api,
                  static_cast<uint32_t>(source_ctx.lr),
                  source_ctx.r3.u32,
                  frame_owner,
                  request_guest,
                  request_handoff,
                  dynamic_object,
                  source124,
                  source128,
                  has_source_pair ? "true" : "false",
                  completion_ran ? "true" : "false",
                  complete_before,
                  LoadGuestU8(base, request_handoff + 168),
                  flag244_before,
                  LoadGuestU8(base, request_handoff + 244),
                  FormatNativeVfsAsyncRequestState(base, request_guest),
                  FormatCurrentPostVideoFrameChainState(base),
                  FormatRefiiLoadingScreenSequenceGlobals(base),
                  FormatRefiiPostVideoHandoffTraceForLog(),
                  FormatRefiiMainMenuBlockerTraceForLog());
    }
    return true;
  }
  if (!LooksLikeGuestPointer(request_handoff)) {
    log_skip("request_handoff_missing");
    return false;
  }
  if ((!source_matches_frame && !ownerless_request_matches_frame) ||
      !inline_owner_matches) {
    log_skip("frame_owner_mismatch");
    return false;
  }

  const uint8_t complete_before = LoadGuestU8(base, request_handoff + 168);
  const uint8_t flag244_before = LoadGuestU8(base, request_handoff + 244);
  StoreGuestU32(base, frame_owner + 4, request_handoff);
  uint32_t source124 = 0xFFFFFFFFu;
  uint32_t source128 = 0xFFFFFFFFu;
  const bool has_source_pair =
      resolve_source_pair(request_handoff, &source124, &source128);
  const uint32_t dynamic_object = seed_dynamic_render_object(request_handoff);
  const bool completion_ran =
      has_source_pair &&
      TryCompleteRefiiPostLoadingFrontendRenderProducer(source_ctx,
                                                        base,
                                                        source_api,
                                                        request_guest,
                                                        frame_owner,
                                                        request_handoff,
                                                        source124,
                                                        source128);
  RecordRefiiRenderProducerScheduleSnapshot(
      base,
      frame_owner,
      static_cast<uint32_t>(source_ctx.lr),
      source124 != 0xFFFFFFFFu ? source124
                               : LoadGuestU32(base, request_handoff + 116),
      source128 != 0xFFFFFFFFu ? source128
                               : LoadGuestU32(base, request_handoff + 120),
      RefiiGuestF64FromWord(LoadGuestU64(base, request_handoff + 160)));

  RecordRefiiMainMenuBlockerEvent(
      "post_gameface_frame_handoff_attach",
      fmt::format("source={} lr=0x{:08X} root=0x{:08X} "
                  "frame_owner=0x{:08X} request=0x{:08X} "
                  "request_handoff=0x{:08X} dynamic_object=0x{:08X} "
                  "source_pair=0x{:08X}/0x{:08X} source_pair_ready={} "
                  "completion_ran={} "
                  "complete=0x{:02X}->0x{:02X} flag244=0x{:02X}->0x{:02X} "
                  "request_state=[{}] frame_chain=[{}]",
                  source_api,
                  static_cast<uint32_t>(source_ctx.lr),
                  source_ctx.r3.u32,
                  frame_owner,
                  request_guest,
                  request_handoff,
                  dynamic_object,
                  source124,
                  source128,
                  has_source_pair ? "true" : "false",
                  completion_ran ? "true" : "false",
                  complete_before,
                  LoadGuestU8(base, request_handoff + 168),
                  flag244_before,
                  LoadGuestU8(base, request_handoff + 244),
                  FormatNativeVfsAsyncRequestState(base, request_guest),
                  FormatCurrentPostVideoFrameChainState(base)));

  if (VfsTracingEnabled() ||
      ShouldLogRepeatedVfsTraceOperation("PostGameFaceFrameHandoffAttach", 8)) {
    REXLOG_INFO("Refii VFS post-GameFace frame handoff attached "
                "source={} lr=0x{:08X} root=0x{:08X} frame_owner=0x{:08X} "
                "request=0x{:08X} request_handoff=0x{:08X} "
                "dynamic_object=0x{:08X} "
                "source_pair=0x{:08X}/0x{:08X} source_pair_ready={} "
                "completion_ran={} complete=0x{:02X}->0x{:02X} "
                "flag244=0x{:02X}->0x{:02X} request_state=[{}] "
                "frame_chain=[{}] globals=[{}] handoff=[{}] main_menu=[{}]",
                source_api,
                static_cast<uint32_t>(source_ctx.lr),
                source_ctx.r3.u32,
                frame_owner,
                request_guest,
                request_handoff,
                dynamic_object,
                source124,
                source128,
                has_source_pair ? "true" : "false",
                completion_ran ? "true" : "false",
                complete_before,
                LoadGuestU8(base, request_handoff + 168),
                flag244_before,
                LoadGuestU8(base, request_handoff + 244),
                FormatNativeVfsAsyncRequestState(base, request_guest),
                FormatCurrentPostVideoFrameChainState(base),
                FormatRefiiLoadingScreenSequenceGlobals(base),
                FormatRefiiPostVideoHandoffTraceForLog(),
                FormatRefiiMainMenuBlockerTraceForLog());
  }

  LogRefiiVfsChainVerdict("frontend_post_video",
                          static_cast<uint32_t>(source_ctx.lr),
                          "post_gameface_frame_handoff",
                          "",
                          "",
                          "Refii_post_gameface_render_request",
                          "post_gameface_frame_handoff_attach",
                          "Opened",
                          "native_renderer_begin_scene_consume");
  return true;
}

bool TryPublishRefiiPostLoadingFrontendCompletionState(
    const PPCContext& source_ctx,
    uint8_t* base,
    std::string_view source_api,
    uint32_t request_guest,
    uint32_t primary_owner) {
  if (base == nullptr || !RefiiLooseAssetBackendEnabled() ||
      !IsNativeFrontendRenderRequest(base, request_guest)) {
    return false;
  }

  constexpr uint32_t kLoadingGlobalBase = 0x83490000u;
  constexpr uint32_t kPrimaryOwnerGlobal = kLoadingGlobalBase + 27320u;
  constexpr uint32_t kPrimaryReadyGlobal = kLoadingGlobalBase + 27230u;
  constexpr uint32_t kLoadingPrimaryModeGlobal = kLoadingGlobalBase + 27392u;

  uint32_t effective_primary_owner = primary_owner;
  if (!LooksLikeGuestPointer(effective_primary_owner)) {
    effective_primary_owner = LoadGuestU32(base, kPrimaryOwnerGlobal);
  }
  if (!LooksLikeGuestPointer(effective_primary_owner)) {
    return false;
  }

  {
    std::lock_guard<std::mutex> lock(RefiiInlineFrontendRenderRequestMutex());
    if (!RefiiPostLoadingFrontendCompletionPublishes()
             .insert(request_guest)
             .second) {
      return true;
    }
  }

  const uint8_t primary_ready_before = LoadGuestU8(base, kPrimaryReadyGlobal);
  const uint8_t request_done_before = LoadGuestU8(base, request_guest + 92);
  const uint32_t request_owner_before = LoadGuestU32(base, request_guest + 132);
  const uint32_t loading_mode_before =
      LoadGuestU32(base, kLoadingPrimaryModeGlobal);
  const uint32_t primary_inner =
      LoadGuestU32(base, effective_primary_owner + 12);
  const uint32_t primary_inner_mode_before =
      LooksLikeGuestPointer(primary_inner)
          ? LoadGuestU32(base, primary_inner + 284)
          : 0;

  StoreGuestU8(base, request_guest + 92, 1);
  if (!LooksLikeGuestPointer(request_owner_before)) {
    StoreGuestU32(base, request_guest + 132, effective_primary_owner);
  }
  if (loading_mode_before == 0) {
    StoreGuestU32(base, kLoadingPrimaryModeGlobal, 1);
  }
  if (primary_ready_before == 0) {
    StoreGuestU8(base, kPrimaryReadyGlobal, 1);
  }

  const uint32_t loading_mode_after =
      LoadGuestU32(base, kLoadingPrimaryModeGlobal);
  if (LooksLikeGuestPointer(primary_inner) &&
      primary_inner_mode_before == 0) {
    StoreGuestU32(base, primary_inner + 284, loading_mode_after);
  }
  const uint32_t primary_inner_mode_after =
      LooksLikeGuestPointer(primary_inner)
          ? LoadGuestU32(base, primary_inner + 284)
          : 0;

  RecordRefiiMainMenuBlockerEvent(
      "post_loading_frontend_completion_publish",
      fmt::format("source={} request=0x{:08X} primary=0x{:08X} "
                  "primary_ready=0x{:02X}->0x{:02X} "
                  "request_done=0x{:02X}->0x{:02X} "
                  "request_owner=0x{:08X}->0x{:08X} "
                  "primary_inner=0x{:08X} "
                  "primary_inner_mode=0x{:08X}->0x{:08X} "
                  "loading_mode=0x{:08X}->0x{:08X} globals=[{}]",
                  source_api,
                  request_guest,
                  effective_primary_owner,
                  primary_ready_before,
                  LoadGuestU8(base, kPrimaryReadyGlobal),
                  request_done_before,
                  LoadGuestU8(base, request_guest + 92),
                  request_owner_before,
                  LoadGuestU32(base, request_guest + 132),
                  primary_inner,
                  primary_inner_mode_before,
                  primary_inner_mode_after,
                  loading_mode_before,
                  loading_mode_after,
                  FormatRefiiLoadingScreenSequenceGlobals(base)));

  if (VfsTracingEnabled() ||
      ShouldLogRepeatedVfsTraceOperation(
          "PostLoadingFrontendCompletionPublish", 8)) {
    REXLOG_INFO("Refii VFS post-loading frontend completion publish "
                "source={} request=0x{:08X} primary=0x{:08X} "
                "primary_ready=0x{:02X}->0x{:02X} "
                "request_done=0x{:02X}->0x{:02X} "
                "request_owner=0x{:08X}->0x{:08X} "
                "primary_inner=0x{:08X} "
                "primary_inner_mode=0x{:08X}->0x{:08X} "
                "loading_mode=0x{:08X}->0x{:08X} request_state=[{}] "
                "globals=[{}] handoff=[{}] main_menu=[{}]",
                source_api,
                request_guest,
                effective_primary_owner,
                primary_ready_before,
                LoadGuestU8(base, kPrimaryReadyGlobal),
                request_done_before,
                LoadGuestU8(base, request_guest + 92),
                request_owner_before,
                LoadGuestU32(base, request_guest + 132),
                primary_inner,
                primary_inner_mode_before,
                primary_inner_mode_after,
                loading_mode_before,
                loading_mode_after,
                FormatNativeVfsAsyncRequestState(base, request_guest),
                FormatRefiiLoadingScreenSequenceGlobals(base),
                FormatRefiiPostVideoHandoffTraceForLog(),
                FormatRefiiMainMenuBlockerTraceForLog());
  }

  LogRefiiVfsChainVerdict("frontend_post_video",
                          static_cast<uint32_t>(source_ctx.lr),
                          "post_loading_frontend_render_request",
                          "",
                          "",
                          "Refii_loading_owner_complete",
                          "post_loading_frontend_completion_publish",
                          "Opened",
                          "native_primary_loading_owner_continue");
  return true;
}

bool TryFinalizeRefiiPostLoadingFrontendGeneratedState(
    const PPCContext& source_ctx,
    uint8_t* base,
    std::string_view source_api,
    bool clear_render_handoff,
    bool preserve_post_gameface_primary_allowed) {
  if (base == nullptr || !RefiiLooseAssetBackendEnabled() ||
      !RefiiPostGameFacePackageHandoffSignalEmitted() ||
      !RefiiStartupVideosReadyForMenuHandoff()) {
    return false;
  }

  constexpr uint32_t kLoadingGlobalBase = 0x83490000u;
  constexpr uint32_t kPrimaryReadyGlobal = kLoadingGlobalBase + 27230u;
  constexpr uint32_t kPrimaryOwnerGlobal = kLoadingGlobalBase + 27320u;
  constexpr uint32_t kSecondaryOwnerGlobal = kLoadingGlobalBase + 27328u;
  constexpr uint32_t kSecondaryReadyGlobal = kLoadingGlobalBase + 27332u;
  constexpr uint32_t kLoadingPrimaryModeGlobal = kLoadingGlobalBase + 27392u;

  uint32_t primary_owner = LoadGuestU32(base, kPrimaryOwnerGlobal);
  uint32_t secondary_owner = LoadGuestU32(base, kSecondaryOwnerGlobal);
  uint32_t request_guest =
      LooksLikeGuestPointer(secondary_owner)
          ? LoadGuestU32(base, secondary_owner + 12)
          : 0;
  if (!IsNativeFrontendRenderRequest(base, request_guest)) {
    const uint32_t remembered_request =
        RefiiPostGameFacePrimaryOwnerRequest().load(std::memory_order_relaxed);
    if (IsNativeFrontendRenderRequest(base, remembered_request)) {
      request_guest = remembered_request;
    }
  }
  if (!IsNativeFrontendRenderRequest(base, request_guest)) {
    return false;
  }

  const bool late_after_holder_cleanup_source =
      source_api == "post_loading_after_holder_mode5_cleaned";
  if (late_after_holder_cleanup_source &&
      !LooksLikeGuestPointer(primary_owner) &&
      LooksLikeGuestPointer(secondary_owner) &&
      IsNativeFrontendRenderRequest(base, request_guest)) {
    std::string completed_package_asset;
    uint32_t completed_package_mask = 0;
    {
      std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
      const auto& trace = RefiiPostVideoHandoffTraceState();
      completed_package_asset =
          !trace.post_gameface_completed_package_key.empty()
              ? trace.post_gameface_completed_package_key + ".fac"
              : trace.post_gameface_package_handoff_asset;
      completed_package_mask =
          trace.post_gameface_completed_package_extension_mask != 0
              ? trace.post_gameface_completed_package_extension_mask
              : trace.post_gameface_package_extension_mask;
    }
    if (completed_package_asset.empty()) {
      completed_package_asset = "art/gui/gameface/frontendstartupscreen.fac";
    }
    const uint8_t secondary_ready_retry_before =
        LoadGuestU8(base, kSecondaryReadyGlobal);
    if (secondary_ready_retry_before == 0) {
      StoreGuestU8(base, kSecondaryReadyGlobal, 1);
    }
    const bool primary_published =
        TryPublishRefiiPostGameFacePrimaryOwner(
            source_ctx,
            base,
            completed_package_asset,
            "post_loading_after_holder_primary_publish",
            completed_package_mask);
    bool primary_native_run = false;
    bool primary_sequence_ready = false;
    bool primary_finish_tick = false;
    uint32_t primary_sequence_handle = 0;
    if (primary_published) {
      primary_owner = LoadGuestU32(base, kPrimaryOwnerGlobal);
      primary_native_run =
          TryRunRefiiPostGameFacePrimaryOwnerNativeFromTrace(
              source_ctx,
              base,
              "post_loading_after_holder_primary_publish");
    }
    RecordRefiiMainMenuBlockerEvent(
        "post_loading_after_holder_primary_publish_retry",
        fmt::format("source={} asset={} package_mask=0x{:02X} "
                    "secondary=0x{:08X} secondary_ready=0x{:02X}->0x{:02X} "
                    "request=0x{:08X} published={} native_run={} "
                    "sequence_ready={} sequence_handle=0x{:08X} "
                    "finish_tick={} globals=[{}]",
                    source_api,
                    completed_package_asset,
                    completed_package_mask,
                    secondary_owner,
                    secondary_ready_retry_before,
                    LoadGuestU8(base, kSecondaryReadyGlobal),
                    request_guest,
                    primary_published ? "true" : "false",
                    primary_native_run ? "true" : "false",
                    primary_sequence_ready ? "true" : "false",
                    primary_sequence_handle,
                    primary_finish_tick ? "true" : "false",
                    FormatRefiiLoadingScreenSequenceGlobals(base)));
    if (!primary_published && secondary_ready_retry_before == 0) {
      StoreGuestU8(base, kSecondaryReadyGlobal, 0);
    }
    primary_owner = LoadGuestU32(base, kPrimaryOwnerGlobal);
    secondary_owner = LoadGuestU32(base, kSecondaryOwnerGlobal);
    if (LooksLikeGuestPointer(secondary_owner)) {
      const uint32_t refreshed_request = LoadGuestU32(base, secondary_owner + 12);
      if (IsNativeFrontendRenderRequest(base, refreshed_request)) {
        request_guest = refreshed_request;
      }
    }
  }

  bool first_finalize = false;
  {
    std::lock_guard<std::mutex> lock(RefiiInlineFrontendRenderRequestMutex());
    first_finalize =
        RefiiPostLoadingFrontendStateFinalizes().insert(request_guest).second;
    if (!first_finalize && !clear_render_handoff) {
      return true;
    }
  }

  uint32_t render_owner = LoadGuestU32(base, request_guest + 24);
  uint32_t render_handoff =
      LooksLikeGuestPointer(render_owner) ? LoadGuestU32(base, render_owner + 4)
                                          : 0;
  const uint32_t request_handoff = LoadGuestU32(base, request_guest + 40);
  if (!LooksLikeGuestPointer(render_handoff) &&
      LooksLikeGuestPointer(request_handoff)) {
    render_handoff = request_handoff;
  }
  if (late_after_holder_cleanup_source &&
      (!LooksLikeGuestPointer(render_owner) ||
       !LooksLikeGuestPointer(render_handoff))) {
    std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
    const auto& trace = RefiiPostVideoHandoffTraceState();
    const uint32_t fallback_owner =
        LooksLikeGuestPointer(trace.last_nonidle_render_schedule_owner)
            ? trace.last_nonidle_render_schedule_owner
            : trace.last_render_schedule_owner;
    const uint32_t fallback_handoff =
        fallback_owner == trace.last_nonidle_render_schedule_owner
            ? trace.last_nonidle_render_schedule_handoff
            : trace.last_render_schedule_handoff;
    if (LooksLikeGuestPointer(fallback_owner) &&
        LooksLikeGuestPointer(fallback_handoff) &&
        LoadGuestU32(base, fallback_owner + 4) == fallback_handoff) {
      render_owner = fallback_owner;
      render_handoff = fallback_handoff;
    }
  }

  const uint8_t secondary_ready_before =
      LoadGuestU8(base, kSecondaryReadyGlobal);
  const uint8_t primary_ready_before = LoadGuestU8(base, kPrimaryReadyGlobal);
  const uint32_t primary_owner_global_before =
      LoadGuestU32(base, kPrimaryOwnerGlobal);
  const uint32_t secondary_owner_global_before =
      LoadGuestU32(base, kSecondaryOwnerGlobal);
  const uint32_t loading_mode_before =
      LoadGuestU32(base, kLoadingPrimaryModeGlobal);
  const uint8_t request_done_before = LoadGuestU8(base, request_guest + 92);
  const uint8_t request_flag196_before = LoadGuestU8(base, request_guest + 196);
  const uint32_t request_owner_before = LoadGuestU32(base, request_guest + 132);
  const uint32_t render_owner_handoff_before =
      LooksLikeGuestPointer(render_owner) ? LoadGuestU32(base, render_owner + 4)
                                          : 0;
  const uint8_t handoff_complete_before =
      LooksLikeGuestPointer(render_handoff) ? LoadGuestU8(base, render_handoff + 168)
                                            : 0;
  const uint8_t handoff_dirty_before =
      LooksLikeGuestPointer(render_handoff) ? LoadGuestU8(base, render_handoff + 132)
                                            : 0;
  const uint8_t handoff_flag233_before =
      LooksLikeGuestPointer(render_handoff) ? LoadGuestU8(base, render_handoff + 233)
                                            : 0;
  const uint8_t handoff_flag244_before =
      LooksLikeGuestPointer(render_handoff) ? LoadGuestU8(base, render_handoff + 244)
                                            : 0;

  StoreGuestU8(base, request_guest + 92, 1);
  StoreGuestU8(base, request_guest + 196, 0);
  if (!LooksLikeGuestPointer(request_owner_before) &&
      LooksLikeGuestPointer(primary_owner)) {
    StoreGuestU32(base, request_guest + 132, primary_owner);
  }
  StoreGuestU8(base, kSecondaryReadyGlobal, 0);

  if (LooksLikeGuestPointer(render_handoff)) {
    StoreGuestU8(base, render_handoff + 168, 1);
    StoreGuestU8(base, render_handoff + 244, 1);
  }
  const bool explicit_post_gameface_release_source =
      source_api == "post_gameface_package_handoff_signal";
  const bool completed_ownerless_post_gameface_release_source =
      source_api == "completed_owner_wait_release" &&
      RefiiPostGameFacePrimaryOwnerReady(base);
  const bool completed_loading_owner_ready =
      clear_render_handoff &&
      RefiiPostVfsChainReadyForLoadingOwnerRelease() &&
      (RefiiCompletedLoadingScreenOwner().load(std::memory_order_relaxed) != 0 ||
       explicit_post_gameface_release_source ||
       completed_ownerless_post_gameface_release_source);
  const bool render_owner_handoff_matched =
      (completed_loading_owner_ready || late_after_holder_cleanup_source) &&
      LooksLikeGuestPointer(render_owner) &&
      LooksLikeGuestPointer(render_handoff) &&
      render_owner_handoff_before == render_handoff;
  const bool render_owner_handoff_already_cleared =
      (completed_loading_owner_ready || late_after_holder_cleanup_source) &&
      LooksLikeGuestPointer(render_owner) &&
      LooksLikeGuestPointer(render_handoff) &&
      render_owner_handoff_before == 0;
  const bool render_owner_handoff_cleared =
      render_owner_handoff_matched || render_owner_handoff_already_cleared;
  if (render_owner_handoff_matched) {
    // Before the loading-owner finish, the generated render frame still needs
    // this handoff attached. Once that owner is complete, mirror the native
    // sub_82389210/sub_82389328 teardown path and release owner+4.
    StoreGuestU32(base, render_owner + 4, 0);
  }
  const uint32_t remembered_post_gameface_request =
      RefiiPostGameFacePrimaryOwnerRequest().load(std::memory_order_relaxed);
  const bool final_post_gameface_release_ready =
      explicit_post_gameface_release_source &&
      RefiiPostGameFacePackageReadyForGeneratedRelease() &&
      RefiiMainMenuTraceHasParsedTextureForGeneratedReadyPayload();
  const bool preserve_post_gameface_primary =
      preserve_post_gameface_primary_allowed &&
      !final_post_gameface_release_ready &&
      request_guest == remembered_post_gameface_request &&
      LooksLikeGuestPointer(primary_owner_global_before) &&
      request_owner_before == primary_owner_global_before;
  const bool late_after_holder_secondary_release =
      late_after_holder_cleanup_source && clear_render_handoff &&
      !LooksLikeGuestPointer(primary_owner_global_before) &&
      LooksLikeGuestPointer(secondary_owner_global_before) &&
      RefiiPostVideoFrontendChainConsumed();
  const bool loading_globals_released =
      ((completed_loading_owner_ready && render_owner_handoff_cleared &&
        LooksLikeGuestPointer(primary_owner_global_before)) ||
       late_after_holder_secondary_release) &&
      RefiiPostVideoFrontendChainConsumed() && !preserve_post_gameface_primary;
  const bool secondary_loading_global_released =
      completed_loading_owner_ready && render_owner_handoff_cleared &&
      preserve_post_gameface_primary &&
      LooksLikeGuestPointer(secondary_owner_global_before) &&
      RefiiPostVideoFrontendChainConsumed();
  if (loading_globals_released) {
    // The generated post-loading frame only leaves the loading-screen branch
    // once the active owner globals are no longer live. The Refii VFS chain has
    // already published the native request, parsed the texture, and released
    // the render handoff here, so retire the completed loading owners too.
    StoreGuestU8(base, kPrimaryReadyGlobal, 0);
    StoreGuestU8(base, kSecondaryReadyGlobal, 0);
    StoreGuestU32(base, kPrimaryOwnerGlobal, 0);
    StoreGuestU32(base, kSecondaryOwnerGlobal, 0);
    StoreGuestU32(base, kLoadingPrimaryModeGlobal, 0);
  } else if (secondary_loading_global_released) {
    // Keep the newly published GameFace primary owner alive. Only the secondary
    // ready bit is stale at this point. The secondary object itself is the
    // generated slot wrapper whose +12 points at the render request; clearing it
    // removes the native follow-on frame before it can consume the VFS result.
    StoreGuestU8(base, kSecondaryReadyGlobal, 0);
    StoreGuestU32(base, kLoadingPrimaryModeGlobal, 0);
  }

  bool preserved_primary_finish_tick = false;
  bool preserved_primary_sequence_ready = false;
  uint32_t preserved_primary_sequence_handle = 0;
  const uint32_t remembered_loading_owner =
      RefiiRememberedLoadingScreenOwner().load(std::memory_order_relaxed);
  if (secondary_loading_global_released && preserve_post_gameface_primary &&
      LooksLikeGuestPointer(remembered_loading_owner) &&
      remembered_loading_owner != primary_owner_global_before) {
    preserved_primary_sequence_ready =
        RefiiLoadingScreenSequenceReady(base,
                                        remembered_loading_owner,
                                        &preserved_primary_sequence_handle);
    preserved_primary_finish_tick =
        TryFinishRefiiLoadingScreenOwnerFromCollapsedPipeline(
            source_ctx,
            base,
            remembered_loading_owner,
            "Renderer_BeginScene_PostReady",
            1,
            preserved_primary_sequence_ready,
            preserved_primary_sequence_handle);
    RecordRefiiMainMenuBlockerEvent(
        "post_gameface_preserved_primary_finish",
        fmt::format("source={} primary=0x{:08X} loading_owner=0x{:08X} "
                    "sequence_ready={} sequence_handle=0x{:08X} "
                    "finish_tick={} globals=[{}]",
                    source_api,
                    primary_owner_global_before,
                    remembered_loading_owner,
                    preserved_primary_sequence_ready ? "true" : "false",
                    preserved_primary_sequence_handle,
                    preserved_primary_finish_tick ? "true" : "false",
                    FormatRefiiLoadingScreenSequenceGlobals(base)));
    if ((VfsTracingEnabled() ||
         ShouldLogRepeatedVfsTraceOperation(
             "PostGameFacePreservedPrimaryFinish", 8)) &&
        preserved_primary_finish_tick) {
      REXLOG_INFO("Refii VFS post-GameFace preserved primary finish "
                  "source={} primary=0x{:08X} loading_owner=0x{:08X} "
                  "sequence_ready={} sequence_handle=0x{:08X} "
                  "finish_tick={} globals=[{}] handoff=[{}] main_menu=[{}]",
                  source_api,
                  primary_owner_global_before,
                  remembered_loading_owner,
                  preserved_primary_sequence_ready ? "true" : "false",
                  preserved_primary_sequence_handle,
                  preserved_primary_finish_tick ? "true" : "false",
                  FormatRefiiLoadingScreenSequenceGlobals(base),
                  FormatRefiiPostVideoHandoffTraceForLog(),
                  FormatRefiiMainMenuBlockerTraceForLog());
    }
  }

  const uint32_t render_owner_handoff_after =
      LooksLikeGuestPointer(render_owner) ? LoadGuestU32(base, render_owner + 4)
                                          : 0;
  uint32_t final_wake_signal_handle = 0;
  uint32_t final_wake_wait_handle = 0;
  uint32_t final_wake_trace_lr = 0;
  uint32_t final_wake_trace_result = 0;
  int64_t final_wake_trace_wait_ms = -1;
  bool final_wake_signaled = false;
  auto looks_like_private_event_handle = [](uint32_t handle) {
    return (handle & 0xFF000000u) == 0xF8000000u;
  };
  if (clear_render_handoff &&
      (loading_globals_released || render_owner_handoff_cleared)) {
    if (RefiiPostGameFacePackageReadyForGeneratedRelease() &&
        RefiiMainMenuTraceHasParsedTextureForGeneratedReadyPayload()) {
      TryReleaseRefiiCompletedNativeVfsFinalizerExit(
          source_ctx, base, source_api, VfsTracingEnabled());
    }
    RefiiPostLoadingFrontendFinalHandoffWakeSeen().store(
        true, std::memory_order_relaxed);
    if (LooksLikeGuestPointer(render_handoff)) {
      final_wake_signal_handle = LoadGuestU32(base, render_handoff + 456);
      final_wake_wait_handle = LoadGuestU32(base, render_handoff + 460 + 128);
    }
    {
      std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
      const auto& trace = RefiiPostVideoHandoffTraceState();
      if (!looks_like_private_event_handle(final_wake_signal_handle)) {
        final_wake_signal_handle = trace.last_wait_signal_handle;
      }
      if (!looks_like_private_event_handle(final_wake_wait_handle)) {
        final_wake_wait_handle = trace.last_wait_handle;
      }
      final_wake_trace_lr = trace.last_wait_lr;
      final_wake_trace_result = trace.last_wait_result;
      final_wake_trace_wait_ms = trace.last_wait_ms;
    }
    static std::atomic<uint32_t> last_final_wake_handle{0};
    const uint32_t final_wake_key =
        final_wake_signal_handle != 0 ? final_wake_signal_handle
                                      : final_wake_wait_handle;
    if (final_wake_key != 0 &&
        last_final_wake_handle.exchange(final_wake_key,
                                        std::memory_order_relaxed) !=
            final_wake_key) {
      PPCContext signal_ctx = source_ctx;
      if (looks_like_private_event_handle(final_wake_signal_handle)) {
        signal_ctx.r3.u32 = final_wake_signal_handle;
        ::__imp__sub_822C9388(signal_ctx, base);
        final_wake_signaled = true;
      }
      PPCContext wait_signal_ctx = source_ctx;
      if (looks_like_private_event_handle(final_wake_wait_handle) &&
          final_wake_wait_handle != final_wake_signal_handle) {
        wait_signal_ctx.r3.u32 = final_wake_wait_handle;
        ::__imp__sub_822C9388(wait_signal_ctx, base);
        final_wake_signaled = true;
      }
      RecordRefiiMainMenuBlockerEvent(
          "post_loading_frontend_final_handoff_wake",
          fmt::format("source={} request=0x{:08X} render_owner=0x{:08X} "
                      "render_handoff=0x{:08X} signal_handle=0x{:08X} "
                      "wait_handle=0x{:08X} trace_wait_lr=0x{:08X} "
                      "trace_wait_result=0x{:08X} trace_wait_ms={} "
                      "signaled={} signal_result=0x{:08X} "
                      "wait_signal_result=0x{:08X}",
                      source_api,
                      request_guest,
                      render_owner,
                      render_handoff,
                      final_wake_signal_handle,
                      final_wake_wait_handle,
                      final_wake_trace_lr,
                      final_wake_trace_result,
                      final_wake_trace_wait_ms,
                      final_wake_signaled ? "true" : "false",
                      signal_ctx.r3.u32,
                      wait_signal_ctx.r3.u32));
      if (VfsTracingEnabled() ||
          ShouldLogRepeatedVfsTraceOperation(
              "PostLoadingFrontendFinalHandoffWake", 8)) {
        REXLOG_INFO("Refii VFS post-loading frontend final handoff wake "
                    "source={} request=0x{:08X} render_owner=0x{:08X} "
                    "render_handoff=0x{:08X} signal_handle=0x{:08X} "
                    "wait_handle=0x{:08X} trace_wait_lr=0x{:08X} "
                    "trace_wait_result=0x{:08X} trace_wait_ms={} "
                    "signaled={} signal_result=0x{:08X} "
                    "wait_signal_result=0x{:08X} handoff=[{}] "
                    "main_menu=[{}]",
                    source_api,
                    request_guest,
                    render_owner,
                    render_handoff,
                    final_wake_signal_handle,
                    final_wake_wait_handle,
                    final_wake_trace_lr,
                    final_wake_trace_result,
                    final_wake_trace_wait_ms,
                    final_wake_signaled ? "true" : "false",
                    signal_ctx.r3.u32,
                    wait_signal_ctx.r3.u32,
                    FormatRefiiPostVideoHandoffTraceForLog(),
                    FormatRefiiMainMenuBlockerTraceForLog());
      }
    }
  }

  // The synthetic after-holder arm/collapse machinery was removed; these
  // stay for the finalize trace fields below and truthfully report inactive.
  const uint32_t post_loading_after_holder_guest = 0;
  const bool post_loading_after_holder_returned = false;
  const bool post_loading_after_holder_armed = false;
  const bool post_loading_after_holder_mode2_collapsed = false;
  const bool post_loading_after_holder_native_tick = false;

  RecordRefiiMainMenuBlockerEvent(
      "post_loading_frontend_generated_state_finalize",
      fmt::format("source={} request=0x{:08X} primary=0x{:08X} "
                  "secondary=0x{:08X} render_owner=0x{:08X} "
                  "render_handoff=0x{:08X} clear_render_handoff={} "
                  "first_finalize={} secondary_ready=0x{:02X}->0x{:02X} "
                  "request_done=0x{:02X}->0x{:02X} "
                  "request_owner=0x{:08X}->0x{:08X} "
                  "request_flag196=0x{:02X}->0x{:02X} "
                  "owner_plus4=0x{:08X}->0x{:08X} "
                  "primary_global=0x{:08X}/0x{:02X}->0x{:08X}/0x{:02X} "
                  "secondary_global=0x{:08X}/0x{:02X}->0x{:08X}/0x{:02X} "
                  "loading_mode=0x{:08X}->0x{:08X} "
                  "handoff_complete=0x{:02X}->0x{:02X} "
                  "handoff_dirty=0x{:02X}->0x{:02X} "
                  "handoff_flags=0x{:02X}/0x{:02X}->0x{:02X}/0x{:02X} "
                  "render_owner_handoff_cleared={} "
                  "loading_globals_released={} "
                  "secondary_loading_global_released={} "
                  "post_gameface_primary_preserved={} "
                  "after_holder=0x{:08X} after_holder_returned={} "
                  "after_holder_armed={} after_holder_mode2_collapsed={} "
                  "after_holder_native_tick={} "
                  "globals=[{}]",
                  source_api,
                  request_guest,
                  primary_owner,
                  secondary_owner,
                  render_owner,
                  render_handoff,
                  clear_render_handoff ? "true" : "false",
                  first_finalize ? "true" : "false",
                  secondary_ready_before,
                  LoadGuestU8(base, kSecondaryReadyGlobal),
                  request_done_before,
                  LoadGuestU8(base, request_guest + 92),
                  request_owner_before,
                  LoadGuestU32(base, request_guest + 132),
                  request_flag196_before,
                  LoadGuestU8(base, request_guest + 196),
                  render_owner_handoff_before,
                  render_owner_handoff_after,
                  primary_owner_global_before,
                  primary_ready_before,
                  LoadGuestU32(base, kPrimaryOwnerGlobal),
                  LoadGuestU8(base, kPrimaryReadyGlobal),
                  secondary_owner_global_before,
                  secondary_ready_before,
                  LoadGuestU32(base, kSecondaryOwnerGlobal),
                  LoadGuestU8(base, kSecondaryReadyGlobal),
                  loading_mode_before,
                  LoadGuestU32(base, kLoadingPrimaryModeGlobal),
                  handoff_complete_before,
                  LooksLikeGuestPointer(render_handoff)
                      ? LoadGuestU8(base, render_handoff + 168)
                      : 0,
                  handoff_dirty_before,
                  LooksLikeGuestPointer(render_handoff)
                      ? LoadGuestU8(base, render_handoff + 132)
                      : 0,
                  handoff_flag233_before,
                  handoff_flag244_before,
                  LooksLikeGuestPointer(render_handoff)
                      ? LoadGuestU8(base, render_handoff + 233)
                      : 0,
                  LooksLikeGuestPointer(render_handoff)
                      ? LoadGuestU8(base, render_handoff + 244)
                      : 0,
                  render_owner_handoff_cleared ? "true" : "false",
                  loading_globals_released ? "true" : "false",
                  secondary_loading_global_released ? "true" : "false",
                  preserve_post_gameface_primary ? "true" : "false",
                  post_loading_after_holder_guest,
                  post_loading_after_holder_returned ? "true" : "false",
                  post_loading_after_holder_armed ? "true" : "false",
                  post_loading_after_holder_mode2_collapsed ? "true" : "false",
                  post_loading_after_holder_native_tick ? "true" : "false",
                  FormatRefiiLoadingScreenSequenceGlobals(base)));

  if (VfsTracingEnabled() ||
      ShouldLogRepeatedVfsTraceOperation(
          "PostLoadingFrontendGeneratedStateFinalize", 8)) {
    REXLOG_INFO("Refii VFS post-loading frontend generated state finalized "
                "source={} request=0x{:08X} primary=0x{:08X} "
                "secondary=0x{:08X} render_owner=0x{:08X} "
                "render_handoff=0x{:08X} clear_render_handoff={} "
                "first_finalize={} secondary_ready=0x{:02X}->0x{:02X} "
                "request_done=0x{:02X}->0x{:02X} "
                "request_owner=0x{:08X}->0x{:08X} "
                "request_flag196=0x{:02X}->0x{:02X} "
                "owner_plus4=0x{:08X}->0x{:08X} "
                "primary_global=0x{:08X}/0x{:02X}->0x{:08X}/0x{:02X} "
                "secondary_global=0x{:08X}/0x{:02X}->0x{:08X}/0x{:02X} "
                "loading_mode=0x{:08X}->0x{:08X} "
                "handoff_complete=0x{:02X}->0x{:02X} "
                "handoff_flags=0x{:02X}/0x{:02X}->0x{:02X}/0x{:02X} "
                "render_owner_handoff_cleared={} "
                "loading_globals_released={} "
                "secondary_loading_global_released={} "
                "post_gameface_primary_preserved={} "
                "after_holder=0x{:08X} after_holder_returned={} "
                "after_holder_armed={} after_holder_mode2_collapsed={} "
                "after_holder_native_tick={} "
                "globals=[{}] handoff=[{}] main_menu=[{}]",
                source_api,
                request_guest,
                primary_owner,
                secondary_owner,
                render_owner,
                render_handoff,
                clear_render_handoff ? "true" : "false",
                first_finalize ? "true" : "false",
                secondary_ready_before,
                LoadGuestU8(base, kSecondaryReadyGlobal),
                request_done_before,
                LoadGuestU8(base, request_guest + 92),
                request_owner_before,
                LoadGuestU32(base, request_guest + 132),
                request_flag196_before,
                LoadGuestU8(base, request_guest + 196),
                render_owner_handoff_before,
                render_owner_handoff_after,
                primary_owner_global_before,
                primary_ready_before,
                LoadGuestU32(base, kPrimaryOwnerGlobal),
                LoadGuestU8(base, kPrimaryReadyGlobal),
                secondary_owner_global_before,
                secondary_ready_before,
                LoadGuestU32(base, kSecondaryOwnerGlobal),
                LoadGuestU8(base, kSecondaryReadyGlobal),
                loading_mode_before,
                LoadGuestU32(base, kLoadingPrimaryModeGlobal),
                handoff_complete_before,
                LooksLikeGuestPointer(render_handoff)
                    ? LoadGuestU8(base, render_handoff + 168)
                    : 0,
                handoff_flag233_before,
                handoff_flag244_before,
                LooksLikeGuestPointer(render_handoff)
                    ? LoadGuestU8(base, render_handoff + 233)
                    : 0,
                LooksLikeGuestPointer(render_handoff)
                    ? LoadGuestU8(base, render_handoff + 244)
                    : 0,
                render_owner_handoff_cleared ? "true" : "false",
                loading_globals_released ? "true" : "false",
                secondary_loading_global_released ? "true" : "false",
                preserve_post_gameface_primary ? "true" : "false",
                post_loading_after_holder_guest,
                post_loading_after_holder_returned ? "true" : "false",
                post_loading_after_holder_armed ? "true" : "false",
                post_loading_after_holder_mode2_collapsed ? "true" : "false",
                post_loading_after_holder_native_tick ? "true" : "false",
                FormatRefiiLoadingScreenSequenceGlobals(base),
                FormatRefiiPostVideoHandoffTraceForLog(),
                FormatRefiiMainMenuBlockerTraceForLog());
  }

  LogRefiiVfsChainVerdict("frontend_post_video",
                          static_cast<uint32_t>(source_ctx.lr),
                          "post_loading_frontend_render_request",
                          "",
                          "",
                          "native_vfs_generated_final_state",
                          clear_render_handoff
                              ? "post_loading_generated_handoff_released"
                              : "post_loading_generated_state_ready",
                          "Opened",
                          clear_render_handoff ? "native_owner_release"
                                               : "native_frame_observe");
  return true;
}

bool TryRunRefiiPostLoadingFrontendRenderRequestNativeFromTrace(
    const PPCContext& source_ctx,
    uint8_t* base,
    std::string_view source_api) {
  auto log_skip = [&](std::string_view reason,
                      uint32_t primary_owner = 0,
                      uint8_t primary_ready = 0,
                      uint32_t secondary_owner = 0,
                      uint32_t request_guest = 0,
                      uint32_t slot_object_guest = 0) {
    if (VfsTracingEnabled() ||
        ShouldLogRepeatedVfsTraceOperation(
            "PostLoadingFrontendRenderNativeRunSkipped", 8)) {
      REXLOG_INFO("Refii VFS post-loading frontend render native run skipped "
                  "source={} reason={} primary=0x{:08X}/{} "
                  "secondary=0x{:08X} request=0x{:08X} "
                  "remembered_request=0x{:08X} slot_object=0x{:08X} "
                  "request_state=[{}] globals=[{}] handoff=[{}] "
                  "main_menu=[{}]",
                  source_api,
                  reason,
                  primary_owner,
                  primary_ready,
                  secondary_owner,
                  request_guest,
                  RefiiPostGameFacePrimaryOwnerRequest().load(
                      std::memory_order_relaxed),
                  slot_object_guest,
                  FormatNativeVfsAsyncRequestState(base, request_guest),
                  FormatRefiiLoadingScreenSequenceGlobals(base),
                  FormatRefiiPostVideoHandoffTraceForLog(),
                  FormatRefiiMainMenuBlockerTraceForLog());
    }
  };

  if (base == nullptr || !RefiiLooseAssetBackendEnabled() ||
      !RefiiPostGameFacePackageHandoffSignalEmitted() ||
      !RefiiStartupVideosReadyForMenuHandoff()) {
    log_skip("gate_not_ready");
    return false;
  }

  constexpr uint32_t kLoadingGlobalBase = 0x83490000u;
  constexpr uint32_t kPrimaryOwnerGlobal = kLoadingGlobalBase + 27320u;
  constexpr uint32_t kPrimaryReadyGlobal = kLoadingGlobalBase + 27230u;
  constexpr uint32_t kSecondaryOwnerGlobal = kLoadingGlobalBase + 27328u;
  constexpr uint32_t kSecondaryReadyGlobal = kLoadingGlobalBase + 27332u;

  const uint32_t primary_owner = LoadGuestU32(base, kPrimaryOwnerGlobal);
  const uint8_t primary_ready = LoadGuestU8(base, kPrimaryReadyGlobal);
  const uint32_t secondary_owner = LoadGuestU32(base, kSecondaryOwnerGlobal);
  const uint8_t secondary_ready = LoadGuestU8(base, kSecondaryReadyGlobal);
  uint32_t request_guest =
      LooksLikeGuestPointer(secondary_owner)
          ? LoadGuestU32(base, secondary_owner + 12)
          : 0;
  if (!IsNativeFrontendRenderRequest(base, request_guest)) {
    std::lock_guard<std::mutex> lock(RefiiPostGameFacePrimaryOwnerMutex());
    for (const uint32_t published_request :
         RefiiPostGameFacePrimaryOwnerRequests()) {
      if (IsNativeFrontendRenderRequest(base, published_request)) {
        request_guest = published_request;
        break;
      }
    }
  }
  if (!IsNativeFrontendRenderRequest(base, request_guest)) {
    const uint32_t remembered_request =
        RefiiPostGameFacePrimaryOwnerRequest().load(std::memory_order_relaxed);
    if (IsNativeFrontendRenderRequest(base, remembered_request)) {
      request_guest = remembered_request;
    }
  }

  const uint32_t slot_object_guest =
      ResolveRefiiPostGameFacePrimaryOwnerSlotObject(base,
                                                     request_guest,
                                                     secondary_owner);
  if (source_api == "completed_owner_wait_release" &&
      RefiiCompletedLoadingScreenOwner().load(std::memory_order_relaxed) == 0) {
    log_skip("loading_owner_not_completed",
             primary_owner,
             primary_ready,
             secondary_owner,
             request_guest,
             slot_object_guest);
    return false;
  }
  const bool can_repair_primary_ready =
      primary_ready == 0 && secondary_ready != 0 &&
      LooksLikeGuestPointer(primary_owner) &&
      IsNativeFrontendRenderRequest(base, request_guest) &&
      LooksLikeGuestPointer(slot_object_guest) &&
      LoadGuestU32(base, slot_object_guest + 12) == request_guest &&
      RefiiPostVideoFrontendChainConsumed() &&
      RefiiCompletedLoadingScreenOwner().load(std::memory_order_relaxed) != 0;
  if (can_repair_primary_ready) {
    StoreGuestU8(base, kPrimaryReadyGlobal, 1);
    RecordRefiiMainMenuBlockerEvent(
        "post_loading_frontend_render_primary_ready_repair",
        fmt::format("source={} request=0x{:08X} slot_object=0x{:08X} "
                    "primary=0x{:08X}/{} secondary=0x{:08X}/{} "
                    "globals=[{}]",
                    source_api,
                    request_guest,
                    slot_object_guest,
                    primary_owner,
                    primary_ready,
                    secondary_owner,
                    secondary_ready,
                    FormatRefiiLoadingScreenSequenceGlobals(base)));
    if (VfsTracingEnabled() ||
        ShouldLogRepeatedVfsTraceOperation(
            "PostLoadingFrontendRenderPrimaryReadyRepair", 8)) {
      REXLOG_INFO("Refii VFS post-loading frontend render primary ready "
                  "repaired source={} request=0x{:08X} slot_object=0x{:08X} "
                  "primary=0x{:08X}/{}->{} secondary=0x{:08X}/{} "
                  "handoff=[{}] main_menu=[{}]",
                  source_api,
                  request_guest,
                  slot_object_guest,
                  primary_owner,
                  primary_ready,
                  LoadGuestU8(base, kPrimaryReadyGlobal),
                  secondary_owner,
                  secondary_ready,
                  FormatRefiiPostVideoHandoffTraceForLog(),
                  FormatRefiiMainMenuBlockerTraceForLog());
    }
  }
  const uint8_t effective_primary_ready = LoadGuestU8(base, kPrimaryReadyGlobal);
  uint32_t ownerless_render_owner = 0;
  uint32_t ownerless_render_handoff = 0;
  uint32_t ownerless_source124 = 0xFFFFFFFFu;
  uint32_t ownerless_source128 = 0xFFFFFFFFu;
  double ownerless_frame_time = 0.0;
  int64_t ownerless_schedule_age_ms = -1;
  bool ownerless_request_ready = false;
  const bool post_gameface_package_handoff_source =
      source_api == "post_gameface_package_handoff_signal";
  const bool primary_ready_ownerless_seed =
      (post_gameface_package_handoff_source ||
       source_api == "completed_owner_wait_release") &&
      effective_primary_ready != 0 &&
      RefiiPostGameFacePrimaryOwnerReady(base);
  const bool ownerless_trace_can_seed_request =
      (RefiiOwnerlessPostVideoAfterHolderCleaned() ||
       RefiiPostVfsChainReadyForLoadingOwnerRelease()) &&
      (secondary_ready != 0 || primary_ready_ownerless_seed) &&
      RefiiPostVideoFrontendChainConsumed();
  if (ownerless_trace_can_seed_request &&
      IsNativeFrontendRenderRequest(base, request_guest) &&
      LooksLikeGuestPointer(slot_object_guest) &&
      LoadGuestU32(base, slot_object_guest + 12) == request_guest) {
    const uint32_t request_render_owner = LoadGuestU32(base, request_guest + 24);
    uint32_t request_render_handoff =
        LooksLikeGuestPointer(request_render_owner)
            ? LoadGuestU32(base, request_render_owner + 4)
            : 0;
    const uint32_t request_direct_handoff =
        LoadGuestU32(base, request_guest + 40);
    if (!LooksLikeGuestPointer(request_render_handoff) &&
        LooksLikeGuestPointer(request_direct_handoff)) {
      request_render_handoff = request_direct_handoff;
    }
    const uint32_t request_render_source =
        LoadGuestU32(base, request_guest + 28);
    const uint32_t request_render_head =
        LooksLikeGuestPointer(request_render_source)
            ? LoadGuestU32(base, request_render_source + 4)
            : 0;
    const uint32_t request_source128 =
        LooksLikeGuestPointer(request_render_head)
            ? LoadGuestU32(base, request_render_head)
            : 0xFFFFFFFFu;
    const uint32_t request_source124 =
        LooksLikeGuestPointer(request_render_head)
            ? LoadGuestU32(base, request_render_head + 8)
            : 0xFFFFFFFFu;
    if (LooksLikeGuestPointer(request_render_owner) &&
        LooksLikeGuestPointer(request_render_handoff) &&
        LooksLikeGuestPointer(request_render_head) &&
        request_source124 != 0xFFFFFFFFu &&
        request_source128 != 0xFFFFFFFFu) {
      ownerless_render_owner = request_render_owner;
      ownerless_render_handoff = request_render_handoff;
      ownerless_source124 = request_source124;
      ownerless_source128 = request_source128;
      ownerless_frame_time =
          RefiiGuestF64FromWord(LoadGuestU64(base, request_guest + 80));
      ownerless_request_ready = true;
    }
  }
  if (!ownerless_request_ready && ownerless_trace_can_seed_request &&
      IsNativeFrontendRenderRequest(base, request_guest) &&
      LooksLikeGuestPointer(slot_object_guest) &&
      LoadGuestU32(base, slot_object_guest + 12) == request_guest) {
    {
      std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
      const auto& trace = RefiiPostVideoHandoffTraceState();
      ownerless_render_owner = trace.last_nonidle_render_schedule_owner;
      ownerless_render_handoff = trace.last_nonidle_render_schedule_handoff;
      ownerless_source124 = trace.last_nonidle_render_schedule_source124;
      ownerless_source128 = trace.last_nonidle_render_schedule_source128;
      ownerless_frame_time = trace.last_nonidle_render_schedule_frame_time;
      if (trace.last_nonidle_render_schedule_at.time_since_epoch().count() !=
          0) {
        ownerless_schedule_age_ms =
            std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() -
                trace.last_nonidle_render_schedule_at)
                .count();
      }
    }
    ownerless_request_ready =
        LooksLikeGuestPointer(ownerless_render_owner) &&
        LooksLikeGuestPointer(ownerless_render_handoff) &&
        LoadGuestU32(base, ownerless_render_owner + 4) ==
            ownerless_render_handoff &&
        ownerless_source124 != 0xFFFFFFFFu &&
        ownerless_source128 != 0xFFFFFFFFu &&
        (ownerless_schedule_age_ms < 0 ||
         ownerless_schedule_age_ms <= 180000);
  }
  if (!ownerless_request_ready &&
      source_api == "completed_owner_wait_release" &&
      IsNativeFrontendRenderRequest(base, request_guest) &&
      LooksLikeGuestPointer(slot_object_guest) &&
      LoadGuestU32(base, slot_object_guest + 12) == request_guest &&
      RefiiPostVideoFrontendChainConsumed() &&
      RefiiPostVfsChainReadyForLoadingOwnerRelease()) {
    uint32_t deferred_owner = 0;
    uint32_t deferred_handoff = 0;
    uint32_t deferred_source124 = 0xFFFFFFFFu;
    uint32_t deferred_source128 = 0xFFFFFFFFu;
    double deferred_frame_time = 0.0;
    int64_t deferred_age_ms = -1;
    bool deferred_trace_matches = false;
    {
      std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
      const auto& trace = RefiiPostVideoHandoffTraceState();
      deferred_owner = trace.post_frontend_deferred_hash_wake_signal_owner;
      deferred_handoff = trace.post_frontend_deferred_hash_wake_signal_handoff;
      deferred_trace_matches =
          trace.post_frontend_deferred_hash_wake_signal_emitted &&
          trace.last_nonidle_render_schedule_owner == deferred_owner &&
          trace.last_nonidle_render_schedule_handoff == deferred_handoff &&
          trace.last_nonidle_render_schedule_source124 != 0xFFFFFFFFu &&
          trace.last_nonidle_render_schedule_source128 != 0xFFFFFFFFu;
      if (deferred_trace_matches) {
        deferred_source124 = trace.last_nonidle_render_schedule_source124;
        deferred_source128 = trace.last_nonidle_render_schedule_source128;
        deferred_frame_time = trace.last_nonidle_render_schedule_frame_time;
        if (trace.post_frontend_deferred_hash_wake_signal_at
                .time_since_epoch()
                .count() != 0) {
          deferred_age_ms =
              std::chrono::duration_cast<std::chrono::milliseconds>(
                  std::chrono::steady_clock::now() -
                  trace.post_frontend_deferred_hash_wake_signal_at)
                  .count();
        }
      }
    }
    ownerless_request_ready =
        deferred_trace_matches &&
        LooksLikeGuestPointer(deferred_owner) &&
        LooksLikeGuestPointer(deferred_handoff) &&
        LoadGuestU32(base, deferred_owner + 4) == deferred_handoff &&
        (deferred_age_ms < 0 || deferred_age_ms <= 180000);
    if (ownerless_request_ready) {
      ownerless_render_owner = deferred_owner;
      ownerless_render_handoff = deferred_handoff;
      ownerless_source124 = deferred_source124;
      ownerless_source128 = deferred_source128;
      ownerless_frame_time = deferred_frame_time;
      ownerless_schedule_age_ms = deferred_age_ms;
      RecordRefiiMainMenuBlockerEvent(
          "post_loading_frontend_render_deferred_wake_seed",
          fmt::format("source={} request=0x{:08X} slot_object=0x{:08X} "
                      "owner=0x{:08X} handoff=0x{:08X} "
                      "source_pair=0x{:08X}/0x{:08X} age_ms={} "
                      "request_state=[{}] handoff=[{}]",
                      source_api,
                      request_guest,
                      slot_object_guest,
                      ownerless_render_owner,
                      ownerless_render_handoff,
                      ownerless_source124,
                      ownerless_source128,
                      ownerless_schedule_age_ms,
                      FormatNativeVfsAsyncRequestState(base, request_guest),
                      FormatRefiiPostVideoHandoffTraceForLog()));
      if (VfsTracingEnabled() ||
          ShouldLogRepeatedVfsTraceOperation(
              "PostLoadingFrontendRenderDeferredWakeSeed", 8)) {
        REXLOG_INFO("Refii VFS post-loading frontend render deferred wake "
                    "seed source={} request=0x{:08X} slot_object=0x{:08X} "
                    "owner=0x{:08X} handoff=0x{:08X} "
                    "source_pair=0x{:08X}/0x{:08X} age_ms={} "
                    "main_menu=[{}]",
                    source_api,
                    request_guest,
                    slot_object_guest,
                    ownerless_render_owner,
                    ownerless_render_handoff,
                    ownerless_source124,
                    ownerless_source128,
                    ownerless_schedule_age_ms,
                    FormatRefiiMainMenuBlockerTraceForLog());
      }
    }
  }
  if (!ownerless_request_ready &&
      source_api == "post_gameface_package_handoff_signal") {
    log_skip("deferred_render_source_missing",
             primary_owner,
             effective_primary_ready,
             secondary_owner,
             request_guest,
             slot_object_guest);
    return false;
  }
  if (!ownerless_request_ready &&
      source_api == "completed_owner_wait_release" &&
      IsNativeFrontendRenderRequest(base, request_guest) &&
      LooksLikeGuestPointer(slot_object_guest) &&
      LoadGuestU32(base, slot_object_guest + 12) == request_guest &&
      RefiiPostVideoFrontendChainConsumed() &&
      RefiiPostVfsChainReadyForLoadingOwnerRelease()) {
    const uint8_t request_done = LoadGuestU8(base, request_guest + 92);
    const uint32_t request_owner = LoadGuestU32(base, request_guest + 132);
    const uint32_t request_handoff = LoadGuestU32(base, request_guest + 40);
    const bool already_recorded = [&]() {
      std::lock_guard<std::mutex> lock(RefiiInlineFrontendRenderRequestMutex());
      return RefiiPostLoadingNativeFrontendRenderRuns().contains(
                 request_guest) ||
             RefiiPostLoadingFrontendRenderCommitRuns().contains(
                 request_guest);
    }();
    if (already_recorded || (request_done != 0 &&
                             LooksLikeGuestPointer(request_owner) &&
                             LooksLikeGuestPointer(request_handoff))) {
      RecordRefiiMainMenuBlockerEvent(
          "post_loading_frontend_render_already_completed",
          fmt::format("source={} request=0x{:08X} slot_object=0x{:08X} "
                      "recorded={} request_done=0x{:02X} "
                      "request_owner=0x{:08X} request_handoff=0x{:08X} "
                      "globals=[{}]",
                      source_api,
                      request_guest,
                      slot_object_guest,
                      already_recorded ? "true" : "false",
                      request_done,
                      request_owner,
                      request_handoff,
                      FormatRefiiLoadingScreenSequenceGlobals(base)));
      if (VfsTracingEnabled() ||
          ShouldLogRepeatedVfsTraceOperation(
              "PostLoadingFrontendRenderAlreadyCompleted", 8)) {
        REXLOG_INFO("Refii VFS post-loading frontend render already completed "
                    "source={} request=0x{:08X} slot_object=0x{:08X} "
                    "recorded={} request_done=0x{:02X} "
                    "request_owner=0x{:08X} request_handoff=0x{:08X} "
                    "handoff=[{}] main_menu=[{}]",
                    source_api,
                    request_guest,
                    slot_object_guest,
                    already_recorded ? "true" : "false",
                    request_done,
                    request_owner,
                    request_handoff,
                    FormatRefiiPostVideoHandoffTraceForLog(),
                    FormatRefiiMainMenuBlockerTraceForLog());
      }
      return true;
    }
  }
  if (!ownerless_request_ready &&
      source_api == "completed_owner_wait_release" &&
      IsNativeFrontendRenderRequest(base, request_guest) &&
      LooksLikeGuestPointer(slot_object_guest) &&
      LoadGuestU32(base, slot_object_guest + 12) == request_guest &&
      RefiiPostVideoFrontendChainConsumed() &&
      RefiiPostVfsChainReadyForLoadingOwnerRelease()) {
    log_skip("completed_render_source_missing",
             primary_owner,
             effective_primary_ready,
             secondary_owner,
             request_guest,
             slot_object_guest);
    return false;
  }
	  if (!ownerless_request_ready &&
	      (!LooksLikeGuestPointer(primary_owner) || effective_primary_ready == 0 ||
	      !IsNativeFrontendRenderRequest(base, request_guest) ||
	      !LooksLikeGuestPointer(slot_object_guest) ||
	      LoadGuestU32(base, slot_object_guest + 12) != request_guest)) {
	    log_skip(!LooksLikeGuestPointer(primary_owner) ? "primary_missing"
	             : effective_primary_ready == 0 ? "primary_not_ready"
	             : !IsNativeFrontendRenderRequest(base, request_guest)
	                 ? "request_missing"
             : !LooksLikeGuestPointer(slot_object_guest) ? "slot_missing"
             : "slot_request_mismatch",
             primary_owner,
             effective_primary_ready,
             secondary_owner,
             request_guest,
             slot_object_guest);
    return false;
  }

  {
    std::lock_guard<std::mutex> lock(RefiiInlineFrontendRenderRequestMutex());
    const bool post_loading_inserted =
        RefiiPostLoadingNativeFrontendRenderRuns().insert(request_guest).second;
    const bool commit_inserted =
        RefiiPostLoadingFrontendRenderCommitRuns().insert(request_guest).second;
    const bool completed_renderer_reentry_inserted =
        source_api == "completed_loading_owner_renderer_reentry" &&
        RefiiPostLoadingCompletedRendererReentryRuns()
            .insert(request_guest)
            .second;
    if (!post_loading_inserted && !commit_inserted &&
        !completed_renderer_reentry_inserted) {
      log_skip("already_completed",
               primary_owner,
               primary_ready,
               secondary_owner,
               request_guest,
               slot_object_guest);
      return true;
    }
  }

  const std::string before_state =
      FormatNativeVfsAsyncRequestState(base, request_guest);
  const uint8_t request_done_before = LoadGuestU8(base, request_guest + 92);
  const uint32_t request_owner_before = LoadGuestU32(base, request_guest + 132);
  const uint8_t secondary_ready_before = LoadGuestU8(base, kSecondaryReadyGlobal);
  const uint32_t render_owner_before = LoadGuestU32(base, request_guest + 24);
  const uint32_t render_source_before = LoadGuestU32(base, request_guest + 28);
  const uint8_t render_flag196_before = LoadGuestU8(base, request_guest + 196);
  uint32_t render_handoff_before =
      LooksLikeGuestPointer(render_owner_before)
          ? LoadGuestU32(base, render_owner_before + 4)
          : 0;
  const uint32_t request_handoff_before = LoadGuestU32(base, request_guest + 40);
  if (!LooksLikeGuestPointer(render_handoff_before) &&
      LooksLikeGuestPointer(request_handoff_before)) {
    render_handoff_before = request_handoff_before;
  }
  const uint32_t render_source_head =
      LooksLikeGuestPointer(render_source_before)
          ? LoadGuestU32(base, render_source_before + 4)
          : 0;
  const uint32_t render_source128 =
      LooksLikeGuestPointer(render_source_head)
          ? LoadGuestU32(base, render_source_head)
          : 0xFFFFFFFFu;
  const uint32_t render_source124 =
      LooksLikeGuestPointer(render_source_head)
          ? LoadGuestU32(base, render_source_head + 8)
          : 0xFFFFFFFFu;
	  const uint64_t render_frame_time_word = LoadGuestU64(base, request_guest + 80);
	  const double render_frame_time =
	      RefiiGuestF64FromWord(render_frame_time_word);
	  uint32_t effective_render_owner = render_owner_before;
	  uint32_t effective_render_handoff = render_handoff_before;
	  uint32_t effective_render_source124 = render_source124;
	  uint32_t effective_render_source128 = render_source128;
	  uint64_t effective_render_frame_time_word = render_frame_time_word;
	  double effective_render_frame_time = render_frame_time;
	  const bool ownerless_trace_source_used =
	      ownerless_request_ready &&
	      (!LooksLikeGuestPointer(effective_render_owner) ||
	       !LooksLikeGuestPointer(effective_render_handoff) ||
	       !LooksLikeGuestPointer(render_source_head) ||
	       effective_render_source124 == 0xFFFFFFFFu ||
	       effective_render_source128 == 0xFFFFFFFFu);
	  if (ownerless_trace_source_used) {
	    effective_render_owner = ownerless_render_owner;
	    effective_render_handoff = ownerless_render_handoff;
	    effective_render_source124 = ownerless_source124;
	    effective_render_source128 = ownerless_source128;
	    effective_render_frame_time = ownerless_frame_time;
	    effective_render_frame_time_word = RefiiGuestF64ToWord(ownerless_frame_time);
	    StoreGuestU32(base, request_guest + 24, effective_render_owner);
	    StoreGuestU32(base, request_guest + 40, effective_render_handoff);
	    StoreGuestU64(base, request_guest + 80, effective_render_frame_time_word);
	  }
	  uint8_t render_handoff_done_before = 0;
	  uint8_t render_handoff_done_after = 0;
	  uint32_t render_handoff_source124_after = 0;
	  uint32_t render_handoff_source128_after = 0;
	  if (LooksLikeGuestPointer(effective_render_handoff)) {
	    render_handoff_done_before =
	        LoadGuestU8(base, effective_render_handoff + 168);
	  }

  uint32_t commit_result = 0;
  bool commit_slot_ran = false;
  bool generated_producer_slot_ran = false;
  uint32_t generated_producer_slot_result = 0;
  int64_t generated_producer_slot_ms = -1;
  bool producer_completion_ran = false;
  bool post_gameface_frame_attach_from_handoff = false;
		  if (LooksLikeGuestPointer(effective_render_owner) &&
		      LooksLikeGuestPointer(effective_render_handoff) &&
		      effective_render_source124 != 0xFFFFFFFFu &&
	      effective_render_source128 != 0xFFFFFFFFu) {
	    // Generated sub_8231AAD0 reaches this exact producer scheduling contract
	    // after its ready-object poll. Calling the whole generated slot from the
	    // VFS worker can block behind the owner teardown path, so keep the VFS
	    // bridge to the native producer shape: handoff+160 frame time and
	    // handoff+128/+124 source pair. Completion is owned by
	    // TryCompleteRefiiPostLoadingFrontendRenderProducer; clearing it here
	    // can regress an already-completed handoff back to pending.
	    if (LoadGuestU32(base, effective_render_owner + 4) !=
	        effective_render_handoff) {
	      StoreGuestU32(base, effective_render_owner + 4,
	                    effective_render_handoff);
	    }
	    StoreGuestU64(base, effective_render_handoff + 160,
	                  effective_render_frame_time_word);
	    StoreGuestU32(base, effective_render_handoff + 128,
	                  effective_render_source128);
	    StoreGuestU32(base, effective_render_handoff + 124,
	                  effective_render_source124);
	    StoreGuestU8(base, request_guest + 196, 0);
	    RecordRefiiRenderProducerScheduleSnapshot(base,
	                                              effective_render_owner,
	                                              0x8231AB34u,
	                                              effective_render_source124,
	                                              effective_render_source128,
	                                              effective_render_frame_time);
	    commit_result = effective_render_owner;
	    commit_slot_ran = true;
		    producer_completion_ran =
		        TryCompleteRefiiPostLoadingFrontendRenderProducer(source_ctx,
		                                                          base,
		                                                          source_api,
		                                                          request_guest,
	                                                          effective_render_owner,
		                                                          effective_render_handoff,
		                                                          effective_render_source124,
		                                                          effective_render_source128);
		    if (producer_completion_ran) {
		      PPCContext attach_ctx = source_ctx;
		      attach_ctx.r3.u32 = effective_render_owner;
		      post_gameface_frame_attach_from_handoff =
		          TryAttachRefiiPostGameFaceRenderHandoffToActiveFrame(
		              attach_ctx,
		              base,
		              "post_loading_frontend_render_handoff_after_seed");
		    }
		  }
	  if (LooksLikeGuestPointer(effective_render_handoff)) {
	    render_handoff_done_after =
	        LoadGuestU8(base, effective_render_handoff + 168);
	    render_handoff_source124_after =
	        LoadGuestU32(base, effective_render_handoff + 124);
	    render_handoff_source128_after =
	        LoadGuestU32(base, effective_render_handoff + 128);
	  }

  // Keep this as a VFS handoff shape, not a full frontend/render tick. The
  // generated runner observes the secondary owner as a ready work item and the
  // request as complete/owned by the primary surface; entering that runner from
  // the VFS worker can block the worker that is meant to release this handoff.
  StoreGuestU8(base, kSecondaryReadyGlobal, 1);
  StoreGuestU8(base, request_guest + 92, 1);
  bool post_gameface_primary_published_from_handoff = false;
  bool post_gameface_primary_native_ready_from_handoff = false;
  if (!RefiiPostGameFacePrimaryOwnerReady(base) &&
      LooksLikeGuestPointer(slot_object_guest) &&
      LoadGuestU32(base, slot_object_guest + 12) == request_guest) {
    PPCContext publish_ctx = source_ctx;
    post_gameface_primary_published_from_handoff =
        TryPublishRefiiPostGameFacePrimaryOwnerFromTrace(
            publish_ctx,
            base,
            "post_loading_frontend_render_handoff");
    if (post_gameface_primary_published_from_handoff) {
      post_gameface_primary_native_ready_from_handoff =
          TryRunRefiiPostGameFacePrimaryOwnerNativeFromTrace(
              source_ctx,
              base,
              "post_gameface_package_handoff_after_publish");
    }
  }
	  if (!LooksLikeGuestPointer(LoadGuestU32(base, request_guest + 132))) {
	    StoreGuestU32(base,
	                  request_guest + 132,
	                  ownerless_request_ready ? slot_object_guest : primary_owner);
	  }
	  const bool completion_publish_ran =
	      ownerless_request_ready && !RefiiPostGameFacePrimaryOwnerReady(base)
	          ? true
	          : TryPublishRefiiPostLoadingFrontendCompletionState(source_ctx,
	                                                              base,
	                                                              source_api,
	                                                              request_guest,
	                                                              LoadGuestU32(base,
	                                                                          kPrimaryOwnerGlobal));

  bool generated_request_run = false;
  uint32_t generated_request_result = 0;
  int64_t generated_request_run_ms = -1;
	  if (producer_completion_ran && completion_publish_ran &&
	      RefiiCollapsedLoadingSequenceReadyForOwnerRelease()) {
    bool should_mark_generated_request = false;
    {
      std::lock_guard<std::mutex> lock(RefiiInlineFrontendRenderRequestMutex());
      should_mark_generated_request =
          RefiiPostLoadingFrontendGeneratedRuns().insert(request_guest).second;
    }
    if (should_mark_generated_request) {
      RecordRefiiMainMenuBlockerEvent(
          "post_loading_frontend_generated_request_deferred",
          fmt::format("source={} request=0x{:08X} slot_object=0x{:08X} "
                      "reason=vfs_worker_avoids_full_frontend_runner "
                      "state=[{}] "
                      "globals=[{}]",
                      source_api,
                      request_guest,
                      slot_object_guest,
                      FormatNativeVfsAsyncRequestState(base, request_guest),
                      FormatRefiiLoadingScreenSequenceGlobals(base)));

      if (VfsTracingEnabled() ||
          ShouldLogRepeatedVfsTraceOperation(
              "PostLoadingFrontendGeneratedRequestDeferred", 8)) {
        REXLOG_INFO("Refii VFS post-loading frontend generated request "
                    "deferred "
                    "source={} request=0x{:08X} slot_object=0x{:08X} "
                    "reason=vfs_worker_avoids_full_frontend_runner state=[{}] "
                    "globals=[{}] handoff=[{}] main_menu=[{}]",
                    source_api,
                    request_guest,
                    slot_object_guest,
                    FormatNativeVfsAsyncRequestState(base, request_guest),
                    FormatRefiiLoadingScreenSequenceGlobals(base),
                    FormatRefiiPostVideoHandoffTraceForLog(),
                    FormatRefiiMainMenuBlockerTraceForLog());
      }

      LogRefiiVfsChainVerdict("frontend_post_video",
                              static_cast<uint32_t>(source_ctx.lr),
                              "post_loading_frontend_render_request",
                              "",
                              "",
                              "native_vfs_scheduled_request",
                              "post_loading_frontend_generated_request_deferred",
                              "Opened",
                              "native_worker_continue");
    }
	    if (ownerless_request_ready) {
	    } else {
      const bool completed_owner_release =
          source_api == "completed_owner_wait_release" ||
          source_api == "post_gameface_package_handoff_signal";
	      TryFinalizeRefiiPostLoadingFrontendGeneratedState(
	          source_ctx,
	          base,
	          source_api,
	          completed_owner_release,
	          true);
	    }
	  }

  bool post_gameface_frontend_run_from_handoff = false;
  if (RefiiPostGameFacePrimaryOwnerReady(base)) {
    PPCContext frontend_ctx = source_ctx;
    post_gameface_frontend_run_from_handoff =
        TryRunRefiiPostGameFaceFrontendRenderRequestFromTrace(
            frontend_ctx,
            base,
            "post_loading_frontend_render_handoff");
  }
  bool post_gameface_late_finalize_from_handoff = false;
  if (!ownerless_request_ready && producer_completion_ran &&
      completion_publish_ran && post_gameface_frontend_run_from_handoff &&
      RefiiPostGameFacePrimaryOwnerReady(base)) {
    // The generated path first wires the GameFace primary render request, then
    // tears down the loading-owner handoff. When Refii primes the package early,
    // the first finalize attempt can run before the native request has its
    // final owner/handoff fields, so retry once after the frontend handoff run.
    post_gameface_late_finalize_from_handoff =
        TryFinalizeRefiiPostLoadingFrontendGeneratedState(
            source_ctx,
            base,
            "post_gameface_package_handoff_signal",
            true,
            true);
  }

  const uint32_t primary_after = LoadGuestU32(base, kPrimaryOwnerGlobal);
  const uint32_t secondary_after = LoadGuestU32(base, kSecondaryOwnerGlobal);
  const std::string after_state =
      LooksLikeGuestPointer(request_guest)
          ? FormatNativeVfsAsyncRequestState(base, request_guest)
          : std::string{"request_pointer_invalid"};

  RecordRefiiMainMenuBlockerEvent(
      "post_loading_frontend_render_native_run",
      fmt::format("source={} request=0x{:08X} slot_object=0x{:08X} "
                  "result=0x{:08X} request_done=0x{:02X}->0x{:02X} "
                  "request_owner=0x{:08X}->0x{:08X} "
	                  "commit_slot={} commit_result=0x{:08X} "
	                  "producer_complete={} "
	                  "frame_attach={} "
	                  "generated_producer_slot={} generated_producer_result=0x{:08X} "
                  "generated_producer_ms={} "
                  "completion_publish={} "
                  "primary_publish_retry={} primary_native_ready_retry={} "
                  "frontend_run_retry={} "
                  "late_finalize_retry={} "
                  "generated_request_run={} generated_request_result=0x{:08X} "
                  "generated_request_run_ms={} "
                  "render_owner=0x{:08X} render_source=0x{:08X} "
                  "render_handoff=0x{:08X} render_head=0x{:08X} "
                  "render_pair=0x{:08X}/0x{:08X} "
                  "render_done=0x{:02X}->0x{:02X} "
                  "render_flag196=0x{:02X}->0x{:02X} "
                  "primary=0x{:08X}/{}->0x{:08X}/{} "
                  "secondary=0x{:08X}/{}->0x{:08X}/{} globals=[{}]",
                  source_api,
                  request_guest,
                  slot_object_guest,
                  request_guest,
                  request_done_before,
                  LooksLikeGuestPointer(request_guest)
                      ? LoadGuestU8(base, request_guest + 92)
                      : 0,
                  request_owner_before,
                  LooksLikeGuestPointer(request_guest)
                      ? LoadGuestU32(base, request_guest + 132)
                      : 0,
                  commit_slot_ran ? "ran" : "skipped",
	                  commit_result,
	                  producer_completion_ran ? "ran" : "skipped",
	                  post_gameface_frame_attach_from_handoff ? "ran" : "skipped",
	                  generated_producer_slot_ran ? "ran" : "skipped",
                  generated_producer_slot_result,
                  generated_producer_slot_ms,
                  completion_publish_ran ? "ran" : "skipped",
                  post_gameface_primary_published_from_handoff ? "ran"
                                                               : "skipped",
                  post_gameface_primary_native_ready_from_handoff ? "ran"
                                                                  : "skipped",
                  post_gameface_frontend_run_from_handoff ? "ran" : "skipped",
                  post_gameface_late_finalize_from_handoff ? "ran" : "skipped",
                  generated_request_run ? "ran" : "skipped",
                  generated_request_result,
                  generated_request_run_ms,
                  render_owner_before,
                  render_source_before,
                  render_handoff_before,
                  render_source_head,
                  render_handoff_source124_after,
                  render_handoff_source128_after,
                  render_handoff_done_before,
                  render_handoff_done_after,
                  render_flag196_before,
                  LooksLikeGuestPointer(request_guest)
                      ? LoadGuestU8(base, request_guest + 196)
                      : 0,
                  primary_owner,
                  primary_ready,
                  primary_after,
                  LoadGuestU8(base, kPrimaryReadyGlobal),
                  secondary_owner,
                  secondary_ready_before,
                  secondary_after,
                  LoadGuestU8(base, kSecondaryReadyGlobal),
                  FormatRefiiLoadingScreenSequenceGlobals(base)));

  if (VfsTracingEnabled() ||
      ShouldLogRepeatedVfsTraceOperation(
          "PostLoadingFrontendRenderNativeRun", 8)) {
    REXLOG_INFO("Refii VFS post-loading frontend render handoff "
                "source={} request=0x{:08X} slot_object=0x{:08X} "
	                "result=0x{:08X} commit_slot={} commit_result=0x{:08X} "
	                "producer_complete={} "
	                "frame_attach={} "
	                "generated_producer_slot={} generated_producer_result=0x{:08X} "
                "generated_producer_ms={} "
                "completion_publish={} "
                "primary_publish_retry={} primary_native_ready_retry={} "
                "frontend_run_retry={} "
                "late_finalize_retry={} "
                "generated_request_run={} generated_request_result=0x{:08X} "
                "generated_request_run_ms={} "
                "render_owner=0x{:08X} render_source=0x{:08X} "
                "render_handoff=0x{:08X} render_head=0x{:08X} "
                "render_pair=0x{:08X}/0x{:08X} "
                "render_done=0x{:02X}->0x{:02X} "
                "render_flag196=0x{:02X}->0x{:02X} before=[{}] after=[{}] "
                "globals=[{}] handoff=[{}] main_menu=[{}]",
                source_api,
                request_guest,
                slot_object_guest,
                request_guest,
                commit_slot_ran ? "ran" : "skipped",
	                commit_result,
	                producer_completion_ran ? "ran" : "skipped",
	                post_gameface_frame_attach_from_handoff ? "ran" : "skipped",
	                generated_producer_slot_ran ? "ran" : "skipped",
                generated_producer_slot_result,
                generated_producer_slot_ms,
                completion_publish_ran ? "ran" : "skipped",
                post_gameface_primary_published_from_handoff ? "ran"
                                                             : "skipped",
                post_gameface_primary_native_ready_from_handoff ? "ran"
                                                                : "skipped",
                post_gameface_frontend_run_from_handoff ? "ran" : "skipped",
                post_gameface_late_finalize_from_handoff ? "ran" : "skipped",
                generated_request_run ? "ran" : "skipped",
                generated_request_result,
                generated_request_run_ms,
                render_owner_before,
                render_source_before,
                render_handoff_before,
                render_source_head,
                render_handoff_source124_after,
                render_handoff_source128_after,
                render_handoff_done_before,
                render_handoff_done_after,
                render_flag196_before,
                LooksLikeGuestPointer(request_guest)
                    ? LoadGuestU8(base, request_guest + 196)
                    : 0,
                before_state,
                after_state,
                FormatRefiiLoadingScreenSequenceGlobals(base),
                FormatRefiiPostVideoHandoffTraceForLog(),
                FormatRefiiMainMenuBlockerTraceForLog());
  }

  LogRefiiVfsChainVerdict("frontend_post_video",
                          static_cast<uint32_t>(source_ctx.lr),
                          "post_loading_frontend_render_request",
                          "",
                          "",
                          "Refii_loading_owner_complete",
                          "post_loading_frontend_render_handoff",
                          "Opened",
                          "native_frontend_render_request_ready");
  return true;
}

bool MaybeRefreshRefiiPostGameFaceFollowupBoundary(
    const PPCContext& source_ctx,
    uint8_t* base,
    std::string_view normalized_asset,
    uint32_t stream_guest,
    std::string_view skip_reason,
    uint32_t package_mask_after) {
  if (base == nullptr || !RefiiLooseAssetBackendEnabled() ||
      !RefiiPostGameFacePackageHandoffSignalEmitted()) {
    return false;
  }

  constexpr uint32_t kLoadingGlobalBase = 0x83490000u;
  constexpr uint32_t kPrimaryOwnerGlobal = kLoadingGlobalBase + 27320u;
  constexpr uint32_t kPrimaryReadyGlobal = kLoadingGlobalBase + 27230u;
  constexpr uint32_t kSecondaryOwnerGlobal = kLoadingGlobalBase + 27328u;

  const uint32_t primary_owner = LoadGuestU32(base, kPrimaryOwnerGlobal);
  const uint8_t primary_ready = LoadGuestU8(base, kPrimaryReadyGlobal);
  const uint32_t secondary_owner = LoadGuestU32(base, kSecondaryOwnerGlobal);
  const uint32_t active_table = FindRefiiLoadingScreenActiveResourceTable(base);
  const bool can_refresh =
      LooksLikeGuestPointer(primary_owner) && primary_ready != 0 &&
      LooksLikeGuestDataPointer(active_table) &&
      RefiiStartupVideosReadyForMenuHandoff();
  if (!can_refresh) {
    if (VfsTracingEnabled() &&
        ShouldLogRepeatedVfsTraceOperation(
            "PostGameFaceFollowupBoundaryRefreshSkipped", 8)) {
      REXLOG_INFO("Refii VFS post-GameFace follow-up boundary refresh skipped "
                  "reason={} asset={} stream=0x{:08X} primary=0x{:08X}/{} "
                  "secondary=0x{:08X} active_table=0x{:08X} globals=[{}] "
                  "handoff=[{}] main_menu=[{}]",
                  skip_reason,
                  normalized_asset,
                  stream_guest,
                  primary_owner,
                  primary_ready,
                  secondary_owner,
                  active_table,
                  FormatRefiiLoadingScreenSequenceGlobals(base),
                  FormatRefiiPostVideoHandoffTraceForLog(),
                  FormatRefiiMainMenuBlockerTraceForLog());
    }
    return false;
  }

	  PPCContext refresh_ctx = source_ctx;
	  uint32_t bridged_handle = 0;
	  bool sequence_bridged =
	      TryRefreshRememberedLoadingScreenSequenceBridge(
	          refresh_ctx,
	          base,
	          "post_gameface_followup_boundary",
	          &bridged_handle);
	  bool loading_animation_primed = false;
	  PPCContext publish_ctx = source_ctx;
	  const bool primary_published =
	      TryPublishRefiiPostGameFacePrimaryOwnerFromTrace(
          publish_ctx, base, "post_gameface_followup_boundary");

  PPCContext frontend_ctx = source_ctx;
  const bool frontend_run =
      TryRunRefiiPostGameFaceFrontendRenderRequestFromTrace(
          frontend_ctx, base, "post_gameface_followup_boundary");

  RecordRefiiMainMenuBlockerEvent(
      "post_gameface_followup_boundary_refresh",
      fmt::format("asset={} stream=0x{:08X} skip_reason={} "
	                  "package_mask=0x{:02X} primary=0x{:08X}/{} "
	                  "secondary=0x{:08X} active_table=0x{:08X} "
	                  "sequence_bridged={} handle=0x{:08X} "
	                  "loading_animation_primed={} "
	                  "primary_published={} frontend_run={} globals=[{}]",
	                  normalized_asset,
	                  stream_guest,
                  skip_reason,
                  package_mask_after,
                  primary_owner,
                  primary_ready,
                  secondary_owner,
	                  active_table,
	                  sequence_bridged ? "true" : "false",
	                  bridged_handle,
	                  loading_animation_primed ? "true" : "false",
	                  primary_published ? "true" : "false",
	                  frontend_run ? "true" : "false",
	                  FormatRefiiLoadingScreenSequenceGlobals(base)));

  if (VfsTracingEnabled() ||
      ShouldLogRepeatedVfsTraceOperation(
          "PostGameFaceFollowupBoundaryRefresh", 8)) {
    REXLOG_INFO("Refii VFS post-GameFace follow-up boundary refresh "
                "asset={} stream=0x{:08X} skip_reason={} "
	                "package_mask=0x{:02X} primary=0x{:08X}/{} "
	                "secondary=0x{:08X} active_table=0x{:08X} "
	                "sequence_bridged={} handle=0x{:08X} "
	                "loading_animation_primed={} "
	                "primary_published={} frontend_run={} globals=[{}] "
	                "handoff=[{}] main_menu=[{}]",
	                normalized_asset,
                stream_guest,
                skip_reason,
                package_mask_after,
                primary_owner,
                primary_ready,
                secondary_owner,
	                active_table,
	                sequence_bridged ? "true" : "false",
	                bridged_handle,
	                loading_animation_primed ? "true" : "false",
	                primary_published ? "true" : "false",
	                frontend_run ? "true" : "false",
	                FormatRefiiLoadingScreenSequenceGlobals(base),
                FormatRefiiPostVideoHandoffTraceForLog(),
                FormatRefiiMainMenuBlockerTraceForLog());
  }

  LogRefiiVfsChainVerdict("frontend_post_video",
                          static_cast<uint32_t>(source_ctx.lr),
                          std::string(normalized_asset),
                          "",
                          "",
                          "Refii_gameface_followup_boundary",
                          "post_gameface_followup_boundary_refresh",
                          "Opened",
                          "native_primary_loading_owner_refresh");
  return true;
}


}  // namespace detail
}  // namespace refii::vfs
