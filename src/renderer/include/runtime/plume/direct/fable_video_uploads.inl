// Included inside hook_observer.cpp's anonymous namespace.

void CapturePixDecodedVideoFrameUploadLocked(const RendererHookArgs& args,
                                             uint32_t result, uint8_t* base) {
  if (!base || result != 0 || args.r8 == 0 ||
      !IsLikelyGuestDwordAddress(args.r8)) {
    return;
  }

  // PixDecodeVideoFrame writes a Pix metadata block, not a texture fetch:
  // dwords 0..5 are marker, flag, width, height, frame base, and alpha.
  // Match the decoded frame base against fetch constants captured from the
  // later PixRender path, then refresh the existing texture upload.
  const uint32_t frame_width = LoadGuestU32(base, args.r8 + 8u);
  const uint32_t frame_height = LoadGuestU32(base, args.r8 + 12u);
  const uint32_t frame_base = LoadGuestU32(base, args.r8 + 16u);
  if (frame_width == 0 || frame_height == 0 || frame_width > 4096u ||
      frame_height > 4096u || frame_base == 0) {
    return;
  }

  if ((RendererDrawTracingEnabled() || RendererResourceTracingEnabled()) &&
      g_observer.logged_pix_decode_packet_shapes < 12u) {
    const bool chunks_readable = IsLikelyGuestDwordAddress(args.r5);
    const uint32_t first_chunk_pointer =
        chunks_readable ? LoadGuestU32(base, args.r5) : 0;
    const uint32_t chunk_virtual = first_chunk_pointer + args.r7;
    const uint32_t chunk =
        chunks_readable
            ? ((((chunk_virtual >> 20u) + 512u) & 0x1000u) +
               (chunk_virtual & 0x1FFFFFFFu) - 0x40000000u)
            : 0;
    const bool chunk_readable = IsLikelyGuestDwordAddress(chunk + 64u);
    const uint32_t chunk_q0_hi = chunk_readable ? LoadGuestU32(base, chunk) : 0;
    const uint32_t chunk_q0_lo =
        chunk_readable ? LoadGuestU32(base, chunk + 4u) : 0;
    const uint32_t chunk_flags =
        chunk_readable ? LoadGuestU32(base, chunk + 12u) : 0;
    const uint32_t chunk_width =
        chunk_readable ? LoadGuestU32(base, chunk + 16u) : 0;
    const uint32_t chunk_height =
        chunk_readable ? LoadGuestU32(base, chunk + 20u) : 0;
    const uint32_t chunk_scale_u =
        chunk_readable ? LoadGuestU32(base, chunk + 24u) : 0;
    const uint32_t chunk_scale_v =
        chunk_readable ? LoadGuestU32(base, chunk + 28u) : 0;
    const uint32_t chunk_alpha =
        chunk_readable ? LoadGuestU32(base, chunk + 32u) : 0;
    std::array<uint32_t, 8> sample = {};
    if (chunk_readable) {
      for (uint32_t i = 0; i < sample.size(); ++i) {
        sample[i] = LoadGuestU32(base, chunk + 36u + i * sizeof(uint32_t));
      }
    }
    REXLOG_INFO(
        "Refii renderer PixDecode packet #{} result=0x{:08X} "
        "chunks=0x{:08X}/{} first=0x{:08X} offset=0x{:08X} "
        "chunk=0x{:08X} readable={} q0={:08X}/{:08X} flags=0x{:08X} "
        "chunk_size={}x{} scale={:08X}/{:08X} alpha={:08X} "
        "frame=0x{:08X} {}x{} out=0x{:08X}/{} "
        "sample={:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}",
        g_observer.logged_pix_decode_packet_shapes + 1u, result, args.r5,
        chunks_readable ? 1 : 0, first_chunk_pointer, args.r7, chunk,
        chunk_readable ? 1 : 0,
        chunk_q0_hi, chunk_q0_lo, chunk_flags, chunk_width, chunk_height,
        chunk_scale_u, chunk_scale_v, chunk_alpha, frame_base, frame_width,
        frame_height, args.r8, args.r9, sample[0], sample[1], sample[2],
        sample[3], sample[4], sample[5], sample[6], sample[7]);
    ++g_observer.logged_pix_decode_packet_shapes;
  }

  ++g_observer.pix_decode_frames;
  g_observer.last_pix_decode_base = frame_base;
  g_observer.last_pix_decode_width = frame_width;
  g_observer.last_pix_decode_height = frame_height;
  g_observer.pending_pix_decoded_video_frames[frame_base] = {
      .width = frame_width,
      .height = frame_height,
      .frame_base = frame_base,
      .serial = ++g_observer.serial,
  };
  while (g_observer.pending_pix_decoded_video_frames.size() >
         kMaxPendingPixDecodedVideoFrames) {
    auto oldest = g_observer.pending_pix_decoded_video_frames.begin();
    for (auto it = std::next(oldest);
         it != g_observer.pending_pix_decoded_video_frames.end(); ++it) {
      if (it->second.serial < oldest->second.serial) {
        oldest = it;
      }
    }
    g_observer.pending_pix_decoded_video_frames.erase(oldest);
  }

  for (auto& [address, record] : FableDirectGuestResourceRegistry().resources) {
    (void)address;
    if (GuestObjectKind(record) != RendererResourceKind::kTexture) {
      continue;
    }

    const std::array<uint32_t, 6> dwords = TextureFetchDwordsForRecord(record);
    RefreshTextureUploadFromPendingPixDecodedFrameLocked(record, dwords, base);
  }
}

