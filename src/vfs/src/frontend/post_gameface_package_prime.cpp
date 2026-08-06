#include "vfs_hooks_private.h"

namespace refii::vfs {
inline namespace detail {

void MarkRefiiPostFrontendConsumerAssetRequest(std::string_view api,
                                               std::string_view asset_path,
                                               uint32_t resource_hash,
                                               uint32_t lr);
uint32_t GetNativeResourceManagerForVfsPackagePrime(uint8_t* base);

uint32_t RefiiGameFacePackageBitForAssetKey(std::string_view normalized_asset) {
  if (EndsWith(normalized_asset, ".bgf")) {
    return 0x1u;
  }
  if (EndsWith(normalized_asset, ".bsg")) {
    return 0x2u;
  }
  if (EndsWith(normalized_asset, ".fac")) {
    return 0x4u;
  }
  return 0;
}

std::string RefiiGameFacePackageKeyForAssetKey(
    std::string_view normalized_asset) {
  const uint32_t bit = RefiiGameFacePackageBitForAssetKey(normalized_asset);
  if (bit == 0 || normalized_asset.size() <= 4) {
    return {};
  }
  return std::string(normalized_asset.substr(0, normalized_asset.size() - 4));
}

bool MaybeRecordRefiiPostGameFacePackageOptionalMiss(
    const PPCContext& source_ctx,
    uint8_t* base,
    std::string_view asset_path,
    std::string_view api) {
  const std::string normalized_asset = NormalizeLooseAssetKey(asset_path);
  if (base == nullptr || !RefiiLooseAssetBackendEnabled() ||
      !IsGameFaceBinaryAssetKey(normalized_asset)) {
    return false;
  }

  const uint32_t package_bit =
      RefiiGameFacePackageBitForAssetKey(normalized_asset);
  const std::string package_key =
      RefiiGameFacePackageKeyForAssetKey(normalized_asset);
  if (package_bit == 0 || package_key.empty()) {
    return false;
  }

  MarkRefiiPostFrontendConsumerAssetRequest(
      api, normalized_asset, 0, static_cast<uint32_t>(source_ctx.lr));

  uint32_t package_mask_after = 0;
  bool package_complete_after_update = false;
  {
    std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
    auto& trace = RefiiPostVideoHandoffTraceState();
    if (trace.post_gameface_package_tracking_key != package_key) {
      trace.post_gameface_package_tracking_key = package_key;
      trace.post_gameface_package_extension_mask = 0;
    }
    trace.post_gameface_package_extension_mask |= package_bit;
    package_mask_after = trace.post_gameface_package_extension_mask;
    package_complete_after_update = (package_mask_after & 0x7u) == 0x7u;
    if (package_complete_after_update) {
      trace.post_gameface_completed_package_key = package_key;
      trace.post_gameface_completed_package_extension_mask = package_mask_after;
    }
  }

  RecordRefiiMainMenuBlockerEvent(
      "gameface_package_optional_miss",
      fmt::format("api={} asset={} package={} bit=0x{:02X} mask=0x{:02X} "
                  "complete={}",
                  api,
                  normalized_asset,
                  package_key,
                  package_bit,
                  package_mask_after,
                  package_complete_after_update ? "true" : "false"));
  if (VfsTracingEnabled()) {
    REXLOG_INFO("Refii VFS GameFace package optional miss api={} asset={} "
                "package={} bit=0x{:02X} mask=0x{:02X} complete={} "
                "handoff=[{}] main_menu=[{}]",
                api,
                normalized_asset,
                package_key,
                package_bit,
                package_mask_after,
                package_complete_after_update ? "true" : "false",
                FormatRefiiPostVideoHandoffTraceForLog(),
                FormatRefiiMainMenuBlockerTraceForLog());
  }

  if (package_complete_after_update) {
    PPCContext signal_ctx = source_ctx;
    return TrySignalRefiiCompletedPostGameFacePackageHandoffFromTrace(
        signal_ctx, base, 0, 0, VfsTracingEnabled());
  }
  return true;
}

bool MaybeSignalRefiiPostGameFacePackageHandoff(
    const PPCContext& source_ctx,
    uint8_t* base,
    std::string_view asset_path,
    uint32_t stream_guest,
    bool allow_generated_frame_completion = true) {
  const std::string normalized_asset = NormalizeLooseAssetKey(asset_path);
  const bool font_boundary =
      StartsWith(normalized_asset, "post_video_font:");
  const uint32_t package_bit =
      RefiiGameFacePackageBitForAssetKey(normalized_asset);
  const std::string package_key =
      RefiiGameFacePackageKeyForAssetKey(normalized_asset);
  uint32_t package_mask_after = 0;
  bool package_complete_after_update = false;
  if (base == nullptr || !RefiiLooseAssetBackendEnabled() ||
      stream_guest == 0 ||
      (!font_boundary && !IsGameFaceBinaryAssetKey(normalized_asset))) {
    return false;
  }

  auto try_prime_package_siblings =
      [&](std::string_view package_key,
          uint32_t package_mask_after) -> bool {
    const bool parsed_post_video_texture =
        RefiiMainMenuTraceHasParsedTextureForGeneratedReadyPayload();
    if (base == nullptr || package_key.empty() ||
        (package_mask_after & 0x7u) == 0x7u ||
        !RefiiStartupVideosSettledForMenuHandoff() ||
        (!parsed_post_video_texture && !RefiiPostVideoFrontendChainConsumed())) {
      return false;
    }

    static std::mutex prime_mutex;
    static std::unordered_set<std::string> primed_packages;
    const std::string normalized_package_key =
        NormalizeLooseAssetKey(package_key);
    {
      std::lock_guard<std::mutex> lock(prime_mutex);
      if (!primed_packages.insert(normalized_package_key).second) {
        return false;
      }
    }

    struct PackageMember {
      std::string_view extension;
      uint32_t bit;
      uint32_t lr;
    };
    constexpr std::array<PackageMember, 3> kPackageMembers = {{
        {".bgf", 0x1u, 0x82C6F248u},
        {".bsg", 0x2u, 0x82C6F534u},
        {".fac", 0x4u, 0x82C6F534u},
    }};

    const uint32_t manager_guest =
        GetNativeResourceManagerForVfsPackagePrime(base);
    uint32_t opened_mask = 0;
    uint32_t missing_mask = 0;
    uint32_t failed_mask = 0;
    uint32_t last_stream_guest = 0;
    for (const PackageMember& member : kPackageMembers) {
      if ((package_mask_after & member.bit) != 0) {
        continue;
      }

      const std::string asset =
          normalized_package_key + std::string(member.extension);
      PPCContext malloc_ctx = source_ctx;
      const uint32_t out_guest =
          CallLhMalloc(malloc_ctx, base, sizeof(uint32_t));
      if (out_guest == 0) {
        failed_mask |= member.bit;
        continue;
      }
      StoreGuestU32(base, out_guest, 0);

      PPCContext open_ctx = source_ctx;
      open_ctx.lr = member.lr;
      RefiiVfsOpenResult open_result;
      const RefiiVfsOpenDisposition disposition =
          TryOpenRefiiVfsAsset(
              open_ctx,
              base,
              open_ctx,
              RefiiVfsOpenShape::RawStream,
              RefiiVfsOpenRequest{
                  .api = "GameFacePackageSiblingPrime",
                  .request_name = asset,
                  .out_guest = out_guest,
                  .manager_guest = manager_guest,
                  .mode = 1,
                  .caller_lr = member.lr,
                  .allow_optional_null_miss = true,
              },
              &open_result);

      uint32_t sibling_stream_guest = TryReadOutputPointer(base, out_guest);
      if (sibling_stream_guest == 0) {
        sibling_stream_guest = open_result.opened_guest;
      }

      if (disposition == RefiiVfsOpenDisposition::Opened &&
          sibling_stream_guest != 0) {
        opened_mask |= member.bit;
        last_stream_guest = sibling_stream_guest;
        MarkRefiiPostFrontendConsumerAssetRequest(
            "GameFacePackageSiblingPrime", asset, 0, member.lr);
        MaybeSignalRefiiPostGameFacePackageHandoff(
            open_ctx,
            base,
            asset,
            sibling_stream_guest,
            member.bit == 0x4u);
      } else if (disposition == RefiiVfsOpenDisposition::OptionalNull) {
        missing_mask |= member.bit;
        MaybeRecordRefiiPostGameFacePackageOptionalMiss(
            open_ctx, base, asset, "GameFacePackageSiblingPrime");
      } else {
        failed_mask |= member.bit;
      }
    }

    const bool completed =
        ((package_mask_after | opened_mask | missing_mask) & 0x7u) == 0x7u;
    RecordRefiiMainMenuBlockerEvent(
        "gameface_package_sibling_prime",
        fmt::format("package={} existing_mask=0x{:02X} opened_mask=0x{:02X} "
                    "missing_mask=0x{:02X} failed_mask=0x{:02X} "
                    "completed={} manager=0x{:08X} last_stream=0x{:08X}",
                    normalized_package_key,
                    package_mask_after,
                    opened_mask,
                    missing_mask,
                    failed_mask,
                    completed ? "true" : "false",
                    manager_guest,
                    last_stream_guest));
    if (VfsTracingEnabled()) {
      REXLOG_INFO("Refii VFS GameFace package sibling prime package={} "
                  "existing_mask=0x{:02X} opened_mask=0x{:02X} "
                  "missing_mask=0x{:02X} failed_mask=0x{:02X} "
                  "completed={} manager=0x{:08X} last_stream=0x{:08X} "
                  "handoff=[{}] main_menu=[{}]",
                  normalized_package_key,
                  package_mask_after,
                  opened_mask,
                  missing_mask,
                  failed_mask,
                  completed ? "true" : "false",
                  manager_guest,
                  last_stream_guest,
                  FormatRefiiPostVideoHandoffTraceForLog(),
                  FormatRefiiMainMenuBlockerTraceForLog());
    }
    return completed;
  };

  uint32_t wait_handle = 0;
  uint32_t signal_handle = 0;
  uint32_t wait_object = 0;
  uint32_t handoff_guest = 0;
  uint32_t owner_guest = 0;
  uint32_t wait_lr = 0;
  uint32_t wait_result = 0;
  uint32_t current_handoff_guest = 0;
  uint32_t source124 = 0;
  uint32_t source128 = 0;
  uint8_t complete_before = 0;
  uint8_t complete_after = 0;
  uint8_t flag244_before = 0;
  uint8_t flag244_after = 0;
  int64_t wait_age_ms = -1;
  bool should_signal = false;
  bool run_generated_frame_completion = false;
  double generated_frame_time = 0.0;
  const char* signal_source = "wait_handoff";
  const uint32_t source_lr = static_cast<uint32_t>(source_ctx.lr);
  const bool source_allows_generated_frame_completion =
      source_lr != 0x82C6A44Cu ||
      (package_bit != 0 && normalized_asset.ends_with(".fac"));
  std::string skip_reason;
  {
    std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
    auto& trace = RefiiPostVideoHandoffTraceState();
    if (package_bit != 0 && !package_key.empty()) {
      if (trace.post_gameface_package_tracking_key != package_key) {
        trace.post_gameface_package_tracking_key = package_key;
        trace.post_gameface_package_extension_mask = 0;
      }
      trace.post_gameface_package_extension_mask |= package_bit;
      package_mask_after = trace.post_gameface_package_extension_mask;
      package_complete_after_update =
          (trace.post_gameface_package_extension_mask & 0x7u) == 0x7u;
      if (package_complete_after_update) {
        trace.post_gameface_completed_package_key = package_key;
        trace.post_gameface_completed_package_extension_mask =
            trace.post_gameface_package_extension_mask;
      }
    } else if (font_boundary) {
      package_mask_after = RefiiCompletedPostGameFacePackageMaskLocked(trace);
    }
    if (!font_boundary && package_bit != 0 &&
        !package_complete_after_update) {
      skip_reason = "post_gameface_package_incomplete";
    } else if (trace.post_gameface_package_handoff_signal_emitted) {
      if (trace.post_gameface_package_handoff_asset == normalized_asset) {
        return false;
      }
      if (trace.post_gameface_package_handoff_signal_at.time_since_epoch()
              .count() != 0) {
        const int64_t previous_signal_age_ms =
            std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() -
                trace.post_gameface_package_handoff_signal_at)
                .count();
        if (!font_boundary && previous_signal_age_ms >= 0 &&
            previous_signal_age_ms < 250 &&
            !package_complete_after_update) {
          skip_reason = "post_gameface_package_signal_throttled";
        }
      }
    }

    if (!skip_reason.empty()) {
      // Keep the common skipped trace path below; it includes the current
      // handoff snapshot, which is the useful part when this gate is wrong.
    } else if (!trace.post_frontend_consumer_request_seen) {
      skip_reason = "post_gameface_package_before_frontend_request";
    } else if (!RefiiStartupVideosSettledForPostVideoHandoffLocked(trace,
                                                                   nullptr)) {
      skip_reason = "startup_videos_not_settled";
    } else {
      owner_guest = trace.last_wait_owner;
      handoff_guest = trace.last_wait_handoff;
      wait_object = trace.last_wait_object;
      wait_handle = trace.last_wait_handle;
      signal_handle = trace.last_wait_signal_handle;
      wait_lr = trace.last_wait_lr;
      wait_result = trace.last_wait_result;
      if (trace.last_wait_at.time_since_epoch().count() != 0) {
        wait_age_ms =
            std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() - trace.last_wait_at)
                .count();
      }
      if (signal_handle == 0) {
        signal_handle = wait_handle;
      }
      current_handoff_guest =
          LooksLikeGuestPointer(owner_guest) ? LoadGuestU32(base, owner_guest + 4)
                                             : 0;
      const bool wait_handoff_live =
          signal_handle != 0 && LooksLikeGuestPointer(wait_object) &&
          LooksLikeGuestPointer(owner_guest) &&
          LooksLikeGuestPointer(handoff_guest) &&
          current_handoff_guest == handoff_guest;

      int64_t render_age_ms = -1;
      if (trace.last_render_schedule_at.time_since_epoch().count() != 0) {
        render_age_ms =
            std::chrono::duration_cast<std::chrono::milliseconds>(
                std::chrono::steady_clock::now() -
                trace.last_render_schedule_at)
                .count();
      }
      const uint32_t render_owner_guest = trace.last_render_schedule_owner;
      const uint32_t render_handoff_guest = trace.last_render_schedule_handoff;
      const uint32_t render_current_handoff =
          LooksLikeGuestPointer(render_owner_guest)
              ? LoadGuestU32(base, render_owner_guest + 4)
              : 0;
      const uint32_t render_signal_handle =
          LooksLikeGuestPointer(render_handoff_guest)
              ? LoadGuestU32(base, render_handoff_guest + 456)
              : 0;
      const uint32_t render_wait_object =
          LooksLikeGuestPointer(render_handoff_guest)
              ? render_handoff_guest + 460
              : 0;
      const uint32_t render_wait_handle =
          LooksLikeGuestPointer(render_wait_object)
              ? LoadGuestU32(base, render_wait_object + 128)
              : 0;
      const bool render_handoff_live =
          render_signal_handle != 0 && LooksLikeGuestPointer(render_wait_object) &&
          LooksLikeGuestPointer(render_owner_guest) &&
          LooksLikeGuestPointer(render_handoff_guest) &&
          render_current_handoff == render_handoff_guest &&
          (render_age_ms < 0 || render_age_ms <= 120000);
      const bool prefer_render_schedule =
          render_handoff_live &&
          (!wait_handoff_live ||
           (trace.last_render_schedule_at.time_since_epoch().count() != 0 &&
            trace.last_wait_at.time_since_epoch().count() != 0 &&
            trace.last_render_schedule_at > trace.last_wait_at));
      if (prefer_render_schedule) {
        owner_guest = render_owner_guest;
        handoff_guest = render_handoff_guest;
        wait_object = render_wait_object;
        wait_handle = render_wait_handle;
        signal_handle = render_signal_handle != 0 ? render_signal_handle
                                                  : render_wait_handle;
        wait_lr = trace.last_render_schedule_lr;
        wait_result = 0;
        wait_age_ms = render_age_ms;
        current_handoff_guest = render_current_handoff;
        signal_source = "render_schedule_handoff";
      }

      const bool selected_handoff_live =
          signal_handle != 0 && LooksLikeGuestPointer(wait_object) &&
          LooksLikeGuestPointer(owner_guest) &&
          LooksLikeGuestPointer(handoff_guest) &&
          current_handoff_guest == handoff_guest;
      if (!selected_handoff_live) {
        skip_reason = "post_gameface_package_wait_handoff_missing";
      } else {
        complete_before = LoadGuestU8(base, handoff_guest + 168);
        flag244_before = LoadGuestU8(base, handoff_guest + 244);
        source124 = LoadGuestU32(base, handoff_guest + 124);
        source128 = LoadGuestU32(base, handoff_guest + 128);
        const bool selected_source_idle =
            source124 == 0xFFFFFFFFu || source128 == 0xFFFFFFFFu;
	        const bool nonidle_source_matches_selected_handoff =
	            package_complete_after_update && selected_source_idle &&
	            trace.last_nonidle_render_schedule_owner == owner_guest &&
	            trace.last_nonidle_render_schedule_handoff == handoff_guest &&
	            trace.last_nonidle_render_schedule_source124 != 0xFFFFFFFFu &&
	            trace.last_nonidle_render_schedule_source128 != 0xFFFFFFFFu;
        if (nonidle_source_matches_selected_handoff) {
          source124 = trace.last_nonidle_render_schedule_source124;
          source128 = trace.last_nonidle_render_schedule_source128;
          generated_frame_time = trace.last_nonidle_render_schedule_frame_time;
          signal_source = "gameface_package_nonidle_handoff";
        } else if (trace.last_render_schedule_owner == owner_guest &&
                   trace.last_render_schedule_handoff == handoff_guest) {
          generated_frame_time = trace.last_render_schedule_frame_time;
        }
        trace.post_gameface_package_handoff_signal_emitted = true;
        trace.post_gameface_package_handoff_signal_handle = signal_handle;
        trace.post_gameface_package_handoff_signal_lr =
            static_cast<uint32_t>(source_ctx.lr);
        trace.post_gameface_package_handoff_asset = normalized_asset;
        trace.post_gameface_package_handoff_signal_at =
            std::chrono::steady_clock::now();
        should_signal = true;
        run_generated_frame_completion =
            allow_generated_frame_completion &&
            package_complete_after_update &&
            source_allows_generated_frame_completion &&
            source124 != 0xFFFFFFFFu &&
            source128 != 0xFFFFFFFFu &&
            LooksLikeGuestPointer(owner_guest) &&
            LooksLikeGuestPointer(handoff_guest) &&
            LoadGuestU32(base, owner_guest + 4) == handoff_guest;
      }
    }
  }

