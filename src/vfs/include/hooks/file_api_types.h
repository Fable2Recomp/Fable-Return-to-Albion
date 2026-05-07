#pragma once

#include <cstddef>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <mutex>
#include <string>
#include <unordered_map>
#include <unordered_set>
#include <vector>

namespace refii::vfs {

struct RefiiPrivateFileHandleRecord {
  std::filesystem::path path;
  std::string virtual_path;
  std::ifstream file;
  uint64_t position = 0;
  uint64_t size = 0;
  uint64_t max_read_end = 0;
  bool fully_read_reported = false;
  std::mutex mutex;
};

struct RefiiPrivateFindEntry {
  std::string name;
  bool directory = false;
  uint64_t size = 0;
};

struct RefiiFindDirectoryIndex {
  std::string root_key;
  bool loaded = false;
  std::unordered_map<std::string, std::vector<RefiiPrivateFindEntry>>
      entries_by_parent;
  size_t directory_count = 0;
  size_t file_count = 0;
};

struct RefiiFindDirectoryParentCache {
  std::string root_key;
  std::unordered_set<std::string> loaded_parents;
  std::unordered_map<std::string, std::vector<RefiiPrivateFindEntry>>
      entries_by_parent;
};

struct RefiiPrivateFindHandleRecord {
  std::string pattern;
  std::vector<RefiiPrivateFindEntry> entries;
  size_t next_index = 0;
  bool exhausted = false;
  std::mutex mutex;
};

}  // namespace refii::vfs