bool RefreshBinkVideoPlaneUploadLocked(RendererResourceRecord& record,
                                       const BinkVideoPlaneBinding& binding,
                                       uint8_t* base,
                                       std::string_view source_symbol) {
  if (!base || binding.surface == 0 || binding.upload_size == 0 ||
      !IsImageResourceKind(GuestObjectKind(record))) {
    return false;
  }

  const uint32_t preferred_source =
      binding.lock_address != 0 ? binding.lock_address : binding.physical_address;
  uint32_t upload_address =
      SelectReadableUploadAddress(base, preferred_source, binding.upload_size);
  if (upload_address == 0 && binding.physical_address != preferred_source) {
    upload_address =
        SelectReadableUploadAddress(base, binding.physical_address,
                                    binding.upload_size);
  }
  if (upload_address == 0) {
    return false;
  }

  const uint64_t previous_generation = record.upload_generation;
  CopyGuestBufferUploadData(record, base, upload_address, binding.upload_size);
  if (record.upload_generation == previous_generation) {
    return false;
  }

  GuestTextureRecord texture = GuestTextureRecordFor(record);
  texture.upload_generation = record.upload_generation;
  ApplyGuestTextureRecord(record, texture);
  record.serial = ++g_observer.serial;
  FableDirectGuestResourceRegistry().last_resource = record;
  MarkResourceChangedLocked(record);
  RefreshQueuedDrawTextureResourceSerialLocked(record);
  const std::array<uint32_t, 6> dwords = TextureFetchDwordsForRecord(record);
  const uint32_t texture_base = TextureBaseFromFetchDwords(dwords);
  const uint32_t record_address = GuestObjectAddress(record);
  if (texture_base != 0 && texture_base != record_address) {
    RemoveDuplicateTextureBaseResourceLocked(record_address, texture_base,
                                            dwords);
  }
  ++g_observer.bink_video_upload_refreshes;

  if ((RendererDrawTracingEnabled() || RendererResourceTracingEnabled()) &&
      g_observer.logged_bink_video_uploads < 24u) {
    const uint32_t source_nonzero = FindFirstNonZeroGuestByte(
        base, upload_address, binding.upload_size, binding.upload_size);
    const uint32_t word0 =
        source_nonzero != UINT32_MAX ? LoadGuestU32(base, upload_address) : 0u;
    REXLOG_INFO(
        "Refii renderer Bink video upload #{} symbol={} surface=0x{:08X} "
        "source=0x{:08X} lock=0x{:08X} physical=0x{:08X} size={} "
        "first_nz={} word0={:08X} generation={}",
        g_observer.logged_bink_video_uploads + 1u, source_symbol,
        binding.surface, upload_address, binding.lock_address,
        binding.physical_address, binding.upload_size,
        source_nonzero == UINT32_MAX ? -1
                                     : static_cast<int32_t>(source_nonzero),
        word0, record.upload_generation);
    ++g_observer.logged_bink_video_uploads;
  }

  return true;
}

