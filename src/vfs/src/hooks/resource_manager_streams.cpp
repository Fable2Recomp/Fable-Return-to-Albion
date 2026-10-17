#include "vfs_hooks_private.h"
#include "core/guest_build.h"

namespace {

} // namespace

REX_EXTERN(rex_lhResourceManager_FindAndCreateStream) {
  REX_FUNC_PROLOGUE();

  // Passthrough logging one stage of the Open→OpenInternal→FindAndCreateStream→Submit chain
  // (see resource_manager_open.cpp).
  {
    const PPCContext entry_ctx = ctx;
    __imp__rex_lhResourceManager_FindAndCreateStream(ctx, base);
    static std::atomic<uint32_t> chain_logged{0};
    if (chain_logged.fetch_add(1, std::memory_order_relaxed) < 8u) {
      const uint32_t produced =
          entry_ctx.r3.u32 != 0 ? refii::vfs::LoadGuestU32(base, entry_ctx.r3.u32) : 0u;
      REXLOG_INFO("Refii FindAndCreateStream: file=0x{:08X} offset=0x{:08X} size=0x{:08X} "
                  "a6=0x{:08X} mode={} -> manifest=0x{:08X} vtable=0x{:08X}",
                  entry_ctx.r4.u32, entry_ctx.r5.u32, entry_ctx.r6.u32,
                  entry_ctx.r8.u32, entry_ctx.r9.u32, produced,
                  produced != 0 ? refii::vfs::LoadGuestU32(base, produced) : 0u);
    }
    return;
  }


  const PPCContext original_ctx = ctx;
  const uint32_t out_guest = ctx.r3.u32;
  const uint32_t resource_manager_guest = ctx.r4.u32;
  const uint32_t name_guest = ctx.r5.u32;
  const uint32_t arg6 = ctx.r6.u32;
  const uint32_t arg7 = ctx.r7.u32;
  const uint32_t arg8 = ctx.r8.u32;
  const uint32_t arg9 = ctx.r9.u32;
  const std::string name =
      refii::vfs::ReadBestEffortGuestString(base, name_guest, 4096);
  const bool trace =
      refii::vfs::VfsTracingEnabled() ||
      (refii::vfs::LooseAssetOverrideTracingEnabled() &&
       (!name.empty() || refii::vfs::FindLooseStreamRecord(
                             base, arg6, nullptr, nullptr)));

  if (trace) {
    REXLOG_INFO("ResourceManager FindAndCreateStream enter caller_lr=0x{:08X} "
                "caller_ctr=0x{:08X} out=0x{:08X} manager=0x{:08X} "
                "name_obj=0x{:08X} name={} r6=0x{:08X} r7=0x{:08X} "
                "r8=0x{:08X} r9=0x{:08X} name_words={} "
                "manager_ascii_32={} manager_words={} {}",
                original_ctx.lr,
                original_ctx.ctr.u32,
                out_guest,
                resource_manager_guest,
                name_guest,
                name,
                arg6,
                arg7,
                arg8,
                arg9,
                refii::vfs::FormatGuestWords(base, name_guest, 10),
                refii::vfs::FormatGuestAsciiBytes(
                    base, resource_manager_guest + 32, 128),
                refii::vfs::FormatGuestWords(
                    base, resource_manager_guest, 16),
                refii::vfs::FormatGuestObjectVTable(
                    base, resource_manager_guest, 12));
  }

  if (refii::vfs::kDirectResourceManagerWrapperOverride) {
    uint32_t resource_hash = 0;
    if (name.empty() && resource_manager_guest != 0 &&
        refii::vfs::LoadGuestU32(base, resource_manager_guest) ==
            refii::guest::Address(0x820F9EA4u)) {
      resource_hash =
          refii::vfs::LoadGuestU32(base, resource_manager_guest + 48);
    }
    if (!name.empty() || resource_hash != 0) {
      PPCContext boot_ctx = original_ctx;
      refii::vfs::EnsureRefiiBootBankTemplates(
          boot_ctx, base, "ResourceManager_FindAndCreateStream");
      refii::vfs::RefiiVfsOpenResult open_result;
      const refii::vfs::RefiiVfsOpenShape shape =
          !name.empty() && refii::vfs::IsLuaScriptAssetKey(name)
              ? refii::vfs::RefiiVfsOpenShape::RawStream
          : !name.empty() &&
                  !refii::vfs::GetResourceManagerCachedDirectBypassReason(
                       name)
                       .empty()
              ? refii::vfs::RefiiVfsOpenShape::NativeGenerated
              : refii::vfs::RefiiVfsOpenShape::ResourceWrapper;
      refii::vfs::TryOpenRefiiVfsAsset(
          ctx,
          base,
          original_ctx,
          shape,
          refii::vfs::RefiiVfsOpenRequest{
              .api = "ResourceManager_FindAndCreateStream",
              .request_name = name,
              .resource_hash = resource_hash,
              .out_guest = out_guest,
              .manager_guest = resource_manager_guest,
              // Mode is r6, not r7: OpenInternal (0x82C68CD0) default-resolves
              // it from -1 via manager vtbl[+24] before passing it here as a4.
              .mode = arg6,
              .resource_flags = arg9,
              .caller_lr = static_cast<uint32_t>(original_ctx.lr),
              .allow_optional_null_miss = true,
          },
          &open_result);
      if (open_result.disposition ==
          refii::vfs::RefiiVfsOpenDisposition::Opened) {
        if (shape == refii::vfs::RefiiVfsOpenShape::ResourceWrapper) {
          refii::vfs::LogRefiiOwnedTextureStreamBuilt(
              base,
              "ResourceManager_FindAndCreateStream",
              static_cast<uint32_t>(original_ctx.lr),
              resource_hash,
              name.empty() ? open_result.candidate : name,
              open_result.resolution,
              open_result.candidate,
              open_result.source,
              out_guest,
              open_result.opened_guest,
              true);
        }
        return;
      }
      if (refii::vfs::IsHandledRefiiVfsOpenDisposition(
              open_result.disposition)) {
        return;
      }
    }
  }

  __imp__rex_lhResourceManager_FindAndCreateStream(ctx, base);

  if (trace) {
    const uint32_t stream_guest =
        out_guest != 0 ? refii::vfs::TryReadOutputPointer(base, out_guest) : 0;
    REXLOG_INFO("ResourceManager FindAndCreateStream exit caller_lr=0x{:08X} "
                "result=0x{:08X} out_stream=0x{:08X} out_words={} {}",
                original_ctx.lr,
                ctx.r3.u32,
                stream_guest,
                refii::vfs::FormatGuestWords(base, out_guest, 8),
                refii::vfs::FormatGuestObjectVTable(base, stream_guest, 12));
  }
}

