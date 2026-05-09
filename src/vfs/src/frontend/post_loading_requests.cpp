#include "vfs_hooks_private.h"

namespace refii::vfs {
inline namespace detail {

constexpr uint32_t kAfterHolderChildOffset = 40u;
constexpr uint32_t kAfterHolderField44Offset = 44u;
constexpr uint32_t kAfterHolderModeOffset = 52u;

uint32_t LoadRefiiPostLoadingAfterHolderMode(uint8_t* base,
                                             uint32_t holder_guest) {
  return LoadGuestU32(base, holder_guest + kAfterHolderModeOffset);
}

void SetRefiiPostLoadingAfterHolderMode(const PPCContext& source_ctx,
                                        uint8_t* base,
                                        uint32_t holder_guest,
                                        uint32_t mode,
                                        uint32_t lr) {
  (void)source_ctx;
  (void)lr;
  if (base == nullptr || !LooksLikeGuestPointer(holder_guest)) {
    return;
  }

  // Generated sub_82467E40 only takes the holder lock and stores this mode.
  // The Refii VFS path owns these synthetic transitions, so mirror the state
  // write directly and avoid re-entering the native lock/wait path.
  StoreGuestU32(base, holder_guest + kAfterHolderModeOffset, mode);
}

std::mutex& RefiiInlineFrontendRenderRequestMutex() {
  static std::mutex mutex;
  return mutex;
}

std::unordered_set<uint32_t>& RefiiInlineFrontendRenderRequests() {
  static std::unordered_set<uint32_t> requests;
  return requests;
}

std::unordered_set<uint32_t>& RefiiCompletedInlineFrontendRenderRequests() {
  static std::unordered_set<uint32_t> requests;
  return requests;
}

std::unordered_set<uint32_t>& RefiiPostLoadingNativeFrontendRenderRuns() {
  static std::unordered_set<uint32_t> requests;
  return requests;
}

std::unordered_set<uint32_t>& RefiiPostLoadingFrontendRenderCommitRuns() {
  static std::unordered_set<uint32_t> requests;
  return requests;
}

std::unordered_set<uint32_t>&
RefiiPostLoadingCompletedRendererReentryRuns() {
  static std::unordered_set<uint32_t> requests;
  return requests;
}

std::unordered_set<uint32_t>& RefiiPostLoadingFrontendRenderProducerCompletions() {
  static std::unordered_set<uint32_t> handoffs;
  return handoffs;
}

std::unordered_set<uint32_t>& RefiiPostLoadingFrontendCompletionPublishes() {
  static std::unordered_set<uint32_t> requests;
  return requests;
}

std::unordered_set<uint32_t>& RefiiPostLoadingFrontendGeneratedRuns() {
  static std::unordered_set<uint32_t> requests;
  return requests;
}

std::unordered_set<uint32_t>& RefiiPostLoadingFrontendStateFinalizes() {
  static std::unordered_set<uint32_t> requests;
  return requests;
}

std::unordered_set<uint32_t>&
RefiiPostLoadingCompletedFrontendPrimaryAttaches() {
  static std::unordered_set<uint32_t> requests;
  return requests;
}

bool RefiiFrontendRenderRequestInlineAlreadyCompleted(uint32_t request_guest) {
  if (request_guest == 0) {
    return false;
  }
  std::lock_guard<std::mutex> lock(RefiiInlineFrontendRenderRequestMutex());
  return RefiiCompletedInlineFrontendRenderRequests().contains(request_guest);
}

std::mutex& RefiiPostGameFacePrimaryOwnerMutex();
std::unordered_set<uint32_t>& RefiiPostGameFacePrimaryOwnerRequests();
std::unordered_map<uint32_t, uint32_t>& RefiiPostGameFacePrimaryOwnerSlots();
std::atomic<uint32_t>& RefiiPostGameFacePrimaryOwnerRequest();

bool IsRefiiNativeSchedulerInlineSource(std::string_view source_api) {
  return source_api == "NativeVfsRetryInsert" ||
         source_api == "NativeVfsSchedulerInsertPending";
}

bool IsRefiiFrontendRenderSlotObject(uint8_t* base,
                                     uint32_t slot_guest,
                                     uint32_t request_guest) {
  return LooksLikeGuestPointer(slot_guest) &&
         LoadGuestU32(base, slot_guest) ==
             kNativeFrontendRenderRequestSlotVTable &&
         LoadGuestU32(base, slot_guest + 12) == request_guest;
}

uint32_t CreateRefiiFrontendRenderSlotObject(const PPCContext& source_ctx,
                                             uint8_t* base,
                                             uint32_t request_guest,
                                             std::string_view source_api) {
  if (!IsNativeFrontendRenderRequest(base, request_guest)) {
    return 0;
  }

  PPCContext alloc_ctx = source_ctx;
  const uint32_t slot_guest = CallLhMalloc(alloc_ctx, base, 16);
  if (!LooksLikeGuestPointer(slot_guest)) {
    return 0;
  }

  StoreGuestU32(base, slot_guest + 0, kNativeFrontendRenderRequestSlotVTable);
  StoreGuestU8(base, slot_guest + 4, 0);
  StoreGuestU8(base, slot_guest + 5, 0);
  StoreGuestU16(base, slot_guest + 6, 0);
  StoreGuestU32(base, slot_guest + 8, LoadGuestU32(base, request_guest + 20));
  StoreGuestU32(base, slot_guest + 12, request_guest);

  {
    std::lock_guard<std::mutex> lock(RefiiPostGameFacePrimaryOwnerMutex());
    RefiiPostGameFacePrimaryOwnerSlots()[request_guest] = slot_guest;
  }
  RefiiPostGameFacePrimaryOwnerRequest().store(request_guest,
                                               std::memory_order_relaxed);

  RecordRefiiMainMenuBlockerEvent(
      "frontend_render_request_slot_reconstructed",
      fmt::format("source={} request=0x{:08X} slot_object=0x{:08X} "
                  "state=[{}]",
                  source_api,
                  request_guest,
                  slot_guest,
                  FormatNativeVfsAsyncRequestState(base, request_guest)));
  if (VfsTracingEnabled() ||
      ShouldLogRepeatedVfsTraceOperation(
          "FrontendRenderRequestSlotReconstructed", 8)) {
    REXLOG_INFO("Refii VFS frontend render request slot reconstructed "
                "source={} request=0x{:08X} slot_object=0x{:08X} "
                "state=[{}] globals=[{}] handoff=[{}] main_menu=[{}]",
                source_api,
                request_guest,
                slot_guest,
                FormatNativeVfsAsyncRequestState(base, request_guest),
                FormatRefiiLoadingScreenSequenceGlobals(base),
                FormatRefiiPostVideoHandoffTraceForLog(),
                FormatRefiiMainMenuBlockerTraceForLog());
  }
  return slot_guest;
}

uint32_t ResolveRefiiFrontendRenderSlotObject(uint8_t* base,
                                              uint32_t request_guest,
                                              uint32_t secondary_owner) {
  if (!IsNativeFrontendRenderRequest(base, request_guest)) {
    return 0;
  }
  if (IsRefiiFrontendRenderSlotObject(base, secondary_owner, request_guest)) {
    return secondary_owner;
  }

  {
    std::lock_guard<std::mutex> lock(RefiiPostGameFacePrimaryOwnerMutex());
    const auto it = RefiiPostGameFacePrimaryOwnerSlots().find(request_guest);
    if (it != RefiiPostGameFacePrimaryOwnerSlots().end() &&
        IsRefiiFrontendRenderSlotObject(base, it->second, request_guest)) {
      return it->second;
    }
  }

  if (IsRefiiFrontendRenderSlotObject(base, request_guest, request_guest)) {
    return request_guest;
  }

  return 0;
}

bool IsRefiiPostGameFaceInlineCompletionSource(std::string_view source_api) {
  return IsRefiiNativeSchedulerInlineSource(source_api) ||
         source_api == "post_gameface_package_handoff_signal" ||
         source_api == "post_gameface_followup_boundary";
}

bool IsRefiiPostPrimaryCompletedInlineAttachSource(
    std::string_view source_api) {
  return source_api == "post_loading_frontend_render_handoff" ||
         source_api == "completed_loading_owner_renderer_reentry" ||
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
}

bool TryAttachCompletedRefiiFrontendRenderRequestToPrimaryOwner(
    const PPCContext& source_ctx,
    uint8_t* base,
    uint32_t request_guest,
    uint32_t slot_object_guest,
    std::string_view source_api) {
  if (!IsRefiiPostPrimaryCompletedInlineAttachSource(source_api) ||
      !RefiiPostGameFacePackageHandoffSignalEmitted() ||
      !RefiiPostGameFacePrimaryOwnerReady(base) ||
      !RefiiPostVideoFrontendChainConsumed() ||
      !RefiiMainMenuTraceHasParsedTextureForGeneratedReadyPayload()) {
    return false;
  }

  uint32_t primary_request = 0;
  uint32_t render_owner = 0;
  uint32_t render_handoff = 0;
  if (!TryGetRefiiPostGameFacePrimaryRenderRequest(
          base, &primary_request, &render_owner, &render_handoff) ||
      primary_request != request_guest || !LooksLikeGuestPointer(render_owner) ||
      !LooksLikeGuestPointer(render_handoff)) {
    return false;
  }

  {
    std::lock_guard<std::mutex> lock(RefiiInlineFrontendRenderRequestMutex());
    if (RefiiPostLoadingCompletedFrontendPrimaryAttaches().contains(
            request_guest)) {
      return false;
    }
  }

  constexpr uint32_t kLoadingGlobalBase = 0x83490000u;
  constexpr uint32_t kPrimaryOwnerGlobal = kLoadingGlobalBase + 27320u;
  const uint32_t published_owner = LoadGuestU32(base, kPrimaryOwnerGlobal);
  const uint8_t request_done_before = LoadGuestU8(base, request_guest + 92);
  const uint32_t request_owner_before = LoadGuestU32(base, request_guest + 132);
  const uint32_t render_owner_handoff_before =
      LoadGuestU32(base, render_owner + 4);
  const uint8_t handoff_complete_before =
      LoadGuestU8(base, render_handoff + 168);
  const uint8_t handoff_flag_before = LoadGuestU8(base, render_handoff + 244);

  PPCContext attach_ctx = source_ctx;
  attach_ctx.r3.u32 = render_owner;
  const bool attached =
      TryAttachRefiiPostGameFaceRenderHandoffToActiveFrame(
          attach_ctx,
          base,
          fmt::format("{}_completed_primary_attach", source_api));
  if (!attached) {
    return false;
  }

  if (request_done_before == 0) {
    StoreGuestU8(base, request_guest + 92, 1);
  }
  if (!LooksLikeGuestPointer(request_owner_before) &&
      LooksLikeGuestPointer(published_owner)) {
    StoreGuestU32(base, request_guest + 132, published_owner);
  }

  {
    std::lock_guard<std::mutex> lock(RefiiInlineFrontendRenderRequestMutex());
    RefiiPostLoadingCompletedFrontendPrimaryAttaches().insert(request_guest);
    RefiiCompletedInlineFrontendRenderRequests().insert(request_guest);
  }

  RecordRefiiMainMenuBlockerEvent(
      "frontend_render_request_completed_primary_attach",
      fmt::format("source={} request=0x{:08X} slot_object=0x{:08X} "
                  "render_owner=0x{:08X} handoff=0x{:08X} "
                  "render_owner_handoff=0x{:08X}->0x{:08X} "
                  "request_done=0x{:02X}->0x{:02X} "
                  "request_owner=0x{:08X}->0x{:08X} "
                  "handoff_complete=0x{:02X}->0x{:02X} "
                  "handoff_flag244=0x{:02X}->0x{:02X} state=[{}] "
                  "globals=[{}]",
                  source_api,
                  request_guest,
                  slot_object_guest,
                  render_owner,
                  render_handoff,
                  render_owner_handoff_before,
                  LoadGuestU32(base, render_owner + 4),
                  request_done_before,
                  LoadGuestU8(base, request_guest + 92),
                  request_owner_before,
                  LoadGuestU32(base, request_guest + 132),
                  handoff_complete_before,
                  LoadGuestU8(base, render_handoff + 168),
                  handoff_flag_before,
                  LoadGuestU8(base, render_handoff + 244),
                  FormatNativeVfsAsyncRequestState(base, request_guest),
                  FormatRefiiLoadingScreenSequenceGlobals(base)));

  if (VfsTracingEnabled() ||
      ShouldLogRepeatedVfsTraceOperation(
          "FrontendRenderRequestCompletedPrimaryAttach", 8)) {
    REXLOG_INFO("Refii VFS frontend render request completed primary attach "
                "source={} request=0x{:08X} slot_object=0x{:08X} "
                "render_owner=0x{:08X} handoff=0x{:08X} "
                "render_owner_handoff=0x{:08X}->0x{:08X} "
                "request_done=0x{:02X}->0x{:02X} "
                "request_owner=0x{:08X}->0x{:08X} "
                "handoff_complete=0x{:02X}->0x{:02X} "
                "handoff_flag244=0x{:02X}->0x{:02X} state=[{}] "
                "handoff_trace=[{}] main_menu=[{}]",
                source_api,
                request_guest,
                slot_object_guest,
                render_owner,
                render_handoff,
                render_owner_handoff_before,
                LoadGuestU32(base, render_owner + 4),
                request_done_before,
                LoadGuestU8(base, request_guest + 92),
                request_owner_before,
                LoadGuestU32(base, request_guest + 132),
                handoff_complete_before,
                LoadGuestU8(base, render_handoff + 168),
                handoff_flag_before,
                LoadGuestU8(base, render_handoff + 244),
                FormatNativeVfsAsyncRequestState(base, request_guest),
                FormatRefiiPostVideoHandoffTraceForLog(),
                FormatRefiiMainMenuBlockerTraceForLog());
  }

  LogRefiiVfsChainVerdict("frontend_post_video",
                          static_cast<uint32_t>(source_ctx.lr),
                          "frontend_render_request",
                          "",
                          "",
                          "post_gameface_primary_owner",
                          std::string(source_api),
                          "Opened",
                          "completed_request_attached_primary_handoff");
  return true;
}

bool TryCompleteRefiiInFlightFrontendRenderRequestFromNativeScheduler(
    const PPCContext& source_ctx,
    uint8_t* base,
    uint32_t request_guest,
    std::string_view source_api) {
  if (!IsRefiiPostGameFaceInlineCompletionSource(source_api) ||
      !RefiiPostGameFacePackageHandoffSignalEmitted() ||
      !RefiiPostGameFacePrimaryOwnerReady(base) ||
      !RefiiMainMenuTraceHasParsedTextureForGeneratedReadyPayload()) {
    return false;
  }

  uint32_t primary_request = 0;
  uint32_t primary_owner = 0;
  uint32_t primary_handoff = 0;
  if (!TryGetRefiiPostGameFacePrimaryRenderRequest(
          base, &primary_request, &primary_owner, &primary_handoff) ||
      primary_request != request_guest ||
      !LooksLikeGuestPointer(primary_owner) ||
      !LooksLikeGuestPointer(primary_handoff)) {
    return false;
  }

  const uint8_t request_done_before = LoadGuestU8(base, request_guest + 92);
  const uint32_t request_owner_before = LoadGuestU32(base, request_guest + 132);
  const uint32_t owner_handoff_before = LoadGuestU32(base, primary_owner + 4);
  const uint8_t handoff_complete_before =
      LoadGuestU8(base, primary_handoff + 168);
  const uint8_t handoff_flag_before = LoadGuestU8(base, primary_handoff + 244);

  PPCContext attach_ctx = source_ctx;
  attach_ctx.r3.u32 = primary_owner;
  const bool attached =
      TryAttachRefiiPostGameFaceRenderHandoffToActiveFrame(
          attach_ctx, base, source_api);
  if (!attached) {
    return false;
  }

  if (request_done_before == 0) {
    StoreGuestU8(base, request_guest + 92, 1);
  }
  if (!LooksLikeGuestPointer(request_owner_before)) {
    StoreGuestU32(base, request_guest + 132, primary_owner);
  }

  {
    std::lock_guard<std::mutex> lock(RefiiInlineFrontendRenderRequestMutex());
    RefiiCompletedInlineFrontendRenderRequests().insert(request_guest);
  }

  RecordRefiiMainMenuBlockerEvent(
      "frontend_render_request_scheduler_inflight_complete",
      fmt::format("source={} request=0x{:08X} owner=0x{:08X} "
                  "handoff=0x{:08X} owner_plus4=0x{:08X}->0x{:08X} "
                  "request_done=0x{:02X}->0x{:02X} "
                  "request_owner=0x{:08X}->0x{:08X} "
                  "handoff_complete=0x{:02X}->0x{:02X} "
                  "handoff_flag244=0x{:02X}->0x{:02X} state=[{}] "
                  "globals=[{}]",
                  source_api,
                  request_guest,
                  primary_owner,
                  primary_handoff,
                  owner_handoff_before,
                  LoadGuestU32(base, primary_owner + 4),
                  request_done_before,
                  LoadGuestU8(base, request_guest + 92),
                  request_owner_before,
                  LoadGuestU32(base, request_guest + 132),
                  handoff_complete_before,
                  LoadGuestU8(base, primary_handoff + 168),
                  handoff_flag_before,
                  LoadGuestU8(base, primary_handoff + 244),
                  FormatNativeVfsAsyncRequestState(base, request_guest),
                  FormatRefiiLoadingScreenSequenceGlobals(base)));

  if (VfsTracingEnabled() ||
      ShouldLogRepeatedVfsTraceOperation(
          "FrontendRenderRequestSchedulerInFlightComplete", 8)) {
    REXLOG_INFO("Refii VFS frontend render request scheduler in-flight "
                "complete source={} request=0x{:08X} owner=0x{:08X} "
                "handoff=0x{:08X} owner_plus4=0x{:08X}->0x{:08X} "
                "request_done=0x{:02X}->0x{:02X} "
                "request_owner=0x{:08X}->0x{:08X} "
                "handoff_complete=0x{:02X}->0x{:02X} "
                "handoff_flag244=0x{:02X}->0x{:02X} state=[{}] "
                "handoff_trace=[{}] main_menu=[{}]",
                source_api,
                request_guest,
                primary_owner,
                primary_handoff,
                owner_handoff_before,
                LoadGuestU32(base, primary_owner + 4),
                request_done_before,
                LoadGuestU8(base, request_guest + 92),
                request_owner_before,
                LoadGuestU32(base, request_guest + 132),
                handoff_complete_before,
                LoadGuestU8(base, primary_handoff + 168),
                handoff_flag_before,
                LoadGuestU8(base, primary_handoff + 244),
                FormatNativeVfsAsyncRequestState(base, request_guest),
                FormatRefiiPostVideoHandoffTraceForLog(),
                FormatRefiiMainMenuBlockerTraceForLog());
  }

  LogRefiiVfsChainVerdict("frontend_post_video",
                          static_cast<uint32_t>(source_ctx.lr),
                          "frontend_render_request",
                          "",
                          "",
                          "native_vfs_scheduler_request",
                          std::string(source_api),
                          "Opened",
                          "completed_inflight_scheduler_handoff");
  return true;
}

bool TryRunRefiiFrontendRenderRequestInline(PPCContext& source_ctx,
                                            uint8_t* base,
                                            uint32_t request_guest,
                                            std::string_view source_api,
                                            bool allow_ready_primary = false,
                                            bool skip_ready_probe = false) {
  if (!RefiiLooseAssetBackendEnabled() ||
      !IsNativeFrontendRenderRequest(base, request_guest) ||
      !RefiiStartupVideosReadyForMenuHandoff()) {
    return false;
  }

  constexpr uint32_t kLoadingGlobalBase = 0x83490000u;
  constexpr uint32_t kPrimaryOwnerGlobal = kLoadingGlobalBase + 27320u;
  constexpr uint32_t kPrimaryReadyGlobal = kLoadingGlobalBase + 27230u;
  constexpr uint32_t kSecondaryOwnerGlobal = kLoadingGlobalBase + 27328u;
  constexpr uint32_t kSecondaryReadyGlobal = kLoadingGlobalBase + 27332u;
  const uint32_t primary_owner = LoadGuestU32(base, kPrimaryOwnerGlobal);
  if (!allow_ready_primary && LooksLikeGuestPointer(primary_owner) &&
      LoadGuestU8(base, kPrimaryReadyGlobal) != 0) {
    return false;
  }
  if (!allow_ready_primary &&
      (!RefiiPostFrontendConsumerHandoffSignalEmitted() ||
       !RefiiPostGameFacePackageHandoffSignalEmitted())) {
    return false;
  }

  const uint32_t secondary_owner = LoadGuestU32(base, kSecondaryOwnerGlobal);
  uint32_t slot_object_guest =
      ResolveRefiiFrontendRenderSlotObject(base, request_guest, secondary_owner);
  if (!IsRefiiFrontendRenderSlotObject(base, slot_object_guest, request_guest) &&
      allow_ready_primary && skip_ready_probe &&
      RefiiOwnerlessPostVideoAfterHolderCleaned() &&
      RefiiPostVideoFrontendChainConsumed() &&
      RefiiPostGameFacePackageHandoffSignalEmitted()) {
    slot_object_guest = CreateRefiiFrontendRenderSlotObject(
        source_ctx, base, request_guest, source_api);
  }
  if (!IsRefiiFrontendRenderSlotObject(base, slot_object_guest, request_guest)) {
    if (VfsTracingEnabled() &&
        ShouldLogRepeatedVfsTraceOperation(
            "FrontendRenderRequestInlineBadSlotObject", 8)) {
      REXLOG_INFO("Refii VFS frontend render request inline skipped bad "
                  "slot object source={} request=0x{:08X} slot_object=0x{:08X} "
                  "secondary=0x{:08X} request_state=[{}] handoff=[{}] "
                  "main_menu=[{}]",
                  source_api,
                  request_guest,
                  slot_object_guest,
                  secondary_owner,
                  FormatNativeVfsAsyncRequestState(base, request_guest),
                  FormatRefiiPostVideoHandoffTraceForLog(),
                  FormatRefiiMainMenuBlockerTraceForLog());
    }
    return false;
  }

  if (IsRefiiNativeSchedulerInlineSource(source_api)) {
    const uint32_t secondary_before = LoadGuestU32(base, kSecondaryOwnerGlobal);
    const uint8_t secondary_ready_before =
        LoadGuestU8(base, kSecondaryReadyGlobal);
    {
      std::lock_guard<std::mutex> lock(RefiiPostGameFacePrimaryOwnerMutex());
      RefiiPostGameFacePrimaryOwnerSlots()[request_guest] = slot_object_guest;
    }
    RefiiPostGameFacePrimaryOwnerRequest().store(request_guest,
                                                std::memory_order_relaxed);
    if (!LooksLikeGuestPointer(secondary_before) ||
        LoadGuestU32(base, secondary_before + 12) != request_guest) {
      StoreGuestU32(base, kSecondaryOwnerGlobal, slot_object_guest);
    }
    if (RefiiPostVideoFrontendChainConsumed() &&
        RefiiPostGameFacePackageHandoffSignalEmitted()) {
      StoreGuestU8(base, kSecondaryReadyGlobal, 1);
    }
    if (VfsTracingEnabled() ||
        ShouldLogRepeatedVfsTraceOperation(
            "FrontendRenderRequestInlineRememberedSlot", 8)) {
      REXLOG_INFO("Refii VFS frontend render request remembered slot "
                  "source={} request=0x{:08X} slot_object=0x{:08X} "
                  "secondary=0x{:08X}->0x{:08X} ready=0x{:02X}->0x{:02X} "
                  "state=[{}] globals=[{}] handoff=[{}] main_menu=[{}]",
                  source_api,
                  request_guest,
                  slot_object_guest,
                  secondary_before,
                  LoadGuestU32(base, kSecondaryOwnerGlobal),
                  secondary_ready_before,
                  LoadGuestU8(base, kSecondaryReadyGlobal),
                  FormatNativeVfsAsyncRequestState(base, request_guest),
                  FormatRefiiLoadingScreenSequenceGlobals(base),
                  FormatRefiiPostVideoHandoffTraceForLog(),
                  FormatRefiiMainMenuBlockerTraceForLog());
    }

    if (RefiiCompletedLoadingScreenOwner().load(std::memory_order_relaxed) ==
        0) {
      if (VfsTracingEnabled() ||
          ShouldLogRepeatedVfsTraceOperation(
              "FrontendRenderRequestInlineSchedulerDeferred", 8)) {
        REXLOG_INFO("Refii VFS frontend render request scheduler deferred "
                    "source={} request=0x{:08X} slot_object=0x{:08X} "
                    "reason=loading_owner_not_completed state=[{}] "
                    "globals=[{}] handoff=[{}] main_menu=[{}]",
                    source_api,
                    request_guest,
                    slot_object_guest,
                    FormatNativeVfsAsyncRequestState(base, request_guest),
                    FormatRefiiLoadingScreenSequenceGlobals(base),
                    FormatRefiiPostVideoHandoffTraceForLog(),
                    FormatRefiiMainMenuBlockerTraceForLog());
      }
      return false;
    }
  }

  bool repeated_attempt = false;
  {
    std::lock_guard<std::mutex> lock(RefiiInlineFrontendRenderRequestMutex());
    if (RefiiCompletedInlineFrontendRenderRequests().contains(request_guest)) {
      repeated_attempt = true;
      if (VfsTracingEnabled() &&
          ShouldLogRepeatedVfsTraceOperation(
              "FrontendRenderRequestInlineAlreadyCompleted", 8)) {
        REXLOG_INFO("Refii VFS frontend render request inline already "
                    "completed source={} request=0x{:08X} slot_object=0x{:08X} "
                    "allow_ready_primary={} skip_ready_probe={} state=[{}] "
                    "handoff=[{}] main_menu=[{}]",
                    source_api,
                    request_guest,
                    slot_object_guest,
                    allow_ready_primary ? "true" : "false",
                    skip_ready_probe ? "true" : "false",
                    FormatNativeVfsAsyncRequestState(base, request_guest),
                    FormatRefiiPostVideoHandoffTraceForLog(),
                    FormatRefiiMainMenuBlockerTraceForLog());
      }
    } else {
      repeated_attempt =
          !RefiiInlineFrontendRenderRequests().insert(request_guest).second;
      if (repeated_attempt) {
        // The generated post-GameFace render run can re-enter the native VFS
        // scheduler while the outer inline request is still active. At that
        // point the scheduler has the exact request/handoff object we need, so
        // complete that VFS-owned handoff instead of leaving it to retry
        // forever.
      }
    }
  }
  if (repeated_attempt) {
    if (TryAttachCompletedRefiiFrontendRenderRequestToPrimaryOwner(
            source_ctx, base, request_guest, slot_object_guest, source_api)) {
      return true;
    }
    if (RefiiFrontendRenderRequestInlineAlreadyCompleted(request_guest)) {
      return true;
    }
    const bool after_holder_completion_source =
        skip_ready_probe && source_api == "post_loading_after_holder_823C1658";
    if (after_holder_completion_source &&
        LoadGuestU8(base, request_guest + 92) != 0) {
      const uint8_t secondary_ready_before =
          LoadGuestU8(base, kSecondaryReadyGlobal);
      if (IsRefiiFrontendRenderSlotObject(base, secondary_owner, request_guest)) {
        StoreGuestU8(base, kSecondaryReadyGlobal, 1);
      }
      {
        std::lock_guard<std::mutex> lock(
            RefiiInlineFrontendRenderRequestMutex());
        RefiiInlineFrontendRenderRequests().erase(request_guest);
        RefiiCompletedInlineFrontendRenderRequests().insert(request_guest);
      }
      RecordRefiiMainMenuBlockerEvent(
          "frontend_render_request_after_holder_native_complete",
          fmt::format("source={} request=0x{:08X} slot_object=0x{:08X} "
                      "secondary=0x{:08X} secondary_ready=0x{:02X}->0x{:02X} "
                      "state=[{}] globals=[{}]",
                      source_api,
                      request_guest,
                      slot_object_guest,
                      secondary_owner,
                      secondary_ready_before,
                      LoadGuestU8(base, kSecondaryReadyGlobal),
                      FormatNativeVfsAsyncRequestState(base, request_guest),
                      FormatRefiiLoadingScreenSequenceGlobals(base)));
      if (VfsTracingEnabled() ||
          ShouldLogRepeatedVfsTraceOperation(
              "FrontendRenderRequestAfterHolderNativeComplete", 8)) {
        REXLOG_INFO("Refii VFS frontend render request after-holder accepted "
                    "native-complete request source={} request=0x{:08X} "
                    "slot_object=0x{:08X} secondary=0x{:08X} "
                    "secondary_ready=0x{:02X}->0x{:02X} state=[{}] "
                    "handoff=[{}] main_menu=[{}]",
                    source_api,
                    request_guest,
                    slot_object_guest,
                    secondary_owner,
                    secondary_ready_before,
                    LoadGuestU8(base, kSecondaryReadyGlobal),
                    FormatNativeVfsAsyncRequestState(base, request_guest),
                    FormatRefiiPostVideoHandoffTraceForLog(),
                    FormatRefiiMainMenuBlockerTraceForLog());
      }
      return true;
    }
    if (TryCompleteRefiiInFlightFrontendRenderRequestFromNativeScheduler(
            source_ctx, base, request_guest, source_api)) {
      return true;
    }
    if (VfsTracingEnabled() &&
        ShouldLogRepeatedVfsTraceOperation(
            "FrontendRenderRequestInlineInFlight", 8)) {
      REXLOG_INFO("Refii VFS frontend render request inline skipped "
                  "in-flight request source={} request=0x{:08X} "
                  "slot_object=0x{:08X} allow_ready_primary={} "
                  "skip_ready_probe={} state=[{}] handoff=[{}] "
                  "main_menu=[{}]",
                  source_api,
                  request_guest,
                  slot_object_guest,
                  allow_ready_primary ? "true" : "false",
                  skip_ready_probe ? "true" : "false",
                  FormatNativeVfsAsyncRequestState(base, request_guest),
                  FormatRefiiPostVideoHandoffTraceForLog(),
                  FormatRefiiMainMenuBlockerTraceForLog());
    }
    return false;
  }

  if (VfsTracingEnabled() &&
      ShouldLogRepeatedVfsTraceOperation(
          "FrontendRenderRequestInlineBegin", 8)) {
    REXLOG_INFO("Refii VFS frontend render request inline begin "
                "source={} request=0x{:08X} slot_object=0x{:08X} "
                "allow_ready_primary={} skip_ready_probe={} state=[{}] "
                "handoff=[{}] main_menu=[{}]",
                source_api,
                request_guest,
                slot_object_guest,
                allow_ready_primary ? "true" : "false",
                skip_ready_probe ? "true" : "false",
                FormatNativeVfsAsyncRequestState(base, request_guest),
                FormatRefiiPostVideoHandoffTraceForLog(),
                FormatRefiiMainMenuBlockerTraceForLog());
  }

  const auto ready_begin = std::chrono::steady_clock::now();
  PPCContext ready_ctx = source_ctx;
  ready_ctx.r3.u32 = slot_object_guest;
  ready_ctx.lr = 0x8244B978u;
  if (skip_ready_probe) {
    ready_ctx.r3.u32 = slot_object_guest;
  } else {
    __imp__sub_8231AA98(ready_ctx, base);
  }
  const int64_t ready_ms =
      std::chrono::duration_cast<std::chrono::milliseconds>(
          std::chrono::steady_clock::now() - ready_begin)
          .count();
  if (ready_ctx.r3.u32 == 0) {
    std::lock_guard<std::mutex> lock(RefiiInlineFrontendRenderRequestMutex());
    RefiiInlineFrontendRenderRequests().erase(request_guest);
    if (VfsTracingEnabled() &&
        ShouldLogRepeatedVfsTraceOperation(
            "FrontendRenderRequestInlineReadyFalse", 8)) {
      REXLOG_INFO("Refii VFS frontend render request inline ready=false "
                  "source={} request=0x{:08X} slot_object=0x{:08X} "
                  "ready_ms={} skip_ready_probe={} state=[{}] "
                  "primary=0x{:08X}/{} handoff=[{}] main_menu=[{}]",
                  source_api,
                  request_guest,
                  slot_object_guest,
                  ready_ms,
                  skip_ready_probe ? "true" : "false",
                  FormatNativeVfsAsyncRequestState(base, request_guest),
                  primary_owner,
                  LoadGuestU8(base, kPrimaryReadyGlobal),
                  FormatRefiiPostVideoHandoffTraceForLog(),
                  FormatRefiiMainMenuBlockerTraceForLog());
    }
    return false;
  }

  if (skip_ready_probe) {
    const uint8_t request_done_before = LoadGuestU8(base, request_guest + 92);
    const uint32_t request_owner_before =
        LoadGuestU32(base, request_guest + 132);
    const uint32_t request_slot36_before =
        LoadGuestU32(base, request_guest + 36);
    const uint8_t secondary_ready_before =
        LoadGuestU8(base, kSecondaryReadyGlobal);
    const uint32_t secondary_owner_before =
        LoadGuestU32(base, kSecondaryOwnerGlobal);
    const uint32_t primary_before = LoadGuestU32(base, kPrimaryOwnerGlobal);
    const uint8_t primary_ready_before = LoadGuestU8(base, kPrimaryReadyGlobal);
    const bool generated_completion_source =
        source_api == "post_gameface_package_handoff_signal" ||
        source_api == "post_gameface_followup_boundary" ||
        source_api == "post_loading_frontend_render_handoff";
    const bool post_loading_handoff_before_owner_complete =
        source_api == "post_loading_frontend_render_handoff" &&
        RefiiCompletedLoadingScreenOwner().load(std::memory_order_relaxed) ==
            0;
    if (post_loading_handoff_before_owner_complete) {
      {
        std::lock_guard<std::mutex> lock(
            RefiiInlineFrontendRenderRequestMutex());
        RefiiInlineFrontendRenderRequests().erase(request_guest);
      }
      RecordRefiiMainMenuBlockerEvent(
          "frontend_render_request_post_loading_deferred",
          fmt::format("source={} request=0x{:08X} slot_object=0x{:08X} "
                      "reason=loading_owner_not_completed "
                      "request_done=0x{:02X} request_owner=0x{:08X} "
                      "primary=0x{:08X}/{} secondary=0x{:08X}/{} "
                      "globals=[{}]",
                      source_api,
                      request_guest,
                      slot_object_guest,
                      request_done_before,
                      request_owner_before,
                      primary_before,
                      primary_ready_before,
                      secondary_owner_before,
                      secondary_ready_before,
                      FormatRefiiLoadingScreenSequenceGlobals(base)));
      if (VfsTracingEnabled() ||
          ShouldLogRepeatedVfsTraceOperation(
              "FrontendRenderRequestPostLoadingDeferred", 8)) {
        REXLOG_INFO("Refii VFS frontend render request post-loading deferred "
                    "source={} request=0x{:08X} slot_object=0x{:08X} "
                    "reason=loading_owner_not_completed state=[{}] "
                    "globals=[{}] handoff=[{}] main_menu=[{}]",
                    source_api,
                    request_guest,
                    slot_object_guest,
                    FormatNativeVfsAsyncRequestState(base, request_guest),
                    FormatRefiiLoadingScreenSequenceGlobals(base),
                    FormatRefiiPostVideoHandoffTraceForLog(),
                    FormatRefiiMainMenuBlockerTraceForLog());
      }
      return false;
    }
    const bool can_run_generated_completion =
        generated_completion_source &&
        RefiiPostGameFacePackageHandoffSignalEmitted() &&
        request_done_before == 0 && secondary_ready_before != 0 &&
        secondary_owner_before == slot_object_guest &&
        LoadGuestU32(base, slot_object_guest + 12) == request_guest;

    if (can_run_generated_completion) {
      const auto run_begin = std::chrono::steady_clock::now();
      PPCContext run_ctx = source_ctx;
      run_ctx.r3.u32 = slot_object_guest;
      run_ctx.lr = 0x8244B978u;
      __imp__sub_8231AAA0(run_ctx, base);
      const int64_t run_ms =
          std::chrono::duration_cast<std::chrono::milliseconds>(
              std::chrono::steady_clock::now() - run_begin)
              .count();

      const uint8_t request_done_after_generated =
          LoadGuestU8(base, request_guest + 92);
      const uint32_t request_owner_after_generated =
          LoadGuestU32(base, request_guest + 132);
      const uint32_t request_slot36_after_generated =
          LoadGuestU32(base, request_guest + 36);
      const uint32_t secondary_owner_after_generated =
          LoadGuestU32(base, kSecondaryOwnerGlobal);
      const uint8_t secondary_ready_after_generated =
          LoadGuestU8(base, kSecondaryReadyGlobal);
      const uint32_t primary_after_generated =
          LoadGuestU32(base, kPrimaryOwnerGlobal);
      const uint8_t primary_ready_after_generated =
          LoadGuestU8(base, kPrimaryReadyGlobal);
      const bool generated_completion_changed_state =
          request_done_after_generated != request_done_before ||
          request_owner_after_generated != request_owner_before ||
          request_slot36_after_generated != request_slot36_before ||
          secondary_owner_after_generated != secondary_owner_before ||
          secondary_ready_after_generated != secondary_ready_before ||
          primary_after_generated != primary_before ||
          primary_ready_after_generated != primary_ready_before ||
          run_ctx.r3.u32 != slot_object_guest;

      RecordRefiiMainMenuBlockerEvent(
          "frontend_render_request_generated_completion",
          fmt::format("source={} request=0x{:08X} slot_object=0x{:08X} "
                      "ready_result=0x{:08X} run_result=0x{:08X} "
                      "ready_ms={} run_ms={} changed={} "
                      "request_done=0x{:02X}->0x{:02X} "
                      "request_owner=0x{:08X}->0x{:08X} "
                      "request_slot36=0x{:08X}->0x{:08X} "
                      "primary=0x{:08X}/{}->0x{:08X}/{} "
                      "secondary=0x{:08X}/{}->0x{:08X}/{} globals=[{}]",
                      source_api,
                      request_guest,
                      slot_object_guest,
                      ready_ctx.r3.u32,
                      run_ctx.r3.u32,
                      ready_ms,
                      run_ms,
                      generated_completion_changed_state ? "true" : "false",
                      request_done_before,
                      request_done_after_generated,
                      request_owner_before,
                      request_owner_after_generated,
                      request_slot36_before,
                      request_slot36_after_generated,
                      primary_before,
                      primary_ready_before,
                      primary_after_generated,
                      primary_ready_after_generated,
                      secondary_owner_before,
                      secondary_ready_before,
                      secondary_owner_after_generated,
                      secondary_ready_after_generated,
                      FormatRefiiLoadingScreenSequenceGlobals(base)));

      if (VfsTracingEnabled() ||
          ShouldLogRepeatedVfsTraceOperation(
              "FrontendRenderRequestInlineGeneratedCompletion", 8)) {
        REXLOG_INFO("Refii VFS frontend render request generated completion "
                    "source={} request=0x{:08X} slot_object=0x{:08X} "
                    "ready_result=0x{:08X} run_result=0x{:08X} ready_ms={} "
                    "run_ms={} changed={} state=[{}] globals=[{}] "
                    "handoff=[{}] main_menu=[{}]",
                    source_api,
                    request_guest,
                    slot_object_guest,
                    ready_ctx.r3.u32,
                    run_ctx.r3.u32,
                    ready_ms,
                    run_ms,
                    generated_completion_changed_state ? "true" : "false",
                    FormatNativeVfsAsyncRequestState(base, request_guest),
                    FormatRefiiLoadingScreenSequenceGlobals(base),
                    FormatRefiiPostVideoHandoffTraceForLog(),
                    FormatRefiiMainMenuBlockerTraceForLog());
      }

      if (generated_completion_changed_state) {
        std::lock_guard<std::mutex> lock(
            RefiiInlineFrontendRenderRequestMutex());
        RefiiInlineFrontendRenderRequests().erase(request_guest);
        RefiiCompletedInlineFrontendRenderRequests().insert(request_guest);
        LogRefiiVfsChainVerdict("frontend_post_video",
                                static_cast<uint32_t>(source_ctx.lr),
                                "frontend_render_request",
                                "",
                                "",
                                "native_vfs_scheduled_request",
                                std::string(source_api),
                                "Opened",
                                "generated_frontend_render_request_run");
        return true;
      }
    }

    StoreGuestU8(base, request_guest + 92, 1);
    if (LooksLikeGuestPointer(primary_before) &&
        !LooksLikeGuestPointer(request_owner_before)) {
      StoreGuestU32(base, request_guest + 132, primary_before);
    }
    StoreGuestU8(base, kSecondaryReadyGlobal, 0);
    if (LooksLikeGuestPointer(primary_before) && primary_ready_before == 0) {
      StoreGuestU8(base, kPrimaryReadyGlobal, 1);
    }

    {
      std::lock_guard<std::mutex> lock(RefiiInlineFrontendRenderRequestMutex());
      RefiiInlineFrontendRenderRequests().erase(request_guest);
      RefiiCompletedInlineFrontendRenderRequests().insert(request_guest);
    }

    const uint32_t primary_after = LoadGuestU32(base, kPrimaryOwnerGlobal);
    RecordRefiiMainMenuBlockerEvent(
        "frontend_render_request_completion_shim",
        fmt::format("source={} request=0x{:08X} slot_object=0x{:08X} "
                    "ready_result=0x{:08X} ready_ms={} "
                    "request_done=0x{:02X}->0x{:02X} "
                    "request_owner=0x{:08X}->0x{:08X} "
                    "primary=0x{:08X}/{}->0x{:08X}/{} "
                    "secondary=0x{:08X}/{}->{} globals=[{}]",
                    source_api,
                    request_guest,
                    slot_object_guest,
                    ready_ctx.r3.u32,
                    ready_ms,
                    request_done_before,
                    LoadGuestU8(base, request_guest + 92),
                    request_owner_before,
                    LoadGuestU32(base, request_guest + 132),
                    primary_before,
                    primary_ready_before,
                    primary_after,
                    LoadGuestU8(base, kPrimaryReadyGlobal),
                    secondary_owner,
                    secondary_ready_before,
                    LoadGuestU8(base, kSecondaryReadyGlobal),
                    FormatRefiiLoadingScreenSequenceGlobals(base)));

    if (VfsTracingEnabled() ||
        ShouldLogRepeatedVfsTraceOperation(
            "FrontendRenderRequestInlineCompletionShim", 8)) {
      REXLOG_INFO("Refii VFS frontend render request inline completion shim "
                  "source={} request=0x{:08X} slot_object=0x{:08X} "
                  "ready_result=0x{:08X} ready_ms={} state=[{}] "
                  "primary=0x{:08X}/{} secondary=0x{:08X}/{} "
                  "handoff=[{}] main_menu=[{}]",
                  source_api,
                  request_guest,
                  slot_object_guest,
                  ready_ctx.r3.u32,
                  ready_ms,
                  FormatNativeVfsAsyncRequestState(base, request_guest),
                  primary_after,
                  LoadGuestU8(base, kPrimaryReadyGlobal),
                  secondary_owner,
                  LoadGuestU8(base, kSecondaryReadyGlobal),
                  FormatRefiiPostVideoHandoffTraceForLog(),
                  FormatRefiiMainMenuBlockerTraceForLog());
    }

    LogRefiiVfsChainVerdict("frontend_post_video",
                            static_cast<uint32_t>(source_ctx.lr),
                            "frontend_render_request",
                            "",
                            "",
                            "native_vfs_scheduled_request",
                            std::string(source_api),
                            "Opened",
                            "vfs_completion_shim");
    return true;
  }

  const auto run_begin = std::chrono::steady_clock::now();
  PPCContext run_ctx = source_ctx;
  run_ctx.r3.u32 = slot_object_guest;
  run_ctx.lr = 0x8244B978u;
  __imp__sub_8231AAA0(run_ctx, base);
  const int64_t run_ms =
      std::chrono::duration_cast<std::chrono::milliseconds>(
          std::chrono::steady_clock::now() - run_begin)
          .count();

  {
    std::lock_guard<std::mutex> lock(RefiiInlineFrontendRenderRequestMutex());
    RefiiInlineFrontendRenderRequests().erase(request_guest);
    RefiiCompletedInlineFrontendRenderRequests().insert(request_guest);
  }

  if (VfsTracingEnabled() ||
      ShouldLogRepeatedVfsTraceOperation("FrontendRenderRequestInlineRun", 8)) {
    const uint32_t after_primary = LoadGuestU32(base, kPrimaryOwnerGlobal);
    REXLOG_INFO("Refii VFS frontend render request inline generated run "
                "source={} request=0x{:08X} slot_object=0x{:08X} "
                "ready_result=0x{:08X} run_result=0x{:08X} ready_ms={} "
                "run_ms={} skip_ready_probe={} state=[{}] primary=0x{:08X}/{} "
                "primary_words=[{}] handoff=[{}] main_menu=[{}]",
                source_api,
                request_guest,
                slot_object_guest,
                ready_ctx.r3.u32,
                run_ctx.r3.u32,
                ready_ms,
                run_ms,
                skip_ready_probe ? "true" : "false",
                FormatNativeVfsAsyncRequestState(base, request_guest),
                after_primary,
                LoadGuestU8(base, kPrimaryReadyGlobal),
                FormatGuestWordsIfPointer(base, after_primary, 12),
                FormatRefiiPostVideoHandoffTraceForLog(),
                FormatRefiiMainMenuBlockerTraceForLog());
  }
  LogRefiiVfsChainVerdict("frontend_post_video",
                          static_cast<uint32_t>(source_ctx.lr),
                          "frontend_render_request",
                          "",
                          "",
                          "native_vfs_scheduled_request",
                          std::string(source_api),
                          "Opened",
                          "generated_frontend_render_request_run");
  return true;
}

bool TryRunRefiiDeferredPostLoadingFrontendRequestOnRenderThread(
    PPCContext& source_ctx,
    uint8_t* base,
    std::string_view source_api) {
  const bool refii_after_holder_ready =
      RefiiCollapsedLoadingSequenceReadyForOwnerRelease() ||
      RefiiOwnerlessPostVideoVfsChainReadyForAfterHolder();
  if (base == nullptr || !RefiiLooseAssetBackendEnabled() ||
      !RefiiPostGameFacePackageHandoffSignalEmitted() ||
      !RefiiStartupVideosReadyForMenuHandoff() ||
      !refii_after_holder_ready) {
    return false;
  }

  constexpr uint32_t kLoadingGlobalBase = 0x83490000u;
  constexpr uint32_t kSecondaryOwnerGlobal = kLoadingGlobalBase + 27328u;
  constexpr uint32_t kSecondaryReadyGlobal = kLoadingGlobalBase + 27332u;
  const uint32_t secondary_owner = LoadGuestU32(base, kSecondaryOwnerGlobal);

  std::vector<uint32_t> candidates;
  if (LooksLikeGuestPointer(secondary_owner)) {
    candidates.push_back(LoadGuestU32(base, secondary_owner + 12));
  }
  candidates.push_back(
      RefiiPostGameFacePrimaryOwnerRequest().load(std::memory_order_relaxed));
  {
    std::lock_guard<std::mutex> lock(RefiiPostGameFacePrimaryOwnerMutex());
    for (const uint32_t published_request :
         RefiiPostGameFacePrimaryOwnerRequests()) {
      candidates.push_back(published_request);
    }
  }

  uint32_t request_guest = 0;
  bool request_completed = false;
  {
    std::lock_guard<std::mutex> lock(RefiiInlineFrontendRenderRequestMutex());
    for (const uint32_t candidate : candidates) {
      if (!IsNativeFrontendRenderRequest(base, candidate) ||
          !RefiiPostLoadingFrontendGeneratedRuns().contains(candidate)) {
        continue;
      }
      request_guest = candidate;
      request_completed =
          RefiiCompletedInlineFrontendRenderRequests().contains(candidate) ||
          LoadGuestU8(base, candidate + 92) != 0;
      if (request_completed) {
        RefiiInlineFrontendRenderRequests().erase(candidate);
        RefiiCompletedInlineFrontendRenderRequests().insert(candidate);
      }
      break;
    }
  }

  if (!IsNativeFrontendRenderRequest(base, request_guest)) {
    return false;
  }

  const uint32_t slot_object_guest =
      ResolveRefiiFrontendRenderSlotObject(base, request_guest, secondary_owner);
  if (request_completed) {
    const uint8_t request_ready_before = LoadGuestU8(base, request_guest + 92);
    const uint8_t secondary_ready_before =
        LoadGuestU8(base, kSecondaryReadyGlobal);
    const bool secondary_matches_request =
        IsRefiiFrontendRenderSlotObject(base, secondary_owner, request_guest);
    if (secondary_matches_request) {
      StoreGuestU8(base, request_guest + 92, 1);
      StoreGuestU8(base, kSecondaryReadyGlobal, 1);
    }
    if (VfsTracingEnabled() &&
        ShouldLogRepeatedVfsTraceOperation(
            "PostLoadingDeferredRenderThreadAlreadyCompleted", 8)) {
      REXLOG_INFO("Refii VFS post-loading deferred frontend request already "
                  "completed on render thread source={} request=0x{:08X} "
                  "slot_object=0x{:08X} secondary=0x{:08X} "
                  "secondary_matches_request={} request_ready=0x{:02X}->0x{:02X} "
                  "secondary_ready=0x{:02X}->0x{:02X} state=[{}] "
                  "globals=[{}] handoff=[{}] main_menu=[{}]",
                  source_api,
                  request_guest,
                  slot_object_guest,
                  secondary_owner,
                  secondary_matches_request ? "true" : "false",
                  request_ready_before,
                  LoadGuestU8(base, request_guest + 92),
                  secondary_ready_before,
                  LoadGuestU8(base, kSecondaryReadyGlobal),
                  FormatNativeVfsAsyncRequestState(base, request_guest),
                  FormatRefiiLoadingScreenSequenceGlobals(base),
                  FormatRefiiPostVideoHandoffTraceForLog(),
                  FormatRefiiMainMenuBlockerTraceForLog());
    }
    return true;
  }

  const std::string before_state =
      FormatNativeVfsAsyncRequestState(base, request_guest);
  const auto run_begin = std::chrono::steady_clock::now();
  const bool use_completion_shim =
      RefiiOwnerlessPostVideoAfterHolderCleaned() ||
      source_api == "post_loading_after_holder_823C1658";
  const bool ran = TryRunRefiiFrontendRenderRequestInline(source_ctx,
                                                          base,
                                                          request_guest,
                                                          source_api,
                                                          true,
                                                          use_completion_shim);
  const int64_t run_ms =
      std::chrono::duration_cast<std::chrono::milliseconds>(
          std::chrono::steady_clock::now() - run_begin)
          .count();
  const std::string after_state =
      FormatNativeVfsAsyncRequestState(base, request_guest);

  RecordRefiiMainMenuBlockerEvent(
      ran ? "post_loading_frontend_generated_request_render_thread"
          : "post_loading_frontend_generated_request_render_thread_skipped",
      fmt::format("source={} request=0x{:08X} slot_object=0x{:08X} "
                  "secondary=0x{:08X} ran={} run_ms={} before=[{}] "
                  "after=[{}] globals=[{}]",
                  source_api,
                  request_guest,
                  slot_object_guest,
                  secondary_owner,
                  ran ? "true" : "false",
                  run_ms,
                  before_state,
                  after_state,
                  FormatRefiiLoadingScreenSequenceGlobals(base)));

  if (VfsTracingEnabled() ||
      ShouldLogRepeatedVfsTraceOperation(
          "PostLoadingDeferredRenderThreadRun", 8)) {
    REXLOG_INFO("Refii VFS post-loading deferred frontend request render "
                "thread run source={} request=0x{:08X} slot_object=0x{:08X} "
                "secondary=0x{:08X} ran={} run_ms={} before=[{}] after=[{}] "
                "globals=[{}] handoff=[{}] main_menu=[{}]",
                source_api,
                request_guest,
                slot_object_guest,
                secondary_owner,
                ran ? "true" : "false",
                run_ms,
                before_state,
                after_state,
                FormatRefiiLoadingScreenSequenceGlobals(base),
                FormatRefiiPostVideoHandoffTraceForLog(),
                FormatRefiiMainMenuBlockerTraceForLog());
  }

  return ran;
}

std::string FormatRefiiPostLoadingAfterHolderState(uint8_t* base,
                                                   uint32_t holder_guest) {
  if (base == nullptr || !LooksLikeGuestPointer(holder_guest)) {
    return fmt::format("holder=0x{:08X}", holder_guest);
  }
  const uint32_t path_object = LoadGuestU32(base, holder_guest + 8);
  std::string path_state = fmt::format("path=0x{:08X}", path_object);
  if (LooksLikeGuestPointer(path_object)) {
    const std::string path_text = ReadMaybeGuestString(base, path_object, 512);
    path_state = fmt::format("path=0x{:08X} text={} words=[{}]",
                             path_object,
                             path_text.empty() ? "<unreadable>" : path_text,
                             FormatGuestWordsIfPointer(base, path_object, 8));
  }
  const uint32_t child = LoadGuestU32(base, holder_guest + kAfterHolderChildOffset);
  const uint32_t mode = LoadRefiiPostLoadingAfterHolderMode(base, holder_guest);
  const uint32_t field44 =
      LoadGuestU32(base, holder_guest + kAfterHolderField44Offset);
  const uint32_t list_head = LoadGuestU32(base, holder_guest + 100);
  const uint32_t active_count = LoadGuestU32(base, holder_guest + 104);
  const uint32_t tick = LoadGuestU32(base, holder_guest + 108);
  const uint32_t field112 = LoadGuestU32(base, holder_guest + 112);
  const uint32_t field116 = LoadGuestU32(base, holder_guest + 116);
  const uint8_t field120 = LoadGuestU8(base, holder_guest + 120);
  std::string child_state = "child=none";
  if (LooksLikeGuestPointer(child)) {
    child_state = fmt::format(
        "child=0x{:08X} child_ref=0x{:08X} child64=0x{:08X} "
        "flags220_223=0x{:02X}/0x{:02X}/0x{:02X}/0x{:02X} flags224=0x{:08X}",
        child,
        LoadGuestU32(base, child + 4),
        LoadGuestU32(base, child + 64),
        LoadGuestU8(base, child + 220),
        LoadGuestU8(base, child + 221),
        LoadGuestU8(base, child + 222),
        LoadGuestU8(base, child + 223),
        LoadGuestU32(base, child + 224));
  }
  return fmt::format(
      "holder=0x{:08X} mode={} child=0x{:08X} list=0x{:08X} active={} "
      "tick=0x{:08X} field44=0x{:08X} "
      "fields112_116=0x{:08X}/0x{:08X} "
      "flag120=0x{:02X} {} {} words=[{}]",
      holder_guest,
      mode,
      child,
      list_head,
      active_count,
      tick,
      field44,
      field112,
      field116,
      field120,
      child_state,
      path_state,
      FormatGuestWordsIfPointer(base, holder_guest, 32));
}

bool TryInitializeRefiiPostLoadingAfterHolderResourcePath(
    const PPCContext& source_ctx,
    uint8_t* base,
    uint32_t holder_guest,
    std::string_view source_api) {
  if (base == nullptr || !LooksLikeGuestPointer(holder_guest)) {
    return false;
  }
  const uint32_t existing_path = LoadGuestU32(base, holder_guest + 8);
  if (existing_path != 0) {
    const uint32_t existing_path_text =
        LooksLikeGuestPointer(existing_path) ? LoadGuestU32(base, existing_path)
                                             : 0;
    const std::string existing_text = ReadMaybeGuestString(
        base,
        LooksLikeGuestPointer(existing_path_text) ? existing_path_text
                                                  : existing_path,
        512);
    const std::string normalized_existing =
        NormalizeLooseAssetKey(existing_text);
    return normalized_existing.size() >= 4 &&
           normalized_existing.substr(normalized_existing.size() - 4) ==
               ".bik";
  }

  constexpr std::string_view kAfterHolderAsset =
      "data/art/videos/middlewarelogos.bik";
  PPCContext alloc_ctx = source_ctx;
  const uint32_t string_data_guest = CallLhMalloc(
      alloc_ctx, base, static_cast<uint32_t>(kAfterHolderAsset.size() + 1));
  if (string_data_guest == 0) {
    return false;
  }

  for (size_t index = 0; index < kAfterHolderAsset.size(); ++index) {
    StoreGuestU8(base,
                 string_data_guest + static_cast<uint32_t>(index),
                 static_cast<uint8_t>(kAfterHolderAsset[index]));
  }
  StoreGuestU8(
      base,
      string_data_guest + static_cast<uint32_t>(kAfterHolderAsset.size()),
      0);

  const std::string before_state =
      VfsTracingEnabled() ? FormatRefiiPostLoadingAfterHolderState(base,
                                                                   holder_guest)
                          : std::string{};
  PPCContext assign_path_ctx = source_ctx;
  assign_path_ctx.r3.u32 = holder_guest + 8;
  assign_path_ctx.r4.u32 = string_data_guest;
  assign_path_ctx.lr = 0x82467A68u;
  ::sub_82275438(assign_path_ctx, base);

  PPCContext assign_display_ctx = source_ctx;
  assign_display_ctx.r3.u32 = holder_guest + 12;
  assign_display_ctx.r4.u32 = string_data_guest;
  assign_display_ctx.lr = 0x82467A74u;
  ::sub_82275438(assign_display_ctx, base);

  StoreGuestU8(base, holder_guest + 136, 0);
  SetRefiiPostLoadingAfterHolderMode(
      source_ctx, base, holder_guest, 1, 0x82467A88u);

  PPCContext free_ctx = source_ctx;
  CallLhFree(free_ctx, base, string_data_guest);

  const uint32_t init_result =
      LoadGuestU32(base, holder_guest + 8) != 0 &&
              LoadRefiiPostLoadingAfterHolderMode(base, holder_guest) == 1
          ? 1u
          : 0u;
  const bool initialized = init_result != 0;
  RecordRefiiMainMenuBlockerEvent(
      initialized ? "post_loading_after_holder_path_initialized"
                  : "post_loading_after_holder_path_init_failed",
      fmt::format("source={} holder=0x{:08X} asset={} result=0x{:08X} "
                  "path=0x{:08X} mode={} before=[{}] after=[{}]",
                  source_api,
                  holder_guest,
                  kAfterHolderAsset,
                  init_result,
                  LoadGuestU32(base, holder_guest + 8),
                  LoadRefiiPostLoadingAfterHolderMode(base, holder_guest),
                  before_state,
                  FormatRefiiPostLoadingAfterHolderState(base, holder_guest)));
  if (VfsTracingEnabled() ||
      ShouldLogRepeatedVfsTraceOperation("PostLoadingAfterHolderPathInit", 8)) {
    REXLOG_INFO("Refii VFS post-loading after-holder path init "
                "source={} holder=0x{:08X} initialized={} asset={} "
                "result=0x{:08X} before=[{}] after=[{}] handoff=[{}] "
                "main_menu=[{}]",
                source_api,
                holder_guest,
                initialized ? "true" : "false",
                kAfterHolderAsset,
                init_result,
                before_state,
                FormatRefiiPostLoadingAfterHolderState(base, holder_guest),
                FormatRefiiPostVideoHandoffTraceForLog(),
                FormatRefiiMainMenuBlockerTraceForLog());
  }
  return initialized;
}

bool TryReturnRefiiPostLoadingAfterHolderSingletonForCompletedVfsChain(
    const PPCContext& source_ctx,
    uint8_t* base,
    uint32_t* out_holder_guest,
    std::string_view source_api) {
  if (out_holder_guest != nullptr) {
    *out_holder_guest = 0;
  }
  if (base == nullptr || !RefiiLooseAssetBackendEnabled() ||
      !RefiiPostGameFacePackageHandoffSignalEmitted() ||
      !RefiiStartupVideosReadyForMenuHandoff() ||
      !RefiiPostVfsChainReadyForLoadingOwnerRelease()) {
    return false;
  }

  constexpr uint32_t kAfterHolderSingleton = 0x834C20B8u;
  constexpr uint32_t kAfterHolderInitFlag = 0x834C3824u;
  const uint32_t init_flags = LoadGuestU32(base, kAfterHolderInitFlag);
  const bool final_handoff_ready =
      RefiiPostLoadingFrontendFinalHandoffWakeEmitted() &&
      (RefiiPostGameFacePackageComplete() ||
       RefiiPostGameFacePackageNativeFallbackReady()) &&
      RefiiMainMenuTraceHasParsedTextureForGeneratedReadyPayload();
  PPCContext singleton_ctx = source_ctx;
  if (final_handoff_ready) {
    singleton_ctx.r3.u32 = kAfterHolderSingleton;
  } else {
    ::__imp__sub_82180418(singleton_ctx, base);
  }
  const bool native_singleton_returned =
      singleton_ctx.r3.u32 == kAfterHolderSingleton;
  if (!native_singleton_returned && !final_handoff_ready) {
    return false;
  }
  const bool already_initialized = (init_flags & 1u) != 0;
  const uint32_t mode =
      LoadRefiiPostLoadingAfterHolderMode(base, kAfterHolderSingleton);
  const uint32_t child =
      LoadGuestU32(base, kAfterHolderSingleton + kAfterHolderChildOffset);
  if (RefiiPostLoadingAfterHolderAlreadyConsumed(base,
                                                 kAfterHolderSingleton)) {
    RecordRefiiMainMenuBlockerEvent(
        "post_loading_after_holder_terminal_singleton_consumed",
        fmt::format("source={} holder=0x{:08X} init_flags=0x{:08X} "
                    "native_result=0x{:08X} final_handoff_ready={} "
                    "state=[{}]",
                    source_api,
                    kAfterHolderSingleton,
                    init_flags,
                    singleton_ctx.r3.u32,
                    final_handoff_ready ? "true" : "false",
                    FormatRefiiPostLoadingAfterHolderState(
                        base, kAfterHolderSingleton)));
    if (VfsTracingEnabled() ||
        ShouldLogRepeatedVfsTraceOperation(
            "PostLoadingAfterHolderTerminalSingletonConsumed", 8)) {
      REXLOG_INFO("Refii VFS post-loading after-holder terminal singleton "
                  "consumed source={} holder=0x{:08X} init_flags=0x{:08X} "
                  "native_result=0x{:08X} final_handoff_ready={} "
                  "handoff=[{}] main_menu=[{}] state=[{}]",
                  source_api,
                  kAfterHolderSingleton,
                  init_flags,
                  singleton_ctx.r3.u32,
                  final_handoff_ready ? "true" : "false",
                  FormatRefiiPostVideoHandoffTraceForLog(),
                  FormatRefiiMainMenuBlockerTraceForLog(),
                  FormatRefiiPostLoadingAfterHolderState(
                      base, kAfterHolderSingleton));
    }
    return false;
  }
  if (already_initialized && mode == 0 && child == 0) {
    const bool path_initialized =
        TryInitializeRefiiPostLoadingAfterHolderResourcePath(
            source_ctx,
            base,
            kAfterHolderSingleton,
            source_api);
    if (out_holder_guest != nullptr) {
      *out_holder_guest = kAfterHolderSingleton;
    }
    RecordRefiiMainMenuBlockerEvent(
        path_initialized
            ? "post_loading_after_holder_terminal_singleton_seeded"
            : "post_loading_after_holder_terminal_singleton_reused",
        fmt::format("source={} holder=0x{:08X} init_flags=0x{:08X} "
                    "native_result=0x{:08X} final_handoff_ready={} "
                    "path_initialized={} state=[{}]",
                    source_api,
                    kAfterHolderSingleton,
                    init_flags,
                    singleton_ctx.r3.u32,
                    final_handoff_ready ? "true" : "false",
                    path_initialized ? "true" : "false",
                    FormatRefiiPostLoadingAfterHolderState(
                        base, kAfterHolderSingleton)));
    if (VfsTracingEnabled() ||
        ShouldLogRepeatedVfsTraceOperation(
            "PostLoadingAfterHolderTerminalSingletonReused", 8)) {
      REXLOG_INFO("Refii VFS post-loading after-holder terminal singleton {} "
                  "source={} holder=0x{:08X} init_flags=0x{:08X} "
                  "native_result=0x{:08X} final_handoff_ready={} "
                  "path_initialized={} handoff=[{}] main_menu=[{}] state=[{}]",
                  path_initialized ? "seeded" : "reused",
                  source_api,
                  kAfterHolderSingleton,
                  init_flags,
                  singleton_ctx.r3.u32,
                  final_handoff_ready ? "true" : "false",
                  path_initialized ? "true" : "false",
                  FormatRefiiPostVideoHandoffTraceForLog(),
                  FormatRefiiMainMenuBlockerTraceForLog(),
                  FormatRefiiPostLoadingAfterHolderState(
                      base, kAfterHolderSingleton));
    }
    if (path_initialized) {
      LogRefiiVfsChainVerdict("frontend_post_video",
                              static_cast<uint32_t>(source_ctx.lr),
                              "data/art/videos/middlewarelogos.bik",
                              "",
                              "",
                              "native_after_holder",
                              "post_loading_after_holder_terminal_seeded",
                              "Opened",
                              "middleware_video_or_main_menu_continue");
    }
    return true;
  }

  if (out_holder_guest != nullptr) {
    *out_holder_guest = kAfterHolderSingleton;
  }

  RecordRefiiMainMenuBlockerEvent(
      already_initialized ? "post_loading_after_holder_singleton_reused"
                          : "post_loading_after_holder_singleton_seeded",
      fmt::format("source={} holder=0x{:08X} init_flags=0x{:08X} "
                  "native_result=0x{:08X} final_handoff_ready={} state=[{}]",
                  source_api,
                  kAfterHolderSingleton,
                  init_flags,
                  singleton_ctx.r3.u32,
                  final_handoff_ready ? "true" : "false",
                  FormatRefiiPostLoadingAfterHolderState(
                      base, kAfterHolderSingleton)));
  if (VfsTracingEnabled() ||
      ShouldLogRepeatedVfsTraceOperation("PostLoadingAfterHolderSingleton",
                                         8)) {
    REXLOG_INFO("Refii VFS post-loading after-holder singleton {} "
                "source={} holder=0x{:08X} init_flags=0x{:08X}->0x{:08X} "
                "native_result=0x{:08X} final_handoff_ready={} "
                "handoff=[{}] main_menu=[{}] state=[{}]",
                already_initialized ? "reused" : "seeded",
                source_api,
                kAfterHolderSingleton,
                init_flags,
                LoadGuestU32(base, kAfterHolderInitFlag),
                singleton_ctx.r3.u32,
                final_handoff_ready ? "true" : "false",
                FormatRefiiPostVideoHandoffTraceForLog(),
                FormatRefiiMainMenuBlockerTraceForLog(),
                FormatRefiiPostLoadingAfterHolderState(
                    base, kAfterHolderSingleton));
  }
  LogRefiiVfsChainVerdict("frontend_post_video",
                          static_cast<uint32_t>(source_ctx.lr),
                          "post_loading_after_holder",
                          "",
                          "",
                          "native_after_holder",
                          already_initialized
                              ? "post_loading_after_holder_singleton_reused"
                              : "post_loading_after_holder_singleton_seeded",
                          "Opened",
                          "native_singleton_ready");
  return true;
}

std::atomic<bool>& RefiiPostLoadingMiddlewareVideoFullyRead() {
  static std::atomic<bool> fully_read{false};
  return fully_read;
}

bool IsRefiiPostLoadingMiddlewareVideoPath(std::string_view path) {
  const std::string key = NormalizeLooseAssetKey(path);
  return key == "art/videos/middlewarelogos.bik" ||
         key == "data/art/videos/middlewarelogos.bik";
}

bool RefiiPostLoadingMiddlewareVideoReadComplete() {
  return RefiiPostLoadingMiddlewareVideoFullyRead().load(
      std::memory_order_relaxed);
}

bool IsRefiiPostLoadingMiddlewareVideoHolder(uint8_t* base,
                                             uint32_t holder_guest) {
  constexpr uint32_t kAfterHolderSingleton = 0x834C20B8u;
  if (holder_guest == kAfterHolderSingleton) {
    return true;
  }
  if (base == nullptr || !LooksLikeGuestPointer(holder_guest)) {
    return false;
  }
  const uint32_t path_object = LoadGuestU32(base, holder_guest + 8);
  if (!LooksLikeGuestPointer(path_object)) {
    return false;
  }
  return IsRefiiPostLoadingMiddlewareVideoPath(
      ReadMaybeGuestString(base, path_object, 512));
}

bool TryTickRefiiPostLoadingMiddlewareAfterHolderNativeMode2(
    const PPCContext& source_ctx,
    uint8_t* base,
    uint32_t holder_guest,
    std::string_view source_api) {
  constexpr uint32_t kAfterHolderSingleton = 0x834C20B8u;
  if (base == nullptr || holder_guest != kAfterHolderSingleton ||
      !LooksLikeGuestPointer(holder_guest) ||
      RefiiPostLoadingMiddlewareVideoReadComplete() ||
      LoadRefiiPostLoadingAfterHolderMode(base, holder_guest) != 2 ||
      LoadGuestU32(base, holder_guest + kAfterHolderChildOffset) != 0) {
    return false;
  }

  const std::string before_state =
      FormatRefiiPostLoadingAfterHolderState(base, holder_guest);
  PPCContext native_ctx = source_ctx;
  native_ctx.r3.u32 = holder_guest;
  native_ctx.lr = static_cast<uint32_t>(source_ctx.lr);
  __imp__sub_82467B08(native_ctx, base);

  const std::string after_state =
      FormatRefiiPostLoadingAfterHolderState(base, holder_guest);
  RecordRefiiMainMenuBlockerEvent(
      "post_loading_after_holder_native_mode2_tick",
      fmt::format("source={} holder=0x{:08X} arg=0x{:08X} result=0x{:08X} "
                  "before=[{}] after=[{}]",
                  source_api,
                  holder_guest,
                  source_ctx.r4.u32,
                  native_ctx.r3.u32,
                  before_state,
                  after_state));
  if (VfsTracingEnabled() ||
      ShouldLogRepeatedVfsTraceOperation(
          "PostLoadingAfterHolderNativeMode2Tick", 16)) {
    REXLOG_INFO("Refii VFS post-loading after-holder native mode2 tick "
                "source={} holder=0x{:08X} arg=0x{:08X} result=0x{:08X} "
                "before=[{}] after=[{}] handoff=[{}] main_menu=[{}]",
                source_api,
                holder_guest,
                source_ctx.r4.u32,
                native_ctx.r3.u32,
                before_state,
                after_state,
                FormatRefiiPostVideoHandoffTraceForLog(),
                FormatRefiiMainMenuBlockerTraceForLog());
  }
  LogRefiiVfsChainVerdict("frontend_post_video",
                          static_cast<uint32_t>(source_ctx.lr),
                          "data/art/videos/middlewarelogos.bik",
                          "",
                          "",
                          "native_after_holder",
                          "post_loading_after_holder_native_mode2_tick",
                          "Opened",
                          "native_mode2_started_middleware_video");
  return true;
}

std::atomic<uint32_t>& RefiiPostLoadingAfterHolderMode5CleanedHolder() {
  static std::atomic<uint32_t> holder{0};
  return holder;
}

bool RefiiOwnerlessPostVideoAfterHolderCleaned() {
  return LooksLikeGuestPointer(
             RefiiPostLoadingAfterHolderMode5CleanedHolder().load(
                 std::memory_order_relaxed)) &&
         RefiiOwnerlessPostVideoVfsChainReadyForAfterHolder() &&
         RefiiPostLoadingMiddlewareVideoReadComplete() &&
         RefiiPostFrontendDeferredHashWakeSignalEmitted();
}

bool RefiiPostLoadingAfterHolderAlreadyConsumed(uint8_t* base,
                                                uint32_t holder_guest) {
  return base != nullptr && LooksLikeGuestPointer(holder_guest) &&
         RefiiPostLoadingAfterHolderMode5CleanedHolder().load(
             std::memory_order_relaxed) == holder_guest &&
         LoadRefiiPostLoadingAfterHolderMode(base, holder_guest) == 0 &&
         LoadGuestU32(base, holder_guest + kAfterHolderChildOffset) == 0 &&
         (RefiiPostGameFacePackageComplete() ||
          RefiiPostGameFacePackageNativeFallbackReady()) &&
         RefiiPostLoadingFrontendFinalHandoffWakeEmitted() &&
         RefiiMainMenuTraceHasParsedTextureForGeneratedReadyPayload();
}

bool TryFinalizeRefiiOwnerlessPostLoadingAfterHolderState(
    const PPCContext& source_ctx,
    uint8_t* base,
    uint32_t holder_guest,
    std::string_view source_api) {
  if (base == nullptr || !RefiiLooseAssetBackendEnabled() ||
      !RefiiPostGameFacePackageHandoffSignalEmitted() ||
      !RefiiPostFrontendDeferredHashWakeSignalEmitted() ||
      !RefiiStartupVideosReadyForMenuHandoff() ||
      !RefiiOwnerlessPostVideoVfsChainReadyForAfterHolder() ||
      !RefiiPostLoadingMiddlewareVideoReadComplete() ||
      !LooksLikeGuestPointer(holder_guest) ||
      LoadRefiiPostLoadingAfterHolderMode(base, holder_guest) != 0 ||
      LoadGuestU32(base, holder_guest + kAfterHolderChildOffset) != 0) {
    return false;
  }

  static std::atomic<uint32_t> last_finalized_holder{0};
  if (last_finalized_holder.exchange(holder_guest, std::memory_order_relaxed) ==
      holder_guest) {
    return true;
  }

  constexpr uint32_t kLoadingGlobalBase = 0x83490000u;
  constexpr uint32_t kPrimaryReadyGlobal = kLoadingGlobalBase + 27230u;
  constexpr uint32_t kPrimaryOwnerGlobal = kLoadingGlobalBase + 27320u;
  constexpr uint32_t kSecondaryOwnerGlobal = kLoadingGlobalBase + 27328u;
  constexpr uint32_t kSecondaryReadyGlobal = kLoadingGlobalBase + 27332u;
  constexpr uint32_t kLoadingPrimaryModeGlobal = kLoadingGlobalBase + 27392u;

  const uint8_t primary_ready_before = LoadGuestU8(base, kPrimaryReadyGlobal);
  const uint8_t secondary_ready_before = LoadGuestU8(base, kSecondaryReadyGlobal);
  const uint32_t primary_owner_before = LoadGuestU32(base, kPrimaryOwnerGlobal);
  const uint32_t secondary_owner_before =
      LoadGuestU32(base, kSecondaryOwnerGlobal);
  const uint32_t loading_mode_before =
      LoadGuestU32(base, kLoadingPrimaryModeGlobal);

  uint32_t render_owner = 0;
  uint32_t render_handoff = 0;
  uint32_t signal_handle = 0;
  uint32_t wait_handle = 0;
  uint32_t trace_wait_lr = 0;
  uint32_t trace_wait_result = 0;
  int64_t trace_wait_ms = -1;
  {
    std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
    const auto& trace = RefiiPostVideoHandoffTraceState();
    render_owner = LooksLikeGuestPointer(trace.last_wait_owner)
                       ? trace.last_wait_owner
                       : trace.last_nonidle_render_schedule_owner;
    render_handoff = LooksLikeGuestPointer(trace.last_wait_handoff)
                         ? trace.last_wait_handoff
                         : trace.last_nonidle_render_schedule_handoff;
    signal_handle = trace.last_wait_signal_handle != 0
                        ? trace.last_wait_signal_handle
                        : trace.post_frontend_deferred_hash_wake_signal_handle;
    wait_handle = trace.last_wait_handle;
    trace_wait_lr = trace.last_wait_lr;
    trace_wait_result = trace.last_wait_result;
    trace_wait_ms = trace.last_wait_ms;
  }
  if (LooksLikeGuestPointer(render_handoff)) {
    const uint32_t handoff_signal = LoadGuestU32(base, render_handoff + 456);
    const uint32_t handoff_wait = LoadGuestU32(base, render_handoff + 460 + 128);
    if (signal_handle == 0) {
      signal_handle = handoff_signal;
    }
    if (wait_handle == 0) {
      wait_handle = handoff_wait;
    }
  }

  const uint32_t owner_handoff_before =
      LooksLikeGuestPointer(render_owner) ? LoadGuestU32(base, render_owner + 4)
                                          : 0;
  const bool owner_handoff_attached =
      LooksLikeGuestPointer(render_owner) &&
      LooksLikeGuestPointer(render_handoff) &&
      owner_handoff_before == render_handoff;
  const bool owner_handoff_already_clear =
      LooksLikeGuestPointer(render_owner) &&
      LooksLikeGuestPointer(render_handoff) && owner_handoff_before == 0;
  if (owner_handoff_attached) {
    StoreGuestU32(base, render_owner + 4, 0);
  }
  if (LooksLikeGuestPointer(render_handoff)) {
    StoreGuestU8(base, render_handoff + 168, 1);
    StoreGuestU8(base, render_handoff + 244, 1);
  }

  StoreGuestU8(base, kPrimaryReadyGlobal, 0);
  StoreGuestU8(base, kSecondaryReadyGlobal, 0);
  StoreGuestU32(base, kPrimaryOwnerGlobal, 0);
  StoreGuestU32(base, kSecondaryOwnerGlobal, 0);
  StoreGuestU32(base, kLoadingPrimaryModeGlobal, 0);

  auto looks_like_private_event_handle = [](uint32_t handle) {
    return (handle & 0xFF000000u) == 0xF8000000u;
  };
  bool signaled = false;
  PPCContext signal_ctx = source_ctx;
  if (looks_like_private_event_handle(signal_handle)) {
    signal_ctx.r3.u32 = signal_handle;
    ::__imp__sub_822C9388(signal_ctx, base);
    signaled = true;
  }
  PPCContext wait_signal_ctx = source_ctx;
  if (looks_like_private_event_handle(wait_handle) &&
      wait_handle != signal_handle) {
    wait_signal_ctx.r3.u32 = wait_handle;
    ::__imp__sub_822C9388(wait_signal_ctx, base);
    signaled = true;
  }

  RecordRefiiMainMenuBlockerEvent(
      "ownerless_post_loading_after_holder_finalized",
      fmt::format("source={} holder=0x{:08X} render_owner=0x{:08X} "
                  "render_handoff=0x{:08X} owner_plus4=0x{:08X}->0x{:08X} "
                  "primary=0x{:08X}/0x{:02X}->0x{:08X}/0x{:02X} "
                  "secondary=0x{:08X}/0x{:02X}->0x{:08X}/0x{:02X} "
                  "loading_mode=0x{:08X}->0x{:08X} "
                  "signal_handle=0x{:08X} wait_handle=0x{:08X} "
                  "trace_wait_lr=0x{:08X} trace_wait_result=0x{:08X} "
                  "trace_wait_ms={} signaled={} attached={} already_clear={}",
                  source_api,
                  holder_guest,
                  render_owner,
                  render_handoff,
                  owner_handoff_before,
                  LooksLikeGuestPointer(render_owner)
                      ? LoadGuestU32(base, render_owner + 4)
                      : 0,
                  primary_owner_before,
                  primary_ready_before,
                  LoadGuestU32(base, kPrimaryOwnerGlobal),
                  LoadGuestU8(base, kPrimaryReadyGlobal),
                  secondary_owner_before,
                  secondary_ready_before,
                  LoadGuestU32(base, kSecondaryOwnerGlobal),
                  LoadGuestU8(base, kSecondaryReadyGlobal),
                  loading_mode_before,
                  LoadGuestU32(base, kLoadingPrimaryModeGlobal),
                  signal_handle,
                  wait_handle,
                  trace_wait_lr,
                  trace_wait_result,
                  trace_wait_ms,
                  signaled ? "true" : "false",
                  owner_handoff_attached ? "true" : "false",
                  owner_handoff_already_clear ? "true" : "false"));
  if (VfsTracingEnabled() ||
      ShouldLogRepeatedVfsTraceOperation(
          "OwnerlessPostLoadingAfterHolderFinalized", 8)) {
    REXLOG_INFO("Refii VFS ownerless post-loading after-holder finalized "
                "source={} holder=0x{:08X} render_owner=0x{:08X} "
                "render_handoff=0x{:08X} signal_handle=0x{:08X} "
                "wait_handle=0x{:08X} signaled={} attached={} "
                "already_clear={} globals=[{}] handoff=[{}] main_menu=[{}]",
                source_api,
                holder_guest,
                render_owner,
                render_handoff,
                signal_handle,
                wait_handle,
                signaled ? "true" : "false",
                owner_handoff_attached ? "true" : "false",
                owner_handoff_already_clear ? "true" : "false",
                FormatRefiiLoadingScreenSequenceGlobals(base),
                FormatRefiiPostVideoHandoffTraceForLog(),
                FormatRefiiMainMenuBlockerTraceForLog());
  }
  LogRefiiVfsChainVerdict("frontend_post_video",
                          static_cast<uint32_t>(source_ctx.lr),
                          "post_loading_after_holder",
                          "",
                          "",
                          "ownerless_vfs_after_holder",
                          "ownerless_after_holder_finalized",
                          "Opened",
                          "native_loading_globals_release");
  return true;
}

void RecordRefiiPostLoadingMiddlewareVideoReadComplete(
    const RefiiPrivateFileHandleRecord& record,
    uint32_t handle,
    uint64_t read_end) {
  const bool already_complete =
      RefiiPostLoadingMiddlewareVideoFullyRead().exchange(
          true, std::memory_order_relaxed);
  if (already_complete) {
    return;
  }

  RecordRefiiMainMenuBlockerEvent(
      "post_loading_middleware_bik_fully_read",
      fmt::format("handle=0x{:08X} path={} read_end={} size={}",
                  handle,
                  record.virtual_path,
                  read_end,
                  record.size));
  if (VfsTracingEnabled() ||
      ShouldLogRepeatedVfsTraceOperation("PostLoadingMiddlewareBikRead", 8)) {
    REXLOG_INFO("Refii VFS post-loading middleware BIK fully read "
                "handle=0x{:08X} path={} host={} read_end={} size={} "
                "handoff=[{}] main_menu=[{}]",
                handle,
                record.virtual_path,
                record.path.string(),
                read_end,
                record.size,
                FormatRefiiPostVideoHandoffTraceForLog(),
                FormatRefiiMainMenuBlockerTraceForLog());
  }
}

bool TryCollapseRefiiPostLoadingAfterHolderMode5(
    const PPCContext& source_ctx,
    uint8_t* base,
    uint32_t holder_guest,
    uint32_t child,
    std::string_view source_api) {
  const bool ownerless_bik_complete =
      RefiiOwnerlessPostVideoVfsChainReadyForAfterHolder() &&
      RefiiPostLoadingMiddlewareVideoReadComplete();
  const bool refii_after_holder_ready =
      RefiiCollapsedLoadingSequenceReadyForOwnerRelease() ||
      ownerless_bik_complete;
  if (base == nullptr || !RefiiLooseAssetBackendEnabled() ||
      !RefiiPostGameFacePackageHandoffSignalEmitted() ||
      !RefiiStartupVideosReadyForMenuHandoff() ||
      !refii_after_holder_ready || !LooksLikeGuestPointer(holder_guest) ||
      !LooksLikeGuestPointer(child)) {
    return false;
  }

  if (LoadRefiiPostLoadingAfterHolderMode(base, holder_guest) != 4 ||
      LoadGuestU8(base, child + 221) == 0) {
    return false;
  }

  static std::atomic<uint32_t> last_mode5_holder{0};
  if (last_mode5_holder.exchange(holder_guest, std::memory_order_relaxed) ==
      holder_guest) {
    return false;
  }

  const std::string before_state =
      FormatRefiiPostLoadingAfterHolderState(base, holder_guest);

  SetRefiiPostLoadingAfterHolderMode(
      source_ctx, base, holder_guest, 5, 0x822A9F20u);

  const bool advanced =
      LoadRefiiPostLoadingAfterHolderMode(base, holder_guest) == 5;
  RecordRefiiMainMenuBlockerEvent(
      advanced ? "post_loading_after_holder_mode5_collapsed"
               : "post_loading_after_holder_mode5_collapse_failed",
      fmt::format("source={} holder=0x{:08X} child=0x{:08X} "
                  "ownerless_bik_complete={} before=[{}] after=[{}]",
                  source_api,
                  holder_guest,
                  child,
                  ownerless_bik_complete ? "true" : "false",
                  before_state,
                  FormatRefiiPostLoadingAfterHolderState(base, holder_guest)));
  if (VfsTracingEnabled() ||
      ShouldLogRepeatedVfsTraceOperation("PostLoadingAfterHolderMode5", 8)) {
    REXLOG_INFO("Refii VFS post-loading after-holder mode5 {} "
                "source={} holder=0x{:08X} child=0x{:08X} "
                "ownerless_bik_complete={} before=[{}] after=[{}] "
                "handoff=[{}] main_menu=[{}]",
                advanced ? "collapsed" : "collapse_failed",
                source_api,
                holder_guest,
                child,
                ownerless_bik_complete ? "true" : "false",
                before_state,
                FormatRefiiPostLoadingAfterHolderState(base, holder_guest),
                FormatRefiiPostVideoHandoffTraceForLog(),
                FormatRefiiMainMenuBlockerTraceForLog());
  }
  LogRefiiVfsChainVerdict("frontend_post_video",
                          static_cast<uint32_t>(source_ctx.lr),
                          "post_loading_after_holder",
                          "",
                          "",
                          "native_after_holder",
                          advanced
                              ? "post_loading_after_holder_mode5_collapsed"
                              : "post_loading_after_holder_mode5_failed",
                          advanced ? "Opened" : "OpenFailed",
                          advanced ? "native_mode5_cleanup"
                                   : "native_mode5_transition_failed");
  if (advanced) {
    const std::string before_cleanup_state =
        FormatRefiiPostLoadingAfterHolderState(base, holder_guest);
    PPCContext cleanup_ctx = source_ctx;
    cleanup_ctx.r3.u32 = holder_guest;
    cleanup_ctx.lr = 0x823C1768u;
    __imp__sub_823C1658(cleanup_ctx, base);

    const bool cleaned =
        LoadRefiiPostLoadingAfterHolderMode(base, holder_guest) == 0 &&
        LoadGuestU32(base, holder_guest + kAfterHolderChildOffset) == 0;
    if (cleaned && ownerless_bik_complete) {
      RefiiPostLoadingAfterHolderMode5CleanedHolder().store(
          holder_guest, std::memory_order_relaxed);
    }
    bool released_post_loading_globals =
        cleaned &&
        TryFinalizeRefiiPostLoadingFrontendGeneratedState(
            source_ctx,
            base,
            "post_loading_after_holder_mode5_cleaned",
            true,
            true);
    if (!released_post_loading_globals && cleaned && ownerless_bik_complete) {
      released_post_loading_globals =
          TryFinalizeRefiiOwnerlessPostLoadingAfterHolderState(
              source_ctx,
              base,
              holder_guest,
              "post_loading_after_holder_mode5_cleaned");
    }
    RecordRefiiMainMenuBlockerEvent(
        cleaned ? "post_loading_after_holder_mode5_cleaned"
                : "post_loading_after_holder_mode5_cleanup_retained",
        fmt::format("source={} holder=0x{:08X} child=0x{:08X} "
                    "released_post_loading_globals={} before=[{}] after=[{}]",
                    source_api,
                    holder_guest,
                    child,
                    released_post_loading_globals ? "true" : "false",
                    before_cleanup_state,
                    FormatRefiiPostLoadingAfterHolderState(base,
                                                           holder_guest)));
    if (VfsTracingEnabled() ||
        ShouldLogRepeatedVfsTraceOperation("PostLoadingAfterHolderMode5Cleanup",
                                           8)) {
      REXLOG_INFO("Refii VFS post-loading after-holder mode5 cleanup {} "
                  "source={} holder=0x{:08X} child=0x{:08X} "
                  "released_post_loading_globals={} before=[{}] after=[{}] "
                  "handoff=[{}] main_menu=[{}]",
                  cleaned ? "cleaned" : "retained",
                  source_api,
                  holder_guest,
                  child,
                  released_post_loading_globals ? "true" : "false",
                  before_cleanup_state,
                  FormatRefiiPostLoadingAfterHolderState(base, holder_guest),
                  FormatRefiiPostVideoHandoffTraceForLog(),
                  FormatRefiiMainMenuBlockerTraceForLog());
    }
    LogRefiiVfsChainVerdict("frontend_post_video",
                            static_cast<uint32_t>(source_ctx.lr),
                            "post_loading_after_holder",
                            "",
                            "",
                            "native_after_holder",
                            cleaned ? "post_loading_after_holder_mode5_cleaned"
                                    : "post_loading_after_holder_mode5_cleanup_retained",
                            cleaned ? "Opened" : "TraceOnce",
                            cleaned ? "native_mode0_ready"
                                    : "native_mode5_cleanup_retained");
  }
  return advanced;
}

bool TryAdvanceRefiiPostLoadingAfterHolderMode4(
    const PPCContext& source_ctx,
    uint8_t* base,
    uint32_t holder_guest,
    std::string_view source_api) {
  const bool ownerless_bik_complete =
      RefiiOwnerlessPostVideoVfsChainReadyForAfterHolder() &&
      RefiiPostLoadingMiddlewareVideoReadComplete();
  const bool refii_after_holder_ready =
      RefiiCollapsedLoadingSequenceReadyForOwnerRelease() ||
      ownerless_bik_complete;
  if (base == nullptr || !RefiiLooseAssetBackendEnabled() ||
      !RefiiPostGameFacePackageHandoffSignalEmitted() ||
      !RefiiStartupVideosReadyForMenuHandoff() ||
      !refii_after_holder_ready || !LooksLikeGuestPointer(holder_guest)) {
    return false;
  }

  const uint32_t child =
      LoadGuestU32(base, holder_guest + kAfterHolderChildOffset);
  const uint32_t mode = LoadRefiiPostLoadingAfterHolderMode(base, holder_guest);
  if (!LooksLikeGuestPointer(child) || LoadGuestU8(base, child + 221) == 0) {
    return false;
  }
  if (mode == 4) {
    return TryCollapseRefiiPostLoadingAfterHolderMode5(
        source_ctx, base, holder_guest, child, source_api);
  }
  if (mode != 3) {
    return false;
  }

  static std::atomic<uint32_t> last_mode4_holder{0};
  if (last_mode4_holder.exchange(holder_guest, std::memory_order_relaxed) ==
      holder_guest) {
    return false;
  }

  const std::string before_state =
      FormatRefiiPostLoadingAfterHolderState(base, holder_guest);

  SetRefiiPostLoadingAfterHolderMode(
      source_ctx, base, holder_guest, 4, 0x823C18E8u);

  const bool advanced =
      LoadRefiiPostLoadingAfterHolderMode(base, holder_guest) == 4;
  RecordRefiiMainMenuBlockerEvent(
      advanced ? "post_loading_after_holder_mode4_advanced"
               : "post_loading_after_holder_mode4_advance_failed",
      fmt::format("source={} holder=0x{:08X} child=0x{:08X} "
                  "ownerless_bik_complete={} before=[{}] after=[{}]",
                  source_api,
                  holder_guest,
                  child,
                  ownerless_bik_complete ? "true" : "false",
                  before_state,
                  FormatRefiiPostLoadingAfterHolderState(base, holder_guest)));
  if (VfsTracingEnabled() ||
      ShouldLogRepeatedVfsTraceOperation("PostLoadingAfterHolderMode4", 8)) {
    REXLOG_INFO("Refii VFS post-loading after-holder mode4 {} "
                "source={} holder=0x{:08X} child=0x{:08X} "
                "ownerless_bik_complete={} before=[{}] after=[{}] "
                "handoff=[{}] main_menu=[{}]",
                advanced ? "advanced" : "advance_failed",
                source_api,
                holder_guest,
                child,
                ownerless_bik_complete ? "true" : "false",
                before_state,
                FormatRefiiPostLoadingAfterHolderState(base, holder_guest),
                FormatRefiiPostVideoHandoffTraceForLog(),
                FormatRefiiMainMenuBlockerTraceForLog());
  }
  LogRefiiVfsChainVerdict("frontend_post_video",
                          static_cast<uint32_t>(source_ctx.lr),
                          "post_loading_after_holder",
                          "",
                          "",
                          "native_after_holder",
                          advanced
                              ? "post_loading_after_holder_mode4_advanced"
                              : "post_loading_after_holder_mode4_failed",
                          advanced ? "Opened" : "OpenFailed",
                          advanced ? "native_mode4_active"
                                   : "native_mode4_transition_failed");
  if (advanced) {
    const bool defer_mode5_until_native_frame_boundary =
        source_api == "sub_822C6C68_exit_before_823C1658" ||
        source_api == "sub_823C1658_enter" ||
        source_api == "sub_823C1658_exit_after_deferred";
    if (!defer_mode5_until_native_frame_boundary) {
      TryCollapseRefiiPostLoadingAfterHolderMode5(
          source_ctx, base, holder_guest, child, source_api);
    }
  }
  return advanced;
}

bool TryMarkRefiiPostLoadingAfterHolderReady(
    const PPCContext& source_ctx,
    uint8_t* base,
    uint32_t holder_guest,
    std::string_view source_api) {
  const bool ownerless_after_holder_ready =
      RefiiOwnerlessPostVideoVfsChainReadyForAfterHolder();
  const bool ownerless_bik_complete =
      ownerless_after_holder_ready &&
      RefiiPostLoadingMiddlewareVideoReadComplete();
  const bool refii_after_holder_ready =
      RefiiCollapsedLoadingSequenceReadyForOwnerRelease() ||
      ownerless_bik_complete;
  if (base == nullptr || !RefiiLooseAssetBackendEnabled() ||
      !RefiiPostGameFacePackageHandoffSignalEmitted() ||
      !RefiiStartupVideosReadyForMenuHandoff() ||
      !refii_after_holder_ready || !LooksLikeGuestPointer(holder_guest)) {
    return false;
  }

  constexpr uint32_t kLoadingGlobalBase = 0x83490000u;
  constexpr uint32_t kPrimaryOwnerGlobal = kLoadingGlobalBase + 27320u;
  constexpr uint32_t kPrimaryReadyGlobal = kLoadingGlobalBase + 27230u;
  constexpr uint32_t kSecondaryOwnerGlobal = kLoadingGlobalBase + 27328u;

  const uint32_t request_guest =
      RefiiPostGameFacePrimaryOwnerRequest().load(std::memory_order_relaxed);
  uint32_t primary_owner = LoadGuestU32(base, kPrimaryOwnerGlobal);
  uint32_t secondary_owner = LoadGuestU32(base, kSecondaryOwnerGlobal);
  const uint32_t child =
      LoadGuestU32(base, holder_guest + kAfterHolderChildOffset);
  const uint32_t mode = LoadRefiiPostLoadingAfterHolderMode(base, holder_guest);
  if (mode != 3 || !LooksLikeGuestPointer(child)) {
    return false;
  }
  if (LoadGuestU8(base, child + 221) != 0) {
    TryAdvanceRefiiPostLoadingAfterHolderMode4(
        source_ctx, base, holder_guest, source_api);
    return false;
  }
  const bool strict_native_owner_ready =
      IsNativeFrontendRenderRequest(base, request_guest) &&
      LoadGuestU8(base, request_guest + 92) != 0 &&
      LooksLikeGuestPointer(primary_owner) &&
      LoadGuestU8(base, kPrimaryReadyGlobal) != 0 &&
      LooksLikeGuestPointer(secondary_owner) &&
      LoadGuestU32(base, secondary_owner + 12) == request_guest;
  const uint32_t completed_loading_owner =
      RefiiCompletedLoadingScreenOwner().load(std::memory_order_relaxed);
  const bool refii_completed_primary_owner_ready =
      IsNativeFrontendRenderRequest(base, request_guest) &&
      LoadGuestU8(base, request_guest + 92) != 0 &&
      LooksLikeGuestPointer(primary_owner) &&
      LoadGuestU8(base, kPrimaryReadyGlobal) != 0 &&
      LooksLikeGuestPointer(completed_loading_owner) &&
      RefiiPostVideoFrontendChainConsumed() &&
      RefiiPostVfsChainReadyForLoadingOwnerRelease();
  const bool refii_completed_ownerless_child_ready =
      LooksLikeGuestPointer(completed_loading_owner) &&
      RefiiPostVideoFrontendChainConsumed() &&
      RefiiPostVfsChainReadyForLoadingOwnerRelease() &&
      LoadGuestU8(base, child + 220) != 0;
  const bool ownerless_child_ready =
      ownerless_bik_complete && LoadGuestU8(base, child + 220) != 0;
  if (!strict_native_owner_ready && !refii_completed_primary_owner_ready &&
      !refii_completed_ownerless_child_ready && !ownerless_child_ready) {
    return false;
  }

  const std::string before_state =
      FormatRefiiPostLoadingAfterHolderState(base, holder_guest);
  StoreGuestU8(base, child + 221, 1);

  RecordRefiiMainMenuBlockerEvent(
      "post_loading_after_holder_ready_marked",
      fmt::format("source={} holder=0x{:08X} child=0x{:08X} "
                  "request=0x{:08X} primary=0x{:08X} secondary=0x{:08X} "
                  "completed_owner=0x{:08X} strict_native_owner_ready={} "
                  "refii_completed_primary_owner_ready={} "
                  "refii_completed_ownerless_child_ready={} "
                  "ownerless_bik_complete={} "
                  "before=[{}] after=[{}]",
                  source_api,
                  holder_guest,
                  child,
                  request_guest,
                  primary_owner,
                  secondary_owner,
                  completed_loading_owner,
                  strict_native_owner_ready ? "true" : "false",
                  refii_completed_primary_owner_ready ? "true" : "false",
                  refii_completed_ownerless_child_ready ? "true" : "false",
                  ownerless_bik_complete ? "true" : "false",
                  before_state,
                  FormatRefiiPostLoadingAfterHolderState(base, holder_guest)));
  if (VfsTracingEnabled() ||
      ShouldLogRepeatedVfsTraceOperation("PostLoadingAfterHolderReadyMarked",
                                         8)) {
    REXLOG_INFO("Refii VFS post-loading after-holder ready marked "
                "source={} holder=0x{:08X} child=0x{:08X} request=0x{:08X} "
                "primary=0x{:08X} secondary=0x{:08X} completed_owner=0x{:08X} "
                "strict_native_owner_ready={} "
                "refii_completed_primary_owner_ready={} "
                "refii_completed_ownerless_child_ready={} "
                "ownerless_bik_complete={} "
                "before=[{}] after=[{}] handoff=[{}] main_menu=[{}]",
                source_api,
                holder_guest,
                child,
                request_guest,
                primary_owner,
                secondary_owner,
                completed_loading_owner,
                strict_native_owner_ready ? "true" : "false",
                refii_completed_primary_owner_ready ? "true" : "false",
                refii_completed_ownerless_child_ready ? "true" : "false",
                ownerless_bik_complete ? "true" : "false",
                before_state,
                FormatRefiiPostLoadingAfterHolderState(base, holder_guest),
                FormatRefiiPostVideoHandoffTraceForLog(),
                FormatRefiiMainMenuBlockerTraceForLog());
  }
  LogRefiiVfsChainVerdict("frontend_post_video",
                          static_cast<uint32_t>(source_ctx.lr),
                          "post_loading_after_holder",
                          "",
                          "",
                          "native_after_holder",
                          "post_loading_after_holder_ready_marked",
                          "Opened",
                          "native_mode4_transition");
  TryAdvanceRefiiPostLoadingAfterHolderMode4(
      source_ctx, base, holder_guest, source_api);
  return true;
}

bool TryCompleteRefiiPostLoadingAfterHolderForCompletedOwner(
    const PPCContext& source_ctx,
    uint8_t* base,
    uint32_t holder_guest,
    std::string_view source_api) {
  if (base == nullptr || !RefiiLooseAssetBackendEnabled() ||
      !RefiiStartupVideosReadyForMenuHandoff() ||
      !RefiiPostGameFacePackageHandoffSignalEmitted() ||
      !RefiiPostVfsChainReadyForLoadingOwnerRelease() ||
      !LooksLikeGuestPointer(holder_guest) ||
      !LooksLikeGuestPointer(
          RefiiCompletedLoadingScreenOwner().load(std::memory_order_relaxed))) {
    return false;
  }

  const uint32_t mode = LoadRefiiPostLoadingAfterHolderMode(base, holder_guest);
  const uint32_t child =
      LoadGuestU32(base, holder_guest + kAfterHolderChildOffset);
  if (!LooksLikeGuestPointer(child) || (mode != 3 && mode != 4)) {
    return false;
  }

  const bool child_opened = LoadGuestU8(base, child + 220) != 0;
  const uint8_t child_ready_before = LoadGuestU8(base, child + 221);
  if (!child_opened && child_ready_before == 0) {
    return false;
  }

  const std::string before_state =
      FormatRefiiPostLoadingAfterHolderState(base, holder_guest);
  if (child_ready_before == 0) {
    StoreGuestU8(base, child + 221, 1);
  }

  const bool advanced =
      TryAdvanceRefiiPostLoadingAfterHolderMode4(
          source_ctx, base, holder_guest, source_api) ||
      LoadRefiiPostLoadingAfterHolderMode(base, holder_guest) == 0;
  RecordRefiiMainMenuBlockerEvent(
      advanced ? "post_loading_after_holder_completed_owner_collapsed"
               : "post_loading_after_holder_completed_owner_retained",
      fmt::format("source={} holder=0x{:08X} child=0x{:08X} "
                  "mode={} child_opened={} child_ready=0x{:02X}->0x{:02X} "
                  "completed_owner=0x{:08X} before=[{}] after=[{}]",
                  source_api,
                  holder_guest,
                  child,
                  mode,
                  child_opened ? "true" : "false",
                  child_ready_before,
                  LoadGuestU8(base, child + 221),
                  RefiiCompletedLoadingScreenOwner().load(
                      std::memory_order_relaxed),
                  before_state,
                  FormatRefiiPostLoadingAfterHolderState(base, holder_guest)));
  if (VfsTracingEnabled() ||
      ShouldLogRepeatedVfsTraceOperation(
          "PostLoadingAfterHolderCompletedOwnerCollapse", 8)) {
    REXLOG_INFO("Refii VFS post-loading after-holder completed-owner {} "
                "source={} holder=0x{:08X} child=0x{:08X} "
                "mode={} child_opened={} child_ready=0x{:02X}->0x{:02X} "
                "before=[{}] after=[{}] handoff=[{}] main_menu=[{}]",
                advanced ? "collapsed" : "retained",
                source_api,
                holder_guest,
                child,
                mode,
                child_opened ? "true" : "false",
                child_ready_before,
                LoadGuestU8(base, child + 221),
                before_state,
                FormatRefiiPostLoadingAfterHolderState(base, holder_guest),
                FormatRefiiPostVideoHandoffTraceForLog(),
                FormatRefiiMainMenuBlockerTraceForLog());
  }
  return advanced;
}

bool TryCollapseRefiiPostLoadingAfterHolderMode2(
    const PPCContext& source_ctx,
    uint8_t* base,
    uint32_t holder_guest,
    std::string_view source_api) {
  if (base == nullptr || !RefiiLooseAssetBackendEnabled() ||
      !RefiiStartupVideosReadyForMenuHandoff() ||
      !RefiiPostGameFacePackageHandoffSignalEmitted() ||
      !RefiiPostVideoFrontendChainConsumed() ||
      !RefiiPostVfsChainReadyForLoadingOwnerRelease() ||
      !LooksLikeGuestPointer(holder_guest)) {
    return false;
  }

  const uint32_t mode = LoadRefiiPostLoadingAfterHolderMode(base, holder_guest);
  const uint32_t child =
      LoadGuestU32(base, holder_guest + kAfterHolderChildOffset);
  const bool middleware_holder =
      IsRefiiPostLoadingMiddlewareVideoHolder(base, holder_guest);
  const bool final_handoff_ready_without_middleware =
      RefiiPostLoadingFrontendFinalHandoffWakeEmitted() &&
      (RefiiPostGameFacePackageComplete() ||
       RefiiPostGameFacePackageNativeFallbackReady()) &&
      RefiiMainMenuTraceHasParsedTextureForGeneratedReadyPayload();
  const bool completed_owner_release_ready =
      LooksLikeGuestPointer(
          RefiiCompletedLoadingScreenOwner().load(std::memory_order_relaxed)) &&
      RefiiPostVideoFrontendChainConsumed() &&
      RefiiPostVfsChainReadyForLoadingOwnerRelease() &&
      RefiiMainMenuTraceHasParsedTextureForGeneratedReadyPayload();
  const bool stale_native_child =
      middleware_holder && child != 0 && !LooksLikeGuestPointer(child) &&
      (final_handoff_ready_without_middleware || completed_owner_release_ready);
  if (mode != 2 || (child != 0 && !stale_native_child)) {
    return false;
  }

  if (middleware_holder && !stale_native_child &&
      !final_handoff_ready_without_middleware &&
      !RefiiPostLoadingMiddlewareVideoReadComplete()) {
    const bool native_tick_started =
        TryTickRefiiPostLoadingMiddlewareAfterHolderNativeMode2(
            source_ctx, base, holder_guest, source_api);
    RecordRefiiMainMenuBlockerEvent(
        "post_loading_after_holder_mode2_deferred",
        fmt::format("source={} holder=0x{:08X} reason=middleware_bik_pending "
                    "native_tick_started={} state=[{}]",
                    source_api,
                    holder_guest,
                    native_tick_started ? "true" : "false",
                    FormatRefiiPostLoadingAfterHolderState(base,
                                                           holder_guest)));
    if (VfsTracingEnabled() ||
        ShouldLogRepeatedVfsTraceOperation(
            "PostLoadingAfterHolderMode2Deferred", 8)) {
      REXLOG_INFO("Refii VFS post-loading after-holder mode2 deferred "
                  "source={} holder=0x{:08X} reason=middleware_bik_pending "
                  "native_tick_started={} handoff=[{}] main_menu=[{}] "
                  "state=[{}]",
                  source_api,
                  holder_guest,
                  native_tick_started ? "true" : "false",
                  FormatRefiiPostVideoHandoffTraceForLog(),
                  FormatRefiiMainMenuBlockerTraceForLog(),
                  FormatRefiiPostLoadingAfterHolderState(base, holder_guest));
    }
    LogRefiiVfsChainVerdict("frontend_post_video",
                            static_cast<uint32_t>(source_ctx.lr),
                            "data/art/videos/middlewarelogos.bik",
                            "",
                            "",
                            "native_after_holder",
                            "post_loading_after_holder_mode2_deferred",
                            "TraceOnce",
                            native_tick_started
                                ? "native_mode2_tick_started"
                                : "wait_for_native_middleware_video_read");
    return false;
  }

  static std::atomic<uint32_t> last_mode2_holder{0};
  static std::atomic<uint32_t> last_mode2_recollapse_holder{0};
  const bool already_collapsed =
      last_mode2_holder.exchange(holder_guest, std::memory_order_relaxed) ==
      holder_guest;
  if (already_collapsed) {
    const bool native_rearm_still_mode2 =
        RefiiPostLoadingFrontendFinalHandoffWakeEmitted() &&
        (RefiiPostGameFacePackageComplete() ||
         RefiiPostGameFacePackageNativeFallbackReady()) &&
        RefiiMainMenuTraceHasParsedTextureForGeneratedReadyPayload() &&
        RefiiPostVideoFrontendChainConsumed();
    const bool completed_owner_rearm_still_mode2 =
        completed_owner_release_ready && stale_native_child;
    if ((!native_rearm_still_mode2 && !completed_owner_rearm_still_mode2) ||
        (!completed_owner_rearm_still_mode2 &&
         last_mode2_recollapse_holder.exchange(
             holder_guest, std::memory_order_relaxed) == holder_guest)) {
      return false;
    }
  }

  const std::string before_state =
      FormatRefiiPostLoadingAfterHolderState(base, holder_guest);

  if (stale_native_child) {
    StoreGuestU32(base, holder_guest + kAfterHolderChildOffset, 0);
    StoreGuestU32(base, holder_guest + kAfterHolderField44Offset, 0);
  }
  SetRefiiPostLoadingAfterHolderMode(
      source_ctx, base, holder_guest, 0, 0x82467CB8u);

  const bool collapsed =
      LoadRefiiPostLoadingAfterHolderMode(base, holder_guest) == 0 &&
      LoadGuestU32(base, holder_guest + kAfterHolderChildOffset) == 0;
  bool released_post_loading_globals = false;
  if (collapsed) {
    RefiiPostLoadingAfterHolderMode5CleanedHolder().store(
        holder_guest, std::memory_order_relaxed);
    released_post_loading_globals =
        TryFinalizeRefiiPostLoadingFrontendGeneratedState(
            source_ctx,
            base,
            "post_loading_after_holder_mode2_collapsed",
            true,
            true);
    if (!released_post_loading_globals) {
      released_post_loading_globals =
          TryFinalizeRefiiOwnerlessPostLoadingAfterHolderState(
              source_ctx,
              base,
              holder_guest,
              "post_loading_after_holder_mode2_collapsed");
    }
  }

  RecordRefiiMainMenuBlockerEvent(
      collapsed ? "post_loading_after_holder_mode2_collapsed"
                : "post_loading_after_holder_mode2_collapse_failed",
      fmt::format("source={} holder=0x{:08X} "
                  "released_post_loading_globals={} stale_native_child={} "
                  "final_handoff_ready_without_middleware={} "
                  "completed_owner_release_ready={} "
                  "before=[{}] after=[{}]",
                  source_api,
                  holder_guest,
                  released_post_loading_globals ? "true" : "false",
                  stale_native_child ? "true" : "false",
                  final_handoff_ready_without_middleware ? "true" : "false",
                  completed_owner_release_ready ? "true" : "false",
                  before_state,
                  FormatRefiiPostLoadingAfterHolderState(base, holder_guest)));
  if (VfsTracingEnabled() ||
      ShouldLogRepeatedVfsTraceOperation("PostLoadingAfterHolderMode2", 8)) {
    REXLOG_INFO("Refii VFS post-loading after-holder mode2 {} "
                "source={} holder=0x{:08X} "
                "released_post_loading_globals={} stale_native_child={} "
                "final_handoff_ready_without_middleware={} "
                "completed_owner_release_ready={} "
                "before=[{}] after=[{}] handoff=[{}] main_menu=[{}]",
                collapsed ? "collapsed" : "collapse_failed",
                source_api,
                holder_guest,
                released_post_loading_globals ? "true" : "false",
                stale_native_child ? "true" : "false",
                final_handoff_ready_without_middleware ? "true" : "false",
                completed_owner_release_ready ? "true" : "false",
                before_state,
                FormatRefiiPostLoadingAfterHolderState(base, holder_guest),
                FormatRefiiPostVideoHandoffTraceForLog(),
                FormatRefiiMainMenuBlockerTraceForLog());
  }
  LogRefiiVfsChainVerdict("frontend_post_video",
                          static_cast<uint32_t>(source_ctx.lr),
                          "post_loading_after_holder",
                          "",
                          "",
                          "native_after_holder",
                          collapsed
                              ? "post_loading_after_holder_mode2_collapsed"
                              : "post_loading_after_holder_mode2_failed",
                          collapsed ? "Opened" : "OpenFailed",
                          collapsed ? "native_mode2_satisfied_by_vfs"
                                    : "native_mode2_collapse_failed");
  return collapsed;
}

bool TryArmRefiiPostLoadingAfterHolderFromCompletedVfsChain(
    const PPCContext& source_ctx,
    uint8_t* base,
    uint32_t holder_guest,
    std::string_view source_api) {
  const bool ownerless_after_holder_ready =
      RefiiOwnerlessPostVideoVfsChainReadyForAfterHolder();
  const bool refii_after_holder_ready =
      RefiiPostVfsChainReadyForLoadingOwnerRelease() ||
      ownerless_after_holder_ready;
  if (base == nullptr || !RefiiLooseAssetBackendEnabled() ||
      !RefiiPostGameFacePackageHandoffSignalEmitted() ||
      !RefiiStartupVideosReadyForMenuHandoff() ||
      !refii_after_holder_ready || !LooksLikeGuestPointer(holder_guest)) {
    return false;
  }

  constexpr uint32_t kLoadingGlobalBase = 0x83490000u;
  constexpr uint32_t kPrimaryOwnerGlobal = kLoadingGlobalBase + 27320u;
  constexpr uint32_t kSecondaryOwnerGlobal = kLoadingGlobalBase + 27328u;
  constexpr uint32_t kPrimaryReadyGlobal = kLoadingGlobalBase + 27230u;

  uint32_t request_guest =
      RefiiPostGameFacePrimaryOwnerRequest().load(std::memory_order_relaxed);
  const uint32_t secondary_owner = LoadGuestU32(base, kSecondaryOwnerGlobal);
  if (!IsNativeFrontendRenderRequest(base, request_guest) &&
      LooksLikeGuestPointer(secondary_owner)) {
    const uint32_t secondary_request =
        LoadGuestU32(base, secondary_owner + 12);
    if (IsNativeFrontendRenderRequest(base, secondary_request)) {
      request_guest = secondary_request;
    }
  }
  const uint32_t primary_owner = LoadGuestU32(base, kPrimaryOwnerGlobal);
  const uint8_t primary_ready = LoadGuestU8(base, kPrimaryReadyGlobal);
  const uint32_t render_owner = source_ctx.r4.u32;
  const uint32_t render_handoff_before =
      LooksLikeGuestPointer(render_owner) ? LoadGuestU32(base, render_owner + 4)
                                          : 0;
  uint32_t render_handoff = render_handoff_before;
  if (LooksLikeGuestPointer(render_owner) &&
      !LooksLikeGuestPointer(render_handoff)) {
    std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
    const auto& trace = RefiiPostVideoHandoffTraceState();
    if (trace.last_nonidle_render_schedule_owner == render_owner &&
        LooksLikeGuestPointer(trace.last_nonidle_render_schedule_handoff)) {
      render_handoff = trace.last_nonidle_render_schedule_handoff;
    } else if (trace.last_render_schedule_owner == render_owner &&
               LooksLikeGuestPointer(trace.last_render_schedule_handoff)) {
      render_handoff = trace.last_render_schedule_handoff;
    }
  }
  const uint32_t completed_loading_owner =
      RefiiCompletedLoadingScreenOwner().load(std::memory_order_relaxed);
  const uint32_t mode = LoadRefiiPostLoadingAfterHolderMode(base, holder_guest);
  const uint32_t child =
      LoadGuestU32(base, holder_guest + kAfterHolderChildOffset);
  const bool native_request_ready =
      IsNativeFrontendRenderRequest(base, request_guest);
  const uint8_t request_done =
      native_request_ready ? LoadGuestU8(base, request_guest + 92) : 0;
  const bool strict_native_owner_ready =
      LooksLikeGuestPointer(completed_loading_owner) &&
      native_request_ready && request_done != 0 &&
      LooksLikeGuestPointer(primary_owner) && primary_ready != 0 &&
      LooksLikeGuestPointer(render_owner) &&
      LooksLikeGuestPointer(render_handoff);
  const bool generated_finalize_ready =
      source_api == "post_loading_generated_state_finalize" &&
      RefiiPostVfsChainReadyForLoadingOwnerRelease() &&
      RefiiPostVideoFrontendChainConsumed() &&
      LooksLikeGuestPointer(completed_loading_owner) &&
      LooksLikeGuestPointer(primary_owner) && primary_ready != 0 &&
      LooksLikeGuestPointer(render_owner) &&
      LooksLikeGuestPointer(render_handoff);
  auto log_arm_skip = [&](std::string_view reason) {
    RecordRefiiMainMenuBlockerEvent(
        "post_loading_after_holder_arm_skipped",
        fmt::format("reason={} source={} holder=0x{:08X} mode={} "
                    "child=0x{:08X} request=0x{:08X} native_request={} "
                    "request_done=0x{:02X} primary=0x{:08X}/0x{:02X} "
                    "secondary=0x{:08X} render_owner=0x{:08X} "
                    "render_handoff=0x{:08X}->0x{:08X} "
                    "completed_owner=0x{:08X} strict_ready={} "
                    "generated_finalize_ready={} ownerless_ready={} "
                    "state=[{}]",
                    reason,
                    source_api,
                    holder_guest,
                    mode,
                    child,
                    request_guest,
                    native_request_ready ? "true" : "false",
                    request_done,
                    primary_owner,
                    primary_ready,
                    secondary_owner,
                    render_owner,
                    render_handoff_before,
                    render_handoff,
                    completed_loading_owner,
                    strict_native_owner_ready ? "true" : "false",
                    generated_finalize_ready ? "true" : "false",
                    ownerless_after_holder_ready ? "true" : "false",
                    FormatRefiiPostLoadingAfterHolderState(base,
                                                           holder_guest)));
    if (VfsTracingEnabled() ||
        ShouldLogRepeatedVfsTraceOperation("PostLoadingAfterHolderArmSkipped",
                                           8)) {
      REXLOG_INFO("Refii VFS post-loading after-holder arm skipped "
                  "reason={} source={} holder=0x{:08X} mode={} "
                  "child=0x{:08X} request=0x{:08X} native_request={} "
                  "request_done=0x{:02X} primary=0x{:08X}/0x{:02X} "
                  "secondary=0x{:08X} render_owner=0x{:08X} "
                  "render_handoff=0x{:08X}->0x{:08X} "
                  "completed_owner=0x{:08X} strict_ready={} "
                  "generated_finalize_ready={} ownerless_ready={} "
                  "handoff=[{}] main_menu=[{}] state=[{}]",
                  reason,
                  source_api,
                  holder_guest,
                  mode,
                  child,
                  request_guest,
                  native_request_ready ? "true" : "false",
                  request_done,
                  primary_owner,
                  primary_ready,
                  secondary_owner,
                  render_owner,
                  render_handoff_before,
                  render_handoff,
                  completed_loading_owner,
                  strict_native_owner_ready ? "true" : "false",
                  generated_finalize_ready ? "true" : "false",
                  ownerless_after_holder_ready ? "true" : "false",
                  FormatRefiiPostVideoHandoffTraceForLog(),
                  FormatRefiiMainMenuBlockerTraceForLog(),
                  FormatRefiiPostLoadingAfterHolderState(base,
                                                         holder_guest));
    }
  };
  const bool path_already_initialized =
      mode == 1 && child == 0 && LoadGuestU32(base, holder_guest + 8) != 0;
  if ((mode != 0 && !path_already_initialized) || child != 0 ||
      (!strict_native_owner_ready && !ownerless_after_holder_ready &&
       !generated_finalize_ready)) {
    log_arm_skip("state_or_owner_not_ready");
    return false;
  }

  if (!path_already_initialized &&
      !TryInitializeRefiiPostLoadingAfterHolderResourcePath(
          source_ctx, base, holder_guest, source_api)) {
    log_arm_skip("path_init_failed");
    return false;
  }

  static std::atomic<uint32_t> last_armed_holder{0};
  static std::atomic<uint32_t> last_rearmed_after_reset_holder{0};
  const bool already_armed =
      last_armed_holder.exchange(holder_guest, std::memory_order_relaxed) ==
      holder_guest;
  if (already_armed) {
    const bool native_reset_back_to_idle =
        (mode == 0 || path_already_initialized) && child == 0 &&
        (ownerless_after_holder_ready || generated_finalize_ready) &&
        RefiiPostVideoFrontendChainConsumed() &&
        RefiiMainMenuTraceHasParsedTextureForGeneratedReadyPayload();
    if (!native_reset_back_to_idle ||
        last_rearmed_after_reset_holder.exchange(
            holder_guest, std::memory_order_relaxed) == holder_guest) {
      log_arm_skip("already_armed");
      return false;
    }
  }

  const std::string before_state =
      FormatRefiiPostLoadingAfterHolderState(base, holder_guest);
  if (LooksLikeGuestPointer(render_owner) &&
      LooksLikeGuestPointer(render_handoff) &&
      render_handoff_before != render_handoff) {
    StoreGuestU32(base, render_owner + 4, render_handoff);
  }
  SetRefiiPostLoadingAfterHolderMode(
      source_ctx, base, holder_guest, 2, 0x82306558u);
  const bool defer_native_tick_for_final_handoff =
      source_api == "post_loading_generated_state_finalize" &&
      RefiiPostLoadingFrontendFinalHandoffWakeEmitted() &&
      (RefiiPostGameFacePackageComplete() ||
       RefiiPostGameFacePackageNativeFallbackReady()) &&
      RefiiMainMenuTraceHasParsedTextureForGeneratedReadyPayload();
  const bool native_tick_started =
      IsRefiiPostLoadingMiddlewareVideoHolder(base, holder_guest) &&
      !defer_native_tick_for_final_handoff &&
      !RefiiPostLoadingMiddlewareVideoReadComplete() &&
      TryTickRefiiPostLoadingMiddlewareAfterHolderNativeMode2(
          source_ctx, base, holder_guest, source_api);

  RecordRefiiMainMenuBlockerEvent(
      "post_loading_after_holder_armed",
      fmt::format("source={} holder=0x{:08X} request=0x{:08X} "
                  "primary=0x{:08X} render_owner=0x{:08X} "
                  "render_handoff=0x{:08X}->0x{:08X} "
                  "completed_owner=0x{:08X} path_already_initialized={} "
                  "native_tick_started={} native_tick_deferred={} "
                  "before=[{}] after=[{}]",
                  source_api,
                  holder_guest,
                  request_guest,
                  primary_owner,
                  render_owner,
                  render_handoff_before,
                  render_handoff,
                  completed_loading_owner,
                  path_already_initialized ? "true" : "false",
                  native_tick_started ? "true" : "false",
                  defer_native_tick_for_final_handoff ? "true" : "false",
                  before_state,
                  FormatRefiiPostLoadingAfterHolderState(base, holder_guest)));
  if (VfsTracingEnabled() ||
      ShouldLogRepeatedVfsTraceOperation("PostLoadingAfterHolderArmed", 8)) {
    REXLOG_INFO("Refii VFS post-loading after-holder armed "
                "source={} holder=0x{:08X} request=0x{:08X} "
                "primary=0x{:08X} render_owner=0x{:08X} "
                "render_handoff=0x{:08X}->0x{:08X} "
                "completed_owner=0x{:08X} path_already_initialized={} "
                "native_tick_started={} native_tick_deferred={} "
                "before=[{}] after=[{}] "
                "handoff=[{}] main_menu=[{}]",
                source_api,
                holder_guest,
                request_guest,
                primary_owner,
                render_owner,
                render_handoff_before,
                render_handoff,
                completed_loading_owner,
                path_already_initialized ? "true" : "false",
                native_tick_started ? "true" : "false",
                defer_native_tick_for_final_handoff ? "true" : "false",
                before_state,
                FormatRefiiPostLoadingAfterHolderState(base, holder_guest),
                FormatRefiiPostVideoHandoffTraceForLog(),
                FormatRefiiMainMenuBlockerTraceForLog());
  }
  LogRefiiVfsChainVerdict("frontend_post_video",
                          static_cast<uint32_t>(source_ctx.lr),
                          "post_loading_after_holder",
                          "",
                          "",
                          "native_after_holder",
                          "post_loading_after_holder_armed",
                          "Opened",
                          "native_mode2_transition");
  return true;
}

uint32_t& RefiiPostLoadingAfterHolderRenderOwnerScratch() {
  static thread_local uint32_t owner_guest = 0;
  return owner_guest;
}


}  // namespace detail
}  // namespace refii::vfs