void RefreshBinkVideoPlaneUploadsLocked(std::string_view source_symbol,
                                        uint8_t* base) {
  if (!base || g_observer.bink_planes_by_surface.empty()) {
    return;
  }

  for (auto& [surface, binding] : g_observer.bink_planes_by_surface) {
    (void)surface;
    auto record_it = FableDirectGuestResourceRegistry().resources.find(binding.surface);
    if (record_it == FableDirectGuestResourceRegistry().resources.end()) {
      continue;
    }
    RefreshBinkVideoPlaneUploadLocked(record_it->second, binding, base,
                                      source_symbol);
  }
}

void ObserveBinkVideoPlaneSetupLocked(const RendererHookArgs& args,
                                      uint8_t* base) {
  if (!base || args.r6 == 0 || args.r7 == 0 ||
      !IsLikelyGuestDwordAddress(args.r6) ||
      !IsLikelyGuestDwordAddress(args.r7)) {
    return;
  }

  const uint32_t surface = LoadGuestU32(base, args.r7);
  if (!IsLikelyFableObjectAddress(surface)) {
    return;
  }

  RendererResourceRecord record = CreateGuestTextureResource(surface, args.r3);
  SnapshotGuestResource(record, base);
  GuestTextureRecord texture = GuestTextureRecordFor(record);
  texture.normalized_fetch_dwords =
      NormalizeTextureFetchDwords(TextureFetchDwordsForRecord(record));
  if (!HasTextureFetchDwords(texture.normalized_fetch_dwords)) {
    return;
  }
  ApplyGuestTextureRecord(record, texture);

  const uint32_t upload_size =
      std::max(TextureSnapshotSizeForRecord(record),
               TextureSnapshotSizeForRelatedRecordLocked(record));
  if (upload_size == 0) {
    return;
  }

  texture = GuestTextureRecordFor(record);
  texture.byte_size = std::max(texture.byte_size, upload_size);
  ApplyGuestTextureRecord(record, texture);
  record.serial = ++g_observer.serial;
  const bool inserted =
      FableDirectGuestResourceRegistry().resources.find(GuestObjectAddress(record)) ==
      FableDirectGuestResourceRegistry().resources.end();
  RendererResourceRecord& stored_record =
      StoreGuestResourceLocked(std::move(record), inserted, inserted);

  BinkVideoPlaneBinding binding = {
      .surface = surface,
      .plane_info = args.r6,
      .plane_output_slot = args.r7,
      .physical_address = LoadGuestU32(base, args.r6 + 4u),
      .lock_address = LoadGuestU32(base, args.r6 + 8u),
      .upload_size = upload_size,
      .serial = ++g_observer.serial,
  };
  g_observer.bink_planes_by_surface[surface] = binding;
  ++g_observer.bink_video_plane_setups;

  if ((RendererDrawTracingEnabled() || RendererResourceTracingEnabled()) &&
      g_observer.logged_bink_video_planes < 16u) {
    const uint32_t selected =
        SelectReadableUploadAddress(base,
                                    binding.lock_address != 0
                                        ? binding.lock_address
                                        : binding.physical_address,
                                    binding.upload_size);
    const uint32_t nonzero =
        selected != 0
            ? FindFirstNonZeroGuestByte(base, selected, binding.upload_size,
                                        binding.upload_size)
            : UINT32_MAX;
    const std::array<uint32_t, 6> dwords =
        TextureFetchDwordsForRecord(stored_record);
    REXLOG_INFO(
        "Refii renderer Bink video plane #{} movie=0x{:08X} "
        "plane=0x{:08X} output_slot=0x{:08X} surface=0x{:08X} "
        "physical=0x{:08X} lock=0x{:08X} selected=0x{:08X} "
        "size={} nz={} dims={}x{} dwords={:08X}/{:08X}/{:08X}/"
        "{:08X}/{:08X}/{:08X}",
        g_observer.logged_bink_video_planes + 1u, args.r3, args.r6, args.r7,
        surface, binding.physical_address, binding.lock_address, selected,
        binding.upload_size,
        nonzero == UINT32_MAX ? -1 : static_cast<int32_t>(nonzero), args.r4,
        args.r5, dwords[0], dwords[1], dwords[2], dwords[3], dwords[4],
        dwords[5]);
    ++g_observer.logged_bink_video_planes;
  }

  RefreshBinkVideoPlaneUploadLocked(stored_record, binding, base,
                                    "sub_82A42FA0");
}

