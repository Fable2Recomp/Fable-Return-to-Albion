#include "vfs_hooks_private.h"

namespace {

bool ShouldUseNativeConcreteBankContainer(std::string_view bank_name,
                                          std::string* out_candidate) {
  const std::string normalized = refii::vfs::NormalizeLooseAssetKey(bank_name);
  if (normalized.empty() || !refii::vfs::EndsWith(normalized, ".bnk")) {
    return false;
  }

  if (refii::vfs::ResolveExactTypedBankManifestLooseAsset(
          normalized, nullptr, nullptr) ||
      refii::vfs::ResolveSyntheticBankManifestLooseAsset(
          normalized, nullptr, nullptr) ||
      refii::vfs::ShouldDeferLooseBankContainer(normalized)) {
    return false;
  }

  refii::vfs::LooseAssetResolution resolution;
  std::string candidate;
  if (!refii::vfs::ResolveOriginalGameDataAsset(
          normalized, &resolution, &candidate)) {
    return false;
  }

  if (out_candidate != nullptr) {
    *out_candidate = candidate.empty() ? resolution.provider_relative_path
                                       : candidate;
  }
  return true;
}

}  // namespace

PPC_FUNC_IMPL(rex_lhBank_Open) {
  PPC_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhBank_Open);

  const PPCContext original_ctx = ctx;
  const uint32_t out_guest = ctx.r3.u32;
  const uint32_t provider_guest = ctx.r4.u32;
  const uint32_t name_guest = ctx.r5.u32;
  const uint32_t mode = ctx.r6.u32;
  if (refii::vfs::ShouldLogVfsTraceOperation("Bank_Open")) {
    REXLOG_INFO("VFS trace Bank_Open enter lr=0x{:08X} out=0x{:08X} "
                "provider={} name_obj=0x{:08X} name={} mode=0x{:08X}",
                original_ctx.lr,
                out_guest,
                refii::vfs::DescribeGuestObject(base, provider_guest, 12, 12),
                name_guest,
                refii::vfs::ReadMaybeGuestString(base, name_guest),
                mode);
  }

  const std::string bank_name =
      refii::vfs::ReadMaybeGuestString(base, name_guest);
  std::string optional_bank_asset;
  if (refii::vfs::RefiiLooseAssetBackendEnabled() &&
      refii::vfs::IsKnownOptionalBankOpenMiss(
          base, provider_guest, bank_name, &optional_bank_asset)) {
    refii::vfs::IsCachedOptionalSyntheticVfsMiss(optional_bank_asset);
    refii::vfs::TryOpenRefiiVfsAsset(
        ctx,
        base,
        original_ctx,
        refii::vfs::RefiiVfsOpenShape::NullOptionalMiss,
        refii::vfs::RefiiVfsOpenRequest{
            .api = "Bank_Open",
            .request_name = optional_bank_asset,
            .out_guest = out_guest,
            .manager_guest = provider_guest,
            .mode = mode,
            .caller_lr = static_cast<uint32_t>(original_ctx.lr),
        });
    if (refii::vfs::VfsTracingEnabled() &&
        refii::vfs::ShouldLogRepeatedVfsTraceOperation(
            "Bank_OpenKnownOptionalMiss", 32)) {
      REXLOG_INFO("Refii VFS Bank_Open known optional bank miss lr=0x{:08X} "
                  "name={} asset={} out=0x{:08X} result=0x{:08X} "
                  "streams={} reads={} banks={} handles={}",
                  original_ctx.lr,
                  bank_name,
                  optional_bank_asset,
                  out_guest,
                  ctx.r3.u32,
                  refii::vfs::RefiiHostStreamRecordCount(),
                  refii::vfs::RefiiReadRequestRecordCount(),
                  refii::vfs::SyntheticBankRecordCount(),
                  refii::vfs::RefiiPrivateHandleCount());
    }
    return;
  }
  std::string concrete_bank_candidate;
  if (refii::vfs::RefiiLooseAssetBackendEnabled() &&
      ShouldUseNativeConcreteBankContainer(bank_name, &concrete_bank_candidate)) {
    if (refii::vfs::ShouldLogVfsTraceOperation("Bank_Open")) {
      REXLOG_INFO("Refii VFS native concrete bank api=Bank_Open name={} "
                  "candidate={} out=0x{:08X}; using generated bank parser",
                  bank_name,
                  concrete_bank_candidate,
                  out_guest);
    }
    __imp__rex_lhBank_Open(ctx, base);
    return;
  }
  if (refii::vfs::TryOpenRefiiVfsAsset(
          ctx,
          base,
          original_ctx,
          refii::vfs::RefiiVfsOpenShape::SyntheticBank,
          refii::vfs::RefiiVfsOpenRequest{
              .api = "Bank_Open",
              .request_name = bank_name,
              .out_guest = out_guest,
              .manager_guest = provider_guest,
              .mode = mode,
              .caller_lr = static_cast<uint32_t>(original_ctx.lr),
          }) == refii::vfs::RefiiVfsOpenDisposition::Opened) {
    return;
  }
  const bool synthetic_bank_miss =
      refii::vfs::RefiiLooseAssetBackendEnabled() && !bank_name.empty();
  if (synthetic_bank_miss &&
      refii::vfs::ShouldLogVfsTraceOperation("Bank_Open")) {
    REXLOG_INFO("Refii synthetic bank miss api=Bank_Open name={} out=0x{:08X}; "
                "using original bank open path for native failure semantics",
                bank_name,
                out_guest);
  }

  __imp__rex_lhBank_Open(ctx, base);

  if (refii::vfs::ShouldLogVfsTraceOperation("Bank_Open")) {
    const uint32_t bank_guest = refii::vfs::TryReadOutputPointer(base, out_guest);
    REXLOG_INFO("VFS trace Bank_Open exit lr=0x{:08X} result=0x{:08X} "
                "out=0x{:08X} bank={} bank_name={}",
                original_ctx.lr,
                ctx.r3.u32,
                out_guest,
                refii::vfs::DescribeGuestObject(base, bank_guest, 20, 12),
                refii::vfs::ReadBankDebugName(base, bank_guest));
  }
}

