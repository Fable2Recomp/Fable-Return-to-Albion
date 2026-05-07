#include "vfs_hooks_private.h"

namespace refii::vfs {
inline namespace detail {

std::shared_ptr<RefiiHostStreamRecord> FindRefiiHostStreamRecordOrInner(
    uint8_t* base,
    uint32_t stream_guest,
    uint32_t* out_record_stream_guest = nullptr) {
  if (const auto exact = FindRefiiHostStreamRecord(stream_guest)) {
    if (out_record_stream_guest != nullptr) {
      *out_record_stream_guest = stream_guest;
    }
    return exact;
  }

  if (base == nullptr || stream_guest < 0x10000 || stream_guest >= 0x90000000) {
    return {};
  }

  for (uint32_t offset = 0; offset < 96; offset += sizeof(uint32_t)) {
    const uint32_t inner_guest = LoadGuestU32(base, stream_guest + offset);
    if (const auto inner = FindRefiiHostStreamRecord(inner_guest)) {
      if (out_record_stream_guest != nullptr) {
        *out_record_stream_guest = inner_guest;
      }
      return inner;
    }
  }

  return {};
}

uint32_t ReadRefiiHostStreamToDestination(PPCContext& ctx,
                                          uint8_t* base,
                                          uint32_t stream_guest,
                                          uint32_t destination_guest,
                                          uint64_t offset,
                                          uint32_t requested_bytes) {
  ZoneScopedN("RefiiVFS/ReadHostStreamToDestination");
  ZoneValue(requested_bytes);
  ConfigureRefiiVfsTracyPlots();
  if (base == nullptr || stream_guest == 0 || destination_guest == 0) {
    return 0;
  }

  const uint32_t destination_base = LoadGuestU32(base, destination_guest);
  const uint32_t destination_kind = LoadGuestU32(base, destination_guest + 4);
  if (destination_base == 0 || destination_kind > 16) {
    REXLOG_WARN("Refii host loose stream destination read rejected "
                "stream=0x{:08X} destination=0x{:08X} base=0x{:08X} "
                "kind={} offset={} requested={}",
                stream_guest,
                destination_guest,
                destination_base,
                destination_kind,
                offset,
                requested_bytes);
    return 0;
  }

  constexpr uint32_t kScratchBytes = 64 * 1024;
  const uint32_t scratch_guest = CallLhMalloc(ctx, base, kScratchBytes);
  if (scratch_guest == 0) {
    REXLOG_WARN("Refii host loose stream destination read failed to allocate "
                "scratch buffer stream=0x{:08X} requested={}",
                stream_guest,
                requested_bytes);
    return 0;
  }

  uint32_t total_read = 0;
  while (total_read < requested_bytes) {
    const uint32_t chunk =
        std::min<uint32_t>(requested_bytes - total_read, kScratchBytes);
    const uint32_t read = ReadRefiiHostStreamBytes(
        base, stream_guest, scratch_guest, offset + total_read, chunk);
    if (read == 0) {
      break;
    }

    if (!CopyToStreamDestination(ctx,
                                 base,
                                 destination_base + total_read,
                                 destination_kind,
                                 scratch_guest,
                                 read)) {
      break;
    }

    total_read += read;
    if (read != chunk) {
      break;
    }
  }

  CallLhFree(ctx, base, scratch_guest);
  ZoneValue(total_read);
  TracyPlot("Refii VFS Read Completed Bytes",
            static_cast<int64_t>(total_read));
  return total_read;
}

uint32_t NormalizeRefiiResourceStreamWrapper(uint8_t* base,
                                             uint32_t wrapper_guest) {
  if (base == nullptr || wrapper_guest < 0x10000 || wrapper_guest >= 0x90000000) {
    return 0;
  }

  if (IsRefiiResourceWrapperPrimaryVTable(LoadGuestU32(base, wrapper_guest)) &&
      (IsRefiiHostDiskStream(base, LoadGuestU32(base, wrapper_guest + 16)) ||
       IsRefiiReadRequestObject(LoadGuestU32(base, wrapper_guest + 16)))) {
    return wrapper_guest;
  }

  if (wrapper_guest >= 0x10004 &&
      IsRefiiResourceWrapperInterfaceVTable(
          LoadGuestU32(base, wrapper_guest)) &&
      IsRefiiResourceWrapperPrimaryVTable(
          LoadGuestU32(base, wrapper_guest - 4)) &&
      (IsRefiiHostDiskStream(base, LoadGuestU32(base, wrapper_guest + 12)) ||
       IsRefiiReadRequestObject(LoadGuestU32(base, wrapper_guest + 12)))) {
    return wrapper_guest - 4;
  }

  return 0;
}

bool IsRefiiResourceStreamWrapper(uint8_t* base, uint32_t wrapper_guest) {
  return NormalizeRefiiResourceStreamWrapper(base, wrapper_guest) != 0;
}

bool EnsureRefiiResourceWrapperMaterialized(PPCContext& ctx,
                                            uint8_t* base,
                                            uint32_t wrapper_guest) {
  wrapper_guest = NormalizeRefiiResourceStreamWrapper(base, wrapper_guest);
  if (wrapper_guest == 0) {
    return false;
  }

  const uint32_t stream_guest = LoadGuestU32(base, wrapper_guest + 16);
  if (const auto request = FindRefiiReadRequestRecord(stream_guest)) {
    EnsureRefiiReadRequestCompleted(
        base, request, request->request_guest, "ResourceWrapper_Materialize");
    uint32_t bytes_read = 0;
    {
      std::lock_guard<std::mutex> lock(request->mutex);
      bytes_read = request->bytes_read;
    }
    if (request->buffer_guest == 0 || bytes_read != request->requested_bytes) {
      return false;
    }
    if (const auto stream_record =
            FindRefiiHostStreamRecord(request->stream_guest)) {
      std::lock_guard<std::mutex> lock(stream_record->mutex);
      stream_record->materialized_buffer_guest = request->buffer_guest;
      stream_record->materialized_bytes = bytes_read;
    }
    StoreGuestU32(base, wrapper_guest + 12, request->buffer_guest);
    return true;
  }

  const auto record = FindRefiiHostStreamRecord(stream_guest);
  if (record == nullptr || record->size > std::numeric_limits<uint32_t>::max()) {
    return false;
  }

  std::lock_guard<std::mutex> lock(record->mutex);
  if (record->materialized_buffer_guest != 0) {
    StoreGuestU32(base, wrapper_guest + 12, record->materialized_buffer_guest);
    return true;
  }

  const uint32_t byte_count = static_cast<uint32_t>(record->size);
  const uint32_t buffer_guest = CallLhMalloc(ctx, base, byte_count == 0 ? 1 : byte_count);
  if (buffer_guest == 0) {
    REXLOG_WARN("Refii resource wrapper materialize failed: allocation failed "
                "wrapper=0x{:08X} stream=0x{:08X} size={} path={}",
                wrapper_guest,
                stream_guest,
                byte_count,
                record->path.string());
    return false;
  }

  auto* destination = rex::memory::GuestPtr<uint8_t*>(base, buffer_guest);
  record->file.clear();
  record->file.seekg(0, std::ios::beg);
  record->file.read(reinterpret_cast<char*>(destination),
                    static_cast<std::streamsize>(byte_count));
  const uint32_t read = static_cast<uint32_t>(record->file.gcount());
  if (read != byte_count) {
    REXLOG_WARN("Refii resource wrapper materialize failed: short read "
                "wrapper=0x{:08X} stream=0x{:08X} requested={} read={} path={}",
                wrapper_guest,
                stream_guest,
                byte_count,
                read,
                record->path.string());
    return false;
  }

  record->materialized_buffer_guest = buffer_guest;
  record->materialized_bytes = byte_count;
  StoreGuestU32(base, wrapper_guest + 12, buffer_guest);

  if (ShouldLogLooseStreamOperation("RefiiResourceWrapper_Materialize",
                                    wrapper_guest,
                                    16)) {
    REXLOG_INFO("Refii resource wrapper materialized wrapper=0x{:08X} "
                "stream=0x{:08X} buffer=0x{:08X} bytes={} path={}",
                wrapper_guest,
                stream_guest,
                buffer_guest,
                byte_count,
                record->path.string());
  }
  return true;
}

bool IsRefiiAnimationTocResource(std::string_view provider_key) {
  const std::string normalized = NormalizeLooseAssetKey(provider_key);
  return normalized == "fable2_anims.animation_toc" ||
         normalized == "data/fable2_anims.animation_toc" ||
         normalized == "game/data/fable2_anims.animation_toc" ||
         normalized.ends_with("/fable2_anims.animation_toc");
}

uint32_t GetAnimationTocPayloadOffset(uint8_t* base,
                                      uint32_t buffer_guest,
                                      uint32_t size,
                                      std::string_view provider_key) {
  if (!IsRefiiAnimationTocResource(provider_key) || base == nullptr ||
      buffer_guest == 0 || size < 12) {
    return 0;
  }

  constexpr char kAnimationTocMagic[] = "AnimBank";
  for (uint32_t index = 0; index < sizeof(kAnimationTocMagic) - 1; ++index) {
    if (LoadGuestU8(base, buffer_guest + index) !=
        static_cast<uint8_t>(kAnimationTocMagic[index])) {
      return 0;
    }
  }
  return 8;
}

RefiiResourceWrapperView GetRefiiResourceWrapperView(PPCContext& ctx,
                                                     uint8_t* base,
                                                     uint32_t wrapper_guest,
                                                     std::string_view api) {
  RefiiResourceWrapperView view;
  wrapper_guest = NormalizeRefiiResourceStreamWrapper(base, wrapper_guest);
  if (wrapper_guest == 0 ||
      !EnsureRefiiResourceWrapperMaterialized(ctx, base, wrapper_guest)) {
    return view;
  }

  const uint32_t stream_guest = LoadGuestU32(base, wrapper_guest + 16);
  uint32_t source_stream_guest = stream_guest;
  view.inner_guest = stream_guest;
  if (const auto request = FindRefiiReadRequestRecord(stream_guest)) {
    std::lock_guard<std::mutex> lock(request->mutex);
    source_stream_guest = request->stream_guest;
    view.buffer_guest = request->buffer_guest;
    view.size = request->bytes_read;
  } else if (const auto record = FindRefiiHostStreamRecord(stream_guest)) {
    std::lock_guard<std::mutex> lock(record->mutex);
    view.buffer_guest = record->materialized_buffer_guest;
    view.size = static_cast<uint32_t>(
        std::min<uint64_t>(record->materialized_bytes,
                           std::numeric_limits<uint32_t>::max()));
  }
  view.source_stream_guest = source_stream_guest;

  LooseStreamRecord loose_record;
  std::string match;
  if (FindLooseStreamRecord(base, wrapper_guest, &loose_record, &match) ||
      FindLooseStreamRecord(base, source_stream_guest, &loose_record, &match)) {
    view.provider_key = loose_record.provider_name.empty()
                            ? loose_record.path
                            : loose_record.provider_name;
  }

  MaybeAugmentRefiiSplitTextureWrapperPayloadView(
      ctx, base, wrapper_guest, api, &view);

  view.view_offset = GetAnimationTocPayloadOffset(
      base, view.buffer_guest, view.size, view.provider_key);
  if (view.view_offset != 0 && view.view_offset < view.size) {
    view.buffer_guest += view.view_offset;
    view.size -= view.view_offset;
    if (VfsTracingEnabled() ||
        ShouldLogRepeatedVfsTraceOperation("AnimationTocWrapperView", 16)) {
      REXLOG_INFO("Refii VFS animation TOC wrapper view api={} wrapper=0x{:08X} "
                  "inner=0x{:08X} source_stream=0x{:08X} provider={} "
                  "view_offset={} view_buffer=0x{:08X} view_size={} "
                  "first_words={}",
                  api,
                  wrapper_guest,
                  view.inner_guest,
                  source_stream_guest,
                  view.provider_key,
                  view.view_offset,
                  view.buffer_guest,
                  view.size,
                  FormatGuestWords(base, view.buffer_guest, 4));
    }
  }

  return view;
}

std::string GetRefiiResourceWrapperProviderKey(uint8_t* base,
                                               uint32_t wrapper_guest,
                                               uint32_t source_stream_guest) {
  LooseStreamRecord loose_record;
  std::string match;
  if (FindLooseStreamRecord(base, wrapper_guest, &loose_record, &match) ||
      FindLooseStreamRecord(base, source_stream_guest, &loose_record, &match)) {
    return loose_record.provider_name.empty() ? loose_record.path
                                             : loose_record.provider_name;
  }
  return {};
}

int32_t GetRefiiResourceWrapperStatusNonBlocking(uint8_t* base,
                                                 uint32_t wrapper_guest,
                                                 uint32_t* inner_guest_out,
                                                 uint32_t* source_stream_out,
                                                 uint32_t* requested_out,
                                                 uint32_t* bytes_out,
                                                 uint32_t* status_word_out,
                                                 uint32_t* buffer_out) {
  if (inner_guest_out != nullptr) {
    *inner_guest_out = 0;
  }
  if (source_stream_out != nullptr) {
    *source_stream_out = 0;
  }
  if (requested_out != nullptr) {
    *requested_out = 0;
  }
  if (bytes_out != nullptr) {
    *bytes_out = 0;
  }
  if (status_word_out != nullptr) {
    *status_word_out = 0;
  }
  if (buffer_out != nullptr) {
    *buffer_out = 0;
  }

  wrapper_guest = NormalizeRefiiResourceStreamWrapper(base, wrapper_guest);
  if (wrapper_guest == 0) {
    return 0;
  }

  const uint32_t inner_guest = LoadGuestU32(base, wrapper_guest + 16);
  if (inner_guest_out != nullptr) {
    *inner_guest_out = inner_guest;
  }

  if (const auto request = FindRefiiReadRequestRecord(inner_guest)) {
    std::shared_future<void> async_read;
    {
      std::lock_guard<std::mutex> lock(request->mutex);
      async_read = request->async_read;
    }
    if (async_read.valid() &&
        async_read.wait_for(std::chrono::seconds(0)) ==
            std::future_status::ready) {
      async_read.wait();
    }

    uint32_t requested = 0;
    uint32_t bytes = 0;
    uint32_t status_word = 0;
    bool completed = false;
    uint32_t source_stream = 0;
    uint32_t buffer_guest = 0;
    {
      std::lock_guard<std::mutex> lock(request->mutex);
      requested = request->requested_bytes;
      bytes = request->bytes_read;
      status_word = LoadGuestU32(base, inner_guest + 24);
      completed = request->completed;
      source_stream = request->stream_guest;
      buffer_guest = request->buffer_guest;
    }
    if (source_stream_out != nullptr) {
      *source_stream_out = source_stream;
    }
    if (requested_out != nullptr) {
      *requested_out = requested;
    }
    if (bytes_out != nullptr) {
      *bytes_out = bytes;
    }
    if (status_word_out != nullptr) {
      *status_word_out = status_word;
    }
    if (buffer_out != nullptr) {
      *buffer_out = buffer_guest;
    }
    return completed ? 1 : 0;
  }

  if (IsRefiiHostDiskStream(base, inner_guest)) {
    if (source_stream_out != nullptr) {
      *source_stream_out = inner_guest;
    }
    if (buffer_out != nullptr) {
      *buffer_out = LoadGuestU32(base, wrapper_guest + 12);
    }
    return LoadGuestU32(base, wrapper_guest + 12) != 0 ? 1 : 0;
  }

  return 0;
}

bool ShouldWarnCompletedMenuTextureStatus(uint32_t wrapper_guest) {
  static std::mutex mutex;
  static std::unordered_set<uint32_t> warned_wrappers;
  if (wrapper_guest == 0) {
    return false;
  }

  std::lock_guard<std::mutex> lock(mutex);
  return warned_wrappers.insert(wrapper_guest).second;
}

std::mutex& RefiiTextureWrapperOwnerMutex() {
  static std::mutex mutex;
  return mutex;
}

std::unordered_map<uint32_t, uint32_t>& RefiiTextureWrapperOwners() {
  static std::unordered_map<uint32_t, uint32_t> owners;
  return owners;
}

void RecordRefiiTextureWrapperOwner(uint32_t wrapper_guest,
                                    uint32_t texture_guest) {
  if (wrapper_guest == 0 || texture_guest == 0) {
    return;
  }

  std::lock_guard<std::mutex> lock(RefiiTextureWrapperOwnerMutex());
  RefiiTextureWrapperOwners()[wrapper_guest] = texture_guest;
}

void ForgetRefiiTextureWrapperOwner(uint32_t wrapper_guest) {
  if (wrapper_guest == 0) {
    return;
  }

  std::lock_guard<std::mutex> lock(RefiiTextureWrapperOwnerMutex());
  RefiiTextureWrapperOwners().erase(wrapper_guest);
}

uint32_t FindRefiiTextureWrapperOwner(uint32_t wrapper_guest) {
  if (wrapper_guest == 0) {
    return 0;
  }

  std::lock_guard<std::mutex> lock(RefiiTextureWrapperOwnerMutex());
  const auto& owners = RefiiTextureWrapperOwners();
  const auto iterator = owners.find(wrapper_guest);
  return iterator == owners.end() ? 0 : iterator->second;
}

std::mutex& TextureParserDescriptorTraceOwnerMutex() {
  static std::mutex mutex;
  return mutex;
}

std::unordered_map<uint32_t, TextureParserDescriptorTraceOwner>&
TextureParserDescriptorTraceOwners() {
  static std::unordered_map<uint32_t, TextureParserDescriptorTraceOwner> owners;
  return owners;
}

void RecordTextureParserDescriptorTraceOwner(uint32_t descriptor_guest,
                                             uint32_t texture_guest,
                                             uint32_t wrapper_guest,
                                             std::string_view provider) {
  if (descriptor_guest == 0 || texture_guest == 0) {
    return;
  }

  std::lock_guard<std::mutex> lock(TextureParserDescriptorTraceOwnerMutex());
  TextureParserDescriptorTraceOwners()[descriptor_guest] = {
      texture_guest, wrapper_guest, std::string(provider)};
}

std::optional<TextureParserDescriptorTraceOwner>
FindTextureParserDescriptorTraceOwner(uint32_t descriptor_guest) {
  if (descriptor_guest == 0) {
    return std::nullopt;
  }

  std::lock_guard<std::mutex> lock(TextureParserDescriptorTraceOwnerMutex());
  const auto& owners = TextureParserDescriptorTraceOwners();
  const auto iterator = owners.find(descriptor_guest);
  if (iterator == owners.end()) {
    return std::nullopt;
  }
  return iterator->second;
}

bool MaybeAugmentRefiiSplitTextureWrapperPayloadView(
    PPCContext& ctx,
    uint8_t* base,
    uint32_t wrapper_guest,
    std::string_view api,
    RefiiResourceWrapperView* view) {
  if (view == nullptr || base == nullptr || wrapper_guest == 0 ||
      view->buffer_guest == 0 ||
      !IsTextureHeaderCandidate(view->provider_key)) {
    return false;
  }

  const uint32_t owner_texture = FindRefiiTextureWrapperOwner(wrapper_guest);
  if (owner_texture == 0 &&
      !ShouldAllowOwnerlessSplitTextureWrapperCombine(api, view->provider_key)) {
    if (VfsTracingEnabled() &&
        IsResourceWrapperTextureTraceAsset(view->provider_key) &&
        ShouldLogRepeatedVfsTraceOperation("SplitTextureOwnerlessSkipped", 32)) {
      REXLOG_INFO("Refii VFS split texture wrapper ownerless combine skipped "
                  "api={} wrapper=0x{:08X} provider={}",
                  api,
                  wrapper_guest,
                  NormalizeLooseAssetKey(view->provider_key));
    }
    return false;
  }

  const bool view_is_header =
      view->size == 84 && LoadGuestU32(base, view->buffer_guest) == 0xFFFFFFFEu;
  const bool view_is_payload_window =
      view->size != 0 && LoadGuestU32(base, view->buffer_guest) != 0xFFFFFFFEu;
  if (!view_is_header && !view_is_payload_window) {
    return false;
  }

  std::array<uint8_t, 84> header_bytes{};
  uint32_t expected_payload_size = 0;
  uint32_t header_size = 84;
  if (view_is_header) {
    expected_payload_size = LoadGuestU32(base, view->buffer_guest + 4);
  } else {
    LooseAssetResolution header_resolution;
    std::string header_candidate;
    if (!MakeVfsDatabaseCandidateResolution(GetLooseAssetRoot(),
                                            view->provider_key,
                                            &header_resolution,
                                            &header_candidate)) {
      if (VfsTracingEnabled() &&
          ShouldLogRepeatedVfsTraceOperation("SplitTextureHeaderMissing", 32)) {
        REXLOG_WARN("Refii VFS split texture wrapper header missing "
                    "api={} wrapper=0x{:08X} owner=0x{:08X} provider={}",
                    api,
                    wrapper_guest,
                    owner_texture,
                    NormalizeLooseAssetKey(view->provider_key));
      }
      return false;
    }
    std::ifstream header_file(header_resolution.host_path, std::ios::binary);
    if (!header_file) {
      REXLOG_WARN("Refii VFS split texture wrapper header open failed "
                  "api={} wrapper=0x{:08X} owner=0x{:08X} provider={} host={}",
                  api,
                  wrapper_guest,
                  owner_texture,
                  NormalizeLooseAssetKey(view->provider_key),
                  header_resolution.host_path.string());
      return false;
    }
    header_file.read(reinterpret_cast<char*>(header_bytes.data()),
                     static_cast<std::streamsize>(header_bytes.size()));
    const uint32_t header_read = static_cast<uint32_t>(header_file.gcount());
    if (header_read != header_bytes.size()) {
      REXLOG_WARN("Refii VFS split texture wrapper header short read "
                  "api={} wrapper=0x{:08X} owner=0x{:08X} provider={} "
                  "host={} requested={} read={}",
                  api,
                  wrapper_guest,
                  owner_texture,
                  NormalizeLooseAssetKey(view->provider_key),
                  header_resolution.host_path.string(),
                  header_bytes.size(),
                  header_read);
      return false;
    }
    const std::array<uint32_t, 4> header_words =
        ReadHostFileFirstWords(header_resolution.host_path);
    expected_payload_size = header_words[1];
    if (header_words[0] != 0xFFFFFFFEu) {
      REXLOG_WARN("Refii VFS split texture wrapper header rejected "
                  "api={} wrapper=0x{:08X} owner=0x{:08X} provider={} "
                  "host={} first_words={}",
                  api,
                  wrapper_guest,
                  owner_texture,
                  NormalizeLooseAssetKey(view->provider_key),
                  header_resolution.host_path.string(),
                  FormatHostFileFirstWords(header_words));
      return false;
    }
  }
  if (expected_payload_size == 0 ||
      expected_payload_size >
          std::numeric_limits<uint32_t>::max() - header_size) {
    return false;
  }

  LooseAssetResolution payload_resolution;
  std::string payload_candidate;
  const bool need_payload_file = view_is_header;
  if (need_payload_file &&
      !ResolvePairedTexturePayloadForHeader(
          view->provider_key, &payload_resolution, &payload_candidate)) {
    if (VfsTracingEnabled() &&
        ShouldLogRepeatedVfsTraceOperation("SplitTexturePayloadMissing", 32)) {
      REXLOG_WARN("Refii VFS split texture wrapper payload missing "
                  "api={} wrapper=0x{:08X} owner=0x{:08X} provider={} "
                  "expected_payload_size={}",
                  api,
                  wrapper_guest,
                  owner_texture,
                  NormalizeLooseAssetKey(view->provider_key),
                  expected_payload_size);
    }
    return false;
  }

  uint32_t payload_size = view->size;
  if (need_payload_file) {
    std::error_code file_size_error;
    const uint64_t payload_size64 =
        std::filesystem::file_size(payload_resolution.host_path, file_size_error);
    if (file_size_error || payload_size64 == 0 ||
        payload_size64 > std::numeric_limits<uint32_t>::max() ||
        payload_size64 != expected_payload_size) {
      REXLOG_WARN("Refii VFS split texture wrapper payload rejected "
                  "api={} wrapper=0x{:08X} owner=0x{:08X} provider={} "
                  "payload={} host={} expected_payload_size={} actual_size={} "
                  "file_error={}",
                  api,
                  wrapper_guest,
                  owner_texture,
                  NormalizeLooseAssetKey(view->provider_key),
                  payload_candidate,
                  payload_resolution.host_path.string(),
                  expected_payload_size,
                  file_size_error ? 0 : payload_size64,
                  file_size_error.message());
      return false;
    }
    payload_size = static_cast<uint32_t>(payload_size64);
  } else if (payload_size != expected_payload_size) {
    REXLOG_WARN("Refii VFS split texture wrapper payload window rejected "
                "api={} wrapper=0x{:08X} owner=0x{:08X} provider={} "
                "expected_payload_size={} actual_size={} first_words={}",
                api,
                wrapper_guest,
                owner_texture,
                NormalizeLooseAssetKey(view->provider_key),
                expected_payload_size,
                payload_size,
                FormatGuestWords(base, view->buffer_guest, 4));
    return false;
  }

  const uint32_t combined_size = header_size + payload_size;
  const uint32_t old_buffer_guest = view->buffer_guest;
  const uint32_t combined_guest = CallLhMalloc(ctx, base, combined_size);
  if (combined_guest == 0) {
    REXLOG_WARN("Refii VFS split texture wrapper payload allocation failed "
                "api={} wrapper=0x{:08X} owner=0x{:08X} provider={} size={}",
                api,
                wrapper_guest,
                owner_texture,
                NormalizeLooseAssetKey(view->provider_key),
                combined_size);
    return false;
  }

  auto* combined = rex::memory::GuestPtr<uint8_t*>(base, combined_guest);
  if (view_is_header) {
    auto* header = rex::memory::GuestPtr<uint8_t*>(base, old_buffer_guest);
    std::memcpy(combined, header, header_size);

    std::ifstream payload_file(payload_resolution.host_path, std::ios::binary);
    if (!payload_file) {
      REXLOG_WARN("Refii VFS split texture wrapper payload open failed "
                  "api={} wrapper=0x{:08X} owner=0x{:08X} provider={} host={}",
                  api,
                  wrapper_guest,
                  owner_texture,
                  NormalizeLooseAssetKey(view->provider_key),
                  payload_resolution.host_path.string());
      CallLhFree(ctx, base, combined_guest);
      return false;
    }
    payload_file.read(reinterpret_cast<char*>(combined + header_size),
                      static_cast<std::streamsize>(payload_size));
    const uint32_t payload_read = static_cast<uint32_t>(payload_file.gcount());
    if (payload_read != payload_size) {
      REXLOG_WARN("Refii VFS split texture wrapper payload short read "
                  "api={} wrapper=0x{:08X} owner=0x{:08X} provider={} "
                  "host={} requested={} read={}",
                  api,
                  wrapper_guest,
                  owner_texture,
                  NormalizeLooseAssetKey(view->provider_key),
                  payload_resolution.host_path.string(),
                  payload_size,
                  payload_read);
      CallLhFree(ctx, base, combined_guest);
      return false;
    }
  } else {
    std::memcpy(combined, header_bytes.data(), header_size);
    auto* payload = rex::memory::GuestPtr<uint8_t*>(base, old_buffer_guest);
    std::memcpy(combined + header_size, payload, payload_size);
  }

  const uint32_t expected_old_size = view_is_header ? header_size : payload_size;
  if (const auto request = FindRefiiReadRequestRecord(view->inner_guest)) {
    std::lock_guard<std::mutex> lock(request->mutex);
    if (request->buffer_guest != old_buffer_guest ||
        request->bytes_read != expected_old_size) {
      CallLhFree(ctx, base, combined_guest);
      return false;
    }
    request->buffer_guest = combined_guest;
    request->requested_bytes = combined_size;
    request->bytes_read = combined_size;
    request->completed = true;
    request->attempted = true;
  } else if (const auto stream_record =
                 FindRefiiHostStreamRecord(view->inner_guest)) {
    std::lock_guard<std::mutex> lock(stream_record->mutex);
    if (stream_record->materialized_buffer_guest != old_buffer_guest ||
        stream_record->materialized_bytes != expected_old_size) {
      CallLhFree(ctx, base, combined_guest);
      return false;
    }
    stream_record->materialized_buffer_guest = combined_guest;
    stream_record->materialized_bytes = combined_size;
  } else {
    CallLhFree(ctx, base, combined_guest);
    return false;
  }

  if (const auto source_record =
          FindRefiiHostStreamRecord(view->source_stream_guest)) {
    std::lock_guard<std::mutex> lock(source_record->mutex);
    if (source_record->materialized_buffer_guest == old_buffer_guest ||
        source_record->materialized_buffer_guest == 0) {
      source_record->materialized_buffer_guest = combined_guest;
      source_record->materialized_bytes = combined_size;
    }
  }

  StoreGuestU32(base, wrapper_guest + 12, combined_guest);
  view->buffer_guest = combined_guest;
  view->size = combined_size;
  view->view_offset = 0;

  CallLhFree(ctx, base, old_buffer_guest);

  if (VfsTracingEnabled() ||
      ShouldLogRepeatedVfsTraceOperation("SplitTexturePayloadCombined", 32)) {
    REXLOG_INFO("Refii VFS split texture wrapper combined "
                "api={} wrapper=0x{:08X} owner=0x{:08X} provider={} "
                "payload={} host={} old_buffer=0x{:08X} "
                "combined_buffer=0x{:08X} header_size={} payload_size={} "
                "combined_size={} first_words={} payload_words={}",
                api,
                wrapper_guest,
                owner_texture,
                NormalizeLooseAssetKey(view->provider_key),
                need_payload_file ? payload_candidate : "<payload-window>",
                need_payload_file ? payload_resolution.host_path.string()
                                  : "<materialized>",
                old_buffer_guest,
                combined_guest,
                header_size,
                payload_size,
                combined_size,
                FormatGuestWords(base, combined_guest, 4),
                FormatGuestWords(base, combined_guest + header_size, 4));
    RecordRefiiMainMenuBlockerEvent(
        "wrapper_view",
        fmt::format("combined split texture wrapper=0x{:08X} owner=0x{:08X} "
                    "provider={} payload={} header_size={} payload_size={} "
                    "combined=0x{:08X}",
                    wrapper_guest,
                    owner_texture,
                    NormalizeLooseAssetKey(view->provider_key),
                    need_payload_file ? payload_candidate : "<payload-window>",
                    header_size,
                    payload_size,
                    combined_guest));
  }

  return true;
}

std::string FormatTextureOwnerWords(uint8_t* base, uint32_t texture_guest) {
  if (texture_guest == 0) {
    return {};
  }
  return FormatGuestWords(base, texture_guest, 32);
}

std::string FormatGuestWordsIfPointer(uint8_t* base,
                                      uint32_t guest,
                                      uint32_t word_count) {
  if (guest < 0x10000) {
    return {};
  }
  return FormatGuestWords(base, guest, word_count);
}

std::string FormatGuestWordsAtOffsetIfPointer(uint8_t* base,
                                              uint32_t guest,
                                              uint32_t offset,
                                              uint32_t word_count) {
  if (guest < 0x10000 ||
      guest > std::numeric_limits<uint32_t>::max() - offset) {
    return {};
  }
  return FormatGuestWordsIfPointer(base, guest + offset, word_count);
}

}  // namespace detail
}  // namespace refii::vfs
