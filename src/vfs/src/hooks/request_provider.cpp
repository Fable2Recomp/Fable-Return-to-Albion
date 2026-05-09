#include "vfs_hooks_private.h"

namespace {

std::string ResolveProviderEntryNameForHashOpen(
    const PPCContext& original_ctx,
    uint8_t* base,
    uint32_t provider_guest,
    uint32_t provider_entry_key) {
  if (base == nullptr || provider_guest == 0 || provider_entry_key == 0) {
    return {};
  }

  const uint32_t provider_entry_table =
      refii::vfs::LoadGuestU32(base, provider_guest + 44);
  if (provider_entry_table == 0) {
    return {};
  }

  PPCContext resolve_ctx = original_ctx;
  resolve_ctx.r3.u32 = provider_entry_table + 8;
  resolve_ctx.r4.u32 = provider_entry_key;
  sub_82313400(resolve_ctx, base);

  if (resolve_ctx.r3.u32 == 0) {
    return {};
  }
  return refii::vfs::ReadGuestWideStringObject(base, resolve_ctx.r3.u32, 4096);
}

uint32_t RequestManagerProviderCount(uint8_t* base, uint32_t manager_guest) {
  if (base == nullptr || manager_guest == 0) {
    return 0;
  }
  const uint32_t begin = refii::vfs::LoadGuestU32(base, manager_guest + 40);
  const uint32_t end = refii::vfs::LoadGuestU32(base, manager_guest + 44);
  if (begin == 0 || end < begin || ((end - begin) % 36) != 0) {
    return 0;
  }
  return (end - begin) / 36;
}

bool IsKnownRequestManagerGuest(uint32_t manager_guest) {
  const std::vector<uint32_t> managers =
      refii::vfs::SnapshotKnownRequestManagerGuests();
  return std::find(managers.begin(), managers.end(), manager_guest) !=
         managers.end();
}

}  // namespace

