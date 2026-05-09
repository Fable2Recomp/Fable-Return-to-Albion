#pragma once

#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <string>
#include <unordered_map>
#include <vector>

namespace refii::vfs {

struct SyntheticVfsEntry {
  std::filesystem::path manifest_root;
  std::string relative_path;
  std::string host_relative_path;
};

struct SyntheticVfsEntryIndex {
  std::string root_key;
  size_t template_count = 0;
  size_t runtime_bank_record_count = 0;
  std::unordered_map<std::string, SyntheticVfsEntry> by_normalized_path;
  size_t manifest_entries = 0;
  size_t manifest_tables = 0;
};

struct RefiiResourceHashIndex {
  std::string root_key;
  size_t synthetic_template_count = 0;
  bool database_loaded = false;
  std::unordered_map<uint32_t, std::vector<std::string>> candidates_by_hash;
  std::unordered_map<std::string, std::vector<std::string>> candidates_by_alias;
  size_t indexed_paths = 0;
  size_t hash_aliases = 0;
  size_t filehash_aliases = 0;
  size_t database_aliases = 0;
};

}  // namespace refii::vfs
