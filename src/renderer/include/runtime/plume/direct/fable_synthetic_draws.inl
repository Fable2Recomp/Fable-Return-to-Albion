// Included inside hook_observer.cpp's anonymous namespace.

struct FablePrivateTextureRectangleDirectVertex {
  float position[4] = {};
  float texcoord0[4] = {};
  float texcoord1[4] = {};
};

bool IsNearHookStackAddress(const RendererHookArgs& args, uint32_t address);
bool IsPrivateTextureRectangleExactStackRectReturnAddress(uint32_t lr);

bool PrivateTextureRectangleFetchIsTexture(
    const std::array<uint32_t, 6>& dwords) {
  if (!HasTextureFetchDwords(dwords)) {
    return false;
  }
  const TextureFetchSnapshotInfo info = GetTextureFetchSnapshotInfo(dwords);
  return info.failure_reason == nullptr ||
         std::string_view(info.failure_reason) == "zero_base" ||
         std::string_view(info.failure_reason) == "zero_size";
}

uint32_t PrivateTextureRectangleSourceFetchAddress(const RendererHookArgs& args,
                                                   uint8_t* base,
                                                   uint32_t source) {
  if (!base || source == 0) {
    return 0;
  }

  constexpr uint32_t kTextureFetchOffset = 28u;
  const uint32_t object_fetch_address = source + kTextureFetchOffset;
  if (IsLikelyFableObjectAddress(source) &&
      IsLikelyGuestDwordAddress(object_fetch_address)) {
    const std::array<uint32_t, 6> dwords =
        LoadTextureFetchDwords(base, object_fetch_address);
    if (PrivateTextureRectangleFetchIsTexture(dwords)) {
      return object_fetch_address;
    }
  }

  if (IsNearHookStackAddress(args, source) &&
      IsLikelyGuestDwordAddress(object_fetch_address) &&
      IsLikelyGuestDwordAddress(object_fetch_address + 20u)) {
    const std::array<uint32_t, 6> dwords =
        LoadTextureFetchDwords(base, object_fetch_address);
    if (PrivateTextureRectangleFetchIsTexture(dwords)) {
      return object_fetch_address;
    }
  }

  return 0;
}

bool IsReasonablePrivateRectangleVertexBuffer(uint8_t* base,
                                              uint32_t vertex_buffer) {
  if (!base || vertex_buffer == 0) {
    return false;
  }
  if (!IsLikelyGpuByteBufferRange(vertex_buffer, sizeof(float) * 6u) &&
      !IsLikelyFableObjectAddress(vertex_buffer)) {
    return false;
  }

  for (uint32_t i = 0; i < 6; ++i) {
    const float value = LoadGuestF32(base, vertex_buffer + i * sizeof(float));
    if (!std::isfinite(value) || std::fabs(value) > 32768.0f) {
      return false;
    }
  }
  return true;
}

uint32_t FindPrivateTextureRectangleVertexBuffer(uint8_t* base,
                                                 uint32_t device_pointer) {
  if (!base || !IsLikelyFableObjectAddress(device_pointer)) {
    return 0;
  }

  const uint32_t command_end = LoadGuestU32(base, device_pointer + 48u);
  if (!IsLikelyGpuByteBufferRange(command_end, sizeof(uint32_t))) {
    return 0;
  }

  constexpr uint32_t kPrivateRectangleVertexFetchPacket = 346112u;
  constexpr uint32_t kScanBackBytes = 1024u;
  const uint32_t scan_begin =
      command_end > kScanBackBytes ? command_end - kScanBackBytes : 0u;
  for (uint32_t command = command_end & ~3u; command >= scan_begin + 4u;
       command -= 4u) {
    if (!IsLikelyGpuByteBufferRange(command - 4u, sizeof(uint32_t) * 2u)) {
      break;
    }
    if (LoadGuestU32(base, command - 4u) !=
        kPrivateRectangleVertexFetchPacket) {
      continue;
    }

    const uint32_t encoded_vertex_buffer = LoadGuestU32(base, command);
    const uint32_t vertex_buffer =
        DecodeFableGpuBufferPointer(encoded_vertex_buffer);
    if (IsReasonablePrivateRectangleVertexBuffer(base, vertex_buffer)) {
      return vertex_buffer;
    }
  }
  return 0;
}

bool BuildPrivateTextureRectangleVerticesFromArgs(
    const RendererHookArgs& args, uint8_t* base,
    std::array<float, 6>& vertices) {
  if (!base || !IsLikelyFableObjectAddress(args.r3)) {
    return false;
  }

  uint32_t left = 0;
  uint32_t top = 0;
  uint32_t right = 0;
  uint32_t bottom = 0;
  if (IsLikelyFableObjectAddress(args.r5) ||
      (IsPrivateTextureRectangleExactStackRectReturnAddress(args.lr) &&
       IsNearHookStackAddress(args, args.r5) &&
       IsLikelyGuestDwordAddress(args.r5 + 12u))) {
    left = LoadGuestU32(base, args.r5 + 0u);
    top = LoadGuestU32(base, args.r5 + 4u);
    right = LoadGuestU32(base, args.r5 + 8u);
    bottom = LoadGuestU32(base, args.r5 + 12u);
  } else {
    const uint32_t source_fetch_address =
        PrivateTextureRectangleSourceFetchAddress(args, base, args.r6);
    if (source_fetch_address == 0) {
      return false;
    }

    const std::array<uint32_t, 6> dwords =
        LoadTextureFetchDwords(base, source_fetch_address);
    if (!HasTextureFetchDwords(dwords)) {
      return false;
    }

    rex::graphics::xenos::xe_gpu_texture_fetch_t fetch = {};
    fetch.dword_0 = dwords[0];
    fetch.dword_1 = dwords[1];
    fetch.dword_2 = dwords[2];
    fetch.dword_3 = dwords[3];
    fetch.dword_4 = dwords[4];
    fetch.dword_5 = dwords[5];
    if (fetch.type != rex::graphics::xenos::FetchConstantType::kTexture) {
      return false;
    }

    if (fetch.dimension == rex::graphics::xenos::DataDimension::k1D) {
      right = fetch.size_1d.width + 1u;
      bottom = 1u;
    } else if (fetch.dimension == rex::graphics::xenos::DataDimension::k3D) {
      right = fetch.size_3d.width + 1u;
      bottom = fetch.size_3d.height + 1u;
    } else {
      right = fetch.size_2d.width + 1u;
      bottom = fetch.size_2d.height + 1u;
    }
  }

  right = (right + 7u) & ~7u;
  bottom = (bottom + 7u) & ~7u;
  if (right < left || bottom < top || right > 32768u || bottom > 32768u) {
    return false;
  }

  const float bias =
      (FableDirectGetRenderState(kFableRenderStateSlotPixelCenterOffset) & 1u)
          ? 0.0f
          : 0.5f;
  const float x0 = static_cast<float>(left) - bias;
  const float y0 = static_cast<float>(top) - bias;
  const float x1 = static_cast<float>(right) - bias;
  const float y1 = static_cast<float>(bottom) - bias;

  // Fable primitive 8 feeds three rectangle corners in hardware order:
  // top-left, top-right, bottom-right. The replacement rectangle-list geometry
  // shader reconstructs the missing bottom-left corner to match that shape.
  // Keep the values in Fable's pixel-space form. The translated GUI vertex
  // shader applies the guest matrices and half-pixel rules, so pre-normalizing
  // here distorts Bink/video and intro quad draws before direct.
  vertices = {x0, y0, x1, y0, x1, y1};
  return std::all_of(vertices.begin(), vertices.end(), [](float value) {
    return std::isfinite(value) && std::fabs(value) <= 32768.0f;
  });
}

uint32_t MixSyntheticDirectAddress(uint32_t value) {
  value ^= value >> 16u;
  value *= 0x7FEB352Du;
  value ^= value >> 15u;
  value *= 0x846CA68Bu;
  value ^= value >> 16u;
  return value;
}

uint32_t SyntheticDirectScratchBufferAddress(uint32_t namespace_base,
                                             uint32_t source,
                                             uint32_t salt) {
  const uint32_t mixed = MixSyntheticDirectAddress(
      source ^ (salt * 0x9E3779B9u) ^ (source >> 7u));
  return (namespace_base & 0xFF000000u) | (mixed & 0x00FFFFF0u);
}

uint32_t SyntheticPrivateTextureRectangleVertexBufferAddress(
    const RendererHookArgs& args) {
  const uint32_t source = args.r5 != 0 ? args.r5 : args.r6;
  const uint32_t salt =
      args.lr ^ args.r3 ^ (args.r4 << 8u) ^
      static_cast<uint32_t>(g_observer.draws + 1u);
  return SyntheticDirectScratchBufferAddress(0xFE000000u, source, salt);
}

std::array<FablePrivateTextureRectangleDirectVertex, 6>
BuildPrivateTextureRectangleDirectVertices(
    const std::array<float, 6>& corners, float u0 = 0.0f, float v0 = 0.0f,
    float u1 = 1.0f, float v1 = 1.0f) {
  const float x0 = corners[0];
  const float y0 = corners[1];
  const float x1 = corners[2];
  const float y1 = corners[5];

  const auto make_vertex = [](float x, float y, float u, float v) {
    FablePrivateTextureRectangleDirectVertex vertex;
    // 0152_vshader_guiquad reconstructs target x as POSITION.z + POSITION.x.
    // Store x - y here because POSITION.z carries the target-local y value.
    vertex.position[0] = x - y;
    vertex.position[1] = 0.0f;
    vertex.position[2] = y;
    vertex.position[3] = 1.0f;
    vertex.texcoord0[0] = u;
    vertex.texcoord0[1] = v;
    vertex.texcoord0[2] = 0.0f;
    vertex.texcoord0[3] = 1.0f;
    vertex.texcoord1[0] = u;
    vertex.texcoord1[1] = v;
    vertex.texcoord1[2] = 0.0f;
    vertex.texcoord1[3] = 1.0f;
    return vertex;
  };

  return {
      make_vertex(x0, y0, u0, v0),
      make_vertex(x0, y1, u0, v1),
      make_vertex(x1, y0, u1, v0),
      make_vertex(x1, y0, u1, v0),
      make_vertex(x0, y1, u0, v1),
      make_vertex(x1, y1, u1, v1),
  };
}

std::array<FablePrivateTextureRectangleDirectVertex, 3>
BuildPrivateTextureRectangleSingleTriangleDirectVertices(
    const std::array<float, 6>& corners, float u0 = 0.0f, float v0 = 0.0f,
    float u1 = 1.0f, float v1 = 1.0f) {
  const float x0 = corners[0];
  const float y0 = corners[1];
  const float x1 = corners[2];
  const float y1 = corners[5];
  const float width = x1 - x0;
  const float height = y1 - y0;
  const float du = u1 - u0;
  const float dv = v1 - v0;

  const auto make_vertex = [](float x, float y, float u, float v) {
    FablePrivateTextureRectangleDirectVertex vertex;
    vertex.position[0] = x - y;
    vertex.position[1] = 0.0f;
    vertex.position[2] = y;
    vertex.position[3] = 1.0f;
    vertex.texcoord0[0] = u;
    vertex.texcoord0[1] = v;
    vertex.texcoord0[2] = 0.0f;
    vertex.texcoord0[3] = 1.0f;
    vertex.texcoord1[0] = u;
    vertex.texcoord1[1] = v;
    vertex.texcoord1[2] = 0.0f;
    vertex.texcoord1[3] = 1.0f;
    return vertex;
  };

  return {
      make_vertex(x0, y0, u0, v0),
      make_vertex(x0 + width * 2.0f, y0, u0 + du * 2.0f, v0),
      make_vertex(x0, y0 + height * 2.0f, u0, v0 + dv * 2.0f),
  };
}

float LoadPrivateTextureSourceCoordinate(uint8_t* base, uint32_t address) {
  if (!base || !IsLikelyGuestDwordAddress(address)) {
    return 0.0f;
  }

  const uint32_t raw = LoadGuestU32(base, address);
  const float as_float = std::bit_cast<float>(raw);
  if (raw > 32768u && std::isfinite(as_float) && as_float >= 0.0f &&
      as_float <= 32768.0f) {
    return as_float;
  }
  if (raw <= 32768u) {
    return static_cast<float>(raw);
  }
  return 0.0f;
}

struct PrivateTextureRectangleUvBounds {
  bool valid = false;
  float u0 = 0.0f;
  float v0 = 0.0f;
  float u1 = 1.0f;
  float v1 = 1.0f;
};

PrivateTextureRectangleUvBounds ComputePrivateTextureRectangleUvBounds(
    const TextureFetchSnapshotInfo& source_fetch_info,
    const std::array<float, 6>& corners, float source_x, float source_y) {
  PrivateTextureRectangleUvBounds bounds;
  const float source_width =
      static_cast<float>(std::max(1u, source_fetch_info.width));
  const float source_height =
      static_cast<float>(std::max(1u, source_fetch_info.height));
  const float rect_width = std::fabs(corners[2] - corners[0]);
  const float rect_height = std::fabs(corners[5] - corners[1]);
  if (!std::isfinite(source_width) || !std::isfinite(source_height) ||
      !std::isfinite(rect_width) || !std::isfinite(rect_height) ||
      source_width <= 0.0f || source_height <= 0.0f || rect_width <= 0.0f ||
      rect_height <= 0.0f) {
    return bounds;
  }

  const float clamped_source_x = std::clamp(source_x, 0.0f, source_width);
  const float clamped_source_y = std::clamp(source_y, 0.0f, source_height);
  const float source_right =
      std::clamp(clamped_source_x + rect_width, 0.0f, source_width);
  const float source_bottom =
      std::clamp(clamped_source_y + rect_height, 0.0f, source_height);
  bounds.u0 = clamped_source_x / source_width;
  bounds.v0 = clamped_source_y / source_height;
  bounds.u1 = source_right / source_width;
  bounds.v1 = source_bottom / source_height;
  bounds.valid = bounds.u1 > bounds.u0 && bounds.v1 > bounds.v0 &&
                 std::isfinite(bounds.u0) && std::isfinite(bounds.v0) &&
                 std::isfinite(bounds.u1) && std::isfinite(bounds.v1);
  return bounds;
}