PPC_FUNC_IMPL(rex_lhBank_OpenWithManifest) {
  PPC_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhBank_OpenWithManifest);

  const PPCContext original_ctx = ctx;
  const uint32_t out_guest = ctx.r3.u32;
  const uint32_t provider_guest = ctx.r4.u32;
  const uint32_t name_guest = ctx.r5.u32;
  if (refii::vfs::ShouldLogVfsTraceOperation("Bank_OpenWithManifest")) {
    REXLOG_INFO("VFS trace Bank_OpenWithManifest enter lr=0x{:08X} "
                "out=0x{:08X} provider={} name_obj=0x{:08X} name={}",
                original_ctx.lr,
                out_guest,
                refii::vfs::DescribeGuestObject(base, provider_guest, 12, 12),
                name_guest,
                refii::vfs::ReadMaybeGuestString(base, name_guest));
  }

  const std::string bank_name =
      refii::vfs::ReadMaybeGuestString(base, name_guest);
  const std::string normalized_bank_name =
      refii::vfs::NormalizeLooseAssetKey(bank_name);
  std::string concrete_bank_candidate;
  if (refii::vfs::RefiiLooseAssetBackendEnabled() &&
      ShouldUseNativeConcreteBankContainer(bank_name, &concrete_bank_candidate)) {
    if (refii::vfs::ShouldLogVfsTraceOperation("Bank_OpenWithManifest")) {
      REXLOG_INFO("Refii VFS native concrete bank api=Bank_OpenWithManifest "
                  "name={} candidate={} out=0x{:08X}; using generated bank "
                  "parser",
                  bank_name,
                  concrete_bank_candidate,
                  out_guest);
    }
    __imp__rex_lhBank_OpenWithManifest(ctx, base);
    return;
  }
  const refii::vfs::RefiiVfsOpenShape open_shape =
      normalized_bank_name == "dir.manifest"
          ? refii::vfs::RefiiVfsOpenShape::NativeGenerated
          : refii::vfs::RefiiVfsOpenShape::SyntheticBank;
  const refii::vfs::RefiiVfsOpenDisposition disposition =
      refii::vfs::TryOpenRefiiVfsAsset(
          ctx,
          base,
          original_ctx,
          open_shape,
          refii::vfs::RefiiVfsOpenRequest{
              .api = "Bank_OpenWithManifest",
              .request_name = bank_name,
              .out_guest = out_guest,
              .manager_guest = provider_guest,
              .caller_lr = static_cast<uint32_t>(original_ctx.lr),
          });
  if (disposition == refii::vfs::RefiiVfsOpenDisposition::Opened) {
    return;
  }
  const bool synthetic_bank_miss =
      refii::vfs::RefiiLooseAssetBackendEnabled() && !bank_name.empty() &&
      open_shape == refii::vfs::RefiiVfsOpenShape::SyntheticBank;
  if (synthetic_bank_miss &&
      refii::vfs::ShouldLogVfsTraceOperation("Bank_OpenWithManifest")) {
    REXLOG_INFO("Refii synthetic bank miss api=Bank_OpenWithManifest name={} "
                "out=0x{:08X}; using original bank open path for native failure "
                "semantics",
                bank_name,
                out_guest);
  }

  __imp__rex_lhBank_OpenWithManifest(ctx, base);

  if (refii::vfs::ShouldLogVfsTraceOperation("Bank_OpenWithManifest")) {
    const uint32_t bank_guest = refii::vfs::TryReadOutputPointer(base, out_guest);
    REXLOG_INFO("VFS trace Bank_OpenWithManifest exit lr=0x{:08X} "
                "result=0x{:08X} out=0x{:08X} bank={} bank_name={}",
                original_ctx.lr,
                ctx.r3.u32,
                out_guest,
                refii::vfs::DescribeGuestObject(base, bank_guest, 20, 12),
                refii::vfs::ReadBankDebugName(base, bank_guest));
  }
}

