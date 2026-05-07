#include "vfs_hooks_private.h"

namespace refii::vfs {
inline namespace detail {

std::string FormatGuestObjectVTableSlots(uint8_t* base,
                                         uint32_t object_guest,
                                         uint32_t slot_count) {
  if (base == nullptr || object_guest < 0x10000) {
    return {};
  }

  const uint32_t vtable_guest = LoadGuestU32(base, object_guest);
  return fmt::format("vtable=0x{:08X} slots={}",
                     vtable_guest,
                     FormatGuestWordsIfPointer(base, vtable_guest, slot_count));
}

bool IsBikLooseStreamTraceAsset(const LooseStreamRecord& record) {
  const std::string provider = NormalizeLooseAssetKey(record.provider_name);
  const std::string path = NormalizeLooseAssetKey(record.path);
  return provider.find("art/videos/microsoft_logo.bik") != std::string::npos ||
         provider.find("art/videos/lionhead_logo.bik") != std::string::npos ||
         path.find("art/videos/microsoft_logo.bik") != std::string::npos ||
         path.find("art/videos/lionhead_logo.bik") != std::string::npos;
}

bool IsBikLooseStreamTraceAsset(uint8_t* base, uint32_t stream_guest) {
  LooseStreamRecord record;
  std::string match;
  return FindLooseStreamRecord(base, stream_guest, &record, &match) &&
         IsBikLooseStreamTraceAsset(record);
}

bool LooksLikeGuestPointer(uint32_t value) {
  return value >= 0x10000 && value < 0x90000000;
}

bool LooksLikeGuestDataPointer(uint32_t value) {
  return value >= 0x10000 && value < 0x80000000;
}

bool IsNativeVfsScheduledRequest(uint8_t* base, uint32_t request_guest) {
  if (!LooksLikeGuestPointer(request_guest)) {
    return false;
  }
  const uint32_t vtable = LoadGuestU32(base, request_guest);
  return vtable == kNativeStartupReadyRequestVTable ||
         vtable == kNativeFrontendRenderRequestVTable;
}

bool IsNativeFrontendRenderRequest(uint8_t* base, uint32_t request_guest) {
  return LooksLikeGuestPointer(request_guest) &&
         LoadGuestU32(base, request_guest) == kNativeFrontendRenderRequestVTable;
}

bool LooksLikePointerPosition(uint32_t position, uint64_t expected_size) {
  return LooksLikeGuestPointer(position) &&
         (expected_size == 0 || static_cast<uint64_t>(position) > expected_size);
}

bool IsStartupLuaAssetName(std::string_view name) {
  const std::string normalized = NormalizeLooseAssetKey(name);
  return normalized.find("scripts/startup/") != std::string::npos &&
         normalized.size() >= 4 &&
         normalized.substr(normalized.size() - 4) == ".lua";
}

bool RefiiStartupVideosReadyForMenuHandoff();
bool RefiiMainMenuTraceIsHashOnly();
bool RefiiMainMenuTraceIsTextureOwnedButNotOpened();

bool ShouldTraceBikPpcShape(uint8_t* base,
                            uint32_t stream_guest,
                            std::string_view api,
                            uint32_t loose_limit,
                            uint32_t native_limit) {
  (void)native_limit;
  if (!(kTraceBikCallbackDiagnostics || kTraceBikConsumerHandoffDiagnostics) ||
      !VfsTracingEnabled()) {
    return false;
  }
  if (RefiiStartupVideosPlaybackCriticalActive()) {
    return false;
  }

  if (IsBikLooseStreamTraceAsset(base, stream_guest)) {
    if (RefiiStartupVideosReadyForMenuHandoff()) {
      return RefiiMainMenuTraceIsHashOnly() &&
             ShouldLogRepeatedVfsTraceOperation(
                 fmt::format("{}_postready", api), 2);
    }
    return ShouldLogLooseStreamOperation(api, stream_guest, loose_limit);
  }

  return false;
}

bool ShouldTraceStartupBikStreamShape(uint8_t* base,
                                      uint32_t stream_guest,
                                      std::string_view api,
                                      uint32_t loose_limit) {
  return ShouldTraceBikPpcShape(base, stream_guest, api, loose_limit);
}

std::string FormatBikPpcStreamSummary(uint8_t* base, uint32_t stream_guest) {
  if (base == nullptr || stream_guest < 0x10000) {
    return {};
  }

  LooseStreamRecord record;
  std::string match;
  const bool loose = FindLooseStreamRecord(base, stream_guest, &record, &match);
  const uint32_t vtable_guest = LoadGuestU32(base, stream_guest);
  const bool valid_vtable = vtable_guest >= 0x10000 && vtable_guest < 0x90000000;
  const uint32_t slot12 =
      valid_vtable ? LoadGuestU32(base, vtable_guest + 12) : 0;
  const uint32_t slot16 =
      valid_vtable ? LoadGuestU32(base, vtable_guest + 16) : 0;
  const uint32_t slot20 =
      valid_vtable ? LoadGuestU32(base, vtable_guest + 20) : 0;
  const uint32_t slot24 =
      valid_vtable ? LoadGuestU32(base, vtable_guest + 24) : 0;
  const uint32_t slot28 =
      valid_vtable ? LoadGuestU32(base, vtable_guest + 28) : 0;
  const uint32_t slot32 =
      valid_vtable ? LoadGuestU32(base, vtable_guest + 32) : 0;
  const uint32_t slot36 =
      valid_vtable ? LoadGuestU32(base, vtable_guest + 36) : 0;

  return fmt::format("loose={} match={} provider={} path={} words={} "
                     "vtable=0x{:08X} slot12=0x{:08X} slot16=0x{:08X} "
                     "slot20=0x{:08X} slot24=0x{:08X} slot28=0x{:08X} "
                     "slot32=0x{:08X} slot36=0x{:08X} slots={}",
                     loose,
                     loose ? match : std::string{},
                     loose ? NormalizeLooseAssetKey(record.provider_name)
                           : std::string{},
                     loose ? record.path : std::string{},
                     FormatGuestWordsIfPointer(base, stream_guest, 12),
                     vtable_guest,
                     slot12,
                     slot16,
                     slot20,
                     slot24,
                     slot28,
                     slot32,
                     slot36,
                     FormatGuestWordsIfPointer(base, vtable_guest, 12));
}

std::string FormatBikBufferedNativeShape(uint8_t* base,
                                         uint32_t stream_guest) {
  if (base == nullptr || stream_guest < 0x10000) {
    return {};
  }
  const uint32_t vtable_guest = LoadGuestU32(base, stream_guest);
  const uint32_t pending_request = LoadGuestU32(base, stream_guest + 36);
  return fmt::format(
      "stream=0x{:08X} words80={} vtable=0x{:08X} slots={} "
      "fields begin=0x{:08X}:0x{:08X} current=0x{:08X}:0x{:08X} "
      "size28=0x{:08X} size32=0x{:08X} pending=0x{:08X} "
      "field72=0x{:08X} pending_words={}",
      stream_guest,
      FormatGuestWordsIfPointer(base, stream_guest, 20),
      vtable_guest,
      FormatGuestWordsIfPointer(base, vtable_guest, 12),
      LoadGuestU32(base, stream_guest + 12),
      LoadGuestU32(base, stream_guest + 16),
      LoadGuestU32(base, stream_guest + 20),
      LoadGuestU32(base, stream_guest + 24),
      LoadGuestU32(base, stream_guest + 28),
      LoadGuestU32(base, stream_guest + 32),
      pending_request,
      LoadGuestU32(base, stream_guest + 72),
      FormatGuestWordsIfPointer(base, pending_request, 16));
}

std::string FormatRefiiReadRequestShape(uint8_t* base,
                                        uint32_t request_guest) {
  if (base == nullptr || request_guest < 0x10000) {
    return {};
  }
  return fmt::format("request=0x{:08X} words={} status24=0x{:08X} "
                     "bytes124={} bytes176={} requested192={} offset200={}",
                     request_guest,
                     FormatGuestWordsIfPointer(base, request_guest, 20),
                     LoadGuestU32(base, request_guest + 24),
                     LoadGuestU32(base, request_guest + 124),
                     LoadGuestU32(base, request_guest + 176),
                     LoadGuestU64(base, request_guest + 192),
                     LoadGuestU32(base, request_guest + 200));
}

std::chrono::steady_clock::time_point RefiiBootTraceStartTime() {
  static const auto start = std::chrono::steady_clock::now();
  return start;
}

int64_t RefiiBootTraceAgeMs() {
  return std::chrono::duration_cast<std::chrono::milliseconds>(
             std::chrono::steady_clock::now() - RefiiBootTraceStartTime())
      .count();
}

std::mutex& RefiiPostVideoHandoffTraceMutex() {
  static std::mutex mutex;
  return mutex;
}

RefiiPostVideoHandoffTrace& RefiiPostVideoHandoffTraceState() {
  static RefiiPostVideoHandoffTrace trace;
  return trace;
}

uint32_t RefiiCompletedPostGameFacePackageMaskLocked(
    const RefiiPostVideoHandoffTrace& trace) {
  if ((trace.post_gameface_package_extension_mask & 0x7u) == 0x7u) {
    return trace.post_gameface_package_extension_mask;
  }
  if ((trace.post_gameface_completed_package_extension_mask & 0x7u) == 0x7u) {
    return trace.post_gameface_completed_package_extension_mask;
  }
  return trace.post_gameface_package_extension_mask;
}

RefiiStartupVideoItemTrace* SelectStartupVideoTraceSlot(
    RefiiPostVideoHandoffTrace& trace,
    std::string_view provider) {
  const std::string normalized = NormalizeLooseAssetKey(provider);
  if (normalized.find("microsoft_logo.bik") != std::string::npos) {
    return &trace.microsoft;
  }
  if (normalized.find("lionhead_logo.bik") != std::string::npos) {
    return &trace.lionhead;
  }
  return nullptr;
}

bool IsStartupBikAssetName(std::string_view name) {
  const std::string normalized = NormalizeLooseAssetKey(name);
  return normalized.find("art/videos/microsoft_logo.bik") != std::string::npos ||
         normalized.find("art/videos/lionhead_logo.bik") != std::string::npos ||
         normalized.find("microsoft_logo.bik") != std::string::npos ||
         normalized.find("lionhead_logo.bik") != std::string::npos;
}

bool GetBikLooseStreamRecord(uint8_t* base,
                             uint32_t stream_guest,
                             LooseStreamRecord* out_record);

RefiiStartupVideoItemTrace* SelectStartupVideoTraceSlotByStreamLocked(
    RefiiPostVideoHandoffTrace& trace,
    uint8_t* base,
    uint32_t stream_guest) {
  (void)base;
  if (stream_guest == 0) {
    return nullptr;
  }
  if (trace.microsoft.source_stream_guest == stream_guest ||
      trace.microsoft.wrapped_stream_guest == stream_guest) {
    return &trace.microsoft;
  }
  if (trace.lionhead.source_stream_guest == stream_guest ||
      trace.lionhead.wrapped_stream_guest == stream_guest) {
    return &trace.lionhead;
  }
  return nullptr;
}

RefiiStartupVideoItemTrace* SelectStartupVideoTraceSlotByObjectOrStreamLocked(
    RefiiPostVideoHandoffTrace& trace,
    uint8_t* base,
    uint32_t object_guest,
    uint32_t stream_guest) {
  if (object_guest != 0) {
    if (trace.microsoft.object_guest == object_guest) {
      return &trace.microsoft;
    }
    if (trace.lionhead.object_guest == object_guest) {
      return &trace.lionhead;
    }
  }
  return SelectStartupVideoTraceSlotByStreamLocked(trace, base, stream_guest);
}

bool GetBikLooseStreamRecord(uint8_t* base,
                             uint32_t stream_guest,
                             LooseStreamRecord* out_record) {
  LooseStreamRecord record;
  std::string match;
  if (!FindLooseStreamRecord(base, stream_guest, &record, &match) ||
      !IsBikLooseStreamTraceAsset(record)) {
    return false;
  }
  if (out_record != nullptr) {
    *out_record = std::move(record);
  }
  return true;
}

void UpdateStartupVideoTraceSlot(RefiiStartupVideoItemTrace* slot,
                                 std::string_view provider,
                                 uint64_t expected_size,
                                 uint32_t object_guest,
                                 uint32_t source_stream_guest,
                                 uint32_t wrapped_stream_guest) {
  if (slot == nullptr) {
    return;
  }
  const auto now = std::chrono::steady_clock::now();
  if (slot->provider.empty()) {
    slot->provider = NormalizeLooseAssetKey(provider);
    slot->first_seen_at = now;
  }
  if (expected_size != 0) {
    slot->expected_size = expected_size;
  }
  if (object_guest != 0) {
    slot->object_guest = object_guest;
  }
  if (source_stream_guest != 0) {
    slot->source_stream_guest = source_stream_guest;
  }
  if (wrapped_stream_guest != 0) {
    slot->wrapped_stream_guest = wrapped_stream_guest;
  }
}

void RecordRefiiStartupVideoBufferedWrapper(uint8_t* base,
                                            std::string_view provider,
                                            uint64_t expected_size,
                                            uint32_t source_stream_guest,
                                            uint32_t wrapped_stream_guest,
                                            uint32_t create_r5,
                                            uint32_t create_r6,
                                            bool repaired) {
  RefiiStartupVideoItemTrace* slot = nullptr;
  {
    std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
    auto& trace = RefiiPostVideoHandoffTraceState();
    slot = SelectStartupVideoTraceSlot(trace, provider);
    UpdateStartupVideoTraceSlot(slot,
                                provider,
                                expected_size,
                                0,
                                source_stream_guest,
                                wrapped_stream_guest);
    if (slot == nullptr) {
      return;
    }
    slot->create_buffered_r5 = create_r5;
    slot->create_buffered_r6 = create_r6;
    slot->wrapper_size28 =
        wrapped_stream_guest != 0 ? LoadGuestU32(base, wrapped_stream_guest + 28)
                                  : 0;
    slot->wrapper_size32 =
        wrapped_stream_guest != 0 ? LoadGuestU32(base, wrapped_stream_guest + 32)
                                  : 0;
    slot->wrapper_pending_request =
        wrapped_stream_guest != 0 ? LoadGuestU32(base, wrapped_stream_guest + 36)
                                  : 0;
    trace.last_bik_provider = slot->provider;
    trace.last_bik_stream_guest =
        wrapped_stream_guest != 0 ? wrapped_stream_guest : source_stream_guest;
    trace.last_bik_at = std::chrono::steady_clock::now();
    if (slot->wrapper_verdict_emitted) {
      return;
    }
    slot->wrapper_verdict_emitted = true;
  }
  if (!VfsTracingEnabled() && !LooseAssetOverrideTracingEnabled()) {
    return;
  }

  const uint32_t vtable =
      wrapped_stream_guest != 0 ? LoadGuestU32(base, wrapped_stream_guest) : 0;
  const uint32_t size28 =
      wrapped_stream_guest != 0 ? LoadGuestU32(base, wrapped_stream_guest + 28)
                                : 0;
  const uint32_t size32 =
      wrapped_stream_guest != 0 ? LoadGuestU32(base, wrapped_stream_guest + 32)
                                : 0;
  const uint32_t pending =
      wrapped_stream_guest != 0 ? LoadGuestU32(base, wrapped_stream_guest + 36)
                                : 0;
  const bool vtable_ok = vtable == kNativeBufferedStreamVTable;
  const bool size_matches =
      expected_size == 0 || size28 == expected_size || size32 == expected_size;
  const bool prebuffer_started = pending != 0;
  const char* next =
      !repaired ? "CreateBuffered_shape_failure"
      : !vtable_ok ? "BIK_buffered_vtable_mismatch"
      : !size_matches ? "BIK_buffered_size_mismatch"
      : !prebuffer_started ? "BIK_prebuffer_source_slot16_missing"
                           : "BIK_cursor_status_or_finalizer";

  REXLOG_INFO(
      "VFS trace BIK buffered wrapper verdict provider={} source=0x{:08X} "
      "wrapped=0x{:08X} expected_size={} create_r5=0x{:08X} "
      "create_r6=0x{:08X} vtable=0x{:08X} vtable_ok={} "
      "size28=0x{:08X} size32=0x{:08X} size_matches={} "
      "pending_request=0x{:08X} source_slot16_prebuffer_started={} "
      "next={} source_shape=[{}] wrapped_shape=[{}] request_shape=[{}]",
      NormalizeLooseAssetKey(provider),
      source_stream_guest,
      wrapped_stream_guest,
      expected_size,
      create_r5,
      create_r6,
      vtable,
      vtable_ok,
      size28,
      size32,
      size_matches,
      pending,
      prebuffer_started,
      next,
      FormatBikPpcStreamSummary(base, source_stream_guest),
      FormatBikBufferedNativeShape(base, wrapped_stream_guest),
      FormatRefiiReadRequestShape(base, pending));
  LogRefiiVfsChainVerdict("native_shaped_stream",
                          0,
                          provider,
                          provider,
                          provider,
                          repaired ? "native_buffered_stream"
                                   : "raw_refii_host_stream",
                          "ResourceManager_GetCached_BIK_buffered_shape",
                          "KeepHooked",
                          next);
}

void RecordRefiiStartupVideoReadQueued(uint8_t* base,
                                       uint32_t stream_guest,
                                       uint32_t request_guest,
                                       uint32_t requested_bytes) {
  LooseStreamRecord record;
  if (!GetBikLooseStreamRecord(base, stream_guest, &record)) {
    return;
  }
  std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
  auto& trace = RefiiPostVideoHandoffTraceState();
  RefiiStartupVideoItemTrace* slot =
      SelectStartupVideoTraceSlot(trace, record.provider_name);
  UpdateStartupVideoTraceSlot(
      slot, record.provider_name, record.expected_size, 0, stream_guest, 0);
  if (slot == nullptr) {
    return;
  }
  slot->read_queued = true;
  slot->last_read_request = request_guest;
  slot->last_read_requested = requested_bytes;
  slot->read_queued_at = std::chrono::steady_clock::now();
  trace.last_bik_provider = slot->provider;
  trace.last_bik_stream_guest = stream_guest;
  trace.last_bik_at = slot->read_queued_at;
}

void RecordRefiiStartupVideoReadComplete(uint8_t* base,
                                         uint32_t stream_guest,
                                         uint32_t requested_bytes,
                                         uint32_t completed_bytes,
                                         int64_t queue_wait_ms,
                                         int64_t read_ms) {
  LooseStreamRecord record;
  if (!GetBikLooseStreamRecord(base, stream_guest, &record)) {
    return;
  }
  std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
  auto& trace = RefiiPostVideoHandoffTraceState();
  RefiiStartupVideoItemTrace* slot =
      SelectStartupVideoTraceSlot(trace, record.provider_name);
  UpdateStartupVideoTraceSlot(
      slot, record.provider_name, record.expected_size, 0, stream_guest, 0);
  if (slot == nullptr) {
    return;
  }
  slot->read_complete = completed_bytes == requested_bytes;
  slot->last_read_requested = requested_bytes;
  slot->last_read_completed = completed_bytes;
  slot->last_read_queue_wait_ms = queue_wait_ms;
  slot->last_read_ms = read_ms;
  slot->read_complete_at = std::chrono::steady_clock::now();
  trace.last_bik_provider = slot->provider;
  trace.last_bik_stream_guest = stream_guest;
  trace.last_bik_at = slot->read_complete_at;
}

void MaybeLogRefiiStartupVideoShapeVerdictLocked(
    uint8_t* base,
    RefiiStartupVideoItemTrace& slot,
    std::string_view phase);

void RecordRefiiStartupVideoCursor(uint8_t* base,
                                   uint32_t object_guest,
                                   uint32_t stream_guest,
                                   uint32_t result) {
  LooseStreamRecord record;
  if (!GetBikLooseStreamRecord(base, stream_guest, &record)) {
    return;
  }
  std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
  auto& trace = RefiiPostVideoHandoffTraceState();
  RefiiStartupVideoItemTrace* slot =
      SelectStartupVideoTraceSlot(trace, record.provider_name);
  UpdateStartupVideoTraceSlot(slot,
                              record.provider_name,
                              record.expected_size,
                              object_guest,
                              0,
                              stream_guest);
  if (slot == nullptr) {
    return;
  }
  slot->cursor_seen = true;
  slot->last_cursor_result = result;
  const auto now = std::chrono::steady_clock::now();
  if (slot->first_cursor_at.time_since_epoch().count() == 0) {
    slot->first_cursor_at = now;
  }
  slot->cursor_at = now;
  trace.last_bik_provider = slot->provider;
  trace.last_bik_object_guest = object_guest;
  trace.last_bik_stream_guest = stream_guest;
  trace.last_bik_at = slot->cursor_at;
  MaybeLogRefiiStartupVideoShapeVerdictLocked(base, *slot, "ready");
}

void RecordRefiiStartupVideoSize(uint8_t* base,
                                 uint32_t object_guest,
                                 uint32_t stream_guest,
                                 uint32_t result) {
  LooseStreamRecord record;
  if (!GetBikLooseStreamRecord(base, stream_guest, &record)) {
    return;
  }
  std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
  auto& trace = RefiiPostVideoHandoffTraceState();
  RefiiStartupVideoItemTrace* slot =
      SelectStartupVideoTraceSlot(trace, record.provider_name);
  UpdateStartupVideoTraceSlot(slot,
                              record.provider_name,
                              record.expected_size,
                              object_guest,
                              0,
                              stream_guest);
  if (slot == nullptr) {
    return;
  }
  slot->size_seen = true;
  slot->last_size_result = result;
  slot->size_at = std::chrono::steady_clock::now();
  trace.last_bik_provider = slot->provider;
  trace.last_bik_object_guest = object_guest;
  trace.last_bik_stream_guest = stream_guest;
  trace.last_bik_at = slot->size_at;
  MaybeLogRefiiStartupVideoShapeVerdictLocked(base, *slot, "ready");
}

void RecordRefiiStartupVideoStatus(uint8_t* base,
                                   uint32_t object_guest,
                                   uint32_t stream_guest,
                                   uint32_t result) {
  LooseStreamRecord record;
  if (!GetBikLooseStreamRecord(base, stream_guest, &record)) {
    return;
  }
  std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
  auto& trace = RefiiPostVideoHandoffTraceState();
  RefiiStartupVideoItemTrace* slot =
      SelectStartupVideoTraceSlot(trace, record.provider_name);
  UpdateStartupVideoTraceSlot(slot,
                              record.provider_name,
                              record.expected_size,
                              object_guest,
                              0,
                              stream_guest);
  if (slot == nullptr) {
    return;
  }
  slot->status_seen = true;
  slot->last_status_result = result;
  slot->status_at = std::chrono::steady_clock::now();
  trace.last_bik_provider = slot->provider;
  trace.last_bik_object_guest = object_guest;
  trace.last_bik_stream_guest = stream_guest;
  trace.last_bik_at = slot->status_at;
  MaybeLogRefiiStartupVideoShapeVerdictLocked(base, *slot, "ready");
}

bool RefiiStartupVideoSlotReady(const RefiiStartupVideoItemTrace& slot);

void MaybeLogRefiiStartupVideoShapeVerdictLocked(
    uint8_t* base,
    RefiiStartupVideoItemTrace& slot,
    std::string_view phase) {
  if (!kTraceBikCallbackDiagnostics || !VfsTracingEnabled() ||
      slot.provider.empty()) {
    return;
  }
  const bool finalizer_phase = phase == "finalizer";
  if (finalizer_phase) {
    if (!slot.finalizer_seen || slot.finalizer_shape_verdict_emitted) {
      return;
    }
    slot.finalizer_shape_verdict_emitted = true;
  } else {
    if (!RefiiStartupVideoSlotReady(slot) || slot.ready_shape_verdict_emitted) {
      return;
    }
    slot.ready_shape_verdict_emitted = true;
  }

  auto delta_ms = [](std::chrono::steady_clock::time_point begin,
                     std::chrono::steady_clock::time_point end) -> int64_t {
    if (begin.time_since_epoch().count() == 0 ||
        end.time_since_epoch().count() == 0) {
      return -1;
    }
    return std::chrono::duration_cast<std::chrono::milliseconds>(end - begin)
        .count();
  };
  const bool cursor_pointer =
      LooksLikePointerPosition(slot.last_cursor_result, slot.expected_size);
  const bool position_pointer =
      LooksLikePointerPosition(slot.last_position_low, slot.expected_size);
  const bool size_matches =
      slot.expected_size == 0 ||
      static_cast<uint64_t>(slot.last_size_result) == slot.expected_size ||
      static_cast<uint64_t>(slot.last_buffered_size_result) == slot.expected_size;

  REXLOG_INFO(
      "VFS trace BIK native stream shape verdict phase={} provider={} "
      "object=0x{:08X} source=0x{:08X} wrapped=0x{:08X} "
      "data_ready_ms={} first_cursor_ms={} first_size_ms={} finalizer_ms={} "
      "read_request=0x{:08X} read={}/{} queue_wait_ms={} read_ms={} "
      "cursor=0x{:08X} cursor_pointer_like={} "
      "position_stream=0x{:08X} position_out=0x{:08X} "
      "position=0x{:08X}:0x{:08X} position_pointer_like={} "
      "size=0x{:08X} buffered_size_stream=0x{:08X} buffered_size=0x{:08X} "
      "size_matches={} status=0x{:08X} finalizer_result=0x{:08X} "
      "generated_shape=GetCached_slot32_CreateBuffered_BufferedInit_slot16_"
      "BIK_callbacks_position_reads_buffered_fields12_16 "
      "source_shape=[{}] wrapped_shape=[{}] request_shape=[{}]",
      phase,
      slot.provider,
      slot.object_guest,
      slot.source_stream_guest,
      slot.wrapped_stream_guest,
      delta_ms(slot.first_seen_at, slot.read_complete_at),
      delta_ms(slot.first_seen_at, slot.cursor_at),
      delta_ms(slot.first_seen_at, slot.size_at),
      delta_ms(slot.first_seen_at, slot.finalizer_at),
      slot.last_read_request,
      slot.last_read_completed,
      slot.last_read_requested,
      slot.last_read_queue_wait_ms,
      slot.last_read_ms,
      slot.last_cursor_result,
      cursor_pointer,
      slot.last_position_stream_guest,
      slot.last_position_out_guest,
      slot.last_position_high,
      slot.last_position_low,
      position_pointer,
      slot.last_size_result,
      slot.last_buffered_size_stream_guest,
      slot.last_buffered_size_result,
      size_matches,
      slot.last_status_result,
      slot.last_finalizer_result,
      FormatBikPpcStreamSummary(base, slot.source_stream_guest),
      FormatBikBufferedNativeShape(base, slot.wrapped_stream_guest),
      FormatRefiiReadRequestShape(base, slot.last_read_request));
  LogRefiiVfsChainVerdict(
      "native_shaped_stream",
      0,
      slot.provider,
      slot.provider,
      slot.provider,
      "native_buffered_stream",
      finalizer_phase ? "BIK_finalizer" : "BIK_ready",
      "KeepHooked",
      finalizer_phase ? "consumer_complete" : "BIK_callbacks_or_finalizer");
}

void MaybeLogRefiiStartupVideoShapeVerdict(uint8_t* base,
                                           uint32_t stream_guest,
                                           std::string_view phase) {
  LooseStreamRecord record;
  if (!GetBikLooseStreamRecord(base, stream_guest, &record)) {
    return;
  }
  std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
  auto& trace = RefiiPostVideoHandoffTraceState();
  if (RefiiStartupVideoItemTrace* slot =
          SelectStartupVideoTraceSlot(trace, record.provider_name)) {
    MaybeLogRefiiStartupVideoShapeVerdictLocked(base, *slot, phase);
  }
}

void RecordRefiiStartupVideoPosition(uint8_t* base,
                                     uint32_t stream_guest,
                                     uint32_t out_guest,
                                     uint32_t low,
                                     uint32_t high) {
  LooseStreamRecord record;
  if (!GetBikLooseStreamRecord(base, stream_guest, &record)) {
    return;
  }
  std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
  auto& trace = RefiiPostVideoHandoffTraceState();
  RefiiStartupVideoItemTrace* slot =
      SelectStartupVideoTraceSlot(trace, record.provider_name);
  UpdateStartupVideoTraceSlot(slot,
                              record.provider_name,
                              record.expected_size,
                              0,
                              IsRefiiHostDiskStream(base, stream_guest)
                                  ? stream_guest
                                  : 0,
                              IsRefiiHostDiskStream(base, stream_guest)
                                  ? 0
                                  : stream_guest);
  if (slot == nullptr) {
    return;
  }
  slot->last_position_stream_guest = stream_guest;
  slot->last_position_out_guest = out_guest;
  slot->last_position_low = low;
  slot->last_position_high = high;
  slot->position_at = std::chrono::steady_clock::now();
  MaybeLogRefiiStartupVideoShapeVerdictLocked(base, *slot, "ready");
}

bool TryRepairRefiiStartupBikBufferedPosition(uint8_t* base,
                                              uint32_t stream_guest,
                                              uint32_t out_guest,
                                              uint32_t native_low,
                                              uint32_t native_high,
                                              uint32_t lr,
                                              uint32_t* repaired_low,
                                              uint32_t* repaired_high) {
  // The BIK cursor path also calls rex_lhStream_GetPosition and expects the
  // native buffered pointer-like value for playback. Only the status/completion
  // helper at sub_82C6EA80 should be normalized for the ready gate.
  constexpr uint32_t kBikStatusGetPositionLr = 0x82C6EAB4;
  if (lr != kBikStatusGetPositionLr) {
    return false;
  }

  if (base == nullptr || stream_guest == 0 || out_guest == 0 ||
      repaired_low == nullptr || repaired_high == nullptr) {
    return false;
  }

  LooseStreamRecord record;
  if (!GetBikLooseStreamRecord(base, stream_guest, &record)) {
    return false;
  }

  std::string provider;
  uint64_t expected_size = record.expected_size;
  uint32_t source_stream = 0;
  uint32_t read_requested = 0;
  uint32_t read_completed = 0;
  bool emit_verdict = false;
  {
    std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
    auto& trace = RefiiPostVideoHandoffTraceState();
    RefiiStartupVideoItemTrace* slot =
        SelectStartupVideoTraceSlotByStreamLocked(trace, base, stream_guest);
    if (slot == nullptr) {
      slot = SelectStartupVideoTraceSlot(trace, record.provider_name);
    }
    UpdateStartupVideoTraceSlot(
        slot, record.provider_name, record.expected_size, 0, 0, stream_guest);
    if (slot == nullptr || !slot->read_complete ||
        !LooksLikePointerPosition(native_low, slot->expected_size)) {
      return false;
    }

    provider = slot->provider;
    expected_size = slot->expected_size;
    source_stream = slot->source_stream_guest;
    read_requested = slot->last_read_requested;
    read_completed = slot->last_read_completed;
    slot->last_native_position_low = native_low;
    slot->last_native_position_high = native_high;
    slot->last_position_stream_guest = stream_guest;
    slot->last_position_out_guest = out_guest;
    slot->last_position_low = 0;
    slot->last_position_high = 0;
    slot->position_at = std::chrono::steady_clock::now();
    trace.last_bik_provider = slot->provider;
    trace.last_bik_stream_guest = stream_guest;
    trace.last_bik_at = slot->position_at;
    if (!slot->position_repair_verdict_emitted) {
      slot->position_repair_verdict_emitted = true;
      emit_verdict = true;
    }
  }

  StoreGuestU32(base, out_guest + 0, 0);
  StoreGuestU32(base, out_guest + 4, 0);
  StoreGuestU32(base, stream_guest + 12, 0);
  StoreGuestU32(base, stream_guest + 16, 0);
  *repaired_low = 0;
  *repaired_high = 0;

  if (emit_verdict && VfsTracingEnabled()) {
    REXLOG_INFO("Refii VFS BIK completion position repair provider={} "
                "lr=0x{:08X} stream=0x{:08X} out=0x{:08X} "
                "native_position=0x{:08X}:0x{:08X} "
                "repaired_position=0x00000000:0x00000000 "
                "expected_size={} read={}/{} source=0x{:08X} "
                "shape=[{}] next=BIK_finalizer_or_startup_update",
                provider,
                lr,
                stream_guest,
                out_guest,
                native_high,
                native_low,
                expected_size,
                read_completed,
                read_requested,
                source_stream,
                FormatBikBufferedNativeShape(base, stream_guest));
    LogRefiiVfsChainVerdict("native_shaped_stream",
                            lr,
                            provider,
                            provider,
                            provider,
                            "native_buffered_stream",
                            "BIK_GetPosition_pointer_shape_repair",
                            "KeepHooked",
                            "BIK_finalizer_or_startup_update");
  }

  return true;
}

void RecordRefiiStartupVideoBufferedSize(uint8_t* base,
                                         uint32_t stream_guest,
                                         uint32_t result) {
  LooseStreamRecord record;
  if (!GetBikLooseStreamRecord(base, stream_guest, &record)) {
    return;
  }
  std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
  auto& trace = RefiiPostVideoHandoffTraceState();
  RefiiStartupVideoItemTrace* slot =
      SelectStartupVideoTraceSlot(trace, record.provider_name);
  UpdateStartupVideoTraceSlot(slot,
                              record.provider_name,
                              record.expected_size,
                              0,
                              IsRefiiHostDiskStream(base, stream_guest)
                                  ? stream_guest
                                  : 0,
                              IsRefiiHostDiskStream(base, stream_guest)
                                  ? 0
                                  : stream_guest);
  if (slot == nullptr) {
    return;
  }
  slot->last_buffered_size_stream_guest = stream_guest;
  slot->last_buffered_size_result = result;
  slot->buffered_size_at = std::chrono::steady_clock::now();
  MaybeLogRefiiStartupVideoShapeVerdictLocked(base, *slot, "ready");
}

void RecordRefiiStartupVideoFinalizer(uint8_t* base,
                                      uint32_t object_guest,
                                      uint32_t result) {
  std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
  auto& trace = RefiiPostVideoHandoffTraceState();
  RefiiStartupVideoItemTrace* slot = nullptr;
  if (trace.microsoft.object_guest == object_guest) {
    slot = &trace.microsoft;
  } else if (trace.lionhead.object_guest == object_guest) {
    slot = &trace.lionhead;
  }
  if (slot == nullptr) {
    return;
  }
  slot->finalizer_seen = true;
  slot->finalizer_synthetic = false;
  slot->last_finalizer_result = result;
  slot->finalizer_at = std::chrono::steady_clock::now();
  MaybeLogRefiiStartupVideoShapeVerdictLocked(base, *slot, "finalizer");
}

std::string FormatBikObjectVirtualProbe(uint8_t* base,
                                        uint32_t object_guest);

void RecordRefiiStartupVideoObjectHandoff(uint8_t* base,
                                          std::string_view method,
                                          uint32_t lr,
                                          uint32_t object_guest,
                                          uint32_t stream_before,
                                          uint32_t stream_after,
                                          uint32_t result,
                                          std::string_view requested_name = {}) {
  if (!VfsTracingEnabled() || !kTraceBikConsumerHandoffDiagnostics) {
    return;
  }

  LooseStreamRecord stream_record;
  const bool after_is_bik_stream =
      GetBikLooseStreamRecord(base, stream_after, &stream_record);
  LooseStreamRecord before_record;
  const bool before_is_bik_stream =
      GetBikLooseStreamRecord(base, stream_before, &before_record);
  if (!after_is_bik_stream && before_is_bik_stream) {
    stream_record = std::move(before_record);
  }
  const bool requested_is_startup_bik = IsStartupBikAssetName(requested_name);
  if (!after_is_bik_stream && !before_is_bik_stream &&
      !requested_is_startup_bik) {
    return;
  }
  const bool after_is_native_buffered =
      base != nullptr && stream_after != 0 &&
      LoadGuestU32(base, stream_after) == kNativeBufferedStreamVTable;
  const std::string stream_provider = !stream_record.provider_name.empty()
                                          ? stream_record.provider_name
                                          : std::string{};

  std::string provider;
  bool emit_verdict = false;
  const char* next = "BIK_object_constructor_or_store_missing";
  {
    std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
    auto& trace = RefiiPostVideoHandoffTraceState();
    RefiiStartupVideoItemTrace* slot =
        SelectStartupVideoTraceSlotByObjectOrStreamLocked(
            trace,
            base,
            object_guest,
            stream_after != 0 ? stream_after : stream_before);
    if (slot == nullptr && !stream_provider.empty()) {
      slot = SelectStartupVideoTraceSlot(trace, stream_provider);
    }
    if (slot == nullptr && IsStartupBikAssetName(requested_name)) {
      slot = SelectStartupVideoTraceSlot(trace, requested_name);
    }
    if (slot == nullptr) {
      return;
    }

    provider = slot->provider.empty() ? NormalizeLooseAssetKey(requested_name)
                                      : slot->provider;
    if (provider.empty()) {
      provider = "startup_bik";
    }
    UpdateStartupVideoTraceSlot(slot,
                                provider,
                                slot->expected_size,
                                object_guest,
                                0,
                                after_is_bik_stream || after_is_native_buffered
                                    ? stream_after
                                    : 0);
    slot->object_method_seen = true;
    slot->last_object_method = std::string(method);
    slot->last_object_method_lr = lr;
    slot->last_object_method_result = result;
    slot->last_object_stream_before = stream_before;
    slot->last_object_stream_after = stream_after;
    slot->object_method_at = std::chrono::steady_clock::now();
    if (stream_after != 0) {
      slot->object_stream_attached_seen = true;
      next = "BIK_cursor_status_or_finalizer";
    } else if (stream_before != 0 && stream_after == 0) {
      slot->object_stream_cleared_seen = true;
      next = "BIK_object_rejected_buffered_stream";
    }

    trace.last_bik_provider = provider;
    trace.last_bik_object_guest = object_guest;
    trace.last_bik_stream_guest =
        stream_after != 0 ? stream_after : stream_before;
    trace.last_bik_at = slot->object_method_at;

    if (!slot->object_handoff_verdict_emitted &&
        (method == "BIKObject_Open_82C6F2A8" || stream_after != 0 ||
         slot->object_stream_cleared_seen)) {
      slot->object_handoff_verdict_emitted = true;
      emit_verdict = true;
    }
  }

  if (!emit_verdict) {
    return;
  }

  REXLOG_INFO("Refii VFS BIK consumer handoff verdict provider={} method={} "
              "lr=0x{:08X} object=0x{:08X} stream_before=0x{:08X} "
              "stream_after=0x{:08X} result=0x{:08X} requested={} "
              "object_state=[{}] stream_before_shape=[{}] "
              "stream_after_shape=[{}] next={}",
              provider,
              method,
              lr,
              object_guest,
              stream_before,
              stream_after,
              result,
              requested_name.empty() ? "unknown" : requested_name,
              FormatBikObjectVirtualProbe(base, object_guest),
              FormatBikPpcStreamSummary(base, stream_before),
              FormatBikBufferedNativeShape(base, stream_after),
              next);
  LogRefiiVfsChainVerdict("native_shaped_stream",
                          lr,
                          provider,
                          provider,
                          provider,
                          "native_buffered_stream",
                          "BIK_consumer_object_handoff",
                          "TraceOnce",
                          next);
}

void MaybeLogStartupLuaGetCachedShapeVerdict(uint8_t* base,
                                             std::string_view name,
                                             uint32_t lr,
                                             uint32_t stream_guest,
                                             bool native_shape_repaired,
                                             std::string_view reason) {
  if (!VfsTracingEnabled() || !IsStartupLuaAssetName(name)) {
    return;
  }

  static std::mutex mutex;
  static std::unordered_set<std::string> emitted;
  const std::string normalized = NormalizeLooseAssetKey(name);
  {
    std::lock_guard<std::mutex> lock(mutex);
    if (!emitted.insert(fmt::format("{}|0x{:08X}|0x{:08X}",
                                    normalized,
                                    lr,
                                    stream_guest))
             .second) {
      return;
    }
  }

  uint64_t read_calls = 0;
  uint64_t read_bytes = 0;
  uint64_t size = 0;
  uint64_t position = 0;
  std::filesystem::path path;
  if (const auto record = FindRefiiHostStreamRecord(stream_guest)) {
    std::lock_guard<std::mutex> lock(record->mutex);
    read_calls = record->read_calls;
    read_bytes = record->read_bytes;
    size = record->size;
    position = record->position;
    path = record->path;
  }

  REXLOG_INFO("Refii VFS startup Lua GetCached shape verdict name={} "
              "lr=0x{:08X} stream=0x{:08X} native_shape_repaired={} "
              "reason={} size={} position={} read_calls={} read_bytes={} "
              "host={} stream_shape=[{}] next=lua_read_or_close",
              normalized,
              lr,
              stream_guest,
              native_shape_repaired,
              reason.empty() ? "none" : reason,
              size,
              position,
              read_calls,
              read_bytes,
              path.string(),
              FormatBikPpcStreamSummary(base, stream_guest));
  LogRefiiVfsChainVerdict("lua",
                          lr,
                          normalized,
                          normalized,
                          NormalizeLooseAssetKey(path.string()),
                          native_shape_repaired ? "native_buffered_stream"
                                                : "Refii_host_stream",
                          "ResourceManager_GetCached_startup_lua_shape",
                          "KeepHooked",
                          "lua_read_or_close");
}

void MaybeLogStartupLuaStreamCloseVerdict(uint32_t stream_guest,
                                          const RefiiHostStreamRecord* record,
                                          size_t active_after) {
  if (!VfsTracingEnabled() || record == nullptr) {
    return;
  }
  const std::string normalized_path = NormalizeLooseAssetKey(record->path.string());
  if (normalized_path.find("luasource/scripts/startup/") == std::string::npos ||
      normalized_path.size() < 4 ||
      normalized_path.substr(normalized_path.size() - 4) != ".lua") {
    return;
  }

  REXLOG_INFO("Refii VFS startup Lua close verdict stream=0x{:08X} "
              "active={} read_calls={} read_bytes={} size={} position={} "
              "host={} next={}",
              stream_guest,
              active_after,
              record->read_calls,
              record->read_bytes,
              record->size,
              record->position,
              record->path.string(),
              record->read_bytes == 0 ? "lua_stream_closed_unread"
                                      : "lua_stream_consumed");
}

bool FrontendTextureConsumerTraceActive();


}  // namespace detail
}  // namespace refii::vfs