void PublishPrivateTextureRectangleExactSurfaceAliasLocked(
    const FablePrivateTextureCopyAction& action, const char* reason) {
  if (action.wrapper_call_serial == 0 && action.wrapper_context_key == 0 &&
      action.wrapper_symbol_address == 0) {
    return;
  }
  if (!IsLikelyFableObjectAddress(action.source_texture) ||
      !IsLikelyFableObjectAddress(action.target_surface) ||
      action.source_texture == action.target_surface) {
    return;
  }

  RendererSurfaceCopyAliasSnapshot alias;
  alias.source_surface = action.target_surface;
  alias.destination_surface = action.source_texture;
  alias.hook_address = action.wrapper_symbol_address != 0
                           ? action.wrapper_symbol_address
                           : action.lr;
  alias.serial = ++g_observer.serial;
  alias.copy_call_serial = action.wrapper_call_serial;
  alias.copy_context_key = action.wrapper_context_key;
  alias.copy_symbol_address = action.wrapper_symbol_address;
  g_observer.surface_copy_aliases[alias.destination_surface] = alias;
  FableDirectTrackSurfaceCopyAlias(alias);
  ClaimExactTitleTextureBlankProbeFromRenderActionLocked(
      action, reason ? reason : "private-rect-surface-alias");

  static uint32_t logged_private_rect_exact_surface_aliases = 0;
  if (logged_private_rect_exact_surface_aliases < 32u) {
    REXLOG_INFO(
        "Refii renderer exact private rect surface alias #{} reason={} "
        "source=0x{:08X} destination=0x{:08X} call={} context=0x{:016X} "
        "symbol=0x{:08X} kind={}",
        ++logged_private_rect_exact_surface_aliases,
        reason ? reason : "unknown", alias.source_surface,
        alias.destination_surface,
        static_cast<unsigned long long>(alias.copy_call_serial),
        static_cast<unsigned long long>(alias.copy_context_key),
        alias.copy_symbol_address,
        FablePrivateTextureCopyKindName(action.kind));
  }
}

void RecordPrivateTextureRectangleNativeAliasLocked(uint8_t* base,
                                                    uint32_t source,
                                                    uint32_t alias_target,
                                                    const char* kind,
                                                    uint32_t lr,
                                                    uint32_t render_target,
                                                    uint32_t depth_stencil,
                                                    const TextureFetchSnapshotInfo&
                                                        source_fetch_info,
                                                    const std::array<uint32_t, 6>&
                                                        source_fetch,
                                                    FablePrivateTextureCopyKind
                                                        copy_kind,
                                                    bool create_missing_source = true,
                                                    uint32_t source_slot =
                                                        0xFFFFFFFFu,
                                                    const RendererDrawEventSnapshot*
                                                        event = nullptr) {
  if (!base || !IsLikelyFableObjectAddress(source) ||
      !IsLikelyFableObjectAddress(alias_target)) {
    return;
  }

  auto source_it = FindResourceIteratorLocked(source);
  if (source_it == FableDirectGuestResourceRegistry().resources.end()) {
    if (!create_missing_source) {
      return;
    }
    RendererResourceRecord record = CreateGuestTextureResource(source,
                                                              alias_target);
    SnapshotGuestResource(record, base);
    record.serial = ++g_observer.serial;
    RendererResourceRecord& stored =
        StoreGuestResourceLocked(std::move(record), true, false);
    source_it = FableDirectGuestResourceRegistry().resources.find(
        GuestObjectAddress(stored));
  }
  if (source_it == FableDirectGuestResourceRegistry().resources.end() ||
      !IsImageResourceKind(GuestObjectKind(source_it->second))) {
    return;
  }

  if (GuestObjectParentAddress(source_it->second) != alias_target) {
    SetGuestResourceParent(source_it->second, alias_target);
    source_it->second.serial = ++g_observer.serial;
    FableDirectGuestResourceRegistry().last_resource = source_it->second;
    MarkResourceChangedLocked(source_it->second);
    RefreshQueuedDrawTextureResourceSerialLocked(source_it->second);
  }

  FablePrivateTextureCopyAction action;
  action.source_slot = source_slot;
  action.source_texture = source;
  action.source_base_address = source_fetch_info.base_address;
  action.source_width = source_fetch_info.width;
  action.source_height = source_fetch_info.height;
  action.source_format = source_fetch_info.format;
  action.source_fetch_dwords = source_fetch;
  action.target_surface = alias_target;
  action.target_format =
      copy_kind == FablePrivateTextureCopyKind::kDepth
          ? 0u
          : (event ? event->rb_color_info[0] & 0x3Fu : 0u);
  action.sampled_source.kind = FableRegisteredSampleSourceKind::kGpuSurface;
  action.sampled_source.resource = alias_target;
  action.render_target = render_target;
  action.depth_stencil = depth_stencil;
  action.lr = lr;
  action.kind = copy_kind;
  if (event) {
    action.vertex_shader = event->vertex_shader;
    action.vertex_shader_root_call_serial =
        event->vertex_shader_root_call_serial;
    action.vertex_shader_root_context_key =
        event->vertex_shader_root_context_key;
    action.vertex_shader_root_symbol_address =
        event->vertex_shader_root_symbol_address;
    action.vertex_shader_root_lr = event->vertex_shader_root_lr;
    action.pixel_shader = event->pixel_shader;
    action.pixel_shader_root_call_serial =
        event->pixel_shader_root_call_serial;
    action.pixel_shader_root_context_key =
        event->pixel_shader_root_context_key;
    action.pixel_shader_root_symbol_address =
        event->pixel_shader_root_symbol_address;
    action.pixel_shader_root_lr = event->pixel_shader_root_lr;
    action.draw_serial = event->serial;
    action.frame_index = CurrentPlumeOutputFrameIndex();
    action.rectangle_valid =
        event->private_texture_rectangle &&
        event->private_texture_corners_valid;
    if (action.rectangle_valid) {
      const auto& corners = event->private_texture_corners;
      action.rect_width = std::fabs(corners[2] - corners[0]);
      action.rect_height = std::fabs(corners[5] - corners[1]);
      action.target_width =
          static_cast<uint32_t>(std::lround(action.rect_width));
      action.target_height =
          static_cast<uint32_t>(std::lround(action.rect_height));
    }
    action.source_x = event->private_texture_source_x;
    action.source_y = event->private_texture_source_y;
    action.uv_override = event->private_texture_uv_override;
    action.u0 = event->private_texture_u0;
    action.v0 = event->private_texture_v0;
    action.u1 = event->private_texture_u1;
    action.v1 = event->private_texture_v1;
    action.wrapper_call_serial = event->private_texture_wrapper_call_serial;
    action.wrapper_context_key = event->private_texture_wrapper_context_key;
    action.wrapper_symbol_address =
        event->private_texture_wrapper_symbol_address;
    action.wrapper_source_texture =
        event->private_texture_wrapper_source_texture;
    action.wrapper_source_exact =
        event->private_texture_wrapper_source_exact;
    AttachPrivateTextureRectangleWrapperActionLocked(action);
  }
  RegisterPrivateTextureCopyActionFromObserverLocked(action);
  PublishPrivateTextureRectangleExactSurfaceAliasLocked(action, "native-alias");

  if (g_observer.logged_private_texture_rectangle_aliases < 24u) {
    REXLOG_INFO(
        "Refii renderer private texture rect native alias #{} "
        "src=0x{:08X} target=0x{:08X} kind={} lr=0x{:08X} "
        "rt=0x{:08X} depth=0x{:08X} fetch_base=0x{:08X} "
        "fetch_size={} fetch_fmt={}",
        g_observer.logged_private_texture_rectangle_aliases + 1u, source,
        alias_target, kind ? kind : "unknown", lr, render_target,
        depth_stencil, source_fetch_info.base_address, source_fetch_info.size,
        source_fetch_info.format);
    ++g_observer.logged_private_texture_rectangle_aliases;
  }
}

void PublishPrivateTextureRectangleSourceIdentityLocked(
    uint8_t* base, uint32_t source, uint32_t target,
    const TextureFetchSnapshotInfo& source_fetch_info,
    const std::array<uint32_t, 6>& source_fetch,
    uint32_t source_slot, uint32_t lr) {
  if (!base || !IsLikelyFableObjectAddress(source) ||
      !IsLikelyFableObjectAddress(target) ||
      !HasTextureFetchDwords(source_fetch)) {
    return;
  }

  auto source_it = FindResourceIteratorLocked(source);
  if (source_it == FableDirectGuestResourceRegistry().resources.end()) {
    RendererResourceRecord record = CreateGuestTextureResource(source, target);
    SnapshotGuestResource(record, base);
    record.serial = ++g_observer.serial;
    RendererResourceRecord& stored =
        StoreGuestResourceLocked(std::move(record), true, false);
    source_it = FableDirectGuestResourceRegistry().resources.find(
        GuestObjectAddress(stored));
  }
  if (source_it == FableDirectGuestResourceRegistry().resources.end() ||
      !IsImageResourceKind(GuestObjectKind(source_it->second))) {
    return;
  }

  RendererResourceRecord before = source_it->second;
  if (GuestObjectParentAddress(source_it->second) != target) {
    SetGuestResourceParent(source_it->second, target);
  }

  GuestTextureRecord texture = GuestTextureRecordFor(source_it->second);
  texture.normalized_fetch_dwords = source_fetch;
  if (GuestObjectKind(source_it->second) == RendererResourceKind::kTexture) {
    texture.raw_fetch_dwords = source_fetch;
  }
  if (source_fetch_info.base_address != 0) {
    texture.base_address = source_fetch_info.base_address;
  }
  texture.byte_size = std::max<uint32_t>(texture.byte_size,
                                         source_fetch_info.size);
  ApplyGuestTextureRecord(source_it->second, texture);
  RefreshTextureUploadFromPendingPixDecodedFrameLocked(source_it->second,
                                                       source_fetch, base);
  MarkTextureResourceChangedIfDirtyLocked(before, source_it->second);
  RegisterTextureIdentityForRecordLocked(source_it->second);
  RefreshQueuedDrawTextureResourceSerialLocked(source_it->second);
  if (source_slot < 32u) {
    RefreshQueuedDrawTextureStateLocked();
  }

  if (RendererVerboseTracingEnabled() &&
      g_observer.logged_private_texture_rectangle_aliases < 24u) {
    REXLOG_INFO(
        "Refii renderer private texture rect source identity #{} "
        "src=0x{:08X} target=0x{:08X} slot={} lr=0x{:08X} "
        "fetch_base=0x{:08X} fetch_size={} fetch_fmt={}",
        g_observer.logged_private_texture_rectangle_aliases + 1u, source,
        target, source_slot, lr, source_fetch_info.base_address,
        source_fetch_info.size, source_fetch_info.format);
    ++g_observer.logged_private_texture_rectangle_aliases;
  }
}

uint32_t PackPrivateTextureRectangleSurfaceExtent(uint32_t width,
                                                  uint32_t height) {
  width = std::clamp(width, 1u, 0x4000u);
  height = std::clamp(height, 1u, 0x8000u);
  return ((width - 1u) << 18u) | ((height - 1u) << 3u);
}

bool ResolvePrivateTextureRectangleTargetExtentLocked(
    const RendererDrawEventSnapshot& event, uint32_t& width,
    uint32_t& height) {
  const auto accept_extent = [&](uint32_t candidate_width,
                                 uint32_t candidate_height) {
    if (!IsPlausibleFableSurfaceExtent(candidate_width, candidate_height)) {
      return false;
    }
    width = candidate_width;
    height = candidate_height;
    return true;
  };

  if (event.viewport.valid && event.viewport.width > 0.0f &&
      event.viewport.height > 0.0f) {
    const uint32_t viewport_width =
        static_cast<uint32_t>(std::lround(event.viewport.width));
    const uint32_t viewport_height =
        static_cast<uint32_t>(std::lround(event.viewport.height));
    if (accept_extent(viewport_width, viewport_height)) {
      return true;
    }
  }

  if (event.scissor.valid && event.scissor.right > event.scissor.left &&
      event.scissor.bottom > event.scissor.top) {
    const uint32_t scissor_width =
        static_cast<uint32_t>(event.scissor.right - event.scissor.left);
    const uint32_t scissor_height =
        static_cast<uint32_t>(event.scissor.bottom - event.scissor.top);
    if (accept_extent(scissor_width, scissor_height)) {
      return true;
    }
  }

  if (event.private_texture_rectangle &&
      event.private_texture_corners_valid) {
    const auto& corners = event.private_texture_corners;
    const uint32_t rect_width =
        static_cast<uint32_t>(std::lround(std::fabs(corners[2] - corners[0])));
    const uint32_t rect_height =
        static_cast<uint32_t>(std::lround(std::fabs(corners[5] - corners[1])));
    if (accept_extent(rect_width, rect_height)) {
      return true;
    }
  }

  return false;
}

void PublishPrivateTextureRectangleTargetSurfaceLocked(
    uint32_t target, bool depth_target,
    const RendererDrawEventSnapshot& event) {
  if (!IsLikelyFableObjectAddress(target)) {
    return;
  }

  uint32_t width = 0;
  uint32_t height = 0;
  if (!ResolvePrivateTextureRectangleTargetExtentLocked(event, width, height)) {
    return;
  }

  const RendererResourceKind kind =
      depth_target ? RendererResourceKind::kDepthSurface
                   : RendererResourceKind::kRenderTargetSurface;
  RendererResourceRecord record =
      CreateGuestSurfaceResource(kind, target, target);
  bool is_new = true;
  auto existing_it = FindResourceIteratorLocked(target);
  if (existing_it != FableDirectGuestResourceRegistry().resources.end()) {
    record = existing_it->second;
    is_new = false;
  }

  SetGuestResourceIdentity(record, kind, target, target);

  GuestSurfaceRecord surface = GuestSurfaceRecordFor(record);
  surface.object_address = target;
  surface.parent_address = target;
  surface.render_target_info =
      depth_target ? 0u : event.rb_color_info[0];
  surface.depth_stencil_info =
      depth_target ? event.rb_depth_info : 0u;
  surface.packed_extent =
      PackPrivateTextureRectangleSurfaceExtent(width, height);
  const uint32_t surface_info =
      depth_target ? event.rb_depth_info : event.rb_color_info[0];
  surface.d3d_format = surface_info & 0x3Fu;
  surface.pitch_msaa = width;
  surface.byte_size = width * height * 4u;
  ApplyGuestSurfaceRecord(record, surface);
  SanitizeImageResourceParent(record);

  const bool shape_changed =
      is_new || !SurfaceShapeMatches(record, existing_it->second);
  if (shape_changed) {
    record.serial = ++g_observer.serial;
  } else {
    record.serial = existing_it->second.serial;
  }
  StoreGuestResourceLocked(std::move(record), is_new, is_new);
}