  if (!should_signal) {
    if (package_bit != 0 &&
        skip_reason == "post_gameface_package_incomplete" &&
        try_prime_package_siblings(package_key, package_mask_after)) {
      return true;
    }
    const bool followup_boundary =
        skip_reason == "post_gameface_package_wait_handoff_missing" &&
        (font_boundary || package_complete_after_update) &&
        (package_mask_after & 0x7u) == 0x7u;
    if (followup_boundary &&
        MaybeRefreshRefiiPostGameFaceFollowupBoundary(source_ctx,
                                                      base,
                                                      normalized_asset,
                                                      stream_guest,
                                                      skip_reason,
                                                      package_mask_after)) {
      return true;
    }
    if (VfsTracingEnabled() &&
        ShouldLogRepeatedVfsTraceOperation(
            "PostGameFacePackageHandoffSignalSkipped", 8)) {
      REXLOG_INFO("Refii VFS post-GameFace package handoff signal skipped "
                  "reason={} lr=0x{:08X} asset={} stream=0x{:08X} "
                  "handoff=[{}] main_menu=[{}]",
                  skip_reason.empty() ? "unknown" : skip_reason,
                  static_cast<uint32_t>(source_ctx.lr),
                  normalized_asset,
                  stream_guest,
                  FormatRefiiPostVideoHandoffTraceForLog(),
                  FormatRefiiMainMenuBlockerTraceForLog());
    }
    return false;
  }

