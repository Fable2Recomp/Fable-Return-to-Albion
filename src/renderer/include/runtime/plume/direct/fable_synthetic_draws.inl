// Included inside hook_observer.cpp's anonymous namespace while the private
// texture rectangle observer is migrated into runtime/hooks/direct modules.

void PublishPrivateTextureRectangleExactSurfaceAliasLocked(
    const FablePrivateTextureCopyAction& action, const char* reason) {
  FablePrivateTextureRectanglePublishExactSurfaceAlias(
      action, reason, PrivateTextureRectangleSurfaceAliasCallbacksLocked());
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
  PublishPrivateTextureRectangleExactSurfaceAliasLocked(action,
                                                       "exact-surface-alias");

  if (g_observer.logged_private_texture_rectangle_aliases < 24u) {
    REXLOG_INFO(
        "Refii renderer private texture rect exact surface alias #{} "
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
      !FableTextureFetchHasDwords(source_fetch)) {
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

  TextureObserverStateFingerprint before =
      TextureObserverFingerprint(source_it->second);
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

void PublishPrivateTextureRectangleTargetSurfaceLocked(
    uint32_t target, bool depth_target,
    const RendererDrawEventSnapshot& event) {
  if (!IsLikelyFableObjectAddress(target)) {
    return;
  }

  uint32_t width = 0;
  uint32_t height = 0;
  if (!FablePrivateTextureRectangleTargetExtent(event, width, height)) {
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
  FablePrivateTextureRectangleRegisterCopyAction(
      g_observer.memory_base, source, target, render_target, depth_stencil,
      vertex_shader, pixel_shader, lr, source_fetch_info, source_fetch,
      copy_kind, event, source_slot,
      PrivateTextureRectangleCopyActionCallbacksLocked());
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
  if (target_extent.width == 0 || target_extent.height == 0) {
    return false;
  }

  FablePrivateRectTargetScaleResult scale_result;
  if (!FablePrivateTextureRectangleScaleVerticesToTarget(
          {
              .source_width = source_width,
              .source_height = source_height,
              .target_width = target_extent.width,
              .target_height = target_extent.height,
          },
          vertices, &scale_result)) {
    return false;
  }

  static bool logged_private_rect_target_scale = false;
  if (!logged_private_rect_target_scale) {
    REXLOG_INFO(
        "Refii renderer scaled private texture rectangle source={}x{} "
        "target={}x{} scale={:.3f}/{:.3f}",
        scale_result.source_width, scale_result.source_height,
        scale_result.target_width, scale_result.target_height,
        scale_result.scale_x, scale_result.scale_y);
    logged_private_rect_target_scale = true;
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
      FablePrivateTextureRectangleBuildVerticesFromArgs(args, base, PrivateTextureRectangleObserverGeometryCallbacks(),
                                                   synthetic_vertices);
  if (has_synthetic_vertices) {
    const uint32_t source = args.r5 != 0 ? args.r5 : args.r6;
    const uint32_t salt =
        args.lr ^ args.r3 ^ (args.r4 << 8u) ^
        static_cast<uint32_t>(g_observer.draws + 1u);
    vertex_buffer =
        SyntheticPrivateTextureRectangleVertexBufferAddress(source, salt);
  }
  if (prefer_rect_argument &&
      vertex_buffer == 0 &&
      FablePrivateTextureRectangleReasonableVertexBuffer(base, args.r5)) {
    vertex_buffer = args.r5;
    upload_source = args.r5;
  }
  if (vertex_buffer == 0) {
    vertex_buffer = FablePrivateTextureRectangleFindVertexBuffer(base, args.r3);
  }
  if (vertex_buffer == 0) {
    return;
  }
  const uint32_t texture_slot = args.r4 & 7u;
  uint32_t previous_texture = 0;
  uint32_t previous_copy_texture = 0;
  std::array<uint32_t, kFableVideoFetchConstantDwords> previous_fetch = {};
  std::array<uint32_t, kFableVideoFetchConstantDwords> previous_copy_fetch = {};
  const uint32_t source_fetch_address =
      FablePrivateTextureRectangleSourceFetchAddress(args, base, args.r6, PrivateTextureRectangleObserverGeometryCallbacks());
  const std::array<uint32_t, kFableVideoFetchConstantDwords> source_fetch =
      source_fetch_address != 0
          ? FableLoadTextureFetchDwords(base, source_fetch_address)
          : std::array<uint32_t, kFableVideoFetchConstantDwords>{};
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
      CurrentDirectPixelShaderIsVideoLocked() ||
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
        FablePrivateTextureRectangleSourceCoordinate(base, args.r7 + 0u, PrivateTextureRectangleObserverGeometryCallbacks());
    private_texture_source_y =
        FablePrivateTextureRectangleSourceCoordinate(base, args.r7 + 4u, PrivateTextureRectangleObserverGeometryCallbacks());
  }
  const FablePrivateRectUvBounds private_texture_uv =
      has_synthetic_vertices && !use_bink_movie_shader
          ? ComputePrivateTextureRectangleUvBounds(
                source_fetch_info.width, source_fetch_info.height,
                synthetic_vertices, private_texture_source_x,
                private_texture_source_y)
          : FablePrivateRectUvBounds{};
  bool scoped_private_texture = false;
  bool scoped_private_copy_texture = false;
  const bool persistent_private_source = IsLikelyFableObjectAddress(args.r6);
  const bool transient_private_header_source =
      !persistent_private_source && source_fetch_address != 0 &&
      FableTextureFetchHasDwords(source_fetch);
  if (args.r6 != 0 &&
      (persistent_private_source || transient_private_header_source) &&
      texture_slot < kRendererD3DTextureSlots) {
    const uint32_t fetch_dword = texture_slot * kFableVideoFetchConstantDwords;
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
          kFablePrivateRectCopyTextureSlot * kFableVideoFetchConstantDwords;
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
        static_cast<uint32_t>(sizeof(FablePrivateRectDirectVertex) *
                              direct_vertices.size()),
        static_cast<uint32_t>(sizeof(FablePrivateRectDirectVertex)),
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
                          sizeof(FablePrivateRectDirectVertex))
                    : static_cast<uint32_t>(sizeof(float) * 2u),
  };
  set_private_rect_stream(0, stream0);
  const size_t prior_event_count = FableDirectStagedDrawEventCount();
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
      kFableSyntheticPrivateRectVertexShader, kFableGuiQuadVertexShaderHash,
      args.r3);
  event.vertex_shader = kFableSyntheticPrivateRectVertexShader;
  uint32_t synthetic_pixel_shader = event.pixel_shader;
  uint64_t synthetic_pixel_shader_hash = 0;
  if (synthetic_pixel_shader == 0) {
    synthetic_pixel_shader = kFableSyntheticPrivateRectCopyPixelShader;
    synthetic_pixel_shader_hash = kFablePrivateRectCopyPixelShaderHash;
    if (use_bink_movie_shader) {
      synthetic_pixel_shader = kFableSyntheticPrivateRectPixelShader;
      synthetic_pixel_shader_hash = kFablePixRenderVideoFramePixelShaderHash;
    } else if (use_depth_copy_shader) {
      synthetic_pixel_shader = kFableSyntheticPrivateRectDepthPixelShader;
      synthetic_pixel_shader_hash = kFablePrivateRectDepthPixelShaderHash;
    }
    event.pixel_shader = synthetic_pixel_shader;
  } else if (synthetic_pixel_shader == kFableSyntheticPrivateRectPixelShader) {
    synthetic_pixel_shader_hash = kFablePixRenderVideoFramePixelShaderHash;
  } else if (synthetic_pixel_shader ==
             kFableSyntheticPrivateRectDepthPixelShader) {
    synthetic_pixel_shader_hash = kFablePrivateRectDepthPixelShaderHash;
  } else if (synthetic_pixel_shader ==
             kFableSyntheticPrivateRectCopyPixelShader) {
    synthetic_pixel_shader_hash = kFablePrivateRectCopyPixelShaderHash;
  }
  if (synthetic_pixel_shader_hash != 0) {
    UpsertSyntheticShaderResourceLocked(
        RendererResourceKind::kPixelShader, synthetic_pixel_shader,
        synthetic_pixel_shader_hash, args.r3);
  }
  event.resource_serial = g_observer.serial;
  if (event.vertex_shader == kFableSyntheticPrivateRectVertexShader) {
    FableVideoConfigureGuiQuadVertexConstants(event);
    event.device_registers_valid = false;
  }
  if (event.pixel_shader == kFableSyntheticPrivateRectPixelShader) {
    FablePixVideoConfigurePixelConstants(event);
  }
  FableVideoConfigureSyntheticPrivateTexturePipelineState(event);
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
  const bool queued_inline_clear =
      FablePrivateTextureRectangleQueueInlineClear(
          args, base, event, use_bink_movie_shader, source_fetch_info,
          PrivateTextureRectangleInlineClearCallbacksLocked());
  if (queued_inline_clear && source_fetch_info.base_address == 0) {
    if (scoped_private_texture) {
      const uint32_t fetch_dword = texture_slot * kFableVideoFetchConstantDwords;
      set_private_rect_texture(texture_slot, previous_texture);
      if (fetch_dword + previous_fetch.size() <= kRendererFetchConstantDwords) {
        FableDirectSetFetchConstantRange(
            fetch_dword, previous_fetch.data(),
            static_cast<uint32_t>(previous_fetch.size()));
      }
    }
    if (scoped_private_copy_texture) {
      const uint32_t fetch_dword =
          kFablePrivateRectCopyTextureSlot * kFableVideoFetchConstantDwords;
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
  if (use_bink_movie_shader) {
    FableDirectCaptureSubmittedRenderCommands(event.serial, true);
  }
  const size_t private_texture_event_count =
      FableDirectStagedDrawEventCount();
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
  const size_t staged_event_count_after_private_rect =
      FableDirectStagedDrawEventCount();
  if (staged_event_count_after_private_rect > prior_event_count) {
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
    const uint32_t fetch_dword = texture_slot * kFableVideoFetchConstantDwords;
    set_private_rect_texture(texture_slot, previous_texture);
    if (fetch_dword + previous_fetch.size() <= kRendererFetchConstantDwords) {
      FableDirectSetFetchConstantRange(
          fetch_dword, previous_fetch.data(),
          static_cast<uint32_t>(previous_fetch.size()));
    }
  }
  if (scoped_private_copy_texture) {
    const uint32_t fetch_dword =
        kFablePrivateRectCopyTextureSlot * kFableVideoFetchConstantDwords;
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

void PublishPixRenderVideoFrameInlineTextureLocked(
    const RendererInlineTextureUploadSnapshot& upload) {
  if (!upload.ready()) {
    return;
  }

  auto it = FindResourceIteratorLocked(upload.address);
  RendererResourceRecord* record = nullptr;
  bool created = false;
  if (it == FableDirectGuestResourceRegistry().resources.end()) {
    RendererResourceRecord created_record =
        CreateGuestTextureResource(upload.address, upload.parent_address);
    record = &StoreGuestResourceLocked(std::move(created_record), true, true);
    created = true;
  } else if (!IsImageResourceKind(GuestObjectKind(it->second))) {
    return;
  } else {
    record = &it->second;
  }

  const TextureObserverStateFingerprint before =
      TextureObserverFingerprint(*record);
  if (!FablePixVideoApplyInlineUploadToResource(*record, upload)) {
    return;
  }
  FableDirectGuestResourceRegistry().last_resource = *record;
  RegisterTextureIdentityForRecordLocked(*record);
  MarkResourceChangedLocked(*record);
  RefreshQueuedDrawTextureResourceSerialLocked(*record);
  UpdateDirectSnapshotCacheLocked(*record);

  if (created || TextureResourceObserverStateChanged(before, *record)) {
    PropagateTextureAliasUploadLocked(*record, false);
  }
}
