#include "vfs_hooks_private.h"

PPC_FUNC_IMPL(sub_82B53438) {
  PPC_FUNC_PROLOGUE();

  if (refii::vfs::TrySilentLargePageReserve(ctx, base)) {
    return;
  }

  __imp__sub_82B53438(ctx, base);
}

PPC_FUNC_IMPL(sub_82B6C340) {
  PPC_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82B6C340);

  const PPCContext original_ctx = ctx;
  const uint32_t texture_guest = original_ctx.r3.u32;
  uint32_t wrapper_guest = 0;
  const std::string provider =
      refii::vfs::GetTextureParserTraceProvider(
          base, texture_guest, &wrapper_guest);
  const uint32_t texture_hash =
      texture_guest == 0
          ? 0
          : refii::vfs::LoadGuestU32(base, texture_guest + 4);
  const bool main_menu_texture =
      refii::vfs::IsMainMenuTextureResourceHash(texture_hash);
  const bool trace_texture_parser =
      refii::vfs::kTraceTextureParserInternals &&
      refii::vfs::VfsTracingEnabled() &&
      (refii::vfs::IsResourceWrapperTextureTraceAsset(provider) ||
       main_menu_texture);
  const uint32_t frame_guest = original_ctx.r1.u32 - 160;
  const uint32_t descriptor_guest = frame_guest + 96;
  if (trace_texture_parser) {
    refii::vfs::RecordTextureParserDescriptorTraceOwner(
        descriptor_guest, texture_guest, wrapper_guest, provider);
    refii::vfs::RecordFrontendTextureConsumerResourceBoundary(
        "TextureParser_C340_enter", texture_hash, wrapper_guest);
  }
  if (trace_texture_parser) {
    REXLOG_INFO("VFS trace TextureParser C340 enter lr=0x{:08X} "
                "texture=0x{:08X} hash=0x{:08X} wrapper=0x{:08X} provider={} "
                "state116=0x{:02X} descriptor=0x{:08X} texture_words={} "
                "wrapper_words={}",
                original_ctx.lr,
                texture_guest,
                texture_hash,
                wrapper_guest,
                refii::vfs::NormalizeLooseAssetKey(provider),
                texture_guest == 0
                    ? 0
                    : refii::vfs::LoadGuestU8(base, texture_guest + 116),
                descriptor_guest,
                refii::vfs::FormatGuestWords(base, texture_guest, 32),
                refii::vfs::FormatGuestWords(base, wrapper_guest, 12));
  }

  if (refii::vfs::TryHandleRefiiTextureParserC340NativeHandoff(
          ctx, base, "TextureParser_C340_hook")) {
  } else {
    __imp__sub_82B6C340(ctx, base);
  }

  if (trace_texture_parser) {
    const uint32_t bytes_pair_guest = frame_guest + 80;
    uint32_t final_wrapper_guest = wrapper_guest;
    const std::string final_provider =
        refii::vfs::GetTextureParserTraceProvider(
            base, texture_guest, &final_wrapper_guest);
    const std::string_view provider_for_log =
        final_provider.empty() ? std::string_view(provider)
                               : std::string_view(final_provider);
    refii::vfs::RecordFrontendTextureConsumerResourceBoundary(
        "TextureParser_C340_exit",
        texture_hash,
        ctx.r3.u32 == 0 ? final_wrapper_guest : ctx.r3.u32);
    REXLOG_INFO("VFS trace TextureParser C340 exit lr=0x{:08X} "
                "texture=0x{:08X} hash=0x{:08X} result=0x{:08X} wrapper=0x{:08X} "
                "provider={} state116=0x{:02X} bytes_pair=0x{:08X} "
                "bytes_pair_words={} descriptor=0x{:08X} descriptor_words={} "
                "primary_words={} secondary_words={} texture_words={} "
                "wrapper_words={}",
                original_ctx.lr,
                texture_guest,
                texture_hash,
                ctx.r3.u32,
                final_wrapper_guest,
                refii::vfs::NormalizeLooseAssetKey(provider_for_log),
                texture_guest == 0
                    ? 0
                    : refii::vfs::LoadGuestU8(base, texture_guest + 116),
                bytes_pair_guest,
                refii::vfs::FormatGuestWords(base, bytes_pair_guest, 4),
                descriptor_guest,
                refii::vfs::FormatGuestWords(base, descriptor_guest, 8),
                refii::vfs::FormatGuestWords(
                    base,
                    refii::vfs::LoadGuestU32(base, descriptor_guest + 12),
                    4),
                refii::vfs::FormatGuestWords(
                    base,
                    refii::vfs::LoadGuestU32(base, descriptor_guest + 28),
                    4),
                refii::vfs::FormatGuestWords(base, texture_guest, 32),
                refii::vfs::FormatGuestWords(base, final_wrapper_guest, 12));
	    refii::vfs::RecordRefiiMainMenuBlockerEvent(
	        "texture_parser",
	        fmt::format("C340 lr=0x{:08X} provider={} texture=0x{:08X} "
                    "wrapper=0x{:08X} result=0x{:08X} state116=0x{:02X} "
                    "bytes={} descriptor={}",
                    original_ctx.lr,
                    refii::vfs::NormalizeLooseAssetKey(provider_for_log),
                    texture_guest,
                    final_wrapper_guest,
                    ctx.r3.u32,
                    texture_guest == 0
                        ? 0
                        : refii::vfs::LoadGuestU8(base, texture_guest + 116),
	                    refii::vfs::FormatGuestWords(base, bytes_pair_guest, 4),
	                    refii::vfs::FormatGuestWords(base, descriptor_guest, 8)));
	    refii::vfs::LogRefiiVfsChainVerdict(
	        "texture_parser",
	        static_cast<uint32_t>(original_ctx.lr),
	        provider_for_log,
	        provider_for_log,
	        provider_for_log,
	        "native_texture_parser_descriptor",
	        "C340_exit",
	        "TraceOnce",
	        ctx.r3.u32 == 0 ? "C340_rejection" : "D028_D170_or_TextureStreamBuild");
	  }
}