  if (LooksLikeGuestPointer(handoff_guest)) {
    if (source128 != 0xFFFFFFFFu) {
      const uint32_t previous_frame = LoadGuestU32(base, handoff_guest + 120);
      if (previous_frame != source128) {
        StoreGuestU8(base, handoff_guest + 132, 1);
      }
      StoreGuestU32(base, handoff_guest + 120, source128);
    }
    if (source124 != 0xFFFFFFFFu) {
      StoreGuestU32(base, handoff_guest + 116, source124);
    }
    // GameFace package arrival should wake the pending handoff, but it is not
    // itself proof that the generated frame owner is ready to be freed.
    StoreGuestU8(base, handoff_guest + 168, 1);
    complete_after = LoadGuestU8(base, handoff_guest + 168);
    flag244_after = LoadGuestU8(base, handoff_guest + 244);
  }

  const bool primary_owner_published =
      package_complete_after_update &&
      TryPublishRefiiPostGameFacePrimaryOwner(source_ctx,
                                              base,
                                              normalized_asset,
                                              signal_source,
                                              package_mask_after);
  if (run_generated_frame_completion && !primary_owner_published &&
      !RefiiPostGameFacePrimaryOwnerReady(base)) {
    run_generated_frame_completion = false;
    RecordRefiiMainMenuBlockerEvent(
        "post_gameface_generated_frame_completion_deferred",
        fmt::format("asset={} source={} package_mask=0x{:02X} "
                    "reason=primary_owner_not_ready globals=[{}]",
                    normalized_asset,
                    signal_source,
                    package_mask_after,
                    FormatRefiiLoadingScreenSequenceGlobals(base)));
  }
  bool post_gameface_frontend_run = false;
  bool post_gameface_primary_owner_run = false;
  bool post_gameface_post_loading_run = false;
  bool post_gameface_pre_frame_attach = false;

