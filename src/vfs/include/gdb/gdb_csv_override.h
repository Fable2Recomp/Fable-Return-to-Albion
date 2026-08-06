#pragma once

#include <filesystem>
#include <string_view>

namespace refii::vfs {
inline namespace detail {

// Compiles a loose ".gdb.csv" override into a cached binary .gdb the guest
// can load. `relative_gdb_path` is the normalized relative path of the .gdb
// the CSV replaces (e.g. "globals/globals.gdb"); the compiled file is cached
// under <loose asset root>/.cache/gdb_compiled/ and rebuilt whenever the CSV
// is newer. Returns false (and logs once per failure state) if the CSV does
// not compile, so callers can fall back to the binary database.
bool TryCompileGdbCsvOverride(const std::filesystem::path& csv_host_path,
                              std::string_view relative_gdb_path,
                              std::filesystem::path* out_compiled_path);

}  // namespace detail
}  // namespace refii::vfs