PPC_FUNC_IMPL(rex_lhBank_ParseHeader) {
  PPC_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhBank_ParseHeader);

  const PPCContext original_ctx = ctx;
  const uint32_t out_guest = ctx.r3.u32;
  const uint32_t stream_guest = ctx.r4.u32;
  const uint32_t name_guest = ctx.r5.u32;
  const uint32_t mode = ctx.r6.u32;
  if (refii::vfs::ShouldLogVfsTraceOperation("Bank_ParseHeader")) {
    REXLOG_INFO("VFS trace Bank_ParseHeader enter lr=0x{:08X} out=0x{:08X} "
                "stream={} name_obj=0x{:08X} name={} mode=0x{:08X}",
                original_ctx.lr,
                out_guest,
                refii::vfs::DescribeGuestObject(base, stream_guest, 12, 12),
                name_guest,
                refii::vfs::ReadMaybeGuestString(base, name_guest),
                mode);
  }

  const std::string bank_name =
      refii::vfs::ReadMaybeGuestString(base, name_guest);
  std::string synthetic_bank_name = bank_name;
  std::string synthetic_bank_source = "name";
  if (synthetic_bank_name.empty()) {
    synthetic_bank_name =
        refii::vfs::InferSyntheticBankNameFromStream(base, stream_guest);
    synthetic_bank_source = "stream_provider";
  }
  std::string concrete_bank_candidate;
  if (refii::vfs::RefiiLooseAssetBackendEnabled() &&
      ShouldUseNativeConcreteBankContainer(synthetic_bank_name,
                                           &concrete_bank_candidate)) {
    if (refii::vfs::ShouldLogVfsTraceOperation("Bank_ParseHeader")) {
      REXLOG_INFO("Refii VFS native concrete bank api=Bank_ParseHeader "
                  "name={} source={} candidate={} out=0x{:08X}; using "
                  "generated bank parser",
                  synthetic_bank_name,
                  synthetic_bank_source,
                  concrete_bank_candidate,
                  out_guest);
    }
    __imp__rex_lhBank_ParseHeader(ctx, base);
    return;
  }
  if (refii::vfs::TryOpenRefiiVfsAsset(
          ctx,
          base,
          original_ctx,
          refii::vfs::RefiiVfsOpenShape::SyntheticBank,
          refii::vfs::RefiiVfsOpenRequest{
              .api = "Bank_ParseHeader",
              .request_name = synthetic_bank_name,
              .out_guest = out_guest,
              .manager_guest = stream_guest,
              .mode = mode,
              .caller_lr = static_cast<uint32_t>(original_ctx.lr),
          }) == refii::vfs::RefiiVfsOpenDisposition::Opened) {
    if (refii::vfs::VfsTracingEnabled()) {
      const uint32_t bank_guest =
          refii::vfs::TryReadOutputPointer(base, out_guest);
      const uint32_t entries_begin =
          bank_guest == 0 ? 0 : refii::vfs::LoadGuestU32(base, bank_guest + 68);
      const uint32_t entries_end =
          bank_guest == 0 ? 0 : refii::vfs::LoadGuestU32(base, bank_guest + 72);
      const uint32_t entry_count =
          entries_end > entries_begin ? (entries_end - entries_begin) / 12 : 0;
      REXLOG_INFO("Refii synthetic bank parse hit name={} source={} "
                  "stream=0x{:08X} out=0x{:08X} bank=0x{:08X} entries={}",
                  synthetic_bank_name,
                  synthetic_bank_source,
                  stream_guest,
                  out_guest,
                  bank_guest,
                  entry_count);
    }
    return;
  }
  const bool synthetic_bank_miss =
      refii::vfs::RefiiLooseAssetBackendEnabled() &&
      (!bank_name.empty() || !synthetic_bank_name.empty());
  if (synthetic_bank_miss &&
      refii::vfs::ShouldLogVfsTraceOperation("Bank_ParseHeader")) {
    REXLOG_INFO("Refii synthetic bank parse miss name={} stream=0x{:08X}; "
                "using original parse path for native failure semantics",
                synthetic_bank_name.empty() ? bank_name : synthetic_bank_name,
                stream_guest);
  }

  __imp__rex_lhBank_ParseHeader(ctx, base);

  if (refii::vfs::ShouldLogVfsTraceOperation("Bank_ParseHeader")) {
    const uint32_t bank_guest = refii::vfs::TryReadOutputPointer(base, out_guest);
    const uint32_t entries_begin =
        bank_guest == 0 ? 0 : refii::vfs::LoadGuestU32(base, bank_guest + 68);
    const uint32_t entries_end =
        bank_guest == 0 ? 0 : refii::vfs::LoadGuestU32(base, bank_guest + 72);
    const uint32_t entry_count =
        entries_end > entries_begin ? (entries_end - entries_begin) / 12 : 0;
    REXLOG_INFO("VFS trace Bank_ParseHeader exit lr=0x{:08X} result=0x{:08X} "
                "out=0x{:08X} bank={} bank_name={} entries={}",
                original_ctx.lr,
                ctx.r3.u32,
                out_guest,
                refii::vfs::DescribeGuestObject(base, bank_guest, 24, 12),
                refii::vfs::ReadBankDebugName(base, bank_guest),
                entry_count);
  }
}