  PPCContext signal_ctx = source_ctx;
  signal_ctx.r3.u32 = signal_handle;
  if (signal_ctx.r3.u32 != 0) {
    ::__imp__sub_822C9388(signal_ctx, base);
  }

  PPCContext wait_signal_ctx = source_ctx;
  wait_signal_ctx.r3.u32 = wait_handle;
  if (wait_signal_ctx.r3.u32 != 0 &&
      wait_signal_ctx.r3.u32 != signal_ctx.r3.u32) {
    ::__imp__sub_822C9388(wait_signal_ctx, base);
  }

  if (primary_owner_published) {
    post_gameface_primary_owner_run =
        TryRunRefiiPostGameFacePrimaryOwnerNativeFromTrace(
            source_ctx,
            base,
            "post_gameface_package_handoff_after_publish");

    uint32_t request_guest = 0;
    uint32_t owner_guest = 0;
    uint32_t request_handoff = 0;
    if (TryGetRefiiPostGameFacePrimaryRenderRequest(
            base, &request_guest, &owner_guest, &request_handoff) &&
        LooksLikeGuestPointer(owner_guest) &&
        LooksLikeGuestPointer(request_handoff)) {
      PPCContext attach_ctx = source_ctx;
      attach_ctx.r3.u32 = owner_guest;
      post_gameface_pre_frame_attach =
          TryAttachRefiiPostGameFaceRenderHandoffToActiveFrame(
              attach_ctx, base, "PostGameFacePackage_Ready_PreFrame");
    }

    PPCContext frontend_ctx = source_ctx;
    post_gameface_frontend_run =
        TryRunRefiiPostGameFaceFrontendRenderRequestFromTrace(
            frontend_ctx,
            base,
            "post_gameface_package_handoff_signal");
    post_gameface_post_loading_run =
        TryRunRefiiPostLoadingFrontendRenderRequestNativeFromTrace(
            source_ctx,
            base,
            "post_gameface_package_handoff_signal");
  }