bool RestoreBinkVideoPlaneResourceLocked(uint32_t surface, uint8_t* base,
                                         std::string_view source_symbol) {
  if (!base || !IsLikelyFableObjectAddress(surface)) {
    return false;
  }

  const auto binding_it = g_observer.bink_planes_by_surface.find(surface);
  if (binding_it == g_observer.bink_planes_by_surface.end()) {
    return false;
  }

  RendererResourceRecord record = CreateGuestTextureResource(surface, surface);
  SnapshotGuestResource(record, base);
  GuestTextureRecord texture = GuestTextureRecordFor(record);
  texture.normalized_fetch_dwords =
      NormalizeTextureFetchDwords(TextureFetchDwordsForRecord(record));
  if (!HasTextureFetchDwords(texture.normalized_fetch_dwords)) {
    return false;
  }

  const uint32_t upload_size =
      std::max(TextureSnapshotSizeForRecord(record),
               TextureSnapshotSizeForRelatedRecordLocked(record));
  if (upload_size == 0) {
    return false;
  }

  texture.byte_size = std::max(texture.byte_size, upload_size);
  ApplyGuestTextureRecord(record, texture);
  if (!RefreshBinkVideoPlaneUploadLocked(record, binding_it->second, base,
                                         source_symbol) ||
      record.upload_data.empty()) {
    return false;
  }

  record.serial = ++g_observer.serial;
  RendererResourceRecord& stored_record =
      StoreGuestResourceLocked(std::move(record), true, true);
  RefreshQueuedDrawTextureResourceSerialLocked(stored_record);
  PropagateTextureAliasUploadLocked(stored_record, false);
  MarkResourceChangedLocked(stored_record);

  if ((RendererDrawTracingEnabled() || RendererResourceTracingEnabled()) &&
      g_observer.logged_bink_video_uploads < 32u) {
    const std::array<uint32_t, 6> dwords =
        TextureFetchDwordsForRecord(stored_record);
    REXLOG_INFO(
        "Refii renderer restored Bink video plane resource #{} "
        "surface=0x{:08X} upload={} serial={} source={} "
        "dwords={:08X}/{:08X}/{:08X}/{:08X}/{:08X}/{:08X}",
        g_observer.logged_bink_video_uploads + 1u, surface,
        stored_record.upload_data.size(),
        static_cast<unsigned long long>(stored_record.serial), source_symbol,
        dwords[0], dwords[1], dwords[2], dwords[3], dwords[4], dwords[5]);
    ++g_observer.logged_bink_video_uploads;
  }
  return true;
}
