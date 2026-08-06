#include "vfs_hooks_private.h"

namespace refii::vfs {
inline namespace detail {

std::unordered_map<uint32_t, std::shared_ptr<RefiiSyntheticBankRecord>>&
SyntheticBankRecords() {
  static std::unordered_map<uint32_t, std::shared_ptr<RefiiSyntheticBankRecord>>
      records;
  return records;
}

std::mutex& SyntheticBankRecordsMutex() {
  static std::mutex mutex;
  return mutex;
}

std::shared_ptr<RefiiSyntheticBankRecord> FindSyntheticBankRecord(
    uint32_t bank_guest) {
  if (bank_guest == 0) {
    return {};
  }

  std::lock_guard<std::mutex> lock(SyntheticBankRecordsMutex());
  const auto match = SyntheticBankRecords().find(bank_guest);
  return match == SyntheticBankRecords().end() ? nullptr : match->second;
}

void StoreSyntheticBankRecord(
    uint32_t bank_guest,
    std::shared_ptr<RefiiSyntheticBankRecord> record) {
  if (bank_guest == 0 || record == nullptr) {
    return;
  }

  std::lock_guard<std::mutex> lock(SyntheticBankRecordsMutex());
  const bool inserted = SyntheticBankRecords().find(bank_guest) ==
                        SyntheticBankRecords().end();
  SyntheticBankRecords()[bank_guest] = std::move(record);
  if (inserted) {
    LooseStats().active_synthetic_banks.fetch_add(1, std::memory_order_relaxed);
  }
}

void EraseSyntheticBankRecord(uint32_t bank_guest) {
  if (bank_guest == 0) {
    return;
  }

  std::lock_guard<std::mutex> lock(SyntheticBankRecordsMutex());
  const size_t erased = SyntheticBankRecords().erase(bank_guest);
  if (erased != 0) {
    LooseStats().active_synthetic_banks.fetch_sub(erased,
                                                  std::memory_order_relaxed);
    if (VfsTracingEnabled()) {
      REXLOG_INFO("Refii synthetic bank closed bank=0x{:08X} active={}",
                  bank_guest,
                  SyntheticBankRecords().size());
    }
  }
}

size_t SyntheticBankRecordCount() {
  std::lock_guard<std::mutex> lock(SyntheticBankRecordsMutex());
  return SyntheticBankRecords().size();
}

std::unordered_map<uint32_t, std::shared_ptr<RefiiSyntheticBankIteratorRecord>>&
SyntheticBankIteratorRecords() {
  static std::unordered_map<uint32_t,
                            std::shared_ptr<RefiiSyntheticBankIteratorRecord>>
      records;
  return records;
}

std::mutex& SyntheticBankIteratorRecordsMutex() {
  static std::mutex mutex;
  return mutex;
}

std::shared_ptr<RefiiSyntheticBankIteratorRecord>
FindSyntheticBankIteratorRecord(uint32_t iterator_guest) {
  if (iterator_guest == 0) {
    return nullptr;
  }
  std::lock_guard<std::mutex> lock(SyntheticBankIteratorRecordsMutex());
  const auto match = SyntheticBankIteratorRecords().find(iterator_guest);
  return match == SyntheticBankIteratorRecords().end() ? nullptr : match->second;
}

void StoreSyntheticBankIteratorRecord(
    uint32_t iterator_guest,
    std::shared_ptr<RefiiSyntheticBankIteratorRecord> record) {
  if (iterator_guest == 0 || record == nullptr) {
    return;
  }
  std::lock_guard<std::mutex> lock(SyntheticBankIteratorRecordsMutex());
  SyntheticBankIteratorRecords()[iterator_guest] = std::move(record);
}

void EraseSyntheticBankIteratorRecord(uint32_t iterator_guest) {
  if (iterator_guest == 0) {
    return;
  }
  std::lock_guard<std::mutex> lock(SyntheticBankIteratorRecordsMutex());
  const size_t erased = SyntheticBankIteratorRecords().erase(iterator_guest);
  if (erased != 0 && VfsTracingEnabled()) {
    REXLOG_INFO("Refii synthetic bank iterator closed iterator=0x{:08X} active={}",
                iterator_guest,
                SyntheticBankIteratorRecords().size());
  }
}

size_t SyntheticBankIteratorRecordCount() {
  std::lock_guard<std::mutex> lock(SyntheticBankIteratorRecordsMutex());
  return SyntheticBankIteratorRecords().size();
}

std::unordered_map<std::string, std::shared_ptr<RefiiSyntheticBankTemplate>>&
SyntheticBankTemplates() {
  static std::unordered_map<std::string,
                            std::shared_ptr<RefiiSyntheticBankTemplate>>
      templates;
  return templates;
}

std::mutex& SyntheticBankTemplatesMutex() {
  static std::mutex mutex;
  return mutex;
}

std::shared_ptr<const RefiiSyntheticBankTemplate>
FindOrCreateSyntheticBankTemplate(PPCContext& ctx,
                                  uint8_t* base,
                                  std::string_view bank_name,
                                  std::string_view api) {
  (void)ctx;
  (void)base;
  // FindSyntheticBankManifest is the single manifest resolver: it consults the
  // extracted banks.tsv typed lookup (locale-redirected banks such as
  // language/speech/speech.bnk only resolve there), recorded open aliases, and
  // finally the path-heuristic candidates.
  std::filesystem::path manifest_root;
  std::filesystem::path manifest_path;
  if (!FindSyntheticBankManifest(bank_name, &manifest_root, &manifest_path)) {
    return {};
  }

  {
    const std::filesystem::path template_root = manifest_root;
    const std::string template_key =
        manifest_path.lexically_normal().string();

    {
      std::lock_guard<std::mutex> lock(SyntheticBankTemplatesMutex());
      const auto match = SyntheticBankTemplates().find(template_key);
      if (match != SyntheticBankTemplates().end()) {
        return match->second;
      }
    }

    auto bank_template = std::make_shared<RefiiSyntheticBankTemplate>();
    bank_template->manifest_path = manifest_path;
    bank_template->manifest_root = template_root;

    std::ifstream manifest(manifest_path);
    if (!manifest) {
      return {};
    }
    struct SyntheticEntryRow {
      uint32_t name_hash = 0;
      uint32_t offset = 0;
      uint32_t size = 0;
    };
    // Native bank rows are {entry-name hash, offset, size}; the hash is a
    // case-folded FNV-1 (sub_825D6B60 with tolower per byte) over the entry
    // name as the game spells it, i.e. with backslash separators. Consumers
    // such as the resource filing system registration key entries by this
    // hash, so it must be populated for synthetic banks too.
    auto compute_entry_name_hash = [](std::string_view path) -> uint32_t {
      uint32_t hash = 0x811C9DC5u;
      for (const char ch : path) {
        unsigned char byte = static_cast<unsigned char>(ch);
        if (byte == '/') {
          byte = '\\';
        }
        byte = static_cast<unsigned char>(std::tolower(byte));
        hash = static_cast<uint32_t>(byte) ^ (hash * 0x01000193u);
      }
      return hash;
    };
    std::vector<SyntheticEntryRow> entry_fields;
    std::string line;
    std::getline(manifest, line);
    while (true) {
      const std::streamoff line_offset = manifest.tellg();
      if (!std::getline(manifest, line)) {
        break;
      }
      if (!line.empty()) {
        bank_template->entry_line_offsets.push_back(line_offset);
        BankManifestEntry entry;
        if (ParseBankManifestEntryLine(template_root, line, &entry)) {
          // The bank name table / hash key uses backslash-separated names.
          std::string entry_name = entry.relative_path;
          std::replace(entry_name.begin(), entry_name.end(), '/', '\\');
          entry_fields.push_back(SyntheticEntryRow{
              compute_entry_name_hash(entry.relative_path),
              entry.offset,
              entry.size});
          bank_template->entry_names.push_back(std::move(entry_name));
        } else {
          entry_fields.push_back(SyntheticEntryRow{});
          bank_template->entry_names.emplace_back();
        }
      }
    }

    if (!entry_fields.empty()) {
      constexpr uint32_t kBankEntrySize = 12;
      const uint32_t entry_count =
          static_cast<uint32_t>(entry_fields.size());
      const uint32_t entries_guest =
          CallLhMalloc(ctx, base, entry_count * kBankEntrySize);
      if (entries_guest == 0) {
        REXLOG_WARN("Refii synthetic bank entry allocation failed api={} bank={} "
                    "entries={}",
                    api,
                    bank_name,
                    entry_count);
        return {};
      }

      auto* entries =
          rex::memory::GuestPtr<uint8_t*>(base, entries_guest);
      std::memset(entries, 0, entry_count * kBankEntrySize);
      for (uint32_t index = 0; index < entry_count; ++index) {
        const uint32_t entry_guest = entries_guest + index * kBankEntrySize;
        StoreGuestU32(base, entry_guest + 0, entry_fields[index].name_hash);
        StoreGuestU32(base, entry_guest + 4, entry_fields[index].offset);
        StoreGuestU32(base, entry_guest + 8, entry_fields[index].size);
      }
      bank_template->entries_begin = entries_guest;
      bank_template->entries_end = entries_guest + entry_count * kBankEntrySize;
    }

    {
      std::lock_guard<std::mutex> lock(SyntheticBankTemplatesMutex());
      auto [iterator, inserted] =
          SyntheticBankTemplates().try_emplace(template_key, bank_template);
      if (!inserted) {
        return iterator->second;
      }
    }

    if (VfsTracingEnabled()) {
      REXLOG_INFO("Refii synthetic bank template created api={} root={} "
                  "entries={}",
                  api,
                  template_root.generic_string(),
                  bank_template->entry_line_offsets.size());
    }
    return bank_template;
  }

  return {};
}

std::string ReadBankNameForLookup(uint8_t* base, uint32_t bank_guest) {
  if (const auto record = FindSyntheticBankRecord(bank_guest)) {
    return record->bank_name;
  }
  return ReadGuestWideStringObject(base, bank_guest + 8, 4096);
}

std::string InferSyntheticBankNameFromStream(uint8_t* base,
                                             uint32_t stream_guest) {
  LooseStreamRecord record;
  std::string match;
  if (!FindLooseStreamRecord(base, stream_guest, &record, &match)) {
    return {};
  }

  const std::string provider_name = NormalizeLooseAssetKey(record.provider_name);
  if (EndsWith(provider_name, ".bnk") &&
      FindSyntheticBankManifest(provider_name, nullptr, nullptr)) {
    return provider_name;
  }

  const std::string path = NormalizeLooseAssetKey(record.path);
  if (!EndsWith(path, "/_manifest.tsv")) {
    return {};
  }

  const std::filesystem::path loose_root = GetLooseAssetRoot();
  const std::filesystem::path manifest_path(record.path);
  std::filesystem::path relative_root =
      manifest_path.parent_path().lexically_relative(loose_root);
  if (relative_root.empty() || relative_root.string().starts_with("..")) {
    relative_root = manifest_path.parent_path();
  }
  const std::string inferred_bank =
      (relative_root.generic_string() + ".bnk");
  if (FindSyntheticBankManifest(inferred_bank, nullptr, nullptr)) {
    return NormalizeLooseAssetKey(inferred_bank);
  }
  return {};
}

bool ReadSyntheticBankEntryByIndex(
    const std::shared_ptr<const RefiiSyntheticBankTemplate>& bank_template,
    uint32_t entry_index,
    BankManifestEntry* out_entry) {
  if (bank_template == nullptr || entry_index == 0) {
    return false;
  }
  const uint32_t zero_index = entry_index - 1;
  if (zero_index >= bank_template->entry_line_offsets.size()) {
    return false;
  }

  std::ifstream manifest(bank_template->manifest_path);
  if (!manifest) {
    return false;
  }
  manifest.seekg(bank_template->entry_line_offsets[zero_index]);
  std::string line;
  if (!std::getline(manifest, line)) {
    return false;
  }
  return ParseBankManifestEntryLine(
      bank_template->manifest_root, line, out_entry);
}

bool ShouldTraceSyntheticBankEntry(std::string_view bank_name,
                                   const BankManifestEntry* entry) {
  if (!VfsTracingEnabled()) {
    return false;
  }

  const std::string normalized_bank = NormalizeLooseAssetKey(bank_name);
  if (normalized_bank.find("globals_model_headers") != std::string::npos) {
    return true;
  }
  if (entry != nullptr &&
      IsTraceInterestingAsset(entry->relative_path)) {
    return true;
  }
  return ShouldLogRepeatedVfsTraceOperation("SyntheticBankEntry", 64);
}

std::string DescribeSyntheticBankEntry(const BankManifestEntry& entry) {
  return fmt::format("root={} entry={} offset={} size={} stored={} "
                     "compressed={}",
                     entry.manifest_root.generic_string(),
                     entry.relative_path,
                     entry.offset,
                     entry.size,
                     entry.stored_size,
                     entry.compressed);
}

std::string DescribeSyntheticBankEntryWindow(
    const std::shared_ptr<const RefiiSyntheticBankTemplate>& bank_template,
    uint32_t entry_index) {
  if (bank_template == nullptr || entry_index == 0) {
    return {};
  }

  std::string description;
  auto append_entry = [&](std::string_view label, uint32_t index) {
    BankManifestEntry entry;
    if (!ReadSyntheticBankEntryByIndex(bank_template, index, &entry)) {
      return;
    }
    description += fmt::format(" {}[#{} {}]",
                               label,
                               index,
                               DescribeSyntheticBankEntry(entry));
  };

  if (entry_index > 1) {
    append_entry("prev", entry_index - 1);
  }
  append_entry("cur", entry_index);
  append_entry("next", entry_index + 1);
  return description;
}

bool TryCreateSyntheticBank(PPCContext& ctx,
                            uint8_t* base,
                            uint32_t out_guest,
                            std::string_view bank_name,
                            std::string_view api) {
  if (!RefiiLooseAssetBackendEnabled() || base == nullptr || out_guest == 0 ||
      bank_name.empty()) {
    return false;
  }

  const std::shared_ptr<const RefiiSyntheticBankTemplate> bank_template =
      FindOrCreateSyntheticBankTemplate(ctx, base, bank_name, api);
  if (bank_template == nullptr) {
    return false;
  }

  const uint32_t bank_guest = CallLhMalloc(ctx, base, kRefiiSyntheticBankSize);
  if (bank_guest == 0) {
    REXLOG_WARN("Refii synthetic bank allocation failed api={} bank={}",
                api,
                bank_name);
    return false;
  }

  auto* bank_object =
      rex::memory::GuestPtr<uint8_t*>(base, bank_guest);
  std::memset(bank_object, 0, kRefiiSyntheticBankSize);
  StoreGuestU32(base, bank_guest, kRefiiSyntheticBankVTable);
  StoreGuestU32(base, bank_guest + 4, 0);

  const uint32_t entry_count =
      static_cast<uint32_t>(bank_template->entry_line_offsets.size());
  // Each bank object gets its own guest copy of the entry table. The native
  // cleanup body (0x82C6C198) frees the vector at +68 unconditionally, and one
  // manifest template backs many bank objects (level.vfsconfig opens the same
  // .bnk as Mode="disk" AND Mode="memory") -- sharing the template's guest
  // allocation across objects means the first cleanup frees it under the
  // others. With a per-object copy, native cleanup/reuse is simply correct and
  // needs no hook. The template's own allocation stays host-side as the parse
  // cache master and is never handed to a bank object.
  const uint32_t entry_bytes =
      bank_template->entries_end - bank_template->entries_begin;
  uint32_t entries_begin = 0;
  if (entry_bytes != 0) {
    entries_begin = CallLhMalloc(ctx, base, entry_bytes);
    if (entries_begin == 0) {
      REXLOG_WARN("Refii synthetic bank entry-table copy allocation failed "
                  "api={} bank={} bytes={}",
                  api, bank_name, entry_bytes);
      CallLhFree(ctx, base, bank_guest);
      return false;
    }
    std::memcpy(rex::memory::GuestPtr<uint8_t*>(base, entries_begin),
                rex::memory::GuestPtr<uint8_t*>(base,
                                                bank_template->entries_begin),
                entry_bytes);
  }
  StoreGuestU32(base, bank_guest + 68, entries_begin);
  StoreGuestU32(base, bank_guest + 72, entries_begin + entry_bytes);

  // Build the native file-name table at bank+96 (a std::string vector, 28-byte
  // rows, begin at +100). The file-system provider enumerates a bank through
  // its GetFiles vtable slot (sub_82C6CE00), which reads names from this table;
  // without it, provider-hash registration finds nothing and frontend loading
  // never completes. We replicate the constructor's per-entry name append with
  // the same native helpers instead of hand-laying the string bytes.
  constexpr uint32_t kBankNameVectorReserve = 0x82C6BC48;
  constexpr uint32_t kBankNameVectorAppend = 0x82C6C2D8;
  constexpr uint32_t kStringFromCStr = 0x8217B0A8;
  constexpr uint32_t kStringDtor = 0x822F22B8;
  // One reusable context for the whole table build: the guest helpers restore
  // non-volatile state on return, so only r3/r4/ctr need refreshing per call.
  // Copying the full PPCContext (GPR+FPR+VMX) per call is measurably hot when
  // large banks append tens of thousands of names.
  PPCContext name_call_ctx = ctx;
  auto call_guest = [&](uint32_t guest_addr, uint32_t r3, uint32_t r4) {
    PPCFunc* fn = FindGuestFunction(guest_addr);
    if (fn == nullptr) {
      return false;
    }
    name_call_ctx.r3.u32 = r3;
    name_call_ctx.r4.u32 = r4;
    name_call_ctx.ctr.u32 = guest_addr;
    fn(name_call_ctx, base);
    return true;
  };
  const uint32_t name_vector_guest = bank_guest + 96;
  bool name_table_built = false;
  if (entry_count > 0 && entry_count == bank_template->entry_names.size() &&
      FindGuestFunction(kBankNameVectorReserve) != nullptr &&
      FindGuestFunction(kBankNameVectorAppend) != nullptr &&
      FindGuestFunction(kStringFromCStr) != nullptr) {
    call_guest(kBankNameVectorReserve, name_vector_guest, entry_count);
    // Reserve leaves begin@+4 allocated with end@+8 == begin. Rows are the
    // native 28-byte narrow-char std::string: data inline at +4 when cap < 16
    // (cap always 15 inline), else heap pointer at +4; len@+20, cap@+24 with
    // the native cap rounding (len | 15) and rex_lh_malloc(cap + 1) backing so
    // the native string dtor frees the block correctly. Building rows directly
    // avoids three guest calls (codepage-widening ctor, append copy, dtor) and
    // two extra heap round-trips per entry — large banks (lipsync: 45k names)
    // otherwise take minutes to hours on the contended guest heap.
    const uint32_t rows_begin = LoadGuestU32(base, name_vector_guest + 4);
    if (rows_begin != 0) {
      uint32_t row = rows_begin;
      bool rows_ok = true;
      for (const std::string& entry_name : bank_template->entry_names) {
        const uint32_t name_len =
            static_cast<uint32_t>(std::min<size_t>(entry_name.size(), 1022));
        uint32_t data_guest;
        if (name_len < 16) {
          data_guest = row + 4;
          StoreGuestU32(base, row + 24, 15);
        } else {
          const uint32_t name_cap = name_len | 15u;
          data_guest = CallLhMalloc(ctx, base, name_cap + 1);
          if (data_guest == 0) {
            rows_ok = false;
            break;
          }
          StoreGuestU32(base, row + 4, data_guest);
          StoreGuestU32(base, row + 24, name_cap);
        }
        for (uint32_t index = 0; index < name_len; ++index) {
          StoreGuestU8(base, data_guest + index,
                       static_cast<uint8_t>(entry_name[index]));
        }
        StoreGuestU8(base, data_guest + name_len, 0);
        StoreGuestU32(base, row + 20, name_len);
        row += 28;
      }
      if (rows_ok) {
        StoreGuestU32(base, name_vector_guest + 8, row);
        name_table_built = true;
      }
    }
    if (!name_table_built) {
      // Fallback: replicate the ctor's per-entry append via the native
      // helpers (slow path, kept for safety if reserve produced no storage).
      const uint32_t name_scratch = CallLhMalloc(ctx, base, 1024);
      const uint32_t temp_string = CallLhMalloc(ctx, base, 64);
      const bool string_dtor_available =
          FindGuestFunction(kStringDtor) != nullptr;
      if (name_scratch != 0 && temp_string != 0) {
        for (const std::string& entry_name : bank_template->entry_names) {
          const size_t copy_len = std::min<size_t>(entry_name.size(), 1022);
          for (size_t index = 0; index < copy_len; ++index) {
            StoreGuestU8(base, name_scratch + static_cast<uint32_t>(index),
                         static_cast<uint8_t>(entry_name[index]));
          }
          StoreGuestU8(base, name_scratch + static_cast<uint32_t>(copy_len), 0);
          call_guest(kStringFromCStr, temp_string, name_scratch);
          call_guest(kBankNameVectorAppend, name_vector_guest, temp_string);
          if (string_dtor_available) {
            call_guest(kStringDtor, temp_string, 0);
          }
        }
        name_table_built = true;
      }
      CallLhFree(ctx, base, temp_string);
      CallLhFree(ctx, base, name_scratch);
    }
  }
  if (VfsTracingEnabled()) {
    const uint32_t nv_begin = LoadGuestU32(base, bank_guest + 100);
    const uint32_t nv_end = LoadGuestU32(base, bank_guest + 104);
    const uint32_t nv_count =
        nv_begin != 0 && nv_end >= nv_begin ? (nv_end - nv_begin) / 28 : 0;
    // Decode the first stored std::string row: length at +20, capacity at +24,
    // data inline at +4 when capacity < 16 else heap pointer at +4.
    std::string decoded_first_name;
    if (nv_begin != 0) {
      const uint32_t row_len = LoadGuestU32(base, nv_begin + 20);
      const uint32_t row_cap = LoadGuestU32(base, nv_begin + 24);
      const uint32_t data_guest =
          row_cap < 16u ? nv_begin + 4 : LoadGuestU32(base, nv_begin + 4);
      for (uint32_t index = 0; index < row_len && index < 128 &&
                               data_guest != 0;
           ++index) {
        decoded_first_name.push_back(
            static_cast<char>(LoadGuestU8(base, data_guest + index)));
      }
    }
    REXLOG_INFO("Refii synthetic bank name table built={} name_vec=0x{:08X} "
                "begin=0x{:08X} count={} expected_first={} decoded_first={}",
                name_table_built,
                name_vector_guest,
                nv_begin,
                nv_count,
                bank_template->entry_names.empty()
                    ? std::string()
                    : bank_template->entry_names.front(),
                decoded_first_name);
  }

  auto record = std::make_shared<RefiiSyntheticBankRecord>();
  record->bank_name = std::string(bank_name);
  record->bank_template = bank_template;

  StoreSyntheticBankRecord(bank_guest, record);
  StoreGuestU32(base, out_guest, bank_guest);
  AddRefGuestObject(ctx, base, bank_guest);
  ctx.r3.u32 = out_guest;

  const uint64_t opened =
      LooseStats().synthetic_banks_opened.fetch_add(
          1, std::memory_order_relaxed) +
      1;
  LogLooseStatsMilestone("synthetic_banks_opened", opened);
  const std::string normalized_bank = NormalizeLooseAssetKey(bank_name);
  if (normalized_bank.find("model_headers") != std::string::npos ||
      normalized_bank.find("_models.bnk") != std::string::npos) {
    // Model payloads are exposed through a native-shaped bank object rather
    // than one host stream per .mdl. Count the resource-class open here so the
    // runtime contract distinguishes genuine model-bank coverage from a boot
    // that only opened Lua/database/texture streams. Entry-table and payload
    // integrity remain covered by the model-bank contract.
    const uint64_t model_resources =
        LooseStats().refii_mesh_streams_opened.fetch_add(
            1, std::memory_order_relaxed) +
        1;
    LogLooseStatsMilestone("mesh_resources_opened", model_resources);
  }
  if (VfsTracingEnabled()) {
    REXLOG_INFO("Refii synthetic bank opened api={} bank=0x{:08X} name={} "
                "root={} entries={} entry_table=0x{:08X}-0x{:08X} "
                "opened={} active={}{}",
                api,
                bank_guest,
                bank_name,
                bank_template->manifest_root.generic_string(),
                entry_count,
                bank_template->entries_begin,
                bank_template->entries_end,
                opened,
                SyntheticBankRecordCount(),
                DescribeSyntheticBankEntryWindow(bank_template, 1));
  }

  return true;
}

void EnsureRefiiBootBankTemplates(PPCContext& ctx,
                                  uint8_t* base,
                                  std::string_view api) {
  (void)ctx;
  (void)base;
  if (!RefiiLooseAssetBackendEnabled() || base == nullptr) {
    return;
  }

  static std::mutex mutex;
  static std::string ensured_root;

  const std::string root_key = GetLooseAssetRoot().lexically_normal().string();
  std::lock_guard<std::mutex> lock(mutex);
  if (ensured_root == root_key) {
    return;
  }

  static constexpr std::string_view kBootBanks[] = {
      "levels.bnk",
      "streaming.bnk",
      "globals/1024mip0_textures.bnk",
      "globals/globals_model_headers.bnk",
      "globals/globals_streaming.bnk",
      "art/gui/gui_streaming.bnk",
      "skeletalmorphs.bnk",
      "globals/globals_models.bnk",
      "globals/globals_texture_headers.bnk",
      "globals/globals_textures.bnk",
      "art/gui/gui_models.bnk",
      "art/gui/gui_texture_headers.bnk",
      "art/gui/gui_textures.bnk",
      "art/localised/en-uk/gui/gui_streaming.bnk",
      "art/localised/en-uk/gui/gui_models.bnk",
      "art/localised/en-uk/gui/gui_texture_headers.bnk",
      "art/localised/en-uk/gui/gui_textures.bnk",
      "audio/gui.bnk",
  };

  size_t indexed = 0;
  for (const std::string_view bank : kBootBanks) {
    if (LookupRefiiVfsTypedBankManifest(bank, nullptr, nullptr)) {
      ++indexed;
    }
  }

  if (VfsTracingEnabled()) {
    REXLOG_INFO("Refii boot VFS contract indexed root={} banks={}/{} "
                "templates=lazy api={}",
                root_key,
                indexed,
                sizeof(kBootBanks) / sizeof(kBootBanks[0]),
                api);
  }
  ensured_root = root_key;
}

bool ReadBankIndexEntryOffsetSize(uint8_t* base,
                                  uint32_t bank_guest,
                                  uint32_t entry_index,
                                  uint32_t* out_offset,
                                  uint32_t* out_size) {
  if (base == nullptr || bank_guest == 0 || entry_index == 0) {
    return false;
  }

  const uint32_t entries_begin = LoadGuestU32(base, bank_guest + 68);
  const uint32_t entries_end = LoadGuestU32(base, bank_guest + 72);
  if (entries_begin == 0 || entries_end <= entries_begin) {
    return false;
  }

  constexpr uint32_t kBankEntrySize = 12;
  const uint32_t entry_count = (entries_end - entries_begin) / kBankEntrySize;
  const uint32_t entry_zero_index = entry_index - 1;
  if (entry_zero_index >= entry_count) {
    return false;
  }

  const uint32_t entry_guest = entries_begin + entry_zero_index * kBankEntrySize;
  if (out_offset != nullptr) {
    *out_offset = LoadGuestU32(base, entry_guest + 4);
  }
  if (out_size != nullptr) {
    *out_size = LoadGuestU32(base, entry_guest + 8);
  }
  return true;
}

std::string LookupBankIndexEntryName(uint8_t* base,
                                     uint32_t bank_guest,
                                     uint32_t entry_index) {
  if (base == nullptr || bank_guest == 0 || entry_index == 0) {
    return {};
  }

  if (const auto record = FindSyntheticBankRecord(bank_guest)) {
    BankManifestEntry entry;
    if (ReadSyntheticBankEntryByIndex(
            record->bank_template, entry_index, &entry)) {
      return entry.relative_path;
    }
    return {};
  }

  const std::string bank_name = ReadBankNameForLookup(base, bank_guest);
  if (bank_name.empty()) {
    return {};
  }

  uint32_t offset = 0;
  uint32_t size = 0;
  if (!ReadBankIndexEntryOffsetSize(
          base, bank_guest, entry_index, &offset, &size)) {
    return {};
  }
  return LookupBankManifestEntry(bank_name, offset, size);
}

bool SyntheticBankEntryMatchesFilter(const BankManifestEntry& entry,
                                     std::string_view entry_filter) {
  const std::string normalized_filter = NormalizeLooseAssetKey(entry_filter);
  if (normalized_filter.empty()) {
    return true;
  }

  const std::string normalized_entry =
      NormalizeLooseAssetKey(entry.relative_path);
  if (normalized_entry == normalized_filter) {
    return true;
  }

  const size_t separator = normalized_entry.find_last_of('/');
  const std::string_view basename =
      separator == std::string::npos
          ? std::string_view(normalized_entry)
          : std::string_view(normalized_entry).substr(separator + 1);
  return basename == normalized_filter;
}

std::vector<std::string> MakeBankLooseCandidates(std::string_view bank_name,
                                                 std::string_view entry_name) {
  std::vector<std::string> candidates;
  const std::string normalized_entry = NormalizeLooseAssetKey(entry_name);
  AddUnique(candidates, normalized_entry);

  const std::string bank_root = BankNameToLooseRoot(bank_name);
  if (!bank_root.empty()) {
    AddUnique(candidates, bank_root + "/" + normalized_entry);
  }

  return candidates;
}

std::string MakeBankLooseResolutionCacheKey(std::string_view bank_name,
                                            std::string_view entry_name) {
  std::string key;
  for (const std::filesystem::path& root : GetLooseAssetSearchRoots()) {
    if (!key.empty()) {
      key += ';';
    }
    key += root.lexically_normal().generic_string();
  }
  key += '\n';
  key += NormalizeLooseAssetKey(bank_name);
  key += '\n';
  key += NormalizeLooseAssetKey(entry_name);
  return key;
}

bool ResolveVfsDatabaseAliasLooseAsset(std::string_view asset_path,
                                       LooseAssetResolution* out_resolution,
                                       std::string* out_candidate);

bool ResolveManifestEntryLooseAsset(const std::filesystem::path& manifest_root,
                                    std::string_view entry_name,
                                    LooseAssetResolution* out_resolution,
                                    std::string* out_candidate,
                                    std::string_view host_entry_name = {}) {
  if (entry_name.empty()) {
    return false;
  }

  const bool has_host_entry = !host_entry_name.empty();
  std::filesystem::path relative_path;
  if (has_host_entry) {
    relative_path = ManifestPathToGenericPath(std::string(host_entry_name));
  } else {
    relative_path = manifest_root / ManifestPathToGenericPath(std::string(entry_name));
  }
  std::filesystem::path provider_relative;
  if (has_host_entry) {
    provider_relative = ManifestPathToGenericPath(std::string(host_entry_name));
  } else {
    provider_relative =
        manifest_root / ManifestPathToGenericPath(std::string(entry_name));
  }
  std::string provider_relative_path = provider_relative.generic_string();
  std::filesystem::path host_path;
  LooseAssetResolution manifest_resolution;
  if (ResolveLooseAssetOverride(relative_path.generic_string(),
                                &manifest_resolution)) {
    host_path = manifest_resolution.host_path;
    provider_relative_path = manifest_resolution.provider_relative_path;
  } else {
    host_path = GetLooseAssetRoot() / relative_path;
    std::error_code error;
    if (!std::filesystem::is_regular_file(host_path, error)) {
      return false;
    }
  }

  if (out_resolution != nullptr) {
    out_resolution->host_path = host_path.lexically_normal();
    out_resolution->normalized_key = NormalizeLooseAssetKey(entry_name);
    out_resolution->provider_relative_path = provider_relative_path;
  }
  if (out_candidate != nullptr) {
    *out_candidate = provider_relative_path;
  }
  return true;
}

bool ResolveBankLooseAssetOverride(std::string_view bank_name,
                                   std::string_view entry_name,
                                   LooseAssetResolution* out_resolution,
                                   std::string* out_candidate) {
  if (entry_name.empty()) {
    return false;
  }

  static std::mutex mutex;
  static std::unordered_map<std::string, CachedBankLooseAssetResolution> cache;

  const std::string key = MakeBankLooseResolutionCacheKey(bank_name, entry_name);
  // Logo-pool watch (title missing-elements campaign; remove when done):
  // every resolution attempt against the 1024mip0 pool or for the known
  // header-only frontend textures, logged unconditionally (capped).
  if (bank_name.find("1024mip0") != std::string_view::npos ||
      entry_name.find("f2logo") != std::string_view::npos ||
      entry_name.find("screen_title") != std::string_view::npos ||
      entry_name.find("loading_") != std::string_view::npos) {
    static std::atomic<uint32_t> logged_mip0_lookups{0};
    const uint32_t n = ++logged_mip0_lookups;
    if (n <= 64u) {
      REXLOG_INFO("Refii mip0-pool lookup #{} bank='{}' entry='{}'", n,
                  std::string(bank_name), std::string(entry_name));
    }
  }
  {
    std::lock_guard<std::mutex> lock(mutex);
    const auto cached = cache.find(key);
    if (cached != cache.end()) {
      if (cached->second.found) {
        if (out_resolution != nullptr) {
          *out_resolution = cached->second.resolution;
        }
        if (out_candidate != nullptr) {
          *out_candidate = cached->second.candidate;
        }
      }
      return cached->second.found;
    }
  }

  CachedBankLooseAssetResolution resolved;
  const std::string bank_root = BankNameToLooseRoot(bank_name);
  if (!bank_root.empty()) {
    for (const std::filesystem::path& manifest_root :
         BuildBankManifestRootCandidates(bank_root)) {
      BankManifestEntry manifest_entry;
      if (!LookupCachedBankManifestEntryByName(
              manifest_root, entry_name, &manifest_entry)) {
        continue;
      }

      resolved.found = ResolveManifestEntryLooseAsset(manifest_root,
                                                      manifest_entry.relative_path,
                                                      &resolved.resolution,
                                                      &resolved.candidate,
                                                      manifest_entry.host_relative_path);
      break;
    }
  }

  for (const std::string& candidate :
       resolved.found ? std::vector<std::string>{}
                      : MakeBankLooseCandidates(bank_name, entry_name)) {
    LooseAssetResolution resolution;
    if (!ResolveLooseAssetOverride(candidate, &resolution)) {
      continue;
    }

    resolved.found = true;
    resolved.resolution = std::move(resolution);
    resolved.candidate = candidate;
    break;
  }

  for (const std::string& candidate :
       resolved.found ? std::vector<std::string>{}
                      : MakeBankLooseCandidates(bank_name, entry_name)) {
    LooseAssetResolution resolution;
    std::string database_candidate;
    if (!ResolveVfsDatabaseAliasLooseAsset(
            candidate, &resolution, &database_candidate)) {
      continue;
    }

    resolved.found = true;
    resolved.resolution = std::move(resolution);
    resolved.candidate =
        database_candidate.empty() ? candidate : database_candidate;
    break;
  }

  {
    std::lock_guard<std::mutex> lock(mutex);
    const auto result = cache.try_emplace(key, std::move(resolved));
    const auto iterator = result.first;
    const CachedBankLooseAssetResolution& value = iterator->second;
    if (value.found) {
      if (out_resolution != nullptr) {
        *out_resolution = value.resolution;
      }
      if (out_candidate != nullptr) {
        *out_candidate = value.candidate;
      }
    }
    return value.found;
  }
}


}  // namespace detail
}  // namespace refii::vfs