  const bool deferred_hash_wake = false;
  const bool loading_owner_finished_from_package =
      package_complete_after_update &&
      TryFinishRememberedRefiiLoadingOwnerFromPostFrontendSignal(
          source_ctx, base, "post_gameface_package_handoff_signal");

  uint32_t generated_frame_result = 0;
  uint32_t generated_frame_owner_after = 0;
  if (run_generated_frame_completion &&
      LooksLikeGuestPointer(owner_guest) &&
      LooksLikeGuestPointer(handoff_guest) &&
      LoadGuestU32(base, owner_guest + 4) == handoff_guest) {
    PPCContext frame_ctx = source_ctx;
    frame_ctx.lr = 0x826E318Cu;
    frame_ctx.r3.u32 = owner_guest;
    frame_ctx.f1.f64 = generated_frame_time;
    ::__imp__sub_82389210(frame_ctx, base);
    generated_frame_result = frame_ctx.r3.u32;
    generated_frame_owner_after =
        LooksLikeGuestPointer(owner_guest) ? LoadGuestU32(base, owner_guest + 4)
                                           : 0;
    RecordRefiiMainMenuBlockerEvent(
        "post_gameface_generated_frame_completion_tail",
        fmt::format("asset={} owner=0x{:08X} handoff=0x{:08X} "
                    "result=0x{:08X} owner_plus4_after=0x{:08X} "
                    "frame_time={} source={} package_mask=0x{:02X}",
                    normalized_asset,
                    owner_guest,
                    handoff_guest,
                    generated_frame_result,
                    generated_frame_owner_after,
                    generated_frame_time,
                    signal_source,
                    package_mask_after));
  }