void RegisterPrivateTextureRectangleCopyActionLocked(
    uint32_t source, uint32_t target, uint32_t render_target,
    uint32_t depth_stencil, uint32_t vertex_shader, uint32_t pixel_shader,
    uint32_t lr, const TextureFetchSnapshotInfo& source_fetch_info,
    const std::array<uint32_t, 6>& source_fetch,
    FablePrivateTextureCopyKind copy_kind,
    const RendererDrawEventSnapshot& event,
    uint32_t source_slot = kFablePrivateRectCopyTextureSlot) {
  const bool persistent_source = IsLikelyFableObjectAddress(source);
  const bool transient_header_source =
      !persistent_source && HasTextureFetchDwords(source_fetch) &&
      (source_fetch_info.width != 0 || source_fetch_info.height != 0);
  if ((!persistent_source && !transient_header_source) ||
      !IsLikelyFableObjectAddress(target)) {
    return;
  }

  if (persistent_source) {
    PublishPrivateTextureRectangleSourceIdentityLocked(
        g_observer.memory_base, source, target, source_fetch_info, source_fetch,
        source_slot, lr);
  } else {
    UpsertTextureResourceFromFetchDwordsLocked(source_fetch,
                                               g_observer.memory_base);
  }
  PublishPrivateTextureRectangleTargetSurfaceLocked(
      target, copy_kind == FablePrivateTextureCopyKind::kDepth, event);

  FablePrivateTextureCopyAction action;
  action.source_slot = source_slot;
  action.source_texture = source;
  action.source_base_address = source_fetch_info.base_address;
  action.source_width = source_fetch_info.width;
  action.source_height = source_fetch_info.height;
  action.source_format = source_fetch_info.format;
  action.source_fetch_dwords = source_fetch;
  action.target_surface = target;
  uint32_t target_width = 0;
  uint32_t target_height = 0;
  if (ResolvePrivateTextureRectangleTargetExtentLocked(event, target_width,
                                                       target_height)) {
    action.target_width = target_width;
    action.target_height = target_height;
  }
  action.target_format =
      copy_kind == FablePrivateTextureCopyKind::kDepth
          ? event.rb_depth_info & 0x3Fu
          : event.rb_color_info[0] & 0x3Fu;
  action.sampled_source.kind = FableRegisteredSampleSourceKind::kGpuSurface;
  action.sampled_source.resource = target;
  action.render_target = render_target;
  action.depth_stencil = depth_stencil;
  action.vertex_shader = vertex_shader;
  action.vertex_shader_root_call_serial = event.vertex_shader_root_call_serial;
  action.vertex_shader_root_context_key = event.vertex_shader_root_context_key;
  action.vertex_shader_root_symbol_address =
      event.vertex_shader_root_symbol_address;
  action.vertex_shader_root_lr = event.vertex_shader_root_lr;
  action.pixel_shader = pixel_shader;
  action.pixel_shader_root_call_serial = event.pixel_shader_root_call_serial;
  action.pixel_shader_root_context_key = event.pixel_shader_root_context_key;
  action.pixel_shader_root_symbol_address =
      event.pixel_shader_root_symbol_address;
  action.pixel_shader_root_lr = event.pixel_shader_root_lr;
  action.lr = lr;
  action.draw_serial = event.serial;
  action.frame_index = CurrentPlumeOutputFrameIndex();
  action.kind = copy_kind;
  action.rectangle_valid =
      event.private_texture_rectangle && event.private_texture_corners_valid;
  if (action.rectangle_valid) {
    const auto& corners = event.private_texture_corners;
    action.rect_width = std::fabs(corners[2] - corners[0]);
    action.rect_height = std::fabs(corners[5] - corners[1]);
  }
  action.source_x = event.private_texture_source_x;
  action.source_y = event.private_texture_source_y;
  action.uv_override = event.private_texture_uv_override;
  action.u0 = event.private_texture_u0;
  action.v0 = event.private_texture_v0;
  action.u1 = event.private_texture_u1;
  action.v1 = event.private_texture_v1;
  action.wrapper_call_serial = event.private_texture_wrapper_call_serial;
  action.wrapper_context_key = event.private_texture_wrapper_context_key;
  action.wrapper_symbol_address = event.private_texture_wrapper_symbol_address;
  action.wrapper_source_texture = event.private_texture_wrapper_source_texture;
  action.wrapper_source_exact = event.private_texture_wrapper_source_exact;
  AttachPrivateTextureRectangleWrapperActionLocked(action);
  RegisterPrivateTextureCopyActionFromObserverLocked(action);
  PublishPrivateTextureRectangleExactSurfaceAliasLocked(action, "registered-action");
  static uint32_t logged_transient_private_rect_actions = 0;
  if (!persistent_source && logged_transient_private_rect_actions < 16u) {
    REXLOG_INFO(
        "Refii renderer transient private texture rect action #{} "
        "src_header=0x{:08X} target=0x{:08X} kind={} slot={} lr=0x{:08X} "
        "rt=0x{:08X} depth=0x{:08X} fetch_base=0x{:08X} "
        "fetch_size={} fetch_fmt={} draw_serial={}",
        logged_transient_private_rect_actions + 1u, source, target,
        FablePrivateTextureCopyKindName(copy_kind), source_slot, lr,
        render_target, depth_stencil, source_fetch_info.base_address,
        source_fetch_info.size, source_fetch_info.format,
        static_cast<unsigned long long>(event.serial));
    ++logged_transient_private_rect_actions;
  }
}

bool ScalePrivateTextureRectangleVerticesToTargetLocked(
    uint8_t* base, uint32_t device, uint32_t source_object,
    const TextureFetchSnapshotInfo& source_info, std::array<float, 6>& vertices) {
  if (!base || device == 0 || source_info.width == 0 ||
      source_info.height == 0) {
    return false;
  }

  uint32_t source_width = source_info.width;
  uint32_t source_height = source_info.height;
  const FableSurfaceExtent source_extent =
      DecodeFableSurfaceExtent(base, source_object);
  if (source_extent.width != 0 && source_extent.height != 0) {
    source_width = source_extent.width;
    source_height = source_extent.height;
  }

  const FableSurfaceExtent target_extent =
      CurrentFableDrawSurfaceExtent(base, device);
  if (target_extent.width == 0 || target_extent.height == 0 ||
      (target_extent.width == source_width &&
       target_extent.height == source_height)) {
    return false;
  }

  const float rect_width = std::fabs(vertices[2] - vertices[0]);
  const float rect_height = std::fabs(vertices[5] - vertices[1]);
  const float source_width_float = static_cast<float>(source_width);
  const float source_height_float = static_cast<float>(source_height);
  const float target_width = static_cast<float>(target_extent.width);
  const float target_height = static_cast<float>(target_extent.height);
  const float scale_x = target_width / source_width_float;
  const float scale_y = target_height / source_height_float;
  if (!std::isfinite(scale_x) || !std::isfinite(scale_y) ||
      scale_x <= 0.0f || scale_y <= 0.0f ||
      (std::fabs(scale_x - 1.0f) < 0.001f &&
       std::fabs(scale_y - 1.0f) < 0.001f)) {
    return false;
  }

  if (!std::isfinite(rect_width) || !std::isfinite(rect_height) ||
      rect_width <= 0.0f || rect_height <= 0.0f ||
      rect_width > source_width_float + 8.0f ||
      rect_height > source_height_float + 8.0f) {
    return false;
  }

  for (uint32_t i = 0; i < vertices.size(); i += 2u) {
    vertices[i + 0u] *= scale_x;
    vertices[i + 1u] *= scale_y;
  }
  static bool logged_private_rect_target_scale = false;
  if (!logged_private_rect_target_scale) {
    REXLOG_INFO(
        "Refii renderer scaled private texture rectangle source={}x{} "
        "target={}x{} scale={:.3f}/{:.3f}",
        source_width, source_height, target_extent.width,
        target_extent.height, scale_x, scale_y);
    logged_private_rect_target_scale = true;
  }
  return std::all_of(vertices.begin(), vertices.end(), [](float value) {
    return std::isfinite(value) && std::fabs(value) <= 32768.0f;
  });
}

void ConfigureGuiQuadVertexConstantsLocked(RendererDrawEventSnapshot& event) {
  auto& constants = event.vertex_shader_constants;
  constants.fill(0);

  float viewport_x = 0.0f;
  float viewport_y = 0.0f;
  float viewport_width = kFableNativeViewportWidth;
  float viewport_height = kFableNativeViewportHeight;
  if (event.viewport.valid && event.viewport.width > 0.0f &&
      event.viewport.height > 0.0f) {
    viewport_x = event.viewport.x;
    viewport_y = event.viewport.y;
    viewport_width = event.viewport.width;
    viewport_height = event.viewport.height;
  } else {
    viewport_width = static_cast<float>(
        std::max(1, RendererOutputWidth()) *
        std::max(1, RendererInternalScale()));
    viewport_height = static_cast<float>(
        std::max(1, RendererOutputHeight()) *
        std::max(1, RendererInternalScale()));
  }
  if (!std::isfinite(viewport_width) || viewport_width <= 0.0f) {
    viewport_width = kFableNativeViewportWidth;
  }
  if (!std::isfinite(viewport_height) || viewport_height <= 0.0f) {
    viewport_height = kFableNativeViewportHeight;
  }

  const auto store_constant = [&](uint32_t reg, float x, float y, float z,
                                  float w) {
    const uint32_t dword = reg * 4u;
    if (dword + 3u >= constants.size()) {
      return;
    }
    constants[dword + 0u] = FloatToBits(x);
    constants[dword + 1u] = FloatToBits(y);
    constants[dword + 2u] = FloatToBits(z);
    constants[dword + 3u] = FloatToBits(w);
  };

  const float clip_x_scale = 2.0f / viewport_width;
  const float clip_y_scale = -2.0f / viewport_height;
  const float clip_x_bias = -1.0f - viewport_x * clip_x_scale;
  const float clip_y_bias = 1.0f - viewport_y * clip_y_scale;

  // 0152_vshader_guiquad reconstructs POSITION.x/z into an intermediate
  // target-local vector before applying g_WorldViewProjection. Keep that
  // direct quad coordinates as identity, then apply the orthographic output transform
  // through the WVP constants.
  store_constant(0, clip_x_scale, 0.0f, 0.0f, clip_x_bias);
  store_constant(1, 0.0f, clip_y_scale, 0.0f, clip_y_bias);
  store_constant(2, 0.0f, 0.0f, 0.0f, 0.0f);
  store_constant(3, 0.0f, 0.0f, 0.0f, 1.0f);
  store_constant(113, 1.0f, 0.0f, 0.0f, 0.0f);
  store_constant(114, 1.0f, 1.0f, 0.0f, 0.0f);
  store_constant(115, 0.0f, 0.0f, 0.0f, 0.0f);
}

void ConfigurePixRenderVideoFramePixelConstantsLocked(
    RendererDrawEventSnapshot& event);
void ConfigureSyntheticPrivateTexturePipelineStateLocked(
    RendererDrawEventSnapshot& event);

RendererScissorSnapshot PrivateTextureRectangleClearRectFromCorners(
    const RendererDrawEventSnapshot& event) {
  RendererScissorSnapshot rect;
  if (!event.private_texture_corners_valid) {
    return rect;
  }
  const auto& corners = event.private_texture_corners;
  const float min_x = std::min({corners[0], corners[2], corners[4]});
  const float max_x = std::max({corners[0], corners[2], corners[4]});
  const float min_y = std::min({corners[1], corners[3], corners[5]});
  const float max_y = std::max({corners[1], corners[3], corners[5]});
  if (!std::isfinite(min_x) || !std::isfinite(max_x) ||
      !std::isfinite(min_y) || !std::isfinite(max_y) ||
      max_x <= min_x || max_y <= min_y) {
    return rect;
  }
  rect.valid = true;
  rect.left = static_cast<int32_t>(std::floor(min_x));
  rect.top = static_cast<int32_t>(std::floor(min_y));
  rect.right = static_cast<int32_t>(std::ceil(max_x));
  rect.bottom = static_cast<int32_t>(std::ceil(max_y));
  return rect;
}

