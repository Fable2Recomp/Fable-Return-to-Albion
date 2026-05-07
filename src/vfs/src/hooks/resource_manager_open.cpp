#include "vfs_hooks_private.h"

PPC_FUNC_IMPL(rex_lhResourceManager_Open) {
  PPC_FUNC_PROLOGUE();

  const PPCContext original_ctx = ctx;
  refii::vfs::RecordFrontendTextureConsumerResourceBoundary(
      "ResourceManager_Open", original_ctx.r3.u32, 0);
  if (refii::vfs::kDirectResourceManagerOpenOverride &&
      refii::vfs::RefiiLooseAssetBackendEnabled()) {
    const uint32_t resource_hash = original_ctx.r3.u32;
    const uint32_t out_guest = original_ctx.r4.u32;
    PPCContext boot_ctx = original_ctx;
    refii::vfs::EnsureRefiiBootBankTemplates(
        boot_ctx, base, "ResourceManager_Open");
    refii::vfs::RefiiVfsOpenResult open_result;
    const refii::vfs::RefiiVfsOpenDisposition open_disposition =
        refii::vfs::TryOpenRefiiVfsAsset(
            ctx,
            base,
            original_ctx,
            refii::vfs::RefiiVfsOpenShape::ResourceWrapper,
            refii::vfs::RefiiVfsOpenRequest{
                .api = "ResourceManager_Open",
                .resource_hash = resource_hash,
                .out_guest = out_guest,
                .manager_guest = original_ctx.r5.u32,
                .mode = original_ctx.r7.u32 == 0 ? 1 : original_ctx.r7.u32,
                .resource_flags = original_ctx.r8.u32,
                .resource_offset = original_ctx.r5.u32,
                .resource_size = original_ctx.r6.u32,
                .caller_lr = static_cast<uint32_t>(original_ctx.lr),
                .prefer_resource_manager_texture_shape = true,
            },
            &open_result);
    if (open_disposition == refii::vfs::RefiiVfsOpenDisposition::Opened) {
      ctx.r3.u32 = 1;
      refii::vfs::LogRefiiOwnedTextureStreamBuilt(
          base,
          "ResourceManager_Open",
          static_cast<uint32_t>(original_ctx.lr),
          resource_hash,
          open_result.candidate,
          open_result.resolution,
          open_result.candidate,
          open_result.source,
          out_guest,
          open_result.opened_guest,
          true);
      return;
    }
    if (refii::vfs::IsHandledRefiiVfsOpenDisposition(open_disposition)) {
      return;
    }
    if (open_result.ownership.kind !=
        refii::vfs::RefiiVfsOwnershipKind::RefiiOwned) {
      refii::vfs::LooseStats().refii_native_fallbacks.fetch_add(
          1, std::memory_order_relaxed);
    }
  }

  if (refii::vfs::ShouldLogVfsTraceOperation("ResourceManager_Open")) {
    REXLOG_INFO("VFS trace ResourceManager_Open enter lr=0x{:08X} "
                "ctr=0x{:08X} r3=0x{:08X}({}) r4=0x{:08X}({}) "
                "r5=0x{:08X}({}) r6=0x{:08X}({}) r7=0x{:08X} r8=0x{:08X}",
                original_ctx.lr,
                original_ctx.ctr.u32,
                original_ctx.r3.u32,
                refii::vfs::ReadMaybeGuestString(base, original_ctx.r3.u32),
                original_ctx.r4.u32,
                refii::vfs::ReadMaybeGuestString(base, original_ctx.r4.u32),
                original_ctx.r5.u32,
                refii::vfs::ReadMaybeGuestString(base, original_ctx.r5.u32),
                original_ctx.r6.u32,
                refii::vfs::ReadMaybeGuestString(base, original_ctx.r6.u32),
                original_ctx.r7.u32,
                original_ctx.r8.u32);
  }

  __imp__rex_lhResourceManager_Open(ctx, base);

  if (refii::vfs::ShouldLogVfsTraceOperation("ResourceManager_Open")) {
    REXLOG_INFO("VFS trace ResourceManager_Open exit lr=0x{:08X} "
                "result=0x{:08X}",
                original_ctx.lr,
                ctx.r3.u32);
  }
}

