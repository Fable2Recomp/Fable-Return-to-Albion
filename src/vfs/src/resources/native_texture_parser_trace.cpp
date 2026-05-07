#include "vfs_hooks_private.h"

namespace refii::vfs {
inline namespace detail {

void RecordRefiiPostLoadingAfterHolderRenderOwner(uint32_t owner_guest) {
  if (LooksLikeGuestPointer(owner_guest)) {
    RefiiPostLoadingAfterHolderRenderOwnerScratch() = owner_guest;
  }
}

uint32_t SelectRefiiPostLoadingAfterHolderRenderOwner() {
  const uint32_t scratch_owner =
      RefiiPostLoadingAfterHolderRenderOwnerScratch();
  if (LooksLikeGuestPointer(scratch_owner)) {
    return scratch_owner;
  }

  std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
  const auto& trace = RefiiPostVideoHandoffTraceState();
  if (LooksLikeGuestPointer(trace.last_nonidle_render_schedule_owner)) {
    return trace.last_nonidle_render_schedule_owner;
  }
  if (LooksLikeGuestPointer(trace.last_render_schedule_owner)) {
    return trace.last_render_schedule_owner;
  }
  return 0;
}

std::string FormatNativeVfsAsyncQueueState(uint8_t* base,
                                           uint32_t queue_guest) {
  if (!LooksLikeGuestPointer(queue_guest)) {
    return fmt::format("queue=0x{:08X}", queue_guest);
  }
  const uint32_t owner_from_work_queue =
      queue_guest >= 176 ? queue_guest - 176 : 0;
  const uint32_t owner_from_pending_queue =
      queue_guest >= 160 ? queue_guest - 160 : 0;
  const uint32_t owner =
      LooksLikeGuestPointer(owner_from_work_queue)
          ? owner_from_work_queue
          : (LooksLikeGuestPointer(owner_from_pending_queue)
                 ? owner_from_pending_queue
                 : 0);
  const uint32_t worker_wait_object =
      LooksLikeGuestPointer(owner) ? owner + 40 : 0;
  const uint32_t worker_wait_handle =
      LooksLikeGuestPointer(worker_wait_object)
          ? LoadGuestU32(base, worker_wait_object + 128)
          : 0;
  return fmt::format("queue=0x{:08X} queue_words={} owner=0x{:08X} "
                     "owner_words={} worker_wait=0x{:08X} "
                     "worker_wait_handle=0x{:08X} worker_wait_words={}",
                     queue_guest,
                     FormatGuestWordsIfPointer(base, queue_guest, 8),
                     owner,
                     FormatGuestWordsIfPointer(base, owner, 16),
                     worker_wait_object,
                     worker_wait_handle,
                     FormatGuestWordsIfPointer(base, worker_wait_object, 8));
}

std::string FormatNativeVfsWorkerOwnerState(uint8_t* base,
                                            uint32_t owner_guest) {
  if (!LooksLikeGuestPointer(owner_guest)) {
    return fmt::format("worker_owner=0x{:08X}", owner_guest);
  }
  const uint32_t shutdown_flag = LoadGuestU8(base, owner_guest + 172);
  const uint32_t signal_handle = LoadGuestU32(base, owner_guest + 148);
  return fmt::format("worker_owner=0x{:08X} shutdown172=0x{:02X} "
                     "signal148=0x{:08X} owner_words={} retry_tree={} "
                     "pending156=[{}] pending160=[{}] work176=[{}]",
                     owner_guest,
                     shutdown_flag,
                     signal_handle,
                     FormatGuestWordsIfPointer(base, owner_guest, 16),
                     FormatGuestWordsAtOffsetIfPointer(base, owner_guest, 28, 8),
                     FormatNativeVfsAsyncQueueState(base, owner_guest + 156),
                     FormatNativeVfsAsyncQueueState(base, owner_guest + 160),
                     FormatNativeVfsAsyncQueueState(base, owner_guest + 176));
}

std::string FormatNativeVfsRetryInsertState(uint8_t* base,
                                            uint32_t list_guest,
                                            uint32_t pair_high_guest,
                                            uint32_t pair_low_guest,
                                            uint32_t value_holder_guest) {
  const uint32_t pair_node =
      LooksLikeGuestPointer(pair_low_guest) ? pair_low_guest : 0;
  const uint32_t pair_prev =
      pair_node != 0 ? LoadGuestU32(base, pair_node + 4) : 0;
  const uint32_t value =
      LooksLikeGuestPointer(value_holder_guest)
          ? LoadGuestU32(base, value_holder_guest)
          : 0;
  return fmt::format("list=0x{:08X} list_words={} pair_high=0x{:08X} "
                     "pair_low=0x{:08X} pair_words={} pair_prev=0x{:08X} "
                     "value_holder=0x{:08X} value=[{}] request=[{}]",
                     list_guest,
                     FormatGuestWordsIfPointer(base, list_guest, 10),
                     pair_high_guest,
                     pair_low_guest,
                     FormatGuestWordsIfPointer(base, pair_low_guest, 10),
                     pair_prev,
                     value_holder_guest,
                     FormatGuestWordsIfPointer(base, value_holder_guest, 4),
                     FormatNativeVfsAsyncRequestState(base, value));
}

std::string FormatNativeVfsSchedulerListState(uint8_t* base,
                                              uint32_t list_guest) {
  if (!LooksLikeGuestPointer(list_guest)) {
    return fmt::format("list=0x{:08X}", list_guest);
  }

  const uint32_t head_or_begin = LoadGuestU32(base, list_guest + 0);
  const uint32_t tail_or_end = LoadGuestU32(base, list_guest + 4);
  const uint32_t third = LoadGuestU32(base, list_guest + 8);
  const uint32_t fourth = LoadGuestU32(base, list_guest + 12);
  return fmt::format("list=0x{:08X} words={} first=0x{:08X} "
                     "second=0x{:08X} third=0x{:08X} fourth=0x{:08X} "
                     "first_words={} first_request=[{}]",
                     list_guest,
                     FormatGuestWordsIfPointer(base, list_guest, 8),
                     head_or_begin,
                     tail_or_end,
                     third,
                     fourth,
                     FormatGuestWordsIfPointer(base, head_or_begin, 8),
                     FormatNativeVfsAsyncRequestState(
                         base,
                         LooksLikeGuestPointer(head_or_begin)
                             ? LoadGuestU32(base, head_or_begin + 8)
                             : 0));
}

std::string FormatNativeVfsSchedulerState(uint8_t* base,
                                          uint32_t scheduler_guest) {
  if (!LooksLikeGuestPointer(scheduler_guest)) {
    return fmt::format("scheduler=0x{:08X}", scheduler_guest);
  }

  return fmt::format("scheduler=0x{:08X} words={} active=[{}] "
                     "queued=[{}] pending48=[{}] pending60=[{}] "
                     "typed76=[{}]",
                     scheduler_guest,
                     FormatGuestWordsIfPointer(base, scheduler_guest, 24),
                     FormatNativeVfsSchedulerListState(base,
                                                       scheduler_guest + 12),
                     FormatNativeVfsSchedulerListState(base,
                                                       scheduler_guest + 24),
                     FormatNativeVfsSchedulerListState(base,
                                                       scheduler_guest + 48),
                     FormatNativeVfsSchedulerListState(base,
                                                       scheduler_guest + 60),
                     FormatNativeVfsSchedulerListState(base,
                                                       scheduler_guest + 76));
}

std::string FormatNativeVfsSchedulerVectorState(uint8_t* base,
                                                uint32_t vector_guest) {
  if (!LooksLikeGuestPointer(vector_guest)) {
    return fmt::format("vector=0x{:08X}", vector_guest);
  }
  const uint32_t begin = LoadGuestU32(base, vector_guest + 4);
  const uint32_t end = LoadGuestU32(base, vector_guest + 8);
  const uint32_t count =
      (LooksLikeGuestPointer(begin) && end >= begin) ? (end - begin) / 4 : 0;
  std::string entries;
  const uint32_t entry_count = std::min<uint32_t>(count, 8);
  for (uint32_t index = 0; index < entry_count; ++index) {
    const uint32_t request_guest = LoadGuestU32(base, begin + index * 4);
    entries += fmt::format(" [#{} {}]",
                           index,
                           FormatNativeVfsAsyncRequestState(base,
                                                            request_guest));
  }
  return fmt::format("vector=0x{:08X} words={} begin=0x{:08X} "
                     "end=0x{:08X} count={}{}",
                     vector_guest,
                     FormatGuestWordsIfPointer(base, vector_guest, 6),
                     begin,
                     end,
                     count,
                     entries);
}

std::string FormatGeneratedEventWaitState(uint8_t* base,
                                          uint32_t wait_object_guest) {
  const uint32_t wait_handle =
      wait_object_guest != 0 ? LoadGuestU32(base, wait_object_guest + 128) : 0;
  const uint32_t handoff_candidate =
      wait_object_guest >= 460 ? wait_object_guest - 460 : 0;
  const uint32_t signal_handle =
      handoff_candidate != 0 ? LoadGuestU32(base, handoff_candidate + 456) : 0;
  const uint32_t worker_owner =
      wait_object_guest >= 40 ? wait_object_guest - 40 : 0;
  const uint32_t worker_owner_vtable =
      LooksLikeGuestPointer(worker_owner) ? LoadGuestU32(base, worker_owner) : 0;
  const bool worker_owner_valid =
      LooksLikeGuestPointer(worker_owner_vtable);
  const uint32_t finalizer_worker =
      wait_object_guest >= 20 ? wait_object_guest - 20 : 0;
  const uint32_t finalizer_queue =
      LooksLikeGuestPointer(finalizer_worker) ? finalizer_worker + 160 : 0;
  const uint32_t finalizer_signal =
      LooksLikeGuestPointer(finalizer_worker)
          ? LoadGuestU32(base, finalizer_worker + 148)
          : 0;
  const uint32_t finalizer_exit =
      LooksLikeGuestPointer(finalizer_worker)
          ? LoadGuestU8(base, finalizer_worker + 152)
          : 0;
  return fmt::format("wait_object=0x{:08X} wait_words={} "
                     "wait_handle=0x{:08X} handoff_candidate=0x{:08X} "
                     "signal_handle=0x{:08X} handoff_words={} "
                     "native_worker=[{}] finalizer_worker=[worker=0x{:08X} "
                     "signal148=0x{:08X} exit152=0x{:02X} queue160=[{}]]",
                     wait_object_guest,
                     FormatGuestWordsIfPointer(base, wait_object_guest, 10),
                     wait_handle,
                     handoff_candidate,
                     signal_handle,
                     FormatGuestWordsIfPointer(base, handoff_candidate, 12),
                     worker_owner_valid
                         ? FormatNativeVfsWorkerOwnerState(base, worker_owner)
                         : "not_worker",
                     finalizer_worker,
                     finalizer_signal,
                     finalizer_exit,
                     FormatNativeVfsAsyncQueueState(base, finalizer_queue));
}

std::string FormatGeneratedEventSetState(uint8_t* base,
                                         uint32_t event_handle_guest) {
  return fmt::format("event_handle=0x{:08X} handle_words={} "
                     "handoff=[{}] main_menu=[{}]",
                     event_handle_guest,
                     FormatGuestWordsIfPointer(base, event_handle_guest, 6),
                     FormatRefiiPostVideoHandoffTraceForLog(),
                     FormatRefiiMainMenuBlockerTraceForLog());
}

std::string FormatPostVideoFrameTeardownState(uint8_t* base,
                                              uint32_t object_guest) {
  const uint32_t owner_candidate =
      object_guest >= 12 ? object_guest - 12 : 0;
  return fmt::format("object=0x{:08X} object_words={} object_vtable={} "
                     "owner_candidate=0x{:08X} owner_words={}",
                     object_guest,
                     FormatGuestWordsIfPointer(base, object_guest, 10),
                     FormatGuestObjectVTableSlots(base, object_guest, 8),
                     owner_candidate,
                     FormatGuestWordsIfPointer(base, owner_candidate, 12));
}

bool ShouldTraceBikObjectVirtualProbe(uint8_t* base,
                                      uint32_t object_guest,
                                      std::string_view api,
                                      uint32_t limit = 8) {
  const uint32_t stream_guest =
      object_guest != 0 ? LoadGuestU32(base, object_guest + 8) : 0;
  if (ShouldTraceBikPpcShape(base, stream_guest, api, limit)) {
    return true;
  }

  return kTraceNonVfsBootFrameProbes && VfsTracingEnabled() &&
         RefiiIsTrackedStartupVideoObject(object_guest) &&
         ShouldLogRepeatedVfsTraceOperation(api, limit);
}

std::string FormatBikObjectVirtualProbe(uint8_t* base,
                                        uint32_t object_guest) {
  const uint32_t stream_guest =
      object_guest != 0 ? LoadGuestU32(base, object_guest + 8) : 0;
  return fmt::format("object=0x{:08X} stream=0x{:08X} object_words={} "
                     "object_vtable={} stream_summary=[{}] handoff=[{}] "
                     "main_menu=[{}]",
                     object_guest,
                     stream_guest,
                     FormatGuestWordsIfPointer(base, object_guest, 12),
                     FormatGuestObjectVTableSlots(base, object_guest, 12),
                     FormatBikPpcStreamSummary(base, stream_guest),
                     FormatRefiiPostVideoHandoffTraceForLog(),
                     FormatRefiiMainMenuBlockerTraceForLog());
}

std::string ReadStartupBikRequestName(uint8_t* base, uint32_t name_guest) {
  std::string name = ReadGuestWideStringObject(base, name_guest, 512);
  if (name.empty()) {
    name = ReadGuestWideCString(base, name_guest, 512);
  }
  return name;
}

bool ShouldTraceBikConsumerHandoffProbe(uint8_t* base,
                                        uint32_t object_guest,
                                        uint32_t stream_guest,
                                        std::string_view requested_name,
                                        std::string_view api,
                                        uint32_t limit = 8) {
  if (!kTraceBikConsumerHandoffDiagnostics || !VfsTracingEnabled()) {
    return false;
  }
  if (IsStartupBikAssetName(requested_name) ||
      IsBikLooseStreamTraceAsset(base, stream_guest) ||
      RefiiIsTrackedStartupVideoObject(object_guest)) {
    return ShouldLogRepeatedVfsTraceOperation(api, limit);
  }
  return false;
}

std::string FormatTextureParserCdf8DispatchSummary(uint8_t* base) {
  const ActiveTextureParserCdf8TraceContext* context =
      ActiveTextureParserCdf8Trace();
  if (context == nullptr || base == nullptr) {
    return {};
  }

  const uint32_t descriptor_guest = context->descriptor_guest;
  const uint32_t descriptor_vtable =
      descriptor_guest == 0 ? 0 : LoadGuestU32(base, descriptor_guest);
  const uint32_t descriptor_slot8 =
      descriptor_vtable == 0 ? 0 : LoadGuestU32(base, descriptor_vtable + 8);
  const uint32_t descriptor_size =
      descriptor_guest == 0 ? 0 : LoadGuestU32(base, descriptor_guest + 8);
  const uint32_t parsed_mip_count =
      context->parsed_header_guest == 0
          ? 0
          : LoadGuestU32(base, context->parsed_header_guest + 28);
  const uint32_t mip_delta =
      parsed_mip_count > context->mip_index
          ? parsed_mip_count - context->mip_index
          : 0;
  const uint32_t mip_offset_source =
      context->parsed_header_guest != 0 &&
              context->mip_index <=
                  ((std::numeric_limits<uint32_t>::max() / 4) - 8)
          ? context->parsed_header_guest + (context->mip_index + 8) * 4
          : 0;
  const uint32_t parsed_payload_key =
      context->parsed_header_guest == 0
          ? 0
          : LoadGuestU32(base, context->parsed_header_guest + 80);

  return fmt::format("desc_vtable=0x{:08X} desc_slots={} "
                     "desc_slot8=0x{:08X} initial_size={} "
                     "parsed_mips={} mip_delta={} mip_offsets=0x{:08X} "
                     "mip_offset_words={} parsed80=0x{:08X} "
                     "parser_object=0x{:08X} parser_object_words={} "
                     "parser_vtable=0x{:08X} parser_slots={} "
                     "parser_slot0=0x{:08X} final_args_hint=size_or_payload "
                     "target=0x{:08X} cache=0x{:08X}",
                     descriptor_vtable,
                     FormatGuestWordsIfPointer(base, descriptor_vtable, 4),
                     descriptor_slot8,
                     descriptor_size,
                     parsed_mip_count,
                     mip_delta,
                     mip_offset_source,
                     FormatGuestWordsIfPointer(base, mip_offset_source, 8),
                     parsed_payload_key,
                     context->parser_object_guest,
                     FormatGuestWordsIfPointer(
                         base, context->parser_object_guest, 12),
                     context->parser_object_vtable_guest,
                     FormatGuestWordsIfPointer(
                         base, context->parser_object_vtable_guest, 8),
                     context->parser_object_slot0_guest,
                     context->target_guest,
                     context->cache_guest);
}

void RecordTextureParserCdf8ParserObject(uint8_t* base,
                                         uint32_t object_guest) {
  if (ActiveTextureParserCdf8Trace() == nullptr) {
    return;
  }

  g_active_texture_parser_cdf8_trace.parser_object_guest = object_guest;
  g_active_texture_parser_cdf8_trace.parser_object_vtable_guest = 0;
  g_active_texture_parser_cdf8_trace.parser_object_slot0_guest = 0;
  if (base != nullptr && object_guest >= 0x10000) {
    const uint32_t vtable_guest = LoadGuestU32(base, object_guest);
    g_active_texture_parser_cdf8_trace.parser_object_vtable_guest =
        vtable_guest;
    g_active_texture_parser_cdf8_trace.parser_object_slot0_guest =
        vtable_guest == 0 ? 0 : LoadGuestU32(base, vtable_guest);
  }
}

std::string FormatTextureParserCdf8Context(uint8_t* base) {
  const ActiveTextureParserCdf8TraceContext* context =
      ActiveTextureParserCdf8Trace();
  if (context == nullptr) {
    return {};
  }

  const uint32_t descriptor_guest = context->descriptor_guest;
  const uint32_t primary_size =
      descriptor_guest == 0 ? 0 : LoadGuestU32(base, descriptor_guest + 8);
  const uint32_t primary_buffer =
      descriptor_guest == 0 ? 0 : LoadGuestU32(base, descriptor_guest + 12);
  const uint32_t secondary_size =
      descriptor_guest == 0 ? 0 : LoadGuestU32(base, descriptor_guest + 20);
  const uint32_t secondary_buffer =
      descriptor_guest == 0 ? 0 : LoadGuestU32(base, descriptor_guest + 28);
  const uint32_t payload_guest =
      primary_size > 84 && primary_buffer <=
                              std::numeric_limits<uint32_t>::max() - 84
          ? primary_buffer + 84
          : 0;
  const uint32_t secondary_payload_guest =
      secondary_size > 84 && secondary_buffer <=
                                std::numeric_limits<uint32_t>::max() - 84
          ? secondary_buffer + 84
          : 0;

  return fmt::format("provider={} target=0x{:08X} descriptor=0x{:08X} "
                     "parsed=0x{:08X} mip={} cache=0x{:08X} r8=0x{:08X} "
                     "owner=0x{:08X} wrapper=0x{:08X} "
                     "primary=0x{:08X}/{} primary_head={} "
                     "primary_payload=0x{:08X} primary_payload_words={} "
                     "secondary=0x{:08X}/{} secondary_head={} "
                     "secondary_payload=0x{:08X} secondary_payload_words={} "
                     "parsed0={} parsed50={} target_words={} owner_words={} "
                     "wrapper_words={} parser_object=0x{:08X} "
                     "parser_object_detail={} dispatch={} "
                     "milestones=82B8CE00,82B8CE40,82B8CE78,82B8CED8",
                     NormalizeLooseAssetKey(context->provider),
                     context->target_guest,
                     descriptor_guest,
                     context->parsed_header_guest,
                     context->mip_index,
                     context->cache_guest,
                     context->r8_guest,
                     context->owner_texture_guest,
                     context->wrapper_guest,
                     primary_buffer,
                     primary_size,
                     FormatGuestWordsIfPointer(base, primary_buffer, 8),
                     payload_guest,
                     FormatGuestWordsIfPointer(base, payload_guest, 8),
                     secondary_buffer,
                     secondary_size,
                     FormatGuestWordsIfPointer(base, secondary_buffer, 8),
                     secondary_payload_guest,
                     FormatGuestWordsIfPointer(base, secondary_payload_guest, 8),
                     FormatGuestWordsIfPointer(
                         base, context->parsed_header_guest, 24),
                     FormatGuestWordsAtOffsetIfPointer(
                         base, context->parsed_header_guest, 0x50, 12),
                     FormatGuestWordsIfPointer(base, context->target_guest, 16),
                     FormatTextureOwnerWords(base,
                                             context->owner_texture_guest),
                     FormatGuestWordsIfPointer(base,
                                               context->wrapper_guest,
                                               12),
                     context->parser_object_guest,
                     FormatGuestObjectVTableSlots(
                         base, context->parser_object_guest, 8),
                     FormatTextureParserCdf8DispatchSummary(base));
}

bool IsParserBoundaryTextureWrapperCaller(uint32_t lr) {
  return lr == 0x82B6D690 || lr == 0x82B6C374 || lr == 0x82B6C390;
}

std::string GetTextureParserTraceProvider(uint8_t* base,
                                          uint32_t texture_guest,
                                          uint32_t* wrapper_out = nullptr) {
  if (wrapper_out != nullptr) {
    *wrapper_out = 0;
  }
  if (base == nullptr || texture_guest == 0) {
    return {};
  }

  const uint32_t wrapper_guest = NormalizeRefiiResourceStreamWrapper(
      base, LoadGuestU32(base, texture_guest + 100));
  if (wrapper_out != nullptr) {
    *wrapper_out = wrapper_guest;
  }
  if (wrapper_guest == 0) {
    return {};
  }

  const uint32_t inner_guest = LoadGuestU32(base, wrapper_guest + 16);
  return GetRefiiResourceWrapperProviderKey(base, wrapper_guest, inner_guest);
}

bool LooksLikeSplitTextureHeader(uint8_t* base, uint32_t buffer_guest) {
  if (base == nullptr || buffer_guest < 0x10000) {
    return false;
  }
  return LoadGuestU32(base, buffer_guest) == 0xFFFFFFFE;
}

bool ShouldTraceTextureParserDescriptor(uint8_t* base,
                                        uint32_t descriptor_guest) {
  if (!kTraceTextureParserInternals || !VfsTracingEnabled() ||
      base == nullptr || descriptor_guest < 0x10000) {
    return false;
  }

  const uint32_t primary_size = LoadGuestU32(base, descriptor_guest + 8);
  const uint32_t primary_buffer = LoadGuestU32(base, descriptor_guest + 12);
  const uint32_t secondary_size = LoadGuestU32(base, descriptor_guest + 20);
  const uint32_t secondary_buffer = LoadGuestU32(base, descriptor_guest + 28);
  return (primary_size >= 84 &&
          LooksLikeSplitTextureHeader(base, primary_buffer)) ||
         (secondary_size >= 84 &&
          LooksLikeSplitTextureHeader(base, secondary_buffer));
}

bool ShouldTraceTextureParserCall(uint8_t* base,
                                  uint32_t lr,
                                  uint32_t descriptor_guest) {
  if (!kTraceTextureParserInternals || !VfsTracingEnabled()) {
    return false;
  }
  return (lr >= 0x82B6C000 && lr < 0x82B6D800) ||
         ShouldTraceTextureParserDescriptor(base, descriptor_guest);
}

uint32_t ActiveTextureParserTraceDescriptor() {
  return g_active_texture_parser_trace_descriptor;
}

ScopedTextureParserTraceDescriptor::ScopedTextureParserTraceDescriptor(
    bool enabled,
    uint32_t descriptor_guest)
    : previous_descriptor_(g_active_texture_parser_trace_descriptor) {
  if (enabled && descriptor_guest != 0) {
    g_active_texture_parser_trace_descriptor = descriptor_guest;
  }
}

ScopedTextureParserTraceDescriptor::~ScopedTextureParserTraceDescriptor() {
  g_active_texture_parser_trace_descriptor = previous_descriptor_;
}

const ActiveTextureParserCdf8TraceContext*
ActiveTextureParserCdf8Trace() {
  return g_active_texture_parser_cdf8_trace.enabled
             ? &g_active_texture_parser_cdf8_trace
             : nullptr;
}

ScopedTextureParserCdf8Trace::ScopedTextureParserCdf8Trace(
    bool enabled,
    uint32_t target_guest,
    uint32_t descriptor_guest,
    uint32_t parsed_header_guest,
    uint32_t mip_index,
    uint32_t cache_guest,
    uint32_t r8_guest)
    : previous_context_(g_active_texture_parser_cdf8_trace) {
  if (!enabled) {
    return;
  }

  g_active_texture_parser_cdf8_trace = {};
  g_active_texture_parser_cdf8_trace.enabled = true;
  g_active_texture_parser_cdf8_trace.target_guest = target_guest;
  g_active_texture_parser_cdf8_trace.descriptor_guest = descriptor_guest;
  g_active_texture_parser_cdf8_trace.parsed_header_guest =
      parsed_header_guest;
  g_active_texture_parser_cdf8_trace.mip_index = mip_index;
  g_active_texture_parser_cdf8_trace.cache_guest = cache_guest;
  g_active_texture_parser_cdf8_trace.r8_guest = r8_guest;

  if (const std::optional<TextureParserDescriptorTraceOwner> owner =
          FindTextureParserDescriptorTraceOwner(descriptor_guest)) {
    g_active_texture_parser_cdf8_trace.owner_texture_guest =
        owner->texture_guest;
    g_active_texture_parser_cdf8_trace.wrapper_guest = owner->wrapper_guest;
    g_active_texture_parser_cdf8_trace.provider = owner->provider;
  }
}

ScopedTextureParserCdf8Trace::~ScopedTextureParserCdf8Trace() {
  g_active_texture_parser_cdf8_trace = previous_context_;
}

bool HasTextureParserDescriptorTraceOwner(uint32_t descriptor_guest) {
  return FindTextureParserDescriptorTraceOwner(descriptor_guest).has_value();
}

bool ShouldTreatAsTextureParserDescriptor(uint8_t* base,
                                          uint32_t descriptor_guest) {
  return ShouldTraceTextureParserDescriptor(base, descriptor_guest) ||
         HasTextureParserDescriptorTraceOwner(descriptor_guest);
}

bool ShouldTraceTextureParserProbeCall(uint8_t* base, const PPCContext& ctx) {
  if (!kTraceTextureParserInternals || !VfsTracingEnabled()) {
    return false;
  }
  if (ActiveTextureParserCdf8Trace() != nullptr) {
    return true;
  }
  if (ActiveTextureParserTraceDescriptor() != 0) {
    return true;
  }
  return ShouldTreatAsTextureParserDescriptor(base, ctx.r3.u32) ||
         ShouldTreatAsTextureParserDescriptor(base, ctx.r4.u32) ||
         ShouldTreatAsTextureParserDescriptor(base, ctx.r5.u32) ||
         ShouldTreatAsTextureParserDescriptor(base, ctx.r6.u32) ||
         ShouldTreatAsTextureParserDescriptor(base, ctx.r7.u32) ||
         ShouldTreatAsTextureParserDescriptor(base, ctx.r8.u32);
}

bool ShouldTraceTextureParserDirectChainCall(uint8_t* base,
                                             const PPCContext& ctx) {
  if (!kTraceTextureParserInternals || !VfsTracingEnabled()) {
    return false;
  }
  if (ActiveTextureParserCdf8Trace() != nullptr) {
    return true;
  }
  return ctx.lr == 0x82B8CE40 || ctx.lr == 0x82B8CE78 ||
         ctx.lr == 0x82B96234 || ctx.lr == 0x82B96258 ||
         ShouldTraceTextureParserProbeCall(base, ctx);
}

std::string FormatTextureParserPrimaryWords(uint8_t* base,
                                            uint32_t descriptor_guest) {
  if (base == nullptr || descriptor_guest == 0) {
    return {};
  }
  return FormatGuestWords(base, LoadGuestU32(base, descriptor_guest + 12), 4);
}

std::string FormatTextureParserSecondaryWords(uint8_t* base,
                                              uint32_t descriptor_guest) {
  if (base == nullptr || descriptor_guest == 0) {
    return {};
  }
  return FormatGuestWords(base, LoadGuestU32(base, descriptor_guest + 28), 4);
}

uint32_t SelectTextureParserDescriptorArgument(uint8_t* base,
                                               const PPCContext& ctx) {
  if (const ActiveTextureParserCdf8TraceContext* cdf8_context =
          ActiveTextureParserCdf8Trace()) {
    if (cdf8_context->descriptor_guest != 0) {
      return cdf8_context->descriptor_guest;
    }
  }
  if (ActiveTextureParserTraceDescriptor() != 0) {
    return ActiveTextureParserTraceDescriptor();
  }
  const std::array<uint32_t, 6> candidates = {
      ctx.r4.u32, ctx.r5.u32, ctx.r3.u32, ctx.r6.u32, ctx.r7.u32, ctx.r8.u32};
  for (const uint32_t candidate : candidates) {
    if (ShouldTreatAsTextureParserDescriptor(base, candidate)) {
      return candidate;
    }
  }
  return 0;
}

std::string FormatTextureParserDescriptorSummary(uint8_t* base,
                                                 uint32_t descriptor_guest) {
  if (base == nullptr || descriptor_guest == 0) {
    return {};
  }

  const uint32_t primary_size = LoadGuestU32(base, descriptor_guest + 8);
  const uint32_t primary_buffer = LoadGuestU32(base, descriptor_guest + 12);
  const uint32_t secondary_size = LoadGuestU32(base, descriptor_guest + 20);
  const uint32_t secondary_buffer = LoadGuestU32(base, descriptor_guest + 28);
  const std::optional<TextureParserDescriptorTraceOwner> owner =
      FindTextureParserDescriptorTraceOwner(descriptor_guest);
  return fmt::format("words={} primary=0x{:08X}/{} primary_words={} "
                     "secondary=0x{:08X}/{} secondary_words={} "
                     "owner_texture=0x{:08X} wrapper=0x{:08X} provider={}",
                     FormatGuestWords(base, descriptor_guest, 8),
                     primary_buffer,
                     primary_size,
                     FormatGuestWordsIfPointer(base, primary_buffer, 4),
                     secondary_buffer,
                     secondary_size,
                     FormatGuestWordsIfPointer(base, secondary_buffer, 4),
                     owner ? owner->texture_guest : 0,
                     owner ? owner->wrapper_guest : 0,
                     owner ? NormalizeLooseAssetKey(owner->provider)
                           : std::string{});
}

std::string FormatTextureParserDescriptorOwnerWords(uint8_t* base,
                                                    uint32_t descriptor_guest) {
  const std::optional<TextureParserDescriptorTraceOwner> owner =
      FindTextureParserDescriptorTraceOwner(descriptor_guest);
  if (!owner) {
    return {};
  }
  return FormatTextureOwnerWords(base, owner->texture_guest);
}

void LogTextureParserProbeEnter(uint8_t* base,
                                const PPCContext& original_ctx,
                                std::string_view label) {
  const uint32_t descriptor_guest =
      SelectTextureParserDescriptorArgument(base, original_ctx);
  REXLOG_INFO("VFS trace TextureParser {} enter lr=0x{:08X} "
              "r3=0x{:08X} r4=0x{:08X} r5=0x{:08X} r6=0x{:08X} "
              "r7=0x{:08X} r8=0x{:08X} descriptor=0x{:08X} "
              "descriptor_detail={} r3_words={} r4_words={} r5_words={} "
              "r6_words={} r7_words={} r8_words={} owner_words={} "
              "cdf8_context={}",
              label,
              original_ctx.lr,
              original_ctx.r3.u32,
              original_ctx.r4.u32,
              original_ctx.r5.u32,
              original_ctx.r6.u32,
              original_ctx.r7.u32,
              original_ctx.r8.u32,
              descriptor_guest,
              FormatTextureParserDescriptorSummary(base, descriptor_guest),
              FormatGuestWordsIfPointer(base, original_ctx.r3.u32, 4),
              FormatGuestWordsIfPointer(base, original_ctx.r4.u32, 4),
              FormatGuestWordsIfPointer(base, original_ctx.r5.u32, 4),
              FormatGuestWordsIfPointer(base, original_ctx.r6.u32, 4),
              FormatGuestWordsIfPointer(base, original_ctx.r7.u32, 4),
              FormatGuestWordsIfPointer(base, original_ctx.r8.u32, 4),
              FormatTextureParserDescriptorOwnerWords(base, descriptor_guest),
              FormatTextureParserCdf8Context(base));
}

void LogTextureParserProbeExit(uint8_t* base,
                               const PPCContext& original_ctx,
                               const PPCContext& ctx,
                               std::string_view label) {
  const uint32_t descriptor_guest =
      SelectTextureParserDescriptorArgument(base, original_ctx);
  REXLOG_INFO("VFS trace TextureParser {} exit lr=0x{:08X} "
              "result=0x{:08X} r3_in=0x{:08X} r4_in=0x{:08X} "
              "r5_in=0x{:08X} descriptor=0x{:08X} descriptor_detail={} "
              "r3_words={} r4_words={} r5_words={} owner_words={} "
              "cdf8_context={}",
              label,
              original_ctx.lr,
              ctx.r3.u32,
              original_ctx.r3.u32,
              original_ctx.r4.u32,
              original_ctx.r5.u32,
              descriptor_guest,
              FormatTextureParserDescriptorSummary(base, descriptor_guest),
              FormatGuestWordsIfPointer(base, original_ctx.r3.u32, 4),
              FormatGuestWordsIfPointer(base, original_ctx.r4.u32, 4),
              FormatGuestWordsIfPointer(base, original_ctx.r5.u32, 4),
              FormatTextureParserDescriptorOwnerWords(base, descriptor_guest),
              FormatTextureParserCdf8Context(base));
  RecordRefiiMainMenuBlockerEvent(
      "texture_parser",
      fmt::format("{} lr=0x{:08X} result=0x{:08X} descriptor=0x{:08X} {}",
                  label,
                  original_ctx.lr,
                  ctx.r3.u32,
                  descriptor_guest,
                  FormatTextureParserDescriptorSummary(base, descriptor_guest)));
}


}  // namespace detail
}  // namespace refii::vfs