bool QueuePrivateTextureRectangleInlineClearLocked(
    const RendererHookArgs& args, uint8_t* base,
    const RendererDrawEventSnapshot& draw_event, bool use_bink_movie_shader,
    const TextureFetchSnapshotInfo& source_fetch_info) {
  if (use_bink_movie_shader || source_fetch_info.base_address != 0 ||
      (args.r4 & 0x300u) == 0) {
    return false;
  }

  uint32_t clear_flags = 0;
  const uint32_t color_slot = args.r4 & 7u;
  if ((args.r4 & 0x100u) != 0 && color_slot < 4u) {
    clear_flags |= 1u << color_slot;
  }
  if ((args.r4 & 0x200u) != 0 && draw_event.depth_stencil != 0) {
    clear_flags |= 0x10u; // kFableClearDepth
  }
  if (clear_flags == 0) {
    return false;
  }

  RendererDrawEventSnapshot clear_event = draw_event;
  clear_event.source = RendererDrawEventSource::kClear;
  clear_event.clear = true;
  clear_event.clear_flags = clear_flags;
  clear_event.clear_depth =
      std::isfinite(args.f1) ? static_cast<float>(args.f1) : 1.0f;
  clear_event.clear_stencil = 0;
  clear_event.clear_rect =
      PrivateTextureRectangleClearRectFromCorners(draw_event);
  if (draw_event.private_texture_wrapper_call_serial != 0 &&
      draw_event.private_texture_wrapper_context_key != 0 &&
      draw_event.private_texture_wrapper_symbol_address != 0) {
    clear_event.clear_root_call_serial =
        draw_event.private_texture_wrapper_call_serial;
    clear_event.clear_root_context_key =
        draw_event.private_texture_wrapper_context_key;
    clear_event.clear_root_symbol_address =
        draw_event.private_texture_wrapper_symbol_address;
    clear_event.clear_root_lr = args.lr;
  }
  clear_event.draw = {};
  clear_event.draw_vertices_up = false;
  clear_event.private_texture_rectangle = false;
  clear_event.vertex_declaration = 0;
  clear_event.vertex_shader = 0;
  clear_event.pixel_shader = 0;
  clear_event.index_buffer = 0;
  std::fill(std::begin(clear_event.textures), std::end(clear_event.textures), 0);

  const uint32_t color_pointer = args.r10;
  if (color_pointer != 0 && base != nullptr) {
    for (uint32_t i = 0; i < 4u; ++i) {
      clear_event.clear_color[i] =
          LoadGuestF32(base, color_pointer + i * sizeof(float));
    }
  }

  StampObserverDrawEventMetadataLocked(clear_event);
  RecordDrawEventLocked(std::move(clear_event));

  if (g_observer.logged_private_texture_rectangle_clears < 24u) {
    REXLOG_INFO(
        "Refii renderer private texture rect inline clear #{} "
        "lr=0x{:08X} flags=0x{:08X}->0x{:08X} color_ptr=0x{:08X} "
        "color={:.4f},{:.4f},{:.4f},{:.4f} "
        "rt=0x{:08X} depth=0x{:08X} root=0x{:08X}/{} "
        "context=0x{:016X} rect={} {}/{}/{}/{}",
        g_observer.logged_private_texture_rectangle_clears + 1u, args.lr,
        args.r4, clear_flags, color_pointer, clear_event.clear_color[0],
        clear_event.clear_color[1], clear_event.clear_color[2],
        clear_event.clear_color[3], draw_event.render_targets[0],
        draw_event.depth_stencil, clear_event.clear_root_symbol_address,
        static_cast<unsigned long long>(clear_event.clear_root_call_serial),
        static_cast<unsigned long long>(clear_event.clear_root_context_key),
        clear_event.clear_rect.valid ? 1 : 0, clear_event.clear_rect.left,
        clear_event.clear_rect.top, clear_event.clear_rect.right,
        clear_event.clear_rect.bottom);
    ++g_observer.logged_private_texture_rectangle_clears;
  }
  return true;
}

