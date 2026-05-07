#include "vfs_hooks_private.h"

namespace refii::vfs {
inline namespace detail {

uint32_t FrontendSyntheticStaticRefObject(uint8_t* base,
                                          uint32_t owner_guest,
                                          size_t index) {
  const uint32_t ref_offset =
      owner_guest + 36 + static_cast<uint32_t>(index) * sizeof(uint32_t);
  const uint32_t ref_object = LoadGuestU32(base, ref_offset);
  return LooksLikeGuestPointer(ref_object) ? ref_object : 0;
}

bool TryCommitFrontendTextureStaticTableListEntry(PPCContext& source_ctx,
                                                  uint8_t* base,
                                                  uint32_t owner_guest,
                                                  uint32_t ref_object_guest,
                                                  uint32_t resolver_result,
                                                  uint32_t previous_handle,
                                                  bool first_entry) {
  if (base == nullptr || !LooksLikeGuestPointer(owner_guest) ||
      !LooksLikeGuestPointer(ref_object_guest) || resolver_result == 0 ||
      resolver_result == 0xFFFFFFFFu) {
    return false;
  }

  PPCContext alloc_ctx = source_ctx;
  const uint32_t dest_guest = CallLhMalloc(alloc_ctx, base, 24);
  const uint32_t source_guest = CallLhMalloc(alloc_ctx, base, 24);
  if (!LooksLikeGuestPointer(dest_guest) ||
      !LooksLikeGuestPointer(source_guest)) {
    if (VfsTracingEnabled() || LooseAssetOverrideTracingEnabled()) {
      REXLOG_WARN("Refii VFS frontend texture synthetic static list alloc "
                  "failed owner=0x{:08X} ref_object=0x{:08X} handle=0x{:08X} "
                  "dest=0x{:08X} source=0x{:08X}",
                  owner_guest,
                  ref_object_guest,
                  resolver_result,
                  dest_guest,
                  source_guest);
    }
    return false;
  }

  StoreGuestU32(base, dest_guest + 0, 0);
  StoreGuestU32(base, dest_guest + 4, previous_handle);
  StoreGuestU32(base, dest_guest + 8, 0);
  StoreGuestU32(base, dest_guest + 12, 0);
  StoreGuestU32(base, dest_guest + 16, 0);
  StoreGuestU32(base, dest_guest + 20, 1);

  StoreGuestU32(base, source_guest + 0, ref_object_guest);
  StoreGuestU32(base, source_guest + 4, 0x83360000u);
  StoreGuestU32(base, source_guest + 8, owner_guest + 248);
  StoreGuestU32(base,
                source_guest + 12,
                first_entry ? previous_handle
                            : LoadGuestU32(base, owner_guest + 252));
  StoreGuestU32(base, source_guest + 16, 0);
  StoreGuestU32(base, source_guest + 20, previous_handle);

  StoreGuestU32(base, dest_guest + 0, ref_object_guest);
  StoreGuestU32(base, dest_guest + 4, resolver_result);
  RecordFrontendTextureConsumerStore(base,
                                     "sub_82379D78",
                                     0x823789D8u,
                                     dest_guest,
                                     source_guest,
                                     resolver_result);

  const uint32_t previous_node = LoadGuestU32(base, owner_guest + 252);
  if (!LooksLikeGuestPointer(previous_node)) {
    if (VfsTracingEnabled() || LooseAssetOverrideTracingEnabled()) {
      REXLOG_WARN("Refii VFS frontend texture synthetic static list missing "
                  "previous node owner=0x{:08X} ref_object=0x{:08X} "
                  "handle=0x{:08X} previous=0x{:08X}",
                  owner_guest,
                  ref_object_guest,
                  resolver_result,
                  previous_node);
    }
    return false;
  }

  const uint32_t next_node = LoadGuestU32(base, previous_node + 4);
  const uint32_t list_node_guest = CallLhMalloc(alloc_ctx, base, 16);
  if (!LooksLikeGuestPointer(list_node_guest)) {
    if (VfsTracingEnabled() || LooseAssetOverrideTracingEnabled()) {
      REXLOG_WARN("Refii VFS frontend texture synthetic static list node "
                  "alloc failed owner=0x{:08X} ref_object=0x{:08X} "
                  "handle=0x{:08X}",
                  owner_guest,
                  ref_object_guest,
                  resolver_result);
    }
    return false;
  }

  StoreGuestU32(base, list_node_guest + 0, previous_node);
  StoreGuestU32(base, list_node_guest + 4, next_node);
  StoreGuestU32(base, list_node_guest + 8, ref_object_guest);
  StoreGuestU32(base, list_node_guest + 12, resolver_result);
  AddRefGuestObject(source_ctx, base, ref_object_guest);
  StoreGuestU32(base, owner_guest + 256, LoadGuestU32(base, owner_guest + 256) + 1);
  StoreGuestU32(base, previous_node + 4, list_node_guest);
  if (LooksLikeGuestPointer(next_node)) {
    StoreGuestU32(base, next_node + 0, list_node_guest);
  }
  RecordFrontendTextureConsumerStaticTableListCommit();
  if (VfsTracingEnabled() || LooseAssetOverrideTracingEnabled()) {
    REXLOG_INFO("Refii VFS frontend texture synthetic static list direct "
                "owner=0x{:08X} node=0x{:08X} previous=0x{:08X} "
                "next=0x{:08X} ref_object=0x{:08X} handle=0x{:08X} "
                "first_entry={} list_words=[{}]",
                owner_guest,
                list_node_guest,
                previous_node,
                next_node,
                ref_object_guest,
                resolver_result,
                first_entry ? "true" : "false",
                FormatGuestWordsIfPointer(base, owner_guest + 248, 8));
  }
  return true;
}

uint32_t FrontendSyntheticFinalDirectRefObject(uint8_t* base,
                                               uint32_t owner_guest) {
  constexpr uint32_t kCandidateOffsets[] = {92, 84, 80, 76};
  for (const uint32_t offset : kCandidateOffsets) {
    const uint32_t ref_object = LoadGuestU32(base, owner_guest + offset);
    if (LooksLikeGuestPointer(ref_object)) {
      return ref_object;
    }
  }
  return 0;
}

bool TrySynthesizeFrontendTextureStaticTablePhase(PPCContext& source_ctx,
                                                  uint8_t* base,
                                                  uint32_t owner_guest) {
  if (base == nullptr || !LooksLikeGuestPointer(owner_guest) ||
      LoadGuestU32(base, owner_guest) != kFrontendTextureConsumerObjectVTable) {
    return false;
  }

  constexpr uint32_t kStaticDescriptorResolverLr = 0x8237899C;
  constexpr uint32_t kStaticDescriptorPairStoreLr = 0x823789D8;
  constexpr uint32_t kStaticHashes[] = {
      0xF66F4689u,
      0x36682B4Cu,
      0x8A549FE5u,
      0xFE8DBD8Cu,
      0xFBB9B971u,
      0x78048C1Du,
      0x0FBBC59Cu,
      0xAEB1EF0Fu,
      0xB56281B1u,
      0x27009C86u,
  };

  uint32_t resolved_count = 0;
  uint32_t committed_count = 0;
  uint32_t previous_handle = LoadGuestU32(base, owner_guest + 268 + 4);
  for (size_t index = 0; index < std::size(kStaticHashes); ++index) {
    const uint32_t ref_object =
        FrontendSyntheticStaticRefObject(base, owner_guest, index);
    uint32_t resolved_ref_object = 0;
    uint32_t resolver_result = 0;
    if (TrySynthesizeFrontendTextureDescriptorSlot(
            source_ctx,
            base,
            owner_guest,
            kStaticHashes[index],
            kStaticDescriptorResolverLr,
            kStaticDescriptorResolverLr,
            kStaticDescriptorPairStoreLr,
            0,
            0,
            ref_object,
            &resolved_ref_object,
            &resolver_result,
            nullptr)) {
      ++resolved_count;
      if (TryCommitFrontendTextureStaticTableListEntry(source_ctx,
                                                       base,
                                                       owner_guest,
                                                       resolved_ref_object,
                                                       resolver_result,
                                                       previous_handle,
                                                       index == 0)) {
        ++committed_count;
      }
      previous_handle = resolver_result;
    }
  }

  if (resolved_count != 0) {
    RecordRefiiMainMenuBlockerEvent(
        "frontend_descriptor_synthetic_static_table",
        fmt::format("owner=0x{:08X} resolved={} committed={} owner_slots=[{}]",
                    owner_guest,
                    resolved_count,
                    committed_count,
                    FormatFrontendTextureConsumerOwnerSlots(base,
                                                            owner_guest)));
    if (VfsTracingEnabled() || LooseAssetOverrideTracingEnabled()) {
      REXLOG_INFO("Refii VFS frontend texture synthetic static table "
                  "owner=0x{:08X} resolved={} committed={} owner_slots=[{}]",
                  owner_guest,
                  resolved_count,
                  committed_count,
                  FormatFrontendTextureConsumerOwnerSlots(base, owner_guest));
    }
  }
  return resolved_count != 0;
}

bool TrySynthesizeFrontendTextureFinalDirectPhase(PPCContext& source_ctx,
                                                  uint8_t* base,
                                                  uint32_t owner_guest,
                                                  bool* out_wrapper_committed) {
  if (out_wrapper_committed != nullptr) {
    *out_wrapper_committed = false;
  }
  if (base == nullptr || !LooksLikeGuestPointer(owner_guest) ||
      LoadGuestU32(base, owner_guest) != kFrontendTextureConsumerObjectVTable) {
    return false;
  }

  constexpr uint32_t kFinalDirectHash = 0x73BCB48Fu;
  constexpr uint32_t kFinalDirectResolverLr = 0x82378DA8u;
  constexpr uint32_t kFinalDirectSlotOffset = 280;

  LooseAssetResolution resolution;
  std::string name;
  std::string candidate;
  std::string source;
  if (!ResolveRefiiOwnedTextureDescriptorHash("FrontendTextureConsumerFinalDirect",
                                              kFinalDirectHash,
                                              kFinalDirectResolverLr,
                                              &resolution,
                                              &name,
                                              &candidate,
                                              &source)) {
    if (VfsTracingEnabled() || LooseAssetOverrideTracingEnabled()) {
      REXLOG_WARN("Refii VFS frontend texture synthetic final direct miss "
                  "lr=0x{:08X} hash=0x{:08X} slot=object+{}",
                  kFinalDirectResolverLr,
                  kFinalDirectHash,
                  kFinalDirectSlotOffset);
    }
    return false;
  }

  MaybeUpgradeResourceManagerTextureResolution(kFinalDirectHash,
                                               name.empty() ? candidate : name,
                                               kFinalDirectResolverLr,
                                               &resolution,
                                               &candidate);

  const uint32_t fallback_ref_object_guest =
      FrontendSyntheticFinalDirectRefObject(base, owner_guest);
  uint32_t ref_object_guest =
      TryAllocateRefiiFrontendTextureObject(source_ctx,
                                            base,
                                            kFinalDirectHash,
                                            resolution,
                                            candidate,
                                            "FrontendTextureConsumerFinalDirect");
  const bool ref_object_is_texture = LooksLikeGuestPointer(ref_object_guest);
  if (!ref_object_is_texture) {
    ref_object_guest = fallback_ref_object_guest;
  }
  PPCContext alloc_ctx = source_ctx;
  const uint32_t descriptor_guest = CallLhMalloc(alloc_ctx, base, 32);
  const uint32_t hash_pointer_guest = CallLhMalloc(alloc_ctx, base, 4);
  const uint32_t ref_pointer_guest = CallLhMalloc(alloc_ctx, base, 16);
  if (!LooksLikeGuestPointer(descriptor_guest) ||
      !LooksLikeGuestPointer(hash_pointer_guest) ||
      !LooksLikeGuestPointer(ref_pointer_guest)) {
    if (VfsTracingEnabled() || LooseAssetOverrideTracingEnabled()) {
      REXLOG_WARN("Refii VFS frontend texture synthetic final direct alloc "
                  "failed lr=0x{:08X} hash=0x{:08X} descriptor=0x{:08X} "
                  "hash_ptr=0x{:08X} ref_ptr=0x{:08X}",
                  kFinalDirectResolverLr,
                  kFinalDirectHash,
                  descriptor_guest,
                  hash_pointer_guest,
                  ref_pointer_guest);
    }
    return false;
  }

  StoreGuestU32(base, hash_pointer_guest, kFinalDirectHash);
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
  const uint32_t descriptor_vtable = LoadGuestU32(base, descriptor_guest);
  const uint32_t descriptor_slot12 =
      descriptor_vtable == 0 ? 0 : LoadGuestU32(base, descriptor_vtable + 12);
  const uint32_t descriptor_slot16 =
      descriptor_vtable == 0 ? 0 : LoadGuestU32(base, descriptor_vtable + 16);
  RecordFrontendTextureConsumerDescriptor(descriptor_guest,
                                          kFinalDirectResolverLr,
                                          kFinalDirectHash,
                                          descriptor_vtable,
                                          descriptor_slot12,
                                          descriptor_slot16,
                                          name,
                                          candidate,
                                          source,
                                          true);

  constexpr uint32_t kFrontendResolverConstantTableBase = 0x8349F9B0;
  constexpr uint32_t kFrontendResolverTableIndex = 1;
  const uint32_t table_entry_guest =
      kFrontendResolverConstantTableBase + kFrontendResolverTableIndex * 4;
  const uint32_t table_object_before = LoadGuestU32(base, table_entry_guest);
  const std::string table_before =
      FormatGuestWordsIfPointer(base, table_object_before, 12);

  PPCContext resolver_ctx = source_ctx;
  resolver_ctx.r3.u32 = descriptor_guest;
  resolver_ctx.r4.u32 = 0x3C;
  resolver_ctx.r5.u32 = kFrontendResolverTableIndex;
  resolver_ctx.lr = kFinalDirectResolverLr;
  uint32_t resolver_result = 0;
  std::string failure_reason;
  if (!TryResolveRefiiFrontendTextureDescriptor(resolver_ctx,
                                                base,
                                                descriptor_guest,
                                                kFinalDirectHash,
                                                kFinalDirectResolverLr,
                                                kFrontendResolverTableIndex,
                                                table_object_before,
                                                resolution,
                                                name,
                                                candidate,
                                                source,
                                                &resolver_result,
                                                &failure_reason)) {
    if (VfsTracingEnabled() || LooseAssetOverrideTracingEnabled()) {
      REXLOG_WARN("Refii VFS frontend texture synthetic final direct resolver "
                  "failed lr=0x{:08X} hash=0x{:08X} descriptor=0x{:08X} "
                  "reason={} table_object=0x{:08X}",
                  kFinalDirectResolverLr,
                  kFinalDirectHash,
                  descriptor_guest,
                  failure_reason,
                  table_object_before);
    }
    return false;
  }

  const uint32_t table_object_after = LoadGuestU32(base, table_entry_guest);
  const std::string table_after =
      FormatGuestWordsIfPointer(base,
                                table_object_after == 0 ? table_object_before
                                                        : table_object_after,
                                12);
  RecordFrontendTextureConsumerResolver(
      base,
      kFinalDirectResolverLr,
      descriptor_guest,
      kFinalDirectHash,
      descriptor_vtable,
      descriptor_slot12,
      descriptor_slot16,
      kFrontendResolverTableIndex,
      table_object_after == 0 ? table_object_before : table_object_after,
      table_before,
      table_after,
      resolver_result,
      true);
  if (kFinalDirectHash == kRefiiLoadingAnimationHash) {
    TryRefreshRememberedLoadingScreenSequenceBridge(
        source_ctx, base, "synthetic_final_direct_resolver", nullptr);
  }

  const uint8_t pending_direct_before = LoadGuestU8(base, owner_guest + 276);
  StoreGuestU32(base, owner_guest + kFinalDirectSlotOffset, resolver_result);
  if (resolver_result != 0 && resolver_result != 0xFFFFFFFFu) {
    StoreGuestU8(base, owner_guest + 276, 1);
  }
  const uint8_t pending_direct_after = LoadGuestU8(base, owner_guest + 276);
  RecordFrontendTextureConsumerStore(base,
                                     "sub_82378DA8",
                                     kFinalDirectResolverLr,
                                     owner_guest + kFinalDirectSlotOffset,
                                     descriptor_guest,
                                     resolver_result);
  RecordFrontendTextureConsumerResourceBoundary(
      "sub_82378618_synthetic_final_direct_slot",
      kFinalDirectHash,
      resolver_result);
  const bool wrapper_committed = TryCommitRefiiFrontendTextureWrapper(
      source_ctx,
      base,
      ref_object_guest,
      kFinalDirectHash,
      resolution,
      name,
      candidate,
      source,
      kFinalDirectResolverLr,
      "sub_82378618_synthetic_final_direct");
  if (out_wrapper_committed != nullptr) {
    *out_wrapper_committed = wrapper_committed;
  }

  RecordRefiiMainMenuBlockerEvent(
      "frontend_descriptor_synthetic_final_direct",
      fmt::format("owner=0x{:08X} hash=0x{:08X} name={} candidate={} "
                  "handle=0x{:08X} ref_object=0x{:08X} "
                  "ref_texture={} fallback_ref=0x{:08X} "
                  "pending276=0x{:02X}->0x{:02X} "
                  "wrapper_committed={} owner_slots=[{}]",
                  owner_guest,
                  kFinalDirectHash,
                  NormalizeLooseAssetKey(name),
                  NormalizeLooseAssetKey(candidate),
                  resolver_result,
                  ref_object_guest,
                  ref_object_is_texture ? "true" : "false",
                  fallback_ref_object_guest,
                  pending_direct_before,
                  pending_direct_after,
                  wrapper_committed ? "true" : "false",
                  FormatFrontendTextureConsumerOwnerSlots(base, owner_guest)));
  if (VfsTracingEnabled() || LooseAssetOverrideTracingEnabled() ||
      IsMainMenuTextureHashTraceAsset(name) ||
      IsMainMenuTextureHashTraceAsset(candidate)) {
    REXLOG_INFO("Refii VFS frontend texture synthetic final direct "
                "slot=object+{} lr=0x{:08X} hash=0x{:08X} name={} "
                "candidate={} source={} ref_object=0x{:08X} "
                "ref_texture={} fallback_ref=0x{:08X} handle=0x{:08X} "
                "pending276=0x{:02X}->0x{:02X} "
                "wrapper_committed={} owner_slots=[{}]",
                kFinalDirectSlotOffset,
                kFinalDirectResolverLr,
                kFinalDirectHash,
                NormalizeLooseAssetKey(name),
                NormalizeLooseAssetKey(candidate),
                source,
                ref_object_guest,
                ref_object_is_texture ? "true" : "false",
                fallback_ref_object_guest,
                resolver_result,
                pending_direct_before,
                pending_direct_after,
                wrapper_committed ? "true" : "false",
                FormatFrontendTextureConsumerOwnerSlots(base, owner_guest));
  }
  return true;
}

bool TrySynthesizeFrontendTextureFollowonFrameEntry(
    PPCContext& source_ctx,
    uint8_t* base,
    uint32_t owner_guest,
    uint32_t resource_hash,
    bool refii_owned,
    uint32_t frame_anchor_guest,
    uint32_t frame_owner_guest,
    uint32_t previous_handle,
    uint32_t* out_resolver_result) {
  if (out_resolver_result != nullptr) {
    *out_resolver_result = previous_handle;
  }
  if (base == nullptr || !LooksLikeGuestPointer(owner_guest) ||
      !LooksLikeGuestPointer(frame_anchor_guest) ||
      !LooksLikeGuestPointer(frame_owner_guest) || resource_hash == 0) {
    return false;
  }

  LooseAssetResolution resolution;
  std::string name;
  std::string candidate;
  std::string source = "frontend_constant_table";
  if (refii_owned) {
    if (!ResolveRefiiOwnedTextureDescriptorHash("FrontendFrameFollowonSynthetic",
                                                resource_hash,
                                                0x826D6754u,
                                                &resolution,
                                                &name,
                                                &candidate,
                                                &source)) {
      if (VfsTracingEnabled() || LooseAssetOverrideTracingEnabled()) {
        REXLOG_WARN("Refii VFS frontend frame follow-on miss "
                    "hash=0x{:08X}",
                    resource_hash);
      }
      return false;
    }
    MaybeUpgradeResourceManagerTextureResolution(resource_hash,
                                                 name.empty() ? candidate : name,
                                                 0x826D6754u,
                                                 &resolution,
                                                 &candidate);
  }

  PPCContext alloc_ctx = source_ctx;
  uint32_t ref_object_guest = 0;
  if (refii_owned) {
    ref_object_guest =
        TryAllocateRefiiFrontendTextureObject(source_ctx,
                                              base,
                                              resource_hash,
                                              resolution,
                                              candidate,
                                              "FrontendFrameFollowonSynthetic");
  }
  const bool ref_object_is_texture = LooksLikeGuestPointer(ref_object_guest);
  if (!ref_object_is_texture) {
    ref_object_guest = CallLhMalloc(alloc_ctx, base, 16);
  }
  const uint32_t descriptor_guest = CallLhMalloc(alloc_ctx, base, 32);
  const uint32_t hash_pointer_guest = CallLhMalloc(alloc_ctx, base, 4);
  const uint32_t ref_pointer_guest = CallLhMalloc(alloc_ctx, base, 16);
  const uint32_t dest_guest = CallLhMalloc(alloc_ctx, base, 24);
  const uint32_t source_guest = CallLhMalloc(alloc_ctx, base, 24);
  if (!LooksLikeGuestPointer(descriptor_guest) ||
      !LooksLikeGuestPointer(hash_pointer_guest) ||
      !LooksLikeGuestPointer(ref_pointer_guest) ||
      !LooksLikeGuestPointer(ref_object_guest) ||
      !LooksLikeGuestPointer(dest_guest) ||
      !LooksLikeGuestPointer(source_guest)) {
    if (VfsTracingEnabled() || LooseAssetOverrideTracingEnabled()) {
      REXLOG_WARN("Refii VFS frontend frame follow-on alloc failed "
                  "hash=0x{:08X} descriptor=0x{:08X} hash_ptr=0x{:08X} "
                  "ref_ptr=0x{:08X} ref_object=0x{:08X} dest=0x{:08X} "
                  "source=0x{:08X}",
                  resource_hash,
                  descriptor_guest,
                  hash_pointer_guest,
                  ref_pointer_guest,
                  ref_object_guest,
                  dest_guest,
                  source_guest);
    }
    return false;
  }

  if (!ref_object_is_texture) {
    StoreGuestU32(base, ref_object_guest + 0, 0);
    StoreGuestU32(base, ref_object_guest + 4, 0);
    StoreGuestU32(base, ref_object_guest + 8, 0);
    StoreGuestU32(base, ref_object_guest + 12, 0);
  }

  StoreGuestU32(base, hash_pointer_guest, resource_hash);
  StoreGuestU32(base, ref_pointer_guest + 0, ref_object_guest);
  StoreGuestU32(base, ref_pointer_guest + 4, 0x826E13F0u);
  StoreGuestU32(base, ref_pointer_guest + 8, frame_anchor_guest);
  StoreGuestU32(base, ref_pointer_guest + 12, frame_owner_guest);

  constexpr uint32_t kTextureResourceDescriptorVTable = 0x820F4540;
  StoreGuestU32(base, descriptor_guest + 0, kTextureResourceDescriptorVTable);
  StoreGuestU32(base, descriptor_guest + 4, 0);
  StoreGuestU32(base, descriptor_guest + 8, 1);
  StoreGuestU32(base, descriptor_guest + 12, resource_hash);
  StoreGuestU32(base, descriptor_guest + 16, ref_object_guest);
  StoreGuestU32(base, descriptor_guest + 20, 1024);
  StoreGuestU32(base, descriptor_guest + 24, 1);
  StoreGuestU32(base, descriptor_guest + 28, 0xFFFFFFFFu);

  const uint32_t descriptor_vtable = LoadGuestU32(base, descriptor_guest);
  const uint32_t descriptor_slot12 =
      descriptor_vtable == 0 ? 0 : LoadGuestU32(base, descriptor_vtable + 12);
  const uint32_t descriptor_slot16 =
      descriptor_vtable == 0 ? 0 : LoadGuestU32(base, descriptor_vtable + 16);
  RecordFrontendTextureConsumerDescriptor(descriptor_guest,
                                          0x826D6744u,
                                          resource_hash,
                                          descriptor_vtable,
                                          descriptor_slot12,
                                          descriptor_slot16,
                                          name,
                                          candidate,
                                          source,
                                          refii_owned);

  constexpr uint32_t kFrontendResolverConstantTableBase = 0x8349F9B0;
  constexpr uint32_t kFrontendResolverTableIndex = 1;
  const uint32_t table_entry_guest =
      kFrontendResolverConstantTableBase + kFrontendResolverTableIndex * 4;
  const uint32_t table_object_before = LoadGuestU32(base, table_entry_guest);
  const std::string table_before =
      FormatGuestWordsIfPointer(base, table_object_before, 12);

  PPCContext resolver_ctx = source_ctx;
  resolver_ctx.r3.u32 = descriptor_guest;
  resolver_ctx.r4.u32 = 0x3C;
  resolver_ctx.r5.u32 = kFrontendResolverTableIndex;
  resolver_ctx.lr = 0x826D6754u;
  uint32_t resolver_result = 0;
  std::string failure_reason;
  if (!TryResolveRefiiFrontendTextureDescriptor(resolver_ctx,
                                                base,
                                                descriptor_guest,
                                                resource_hash,
                                                0x826D6754u,
                                                kFrontendResolverTableIndex,
                                                table_object_before,
                                                resolution,
                                                name,
                                                candidate,
                                                source,
                                                &resolver_result,
                                                &failure_reason)) {
    if (VfsTracingEnabled() || LooseAssetOverrideTracingEnabled()) {
      REXLOG_WARN("Refii VFS frontend frame follow-on resolver failed "
                  "hash=0x{:08X} descriptor=0x{:08X} reason={} "
                  "table_object=0x{:08X}",
                  resource_hash,
                  descriptor_guest,
                  failure_reason,
                  table_object_before);
    }
    return false;
  }

  const uint32_t table_object_after = LoadGuestU32(base, table_entry_guest);
  const std::string table_after =
      FormatGuestWordsIfPointer(base,
                                table_object_after == 0 ? table_object_before
                                                        : table_object_after,
                                12);
	  RecordFrontendTextureConsumerResolver(
	      base,
	      0x826D6754u,
	      descriptor_guest,
      resource_hash,
      descriptor_vtable,
      descriptor_slot12,
      descriptor_slot16,
      kFrontendResolverTableIndex,
      table_object_after == 0 ? table_object_before : table_object_after,
      table_before,
      table_after,
	      resolver_result,
	      refii_owned);
	  if (refii_owned && resource_hash == kRefiiLoadingAnimationHash) {
	    RecordRefiiLoadingSequenceResolverHandle(resource_hash,
	                                             resolver_result,
	                                             0x826D6754u,
	                                             kFrontendResolverTableIndex);
	    PPCContext publish_ctx = source_ctx;
	    publish_ctx.lr = 0x826D6754u;
	    const bool primary_published =
	        TryPublishRefiiPostGameFacePrimaryOwnerFromTrace(
	            publish_ctx, base, "frontend_followon_loading_sequence_resolved");
	    const bool primary_ready =
	        primary_published || RefiiPostGameFacePrimaryOwnerReady(base);
	    const bool primary_native_ready =
	        primary_ready &&
	        TryRunRefiiPostGameFacePrimaryOwnerNativeFromTrace(
	            source_ctx,
	            base,
	            "post_gameface_package_handoff_after_publish");
	    PPCContext frontend_ctx = source_ctx;
	    const bool frontend_run =
	        primary_ready &&
	        TryRunRefiiPostGameFaceFrontendRenderRequestFromTrace(
	            frontend_ctx,
	            base,
	            "post_gameface_package_handoff_signal");
	    const bool post_loading_run =
	        frontend_run &&
	        TryRunRefiiPostLoadingFrontendRenderRequestNativeFromTrace(
	            source_ctx, base, "post_gameface_package_handoff_signal");
	    const bool loading_owner_finished =
	        TryFinishRememberedRefiiLoadingOwnerFromPostFrontendSignal(
	            source_ctx, base, "post_gameface_package_handoff_signal");
	    RecordRefiiMainMenuBlockerEvent(
	        "frontend_followon_loading_sequence_resolved",
	        fmt::format("handle=0x{:08X} primary_published={} "
	                    "primary_ready={} primary_native_ready={} "
	                    "frontend_run={} post_loading_run={} "
	                    "loading_owner_finished={} globals=[{}]",
	                    resolver_result,
	                    primary_published ? "true" : "false",
	                    primary_ready ? "true" : "false",
	                    primary_native_ready ? "true" : "false",
	                    frontend_run ? "true" : "false",
	                    post_loading_run ? "true" : "false",
	                    loading_owner_finished ? "true" : "false",
	                    FormatRefiiLoadingScreenSequenceGlobals(base)));
	  }

  StoreGuestU32(base, dest_guest + 0, 0);
  StoreGuestU32(base, dest_guest + 4, previous_handle);
  StoreGuestU32(base, dest_guest + 8, 0);
  StoreGuestU32(base, dest_guest + 12, frame_owner_guest);
  StoreGuestU32(base, dest_guest + 16, 5);
  StoreGuestU32(base, dest_guest + 20, table_object_after == 0
                                      ? table_object_before
                                      : table_object_after);

  StoreGuestU32(base, source_guest + 0, ref_object_guest);
  StoreGuestU32(base, source_guest + 4, 0x826E13F0u);
  StoreGuestU32(base, source_guest + 8, frame_anchor_guest);
  StoreGuestU32(base, source_guest + 12, frame_owner_guest);
  StoreGuestU32(base, source_guest + 16, frame_anchor_guest);
  StoreGuestU32(base, source_guest + 20, frame_owner_guest);

  StoreGuestU32(base, dest_guest + 0, ref_object_guest);
  StoreGuestU32(base, dest_guest + 4, resolver_result);
  if (LooksLikeGuestPointer(ref_object_guest)) {
    AddRefGuestObject(source_ctx, base, ref_object_guest);
  }
  RecordFrontendTextureConsumerStore(base,
                                     "sub_82379D78_direct_followon",
                                     0x826D6790u,
                                     dest_guest,
                                     source_guest,
                                     resolver_result);
  RecordFrontendTextureConsumerFollowonFrameListCommit();

  bool wrapper_committed = false;
  if (refii_owned && ref_object_is_texture) {
    wrapper_committed =
        TryCommitRefiiFrontendTextureWrapper(source_ctx,
                                             base,
                                             ref_object_guest,
                                             resource_hash,
                                             resolution,
                                             name,
                                             candidate,
                                             source,
                                             0x826D6754u,
                                             "FrontendFrameFollowonSynthetic");
  }
  if ((VfsTracingEnabled() || LooseAssetOverrideTracingEnabled()) &&
      refii_owned) {
    REXLOG_INFO("Refii VFS frontend frame follow-on texture handoff "
                "hash=0x{:08X} ref_object=0x{:08X} ref_texture={} "
                "handle=0x{:08X} wrapper_committed={} candidate={} "
                "provider={}",
                resource_hash,
                ref_object_guest,
                ref_object_is_texture ? "true" : "false",
                resolver_result,
                wrapper_committed ? "true" : "false",
                NormalizeLooseAssetKey(candidate),
                NormalizeLooseAssetKey(resolution.provider_relative_path));
  }

  if (out_resolver_result != nullptr) {
    *out_resolver_result = resolver_result;
  }
  return true;
}

bool TrySynthesizeFrontendTextureFollowonFramePhase(PPCContext& source_ctx,
                                                    uint8_t* base,
                                                    uint32_t owner_guest) {
  if (base == nullptr || !LooksLikeGuestPointer(owner_guest) ||
      LoadGuestU32(base, owner_guest) != kFrontendTextureConsumerObjectVTable) {
    return false;
  }

  constexpr uint32_t kFrameDetailHash = 0xC5A8E73Cu;
  constexpr uint32_t kFrameConstantHashes[] = {
      0x099E8683u,
      0xBE651C14u,
      0xDC3604EDu,
      0x8D74118Eu,
      0x5882A67Fu,
      0xE4B1DD00u,
      0x1D960609u,
      0xE1243C8Au,
      0x2FB5325Bu,
      0xEBBDA5CCu,
      0x8EF49D45u,
      0x3FE165C6u,
      0x45DACAF7u,
      0x6F7F6A18u,
  };

  PPCContext alloc_ctx = source_ctx;
  const uint32_t frame_anchor_guest = CallLhMalloc(alloc_ctx, base, 16);
  const uint32_t frame_owner_guest = CallLhMalloc(alloc_ctx, base, 16);
  if (!LooksLikeGuestPointer(frame_anchor_guest) ||
      !LooksLikeGuestPointer(frame_owner_guest)) {
    return false;
  }
  StoreGuestU32(base, frame_anchor_guest + 0, 0);
  StoreGuestU32(base, frame_anchor_guest + 4, 0);
  StoreGuestU32(base, frame_anchor_guest + 8, 0);
  StoreGuestU32(base, frame_anchor_guest + 12, 0);
  StoreGuestU32(base, frame_owner_guest + 0, 0);
  StoreGuestU32(base, frame_owner_guest + 4, 0);
  StoreGuestU32(base, frame_owner_guest + 8, 0);
  StoreGuestU32(base, frame_owner_guest + 12, 0);

  uint32_t previous_handle = LoadGuestU32(base, owner_guest + 280);
  uint32_t committed_count = 0;
  for (const uint32_t constant_hash : kFrameConstantHashes) {
    if (TrySynthesizeFrontendTextureFollowonFrameEntry(source_ctx,
                                                       base,
                                                       owner_guest,
                                                       constant_hash,
                                                       false,
                                                       frame_anchor_guest,
                                                       frame_owner_guest,
                                                       previous_handle,
                                                       &previous_handle)) {
      ++committed_count;
    }
  }
  if (TrySynthesizeFrontendTextureFollowonFrameEntry(source_ctx,
                                                     base,
                                                     owner_guest,
                                                     kFrameDetailHash,
                                                     true,
                                                     frame_anchor_guest,
                                                     frame_owner_guest,
                                                     previous_handle,
                                                     &previous_handle)) {
    ++committed_count;
  }

  if (committed_count != 0) {
    RecordRefiiMainMenuBlockerEvent(
        "frontend_descriptor_synthetic_followon_frame",
        fmt::format("owner=0x{:08X} committed={} last_handle=0x{:08X} "
                    "anchor=0x{:08X} frame_owner=0x{:08X} owner_slots=[{}]",
                    owner_guest,
                    committed_count,
                    previous_handle,
                    frame_anchor_guest,
                    frame_owner_guest,
                    FormatFrontendTextureConsumerOwnerSlots(base,
                                                            owner_guest)));
    if (VfsTracingEnabled() || LooseAssetOverrideTracingEnabled()) {
      REXLOG_INFO("Refii VFS frontend texture synthetic follow-on frame "
                  "owner=0x{:08X} committed={} last_handle=0x{:08X} "
                  "anchor=0x{:08X} frame_owner=0x{:08X} owner_slots=[{}]",
                  owner_guest,
                  committed_count,
                  previous_handle,
                  frame_anchor_guest,
                  frame_owner_guest,
                  FormatFrontendTextureConsumerOwnerSlots(base, owner_guest));
    }
  }
  return committed_count != 0;
}


}  // namespace detail
}  // namespace refii::vfs