  RecordRefiiMainMenuBlockerEvent(
      "post_gameface_package_handoff_signal",
      fmt::format("asset={} stream=0x{:08X} owner=0x{:08X} handoff=0x{:08X} "
                  "current_handoff=0x{:08X} wait_object=0x{:08X} "
                  "wait_handle=0x{:08X} signal_handle=0x{:08X} "
                  "wait_lr=0x{:08X} wait_result=0x{:08X} wait_age_ms={} "
                  "source={} complete=0x{:02X}->0x{:02X} "
                  "flag244=0x{:02X}->0x{:02X} "
                  "source124=0x{:08X} source128=0x{:08X} "
                  "package_key={} package_mask=0x{:02X} "
                  "package_complete={} "
                  "generated_frame_completion={} generated_frame_result=0x{:08X} "
                  "generated_frame_owner_after=0x{:08X} "
                  "primary_owner_published={} pre_frame_attach={} "
                  "frontend_run={} "
                  "primary_owner_run={} post_loading_run={} "
                  "deferred_hash_wake={} "
                  "loading_owner_finished={} "
                  "signal_result=0x{:08X} wait_signal_result=0x{:08X}",
                  normalized_asset,
                  stream_guest,
                  owner_guest,
                  handoff_guest,
                  current_handoff_guest,
                  wait_object,
                  wait_handle,
                  signal_handle,
                  wait_lr,
                  wait_result,
                  wait_age_ms,
                  signal_source,
                  complete_before,
                  complete_after,
                  flag244_before,
                  flag244_after,
                  source124,
                  source128,
                  package_key.empty() ? "none" : package_key,
                  package_mask_after,
                  package_complete_after_update ? "true" : "false",
                  run_generated_frame_completion ? "true" : "false",
                  generated_frame_result,
                  generated_frame_owner_after,
                  primary_owner_published ? "true" : "false",
                  post_gameface_pre_frame_attach ? "true" : "false",
                  post_gameface_frontend_run ? "true" : "false",
                  post_gameface_primary_owner_run ? "true" : "false",
                  post_gameface_post_loading_run ? "true" : "false",
                  deferred_hash_wake ? "true" : "false",
                  loading_owner_finished_from_package ? "true" : "false",
                  signal_ctx.r3.u32,
                  wait_signal_ctx.r3.u32));

