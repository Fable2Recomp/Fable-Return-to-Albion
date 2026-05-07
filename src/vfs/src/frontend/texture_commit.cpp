#include "vfs_hooks_private.h"

namespace refii::vfs {
inline namespace detail {

uint32_t TryAllocateRefiiFrontendTextureObject(PPCContext& source_ctx,
                                               uint8_t* base,
                                               uint32_t resource_hash,
                                               const LooseAssetResolution& resolution,
                                               std::string_view candidate,
                                               std::string_view api) {
  if (base == nullptr || resource_hash == 0) {
    return 0;
  }

  const std::array<uint32_t, 4> first_words =
      ReadHostFileFirstWords(resolution.host_path);
  const bool split_texture_header = LooksLikeTextureHeaderDescriptor(first_words);
  const bool full_resource_texture = LooksLikeResourceWrapperHeader(first_words);
  if (!split_texture_header && !full_resource_texture) {
    if (VfsTracingEnabled() || LooseAssetOverrideTracingEnabled()) {
      REXLOG_WARN("Refii VFS frontend texture object alloc skipped api={} "
                  "hash=0x{:08X} candidate={} provider={} first_words={}",
                  api,
                  resource_hash,
                  NormalizeLooseAssetKey(candidate),
                  NormalizeLooseAssetKey(resolution.provider_relative_path),
                  FormatHostFileFirstWords(first_words));
    }
    return 0;
  }

  std::array<uint32_t, 21> header_words{};
  {
    std::ifstream input(resolution.host_path, std::ios::binary);
    std::array<uint8_t, 84> bytes{};
    if (!input.read(reinterpret_cast<char*>(bytes.data()), bytes.size())) {
      if (VfsTracingEnabled() || LooseAssetOverrideTracingEnabled()) {
        REXLOG_WARN("Refii VFS frontend texture object header read failed "
                    "api={} hash=0x{:08X} candidate={} provider={} host={}",
                    api,
                    resource_hash,
                    NormalizeLooseAssetKey(candidate),
                    NormalizeLooseAssetKey(resolution.provider_relative_path),
                    resolution.host_path.string());
      }
      return 0;
    }
    for (size_t index = 0; index < header_words.size(); ++index) {
      header_words[index] = LoadBigEndianU32(bytes.data() + index * 4);
    }
  }

  constexpr uint32_t kNativeTextureFormatListAnchor = 0x834A5FD8u;
  PPCContext alloc_ctx = source_ctx;
  const uint32_t texture_guest = CallLhMalloc(alloc_ctx, base, 128);
  if (!LooksLikeGuestPointer(texture_guest)) {
    return 0;
  }
  for (uint32_t offset = 0; offset < 128; offset += 4) {
    StoreGuestU32(base, texture_guest + offset, 0);
  }

  StoreGuestU32(base, texture_guest + 0, kNativeTextureFormatListAnchor);
  StoreGuestU32(base, texture_guest + 4, resource_hash);
  StoreGuestU32(base, texture_guest + 8, 1);
  StoreGuestU32(base, texture_guest + 12, 0xFFFFFFFFu);
  StoreGuestU32(base, texture_guest + 16, 2);
  if (split_texture_header) {
    for (size_t index = 1; index < header_words.size(); ++index) {
      StoreGuestU32(base,
                    texture_guest + 20 + static_cast<uint32_t>((index - 1) * 4),
                    header_words[index]);
    }
  } else {
    const uint64_t host_size = HostFileSize(resolution.host_path);
    StoreGuestU32(base, texture_guest + 16, 0);
    StoreGuestU32(base, texture_guest + 20, static_cast<uint32_t>(
                                             std::min<uint64_t>(
                                                 host_size,
                                                 std::numeric_limits<uint32_t>::max())));
    StoreGuestU32(base, texture_guest + 24, 0);
    StoreGuestU32(base, texture_guest + 28, 0);
  }
  StoreGuestU32(base, texture_guest + 104, 2);
  StoreGuestU32(base, texture_guest + 108, 0x01000000u);
  StoreGuestU32(base, texture_guest + 120, resource_hash);

  return texture_guest;
}

bool TryHandleRefiiTextureParserC340(PPCContext& ctx,
                                     uint8_t* base,
                                     std::string_view api) {
  const PPCContext original_ctx = ctx;
  const uint32_t texture_guest = original_ctx.r3.u32;
  if (base == nullptr || !LooksLikeGuestPointer(texture_guest)) {
    return false;
  }

  const uint32_t resource_hash = LoadGuestU32(base, texture_guest + 120);
  const uint32_t texture_hash = LoadGuestU32(base, texture_guest + 4);
  uint32_t wrapper_guest = NormalizeRefiiResourceStreamWrapper(
      base, LoadGuestU32(base, texture_guest + 100));
  if (wrapper_guest == 0) {
    return false;
  }

  const std::string provider =
      GetRefiiResourceWrapperProviderKey(
          base, wrapper_guest, LoadGuestU32(base, wrapper_guest + 16));
  const bool main_menu_texture =
      IsMainMenuTextureResourceHash(resource_hash) ||
      IsMainMenuTextureResourceHash(texture_hash) ||
      IsResourceWrapperTextureTraceAsset(provider);
  if (!main_menu_texture) {
    return false;
  }

  RecordRefiiTextureWrapperOwner(wrapper_guest, texture_guest);
  const RefiiResourceWrapperView view =
      GetRefiiResourceWrapperView(ctx, base, wrapper_guest, api);
  const bool parsed = view.buffer_guest != 0 && view.size != 0;
  StoreGuestU8(base, texture_guest + 116, parsed ? 1 : 0);
  ctx.r3.u32 = parsed ? wrapper_guest : 0;

  RecordFrontendTextureConsumerResourceBoundary(
      "TextureParser_C340_refii_vfs_shim",
      resource_hash == 0 ? texture_hash : resource_hash,
      parsed ? wrapper_guest : 0);
  RecordRefiiMainMenuBlockerEvent(
      "texture_parser",
      fmt::format("refii_C340_shim api={} lr=0x{:08X} texture=0x{:08X} "
                  "hash=0x{:08X} resource_hash=0x{:08X} wrapper=0x{:08X} "
                  "parsed={} state116=0x{:02X} buffer=0x{:08X} size={} "
                  "provider={} first_words={}",
                  api,
                  original_ctx.lr,
                  texture_guest,
                  texture_hash,
                  resource_hash,
                  wrapper_guest,
                  parsed ? "true" : "false",
                  LoadGuestU8(base, texture_guest + 116),
                  view.buffer_guest,
                  view.size,
                  NormalizeLooseAssetKey(provider),
                  FormatGuestWords(base, view.buffer_guest, 4)));
  if (VfsTracingEnabled() || LooseAssetOverrideTracingEnabled() ||
      IsResourceWrapperTextureTraceAsset(provider)) {
    REXLOG_INFO("Refii VFS TextureParser C340 shim api={} lr=0x{:08X} "
                "texture=0x{:08X} hash=0x{:08X} resource_hash=0x{:08X} "
                "wrapper=0x{:08X} result=0x{:08X} state116=0x{:02X} "
                "buffer=0x{:08X} size={} provider={} first_words={} "
                "texture_words={} wrapper_words={}",
                api,
                original_ctx.lr,
                texture_guest,
                texture_hash,
                resource_hash,
                wrapper_guest,
                ctx.r3.u32,
                LoadGuestU8(base, texture_guest + 116),
                view.buffer_guest,
                view.size,
                NormalizeLooseAssetKey(provider),
                FormatGuestWords(base, view.buffer_guest, 4),
                FormatGuestWords(base, texture_guest, 32),
                FormatGuestWords(base, wrapper_guest, 12));
  }
  return true;
}

bool TryHandleRefiiTextureParserC340NativeHandoff(PPCContext& ctx,
                                                  uint8_t* base,
                                                  std::string_view api) {
  const PPCContext original_ctx = ctx;
  const uint32_t texture_guest = original_ctx.r3.u32;
  uint32_t wrapper_guest = 0;
  const std::string provider =
      GetTextureParserTraceProvider(base, texture_guest, &wrapper_guest);
  const uint32_t texture_hash =
      texture_guest == 0 ? 0 : LoadGuestU32(base, texture_guest + 4);
  const uint32_t resource_hash =
      texture_guest == 0 ? 0 : LoadGuestU32(base, texture_guest + 120);

  PPCContext shim_ctx = ctx;
  if (!TryHandleRefiiTextureParserC340(shim_ctx, base, api)) {
    return false;
  }

  RefiiResourceWrapperView native_view;
  if (wrapper_guest != 0) {
    native_view =
        GetRefiiResourceWrapperView(shim_ctx, base, wrapper_guest, api);
  }
  const bool native_view_ready =
      native_view.buffer_guest != 0 && native_view.size >= 84 &&
      LoadGuestU32(base, native_view.buffer_guest) == 0xFFFFFFFEu;
  if (!native_view_ready) {
    ctx = shim_ctx;
    RecordFrontendTextureConsumerResourceBoundary(
        "TextureParser_C340_native_skipped_non_header_view",
        resource_hash == 0 ? texture_hash : resource_hash,
        shim_ctx.r3.u32);
    if (VfsTracingEnabled() || LooseAssetOverrideTracingEnabled() ||
        IsResourceWrapperTextureTraceAsset(provider) ||
        IsMainMenuTextureResourceHash(texture_hash) ||
        IsMainMenuTextureResourceHash(resource_hash)) {
      REXLOG_INFO("Refii VFS TextureParser C340 native handoff skipped api={} "
                  "lr=0x{:08X} texture=0x{:08X} hash=0x{:08X} "
                  "resource_hash=0x{:08X} wrapper=0x{:08X} "
                  "shim_result=0x{:08X} provider={} view_buffer=0x{:08X} "
                  "view_size={} first_words={}",
                  api,
                  original_ctx.lr,
                  texture_guest,
                  texture_hash,
                  resource_hash,
                  wrapper_guest,
                  shim_ctx.r3.u32,
                  NormalizeLooseAssetKey(provider),
                  native_view.buffer_guest,
                  native_view.size,
                  FormatGuestWords(base, native_view.buffer_guest, 4));
    }
    return true;
  }

  ctx = shim_ctx;
  RecordFrontendTextureConsumerResourceBoundary(
      "TextureParser_C340_native_deferred_refii_wrapper",
      resource_hash == 0 ? texture_hash : resource_hash,
      shim_ctx.r3.u32);

  if (VfsTracingEnabled() || LooseAssetOverrideTracingEnabled() ||
      IsResourceWrapperTextureTraceAsset(provider) ||
      IsMainMenuTextureResourceHash(texture_hash) ||
      IsMainMenuTextureResourceHash(resource_hash)) {
    REXLOG_INFO("Refii VFS TextureParser C340 native handoff deferred api={} "
                "lr=0x{:08X} texture=0x{:08X} hash=0x{:08X} "
                "resource_hash=0x{:08X} wrapper=0x{:08X} "
                "shim_result=0x{:08X} selected=0x{:08X} provider={} "
                "view_buffer=0x{:08X} view_size={} first_words={} "
                "texture_words={} wrapper_words={}",
                api,
                original_ctx.lr,
                texture_guest,
                texture_hash,
                resource_hash,
                wrapper_guest,
                shim_ctx.r3.u32,
                ctx.r3.u32,
                NormalizeLooseAssetKey(provider),
                native_view.buffer_guest,
                native_view.size,
                FormatGuestWords(base, native_view.buffer_guest, 4),
                FormatGuestWords(base, texture_guest, 32),
                FormatGuestWords(base, wrapper_guest, 12));
  }
  return true;
}

bool TryCommitRefiiFrontendTextureWrapper(PPCContext& source_ctx,
                                          uint8_t* base,
                                          uint32_t texture_guest,
                                          uint32_t resource_hash,
                                          const LooseAssetResolution& resolution,
                                          std::string_view name,
                                          std::string_view candidate,
                                          std::string_view source,
                                          uint32_t caller_lr,
                                          std::string_view api) {
  if (base == nullptr || !LooksLikeGuestPointer(texture_guest) ||
      resource_hash == 0) {
    return false;
  }

  const uint32_t texture_hash = LoadGuestU32(base, texture_guest + 4);
  const uint32_t texture_resource_hash = LoadGuestU32(base, texture_guest + 120);
  const uint32_t existing_wrapper = NormalizeRefiiResourceStreamWrapper(
      base, LoadGuestU32(base, texture_guest + 100));
  const bool hash_matches =
      texture_hash == resource_hash || texture_resource_hash == resource_hash;
  if (!hash_matches) {
    if ((VfsTracingEnabled() || LooseAssetOverrideTracingEnabled()) &&
        (IsMainMenuTextureHashTraceAsset(name) ||
         IsMainMenuTextureHashTraceAsset(candidate) ||
         IsMainMenuTextureHashTraceAsset(resolution.provider_relative_path))) {
      REXLOG_INFO("Refii VFS frontend texture wrapper commit skipped api={} "
                  "lr=0x{:08X} texture=0x{:08X} expected_hash=0x{:08X} "
                  "actual_hash=0x{:08X} resource_hash_slot=0x{:08X} "
                  "candidate={} texture_words={}",
                  api,
                  caller_lr,
                  texture_guest,
                  resource_hash,
                  texture_hash,
                  texture_resource_hash,
                  NormalizeLooseAssetKey(candidate),
                  FormatGuestWords(base, texture_guest, 32));
    }
    return false;
  }

  if (existing_wrapper != 0) {
    RecordRefiiTextureWrapperOwner(existing_wrapper, texture_guest);
    RecordFrontendTextureConsumerResourceBoundary(
        "ResourceManager_Open_FrontendTextureWrapperAlreadyAttached",
        resource_hash,
        existing_wrapper);
    return true;
  }

  const uint32_t active_count = LoadGuestU32(base, texture_guest + 16);
  const uint32_t flags = LoadGuestU32(base, texture_guest + 28);
  const bool use_resource_offset_path =
      active_count > 0 && ((flags & 0x1FU) & 0xFFFFFFF1U) != 0;
  uint64_t resource_offset = 0;
  uint32_t resource_size = 0;
  if (use_resource_offset_path) {
    const uint32_t range_index = LoadGuestU32(base, texture_guest + 112) + 12;
    const uint32_t range_offset_guest = texture_guest + range_index * 4;
    const uint32_t range_start = LoadGuestU32(base, range_offset_guest);
    const uint32_t range_end =
        LoadGuestU32(base, texture_guest + 48) +
        LoadGuestU32(base, texture_guest + 20);
    if (range_offset_guest < texture_guest || range_end < range_start) {
      if (VfsTracingEnabled() || LooseAssetOverrideTracingEnabled()) {
        REXLOG_WARN("Refii VFS frontend texture wrapper range invalid api={} "
                    "lr=0x{:08X} texture=0x{:08X} hash=0x{:08X} "
                    "range_index={} range_start={} range_end={}",
                    api,
                    caller_lr,
                    texture_guest,
                    resource_hash,
                    range_index,
                    range_start,
                    range_end);
      }
      return false;
    }
    resource_offset = range_start;
    resource_size = range_end - range_start;
  }

  LooseAssetResolution selected_resolution = resolution;
  std::string selected_candidate =
      candidate.empty() ? NormalizeLooseAssetKey(name)
                        : NormalizeLooseAssetKey(candidate);
  MaybeUpgradeResourceManagerTextureResolution(
      resource_hash,
      name.empty() ? selected_candidate : name,
      caller_lr,
      &selected_resolution,
      &selected_candidate);

  PPCContext open_ctx = source_ctx;
  const uint32_t out_guest = CallLhMalloc(open_ctx, base, 8);
  if (!LooksLikeGuestPointer(out_guest)) {
    return false;
  }
  StoreGuestU64(base, out_guest, 0);

  RefiiVfsOpenResult open_result;
  const RefiiVfsOpenDisposition open_disposition =
      TryOpenResolvedRefiiVfsAsset(
          open_ctx,
          base,
          source_ctx,
          RefiiVfsOpenShape::ResourceWrapper,
          selected_resolution,
          RefiiResolvedVfsOpenRequest{
              .api = api,
              .request_name = name,
              .candidate = selected_candidate,
              .source = source,
              .resource_hash = resource_hash,
              .out_guest = out_guest,
              .mode = 1,
              .resource_flags = 1,
              .resource_offset = resource_offset,
              .resource_size = resource_size,
              .caller_lr = caller_lr,
              .count_vfs_hit = false,
          },
          &open_result);
  const bool opened =
      open_disposition == RefiiVfsOpenDisposition::Opened;
  const uint32_t wrapper_guest = TryReadOutputPointer(base, out_guest);
  const uint32_t normalized_wrapper =
      NormalizeRefiiResourceStreamWrapper(base, wrapper_guest);
  if (!opened || normalized_wrapper == 0) {
    if (normalized_wrapper != 0) {
      PPCContext release_ctx = source_ctx;
      TryHandleRefiiResourceWrapperRelease(
          release_ctx,
          base,
          normalized_wrapper,
          out_guest,
          "ResourceWrapper_FrontendTextureFallbackRelease");
    }
    CallLhFree(open_ctx, base, out_guest);
    return false;
  }

  uint32_t commit_arg = 1;
  switch (LoadGuestU32(base, texture_guest + 104)) {
    case 0:
      commit_arg = 0;
      break;
    case 2:
      commit_arg = 2;
      break;
    case 3:
      commit_arg = 3;
      break;
    default:
      commit_arg = 1;
      break;
  }

  PPCContext slot_ctx = source_ctx;
  slot_ctx.r3.u32 = normalized_wrapper;
  slot_ctx.r4.u32 = commit_arg;
  slot_ctx.lr = 0x82B6DC50;
  TryHandleRefiiResourceWrapperSlot36(
      slot_ctx, base, normalized_wrapper, commit_arg);

  StoreGuestU8(base, texture_guest + 116, 1);
  RecordFrontendTextureConsumerResourceBoundary(
      "TextureParser_C340_synthetic_wrapper_commit",
      resource_hash,
      normalized_wrapper);

  PPCContext assign_ctx = source_ctx;
  assign_ctx.r3.u32 = texture_guest + 100;
  assign_ctx.r4.u32 = out_guest;
  assign_ctx.lr = 0x82B6DC60;
  const bool assigned = TryHandleRefiiReadRequestSmartPtrAssign(
      assign_ctx, base, texture_guest + 100, out_guest);

  PPCContext release_ctx = source_ctx;
  release_ctx.r3.u32 = normalized_wrapper;
  release_ctx.r4.u32 = out_guest;
  release_ctx.lr = 0x82B6DC7C;
  const bool released = TryHandleRefiiResourceWrapperRelease(
      release_ctx,
      base,
      normalized_wrapper,
      out_guest,
      "ResourceWrapper_FrontendTextureStackRelease");

  RecordRefiiTextureWrapperOwner(normalized_wrapper, texture_guest);
  RecordFrontendTextureConsumerResourceBoundary(
      "ResourceManager_Open_FrontendTextureWrapperCommit",
      resource_hash,
      normalized_wrapper);
  bool native_parser_prime = false;
  uint32_t native_parser_result = 0;
  const bool split_texture_view =
      IsTextureHeaderCandidate(selected_candidate) ||
      IsTextureHeaderCandidate(selected_resolution.provider_relative_path);
  if (IsMainMenuTextureResourceHash(resource_hash) || split_texture_view) {
    PPCContext parse_ctx = source_ctx;
    parse_ctx.r3.u32 = texture_guest;
    parse_ctx.lr = 0x82B6D6E8u;
    RecordFrontendTextureConsumerResourceBoundary(
        "TextureParser_C340_native_commit_enter",
        resource_hash,
        normalized_wrapper);
    TryHandleRefiiTextureParserC340NativeHandoff(
        parse_ctx, base, "TextureParser_C340_commit");
    native_parser_prime = true;
    native_parser_result = parse_ctx.r3.u32;
    RecordFrontendTextureConsumerResourceBoundary(
        "TextureParser_C340_commit_exit",
        resource_hash,
        native_parser_result == 0 ? normalized_wrapper : native_parser_result);
    if (VfsTracingEnabled() || LooseAssetOverrideTracingEnabled() ||
        IsMainMenuTextureHashTraceAsset(name) ||
        IsMainMenuTextureHashTraceAsset(selected_candidate) ||
        IsMainMenuTextureHashTraceAsset(
            selected_resolution.provider_relative_path)) {
      REXLOG_INFO("Refii VFS frontend texture native parser committed api={} "
                  "lr=0x{:08X} texture=0x{:08X} hash=0x{:08X} "
                  "wrapper=0x{:08X} result=0x{:08X} state116=0x{:02X} "
                  "provider={} texture_words={} wrapper_words={}",
                  api,
                  caller_lr,
                  texture_guest,
                  resource_hash,
                  normalized_wrapper,
                  native_parser_result,
                  LoadGuestU8(base, texture_guest + 116),
                  NormalizeLooseAssetKey(
                      selected_resolution.provider_relative_path),
                  FormatGuestWords(base, texture_guest, 32),
                  FormatGuestWords(base, normalized_wrapper, 12));
    }
  }
  LogRefiiOwnedTextureStreamBuilt(base,
                                  api,
                                  caller_lr,
                                  resource_hash,
                                  name,
                                  selected_resolution,
                                  selected_candidate,
                                  source,
                                  out_guest,
                                  normalized_wrapper,
                                  true);

  if (VfsTracingEnabled() || LooseAssetOverrideTracingEnabled() ||
      IsMainMenuTextureHashTraceAsset(name) ||
      IsMainMenuTextureHashTraceAsset(selected_candidate) ||
      IsMainMenuTextureHashTraceAsset(
          selected_resolution.provider_relative_path)) {
    REXLOG_INFO("Refii VFS frontend texture wrapper committed api={} "
                "lr=0x{:08X} texture=0x{:08X} hash=0x{:08X} wrapper=0x{:08X} "
                "commit_arg={} assigned={} released={} target_value=0x{:08X} "
                "refs={} offset={} size={} hash_slot=0x{:08X} "
                "resource_hash_slot=0x{:08X} candidate={} provider={} "
                "native_parser={} native_parser_result=0x{:08X} "
                "wrapper_words={} texture_words={}",
                api,
                caller_lr,
                texture_guest,
                resource_hash,
                normalized_wrapper,
                commit_arg,
                assigned,
                released,
                LoadGuestU32(base, texture_guest + 100),
                LoadGuestU32(base, normalized_wrapper + 8),
                resource_offset,
                resource_size,
                texture_hash,
                texture_resource_hash,
                selected_candidate,
                NormalizeLooseAssetKey(
                    selected_resolution.provider_relative_path),
                native_parser_prime ? "true" : "false",
                native_parser_result,
                FormatGuestWords(base, normalized_wrapper, 12),
                FormatGuestWords(base, texture_guest, 32));
  }

  CallLhFree(open_ctx, base, out_guest);
  return true;
}

bool TryPrimeRefiiFrontendTextureResource(PPCContext& source_ctx,
                                          uint8_t* base,
                                          uint32_t resource_hash,
                                          std::string_view api) {
  if (base == nullptr || resource_hash == 0 ||
      !RefiiLooseAssetBackendEnabled()) {
    return false;
  }

  LooseAssetResolution resolution;
  std::string name;
  std::string candidate;
  std::string source;
  if (!ResolveRefiiOwnedTextureDescriptorHash(api,
                                              resource_hash,
                                              static_cast<uint32_t>(source_ctx.lr),
                                              &resolution,
                                              &name,
                                              &candidate,
                                              &source)) {
    return false;
  }
  MaybeUpgradeResourceManagerTextureResolution(resource_hash,
                                               name.empty() ? candidate : name,
                                               static_cast<uint32_t>(source_ctx.lr),
                                               &resolution,
                                               &candidate);

  const uint32_t texture_guest =
      TryAllocateRefiiFrontendTextureObject(source_ctx,
                                            base,
                                            resource_hash,
                                            resolution,
                                            candidate,
                                            api);
  if (!LooksLikeGuestPointer(texture_guest)) {
    return false;
  }

  PPCContext db20_ctx = source_ctx;
  db20_ctx.r3.u32 = texture_guest;
  db20_ctx.lr = 0x82B6D474u;
  ::__imp__sub_82B6DB20(db20_ctx, base);

  const uint32_t wrapper_guest = NormalizeRefiiResourceStreamWrapper(
      base, LoadGuestU32(base, texture_guest + 100));
  bool parsed = false;
  bool parse_skipped = false;
  uint32_t parse_result = 0;
  if (wrapper_guest != 0) {
    RecordRefiiTextureWrapperOwner(wrapper_guest, texture_guest);
    const RefiiResourceWrapperView view =
        GetRefiiResourceWrapperView(source_ctx,
                                    base,
                                    wrapper_guest,
                                    "FrontendTexturePrimeSyntheticParser");
    PPCContext slot_ctx = source_ctx;
    slot_ctx.r3.u32 = wrapper_guest;
    slot_ctx.r4.u32 = LoadGuestU32(base, texture_guest + 104) == 2 ? 2 : 1;
    slot_ctx.lr = 0x82B6DC50u;
    TryHandleRefiiResourceWrapperSlot36(
        slot_ctx, base, wrapper_guest, slot_ctx.r4.u32);

    const bool use_refii_c340_prime =
        IsMainMenuTextureResourceHash(resource_hash);
    if (use_refii_c340_prime) {
      PPCContext c340_ctx = source_ctx;
      c340_ctx.r3.u32 = texture_guest;
      c340_ctx.lr = 0x82B6D6E8u;
      TryHandleRefiiTextureParserC340NativeHandoff(
          c340_ctx, base, "FrontendTexturePrimeC340");
      parse_result = c340_ctx.r3.u32;
    } else {
      StoreGuestU8(base, texture_guest + 116, 1);
      parse_result = wrapper_guest;
      parse_skipped = true;
    }
    parsed = view.buffer_guest != 0 && view.size != 0;
    RecordFrontendTextureConsumerResourceBoundary(
        use_refii_c340_prime ? "TextureParser_C340_refii_prime_exit"
                             : "TextureParser_C340_synthetic_prime_exit",
        resource_hash,
        parsed ? (parse_result == 0 ? wrapper_guest : parse_result) : 0);
    RecordRefiiMainMenuBlockerEvent(
        "texture_parser",
        fmt::format("{} api={} hash=0x{:08X} texture=0x{:08X} "
                    "wrapper=0x{:08X} parsed={} result=0x{:08X} "
                    "state116=0x{:02X} buffer=0x{:08X} size={} provider={} "
                    "first_words={}",
                    use_refii_c340_prime ? "refii_C340_prime"
                                         : "synthetic_C340_prime",
                    api,
                    resource_hash,
                    texture_guest,
                    wrapper_guest,
                    parsed ? "true" : "false",
                    parse_result,
                    LoadGuestU8(base, texture_guest + 116),
                    view.buffer_guest,
                    view.size,
                    NormalizeLooseAssetKey(resolution.provider_relative_path),
                    FormatGuestWords(base, view.buffer_guest, 4)));
  }

  RecordFrontendTextureConsumerResourceBoundary(
      "frontend_texture_resource_prime",
      resource_hash,
      parsed ? parse_result : wrapper_guest);
  RecordRefiiMainMenuBlockerEvent(
      "frontend_texture_resource_prime",
      fmt::format("api={} hash=0x{:08X} name={} candidate={} texture=0x{:08X} "
                  "wrapper=0x{:08X} db20_result=0x{:08X} parse_result=0x{:08X} "
                  "parse_skipped={} provider={}",
                  api,
                  resource_hash,
                  NormalizeLooseAssetKey(name),
                  NormalizeLooseAssetKey(candidate),
                  texture_guest,
                  wrapper_guest,
                  db20_ctx.r3.u32,
                  parse_result,
                  parse_skipped ? "true" : "false",
                  NormalizeLooseAssetKey(resolution.provider_relative_path)));
  if (VfsTracingEnabled() || LooseAssetOverrideTracingEnabled() ||
      IsMainMenuTextureHashTraceAsset(name) ||
      IsMainMenuTextureHashTraceAsset(candidate) ||
      IsMainMenuTextureHashTraceAsset(resolution.provider_relative_path)) {
    REXLOG_INFO("Refii VFS frontend texture resource prime api={} "
                "hash=0x{:08X} name={} candidate={} source={} "
                "texture=0x{:08X} wrapper=0x{:08X} db20_result=0x{:08X} "
                "parse_result=0x{:08X} parsed={} parse_skipped={} provider={} "
                "texture_words={}",
                api,
                resource_hash,
                NormalizeLooseAssetKey(name),
                NormalizeLooseAssetKey(candidate),
                source,
                texture_guest,
                wrapper_guest,
                db20_ctx.r3.u32,
                parse_result,
                parsed ? "true" : "false",
                parse_skipped ? "true" : "false",
                NormalizeLooseAssetKey(resolution.provider_relative_path),
                FormatGuestWords(base, texture_guest, 32));
  }
  LogRefiiVfsChainVerdict("texture_parser",
                          static_cast<uint32_t>(source_ctx.lr),
                          name,
                          candidate,
                          resolution.provider_relative_path,
                          "synthetic_texture_object",
                          "frontend_texture_resource_prime",
                          "KeepHooked",
                          parsed ? "TextureStreamBuild_or_menu_consumer"
                                 : (parse_skipped
                                        ? "DB20_attached_followon_frame"
                                        : "DB20_or_C340_rejection"));
  return wrapper_guest != 0;
}

bool TrySynthesizeFrontendTextureDescriptorSlot(PPCContext& source_ctx,
                                                uint8_t* base,
                                                uint32_t owner_guest,
                                                uint32_t resource_hash,
                                                uint32_t ctor_lr,
                                                uint32_t resolver_lr,
                                                uint32_t pair_store_lr,
                                                uint32_t slot_store_lr,
                                                uint32_t slot_offset,
                                                uint32_t ref_object_guest,
                                                uint32_t* out_ref_object_guest,
                                                uint32_t* out_resolver_result,
                                                uint32_t* out_pair_guest) {
  if (base == nullptr || !LooksLikeGuestPointer(owner_guest) ||
      resource_hash == 0) {
    return false;
  }
  const bool has_owner_slot = slot_offset >= 248;

  LooseAssetResolution resolution;
  std::string name;
  std::string candidate;
  std::string source;
  if (!ResolveRefiiOwnedTextureDescriptorHash("FrontendTextureConsumerSynthetic",
                                              resource_hash,
                                              ctor_lr,
                                              &resolution,
                                              &name,
                                              &candidate,
                                              &source)) {
    if (VfsTracingEnabled() || LooseAssetOverrideTracingEnabled()) {
      REXLOG_WARN("Refii VFS frontend texture synthetic descriptor miss "
                  "lr=0x{:08X} hash=0x{:08X} slot={}",
                  ctor_lr,
                  resource_hash,
                  has_owner_slot ? fmt::format("object+{}", slot_offset)
                                 : "static_table");
    }
    return false;
  }

  if (!LooksLikeGuestPointer(ref_object_guest)) {
    ref_object_guest = 0;
  }

  LooseAssetResolution selected_resolution = resolution;
  std::string selected_candidate =
      candidate.empty() ? NormalizeLooseAssetKey(name)
                        : NormalizeLooseAssetKey(candidate);
  MaybeUpgradeResourceManagerTextureResolution(resource_hash,
                                               name.empty() ? selected_candidate
                                                            : name,
                                               resolver_lr,
                                               &selected_resolution,
                                               &selected_candidate);

  uint32_t native_ref_object_guest = ref_object_guest;
  bool ref_object_is_texture = false;
  if (has_owner_slot) {
    const uint32_t texture_object_guest =
        TryAllocateRefiiFrontendTextureObject(source_ctx,
                                              base,
                                              resource_hash,
                                              selected_resolution,
                                              selected_candidate,
                                              "FrontendTextureConsumerFixedSlot");
    if (LooksLikeGuestPointer(texture_object_guest)) {
      ref_object_guest = texture_object_guest;
      ref_object_is_texture = true;
    }
  }

  PPCContext alloc_ctx = source_ctx;
  const uint32_t descriptor_guest = CallLhMalloc(alloc_ctx, base, 32);
  const uint32_t hash_pointer_guest = CallLhMalloc(alloc_ctx, base, 4);
  const uint32_t ref_pointer_guest = CallLhMalloc(alloc_ctx, base, 16);
  const uint32_t pair_guest = CallLhMalloc(alloc_ctx, base, 8);
  if (!LooksLikeGuestPointer(descriptor_guest) ||
      !LooksLikeGuestPointer(hash_pointer_guest) ||
      !LooksLikeGuestPointer(ref_pointer_guest) ||
      !LooksLikeGuestPointer(pair_guest)) {
    if (VfsTracingEnabled() || LooseAssetOverrideTracingEnabled()) {
      REXLOG_WARN("Refii VFS frontend texture synthetic descriptor alloc "
                  "failed lr=0x{:08X} hash=0x{:08X} descriptor=0x{:08X} "
                  "hash_ptr=0x{:08X} ref_ptr=0x{:08X} pair=0x{:08X}",
                  ctor_lr,
                  resource_hash,
                  descriptor_guest,
                  hash_pointer_guest,
                  ref_pointer_guest,
                  pair_guest);
    }
    return false;
  }

  StoreGuestU32(base, hash_pointer_guest, resource_hash);
  StoreGuestU32(base, ref_pointer_guest + 0, ref_object_guest);
  StoreGuestU32(base, ref_pointer_guest + 4, 0x83360000u);
  StoreGuestU32(base, ref_pointer_guest + 8, 0);
  StoreGuestU32(base, ref_pointer_guest + 12, 0);

  BuildTextureResourceDescriptor(alloc_ctx,
                                 base,
                                 descriptor_guest,
                                 1,
                                 hash_pointer_guest,
                                 ref_pointer_guest);
  const uint32_t descriptor_vtable = LoadGuestU32(base, descriptor_guest);
  const uint32_t descriptor_slot12 =
      descriptor_vtable == 0 ? 0 : LoadGuestU32(base, descriptor_vtable + 12);
  const uint32_t descriptor_slot16 =
      descriptor_vtable == 0 ? 0 : LoadGuestU32(base, descriptor_vtable + 16);
  RecordFrontendTextureConsumerDescriptor(descriptor_guest,
                                          ctor_lr,
                                          resource_hash,
                                          descriptor_vtable,
                                          descriptor_slot12,
                                          descriptor_slot16,
                                          name,
                                          candidate,
                                          source,
                                          true);

  constexpr uint32_t kFrontendResolverConstantTableBase = 0x8349F9B0;
  constexpr uint32_t kFrontendResolverTableIndex = 1;
  const uint32_t table_entry_guest =
      kFrontendResolverConstantTableBase + kFrontendResolverTableIndex * 4;
  const uint32_t table_object_before = LoadGuestU32(base, table_entry_guest);
  const std::string table_before =
      FormatGuestWordsIfPointer(base, table_object_before, 12);

  PPCContext resolver_ctx = source_ctx;
  resolver_ctx.r3.u32 = descriptor_guest;
  resolver_ctx.r4.u32 = 0x3C;
  resolver_ctx.r5.u32 = kFrontendResolverTableIndex;
  resolver_ctx.lr = resolver_lr;
  uint32_t resolver_result = 0;
  std::string failure_reason;
  if (!TryResolveRefiiFrontendTextureDescriptor(resolver_ctx,
                                                base,
                                                descriptor_guest,
                                                resource_hash,
                                                resolver_lr,
                                                kFrontendResolverTableIndex,
                                                table_object_before,
                                                resolution,
                                                name,
                                                candidate,
                                                source,
                                                &resolver_result,
                                                &failure_reason)) {
    if (VfsTracingEnabled() || LooseAssetOverrideTracingEnabled()) {
      REXLOG_WARN("Refii VFS frontend texture synthetic descriptor resolver "
                  "failed lr=0x{:08X} hash=0x{:08X} descriptor=0x{:08X} "
                  "reason={} table_object=0x{:08X}",
                  resolver_lr,
                  resource_hash,
                  descriptor_guest,
                  failure_reason,
                  table_object_before);
    }
    return false;
  }

  const uint32_t table_object_after = LoadGuestU32(base, table_entry_guest);
  const std::string table_after =
      FormatGuestWordsIfPointer(base,
                                table_object_after == 0 ? table_object_before
                                                        : table_object_after,
                                12);
	  RecordFrontendTextureConsumerResolver(
	      base,
	      resolver_lr,
	      descriptor_guest,
      resource_hash,
      descriptor_vtable,
      descriptor_slot12,
      descriptor_slot16,
      kFrontendResolverTableIndex,
      table_object_after == 0 ? table_object_before : table_object_after,
      table_before,
      table_after,
	      resolver_result,
	      true);
	  if (resource_hash == kRefiiLoadingAnimationHash) {
	    RecordRefiiLoadingSequenceResolverHandle(resource_hash,
	                                             resolver_result,
	                                             resolver_lr,
	                                             kFrontendResolverTableIndex);
	    PPCContext publish_ctx = source_ctx;
	    publish_ctx.lr = resolver_lr;
	    const bool primary_published =
	        TryPublishRefiiPostGameFacePrimaryOwnerFromTrace(
	            publish_ctx, base, "loading_animation_sequence_resolved");
	    const bool primary_ready =
	        primary_published || RefiiPostGameFacePrimaryOwnerReady(base);
	    const bool primary_native_ready =
	        primary_ready &&
	        TryRunRefiiPostGameFacePrimaryOwnerNativeFromTrace(
	            source_ctx,
	            base,
	            "post_gameface_package_handoff_after_publish");
	    PPCContext frontend_ctx = source_ctx;
	    const bool frontend_run =
	        primary_ready &&
	        TryRunRefiiPostGameFaceFrontendRenderRequestFromTrace(
	            frontend_ctx, base, "post_gameface_package_handoff_signal");
	    const bool post_loading_run =
	        frontend_run &&
	        TryRunRefiiPostLoadingFrontendRenderRequestNativeFromTrace(
	            source_ctx, base, "post_gameface_package_handoff_signal");
	    const bool loading_owner_finished =
	        TryFinishRememberedRefiiLoadingOwnerFromPostFrontendSignal(
	            source_ctx, base, "post_gameface_package_handoff_signal");
	    RecordRefiiMainMenuBlockerEvent(
	        "loading_animation_sequence_resolved",
	        fmt::format("handle=0x{:08X} primary_published={} "
	                    "primary_ready={} primary_native_ready={} "
	                    "frontend_run={} post_loading_run={} "
	                    "loading_owner_finished={} globals=[{}]",
	                    resolver_result,
	                    primary_published ? "true" : "false",
	                    primary_ready ? "true" : "false",
	                    primary_native_ready ? "true" : "false",
	                    frontend_run ? "true" : "false",
	                    post_loading_run ? "true" : "false",
	                    loading_owner_finished ? "true" : "false",
	                    FormatRefiiLoadingScreenSequenceGlobals(base)));
	  }

  StoreGuestU32(base, pair_guest + 0, ref_object_guest);
  StoreGuestU32(base, pair_guest + 4, resolver_result);
  if (out_ref_object_guest != nullptr) {
    *out_ref_object_guest = ref_object_guest;
  }
  if (out_resolver_result != nullptr) {
    *out_resolver_result = resolver_result;
  }
  if (out_pair_guest != nullptr) {
    *out_pair_guest = pair_guest;
  }
  RecordFrontendTextureConsumerStore(base,
                                     "sub_82379D78",
                                     pair_store_lr,
                                     pair_guest,
                                     ref_pointer_guest,
                                     resolver_result);

  if (has_owner_slot) {
    StoreGuestU32(base, owner_guest + slot_offset + 0, ref_object_guest);
    StoreGuestU32(base, owner_guest + slot_offset + 4, resolver_result);
    RecordFrontendTextureConsumerStore(base,
                                       "sub_82378F00",
                                       slot_store_lr,
                                       owner_guest + slot_offset,
                                       pair_guest,
                                       ref_object_guest);
  }
  RecordFrontendTextureConsumerResourceBoundary(
      has_owner_slot ? "sub_82378618_synthetic_descriptor_slot"
                     : "sub_82378618_synthetic_static_descriptor",
      resource_hash,
      resolver_result);
  TryCommitRefiiFrontendTextureWrapper(
      source_ctx,
      base,
      ref_object_guest,
      resource_hash,
      selected_resolution,
      name,
      selected_candidate,
      source,
      resolver_lr,
      has_owner_slot ? "sub_82378618_synthetic_descriptor_slot"
                     : "sub_82378618_synthetic_static_descriptor");

  if (VfsTracingEnabled() || LooseAssetOverrideTracingEnabled() ||
      IsMainMenuTextureHashTraceAsset(name) ||
      IsMainMenuTextureHashTraceAsset(candidate)) {
    REXLOG_INFO("Refii VFS frontend texture synthetic descriptor {} "
                "ctor_lr=0x{:08X} resolver_lr=0x{:08X} "
                "hash=0x{:08X} name={} candidate={} source={} "
                "ref_object=0x{:08X} native_ref=0x{:08X} "
                "ref_texture={} handle=0x{:08X} owner_slots=[{}]",
                has_owner_slot ? fmt::format("slot=object+{}", slot_offset)
                               : "static_table",
                ctor_lr,
                resolver_lr,
                resource_hash,
                NormalizeLooseAssetKey(name),
                NormalizeLooseAssetKey(selected_candidate),
                source,
                ref_object_guest,
                native_ref_object_guest,
                ref_object_is_texture ? "true" : "false",
                resolver_result,
                FormatFrontendTextureConsumerOwnerSlots(base, owner_guest));
  }
  return true;
}


}  // namespace detail
}  // namespace refii::vfs