REX_EXTERN(rex_lhResourceManager_GetCached) {
  REX_FUNC_PROLOGUE();

  const PPCContext original_ctx = ctx;
  const uint32_t out_guest = ctx.r3.u32;
  const uint32_t resource_manager_guest = ctx.r4.u32;
  const uint32_t name_guest = ctx.r5.u32;
  const uint32_t mode = ctx.r6.u32;
  const std::string name =
      refii::vfs::ReadBestEffortGuestString(base, name_guest, 4096);

  // Startup videos stay on the guest's own path: a `.bik` is owned by the guest's Bink player,
  // whose completion field (`player+233`, written by Bink decode) advances the movie index, and
  // until the index reaches the count `sub_822F4CE8` never returns a next state. Keyed on the
  // boot path's startup-movie predicate, not on a caller address.
  if (refii::vfs::IsStartupBikAssetName(name)) {
    __imp__rex_lhResourceManager_GetCached(ctx, base);
    return;
  }

  uint32_t caller_resource_hash = 0;
  if ((original_ctx.lr == refii::guest::Address(0x82C68EB0u) || original_ctx.lr == refii::guest::Address(0x82C68DD8u)) &&
      original_ctx.r1.u32 != 0) {
    caller_resource_hash =
        refii::vfs::LoadGuestU32(base, original_ctx.r1.u32 + 124);
  }

  std::string resolved_hash_name;
  if (name.empty() &&
      original_ctx.lr == refii::guest::Address(0x82C68EB0u) &&
      caller_resource_hash != 0) {
    std::string candidate = refii::vfs::HostAssetNameForHash(caller_resource_hash);
    if (!candidate.empty() && refii::vfs::HostAssetIsOverride(candidate)) {
      resolved_hash_name = std::move(candidate);
    }
  }
  const std::string& effective_name =
      !name.empty() ? name : resolved_hash_name;

  if (refii::vfs::IsStartupBikAssetName(effective_name)) {
    __imp__rex_lhResourceManager_GetCached(ctx, base);
    return;
  }

  const bool trace =
      refii::vfs::LooseAssetOverrideTracingEnabled() ||
      refii::vfs::ShouldLogVfsTraceOperation("ResourceManager_GetCached");
  const std::string_view direct_bypass_reason =
      refii::vfs::GetResourceManagerCachedDirectBypassReason(effective_name);
  refii::vfs::RefiiVfsOwnershipDecision ownership;
  refii::vfs::RecordRefiiGetCachedHandoffEvent(
      effective_name, original_ctx.lr, 0);

  if (trace) {
    REXLOG_INFO("ResourceManager GetCached enter caller_lr=0x{:08X} "
                "caller_ctr=0x{:08X} out=0x{:08X} manager=0x{:08X} "
                "name_obj=0x{:08X} name={} effective_name={} mode=0x{:08X} "
                "caller_resource_hash=0x{:08X}",
                original_ctx.lr,
                original_ctx.ctr.u32,
                out_guest,
                resource_manager_guest,
                name_guest,
                name,
                effective_name,
                mode,
                caller_resource_hash);
  }

  if (!effective_name.empty()) {
    refii::vfs::RefiiVfsOpenResult open_result;
    const bool bik_object_open_stream =
        original_ctx.lr == refii::guest::Address(0x82C6F534u) &&
        refii::vfs::IsStartupBikAssetName(effective_name);
    const refii::vfs::RefiiVfsOpenShape shape =
        bik_object_open_stream ? refii::vfs::RefiiVfsOpenShape::RawStream
        : refii::vfs::IsLuaScriptAssetKey(effective_name)
            ? refii::vfs::RefiiVfsOpenShape::NativeGenerated
        : !direct_bypass_reason.empty()
            ? refii::vfs::RefiiVfsOpenShape::NativeGenerated
            : refii::vfs::RefiiVfsOpenShape::RawStream;
    if (bik_object_open_stream &&
        refii::vfs::ShouldLogRepeatedVfsTraceOperation(
            "StartupBikGetCachedRawStreamShape", 8)) {
      REXLOG_INFO("Refii VFS startup BIK GetCached raw-stream shape "
                  "lr=0x{:08X} name={} reason=native_constructor_buffers_after_open",
                  original_ctx.lr,
                  effective_name);
    }
    refii::vfs::TryOpenRefiiVfsAsset(
        ctx,
        base,
        original_ctx,
        shape,
        refii::vfs::RefiiVfsOpenRequest{
            .api = "ResourceManager_GetCached",
            .request_name = effective_name,
            .resource_hash = caller_resource_hash,
            .out_guest = out_guest,
            .manager_guest = resource_manager_guest,
            .mode = mode,
            .caller_lr = static_cast<uint32_t>(original_ctx.lr),
            .allow_optional_null_miss = true,
        },
        &open_result);
    if (open_result.disposition ==
        refii::vfs::RefiiVfsOpenDisposition::Opened) {
      const uint32_t opened_stream_guest =
          refii::vfs::TryReadOutputPointer(base, out_guest);
      refii::vfs::RecordRefiiGetCachedHandoffEvent(
          effective_name, original_ctx.lr, opened_stream_guest);
      if (refii::vfs::IsTextureAssetCandidate(effective_name)) {
        refii::vfs::LogRefiiOwnedTextureStreamBuilt(
            base,
            "ResourceManager_GetCached",
            static_cast<uint32_t>(original_ctx.lr),
            caller_resource_hash,
            effective_name,
            open_result.resolution,
            open_result.candidate,
            open_result.source,
            out_guest,
            open_result.opened_guest,
            false);
      }
      return;
    }
    if (refii::vfs::IsHandledRefiiVfsOpenDisposition(
            open_result.disposition)) {
      refii::vfs::RecordRefiiGetCachedHandoffEvent(effective_name, original_ctx.lr, 0);
      return;
    }
    if (open_result.disposition ==
        refii::vfs::RefiiVfsOpenDisposition::NativeGenerated) {
      ownership = open_result.ownership;
    }
  }

  ctx = original_ctx;
  __imp__rex_lhResourceManager_GetCached(ctx, base);

  const bool repaired_native_shape =
      refii::vfs::TryRepairNativeShapedGetCachedOutput(
          ctx,
          base,
          original_ctx,
          out_guest,
          name,
          direct_bypass_reason,
          ownership);

  const uint32_t stream_guest =
      out_guest != 0 ? refii::vfs::TryReadOutputPointer(base, out_guest) : 0;
  refii::vfs::RecordRefiiGetCachedHandoffEvent(
      name, original_ctx.lr, stream_guest);

  if (refii::vfs::VfsTracingEnabled() && !direct_bypass_reason.empty()) {
    refii::vfs::MaybeLogStartupLuaGetCachedShapeVerdict(
        base,
        name,
        static_cast<uint32_t>(original_ctx.lr),
        stream_guest,
        repaired_native_shape,
        direct_bypass_reason);
  }
  if (refii::vfs::VfsVerboseIoTracingEnabled() &&
      (trace || (!direct_bypass_reason.empty() &&
                 refii::vfs::VfsTracingEnabled()))) {
    REXLOG_INFO("ResourceManager GetCached exit caller_lr=0x{:08X} "
                "result=0x{:08X} out=0x{:08X} out_stream=0x{:08X} "
                "bypass_reason={} native_shape_repaired={} stream_words={} {}",
                original_ctx.lr,
                ctx.r3.u32,
                out_guest,
                stream_guest,
                direct_bypass_reason.empty() ? "none" : direct_bypass_reason,
                repaired_native_shape,
                refii::vfs::FormatGuestWords(base, stream_guest, 12),
                refii::vfs::FormatGuestObjectVTable(
                    base, stream_guest, 12));
  }
  if (refii::vfs::VfsTracingEnabled() &&
      !name.empty() &&
      ownership.kind != refii::vfs::RefiiVfsOwnershipKind::RefiiOwned) {
    REXLOG_INFO("Refii VFS native fallback api=ResourceManager_GetCached "
                "lr=0x{:08X} name={} ownership={} reason={}",
                original_ctx.lr,
                name,
                refii::vfs::ToString(ownership.kind),
                ownership.reason);
  }
}