void QueuePrivateTextureRectangleDrawEventLocked(const RendererHookArgs& args,
                                                 uint8_t* base,
                                                 bool prefer_rect_argument) {
  if (!base || args.r3 == 0) {
    return;
  }
  if (IsLikelyFableObjectAddress(args.r3)) {
    g_observer.last_video_device = args.r3;
  }

  uint32_t vertex_buffer = 0;
  uint32_t upload_source = 0;
  std::array<float, 6> synthetic_vertices = {};
  const bool has_synthetic_vertices =
      prefer_rect_argument &&
      BuildPrivateTextureRectangleVerticesFromArgs(args, base,
                                                   synthetic_vertices);
  if (has_synthetic_vertices) {
    vertex_buffer = SyntheticPrivateTextureRectangleVertexBufferAddress(args);
  }
  if (prefer_rect_argument &&
      vertex_buffer == 0 &&
      IsReasonablePrivateRectangleVertexBuffer(base, args.r5)) {
    vertex_buffer = args.r5;
    upload_source = args.r5;
  }
  if (vertex_buffer == 0) {
    vertex_buffer = FindPrivateTextureRectangleVertexBuffer(base, args.r3);
  }
  if (vertex_buffer == 0) {
    return;
  }

  constexpr uint32_t kBinkVideoFetchConstantDwords = 6;
  const uint32_t texture_slot = args.r4 & 7u;
  uint32_t previous_texture = 0;
  uint32_t previous_copy_texture = 0;
  std::array<uint32_t, kBinkVideoFetchConstantDwords> previous_fetch = {};
  std::array<uint32_t, kBinkVideoFetchConstantDwords> previous_copy_fetch = {};
  const uint32_t source_fetch_address =
      PrivateTextureRectangleSourceFetchAddress(args, base, args.r6);
  const std::array<uint32_t, kBinkVideoFetchConstantDwords> source_fetch =
      source_fetch_address != 0
          ? LoadTextureFetchDwords(base, source_fetch_address)
          : std::array<uint32_t, kBinkVideoFetchConstantDwords>{};
  rex::graphics::xenos::xe_gpu_texture_fetch_t source_fetch_shape = {};
  source_fetch_shape.dword_0 = source_fetch[0];
  source_fetch_shape.dword_1 = source_fetch[1];
  source_fetch_shape.dword_2 = source_fetch[2];
  source_fetch_shape.dword_3 = source_fetch[3];
  source_fetch_shape.dword_4 = source_fetch[4];
  source_fetch_shape.dword_5 = source_fetch[5];
  const TextureFetchSnapshotInfo source_fetch_info =
      GetTextureFetchSnapshotInfo(source_fetch);
  const bool source_is_decoded_video_plane =
      static_cast<uint32_t>(source_fetch_shape.format) == 2u &&
      source_fetch_info.size != 0 &&
      source_fetch_info.size <= (1280u * 720u);
  const bool use_bink_movie_shader =
      CurrentDirectPixelShaderIsBinkMovieLocked() ||
      source_is_decoded_video_plane;
  const bool use_depth_copy_shader =
      !use_bink_movie_shader &&
      FableSetSurfaceHeaderFormatIsDepth(
          static_cast<uint32_t>(source_fetch_shape.format));
  const auto set_private_rect_texture = [&](uint32_t slot, uint32_t texture) {
    if (use_bink_movie_shader) {
      FableDirectSetTexture(args.r3, slot, texture);
    } else {
      FableDirectMirrorTextureState(args.r3, slot, texture);
    }
  };
  const auto set_private_rect_stream =
      [&](uint32_t slot, const RendererStreamBindingSnapshot& stream) {
        if (use_bink_movie_shader) {
          FableDirectSetStream(args.r3, slot, stream);
        } else {
          FableDirectMirrorStreamState(args.r3, slot, stream);
        }
      };
  float private_texture_source_x = 0.0f;
  float private_texture_source_y = 0.0f;
  if (args.r7 != 0 && IsLikelyFableObjectAddress(args.r7)) {
    private_texture_source_x =
        LoadPrivateTextureSourceCoordinate(base, args.r7 + 0u);
    private_texture_source_y =
        LoadPrivateTextureSourceCoordinate(base, args.r7 + 4u);
  }
  const PrivateTextureRectangleUvBounds private_texture_uv =
      has_synthetic_vertices && !use_bink_movie_shader
          ? ComputePrivateTextureRectangleUvBounds(
                source_fetch_info, synthetic_vertices, private_texture_source_x,
                private_texture_source_y)
          : PrivateTextureRectangleUvBounds{};
  bool scoped_private_texture = false;
  bool scoped_private_copy_texture = false;
  const bool persistent_private_source = IsLikelyFableObjectAddress(args.r6);
  const bool transient_private_header_source =
      !persistent_private_source && source_fetch_address != 0 &&
      HasTextureFetchDwords(source_fetch);
  if (args.r6 != 0 &&
      (persistent_private_source || transient_private_header_source) &&
      texture_slot < kRendererD3DTextureSlots) {
    const uint32_t fetch_dword = texture_slot * kBinkVideoFetchConstantDwords;
    const FableDirectStateSnapshot direct_snapshot =
        GetFableDirectStateSnapshot();
    previous_texture = direct_snapshot.textures.textures[texture_slot];
    const auto& fetch_constants = direct_snapshot.constants.fetch;
    if (fetch_dword + previous_fetch.size() <= fetch_constants.size()) {
      std::copy_n(fetch_constants.begin() + fetch_dword,
                  previous_fetch.size(), previous_fetch.begin());
    }
    set_private_rect_texture(texture_slot, args.r6);
    if (persistent_private_source) {
      StoreTextureObjectFetchDwordsLocked(args.r6, source_fetch_address,
                                          texture_slot, base, args.lr, true,
                                          false);
    } else {
      FableDirectSetFetchConstantRange(
          fetch_dword, source_fetch.data(),
          static_cast<uint32_t>(source_fetch.size()));
      UpsertTextureResourceFromFetchDwordsLocked(source_fetch, base);
    }
    scoped_private_texture = true;
    if (!use_bink_movie_shader &&
        kFablePrivateRectCopyTextureSlot < kRendererD3DTextureSlots) {
      const uint32_t copy_fetch_dword =
          kFablePrivateRectCopyTextureSlot * kBinkVideoFetchConstantDwords;
      previous_copy_texture =
          direct_snapshot.textures.textures[kFablePrivateRectCopyTextureSlot];
      if (copy_fetch_dword + previous_copy_fetch.size() <=
          fetch_constants.size()) {
        std::copy_n(fetch_constants.begin() + copy_fetch_dword,
                    previous_copy_fetch.size(), previous_copy_fetch.begin());
      }
      set_private_rect_texture(kFablePrivateRectCopyTextureSlot, args.r6);
      if (persistent_private_source) {
        StoreTextureObjectFetchDwordsLocked(args.r6, source_fetch_address,
                                            kFablePrivateRectCopyTextureSlot,
                                            base, args.lr, true, false);
      } else {
        FableDirectSetFetchConstantRange(
            copy_fetch_dword, source_fetch.data(),
            static_cast<uint32_t>(source_fetch.size()));
      }
      scoped_private_copy_texture = true;
    }
  }

  RefreshBoundSurfacesLocked(base, args.r3);
  const bool scaled_private_texture_target =
      has_synthetic_vertices && !use_bink_movie_shader &&
      ScalePrivateTextureRectangleVerticesToTargetLocked(
          base, args.r3, args.r6, source_fetch_info, synthetic_vertices);
  const RendererLastDrawSnapshot last_draw = {
      .indexed = false,
      .primitive_type = 8,
      .base_vertex = 0,
      .start = 0,
      .primitive_count = has_synthetic_vertices ? 6u : 3u,
  };
  ++g_observer.draws;

  if (has_synthetic_vertices) {
    const auto direct_vertices = private_texture_uv.valid
                                     ? BuildPrivateTextureRectangleDirectVertices(
                                           synthetic_vertices,
                                           private_texture_uv.u0,
                                           private_texture_uv.v0,
                                           private_texture_uv.u1,
                                           private_texture_uv.v1)
                                     : BuildPrivateTextureRectangleDirectVertices(
                                           synthetic_vertices);
    UpsertSyntheticScratchBufferResourceLocked(
        RendererResourceKind::kVertexBuffer, vertex_buffer,
        direct_vertices.data(),
        static_cast<uint32_t>(sizeof(FablePrivateTextureRectangleDirectVertex) *
                              direct_vertices.size()),
        static_cast<uint32_t>(sizeof(FablePrivateTextureRectangleDirectVertex)),
        args.r3);
  } else {
    UpsertScratchBufferResourceLocked(RendererResourceKind::kVertexBuffer,
                                      vertex_buffer, sizeof(float) * 6u,
                                      sizeof(float) * 2u, args.r3,
                                      upload_source);
  }
  const RendererStreamBindingSnapshot previous_stream0 =
      GetFableDirectStateSnapshot().input.streams[0];
  const RendererStreamBindingSnapshot stream0 = {
      .buffer = vertex_buffer,
      .offset = 0,
      .stride = has_synthetic_vertices
                    ? static_cast<uint32_t>(
                          sizeof(FablePrivateTextureRectangleDirectVertex))
                    : static_cast<uint32_t>(sizeof(float) * 2u),
  };
  set_private_rect_stream(0, stream0);
  RefreshBinkVideoPlaneUploadsLocked("private-texture-rect", base);
  const size_t prior_event_count = FableDirectPendingDrawEventCount();
  RendererDrawEventSnapshot event =
      BuildDrawEventLocked(args.r3, false, false,
                           RendererDrawEventSource::kPrivateTextureRectangle,
                           last_draw);
  if (scaled_private_texture_target) {
    const FableSurfaceExtent target_extent =
        CurrentFableDrawSurfaceExtent(base, args.r3);
    if (target_extent.width != 0 && target_extent.height != 0 &&
        target_extent.width <=
            static_cast<uint32_t>(std::numeric_limits<int32_t>::max()) &&
        target_extent.height <=
            static_cast<uint32_t>(std::numeric_limits<int32_t>::max())) {
      event.viewport = RendererViewportSnapshot{
          0.0f, 0.0f, static_cast<float>(target_extent.width),
          static_cast<float>(target_extent.height), 0.0f, 1.0f, true};
      event.scissor = RendererScissorSnapshot{
          0, 0, static_cast<int32_t>(target_extent.width),
          static_cast<int32_t>(target_extent.height), true};
    }
  }
  ApplyDrawEventScratchBuffersLocked(event, vertex_buffer, stream0.stride, 0);
  event.private_texture_rectangle = true;
  event.private_texture_slot = texture_slot;
  if (has_synthetic_vertices) {
    event.private_texture_corners = synthetic_vertices;
    event.private_texture_corners_valid = true;
  } else {
    event.private_texture_corners = {
        LoadGuestF32(base, vertex_buffer + 0u),
        LoadGuestF32(base, vertex_buffer + 4u),
        LoadGuestF32(base, vertex_buffer + 8u),
        LoadGuestF32(base, vertex_buffer + 12u),
        LoadGuestF32(base, vertex_buffer + 16u),
        LoadGuestF32(base, vertex_buffer + 20u),
    };
    event.private_texture_corners_valid =
        std::all_of(event.private_texture_corners.begin(),
                    event.private_texture_corners.end(),
                    [](float value) {
                      return std::isfinite(value) &&
                             std::fabs(value) <= 32768.0f;
                    });
  }
  // This root submits its own rectangle-shaped input. Do not let a stale
  // device VS leak into the direct; the synthetic stream layout is paired
  // with the GUI quad vertex shader shape.
  UpsertSyntheticShaderResourceLocked(
      RendererResourceKind::kVertexShader,
      kSyntheticFablePrivateRectVertexShader, kFableGuiQuadVertexShaderHash,
      args.r3);
  event.vertex_shader = kSyntheticFablePrivateRectVertexShader;
  uint32_t synthetic_pixel_shader = event.pixel_shader;
  uint64_t synthetic_pixel_shader_hash = 0;
  if (synthetic_pixel_shader == 0) {
    synthetic_pixel_shader = kSyntheticFablePrivateRectCopyPixelShader;
    synthetic_pixel_shader_hash = kFablePrivateRectCopyPixelShaderHash;
    if (use_bink_movie_shader) {
      synthetic_pixel_shader = kSyntheticFablePrivateRectPixelShader;
      synthetic_pixel_shader_hash = kFableBinkMoviePixelShaderHash;
    } else if (use_depth_copy_shader) {
      synthetic_pixel_shader = kSyntheticFablePrivateRectDepthPixelShader;
      synthetic_pixel_shader_hash = kFablePrivateRectDepthPixelShaderHash;
    }
    event.pixel_shader = synthetic_pixel_shader;
  } else if (synthetic_pixel_shader == kSyntheticFablePrivateRectPixelShader) {
    synthetic_pixel_shader_hash = kFableBinkMoviePixelShaderHash;
  } else if (synthetic_pixel_shader ==
             kSyntheticFablePrivateRectDepthPixelShader) {
    synthetic_pixel_shader_hash = kFablePrivateRectDepthPixelShaderHash;
  } else if (synthetic_pixel_shader ==
             kSyntheticFablePrivateRectCopyPixelShader) {
    synthetic_pixel_shader_hash = kFablePrivateRectCopyPixelShaderHash;
  }
  if (synthetic_pixel_shader_hash != 0) {
    UpsertSyntheticShaderResourceLocked(
        RendererResourceKind::kPixelShader, synthetic_pixel_shader,
        synthetic_pixel_shader_hash, args.r3);
  }
  event.resource_serial = g_observer.serial;
  if (event.vertex_shader == kSyntheticFablePrivateRectVertexShader) {
    ConfigureGuiQuadVertexConstantsLocked(event);
    event.device_registers_valid = false;
  }
  if (event.pixel_shader == kSyntheticFablePrivateRectPixelShader) {
    ConfigurePixRenderVideoFramePixelConstantsLocked(event);
  }
  ConfigureSyntheticPrivateTexturePipelineStateLocked(event);
  event.private_texture_source_x = private_texture_source_x;
  event.private_texture_source_y = private_texture_source_y;
  if (private_texture_uv.valid) {
    event.private_texture_uv_override = true;
    event.private_texture_u0 = private_texture_uv.u0;
    event.private_texture_v0 = private_texture_uv.v0;
    event.private_texture_u1 = private_texture_uv.u1;
    event.private_texture_v1 = private_texture_uv.v1;
  }
  StampPrivateTextureRectangleWrapperEventLocked(event, args.r6);
  const bool queued_inline_clear = QueuePrivateTextureRectangleInlineClearLocked(
      args, base, event, use_bink_movie_shader, source_fetch_info);
  if (queued_inline_clear && source_fetch_info.base_address == 0) {
    if (scoped_private_texture) {
      const uint32_t fetch_dword = texture_slot * kBinkVideoFetchConstantDwords;
      set_private_rect_texture(texture_slot, previous_texture);
      if (fetch_dword + previous_fetch.size() <= kRendererFetchConstantDwords) {
        FableDirectSetFetchConstantRange(
            fetch_dword, previous_fetch.data(),
            static_cast<uint32_t>(previous_fetch.size()));
      }
    }
    if (scoped_private_copy_texture) {
      const uint32_t fetch_dword =
          kFablePrivateRectCopyTextureSlot * kBinkVideoFetchConstantDwords;
      set_private_rect_texture(kFablePrivateRectCopyTextureSlot,
                               previous_copy_texture);
      if (fetch_dword + previous_copy_fetch.size() <=
          kRendererFetchConstantDwords) {
        FableDirectSetFetchConstantRange(
            fetch_dword, previous_copy_fetch.data(),
            static_cast<uint32_t>(previous_copy_fetch.size()));
      }
    }
    return;
  }
  const uint64_t private_texture_detail_key =
      (static_cast<uint64_t>(args.lr) << 32u) ^
      (static_cast<uint64_t>(args.r6) << 1u) ^
      (static_cast<uint64_t>(event.render_targets[0]) << 17u) ^
      (static_cast<uint64_t>(event.pixel_shader) << 3u) ^
      texture_slot;
  const bool title_private_texture =
      args.r6 == 0x40131610u || args.r6 == 0x40131650u ||
      args.r6 == 0x40133CD0u || args.r6 == 0x40133D10u ||
      args.r6 == 0x40513AD0u || args.r6 == 0x40513C10u ||
      args.r6 == 0x40513D10u ||
      args.r6 == 0x40130C90u || args.r6 == 0x40130CD0u ||
      args.r6 == 0x40131C90u || args.r6 == 0x40131CD0u ||
      args.r6 == 0x40137C90u || args.r6 == 0x40137CD0u ||
      args.r6 == 0x40137D10u;
  const bool log_private_texture_detail =
      g_observer.logged_private_texture_rectangle_detail_keys.size() < 96u &&
      (RendererVerboseTracingEnabled() || g_observer.pix_decode_frames != 0 ||
       title_private_texture) &&
      g_observer.logged_private_texture_rectangle_detail_keys
          .insert(private_texture_detail_key)
          .second;
  if (log_private_texture_detail) {
    uint32_t source_x_raw = 0;
    uint32_t source_y_raw = 0;
    if (IsLikelyGuestDwordAddress(args.r7)) {
      source_x_raw = LoadGuestU32(base, args.r7 + 0u);
      source_y_raw = LoadGuestU32(base, args.r7 + 4u);
    }
    REXLOG_INFO(
        "Refii renderer private texture rect detail #{} lr=0x{:08X} "
        "flags=0x{:08X} slot={} src=0x{:08X} fetch_base=0x{:08X} "
        "fetch_size={} fetch_fmt={} fetch_reason={} r5=0x{:08X} "
        "r7=0x{:08X} src_xy={:.2f}/{:.2f} "
        "src_raw=0x{:08X}/0x{:08X} r8=0x{:08X} "
        "rt={:08X}/{:08X}/{:08X}/{:08X} "
        "depth=0x{:08X} vs=0x{:08X} ps=0x{:08X} decl=0x{:08X} "
        "vb=0x{:08X} rect={:.2f}/{:.2f}/{:.2f}/{:.2f}/{:.2f}/{:.2f} "
        "scaled={} uv={} {:.4f}/{:.4f}->{:.4f}/{:.4f} "
        "vp={} {:.1f}/{:.1f}/{:.1f}/{:.1f} sc={} {}/{}/{}/{}",
        ++g_observer.logged_private_texture_rectangle_details, args.lr,
        args.r4, texture_slot, args.r6, source_fetch_info.base_address,
        source_fetch_info.size, static_cast<uint32_t>(source_fetch_shape.format),
        source_fetch_info.failure_reason ? source_fetch_info.failure_reason
                                         : "ok",
        args.r5, args.r7, event.private_texture_source_x,
        event.private_texture_source_y, source_x_raw, source_y_raw, args.r8,
        event.render_targets[0], event.render_targets[1],
        event.render_targets[2], event.render_targets[3],
        event.depth_stencil, event.vertex_shader, event.pixel_shader,
        event.vertex_declaration, vertex_buffer,
        event.private_texture_corners[0], event.private_texture_corners[1],
        event.private_texture_corners[2], event.private_texture_corners[3],
        event.private_texture_corners[4], event.private_texture_corners[5],
        scaled_private_texture_target ? 1 : 0,
        event.private_texture_uv_override ? 1 : 0, event.private_texture_u0,
        event.private_texture_v0, event.private_texture_u1,
        event.private_texture_v1, event.viewport.valid ? 1 : 0,
        event.viewport.x, event.viewport.y, event.viewport.width,
        event.viewport.height, event.scissor.valid ? 1 : 0,
        event.scissor.left, event.scissor.top, event.scissor.right,
        event.scissor.bottom);
  }
  event = RecordDrawEventLocked(std::move(event));
  const size_t private_texture_event_count =
      FableDirectPendingDrawEventCount();
  if (!use_bink_movie_shader && title_private_texture &&
      g_observer.logged_private_texture_rectangle_exact_guards < 24u) {
    REXLOG_INFO(
        "Refii renderer private texture rectangle exact guard #{} "
        "prior={} after={} delta={} serial={} src=0x{:08X} rt=0x{:08X} "
        "ds=0x{:08X} vs=0x{:08X} ps=0x{:08X} prim={} count={} "
        "slot={} fetch_base=0x{:08X} fetch_size={} fmt={}",
        ++g_observer.logged_private_texture_rectangle_exact_guards,
        prior_event_count, private_texture_event_count,
        private_texture_event_count >= prior_event_count
            ? private_texture_event_count - prior_event_count
            : 0u,
        static_cast<unsigned long long>(event.serial), args.r6,
        event.render_targets[0], event.depth_stencil, event.vertex_shader,
        event.pixel_shader, event.draw.primitive_type,
        event.draw.primitive_count, texture_slot,
        source_fetch_info.base_address, source_fetch_info.size,
        static_cast<uint32_t>(source_fetch_shape.format));
  }
  const bool can_register_private_source =
      persistent_private_source || transient_private_header_source;
  if (!use_bink_movie_shader && can_register_private_source) {
    const uint32_t exact_target =
        use_depth_copy_shader ? event.depth_stencil : event.render_targets[0];
    RegisterPrivateTextureRectangleCopyActionLocked(
        args.r6, exact_target, event.render_targets[0], event.depth_stencil,
        event.vertex_shader, event.pixel_shader, args.lr, source_fetch_info,
        source_fetch,
        use_depth_copy_shader ? FablePrivateTextureCopyKind::kDepth
                              : FablePrivateTextureCopyKind::kColor,
        event, texture_slot);
    RegisterPrivateTextureRectangleCopyActionLocked(
        args.r6, exact_target, event.render_targets[0], event.depth_stencil,
        event.vertex_shader, event.pixel_shader, args.lr, source_fetch_info,
        source_fetch,
        use_depth_copy_shader ? FablePrivateTextureCopyKind::kDepth
                              : FablePrivateTextureCopyKind::kColor,
        event, kFablePrivateRectCopyTextureSlot);
  }
  if (!use_bink_movie_shader && source_fetch_info.base_address == 0 &&
      IsLikelyFableObjectAddress(args.r6)) {
    const uint32_t alias_target =
        use_depth_copy_shader ? event.depth_stencil : event.render_targets[0];
    RecordPrivateTextureRectangleNativeAliasLocked(
        base, args.r6, alias_target, use_depth_copy_shader ? "depth" : "color",
        args.lr, event.render_targets[0], event.depth_stencil,
        source_fetch_info, source_fetch,
        use_depth_copy_shader ? FablePrivateTextureCopyKind::kDepth
                              : FablePrivateTextureCopyKind::kColor,
        true, texture_slot, &event);
    RecordPrivateTextureRectangleNativeAliasLocked(
        base, args.r6, alias_target, use_depth_copy_shader ? "depth" : "color",
        args.lr, event.render_targets[0], event.depth_stencil,
        source_fetch_info, source_fetch,
        use_depth_copy_shader ? FablePrivateTextureCopyKind::kDepth
                              : FablePrivateTextureCopyKind::kColor,
        true, kFablePrivateRectCopyTextureSlot, &event);
  }
  if (!use_bink_movie_shader) {
    FableDirectCaptureExactNonVideoRenderCommands(event.serial);
  }
  const size_t pending_event_count_after_private_rect =
      FableDirectPendingDrawEventCount();
  if (pending_event_count_after_private_rect > prior_event_count) {
    if (!use_bink_movie_shader &&
        (event.private_texture_wrapper_call_serial != 0 ||
         event.private_texture_wrapper_context_key != 0)) {
      FablePrivateTextureCopyAction exact_wrapper_action;
      const bool has_exact_wrapper_action =
          CopyPrivateTextureWrapperActionFromRegistryLocked(
              event.private_texture_wrapper_call_serial,
              event.private_texture_wrapper_context_key, exact_wrapper_action);
      static uint32_t logged_private_rect_exact_wrapper_actions = 0;
      if (has_exact_wrapper_action &&
          logged_private_rect_exact_wrapper_actions < 32u) {
        REXLOG_INFO(
            "Refii renderer private texture rectangle exact wrapper action #{} "
            "serial={} call={} context=0x{:016X} symbol=0x{:08X} "
            "source=0x{:08X} target=0x{:08X} rt=0x{:08X} ds=0x{:08X} "
            "vs=0x{:08X} ps=0x{:08X}",
            ++logged_private_rect_exact_wrapper_actions,
            static_cast<unsigned long long>(event.serial),
            static_cast<unsigned long long>(
                event.private_texture_wrapper_call_serial),
            static_cast<unsigned long long>(
                event.private_texture_wrapper_context_key),
            event.private_texture_wrapper_symbol_address,
            exact_wrapper_action.source_texture,
            exact_wrapper_action.target_surface, event.render_targets[0],
            event.depth_stencil, event.vertex_shader, event.pixel_shader);
      }
    }
  }
  if (scoped_private_texture) {
    const uint32_t fetch_dword = texture_slot * kBinkVideoFetchConstantDwords;
    set_private_rect_texture(texture_slot, previous_texture);
    if (fetch_dword + previous_fetch.size() <= kRendererFetchConstantDwords) {
      FableDirectSetFetchConstantRange(
          fetch_dword, previous_fetch.data(),
          static_cast<uint32_t>(previous_fetch.size()));
    }
  }
  if (scoped_private_copy_texture) {
    const uint32_t fetch_dword =
        kFablePrivateRectCopyTextureSlot * kBinkVideoFetchConstantDwords;
    set_private_rect_texture(kFablePrivateRectCopyTextureSlot,
                             previous_copy_texture);
    if (fetch_dword + previous_copy_fetch.size() <=
        kRendererFetchConstantDwords) {
      FableDirectSetFetchConstantRange(
          fetch_dword, previous_copy_fetch.data(),
          static_cast<uint32_t>(previous_copy_fetch.size()));
    }
  }
  set_private_rect_stream(0, previous_stream0);
  if (!g_observer.logged_private_texture_rectangle_capture) {
    const float sample0 =
        has_synthetic_vertices ? synthetic_vertices[0]
                               : LoadGuestF32(base, vertex_buffer + 0);
    const float sample1 =
        has_synthetic_vertices ? synthetic_vertices[1]
                               : LoadGuestF32(base, vertex_buffer + 4);
    const float sample2 =
        has_synthetic_vertices ? synthetic_vertices[2]
                               : LoadGuestF32(base, vertex_buffer + 8);
    const float sample3 =
        has_synthetic_vertices ? synthetic_vertices[3]
                               : LoadGuestF32(base, vertex_buffer + 12);
    const float sample4 =
        has_synthetic_vertices ? synthetic_vertices[4]
                               : LoadGuestF32(base, vertex_buffer + 16);
    const float sample5 =
        has_synthetic_vertices ? synthetic_vertices[5]
                               : LoadGuestF32(base, vertex_buffer + 20);
    REXLOG_INFO(
        "Refii renderer captured private texture rectangle draw "
        "device=0x{:08X} flags=0x{:08X} slot={} source=0x{:08X} vb=0x{:08X} "
        "sample={:.2f}/{:.2f}/{:.2f}/{:.2f}/{:.2f}/{:.2f}",
        args.r3, args.r4, texture_slot, args.r6, vertex_buffer, sample0,
        sample1, sample2, sample3, sample4, sample5);
    g_observer.logged_private_texture_rectangle_capture = true;
  }
  TraceDrawCall("sub_82206888", last_draw);
  TrimScratchBufferResourcesLocked();
}

