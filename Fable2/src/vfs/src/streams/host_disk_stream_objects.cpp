#include "vfs_hooks_private.h"
#include "core/guest_build.h"

namespace refii::vfs {
inline namespace detail {

namespace {
constexpr refii::guest::Addr kCreateEvent{0x82CC2CE0u};
// `0x822C9388` is PulseEvent, not SetEvent (no SetEvent symbol exists in the image). The guest
// signals request completion with PulseEvent too (`sub_82B5BC20`), but PulseEvent releases only
// threads already blocked, and the guest's waiter `sub_82B5BFF8` waits before it checks
// completion:
//
//     82b5c064  bl sub_82B4F400    ; wait
//     82b5c078  bctrl              ; IsComplete()
//     82b5c080  beq loc_82B5C058   ; loop
//
// Synchronous reads can therefore pulse before anyone waits; safe only because
// `TryHandleRefiiReadRequestWait` intercepts that waiter for our requests and never blocks.
constexpr refii::guest::Addr kPulseEvent{0x822C9388u};
constexpr refii::guest::Addr kCloseHandle{0x82CC27B0u};

uint32_t CreateReadRequestEvent(PPCContext& source_ctx, uint8_t* base) {
  PPCFunc* function = FindGuestFunction(kCreateEvent);
  if (function == nullptr) {
    return 0;
  }
  PPCContext call_ctx = source_ctx;
  call_ctx.r3.u32 = 0;
  call_ctx.r4.u32 = 0;
  call_ctx.r5.u32 = 0;
  call_ctx.r6.u32 = 0;
  call_ctx.ctr.u32 = kCreateEvent;
  function(call_ctx, base);
  return call_ctx.r3.u32;
}

void SignalReadRequestEvent(PPCContext& source_ctx,
                            uint8_t* base,
                            uint32_t request_guest) {
  const uint32_t event = LoadGuestU32(base, request_guest + 332);
  PPCFunc* function = event != 0 ? FindGuestFunction(kPulseEvent) : nullptr;
  if (function == nullptr) {
    return;
  }
  PPCContext call_ctx = source_ctx;
  call_ctx.r3.u32 = event;
  call_ctx.ctr.u32 = kPulseEvent;
  function(call_ctx, base);
}
}  // namespace

void CloseRefiiReadRequestEvent(PPCContext& source_ctx,
                                uint8_t* base,
                                uint32_t request_guest) {
  const uint32_t event = LoadGuestU32(base, request_guest + 332);
  PPCFunc* function = event != 0 ? FindGuestFunction(kCloseHandle) : nullptr;
  if (function == nullptr) {
    return;
  }
  StoreGuestU32(base, request_guest + 332, 0);
  PPCContext call_ctx = source_ctx;
  call_ctx.r3.u32 = event;
  call_ctx.ctr.u32 = kCloseHandle;
  function(call_ctx, base);
}

void StoreRefiiHostDiskStreamObject(uint8_t* base,
                                    uint32_t stream_guest,
                                    uint64_t size) {
  StoreGuestU32(base, stream_guest, kRefiiDiskStreamVTable);
  StoreGuestU32(base, stream_guest + 4, 0);
  StoreGuestU64(base, stream_guest + 8, 0);
  StoreGuestU64(base, stream_guest + 16, size);
  StoreGuestU32(base, stream_guest + 24, 0);
  StoreGuestU32(base, stream_guest + 28, 0);
}

uint32_t CreateRefiiHostDiskStream(PPCContext& ctx,
                                   uint8_t* base,
                                   uint32_t mode,
                                   const std::filesystem::path& loose_path,
                                   std::string_view provider_relative_path) {
  ZoneScopedN("RefiiVFS/CreateHostDiskStream");
  ConfigureRefiiVfsTracyPlots();
  const uint32_t open_mode = mode == 0 ? 1 : mode;
  if (!IsSupportedRefiiLooseOpenMode(open_mode)) {
    if (VfsTracingEnabled() || LooseAssetOverrideTracingEnabled()) {
      REXLOG_WARN("Refii loose asset open skipped: unsupported mode={} "
                  "path={} provider_relative={}",
                  open_mode,
                  loose_path.string(),
                  provider_relative_path);
    }
    return 0;
  }

  auto record = std::make_shared<RefiiHostStreamRecord>();
  record->path = loose_path;
  record->file.open(loose_path, std::ios::binary | std::ios::ate);
  if (!record->file) {
    REXLOG_WARN("Refii loose asset open failed: host file could not be opened "
                "path={}",
                loose_path.string());
    return 0;
  }
  const std::streamoff loose_size = record->file.tellg();
  if (loose_size < 0) {
    REXLOG_WARN("Refii loose asset open failed: size unavailable path={}",
                loose_path.string());
    return 0;
  }
  record->file.seekg(0, std::ios::beg);
  record->size = static_cast<uint64_t>(loose_size);

  const uint32_t stream_guest =
      CallLhMalloc(ctx, base, kRefiiHostDiskStreamSize);
  if (stream_guest == 0) {
    REXLOG_WARN("Refii loose asset open failed: could not allocate DiskStream "
                "object path={}",
                loose_path.string());
    return 0;
  }

  StoreRefiiHostDiskStreamObject(base, stream_guest, record->size);
  StoreRefiiHostStreamRecord(stream_guest, record);
  RecordLooseStream(base,
                    stream_guest,
                    loose_path,
                    "refii",
                    std::string(provider_relative_path));

  if (ShouldLogLooseStreamOperation("RefiiHostStream_Open", stream_guest, 16)) {
    REXLOG_INFO("Refii host loose stream opened stream=0x{:08X} mode={} "
                "size={} active={} caller_lr=0x{:08X} caller_ctr=0x{:08X} "
                "path={} provider_relative={}",
                stream_guest,
                open_mode,
                record->size,
                RefiiHostStreamRecordCount(),
                ctx.lr,
                ctx.ctr.u32,
                loose_path.string(),
                provider_relative_path);
  }

  return stream_guest;
}

uint32_t CreateRefiiHostMemoryStream(PPCContext& ctx,
                                     uint8_t* base,
                                     uint32_t mode,
                                     std::string_view asset_key,
                                     std::string_view provider_relative_path) {
  ZoneScopedN("RefiiVFS/CreateHostMemoryStream");
  ConfigureRefiiVfsTracyPlots();
  const uint32_t open_mode = mode == 0 ? 1 : mode;
  if (!IsSupportedRefiiLooseOpenMode(open_mode)) {
    REXLOG_WARN("Refii Godot asset open skipped: unsupported mode={} asset={}",
                open_mode,
                asset_key);
    return 0;
  }

  auto record = std::make_shared<RefiiHostStreamRecord>();
  // The whole payload, once — see RefiiHostStreamRecord::bytes for why this is not per-read.
  if (!ReadHostAsset(asset_key, record->bytes)) {
    REXLOG_WARN("Refii Godot asset open failed: the host mount refused '{}'",
                asset_key);
    return 0;
  }

  record->host_served = true;
  record->size = record->bytes.size();

  const uint32_t stream_guest =
      CallLhMalloc(ctx, base, kRefiiHostDiskStreamSize);
  if (stream_guest == 0) {
    REXLOG_WARN("Refii Godot asset open failed: could not allocate DiskStream "
                "object asset={}",
                asset_key);
    return 0;
  }

  StoreRefiiHostDiskStreamObject(base, stream_guest, record->size);
  StoreRefiiHostStreamRecord(stream_guest, record);
  RecordLooseStream(base,
                    stream_guest,
                    std::filesystem::path(),
                    "godot",
                    std::string(provider_relative_path));

  // Deliberately not behind `ShouldLogLooseStreamOperation`, which is false unless a tracing
  // cvar is on: every stream states its open outcome.
  NoteRefiiStreamOpened("RefiiGodotStream_Open",
                        stream_guest,
                        LoadGuestU32(base, stream_guest),
                        record->size,
                        asset_key,
                        static_cast<uint32_t>(ctx.lr));

  return stream_guest;
}

uint32_t ReadRefiiHostStreamBytes(uint8_t* base,
                                  uint32_t stream_guest,
                                  uint32_t buffer_guest,
                                  uint64_t offset,
                                  uint32_t requested_bytes);

uint32_t ReadRefiiHostStreamToDestination(PPCContext& ctx,
                                          uint8_t* base,
                                          uint32_t stream_guest,
                                          uint32_t destination_guest,
                                          uint64_t offset,
                                          uint32_t requested_bytes);

uint32_t CreateDeferredReadRequest(PPCContext& ctx,
                                   uint8_t* base,
                                   uint32_t stream_guest,
                                   uint32_t buffer_guest,
                                   uint64_t offset,
                                   uint32_t requested_bytes,
                                   bool uses_stream_destination = false);

void StartRefiiReadRequestAsync(
    PPCContext ctx,
    uint8_t* base,
    uint32_t record_guest,
    const std::shared_ptr<RefiiReadRequestRecord>& record);

bool TryHandleRefiiReadRequestRelease(PPCContext& ctx,
                                      uint8_t* base,
                                      uint32_t request_guest);

void PerformRefiiReadRequestNow(
    PPCContext& ctx,
    uint8_t* base,
    const std::shared_ptr<RefiiReadRequestRecord>& record,
    uint32_t record_guest);

bool EnsureRefiiReadRequestCompleted(
    PPCContext& ctx,
    uint8_t* base,
    const std::shared_ptr<RefiiReadRequestRecord>& record,
    uint32_t record_guest,
    std::string_view api);

// Opens whichever side holds the bytes: a resolution reporting `host_served` has no host path,
// so opening it from disk fails even though the asset resolved.
uint32_t CreateRefiiHostStreamForResolution(
    PPCContext& ctx,
    uint8_t* base,
    uint32_t mode,
    const LooseAssetResolution& resolution,
    std::string_view provider_relative_path) {
  if (resolution.host_served) {
    return CreateRefiiHostMemoryStream(
        ctx, base, mode, resolution.normalized_key, provider_relative_path);
  }
  return CreateRefiiHostDiskStream(
      ctx, base, mode, resolution.host_path, provider_relative_path);
}

bool TryOpenLooseAssetResourceWrapper(
    PPCContext& ctx,
    uint8_t* base,
    uint32_t out_guest,
    uint32_t mode,
    const LooseAssetResolution& resolution,
    std::string_view provider_relative_path,
    uint8_t resource_flags,
    uint64_t read_offset = 0,
    uint32_t requested_size = 0) {
  if (base == nullptr || out_guest == 0) {
    return false;
  }

  const std::filesystem::path& loose_path = resolution.host_path;
  const uint32_t stream_guest = CreateRefiiHostStreamForResolution(
      ctx, base, mode, resolution, provider_relative_path);
  if (stream_guest == 0) {
    return false;
  }

  const auto stream_record = FindRefiiHostStreamRecord(stream_guest);
  if (stream_record == nullptr ||
      stream_record->size > std::numeric_limits<uint32_t>::max()) {
    EraseRefiiHostStreamRecord(stream_guest);
    EraseLooseStreamRecord(stream_guest);
    return false;
  }

  if (read_offset > stream_record->size) {
    EraseRefiiHostStreamRecord(stream_guest);
    EraseLooseStreamRecord(stream_guest);
    REXLOG_WARN("Refii loose asset open failed: ResourceManager read offset "
                "past EOF path={} offset={} size={}",
                loose_path.string(),
                read_offset,
                stream_record->size);
    return false;
  }

  const uint64_t available = stream_record->size - read_offset;
  const bool full_remaining_request =
      requested_size == 0 || requested_size == 0xFFFFFFFFu;
  const uint64_t requested =
      full_remaining_request ? available : requested_size;
  if (requested > available ||
      requested > std::numeric_limits<uint32_t>::max()) {
    EraseRefiiHostStreamRecord(stream_guest);
    EraseLooseStreamRecord(stream_guest);
    REXLOG_WARN("Refii loose asset open failed: ResourceManager read window "
                "outside host file path={} offset={} requested={} available={}",
                loose_path.string(),
                read_offset,
                requested,
                available);
    return false;
  }

  const uint32_t byte_count = static_cast<uint32_t>(requested);
  const uint32_t buffer_guest =
      CallLhMalloc(ctx, base, byte_count == 0 ? 1 : byte_count);
  if (buffer_guest == 0) {
    EraseRefiiHostStreamRecord(stream_guest);
    EraseLooseStreamRecord(stream_guest);
    REXLOG_WARN("Refii loose asset open failed: could not allocate ResourceManager "
                "buffer path={} size={}",
                loose_path.string(),
                byte_count);
    return false;
  }

  const uint32_t request_guest =
      CreateDeferredReadRequest(
          ctx, base, stream_guest, buffer_guest, read_offset, byte_count);
  if (request_guest == 0) {
    CallLhFree(ctx, base, buffer_guest);
    EraseRefiiHostStreamRecord(stream_guest);
    EraseLooseStreamRecord(stream_guest);
    REXLOG_WARN("Refii loose asset open failed: could not allocate ResourceManager "
                "read request path={}",
                loose_path.string());
    return false;
  }

  const uint32_t wrapper_guest =
      CallLhMalloc(ctx, base, kResourceStreamWrapperSize);
  if (wrapper_guest == 0) {
    CallLhFree(ctx, base, buffer_guest);
    TryHandleRefiiReadRequestRelease(ctx, base, request_guest);
    ReleaseGuestObject(ctx, base, stream_guest);
    REXLOG_WARN("Refii loose asset open failed: could not allocate ResourceManager "
                "stream wrapper path={}",
                loose_path.string());
    return false;
  }
  auto* wrapper = rex::memory::GuestPtr<uint8_t*>(base, wrapper_guest);
  std::memset(wrapper, 0, kResourceStreamWrapperSize);

  const auto [wrapper_vtable, wrapper_interface_vtable] =
      EnsureRefiiResourceWrapperVTables(ctx, base);
  StoreGuestU32(base, wrapper_guest + 0, wrapper_vtable);
  StoreGuestU32(base, wrapper_guest + 4, wrapper_interface_vtable);
  // Native ResourceManager wrappers are observed with refcount 2 when returned
  // from OpenInternal; mirroring that avoids an early release window while the
  // generated parser still owns the completed read request.
  StoreGuestU32(base, wrapper_guest + 8, 2);
  StoreGuestU32(base, wrapper_guest + 12, buffer_guest);
  StoreGuestU32(base, wrapper_guest + 16, request_guest);
  StoreGuestU32(base, wrapper_guest + 20, request_guest + 4);
  StoreGuestU8(base, wrapper_guest + 24, resource_flags);
  StoreGuestU8(base, wrapper_guest + 25, 0);
  StoreGuestU16(base, wrapper_guest + 26, 0);
  StoreGuestU32(base, wrapper_guest + 28, 0);
  StoreGuestU32(base, out_guest, wrapper_guest);
  RecordLooseStream(base,
                    wrapper_guest,
                    loose_path,
                    "refii-resource",
                    std::string(provider_relative_path));

  const bool track_menu_texture =
      IsMainMenuTextureTraceAsset(provider_relative_path);
  std::string first_words;
  if (track_menu_texture) {
    first_words =
        VfsTracingEnabled()
            ? FormatHostFileFirstWords(ReadHostFileFirstWords(loose_path))
            : std::string{"suppressed"};
    RecordRefiiMainMenuBlockerEvent(
        "texture_selection",
        fmt::format("open provider={} wrapper=0x{:08X} stream=0x{:08X} "
                    "request=0x{:08X} buffer=0x{:08X} offset={} size={} words={}",
                    NormalizeLooseAssetKey(provider_relative_path),
                    wrapper_guest,
                    stream_guest,
                    request_guest,
                    buffer_guest,
                    read_offset,
                    byte_count,
                    first_words));
  }
  if (track_menu_texture && VfsTracingEnabled()) {
    REXLOG_INFO("Refii VFS ResourceManager texture wrapper open "
                "provider={} wrapper=0x{:08X} stream=0x{:08X} "
                "request=0x{:08X} buffer=0x{:08X} offset={} size={} first_words={} "
                "host={}",
                NormalizeLooseAssetKey(provider_relative_path),
                wrapper_guest,
                stream_guest,
                request_guest,
                buffer_guest,
                read_offset,
                byte_count,
                first_words,
                loose_path.string());
  }

  // The wrapper owns the read request; the request owns the backing stream.
  // Drop the creation reference so wrapper destruction can retire the whole
  // internal chain without leaving host stream records behind.
  ReleaseGuestObject(ctx, base, stream_guest);
  ctx.r3.u32 = out_guest;

  if (VfsVerboseIoTracingEnabled() &&
      (LooseAssetOverrideTracingEnabled() ||
       ShouldLogLooseStreamOperation("RefiiResourceStream_Open",
                                     wrapper_guest,
                                     16))) {
    REXLOG_INFO("Refii ResourceManager stream wrapper opened wrapper=0x{:08X} "
                "stream=0x{:08X} request=0x{:08X} buffer=0x{:08X} "
                "offset={} bytes={} pending=1 flags=0x{:02X} path={} "
                "provider_relative={} "
                "wrapper_words={} stream_words={} request_words={}",
                wrapper_guest,
                stream_guest,
                request_guest,
                buffer_guest,
                read_offset,
                byte_count,
                resource_flags,
                loose_path.string(),
                provider_relative_path,
                FormatGuestWords(base, wrapper_guest, 8),
                FormatGuestWords(base, stream_guest, 8),
                FormatGuestWords(base, request_guest, 8));
  }

  return true;
}

uint32_t CreateDeferredReadRequest(PPCContext& ctx,
                                   uint8_t* base,
                                   uint32_t stream_guest,
                                   uint32_t buffer_guest,
                                   uint64_t offset,
                                   uint32_t requested_bytes,
                                   bool uses_stream_destination) {
  ZoneScopedN("RefiiVFS/CreateDeferredReadRequest");
  ZoneValue(requested_bytes);
  ConfigureRefiiVfsTracyPlots();
  TracyPlot("Refii VFS Read Request Bytes",
            static_cast<int64_t>(requested_bytes));
  if (base == nullptr || stream_guest == 0 || buffer_guest == 0) {
    return 0;
  }

  const uint32_t request_guest =
      CallLhMalloc(ctx, base, kNativeReadRequestSize);
  if (request_guest == 0) {
    return 0;
  }

  auto* request = rex::memory::GuestPtr<uint8_t*>(base, request_guest);
  std::memset(request, 0, kNativeReadRequestSize);

  StoreGuestU32(base, request_guest + 0, kNativeReadRequestVTable);
  StoreGuestU32(base, request_guest + 4, kNativeReadRequestInterfaceVTable);
  StoreGuestU32(base, request_guest + 8, 1);
  StoreGuestU32(base, request_guest + 12, stream_guest);
  AddRefGuestObject(ctx, base, stream_guest);
  StoreGuestU32(base, request_guest + 20, 1);

  // Serve the bytes now and state them in the fields the guest reads. The object is a
  // `CWin32Request` (vtable at +0, interface vtable at +4, 344 bytes), so the guest's own
  // methods operate on it directly, offsets from the request:
  //
  //     +24   IsComplete()     -> `sub_8290DD10`: `return *(this+24)`
  //     +124  GetDataBuffer()  -> `sub_82B5C0C0`: sret = { *(this+124), *(this+128) }
  //     +200  bytes read       -> `sub_82B5C0D8`: `return *(this+200)`
  //
  // `rex_lhResourceManager_FindAndCreateStream` branches on that data pointer
  // (`0x82B53DF0 lwz r9, 0(r3)` / `0x82B53DF8 beq`): non-null builds the inert 16-byte
  // `CBasicCompleteRequest`, which forwards GetDataBuffer straight back to ours; null builds
  // the 28-byte `CBasicBufferedRequest`, which drives an asynchronous read through an operation
  // object we never supply and so waits for a completion that cannot arrive.
  //
  // The memspec is { buffer pointer, length }, and a non-null pointer is the caller's buffer:
  // the native ctor (`sub_82B5BCF0`) hands it to SetTargetBuffer (0x82B5C5F0), which does
  // `if (memspec.ptr) { *(QWORD*)(req+124) = memspec; arm the read }`. A null pointer means
  // "allocate one for me". So a supplied buffer is served in place and IS the data pointer;
  // only an unnamed one gets a payload of ours.
  uint32_t destination_ptr = 0;
  uint32_t destination_len = 0;
  if (uses_stream_destination && buffer_guest != 0) {
    destination_ptr = LoadGuestU32(base, buffer_guest);
    destination_len = LoadGuestU32(base, buffer_guest + 4);
  }

  uint32_t payload_guest = 0;
  uint32_t served = 0;
  if (destination_ptr != 0) {
    // The caller's length bounds the copy, never the request size alone: writing past a buffer
    // the guest sized is heap corruption, and an over-large ask gets a short read as natively.
    const uint32_t to_serve =
        destination_len != 0 ? std::min(requested_bytes, destination_len) : requested_bytes;
    served = ReadRefiiHostStreamBytes(base, stream_guest, destination_ptr, offset, to_serve);
    payload_guest = destination_ptr;
    static std::atomic<uint32_t> in_place{0};
    const uint32_t nth = in_place.fetch_add(1, std::memory_order_relaxed) + 1u;
    if (nth <= 8u || (nth & (nth - 1u)) == 0u) {
      REXLOG_INFO("Refii read request #{} served IN PLACE: stream=0x{:08X} caller buffer=0x{:08X} "
                  "len={} requested={} served={}",
                  nth, stream_guest, destination_ptr, destination_len, requested_bytes, served);
    }
  } else {
    const uint32_t payload_bytes = requested_bytes != 0 ? requested_bytes : 1u;
    payload_guest = CallLhMalloc(ctx, base, payload_bytes);
    if (payload_guest != 0) {
      served = ReadRefiiHostStreamBytes(base, stream_guest, payload_guest, offset,
                                        requested_bytes);
    }
  }

  StoreGuestU32(base, request_guest + 24, served != 0 ? 1u : 0u);
  StoreGuestU32(base, request_guest + 28, served != 0 ? 0u : 997u);
  StoreGuestU32(base, request_guest + 124, served != 0 ? payload_guest : 0u);
  StoreGuestU32(base, request_guest + 128, served);
  StoreGuestU32(base, request_guest + 168, 5);
  StoreGuestU32(base, request_guest + 172, 0);
  StoreGuestU32(base, request_guest + 176, 0);
  StoreGuestU64(base, request_guest + 192, requested_bytes);
  StoreGuestU32(base, request_guest + 200, served);

  if (served != requested_bytes) {
    REXLOG_WARN("Refii read request SHORT: request=0x{:08X} stream=0x{:08X} offset={} "
                "wanted={} served={} — a short read leaves the guest's refill loop asking "
                "for the remainder for ever.",
                request_guest, stream_guest, offset, requested_bytes, served);
  }

  // Dumps what the guest will parse, read back through the same base+VA the guest uses rather
  // than from our own buffer, so a wrong mapping shows up here too.
  {
    static std::atomic<uint32_t> dumped{0};
    if (dumped.fetch_add(1, std::memory_order_relaxed) < 4u) {
      uint32_t w[8] = {};
      for (uint32_t i = 0; i < 8; ++i) {
        w[i] = LoadGuestU32(base, payload_guest + i * 4u);
      }
      REXLOG_INFO("Refii read request PAYLOAD: request=0x{:08X} payload=0x{:08X} served={} "
                  "words[0..7]={:08X} {:08X} {:08X} {:08X} {:08X} {:08X} {:08X} {:08X} "
                  "dword@21=0x{:08X} req[+24]={} req[+124]=0x{:08X} req[+128]={} req[+200]={}",
                  request_guest, payload_guest, served,
                  w[0], w[1], w[2], w[3], w[4], w[5], w[6], w[7],
                  LoadGuestU32(base, payload_guest + 21u),
                  LoadGuestU32(base, request_guest + 24u),
                  LoadGuestU32(base, request_guest + 124u),
                  LoadGuestU32(base, request_guest + 128u),
                  LoadGuestU32(base, request_guest + 200u));
    }
  }
  StoreGuestU32(base, request_guest + 332,
                CreateReadRequestEvent(ctx, base));
  StoreGuestU32(base, request_guest + 340, 0);

  auto read_record = std::make_shared<RefiiReadRequestRecord>();
  read_record->request_guest = request_guest;
  read_record->stream_guest = stream_guest;
  read_record->buffer_guest = buffer_guest;
  read_record->offset = offset;
  read_record->requested_bytes = requested_bytes;
  read_record->bytes_read = 0;
  read_record->uses_stream_destination = uses_stream_destination;

  // Already served above into the buffer `GetDataBuffer` reports. Marking the record complete
  // stops the async path running a second read, or deferring one nobody will collect.
  read_record->completed = served != 0;
  read_record->attempted = served != 0;
  read_record->bytes_read = served;
  read_record->queued_at = std::chrono::steady_clock::now();
  read_record->started_at = read_record->queued_at;
  if (served != 0) {
    read_record->completed_at = read_record->queued_at;
  }
  StoreRefiiReadRequestRecord(request_guest, read_record);
  if (served == 0) {
    StartRefiiReadRequestAsync(ctx, base, request_guest, read_record);
  }

  if (LooseAssetOverrideTracingEnabled() ||
      ShouldLogLooseStreamOperation("RefiiHostReadRequest_Defer",
                                    stream_guest,
                                    16)) {
    REXLOG_INFO("Refii host loose stream deferred read request "
                "request=0x{:08X} stream=0x{:08X} buffer=0x{:08X} "
                "offset={} requested={} destination={} active_requests={} "
                "async=1 words={} {}",
                request_guest,
                stream_guest,
                buffer_guest,
                offset,
                requested_bytes,
                uses_stream_destination,
                RefiiReadRequestRecordCount(),
                FormatGuestWords(base, request_guest, 16),
                FormatGuestObjectVTable(base, request_guest, 8));
  }
  if (ShouldTraceStartupBikStreamShape(
          base, stream_guest, "BIKReadRequest_Defer", 6)) {
    REXLOG_INFO("VFS trace BIK read request defer lr=0x{:08X} ctr=0x{:08X} "
                "stream=0x{:08X} request=0x{:08X} buffer=0x{:08X} "
                "offset={} requested={} destination={} "
                "generated_shape=Buffered_Init_prebuffer_source_slot16 "
                "request_shape=[{}] stream_shape=[{}] destination_words={}",
                ctx.lr,
                ctx.ctr.u32,
                stream_guest,
                request_guest,
                buffer_guest,
                offset,
                requested_bytes,
                uses_stream_destination,
                FormatRefiiReadRequestShape(base, request_guest),
                FormatBikPpcStreamSummary(base, stream_guest),
                FormatGuestWordsIfPointer(base, buffer_guest, 4));
  }

  return request_guest;
}

bool TryOpenLooseAsset(PPCContext& ctx,
                       uint8_t* base,
                       uint32_t out_guest,
                       uint32_t manager_guest,
                       uint32_t mode,
                       const LooseAssetResolution& resolution,
                       std::string_view provider_relative_path = {}) {
  (void)manager_guest;

  const uint32_t open_mode = mode == 0 ? 1 : mode;
  if (base == nullptr || out_guest == 0) {
    return false;
  }

  const std::filesystem::path& loose_path = resolution.host_path;
  const uint32_t stream_guest = CreateRefiiHostStreamForResolution(
      ctx, base, open_mode, resolution, provider_relative_path);
  if (stream_guest == 0) {
    return false;
  }

  StoreGuestU32(base, out_guest, stream_guest);
  AddRefGuestObject(ctx, base, stream_guest);
  ctx.r3.u32 = out_guest;
  LogRefiiVfsChainVerdict("named_stream",
                          ctx.lr,
                          provider_relative_path,
                          loose_path.generic_string(),
                          provider_relative_path,
                          "Refii_host_stream",
                          "TryOpenLooseAsset",
                          "KeepHooked",
                          "stream_vtable_read_size_or_close");
  return true;
}

uint32_t ReadRefiiHostStreamBytes(uint8_t* base,
                                  uint32_t stream_guest,
                                  uint32_t buffer_guest,
                                  uint64_t offset,
                                  uint32_t requested_bytes) {
  ZoneScopedN("RefiiVFS/ReadHostStreamBytes");
  ZoneValue(requested_bytes);
  ConfigureRefiiVfsTracyPlots();
  TracyPlot("Refii VFS Read Request Bytes",
            static_cast<int64_t>(requested_bytes));
  if (base == nullptr || buffer_guest == 0) {
    return 0;
  }

  const auto record = FindRefiiHostStreamRecord(stream_guest);
  if (record == nullptr) {
    return 0;
  }

  std::lock_guard<std::mutex> lock(record->mutex);
  if (offset > record->size) {
    return 0;
  }

  requested_bytes = static_cast<uint32_t>(
      std::min<uint64_t>(requested_bytes, record->size - offset));
  if (requested_bytes == 0) {
    record->position = offset;
    return 0;
  }

  // Godot's bytes are already resident — no file to seek. Copied page by page because a guest
  // buffer is only guaranteed contiguous within a page.
  if (record->host_served) {
    uint32_t copied = 0;
    while (copied < requested_bytes) {
      const uint32_t guest = buffer_guest + copied;
      const uint32_t page_remaining = 0x1000u - (guest & 0x0FFFu);
      const uint32_t copy_size =
          std::min<uint32_t>(requested_bytes - copied, page_remaining);
      std::memcpy(rex::memory::GuestPtr<uint8_t*>(base, guest),
                  record->bytes.data() + offset + copied,
                  copy_size);
      copied += copy_size;
    }
    record->position = offset + copied;
    ++record->read_calls;
    record->read_bytes += copied;
    ZoneValue(copied);
    TracyPlot("Refii VFS Read Completed Bytes", static_cast<int64_t>(copied));
    return copied;
  }

  record->file.clear();
  record->file.seekg(static_cast<std::streamoff>(offset), std::ios::beg);

  uint32_t total_read = 0;
  std::array<uint8_t, 64 * 1024> scratch{};
  while (total_read < requested_bytes) {
    const uint32_t remaining = requested_bytes - total_read;
    const uint32_t chunk_size =
        std::min<uint32_t>(remaining, static_cast<uint32_t>(scratch.size()));
    record->file.read(reinterpret_cast<char*>(scratch.data()),
                      static_cast<std::streamsize>(chunk_size));
    const uint32_t read =
        static_cast<uint32_t>(std::max<std::streamsize>(record->file.gcount(), 0));
    if (read == 0) {
      break;
    }

    uint32_t copied = 0;
    while (copied < read) {
      const uint32_t guest = buffer_guest + total_read + copied;
      const uint32_t page_remaining = 0x1000u - (guest & 0x0FFFu);
      const uint32_t copy_size = std::min<uint32_t>(read - copied, page_remaining);
      std::memcpy(rex::memory::GuestPtr<uint8_t*>(base, guest),
                  scratch.data() + copied,
                  copy_size);
      copied += copy_size;
    }
    total_read += read;

    if (read != chunk_size) {
      break;
    }
  }

  record->position = offset + total_read;
  ++record->read_calls;
  record->read_bytes += total_read;
  ZoneValue(total_read);
  TracyPlot("Refii VFS Read Completed Bytes",
            static_cast<int64_t>(total_read));
  return total_read;
}

uint32_t ReadRefiiHostStreamForReadRequest(
    PPCContext& ctx,
    uint8_t* base,
    const std::shared_ptr<RefiiReadRequestRecord>& record) {
  if (record == nullptr) {
    return 0;
  }

  if (!record->uses_stream_destination) {
    return ReadRefiiHostStreamBytes(base,
                                    record->stream_guest,
                                    record->buffer_guest,
                                    record->offset,
                                    record->requested_bytes);
  }

  if (base == nullptr || record->buffer_guest == 0) {
    return 0;
  }

  const uint32_t destination_base = LoadGuestU32(base, record->buffer_guest);
  const uint32_t destination_kind = LoadGuestU32(base, record->buffer_guest + 4);
  if (destination_base == 0 || destination_kind > 16) {
    REXLOG_WARN("Refii read request destination rejected "
                "stream=0x{:08X} destination=0x{:08X} base=0x{:08X} "
                "kind={} offset={} requested={}",
                record->stream_guest,
                record->buffer_guest,
                destination_base,
                destination_kind,
                record->offset,
                record->requested_bytes);
    return 0;
  }

  if (destination_kind == 0) {
    return ReadRefiiHostStreamBytes(base,
                                    record->stream_guest,
                                    destination_base,
                                    record->offset,
                                    record->requested_bytes);
  }

  return ReadRefiiHostStreamToDestination(ctx,
                                          base,
                                          record->stream_guest,
                                          record->buffer_guest,
                                          record->offset,
                                          record->requested_bytes);
}

void CompleteRefiiReadRequest(PPCContext& ctx,
                              uint8_t* base,
                              const std::shared_ptr<RefiiReadRequestRecord>& record,
                              uint32_t record_guest,
                              uint32_t bytes_read) {
  if (base == nullptr || record == nullptr || record_guest == 0) {
    return;
  }

  // Native NtReadFile completion reports status and transferred bytes
  // independently. A short read at EOF is STATUS_SUCCESS; Bink relies on that
  // final partial refill. Validate the transferred count against the stream's
  // remaining bytes instead of treating every short read as an I/O failure.
  bool io_succeeded = false;
  if (const auto stream_record =
          FindRefiiHostStreamRecord(record->stream_guest)) {
    std::lock_guard<std::mutex> stream_lock(stream_record->mutex);
    const uint64_t available =
        record->offset < stream_record->size
            ? stream_record->size - record->offset
            : 0u;
    const uint32_t expected_read = static_cast<uint32_t>(
        std::min<uint64_t>(available, record->requested_bytes));
    io_succeeded = bytes_read == expected_read;
  }

  bool count_completion = false;
  bool completed = false;
  std::chrono::steady_clock::time_point queued_at{};
  std::chrono::steady_clock::time_point started_at{};
  std::chrono::steady_clock::time_point completed_at{};
  {
    std::lock_guard<std::mutex> lock(record->mutex);
    if (record->attempted) {
      return;
    }
    record->attempted = true;
    record->bytes_read = bytes_read;
    record->completed = io_succeeded;
    record->completed_at = std::chrono::steady_clock::now();
    queued_at = record->queued_at;
    started_at = record->started_at;
    completed_at = record->completed_at;
    completed = record->completed;
    if (completed && !record->completion_counted) {
      record->completion_counted = true;
      count_completion = true;
    }
  }

  // Fields the guest reads back, offsets from the request: `GetDataBuffer` (0x82B5C0C0) returns
  // { *(this+124), *(this+128) } — pointer and length — and slot +24 (0x82B5C0D8) returns
  // *(this+200) as the byte count. The pointer is wherever ReadRefiiHostStreamForReadRequest
  // put the bytes: the caller's buffer, or the base of the destination it was asked to fill.
  uint32_t data_guest = record->buffer_guest;
  if (record->uses_stream_destination && record->buffer_guest != 0) {
    data_guest = LoadGuestU32(base, record->buffer_guest);
  }
  StoreGuestU32(base, record_guest + 24, completed ? 1 : 0);
  StoreGuestU32(base, record_guest + 28, completed ? 0 : 997);
  StoreGuestU32(base, record_guest + 124, completed ? data_guest : 0u);
  StoreGuestU32(base, record_guest + 128, bytes_read);
  StoreGuestU32(base, record_guest + 176, bytes_read);
  StoreGuestU32(base, record_guest + 200, bytes_read);
  SignalReadRequestEvent(ctx, base, record_guest);

  if (completed) {
    if (record->offset == 0 && record->buffer_guest != 0) {
      if (const auto stream_record =
              FindRefiiHostStreamRecord(record->stream_guest)) {
        std::lock_guard<std::mutex> stream_lock(stream_record->mutex);
        stream_record->materialized_buffer_guest = record->buffer_guest;
        stream_record->materialized_bytes = bytes_read;
      }
    }

    RecordRefiiStartupVideoReadComplete(base,
                                        record->stream_guest,
                                        record->requested_bytes,
                                        bytes_read);
  }

  if (count_completion) {
    LooseStats().read_requests_completed.fetch_add(1,
                                                   std::memory_order_relaxed);
    LooseStats().refii_owned_reads_completed.fetch_add(
        1, std::memory_order_relaxed);
  }

  if (LooseAssetOverrideTracingEnabled() ||
      ShouldLogLooseStreamOperation("ReadRequest_Complete", record_guest, 16)) {
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
    REXLOG_INFO("Refii read request async complete request=0x{:08X} "
                "stream=0x{:08X} buffer=0x{:08X} offset={} requested={} "
                "read={} completed={} queue_wait_ms={} read_ms={}",
                record_guest,
                record->stream_guest,
                record->buffer_guest,
                record->offset,
                record->requested_bytes,
                bytes_read,
                completed,
                queue_wait_ms,
                read_ms);
  }
  if (ShouldTraceStartupBikStreamShape(
          base, record->stream_guest, "BIKReadRequest_Complete", 6)) {
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
    REXLOG_INFO("VFS trace BIK read request complete request=0x{:08X} "
                "stream=0x{:08X} buffer=0x{:08X} offset={} requested={} "
                "read={} completed={} queue_wait_ms={} read_ms={} "
                "request_shape=[{}] stream_shape=[{}]",
                record_guest,
                record->stream_guest,
                record->buffer_guest,
                record->offset,
                record->requested_bytes,
                bytes_read,
                completed,
                queue_wait_ms,
                read_ms,
                FormatRefiiReadRequestShape(base, record_guest),
                FormatBikPpcStreamSummary(base, record->stream_guest));
  }
}
}  // namespace detail
}  // namespace refii::vfs
