#pragma once

#include "loose_asset_hooks.h"

#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <ios>
#include <memory>
#include <string>
#include <unordered_map>
#include <vector>

namespace refii::vfs {

struct BankManifestEntry {
  uint32_t offset = 0;
  uint32_t size = 0;
  uint32_t stored_size = 0;
  bool compressed = false;
  std::string relative_path;
  std::string host_relative_path;
  std::filesystem::path manifest_root;
};

struct BankManifestIndex {
  std::vector<BankManifestEntry> entries;
  std::unordered_map<uint64_t, size_t> by_offset_size;
  std::unordered_map<std::string, size_t> by_normalized_path;
};

struct RefiiSyntheticBankTemplate {
  std::filesystem::path manifest_path;
  std::filesystem::path manifest_root;
  std::vector<std::streamoff> entry_line_offsets;
  // Entry names as the game spells them (backslash separators), parallel to
  // entry_line_offsets. Used to build the native bank name table so the file
  // system provider can enumerate the bank's contents.
  std::vector<std::string> entry_names;
  uint32_t entries_begin = 0;
  uint32_t entries_end = 0;
};

struct RefiiSyntheticBankRecord {
  std::string bank_name;
  std::shared_ptr<const RefiiSyntheticBankTemplate> bank_template;
};

struct RefiiSyntheticBankIteratorRecord {
  uint32_t bank_guest = 0;
  std::shared_ptr<RefiiSyntheticBankRecord> bank_record;
  uint32_t next_entry_index = 1;
};
}  // namespace refii::vfs