PPC_FUNC_IMPL(sub_82B8BA38) {
  PPC_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82B8BA38);

  const PPCContext original_ctx = ctx;
  const uint32_t out_guest = original_ctx.r3.u32;
  const uint32_t descriptor_guest = original_ctx.r4.u32;
  const bool trace_texture_parser =
      refii::vfs::ShouldTraceTextureParserCall(base,
                                                   original_ctx.lr,
                                                   descriptor_guest);
  if (trace_texture_parser) {
    REXLOG_INFO("VFS trace TextureParser BA38 enter lr=0x{:08X} "
                "out=0x{:08X} descriptor=0x{:08X} descriptor_words={} "
                "primary_words={} secondary_words={}",
                original_ctx.lr,
                out_guest,
                descriptor_guest,
                refii::vfs::FormatGuestWords(base, descriptor_guest, 8),
                refii::vfs::FormatTextureParserPrimaryWords(
                    base, descriptor_guest),
                refii::vfs::FormatTextureParserSecondaryWords(
                    base, descriptor_guest));
  }

  __imp__sub_82B8BA38(ctx, base);

  if (trace_texture_parser) {
    REXLOG_INFO("VFS trace TextureParser BA38 exit lr=0x{:08X} "
                "result=0x{:08X} out=0x{:08X} out_words={} "
                "descriptor=0x{:08X} descriptor_words={} primary_words={} "
                "secondary_words={}",
                original_ctx.lr,
                ctx.r3.u32,
                out_guest,
                refii::vfs::FormatGuestWords(base, out_guest, 24),
                descriptor_guest,
                refii::vfs::FormatGuestWords(base, descriptor_guest, 8),
                refii::vfs::FormatTextureParserPrimaryWords(
                    base, descriptor_guest),
                refii::vfs::FormatTextureParserSecondaryWords(
                    base, descriptor_guest));
    refii::vfs::RecordRefiiMainMenuBlockerEvent(
        "texture_parser",
        fmt::format("BA38 lr=0x{:08X} result=0x{:08X} out={}",
                    original_ctx.lr,
                    ctx.r3.u32,
                    refii::vfs::FormatGuestWords(base, out_guest, 12)));
  }
}

PPC_FUNC_IMPL(sub_82B8C158) {
  PPC_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82B8C158);

  const PPCContext original_ctx = ctx;
  const uint32_t target_guest = original_ctx.r3.u32;
  const uint32_t descriptor_guest = original_ctx.r4.u32;
  const uint32_t parsed_header_guest = original_ctx.r5.u32;
  const bool trace_texture_parser =
      refii::vfs::ShouldTraceTextureParserCall(base,
                                                   original_ctx.lr,
                                                   descriptor_guest);
  if (trace_texture_parser) {
    REXLOG_INFO("VFS trace TextureParser C158 enter lr=0x{:08X} "
                "target=0x{:08X} descriptor=0x{:08X} parsed=0x{:08X} "
                "mip_index={} descriptor_words={} parsed_words={} "
                "primary_words={} secondary_words={} target_words={}",
                original_ctx.lr,
                target_guest,
                descriptor_guest,
                parsed_header_guest,
                original_ctx.r6.u32,
                refii::vfs::FormatGuestWords(base, descriptor_guest, 8),
                refii::vfs::FormatGuestWords(base, parsed_header_guest, 24),
                refii::vfs::FormatTextureParserPrimaryWords(
                    base, descriptor_guest),
                refii::vfs::FormatTextureParserSecondaryWords(
                    base, descriptor_guest),
                refii::vfs::FormatGuestWords(base, target_guest, 12));
  }

  __imp__sub_82B8C158(ctx, base);

  if (trace_texture_parser) {
    REXLOG_INFO("VFS trace TextureParser C158 exit lr=0x{:08X} "
                "result=0x{:08X} target=0x{:08X} descriptor=0x{:08X} "
                "parsed=0x{:08X} descriptor_words={} parsed_words={} "
                "target_words={}",
                original_ctx.lr,
                ctx.r3.u32,
                target_guest,
                descriptor_guest,
                parsed_header_guest,
                refii::vfs::FormatGuestWords(base, descriptor_guest, 8),
                refii::vfs::FormatGuestWords(base, parsed_header_guest, 24),
                refii::vfs::FormatGuestWords(base, target_guest, 12));
    refii::vfs::RecordRefiiMainMenuBlockerEvent(
        "texture_parser",
        fmt::format("C158 lr=0x{:08X} result=0x{:08X} parsed={} target={}",
                    original_ctx.lr,
                    ctx.r3.u32,
                    refii::vfs::FormatGuestWords(
                        base, parsed_header_guest, 12),
                    refii::vfs::FormatGuestWords(base, target_guest, 8)));
  }
}

