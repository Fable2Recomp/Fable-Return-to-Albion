#include "vfs_hooks_private.h"

namespace refii::vfs {
inline namespace detail {

bool ShouldAttemptPostVideoFrontendSyntheticHandoff() {
  const bool menu_texture_known_owned =
      RefiiMainMenuTextureKnownOwnedByVfsIndex();
  if (!RefiiLooseAssetBackendEnabled() ||
      (!RefiiMainMenuTraceNeedsFrontendHandoff() &&
       !menu_texture_known_owned)) {
    return false;
  }

  std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
  auto& trace = RefiiPostVideoHandoffTraceState();
  if (!RefiiStartupVideosSettledForPostVideoHandoffLocked(trace, nullptr)) {
    return false;
  }
  if (!SkipStartupVideosEnabled() && !menu_texture_known_owned &&
      !RefiiStartupReadyCompletionSeenForPostVideoHandoffLocked(trace)) {
    return false;
  }
  if (trace.frontend_consumer_seen ||
      trace.post_video_synthetic_handoff_attempts >= 6) {
    return false;
  }
  const auto now = std::chrono::steady_clock::now();
  if (trace.last_post_video_synthetic_handoff_attempt_at.time_since_epoch()
              .count() != 0 &&
      std::chrono::duration_cast<std::chrono::milliseconds>(
          now - trace.last_post_video_synthetic_handoff_attempt_at)
              .count() < 1000) {
    return false;
  }
  return true;
}

bool TryPrimeRefiiPostFrontendGameFacePackage(
    const PPCContext& source_ctx,
    uint8_t* base,
    const ActiveFrontendTextureConsumerTraceContext& consumer_trace,
    std::string_view mode);

bool MaybeSignalRefiiFrontendDeferredHashWakeHandoff(
    const PPCContext& source_ctx,
    uint8_t* base,
    std::string_view mode);

void MarkPostVideoFrontendSyntheticHandoffProbe(bool consumed_attempt) {
  std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
  auto& trace = RefiiPostVideoHandoffTraceState();
  trace.last_post_video_synthetic_handoff_attempt_at =
      std::chrono::steady_clock::now();
  if (consumed_attempt) {
    ++trace.post_video_synthetic_handoff_attempts;
  }
}

bool TryRunPostVideoFrontendSyntheticHandoff(PPCContext& source_ctx,
                                             uint8_t* base,
                                             uint32_t root_guest,
                                             std::string_view api) {
  if (base == nullptr || !ShouldAttemptPostVideoFrontendSyntheticHandoff()) {
    return false;
  }

  std::string owner_source;
  const uint32_t owner_guest =
      FindPostVideoFrontendTextureConsumerOwner(base, root_guest, &owner_source);
  bool synthetic_descriptors = false;
  bool generated_handoff = false;
  bool button_prime = false;
  bool frame_prime = false;
  bool ownerless_gameface_prime = false;
  bool ownerless_deferred_hash_wake = false;
  ActiveFrontendTextureConsumerTraceContext frontend_trace;
  MarkPostVideoFrontendSyntheticHandoffProbe(owner_guest != 0);
  if (owner_guest != 0) {
    BeginFrontendTextureConsumerTrace(owner_guest,
                                      static_cast<uint32_t>(source_ctx.lr),
                                      VfsTracingEnabled());
    synthetic_descriptors =
        TrySynthesizeFrontendTextureDescriptorPhase(source_ctx,
                                                   base,
                                                   owner_guest,
                                                   true,
                                                   false);
    if (synthetic_descriptors && kRunCollapsedFrontendGeneratedHandoff) {
      PPCContext handoff_ctx = source_ctx;
      handoff_ctx.r3.u32 = owner_guest;
      handoff_ctx.lr = 0x82378DB4u;
      generated_handoff =
          TryCollapseFrontendTextureGeneratedHandoff(
              handoff_ctx,
              base,
              false,
              "PostVideoFrontendSyntheticHandoff");
      if (generated_handoff) {
        TryRunFrontendTextureConsumerNativeUpdateForPendingVfsDirect(
            handoff_ctx,
            base,
            owner_guest,
            "PostVideoFrontendSyntheticHandoff");
      }
    }
    RecordFrontendTextureConsumerOwnerSlotSnapshot(base, owner_guest);
    frontend_trace = EndFrontendTextureConsumerTrace();
    RecordRefiiFrontendTextureConsumerVerdict(
        static_cast<uint32_t>(source_ctx.lr),
        synthetic_descriptors ? owner_guest : 0,
        frontend_trace,
        true);
  } else {
    RecordRefiiMainMenuBlockerEvent(
        "post_video_frontend_synthetic_handoff_owner_missing",
        fmt::format("api={} root=0x{:08X} owner_source={} main_menu=[{}]",
                    api,
                    root_guest,
                    owner_source,
                    FormatRefiiMainMenuBlockerTraceForLog()));
  }

  const bool ownerless_texture_already_parsed =
      owner_guest == 0 && RefiiMainMenuTraceHasParsedTextureForGeneratedReadyPayload();
  if (!ownerless_texture_already_parsed) {
    button_prime = TryPrimeRefiiFrontendTextureResource(
        source_ctx,
        base,
        0x73BCB48Fu,
        "PostVideoFrontendSyntheticHandoff_ButtonDetailPrime");
    frame_prime = TryPrimeRefiiFrontendTextureResource(
        source_ctx,
        base,
        0xC5A8E73Cu,
        "PostVideoFrontendSyntheticHandoff_FrameDetailPrime");
  } else if (owner_guest == 0) {
    ownerless_gameface_prime = TryPrimeRefiiPostFrontendGameFacePackage(
        source_ctx,
        base,
        frontend_trace,
        "ownerless_post_video_texture_parser");
    ownerless_deferred_hash_wake =
        MaybeSignalRefiiFrontendDeferredHashWakeHandoff(
            source_ctx, base, "ownerless_post_video_texture_parser");
  }

  const bool opened_any =
      synthetic_descriptors || generated_handoff || button_prime ||
      frame_prime || ownerless_gameface_prime || ownerless_deferred_hash_wake;
  RecordRefiiMainMenuBlockerEvent(
      "post_video_frontend_synthetic_handoff",
      fmt::format("api={} root=0x{:08X} owner=0x{:08X} owner_source={} "
                  "synthetic_descriptors={} generated_handoff={} "
                  "button_prime={} frame_prime={} "
                  "ownerless_texture_already_parsed={} "
                  "ownerless_gameface_prime={} "
                  "ownerless_deferred_hash_wake={} consumer_trace=[{}] "
                  "main_menu=[{}]",
                  api,
                  root_guest,
                  owner_guest,
                  owner_source,
                  synthetic_descriptors ? "true" : "false",
                  generated_handoff ? "true" : "false",
                  button_prime ? "true" : "false",
                  frame_prime ? "true" : "false",
                  ownerless_texture_already_parsed ? "true" : "false",
                  ownerless_gameface_prime ? "true" : "false",
                  ownerless_deferred_hash_wake ? "true" : "false",
                  FormatFrontendTextureConsumerTraceForLog(frontend_trace),
                  FormatRefiiMainMenuBlockerTraceForLog()));
  if (VfsTracingEnabled() || LooseAssetOverrideTracingEnabled()) {
    REXLOG_INFO("Refii VFS post-video frontend synthetic handoff api={} "
                "lr=0x{:08X} root=0x{:08X} owner=0x{:08X} owner_source={} "
                "opened={} synthetic_descriptors={} generated_handoff={} "
                "button_prime={} frame_prime={} "
                "ownerless_texture_already_parsed={} "
                "ownerless_gameface_prime={} "
                "ownerless_deferred_hash_wake={} owner_words={} "
                "consumer_trace=[{}] handoff=[{}] main_menu=[{}]",
                api,
                static_cast<uint32_t>(source_ctx.lr),
                root_guest,
                owner_guest,
                owner_source,
                opened_any ? "true" : "false",
                synthetic_descriptors ? "true" : "false",
                generated_handoff ? "true" : "false",
                button_prime ? "true" : "false",
                frame_prime ? "true" : "false",
                ownerless_texture_already_parsed ? "true" : "false",
                ownerless_gameface_prime ? "true" : "false",
                ownerless_deferred_hash_wake ? "true" : "false",
                FormatGuestWordsIfPointer(base, owner_guest, 24),
                FormatFrontendTextureConsumerTraceForLog(frontend_trace),
                FormatRefiiPostVideoHandoffTraceForLog(),
                FormatRefiiMainMenuBlockerTraceForLog());
  }
  LogRefiiVfsChainVerdict("frontend_post_video",
                          static_cast<uint32_t>(source_ctx.lr),
                          "menu_texture_refii_owned",
                          "",
                          "",
                          owner_guest != 0 ? "frontend_texture_descriptor_consumer"
                                           : "synthetic_texture_object",
                          "post_video_frontend_synthetic_handoff",
                          opened_any ? "Opened" : "TraceOnce",
                          opened_any ? "GameFace_or_texture_parser"
                                     : "frontend_consumer_or_resource_open");
  return opened_any;
}

std::string FrontendTextureConsumerNextBoundary(
    const ActiveFrontendTextureConsumerTraceContext& trace) {
  if (trace.descriptor_calls == 0) {
    return "descriptor_ctor_missing";
  }
  if (trace.resolver_calls == 0) {
    return "resolver_not_called";
  }
  if (trace.descriptor_slot16_rejected) {
    return "descriptor_vslot16_rejected";
  }
  if (trace.resolver_empty_refii_owned) {
    return "resolver_result_empty";
  }
  if (trace.store_pair_calls == 0 && trace.slot_store_calls == 0 &&
      trace.last_store_value == 0) {
    return "store_missing";
  }
  if (!trace.request_manager_seen && !trace.resource_manager_seen) {
    if (trace.any_refii_owned && !trace.static_table_list_committed &&
        trace.last_descriptor_lr == 0x82378DA8u) {
      return "static_table_list_commit_missing";
    }
    if (trace.any_refii_owned && !trace.final_direct_slot_valid &&
        trace.static_table_list_committed) {
      return "final_direct_slot_missing";
    }
    if (trace.any_refii_owned && trace.static_table_list_committed &&
        trace.final_direct_slot_valid) {
      return trace.followon_frame_list_committed
                 ? "resource_open_missing_after_followon_frame"
                 : "followon_frame_descriptor_missing";
    }
    return "resource_open_missing";
  }
  return trace.texture_parser_seen ? "post_texture_parser_or_gameface"
                                   : "texture_parser_missing";
}

std::string FormatFrontendTextureConsumerTraceForLog(
    const ActiveFrontendTextureConsumerTraceContext& trace) {
  return fmt::format(
      "owner=0x{:08X} root_lr=0x{:08X} descriptors={} resolvers={} "
      "store_pairs={} slot_stores={} last_lr=0x{:08X} last_hash=0x{:08X} "
      "last_desc=0x{:08X} vtable=0x{:08X} slot12=0x{:08X} "
      "slot16=0x{:08X} table_index={} table_object=0x{:08X} "
      "last_result=0x{:08X} last_store_dest=0x{:08X} last_store_value=0x{:08X} "
      "any_refii_owned={} resource_manager_seen={} request_manager_seen={} "
      "texture_parser_seen={} "
      "resolver_empty_refii_owned={} static_table_list_committed={} "
      "final_direct_slot_valid={} followon_frame_list_committed={} "
      "generated_next={} descriptors=[{}] resolvers=[{}] stores=[{}] "
      "owner_slots=[{}]",
      trace.owner_guest,
      trace.root_lr,
      trace.descriptor_calls,
      trace.resolver_calls,
      trace.store_pair_calls,
      trace.slot_store_calls,
      trace.last_descriptor_lr,
      trace.last_descriptor_hash,
      trace.last_descriptor_guest,
      trace.last_descriptor_vtable,
      trace.last_descriptor_slot12,
      trace.last_descriptor_slot16,
      trace.last_resolver_table_index,
      trace.last_resolver_table_object,
      trace.last_resolved_object,
      trace.last_store_destination,
      trace.last_store_value,
      trace.any_refii_owned ? "true" : "false",
      trace.resource_manager_seen ? "true" : "false",
      trace.request_manager_seen ? "true" : "false",
      trace.texture_parser_seen ? "true" : "false",
      trace.resolver_empty_refii_owned ? "true" : "false",
      trace.static_table_list_committed ? "true" : "false",
      trace.final_direct_slot_valid ? "true" : "false",
      trace.followon_frame_list_committed ? "true" : "false",
      FrontendTextureConsumerNextBoundary(trace),
      trace.descriptor_summary,
      trace.resolver_summary,
      trace.store_summary,
      trace.owner_slot_summary);
}

bool RecordRefiiFrontendTextureConsumerVerdict(
    uint32_t lr,
    uint32_t result,
    const ActiveFrontendTextureConsumerTraceContext& consumer_trace,
    bool texture_owned_but_unopened) {
  std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
  auto& trace = RefiiPostVideoHandoffTraceState();
  trace.frontend_consumer_seen = true;
  trace.frontend_consumer_any_refii_owned =
      trace.frontend_consumer_any_refii_owned ||
      consumer_trace.any_refii_owned;
  trace.last_frontend_consumer_lr = lr;
  trace.last_frontend_consumer_result = result;
  trace.last_frontend_consumer_owner = consumer_trace.owner_guest;
  trace.last_frontend_consumer_next =
      FrontendTextureConsumerNextBoundary(consumer_trace);
  trace.last_frontend_consumer_descriptors = consumer_trace.descriptor_calls;
  trace.last_frontend_consumer_resolvers = consumer_trace.resolver_calls;
  trace.last_frontend_consumer_stores =
      consumer_trace.store_pair_calls + consumer_trace.slot_store_calls;
  trace.last_frontend_consumer_at = std::chrono::steady_clock::now();
  const bool should_emit =
      (consumer_trace.any_refii_owned || texture_owned_but_unopened) &&
      !trace.frontend_consumer_verdict_emitted;
  if (should_emit) {
    trace.frontend_consumer_verdict_emitted = true;
  }
  return should_emit;
}

bool MaybeSignalRefiiPostFrontendConsumerHandoff(
    const PPCContext& source_ctx,
    uint8_t* base,
    const ActiveFrontendTextureConsumerTraceContext& consumer_trace,
    std::string_view mode) {
  if (base == nullptr || !RefiiLooseAssetBackendEnabled() ||
      mode != "collapsed_descriptor_bundle" || !consumer_trace.any_refii_owned ||
      !consumer_trace.texture_parser_seen ||
      !consumer_trace.followon_frame_list_committed ||
      (!RefiiStartupVideosSettledForMenuHandoff() &&
       !SkipStartupVideosEnabled())) {
    return false;
  }

  uint32_t wait_handle = 0;
  uint32_t signal_handle = 0;
  uint32_t wait_object = 0;
  uint32_t handoff_guest = 0;
  uint32_t owner_guest = 0;
  uint32_t wait_lr = 0;
  uint32_t wait_result = 0;
  uint32_t current_handoff_guest = 0;
  uint32_t frame_owner_guest = 0;
  uint32_t frame_handoff_guest = 0;
  uint32_t frame_lr = 0;
  uint32_t frame_flag244 = 0;
  uint32_t requeue_source124 = 0xFFFFFFFFu;
  uint32_t requeue_source128 = 0xFFFFFFFFu;
  uint32_t requeue_source_lr = 0;
  double requeue_frame_time = 0.0;
  int64_t requeue_source_age_ms = -1;
  int64_t wait_age_ms = -1;
  int64_t frame_age_ms = -1;
  bool wait_signal_allowed = false;
  bool gameface_package_complete = false;
  bool use_frame_flag = false;
  bool use_wait_signal = false;
  bool requeue_idle_wait_producer = false;
  bool complete_requeued_wait_producer = false;
  bool signal_wait_handle_too = false;
  bool run_generated_frame_completion = false;
  bool should_signal = false;
  const char* signal_source = "none";
  std::string skip_reason;
  {
    std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
    auto& trace = RefiiPostVideoHandoffTraceState();
    if (trace.post_frontend_consumer_handoff_signal_emitted) {
      return false;
    }

    owner_guest = trace.last_wait_owner;
    handoff_guest = trace.last_wait_handoff;
    wait_object = trace.last_wait_object;
    wait_handle = trace.last_wait_handle;
    signal_handle = trace.last_wait_signal_handle;
    wait_lr = trace.last_wait_lr;
    wait_result = trace.last_wait_result;
    frame_owner_guest = trace.last_frame_owner;
    frame_handoff_guest = trace.last_frame_handoff;
    frame_lr = trace.last_frame_lr;
    frame_flag244 = trace.last_frame_flag244;
    if (trace.last_wait_at.time_since_epoch().count() != 0) {
      wait_age_ms =
          std::chrono::duration_cast<std::chrono::milliseconds>(
              std::chrono::steady_clock::now() - trace.last_wait_at)
              .count();
    }
    if (trace.last_frame_at.time_since_epoch().count() != 0) {
      frame_age_ms =
          std::chrono::duration_cast<std::chrono::milliseconds>(
              std::chrono::steady_clock::now() - trace.last_frame_at)
              .count();
    }
    if (signal_handle == 0) {
      signal_handle = wait_handle;
    }
    current_handoff_guest =
        LooksLikeGuestPointer(owner_guest) ? LoadGuestU32(base, owner_guest + 4) : 0;
    const bool frame_handoff_live =
        LooksLikeGuestPointer(frame_owner_guest) &&
        LooksLikeGuestPointer(frame_handoff_guest) &&
        LoadGuestU32(base, frame_owner_guest + 4) == frame_handoff_guest;
    const bool wait_handoff_live =
        signal_handle != 0 && LooksLikeGuestPointer(wait_object) &&
        LooksLikeGuestPointer(owner_guest) && LooksLikeGuestPointer(handoff_guest) &&
        current_handoff_guest == handoff_guest;
    const uint8_t pending_complete =
        wait_handoff_live ? LoadGuestU8(base, handoff_guest + 168) : 1;
    const uint32_t pending_source124 =
        wait_handoff_live ? LoadGuestU32(base, handoff_guest + 124) : 0xFFFFFFFFu;
    const uint32_t pending_source128 =
        wait_handoff_live ? LoadGuestU32(base, handoff_guest + 128) : 0xFFFFFFFFu;
    const bool wait_handoff_pending =
        wait_handoff_live && pending_complete == 0 &&
        (pending_source124 != 0xFFFFFFFFu ||
         pending_source128 != 0xFFFFFFFFu);
    wait_signal_allowed = wait_handoff_live && wait_handoff_pending;
    const bool wait_handoff_idle_complete =
        wait_handoff_live && !wait_handoff_pending &&
        RefiiStartupVideosSettledForPostVideoHandoffLocked(trace, nullptr);
    const bool wait_handoff_has_completed_result =
        wait_handoff_live && pending_complete != 0 &&
        pending_source124 != 0xFFFFFFFFu &&
        pending_source128 != 0xFFFFFFFFu;
    if (trace.last_nonidle_render_schedule_at.time_since_epoch().count() != 0) {
      requeue_source_age_ms =
          std::chrono::duration_cast<std::chrono::milliseconds>(
              std::chrono::steady_clock::now() -
              trace.last_nonidle_render_schedule_at)
              .count();
    }
    gameface_package_complete =
        (RefiiCompletedPostGameFacePackageMaskLocked(trace) & 0x7u) == 0x7u;
    // Only replay a render schedule while it is still the live handoff. The
    // native producer owns the BeginScene/dynamic-slot/free sequence; this
    // bridge only republishes the producer's source/result fields after Refii
    // has delivered the GameFace package that the generated path was waiting
    // on.
    const bool gameface_package_allows_stale_live_handoff =
        gameface_package_complete &&
        RefiiStartupVideosSettledForPostVideoHandoffLocked(trace, nullptr);
    const bool nonidle_schedule_fresh =
        requeue_source_age_ms < 0 || requeue_source_age_ms <= 5000 ||
        gameface_package_allows_stale_live_handoff;
    const bool wait_handoff_fresh =
        wait_age_ms < 0 || wait_age_ms <= 5000 ||
        gameface_package_allows_stale_live_handoff;
    const bool nonidle_schedule_matches_wait =
        wait_handoff_idle_complete &&
        wait_handoff_fresh &&
        trace.last_nonidle_render_schedule_owner == owner_guest &&
        trace.last_nonidle_render_schedule_handoff == handoff_guest &&
        trace.last_nonidle_render_schedule_source128 != 0xFFFFFFFFu &&
        trace.last_nonidle_render_schedule_source124 != 0xFFFFFFFFu &&
        nonidle_schedule_fresh;
    const bool frame_handoff_fresh =
        frame_handoff_live && (frame_age_ms < 0 || frame_age_ms <= 1000);
    const bool frame_handoff_known =
        LooksLikeGuestPointer(frame_owner_guest) &&
        LooksLikeGuestPointer(frame_handoff_guest) &&
        (frame_age_ms < 0 || frame_age_ms <= 120000);
    const bool frame_handoff_needs_completion_flag =
        frame_handoff_known &&
        LoadGuestU8(base, frame_handoff_guest + 233) == 0 &&
        LoadGuestU8(base, frame_handoff_guest + 244) == 0;
    if (frame_handoff_fresh) {
      trace.post_frontend_consumer_handoff_signal_emitted = true;
      trace.post_frontend_consumer_handoff_signal_handle = signal_handle;
      trace.post_frontend_consumer_handoff_signal_lr =
          static_cast<uint32_t>(source_ctx.lr);
      trace.post_frontend_consumer_handoff_signal_at =
          std::chrono::steady_clock::now();
      should_signal = true;
      use_frame_flag = true;
      use_wait_signal = wait_signal_allowed;
      signal_source =
          wait_signal_allowed ? "frame_and_wait_handoff" : "frame_handoff";
    } else if (wait_signal_allowed) {
      trace.post_frontend_consumer_handoff_signal_emitted = true;
      trace.post_frontend_consumer_handoff_signal_handle = signal_handle;
      trace.post_frontend_consumer_handoff_signal_lr =
          static_cast<uint32_t>(source_ctx.lr);
      trace.post_frontend_consumer_handoff_signal_at =
          std::chrono::steady_clock::now();
      should_signal = true;
      use_wait_signal = true;
      signal_source = "wait_handoff";
    } else if (trace.post_frontend_consumer_request_seen &&
               nonidle_schedule_matches_wait) {
      trace.post_frontend_consumer_handoff_signal_emitted = true;
      trace.post_frontend_consumer_handoff_signal_handle = signal_handle;
      trace.post_frontend_consumer_handoff_signal_lr =
          static_cast<uint32_t>(source_ctx.lr);
      trace.post_frontend_consumer_handoff_signal_at =
          std::chrono::steady_clock::now();
      should_signal = true;
      use_wait_signal = true;
      use_frame_flag = frame_handoff_needs_completion_flag;
      requeue_idle_wait_producer = true;
      complete_requeued_wait_producer = true;
      signal_wait_handle_too = true;
      requeue_source124 = trace.last_nonidle_render_schedule_source124;
      requeue_source128 = trace.last_nonidle_render_schedule_source128;
      requeue_source_lr = trace.last_nonidle_render_schedule_lr;
      requeue_frame_time = trace.last_nonidle_render_schedule_frame_time;
      // The collapsed Refii consumer bypasses the generated caller that would
      // normally return through sub_82389210 and retire the producer. Once the
      // same live handoff has been requeued and completed, run that generated
      // tail so owner+4 is cleared exactly as the native frame path expects.
      run_generated_frame_completion = true;
      signal_source = use_frame_flag
                          ? "frame_flag_and_idle_wait_requeued"
                          : "idle_wait_requeued_from_render_schedule";
    } else if (trace.post_frontend_consumer_request_seen &&
               wait_handoff_live &&
               RefiiStartupVideosSettledForPostVideoHandoffLocked(trace,
                                                                  nullptr)) {
      trace.post_frontend_consumer_handoff_signal_emitted = true;
      trace.post_frontend_consumer_handoff_signal_handle = signal_handle;
      trace.post_frontend_consumer_handoff_signal_lr =
          static_cast<uint32_t>(source_ctx.lr);
      trace.post_frontend_consumer_handoff_signal_at =
          std::chrono::steady_clock::now();
      should_signal = true;
      use_wait_signal = true;
      use_frame_flag = frame_handoff_needs_completion_flag;
      // Fable 2 render producers can publish small resource handles here, not
      // guest pointers. Once Refii has delivered the complete GameFace package
      // and the live handoff already contains a completed producer result,
      // run the generated 82389210-style tail to retire owner+4.
      run_generated_frame_completion =
          gameface_package_complete && wait_handoff_has_completed_result;
      signal_source =
          run_generated_frame_completion
              ? (use_frame_flag ? "frame_flag_wait_event_generated_tail"
                                : "post_frontend_wait_event_generated_tail")
              : (use_frame_flag ? "frame_flag_and_wait_event"
                                : "post_frontend_request_wait_event");
    } else if (trace.post_frontend_consumer_request_seen &&
               gameface_package_complete &&
               frame_handoff_known &&
               RefiiStartupVideosSettledForPostVideoHandoffLocked(trace,
                                                                  nullptr)) {
      trace.post_frontend_consumer_handoff_signal_emitted = true;
      trace.post_frontend_consumer_handoff_signal_handle = signal_handle;
      trace.post_frontend_consumer_handoff_signal_lr =
          static_cast<uint32_t>(source_ctx.lr);
      trace.post_frontend_consumer_handoff_signal_at =
          std::chrono::steady_clock::now();
      should_signal = true;
      use_frame_flag = true;
      use_wait_signal = false;
      signal_source = "stale_frame_handoff_after_gameface_package";
    } else if (trace.post_frontend_consumer_request_seen) {
      skip_reason = "post_frontend_request_seen_no_pending_handoff";
    } else if (wait_handoff_idle_complete) {
      skip_reason = "idle_complete_no_pending_handoff";
    } else if (wait_handoff_live && !wait_handoff_pending) {
      skip_reason = "wait_handoff_idle_complete";
    } else if (!frame_handoff_live) {
      skip_reason = "frame_handoff_missing";
    } else if (frame_age_ms > 1000) {
      skip_reason = "stale_frame_handoff";
    } else {
      skip_reason = "wait_handoff_mismatch";
    }
  }

  if (!should_signal) {
    if (VfsTracingEnabled()) {
      REXLOG_INFO("Refii VFS post-frontend handoff signal skipped "
                  "reason={} lr=0x{:08X} mode={} owner=0x{:08X} "
                  "handoff=0x{:08X} current_handoff=0x{:08X} "
                  "wait_object=0x{:08X} wait_handle=0x{:08X} "
                  "signal_handle=0x{:08X} wait_age_ms={} "
                  "frame_owner=0x{:08X} frame_handoff=0x{:08X} "
                  "frame_lr=0x{:08X} frame_age_ms={} frame_flag244=0x{:02X} "
                  "consumer_trace=[{}] handoff=[{}] main_menu=[{}]",
                  skip_reason.empty() ? "unknown" : skip_reason,
                  static_cast<uint32_t>(source_ctx.lr),
                  mode,
                  owner_guest,
                  handoff_guest,
                  current_handoff_guest,
                  wait_object,
                  wait_handle,
                  signal_handle,
                  wait_age_ms,
                  frame_owner_guest,
                  frame_handoff_guest,
                  frame_lr,
                  frame_age_ms,
                  frame_flag244,
                  FormatFrontendTextureConsumerTraceForLog(consumer_trace),
                  FormatRefiiPostVideoHandoffTraceForLog(),
                  FormatRefiiMainMenuBlockerTraceForLog());
    }
    return false;
  }

  const uint8_t before_244 =
      LooksLikeGuestPointer(frame_handoff_guest)
          ? LoadGuestU8(base, frame_handoff_guest + 244)
          : 0;
  if (use_frame_flag && LooksLikeGuestPointer(frame_handoff_guest)) {
    StoreGuestU8(base, frame_handoff_guest + 244, 1);
  }

  if (use_wait_signal && wait_handle != 0 && wait_handle != signal_handle) {
    signal_wait_handle_too = true;
  }

  uint8_t wait_complete_before = 0;
  uint8_t wait_complete_after = 0;
  uint8_t wait_dirty_before = 0;
  uint8_t wait_dirty_after = 0;
  uint8_t wait_flag244_before = 0;
  uint8_t wait_flag244_after = 0;
  uint32_t wait_result116_before = 0;
  uint32_t wait_result116_after = 0;
  uint32_t wait_frame120_before = 0;
  uint32_t wait_frame120_after = 0;
  uint32_t wait_source124 = 0;
  uint32_t wait_source128 = 0;
  uint32_t requeue_result = 0;
  if (use_wait_signal && LooksLikeGuestPointer(handoff_guest)) {
    wait_complete_before = LoadGuestU8(base, handoff_guest + 168);
    wait_dirty_before = LoadGuestU8(base, handoff_guest + 132);
    wait_flag244_before = LoadGuestU8(base, handoff_guest + 244);
    wait_result116_before = LoadGuestU32(base, handoff_guest + 116);
    wait_frame120_before = LoadGuestU32(base, handoff_guest + 120);
    wait_source124 = LoadGuestU32(base, handoff_guest + 124);
    wait_source128 = LoadGuestU32(base, handoff_guest + 128);

    if (requeue_idle_wait_producer) {
      PPCContext requeue_ctx = source_ctx;
      requeue_ctx.r3.u32 = owner_guest;
      requeue_ctx.r5.u32 = requeue_source128;
      requeue_ctx.r6.u32 = requeue_source124;
      requeue_ctx.f1.f64 = requeue_frame_time;
      ::__imp__sub_82388A88(requeue_ctx, base);
      requeue_result = requeue_ctx.r3.u32;
    }

    if (complete_requeued_wait_producer) {
      const uint32_t queued_frame120 = LoadGuestU32(base, handoff_guest + 120);
      if (queued_frame120 != requeue_source128) {
        StoreGuestU8(base, handoff_guest + 132, 1);
      }
      StoreGuestU32(base, handoff_guest + 120, requeue_source128);
      StoreGuestU32(base, handoff_guest + 116, requeue_source124);
      StoreGuestU8(base, handoff_guest + 168, 1);
    }

    // Match the generated render-producer contract: normal pending work wakes
    // the producer, while the collapsed VFS bridge may also publish the same
    // completion fields the producer writes before signaling +460.
    StoreGuestU8(base, handoff_guest + 244, 1);
    wait_complete_after = LoadGuestU8(base, handoff_guest + 168);
    wait_dirty_after = LoadGuestU8(base, handoff_guest + 132);
    wait_flag244_after = LoadGuestU8(base, handoff_guest + 244);
    wait_result116_after = LoadGuestU32(base, handoff_guest + 116);
    wait_frame120_after = LoadGuestU32(base, handoff_guest + 120);
  }

  PPCContext signal_ctx = source_ctx;
  signal_ctx.r3.u32 = 0;
  if (use_wait_signal) {
    signal_ctx.r3.u32 = signal_handle;
    ::__imp__sub_822C9388(signal_ctx, base);
  }
  PPCContext wait_signal_ctx = source_ctx;
  wait_signal_ctx.r3.u32 = 0;
  if (signal_wait_handle_too) {
    wait_signal_ctx.r3.u32 = wait_handle;
    ::__imp__sub_822C9388(wait_signal_ctx, base);
  }

  uint32_t generated_frame_completion_result = 0;
  uint32_t generated_frame_begin_scene_result = 0;
  uint32_t generated_frame_dynamic_object = 0;
  uint32_t generated_frame_dynamic_vtable = 0;
  uint32_t generated_frame_dynamic_slot = 0;
  uint32_t generated_frame_dynamic_result = 0;
  uint32_t generated_frame_wait_object = 0;
  uint32_t generated_frame_teardown_result = 0;
  uint32_t generated_frame_free_result = 0;
  uint32_t generated_frame_owner_after = 0;
  bool generated_frame_dynamic_called = false;
  uint8_t generated_frame_flag233_before = 0;
  uint8_t generated_frame_flag244_before = 0;
  uint8_t generated_frame_flag233_after = 0;
  uint8_t generated_frame_flag244_after = 0;
  std::string wait_producer_log_state;
  std::string frame_producer_log_state;
  if (run_generated_frame_completion && LooksLikeGuestPointer(owner_guest) &&
      LooksLikeGuestPointer(handoff_guest) &&
      LoadGuestU32(base, owner_guest + 4) == handoff_guest) {
    generated_frame_flag233_before = LoadGuestU8(base, handoff_guest + 233);
    generated_frame_flag244_before = LoadGuestU8(base, handoff_guest + 244);
    wait_producer_log_state =
        FormatPostVideoRenderProducerState(base, handoff_guest);
    frame_producer_log_state =
        FormatPostVideoRenderProducerState(base, frame_handoff_guest);

    PPCContext begin_scene_ctx = source_ctx;
    begin_scene_ctx.lr = 0x823892F0u;
    begin_scene_ctx.r3.u32 = owner_guest;
    ::__imp__rex_lhRenderer_BeginScene(begin_scene_ctx, base);
    generated_frame_begin_scene_result = begin_scene_ctx.r3.u32;

    PPCContext wait_ctx = source_ctx;
    generated_frame_wait_object = owner_guest;
    generated_frame_dynamic_object = LoadGuestU32(base, handoff_guest + 240);
    if (LooksLikeGuestPointer(generated_frame_dynamic_object)) {
      generated_frame_dynamic_vtable =
          LoadGuestU32(base, generated_frame_dynamic_object);
      generated_frame_dynamic_slot =
          LooksLikeGuestPointer(generated_frame_dynamic_vtable)
              ? LoadGuestU32(base, generated_frame_dynamic_vtable + 16)
              : 0;
      PPCFunc* dynamic_slot_function =
          FindGuestFunction(generated_frame_dynamic_slot);
      if (dynamic_slot_function != nullptr) {
        PPCContext dynamic_ctx = source_ctx;
        dynamic_ctx.lr = 0x82389304u;
        dynamic_ctx.r3.u32 = generated_frame_dynamic_object;
        dynamic_ctx.ctr.u32 = generated_frame_dynamic_slot;
        dynamic_slot_function(dynamic_ctx, base);
        generated_frame_dynamic_called = true;
        generated_frame_dynamic_result = dynamic_ctx.r3.u32;
        generated_frame_wait_object = dynamic_ctx.r3.u32;
        wait_ctx = dynamic_ctx;
      }
    }
    wait_ctx.lr = 0x82389308u;
    if (!LooksLikeGuestPointer(generated_frame_wait_object) ||
        LoadGuestU32(base, generated_frame_wait_object + 4) != handoff_guest) {
      generated_frame_wait_object = owner_guest;
      wait_ctx.r3.u32 = owner_guest;
    }
    ::__imp__sub_82388A18(wait_ctx, base);
    generated_frame_completion_result = wait_ctx.r3.u32;

    PPCContext teardown_ctx = source_ctx;
    teardown_ctx.r3.u32 = handoff_guest + 12;
    ::__imp__sub_82A41B00(teardown_ctx, base);
    generated_frame_teardown_result = teardown_ctx.r3.u32;

    PPCContext free_ctx = source_ctx;
    free_ctx.r3.u32 = handoff_guest;
    rex_lh_free(free_ctx, base);
    generated_frame_free_result = free_ctx.r3.u32;

    StoreGuestU32(base, owner_guest + 4, 0);
    generated_frame_owner_after = LoadGuestU32(base, owner_guest + 4);
    if (generated_frame_owner_after == handoff_guest) {
      generated_frame_flag233_after = LoadGuestU8(base, handoff_guest + 233);
      generated_frame_flag244_after = LoadGuestU8(base, handoff_guest + 244);
    } else {
      generated_frame_flag233_after = generated_frame_flag233_before;
      generated_frame_flag244_after = generated_frame_flag244_before;
    }

    RecordRefiiMainMenuBlockerEvent(
        "frontend_consumer_generated_frame_completion_tail",
        fmt::format("mode={} source={} owner=0x{:08X} handoff=0x{:08X} "
                    "begin_scene_result=0x{:08X} "
                    "dynamic_object=0x{:08X} dynamic_vtable=0x{:08X} "
                    "dynamic_slot=0x{:08X} dynamic_called={} "
                    "dynamic_result=0x{:08X} wait_object=0x{:08X} "
                    "result=0x{:08X} owner_plus4_after=0x{:08X} "
                    "teardown_result=0x{:08X} free_result=0x{:08X} "
                    "flag233=0x{:02X}->0x{:02X} "
                    "flag244=0x{:02X}->0x{:02X} frame_time={} "
                    "reason=generated_82389210_tail_with_dynamic_slot "
                    "main_menu=[{}]",
                    mode,
                    signal_source,
                    owner_guest,
                    handoff_guest,
                    generated_frame_begin_scene_result,
                    generated_frame_dynamic_object,
                    generated_frame_dynamic_vtable,
                    generated_frame_dynamic_slot,
                    generated_frame_dynamic_called ? "true" : "false",
                    generated_frame_dynamic_result,
                    generated_frame_wait_object,
                    generated_frame_completion_result,
                    generated_frame_owner_after,
                    generated_frame_teardown_result,
                    generated_frame_free_result,
                    generated_frame_flag233_before,
                    generated_frame_flag233_after,
                    generated_frame_flag244_before,
                    generated_frame_flag244_after,
                    requeue_frame_time,
                    FormatRefiiMainMenuBlockerTraceForLog()));
  }
  if (wait_producer_log_state.empty()) {
    wait_producer_log_state =
        FormatPostVideoRenderProducerState(base, handoff_guest);
  }
  if (frame_producer_log_state.empty()) {
    frame_producer_log_state =
        FormatPostVideoRenderProducerState(base, frame_handoff_guest);
  }

  const bool loading_owner_finished_from_handoff =
      TryFinishRememberedRefiiLoadingOwnerFromPostFrontendSignal(
          source_ctx, base, "post_frontend_consumer_handoff_signal");
  PPCContext post_gameface_publish_ctx = source_ctx;
  const bool post_gameface_primary_published =
      gameface_package_complete &&
      (loading_owner_finished_from_handoff ||
       RefiiCollapsedLoadingSequenceReadyForOwnerRelease()) &&
      TryPublishRefiiPostGameFacePrimaryOwnerFromTrace(
          post_gameface_publish_ctx,
          base,
          "post_frontend_consumer_handoff_signal");
  const bool post_gameface_primary_ready =
      post_gameface_primary_published ||
      RefiiPostGameFacePrimaryOwnerReady(base);
  const bool post_gameface_primary_native_ready =
      post_gameface_primary_ready &&
      TryRunRefiiPostGameFacePrimaryOwnerNativeFromTrace(
          source_ctx,
          base,
          "post_gameface_package_handoff_after_publish");
  PPCContext post_gameface_frontend_ctx = source_ctx;
  const bool post_gameface_frontend_run =
      post_gameface_primary_ready &&
      TryRunRefiiPostGameFaceFrontendRenderRequestFromTrace(
          post_gameface_frontend_ctx,
          base,
          "post_gameface_package_handoff_signal");
  const bool post_gameface_post_loading_run =
      post_gameface_frontend_run &&
      TryRunRefiiPostLoadingFrontendRenderRequestNativeFromTrace(
          source_ctx,
          base,
          "post_gameface_package_handoff_signal");

  RecordRefiiMainMenuBlockerEvent(
      "frontend_consumer_handoff_signal",
      fmt::format("mode={} signal_source={} owner=0x{:08X} handoff=0x{:08X} "
                  "current_handoff=0x{:08X} "
                  "wait_object=0x{:08X} wait_handle=0x{:08X} "
                  "signal_handle=0x{:08X} "
                  "wait_lr=0x{:08X} wait_result=0x{:08X} wait_age_ms={} "
                  "frame_owner=0x{:08X} frame_handoff=0x{:08X} "
                  "frame_lr=0x{:08X} frame_age_ms={} "
                  "use_frame_flag={} wait_signal_allowed={} "
                  "flag244_before=0x{:02X} flag244_after=0x{:02X} "
                  "wait_flag244=0x{:02X}->0x{:02X} "
                  "wait_complete=0x{:02X}->0x{:02X} "
                  "wait_dirty=0x{:02X}->0x{:02X} "
                  "wait_result116=0x{:08X}->0x{:08X} "
                  "wait_frame120=0x{:08X}->0x{:08X} "
                  "wait_source124=0x{:08X} wait_source128=0x{:08X} "
                  "requeue={} complete_requeue={} requeue_lr=0x{:08X} "
                  "requeue_source=0x{:08X}/0x{:08X} "
                  "requeue_age_ms={} requeue_result=0x{:08X} "
                  "gameface_package_complete={} "
                  "generated_frame_completion={} "
                  "generated_frame_dynamic_object=0x{:08X} "
                  "generated_frame_dynamic_slot=0x{:08X} "
                  "generated_frame_dynamic_called={} "
                  "generated_frame_dynamic_result=0x{:08X} "
                  "generated_frame_wait_object=0x{:08X} "
                  "generated_frame_result=0x{:08X} "
                  "generated_frame_teardown=0x{:08X} "
                  "generated_frame_free=0x{:08X} "
                  "generated_frame_owner_after=0x{:08X} "
                  "generated_frame_flags=0x{:02X}/0x{:02X}->0x{:02X}/0x{:02X} "
                  "loading_owner_finished={} "
                  "post_gameface_primary_published={} "
                  "post_gameface_primary_ready={} "
                  "post_gameface_primary_native_ready={} "
                  "post_gameface_frontend_run={} "
                  "post_gameface_post_loading_run={} "
                  "signal_result=0x{:08X} wait_signal_result=0x{:08X}",
                  mode,
                  signal_source,
                  owner_guest,
                  handoff_guest,
                  current_handoff_guest,
                  wait_object,
                  wait_handle,
                  signal_handle,
                  wait_lr,
                  wait_result,
                  wait_age_ms,
                  frame_owner_guest,
                  frame_handoff_guest,
                  frame_lr,
                  frame_age_ms,
                  use_frame_flag ? "true" : "false",
                  wait_signal_allowed ? "true" : "false",
                  before_244,
                  LooksLikeGuestPointer(frame_handoff_guest)
                      ? LoadGuestU8(base, frame_handoff_guest + 244)
                      : 0,
                  wait_flag244_before,
                  wait_flag244_after,
                  wait_complete_before,
                  wait_complete_after,
                  wait_dirty_before,
                  wait_dirty_after,
                  wait_result116_before,
                  wait_result116_after,
                  wait_frame120_before,
                  wait_frame120_after,
                  wait_source124,
                  wait_source128,
                  requeue_idle_wait_producer ? "true" : "false",
                  complete_requeued_wait_producer ? "true" : "false",
                  requeue_source_lr,
                  requeue_source124,
                  requeue_source128,
                  requeue_source_age_ms,
                  requeue_result,
                  gameface_package_complete ? "true" : "false",
                  run_generated_frame_completion ? "true" : "false",
                  generated_frame_dynamic_object,
                  generated_frame_dynamic_slot,
                  generated_frame_dynamic_called ? "true" : "false",
                  generated_frame_dynamic_result,
                  generated_frame_wait_object,
                  generated_frame_completion_result,
                  generated_frame_teardown_result,
                  generated_frame_free_result,
                  generated_frame_owner_after,
                  generated_frame_flag233_before,
                  generated_frame_flag244_before,
                  generated_frame_flag233_after,
                  generated_frame_flag244_after,
                  loading_owner_finished_from_handoff ? "true" : "false",
                  post_gameface_primary_published ? "true" : "false",
                  post_gameface_primary_ready ? "true" : "false",
                  post_gameface_primary_native_ready ? "true" : "false",
                  post_gameface_frontend_run ? "true" : "false",
                  post_gameface_post_loading_run ? "true" : "false",
                  signal_ctx.r3.u32,
                  wait_signal_ctx.r3.u32));

  if (VfsTracingEnabled()) {
    REXLOG_INFO("Refii VFS post-frontend handoff signal "
                "mode={} source={} lr=0x{:08X} owner=0x{:08X} handoff=0x{:08X} "
                "current_handoff=0x{:08X} "
                "wait_object=0x{:08X} wait_handle=0x{:08X} "
                "signal_handle=0x{:08X} "
                "wait_lr=0x{:08X} wait_result=0x{:08X} wait_age_ms={} "
                "frame_owner=0x{:08X} frame_handoff=0x{:08X} "
                "frame_lr=0x{:08X} frame_age_ms={} "
                "use_frame_flag={} wait_signal_allowed={} "
                "flag244_before=0x{:02X} flag244_after=0x{:02X} "
                "wait_flag244=0x{:02X}->0x{:02X} "
                "wait_complete=0x{:02X}->0x{:02X} "
                "wait_dirty=0x{:02X}->0x{:02X} "
                "wait_result116=0x{:08X}->0x{:08X} "
                "wait_frame120=0x{:08X}->0x{:08X} "
                "wait_source124=0x{:08X} wait_source128=0x{:08X} "
                "requeue={} complete_requeue={} requeue_lr=0x{:08X} "
                "requeue_source=0x{:08X}/0x{:08X} "
                "requeue_age_ms={} requeue_result=0x{:08X} "
                "gameface_package_complete={} "
                "generated_frame_completion={} "
                "generated_frame_dynamic_object=0x{:08X} "
                "generated_frame_dynamic_slot=0x{:08X} "
                "generated_frame_dynamic_called={} "
                "generated_frame_dynamic_result=0x{:08X} "
                "generated_frame_wait_object=0x{:08X} "
                "generated_frame_result=0x{:08X} "
                "generated_frame_teardown=0x{:08X} "
                "generated_frame_free=0x{:08X} "
                "generated_frame_owner_after=0x{:08X} "
                "generated_frame_flags=0x{:02X}/0x{:02X}->0x{:02X}/0x{:02X} "
                "loading_owner_finished={} "
                "post_gameface_primary_published={} "
                "post_gameface_primary_ready={} "
                "post_gameface_primary_native_ready={} "
                "post_gameface_frontend_run={} "
                "post_gameface_post_loading_run={} "
                "wait_producer=[{}] frame_producer=[{}] "
                "signal_result=0x{:08X} "
                "wait_signal_result=0x{:08X} event_state=[{}] "
                "wait_event_state=[{}] handoff_trace=[{}] main_menu=[{}]",
                mode,
                signal_source,
                static_cast<uint32_t>(source_ctx.lr),
                owner_guest,
                handoff_guest,
                current_handoff_guest,
                wait_object,
                wait_handle,
                signal_handle,
                wait_lr,
                wait_result,
                wait_age_ms,
                frame_owner_guest,
                frame_handoff_guest,
                frame_lr,
                frame_age_ms,
                use_frame_flag ? "true" : "false",
                wait_signal_allowed ? "true" : "false",
                before_244,
                LooksLikeGuestPointer(frame_handoff_guest)
                    ? LoadGuestU8(base, frame_handoff_guest + 244)
                    : 0,
                wait_flag244_before,
                wait_flag244_after,
                wait_complete_before,
                wait_complete_after,
                wait_dirty_before,
                wait_dirty_after,
                wait_result116_before,
                wait_result116_after,
                wait_frame120_before,
                wait_frame120_after,
                wait_source124,
                wait_source128,
                requeue_idle_wait_producer ? "true" : "false",
                complete_requeued_wait_producer ? "true" : "false",
                requeue_source_lr,
                requeue_source124,
                requeue_source128,
                requeue_source_age_ms,
                requeue_result,
                gameface_package_complete ? "true" : "false",
                run_generated_frame_completion ? "true" : "false",
                generated_frame_dynamic_object,
                generated_frame_dynamic_slot,
                generated_frame_dynamic_called ? "true" : "false",
                generated_frame_dynamic_result,
                generated_frame_wait_object,
                generated_frame_completion_result,
                generated_frame_teardown_result,
                generated_frame_free_result,
                generated_frame_owner_after,
                generated_frame_flag233_before,
                generated_frame_flag244_before,
                generated_frame_flag233_after,
                generated_frame_flag244_after,
                loading_owner_finished_from_handoff ? "true" : "false",
                post_gameface_primary_published ? "true" : "false",
                post_gameface_primary_ready ? "true" : "false",
                post_gameface_primary_native_ready ? "true" : "false",
                post_gameface_frontend_run ? "true" : "false",
                post_gameface_post_loading_run ? "true" : "false",
                wait_producer_log_state,
                frame_producer_log_state,
                signal_ctx.r3.u32,
                wait_signal_ctx.r3.u32,
                FormatGeneratedEventSetState(base, signal_handle),
                FormatGeneratedEventSetState(base, wait_handle),
                FormatRefiiPostVideoHandoffTraceForLog(),
                FormatRefiiMainMenuBlockerTraceForLog());
    LogRefiiVfsChainVerdict("frontend_post_video",
                            static_cast<uint32_t>(source_ctx.lr),
                            "menu_texture_refii_owned",
                            "",
                            "",
                            "frontend_texture_descriptor_consumer",
                            "post_frontend_consumer_handoff_signal",
                            "Opened",
                            "post_ready_frame_or_gameface");
  }
  return true;
}


}  // namespace detail
}  // namespace refii::vfs