PPC_FUNC_IMPL(rex_lhBank_DirectoryIterator_Ctor) {
  PPC_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhBank_DirectoryIterator_Ctor);

  const PPCContext original_ctx = ctx;
  const uint32_t iterator_guest = ctx.r3.u32;
  const uint32_t bank_guest = ctx.r4.u32;
  const uint32_t provider_smart_ptr_guest = ctx.r5.u32;
  const uint32_t take_inner_provider = ctx.r6.u32;

  __imp__rex_lhBank_DirectoryIterator_Ctor(ctx, base);

  if (!refii::vfs::RefiiLooseAssetBackendEnabled() ||
      iterator_guest == 0) {
    return;
  }

  const auto bank_record =
      refii::vfs::FindSyntheticBankRecord(bank_guest);
  if (bank_record == nullptr) {
    return;
  }

  auto iterator_record =
      std::make_shared<refii::vfs::RefiiSyntheticBankIteratorRecord>();
  iterator_record->bank_guest = bank_guest;
  iterator_record->bank_record = bank_record;
  iterator_record->next_entry_index = 1;
  refii::vfs::StoreSyntheticBankIteratorRecord(iterator_guest,
                                                   iterator_record);

  if (refii::vfs::VfsTracingEnabled()) {
    REXLOG_INFO("Refii synthetic bank iterator opened iterator=0x{:08X} "
                "bank=0x{:08X} bank_name={} entries={} provider_sp=0x{:08X} "
                "take_inner={} active_iterators={}",
                iterator_guest,
                bank_guest,
                bank_record->bank_name,
                bank_record->bank_template == nullptr
                    ? 0
                    : bank_record->bank_template->entry_line_offsets.size(),
                provider_smart_ptr_guest,
                take_inner_provider,
                refii::vfs::SyntheticBankIteratorRecordCount());
  }
}

