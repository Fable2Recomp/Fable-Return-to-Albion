// The Godot importer supplies ordinary app-owned files to the embedded runtime.
// Direct content:// access requires its Activity's ContentResolver and is an
// explicit unsupported path until that separate importer bridge is implemented.
#include <rex/filesystem.h>
#include <rex/logging.h>
#include <cerrno>

namespace rex::filesystem {
/// Rejects direct SAF mapping with a diagnostic rather than a missing linker symbol.
int OpenAndroidContentFileDescriptor(const std::string_view uri, const char* mode) {
  (void)mode;
  REXLOG_ERROR("Direct Android content URI mapping is unsupported; import to an app-owned file first: {}", uri);
  errno = ENOTSUP;
  return -1;
}
}  // namespace rex::filesystem