PPC_FUNC_IMPL(sub_82B8C5F0) {
  PPC_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82B8C5F0);

  const PPCContext original_ctx = ctx;
  const uint32_t target_guest = original_ctx.r3.u32;
  const uint32_t descriptor_guest = original_ctx.r4.u32;
  const uint32_t parsed_header_guest = original_ctx.r5.u32;
  const uint32_t cache_guest = original_ctx.r6.u32;
  const bool trace_texture_parser =
      refii::vfs::ShouldTraceTextureParserCall(base,
                                                   original_ctx.lr,
                                                   descriptor_guest);
  const refii::vfs::ScopedTextureParserTraceDescriptor parser_scope(
      trace_texture_parser, descriptor_guest);
  if (trace_texture_parser) {
    REXLOG_INFO("VFS trace TextureParser C5F0 enter lr=0x{:08X} "
                "target=0x{:08X} descriptor=0x{:08X} parsed=0x{:08X} "
                "cache=0x{:08X} descriptor_words={} parsed_words={} "
                "primary_words={} secondary_words={} target_words={} "
                "cache_words={} cdf8_context={}",
                original_ctx.lr,
                target_guest,
                descriptor_guest,
                parsed_header_guest,
                cache_guest,
                refii::vfs::FormatGuestWords(base, descriptor_guest, 8),
                refii::vfs::FormatGuestWords(base, parsed_header_guest, 24),
                refii::vfs::FormatTextureParserPrimaryWords(
                    base, descriptor_guest),
                refii::vfs::FormatTextureParserSecondaryWords(
                    base, descriptor_guest),
                refii::vfs::FormatGuestWords(base, target_guest, 12),
                refii::vfs::FormatGuestWords(base, cache_guest, 8),
                refii::vfs::FormatTextureParserCdf8Context(base));
  }

  __imp__sub_82B8C5F0(ctx, base);

  if (trace_texture_parser) {
    REXLOG_INFO("VFS trace TextureParser C5F0 exit lr=0x{:08X} "
                "result=0x{:08X} target=0x{:08X} descriptor=0x{:08X} "
                "parsed=0x{:08X} cache=0x{:08X} descriptor_words={} "
                "parsed_words={} target_words={} cache_words={} "
                "cdf8_context={}",
                original_ctx.lr,
                ctx.r3.u32,
                target_guest,
                descriptor_guest,
                parsed_header_guest,
                cache_guest,
                refii::vfs::FormatGuestWords(base, descriptor_guest, 8),
                refii::vfs::FormatGuestWords(base, parsed_header_guest, 24),
                refii::vfs::FormatGuestWords(base, target_guest, 12),
                refii::vfs::FormatGuestWords(base, cache_guest, 8),
                refii::vfs::FormatTextureParserCdf8Context(base));
    refii::vfs::RecordRefiiMainMenuBlockerEvent(
        "texture_parser",
        fmt::format("C5F0 lr=0x{:08X} result=0x{:08X} parsed={} target={}",
                    original_ctx.lr,
                    ctx.r3.u32,
                    refii::vfs::FormatGuestWords(
                        base, parsed_header_guest, 12),
                    refii::vfs::FormatGuestWords(base, target_guest, 8)));
  }
}

PPC_FUNC_IMPL(sub_82B8CAC0) {
  PPC_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82B8CAC0);

  const PPCContext original_ctx = ctx;
  const bool trace_texture_parser =
      refii::vfs::ShouldTraceTextureParserProbeCall(base, original_ctx);
  if (trace_texture_parser) {
    REXLOG_INFO("VFS trace TextureParser CAC0 enter lr=0x{:08X} "
                "r3=0x{:08X} r4=0x{:08X} r5=0x{:08X} r6=0x{:08X} "
                "r7=0x{:08X} r3_words={} r4_words={} r5_words={}",
                original_ctx.lr,
                original_ctx.r3.u32,
                original_ctx.r4.u32,
                original_ctx.r5.u32,
                original_ctx.r6.u32,
                original_ctx.r7.u32,
                refii::vfs::FormatGuestWords(base, original_ctx.r3.u32, 8),
                refii::vfs::FormatGuestWords(base, original_ctx.r4.u32, 8),
                refii::vfs::FormatGuestWords(base, original_ctx.r5.u32, 8));
  }

  __imp__sub_82B8CAC0(ctx, base);

  if (trace_texture_parser) {
    REXLOG_INFO("VFS trace TextureParser CAC0 exit lr=0x{:08X} "
                "result=0x{:08X} r3_in=0x{:08X} r4_in=0x{:08X} "
                "r3_words={} r4_words={}",
                original_ctx.lr,
                ctx.r3.u32,
                original_ctx.r3.u32,
                original_ctx.r4.u32,
                refii::vfs::FormatGuestWords(base, original_ctx.r3.u32, 8),
                refii::vfs::FormatGuestWords(base, original_ctx.r4.u32, 8));
    refii::vfs::RecordRefiiMainMenuBlockerEvent(
        "texture_parser",
        fmt::format("CAC0 lr=0x{:08X} result=0x{:08X} r3=0x{:08X} "
                    "r4=0x{:08X}",
                    original_ctx.lr,
                    ctx.r3.u32,
                    original_ctx.r3.u32,
                    original_ctx.r4.u32));
  }
}

