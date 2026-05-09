#include "vfs_hooks_private.h"

namespace refii::vfs {
inline namespace detail {

void MaybeLogStartupLuaStreamCloseVerdict(uint32_t stream_guest,
                                          const RefiiHostStreamRecord* record,
                                          size_t active_after);

bool IsGameFaceBinaryAssetKey(std::string_view asset_path);


thread_local int g_suppress_request_manager_hash_entry_loose_streams = 0;

std::mutex g_refii_resource_wrapper_vtable_mutex;
uint32_t g_refii_resource_wrapper_vtable = 0;
uint32_t g_refii_resource_wrapper_interface_vtable = 0;
thread_local uint32_t g_active_texture_parser_trace_descriptor = 0;

thread_local ActiveTextureParserCdf8TraceContext
    g_active_texture_parser_cdf8_trace;

thread_local ActiveFrontendTextureConsumerTraceContext
    g_active_frontend_texture_consumer_trace;

const ActiveTextureParserCdf8TraceContext*
ActiveTextureParserCdf8Trace();

std::array<uint32_t, 4> ReadHostFileFirstWords(
    const std::filesystem::path& path);
std::string FormatHostFileFirstWords(const std::array<uint32_t, 4>& words);
bool IsTextureHeaderCandidate(std::string_view candidate);
bool MakeVfsDatabaseCandidateResolution(
    const std::filesystem::path& loose_root,
    std::string_view candidate,
    LooseAssetResolution* out_resolution,
    std::string* out_candidate);
bool ResolvePairedTexturePayloadForHeader(std::string_view header_key,
                                          LooseAssetResolution* out_resolution,
                                          std::string* out_candidate);
bool MaybeAugmentRefiiSplitTextureWrapperPayloadView(
    PPCContext& ctx,
    uint8_t* base,
    uint32_t wrapper_guest,
    std::string_view api,
    RefiiResourceWrapperView* view);
void RecordRefiiStartupVideoReadComplete(uint8_t* base,
                                         uint32_t stream_guest,
                                         uint32_t requested_bytes,
                                         uint32_t completed_bytes,
                                         int64_t queue_wait_ms = -1,
                                         int64_t read_ms = -1);
void RecordRefiiMainMenuBlockerEvent(std::string_view kind,
                                     std::string message);
std::string FormatFrontendTextureConsumerTraceForLog(
    const ActiveFrontendTextureConsumerTraceContext& trace);
bool RecordRefiiFrontendTextureConsumerVerdict(
    uint32_t lr,
    uint32_t result,
    const ActiveFrontendTextureConsumerTraceContext& consumer_trace,
    bool texture_owned_but_unopened);
std::string FormatRefiiLastVfsOperationForLog();
std::string FormatRefiiMainMenuBlockerTraceForLog();
std::string FormatRefiiPostVideoHandoffTraceForLog();
std::string FormatRefiiLoadingScreenSequenceGlobals(uint8_t* base);
std::string FormatKnownRequestManagerGuestsForLog(size_t max_count = 8);
std::string FormatTargetedTextureHashTableAudit(uint8_t* base);
size_t TargetedRequestManagerHashContributionCount();
float RefiiGuestF32FromWord(uint32_t word);
bool RefiiMainMenuTraceIsTextureOwnedButNotOpened();
bool RefiiMainMenuTraceNeedsTexturePrime();
bool RefiiMainMenuTraceNeedsFrontendHandoff();
bool RefiiMainMenuTraceAllowsPostVideoReadyAdvance();
bool RefiiStartupVideosPlaybackCriticalActive();
void LogRefiiVfsChainVerdict(std::string_view chain_id,
                             uint32_t root_lr,
                             std::string_view asset,
                             std::string_view candidate,
                             std::string_view provider_relative,
                             std::string_view shape_owner,
                             std::string_view live_boundary,
                             std::string_view collapse_status,
                             std::string_view next_boundary);

void ConfigureRefiiVfsTracyPlots() {
  static std::once_flag configure_once;
  std::call_once(configure_once, [] {
    TracyPlotConfig("Refii VFS Queue Depth",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x38BDF8);
    TracyPlotConfig("Refii VFS Active IO Workers",
                    tracy::PlotFormatType::Number,
                    true,
                    false,
                    0x22C55E);
    TracyPlotConfig("Refii VFS Read Request Bytes",
                    tracy::PlotFormatType::Memory,
                    false,
                    false,
                    0xF59E0B);
    TracyPlotConfig("Refii VFS Read Completed Bytes",
                    tracy::PlotFormatType::Memory,
                    false,
                    false,
                    0x84CC16);
    TracyPlotConfig("Refii VFS StreamReader Page Bytes",
                    tracy::PlotFormatType::Memory,
                    false,
                    false,
                    0xA855F7);
  });
}

LooseAssetRuntimeStats& LooseStats() {
  static LooseAssetRuntimeStats stats;
  return stats;
}

bool ShouldLogLooseStatsMilestone(uint64_t value) {
  return value == 1 || value == 16 || value == 64 || value == 256 ||
         (value != 0 && value % 1024 == 0);
}

void LogLooseStatsMilestone(std::string_view label,
                            uint64_t value,
                            size_t stream_record_count =
                                std::numeric_limits<size_t>::max()) {
  if (!ShouldLogLooseStatsMilestone(value)) {
    return;
  }

  auto& stats = LooseStats();
  const std::string records =
      stream_record_count == std::numeric_limits<size_t>::max()
          ? fmt::format("{}", stats.active_stream_records.load(
                                  std::memory_order_relaxed))
          : fmt::format("{}", stream_record_count);
  REXLOG_INFO("Loose asset stats {}={} vfs_hits={}/{} bank_hits={}/{} "
              "streams={} records={} synthetic={}/{} synthetic_vfs={}/{} "
              "reads={} active_reads={} handles={}/{} pruned={} "
              "ownership=owned:{} native_shaped:{} optional:{} "
              "native_fallback:{} owned_streams:{}/{} owned_reads:{} "
              "sources=lua:{} synthetic:{} database:{}",
              label,
              value,
              stats.vfs_hits.load(std::memory_order_relaxed),
              stats.vfs_lookups.load(std::memory_order_relaxed),
              stats.bank_hits.load(std::memory_order_relaxed),
              stats.bank_lookups.load(std::memory_order_relaxed),
              stats.streams_opened.load(std::memory_order_relaxed),
              records,
              stats.active_synthetic_banks.load(std::memory_order_relaxed),
              stats.synthetic_banks_opened.load(std::memory_order_relaxed),
              stats.synthetic_vfs_hits.load(std::memory_order_relaxed),
              stats.synthetic_vfs_lookups.load(std::memory_order_relaxed),
              stats.read_requests_completed.load(std::memory_order_relaxed),
              stats.active_read_requests.load(std::memory_order_relaxed),
              stats.active_private_file_handles.load(std::memory_order_relaxed),
              stats.active_private_find_handles.load(std::memory_order_relaxed),
              stats.stream_records_pruned.load(std::memory_order_relaxed),
              stats.refii_owned_decisions.load(std::memory_order_relaxed),
              stats.refii_owned_native_shaped.load(std::memory_order_relaxed),
              stats.refii_optional_misses.load(std::memory_order_relaxed),
              stats.refii_native_fallbacks.load(std::memory_order_relaxed),
              stats.refii_owned_streams_opened.load(std::memory_order_relaxed),
              stats.refii_owned_streams_closed.load(std::memory_order_relaxed),
              stats.refii_owned_reads_completed.load(std::memory_order_relaxed),
              stats.refii_lua_streams_opened.load(std::memory_order_relaxed),
              stats.refii_synthetic_streams_opened.load(std::memory_order_relaxed),
              stats.refii_database_streams_opened.load(std::memory_order_relaxed));
}

void RecordRefiiOwnedStreamLifecycleOpen(std::string_view provider_name) {
  auto& stats = LooseStats();
  stats.refii_owned_streams_opened.fetch_add(1, std::memory_order_relaxed);
  const std::string normalized = NormalizeLooseAssetKey(provider_name);
  if (normalized.ends_with(".lua") || normalized.find("lua") != std::string::npos) {
    stats.refii_lua_streams_opened.fetch_add(1, std::memory_order_relaxed);
  }
  if (normalized.rfind("banks/", 0) == 0 ||
      normalized.find(".bank") != std::string::npos ||
      normalized.find("_headers/") != std::string::npos) {
    stats.refii_synthetic_streams_opened.fetch_add(1, std::memory_order_relaxed);
  }
  if (normalized.rfind("refii_vfs/", 0) == 0 ||
      normalized.find("texture_headers/") != std::string::npos ||
      normalized.find("textures/") != std::string::npos ||
      normalized.find("data/") != std::string::npos) {
    stats.refii_database_streams_opened.fetch_add(1, std::memory_order_relaxed);
  }
}

uint32_t HighestPowerOfTwoAtMost(uint32_t value) {
  if (value == 0) {
    return 0;
  }
  uint32_t result = 1;
  while (value >>= 1) {
    result <<= 1;
  }
  return result;
}

bool AddUint32WillOverflow(uint32_t left, uint32_t right) {
  return left > UINT32_MAX - right;
}

bool RoundUpUint32(uint32_t value, uint32_t alignment, uint32_t* out_value) {
  if (out_value == nullptr || alignment == 0) {
    return false;
  }
  const uint32_t remainder = value % alignment;
  if (remainder == 0) {
    *out_value = value;
    return true;
  }
  const uint32_t delta = alignment - remainder;
  if (AddUint32WillOverflow(value, delta)) {
    return false;
  }
  *out_value = value + delta;
  return true;
}

bool IsHeapRangeFree(rex::memory::BaseHeap* heap,
                     uint32_t address,
                     uint32_t size,
                     uint32_t page_size) {
  if (heap == nullptr || size == 0 || heap->page_size() != page_size) {
    return false;
  }

  uint32_t rounded_size = 0;
  if (!RoundUpUint32(size, page_size, &rounded_size) ||
      AddUint32WillOverflow(address, rounded_size)) {
    return false;
  }

  const uint32_t heap_base = heap->heap_base();
  const uint32_t heap_size = heap->heap_size();
  if (address < heap_base || AddUint32WillOverflow(heap_base, heap_size) ||
      address + rounded_size > heap_base + heap_size) {
    return false;
  }

  uint32_t cursor = address;
  const uint32_t end = address + rounded_size;
  while (cursor < end) {
    rex::memory::HeapAllocationInfo info{};
    if (!heap->QueryRegionInfo(cursor, &info) || info.state != 0 ||
        info.region_size == 0) {
      return false;
    }

    if (info.region_size >= end - cursor) {
      return true;
    }
    if (AddUint32WillOverflow(cursor, info.region_size)) {
      return false;
    }
    cursor += info.region_size;
  }

  return true;
}

uint32_t CallVirtualAlloc(PPCContext& source_ctx,
                          uint8_t* base,
                          uint32_t address,
                          uint32_t size,
                          uint32_t allocation_type,
                          uint32_t protect) {
  PPCContext alloc_ctx = source_ctx;
  alloc_ctx.r3.u32 = address;
  alloc_ctx.r4.u32 = size;
  alloc_ctx.r5.u32 = allocation_type;
  alloc_ctx.r6.u32 = protect;
  __imp__rex_VirtualAlloc(alloc_ctx, base);
  return alloc_ctx.r3.u32;
}

uint32_t CallLhMalloc(PPCContext& source_ctx, uint8_t* base, uint32_t size) {
  PPCContext malloc_ctx = source_ctx;
  malloc_ctx.r3.u32 = size;
  __imp__rex_lh_malloc(malloc_ctx, base);
  return malloc_ctx.r3.u32;
}

void CallLhFree(PPCContext& source_ctx, uint8_t* base, uint32_t guest_address) {
  if (guest_address == 0) {
    return;
  }

  PPCContext free_ctx = source_ctx;
  free_ctx.r3.u32 = guest_address;
  rex_lh_free(free_ctx, base);
}

uint32_t LoadGuestU32(uint8_t* base, uint32_t guest_address);

PPCFunc* FindGuestFunction(uint32_t guest_address) {
  if (guest_address == 0) {
    return nullptr;
  }

  for (PPCFuncMapping* mapping = PPCFuncMappings;
       mapping != nullptr && mapping->host != nullptr;
       ++mapping) {
    if (static_cast<uint32_t>(mapping->guest) == guest_address) {
      return mapping->host;
    }
  }

  return nullptr;
}

bool CopyToStreamDestination(PPCContext& source_ctx,
                             uint8_t* base,
                             uint32_t destination_base_guest,
                             uint32_t destination_kind,
                             uint32_t source_guest,
                             uint32_t byte_count) {
  if (base == nullptr || destination_base_guest == 0 || source_guest == 0 ||
      byte_count == 0 || destination_kind > 16) {
    return byte_count == 0;
  }

  const uint32_t copy_function_guest =
      LoadGuestU32(base, kStreamDestinationCopyTable + destination_kind * 4);
  PPCFunc* copy_function = FindGuestFunction(copy_function_guest);
  if (copy_function == nullptr) {
    REXLOG_WARN("Refii stream destination copy failed: kind={} "
                "function=0x{:08X} destination=0x{:08X} source=0x{:08X} "
                "bytes={}",
                destination_kind,
                copy_function_guest,
                destination_base_guest,
                source_guest,
                byte_count);
    return false;
  }

  PPCContext copy_ctx = source_ctx;
  copy_ctx.r3.u32 = destination_base_guest;
  copy_ctx.r4.u32 = source_guest;
  copy_ctx.r5.u32 = byte_count;
  copy_ctx.ctr.u32 = copy_function_guest;
  copy_function(copy_ctx, base);
  return true;
}

void AddRefGuestObject(PPCContext& source_ctx,
                       uint8_t* base,
                       uint32_t object_guest) {
  if (object_guest == 0) {
    return;
  }

  PPCContext add_ref_ctx = source_ctx;
  add_ref_ctx.r3.u32 = object_guest;
  __imp__rex_lhRefCounted_AddRef(add_ref_ctx, base);
}

void ReleaseGuestObject(PPCContext& source_ctx,
                        uint8_t* base,
                        uint32_t object_guest) {
  if (object_guest == 0) {
    return;
  }

  PPCContext release_ctx = source_ctx;
  release_ctx.r3.u32 = object_guest;
  __imp__rex_lhRefCounted_Release(release_ctx, base);
}

bool ShouldLogRepeatedVfsTraceOperation(std::string_view api,
                                        uint32_t limit);

bool TrySilentLargePageReserve(PPCContext& ctx, uint8_t* base) {
  constexpr uint32_t kLargePageSize = 64 * 1024;
  constexpr uint32_t kLargePageScanBegin = 0x40000000;
  constexpr uint32_t kLargePageScanEnd = 0x80000000;

  const uint32_t allocation_size = ctx.r3.u32;
  const uint32_t stride_hint = ctx.r4.u32;
  const uint32_t alignment = ctx.r5.u32;
  if (allocation_size == 0 || alignment != kLargePageSize) {
    return false;
  }

  auto* kernel = rex::system::kernel_state();
  if (kernel == nullptr || kernel->memory() == nullptr) {
    return false;
  }

  const uint32_t stride =
      HighestPowerOfTwoAtMost(std::max(stride_hint, alignment));
  if (stride == 0) {
    return false;
  }

  auto* memory = kernel->memory();
  for (uint32_t candidate = kLargePageScanBegin; candidate < kLargePageScanEnd;) {
    rex::memory::BaseHeap* heap = memory->LookupHeap(candidate);
    if (IsHeapRangeFree(heap, candidate, allocation_size, kLargePageSize)) {
      const uint32_t reserved =
          CallVirtualAlloc(ctx,
                           base,
                           candidate,
                           allocation_size,
                           rex::X_MEM_LARGE_PAGES | rex::X_MEM_RESERVE,
                           rex::X_PAGE_NOACCESS);
      if (reserved != 0) {
        const uint32_t committed =
            CallVirtualAlloc(ctx,
                             base,
                             reserved,
                             allocation_size,
                             rex::X_MEM_LARGE_PAGES | rex::X_MEM_COMMIT,
                             rex::X_PAGE_READWRITE);
        if (committed != 0) {
          ctx.r3.u32 = reserved;
          return true;
        }

        if (auto* reserved_heap = memory->LookupHeap(reserved)) {
          reserved_heap->Release(reserved);
        }
        if (VfsTracingEnabled() &&
            ShouldLogRepeatedVfsTraceOperation("AllocFixedCommitFailed", 8)) {
          REXLOG_WARN("Refii AllocFixed large-page commit failed reserved=0x{:08X} "
                      "size={} stride={} alignment={}",
                      reserved,
                      allocation_size,
                      stride,
                      alignment);
        }
      }
    }

    if (AddUint32WillOverflow(candidate, stride)) {
      break;
    }
    candidate += stride;
  }

  if (VfsTracingEnabled() &&
      ShouldLogRepeatedVfsTraceOperation("AllocFixedFallback", 8)) {
    REXLOG_INFO("Refii AllocFixed large-page fallback size={} stride_hint={} "
                "alignment={}",
                allocation_size,
                stride_hint,
                alignment);
  }
  return false;
}

uint8_t LoadGuestU8(uint8_t* base, uint32_t guest_address) {
  return *rex::memory::GuestPtr<uint8_t*>(base, guest_address);
}

uint16_t LoadGuestU16(uint8_t* base, uint32_t guest_address) {
  const auto* ptr = rex::memory::GuestPtr<uint8_t*>(base, guest_address);
  return static_cast<uint16_t>((uint16_t(ptr[0]) << 8) | uint16_t(ptr[1]));
}

uint32_t LoadGuestU32(uint8_t* base, uint32_t guest_address) {
  const auto* ptr = rex::memory::GuestPtr<uint8_t*>(base, guest_address);
  return (uint32_t(ptr[0]) << 24) | (uint32_t(ptr[1]) << 16) |
         (uint32_t(ptr[2]) << 8) | uint32_t(ptr[3]);
}

uint64_t LoadGuestU64(uint8_t* base, uint32_t guest_address) {
  return (static_cast<uint64_t>(LoadGuestU32(base, guest_address)) << 32) |
         static_cast<uint64_t>(LoadGuestU32(base, guest_address + 4));
}

void StoreGuestU8(uint8_t* base, uint32_t guest_address, uint8_t value) {
  *rex::memory::GuestPtr<uint8_t*>(base, guest_address) = value;
}

void StoreGuestU16(uint8_t* base, uint32_t guest_address, uint16_t value) {
  auto* ptr = rex::memory::GuestPtr<uint8_t*>(base, guest_address);
  ptr[0] = static_cast<uint8_t>((value >> 8) & 0xFF);
  ptr[1] = static_cast<uint8_t>(value & 0xFF);
}

void StoreGuestU32(uint8_t* base, uint32_t guest_address, uint32_t value) {
  auto* ptr = rex::memory::GuestPtr<uint8_t*>(base, guest_address);
  ptr[0] = static_cast<uint8_t>((value >> 24) & 0xFF);
  ptr[1] = static_cast<uint8_t>((value >> 16) & 0xFF);
  ptr[2] = static_cast<uint8_t>((value >> 8) & 0xFF);
  ptr[3] = static_cast<uint8_t>(value & 0xFF);
}

void StoreGuestU64(uint8_t* base, uint32_t guest_address, uint64_t value) {
  StoreGuestU32(base, guest_address, static_cast<uint32_t>(value >> 32));
  StoreGuestU32(base, guest_address + 4, static_cast<uint32_t>(value));
}

bool IsRefiiResourceWrapperPrimaryVTable(uint32_t vtable_guest) {
  return vtable_guest == kResourceStreamWrapperVTable ||
         (g_refii_resource_wrapper_vtable != 0 &&
          vtable_guest == g_refii_resource_wrapper_vtable);
}

bool IsRefiiResourceWrapperInterfaceVTable(uint32_t vtable_guest) {
  return vtable_guest == kResourceStreamWrapperInterfaceVTable ||
         (g_refii_resource_wrapper_interface_vtable != 0 &&
          vtable_guest == g_refii_resource_wrapper_interface_vtable);
}

std::pair<uint32_t, uint32_t> EnsureRefiiResourceWrapperVTables(
    PPCContext& ctx,
    uint8_t* base) {
  if (base == nullptr) {
    return {kResourceStreamWrapperVTable, kResourceStreamWrapperInterfaceVTable};
  }

  std::lock_guard<std::mutex> lock(g_refii_resource_wrapper_vtable_mutex);
  if (g_refii_resource_wrapper_vtable != 0 &&
      g_refii_resource_wrapper_interface_vtable != 0) {
    return {g_refii_resource_wrapper_vtable,
            g_refii_resource_wrapper_interface_vtable};
  }

  const uint32_t primary =
      CallLhMalloc(ctx, base, kResourceWrapperVTableCopySize);
  const uint32_t interface =
      CallLhMalloc(ctx, base, kResourceWrapperVTableCopySize);
  if (primary == 0 || interface == 0) {
    if (primary != 0) {
      CallLhFree(ctx, base, primary);
    }
    if (interface != 0) {
      CallLhFree(ctx, base, interface);
    }
    REXLOG_WARN("Refii resource wrapper private vtable allocation failed; "
                "falling back to native vtables");
    return {kResourceStreamWrapperVTable, kResourceStreamWrapperInterfaceVTable};
  }

  std::memcpy(rex::memory::GuestPtr<uint8_t*>(base, primary),
              rex::memory::GuestPtr<uint8_t*>(base, kResourceStreamWrapperVTable),
              kResourceWrapperVTableCopySize);
  std::memcpy(
      rex::memory::GuestPtr<uint8_t*>(base, interface),
      rex::memory::GuestPtr<uint8_t*>(base, kResourceStreamWrapperInterfaceVTable),
      kResourceWrapperVTableCopySize);

  StoreGuestU32(base, primary + 8, kResourceWrapperDtorFunc);
  StoreGuestU32(base, primary + 12, kResourceWrapperStatusFunc);
  StoreGuestU32(base, primary + 16, kResourceWrapperWaitFunc);
  StoreGuestU32(base, primary + 20, kResourceWrapperGetBytesFunc);
  StoreGuestU32(base, primary + 24, kResourceWrapperGetSizeFunc);
  StoreGuestU32(base, primary + 36, kResourceWrapperSlot36Func);

  StoreGuestU32(base, interface + 8, kResourceWrapperDtorFunc);
  StoreGuestU32(base, interface + 12, kResourceWrapperStatusFunc);
  StoreGuestU32(base, interface + 16, kResourceWrapperWaitFunc);
  StoreGuestU32(base, interface + 20, kResourceWrapperGetBytesFunc);
  StoreGuestU32(base, interface + 24, kResourceWrapperGetSizeFunc);
  StoreGuestU32(base, interface + 36, kResourceWrapperSlot36Func);

  g_refii_resource_wrapper_vtable = primary;
  g_refii_resource_wrapper_interface_vtable = interface;

  if (VfsVerboseIoTracingEnabled()) {
    REXLOG_INFO("Refii resource wrapper private vtables primary=0x{:08X} "
                "interface=0x{:08X} slots=[dtor=0x{:08X} status=0x{:08X} "
                "wait=0x{:08X} bytes=0x{:08X} size=0x{:08X} slot36=0x{:08X}]",
                primary,
                interface,
                kResourceWrapperDtorFunc,
                kResourceWrapperStatusFunc,
                kResourceWrapperWaitFunc,
                kResourceWrapperGetBytesFunc,
                kResourceWrapperGetSizeFunc,
                kResourceWrapperSlot36Func);
  }

  return {primary, interface};
}

bool CallGuestVTableSlot(PPCContext& source_ctx,
                         uint8_t* base,
                         uint32_t object_guest,
                         uint32_t slot_offset,
                         uint32_t arg4 = 0,
                         uint32_t arg5 = 0,
                         uint32_t arg6 = 0,
                         uint32_t arg7 = 0,
                         uint32_t synthetic_lr = 0) {
  if (base == nullptr || object_guest == 0) {
    return false;
  }

  const uint32_t vtable_guest = LoadGuestU32(base, object_guest);
  const uint32_t function_guest = LoadGuestU32(base, vtable_guest + slot_offset);
  PPCFunc* function = FindGuestFunction(function_guest);
  if (function == nullptr) {
    REXLOG_WARN("Refii guest vtable call failed object=0x{:08X} "
                "vtable=0x{:08X} slot=0x{:X} function=0x{:08X}",
                object_guest,
                vtable_guest,
                slot_offset,
                function_guest);
    return false;
  }

  PPCContext call_ctx = source_ctx;
  call_ctx.r3.u32 = object_guest;
  call_ctx.r4.u32 = arg4;
  call_ctx.r5.u32 = arg5;
  call_ctx.r6.u32 = arg6;
  call_ctx.r7.u32 = arg7;
  call_ctx.ctr.u32 = function_guest;
  if (synthetic_lr != 0) {
    call_ctx.lr = synthetic_lr;
  }
  function(call_ctx, base);
  return true;
}

bool ApplyResourceAliasNameSideEffects(PPCContext& source_ctx,
                                       uint8_t* base,
                                       uint32_t owner_guest,
                                       uint32_t resolved_name_guest,
                                       uint32_t requested_hash) {
  if (base == nullptr || owner_guest == 0 || resolved_name_guest == 0) {
    return false;
  }

  const uint32_t hash_guest = CallLhMalloc(source_ctx, base, sizeof(uint32_t));
  if (hash_guest == 0) {
    return false;
  }

  StoreGuestU32(base, hash_guest, requested_hash);

  PPCContext find_ctx = source_ctx;
  find_ctx.r3.u32 = owner_guest;
  find_ctx.r4.u32 = hash_guest;
  find_ctx.lr = 0x8237476C;
  sub_82374F70(find_ctx, base);

  const uint32_t alias_table_entry_guest = find_ctx.r3.u32;
  if (alias_table_entry_guest != 0) {
    PPCContext append_ctx = source_ctx;
    append_ctx.r3.u32 = alias_table_entry_guest;
    append_ctx.r4.u32 = resolved_name_guest;
    append_ctx.lr = 0x82374774;
    sub_82374D78(append_ctx, base);
  }

  CallLhFree(source_ctx, base, hash_guest);
  return alias_table_entry_guest != 0;
}

ResourceAliasValidationResult ValidateSyntheticResourceAliasAttach(
    PPCContext& source_ctx,
    uint8_t* base,
    uint32_t owner_guest,
    uint32_t requested_hash) {
  ResourceAliasValidationResult result;
  if (base == nullptr || owner_guest == 0) {
    return result;
  }

  result.expected_container = owner_guest + 12;
  result.expected_node = LoadGuestU32(base, owner_guest + 16);

  const uint32_t key_guest = CallLhMalloc(source_ctx, base, sizeof(uint32_t));
  const uint32_t out_guest = CallLhMalloc(source_ctx, base, 8);
  if (key_guest == 0 || out_guest == 0) {
    if (key_guest != 0) {
      CallLhFree(source_ctx, base, key_guest);
    }
    if (out_guest != 0) {
      CallLhFree(source_ctx, base, out_guest);
    }
    return result;
  }

  StoreGuestU32(base, key_guest, requested_hash);
  StoreGuestU32(base, out_guest, 0);
  StoreGuestU32(base, out_guest + 4, 0);

  PPCContext validate_ctx = source_ctx;
  validate_ctx.r3.u32 = out_guest;
  validate_ctx.r4.u32 = result.expected_container;
  validate_ctx.r5.u32 = key_guest;
  validate_ctx.lr = 0x8237478C;
  __imp__rex_lhMeshResource_Validate(validate_ctx, base);

  result.ran = true;
  result.result_container = LoadGuestU32(base, out_guest);
  result.result_node = LoadGuestU32(base, out_guest + 4);
  result.attach = result.result_container == result.expected_container &&
                  result.result_node == result.expected_node;

  CallLhFree(source_ctx, base, out_guest);
  CallLhFree(source_ctx, base, key_guest);
  return result;
}

bool ApplySyntheticResourceAliasAttach(PPCContext& source_ctx,
                                       uint8_t* base,
                                       uint32_t owner_guest,
                                       uint32_t requested_name_guest) {
  if (base == nullptr || owner_guest == 0 || requested_name_guest == 0) {
    return false;
  }

  const uint32_t primary_resource = LoadGuestU32(base, owner_guest + 24);
  if (primary_resource == 0 || LoadGuestU32(base, primary_resource) == 0) {
    return false;
  }

  const uint32_t alias_guest = CallLhMalloc(source_ctx, base, 52);
  if (alias_guest == 0) {
    return false;
  }
  auto* alias_object = rex::memory::GuestPtr<uint8_t*>(base, alias_guest);
  std::memset(alias_object, 0, 52);
  StoreGuestU32(base, alias_guest + 0, kResourceAliasVTable);
  StoreGuestU32(base, alias_guest + 4, 0);

  PPCContext name_ctx = source_ctx;
  name_ctx.r3.u32 = alias_guest + 8;
  name_ctx.r4.u32 = requested_name_guest;
  name_ctx.lr = 0x823747F8;
  sub_822F2308(name_ctx, base);

  StoreGuestU32(base, alias_guest + 44, primary_resource);
  CallGuestVTableSlot(source_ctx,
                      base,
                      primary_resource,
                      4,
                      0,
                      0,
                      0,
                      0,
                      0x82374810);
  StoreGuestU32(base, alias_guest + 48, owner_guest);

  CallGuestVTableSlot(source_ctx,
                      base,
                      alias_guest,
                      4,
                      0,
                      0,
                      0,
                      0,
                      0x8237483C);

  const uint32_t request_guest = CallLhMalloc(source_ctx, base, 32);
  if (request_guest == 0) {
    CallGuestVTableSlot(source_ctx,
                        base,
                        alias_guest,
                        8,
                        0,
                        0,
                        0,
                        0,
                        0x8237489C);
    return false;
  }

  auto* request_object = rex::memory::GuestPtr<uint8_t*>(base, request_guest);
  std::memset(request_object, 0, 32);
  StoreGuestU16(base, request_guest + 4, 0);
  StoreGuestU32(base, request_guest + 20, 0);
  StoreGuestU32(base, request_guest + 24, 7);

  const bool attached = CallGuestVTableSlot(source_ctx,
                                           base,
                                           primary_resource,
                                           36,
                                           alias_guest,
                                           request_guest,
                                           2,
                                           1,
                                           0x82374878);

  PPCContext destroy_request_ctx = source_ctx;
  destroy_request_ctx.r3.u32 = request_guest;
  destroy_request_ctx.lr = 0x82374880;
  sub_822F22B8(destroy_request_ctx, base);
  CallLhFree(source_ctx, base, request_guest);

  CallGuestVTableSlot(source_ctx,
                      base,
                      alias_guest,
                      8,
                      0,
                      0,
                      0,
                      0,
                      0x8237489C);
  return attached;
}

bool IsReadableAssetChar(char ch) {
  const unsigned char value = static_cast<unsigned char>(ch);
  return std::isalnum(value) || ch == '/' || ch == '\\' || ch == '.' ||
         ch == '_' || ch == '-' || ch == ':' || ch == ' ' || ch == '@' ||
         ch == '[' || ch == ']' || ch == '*' || ch == '?' || ch == '(' ||
         ch == ')' || ch == ',' || ch == '\'' || ch == '"' || ch == '=';
}

std::string ReadGuestWideStringObject(uint8_t* base,
                                      uint32_t object_guest,
                                      size_t max_chars) {
  if (base == nullptr || object_guest == 0) {
    return {};
  }

  auto read_wide = [&](uint32_t data_guest, uint32_t length) -> std::string {
    if (data_guest == 0 || length == 0 || length > max_chars ||
        length > 0x100000) {
      return {};
    }

    std::string value;
    value.reserve(length);
    for (uint32_t index = 0; index < length; ++index) {
      const uint16_t code_unit = LoadGuestU16(
          base, data_guest + static_cast<uint32_t>(index * 2));
      if (code_unit == 0) {
        break;
      }
      if (code_unit > 0x7F) {
        return {};
      }
      const char ch = static_cast<char>(code_unit);
      if (!IsReadableAssetChar(ch)) {
        return {};
      }
      value.push_back(ch);
    }
    return value;
  };

  auto read_wide_cstr = [&](uint32_t data_guest) -> std::string {
    if (data_guest == 0) {
      return {};
    }

    std::string value;
    value.reserve(std::min<size_t>(max_chars, 256));
    for (size_t index = 0; index < max_chars; ++index) {
      const uint16_t code_unit = LoadGuestU16(
          base, data_guest + static_cast<uint32_t>(index * 2));
      if (code_unit == 0) {
        break;
      }
      if (code_unit > 0x7F) {
        return {};
      }
      const char ch = static_cast<char>(code_unit);
      if (!IsReadableAssetChar(ch)) {
        return {};
      }
      value.push_back(ch);
    }
    return value;
  };

  const uint32_t ref_string_guest = LoadGuestU32(base, object_guest);
  if (ref_string_guest >= 0x10000 && ref_string_guest < 0x90000000) {
    const uint32_t data_guest = LoadGuestU32(base, ref_string_guest);
    const uint32_t length = LoadGuestU32(base, ref_string_guest + 4);
    std::string value = read_wide(data_guest, length);
    if (!value.empty()) {
      return value;
    }

    value = read_wide_cstr(ref_string_guest);
    if (!value.empty()) {
      return value;
    }
  }

  std::string value =
      read_wide(LoadGuestU32(base, object_guest), LoadGuestU32(base, object_guest + 4));
  if (!value.empty()) {
    return value;
  }

  const uint32_t length = LoadGuestU32(base, object_guest + 20);
  const uint32_t capacity = LoadGuestU32(base, object_guest + 24);
  if (length == 0 || length > max_chars || capacity > 0x100000) {
    return read_wide_cstr(object_guest);
  }

  const uint32_t data_guest =
      capacity >= 8 ? LoadGuestU32(base, object_guest + 4) : object_guest + 4;
  if (data_guest == 0) {
    return {};
  }

  return read_wide(data_guest, length);
}

std::string ReadGuestAsciiStringObject(uint8_t* base,
                                       uint32_t object_guest,
                                       size_t max_chars) {
  if (base == nullptr || object_guest == 0) {
    return {};
  }

  auto read_ascii = [&](uint32_t data_guest, uint32_t length) -> std::string {
    if (data_guest == 0 || length == 0 || length > max_chars ||
        length > 0x100000) {
      return {};
    }

    std::string value;
    value.reserve(length);
    for (uint32_t index = 0; index < length; ++index) {
      const char ch = static_cast<char>(LoadGuestU8(base, data_guest + index));
      if (ch == '\0') {
        break;
      }
      if (!IsReadableAssetChar(ch)) {
        return {};
      }
      value.push_back(ch);
    }
    return value;
  };

  auto read_ascii_cstr = [&](uint32_t data_guest) -> std::string {
    if (data_guest == 0) {
      return {};
    }

    std::string value;
    value.reserve(std::min<size_t>(max_chars, 256));
    for (size_t index = 0; index < max_chars; ++index) {
      const char ch =
          static_cast<char>(LoadGuestU8(base, data_guest + index));
      if (ch == '\0') {
        break;
      }
      if (!IsReadableAssetChar(ch)) {
        return {};
      }
      value.push_back(ch);
    }
    return value;
  };

  const uint32_t ref_string_guest = LoadGuestU32(base, object_guest);
  if (ref_string_guest >= 0x10000 && ref_string_guest < 0x90000000) {
    const uint32_t data_guest = LoadGuestU32(base, ref_string_guest);
    const uint32_t length = LoadGuestU32(base, ref_string_guest + 4);
    std::string value = read_ascii(data_guest, length);
    if (!value.empty()) {
      return value;
    }
    value = read_ascii_cstr(data_guest);
    if (!value.empty()) {
      return value;
    }
  }

  const uint32_t length = LoadGuestU32(base, object_guest + 20);
  const uint32_t capacity = LoadGuestU32(base, object_guest + 24);
  if (length == 0 || length > max_chars || capacity > 0x100000) {
    return {};
  }

  const uint32_t data_guest =
      capacity >= 16 ? LoadGuestU32(base, object_guest + 4) : object_guest + 4;
  std::string value = read_ascii(data_guest, length);
  if (!value.empty()) {
    return value;
  }
  return read_ascii_cstr(data_guest);
}

std::string ReadGuestWideCString(uint8_t* base,
                                 uint32_t string_guest,
                                 size_t max_chars) {
  if (base == nullptr || string_guest == 0) {
    return {};
  }

  std::string value;
  value.reserve(std::min<size_t>(max_chars, 256));
  for (size_t index = 0; index < max_chars; ++index) {
    const uint16_t code_unit = LoadGuestU16(
        base, string_guest + static_cast<uint32_t>(index * sizeof(uint16_t)));
    if (code_unit == 0) {
      break;
    }
    if (code_unit > 0x7F) {
      return {};
    }
    const char ch = static_cast<char>(code_unit);
    if (!IsReadableAssetChar(ch)) {
      return {};
    }
    value.push_back(ch);
  }
  return value;
}

bool WriteGuestWideStringObject(uint8_t* base,
                                uint32_t object_guest,
                                uint32_t data_guest,
                                std::string_view value) {
  if (base == nullptr || object_guest == 0 || data_guest == 0 ||
      value.size() > 2047) {
    return false;
  }

  for (uint32_t offset = 0; offset < 32; offset += 4) {
    StoreGuestU32(base, object_guest + offset, 0);
  }

  for (size_t index = 0; index < value.size(); ++index) {
    const unsigned char ch = static_cast<unsigned char>(value[index]);
    if (ch > 0x7F) {
      return false;
    }
    StoreGuestU16(base,
                  data_guest + static_cast<uint32_t>(index * 2),
                  static_cast<uint16_t>(ch));
  }
  StoreGuestU16(base,
                data_guest + static_cast<uint32_t>(value.size() * 2),
                0);

  StoreGuestU32(base, object_guest + 4, data_guest);
  StoreGuestU32(base, object_guest + 20, static_cast<uint32_t>(value.size()));
  StoreGuestU32(base, object_guest + 24, std::max<uint32_t>(
                                      8, static_cast<uint32_t>(value.size())));
  StoreGuestU32(base, object_guest + 28, 0);
  return true;
}

bool IsOutputStreamSet(uint8_t* base, uint32_t out_guest) {
  return base != nullptr && out_guest != 0 && LoadGuestU32(base, out_guest) != 0;
}

std::string FormatGuestWords(uint8_t* base, uint32_t guest, uint32_t word_count) {
  if (base == nullptr || guest == 0) {
    return {};
  }

  std::ostringstream stream;
  for (uint32_t index = 0; index < word_count; ++index) {
    if (index != 0) {
      stream << ' ';
    }
    stream << fmt::format("0x{:08X}",
                          LoadGuestU32(base, guest + index * sizeof(uint32_t)));
  }
  return stream.str();
}

std::string JoinStrings(const std::vector<std::string>& values,
                        std::string_view separator) {
  std::ostringstream stream;
  for (size_t index = 0; index < values.size(); ++index) {
    if (index != 0) {
      stream << separator;
    }
    stream << values[index];
  }
  return stream.str();
}

std::string FormatGuestAsciiBytes(uint8_t* base,
                                  uint32_t guest,
                                  uint32_t max_chars) {
  if (base == nullptr || guest == 0) {
    return {};
  }

  std::string value;
  value.reserve(max_chars);
  for (uint32_t index = 0; index < max_chars; ++index) {
    const char ch = static_cast<char>(LoadGuestU8(base, guest + index));
    if (ch == '\0') {
      break;
    }
    if (!IsReadableAssetChar(ch)) {
      return {};
    }
    value.push_back(ch);
  }
  return value;
}

std::string ReadBestEffortGuestString(uint8_t* base,
                                      uint32_t guest,
                                      uint32_t max_chars) {
  std::string value = ReadGuestWideStringObject(base, guest, max_chars);
  if (!value.empty()) {
    return value;
  }
  value = ReadGuestAsciiStringObject(base, guest, max_chars);
  if (!value.empty()) {
    return value;
  }
  return FormatGuestAsciiBytes(base, guest, max_chars);
}

std::string FormatGuestObjectVTable(uint8_t* base,
                                    uint32_t object_guest,
                                    uint32_t slot_count) {
  if (base == nullptr || object_guest == 0) {
    return {};
  }

  const uint32_t vtable_guest = LoadGuestU32(base, object_guest);
  if (vtable_guest == 0) {
    return {};
  }

  return fmt::format("vtable=0x{:08X} slots={}",
                     vtable_guest,
                     FormatGuestWords(base, vtable_guest, slot_count));
}

bool ShouldLogVfsTraceOperation(std::string_view api) {
  if (!VfsTracingEnabled()) {
    return false;
  }
  if (RefiiStartupVideosPlaybackCriticalActive()) {
    return false;
  }

  static std::mutex mutex;
  static std::unordered_map<std::string, uint32_t> counts;

  std::lock_guard<std::mutex> lock(mutex);
  const uint32_t next_count = ++counts[std::string(api)];
  return next_count <= 8 || (next_count & (next_count - 1)) == 0;
}

bool ShouldLogRepeatedVfsTraceOperation(std::string_view api,
                                        uint32_t limit = 8) {
  if (!VfsTracingEnabled()) {
    return false;
  }
  if (RefiiStartupVideosPlaybackCriticalActive()) {
    return false;
  }

  static std::mutex mutex;
  static std::unordered_map<std::string, uint32_t> counts;

  std::lock_guard<std::mutex> lock(mutex);
  const uint32_t next_count = ++counts[std::string(api)];
  return next_count <= limit;
}

std::string DescribeGuestObject(uint8_t* base,
                                uint32_t object_guest,
                                uint32_t word_count = 8,
                                uint32_t slot_count = 8) {
  if (base == nullptr || object_guest == 0) {
    return "0x00000000";
  }

  return fmt::format("0x{:08X} words={} {}",
                     object_guest,
                     FormatGuestWords(base, object_guest, word_count),
                     FormatGuestObjectVTable(base, object_guest, slot_count));
}

std::string ReadBankDebugName(uint8_t* base, uint32_t bank_guest) {
  if (base == nullptr || bank_guest == 0) {
    return {};
  }
  return ReadGuestWideStringObject(base, bank_guest + 8, 4096);
}

std::string ReadMaybeGuestString(uint8_t* base,
                                 uint32_t guest,
                                 uint32_t max_chars = 4096) {
  if (base == nullptr || guest < 0x10000 || guest >= 0x90000000) {
    return {};
  }
  return ReadBestEffortGuestString(base, guest, max_chars);
}

std::string KnownGuiCommandForCallerLr(uint32_t lr) {
  switch (lr) {
  case 0x823DCCACu:
    return "GUI:SetAsNotRunningInAnarkStudio()";
  case 0x823DCD2Cu:
    return "GUI:StoreScriptsDirectoryAndLoadAppropriateScripts()";
  case 0x823DCD68u:
    return "GUI:SetToDisplayInWidescreen()";
  default:
    return {};
  }
}

bool IsTraceInterestingAsset(std::string_view asset_path) {
  const std::string normalized = NormalizeLooseAssetKey(asset_path);
  auto ends_with = [&normalized](std::string_view suffix) {
    return normalized.size() >= suffix.size() &&
           normalized.substr(normalized.size() - suffix.size()) == suffix;
  };
  return normalized.find("gameface") != std::string::npos ||
         normalized.find("frontendstartupscreen") != std::string::npos ||
         normalized.find("videos/") != std::string::npos ||
         normalized.find("speech.bnk") != std::string::npos ||
         normalized.find("lipsync.bnk") != std::string::npos ||
         normalized == "dir.manifest" ||
         normalized == "startup.vfsconfig" ||
         normalized == "gold_version.txt" ||
         ends_with(".bik") || ends_with(".bgf") || ends_with(".bsg") ||
         ends_with(".tex") || ends_with(".vfsconfig") ||
         ends_with(".fac");
}

bool IsMainMenuTextureTraceAsset(std::string_view asset_path) {
  const std::string normalized = NormalizeLooseAssetKey(asset_path);
  if (normalized.size() < 4 ||
      normalized.substr(normalized.size() - 4) != ".tex") {
    return false;
  }
  return normalized.find("icon_button_a.tex") != std::string::npos ||
	         normalized.find("fe_f2logo.tex") != std::string::npos ||
	         normalized.find("buttondetailmap.tex") != std::string::npos ||
	         normalized.find("framedetailmap.tex") != std::string::npos ||
	         normalized.find("frames_01.tex") != std::string::npos ||
	         normalized.find("panoramich.tex") != std::string::npos ||
	         normalized.find("loading_animation.tex") != std::string::npos;
}

bool IsMainMenuTextureHashTraceAsset(std::string_view asset_path) {
  const std::string normalized = NormalizeLooseAssetKey(asset_path);
  return IsMainMenuTextureTraceAsset(normalized) ||
         normalized.find("fxt_soft_glow.tex") != std::string::npos;
}

bool ShouldAllowOwnerlessSplitTextureWrapperCombine(std::string_view api,
                                                    std::string_view provider) {
  const std::string normalized_api = std::string(api);
  if (!IsTextureHeaderCandidate(provider)) {
    return false;
  }
  return normalized_api == "GetSize" || normalized_api == "GetBytes" ||
         normalized_api == "ResourceWrapper_Wait";
}

bool IsMainMenuTextureResourceHash(uint32_t resource_hash) {
  return resource_hash == 0x73BCB48Fu || resource_hash == 0xC5A8E73Cu;
}

bool IsBootFontResourceHash(uint32_t resource_hash) {
  switch (resource_hash) {
    case 0xD214633Eu: // language/fonts/couriernew_16.bft
    case 0x989D3506u: // language/en-uk/fonts/gui_small.bft
    case 0xC5616AFEu: // language/en-uk/fonts/gui_large.bft
      return true;
    default:
      return false;
  }
}

bool ShouldTracePostVideoFontCaller(uint32_t lr) {
  switch (lr) {
    case 0x8287006Cu:
    case 0x82870078u:
    case 0x82870080u:
    case 0x828716A8u:
    case 0x828716B4u:
    case 0x828716BCu:
    case 0x82871760u:
    case 0x8287176Cu:
    case 0x82871774u:
      return true;
    default:
      return false;
  }
}

std::string_view MainMenuTextureNameForResourceHash(uint32_t resource_hash) {
  switch (resource_hash) {
    case 0x73BCB48Fu:
      return "art/gui/frames/buttondetailmap.tex";
    case 0xC5A8E73Cu:
      return "art/gui/frames/framedetailmap.tex";
    default:
      return {};
  }
}

void LogRefiiVfsChainVerdict(std::string_view chain_id,
                             uint32_t root_lr,
                             std::string_view asset,
                             std::string_view candidate,
                             std::string_view provider_relative,
                             std::string_view shape_owner,
                             std::string_view live_boundary,
                             std::string_view collapse_status,
                             std::string_view next_boundary) {
  if (!VfsTracingEnabled() && !LooseAssetOverrideTracingEnabled()) {
    return;
  }
  if (RefiiStartupVideosPlaybackCriticalActive()) {
    return;
  }

  const std::string normalized_asset = NormalizeLooseAssetKey(asset);
  const std::string normalized_candidate = NormalizeLooseAssetKey(candidate);
  const std::string normalized_provider = NormalizeLooseAssetKey(provider_relative);
  const std::string key_asset =
      !normalized_asset.empty()
          ? normalized_asset
          : (!normalized_candidate.empty() ? normalized_candidate
                                           : normalized_provider);
  const bool interesting =
      IsTraceInterestingAsset(key_asset) ||
      IsTraceInterestingAsset(normalized_candidate) ||
      IsTraceInterestingAsset(normalized_provider) ||
      chain_id == "lua" || chain_id == "native_shaped_stream" ||
      chain_id == "request_manager_hash";

  static std::mutex mutex;
  static std::unordered_set<std::string> emitted;
  {
    std::lock_guard<std::mutex> lock(mutex);
    const std::string key = fmt::format("{}|{}|{}|{}|{}",
                                        chain_id,
                                        key_asset,
                                        live_boundary,
                                        collapse_status,
                                        next_boundary);
    if (!emitted.insert(key).second) {
      return;
    }
    constexpr size_t kMaxChainVerdicts = 512;
    if (emitted.size() > kMaxChainVerdicts) {
      emitted.clear();
      emitted.insert(key);
    }
  }

  if (!interesting &&
      !ShouldLogRepeatedVfsTraceOperation("RefiiVfsChainVerdictGeneric", 96)) {
    return;
  }

  REXLOG_INFO("Refii VFS chain verdict chain={} lr=0x{:08X} asset={} "
              "candidate={} provider_relative={} shape_owner={} boundary={} "
              "status={} next={}",
              chain_id,
              root_lr,
              normalized_asset,
              normalized_candidate,
              normalized_provider,
              shape_owner,
              live_boundary,
              collapse_status,
              next_boundary);
}

bool IsResourceWrapperTextureTraceAsset(std::string_view asset_path) {
  const std::string normalized = NormalizeLooseAssetKey(asset_path);
  return IsMainMenuTextureTraceAsset(normalized) ||
         IsTextureHeaderCandidate(normalized);
}

bool IsTextureResourceWrapperCaller(uint32_t lr) {
  return (lr >= 0x82A7A000 && lr < 0x82A7C000) ||
         (lr >= 0x82B6D000 && lr < 0x82B6E800);
}

bool ShouldTraceNativeResourceWrapperApi(std::string_view api, uint32_t lr) {
  if (!VfsTracingEnabled() || !IsTextureResourceWrapperCaller(lr)) {
    return false;
  }
  return ShouldLogRepeatedVfsTraceOperation(
      fmt::format("NativeResourceWrapper{}", api), 32);
}

bool IsGameFaceBoundaryPath(std::string_view asset_path) {
  const std::string normalized = NormalizeLooseAssetKey(asset_path);
  return normalized.find("scripts/gameface") != std::string::npos ||
         normalized.find("guiscripts") != std::string::npos ||
         normalized.find("art/gui/gameface") != std::string::npos ||
         normalized.find("frontendstartupscreen") != std::string::npos;
}

bool ShouldLogFileApiPath(std::string_view api, std::string_view path) {
  if (RefiiStartupVideosPlaybackCriticalActive()) {
    return false;
  }
  if (IsGameFaceBoundaryPath(path)) {
    return VfsTracingEnabled();
  }
  if (!path.empty() && IsTraceInterestingAsset(path)) {
    return false;
  }
  return VfsTracingEnabled() && ShouldLogVfsTraceOperation(api);
}

bool LooksLikeConcreteVfsFilePath(std::string_view asset_path) {
  const std::string normalized = NormalizeLooseAssetKey(asset_path);
  if (normalized.empty() || normalized.back() == '/') {
    return false;
  }

  const size_t separator = normalized.find_last_of('/');
  const std::string_view basename =
      separator == std::string::npos
          ? std::string_view(normalized)
          : std::string_view(normalized).substr(separator + 1);
  return basename.find('.') != std::string_view::npos;
}

bool ShouldTraceMeshResourceValidate(uint32_t link_register) {
  if (!kTraceMeshModelAudioDiagnostics) {
    return false;
  }
  switch (link_register) {
    case 0x821F5CFC:
    case 0x8237478C:
    case 0x82B60988:
    case 0x82B708A4:
      return true;
    default:
      return false;
  }
}

bool IsSupportedDispatchHeaderType(uint8_t type) {
  switch (type) {
    case 0:
    case 1:
    case 2:
    case 5:
      return true;
    default:
      return false;
  }
}

void SanitizeCriticalSectionBeforeEnter(uint8_t* base,
                                        uint32_t critical_section_guest,
                                        uint32_t link_register) {
  if (base == nullptr || critical_section_guest < 0x10000 ||
      critical_section_guest >= 0x90000000) {
    return;
  }

  const uint8_t type = LoadGuestU8(base, critical_section_guest);
  if (IsSupportedDispatchHeaderType(type)) {
    return;
  }

  const uint8_t absolute = LoadGuestU8(base, critical_section_guest + 1);
  const uint32_t signal_state = LoadGuestU32(base, critical_section_guest + 4);
  const uint32_t lock_count = LoadGuestU32(base, critical_section_guest + 0x10);
  const uint32_t recursion_count =
      LoadGuestU32(base, critical_section_guest + 0x14);
  const uint32_t owning_thread =
      LoadGuestU32(base, critical_section_guest + 0x18);

  // Some game-owned critical sections live in embedded data and may not pass
  // through rexglue's initializer before contention. When the dispatcher type
  // is invalid, the rest of the structure has also proven to be raw embedded
  // bytes, so initialize the complete critical section before the first enter.
  StoreGuestU8(base, critical_section_guest, 1);
  StoreGuestU8(base,
               critical_section_guest + 1,
               absolute != 0 ? absolute : static_cast<uint8_t>(96));
  StoreGuestU32(base, critical_section_guest + 4, 0);
  StoreGuestU32(base, critical_section_guest + 0x10, 0xFFFFFFFF);
  StoreGuestU32(base, critical_section_guest + 0x14, 0);
  StoreGuestU32(base, critical_section_guest + 0x18, 0);

  static std::atomic<uint64_t> sanitized_count{0};
  const uint64_t count =
      sanitized_count.fetch_add(1, std::memory_order_relaxed) + 1;
  if (VfsTracingEnabled() && (count <= 32 || (count & (count - 1)) == 0)) {
    REXLOG_WARN("VFS trace sanitized embedded critical section "
                "cs=0x{:08X} old_type={} old_absolute={} old_signal={} "
                "lock_count=0x{:08X} recursion={} owner=0x{:08X} "
                "lr=0x{:08X} count={}",
                critical_section_guest,
                type,
                absolute,
                signal_state,
                lock_count,
                recursion_count,
                owning_thread,
                link_register,
                count);
  }
}

}  // namespace detail
}  // namespace refii::vfs
