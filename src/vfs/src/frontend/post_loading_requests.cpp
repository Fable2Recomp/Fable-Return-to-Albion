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
  // Gated with the rest of the synthetic frontend handoff machinery (default
  // OFF): this path fabricates frontend owner/ready globals and synthetic slot
  // objects to force the post-video handoff, but it corrupts the native
  // request scheduler during the *natural* video sequence and deadlocks the
  // game thread inside the frontend texture consumer (the "lionhead loop":
  // videos read fully, then the game hangs in post-video frontend loading and
  // never reaches the menu). Native advances this flow on its own.
  if (!FrontendSyntheticHandoffEnabled() ||
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

  // The native assign helper (sub_82275438) releases whatever the slot
  // currently holds before storing the new string. A holder that is fresh
  // enough to have no path (+8 == 0) must also have no display path; nonzero
  // garbage at +12 would be interlocked-released by native code and fault.
  const uint32_t existing_display = LoadGuestU32(base, holder_guest + 12);
  if (existing_display != 0) {
    RecordRefiiMainMenuBlockerEvent(
        "post_loading_after_holder_unexpected_display_slot",
        fmt::format("source={} holder=0x{:08X} display=0x{:08X}",
                    source_api,
                    holder_guest,
                    existing_display));
    return false;
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

uint32_t& RefiiPostLoadingAfterHolderRenderOwnerScratch() {
  static thread_local uint32_t owner_guest = 0;
  return owner_guest;
}


}  // namespace detail
}  // namespace refii::vfs