REX_EXTERN(rex_lhVFS_RequestManager_FindStream) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhVFS_RequestManager_FindStream);

  const PPCContext original_ctx = ctx;
  const uint32_t out_guest = ctx.r3.u32;
  const uint32_t manager_guest = ctx.r4.u32;
  const uint32_t name_guest = ctx.r5.u32;
  const uint32_t mode = ctx.r6.u32;

  if (refii::vfs::RefiiLooseAssetBackendEnabled()) {
    const std::string asset_path =
        refii::vfs::ReadGuestWideStringObject(base, name_guest, 4096);
    if (!asset_path.empty()) {
      const bool trace_lookup =
          refii::vfs::VfsTracingEnabled() &&
          (refii::vfs::IsTraceInterestingAsset(asset_path) ||
           refii::vfs::ShouldLogVfsTraceOperation(
               "RequestManager_FindStream"));
      if (trace_lookup) {
        REXLOG_INFO("VFS trace RequestManager_FindStream lookup lr=0x{:08X} "
                    "out=0x{:08X} manager=0x{:08X} name_obj=0x{:08X} "
                    "name={} mode=0x{:08X}",
                    original_ctx.lr,
                    out_guest,
                    manager_guest,
                    name_guest,
                    asset_path,
                    mode);
      }
      refii::vfs::LogLooseAssetCallSite("VFS FindStream lookup",
                                            original_ctx,
                                            out_guest,
                                            manager_guest,
                                            name_guest,
                                            mode,
                                            asset_path);
      refii::vfs::RefiiVfsOpenResult open_result;
      refii::vfs::TryOpenRefiiVfsAsset(
          ctx,
          base,
          original_ctx,
          refii::vfs::RefiiVfsOpenShape::RawStream,
          refii::vfs::RefiiVfsOpenRequest{
              .api = "RequestManager_FindStream",
              .request_name = asset_path,
              .out_guest = out_guest,
              .manager_guest = manager_guest,
              .mode = mode,
              .caller_lr = static_cast<uint32_t>(original_ctx.lr),
              .prefer_resource_manager_texture_shape =
                  refii::vfs::IsTextureAssetCandidate(asset_path),
              .allow_optional_null_miss = true,
          },
          &open_result);
      if (open_result.disposition ==
          refii::vfs::RefiiVfsOpenDisposition::Opened) {
        if (refii::vfs::IsTextureAssetCandidate(asset_path)) {
          refii::vfs::LogRefiiOwnedTextureStreamBuilt(
              base,
              "RequestManager_FindStream",
              static_cast<uint32_t>(original_ctx.lr),
              refii::vfs::ComputeResourceNameHash(asset_path),
              asset_path,
              open_result.resolution,
              open_result.candidate,
              open_result.source,
              out_guest,
              open_result.opened_guest,
              false);
        }
        const std::string normalized_asset_path =
            refii::vfs::NormalizeLooseAssetKey(asset_path);
        if (refii::vfs::IsGameFaceBinaryAssetKey(normalized_asset_path) &&
            refii::vfs::EndsWith(normalized_asset_path, ".fac")) {
          const uint32_t opened_stream_guest =
              out_guest != 0
                  ? refii::vfs::TryReadOutputPointer(base, out_guest)
                  : open_result.opened_guest;
          refii::vfs::MaybeSignalRefiiPostGameFacePackageHandoff(
              original_ctx, base, normalized_asset_path, opened_stream_guest);
        }
        return;
      }
      if (refii::vfs::IsHandledRefiiVfsOpenDisposition(
              open_result.disposition)) {
        return;
      }
      if (trace_lookup || refii::vfs::VfsTracingEnabled()) {
        REXLOG_INFO("Refii VFS RequestManager helper fallback "
                    "lr=0x{:08X} asset={} disposition={} route=native",
                    original_ctx.lr,
                    asset_path,
                    refii::vfs::ToString(open_result.disposition));
      }
      ctx = original_ctx;
      __imp__rex_lhVFS_RequestManager_FindStream(ctx, base);
      if (refii::vfs::VfsTracingEnabled() &&
          (refii::vfs::IsTraceInterestingAsset(asset_path) ||
           refii::vfs::ShouldLogVfsTraceOperation(
               "RequestManager_FindStream"))) {
        const uint32_t stream_guest =
            out_guest != 0 ? refii::vfs::TryReadOutputPointer(base, out_guest)
                           : 0;
        REXLOG_INFO("VFS trace RequestManager_FindStream native exit lr=0x{:08X} "
                    "name={} result=0x{:08X} out=0x{:08X} stream=0x{:08X}",
                    original_ctx.lr,
                    asset_path,
                    ctx.r3.u32,
                    out_guest,
                    stream_guest);
      }
      return;
    } else if (refii::vfs::LooseAssetOverrideTracingEnabled()) {
      REXLOG_INFO("Loose asset lookup skipped for unreadable resource name "
                  "object=0x{:08X}",
                  name_guest);
    }
  }

  ctx = original_ctx;
  __imp__rex_lhVFS_RequestManager_FindStream(ctx, base);

  if (refii::vfs::VfsTracingEnabled()) {
    const std::string asset_path =
        refii::vfs::ReadGuestWideStringObject(base, name_guest, 4096);
    if (!asset_path.empty() &&
        (refii::vfs::IsTraceInterestingAsset(asset_path) ||
         refii::vfs::ShouldLogVfsTraceOperation(
             "RequestManager_FindStream"))) {
      const uint32_t stream_guest =
          out_guest != 0 ? refii::vfs::TryReadOutputPointer(base, out_guest)
                         : 0;
      REXLOG_INFO("VFS trace RequestManager_FindStream native exit lr=0x{:08X} "
                  "name={} result=0x{:08X} out=0x{:08X} stream=0x{:08X}",
                  original_ctx.lr,
                  asset_path,
                  ctx.r3.u32,
                  out_guest,
                  stream_guest);
    }
  }
}

REX_EXTERN(rex_lhVFS_RequestManager_BuildHashTable) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhVFS_RequestManager_BuildHashTable);

  const PPCContext original_ctx = ctx;
  const uint32_t manager_guest = original_ctx.r3.u32;

  __imp__rex_lhVFS_RequestManager_BuildHashTable(ctx, base);

  if (ctx.r3.s32 >= 0 && refii::vfs::RefiiLooseAssetBackendEnabled()) {
    refii::vfs::RecordKnownRequestManagerGuest(
        manager_guest, "RequestManager_BuildHashTable");
    PPCContext augment_ctx = original_ctx;
    refii::vfs::EnsureRefiiBootBankTemplates(
        augment_ctx, base, "RequestManager_BuildHashTable");
    refii::vfs::AugmentRequestManagerHashTable(
        augment_ctx, base, manager_guest, "RequestManager_BuildHashTable");
  }

  if (refii::vfs::VfsTracingEnabled() &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "RequestManager_BuildHashTable", 16)) {
    const uint32_t container_guest = manager_guest + 52;
    const uint32_t begin =
        refii::vfs::LoadGuestU32(base, container_guest + 4);
    const uint32_t end =
        refii::vfs::LoadGuestU32(base, container_guest + 8);
    const uint32_t count =
        begin != 0 && end >= begin ? static_cast<uint32_t>((end - begin) / 12)
                                   : 0;
    REXLOG_INFO("VFS trace RequestManager_BuildHashTable exit lr=0x{:08X} "
                "manager=0x{:08X} result=0x{:08X} entries={} begin=0x{:08X} "
                "end=0x{:08X}",
                original_ctx.lr,
                manager_guest,
                ctx.r3.u32,
                count,
                begin,
                end);
  }
}

