#include "vfs_hooks_private.h"

namespace refii::vfs {
inline namespace detail {

std::string DescribeRefiiHostOrCompositeStream(uint8_t* base,
                                               uint32_t stream_guest) {
  if (base == nullptr || stream_guest == 0) {
    return "stream=0x00000000";
  }

  std::string description = fmt::format(
      "stream=0x{:08X} words={} {}",
      stream_guest,
      FormatGuestWords(base, stream_guest, 10),
      FormatGuestObjectVTable(base, stream_guest, 10));

  if (const auto record = FindRefiiHostStreamRecord(stream_guest)) {
    description += fmt::format(" refii_host size={} path={}",
                               record->size,
                               record->path.string());
    return description;
  }

  const uint32_t entries_begin = LoadGuestU32(base, stream_guest + 12);
  const uint32_t entries_end = LoadGuestU32(base, stream_guest + 16);
  const uint64_t total_size = LoadGuestU64(base, stream_guest + 24);
  if (entries_begin == 0 || entries_end <= entries_begin ||
      ((entries_end - entries_begin) % 16) != 0 ||
      (entries_end - entries_begin) / 16 > 64) {
    return description;
  }

  const uint32_t entry_count = (entries_end - entries_begin) / 16;
  description += fmt::format(" composite total={} segments={}",
                             total_size,
                             entry_count);
  const uint32_t described_count = std::min<uint32_t>(entry_count, 6);
  for (uint32_t index = 0; index < described_count; ++index) {
    const uint32_t entry_guest = entries_begin + index * 16;
    const uint32_t segment_guest = LoadGuestU32(base, entry_guest);
    const uint64_t segment_size = LoadGuestU64(base, entry_guest + 8);
    description += fmt::format(" [#{} stream=0x{:08X} size={}",
                               index,
                               segment_guest,
                               segment_size);
    if (const auto segment_record = FindRefiiHostStreamRecord(segment_guest)) {
      description += fmt::format(" host={} record_size={}",
                                 segment_record->path.string(),
                                 segment_record->size);
    }
    description += "]";
  }
  if (described_count != entry_count) {
    description += " ...";
  }

  return description;
}

void LogTextureStreamBuildResult(uint8_t* base,
                                 const PPCContext& original_ctx,
                                 uint32_t out_guest,
                                 uint32_t texture_set_guest,
                                 uint32_t texture_index) {
  if (out_guest == 0) {
    return;
  }

  const uint32_t stream_guest = TryReadOutputPointer(base, out_guest);
  if (stream_guest == 0) {
    return;
  }

  LooseStreamRecord loose_record;
  std::string match;
  const bool stream_is_loose =
      FindLooseStreamRecord(base, stream_guest, &loose_record, &match) ||
      FindRefiiHostStreamRecord(stream_guest) != nullptr;
  const uint32_t entries_begin = LoadGuestU32(base, stream_guest + 12);
  const uint32_t entries_end = LoadGuestU32(base, stream_guest + 16);
  bool composite_has_loose_segment = false;
  if (entries_begin != 0 && entries_end > entries_begin &&
      ((entries_end - entries_begin) % 16) == 0 &&
      (entries_end - entries_begin) / 16 <= 64) {
    const uint32_t entry_count = (entries_end - entries_begin) / 16;
    for (uint32_t index = 0; index < entry_count; ++index) {
      const uint32_t segment_guest = LoadGuestU32(base, entries_begin + index * 16);
      if (FindLooseStreamRecord(base, segment_guest, nullptr, nullptr) ||
          FindRefiiHostStreamRecord(segment_guest) != nullptr) {
        composite_has_loose_segment = true;
        break;
      }
    }
  }
  if (!stream_is_loose && !composite_has_loose_segment) {
    return;
  }

  const std::string texture_key =
      loose_record.provider_name.empty() ? loose_record.path
                                         : loose_record.provider_name;
  const bool trace_menu_texture =
      VfsTracingEnabled() && IsMainMenuTextureTraceAsset(texture_key);
  if (!trace_menu_texture &&
      !ShouldLogLooseStreamOperation("TextureStreamBuild", out_guest, 64)) {
    return;
  }

  const std::string description =
      DescribeRefiiHostOrCompositeStream(base, stream_guest);
  const uint32_t provider_begin =
      texture_set_guest == 0 ? 0 : LoadGuestU32(base, texture_set_guest + 12);
  const uint32_t provider_end =
      texture_set_guest == 0 ? 0 : LoadGuestU32(base, texture_set_guest + 16);
  const uint32_t entry_begin =
      texture_set_guest == 0 ? 0 : LoadGuestU32(base, texture_set_guest + 28);
  const uint32_t entry_end =
      texture_set_guest == 0 ? 0 : LoadGuestU32(base, texture_set_guest + 32);
  const uint32_t provider_count =
      provider_end >= provider_begin ? (provider_end - provider_begin) / 8 : 0;
  const uint32_t entry_count =
      entry_end >= entry_begin ? (entry_end - entry_begin) / 20 : 0;
  uint32_t texture_entry_guest = 0;
  uint32_t key_begin = 0;
  uint32_t key_end = 0;
  uint32_t key_count = 0;
  std::string key_sample;
  if (entry_begin != 0 && texture_index != 0 && texture_index <= entry_count) {
    texture_entry_guest = entry_begin + (texture_index - 1) * 20;
    key_begin = LoadGuestU32(base, texture_entry_guest + 8);
    key_end = LoadGuestU32(base, texture_entry_guest + 12);
    if (key_begin != 0 && key_end >= key_begin &&
        ((key_end - key_begin) % 4) == 0) {
      key_count = (key_end - key_begin) / 4;
      const uint32_t sample_count = std::min<uint32_t>(key_count, 8);
      for (uint32_t index = 0; index < sample_count; ++index) {
        key_sample += fmt::format("{}0x{:08X}",
                                  index == 0 ? "" : ",",
                                  LoadGuestU32(base, key_begin + index * 4));
      }
    }
  }
  if (trace_menu_texture) {
    RecordRefiiMainMenuBlockerEvent(
        "texture_stream_build",
        fmt::format("lr=0x{:08X} texture_set=0x{:08X} index={} provider={} "
                    "providers={} entries={} key_count={} keys=[{}] "
                    "stream=0x{:08X} {}",
                    original_ctx.lr,
                    texture_set_guest,
                    texture_index,
                    NormalizeLooseAssetKey(texture_key),
                    provider_count,
                    entry_count,
                    key_count,
                    key_sample,
                    stream_guest,
                    description));
  }

  REXLOG_INFO("Loose texture stream build caller_lr=0x{:08X} "
              "caller_ctr=0x{:08X} out=0x{:08X} result=0x{:08X} "
              "texture_set=0x{:08X} texture_index={} provider={} "
              "providers={} entries={} texture_entry=0x{:08X} key_count={} "
              "keys=[{}] {}",
              original_ctx.lr,
              original_ctx.ctr.u32,
              out_guest,
              stream_guest,
              texture_set_guest,
              texture_index,
              NormalizeLooseAssetKey(texture_key),
              provider_count,
              entry_count,
              texture_entry_guest,
              key_count,
              key_sample,
              description);
}

std::string DescribeResourceCallbackTarget(uint32_t target) {
  switch (target) {
    case 0x82188750:
      return "sub_82188750:state10568[8:1]";
    case 0x821BFDF0:
      return "sub_821BFDF0:state10548[6:4]";
    case 0x821C91C8:
      return "sub_821C91C8:state10460+11860";
    case 0x8220B470:
      return "sub_8220B470:state10568[2:0]";
    case 0x8226B058:
      return "sub_8226B058:state10548[0:1]";
    case 0x82286D78:
      return "sub_82286D78:state10548[1]+11876";
    default:
      return target == 0 ? "none" : fmt::format("sub_{:08X}", target);
  }
}

std::string DescribeResourceCallbackState(uint8_t* base,
                                          uint32_t callback_table_guest) {
  if (base == nullptr || callback_table_guest == 0) {
    return "callback_state=none";
  }

  return fmt::format(
      "callback_state=0x{:08X} flags16=0x{:016X} flags24=0x{:016X} "
      "s10460=0x{:08X} s10548=0x{:08X} s10568=0x{:08X} "
      "v11860=0x{:08X} v11876=0x{:08X} gate12440=0x{:08X} "
      "gate12456=0x{:08X}",
      callback_table_guest,
      LoadGuestU64(base, callback_table_guest + 16),
      LoadGuestU64(base, callback_table_guest + 24),
      LoadGuestU32(base, callback_table_guest + 10460),
      LoadGuestU32(base, callback_table_guest + 10548),
      LoadGuestU32(base, callback_table_guest + 10568),
      LoadGuestU32(base, callback_table_guest + 11860),
      LoadGuestU32(base, callback_table_guest + 11876),
      LoadGuestU32(base, callback_table_guest + 12440),
      LoadGuestU32(base, callback_table_guest + 12456));
}

std::string DescribeInitModelHeaderSummary(uint8_t* base) {
  if (base == nullptr) {
    return {};
  }

  constexpr uint32_t kResourceEntryCountGuest = 0x833605AC;
  constexpr uint32_t kPendingQueueCountGuest = 0x83490DB0;
  constexpr uint32_t kCallbackTablePointerGuest = 0x834970E8;

  const uint32_t callback_table_guest =
      LoadGuestU32(base, kCallbackTablePointerGuest);
  return fmt::format("resource_entries={} pending_queue={} callbacks=0x{:08X} {}",
                     LoadGuestU32(base, kResourceEntryCountGuest),
                     LoadGuestU32(base, kPendingQueueCountGuest),
                     callback_table_guest,
                     DescribeResourceCallbackState(base, callback_table_guest));
}

std::string DescribeInitModelHeaderQueue(uint8_t* base, uint32_t max_entries = 6) {
  if (base == nullptr) {
    return {};
  }

  constexpr uint32_t kResourceEntryCountGuest = 0x833605AC;
  constexpr uint32_t kResourceEntriesGuest = 0x834905B0;
  constexpr uint32_t kPendingQueueCountGuest = 0x83490DB0;
  constexpr uint32_t kCallbackTablePointerGuest = 0x834970E8;

  const uint32_t count = LoadGuestU32(base, kResourceEntryCountGuest);
  const uint32_t pending_count = LoadGuestU32(base, kPendingQueueCountGuest);
  const uint32_t callback_table_guest =
      LoadGuestU32(base, kCallbackTablePointerGuest);
  std::string description =
      fmt::format("resource_entries={} pending_queue={} callbacks=0x{:08X} {}",
                  count,
                  pending_count,
                  callback_table_guest,
                  DescribeResourceCallbackState(base, callback_table_guest));
  uint32_t described_count = 0;
  uint32_t pending_described = 0;
  for (uint32_t index = 0; index < count; ++index) {
    const uint32_t entry_guest =
        LoadGuestU32(base, kResourceEntriesGuest + index * sizeof(uint32_t));
    if (entry_guest == 0) {
      if (described_count < max_entries) {
        description += fmt::format(" [#{} null]", index);
        ++described_count;
      }
      continue;
    }

    const uint32_t current = LoadGuestU32(base, entry_guest);
    const uint32_t next = LoadGuestU32(base, entry_guest + 4);
    const uint32_t callback_offset = LoadGuestU32(base, entry_guest + 16);
    const uint32_t pending = LoadGuestU8(base, entry_guest + 20);
    const uint32_t kind = LoadGuestU8(base, entry_guest + 21);
    const uint32_t subkind = LoadGuestU8(base, entry_guest + 22);
    const uint32_t callback_guest =
        callback_offset == 0 || callback_table_guest == 0
            ? 0
            : callback_table_guest + callback_offset +
                  (kind == 2 ? 468 : 64);
    const uint32_t target =
        callback_guest == 0 ? 0 : LoadGuestU32(base, callback_guest);

    const bool should_describe =
        described_count < max_entries || pending != 0;
    if (!should_describe) {
      continue;
    }
    if (pending != 0) {
      ++pending_described;
    }

    description += fmt::format(
        " [#{} entry=0x{:08X} cur=0x{:08X} next=0x{:08X} "
        "cb_off=0x{:08X} pending={} kind={} subkind={} target=0x{:08X} "
        "{}]",
        index,
        entry_guest,
        current,
        next,
        callback_offset,
        pending,
        kind,
        subkind,
        target,
        DescribeResourceCallbackTarget(target));
    ++described_count;
  }
  if (count > described_count) {
    description += fmt::format(" ... described={} pending_described={}",
                               described_count,
                               pending_described);
  }
  return description;
}

void LogLooseBankHitDetail(std::string_view api,
                           uint8_t* base,
                           const PPCContext& caller_ctx,
                           uint32_t out_guest,
                           uint32_t bank_guest,
                           uint32_t manager_guest,
                           uint32_t mode,
                           uint64_t hit_count,
                           std::string_view bank_name,
                           std::string_view entry_name,
                           std::string_view candidate,
                           const LooseAssetResolution& resolution) {
  if (!ShouldLogLooseStatsMilestone(hit_count)) {
    return;
  }

  const uint32_t stream_guest = TryReadOutputPointer(base, out_guest);
  const uint32_t backing_stream_guest =
      stream_guest == 0 ? 0 : LoadGuestU32(base, stream_guest + 24);
  REXLOG_INFO("Loose bank hit detail {} hit={} caller_lr=0x{:08X} "
              "caller_ctr=0x{:08X} out=0x{:08X} stream=0x{:08X} "
              "bank=0x{:08X} manager=0x{:08X} mode={} bank_name={} "
              "entry={} candidate={} provider_relative={} host={} "
              "stream_words={} {} backing=0x{:08X} backing_words={} {}",
              api,
              hit_count,
              caller_ctx.lr,
              caller_ctx.ctr.u32,
              out_guest,
              stream_guest,
              bank_guest,
              manager_guest,
              mode,
              bank_name,
              entry_name,
              candidate,
              resolution.provider_relative_path,
              resolution.host_path.string(),
              FormatGuestWords(base, stream_guest, 12),
              FormatGuestObjectVTable(base, stream_guest, 12),
              backing_stream_guest,
              FormatGuestWords(base, backing_stream_guest, 14),
              FormatGuestObjectVTable(base, backing_stream_guest, 12));
}

void LogLooseVfsHitDetail(uint8_t* base,
                          const PPCContext& caller_ctx,
                          uint32_t out_guest,
                          uint32_t manager_guest,
                          uint32_t mode,
                          uint64_t hit_count,
                          std::string_view asset_path,
                          const LooseAssetResolution& resolution) {
  if (!ShouldLogLooseStatsMilestone(hit_count)) {
    return;
  }

  const uint32_t stream_guest = TryReadOutputPointer(base, out_guest);
  REXLOG_INFO("Loose VFS hit detail hit={} caller_lr=0x{:08X} "
              "caller_ctr=0x{:08X} out=0x{:08X} stream=0x{:08X} "
              "manager=0x{:08X} mode={} asset={} provider_relative={} "
              "host={} stream_words={} {}",
              hit_count,
              caller_ctx.lr,
              caller_ctx.ctr.u32,
              out_guest,
              stream_guest,
              manager_guest,
              mode,
              asset_path,
              resolution.provider_relative_path,
              resolution.host_path.string(),
              FormatGuestWords(base, stream_guest, 12),
              FormatGuestObjectVTable(base, stream_guest, 12));
}

uint32_t GetGlobalDataProvider(uint8_t* base, const PPCContext& source_ctx) {
  PPCContext ctx = source_ctx;
  rex_lhVFS_GetGlobalDataProvider(ctx, base);
  if (ctx.r3.u32 == 0) {
    return 0;
  }
  return LoadGuestU32(base, ctx.r3.u32);
}

bool StartsWith(std::string_view value, std::string_view prefix) {
  return value.size() >= prefix.size() && value.substr(0, prefix.size()) == prefix;
}

bool EndsWith(std::string_view value, std::string_view suffix) {
  return value.size() >= suffix.size() &&
         value.substr(value.size() - suffix.size()) == suffix;
}

uint64_t MakeBankManifestLookupKey(uint32_t offset, uint32_t size) {
  return (static_cast<uint64_t>(offset) << 32) | static_cast<uint64_t>(size);
}

std::string ManifestPathToGenericPath(std::string path) {
  std::replace(path.begin(), path.end(), '\\', '/');
  return path;
}

std::filesystem::path ResolveCaseInsensitiveExistingPath(
    const std::filesystem::path& root,
    const std::filesystem::path& relative_path);

bool IsLuaScriptAssetKey(std::string_view asset_path) {
  const std::string normalized = NormalizeLooseAssetKey(asset_path);
  if (!EndsWith(normalized, ".lua")) {
    return false;
  }

  // Lua has its own override path. The native loose bank hook deals in raw
  // streams, so feeding decompiled/text Lua through it can stall consumers that
  // expect the original banked bytecode layout.
  return true;
}

bool LuaVfsBridgeEnabled() {
  return RefiiLooseAssetBackendEnabled();
}

bool LooseLuaOverridesEnabled() {
  return refii::lua::NativeLuaModOverridesEnabled();
}

bool ResolveTypedLuaVfsAsset(std::string_view asset_path,
                             bool carrier,
                             LooseAssetResolution* out_resolution,
                             std::string* out_candidate);
bool ResolveExtractedLuaSourceAsset(std::string_view asset_path,
                                    LooseAssetResolution* out_resolution,
                                    std::string* out_candidate);
bool ResolveOriginalDataLuaAsset(std::string_view asset_path,
                                 LooseAssetResolution* out_resolution,
                                 std::string* out_candidate);
bool ResolveConcreteLuaLooseAsset(std::string_view asset_path,
                                  LooseAssetResolution* out_resolution,
                                  std::string* out_candidate,
                                  std::string* out_source);
bool ResolveRefiiLuaScriptAsset(std::string_view asset_path,
                                LooseAssetResolution* out_resolution,
                                std::string* out_candidate,
                                std::string* out_source);

bool ResolveLuaVfsOverride(std::string_view asset_path,
                           LooseAssetResolution* out_resolution,
                           std::string* out_candidate) {
  if (!LooseLuaOverridesEnabled() || !IsLuaScriptAssetKey(asset_path)) {
    return false;
  }

  if (ResolveTypedLuaVfsAsset(asset_path, false, out_resolution, out_candidate)) {
    return true;
  }

  const std::filesystem::path host_path =
      refii::lua::ResolveLuaModOverridePath(asset_path);
  if (host_path.empty()) {
    return false;
  }

  if (out_resolution != nullptr) {
    out_resolution->host_path = host_path.lexically_normal();
    out_resolution->normalized_key = NormalizeLooseAssetKey(asset_path);
    const std::filesystem::path loose_root = GetLooseAssetRoot();
    const std::filesystem::path relative =
        loose_root.empty()
            ? std::filesystem::path{}
            : host_path.lexically_relative(loose_root);
    out_resolution->provider_relative_path =
        (!relative.empty() && !relative.string().starts_with(".."))
            ? relative.generic_string()
            : host_path.filename().generic_string();
  }
  if (out_candidate != nullptr) {
    *out_candidate = std::string(asset_path);
  }
  return true;
}

void AddLuaVfsCarrierCandidate(std::vector<std::string>& candidates,
                               std::string value) {
  if (value.empty()) {
    return;
  }
  value = NormalizeLooseAssetKey(value);
  while (!value.empty() && value.front() == '/') {
    value.erase(value.begin());
  }
  if (value.empty()) {
    return;
  }
  if (std::find(candidates.begin(), candidates.end(), value) ==
      candidates.end()) {
    candidates.push_back(std::move(value));
  }
}

std::string StripTrailingSlashes(std::string value) {
  while (!value.empty() && value.back() == '/') {
    value.pop_back();
  }
  return value;
}

std::vector<std::string> BuildGameFaceRedirectCandidates(
    std::string_view asset_path) {
  const std::string normalized =
      StripTrailingSlashes(NormalizeLooseAssetKey(asset_path));
  std::vector<std::string> candidates;
  if (normalized.empty()) {
    return candidates;
  }

  auto add_candidate = [&](std::string value) {
    value = StripTrailingSlashes(NormalizeLooseAssetKey(value));
    if (value.empty() ||
        std::find(candidates.begin(), candidates.end(), value) !=
            candidates.end()) {
      return;
    }
    candidates.push_back(std::move(value));
  };

  constexpr std::string_view kDataScriptsGameface = "data/scripts/gameface";
  constexpr std::string_view kScriptsGameface = "scripts/gameface";
  constexpr std::string_view kDataArtGameface = "data/art/gui/gameface";
  constexpr std::string_view kArtGameface = "art/gui/gameface";
  constexpr std::string_view kGuiScriptsGameface = "guiscripts/scripts/gameface";
  constexpr std::string_view kGuiArtGameface = "guiscripts/art/gui/gameface";

  auto remap_prefix = [&](std::string_view prefix, std::string_view replacement) {
    if (!StartsWith(normalized, prefix)) {
      return;
    }
    std::string tail = std::string(normalized.substr(prefix.size()));
    while (!tail.empty() && tail.front() == '/') {
      tail.erase(tail.begin());
    }
    add_candidate(std::string(replacement) + (tail.empty() ? "" : "/" + tail));
  };

  auto remap_embedded = [&](std::string_view marker,
                            std::string_view replacement) {
    const size_t marker_pos = normalized.find(marker);
    if (marker_pos == std::string::npos) {
      return;
    }
    std::string tail =
        std::string(normalized.substr(marker_pos + marker.size()));
    while (!tail.empty() && tail.front() == '/') {
      tail.erase(tail.begin());
    }
    add_candidate(std::string(replacement) + (tail.empty() ? "" : "/" + tail));
  };

  remap_prefix(kDataScriptsGameface, kGuiScriptsGameface);
  remap_prefix(kScriptsGameface, kGuiScriptsGameface);
  remap_prefix(kDataArtGameface, kGuiArtGameface);
  remap_prefix(kArtGameface, kGuiArtGameface);
  remap_embedded("sourceassets/scripts/gameface", kGuiScriptsGameface);
  remap_embedded("scripts/gameface", kGuiScriptsGameface);
  remap_embedded("sourceassets/art/gui/gameface", kGuiArtGameface);
  remap_embedded("art/gui/gameface", kGuiArtGameface);
  add_candidate(normalized);
  return candidates;
}

}  // namespace detail
}  // namespace refii::vfs
