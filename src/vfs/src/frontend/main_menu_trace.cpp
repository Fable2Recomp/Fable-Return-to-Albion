#include "vfs_hooks_private.h"

namespace refii::vfs {
inline namespace detail {

std::mutex& RefiiMainMenuBlockerTraceMutex() {
  static std::mutex mutex;
  return mutex;
}

RefiiMainMenuBlockerTrace& RefiiMainMenuBlockerTraceState() {
  static RefiiMainMenuBlockerTrace trace;
  return trace;
}

void RecordRefiiMainMenuBlockerEvent(std::string_view kind,
                                     std::string message) {
  if (message.empty()) {
    return;
  }

  std::lock_guard<std::mutex> lock(RefiiMainMenuBlockerTraceMutex());
  auto& trace = RefiiMainMenuBlockerTraceState();
  const auto now = std::chrono::steady_clock::now();
  if (kind == "texture_hash") {
    if (trace.last_texture_hash.empty()) {
      trace.last_texture_hash = std::move(message);
    } else if (trace.last_texture_hash.find(message) == std::string::npos) {
      if (trace.last_texture_hash.size() < 1024) {
        trace.last_texture_hash += " | ";
        trace.last_texture_hash += message;
      } else {
        trace.last_texture_hash = std::move(message);
      }
    }
    trace.texture_hash_at = now;
  } else if (kind == "texture_selection") {
    trace.last_texture_selection = std::move(message);
    trace.texture_selection_at = now;
  } else if (kind == "wrapper_view") {
    trace.last_wrapper_view = std::move(message);
    trace.wrapper_view_at = now;
  } else if (kind == "wrapper_slot36") {
    trace.last_wrapper_slot36 = std::move(message);
    trace.wrapper_slot36_at = now;
  } else if (kind == "texture_parser") {
    trace.last_texture_parser = std::move(message);
    trace.texture_parser_at = now;
  } else if (kind == "texture_stream_build") {
    trace.last_texture_stream_build = std::move(message);
    trace.texture_stream_build_at = now;
  } else if (kind == "loading_frame") {
    trace.last_loading_frame = std::move(message);
    trace.loading_frame_at = now;
  }
}

std::string FormatRefiiMainMenuBlockerTraceForLog() {
  std::lock_guard<std::mutex> lock(RefiiMainMenuBlockerTraceMutex());
  const auto& trace = RefiiMainMenuBlockerTraceState();
  const auto now = std::chrono::steady_clock::now();
  auto format_entry =
      [&](std::string_view label,
          const std::string& value,
          std::chrono::steady_clock::time_point timestamp) {
        if (value.empty()) {
          return fmt::format("{}=none", label);
        }
        const auto age_ms = std::chrono::duration_cast<std::chrono::milliseconds>(
                                now - timestamp)
                                .count();
        return fmt::format("{}=[{} age_ms={}]", label, value, age_ms);
      };
  return fmt::format("{} {} {} {} {} {} {}",
                     format_entry("hash", trace.last_texture_hash,
                                  trace.texture_hash_at),
                     format_entry("texture", trace.last_texture_selection,
                                  trace.texture_selection_at),
                     format_entry("wrapper", trace.last_wrapper_view,
                                  trace.wrapper_view_at),
                     format_entry("slot36", trace.last_wrapper_slot36,
                                  trace.wrapper_slot36_at),
                     format_entry("parser", trace.last_texture_parser,
                                  trace.texture_parser_at),
                     format_entry("texture_build",
                                  trace.last_texture_stream_build,
                                  trace.texture_stream_build_at),
                     format_entry("loading_frame",
                                  trace.last_loading_frame,
                                  trace.loading_frame_at));
}

uint32_t CountGuestLinearVector(uint8_t* base,
                                uint32_t object_guest,
                                uint32_t vector_offset,
                                uint32_t element_size) {
  if (base == nullptr || !LooksLikeGuestPointer(object_guest) ||
      element_size == 0 ||
      object_guest > std::numeric_limits<uint32_t>::max() - vector_offset - 12) {
    return 0;
  }

  const uint32_t begin = LoadGuestU32(base, object_guest + vector_offset + 4);
  const uint32_t end = LoadGuestU32(base, object_guest + vector_offset + 8);
  if (!LooksLikeGuestPointer(begin) || !LooksLikeGuestPointer(end) ||
      end < begin) {
    return 0;
  }
  return (end - begin) / element_size;
}

std::string FormatGuestLinearVector(uint8_t* base,
                                    uint32_t object_guest,
                                    uint32_t vector_offset,
                                    uint32_t element_size,
                                    uint32_t preview_words) {
  if (base == nullptr || !LooksLikeGuestPointer(object_guest) ||
      element_size == 0 ||
      object_guest > std::numeric_limits<uint32_t>::max() - vector_offset - 12) {
    return {};
  }

  const uint32_t begin = LoadGuestU32(base, object_guest + vector_offset + 4);
  const uint32_t end = LoadGuestU32(base, object_guest + vector_offset + 8);
  const uint32_t capacity = LoadGuestU32(base, object_guest + vector_offset + 12);
  const uint32_t count =
      (LooksLikeGuestPointer(begin) && LooksLikeGuestPointer(end) &&
       end >= begin)
          ? (end - begin) / element_size
          : 0;
  const uint32_t capacity_count =
      (LooksLikeGuestPointer(begin) && LooksLikeGuestPointer(capacity) &&
       capacity >= begin)
          ? (capacity - begin) / element_size
          : 0;

  return fmt::format(
      "base=0x{:08X} begin=0x{:08X} end=0x{:08X} cap=0x{:08X} count={} "
      "capacity={} first={}",
      object_guest + vector_offset,
      begin,
      end,
      capacity,
      count,
      capacity_count,
      count == 0 || !LooksLikeGuestPointer(begin)
          ? std::string{}
          : FormatGuestWords(base, begin, preview_words));
}

uint32_t FindRefiiLoadingScreenActiveResourceTable(uint8_t* base);

std::string FormatRefiiLoadingScreenSequenceGlobals(uint8_t* base) {
  if (base == nullptr) {
    return {};
  }

  constexpr uint32_t kLoadingGlobalBase = 0x83490000u;
  constexpr uint32_t kPrimaryOwnerGlobal = kLoadingGlobalBase + 27320u;
  constexpr uint32_t kPrimaryReadyGlobal = kLoadingGlobalBase + 27230u;
  constexpr uint32_t kSecondaryOwnerGlobal = kLoadingGlobalBase + 27328u;
  constexpr uint32_t kSecondaryReadyGlobal = kLoadingGlobalBase + 27332u;
  constexpr uint32_t kLoadStateGlobal = kLoadingGlobalBase + 27376u;
  constexpr uint32_t kSequenceRootGlobal = kLoadingGlobalBase + 30144u;

  const uint32_t primary_owner = LoadGuestU32(base, kPrimaryOwnerGlobal);
  const uint32_t secondary_owner = LoadGuestU32(base, kSecondaryOwnerGlobal);
  const uint32_t sequence_root = LoadGuestU32(base, kSequenceRootGlobal);
  const uint32_t primary_inner =
      LooksLikeGuestPointer(primary_owner) ? LoadGuestU32(base,
                                                          primary_owner + 12)
                                           : 0;
  const uint32_t primary_surface =
      LooksLikeGuestPointer(primary_inner) ? LoadGuestU32(base,
                                                          primary_inner + 140)
                                           : 0;
  const uint32_t secondary_inner =
      LooksLikeGuestPointer(secondary_owner)
          ? LoadGuestU32(base, secondary_owner + 12)
          : 0;
  const uint32_t secondary_surface =
      LooksLikeGuestPointer(secondary_inner)
          ? LoadGuestU32(base, secondary_inner + 140)
          : 0;

  return fmt::format(
      "primary=0x{:08X}/ready={} secondary=0x{:08X}/ready={} "
      "load_state={} sequence_root=0x{:08X} active_table=0x{:08X} "
      "primary_inner=0x{:08X} primary_surface=0x{:08X} "
      "secondary_inner=0x{:08X} secondary_surface=0x{:08X} "
      "primary_words=[{}] primary_inner_words=[{}] primary_surface_words=[{}] "
      "secondary_words=[{}] secondary_inner_words=[{}] "
      "secondary_surface_words=[{}] sequence_root_words=[{}]",
      primary_owner,
      LoadGuestU8(base, kPrimaryReadyGlobal),
      secondary_owner,
      LoadGuestU8(base, kSecondaryReadyGlobal),
      LoadGuestU32(base, kLoadStateGlobal),
      sequence_root,
      FindRefiiLoadingScreenActiveResourceTable(base),
      primary_inner,
      primary_surface,
      secondary_inner,
      secondary_surface,
      FormatGuestWordsIfPointer(base, primary_owner, 12),
      FormatGuestWordsIfPointer(base, primary_inner, 12),
      LooksLikeGuestDataPointer(primary_surface)
          ? FormatGuestWordsIfPointer(base, primary_surface, 12)
          : std::string{},
      FormatGuestWordsIfPointer(base, secondary_owner, 12),
      FormatGuestWordsIfPointer(base, secondary_inner, 12),
      LooksLikeGuestDataPointer(secondary_surface)
          ? FormatGuestWordsIfPointer(base, secondary_surface, 12)
          : std::string{},
      FormatGuestWordsIfPointer(base, sequence_root, 8));
}

bool LooksLikeNativeResourceHandle(uint32_t handle) {
  if (handle == 0 || handle == 0xFFFFFFFFu) {
    return false;
  }
  if ((handle & 0xFF000000u) == 0xF8000000u) {
    return true;
  }
  // Fable's frontend resource tables hand back small integer handles. These
  // are not guest pointers, but generated loading-screen code treats them as
  // opaque resource ids and resolves them through sub_8237AA98.
  return handle >= 4 && handle < 0x01000000u;
}

std::atomic<uint32_t>& RefiiRememberedLoadingScreenOwner() {
  static std::atomic<uint32_t> owner{0};
  return owner;
}

std::atomic<uint32_t>& RefiiBridgedLoadingSequenceHandle() {
  static std::atomic<uint32_t> handle{0};
  return handle;
}

std::atomic<uint32_t>& RefiiLoadingAnimationResolverHandle() {
  static std::atomic<uint32_t> handle{0};
  return handle;
}

std::atomic<uint32_t>& RefiiCompletedLoadingScreenOwner() {
  static std::atomic<uint32_t> owner{0};
  return owner;
}

std::atomic<uint32_t>& RefiiForcedLoadingScreenOwnerFinish() {
  static std::atomic<uint32_t> owner{0};
  return owner;
}

std::mutex& RefiiLoadingSequenceHandleMutex() {
  static std::mutex mutex;
  return mutex;
}

std::unordered_set<uint32_t>& RefiiLoadingSequenceHandles() {
  static std::unordered_set<uint32_t> handles;
  return handles;
}

void RecordRefiiLoadingSequenceHandle(uint32_t handle) {
  if (!LooksLikeNativeResourceHandle(handle)) {
    return;
  }
  std::lock_guard<std::mutex> lock(RefiiLoadingSequenceHandleMutex());
  RefiiLoadingSequenceHandles().insert(handle);
}

bool IsRefiiLoadingSequenceHandle(uint32_t handle) {
  if (!LooksLikeNativeResourceHandle(handle)) {
    return false;
  }
  std::lock_guard<std::mutex> lock(RefiiLoadingSequenceHandleMutex());
  return RefiiLoadingSequenceHandles().contains(handle);
}

void RecordRefiiLoadingSequenceResolverHandle(uint32_t resource_hash,
                                              uint32_t resolver_result,
                                              uint32_t caller_lr,
                                              uint32_t table_index) {
  if (resource_hash != kRefiiLoadingAnimationHash ||
      !LooksLikeNativeResourceHandle(resolver_result)) {
    return;
  }

  const uint32_t previous =
      RefiiLoadingAnimationResolverHandle().exchange(resolver_result,
                                                     std::memory_order_relaxed);
  RecordRefiiLoadingSequenceHandle(resolver_result);
  if (previous != resolver_result &&
      (VfsTracingEnabled() || LooseAssetOverrideTracingEnabled())) {
    REXLOG_INFO("Refii VFS loading sequence resolver handle "
                "hash=0x{:08X} handle=0x{:08X} previous=0x{:08X} "
                "lr=0x{:08X} table_index={}",
                resource_hash,
                resolver_result,
                previous,
                caller_lr,
                table_index);
  }
}

void RecordRefiiLoadingScreenOwner(uint8_t* base,
                                   uint32_t owner_guest,
                                   std::string_view source_api) {
  if (base == nullptr || !LooksLikeGuestPointer(owner_guest)) {
    return;
  }
  const uint32_t map356_count =
      CountGuestLinearVector(base, owner_guest, 356, 20);
  const uint32_t sequence_count = LoadGuestU32(base, owner_guest + 388);
  const uint32_t sequence_sentinel = LoadGuestU32(base, owner_guest + 384);
  const bool loading_owner_shape =
      LooksLikeGuestPointer(sequence_sentinel) ||
      LoadGuestU8(base, owner_guest + 313) != 0 ||
      LoadGuestU8(base, owner_guest + 314) != 0;
  if (map356_count == 0 && sequence_count == 0 && !loading_owner_shape) {
    return;
  }

  const uint32_t previous =
      RefiiRememberedLoadingScreenOwner().exchange(owner_guest,
                                                   std::memory_order_relaxed);
  if (previous != owner_guest &&
      (VfsTracingEnabled() || LooseAssetOverrideTracingEnabled())) {
    REXLOG_INFO("Refii VFS remembered loading-screen owner "
                "source={} owner=0x{:08X} previous=0x{:08X} state=[{}]",
                source_api,
                owner_guest,
                previous,
                FormatGuestWordsIfPointer(base, owner_guest, 24));
  }
}

uint32_t FindRefiiLoadingScreenActiveResourceTable(uint8_t* base) {
  if (base == nullptr) {
    return 0;
  }

  constexpr uint32_t kLoadingGlobalBase = 0x83490000u;
  constexpr uint32_t kPrimaryOwnerGlobal = kLoadingGlobalBase + 27320u;
  constexpr uint32_t kPrimaryReadyGlobal = kLoadingGlobalBase + 27230u;
  constexpr uint32_t kSecondaryOwnerGlobal = kLoadingGlobalBase + 27328u;
  constexpr uint32_t kSecondaryReadyGlobal = kLoadingGlobalBase + 27332u;

  const auto read_owner_table = [base](uint32_t owner) -> uint32_t {
    if (!LooksLikeGuestPointer(owner)) {
      return 0;
    }
    // Generated loading owners keep the full loading object at +12. The +8
    // field is the request resource pointer used when the owner is published.
    // The resource surface itself is not always at the same inner offset:
    // sub_82351340 reads owner+12, then inner+140, while adjacent loading
    // setup code still carries older table-style state at inner+84.
    for (const uint32_t inner_offset : {12u, 8u}) {
      const uint32_t owner_inner = LoadGuestU32(base, owner + inner_offset);
      if (!LooksLikeGuestPointer(owner_inner)) {
        continue;
      }
      for (const uint32_t table_offset : {140u, 84u}) {
        if (const uint32_t table = LoadGuestU32(base,
                                                owner_inner + table_offset);
            LooksLikeGuestDataPointer(table)) {
          return table;
        }
      }
    }
    return 0;
  };

  const uint32_t secondary_owner = LoadGuestU32(base, kSecondaryOwnerGlobal);
  if (secondary_owner != 0 && LoadGuestU8(base, kSecondaryReadyGlobal) != 0) {
    if (const uint32_t table = read_owner_table(secondary_owner); table != 0) {
      return table;
    }
  }

  const uint32_t primary_owner = LoadGuestU32(base, kPrimaryOwnerGlobal);
  if (primary_owner != 0) {
    const uint32_t primary_table = read_owner_table(primary_owner);
    if (primary_table != 0 &&
        (LoadGuestU8(base, kPrimaryReadyGlobal) != 0 ||
         RefiiPostVideoVfsHandoffTraceActive())) {
      return primary_table;
    }
  }
  return 0;
}

bool RefiiPostGameFacePackageHandoffSignalEmitted() {
  std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
  const auto& trace = RefiiPostVideoHandoffTraceState();
  return trace.post_gameface_package_handoff_signal_emitted &&
         (EndsWith(trace.post_gameface_package_handoff_asset, ".fac") ||
          (RefiiCompletedPostGameFacePackageMaskLocked(trace) & 0x7u) == 0x7u);
}

bool RefiiPostGameFacePackageComplete() {
  std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
  const auto& trace = RefiiPostVideoHandoffTraceState();
  return trace.post_gameface_package_handoff_signal_emitted &&
         (RefiiCompletedPostGameFacePackageMaskLocked(trace) & 0x7u) == 0x7u;
}

bool RefiiPostFrontendDeferredHashWakeSignalEmitted() {
  std::lock_guard<std::mutex> lock(RefiiPostVideoHandoffTraceMutex());
  const auto& trace = RefiiPostVideoHandoffTraceState();
  return trace.post_frontend_deferred_hash_wake_signal_emitted;
}

bool RefiiPostVfsChainReadyForLoadingOwnerRelease() {
  return RefiiStartupVideosReadyForMenuHandoff() &&
         RefiiPostGameFacePackageComplete() &&
         RefiiPostVideoFrontendChainConsumed() &&
         RefiiMainMenuTraceHasParsedTextureForGeneratedReadyPayload();
}

bool RefiiOwnerlessPostVideoVfsChainReadyForAfterHolder() {
  return RefiiStartupVideosReadyForMenuHandoff() &&
         RefiiPostGameFacePackageComplete() &&
         RefiiMainMenuTraceHasParsedTextureForGeneratedReadyPayload() &&
         !RefiiMainMenuTraceNeedsTexturePrime();
}


}  // namespace detail
}  // namespace refii::vfs