REX_EXTERN(sub_82B45F70) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82B45F70);

  const PPCContext original_ctx = ctx;
  const uint32_t manager_guest = original_ctx.r3.u32;
  const uint32_t provider_guest = original_ctx.r4.u32;
  const uint32_t provider_index = original_ctx.r5.u32;

  __imp__sub_82B45F70(ctx, base);

  const uint32_t provider_count =
      RequestManagerProviderCount(base, manager_guest);
  const bool provider_is_known_request_manager =
      IsKnownRequestManagerGuest(provider_guest);
  const bool provider_build_chain_complete =
      provider_count == 0 || provider_index + 1 >= provider_count;
  const bool should_augment_provider_hash =
      provider_build_chain_complete &&
      (provider_count != 1 || provider_is_known_request_manager);
  if (ctx.r3.s32 >= 0 && refii::vfs::RefiiLooseAssetBackendEnabled()) {
    refii::vfs::RecordKnownRequestManagerGuest(
        manager_guest, "RequestManager_BuildProviderHash");
    if (should_augment_provider_hash) {
      PPCContext augment_ctx = original_ctx;
      refii::vfs::EnsureRefiiBootBankTemplates(
          augment_ctx, base, "RequestManager_BuildProviderHash");
      refii::vfs::AugmentRequestManagerHashTable(
          augment_ctx, base, manager_guest, "RequestManager_BuildProviderHash");
    }
  }

  if (refii::vfs::VfsTracingEnabled() &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "RequestManager_BuildProviderHash", 32)) {
    const uint32_t begin =
        refii::vfs::LoadGuestU32(base, manager_guest + 56);
    const uint32_t end =
        refii::vfs::LoadGuestU32(base, manager_guest + 60);
    const uint32_t count =
        begin != 0 && end >= begin ? static_cast<uint32_t>((end - begin) / 12)
                                   : 0;
    REXLOG_INFO("VFS trace RequestManager_BuildProviderHash exit lr=0x{:08X} "
                "manager=0x{:08X} provider=0x{:08X} provider_index={} "
                "provider_count={} augmentation={} result=0x{:08X} "
                "hash_entries={}",
                original_ctx.lr,
                manager_guest,
                provider_guest,
                provider_index,
                provider_count,
                should_augment_provider_hash ? "complete" : "deferred",
                ctx.r3.u32,
                count);
  }
}

