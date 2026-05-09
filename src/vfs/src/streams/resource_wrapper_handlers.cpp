#include "vfs_hooks_private.h"

namespace refii::vfs {
inline namespace detail {

bool TryHandleRefiiResourceWrapperWait(PPCContext& ctx,
                                       uint8_t* base,
                                       uint32_t wrapper_guest,
                                       std::string_view api) {
  const uint32_t original_r3 = ctx.r3.u32;
  const uint32_t original_r4 = ctx.r4.u32;
  wrapper_guest = NormalizeRefiiResourceStreamWrapper(base, wrapper_guest);
  if (wrapper_guest == 0) {
    return false;
  }

  const bool status_only = api == "ResourceWrapper_Status";
  uint32_t inner_guest = LoadGuestU32(base, wrapper_guest + 16);
  uint32_t source_stream_guest = inner_guest;
  uint32_t requested_bytes = 0;
  uint32_t bytes_read = 0;
  uint32_t status_word = 0;
  uint32_t status_buffer_guest = 0;
  const bool is_request = FindRefiiReadRequestRecord(inner_guest) != nullptr;
  ctx.r3.s32 =
      status_only
          ? GetRefiiResourceWrapperStatusNonBlocking(base,
                                                     wrapper_guest,
                                                     &inner_guest,
                                                     &source_stream_guest,
                                                     &requested_bytes,
                                                     &bytes_read,
                                                     &status_word,
                                                     &status_buffer_guest)
          : (EnsureRefiiResourceWrapperMaterialized(ctx, base, wrapper_guest)
                 ? (is_request ? 1 : 0)
                 : -1);
  if (!status_only && ctx.r3.s32 > 0) {
    GetRefiiResourceWrapperStatusNonBlocking(base,
                                             wrapper_guest,
                                             &inner_guest,
                                             &source_stream_guest,
                                             &requested_bytes,
                                             &bytes_read,
                                             &status_word,
                                             &status_buffer_guest);
  }
  const bool log_wait =
      LooseAssetOverrideTracingEnabled() ||
      ShouldLogLooseStreamOperation(api, wrapper_guest, 16);
  RefiiResourceWrapperView view;
  const bool can_read_view = !status_only && ctx.r3.s32 > 0;
  if (can_read_view && (log_wait || VfsTracingEnabled())) {
    view = GetRefiiResourceWrapperView(ctx, base, wrapper_guest, api);
  } else if (status_only) {
    view.inner_guest = inner_guest;
    view.source_stream_guest = source_stream_guest;
    view.buffer_guest = status_buffer_guest != 0
                            ? status_buffer_guest
                            : LoadGuestU32(base, wrapper_guest + 12);
    view.size = bytes_read;
    view.provider_key = GetRefiiResourceWrapperProviderKey(
        base, wrapper_guest, source_stream_guest);
    view.view_offset = GetAnimationTocPayloadOffset(
        base, view.buffer_guest, view.size, view.provider_key);
    if (view.view_offset != 0 && view.view_offset < view.size) {
      view.buffer_guest += view.view_offset;
      view.size -= view.view_offset;
    }
  }
  const bool trace_menu_texture =
      VfsTracingEnabled() && IsResourceWrapperTextureTraceAsset(view.provider_key);
  const uint32_t owner_texture = FindRefiiTextureWrapperOwner(wrapper_guest);
  if (trace_menu_texture) {
      RecordRefiiMainMenuBlockerEvent(
          "wrapper_view",
          fmt::format("{} lr=0x{:08X} ctr=0x{:08X} r3=0x{:08X} r4=0x{:08X} "
                      "provider={} wrapper=0x{:08X} result={} "
                      "status_word=0x{:08X} buffer=0x{:08X} size={} requested={} read={} "
                      "offset={} owner=0x{:08X} words={} wrapper_words={} "
                      "owner_words={}",
                      api,
                      ctx.lr,
                      ctx.ctr.u32,
                      original_r3,
                      original_r4,
                      NormalizeLooseAssetKey(view.provider_key),
                      wrapper_guest,
                      ctx.r3.u32,
                      status_word,
                      view.buffer_guest,
                      view.size,
                      requested_bytes,
                      bytes_read,
                      view.view_offset,
                      owner_texture,
                      FormatGuestWords(base, view.buffer_guest, 4),
                      FormatGuestWords(base, wrapper_guest, 12),
                      FormatTextureOwnerWords(base, owner_texture)));
  }
  const bool trace_parser_boundary =
      VfsTracingEnabled() && IsParserBoundaryTextureWrapperCaller(ctx.lr) &&
      owner_texture != 0;
  if ((log_wait && VfsVerboseIoTracingEnabled()) || trace_menu_texture ||
      trace_parser_boundary) {
    REXLOG_INFO("Refii resource wrapper {} lr=0x{:08X} ctr=0x{:08X} "
                "r3=0x{:08X} r4=0x{:08X} wrapper=0x{:08X} result=0x{:08X} "
                "status_word=0x{:08X} inner=0x{:08X} source_stream=0x{:08X} "
                "buffer=0x{:08X} size={} requested={} read={} view_offset={} "
                "provider={} owner=0x{:08X} first_words={} wrapper_words={} "
                "owner_words={}",
                api,
                ctx.lr,
                ctx.ctr.u32,
                original_r3,
                original_r4,
                wrapper_guest,
                ctx.r3.u32,
                status_word,
                view.inner_guest,
                view.source_stream_guest,
                view.buffer_guest,
                view.size,
                requested_bytes,
                bytes_read,
                view.view_offset,
                view.provider_key,
                owner_texture,
                FormatGuestWords(base, view.buffer_guest, 4),
                FormatGuestWords(base, wrapper_guest, 12),
                FormatTextureOwnerWords(base, owner_texture));
  }
  if (status_only && trace_menu_texture && ctx.r3.u32 == 1 &&
      ShouldWarnCompletedMenuTextureStatus(wrapper_guest)) {
    REXLOG_WARN("Refii VFS completed menu texture status awaiting follow-up "
                "wrapper=0x{:08X} provider={} request=0x{:08X} "
                "buffer=0x{:08X} requested={} read={} status_word=0x{:08X} "
                "active_reads={} owner=0x{:08X} first_words={} "
                "wrapper_words={} owner_words={}",
                wrapper_guest,
                NormalizeLooseAssetKey(view.provider_key),
                inner_guest,
                view.buffer_guest,
                requested_bytes,
                bytes_read,
                status_word,
                RefiiReadRequestRecordCount(),
                owner_texture,
                FormatGuestWords(base, view.buffer_guest, 4),
                FormatGuestWords(base, wrapper_guest, 12),
                FormatTextureOwnerWords(base, owner_texture));
  }
  return true;
}

bool TryHandleRefiiResourceWrapperGetBytes(PPCContext& ctx,
                                           uint8_t* base,
                                           uint32_t out_guest,
                                           uint32_t wrapper_guest) {
  const uint32_t original_r3 = ctx.r3.u32;
  const uint32_t original_r4 = ctx.r4.u32;
  wrapper_guest = NormalizeRefiiResourceStreamWrapper(base, wrapper_guest);
  if (wrapper_guest == 0) {
    return false;
  }

  const RefiiResourceWrapperView view =
      GetRefiiResourceWrapperView(ctx, base, wrapper_guest, "GetBytes");
  const uint32_t buffer_guest = view.buffer_guest;
  const std::string out_before = FormatGuestWords(base, out_guest, 2);
  if (out_guest != 0) {
    StoreGuestU32(base, out_guest, buffer_guest);
    StoreGuestU32(base, out_guest + 4, 0);
  }
  const std::string out_after = FormatGuestWords(base, out_guest, 2);
  ctx.r3.u32 = out_guest;
  const uint32_t owner_texture = FindRefiiTextureWrapperOwner(wrapper_guest);
  const uint32_t owner_hash =
      owner_texture == 0 ? 0 : LoadGuestU32(base, owner_texture + 4);
  const bool trace_parser_boundary =
      VfsTracingEnabled() && IsParserBoundaryTextureWrapperCaller(ctx.lr) &&
      owner_texture != 0;
  if (trace_parser_boundary && IsMainMenuTextureResourceHash(owner_hash)) {
    RecordRefiiMainMenuBlockerEvent(
        "texture_parser",
        fmt::format("C340_GetBytes lr=0x{:08X} provider={} texture=0x{:08X} "
                    "hash=0x{:08X} wrapper=0x{:08X} buffer=0x{:08X} size={}",
                    ctx.lr,
                    NormalizeLooseAssetKey(view.provider_key),
                    owner_texture,
                    owner_hash,
                    wrapper_guest,
                    buffer_guest,
                    view.size));
    LogRefiiVfsChainVerdict("texture_parser",
                            static_cast<uint32_t>(ctx.lr),
                            view.provider_key,
                            view.provider_key,
                            view.provider_key,
                            "native_texture_parser_descriptor",
                            "C340_GetBytes",
                            "TraceOnce",
                            "C340_GetSize_or_D028_D170");
  }
  if (LooseAssetOverrideTracingEnabled() ||
      ShouldLogLooseStreamOperation("RefiiResourceWrapper_GetBytes",
                                    wrapper_guest,
                                    16) ||
      (VfsTracingEnabled() &&
       IsResourceWrapperTextureTraceAsset(view.provider_key)) ||
      trace_parser_boundary) {
    if (VfsTracingEnabled() &&
        IsResourceWrapperTextureTraceAsset(view.provider_key)) {
      RecordRefiiMainMenuBlockerEvent(
          "wrapper_view",
          fmt::format("GetBytes lr=0x{:08X} ctr=0x{:08X} "
                      "r3=0x{:08X} r4=0x{:08X} provider={} "
                      "wrapper=0x{:08X} out=0x{:08X} out_before={} "
                      "out_after={} buffer=0x{:08X} size={} offset={} "
                      "owner=0x{:08X} words={} wrapper_words={} owner_words={}",
                      ctx.lr,
                      ctx.ctr.u32,
                      original_r3,
                      original_r4,
                      NormalizeLooseAssetKey(view.provider_key),
                      wrapper_guest,
                      out_guest,
                      out_before,
                      out_after,
                      buffer_guest,
                      view.size,
                      view.view_offset,
                      owner_texture,
                      FormatGuestWords(base, buffer_guest, 4),
                      FormatGuestWords(base, wrapper_guest, 12),
                      FormatTextureOwnerWords(base, owner_texture)));
    }
    if (VfsVerboseIoTracingEnabled()) {
      REXLOG_INFO("Refii resource wrapper ResourceWrapper_GetBytes "
                  "lr=0x{:08X} ctr=0x{:08X} r3=0x{:08X} r4=0x{:08X} "
                  "wrapper=0x{:08X} out=0x{:08X} out_before={} out_after={} "
                  "inner=0x{:08X} source_stream=0x{:08X} "
                  "buffer=0x{:08X} view_size={} view_offset={} provider={} "
                  "owner=0x{:08X} first_words={} wrapper_words={} owner_words={}",
                  ctx.lr,
                  ctx.ctr.u32,
                  original_r3,
                  original_r4,
                  wrapper_guest,
                  out_guest,
                  out_before,
                  out_after,
                  view.inner_guest,
                  view.source_stream_guest,
                  buffer_guest,
                  view.size,
                  view.view_offset,
                  view.provider_key,
                  owner_texture,
                  FormatGuestWords(base, buffer_guest, 4),
                  FormatGuestWords(base, wrapper_guest, 12),
                  FormatTextureOwnerWords(base, owner_texture));
    }
  }
  return true;
}

bool TryHandleRefiiResourceWrapperGetSize(PPCContext& ctx,
                                          uint8_t* base,
                                          uint32_t wrapper_guest) {
  const uint32_t original_r3 = ctx.r3.u32;
  const uint32_t original_r4 = ctx.r4.u32;
  wrapper_guest = NormalizeRefiiResourceStreamWrapper(base, wrapper_guest);
  if (wrapper_guest == 0) {
    return false;
  }

  const RefiiResourceWrapperView view =
      GetRefiiResourceWrapperView(ctx, base, wrapper_guest, "GetSize");
  ctx.r3.u32 = view.size;
  const uint32_t owner_texture = FindRefiiTextureWrapperOwner(wrapper_guest);
  const uint32_t owner_hash =
      owner_texture == 0 ? 0 : LoadGuestU32(base, owner_texture + 4);
  const bool trace_parser_boundary =
      VfsTracingEnabled() && IsParserBoundaryTextureWrapperCaller(ctx.lr) &&
      owner_texture != 0;
  if (trace_parser_boundary && IsMainMenuTextureResourceHash(owner_hash)) {
    RecordRefiiMainMenuBlockerEvent(
        "texture_parser",
        fmt::format("C340_GetSize lr=0x{:08X} provider={} texture=0x{:08X} "
                    "hash=0x{:08X} wrapper=0x{:08X} size={}",
                    ctx.lr,
                    NormalizeLooseAssetKey(view.provider_key),
                    owner_texture,
                    owner_hash,
                    wrapper_guest,
                    view.size));
    LogRefiiVfsChainVerdict("texture_parser",
                            static_cast<uint32_t>(ctx.lr),
                            view.provider_key,
                            view.provider_key,
                            view.provider_key,
                            "native_texture_parser_descriptor",
                            "C340_GetSize",
                            "TraceOnce",
                            "D028_D170_or_TextureStreamBuild");
  }
  if (LooseAssetOverrideTracingEnabled() ||
      ShouldLogLooseStreamOperation("RefiiResourceWrapper_GetSize",
                                    wrapper_guest,
                                    16) ||
      (VfsTracingEnabled() &&
       IsResourceWrapperTextureTraceAsset(view.provider_key)) ||
      trace_parser_boundary) {
    if (VfsTracingEnabled() &&
        IsResourceWrapperTextureTraceAsset(view.provider_key)) {
      RecordRefiiMainMenuBlockerEvent(
          "wrapper_view",
          fmt::format("GetSize lr=0x{:08X} ctr=0x{:08X} "
                      "r3=0x{:08X} r4=0x{:08X} provider={} "
                      "wrapper=0x{:08X} size={} offset={} words={} "
                      "wrapper_words={} owner=0x{:08X} owner_words={}",
                      ctx.lr,
                      ctx.ctr.u32,
                      original_r3,
                      original_r4,
                      NormalizeLooseAssetKey(view.provider_key),
                      wrapper_guest,
                      ctx.r3.u32,
                      view.view_offset,
                      FormatGuestWords(base, view.buffer_guest, 4),
                      FormatGuestWords(base, wrapper_guest, 12),
                      owner_texture,
                      FormatTextureOwnerWords(base, owner_texture)));
    }
    if (VfsVerboseIoTracingEnabled()) {
      REXLOG_INFO("Refii resource wrapper ResourceWrapper_GetSize "
                  "lr=0x{:08X} ctr=0x{:08X} r3=0x{:08X} r4=0x{:08X} "
                  "wrapper=0x{:08X} inner=0x{:08X} source_stream=0x{:08X} "
                  "size={} view_offset={} provider={} owner=0x{:08X} "
                  "first_words={} wrapper_words={} owner_words={}",
                  ctx.lr,
                  ctx.ctr.u32,
                  original_r3,
                  original_r4,
                  wrapper_guest,
                  view.inner_guest,
                  view.source_stream_guest,
                  ctx.r3.u32,
                  view.view_offset,
                  view.provider_key,
                  owner_texture,
                  FormatGuestWords(base, view.buffer_guest, 4),
                  FormatGuestWords(base, wrapper_guest, 12),
                  FormatTextureOwnerWords(base, owner_texture));
    }
  }
  return true;
}

bool TryHandleRefiiHostDiskStreamRead(PPCContext& ctx,
                                      uint8_t* base,
                                      uint32_t out_guest,
                                      uint32_t stream_guest,
                                      uint32_t buffer_guest,
                                      uint64_t offset,
                                      uint32_t requested_bytes) {
  if (!IsRefiiHostDiskStream(base, stream_guest)) {
    return false;
  }

  const uint32_t request_guest = CreateDeferredReadRequest(
      ctx, base, stream_guest, buffer_guest, offset, requested_bytes, true);
  if (out_guest != 0) {
    StoreGuestU32(base, out_guest, request_guest);
  }
  ctx.r3.u32 = out_guest;
  RecordRefiiStartupVideoReadQueued(
      base, stream_guest, request_guest, requested_bytes);
  if (ShouldLogLooseStreamOperation("RefiiHostStream_Read", stream_guest, 16)) {
    REXLOG_INFO("Refii host loose stream async read queued stream=0x{:08X} out=0x{:08X} "
                "destination=0x{:08X} offset={} requested={} read={} "
                "result_object=0x{:08X}",
                stream_guest,
                out_guest,
                buffer_guest,
                offset,
                requested_bytes,
                0,
                request_guest);
  }
  if (ShouldTraceBikPpcShape(base, stream_guest, "BIKPpcSlot16", 12)) {
    REXLOG_INFO("VFS trace BIK PPC slot16 async read lr=0x{:08X} "
                "ctr=0x{:08X} out=0x{:08X} stream=0x{:08X} "
                "destination_pair=0x{:08X} offset={} requested={} "
                "request=0x{:08X} pair_words={} request_words={} "
                "stream_summary=[{}]",
                ctx.lr,
                ctx.ctr.u32,
                out_guest,
                stream_guest,
                buffer_guest,
                offset,
                requested_bytes,
                request_guest,
                FormatGuestWordsIfPointer(base, buffer_guest, 4),
                FormatGuestWordsIfPointer(base, request_guest, 16),
                FormatBikPpcStreamSummary(base, stream_guest));
  }
  return true;
}

bool TryHandleRefiiHostDiskStreamDirectRead(PPCContext& ctx,
                                            uint8_t* base,
                                            uint32_t stream_guest,
                                            uint32_t buffer_guest,
                                            uint64_t offset,
                                            uint32_t requested_bytes) {
  if (!IsRefiiHostDiskStream(base, stream_guest)) {
    return false;
  }

  const bool uses_stream_reader_destination =
      ctx.lr == 0x82B4E89C ||
      ctx.lr == 0x82C8EC80 ||
      ctx.lr == 0x82C6E950 ||
      ctx.lr == 0x82C6E9CC;
  const bool trace_bik_slot12 =
      ShouldTraceBikPpcShape(base, stream_guest, "BIKPpcSlot12", 16);
  const std::string destination_before =
      trace_bik_slot12 ? FormatGuestWordsIfPointer(base, buffer_guest, 4)
                       : std::string{};
  const uint32_t bytes_read =
      uses_stream_reader_destination
          ? ReadRefiiHostStreamToDestination(
                ctx, base, stream_guest, buffer_guest, offset, requested_bytes)
          : ReadRefiiHostStreamBytes(
                base, stream_guest, buffer_guest, offset, requested_bytes);
  ctx.r3.u32 = bytes_read == requested_bytes ? 0 : 0x80004005;
  if (ShouldLogLooseStreamOperation("RefiiHostStream_DirectRead",
                                    stream_guest,
                                    16)) {
    REXLOG_INFO("Refii host loose stream direct read stream=0x{:08X} "
                "buffer=0x{:08X} destination={} offset={} requested={} "
                "read={} status=0x{:08X} caller_lr=0x{:08X}",
                stream_guest,
                buffer_guest,
                uses_stream_reader_destination,
                offset,
                requested_bytes,
                bytes_read,
                ctx.r3.u32,
                ctx.lr);
  }
  if (trace_bik_slot12) {
    REXLOG_INFO("VFS trace BIK PPC slot12 direct read lr=0x{:08X} "
                "ctr=0x{:08X} stream=0x{:08X} destination_pair=0x{:08X} "
                "offset={} requested={} read={} status=0x{:08X} "
                "uses_destination_pair={} pair_before={} pair_after={} "
                "generated_shape=sub_82C6E8F0:r4_stack_pair_dst_zero "
                "stream_summary=[{}]",
                ctx.lr,
                ctx.ctr.u32,
                stream_guest,
                buffer_guest,
                offset,
                requested_bytes,
                bytes_read,
                ctx.r3.u32,
                uses_stream_reader_destination,
                destination_before,
                FormatGuestWordsIfPointer(base, buffer_guest, 4),
                FormatBikPpcStreamSummary(base, stream_guest));
  }
  if (trace_bik_slot12) {
    RecordRefiiStartupVideoReadComplete(
        base, stream_guest, requested_bytes, bytes_read);
  }
  return true;
}

bool TryHandleRefiiHostDiskStreamNoopSuccess(PPCContext& ctx,
                                             uint8_t* base,
                                             uint32_t stream_guest,
                                             std::string_view api) {
  if (!IsRefiiHostDiskStream(base, stream_guest) &&
      !IsCompositeStreamWithRefiiHostSegment(base, stream_guest)) {
    return false;
  }

  ctx.r3.u32 = 0;
  if (ShouldLogLooseStreamOperation(api, stream_guest, 8)) {
    REXLOG_INFO("Refii host loose stream no-op success {} stream=0x{:08X} "
                "r4=0x{:08X} r5=0x{:08X} caller_lr=0x{:08X}",
                api,
                stream_guest,
                ctx.r4.u32,
                ctx.r5.u32,
                ctx.lr);
  }
  return true;
}

bool TryHandleRefiiStreamReaderFlush(PPCContext& ctx,
                                     uint8_t* base,
                                     uint32_t reader_guest,
                                     std::string_view api) {
  if (!IsRefiiStreamReader(base, reader_guest)) {
    return false;
  }

  ctx.r3.u32 = 1;
  if (VfsVerboseIoTracingEnabled() &&
      ShouldLogLooseStreamOperation(api, reader_guest, 8)) {
    REXLOG_INFO("VFS trace {} refii reader flush success reader={}",
                api,
                DescribeGuestObject(base, reader_guest, 12, 12));
  }
  return true;
}

bool TryHandleRefiiStreamReaderRefill(PPCContext& ctx,
                                      uint8_t* base,
                                      uint32_t reader_guest,
                                      uint32_t page_index,
                                      uint64_t target_offset) {
  ZoneScopedN("RefiiVFS/StreamReaderRefill");
  ConfigureRefiiVfsTracyPlots();
  if (!IsRefiiStreamReader(base, reader_guest)) {
    return false;
  }

  const uint32_t source_stream_guest = LoadGuestU32(base, reader_guest + 8);
  const auto record = FindRefiiHostStreamRecord(source_stream_guest);
  const uint64_t stream_size =
      record != nullptr ? record->size : LoadGuestU64(base, source_stream_guest + 16);
  const uint32_t page_size = LoadGuestU32(base, reader_guest + 32);
  const uint32_t entry_guest =
      RefiiReaderPageEntry(base, reader_guest, page_index);
  if (entry_guest == 0 || page_size == 0) {
    ctx.r3.u32 = 0;
    return true;
  }

  const uint32_t buffer_guest = LoadGuestU32(base, entry_guest + 8);
  const uint64_t cached_offset = LoadGuestU64(base, entry_guest);
  const uint32_t cached_bytes = LoadGuestU32(base, entry_guest + 28);
  if (buffer_guest != 0 && cached_offset == target_offset &&
      cached_bytes != 0) {
    ctx.r3.u32 = 1;
    if (VfsVerboseIoTracingEnabled() &&
        ShouldLogLooseStreamOperation("StreamReaderRefillCached",
                                      reader_guest,
                                      16)) {
      REXLOG_INFO("VFS trace StreamReader refill refii cached reader=0x{:08X} "
                  "source=0x{:08X} page={} offset={} cached_bytes={} "
                  "entry=0x{:08X} buffer=0x{:08X}",
                  reader_guest,
                  source_stream_guest,
                  page_index,
                  target_offset,
                  cached_bytes,
                  entry_guest,
                  buffer_guest);
    }
    return true;
  }

  StoreGuestU64(base, entry_guest, target_offset);
  StoreGuestU32(base, entry_guest + 16, 0);
  StoreGuestU8(base, entry_guest + 20, 0);
  StoreGuestU32(base, entry_guest + 24, 0);
  StoreGuestU32(base, entry_guest + 28, 0);

  if (target_offset >= stream_size || buffer_guest == 0) {
    ctx.r3.u32 = 1;
    if (VfsVerboseIoTracingEnabled() &&
        ShouldLogLooseStreamOperation("StreamReaderRefillEof",
                                      reader_guest,
                                      16)) {
      REXLOG_INFO("VFS trace StreamReader refill refii eof reader=0x{:08X} "
                  "source=0x{:08X} page={} offset={} size={} entry=0x{:08X} "
                  "buffer=0x{:08X}",
                  reader_guest,
                  source_stream_guest,
                  page_index,
                  target_offset,
                  stream_size,
                  entry_guest,
                  buffer_guest);
    }
    return true;
  }

  const uint32_t requested = static_cast<uint32_t>(
      std::min<uint64_t>(page_size, stream_size - target_offset));
  ZoneValue(requested);
  TracyPlot("Refii VFS StreamReader Page Bytes",
            static_cast<int64_t>(requested));
  const uint32_t read = ReadRefiiHostStreamBytes(
      base, source_stream_guest, buffer_guest, target_offset, requested);
  StoreGuestU32(base, entry_guest + 28, read);

  ctx.r3.u32 = read == requested ? 1 : 0;
  if (VfsVerboseIoTracingEnabled() &&
      ShouldLogLooseStreamOperation("StreamReaderRefillSync",
                                    reader_guest,
                                    16)) {
    REXLOG_INFO("VFS trace StreamReader refill refii sync reader=0x{:08X} "
                "source=0x{:08X} page={} offset={} requested={} read={} "
                "entry=0x{:08X} buffer=0x{:08X} result={}",
                reader_guest,
                source_stream_guest,
                page_index,
                target_offset,
                requested,
                read,
                entry_guest,
                buffer_guest,
                ctx.r3.u32);
  }
  return true;
}

bool EnsureRefiiReadRequestCompleted(uint8_t* base,
                                     const std::shared_ptr<RefiiReadRequestRecord>& record,
                                     uint32_t record_guest,
                                     std::string_view api) {
  ZoneScopedN("RefiiVFS/EnsureReadRequestCompleted");
  if (record == nullptr) {
    return false;
  }
  {
    std::lock_guard<std::mutex> lock(record->mutex);
    if (record->completed) {
      return true;
    }
  }
  if (base == nullptr || record_guest == 0 || record->stream_guest == 0 ||
      record->buffer_guest == 0) {
    return false;
  }

  WaitForRefiiReadRequest(record);

  uint32_t read = 0;
  bool completed = false;
  bool attempted = false;
  std::chrono::steady_clock::time_point queued_at{};
  std::chrono::steady_clock::time_point started_at{};
  std::chrono::steady_clock::time_point completed_at{};
  {
    std::lock_guard<std::mutex> lock(record->mutex);
    read = record->bytes_read;
    completed = record->completed;
    attempted = record->attempted;
    queued_at = record->queued_at;
    started_at = record->started_at;
    completed_at = record->completed_at;
  }

  if (!attempted) {
    PPCContext fallback_ctx{};
    read = ReadRefiiHostStreamForReadRequest(fallback_ctx, base, record);
    CompleteRefiiReadRequest(base, record, record_guest, read);
    std::lock_guard<std::mutex> lock(record->mutex);
    completed = record->completed;
  }

  if (LooseAssetOverrideTracingEnabled() ||
      ShouldLogLooseStreamOperation(api, record_guest, 16)) {
    REXLOG_INFO("Refii read request completed {} request=0x{:08X} "
                "stream=0x{:08X} buffer=0x{:08X} offset={} requested={} "
                "read={} completed={}",
                api,
                record_guest,
                record->stream_guest,
                record->buffer_guest,
                record->offset,
                record->requested_bytes,
                read,
                completed);
  }
  if (completed) {
    const auto zero = std::chrono::steady_clock::time_point{};
    const int64_t queue_wait_ms =
        queued_at == zero || started_at == zero
            ? -1
            : std::chrono::duration_cast<std::chrono::milliseconds>(
                  started_at - queued_at)
                  .count();
    const int64_t read_ms =
        started_at == zero || completed_at == zero
            ? -1
            : std::chrono::duration_cast<std::chrono::milliseconds>(
                  completed_at - started_at)
                  .count();
    RecordRefiiStartupVideoReadComplete(
        base,
        record->stream_guest,
        record->requested_bytes,
        read,
        queue_wait_ms,
        read_ms);
    MaybeLogRefiiStartupVideoShapeVerdict(base, record->stream_guest, "ready");
  }
  return completed;
}

bool TryHandleRefiiReadRequestWait(PPCContext& ctx,
                                   uint8_t* base,
                                   uint32_t request_guest,
                                   std::string_view api) {
  uint32_t record_guest = 0;
  const auto record = FindRefiiReadRequestRecord(request_guest, &record_guest);
  if (record == nullptr) {
    return false;
  }

  ctx.r3.u32 = EnsureRefiiReadRequestCompleted(base, record, record_guest, api)
                   ? 1
                   : 0;
  uint32_t bytes_read = 0;
  bool completed = false;
  {
    std::lock_guard<std::mutex> lock(record->mutex);
    bytes_read = record->bytes_read;
    completed = record->completed;
  }
  if (LooseAssetOverrideTracingEnabled() ||
      ShouldLogLooseStreamOperation(api, record_guest, 16)) {
    REXLOG_INFO("Refii read request complete {} request=0x{:08X} "
                "record=0x{:08X} stream=0x{:08X} requested={} read={} "
                "completed={}",
                api,
                request_guest,
                record_guest,
                record->stream_guest,
                record->requested_bytes,
                bytes_read,
                completed);
  }
  return true;
}

bool TryHandleRefiiReadRequestAddRef(PPCContext& ctx,
                                     uint8_t* base,
                                     uint32_t request_guest) {
  uint32_t record_guest = 0;
  const auto record = FindRefiiReadRequestRecord(request_guest, &record_guest);
  if (record == nullptr || base == nullptr || record_guest == 0) {
    return false;
  }

  const uint32_t ref_count = LoadGuestU32(base, record_guest + 8) + 1;
  StoreGuestU32(base, record_guest + 8, ref_count);
  ctx.r3.u32 = ref_count;

  if (LooseAssetOverrideTracingEnabled() ||
      ShouldLogLooseStreamOperation("ReadRequest_AddRef", record_guest, 16)) {
    REXLOG_INFO("Refii read request addref request=0x{:08X} record=0x{:08X} "
                "refs={} stream=0x{:08X}",
                request_guest,
                record_guest,
                ref_count,
                record->stream_guest);
  }

  return true;
}

struct ReleasedRefiiHostStreamReference {
  uint32_t ref_before = 0;
  uint32_t ref_after = 0;
  bool active_after = false;
};

ReleasedRefiiHostStreamReference ReleaseRefiiReadRequestStreamReference(
    PPCContext& ctx,
    uint8_t* base,
    uint32_t stream_guest) {
  ReleasedRefiiHostStreamReference result;
  if (base == nullptr || stream_guest == 0) {
    return result;
  }

  if (FindRefiiHostStreamRecord(stream_guest) != nullptr) {
    result.ref_before = LoadGuestU32(base, stream_guest + 4);
  }
  ReleaseGuestObject(ctx, base, stream_guest);
  result.active_after = FindRefiiHostStreamRecord(stream_guest) != nullptr;
  if (result.active_after) {
    result.ref_after = LoadGuestU32(base, stream_guest + 4);
    if (result.ref_after == 0) {
      EraseRefiiHostStreamRecord(stream_guest);
      EraseLooseStreamRecord(stream_guest);
      result.active_after = false;
    }
  }
  return result;
}

bool TryHandleRefiiReadRequestRelease(PPCContext& ctx,
                                      uint8_t* base,
                                      uint32_t request_guest) {
  uint32_t record_guest = 0;
  const auto record = FindRefiiReadRequestRecord(request_guest, &record_guest);
  if (record == nullptr || base == nullptr || record_guest == 0) {
    return false;
  }

  const uint32_t old_ref_count = LoadGuestU32(base, record_guest + 8);
  const uint32_t ref_count = old_ref_count == 0 ? 0 : old_ref_count - 1;
  StoreGuestU32(base, record_guest + 8, ref_count);
  ctx.r3.u32 = ref_count;

  if (ref_count == 0) {
    WaitForRefiiReadRequest(record);
    EraseRefiiReadRequestRecord(record_guest);
    ReleaseRefiiReadRequestStreamReference(ctx, base, record->stream_guest);
  }

  if (LooseAssetOverrideTracingEnabled() ||
      ShouldLogLooseStreamOperation("ReadRequest_Release", record_guest, 16)) {
    REXLOG_INFO("Refii read request release request=0x{:08X} record=0x{:08X} "
                "refs={} stream=0x{:08X} active_requests={}",
                request_guest,
                record_guest,
                ref_count,
                record->stream_guest,
                RefiiReadRequestRecordCount());
  }

  return true;
}

bool RetireRefiiReadRequestAfterNativeRelease(PPCContext& ctx,
                                              uint8_t* base,
                                              uint32_t request_guest,
                                              std::string_view api) {
  uint32_t record_guest = 0;
  const auto record = FindRefiiReadRequestRecord(request_guest, &record_guest);
  if (record == nullptr || base == nullptr || record_guest == 0) {
    return false;
  }

  const uint32_t stream_guest = record->stream_guest;
  const uint32_t stream_ref_before =
      stream_guest != 0 && FindRefiiHostStreamRecord(stream_guest) != nullptr
          ? LoadGuestU32(base, stream_guest + 4)
          : 0;

  WaitForRefiiReadRequest(record);
  EraseRefiiReadRequestRecord(record_guest);
  const ReleasedRefiiHostStreamReference released =
      ReleaseRefiiReadRequestStreamReference(ctx, base, stream_guest);

  const bool stream_still_active =
      stream_guest != 0 && FindRefiiHostStreamRecord(stream_guest) != nullptr;
  const uint32_t stream_ref_after =
      stream_still_active ? LoadGuestU32(base, stream_guest + 4)
                          : released.ref_after;

  if (LooseAssetOverrideTracingEnabled() ||
      ShouldLogLooseStreamOperation("ReadRequest_NativeRelease",
                                    record_guest,
                                    16)) {
    REXLOG_INFO("Refii read request native release {} request=0x{:08X} "
                "record=0x{:08X} stream=0x{:08X} stream_ref_before={} "
                "stream_ref_after={} stream_active={} active_requests={}",
                api,
                request_guest,
                record_guest,
                stream_guest,
                stream_ref_before != 0 ? stream_ref_before
                                       : released.ref_before,
                stream_ref_after,
                stream_still_active,
                RefiiReadRequestRecordCount());
  }

  return true;
}

bool TryHandleRefiiResourceWrapperSlot36(PPCContext& ctx,
                                         uint8_t* base,
                                         uint32_t first_guest,
                                         uint32_t second_guest) {
  const uint32_t wrapper_from_second =
      NormalizeRefiiResourceStreamWrapper(base, second_guest);
  const uint32_t wrapper_from_first =
      NormalizeRefiiResourceStreamWrapper(base, first_guest);
  const uint32_t wrapper_guest =
      wrapper_from_second != 0 ? wrapper_from_second : wrapper_from_first;
  if (wrapper_guest == 0) {
    return false;
  }

  const uint32_t original_r3 = first_guest;
  const uint32_t original_r4 = second_guest;
  const bool materialized =
      EnsureRefiiResourceWrapperMaterialized(ctx, base, wrapper_guest);
  RefiiResourceWrapperView view;
  if (materialized) {
    view = GetRefiiResourceWrapperView(ctx, base, wrapper_guest, "Slot36");
  } else {
    view.inner_guest = LoadGuestU32(base, wrapper_guest + 16);
    view.source_stream_guest = view.inner_guest;
    view.provider_key =
        GetRefiiResourceWrapperProviderKey(base, wrapper_guest, view.inner_guest);
  }

  uint32_t requested_bytes = 0;
  uint32_t bytes_read = 0;
  uint32_t status_word = 0;
  uint32_t status_buffer_guest = 0;
  uint32_t inner_guest = 0;
  uint32_t source_stream_guest = 0;
  const int32_t status = GetRefiiResourceWrapperStatusNonBlocking(
      base,
      wrapper_guest,
      &inner_guest,
      &source_stream_guest,
      &requested_bytes,
      &bytes_read,
      &status_word,
      &status_buffer_guest);

  ctx.r3.u32 = original_r3;
  const bool trace_texture =
      VfsTracingEnabled() && IsMainMenuTextureTraceAsset(view.provider_key);
  if (trace_texture ||
      (VfsTracingEnabled() &&
       ShouldLogRepeatedVfsTraceOperation("ResourceWrapperSlot36", 32))) {
    REXLOG_INFO("Refii resource wrapper slot36 lr=0x{:08X} "
                "wrapper=0x{:08X} r3_in=0x{:08X} r4_in=0x{:08X} "
                "result=0x{:08X} status={} status_word=0x{:08X} "
                "inner=0x{:08X} source_stream=0x{:08X} buffer=0x{:08X} "
                "size={} requested={} read={} materialized={} provider={} "
                "first_words={} wrapper_words={}",
                ctx.lr,
                wrapper_guest,
                original_r3,
                original_r4,
                ctx.r3.u32,
                status,
                status_word,
                inner_guest,
                source_stream_guest,
                view.buffer_guest != 0 ? view.buffer_guest : status_buffer_guest,
                view.size,
                requested_bytes,
                bytes_read,
                materialized,
                NormalizeLooseAssetKey(view.provider_key),
                FormatGuestWords(base,
                                 view.buffer_guest != 0 ? view.buffer_guest
                                                        : status_buffer_guest,
                                 4),
                FormatGuestWords(base, wrapper_guest, 12));
  }
  if (trace_texture) {
    RecordRefiiMainMenuBlockerEvent(
        "wrapper_slot36",
        fmt::format("lr=0x{:08X} provider={} wrapper=0x{:08X} "
                    "r3=0x{:08X} r4=0x{:08X} status={} status_word=0x{:08X} "
                    "buffer=0x{:08X} size={} requested={} read={} words={}",
                    ctx.lr,
                    NormalizeLooseAssetKey(view.provider_key),
                    wrapper_guest,
                    original_r3,
                    original_r4,
                    status,
                    status_word,
                    view.buffer_guest != 0 ? view.buffer_guest
                                           : status_buffer_guest,
                    view.size,
                    requested_bytes,
                    bytes_read,
                    FormatGuestWords(base,
                                     view.buffer_guest != 0 ? view.buffer_guest
                                                            : status_buffer_guest,
                                     4)));
  }

  return true;
}

bool TryHandleRefiiResourceWrapperAddRef(PPCContext& ctx,
                                         uint8_t* base,
                                         uint32_t wrapper_guest,
                                         std::string_view api) {
  wrapper_guest = NormalizeRefiiResourceStreamWrapper(base, wrapper_guest);
  if (wrapper_guest == 0) {
    return false;
  }

  const uint32_t ref_count = LoadGuestU32(base, wrapper_guest + 8) + 1;
  StoreGuestU32(base, wrapper_guest + 8, ref_count);
  ctx.r3.u32 = ref_count;

  const uint32_t inner_guest = LoadGuestU32(base, wrapper_guest + 16);
  const std::string provider_key =
      GetRefiiResourceWrapperProviderKey(base, wrapper_guest, inner_guest);
  if (VfsVerboseIoTracingEnabled() &&
      (VfsTracingEnabled() ||
       ShouldLogLooseStreamOperation("ResourceWrapper_AddRef",
                                     wrapper_guest,
                                     16))) {
    REXLOG_INFO("Refii resource wrapper addref api={} wrapper=0x{:08X} "
                "refs={} inner=0x{:08X} provider={} wrapper_words={}",
                api,
                wrapper_guest,
                ref_count,
                inner_guest,
                NormalizeLooseAssetKey(provider_key),
                FormatGuestWords(base, wrapper_guest, 12));
  }
  return true;
}

bool TryHandleRefiiResourceWrapperRelease(PPCContext& ctx,
                                          uint8_t* base,
                                          uint32_t wrapper_guest,
                                          uint32_t flags,
                                          std::string_view api) {
  const uint32_t original_guest = wrapper_guest;
  wrapper_guest = NormalizeRefiiResourceStreamWrapper(base, wrapper_guest);
  if (wrapper_guest == 0) {
    return false;
  }

  const uint32_t old_wrapper_refs = LoadGuestU32(base, wrapper_guest + 8);
  const uint32_t next_wrapper_refs =
      old_wrapper_refs == 0 ? 0 : old_wrapper_refs - 1;
  StoreGuestU32(base, wrapper_guest + 8, next_wrapper_refs);
  ctx.r3.u32 = next_wrapper_refs;

  const uint32_t inner_for_log = LoadGuestU32(base, wrapper_guest + 16);
  const std::string provider_key =
      GetRefiiResourceWrapperProviderKey(base, wrapper_guest, inner_for_log);
  if (next_wrapper_refs != 0) {
    if (VfsVerboseIoTracingEnabled() &&
        (VfsTracingEnabled() ||
         ShouldLogLooseStreamOperation("ResourceWrapper_Release",
                                       wrapper_guest,
                                       16))) {
      REXLOG_INFO("Refii resource wrapper release api={} wrapper=0x{:08X} "
                  "old_refs={} refs={} flags=0x{:08X} inner=0x{:08X} "
                  "provider={} wrapper_words={}",
                  api,
                  wrapper_guest,
                  old_wrapper_refs,
                  next_wrapper_refs,
                  flags,
                  inner_for_log,
                  NormalizeLooseAssetKey(provider_key),
                  FormatGuestWords(base, wrapper_guest, 12));
    }
    return true;
  }

  ForgetRefiiTextureWrapperOwner(wrapper_guest);

  const uint32_t buffer_guest = LoadGuestU32(base, wrapper_guest + 12);
  const uint32_t request_guest = LoadGuestU32(base, wrapper_guest + 16);
  uint32_t record_guest = 0;
  const auto request = FindRefiiReadRequestRecord(request_guest, &record_guest);
  const uint32_t old_ref_count =
      record_guest == 0 ? 0 : LoadGuestU32(base, record_guest + 8);
  const bool last_request_ref = request != nullptr && old_ref_count <= 1;

  if (last_request_ref) {
    WaitForRefiiReadRequest(request);
  }

  if (last_request_ref && buffer_guest != 0 &&
      buffer_guest == request->buffer_guest) {
    CallLhFree(ctx, base, buffer_guest);
  }

  if (request != nullptr) {
    TryHandleRefiiReadRequestRelease(ctx, base, request_guest);
  } else if (IsRefiiHostDiskStream(base, request_guest)) {
    ReleaseGuestObject(ctx, base, request_guest);
  }

  EraseLooseStreamRecord(wrapper_guest);
  if (original_guest != wrapper_guest) {
    EraseLooseStreamRecord(original_guest);
  }

  StoreGuestU32(base, wrapper_guest + 0, 0);
  StoreGuestU32(base, wrapper_guest + 4, 0);
  StoreGuestU32(base, wrapper_guest + 12, 0);
  StoreGuestU32(base, wrapper_guest + 16, 0);
  StoreGuestU32(base, wrapper_guest + 20, 0);

  CallLhFree(ctx, base, wrapper_guest);

  ctx.r3.u32 = 0;
  if (VfsVerboseIoTracingEnabled()) {
    REXLOG_INFO("Refii resource wrapper destroyed api={} wrapper=0x{:08X} "
                "old_wrapper_refs={} flags=0x{:08X} provider={} "
                "request=0x{:08X} record=0x{:08X} old_request_refs={} "
                "buffer=0x{:08X} freed_buffer={} active_requests={}",
                api,
                wrapper_guest,
                old_wrapper_refs,
                flags,
                NormalizeLooseAssetKey(provider_key),
                request_guest,
                record_guest,
                old_ref_count,
                buffer_guest,
                last_request_ref && buffer_guest != 0,
                RefiiReadRequestRecordCount());
  }
  return true;
}

bool TryHandleRefiiResourceWrapperDtor(PPCContext& ctx,
                                       uint8_t* base,
                                       uint32_t wrapper_guest,
                                       uint32_t flags) {
  return TryHandleRefiiResourceWrapperRelease(
      ctx, base, wrapper_guest, flags, "ResourceWrapper_Release");
}

bool TryHandleRefiiReadRequestGetStatus(PPCContext& ctx,
                                        uint8_t* base,
                                        uint32_t request_guest,
                                        std::string_view api) {
  uint32_t record_guest = 0;
  const auto record = FindRefiiReadRequestRecord(request_guest, &record_guest);
  if (record == nullptr) {
    return false;
  }

  std::shared_future<void> async_read;
  {
    std::lock_guard<std::mutex> lock(record->mutex);
    async_read = record->async_read;
  }
  if (async_read.valid() &&
      async_read.wait_for(std::chrono::seconds(0)) == std::future_status::ready) {
    async_read.wait();
  }

  uint32_t bytes_read = 0;
  bool completed = false;
  {
    std::lock_guard<std::mutex> lock(record->mutex);
    bytes_read = record->bytes_read;
    completed = record->completed;
  }
  ctx.r3.u32 = completed ? 1 : 0;
  if (LooseAssetOverrideTracingEnabled() ||
      ShouldLogLooseStreamOperation(api, record_guest, 16)) {
    REXLOG_INFO("Refii read request status {} request=0x{:08X} "
                "record=0x{:08X} status={} requested={} read={}",
                api,
                request_guest,
                record_guest,
                ctx.r3.u32,
                record->requested_bytes,
                bytes_read);
  }
  return true;
}

bool TryHandleRefiiReadRequestGetBytes(PPCContext& ctx,
                                       uint8_t* base,
                                       uint32_t out_guest,
                                       uint32_t request_guest) {
  uint32_t record_guest = 0;
  const auto record = FindRefiiReadRequestRecord(request_guest, &record_guest);
  if (record == nullptr) {
    return false;
  }

  EnsureRefiiReadRequestCompleted(
      base, record, record_guest, "ReadRequest_GetBytes");
  uint32_t bytes_read = 0;
  {
    std::lock_guard<std::mutex> lock(record->mutex);
    bytes_read = record->bytes_read;
  }
  if (out_guest != 0) {
    StoreGuestU32(base, out_guest, bytes_read);
    StoreGuestU32(base, out_guest + 4, 0);
  }
  ctx.r3.u32 = out_guest;
  if (LooseAssetOverrideTracingEnabled() ||
      ShouldLogLooseStreamOperation("ReadRequest_GetBytes", record_guest, 16)) {
    REXLOG_INFO("Refii read request bytes request=0x{:08X} record=0x{:08X} "
                "out=0x{:08X} bytes={}",
                request_guest,
                record_guest,
                out_guest,
                bytes_read);
  }
  return true;
}

bool TryHandleRefiiReadRequestGetOffset(PPCContext& ctx,
                                        uint32_t request_guest) {
  uint32_t record_guest = 0;
  const auto record = FindRefiiReadRequestRecord(request_guest, &record_guest);
  if (record == nullptr) {
    return false;
  }

  ctx.r3.u32 = static_cast<uint32_t>(record->offset);
  if (LooseAssetOverrideTracingEnabled() ||
      ShouldLogLooseStreamOperation("ReadRequest_GetOffset", record_guest, 16)) {
    REXLOG_INFO("Refii read request offset request=0x{:08X} record=0x{:08X} "
                "offset={}",
                request_guest,
                record_guest,
                record->offset);
  }
  return true;
}

bool TryHandleRefiiReadRequestReturnSuccess(PPCContext& ctx,
                                            uint8_t* base,
                                            uint32_t request_guest,
                                            std::string_view api) {
  uint32_t record_guest = 0;
  const auto record = FindRefiiReadRequestRecord(request_guest, &record_guest);
  if (record == nullptr) {
    return false;
  }

  if (record_guest != 0) {
    StoreGuestU32(base, record_guest + 20, ctx.r4.u32);
  }
  ctx.r3.u32 = 0;
  if (LooseAssetOverrideTracingEnabled() ||
      ShouldLogLooseStreamOperation(api, record_guest, 16)) {
    REXLOG_INFO("Refii read request success {} request=0x{:08X} "
                "record=0x{:08X} stream=0x{:08X} r4=0x{:08X}",
                api,
                request_guest,
                record_guest,
                record->stream_guest,
                ctx.r4.u32);
  }
  return true;
}

bool TryHandleRefiiReadRequestSmartPtrAssign(PPCContext& ctx,
                                             uint8_t* base,
                                             uint32_t target_guest,
                                             uint32_t source_guest) {
  if (base == nullptr || target_guest == 0) {
    return false;
  }

  const uint32_t previous_guest = LoadGuestU32(base, target_guest);
  const uint32_t previous_wrapper =
      NormalizeRefiiResourceStreamWrapper(base, previous_guest);
  const uint32_t source_value =
      source_guest == 0 ? 0 : LoadGuestU32(base, source_guest);
  uint32_t new_wrapper =
      NormalizeRefiiResourceStreamWrapper(base, source_value);
  if (new_wrapper == 0 && source_guest != 0) {
    new_wrapper = NormalizeRefiiResourceStreamWrapper(base, source_guest);
  }

  if (new_wrapper != 0 || (source_value == 0 && previous_wrapper != 0)) {
    const uint32_t new_inner =
        new_wrapper == 0 ? 0 : LoadGuestU32(base, new_wrapper + 16);
    const uint32_t previous_inner =
        previous_wrapper == 0 ? 0 : LoadGuestU32(base, previous_wrapper + 16);
    const std::string new_provider =
        new_wrapper == 0
            ? std::string{}
            : GetRefiiResourceWrapperProviderKey(base, new_wrapper, new_inner);
    const std::string previous_provider =
        previous_wrapper == 0
            ? std::string{}
            : GetRefiiResourceWrapperProviderKey(
                  base, previous_wrapper, previous_inner);

    if (new_wrapper != 0 && new_wrapper != previous_wrapper) {
      PPCContext add_ref_ctx = ctx;
      TryHandleRefiiResourceWrapperAddRef(
          add_ref_ctx, base, new_wrapper, "ResourceWrapper_SmartPtrAssign");
    }

    if (previous_wrapper != 0 && previous_wrapper != new_wrapper) {
      PPCContext release_ctx = ctx;
      TryHandleRefiiResourceWrapperRelease(release_ctx,
                                           base,
                                           previous_wrapper,
                                           1,
                                           "ResourceWrapper_SmartPtrAssign");
    }

    StoreGuestU32(base, target_guest, new_wrapper);
    ctx.r3.u32 = target_guest;
    const uint32_t new_refs =
        new_wrapper == 0 ? 0 : LoadGuestU32(base, new_wrapper + 8);
    const uint32_t previous_refs =
        previous_wrapper == 0 || previous_wrapper != new_wrapper
            ? 0
            : LoadGuestU32(base, previous_wrapper + 8);

    if (VfsTracingEnabled() ||
        ShouldLogLooseStreamOperation("ResourceWrapper_SmartPtrAssign",
                                      new_wrapper != 0 ? new_wrapper
                                                       : previous_wrapper,
                                      16)) {
      REXLOG_INFO("Refii resource wrapper smartptr assign target=0x{:08X} "
                  "source=0x{:08X} source_value=0x{:08X} "
                  "wrapper=0x{:08X} previous=0x{:08X} provider={} "
                  "previous_provider={} refs={} previous_refs={}",
                  target_guest,
                  source_guest,
                  source_value,
                  new_wrapper,
                  previous_wrapper,
                  NormalizeLooseAssetKey(new_provider),
                  NormalizeLooseAssetKey(previous_provider),
                  new_refs,
                  previous_refs);
    }
    return true;
  }

  if (source_guest == 0) {
    return false;
  }

  const uint32_t request_guest = LoadGuestU32(base, source_guest);
  uint32_t record_guest = 0;
  const auto record = FindRefiiReadRequestRecord(request_guest, &record_guest);
  if (record == nullptr || record_guest == 0) {
    return false;
  }

  uint32_t data_buffer_guest = record->buffer_guest;
  if (record->uses_stream_destination) {
    data_buffer_guest = LoadGuestU32(base, record->buffer_guest);
  }
  const uint64_t read_end =
      static_cast<uint64_t>(data_buffer_guest) + record->requested_bytes;
  const bool target_overlaps_read_buffer =
      data_buffer_guest != 0 && record->requested_bytes != 0 &&
      target_guest >= data_buffer_guest &&
      static_cast<uint64_t>(target_guest) < read_end;
  if (target_overlaps_read_buffer) {
    // Some native paths reuse smart-pointer assignment helpers while the
    // destination points into an async read buffer. The final memory must be
    // asset bytes, so do not write a pointer into the buffer at all.
    ctx.r3.u32 = target_guest;

    if (LooseAssetOverrideTracingEnabled() ||
        ShouldLogLooseStreamOperation("ReadRequest_SmartPtrAssign",
                                      record_guest,
                                      16)) {
      REXLOG_INFO("Refii read request smartptr assign skipped overlapping "
                  "read buffer target=0x{:08X} source=0x{:08X} "
                  "request=0x{:08X} buffer=0x{:08X} requested={} "
                  "stream=0x{:08X}",
                  target_guest,
                  source_guest,
                  request_guest,
                  data_buffer_guest,
                  record->requested_bytes,
                  record->stream_guest);
    }
    return true;
  }

  PPCContext add_ref_ctx = ctx;
  if (!TryHandleRefiiReadRequestAddRef(add_ref_ctx, base, request_guest)) {
    return false;
  }

  if (previous_guest != 0) {
    PPCContext release_ctx = ctx;
    if (!TryHandleRefiiReadRequestRelease(release_ctx, base, previous_guest)) {
      ReleaseGuestObject(ctx, base, previous_guest);
    }
  }

  StoreGuestU32(base, target_guest, request_guest);
  ctx.r3.u32 = target_guest;

  if (LooseAssetOverrideTracingEnabled() ||
      ShouldLogLooseStreamOperation("ReadRequest_SmartPtrAssign",
                                    record_guest,
                                    16)) {
    REXLOG_INFO("Refii read request smartptr assign target=0x{:08X} "
                "source=0x{:08X} request=0x{:08X} previous=0x{:08X} "
                "stream=0x{:08X}",
                target_guest,
                source_guest,
                request_guest,
                previous_guest,
                record->stream_guest);
  }

  return true;
}

bool TryHandleRefiiHostCompositeStreamRead(PPCContext& ctx,
                                           uint8_t* base,
                                           uint32_t out_guest,
                                           uint32_t stream_guest,
                                           uint32_t buffer_guest,
                                           uint64_t offset,
                                           uint32_t requested_bytes) {
  if (base == nullptr || out_guest == 0 || stream_guest < 0x10000 ||
      stream_guest >= 0x90000000 || buffer_guest == 0) {
    return false;
  }

  const uint32_t entries_begin = LoadGuestU32(base, stream_guest + 12);
  const uint32_t entries_end = LoadGuestU32(base, stream_guest + 16);
  const uint64_t total_size = LoadGuestU64(base, stream_guest + 24);
  if (entries_begin == 0 || entries_end <= entries_begin ||
      ((entries_end - entries_begin) % 16) != 0 ||
      (entries_end - entries_begin) / 16 > 1024) {
    return false;
  }

  const uint32_t entry_count = (entries_end - entries_begin) / 16;
  for (uint32_t index = 0; index < entry_count; ++index) {
    const uint32_t segment_guest = LoadGuestU32(base, entries_begin + index * 16);
    if (!IsRefiiHostDiskStream(base, segment_guest)) {
      return false;
    }
  }

  if (static_cast<uint64_t>(requested_bytes) > total_size ||
      offset > total_size - requested_bytes) {
    StoreGuestU32(base, out_guest, 0);
    ctx.r3.u32 = out_guest;
    return true;
  }

  uint64_t segment_base = 0;
  uint64_t read_cursor = offset;
  uint32_t destination_guest = buffer_guest;
  uint32_t remaining = requested_bytes;
  for (uint32_t index = 0; index < entry_count && remaining != 0; ++index) {
    const uint32_t entry_guest = entries_begin + index * 16;
    const uint32_t segment_guest = LoadGuestU32(base, entry_guest);
    const uint64_t segment_size = LoadGuestU64(base, entry_guest + 8);
    if (read_cursor >= segment_base + segment_size) {
      segment_base += segment_size;
      continue;
    }

    const uint64_t segment_offset = read_cursor - segment_base;
    const uint32_t to_read = static_cast<uint32_t>(
        std::min<uint64_t>(segment_size - segment_offset, remaining));
    const uint32_t read = ReadRefiiHostStreamBytes(
        base, segment_guest, destination_guest, segment_offset, to_read);
    if (read != to_read) {
      return false;
    }

    destination_guest += to_read;
    read_cursor += to_read;
    remaining -= to_read;
    segment_base += segment_size;
  }

  if (remaining != 0) {
    return false;
  }

  const uint32_t request_guest = CreateCompletedReadRequest(
      ctx,
      base,
      stream_guest,
      buffer_guest,
      offset,
      requested_bytes,
      requested_bytes);
  if (request_guest == 0) {
    return false;
  }

  StoreGuestU32(base, out_guest, request_guest);
  ctx.r3.u32 = out_guest;
  if (ShouldLogLooseStreamOperation("RefiiCompositeStream_Read",
                                    stream_guest,
                                    16)) {
    REXLOG_INFO("Refii host loose composite read stream=0x{:08X} out=0x{:08X} "
                "buffer=0x{:08X} offset={} requested={} segments={} "
                "result_object=0x{:08X}",
                stream_guest,
                out_guest,
                buffer_guest,
                offset,
                requested_bytes,
                entry_count,
                request_guest);
  }
  return true;
}

bool TryHandleRefiiHostCompositeStreamDirectRead(PPCContext& ctx,
                                                 uint8_t* base,
                                                 uint32_t stream_guest,
                                                 uint32_t buffer_guest,
                                                 uint64_t offset,
                                                 uint32_t requested_bytes) {
  if (base == nullptr || stream_guest < 0x10000 ||
      stream_guest >= 0x90000000 || buffer_guest == 0) {
    return false;
  }

  const uint32_t entries_begin = LoadGuestU32(base, stream_guest + 12);
  const uint32_t entries_end = LoadGuestU32(base, stream_guest + 16);
  const uint64_t total_size = LoadGuestU64(base, stream_guest + 24);
  if (entries_begin == 0 || entries_end <= entries_begin ||
      ((entries_end - entries_begin) % 16) != 0 ||
      (entries_end - entries_begin) / 16 > 1024) {
    return false;
  }

  const uint32_t entry_count = (entries_end - entries_begin) / 16;
  for (uint32_t index = 0; index < entry_count; ++index) {
    const uint32_t segment_guest = LoadGuestU32(base, entries_begin + index * 16);
    if (!IsRefiiHostDiskStream(base, segment_guest)) {
      return false;
    }
  }

  if (static_cast<uint64_t>(requested_bytes) > total_size ||
      offset > total_size - requested_bytes) {
    ctx.r3.u32 = 0x80004005;
    return true;
  }

  uint64_t segment_base = 0;
  uint64_t read_cursor = offset;
  uint32_t destination_guest = buffer_guest;
  uint32_t remaining = requested_bytes;
  bool ok = true;
  for (uint32_t index = 0; index < entry_count && remaining != 0; ++index) {
    const uint32_t entry_guest = entries_begin + index * 16;
    const uint32_t segment_guest = LoadGuestU32(base, entry_guest);
    const uint64_t segment_size = LoadGuestU64(base, entry_guest + 8);
    if (read_cursor >= segment_base + segment_size) {
      segment_base += segment_size;
      continue;
    }

    const uint64_t segment_offset = read_cursor - segment_base;
    const uint32_t to_read = static_cast<uint32_t>(
        std::min<uint64_t>(segment_size - segment_offset, remaining));
    const uint32_t read = ReadRefiiHostStreamBytes(
        base, segment_guest, destination_guest, segment_offset, to_read);
    if (read != to_read) {
      ok = false;
      break;
    }

    destination_guest += to_read;
    read_cursor += to_read;
    remaining -= to_read;
    segment_base += segment_size;
  }

  if (remaining != 0) {
    ok = false;
  }

  ctx.r3.u32 = ok ? 0 : 0x80004005;
  if (ShouldLogLooseStreamOperation("RefiiCompositeStream_DirectRead",
                                    stream_guest,
                                    32)) {
    REXLOG_INFO("Refii host loose composite direct read stream=0x{:08X} "
                "buffer=0x{:08X} offset={} requested={} remaining={} "
                "segments={} status=0x{:08X}",
                stream_guest,
                buffer_guest,
                offset,
                requested_bytes,
                remaining,
                entry_count,
                ctx.r3.u32);
  }
  return true;
}


}  // namespace detail
}  // namespace refii::vfs