PPC_FUNC_IMPL(sub_82176018) {
  PPC_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82176018);

  const PPCContext original_ctx = ctx;
  const bool trace_texture_parser =
      refii::vfs::ShouldTraceTextureParserDirectChainCall(base,
                                                              original_ctx);
  const uint32_t descriptor_guest = original_ctx.r3.u32;
  const uint32_t descriptor_vtable =
      base == nullptr || descriptor_guest < 0x10000
          ? 0
          : refii::vfs::LoadGuestU32(base, descriptor_guest);
  if (trace_texture_parser) {
    REXLOG_INFO("VFS trace TextureParser 82176018 enter lr=0x{:08X} "
                "descriptor=0x{:08X} requested=0x{:08X} "
                "descriptor_words={} descriptor_vtable=0x{:08X} "
                "descriptor_slots={} generated_shape=descriptor_seek_bounds "
                "dispatch={} cdf8_context={}",
                original_ctx.lr,
                descriptor_guest,
                original_ctx.r4.u32,
                refii::vfs::FormatGuestWordsIfPointer(
                    base, descriptor_guest, 8),
                descriptor_vtable,
                refii::vfs::FormatGuestWordsIfPointer(
                    base, descriptor_vtable, 4),
                refii::vfs::FormatTextureParserCdf8DispatchSummary(base),
                refii::vfs::FormatTextureParserCdf8Context(base));
  }

  __imp__sub_82176018(ctx, base);

  if (trace_texture_parser) {
    REXLOG_INFO("VFS trace TextureParser 82176018 exit lr=0x{:08X} "
                "result=0x{:08X} descriptor=0x{:08X} requested=0x{:08X} "
                "descriptor_words={} dispatch={} cdf8_context={}",
                original_ctx.lr,
                ctx.r3.u32,
                descriptor_guest,
                original_ctx.r4.u32,
                refii::vfs::FormatGuestWordsIfPointer(
                    base, descriptor_guest, 8),
                refii::vfs::FormatTextureParserCdf8DispatchSummary(base),
                refii::vfs::FormatTextureParserCdf8Context(base));
    refii::vfs::RecordRefiiMainMenuBlockerEvent(
        "texture_parser",
        fmt::format("82176018 lr=0x{:08X} result=0x{:08X} descriptor={} "
                    "requested=0x{:08X}",
                    original_ctx.lr,
                    ctx.r3.u32,
                    refii::vfs::FormatGuestWordsIfPointer(
                        base, descriptor_guest, 8),
                    original_ctx.r4.u32));
  }
}