REX_EXTERN(sub_82B456D8) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82B456D8);

  const PPCContext original_ctx = ctx;
  const uint32_t out_guest = original_ctx.r3.u32;
  const uint32_t manager_guest = original_ctx.r4.u32;
  const uint32_t hash_entry_guest = original_ctx.r5.u32;
  const uint32_t mode = original_ctx.r6.u32;
  const uint32_t resource_hash =
      hash_entry_guest == 0
          ? 0
          : refii::vfs::LoadGuestU32(base, hash_entry_guest);
  const uint32_t provider_index =
      hash_entry_guest == 0
          ? 0
          : refii::vfs::LoadGuestU32(base, hash_entry_guest + 4);
  const uint32_t provider_entry_key =
      hash_entry_guest == 0
          ? 0
          : refii::vfs::LoadGuestU32(base, hash_entry_guest + 8);
  refii::vfs::RecordFrontendTextureConsumerResourceBoundary(
      "RequestManager_FindStreamByHashEntry", resource_hash, 0);
  const std::vector<std::string> runtime_names =
      refii::vfs::GetRuntimeResourceHashNames(resource_hash);
  static std::atomic<uint64_t> hash_entry_trace_sequence{0};
  const uint64_t hash_entry_trace_index =
      hash_entry_trace_sequence.fetch_add(1, std::memory_order_relaxed) + 1;
  const bool trace =
      refii::vfs::VfsTracingEnabled() &&
      (hash_entry_trace_index <= 8 ||
       refii::vfs::ShouldLogVfsTraceOperation(
           "RequestManager_FindStreamByHashEntry") ||
       std::any_of(runtime_names.begin(),
                   runtime_names.end(),
                   [](const std::string& name) {
                     return refii::vfs::IsTraceInterestingAsset(name);
                   }));
  const bool pass_through_hash =
      refii::vfs::ShouldPassThroughNativeResourceHash(resource_hash,
                                                         runtime_names);
  if (pass_through_hash) {
    refii::vfs::TraceLuaVfsBridgeResolution(
        "request_manager_hash_entry", resource_hash, runtime_names);
  }

  if (refii::vfs::RefiiLooseAssetBackendEnabled() && resource_hash != 0 &&
      refii::vfs::g_suppress_request_manager_hash_entry_loose_streams == 0 &&
      !pass_through_hash) {
    refii::vfs::RefiiVfsOpenResult open_result;
    refii::vfs::TryOpenRefiiVfsAsset(
        ctx,
        base,
        original_ctx,
        refii::vfs::RefiiVfsOpenShape::RawStream,
        refii::vfs::RefiiVfsOpenRequest{
            .api = "RequestManager_FindStreamByHashEntry",
            .resource_hash = resource_hash,
            .out_guest = out_guest,
            .manager_guest = manager_guest,
            .mode = mode,
            .caller_lr = static_cast<uint32_t>(original_ctx.lr),
            .prefer_resource_manager_texture_shape =
                refii::vfs::HasRuntimeTextureName(resource_hash),
        },
        &open_result);
    if (open_result.disposition ==
        refii::vfs::RefiiVfsOpenDisposition::Opened) {
      refii::vfs::LogRefiiOwnedTextureStreamBuilt(
          base,
          "RequestManager_FindStreamByHashEntry",
          static_cast<uint32_t>(original_ctx.lr),
          resource_hash,
          std::string_view{},
          open_result.resolution,
          open_result.candidate,
          open_result.source,
          out_guest,
          open_result.opened_guest,
          false);
      if (trace || refii::vfs::LooseAssetOverrideTracingEnabled() ||
          refii::vfs::ShouldLogLooseStatsMilestone(
              open_result.hit_count)) {
        REXLOG_INFO("Refii VFS RequestManager hash-entry hit hit={} "
                    "hash=0x{:08X} candidate={} provider_relative={} "
                    "host={} source={} stream=0x{:08X} manager=0x{:08X} "
                    "entry=0x{:08X} provider_index={} provider_entry_key=0x{:08X} "
                    "mode=0x{:08X} names={}",
                    open_result.hit_count,
                    resource_hash,
                    open_result.candidate,
                    open_result.resolution.provider_relative_path,
                    open_result.resolution.host_path.string(),
                    open_result.source,
                    open_result.opened_guest,
                    manager_guest,
                    hash_entry_guest,
                    provider_index,
                    provider_entry_key,
                    mode,
                    refii::vfs::JoinStrings(runtime_names, ", "));
      }
      return;
    }
    if (open_result.disposition ==
        refii::vfs::RefiiVfsOpenDisposition::OpenFailed) {
      if (trace || refii::vfs::LooseAssetOverrideTracingEnabled()) {
        REXLOG_WARN("Refii VFS RequestManager hash-entry matched "
                    "hash=0x{:08X} candidate={}, but host stream failed {}",
                    resource_hash,
                    open_result.candidate,
                    open_result.resolution.host_path.string());
      }
      if (provider_index == refii::vfs::kRefiiRequestManagerProviderIndex &&
          provider_entry_key == refii::vfs::kRefiiRequestManagerEntryKey) {
        if (out_guest != 0) {
          refii::vfs::StoreGuestU32(base, out_guest, 0);
        }
        ctx.r3.u32 = out_guest;
        return;
      }
    } else if (trace) {
      REXLOG_INFO("Refii VFS RequestManager hash-entry miss hash=0x{:08X} "
                  "entry=0x{:08X} provider_index={} "
                  "provider_entry_key=0x{:08X} mode=0x{:08X} sample={} "
                  "names={} disposition={}",
                  resource_hash,
                  hash_entry_guest,
                  provider_index,
                  provider_entry_key,
                  mode,
                  hash_entry_trace_index,
                  refii::vfs::JoinStrings(runtime_names, ", "),
                  refii::vfs::ToString(open_result.disposition));
    }
  }

  if (provider_index == refii::vfs::kRefiiRequestManagerProviderIndex &&
      provider_entry_key == refii::vfs::kRefiiRequestManagerEntryKey) {
    if (out_guest != 0) {
      refii::vfs::StoreGuestU32(base, out_guest, 0);
    }
    ctx.r3.u32 = out_guest;
    if (trace || refii::vfs::VfsTracingEnabled()) {
      REXLOG_WARN("Refii VFS RequestManager synthetic hash entry failed "
                  "cleanly hash=0x{:08X} out=0x{:08X} names={}",
                  resource_hash,
                  out_guest,
                  refii::vfs::JoinStrings(runtime_names, ", "));
    }
    return;
  }

  ctx = original_ctx;
  __imp__sub_82B456D8(ctx, base);

  if (trace) {
    const uint32_t stream_guest =
        out_guest != 0 ? refii::vfs::TryReadOutputPointer(base, out_guest)
                       : 0;
    REXLOG_INFO("VFS trace RequestManager hash-entry native exit lr=0x{:08X} "
                "hash=0x{:08X} out=0x{:08X} stream=0x{:08X} result=0x{:08X} "
                "entry=0x{:08X} provider_index={} provider_entry_key=0x{:08X} "
                "names={}",
                original_ctx.lr,
                resource_hash,
                out_guest,
                stream_guest,
                ctx.r3.u32,
                hash_entry_guest,
                provider_index,
                provider_entry_key,
                refii::vfs::JoinStrings(runtime_names, ", "));
  }
}