PPC_FUNC_IMPL(rex_lhBank_DirIter_FindStreamNext) {
  PPC_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhBank_DirIter_FindStreamNext);

  const PPCContext original_ctx = ctx;
  const uint32_t out_guest = ctx.r3.u32;
  const uint32_t iterator_guest = ctx.r4.u32;
  const uint32_t filter_guest = ctx.r5.u32;
  const uint32_t mode = ctx.r6.u32;
  const auto iterator_record =
      refii::vfs::FindSyntheticBankIteratorRecord(iterator_guest);
  if (refii::vfs::RefiiLooseAssetBackendEnabled() &&
      iterator_record != nullptr && iterator_record->bank_record != nullptr &&
      iterator_record->bank_record->bank_template != nullptr) {
    const std::string filter =
        refii::vfs::ReadGuestWideStringObject(base, filter_guest, 4096);
    const auto& bank_template = iterator_record->bank_record->bank_template;
    const uint32_t entry_count =
        static_cast<uint32_t>(bank_template->entry_line_offsets.size());
    const uint32_t manager_guest =
        refii::vfs::GetGlobalDataProvider(base, original_ctx);

    for (uint32_t entry_index = iterator_record->next_entry_index;
         entry_index <= entry_count;
         ++entry_index) {
      refii::vfs::BankManifestEntry entry;
      if (!refii::vfs::ReadSyntheticBankEntryByIndex(
              bank_template, entry_index, &entry)) {
        continue;
      }
      if (!refii::vfs::SyntheticBankEntryMatchesFilter(entry, filter)) {
        continue;
      }

      iterator_record->next_entry_index = entry_index + 1;
      if (refii::vfs::ShouldSkipLooseNativeAsset(entry.relative_path,
                                                    "bank_diriter_next")) {
        continue;
      }

      refii::vfs::RefiiVfsOpenResult open_result;
      refii::vfs::TryOpenRefiiBankEntryAsset(
          ctx,
          base,
          original_ctx,
          refii::vfs::RefiiBankEntryOpenRequest{
              .api = "Bank_DirIter_FindStreamNext",
              .bank_name = iterator_record->bank_record->bank_name,
              .entry_name = entry.relative_path,
              .manifest_entry = &entry,
              .out_guest = out_guest,
              .manager_guest = manager_guest,
              .mode = mode,
              .caller_lr = static_cast<uint32_t>(original_ctx.lr),
              .count_bank_lookup = false,
              .lookup_label = "bank_diriter_next",
              .lua_trace_tag = "bank_diriter_find_stream_next",
          },
          &open_result);
      if (open_result.disposition !=
          refii::vfs::RefiiVfsOpenDisposition::Opened) {
        if (refii::vfs::VfsTracingEnabled()) {
          REXLOG_INFO("Refii VFS bank diriter next unresolved bank={} "
                      "iterator=0x{:08X} index={} entry={} filter={} "
                      "disposition={}",
                      iterator_record->bank_record->bank_name,
                      iterator_guest,
                      entry_index,
                      entry.relative_path,
                      filter,
                      refii::vfs::ToString(open_result.disposition));
        }
        continue;
      }

      const uint64_t hits =
          refii::vfs::LooseStats().bank_hits.fetch_add(
              1, std::memory_order_relaxed) +
          1;
      refii::vfs::LogLooseStatsMilestone("bank_hits", hits);
      if (refii::vfs::VfsTracingEnabled() ||
          refii::vfs::LooseAssetOverrideTracingEnabled() ||
          refii::vfs::ShouldLogLooseStatsMilestone(hits)) {
        REXLOG_INFO("Refii VFS bank diriter next hit hit={} bank={} "
                    "iterator=0x{:08X} index={} entry={} filter={} "
                    "candidate={} provider_relative={} host={} "
                    "stream=0x{:08X} mode=0x{:08X}",
                    hits,
                    iterator_record->bank_record->bank_name,
                    iterator_guest,
                    entry_index,
                    entry.relative_path,
                    filter,
                    open_result.candidate,
                    open_result.resolution.provider_relative_path,
                    open_result.resolution.host_path.string(),
                    open_result.opened_guest,
                    mode);
      }
      return;
    }

    if (out_guest != 0) {
      refii::vfs::StoreGuestU32(base, out_guest, 0);
    }
    ctx.r3.u32 = out_guest;
    if (refii::vfs::VfsTracingEnabled()) {
      REXLOG_INFO("Refii VFS bank diriter next exhausted bank={} "
                  "iterator=0x{:08X} next_index={} entries={} filter={}",
                  iterator_record->bank_record->bank_name,
                  iterator_guest,
                  iterator_record->next_entry_index,
                  entry_count,
                  filter);
    }
    return;
  }

  ctx = original_ctx;
  __imp__rex_lhBank_DirIter_FindStreamNext(ctx, base);
}

PPC_FUNC_IMPL(rex_lhBank_DirIter_GetName) {
  PPC_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhBank_DirIter_GetName);

  const PPCContext original_ctx = ctx;
  __imp__rex_lhBank_DirIter_GetName(ctx, base);
  if (refii::vfs::VfsTracingEnabled() &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "BankDirIter_GetName", 32)) {
    REXLOG_INFO("VFS trace Bank_DirIter_GetName lr=0x{:08X} out=0x{:08X} "
                "result=0x{:08X} out_words={}",
                original_ctx.lr,
                original_ctx.r3.u32,
                ctx.r3.u32,
                refii::vfs::FormatGuestWords(base, original_ctx.r3.u32, 8));
  }
}