PPC_FUNC_IMPL(sub_82B6DB20) {
  PPC_FUNC_PROLOGUE();

  const PPCContext original_ctx = ctx;
  const uint32_t texture_guest = original_ctx.r3.u32;
  const bool refii_enabled =
      refii::vfs::RefiiLooseAssetBackendEnabled() && texture_guest != 0;

  const bool trace_texture =
      refii::vfs::VfsTracingEnabled() && texture_guest != 0;
  const uint32_t original_texture_wrapper =
      texture_guest == 0
          ? 0
          : refii::vfs::NormalizeRefiiResourceStreamWrapper(
                base, refii::vfs::LoadGuestU32(base, texture_guest + 100));
  if (trace_texture &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation("TextureCallerDB20",
                                                         64)) {
    REXLOG_INFO("VFS trace TextureCaller DB20 enter lr=0x{:08X} "
                "texture=0x{:08X} hash=0x{:08X} active_count={} "
                "flags=0x{:08X} current_wrapper=0x{:08X} "
                "texture_words={}",
                original_ctx.lr,
                texture_guest,
                refii::vfs::LoadGuestU32(base, texture_guest + 4),
                refii::vfs::LoadGuestU32(base, texture_guest + 16),
                refii::vfs::LoadGuestU32(base, texture_guest + 28),
                original_texture_wrapper,
                refii::vfs::FormatGuestWords(base, texture_guest, 32));
  }

  if (refii_enabled && refii::vfs::kDirectTextureCallerOverride) {
    const uint32_t active_count =
        refii::vfs::LoadGuestU32(base, texture_guest + 16);
    const uint32_t flags = refii::vfs::LoadGuestU32(base, texture_guest + 28);
    const bool use_resource_offset_path =
        active_count > 0 && ((flags & 0x1FU) & 0xFFFFFFF1U) != 0;

    if (!use_resource_offset_path) {
      const uint32_t resource_hash =
          refii::vfs::LoadGuestU32(base, texture_guest + 4);
      refii::vfs::LooseAssetResolution resolution;
      std::string name;
      std::string candidate;
      std::string source;
      if (refii::vfs::ResolveRefiiOwnedTextureDescriptorHash(
              "TextureCallerDB20",
              resource_hash,
              0x82B6DBE0,
              &resolution,
              &name,
              &candidate,
              &source)) {
        refii::vfs::MaybeUpgradeResourceManagerTextureResolution(
            resource_hash,
            name.empty() ? candidate : name,
            0x82B6DBE0,
            &resolution,
            &candidate);

        PPCContext commit_ctx = original_ctx;
        const bool committed =
            refii::vfs::TryCommitRefiiFrontendTextureWrapper(
                commit_ctx,
                base,
                texture_guest,
                resource_hash,
                resolution,
                name,
                candidate,
                source,
                0x82B6DBE0,
                "TextureCallerDB20");
        if (refii::vfs::VfsTracingEnabled() ||
            refii::vfs::IsMainMenuTextureTraceAsset(
                resolution.provider_relative_path)) {
          REXLOG_INFO("Refii texture caller DBE0 helper commit lr=0x{:08X} "
                      "texture=0x{:08X} hash=0x{:08X} candidate={} "
                      "source={} committed={} provider={} host={}",
                      original_ctx.lr,
                      texture_guest,
                      resource_hash,
                      candidate,
                      source,
                      committed,
                      refii::vfs::NormalizeLooseAssetKey(
                          resolution.provider_relative_path),
                      resolution.host_path.string());
        }
        if (committed) {
          refii::vfs::LogRefiiVfsChainVerdict(
              "texture_parser",
              static_cast<uint32_t>(original_ctx.lr),
              candidate,
              candidate,
              resolution.provider_relative_path,
              "Refii_resource_wrapper",
              "DB20_wrapper_assigned",
              "TraceOnce",
              "C340_or_TextureStreamBuild");
          ctx = original_ctx;
          ctx.r3.u32 = texture_guest;
          return;
        }
      } else if (refii::vfs::VfsTracingEnabled()) {
        REXLOG_INFO("Refii texture caller DBE0 no Refii resolution "
                    "texture=0x{:08X} hash=0x{:08X} active_count={} "
                    "flags=0x{:08X}",
                    texture_guest,
                    resource_hash,
                    active_count,
                    flags);
      }
    }
  }

  __imp__sub_82B6DB20(ctx, base);

  if (trace_texture) {
    const uint32_t next_texture_wrapper =
        refii::vfs::NormalizeRefiiResourceStreamWrapper(
            base, refii::vfs::LoadGuestU32(base, texture_guest + 100));
    const uint32_t wrapper_inner =
        next_texture_wrapper == 0
            ? 0
            : refii::vfs::LoadGuestU32(base, next_texture_wrapper + 16);
    const std::string provider =
        next_texture_wrapper == 0
            ? std::string{}
            : refii::vfs::GetRefiiResourceWrapperProviderKey(
                  base, next_texture_wrapper, wrapper_inner);
    if (next_texture_wrapper != 0) {
      refii::vfs::RecordRefiiTextureWrapperOwner(next_texture_wrapper,
                                                     texture_guest);
    }
    if (next_texture_wrapper != original_texture_wrapper ||
        refii::vfs::IsMainMenuTextureTraceAsset(provider) ||
        refii::vfs::ShouldLogRepeatedVfsTraceOperation(
            "TextureCallerDB20Exit", 32)) {
      REXLOG_INFO("VFS trace TextureCaller DB20 exit lr=0x{:08X} "
                  "texture=0x{:08X} result=0x{:08X} wrapper=0x{:08X} "
                  "previous_wrapper=0x{:08X} provider={} wrapper_words={} "
                  "texture_words={}",
                  original_ctx.lr,
                  texture_guest,
                  ctx.r3.u32,
                  next_texture_wrapper,
                  original_texture_wrapper,
                  refii::vfs::NormalizeLooseAssetKey(provider),
                  refii::vfs::FormatGuestWords(
                      base, next_texture_wrapper, 12),
                  refii::vfs::FormatGuestWords(base, texture_guest, 32));
    }
  }
}