REX_EXTERN(rex_lhVFS_Provider_FindStreamByHash) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhVFS_Provider_FindStreamByHash);

  const PPCContext original_ctx = ctx;
  const uint32_t out_guest = ctx.r3.u32;
  const uint32_t provider_guest = ctx.r4.u32;
  const uint32_t provider_entry_key = ctx.r5.u32;
  const uint32_t mode = ctx.r6.u32;

  uint32_t resource_hash = 0;
  if (original_ctx.lr == 0x82B45644 && original_ctx.r1.u32 != 0) {
    resource_hash = refii::vfs::LoadGuestU32(base, original_ctx.r1.u32 + 128);
  }
  const std::string provider_entry_name =
      ResolveProviderEntryNameForHashOpen(
          original_ctx, base, provider_guest, provider_entry_key);
  if (resource_hash == 0 && !provider_entry_name.empty()) {
    resource_hash = refii::vfs::ComputeResourceNameHash(provider_entry_name);
  }

  const std::vector<std::string> runtime_names =
      refii::vfs::GetRuntimeResourceHashNames(resource_hash);
  static std::atomic<uint64_t> provider_hash_trace_sequence{0};
  const uint64_t provider_hash_trace_index =
      provider_hash_trace_sequence.fetch_add(1, std::memory_order_relaxed) + 1;
  const bool trace =
      refii::vfs::VfsTracingEnabled() &&
      (provider_hash_trace_index <= 64 ||
       refii::vfs::ShouldLogVfsTraceOperation(
           "Provider_FindStreamByHash") ||
       std::any_of(runtime_names.begin(),
                   runtime_names.end(),
                   [](const std::string& name) {
                     return refii::vfs::IsTraceInterestingAsset(name);
                   }));

  if (refii::vfs::RefiiLooseAssetBackendEnabled() && resource_hash != 0) {
    refii::vfs::RefiiVfsOpenResult open_result;
    refii::vfs::TryOpenRefiiVfsAsset(
        ctx,
        base,
        original_ctx,
        refii::vfs::RefiiVfsOpenShape::RawStream,
        refii::vfs::RefiiVfsOpenRequest{
            .api = "Provider_FindStreamByHash",
            .request_name = provider_entry_name,
            .resource_hash = resource_hash,
            .out_guest = out_guest,
            .manager_guest = provider_guest,
            .mode = mode,
            .caller_lr = static_cast<uint32_t>(original_ctx.lr),
            .prefer_resource_manager_texture_shape =
                refii::vfs::HasRuntimeTextureName(resource_hash) ||
                refii::vfs::IsTextureAssetCandidate(provider_entry_name),
        },
        &open_result);
    if (open_result.disposition ==
        refii::vfs::RefiiVfsOpenDisposition::Opened) {
      refii::vfs::LogRefiiOwnedTextureStreamBuilt(
          base,
          "Provider_FindStreamByHash",
          static_cast<uint32_t>(original_ctx.lr),
          resource_hash,
          provider_entry_name,
          open_result.resolution,
          open_result.candidate,
          open_result.source,
          out_guest,
          open_result.opened_guest,
          false);
      if (trace || refii::vfs::LooseAssetOverrideTracingEnabled() ||
          refii::vfs::ShouldLogLooseStatsMilestone(
              open_result.hit_count)) {
        REXLOG_INFO("Refii VFS Provider_FindStreamByHash hit hit={} "
                    "hash=0x{:08X} candidate={} provider_relative={} "
                    "host={} source={} stream=0x{:08X} provider={} entry_key=0x{:08X} "
                    "entry_name={} mode=0x{:08X} sample={} names={}",
                    open_result.hit_count,
                    resource_hash,
                    open_result.candidate,
                    open_result.resolution.provider_relative_path,
                    open_result.resolution.host_path.string(),
                    open_result.source,
                    open_result.opened_guest,
                    refii::vfs::DescribeGuestObject(
                        base, provider_guest, 12, 12),
                    provider_entry_key,
                    provider_entry_name,
                    mode,
                    provider_hash_trace_index,
                    refii::vfs::JoinStrings(runtime_names, ", "));
      }
      return;
    }
    if (open_result.disposition ==
        refii::vfs::RefiiVfsOpenDisposition::OpenFailed) {
      if (trace || refii::vfs::LooseAssetOverrideTracingEnabled()) {
        REXLOG_WARN("Refii VFS Provider_FindStreamByHash matched "
                    "hash=0x{:08X} candidate={}, but host stream failed {}",
                    resource_hash,
                    open_result.candidate,
                    open_result.resolution.host_path.string());
      }
    } else if (trace) {
      REXLOG_INFO("Refii VFS Provider_FindStreamByHash miss hash=0x{:08X} "
                  "provider={} entry_key=0x{:08X} entry_name={} mode=0x{:08X} sample={} "
                  "names={} disposition={} ownership={} reason={}",
                  resource_hash,
                  refii::vfs::DescribeGuestObject(base, provider_guest, 12, 12),
                  provider_entry_key,
                  provider_entry_name,
                  mode,
                  provider_hash_trace_index,
                  refii::vfs::JoinStrings(runtime_names, ", "),
                  refii::vfs::ToString(open_result.disposition),
                  refii::vfs::ToString(open_result.ownership.kind),
                  open_result.ownership.reason);
    }
  }

  ctx = original_ctx;
  __imp__rex_lhVFS_Provider_FindStreamByHash(ctx, base);

  if (trace) {
    const uint32_t stream_guest =
        out_guest != 0 ? refii::vfs::TryReadOutputPointer(base, out_guest)
                       : 0;
    REXLOG_INFO("VFS trace Provider_FindStreamByHash native exit lr=0x{:08X} "
                "hash=0x{:08X} out=0x{:08X} stream={} result=0x{:08X} "
                "provider={} entry_key=0x{:08X} entry_name={} sample={} names={}",
                original_ctx.lr,
                resource_hash,
                out_guest,
                refii::vfs::DescribeGuestObject(base, stream_guest, 12, 12),
                ctx.r3.u32,
                refii::vfs::DescribeGuestObject(base, provider_guest, 12, 12),
                provider_entry_key,
                provider_entry_name,
                provider_hash_trace_index,
                refii::vfs::JoinStrings(runtime_names, ", "));
  }
}