PPC_FUNC_IMPL(rex_lhBank_OpenStreamByIndex) {
  PPC_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhBank_OpenStreamByIndex);

  const PPCContext original_ctx = ctx;
  const uint32_t out_guest = ctx.r3.u32;
  const uint32_t bank_guest = ctx.r4.u32;
  const uint32_t entry_index = ctx.r5.u32;
  const uint32_t mode = ctx.r6.u32;

  if (refii::vfs::RefiiLooseAssetBackendEnabled() &&
      refii::vfs::LooseBankEntryOverridesEnabled()) {
    const std::string bank_name =
        refii::vfs::ReadBankNameForLookup(base, bank_guest);
    refii::vfs::BankManifestEntry synthetic_entry;
    const auto synthetic_bank =
        refii::vfs::FindSyntheticBankRecord(bank_guest);
    const bool has_synthetic_entry =
        synthetic_bank != nullptr &&
        refii::vfs::ReadSyntheticBankEntryByIndex(
            synthetic_bank->bank_template, entry_index, &synthetic_entry);
    const std::string entry_name =
        has_synthetic_entry
            ? synthetic_entry.relative_path
            : refii::vfs::LookupBankIndexEntryName(base,
                                                       bank_guest,
                                                       entry_index);
    if (synthetic_bank != nullptr &&
        refii::vfs::ShouldTraceSyntheticBankEntry(
            bank_name, has_synthetic_entry ? &synthetic_entry : nullptr)) {
      REXLOG_INFO("Refii synthetic bank index lookup api=Bank_OpenStreamByIndex "
                  "bank={} bank_obj=0x{:08X} index={} mode=0x{:08X} "
                  "has_entry={} entry={} table={}{}",
                  bank_name,
                  bank_guest,
                  entry_index,
                  mode,
                  has_synthetic_entry,
                  entry_name,
                  synthetic_bank->bank_template == nullptr
                      ? std::string("<null>")
                      : fmt::format("0x{:08X}-0x{:08X}",
                                    synthetic_bank->bank_template->entries_begin,
                                    synthetic_bank->bank_template->entries_end),
                  refii::vfs::DescribeSyntheticBankEntryWindow(
                      synthetic_bank->bank_template, entry_index));
    }
    if (entry_name.empty() &&
        refii::vfs::LooseAssetOverrideTracingEnabled()) {
      uint32_t entry_offset = 0;
      uint32_t entry_size = 0;
      const bool has_entry = refii::vfs::ReadBankIndexEntryOffsetSize(
          base, bank_guest, entry_index, &entry_offset, &entry_size);
      REXLOG_INFO("Loose bank index manifest miss bank={} index={} "
                  "has_entry={} offset={} size={} root={}",
                  bank_name,
                  entry_index,
                  has_entry,
                  entry_offset,
                  entry_size,
                  refii::vfs::GetLooseAssetRoot().string());
    }
    if (!entry_name.empty()) {
      const uint32_t manager_guest =
          refii::vfs::GetGlobalDataProvider(base, original_ctx);
      refii::vfs::RefiiVfsOpenResult open_result;
      refii::vfs::TryOpenRefiiBankEntryAsset(
          ctx,
          base,
          original_ctx,
          refii::vfs::RefiiBankEntryOpenRequest{
              .api = "Bank_OpenStreamByIndex",
              .bank_name = bank_name,
              .entry_name = entry_name,
              .manifest_entry = has_synthetic_entry ? &synthetic_entry : nullptr,
              .out_guest = out_guest,
              .manager_guest = manager_guest,
              .mode = mode,
              .caller_lr = static_cast<uint32_t>(original_ctx.lr),
              .count_bank_lookup =
                  !refii::vfs::IsLuaScriptAssetKey(entry_name),
              .lookup_label = "bank_index",
              .lua_trace_tag = "bank_open_stream_by_index",
          },
          &open_result);
      if (open_result.disposition ==
          refii::vfs::RefiiVfsOpenDisposition::Opened) {
        const uint64_t hits =
            refii::vfs::LooseStats().bank_hits.fetch_add(
                1, std::memory_order_relaxed) +
            1;
        refii::vfs::LogLooseStatsMilestone("bank_hits", hits);
        if (refii::vfs::IsLuaScriptAssetKey(entry_name)) {
          if (refii::vfs::LooseAssetOverrideTracingEnabled() ||
              refii::vfs::VfsTracingEnabled() ||
              refii::vfs::ShouldLogLooseStatsMilestone(hits)) {
            REXLOG_INFO("Refii VFS bank Lua script hit hit={} bank={} "
                        "index={} entry={} candidate={} source={} "
                        "provider_relative={} host={} stream=0x{:08X}",
                        hits,
                        bank_name,
                        entry_index,
                        entry_name,
                        open_result.candidate,
                        open_result.source,
                        open_result.resolution.provider_relative_path,
                        open_result.resolution.host_path.string(),
                        open_result.opened_guest);
          }
        } else {
          refii::vfs::LogLooseBankHitDetail("index",
                                                base,
                                                original_ctx,
                                                out_guest,
                                                bank_guest,
                                                manager_guest,
                                                mode,
                                                hits,
                                                bank_name,
                                                entry_name,
                                                open_result.candidate,
                                                open_result.resolution);
          if (refii::vfs::LooseAssetOverrideTracingEnabled()) {
            REXLOG_INFO("Loose bank index override bank={} index={} entry={} "
                        "candidate={} -> {}",
                        bank_name,
                        entry_index,
                        entry_name,
                        open_result.candidate,
                        open_result.resolution.host_path.string());
          }
        }
        return;
      }
    } else if (refii::vfs::LooseAssetOverrideTracingEnabled()) {
      REXLOG_INFO("Refii bank index miss bank={} index={} root={}",
                  bank_name,
                  entry_index,
                  refii::vfs::GetLooseAssetRoot().string());
    }

    if (!entry_name.empty() && !refii::vfs::IsLuaScriptAssetKey(entry_name) &&
        !refii::vfs::ShouldSkipLooseNativeAsset(entry_name, "bank_index")) {
      if (out_guest != 0) {
        refii::vfs::StoreGuestU32(base, out_guest, 0);
      }
      ctx.r3.u32 = out_guest;
      return;
    }
    if (synthetic_bank != nullptr) {
      if (out_guest != 0) {
        refii::vfs::StoreGuestU32(base, out_guest, 0);
      }
      ctx.r3.u32 = out_guest;
      return;
    }
  }

  ctx = original_ctx;
  __imp__rex_lhBank_OpenStreamByIndex(ctx, base);
}