bool BuildPixRenderVideoFrameVerticesFromArgs(
    const RendererHookArgs& args, uint8_t* base,
    std::array<float, 6>& vertices) {
  if (!base || !IsLikelyGuestDwordAddress(args.r9) ||
      !IsLikelyGuestDwordAddress(args.r10)) {
    return false;
  }

  const float width = LoadGuestF32(base, args.r9 + 0u);
  const float height = LoadGuestF32(base, args.r9 + 4u);
  const float left = LoadGuestF32(base, args.r10 + 0u);
  const float top = LoadGuestF32(base, args.r10 + 4u);
  if (!std::isfinite(width) || !std::isfinite(height) ||
      !std::isfinite(left) || !std::isfinite(top) || width <= 0.0f ||
      height <= 0.0f || width > 32768.0f || height > 32768.0f ||
      std::fabs(left) > 32768.0f || std::fabs(top) > 32768.0f) {
    return false;
  }

  const float x0 = left - 0.5f;
  const float y0 = top - 0.5f;
  const float x1 = left + width - 0.5f;
  const float y1 = top + height - 0.5f;
  vertices = {x0, y0, x1, y0, x1, y1};
  return std::all_of(vertices.begin(), vertices.end(), [](float value) {
    return std::isfinite(value) && std::fabs(value) <= 32768.0f;
  });
}

void ConfigurePixRenderVideoFrameVertexConstantsLocked(
    RendererDrawEventSnapshot& event) {
  ConfigureGuiQuadVertexConstantsLocked(event);
}

void ConfigureSyntheticVideoOutputStateLocked(RendererDrawEventSnapshot& event,
                                              uint32_t width,
                                              uint32_t height) {
  width = std::max<uint32_t>(1u, width);
  height = std::max<uint32_t>(1u, height);

  const float min_depth =
      event.viewport.valid ? event.viewport.min_depth : 0.0f;
  const float max_depth =
      event.viewport.valid ? event.viewport.max_depth : 1.0f;
  event.viewport = RendererViewportSnapshot{
      .x = 0.0f,
      .y = 0.0f,
      .width = static_cast<float>(width),
      .height = static_cast<float>(height),
      .min_depth = min_depth,
      .max_depth = max_depth,
      .valid = true,
  };
  event.scissor = RendererScissorSnapshot{
      .left = 0,
      .top = 0,
      .right = static_cast<int32_t>(width),
      .bottom = static_cast<int32_t>(height),
      .valid = true,
  };

  // Synthetic video draws are Plume-owned replacement draws. Do not let stale
  // PM4 state inherited from the suppressed native path decide EDRAM mode,
  // color masks, depth, or alpha test for the replacement pipeline.
  event.device_registers_valid = false;
  event.pm4_registers_valid = false;
}

void ConfigurePixRenderVideoFramePixelConstantsLocked(
    RendererDrawEventSnapshot& event) {
  auto& constants = event.pixel_shader_constants;
  constants.fill(0);

  const auto store_constant = [&](uint32_t reg, float x, float y, float z,
                                  float w) {
    const uint32_t dword = reg * 4u;
    if (dword + 3u >= constants.size()) {
      return;
    }
    constants[dword + 0u] = FloatToBits(x);
    constants[dword + 1u] = FloatToBits(y);
    constants[dword + 2u] = FloatToBits(z);
    constants[dword + 3u] = FloatToBits(w);
  };

  // 0171_shader_postprocessing_horizontal_gaussblur samples decoded Bink
  // planes from .w and combines them through c253/c254/c255. Synthetic video
  // replacement owns this draw, so provide the full-range YCrCb matrix instead
  // of inheriting whatever constants the suppressed D3D9 path last had.
  store_constant(253, -0.344136f, 1.772000f, 1.000000f, 1.402000f);
  store_constant(254, 0.529136f, -0.886000f, 1.000000f, -0.714136f);
  store_constant(255, -0.701000f, 0.0f, 0.0f, 0.0f);

  constexpr uint32_t kFixedAlphaRegister = 31u;
  constexpr uint32_t kFixedAlphaDword = kFixedAlphaRegister * 4u;
  constexpr uint32_t kFloatOneBits = 0x3F800000u;
  if (kFixedAlphaDword + 3u < constants.size()) {
    constants[kFixedAlphaDword + 0u] = kFloatOneBits;
    constants[kFixedAlphaDword + 1u] = kFloatOneBits;
    constants[kFixedAlphaDword + 2u] = kFloatOneBits;
    constants[kFixedAlphaDword + 3u] = kFloatOneBits;
  }
}

void ConfigureSyntheticPrivateTexturePipelineStateLocked(
    RendererDrawEventSnapshot& event) {
  event.render_states[kFableRenderStateSlotColorWriteEnable] = 0xFu;
  event.render_states[kFableRenderStateSlotAlphaBlendEnable] = 0u;
  event.render_states[kFableRenderStateSlotSrcBlend] = 1u;
  event.render_states[kFableRenderStateSlotDestBlend] = 0u;
  event.render_states[kFableRenderStateSlotAlphaTestEnable] = 0u;
  event.render_states[kFableRenderStateSlotZEnable] = 0u;
  event.render_states[kFableRenderStateSlotZWriteEnable] = 0u;
  event.render_states[kFableRenderStateSlotHiZWriteEnable] = 0u;
  event.render_states[kFableRenderStateSlotCullMode] = 0u;
  event.render_states[kFableRenderStateSlotStencilEnable] = 0u;
  event.render_states[kFableRenderStateSlotSeparateAlphaBlendEnable] = 0u;

  // Synthetic movie/title rectangle draws own their direct pipeline state.
  // Leaving a valid guest PM4 block attached lets stale depth, alpha test, cull,
  // blend, or color-mask state override the render-state cleanup above.
  event.pm4_registers_valid = true;
  event.rb_depthcontrol = 0u;
  event.rb_color_mask = 0xFFFFu;
  event.rb_colorcontrol = 0u;
  event.rb_alpha_ref = 0u;
  event.rb_stencilrefmask = 0x00FFFF00u;
  event.rb_stencilrefmask_bf = 0x00FFFF00u;
  event.pa_su_sc_mode_cntl = 0u;
  for (uint32_t& blend_control : event.rb_blendcontrol) {
    blend_control = 0x00010001u;
  }
}

void ConfigurePixRenderVideoFrameTextureCoordinatesLocked(
    const RendererHookArgs& args, uint8_t* base,
    const std::array<float, 6>& vertices, RendererDrawEventSnapshot& event) {
  if (!base || !IsLikelyGuestDwordAddress(args.r1 + 84u)) {
    return;
  }

  // PixRenderVideoFrame receives the decoded-frame extent in the later PPC
  // stack arguments. The caller stores the pointer at SP+84 before branching
  // to sub_82BA83C0; IDA shows the native path using it as a28.
  const uint32_t extent_pointer = LoadGuestU32(base, args.r1 + 84u);
  if (!IsLikelyGuestDwordAddress(extent_pointer)) {
    return;
  }

  const float decoded_width = LoadGuestF32(base, extent_pointer + 0u);
  const float decoded_height = LoadGuestF32(base, extent_pointer + 4u);
  const float output_width = std::fabs(vertices[2] - vertices[0]);
  const float output_height = std::fabs(vertices[5] - vertices[1]);
  if (!std::isfinite(decoded_width) || !std::isfinite(decoded_height) ||
      !std::isfinite(output_width) || !std::isfinite(output_height) ||
      decoded_width <= 0.0f || decoded_height <= 0.0f ||
      output_width <= 0.0f || output_height <= 0.0f) {
    return;
  }

  const float u1 = output_width / (decoded_width * 4.0f);
  const float v1 = output_height / (decoded_height * 4.0f);
  if (!std::isfinite(u1) || !std::isfinite(v1) || u1 <= 0.0f ||
      v1 <= 0.0f || u1 > 4.0f || v1 > 4.0f) {
    return;
  }

  event.private_texture_uv_override = true;
  event.private_texture_u0 = 0.0f;
  event.private_texture_v0 = 0.0f;
  event.private_texture_u1 = u1;
  event.private_texture_v1 = v1;
  ++g_observer.pix_render_uv_overrides;
  g_observer.last_pix_output_width = output_width;
  g_observer.last_pix_output_height = output_height;
  g_observer.last_pix_decoded_width = decoded_width;
  g_observer.last_pix_decoded_height = decoded_height;
  g_observer.last_pix_u1 = u1;
  g_observer.last_pix_v1 = v1;
}

uint32_t LoadHookStackU32(uint8_t* base, const RendererHookArgs& args,
                          uint32_t offset) {
  const uint32_t address = args.r1 + offset;
  return base && IsLikelyGuestDwordAddress(address) ? LoadGuestU32(base, address)
                                                    : 0u;
}

bool IsNearHookStackAddress(const RendererHookArgs& args, uint32_t address) {
  if (args.r1 == 0 || address == 0) {
    return false;
  }

  constexpr uint32_t kStackScratchWindow = 0x4000u;
  const uint32_t low =
      args.r1 > kStackScratchWindow ? args.r1 - kStackScratchWindow : 0u;
  const uint32_t high =
      args.r1 <= UINT32_MAX - kStackScratchWindow ? args.r1 + kStackScratchWindow
                                                  : UINT32_MAX;
  return address >= low && address <= high;
}

struct BinkDecodedPlaneSetSnapshot {
  uint32_t y = 0;
  uint32_t cr = 0;
  uint32_t cb = 0;
  uint32_t width = 0;
  uint32_t height = 0;
  uint32_t chroma_width = 0;
  uint32_t chroma_height = 0;
};

bool FindLatestBinkDecodedPlaneSetLocked(BinkDecodedPlaneSetSnapshot& planes) {
  planes = {};

  struct PlaneCandidate {
    uint32_t surface = 0;
    uint32_t output_slot = 0;
    uint32_t width = 0;
    uint32_t height = 0;
    uint32_t area = 0;
    uint32_t upload_size = 0;
    uint64_t upload_generation = 0;
    uint64_t upload_hash = 0;
    uint64_t serial = 0;
    bool upload_ready = false;
    bool upload_changed_since_queue = false;
  };

  const auto candidate_for = [](uint32_t surface,
                                const BinkVideoPlaneBinding& binding) {
    PlaneCandidate candidate = {
        .surface = surface,
        .output_slot = binding.plane_output_slot,
        .upload_size = binding.upload_size,
        .serial = binding.serial,
    };

    const auto record_it = FableDirectGuestResourceRegistry().resources.find(surface);
    if (record_it != FableDirectGuestResourceRegistry().resources.end()) {
      const TextureFetchSnapshotInfo info = GetTextureFetchSnapshotInfo(
          TextureFetchDwordsForRecord(record_it->second));
      candidate.width = info.width;
      candidate.height = info.height;
      candidate.upload_generation = record_it->second.upload_generation;
      candidate.upload_hash = record_it->second.raw.hash;
      candidate.serial = std::max(candidate.serial, record_it->second.serial);
      candidate.upload_ready =
          !record_it->second.upload_data.empty() &&
          (info.size == 0 || record_it->second.upload_data.size() >= info.size);
      const auto last_generation =
          g_observer.last_queued_bink_plane_generations.find(surface);
      const auto last_hash =
          g_observer.last_queued_bink_plane_hashes.find(surface);
      candidate.upload_changed_since_queue =
          last_generation == g_observer.last_queued_bink_plane_generations.end() ||
          last_generation->second != candidate.upload_generation ||
          last_hash == g_observer.last_queued_bink_plane_hashes.end() ||
          last_hash->second != candidate.upload_hash;
    }

    if ((candidate.width == 0 || candidate.height == 0) &&
        binding.upload_size != 0) {
      if (binding.upload_size >= 1280u * 720u) {
        candidate.width = 1280u;
        candidate.height = 720u;
      } else if (binding.upload_size >= 640u * 360u) {
        candidate.width = 640u;
        candidate.height = 360u;
      }
    }

    candidate.area = candidate.width * candidate.height;
    return candidate;
  };

  const auto candidate_newer = [](const PlaneCandidate& candidate,
                                  const PlaneCandidate& best) {
    return best.surface == 0 ||
           candidate.upload_generation > best.upload_generation ||
           (candidate.upload_generation == best.upload_generation &&
            candidate.serial > best.serial);
  };

  PlaneCandidate luma = {};
  PlaneCandidate changed_luma = {};
  PlaneCandidate fallback_luma = {};
  for (const auto& [surface, binding] : g_observer.bink_planes_by_surface) {
    const PlaneCandidate candidate = candidate_for(surface, binding);
    if (!candidate.upload_ready || candidate.width < 640u ||
        candidate.height < 360u || candidate.area == 0) {
      continue;
    }
    PlaneCandidate& best =
        candidate.upload_changed_since_queue ? changed_luma : fallback_luma;
    if (candidate.area > best.area ||
        (candidate.area == best.area && candidate_newer(candidate, best))) {
      best = candidate;
    }
  }
  luma = changed_luma.surface != 0 ? changed_luma : fallback_luma;

  if (luma.surface == 0 || luma.width == 0 || luma.height == 0) {
    return false;
  }

  planes.y = luma.surface;
  planes.width = luma.width;
  planes.height = luma.height;
  planes.chroma_width = std::max<uint32_t>(1u, luma.width / 2u);
  planes.chroma_height = std::max<uint32_t>(1u, luma.height / 2u);

  PlaneCandidate expected_cr = {};
  PlaneCandidate expected_cb = {};
  const uint32_t expected_cr_slot =
      luma.output_slot <= UINT32_MAX - 4u ? luma.output_slot + 4u : 0u;
  const uint32_t expected_cb_slot =
      luma.output_slot <= UINT32_MAX - 8u ? luma.output_slot + 8u : 0u;
  std::array<PlaneCandidate, 2> chroma = {};
  for (const auto& [surface, binding] : g_observer.bink_planes_by_surface) {
    if (surface == planes.y) {
      continue;
    }
    const PlaneCandidate candidate = candidate_for(surface, binding);
    if (!candidate.upload_ready ||
        candidate.width != planes.chroma_width ||
        candidate.height != planes.chroma_height) {
      continue;
    }

    if (expected_cr_slot != 0 && candidate.output_slot == expected_cr_slot) {
      if (expected_cr.surface == 0 || candidate.serial > expected_cr.serial) {
        expected_cr = candidate;
      }
    }
    if (expected_cb_slot != 0 && candidate.output_slot == expected_cb_slot) {
      if (expected_cb.surface == 0 || candidate.serial > expected_cb.serial) {
        expected_cb = candidate;
      }
    }

    if (chroma[0].surface == 0 || candidate.serial > chroma[0].serial) {
      chroma[1] = chroma[0];
      chroma[0] = candidate;
    } else if (chroma[1].surface == 0 ||
               candidate.serial > chroma[1].serial) {
      chroma[1] = candidate;
    }
  }

  if (chroma[0].surface != 0 && chroma[1].surface != 0 &&
      chroma[0].serial > chroma[1].serial) {
    std::swap(chroma[0], chroma[1]);
  }

  if (expected_cr.surface != 0 && expected_cb.surface != 0 &&
      expected_cr.surface != expected_cb.surface) {
    planes.cr = expected_cr.surface;
    planes.cb = expected_cb.surface;
  } else {
    planes.cr = chroma[0].surface;
    planes.cb = chroma[1].surface;
  }
  return planes.cr != 0 && planes.cb != 0 && planes.cr != planes.cb;
}