REX_EXTERN(rex_lhVFS_Provider_OpenStream) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhVFS_Provider_OpenStream);

  const PPCContext original_ctx = ctx;
  const uint32_t out_guest = ctx.r3.u32;
  const uint32_t provider_guest = ctx.r4.u32;
  const uint32_t name_guest = ctx.r5.u32;
  const uint32_t mode = ctx.r6.u32;
  const std::string provider =
      refii::vfs::ReadGuestWideStringObject(base, provider_guest + 8, 4096);
  const std::string name =
      refii::vfs::ReadGuestWideStringObject(base, name_guest, 4096);
  const std::vector<std::string> asset_candidates =
      refii::vfs::BuildProviderOpenAssetPathCandidates(provider, name);
  const std::string asset_path =
      asset_candidates.empty() ? refii::vfs::NormalizeLooseAssetKey(name)
                               : asset_candidates.front();

  const bool trace =
      refii::vfs::ShouldLogFileApiPath("Provider_OpenStream", asset_path) ||
      refii::vfs::ShouldLogFileApiPath("Provider_OpenStream", name);
  refii::vfs::RefiiVfsOwnershipDecision ownership;

  if (refii::vfs::RefiiLooseAssetBackendEnabled() &&
      out_guest != 0 && !asset_candidates.empty()) {
    refii::vfs::RefiiVfsOpenResult open_result;
    std::string opened_asset_path;
    for (const std::string& candidate : asset_candidates) {
      ctx = original_ctx;
      refii::vfs::TryOpenRefiiVfsAsset(
          ctx,
          base,
          original_ctx,
          refii::vfs::RefiiVfsOpenShape::RawStream,
          refii::vfs::RefiiVfsOpenRequest{
              .api = "Provider_OpenStream",
              .request_name = candidate,
              .out_guest = out_guest,
              .manager_guest = provider_guest,
              .mode = mode,
              .caller_lr = static_cast<uint32_t>(original_ctx.lr),
              .prefer_resource_manager_texture_shape =
                  refii::vfs::IsTextureAssetCandidate(candidate),
              .allow_optional_null_miss = true,
          },
          &open_result);
      ownership = open_result.ownership;
      if (open_result.disposition ==
          refii::vfs::RefiiVfsOpenDisposition::Opened) {
        opened_asset_path = candidate;
        if (refii::vfs::IsTextureAssetCandidate(opened_asset_path)) {
          refii::vfs::LogRefiiOwnedTextureStreamBuilt(
              base,
              "Provider_OpenStream",
              static_cast<uint32_t>(original_ctx.lr),
              refii::vfs::ComputeResourceNameHash(opened_asset_path),
              opened_asset_path,
              open_result.resolution,
              open_result.candidate,
              open_result.source,
              out_guest,
              open_result.opened_guest,
              false);
        }
        return;
      }
      if (open_result.disposition !=
          refii::vfs::RefiiVfsOpenDisposition::Miss) {
        opened_asset_path = candidate;
        break;
      }
    }
    if (refii::vfs::IsHandledRefiiVfsOpenDisposition(
            open_result.disposition)) {
      return;
    }
    if (trace || refii::vfs::VfsTracingEnabled()) {
      REXLOG_INFO("Refii VFS Provider_OpenStream helper fallback "
                  "lr=0x{:08X} asset={} candidates={} disposition={} route=native",
                  original_ctx.lr,
                  opened_asset_path.empty() ? asset_path : opened_asset_path,
                  refii::vfs::JoinStrings(asset_candidates, ", "),
                  refii::vfs::ToString(open_result.disposition));
    }
    if (trace) {
      REXLOG_INFO("VFS trace Provider_OpenStream enter lr=0x{:08X} out=0x{:08X} "
                  "provider=0x{:08X} provider_path={} name=0x{:08X} "
                  "name_path={} asset={} mode={}",
                  original_ctx.lr,
                  out_guest,
                  provider_guest,
                  provider,
                  name_guest,
                  name,
                  asset_path,
                  mode);
    }
    ctx = original_ctx;
    __imp__rex_lhVFS_Provider_OpenStream(ctx, base);
    if (trace) {
      const uint32_t stream_guest =
          out_guest != 0 ? refii::vfs::TryReadOutputPointer(base, out_guest)
                         : 0;
      REXLOG_INFO("VFS trace Provider_OpenStream exit lr=0x{:08X} out=0x{:08X} "
                  "stream=0x{:08X} result=0x{:08X} stream_words={} {}",
                  original_ctx.lr,
                  out_guest,
                  stream_guest,
                  ctx.r3.u32,
                  refii::vfs::FormatGuestWords(base, stream_guest, 14),
                  refii::vfs::FormatGuestObjectVTable(
                      base, stream_guest, 12));
    }
    if (refii::vfs::VfsTracingEnabled() &&
        ownership.kind != refii::vfs::RefiiVfsOwnershipKind::RefiiOwned) {
      REXLOG_INFO("Refii VFS native fallback api=Provider_OpenStream "
                  "lr=0x{:08X} asset={} ownership={} reason={}",
                  original_ctx.lr,
                  asset_path,
                  refii::vfs::ToString(ownership.kind),
                  ownership.reason);
    }
    return;
  }

  if (trace) {
    REXLOG_INFO("VFS trace Provider_OpenStream enter lr=0x{:08X} out=0x{:08X} "
                "provider=0x{:08X} provider_path={} name=0x{:08X} "
                "name_path={} asset={} mode={}",
                original_ctx.lr,
                out_guest,
                provider_guest,
                provider,
                name_guest,
                name,
                asset_path,
                mode);
  }

  __imp__rex_lhVFS_Provider_OpenStream(ctx, base);

  if (trace) {
    const uint32_t stream_guest =
        out_guest != 0 ? refii::vfs::TryReadOutputPointer(base, out_guest)
                       : 0;
    REXLOG_INFO("VFS trace Provider_OpenStream exit lr=0x{:08X} out=0x{:08X} "
                "stream=0x{:08X} result=0x{:08X} stream_words={} {}",
                original_ctx.lr,
                out_guest,
                stream_guest,
                ctx.r3.u32,
                refii::vfs::FormatGuestWords(base, stream_guest, 14),
                refii::vfs::FormatGuestObjectVTable(base, stream_guest, 12));
  }
  if (refii::vfs::VfsTracingEnabled() && !asset_path.empty() &&
      ownership.kind != refii::vfs::RefiiVfsOwnershipKind::RefiiOwned) {
    REXLOG_INFO("Refii VFS native fallback api=Provider_OpenStream "
                "lr=0x{:08X} asset={} ownership={} reason={}",
                original_ctx.lr,
                asset_path,
                refii::vfs::ToString(ownership.kind),
                ownership.reason);
  }
}