PPC_FUNC_IMPL(sub_82B8CDF8) {
  PPC_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82B8CDF8);

  const PPCContext original_ctx = ctx;
  const uint32_t target_guest = original_ctx.r3.u32;
  const uint32_t descriptor_guest = original_ctx.r4.u32;
  const uint32_t parsed_header_guest = original_ctx.r5.u32;
  const uint32_t mip_index = original_ctx.r6.u32;
  const uint32_t cache_guest = original_ctx.r7.u32;
  const bool trace_texture_parser =
      refii::vfs::ShouldTraceTextureParserProbeCall(base, original_ctx);
  const refii::vfs::ScopedTextureParserTraceDescriptor parser_scope(
      trace_texture_parser, descriptor_guest);
  const refii::vfs::ScopedTextureParserCdf8Trace cdf8_scope(
      trace_texture_parser,
      target_guest,
      descriptor_guest,
      parsed_header_guest,
      mip_index,
      cache_guest,
      original_ctx.r8.u32);
  if (trace_texture_parser) {
    REXLOG_INFO("VFS trace TextureParser CDF8 enter lr=0x{:08X} "
                "target=0x{:08X} descriptor=0x{:08X} parsed=0x{:08X} "
                "mip_index={} cache=0x{:08X} r8=0x{:08X} "
                "descriptor_detail={} parsed_words={} target_words={} "
                "cache_words={} owner_words={} "
                "generated_chain=CE40:descriptor_slot8,CE78:B96220,"
                "CED8:parser_object_slot0 dispatch={} cdf8_context={}",
                original_ctx.lr,
                target_guest,
                descriptor_guest,
                parsed_header_guest,
                mip_index,
                cache_guest,
                original_ctx.r8.u32,
                refii::vfs::FormatTextureParserDescriptorSummary(
                    base, descriptor_guest),
                refii::vfs::FormatGuestWordsIfPointer(
                    base, parsed_header_guest, 24),
                refii::vfs::FormatGuestWordsIfPointer(
                    base, target_guest, 16),
                refii::vfs::FormatGuestWordsIfPointer(
                    base, cache_guest, 8),
                refii::vfs::FormatTextureParserDescriptorOwnerWords(
                    base, descriptor_guest),
                refii::vfs::FormatTextureParserCdf8DispatchSummary(base),
                refii::vfs::FormatTextureParserCdf8Context(base));
  }

  __imp__sub_82B8CDF8(ctx, base);

  if (trace_texture_parser) {
    REXLOG_INFO("VFS trace TextureParser CDF8 exit lr=0x{:08X} "
                "result=0x{:08X} target=0x{:08X} descriptor=0x{:08X} "
                "parsed=0x{:08X} cache=0x{:08X} descriptor_detail={} "
                "parsed_words={} target_words={} cache_words={} owner_words={} "
                "dispatch={} cdf8_context={}",
                original_ctx.lr,
                ctx.r3.u32,
                target_guest,
                descriptor_guest,
                parsed_header_guest,
                cache_guest,
                refii::vfs::FormatTextureParserDescriptorSummary(
                    base, descriptor_guest),
                refii::vfs::FormatGuestWordsIfPointer(
                    base, parsed_header_guest, 24),
                refii::vfs::FormatGuestWordsIfPointer(
                    base, target_guest, 16),
                refii::vfs::FormatGuestWordsIfPointer(
                    base, cache_guest, 8),
                refii::vfs::FormatTextureParserDescriptorOwnerWords(
                    base, descriptor_guest),
                refii::vfs::FormatTextureParserCdf8DispatchSummary(base),
                refii::vfs::FormatTextureParserCdf8Context(base));
    refii::vfs::RecordRefiiMainMenuBlockerEvent(
        "texture_parser",
        fmt::format("CDF8 lr=0x{:08X} result=0x{:08X} target=0x{:08X} "
                    "descriptor=0x{:08X} parsed={} descriptor_detail={}",
                    original_ctx.lr,
                    ctx.r3.u32,
                    target_guest,
                    descriptor_guest,
                    refii::vfs::FormatGuestWordsIfPointer(
                        base, parsed_header_guest, 12),
                    refii::vfs::FormatTextureParserDescriptorSummary(
                        base, descriptor_guest)));
    const refii::vfs::ActiveTextureParserCdf8TraceContext* active_cdf8 =
        refii::vfs::ActiveTextureParserCdf8Trace();
    const std::string provider =
        active_cdf8 == nullptr ? std::string{} : active_cdf8->provider;
    refii::vfs::LogRefiiVfsChainVerdict(
        "texture_parser",
        static_cast<uint32_t>(original_ctx.lr),
        provider,
        provider,
        provider,
        "native_texture_parser_cdf8",
        "CDF8_exit",
        "TraceOnce",
        ctx.r3.u32 == 0 ? "CDF8_rejection_or_no_stream"
                        : "TextureStreamBuild");
  }
}

PPC_FUNC_IMPL(sub_82B8CEE0) {
  PPC_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82B8CEE0);

  const PPCContext original_ctx = ctx;
  const bool trace_texture_parser =
      refii::vfs::ShouldTraceTextureParserProbeCall(base, original_ctx);
  if (trace_texture_parser) {
    REXLOG_INFO("VFS trace TextureParser CEE0 enter lr=0x{:08X} "
                "r3=0x{:08X} r4=0x{:08X} r5=0x{:08X} r6=0x{:08X} "
                "r7=0x{:08X} r3_words={} r4_words={} r5_words={}",
                original_ctx.lr,
                original_ctx.r3.u32,
                original_ctx.r4.u32,
                original_ctx.r5.u32,
                original_ctx.r6.u32,
                original_ctx.r7.u32,
                refii::vfs::FormatGuestWords(base, original_ctx.r3.u32, 8),
                refii::vfs::FormatGuestWords(base, original_ctx.r4.u32, 8),
                refii::vfs::FormatGuestWords(base, original_ctx.r5.u32, 8));
  }

  __imp__sub_82B8CEE0(ctx, base);

  if (trace_texture_parser) {
    REXLOG_INFO("VFS trace TextureParser CEE0 exit lr=0x{:08X} "
                "result=0x{:08X} r3_in=0x{:08X} r4_in=0x{:08X} "
                "r3_words={} r4_words={}",
                original_ctx.lr,
                ctx.r3.u32,
                original_ctx.r3.u32,
                original_ctx.r4.u32,
                refii::vfs::FormatGuestWords(base, original_ctx.r3.u32, 8),
                refii::vfs::FormatGuestWords(base, original_ctx.r4.u32, 8));
    refii::vfs::RecordRefiiMainMenuBlockerEvent(
        "texture_parser",
        fmt::format("CEE0 lr=0x{:08X} result=0x{:08X} r3=0x{:08X} "
                    "r4=0x{:08X}",
                    original_ctx.lr,
                    ctx.r3.u32,
                    original_ctx.r3.u32,
                    original_ctx.r4.u32));
    refii::vfs::LogRefiiVfsChainVerdict(
        "texture_parser",
        static_cast<uint32_t>(original_ctx.lr),
        "",
        "",
        "",
        "native_texture_create_parser",
        "CEE0_exit",
        "TraceOnce",
        ctx.r3.u32 == 0 ? "CEE0_failed"
                        : "texture_created_or_payload_branch");
  }
}