  if (VfsTracingEnabled()) {
    REXLOG_INFO("Refii VFS post-GameFace package handoff signal "
                "lr=0x{:08X} asset={} stream=0x{:08X} owner=0x{:08X} "
                "handoff=0x{:08X} current_handoff=0x{:08X} "
                "wait_object=0x{:08X} wait_handle=0x{:08X} "
                "signal_handle=0x{:08X} wait_lr=0x{:08X} "
                "wait_result=0x{:08X} wait_age_ms={} source={} "
                "complete=0x{:02X}->0x{:02X} "
                "flag244=0x{:02X}->0x{:02X} source124=0x{:08X} "
                "source128=0x{:08X} package_key={} "
                "package_mask=0x{:02X} package_complete={} "
                "generated_frame_completion={} "
                "generated_frame_result=0x{:08X} "
                "generated_frame_owner_after=0x{:08X} "
                "primary_owner_published={} pre_frame_attach={} "
                "frontend_run={} "
                "primary_owner_run={} post_loading_run={} "
                "deferred_hash_wake={} "
                "loading_owner_finished={} "
                "signal_result=0x{:08X} "
                "wait_signal_result=0x{:08X} event_state=[{}] "
                "wait_event_state=[{}] handoff_trace=[{}] main_menu=[{}]",
                static_cast<uint32_t>(source_ctx.lr),
                normalized_asset,
                stream_guest,
                owner_guest,
                handoff_guest,
                current_handoff_guest,
                wait_object,
                wait_handle,
                signal_handle,
                wait_lr,
                wait_result,
                wait_age_ms,
                signal_source,
                complete_before,
                complete_after,
                flag244_before,
                flag244_after,
                source124,
                source128,
                package_key.empty() ? "none" : package_key,
                package_mask_after,
                package_complete_after_update ? "true" : "false",
                run_generated_frame_completion ? "true" : "false",
                generated_frame_result,
                generated_frame_owner_after,
                primary_owner_published ? "true" : "false",
                post_gameface_pre_frame_attach ? "true" : "false",
                post_gameface_frontend_run ? "true" : "false",
                post_gameface_primary_owner_run ? "true" : "false",
                post_gameface_post_loading_run ? "true" : "false",
                deferred_hash_wake ? "true" : "false",
                loading_owner_finished_from_package ? "true" : "false",
                signal_ctx.r3.u32,
                wait_signal_ctx.r3.u32,
                FormatGeneratedEventSetState(base, signal_handle),
                FormatGeneratedEventSetState(base, wait_handle),
                FormatRefiiPostVideoHandoffTraceForLog(),
                FormatRefiiMainMenuBlockerTraceForLog());
    LogRefiiVfsChainVerdict("frontend_post_video",
                            static_cast<uint32_t>(source_ctx.lr),
                            normalized_asset,
                            "",
                            "",
                            "Refii_gameface_native_cache_stream",
                            "post_gameface_package_handoff_signal",
                            "Opened",
                            "next_gameface_or_main_menu_request");
  }
  return true;
}