PPC_FUNC_IMPL(rex_lhResourceManager_OpenInternal) {
  PPC_FUNC_PROLOGUE();

  const PPCContext original_ctx = ctx;
  if (refii::vfs::ShouldLogVfsTraceOperation(
          "ResourceManager_OpenInternal")) {
    REXLOG_INFO("VFS trace ResourceManager_OpenInternal enter lr=0x{:08X} "
                "manager={} r4=0x{:08X} r5=0x{:08X} out=0x{:08X} "
                "r7=0x{:08X}({}) r8=0x{:08X} manager_ascii_32={}",
                original_ctx.lr,
                refii::vfs::DescribeGuestObject(
                    base, original_ctx.r3.u32, 16, 12),
                original_ctx.r4.u32,
                original_ctx.r5.u32,
                original_ctx.r6.u32,
                original_ctx.r7.u32,
                refii::vfs::ReadMaybeGuestString(base, original_ctx.r7.u32),
                original_ctx.r8.u32,
                refii::vfs::FormatGuestAsciiBytes(
                    base, original_ctx.r3.u32 + 32, 160));
  }

  __imp__rex_lhResourceManager_OpenInternal(ctx, base);

  if (refii::vfs::ShouldLogVfsTraceOperation(
          "ResourceManager_OpenInternal")) {
    const uint32_t out_stream =
        refii::vfs::TryReadOutputPointer(base, original_ctx.r6.u32);
    REXLOG_INFO("VFS trace ResourceManager_OpenInternal exit lr=0x{:08X} "
                "result=0x{:08X} out=0x{:08X} out_stream={}",
                original_ctx.lr,
                ctx.r3.u32,
                original_ctx.r6.u32,
                refii::vfs::DescribeGuestObject(base, out_stream, 12, 12));
  }
}