PPC_FUNC_IMPL(sub_82B8D028) {
  PPC_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82B8D028);

  const PPCContext original_ctx = ctx;
  const uint32_t descriptor_guest = original_ctx.r5.u32;
  const bool trace_texture_parser =
      refii::vfs::ShouldTraceTextureParserCall(base,
                                                   original_ctx.lr,
                                                   descriptor_guest);
  const refii::vfs::ScopedTextureParserTraceDescriptor parser_scope(
      trace_texture_parser, descriptor_guest);
  if (trace_texture_parser) {
    REXLOG_INFO("VFS trace TextureParser D028 enter lr=0x{:08X} "
                "r3=0x{:08X} r4=0x{:08X} descriptor=0x{:08X} "
                "r6=0x{:08X} cache=0x{:08X} descriptor_words={} "
                "primary_words={} secondary_words={} cache_words={}",
                original_ctx.lr,
                original_ctx.r3.u32,
                original_ctx.r4.u32,
                descriptor_guest,
                original_ctx.r6.u32,
                original_ctx.r7.u32,
                refii::vfs::FormatGuestWords(base, descriptor_guest, 8),
                refii::vfs::FormatTextureParserPrimaryWords(
                    base, descriptor_guest),
                refii::vfs::FormatTextureParserSecondaryWords(
                    base, descriptor_guest),
                refii::vfs::FormatGuestWords(base, original_ctx.r7.u32, 8));
  }

  __imp__sub_82B8D028(ctx, base);

  if (trace_texture_parser) {
    REXLOG_INFO("VFS trace TextureParser D028 exit lr=0x{:08X} "
                "result=0x{:08X} descriptor=0x{:08X} descriptor_words={} "
                "cache=0x{:08X} cache_words={}",
                original_ctx.lr,
                ctx.r3.u32,
                descriptor_guest,
                refii::vfs::FormatGuestWords(base, descriptor_guest, 8),
                original_ctx.r7.u32,
                refii::vfs::FormatGuestWords(base, original_ctx.r7.u32, 8));
    refii::vfs::RecordRefiiMainMenuBlockerEvent(
        "texture_parser",
        fmt::format("D028 lr=0x{:08X} result=0x{:08X} descriptor={}",
                    original_ctx.lr,
                    ctx.r3.u32,
                    refii::vfs::FormatGuestWords(base, descriptor_guest, 8)));
    refii::vfs::LogRefiiVfsChainVerdict(
        "texture_parser",
        static_cast<uint32_t>(original_ctx.lr),
        "",
        "",
        "",
        "native_texture_parser_dispatch",
        "D028_exit",
        "TraceOnce",
        ctx.r3.u32 == 0 ? "D028_rejection" : "CDF8_or_TextureStreamBuild");
  }
}

PPC_FUNC_IMPL(sub_82B8D170) {
  PPC_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82B8D170);

  const PPCContext original_ctx = ctx;
  const uint32_t descriptor_guest = original_ctx.r4.u32;
  const bool trace_texture_parser =
      refii::vfs::ShouldTraceTextureParserCall(base,
                                                   original_ctx.lr,
                                                   descriptor_guest);
  const refii::vfs::ScopedTextureParserTraceDescriptor parser_scope(
      trace_texture_parser, descriptor_guest);
  if (trace_texture_parser) {
    REXLOG_INFO("VFS trace TextureParser D170 enter lr=0x{:08X} "
                "r3=0x{:08X} descriptor=0x{:08X} cache=0x{:08X} "
                "descriptor_words={} primary_words={} secondary_words={} "
                "cache_words={}",
                original_ctx.lr,
                original_ctx.r3.u32,
                descriptor_guest,
                original_ctx.r5.u32,
                refii::vfs::FormatGuestWords(base, descriptor_guest, 8),
                refii::vfs::FormatTextureParserPrimaryWords(
                    base, descriptor_guest),
                refii::vfs::FormatTextureParserSecondaryWords(
                    base, descriptor_guest),
                refii::vfs::FormatGuestWords(base, original_ctx.r5.u32, 8));
  }

  __imp__sub_82B8D170(ctx, base);

  if (trace_texture_parser) {
    REXLOG_INFO("VFS trace TextureParser D170 exit lr=0x{:08X} "
                "result=0x{:08X} descriptor=0x{:08X} descriptor_words={} "
                "cache=0x{:08X} cache_words={}",
                original_ctx.lr,
                ctx.r3.u32,
                descriptor_guest,
                refii::vfs::FormatGuestWords(base, descriptor_guest, 8),
                original_ctx.r5.u32,
                refii::vfs::FormatGuestWords(base, original_ctx.r5.u32, 8));
    refii::vfs::RecordRefiiMainMenuBlockerEvent(
        "texture_parser",
        fmt::format("D170 lr=0x{:08X} result=0x{:08X} descriptor={}",
                    original_ctx.lr,
                    ctx.r3.u32,
                    refii::vfs::FormatGuestWords(base, descriptor_guest, 8)));
    refii::vfs::LogRefiiVfsChainVerdict(
        "texture_parser",
        static_cast<uint32_t>(original_ctx.lr),
        "",
        "",
        "",
        "native_texture_parser_dispatch",
        "D170_exit",
        "TraceOnce",
        ctx.r3.u32 == 0 ? "D170_rejection" : "CDF8_or_TextureStreamBuild");
  }
}