void RememberQueuedBinkPlaneUploadsLocked(
    const BinkDecodedPlaneSetSnapshot& planes) {
  const std::array<uint32_t, 3> surfaces = {planes.y, planes.cr, planes.cb};
  for (uint32_t surface : surfaces) {
    if (surface == 0) {
      continue;
    }
    const auto record_it =
        FableDirectGuestResourceRegistry().resources.find(surface);
    if (record_it == FableDirectGuestResourceRegistry().resources.end()) {
      continue;
    }
    g_observer.last_queued_bink_plane_generations[surface] =
        record_it->second.upload_generation;
    g_observer.last_queued_bink_plane_hashes[surface] =
        record_it->second.raw.hash;
  }
}

bool BinkDecodedPlaneSetChangedSinceQueueLocked(
    const BinkDecodedPlaneSetSnapshot& planes) {
  const std::array<uint32_t, 3> surfaces = {planes.y, planes.cr, planes.cb};
  for (uint32_t surface : surfaces) {
    if (surface == 0) {
      return false;
    }
    const auto record_it =
        FableDirectGuestResourceRegistry().resources.find(surface);
    if (record_it == FableDirectGuestResourceRegistry().resources.end()) {
      return false;
    }
    const auto generation_it =
        g_observer.last_queued_bink_plane_generations.find(surface);
    const auto hash_it = g_observer.last_queued_bink_plane_hashes.find(surface);
    if (generation_it == g_observer.last_queued_bink_plane_generations.end() ||
        hash_it == g_observer.last_queued_bink_plane_hashes.end()) {
      return true;
    }
    const uint64_t last_hash = hash_it->second;
    const uint64_t current_hash = record_it->second.raw.hash;
    if (last_hash != current_hash ||
        (current_hash == 0 &&
         generation_it->second != record_it->second.upload_generation)) {
      return true;
    }
  }
  return false;
}

bool QueueBinkDecodedPlaneVideoDrawEventLocked(const RendererHookArgs& args,
                                               uint8_t* base) {
  const auto fail = [&](const char* reason) {
    if (g_observer.logged_pix_render_packet_builders < 16u) {
      REXLOG_INFO(
          "Refii renderer synthetic Bink/Pix video draw skipped #{} "
          "reason={} device=0x{:08X} bink_planes={} resources={}",
          g_observer.logged_pix_render_packet_builders + 1u, reason, args.r3,
          g_observer.bink_planes_by_surface.size(),
          FableDirectGuestResourceRegistry().resources.size());
      ++g_observer.logged_pix_render_packet_builders;
    }
    return false;
  };

  if (!base || args.r3 == 0) {
    return fail("bad_args");
  }

  RefreshBinkVideoPlaneUploadsLocked("synthetic-bink-video", base);
  BinkDecodedPlaneSetSnapshot planes = {};
  if (!FindLatestBinkDecodedPlaneSetLocked(planes)) {
    return fail("no_plane_set");
  }
  if (!BinkDecodedPlaneSetChangedSinceQueueLocked(planes)) {
    return false;
  }

  const FableDirectStateSnapshot direct_snapshot =
      GetFableDirectStateSnapshot();
  constexpr uint32_t kBinkVideoFetchConstantDwords = 6;
  std::array<uint32_t, 3> previous_textures = {};
  std::array<std::array<uint32_t, kBinkVideoFetchConstantDwords>, 3>
      previous_fetch = {};
  for (uint32_t slot = 0; slot < previous_textures.size(); ++slot) {
    previous_textures[slot] = direct_snapshot.textures.textures[slot];
    const uint32_t fetch_dword = slot * kBinkVideoFetchConstantDwords;
    if (fetch_dword + kBinkVideoFetchConstantDwords <=
        direct_snapshot.constants.fetch.size()) {
      std::copy_n(direct_snapshot.constants.fetch.begin() + fetch_dword,
                  kBinkVideoFetchConstantDwords, previous_fetch[slot].begin());
    }
  }
  const RendererStreamBindingSnapshot previous_stream0 =
      direct_snapshot.input.streams[0];
  const auto resolve_color_surface_target = [&](uint32_t address) -> uint32_t {
    RendererResourceRecord* record = FindResourceRecordLocked(address);
    if (!record) {
      return 0;
    }
    const RendererResourceKind kind = GuestObjectKind(*record);
    if (kind == RendererResourceKind::kDepthSurface ||
        !IsSurfaceResourceKind(kind)) {
      return 0;
    }
    const GuestSurfaceRecord surface = GuestSurfaceRecordFor(*record);
    if (FableSetSurfaceHeaderFormatIsDepth(surface.d3d_format)) {
      return 0;
    }
    const uint32_t object_address = GuestObjectAddress(*record);
    return object_address != 0 ? object_address : address;
  };
  uint32_t video_target = resolve_color_surface_target(
      direct_snapshot.targets.color[0]);
  for (uint32_t target_slot = 1;
       video_target == 0 && target_slot < direct_snapshot.targets.color.size();
       ++target_slot) {
    video_target = resolve_color_surface_target(
        direct_snapshot.targets.color[target_slot]);
  }
  if (video_target == 0) {
    video_target = resolve_color_surface_target(
        g_observer.display_present_surface_object);
  }

  const std::array<uint32_t, 3> plane_surfaces = {planes.y, planes.cr,
                                                  planes.cb};
  for (uint32_t slot = 0; slot < plane_surfaces.size(); ++slot) {
    const auto record_it =
        FableDirectGuestResourceRegistry().resources.find(plane_surfaces[slot]);
    if (record_it == FableDirectGuestResourceRegistry().resources.end()) {
      return fail("missing_plane_record");
    }
    const std::array<uint32_t, kBinkVideoFetchConstantDwords> fetch_dwords =
        TextureFetchDwordsForRecord(record_it->second);
    FableDirectSetTexture(args.r3, slot, plane_surfaces[slot]);
    FableDirectSetFetchConstantRange(slot * kBinkVideoFetchConstantDwords,
                                     fetch_dwords.data(),
                                     kBinkVideoFetchConstantDwords);
  }
  RefreshBoundSurfacesLocked(base, args.r3);
  const RendererLastDrawSnapshot last_draw = {
      .indexed = false,
      .primitive_type = 4,
      .base_vertex = 0,
      .start = 0,
      .primitive_count = 3,
  };
  ++g_observer.draws;

  uint32_t output_width =
      static_cast<uint32_t>(std::max(1, RendererOutputWidth()) *
                            std::max(1, RendererInternalScale()));
  uint32_t output_height =
      static_cast<uint32_t>(std::max(1, RendererOutputHeight()) *
                            std::max(1, RendererInternalScale()));
  const bool output_matches_video_aspect =
      output_width >= planes.width && output_height >= planes.height &&
      uint64_t(output_width) * uint64_t(planes.height) ==
          uint64_t(output_height) * uint64_t(planes.width);
  if (!output_matches_video_aspect) {
    output_width = std::max<uint32_t>(1u, planes.width);
    output_height = std::max<uint32_t>(1u, planes.height);
  }

  const float x0 = -0.5f;
  const float y0 = -0.5f;
  const float x1 = static_cast<float>(output_width) - 0.5f;
  const float y1 = static_cast<float>(output_height) - 0.5f;
  const std::array<float, 6> synthetic_vertices = {x0, y0, x1, y0, x1, y1};
  const auto direct_vertices =
      BuildPrivateTextureRectangleSingleTriangleDirectVertices(
          synthetic_vertices);
  const uint32_t vertex_buffer = SyntheticDirectScratchBufferAddress(
      0xFD000000u, planes.y,
      args.lr ^ args.r3 ^ (planes.width << 16u) ^ planes.height);
  UpsertSyntheticScratchBufferResourceLocked(
      RendererResourceKind::kVertexBuffer, vertex_buffer, direct_vertices.data(),
      static_cast<uint32_t>(sizeof(FablePrivateTextureRectangleDirectVertex) *
                            direct_vertices.size()),
      static_cast<uint32_t>(sizeof(FablePrivateTextureRectangleDirectVertex)),
      args.r3);

  const RendererStreamBindingSnapshot stream0 = {
      .buffer = vertex_buffer,
      .offset = 0,
      .stride =
          static_cast<uint32_t>(sizeof(FablePrivateTextureRectangleDirectVertex)),
  };
  FableDirectSetStream(args.r3, 0, stream0);

  RendererDrawEventSnapshot event =
      BuildDrawEventLocked(args.r3, false, false,
                           RendererDrawEventSource::kPixVideo, last_draw);
  if (video_target != 0) {
    event.render_targets[0] = video_target;
    event.render_targets[1] = 0;
    event.render_targets[2] = 0;
    event.render_targets[3] = 0;
    event.depth_stencil = 0;
  }
  ApplyDrawEventScratchBuffersLocked(event, vertex_buffer, stream0.stride, 0);
  event.private_texture_rectangle = true;
  event.private_texture_slot = 0u;
  event.private_texture_corners = synthetic_vertices;
  event.private_texture_corners_valid = true;
  event.private_texture_uv_override = true;
  event.private_texture_u0 = 0.0f;
  event.private_texture_v0 = 0.0f;
  event.private_texture_u1 = 1.0f;
  event.private_texture_v1 = 1.0f;
  ConfigureSyntheticVideoOutputStateLocked(event, output_width, output_height);
  ConfigureGuiQuadVertexConstantsLocked(event);
  ConfigurePixRenderVideoFramePixelConstantsLocked(event);
  ConfigureSyntheticPrivateTexturePipelineStateLocked(event);
  UpsertSyntheticShaderResourceLocked(
      RendererResourceKind::kVertexShader,
      kSyntheticFablePrivateRectVertexShader, kFableGuiQuadVertexShaderHash,
      args.r3);
  UpsertSyntheticShaderResourceLocked(
      RendererResourceKind::kPixelShader,
      kSyntheticFablePrivateRectPixelShader, kFableBinkMoviePixelShaderHash,
      args.r3);
  event.vertex_shader = kSyntheticFablePrivateRectVertexShader;
  event.pixel_shader = kSyntheticFablePrivateRectPixelShader;
  for (uint32_t slot = 0; slot < plane_surfaces.size(); ++slot) {
    const auto record_it =
        FableDirectGuestResourceRegistry().resources.find(plane_surfaces[slot]);
    if (record_it == FableDirectGuestResourceRegistry().resources.end() ||
        record_it->second.upload_data.empty()) {
      return fail("missing_plane_upload");
    }
    const RendererResourceRecord& record = record_it->second;
    const GuestTextureRecord texture = GuestTextureRecordFor(record);
    const std::array<uint32_t, kBinkVideoFetchConstantDwords> fetch_dwords =
        TextureFetchDwordsForRecord(record);
    RendererInlineTextureUploadSnapshot& upload = event.pix_video_planes[slot];
    upload.address = plane_surfaces[slot];
    upload.parent_address = texture.parent_address;
    upload.base_address = texture.base_address;
    upload.byte_size =
        texture.byte_size != 0
            ? texture.byte_size
            : static_cast<uint32_t>(record.upload_data.size());
    upload.raw_fetch_dwords = texture.raw_fetch_dwords;
    upload.normalized_fetch_dwords = fetch_dwords;
    upload.upload_generation = record.upload_generation;
    upload.upload_hash = record.raw.hash;
    upload.serial = record.serial != 0 ? record.serial : g_observer.serial;
    upload.data =
        std::make_shared<const std::vector<uint8_t>>(record.upload_data);
  }
  event.resource_serial = g_observer.serial;
  ++g_observer.pix_render_events;
  const uint64_t superseded_scratch_serial = g_observer.last_queued_draw_serial;
  const bool can_supersede_scratch_draw =
      superseded_scratch_serial != 0 &&
      g_observer.last_queued_draw_source ==
          RendererDrawEventSource::kScratchWrapper &&
      (video_target == 0 ||
       g_observer.last_queued_draw_render_target == video_target);
  const RendererDrawEventSnapshot recorded_event =
      RecordDrawEventLocked(std::move(event));
  RememberQueuedBinkPlaneUploadsLocked(planes);
  const bool superseded_scratch_draw =
      can_supersede_scratch_draw &&
      FableDirectRemoveSupersededScratchDrawEvent(superseded_scratch_serial,
                                                  video_target);
  const size_t superseded_target_scratch_draws =
      FableDirectRemoveSupersededScratchDrawEventsForTarget(video_target);
  if (superseded_scratch_draw &&
      g_observer.last_queued_draw_serial == superseded_scratch_serial) {
    g_observer.last_queued_draw_serial = recorded_event.serial;
    g_observer.last_queued_draw_source = recorded_event.source;
    g_observer.last_queued_draw_render_target = recorded_event.render_targets[0];
  }

  for (uint32_t slot = 0; slot < previous_textures.size(); ++slot) {
    FableDirectSetTexture(args.r3, slot, previous_textures[slot]);
    FableDirectSetFetchConstantRange(slot * kBinkVideoFetchConstantDwords,
                                     previous_fetch[slot].data(),
                                     kBinkVideoFetchConstantDwords);
  }
  FableDirectSetStream(args.r3, 0, previous_stream0);
  TraceDrawCall("synthetic-bink-video", last_draw);
  TrimScratchBufferResourcesLocked();

  if (RendererDrawTracingEnabled() &&
      g_observer.logged_pix_render_packet_builders < 16u) {
    REXLOG_INFO(
        "Refii renderer queued synthetic Bink/Pix video draw #{} "
        "device=0x{:08X} Y=0x{:08X} CR=0x{:08X} CB=0x{:08X} size={}x{} "
        "draw={}x{} chroma={}x{} rt=0x{:08X} vb=0x{:08X} "
        "serial={} replaced_guest={} removed_scratch={}",
        g_observer.logged_pix_render_packet_builders + 1u, args.r3, planes.y,
        planes.cr, planes.cb, planes.width, planes.height,
        output_width, output_height, planes.chroma_width, planes.chroma_height,
        recorded_event.render_targets[0], vertex_buffer, recorded_event.serial,
        superseded_scratch_draw ? superseded_scratch_serial : 0ull,
        superseded_target_scratch_draws);
    ++g_observer.logged_pix_render_packet_builders;
  }
  if (!RendererDrawTracingEnabled() &&
      g_observer.logged_pix_render_packet_builders < 16u) {
    REXLOG_INFO(
        "Refii renderer queued synthetic Bink/Pix video draw #{} "
        "device=0x{:08X} Y=0x{:08X} CR=0x{:08X} CB=0x{:08X} size={}x{} "
        "rt=0x{:08X} serial={}",
        g_observer.logged_pix_render_packet_builders + 1u, args.r3, planes.y,
        planes.cr, planes.cb, planes.width, planes.height,
        recorded_event.render_targets[0], recorded_event.serial);
    ++g_observer.logged_pix_render_packet_builders;
  }
  // Pix/Bink renders are presentation-time packets. If they sit in the
  // pending direct stream until a later scene flush, they replay into the title
  // path as stale video quads. Publish the boundary immediately after restoring
  // the guest state so the packet lifetime matches the native call.
  FableDirectCaptureSubmittedRenderCommands(recorded_event.serial, true);
  return true;
}