PPC_FUNC_IMPL(sub_82426470) {
  PPC_FUNC_PROLOGUE();

  const PPCContext original_ctx = ctx;
  std::string command =
      refii::vfs::ReadMaybeGuestString(base, original_ctx.r3.u32, 512);
  std::string known_command =
      refii::vfs::KnownGuiCommandForCallerLr(original_ctx.lr);
  if (command.empty()) {
    command = known_command;
  }
  const bool gameface_command =
      command.find("GUI:") != std::string::npos ||
      command.find("GameFace") != std::string::npos ||
      command.find("gameface") != std::string::npos;
  const bool trace =
      refii::vfs::VfsTracingEnabled() &&
      (gameface_command ||
       refii::vfs::RefiiPostGameFacePackageHandoffSignalEmitted()) &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "GuiCommandExecutor_82426470", 64);
  const uint32_t global =
      base != nullptr ? refii::vfs::LoadGuestU32(base, 0x83496B08u) : 0;
  const uint8_t immediate_mode =
      refii::vfs::LooksLikeGuestPointer(global)
          ? refii::vfs::LoadGuestU8(base, global + 100)
          : 0;
  if (trace) {
    const uint32_t ref_string =
        refii::vfs::LooksLikeGuestPointer(original_ctx.r3.u32)
            ? refii::vfs::LoadGuestU32(base, original_ctx.r3.u32)
            : 0;
    const uint32_t data_string =
        refii::vfs::LooksLikeGuestPointer(ref_string)
            ? refii::vfs::LoadGuestU32(base, ref_string)
            : 0;
    REXLOG_INFO("VFS trace GUI command executor enter sub_82426470 "
                "lr=0x{:08X} command={} command_obj=0x{:08X} gui_ctx=0x{:08X} "
                "global=0x{:08X} immediate={} known={} obj_words={} "
                "ref=0x{:08X} ref_words={} data=0x{:08X} data_ascii={} "
                "handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                command,
                original_ctx.r3.u32,
                original_ctx.r4.u32,
                global,
                immediate_mode,
                known_command,
                refii::vfs::FormatGuestWordsIfPointer(
                    base, original_ctx.r3.u32, 8),
                ref_string,
                refii::vfs::FormatGuestWordsIfPointer(base, ref_string, 4),
                data_string,
                refii::vfs::FormatGuestAsciiBytes(base, data_string, 128),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  __imp__sub_82426470(ctx, base);

  if (gameface_command) {
    refii::vfs::RecordRefiiMainMenuBlockerEvent(
        "gui_command_executor_82426470",
        fmt::format("command={} lr=0x{:08X} result=0x{:08X} "
                    "command_obj=0x{:08X} gui_ctx=0x{:08X} immediate={}",
                    command,
                    static_cast<uint32_t>(original_ctx.lr),
                    ctx.r3.u32,
                    original_ctx.r3.u32,
                    original_ctx.r4.u32,
                    immediate_mode));
  }

  if (trace) {
    REXLOG_INFO("VFS trace GUI command executor exit sub_82426470 "
                "lr=0x{:08X} command={} result=0x{:08X} handoff=[{}] "
                "main_menu=[{}]",
                original_ctx.lr,
                command,
                ctx.r3.u32,
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
}

PPC_FUNC_IMPL(sub_82372500) {
  PPC_FUNC_PROLOGUE();

  const PPCContext original_ctx = ctx;
  const std::string command =
      refii::vfs::ReadMaybeGuestString(base, original_ctx.r3.u32, 512);
  const bool trace =
      refii::vfs::VfsTracingEnabled() &&
      (refii::vfs::RefiiPostGameFacePackageHandoffSignalEmitted() ||
       command.find("GUI:") != std::string::npos ||
       command.find("GameFace") != std::string::npos ||
       command.find("gameface") != std::string::npos) &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "GuiCommandImmediate_82372500", 64);

  if (trace) {
    const uint32_t ref_string =
        refii::vfs::LooksLikeGuestPointer(original_ctx.r3.u32)
            ? refii::vfs::LoadGuestU32(base, original_ctx.r3.u32)
            : 0;
    const uint32_t data_string =
        refii::vfs::LooksLikeGuestPointer(ref_string)
            ? refii::vfs::LoadGuestU32(base, ref_string)
            : 0;
    REXLOG_INFO("VFS trace GUI command immediate enter sub_82372500 "
                "lr=0x{:08X} command={} command_obj=0x{:08X} exec=0x{:08X} "
                "obj_words={} ref=0x{:08X} ref_words={} data=0x{:08X} "
                "data_ascii={} exec_words={} handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                command,
                original_ctx.r3.u32,
                original_ctx.r4.u32,
                refii::vfs::FormatGuestWordsIfPointer(
                    base, original_ctx.r3.u32, 8),
                ref_string,
                refii::vfs::FormatGuestWordsIfPointer(base, ref_string, 4),
                data_string,
                refii::vfs::FormatGuestAsciiBytes(base, data_string, 128),
                refii::vfs::FormatGuestWordsIfPointer(
                    base, original_ctx.r4.u32, 24),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }

  __imp__sub_82372500(ctx, base);

  if (trace) {
    REXLOG_INFO("VFS trace GUI command immediate exit sub_82372500 "
                "lr=0x{:08X} command={} result=0x{:08X} exec_words={} "
                "handoff=[{}] main_menu=[{}]",
                original_ctx.lr,
                command,
                ctx.r3.u32,
                refii::vfs::FormatGuestWordsIfPointer(
                    base, original_ctx.r4.u32, 24),
                refii::vfs::FormatRefiiPostVideoHandoffTraceForLog(),
                refii::vfs::FormatRefiiMainMenuBlockerTraceForLog());
  }
}