PPC_FUNC_IMPL(sub_82B96220) {
  PPC_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82B96220);

  const PPCContext original_ctx = ctx;
  const bool trace_texture_parser =
      refii::vfs::ShouldTraceTextureParserDirectChainCall(base,
                                                              original_ctx);
  const uint32_t singleton_before =
      base == nullptr
          ? 0
          : refii::vfs::LoadGuestU32(
                base, refii::vfs::kTextureParserSingletonObjectGlobal);
  if (trace_texture_parser) {
    REXLOG_INFO("VFS trace TextureParser B96220 enter lr=0x{:08X} "
                "r3=0x{:08X} r4=0x{:08X} r5=0x{:08X} r6=0x{:08X} "
                "r7=0x{:08X} r8=0x{:08X} singleton_global=0x{:08X} "
                "singleton_before=0x{:08X} singleton_words={} "
                "singleton_vtable={} dispatch={} cdf8_context={}",
                original_ctx.lr,
                original_ctx.r3.u32,
                original_ctx.r4.u32,
                original_ctx.r5.u32,
                original_ctx.r6.u32,
                original_ctx.r7.u32,
                original_ctx.r8.u32,
                refii::vfs::kTextureParserSingletonObjectGlobal,
                singleton_before,
                refii::vfs::FormatGuestWordsIfPointer(
                    base, singleton_before, 12),
                refii::vfs::FormatGuestObjectVTableSlots(
                    base, singleton_before, 8),
                refii::vfs::FormatTextureParserCdf8DispatchSummary(base),
                refii::vfs::FormatTextureParserCdf8Context(base));
  }

  __imp__sub_82B96220(ctx, base);

  if (trace_texture_parser) {
    const uint32_t singleton_after =
        base == nullptr
            ? 0
            : refii::vfs::LoadGuestU32(
                  base, refii::vfs::kTextureParserSingletonObjectGlobal);
    refii::vfs::RecordTextureParserCdf8ParserObject(base, ctx.r3.u32);
    REXLOG_INFO("VFS trace TextureParser B96220 exit lr=0x{:08X} "
                "result=0x{:08X} singleton_before=0x{:08X} "
                "singleton_after=0x{:08X} singleton_words={} "
                "singleton_vtable={} object_words={} object_vtable={} "
                "final_slot0=0x{:08X} dispatch={} cdf8_context={}",
                original_ctx.lr,
                ctx.r3.u32,
                singleton_before,
                singleton_after,
                refii::vfs::FormatGuestWordsIfPointer(
                    base, singleton_after, 12),
                refii::vfs::FormatGuestObjectVTableSlots(
                    base, singleton_after, 8),
                refii::vfs::FormatGuestWordsIfPointer(base, ctx.r3.u32, 12),
                refii::vfs::FormatGuestObjectVTableSlots(
                    base, ctx.r3.u32, 8),
                refii::vfs::ActiveTextureParserCdf8Trace() == nullptr
                    ? 0
                    : refii::vfs::ActiveTextureParserCdf8Trace()
                          ->parser_object_slot0_guest,
                refii::vfs::FormatTextureParserCdf8DispatchSummary(base),
                refii::vfs::FormatTextureParserCdf8Context(base));
    refii::vfs::RecordRefiiMainMenuBlockerEvent(
        "texture_parser",
        fmt::format("B96220 lr=0x{:08X} result=0x{:08X} object_vtable={} "
                    "singleton_before=0x{:08X} singleton_after=0x{:08X} "
                    "slot0=0x{:08X}",
                    original_ctx.lr,
                    ctx.r3.u32,
                    refii::vfs::FormatGuestObjectVTableSlots(
                        base, ctx.r3.u32, 8),
                    singleton_before,
                    singleton_after,
                    refii::vfs::ActiveTextureParserCdf8Trace() == nullptr
                        ? 0
                        : refii::vfs::ActiveTextureParserCdf8Trace()
                              ->parser_object_slot0_guest));
  }
}

#define REFII_TEXTURE_PARSER_PROBE_HOOK(function_name, imp_name, label_name) \
  PPC_FUNC_IMPL(function_name) {                                             \
    PPC_FUNC_PROLOGUE();                                                     \
    REFII_VFS_NATIVE_PASSTHROUGH(function_name);                             \
                                                                             \
    const PPCContext original_ctx = ctx;                                      \
    const bool trace_texture_parser =                                         \
        refii::vfs::ShouldTraceTextureParserProbeCall(base, original_ctx);\
    if (trace_texture_parser) {                                              \
      refii::vfs::LogTextureParserProbeEnter(                            \
          base, original_ctx, label_name);                                    \
    }                                                                        \
                                                                             \
    imp_name(ctx, base);                                                     \
                                                                             \
    if (trace_texture_parser) {                                              \
      refii::vfs::LogTextureParserProbeExit(                             \
          base, original_ctx, ctx, label_name);                               \
    }                                                                        \
  }

