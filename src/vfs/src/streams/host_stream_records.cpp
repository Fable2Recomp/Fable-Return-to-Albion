#include "vfs_hooks_private.h"

namespace refii::vfs {
inline namespace detail {

std::mutex& RenderSharedStateMutex() {
  static std::mutex mutex;
  return mutex;
}

void LogLooseAssetCallSite(std::string_view api,
                           const PPCContext& ctx,
                           uint32_t out_guest,
                           uint32_t manager_guest,
                           uint32_t name_guest,
                           uint32_t mode,
                           std::string_view asset_path) {
  if (!LooseAssetOverrideTracingEnabled() ||
      !IsTraceInterestingAsset(asset_path)) {
    return;
  }

  REXLOG_INFO("{} caller_lr=0x{:08X} caller_ctr=0x{:08X} sp=0x{:08X} "
              "out=0x{:08X} manager=0x{:08X} name_obj=0x{:08X} mode={} "
              "asset={}",
              api,
              ctx.lr,
              ctx.ctr.u32,
              ctx.r1.u32,
              out_guest,
              manager_guest,
              name_guest,
              mode,
              asset_path);
}

std::unordered_map<uint32_t, LooseStreamRecord>& LooseStreamRecords() {
  static std::unordered_map<uint32_t, LooseStreamRecord> records;
  return records;
}

std::deque<uint32_t>& LooseStreamRecordOrder() {
  static std::deque<uint32_t> order;
  return order;
}

std::mutex& LooseStreamRecordsMutex() {
  static std::mutex mutex;
  return mutex;
}

std::unordered_map<uint32_t, std::shared_ptr<RefiiHostStreamRecord>>&
RefiiHostStreamRecords() {
  static std::unordered_map<uint32_t, std::shared_ptr<RefiiHostStreamRecord>>
      records;
  return records;
}

std::mutex& RefiiHostStreamRecordsMutex() {
  static std::mutex mutex;
  return mutex;
}

std::unordered_map<uint32_t, std::shared_ptr<RefiiReadRequestRecord>>&
RefiiReadRequestRecords() {
  static std::unordered_map<uint32_t, std::shared_ptr<RefiiReadRequestRecord>>
      records;
  return records;
}

std::mutex& RefiiReadRequestRecordsMutex() {
  static std::mutex mutex;
  return mutex;
}

std::deque<uint32_t>& RefiiReadRequestRecordOrder() {
  static std::deque<uint32_t> order;
  return order;
}

std::shared_ptr<RefiiHostStreamRecord> FindRefiiHostStreamRecord(
    uint32_t stream_guest) {
  if (stream_guest == 0) {
    return {};
  }

  std::lock_guard<std::mutex> lock(RefiiHostStreamRecordsMutex());
  const auto match = RefiiHostStreamRecords().find(stream_guest);
  return match == RefiiHostStreamRecords().end() ? nullptr : match->second;
}

void StoreRefiiHostStreamRecord(
    uint32_t stream_guest,
    std::shared_ptr<RefiiHostStreamRecord> record) {
  if (stream_guest == 0 || record == nullptr) {
    return;
  }

  std::lock_guard<std::mutex> lock(RefiiHostStreamRecordsMutex());
  RefiiHostStreamRecords()[stream_guest] = std::move(record);
}

size_t RefiiHostStreamRecordCount() {
  std::lock_guard<std::mutex> lock(RefiiHostStreamRecordsMutex());
  return RefiiHostStreamRecords().size();
}

size_t RefiiReadRequestRecordCount() {
  std::lock_guard<std::mutex> lock(RefiiReadRequestRecordsMutex());
  return RefiiReadRequestRecords().size();
}

std::shared_ptr<RefiiReadRequestRecord> FindRefiiReadRequestRecord(
    uint32_t request_guest,
    uint32_t* out_record_guest) {
  std::lock_guard<std::mutex> lock(RefiiReadRequestRecordsMutex());
  const auto find_record = [&](uint32_t guest)
      -> std::shared_ptr<RefiiReadRequestRecord> {
    const auto match = RefiiReadRequestRecords().find(guest);
    if (match == RefiiReadRequestRecords().end()) {
      return nullptr;
    }
    if (out_record_guest != nullptr) {
      *out_record_guest = guest;
    }
    return match->second;
  };

  if (auto exact = find_record(request_guest)) {
    return exact;
  }

  // Native read requests expose a secondary interface at object +4. Consumers
  // often hold that pointer rather than the primary ref-counted object.
  if (request_guest >= 4) {
    return find_record(request_guest - 4);
  }

  return nullptr;
}

void StoreRefiiReadRequestRecord(uint32_t request_guest,
                                 std::shared_ptr<RefiiReadRequestRecord> record) {
  if (request_guest == 0 || record == nullptr) {
    return;
  }

  std::lock_guard<std::mutex> lock(RefiiReadRequestRecordsMutex());
  const bool inserted = RefiiReadRequestRecords().find(request_guest) ==
                        RefiiReadRequestRecords().end();
  record->request_guest = request_guest;
  RefiiReadRequestRecords()[request_guest] = std::move(record);
  if (inserted) {
    RefiiReadRequestRecordOrder().push_back(request_guest);
    LooseStats().active_read_requests.fetch_add(1, std::memory_order_relaxed);
  }
  constexpr size_t kMaxReadRequestRecords = 8192;
  while (RefiiReadRequestRecords().size() > kMaxReadRequestRecords &&
         !RefiiReadRequestRecordOrder().empty()) {
    const uint32_t oldest = RefiiReadRequestRecordOrder().front();
    RefiiReadRequestRecordOrder().pop_front();
    const size_t erased = RefiiReadRequestRecords().erase(oldest);
    if (erased != 0) {
      LooseStats().active_read_requests.fetch_sub(erased,
                                                  std::memory_order_relaxed);
    }
  }
}

void EraseRefiiReadRequestRecord(uint32_t request_guest) {
  if (request_guest == 0) {
    return;
  }

  std::lock_guard<std::mutex> lock(RefiiReadRequestRecordsMutex());
  const size_t erased = RefiiReadRequestRecords().erase(request_guest);
  if (erased != 0) {
    LooseStats().active_read_requests.fetch_sub(erased,
                                                std::memory_order_relaxed);
    if (VfsTracingEnabled()) {
      REXLOG_INFO("Refii read request released request=0x{:08X} active={}",
                  request_guest,
                  RefiiReadRequestRecords().size());
    }
  }
}

void EraseRefiiHostStreamRecord(uint32_t stream_guest) {
  if (stream_guest == 0) {
    return;
  }

  std::shared_ptr<RefiiHostStreamRecord> record;
  size_t active_after = 0;
  bool erased = false;
  {
    std::lock_guard<std::mutex> lock(RefiiHostStreamRecordsMutex());
    auto& records = RefiiHostStreamRecords();
    const auto match = records.find(stream_guest);
    if (match != records.end()) {
      record = match->second;
      records.erase(match);
      active_after = records.size();
      erased = true;
    }
  }
  if (erased) {
    LooseStats().refii_owned_streams_closed.fetch_add(
        1, std::memory_order_relaxed);
  }
  if (erased && (LooseAssetOverrideTracingEnabled() || VfsTracingEnabled())) {
    const uint64_t read_calls = record != nullptr ? record->read_calls : 0;
    const uint64_t read_bytes = record != nullptr ? record->read_bytes : 0;
    REXLOG_INFO("Refii host loose stream closed stream=0x{:08X} active={} "
                "read_calls={} read_bytes={} size={} path={}",
                stream_guest,
                active_after,
                read_calls,
                read_bytes,
                record != nullptr ? record->size : 0,
                record != nullptr ? record->path.string() : std::string{});
    MaybeLogStartupLuaStreamCloseVerdict(stream_guest, record.get(), active_after);
  }
}

void PruneLooseStreamRecordsLocked() {
  constexpr size_t kMaxLooseStreamRecords = 32768;

  auto& records = LooseStreamRecords();
  auto& order = LooseStreamRecordOrder();
  uint64_t pruned = 0;
  while (records.size() > kMaxLooseStreamRecords && !order.empty()) {
    const uint32_t oldest = order.front();
    order.pop_front();
    pruned += records.erase(oldest);
  }
  if (pruned != 0) {
    auto& stats = LooseStats();
    stats.stream_records_pruned.fetch_add(pruned, std::memory_order_relaxed);
    stats.active_stream_records.fetch_sub(pruned, std::memory_order_relaxed);
  }
}

void StoreLooseStreamRecordLocked(uint32_t stream_guest,
                                  LooseStreamRecord record) {
  auto& records = LooseStreamRecords();
  const bool inserted = records.find(stream_guest) == records.end();
  records[stream_guest] = std::move(record);
  if (inserted) {
    LooseStats().active_stream_records.fetch_add(1, std::memory_order_relaxed);
    LooseStreamRecordOrder().push_back(stream_guest);
    PruneLooseStreamRecordsLocked();
  }
}

void EraseLooseStreamRecord(uint32_t stream_guest) {
  if (stream_guest == 0) {
    return;
  }

  std::lock_guard<std::mutex> lock(LooseStreamRecordsMutex());
  const size_t erased = LooseStreamRecords().erase(stream_guest);
  if (erased != 0) {
    LooseStats().active_stream_records.fetch_sub(erased,
                                                 std::memory_order_relaxed);
  }
}

void RecordLooseStream(uint8_t* base,
                       uint32_t stream_guest,
                       const std::filesystem::path& loose_path,
                       std::string provider_root,
                       std::string provider_name) {
  if (stream_guest == 0) {
    return;
  }

  std::error_code error;
  const uint64_t expected_size = std::filesystem::file_size(loose_path, error);
  LooseStreamRecord record;
  record.path = loose_path.string();
  record.provider_root = std::move(provider_root);
  record.provider_name = std::move(provider_name);
  record.expected_size = error ? 0 : expected_size;

  {
    std::lock_guard<std::mutex> lock(LooseStreamRecordsMutex());
    StoreLooseStreamRecordLocked(stream_guest, record);
  }
  const uint64_t opened =
      LooseStats().streams_opened.fetch_add(1, std::memory_order_relaxed) + 1;
  RecordRefiiOwnedStreamLifecycleOpen(record.provider_name);
  if (!LooseAssetOverrideTracingEnabled()) {
    std::lock_guard<std::mutex> lock(LooseStreamRecordsMutex());
    LogLooseStatsMilestone(
        "streams_opened", opened, LooseStreamRecords().size());
  }

  if (LooseAssetOverrideTracingEnabled()) {
    REXLOG_INFO("Loose stream opened stream=0x{:08X} expected_size={} path={} "
                "provider_root={} provider_name={} words={} {}",
                stream_guest,
                record.expected_size,
                record.path,
                record.provider_root,
                record.provider_name,
                FormatGuestWords(base, stream_guest, 12),
                FormatGuestObjectVTable(base, stream_guest, 12));
  }
}

bool FindLooseStreamRecord(uint8_t* base,
                           uint32_t stream_guest,
                           LooseStreamRecord* out_record,
                           std::string* out_match);

void RecordLooseStreamAlias(uint8_t* base,
                            std::string_view api,
                            uint32_t alias_stream_guest,
                            uint32_t source_stream_guest) {
  if (alias_stream_guest == 0 || source_stream_guest == 0 ||
      alias_stream_guest == source_stream_guest) {
    return;
  }

  LooseStreamRecord source_record;
  std::string source_match;
  if (!FindLooseStreamRecord(base,
                             source_stream_guest,
                             &source_record,
                             &source_match)) {
    return;
  }

  {
    std::lock_guard<std::mutex> lock(LooseStreamRecordsMutex());
    StoreLooseStreamRecordLocked(alias_stream_guest, source_record);
  }

  if (LooseAssetOverrideTracingEnabled()) {
    REXLOG_INFO("Loose stream alias {} alias=0x{:08X} source=0x{:08X} "
                "source_match={} expected_size={} path={} alias_words={}",
                api,
                alias_stream_guest,
                source_stream_guest,
                source_match,
                source_record.expected_size,
                source_record.path,
                FormatGuestWords(base, alias_stream_guest, 12));
  }
}

uint32_t TryReadOutputPointer(uint8_t* base, uint32_t out_guest) {
  if (base == nullptr || out_guest < 0x10000 || out_guest >= 0x90000000) {
    return 0;
  }
  return LoadGuestU32(base, out_guest);
}

void RecordLooseStreamAliasFromOutput(uint8_t* base,
                                      std::string_view api,
                                      uint32_t out_guest,
                                      uint32_t source_stream_guest) {
  LooseStreamRecord source_record;
  std::string source_match;
  if (!FindLooseStreamRecord(base,
                             source_stream_guest,
                             &source_record,
                             &source_match)) {
    return;
  }

  RecordLooseStreamAlias(
      base, api, TryReadOutputPointer(base, out_guest), source_stream_guest);
}

bool ShouldLogLooseStreamOperation(std::string_view api,
                                   uint32_t stream_guest,
                                   uint32_t limit = 12) {
  if (!VfsTracingEnabled() && !LooseAssetOverrideTracingEnabled() &&
      !VfsVerboseIoTracingEnabled()) {
    return false;
  }

  auto is_high_volume_io = [](std::string_view name) {
    return name == "Buffered_Read" || name == "ReadUInt32" ||
           name == "Reader_Read" || name == "DiskStream_Read" ||
           name == "DiskStream_DirectRead" ||
           name == "CompositeStream_DirectRead" ||
           name == "RefiiHostReadRequest" ||
           name == "RefiiHostReadRequest_Defer" ||
           name == "RefiiHostStream_Read" ||
           name == "RefiiIoQueue_Enqueue" ||
           name == "ReadRequest_AddRef" ||
           name == "ReadRequest_Complete" ||
           name == "ReadRequest_GetBytes" ||
           name == "ReadRequest_GetOffset" ||
           name == "ReadRequest_GetStatus" ||
           name == "ReadRequest_Release" ||
           name == "ReadRequest_SmartPtrAssign" ||
           name == "ReadRequest_WaitTiming" ||
           name == "StreamReader_FlushPage" ||
           name == "StreamReaderRefillCached" ||
           name == "StreamReaderRefillEof" ||
           name == "StreamReaderRefillSync" ||
           name == "StreamReaderReadE6D0" ||
           name == "StreamReaderReadE8E0" || name == "BIKRead" ||
           name == "BIKCursor";
  };
  if (is_high_volume_io(api) && !VfsVerboseIoTracingEnabled()) {
    return false;
  }

  static std::mutex mutex;
  static std::unordered_map<std::string, uint32_t> counts;

  std::lock_guard<std::mutex> lock(mutex);
  std::string key = std::string(api);
  key += ':';
  key += fmt::format("{:08X}", stream_guest);
  const uint32_t next_count = ++counts[key];
  return next_count <= limit || next_count == 32 || next_count == 128;
}

bool FindLooseStreamRecord(uint8_t* base,
                           uint32_t stream_guest,
                           LooseStreamRecord* out_record,
                           std::string* out_match) {
  if (stream_guest == 0) {
    return false;
  }
  if (LooseStats().active_stream_records.load(std::memory_order_relaxed) == 0) {
    return false;
  }

  std::lock_guard<std::mutex> lock(LooseStreamRecordsMutex());
  const auto& records = LooseStreamRecords();
  const auto exact = records.find(stream_guest);
  if (exact != records.end()) {
    if (out_record != nullptr) {
      *out_record = exact->second;
    }
    if (out_match != nullptr) {
      *out_match = "exact";
    }
    return true;
  }

  if (base == nullptr || stream_guest < 0x10000 || stream_guest >= 0x90000000) {
    return false;
  }

  for (uint32_t offset = 0; offset < 96; offset += sizeof(uint32_t)) {
    const uint32_t inner_guest = LoadGuestU32(base, stream_guest + offset);
    const auto inner = records.find(inner_guest);
    if (inner == records.end()) {
      continue;
    }
    if (out_record != nullptr) {
      *out_record = inner->second;
    }
    if (out_match != nullptr) {
      *out_match = fmt::format("inner+0x{:02X}", offset);
    }
    return true;
  }

  return false;
}

void LogLooseStreamSizeResult(std::string_view api,
                              uint8_t* base,
                              uint32_t stream_guest,
                              const PPCContext& ctx) {
  if (!ShouldLogLooseStreamOperation(api, stream_guest, 8)) {
    return;
  }

  LooseStreamRecord record;
  std::string match;
  if (!FindLooseStreamRecord(base, stream_guest, &record, &match)) {
    return;
  }

  REXLOG_INFO("Loose stream size {} stream=0x{:08X} match={} r3=0x{:08X} "
              "r4=0x{:08X} expected_size={} path={} words={}",
              api,
              stream_guest,
              match,
              ctx.r3.u32,
              ctx.r4.u32,
              record.expected_size,
              record.path,
              FormatGuestWords(base, stream_guest, 12));
}

void LogLooseStreamRead(std::string_view api,
                        uint8_t* base,
                        uint32_t stream_guest,
                        uint32_t arg4,
                        uint32_t arg5,
                        uint32_t arg6,
                        const PPCContext& ctx) {
  if (!ShouldLogLooseStreamOperation(api, stream_guest)) {
    return;
  }

  LooseStreamRecord record;
  std::string match;
  if (!FindLooseStreamRecord(base, stream_guest, &record, &match)) {
    return;
  }

  const uint32_t out_value = TryReadOutputPointer(base, ctx.r3.u32);
  REXLOG_INFO("Loose stream read {} caller_lr=0x{:08X} caller_ctr=0x{:08X} "
              "stream=0x{:08X} match={} r3=0x{:08X} r4=0x{:08X} "
              "r5=0x{:08X} r6=0x{:08X} r7=0x{:08X} result=0x{:08X} "
              "out_value=0x{:08X} expected_size={} path={} words={}",
              api,
              ctx.lr,
              ctx.ctr.u32,
              stream_guest,
              match,
              stream_guest,
              arg4,
              arg5,
              arg6,
              ctx.r7.u32,
              ctx.r3.u32,
              out_value,
              record.expected_size,
              record.path,
              FormatGuestWords(base, stream_guest, 12));
}

bool ShouldTraceStartupBikStreamShape(uint8_t* base,
                                      uint32_t stream_guest,
                                      std::string_view api,
                                      uint32_t loose_limit = 10);
bool IsRefiiHostDiskStream(uint8_t* base, uint32_t stream_guest);
std::string FormatBikPpcStreamSummary(uint8_t* base, uint32_t stream_guest);
std::string FormatBikBufferedNativeShape(uint8_t* base,
                                         uint32_t stream_guest);
std::string FormatRefiiReadRequestShape(uint8_t* base,
                                        uint32_t request_guest);
std::string FormatGuestWordsIfPointer(uint8_t* base,
                                      uint32_t guest,
                                      uint32_t word_count);

void LogLooseStreamWrapperCall(std::string_view api,
                               uint8_t* base,
                               const PPCContext& ctx,
                               uint32_t out_guest,
                               uint32_t source_stream_guest) {
  LooseStreamRecord record;
  std::string match;
  if (!FindLooseStreamRecord(base, source_stream_guest, &record, &match)) {
    return;
  }
  const bool trace_startup_bik =
      ShouldTraceStartupBikStreamShape(base, source_stream_guest, api, 6);
  if (!LooseAssetOverrideTracingEnabled() && !trace_startup_bik) {
    return;
  }

  REXLOG_INFO("Loose stream wrapper {} caller_lr=0x{:08X} caller_ctr=0x{:08X} "
              "out=0x{:08X} source=0x{:08X} match={} r5=0x{:08X} "
              "r6=0x{:08X} expected_size={} path={} source_words={} {} "
              "generated_shape=GetCached:size_slot32_then_CreateBuffered",
              api,
              ctx.lr,
              ctx.ctr.u32,
              out_guest,
              source_stream_guest,
              match,
              ctx.r5.u32,
              ctx.r6.u32,
              record.expected_size,
              record.path,
              FormatGuestWords(base, source_stream_guest, 12),
              FormatGuestObjectVTable(base, source_stream_guest, 12));
}

void LogLooseStreamWrapperResult(std::string_view api,
                                 uint8_t* base,
                                 const PPCContext& before_ctx,
                                 const PPCContext& after_ctx,
                                 uint32_t out_guest,
                                 uint32_t source_stream_guest) {
  LooseStreamRecord record;
  std::string match;
  if (!FindLooseStreamRecord(base, source_stream_guest, &record, &match)) {
    return;
  }
  const bool trace_startup_bik =
      ShouldTraceStartupBikStreamShape(base, source_stream_guest, api, 6);
  if (!LooseAssetOverrideTracingEnabled() && !trace_startup_bik) {
    return;
  }

  const uint32_t out_stream_guest = TryReadOutputPointer(base, out_guest);
  REXLOG_INFO("Loose stream wrapper result {} caller_lr=0x{:08X} "
              "out=0x{:08X} out_stream=0x{:08X} ret=0x{:08X} "
              "source=0x{:08X} match={} expected_size={} path={} "
              "out_words={} out_stream_words={} ret_words={} "
              "buffered_shape=[{}] source_shape=[{}]",
              api,
              before_ctx.lr,
              out_guest,
              out_stream_guest,
              after_ctx.r3.u32,
              source_stream_guest,
              match,
              record.expected_size,
              record.path,
              FormatGuestWords(base, out_guest, 4),
              FormatGuestWords(base, out_stream_guest, 12),
              FormatGuestWords(base, after_ctx.r3.u32, 12),
              FormatBikBufferedNativeShape(base, out_stream_guest),
              FormatBikPpcStreamSummary(base, source_stream_guest));
}

bool IsNativeGetCachedShapeRepairCandidate(std::string_view asset_path) {
  // Generated Fable 2 callers that need buffered streams already call
  // rex_lhStream_CreateBuffered after GetCached. Repairing here double-wraps
  // startup BIKs and can leave the playback cursor parked on the outer wrapper.
  //
  // GameFace package binaries are the exception we have observed from native:
  // the post-video lhFile path accepts the Refii-owned GetCached stream, but
  // then closes it unread unless it receives the native buffered/cache shape.
  return IsGameFaceBinaryAssetKey(asset_path);
}

void LogLooseBufferedInit(uint8_t* base,
                          const PPCContext& before_ctx,
                          const PPCContext& after_ctx,
                          uint32_t buffered_guest,
                          uint32_t source_stream_guest,
                          uint32_t arg5,
                          uint32_t arg6) {
  LooseStreamRecord record;
  std::string match;
  if (!FindLooseStreamRecord(base, source_stream_guest, &record, &match)) {
    return;
  }
  const bool trace_startup_bik =
      ShouldTraceStartupBikStreamShape(base, source_stream_guest, "Buffered_Init", 6);
  if (!LooseAssetOverrideTracingEnabled() && !trace_startup_bik) {
    return;
  }

  REXLOG_INFO("Loose stream buffered init caller_lr=0x{:08X} "
              "caller_ctr=0x{:08X} buffered=0x{:08X} source=0x{:08X} "
              "match={} r5=0x{:08X} r6=0x{:08X} result=0x{:08X} "
              "expected_size={} path={} buffered_words={} source_words={} "
              "generated_shape=prebuffer_if_r6_nonzero_source_slot16 "
              "buffered_shape=[{}] source_shape=[{}]",
              before_ctx.lr,
              before_ctx.ctr.u32,
              buffered_guest,
              source_stream_guest,
              match,
              arg5,
              arg6,
              after_ctx.r3.u32,
              record.expected_size,
              record.path,
              FormatGuestWords(base, buffered_guest, 16),
              FormatGuestWords(base, source_stream_guest, 12),
              FormatBikBufferedNativeShape(base, buffered_guest),
              FormatBikPpcStreamSummary(base, source_stream_guest));
}

bool IsRefiiHostDiskStream(uint8_t* base, uint32_t stream_guest) {
  if (base == nullptr || stream_guest < 0x10000 || stream_guest >= 0x90000000) {
    return false;
  }

  return LoadGuestU32(base, stream_guest) == kRefiiDiskStreamVTable &&
         FindRefiiHostStreamRecord(stream_guest) != nullptr;
}

bool IsRefiiOwnedLhFileStream(uint8_t* base, uint32_t file_guest, int offset) {
  if (base == nullptr || file_guest < 0x10000 || file_guest >= 0x90000000) {
    return false;
  }
  return IsRefiiHostDiskStream(base, LoadGuestU32(base, file_guest + offset));
}

bool TryGetRefiiOwnedLhFileStreamSize(uint8_t* base,
                                      uint32_t file_guest,
                                      int offset,
                                      uint32_t* out_size) {
  if (base == nullptr || file_guest < 0x10000 || file_guest >= 0x90000000) {
    return false;
  }
  const uint32_t stream_guest = LoadGuestU32(base, file_guest + offset);
  if (!IsRefiiHostDiskStream(base, stream_guest)) {
    return false;
  }
  const auto record = FindRefiiHostStreamRecord(stream_guest);
  const uint64_t size = record != nullptr ? record->size
                                          : LoadGuestU64(base, stream_guest + 16);
  if (out_size != nullptr) {
    *out_size = static_cast<uint32_t>(
        std::min<uint64_t>(size, std::numeric_limits<uint32_t>::max()));
  }
  return true;
}

bool IsRefiiReadRequestObject(uint32_t request_guest) {
  return FindRefiiReadRequestRecord(request_guest) != nullptr;
}

bool IsSupportedRefiiLooseOpenMode(uint32_t mode) {
  const uint32_t open_mode = mode == 0 ? 1 : mode;
  return open_mode == 1 || open_mode == 2;
}

bool IsCompositeStreamWithRefiiHostSegment(uint8_t* base, uint32_t stream_guest) {
  if (base == nullptr || stream_guest < 0x10000 || stream_guest >= 0x90000000) {
    return false;
  }

  const uint32_t entries_begin = LoadGuestU32(base, stream_guest + 12);
  const uint32_t entries_end = LoadGuestU32(base, stream_guest + 16);
  if (entries_begin == 0 || entries_end <= entries_begin ||
      ((entries_end - entries_begin) % 16) != 0 ||
      (entries_end - entries_begin) / 16 > 1024) {
    return false;
  }

  const uint32_t entry_count = (entries_end - entries_begin) / 16;
  for (uint32_t index = 0; index < entry_count; ++index) {
    const uint32_t segment_guest = LoadGuestU32(base, entries_begin + index * 16);
    if (IsRefiiHostDiskStream(base, segment_guest)) {
      return true;
    }
  }

  return false;
}

bool IsRefiiStreamReader(uint8_t* base, uint32_t reader_guest) {
  if (base == nullptr || reader_guest < 0x10000 || reader_guest >= 0x90000000) {
    return false;
  }

  const uint32_t source_stream_guest = LoadGuestU32(base, reader_guest + 8);
  return IsRefiiHostDiskStream(base, source_stream_guest) ||
         IsCompositeStreamWithRefiiHostSegment(base, source_stream_guest);
}

uint32_t RefiiReaderPageEntry(uint8_t* base,
                              uint32_t reader_guest,
                              uint32_t page_index) {
  if (base == nullptr || reader_guest == 0) {
    return 0;
  }

  const uint32_t salt = LoadGuestU32(base, reader_guest + 36);
  const uint32_t mixed = salt ^ page_index;
  const uint32_t rotated = (mixed << 5) | (mixed >> 27);
  return reader_guest + 40 + (rotated & 0xFFFFFFE0u);
}


}  // namespace detail
}  // namespace refii::vfs