void CaptureRootTextureHeaderLocked(std::string_view symbol,
                                    const RendererHookArgs& args,
                                    uint8_t* base,
                                    uint32_t texture_header,
                                    std::string_view source_name) {
  if (!base || texture_header == 0 ||
      !IsLikelyGuestDwordAddress(texture_header) ||
      !IsLikelyGuestDwordAddress(texture_header + 48u)) {
    return;
  }

  constexpr uint32_t kTextureFetchOffset = 28u;
  const uint32_t fetch_address = texture_header + kTextureFetchOffset;
  const std::array<uint32_t, 6> dwords =
      LoadTextureFetchDwords(base, fetch_address);
  if (!HasTextureFetchDwords(dwords)) {
    return;
  }

  UpsertTextureResourceFromFetchDwordsLocked(dwords, base);
  const TextureFetchSnapshotInfo fetch_info = GetTextureFetchSnapshotInfo(dwords);

  const bool stack_scratch = IsNearHookStackAddress(args, texture_header);
  bool tracked_object = false;
  uint32_t texture_object = 0;
  uint32_t parent_texture = 0;
  if (!stack_scratch && IsLikelyFableObjectAddress(texture_header)) {
    StoreTextureObjectFetchDwordsLocked(texture_header, fetch_address, 32u,
                                        base, args.lr, true, false);
    tracked_object = true;
    texture_object = texture_header;
    if (const auto record_it =
            FableDirectGuestResourceRegistry().resources.find(texture_header);
        record_it != FableDirectGuestResourceRegistry().resources.end()) {
      parent_texture = GuestObjectParentAddress(record_it->second);
    }
    if (IsPlausibleFableSurfaceExtent(fetch_info.width, fetch_info.height)) {
      g_observer.last_root_texture_header = texture_header;
      g_observer.last_root_texture_width = fetch_info.width;
      g_observer.last_root_texture_height = fetch_info.height;
      g_observer.last_root_texture_format = fetch_info.format;
      g_observer.last_root_texture_serial = g_observer.serial;
    }
  }
  if (tracked_object) {
    auto record_it = FindResourceIteratorLocked(texture_header);
    if (record_it == FableDirectGuestResourceRegistry().resources.end()) {
      record_it = FindResourceIteratorLocked(fetch_info.base_address);
    }
    if (record_it != FableDirectGuestResourceRegistry().resources.end()) {
      RememberExactTitleTextureRefreshCandidateLocked(
          record_it->second, fetch_info, source_name,
          ExactTitleTextureProducerSymbolAddress(symbol), args.lr,
          texture_header);
    }
  }
  RegisterExactTextureHeaderActionLocked(
      symbol, args, texture_header, fetch_address, dwords, fetch_info,
      stack_scratch, texture_object, parent_texture);

  if ((RendererDrawTracingEnabled() || RendererResourceTracingEnabled()) &&
      g_observer.logged_inline_texture_descriptor_builds < 16u) {
    REXLOG_INFO(
        "Refii renderer root texture header #{} symbol={} source={} "
        "header=0x{:08X} stack={} object={} base=0x{:08X} size={} "
        "extent={}x{} fmt={} "
        "dwords={:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X} lr=0x{:08X}",
        g_observer.logged_inline_texture_descriptor_builds + 1u, symbol,
        source_name, texture_header, stack_scratch ? 1 : 0,
        tracked_object ? 1 : 0, fetch_info.base_address, fetch_info.size,
        fetch_info.width, fetch_info.height, fetch_info.format, dwords[0],
        dwords[1], dwords[2], dwords[3], dwords[4], dwords[5], args.lr);
    ++g_observer.logged_inline_texture_descriptor_builds;
  }
}

void QueuePixRenderVideoFrameDrawEventLocked(const RendererHookArgs& args,
                                             uint8_t* base) {
  ++g_observer.pix_render_hooks;
  g_observer.last_pix_packet = args.r8;

  if (!base || args.r3 == 0 || !IsLikelyGuestDwordAddress(args.r8 + 28u)) {
    ++g_observer.pix_render_bad_packets;
    return;
  }

  const std::array<uint32_t, 6> pix_fetch_dwords =
      LoadTextureFetchDwords(base, args.r8 + 28u);
  const TextureFetchSnapshotInfo pix_fetch_info =
      GetTextureFetchSnapshotInfo(pix_fetch_dwords);
  g_observer.last_pix_fetch_base = pix_fetch_info.base_address;
  g_observer.last_pix_fetch_size = pix_fetch_info.size;
  rex::graphics::xenos::xe_gpu_texture_fetch_t pix_fetch = {};
  pix_fetch.dword_0 = pix_fetch_dwords[0];
  pix_fetch.dword_1 = pix_fetch_dwords[1];
  pix_fetch.dword_2 = pix_fetch_dwords[2];
  pix_fetch.dword_3 = pix_fetch_dwords[3];
  pix_fetch.dword_4 = pix_fetch_dwords[4];
  pix_fetch.dword_5 = pix_fetch_dwords[5];
  g_observer.last_pix_fetch_format =
      static_cast<uint32_t>(pix_fetch.format);
  if (!HasTextureFetchDwords(pix_fetch_dwords) ||
      pix_fetch_info.base_address == 0 || pix_fetch_info.size == 0) {
    ++g_observer.pix_render_bad_packets;
    return;
  }
  const uint32_t pix_texture_handle = pix_fetch_info.base_address;
  g_observer.last_pix_texture = pix_texture_handle;

  std::array<float, 6> synthetic_vertices = {};
  if (!BuildPixRenderVideoFrameVerticesFromArgs(args, base,
                                                synthetic_vertices)) {
    ++g_observer.pix_render_bad_vertices;
    return;
  }

  constexpr uint32_t kTextureSlot = 0;
  constexpr uint32_t kBinkVideoFetchConstantDwords = 6;
  const uint32_t vertex_buffer = SyntheticDirectScratchBufferAddress(
      0xFD000000u, args.r8, args.lr ^ args.r3);

  const FableDirectStateSnapshot direct_snapshot =
      GetFableDirectStateSnapshot();
  uint32_t previous_texture = direct_snapshot.textures.textures[kTextureSlot];
  std::array<uint32_t, kBinkVideoFetchConstantDwords> previous_fetch = {};
  const auto& fetch_constants = direct_snapshot.constants.fetch;
  std::copy_n(fetch_constants.begin(),
              previous_fetch.size(), previous_fetch.begin());
  const RendererStreamBindingSnapshot previous_stream0 =
      direct_snapshot.input.streams[0];
  FableDirectSetTexture(args.r3, kTextureSlot, pix_texture_handle);
  // PixRenderVideoFrame consumes a dynamic texture object whose backing memory
  // is updated by the Bink/Pix decode path. Key it by the fetch base rather
  // than the transient stack packet so the draw samples the decoded frame
  // resource directly.
  StoreTextureObjectFetchDwordsLocked(pix_texture_handle, args.r8 + 28u,
                                      kTextureSlot, base, args.lr, true,
                                      false);

  RefreshBoundSurfacesLocked(base, args.r3);
  const RendererLastDrawSnapshot last_draw = {
      .indexed = false,
      .primitive_type = 4,
      .base_vertex = 0,
      .start = 0,
      .primitive_count = 3,
  };
  ++g_observer.draws;

  const auto direct_vertices =
      BuildPrivateTextureRectangleSingleTriangleDirectVertices(
          synthetic_vertices);
  UpsertSyntheticScratchBufferResourceLocked(
      RendererResourceKind::kVertexBuffer, vertex_buffer, direct_vertices.data(),
      static_cast<uint32_t>(sizeof(FablePrivateTextureRectangleDirectVertex) *
                            direct_vertices.size()),
      static_cast<uint32_t>(sizeof(FablePrivateTextureRectangleDirectVertex)),
      args.r3);
  const RendererStreamBindingSnapshot stream0 = {
      .buffer = vertex_buffer,
      .offset = 0,
      .stride =
          static_cast<uint32_t>(sizeof(FablePrivateTextureRectangleDirectVertex)),
  };
  FableDirectSetStream(args.r3, 0, stream0);
  RendererDrawEventSnapshot event =
      BuildDrawEventLocked(args.r3, false, false,
                           RendererDrawEventSource::kPixVideo, last_draw);
  ApplyDrawEventScratchBuffersLocked(event, vertex_buffer, stream0.stride, 0);
  event.private_texture_rectangle = true;
  event.private_texture_slot = kTextureSlot;
  event.private_texture_corners = synthetic_vertices;
  event.private_texture_corners_valid = true;
  ConfigureSyntheticVideoOutputStateLocked(
      event,
      static_cast<uint32_t>(
          std::max(1.0f, std::fabs(synthetic_vertices[2] - synthetic_vertices[0]))),
      static_cast<uint32_t>(
          std::max(1.0f, std::fabs(synthetic_vertices[5] - synthetic_vertices[1]))));
  ConfigurePixRenderVideoFrameVertexConstantsLocked(event);
  ConfigurePixRenderVideoFramePixelConstantsLocked(event);
  ConfigurePixRenderVideoFrameTextureCoordinatesLocked(
      args, base, synthetic_vertices, event);
  ConfigureSyntheticPrivateTexturePipelineStateLocked(event);
  UpsertSyntheticShaderResourceLocked(
      RendererResourceKind::kVertexShader,
      kSyntheticFablePrivateRectVertexShader, kFableGuiQuadVertexShaderHash,
      args.r3);
  UpsertSyntheticShaderResourceLocked(
      RendererResourceKind::kPixelShader,
      kSyntheticFablePrivateRectPixelShader, kFableBinkMoviePixelShaderHash,
      args.r3);
  event.vertex_shader = kSyntheticFablePrivateRectVertexShader;
  event.pixel_shader = kSyntheticFablePrivateRectPixelShader;
  event.resource_serial = g_observer.serial;
  ++g_observer.pix_render_events;
  const RendererDrawEventSnapshot recorded_event =
      RecordDrawEventLocked(std::move(event));

  FableDirectSetTexture(args.r3, kTextureSlot, previous_texture);
  FableDirectSetFetchConstantRange(
      0, previous_fetch.data(), static_cast<uint32_t>(previous_fetch.size()));
  FableDirectSetStream(args.r3, 0, previous_stream0);
  TraceDrawCall("sub_82BA83C0", last_draw);
  TrimScratchBufferResourcesLocked();
  FableDirectCaptureSubmittedRenderCommands(recorded_event.serial, true);
}
