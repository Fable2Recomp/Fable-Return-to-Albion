#include "vfs_hooks_private.h"

namespace refii::vfs {
inline namespace detail {

bool TryResolveRefiiLoadingAnimationSequenceHandle(PPCContext& source_ctx,
                                                   uint8_t* base,
                                                   std::string_view source_api,
                                                   uint32_t* out_handle) {
  if (out_handle != nullptr) {
    *out_handle = 0;
  }

  const uint32_t remembered_handle =
      RefiiLoadingAnimationResolverHandle().load(std::memory_order_relaxed);
  if (LooksLikeNativeResourceHandle(remembered_handle)) {
    if (out_handle != nullptr) {
      *out_handle = remembered_handle;
    }
    return true;
  }

  if (base == nullptr || !RefiiLooseAssetBackendEnabled()) {
    return false;
  }

  LooseAssetResolution resolution;
  std::string name;
  std::string candidate;
  std::string source;
  constexpr uint32_t kResolverLr = 0x8237AA98u;
  if (!ResolveRefiiOwnedTextureDescriptorHash(
          "LoadingSequenceHandleBootstrap",
          kRefiiLoadingAnimationHash,
          kResolverLr,
          &resolution,
          &name,
          &candidate,
          &source)) {
    if (VfsTracingEnabled() || LooseAssetOverrideTracingEnabled()) {
      REXLOG_WARN("Refii VFS loading sequence handle bootstrap miss "
                  "source={} hash=0x{:08X}",
                  source_api,
                  kRefiiLoadingAnimationHash);
    }
    return false;
  }

  LooseAssetResolution selected_resolution = resolution;
  std::string selected_candidate =
      candidate.empty() ? NormalizeLooseAssetKey(name)
                        : NormalizeLooseAssetKey(candidate);
  MaybeUpgradeResourceManagerTextureResolution(kRefiiLoadingAnimationHash,
                                               name.empty() ? selected_candidate
                                                            : name,
                                               kResolverLr,
                                               &selected_resolution,
                                               &selected_candidate);

  const uint32_t ref_object_guest =
      TryAllocateRefiiFrontendTextureObject(source_ctx,
                                            base,
                                            kRefiiLoadingAnimationHash,
                                            selected_resolution,
                                            selected_candidate,
                                            "LoadingSequenceHandleBootstrap");

  PPCContext alloc_ctx = source_ctx;
  const uint32_t descriptor_guest = CallLhMalloc(alloc_ctx, base, 32);
  const uint32_t hash_pointer_guest = CallLhMalloc(alloc_ctx, base, 4);
  const uint32_t ref_pointer_guest = CallLhMalloc(alloc_ctx, base, 16);
  if (!LooksLikeGuestPointer(descriptor_guest) ||
      !LooksLikeGuestPointer(hash_pointer_guest) ||
      !LooksLikeGuestPointer(ref_pointer_guest)) {
    if (VfsTracingEnabled() || LooseAssetOverrideTracingEnabled()) {
      REXLOG_WARN("Refii VFS loading sequence handle bootstrap alloc failed "
                  "source={} descriptor=0x{:08X} hash_ptr=0x{:08X} "
                  "ref_ptr=0x{:08X} ref_object=0x{:08X}",
                  source_api,
                  descriptor_guest,
                  hash_pointer_guest,
                  ref_pointer_guest,
                  ref_object_guest);
    }
    return false;
  }

  StoreGuestU32(base, hash_pointer_guest, kRefiiLoadingAnimationHash);
  StoreGuestU32(base, ref_pointer_guest + 0, ref_object_guest);
  StoreGuestU32(base, ref_pointer_guest + 4, 0x83360000u);
  StoreGuestU32(base, ref_pointer_guest + 8, 0);
  StoreGuestU32(base, ref_pointer_guest + 12, 0);

  BuildTextureResourceDescriptor(alloc_ctx,
                                 base,
                                 descriptor_guest,
                                 1,
                                 hash_pointer_guest,
                                 ref_pointer_guest);

  constexpr uint32_t kFrontendResolverConstantTableBase = 0x8349F9B0u;
  constexpr uint32_t kFrontendResolverTableIndex = 1;
  const uint32_t table_entry_guest =
      kFrontendResolverConstantTableBase + kFrontendResolverTableIndex * 4;
  uint32_t table_object = LoadGuestU32(base, table_entry_guest);
  if (table_object == 0) {
    table_object = FindRefiiLoadingScreenActiveResourceTable(base);
  }

  PPCContext resolver_ctx = source_ctx;
  resolver_ctx.r3.u32 = descriptor_guest;
  resolver_ctx.r4.u32 = 0x3C;
  resolver_ctx.r5.u32 = kFrontendResolverTableIndex;
  resolver_ctx.lr = kResolverLr;
  uint32_t resolver_result = 0;
  std::string failure_reason;
  const bool resolved =
      TryResolveRefiiFrontendTextureDescriptor(resolver_ctx,
                                               base,
                                               descriptor_guest,
                                               kRefiiLoadingAnimationHash,
                                               kResolverLr,
                                               kFrontendResolverTableIndex,
                                               table_object,
                                               selected_resolution,
                                               name,
                                               selected_candidate,
                                               source,
                                               &resolver_result,
                                               &failure_reason);
  if (!resolved || !LooksLikeNativeResourceHandle(resolver_result)) {
    if (VfsTracingEnabled() || LooseAssetOverrideTracingEnabled()) {
      REXLOG_WARN("Refii VFS loading sequence handle bootstrap failed "
                  "source={} resolved={} result=0x{:08X} reason={} "
                  "descriptor=0x{:08X} ref_object=0x{:08X} "
                  "table_object=0x{:08X} candidate={} provider={}",
                  source_api,
                  resolved ? "true" : "false",
                  resolver_result,
                  failure_reason,
                  descriptor_guest,
                  ref_object_guest,
                  table_object,
                  selected_candidate,
                  NormalizeLooseAssetKey(
                      selected_resolution.provider_relative_path));
    }
    return false;
  }

  RecordRefiiLoadingSequenceResolverHandle(kRefiiLoadingAnimationHash,
                                           resolver_result,
                                           kResolverLr,
                                           kFrontendResolverTableIndex);
  RecordRefiiLoadingSequenceHandle(resolver_result);
  static std::atomic<bool> loading_animation_prime_attempted{false};
  bool loading_animation_primed = false;
  if (!loading_animation_prime_attempted.exchange(true,
                                                  std::memory_order_relaxed)) {
    PPCContext prime_ctx = source_ctx;
    prime_ctx.lr = kResolverLr;
    loading_animation_primed = TryPrimeRefiiFrontendTextureResource(
        prime_ctx,
        base,
        kRefiiLoadingAnimationHash,
        "LoadingSequenceHandleBootstrap_PrimeTexture");
    RecordRefiiMainMenuBlockerEvent(
        "loading_sequence_texture_prime",
        fmt::format("handle=0x{:08X} primed={} hash=0x{:08X} "
                    "candidate={} provider={}",
                    resolver_result,
                    loading_animation_primed ? "true" : "false",
                    kRefiiLoadingAnimationHash,
                    selected_candidate,
                    NormalizeLooseAssetKey(
                        selected_resolution.provider_relative_path)));
  }
  if (out_handle != nullptr) {
    *out_handle = resolver_result;
  }
  if (VfsTracingEnabled() || LooseAssetOverrideTracingEnabled()) {
    REXLOG_INFO("Refii VFS loading sequence handle bootstrap resolved "
                "source={} handle=0x{:08X} descriptor=0x{:08X} "
                "ref_object=0x{:08X} table_object=0x{:08X} candidate={} "
                "provider={} texture_primed={}",
                source_api,
                resolver_result,
                descriptor_guest,
                ref_object_guest,
                table_object,
                selected_candidate,
                NormalizeLooseAssetKey(selected_resolution.provider_relative_path),
                loading_animation_primed ? "true" : "false");
  }
  return true;
}

bool TryBridgeLoadingScreenSequenceListFromRefiiHandle(PPCContext& source_ctx,
                                                       uint8_t* base,
                                                       uint32_t owner_guest,
                                                       uint32_t* out_handle) {
  if (out_handle != nullptr) {
    *out_handle = 0;
  }
  if (base == nullptr || !LooksLikeGuestPointer(owner_guest)) {
    return false;
  }

  constexpr uint32_t kLoadingGlobalBase = 0x83490000u;
  constexpr uint32_t kPrimaryOwnerGlobal = kLoadingGlobalBase + 27320u;
  constexpr uint32_t kSequenceRootGlobal = kLoadingGlobalBase + 30144u;
  constexpr uint32_t kSecondaryOwnerGlobal = kLoadingGlobalBase + 27328u;
  const uint32_t primary_owner = LoadGuestU32(base, kPrimaryOwnerGlobal);
  const uint32_t secondary_owner = LoadGuestU32(base, kSecondaryOwnerGlobal);
  const uint32_t active_table = FindRefiiLoadingScreenActiveResourceTable(base);
  const uint32_t sequence_root = LoadGuestU32(base, kSequenceRootGlobal);
  const uint32_t map356_count =
      CountGuestLinearVector(base, owner_guest, 356, 20);
  const bool looks_like_loading_screen_owner =
      map356_count != 0 || LoadGuestU8(base, owner_guest + 313) != 0 ||
      LoadGuestU8(base, owner_guest + 314) != 0 ||
      ((owner_guest == primary_owner || owner_guest == secondary_owner) &&
       (LooksLikeGuestPointer(sequence_root) ||
        LooksLikeGuestDataPointer(active_table)));
  if (!looks_like_loading_screen_owner) {
    return false;
  }

  const bool post_gameface_primary_owner_published =
      LooksLikeGuestPointer(LoadGuestU32(base, kPrimaryOwnerGlobal)) &&
      RefiiPostGameFacePackageHandoffSignalEmitted();

  uint32_t loading_animation_handle =
      RefiiLoadingAnimationResolverHandle().load(std::memory_order_relaxed);
  const uint32_t owner_slot180_handle = LoadGuestU32(base, owner_guest + 180);
  if (!LooksLikeNativeResourceHandle(loading_animation_handle) &&
      LooksLikeNativeResourceHandle(owner_slot180_handle)) {
    loading_animation_handle = owner_slot180_handle;
  }
  const bool has_loading_animation_handle =
      LooksLikeNativeResourceHandle(loading_animation_handle);
  if (map356_count == 0 && !has_loading_animation_handle &&
      !LooksLikeGuestPointer(sequence_root)) {
    return false;
  }
  if (active_table == 0 && !post_gameface_primary_owner_published &&
      !has_loading_animation_handle) {
    return false;
  }
  if (!LooksLikeNativeResourceHandle(loading_animation_handle)) {
    uint32_t bootstrapped_handle = 0;
    if (TryResolveRefiiLoadingAnimationSequenceHandle(
            source_ctx, base, "loading_sequence_bridge", &bootstrapped_handle)) {
      loading_animation_handle = bootstrapped_handle;
    }
  }
  if (!LooksLikeNativeResourceHandle(loading_animation_handle)) {
    if (VfsTracingEnabled() || LooseAssetOverrideTracingEnabled()) {
      REXLOG_WARN("Refii VFS loading sequence bridge missing handle "
                  "owner=0x{:08X} owner_slot180=0x{:08X} remembered=0x{:08X} "
                  "map356_count={} active_table=0x{:08X} "
                  "sequence_root=0x{:08X} "
                  "owner_words=[{}]",
                  owner_guest,
                  owner_slot180_handle,
                  RefiiLoadingAnimationResolverHandle().load(
                      std::memory_order_relaxed),
                  map356_count,
                  active_table,
                  sequence_root,
                  FormatGuestWordsIfPointer(base, owner_guest, 56));
    }
    return false;
  }

  const uint32_t previous_bridged_handle =
      RefiiBridgedLoadingSequenceHandle().load(std::memory_order_relaxed);
  const uint32_t existing_sequence_count = LoadGuestU32(base, owner_guest + 388);
  if (existing_sequence_count != 0 &&
      previous_bridged_handle == loading_animation_handle) {
    return false;
  }

  const std::string before_raw380 =
      (VfsTracingEnabled() || LooseAssetOverrideTracingEnabled())
          ? FormatGuestWordsAtOffsetIfPointer(base, owner_guest, 380, 8)
          : std::string{};
  const uint32_t before_table = LoadGuestU32(base, owner_guest + 380);
  const uint32_t before_cursor = LoadGuestU32(base, owner_guest + 384);
  const uint32_t before_count = LoadGuestU32(base, owner_guest + 388);

  PPCContext append_ctx = source_ctx;
  const uint32_t original_sp = append_ctx.r1.u32;
  append_ctx.r1.u32 = original_sp - 128;
  StoreGuestU32(base, append_ctx.r1.u32, original_sp);
  StoreGuestU32(base, append_ctx.r1.u32 + 80, loading_animation_handle);
  StoreGuestU32(base, append_ctx.r1.u32 + 88, owner_guest + 380);
  StoreGuestU32(base,
                append_ctx.r1.u32 + 92,
                LoadGuestU32(base, owner_guest + 384));
  append_ctx.r3.u32 = owner_guest + 380;
  append_ctx.r4.u64 = LoadGuestU64(base, append_ctx.r1.u32 + 88);
  append_ctx.r5.u32 = append_ctx.r1.u32 + 80;
  append_ctx.lr = 0x8235E580u;
  sub_82A97368(append_ctx, base);

  const bool bridged = LoadGuestU32(base, owner_guest + 388) != 0;
  if (out_handle != nullptr && bridged) {
    *out_handle = loading_animation_handle;
  }
  if (bridged) {
    if (before_count != 0 &&
        previous_bridged_handle != loading_animation_handle) {
      StoreGuestU32(base, owner_guest + 300, before_count);
    }
    RefiiBridgedLoadingSequenceHandle().store(loading_animation_handle,
                                              std::memory_order_relaxed);
    RecordRefiiLoadingSequenceHandle(loading_animation_handle);
    if (VfsTracingEnabled() || LooseAssetOverrideTracingEnabled()) {
      REXLOG_INFO("Refii VFS bridged loading sequence handle "
                  "owner=0x{:08X} handle=0x{:08X} previous=0x{:08X} "
                  "owner_slot180=0x{:08X} "
                  "table380=0x{:08X}->0x{:08X} cursor384=0x{:08X}->0x{:08X} "
                  "count388={}->{} map356_count={} raw380_before=[{}] "
                  "active_table=0x{:08X} sequence_root=0x{:08X} "
                  "raw380_after=[{}]",
                  owner_guest,
                  loading_animation_handle,
                  previous_bridged_handle,
                  owner_slot180_handle,
                  before_table,
                  LoadGuestU32(base, owner_guest + 380),
                  before_cursor,
                  LoadGuestU32(base, owner_guest + 384),
                  before_count,
                  LoadGuestU32(base, owner_guest + 388),
                  map356_count,
                  before_raw380,
                  active_table,
                  sequence_root,
                  FormatGuestWordsAtOffsetIfPointer(base, owner_guest, 380, 8));
    }
  } else if (VfsTracingEnabled() || LooseAssetOverrideTracingEnabled()) {
    REXLOG_WARN("Refii VFS loading sequence bridge append failed "
                "owner=0x{:08X} handle=0x{:08X} previous=0x{:08X} "
                "owner_slot180=0x{:08X} table380=0x{:08X}->0x{:08X} "
                "cursor384=0x{:08X}->0x{:08X} count388={}->{} "
                "map356_count={} active_table=0x{:08X} "
                "sequence_root=0x{:08X} raw380_before=[{}] raw380_after=[{}]",
                owner_guest,
                loading_animation_handle,
                previous_bridged_handle,
                owner_slot180_handle,
                before_table,
                LoadGuestU32(base, owner_guest + 380),
                before_cursor,
                LoadGuestU32(base, owner_guest + 384),
                before_count,
                LoadGuestU32(base, owner_guest + 388),
                map356_count,
                active_table,
                sequence_root,
                before_raw380,
                FormatGuestWordsAtOffsetIfPointer(base, owner_guest, 380, 8));
  }
  return bridged;
}

std::string FormatRefiiLoadingScreenOwnerState(uint8_t* base,
                                               uint32_t owner_guest);

bool LooksLikeRefiiLoadingScreenSequenceOwner(uint8_t* base,
                                              uint32_t owner_guest) {
  if (base == nullptr || !LooksLikeGuestPointer(owner_guest)) {
    return false;
  }

  const uint32_t sequence_count = LoadGuestU32(base, owner_guest + 388);
  if (sequence_count == 0 || sequence_count > 1024) {
    return false;
  }

  const uint32_t map356_count =
      CountGuestLinearVector(base, owner_guest, 356, 20);
  return map356_count != 0 || LoadGuestU8(base, owner_guest + 313) != 0 ||
         LoadGuestU8(base, owner_guest + 314) != 0;
}

bool TryRefreshRememberedLoadingScreenSequenceBridge(PPCContext& source_ctx,
                                                     uint8_t* base,
                                                     std::string_view source_api,
                                                     uint32_t* out_handle) {
  if (out_handle != nullptr) {
    *out_handle = 0;
  }
  constexpr uint32_t kLoadingGlobalBase = 0x83490000u;
  constexpr uint32_t kPrimaryOwnerGlobal = kLoadingGlobalBase + 27320u;
  constexpr uint32_t kSecondaryOwnerGlobal = kLoadingGlobalBase + 27328u;
  uint32_t owner_guest =
      RefiiRememberedLoadingScreenOwner().load(std::memory_order_relaxed);
  if (base != nullptr && !LooksLikeGuestPointer(owner_guest)) {
    const uint32_t primary_owner = LoadGuestU32(base, kPrimaryOwnerGlobal);
    const uint32_t secondary_owner = LoadGuestU32(base, kSecondaryOwnerGlobal);
    if (LooksLikeGuestPointer(primary_owner)) {
      owner_guest = primary_owner;
    } else if (LooksLikeGuestPointer(secondary_owner)) {
      owner_guest = secondary_owner;
    }
  }
  if (base == nullptr || !LooksLikeGuestPointer(owner_guest)) {
    return false;
  }

  uint32_t bridged_handle = 0;
  const bool bridged = TryBridgeLoadingScreenSequenceListFromRefiiHandle(
      source_ctx, base, owner_guest, &bridged_handle);
  const bool already_ready =
      !bridged && LoadGuestU32(base, owner_guest + 388) != 0 &&
      LooksLikeNativeResourceHandle(
          RefiiBridgedLoadingSequenceHandle().load(std::memory_order_relaxed));
  if (already_ready) {
    bridged_handle =
        RefiiBridgedLoadingSequenceHandle().load(std::memory_order_relaxed);
  }
  if (out_handle != nullptr) {
    *out_handle = bridged_handle;
  }
  if ((bridged || already_ready || VfsTracingEnabled()) &&
      ShouldLogLooseStreamOperation(
          "LoadingSequenceRememberedOwnerRefresh", owner_guest, 16)) {
    REXLOG_INFO("Refii VFS loading sequence remembered-owner refresh "
                "source={} owner=0x{:08X} bridged={} ready={} handle=0x{:08X} "
                "latest=0x{:08X} state=[{}] handoff=[{}] main_menu=[{}]",
                source_api,
                owner_guest,
                bridged ? "true" : "false",
                already_ready ? "true" : "false",
                bridged_handle,
                RefiiLoadingAnimationResolverHandle().load(
                    std::memory_order_relaxed),
                FormatRefiiLoadingScreenOwnerState(base, owner_guest),
                FormatRefiiPostVideoHandoffTraceForLog(),
                FormatRefiiMainMenuBlockerTraceForLog());
  }
  return bridged || already_ready;
}

bool RefiiLoadingScreenSequenceReady(uint8_t* base,
                                     uint32_t owner_guest,
                                     uint32_t* out_handle) {
  if (out_handle != nullptr) {
    *out_handle = 0;
  }
  if (!LooksLikeRefiiLoadingScreenSequenceOwner(base, owner_guest)) {
    return false;
  }
  const uint32_t bridged_handle =
      RefiiBridgedLoadingSequenceHandle().load(std::memory_order_relaxed);
  if (!LooksLikeNativeResourceHandle(bridged_handle)) {
    return false;
  }
  if (out_handle != nullptr) {
    *out_handle = bridged_handle;
  }
  return true;
}

bool TryFinishRefiiLoadingScreenOwnerFromCollapsedPipeline(
    const PPCContext& original_ctx,
    uint8_t* base,
    uint32_t owner_guest,
    std::string_view source_api,
    uint32_t source_result,
    bool sequence_ready,
    uint32_t sequence_handle) {
  const bool supported_finish_source =
      (source_api == "sub_8235D5E8" && original_ctx.lr == 0x8235D518u) ||
      (source_api == "sub_8235E1E0" && original_ctx.lr == 0x8235D520u) ||
      (source_api == "sub_8235E420" && original_ctx.lr == 0x8235D534u) ||
      (source_api == "sub_82360418" && original_ctx.lr == 0x8235D574u) ||
      (source_api == "sub_82360618" && original_ctx.lr == 0x8235D5D0u) ||
      source_api == "sub_82360968" ||
      (source_api == "post_loading_after_holder_primary_publish" &&
       original_ctx.lr == 0x8238866Cu) ||
      (source_api == "completed_owner_wait_release" &&
       original_ctx.lr == 0x82B48B08u) ||
      (source_api == "Renderer_BeginScene_PostReady" &&
       original_ctx.lr == 0x823892F0u);

  auto log_skip = [&](std::string_view reason) {
    if (VfsTracingEnabled() ||
        ShouldLogRepeatedVfsTraceOperation("LoadingOwnerFinishSkip", 12)) {
      REXLOG_INFO("Refii VFS loading owner finish skipped "
                  "reason={} source={} owner=0x{:08X} result=0x{:08X} "
                  "lr=0x{:08X} supported={} sequence_ready={} "
                  "sequence_handle=0x{:08X} backend={} videos_ready={} "
                  "gameface_signal={} frontend_consumed={} "
                  "owner_state=[{}] globals=[{}] handoff=[{}] "
                  "main_menu=[{}]",
                  reason,
                  source_api,
                  owner_guest,
                  source_result,
                  static_cast<uint32_t>(original_ctx.lr),
                  supported_finish_source ? "true" : "false",
                  sequence_ready ? "true" : "false",
                  sequence_handle,
                  RefiiLooseAssetBackendEnabled() ? "true" : "false",
                  RefiiStartupVideosReadyForMenuHandoff() ? "true" : "false",
                  RefiiPostGameFacePackageHandoffSignalEmitted() ? "true"
                                                                 : "false",
                  RefiiPostVideoFrontendChainConsumed() ? "true" : "false",
                  FormatRefiiLoadingScreenOwnerState(base, owner_guest),
                  FormatRefiiLoadingScreenSequenceGlobals(base),
                  FormatRefiiPostVideoHandoffTraceForLog(),
                  FormatRefiiMainMenuBlockerTraceForLog());
    }
  };

  if (base == nullptr) {
    log_skip("base_null");
    return false;
  }
  if (!LooksLikeGuestPointer(owner_guest)) {
    log_skip("owner_invalid");
    return false;
  }
  if (!supported_finish_source) {
    log_skip("unsupported_source");
    return false;
  }
  if (source_result == 0) {
    log_skip("source_result_zero");
    return false;
  }
  if (!RefiiLooseAssetBackendEnabled()) {
    log_skip("backend_disabled");
    return false;
  }
  if (!RefiiStartupVideosReadyForMenuHandoff()) {
    log_skip("startup_videos_not_ready");
    return false;
  }
  if (!RefiiPostGameFacePackageHandoffSignalEmitted()) {
    log_skip("gameface_signal_missing");
    return false;
  }
  if (!RefiiPostVideoFrontendChainConsumed()) {
    log_skip("frontend_chain_not_consumed");
    return false;
  }

  if (!LooksLikeNativeResourceHandle(sequence_handle)) {
    sequence_handle =
        RefiiBridgedLoadingSequenceHandle().load(std::memory_order_relaxed);
  }
  const bool sequence_table_ready =
      sequence_ready && LoadGuestU32(base, owner_guest + 388) != 0;
  if (!sequence_table_ready &&
      ((!sequence_ready || !LooksLikeNativeResourceHandle(sequence_handle)) &&
       !RefiiLoadingScreenSequenceReady(base, owner_guest, &sequence_handle))) {
    log_skip("sequence_not_ready");
    return false;
  }

  const uint8_t done177_gate_before = LoadGuestU8(base, owner_guest + 177);
  if (done177_gate_before != 0 &&
      RefiiCompletedLoadingScreenOwner().load(std::memory_order_relaxed) ==
          owner_guest) {
    log_skip("already_completed_owner");
    return false;
  }
  const uint32_t previous_forced_owner =
      RefiiForcedLoadingScreenOwnerFinish().exchange(
          owner_guest, std::memory_order_relaxed);
  if (done177_gate_before != 0 && previous_forced_owner == owner_guest) {
    log_skip("already_forced_owner");
    return false;
  }

  const uint32_t frame_before = LoadGuestU32(base, owner_guest + 168);
  const uint8_t pending308_before = LoadGuestU8(base, owner_guest + 308);
  const uint8_t fx309_before = LoadGuestU8(base, owner_guest + 309);
  const uint8_t fade176_before = LoadGuestU8(base, owner_guest + 176);
  const uint8_t done177_before = LoadGuestU8(base, owner_guest + 177);
  const std::string before_state =
      VfsTracingEnabled() ? FormatRefiiLoadingScreenOwnerState(base, owner_guest)
                          : std::string{};

  StoreGuestU8(base, owner_guest + 308, 1);
  StoreGuestU8(base, owner_guest + 309, 1);
  if (frame_before < 10) {
    StoreGuestU32(base, owner_guest + 168, 10);
  }
  StoreGuestU32(base, owner_guest + 172, 0);
  StoreGuestU8(base, owner_guest + 176, 1);

  PPCContext tick_ctx = original_ctx;
  tick_ctx.r3.u32 = owner_guest;
  tick_ctx.lr = 0x8235D5D0u;
  ::__imp__sub_8235E5A0(tick_ctx, base);

  bool completed = LoadGuestU8(base, owner_guest + 177) != 0;
  bool direct_completed = false;
  if (!completed && RefiiCollapsedLoadingSequenceReadyForOwnerRelease()) {
    const uint32_t finish_event = LoadGuestU32(base, owner_guest + 180);
    StoreGuestU8(base, owner_guest + 176, 0);
    StoreGuestU8(base, owner_guest + 177, 1);
    if (finish_event != 0) {
      PPCContext signal_ctx = original_ctx;
      signal_ctx.r3.u32 = finish_event;
      signal_ctx.lr = 0x8235E7D8u;
      ::__imp__sub_822C9388(signal_ctx, base);
    }
    completed = true;
    direct_completed = true;
  }
  if (completed) {
    RefiiCompletedLoadingScreenOwner().store(owner_guest,
                                             std::memory_order_relaxed);
  }

  bool post_finish_native_run = false;
  bool post_finish_generated_state_released = false;
  if (completed) {
    PPCContext post_finish_ctx = original_ctx;
    post_finish_ctx.lr = 0x823892F0u;
    post_finish_native_run =
        TryRunRefiiPostLoadingFrontendRenderRequestNativeFromTrace(
            post_finish_ctx, base, "collapsed_sequence_finish_owner_complete");
    post_finish_generated_state_released =
        TryFinalizeRefiiPostLoadingFrontendGeneratedState(
            post_finish_ctx,
            base,
            "collapsed_sequence_finish_owner_complete",
            true,
            true);
  }

  RecordRefiiMainMenuBlockerEvent(
      completed ? "loading_owner_finish_completed_from_wrapper"
                : "loading_owner_finish_tick_from_wrapper",
      fmt::format("source={} owner=0x{:08X} result=0x{:08X} "
                  "sequence_handle=0x{:08X} frame={}->{} "
                  "pending308=0x{:02X}->0x{:02X} fx309=0x{:02X}->0x{:02X} "
                  "fade176=0x{:02X}->0x{:02X} done177=0x{:02X}->0x{:02X} "
                  "tick_result=0x{:08X} lr=0x{:08X} direct_completed={} "
                  "post_finish_native_run={} "
                  "post_finish_generated_state_released={}",
                  source_api,
                  owner_guest,
                  source_result,
                  sequence_handle,
                  frame_before,
                  LoadGuestU32(base, owner_guest + 168),
                  pending308_before,
                  LoadGuestU8(base, owner_guest + 308),
                  fx309_before,
                  LoadGuestU8(base, owner_guest + 309),
                  fade176_before,
                  LoadGuestU8(base, owner_guest + 176),
                  done177_before,
                  LoadGuestU8(base, owner_guest + 177),
                  tick_ctx.r3.u32,
                  static_cast<uint32_t>(original_ctx.lr),
                  direct_completed ? "true" : "false",
                  post_finish_native_run ? "true" : "false",
                  post_finish_generated_state_released ? "true" : "false"));

  if (VfsTracingEnabled() ||
      ShouldLogRepeatedVfsTraceOperation("LoadingOwnerFinishFromWrapper", 8)) {
    REXLOG_INFO("Refii VFS loading owner finish tick from wrapper "
                "source={} owner=0x{:08X} completed={} direct_completed={} "
                "post_finish_native_run={} "
                "post_finish_generated_state_released={} "
                "result=0x{:08X} "
                "sequence_handle=0x{:08X} before=[{}] after=[{}] "
                "handoff=[{}] main_menu=[{}]",
                source_api,
                owner_guest,
                completed ? "true" : "false",
                direct_completed ? "true" : "false",
                post_finish_native_run ? "true" : "false",
                post_finish_generated_state_released ? "true" : "false",
                tick_ctx.r3.u32,
                sequence_handle,
                before_state,
                FormatRefiiLoadingScreenOwnerState(base, owner_guest),
                FormatRefiiPostVideoHandoffTraceForLog(),
                FormatRefiiMainMenuBlockerTraceForLog());
  }

  LogRefiiVfsChainVerdict("frontend_post_video",
                          static_cast<uint32_t>(original_ctx.lr),
                          "loading_screen_owner",
                          "",
                          "",
                          "native_loading_owner_tick",
                          "collapsed_sequence_finish",
                          completed ? "Opened" : "TraceOnce",
                          completed ? "main_menu_owner_event"
                                    : "loading_owner_tick_pending");
  return completed;
}

bool TryFinishRememberedRefiiLoadingOwnerFromPostFrontendSignal(
    const PPCContext& source_ctx,
    uint8_t* base,
    std::string_view source_api) {
  const uint32_t owner_guest =
      RefiiRememberedLoadingScreenOwner().load(std::memory_order_relaxed);
  if (base == nullptr || !LooksLikeGuestPointer(owner_guest) ||
      !RefiiPostVfsChainReadyForLoadingOwnerRelease()) {
    return false;
  }
  if (LoadGuestU8(base, owner_guest + 177) != 0 &&
      RefiiCompletedLoadingScreenOwner().load(std::memory_order_relaxed) ==
          owner_guest) {
    return true;
  }

  uint32_t sequence_handle = 0;
  bool sequence_ready =
      RefiiLoadingScreenSequenceReady(base, owner_guest, &sequence_handle);
  if (!sequence_ready) {
    PPCContext refresh_ctx = source_ctx;
    sequence_ready = TryRefreshRememberedLoadingScreenSequenceBridge(
        refresh_ctx, base, source_api, &sequence_handle);
  }
  if (!sequence_ready) {
    if (VfsTracingEnabled() &&
        ShouldLogRepeatedVfsTraceOperation(
            "PostFrontendRememberedLoadingOwnerFinishSkipped", 8)) {
      REXLOG_INFO("Refii VFS post-frontend remembered loading owner finish "
                  "skipped source={} reason=sequence_not_ready owner=0x{:08X} "
                  "handoff=[{}] main_menu=[{}]",
                  source_api,
                  owner_guest,
                  FormatRefiiPostVideoHandoffTraceForLog(),
                  FormatRefiiMainMenuBlockerTraceForLog());
    }
    return false;
  }

  PPCContext finish_ctx = source_ctx;
  // Reuse the proven sub_8235E420 loading-owner source contract. The VFS
  // completion signal can arrive after that generated frame has already run.
  finish_ctx.lr = 0x8235D534u;
  const bool completed = TryFinishRefiiLoadingScreenOwnerFromCollapsedPipeline(
      finish_ctx,
      base,
      owner_guest,
      "sub_8235E420",
      owner_guest,
      true,
      sequence_handle);

  RecordRefiiMainMenuBlockerEvent(
      completed ? "post_frontend_loading_owner_finish_completed"
                : "post_frontend_loading_owner_finish_pending",
      fmt::format("source={} owner=0x{:08X} sequence_handle=0x{:08X} "
                  "sequence_ready={} handoff=[{}] main_menu=[{}]",
                  source_api,
                  owner_guest,
                  sequence_handle,
                  sequence_ready ? "true" : "false",
                  FormatRefiiPostVideoHandoffTraceForLog(),
                  FormatRefiiMainMenuBlockerTraceForLog()));
  return completed;
}

bool AssignGuestWideStringHandleResult(PPCContext& source_ctx,
                                       uint8_t* base,
                                       uint32_t out_guest,
                                       std::string_view value,
                                       uint32_t synthetic_lr) {
  if (base == nullptr || !LooksLikeGuestPointer(out_guest) ||
      value.empty() || value.size() > 220) {
    return false;
  }

  PPCContext assign_ctx = source_ctx;
  const uint32_t temp_stack = assign_ctx.r1.u32 - 512;
  const uint32_t temp_string = temp_stack + 256;
  for (size_t index = 0; index < value.size(); ++index) {
    StoreGuestU16(base,
                  temp_string + static_cast<uint32_t>(index * 2),
                  static_cast<uint16_t>(
                      static_cast<unsigned char>(value[index])));
  }
  StoreGuestU16(base,
                temp_string + static_cast<uint32_t>(value.size() * 2),
                0);
  assign_ctx.r1.u32 = temp_stack;
  assign_ctx.r3.u32 = out_guest;
  assign_ctx.r4.u32 = temp_string;
  assign_ctx.lr = synthetic_lr;
  sub_822D6408(assign_ctx, base);
  source_ctx.r3.u32 = out_guest;
  return LoadGuestU32(base, out_guest) != 0;
}

bool TryResolveRefiiBridgedLoadingSequenceHandleLookup(PPCContext& source_ctx,
                                                       uint8_t* base,
                                                       uint32_t out_guest,
                                                       uint32_t table_guest,
                                                       uint32_t handle_guest) {
  if (base == nullptr || table_guest != 0 ||
      !RefiiPostGameFacePackageHandoffSignalEmitted()) {
    return false;
  }
  const uint32_t bridged_handle =
      RefiiBridgedLoadingSequenceHandle().load(std::memory_order_relaxed);
  if (!LooksLikeNativeResourceHandle(handle_guest) ||
      (handle_guest != bridged_handle &&
       !IsRefiiLoadingSequenceHandle(handle_guest))) {
    return false;
  }

  constexpr std::string_view kLoadingAnimationName =
      "art/gui/loadingscreens/loading_animation.tex";
  const bool assigned = AssignGuestWideStringHandleResult(
      source_ctx, base, out_guest, kLoadingAnimationName, 0x8237AA98u);
  if (VfsTracingEnabled() ||
      ShouldLogRepeatedVfsTraceOperation(
          "BridgedLoadingSequenceHandleLookup", 8)) {
    REXLOG_INFO("Refii VFS bridged loading sequence handle lookup "
                "handle=0x{:08X} out=0x{:08X} assigned={} name={} "
                "out_after={} handoff=[{}] main_menu=[{}]",
                handle_guest,
                out_guest,
                assigned ? "true" : "false",
                kLoadingAnimationName,
                FormatGuestWordsIfPointer(base, out_guest, 12),
                FormatRefiiPostVideoHandoffTraceForLog(),
                FormatRefiiMainMenuBlockerTraceForLog());
  }
  LogRefiiVfsChainVerdict("frontend_post_video",
                          static_cast<uint32_t>(source_ctx.lr),
                          std::string(kLoadingAnimationName),
                          "",
                          "",
                          "Refii_bridged_loading_sequence_handle",
                          "sub_8237AA98",
                          assigned ? "Opened" : "OpenFailed",
                          assigned ? "native_loading_resource_name"
                                   : "loading_resource_name_missing");
  return assigned;
}

std::string FormatRefiiLoadingScreenOwnerState(uint8_t* base,
                                               uint32_t owner_guest) {
  if (base == nullptr || !LooksLikeGuestPointer(owner_guest)) {
    return fmt::format("owner=0x{:08X}", owner_guest);
  }

  return fmt::format(
      "owner=0x{:08X} frame={} mode304={} current300={} pending308=0x{:02X} "
      "sequence313=0x{:02X} fx314=0x{:02X} intro320=0x{:02X} "
      "scheduled322=0x{:02X} map356_count={} render340_count={} "
      "table380=0x{:08X}/0x{:08X}/{} anim392={} map356=[{}] "
      "render340=[{}] list184={} list200={} list232={} list248={} "
      "list268={} list280={} raw280={} raw300={} raw340={} raw356={} "
      "raw372={} raw380={} globals=[{}]",
      owner_guest,
      LoadGuestU32(base, owner_guest + 168),
      LoadGuestU32(base, owner_guest + 304),
      LoadGuestU32(base, owner_guest + 300),
      LoadGuestU8(base, owner_guest + 308),
      LoadGuestU8(base, owner_guest + 313),
      LoadGuestU8(base, owner_guest + 314),
      LoadGuestU8(base, owner_guest + 320),
      LoadGuestU8(base, owner_guest + 322),
      CountGuestLinearVector(base, owner_guest, 356, 20),
      CountGuestLinearVector(base, owner_guest, 340, 4),
      LoadGuestU32(base, owner_guest + 380),
      LoadGuestU32(base, owner_guest + 384),
      LoadGuestU32(base, owner_guest + 388),
      LoadGuestU32(base, owner_guest + 392),
      FormatGuestLinearVector(base, owner_guest, 356, 20, 10),
      FormatGuestLinearVector(base, owner_guest, 340, 4, 8),
      FormatGuestWordsAtOffsetIfPointer(base, owner_guest, 184, 8),
      FormatGuestWordsAtOffsetIfPointer(base, owner_guest, 200, 8),
      FormatGuestWordsAtOffsetIfPointer(base, owner_guest, 232, 8),
      FormatGuestWordsAtOffsetIfPointer(base, owner_guest, 248, 8),
      FormatGuestWordsAtOffsetIfPointer(base, owner_guest, 268, 8),
      FormatGuestWordsAtOffsetIfPointer(base, owner_guest, 280, 8),
      FormatGuestWordsAtOffsetIfPointer(base, owner_guest, 280, 16),
      FormatGuestWordsAtOffsetIfPointer(base, owner_guest, 300, 12),
      FormatGuestWordsAtOffsetIfPointer(base, owner_guest, 340, 12),
      FormatGuestWordsAtOffsetIfPointer(base, owner_guest, 356, 8),
      FormatGuestWordsAtOffsetIfPointer(base, owner_guest, 372, 8),
      FormatGuestWordsAtOffsetIfPointer(base, owner_guest, 380, 8),
      FormatRefiiLoadingScreenSequenceGlobals(base));
}

bool RefiiMainMenuTraceIsHashOnly() {
  std::lock_guard<std::mutex> lock(RefiiMainMenuBlockerTraceMutex());
  const auto& trace = RefiiMainMenuBlockerTraceState();
  return !trace.last_texture_hash.empty() &&
         trace.last_texture_selection.empty() &&
         trace.last_wrapper_view.empty() &&
         trace.last_wrapper_slot36.empty() &&
         trace.last_texture_stream_build.empty();
}

bool RefiiMainMenuTraceIsTextureOwnedButNotOpened() {
  std::lock_guard<std::mutex> lock(RefiiMainMenuBlockerTraceMutex());
  const auto& trace = RefiiMainMenuBlockerTraceState();
  return !trace.last_texture_selection.empty() &&
         trace.last_wrapper_view.empty() &&
         trace.last_wrapper_slot36.empty() &&
         trace.last_texture_stream_build.empty();
}

bool RefiiMainMenuTraceHasStarted() {
  std::lock_guard<std::mutex> lock(RefiiMainMenuBlockerTraceMutex());
  const auto& trace = RefiiMainMenuBlockerTraceState();
  return
      !trace.last_texture_hash.empty() ||
      !trace.last_texture_selection.empty() ||
      !trace.last_wrapper_view.empty() ||
      !trace.last_wrapper_slot36.empty() ||
      !trace.last_texture_parser.empty() ||
      !trace.last_texture_stream_build.empty() ||
      !trace.last_loading_frame.empty();
}

bool RefiiMainMenuTraceNeedsTexturePrime() {
  std::lock_guard<std::mutex> lock(RefiiMainMenuBlockerTraceMutex());
  const auto& trace = RefiiMainMenuBlockerTraceState();
  const bool chain_started =
      !trace.last_texture_hash.empty() ||
      !trace.last_texture_selection.empty() ||
      !trace.last_wrapper_view.empty() ||
      !trace.last_texture_stream_build.empty();
  return chain_started && trace.last_wrapper_slot36.empty() &&
         trace.last_texture_parser.empty();
}

bool RefiiPostVideoFrontendChainConsumed() {
  std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
  const auto& trace = RefiiPostVideoHandoffTraceState();
  // In the ownerless post-video route the generated frontend consumer object is
  // bypassed, but the VFS-facing work is still complete once a post-frontend
  // GameFace request has arrived and the full Refii package has been handed
  // back to the native wait chain. Callers that need proof of texture parsing
  // keep checking RefiiMainMenuTraceHasParsedTextureForGeneratedReadyPayload().
  return trace.frontend_consumer_seen ||
         (trace.post_frontend_consumer_request_seen &&
          trace.post_gameface_package_handoff_signal_emitted &&
          (RefiiCompletedPostGameFacePackageMaskLocked(trace) & 0x7u) == 0x7u);
}

bool RefiiPostFrontendConsumerHandoffSignalEmitted() {
  std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
  const auto& trace = RefiiPostVideoHandoffTraceState();
  return trace.post_frontend_consumer_handoff_signal_emitted;
}

bool RefiiMainMenuTraceNeedsFrontendHandoff() {
  if (RefiiMainMenuTraceNeedsTexturePrime()) {
    return true;
  }
  if (!RefiiMainMenuTraceHasStarted()) {
    return false;
  }

  // Once DB20/C340 has parsed the Refii-owned menu texture, the VFS job is
  // still not done until the native frontend consumer has observed that
  // resource chain. A GameFace request may be emitted while the collapsed
  // consumer is still active, so it is useful telemetry but not completion.
  return !RefiiPostVideoFrontendChainConsumed();
}

bool RefiiMainMenuTraceAllowsPostVideoReadyAdvance() {
  std::lock_guard<std::mutex> lock(RefiiMainMenuBlockerTraceMutex());
  const auto& trace = RefiiMainMenuBlockerTraceState();
  const bool chain_started =
      !trace.last_texture_hash.empty() ||
      !trace.last_texture_selection.empty() ||
      !trace.last_wrapper_view.empty() ||
      !trace.last_texture_stream_build.empty();
  if (!chain_started) {
    return false;
  }

  const bool still_needs_frontend_handoff =
      trace.last_wrapper_slot36.empty() && trace.last_texture_parser.empty();
  const bool refii_texture_opened_for_frontend =
      !trace.last_wrapper_view.empty() ||
      !trace.last_texture_stream_build.empty();
  const bool parser_or_commit_seen =
      !trace.last_wrapper_slot36.empty() ||
      !trace.last_texture_parser.empty();
  return still_needs_frontend_handoff ||
         (refii_texture_opened_for_frontend && parser_or_commit_seen);
}

bool RefiiMainMenuTraceHasParsedTextureForGeneratedReadyPayload() {
  std::lock_guard<std::mutex> lock(RefiiMainMenuBlockerTraceMutex());
  const auto& trace = RefiiMainMenuBlockerTraceState();
  return !trace.last_wrapper_slot36.empty() ||
         !trace.last_texture_parser.empty();
}

bool RefiiCollapsedLoadingSequenceReadyForOwnerRelease() {
  return RefiiStartupVideosReadyForMenuHandoff() &&
         RefiiPostGameFacePackageComplete() &&
         RefiiPostVideoFrontendChainConsumed() &&
         !RefiiMainMenuTraceNeedsTexturePrime();
}

bool IsGeneratedPostVideoRenderReadyPollLr(uint32_t lr) {
  return lr == 0x823195D4u || lr == 0x823198D0u;
}


}  // namespace detail
}  // namespace refii::vfs
