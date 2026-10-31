/**
 ******************************************************************************
 * Xenia : Xbox 360 Emulator Research Project                                 *
 ******************************************************************************
 * Copyright 2013 Ben Vanik. All rights reserved.                             *
 * Released under the BSD license - see LICENSE in the root for more details. *
 ******************************************************************************
 *
 * @modified    Tom Clay, 2026 - Adapted for ReXGlue runtime
 */

#include <rex/filesystem/devices/host_path_entry.h>
#include <rex/filesystem/devices/host_path_file.h>

#if defined(__ANDROID__)
#include <cstdlib>
#include <cstring>

#include <rex/logging.h>
#include <rex/string.h>
#endif

namespace rex::filesystem {

#if defined(__ANDROID__)
namespace {
// Refii experiment diagnostic: a world name alone cannot prove the copied save
// was deserialized. Trace only the requested save-file metadata after explicit
// opt-in, without reading/logging payload bytes or unrelated filesystem paths.
bool AndroidSaveIoTraceEnabled() {
  static const bool enabled = [] {
    const char* value = std::getenv("REFII_ANDROID_SAVE_IO_TRACE");
    return value && std::strcmp(value, "1") == 0;
  }();
  return enabled;
}

bool IsAndroidSaveTraceFile(const HostPathEntry& entry) {
  std::string name = entry.name();
  for (char& character : name) {
    if (character >= 'A' && character <= 'Z') {
      character = static_cast<char>(character + ('a' - 'A'));
    }
  }
  return name == "mainsave.bin" || name == "herosave.bin" ||
         name == "chaptersave.bin" ||
         (name.starts_with("hero") && name.ends_with(".header"));
}

void TraceAndroidSaveIo(const char* operation, const HostPathEntry& entry,
                        size_t offset, size_t requested, size_t actual,
                        X_STATUS status) {
  if (!AndroidSaveIoTraceEnabled() || !IsAndroidSaveTraceFile(entry)) {
    return;
  }
  REXLOG_INFO("[android-save-io] {} file={} size={} offset={} requested={} actual={} status={:08X}",
              operation, rex::path_to_utf8(entry.host_path()), entry.size(),
              offset, requested, actual, status);
}
}  // namespace
#endif

HostPathFile::HostPathFile(uint32_t file_access, HostPathEntry* entry,
                           std::unique_ptr<rex::filesystem::FileHandle> file_handle)
    : File(file_access, entry), file_handle_(std::move(file_handle)) {
#if defined(__ANDROID__)
  TraceAndroidSaveIo("open", *entry, 0, 0, 0, X_STATUS_SUCCESS);
#endif
}

HostPathFile::~HostPathFile() = default;

void HostPathFile::Destroy() {
  delete this;
}

X_STATUS HostPathFile::ReadSync(std::span<uint8_t> buffer, size_t byte_offset,
                                size_t* out_bytes_read) {
  if (!(file_access_ & (FileAccess::kGenericRead | FileAccess::kFileReadData))) {
#if defined(__ANDROID__)
    TraceAndroidSaveIo("read", *static_cast<HostPathEntry*>(entry_), byte_offset,
                       buffer.size(), 0, X_STATUS_ACCESS_DENIED);
#endif
    return X_STATUS_ACCESS_DENIED;
  }

  const bool read_success =
      file_handle_->Read(byte_offset, buffer.data(), buffer.size(), out_bytes_read);
  const X_STATUS status = read_success ? X_STATUS_SUCCESS : X_STATUS_END_OF_FILE;
#if defined(__ANDROID__)
  TraceAndroidSaveIo("read", *static_cast<HostPathEntry*>(entry_), byte_offset,
                     buffer.size(), out_bytes_read ? *out_bytes_read : 0, status);
#endif
  return status;
}

X_STATUS HostPathFile::WriteSync(std::span<const uint8_t> buffer, size_t byte_offset,
                                 size_t* out_bytes_written) {
  if (!(file_access_ &
        (FileAccess::kGenericWrite | FileAccess::kFileWriteData | FileAccess::kFileAppendData))) {
    return X_STATUS_ACCESS_DENIED;
  }

  if (file_handle_->Write(byte_offset, buffer.data(), buffer.size(), out_bytes_written)) {
    return X_STATUS_SUCCESS;
  } else {
    return X_STATUS_END_OF_FILE;
  }
}

X_STATUS HostPathFile::SetLength(size_t length) {
  if (!(file_access_ & (FileAccess::kGenericWrite | FileAccess::kFileWriteData))) {
    return X_STATUS_ACCESS_DENIED;
  }

  if (file_handle_->SetLength(length)) {
    return X_STATUS_SUCCESS;
  } else {
    return X_STATUS_END_OF_FILE;
  }
}

}  // namespace rex::filesystem