void MarkRefiiPostFrontendConsumerAssetRequest(std::string_view api,
                                               std::string_view asset_path,
                                               uint32_t resource_hash,
                                               uint32_t lr) {
  const std::string normalized = NormalizeLooseAssetKey(asset_path);
  if (normalized.empty()) {
    return;
  }

  bool should_log = false;
  {
    std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
    auto& trace = RefiiPostVideoHandoffTraceState();
    if (trace.post_frontend_consumer_request_seen) {
      return;
    }
    trace.post_frontend_consumer_request_seen = true;
    trace.first_post_frontend_consumer_request_api = std::string(api);
    trace.first_post_frontend_consumer_request_asset = normalized;
    trace.first_post_frontend_consumer_request_lr = lr;
    trace.first_post_frontend_consumer_request_hash = resource_hash;
    trace.first_post_frontend_consumer_request_at =
        std::chrono::steady_clock::now();
    should_log = true;
  }

  if (should_log && VfsTracingEnabled()) {
    REXLOG_INFO("Refii VFS first post-frontend-consumer asset request api={} "
                "asset={} hash=0x{:08X} lr=0x{:08X} handoff=[{}] "
                "main_menu=[{}]",
                api,
                normalized,
                resource_hash,
                lr,
                FormatRefiiPostVideoHandoffTraceForLog(),
                FormatRefiiMainMenuBlockerTraceForLog());
  }
}

uint32_t GetNativeResourceManagerForVfsPackagePrime(uint8_t* base) {
  if (base == nullptr) {
    return 0;
  }

  constexpr uint32_t kGlobalResourceManagerAnchor = 0x83496B08u;
  const uint32_t global_guest = LoadGuestU32(base, kGlobalResourceManagerAnchor);
  if (global_guest == 0) {
    return 0;
  }
  return LoadGuestU32(base, global_guest + 16);
}

}  // namespace detail
}  // namespace refii::vfs