REX_EXTERN(rex_lhBank_OpenStreamByName) {
  REX_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhBank_OpenStreamByName);

  const PPCContext original_ctx = ctx;
  const uint32_t out_guest = ctx.r3.u32;
  const uint32_t bank_guest = ctx.r4.u32;
  const uint32_t name_guest = ctx.r5.u32;
  const uint32_t mode = ctx.r6.u32;

  if (refii::vfs::RefiiLooseAssetBackendEnabled() &&
      refii::vfs::LooseBankEntryOverridesEnabled()) {
    const std::string entry_name =
        refii::vfs::ReadGuestWideStringObject(base, name_guest, 4096);
    const std::string bank_name =
        refii::vfs::ReadBankNameForLookup(base, bank_guest);
    if (!entry_name.empty()) {
      const uint32_t manager_guest =
          refii::vfs::GetGlobalDataProvider(base, original_ctx);
      refii::vfs::RefiiVfsOpenResult open_result;
      refii::vfs::TryOpenRefiiBankEntryAsset(
          ctx,
          base,
          original_ctx,
          refii::vfs::RefiiBankEntryOpenRequest{
              .api = "Bank_OpenStreamByName",
              .bank_name = bank_name,
              .entry_name = entry_name,
              .out_guest = out_guest,
              .manager_guest = manager_guest,
              .mode = mode,
              .caller_lr = static_cast<uint32_t>(original_ctx.lr),
              .count_bank_lookup =
                  !refii::vfs::IsLuaScriptAssetKey(entry_name),
              .lookup_label = "bank_name",
              .lua_trace_tag = "bank_open_stream_by_name",
          },
          &open_result);
      if (open_result.disposition ==
          refii::vfs::RefiiVfsOpenDisposition::Opened) {
        const uint64_t hits =
            refii::vfs::LooseStats().bank_hits.fetch_add(
                1, std::memory_order_relaxed) +
            1;
        refii::vfs::LogLooseStatsMilestone("bank_hits", hits);
        if (refii::vfs::IsLuaScriptAssetKey(entry_name)) {
          if (refii::vfs::LooseAssetOverrideTracingEnabled() ||
              refii::vfs::VfsTracingEnabled() ||
              refii::vfs::ShouldLogLooseStatsMilestone(hits)) {
            REXLOG_INFO("Refii VFS bank Lua script hit hit={} api=name "
                        "bank={} entry={} candidate={} source={} "
                        "provider_relative={} host={} stream=0x{:08X}",
                        hits,
                        bank_name,
                        entry_name,
                        open_result.candidate,
                        open_result.source,
                        open_result.resolution.provider_relative_path,
                        open_result.resolution.host_path.string(),
                        open_result.opened_guest);
          }
        } else {
          refii::vfs::LogLooseBankHitDetail("name",
                                                base,
                                                original_ctx,
                                                out_guest,
                                                bank_guest,
                                                manager_guest,
                                                mode,
                                                hits,
                                                bank_name,
                                                entry_name,
                                                open_result.candidate,
                                                open_result.resolution);
          if (refii::vfs::LooseAssetOverrideTracingEnabled()) {
            REXLOG_INFO("Loose bank asset override bank={} entry={} "
                        "candidate={} -> {}",
                        bank_name,
                        entry_name,
                        open_result.candidate,
                        open_result.resolution.host_path.string());
          }
        }
        return;
      }
      if (refii::vfs::IsLuaScriptAssetKey(entry_name) ||
          refii::vfs::ShouldSkipLooseNativeAsset(entry_name, "bank_name")) {
        if (refii::vfs::VfsTracingEnabled()) {
          REXLOG_INFO("Refii VFS bank name fallback bank={} entry={} "
                      "disposition={}",
                      bank_name,
                      entry_name,
                      refii::vfs::ToString(open_result.disposition));
        }
        ctx = original_ctx;
        __imp__rex_lhBank_OpenStreamByName(ctx, base);
        return;
      }
      if (out_guest != 0) {
        refii::vfs::StoreGuestU32(base, out_guest, 0);
      }
      ctx.r3.u32 = out_guest;
      return;
    } else if (refii::vfs::LooseAssetOverrideTracingEnabled()) {
      REXLOG_INFO("Loose bank asset lookup skipped for unreadable entry name "
                  "object=0x{:08X} bank=0x{:08X}",
                  name_guest,
                  bank_guest);
    }
    if (refii::vfs::FindSyntheticBankRecord(bank_guest) != nullptr) {
      if (out_guest != 0) {
        refii::vfs::StoreGuestU32(base, out_guest, 0);
      }
      ctx.r3.u32 = out_guest;
      return;
    }
  }

  ctx = original_ctx;
  __imp__rex_lhBank_OpenStreamByName(ctx, base);
}
