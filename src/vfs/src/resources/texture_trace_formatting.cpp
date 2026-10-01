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
  return provider.find("art/videos/middlewarelogos.bik") != std::string::npos ||
         provider.find("art/videos/microsoft_logo.bik") != std::string::npos ||
         provider.find("art/videos/lionhead_logo.bik") != std::string::npos ||
         path.find("art/videos/middlewarelogos.bik") != std::string::npos ||
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

bool IsNativeVfsScheduledRequest(uint8_t* base, uint32_t request_guest) {
  if (!LooksLikeGuestPointer(request_guest)) {
    return false;
  }
  const uint32_t vtable = LoadGuestU32(base, request_guest);
  return vtable == kNativeStartupReadyRequestVTable ||
         vtable == kNativeFrontendRenderRequestVTable;
}

bool IsStartupLuaAssetName(std::string_view name) {
  const std::string normalized = NormalizeLooseAssetKey(name);
  return normalized.find("scripts/startup/") != std::string::npos &&
         normalized.size() >= 4 &&
         normalized.substr(normalized.size() - 4) == ".lua";
}

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
  if (!VfsVerboseIoTracingEnabled()) {
    return false;
  }
  if (RefiiStartupVideosPlaybackCriticalActive()) {
    return false;
  }

  if (IsBikLooseStreamTraceAsset(base, stream_guest)) {
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

RefiiStartupVideoItemTrace* SelectStartupVideoTraceSlot(
    RefiiPostVideoHandoffTrace& trace,
    std::string_view provider) {
  const std::string normalized = NormalizeLooseAssetKey(provider);
  if (normalized.find("middlewarelogos.bik") != std::string::npos) {
    return &trace.middleware;
  }
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
  return normalized.find("art/videos/middlewarelogos.bik") != std::string::npos ||
         normalized.find("art/videos/microsoft_logo.bik") != std::string::npos ||
         normalized.find("art/videos/lionhead_logo.bik") != std::string::npos ||
         normalized.find("middlewarelogos.bik") != std::string::npos ||
         normalized.find("microsoft_logo.bik") != std::string::npos ||
         normalized.find("lionhead_logo.bik") != std::string::npos;
}

bool GetBikLooseStreamRecord(uint8_t* base,
                             uint32_t stream_guest,
                             LooseStreamRecord* out_record);

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
  if (slot->provider.empty()) {
    slot->provider = NormalizeLooseAssetKey(provider);
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
  slot->last_read_requested = requested_bytes;
  slot->read_queued_at = std::chrono::steady_clock::now();
  trace.last_bik_provider = slot->provider;
  trace.last_bik_stream_guest = stream_guest;
  trace.last_bik_at = slot->read_queued_at;
}

void RecordRefiiStartupVideoReadComplete(uint8_t* base,
                                         uint32_t stream_guest,
                                         uint32_t requested_bytes,
                                         uint32_t completed_bytes) {
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
  slot->read_complete_at = std::chrono::steady_clock::now();
  trace.last_bik_provider = slot->provider;
  trace.last_bik_stream_guest = stream_guest;
  trace.last_bik_at = slot->read_complete_at;
}

void RecordRefiiStartupVideoCursor(uint8_t* base,
                                   uint32_t object_guest,
                                   uint32_t stream_guest) {
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
  const auto now = std::chrono::steady_clock::now();
  if (slot->first_cursor_at.time_since_epoch().count() == 0) {
    slot->first_cursor_at = now;
  }
  slot->cursor_at = now;
  trace.last_bik_provider = slot->provider;
  trace.last_bik_object_guest = object_guest;
  trace.last_bik_stream_guest = stream_guest;
  trace.last_bik_at = slot->cursor_at;
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

  std::filesystem::path path;
  if (const auto record = FindRefiiHostStreamRecord(stream_guest)) {
    std::lock_guard<std::mutex> lock(record->mutex);
    path = record->path;
    if (VfsVerboseIoTracingEnabled()) {
      REXLOG_INFO("Refii VFS startup Lua GetCached shape verdict name={} "
                  "lr=0x{:08X} stream=0x{:08X} native_shape_repaired={} "
                  "reason={} size={} position={} read_calls={} read_bytes={} "
                  "host={} stream_shape=[{}] next=lua_read_or_close",
                  normalized,
                  lr,
                  stream_guest,
                  native_shape_repaired,
                  reason.empty() ? "none" : reason,
                  record->size,
                  record->position,
                  record->read_calls,
                  record->read_bytes,
                  path.string(),
                  FormatBikPpcStreamSummary(base, stream_guest));
    }
  }
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
  if (!VfsTracingEnabled() || !VfsVerboseIoTracingEnabled() ||
      record == nullptr) {
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
}  // namespace detail
}  // namespace refii::vfs