PPC_FUNC_IMPL(rex_lhBank_DirIter_FindStreamByIndex) {
  PPC_FUNC_PROLOGUE();

  REFII_VFS_NATIVE_PASSTHROUGH(rex_lhBank_DirIter_FindStreamByIndex);

  const PPCContext original_ctx = ctx;
  const uint32_t out_guest = ctx.r3.u32;
  const uint32_t iterator_guest = ctx.r4.u32;
  const uint32_t entry_index = ctx.r5.u32;
  const uint32_t mode = ctx.r6.u32;
  const uint32_t iterator_bank_guest =
      iterator_guest == 0 ? 0
                          : refii::vfs::LoadGuestU32(
                                base, iterator_guest + 12);
  const bool trace_entry =
      refii::vfs::VfsTracingEnabled() &&
      refii::vfs::ShouldLogRepeatedVfsTraceOperation(
          "BankDirIter_FindStreamByIndex", 48);
  if (trace_entry) {
    REXLOG_INFO("VFS trace Bank_DirIter_FindStreamByIndex enter lr=0x{:08X} "
                "out=0x{:08X} iterator={} bank={} index={} mode=0x{:08X}",
                original_ctx.lr,
                out_guest,
                refii::vfs::DescribeGuestObject(
                    base, iterator_guest, 16, 12),
                refii::vfs::DescribeGuestObject(
                    base, iterator_bank_guest, 20, 12),
                entry_index,
                mode);
  }

  uint32_t bank_guest = iterator_bank_guest;
  auto synthetic_bank =
      refii::vfs::FindSyntheticBankRecord(iterator_bank_guest);
  if (synthetic_bank == nullptr) {
    const auto iterator_record =
        refii::vfs::FindSyntheticBankIteratorRecord(iterator_guest);
    if (iterator_record != nullptr) {
      bank_guest = iterator_record->bank_guest;
      synthetic_bank = iterator_record->bank_record;
    }
  }
  if (synthetic_bank == nullptr) {
    synthetic_bank = refii::vfs::FindSyntheticBankRecord(iterator_guest);
    if (synthetic_bank != nullptr) {
      bank_guest = iterator_guest;
    }
  }

  if (refii::vfs::RefiiLooseAssetBackendEnabled() &&
      refii::vfs::LooseBankEntryOverridesEnabled()) {
    const std::string bank_name =
        synthetic_bank != nullptr
            ? synthetic_bank->bank_name
            : refii::vfs::ReadBankNameForLookup(base, bank_guest);
    refii::vfs::BankManifestEntry synthetic_entry;
    const bool has_synthetic_entry =
        synthetic_bank != nullptr &&
        refii::vfs::ReadSyntheticBankEntryByIndex(
            synthetic_bank->bank_template, entry_index, &synthetic_entry);
    const std::string entry_name =
        has_synthetic_entry
            ? synthetic_entry.relative_path
            : refii::vfs::LookupBankIndexEntryName(base,
                                                       bank_guest,
                                                       entry_index);
    if (synthetic_bank != nullptr &&
        refii::vfs::ShouldTraceSyntheticBankEntry(
            bank_name, has_synthetic_entry ? &synthetic_entry : nullptr)) {
      REXLOG_INFO("Refii synthetic bank diriter index lookup bank={} "
                  "iterator=0x{:08X} bank_obj=0x{:08X} index={} "
                  "mode=0x{:08X} has_entry={} entry={} table={}{}",
                  bank_name,
                  iterator_guest,
                  bank_guest,
                  entry_index,
                  mode,
                  has_synthetic_entry,
                  entry_name,
                  synthetic_bank->bank_template == nullptr
                      ? std::string("<null>")
                      : fmt::format("0x{:08X}-0x{:08X}",
                                    synthetic_bank->bank_template->entries_begin,
                                    synthetic_bank->bank_template->entries_end),
                  refii::vfs::DescribeSyntheticBankEntryWindow(
                      synthetic_bank->bank_template, entry_index));
    }
    if (!entry_name.empty()) {
      const uint32_t manager_guest =
          refii::vfs::GetGlobalDataProvider(base, original_ctx);
      refii::vfs::RefiiVfsOpenResult open_result;
      refii::vfs::TryOpenRefiiBankEntryAsset(
          ctx,
          base,
          original_ctx,
          refii::vfs::RefiiBankEntryOpenRequest{
              .api = "Bank_DirIter_FindStreamByIndex",
              .bank_name = bank_name,
              .entry_name = entry_name,
              .manifest_entry = has_synthetic_entry ? &synthetic_entry : nullptr,
              .out_guest = out_guest,
              .manager_guest = manager_guest,
              .mode = mode,
              .caller_lr = static_cast<uint32_t>(original_ctx.lr),
              .count_bank_lookup = false,
              .lookup_label = "bank_diriter_index",
              .lua_trace_tag = "bank_diriter_find_stream_by_index",
          },
          &open_result);
      if (open_result.disposition ==
          refii::vfs::RefiiVfsOpenDisposition::Opened) {
        const uint64_t hits =
            refii::vfs::LooseStats().bank_hits.fetch_add(
                1, std::memory_order_relaxed) +
            1;
        refii::vfs::LogLooseStatsMilestone("bank_hits", hits);
        if (refii::vfs::IsLuaScriptAssetKey(entry_name)) {
          if (refii::vfs::VfsTracingEnabled() ||
              refii::vfs::LooseAssetOverrideTracingEnabled() ||
              refii::vfs::ShouldLogLooseStatsMilestone(hits)) {
            REXLOG_INFO("Refii VFS bank diriter Lua script hit hit={} "
                        "bank={} iterator=0x{:08X} bank_obj=0x{:08X} "
                        "index={} entry={} candidate={} source={} "
                        "provider_relative={} host={} stream=0x{:08X}",
                        hits,
                        bank_name,
                        iterator_guest,
                        bank_guest,
                        entry_index,
                        entry_name,
                        open_result.candidate,
                        open_result.source,
                        open_result.resolution.provider_relative_path,
                        open_result.resolution.host_path.string(),
                        open_result.opened_guest);
          }
        } else {
          refii::vfs::LogLooseBankHitDetail(
              "diriter_index",
              base,
              original_ctx,
              out_guest,
              bank_guest,
              manager_guest,
              mode,
              hits,
              bank_name,
              entry_name,
              open_result.candidate,
              open_result.resolution);
          if (refii::vfs::VfsTracingEnabled() ||
              refii::vfs::LooseAssetOverrideTracingEnabled() ||
              refii::vfs::ShouldLogLooseStatsMilestone(hits)) {
            REXLOG_INFO("Refii VFS bank diriter index hit hit={} "
                        "bank={} iterator=0x{:08X} bank_obj=0x{:08X} "
                        "index={} entry={} candidate={} provider_relative={} "
                        "host={} stream=0x{:08X} mode=0x{:08X}",
                        hits,
                        bank_name,
                        iterator_guest,
                        bank_guest,
                        entry_index,
                        entry_name,
                        open_result.candidate,
                        open_result.resolution.provider_relative_path,
                        open_result.resolution.host_path.string(),
                        open_result.opened_guest,
                        mode);
          }
        }
        return;
      }
      if (refii::vfs::VfsTracingEnabled() ||
          refii::vfs::LooseAssetOverrideTracingEnabled()) {
        REXLOG_INFO("Refii VFS bank diriter index unresolved bank={} "
                    "iterator=0x{:08X} bank_obj=0x{:08X} index={} entry={} "
                    "mode=0x{:08X} disposition={}",
                    bank_name,
                    iterator_guest,
                    bank_guest,
                    entry_index,
                    entry_name,
                    mode,
                    refii::vfs::ToString(open_result.disposition));
      }
    }

    if (!entry_name.empty() || synthetic_bank != nullptr) {
      if (out_guest != 0) {
        refii::vfs::StoreGuestU32(base, out_guest, 0);
      }
      ctx.r3.u32 = out_guest;
      if (refii::vfs::VfsTracingEnabled() ||
          refii::vfs::LooseAssetOverrideTracingEnabled()) {
        REXLOG_INFO("Refii VFS bank diriter index miss bank={} iterator=0x{:08X} "
                    "bank_obj=0x{:08X} index={} entry={} mode=0x{:08X}",
                    bank_name,
                    iterator_guest,
                    bank_guest,
                    entry_index,
                    entry_name,
                    mode);
      }
      return;
    }
  }

  ctx = original_ctx;
  __imp__rex_lhBank_DirIter_FindStreamByIndex(ctx, base);
  if (trace_entry) {
    const uint32_t stream_guest =
        out_guest == 0 ? 0
                       : refii::vfs::TryReadOutputPointer(base, out_guest);
    REXLOG_INFO("VFS trace Bank_DirIter_FindStreamByIndex exit lr=0x{:08X} "
                "result=0x{:08X} out=0x{:08X} stream={}",
                original_ctx.lr,
                ctx.r3.u32,
                out_guest,
                refii::vfs::DescribeGuestObject(
                    base, stream_guest, 12, 12));
  }
}
