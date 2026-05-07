#include "vfs_hooks_private.h"

namespace refii::vfs {
inline namespace detail {

void TraceFrontendTextureSyntheticPhaseStep(std::string_view step,
                                            uint8_t* base,
                                            uint32_t owner_guest,
                                            bool result) {
  if (!VfsTracingEnabled()) {
    return;
  }
  REXLOG_INFO("Refii VFS frontend texture synthetic phase step={} "
              "result={} owner=0x{:08X} owner_slots=[{}] main_menu=[{}]",
              step,
              result ? "true" : "false",
              owner_guest,
              FormatFrontendTextureConsumerOwnerSlots(base, owner_guest),
              FormatRefiiMainMenuBlockerTraceForLog());
}

bool TryInitializeFrontendTextureConsumerList200(PPCContext& source_ctx,
                                                 uint8_t* base,
                                                 uint32_t owner_guest,
                                                 std::string_view source_api) {
  if (base == nullptr || !LooksLikeGuestPointer(owner_guest) ||
      LoadGuestU32(base, owner_guest) != kFrontendTextureConsumerObjectVTable) {
    return false;
  }

  constexpr uint32_t kFrontendListNameGuest = 0x834977D8u;
  const uint32_t list_slot_guest = owner_guest + 200;
  const uint32_t before_list_guest = LoadGuestU32(base, list_slot_guest);
  bool initialized = false;
  if (!LooksLikeGuestPointer(before_list_guest)) {
    PPCContext init_ctx = source_ctx;
    init_ctx.r3.u32 = list_slot_guest;
    init_ctx.r4.u32 = kFrontendListNameGuest;
    init_ctx.lr = 0x82378A80u;
    sub_823DCB10(init_ctx, base);
    initialized = LooksLikeGuestPointer(LoadGuestU32(base, list_slot_guest));
  }

  const uint32_t list_guest = LoadGuestU32(base, list_slot_guest);
  const uint32_t list_owner_guest = LoadGuestU32(base, owner_guest + 56);
  bool bound = false;
  if (LooksLikeGuestPointer(list_guest) &&
      LooksLikeGuestPointer(list_owner_guest)) {
    PPCContext bind_ctx = source_ctx;
    bind_ctx.r3.u32 = list_owner_guest;
    bind_ctx.r4.u32 = list_guest;
    bind_ctx.lr = 0x82378A8Cu;
    sub_823DCC70(bind_ctx, base);
    bound = true;
  }

  if (initialized || bound) {
    RecordFrontendTextureConsumerResourceBoundary(
        "frontend_texture_consumer_list200_init",
        0,
        list_guest);
    RecordRefiiMainMenuBlockerEvent(
        "frontend_descriptor_list200_init",
        fmt::format("source={} owner=0x{:08X} before=0x{:08X} "
                    "list=0x{:08X} list_owner=0x{:08X} initialized={} "
                    "bound={} owner_slots=[{}]",
                    source_api,
                    owner_guest,
                    before_list_guest,
                    list_guest,
                    list_owner_guest,
                    initialized ? "true" : "false",
                    bound ? "true" : "false",
                    FormatFrontendTextureConsumerOwnerSlots(base,
                                                            owner_guest)));
    if (VfsTracingEnabled() || LooseAssetOverrideTracingEnabled()) {
      REXLOG_INFO("Refii VFS frontend texture list200 init source={} "
                  "owner=0x{:08X} before=0x{:08X} list=0x{:08X} "
                  "list_owner=0x{:08X} initialized={} bound={} "
                  "owner_slots=[{}]",
                  source_api,
                  owner_guest,
                  before_list_guest,
                  list_guest,
                  list_owner_guest,
                  initialized ? "true" : "false",
                  bound ? "true" : "false",
                  FormatFrontendTextureConsumerOwnerSlots(base, owner_guest));
    }
  }

  return initialized || bound;
}

bool TrySynthesizeFrontendTextureDescriptorPhase(PPCContext& source_ctx,
                                                 uint8_t* base,
                                                 uint32_t owner_guest,
                                                 bool allow_followon_frame,
                                                 bool allow_native_list200) {
  if (base == nullptr || !LooksLikeGuestPointer(owner_guest) ||
      LoadGuestU32(base, owner_guest) != kFrontendTextureConsumerObjectVTable) {
    return false;
  }

  const uint32_t fixed_slot_260_ref = LoadGuestU32(base, owner_guest + 20);
  const uint32_t fixed_slot_268_ref = LoadGuestU32(base, owner_guest + 24);
  bool opened_any = false;
  opened_any |= TrySynthesizeFrontendTextureDescriptorSlot(source_ctx,
                                                           base,
                                                           owner_guest,
                                                           0x6B8D400Fu,
                                                           0x823787D4u,
                                                           0x823787E4u,
                                                           0x82378820u,
                                                           0x8237882Cu,
                                                           260,
                                                           fixed_slot_260_ref,
                                                           nullptr,
                                                           nullptr,
                                                           nullptr);
  opened_any |= TrySynthesizeFrontendTextureDescriptorSlot(source_ctx,
                                                           base,
                                                           owner_guest,
                                                           0x448CC960u,
                                                           0x82378884u,
                                                           0x82378894u,
                                                           0x823788D0u,
                                                           0x823788DCu,
                                                           268,
                                                           fixed_slot_268_ref,
                                                           nullptr,
                                                           nullptr,
                                                           nullptr);
  TraceFrontendTextureSyntheticPhaseStep("fixed_slots",
                                         base,
                                         owner_guest,
                                         opened_any);
  const bool static_table =
      TrySynthesizeFrontendTextureStaticTablePhase(source_ctx,
                                                  base,
                                                  owner_guest);
  opened_any |= static_table;
  TraceFrontendTextureSyntheticPhaseStep("static_table",
                                         base,
                                         owner_guest,
                                         static_table);
  const bool list200_initialized =
      allow_followon_frame && allow_native_list200 && static_table &&
      TryInitializeFrontendTextureConsumerList200(
          source_ctx,
          base,
          owner_guest,
          "FrontendTextureConsumerSynthetic");
  opened_any |= list200_initialized;
  if (allow_followon_frame && static_table && !allow_native_list200) {
    RecordRefiiMainMenuBlockerEvent(
        "frontend_descriptor_list200_native_skipped",
        fmt::format("owner=0x{:08X} reason=collapsed_vfs_shape_only "
                    "owner_slots=[{}]",
                    owner_guest,
                    FormatFrontendTextureConsumerOwnerSlots(base,
                                                            owner_guest)));
    TraceFrontendTextureSyntheticPhaseStep("list200_native_skipped",
                                           base,
                                           owner_guest,
                                           true);
  } else {
    TraceFrontendTextureSyntheticPhaseStep("list200",
                                           base,
                                           owner_guest,
                                           list200_initialized);
  }
  bool final_direct_wrapper_committed = false;
  const bool final_direct =
      TrySynthesizeFrontendTextureFinalDirectPhase(
          source_ctx, base, owner_guest, &final_direct_wrapper_committed);
  opened_any |= final_direct;
  TraceFrontendTextureSyntheticPhaseStep("final_direct",
                                         base,
                                         owner_guest,
                                         final_direct);
  const uint32_t final_direct_handle = LoadGuestU32(base, owner_guest + 280);
  const bool final_direct_handle_valid =
      final_direct && final_direct_handle != 0 &&
      final_direct_handle != 0xFFFFFFFFu;
  const bool followon_frame =
      allow_followon_frame && final_direct_handle_valid &&
      TrySynthesizeFrontendTextureFollowonFramePhase(source_ctx,
                                                    base,
                                                    owner_guest);
  opened_any |= followon_frame;
  TraceFrontendTextureSyntheticPhaseStep("followon_frame",
                                         base,
                                         owner_guest,
                                         followon_frame);
  constexpr uint32_t kFinalDirectHash = 0x73BCB48Fu;
  const bool final_direct_resource_prime =
      allow_followon_frame && final_direct_handle_valid &&
      !final_direct_wrapper_committed &&
      TryPrimeRefiiFrontendTextureResource(
          source_ctx,
          base,
          kFinalDirectHash,
          "FrontendTextureConsumerFinalDirectPrime");
  opened_any |= final_direct_resource_prime;
  TraceFrontendTextureSyntheticPhaseStep("final_direct_resource_prime",
                                         base,
                                         owner_guest,
                                         final_direct_resource_prime);
  constexpr uint32_t kFrameDetailHash = 0xC5A8E73Cu;
  const bool frame_detail_resource_prime =
      followon_frame &&
      TryPrimeRefiiFrontendTextureResource(
          source_ctx,
          base,
          kFrameDetailHash,
          "FrontendTextureConsumerFollowonFramePrime");
  opened_any |= frame_detail_resource_prime;
  TraceFrontendTextureSyntheticPhaseStep("frame_detail_resource_prime",
                                         base,
                                         owner_guest,
                                         frame_detail_resource_prime);
  if (opened_any) {
    RecordRefiiMainMenuBlockerEvent(
        "frontend_descriptor_synthetic_phase",
        fmt::format("owner=0x{:08X} fixed_slot_260_ref=0x{:08X} "
                    "fixed_slot_268_ref=0x{:08X} static_table={} "
                    "list200_initialized={} "
                    "final_direct={} final_direct_wrapper={} "
                    "final_direct_handle=0x{:08X} "
                    "final_direct_handle_valid={} "
                    "followon_allowed={} followon_frame={} "
                    "native_list200_allowed={} "
                    "final_direct_resource_prime={} "
                    "frame_detail_resource_prime={} "
                    "owner_slots=[{}]",
                    owner_guest,
                    fixed_slot_260_ref,
                    fixed_slot_268_ref,
                    static_table ? "true" : "false",
                    list200_initialized ? "true" : "false",
                    final_direct ? "true" : "false",
                    final_direct_wrapper_committed ? "true" : "false",
                    final_direct_handle,
                    final_direct_handle_valid ? "true" : "false",
                    allow_followon_frame ? "true" : "false",
                    followon_frame ? "true" : "false",
                    allow_native_list200 ? "true" : "false",
                    final_direct_resource_prime ? "true" : "false",
                    frame_detail_resource_prime ? "true" : "false",
                    FormatFrontendTextureConsumerOwnerSlots(base,
                                                            owner_guest)));
  }
  if (VfsTracingEnabled() &&
      (allow_followon_frame || final_direct || final_direct_wrapper_committed)) {
    REXLOG_INFO("Refii VFS frontend texture synthetic phase result "
                "owner=0x{:08X} static_table={} final_direct={} "
                "list200_initialized={} final_direct_handle=0x{:08X} "
                "final_direct_handle_valid={} "
                "final_direct_wrapper={} followon_allowed={} "
                "followon_frame={} native_list200_allowed={} "
                "final_direct_resource_prime={} "
                "frame_detail_resource_prime={} "
                "owner_slots=[{}]",
                owner_guest,
                static_table ? "true" : "false",
                final_direct ? "true" : "false",
                list200_initialized ? "true" : "false",
                final_direct_handle,
                final_direct_handle_valid ? "true" : "false",
                final_direct_wrapper_committed ? "true" : "false",
                allow_followon_frame ? "true" : "false",
                followon_frame ? "true" : "false",
                allow_native_list200 ? "true" : "false",
                final_direct_resource_prime ? "true" : "false",
                frame_detail_resource_prime ? "true" : "false",
                FormatFrontendTextureConsumerOwnerSlots(base, owner_guest));
  }
  return opened_any;
}

bool TryRunFrontendTextureConsumerNativeUpdateForPendingVfsDirect(
    PPCContext& source_ctx,
    uint8_t* base,
    uint32_t owner_guest,
    std::string_view source_api) {
  if (base == nullptr || !LooksLikeGuestPointer(owner_guest) ||
      LoadGuestU32(base, owner_guest) != kFrontendTextureConsumerObjectVTable) {
    return false;
  }

  const uint8_t pending_direct = LoadGuestU8(base, owner_guest + 276);
  const uint32_t direct_handle = LoadGuestU32(base, owner_guest + 280);
  if (pending_direct == 0 || direct_handle == 0 ||
      direct_handle == 0xFFFFFFFFu) {
    return false;
  }

  const bool use_refii_collapsed_prefix =
      source_api == "sub_82378618_collapsed" ||
      StartsWith(source_api, "sub_826DB598_static_bridge") ||
      source_api == "PostVideoFrontendSyntheticHandoff";
  if (use_refii_collapsed_prefix) {
    const uint32_t slot260_guest = LoadGuestU32(base, owner_guest + 260);
    const uint32_t slot268_guest = LoadGuestU32(base, owner_guest + 268);
    const uint32_t direct_list_tail_guest = LoadGuestU32(base, owner_guest + 148);
    const bool generated_preconditions =
        LooksLikeGuestPointer(slot260_guest) &&
        LooksLikeGuestPointer(slot268_guest) &&
        LoadGuestU32(base, slot260_guest + 8) != 0 &&
        LoadGuestU32(base, slot268_guest + 8) != 0 &&
        LooksLikeGuestPointer(direct_list_tail_guest);
    if (generated_preconditions) {
      const std::string owner_slots_before =
          FormatFrontendTextureConsumerOwnerSlots(base, owner_guest);
      uint32_t append_result = 0;
      bool append_attempted = false;
      if (direct_handle != 0xFFFFFFFFu) {
        PPCContext append_ctx = source_ctx;
        const uint32_t original_sp = append_ctx.r1.u32;
        append_ctx.r1.u32 = original_sp - 128;
        StoreGuestU32(base, append_ctx.r1.u32, original_sp);
        StoreGuestU32(base, append_ctx.r1.u32 + 80, direct_handle);
        StoreGuestU32(base, append_ctx.r1.u32 + 88, owner_guest + 144);
        StoreGuestU32(base,
                      append_ctx.r1.u32 + 92,
                      LoadGuestU32(base, owner_guest + 148));
        append_ctx.r3.u32 = owner_guest + 144;
        append_ctx.r4.u64 = LoadGuestU64(base, append_ctx.r1.u32 + 88);
        append_ctx.r5.u32 = append_ctx.r1.u32 + 80;
        append_ctx.lr = 0x823791F4u;
        sub_82A97368(append_ctx, base);
        append_result = append_ctx.r3.u32;
        append_attempted = true;
        StoreGuestU32(base, owner_guest + 280, 0xFFFFFFFFu);
      }

      uint32_t resource_touch_result = 0;
      uint32_t resource_commit_result = 0;
      uint32_t touched_resource_guest = 0;
      bool resource_commit_attempted = false;
      const uint32_t resource_owner_guest = LoadGuestU32(base, owner_guest + 180);
      if (LooksLikeGuestPointer(resource_owner_guest)) {
        touched_resource_guest = LoadGuestU32(base, resource_owner_guest + 20);
      }
      if (LooksLikeGuestPointer(touched_resource_guest)) {
        PPCContext touch_ctx = source_ctx;
        touch_ctx.r3.u32 = touched_resource_guest;
        touch_ctx.lr = 0x8237920Cu;
        sub_82426518(touch_ctx, base);
        resource_touch_result = touch_ctx.r3.u32;

        PPCContext commit_ctx = touch_ctx;
        commit_ctx.r4.u32 = touched_resource_guest;
        commit_ctx.r5.u32 = 1;
        commit_ctx.lr = 0x82379218u;
        sub_82426CA8(commit_ctx, base);
        resource_commit_result = commit_ctx.r3.u32;
        resource_commit_attempted = true;
      }

      StoreGuestU8(base, owner_guest + 276, 0);
      const uint8_t pending_after = LoadGuestU8(base, owner_guest + 276);
      const uint32_t direct_after = LoadGuestU32(base, owner_guest + 280);
      const uint32_t handoff_after = LoadGuestU32(base, owner_guest + 208);
      RecordFrontendTextureConsumerResourceBoundary(
          "sub_82379170_collapsed_pending_direct_prefix",
          0x73BCB48Fu,
          handoff_after != 0 ? handoff_after : direct_after);
      RecordRefiiMainMenuBlockerEvent(
          "frontend_descriptor_collapsed_pending_direct_prefix",
          fmt::format("source={} owner=0x{:08X} result=0x{:08X} "
                      "pending276=0x{:02X}->0x{:02X} "
                      "direct280=0x{:08X}->0x{:08X} "
                      "handoff208=0x{:08X} list_tail=0x{:08X} "
                      "append={} append_result=0x{:08X} "
                      "resource=0x{:08X} resource_commit={} "
                      "touch_result=0x{:08X} commit_result=0x{:08X} "
                      "before=[{}] after=[{}]",
                      source_api,
                      owner_guest,
                      append_result != 0 ? append_result : handoff_after,
                      pending_direct,
                      pending_after,
                      direct_handle,
                      direct_after,
                      handoff_after,
                      direct_list_tail_guest,
                      append_attempted ? "true" : "false",
                      append_result,
                      touched_resource_guest,
                      resource_commit_attempted ? "true" : "false",
                      resource_touch_result,
                      resource_commit_result,
                      owner_slots_before,
                      FormatFrontendTextureConsumerOwnerSlots(base,
                                                              owner_guest)));
      if (VfsTracingEnabled() || LooseAssetOverrideTracingEnabled()) {
        REXLOG_INFO("Refii VFS frontend texture collapsed pending-direct "
                    "source={} owner=0x{:08X} result=0x{:08X} "
                    "pending276=0x{:02X}->0x{:02X} "
                    "direct280=0x{:08X}->0x{:08X} "
                    "handoff208=0x{:08X} list_tail=0x{:08X} "
                    "append={} append_result=0x{:08X} "
                    "resource=0x{:08X} resource_commit={} "
                    "touch_result=0x{:08X} commit_result=0x{:08X} "
                    "before=[{}] after=[{}] main_menu=[{}]",
                    source_api,
                    owner_guest,
                    append_result != 0 ? append_result : handoff_after,
                    pending_direct,
                    pending_after,
                    direct_handle,
                    direct_after,
                    handoff_after,
                    direct_list_tail_guest,
                    append_attempted ? "true" : "false",
                    append_result,
                    touched_resource_guest,
                    resource_commit_attempted ? "true" : "false",
                    resource_touch_result,
                    resource_commit_result,
                    owner_slots_before,
                    FormatFrontendTextureConsumerOwnerSlots(base, owner_guest),
                    FormatRefiiMainMenuBlockerTraceForLog());
      }
      return true;
    }

    RecordFrontendTextureConsumerResourceBoundary(
        "sub_82379170_pending_vfs_direct_update_deferred",
        0x73BCB48Fu,
        direct_handle);
    RecordRefiiMainMenuBlockerEvent(
        "frontend_descriptor_pending_direct_native_update_deferred",
        fmt::format("source={} owner=0x{:08X} pending276=0x{:02X} "
                    "direct280=0x{:08X} handoff208=0x{:08X} "
                    "slot260=0x{:08X} slot268=0x{:08X} "
                    "list_tail=0x{:08X} owner_slots=[{}]",
                    source_api,
                    owner_guest,
                    pending_direct,
                    direct_handle,
                    LoadGuestU32(base, owner_guest + 208),
                    slot260_guest,
                    slot268_guest,
                    direct_list_tail_guest,
                    FormatFrontendTextureConsumerOwnerSlots(base,
                                                            owner_guest)));
    if (VfsTracingEnabled() || LooseAssetOverrideTracingEnabled()) {
      REXLOG_INFO("Refii VFS frontend texture pending direct native update "
                  "deferred source={} owner=0x{:08X} pending276=0x{:02X} "
                  "direct280=0x{:08X} handoff208=0x{:08X} "
                  "slot260=0x{:08X} slot268=0x{:08X} list_tail=0x{:08X} "
                  "owner_slots=[{}] main_menu=[{}]",
                  source_api,
                  owner_guest,
                  pending_direct,
                  direct_handle,
                  LoadGuestU32(base, owner_guest + 208),
                  slot260_guest,
                  slot268_guest,
                  direct_list_tail_guest,
                  FormatFrontendTextureConsumerOwnerSlots(base, owner_guest),
                  FormatRefiiMainMenuBlockerTraceForLog());
    }
    return false;
  }

  const std::string owner_slots_before =
      FormatFrontendTextureConsumerOwnerSlots(base, owner_guest);
  PPCContext update_ctx = source_ctx;
  update_ctx.r3.u32 = owner_guest;
  update_ctx.lr = 0x82379170u;
  ::__imp__sub_82379170(update_ctx, base);
  const uint8_t pending_after = LoadGuestU8(base, owner_guest + 276);
  const uint32_t direct_after = LoadGuestU32(base, owner_guest + 280);
  const uint32_t handoff_after = LoadGuestU32(base, owner_guest + 208);
  const std::string owner_slots_after =
      FormatFrontendTextureConsumerOwnerSlots(base, owner_guest);

  RecordFrontendTextureConsumerResourceBoundary(
      "sub_82379170_pending_vfs_direct_update",
      0x73BCB48Fu,
      direct_after == 0xFFFFFFFFu ? handoff_after : direct_after);
  RecordRefiiMainMenuBlockerEvent(
      "frontend_descriptor_pending_direct_native_update",
      fmt::format("source={} owner=0x{:08X} result=0x{:08X} "
                  "pending276=0x{:02X}->0x{:02X} "
                  "direct280=0x{:08X}->0x{:08X} "
                  "handoff208=0x{:08X} before=[{}] after=[{}]",
                  source_api,
                  owner_guest,
                  update_ctx.r3.u32,
                  pending_direct,
                  pending_after,
                  direct_handle,
                  direct_after,
                  handoff_after,
                  owner_slots_before,
                  owner_slots_after));
  if (VfsTracingEnabled() || LooseAssetOverrideTracingEnabled()) {
    REXLOG_INFO("Refii VFS frontend texture pending direct native update "
                "source={} owner=0x{:08X} result=0x{:08X} "
                "pending276=0x{:02X}->0x{:02X} "
                "direct280=0x{:08X}->0x{:08X} handoff208=0x{:08X} "
                "before=[{}] after=[{}] main_menu=[{}]",
                source_api,
                owner_guest,
                update_ctx.r3.u32,
                pending_direct,
                pending_after,
                direct_handle,
                direct_after,
                handoff_after,
                owner_slots_before,
                owner_slots_after,
                FormatRefiiMainMenuBlockerTraceForLog());
  }
  return true;
}

bool TryCollapseFrontendTextureGeneratedHandoff(PPCContext& ctx,
                                                uint8_t* base,
                                                bool synthesize_missing_followon,
                                                std::string_view source_api) {
  const PPCContext original_ctx = ctx;
  const uint32_t owner_guest = original_ctx.r3.u32;
  if (base == nullptr ||
      static_cast<uint32_t>(original_ctx.lr) != 0x82378DB4u ||
      !LooksLikeGuestPointer(owner_guest) ||
      LoadGuestU32(base, owner_guest) != kFrontendTextureConsumerObjectVTable) {
    return false;
  }

  const uint32_t final_direct_handle = LoadGuestU32(base, owner_guest + 280);
  uint32_t list_guest = LoadGuestU32(base, owner_guest + 200);
  const uint32_t previous_handoff = LoadGuestU32(base, owner_guest + 208);
  uint32_t list_vtable =
      LooksLikeGuestPointer(list_guest) ? LoadGuestU32(base, list_guest) : 0;
  const bool final_direct_handle_valid =
      final_direct_handle != 0 && final_direct_handle != 0xFFFFFFFFu;
  const bool followon_already_committed =
      g_active_frontend_texture_consumer_trace.enabled &&
      g_active_frontend_texture_consumer_trace.followon_frame_list_committed;
  const uint32_t trace_followon_handle =
      g_active_frontend_texture_consumer_trace.enabled
          ? g_active_frontend_texture_consumer_trace.last_resolved_object
          : 0;
  bool followon_frame = final_direct_handle_valid && followon_already_committed;
  if (!followon_frame && final_direct_handle_valid &&
      synthesize_missing_followon) {
    PPCContext synth_ctx = original_ctx;
    followon_frame =
        TrySynthesizeFrontendTextureFollowonFramePhase(synth_ctx,
                                                       base,
                                                       owner_guest);
  }

  bool list200_initialized = false;
  if (final_direct_handle_valid && followon_frame &&
      !LooksLikeGuestPointer(list_guest)) {
    PPCContext list_ctx = original_ctx;
    list200_initialized =
        TryInitializeFrontendTextureConsumerList200(list_ctx,
                                                    base,
                                                    owner_guest,
                                                    source_api);
    list_guest = LoadGuestU32(base, owner_guest + 200);
    list_vtable =
        LooksLikeGuestPointer(list_guest) ? LoadGuestU32(base, list_guest) : 0;
  }

  const bool list_vtable_ok =
      LooksLikeGuestPointer(list_guest) && LooksLikeGuestPointer(list_vtable) &&
      FindGuestFunction(LoadGuestU32(base, list_vtable + 36)) != nullptr &&
      FindGuestFunction(LoadGuestU32(base, list_vtable + 92)) != nullptr &&
      FindGuestFunction(LoadGuestU32(base, list_vtable + 100)) != nullptr;
  const bool native_binding_allowed =
      final_direct_handle_valid && followon_frame && list_vtable_ok;
  bool native_binding_attempted = false;
  bool native_binding_kept = false;
  bool fallback_binding_used = false;
  uint32_t native_binding_result = 0;
  if (native_binding_allowed) {
    PPCContext native_ctx = original_ctx;
    native_ctx.r3.u32 = owner_guest;
    ::__imp__sub_82379088(native_ctx, base);
    native_binding_attempted = true;
    native_binding_result = LoadGuestU32(base, owner_guest + 208);
    native_binding_kept =
        native_binding_result == 0 || LooksLikeGuestPointer(native_binding_result);
  }

  // The generated body binds an optional list item into object+208 through
  // object+200 virtual calls. Now that list200 is initialized, try that native
  // binding first; if it produces no concrete value, keep the Refii-created
  // descriptor handle instead of erasing the handoff slot entirely.
  if (native_binding_attempted && native_binding_result == 0 &&
      final_direct_handle_valid) {
    const uint32_t fallback_handle =
        trace_followon_handle != 0 && trace_followon_handle != 0xFFFFFFFFu
            ? trace_followon_handle
            : final_direct_handle;
    StoreGuestU32(base, owner_guest + 208, fallback_handle);
    native_binding_result = fallback_handle;
    native_binding_kept = true;
    fallback_binding_used = true;
  }
  if (!native_binding_attempted && final_direct_handle_valid &&
      followon_frame) {
    const uint32_t fallback_handle =
        trace_followon_handle != 0 && trace_followon_handle != 0xFFFFFFFFu
            ? trace_followon_handle
            : final_direct_handle;
    StoreGuestU32(base, owner_guest + 208, fallback_handle);
    native_binding_result = fallback_handle;
    native_binding_kept = true;
    fallback_binding_used = true;
  }
  if (!native_binding_kept) {
    StoreGuestU32(base, owner_guest + 208, 0);
  }
  RecordFrontendTextureConsumerResourceBoundary(
      "sub_82379088_collapsed_generated_handoff",
      0,
      followon_frame ? final_direct_handle : 0);
  RecordRefiiMainMenuBlockerEvent(
      "frontend_descriptor_collapsed_generated_handoff",
      fmt::format("source={} object=0x{:08X} list=0x{:08X} "
                  "previous_208=0x{:08X} final_direct=0x{:08X} "
                  "final_direct_valid={} followon_precommitted={} "
                  "trace_followon_handle=0x{:08X} "
                  "followon_synth_allowed={} followon_frame={} "
                  "list200_initialized={} "
                  "native_binding_allowed={} native_binding_attempted={} "
                  "native_binding_result=0x{:08X} native_binding_kept={} "
                  "fallback_binding_used={} "
                  "owner_slots=[{}]",
                  source_api,
                  owner_guest,
                  list_guest,
                  previous_handoff,
                  final_direct_handle,
                  final_direct_handle_valid ? "true" : "false",
                  followon_already_committed ? "true" : "false",
                  trace_followon_handle,
                  synthesize_missing_followon ? "true" : "false",
                  followon_frame ? "true" : "false",
                  list200_initialized ? "true" : "false",
                  native_binding_allowed ? "true" : "false",
                  native_binding_attempted ? "true" : "false",
                  native_binding_result,
                  native_binding_kept ? "true" : "false",
                  fallback_binding_used ? "true" : "false",
                  FormatFrontendTextureConsumerOwnerSlots(base,
                                                          owner_guest)));
  if (VfsTracingEnabled()) {
    REXLOG_INFO("Refii VFS frontend texture generated handoff collapsed "
                "source={} sub_82379088 lr=0x{:08X} object=0x{:08X} "
                "list=0x{:08X} previous_208=0x{:08X} "
                "final_direct=0x{:08X} final_direct_valid={} "
                "followon_precommitted={} trace_followon_handle=0x{:08X} "
                "followon_synth_allowed={} "
                "followon_frame={} list200_initialized={} "
                "native_binding_allowed={} "
                "native_binding_attempted={} native_binding_result=0x{:08X} "
                "native_binding_kept={} fallback_binding_used={} "
                "owner_slots=[{}] main_menu=[{}]",
                source_api,
                original_ctx.lr,
                owner_guest,
                list_guest,
                previous_handoff,
                final_direct_handle,
                final_direct_handle_valid ? "true" : "false",
                followon_already_committed ? "true" : "false",
                trace_followon_handle,
                synthesize_missing_followon ? "true" : "false",
                followon_frame ? "true" : "false",
                list200_initialized ? "true" : "false",
                native_binding_allowed ? "true" : "false",
                native_binding_attempted ? "true" : "false",
                native_binding_result,
                native_binding_kept ? "true" : "false",
                fallback_binding_used ? "true" : "false",
                FormatFrontendTextureConsumerOwnerSlots(base, owner_guest),
                FormatRefiiMainMenuBlockerTraceForLog());
    LogRefiiVfsChainVerdict(
        "frontend_texture_consumer",
        static_cast<uint32_t>(original_ctx.lr),
        "menu_texture_refii_owned",
        "",
        "",
        "frontend_generated_handoff",
        "sub_82379088_collapsed",
        "TraceOnce",
        followon_frame ? "ResourceManager_Open_or_DB20"
                       : "followon_frame_descriptor_missing");
  }
  ctx = original_ctx;
  ctx.r3.u32 = 0;
  return true;
}

bool TryRunActiveFrontendTextureConsumerSyntheticPhase(PPCContext& source_ctx,
                                                       uint8_t* base,
                                                       uint32_t* out_owner_guest,
                                                       bool* out_opened_any) {
  if (out_owner_guest != nullptr) {
    *out_owner_guest = 0;
  }
  if (out_opened_any != nullptr) {
    *out_opened_any = false;
  }
  if (base == nullptr || !RefiiLooseAssetBackendEnabled() ||
      !FrontendTextureConsumerTraceActive()) {
    return false;
  }

  uint32_t owner_guest = 0;
  {
    auto& trace = g_active_frontend_texture_consumer_trace;
    if (trace.synthetic_descriptor_phase_attempted) {
      return false;
    }
    trace.synthetic_descriptor_phase_attempted = true;
    owner_guest = trace.owner_guest;
  }

  if (out_owner_guest != nullptr) {
    *out_owner_guest = owner_guest;
  }
  if (!LooksLikeGuestPointer(owner_guest) ||
      LoadGuestU32(base, owner_guest) != kFrontendTextureConsumerObjectVTable) {
    return true;
  }

  const bool allow_followon_frame =
      RefiiMainMenuTraceNeedsFrontendHandoff() &&
      (RefiiStartupVideosSettledForMenuHandoff() ||
       SkipStartupVideosEnabled());
  const bool opened_any =
      TrySynthesizeFrontendTextureDescriptorPhase(
          source_ctx,
          base,
          owner_guest,
          allow_followon_frame,
          false);
  if (out_opened_any != nullptr) {
    *out_opened_any = opened_any;
  }
  return true;
}


}  // namespace detail
}  // namespace refii::vfs