REFII_TEXTURE_PARSER_PROBE_HOOK(sub_82B8C4A8, __imp__sub_82B8C4A8, "C4A8")
REFII_TEXTURE_PARSER_PROBE_HOOK(sub_82B8D280, __imp__sub_82B8D280, "D280")
REFII_TEXTURE_PARSER_PROBE_HOOK(sub_82B8D2D0, __imp__sub_82B8D2D0, "D2D0")
REFII_TEXTURE_PARSER_PROBE_HOOK(sub_82B8D348, __imp__sub_82B8D348, "D348")
REFII_TEXTURE_PARSER_PROBE_HOOK(sub_82B8D3D8, __imp__sub_82B8D3D8, "D3D8")
REFII_TEXTURE_PARSER_PROBE_HOOK(sub_82B8D468, __imp__sub_82B8D468, "D468")
REFII_TEXTURE_PARSER_PROBE_HOOK(sub_82B8D500, __imp__sub_82B8D500, "D500")
REFII_TEXTURE_PARSER_PROBE_HOOK(sub_82B8D718, __imp__sub_82B8D718, "D718")
REFII_TEXTURE_PARSER_PROBE_HOOK(sub_82B8D870, __imp__sub_82B8D870, "D870")
REFII_TEXTURE_PARSER_PROBE_HOOK(sub_82B8D8C8, __imp__sub_82B8D8C8, "D8C8")
REFII_TEXTURE_PARSER_PROBE_HOOK(sub_82B8DC78, __imp__sub_82B8DC78, "DC78")
REFII_TEXTURE_PARSER_PROBE_HOOK(sub_82B8DCE0, __imp__sub_82B8DCE0, "DCE0")
REFII_TEXTURE_PARSER_PROBE_HOOK(sub_82B8DDB0, __imp__sub_82B8DDB0, "DDB0")
REFII_TEXTURE_PARSER_PROBE_HOOK(sub_82B8DE40, __imp__sub_82B8DE40, "DE40")
REFII_TEXTURE_PARSER_PROBE_HOOK(sub_82B8DF50, __imp__sub_82B8DF50, "DF50")
REFII_TEXTURE_PARSER_PROBE_HOOK(sub_82B8DFA0, __imp__sub_82B8DFA0, "DFA0")

#undef REFII_TEXTURE_PARSER_PROBE_HOOK

PPC_FUNC_IMPL(sub_82B504D8) {
  PPC_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(sub_82B504D8);

  const PPCContext original_ctx = ctx;
  const uint32_t out_guest = ctx.r3.u32;
  const uint32_t texture_set_guest = ctx.r4.u32;
  const uint32_t texture_index = ctx.r5.u32;
  const bool trace_texture_build =
      refii::vfs::VfsTracingEnabled() &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "TextureStreamBuildEnter", 32);
  if (trace_texture_build) {
    const uint32_t provider_begin =
        texture_set_guest == 0
            ? 0
            : refii::vfs::LoadGuestU32(base, texture_set_guest + 12);
    const uint32_t provider_end =
        texture_set_guest == 0
            ? 0
            : refii::vfs::LoadGuestU32(base, texture_set_guest + 16);
    const uint32_t entry_begin =
        texture_set_guest == 0
            ? 0
            : refii::vfs::LoadGuestU32(base, texture_set_guest + 28);
    const uint32_t entry_end =
        texture_set_guest == 0
            ? 0
            : refii::vfs::LoadGuestU32(base, texture_set_guest + 32);
    std::string providers;
    if (provider_begin != 0 && provider_end >= provider_begin) {
      const uint32_t provider_count =
          std::min<uint32_t>((provider_end - provider_begin) / 8, 6);
      for (uint32_t index = 0; index < provider_count; ++index) {
        const uint32_t provider_guest =
            refii::vfs::LoadGuestU32(base, provider_begin + index * 8);
        providers += fmt::format(
            " [#{} {}]",
            index,
            refii::vfs::DescribeGuestObject(
                base, provider_guest, 12, 12));
      }
    }
    uint32_t texture_entry_guest = 0;
    if (entry_begin != 0 && texture_index != 0) {
      texture_entry_guest = entry_begin + (texture_index - 1) * 20;
    }
    REXLOG_INFO("VFS trace TextureStreamBuild enter lr=0x{:08X} "
                "out=0x{:08X} texture_set=0x{:08X} index={} "
                "providers=0x{:08X}-0x{:08X} entries=0x{:08X}-0x{:08X} "
                "set_words={} texture_entry=0x{:08X} entry_words={}{}",
                original_ctx.lr,
                out_guest,
                texture_set_guest,
                texture_index,
                provider_begin,
                provider_end,
                entry_begin,
                entry_end,
                refii::vfs::FormatGuestWords(base, texture_set_guest, 12),
                texture_entry_guest,
                refii::vfs::FormatGuestWords(
                    base, texture_entry_guest, 8),
                providers);
  }
  __imp__sub_82B504D8(ctx, base);
  if (trace_texture_build) {
    const uint32_t stream_guest =
        refii::vfs::TryReadOutputPointer(base, out_guest);
    REXLOG_INFO("VFS trace TextureStreamBuild exit lr=0x{:08X} "
                "out=0x{:08X} stream={}",
                original_ctx.lr,
                out_guest,
                refii::vfs::DescribeRefiiHostOrCompositeStream(
                    base, stream_guest));
    refii::vfs::LogRefiiVfsChainVerdict(
        "texture_parser",
        static_cast<uint32_t>(original_ctx.lr),
        "",
        "",
        "",
        "native_texture_stream_build",
        "TextureStreamBuild",
        "TraceOnce",
        stream_guest == 0 ? "stream_build_failed" : "texture_stream_ready");
  }
  refii::vfs::LogTextureStreamBuildResult(
      base, original_ctx, out_guest, texture_set_guest, texture_index);
}
