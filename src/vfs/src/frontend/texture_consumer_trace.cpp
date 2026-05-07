#include "vfs_hooks_private.h"

namespace refii::vfs {
inline namespace detail {

bool FrontendTextureConsumerTraceActive() {
  return g_active_frontend_texture_consumer_trace.enabled;
}

constexpr uint32_t kFrontendTextureDescriptorBundleVTable = 0x82006CC0;
constexpr bool kCollapseFrontendTextureConsumerTopLevel = true;

bool LooksLikeFrontendTextureConsumerObject(uint8_t* base,
                                            uint32_t candidate_guest) {
  if (base == nullptr || !LooksLikeGuestPointer(candidate_guest) ||
      LoadGuestU32(base, candidate_guest) != kFrontendTextureConsumerObjectVTable) {
    return false;
  }

  const uint32_t vtable_guest = LoadGuestU32(base, candidate_guest);
  if (LoadGuestU32(base, vtable_guest + 20) != 0x82378618u) {
    return false;
  }

  const uint32_t bundle_guest = LoadGuestU32(base, candidate_guest + 8);
  return !LooksLikeGuestPointer(bundle_guest) ||
         LoadGuestU32(base, bundle_guest) == kFrontendTextureDescriptorBundleVTable;
}

uint32_t TryFindFrontendTextureConsumerFromCandidate(uint8_t* base,
                                                    uint32_t candidate_guest) {
  if (!LooksLikeGuestPointer(candidate_guest)) {
    return 0;
  }
  if (LooksLikeFrontendTextureConsumerObject(base, candidate_guest)) {
    return candidate_guest;
  }

  for (uint32_t offset = 0; offset < 96; offset += 4) {
    const uint32_t nested_guest = LoadGuestU32(base, candidate_guest + offset);
    if (LooksLikeFrontendTextureConsumerObject(base, nested_guest)) {
      return nested_guest;
    }
    if (!LooksLikeGuestPointer(nested_guest)) {
      continue;
    }
    for (uint32_t nested_offset = 0; nested_offset < 48; nested_offset += 4) {
      const uint32_t deeper_guest =
          LoadGuestU32(base, nested_guest + nested_offset);
      if (LooksLikeFrontendTextureConsumerObject(base, deeper_guest)) {
        return deeper_guest;
      }
    }
  }
  return 0;
}

bool AddFrontendConsumerGraphCandidate(std::array<uint32_t, 256>& queue,
                                       uint32_t* queue_count,
                                       std::array<uint32_t, 256>& seen,
                                       uint32_t* seen_count,
                                       uint32_t candidate_guest) {
  if (!LooksLikeGuestPointer(candidate_guest)) {
    return false;
  }
  for (uint32_t index = 0; index < *seen_count; ++index) {
    if (seen[index] == candidate_guest) {
      return false;
    }
  }
  if (*seen_count >= seen.size() || *queue_count >= queue.size()) {
    return false;
  }
  seen[(*seen_count)++] = candidate_guest;
  queue[(*queue_count)++] = candidate_guest;
  return true;
}

uint32_t TryFindFrontendTextureConsumerFromGraph(
    uint8_t* base,
    std::initializer_list<uint32_t> roots,
    std::string_view source_prefix,
    std::string* source_out) {
  if (base == nullptr) {
    return 0;
  }

  std::array<uint32_t, 256> queue{};
  std::array<uint32_t, 256> seen{};
  std::array<uint8_t, 256> depths{};
  uint32_t queue_count = 0;
  uint32_t seen_count = 0;
  for (const uint32_t root_guest : roots) {
    const uint32_t before_count = queue_count;
    AddFrontendConsumerGraphCandidate(queue,
                                      &queue_count,
                                      seen,
                                      &seen_count,
                                      root_guest);
    if (queue_count != before_count) {
      depths[queue_count - 1] = 0;
    }
  }

  for (uint32_t read_index = 0; read_index < queue_count; ++read_index) {
    const uint32_t candidate_guest = queue[read_index];
    const uint8_t depth = depths[read_index];
    if (LooksLikeFrontendTextureConsumerObject(base, candidate_guest)) {
      if (source_out != nullptr) {
        *source_out = fmt::format("{}_graph_depth{}", source_prefix, depth);
      }
      return candidate_guest;
    }

    if (depth >= 4) {
      continue;
    }

    for (uint32_t offset = 0; offset < 0x180; offset += 4) {
      const uint32_t nested_guest = LoadGuestU32(base, candidate_guest + offset);
      const uint32_t before_count = queue_count;
      if (AddFrontendConsumerGraphCandidate(queue,
                                            &queue_count,
                                            seen,
                                            &seen_count,
                                            nested_guest)) {
        depths[before_count] = static_cast<uint8_t>(depth + 1);
      }
      if (queue_count >= queue.size()) {
        break;
      }
    }
  }
  return 0;
}

uint32_t ScanLikelyFrontendTextureConsumerHeapRange(uint8_t* base,
                                                    uint32_t begin_guest,
                                                    uint32_t end_guest) {
  if (base == nullptr || begin_guest >= end_guest) {
    return 0;
  }
  for (uint32_t candidate_guest = begin_guest; candidate_guest < end_guest;
       candidate_guest += 4) {
    if (LoadGuestU32(base, candidate_guest) == kFrontendTextureConsumerObjectVTable &&
        LooksLikeFrontendTextureConsumerObject(base, candidate_guest)) {
      return candidate_guest;
    }
  }
  return 0;
}

uint32_t FindPostVideoFrontendTextureConsumerOwner(uint8_t* base,
                                                   uint32_t root_guest,
                                                   std::string* source_out) {
  auto found = [&](uint32_t owner_guest, std::string_view source) {
    if (source_out != nullptr) {
      *source_out = std::string(source);
    }
    return owner_guest;
  };

  if (uint32_t owner_guest =
          TryFindFrontendTextureConsumerFromCandidate(base, root_guest);
      owner_guest != 0) {
    return found(owner_guest, "root");
  }
  const uint32_t global_owner_guest = FindPostVideoOwnerFromGlobalRoot(base);
  if (uint32_t owner_guest =
          TryFindFrontendTextureConsumerFromCandidate(base, global_owner_guest);
      owner_guest != 0) {
    return found(owner_guest, "post_video_global_owner");
  }
  const uint32_t update_guest = LastRefiiStartupUpdateGuest();
  if (uint32_t owner_guest =
          TryFindFrontendTextureConsumerFromCandidate(base, update_guest);
      owner_guest != 0) {
    return found(owner_guest, "startup_update");
  }
  if (uint32_t owner_guest = TryFindFrontendTextureConsumerFromGraph(
          base,
          {root_guest, global_owner_guest, update_guest},
          "handoff",
          source_out);
      owner_guest != 0) {
    return owner_guest;
  }

  // The frontend consumer is normally allocated in this heap band shortly after
  // the startup-video completion gate. This scan is one-shot/throttled by the caller.
  if (uint32_t owner_guest =
          ScanLikelyFrontendTextureConsumerHeapRange(base, 0x42100000u, 0x42280000u);
      owner_guest != 0) {
    return found(owner_guest, "heap_421");
  }
  if (uint32_t owner_guest =
          ScanLikelyFrontendTextureConsumerHeapRange(base, 0x40100000u, 0x40200000u);
      owner_guest != 0) {
    return found(owner_guest, "heap_401");
  }

  if (source_out != nullptr) {
    *source_out = "missing";
  }
  return 0;
}

uint32_t TryGetCollapsibleRefiiFrontendTextureBundle(uint8_t* base,
                                                     const PPCContext& ctx) {
  if (!kCollapseFrontendTextureConsumerTopLevel) {
    return 0;
  }
  if (base == nullptr || !RefiiLooseAssetBackendEnabled() ||
      !RefiiMainMenuTraceNeedsFrontendHandoff() ||
      (!RefiiStartupVideosSettledForMenuHandoff() &&
       !SkipStartupVideosEnabled())) {
    return 0;
  }
  if (static_cast<uint32_t>(ctx.lr) != 0x822F5174) {
    return 0;
  }
  const uint32_t owner_guest = ctx.r3.u32;
  if (!LooksLikeGuestPointer(owner_guest) ||
      LoadGuestU32(base, owner_guest) != kFrontendTextureConsumerObjectVTable) {
    return 0;
  }
  const uint32_t bundle_guest = LoadGuestU32(base, owner_guest + 8);
  if (!LooksLikeGuestPointer(bundle_guest) ||
      LoadGuestU32(base, bundle_guest) !=
          kFrontendTextureDescriptorBundleVTable) {
    return 0;
  }
  return bundle_guest;
}

bool FrontendTextureConsumerVerboseTraceActive() {
  return g_active_frontend_texture_consumer_trace.enabled &&
         g_active_frontend_texture_consumer_trace.verbose;
}

void AppendFrontendTextureConsumerTraceSummary(std::string& target,
                                               std::string_view item) {
  if (item.empty()) {
    return;
  }
  constexpr size_t kMaxSummaryBytes = 1400;
  if (target.size() >= kMaxSummaryBytes) {
    return;
  }
  if (!target.empty()) {
    target.append(" | ");
  }
  const size_t remaining = kMaxSummaryBytes - target.size();
  if (item.size() <= remaining) {
    target.append(item);
    return;
  }
  if (remaining > 4) {
    target.append(item.substr(0, remaining - 4));
    target.append("...");
  }
}

std::string_view LabelFrontendTextureConsumerGeneratedCallSite(uint32_t lr) {
  switch (lr) {
    case 0x823787D4:
      return "fixed_descriptor_slot_260_ctor";
    case 0x823787E4:
      return "fixed_descriptor_slot_260_resolver";
    case 0x82378820:
      return "fixed_descriptor_slot_260_pair_store";
    case 0x8237882C:
      return "fixed_descriptor_slot_260_slot_store";
    case 0x82378884:
      return "fixed_descriptor_slot_268_ctor";
    case 0x82378894:
      return "fixed_descriptor_slot_268_resolver";
    case 0x823788D0:
      return "fixed_descriptor_slot_268_pair_store";
    case 0x823788DC:
      return "fixed_descriptor_slot_268_slot_store";
    case 0x8237899C:
      return "static_table_loop_descriptor_resolver";
    case 0x823789D8:
      return "static_table_loop_pair_store";
    case 0x82378DA8:
      return "final_direct_slot_280_resolver";
    case 0x826D6744:
      return "followon_frame_descriptor_ctor";
    case 0x826D6754:
      return "followon_frame_descriptor_resolver";
    case 0x826D6790:
      return "followon_frame_pair_store";
    case 0x826D5E94:
      return "bundle_primary_handle_104_resolver";
    case 0x826D5F94:
      return "bundle_secondary_handle_96_resolver";
    case 0x826D6074:
      return "bundle_tertiary_handle_100_resolver";
    case 0x826D60E8:
      return "bundle_final_list_handle_resolver";
    default:
      return "unknown_frontend_texture_site";
  }
}

std::string_view LabelFrontendConstantTableResolverShape(uint32_t descriptor_vtable,
                                                         uint32_t slot16,
                                                         uint32_t slot12) {
  if (slot16 != 0x82EA63A8) {
    return {};
  }
  if (descriptor_vtable == 0x820016EC && slot12 == 0x822CA820) {
    return "simple_constant_table_serializer";
  }
  if (descriptor_vtable == 0x820F470C && slot12 == 0x82A43B08) {
    return "frontend_quad_constant_table_serializer";
  }
  if (descriptor_vtable == 0x82001674 && slot12 == 0x8217D3F8) {
    return "frontend_final_list_constant_table_serializer";
  }
  if (descriptor_vtable == 0x820F4540 && slot12 == 0x82A41748) {
    return "frontend_texture_resource_constant_table_serializer";
  }
  return {};
}

std::string LabelFrontendTextureConsumerStoreDestination(uint32_t owner_guest,
                                                         uint32_t destination) {
  if (owner_guest == 0 || destination == 0) {
    return {};
  }
  if (destination == owner_guest + 248) {
    return "object+248";
  }
  if (destination == owner_guest + 260) {
    return "object+260";
  }
  if (destination == owner_guest + 268) {
    return "object+268";
  }
  if (destination == owner_guest + 280) {
    return "object+280";
  }
  return {};
}

std::string FormatFrontendTextureConsumerOwnerSlots(uint8_t* base,
                                                    uint32_t owner_guest) {
  if (base == nullptr || owner_guest == 0) {
    return {};
  }
  return fmt::format("object+248=[{}] object+260=[{}] object+268=[{}] "
                     "object+276=0x{:02X} object+280=[{}]",
                     FormatGuestWordsAtOffsetIfPointer(base,
                                                       owner_guest,
                                                       248,
                                                       12),
                     FormatGuestWordsAtOffsetIfPointer(base,
                                                       owner_guest,
                                                       260,
                                                       8),
                     FormatGuestWordsAtOffsetIfPointer(base,
                                                       owner_guest,
                                                       268,
                                                       8),
                     LoadGuestU8(base, owner_guest + 276),
                     FormatGuestWordsAtOffsetIfPointer(base,
                                                       owner_guest,
                                                       280,
                                                       4));
}

void BeginFrontendTextureConsumerTrace(uint32_t owner_guest,
                                       uint32_t lr,
                                       bool verbose) {
  auto& trace = g_active_frontend_texture_consumer_trace;
  trace = ActiveFrontendTextureConsumerTraceContext{};
  trace.enabled = true;
  trace.verbose = verbose;
  trace.owner_guest = owner_guest;
  trace.root_lr = lr;
}

ActiveFrontendTextureConsumerTraceContext
EndFrontendTextureConsumerTrace() {
  ActiveFrontendTextureConsumerTraceContext trace =
      g_active_frontend_texture_consumer_trace;
  g_active_frontend_texture_consumer_trace =
      ActiveFrontendTextureConsumerTraceContext{};
  return trace;
}

void RecordFrontendTextureConsumerDescriptor(uint32_t descriptor_guest,
                                             uint32_t descriptor_lr,
                                             uint32_t resource_hash,
                                             uint32_t descriptor_vtable,
                                             uint32_t descriptor_slot12,
                                             uint32_t descriptor_slot16,
                                             std::string_view name,
                                             std::string_view candidate,
                                             std::string_view source,
                                             bool refii_owned) {
  auto& trace = g_active_frontend_texture_consumer_trace;
  if (!trace.enabled) {
    return;
  }
  ++trace.descriptor_calls;
  trace.last_descriptor_lr = descriptor_lr;
  trace.last_descriptor_guest = descriptor_guest;
  trace.last_descriptor_hash = resource_hash;
  trace.last_descriptor_vtable = descriptor_vtable;
  trace.last_descriptor_slot12 = descriptor_slot12;
  trace.last_descriptor_slot16 = descriptor_slot16;
  trace.any_refii_owned = trace.any_refii_owned || refii_owned;
  AppendFrontendTextureConsumerTraceSummary(
      trace.descriptor_summary,
      fmt::format("#{} site={} lr=0x{:08X} hash=0x{:08X} out=0x{:08X} "
                  "vtable=0x{:08X} slot12=0x{:08X} slot16=0x{:08X} "
                  "owned={} name={} candidate={} source={}",
                  trace.descriptor_calls,
                  LabelFrontendTextureConsumerGeneratedCallSite(descriptor_lr),
                  descriptor_lr,
                  resource_hash,
                  descriptor_guest,
                  descriptor_vtable,
                  descriptor_slot12,
                  descriptor_slot16,
                  refii_owned ? "true" : "false",
                  NormalizeLooseAssetKey(name),
                  candidate,
                  source));
}

void RecordFrontendTextureConsumerResolver(uint8_t* base,
                                           uint32_t lr,
                                           uint32_t descriptor_guest,
                                           uint32_t resource_hash,
                                           uint32_t descriptor_vtable,
                                           uint32_t descriptor_slot12,
                                           uint32_t descriptor_slot16,
                                           uint32_t table_index,
                                           uint32_t table_object,
                                           std::string_view table_before,
                                           std::string_view table_after,
                                           uint32_t result_guest,
                                           bool refii_owned) {
  if (refii_owned) {
    RecordRefiiLoadingSequenceResolverHandle(resource_hash,
                                             result_guest,
                                             lr,
                                             table_index);
  }

  auto& trace = g_active_frontend_texture_consumer_trace;
  if (!trace.enabled) {
    return;
  }
  ++trace.resolver_calls;
  trace.last_resolved_object = result_guest;
  trace.last_resolved_hash = resource_hash;
  trace.last_descriptor_vtable = descriptor_vtable;
  trace.last_descriptor_slot12 = descriptor_slot12;
  trace.last_descriptor_slot16 = descriptor_slot16;
  trace.last_resolver_table_index = table_index;
  trace.last_resolver_table_object = table_object;
  trace.any_refii_owned = trace.any_refii_owned || refii_owned;
  const bool resolver_empty = result_guest == 0xFFFFFFFF;
  if (refii_owned && resolver_empty) {
    trace.resolver_empty_refii_owned = true;
    if (result_guest == 0xFFFFFFFF) {
      trace.descriptor_slot16_rejected = true;
    }
  }
  AppendFrontendTextureConsumerTraceSummary(
      trace.resolver_summary,
      fmt::format("#{} site={} lr=0x{:08X} hash=0x{:08X} desc=0x{:08X} "
                  "slot16=0x{:08X} slot16_inferred={} slot12=0x{:08X} "
                  "table_index={} table_object=0x{:08X} result=0x{:08X} "
                  "owned={} result_words=[{}] table_before=[{}] "
                  "table_after=[{}]",
                  trace.resolver_calls,
                  LabelFrontendTextureConsumerGeneratedCallSite(lr),
                  lr,
                  resource_hash,
                  descriptor_guest,
                  descriptor_slot16,
                  result_guest == 0xFFFFFFFF ? "rejected_or_empty" : "accepted",
                  descriptor_slot12,
                  table_index,
                  table_object,
                  result_guest,
                  refii_owned ? "true" : "false",
                  FormatGuestWordsIfPointer(base, result_guest, 8),
                  table_before,
                  table_after));
}

void RecordFrontendTextureConsumerStore(uint8_t* base,
                                        std::string_view api,
                                        uint32_t lr,
                                        uint32_t destination_guest,
                                        uint32_t source_guest,
                                        uint32_t value_guest) {
  auto& trace = g_active_frontend_texture_consumer_trace;
  if (!trace.enabled) {
    return;
  }
  if (api == "sub_82379D78") {
    ++trace.store_pair_calls;
  } else {
    ++trace.slot_store_calls;
  }
  if (destination_guest == trace.owner_guest + 280 && value_guest != 0 &&
      value_guest != 0xFFFFFFFFu) {
    trace.final_direct_slot_valid = true;
  }
  trace.last_store_destination = destination_guest;
  trace.last_store_value = value_guest;
  const std::string destination_label =
      LabelFrontendTextureConsumerStoreDestination(trace.owner_guest,
                                                   destination_guest);
  AppendFrontendTextureConsumerTraceSummary(
      trace.store_summary,
      fmt::format("{} site={} lr=0x{:08X} dest=0x{:08X}{} source=0x{:08X} "
                  "value=0x{:08X} dest_words=[{}]",
                  api,
                  LabelFrontendTextureConsumerGeneratedCallSite(lr),
                  lr,
                  destination_guest,
                  destination_label.empty()
                      ? std::string{}
                      : fmt::format("({})", destination_label),
                  source_guest,
                  value_guest,
                  FormatGuestWordsIfPointer(base, destination_guest, 6)));

  const bool useful_store =
      trace.any_refii_owned &&
      value_guest != 0 &&
      value_guest != 0xFFFFFFFFu;
  if (!useful_store) {
    return;
  }

  const std::string site =
      std::string(LabelFrontendTextureConsumerGeneratedCallSite(lr));
  const std::string target =
      destination_label.empty()
          ? fmt::format("dest_0x{:08X}", destination_guest)
          : destination_label;
  const std::string next_boundary =
      (trace.resource_manager_seen || trace.request_manager_seen)
          ? "ResourceManager_or_RequestManager_seen"
          : "ResourceManager_Open_or_RequestManager_hash_lookup";
  RecordRefiiMainMenuBlockerEvent(
      "frontend_texture_store",
      fmt::format("api={} site={} lr=0x{:08X} hash=0x{:08X} "
                  "resolver_result=0x{:08X} target={} dest=0x{:08X} "
                  "source=0x{:08X} value=0x{:08X} next={} owner_slots=[{}]",
                  api,
                  site,
                  lr,
                  trace.last_resolved_hash,
                  trace.last_resolved_object,
                  target,
                  destination_guest,
                  source_guest,
                  value_guest,
                  next_boundary,
                  FormatFrontendTextureConsumerOwnerSlots(base,
                                                          trace.owner_guest)));
  LogRefiiVfsChainVerdict("frontend_texture_consumer",
                          lr,
                          "frontend_texture_store",
                          site,
                          target,
                          "Fable2_frontend_store",
                          api,
                          "TraceOnce",
                          next_boundary);
}

void RecordFrontendTextureConsumerOwnerSlotSnapshot(uint8_t* base,
                                                    uint32_t owner_guest) {
  auto& trace = g_active_frontend_texture_consumer_trace;
  if (!trace.enabled) {
    return;
  }
  trace.owner_slot_summary =
      FormatFrontendTextureConsumerOwnerSlots(base, owner_guest);
  const uint32_t direct_slot_280 =
      owner_guest == 0 ? 0 : LoadGuestU32(base, owner_guest + 280);
  if (direct_slot_280 != 0 && direct_slot_280 != 0xFFFFFFFFu) {
    trace.final_direct_slot_valid = true;
    trace.last_store_destination = owner_guest + 280;
    trace.last_store_value = direct_slot_280;
  }
}

void RecordFrontendTextureConsumerStaticTableListCommit() {
  auto& trace = g_active_frontend_texture_consumer_trace;
  if (!trace.enabled) {
    return;
  }
  trace.static_table_list_committed = true;
}

void RecordFrontendTextureConsumerFollowonFrameListCommit() {
  auto& trace = g_active_frontend_texture_consumer_trace;
  if (!trace.enabled) {
    return;
  }
  trace.followon_frame_list_committed = true;
}

void RecordFrontendTextureConsumerResourceBoundary(std::string_view api,
                                                   uint32_t resource_hash,
                                                   uint32_t result_guest) {
  auto& trace = g_active_frontend_texture_consumer_trace;
  if (!trace.enabled) {
    return;
  }
  if (api.find("RequestManager") != std::string_view::npos) {
    trace.request_manager_seen = true;
  }
  if (api.find("ResourceManager") != std::string_view::npos) {
    trace.resource_manager_seen = true;
  }
  if (api.find("TextureParser") != std::string_view::npos ||
      api.find("C340") != std::string_view::npos) {
    trace.texture_parser_seen = true;
  }
  AppendFrontendTextureConsumerTraceSummary(
      trace.resolver_summary,
      fmt::format("{} hash=0x{:08X} result=0x{:08X}",
                  api,
                  resource_hash,
                  result_guest));
}


}  // namespace detail
}  // namespace refii::vfs
