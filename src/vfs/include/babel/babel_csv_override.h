#pragma once

#include <filesystem>
#include <string_view>

namespace refii::vfs {
inline namespace detail {

// Compiles a loose "<name>.babel.csv" override into a cached binary .babel the
// guest can load. `relative_babel_path` is the normalized relative path of the
// .babel the CSV replaces (e.g. "language/en-uk/text/book.babel"); the compiled
// file is cached under <loose asset root>/.cache/babel_compiled/ and rebuilt
// whenever the CSV is newer. Returns false (logging once per failure state) if
// the CSV does not compile, so callers can fall back to the binary database.
bool TryCompileBabelCsvOverride(const std::filesystem::path& csv_host_path,
                                std::string_view relative_babel_path,
                                std::filesystem::path* out_compiled_path);

}  // namespace detail
}  // namespace refii::vfs
